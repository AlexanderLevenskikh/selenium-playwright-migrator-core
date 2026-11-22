"""Portable P1-M fictional controls. No external process, network or real ZIP rewrite."""
import hashlib
import json
from pathlib import Path
import tempfile
import unittest

import p1m_crashpad_tagged_source_boundary as m
from test_p1l_crashpad_client_boundary import fixture_archive
from p1l_crashpad_client_boundary import Unverified


def sha(data):
    return hashlib.sha256(data).hexdigest()


class P1MControls(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.zip = self.root / "original.zip"
        self.src = self.root / "chromium"
        self.src.mkdir()

    def source_fixture(self):
        entries = {
            "chrome/VERSION": b"MAJOR=156\nMINOR=0\nBUILD=8078\nPATCH=80\n",
            "third_party/crashpad/README.chromium":
                ("Name: Crashpad\nRevision: " + m.CRASHPAD_REV + "\n").encode(),
            m.ROOT + "util/win/exception_handler_server.cc":
                b"fictional complete server implementation",
            m.ROOT + "util/win/exception_handler_server.h":
                b"fictional private state",
            m.ROOT + "handler/handler_main.cc": b"fictional handler main",
            m.ROOT + "handler/crashpad_handler.md": b"fictional manual"
        }
        for relative, content in entries.items():
            path = self.src / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(content)
        return {p: m.git_blob_sha1(data) for p, data in entries.items()}

    def test_exact_source_git_blob_validator_and_no_binary_claim(self):
        expected = self.source_fixture()
        proof = m.verify_tagged_source(self.src, expected)
        self.assertEqual(proof["status"], "tagged_source_exact_git_blobs_checked")
        self.assertEqual(len(proof["sourceFiles"]), 6)
        self.assertFalse(proof["deployedChromeBinaryProven"])
        self.assertEqual(proof["crashpadRevision"], m.CRASHPAD_REV)

    def test_source_mutation_is_rejected(self):
        expected = self.source_fixture()
        path = self.src / (m.ROOT + "handler/handler_main.cc")
        path.write_bytes(path.read_bytes() + b" tamper")
        with self.assertRaisesRegex(Unverified, "TAGGED_SOURCE_BLOB_MISMATCH"):
            m.verify_tagged_source(self.src, expected)

    def test_source_link_is_rejected_if_supported(self):
        expected = self.source_fixture()
        link = self.src / (m.ROOT + "handler/handler_main.cc")
        sibling = self.root / "external.cc"
        sibling.write_bytes(link.read_bytes())
        link.unlink()
        try:
            link.symlink_to(sibling)
        except (OSError, NotImplementedError):
            self.skipTest("OS prohibits symlinks in this unprivileged account")
        with self.assertRaisesRegex(Unverified, "TAGGED_SOURCE_SYMLINK_REJECTED"):
            m.verify_tagged_source(self.src, expected)

    def test_historical_original_native_live_never_becomes_registry_or_fix(self):
        digest = fixture_archive(self.zip, fail=True, primary=True)
        result = m.evaluate([("original_P47", digest, self.zip)])
        self.assertEqual(result["historicalNativeLiveFailures"], 1)
        self.assertEqual(result["originals"][0]["nativeLiveCrashpadCount"], 1)
        self.assertEqual(result["originals"][0]["originalSurvivors"][0]["creationFileTime"],
                         "3000")
        self.assertEqual(result["originals"][0]["persistentHandlerMode"], "unknown")
        self.assertEqual(result["originals"][0]["clientCompletionConsumed"], "unknown")
        self.assertEqual(result["originals"][0]["crashUploadInFlight"], "unknown")
        self.assertFalse(result["rootCauseProven"])
        self.assertFalse(result["productFixProven"])
        self.assertFalse(result["fixClaim"])
        self.assertEqual(len(result["requiredPrivateHandlerWitness"]), 11)

    def test_green_nonreproduction_is_not_a_repair(self):
        digest = fixture_archive(self.zip, fail=False)
        result = m.evaluate([("green", digest, self.zip)])
        self.assertEqual(result["historicalNativeLiveFailures"], 0)
        self.assertFalse(result["productFixProven"])
        self.assertEqual(result["originals"][0]["originalStatus"],
                         "no_original_native_live_failure_attested")

    def test_bad_outer_zip_sha_and_malformed_labels_rejected(self):
        digest = fixture_archive(self.zip, fail=True)
        with self.assertRaisesRegex(Unverified, "PINNED_ORIGINAL_ZIP_SHA"):
            m.evaluate([("bad", "f" * 64, self.zip)])
        with self.assertRaisesRegex(Unverified, "UNSAFE_EVIDENCE_LABEL"):
            m.evaluate([("private/raw/path", digest, self.zip)])

    def test_output_contains_no_pipe_handle_url_command_or_client_identifier(self):
        digest = fixture_archive(self.zip)
        rendered = json.dumps(m.evaluate([("fixture", digest, self.zip)]))
        for forbidden in ("pipeName", "commandLine", "rawHandle",
                          "crashReportUrl", "databasePath", "ClientDataId",
                          "private_ArBilling"):
            self.assertNotIn(forbidden, rendered)


if __name__ == "__main__":
    unittest.main()
