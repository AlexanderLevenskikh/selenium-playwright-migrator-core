"""P1-L portable fictional controls. Never launches Chromium, Node or NUnit."""
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

import p1l_crashpad_client_boundary as audit


def sha(data):
    return hashlib.sha256(data).hexdigest()


def fixture_archive(original, *, fail=True, corruption=None):
    driver = {"pid": 90, "startIdentity": "node-start", "kind": "driver",
              "native": {"creationFileTime": "1000", "state": "live"}}
    root = {"pid": 91, "startIdentity": "root-start", "kind": "chrome",
            "chromiumRole": "root",
            "native": {"creationFileTime": "2000", "state": "live"}}
    crashpad = {"pid": 92, "startIdentity": "handler-start", "kind": "chrome",
                "chromiumRole": "crashpad-handler",
                "native": {"creationFileTime": "3000", "state": "live"}}
    ledger = [
        {"observedPid": 91, "startIdentity": "root-start",
         "creationFileTime": "2000",
         "wasInBaselineWithOriginalIdentity": False,
         "pathToTestProcess": [root, driver]},
        {"observedPid": 92, "startIdentity": "handler-start",
         "creationFileTime": "3000",
         "wasInBaselineWithOriginalIdentity": False,
         "pathToTestProcess": [crashpad, root, driver]}
    ]
    receipt = {
        "run": "disconnect_primary",
        "originalPresenceGate": "failed" if fail else "passed",
        "failure": ("P47_OWNED_OBSERVED_CHILD_PIDS_SURVIVED: [(92,...)]"
                    if fail else ""),
        "hostOwnedBrowserCleanup": "not_attempted",
        "driverTermination": {
            "pid": 90, "mechanism": "os.kill(SIGTERM)", "signalSent": True},
        "observedOwnedAncestry": ledger,
        "originalPresenceSurvivors": (
            [{"pid": 92, "startIdentity": "handler-start"}] if fail else []),
        "finalObservedProcesses": [
            {"pid": 91, "startIdentity": "root-start",
             "native": {"creationFileTime": "2000",
                        "expectedCreationFileTime": "2000",
                        "state": "exited", "waitResult": 0}},
            {"pid": 92, "startIdentity": "handler-start",
             "kind": "chrome", "chromiumRole": "crashpad-handler",
             "executableMatchesConfiguredChrome": True, "parentPid": 91,
             "crashpadSwitches": {"initialClientData": "present",
                                  "uploadUrlConfigured": "absent"},
             "native": {"creationFileTime": "3000",
                        "expectedCreationFileTime": "3000",
                        "state": "live", "waitResult": 258}}
        ]}
    trx = (b'<TestRun><UnitTestResult testName="Operation" '
           b'outcome="Failed"/></TestRun>')
    receipt["trxSha256"] = sha(trx)
    if corruption == "pid":
        receipt["finalObservedProcesses"][1]["native"]["creationFileTime"] = "9999"
    if corruption == "root":
        receipt["finalObservedProcesses"][0]["native"]["state"] = "unknown"
    base = "diagnostic-cycles/cycle-001/"
    events = [
        {"phase": "runner_exited", "utc": "2026-11-22T01:00:00+00:00"},
        {"phase": "passive_cleanup_observation",
         "utc": "2026-11-22T01:00:12.800000+00:00"}]
    entries = {
        base + "process-disconnect_primary.json": json.dumps(receipt).encode(),
        base + "process-disconnect_primary.jsonl": (
            "\n".join(json.dumps(e) for e in events) + "\n").encode(),
        base + "disconnect_primary.trx": trx}
    child = {k[len(base):]: sha(v) for k, v in entries.items()}
    entries[base + "SHA256SUMS.json"] = json.dumps(child).encode()
    manifest = {k: sha(v) for k, v in entries.items()}
    if corruption == "manifest":
        manifest[base + "disconnect_primary.trx"] = "0" * 64
    entries["SHA256SUMS.json"] = json.dumps(manifest).encode()
    with zipfile.ZipFile(original, "w", zipfile.ZIP_DEFLATED) as archive:
        for name, data in entries.items():
            archive.writestr(name, data)
    return sha(original.read_bytes())


class P1LControls(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.archive = Path(self.temp.name) / "fictional-original.zip"

    def test_native_live_and_root_exited_still_not_registration_proof(self):
        digest = fixture_archive(self.archive)
        result = audit.inspect_archive(self.archive, digest)
        self.assertEqual(result["status"], "native_live_failure_observed")
        failure = result["originalFailFastFailure"]
        self.assertTrue(failure["rootNativeExited"])
        self.assertEqual(failure["sourceBoundNativeLiveCrashpads"], 1)
        self.assertEqual(failure["survivors"][0]["uploadUrlConfigured"], "absent")
        self.assertEqual(failure["otherSourceBoundChromeLiveAtGate"], 0)
        for key in ("registeredClientIdentityProven",
                    "absenceOfOtherRegisteredClientsProven",
                    "rootCauseProven", "productFixProven"):
            self.assertFalse(failure[key])
        for key in ("registeredClientIdentityProven",
                    "fixClaim", "postgateJobCloseProven"):
            self.assertFalse(result[key])

    def test_no_reproduction_does_not_establish_fix(self):
        digest = fixture_archive(self.archive, fail=False)
        result = audit.inspect_archive(self.archive, digest)
        self.assertEqual(result["status"],
                         "no_original_native_live_failure_attested")
        self.assertIsNone(result["originalFailFastFailure"])
        self.assertFalse(result["fixClaim"])

    def test_original_outer_sha_mismatch_rejected(self):
        digest = fixture_archive(self.archive)
        with self.assertRaisesRegex(audit.Unverified,
                                    "PINNED_ORIGINAL_ZIP_SHA"):
            audit.inspect_archive(self.archive, "f" * 64)
        self.assertEqual(len(digest), 64)

    def test_manifest_tampering_fails_closed(self):
        digest = fixture_archive(self.archive, corruption="manifest")
        with self.assertRaisesRegex(audit.Unverified,
                                    "MANIFEST_DIGEST_MISMATCH"):
            audit.inspect_archive(self.archive, digest)

    def test_pid_reuse_or_unproven_root_rejected(self):
        for mode, code in (
            ("pid", "ORIGINAL_SURVIVOR_NOT_NATIVE_LIVE_EXACT_IDENTITY"),
            ("root", "CHROMIUM_ROOT_NATIVE_EXIT_UNPROVEN")
        ):
            with self.subTest(mode=mode):
                digest = fixture_archive(self.archive, corruption=mode)
                with self.assertRaisesRegex(audit.Unverified, code):
                    audit.inspect_archive(self.archive, digest)

    def test_no_private_pipe_or_client_registration_values_returned(self):
        digest = fixture_archive(self.archive)
        report = json.dumps(audit.inspect_archive(self.archive, digest))
        for secret_key in ("commandLine", "pipeValue", "clientHandle",
                           "dumpPath", "crashReportUrl"):
            self.assertNotIn(secret_key, report)


if __name__ == "__main__":
    unittest.main()
