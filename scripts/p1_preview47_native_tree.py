"""P1-F: independent native sampling of P47's existing Windows process tree.

No hooks or synchronization enter the original P47 process. Sampling takes place
outside the testhost's Job and is joined to P47's original durable timeline
*after* its unchanged 12-second presence gate. Unknown is never a proof.
"""
import ctypes
from ctypes import wintypes
import json
from pathlib import Path

SCHEMA = "migrator-preview47-p1f-native-tree/v1"
TH32CS_SNAPPROCESS = 0x00000002
ERROR_NO_MORE_FILES = 18
INVALID_HANDLE_VALUE = ctypes.c_void_p(-1).value


class ProcessEntry(ctypes.Structure):
    _fields_ = [
        ("dwSize", wintypes.DWORD), ("cntUsage", wintypes.DWORD),
        ("th32ProcessID", wintypes.DWORD), ("th32DefaultHeapID", ctypes.c_void_p),
        ("th32ModuleID", wintypes.DWORD), ("cntThreads", wintypes.DWORD),
        ("th32ParentProcessID", wintypes.DWORD), ("pcPriClassBase", wintypes.LONG),
        ("dwFlags", wintypes.DWORD), ("szExeFile", ctypes.c_wchar * 260),
    ]


def owned_chain(processes, pid, wrapper_pid):
    """Return complete CURRENT-snapshot ancestry, no PID-only historical joins."""
    chain, seen = [], set()
    while pid not in seen and pid in processes:
        seen.add(pid)
        chain.append(pid)
        if pid == wrapper_pid:
            return chain
        pid = processes[pid][0]
    return None


def join_prefault(samples, events):
    """Join independently sampled live Win32 identities to original pre-fault rows.

    The original P47 source-backed ancestry/identity must match both PID and
    creation FILETIME. Its 'runner_active' event must precede the real SIGTERM.
    The native sample must ALSO predate SIGTERM; late observation is excluded.
    """
    fault = next((e["utc"] for e in events
                  if e.get("phase") == "driver_termination_before_signal"), None)
    if not fault:
        return {}
    sampled = {(s["pid"], str(s["creationFileTime"]), s["kind"]): s
               for s in samples if s["checkedUtc"] < fault and s.get("nativeState") == "live"}
    joined = {}
    for event in events:
        if event.get("phase") != "runner_active" or event["utc"] >= fault:
            continue
        for row in event.get("processes", []):
            native = row.get("native") or {}
            expected = native.get("expectedCreationFileTime")
            if (row.get("kind") not in ("driver", "chrome") or not expected
                    or not row.get("presentWithOriginalIdentity")
                    or native.get("state") != "live"):
                continue
            observed = sampled.get((row["pid"], str(expected), row["kind"]))
            if observed is None or not observed.get("pathToWrapper"):
                continue
            key = (row["pid"], row["startIdentity"])
            joined[key] = {
                "pid": row["pid"], "startIdentity": row["startIdentity"],
                "kind": row["kind"], "creationFileTime": str(expected),
                "state": "live", "nativeState": "live", "checkedUtc": observed["checkedUtc"],
                "inHostJob": observed["inHostJob"],
                "pathToWrapper": observed["pathToWrapper"],
                "sourceBackedBeforeFault": True,
            }
    return joined


