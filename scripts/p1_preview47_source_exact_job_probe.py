#!/usr/bin/env python3
"""Independent SOURCE-EXACT P47 NUnit TearDown/OneTimeTearDown host-owned Job experiment.

The actual NUnit testhost waits BEFORE Playwright.CreateAsync. The host
assigns it to a Job, then proves real Node+Chrome descendant membership
using CIM creation time, native FILETIME and IsProcessInJob. Node is killed
with the original Windows os.kill(SIGTERM) fault. The unassisted original
12-second process-presence gate runs BEFORE host Job close. A later host
kill can NEVER promote a failed original gate to PASS.

The original P47 fixture is reused byte-for-byte except a pre-CreateAsync host gate.
This DOES NOT change the original preview.47 run or generated product.
"""
import ctypes
from ctypes import wintypes
import hashlib
import json
import os
from pathlib import Path
import signal
import subprocess
import tempfile
import time
import threading
from http.server import ThreadingHTTPServer
import p2_preview47_one_time_real_chromium as p47
import p2_preview42_fixture_lifecycle_real_chromium as p42

import p2_preview37_rendered_text_unicode_real_chromium as p37
import p2_preview45_sink_close_process_ancestry as p45
import p2_preview47_process_evidence as p47e
import p2_preview47_windows_job_object_probe as jobs
import p1_preview47_job_postgate_proof as postgate

SCHEMA = "migrator-preview47-source-exact-host-job/v1"
CHILD_RIGHTS = 0x00100000 | 0x1000
ASSIGN_RIGHTS = CHILD_RIGHTS | 0x0100 | 0x0001

PRE_CREATE_GATE = """        # Host-assignment handshake: the ONLY addition to original P47 NUnit
        # setup, before Playwright.CreateAsync can spawn the Node driver.
        File.WriteAllText(Path.Combine(DirectoryPath,"testhost-gate"),
            Environment.ProcessId.ToString());
        for(var i=0;i<300&&!File.Exists(Path.Combine(DirectoryPath,"job-assigned"));i++)
            await Task.Delay(100);
        Assert.That(File.Exists(Path.Combine(DirectoryPath,"job-assigned")),Is.True,
            "P47_EXACT_PRECREATE_JOB_ASSIGNMENT_MISSING");
"""

def source_exact_fixture(repo):
    """Return original P47 runtime fixture and SAME fixture with pre-create Job gate.

    Original C# is the exact source used by scripts/p2_preview47_one_time_real_chromium.py:
    TEMPLATE + typed TearDown from preview.46 + OneTimeTearDown from preview.47.
    No handwritten replacement hooks, assertions, selector, or cleanup body.
    """
    file46 = repo / "Migrator.Application.Smoke/PomPreview46TypedTargetClosedSmoke.cs"
    file47 = repo / "Migrator.Application.Smoke/PomPreview47OneTimeCleanupSmoke.cs"
    bytes46, bytes47 = file46.read_bytes(), file47.read_bytes()
    teardown = p42.synthetic("TypedTearDown", bytes46.decode("utf-8"))
    one_time = p42.synthetic("OneTimeTearDown", bytes47.decode("utf-8"))
    require(p47.TEMPLATE.count("__TEARDOWN_SOURCE__") == 1 and
            p47.TEMPLATE.count("__ONE_TIME_SOURCE__") == 1,
            "P47_SOURCE_EXACT_TEMPLATE_PLACEHOLDER_MISMATCH")
    original = p47.TEMPLATE.replace("__TEARDOWN_SOURCE__", teardown).replace(
        "__ONE_TIME_SOURCE__", one_time)
    require(original.count(teardown) == 1 and original.count(one_time) == 1,
            "P47_SOURCE_EXACT_HOOKS_ARE_NOT_BYTE_EXACT")
    require("P47_PRIMARY_ASSERTION_MUST_SURVIVE" in original and
            "P47_REAL_DRIVER_KILL_NOT_CONFIRMED" in original and
            "await Browser.CloseAsync()" in one_time and "PW.Dispose()" in one_time and
            "await Context.CloseAsync()" in teardown,
            "P47_SOURCE_EXACT_RUNTIME_CONTRACT_MISSING")
    anchor = "        PW=await Microsoft.Playwright.Playwright.CreateAsync();"
    require(original.count(anchor) == 1, "P47_SOURCE_EXACT_CREATE_ANCHOR_CHANGED")
    instrumented = original.replace(anchor, PRE_CREATE_GATE + anchor, 1)
    require(instrumented.replace(PRE_CREATE_GATE, "", 1) == original,
            "P47_SOURCE_EXACT_NON_GATE_CODE_CHANGED")
    sha = lambda content: hashlib.sha256(content).hexdigest()
    proof = {
        "schema": "migrator-preview47-source-exact-nunit-fixture/v1",
        "runtimeTemplate": "p2_preview47_one_time_real_chromium.TEMPLATE",
        "fixtureKind": "original_P47_runtime_template_with_pre_Playwright_CreateAsync_gate_only",
        "typedTearDownSource": file46.name,
        "oneTimeTearDownSource": file47.name,
        "typedSourceFileSha256": sha(bytes46),
        "oneTimeSourceFileSha256": sha(bytes47),
        "tearDownSha256": sha(teardown.encode("utf-8")),
        "oneTimeTearDownSha256": sha(one_time.encode("utf-8")),
        "originalRuntimeFixtureSha256": sha(original.encode("utf-8")),
        "instrumentedFixtureSha256": sha(instrumented.encode("utf-8")),
        "byteExactExceptPreCreateGate": True,
        "unmodifiedPrimaryAssertion": "P47_PRIMARY_ASSERTION_MUST_SURVIVE",
        "noSyntheticExceptionInjection": True,
    }
    return original, instrumented, proof


