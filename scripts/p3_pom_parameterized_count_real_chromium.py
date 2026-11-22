#!/usr/bin/env python3
"""P3: real source Selenium versus generated review-only POM guards in Chromium."""
import hashlib
import json
import os
from pathlib import Path
import tempfile
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import p2_preview34_real_selenium_vs_playwright as common

SOURCE = """
using OpenQA.Selenium;
namespace Fictional.P3;
public sealed class CountPage {
  private readonly IWebDriver driver;
  public CountPage(IWebDriver d) { driver=d; }
  public void Choose(int minimum,int maximum) {
    if (driver.FindElements(By.CssSelector(".row")).Count >= minimum
        && maximum > driver.FindElements(By.CssSelector(".row")).Count) Accept();
    else Reject();
  }
  public void Reverse(int threshold) {
    if (threshold < driver.FindElements(By.CssSelector(".row")).Count) Accept();
    else Reject();
  }
  public void Confirm(string decision) {
    if ("yes" == decision) Accept(); else Reject();
  }
  private void Accept() { driver.FindElement(By.Id("accept")).Click(); }
  private void Reject() { driver.FindElement(By.Id("reject")).Click(); }
}
"""
TARGET = """
using System.Threading.Tasks;
using Microsoft.Playwright;
namespace Fictional.P3;
public sealed class TargetPage {
  private readonly IPage Page;
  public TargetPage(IPage p) { Page=p; }
  public async Task Choose(int minimum,int maximum) { __CHOOSE__ }
  public async Task Reverse(int threshold) { __REVERSE__ }
  public async Task Confirm(string decision) { __CONFIRM__ }
  public async Task Mutant(int minimum,int maximum) { __MUTANT__ }
}
"""
APP = """<!doctype html><html><body><div id="rows"></div>
<button id="accept" onclick="window.events.push('accept')">A</button>
<button id="reject" onclick="window.events.push('reject')">R</button>
<script>
window.events=[];
let count=Number(new URLSearchParams(location.search).get('rows')||0);
for(let i=0;i<count;i++) {
 let n=document.createElement('span');n.className='row';
 document.getElementById('rows').appendChild(n);
}
window.snapshot=()=>({events:window.events.slice(),
 count:document.querySelectorAll('.row').length,userAgent:navigator.userAgent});
</script></body></html>"""
ATTRS = """
[TestCase("choose",1,3,2,"accept",TestName="Inside")]
[TestCase("choose",3,4,2,"reject",TestName="BelowMinimum")]
[TestCase("choose",1,2,2,"reject",TestName="AtMaximum")]
[TestCase("choose",0,1,0,"accept",TestName="EmptyWithin")]
[TestCase("reverse",1,0,2,"accept",TestName="ReverseInside")]
[TestCase("reverse",2,0,2,"reject",TestName="ReverseEqual")]
[TestCase("confirm",0,0,2,"accept",TestName="ConfirmYes")]
[TestCase("confirm",0,0,2,"reject",TestName="ConfirmNo")]
"""
COMMON = """
using System;
using System.IO;
using System.Text.Json;
using NUnit.Framework;
namespace Fictional.P3;
public abstract class BrowserEvidence {
  protected static string Url(int rows) => Environment.GetEnvironmentVariable("P3_URL")!
      +"?rows="+rows.ToString(System.Globalization.CultureInfo.InvariantCulture);
  protected static string Dir => Environment.GetEnvironmentVariable("P3_EVIDENCE")!;
  protected static void Record(string engine,string snapshot) =>
    File.WriteAllText(Path.Combine(Dir,engine+"-"+
      TestContext.CurrentContext.Test.Name+".json"),snapshot);
  protected static void CheckEvent(string snapshot,string expected) {
    using var doc=JsonDocument.Parse(snapshot);
    var arr=doc.RootElement.GetProperty("events").EnumerateArray();
    Assert.That(arr.Select(x=>x.GetString()).ToArray(),Is.EqualTo(new[]{expected}));
  }
}
"""
SELENIUM = """
using System;
using System.IO;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using NUnit.Framework;
namespace Fictional.P3;
[TestFixture,NonParallelizable] public sealed class SeleniumParityTests:BrowserEvidence {
  private static ChromeDriver Start(int rows) {
    var executable=Environment.GetEnvironmentVariable("MIGRATOR_CHROMEDRIVER_BIN")!;
    var service=ChromeDriverService.CreateDefaultService(
      Path.GetDirectoryName(executable)!,Path.GetFileName(executable));
    service.HideCommandPromptWindow=true;
    var opts=new ChromeOptions {BinaryLocation=
      Environment.GetEnvironmentVariable("MIGRATOR_CHROME_BIN")!};
    opts.AddArgument("--headless=new");opts.AddArgument("--no-sandbox");
    opts.AddArgument("--disable-dev-shm-usage");
    var d=new ChromeDriver(service,opts);d.Navigate().GoToUrl(Url(rows));return d;
  }
  __ATTRS__
  public void Case(string mode,int min,int max,int rows,string expected) {
    using var d=Start(rows);
    try {
      var page=new CountPage(d);
      if(mode=="choose") page.Choose(min,max);
      else if(mode=="reverse") page.Reverse(min);
      else if(mode=="confirm") page.Confirm(expected=="accept"?"yes":"no");
      else throw new InvalidOperationException("Unknown case");
      var snapshot=(string)((IJavaScriptExecutor)d)
          .ExecuteScript("return JSON.stringify(window.snapshot())")!;
      Record("selenium",snapshot);CheckEvent(snapshot,expected);
    } finally { d.Quit(); }
  }
}
""".replace("__ATTRS__", ATTRS)
PLAYWRIGHT = """
using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Playwright;
using NUnit.Framework;
namespace Fictional.P3;
[TestFixture,NonParallelizable] public sealed class PlaywrightParityTests:BrowserEvidence {
  private static async Task<(IPlaywright,IBrowser,IPage)> Start(int rows) {
    var pw=await Microsoft.Playwright.Playwright.CreateAsync();
    try {
      var b=await pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions {
        ExecutablePath=Environment.GetEnvironmentVariable("MIGRATOR_CHROME_BIN")!,
        Headless=true,Args=new[]{"--no-sandbox","--disable-dev-shm-usage"}});
      var page=await b.NewPageAsync();await page.GotoAsync(Url(rows));
      return(pw,b,page);
    } catch {pw.Dispose();throw;}
  }
  __ATTRS__
  public async Task Case(string mode,int min,int max,int rows,string expected) {
    var(pw,b,page)=await Start(rows);
    try {
      var pom=new TargetPage(page);
      if(mode=="choose") await pom.Choose(min,max);
      else if(mode=="reverse") await pom.Reverse(min);
      else if(mode=="confirm") await pom.Confirm(expected=="accept"?"yes":"no");
      else throw new InvalidOperationException("Unknown case");
      var snapshot=await page.EvaluateAsync<string>(
        "() => JSON.stringify(window.snapshot())");
      Record("playwright",snapshot);CheckEvent(snapshot,expected);
    } finally {await b.CloseAsync();pw.Dispose();}
  }
  [Test,Explicit("negative changed guard must fail")]
  public async Task MutationMustFail() {
    var(pw,b,page)=await Start(2);
    try {
      await new TargetPage(page).Mutant(1,3);
      var snapshot=await page.EvaluateAsync<string>(
        "() => JSON.stringify(window.snapshot())");
      Record("mutant",snapshot);
      await page.ScreenshotAsync(new PageScreenshotOptions {
        Path=Path.Combine(Dir,"mutant-guard.png")});
      CheckEvent(snapshot,"accept");
    } finally {await b.CloseAsync();pw.Dispose();}
  }
}
""".replace("__ATTRS__", ATTRS)
CASES = {"Inside":"accept","BelowMinimum":"reject","AtMaximum":"reject",
         "EmptyWithin":"accept","ReverseInside":"accept","ReverseEqual":"reject",
         "ConfirmYes":"accept","ConfirmNo":"reject"}