class NativeTreeObserver:
    """Toolhelp process tree, GetProcessTimes, Wait(0), IsProcessInJob.

    Collect first live observation for each Windows (PID, creation FILETIME,
    kind) while the original stage is running, then join to the original
    immutable timeline. Observations do not change any process or timing gate.
    """
    def __init__(self, api, native, job, wrapper_pid, wrapper_filetime, arm,
                 source, stages, utc_now, child_rights):
        self.api, self.native, self.job = api, native, job
        self.wrapper_pid, self.wrapper_filetime = wrapper_pid, str(wrapper_filetime)
        self.arm, self.source = arm, Path(source)
        self.stages, self.utc_now, self.child_rights = stages, utc_now, child_rights
        self.samples, self.polls, self.processes_seen = {}, 0, 0
        api.CreateToolhelp32Snapshot.argtypes = (wintypes.DWORD, wintypes.DWORD)
        api.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
        api.Process32FirstW.argtypes = (wintypes.HANDLE, ctypes.POINTER(ProcessEntry))
        api.Process32FirstW.restype = wintypes.BOOL
        api.Process32NextW.argtypes = (wintypes.HANDLE, ctypes.POINTER(ProcessEntry))
        api.Process32NextW.restype = wintypes.BOOL
        if ctypes.sizeof(ctypes.c_void_p) != 8 or ctypes.sizeof(ProcessEntry) != 568:
            raise AssertionError("P1F_TOOLHELP_LAYOUT_NOT_X64")

    def snapshot(self):
        handle = self.api.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
        if not handle or handle == INVALID_HANDLE_VALUE:
            raise OSError(ctypes.get_last_error(), "P1F_TOOLHELP_SNAPSHOT")
        result = {}
        try:
            entry = ProcessEntry()
            entry.dwSize = ctypes.sizeof(entry)
            ok = self.api.Process32FirstW(handle, ctypes.byref(entry))
            if not ok:
                raise OSError(ctypes.get_last_error(), "P1F_TOOLHELP_FIRST")
            while ok:
                result[int(entry.th32ProcessID)] = (
                    int(entry.th32ParentProcessID), entry.szExeFile.lower())
                ok = self.api.Process32NextW(handle, ctypes.byref(entry))
            error = ctypes.get_last_error()
            if error != ERROR_NO_MORE_FILES:
                raise OSError(error, "P1F_TOOLHELP_NEXT")
        finally:
            if not self.api.CloseHandle(handle):
                raise OSError(ctypes.get_last_error(), "P1F_TOOLHELP_CLOSE")
        return result

    def active_stage(self):
        for stage, _expected, disconnect in reversed(self.stages):
            if not disconnect:
                continue
            path = self.source / f"process-{stage}.json"
            # The original stage updates JSON via os.replace(). Opening it for
            # polling races with rename on Windows (WinError 5); stat-only.
            # Previous stage files remain, so the latest existing stage wins.
            if path.is_file():
                return stage
        return None

    def inspect_chain(self, chain, processes):
        """Verify live native identities for every ancestor, including wrapper."""
        proofs = []
        for pid in reversed(chain):
            handle = self.api.OpenProcess(self.child_rights, False, pid)
            if not handle:
                return None  # normal exit race; never infer liveness
            try:
                row = self.native.inspect_handle(handle, pid, None)
                if row["state"] != "live" or not row.get("creationFileTime"):
                    return None
                if pid == self.wrapper_pid and str(row["creationFileTime"]) != self.wrapper_filetime:
                    return None
                from p2_preview47_windows_job_object_probe import member
                in_job = member(self.api, handle, self.job)
                if in_job != (self.arm == "B"):
                    # A/B assignment isn't established for this descendant.
                    return None
                proofs.append({"pid": pid, "parentPid": processes[pid][0],
                               "creationFileTime": str(row["creationFileTime"]),
                               "inHostJob": in_job})
            finally:
                self.api.CloseHandle(handle)
        if any(int(child["creationFileTime"]) < int(parent["creationFileTime"])
               for parent, child in zip(proofs, proofs[1:])):
            return None
        return proofs

    def poll(self):
        self.polls += 1
        stage = self.active_stage()
        if stage is None:
            return
        processes = self.snapshot()
        self.processes_seen += len(processes)
        stage_samples = self.samples.setdefault(stage, {})
        for pid, (_parent, filename) in processes.items():
            kind = "driver" if filename == "node.exe" else "chrome" if filename == "chrome.exe" else None
            if kind is None or pid == self.wrapper_pid:
                continue
            chain = owned_chain(processes, pid, self.wrapper_pid)
            if chain is None:
                continue
            # No PID reuse: native FILETIME for entire living ancestry is read
            # independently from the original CIM snapshot.
            proofs = self.inspect_chain(chain, processes)
            if proofs is None:
                continue
            leaf = proofs[-1]
            key = (pid, leaf["creationFileTime"], kind)
            if key in stage_samples:
                continue
            stage_samples[key] = {
                "pid": pid, "kind": kind,
                "creationFileTime": leaf["creationFileTime"],
                "nativeState": "live", "inHostJob": leaf["inHostJob"],
                "checkedUtc": self.utc_now(), "pathToWrapper": proofs,
                "windowsExecutable": filename,
            }

    def joined(self):
        output = {}
        for stage, _expected, disconnect in self.stages:
            if not disconnect:
                continue
            path = self.source / f"process-{stage}.jsonl"
            if path.is_file():
                events = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]
                output[stage] = join_prefault(list(self.samples.get(stage, {}).values()), events)
        return output

    def report(self):
        return {"schema": SCHEMA, "arm": self.arm, "pollCount": self.polls,
                "toolhelpProcessesEnumerated": self.processes_seen,
                "observationSource": "Toolhelp32 + GetProcessTimes + Wait(0) + IsProcessInJob",
                "originalP47Modified": False, "originalGateSeconds": 12,
                "samples": {stage: list(entries.values())
                            for stage, entries in self.samples.items()}}
