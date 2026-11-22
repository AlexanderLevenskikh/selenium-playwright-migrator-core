"""P1-K pure portable negative tests; does not execute browser or source P47."""
import json
import tempfile
import unittest
from pathlib import Path
import p2_preview47_process_evidence as evidence
import p1k_preview47_activity_summary as summary


class CounterApi:
    def __init__(self, handles=True, io=True):
        self.handles, self.io = handles, io

    def GetProcessHandleCount(self, handle, ptr):
        if not self.handles:
            return 0
        ptr._obj.value = 19
        return 1

    def GetProcessIoCounters(self, handle, ptr):
        if not self.io:
            return 0
        v = ptr._obj
        v.ReadOperationCount, v.WriteOperationCount, v.OtherOperationCount = 4, 2, 3
        v.ReadTransferCount, v.WriteTransferCount, v.OtherTransferCount = 4096, 512, 128
        return 1


def counters(handles=True, io=True):
    k, u = evidence.wintypes.FILETIME(), evidence.wintypes.FILETIME()
    k.dwLowDateTime, u.dwLowDateTime = 800, 1200
    return evidence.p1k_read_live_activity(CounterApi(handles, io), 1, k, u)


def event(phase, elapsed=1, pid=80, ft="134398242040452709", cpu=2000, count=19):
    return {"phase": phase, "elapsedSec": elapsed, "processes": [{
        "pid": pid, "startIdentity": "exact-cim", "kind": "chrome",
        "chromiumRole": "crashpad-handler",
        "crashpadSwitches": {"initialClientData": "present", "pipeName": "absent",
                             "uploadUrlConfigured": "absent"},
        "native": {"state": "live", "creationFileTime": ft,
                   "expectedCreationFileTime": ft,
                   "activityCounters": {
                       "status": "observed", "cpuKernel100ns": cpu,
                       "cpuUser100ns": 10, "handleCount": count,
                       "ioReadOperations": 10, "ioWriteOperations": 5,
                       "ioOtherOperations": 0, "ioReadBytes": cpu,
                       "ioWriteBytes": 1, "ioOtherBytes": 0}}}]}


def source():
    return {"originalPresenceGate": "failed", "observedOwnedAncestry": [{
        "observedPid": 80, "startIdentity": "exact-cim",
        "creationFileTime": "134398242040452709",
        "wasInBaselineWithOriginalIdentity": False}]}


class P1KControls(unittest.TestCase):
    def test_counters_no_client_or_upload_proof(self):
        a = counters()
        self.assertEqual((a["status"], a["handleCount"], a["ioReadBytes"]),
                         ("observed", 19, 4096))
        self.assertEqual(a["cpuUser100ns"], 1200)
        for flag in ("containsHandleNames", "containsPaths",
                     "containsNetworkDestinations", "pendingUploadProven",
                     "registeredClientsProven"):
            self.assertFalse(a[flag])

    def test_missing_one_or_both_queries_is_not_full_evidence(self):
        self.assertEqual(counters(io=False)["status"], "partial")
        self.assertEqual(counters(handles=False)["status"], "partial")
        self.assertEqual(counters(handles=False, io=False)["status"], "unknown")

    def test_scope_excludes_node_runner_root_and_renderer(self):
        chooser = evidence.p1k_target_helper
        self.assertFalse(chooser("driver", {"chromiumRole": "crashpad-handler"}))
        self.assertFalse(chooser("other", {"chromiumRole": "crashpad-handler"}))
        self.assertFalse(chooser("chrome", {"chromiumRole": "root"}))
        self.assertFalse(chooser("chrome", {"chromiumRole": "renderer"}))
        self.assertTrue(chooser("chrome", {"chromiumRole": "crashpad-handler"}))
        self.assertTrue(chooser("chrome", {
            "chromiumRole": "utility",
            "chromiumUtilitySubtype": "storage.mojom.StorageService"}))
        self.assertFalse(chooser("chrome", {"chromiumRole": "utility",
                                           "chromiumUtilitySubtype": "network.mojom.NetworkService"}))

    def test_new_url_presence_is_allowlisted_and_legacy_unknown(self):
        text = ("80|30|11/22/2026|chrome|2026-11-22Z|crashpad-handler||"
                "present|absent|absent|absent|present")
        self.assertEqual(evidence.parse_sanitized_cim_record(text)[2]
                         ["crashpadSwitches"]["uploadUrlConfigured"], "present")
        self.assertEqual(evidence.parse_sanitized_cim_record(text.rsplit("|", 1)[0])[2]
                         ["crashpadSwitches"]["uploadUrlConfigured"], "unknown")
        self.assertIsNone(evidence.parse_sanitized_cim_record(text + "|SECRET"))
        self.assertIsNone(evidence.parse_sanitized_cim_record(
            text.replace("|absent|present", "|absent|https://private.invalid/id")))
        self.assertEqual(evidence.parse_sanitized_cim_record(
            "90|10|date|driver|utc|||not_applicable|not_applicable|not_applicable|not_applicable"
        )[1][2], "driver")

    def test_exact_source_pid_filetime_and_counter_deltas(self):
        events = [event("runner_active"), {"phase": "driver_termination_before_signal"},
                  {"phase": "runner_exited"},
                  event("passive_cleanup_observation", 14, cpu=3500, count=17)]
        observed = summary.summarize_stage(source(), events)
        self.assertEqual(len(observed), 1)
        self.assertEqual(observed[0]["status"], "source_attested_live_delta_observed")
        self.assertEqual(observed[0]["nativeCounterDeltas"]["handleCount"]["delta"], -2)
        self.assertEqual(observed[0]["nativeCounterDeltas"]["cpuKernel100ns"]["delta"], 1500)
        self.assertFalse(observed[0]["registeredClientsProven"])
        self.assertFalse(observed[0]["pendingUploadProven"])
        events[3]["processes"][0]["native"]["creationFileTime"] = "other"
        self.assertNotEqual(summary.summarize_stage(source(), events)[0]["status"],
                            "source_attested_live_delta_observed")

    def test_no_prefault_or_decreasing_cpu_cannot_be_proof(self):
        events = [{"phase": "driver_termination_before_signal"},
                  {"phase": "runner_exited"}, event("passive_cleanup_observation")]
        self.assertEqual(summary.summarize_stage(source(), events)[0]["status"],
                         "source_attested_but_not_comparable")
        events.insert(0, event("runner_active", cpu=5000))
        self.assertEqual(summary.summarize_stage(source(), events)[0]["status"],
                         "source_attested_but_not_comparable")

    def test_offline_summary_never_mutates_original_files(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)/"original"
            root.mkdir()
            path = root/"process-disconnect_primary"
            path.with_suffix(".json").write_text(json.dumps(source()), encoding="utf-8")
            events = [event("runner_active"), {"phase": "driver_termination_before_signal"},
                      {"phase": "runner_exited"}, event("passive_cleanup_observation")]
            path.with_suffix(".jsonl").write_text("\n".join(json.dumps(e) for e in events)+"\n")
            original = path.with_suffix(".jsonl").read_bytes()
            result = summary.summarize(root)
            self.assertEqual(result["status"], "observed_diagnostic_only")
            self.assertEqual(path.with_suffix(".jsonl").read_bytes(), original)
            self.assertFalse(result["fixClaim"])
            self.assertFalse(result["pendingCrashUploadProven"])


if __name__ == "__main__":
    unittest.main()
