"""P1-F: one Windows runner, original P47 B then A, external Win32 witness.

B: Job assigned BEFORE gated wrapper is released. A: never assigned.
Each source-exact five-stage P47 block retains the original fail-fast and
unassisted 12-second gate. Nothing here claims a fix from missing survivors.
"""
import json
import os
from pathlib import Path
import subprocess

import p1_preview47_external_job as external
import p1_preview47_native_tree as tree

EXPECTED_ORIGINAL_SHA = "fb4f7287c93e5406ff5616afa67ab10d041ab303476a25547ff3815b6e7bb8b4"
ORDER = ("B", "A")
MAX_BLOCKS_PER_ARM = 3


def native_factory(api, native, job, wrapper_pid, wrapper_filetime, arm, source):
    return tree.NativeTreeObserver(
        api, native, job, wrapper_pid, wrapper_filetime, arm,
        source, external.p47.STAGES, external.evidence.utc_now,
        external.proof.CHILD_RIGHTS)


def main():
    external.proof.require(os.name == "nt", "P1F_NATIVE_WINDOWS_REQUIRED")
    external.proof.require(external.evidence.PASSIVE_WINDOW_SECONDS == 12,
                           "P1F_ORIGINAL_PRESENCE_GATE_CHANGED")
    external.proof.require(external.digest(external.ORIGINAL) == EXPECTED_ORIGINAL_SHA,
                           "P1F_ORIGINAL_SOURCE_SHA_DRIFT")
    out = external.ROOT / "artifacts/pom-preview47-p1f-ba"
    out.mkdir(parents=True, exist_ok=False)
    plan = {"schema": "migrator-preview47-p1f-plan/v1",
            "order": ORDER, "sameWindowsRunner": True,
            "maxBlocksPerArm": MAX_BLOCKS_PER_ARM, "stageRetries": 0,
            "stages": external.p47.STAGES, "originalFailFast": True,
            "originalGateSeconds": 12, "originalSourceSha256": EXPECTED_ORIGINAL_SHA,
            "observer": "independent_live_win32_toolhelp_native_join",
            "B": "host Job before the original bootstrap byte",
            "A": "no host Job assignment", "postGateProofOnly": True,
            "noSurvivorCannotProveFix": True}
    external.evidence.atomic_json(out / "plan.json", plan)
    result = {"schema": "migrator-preview47-p1f-result/v1", "order": ORDER,
              "arms": {}, "fixClaim": False, "releaseAllowed": False}
    try:
        os.environ["P47_DIAGNOSTIC_CYCLES"] = "0"
        environment = external.fingerprint()
        external.evidence.atomic_json(out / "environment.json", environment)
        prerequisite = False
        for arm in ORDER:
            (out / arm).mkdir()
            arm_rows = []
            for number in range(1, MAX_BLOCKS_PER_ARM + 1):
                block_out = out / arm / f"block-{number}"
                try:
                    status = external.run_block(arm, block_out,
                                                observer_factory=native_factory)
                    arm_rows.append({"block": number, "status": status})
                except (OSError, ValueError, AssertionError, subprocess.SubprocessError) as error:
                    status = "prerequisite_failure"
                    arm_rows.append({"block": number, "status": status,
                                     "detail": str(error)})
                if status != "original_pass":
                    break  # original five-stage block already stopped on first FAIL
            result["arms"][arm] = arm_rows
            external.evidence.atomic_json(out / "result.json", result)
            if status == "prerequisite_failure":
                prerequisite = True
                break
        if not prerequisite:
            environment["packages"] = external.package_fingerprint()
            external.evidence.atomic_json(out / "environment.json", environment)
        proven = False
        for block in (out / "B").glob("block-*/containment.json"):
            proof = json.loads(block.read_text(encoding="utf-8"))
            state = proof.get("postGateHostCleanup", {})
            if (state.get("status") == "real_survivors_terminated_after_failed_unassisted_gate"
                    and state.get("hostTerminatedRealSurvivor") is True
                    and state.get("unassistedGateStillFailed") is True):
                proven = True
        result["BRealPostGateJobCloseProven"] = proven
        coverage = []
        for arm, blocks in result["arms"].items():
            for block in blocks:
                receipt = out / arm / f"block-{block['block']}" / "stages.json"
                if receipt.is_file():
                    for stage in json.loads(receipt.read_text(encoding="utf-8")):
                        if stage.get("disconnect") and stage.get("status") not in (
                                "not_run_due_to_original_fail_fast", "prerequisite_failure"):
                            coverage.append({"arm": arm, "block": block["block"],
                                             "stage": stage["stage"],
                                             "membership": stage.get("prefaultMembership")})
        result["prefaultCoverage"] = coverage
        result["allObservedDisconnectsAttested"] = bool(coverage) and all(
            c["membership"] == "attested" for c in coverage)
        statuses = [row["status"] for rows in result["arms"].values() for row in rows]
        result["status"] = ("prerequisite_failure" if prerequisite else
                            "original_native_live_fail_preserved" if "native_live_fail" in statuses else
                            "original_fail_unproven" if "original_fail_native_liveness_unproven" in statuses else
                            "native_attestation_incomplete" if not result["allObservedDisconnectsAttested"] else
                            "no_native_live_reproduction")
    except (OSError, ValueError, AssertionError, subprocess.SubprocessError) as error:
        result.update(status="prerequisite_failure", detail=str(error))
    finally:
        external.evidence.atomic_json(out / "result.json", result)
        external.evidence.write_manifest(out)
    print(json.dumps(result, sort_keys=True))
    return int(result["status"] != "no_native_live_reproduction")


if __name__ == "__main__":
    raise SystemExit(main())
