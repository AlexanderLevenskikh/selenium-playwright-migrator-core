"""P1-E: supervise the unmodified P47 entry point from outside its Job."""
import ctypes
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time

import p1_preview47_job_postgate_proof as proof
import p2_preview47_one_time_real_chromium as p47
import p2_preview47_process_evidence as evidence
import p2_preview47_windows_job_object_probe as jobs

ROOT = Path(__file__).resolve().parent.parent
ORIGINAL = Path(p47.__file__).resolve()
SOURCE = ROOT / "artifacts/pom-preview47"
MAX_BLOCKS = 3
BLOCK_TIMEOUT = 420
BOOTSTRAP = (
    "import sys\n"
    "if sys.stdin.buffer.read(1) != b'G': raise SystemExit(3)\n"
    "import runpy\n"
    "sys.argv = sys.argv[1:]\n"
    "sys.path.insert(0, sys.argv[0].rsplit('/', 1)[0])\n"
    "runpy.run_path(sys.argv[0], run_name='__main__')\n"
)


def digest(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def fingerprint():
    dotnet = Path(shutil.which("dotnet")).resolve()
    runtimes = subprocess.check_output([dotnet, "--list-runtimes"], text=True, timeout=20)
    core_runtime = {}
    for line in runtimes.splitlines():
        if line.startswith("Microsoft.NETCore.App 10."):
            version, location = line.split(" ", 2)[1:]
            directory = Path(location.strip("[]")) / version
            core_runtime[version] = {p.name: digest(p) for p in sorted(directory.iterdir()) if p.is_file()}
    proof.require(bool(core_runtime), "P1E_NET10_RUNTIME_MISSING")
    return {
        "imageOS": os.environ["ImageOS"], "imageVersion": os.environ["ImageVersion"],
        "osBuild": sys.getwindowsversion().build,
        "runnerName": os.environ["RUNNER_NAME"],
        "pythonVersion": sys.version, "pythonSha256": digest(sys.executable),
        "dotnetSdk": subprocess.check_output([dotnet, "--version"], text=True, timeout=20).strip(),
        "dotnetSha256": digest(dotnet), "coreRuntime": core_runtime,
        "chromeVersion": os.environ["MIGRATOR_CHROME_VERSION"],
        "chromeSha256": digest(os.environ["MIGRATOR_CHROME_BIN"]),
        "chromeDriverVersion": os.environ["MIGRATOR_CHROMEDRIVER_VERSION"],
        "chromeDriverSha256": digest(os.environ["MIGRATOR_CHROMEDRIVER_BIN"]),
        "originalSha256": digest(ORIGINAL),
    }


def package_fingerprint():
    cache = Path(os.environ["NUGET_PACKAGES"])
    packages = {}
    for name, version in (("nunit", "4.3.2"), ("nunit3testadapter", "5.1.0"),
                          ("microsoft.testplatform.testhost", "17.14.1"),
                          ("microsoft.playwright", "1.63.0")):
        directory = cache / name / version
        archive = directory / f"{name}.{version}.nupkg"
        packages[name] = {"version": version, "archiveSha256": digest(archive)}
    node = list((cache / "microsoft.playwright/1.63.0").rglob("node.exe"))
    node = [path for path in node if path.parent.name == "win32_x64"]
    proof.require(len(node) == 1, "P1E_PLAYWRIGHT_NODE_BINARY_AMBIGUOUS")
    packages["node"] = {
        "version": subprocess.check_output([node[0], "--version"], text=True, timeout=20).strip(),
        "sha256": digest(node[0]),
    }
    return packages


def observe_prefault(api, native, job, offsets, observations):
    for stage, _, disconnect in p47.STAGES:
        path = SOURCE / f"process-{stage}.jsonl"
        if not disconnect or offsets.get(stage, 0) is None or not path.is_file():
            continue
        with path.open("rb") as stream:
            stream.seek(offsets.get(stage, 0))
            while line := stream.readline():
                if not line.endswith(b"\n"):
                    break
                offsets[stage] = stream.tell()
                event = json.loads(line)
                if event["phase"] == "driver_termination_before_signal":
                    offsets[stage] = None
                    break
                if event["phase"] != "runner_active":
                    continue
                for row in event["processes"]:
                    expected = row["native"].get("expectedCreationFileTime")
                    if (row.get("kind") not in ("chrome", "driver") or not expected
                            or not row["presentWithOriginalIdentity"]):
                        continue
                    key = (row["pid"], row["startIdentity"])
                    if key in observations.setdefault(stage, {}):
                        continue
                    handle = api.OpenProcess(proof.CHILD_RIGHTS, False, row["pid"])
                    if not handle:
                        continue
                    try:
                        checked = native.inspect_handle(handle, row["pid"], expected)
                        if checked["state"] == "live":
                            checked.update(startIdentity=row["startIdentity"], kind=row["kind"],
                                           inHostJob=jobs.member(api, handle, job),
                                           checkedUtc=evidence.utc_now())
                            observations[stage][key] = checked
                    finally:
                        api.CloseHandle(handle)


def stage_receipts(arm, out, observations):
    rows = []
    stopped = False
    for stage, expected, disconnect in p47.STAGES:
        row = {"stage": stage, "expectedNUnit": expected, "disconnect": disconnect}
        rows.append(row)
        path = SOURCE / f"process-{stage}.json"
        if stopped:
            proof.require(not path.exists(), "P1E_STAGE_LAUNCHED_AFTER_ORIGINAL_FAIL")
            row["status"] = "not_run_due_to_original_fail_fast"
            continue
        if not path.is_file():
            row["status"] = "prerequisite_failure"
            stopped = True
            continue
        result = json.loads(path.read_text(encoding="utf-8"))
        timeline = SOURCE / f"process-{stage}.jsonl"
        fault_utc = next((event["utc"] for event in map(json.loads, timeline.read_text(
            encoding="utf-8").splitlines()) if event["phase"] == "driver_termination_before_signal"), None)
        pre = [entry for entry in observations.get(stage, {}).values()
               if fault_utc and entry["checkedUtc"] < fault_utc]
        pre.sort(key=lambda entry: entry["pid"])
        row.update(originalPresenceGate=result["originalPresenceGate"],
                   originalFailure=result.get("failure"), actualNUnit=result["actual"],
                   verifiedBeforeFault=pre)
        survivor_keys = {(x["pid"], x["startIdentity"]) for x in result.get("originalPresenceSurvivors", [])}
        survivors = [
            {"pid": x["pid"], "startIdentity": x["startIdentity"], "kind": "chrome",
             "creationFileTime": x["native"]["creationFileTime"]}
            for x in result.get("finalObservedProcesses", [])
            if (x["pid"], x["startIdentity"]) in survivor_keys and x.get("kind") == "chrome"
            and x["native"]["state"] == "live" and x["native"].get("expectedCreationFileTime")
            and x["native"]["creationFileTime"] == x["native"]["expectedCreationFileTime"]
        ]
        row["nativeLiveChrome"] = survivors
        if arm == "B_late":
            # P1-G Chrome-only late-association is a separate oracle experiment:
            # wrapper/Node remain out of Job. It cannot prove pre-create mitigation.
            from p1_preview47_late_chrome_job import membership_contract
            membership = membership_contract(arm, pre)
        else:
            attested = {x["kind"] for x in pre} == {"chrome", "driver"} and all(
                x["inHostJob"] == (arm == "B") for x in pre)
            membership = "attested" if attested else "identity_or_membership_unattested"
        row["prefaultMembership"] = membership if disconnect else "not_applicable"
        stopped = result["status"] != "passed"
        if not stopped:
            row["status"] = "original_pass"
        elif (disconnect and result.get("failure", "").startswith(
                "P47_OWNED_OBSERVED_CHILD_PIDS_SURVIVED:")
              and survivors and result.get("realDriverKilled")
              and result.get("actual") == expected and result["originalPresenceGate"] == "failed"):
            row["status"] = "native_live_fail"
        elif result.get("failure", "").startswith("P47_OWNED_OBSERVED_CHILD_PIDS_SURVIVED:"):
            row["status"] = "original_fail_native_liveness_unproven"
        else:
            row["status"] = "prerequisite_failure"
    evidence.atomic_json(out / "stages.json", rows)
    return rows


def run_block(arm, out, observer_factory=None):
    out.mkdir()
    proof.require(not SOURCE.exists(), "P1E_ORIGINAL_EVIDENCE_NOT_FRESH")
    api = jobs.win32()
    native = evidence.NativeWindowsProbe()
    ref = {"handle": api.CreateJobObjectW(None, None)}
    proof.require(bool(ref["handle"]), "P1E_CREATE_JOB")
    process = None
    observations = {}
    offsets = {}
    observer = None
    try:
        job = ref["handle"]
        limits = jobs.ExtendedLimits()
        limits.BasicLimitInformation.LimitFlags = jobs.KILL_ON_JOB_CLOSE
        proof.require(api.SetInformationJobObject(job, 9, ctypes.byref(limits),
                                                  ctypes.sizeof(limits)), "P1E_SET_JOB_LIMITS")
        proof.require(api.QueryInformationJobObject(job, 9, ctypes.byref(limits),
                                                    ctypes.sizeof(limits), None), "P1E_QUERY_JOB_LIMITS")
        proof.require(limits.BasicLimitInformation.LimitFlags == jobs.KILL_ON_JOB_CLOSE,
                      "P1E_UNEXPECTED_JOB_LIMITS")
        with (out / "original.log").open("wb") as log:
            process = subprocess.Popen([sys.executable, "-c", BOOTSTRAP, ORIGINAL.as_posix()],
                                       cwd=ROOT, stdin=subprocess.PIPE, stdout=log,
                                       stderr=subprocess.STDOUT, close_fds=True)
            handle = api.OpenProcess(proof.CHILD_RIGHTS | 0x0100 | 0x0001, False, process.pid)
            proof.require(bool(handle), "P1E_OPEN_WRAPPER")
            try:
                wrapper = native.inspect_handle(handle, process.pid, None)
                proof.require(wrapper["state"] == "live", "P1E_WRAPPER_NOT_LIVE")
                wrapper["inRunnerJobBeforeAssignment"] = jobs.member(api, handle, None)
                evidence.atomic_json(out / "wrapper.json", wrapper)
                if arm == "B" and not api.AssignProcessToJobObject(job, handle):
                    raise OSError(ctypes.get_last_error(), "P1E_ASSIGN_WRAPPER")
                wrapper["inHostJob"] = jobs.member(api, handle, job)
                proof.require(wrapper["inHostJob"] == (arm == "B"), "P1E_WRAPPER_MEMBERSHIP")
                evidence.atomic_json(out / "wrapper.json", wrapper)
                if observer_factory is not None:
                    # P1-F starts its independent Win32 watcher BEFORE the original
                    # wrapper receives its bootstrap byte. P1-E's path is unchanged.
                    observer = observer_factory(api, native, job, process.pid,
                                                wrapper["creationFileTime"], arm, SOURCE)
                process.stdin.write(b"G")
                process.stdin.close()
            finally:
                api.CloseHandle(handle)
            deadline = time.monotonic() + BLOCK_TIMEOUT
            observer_error = None
            while process.poll() is None:
                if observer_error is None:
                    try:
                        if observer is None:
                            observe_prefault(api, native, job, offsets, observations)
                        else:
                            observer.poll()
                    except (OSError, ValueError, AssertionError) as error:
                        observer_error = str(error)
                proof.require(time.monotonic() < deadline, "P1E_ORIGINAL_BLOCK_TIMEOUT")
                time.sleep(.025)
        if observer is not None:
            observations = observer.joined()
            evidence.atomic_json(out / "native-observer.json", observer.report())
        evidence.atomic_json(out / "terminal.json",
                             {"exitCode": process.returncode, "observerError": observer_error})
        rows = stage_receipts(arm, out, observations)
        if observer is not None and observer_error is not None:
            proof.require(False, "P1F_NATIVE_OBSERVER_ERROR: " + observer_error)
        failure = next((row for row in rows if row["status"] != "original_pass"), None)
        proof.require((process.returncode == 0) == (failure is None), "P1E_ORIGINAL_EXIT_MISMATCH")
        if failure and failure["status"] == "native_live_fail" and arm in ("B", "B_late"):
            report = {"unassistedGateBeforeHostClose": "failed",
                      "arm": arm,
                      "productPreCreateMitigationProved": False,
                      "assignmentMode": ("before_bootstrap" if arm == "B"
                                         else "late_chrome_only_before_ready")}
            destination = out / "containment.json"
            try:
                members = failure["verifiedBeforeFault"]
                expected_membership = ("attested" if arm == "B"
                                       else "chrome_attested_driver_outside_job")
                proof.require(failure["prefaultMembership"] == expected_membership,
                              "P1G_PREFAULT_UNATTESTED")
                proof.prove_after_gate(
                    api, native, ref, members, failure["nativeLiveChrome"], report,
                    lambda: evidence.atomic_json(destination, report), jobs.member)
            except (OSError, AssertionError) as error:
                report.update(status="identity_or_membership_or_exit_unattested", detail=str(error))
                evidence.atomic_json(destination, report)
        if failure and failure["status"] == "native_live_fail" and arm == "A":
            control = []
            for survivor in failure["nativeLiveChrome"]:
                pid = survivor["pid"]
                handle = api.OpenProcess(proof.CHILD_RIGHTS, False, pid)
                row = dict(survivor, status="identity_or_membership_unattested")
                if handle:
                    try:
                        checked = native.inspect_handle(handle, pid, survivor["creationFileTime"])
                        row["nativeBeforeClose"] = checked
                        if checked["state"] == "live":
                            row["inHostJob"] = jobs.member(api, handle, job)
                            if not row["inHostJob"]:
                                row["status"] = "live_outside_experiment_job"
                    except (OSError, AssertionError) as error:
                        row["detail"] = str(error)
                    finally:
                        api.CloseHandle(handle)
                control.append(row)
            evidence.atomic_json(out / "control-survivors.json", control)
        return failure["status"] if failure else "original_pass"
    finally:
        if process is not None:
            if process.stdin and not process.stdin.closed:
                process.stdin.close()
            if process.poll() is None:
                process.kill()  # bounded emergency stop of this exact wrapper only
                process.wait(timeout=15)
        if ref["handle"]:
            proof.require(api.CloseHandle(ref["handle"]), "P1E_CLOSE_JOB")
        if SOURCE.exists():
            SOURCE.rename(out / "original")


def main():
    arm = sys.argv[1]
    proof.require(arm in ("A", "B") and os.name == "nt", "P1E_WINDOWS_ARM_REQUIRED")
    out = ROOT / f"artifacts/pom-preview47-p1e-{arm}"
    out.mkdir(parents=True)
    plan = {"arm": arm, "stages": p47.STAGES, "maxBlocks": MAX_BLOCKS, "stageRetries": 0,
            "originalFailFast": True, "originalGateSeconds": evidence.PASSIVE_WINDOW_SECONDS}
    evidence.atomic_json(out / "plan.json", plan)
    result = {"arm": arm, "blocks": [], "fixClaim": False}
    try:
        os.environ["P47_DIAGNOSTIC_CYCLES"] = "0"
        environment = fingerprint()
        evidence.atomic_json(out / "environment.json", environment)
        for number in range(1, MAX_BLOCKS + 1):
            status = run_block(arm, out / f"block-{number}")
            result["blocks"].append(status)
            if status != "original_pass":
                break
        if result["blocks"][-1] != "prerequisite_failure":
            environment["packages"] = package_fingerprint()
            evidence.atomic_json(out / "environment.json", environment)
        result["status"] = ("no_native_live_reproduction" if status == "original_pass" else status)
    except (OSError, ValueError, AssertionError, subprocess.SubprocessError) as error:
        result.update(status="prerequisite_failure", detail=str(error),
                      originalNativeLiveFail="native_live_fail" in result["blocks"])
    finally:
        evidence.atomic_json(out / "result.json", result)
        evidence.write_manifest(out)
    print(json.dumps(result, sort_keys=True))
    return int(result["status"] in {"prerequisite_failure", "native_live_fail",
                                    "original_fail_native_liveness_unproven"})


if __name__ == "__main__":
    raise SystemExit(main())
