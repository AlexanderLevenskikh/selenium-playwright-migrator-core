"""P1-G experimental late Chrome-only Job attachment, not product mitigation.

The original P47, NUnit source, driver SIGTERM and runner-exit-anchored
12-second gate are unchanged. This observer only puts already-running,
source-owned Chrome processes into the host-owned Job BEFORE the unmodified
P47 ready marker. The Playwright Node and the wrapper remain outside the Job.

A surviving Chrome is a real post-gate witness ONLY after a native FILETIME
and source lineage join; Job close is proved separately after the failed gate.
"""
import ctypes
from ctypes import wintypes
import json
from pathlib import Path

import p1_preview47_native_tree as tree
import p2_preview47_windows_job_object_probe as jobs

SCHEMA = "migrator-preview47-p1g-late-chrome-attachment/v1"
ASSIGN_RIGHTS = 0x00100000 | 0x1000 | 0x0100 | 0x0001
MAX_REJECTIONS = 64

class BasicAccounting(ctypes.Structure):
    """JOB_OBJECT_BASIC_ACCOUNTING_INFORMATION, Win32 x64; Class 1."""
    _fields_ = [
        ("TotalUserTime", ctypes.c_int64), ("TotalKernelTime", ctypes.c_int64),
        ("ThisPeriodTotalUserTime", ctypes.c_int64),
        ("ThisPeriodTotalKernelTime", ctypes.c_int64),
        ("TotalPageFaultCount", wintypes.DWORD),
        ("TotalProcesses", wintypes.DWORD),
        ("ActiveProcesses", wintypes.DWORD),
        ("TotalTerminatedProcesses", wintypes.DWORD),
    ]



def attachment_allowed(ready_marker):
    """Stat-only: never change/read the original P47 handshake or JSON files."""
    return not Path(ready_marker).exists()


def membership_contract(arm, members):
    kinds = {row.get("kind") for row in members}
    if kinds != {"chrome", "driver"}:
        return "identity_or_membership_unattested"
    if arm == "B_late":
        chrome = [row for row in members if row["kind"] == "chrome"]
        drivers = [row for row in members if row["kind"] == "driver"]
        if (chrome and drivers and all(row.get("inHostJob") is True for row in chrome)
                and all(row.get("inHostJob") is False for row in drivers)
                and all(row.get("pathToWrapper") for row in members)):
            return "chrome_attested_driver_outside_job"
    elif all(row.get("inHostJob") is (arm == "B") for row in members):
        return "attested"
    return "identity_or_membership_unattested"


