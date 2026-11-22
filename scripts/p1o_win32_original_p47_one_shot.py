#!/usr/bin/env python3
"""ONE source-exact original Windows P47 against an instrumented owned Chrome.

Only for a preapproved private isolated Windows builder with the P1-O binary
receipt. No preview/47 source edits, retries, forced cleanup, host Job Object,
privileged handles, foreign process handles or raw command-line logging.
"""
import argparse
import ctypes
from ctypes import wintypes
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import zipfile

import p1o_builder_and_witness_contract as contract
import p2_preview45_sink_close_process_ancestry as existing_observer

P47 = "scripts/p2_preview47_one_time_real_chromium.py"
STAGES = [
    "healthy_pass", "healthy_primary", "disconnect_primary",
    "disconnect_pass", "disconnect_primary_repeat",
]
WAIT_TIMEOUT = 258
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000


def require(ok, message):
    contract.check(ok, message)


def sha_file(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def native_identity(pid):
    """Only a previously identified test-owned Chrome PID is opened."""
    if os.name != "nt":
        raise RuntimeError("P1O_WINDOWS_WIN32_NATIVE_REQUIRED")
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)

    class FILETIME(ctypes.Structure):
        _fields_ = [("low", wintypes.DWORD), ("high", wintypes.DWORD)]

    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.GetProcessTimes.argtypes = [
        wintypes.HANDLE, ctypes.POINTER(FILETIME),
        ctypes.POINTER(FILETIME), ctypes.POINTER(FILETIME),
        ctypes.POINTER(FILETIME)]
    kernel.GetProcessTimes.restype = wintypes.BOOL
    kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    kernel.WaitForSingleObject.restype = wintypes.DWORD
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel.CloseHandle.restype = wintypes.BOOL
    handle = kernel.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not handle:
        return None
    try:
        created, exited, kernel_time, user = (
            FILETIME(), FILETIME(), FILETIME(), FILETIME())
        if not kernel.GetProcessTimes(handle, ctypes.byref(created),
                                      ctypes.byref(exited),
                                      ctypes.byref(kernel_time),
                                      ctypes.byref(user)):
            return None
        native = (int(created.high) << 32) | int(created.low)
        live = kernel.WaitForSingleObject(handle, 0) == WAIT_TIMEOUT
        return (str(native), bool(live))
    finally:
        kernel.CloseHandle(handle)


def poll_owned_chrome(chrome, original_pid, previously_owned):
    # The stock P47 observer's Windows CIM command relies on this env var
    # when comparing exact exe paths. Scope it to this subprocess only.
    previous = os.environ.get("MIGRATOR_CHROME_BIN")
    try:
        os.environ["MIGRATOR_CHROME_BIN"] = str(chrome)
        current = existing_observer.snapshot(str(chrome))
    finally:
        if previous is None:
            os.environ.pop("MIGRATOR_CHROME_BIN", None)
        else:
            os.environ["MIGRATOR_CHROME_BIN"] = previous
    snapshot = []
    for pid, (_parent, started, role) in current.items():
        if role != "chrome":
            continue
        # The exact image path is NOT sufficient ownership evidence. While
        # the original P47 parent is alive, require a real observed chain to
        # its own Python PID. After P47 exits, trust only exact PID + native
        # FILETIME bindings previously rooted in that observed ancestry.
        ancestry = existing_observer.ancestry(current, pid, original_pid)
        # Never even OPEN a native process handle for a foreign same-image
        # PID. A formerly owned PID may be queried only to disprove PID
        # reuse by comparing the native creation FILETIME.
        if ancestry is None and not any(prior_pid == pid
                                        for prior_pid, _ in previously_owned):
            continue
        binding = native_identity(pid)
        if binding is None:
            continue
        filetime, native_live = binding
        identity = (pid, filetime)
        if ancestry is None and identity not in previously_owned:
            continue
        if ancestry is not None:
            previously_owned.add(identity)
        snapshot.append({
            "pid": pid, "nativeFiletime": filetime,
            "startIdentity": started,
            "nativeLive": native_live,
            "observedOriginalAncestry": identity in previously_owned,
        })
    return snapshot


