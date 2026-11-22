#!/usr/bin/env python3
"""Independent, fail-closed SHA-bound verification of a real Selenium/PW oracle.

Read-only on already generated public fictional evidence; never executes source,
downloads external files or elevates a local scenario to general parity.
"""
import argparse
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET

SCHEMA = "migrator-p3-real-pom-count-guards/v1"
CASES = ("AtMaximum", "BelowMinimum", "ConfirmNo", "ConfirmYes",
         "EmptyWithin", "Inside", "ReverseEqual", "ReverseInside")
LIMIT = 64 * 1024 * 1024


def check(ok, code):
    if not ok:
        raise ValueError("P2P3_BEHAVIOR_RECEIPT_REJECTED:" + code)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def nunit_trx(blob):
    root = ET.fromstring(blob)
    observed = {}
    for result in root.iter():
        if not result.tag.endswith("UnitTestResult"):
            continue
        name = result.attrib.get("testName", "").split(".")[-1]
        check(name and name not in observed, "NUNIT_CASE_IDENTITY_DUPLICATE")
        observed[name] = result.attrib.get("outcome")
    return observed


def verify(directory):
    root = Path(directory)
    check(root.is_dir() and not root.is_symlink(), "ARTIFACT_ROOT_MISSING")
    members = [p for p in root.iterdir() if p.is_file()]
    check(members and len(members) <= 80
          and all(not p.is_symlink() and p.stat().st_size < LIMIT for p in members),
          "ARTIFACT_FILE_BUDGET")
    sums_path = root / "SHA256SUMS.json"
    check(sums_path.is_file(), "MANIFEST_MISSING")
    sums = json.loads(sums_path.read_text(encoding="utf-8"))
    actual = {p.name for p in members} - {"SHA256SUMS.json"}
    check(isinstance(sums, dict) and set(sums) == actual,
          "MANIFEST_COVERAGE")
    for name, expected in sums.items():
        check(isinstance(name, str) and "/" not in name
              and "\\" not in name and name not in ("", ".", "..")
              and isinstance(expected, str) and len(expected) == 64
              and digest((root / name).read_bytes()) == expected,
              "MANIFEST_SHA256")
    report = json.loads((root / "summary.json").read_text(encoding="utf-8"))
    check(report.get("schema") == SCHEMA, "SCHEMA")
    check(report.get("sourceFileName") == "synthetic-source.cs"
          and report.get("targetFileName") == "synthetic-target.cs",
          "CANDIDATE_FILE_ID")
    source = (root / "synthetic-source.cs").read_bytes()
    target = (root / "synthetic-target.cs").read_bytes()
    check(digest(source) == report.get("sourceSha256")
          and digest(target) == report.get("targetFileSha256"),
          "CANDIDATE_BYTES_MOVED")
    revision = digest(("migrator-public-oracle/v1\n"
                       + report["sourceTreeSha256"] + "\n"
                       + report["sourceSha256"] + "\n"
                       + report["targetFileSha256"]).encode("utf-8"))
    check(revision == report.get("oracleCandidateSha256")
          and report.get("candidateProvenance")
          == "independent_synthetic_oracle_not_Core_VerifyArtifact",
          "CANDIDATE_REVISION_INCORRECT")
    check(report.get("positiveCases") == list(CASES), "DECLARED_CASE_SET")
    check(set(report.get("methodSha256", {})) ==
          {"Choose", "Reverse", "Confirm"}
          and all(len(x) == 64 for x in report["methodSha256"].values()),
          "METHOD_LINEAGE_MISSING")
    for engine in ("selenium", "playwright"):
        outcome = nunit_trx((root / (engine + ".trx")).read_bytes())
        check(outcome == {name: "Passed" for name in CASES},
              "REAL_POSITIVE_NUNIT_" + engine.upper())
    for name in CASES:
        source_observed = json.loads((root / ("selenium-" + name + ".json")).read_text())
        target_observed = json.loads((root / ("playwright-" + name + ".json")).read_text())
        check(source_observed == target_observed
              and source_observed.get("events") ==
              (["accept"] if name in
               ("Inside", "EmptyWithin", "ReverseInside", "ConfirmYes")
               else ["reject"]), "OBSERVABLE_MISMATCH_" + name)
    check(nunit_trx((root / "mutant.trx").read_bytes()) ==
          {"MutationMustFail": "Failed"}, "MUTATION_NUNIT_NOT_FAILED")
    check(nunit_trx((root / "falsegreen.trx").read_bytes()) ==
          {"LostActionNUnitGreen": "Passed"}, "FALSE_GREEN_NUNIT_NOT_PASSED")
    rejected = report.get("falseGreenNegative", {})
    check(rejected == {"sourceBaselineNUnit":"Passed",
                       "mutantTargetNUnit":"Passed",
                       "sourceEvents":["accept"],"mutantTargetEvents":[],
                       "behavior":"different",
                       "acceptance":"rejected_behavior_difference"},
          "FALSE_GREEN_NOT_REJECTED")
    no_action = json.loads(
        (root / "falsegreen-LostActionNUnitGreen.json").read_text())
    check(no_action.get("events") == []
          and json.loads((root / "selenium-Inside.json").read_text())
                  .get("events") == ["accept"], "FALSE_GREEN_OBSERVABLE_INVALID")
    photo = (root / "mutant-guard.png").read_bytes()
    check(photo.startswith(b"\x89PNG\r\n\x1a\n")
          and digest(photo) == report.get("negativeMutation", {}).get("pngSha256"),
          "MUTANT_FAILURE_PNG_INVALID")
    check(report.get("autoApply") is False
          and report.get("requiresReview") is True
          and report.get("generalBehavioralEquivalence") == "not_proven"
          and report.get("privateProjects") == "not_run",
          "FORBIDDEN_BEHAVIORAL_PROMOTION")
    return {"schema": "migrator-p2p3-independent-observable-gate/v1",
            "status": "bounded_observable_slice_verified",
            "candidateSha256": revision,
            "cases": len(CASES),
            "negativeMutations": 2,
            "falseGreenNUnitPassedButBehaviorRejected": True,
            "publicProjectBehaviorEquivalence": "unknown",
            "generalBehavioralEquivalence": "not_proven",
            "canAutoApply": False}


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("evidence_root", type=Path)
    args = p.parse_args()
    print(json.dumps(verify(args.evidence_root), sort_keys=True))


if __name__ == "__main__":
    main()
