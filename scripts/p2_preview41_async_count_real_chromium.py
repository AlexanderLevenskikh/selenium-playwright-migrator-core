#!/usr/bin/env python3
"""Source-bound preview.41: same-Chrome Selenium vs generated async Count chain."""
import hashlib, json, os, re, tempfile, threading
from pathlib import Path
from http.server import ThreadingHTTPServer
import p2_preview37_rendered_text_unicode_real_chromium as p37
import p2_preview38_source_exception_real_chromium as p38
import p2_preview39_external_exception_real_chromium as p39
import p2_preview40_external_contract_real_chromium as p40

BASE = """
namespace Fictional.P37;
public class SourceTestBase {
    protected readonly SourcePage page;
    public SourceTestBase(SourcePage page){this.page=page;}
    protected void ForwardMissing(){page.CatchMissingRethrow();}
    protected void ForwardTimeout(){page.CatchTimeoutRethrow();}
}
"""
OUTER = """
using NUnit.Framework;
using OpenQA.Selenium;
namespace Fictional.P37;
[TestFixture] public sealed class SourceNUnitOuter:SourceTestBase {
    private readonly IWebDriver driver;
    private readonly Microsoft.Playwright.IPage Page;
    public SourceNUnitOuter(SourcePage source,IWebDriver driver,
            Microsoft.Playwright.IPage page):base(source) {
        this.driver=driver;this.Page=page;
    }
    [Test] public void MissingCount(){
        try {ForwardMissing();}
        catch(NoSuchElementException){
            Assert.That(driver.FindElements(By.Id("never-created")).Count,Is.EqualTo(0));
        }
    }
    [Test] public void TimeoutCount(){
        try {ForwardTimeout();}
        catch(WebDriverTimeoutException){
            Assert.That(driver.FindElements(By.Id("loader")).Count,Is.EqualTo(1));
        }
    }
}
"""
S_TESTS = """
    [Test] public Task ExternalMissingCount()=>Measured(async()=>{
        new SourceNUnitOuter(new SourcePage(driver!),driver!,null!).MissingCount();
        await Task.CompletedTask;
    });
    [Test] public Task ExternalTimeoutCount()=>Measured(async()=>{
        new SourceNUnitOuter(new SourcePage(driver!),driver!,null!).TimeoutCount();
        await Task.CompletedTask;
    });
    [Test,Explicit("missing no-adapter negative")]
    public Task UnadaptedMissingCount()=>Measured(async()=>{
        new SourceNUnitOuter(new SourcePage(driver!),driver!,null!).MissingCount();
        await Task.CompletedTask;
    });
    [Test,Explicit("timeout no-adapter negative")]
    public Task UnadaptedTimeoutCount()=>Measured(async()=>{
        new SourceNUnitOuter(new SourcePage(driver!),driver!,null!).TimeoutCount();
        await Task.CompletedTask;
    });
"""
P_TESTS = """
    private sealed class TargetPage {
        private readonly Microsoft.Playwright.IPage Page;
        public TargetPage(Microsoft.Playwright.IPage page){Page=page;}
        __ROOT_MISSING__
        __ROOT_TIMEOUT__
    }
    private class TargetTestBase {
        protected readonly TargetPage page;
        public TargetTestBase(TargetPage page){this.page=page;}
        __BASE_MISSING__
        __BASE_TIMEOUT__
    }
    private sealed class TargetNUnitOuter:TargetTestBase {
        private readonly Microsoft.Playwright.IPage Page;
        public TargetNUnitOuter(TargetPage page,Microsoft.Playwright.IPage value)
            :base(page){Page=value;}
        __OUTER_MISSING__
        __OUTER_TIMEOUT__
    }
    [Test] public Task ExternalMissingCount()=>Measured(async()=>{
        await new TargetNUnitOuter(new TargetPage(Page),Page).MissingCount();
    });
    [Test] public Task ExternalTimeoutCount()=>Measured(async()=>{
        await new TargetNUnitOuter(new TargetPage(Page),Page).TimeoutCount();
    });
    [Test,Explicit("missing no-adapter negative")]
    public Task UnadaptedMissingCount()=>Measured(async()=>{
        await new TargetPage(Page).CatchMissingRethrow();
    });
    [Test,Explicit("timeout no-adapter negative")]
    public Task UnadaptedTimeoutCount()=>Measured(async()=>{
        await new TargetPage(Page).CatchTimeoutRethrow();
    });
"""
METHODS={
 "CatchMissingRethrow":("ROOT_MISSING","BASE_MISSING","OUTER_MISSING"),
 "CatchTimeoutRethrow":("ROOT_TIMEOUT","BASE_TIMEOUT","OUTER_TIMEOUT")
}
CASES={
 "ExternalMissingCount":(True,None),
 "ExternalTimeoutCount":(True,None),
 "UnadaptedMissingCount":(False,"Microsoft.Playwright.PlaywrightException"),
 "UnadaptedTimeoutCount":(False,"System.TimeoutException")
}
def run(cmd,repo,ok=True):
    return p39.cli_json(cmd,repo,0 if ok else 3)

