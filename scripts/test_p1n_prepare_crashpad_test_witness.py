"""P1-N portable fictional regressions; no Chromium, Node or NUnit execution."""
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import p1n_prepare_crashpad_test_witness as p
from p1l_crashpad_client_boundary import Unverified


class P1NControls(unittest.TestCase):
    def test_exact_source_anchor_and_event_insertion(self):
        server = "\n".join(old for old, _ in p.SERVER_EDITS)
        upload = "\n".join(old for old, _ in p.UPLOAD_EDITS)
        patched = p.patch_exact(server, p.SERVER_EDITS)
        upload_patched = p.patch_exact(upload, p.UPLOAD_EDITS)
        for event in ("initial_register", "pipe_register", "mode",
                      "process_end_callback", "completion_posted",
                      "completion_post_failed", "completion_consumed",
                      "client_erased"):
            self.assertIn(event, patched)
        for event in ("upload_begin", "upload_end"):
            self.assertIn(event, upload_patched)
        self.assertIn("GetProcessTimes", p.HEADER_TEXT)
        self.assertIn("MIGRATOR_P1_TEST_OWNED_WITNESS", p.HEADER_TEXT)
        self.assertIn("MIGRATOR_P1_TEST_WITNESS_FILE", p.HEADER_TEXT)

    def test_missing_and_duplicate_upstream_anchors_fail_closed(self):
        with self.assertRaisesRegex(Unverified, "ANCHOR_NOT_EXACT"):
            p.patch_exact("no upstream", p.SERVER_EDITS)
        old = p.SERVER_EDITS[0][0]
        with self.assertRaisesRegex(Unverified, "ANCHOR_NOT_EXACT"):
            p.patch_exact(old + "\n" + old, p.SERVER_EDITS[:1])

    def test_cross_contamination_or_existing_overlay_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp) / "source"
            root.mkdir()
            with self.assertRaisesRegex(Unverified, "MUST_BE_ISOLATED"):
                p.prepare(root, root / "overlay")

    def test_fake_uploader_git_blob_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / p.UPLOADER
            path.parent.mkdir(parents=True)
            path.write_bytes(b"fake-upload-implementation")
            with self.assertRaisesRegex(Unverified, "UPLOADER_BLOB_MISMATCH"):
                p.verify_uploader(Path(tmp))

    def test_test_owned_overlay_never_claims_compiled_fix(self):
        with tempfile.TemporaryDirectory() as tmp:
            source = Path(tmp) / "source"
            output = Path(tmp) / "overlay"
            source.mkdir()
            for path, data in (
                (p.SERVER, "\n".join(old for old, _ in p.SERVER_EDITS)),
                (p.UPLOADER, "\n".join(old for old, _ in p.UPLOAD_EDITS))):
                target = source / path
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_text(data, encoding="utf-8")
            with mock.patch.object(p.pinned, "verify_tagged_source",
                                   return_value={"status":
                                       "tagged_source_exact_git_blobs_checked"}), \
                 mock.patch.object(p, "verify_uploader",
                                   return_value=(source / p.UPLOADER).read_text()):
                result = p.prepare(source, output)
            self.assertEqual(result["schema"], p.SCHEMA)
            self.assertEqual(len(result["testOwnedOverlayFiles"]), 3)
            self.assertTrue(result["sourceIdentity"].startswith("tagged_source"))
            for key in ("compiled", "executed", "originalP47Changed",
                        "stockChromeChanged", "rootCauseProven",
                        "productFixProven", "fixClaim",
                        "registeredClientIdentityInOriginalProven"):
                self.assertFalse(result[key])
            self.assertTrue((output / p.HEADER).is_file())


if __name__ == "__main__":
    unittest.main()
