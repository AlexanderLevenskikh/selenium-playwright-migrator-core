#!/usr/bin/env python3
"""preview.42 actual Selenium vs generated Playwright NUnit owned lifecycle.

The planner receives an exact source-only capture. Real original Selenium
and generated Playwright NUnit fixtures execute on the same explicit Chrome.
Failure tests prove TearDown/OneTimeTearDown cleanup; no Selenium in target.
"""
import hashlib
import json
import os
from pathlib import Path
import re
import tempfile
import textwrap
import threading
from http.server import ThreadingHTTPServer

import p2_preview37_rendered_text_unicode_real_chromium as p37
import p2_preview38_source_exception_real_chromium as p38
import p2_preview39_external_exception_real_chromium as p39

SRC = r"""
using System;
using System.IO;
using NUnit.Framework;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using Microsoft.Playwright;
namespace Fictional.P42;
[TestFixture] public sealed class SeleniumActual : TestBase {
    private ChromeDriver? driver;
    private string exceptionType = "";
    [SetUp] public void Setup() {
        var chrome = Environment.GetEnvironmentVariable("MIGRATOR_CHROME_BIN")!;
        var driverPath = Environment.GetEnvironmentVariable("MIGRATOR_CHROMEDRIVER_BIN")!;
        var opts = new ChromeOptions {BinaryLocation=chrome};
        opts.AddArgument("--headless=new");opts.AddArgument("--no-sandbox");
        opts.AddArgument("--disable-dev-shm-usage");
        opts.AddArgument("--window-size=1280,720");
        var service=ChromeDriverService.CreateDefaultService(
            Path.GetDirectoryName(driverPath)!,Path.GetFileName(driverPath));
        service.HideCommandPromptWindow=true;
        driver=new ChromeDriver(service,opts);
        driver.Navigate().GoToUrl(Environment.GetEnvironmentVariable("POM42_URL")!);
        page=new SourcePage(driver,null!,null!);
    }
    [TearDown] public void Teardown() {
        var name=TestContext.CurrentContext.Test.Name;
        var folder=Environment.GetEnvironmentVariable("POM42_EVIDENCE")!;
        var result=TestContext.CurrentContext.Result.Outcome.Status.ToString();
        string? snapshot=null;bool closed=false;
        try {
            snapshot=(string)((IJavaScriptExecutor)driver!).ExecuteScript(
                "return JSON.stringify(window.snapshot())")!;
        } finally {
            try {driver!.Quit();}finally {driver!.Dispose();closed=true;}
        }
        File.WriteAllText(Path.Combine(folder,"selenium-"+name+".json"),
            System.Text.Json.JsonSerializer.Serialize(new {
                outcome=result,exceptionType=exceptionType==""?null:exceptionType,
                snapshot,browserClosed=closed,
                binaryPath=Environment.GetEnvironmentVariable("MIGRATOR_CHROME_BIN")
            }));
        if(!closed || snapshot==null) Assert.Fail("source cleanup or snapshot failure");
    }
    private void Run(Action action) {
        try {action();} catch(Exception e) {exceptionType=e.GetType().FullName!;throw;}
    }
    [Test] public void ExternalMissingCount() => Run(()=>{
        try {ForwardMissing();}
        catch(NoSuchElementException){
            Assert.That(driver!.FindElements(By.Id("never-created")).Count,Is.EqualTo(0));
        }
    });
    [Test] public void ExternalTimeoutCount() => Run(()=>{
        try {ForwardTimeout();}
        catch(WebDriverTimeoutException){
            Assert.That(driver!.FindElements(By.Id("loader")).Count,Is.EqualTo(1));
        }
    });
    [Test,Explicit("source retains source CLR exception recovery")]
    public void UnadaptedMissing() => ExternalMissingCount();
    [Test,Explicit("source retains source CLR exception recovery")]
    public void UnadaptedTimeout() => ExternalTimeoutCount();
}
"""
TARGET = r"""
using System;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using Microsoft.Playwright;
namespace Fictional.P42;

internal sealed class TargetPage {
    private readonly IPage Page;
    private readonly IBrowserContext Context;
    public TargetPage(IPage page,IBrowserContext context){Page=page;Context=context;}
    public bool SameContext => object.ReferenceEquals(Page.Context,Context);
    __ROOT_MISSING__
    __ROOT_TIMEOUT__
}
internal class TargetBase {
    protected TargetPage page = null!;
    __FORWARD_MISSING__
    __FORWARD_TIMEOUT__
}
[TestFixture,NonParallelizable] public sealed class PlaywrightActual:TargetBase {
    private IPlaywright PW = null!;
    private IBrowser Browser = null!;
    private IBrowserContext Context = null!;
    private IPage Page = null!;
    private bool injectedSameContext;
    private bool perTestContextClosed;
    private string? exceptionType;
    [OneTimeSetUp] public async Task StartAll() {
        PW=await Microsoft.Playwright.Playwright.CreateAsync();
        Browser=await PW.Chromium.LaunchAsync(new BrowserTypeLaunchOptions {
            ExecutablePath=Environment.GetEnvironmentVariable("MIGRATOR_CHROME_BIN"),
            Headless=true,
            Args=new[]{"--no-sandbox","--disable-dev-shm-usage","--window-size=1280,720"}
        });
    }
    [SetUp] public async Task StartEach() {
        exceptionType=null;
        perTestContextClosed=false;
        Context=await Browser.NewContextAsync();
        Page=await Context.NewPageAsync();
        page=new TargetPage(Page,Context);
        injectedSameContext=page.SameContext &&
            object.ReferenceEquals(Page.Context,Context);
        await Page.GotoAsync(Environment.GetEnvironmentVariable("POM42_URL")!);
    }
    [TearDown] public async Task StopEach() {
        var name=TestContext.CurrentContext.Test.Name;
        var folder=Environment.GetEnvironmentVariable("POM42_EVIDENCE")!;
        var outcome=TestContext.CurrentContext.Result.Outcome.Status.ToString();
        string? snapshot=null,screen=null,error=null;
        try {
            snapshot=await Page.EvaluateAsync<string>(
                "() => JSON.stringify(window.snapshot())");
            if(outcome=="Failed") {
                screen="playwright-"+name+"-failure.png";
                await Page.ScreenshotAsync(new PageScreenshotOptions{
                    Path=Path.Combine(folder,screen)
                });
            }
        } catch(Exception e) {error=e.GetType().FullName;}
        bool pageClosed=false;
        try {
            try {
                await Page.CloseAsync();
                pageClosed=Page.IsClosed;
            } finally {
                await Context.CloseAsync();
                perTestContextClosed=true;
            }
        } finally {
            File.WriteAllText(Path.Combine(folder,"playwright-"+name+".json"),
                System.Text.Json.JsonSerializer.Serialize(new {
                    outcome,exceptionType,snapshot,screenshot=screen,error,
                    pageClosed,contextClosed=perTestContextClosed,
                    injectedSameContext,
                    browserConnectedBeforeOneTimeCleanup=Browser.IsConnected,
                    binaryPath=Environment.GetEnvironmentVariable("MIGRATOR_CHROME_BIN")
                }));
        }
        if(error!=null || !pageClosed || !perTestContextClosed
            || !injectedSameContext || snapshot==null)
            Assert.Fail("target cleanup, Page/Context binding or snapshot failure");
    }
    [OneTimeTearDown] public async Task StopAll() {
        bool closed=false,disposed=false;
        try {
            await Browser.CloseAsync();
            closed=!Browser.IsConnected;
        } finally {
            PW.Dispose();
            disposed=true;
            File.WriteAllText(Path.Combine(
                Environment.GetEnvironmentVariable("POM42_EVIDENCE")!,
                "playwright-one-time-"+TestContext.CurrentContext.Test.Name+".json"),
                System.Text.Json.JsonSerializer.Serialize(new {closed,disposed}));
        }
    }
    private async Task Run(Func<Task> callback) {
        try {await callback();}
        catch(Exception e) {exceptionType=e.GetType().FullName;throw;}
    }
    __OUTER_MISSING__
    __OUTER_TIMEOUT__
    [Test] public Task ExternalMissingCount()=>Run(CatchMissing);
    [Test] public Task ExternalTimeoutCount()=>Run(CatchTimeout);
    [Test,Explicit("unadapted exception contract must fail")]
    public Task UnadaptedMissing()=>Run(()=>page.Missing());
    [Test,Explicit("unadapted exception contract must fail")]
    public Task UnadaptedTimeout()=>Run(()=>page.Timeout());
}
"""
TESTS={
    "ExternalMissingCount":(True,None),
    "ExternalTimeoutCount":(True,None),
    "UnadaptedMissing":(False,"Microsoft.Playwright.PlaywrightException"),
    "UnadaptedTimeout":(False,"System.TimeoutException")
}
TOKENS={
    "Missing":["__ROOT_MISSING__","__FORWARD_MISSING__","__OUTER_MISSING__"],
    "Timeout":["__ROOT_TIMEOUT__","__FORWARD_TIMEOUT__","__OUTER_TIMEOUT__"]
}
def synthetic(name,cs):
    marker="internal const string "+name+' = """'
    match=re.search(re.escape(marker)+r"\n(.*?)\n        \"\"\";",cs,re.S)
    p37.check(match is not None,"Source AST canonical fixture missing: "+name)
    return textwrap.dedent(match.group(1))+"\n"

