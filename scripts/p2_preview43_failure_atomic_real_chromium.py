#!/usr/bin/env python3
"""Preview.43: real Chrome / NUnit rollback and source-attested setup contract.
The injected runtime fixture is a public fault *probe*, not product-generated
source. "create-before" is synthetic and does not exercise a Playwright.CreateAsync
internal failure. Actual Launch/NewContext/NewPage API failure paths are included.
"""
import hashlib, json, os, re, shutil, tempfile, textwrap, threading
from pathlib import Path
from http.server import ThreadingHTTPServer
import p2_preview37_rendered_text_unicode_real_chromium as p37
import p2_preview39_external_exception_real_chromium as p39
import p2_preview42_fixture_lifecycle_real_chromium as p42

TEST_PROJECT = p37.CSPROJ.replace(
    '<PackageReference Include="Selenium.WebDriver" Version="4.33.0" />', '')

TESTS = r"""
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Fictional.P43;
public sealed class AtomicFault : Exception {
    public AtomicFault(string stage):base("FAULT:"+stage){}
}
public sealed class CleanupFault : Exception {
    public CleanupFault(string resource):base("CLEANUP:"+resource){}
}
[TestFixture,NonParallelizable] public sealed class FaultFixture {
    private IPlaywright? pw;
    private IBrowser? browser;
    private IBrowserContext? context;
    private IPage? page;
    private string Stage => Environment.GetEnvironmentVariable("POM43_STAGE")!;
    private readonly List<string> cleanupErrors = new();
    private Exception? primary;
    private bool pageCloseAttempted, contextCloseAttempted, browserCloseAttempted;
    private bool pageClosed, contextClosed, browserClosed, pwDisposed, sameContext;
    private bool hadPlaywright, hadBrowser, hadContext, hadPage;
    private bool testRan;

    private async Task CleanupSetup(bool injectSecondary) {
        try {
            if(page != null) {
                pageCloseAttempted=true;
                await page.CloseAsync();
                pageClosed=page.IsClosed;
                if(injectSecondary && Stage=="cleanup-both")
                    throw new CleanupFault("page");
            }
        } catch(Exception e) {cleanupErrors.Add(e.GetType().Name+":"+e.Message);}
        try {
            if(context != null) {
                contextCloseAttempted=true;
                await context.CloseAsync();
                contextClosed=true;
                if(injectSecondary && Stage=="cleanup-both")
                    throw new CleanupFault("context");
            }
        } catch(Exception e) {cleanupErrors.Add(e.GetType().Name+":"+e.Message);}
    }

    private async Task CleanupOne(bool injectSecondary) {
        try {
            if(browser != null) {
                browserCloseAttempted=true;
                await browser.CloseAsync();
                browserClosed=!browser.IsConnected;
                if(injectSecondary && Stage=="cleanup-browser")
                    throw new CleanupFault("browser");
            }
        } catch(Exception e) {cleanupErrors.Add(e.GetType().Name+":"+e.Message);}
        try {
            if(pw != null) {
                pw.Dispose();
                pwDisposed=true;
                if(injectSecondary && Stage=="cleanup-playwright")
                    throw new CleanupFault("playwright");
            }
        } catch(Exception e) {cleanupErrors.Add(e.GetType().Name+":"+e.Message);}
    }

    private void Receipt(string phase) {
        File.WriteAllText(Path.Combine(
            Environment.GetEnvironmentVariable("POM43_EVIDENCE")!,
            "atomic-"+Stage+".json"), JsonSerializer.Serialize(new {
                stage=Stage,phase,primaryType=primary?.GetType().FullName,
                primaryMessage=primary?.Message, cleanupErrors,
                hadPlaywright,hadBrowser,hadContext,hadPage,
                pageCloseAttempted,contextCloseAttempted,browserCloseAttempted,
                pageClosed,contextClosed,browserClosed,pwDisposed,sameContext,
                testRan,
                chromePath=Environment.GetEnvironmentVariable("MIGRATOR_CHROME_BIN")
            }));
    }

    [OneTimeSetUp] public async Task StartAll() {
        try {
            if(Stage=="create-before")throw new AtomicFault("create-before");
            pw=await Microsoft.Playwright.Playwright.CreateAsync();
            hadPlaywright=true;
            if(Stage=="create-after")throw new AtomicFault("create-after");
            var executable=Stage=="launch-actual"
                ?Path.Combine(Path.GetTempPath(),"missing-p43-chrome","chrome-never-exists")
                :Environment.GetEnvironmentVariable("MIGRATOR_CHROME_BIN")!;
            browser=await pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions{
                ExecutablePath=executable,Headless=true,
                Args=new[]{"--no-sandbox","--disable-dev-shm-usage",
                    "--window-size=1280,720"}
            });
            hadBrowser=true;
            if(Stage=="launch-after" || Stage=="cleanup-browser"
                || Stage=="cleanup-playwright")
                throw new AtomicFault(Stage);
        } catch(Exception e) {
            primary=e;
            await CleanupOne(true);
            Receipt("one-time-rollback");
            throw; // original wins even when cleanup raised
        }
    }

    [SetUp] public async Task StartEach() {
        try {
            if(Stage=="context-actual") {
                await browser!.CloseAsync();
                browserClosed=!browser.IsConnected;
            }
            context=await browser!.NewContextAsync();
            hadContext=true;
            if(Stage=="context-after")throw new AtomicFault("context-after");
            if(Stage=="page-actual") {
                await context.CloseAsync();
                contextClosed=true;
            }
            page=await context.NewPageAsync();
            hadPage=true;
            sameContext=ReferenceEquals(page.Context,context);
            if(Stage=="page-after"||Stage=="cleanup-both")
                throw new AtomicFault(Stage);
            if(Stage=="constructor") {
                var _ = new FaultPage(page,context,true);
            }
            else {
                var _ = new FaultPage(page,context,false);
            }
            await page.GotoAsync(Environment.GetEnvironmentVariable("POM43_URL")!);
        } catch(Exception e) {
            primary=e;
            await CleanupSetup(true);
            Receipt("per-test-rollback");
            throw; // do not replace the original
        }
    }

    private sealed class FaultPage {
        public FaultPage(IPage page,IBrowserContext context,bool fail) {
            if(!ReferenceEquals(page.Context,context))
                throw new AtomicFault("wrong-context");
            if(fail)throw new AtomicFault("constructor");
        }
    }

    [Test] public async Task BrowserIsReal() {
        testRan=true;
        Assert.That(page,Is.Not.Null);
        Assert.That(browser!.IsConnected,Is.True);
        var snapshot=await page!.EvaluateAsync<string>(
            "() => JSON.stringify(window.snapshot())");
        Assert.That(snapshot,Does.Contain("ready"));
    }
    [TearDown] public async Task StopEach() {
        await CleanupSetup(false);
        Receipt("test-teardown");
    }
    [OneTimeTearDown] public async Task StopAll() {
        await CleanupSetup(false);
        await CleanupOne(false);
        Receipt("one-time-teardown");
    }
}
"""
STAGES = [
    "healthy","create-before","create-after","launch-actual","launch-after",
    "context-actual","context-after","page-actual","page-after","constructor",
    "cleanup-both","cleanup-browser","cleanup-playwright"
]
REAL_API = {"launch-actual","context-actual","page-actual"}
BEFORE_API = {"create-before"}
ONE_TIME = {"create-before","create-after","launch-actual","launch-after",
    "cleanup-browser","cleanup-playwright"}

