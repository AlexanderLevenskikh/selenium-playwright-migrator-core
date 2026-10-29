#!/usr/bin/env python3
"""Only invented NUnit fixtures. Compare source and generated NUnit discovery/runtime.

Static Roslyn inventory is NOT NUnit discovery. All subprocesses are explicit,
the synthetic failure is expected, and TRX is retained as evidence. No ArBilling.
"""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

PACKAGE = {
    "nunit3": ("3.14.0", "4.5.0"),
    "nunit4": ("4.3.2", "5.1.0"),
}
SUITE = r"""
using NUnit.Framework;
[TestFixture]
public class GateFixture
{
    private static readonly object[][] Rows = new object[][] {
        new object[] { 5 }, new object[] { 6 }
    };

    [Test] public void Ordinary() { Assert.That(1, Is.EqualTo(1)); }
    [Test] [Ignore("synthetic maintenance")] public void Ignored()
        { Assert.That(false, Is.True); }
    [Test] [Explicit("synthetic opt-in")] public void OptIn()
        { Assert.That(true, Is.True); }
    [TestCase(1)] [TestCase(2)]
    public void Inline(int value) { Assert.That(value > 0, Is.True); }
    [TestCase(3, Ignore = "row maintenance")]
    [TestCase(4, Explicit = true)]
    public void GatedInline(int value) { Assert.That(value > 0, Is.True); }
    [TestCaseSource(nameof(Rows))]
    public void SourceRows(int value) { Assert.That(value > 0, Is.True); }
    [Test] [Explicit("expected failure when selected")]
    public void Crash() { Assert.That(false, Is.True); }
}
"""


def check(ok, reason):
    if not ok:
        raise AssertionError(reason)


