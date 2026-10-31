#!/usr/bin/env python3
"""Preview.33 public synthetic proof: source-attested POM templates and real
Playwright Chromium on a loopback app. The original NUnit suite uses HttpClient,
NOT Selenium WebDriver. Matching TRX is NOT Selenium/Playwright equivalence.
"""
import hashlib
import json
import os
from pathlib import Path
import tempfile
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from p2_typed_retry_source_target_chromium_runtime import (
    PACKAGES, check, discovery, jrun, run, test)

PAGE = """
using System;
using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
namespace Fictional.Preview33;
public sealed partial class CatalogPage {
    private readonly IWebDriver driver;
    public CatalogPage(IWebDriver driver) { this.driver = driver; }
    public void Choose(string search, string confirmation) {
        OpenSearchConfirm(search, confirmation);
    }
    public void WaitGone() {
        new WebDriverWait(driver, TimeSpan.FromMilliseconds(75))
            .Until(d => d.FindElements(By.Id("loader")).Count == 0);
    }
    public void RetryGone() {
        for (var attempt = 0; attempt < 2; attempt++) {
            try {
                new WebDriverWait(driver, TimeSpan.FromMilliseconds(75))
                    .Until(d => d.FindElements(By.Id("loader")).Count == 0);
                break;
            } catch (WebDriverTimeoutException) {
                if (attempt == 1) throw;
            }
        }
    }
    public void UnsafeHidden() {
        new WebDriverWait(driver, TimeSpan.FromMilliseconds(75))
            .Until(d => !d.FindElement(By.Id("loader")).Displayed);
    }
}
"""
HELPERS = """
using OpenQA.Selenium;
using NUnit.Framework;
namespace Fictional.Preview33;
public sealed partial class CatalogPage {
    private void OpenSearchConfirm(string a, string b) {
        driver.FindElement(By.Id("picker")).Click();
        driver.FindElement(By.Id("search")).Clear();
        driver.FindElement(By.Id("search")).SendKeys(a);
        if (b == "yes") {
            driver.FindElement(By.Id("accept")).Click();
        } else {
            Assert.That(driver.FindElements(By.Id("accept")).Count,
                Is.GreaterThan(0));
        }
    }
}
"""
HTML = """<!doctype html><html><head><meta charset="utf-8"></head><body>
<button id="picker">Open</button><input id="search"/>
<button id="accept" onclick="document.getElementById('accepted').textContent=document.getElementById('search').value">Accept</button>
<div id="accepted"></div><div id="loader">pending</div>
<script>console.log('fictional-preview33-ready');</script>
</body></html>"""

SOURCE_TESTS = """
using System;
using System.Net.Http;
using System.Threading.Tasks;
using NUnit.Framework;
namespace Fictional.Preview33;
[TestFixture] public class CatalogSuite {
    [TestCase("alpha", "yes")] [TestCase("beta", "no")]
    public async Task Choose(string search, string confirmation) {
        using var client = new HttpClient();
        string html = await client.GetStringAsync(
            Environment.GetEnvironmentVariable("POM33_URL")!);
        Assert.That(html.Contains("id=\\"search\\""), Is.True);
        Assert.That(search.Length, Is.GreaterThan(0));
        Assert.That(confirmation.Length, Is.GreaterThan(0));
    }
    [Test] public async Task LoaderGone() {
        using var client = new HttpClient();
        string html = await client.GetStringAsync(
            Environment.GetEnvironmentVariable("POM33_URL")!);
        Assert.That(html.Contains("id=\\"loader\\""), Is.True);
    }
    [Test] [Ignore("synthetic disabled")] public void Disabled() {
        Assert.Fail("disabled should not execute");
    }
    [Test] [Explicit("synthetic success")] public void OptIn() {
        Assert.That(true, Is.True);
    }
    [Test] [Explicit("synthetic timeout failure")]
    public void LoaderFailure() {
        Assert.Fail("synthetic expected source-side failure");
    }
}
"""