def main():
    repo=Path(__file__).resolve().parent.parent
    chrome=Path(os.environ["MIGRATOR_CHROME_BIN"])
    chromedriver=Path(os.environ["MIGRATOR_CHROMEDRIVER_BIN"])
    p37.check(chrome.is_file() and chromedriver.is_file(),
        "Exact Chrome and ChromeDriver required")
    cli=repo/"Migrator.AssemblyCli/bin/Release/net10.0/Migrator.AssemblyCli.dll"
    p37.check(cli.is_file(),"Core CLI missing")
    evidence=repo/"artifacts/pom-preview42"
    evidence.mkdir(parents=True,exist_ok=True)

    fixtureSource=(repo/"Migrator.Application.Smoke/PomPreview42FixtureLifecycleSmoke.cs")\
        .read_text(encoding="utf-8")
    page=synthetic("Page",fixtureSource)
    helper=synthetic("Base",fixtureSource)
    fixture=synthetic("Fixture",fixtureSource)

    with ThreadingHTTPServer(("127.0.0.1",0),p37.Handler) as server:
        threading.Thread(target=server.serve_forever,daemon=True).start()
        try:
            with tempfile.TemporaryDirectory(prefix="fictional-p42-browser-") as td:
                root=Path(td)
                source=root/"source";source.mkdir()
                for name,content in {
                    "Page.cs":page,"TestBase.cs":helper,
                    "NUnitFixture.cs":fixture
                }.items():
                    (source/name).write_bytes(content.encode("utf-8"))
                hashes={p.name:hashlib.sha256(p.read_bytes()).hexdigest()
                    for p in sorted(source.glob("*.cs"))}
                sha=p39.cli_json(["dotnet",cli,"pom-inspect",source],repo,0)["sourceTreeHash"]
                target=TARGET
                plans={}
                for method,placeholders in TOKENS.items():
                    old=p39.cli_json(["dotnet",cli,"pom-async-count-contract",
                        source,"Fictional.P42.SourcePage",method,sha,"Page"],repo,3)
                    p37.check(old["status"]=="blocked" and not old["asyncMethods"],
                        "preview.41 constructor-only contract was weakened")
                    args=["dotnet",cli,"pom-lifecycle-count-contract",source,
                        "Fictional.P42.SourcePage",method,sha,"Page","Context",
                        "Browser","PW"]
                    hidden=p39.cli_json(args,repo,0)
                    p37.check(hidden["status"]=="requires_review"
                        and hidden["fixtureLifecycle"]["pageAndContextConstructorInjected"]
                        and hidden["fixtureLifecycle"]["perTestCleanupInFinally"]
                        and hidden["fixtureLifecycle"]["oneTimeCleanupInFinally"]
                        and all(m["targetBody"] is None and m["targetSignature"] is None
                            for m in hidden["asyncMethods"])
                        and hidden["catches"][0]["targetCatchClause"] is None,
                        "Default lifecycle CLI leaks target code or lacks ownership proof")
                    plan=p39.cli_json(args+["--emit-target"],repo,0)
                    p37.check(plan["status"]=="requires_review"
                        and len(plan["asyncMethods"])==3 and not plan["canAutoApply"]
                        and not plan["runtimeVerified"]
                        and plan["verifiedPageReceiver"]=="Page"
                        and plan["fixtureLifecycle"]["pageFromContext"],
                        "No source-verified lifecycle proposal: "+str(plan))
                    plans[method]=plan
                    for entry,placeholder in zip(plan["asyncMethods"],placeholders):
                        target=p38.patch(target,placeholder,
                            entry["targetSignature"]+" {\n"+entry["targetBody"]+"\n}",
                            "Source-backed async Count "+method)
                for receiver in ("GuessedPage","UnknownContext"):
                    flags=["Page","Context","Browser","PW"]
                    flags[0 if receiver=="GuessedPage" else 1]=receiver
                    invalid=p39.cli_json(["dotnet",cli,
                        "pom-lifecycle-count-contract",source,
                        "Fictional.P42.SourcePage","Missing",sha,*flags,
                        "--emit-target"],repo,3)
                    p37.check(invalid["status"]=="blocked" and not invalid["asyncMethods"]
                        and not invalid["catches"],"Guessed receiver accepted")
                p37.check({p.name:hashlib.sha256(p.read_bytes()).hexdigest()
                    for p in sorted(source.glob("*.cs"))}==hashes,
                    "Read-only contract mutated source")
                p37.check("OpenQA.Selenium" not in target
                    and not re.search(r"__[A-Z][A-Z0-9_]*__",target),
                    "Target has unresolved snippets or Selenium dependency")

                project=root/"tests";project.mkdir()
                for name,content in {
                    "Synthetic.Tests.csproj":p37.CSPROJ,
                    "Page.cs":page,"TestBase.cs":helper,
                    "NUnitFixture.cs":fixture,
                    "SeleniumActual.cs":SRC,"PlaywrightActual.cs":target
                }.items():
                    (project/name).write_bytes(content.encode("utf-8"))
                build=p37.run(["dotnet","build","-c","Release","--nologo"],
                    project,timeout=360)
                p37.check(build["code"]==0,"Actual source/target NUnit compile failed "+str(build))
                only=root/"target-only";only.mkdir()
                noSelenium=p37.CSPROJ.replace(
                    '<PackageReference Include="Selenium.WebDriver" Version="4.33.0" />',"")
                p37.check(noSelenium!=p37.CSPROJ and
                    "OpenQA.Selenium" not in target,"Playwright has hidden source type")
                for name,content in {
                    "Synthetic.Tests.csproj":noSelenium,
                    "PlaywrightActual.cs":target
                }.items():
                    (only/name).write_bytes(content.encode("utf-8"))
                buildOnly=p37.run(["dotnet","build","-c","Release","--nologo"],
                    only,timeout=360)
                p37.check(buildOnly["code"]==0,
                    "Standalone Playwright-only NUnit compile failed "+str(buildOnly))
                deps=only/"bin/Release/net10.0/Synthetic.Tests.deps.json"
                p37.check(deps.is_file() and "Selenium.WebDriver" not in
                    deps.read_text(encoding="utf-8"),"Selenium runtime dependency")

                os.environ["POM42_URL"]="http://127.0.0.1:"+str(server.server_port)+"/"
                os.environ["POM42_EVIDENCE"]=str(evidence)
                results={}
                for name,(adapted,expectedFailure) in TESTS.items():
                    results[name]={}
                    for engine,fixtureName in (
                        ("selenium","SeleniumActual"),
                        ("playwright","PlaywrightActual")):
                        command=["dotnet","test","-c","Release","--no-build",
                            "--no-restore","--nologo",
                            "--logger","trx;LogFileName="+engine+"_"+name+".trx",
                            "--results-directory",evidence,
                            "--filter","FullyQualifiedName~"+fixtureName+"&Name~"+name]
                        res=p37.run(command,project,timeout=300)
                        trx=evidence/(engine+"_"+name+".trx")
                        p37.check(trx.is_file(),"Missing real NUnit TRX "+str(res))
                        expectedPass=adapted or engine=="selenium"
                        p37.check(p37.read_trx(trx)=={
                            name:"Passed" if expectedPass else "Failed"
                        } and (res["code"]==0)==expectedPass,
                            "Actual NUnit result not source/target parity "+name+"/"+engine+" "+str(res))
                        file=evidence/(engine+"-"+name+".json")
                        p37.check(file.is_file(),"Missing actual NUnit teardown receipt")
                        data=json.loads(file.read_text(encoding="utf-8"))
                        p37.check(data["binaryPath"]==str(chrome),
                            "Real same-Chrome executable drift "+name+"/"+engine)
                        if engine=="selenium":
                            p37.check(data["browserClosed"] and data["snapshot"],
                                "Selenium driver cleanup or snapshot missing")
                        else:
                            p37.check(data["pageClosed"] and data["contextClosed"]
                                and data["injectedSameContext"]
                                and data["browserConnectedBeforeOneTimeCleanup"]
                                and not data["error"] and data["snapshot"],
                                "Playwright Page/Context identity or finally cleanup failed")
                            closure=list(evidence.glob("playwright-one-time-*.json"))
                            p37.check(closure and all(
                                json.loads(f.read_text(encoding="utf-8"))["closed"]
                                and json.loads(f.read_text(encoding="utf-8"))["disposed"]
                                for f in closure),
                                "OneTimeTearDown browser close/Playwright dispose missing")
                            if expectedPass:
                                p37.check(data["exceptionType"] is None,
                                    "Reviewed Count recovery did not pass")
                            else:
                                p37.check(data["exceptionType"]==expectedFailure,
                                    "Unadapted Playwright CLR type drift")
                                pic=evidence/data["screenshot"]
                                p37.check(pic.is_file() and pic.read_bytes()[:8]
                                    ==b"\x89PNG\r\n\x1a\n",
                                    "Actual negative screenshot missing")
                        results[name][engine]=data
                    p37.check(json.loads(results[name]["selenium"]["snapshot"])
                        ==json.loads(results[name]["playwright"]["snapshot"]),
                        "Observed DOM/event/application console differed "+name)
                summary={
                    "schema":"migrator-preview42-lifecycle-source-attested/v1",
                    "sourceTreeSha256":sha,"sourceFilesSha256":hashes,
                    "pageContextBoundSameInstance":True,
                    "perTestCleanupFinallyAll8":True,
                    "oneTimeBrowserDisposedAll4":True,
                    "standaloneTargetNoSelenium":True,
                    "chromeVersion":os.environ.get("MIGRATOR_CHROME_VERSION"),
                    "chromeSha256":hashlib.sha256(chrome.read_bytes()).hexdigest(),
                    "driverVersion":os.environ.get("MIGRATOR_CHROMEDRIVER_VERSION"),
                    "driverSha256":hashlib.sha256(chromedriver.read_bytes()).hexdigest(),
                    "plans":{name:{
                        "sourceMethodSha256":p["sourceMethodSha256"],
                        "fixtureLifecycle":p["fixtureLifecycle"],
                        "asyncMethodsSha256":[m["sourceSha256"] for m in p["asyncMethods"]],
                        "catchSha256":p["catches"][0]["targetCatchSha256"]
                        } for name,p in plans.items()},
                    "results":{name:{engine:{
                        "outcome":results[name][engine]["outcome"],
                        "exceptionType":results[name][engine]["exceptionType"]}
                        for engine in ("selenium","playwright")}
                        for name in TESTS},
                    "negativeControls":["UnadaptedMissing","UnadaptedTimeout",
                        "unknown Page field","unknown Context field"],
                    "fixtureOneTimeSetUpFailureCleanup":"not_verified",
                    "fullProductEquivalence":"not_verified",
                    "canAutoApply":False,"privateArBilling":"not_run"
                }
                (evidence/"summary.json").write_text(
                    json.dumps(summary,sort_keys=True,indent=2)+"\n",encoding="utf-8")
                manifests={p.name:hashlib.sha256(p.read_bytes()).hexdigest()
                    for p in evidence.iterdir()
                    if p.is_file() and p.name!="SHA256SUMS.json"}
                (evidence/"SHA256SUMS.json").write_text(
                    json.dumps(manifests,sort_keys=True,indent=2)+"\n",encoding="utf-8")
                print("POM_PREVIEW42_REAL_NUNIT_FIXTURE_CONTEXT_CLEANUP_PASSED")
                print(json.dumps(summary,sort_keys=True))
        finally:server.shutdown()

if __name__=="__main__":main()
