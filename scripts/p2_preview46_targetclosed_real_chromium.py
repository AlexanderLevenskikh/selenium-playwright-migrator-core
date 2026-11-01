#!/usr/bin/env python3
"""preview.46: source-exact NUnit TearDown, actual driver disconnect on Chrome.

The exact TearDown C# source comes from Core's Roslyn-positive fixture,
not a hand-written substitute. A real Playwright node driver (proven child
of the dotnet test runner) is terminated by its observed PID/start identity.
No exception is injected as a substitute for TargetClosedException.

NUnit test primary assertion and cleanup fixed-code evidence are kept
separate, including a real failing Console.Error sink on an opt-in stage.
"""
import hashlib
import json
import os
from pathlib import Path
import signal
import subprocess
import tempfile
import threading
import time
import xml.etree.ElementTree as ET
from http.server import ThreadingHTTPServer

import p2_preview37_rendered_text_unicode_real_chromium as p37
import p2_preview39_external_exception_real_chromium as p39
import p2_preview42_fixture_lifecycle_real_chromium as p42
import p2_preview45_sink_close_process_ancestry as p45

TEMPLATE=r"""
using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Playwright;
using NUnit.Framework;
namespace Fictional.P46;

public sealed class ThrowingWriter : StringWriter {
    public int Attempts {get;private set;}
    public override void WriteLine(string? value) {
        Attempts++;
        throw new IOException("P46_INJECTED_SINK_FAILURE");
    }
}
[TestFixture,NonParallelizable] public sealed class RealNUnitFixture {
    private IPlaywright PW = null!;
    private IBrowser Browser = null!;
    private IBrowserContext Context = null!;
    private IPage Page = null!;
    private static ThrowingWriter? Sink;
    private static TextWriter? OriginalError;
    private static string Stage =>
        Environment.GetEnvironmentVariable("P46_STAGE") ?? "unknown";
    private static string DirectoryPath =>
        Environment.GetEnvironmentVariable("P46_EVIDENCE")!;
    [OneTimeSetUp] public async Task StartAll() {
        PW=await Microsoft.Playwright.Playwright.CreateAsync();
        Browser=await PW.Chromium.LaunchAsync(new BrowserTypeLaunchOptions {
            ExecutablePath=Environment.GetEnvironmentVariable("MIGRATOR_CHROME_BIN"),
            Headless=true,Args=new[]{"--no-sandbox",
                "--disable-dev-shm-usage","--window-size=1280,720"}
        });
    }
    [SetUp] public async Task StartEach() {
        Context=await Browser.NewContextAsync();
        Page=await Context.NewPageAsync();
        Assert.That(Object.ReferenceEquals(Page.Context,Context),Is.True);
        await Page.GotoAsync(Environment.GetEnvironmentVariable("P46_URL")!);
    }
    __TEARDOWN_SOURCE__
    [OneTimeTearDown] public async Task StopAll() {
        bool closeAttempted=false,disposed=false;
        string? closeType=null,disposeType=null;
        try {
            try {await Browser.CloseAsync();closeAttempted=true;}
            catch(Exception e) {
                closeAttempted=true;closeType=e.GetType().FullName;
            }
        } finally {
            try {PW.Dispose();disposed=true;}
            catch(Exception e) {disposed=true;disposeType=e.GetType().FullName;}
            if(OriginalError!=null) {
                Console.SetError(OriginalError);
            }
            File.WriteAllText(Path.Combine(DirectoryPath,
                "one-time-"+Environment.GetEnvironmentVariable("P46_RUN")+".json"),
                System.Text.Json.JsonSerializer.Serialize(new {
                    closeAttempted,disposed,closeType,disposeType,
                    sinkAttempts=Sink?.Attempts ?? 0
                }));
        }
    }
    [Test] public async Task Operation() {
        var snap=await Page.EvaluateAsync<string>(
            "() => JSON.stringify(window.snapshot())");
        Assert.That(snap,Does.Contain("ready"));
        if(Stage.StartsWith("disconnect",StringComparison.Ordinal)) {
            var ready=Path.Combine(DirectoryPath,
                "ready-"+Environment.GetEnvironmentVariable("P46_RUN"));
            var ack=Path.Combine(DirectoryPath,
                "ack-"+Environment.GetEnvironmentVariable("P46_RUN"));
            File.WriteAllText(ready,"ready");
            for(var i=0;i<160&&!File.Exists(ack);i++)await Task.Delay(200);
            Assert.That(File.Exists(ack),Is.True,"P46_DRIVER_KILL_ACK_MISSING");
            Assert.That(File.ReadAllText(ack),Is.EqualTo("driver_killed"),
                "P46_REAL_DRIVER_KILL_NOT_CONFIRMED");
        }
        if(Stage=="disconnect_sink_primary") {
            OriginalError=Console.Error;
            Sink=new ThrowingWriter();
            Console.SetError(Sink);
        }
        if(Stage=="healthy_primary" ||
            Stage=="disconnect_primary" ||
            Stage=="disconnect_primary_repeat" ||
            Stage=="disconnect_sink_primary")
            Assert.Fail("P46_PRIMARY_ASSERTION_MUST_SURVIVE");
    }
}
"""
STAGES=(
 ("healthy_pass","Passed",False),
 ("healthy_primary","Failed",False),
 ("disconnect_primary","Failed",True),
 ("disconnect_pass","Passed",True),
 ("disconnect_primary_repeat","Failed",True),
 ("disconnect_sink_primary","Failed",True),
)
LOG_PAGE="MIGRATOR_CLEANUP_PAGE_CLOSE_FAILED"
LOG_CONTEXT="MIGRATOR_CLEANUP_CONTEXT_CLOSE_FAILED"

