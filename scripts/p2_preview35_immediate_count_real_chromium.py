#!/usr/bin/env python3
"""preview.34: real Selenium WebDriver/ChromeDriver vs source-projected
Playwright .NET on one pinned Chrome executable and one loopback HTML app.
The assertion race is an EXPECTED non-equivalence, not a successful migration.
"""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import xml.etree.ElementTree as ET

PAGE = """
using System;
using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using NUnit.Framework;
namespace Fictional.P35;
public sealed partial class SourcePage {
    private readonly IWebDriver driver;
    public SourcePage(IWebDriver driver) { this.driver=driver; }
    public void Choose(string search, string confirmation) { SearchThenChoose(search,confirmation); }
    public void WaitGone() {
        new WebDriverWait(driver,TimeSpan.FromMilliseconds(750))
            .Until(d => d.FindElements(By.Id("loader")).Count == 0);
    }
    public void RetryReady() {
        for(var attempt=0;attempt<2;attempt++) {
            try {
                new WebDriverWait(driver,TimeSpan.FromMilliseconds(750))
                    .Until(d => d.FindElements(By.Id("loader")).Count == 0);
                break;
            } catch(WebDriverTimeoutException) {
                if(attempt==1) throw;
            }
        }
    }
    public void AssertOptionCount() {
        Assert.That(driver.FindElements(By.Id("option")).Count,Is.EqualTo(1));
    }
    public void AssertSelectedText(string expected) {
        Assert.That(driver.FindElement(By.Id("selected")).Text, Is.EqualTo(expected));
    }
    public void AssertStaticText() {
        Assert.That(driver.FindElement(By.Id("message")).Text, Is.EqualTo("waiting"));
    }
    public void AssertChangingText() {
        Assert.That(driver.FindElement(By.Id("message")).Text, Is.EqualTo("ready"));
    }
    public void AssertDelayedText() {
        Assert.That(driver.FindElement(By.Id("delayed-text")).Text, Is.EqualTo("ready"));
    }
    public void AssertHiddenText() {
        Assert.That(driver.FindElement(By.Id("hidden-message")).Text, Is.EqualTo(""));
    }
    public void UnsafeCapturedReceiver() {
        new WebDriverWait(driver,TimeSpan.FromMilliseconds(750))
            .Until(d => driver.FindElements(By.Id("loader")).Count == 0);
    }
    public void UnsafeSideEffect() {
        System.Threading.Thread.Sleep(5);
        driver.FindElement(By.Id("picker")).Click();
    }
}
"""
HELPERS = """
using OpenQA.Selenium;
using NUnit.Framework;
namespace Fictional.P35;
public sealed partial class SourcePage {
    private void SearchThenChoose(string value,string confirm) {
        driver.FindElement(By.Id("picker")).Click();
        driver.FindElement(By.Id("search")).Clear();
        driver.FindElement(By.Id("search")).SendKeys(value);
        if(confirm=="yes") {
            driver.FindElement(By.Id("option")).Click();
            driver.FindElement(By.Id("accept")).Click();
        } else {
            Assert.That(driver.FindElements(By.Id("option")).Count,Is.EqualTo(0));
        }
    }
}
"""
HTML = """<!doctype html><html><meta charset="utf-8"><body>
<button id="picker" onclick="openPicker()">Open</button>
<input id="search" style="display:none" oninput="onInput(this.value)"/>
<button id="option" style="display:none" onclick="choose()">Select</button>
<button id="accept" style="display:none" onclick="confirmSelection()">Confirm</button>
<span id="selected"></span><span id="message">waiting</span><span id="hidden-message" style="display:none">SECRET</span><div id="loader">Loading</div>
<script>
window.appEvents=[];window.appConsole=[];let lastInput="";
function emit(x){window.appEvents.push(x);window.appConsole.push("p35:"+x);console.log("p35:"+x);}
function openPicker(){emit("open");document.querySelector("#search").style.display="inline";if(document.querySelector("#option"))document.querySelector("#option").style.display="inline";document.querySelector("#accept").style.display="inline";}
function onInput(x){if(x && lastInput!==x){lastInput=x;emit("type:"+x);}}
function choose(){emit("select:"+document.querySelector("#search").value);}
function confirmSelection(){const s=document.querySelector("#search").value;document.querySelector("#selected").textContent=s;emit("confirm:"+s);}
window.removeLoader=()=>{const l=document.querySelector("#loader");if(l){l.remove();emit("loader:detached");}};
window.beginRace=()=>{const o=document.querySelector("#option");if(o)o.remove();emit("race:started");setTimeout(()=>{const n=document.createElement("button");n.id="option";n.textContent="Option";document.body.appendChild(n);emit("race:inserted");},450);};
window.beginTextRace=()=>{const e=document.querySelector("#message");e.textContent="pending";emit("text:start");setTimeout(()=>{e.textContent="ready";emit("text:end");},1800);};
window.beginInsertionRace=()=>{emit("insertion:start");setTimeout(()=>{const e=document.createElement("span");e.id="delayed-text";e.textContent="ready";document.body.appendChild(e);emit("insertion:end");},1800);};
window.snapshot=()=>({events:window.appEvents.slice(),applicationConsole:window.appConsole.slice(),selected:document.querySelector("#selected").textContent,search:document.querySelector("#search").value,optionCount:document.querySelectorAll("#option").length,loaderCount:document.querySelectorAll("#loader").length,userAgent:navigator.userAgent});
const scenario=new URLSearchParams(location.search).get("scenario");
if(scenario==="missing")document.querySelector("#option").remove();
if(scenario==="loader_gone")setTimeout(window.removeLoader,90);
if(scenario==="retry_recover")setTimeout(window.removeLoader,1250);
emit("ready");
</script></body></html>"""
CSPROJ = """<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><IsPackable>false</IsPackable><IsTestProject>true</IsTestProject></PropertyGroup>
<ItemGroup>
<PackageReference Include="NUnit" Version="4.3.2" />
<PackageReference Include="NUnit3TestAdapter" Version="5.1.0" />
<PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
<PackageReference Include="Selenium.WebDriver" Version="4.33.0" />
<PackageReference Include="Microsoft.Playwright" Version="1.63.0" />
</ItemGroup></Project>"""
BASE_TESTS = r"""
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
namespace Fictional.P35;
[NonParallelizable] public abstract class ParityTestsBase {
    private string testName="";
    private string? caughtException;
    protected abstract string Engine {get;}
    protected abstract Task Open(string url);
    protected abstract Task Choose(string search,string confirmation);
    protected abstract Task WaitGone();
    protected abstract Task RetryReady();
    protected abstract Task AssertOptionCount();
    protected abstract Task AssertSelectedText(string expected);
    protected abstract Task AssertStaticText();
    protected abstract Task AssertChangingText();
    protected abstract Task AssertDelayedText();
    protected abstract Task AssertHiddenText();
    protected abstract Task StartTextRace();
    protected abstract Task StartInsertionRace();
    protected abstract Task StartRace();
    protected abstract Task<string> Snapshot();
    protected abstract Task Screenshot(string path);
    protected abstract Task Close();
    private static string Evidence => Environment.GetEnvironmentVariable("POM35_EVIDENCE")!;
    [SetUp] public async Task Setup() {
        testName=TestContext.CurrentContext.Test.Name;
        caughtException=null;
        var scenario=testName switch {
            "ComboFound"=>"present","ComboMissing"=>"missing",
            "LoaderGone"=>"loader_gone","RetryRecovery"=>"retry_recover",
            "RetryExhausted"=>"retry_exhausted",
            "AssertionRaceNegative"=>"assertion_race","TextStatic"=>"text_static",
            "TextRaceNegative"=>"text_race","TextInsertionNegative"=>"text_insertion",
            "TextHiddenMismatch"=>"text_hidden",_=>throw new Exception("Unexpected test")
        };
        var url=Environment.GetEnvironmentVariable("POM35_URL")!;
        await Open(url+"?scenario="+scenario);
    }
    [TearDown] public async Task Teardown() {
        var status=TestContext.CurrentContext.Result.Outcome.Status.ToString();
        string? snapshot=null, error=null, picture=null;
        try {snapshot=await Snapshot();} catch(Exception e){error="snapshot:"+e.GetType().FullName;}
        if(status=="Failed") {
            picture=Engine+"-"+testName+"-failure.png";
            try {await Screenshot(Path.Combine(Evidence,picture));}
            catch(Exception e){error="screenshot:"+e.GetType().FullName;}
        }
        bool closed=true;
        try {await Close();} catch {closed=false;}
        var record=new { engine=Engine, name=testName, outcome=status,
            exceptionType=caughtException, snapshot, error, screenshot=picture,
            browserClosed=closed, binaryPath=Environment.GetEnvironmentVariable("MIGRATOR_CHROME_BIN") };
        File.WriteAllText(Path.Combine(Evidence,Engine+"-"+testName+".json"),JsonSerializer.Serialize(record));
        if(!closed || error!=null || snapshot==null)Assert.Fail("Missing browser evidence or cleanup");
    }
    private async Task Measured(Func<Task> action) {
        try {await action();} catch(Exception e){caughtException=e.GetType().FullName;throw;}
    }
    [Test] public Task ComboFound()=>Measured(async ()=>{
        await Choose("alpha","yes");await AssertOptionCount();await AssertSelectedText("alpha");
    });
    [Test] public Task TextStatic()=>Measured(AssertStaticText);
    [Test] public Task ComboMissing()=>Measured(()=>Choose("beta","no"));
    [Test] public Task LoaderGone()=>Measured(WaitGone);
    [Test] public Task RetryRecovery()=>Measured(RetryReady);
    [Test,Explicit("verify actual terminal exception")]
    public Task RetryExhausted()=>Measured(RetryReady);
    // Previous preview.34 counterexample: both now FAIL immediately.
    [Test,Explicit("immediate count assertion under DOM mutation")]
    public Task AssertionRaceNegative()=>Measured(async ()=>{
        await StartRace();await AssertOptionCount();
    });
    [Test,Explicit("changing text assertion must fail immediately")]
    public Task TextRaceNegative()=>Measured(async ()=>{
        await StartTextRace();await AssertChangingText();
    });
    [Test,Explicit("absent text element must not be auto-waited")]
    public Task TextInsertionNegative()=>Measured(async ()=>{
        await StartInsertionRace();await AssertDelayedText();
    });
    [Test,Explicit("known hidden Selenium Text / DOM innerText difference")]
    public Task TextHiddenMismatch()=>Measured(AssertHiddenText);
}
"""
SELENIUM_TESTS = r"""
using System;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
namespace Fictional.P35;
[TestFixture] public sealed class SeleniumParityTests:ParityTestsBase {
    private ChromeDriver? driver;
    protected override string Engine=>"selenium";
    protected override Task Open(string url) {
        string chrome=Environment.GetEnvironmentVariable("MIGRATOR_CHROME_BIN")!;
        string driverPath=Environment.GetEnvironmentVariable("MIGRATOR_CHROMEDRIVER_BIN")!;
        if(!File.Exists(chrome)||!File.Exists(driverPath))throw new FileNotFoundException("Explicit chrome/driver binary missing");
        var options=new ChromeOptions {BinaryLocation=chrome};
        options.AddArgument("--headless=new");options.AddArgument("--no-sandbox");
        options.AddArgument("--disable-dev-shm-usage");options.AddArgument("--window-size=1280,720");
        var service=ChromeDriverService.CreateDefaultService(Path.GetDirectoryName(driverPath)!,Path.GetFileName(driverPath));
        service.HideCommandPromptWindow=true;
        driver=new ChromeDriver(service,options);
        driver.Navigate().GoToUrl(url);
        return Task.CompletedTask;
    }
    protected override Task Choose(string search,string confirmation) {
        new SourcePage(driver!).Choose(search,confirmation);return Task.CompletedTask;
    }
    protected override Task WaitGone(){new SourcePage(driver!).WaitGone();return Task.CompletedTask;}
    protected override Task RetryReady(){new SourcePage(driver!).RetryReady();return Task.CompletedTask;}
    protected override Task AssertOptionCount(){new SourcePage(driver!).AssertOptionCount();return Task.CompletedTask;}
    protected override Task AssertSelectedText(string expected){new SourcePage(driver!).AssertSelectedText(expected);return Task.CompletedTask;}
    protected override Task AssertStaticText(){new SourcePage(driver!).AssertStaticText();return Task.CompletedTask;}
    protected override Task AssertChangingText(){new SourcePage(driver!).AssertChangingText();return Task.CompletedTask;}
    protected override Task AssertDelayedText(){new SourcePage(driver!).AssertDelayedText();return Task.CompletedTask;}
    protected override Task AssertHiddenText(){new SourcePage(driver!).AssertHiddenText();return Task.CompletedTask;}
    protected override Task StartRace(){((IJavaScriptExecutor)driver!).ExecuteScript("window.beginRace()");return Task.CompletedTask;}
    protected override Task StartTextRace(){((IJavaScriptExecutor)driver!).ExecuteScript("window.beginTextRace()");return Task.CompletedTask;}
    protected override Task StartInsertionRace(){((IJavaScriptExecutor)driver!).ExecuteScript("window.beginInsertionRace()");return Task.CompletedTask;
    protected override Task<string> Snapshot()=>Task.FromResult((string)((IJavaScriptExecutor)driver!).ExecuteScript("return JSON.stringify(window.snapshot())")!);
    protected override Task Screenshot(string path){((ITakesScreenshot)driver!).GetScreenshot().SaveAsFile(path);return Task.CompletedTask;}
    protected override Task Close(){try{driver?.Quit();}finally{driver?.Dispose();driver=null;}return Task.CompletedTask;}
}
"""
PLAYWRIGHT_TESTS = r"""
using System;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using Microsoft.Playwright;
namespace Fictional.P35;
[TestFixture] public sealed class PlaywrightParityTests:ParityTestsBase {
    private IPlaywright? pw;private IBrowser? browser;
    private IBrowserContext? context;private IPage? current;
    private IPage Page=>current!;
    protected override string Engine=>"playwright";
    protected override async Task Open(string url) {
        var chrome=Environment.GetEnvironmentVariable("MIGRATOR_CHROME_BIN")!;
        if(!File.Exists(chrome))throw new FileNotFoundException(chrome);
        pw=await Playwright.CreateAsync();
        browser=await pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions{
            ExecutablePath=chrome,Headless=true,
            Args=new[]{"--no-sandbox","--disable-dev-shm-usage"}});
        context=await browser.NewContextAsync(new BrowserNewContextOptions{
            ViewportSize=new ViewportSize{Width=1280,Height=720}});
        current=await context.NewPageAsync();await Page.GotoAsync(url);
    }
    protected override async Task Choose(string search,string confirmation) {
__CHOOSE__
    }
    protected override async Task WaitGone() {
__WAIT__
    }
    protected override async Task RetryReady() {
__RETRY__
    }
    protected override async Task AssertOptionCount() {
__ASSERT__
    }
    protected override async Task AssertSelectedText(string expected) {
__TEXT_SELECTED__
    }
    protected override async Task AssertStaticText() {
__TEXT_STATIC__
    }
    protected override async Task AssertChangingText() {
__TEXT_CHANGE__
    }
    protected override async Task AssertDelayedText() {
__TEXT_DELAYED__
    }
    protected override async Task AssertHiddenText() {
__TEXT_HIDDEN__
    }
    protected override Task StartRace()=>Page.EvaluateAsync("() => window.beginRace()");
    protected override Task StartTextRace()=>Page.EvaluateAsync("() => window.beginTextRace()");
    protected override Task StartInsertionRace()=>Page.EvaluateAsync("() => window.beginInsertionRace()");
    protected override Task<string> Snapshot()=>Page.EvaluateAsync<string>("() => JSON.stringify(window.snapshot())");
    protected override async Task Screenshot(string path){await Page.ScreenshotAsync(new PageScreenshotOptions{Path=path});}
    protected override async Task Close(){
        try{if(context!=null)await context.CloseAsync();if(browser!=null)await browser.CloseAsync();}
        finally{pw?.Dispose();context=null;browser=null;current=null;pw=null;}
    }
}
"""
def check(ok,reason):
    if not ok: raise AssertionError(reason)
