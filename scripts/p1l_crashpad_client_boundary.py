#!/usr/bin/env python3
"""P1-L: offline original-P47 registered-client evidence boundary.

Never opens processes, emits raw source/pipe/commands, or equates a process
tree with Crashpad client registration. Accepts SHA-pinned original ZIPs.
"""
import argparse
from datetime import datetime
import hashlib
import json
from pathlib import Path, PurePosixPath
import zipfile

SCHEMA = "migrator-p1l-original-client-boundary/v1"
LIVE_WAIT = 258
MAX_ZIP_BYTES = 40 * 1024 * 1024
MAX_MEMBERS = 2000
MAX_RECEIPTS = 100


class Unverified(ValueError):
    pass


def demand(condition, code):
    if not condition:
        raise Unverified(code)


def json_member(archive, name):
    return json.loads(archive.read(name).decode("utf-8"))


def verify_original_archive(path, expected_sha):
    data = Path(path).read_bytes()
    demand(len(data) <= MAX_ZIP_BYTES and len(expected_sha) == 64
           and hashlib.sha256(data).hexdigest() == expected_sha,
           "PINNED_ORIGINAL_ZIP_SHA_MISMATCH_OR_SIZE")
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        demand(0 < len(names) <= MAX_MEMBERS and len(names) == len(set(names)),
               "ZIP_MEMBER_COUNT_OR_DUPLICATE")
        demand(all(not n.endswith("/") and not PurePosixPath(n).is_absolute()
                   and ".." not in PurePosixPath(n).parts and "\\" not in n
                   and archive.getinfo(n).file_size <= MAX_ZIP_BYTES
                   for n in names), "UNSAFE_OR_OVERSIZED_ZIP_MEMBER")
        demand(archive.testzip() is None, "ORIGINAL_ZIP_CRC_MISMATCH")
        demand("SHA256SUMS.json" in names, "ORIGINAL_SHA256SUMS_MISSING")
        root = json_member(archive, "SHA256SUMS.json")
        demand(isinstance(root, dict)
               and set(root) == set(names) - {"SHA256SUMS.json"},
               "ORIGINAL_ROOT_MANIFEST_COVERAGE_MISMATCH")
        for filename, digest in root.items():
            demand(isinstance(digest, str) and len(digest) == 64
                   and hashlib.sha256(archive.read(filename)).hexdigest() == digest,
                   "ORIGINAL_ROOT_MANIFEST_DIGEST_MISMATCH")
        nested = [n for n in names if n.endswith("/SHA256SUMS.json")]
        for name in nested:
            prefix = str(PurePosixPath(name).parent) + "/"
            items = json_member(archive, name)
            required = {n[len(prefix):] for n in names
                        if n.startswith(prefix) and n != name}
            demand(isinstance(items, dict) and set(items) == required,
                   "ORIGINAL_CHILD_MANIFEST_COVERAGE_MISMATCH")
            for rel, digest in items.items():
                demand(isinstance(digest, str) and len(digest) == 64
                       and hashlib.sha256(archive.read(prefix + rel)).hexdigest() == digest,
                       "ORIGINAL_CHILD_MANIFEST_DIGEST_MISMATCH")
        return sorted(names)


def safe_mode(value):
    return value if value in ("present", "absent", "unknown", "not_applicable") else "unknown"


def safe_role(row):
    kind = row.get("chromiumRole")
    if kind == "utility":
        subtype = row.get("chromiumUtilitySubtype")
        return "StorageService" if subtype == "storage.mojom.StorageService" else "other-utility"
    if kind in ("root", "crashpad-handler", "gpu-process", "renderer", "zygote"):
        return kind
    return "other-chrome"


