#!/usr/bin/env python3
"""Immutable public Selenium POM vs actual Core-projected Playwright in Chrome.

Review-only method projection. No original upstream NUnit lifecycle, project
scripts or external website is executed. Full-project RC remains UNKNOWN.
"""
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import threading

import p2_real_public_text_input_slice as baseline

FILES = {
    "AssignmentProject/Pages/HomePage.cs":
        "c69b3afb253db994cdab978207f27285979b4eae",
    "AssignmentProject/Pages/SampleAppPage.cs":
        "35e23d75a017f50601806c754b4e823386f8394a",
    "AssignmentProject/Tests/SampleAppTests.cs":
        "5eaaded454c54b4e9a49b4ab71aa7e8a6feca9c0",
}
PAGES = tuple(p for p in FILES if "/Pages/" in p)
METHODS = (
    ("HomePage", "GoToSampleApp", ("click",)),
    ("SampleAppPage", "Login", ("input_sendkeys", "input_sendkeys", "click")),
    ("SampleAppPage", "GetMessage", ("rendered_text_return",)),
)
OUT = Path("artifacts/p2-generated-public-sampleapp")
GENERATED = r"""
namespace Migrator.PublicSampleApp;
internal sealed class CoreProjectedHomePage {
  private readonly Microsoft.Playwright.IPage Page;
  public CoreProjectedHomePage(Microsoft.Playwright.IPage page) { Page = page; }
  public async System.Threading.Tasks.Task GoToSampleApp() {
__NAV__
  }
}
internal sealed class CoreProjectedSampleApp {
  private readonly Microsoft.Playwright.IPage Page;
  public CoreProjectedSampleApp(Microsoft.Playwright.IPage page) { Page = page; }
  public async System.Threading.Tasks.Task Login(string username, string password) {
__LOGIN__
  }
  public async System.Threading.Tasks.Task<string> GetMessage() {
__MESSAGE__
  }
}
internal sealed class SourceMutation {
  private readonly Microsoft.Playwright.IPage Page;
  public SourceMutation(Microsoft.Playwright.IPage page) { Page = page; }
  public async System.Threading.Tasks.Task MissingClick(string username, string password) {
__MISSING_CLICK__
  }
  public async System.Threading.Tasks.Task WrongSelector(string username, string password) {
__WRONG_SELECTOR__
  }
}
"""
HARNESS = r"""
using System;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using Microsoft.Playwright;
using CSE2522_Selenium_NUnit.Pages;
namespace Migrator.PublicSampleApp;
internal static class TestEnvironment {
  public static string Url => Environment.GetEnvironmentVariable("PUBLIC_POM_URL")!;
  public static string Dir => Environment.GetEnvironmentVariable("PUBLIC_POM_OUT")!;
  public static void Save(string key, string snapshot) =>
    File.WriteAllText(Path.Combine(Dir, key + ".json"), snapshot);
  public static ChromeDriver Selenium() {
    var exe = Environment.GetEnvironmentVariable("MIGRATOR_CHROMEDRIVER_BIN")!;
    var svc = ChromeDriverService.CreateDefaultService(
      Path.GetDirectoryName(exe)!, Path.GetFileName(exe));
    svc.HideCommandPromptWindow = true;
    var options = new ChromeOptions {
      BinaryLocation = Environment.GetEnvironmentVariable("MIGRATOR_CHROME_BIN")! };
    options.AddArgument("--headless=new");
    options.AddArgument("--no-sandbox");
    options.AddArgument("--disable-dev-shm-usage");
    return new ChromeDriver(svc, options);
  }
  public static async Task<(IPlaywright, IBrowser, IPage)> Playwright() {
    var pw = await Microsoft.Playwright.Playwright.CreateAsync();
    try {
      var browser = await pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions {
        ExecutablePath = Environment.GetEnvironmentVariable("MIGRATOR_CHROME_BIN")!,
        Headless = true, Args = new[] { "--no-sandbox", "--disable-dev-shm-usage" } });
      var page = await browser.NewPageAsync();
      page.SetDefaultTimeout(1500);
      await page.GotoAsync(Url + "/");
      return (pw, browser, page);
    } catch { pw.Dispose(); throw; }
  }
  public static string SeleniumState(IWebDriver driver) =>
    (string)((IJavaScriptExecutor)driver)
      .ExecuteScript("return JSON.stringify(window.snapshot())")!;
  public static Task<string> PlaywrightState(IPage page) =>
    page.EvaluateAsync<string>("() => JSON.stringify(window.snapshot())");
}
[TestFixture, NonParallelizable]
public sealed class OriginalPublicSampleApp {
  [Test] public void Positive() => Run("pwd", "Welcome abc", "selenium_positive");
  [Test] public void Negative() => Run("wrong", "Invalid credentials", "selenium_negative");
  private static void Run(string password, string expected, string key) {
    var driver = TestEnvironment.Selenium();
    try {
      driver.Navigate().GoToUrl(TestEnvironment.Url + "/");
      new HomePage(driver).GoToSampleApp();
      var pom = new SampleAppPage(driver);
      pom.Login("abc", password);
      Assert.That(pom.GetMessage(), Does.Contain(expected));
      TestEnvironment.Save(key, TestEnvironment.SeleniumState(driver));
    } finally { driver.Quit(); driver.Dispose(); }
  }
}
[TestFixture, NonParallelizable]
public sealed class CoreProjectedSampleAppTests {
  [Test] public async Task Positive() =>
    await Run("pwd", "Welcome abc", "projected_positive");
  [Test] public async Task Negative() =>
    await Run("wrong", "Invalid credentials", "projected_negative");
  private static async Task Run(string password, string expected, string key) {
    var (pw, browser, page) = await TestEnvironment.Playwright();
    try {
      await new CoreProjectedHomePage(page).GoToSampleApp();
      var pom = new CoreProjectedSampleApp(page);
      await pom.Login("abc", password);
      Assert.That(await pom.GetMessage(), Does.Contain(expected));
      TestEnvironment.Save(key, await TestEnvironment.PlaywrightState(page));
    } finally { await browser.CloseAsync(); pw.Dispose(); }
  }
}
[TestFixture, NonParallelizable]
public sealed class MutantMissingClick {
  [Test] public async Task FalseGreen() {
    var (pw, browser, page) = await TestEnvironment.Playwright();
    try {
      await new CoreProjectedHomePage(page).GoToSampleApp();
      await new SourceMutation(page).MissingClick("abc", "pwd");
      Assert.That(true, Is.True);
      TestEnvironment.Save("missing_click", await TestEnvironment.PlaywrightState(page));
    } finally { await browser.CloseAsync(); pw.Dispose(); }
  }
}
[TestFixture, NonParallelizable]
public sealed class MutantSwappedArguments {
  [Test] public async Task FalseGreen() {
    var (pw, browser, page) = await TestEnvironment.Playwright();
    try {
      await new CoreProjectedHomePage(page).GoToSampleApp();
      await new CoreProjectedSampleApp(page).Login("pwd", "abc");
      Assert.That(true, Is.True);
      TestEnvironment.Save("swapped_arguments", await TestEnvironment.PlaywrightState(page));
    } finally { await browser.CloseAsync(); pw.Dispose(); }
  }
}
[TestFixture, NonParallelizable]
public sealed class MutantWrongNameSelector {
  [Test] public async Task MustFail() {
    var (pw, browser, page) = await TestEnvironment.Playwright();
    try {
      await new CoreProjectedHomePage(page).GoToSampleApp();
      await new SourceMutation(page).WrongSelector("abc", "pwd");
      Assert.Fail("Mutated By.Name selector was accepted");
    } finally { await browser.CloseAsync(); pw.Dispose(); }
  }
}
"""
INDEX = '<!doctype html><html><body><a href="/sampleapp">Sample App</a></body></html>'
PAGE = r"""<!doctype html><html><body>
<input name="UserName"><input name="Password" type="password">
<button id="login">Log in</button><div id="loginstatus">Ready</div>
<script>
window.events=["page_ready"];window.clickCount=0;
const user=document.querySelector('[name="UserName"]');
const pass=document.querySelector('[name="Password"]');
const status=document.querySelector('#loginstatus');
document.querySelector('#login').onclick=()=>{
  window.clickCount++;
  const good=user.value==="abc" && pass.value==="pwd";
  status.textContent=good ? "Welcome abc" : "Invalid credentials";
  window.events.push("login:"+user.value+":"+(good ? "ok" : "invalid"));
};
window.snapshot=()=>({path:location.pathname,username:user.value,
  password:pass.value,status:status.textContent,clicks:window.clickCount,
  events:window.events.slice()});
</script></body></html>"""


