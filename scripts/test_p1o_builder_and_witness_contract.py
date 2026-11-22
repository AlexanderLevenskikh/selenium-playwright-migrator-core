"""Offline P1-O hard blockers: no fake native-live, no weakened P47 gate."""
import copy
import json
from pathlib import Path
import tempfile
import unittest

import p1o_builder_and_witness_contract as p

LOCK_FILE = Path(__file__).resolve().parent.parent / (
    "docs/p1o-chromium156-toolchain.lock.json")


def event(phase, seq, tick, pid=3436, filetime="134398240007207002",
          client=(8080, "134398240009020979"), count=-1,
          persistent=-1, upload=-1):
    with_client = phase in p.CLIENT_PHASES
    return {
        "schema": p.EVENT_SCHEMA,
        "phase": phase,
        "seq": seq,
        "tickMs": tick,
        "handlerPid": pid,
        "handlerFiletime": filetime,
        "clientPid": client[0] if with_client else 0,
        "clientFiletime": client[1] if with_client else "0",
        "registeredCount": count,
        "persistentMode": persistent,
        "uploadInFlight": upload,
    }


def test_events():
    return [
        event("initial_register", 1, 10, count=1),
        event("mode", 2, 11, count=1, persistent=0),
        event("upload_begin", 3, 12, upload=1),
        event("process_end_callback", 4, 14),
        event("completion_posted", 5, 15),
        event("completion_consumed", 6, 16, count=1),
        event("client_erased", 7, 17, count=0),
        event("upload_end", 8, 18, upload=0),
    ]


def gate():
    return {
        "schema": p.GATE_SCHEMA,
        "originalP47ScriptGitBlobSha1":
            "f0d44e21d40b2cbf16397bc400671f77232004db",
        "originalGateSeconds": 12,
        "originalNodeSigterm": True,
        "stage": "disconnect_primary",
        "failFast": True,
        "hostJobObjectUsed": False,
        "afterGateNativeLive": True,
        "handlerPid": 3436,
        "handlerFiletime": "134398240007207002",
        "gateTickMs": 16,
    }


def serialized(rows):
    return [json.dumps(e, separators=(",", ":")) for e in rows]


