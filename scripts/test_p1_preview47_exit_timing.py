"""Portable fail-closed source/native join tests for the P1-H observation experiment."""
import json
from pathlib import Path
import tempfile
import unittest

from p1_preview47_exit_timing import duration_ms, filetime_utc, source_lifetime_join

STAGES = (("healthy_pass", "Passed", False),
          ("disconnect_primary", "Failed", True))
MARKER = "2026-11-22T00:00:02.000000+00:00"
RUNNER = "2026-11-22T00:00:03.000000+00:00"


class P1HNegativeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)
        events = [
            {"phase": "runner_active", "utc": "2026-11-22T00:00:01+00:00",
             "processes": [{"pid": 40, "kind": "chrome", "chromiumRole": "crashpad-handler",
                            "presentWithOriginalIdentity": True,
                            "native": {"state": "live", "expectedCreationFileTime": "134398073839406434"}}]},
            {"phase": "driver_termination_before_signal", "utc": MARKER},
            {"phase": "runner_exited", "utc": RUNNER},
        ]
        (self.directory / "process-disconnect_primary.jsonl").write_text(
            "\n".join(json.dumps(e) for e in events) + "\n")
        (self.directory / "process-disconnect_primary.json").write_text(json.dumps(
            {"status": "failed", "originalPresenceGate": "failed"}))
        self.t = [{"stage": "disconnect_primary", "pid": 40, "kind": "chrome",
                   "creationFileTime": "134398073839406434",
                   "nativeStartUtc": "2026-11-22T00:00:00+00:00",
                   "inHostJobAtFirstLive": True,
                   "firstNativeLiveUtc": "2026-11-22T00:00:01.100000+00:00",
                   "lastNativeLiveUtc": "2026-11-22T00:00:02.500000+00:00",
                   "firstNativeExitUtc": "2026-11-22T00:00:02.750000+00:00",
                   "nativeAnomaly": None}]

    def joined(self):
        return source_lifetime_join(self.t, STAGES, self.directory)[0]

    def test_source_native_filetime_and_exact_chromium_role(self):
        row = self.joined()
        self.assertEqual(row["originalGate"], "failed")
        self.assertEqual(row["nativeTracks"][0]["chromiumRole"], "crashpad-handler")
        self.assertTrue(row["nativeTracks"][0]["sourceLinked"])
        self.assertEqual(row["nativeTracks"][0]["faultToNativeExitMilliseconds"], 750)
        self.assertEqual(row["nativeTracks"][0]["timingClassification"],
                         "native_exit_after_fault_before_runner_exit")

    def test_identity_reuse_refused(self):
        self.t[0]["creationFileTime"] = "134398073839406435"
        row = self.joined()["nativeTracks"][0]
        self.assertFalse(row["sourceLinked"])
        self.assertEqual(row["timingClassification"], "source_or_prefault_identity_unattested")

    def test_post_fault_first_observation_not_prefault(self):
        self.t[0]["firstNativeLiveUtc"] = "2026-11-22T00:00:02.100000+00:00"
        self.assertEqual(self.joined()["nativeTracks"][0]["timingClassification"],
                         "source_or_prefault_identity_unattested")

    def test_late_chrome_exit_after_runner_is_distinct(self):
        self.t[0]["firstNativeExitUtc"] = "2026-11-22T00:00:04+00:00"
        self.assertEqual(self.joined()["nativeTracks"][0]["timingClassification"],
                         "native_exit_at_or_after_runner_exit")

    def test_non_exit_live_after_runner_only_when_observed(self):
        self.t[0]["firstNativeExitUtc"] = None
        self.t[0]["lastNativeLiveUtc"] = "2026-11-22T00:00:03.001+00:00"
        self.assertEqual(self.joined()["nativeTracks"][0]["timingClassification"],
                         "native_live_at_or_after_runner_exit")
        self.t[0]["lastNativeLiveUtc"] = "2026-11-22T00:00:02.5+00:00"
        self.assertEqual(self.joined()["nativeTracks"][0]["timingClassification"],
                         "exit_timing_unknown")

    def test_missing_fault_or_runner_is_unknown_not_exited(self):
        p = self.directory / "process-disconnect_primary.jsonl"
        events = [json.loads(x) for x in p.read_text().splitlines()]
        p.write_text("\n".join(json.dumps(e) for e in (events[0], events[2])))
        self.assertEqual(self.joined()["nativeTracks"][0]["timingClassification"],
                         "source_or_prefault_identity_unattested")

    def test_filetime_is_absolute_windows_utc(self):
        self.assertTrue(filetime_utc("134398073839406434").startswith("2026-11-22"))
        self.assertEqual(duration_ms("2026-11-22T00:00:02+00:00",
                                     "2026-11-22T00:00:03.100+00:00"), 1100)


if __name__ == "__main__":
    unittest.main()