def main():
    repo=Path(__file__).resolve().parent.parent
    chrome=Path(os.environ["MIGRATOR_CHROME_BIN"])
    driver=Path(os.environ["MIGRATOR_CHROMEDRIVER_BIN"])
    p37.check(chrome.is_file() and driver.is_file(),"Explicit Chrome+Driver are required")
    dll=repo/"Migrator.AssemblyCli/bin/Release/net10.0/Migrator.AssemblyCli.dll"
    p37.check(dll.is_file(),"Build Core CLI first")
    evidence=repo/"artifacts/pom-preview41";evidence.mkdir(parents=True,exist_ok=True)
    with ThreadingHTTPServer(("127.0.0.1",0),p37.Handler) as server:
        threading.Thread(target=server.serve_forever,daemon=True).start()
        try:
            with tempfile.TemporaryDirectory(prefix="fictional-p41-") as tmp:
                work=Path(tmp)
                page,fixture,selenium,target=p40.make_browser_fixture()
                for before,after in (
                    ('"ExternalMissingCatch"=>"outer_missing",',
                     '"ExternalMissingCount"=>"outer_missing",'),
                    ('"ExternalTimeoutCatch"=>"outer_timeout",',
                     '"ExternalTimeoutCount"=>"outer_timeout",'),
                    ('"UnadaptedNoSuch"=>"unadapted_missing",',
                     '"UnadaptedMissingCount"=>"unadapted_missing",'),
                    ('"UnadaptedTimeout"=>"unadapted_timeout",',
                     '"UnadaptedTimeoutCount"=>"unadapted_timeout",')):
                    fixture=p38.patch(fixture,before,after,"scenario name")
                selenium=p38.patch(selenium,p40.SELENIUM_TESTS,S_TESTS,
                    "actual Selenium NUnit external Count")
                target=p38.patch(target,p40.PLAYWRIGHT_TESTS,P_TESTS,
                    "review-only async Playwright Count target")
                source=work/"source";source.mkdir()
                for filename,content in {
                    "Page.cs":page,"Helpers.cs":p37.HELPERS,
                    "TestBase.cs":BASE,"NUnitOuter.cs":OUTER}.items():
                    (source/filename).write_bytes(content.encode("utf-8"))
                baseline={p.name:hashlib.sha256(p.read_bytes()).hexdigest()
                    for p in sorted(source.glob("*.cs"))}
                sha=run(["dotnet",dll,"pom-inspect",source],repo)["sourceTreeHash"]
                proposals={}
                for method,tokens in METHODS.items():
                    old=run(["dotnet",dll,"pom-exception-contract",source,
                        "Fictional.P37.SourcePage",method,sha],repo,False)
                    p37.check(old["status"]=="blocked" and not old["catches"],
                        "Historical preview.40 Count block changed")
                    args=["dotnet",dll,"pom-async-count-contract",source,
                        "Fictional.P37.SourcePage",method,sha,"Page"]
                    hidden=run(args,repo)
                    p37.check(hidden["status"]=="requires_review"
                        and hidden["catches"][0]["targetCatchClause"] is None
                        and all(m["targetBody"] is None and m["targetSignature"] is None
                            for m in hidden["asyncMethods"]),
                        "Default CLI leaked target snippet")
                    plan=run(args+["--emit-target"],repo)
                    edits=plan["asyncMethods"]
                    p37.check(plan["status"]=="requires_review"
                        and plan["verifiedPageReceiver"]=="Page"
                        and len(edits)==3 and not plan["canAutoApply"]
                        and not plan["runtimeVerified"]
                        and [e["sourcePath"] for e in edits]
                           ==["Page.cs","TestBase.cs","NUnitOuter.cs"]
                        and "await Page.Locator(" in edits[2]["targetBody"]
                        and "await Forward" in edits[2]["targetBody"],
                        "Incomplete or unverified async chain "+method+" "+str(plan))
                    proposals[method]=plan
                    for token,edit in zip(tokens,edits):
                        snippet=edit["targetSignature"]+" {\n"+edit["targetBody"]+"\n}"
                        target=p38.patch(target,"__"+token+"__",snippet,
                            "Core-generated method snippet "+token)
                bad=run(["dotnet",dll,"pom-async-count-contract",source,
                    "Fictional.P37.SourcePage","CatchMissingRethrow",sha,
                    "GuessedPage","--emit-target"],repo,False)
                p37.check(bad["status"]=="blocked" and not bad["asyncMethods"]
                    and not bad["catches"],"Unknown target receiver accepted")
                filtered=work/"filtered";filtered.mkdir()
                for p in source.glob("*.cs"):
                    code=p.read_text(encoding="utf-8")
                    if p.name=="NUnitOuter.cs":
                        code=code.replace("catch(NoSuchElementException){",
                            "catch(NoSuchElementException e) when(e.Message.Length>0){",1)
                    (filtered/p.name).write_text(code,encoding="utf-8")
                filteredSha=run(["dotnet",dll,"pom-inspect",filtered],repo)["sourceTreeHash"]
                negative=run(["dotnet",dll,"pom-async-count-contract",filtered,
                    "Fictional.P37.SourcePage","CatchMissingRethrow",
                    filteredSha,"Page","--emit-target"],repo,False)
                p37.check(negative["status"]=="blocked" and not negative["asyncMethods"]
                    and not negative["catches"],"Filtered catch escaped blocker")
                p37.check({p.name:hashlib.sha256(p.read_bytes()).hexdigest()
                    for p in sorted(source.glob("*.cs"))}==baseline,
                    "Planner modified customer source")
                for token in sorted(set(re.findall(r"__[A-Z][A-Z0-9_]*__",target))):
                    target=target.replace(token,
                        'throw new System.NotSupportedException("outside preview41 fixture");')
                p37.check("OpenQA.Selenium" not in target
                    and not re.search(r"__[A-Z][A-Z0-9_]*__",target),
                    "Target has hidden Selenium or unresolved snippet")
                project=work/"tests";project.mkdir()
                for name,data in {
                    "Synthetic.Tests.csproj":p37.CSPROJ,
                    "SourcePage.cs":page,"Helpers.cs":p37.HELPERS,
                    "TestBase.cs":BASE,"NUnitOuter.cs":OUTER,
                    "ParityTestsBase.cs":fixture,
                    "SeleniumParityTests.cs":selenium,
                    "PlaywrightParityTests.cs":target}.items():
                    (project/name).write_bytes(data.encode("utf-8"))
                compiled=p37.run(["dotnet","build","-c","Release","--nologo"],
                    project,timeout=360)
                p37.check(compiled["code"]==0,"Mixed NUnit compile failed "+str(compiled))
                targetOnly=work/"target-only";targetOnly.mkdir()
                csproj=p37.CSPROJ.replace(
                    '<PackageReference Include="Selenium.WebDriver" Version="4.33.0" />',"")
                p37.check(csproj!=p37.CSPROJ and
                    "OpenQA.Selenium" not in target and
                    "OpenQA.Selenium" not in fixture,"Hidden source dependency")
                for name,data in {
                    "Synthetic.Tests.csproj":csproj,
                    "ParityTestsBase.cs":fixture,
                    "PlaywrightParityTests.cs":target}.items():
                    (targetOnly/name).write_bytes(data.encode("utf-8"))
                standalone=p37.run(["dotnet","build","-c","Release","--nologo"],
                    targetOnly,timeout=360)
                p37.check(standalone["code"]==0,
                    "Standalone no-Selenium target failed "+str(standalone))
                deps=targetOnly/"bin/Release/net10.0/Synthetic.Tests.deps.json"
                p37.check(deps.is_file() and "Selenium.WebDriver" not in
                    deps.read_text(encoding="utf-8"),"Runtime Selenium dependency")
                os.environ["POM37_URL"]="http://127.0.0.1:"+str(server.server_port)+"/"
                os.environ["POM37_EVIDENCE"]=str(evidence)
                cases={}
                for name,(adapted,exception) in CASES.items():
                    cases[name]={}
                    for engine in ("selenium","playwright"):
                        result=p38.run_case(project,evidence,engine,name)
                        good=adapted or engine=="selenium"
                        p37.check(result["tests"]=={name:"Passed" if good else "Failed"}
                            and (result["exit"]==0)==good,
                            "Real NUnit result mismatch "+name+"/"+engine+" "+str(result))
                        state=json.loads((evidence/(engine+"-"+name+".json"))
                            .read_text(encoding="utf-8"))
                        p37.check(state["browserClosed"] and state["error"] is None
                            and state["binaryPath"]==str(chrome),
                            "Browser cleanup, snapshot or Chrome drift")
                        state["snapshot"]=json.loads(state["snapshot"])
                        if good: p37.check(state["exceptionType"] is None,
                            "Count recovery failed "+name+"/"+engine)
                        else:
                            p37.check(state["exceptionType"]==exception,
                                "Expected unadapted CLR type not observed")
                            png=evidence/state["screenshot"]
                            p37.check(png.is_file() and
                                png.read_bytes().startswith(b"\x89PNG\r\n\x1a\n"),
                                "Missing failure screenshot")
                        cases[name][engine]=state
                    p37.check(cases[name]["selenium"]["snapshot"]==
                        cases[name]["playwright"]["snapshot"],
                        "Same-Chrome DOM/events/application console differ "+name)
                summary={
                    "schema":"migrator-preview41-async-count/v1",
                    "chromeVersion":os.environ.get("MIGRATOR_CHROME_VERSION"),
                    "chromeSha256":hashlib.sha256(chrome.read_bytes()).hexdigest(),
                    "chromedriverVersion":os.environ.get("MIGRATOR_CHROMEDRIVER_VERSION"),
                    "chromedriverSha256":hashlib.sha256(driver.read_bytes()).hexdigest(),
                    "sourceTreeSha256":sha,"sourceFilesSha256":baseline,
                    "sourceMethodSha256":{k:v["sourceMethodSha256"] for k,v in proposals.items()},
                    "targetCatchSha256":{k:v["catches"][0]["targetCatchSha256"] for k,v in proposals.items()},
                    "asyncMethodSha256":{k:[x["sourceSha256"] for x in v["asyncMethods"]]
                        for k,v in proposals.items()},
                    "cases":{k:{engine:{"outcome":v[engine]["outcome"],
                            "exceptionType":v[engine]["exceptionType"]}
                            for engine in ("selenium","playwright")} for k,v in cases.items()},
                    "expectedNegativeControls":["UnadaptedMissingCount","UnadaptedTimeoutCount"],
                    "blocked":["GuessedPage","filtered-catch"],
                    "targetOnlyNoSelenium":True,"autoApply":False,
                    "outerSeleniumClrIdentityPreserved":False,
                    "privateArBilling":"not_run","fullProductEquivalence":"not_verified"}
                (evidence/"summary.json").write_text(
                    json.dumps(summary,sort_keys=True,indent=2)+"\n",encoding="utf-8")
                manifest={p.name:hashlib.sha256(p.read_bytes()).hexdigest()
                    for p in evidence.iterdir() if p.is_file()
                    and p.name!="SHA256SUMS.json"}
                (evidence/"SHA256SUMS.json").write_text(
                    json.dumps(manifest,sort_keys=True,indent=2)+"\n",encoding="utf-8")
                print("POM_PREVIEW41_SOURCE_BACKED_ASYNC_COUNT_REAL_CHROME_PASSED")
                print(json.dumps(summary,sort_keys=True))
        finally:server.shutdown()
if __name__=="__main__":main()
