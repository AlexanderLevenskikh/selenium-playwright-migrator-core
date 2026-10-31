#!/usr/bin/env python3
"""Public synthetic P2: read-only Roslyn templates -> reviewed Playwright NUnit
assembly -> real installed Chromium on a loopback-only app. Original NUnit
control suite checks discovery/run-state, not Selenium UI behavioral parity.
No source customer project, no generated selector guesses.
"""
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import xml.etree.ElementTree as ET

PAGE_OBJECT = """
using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using NUnit.Framework;
using System;
namespace Fictional.Ui;
public sealed class CatalogPage {
    private readonly IWebDriver driver;
    public CatalogPage(IWebDriver driver) { this.driver = driver; }
    public void Open(string url) { driver.Navigate().GoToUrl(url); }
    public void Choose(string value) {
        OpenPicker();
        ResetAndType(value);
        if (driver.FindElements(By.CssSelector("[data-test='option']")).Count > 0) {
            driver.FindElements(By.CssSelector("[data-test='option']"))[0].Click();
            driver.FindElement(By.Id("accept")).Click();
        } else {
            Assert.That(driver.FindElements(By.CssSelector("[data-test='option']")).Count,
                Is.EqualTo(0));
        }
    }
    private void OpenPicker() { driver.FindElement(By.Id("picker")).Click(); }
    private void ResetAndType(string search) {
        driver.FindElement(By.Id("search")).Clear();
        TypeValue(search);
    }
    private void TypeValue(string text) {
        driver.FindElement(By.Id("search")).SendKeys(text);
    }
    public void WaitReady(int timeoutMs) {
        if (timeoutMs < 1 || timeoutMs > 3000)
            throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        new WebDriverWait(driver, TimeSpan.FromMilliseconds(timeoutMs))
            .Until(d => d.FindElement(By.Id("ready")).Displayed);
    }
    public void RetryReady() {
        for (var attempt = 0; attempt < 2; attempt++) {
            try {
                new WebDriverWait(driver, TimeSpan.FromMilliseconds(120))
                    .Until(d => d.FindElement(By.Id("ready")).Displayed);
                break;
            } catch (WebDriverTimeoutException) {
                if (attempt == 1) throw;
            }
        }
    }
    public void CheckRows() {
        Assert.That(driver.FindElements(By.CssSelector("[data-test='row']")).Count,
            Is.GreaterThan(1));
    }
    public void AssertFirstRow(string expected) {
        Assert.That(driver.FindElements(By.CssSelector("[data-test='row']"))[0].Text,
            Is.EqualTo(expected));
    }
    public void ClickFirstRow() {
        driver.FindElements(By.CssSelector("[data-test='row']"))[0].Click();
    }
    public void UnsafeDynamicWait(int timeoutMs) {
        new WebDriverWait(driver, TimeSpan.FromMilliseconds(timeoutMs))
            .Until(d => d.FindElement(By.Id("ready")).Displayed);
    }
    public void UnsafeConditionalBranch() {
        if (driver.FindElements(By.Id("picker")).Count > 0) {
            driver.FindElement(By.Id("picker")).Click();
        } else { System.Threading.Thread.Sleep(1); }
    }
}
"""
HTML = """<!doctype html><html><head><meta charset="utf-8"><title>Fictional P31</title></head>
<body>
  <button id="picker">Open</button>
  <input id="search" />
  <button data-test="option" onclick="document.getElementById('selected').textContent=document.getElementById('search').value">Choose</button>
  <button id="accept">Accept</button>
  <div id="ready" style="height:8px;width:20px;background:gray"></div>
  <div data-test="row" onclick="this.setAttribute('data-clicked','yes')">Row A</div>
  <div data-test="row">Row B</div>
  <div id="selected"></div>
  <script>console.log('fictional-p31-console-ready');</script>
</body></html>"""
PACKAGES = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsTestProject>true</IsTestProject>
    <IsPackable>false</IsPackable>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="NUnit" Version="4.3.2" />
    <PackageReference Include="NUnit3TestAdapter" Version="5.1.0" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    {playwright}
  </ItemGroup>
