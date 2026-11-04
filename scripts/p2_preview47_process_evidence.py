#!/usr/bin/env python3
"""Fail-preserving P47 process diagnostics; never kills observed browser processes.

CIM membership and native process liveness are separate observations. The
caller retains the original 12-second presence gate until evidence permits a
reviewed contract change. Native inspection opens and closes a temporary
handle per observation; retaining handles here would change object lifetime.
"""
from datetime import datetime, timedelta, timezone
import ctypes
from ctypes import wintypes
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import time

import p2_preview45_sink_close_process_ancestry as p45

SCHEMA = "migrator-preview47-process-diagnostic/v1"
PASSIVE_WINDOW_SECONDS = 12


def atomic_json(path, value):
    path = Path(path)
    temporary = path.with_name(path.name + ".tmp")
    with temporary.open("w", encoding="utf-8", newline="\n") as stream:
        stream.write(json.dumps(value, sort_keys=True, indent=2) + "\n")
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temporary, path)


def write_manifest(evidence):
    evidence = Path(evidence)
    files = {p.name: hashlib.sha256(p.read_bytes()).hexdigest()
             for p in sorted(evidence.iterdir())
             if p.is_file() and p.name != "SHA256SUMS.json"
             and not p.name.endswith(".tmp")}
    atomic_json(evidence / "SHA256SUMS.json", files)


def utc_now():
    return datetime.now(timezone.utc).isoformat()


# Windows termination signals the process object; an open handle can outlive it.
# https://learn.microsoft.com/en-us/windows/win32/procthread/terminating-a-process
# https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-waitforsingleobject
# https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocesstimes
# CIM_DATETIME creation timestamps have microsecond resolution:
# https://learn.microsoft.com/en-us/windows/win32/wmisdk/cim-datetime


def native_creation_utc(filetime):
    # Integer arithmetic avoids rounding a 64-bit FILETIME through float seconds.
    # CIM creation dates carry microseconds; keep the full native 100ns identity
    # separately after this resolution-bounded join for later exact comparisons.
    return datetime(1601, 1, 1, tzinfo=timezone.utc) + timedelta(microseconds=int(filetime) // 10)


def cim_matches_native(creation_utc, filetime):
    if not creation_utc or not filetime:
        return False
    try:
        cim = datetime.fromisoformat(creation_utc.replace("Z", "+00:00"))
        return cim.tzinfo is not None and cim == native_creation_utc(filetime)
    except (TypeError, ValueError, OverflowError):
        return False


class NativeWindowsProbe:
    """Read identity and zero-time wait state from the same process handle."""
    def __init__(self):
        if os.name != "nt":
            raise RuntimeError("P47_NATIVE_WINDOWS_PROBE_REQUIRES_WINDOWS")
        self.api = ctypes.WinDLL("kernel32", use_last_error=True)
        self.api.OpenProcess.argtypes = (wintypes.DWORD, wintypes.BOOL, wintypes.DWORD)
        self.api.OpenProcess.restype = wintypes.HANDLE
        self.api.GetProcessTimes.argtypes = (wintypes.HANDLE,) + (ctypes.POINTER(wintypes.FILETIME),) * 4
        self.api.GetProcessTimes.restype = wintypes.BOOL
        self.api.WaitForSingleObject.argtypes = (wintypes.HANDLE, wintypes.DWORD)
        self.api.WaitForSingleObject.restype = wintypes.DWORD
        self.api.GetExitCodeProcess.argtypes = (wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD))
        self.api.GetExitCodeProcess.restype = wintypes.BOOL
        self.api.CloseHandle.argtypes = (wintypes.HANDLE,)
        self.api.CloseHandle.restype = wintypes.BOOL

    def inspect(self, pid, expected_start=None):
        result = {"pid": pid, "expectedCreationFileTime": expected_start,
                  "state": "unknown", "method": "GetProcessTimes+WaitForSingleObject(0)"}
        # SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, no terminate rights.
        handle = self.api.OpenProcess(0x00100000 | 0x1000, False, pid)
        if not handle:
            error = ctypes.get_last_error()
            result.update(state="absent" if error == 87 else "unknown",
                          openError=error)
            return result
        try:
            return self.inspect_handle(handle, pid, expected_start)
        finally:
            self.api.CloseHandle(handle)

    def inspect_handle(self, handle, pid, expected_start=None):
        # Caller owns this handle. Production inspect() always closes its
        # temporary handle; only the synthetic exit-object control retains one.
        result = {"pid": pid, "expectedCreationFileTime": expected_start,
                  "state": "unknown", "method": "GetProcessTimes+WaitForSingleObject(0)"}
        created, exited, kernel, user = (wintypes.FILETIME() for _ in range(4))
        if not self.api.GetProcessTimes(handle, ctypes.byref(created),
                ctypes.byref(exited), ctypes.byref(kernel), ctypes.byref(user)):
            result["identityError"] = ctypes.get_last_error()
            return result
        identity = str((created.dwHighDateTime << 32) | created.dwLowDateTime)
        result["creationFileTime"] = identity
        if expected_start is not None and identity != expected_start:
            result["state"] = "identity_mismatch"
            return result
        wait = self.api.WaitForSingleObject(handle, 0)
        result["waitResult"] = wait
        if wait == 0:  # WAIT_OBJECT_0, authoritative termination signal.
            result["state"] = "exited"
            code = wintypes.DWORD()
            if self.api.GetExitCodeProcess(handle, ctypes.byref(code)):
                result["exitCode"] = code.value
        elif wait == 258:  # WAIT_TIMEOUT, process is not signaled.
            result["state"] = "live"
        else:
            result["waitError"] = ctypes.get_last_error()
        return result


