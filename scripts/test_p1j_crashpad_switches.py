"""P1-J fail-closed switch-only, read-only OS diagnostic contract."""
import hashlib
from pathlib import Path
import tempfile
import unittest

import p2_preview47_process_evidence as evidence

ORIGINAL_P47_SHA256 = "fb4f7287c93e5406ff5616afa67ab10d041ab303476a25547ff3815b6e7bb8b4"


def record(flags=("present", "absent", "present"), role="crashpad-handler"):
    return ("820|3472|11/22/2026 09:48:57|chrome|2026-11-22T09:48:57.0Z|"
            + role + "||" + "|".join(flags))


class CrashpadFlagControls(unittest.TestCase):
    def test_prefault_identity_is_retained_and_only_presence_is_reported(self):
        decoded = evidence.parse_sanitized_cim_record(record())
        self.assertIsNotNone(decoded)
        pid, original, safe = decoded
        self.assertEqual((pid, original[0], original[2]), (820, 3472, "chrome"))
        self.assertEqual(safe["crashpadSwitches"],
                         {"initialClientData": "present", "pipeName": "absent",
                          "noPeriodicTasks": "present", "containsValues": False})

    def test_unknown_does_not_mean_absent_and_old_evidence_is_unknown(self):
        for flags in (("unknown", "unknown", "unknown"),):
            self.assertEqual(evidence.parse_sanitized_cim_record(record(flags))[2]
                             ["crashpadSwitches"]["pipeName"], "unknown")
        legacy = record().rsplit("|", 3)[0]
        self.assertEqual(evidence.parse_sanitized_cim_record(legacy)[2]
                         ["crashpadSwitches"]["initialClientData"], "unknown")

    def test_raw_client_ids_names_paths_are_not_emitted(self):
        for suffix in ("--initial-client-data=SECRET", r"\\.\pipe\private", "C:\\Users\\secret"):
            bad = record(("present", suffix, "absent"))
            self.assertIsNone(evidence.parse_sanitized_cim_record(bad))
        self.assertIsNone(evidence.parse_sanitized_cim_record(record() + "|extra"))
        self.assertIsNone(evidence.parse_sanitized_cim_record("820|3472|x|chrome|x|crashpad-handler||present|absent"))
        self.assertIsNone(evidence.parse_sanitized_cim_record("820|3472|x|other|x|||present|absent|absent"))

    def test_unrelated_chrome_flags_are_not_attributed_to_crashpad(self):
        row = record(("not_applicable",)*3, role="utility")
        self.assertEqual(evidence.parse_sanitized_cim_record(row)[2]
                         ["crashpadSwitches"]["pipeName"], "not_applicable")

    def test_source_original_p47_is_byte_for_byte_unchanged(self):
        p47 = Path(__file__).resolve().parent / "p2_preview47_one_time_real_chromium.py"
        self.assertEqual(hashlib.sha256(p47.read_bytes()).hexdigest(),
                         ORIGINAL_P47_SHA256)

    def test_empty_metadata_does_not_invent_crashpad_switch_evidence(self):
        with tempfile.TemporaryDirectory() as folder:
            audit = evidence.StageEvidence(folder, "control", "Passed", False,
                                           native=type("Native", (), {"inspect": lambda *a: {
                                               "state": "live", "creationFileTime": "123"}})())
            rows = audit.records({820: (3472, "time", "chrome")},
                                 {820: {"chromiumRole": "crashpad-handler"}},
                                 {(820, "time")})
            self.assertIsNone(rows[0]["crashpadSwitches"])

if __name__ == "__main__":
    unittest.main()
