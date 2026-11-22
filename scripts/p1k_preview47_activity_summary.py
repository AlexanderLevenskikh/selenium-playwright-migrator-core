#!/usr/bin/env python3
"""P1-K: read-only offline summary of ORIGINAL P47 stage receipts.

No process access, additional polling, browser/test execution or mutations.
Only source-owned exact PID + creation FILETIME can be compared across phases.
CPU/I/O delta is NOT evidence of a registered client or pending upload.
"""
import json
from pathlib import Path
import sys

SCHEMA = "migrator-preview47-p1k-native-accounting/v1"
METRICS = ("cpuKernel100ns", "cpuUser100ns", "handleCount", "ioReadOperations",
           "ioWriteOperations", "ioOtherOperations",
           "ioReadBytes", "ioWriteBytes", "ioOtherBytes")
ROLES = ("crashpad-handler", "storage.mojom.StorageService")


def safe_modes(raw):
    if not isinstance(raw, dict):
        return None
    modes = ("initialClientData", "pipeName", "monitorSelf",
             "noPeriodicTasks", "uploadUrlConfigured")
    valid = ("present", "absent", "unknown", "not_applicable")
    return {k: raw[k] if raw.get(k) in valid else "unknown" for k in modes}


def summarize_stage(stage, events):
    bound = {}
    for source in stage.get("observedOwnedAncestry") or []:
        key = (source.get("observedPid"), source.get("startIdentity"))
        ft = source.get("creationFileTime")
        if not source.get("wasInBaselineWithOriginalIdentity") and (
                isinstance(key[0], int) and key[1] and
                isinstance(ft, str) and ft.isdigit()):
            bound[key] = ft
    fault = next((i for i, e in enumerate(events) if
                  e.get("phase") == "driver_termination_before_signal"), None)
    exited = next((i for i, e in enumerate(events) if
                   e.get("phase") == "runner_exited"), None)
    if fault is None or exited is None or exited <= fault:
        return []
    tracks = {}
    for idx, event in enumerate(events):
        phase = event.get("phase")
        if phase not in ("runner_active", "passive_cleanup_observation"):
            continue
        moment = "pre_fault" if idx < fault else (
            "after_fault_before_runner_exit" if idx < exited else "post_runner_gate")
        for row in event.get("processes") or []:
            key = (row.get("pid"), row.get("startIdentity"))
            ft, native = bound.get(key), row.get("native") or {}
            if not ft or native.get("state") != "live" or native.get("creationFileTime") != ft:
                continue
            if native.get("expectedCreationFileTime") not in (None, ft):
                continue
            kind = row.get("chromiumRole")
            if kind == "utility":
                kind = row.get("chromiumUtilitySubtype")
            if row.get("kind") != "chrome" or kind not in ROLES:
                continue
            activity = native.get("activityCounters") or {}
            track = tracks.setdefault((key[0], ft), {
                "pid": key[0], "creationFileTime": ft, "chromiumRole": kind,
                "crashpadModes": safe_modes(row.get("crashpadSwitches")),
                "observations": []})
            track["observations"].append({
                "phase": moment, "elapsedSec": event.get("elapsedSec"),
                "status": activity.get("status"),
                "metrics": {m: activity.get(m) for m in METRICS}})
    result = []
    for track in tracks.values():
        points = track.pop("observations")
        first = next((p for p in reversed(points) if
                      p["phase"] == "pre_fault" and p["status"] == "observed"), None)
        last = next((p for p in reversed(points) if
                     p["phase"] == "post_runner_gate" and p["status"] == "observed"), None)
        delta = {}
        if first and last:
            for m in METRICS:
                b, a = first["metrics"][m], last["metrics"][m]
                if isinstance(a, int) and isinstance(b, int) and (
                        m == "handleCount" or a >= b):
                    delta[m] = {"before": b, "after": a, "delta": a - b}
                else:
                    delta[m] = None
        track.update(
            status="source_attested_live_delta_observed" if first and last and
                   all(delta.get(m) is not None for m in METRICS)
                   else "source_attested_but_not_comparable",
            originalPresenceGate=stage.get("originalPresenceGate"),
            observationPhases=sorted({p["phase"] for p in points}),
            sampleCount=len(points), nativeCounterDeltas=delta,
            firstSampleElapsedSec=points[0]["elapsedSec"],
            lastSampleElapsedSec=points[-1]["elapsedSec"],
            registeredClientsProven=False, pendingUploadProven=False,
            rootCauseProven=False)
        result.append(track)
    return sorted(result, key=lambda x: (x["pid"], x["creationFileTime"]))


def summarize(root):
    root = Path(root)
    traces = sorted(root.rglob("process-*.jsonl")) if root.is_dir() else []
    issues, data = [], []
    if len(traces) > 66:
        issues.append("TRACE_COUNT_EXCEEDED")
        traces = []
    for trace in traces:
        stage_path = trace.with_suffix(".json")
        if not stage_path.is_file() or trace.stat().st_size > 12 * 1024 * 1024:
            issues.append("STAGE_REPORT_MISSING_OR_TOO_LARGE")
            continue
        try:
            rows = trace.read_text(encoding="utf-8").splitlines()
            if len(rows) > 4096:
                issues.append("STAGE_EVENTS_EXCEEDED")
                continue
            stage = json.loads(stage_path.read_text(encoding="utf-8"))
            events = [json.loads(row) for row in rows]
            for item in summarize_stage(stage, events):
                item["stagePath"] = stage_path.relative_to(root).as_posix()
                data.append(item)
        except (OSError, ValueError, UnicodeError, KeyError, TypeError):
            issues.append("STAGE_EVIDENCE_UNREADABLE")
        if len(data) > 2048:
            issues.append("SOURCE_TRACK_BUDGET_EXCEEDED")
            data = []
            break
    if not traces:
        issues.append("ORIGINAL_PROCESS_TIMELINES_MISSING")
    observed = any(x["status"] == "source_attested_live_delta_observed" for x in data)
    status = "observed_diagnostic_only" if observed else "insufficient_observations"
    if any(x.endswith(("EXCEEDED", "UNREADABLE", "TOO_LARGE")) for x in issues):
        status = "prerequisite_failure"
    return {
        "schema": SCHEMA, "status": status,
        "sourceTrackedProcessCount": len(data),
        "sourceTrackedProcesses": data, "issues": sorted(set(issues)),
        "originalP47Modified": False, "newProcessSampling": False,
        "registeredClientsProven": False, "pendingCrashUploadProven": False,
        "productFixProven": False, "postGateHostRemediationProven": False,
        "fixClaim": False}


def main():
    if len(sys.argv) != 3:
        raise SystemExit("usage: p1k_preview47_activity_summary.py <original P47 dir> <output dir>")
    root, out = Path(sys.argv[1]), Path(sys.argv[2])
    if out.resolve() == root.resolve() or root.resolve() in out.resolve().parents:
        raise SystemExit("P1K_OUTPUT_MUST_NOT_OVERLAP_ORIGINAL_P47")
    out.mkdir(parents=True, exist_ok=True)
    result = summarize(root)
    (out / "native-accounting-summary.json").write_text(
        json.dumps(result, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": result["status"], "issues": result["issues"],
                      "sourceTrackedProcessCount": result["sourceTrackedProcessCount"],
                      "fixClaim": False}, sort_keys=True))


if __name__ == "__main__":
    main()