def snapshot(chrome):
    """Preserve P45's original identity and add unrounded CIM creation time."""
    if os.name != "nt":
        return p45.snapshot(chrome), {}
    ps = r"""
    $ErrorActionPreference='Stop'
    $all=Get-CimInstance Win32_Process
    foreach($p in $all) {
      $kind='other'
      if($p.ExecutablePath -and $p.ExecutablePath -ieq $env:MIGRATOR_CHROME_BIN) {
        $kind='chrome'
      } elseif($p.Name -ieq 'node.exe' -and $p.CommandLine -and
          $p.CommandLine -match 'playwright.*cli\.js') {
        $kind='driver'
      }
      $created=$p.CreationDate
      $precise=''
      if($null -ne $created) {
        $precise=$created.ToUniversalTime().ToString('o',[System.Globalization.CultureInfo]::InvariantCulture)
      }
      Write-Output "$($p.ProcessId)|$($p.ParentProcessId)|$created|$kind|$precise"
    }
    """
    result = subprocess.run(["powershell", "-NoProfile", "-NonInteractive",
                             "-Command", ps], capture_output=True, text=True, timeout=35)
    if result.returncode:
        raise RuntimeError("P47_WINDOWS_CIM_PROCESS_METADATA_UNAVAILABLE")
    data, precise = {}, {}
    for line in result.stdout.splitlines():
        fields = line.strip().split("|")
        if len(fields) != 5 or not fields[0].isdigit() or not fields[1].isdigit():
            continue
        pid = int(fields[0])
        data[pid] = (int(fields[1]), fields[2], fields[3])
        precise[pid] = fields[4]
    if not data:
        raise RuntimeError("P47_WINDOWS_CIM_RETURNED_EMPTY_PROCESS_TABLE")
    return data, precise


def classify_presence(present, native):
    """Do not equate a retained process object, PID reuse, or unknown with live."""
    return {state: [dict(pid=pid, startIdentity=start, native=native.get((pid, start)))
                    for pid, start in sorted(present)
                    if native.get((pid, start), {}).get("state", "unknown") == state]
            for state in ("live", "exited", "absent", "identity_mismatch", "unknown", "not_applicable")}


