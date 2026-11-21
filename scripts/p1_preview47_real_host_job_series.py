#!/usr/bin/env python3
"""Fixed six-cycle real Node/Chromium Job observation; first failure stops.

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
SCHEMA = "migrator-preview47-real-host-job-fixed-series/v1"


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
              "originalP47Unchanged": True, "hostCleanupIsNotOriginalPass": True,
              "historicalNov2AndNov21Failures": "preserved_unresolved",
              "productFixProven": False, "autoApply": False}
    summary = root / "series-summary.json"
    try:
        for ordinal in range(1, count + 1):
            index = f"{ordinal:03d}"
            env = dict(os.environ, P47_HOST_JOB_PROBE_INDEX=index)
            result = subprocess.run(
                [sys.executable,
                 str(repo / "scripts/p2_preview47_real_chromium_job_object_probe.py")],
                cwd=repo, env=env, check=False)
            receipt = root / ("cycle-" + index) / "real-job.json"
            cycle = None
            if receipt.is_file():
                try:
                    cycle = json.loads(receipt.read_text(encoding="utf-8"))
                except (ValueError, OSError):
                    cycle = {"status": "unreadable"}
            report["results"].append({
                "cycle": ordinal, "exitCode": result.returncode,
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
        report["status"] = "completed_no_host_remediation_proof_claimed"
        print("P47_REAL_HOST_JOB_SERIES_COMPLETED_NO_FIX_CLAIM")
    except BaseException:
        if report["status"] == "running":
            report["status"] = "failed"
        raise
    finally:
        evidence.atomic_json(summary, report)
        evidence.write_manifest(root)


if __name__ == "__main__":
    main()