class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        content=APP.encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type","text/html; charset=utf-8")
        self.send_header("Content-Length",str(len(content)))
        self.end_headers()
        self.wfile.write(content)
    def log_message(self,*args):
        pass

def main():
    repo=Path(__file__).resolve().parent.parent
    chrome=Path(os.environ["MIGRATOR_CHROME_BIN"])
    driver=Path(os.environ["MIGRATOR_CHROMEDRIVER_BIN"])
    common.check(chrome.is_file() and driver.is_file(),"Exact Chrome/Driver missing")
    cli=repo/"Migrator.AssemblyCli/bin/Release/net10.0/Migrator.AssemblyCli.dll"
    common.check(cli.is_file(),"Build Core assembly CLI first")
    evidence=repo/"artifacts/p3-parameterized-count"
    evidence.mkdir(parents=True,exist_ok=True)
    with ThreadingHTTPServer(("127.0.0.1",0),Handler) as server:
        threading.Thread(target=server.serve_forever,daemon=True).start()
        try:
            with tempfile.TemporaryDirectory(prefix="p3-real-pom-count-") as work:
                root=Path(work);source=root/"pom";source.mkdir()
                source_file=source/"CountPage.cs"
                source_file.write_bytes(SOURCE.encode("utf-8"))
                original_hash=hashlib.sha256(source_file.read_bytes()).hexdigest()
                tree=common.json_cli(["dotnet",cli,"pom-inspect",source],repo)["sourceTreeHash"]
                plans={}
                for method in ("Choose","Reverse","Confirm"):
                    p=common.json_cli(["dotnet",cli,"pom-plan",source,
                        "Fictional.P3.CountPage",method,tree,"--emit-target"],repo)
                    common.check(p["status"]=="requires_review"
                        and p["blockers"]==[]
                        and len(p["targetStatements"])==1
                        and p["adapterCandidate"]["requiresReview"]
                        and not p["runtimeVerified"] and not p["canAutoApply"],
                        "Unsafe or missing POM projection: "+method)
                    plans[method]=p
                choose=plans["Choose"]["targetStatements"][0]
                common.check(choose.count("CountAsync()")==2
                    and " >= minimum" in choose
                    and "maximum > (await " in choose and "else" in choose,
                    "POM Count guard lost parameter/operand order")
                common.check("threshold < (await " in plans["Reverse"]["targetStatements"][0]
                    and '"yes" == decision' in plans["Confirm"]["targetStatements"][0],
                    "Reversed Count or string guard changed")
                mutant=choose.replace(" >= minimum"," < minimum",1)
                common.check(mutant!=choose,"Intentional negative mutation absent")
                common.check(original_hash==hashlib.sha256(source_file.read_bytes()).hexdigest(),
                    "POM input changed")
                rendered=TARGET
                for placeholder,action in (
                    ("__CHOOSE__",choose),
                    ("__REVERSE__",plans["Reverse"]["targetStatements"][0]),
                    ("__CONFIRM__",plans["Confirm"]["targetStatements"][0]),
                    ("__MUTANT__",mutant)):
                    common.check(rendered.count(placeholder)==1,
                        "Unexpected target placeholder "+placeholder)
                    rendered=rendered.replace(placeholder,action)
                tests=root/"tests";tests.mkdir()
                for name,data in (
                    ("Synthetic.Tests.csproj",common.CSPROJ),
                    ("CountPage.cs",SOURCE),("TargetPage.cs",rendered),
                    ("BrowserEvidence.cs",COMMON),
                    ("SeleniumParityTests.cs",SELENIUM),
                    ("PlaywrightParityTests.cs",PLAYWRIGHT)):
                    (tests/name).write_bytes(data.encode("utf-8"))
                build=common.run(["dotnet","build","-c","Release","--nologo"],
                    tests,timeout=360)
                common.check(build["code"]==0,
                    "Real NUnit Selenium/Playwright compilation failed "+str(build))
                os.environ["P3_URL"]="http://127.0.0.1:"+str(server.server_port)+"/"
                os.environ["P3_EVIDENCE"]=str(evidence)
                results={}
                for fixture,engine in (
                    ("SeleniumParityTests","selenium"),
                    ("PlaywrightParityTests","playwright")):
                    output=common.run(["dotnet","test","-c","Release","--no-build",
                        "--no-restore","--nologo",
                        "--logger","trx;LogFileName="+engine+".trx",
                        "--results-directory",evidence,
                        "--filter","FullyQualifiedName~"+fixture],
                        tests,timeout=360)
                    trx=common.read_trx(evidence/(engine+".trx"))
                    common.check(output["code"]==0
                        and trx=={n:"Passed" for n in CASES},
                        engine+" unexpected default NUnit TRX "+str(trx)+" "+str(output))
                    results[engine]=trx
                pairs={}
                for case,expected in CASES.items():
                    a=json.loads((evidence/("selenium-"+case+".json")).read_text())
                    b=json.loads((evidence/("playwright-"+case+".json")).read_text())
                    common.check(a==b and a["events"]==[expected],
                        "Independent DOM/ordered event mismatch "+case+" "+str((a,b)))
                    pairs[case]=a
                failed=common.run(["dotnet","test","-c","Release","--no-build",
                    "--no-restore","--nologo",
                    "--logger","trx;LogFileName=mutant.trx",
                    "--results-directory",evidence,
                    "--filter","Name~MutationMustFail"],tests,timeout=360)
                mutant_trx=common.read_trx(evidence/"mutant.trx")
                common.check(failed["code"]!=0
                    and mutant_trx=={"MutationMustFail":"Failed"},
                    "Inverted Count condition was not rejected by real NUnit")
                m=json.loads((evidence/"mutant-MutationMustFail.json").read_text())
                common.check(m["events"]==["reject"] and pairs["Inside"]["events"]==["accept"],
                    "Mutant did not change the side effect")
                png=evidence/"mutant-guard.png"
                common.check(png.read_bytes()[:8]==b"\x89PNG\r\n\x1a\n",
                    "Actual negative Chromium PNG not produced")
                summary={
                    "schema":"migrator-p3-real-pom-count-guards/v1",
                    "source":"real Selenium C# NUnit on localhost fictional DOM",
                    "target":"generated review-only Playwright .NET",
                    "sourceTreeSha256":tree,"sourceSha256":original_hash,
                    "methodSha256":{k:v["sourceMethodSha256"] for k,v in plans.items()},
                    "chromeVersion":os.environ.get("MIGRATOR_CHROME_VERSION"),
                    "chromeSha256":hashlib.sha256(chrome.read_bytes()).hexdigest(),
                    "chromedriverSha256":hashlib.sha256(driver.read_bytes()).hexdigest(),
                    "positiveCases":sorted(CASES),
                    "compared":["NUnit_TRX","DOM","ordered_events","Chrome_user_agent"],
                    "negativeMutation":{"outcome":"Failed","before":"accept",
                        "after":"reject","pngSha256":hashlib.sha256(png.read_bytes()).hexdigest()},
                    "autoApply":False,"requiresReview":True,
                    "generalBehavioralEquivalence":"not_proven",
                    "privateProjects":"not_run"}
                (evidence/"summary.json").write_text(
                    json.dumps(summary,indent=2,sort_keys=True)+"\n",encoding="utf-8")
                sums={p.name:hashlib.sha256(p.read_bytes()).hexdigest()
                    for p in sorted(evidence.iterdir()) if p.is_file()
                    and p.name!="SHA256SUMS.json"}
                (evidence/"SHA256SUMS.json").write_text(
                    json.dumps(sums,indent=2,sort_keys=True)+"\n",encoding="utf-8")
                print("P3_REAL_SELENIUM_PLAYWRIGHT_PARAMETERIZED_COUNT_PASSED")
                print(json.dumps(summary,sort_keys=True))
        finally:
            server.shutdown()

if __name__=="__main__":
    main()
