"""Fail-closed portable tests for P1-F Win32-to-original timeline joins."""
import unittest
from pathlib import Path
import tempfile
from unittest.mock import patch
from p1_preview47_native_tree import owned_chain, join_prefault


def event(t, phase, processes=None):
    return {"utc": t, "phase": phase, "processes": processes or []}


class P1FNativeJoinTests(unittest.TestCase):
    def setUp(self):
        self.samples = [{"pid": 40, "kind": "driver", "creationFileTime": "100",
                         "nativeState": "live", "checkedUtc": "2026-11-22T01:00:00+00:00",
                         "inHostJob": True, "pathToWrapper": [{"pid": 10}, {"pid": 40}]},
                        {"pid": 50, "kind": "chrome", "creationFileTime": "101",
                         "nativeState": "live", "checkedUtc": "2026-11-22T01:00:00+00:00",
                         "inHostJob": True, "pathToWrapper": [{"pid": 10}, {"pid": 40}, {"pid": 50}]}]
        rows = [{"pid": s["pid"], "kind": s["kind"], "startIdentity": "start-"+str(s["pid"]),
                 "presentWithOriginalIdentity": True,
                 "native": {"state": "live", "expectedCreationFileTime": s["creationFileTime"]}}
                for s in self.samples]
        self.events = [event("2026-11-22T01:00:01+00:00", "runner_active", rows),
                       event("2026-11-22T01:00:02+00:00", "driver_termination_before_signal")]

    def test_joins_both_native_liveness_and_job(self):
        actual = join_prefault(self.samples, self.events)
        self.assertEqual(set(actual), {(40, "start-40"), (50, "start-50")})
        self.assertTrue(all(v["inHostJob"] for v in actual.values()))

    def test_late_sample_cannot_prove_prefault(self):
        self.samples[1]["checkedUtc"] = "2026-11-22T01:00:03+00:00"
        self.assertEqual(set(join_prefault(self.samples, self.events)), {(40, "start-40")})

    def test_pid_reuse_filetime_rejected(self):
        self.samples[1]["creationFileTime"] = "999"
        self.assertEqual(set(join_prefault(self.samples, self.events)), {(40, "start-40")})

    def test_non_original_child_rejected(self):
        self.events[0]["processes"][1]["presentWithOriginalIdentity"] = False
        self.assertEqual(set(join_prefault(self.samples, self.events)), {(40, "start-40")})

    def test_missing_fault_is_unknown(self):
        self.assertEqual(join_prefault(self.samples, self.events[:1]), {})

    def test_active_stage_never_opens_original_atomic_json(self):
        from p1_preview47_native_tree import NativeTreeObserver
        with tempfile.TemporaryDirectory() as directory:
            Path(directory, "process-disconnect_primary.json").write_text("incomplete-json")
            observer = NativeTreeObserver.__new__(NativeTreeObserver)
            observer.source = Path(directory)
            observer.stages = (("disconnect_primary", "Failed", True),
                               ("disconnect_pass", "Passed", True))
            # Opening the file would interfere with os.replace on Windows.
            with patch.object(Path, "read_text", side_effect=AssertionError("read forbidden")):
                self.assertEqual(observer.active_stage(), "disconnect_primary")

    def test_no_pid_only_orphan_ancestry(self):
        table = {10: (1, "python.exe"), 20: (10, "dotnet.exe"), 30: (20, "node.exe")}
        self.assertEqual(owned_chain(table, 30, 10), [30, 20, 10])
        self.assertIsNone(owned_chain({30: (20, "node.exe")}, 30, 10))
        self.assertIsNone(owned_chain({10: (1, "python.exe"), 20: (30, "node.exe"),
                                       30: (20, "chrome.exe")}, 30, 10))


if __name__ == "__main__":
    unittest.main()
