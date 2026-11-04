#!/usr/bin/env python3
"""Independent Windows host-owned Job Object containment probe.

This does not launch Playwright or clean any P47 browser. It verifies a
real Win32 parent/grandchild survives parent loss until the LAST job
handle is closed. The original 12-second Chromium presence gate runs
in a separate, unchanged stage before this control.
"""
import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time

import p2_preview47_process_evidence as evidence

SCHEMA = "migrator-preview47-windows-host-job-object/v1"
KILL_ON_JOB_CLOSE = 0x00002000
BREAKAWAY_OK = 0x00000800
SILENT_BREAKAWAY_OK = 0x00001000
NO_WINDOW = 0x08000000


class BasicLimits(ctypes.Structure):
    _fields_ = [
        ("PerProcessUserTimeLimit", ctypes.c_int64),
        ("PerJobUserTimeLimit", ctypes.c_int64),
        ("LimitFlags", wintypes.DWORD),
        ("MinimumWorkingSetSize", ctypes.c_size_t),
        ("MaximumWorkingSetSize", ctypes.c_size_t),
        ("ActiveProcessLimit", wintypes.DWORD),
        ("Affinity", ctypes.c_size_t),
        ("PriorityClass", wintypes.DWORD),
        ("SchedulingClass", wintypes.DWORD),
    ]


class IoCounters(ctypes.Structure):
    _fields_ = [(name, ctypes.c_uint64) for name in (
        "ReadOperationCount", "WriteOperationCount", "OtherOperationCount",
        "ReadTransferCount", "WriteTransferCount", "OtherTransferCount")]


class ExtendedLimits(ctypes.Structure):
    _fields_ = [
        ("BasicLimitInformation", BasicLimits),
        ("IoInfo", IoCounters),
        ("ProcessMemoryLimit", ctypes.c_size_t),
        ("JobMemoryLimit", ctypes.c_size_t),
        ("PeakProcessMemoryUsed", ctypes.c_size_t),
        ("PeakJobMemoryUsed", ctypes.c_size_t),
    ]


def require(condition, code):
    if not condition:
        raise AssertionError(code)


def win32():
    api = ctypes.WinDLL("kernel32", use_last_error=True)
    api.CreateJobObjectW.argtypes = (ctypes.c_void_p, wintypes.LPCWSTR)
    api.CreateJobObjectW.restype = wintypes.HANDLE
    api.SetInformationJobObject.argtypes = (wintypes.HANDLE, ctypes.c_int,
                                           ctypes.c_void_p, wintypes.DWORD)
    api.SetInformationJobObject.restype = wintypes.BOOL
    api.QueryInformationJobObject.argtypes = (wintypes.HANDLE, ctypes.c_int,
                                             ctypes.c_void_p, wintypes.DWORD,
                                             ctypes.c_void_p)
    api.QueryInformationJobObject.restype = wintypes.BOOL
    api.AssignProcessToJobObject.argtypes = (wintypes.HANDLE, wintypes.HANDLE)
    api.AssignProcessToJobObject.restype = wintypes.BOOL
    api.IsProcessInJob.argtypes = (wintypes.HANDLE, wintypes.HANDLE,
                                  ctypes.POINTER(wintypes.BOOL))
    api.IsProcessInJob.restype = wintypes.BOOL
    api.OpenProcess.argtypes = (wintypes.DWORD, wintypes.BOOL, wintypes.DWORD)
    api.OpenProcess.restype = wintypes.HANDLE
    api.WaitForSingleObject.argtypes = (wintypes.HANDLE, wintypes.DWORD)
    api.WaitForSingleObject.restype = wintypes.DWORD
    api.CloseHandle.argtypes = (wintypes.HANDLE,)
    api.CloseHandle.restype = wintypes.BOOL
    api.TerminateProcess.argtypes = (wintypes.HANDLE, wintypes.UINT)
    api.TerminateProcess.restype = wintypes.BOOL
    return api


