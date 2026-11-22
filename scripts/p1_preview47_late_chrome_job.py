"""P1-G experimental late Chrome-only Job attachment, not product mitigation.

The original P47, NUnit source, driver SIGTERM and runner-exit-anchored
12-second gate are unchanged. This observer only puts already-running,
source-owned Chrome processes into the host-owned Job BEFORE the unmodified
P47 ready marker. The Playwright Node and the wrapper remain outside the Job.

A surviving Chrome is a real post-gate witness ONLY after a native FILETIME
and source lineage join; Job close is proved separately after the failed gate.
"""
import ctypes
from pathlib import Path

import p1_preview47_native_tree as tree
import p2_preview47_windows_job_object_probe as jobs

SCHEMA = "migrator-preview47-p1g-late-chrome-attachment/v1"
ASSIGN_RIGHTS = 0x00100000 | 0x1000 | 0x0100 | 0x0001
MAX_REJECTIONS = 64


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

    def _reject(self, reason, pid, detail=None):
        if len(self.rejections) < MAX_REJECTIONS:
            self.rejections.append({"stage": self.active_name, "pid": pid,
                                    "reason": reason, "detail": detail,
                                    "utc": self.utc_now()})

    def poll(self):
        self.active_name = self.active_stage()
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
                      rejectedObservations=self.rejections)
        return result
