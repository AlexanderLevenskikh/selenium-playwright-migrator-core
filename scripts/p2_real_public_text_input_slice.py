#!/usr/bin/env python3
"""Bounded REAL public-source POM browser slice, never a product-wide PASS.

Only the exact pinned external TextInputPage and HomePage .cs files are
compiled, inside our self-authored ephemeral project. No external project
script, project file, external test URL or binary is executed. Playwright is
a MANUAL reference implementation, not a Migrator-generated candidate.
"""
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import threading
import xml.etree.ElementTree as ET

COMMIT = "edacb9feb41a7e21d9b6bf6924f24c11698a4427"
FILES = {
    "AssignmentProject/Tests/TextInputTests.cs":
        "92592a1746b876956ab98506c7dcb8818f479b9a",
    "AssignmentProject/Pages/TextInputPage.cs":
        "2aec530bd9dbbacf051a4c19b7cda3fda1ec0a00",
    "AssignmentProject/Pages/HomePage.cs":
        "c69b3afb253db994cdab978207f27285979b4eae",
}
SOURCE_FILES = ("AssignmentProject/Pages/TextInputPage.cs",
                "AssignmentProject/Pages/HomePage.cs")
OUTPUT = Path("artifacts/p2-real-public-pom-slice")
TEXT = "Hello CSE2522"
PROJECT = """<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework>
<Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>
<IsTestProject>true</IsTestProject><IsPackable>false</IsPackable>
</PropertyGroup><ItemGroup>
<PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
<PackageReference Include="NUnit" Version="4.3.2" />
<PackageReference Include="NUnit3TestAdapter" Version="5.1.0" />
<PackageReference Include="Selenium.WebDriver" Version="4.33.0" />
<PackageReference Include="Microsoft.Playwright" Version="1.63.0" />
</ItemGroup></Project>"""

HARNESS = r"""
using System;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using Microsoft.Playwright;
using CSE2522_Selenium_NUnit.Pages;
namespace Migrator.ExternalPOMSlice;
public static class Evidence {
  public const string Expected = "Hello CSE2522";
  public static string Url => Environment.GetEnvironmentVariable("PUBLIC_POM_URL")!;
  public static string Bin => Environment.GetEnvironmentVariable("MIGRATOR_CHROME_BIN")!;
  public static string Dir => Environment.GetEnvironmentVariable("PUBLIC_POM_OUT")!;
  public static string Script = "return JSON.stringify(window.snapshot())";
  public static void Save(string key, string snapshot) =>
    File.WriteAllText(Path.Combine(Dir, key + ".json"), snapshot);
  public static (IWebDriver, ChromeDriver) StartSelenium() {
    var driverBin = Environment.GetEnvironmentVariable("MIGRATOR_CHROMEDRIVER_BIN")!;
    var svc = ChromeDriverService.CreateDefaultService(
      Path.GetDirectoryName(driverBin)!, Path.GetFileName(driverBin));
    svc.HideCommandPromptWindow = true;
    var options = new ChromeOptions { BinaryLocation = Bin };
    options.AddArgument("--headless=new");
    options.AddArgument("--no-sandbox");
    options.AddArgument("--disable-dev-shm-usage");
    var driver = new ChromeDriver(svc, options);
    return (driver, driver);
  }
  public static async Task<(IPlaywright, IBrowser, IPage)> StartPlaywright() {
    var pw = await Microsoft.Playwright.Playwright.CreateAsync();
    try {
      var browser = await pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions {
        ExecutablePath = Bin, Headless = true,
        Args = new[] { "--no-sandbox", "--disable-dev-shm-usage" } });
      var page = await browser.NewPageAsync();
      await page.GotoAsync(Url + "/");
      await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions {
        Name = "Text Input" }).ClickAsync();
      return (pw, browser, page);
    } catch { pw.Dispose(); throw; }
  }
}
[TestFixture, NonParallelizable]
public sealed class OriginalPublicPOM {
  [Test] public void TextInput() {
    var (_, driver) = Evidence.StartSelenium();
    try {
      driver.Navigate().GoToUrl(Evidence.Url + "/");
      // These are ORIGINAL, SHA-pinned public C# source classes, not a copy.
      new HomePage(driver).GoToTextInput();
      var pom = new TextInputPage(driver);
      pom.EnterText(Evidence.Expected);
      pom.ClickButton();
      Assert.That(pom.GetButtonText(), Is.EqualTo(Evidence.Expected));
      Evidence.Save("selenium", (string)((IJavaScriptExecutor)driver)
        .ExecuteScript(Evidence.Script)!);
    } finally { driver.Quit(); driver.Dispose(); }
  }
}
// Deliberately handwritten comparison adapter. Core has NOT generated this.
internal sealed class ManualReferencePage {
  private readonly IPage page;
  public ManualReferencePage(IPage p) { page = p; }
  public async Task EnterText(string value) {
    await page.Locator("#newButtonName").FillAsync("");
    await page.Locator("#newButtonName").FillAsync(value);
  }
  public Task ClickButton() => page.Locator("#updatingButton").ClickAsync();
  public Task<string> ButtonText() => page.Locator("#updatingButton").InnerTextAsync();
}
[TestFixture, NonParallelizable]
public sealed class ManualPlaywrightReference {
  [Test] public async Task TextInput() {
    var (pw, browser, page) = await Evidence.StartPlaywright();
    try {
      var pom = new ManualReferencePage(page);
      await pom.EnterText(Evidence.Expected);
      await pom.ClickButton();
      Assert.That(await pom.ButtonText(), Is.EqualTo(Evidence.Expected));
      Evidence.Save("playwright", await page.EvaluateAsync<string>(
        "() => JSON.stringify(window.snapshot())"));
    } finally { await browser.CloseAsync(); pw.Dispose(); }
  }
}
[TestFixture, NonParallelizable]
public sealed class MutantMissingClick {
  // A plain NUnit assertion passes even when the source Click was removed.
  [Test] public async Task FalseGreen() {
    var (pw, browser, page) = await Evidence.StartPlaywright();
    try {
      await new ManualReferencePage(page).EnterText(Evidence.Expected);
      Assert.That(true, Is.True);
      Evidence.Save("missing_click", await page.EvaluateAsync<string>(
        "() => JSON.stringify(window.snapshot())"));
    } finally { await browser.CloseAsync(); pw.Dispose(); }
  }
}
[TestFixture, NonParallelizable]
public sealed class MutantWrongSelector {
  [Test] public async Task MustFail() {
    var (pw, browser, page) = await Evidence.StartPlaywright();
    try {
      await page.Locator("#wrong-newButtonName").FillAsync(Evidence.Expected,
        new LocatorFillOptions { Timeout = 1500 });
      Assert.Fail("Wrong selector was not rejected");
    } finally { await browser.CloseAsync(); pw.Dispose(); }
  }
}
"""

