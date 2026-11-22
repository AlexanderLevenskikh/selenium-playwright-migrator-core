"""Synthetic negative controls for independent real-browser SHA-bound receipt."""
import hashlib
import json
from pathlib import Path
import tempfile
import unittest

import p2_p3_validate_behavior_receipt as audit


def sha(data):
    return hashlib.sha256(data).hexdigest()


class ReceiptControls(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.source = b"fictional source fixture"
        self.target = b"fictional target fixture"
        self.tree = "a" * 64
        self.summary = {
            "schema": audit.SCHEMA,
            "sourceFileName": "synthetic-source.cs",
            "targetFileName": "synthetic-target.cs",
            "sourceSha256": sha(self.source),
            "targetFileSha256": sha(self.target),
            "sourceTreeSha256": self.tree,
            "candidateProvenance":
                "independent_synthetic_oracle_not_Core_VerifyArtifact",
            "methodSha256": {x:"f"*64 for x in ("Choose","Reverse","Confirm")},
            "positiveCases": list(audit.CASES),
            "autoApply": False, "requiresReview": True,
            "generalBehavioralEquivalence": "not_proven",
            "privateProjects": "not_run",
            "falseGreenNegative": {
                "sourceBaselineNUnit":"Passed", "mutantTargetNUnit":"Passed",
                "sourceEvents":["accept"], "mutantTargetEvents":[],
                "behavior":"different",
                "acceptance":"rejected_behavior_difference"},
            "negativeMutation": {"pngSha256": sha(b"\x89PNG\r\n\x1a\nfake")}
        }
        self.summary["oracleCandidateSha256"] = sha(
            ("migrator-public-oracle/v1\n" + self.tree + "\n"
             + sha(self.source) + "\n" + sha(self.target)).encode("utf-8"))
        self.write("synthetic-source.cs", self.source)
        self.write("synthetic-target.cs", self.target)
        self.write("mutant-guard.png", b"\x89PNG\r\n\x1a\nfake")
        for engine in ("selenium", "playwright"):
            self.trx(engine + ".trx", {n:"Passed" for n in audit.CASES})
        self.trx("mutant.trx", {"MutationMustFail":"Failed"})
        self.trx("falsegreen.trx", {"LostActionNUnitGreen":"Passed"})
        self.write("falsegreen-LostActionNUnitGreen.json",
                   json.dumps({"events":[]}).encode())
        self.write("mutant-MutationMustFail.json",
                   json.dumps({"events":["reject"]}).encode())
        for case in audit.CASES:
            event = "accept" if case in (
                "Inside", "EmptyWithin", "ReverseInside", "ConfirmYes"
            ) else "reject"
            for engine in ("selenium", "playwright"):
                self.write(engine+"-"+case+".json",
                           json.dumps({"events":[event],"count":2,
                                       "userAgent":"fictional"}).encode())
        self.sync()

    def write(self, name, body):
        (self.root / name).write_bytes(body)

    def trx(self, name, cases):
        lines = ["<TestRun>"]
        for test, outcome in cases.items():
            lines.append('<UnitTestResult testName="' + test
                         + '" outcome="' + outcome + '"/>')
        lines.append("</TestRun>")
        self.write(name, "".join(lines).encode())

    def sync(self):
        self.write("summary.json",
                   (json.dumps(self.summary,sort_keys=True)+"\n").encode())
        sums = {p.name:sha(p.read_bytes())
                for p in self.root.iterdir() if p.name!="SHA256SUMS.json"}
        self.write("SHA256SUMS.json",json.dumps(sums).encode())

    def test_bounded_oracle_verified_without_general_accept(self):
        result = audit.verify(self.root)
        self.assertEqual(result["status"],"bounded_observable_slice_verified")
        self.assertEqual(result["cases"],8)
        self.assertEqual(result["negativeMutations"],2)
        self.assertFalse(result["canAutoApply"])
        self.assertEqual(result["publicProjectBehaviorEquivalence"],"unknown")

    def test_tampered_target_rejected_even_if_manifest_updated(self):
        self.write("synthetic-target.cs",self.target+b" changed")
        self.sync()
        with self.assertRaisesRegex(ValueError,"CANDIDATE_BYTES_MOVED"):
            audit.verify(self.root)

    def test_incorrect_candidate_digest_rejected(self):
        self.summary["oracleCandidateSha256"]="b"*64
        self.sync()
        with self.assertRaisesRegex(ValueError,"CANDIDATE_REVISION_INCORRECT"):
            audit.verify(self.root)

    def test_falsegreen_must_be_explicitly_rejected(self):
        self.summary["falseGreenNegative"]["acceptance"]="accepted"
        self.sync()
        with self.assertRaisesRegex(ValueError,"FALSE_GREEN_NOT_REJECTED"):
            audit.verify(self.root)

    def test_mutant_nunit_must_remain_failed(self):
        self.trx("mutant.trx",{"MutationMustFail":"Passed"})
        self.sync()
        with self.assertRaisesRegex(ValueError,"MUTATION_NUNIT_NOT_FAILED"):
            audit.verify(self.root)

    def test_missing_or_corrupted_manifest_rejected(self):
        self.write("synthetic-source.cs",self.source+b"\nmutation")
        with self.assertRaisesRegex(ValueError,"MANIFEST_SHA256"):
            audit.verify(self.root)


if __name__=="__main__":
    unittest.main()
