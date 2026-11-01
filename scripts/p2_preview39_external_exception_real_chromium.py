#!/usr/bin/env python3
"""preview.39: real same-Chrome Selenium vs Playwright *counterexample*
for an external Selenium-specific catch in a TestBase->NUnit call chain.

Static Roslyn planner must BLOCK that full-tree target candidate.
The intentionally unapproved comparison uses the isolated preview.38
review-only inner projection, WITHOUT inventing an outer handler.
It is a diagnostic of changed runtime behavior, not a converted test.
"""
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import threading
from http.server import ThreadingHTTPServer

import p2_preview38_source_exception_real_chromium as p38
import p2_preview37_rendered_text_unicode_real_chromium as p37

SOURCE_BASE = """
using OpenQA.Selenium;
namespace Fictional.P37;
public sealed class SourceTestBase {
    private readonly SourcePage page;
    public SourceTestBase(SourcePage page) { this.page=page; }
    public void RethrowMissing() { page.CatchMissingRethrow(); }
    public void RethrowTimeout() { page.CatchTimeoutRethrow(); }
}
"""

SOURCE_NUNIT_CALLERS = """
using OpenQA.Selenium;
using System;
namespace Fictional.P37;
public sealed class NUnitOuter {
    private readonly SourceTestBase testBase;
    private readonly SourcePage page;
    public NUnitOuter(SourcePage page) {
        this.page=page; testBase=new SourceTestBase(page);
    }
    public void CatchMissing() {
        try { testBase.RethrowMissing(); }
        catch (NoSuchElementException) { }
    }
    public void CatchTimeout() {
        try { testBase.RethrowTimeout(); }
        catch (WebDriverTimeoutException) { }
    }
    public void CatchFiltered() {
        try { testBase.RethrowMissing(); }
        catch (NoSuchElementException error) when (error.Message.Length > 0) { }
    }
    public void CatchBroad() {
        try { testBase.RethrowMissing(); }
        catch (Exception) { }
    }
    public void Irrelevant() {
        try { page.CatchMissingRecovery(); }
        catch (ArgumentException) { }
    }
}
"""

SOURCE_NUNIT_TESTS = """
    [Test] public Task ExternalMissingCatch() => Measured(async ()=> {
        try {
            new SourceTestBase(new SourcePage(driver!)).RethrowMissing();
        } catch (NoSuchElementException) {
            Assert.That(driver!.FindElements(By.Id("never-created")).Count,
                Is.EqualTo(0));
        }
        await Task.CompletedTask;
    });
    [Test] public Task ExternalTimeoutCatch() => Measured(async ()=> {
        try {
            new SourceTestBase(new SourcePage(driver!)).RethrowTimeout();
        } catch (WebDriverTimeoutException) {
            Assert.That(driver!.FindElements(By.Id("loader")).Count,
                Is.EqualTo(1));
        }
        await Task.CompletedTask;
    });
"""
UNAPPROVED_PLAYWRIGHT_COMPARISON = """
    // Intentionally no inferred Selenium catch: the full-tree Roslyn plan
    // is BLOCKED. Running this isolated inner draft proves the mismatch.
    [Test] public Task ExternalMissingCatch() =>
        Measured(async ()=> { await DoCatchMissingRethrow(); });
    [Test] public Task ExternalTimeoutCatch() =>
        Measured(async ()=> { await DoCatchTimeoutRethrow(); });
"""

CASE_NAMES = ("ExternalMissingCatch", "ExternalTimeoutCatch")
EXCEPTION_NAMES = {
    "ExternalMissingCatch": "Microsoft.Playwright.PlaywrightException",
    "ExternalTimeoutCatch": "System.TimeoutException"
}
BLOCKER_CODES = {
    "CatchMissingRethrow": "POM_EXTERNAL_NO_SUCH_ELEMENT_CATCH",
    "CatchTimeoutRethrow": "POM_EXTERNAL_WEBDRIVER_TIMEOUT_CATCH"
}

