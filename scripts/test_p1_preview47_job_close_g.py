"""Portable negative controls for P1-G Windows Chrome Job association.

Tests deliberately use fake Win32 handles; none can satisfy a real Chromium
gate, and all real acceptance comes exclusively from the Actions evidence.
"""
from pathlib import Path
from tempfile import TemporaryDirectory
import json
import unittest
from unittest.mock import patch

import p1_preview47_late_chrome_job as late
import p2_preview47_windows_job_object_probe as jobs
from p1_preview47_job_close_g import inspect_containment


class FakeAPI:
    def __init__(self):
        self.members = set()
        self.assigned = []
        self.closed = []
        self.opened = []

    def OpenProcess(self, rights, _inherit, pid):
        self.opened.append((pid, rights))
        return pid

    def CloseHandle(self, handle):
        self.closed.append(handle)
        return True

    def AssignProcessToJobObject(self, _job, handle):
        self.assigned.append(handle)
        self.members.add(handle)
        return True


class FakeNative:
    def __init__(self):
        self.exited_after_assignment = False

    def inspect_handle(self, handle, pid, expected):
        filetime = str(100000000 + pid)
        if expected and self.exited_after_assignment:
            return {"state": "exited", "creationFileTime": filetime}
        return {"state": "live", "creationFileTime": filetime, "waitResult": 258}


class LateAttachmentTests(unittest.TestCase):
    def setUp(self):
        self.tmp = TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.api, self.native = FakeAPI(), FakeNative()
        self.observer = late.LateChromeJobObserver.__new__(late.LateChromeJobObserver)
        o = self.observer
        o.api, o.native, o.job = self.api, self.native, 77
        o.wrapper_pid, o.wrapper_filetime = 101, "100000101"
        o.arm, o.source = "B_late", Path(self.tmp.name)
        o.utc_now, o.child_rights = lambda: "2026-11-22T00:00:01+00:00", 0x00101000
        o.active_name, o.can_attach = "disconnect_primary", True
        o.associations, o.rejections = {}, []
        o.cutoffs = {}
        self.table = {101: (0, "python.exe"),
                      202: (101, "node.exe"),
                      303: (202, "chrome.exe")}
        self.member_patch = patch.object(jobs, "member",
                                         side_effect=lambda api, handle, job:
                                             handle in api.members)
        self.member_patch.start()
        self.addCleanup(self.member_patch.stop)

    def test_only_live_source_descendant_chrome_assigned(self):
        path = self.observer.inspect_chain([303, 202, 101], self.table)
        self.assertEqual(self.api.assigned, [303])
        self.assertEqual([r["inHostJob"] for r in path], [False, False, True])
        self.assertEqual(len(self.observer.associations), 1)
        proof = next(iter(self.observer.associations.values()))
        self.assertEqual(proof["mode"], "explicit_assign_after_launch_before_ready")
        self.assertTrue(proof["readyWasAbsentAtAssignment"])

    def test_original_ready_marker_freezes_all_new_assignments(self):
        (self.observer.source / "ready-disconnect_primary").write_text("ready")
        result = self.observer.inspect_chain([303, 202, 101], self.table)
        self.assertIsNone(result)
        self.assertEqual(self.api.assigned, [])
        self.assertTrue(any(x["reason"] == "attachment_frozen_at_ready"
                            for x in self.observer.rejections))

    def test_node_is_never_assigned_and_has_native_identity(self):
        path = self.observer.inspect_chain([202, 101], self.table)
        self.assertEqual(self.api.assigned, [])
        self.assertEqual([r["pid"] for r in path], [101, 202])
        self.assertFalse(path[-1]["inHostJob"])

    def test_root_pid_reuse_rejected_before_association(self):
        self.observer.wrapper_filetime = "100000999"
        self.assertIsNone(self.observer.inspect_chain([303, 202, 101], self.table))
        self.assertEqual(self.api.assigned, [])

    def test_native_process_exited_during_assignment_not_attested(self):
        self.native.exited_after_assignment = True
        self.assertIsNone(self.observer.inspect_chain([303, 202, 101], self.table))
        self.assertNotEqual(len(self.api.assigned), 0)
        self.assertTrue(any(x["reason"] == "native_identity_changed_after_assign"
                            for x in self.observer.rejections))


class MembershipTests(unittest.TestCase):
    def test_late_chrome_membership_is_not_precreate(self):
        rows = [{"kind": "driver", "inHostJob": False,
                 "pathToWrapper": [{"pid": 1}]},
                {"kind": "chrome", "inHostJob": True,
                 "pathToWrapper": [{"pid": 1}]}]
        self.assertEqual(late.membership_contract("B_late", rows),
                         "chrome_attested_driver_outside_job")
        self.assertNotEqual(late.membership_contract("B", rows), "attested")

    def test_missing_chrome_or_wrong_node_job_never_attested(self):
        self.assertEqual(late.membership_contract("B_late", [
            {"kind": "driver", "inHostJob": False}]), "identity_or_membership_unattested")
        wrong = [{"kind": "driver", "inHostJob": True, "pathToWrapper": [1]},
                 {"kind": "chrome", "inHostJob": True, "pathToWrapper": [1]}]
        self.assertEqual(late.membership_contract("B_late", wrong),
                         "identity_or_membership_unattested")

    def test_missing_original_ready_defaults_to_allowed_only_for_new_process(self):
        with TemporaryDirectory() as temp:
            path = Path(temp) / "ready-disconnect_primary"
            self.assertTrue(late.attachment_allowed(path))
            path.write_text("ready")
            self.assertFalse(late.attachment_allowed(path))


class PostGateClassificationTests(unittest.TestCase):
    def test_no_survivor_is_not_job_close_proof(self):
        with TemporaryDirectory() as temp:
            path = Path(temp) / "containment.json"
            path.write_text(json.dumps({
                "arm": "B_late", "unassistedGateBeforeHostClose": "passed",
                "postGateHostCleanup": {"status": "no_survivor_not_a_remediation_proof",
                                        "hostTerminatedRealSurvivor": False}}))
            self.assertFalse(inspect_containment(path, "B_late")["proved"])

    def test_early_or_natural_exit_rejected(self):
        with TemporaryDirectory() as temp:
            path = Path(temp) / "containment.json"
            data = {"arm": "B_late", "unassistedGateBeforeHostClose": "failed",
                    "productPreCreateMitigationProved": False,
                    "postGateHostCleanup": {
                        "originalGate": "failed", "unassistedGateStillFailed": True,
                        "originalGateReclassified": False, "nativeLiveAfterHold": True,
                        "jobClosedAfterGate": True, "hostTerminatedRealSurvivor": True,
                        "status": "real_survivors_terminated_after_failed_unassisted_gate",
                        "verifiedBeforeClose": [{"pid": 303}],
                        "observedExitsAfterJobClose": [
                            {"pid": 303, "waitResult": 258, "nativeState": "live"}]}}
            path.write_text(json.dumps(data))
            self.assertFalse(inspect_containment(path, "B_late")["proved"])
            data["postGateHostCleanup"]["observedExitsAfterJobClose"][0].update(
                waitResult=0, nativeState="exited")
            path.write_text(json.dumps(data))
            self.assertTrue(inspect_containment(path, "B_late")["proved"])
            self.assertFalse(inspect_containment(path, "B")["proved"])


if __name__ == "__main__":
    unittest.main()
