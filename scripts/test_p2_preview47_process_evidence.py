#!/usr/bin/env python3
"""Negative controls for P47 evidence persistence and unchanged failure gate."""
import ctypes
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock

import p2_preview47_one_time_real_chromium as p47
import p2_preview47_process_evidence as evidence


class FakeWin32:
    def __init__(self, wait=258, creation=12345678901234567, code=0, opened=True, times=True):
        self.wait = wait
        self.creation = creation
        self.code = code
        self.opened = opened
        self.times = times
        self.closed = []
        self.waited = 0

    def OpenProcess(self, rights, inherit, pid):
        assert rights == 0x00100000 | 0x1000  # No PROCESS_TERMINATE authority.
        return 99 if self.opened else 0

    def GetProcessTimes(self, handle, created, exited, kernel, user):
        created._obj.dwHighDateTime = self.creation >> 32
        created._obj.dwLowDateTime = self.creation & 0xffffffff
        return self.times

    def WaitForSingleObject(self, handle, timeout):
        assert timeout == 0
        self.waited += 1
        return self.wait

    def GetExitCodeProcess(self, handle, code):
        code._obj.value = self.code
        return True

    def CloseHandle(self, handle):
        self.closed.append(handle)


class NativeObserverTests(unittest.TestCase):
    def probe(self, **values):
        result = object.__new__(evidence.NativeWindowsProbe)
        result.api = FakeWin32(**values)
        return result

    def test_live_process_uses_wait_not_exit_code(self):
        probe = self.probe(wait=258, code=0)
        result = probe.inspect(80)
        self.assertEqual(result["state"], "live")
        self.assertEqual(result["creationFileTime"], "12345678901234567")
        self.assertEqual(probe.api.closed, [99])

    def test_signaled_process_is_exited_even_with_still_active_exit_code(self):
        probe = self.probe(wait=0, code=259)
        result = probe.inspect(80, str(probe.api.creation))
        self.assertEqual(result["state"], "exited")
        self.assertEqual(result["exitCode"], 259)
        self.assertEqual(probe.api.closed, [99])

    def test_reused_pid_is_not_inspected_as_original_process(self):
        probe = self.probe()
        result = probe.inspect(80, str(probe.api.creation - 1))
        self.assertEqual(result["state"], "identity_mismatch")
        self.assertEqual(probe.api.waited, 0)
        self.assertEqual(probe.api.closed, [99])

    def test_absent_and_access_denied_remain_different(self):
        for error, state in ((87, "absent"), (5, "unknown")):
            with self.subTest(error=error), mock.patch.object(ctypes, "get_last_error", return_value=error, create=True):
                probe = self.probe(opened=False)
                self.assertEqual(probe.inspect(80)["state"], state)
                self.assertEqual(probe.api.closed, [])

    def test_identity_failure_is_unknown_and_handle_is_closed(self):
        with mock.patch.object(ctypes, "get_last_error", return_value=5, create=True):
            probe = self.probe(times=False)
            self.assertEqual(probe.inspect(80)["state"], "unknown")
            self.assertEqual(probe.api.waited, 0)
            self.assertEqual(probe.api.closed, [99])

    def test_wait_failure_is_not_treated_as_cleanup(self):
        with mock.patch.object(ctypes, "get_last_error", return_value=6, create=True):
            probe = self.probe(wait=0xffffffff)
            self.assertEqual(probe.inspect(80)["state"], "unknown")
            self.assertEqual(probe.api.closed, [99])


