"""P1-I: exactly one source-exact A five-stage P47 block, no retries.

On a real original Windows native-LIVE failure only, inspect and possibly
post-gate adopt observed Chromium orphans into a new host-owned Job Object.
Even a successful last-handle-close is OS proof only, not pre-create B fix.
"""
import json
import os
import subprocess

import p1_preview47_external_job as external
import p1_preview47_native_tree as tree
import p1_preview47_adopt_survivor as adoption

ORIGINAL_SHA256 = "fb4f7287c93e5406ff5616afa67ab10d041ab303476a25547ff3815b6e7bb8b4"


def watcher(api, native, job, wrapper_pid, wrapper_filetime, arm, source):
    return tree.NativeTreeObserver(
        api, native, job, wrapper_pid, wrapper_filetime, arm,
        source, external.p47.STAGES, external.evidence.utc_now,
        external.proof.CHILD_RIGHTS)


def main():
    require = external.proof.require
    require(os.name == "nt", "P1I_WINDOWS_ONLY")
    require(external.evidence.PASSIVE_WINDOW_SECONDS == 12, "P1I_ORIGINAL_GATE_CHANGED")
    require(external.digest(external.ORIGINAL) == ORIGINAL_SHA256,
            "P1I_ORIGINAL_SOURCE_SHA_DRIFT")
    out = external.ROOT / "artifacts/pom-preview47-p1i-real-orphan"
    out.mkdir(parents=True, exist_ok=False)
    plan = {"schema": "migrator-preview47-p1i-plan/v1", "arm": "A",
            "originalSourceSha256": ORIGINAL_SHA256,
            "originalStages": external.p47.STAGES,
            "maxOriginalFiveStageBlocks": 1, "stageRetries": 0,
            "stopAtFirstOriginalFail": True, "originalGateSeconds": 12,
            "noPreGateAssignment": True, "noSyntheticFault": True,
            "noChromiumPauseOrPriorityManipulation": True,
            "adoptionCondition": "real_original_native_live_chrome_after_original_failed_gate",
            "adoptionRequiresExactPreFaultAndOriginalAncestry": True,
            "postGateHoldMillisecondsBeforeAndAfterAdoption": [300, 300],
            "closeMethod": "last_host_owned_job_handle_CloseHandle",
            "postCloseExitProof": "same_native_FILETIME_WaitForSingleObject",
            "notAProductPrecreateBTest": True,
            "historicalOriginalEvidence": ["14747793425", "14744145404",
                                           "14745840024", "14743657457",
                                           "14742598100", "14743302481"],
            "fixClaim": False}
    external.evidence.atomic_json(out / "plan.json", plan)
    result = {"schema": "migrator-preview47-p1i-result/v1", "arm": "A",
              "maxBlocks": 1, "fixClaim": False, "precreateBProductFixProven": False,
              "mergeAllowed": False, "releaseAllowed": False}
    try:
        os.environ["P47_DIAGNOSTIC_CYCLES"] = "0"
        environment = external.fingerprint()
        external.evidence.atomic_json(out / "environment.json", environment)
        block = out / "block-1"
        try:
            original_status = external.run_block(
                "A", block, observer_factory=watcher,
                postgate_callback=adoption.postgate_adopt)
            result["originalStatus"] = original_status
        except (OSError, AssertionError, ValueError, subprocess.SubprocessError) as error:
            result.update(originalStatus="prerequisite_failure", detail=str(error))
        result["blocksExecuted"] = 1
        proof_path = block / "postgate-adoption.json"
        if proof_path.is_file():
            proof = json.loads(proof_path.read_text(encoding="utf-8"))
            result["adoptionStatus"] = proof.get("status")
            result["adoptedProcessCount"] = sum(
                x.get("status") == "assigned" for x in proof.get("adoption", []))
            result["realAOrphanKilledByPostGateJobClose"] = (
                proof.get("status") == "real_orphan_exited_after_postgate_job_close" and
                proof.get("osMechanismProvedForAdoptedAOnly") is True and
                proof.get("originalUnassistedGateStillFailed") is True and
                proof.get("fixClaim") is False)
        else:
            result["adoptionStatus"] = "not_attempted_no_source_attested_original_A_orphan"
            result["realAOrphanKilledByPostGateJobClose"] = False
        original = result.get("originalStatus")
        if original == "native_live_fail":
            result["status"] = ("OS_job_close_proved_on_real_A_orphan_only"
                                if result["realAOrphanKilledByPostGateJobClose"]
                                else "original_A_live_fail_adoption_unproven")
        elif original == "original_pass":
            result["status"] = "no_A_native_live_survivor_not_a_job_close_proof"
        elif original == "prerequisite_failure":
            result["status"] = "prerequisite_failure"
        else:
            result["status"] = "original_A_fail_native_liveness_unproven"
        if original in ("native_live_fail", "original_pass"):
            environment["packages"] = external.package_fingerprint()
            external.evidence.atomic_json(out / "environment.json", environment)
    except (OSError, AssertionError, ValueError, subprocess.SubprocessError) as error:
        result.update(status="prerequisite_failure", detail=str(error))
    finally:
        external.evidence.atomic_json(out / "result.json", result)
        external.evidence.write_manifest(out)
    print(json.dumps(result, sort_keys=True))
    # Diagnostic red is intentional: OS job-close proof is not a product fix.
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