def audited_original_fail(archive, name):
    stage = json_member(archive, name)
    if stage.get("originalPresenceGate") != "failed":
        return None
    demand(stage.get("failure", "").startswith(
        "P47_OWNED_OBSERVED_CHILD_PIDS_SURVIVED"),
        "NOT_ORIGINAL_GATE_SURVIVOR_FAILURE")
    signal = stage.get("driverTermination") or {}
    demand(signal.get("mechanism") == "os.kill(SIGTERM)"
           and signal.get("signalSent") is True
           and stage.get("hostOwnedBrowserCleanup") == "not_attempted",
           "ORIGINAL_SIGTERM_AND_NO_HOST_CLEANUP_REQUIRED")
    root_stage = name.startswith("process-") and "/" not in name
    diagnostic_stage = (name.startswith("diagnostic-cycles/cycle-")
                        and "/process-" in name)
    demand(root_stage or diagnostic_stage, "ORIGINAL_STAGE_PATH_REQUIRED")
    event_name = name[:-5] + ".jsonl"
    demand(event_name in archive.namelist(), "ORIGINAL_TIMELINE_MISSING")
    events = [json.loads(x) for x in archive.read(event_name).decode("utf-8").splitlines()]
    demand(len(events) <= 4096, "ORIGINAL_EVENT_BUDGET_EXCEEDED")
    runner = [e for e in events if e.get("phase") == "runner_exited"]
    tail = [e for e in events if e.get("phase") == "passive_cleanup_observation"]
    demand(len(runner) == 1 and bool(tail), "ORIGINAL_RUNNER_OR_GATE_MISSING")

    def stamp(event):
        return datetime.fromisoformat(event["utc"].replace("Z", "+00:00"))

    demand((stamp(tail[-1]) - stamp(runner[0])).total_seconds() >= 12,
           "ORIGINAL_12S_GATE_NOT_ATTESTED")
    ledger = {}
    for source in stage.get("observedOwnedAncestry") or []:
        ft = source.get("creationFileTime")
        if (source.get("wasInBaselineWithOriginalIdentity")
                or not isinstance(ft, str) or not ft.isdigit()):
            continue
        key = (source.get("observedPid"), source.get("startIdentity"))
        demand(key not in ledger, "DUPLICATE_SOURCE_ANCESTRY")
        ledger[key] = (ft, source)
    source_roles = {}
    for _, source in ledger.values():
        for node in source.get("pathToTestProcess") or []:
            key = (node.get("pid"), node.get("startIdentity"))
            ft = (node.get("native") or {}).get("creationFileTime")
            if ft and (key not in source_roles or source_roles[key][0] == ft):
                source_roles[key] = (ft, node.get("kind"), node.get("chromiumRole"),
                                     node.get("chromiumUtilitySubtype"))

    final = {}
    for row in stage.get("finalObservedProcesses") or []:
        key = (row.get("pid"), row.get("startIdentity"))
        demand(key not in final, "DUPLICATE_ORIGINAL_FINAL_PROCESS")
        final[key] = row
    survivors = stage.get("originalPresenceSurvivors") or []
    demand(bool(survivors), "GATE_FAIL_WITH_ZERO_SURVIVORS")
    alive = []
    for candidate in survivors:
        key = (candidate.get("pid"), candidate.get("startIdentity"))
        demand(key in ledger and key in final, "ORIGINAL_SURVIVOR_NOT_SOURCE_BOUND")
        ft, source = ledger[key]
        row = final[key]
        native = row.get("native") or {}
        demand(native.get("state") == "live" and native.get("waitResult") == LIVE_WAIT
               and native.get("creationFileTime") == ft
               and native.get("expectedCreationFileTime") == ft,
               "ORIGINAL_SURVIVOR_NOT_NATIVE_LIVE_EXACT_IDENTITY")
        demand(row.get("kind") == "chrome"
               and row.get("executableMatchesConfiguredChrome") is True,
               "ORIGINAL_CHROME_EXECUTABLE_NOT_ATTESTED")
        lineage = source.get("pathToTestProcess") or []
        demand(any(q.get("pid") == signal.get("pid") for q in lineage),
               "ORIGINAL_NODE_ANCESTRY_NOT_ATTESTED")
        modes = row.get("crashpadSwitches") or {}
        alive.append({
            "pid": key[0], "creationFileTime": ft,
            "parentPid": row.get("parentPid"), "role": safe_role(row),
            "uploadUrlConfigured": safe_mode(modes.get("uploadUrlConfigured")),
            "initialClientData": safe_mode(modes.get("initialClientData"))
        })
    safe = []
    for key, row in final.items():
        if key not in ledger or key not in source_roles:
            continue
        ft, _ = ledger[key]
        canonical_ft, kind, role, subtype = source_roles[key]
        if canonical_ft != ft or kind != "chrome":
            continue
        native = row.get("native") or {}
        if (native.get("creationFileTime") != ft
                or native.get("expectedCreationFileTime") != ft
                or native.get("state") not in ("exited", "live")):
            continue
        demand(native.get("waitResult") == (
            0 if native["state"] == "exited" else LIVE_WAIT),
            "ORIGINAL_NATIVE_WAIT_STATE_CONTRADICTORY")
        safe.append({
            "pid": key[0], "creationFileTime": ft,
            "role": safe_role({
                "chromiumRole": role, "chromiumUtilitySubtype": subtype}),
            "nativeState": native["state"]
        })
    demand(any(x["role"] == "root" and x["nativeState"] == "exited"
               for x in safe), "CHROMIUM_ROOT_NATIVE_EXIT_UNPROVEN")
    prefix = name.rsplit("/", 1)[0] + "/" if "/" in name else ""
    trx = prefix + stage["run"] + ".trx"
    demand(trx in archive.namelist()
           and hashlib.sha256(archive.read(trx)).hexdigest() == stage.get("trxSha256"),
           "ORIGINAL_NUNIT_TRX_INTEGRITY_UNPROVEN")
    live_crashpads = [x for x in alive if x["role"] == "crashpad-handler"]
    live_other = [x for x in alive if x["role"] != "crashpad-handler"]
    known_live_non_crashpad = [
        x for x in safe if x["nativeState"] == "live"
        and x["role"] != "crashpad-handler"]
    return {
        "stage": stage["run"], "sourceReceipt": name,
        "originalGate": "failed", "actualNUnit": stage.get("actual"),
        "survivors": sorted(alive, key=lambda x: x["pid"]),
        "sourceBoundNativeLiveCrashpads": len(live_crashpads),
        "sourceBoundOtherSurvivors": len(live_other),
        "otherSourceBoundChromeLiveAtGate": len(known_live_non_crashpad),
        "knownLiveNonCrashpadRoles": sorted({
            x["role"] for x in known_live_non_crashpad}),
        "rootNativeExited": True,
        "registrationEvidence": "not_observed",
        "crashpadOnProcessEndCallback": "not_observed",
        "inFlightUploadEvidence": "not_observed",
        "registeredClientIdentityProven": False,
        "absenceOfOtherRegisteredClientsProven": False,
        "rootCauseProven": False, "productFixProven": False
    }


