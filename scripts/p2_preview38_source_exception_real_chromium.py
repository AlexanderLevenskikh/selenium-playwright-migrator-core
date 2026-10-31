#!/usr/bin/env python3
"""Preview.38: real Selenium WebDriver/ChromeDriver and Playwright .NET on
one Chrome executable and one localhost public synthetic application.
All source-backed candidates remain requires_review (never auto-apply).
"""
import hashlib
import json
import os
import subprocess
import re
from pathlib import Path
import tempfile
import threading
from http.server import ThreadingHTTPServer

import p2_preview37_rendered_text_unicode_real_chromium as base

SOURCE_METHODS = """
    public void CatchMissingRecovery() {
        try {
            Assert.That(driver.FindElement(By.Id("never-created")).Text,
                Is.EqualTo("ready"));
        } catch (NoSuchElementException) {
            Assert.That(driver.FindElements(By.Id("never-created")).Count,
                Is.EqualTo(0));
        }
    }
    public void CatchMissingUnusedVariable() {
        try {
            Assert.That(driver.FindElement(By.Id("never-created")).Text,
                Is.EqualTo("ready"));
        } catch (NoSuchElementException ignored) {
            Assert.That(driver.FindElements(By.Id("never-created")).Count,
                Is.EqualTo(0));
        }
    }
    public void CatchTimeoutRecovery() {
        try {
            new WebDriverWait(driver, TimeSpan.FromMilliseconds(80))
                .Until(d => d.FindElements(By.Id("loader")).Count == 0);
        } catch (WebDriverTimeoutException) {
            Assert.That(driver.FindElements(By.Id("loader")).Count,
                Is.EqualTo(1));
        }
    }
    public void CatchMissingRethrow() {
        try {
            Assert.That(driver.FindElement(By.Id("never-created")).Text,
                Is.EqualTo("ready"));
        } catch (NoSuchElementException) { throw; }
    }
    public void CatchTimeoutRethrow() {
        try {
            new WebDriverWait(driver, TimeSpan.FromMilliseconds(80))
                .Until(d => d.FindElements(By.Id("loader")).Count == 0);
        } catch (WebDriverTimeoutException) { throw; }
    }
    public void CatchMissingDoesNotCatchAssertion() {
        try {
            Assert.That(driver.FindElement(By.Id("message")).Text,
                Is.EqualTo("unexpected"));
        } catch (NoSuchElementException) {
            Assert.That(driver.FindElements(By.Id("message")).Count,
                Is.EqualTo(1));
        }
    }
    public void UnsafeClickMissingCatch() {
        try { driver.FindElement(By.Id("never-created")).Click(); }
        catch (NoSuchElementException) { throw; }
    }
    public void UnsafeCatchFilter() {
        try {
            Assert.That(driver.FindElement(By.Id("never-created")).Text,
                Is.EqualTo("ready"));
        } catch (NoSuchElementException ex) when (ex.Message.Length > 0) {
            throw;
        }
    }
    public void UnsafeBroadCatch() {
        try {
            Assert.That(driver.FindElement(By.Id("never-created")).Text,
                Is.EqualTo("ready"));
        } catch (Exception) { throw; }
    }
    public void UnsafeCatchVariableUsed() {
        try {
            Assert.That(driver.FindElement(By.Id("never-created")).Text,
                Is.EqualTo("ready"));
        } catch (NoSuchElementException ex) {
            Assert.That(ex.Message, Is.EqualTo("anything"));
        }
    }
    public void UnsafeCatchSideEffect() {
        try {
            Assert.That(driver.FindElement(By.Id("never-created")).Text,
                Is.EqualTo("ready"));
        } catch (NoSuchElementException) {
            driver.FindElement(By.Id("other")).Click();
        }
    }
"""
ABSTRACT = """
    protected abstract Task DoCatchMissingRecovery();
    protected abstract Task DoCatchMissingUnusedVariable();
    protected abstract Task DoCatchTimeoutRecovery();
    protected abstract Task DoCatchMissingRethrow();
    protected abstract Task DoCatchTimeoutRethrow();
    protected abstract Task DoCatchMissingDoesNotCatchAssertion();
"""
TESTS = """
    [Test] public Task CatchMissingRecovery() =>
        Measured(DoCatchMissingRecovery);
    [Test] public Task CatchMissingUnusedVariable() =>
        Measured(DoCatchMissingUnusedVariable);
    [Test] public Task CatchTimeoutRecovery() =>
        Measured(DoCatchTimeoutRecovery);
    [Test, Explicit("Source NoSuchElement and target exception CLR types differ")]
    public Task CatchMissingRethrow() => Measured(DoCatchMissingRethrow);
    [Test, Explicit("Source WebDriverTimeout and target exception CLR types differ")]
    public Task CatchTimeoutRethrow() => Measured(DoCatchTimeoutRethrow);
    [Test, Explicit("Do not swallow unrelated NUnit assertion errors")]
    public Task CatchMissingDoesNotCatchAssertion() =>
        Measured(DoCatchMissingDoesNotCatchAssertion);
"""
SELENIUM_OVERRIDES = """
    protected override Task DoCatchMissingRecovery() {
        new SourcePage(driver!).CatchMissingRecovery(); return Task.CompletedTask;
    }
    protected override Task DoCatchMissingUnusedVariable() {
        new SourcePage(driver!).CatchMissingUnusedVariable(); return Task.CompletedTask;
    }
    protected override Task DoCatchTimeoutRecovery() {
        new SourcePage(driver!).CatchTimeoutRecovery(); return Task.CompletedTask;
    }
    protected override Task DoCatchMissingRethrow() {
        new SourcePage(driver!).CatchMissingRethrow(); return Task.CompletedTask;
    }
    protected override Task DoCatchTimeoutRethrow() {
        new SourcePage(driver!).CatchTimeoutRethrow(); return Task.CompletedTask;
    }
    protected override Task DoCatchMissingDoesNotCatchAssertion() {
        new SourcePage(driver!).CatchMissingDoesNotCatchAssertion();
        return Task.CompletedTask;
    }
"""
PLAYWRIGHT_OVERRIDES = """
    protected override async Task DoCatchMissingRecovery() {
__MISSING_RECOVERY__
    }
    protected override async Task DoCatchMissingUnusedVariable() {
__MISSING_UNUSED__
    }
    protected override async Task DoCatchTimeoutRecovery() {
__TIMEOUT_RECOVERY__
    }
    protected override async Task DoCatchMissingRethrow() {
__MISSING_RETHROW__
    }
    protected override async Task DoCatchTimeoutRethrow() {
__TIMEOUT_RETHROW__
    }
    protected override async Task DoCatchMissingDoesNotCatchAssertion() {
__ASSERT_NOT_SWALLOWED__
    }
"""
CASES = [
    ("CatchMissingRecovery", True),
    ("CatchMissingUnusedVariable", True),
    ("CatchTimeoutRecovery", True),
    ("CatchMissingRethrow", False),
    ("CatchTimeoutRethrow", False),
    ("CatchMissingDoesNotCatchAssertion", False)
]
PROJECTIONS = {
    "__MISSING_RECOVERY__": "CatchMissingRecovery",
    "__MISSING_UNUSED__": "CatchMissingUnusedVariable",
    "__TIMEOUT_RECOVERY__": "CatchTimeoutRecovery",
    "__MISSING_RETHROW__": "CatchMissingRethrow",
    "__TIMEOUT_RETHROW__": "CatchTimeoutRethrow",
    "__ASSERT_NOT_SWALLOWED__": "CatchMissingDoesNotCatchAssertion"
}
EXCEPTIONS = {
    "CatchMissingRethrow": {
        "selenium": "OpenQA.Selenium.NoSuchElementException",
        "playwright": "Microsoft.Playwright.PlaywrightException"
    },
    "CatchTimeoutRethrow": {
        "selenium": "OpenQA.Selenium.WebDriverTimeoutException",
        "playwright": "System.TimeoutException"
    },
    "CatchMissingDoesNotCatchAssertion": {
        "selenium": "NUnit.Framework.AssertionException",
        "playwright": "NUnit.Framework.AssertionException"
    }
}