def run(args, cwd, timeout=360):
    proc = subprocess.run(args, cwd=str(cwd), capture_output=True,
                          text=True, timeout=timeout,
                          env=dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1"))
    return {"exit_code": proc.returncode,
            "stdout_tail": proc.stdout[-25000:],
            "stderr_tail": proc.stderr[-2000:]}


def project(nunit, adapter):
    return f"""<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup>
<TargetFramework>net10.0</TargetFramework>
<IsTestProject>true</IsTestProject>
<IsPackable>false</IsPackable>
<ImplicitUsings>enable</ImplicitUsings>
</PropertyGroup>
<ItemGroup>
<PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
<PackageReference Include="NUnit" Version="{nunit}" />
<PackageReference Include="NUnit3TestAdapter" Version="{adapter}" />
</ItemGroup>
</Project>"""


def trx(path):
    if not path.exists():
        raise AssertionError("NUnit did not produce TRX: " + str(path))
    root = ET.parse(path).getroot()
    counters = next((n.attrib for n in root.iter()
                     if n.tag.endswith("Counters")), None)
    check(counters is not None, "TRX counters absent")
    cases = {}
    details = {}
    for node in root.iter():
        if not node.tag.endswith("UnitTestResult"):
            continue
        key = node.get("testName")
        check(key and key not in cases, "Duplicate/unnamed NUnit case in TRX: " + str(key))
        cases[key] = node.get("outcome")
        details[key] = "".join(node.itertext())[:1000]
    return {"counters": {k: int(counters.get(k, "0")) for k in
                         ("total", "passed", "failed", "notExecuted")},
            "cases": cases, "details": details}


def run_tests(directory, label, arguments=None):
    arguments = arguments or []
    results = directory / "TestResults"
    results.mkdir(exist_ok=True)
    name = label + ".trx"
    invocation = run(["dotnet", "test", "-c", "Release", "--no-restore",
                      "--logger", "trx;LogFileName=" + name,
                      "--results-directory", str(results), "--nologo",
                      *arguments], directory)
    return {"process": invocation, "trx": trx(results / name)}


def list_tests(directory):
    p = run(["dotnet", "test", "--list-tests", "-c", "Release",
             "--no-restore", "--nologo"], directory)
    check(p["exit_code"] == 0, "NUnit discovery failed: " + str(p))
    lines = p["stdout_tail"].splitlines()
    marker = next((i for i, line in enumerate(lines)
                   if "following tests are available" in line.lower()), None)
    check(marker is not None, "NUnit discovery list missing: " + str(p))
    names = [line.strip() for line in lines[marker + 1:]
             if line.strip() and not line.startswith("Test Run")]
    check(names, "NUnit discovery returned no names")
    return sorted(names)


def compare(left, right, scenario):
    a, b = left["trx"]["cases"], right["trx"]["cases"]
    check(a == b, "NUnit TRX mismatch for " + scenario + ": "
          + json.dumps({"source": a, "target": b}, sort_keys=True))
    ac, bc = left["trx"]["counters"], right["trx"]["counters"]
    check(ac == bc, "NUnit TRX counter mismatch for " + scenario + ": "
          + str((ac, bc)))
    check(left["process"]["exit_code"] == right["process"]["exit_code"],
          "NUnit exit code mismatch for " + scenario)
    return {"source": left, "target": right}


def main():
    root_repo = Path(__file__).resolve().parent.parent
    evidence = root_repo / "artifacts" / "nunit-preview29"
    evidence.mkdir(parents=True, exist_ok=True)
    observations = {}
    with tempfile.TemporaryDirectory(prefix="fictional-nunit-preview29-") as temp:
        work = Path(temp)
        projects = {}
        for name, (nunit, adapter) in PACKAGE.items():
            directory = work / name
            directory.mkdir()
            (directory / "Suite.cs").write_text(SUITE, encoding="utf-8")
            (directory / "Synthetic.csproj").write_text(
                project(nunit, adapter), encoding="utf-8")
            build = run(["dotnet", "build", "-c", "Release", "--nologo"],
                        directory)
            check(build["exit_code"] == 0, name + " source build failed: " + str(build))
            projects[name] = directory

        target = work / "generated"
        config = work / "adapter.json"
        config.write_text(json.dumps({"testHost": {
            "baseClass": "object", "className": "GateFixture",
            "classAttributes": ["TestFixture"]}}),
            encoding="utf-8")
        cli = root_repo / "Migrator.AssemblyCli" / "bin" / "Release" / "net10.0" / "Migrator.AssemblyCli.dll"
        check(cli.exists(), "Build Migrator.AssemblyCli before NUnit regression")
        generated = run(["dotnet", str(cli), "generate", str(projects["nunit3"]),
                         str(target), "--config", str(config), "--build", "--test"],
                        root_repo)
        check(generated["exit_code"] == 0,
              "Synthetic target generation/build/NUnit failed: " + str(generated))
        verification = json.loads(generated["stdout_tail"])
        runtime = verification["nUnitRuntime"]
        check(runtime["status"] == "passed"
              and runtime["executedTests"] == 5
              and runtime["passedTests"] == 5
              and verification["status"] == "runtime_passed_review_required",
              "Target assembly NUnit TRX gate mishandled ignored/explicit cases: "
              + json.dumps(runtime, ensure_ascii=False))
        source_body = (target / "GateFixturePlaywright.cs")
        check(source_body.exists(), "Expected generated source missing")
        generated_text = source_body.read_text(encoding="utf-8")
        for term in ('[Ignore("synthetic maintenance")]', '[Explicit("synthetic opt-in")]',
                     '[TestCase(5)]', '[TestCase(6)]',
                     '[TestCase(3, Ignore = "row maintenance")]',
                     '[TestCase(4, Explicit = true)]'):
            check(term in generated_text, "Generated target lost " + term)
        check("TestCaseSource" not in generated_text,
              "Unsafe source-backed TestCaseSource survived without declaration")
        check("Assert.Inconclusive" not in generated_text,
              "Synthetic target hides unconverted actions via inconclusive")
        observations["target_generated_build"] = generated

        for label, directory in [("nunit3", projects["nunit3"]),
                                 ("nunit4", projects["nunit4"]),
                                 ("target", target)]:
            observations[label + "_discovery"] = list_tests(directory)
            observations[label + "_default"] = run_tests(directory, label + "_default")
            observations[label + "_explicit"] = run_tests(
                directory, label + "_explicit",
                ["--filter", "FullyQualifiedName~OptIn"])
            observations[label + "_failure"] = run_tests(
                directory, label + "_failure",
                ["--filter", "FullyQualifiedName~Crash"])
            regular = observations[label + "_default"]
            check(regular["process"]["exit_code"] == 0,
                  label + " default run failed")
            check(regular["trx"]["counters"]["failed"] == 0,
                  label + " failed default run")
            check("Ignored" not in "".join(
                n for n, o in regular["trx"]["cases"].items() if o == "Passed"),
                label + " accidentally activated ignored method")
            check("Crash" not in "".join(
                n for n, o in regular["trx"]["cases"].items() if o == "Passed"),
                label + " accidentally activated explicit failure")
            check(observations[label + "_explicit"]["process"]["exit_code"] == 0,
                  label + " explicitly selected opt-in did not pass")
            check(observations[label + "_failure"]["process"]["exit_code"] != 0
                  and observations[label + "_failure"]["trx"]["counters"]["failed"] >= 1,
                  label + " explicitly selected failure did not fail")
        check(observations["nunit3_discovery"] == observations["target_discovery"],
              "NUnit3 source/target --list-tests mismatch")
        observations["comparison_default"] = compare(
            observations["nunit3_default"], observations["target_default"], "default")
        observations["comparison_explicit"] = compare(
            observations["nunit3_explicit"], observations["target_explicit"], "explicit")
        observations["comparison_failure"] = compare(
            observations["nunit3_failure"], observations["target_failure"], "failure")
        # NUnit 4 is an independently observed baseline; target currently pins NUnit 3.
        for name in ("nunit3", "nunit4"):
            check(observations[name + "_discovery"], name + " discovery empty")
        for label, directory in [("nunit3", projects["nunit3"]),
                                 ("nunit4", projects["nunit4"]),
                                 ("target", target)]:
            for trx_file in (directory / "TestResults").glob("*.trx"):
                (evidence / (label + "_" + trx_file.name)).write_bytes(
                    trx_file.read_bytes())
        (evidence / "summary.json").write_text(
            json.dumps(observations, indent=2, ensure_ascii=False) + "\n",
            encoding="utf-8")
        print("NUNIT_PREVIEW29_SYNTHETIC_RUNTIME_PASSED")
        print(json.dumps({k: v for k, v in observations.items()
                          if k.endswith("_discovery")}, ensure_ascii=False))


if __name__ == "__main__":
    main()