def inspect_archive(path, expected_sha):
    members = verify_original_archive(path, expected_sha)
    with zipfile.ZipFile(path) as archive:
        names = [x for x in members if x.endswith(".json") and (
            (x.startswith("process-") and "/" not in x)
            or (x.startswith("diagnostic-cycles/cycle-")
                and "/process-" in x))]
        demand(len(names) <= MAX_RECEIPTS, "ORIGINAL_RECEIPT_BUDGET_EXCEEDED")
        failures = [entry for x in names
                    if (entry := audited_original_fail(archive, x)) is not None]
        demand(len(failures) <= 1, "ORIGINAL_FAIL_FAST_VIOLATION")
    return {
        "schema": SCHEMA, "zipSha256": expected_sha,
        "zipMembers": len(members),
        "status": ("native_live_failure_observed" if failures
                   else "no_original_native_live_failure_attested"),
        "originalFailFastFailure": failures[0] if failures else None,
        "registeredClientIdentityProven": False,
        "registeredClientRegistryObservable": False,
        "originalSourceModified": False,
        "additionalProcessAccess": False,
        "rootCauseProven": False, "productFixProven": False,
        "postgateJobCloseProven": False, "fixClaim": False
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--original", action="append", nargs=3,
                        metavar=("LABEL", "SHA256", "ZIP"), required=True)
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()
    try:
        result = {
            label: inspect_archive(path, digest)
            for label, digest, path in args.original}
        payload = json.dumps({
            "schema": SCHEMA, "artifacts": result,
            "sourceRegisteredClientsProven": False, "fixClaim": False
        }, sort_keys=True, indent=2) + "\n"
        if args.out:
            args.out.parent.mkdir(parents=True, exist_ok=True)
            args.out.write_text(payload, encoding="utf-8")
        else:
            print(payload, end="")
    except (Unverified, OSError, ValueError, zipfile.BadZipFile,
            KeyError, TypeError) as exc:
        parser.exit(2, "P1L_UNVERIFIED: " + str(exc) + "\n")


if __name__ == "__main__":
    main()
