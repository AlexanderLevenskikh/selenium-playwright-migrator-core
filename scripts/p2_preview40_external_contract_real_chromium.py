#!/usr/bin/env python3
"""preview.40: actual Selenium WebDriver vs Playwright .NET on same Chrome.
Real source NUnitOuter and TestBase are executed. Target uses an isolated
source-attested inner draft plus the exact reviewed Core catch CLAUSE.
Unadapted cases are deliberate failures, not successful migrations.
"""
import hashlib
import json
import os
from pathlib import Path
import re
import tempfile
import threading
from http.server import ThreadingHTTPServer

import p2_preview37_rendered_text_unicode_real_chromium as p37
import p2_preview38_source_exception_real_chromium as p38
import p2_preview39_external_exception_real_chromium as p39

OUTER = """
using OpenQA.Selenium;
namespace Fictional.P37;
public sealed class NUnitOuter {
    private readonly SourceTestBase baseTest;
    public NUnitOuter(SourcePage page) {baseTest=new SourceTestBase(page);}
    public void CatchMissing() {
        try {baseTest.RethrowMissing();}
        catch (NoSuchElementException) {}
    }
    public void CatchTimeout() {
        try {baseTest.RethrowTimeout();}
        catch (WebDriverTimeoutException) {}
    }
}
"""
SELENIUM_TESTS = """
    [Test] public Task ExternalMissingCatch() => Measured(async()=>{
        new NUnitOuter(new SourcePage(driver!)).CatchMissing();
        await Task.CompletedTask;
    });
    [Test] public Task ExternalTimeoutCatch() => Measured(async()=>{
        new NUnitOuter(new SourcePage(driver!)).CatchTimeout();
        await Task.CompletedTask;
    });
    [Test,Explicit("unadapted external catch negative")]
    public Task UnadaptedNoSuch() => Measured(async()=>{
        new NUnitOuter(new SourcePage(driver!)).CatchMissing();
        await Task.CompletedTask;
    });
    [Test,Explicit("unadapted external timeout negative")]
    public Task UnadaptedTimeout() => Measured(async()=>{
        new NUnitOuter(new SourcePage(driver!)).CatchTimeout();
        await Task.CompletedTask;
    });
"""
PLAYWRIGHT_TESTS = """
    [Test] public Task ExternalMissingCatch() => Measured(async()=>{
        try {await DoCatchMissingRethrow();}
__EXTERNAL_MISSING_CATCH__
    });
    [Test] public Task ExternalTimeoutCatch() => Measured(async()=>{
        try {await DoCatchTimeoutRethrow();}
__EXTERNAL_TIMEOUT_CATCH__
    });
    [Test,Explicit("unadapted target must still fail")]
    public Task UnadaptedNoSuch() =>
        Measured(async()=>{await DoCatchMissingRethrow();});
    [Test,Explicit("unadapted target must still fail")]
    public Task UnadaptedTimeout() =>
        Measured(async()=>{await DoCatchTimeoutRethrow();});
"""
METHODS={
    "CatchMissingRethrow":"__EXTERNAL_MISSING_CATCH__",
    "CatchTimeoutRethrow":"__EXTERNAL_TIMEOUT_CATCH__"
}
TESTS={
    "ExternalMissingCatch":(True,None),
    "ExternalTimeoutCatch":(True,None),
    "UnadaptedNoSuch":(False,"Microsoft.Playwright.PlaywrightException"),
    "UnadaptedTimeout":(False,"System.TimeoutException")
}

def run_cli(args, repo, success):
    return p39.cli_json(args,repo,0 if success else 3)

def input_tree(root,page,outer):
    root.mkdir()
    for filename,content in (
        ("Page.cs",page),("Helpers.cs",p37.HELPERS),
        ("TestBase.cs",p39.SOURCE_BASE),("NUnitOuter.cs",outer)):
        (root/filename).write_bytes(content.encode("utf-8"))

