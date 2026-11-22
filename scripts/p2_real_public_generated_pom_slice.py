#!/usr/bin/env python3
"""Run REAL public Selenium source against CORE-PROJECTED Playwright POM methods.

Deliberately not a whole-project acceptance: source NUnit lifecycle is not
executed, Core pom-plan creates review-only source-bound methods (not the
migrator-acceptance/v1 edited CandidateId), and no autoapply is authorized.
No upstream .csproj, build hooks, external website or third-party binaries.
"""
import hashlib
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import threading
from http.server import ThreadingHTTPServer

import p2_real_public_text_input_slice as baseline

OUTPUT = Path("artifacts/p2-generated-real-public-pom")
METHODS = (
    ("HomePage", "GoToTextInput", 1, ("click",)),
    ("TextInputPage", "EnterText", 2, ("input_clear", "input_sendkeys")),
    ("TextInputPage", "ClickButton", 1, ("click",)),
    ("TextInputPage", "GetButtonText", 1, ("rendered_text_return",)),
)

GENERATED = """
namespace Migrator.ExternalPOMSlice;
internal sealed class CoreProjectedHomePage
{
    private readonly Microsoft.Playwright.IPage Page;
    public CoreProjectedHomePage(Microsoft.Playwright.IPage page) { Page = page; }
    public async System.Threading.Tasks.Task GoToTextInput()
    {
__NAV__
    }
}
internal sealed class CoreProjectedPage
{
    private readonly Microsoft.Playwright.IPage Page;
    public CoreProjectedPage(Microsoft.Playwright.IPage page) { Page = page; }

    public async System.Threading.Tasks.Task EnterText(string text)
    {
__ENTER__
    }
    public async System.Threading.Tasks.Task ClickButton()
    {
__CLICK__
    }
    public async System.Threading.Tasks.Task<string> GetButtonText()
    {
__TEXT__
    }
}
"""

EXPECTED_EFFECTS = {
    ("HomePage", "GoToTextInput"): ("click",),
    ("TextInputPage", "EnterText"): ("input_clear", "input_sendkeys"),
    ("TextInputPage", "ClickButton"): ("click",),
    ("TextInputPage", "GetButtonText"): ("rendered_text_return",),
}


def require(condition, message):
    if not condition:
        raise RuntimeError("PUBLIC_CORE_PROJECTED_SLICE_FAIL_CLOSED: " + message)


def json_cli(cmd, cwd):
    response = baseline.invoke(cmd, cwd, timeout=120)
    require(response["exitCode"] == 0,
            "Core CLI exit was not a review-only successful plan: " +
            str(response))
    return json.loads(response["stdoutTail"].strip())


