"""P1-G: source-exact P47 Job-close witness with two *separately labelled* B modes.

B_precreate: original P1-F Job assignment before the bootstrap byte (one block).
B_late: already-launched Chrome-only processes added to a host Job BEFORE
P47's original ready marker (at most 3 original fail-fast blocks). This mode
tests the Win32 Job-close oracle but is NOT product pre-create mitigation.

No synthetic Chrome, no altered P47, no SIGTERM injection, no modified
runner-exit-anchored 12-second gate and no rerun of any original FAIL.
"""
import json
import os
import subprocess

import p1_preview47_external_job as external
import p1_preview47_native_tree as native_tree
from p1_preview47_late_chrome_job import LateChromeJobObserver

EXPECTED_SOURCE_SHA = "fb4f7287c93e5406ff5616afa67ab10d041ab303476a25547ff3815b6e7bb8b4"
PHASES = (("B", 1, "precreate"), ("B_late", 3, "late_chrome_only"))


def witness_factory(api, native, job, wrapper_pid, wrapper_filetime, arm, source):
    cls = LateChromeJobObserver if arm == "B_late" else native_tree.NativeTreeObserver
    return cls(api, native, job, wrapper_pid, wrapper_filetime, arm,
               source, external.p47.STAGES, external.evidence.utc_now,
               external.proof.CHILD_RIGHTS)


def inspect_containment(path, arm):
    if not path.is_file():
        return {"status": "no_real_survivor_or_unattested", "proved": False}
    receipt = json.loads(path.read_text(encoding="utf-8"))
    proof = receipt.get("postGateHostCleanup", {})
    confirmed = (
        receipt.get("unassistedGateBeforeHostClose") == "failed"
        and receipt.get("arm") == arm
        and receipt.get("productPreCreateMitigationProved") is False
        and proof.get("originalGate") == "failed"
        and proof.get("unassistedGateStillFailed") is True
        and proof.get("originalGateReclassified") is False
        and proof.get("nativeLiveAfterHold") is True
        and proof.get("jobClosedAfterGate") is True
        and proof.get("hostTerminatedRealSurvivor") is True
        and proof.get("status") == "real_survivors_terminated_after_failed_unassisted_gate"
        and bool(proof.get("verifiedBeforeClose"))
        and len(proof.get("verifiedBeforeClose")) ==
            len(proof.get("observedExitsAfterJobClose", []))
        and all(row.get("waitResult") == 0 and row.get("nativeState") == "exited"
                for row in proof.get("observedExitsAfterJobClose", []))
    )
    return {"status": proof.get("status", receipt.get("status", "unattested")),
            "proved": confirmed, "survivors": proof.get("verifiedBeforeClose", []),
            "exits": proof.get("observedExitsAfterJobClose", []),
            "scope": "precreate_product_compatible" if arm == "B"
                     else "experimental_chrome_only_late_assignment_not_product"}