def require(value, msg):
    if not value:
        raise RuntimeError("PUBLIC_SAMPLEAPP_FAIL_CLOSED: " + msg)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def attest(checkout):
    require(baseline.git(checkout, "rev-parse", "HEAD").decode().strip()
            == baseline.COMMIT, "source commit changed")
    records = []
    for rel, blob in sorted(FILES.items()):
        file = checkout / rel
        require(file.is_file() and not file.is_symlink(), "source absent: " + rel)
        require(baseline.git(checkout, "rev-parse", "HEAD:" + rel)
                .decode().strip() == blob, "source Git blob changed: " + rel)
        canonical = baseline.git(checkout, "cat-file", "blob", blob)
        disk = file.read_bytes()
        require(len(canonical) < 1024 * 1024 and
                canonical.replace(b"\r\n", b"\n") ==
                disk.replace(b"\r\n", b"\n"), "source bytes changed: " + rel)
        records.append({"path": rel, "gitBlobSha1": blob,
                        "canonicalSha256": sha(canonical),
                        "checkoutSha256": sha(disk)})
    return records


def plan(cli, source, repo):
    inv = baseline.invoke(("dotnet", str(cli), "pom-inspect", source), repo)
    require(inv["exitCode"] == 0, "real Core source inventory failed")
    index = json.loads(inv["stdoutTail"])
    source_tree = index["sourceTreeHash"]
    require(index["inspectedFiles"] == 2 and len(source_tree) == 64,
            "source scope is not exactly two public POM files")
    plans = {}
    for owner, method, effects in METHODS:
        response = baseline.invoke(("dotnet", str(cli), "pom-plan", source,
            "CSE2522_Selenium_NUnit.Pages." + owner, method, source_tree,
            "--emit-target"), repo)
        require(response["exitCode"] == 0, method + " blocked " + str(response))
        item = json.loads(response["stdoutTail"])
        actions = item["actions"]
        require(item["status"] == "requires_review" and item["blockers"] == []
                and item["sourceTreeHash"] == source_tree
                and len(actions) == len(effects)
                and len(item["targetStatements"]) == len(effects)
                and tuple(a["effect"] for a in actions) == effects
                and all(a["requiresReview"] and len(a["sourceSha256"]) == 64
                        and len(a["targetSha256"]) == 64 and
                        a["sourcePath"].endswith(owner + ".cs") for a in actions)
                and item["runtimeVerified"] is False
                and item["canAutoApply"] is False,
                "obligation/identity/review gate failed: " + method)
        plans[(owner, method)] = item
    nav = plans["HomePage", "GoToSampleApp"]["targetStatements"][0]
    login = plans["SampleAppPage", "Login"]["targetStatements"]
    message = plans["SampleAppPage", "GetMessage"]["targetStatements"][0]
    require("AriaRole.Link" in nav and '"Sample App"' in nav and
            "Exact = true" in nav and
            "[name=" in login[0] and "UserName" in login[0] and
            "PressSequentiallyAsync(username)" in login[0] and
            "[name=" in login[1] and "Password" in login[1] and
            "PressSequentiallyAsync(password)" in login[1] and
            "login" in login[2] and "ClickAsync()" in login[2] and
            "loginstatus" in message and "InnerTextAsync()" in message,
            "original public selector/argument/action/return mismatch")
    require(login[0].count("UserName") == 1, "wrong-name mutant not unique")
    substitutions = {
        "__NAV__": nav, "__LOGIN__": "\n".join(login),
        "__MESSAGE__": message,
        "__MISSING_CLICK__": "\n".join(login[:-1]),
        "__WRONG_SELECTOR__": "\n".join((
            login[0].replace("UserName", "WrongUserName", 1), *login[1:])),
    }
    emitted = GENERATED
    for mark, value in substitutions.items():
        require(emitted.count(mark) == 1 and "OpenQA.Selenium" not in value,
                "unsafe/missing Core emitter substitution")
        emitted = emitted.replace(mark, value)
    require("__" not in emitted, "partial target output")
    return source_tree, plans, emitted


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path == "/":
            data = INDEX.encode()
        elif self.path == "/sampleapp":
            data = PAGE.encode()
        else:
            self.send_error(404)
            return
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, *_args):
        pass