def patch(content, old, replacement, label):
    base.check(content.count(old)==1, "Ambiguous or missing source fixture marker "+label)
    return content.replace(old,replacement)

def json_cli(command, cwd):
    # The established preview.37 subprocess helper keeps only the final
    # 9 KB of stdout. Large source-backed exception plans on Windows
    # include longer path/SHA annotations and exceed that threshold.
    # Read the complete JSON; never treat a truncated tail as a plan.
    result=subprocess.run([str(item) for item in command],cwd=str(cwd),
        capture_output=True,text=True,timeout=240,
        env=dict(os.environ,DOTNET_CLI_TELEMETRY_OPTOUT="1"))
    base.check(result.returncode==0 and result.stdout.lstrip().startswith("{"),
        "Core CLI failed or omitted complete source JSON: "+
        str(command)+" / "+result.stdout[-1500:]+result.stderr[-2000:])
    return json.loads(result.stdout)

def synthetic_fixtures():
    source=patch(base.PAGE,"    public void UnsafeCapturedReceiver() {",
                 SOURCE_METHODS+"    public void UnsafeCapturedReceiver() {","source methods")
    fixture=patch(base.BASE_TESTS, "    protected abstract Task StartRace();",
                  ABSTRACT+"    protected abstract Task StartRace();","abstract fixture")
    fixture=patch(fixture, '\n}\n', TESTS+'\n}\n',"NUnit test declarations")
    fixture=patch(fixture,
        '"ComboFound"=>"present","ComboMissing"=>"missing",',
        '"ComboFound"=>"present","ComboMissing"=>"missing",\n'
        '            "CatchMissingRecovery"=>"missing_recover",'
        '"CatchMissingUnusedVariable"=>"missing_recover",\n'
        '            "CatchTimeoutRecovery"=>"timeout_recover",'
        '"CatchMissingRethrow"=>"missing_rethrow",\n'
        '            "CatchTimeoutRethrow"=>"timeout_rethrow",'
        '"CatchMissingDoesNotCatchAssertion"=>"assertion_unrelated",',
        "NUnit scenario mapping")
    selenium=patch(base.SELENIUM_TESTS,
        '    protected override Task Close(){',
        SELENIUM_OVERRIDES+'    protected override Task Close(){',
        "Real Selenium source bindings")
    playwright=patch(base.PLAYWRIGHT_TESTS,
        '    protected override async Task Close(){',
        PLAYWRIGHT_OVERRIDES+'    protected override async Task Close(){',
        "Real Playwright target bindings")
    return source,fixture,selenium,playwright

