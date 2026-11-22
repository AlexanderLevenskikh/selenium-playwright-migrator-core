#!/usr/bin/env python3
"""P1-J offline, read-only lifecycle comparison of ORIGINAL P47 ZIP evidence.

No browser, process handle, GitHub CI rerun, synthetic driver fault, process
termination, Job Object assignment, or product cleanup is performed here.
An independent Job B result must never be called a product remediation.
"""
import argparse
from datetime import datetime
import hashlib
import json
from pathlib import Path, PurePosixPath
import zipfile

SCHEMA = "migrator-preview47-p1j-lifecycle-evidence/v1"
LIVE_WAIT = 258


class EvidenceError(ValueError):
    pass


def require(condition, code):
    if not condition:
        raise EvidenceError(code)


def read_json(archive, name):
    return json.loads(archive.read(name).decode("utf-8"))


def checked_zip(path, sha256):
    require(sha256 and len(sha256) == 64, "P1J_EXPECTED_ZIP_SHA_REQUIRED")
    require(hashlib.sha256(Path(path).read_bytes()).hexdigest() == sha256,
            "P1J_ORIGINAL_ZIP_SHA_MISMATCH")
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        require(len(names) == len(set(names)), "P1J_DUPLICATE_ZIP_MEMBER")
        require(all(not PurePosixPath(n).is_absolute()
                    and ".." not in PurePosixPath(n).parts
                    and not n.endswith("/") and "\\" not in n for n in names),
                "P1J_UNSAFE_ZIP_MEMBER")
        require(archive.testzip() is None, "P1J_ZIP_CRC_FAILURE")
        require("SHA256SUMS.json" in names, "P1J_SHA256SUMS_REQUIRED")
        manifest = read_json(archive, "SHA256SUMS.json")
        require(isinstance(manifest, dict)
                and set(manifest) == set(names) - {"SHA256SUMS.json"},
                "P1J_MANIFEST_COVERAGE_MISMATCH")
        for name, digest in manifest.items():
            require(isinstance(digest, str) and len(digest) == 64
                    and hashlib.sha256(archive.read(name)).hexdigest() == digest,
                    "P1J_MEMBER_HASH_MISMATCH:" + name)
        for nested in (n for n in names if n.endswith("/SHA256SUMS.json")):
            mapping = read_json(archive, nested)
            prefix = str(PurePosixPath(nested).parent) + "/"
            expected = {n[len(prefix):] for n in names if n.startswith(prefix) and n != nested}
            require(isinstance(mapping, dict) and set(mapping) == expected,
                    "P1J_NESTED_MANIFEST_MISMATCH:" + nested)
            for relative, digest in mapping.items():
                require(hashlib.sha256(archive.read(prefix + relative)).hexdigest() == digest,
                        "P1J_NESTED_MEMBER_HASH_MISMATCH:" + relative)
    return len(names)


def duration_ms(before, after):
    a = datetime.fromisoformat(before.replace("Z", "+00:00"))
    b = datetime.fromisoformat(after.replace("Z", "+00:00"))
    return round((b - a).total_seconds() * 1000, 1)


