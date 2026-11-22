#!/usr/bin/env python3
"""Public source-exact P47 regression and negative controls on Linux/Windows.

No browser is started here. Real Windows Job ownership is tested separately.
"""
import hashlib
from pathlib import Path
import unittest
from unittest import mock

import p1_preview47_source_exact_job_probe as probe
import p2_preview42_fixture_lifecycle_real_chromium as p42

REPO = Path(__file__).resolve().parent.parent


class SourceExactP47FixtureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.original, cls.host, cls.proof = probe.source_exact_fixture(REPO)

    def test_only_pre_create_job_gate_can_change_original_p47(self):
        self.assertTrue(self.proof["byteExactExceptPreCreateGate"])
        self.assertEqual(self.host.replace(probe.PRE_CREATE_GATE, "", 1),
                         self.original)
        self.assertEqual(self.host.count(probe.PRE_CREATE_GATE), 1)

    def test_real_original_one_time_teardown_byte_exact(self):
        src = (REPO / "Migrator.Application.Smoke/PomPreview47OneTimeCleanupSmoke.cs"
               ).read_text(encoding="utf-8")
        method = p42.synthetic("OneTimeTearDown", src)
        self.assertEqual(self.original.count(method), 1)
        self.assertEqual(self.host.count(method), 1)
        self.assertIn("await Browser.CloseAsync()", method)
        self.assertIn("PW.Dispose()", method)
        self.assertEqual(self.proof["oneTimeTearDownSha256"],
                         hashlib.sha256(method.encode("utf-8")).hexdigest())

    def test_real_original_typed_teardown_byte_exact(self):
        src = (REPO / "Migrator.Application.Smoke/PomPreview46TypedTargetClosedSmoke.cs"
               ).read_text(encoding="utf-8")
        method = p42.synthetic("TypedTearDown", src)
        self.assertEqual(self.original.count(method), 1)
        self.assertEqual(self.host.count(method), 1)
        self.assertIn("await Context.CloseAsync()", method)
        self.assertEqual(self.proof["tearDownSha256"],
                         hashlib.sha256(method.encode("utf-8")).hexdigest())

    def test_no_original_nunit_test_assertion_or_driver_ack_removed(self):
        self.assertEqual(self.host[self.host.index("[Test]"):],
                         self.original[self.original.index("[Test]"):])
        self.assertIn("P47_PRIMARY_ASSERTION_MUST_SURVIVE", self.host)
        self.assertIn("P47_REAL_DRIVER_KILL_NOT_CONFIRMED", self.host)
        self.assertIn("window.snapshot()", self.host)
        self.assertIn("await Page.GotoAsync(", self.host)
        self.assertNotIn("SetContentAsync", self.host)

    def test_job_gate_is_before_any_playwright_node_spawn(self):
        self.assertLess(self.host.index('Path.Combine(DirectoryPath,"testhost-gate")'),
                        self.host.index("PW=await Microsoft.Playwright.Playwright.CreateAsync()"))
        self.assertIn('Path.Combine(DirectoryPath,"job-assigned")', self.host)
        self.assertEqual(self.original.count("Playwright.CreateAsync()"), 1)

    def test_source_proof_exact_hashes_not_claimed_runtime(self):
        self.assertEqual(
            self.proof["originalRuntimeFixtureSha256"],
            hashlib.sha256(self.original.encode("utf-8")).hexdigest())
        self.assertEqual(
            self.proof["instrumentedFixtureSha256"],
            hashlib.sha256(self.host.encode("utf-8")).hexdigest())
        self.assertTrue(self.proof["noSyntheticExceptionInjection"])

    def test_only_disconnected_original_p47_stage_names_allowed(self):
        self.assertEqual(probe.expected_nunit("disconnect_primary"), "Failed")
        self.assertEqual(probe.expected_nunit("disconnect_primary_repeat"), "Failed")
        self.assertEqual(probe.expected_nunit("disconnect_pass"), "Passed")
        for name in ("healthy_pass", "missing", "disconnect_sink_primary_unproven"):
            with self.assertRaisesRegex(AssertionError, "STAGE_NOT_IN_ORIGINAL"):
                probe.expected_nunit(name)

    def test_missing_one_time_source_placeholder_fails_closed(self):
        with mock.patch.object(probe.p47, "TEMPLATE", probe.p47.TEMPLATE.replace(
                "__ONE_TIME_SOURCE__", "not-a-real-teardown", 1)):
            with self.assertRaisesRegex(AssertionError, "PLACEHOLDER_MISMATCH"):
                probe.source_exact_fixture(REPO)

    def test_duplicate_playwright_create_anchor_fails_closed(self):
        anchor = "        PW=await Microsoft.Playwright.Playwright.CreateAsync();"
        with mock.patch.object(probe.p47, "TEMPLATE", probe.p47.TEMPLATE.replace(
                anchor, anchor + "\n" + anchor, 1)):
            with self.assertRaisesRegex(AssertionError, "CREATE_ANCHOR_CHANGED"):
                probe.source_exact_fixture(REPO)


    def test_post_fault_new_owned_descendant_is_in_unassisted_inventory(self):
        # Node 2 / Chrome root 3 were observed before the real SIGTERM.
        # A new child 4 appears while the NUnit test runner is still alive.
        data = {1: (0, "runner", "other"),
                2: (1, "node", "driver"),
                3: (2, "chrome-root", "chrome"),
                4: (3, "late-child", "chrome")}
        observed = {(2, "node"): "driver", (3, "chrome-root"): "chrome"}
        new = probe.record_owned_snapshot(
            data, 1, observed, set(), "runner_after_node_fault")
        self.assertEqual(new, [{"pid": 4, "startIdentity": "late-child",
                                "kind": "chrome",
                                "firstObservedPhase": "runner_after_node_fault"}])
        self.assertEqual(observed[(4, "late-child")], "chrome")
        self.assertEqual(probe.record_owned_snapshot(
            data, 1, observed, set(), "runner_exit"), [])

    def test_reused_baseline_pid_with_same_start_is_blocked(self):
        data = {1: (0, "runner", "other"), 3: (1, "chrome", "chrome")}
        with self.assertRaisesRegex(AssertionError, "BASELINE_REUSED"):
            probe.record_owned_snapshot(
                data, 1, {}, {(3, "chrome")}, "runner_after_node_fault")

    def test_gate_uses_existing_deadline_not_extended_after_trx(self):
        import tempfile
        from unittest import mock
        seen = {(7, "chrome-native-start"): "chrome"}
        last = {7: (1, "chrome-native-start", "chrome")}
        report = {}
        with tempfile.TemporaryDirectory() as tmp:
            with mock.patch.object(probe.p47e, "snapshot", return_value=(last, {})):
                with mock.patch.object(probe.time, "monotonic", return_value=30.0):
                    with mock.patch.object(probe.time, "sleep") as sleep:
                        still_live = probe.unassisted_gate(
                            "fake-chrome", seen, report, Path(tmp), limit=12.0)
                        sleep.assert_not_called()
        self.assertEqual(len(still_live), 1)
        self.assertEqual(still_live[0]["pid"], 7)
        self.assertEqual(report["unassistedGateBeforeHostClose"], "failed")
        self.assertEqual(report["unassistedGateSeconds"], 12)


if __name__ == "__main__":
    unittest.main(verbosity=2)