def make_browser_fixture():
    source,fixture,selenium,target=p39.make_synthetic_fixture()
    fixture=p38.patch(fixture,
        '"ExternalTimeoutCatch"=>"outer_timeout",',
        '"ExternalTimeoutCatch"=>"outer_timeout",'
        '"UnadaptedNoSuch"=>"unadapted_missing",'
        '"UnadaptedTimeout"=>"unadapted_timeout",',
        "Four NUnit scenario names")
    selenium=p38.patch(selenium,p39.SOURCE_NUNIT_TESTS,
                       SELENIUM_TESTS,"actual Selenium original outer catch test")
    target=p38.patch(target,p39.UNAPPROVED_PLAYWRIGHT_COMPARISON,
                     PLAYWRIGHT_TESTS,"candidate Playwright external contract")
    return source,fixture,selenium,target

def main():
    repo=Path(__file__).resolve().parent.parent
    chrome=Path(os.environ["MIGRATOR_CHROME_BIN"])
    driver=Path(os.environ["MIGRATOR_CHROMEDRIVER_BIN"])
    p37.check(chrome.is_file() and driver.is_file(),
               "Explicit real Chrome and ChromeDriver required")
    cli=repo/"Migrator.AssemblyCli"/"bin"/"Release"/"net10.0"/"Migrator.AssemblyCli.dll"
    p37.check(cli.is_file(),"Compile source-bound Core CLI first")
    evidence=repo/"artifacts"/"pom-preview40"
    evidence.mkdir(parents=True,exist_ok=True)

    with ThreadingHTTPServer(("127.0.0.1",0),p37.Handler) as server:
        threading.Thread(target=server.serve_forever,daemon=True).start()
        try:
            with tempfile.TemporaryDirectory(prefix="fictional-p40-contract-") as folder:
                work=Path(folder)
                page,fixture,selenium,target=make_browser_fixture()
                full=work/"full";alone=work/"isolated"
                input_tree(full,page,OUTER)
                alone.mkdir()
                (alone/"Page.cs").write_bytes(page.encode("utf-8"))
                (alone/"Helpers.cs").write_bytes(p37.HELPERS.encode("utf-8"))
                inputs={p.name:hashlib.sha256(p.read_bytes()).hexdigest()
                        for p in sorted(full.glob("*.cs"))}
                full_hash=run_cli(["dotnet",cli,"pom-inspect",full],repo,True)[
                    "sourceTreeHash"]
                only_hash=run_cli(["dotnet",cli,"pom-inspect",alone],repo,True)[
                    "sourceTreeHash"]
                proposals={}
                inner={}
                for method,placeholder in METHODS.items():
                    # Preview.39 general action plan stays BLOCKED for the
                    # complete multi-file graph. Only the new opt-in
                    # external contract API may propose a catch clause.
                    blocked=run_cli(["dotnet",cli,"pom-plan",full,
                        "Fictional.P37.SourcePage",method,full_hash,
                        "--emit-target"],repo,False)
                    p37.check(blocked["status"]=="blocked"
                        and blocked["targetStatements"]==[]
                        and blocked["adapterCandidate"] is None,
                        "Existing full-graph safety blocker was weakened")
                    raw=run_cli(["dotnet",cli,"pom-exception-contract",full,
                        "Fictional.P37.SourcePage",method,full_hash,
                        "--emit-target"],repo,True)
                    p37.check(raw["status"]=="requires_review"
                        and raw["requiresReview"] and not raw["canAutoApply"]
                        and not raw["runtimeVerified"] and not raw["blockers"]
                        and len(raw["catches"])==1,
                        "No exact external source contract: "+method+" "+str(raw))
                    c=raw["catches"][0]
                    p37.check(c["catchSourcePath"]=="NUnitOuter.cs"
                        and c["catchSourceLine"]>0
                        and len(c["catchSourceSha256"])==64
                        and len(c["targetCatchSha256"])==64
                        and "SourceTestBase" in c["callerChain"]
                        and "NUnitOuter" in c["callerChain"]
                        and "OpenQA.Selenium" not in c["targetCatchClause"]
                        and "throw;" not in c["targetCatchClause"],
                        "External target catch is not precise, safe source evidence")
                    proposals[method]=raw
                    inside=run_cli(["dotnet",cli,"pom-plan",alone,
                        "Fictional.P37.SourcePage",method,only_hash,
                        "--emit-target"],repo,True)
                    p37.check(inside["status"]=="requires_review"
                        and len(inside["targetStatements"])==1
                        and not inside["canAutoApply"]
                        and not inside["runtimeVerified"],
                        "Isolated inner source projection is not review-only")
                    inner[method]=inside

                # Default output is redacted; explicit target emission only
                # within this trusted localhost synthetic test generator.
                redacted=run_cli(["dotnet",cli,"pom-exception-contract",full,
                    "Fictional.P37.SourcePage","CatchMissingRethrow",
                    full_hash],repo,True)
                p37.check(redacted["catches"][0]["targetCatchClause"] is None
                    and "never-created" not in str(redacted),
                    "Default external contract CLI leaked source selector")

                # Negative controls must be blocked and emit no partial
                # target even though the inner Page method is unchanged.
                for key,outer in (
                    ("filter",OUTER.replace(
                        "catch (NoSuchElementException) {}",
                        "catch (NoSuchElementException ex) when(ex.Message.Length > 0) {}",1)),
                    ("broad",OUTER.replace(
                        "catch (NoSuchElementException) {}",
                        "catch (System.Exception) {}",1)),
                    ("rethrow",OUTER.replace(
                        "catch (NoSuchElementException) {}",
                        "catch (NoSuchElementException) {throw;}",1)),
                    ("extra",OUTER.replace(
                        "try {baseTest.RethrowMissing();}",
                        "try {baseTest.RethrowMissing();baseTest.RethrowTimeout();}",1)),
                    ("multiple",OUTER.replace(
                        "    public void CatchTimeout() {",
                        "    public void CatchMissingAgain(){try{baseTest.RethrowMissing();}"
                        "catch(NoSuchElementException){}}\n"
                        "    public void CatchTimeout() {",1))
                ):
                    neg=work/("negative-"+key)
                    input_tree(neg,page,outer)
                    h=run_cli(["dotnet",cli,"pom-inspect",neg],repo,True)[
                        "sourceTreeHash"]
                    result=run_cli(["dotnet",cli,"pom-exception-contract",neg,
                        "Fictional.P37.SourcePage","CatchMissingRethrow",
                        h,"--emit-target"],repo,False)
                    p37.check(result["status"]=="blocked"
                        and not result["catches"]
                        and result["blockers"] and not result["canAutoApply"],
                        "Unverified filter/throw/side-effect/multipath allowed "+key)

                p37.check({p.name:hashlib.sha256(p.read_bytes()).hexdigest()
                    for p in sorted(full.glob("*.cs"))}==inputs,
                    "Read-only contract planning modified source bytes")
                for token,method in (
                    ("__MISSING_RETHROW__","CatchMissingRethrow"),
                    ("__TIMEOUT_RETHROW__","CatchTimeoutRethrow")
                ):
                    target=p38.patch(target,token,inner[method]["targetStatements"][0],
                        "Source-backed isolated Page action "+method)
                for method,token in METHODS.items():
                    target=p38.patch(target,token,
                        proposals[method]["catches"][0]["targetCatchClause"],
                        "Exact Core-reviewed external catch clause "+method)
                for token in sorted(set(re.findall(r"__[A-Z][A-Z0-9_]*__",target))):
                    target=target.replace(token,
                        'throw new System.NotSupportedException("outside p40 fixture");')
                p37.check(not re.search(r"__[A-Z][A-Z0-9_]*__",target)
                    and "OpenQA.Selenium" not in target,
                    "Target C# has unresolved snippet or Selenium dependency")

                project=work/"tests";project.mkdir()
                for filename,content in {
                    "Synthetic.Tests.csproj":p37.CSPROJ,
                    "SourcePage.cs":page,"Helpers.cs":p37.HELPERS,
                    "TestBase.cs":p39.SOURCE_BASE,
                    "NUnitOuter.cs":OUTER,
                    "ParityTestsBase.cs":fixture,
                    "SeleniumParityTests.cs":selenium,
                    "PlaywrightParityTests.cs":target
                }.items():
                    (project/filename).write_bytes(content.encode("utf-8"))
                build=p37.run(["dotnet","build","-c","Release","--nologo"],
                    project,timeout=360)
                p37.check(build["code"]==0,
                    "Selenium+Playwright NUnit real source and target failed to compile: "+
                    str(build))
                os.environ["POM37_URL"]="http://127.0.0.1:"+str(server.server_port)+"/"
                os.environ["POM37_EVIDENCE"]=str(evidence)
                cases={}
                for name,(adapted,unadapted_type) in TESTS.items():
                    cases[name]={}
                    for engine in ("selenium","playwright"):
                        result=p38.run_case(project,evidence,engine,name)
                        pass_expected=adapted or engine=="selenium"
                        p37.check(result["tests"]=={name:
                            "Passed" if pass_expected else "Failed"}
                            and (result["exit"]==0)==pass_expected,
                            "Actual selected NUnit outcome mismatch: "+
                            name+"/"+engine+" "+str(result))
                        snapshot_file=evidence/(engine+"-"+name+".json")
                        p37.check(snapshot_file.is_file(),
                            "Real browser evidence missing "+str(snapshot_file))
                        state=json.loads(snapshot_file.read_text(encoding="utf-8"))
                        p37.check(state["browserClosed"] and state["error"] is None
                            and state["binaryPath"]==str(chrome),
                            "Real Chromium cleanup/binary drift "+name+"/"+engine)
                        state["snapshot"]=json.loads(state["snapshot"])
                        if pass_expected:
                            p37.check(state["exceptionType"] is None,
                                "Expected source/target external catch recovery "+name)
                        else:
                            p37.check(state["exceptionType"]==unadapted_type,
                                "Unadapted Playwright source CLR type mismatch "+name)
                            picture=evidence/state["screenshot"]
                            p37.check(picture.is_file() and
                                picture.read_bytes()[:8]==b"\x89PNG\r\n\x1a\n",
                                "Real negative screenshot absent "+str(picture))
                        cases[name][engine]=state
                    p37.check(cases[name]["selenium"]["snapshot"]==
                        cases[name]["playwright"]["snapshot"],
                        "Same Chrome DOM/event/application-console mismatch "+name)

                summary={
                    "schema":"migrator-preview40-source-backed-external-contract/v1",
                    "source":"real_Selenium_WebDriver_ChromeDriver_NUnit_TestBase",
                    "target":"real_Playwright_DotNet_explicit_reviewed_catch_clause",
                    "chrome_version":os.environ.get("MIGRATOR_CHROME_VERSION"),
                    "chromedriver_version":os.environ.get("MIGRATOR_CHROMEDRIVER_VERSION"),
                    "chrome_sha256":hashlib.sha256(chrome.read_bytes()).hexdigest(),
                    "chromedriver_sha256":hashlib.sha256(driver.read_bytes()).hexdigest(),
                    "sourceTreeSha256":full_hash,
                    "isolatedTreeSha256":only_hash,
                    "sourceFilesSha256":inputs,
                    "sourceMethodSha256":{k:v["sourceMethodSha256"] for k,v in proposals.items()},
                    "sourceCatchSha256":{k:v["catches"][0]["catchSourceSha256"] for k,v in proposals.items()},
                    "targetCatchSha256":{k:v["catches"][0]["targetCatchSha256"] for k,v in proposals.items()},
                    "expectedAdaptedCases":["ExternalMissingCatch","ExternalTimeoutCatch"],
                    "expectedUnadaptedNegativeCases":["UnadaptedNoSuch","UnadaptedTimeout"],
                    "unverifiedContractBlockers":["filter","broad","rethrow","extra","multiple"],
                    "cases":{name:{engine:{
                        "outcome":cases[name][engine]["outcome"],
                        "exceptionType":cases[name][engine]["exceptionType"]}
                        for engine in ("selenium","playwright")}
                        for name in TESTS},
                    "runtimeVerifiedByMCP":False,
                    "autoApply":False,
                    "outerExceptionClrIdentityPreserved":False,
                    "fullProductEquivalence":"not_verified",
                    "privateArBilling":"not_run"
                }
                (evidence/"summary.json").write_text(
                    json.dumps(summary,sort_keys=True,indent=2)+"\n",encoding="utf-8")
                manifest={x.name:hashlib.sha256(x.read_bytes()).hexdigest()
                    for x in sorted(evidence.iterdir())
                    if x.is_file() and x.name!="SHA256SUMS.json"}
                (evidence/"SHA256SUMS.json").write_text(
                    json.dumps(manifest,sort_keys=True,indent=2)+"\n",encoding="utf-8")
                print("POM_PREVIEW40_SOURCE_BACKED_EXTERNAL_CONTRACT_REAL_BROWSER_PASSED")
                print(json.dumps(summary,sort_keys=True))
        finally:
            server.shutdown()
if __name__=="__main__":
    main()