def project_methods(cli, root, repo):
    inventory = json_cli(("dotnet", str(cli), "pom-inspect", root), repo)
    tree = inventory["sourceTreeHash"]
    require(len(tree) == 64 and inventory["inspectedFiles"] == 2,
            "unexpected source tree or file count")
    plans = {}
    for owner, method, count, effects in METHODS:
        source_type = "CSE2522_Selenium_NUnit.Pages." + owner
        plan = json_cli(("dotnet", str(cli), "pom-plan", root,
                         source_type, method, tree, "--emit-target"), repo)
        actions = plan["actions"]
        rendered = plan["targetStatements"]
        require(plan["status"] == "requires_review" and
                plan["schemaVersion"] == "migrator-pom-action/v1alpha3" and
                plan["sourceTreeHash"] == tree and
                plan["sourceType"] == source_type and
                plan["sourceMethod"] == method and
                len(actions) == count and len(rendered) == count and
                plan["blockers"] == [] and
                tuple(a["effect"] for a in actions) == effects and
                all(a["requiresReview"] and
                    a["sourceSha256"] and len(a["sourceSha256"]) == 64 and
                    a["targetSha256"] and len(a["targetSha256"]) == 64 and
                    a["sourcePath"].endswith(owner + ".cs")
                    for a in actions) and
                plan["runtimeVerified"] is False and
                plan["canAutoApply"] is False and
                plan["adapterCandidate"]["requiresReview"] is True,
                "projector failed source/method/effects/review gate: " +
                source_type + "." + method + ": " + str(plan))
        require(len(plan["sourceMethodSha256"]) == 64 and
                plan["sourceLine"] > 0, "source method identity missing")
        plans[(owner, method)] = plan

    # Reject wrong action order or silent string return/no target emission.
    navigation = plans[("HomePage", "GoToTextInput")]["targetStatements"][0]
    enter = plans[("TextInputPage", "EnterText")]["targetStatements"]
    click = plans[("TextInputPage", "ClickButton")]["targetStatements"][0]
    text = plans[("TextInputPage", "GetButtonText")]["targetStatements"][0]
    require("Microsoft.Playwright.AriaRole.Link" in navigation and
            "Exact = true" in navigation and
            '"Text Input"' in navigation and "ClickAsync" in navigation and
            "ClearAsync" in enter[0] and "newButtonName" in enter[0] and
            "PressSequentiallyAsync(text)" in enter[1] and
            "newButtonName" in enter[1] and
            "updatingButton" in click and "ClickAsync" in click and
            "return await" in text and "InnerTextAsync" in text and
            "updatingButton" in text,
            "public source selector/argument/action/getter lineage incomplete")
    replacements = {
        "__NAV__": navigation,
        "__ENTER__": "\n".join(enter),
        "__CLICK__": click,
        "__TEXT__": text,
    }
    generated = GENERATED
    for key, value in replacements.items():
        require(generated.count(key) == 1 and
                "__" not in value and
                "driver" not in value and
                "OpenQA.Selenium" not in value,
                "generated method template unsafe or partly unconverted")
        generated = generated.replace(key, value)
    require("__" not in generated, "unsubstituted generated placeholder")
    return tree, plans, generated


def projected_harness():
    original = baseline.HARNESS
    first = "internal sealed class ManualReferencePage {"
    second = ("[TestFixture, NonParallelizable]\n"
              "public sealed class ManualPlaywrightReference")
    require(original.count(first) == 1 and original.count(second) == 1,
            "upstream owned harness composition changed")
    begin = original.index(first)
    end = original.index(second, begin)
    # Drop *all* handwritten target Page Object methods.
    test = original[:begin] + original[end:]
    test = test.replace("ManualPlaywrightReference", "CoreProjectedPlaywright")
    test = test.replace("ManualReferencePage", "CoreProjectedPage")
    test = test.replace("pom.ButtonText()", "pom.GetButtonText()")
    nav = ('      await page.GetByRole(AriaRole.Link, new PageGetByRoleOptions {\n'
           '        Name = "Text Input" }).ClickAsync();\n')
    require(test.count(nav) == 1, "handwritten target navigation not isolated")
    test = test.replace(nav, "")
    # ALL Playwright paths must use the source-projected navigation first,
    # including the NUnit-green missing-Click negative and selector mutant.
    original_pos = '      var pom = new CoreProjectedPage(page);'
    require(test.count(original_pos) == 1, "positive target fixture changed")
    test = test.replace(original_pos,
        '      await new CoreProjectedHomePage(page).GoToTextInput();\n'
        + original_pos)
    mutant_pos = '      await new CoreProjectedPage(page).EnterText(Evidence.Expected);'
    require(test.count(mutant_pos) == 1, "false-green fixture changed")
    test = test.replace(mutant_pos,
        '      await new CoreProjectedHomePage(page).GoToTextInput();\n'
        + mutant_pos)
    wrong_selector = '      await page.Locator("#wrong-newButtonName").FillAsync('
    require(test.count(wrong_selector) == 1,
            "source-selector negative fixture changed")
    test = test.replace(wrong_selector,
        '      await new CoreProjectedHomePage(page).GoToTextInput();\n'
        + wrong_selector)
    require("ManualReferencePage" not in test and
            "ManualPlaywrightReference" not in test,
            "handwritten target accidentally present")
    return test


