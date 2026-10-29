#!/usr/bin/env python3
"""Synthetic NUnit 3/4 observation of a nested, cleanup-only SetUpFixture.

The source under test is invented for this regression. It contains no
customer code or paths. Do not infer execution semantics from attribute names.
"""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import xml.etree.ElementTree as ET


SOURCE = """
using System;
using System.IO;
using NUnit.Framework;

namespace Fictional.Regression {
    public static class Probe {
        public static void Note(string name) {
            var path = Environment.GetEnvironmentVariable("NUNIT_NESTED_PROBE");
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("Probe path unavailable");
            File.AppendAllText(path, name + "\\n");
        }
    }
    public abstract class FixtureBase {
        [SetUpFixture]
        public class NestedCleanup {
            [OneTimeTearDown] public void End() {
                Probe.Note("nested-cleanup");
            }
        }
    }
    [SetUpFixture]
    public class NamespaceCleanup {
        [OneTimeSetUp] public void Before() { Probe.Note("namespace-setup"); }
        [OneTimeTearDown] public void After() { Probe.Note("namespace-cleanup"); }
    }
    [TestFixture]
    public class OneTest : FixtureBase {
        [Test] public void Runs() { Probe.Note("test"); }
    }
}
"""


def run_one(root: Path, nunit: str, adapter: str):
    directory = root / ("nunit-" + nunit.replace(".", "-"))
    directory.mkdir()
    (directory / "Source.cs").write_text(SOURCE, encoding="utf-8")
    project = f"""<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup>
<TargetFramework>net10.0</TargetFramework>
<IsTestProject>true</IsTestProject>
<IsPackable>false</IsPackable>
</PropertyGroup>
<ItemGroup>
<PackageReference Include="NUnit" Version="{nunit}" />
<PackageReference Include="NUnit3TestAdapter" Version="{adapter}" />
<PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
</ItemGroup>
</Project>"""
    (directory / "Synthetic.csproj").write_text(project, encoding="utf-8")
    trace = directory / "observed-order.txt"
    results = directory / "test-results"
    results.mkdir()
    cmd = ["dotnet", "test", str(directory / "Synthetic.csproj"),
           "-c", "Release", "--logger", "trx;LogFileName=result.trx",
           "--results-directory", str(results), "--verbosity", "quiet"]
    proc = subprocess.run(cmd, capture_output=True, text=True,
                          timeout=240, env=dict(os.environ,
                          NUNIT_NESTED_PROBE=str(trace)))
    events = trace.read_text(encoding="utf-8").splitlines() if trace.exists() else []
    trx = results / "result.trx"
    counts = None
    if trx.exists():
        xml = ET.parse(trx)
        counters = next((element.attrib for element in xml.iter()
                        if element.tag.endswith("Counters")), None)
        if counters:
            counts = {"total": int(counters.get("total", "0")),
                      "passed": int(counters.get("passed", "0")),
                      "failed": int(counters.get("failed", "0"))}
    response = {"nunit": nunit, "adapter": adapter,
                "exit_code": proc.returncode, "counters": counts,
                "events": events,
                "nested_cleanup_observed": "nested-cleanup" in events,
                "log_tail": (proc.stdout + "\n" + proc.stderr)[-2200:]}
    print(json.dumps(response, ensure_ascii=False), flush=True)
    if proc.returncode != 0 or not counts or counts["passed"] != 1:
        raise AssertionError("Synthetic NUnit baseline did not run 1/1 tests")
    if "namespace-setup" not in events or "namespace-cleanup" not in events:
        raise AssertionError("Synthetic namespace-level NUnit hooks missing")
    if events.index("namespace-setup") >= events.index("test") or \
        events.index("test") >= events.index("namespace-cleanup"):
        raise AssertionError("Namespace NUnit hook ordering failed")
    return response


if __name__ == "__main__":
    with tempfile.TemporaryDirectory(prefix="fictional-nunit-nested-") as temp:
        root = Path(temp)
        observations = [run_one(root, "3.14.0", "4.5.0"),
                        run_one(root, "4.3.2", "5.1.0")]
        print("NESTED_PROBE_SUMMARY=" + json.dumps(
            [{"version": x["nunit"], "nested_cleanup_observed":
                x["nested_cleanup_observed"], "events": x["events"]}
             for x in observations]), flush=True)
