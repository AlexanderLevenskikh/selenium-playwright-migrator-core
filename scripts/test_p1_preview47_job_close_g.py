"""Portable negative controls for P1-G Windows Chrome Job association.

Tests deliberately use fake Win32 handles; none can satisfy a real Chromium
gate, and all real acceptance comes exclusively from the Actions evidence.
"""
import ctypes
from pathlib import Path
from tempfile import TemporaryDirectory
import json
import unittest
from unittest.mock import patch

import p1_preview47_late_chrome_job as late
import p2_preview47_windows_job_object_probe as jobs
from p1_preview47_job_close_g import inspect_containment, verify_block_membership


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
        o.job_ref, o.job_generation, o.stage_rotations = {"handle": 77}, 1, []
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


class StageScopedJobRotationTests(LateAttachmentTests):
    def _receipt(self, status="passed", gate="passed"):
        self.observer.job_ref = {"handle": 77}
        self.observer.job = 77
        self.observer.job_generation = 1
        self.observer.stage_rotations = []
        self.observer.samples = {"disconnect_primary": {
            (303, "100000303", "chrome"): {
                "pid": 303, "kind": "chrome",
                "creationFileTime": "100000303", "inHostJob": True}}}
        (self.observer.source / "process-disconnect_primary.json").write_text(
            json.dumps({"status": status, "originalPresenceGate": gate,
                        "originalPresenceSurvivors": [],
                        "survivingObservedProcessCount": 0}))
        return self.observer

    def test_never_close_stage_job_before_original_pass(self):
        observer = self._receipt(status="running")
        with self.assertRaisesRegex(AssertionError, "P1G_ROTATION_BEFORE_ORIGINAL_PASSED_GATE"):
            observer._rotate_after_original_pass("disconnect_primary", "disconnect_pass")
        self.assertEqual(self.api.closed, [])

    def test_never_kill_still_live_member_during_rotation(self):
        observer = self._receipt()
        self.native.inspect = lambda pid, expected: {"state": "live"}
        with self.assertRaisesRegex(AssertionError, "P1G_PREVIOUS_STAGE_MEMBER_STILL_LIVE"):
            observer._rotate_after_original_pass("disconnect_primary", "disconnect_pass")
        self.assertEqual(self.api.closed, [])

    def test_zero_active_members_allows_new_job_only_after_gate(self):
        observer = self._receipt()
        self.native.inspect = lambda pid, expected: {"state": "exited"}
        class API(FakeAPI):
            def QueryInformationJobObject(self, job, kind, pointer, size, _):
                if kind == 1:
                    ctypes.cast(pointer, ctypes.POINTER(late.BasicAccounting)).contents.ActiveProcesses = 0
                elif kind == 9:
                    ctypes.cast(pointer, ctypes.POINTER(jobs.ExtendedLimits)).contents.BasicLimitInformation.LimitFlags = jobs.KILL_ON_JOB_CLOSE
                return True
            def CreateJobObjectW(self, *args):
                return 88
            def SetInformationJobObject(self, *args):
                return True
        api = API()
        observer.api = api
        observer._rotate_after_original_pass("disconnect_primary", "disconnect_pass")
        self.assertEqual(api.closed, [77])
        self.assertEqual(observer.job_ref["handle"], 88)
        self.assertEqual(observer.job_generation, 2)
        self.assertEqual(observer.stage_rotations[0]["previousOriginalGate"], "passed")


class CoverageTests(unittest.TestCase):
    def test_incomplete_job_membership_stops_new_original_block(self):
        with TemporaryDirectory() as directory:
            p = Path(directory) / "stages.json"
            p.write_text(json.dumps([
                {"stage": "disconnect_primary", "disconnect": True,
                 "status": "original_pass",
                 "prefaultMembership": "chrome_attested_driver_outside_job"},
                {"stage": "disconnect_pass", "disconnect": True,
                 "status": "original_pass",
                 "prefaultMembership": "identity_or_membership_unattested"}]))
            receipt = verify_block_membership(Path(directory), "B_late")
            self.assertFalse(receipt["valid"])
            self.assertEqual(receipt["unattestedStages"], ["disconnect_pass"])
            rows = json.loads(p.read_text())
            self.assertTrue(all(x["status"] == "original_pass" for x in rows))
            rows[1]["prefaultMembership"] = "chrome_attested_driver_outside_job"
            p.write_text(json.dumps(rows))
            self.assertTrue(verify_block_membership(Path(directory), "B_late")["valid"])


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