PAGE = """<!doctype html><html lang="en"><meta charset="utf-8">
<body><h1>Text Input</h1>
<input id="newButtonName" value="">
<button id="updatingButton" type="button">Default Button</button>
<script>
window.events=["page_ready"];window.clickCount=0;
const input=document.querySelector("#newButtonName");
const button=document.querySelector("#updatingButton");
button.onclick=()=>{window.clickCount++;
  button.textContent=input.value;
  window.events.push("button_updated:"+input.value);};
window.snapshot=()=>({path:location.pathname,input:input.value,
  button:button.textContent,clicks:window.clickCount,events:window.events.slice()});
</script></body></html>"""
INDEX = """<!doctype html><html><body>
<a href="/textinput">Text Input</a></body></html>"""


def demand(ok, message):
    if not ok:
        raise AssertionError("PUBLIC_REAL_SLICE_FAIL_CLOSED: " + message)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def invoke(args, cwd, timeout=240, env=None):
    result = subprocess.run([str(v) for v in args], cwd=str(cwd),
        capture_output=True, text=True, timeout=timeout,
        env=dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", **(env or {})))
    return {"exitCode": result.returncode,
            "stdoutTail": result.stdout[-10000:],
            "stderrTail": result.stderr[-5000:]}


def git(root, *args):
    result = subprocess.run(["git", "-C", str(root), *args],
        capture_output=True, timeout=30)
    demand(result.returncode == 0, "pinned Git metadata command failed")
    return result.stdout


def attest(checkout):
    demand(checkout.is_dir() and not checkout.is_symlink(), "checkout missing")
    demand(git(checkout, "rev-parse", "HEAD").strip().decode() == COMMIT,
           "wrong external source commit")
    records = []
    for rel, expected_blob in sorted(FILES.items()):
        file = checkout / rel
        demand(file.is_file() and not file.is_symlink(), "source slice missing")
        canonical = git(checkout, "cat-file", "blob", expected_blob)
        digest = git(checkout, "rev-parse", "HEAD:" + rel).strip().decode()
        demand(digest == expected_blob, "external Git blob ref changed")
        checkout_bytes = file.read_bytes()
        demand(len(canonical) < 1024 * 1024 and
               checkout_bytes.replace(b"\r\n", b"\n") ==
               canonical.replace(b"\r\n", b"\n"),
               "pinned external bytes mutated beyond EOL")
        records.append({"path": rel, "gitBlobSha1": expected_blob,
                        "canonicalSha256": sha(canonical),
                        "checkoutSha256": sha(checkout_bytes)})
    return records


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path == "/":
            payload = INDEX.encode("utf-8")
        elif self.path == "/textinput":
            payload = PAGE.encode("utf-8")
        else:
            self.send_error(404)
            return
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def log_message(self, *_args):
        pass


def trx(result_path, test_name):
    demand(result_path.is_file(), "missing real NUnit TRX")
    results = [node.attrib for node in ET.parse(result_path).getroot().iter()
               if node.tag.endswith("UnitTestResult")]
    demand(len(results) == 1 and results[0].get("testName") == test_name,
           "unexpected NUnit test discovery or result: " + str(results))
    return results[0]["outcome"]


def main(checkout):
    repo = Path(__file__).resolve().parent.parent
    checkout = Path(checkout).resolve()
    chrome = Path(os.environ["MIGRATOR_CHROME_BIN"])
    chromedriver = Path(os.environ["MIGRATOR_CHROMEDRIVER_BIN"])
    demand(chrome.is_file() and chromedriver.is_file(),
           "explicit Chrome/ChromeDriver binaries missing")
    records = attest(checkout)
    out = repo / OUTPUT
    out.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="migrator-public-pom-slice-") as tmp:
        testroot = Path(tmp)
        for rel in SOURCE_FILES:
            shutil.copyfile(checkout / rel, testroot / Path(rel).name)
        (testroot / "Pilot.csproj").write_text(PROJECT, encoding="utf-8")
        (testroot / "Pilot.cs").write_text(HARNESS, encoding="utf-8")
        with ThreadingHTTPServer(("127.0.0.1", 0), Handler) as server:
            threading.Thread(target=server.serve_forever, daemon=True).start()
            base = "http://127.0.0.1:" + str(server.server_port)
            runtime_env = {"PUBLIC_POM_URL": base,
                           "PUBLIC_POM_OUT": str(out)}
            build = invoke(["dotnet", "build", "Pilot.csproj", "-c", "Release",
                            "--nologo"], testroot)
            demand(build["exitCode"] == 0, "controlled harness build failed: " +
                   str(build))
            cases = (
                ("OriginalPublicPOM", "TextInput", "Passed", 0),
                ("ManualPlaywrightReference", "TextInput", "Passed", 0),
                ("MutantMissingClick", "FalseGreen", "Passed", 0),
                ("MutantWrongSelector", "MustFail", "Failed", 1),
            )
            seen = {}
            for fixture, method, expected, exit_status in cases:
                name = fixture + ".trx"
                ran = invoke(["dotnet", "test", "Pilot.csproj", "-c", "Release",
                    "--no-build", "--no-restore", "--nologo", "--filter",
                    "FullyQualifiedName~" + fixture,
                    "--logger", "trx;LogFileName=" + name,
                    "--results-directory", str(out)], testroot, timeout=180,
                    env=runtime_env)
                expected_return = ran["exitCode"] == 0 if exit_status == 0 else ran["exitCode"] != 0
                demand(expected_return, fixture + " runner exit incorrect: " +
                       str(ran))
                outcome = trx(out / name, method)
                demand(outcome == expected, fixture + " TRX: " + outcome)
                seen[fixture] = {"outcome": outcome,
                                 "trxSha256": sha((out / name).read_bytes())}
            source = json.loads((out / "selenium.json").read_text("utf-8"))
            target = json.loads((out / "playwright.json").read_text("utf-8"))
            mutant = json.loads((out / "missing_click.json").read_text("utf-8"))
            expected = {"path": "/textinput", "input": TEXT,
                        "button": TEXT, "clicks": 1,
                        "events": ["page_ready", "button_updated:" + TEXT]}
            demand(source == target == expected,
                   "source and handwritten target actual DOM/ordered effects diverge")
            demand(mutant != source and mutant["clicks"] == 0 and
                   mutant["button"] != TEXT, "NUnit false green escaped oracle")
            files = sorted(p for p in out.iterdir() if p.is_file() and
                           p.name != "receipt.json")
            receipt = {
                "schema": "migrator-public-pom-real-slice/v1",
                "source": "desh2001/Selenium-NUnit-Automation",
                "commit": COMMIT, "sourceFiles": records,
                "compiledExternalScripts": False,
                "executedExternalProjectBuild": False,
                "externalWebsiteUsed": False,
                "browserServer": "127.0.0.1-only",
                "sourcePomRuntime": "passed",
                "handwrittenPlaywrightReferenceRuntime": "passed",
                "boundedObservableParity": "passed",
                "negativeMissingClickRunner": "passed_but_oracle_rejected",
                "negativeWrongSelectorRunner": "failed_as_expected",
                "originalProjectLifecycle": "not_run",
                "migratorGeneratedCandidate": "not_run",
                "fullRealProjectBehavioralEquivalence": "unknown",
                "canAutoApply": False, "acceptedForRC": False,
                "targetType": "manual_reference_not_product_generated",
                "manualReferenceSha256": sha(HARNESS.encode("utf-8")),
                "results": seen,
                "evidence": [{"file": p.name, "sha256": sha(p.read_bytes())}
                             for p in files],
            }
            (out / "receipt.json").write_text(
                json.dumps(receipt, indent=2, sort_keys=True) + "\n",
                encoding="utf-8")
            print("PUBLIC_REAL_SOURCE_POM_SLICE_PASSED_NOT_FULL_PROJECT")
            print(json.dumps({"source": COMMIT, "equivalence": "bounded_only",
                              "fullProject": "unknown"}, sort_keys=True))


if __name__ == "__main__":
    demand(len(sys.argv) == 2, "pinned checkout path required")
    main(sys.argv[1])