class LateChromeJobObserver(tree.NativeTreeObserver):
    """Assign Chrome-only at creation-time observation; freeze on P47 ready.

    The process ancestry is validated by live native handles *before*
    association. Every actual association receives an identity-bound receipt.
    These are host-observer actions, not injected actions in P47 or NUnit.
    """
    def __init__(self, api, native, job, wrapper_pid, wrapper_filetime, arm,
                 source, stages, utc_now, child_rights):
        if arm != "B_late":
            raise AssertionError("P1G_LATE_ARM_REQUIRED")
        super().__init__(api, native, job, wrapper_pid, wrapper_filetime, arm,
                         source, stages, utc_now, child_rights)
        self.active_name = None
        self.can_attach = False
        self.associations = {}
        self.cutoffs = {}
        self.rejections = []
        self.job_ref = None
        self.job_generation = 1
        self.stage_rotations = []

    def _reject(self, reason, pid, detail=None):
        if len(self.rejections) < MAX_REJECTIONS:
            self.rejections.append({"stage": self.active_name, "pid": pid,
                                    "reason": reason, "detail": detail,
                                    "utc": self.utc_now()})

    def bind_job_ref(self, ref):
        if ref.get("handle") != self.job:
            raise AssertionError("P1G_JOB_REF_MISMATCH")
        self.job_ref = ref

    def _rotate_after_original_pass(self, previous, next_stage):
        # Next stage's original evidence file exists only after P47 returned
        # from previous run_probe and its original gate. No same-file race.
        if self.job_ref is None or self.job_ref.get("handle") != self.job:
            raise AssertionError("P1G_STAGE_JOB_OWNER_MISSING")
        previous_path = self.source / ("process-" + previous + ".json")
        receipt = json.loads(previous_path.read_text(encoding="utf-8"))
        if (receipt.get("status") != "passed"
                or receipt.get("originalPresenceGate") != "passed"
                or receipt.get("originalPresenceSurvivors")
                or receipt.get("survivingObservedProcessCount") != 0):
            raise AssertionError("P1G_ROTATION_BEFORE_ORIGINAL_PASSED_GATE")
        members = [row for row in self.samples.get(previous, {}).values()
                   if row.get("kind") == "chrome" and row.get("inHostJob") is True]
        if not members:
            raise AssertionError("P1G_PREVIOUS_STAGE_CHROME_MEMBERSHIP_UNPROVEN")
        # No Job close may kill a Chrome after an original green gate.
        for row in members:
            state = self.native.inspect(row["pid"], row["creationFileTime"])
            if state.get("state") not in ("absent", "exited", "identity_mismatch"):
                raise AssertionError("P1G_PREVIOUS_STAGE_MEMBER_STILL_LIVE_OR_UNKNOWN")
        if ctypes.sizeof(ctypes.c_void_p) != 8 or ctypes.sizeof(BasicAccounting) != 48:
            raise AssertionError("P1G_JOB_ACCOUNTING_LAYOUT_NOT_X64")
        accounting = BasicAccounting()
        if not self.api.QueryInformationJobObject(
                self.job, 1, ctypes.byref(accounting),
                ctypes.sizeof(accounting), None):
            raise OSError(ctypes.get_last_error(), "P1G_QUERY_JOB_ACCOUNTING")
        if accounting.ActiveProcesses != 0:
            raise AssertionError("P1G_JOB_HAS_LIVE_MEMBERS_AFTER_ORIGINAL_GATE")
        old = self.job
        if not self.api.CloseHandle(old):
            raise OSError(ctypes.get_last_error(), "P1G_CLOSE_EMPTY_STAGE_JOB")
        self.job_ref["handle"] = None
        new = self.api.CreateJobObjectW(None, None)
        if not new:
            raise OSError(ctypes.get_last_error(), "P1G_CREATE_NEXT_STAGE_JOB")
        self.job_ref["handle"] = new
        self.job = new
        limits = jobs.ExtendedLimits()
        limits.BasicLimitInformation.LimitFlags = jobs.KILL_ON_JOB_CLOSE
        if not self.api.SetInformationJobObject(
                new, 9, ctypes.byref(limits), ctypes.sizeof(limits)):
            raise OSError(ctypes.get_last_error(), "P1G_SET_NEXT_STAGE_LIMITS")
        if not self.api.QueryInformationJobObject(
                new, 9, ctypes.byref(limits), ctypes.sizeof(limits), None):
            raise OSError(ctypes.get_last_error(), "P1G_QUERY_NEXT_STAGE_LIMITS")
        if limits.BasicLimitInformation.LimitFlags != jobs.KILL_ON_JOB_CLOSE:
            raise AssertionError("P1G_NEXT_JOB_LIMIT_FLAGS_CHANGED")
        self.job_generation += 1
        self.stage_rotations.append({
            "previousStage": previous, "nextStage": next_stage,
            "previousOriginalGate": "passed",
            "activeJobProcessesBeforeClose": 0,
            "knownChromeMembersConfirmedExitedOrAbsent": len(members),
            "closedAfterOriginalGate": True,
            "newJobGeneration": self.job_generation,
            "rotatedUtc": self.utc_now(),
        })

    def poll(self):
        stage = self.active_stage()
        if stage and self.active_name and stage != self.active_name:
            self._rotate_after_original_pass(self.active_name, stage)
        self.active_name = stage
        if self.active_name is not None:
            ready = self.source / ("ready-" + self.active_name)
            self.can_attach = attachment_allowed(ready)
            if not self.can_attach and self.active_name not in self.cutoffs:
                self.cutoffs[self.active_name] = self.utc_now()
        else:
            self.can_attach = False
        super().poll()

    def inspect_chain(self, chain, processes):
        # Toolhelp ancestry is from the current snapshot, never PID-only
        # historic ancestry. Native handles remain open through the final
        # association check, including the root wrapper identity.
        proofs = []
        target = chain[0]
        is_chrome = processes[target][1] == "chrome.exe"
        handles = []
        try:
            for pid in reversed(chain):
                rights = ASSIGN_RIGHTS if pid == target and is_chrome else self.child_rights
                handle = self.api.OpenProcess(rights, False, pid)
                if not handle:
                    self._reject("process_handle_unavailable", pid, ctypes.get_last_error())
                    return None
                handles.append(handle)
                current = self.native.inspect_handle(handle, pid, None)
                if current.get("state") != "live" or not current.get("creationFileTime"):
                    return None
                creation = str(current["creationFileTime"])
                if pid == self.wrapper_pid and creation != self.wrapper_filetime:
                    self._reject("wrapper_pid_reused", pid)
                    return None
                in_job = jobs.member(self.api, handle, self.job)
                if pid == target:
                    if is_chrome:
                        if not in_job:
                            # Check again directly at the assignment point.
                            # Never attach after P47 has signaled readiness:
                            # only the external observer controls this cutoff.
                            ready = self.source / ("ready-" + self.active_name)
                            if not self.can_attach or not attachment_allowed(ready):
                                self._reject("attachment_frozen_at_ready", pid)
                                return None
                            if not self.api.AssignProcessToJobObject(self.job, handle):
                                self._reject("assign_failed", pid, ctypes.get_last_error())
                                return None
                            checked = self.native.inspect_handle(handle, pid, creation)
                            if (checked.get("state") != "live"
                                    or str(checked.get("creationFileTime")) != creation):
                                self._reject("native_identity_changed_after_assign", pid)
                                return None
                            in_job = jobs.member(self.api, handle, self.job)
                            if not in_job:
                                self._reject("assign_membership_not_verified", pid)
                                return None
                            key = (self.active_name, pid, creation)
                            self.associations.setdefault(key, {
                                "stage": self.active_name, "pid": pid,
                                "creationFileTime": creation, "kind": "chrome",
                                "mode": "explicit_assign_after_launch_before_ready",
                                "readyWasAbsentAtAssignment": True,
                                "jobGeneration": self.job_generation,
                                "assignedUtc": self.utc_now(),
                                "inHostJobAfterAssignment": True,
                            })
                    elif in_job:
                        self._reject("node_unexpectedly_in_chrome_only_job", pid)
                        return None
                proofs.append({"pid": pid, "parentPid": processes[pid][0],
                               "creationFileTime": creation, "inHostJob": in_job})
            if any(int(child["creationFileTime"]) < int(parent["creationFileTime"])
                   for parent, child in zip(proofs, proofs[1:])):
                self._reject("native_ancestry_time_reversal", target)
                return None
            leaf = proofs[-1]
            if is_chrome and leaf["inHostJob"] is not True:
                return None
            if not is_chrome and leaf["inHostJob"] is not False:
                return None
            return proofs
        finally:
            for handle in handles:
                self.api.CloseHandle(handle)

    def report(self):
        result = super().report()
        result.update(schema=SCHEMA,
                      assignmentTiming="after_chrome_launch_before_original_ready",
                      assignmentCutoff="original_ready_marker_no_later_association",
                      onlyChromeAssigned=True,
                      wrapperAndNodeAssigned=False,
                      productPreCreateContainmentProved=False,
                      associations=sorted(self.associations.values(),
                                          key=lambda item: (item["stage"], item["pid"])),
                      readyMarkerObservedUtc=self.cutoffs,
                      stageJobRotations=self.stage_rotations,
                      jobGenerationAtOriginalExit=self.job_generation,
                      rejectedObservations=self.rejections)
        return result
