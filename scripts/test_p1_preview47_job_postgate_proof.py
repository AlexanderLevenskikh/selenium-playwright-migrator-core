#!/usr/bin/env python3
"""Pure fail-closed oracle tests; real child/process proof runs on Windows CI."""
import copy
import unittest
import p1_preview47_job_postgate_proof as proof


class FakeApi:
    def __init__(self, exit_after_close=True, hold_exit=False):
        self.closed = []
        self.waits = []
        self.exit_after_close = exit_after_close
        self.hold_exit = hold_exit
        self.closed_job = False

    def OpenProcess(self, rights, inherit, pid):
        assert rights == proof.CHILD_RIGHTS and not inherit
        return pid + 100

    def WaitForSingleObject(self, handle, timeout):
        self.waits.append((handle, timeout))
        if timeout == proof.HOLD_MILLISECONDS:
            return 0 if self.hold_exit else 258
        if timeout == proof.CLOSE_WAIT_MILLISECONDS:
            return 0 if self.exit_after_close and self.closed_job else 258
        raise AssertionError("unexpected wait")

    def CloseHandle(self, handle):
        self.closed.append(handle)
        if handle == 42:
            self.closed_job = True
        return True


class FakeNative:
    def __init__(self, api, reused=False):
        self.api, self.reused = api, reused

    def inspect_handle(self, handle, pid, expected):
        return {"creationFileTime": "999" if self.reused else expected,
                "state": ("exited" if self.api.closed_job and self.api.exit_after_close
                          else "live")}


def fixture():
    survivors = [{"pid": 1234, "startIdentity": "11/21/2026 20:00:32",
                  "kind": "chrome"}]
    members = [{"pid": 1234, "startIdentity": "11/21/2026 20:00:32",
                "creationFileTime": "134397648320460627", "inHostJob": True,
                "kind": "chrome", "chromiumRole": "crashpad-handler"}]
    return survivors, members


class PostGateTests(unittest.TestCase):
    def run_proof(self, status="failed", survivors=None, members=None,
                  reused=False, hold_exit=False, exit_after_close=True,
                  job_member=True):
        default_survivors, default_members = fixture()
        if survivors is None:
            survivors = default_survivors
        if members is None:
            members = default_members
        report = {"unassistedGateBeforeHostClose": status}
        api = FakeApi(exit_after_close, hold_exit)
        native = FakeNative(api, reused)
        job_ref = {"handle": 42}
        saved = []

        def persist():
            saved.append(copy.deepcopy(report))

        def is_member(_api, _handle, job):
            self.assertEqual(job, 42)
            return job_member

        def go():
            return proof.prove_after_gate(
                api, native, job_ref, members, survivors,
                report, persist, is_member)
        return go, report, job_ref, api, saved

    def test_real_survivor_exit_cannot_reclassify_original_failure(self):
        go, report, job, api, saved = self.run_proof()
        self.assertTrue(go())
        self.assertEqual(report["unassistedGateBeforeHostClose"], "failed")
        self.assertEqual(report["postGateHostCleanup"]["status"],
                         "real_survivors_terminated_after_failed_unassisted_gate")
        self.assertTrue(report["postGateHostCleanup"]["unassistedGateStillFailed"])
        self.assertIsNone(job["handle"])
        self.assertEqual(api.waits, [(1334, 300), (1334, 8000)])
        self.assertEqual(api.closed[-2:], [42, 1334])
        self.assertTrue(all(x["unassistedGateBeforeHostClose"] == "failed"
                            for x in saved))

    def test_no_survivor_never_claims_remediation(self):
        go, report, job, api, _ = self.run_proof("passed", survivors=[])
        self.assertFalse(go())
        self.assertEqual(report["postGateHostCleanup"]["status"],
                         "no_survivor_not_a_remediation_proof")
        self.assertFalse(report["postGateHostCleanup"]["hostTerminatedRealSurvivor"])
        self.assertIsNone(job["handle"])
        self.assertEqual(api.closed, [42])

    def test_gate_disagreement_blocks(self):
        go, _, job, _, _ = self.run_proof("passed")
        with self.assertRaisesRegex(AssertionError, "GATE_RESULT_DISAGREES"):
            go()
        self.assertEqual(job["handle"], 42)

    def test_unattested_pre_fault_process_blocks(self):
        go, report, job, _, _ = self.run_proof(members=[])
        with self.assertRaisesRegex(AssertionError, "NOT_PROVEN_BEFORE_DRIVER_LOSS"):
            go()
        self.assertEqual(job["handle"], 42)
        self.assertNotIn("hostTerminatedRealSurvivor", report["postGateHostCleanup"])

    def test_pid_reuse_cannot_claim_original_process(self):
        go, _, job, api, _ = self.run_proof(reused=True)
        with self.assertRaisesRegex(AssertionError, "NOT_SAME_LIVE_PROCESS"):
            go()
        self.assertFalse(api.closed_job)
        self.assertIn(1334, api.closed)

    def test_membership_mismatch_blocks(self):
        go, _, _, api, _ = self.run_proof(job_member=False)
        with self.assertRaisesRegex(AssertionError, "NO_LONGER_IN_JOB"):
            go()
        self.assertFalse(api.closed_job)

    def test_natural_exit_while_job_still_open_does_not_prove_host_kill(self):
        go, _, _, api, _ = self.run_proof(hold_exit=True)
        with self.assertRaisesRegex(AssertionError, "EXITED_BEFORE_JOB_CLOSE"):
            go()
        self.assertFalse(api.closed_job)

    def test_no_native_exit_after_job_close_blocks(self):
        go, report, job, api, _ = self.run_proof(exit_after_close=False)
        with self.assertRaisesRegex(AssertionError, "DID_NOT_EXIT_AFTER_JOB_CLOSE"):
            go()
        self.assertTrue(api.closed_job)
        self.assertIsNone(job["handle"])
        self.assertEqual(report["unassistedGateBeforeHostClose"], "failed")


if __name__ == "__main__":
    unittest.main(verbosity=2)