def run(cmd,cwd,env=None,timeout=240):
    p=subprocess.run([str(x) for x in cmd],cwd=str(cwd),capture_output=True,text=True,
        env=dict(os.environ,DOTNET_CLI_TELEMETRY_OPTOUT="1",**(env or {})),timeout=timeout)
    return dict(code=p.returncode,stdout=p.stdout[-9000:],stderr=p.stderr[-5000:])
def json_cli(cmd,cwd):
    r=run(cmd,cwd)
    check(r["code"]==0,"Source-backed CLI failed: "+str(r))
    return json.loads(r["stdout"])
def read_trx(path):
    check(path.exists(),"NUnit TRX missing "+str(path))
    cases={}
    for node in ET.parse(path).getroot().iter():
        if node.tag.endswith("UnitTestResult"):
            name=node.attrib["testName"].split(".")[-1]
            check(name not in cases,"Duplicate case "+name)
            cases[name]=node.attrib["outcome"]
    check(cases,"Empty TRX")
    return cases
def test(project,evidence,engine,scenario):
    fixture="SeleniumParityTests" if engine=="selenium" else "PlaywrightParityTests"
    expr="FullyQualifiedName~"+fixture
    selected={"retry":"RetryExhausted","race":"AssertionRaceNegative"}.get(scenario)
    if selected:expr+="&Name~"+selected
    label=engine+"_"+scenario
    argv=["dotnet","test","-c","Release","--no-build","--no-restore",
          "--nologo","--logger","trx;LogFileName="+label+".trx",
          "--results-directory",evidence,"--filter",expr]
    r=run(argv,project,timeout=300)
    p=evidence/(label+".trx")
    check(p.exists(),"NUnit failed to produce TRX: "+str(r))
    return {"code":r["code"],"cases":read_trx(p),"log":r["stdout"][-2500:]}