class EvidenceTests(unittest.TestCase):
    def assert_manifest(self, directory):
        manifest = json.loads((directory / "SHA256SUMS.json").read_text())
        self.assertTrue(manifest)
        for name, digest in manifest.items():
            self.assertEqual(hashlib.sha256((directory / name).read_bytes()).hexdigest(), digest)

    def test_failure_summary_and_manifest_exist_before_assertion_returns(self):
        with tempfile.TemporaryDirectory() as folder:
            directory = Path(folder)
            audit = evidence.StageEvidence(directory, "disconnect_primary", "Failed", True)
            audit.event("passive_cleanup_observation", originalPresenceSurvivors=[{"pid": 40}])
            audit.report.update(originalPresenceGate="failed", survivingObservedProcessCount=1)
            with self.assertRaisesRegex(AssertionError, "SURVIVED"):
                audit.check(False, "SURVIVED")
            report = json.loads((directory / "failure-summary.json").read_text())
            failed = report["outcomes"]["disconnect_primary"]
            self.assertEqual(failed["status"], "failed")
            self.assertEqual(failed["realBrowserCloseException"], "not_observed")
            self.assertEqual(failed["realDisposeException"], "not_observed")
            self.assertEqual(failed["hostOwnedBrowserCleanup"], "not_attempted")
            self.assert_manifest(directory)

    def test_completed_stage_survives_later_failure(self):
        with tempfile.TemporaryDirectory() as folder:
            directory = Path(folder)
            first = evidence.StageEvidence(directory, "healthy_pass", "Passed", False)
            first.finish({"actual": "Passed"})
            failed = evidence.StageEvidence(directory, "disconnect_primary", "Failed", True)
            failed.fail("survivor")
            report = json.loads((directory / "failure-summary.json").read_text())
            self.assertEqual(report["outcomes"]["healthy_pass"]["status"], "passed")
            self.assertEqual(report["outcomes"]["disconnect_primary"]["status"], "failed")
            self.assert_manifest(directory)

    def test_native_unknown_and_pid_reuse_do_not_disappear_from_classification(self):
        keys = {(1, "a"), (2, "b"), (3, "c"), (4, "d")}
        native = {(1, "a"): {"state": "live"}, (2, "b"): {"state": "exited"},
                  (3, "c"): {"state": "identity_mismatch"}}
        result = evidence.classify_presence(keys, native)
        self.assertEqual([x["pid"] for x in result["live"]], [1])
        self.assertEqual([x["pid"] for x in result["exited"]], [2])
        self.assertEqual([x["pid"] for x in result["identity_mismatch"]], [3])
        self.assertEqual([x["pid"] for x in result["unknown"]], [4])

    def test_unbound_exited_object_cannot_be_retroactively_bound(self):
        native = mock.Mock()
        native.inspect.return_value = {"pid": 1, "state": "exited", "creationFileTime": "123"}
        with tempfile.TemporaryDirectory() as folder:
            audit = evidence.StageEvidence(folder, "control", "Passed", False, native=native)
            result = audit.observe_native(1, "old-cim-time", bind=True)
            self.assertEqual(result["state"], "unknown")
            self.assertEqual(result["unboundState"], "exited")
            self.assertEqual(audit.native_starts, {})

    def test_initial_binding_rejects_reused_pid_within_same_rounded_cim_second(self):
        native = mock.Mock()
        first = 12345678901234560
        native.inspect.return_value = {"pid": 1, "state": "live", "creationFileTime": str(first + 10000)}
        with tempfile.TemporaryDirectory() as folder:
            audit = evidence.StageEvidence(folder, "control", "Passed", False, native=native)
            result = audit.observe_native(1, "same-rounded-second", bind=True,
                cim_creation_utc=evidence.native_creation_utc(first).isoformat())
            self.assertEqual(result["state"], "unknown")
            self.assertEqual(result["identityBindingReason"], "cim_native_creation_not_joined")
            self.assertEqual(audit.native_starts, {})

    def test_join_uses_cim_microsecond_precision_and_then_keeps_exact_native_identity(self):
        native = mock.Mock()
        created = 12345678901234567
        native.inspect.return_value = {"pid": 1, "state": "live", "creationFileTime": str(created)}
        with tempfile.TemporaryDirectory() as folder:
            audit = evidence.StageEvidence(folder, "control", "Passed", False, native=native)
            result = audit.observe_native(1, "cim-start", bind=True,
                cim_creation_utc=evidence.native_creation_utc(created).isoformat())
            self.assertEqual(result["state"], "live")
            self.assertEqual(audit.native_starts[(1, "cim-start")], str(created))
            audit.observe_native(1, "cim-start")
            native.inspect.assert_called_with(1, str(created))