def member(api, process_handle, job):
    result = wintypes.BOOL()
    require(api.IsProcessInJob(process_handle, job, ctypes.byref(result)),
            "P47_JOB_MEMBERSHIP_QUERY_FAILED")
    return bool(result.value)


PARENT = (
    "from pathlib import Path\n"
    "import subprocess,sys,time\n"
    "if sys.stdin.buffer.read(1) != b'G': sys.exit(3)\n"
    "child=subprocess.Popen([sys.executable, '-c', 'import time;time.sleep(60)'],"
    " stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,"
    " stderr=subprocess.DEVNULL,creationflags=0x08000000,close_fds=True)\n"
    "Path(sys.argv[1]).write_text(str(child.pid),encoding='ascii')\n"
    "time.sleep(60)\n"
)


def main():
    require(os.name == "nt" and ctypes.sizeof(ctypes.c_void_p) == 8,
            "P47_JOB_WIN_X64_REQUIRED")
    path = Path("artifacts/pom-preview47")
    path.mkdir(parents=True, exist_ok=True)
    receipt = path / "job-object-probe.json"
    report = {
        "schema": SCHEMA, "status": "running",
        "scope": "independent_synthetic_parent_grandchild",
        "actualPlaywrightDriverUsed": False,
        "chromiumProcessesModified": False,
        "browserBehaviorVerified": False,
        "originalP47PresenceGateAltered": False,
        "passiveWindowSecondsOriginal": 12,
        "originalHistoricalOrphanFailure": "preserved_unresolved",
        "hostOwner": "independent_python_supervisor",
        "exitPolicy": "KILL_ON_JOB_CLOSE",
        "breakawayAllowed": False,
    }
    evidence.atomic_json(receipt, report)
    api = win32()
    job = None
    parent = None
    child_handle = None
    probe = evidence.NativeWindowsProbe()
    try:
        require(ctypes.sizeof(ExtendedLimits) == 144,
                "P47_JOB_EXTENDED_LIMIT_LAYOUT_UNVERIFIED")
        job = api.CreateJobObjectW(None, None)
        require(bool(job), "P47_JOB_CREATE_FAILED")
        limits = ExtendedLimits()
        limits.BasicLimitInformation.LimitFlags = KILL_ON_JOB_CLOSE
        require(api.SetInformationJobObject(job, 9, ctypes.byref(limits),
                                            ctypes.sizeof(limits)),
                "P47_JOB_SET_LIMITS_FAILED")
        effective = ExtendedLimits()
        require(api.QueryInformationJobObject(job, 9, ctypes.byref(effective),
                                              ctypes.sizeof(effective), None),
                "P47_JOB_QUERY_LIMITS_FAILED")
        flags = effective.BasicLimitInformation.LimitFlags
        report["effectiveLimitFlags"] = int(flags)
        require(flags & KILL_ON_JOB_CLOSE and not flags & (
                BREAKAWAY_OK | SILENT_BREAKAWAY_OK),
                "P47_JOB_KILL_ON_CLOSE_OR_BREAKAWAY_UNVERIFIED")
        with tempfile.TemporaryDirectory(prefix="p47-owned-job-") as temporary:
            child_pid_file = Path(temporary) / "child-pid"
            parent = subprocess.Popen(
                [sys.executable, "-u", "-c", PARENT, str(child_pid_file)],
                stdin=subprocess.PIPE, stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL, creationflags=NO_WINDOW,
                close_fds=True)
            report["parentPid"] = parent.pid
            # This parent is blocked on its one-byte gate and cannot spawn a
            # child before the exact job association is established.
            require(api.AssignProcessToJobObject(job, parent._handle),
                    "P47_JOB_ASSIGN_PARENT_FAILED")
            require(member(api, parent._handle, job),
                    "P47_JOB_PARENT_NOT_IN_JOB")
            parent.stdin.write(b"G")
            parent.stdin.flush()
            parent.stdin.close()
            deadline = time.monotonic() + 10.0
            while not child_pid_file.exists() and time.monotonic() < deadline:
                require(parent.poll() is None, "P47_JOB_PARENT_EARLY_EXIT")
                time.sleep(.1)
            require(child_pid_file.is_file(), "P47_JOB_CHILD_NOT_CREATED")
            child_pid = int(child_pid_file.read_text(encoding="ascii").strip())
            report["childPid"] = child_pid
            # This handle and all cleanup calls concern our own synthetic
            # subprocess only, not Chromium, Node or another user's process.
            # PROCESS_TERMINATE is limited to our own synthetic child, so even
            # if closing the Job handle fails we can fail safely without leaks.
            child_handle = api.OpenProcess(0x00100000 | 0x1000 | 0x0001, False, child_pid)
            require(bool(child_handle), "P47_JOB_CHILD_HANDLE_UNAVAILABLE")
            require(member(api, child_handle, job), "P47_JOB_CHILD_NOT_IN_JOB")
            before = probe.inspect_handle(child_handle, child_pid)
            report["childNativeBeforeParentLoss"] = before
            require(before["state"] == "live" and before.get("creationFileTime"),
                    "P47_JOB_CHILD_NOT_LIVE")
            process_table, _ = evidence.snapshot(sys.executable)
            row = process_table.get(child_pid)
            report["independentParentPid"] = row[0] if row else None
            require(row is not None and row[0] == parent.pid,
                    "P47_JOB_CHILD_ANCESTRY_UNVERIFIED")
            parent.terminate()
            parent.wait(timeout=8)
            report["parentTerminated"] = parent.returncode is not None
            survivor = probe.inspect_handle(
                child_handle, child_pid, before["creationFileTime"])
            report["childAfterParentLossBeforeJobClose"] = survivor
            require(survivor["state"] == "live",
                    "P47_JOB_CONTROL_CHILD_DID_NOT_SURVIVE_PARENT")
            # The last handle close is the *host-owned* cleanup boundary.
            # It occurs after the synthetic survivor observation. The
            # unassisted real Chromium P47 gate is NOT touched by this script.
            require(api.CloseHandle(job), "P47_JOB_CLOSE_FAILED")
            job = None
            wait_result = api.WaitForSingleObject(child_handle, 8000)
            report["waitAfterLastJobHandleClose"] = int(wait_result)
            after = probe.inspect_handle(
                child_handle, child_pid, before["creationFileTime"])
            report["childAfterLastJobHandleClose"] = after
            require(wait_result == 0 and after["state"] == "exited",
                    "P47_JOB_CHILD_NOT_TERMINATED_ON_CLOSE")
            report["status"] = "passed"
            print("P47_WINDOWS_JOB_OBJECT_SYNTHETIC_CONTAINMENT_PASSED")
    except BaseException as ex:
        report.update(status="failed", failureType=type(ex).__name__,
                      failureCode=str(ex) if isinstance(ex, AssertionError)
                      else "P47_JOB_WIN32_UNEXPECTED_FAILURE")
        raise
    finally:
        # Always close the job: on success it was already closed. A failed
        # assignment never grants permission to terminate unrelated PIDs.
        if job is not None:
            api.CloseHandle(job)
        if child_handle is not None:
            state = probe.inspect_handle(child_handle, report.get("childPid", 0))
            if state["state"] == "live":
                api.TerminateProcess(child_handle, 1)
                api.WaitForSingleObject(child_handle, 5000)
            api.CloseHandle(child_handle)
        if parent is not None:
            if parent.poll() is None:
                parent.kill()
            parent.wait(timeout=8)
            if parent.stdin and not parent.stdin.closed:
                parent.stdin.close()
        evidence.atomic_json(receipt, report)
        evidence.write_manifest(path)


if __name__ == "__main__":
    main()