def trx_error(path):
    root=ET.parse(path).getroot()
    for node in root.iter():
        if node.tag.endswith("UnitTestResult"):
            for sub in node.iter():
                if sub.tag.endswith("Message"):
                    return sub.text or ""
    return ""

def runtime_canonical(source):
    # Extract exact raw C# method from public AST smoke, source unchanged.
    return p42.synthetic("TypedTearDown",source)

def run_probe(stage,outcome,disconnect,project,evidence,chrome,url):
    run_name=stage
    env=dict(os.environ,P46_STAGE=stage,P46_RUN=run_name,
             P46_EVIDENCE=str(evidence),P46_URL=url)
    command=["dotnet","test","-c","Release","--no-build",
        "--no-restore","--nologo",
        "--logger","trx;LogFileName="+run_name+".trx",
        "--results-directory",str(evidence),
        "--filter","FullyQualifiedName~RealNUnitFixture&Name~Operation"]
    baseline=p45.snapshot(chrome)
    started=time.monotonic()
    process=subprocess.Popen(command,cwd=project,env=env,
        stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,
        encoding="utf-8",errors="replace")
    observed={}
    observed_paths={}
    driver_pid=None
    driver_start=None
    driver_terminated=False
    exceeded=started+230
    ready=evidence/("ready-"+run_name)
    ack=evidence/("ack-"+run_name)
    while process.poll() is None:
        data=p45.snapshot(chrome)
        for pid,(parent,start,kind) in data.items():
            if kind not in ("chrome","driver"): continue
            chain=p45.ancestry(data,pid,process.pid)
            if chain is None: continue
            key=(pid,str(start))
            observed[key]=kind
            observed_paths[key]=[
                {"pid":member,"parentPid":data[member][0],
                 "startIdentity":str(data[member][1]),
                 "kind":data[member][2]}
                for member in chain if member in data]
        if disconnect and ready.is_file() and not ack.exists():
            candidates=sorted((pid,start) for (pid,start),kind in observed.items()
                if kind=="driver" and pid in data
                and str(data[pid][1])==start)
            if not candidates:
                process.kill()
                raise AssertionError("P46_NO_OWNED_REAL_DRIVER_PROCESS: "+stage)
            driver_pid,driver_start=candidates[0]
            os.kill(driver_pid,signal.SIGTERM)
            driver_terminated=True
            ack.write_text("driver_killed",encoding="utf-8")
        if time.monotonic()>exceeded:
            process.kill()
            raise AssertionError("P46_NUNIT_TIMEOUT: "+stage)
        time.sleep(.25 if os.name!="nt" else .4)
    stdout,stderr=process.communicate(timeout=30)
    last=p45.snapshot(chrome)
    survivors=set()
    limit=time.monotonic()+12
    while True:
        last=p45.snapshot(chrome)
        survivors={(pid,start) for pid,start in observed
            if pid in last and str(last[pid][1])==start}
        if not survivors or time.monotonic()>limit:break
        time.sleep(.4)
    trx=evidence/(run_name+".trx")
    p37.check(trx.is_file(),"P46_REAL_NUNIT_TRX_ABSENT: "+stage+
        stdout[-1200:]+stderr[-800:])
    values=p37.read_trx(trx)
    p37.check(values=={"Operation":outcome}
        and (process.returncode==0)==(outcome=="Passed"),
        "P46_NUNIT_OUTCOME_MISMATCH: "+stage+" "+str(values)+
        stdout[-1300:])
    if disconnect:
        p37.check(driver_terminated and ack.read_text(encoding="utf-8")
            =="driver_killed" and driver_pid is not None,
            "P46_DRIVER_KILL_NOT_REAL")
    message=trx_error(trx)
    if outcome=="Failed":
        p37.check("P46_PRIMARY_ASSERTION_MUST_SURVIVE" in message,
            "P46_NUNIT_PRIMARY_EXCEPTION_REPLACED: "+stage+" "+message)
    else:
        p37.check("P46_PRIMARY_ASSERTION_MUST_SURVIVE" not in message,
            "P46_FALSE_PRIMARY_FAILURE")
    codes=trx.read_text(encoding="utf-8")
    page_caught=LOG_PAGE in codes
    context_caught=LOG_CONTEXT in codes
    receipt=evidence/("one-time-"+run_name+".json")
    p37.check(receipt.is_file(),"P46_ONE_TIME_RESOURCE_CLEANUP_RECEIPT_MISSING")
    state=json.loads(receipt.read_text(encoding="utf-8"))
    p37.check(state["closeAttempted"] and state["disposed"],
        "P46_ONE_TIME_RESOURCE_RELEASE_NOT_ATTEMPTED")
    if stage=="disconnect_sink_primary" and os.name=="nt":
        p37.check(state["sinkAttempts"]>0,
            "P46_REAL_TARGETCLOSED_DIAGNOSTIC_SINK_NOT_EXERCISED_WINDOWS")
    if not disconnect:
        p37.check(not page_caught and not context_caught,
            "P46_UNEXPECTED_TARGET_CLOSED_ON_HEALTHY_BROWSER")
    p37.check(not survivors,"P46_OWNED_OBSERVED_CHILD_PIDS_SURVIVED: "+
        str(sorted(survivors)))
    p37.check(any(v=="chrome" for v in observed.values())
        and any(v=="driver" for v in observed.values()),
        "P46_LIVE_OWNED_CHROME_DRIVER_NOT_OBSERVED")
    return {
        "run":stage,"expected":outcome,"actual":values["Operation"],
        "realDriverKilled":driver_terminated,
        "observedTargetClosedPage":page_caught,
        "observedTargetClosedContext":context_caught,
        "targetClosedCatchObserved":page_caught or context_caught or
            state["sinkAttempts"]>0,
        "primaryTestFailurePreserved":outcome=="Failed",
        "sinkDiagnosticWriteAttempts":state["sinkAttempts"],
        "oneTimeCloseAttempted":state["closeAttempted"],
        "oneTimeDisposeAttempted":state["disposed"],
        "oneTimeCloseExceptionType":state["closeType"],
        "oneTimeDisposeExceptionType":state["disposeType"],
        "observedChrome":sum(x=="chrome" for x in observed.values()),
        "observedDriver":sum(x=="driver" for x in observed.values()),
        "observedOwnedAncestry":[
            {"observedPid":pid,"startIdentity":start,
             "kind":kind,"pathToTestProcess":observed_paths[(pid,start)]}
            for (pid,start),kind in sorted(observed.items())
        ],
        "survivingObservedProcessCount":len(survivors),
        "elapsedSec":round(time.monotonic()-started,2)
    }