class OriginalGateIntegrationTests(unittest.TestCase):
    def run_case(self, survivor, native_state, exit_diagnostic_delay=0):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        directory = Path(folder.name)
        stage = "disconnect_primary_repeat"
        (directory / ("ready-" + stage)).write_text("ready")
        (directory / (stage + ".trx")).write_text(
            '<TestRun><UnitTestResult testName="Operation" outcome="Failed">'
            '<Output><ErrorInfo><Message>P47_PRIMARY_ASSERTION_MUST_SURVIVE</Message>'
            '</ErrorInfo></Output></UnitTestResult></TestRun>')
        active = {10: (0, "test-start", "other"), 20: (10, "driver-start", "driver"),
                  30: (20, "chrome-start", "chrome"), 40: (1, "unrelated-start", "chrome")}
        baseline = {40: active[40]}
        after = {40: active[40]}
        if survivor:
            after[30] = active[30]
        calls = {"snapshot": 0, "time": 0.0}

        def table(chrome):
            calls["snapshot"] += 1
            data = baseline if calls["snapshot"] == 1 else active if calls["snapshot"] == 2 else after
            return data, {pid: {"creationUtc": evidence.native_creation_utc(pid * 1000).isoformat(),
                               "chromiumRole": "utility" if pid == 30 else "root",
                               "chromiumUtilitySubtype": "network.mojom.NetworkService" if pid == 30 else None}
                          for pid in data}

        def inspect(pid, expected=None):
            state = "live" if calls["snapshot"] <= 2 else native_state if pid == 30 else "absent"
            return {"pid": pid, "state": state, "creationFileTime": str(pid * 1000)}

        def sleep(seconds):
            calls["time"] += seconds

        native = mock.Mock()
        native.inspect.side_effect = inspect
        real_audit = evidence.StageEvidence
        real_event = real_audit.event

        def event(audit, phase, **fields):
            if phase == "runner_exited":
                calls["time"] += exit_diagnostic_delay
            return real_event(audit, phase, **fields)

        process = mock.Mock(pid=10, returncode=1)
        process.poll.side_effect = [None, 1]
        process.communicate.return_value = ("", "")
        with mock.patch.object(real_audit, "event", new=event), \
             mock.patch.object(evidence, "StageEvidence", side_effect=lambda *args: real_audit(*args, native=native)), \
             mock.patch.object(evidence, "snapshot", side_effect=table), \
             mock.patch.object(p47.subprocess, "Popen", return_value=process), \
             mock.patch.object(p47.os, "kill") as kill, \
             mock.patch.object(p47.time, "monotonic", side_effect=lambda: calls["time"]), \
             mock.patch.object(p47.time, "sleep", side_effect=sleep):
            if survivor:
                with self.assertRaisesRegex(AssertionError, "P47_OWNED_OBSERVED_CHILD_PIDS_SURVIVED"):
                    p47.run_probe(stage, "Failed", True, directory, directory, "chrome", "local")
            else:
                p47.run_probe(stage, "Failed", True, directory, directory, "chrome", "local")
            kill.assert_called_once_with(20, p47.signal.SIGTERM)
        return json.loads((directory / ("process-" + stage + ".json")).read_text()), calls

    def test_exited_native_diagnostic_does_not_silently_weaken_original_presence_gate(self):
        report, calls = self.run_case(True, "exited")
        self.assertEqual(report["status"], "failed")
        self.assertEqual(report["originalPresenceGate"], "failed")
        self.assertEqual(len(report["nativeClassificationOfOriginalPresence"]["exited"]), 1)
        self.assertEqual(report["passiveWindowSeconds"], 12)
        self.assertGreaterEqual(calls["time"], 12)
        self.assertLess(calls["time"], 13)

    def test_diagnostic_work_consumes_existing_window_and_cannot_extend_it(self):
        report, calls = self.run_case(True, "live", exit_diagnostic_delay=5)
        self.assertEqual(report["status"], "failed")
        self.assertGreaterEqual(calls["time"], 12)
        self.assertLess(calls["time"], 13)

    def test_live_descendant_remains_failure_and_unrelated_process_is_not_owned(self):
        report, _ = self.run_case(True, "live")
        self.assertEqual(report["status"], "failed")
        self.assertEqual(len(report["nativeClassificationOfOriginalPresence"]["live"]), 1)
        self.assertEqual({x["observedPid"] for x in report["observedOwnedAncestry"]}, {20, 30})
        survivor = report["finalObservedProcesses"][1]
        self.assertEqual(survivor["chromiumRole"], "utility")
        self.assertEqual(survivor["chromiumUtilitySubtype"], "network.mojom.NetworkService")
        self.assertTrue(survivor["executableMatchesConfiguredChrome"])
        self.assertTrue(report["primaryTestFailurePreserved"])

    def test_original_success_remains_success_without_host_browser_cleanup(self):
        report, calls = self.run_case(False, "absent")
        self.assertEqual(report["status"], "passed")
        self.assertEqual(report["originalPresenceGate"], "passed")
        self.assertEqual(report["hostOwnedBrowserCleanup"], "not_attempted")
        self.assertLess(calls["time"], 1)