</Project>"""

SOURCE_TESTS = """
using System;
using System.Net.Http;
using System.Threading.Tasks;
using NUnit.Framework;
namespace Fictional.Preview32;
[TestFixture] public class CatalogSuite {
    [TestCase("alpha")] [TestCase("beta")]
    public async Task ExecutesPOM(string value) {
        using var client = new HttpClient();
        var page = await client.GetStringAsync(
            Environment.GetEnvironmentVariable("POM32_URL")!);
        Assert.That(page.Contains("data-test=\\\"row\\\""), Is.True);
        Assert.That(page.Contains("id=\\\"search\\\""), Is.True);
        Assert.That(value.Length, Is.GreaterThan(0));
    }
    [Test] [Ignore("synthetic maintenance")]
    public void Disabled() { Assert.Fail("disabled test activated"); }
    [Test] [Explicit("synthetic opt-in")]
    public void OptIn() { Assert.That(true, Is.True); }
    [Test] [Explicit("synthetic expected failure")]
    public void DeliberateFailure() { Assert.Fail("deliberate synthetic failure"); }
    [Test] [Explicit("synthetic terminal wait failure")]
    public void RetryFailure() { Assert.Fail("expected fictional retry timeout"); }
}
"""

TARGET_TEMPLATE = r"""
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Playwright;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
namespace Fictional.Preview32;
[TestFixture] [NonParallelizable]
public class CatalogSuite {
    private IPlaywright? pw;
    private IBrowser? browser;
    private IBrowserContext? context;
    private IPage? current;
    private IPage Page => current!;
    private static void Record(string message) {
        var path = Environment.GetEnvironmentVariable("POM32_TRACE");
        if (path != null) File.AppendAllText(path, message + "\n");
    }
    [OneTimeSetUp]
    public async Task Start() {
        pw = await Playwright.CreateAsync();
        browser = await pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        Record("browser-open");
    }
    [SetUp]
    public async Task Setup() {
        context = await browser!.NewContextAsync();
        current = await context.NewPageAsync();
        current.Console += (_, message) => {
            var path = Environment.GetEnvironmentVariable("POM32_CONSOLE");
            if (path != null) File.AppendAllText(path, message.Text + "\n");
        };
        Record("context-open");
    }
    [TearDown]
    public async Task Teardown() {
        if (TestContext.CurrentContext.Result.Outcome.Status == TestStatus.Failed) {
            var path = Environment.GetEnvironmentVariable("POM32_SCREENSHOT");
            if (path != null && current != null)
                await current.ScreenshotAsync(new PageScreenshotOptions { Path = path });
            Record("screenshot-on-failure");
        }
        if (context != null) {
            await context.CloseAsync();
            Record("context-close");
        }
        current = null;
        context = null;
    }
    [OneTimeTearDown]
    public async Task End() {
        if (browser != null) {
            await browser.CloseAsync();
            Record("browser-close");
        }
        pw?.Dispose();
        Record("playwright-dispose");
    }