TARGET = r"""
using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Playwright;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
namespace Fictional.Preview33;
[TestFixture] [NonParallelizable] public class CatalogSuite {
    private IPlaywright? pw;
    private IBrowser? browser;
    private IBrowserContext? context;
    private IPage? current;
    private IPage Page => current!;
    private static void Record(string value) {
        var file = Environment.GetEnvironmentVariable("POM33_TRACE");
        if (file != null) File.AppendAllText(file, value + "\n");
    }
    [OneTimeSetUp] public async Task Start() {
        pw = await Playwright.CreateAsync();
        browser = await pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions {
            Headless = true
        });
        Record("browser-open");
    }
    [SetUp] public async Task Setup() {
        context = await browser!.NewContextAsync();
        current = await context.NewPageAsync();
        current.Console += (_, m) => {
            var file = Environment.GetEnvironmentVariable("POM33_CONSOLE");
            if (file != null) File.AppendAllText(file, m.Text + "\n");
        };
        Record("context-open");
    }
    [TearDown] public async Task Cleanup() {
        if (TestContext.CurrentContext.Result.Outcome.Status == TestStatus.Failed) {
            var path = Environment.GetEnvironmentVariable("POM33_SCREENSHOT");
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
    [OneTimeTearDown] public async Task End() {
        if (browser != null) {
            await browser.CloseAsync();
            Record("browser-close");
        }
        pw?.Dispose();
        Record("playwright-dispose");
    }
    [TestCase("alpha", "yes")] [TestCase("beta", "no")]
    public async Task Choose(string search, string confirmation) {
        await Page.GotoAsync(Environment.GetEnvironmentVariable("POM33_URL")!);
__CHOOSE__
        Assert.That(await Page.Locator("#search").InputValueAsync(),
            Is.EqualTo(search));
        if (confirmation == "yes") {
            Assert.That(await Page.Locator("#accepted").InnerTextAsync(),
                Is.EqualTo(search));
        } else {
            Assert.That(await Page.Locator("#accepted").InnerTextAsync(),
                Is.EqualTo(""));
        }
        Record("combo-verified-" + confirmation);
    }
    [Test] public async Task LoaderGone() {
        await Page.GotoAsync(Environment.GetEnvironmentVariable("POM33_URL")!);
        await Page.Locator("#loader").EvaluateAsync("el => el.remove()");
__GONE__
        Assert.That(await Page.Locator("#loader").CountAsync(), Is.EqualTo(0));
        Record("detached-confirmed");
    }
    [Test] [Ignore("synthetic disabled")] public void Disabled() {
        Assert.Fail("disabled should not execute");
    }
    [Test] [Explicit("synthetic success")] public void OptIn() {
        Assert.That(true, Is.True);
    }
    [Test] [Explicit("synthetic timeout failure")]
    public async Task LoaderFailure() {
        await Page.GotoAsync(Environment.GetEnvironmentVariable("POM33_URL")!);
        try {
__RETRY__
            Assert.Fail("retry incorrectly passed with loader still present");
        } catch (System.TimeoutException) {
            Record("terminal-timeout-System.TimeoutException");
            throw;
        }
    }
}
"""

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
    check(cli.is_file(), "Build the assembly CLI before preview33 runtime")
    evidence = repo / "artifacts" / "pom-preview33"
    evidence.mkdir(parents=True, exist_ok=True)
    with ThreadingHTTPServer(("127.0.0.1", 0), Handler) as server:
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        url = "http://127.0.0.1:" + str(server.server_port) + "/"
        with tempfile.TemporaryDirectory(prefix="fictional-pom-preview33-") as tmp:
            root = Path(tmp)
            source = root / "pom"
            source.mkdir()
            (source / "Page.cs").write_text(PAGE, encoding="utf-8")
            (source / "Helpers.cs").write_text(HELPERS, encoding="utf-8")
            sources_sha = {
                file.name: hashlib.sha256(file.read_bytes()).hexdigest()
                for file in source.glob("*.cs")
            }
            snapshot = jrun(["dotnet", cli, "pom-inspect", source], repo)
            sha = snapshot["sourceTreeHash"]
            projections = {}
            for method in ("Choose", "WaitGone", "RetryGone"):
                p = jrun(["dotnet", cli, "pom-plan", source,
                          "Fictional.Preview33.CatalogPage", method, sha,
                          "--emit-target"], repo)
                check(p["status"] == "requires_review"
                      and p["targetStatements"]
                      and p["adapterCandidate"]["requiresReview"]
                      and p["sourceMethodSha256"], method + " not source-backed")
                projections[method] = p
            check(len(projections["Choose"]["targetStatements"]) == 1
                  and "PressSequentiallyAsync(search)" in
                  projections["Choose"]["targetStatements"][0]
                  and "if (confirmation == \"yes\")" in
                  projections["Choose"]["targetStatements"][0],
                  "Multi-argument ComboBox parameter flow lost.")
            check(any(a["effect"] == "detached_wait" for a in
                      projections["WaitGone"]["actions"])
                  and "WaitForSelectorState.Detached" in
                  projections["WaitGone"]["targetStatements"][0],
                  "Loader removal is not an exact source-backed DOM absence.")
            check("catch (System.TimeoutException)" in
                  projections["RetryGone"]["targetStatements"][0],
                  "Finite retry exception path lost.")
            unsafe = run(["dotnet", cli, "pom-plan", source,
                          "CatalogPage", "UnsafeHidden", sha,
                          "--emit-target"], repo)
            check(unsafe["exit"] == 3
                  and json.loads(unsafe["stdout"])["targetStatements"] == [],
                  "Hidden-but-present was silently mapped to Detached.")
            redacted = jrun(["dotnet", cli, "pom-plan", source,
                              "CatalogPage", "Choose", sha], repo)
            check(not redacted["targetStatements"]
                  and redacted["adapterCandidate"] is None,
                  "Default Core inspection leaked the candidate.")
            check(sources_sha == {
                p.name: hashlib.sha256(p.read_bytes()).hexdigest()
                for p in source.glob("*.cs")}, "Source files were modified.")

            original, target = root / "original", root / "target"
            original.mkdir()
            target.mkdir()
            (original / "Suite.cs").write_text(SOURCE_TESTS, encoding="utf-8")
            (original / "Synthetic.csproj").write_text(
                PACKAGES.format(playwright=""), encoding="utf-8")
            body = TARGET
            for token, method in (("__CHOOSE__", "Choose"),
                                  ("__GONE__", "WaitGone"),
                                  ("__RETRY__", "RetryGone")):
                body = body.replace(token, "        " +
                    projections[method]["targetStatements"][0])
            (target / "Suite.cs").write_text(body, encoding="utf-8")
            (target / "Synthetic.csproj").write_text(PACKAGES.format(
                playwright='<PackageReference Include="Microsoft.Playwright" Version="1.63.0" />'),
                encoding="utf-8")
            for project in (original, target):
                r = run(["dotnet", "build", "-c", "Release", "--nologo"], project)
                check(r["exit"] == 0, "Target/source failed build: " + str(r))
            installer = target / "bin" / "Release" / "net10.0" / "playwright.ps1"
            check(installer.is_file(), "Playwright browser installer missing.")
            browser = run(["pwsh", installer, "install", "chromium"],
                          target, timeout=540)
            check(browser["exit"] == 0, "Real Chromium installation failed: " + str(browser))
            trace, console, screenshot = (root / "trace.log",
                                          root / "console.log",
                                          root / "failure.png")
            environment = {"POM33_URL": url}
            target_env = {**environment, "POM33_TRACE": str(trace),
                          "POM33_CONSOLE": str(console),
                          "POM33_SCREENSHOT": str(screenshot)}
            results = {}
            for name, project, env in (
                ("source", original, environment),
                ("target", target, target_env)):
                results[name + "_discovery"] = discovery(project, env)
                for scenario, selected in (("default", None),
                                           ("opt_in", "OptIn"),
                                           ("failure", "LoaderFailure")):
                    results[name + "_" + scenario] = test(
                        project, name + "_" + scenario, root / "trx", env, selected)
                check(results[name + "_default"]["exit"] == 0
                      and results[name + "_opt_in"]["exit"] == 0
                      and results[name + "_failure"]["exit"] != 0,
                      name + " unexpected NUnit exit code")
            check(results["source_discovery"] == results["target_discovery"],
                  "Source/target NUnit discovery diverged.")
            for scenario in ("default", "opt_in", "failure"):
                check(results["source_" + scenario]["records"]
                      == results["target_" + scenario]["records"],
                      "NUnit TRX outcomes diverged for " + scenario)
            default = results["target_default"]["records"]
            check(len(default) == 6
                  and list(default.values()).count("Passed") == 3
                  and list(default.values()).count("NotExecuted") == 3,
                  "NUnit default count or Ignore/Explicit changed.")
            check(screenshot.is_file() and screenshot.read_bytes()[:8]
                  == b"\x89PNG\r\n\x1a\n", "Actual browser failure PNG missing")
            check(console.is_file() and "fictional-preview33-ready"
                  in console.read_text(encoding="utf-8"), "Chromium console missing.")
            events = trace.read_text(encoding="utf-8").splitlines()
            check(events.count("context-open") == 5
                  and events.count("context-close") == 5
                  and events.count("browser-open") == 3
                  and events.count("browser-close") == 3
                  and events.count("playwright-dispose") == 3
                  and events.count("screenshot-on-failure") == 1
                  and events.count("terminal-timeout-System.TimeoutException") == 1
                  and events.count("detached-confirmed") == 1
                  and events.count("combo-verified-yes") == 1
                  and events.count("combo-verified-no") == 1,
                  "Unexpected browser/timeout/retry observable trace: " + str(events))
            for trx_file in (root / "trx").glob("*.trx"):
                (evidence / trx_file.name).write_bytes(trx_file.read_bytes())
            (evidence / "browser-console.log").write_bytes(console.read_bytes())
            (evidence / "failure.png").write_bytes(screenshot.read_bytes())
            summary = {
                "schema": "migrator-pom-preview33-runtime/v1",
                "source_tree_hash": sha,
                "source_sha256": sources_sha,
                "source_fixture": "NUnit HttpClient control, NOT Selenium browser",
                "source_target_behavioral_equivalence": "not_verified",
                "target_fixture": "NUnit real Playwright Chromium loopback",
                "nunit_case_count": 6,
                "default_passed": 3,
                "default_not_executed": 3,
                "discovery_trx_outcomes_match": True,
                "detached_loader_success": True,
                "combo_two_argument_both_branches": True,
                "terminal_system_timeout_exception_runtime_observed": True,
                "unknown_and_hidden_predicates_blocked": True,
                "review_required_not_autoapplied": True,
                "private_ArBilling": "not_run",
                "trace": events
            }
            (evidence / "summary.json").write_text(
                json.dumps(summary, indent=2) + "\n", encoding="utf-8")
            manifest = {
                p.name: hashlib.sha256(p.read_bytes()).hexdigest()
                for p in sorted(evidence.glob("*")) if p.is_file()
            }
            (evidence / "SHA256SUMS.json").write_text(
                json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
            print("POM_PREVIEW33_REAL_CHROMIUM_NUNIT_PASSED")
            print(json.dumps(summary))
        server.shutdown()

if __name__ == "__main__":
    main()
