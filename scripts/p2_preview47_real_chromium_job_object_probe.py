#!/usr/bin/env python3
"""Independent real Playwright/Chromium Win32 Job experiment after original P47.

The actual NUnit testhost waits BEFORE Playwright.CreateAsync. The host
assigns it to a Job, then proves real Node+Chrome descendant membership
using CIM creation time, native FILETIME and IsProcessInJob. Node is killed
with the original Windows os.kill(SIGTERM) fault. The unassisted original
12-second process-presence gate runs BEFORE host Job close. A later host
kill can NEVER promote a failed original gate to PASS.

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

import p2_preview37_rendered_text_unicode_real_chromium as p37
import p2_preview45_sink_close_process_ancestry as p45
import p2_preview47_process_evidence as p47e
import p2_preview47_windows_job_object_probe as jobs
import p1_preview47_job_postgate_proof as postgate

SCHEMA = "migrator-preview47-real-playwright-job/v1"
CHILD_RIGHTS = 0x00100000 | 0x1000
ASSIGN_RIGHTS = CHILD_RIGHTS | 0x0100 | 0x0001

NUNIT = r"""
using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Playwright;
using NUnit.Framework;
namespace Fictional.P47RealJob;
[TestFixture, NonParallelizable] public sealed class RealJobTests {
    static string Root => Environment.GetEnvironmentVariable("P47_REAL_JOB_EVIDENCE")!;
    static string Marker(string n) => Path.Combine(Root, n);
    [Test] public async Task RealNodeAndChromium() {
        // Do not create Playwright's Node driver until host releases the gate.
        File.WriteAllText(Marker("testhost-gate"), Environment.ProcessId.ToString());
        for (int i=0; i<300 && !File.Exists(Marker("job-assigned")); i++)
            await Task.Delay(100);
        Assert.That(File.Exists(Marker("job-assigned")), Is.True,
            "P47_REAL_JOB_PRE_SPAWN_ASSIGNMENT_MISSING");
        var pw = await Playwright.CreateAsync();
        var browser = await pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions {
            ExecutablePath=Environment.GetEnvironmentVariable("MIGRATOR_CHROME_BIN"),
            Headless=true, Args=new[] {"--no-sandbox", "--disable-dev-shm-usage"}
        });
        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.SetContentAsync("<h1>real Chromium ready</h1>");
        Assert.That(await page.Locator("h1").TextContentAsync(),
            Is.EqualTo("real Chromium ready"));
        File.WriteAllText(Marker("browser-ready"), "real");
        for (int i=0; i<300 && !File.Exists(Marker("driver-killed")); i++)
            await Task.Delay(100);
        Assert.That(File.Exists(Marker("driver-killed")), Is.True,
            "P47_REAL_JOB_DRIVER_ACK_MISSING");
        if(Environment.GetEnvironmentVariable("P47_HOST_JOB_EXPECTED_NUNIT") == "Failed")
            Assert.Fail("P47_JOB_PRIMARY_FAILURE_MUST_SURVIVE");
    }
}
"""


def require(ok, code):
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
    expected = os.environ.get("P47_HOST_JOB_EXPECTED_NUNIT", "Failed")
    require(expected in ("Passed", "Failed"), "P47_REAL_JOB_NUNIT_EXPECTATION_INVALID")
    report = {"schema": SCHEMA, "status": "running",
              "actualPlaywrightNodeAndChromium": True,
              "cycle": int(cycle),
              "expectedNUnitOutcome": expected,
              "fixtureKind": "public_synthetic_NUnit_real_Playwright_not_original_P47_fixture",
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
            (work / "RealJobTests.cs").write_text(NUNIT, encoding="utf-8")
            build = p37.run(["dotnet", "build", "-c", "Release", "--nologo"],
                            work, timeout=360)
            require(build["code"] == 0, "P47_REAL_JOB_NUNIT_BUILD_FAILED")
            before, _ = p47e.snapshot(chrome)
            baseline = {(pid, str(row[1])) for pid, row in before.items()
                        if row[2] in ("chrome", "driver")}
            env = dict(os.environ, P47_REAL_JOB_EVIDENCE=str(out),
                       P47_HOST_JOB_EXPECTED_NUNIT=expected)
            command = ["dotnet", "test", "-c", "Release", "--no-build",
                       "--no-restore", "--nologo", "--logger",
                       "trx;LogFileName=real-job.trx", "--results-directory",
                       str(out), "--filter",
                       "FullyQualifiedName~RealJobTests&Name~RealNodeAndChromium"]
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
                if ((out / "browser-ready").is_file() and
                        any(v == "driver" for v in observed.values()) and
                        any(v == "chrome" for v in observed.values())):
                    break
                require(process.poll() is None, "P47_REAL_JOB_BROWSER_DID_NOT_START")
                time.sleep(.4)
            require((out / "browser-ready").is_file(),
                    "P47_REAL_JOB_NO_ACTUAL_BROWSER_CONTENT")
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
            p47e.atomic_json(receipt, report)
            os.kill(driver["pid"], signal.SIGTERM)
            (out / "driver-killed").write_text("driver_killed", encoding="ascii")
            stdout, stderr = process.communicate(timeout=90)
            report["testRunnerExitCode"] = process.returncode
            trx = out / "real-job.trx"
            require(trx.is_file(), "P47_REAL_JOB_NUNIT_TRX_MISSING")
            report["trxSha256"] = hashlib.sha256(trx.read_bytes()).hexdigest()
            actual = p37.read_trx(trx).get("RealNodeAndChromium", "not_observed")
            report["actualNUnitOutcome"] = actual
            require(actual == expected and
                    ((process.returncode == 0) == (expected == "Passed")),
                    "P47_REAL_JOB_NUNIT_OUTCOME_MISMATCH")
            primary_in_trx = "P47_JOB_PRIMARY_FAILURE_MUST_SURVIVE" in trx.read_text(
                encoding="utf-8")
            require(primary_in_trx == (expected == "Failed"),
                    "P47_REAL_JOB_PRIMARY_NUNIT_EXCEPTION_NOT_PRESERVED")
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
            print("P47_ACTUAL_PLAYWRIGHT_NODE_CHROME_HOST_JOB_MEMBERSHIP_PASSED")
    except BaseException as ex:
        report["status"] = "failed"
        report["error"] = str(ex) if isinstance(ex, AssertionError) else type(ex).__name__
        raise
    finally:
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