class StageEvidence:
    def __init__(self, evidence, stage, expected, disconnect, native=None):
        self.directory = Path(evidence)
        self.directory.mkdir(parents=True, exist_ok=True)
        self.stage = stage
        self.started = time.monotonic()
        self.native = native if native is not None else (NativeWindowsProbe() if os.name == "nt" else None)
        self.native_starts = {}
        self.ordinal = 0
        self.report = {"schema": SCHEMA, "run": stage, "expected": expected,
                       "disconnectRequested": disconnect, "status": "running",
                       "passiveWindowSeconds": PASSIVE_WINDOW_SECONDS,
                       "originalPresenceGate": "pending", "nativeLivenessIsDiagnosticOnly": True,
                       "hostOwnedBrowserCleanup": "not_attempted",
                       "observedOwnedAncestry": [], "actual": "not_observed",
                       "realBrowserCloseException": "not_observed",
                       "realDisposeException": "not_observed"}
        self.persist()

    def persist(self):
        atomic_json(self.directory / ("process-" + self.stage + ".json"), self.report)

    def event(self, phase, **fields):
        self.ordinal += 1
        event = dict(ordinal=self.ordinal, phase=phase, utc=utc_now(),
                     elapsedSec=round(time.monotonic() - self.started, 3), **fields)
        with (self.directory / ("process-" + self.stage + ".jsonl")).open("a", encoding="utf-8", newline="\n") as stream:
            stream.write(json.dumps(event, sort_keys=True) + "\n")
            stream.flush()
            os.fsync(stream.fileno())
        return event

    def observe_native(self, pid, start, bind=False, cim_creation_utc=None):
        if self.native is None:
            return {"pid": pid, "state": "not_applicable", "method": "linux_proc_start_identity"}
        key = (pid, start)
        native = self.native.inspect(pid, self.native_starts.get(key))
        if key not in self.native_starts:
            joined = cim_matches_native(cim_creation_utc, native.get("creationFileTime"))
            if bind and native["state"] == "live" and joined:
                self.native_starts[key] = native["creationFileTime"]
                native = dict(native, initialIdentityJoin="matched_at_cim_microsecond_resolution",
                              cimCreationUtc=cim_creation_utc)
            else:
                # Never assign a new PID occupant's native identity to an older
                # CIM ancestry record. Missing timestamps remain explicitly unknown.
                reason = "not_a_live_binding" if native["state"] != "live" else \
                    "cim_native_creation_not_joined" if not joined else "binding_not_requested"
                native = dict(native, unboundState=native["state"], state="unknown",
                              identityBindingReason=reason, cimCreationUtc=cim_creation_utc)
        return native

    def records(self, data, precise, keys, bind=False):
        records = []
        for pid, start in sorted(keys):
            row = {"pid": pid, "startIdentity": start,
                   "presentWithOriginalIdentity": pid in data and str(data[pid][1]) == start,
                   "native": self.observe_native(pid, start, bind=bind, cim_creation_utc=precise.get(pid))}
            if pid in data:
                row.update(parentPid=data[pid][0], currentStartIdentity=str(data[pid][1]),
                           creationUtc=precise.get(pid), kind=data[pid][2])
            records.append(row)
        return records

    def fail(self, message):
        self.report.update(status="failed", failure=str(message),
                           elapsedSec=round(time.monotonic() - self.started, 2))
        self.persist()
        completed = {}
        for path in sorted(self.directory.glob("process-*.json")):
            value = json.loads(path.read_text(encoding="utf-8"))
            completed[value["run"]] = value
        atomic_json(self.directory / "failure-summary.json", {
            "schema": SCHEMA, "status": "failed", "failureStage": self.stage,
            "failure": str(message), "outcomes": completed,
            "originalHistoricalFailure": "unchanged_not_reclassified",
            "rootCause": "not_proven_by_this_diagnostic", "autoApply": False,
            "ArBilling": "not_run"})
        write_manifest(self.directory)

    def check(self, condition, message):
        if not condition:
            # Both human-readable summary and its manifest survive an assertion.
            self.fail(message)
            raise AssertionError(message)

    def finish(self, result):
        self.report.update(result, status="passed")
        self.persist()
        write_manifest(self.directory)