def inspect_original(path, expected_sha):
    count = checked_zip(path, expected_sha)
    with zipfile.ZipFile(path) as archive:
        candidate_names = sorted(n for n in archive.namelist()
                                 if n.startswith("diagnostic-cycles/")
                                 and "/process-disconnect_" in n and n.endswith(".json"))
        failures = []
        for name in candidate_names:
            receipt = read_json(archive, name)
            if receipt.get("originalPresenceGate") != "failed":
                continue
            require("P47_OWNED_OBSERVED_CHILD_PIDS_SURVIVED" in receipt.get("failure", ""),
                    "P1J_ORIGINAL_GATE_ERROR_REQUIRED")
            driver = receipt.get("driverTermination") or {}
            require(driver.get("signalSent") is True
                    and driver.get("mechanism") == "os.kill(SIGTERM)"
                    and receipt.get("hostOwnedBrowserCleanup") == "not_attempted",
                    "P1J_REAL_NODE_SIGTERM_REQUIRED")
            log_name = name[:-5] + ".jsonl"
            require(log_name in archive.namelist(), "P1J_ORIGINAL_TIMELINE_REQUIRED")
            events = [json.loads(line)
                      for line in archive.read(log_name).decode("utf-8").splitlines()]
            def unique_phase(phase):
                matches = [e for e in events if e.get("phase") == phase]
                require(len(matches) == 1, "P1J_UNATTESTED_PHASE:" + phase)
                return matches[0]
            fault = unique_phase("driver_termination_before_signal")
            runner = unique_phase("runner_exited")
            passive = [e for e in events if e.get("phase") == "passive_cleanup_observation"]
            require(passive and duration_ms(runner["utc"], passive[-1]["utc"]) >= 12000,
                    "P1J_UNCHANGED_12S_GATE_REQUIRED")
            before = set()
            for e in events:
                if e.get("phase") != "runner_active" or e["utc"] >= fault["utc"]:
                    continue
                for row in e.get("processes", []):
                    n = row.get("native") or {}
                    if row.get("presentWithOriginalIdentity") and n.get("state") == "live":
                        before.add((row["pid"], str(n.get("creationFileTime"))))
            observed = receipt.get("observedOwnedAncestry", [])
            roots = {}
            for source in observed:
                for node in source.get("pathToTestProcess", []):
                    if node.get("chromiumRole") == "root":
                        n = node.get("native") or {}
                        roots[(node["pid"], str(n.get("creationFileTime")))] = node
            final_rows = receipt.get("finalObservedProcesses", [])
            root = [row for row in final_rows
                    if (row.get("pid"), str((row.get("native") or {}).get("creationFileTime")))
                    in roots]
            require(len(root) == 1 and root[0]["native"].get("state") == "exited"
                    and root[0]["native"].get("waitResult") == 0,
                    "P1J_CHROMIUM_ROOT_NATIVE_EXIT_UNPROVEN")
            root = root[0]
            survivors = []
            for pair in receipt.get("originalPresenceSurvivors", []):
                match = [p for p in final_rows if p.get("pid") == pair["pid"]
                         and p.get("startIdentity") == pair["startIdentity"]]
                require(len(match) == 1, "P1J_SURVIVOR_IDENTITY_MISMATCH")
                row = match[0]
                native = row.get("native") or {}
                ft = str(native.get("creationFileTime"))
                require(row.get("kind") == "chrome"
                        and row.get("executableMatchesConfiguredChrome") is True
                        and native.get("state") == "live"
                        and native.get("waitResult") == LIVE_WAIT
                        and native.get("expectedCreationFileTime") == ft
                        and (row["pid"], ft) in before,
                        "P1J_SOURCE_BOUND_PREFAULT_NATIVE_LIVE_REQUIRED")
                ancestry = [a for a in observed if a.get("observedPid") == row["pid"]
                            and str(a.get("creationFileTime")) == ft]
                require(len(ancestry) == 1, "P1J_SOURCE_ANCESTRY_REQUIRED")
                ancestry_pids = {n.get("pid") for n in ancestry[0].get("pathToTestProcess", [])}
                require(root["pid"] in ancestry_pids and driver["pid"] in ancestry_pids,
                        "P1J_NODE_ROOT_HELPER_CHAIN_UNPROVEN")
                survivors.append({"pid": row["pid"], "creationFileTime": ft,
                                  "chromiumRole": row.get("chromiumRole"),
                                  "utilitySubtype": row.get("chromiumUtilitySubtype"),
                                  "parentPid": row.get("parentPid"),
                                  "nativeWaitResult": LIVE_WAIT})
            require(survivors, "P1J_REAL_NATIVE_LIVE_SURVIVOR_REQUIRED")
            trx = name.rsplit("/", 1)[0] + "/" + receipt["run"] + ".trx"
            require(trx in archive.namelist()
                    and hashlib.sha256(archive.read(trx)).hexdigest() == receipt.get("trxSha256"),
                    "P1J_ORIGINAL_TRX_MISMATCH")
            failures.append({"originalStage": receipt["run"],
                             "originalReceipt": name,
                             "nunitExpected": receipt["expected"],
                             "nunitActual": receipt["actual"],
                             "nodePid": driver["pid"],
                             "rootPid": root["pid"],
                             "rootCreationFileTime": root["native"]["creationFileTime"],
                             "rootExitCode": root["native"].get("exitCode"),
                             "rootState": "exited",
                             "faultToRunnerExitMs": duration_ms(fault["utc"], runner["utc"]),
                             "runnerToLastLiveMs": duration_ms(runner["utc"], passive[-1]["utc"]),
                             "survivors": survivors})
        require(len(failures) == 1, "P1J_ONE_ORIGINAL_FAIL_FAST_FAILURE_REQUIRED")
        return {"sha256": expected_sha, "zipEntries": count, "originalNativeLiveFailure": failures[0]}