def main():
    require = external.proof.require
    require(os.name == "nt", "P1G_WINDOWS_REQUIRED")
    require(external.evidence.PASSIVE_WINDOW_SECONDS == 12, "P1G_ORIGINAL_GATE_CHANGED")
    require(external.digest(external.ORIGINAL) == EXPECTED_SOURCE_SHA,
            "P1G_SOURCE_EXACT_P47_SHA_DRIFT")
    out = external.ROOT / "artifacts/pom-preview47-p1g-job-close"
    out.mkdir(parents=True, exist_ok=False)
    plan = {"schema": "migrator-preview47-p1g-predeclared-plan/v1",
            "originalSourceSha256": EXPECTED_SOURCE_SHA,
            "originalGateSeconds": 12, "originalFailFast": True,
            "stages": external.p47.STAGES, "stageRetries": 0,
            "phases": [{"arm": arm, "maxOriginalBlocks": count,
                        "association": assoc} for arm, count, assoc in PHASES],
            "stopWhenOriginalFails": True,
            "BPrecreateProductMode": "Job-assigned-wrapper-before-bootstrap",
            "BLateExperimentOnly": "Chrome-associated-after-launch_before_ready_Node_outside_job",
            "BTarget": "real_source_attested_native_live_chromium_after_original_failed_gate",
            "postgateProof": "KILL_ON_JOB_CLOSE last host handle only",
            "acceptNoSurvivorAsFix": False,
            "precreateProductFixMayNotBeInferredFromLateExperiment": True,
            "historicalAFailuresPreserved": ["14744145404", "14745840024",
                                             "14743657457", "14742598100"],
            "noReclassificationOfOriginalFailure": True}
    external.evidence.atomic_json(out / "plan.json", plan)
    result = {"schema": "migrator-preview47-p1g-result/v1",
              "headOrigin": "P1F_SHA_4114844", "phases": {},
              "fixClaim": False, "precreateProductFixProven": False,
              "mergeAllowed": False, "releaseAllowed": False}
    try:
        os.environ["P47_DIAGNOSTIC_CYCLES"] = "0"
        environment = external.fingerprint()
        external.evidence.atomic_json(out / "environment.json", environment)
        stop = False
        for arm, count, association in PHASES:
            if stop:
                break
            (out / arm).mkdir(exist_ok=False)
            rows = []
            for number in range(1, count + 1):
                folder = out / arm / f"block-{number}"
                try:
                    status = external.run_block(arm, folder,
                                                observer_factory=witness_factory)
                    item = {"block": number, "status": status,
                            "originalP47Unchanged": True}
                except (OSError, ValueError, AssertionError, subprocess.SubprocessError) as error:
                    status = "prerequisite_failure"
                    item = {"block": number, "status": status,
                            "detail": str(error), "originalP47Unchanged": True}
                item["jobClose"] = inspect_containment(folder / "containment.json", arm)
                if item["jobClose"]["proved"]:
                    # Only a real native-LIVE survivor after original gate may
                    # ever reach this branch. The original FAIL is not erased.
                    require(status == "native_live_fail", "P1G_PROOF_WITHOUT_ORIGINAL_FAIL")
                rows.append(item)
                result["phases"][arm] = rows
                external.evidence.atomic_json(out / "result.json", result)
                if status != "original_pass":
                    stop = True
                    break
            if stop:
                break
        statuses = [row["status"] for phase in result["phases"].values()
                    for row in phase]
        positives = [{"arm": arm, "block": row["block"], "jobClose": row["jobClose"]}
                     for arm, phase in result["phases"].items()
                     for row in phase if row["jobClose"]["proved"]]
        result["realSurvivorPostgateJobCloseProofs"] = positives
        result["postGateJobCloseProvenForLateChromeOnly"] = any(
            item["arm"] == "B_late" for item in positives)
        result["postGateJobCloseProvenForPrecreateB"] = any(
            item["arm"] == "B" for item in positives)
        if "prerequisite_failure" in statuses:
            result["status"] = "prerequisite_failure"
        elif positives:
            result["status"] = "real_postgate_job_close_proved_original_fail_preserved"
        elif "native_live_fail" in statuses:
            result["status"] = "real_survivor_observed_job_close_unproven"
        elif "original_fail_native_liveness_unproven" in statuses:
            result["status"] = "original_failure_native_liveness_unproven"
        else:
            result["status"] = "inconclusive_no_native_live_B_survivor"
        if "prerequisite_failure" not in statuses:
            environment["packages"] = external.package_fingerprint()
            external.evidence.atomic_json(out / "environment.json", environment)
    except (OSError, ValueError, AssertionError, subprocess.SubprocessError) as error:
        result.update(status="prerequisite_failure", detail=str(error))
    finally:
        external.evidence.atomic_json(out / "result.json", result)
        external.evidence.write_manifest(out)
    print(json.dumps(result, sort_keys=True))
    # This experiment is not a green product acceptance gate even when the
    # separate after-gate Job-close oracle succeeds.
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