def run_case(project,evidence,engine,method):
    fixture="SeleniumParityTests" if engine=="selenium" else "PlaywrightParityTests"
    log_name=engine+"_"+method+".trx"
    command=["dotnet","test","-c","Release","--no-build","--no-restore",
             "--nologo","--logger","trx;LogFileName="+log_name,
             "--results-directory",evidence,
             "--filter","FullyQualifiedName~"+fixture+"&Name~"+method]
    response=base.run(command,project,timeout=300)
    trx=evidence/log_name
    base.check(trx.is_file(),"No real NUnit TRX: "+str(response))
    return {"exit":response["code"],"tests":base.read_trx(trx),
            "log":response["stdout"][-2000:]}

def main():
    repo=Path(__file__).resolve().parent.parent
    chrome=Path(os.environ["MIGRATOR_CHROME_BIN"])
    driver=Path(os.environ["MIGRATOR_CHROMEDRIVER_BIN"])
    base.check(chrome.is_file() and driver.is_file(),
               "Exact Chrome for Testing and compatible ChromeDriver required")
    cli=repo/"Migrator.AssemblyCli"/"bin"/"Release"/"net10.0"/"Migrator.AssemblyCli.dll"
    base.check(cli.is_file(),"Build source-backed Core CLI first")
    evidence=repo/"artifacts"/"pom-preview38"
    evidence.mkdir(parents=True,exist_ok=True)
    with ThreadingHTTPServer(("127.0.0.1",0),base.Handler) as server:
        threading.Thread(target=server.serve_forever,daemon=True).start()
        try:
            with tempfile.TemporaryDirectory(prefix="fictional-p38-exceptions-") as temporary:
                root=Path(temporary)
                pom=root/"pom";pom.mkdir()
                source,fixture,selenium,playwright=synthetic_fixtures()
                helpers=base.HELPERS
                (pom/"Page.cs").write_bytes(source.encode("utf-8"))
                (pom/"Helpers.cs").write_bytes(helpers.encode("utf-8"))
                hashes={p.name:hashlib.sha256(p.read_bytes()).hexdigest()
                        for p in sorted(pom.glob("*.cs"))}
                tree_hash=json_cli(["dotnet",cli,"pom-inspect",pom],repo)["sourceTreeHash"]
                plans={}
                for method,positive in CASES:
                    p=json_cli(["dotnet",cli,"pom-plan",pom,
                        "Fictional.P37.SourcePage",method,tree_hash,"--emit-target"],repo)
                    target=p["targetStatements"][0] if p["targetStatements"] else ""
                    base.check(p["status"]=="requires_review" and not p["runtimeVerified"]
                         and not p["canAutoApply"] and p["adapterCandidate"]["requiresReview"]
                         and len(p["targetStatements"])==1
                         and target.startswith("try\n{")
                         and "OpenQA.Selenium." not in target
                         and any(a["effect"]=="source_exception_catch" for a in p["actions"]),
                         "No complete reviewed source catch candidate: "+method+" "+str(p))
                    if "Timeout" in method:
                        base.check("catch (System.TimeoutException)" in target,
                            "Wait timeout catcher must be limited to typed wait")
                    else:
                        base.check("catch (Microsoft.Playwright.PlaywrightException sourceMissing)" in target
                             and "when (sourceMissing.Message.Contains(" in target
                             and "POM_SOURCE_ELEMENT_ABSENT" in target,
                             "NoSuch exception must use emitted missing-element marker")
                    plans[method]=p
                for name in ("UnsafeClickMissingCatch","UnsafeCatchFilter",
                             "UnsafeBroadCatch","UnsafeCatchVariableUsed",
                             "UnsafeCatchSideEffect"):
                    response=base.run(["dotnet",cli,"pom-plan",pom,
                         "SourcePage",name,tree_hash,"--emit-target"],repo)
                    base.check(response["code"]==3 and
                         json.loads(response["stdout"])["targetStatements"]==[],
                         name+" must remain blocked, not partially emitted")
                redacted=json_cli(["dotnet",cli,"pom-plan",pom,
                    "SourcePage","CatchMissingRecovery",tree_hash],repo)
                base.check(redacted["targetStatements"]==[] and
                    redacted["adapterCandidate"] is None,
                    "Default Core API exposed executable code")
                base.check({p.name:hashlib.sha256(p.read_bytes()).hexdigest()
                    for p in sorted(pom.glob("*.cs"))}==hashes,
                    "Source mutated by read-only projector")
                for token,method in PROJECTIONS.items():
                    playwright=patch(playwright,token,
                        plans[method]["targetStatements"][0],method)
                # The public preview.37 base fixture contains additional
                # abstract methods that are NOT selected by this scope.
                # Keep them compilable without inventing their behavior:
                # they are deliberate guard stubs, never executed as evidence.
                for placeholder in sorted(set(re.findall(r"__[A-Z][A-Z_0-9]*__",playwright))):
                    playwright=playwright.replace(placeholder,
                        'throw new System.NotSupportedException("not in preview.38 selected exception fixture");')
                base.check(not re.search(r"__[A-Z][A-Z_0-9]*__",playwright),
                           "Unexpanded Playwright source fixture placeholder")
                project=root/"tests";project.mkdir()
                for name,content in {
                    "Synthetic.Tests.csproj":base.CSPROJ,
                    "SourcePage.cs":source,"Helpers.cs":helpers,
                    "ParityTestsBase.cs":fixture,"SeleniumParityTests.cs":selenium,
                    "PlaywrightParityTests.cs":playwright
                }.items():
                    (project/name).write_bytes(content.encode("utf-8"))
                build=base.run(["dotnet","build","-c","Release","--nologo"],
                               project,timeout=360)
                base.check(build["code"]==0,"Real Selenium/Playwright compilation failed: "+str(build))
                os.environ["POM37_URL"]="http://127.0.0.1:"+str(server.server_port)+"/"
                os.environ["POM37_EVIDENCE"]=str(evidence)
                results={}
                for engine in ("selenium","playwright"):
                    for method,positive in CASES:
                        run=run_case(project,evidence,engine,method)
                        base.check((run["exit"]==0)==positive and
                            run["tests"]=={method:"Passed" if positive else "Failed"},
                            "Real NUnit outcome differs: "+engine+"/"+method+" "+str(run))
                        results[(engine,method)]=run
                case_evidence={}
                for method,positive in CASES:
                    pair={}
                    for engine in ("selenium","playwright"):
                        path=evidence/(engine+"-"+method+".json")
                        base.check(path.is_file(),"Missing browser JSON for "+engine+"/"+method)
                        record=json.loads(path.read_text(encoding="utf-8"))
                        base.check(record["browserClosed"] and record["error"] is None
                                   and record["binaryPath"]==str(chrome),
                                   "Wrong browser binary or cleanup: "+str(record))
                        record["snapshot"]=json.loads(record["snapshot"])
                        if not positive:
                            img=evidence/record["screenshot"]
                            base.check(img.is_file()
                                and img.read_bytes()[:8]==b"\x89PNG\r\n\x1a\n",
                                "No real PNG screenshot for "+method+"/"+engine)
                        pair[engine]=record
                    base.check(pair["selenium"]["snapshot"]==pair["playwright"]["snapshot"],
                               "Same-browser DOM/events drift: "+method)
                    if positive:
                        base.check(pair["selenium"]["exceptionType"] is None
                            and pair["playwright"]["exceptionType"] is None,
                            "Recovery should be caught on both drivers")
                    else:
                        for engine,expected in EXCEPTIONS[method].items():
                            base.check(pair[engine]["exceptionType"]==expected,
                                "Observed exception differs: "+method+"/"+engine+
                                " actual="+str(pair[engine]["exceptionType"]))
                    case_evidence[method]={
                        "seleniumOutcome":pair["selenium"]["outcome"],
                        "playwrightOutcome":pair["playwright"]["outcome"],
                        "seleniumException":pair["selenium"]["exceptionType"],
                        "playwrightException":pair["playwright"]["exceptionType"],
                        "sourceMethodSha256":plans[method]["sourceMethodSha256"],
                        "sourceEffects":[a["effect"] for a in plans[method]["actions"]],
                        "sameBrowserDomAndAppEvents":True,"bothBrowsersClosed":True,
                        "canAutoApply":False
                    }
                summary={
                    "schema":"migrator-preview38-typed-catch-real-browser/v1",
                    "source":"real_Selenium_WebDriver_ChromeDriver_NUnit",
                    "target":"real_Playwright_DotNet_review_only",
                    "chrome_version":os.environ.get("MIGRATOR_CHROME_VERSION"),
                    "chromedriver_version":os.environ.get("MIGRATOR_CHROMEDRIVER_VERSION"),
                    "chrome_binary_sha256":hashlib.sha256(chrome.read_bytes()).hexdigest(),
                    "chromedriver_binary_sha256":hashlib.sha256(driver.read_bytes()).hexdigest(),
                    "sourceTreeSha256":tree_hash,"sourceFilesSha256":hashes,
                    "cases":case_evidence,
                    "blocked_ast_negative_controls":["UnsafeClickMissingCatch",
                        "UnsafeCatchFilter","UnsafeBroadCatch",
                        "UnsafeCatchVariableUsed","UnsafeCatchSideEffect"],
                    "external_exception_type_equivalence":"not_equivalent",
                    "no_Selenium_dependency_in_emitted_target":True,
                    "full_product_equivalence":"not_verified",
                    "source_candidate_auto_apply":False,"private_ArBilling":"not_run"
                }
                (evidence/"summary.json").write_text(
                    json.dumps(summary,sort_keys=True,indent=2)+"\n",encoding="utf-8")
                manifest={p.name:hashlib.sha256(p.read_bytes()).hexdigest()
                          for p in sorted(evidence.iterdir())
                          if p.is_file() and p.name!="SHA256SUMS.json"}
                (evidence/"SHA256SUMS.json").write_text(
                    json.dumps(manifest,sort_keys=True,indent=2)+"\n",encoding="utf-8")
                print("POM_PREVIEW38_REAL_SELENIUM_PLAYWRIGHT_EXCEPTIONS_PASSED")
                print(json.dumps(summary,sort_keys=True))
        finally:
            server.shutdown()

if __name__=="__main__":
    main()
