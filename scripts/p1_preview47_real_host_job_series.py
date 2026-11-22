#!/usr/bin/env python3
"""Fixed six-cycle SOURCE-EXACT original P47 NUnit fixture Job observation.

This is NOT a retry-until-green loop and cannot approve original P47 cleanup.
Each real cycle gets its own immutable NUnit TRX, native PID/FILETIME evidence,
unassisted gate result and separately attributed post-gate Job receipt.
"""
import json
import os
from pathlib import Path
import subprocess
import sys

import p2_preview47_process_evidence as evidence

MAX_CYCLES = 6
# Same original P47 Operation, SetUp/TearDown/OneTimeTearDown; only host
# pre-Playwright.CreateAsync gate is added. Three primary-failure stages,
# three source-exact disconnect_pass stages. Order fixed before starting.
FIXED_SOURCE_EXACT_STAGES = (
    ("disconnect_primary", "Failed"),
    ("disconnect_pass", "Passed"),
    ("disconnect_primary_repeat", "Failed"),
    ("disconnect_pass", "Passed"),
    ("disconnect_primary", "Failed"),
    ("disconnect_pass", "Passed"),
)
SCHEMA = "migrator-preview47-source-exact-host-job-series/v1"


def main():
    raw = os.environ.get("P47_HOST_JOB_SERIES_CYCLES", "6")
    try:
        count = int(raw)
    except ValueError:
        raise AssertionError("P47_HOST_JOB_SERIES_CYCLES_MUST_BE_1_TO_6") from None
    if not 1 <= count <= MAX_CYCLES:
        raise AssertionError("P47_HOST_JOB_SERIES_CYCLES_MUST_BE_1_TO_6")
    repo = Path(__file__).resolve().parent.parent
    root = repo / "artifacts/pom-preview47-real-job"
    root.mkdir(parents=True, exist_ok=True)
    report = {"schema": SCHEMA, "requestedCycles": count, "completedCycles": 0,
              "status": "running", "results": [], "firstFailureStops": True,
              "originalP47Unchanged": True, "sourceExactRuntimeFixture": True,
              "fixedSourceExactStages": FIXED_SOURCE_EXACT_STAGES,
              "hostCleanupIsNotOriginalPass": True,
              "historicalNov2AndNov21Failures": "preserved_unresolved",
              "productFixProven": False, "autoApply": False}
    summary = root / "series-summary.json"
    try:
        for ordinal in range(1, count + 1):
            index = f"{ordinal:03d}"
            stage, expected = FIXED_SOURCE_EXACT_STAGES[ordinal - 1]
            env = dict(os.environ, P47_HOST_JOB_PROBE_INDEX=index,
                       P47_HOST_JOB_STAGE=stage,
                       P47_HOST_JOB_EXPECTED_NUNIT=expected)
            result = subprocess.run(
                [sys.executable,
                 str(repo / "scripts/p1_preview47_source_exact_job_probe.py")],
                cwd=repo, env=env, check=False)
            receipt = root / ("cycle-" + index) / "real-job.json"
            cycle = None
            if receipt.is_file():
                try:
                    cycle = json.loads(receipt.read_text(encoding="utf-8"))
                except (ValueError, OSError):
                    cycle = {"status": "unreadable"}
            report["results"].append({
                "cycle": ordinal, "sourceExactStage": stage,
                "expectedNUnit": expected, "exitCode": result.returncode,
                "fixtureExactExceptPreCreateGate": cycle.get(
                    "sourceExactFixtureProof", {}).get("byteExactExceptPreCreateGate")
                    if cycle else False,
                "actualNUnit": cycle.get("actualNUnitOutcome") if cycle else None,
                "receiptExists": bool(cycle),
                "unassistedGate": cycle.get("unassistedGateBeforeHostClose")
                    if cycle else "not_observed",
                "postGateHostCleanup": cycle.get("postGateHostCleanup", {}).get("status")
                    if cycle else "not_observed",
                "observedOriginalSurvivors": len(
                    cycle.get("unassistedSurvivorsBeforeHostClose", []))
                    if cycle else None,
            })
            report["completedCycles"] = ordinal
            evidence.atomic_json(summary, report)
            if result.returncode:
                report["status"] = "stopped_on_first_real_or_blocked_failure"
                # No later attempt can wash away a failed unassisted gate.
                raise AssertionError("P47_REAL_HOST_JOB_CYCLE_FAILED_" + index)
        # Six no-survivor source-exact trials do not prove the requested
        # native-live post-gate Job-close mitigation; fail closed as UNKNOWN.
        # No retries beyond the exact predeclared matrix or green selection.
        report["status"] = "completed_unproven_native_live_chrome_not_observed"
        raise AssertionError("P47_SOURCE_EXACT_REAL_NATIVE_LIVE_ORPHAN_NOT_OBSERVED")
    except BaseException:
        if report["status"] == "running":
            report["status"] = "failed"
        raise
    finally:
        evidence.atomic_json(summary, report)
        evidence.write_manifest(root)


if __name__ == "__main__":
    main()