class FixedDiagnosticSeriesTests(unittest.TestCase):
    def test_default_zero_and_explicit_limit_prevent_unbounded_runs(self):
        with mock.patch.dict(p47.os.environ, {}, clear=True):
            self.assertEqual(p47.diagnostic_cycle_count(), 0)
        for raw in ("-1", "11", "invalid", "1.5"):
            with self.subTest(raw=raw), mock.patch.dict(p47.os.environ, {"P47_DIAGNOSTIC_CYCLES": raw}):
                with self.assertRaisesRegex(AssertionError, "MUST_BE_0_TO_10"):
                    p47.diagnostic_cycle_count()
        with mock.patch.dict(p47.os.environ, {"P47_DIAGNOSTIC_CYCLES": "10"}):
            self.assertEqual(p47.diagnostic_cycle_count(), 10)

    def test_fixed_series_isolated_and_first_failure_preserves_primary_and_hashes(self):
        for fail_at in (None, 8):
            with self.subTest(fail_at=fail_at), tempfile.TemporaryDirectory() as temporary:
                directory = Path(temporary)
                primary = b'{"originalFiveStages":"passed"}\n'
                (directory / "summary.json").write_bytes(primary)
                calls = []
                failure = RuntimeError("P47_TEST_FIRST_DIAGNOSTIC_FAILURE")

                def probe(stage, expected, disconnect, project, folder, chrome, url):
                    # The fixed sample plan and primary evidence exist before
                    # the first additional invocation, including on failure.
                    plan = json.loads((directory / "diagnostic-summary.json").read_text())
                    self.assertEqual(plan["requestedCycles"], 10)
                    self.assertEqual(plan["plannedProbes"], 50)
                    self.assertEqual((directory / "summary.json").read_bytes(), primary)
                    for name in ("ready-" + stage, "ack-" + stage, stage + ".trx"):
                        path = folder / name
                        self.assertFalse(path.exists(), "stale ready/ack/TRX reused")
                        path.write_text(folder.name + ":" + stage)
                    calls.append((folder.name, stage))
                    audit = evidence.StageEvidence(folder, stage, expected, disconnect)
                    if len(calls) == fail_at:
                        audit.fail(str(failure))
                        raise failure
                    result = {"actual": expected, "originalPresenceGate": "passed",
                              "survivingObservedProcessCount": 0}
                    audit.finish(result)
                    return result

                with mock.patch.object(p47, "run_probe", side_effect=probe):
                    if fail_at is not None:
                        with self.assertRaises(RuntimeError) as caught:
                            p47.run_diagnostic_cycles(10, directory, directory, "chrome", "local")
                        self.assertIs(caught.exception, failure)
                    else:
                        p47.run_diagnostic_cycles(10, directory, directory, "chrome", "local")
                planned = [("cycle-" + str(index).zfill(3), stage)
                           for index in range(1, 11) for stage, _, _ in p47.STAGES]
                expected_calls = fail_at or 50
                self.assertEqual(calls, planned[:expected_calls])
                self.assertEqual((directory / "summary.json").read_bytes(), primary)
                result = json.loads((directory / "diagnostic-summary.json").read_text())
                self.assertEqual(result["attemptedProbes"], expected_calls)
                self.assertEqual(result["completedProbes"], 7 if fail_at else 50)
                self.assertEqual(result["completedCycles"], 1 if fail_at else 10)
                self.assertEqual(result["status"], "failed" if fail_at else "passed")
                self.assertEqual(result["primarySummarySha256"], hashlib.sha256(primary).hexdigest())
                if fail_at:
                    self.assertEqual(result["failureCycle"], 2)
                    self.assertEqual(result["failureStage"], "disconnect_primary")
                    self.assertFalse((directory / "diagnostic-cycles/cycle-003").exists())
                    root_failure = json.loads((directory / "failure-summary.json").read_text())
                    self.assertEqual(root_failure["failureScope"], "diagnostic_cycles")
                    self.assertTrue((directory / "diagnostic-cycles/cycle-002/failure-summary.json").is_file())
                manifest = json.loads((directory / "SHA256SUMS.json").read_text())
                self.assertNotIn("SHA256SUMS.json", manifest)
                self.assertIn("diagnostic-cycles/cycle-001/SHA256SUMS.json", manifest)
                self.assertIn("diagnostic-cycles/cycle-002/ready-disconnect_primary", manifest)
                for name, digest in manifest.items():
                    self.assertEqual(hashlib.sha256((directory / name).read_bytes()).hexdigest(), digest)


if __name__ == "__main__":
    unittest.main()