def expected_nunit(stage):
    expected = {name: outcome for name, outcome, disconnected in p47.STAGES
                if disconnected}.get(stage)
    require(expected is not None, "P47_SOURCE_EXACT_STAGE_NOT_IN_ORIGINAL_P47")
    return expected


def require(condition, code):
    if not ok:
        raise AssertionError(code)


def owned(data, root):
    return {(pid, str(start)): kind
            for pid, (_parent, start, kind) in data.items()
            if kind in ("chrome", "driver") and
            p45.ancestry(data, pid, root) is not None}


def live_member(api, probe, job, data, precise, pid, start):
    require(pid in data and str(data[pid][1]) == start,
            "P47_REAL_JOB_OBSERVED_PROCESS_IDENTITY_CHANGED")
    handle = api.OpenProcess(CHILD_RIGHTS, False, pid)
    require(bool(handle), "P47_REAL_JOB_OPEN_PROCESS_FAILED")
    try:
        status = probe.inspect_handle(handle, pid)
        require(status["state"] == "live" and
                p47e.cim_matches_native(precise.get(pid, {}).get("creationUtc"),
                                        status.get("creationFileTime")),
                "P47_REAL_JOB_CIM_NATIVE_IDENTITY_MISMATCH")
        require(jobs.member(api, handle, job), "P47_REAL_JOB_NOT_A_JOB_MEMBER")
        return {"pid": pid, "startIdentity": start,
                "creationFileTime": status["creationFileTime"],
                "nativeState": "live", "inHostJob": True,
                "chromiumRole": precise.get(pid, {}).get("chromiumRole"),
                "chromiumUtilitySubtype": precise.get(pid, {}).get("chromiumUtilitySubtype")}
    finally:
        api.CloseHandle(handle)


def unassisted_gate(chrome, observed, report, out):
    # Identical presence predicate and 12s time budget to the original P47.
    limit = time.monotonic() + p47e.PASSIVE_WINDOW_SECONDS
    while True:
        data, _ = p47e.snapshot(chrome)
        survivors = [{"pid": pid, "startIdentity": start, "kind": kind}
                     for (pid, start), kind in sorted(observed.items())
                     if pid in data and str(data[pid][1]) == start]
        with (out / "pre-host-timeline.jsonl").open("a", encoding="utf-8") as f:
            f.write(json.dumps({"at": p47e.utc_now(),
                                "survivors": survivors}, sort_keys=True) + "\n")
        if not survivors or time.monotonic() > limit:
            report["unassistedGateSeconds"] = 12
            report["unassistedGateBeforeHostClose"] = ("failed" if survivors else "passed")
            report["unassistedSurvivorsBeforeHostClose"] = survivors
            p47e.atomic_json(out / "real-job.json", report)
            return survivors
        time.sleep(.4)