def verify_mutant_closure(plans, checkout, cli, repo, source_root):
    # A source selector mutation MUST invalidate the captured source tree.
    # The old SHA cannot be reused to generate a stale target candidate.
    source_file = source_root / "TextInputPage.cs"
    original = source_file.read_bytes()
    require(original.count(b'By.Id("newButtonName")') == 1,
            "original pinned source selector count drifted")
    mutated = original.replace(b'By.Id("newButtonName")',
                               b'By.Id("mutated-input")')
    source_file.write_bytes(mutated)
    stale = baseline.invoke(("dotnet", str(cli), "pom-plan", source_root,
        "CSE2522_Selenium_NUnit.Pages.TextInputPage", "EnterText",
        plans[("TextInputPage", "EnterText")]["sourceTreeHash"],
        "--emit-target"), repo)
    require(stale["exitCode"] != 0 and
            "STALE_POM_ACTION_PLAN" in stale["stderrTail"],
            "source property selector changed but exact stale hash was accepted")
    source_file.write_bytes(original)


def main(external_checkout):
    checkout = Path(external_checkout).resolve()
    repo = Path(__file__).resolve().parent.parent
    evidence = repo / OUTPUT
    evidence.mkdir(parents=True, exist_ok=True)
    records = baseline.attest(checkout)
    cli = (repo / "Migrator.AssemblyCli" / "bin" / "Release"
           / "net10.0" / "Migrator.AssemblyCli.dll")
    require(cli.is_file(), "real Core CLI must be built before this oracle")
    require(Path(os.environ["MIGRATOR_CHROME_BIN"]).is_file() and
            Path(os.environ["MIGRATOR_CHROMEDRIVER_BIN"]).is_file(),
            "explicit identical Chrome/Driver binary paths missing")
    with tempfile.TemporaryDirectory(prefix="migrator-core-public-pom-") as tmp:
        temp = Path(tmp)
        pom = temp / "pom"
        pom.mkdir()
        for rel in baseline.SOURCE_FILES:
            shutil.copyfile(checkout / rel, pom / Path(rel).name)
        tree, plans, emitted = project_methods(cli, pom, repo)
        verify_mutant_closure(plans, checkout, cli, repo, pom)
        target = temp / "test"
        target.mkdir()
        for rel in baseline.SOURCE_FILES:
            shutil.copyfile(checkout / rel, target / Path(rel).name)
        (target / "Pilot.csproj").write_text(baseline.PROJECT, encoding="utf-8")
        (target / "CoreProjectedPage.cs").write_text(emitted, encoding="utf-8")
        harness = projected_harness()
        (target / "Pilot.cs").write_text(harness, encoding="utf-8")
        target_sha = baseline.sha(emitted.encode("utf-8"))
        with ThreadingHTTPServer(("127.0.0.1", 0), baseline.Handler) as server:
            threading.Thread(target=server.serve_forever, daemon=True).start()
            env = {"PUBLIC_POM_URL": "http://127.0.0.1:" + str(server.server_port),
                   "PUBLIC_POM_OUT": str(evidence)}
            build = baseline.invoke(("dotnet", "build", "Pilot.csproj", "-c",
                                     "Release", "--nologo"), target)
            require(build["exitCode"] == 0,
                    "genuine generated Playwright target did not compile: " +
                    str(build))
            expected = (
                ("OriginalPublicPOM", "TextInput", "Passed", True),
                ("CoreProjectedPlaywright", "TextInput", "Passed", True),
                ("MutantMissingClick", "FalseGreen", "Passed", True),
                ("MutantWrongSelector", "MustFail", "Failed", False),
            )
            observed = {}
            for fixture, test_name, outcome, expected_success in expected:
                filename = fixture + ".trx"
                ran = baseline.invoke(("dotnet", "test", "Pilot.csproj",
                    "-c", "Release", "--no-build", "--no-restore", "--nologo",
                    "--filter", "FullyQualifiedName~" + fixture,
                    "--logger", "trx;LogFileName=" + filename,
                    "--results-directory", evidence), target, timeout=180, env=env)
                require((ran["exitCode"] == 0) == expected_success,
                        fixture + " test runner outcome: " + str(ran))
                actual = baseline.trx(evidence / filename, test_name)
                require(actual == outcome,
                        fixture + " NUnit TRX: " + actual + " expected " + outcome)
                observed[fixture] = {
                    "outcome": actual,
                    "trxSha256": baseline.sha((evidence / filename).read_bytes()),
                }
            source = json.loads((evidence / "selenium.json").read_text("utf-8"))
            produced = json.loads((evidence / "playwright.json").read_text("utf-8"))
            missing = json.loads((evidence / "missing_click.json").read_text("utf-8"))
            proper = {"path": "/textinput", "input": baseline.TEXT,
                      "button": baseline.TEXT, "clicks": 1,
                      "events": ["page_ready",
                                 "button_updated:" + baseline.TEXT]}
            require(source == produced == proper,
                    "Core-projected target diverges from original public source")
            require(missing["clicks"] == 0 and missing != source and
                    missing["button"] != baseline.TEXT,
                    "independent false-green DOM mutant was not rejected")
            meta = {}
            for key, plan in plans.items():
                meta[".".join(key)] = {
                    "sourceMethodSha256": plan["sourceMethodSha256"],
                    "sourcePath": plan["sourcePath"],
                    "sourceLine": plan["sourceLine"],
                    "sourceTreeHash": plan["sourceTreeHash"],
                    "sourceActions": [
                        {name: action[name] for name in (
                            "order", "effect", "sourceSha256",
                            "sourceSymbol", "targetSha256",
                            "controlFlowPath", "requiresReview")}
                        for action in plan["actions"]],
                    "targetStatementSha256": [
                        baseline.sha(statement.encode("utf-8"))
                        for statement in plan["targetStatements"]],
                    "requiresReview": True,
                }
            files = sorted(p for p in evidence.iterdir()
                           if p.is_file() and p.name != "receipt.json")
            receipt = {
                "schema": "migrator-public-core-pom-projected-slice/v1",
                "repository": "desh2001/Selenium-NUnit-Automation",
                "sourceCommit": baseline.COMMIT, "sourceFiles": records,
                "sourceTreeHash": tree,
                "actualCorePomPlanProducedTarget": True,
                "generatedTargetSha256": target_sha,
                "generatedMethodEvidence": meta,
                "sourceNUnitRuntime": "passed_in_controlled_harness",
                "coreProjectedPlaywrightNUnit": "passed_in_controlled_harness",
                "boundedObservableParity": "passed",
                "staleSourceSelectorMutation": "rejected",
                "missingClickMutant": "nunit_pass_but_oracle_rejected",
                "wrongSelectorMutant": "nunit_fail",
                "originalUpstreamProjectBuild": "not_run",
                "originalNUnitLifecycle": "not_run",
                "fullPOMAndHelperGraph": "unknown",
                "fullRealProjectBehavioralEquivalence": "unknown",
                "migratorAcceptanceCandidateId": "not_created",
                "canAutoApply": False, "acceptedForRC": False,
                "externalWebsiteUsed": False,
                "executedThirdPartyProjectScripts": False,
                "results": observed,
                "evidence": [
                    {"file": p.name, "sha256": baseline.sha(p.read_bytes())}
                    for p in files],
            }
            (evidence / "receipt.json").write_text(
                json.dumps(receipt, sort_keys=True, indent=2) + "\n",
                encoding="utf-8")
            print("PUBLIC_CORE_PROJECTED_REAL_POM_BOUNDED_PARITY_PASSED")
            print(json.dumps({"sourceCommit": baseline.COMMIT,
                "sourceTreeHash": tree, "targetSha256": target_sha,
                "fullProject": "unknown", "release": "blocked"},
                sort_keys=True))


if __name__ == "__main__":
    require(len(sys.argv) == 2, "pinned public source checkout required")
    main(sys.argv[1])