def main():
    repo=Path(__file__).resolve().parent.parent
    chrome=Path(os.environ["MIGRATOR_CHROME_BIN"])
    driver=Path(os.environ["MIGRATOR_CHROMEDRIVER_BIN"])
    p37.check(chrome.is_file() and driver.is_file(),
        "P46_EXPLICIT_CHROME_DRIVER_NOT_INSTALLED")
    dll=repo/"Migrator.AssemblyCli/bin/Release/net10.0/Migrator.AssemblyCli.dll"
    p37.check(dll.is_file(),"P46_TYPED_SOURCE_CLI_UNAVAILABLE")
    evidence=repo/"artifacts/pom-preview46"
    evidence.mkdir(parents=True,exist_ok=True)
    fixture_file=(repo/"Migrator.Application.Smoke/PomPreview46TypedTargetClosedSmoke.cs")
    source=fixture_file.read_text(encoding="utf-8")
    teardown=runtime_canonical(source)
    p37.check("TargetClosedException" in teardown and
        "IBrowserContext" not in teardown and
        "MIGRATOR_CLEANUP_CONTEXT_CLOSE_FAILED" in teardown,
        "P46_CANONICAL_SOURCE_TEARDOWN_MISSING")
    with tempfile.TemporaryDirectory(prefix="p46-nunit-targetclosed-") as temporary:
        work=Path(temporary);pom=work/"source";pom.mkdir()
        # Full source tree is the exact preview.46 Roslyn-positive source.
        original=p42.synthetic("Fixture",
            (repo/"Migrator.Application.Smoke/PomPreview42FixtureLifecycleSmoke.cs")
                .read_text(encoding="utf-8"))
        atomic=(repo/"Migrator.Application.Smoke/PomPreview43FailureAtomicSmoke.cs")
            .read_text(encoding="utf-8")
        import re,textwrap
        hook=re.search(r'private const string AtomicHooks = """\n(.*?)\n        """;',atomic,re.S)
        p37.check(hook is not None,"P46_CANONICAL_ATOMIC_HOOKS_MISSING")
        frompos=original.index("[OneTimeSetUp]")
        topos=original.index("[TearDown]")
        fixture=original[:frompos]+textwrap.dedent(hook.group(1))+"\n"+original[topos:]
        cursor=0
        for code in ("MIGRATOR_CLEANUP_BROWSER_CLOSE_FAILED",
                     "MIGRATOR_CLEANUP_PLAYWRIGHT_DISPOSE_FAILED",
                     "MIGRATOR_CLEANUP_PAGE_CLOSE_FAILED",
                     "MIGRATOR_CLEANUP_CONTEXT_CLOSE_FAILED"):
            ix=fixture.find("catch {}",cursor)
            p37.check(ix>=0,"P46_CANONICAL_ATOMIC_DIAGNOSTIC_MISSING")
            replacement='catch { try { System.Console.Error.WriteLine("'+code+'"); } catch {} }'
            fixture=fixture[:ix]+replacement+fixture[ix+len("catch {}"):]
            cursor=ix+len(replacement)
        old=p42.synthetic("OriginalTearDown",source).strip()
        new=teardown.strip()
        p37.check(fixture.split(old).__len__()==2,
            "P46_SOURCE_TEAR_DOWN_EXTRA_OR_MISSING")
        fixture=fixture.replace(old,new)
        for name,content in {
            "Page.cs":p42.synthetic("Page",
                (repo/"Migrator.Application.Smoke/PomPreview42FixtureLifecycleSmoke.cs")
                    .read_text(encoding="utf-8")),
            "TestBase.cs":p42.synthetic("Base",
                (repo/"Migrator.Application.Smoke/PomPreview42FixtureLifecycleSmoke.cs")
                    .read_text(encoding="utf-8")),
            "NUnitFixture.cs":fixture
        }.items():
            (pom/name).write_bytes(content.encode("utf-8"))
        file_hash={p.name:hashlib.sha256(p.read_bytes()).hexdigest()
            for p in pom.iterdir()}
        tree=p39.cli_json(["dotnet",dll,"pom-inspect",pom],repo,0)["sourceTreeHash"]
        verified={}
        for method in ("Missing","Timeout"):
            args=["dotnet",dll,"pom-targetclosed-teardown",pom,
                "Fictional.P42.SourcePage",method,tree,
                "Page","Context","Browser","PW"]
            result=p39.cli_json(args,repo,0)
            facts=result["fixtureLifecycle"]
            p37.check(result["status"]=="requires_review"
                and result["schemaVersion"]=="migrator-pom-typed-target-closed-teardown/v1"
                and facts["typedTargetClosedTearDownSourceOnly"]
                and facts["contextTargetClosedCatchGuarded"]
                and facts["pageTargetClosedCatchGuarded"]
                and facts["testFailurePrecedenceOnlyForTargetClosed"]
                and len(facts["contextTargetClosedCatchSha256"])==64
                and len(facts["pageTargetClosedCatchSha256"])==64
                and len(result["asyncMethods"])==3
                and not result["canAutoApply"] and not result["runtimeVerified"]
                and all(x["targetBody"] is None for x in result["asyncMethods"]),
                "P46_SOURCE_CONTRACT_UNVERIFIED: "+str(result))
            prev=p39.cli_json(["dotnet",dll,"pom-cleanup-fault-witness",
                pom,"Fictional.P42.SourcePage",method,tree,
                "Page","Context","Browser","PW"],repo,3)
            p37.check(prev["status"]=="blocked" and not prev["catches"],
                "P46_HISTORICAL_FAULT_WITNESS_WAS_WIDENED")
            verified[method]={
                "sourceMethodSha256":result["sourceMethodSha256"],
                "fixtureTearDownSha256":facts["tearDownSha256"],
                "pageCatchSha256":facts["pageTargetClosedCatchSha256"],
                "contextCatchSha256":facts["contextTargetClosedCatchSha256"],
                "pageCatchLine":facts["pageTargetClosedCatchLine"],
                "contextCatchLine":facts["contextTargetClosedCatchLine"]
            }
        p37.check({p.name:hashlib.sha256(p.read_bytes()).hexdigest()
            for p in pom.iterdir()}==file_hash,
            "P46_READ_ONLY_SOURCE_MUTATION")
        runtime=work/"target-only";runtime.mkdir()
        csproj=p37.CSPROJ.replace(
            '<PackageReference Include="Selenium.WebDriver" Version="4.33.0" />',"")
        p37.check(csproj!=p37.CSPROJ and "OpenQA.Selenium" not in TEMPLATE,
            "P46_TARGET_RUNTIME_CONTAINS_SELENIUM")
        (runtime/"Synthetic.Tests.csproj").write_text(csproj,encoding="utf-8")
        (runtime/"RealNUnitFixture.cs").write_text(
            TEMPLATE.replace("__TEARDOWN_SOURCE__",teardown),encoding="utf-8")
        build=p37.run(["dotnet","build","-c","Release","--nologo"],
            runtime,timeout=360)
        p37.check(build["code"]==0,"P46_ACTUAL_NUNIT_ASSEMBLY_COMPILE_FAILED: "+
            str(build))
        deps=runtime/"bin/Release/net10.0/Synthetic.Tests.deps.json"
        p37.check(deps.is_file() and
            "Selenium.WebDriver" not in deps.read_text(encoding="utf-8"),
            "P46_TARGET_DEPENDS_ON_SELENIUM")
        with ThreadingHTTPServer(("127.0.0.1",0),p37.Handler) as server:
            threading.Thread(target=server.serve_forever,daemon=True).start()
            try:
                outcomes={}
                for stage,expected,disconnect in STAGES:
                    outcomes[stage]=run_probe(stage,expected,disconnect,
                        runtime,evidence,str(chrome),
                        "http://127.0.0.1:"+str(server.server_port)+"/")
            finally:server.shutdown()
        # OS-specific observation must not be magically invented.
        positives=[name for name,info in outcomes.items()
            if name.startswith("disconnect") and
                info["targetClosedCatchObserved"]]
        if os.name=="nt":
            p37.check(bool(positives),
                "P46_WINDOWS_POSITIVE_REAL_TARGET_CLOSED_NOT_OBSERVED")
        else:
            p37.check(not positives,
                "P46_LINUX_TARGET_CLOSED_OBSERVED: do not label as not_observed "+
                str(positives))
        summary={
            "schema":"migrator-preview46-targetclosed-source-owned-nunit/v1",
            "sourceTreeHash":tree,"sourceFilesSha256":file_hash,
            "sourceContracts":verified,
            "chromeVersion":os.environ.get("MIGRATOR_CHROME_VERSION"),
            "driverVersion":os.environ.get("MIGRATOR_CHROMEDRIVER_VERSION"),
            "chromeSha256":hashlib.sha256(chrome.read_bytes()).hexdigest(),
            "outcomes":outcomes,
            "platformTargetClosedObservation":
                "observed" if positives else "not_observed",
            "positiveStages":positives,
            "runtimeTargetAssemblyHasSelenium":False,
            "driverDisconnect":"actual observed node process killed",
            "sourceTearDownExactlyInserted":True,
            "primaryExceptionPrecedenceForTargetClosedOnly":True,
            "unrelatedCleanupErrors":"blocked_not_proven",
            "oneTimeTearDownFailures":"not_source_admitted",
            "otherUnobservedOsProcesses":"not_verified",
            "autoApply":False,"ArBilling":"not_run"
        }
        (evidence/"summary.json").write_text(
            json.dumps(summary,sort_keys=True,indent=2)+"\n",encoding="utf-8")
        checksums={p.name:hashlib.sha256(p.read_bytes()).hexdigest()
            for p in sorted(evidence.iterdir())
            if p.is_file() and p.name!="SHA256SUMS.json"}
        (evidence/"SHA256SUMS.json").write_text(
            json.dumps(checksums,sort_keys=True,indent=2)+"\n",encoding="utf-8")
        print("POM_PREVIEW46_REAL_TARGET_CLOSED_NUNIT_PRECEDENCE_PASSED")
        print(json.dumps(summary,sort_keys=True))
if __name__=="__main__":main()