class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        body=HTML.encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type","text/html; charset=utf-8")
        self.send_header("Content-Length",str(len(body)))
        self.end_headers()
        self.wfile.write(body)
    def log_message(self,*args):pass
def main():
    repo=Path(__file__).resolve().parent.parent
    chrome=Path(os.environ["MIGRATOR_CHROME_BIN"])
    chromedriver=Path(os.environ["MIGRATOR_CHROMEDRIVER_BIN"])
    check(chrome.is_file() and chromedriver.is_file(),
          "Chrome and exact matching ChromeDriver must be provided explicitly")
    evidence=repo/"artifacts"/"pom-preview35";evidence.mkdir(parents=True,exist_ok=True)
    cli=repo/"Migrator.AssemblyCli"/"bin"/"Release"/"net10.0"/"Migrator.AssemblyCli.dll"
    check(cli.is_file(),"Compile assembly CLI first")
    with ThreadingHTTPServer(("127.0.0.1",0),Handler) as server:
        threading.Thread(target=server.serve_forever,daemon=True).start()
        with tempfile.TemporaryDirectory(prefix="fictional-p35-") as d:
            root=Path(d);pom=root/"pom";pom.mkdir()
            (pom/"Page.cs").write_bytes(PAGE.encode("utf-8"))
            (pom/"Helpers.cs").write_bytes(HELPERS.encode("utf-8"))
            inputs={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in pom.glob("*.cs")}
            check(inputs["Page.cs"]==hashlib.sha256(PAGE.encode("utf-8")).hexdigest()
                  and inputs["Helpers.cs"]==hashlib.sha256(HELPERS.encode("utf-8")).hexdigest(),
                  "Source bytes changed by host newline translation")
            hash0=json_cli(["dotnet",cli,"pom-inspect",pom],repo)["sourceTreeHash"]
            plans={}
            for method in ("Choose","WaitGone","RetryReady","AssertOptionCount",
                           "AssertSelectedText","AssertStaticText",
                           "AssertChangingText","AssertDelayedText","AssertHiddenText"):
                p=json_cli(["dotnet",cli,"pom-plan",pom,"Fictional.P35.SourcePage",method,hash0,"--emit-target"],repo)
                check(p["status"]=="requires_review" and not p["runtimeVerified"]
                      and not p["canAutoApply"] and p["adapterCandidate"]["requiresReview"]
                      and len(p["targetStatements"])==1,
                      method+" does not provide a review-only source-backed statement")
                plans[method]=p
            for method in ("UnsafeCapturedReceiver","UnsafeSideEffect"):
                blocked=run(["dotnet",cli,"pom-plan",pom,"SourcePage",method,hash0,"--emit-target"],repo)
                check(blocked["code"]==3 and not json.loads(blocked["stdout"])["targetStatements"],
                      "Unsafe source produced active target "+method)
            redacted=json_cli(["dotnet",cli,"pom-plan",pom,"SourcePage","Choose",hash0],repo)
            check(redacted["targetStatements"]==[] and redacted["adapterCandidate"] is None,
                  "Default Core CLI exposed target code")
            check("WaitForSelectorState.Detached" in plans["RetryReady"]["targetStatements"][0]
                  and "catch (System.TimeoutException)" in plans["RetryReady"]["targetStatements"][0]
                  and "CountAsync(), NUnit.Framework.Is.EqualTo(1)" in plans["AssertOptionCount"]["targetStatements"][0]
                  and all("ToHaveCountAsync" not in t and "ToHaveTextAsync" not in t
                          for p in plans.values() for t in p["targetStatements"])
                  and all("EvaluateAllAsync<string>" in plans[k]["targetStatements"][0]
                          for k in ("AssertSelectedText","AssertStaticText",
                                    "AssertChangingText","AssertDelayedText","AssertHiddenText")),
                  "Source-backed waits, retries or NUnit constraint semantics changed")
            check({p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in pom.glob("*.cs")}==inputs,
                  "Source files mutated during projection")
            project=root/"tests";project.mkdir()
            for name,content in (
                ("Synthetic.Tests.csproj",CSPROJ),("SourcePage.cs",PAGE),
                ("Helpers.cs",HELPERS),("ParityTestsBase.cs",BASE_TESTS),
                ("SeleniumParityTests.cs",SELENIUM_TESTS)):
                (project/name).write_bytes(content.encode("utf-8"))
            generated=PLAYWRIGHT_TESTS
            for token,method in (("__CHOOSE__","Choose"),("__WAIT__","WaitGone"),
                                 ("__RETRY__","RetryReady"),("__ASSERT__","AssertOptionCount"),
                                 ("__TEXT_SELECTED__","AssertSelectedText"),
                                 ("__TEXT_STATIC__","AssertStaticText"),
                                 ("__TEXT_CHANGE__","AssertChangingText"),
                                 ("__TEXT_DELAYED__","AssertDelayedText"),
                                 ("__TEXT_HIDDEN__","AssertHiddenText")):
                generated=generated.replace(token,"        "+plans[method]["targetStatements"][0])
            check("__CHOOSE__" not in generated and "__ASSERT__" not in generated,
                  "Unresolved target template")
            (project/"PlaywrightParityTests.cs").write_bytes(generated.encode("utf-8"))
            build=run(["dotnet","build","-c","Release","--nologo"],project,timeout=360)
            check(build["code"]==0,"Selenium+Playwright compiled assembly failed: "+str(build))
            os.environ["POM35_URL"]="http://127.0.0.1:"+str(server.server_port)+"/"
            os.environ["POM35_EVIDENCE"]=str(evidence)
            results={}
            for engine in ("selenium","playwright"):
                for scenario in ("default","retry","race"):
                    results[engine+"_"+scenario]=test(project,evidence,engine,scenario)
                    should_fail=scenario in ("retry","race")
                    check((results[engine+"_"+scenario]["code"]!=0)==should_fail,
                          "Unexpected real NUnit exit "+engine+"/"+scenario+": "
                          +str(results[engine+"_"+scenario]))
            orig=results["selenium_default"]["cases"]
            target=results["playwright_default"]["cases"]
            check(orig==target,"Real Selenium vs Playwright default TRX differs: "+str(orig)+" / "+str(target))
            # NUnit4 does not necessarily write unselected Explicit
            # cases into the default TRX. Independently selected TRX runs
            # below prove those declarations were discovered and executed.
            check(set(orig)=={"ComboFound","ComboMissing","LoaderGone","RetryRecovery"}
                  and all(x=="Passed" for x in orig.values()),
                  "Unexpected default real NUnit cases: "+str(orig))
            for engine in ("selenium","playwright"):
                check(results[engine+"_retry"]["cases"]=={"RetryExhausted":"Failed"},
                      "Terminal timeout failed to produce selected NUnit failure "+engine)
            check(results["selenium_race"]["cases"]=={"AssertionRaceNegative":"Failed"}
                  and results["playwright_race"]["cases"]=={"AssertionRaceNegative":"Failed"},
                  "Required NUnit immediate-vs-Playwright auto-retry counterexample missing")
            def record(engine,name):
                f=evidence/(engine+"-"+name+".json")
                check(f.exists(),"Missing real browser trace "+str(f))
                r=json.loads(f.read_text(encoding="utf-8"))
                check(r["browserClosed"] and r["error"] is None and r["binaryPath"]==str(chrome),
                      "Browser not closed or wrong binary used: "+str(r))
                r["snapshot"]=json.loads(r["snapshot"])
                return r
            pairs={}
            for name in ("ComboFound","ComboMissing","LoaderGone","RetryRecovery"):
                a,b=record("selenium",name),record("playwright",name)
                check(a["outcome"]==b["outcome"]=="Passed"
                      and a["snapshot"]==b["snapshot"],
                      "Real browser DOM/order/console diverged: "+name+"\n"
                      +str(a)+"\n"+str(b))
                pairs[name]=a["snapshot"]
            found=pairs["ComboFound"];missing=pairs["ComboMissing"]
            check(found["selected"]=="alpha" and found["search"]=="alpha"
                  and found["events"][:2]==["ready","open"]
                  and "type:alpha" in found["events"]
                  and found["events"][-2:]==["select:alpha","confirm:alpha"],
                  "ComboBox open/type/select/confirm ordered observables wrong")
            check(missing["search"]=="beta" and missing["selected"]==""
                  and missing["optionCount"]==0
                  and not any(x.startswith(("select:","confirm:")) for x in missing["events"]),
                  "No-option conditional branch lost")
            for name in ("LoaderGone","RetryRecovery"):
                check(pairs[name]["loaderCount"]==0
                      and pairs[name]["events"].count("loader:detached")==1,
                      "Loader detached state not proved "+name)
            fail_s,fail_p=record("selenium","RetryExhausted"),record("playwright","RetryExhausted")
            check(fail_s["exceptionType"]=="OpenQA.Selenium.WebDriverTimeoutException"
                  and fail_p["exceptionType"]=="System.TimeoutException"
                  and fail_s["snapshot"]==fail_p["snapshot"],
                  "Runtime timeout failure exception/state mismatch "+str(fail_s)+" / "+str(fail_p))
            race_s,race_p=record("selenium","AssertionRaceNegative"),record("playwright","AssertionRaceNegative")
            check(race_s["exceptionType"]=="NUnit.Framework.AssertionException"
                  and race_p["exceptionType"]=="NUnit.Framework.AssertionException",
                  "Immediate count assertion must fail on BOTH drivers, not retry")
            for engine,name in (("selenium","RetryExhausted"),("playwright","RetryExhausted"),
                                ("selenium","AssertionRaceNegative"),("playwright","AssertionRaceNegative")):
                filename=record(engine,name)["screenshot"]
                f=evidence/filename
                check(f.is_file() and f.read_bytes()[:8]==b"\x89PNG\r\n\x1a\n",
                      "Actual failure PNG missing "+str(f))
            summary={
                "schema":"migrator-preview35-immediate-count-parity/v1",
                "source":"real_Selenium_WebDriver_ChromeDriver_CSharp_NUnit",
                "target":"real_Playwright_DotNet_source_backed_review_only",
                "browser":"same_Chrome_for_Testing_executable",
                "chrome_version":os.environ.get("MIGRATOR_CHROME_VERSION"),
                "chromedriver_version":os.environ.get("MIGRATOR_CHROMEDRIVER_VERSION"),
                "chrome_sha256":hashlib.sha256(chrome.read_bytes()).hexdigest(),
                "chromedriver_sha256":hashlib.sha256(chromedriver.read_bytes()).hexdigest(),
                "source_tree_sha256":hash0,"source_sha256":inputs,
                "source_method_sha256":{k:v["sourceMethodSha256"] for k,v in plans.items()},
                "source_backed_effects":{k:[a["effect"] for a in v["actions"]] for k,v in plans.items()},
                "nunit_default_cases":orig,"observed_positive_cases":sorted(pairs),
                "compared":["DOM","ordered_app_events","browser_app_console",
                            "selector_count","loader_state","Chrome_user_agent",
                            "NUnit_TRX","actual_failure_screenshots","cleanup"],
                "timeout_mapping_observed":[fail_s["exceptionType"],fail_p["exceptionType"]],
                "bounded_retry_source_attempts":2,
                "retry_attempt_count_runtime_independently_observed":False,
                "count_assertion_race":{
                    "source":"Failed","target":"Failed",
                    "reason":"NUnit Count and one-shot Playwright CountAsync are both immediate",
                    "timing_equivalence_observed":True,
                    "auto_approval":"requires_review"},
                "source_candidate_auto_apply":False,
                "full_product_equivalence":"not_verified","private_ArBilling":"not_run"}
            (evidence/"summary.json").write_text(json.dumps(summary,indent=2)+"\n",encoding="utf-8")
            manifest={p.name:hashlib.sha256(p.read_bytes()).hexdigest()
                      for p in sorted(evidence.iterdir()) if p.is_file() and p.name!="SHA256SUMS.json"}
            (evidence/"SHA256SUMS.json").write_text(json.dumps(manifest,indent=2)+"\n",encoding="utf-8")
            print("POM_PREVIEW35_IMMEDIATE_COUNT_REAL_BROWSER_PASSED")
            print(json.dumps(summary,sort_keys=True))
        server.shutdown()
if __name__=="__main__":main()