def cli_json(command, cwd, expected_code):
    result=subprocess.run([str(x) for x in command],cwd=str(cwd),
        capture_output=True,text=True,timeout=240,
        env=dict(os.environ,DOTNET_CLI_TELEMETRY_OPTOUT="1"))
    p37.check(result.returncode==expected_code,
        "Core exact source graph unexpectedly returned code "+
        str(result.returncode)+": "+str(command)+
        " / "+result.stdout[-1200:] + result.stderr[-1700:])
    p37.check(result.stdout.lstrip().startswith("{"),
        "Core CLI did not return a complete JSON object")
    return json.loads(result.stdout)

def make_synthetic_fixture():
    source,fixture,selenium,playwright=p38.synthetic_fixtures()
    fixture=p38.patch(fixture,"private async Task Measured(",
        "protected async Task Measured(","Make source/target observed exception recordable")
    fixture=p38.patch(fixture,
        '"CatchMissingRecovery"=>"missing_recover",',
        '"ExternalMissingCatch"=>"outer_missing",'
        '"ExternalTimeoutCatch"=>"outer_timeout",'
        '"CatchMissingRecovery"=>"missing_recover",',
        "NUnit browser scenario mapping")
    selenium=p38.patch(selenium,
        '    protected override Task Close(){',
        SOURCE_NUNIT_TESTS+'    protected override Task Close(){',
        "real Selenium NUnit outer catch controls")
    playwright=p38.patch(playwright,
        '    protected override async Task Close(){',
        UNAPPROVED_PLAYWRIGHT_COMPARISON+'    protected override async Task Close(){',
        "real Playwright deliberately unapproved outer comparison")
    return source,fixture,selenium,playwright