def atomic_source(core):
    p42_source=(core/"Migrator.Application.Smoke/PomPreview42FixtureLifecycleSmoke.cs").read_text(encoding="utf-8")
    p43_source=(core/"Migrator.Application.Smoke/PomPreview43FailureAtomicSmoke.cs").read_text(encoding="utf-8")
    page=p42.synthetic("Page",p42_source)
    base=p42.synthetic("Base",p42_source)
    fixture=p42.synthetic("Fixture",p42_source)
    match=re.search(r'private const string AtomicHooks = """\n(.*?)\n        """;',p43_source,re.S)
    p37.check(match is not None,"Canonical C# atomic fixture definition absent")
    hooks=textwrap.dedent(match.group(1))+"\n"
    begin=fixture.index("[OneTimeSetUp]")
    end=fixture.index("[TearDown]")
    return page,base,fixture[:begin]+hooks+fixture[end:]

def main():
    repo=Path(__file__).resolve().parent.parent
    chrome=Path(os.environ["MIGRATOR_CHROME_BIN"])
    driver=Path(os.environ["MIGRATOR_CHROMEDRIVER_BIN"])
    p37.check(chrome.is_file() and driver.is_file(),
        "Actual Chrome and ChromeDriver are required")
    dll=repo/"Migrator.AssemblyCli/bin/Release/net10.0/Migrator.AssemblyCli.dll"
    p37.check(dll.is_file(),"Compiled source-backed CLI missing")
    evidence=repo/"artifacts/pom-preview43"
    evidence.mkdir(parents=True,exist_ok=True)
    page,base,fixture=atomic_source(repo)
    with tempfile.TemporaryDirectory(prefix="fictional-p43-faults-") as tmp:
        work=Path(tmp)
        source=work/"source";source.mkdir()
        for name,body in {"Page.cs":page,"TestBase.cs":base,
            "NUnitFixture.cs":fixture}.items():
            (source/name).write_bytes(body.encode("utf-8"))
        originals={p.name:hashlib.sha256(p.read_bytes()).hexdigest()
            for p in sorted(source.iterdir())}
        sha=p39.cli_json(["dotnet",dll,"pom-inspect",source],repo,0)["sourceTreeHash"]
        contracts={}
        for method in ("Missing","Timeout"):
            args=["dotnet",dll,"pom-failure-atomic-count-contract",
                source,"Fictional.P42.SourcePage",method,sha,
                "Page","Context","Browser","PW"]
            redacted=p39.cli_json(args,repo,0)
            p37.check(redacted["status"]=="requires_review"
                and redacted["fixtureLifecycle"]["oneTimeSetUpFailureCleanup"]
                and redacted["fixtureLifecycle"]["setUpFailureCleanup"]
                and redacted["fixtureLifecycle"]["primaryExceptionRethrown"]
                and redacted["fixtureLifecycle"]["cleanupFailuresDoNotReplacePrimary"]
                and not redacted["canAutoApply"] and not redacted["runtimeVerified"]
                and len(redacted["asyncMethods"])==3
                and all(x["targetBody"] is None and x["targetSignature"] is None
                    for x in redacted["asyncMethods"]),
                "Core atomic source contract absent, partial or leaked: "+str(redacted))
            old=p39.cli_json(["dotnet",dll,"pom-lifecycle-count-contract",
                source,"Fictional.P42.SourcePage",method,sha,
                "Page","Context","Browser","PW"],repo,3)
            p37.check(old["status"]=="blocked" and not old["catches"],
                "preview42 normal setup shape safety was widened")
            contracts[method]={
                "sourceMethodSha256":redacted["sourceMethodSha256"],
                "fixture":redacted["fixtureLifecycle"],
                "catchSha256":redacted["catches"][0]["targetCatchSha256"]
            }
        p37.check(originals=={p.name:hashlib.sha256(p.read_bytes()).hexdigest()
            for p in sorted(source.iterdir())},
            "Roslyn contract analysis modified immutable source")
        # Runtime fault probe deliberately differs from strict source contract:
        # injection branches exercise resource rollback and ORIGINAL errors.
        # They are not automatic target snippets or claims of API internals.
        project=work/"tests";project.mkdir()
        (project/"Synthetic.Tests.csproj").write_text(TEST_PROJECT,encoding="utf-8")
        (project/"FaultFixture.cs").write_text(TESTS,encoding="utf-8")
        build=p37.run(["dotnet","build","-c","Release","--nologo"],project,timeout=360)
        p37.check(build["code"]==0,
            "Real Playwright NUnit fault fixture failed compile: "+str(build))
        deps=project/"bin/Release/net10.0/Synthetic.Tests.deps.json"
        p37.check(deps.is_file() and "Selenium.WebDriver" not in
            deps.read_text(encoding="utf-8") and
            "OpenQA.Selenium" not in TESTS,
            "Standalone Playwright-only runtime has hidden Selenium")
        with ThreadingHTTPServer(("127.0.0.1",0),p37.Handler) as server:
            threading.Thread(target=server.serve_forever,daemon=True).start()
            try:
                outcome={}
                for stage in STAGES:
                    command=["dotnet","test","-c","Release","--no-build",
                        "--no-restore","--nologo",
                        "--logger","trx;LogFileName="+stage+".trx",
                        "--results-directory",evidence,
                        "--filter","FullyQualifiedName~FaultFixture&Name~BrowserIsReal"]
                    # one NUnit invocation per stage to enforce OneTimeSetUp
                    import subprocess
                    env=dict(os.environ,POM43_STAGE=stage,
                        POM43_EVIDENCE=str(evidence),
                        POM43_URL="http://127.0.0.1:"+str(server.server_port)+"/")
                    run=subprocess.run(command,cwd=str(project),env=env,
                        capture_output=True,text=True,timeout=300)
                    trx=evidence/(stage+".trx")
                    p37.check(trx.is_file(),"NUnit TRX missing for "+stage+
                        " "+run.stdout[-1400:]+run.stderr[-1200:])
                    selected=p37.read_trx(trx)
                    expected="Passed" if stage=="healthy" else "Failed"
                    p37.check(selected and set(selected.values())=={expected}
                        and (run.returncode==0)==(stage=="healthy"),
                        "NUnit source fault result mismatch "+stage+" "+
                        str(selected)+" "+run.stdout[-1500:])
                    receipt=evidence/("atomic-"+stage+".json")
                    p37.check(receipt.is_file(),
                        "No rollback/teardown proof for "+stage)
                    state=json.loads(receipt.read_text(encoding="utf-8"))
                    p37.check(state["chromePath"]==str(chrome),
                        "Real browser executable drift "+stage)
                    if stage=="healthy":
                        p37.check(state["testRan"] and not state["primaryType"]
                            and state["pageClosed"] and state["contextClosed"]
                            and state["browserClosed"] and state["pwDisposed"]
                            and state["sameContext"],
                            "Healthy browser/context cleanup incomplete: "+str(state))
                    else:
                        p37.check(state["primaryType"] and not state["testRan"]
                            and (not state["hadPlaywright"] or state["pwDisposed"])
                            and (not state["hadBrowser"] or state["browserClosed"])
                            and (not state["hadPage"] or state["pageCloseAttempted"])
                            and (not state["hadContext"] or state["contextCloseAttempted"]),
                            "Partial failure leaks created resources: "+stage+" "+str(state))
                        if stage not in REAL_API:
                            p37.check(state["primaryType"]=="Fictional.P43.AtomicFault"
                                and state["primaryMessage"]=="FAULT:"+stage,
                                "Cleanup replaced primary failure: "+stage+" "+str(state))
                        if stage in ("cleanup-both","cleanup-browser","cleanup-playwright"):
                            p37.check(any("CleanupFault" in e for e in state["cleanupErrors"]),
                                "Secondary cleanup failure not exercised "+stage)
                    outcome[stage]={
                        "nunit":expected,
                        "primaryType":state["primaryType"],
                        "cleanupExceptions":state["cleanupErrors"],
                        "created":{
                            "playwright":state["hadPlaywright"],
                            "browser":state["hadBrowser"],
                            "context":state["hadContext"],
                            "page":state["hadPage"]
                        },
                        "closed":{
                            "browser":state["browserClosed"],
                            "context":state["contextClosed"],
                            "page":state["pageClosed"],
                            "playwrightDisposed":state["pwDisposed"]
                        }
                    }
            finally:server.shutdown()
        summary={
            "schema":"migrator-preview43-atomic-fault-probes/v1",
            "coreSourceTreeSha256":sha,"sourceFileSha256":originals,
            "sourceProof":contracts,"chromeVersion":os.environ.get("MIGRATOR_CHROME_VERSION"),
            "driverVersion":os.environ.get("MIGRATOR_CHROMEDRIVER_VERSION"),
            "chromeSha256":hashlib.sha256(chrome.read_bytes()).hexdigest(),
            "chromedriverSha256":hashlib.sha256(driver.read_bytes()).hexdigest(),
            "faultStages":outcome,
            "actualApiFailures":sorted(REAL_API),
            "syntheticBeforeCall":sorted(BEFORE_API),
            "syntheticRuntimeFaultProbeNotEmittedByMigrator":True,
            "cleanupFailuresSuppressSecondary":True,
            "secondaryCleanupErrorsObservableOnlyInProbe":True,
            "apiNeverReturnedResourceCannotBeClosed":"not_verified",
            "processLevelLeakInspection":"not_run",
            "sourceNUnitEquivalence":"not_verified_for_fault_injection",
            "historicalSameChromeSeleniumVsPlaywright":"separate_preview42_CI",
            "canAutoApply":False,"privateArBilling":"not_run"
        }
        (evidence/"summary.json").write_text(
            json.dumps(summary,sort_keys=True,indent=2)+"\n",encoding="utf-8")
        manifest={p.name:hashlib.sha256(p.read_bytes()).hexdigest()
            for p in sorted(evidence.iterdir()) if p.is_file()
            and p.name!="SHA256SUMS.json"}
        (evidence/"SHA256SUMS.json").write_text(
            json.dumps(manifest,sort_keys=True,indent=2)+"\n",encoding="utf-8")
        print("POM_PREVIEW43_REAL_CHROMIUM_NUNIT_FAILURE_ATOMIC_PASSED")
        print(json.dumps(summary,sort_keys=True))
if __name__=="__main__":main()
