"""P1-J evidence checking is portable; tests never launch Chrome or Node."""
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

import p1_preview47_j_offline as audit


class P1JOfflineContract(unittest.TestCase):
    def _archive(self, path, entries):
        manifest = {k: hashlib.sha256(v).hexdigest() for k, v in entries.items()}
        with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
            for key, value in entries.items():
                z.writestr(key, value)
            z.writestr("SHA256SUMS.json", json.dumps(manifest))
        return hashlib.sha256(path.read_bytes()).hexdigest()

    def test_complete_zip_crc_and_manifest_are_required(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder, "input.zip")
            correct = self._archive(path, {"source.trx": b"nunit", "process.json": b"{}"})
            self.assertEqual(audit.checked_zip(path, correct), 3)
            with self.assertRaisesRegex(audit.EvidenceError, "ORIGINAL_ZIP_SHA_MISMATCH"):
                audit.checked_zip(path, "0" * 64)
            with zipfile.ZipFile(path, "w") as z:
                z.writestr("source.trx", b"altered")
                z.writestr("SHA256SUMS.json", '{"source.trx":"' + "0" * 64 + '"}')
            bad = hashlib.sha256(path.read_bytes()).hexdigest()
            with self.assertRaisesRegex(audit.EvidenceError, "MEMBER_HASH_MISMATCH"):
                audit.checked_zip(path, bad)

    def test_zip_path_traversal_is_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder, "input.zip")
            digest = self._archive(path, {"../escape": b"no"})
            with self.assertRaisesRegex(audit.EvidenceError, "UNSAFE_ZIP_MEMBER"):
                audit.checked_zip(path, digest)

    def test_no_real_original_gate_failure_never_qualifies(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder, "input.zip")
            digest = self._archive(path, {"summary.json": b'{"status":"pass"}'})
            with self.assertRaisesRegex(audit.EvidenceError, "ORIGINAL_FAIL_FAST_FAILURE_REQUIRED"):
                audit.inspect_original(path, digest)

    def test_no_independent_host_b_blocks_comparison(self):
        with self.assertRaisesRegex(audit.EvidenceError, "INDEPENDENT_CONTROL_CLASSES_REQUIRED"):
            audit.compare({"A": {}}, {})
        with self.assertRaisesRegex(audit.EvidenceError, "INDEPENDENT_CONTROL_CLASSES_REQUIRED"):
            audit.compare({}, {"B": {}})

    def test_valid_comparison_never_proves_causation_or_fix(self):
        report = audit.compare({"A": {"originalNativeLiveFailure": True}},
                               {"B": {"nativeTracked": 27}})
        self.assertEqual(report["schema"], audit.SCHEMA)
        self.assertTrue(report["status"].startswith("root_exited"))
        self.assertFalse(report["rootCauseProven"])
        self.assertFalse(report["productCleanupProven"])
        self.assertFalse(report["postGateHostRemediationProven"])
        self.assertFalse(report["sameRunnerRandomizedComparison"])
        self.assertFalse(report["fixClaim"])


if __name__ == "__main__":
    unittest.main()