class P1OOfflineGates(unittest.TestCase):
    def setUp(self):
        self.lock = json.loads(LOCK_FILE.read_text(encoding="utf-8"))

    def test_pinned_contract_exact(self):
        result = p.validate_lock(self.lock)
        self.assertEqual(result["sourceCommit"],
                         "fb096c0011f9d50f0a264a9376420cdea9c421e3")
        self.assertFalse(result["fixClaim"])

    def test_reject_other_chromium_or_crashpad_revision(self):
        for key, mutated in (
            ("commit", "a" * 40),
            ("crashpadRevision", "b" * 40),
            ("tag", "157.0.8103.38"),
        ):
            bad = copy.deepcopy(self.lock)
            bad["source"][key] = mutated
            with self.assertRaisesRegex(p.EvidenceError, "SOURCE_DRIFT"):
                p.validate_lock(bad)

    def test_reject_floating_depot_tools_and_vscode(self):
        for field in ("depotTools", "visualStudio", "windowsSdk"):
            bad = copy.deepcopy(self.lock)
            if field == "depotTools":
                bad["builder"][field]["exactCommit"] = "f" * 40
            elif field == "visualStudio":
                bad["builder"][field]["version"] = "latest"
            else:
                bad["builder"][field]["kitVersion"] = "10.0.26100.0"
            with self.assertRaises(p.EvidenceError):
                p.validate_lock(bad)

    def test_reject_changed_twelve_second_gate_sigterm_or_failfast(self):
        for key, value in (
            ("originalGateSeconds", 30),
            ("originalFault", "driver.kill()"),
            ("firstFailStopsSequence", False),
            ("noRetries", False),
            ("noWin32HostJobSubstitution", False),
        ):
            bad = copy.deepcopy(self.lock)
            bad["p47"][key] = value
            with self.assertRaisesRegex(p.EvidenceError, "P47_GATE"):
                p.validate_lock(bad)

    def test_plain_linux_or_small_github_windows_classified_prereq(self):
        for facts in ({"os": "Linux"},
                      {"os": "Windows", "architecture": "AMD64",
                       "scratchFilesystem": "NTFS", "scratchFreeGiB": 14,
                       "ramGiB": 16}):
            response = p.assess_builder(self.lock, facts)
            self.assertEqual(response["status"], "prerequisite_failure")
            self.assertFalse(response["compiled"])
            self.assertFalse(response["fixClaim"])
        self.assertIn("SCRATCH_GIB_INSUFFICIENT", response["reasons"])

    def test_attested_owned_builder_can_only_pass_preflight(self):
        lock = self.lock
        facts = {
            "os": "Windows", "architecture": "AMD64",
            "scratchFilesystem": "NTFS",
            "scratchFreeGiB": 300, "ramGiB": 32,
            "visualStudioVersion": lock["builder"]["visualStudio"]["version"],
            "visualStudioBuild":
                lock["builder"]["visualStudio"]["microsoftReleaseBuild"],
            "windowsSdk": lock["builder"]["windowsSdk"]["kitVersion"],
            "depotToolsCommit":
                lock["builder"]["depotTools"]["exactCommit"],
            "chromiumCommit": lock["source"]["commit"],
            "crashpadRevision": lock["source"]["crashpadRevision"],
            "isolatedOwnedHost": True,
            "depotToolsAutoUpdate": False,
            "sourceTreeClean": True,
        }
        report = p.assess_builder(lock, facts)
        self.assertEqual(report["status"], "ready_for_isolated_compile")
        self.assertFalse(report["compiled"])
        self.assertFalse(report["releaseAllowed"])
        facts["depotToolsAutoUpdate"] = True
        self.assertEqual(p.assess_builder(lock, facts)["status"],
                         "prerequisite_failure")

    def test_absent_runtime_is_not_run(self):
        report = p.evaluate_witness(self.lock)
        self.assertEqual(report["status"], "not_run")
        self.assertFalse(report["fixClaim"])
        self.assertFalse(report["rootCauseProven"])

    def test_synthetic_private_trace_never_claims_product_fix(self):
        report = p.evaluate_witness(
            self.lock, serialized(test_events()), gate())
        self.assertEqual(report["status"],
                         "test_owned_runtime_correlation_candidate")
        evidence = report["directHandlerEvidence"]
        self.assertTrue(evidence["traceCoversGateTick"])
        self.assertEqual(evidence["persistentModeObserved"], 0)
        self.assertEqual(evidence["completionPostsObserved"], 1)
        self.assertEqual(evidence["completionConsumptionObserved"], 1)
        self.assertEqual(evidence["uploadAtGate"],
                         "in_flight_observed_on_test_owned_timeline")
        self.assertEqual(len(evidence["privateRegisteredClientsObservedByGate"]), 1)
        self.assertFalse(report["productFixProven"])
        self.assertFalse(report["rootCauseProven"])
        self.assertFalse(report["fixClaim"])

    def test_nonreproduction_is_not_fix(self):
        g = gate()
        g["afterGateNativeLive"] = False
        report = p.evaluate_witness(self.lock, serialized(test_events()), g)
        self.assertEqual(report["status"], "not_observed")
        self.assertFalse(report["releaseAllowed"])

    def test_pid_reuse_or_wrong_filetime_does_not_correlate(self):
        for value in ("134398240007207003", "1"):
            g = gate()
            g["handlerFiletime"] = value
            report = p.evaluate_witness(self.lock, serialized(test_events()), g)
            self.assertEqual(report["status"],
                             "handler_identity_not_correlated")
        g = gate()
        g["handlerPid"] = 9999
        self.assertEqual(p.evaluate_witness(
            self.lock, serialized(test_events()), g)["status"],
            "handler_identity_not_correlated")

    def test_missing_gate_receipt_does_not_promote_live(self):
        self.assertEqual(p.evaluate_witness(
            self.lock, serialized(test_events()))["status"], "not_run")
        for field, val in (("originalGateSeconds", 13),
                           ("originalNodeSigterm", False),
                           ("hostJobObjectUsed", True),
                           ("originalP47ScriptGitBlobSha1", "a" * 40),
                           ("failFast", False)):
            g = gate()
            g[field] = val
            with self.assertRaisesRegex(p.EvidenceError, "P47_ORIGINAL"):
                p.evaluate_witness(self.lock, serialized(test_events()), g)

    def test_missing_client_completion_keeps_unknown_not_false_negative(self):
        rows = test_events()[:3]
        inspected = p.analyze_handler(rows, 16)
        self.assertFalse(inspected["traceCoversGateTick"])
        self.assertEqual(inspected["uploadAtGate"], "unknown")
        result = p.evaluate_witness(self.lock, serialized(rows), gate())
        self.assertEqual(result["status"], "partial_handler_trace_unknown")

    def test_reject_secret_or_unexpected_field(self):
        row = test_events()
        row[1]["rawPipeName"] = "secret"
        with self.assertRaisesRegex(p.EvidenceError, "SENSITIVE"):
            p.parse_handler_events(serialized(row))

    def test_reject_nonmonotonic_sequence_and_missing_first_event(self):
        rows = test_events()
        rows[1]["seq"] = 4
        with self.assertRaisesRegex(p.EvidenceError, "MONOTONIC"):
            p.parse_handler_events(serialized(rows))
        rows = test_events()[1:]
        with self.assertRaisesRegex(p.EvidenceError, "FIRST_HANDLER"):
            p.parse_handler_events(serialized(rows))

    def test_registered_client_requires_native_filetime(self):
        rows = test_events()
        rows[0]["clientFiletime"] = "0"
        with self.assertRaisesRegex(p.EvidenceError, "CLIENT_NATIVE_IDENTITY"):
            p.parse_handler_events(serialized(rows))

    def test_512_record_cap_is_trace_unknown_not_proof(self):
        rows = test_events()
        rows[-1]["seq"] = 512
        with self.assertRaisesRegex(p.EvidenceError, "MONOTONIC"):
            p.parse_handler_events(serialized(rows))
        # Even a sequential but final 512th event cannot prove completeness.
        data = [event("mode", 1, 10, count=0, persistent=0)]
        data.extend(event("mode", n, n + 10, count=0, persistent=0)
                    for n in range(2, 513))
        status = p.analyze_handler(data, 1000)
        self.assertFalse(status["traceCoversGateTick"])

    def test_original_historical_failures_remain_pinned(self):
        self.assertEqual(self.lock["p47"]["historicalNativeLiveArtifacts"],
                         [14745840024, 14753824405, 14754039147])
        self.assertEqual(self.lock["p47"]["originalGateSeconds"], 12)


if __name__ == "__main__":
    unittest.main()
