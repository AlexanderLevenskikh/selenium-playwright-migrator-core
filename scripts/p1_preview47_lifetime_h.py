"""P1-H: ONE original five-stage B block, native Chrome process exit timing.

Source-exact P47, real Node SIGTERM, unchanged original 12-s max presence gate.
No injected fault, delayed/suspended Chrome, retries or extra green B attempts.
A historical native-live evidence remains the A control; no new A run.
"""
import json
import os
import subprocess

import p1_preview47_external_job as external
import p1_preview47_exit_timing as lifetime

ORIGINAL_SOURCE_SHA256 = "fb4f7287c93e5406ff5616afa67ab10d041ab303476a25547ff3815b6e7bb8b4"


def native_factory(api, native, job, wrapper_pid, wrapper_filetime, arm, source):
    return lifetime.LifetimeObserver(api, native, job, wrapper_pid,
                                     wrapper_filetime, arm, source,
                                     external.p47.STAGES, external.evidence.utc_now,
                                     external.proof.CHILD_RIGHTS)


def job_close_proven(folder):
    path = folder / "containment.json"
    if not path.is_file():
        return False
    data = json.loads(path.read_text(encoding="utf-8"))
    proof = data.get("postGateHostCleanup", {})
    return (data.get("unassistedGateBeforeHostClose") == "failed"
            and proof.get("originalGate") == "failed"
            and proof.get("originalGateReclassified") is False
            and proof.get("nativeLiveAfterHold") is True
            and proof.get("jobClosedAfterGate") is True
            and proof.get("hostTerminatedRealSurvivor") is True
            and proof.get("unassistedGateStillFailed") is True
            and proof.get("status") == "real_survivors_terminated_after_failed_unassisted_gate"
            and bool(proof.get("verifiedBeforeClose"))
            and len(proof["verifiedBeforeClose"]) == len(proof.get("observedExitsAfterJobClose", []))
            and all(row["waitResult"] == 0 and row["nativeState"] == "exited"
                    for row in proof["observedExitsAfterJobClose"]))


def main():
    require = external.proof.require
    require(os.name == "nt", "P1H_WINDOWS_REQUIRED")
    require(external.evidence.PASSIVE_WINDOW_SECONDS == 12, "P1H_ORIGINAL_GATE_DRIFT")
    require(external.digest(external.ORIGINAL) == ORIGINAL_SOURCE_SHA256,
            "P1H_ORIGINAL_P47_SHA_DRIFT")
    out = external.ROOT / "artifacts/pom-preview47-p1h-lifetime"
    out.mkdir(parents=True, exist_ok=False)
    plan = {"schema": "migrator-preview47-p1h-plan/v1",
            "arm": "B", "maxOriginalFiveStageBlocks": 1,
            "originalFiveStages": external.p47.STAGES, "originalFailFast": True,
            "stageRetries": 0, "originalGateSeconds": 12,
            "originalSourceSha256": ORIGINAL_SOURCE_SHA256,
            "hostJobAssignedBeforeBootstrap": True,
            "readOnlyObserver": "retained_native_process_handles_wait_25ms",
            "nativeRights": "SYNCHRONIZE|PROCESS_QUERY_LIMITED_INFORMATION",
            "noInjectedFailure": True, "noChromiumSuspension": True,
            "noSchedulingManipulation": True, "noNewAControl": True,
            "historicalA": ["14744145404", "14745840024",
                            "14743657457", "14742598100"],
            "productFixClaimsAllowed": False,
            "postGateProofOnlyForRealNativeLiveChrome": True}
    external.evidence.atomic_json(out / "plan.json", plan)
    result = {"schema": "migrator-preview47-p1h-result/v1",
              "arm": "B", "originalBlocksPlanned": 1, "originalBlocksExecuted": 0,
              "fixClaim": False, "mergeAllowed": False, "releaseAllowed": False}
    try:
        os.environ["P47_DIAGNOSTIC_CYCLES"] = "0"
        environment = external.fingerprint()
        external.evidence.atomic_json(out / "environment.json", environment)
        block = out / "block-1"
        try:
            status = external.run_block("B", block, observer_factory=native_factory)
        except (OSError, ValueError, AssertionError, subprocess.SubprocessError) as error:
            status = "prerequisite_failure"
            result["detail"] = str(error)
        result["originalBlocksExecuted"] = 1
        result["originalBlockStatus"] = status
        native = block / "native-observer.json"
        if native.is_file():
            report = json.loads(native.read_text(encoding="utf-8"))
            result["nativeLifetimeStages"] = [
                {"stage": r["stage"], "originalGate": r["originalGate"],
                 "originalStatus": r["originalStatus"], "tracks": len(r["nativeTracks"]),
                 "nativeExitsBeforeRunner": sum(
                     t["timingClassification"] == "native_exit_after_fault_before_runner_exit"
                     for t in r["nativeTracks"]),
                 "survivedAfterRunner": sum(
                     t["timingClassification"] == "native_live_at_or_after_runner_exit"
                     for t in r["nativeTracks"])}
                for r in report.get("sourceLinkedStages", [])]
            result["hostJobHeldAfterOriginalGate"] = report.get(
                "hostJobHandleHeldAfterOriginalGate") is True
        result["nativeLiveBOriginalFail"] = status == "native_live_fail"
        result["independentPostGateJobCloseProven"] = job_close_proven(block)
        stages = block / "stages.json"
        if stages.is_file():
            rows = json.loads(stages.read_text(encoding="utf-8"))
            executed = [r for r in rows if r["disconnect"] and
                        r["status"] not in ("not_run_due_to_original_fail_fast",
                                            "prerequisite_failure")]
            result["prefaultMembershipAttested"] = bool(executed) and all(
                row["prefaultMembership"] == "attested" for row in executed)
        if status == "original_pass" and not result.get("prefaultMembershipAttested"):
            result["status"] = "prerequisite_failure_native_membership"
        elif status == "native_live_fail":
            result["status"] = ("native_live_B_job_close_oracle_proved_original_fail_preserved"
                                if result["independentPostGateJobCloseProven"] else
                                "native_live_B_survivor_without_job_close_proof")
        elif status != "original_pass":
            result["status"] = status
        elif not result.get("hostJobHeldAfterOriginalGate"):
            result["status"] = "prerequisite_failure_job_owner_unproven"
        else:
            result["status"] = "natural_chrome_exit_timing_measured_no_B_survivor"
        if not result["status"].startswith("prerequisite_failure"):
            environment["packages"] = external.package_fingerprint()
            external.evidence.atomic_json(out / "environment.json", environment)
    except (OSError, ValueError, AssertionError, subprocess.SubprocessError) as error:
        result.update(status="prerequisite_failure", detail=str(error))
    finally:
        external.evidence.atomic_json(out / "result.json", result)
        external.evidence.write_manifest(out)
    print(json.dumps(result, sort_keys=True))
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