    [TestCase("alpha")] [TestCase("beta")]
    public async Task ExecutesPOM(string value) {
        string url = Environment.GetEnvironmentVariable("POM32_URL")!;
        string expected = "Row A";
        int timeoutMs = 1200;
        // First action is the exact source-bound navigation.
__OPEN_STATEMENTS__
        // beta exercises the else branch of the source-backed ComboBox.
        if (value == "beta") {
            await Page.Locator("[data-test='option']")
                .EvaluateAsync("el => el.remove()");
        }
        // All selected browser actions below are source-attested,
        // review-required, and generated from Roslyn's typed source AST.
__PROJECTION_STATEMENTS__
        if (value == "alpha") {
            Assert.That(await Page.Locator("#selected").InnerTextAsync(),
                Is.EqualTo(value));
        } else {
            Assert.That(await Page.Locator("[data-test='option']").CountAsync(),
                Is.EqualTo(0));
            Assert.That(await Page.Locator("#search").InputValueAsync(),
                Is.EqualTo(value));
        }
        Assert.That(await Page.Locator("[data-test='row']").First
            .GetAttributeAsync("data-clicked"), Is.EqualTo("yes"));
    }
    [Test] [Ignore("synthetic maintenance")]
    public void Disabled() { Assert.Fail("disabled test activated"); }
    [Test] [Explicit("synthetic opt-in")]
    public void OptIn() { Assert.That(true, Is.True); }
    [Test] [Explicit("synthetic expected failure")]
    public void DeliberateFailure() { Assert.Fail("deliberate synthetic failure"); }
    [Test] [Explicit("synthetic terminal wait failure")]
    public async Task RetryFailure() {
        string url = Environment.GetEnvironmentVariable("POM32_URL")!;
        await Page.GotoAsync(url);
        await Page.Locator("#ready").EvaluateAsync("el => el.style.display='none'");
        try {
__RETRY_FAILURE_STATEMENTS__
            Assert.Fail("bounded source retry unexpectedly succeeded");
        } catch (System.TimeoutException) {
            Record("retry-terminal-timeout-type-confirmed");
            throw;
        }
    }
}
"""


def check(ok, reason):
    if not ok:
        raise AssertionError(reason)


def run(argv, cwd, env=None, timeout=300):
    proc = subprocess.run(list(map(str, argv)), cwd=str(cwd), text=True,
                          capture_output=True, timeout=timeout,
                          env=dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1",
                                   **(env or {})))
    return {"exit": proc.returncode, "stdout": proc.stdout[-11000:],
            "stderr": proc.stderr[-5000:]}


def jrun(args, cwd, **kw):
    p = run(args, cwd, **kw)
    check(p["exit"] == 0, "CLI failed: " + str(args) + " " + str(p))
    return json.loads(p["stdout"])


def trx(path):
    check(path.exists(), "TRX missing: " + str(path))
    root = ET.parse(path).getroot()
    records = {}
    for elem in root.iter():
        if elem.tag.endswith("UnitTestResult"):
            name = elem.attrib.get("testName")
            check(name and name not in records, "Invalid/duplicated testName in " + str(path))
            records[name] = elem.attrib.get("outcome")
    return records


def discovery(path, env):
    r = run(["dotnet", "test", "--list-tests", "-c", "Release",
             "--no-restore", "--nologo"], path, env=env)
    check(r["exit"] == 0, "Discovery failed: " + str(r))
    lines = r["stdout"].splitlines()
    indices = [i for i, line in enumerate(lines)
               if "following tests are available" in line.lower()]
    check(indices, "NUnit list-tests marker missing")
    return sorted(x.strip() for x in lines[indices[-1] + 1:] if x.strip())


def test(path, label, directory, env, filter_text=None):
    directory.mkdir(exist_ok=True)
    cmd = ["dotnet", "test", "-c", "Release", "--no-restore", "--no-build",
           "--nologo", "--logger", "trx;LogFileName=" + label + ".trx",
           "--results-directory", str(directory)]
    if filter_text:
        cmd.extend(["--filter", "FullyQualifiedName~" + filter_text])
    result = run(cmd, path, env=env, timeout=180)
    result["records"] = trx(directory / (label + ".trx"))
    return result


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        body = HTML.encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, fmt, *args):
        pass


def main():
    repo = Path(__file__).resolve().parent.parent
    cli = repo / "Migrator.AssemblyCli" / "bin" / "Release" / "net10.0" / "Migrator.AssemblyCli.dll"
    check(cli.exists(), "Build Migrator.AssemblyCli -c Release first")
    evidence = repo / "artifacts" / "pom-preview32"
    evidence.mkdir(parents=True, exist_ok=True)
    with ThreadingHTTPServer(("127.0.0.1", 0), Handler) as server:
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        url = "http://127.0.0.1:" + str(server.server_port) + "/"
        with tempfile.TemporaryDirectory(prefix="fictional-pom-preview32-") as temp:
            folder = Path(temp)
            source_dir = folder / "pom"
            source_dir.mkdir()
            pom_file = source_dir / "CatalogPage.cs"
            pom_file.write_text(PAGE_OBJECT, encoding="utf-8")
            pom_sha = hashlib.sha256(pom_file.read_bytes()).hexdigest()
            h = jrun(["dotnet", cli, "pom-inspect", source_dir], repo)["sourceTreeHash"]
            projections = {}
            for method in ("Open", "Choose", "WaitReady", "RetryReady",
                           "CheckRows", "AssertFirstRow", "ClickFirstRow"):
                plan = jrun(["dotnet", cli, "pom-plan", source_dir,
                             "Fictional.Ui.CatalogPage", method, h,
                             "--emit-target"], repo)
                check(plan["status"] == "requires_review"
                      and plan["sourceMethodSha256"]
                      and plan["targetStatements"], "No source-backed projection for " + method)
                candidate = plan.get("adapterCandidate")
                check(candidate is not None and candidate["requiresReview"]
                      and candidate["sourceMethodSha256"] == plan["sourceMethodSha256"],
                      method + " review-only adapter candidate is missing")
                if method == "Choose":
                    check(any("PressSequentiallyAsync({value})" in stmt
                              for stmt in candidate["targetStatementTemplates"]),
                          "ComboBox parameter binding was not preserved")
                projections[method] = plan
            default_redacted = jrun(["dotnet", cli, "pom-plan", source_dir,
                                      "CatalogPage", "Choose", h], repo)
            check(default_redacted["targetStatements"] == []
                  and default_redacted["adapterCandidate"] is None
                  and all("targetStatement" not in action for action in default_redacted["actions"])
                  and "[data-test='option']" not in json.dumps(default_redacted)
                  and "[id=\\\"picker\\\"]" not in json.dumps(default_redacted),
                  "Default read-only Core CLI leaked target locator literals.")
            unsafe = run(["dotnet", cli, "pom-plan", source_dir,
                          "CatalogPage", "UnsafeDynamicWait", h, "--emit-target"], repo)
            check(unsafe["exit"] == 3 and
                  json.loads(unsafe["stdout"])["targetStatements"] == [],
                  "Unknown POM side effect was promoted to executable target.")
            all_statements = [statement for method in projections
                              for statement in projections[method]["targetStatements"]]
            check(len(all_statements) == 10,
                  "A navigation, combo, loader, table or assertion source effect disappeared.")
            check(hashlib.sha256(pom_file.read_bytes()).hexdigest() == pom_sha,
                  "POM plan modified input source.")

            source_tests = folder / "source-tests"
            target_tests = folder / "target-tests"
            source_tests.mkdir()
            target_tests.mkdir()
            (source_tests / "Synthetic.csproj").write_text(
                PACKAGES.format(playwright=""), encoding="utf-8")
            (source_tests / "Suite.cs").write_text(SOURCE_TESTS, encoding="utf-8")
            (target_tests / "Synthetic.csproj").write_text(
                PACKAGES.format(playwright='<PackageReference Include="Microsoft.Playwright" Version="1.63.0" />'),
                encoding="utf-8")
            source_open = projections["Open"]["targetStatements"]
            check(len(source_open) == 1 and "GotoAsync" in source_open[0],
                  "POM source navigation lost before conditional browser setup.")
            retry_statements = projections["RetryReady"]["targetStatements"]
            check(len(retry_statements) == 1
                  and "catch (System.TimeoutException)" in retry_statements[0]
                  and "if (attempt == 1) throw;" in retry_statements[0],
                  "Source-backed retry attempt/catch failure shape was lost.")
            target_body = TARGET_TEMPLATE.replace(
                "__OPEN_STATEMENTS__", "        " + source_open[0]).replace(
                "__RETRY_FAILURE_STATEMENTS__", "        " + retry_statements[0]).replace(
                "__PROJECTION_STATEMENTS__",
                "\n".join("        " + item
                           for item in all_statements if item not in source_open))
            (target_tests / "Suite.cs").write_text(target_body, encoding="utf-8")
            for dirname in (source_tests, target_tests):
                built = run(["dotnet", "build", "-c", "Release", "--nologo"], dirname)
                check(built["exit"] == 0, "Build failed " + str(dirname) + ": " + str(built))
            install_script = target_tests / "bin" / "Release" / "net10.0" / "playwright.ps1"
            check(install_script.exists(), "Playwright installer missing")
            browser = run(["pwsh", install_script, "install", "chromium"],
                          target_tests, timeout=540)
            check(browser["exit"] == 0, "Real Chromium install failed: " + str(browser))
            common = {"POM32_URL": url}
            trace = folder / "trace.txt"
            console = folder / "console.txt"
            screenshot = folder / "failure.png"
            target_env = {**common, "POM32_TRACE": str(trace),
                          "POM32_CONSOLE": str(console),
                          "POM32_SCREENSHOT": str(screenshot)}
            runs = {}
            for label, project, env in (
                ("source", source_tests, common),
                ("target", target_tests, target_env),
            ):
                runs[label + "_discovery"] = discovery(project, env)
                runs[label + "_default"] = test(project, label + "_default",
                                                 folder / "trx", env)
                runs[label + "_opt_in"] = test(project, label + "_opt_in",
                                                folder / "trx", env, "OptIn")
                runs[label + "_failure"] = test(project, label + "_failure",
                                                 folder / "trx", env, "DeliberateFailure")
                runs[label + "_retry_failure"] = test(project,
                                                  label + "_retry_failure",
                                                  folder / "trx", env, "RetryFailure")
                check(runs[label + "_default"]["exit"] == 0
                      and runs[label + "_opt_in"]["exit"] == 0
                      and runs[label + "_failure"]["exit"] != 0
                      and runs[label + "_retry_failure"]["exit"] != 0,
                      "Unexpected NUnit execution exit for " + label)
            check(runs["source_discovery"] == runs["target_discovery"],
                  "Original/target NUnit discovery names differ")
            for scenario in ("default", "opt_in", "failure", "retry_failure"):
                check(runs["source_" + scenario]["records"]
                      == runs["target_" + scenario]["records"],
                      "NUnit outcomes diverged: " + scenario + " "
                      + str(runs["source_" + scenario]["records"]) + " vs "
                      + str(runs["target_" + scenario]["records"]))
            expected = runs["target_default"]["records"]
            check(len(expected) == 6
                  and sum(v == "Passed" for v in expected.values()) == 2
                  and sum(v == "NotExecuted" for v in expected.values()) == 4,
                  "Default target incorrectly ran Ignore/Explicit or omitted cases.")
            check(screenshot.exists() and screenshot.read_bytes()[:8]
                  == b"\x89PNG\r\n\x1a\n", "Actual failure screenshot missing")
            check(console.exists() and "fictional-p31-console-ready"
                  in console.read_text(encoding="utf-8"), "Browser console event missing")
            events = trace.read_text(encoding="utf-8").splitlines()
            check(events.count("context-open") == 5
                  and events.count("context-close") == 5
                  and events.count("browser-open") == 4
                  and events.count("browser-close") == 4
                  and events.count("playwright-dispose") == 4
                  and events.count("screenshot-on-failure") == 2
                  and events.count("retry-terminal-timeout-type-confirmed") == 1,
                  "Browser context/fixture cleanup or failure screenshot count differs: " + str(events))
            for trx_file in (folder / "trx").glob("*.trx"):
                (evidence / trx_file.name).write_bytes(trx_file.read_bytes())
            (evidence / "synthetic-failure.png").write_bytes(screenshot.read_bytes())
            (evidence / "browser-console.txt").write_text(
                console.read_text(encoding="utf-8"), encoding="utf-8")
            summary = {
                "schema": "migrator-pom-preview32-runtime/v1",
                "source": "fictional_synthetic_no_customer_code",
                "projection_schema": "migrator-pom-action/v1alpha3",
                "source_tree_hash": h,
                "projector_status": "requires_review",
                "full_browser": "real_installed_chromium",
                "application": "loopback_http_only",
                "test_case_count": 6,
                "bounded_retry_literal_attempts": 2,
                "bounded_retry_wait_ms": 120,
                "typed_helper_nested_parameter_binding": True,
                "selected_retry_failure": "failed_expected",
                "real_chromium_timeout_exception_type": "System.TimeoutException_confirmed",
                "conditional_combo_success_branch": "alpha",
                "conditional_combo_else_branch": "beta",
                "guarded_dynamic_wait_ms": 1200,
                "default_passed": sum(v == "Passed" for v in expected.values()),
                "default_not_executed": sum(v == "NotExecuted" for v in expected.values()),
                "default_failed": sum(v == "Failed" for v in expected.values()),
                "source_test_runtime": "real_NUnit_httpclient_control_suite_not_selenium_browser",
                "target_test_runtime": "real_NUnit_Playwright_Chromium",
                "source_target_behavioral_equivalence": "not_verified",
                "private_customer_application": "not_run",
                "effects": [x["effect"] for p in projections.values()
                            for x in p["actions"]],
                "trace": events,
                "discovery_matches": True,
                "trx_outcomes_match": True,
                "source_sha256": pom_sha,
            }
            (evidence / "summary.json").write_text(
                json.dumps(summary, indent=2) + "\n", encoding="utf-8")
            print("POM_PREVIEW32_TYPED_RETRY_CHROMIUM_NUNIT_PASSED")
            print(json.dumps(summary))
        server.shutdown()


if __name__ == "__main__":
    main()
