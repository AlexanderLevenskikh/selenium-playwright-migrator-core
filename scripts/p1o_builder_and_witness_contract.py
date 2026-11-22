#!/usr/bin/env python3
"""P1-O source-pinned builder and private Win32 handler witness gate.

Read-only, portable: never launches Chromium, reads process handles, changes P47,
registers a runner or infers a product fix. All input is untrusted evidence.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess

SCHEMA = "migrator-p1o-chromium156-builder-lock/v1"
EVENT_SCHEMA = "migrator-p1n-test-owned-handler-event/v1"
GATE_SCHEMA = "migrator-p1o-original-p47-native-gate/v1"
PHASES = {
    "initial_register", "pipe_register", "mode", "process_end_callback",
    "completion_posted", "completion_post_failed",
    "completion_consumed", "client_erased", "upload_begin", "upload_end",
}
FIELDS = {
    "schema", "phase", "seq", "tickMs", "handlerPid", "handlerFiletime",
    "clientPid", "clientFiletime", "registeredCount",
    "persistentMode", "uploadInFlight",
}
CLIENT_PHASES = {
    "initial_register", "pipe_register", "process_end_callback",
    "completion_posted", "completion_post_failed",
    "completion_consumed", "client_erased",
}
HASH40 = re.compile(r"[0-9a-f]{40}\Z")
HASH64 = re.compile(r"[0-9a-f]{64}\Z")


class EvidenceError(ValueError):
    pass


def check(condition, code):
    if not condition:
        raise EvidenceError("P1O_FAIL_CLOSED:" + code)


def sha256_bytes(data):
    return hashlib.sha256(data).hexdigest()


def git_blob_sha1(data):
    return hashlib.sha1(b"blob " + str(len(data)).encode("ascii") +
                        b"\0" + data).hexdigest()


def load_json(path):
    file = Path(path)
    check(file.is_file() and not file.is_symlink() and
          0 < file.stat().st_size < 1_000_000, "JSON_FILE_MISSING_UNSAFE_OVERSIZED")
    return json.loads(file.read_text(encoding="utf-8"))


def validate_lock(lock):
    check(isinstance(lock, dict) and lock.get("schema") == SCHEMA,
          "LOCK_SCHEMA_UNKNOWN")
    source = lock.get("source", {})
    build = lock.get("builder", {})
    p47 = lock.get("p47", {})
    check(source.get("tag") == "156.0.8078.80"
          and source.get("commit") ==
              "fb096c0011f9d50f0a264a9376420cdea9c421e3"
          and source.get("crashpadRevision") ==
              "53c4dcfae02976fe5c1b0b9e5347914b1ccfcd9f",
          "CHROMIUM_OR_CRASHPAD_SOURCE_DRIFT")
    check(source.get("externalDepotToolsCommitFromDeps") ==
          "a07c06fe67a1a9d64ac4728df3a11c1ceb0cf73e"
          and build.get("depotTools", {}).get("exactCommit") ==
          source["externalDepotToolsCommitFromDeps"],
          "DEPOT_TOOLS_COMMIT_DRIFT")
    check(build.get("os") == "Windows"
          and build.get("architecture") == "AMD64"
          and build.get("scratchFilesystem") == "NTFS"
          and build.get("minScratchFreeGiB", 0) >= 300
          and build.get("minRamGiB", 0) >= 32
          and build.get("visualStudio", {}).get("version") == "18.11.4"
          and build["visualStudio"].get("microsoftReleaseBuild") == "12402.416"
          and build.get("windowsSdk", {}).get("kitVersion") == "10.0.28000.0"
          and build["windowsSdk"].get("releaseVersion") == "10.0.28000.2270"
          and build["windowsSdk"].get("minDebuggingToolsVersion") ==
              "10.0.26100.3323"
          and build.get("depotTools", {}).get("autoUpdate") is False
          and build.get("allowNetworkDuringBuild") is False
          and build.get("noGlobalEnvironmentOrCredentialChanges") is True
          and build.get("preprovisionedAndIsolatedRequired") is True,
          "BUILDER_PIN_OR_ISOLATION_WEAKENED")
    check(p47.get("path") == "scripts/p2_preview47_one_time_real_chromium.py"
          and p47.get("originalGitBlobSha1") ==
              "f0d44e21d40b2cbf16397bc400671f77232004db"
          and p47.get("originalGateSeconds") == 12
          and p47.get("originalFault") ==
              "os.kill(driver_pid, signal.SIGTERM)"
          and p47.get("originalStageOrder") == [
              "healthy_pass", "healthy_primary", "disconnect_primary",
              "disconnect_pass", "disconnect_primary_repeat"]
          and p47.get("firstFailStopsSequence") is True
          and p47.get("noRetries") is True
          and p47.get("noWin32HostJobSubstitution") is True,
          "ORIGINAL_P47_GATE_FAULT_OR_ORDER_WEAKENED")
    pins = source.get("gitBlobs", {})
    check(isinstance(pins, dict) and
          len(pins) >= 7 and
          all(isinstance(p, str) and
              not Path(p).is_absolute() and
              not any(x in (".", "..", "") for x in p.split("/"))
              and HASH40.fullmatch(h)
              for p, h in pins.items()),
          "SOURCE_GIT_BLOB_LOCK_INVALID")
    expected = {
        "DEPS": "b902d611fe6a71d28add17ec663faa48e10f877d",
        "docs/windows_build_instructions.md":
            "224fe5df98bcdb612e1682791957327ea06b5b8a",
        "third_party/crashpad/crashpad/util/win/exception_handler_server.cc":
            "8af53a62b088fececd558f1436a84d4c2c4feabd",
        "third_party/crashpad/crashpad/handler/crash_report_upload_thread.cc":
            "bec8714263779d57bfff5ba83cfe26776acf3732",
    }
    check(all(pins.get(p) == h for p, h in expected.items()),
          "PINNED_CHROMIUM_FILES_CHANGED")
    args = lock.get("gn", {})
    check(args.get("buildTarget") == "chrome"
          and args.get("explicitTestMacro") ==
              "MIGRATOR_P1_TEST_OWNED_WITNESS"
          and args.get("args") == [
              "is_debug = false", "is_component_build = false",
              "is_official_build = false", "symbol_level = 1",
              'target_cpu = "x64"', "use_remoteexec = false",
              "use_siso = false"],
          "GN_BUILDER_SCOPE_CHANGED")
    state = lock.get("state", {})
    check(state.get("productFixProven") is False
          and state.get("releaseAllowed") is False,
          "UNAPPROVED_PRODUCT_FIX_OR_RELEASE")
    return {"status": "source_and_builder_lock_checked",
            "sourceCommit": source["commit"],
            "crashpadRevision": source["crashpadRevision"],
            "depotToolsCommit": build["depotTools"]["exactCommit"],
            "originalP47Changed": False, "fixClaim": False}


def assess_builder(lock, facts):
    """Do not accept a floating runner label as a pinned Windows build host."""
    validate_lock(lock)
    check(isinstance(facts, dict), "BUILDER_FACTS_REQUIRED")
    expected = lock["builder"]
    reasons = []
    checks = [
        ("os", "WINDOWS_REQUIRED"),
        ("architecture", "AMD64_REQUIRED"),
        ("scratchFilesystem", "NTFS_REQUIRED"),
    ]
    for key, code in checks:
        if facts.get(key) != expected[key]:
            reasons.append(code)
    for key, code in [
        ("scratchFreeGiB", "SCRATCH_GIB_INSUFFICIENT"),
        ("ramGiB", "RAM_GIB_INSUFFICIENT"),
    ]:
        needed = expected["minScratchFreeGiB"] if key == "scratchFreeGiB" \
            else expected["minRamGiB"]
        val = facts.get(key)
        if isinstance(val, bool) or not isinstance(val, (float, int)) \
                or val < needed:
            reasons.append(code)
    exact = {
        "visualStudioVersion": expected["visualStudio"]["version"],
        "visualStudioBuild": expected["visualStudio"]["microsoftReleaseBuild"],
        "windowsSdk": expected["windowsSdk"]["kitVersion"],
        "depotToolsCommit": expected["depotTools"]["exactCommit"],
        "chromiumCommit": lock["source"]["commit"],
        "crashpadRevision": lock["source"]["crashpadRevision"],
    }
    for key, wanted in exact.items():
        if facts.get(key) != wanted:
            reasons.append("PIN_MISMATCH_" + key.upper())
    if facts.get("isolatedOwnedHost") is not True:
        reasons.append("HOST_OWNERSHIP_NOT_ATTESTED")
    if facts.get("depotToolsAutoUpdate") is not False:
        reasons.append("DEPOT_TOOLS_AUTO_UPDATE_NOT_DISABLED")
    if facts.get("sourceTreeClean") is not True:
        reasons.append("PINNED_SOURCE_NOT_CLEAN")
    return {
        "schema": "migrator-p1o-builder-assessment/v1",
        "status": "ready_for_isolated_compile" if not reasons
                  else "prerequisite_failure",
        "reasons": sorted(set(reasons)),
        "compiled": False,
        "executed": False,
        "fixClaim": False,
        "releaseAllowed": False,
    }


def check_pinned_checkout(lock, root):
    """Read-only immutable tracked source bytes: no synthesis from tag text."""
    validate_lock(lock)
    folder = Path(root)
    check(folder.is_dir() and not folder.is_symlink(), "SOURCE_ROOT_UNSAFE")
    head = subprocess.run(["git", "-C", str(folder), "rev-parse", "HEAD"],
                          capture_output=True, text=True, timeout=30)
    check(head.returncode == 0 and head.stdout.strip() ==
          lock["source"]["commit"], "WRONG_CHROMIUM_SOURCE_COMMIT")
    evidence = []
    for relative, pinned_sha in sorted(lock["source"]["gitBlobs"].items()):
        path = folder
        for part in relative.split("/"):
            path = path / part
            check(not path.is_symlink(), "SYMLINKED_PINNED_SOURCE")
        check(path.is_file() and path.stat().st_size < 1024 * 1024,
              "PINNED_BLOB_MISSING_OVERSIZED")
        data = path.read_bytes()
        check(git_blob_sha1(data) == pinned_sha,
              "WRONG_PINNED_SOURCE_BLOB_" + relative)
        evidence.append({
            "path": relative, "gitBlobSha1": pinned_sha,
            "sha256": sha256_bytes(data)
        })
    return {"status": "pinned_source_bytes_checked",
            "sourceCommit": lock["source"]["commit"],
            "files": evidence, "compiled": False, "fixClaim": False}


def parse_handler_events(lines):
    """Return bounded per-process events, without guessing missing registrations."""
    handlers = {}
    check(len(lines) <= 8192, "TOO_MANY_EVENTS")
    for line in lines:
        check(isinstance(line, str) and 0 < len(line) < 1024,
              "HANDLER_EVENT_TRUNCATED_OVERSIZED")
        try:
            event = json.loads(line)
        except json.JSONDecodeError as ex:
            raise EvidenceError("P1O_FAIL_CLOSED:INVALID_EVENT_JSON") from ex
        check(isinstance(event, dict) and set(event) == FIELDS,
              "EVENT_FIELDS_UNEXPECTED_OR_SENSITIVE")
        check(event["schema"] == EVENT_SCHEMA and
              event["phase"] in PHASES, "UNKNOWN_HANDLER_EVENT")
        for key in ("seq", "tickMs", "handlerPid", "registeredCount",
                    "persistentMode", "uploadInFlight", "clientPid"):
            check(type(event[key]) is int, "EVENT_INTEGER_REQUIRED_" + key)
        for key in ("handlerFiletime", "clientFiletime"):
            check(isinstance(event[key], str)
                  and re.fullmatch(r"0|[1-9][0-9]{0,19}", event[key]),
                  "INVALID_NATIVE_FILETIME")
        check(0 < event["seq"] <= 512 and
              event["tickMs"] >= 0 and event["handlerPid"] > 0
              and int(event["handlerFiletime"]) > 0
              and event["registeredCount"] >= -1
              and event["persistentMode"] in (-1, 0, 1)
              and event["uploadInFlight"] in (-1, 0, 1)
              and event["clientPid"] >= 0, "EVENT_VALUES_INVALID")
        if event["phase"] in CLIENT_PHASES:
            check(event["clientPid"] > 0 and
                  int(event["clientFiletime"]) > 0,
                  "CLIENT_NATIVE_IDENTITY_MISSING")
        else:
            check(event["clientPid"] == 0 and
                  event["clientFiletime"] == "0",
                  "NONCLIENT_EVENT_LEAKED_IDENTITY")
        identity = (event["handlerPid"], event["handlerFiletime"])
        history = handlers.setdefault(identity, [])
        check(not history or (
            event["seq"] == history[-1]["seq"] + 1
            and event["tickMs"] >= history[-1]["tickMs"]),
            "HANDLER_SEQUENCE_OR_MONOTONIC_GAP")
        check(history or event["seq"] == 1, "FIRST_HANDLER_EVENT_MISSING")
        history.append(event)
    return handlers


def analyze_handler(events, at_tick_ms):
    """Trace quality matters: an absent callback is never proof it did not fire."""
    check(type(at_tick_ms) is int and at_tick_ms >= 0,
          "GATE_TICK_REQUIRED")
    check(events, "HANDLER_EVENTS_MISSING")
    phases = [e["phase"] for e in events]
    reg = {}
    posts = set()
    consumed = set()
    uploads = 0
    saw_upload = False
    mode = None
    issues = []
    for e in events:
        if e["tickMs"] > at_tick_ms:
            break
        phase = e["phase"]
        cid = (e["clientPid"], e["clientFiletime"])
        if phase in ("initial_register", "pipe_register"):
            if cid in reg:
                issues.append("duplicate_client_registration")
            reg[cid] = e["tickMs"]
            if e["registeredCount"] != len(reg):
                issues.append("registration_count_mismatch")
        elif phase == "mode":
            if mode is not None:
                issues.append("duplicate_mode")
            mode = e["persistentMode"] if e["persistentMode"] in (0, 1) else None
        elif phase == "completion_posted":
            posts.add(cid)
        elif phase == "completion_post_failed":
            issues.append("completion_port_post_failed")
        elif phase == "completion_consumed":
            consumed.add(cid)
            if cid not in posts:
                issues.append("completion_without_captured_post")
        elif phase == "client_erased":
            if cid not in reg or cid not in consumed:
                issues.append("erasure_without_observed_registration_completion")
            reg.pop(cid, None)
            if e["registeredCount"] != len(reg):
                issues.append("client_erasure_count_mismatch")
        elif phase == "upload_begin":
            uploads += 1
            saw_upload = True
        elif phase == "upload_end":
            uploads -= 1
            if uploads < 0:
                issues.append("upload_end_without_begin")
    coverage_at_gate = any(e["tickMs"] >= at_tick_ms for e in events)
    trace_complete = events[-1]["seq"] < 512 and not issues
    return {
        "status": "bounded_private_handler_events_inspected",
        "observedHandler": {
            "pid": events[0]["handlerPid"],
            "nativeFiletime": events[0]["handlerFiletime"],
        },
        "traceHadCriticalErrors": bool(issues),
        "traceProblems": sorted(set(issues)),
        "sequenceNearCap": events[-1]["seq"] >= 512,
        "privateRegisteredClientsObservedByGate": [
            {"pid": pid, "nativeFiletime": ft}
            for pid, ft in sorted(reg)
        ],
        "persistentModeObserved": mode if mode is not None else "unknown",
        "completionPostsObserved": len(posts),
        "completionConsumptionObserved": len(consumed),
        "uploadAtGate": (
            "in_flight_observed_on_test_owned_timeline"
            if saw_upload and uploads > 0 and coverage_at_gate and trace_complete
            else "not_in_flight_in_observed_window"
            if saw_upload and uploads == 0 and coverage_at_gate
                 and trace_complete
            else "unknown"
        ),
        "traceCoversGateTick": coverage_at_gate and trace_complete,
        "rootCauseProven": False,
        "productFixProven": False,
    }


def evaluate_witness(lock, lines=None, gate=None):
    validate_lock(lock)
    default = {
        "schema": "migrator-p1o-direct-runtime-witness-assessment/v1",
        "status": "not_run",
        "directHandlerEvidence": "unknown",
        "originalP47AfterGateNativeLive": "not_run",
        "stockChromeIdentity": "historical_binary_unknown",
        "productFixProven": False,
        "rootCauseProven": False,
        "releaseAllowed": False,
        "fixClaim": False,
    }
    if lines is None or gate is None:
        return default
    handlers = parse_handler_events(lines)
    check(isinstance(gate, dict) and gate.get("schema") == GATE_SCHEMA,
          "GATE_RECORD_MISSING_OR_UNKNOWN")
    p47 = lock["p47"]
    check(gate.get("originalP47ScriptGitBlobSha1") ==
          p47["originalGitBlobSha1"]
          and gate.get("originalGateSeconds") == 12
          and gate.get("originalNodeSigterm") is True
          and gate.get("stage") in p47["originalStageOrder"][2:]
          and gate.get("failFast") is True
          and gate.get("hostJobObjectUsed") is False,
          "P47_ORIGINAL_GATE_OR_FAULT_NOT_PROVEN")
    if not gate.get("afterGateNativeLive"):
        return {**default, "status": "not_observed",
                "originalP47AfterGateNativeLive": "not_observed"}
    check(type(gate.get("handlerPid")) is int
          and isinstance(gate.get("handlerFiletime"), str)
          and gate["handlerPid"] > 0
          and gate["handlerFiletime"].isdigit()
          and type(gate.get("gateTickMs")) is int,
          "NATIVE_LIVE_GATE_IDENTITY_NOT_BOUND")
    exact = (gate["handlerPid"], gate["handlerFiletime"])
    if exact not in handlers:
        return {**default, "status": "handler_identity_not_correlated",
                "originalP47AfterGateNativeLive": "observed",
                "directHandlerEvidence": "unknown"}
    observed = analyze_handler(handlers[exact], gate["gateTickMs"])
    return {
        **default,
        "status": ("test_owned_runtime_correlation_candidate"
                   if observed["traceCoversGateTick"]
                   and not observed["traceHadCriticalErrors"]
                   else "partial_handler_trace_unknown"),
        "originalP47AfterGateNativeLive": "observed",
        "directHandlerEvidence": observed,
        "stockChromeIdentity": "historical_binary_unknown",
    }


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--lock", type=Path, required=True)
    ap.add_argument("--source-root", type=Path)
    ap.add_argument("--builder-facts", type=Path)
    ap.add_argument("--handler-jsonl", type=Path)
    ap.add_argument("--native-gate", type=Path)
    ap.add_argument("--out", type=Path)
    ap.add_argument("--require-builder-ready", action="store_true")
    args = ap.parse_args()
    lock = load_json(args.lock)
    report = {"lock": validate_lock(lock),
              "builder": {"status": "prerequisite_failure",
                          "reason": "no_attested_owned_windows_builder"},
              "witness": evaluate_witness(lock)}
    if args.source_root:
        report["source"] = check_pinned_checkout(lock, args.source_root)
    if args.builder_facts:
        report["builder"] = assess_builder(lock, load_json(args.builder_facts))
    if args.handler_jsonl or args.native_gate:
        check(args.handler_jsonl is not None and args.native_gate is not None,
              "EVENTS_AND_GATE_REQUIRED_TOGETHER")
        lines = args.handler_jsonl.read_text(encoding="utf-8").splitlines()
        report["witness"] = evaluate_witness(
            lock, lines, load_json(args.native_gate))
    report["releaseAllowed"] = False
    report["fixClaim"] = False
    raw = json.dumps(report, indent=2, sort_keys=True) + "\n"
    if args.out:
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(raw, encoding="utf-8")
    else:
        print(raw, end="")
    if args.require_builder_ready and report["builder"]["status"] != \
            "ready_for_isolated_compile":
        raise SystemExit("P1O_PREREQUISITE_FAILURE: pinned builder unavailable")


if __name__ == "__main__":
    main()