def main():
    repo=Path(__file__).resolve().parent.parent
    chrome=Path(os.environ["MIGRATOR_CHROME_BIN"])
    driver=Path(os.environ["MIGRATOR_CHROMEDRIVER_BIN"])
    p37.check(chrome.is_file() and driver.is_file(),
              "Same exact Chrome binary and ChromeDriver required")
    cli=repo/"Migrator.AssemblyCli"/"bin"/"Release"/"net10.0"/"Migrator.AssemblyCli.dll"
    p37.check(cli.is_file(),"Build Core CLI first")
    evidence=repo/"artifacts"/"pom-preview39"
    evidence.mkdir(parents=True,exist_ok=True)

    with ThreadingHTTPServer(("127.0.0.1",0),p37.Handler) as server:
        threading.Thread(target=server.serve_forever,daemon=True).start()
        try:
            with tempfile.TemporaryDirectory(prefix="fictional-p39-callgraph-") as td:
                root=Path(td);source,fixture,selenium,playwright=make_synthetic_fixture()
                source_tree=root/"all-source";source_tree.mkdir()
                page_only=root/"isolated-page";page_only.mkdir()
                for folder in (source_tree,page_only):
                    (folder/"Page.cs").write_bytes(source.encode("utf-8"))
                    (folder/"Helpers.cs").write_bytes(p37.HELPERS.encode("utf-8"))
                (source_tree/"TestBase.cs").write_bytes(SOURCE_BASE.encode("utf-8"))
                (source_tree/"NUnitOuter.cs").write_bytes(
                    SOURCE_NUNIT_CALLERS.encode("utf-8"))
                before={p.name:hashlib.sha256(p.read_bytes()).hexdigest()
                        for p in sorted(source_tree.glob("*.cs"))}
                full_hash=cli_json(["dotnet",cli,"pom-inspect",source_tree],repo,0)[
                    "sourceTreeHash"]
                isolated_hash=cli_json(["dotnet",cli,"pom-inspect",page_only],repo,0)[
                    "sourceTreeHash"]
                blocked={}
                isolated={}
                for method,code in BLOCKER_CODES.items():
                    args=["dotnet",cli,"pom-plan",source_tree,
                         "Fictional.P37.SourcePage",method,full_hash,"--emit-target"]
                    no=cli_json(args,repo,3)
                    p37.check(no["status"]=="blocked" and no["targetStatements"]==[]
                        and no["adapterCandidate"] is None and not no["canAutoApply"]
                        and not no["runtimeVerified"]
                        and any(b["code"]==code and b["sourcePath"]=="NUnitOuter.cs"
                                and b["sourceLine"]>0 and
                                len(b["sourceSha256"])==64 for b in no["blockers"]),
                        "External TestBase/NUnit Selenium catch did not BLOCK "+method)
                    p37.check(any(b["code"]=="POM_EXTERNAL_EXCEPTION_FILTER"
                                  for b in no["blockers"]),
                        "External catch filter was not preserved as a blocker "+method
                        if method=="CatchMissingRethrow" else
                        "Missing external blocker") if method=="CatchMissingRethrow" else None
                    blocked[method]=no
                    yes=cli_json(["dotnet",cli,"pom-plan",page_only,
                        "Fictional.P37.SourcePage",method,isolated_hash,
                        "--emit-target"],repo,0)
                    p37.check(yes["status"]=="requires_review"
                        and yes["adapterCandidate"]["requiresReview"]
                        and not yes["runtimeVerified"] and not yes["canAutoApply"]
                        and len(yes["targetStatements"])==1,
                        "Isolated inner draft is not explicitly review-only "+method)
                    isolated[method]=yes
                unrelated=cli_json(["dotnet",cli,"pom-plan",source_tree,
                    "Fictional.P37.SourcePage","CatchMissingRecovery",
                    full_hash,"--emit-target"],repo,0)
                p37.check(unrelated["status"]=="requires_review"
                    and not unrelated["blockers"],
                    "Unrelated external ArgumentException falsely blocked source mapping")
                p37.check({p.name:hashlib.sha256(p.read_bytes()).hexdigest()
                    for p in sorted(source_tree.glob("*.cs"))}==before,
                    "Core source-only graph inspection mutated inputs")
                # The full-tree projected target code must never be emitted.
                # Only run the *isolated* prior preview.38 inner draft as a
                # counterfactual to measure what an unchecked outer catch
                # migration would change. This is not an auto-applied target.
                for token,method in [
                    ("__MISSING_RETHROW__","CatchMissingRethrow"),
                    ("__TIMEOUT_RETHROW__","CatchTimeoutRethrow")
                ]:
                    playwright=p38.patch(playwright,token,
                        isolated[method]["targetStatements"][0],
                        "unapproved isolated inner C# draft")
                # Other inherited preview.37/38 methods are not selected by
                # this test suite and never execute.
                for token in sorted(set(re.findall(r"__[A-Z][A-Z0-9_]*__",playwright))):
                    playwright=playwright.replace(token,
                        'throw new System.NotSupportedException("not selected for p39");')
                p37.check(not re.search(r"__[A-Z][A-Z0-9_]*__",playwright),
                    "Unexpected unresolved C# fixture placeholder")
                project=root/"tests";project.mkdir()
                files={
                    "Synthetic.Tests.csproj":p37.CSPROJ,
                    "SourcePage.cs":source,
                    "Helpers.cs":p37.HELPERS,
                    "TestBase.cs":SOURCE_BASE,
                    "ParityTestsBase.cs":fixture,
                    "SeleniumParityTests.cs":selenium,
                    "PlaywrightParityTests.cs":playwright
                }
                for name,contents in files.items():
                    (project/name).write_bytes(contents.encode("utf-8"))
                result=p37.run(["dotnet","build","-c","Release","--nologo"],
                    project,timeout=360)
                p37.check(result["code"]==0,
                    "Synthetic real Chrome source/target NUnit assembly did not compile: "+
                    str(result))
                os.environ["POM37_URL"]="http://127.0.0.1:"+str(server.server_port)+"/"
                os.environ["POM37_EVIDENCE"]=str(evidence)
                compared={}
                for test in CASE_NAMES:
                    compared[test]={}
                    for engine in ("selenium","playwright"):
                        outcome=p38.run_case(project,evidence,engine,test)
                        expected="Passed" if engine=="selenium" else "Failed"
                        p37.check(outcome["tests"]=={test:expected} and
                            (outcome["exit"]==0)==(engine=="selenium"),
                            "External source catch counterexample missing: "+
                            test+"/"+engine+" "+str(outcome))
                        state=evidence/(engine+"-"+test+".json")
                        p37.check(state.is_file(),"Missing actual browser JSON "+str(state))
                        data=json.loads(state.read_text(encoding="utf-8"))
                        p37.check(data["browserClosed"] and data["error"] is None
                            and data["binaryPath"]==str(chrome),
                            "Browser did not clean up or binary drifted "+str(data))
                        data["snapshot"]=json.loads(data["snapshot"])
                        if engine=="playwright":
                            p37.check(data["exceptionType"]==EXCEPTION_NAMES[test],
                                "Actual target exception identity mismatch "+str(data))
                            image=evidence/data["screenshot"]
                            p37.check(image.is_file() and
                                image.read_bytes()[:8]==b"\x89PNG\r\n\x1a\n",
                                "Real target failure screenshot missing "+str(image))
                        else:
                            p37.check(data["exceptionType"] is None,
                                "Source TestBase/NUnit handler should have recovered")
                        compared[test][engine]=data
                    p37.check(
                        compared[test]["selenium"]["snapshot"]==
                        compared[test]["playwright"]["snapshot"],
                        "Same Chrome browser app DOM and event trace drift "+test)
                summary={
                    "schema":"migrator-preview39-external-catch-counterexample/v1",
                    "source":"real_Selenium_WebDriver_ChromeDriver_NUnit_outer_catch",
                    "target":"real_Playwright_DotNet_inner_draft_without_unproven_outer_adapter",
                    "target_is_unapproved_counterfactual":True,
                    "source_tree_sha256":full_hash,
                    "source_files_sha256":before,
                    "isolated_page_source_tree_sha256":isolated_hash,
                    "source_method_sha256":{
                        name:blocked[name]["sourceMethodSha256"]
                        for name in BLOCKER_CODES},
                    "external_source_blockers":{
                        name:[{"code":b["code"],"path":b["sourcePath"],
                               "line":b["sourceLine"],"sha256":b["sourceSha256"]}
                              for b in blocked[name]["blockers"]
                              if b["code"].startswith("POM_EXTERNAL_")]
                        for name in BLOCKER_CODES},
                    "nunit_results":{
                        test:{engine:{
                            "outcome":compared[test][engine]["outcome"],
                            "exceptionType":compared[test][engine]["exceptionType"]
                        } for engine in ("selenium","playwright")}
                        for test in CASE_NAMES},
                    "same_browser_snapshots":True,
                    "chrome_binary_sha256":hashlib.sha256(chrome.read_bytes()).hexdigest(),
                    "chromedriver_binary_sha256":hashlib.sha256(driver.read_bytes()).hexdigest(),
                    "chrome_version":os.environ.get("MIGRATOR_CHROME_VERSION"),
                    "chromedriver_version":os.environ.get("MIGRATOR_CHROMEDRIVER_VERSION"),
                    "canAutoApply":False,
                    "external_exception_type_equivalence":"not_verified",
                    "no_generated_target_Selenium_namespace":all(
                        "OpenQA.Selenium" not in isolated[x]["targetStatements"][0]
                        for x in BLOCKER_CODES),
                    "limitations":[
                        "No external Selenium catch adapter was emitted or run",
                        "Isolated inner draft is comparison only, not a migration",
                        "Static Roslyn caller graph is bounded to in-scope sources",
                        "External assemblies, virtual dispatch and reflection unverified"
                    ],
                    "private_ArBilling":"not_run"
                }
                (evidence/"summary.json").write_text(
                    json.dumps(summary,sort_keys=True,indent=2)+"\n",encoding="utf-8")
                manifest={p.name:hashlib.sha256(p.read_bytes()).hexdigest()
                          for p in sorted(evidence.iterdir())
                          if p.is_file() and p.name!="SHA256SUMS.json"}
                (evidence/"SHA256SUMS.json").write_text(
                    json.dumps(manifest,sort_keys=True,indent=2)+"\n",encoding="utf-8")
                print("POM_PREVIEW39_REAL_SELENIUM_OUTER_CATCH_MISMATCH_CONFIRMED")
                print(json.dumps(summary,sort_keys=True))
        finally:
            server.shutdown()

if __name__=="__main__":
    main()