def inspect_job_b(path, expected_sha):
    count = checked_zip(path, expected_sha)
    with zipfile.ZipFile(path) as archive:
        names = [n for n in archive.namelist() if n.endswith("/native-observer.json")]
        require(len(names) == 1, "P1J_ONE_B_OBSERVER_REQUIRED")
        report = read_json(archive, names[0])
        require(report.get("hostJobHandleHeldAfterOriginalGate") is True
                and report.get("hostJobLimitFlagsAtReport") == 8192,
                "P1J_HOST_JOB_HELD_UNPROVEN")
        stages = report.get("sourceLinkedStages", [])
        require(len(stages) == 3, "P1J_THREE_DISCONNECT_STAGES_REQUIRED")
        result = []
        for stage in stages:
            tracks = stage.get("nativeTracks", [])
            require(stage.get("allObservedTracksSourceLinked") is True
                    and stage.get("originalGate") == "passed" and tracks,
                    "P1J_B_SOURCE_JOIN_UNPROVEN")
            require(all(t.get("sourceLinked") is True
                        and t.get("inHostJobBeforeFault") is True
                        and t.get("nativeAnomaly") is None
                        and t.get("timingClassification") ==
                        "native_exit_after_fault_before_runner_exit"
                        for t in tracks), "P1J_B_NATURAL_EXIT_UNPROVEN")
            result.append({"stage": stage["stage"], "nativeTracked": len(tracks)})
        return {"sha256": expected_sha, "zipEntries": count,
                "hostJobHeldAfterGate": True,
                "sourceLinkedStages": result,
                "nativeTracked": sum(x["nativeTracked"] for x in result)}


def compare(originals, jobs):
    require(originals and jobs, "P1J_INDEPENDENT_CONTROL_CLASSES_REQUIRED")
    return {"schema": SCHEMA,
            "status": "root_exited_while_helpers_remained_live_after_gate",
            "originalA": originals, "independentJobB": jobs,
            "rootCauseProven": False, "productCleanupProven": False,
            "postGateHostRemediationProven": False,
            "sameRunnerRandomizedComparison": False, "fixClaim": False,
            "explanation": "Original A root exited while crashpad/StorageService remained live beyond 12s. Independent Job B processes exited naturally before runner exit while Job handle was held; these different runners cannot establish causation. IPC/handle lifetime UNKNOWN."}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--original", nargs=3, action="append",
                        metavar=("LABEL", "SHA256", "ZIP"), required=True)
    parser.add_argument("--job-b", nargs=3, action="append",
                        metavar=("LABEL", "SHA256", "ZIP"), required=True)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args(argv)
    try:
        originals = {label: inspect_original(path, sha)
                     for label, sha, path in args.original}
        jobs = {label: inspect_job_b(path, sha)
                for label, sha, path in args.job_b}
        text = json.dumps(compare(originals, jobs), indent=2, sort_keys=True) + "\n"
        if args.output:
            args.output.write_text(text, encoding="utf-8")
        else:
            print(text, end="")
        return 0
    except (OSError, ValueError, KeyError, TypeError, zipfile.BadZipFile) as exc:
        parser.exit(2, "P1J_UNVERIFIED: " + str(exc) + "\n")


if __name__ == "__main__":
    raise SystemExit(main())