def main():
    require(os.name == "nt" and ctypes.sizeof(ctypes.c_void_p) == 8,
            "P47_REAL_JOB_WINDOWS_X64_REQUIRED")
    repo = Path(__file__).resolve().parent.parent
    cycle = os.environ.get("P47_HOST_JOB_PROBE_INDEX", "001")
    require(len(cycle) == 3 and cycle.isascii() and cycle.isdigit()
            and 1 <= int(cycle) <= 6, "P47_HOST_JOB_CYCLE_INDEX_INVALID")
    out = repo / "artifacts/pom-preview47-real-job" / ("cycle-" + cycle)
    out.mkdir(parents=True, exist_ok=True)
    chrome = str(Path(os.environ["MIGRATOR_CHROME_BIN"]).resolve())
    require(Path(chrome).is_file(), "P47_REAL_JOB_CHROME_NOT_INSTALLED")
    stage = os.environ.get("P47_HOST_JOB_STAGE", "disconnect_primary")
    expected = expected_nunit(stage)
    require(os.environ.get("P47_HOST_JOB_EXPECTED_NUNIT", expected) == expected,
            "P47_SOURCE_EXACT_STAGE_EXPECTATION_MISMATCH")
    report = {"schema": SCHEMA, "status": "running",
              "actualPlaywrightNodeAndChromium": True,
              "cycle": int(cycle),
              "expectedNUnitOutcome": expected,
              "sourceExactOriginalP47Stage": stage,
              "fixtureKind": "source_exact_original_P47_runtime_with_pre_create_host_gate",
              "sourcePlaywrightVersion": "1.63.0",
              "originalP47StageModified": False,
              "canRepairHistoricalFailure": False,
              "originalHistoricalFailure": "preserved_not_reclassified",
              "hostCleanup": "not_attempted", "autoApply": False,
              "chromeVersion": os.environ.get("MIGRATOR_CHROME_VERSION"),
              "chromeSha256": hashlib.sha256(Path(chrome).read_bytes()).hexdigest(),
              "rootCauseOfOriginal5936And9024": "unproven"}
    receipt = out / "real-job.json"
    p47e.atomic_json(receipt, report)
    api = jobs.win32()
    native = p47e.NativeWindowsProbe()
    job_ref = {"handle": None}
    job = None
    host_handle = None
    process = None
    server = None
    try:
        require(ctypes.sizeof(jobs.ExtendedLimits) == 144,
                "P47_REAL_JOB_WIN32_LAYOUT_NOT_PROVEN")
        job = api.CreateJobObjectW(None, None)
        require(bool(job), "P47_REAL_JOB_CREATE_FAILED")
        job_ref["handle"] = job  # host owns the only non-inheritable Job handle
        limits = jobs.ExtendedLimits()
        limits.BasicLimitInformation.LimitFlags = jobs.KILL_ON_JOB_CLOSE
        require(api.SetInformationJobObject(job, 9, ctypes.byref(limits),
                                            ctypes.sizeof(limits)),
                "P47_REAL_JOB_SET_LIMITS_FAILED")
        effective = jobs.ExtendedLimits()
        require(api.QueryInformationJobObject(job, 9, ctypes.byref(effective),
                                              ctypes.sizeof(effective), None),
                "P47_REAL_JOB_QUERY_LIMITS_FAILED")
        flags = effective.BasicLimitInformation.LimitFlags
        require(flags & jobs.KILL_ON_JOB_CLOSE and
                not flags & (jobs.BREAKAWAY_OK | jobs.SILENT_BREAKAWAY_OK),
                "P47_REAL_JOB_BREAKAWAY_OR_KILL_ON_CLOSE_NOT_PROVEN")
        report["effectiveLimitFlags"] = int(flags)
        with tempfile.TemporaryDirectory(prefix="p47-actual-playwright-job-") as tmp:
            work = Path(tmp)
            target = p37.CSPROJ.replace(
                '<PackageReference Include="Selenium.WebDriver" Version="4.33.0" />', "")
            require(target != p37.CSPROJ, "P47_REAL_JOB_TARGET_STILL_SELENIUM")
            (work / "Synthetic.Tests.csproj").write_text(target, encoding="utf-8")
            _original, fixture, fixture_proof = source_exact_fixture(repo)
            (work / "RealNUnitFixture.cs").write_bytes(fixture.encode("utf-8"))
            report["sourceExactFixtureProof"] = fixture_proof
            p47e.atomic_json(receipt, report)
            build = p37.run(["dotnet", "build", "-c", "Release", "--nologo"],
                            work, timeout=360)
            require(build["code"] == 0, "P47_SOURCE_EXACT_NUNIT_BUILD_FAILED")
            # The original P47 operation loads window.snapshot() from this
            # same public local HTTP handler. Do not replace with SetContent.
            server = ThreadingHTTPServer(("127.0.0.1", 0), p37.Handler)
            threading.Thread(target=server.serve_forever, daemon=True).start()
            before, _ = p47e.snapshot(chrome)
            baseline = {(pid, str(row[1])) for pid, row in before.items()
                        if row[2] in ("chrome", "driver")}
            env = dict(os.environ, P47_STAGE=stage, P47_RUN=cycle,
                       P47_EVIDENCE=str(out),
                       P47_URL="http://127.0.0.1:" + str(server.server_port) + "/")
            command = ["dotnet", "test", "-c", "Release", "--no-build",
                       "--no-restore", "--nologo", "--logger",
                       "trx;LogFileName=real-job.trx", "--results-directory",
                       str(out), "--filter",
                       "FullyQualifiedName~RealNUnitFixture&Name~Operation"]
            process = subprocess.Popen(command, cwd=work, env=env,
                                       stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                       text=True, encoding="utf-8", errors="replace")
            report["runnerPid"] = process.pid
            deadline = time.monotonic() + 90
            gate = out / "testhost-gate"
            while not gate.is_file() and time.monotonic() < deadline:
                require(process.poll() is None, "P47_REAL_JOB_EARLY_TESTHOST_EXIT")
                time.sleep(.2)
            require(gate.is_file(), "P47_REAL_JOB_TESTHOST_PRESPAWN_GATE_NOT_REACHED")
            host_pid = int(gate.read_text(encoding="ascii"))
            report["testhostPid"] = host_pid
            data, precise = p47e.snapshot(chrome)
            require(host_pid in data and
                    p45.ancestry(data, host_pid, process.pid) is not None,
                    "P47_REAL_JOB_TESTHOST_ANCESTRY_NOT_PROVEN")
            require(not owned(data, host_pid), "P47_REAL_JOB_NODE_ALREADY_SPAWNED")
            host_handle = api.OpenProcess(ASSIGN_RIGHTS, False, host_pid)
            require(bool(host_handle), "P47_REAL_JOB_TESTHOST_HANDLE_UNAVAILABLE")
            identity = native.inspect_handle(host_handle, host_pid)
            require(identity["state"] == "live" and p47e.cim_matches_native(
                    precise.get(host_pid, {}).get("creationUtc"),
                    identity.get("creationFileTime")),
                    "P47_REAL_JOB_TESTHOST_NATIVE_IDENTITY_UNPROVEN")
            require(api.AssignProcessToJobObject(job, host_handle),
                    "P47_REAL_JOB_REAL_TESTHOST_ASSIGNMENT_FAILED")
            require(jobs.member(api, host_handle, job),
                    "P47_REAL_JOB_TESTHOST_NOT_IN_JOB")
            report["testhostCreationFileTime"] = identity["creationFileTime"]
            report["assignedBeforeActualNodeSpawn"] = True
            p47e.atomic_json(receipt, report)
            (out / "job-assigned").write_text("assigned", encoding="ascii")
            observed = {}
            deadline = time.monotonic() + 90
            while time.monotonic() < deadline:
                data, precise = p47e.snapshot(chrome)
                for key, kind in owned(data, host_pid).items():
                    require(key not in baseline, "P47_REAL_JOB_BASELINE_REUSED")
                    observed[key] = kind
                if ((out / ("ready-" + cycle)).is_file() and
                        any(v == "driver" for v in observed.values()) and
                        any(v == "chrome" for v in observed.values())):
                    break
                require(process.poll() is None, "P47_REAL_JOB_BROWSER_DID_NOT_START")
                time.sleep(.4)
            require((out / ("ready-" + cycle)).is_file(),
                    "P47_SOURCE_EXACT_REAL_NUNIT_READY_MISSING")
            data, precise = p47e.snapshot(chrome)
            members = []
            for (pid, start), kind in sorted(observed.items()):
                if pid not in data or str(data[pid][1]) != start:
                    continue   # transient exited process is not a proven member
                row = live_member(api, native, job, data, precise, pid, start)
                row["kind"] = kind
                members.append(row)
            require(any(r["kind"] == "driver" for r in members) and
                    any(r["kind"] == "chrome" for r in members),
                    "P47_REAL_JOB_ACTUAL_NODE_CHROME_MEMBERSHIP_NOT_PROVEN")
            report["verifiedMembersBeforeFault"] = members
            report["transientUnattested"] = len(observed) - len(members)
            drivers = [r for r in members if r["kind"] == "driver"]
            require(len(drivers) == 1, "P47_REAL_JOB_NODE_NOT_UNIQUE")
            driver = drivers[0]
            report["driverTermination"] = {
                "pid": driver["pid"], "startIdentity": driver["startIdentity"],
                "creationFileTime": driver["creationFileTime"],
                "mechanism": "os.kill(SIGTERM), Windows TerminateProcess"}
            pre_kill = native.inspect(driver["pid"], driver["creationFileTime"])
            require(pre_kill["state"] == "live",
                    "P47_SOURCE_EXACT_NODE_NOT_NATIVE_LIVE_AT_KILL")
            report["driverTermination"]["nativeBeforeKill"] = pre_kill
            p47e.atomic_json(receipt, report)
            os.kill(driver["pid"], signal.SIGTERM)
            report["driverTermination"]["signalSent"] = True
            p47e.atomic_json(receipt, report)
            (out / ("ack-" + cycle)).write_text("driver_killed", encoding="utf-8")
            stdout, stderr = process.communicate(timeout=90)
            report["testRunnerExitCode"] = process.returncode
            trx = out / "real-job.trx"
            require(trx.is_file(), "P47_REAL_JOB_NUNIT_TRX_MISSING")
            report["trxSha256"] = hashlib.sha256(trx.read_bytes()).hexdigest()
            results = p37.read_trx(trx)
            actual = results.get("Operation", "not_observed")
            report["actualNUnitOutcome"] = actual
            report["originalP47TestCaseInventory"] = results
            require(results == {"Operation": expected} and
                    ((process.returncode == 0) == (expected == "Passed")),
                    "P47_SOURCE_EXACT_NUNIT_OUTCOME_MISMATCH")
            primary_in_trx = "P47_PRIMARY_ASSERTION_MUST_SURVIVE" in p47.trx_error(trx)
            require(primary_in_trx == (expected == "Failed"),
                    "P47_SOURCE_EXACT_NUNIT_PRIMARY_EXCEPTION_NOT_PRESERVED")
            trx_text = trx.read_text(encoding="utf-8")
            report["actualBrowserTargetClosed"] = (
                "observed" if p47.LOG_BROWSER in trx_text else "not_observed")
            report["actualPlaywrightDisposeTargetClosed"] = (
                "observed" if p47.LOG_DISPOSE in trx_text else "not_observed")
            survivors = unassisted_gate(chrome, observed, report, out)
            # The 12-second unassisted result is already durable. From here on,
            # all actions are SEPARATE, post-gate host cleanup; they cannot
            # change a failed original observation into PASS.
            report["hostCleanup"] = "pending_after_unassisted_gate"
            p47e.atomic_json(receipt, report)
            postgate.prove_after_gate(
                api, native, job_ref, members, survivors, report,
                lambda: p47e.atomic_json(receipt, report), jobs.member)
            report["hostCleanup"] = "job_handle_closed_after_original_gate"
            report["status"] = ("observed_unassisted_failure_host_cleanup_separate"
                                if survivors else "membership_proven_no_leak_reproduced")
            p47e.atomic_json(receipt, report)
            # Even a PROVEN host termination of native-live Chrome survivors is
            # not proof of automatic Playwright cleanup. Keep CI FAILED.
            require(not survivors,
                    "P47_REAL_JOB_ORPHAN_CHROMIUM_PRESENCE_AFTER_12S")
            print("P47_SOURCE_EXACT_NODE_CHROME_MEMBERSHIP_PASSED_NO_FIX_CLAIM")
    except BaseException as ex:
        report["status"] = "failed"
        report["error"] = str(ex) if isinstance(ex, AssertionError) else type(ex).__name__
        raise
    finally:
        if server is not None:
            server.shutdown()
            server.server_close()
        if job_ref["handle"] is not None:
            report["hostCleanup"] = "emergency_close_not_unassisted_pass"
            api.CloseHandle(job_ref["handle"])
            job_ref["handle"] = None
        if host_handle is not None:
            api.CloseHandle(host_handle)
        if process is not None:
            if process.poll() is None:
                process.kill()
            try:
                process.communicate(timeout=15)
            except subprocess.TimeoutExpired:
                process.kill()
        p47e.atomic_json(receipt, report)
        p47e.write_manifest(out)


if __name__ == "__main__":
    main()