def current_boot_tick_ms():
    # ctypes defaults to 32-bit c_int, which silently wraps GetTickCount64.
    native = ctypes.WinDLL("kernel32", use_last_error=True)
    native.GetTickCount64.argtypes = []
    native.GetTickCount64.restype = ctypes.c_ulonglong
    return int(native.GetTickCount64())


def original_blob(core_root):
    result = subprocess.run([
        "git", "-C", str(core_root), "rev-parse", "HEAD:" + P47],
        text=True, capture_output=True, timeout=30)
    require(result.returncode == 0, "ORIGINAL_P47_GIT_PROVENANCE_UNAVAILABLE")
    return result.stdout.strip()


def zip_evidence(private_output):
    archive = private_output.with_suffix(".zip")
    require(not archive.exists(), "EVIDENCE_ZIP_OUTPUT_ALREADY_EXISTS")
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as z:
        for path in sorted(private_output.rglob("*")):
            if path.is_file() and not path.is_symlink():
                z.write(path, arcname=path.relative_to(private_output).as_posix())
    with zipfile.ZipFile(archive) as z:
        require(z.testzip() is None, "P47_PRIVATE_EVIDENCE_ZIP_CRC")
    return {"sha256": sha_file(archive), "bytes": archive.stat().st_size}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--core-checkout", type=Path, required=True)
    parser.add_argument("--chromium-exe", type=Path, required=True)
    parser.add_argument("--chrome-driver", type=Path, required=True)
    parser.add_argument("--build-receipt", type=Path, required=True)
    parser.add_argument("--fresh-private-evidence", type=Path, required=True)
    parser.add_argument("--approve-original-one-shot", action="store_true")
    args = parser.parse_args(argv)
    require(os.name == "nt" and sys.platform == "win32",
            "ONLY_WINDOWS_OWNED_BUILDER_CAN_RUN_P47")
    require(args.approve_original_one_shot,
            "EXPLICIT_ONE_SHOT_APPROVAL_REQUIRED")
    root = args.core_checkout.resolve(strict=True)
    chrome = args.chromium_exe.resolve(strict=True)
    driver = args.chrome_driver.resolve(strict=True)
    evidence = args.fresh_private_evidence.resolve()
    require(not evidence.exists() and not evidence.is_symlink(),
            "EVIDENCE_DESTINATION_MUST_BE_FRESH")
    require(root not in evidence.parents and evidence not in root.parents
            and chrome not in evidence.parents,
            "OWNED_EVIDENCE_MUST_BE_OUTSIDE_SOURCE")
    lock = contract.load_json(root / "docs/p1o-chromium156-toolchain.lock.json")
    contract.validate_lock(lock)
    receipt = contract.load_json(args.build_receipt)
    require(receipt.get("schema") ==
            "migrator-p1o-test-owned-build-receipt/v1"
            and receipt.get("status") == "instrumented_test_binary_compiled"
            and receipt.get("compiled") is True
            and receipt.get("executed") is False
            and receipt.get("productFixProven") is False
            and receipt.get("originalP47Changed") is False
            and receipt.get("sourceGitSha") == lock["source"]["commit"]
            and receipt.get("chromeExeSha256") == sha_file(chrome),
            "INSTRUMENTED_CHROME_BINARY_ATTESTATION_MISSING")
    p47_blob = original_blob(root)
    require(p47_blob == lock["p47"]["originalGitBlobSha1"],
            "P47_ORIGINAL_SOURCE_GIT_BLOB_CHANGED")
    p47_file = root / P47
    p47_sha = sha_file(p47_file)
    require(receipt.get("originalP47Sha256") == p47_sha,
            "P47_ORIGINAL_SOURCE_BYTES_CHANGED")
    driver_version = subprocess.run(
        [str(driver), "--version"], capture_output=True,
        text=True, timeout=20)
    require(driver_version.returncode == 0 and
            "156.0.8078.80" in driver_version.stdout,
            "CHROMEDRIVER_IS_NOT_PINNED_156")
    require(root.is_dir() and chrome.is_file() and driver.is_file(),
            "ORIGINAL_RUNTIME_DEPENDENCIES_MISSING")
    # Original existing .NET Core CLI must have been built before this one-shot.
    require((root / "Migrator.AssemblyCli/bin/Release/net10.0/"
            "Migrator.AssemblyCli.dll").is_file(),
            "ORIGINAL_P47_CORE_CLI_NOT_BUILT")

    evidence.mkdir(parents=True, exist_ok=False)
    handler_trace = evidence / "handler-events.jsonl"
    env = dict(os.environ,
        MIGRATOR_CHROME_BIN=str(chrome),
        MIGRATOR_CHROMEDRIVER_BIN=str(driver),
        MIGRATOR_CHROME_VERSION="156.0.8078.80",
        MIGRATOR_CHROMEDRIVER_VERSION="156.0.8078.80",
        MIGRATOR_P1_TEST_WITNESS_FILE=str(handler_trace),
        DOTNET_CLI_TELEMETRY_OPTOUT="1")
    command = [sys.executable, str(p47_file)]
    started = time.monotonic()
    # Only ONE invocation. Never rerun a passed/failed stage for a green label.
    original = subprocess.Popen(command, cwd=str(root), env=env,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
        encoding="utf-8", errors="replace")
    samples = []
    owned_native_identities = set()
    query_fault = None
    try:
        while original.poll() is None:
            try:
                samples.extend(poll_owned_chrome(chrome, original.pid, owned_native_identities))
            except (subprocess.SubprocessError, RuntimeError, OSError) as ex:
                query_fault = type(ex).__name__
                break
            time.sleep(.4)
        # Do not kill an active original P47. Inability to observe is UNKNOWN;
        # allow it to complete naturally so the original first-FAIL remains.
        stdout, stderr = original.communicate()
    finally:
        # A final query is AFTER P47's own 12s gate and after original exit.
        try:
            last = poll_owned_chrome(chrome, original.pid, owned_native_identities)
        except (subprocess.SubprocessError, RuntimeError, OSError):
            last = []
    # stdout/stderr may contain paths and error details, kept PRIVATE ONLY.
    (evidence / "original-p47-stdout.private.txt").write_text(
        stdout, encoding="utf-8")
    (evidence / "original-p47-stderr.private.txt").write_text(
        stderr, encoding="utf-8")
    p47_artifacts = root / "artifacts/pom-preview47"
    if p47_artifacts.is_dir():
        # Copy original emitted files byte-for-byte, no modification/renaming.
        original_out = evidence / "original-p47-unaltered"
        original_out.mkdir()
        for file in sorted(p47_artifacts.iterdir()):
            if file.is_file() and not file.is_symlink():
                (original_out / file.name).write_bytes(file.read_bytes())
    ack_stages = [s for s in STAGES if (
        p47_artifacts / ("ack-" + s)).is_file()] if p47_artifacts.exists() else []
    trx_stages = [s for s in STAGES if (
        p47_artifacts / (s + ".trx")).is_file()] if p47_artifacts.exists() else []
    # Last original successfully ACKed disconnect stage is a candidate.
    last_disconnected = ack_stages[-1] if ack_stages else None
    fail = "P47_OWNED_OBSERVED_CHILD_PIDS_SURVIVED" in stderr
    native_live = [s for s in last if s["nativeLive"]]
    parsed = {}
    trace_correlates = None
    if handler_trace.is_file():
        try:
            parsed = contract.parse_handler_events(
                handler_trace.read_text(encoding="utf-8").splitlines())
        except (contract.EvidenceError, ValueError, OSError):
            parsed = {}
    for record in native_live:
        candidate = (record["pid"], record["nativeFiletime"])
        if candidate in parsed:
            trace_correlates = record
            break
    gate = {
        "schema": contract.GATE_SCHEMA,
        "originalP47ScriptGitBlobSha1": p47_blob,
        "originalGateSeconds": 12,
        "originalNodeSigterm": bool(last_disconnected),
        "stage": last_disconnected,
        "failFast": all(s not in trx_stages
                       for s in STAGES[STAGES.index(last_disconnected) + 1:])
                    if last_disconnected else False,
        "hostJobObjectUsed": False,
        "afterGateNativeLive": bool(fail and trace_correlates),
        "handlerPid": trace_correlates["pid"] if trace_correlates else 0,
        "handlerFiletime": trace_correlates["nativeFiletime"]
        if trace_correlates else "0",
        "gateTickMs": current_boot_tick_ms(),
    }
    # Never claim that an unrelated Chrome survivor is an internal handler.
    result = {
        "schema": "migrator-p1o-instrumented-p47-one-shot/v1",
        "status": "original_fail_with_bound_native_live_handler"
                  if gate["afterGateNativeLive"] else
                  "prerequisite_failure"
                  if query_fault or not handler_trace.exists()
                  or (original.returncode != 0 and not fail)
                  else "not_observed",
        "originalP47ExitCode": original.returncode,
        "originalP47SourceGitBlobSha1": p47_blob,
        "originalP47SourceSha256": p47_sha,
        "originalP47RunCount": 1,
        "originalGateSeconds": 12,
        "originalNodeSigtermAckStages": ack_stages,
        "originalTrxStages": trx_stages,
        "originalFailFastWitness": fail,
        "queryFaultType": query_fault or "none",
        "instrumentedChromeSha256": sha_file(chrome),
        "chromeDriverSha256": sha_file(driver),
        "privateEventFileSha256": sha_file(handler_trace)
                                  if handler_trace.is_file() else None,
        "originalAncestryBoundNativeIdentitiesCount": len(owned_native_identities),
        "nativeLiveChromeAfterOriginalExit": native_live,
        "nativeHandlerBindingAtOriginalExit": trace_correlates,
        "originalP47Changed": False,
        "hostJobObjectUsed": False,
        "rootCauseProven": False,
        "productFixProven": False,
        "fixClaim": False,
        "releaseAllowed": False,
        "elapsedSecondsTotal": round(time.monotonic() - started, 3),
    }
    (evidence / "original-p47-native-gate-candidate.json").write_text(
        json.dumps(gate, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    # If any original input is missing, parser must classify it as UNKNOWN,
    # not fabricate a completed private registration/OnProcessEnd chain.
    try:
        result["privateHandlerAssessment"] = contract.evaluate_witness(
            lock,
            handler_trace.read_text(encoding="utf-8").splitlines()
            if handler_trace.is_file() else None, gate)
    except (contract.EvidenceError, ValueError, OSError) as ex:
        result["privateHandlerAssessment"] = {
            "status": "trace_invalid_unknown",
            "errorKind": type(ex).__name__,
            "rootCauseProven": False,
            "productFixProven": False,
        }
    receipt_path = evidence / "p1o-original-p47-private-receipt.json"
    receipt_path.write_text(
        json.dumps(result, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    result["evidenceZip"] = zip_evidence(evidence)
    # Fixed, private one-shot receipt, never a published artifact by default.
    print(json.dumps({key: result[key] for key in (
        "schema", "status", "originalP47RunCount", "originalP47ExitCode",
        "originalGateSeconds", "rootCauseProven", "productFixProven",
        "fixClaim", "evidenceZip")}, sort_keys=True))
    # Preserve original failure status, no cosmetic green.
    if original.returncode != 0:
        raise SystemExit(original.returncode or 1)
    if result["status"] != "not_observed":
        raise SystemExit(33)


if __name__ == "__main__":
    main()