def run_windows_controls(evidence):
    """A retained exited object and a live child validate observer semantics.

    This helper owns only its synthetic Python subprocess. It cannot clean up
    any NUnit/Chromium process, and its results cannot satisfy the browser gate.
    """
    if os.name != "nt":
        return {"status": "not_applicable", "platform": sys.platform}
    probe = NativeWindowsProbe()
    report = {"schema": SCHEMA, "status": "running", "scope": "synthetic_observer_control",
              "canSatisfyBrowserCleanupGate": False}
    path = Path(evidence) / "windows-process-controls.json"
    process = None
    retained_handle = None
    try:
        process = subprocess.Popen([sys.executable, "-c", "import time;time.sleep(60)"],
                                   stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                                   stderr=subprocess.DEVNULL)
        live = probe.inspect(process.pid)
        report["liveChild"] = live
        atomic_json(path, report)
        if live["state"] != "live" or not live.get("creationFileTime"):
            raise AssertionError("P47_NATIVE_LIVE_CHILD_CONTROL_FAILED")
        before, before_precise = snapshot(sys.executable)
        row = before.get(process.pid)
        report["liveChildCim"] = {"present": row is not None,
            "startIdentity": str(row[1]) if row else None,
            "creationUtc": before_precise.get(process.pid)}
        wrong = probe.inspect(process.pid, str(int(live["creationFileTime"]) + 1))
        report["identityMismatch"] = wrong
        atomic_json(path, report)
        if wrong["state"] != "identity_mismatch":
            raise AssertionError("P47_NATIVE_PID_IDENTITY_CONTROL_FAILED")
        # Retain a handle only to this synthetic child. This explicitly checks
        # the API's signaled-object distinction without modifying browser life.
        retained_handle = probe.api.OpenProcess(0x00100000 | 0x1000, False, process.pid)
        if not retained_handle:
            raise AssertionError("P47_NATIVE_CONTROL_HANDLE_UNAVAILABLE")
        process.terminate()
        process.wait(timeout=5)
        ended = probe.inspect_handle(retained_handle, process.pid, live["creationFileTime"])
        report["exitedWithRetainedOwnerHandle"] = ended
        report["exitedReopenObservation"] = probe.inspect(process.pid, live["creationFileTime"])
        after, after_precise = snapshot(sys.executable)
        after_row = after.get(process.pid)
        same = row is not None and after_row is not None and str(row[1]) == str(after_row[1])
        report["exitedChildCim"] = {"present": after_row is not None,
            "startIdentity": str(after_row[1]) if after_row else None,
            "creationUtc": after_precise.get(process.pid),
            "originalIdentityMatches": same,
            "oldPresenceGateWouldReject": same,
            "nativeWaitState": ended["state"]}
        atomic_json(path, report)
        if ended["state"] != "exited":
            raise AssertionError("P47_NATIVE_EXITED_OBJECT_CONTROL_FAILED")
        # CIM is allowed either to retain or remove an exited process record.
        # The control records which happened; it does not require a false positive.
        report["status"] = "passed"
        return report
    except BaseException as error:
        report.update(status="failed", failure=str(error))
        raise
    finally:
        if retained_handle:
            probe.api.CloseHandle(retained_handle)
        # Finite cleanup of our synthetic child; never a browser/API witness.
        if process is not None and process.poll() is None:
            process.kill()
            process.wait(timeout=5)
        atomic_json(path, report)
        write_manifest(evidence)