def expected(password):
    good = password == "pwd"
    return {"path": "/sampleapp", "username": "abc", "password": password,
            "status": "Welcome abc" if good else "Invalid credentials",
            "clicks": 1, "events": ["page_ready", "login:abc:" +
                                    ("ok" if good else "invalid")]}


def main(arg):
    checkout = Path(arg).resolve()
    root = Path(__file__).resolve().parent.parent
    out = root / OUT
    out.mkdir(parents=True, exist_ok=True)
    records = attest(checkout)
    cli = root / "Migrator.AssemblyCli" / "bin" / "Release" / "net10.0" / "Migrator.AssemblyCli.dll"
    require(cli.is_file(), "real Core CLI must be compiled")
    require(Path(os.environ["MIGRATOR_CHROME_BIN"]).is_file() and
            Path(os.environ["MIGRATOR_CHROMEDRIVER_BIN"]).is_file(),
            "same explicit Chrome/ChromeDriver binary paths missing")
    with tempfile.TemporaryDirectory(prefix="migrator-public-sampleapp-") as folder:
        tmp = Path(folder)
        pom = tmp / "pom"
        pom.mkdir()
        for rel in PAGES:
            shutil.copyfile(checkout / rel, pom / Path(rel).name)
        tree, plans, generated = plan(cli, pom, root)
        target = tmp / "test"
        target.mkdir()
        for rel in PAGES:
            shutil.copyfile(checkout / rel, target / Path(rel).name)
        (target / "Pilot.csproj").write_text(baseline.PROJECT, encoding="utf-8")
        (target / "CoreGenerated.cs").write_text(generated, encoding="utf-8")
        (target / "Pilot.cs").write_text(HARNESS, encoding="utf-8")
        with ThreadingHTTPServer(("127.0.0.1", 0), Handler) as server:
            threading.Thread(target=server.serve_forever, daemon=True).start()
            env = {"PUBLIC_POM_URL": "http://127.0.0.1:" + str(server.server_port),
                   "PUBLIC_POM_OUT": str(out)}
            build = baseline.invoke(("dotnet", "build", "Pilot.csproj",
                "-c", "Release", "--nologo"), target)
            require(build["exitCode"] == 0,
                    "actual Core-generated target does not compile: " + str(build))
            cases = (
                ("OriginalPublicSampleApp", "Positive", "Passed", True),
                ("OriginalPublicSampleApp", "Negative", "Passed", True),
                ("CoreProjectedSampleAppTests", "Positive", "Passed", True),
                ("CoreProjectedSampleAppTests", "Negative", "Passed", True),
                ("MutantMissingClick", "FalseGreen", "Passed", True),
                ("MutantSwappedArguments", "FalseGreen", "Passed", True),
                ("MutantWrongNameSelector", "MustFail", "Failed", False),
            )
            observed = {}
            for fixture, name, outcome, success in cases:
                trx = fixture + "-" + name + ".trx"
                run = baseline.invoke(("dotnet", "test", "Pilot.csproj",
                    "-c", "Release", "--no-build", "--no-restore", "--nologo",
                    "--filter", "FullyQualifiedName~" + fixture + "." + name,
                    "--logger", "trx;LogFileName=" + trx,
                    "--results-directory", out), target, timeout=180, env=env)
                require((run["exitCode"] == 0) == success,
                        "NUnit runner mismatch " + fixture + "." + name + ": " + str(run))
                actual = baseline.trx(out / trx, name)
                require(actual == outcome, "NUnit TRX outcome mismatch: " + trx)
                observed[fixture + "." + name] = {
                    "outcome": actual, "trxSha256": sha((out / trx).read_bytes())}
            states = {name: json.loads((out / (name + ".json")).read_text())
                      for name in ("selenium_positive", "selenium_negative",
                                   "projected_positive", "projected_negative",
                                   "missing_click", "swapped_arguments")}
            require(states["selenium_positive"] ==
                    states["projected_positive"] == expected("pwd"),
                    "real successful login Selenium/Core DOM/action mismatch")
            require(states["selenium_negative"] ==
                    states["projected_negative"] == expected("wrong"),
                    "real failed login Selenium/Core DOM/action mismatch")
            require(states["missing_click"]["clicks"] == 0 and
                    states["missing_click"]["status"] == "Ready" and
                    states["missing_click"] != expected("pwd"),
                    "green NUnit without Click escaped independent oracle")
            require(states["swapped_arguments"]["username"] == "pwd" and
                    states["swapped_arguments"]["status"] == "Invalid credentials" and
                    states["swapped_arguments"] != expected("pwd"),
                    "green NUnit with swapped argument escaped oracle")
            original = (pom / "SampleAppPage.cs").read_bytes()
            require(original.count(b'By.Name("UserName")') == 1,
                    "source selector no longer unique")
            (pom / "SampleAppPage.cs").write_bytes(original.replace(
                b'By.Name("UserName")', b'By.Name("ChangedUserName")'))
            stale = baseline.invoke(("dotnet", str(cli), "pom-plan", pom,
                "CSE2522_Selenium_NUnit.Pages.SampleAppPage", "Login", tree,
                "--emit-target"), root)
            require(stale["exitCode"] != 0 and
                    "STALE_POM_ACTION_PLAN" in stale["stderrTail"],
                    "stale source selector was reused")
            (pom / "SampleAppPage.cs").write_bytes(original)
            methods = {}
            for (owner, method), item in plans.items():
                methods[owner+"."+method] = {
                    "sourceMethodSha256": item["sourceMethodSha256"],
                    "sourceTreeHash": item["sourceTreeHash"],
                    "sourceLine": item["sourceLine"],
                    "actionLineage": [{"order":a["order"],"effect":a["effect"],
                      "sourceSha256":a["sourceSha256"],
                      "targetSha256":a["targetSha256"],
                      "controlFlowPath":a["controlFlowPath"]} for a in item["actions"]],
                    "targetStatementSha256": [sha(s.encode()) for s in
                                               item["targetStatements"]]}
            files = sorted(f for f in out.iterdir() if f.is_file()
                           and f.name != "receipt.json")
            receipt = {
                "schema":"migrator-public-core-generated-sampleapp/v1",
                "sourceCommit":baseline.COMMIT, "sourceFiles":records,
                "sourceTreeHash":tree, "generatedTargetSha256":sha(generated.encode()),
                "generatedMethodLineage":methods,"realNUnitTRX":observed,
                "loginSuccessAndFailureDOM":"bounded_parity_pass",
                "missingClick":"NUnit_PASS_oracle_REJECT",
                "swappedArguments":"NUnit_PASS_oracle_REJECT",
                "wrongNameSelector":"NUnit_FAIL",
                "staleSourceSelector":"REJECT",
                "originalNUnitLifecycle":"not_run",
                "originalUpstreamProjectBuild":"not_run_in_this_harness",
                "fullProjectEquivalence":"UNKNOWN",
                "candidateId":"not_created_by_pom_plan",
                "runtimeVerifiedFullProject":False,
                "canAutoApply":False,"acceptedForRC":False,
                "externalWebsiteUsed":False,"upstreamProjectScriptsExecuted":False,
                "files":[{"file":f.name,"sha256":sha(f.read_bytes())} for f in files],
            }
            (out / "receipt.json").write_text(
                json.dumps(receipt,sort_keys=True,indent=2)+"\n",encoding="utf-8")
            print("PUBLIC_SAMPLEAPP_CORE_GENERATED_BOUNDED_PARITY_PASSED")
            print(json.dumps({"source":baseline.COMMIT,
                "targetSha256":sha(generated.encode()),
                "projectEquivalent":"UNKNOWN","release":"BLOCKED"},sort_keys=True))


if __name__ == "__main__":
    require(len(sys.argv)==2,"source checkout argument required")
    main(sys.argv[1])
