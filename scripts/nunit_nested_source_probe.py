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



MULTI_SOURCE = """
using System;
using System.IO;
using NUnit.Framework;
namespace Fictional.Regression {
    public static class Probe {
        public static void Note(string name) {
            var path = Environment.GetEnvironmentVariable("NUNIT_NESTED_PROBE");
            File.AppendAllText(path!, name + "\\n");
        }
    }
    public abstract class SharedBase {
        [SetUpFixture] public class NestedCleanup {
            [OneTimeTearDown] public void End() {
                Probe.Note("nested-cleanup");
            }
        }
    }
    [SetUpFixture] public class NamespaceScope {
        [OneTimeSetUp] public void Begin() {
            Probe.Note("namespace-setup");
        }
        [OneTimeTearDown] public void End() {
            Probe.Note("namespace-cleanup");
        }
    }
    [TestFixture] public class DerivedA : SharedBase {
        [Test] public void A() { Probe.Note("derived-A"); }
    }
    [TestFixture] public class DerivedB : SharedBase {
        [Test] public void B() { Probe.Note("derived-B"); }
    }
    [TestFixture] public class Unrelated {
        [Test] public void C() { Probe.Note("unrelated-C"); }
    }
}
namespace Fictional.Other {
    [TestFixture] public class Other {
        [Test] public void D() {
            Fictional.Regression.Probe.Note("other-D");
        }
    }
}
"""

FAILURE_SOURCE = """
using System;
using System.IO;
using NUnit.Framework;
namespace Fictional.Regression {
    public static class Probe {
        public static void Note(string name) {
            var path = Environment.GetEnvironmentVariable("NUNIT_NESTED_PROBE");
            File.AppendAllText(path!, name + "\\n");
        }
    }
    public abstract class SharedBase {
        [SetUpFixture] public class NestedCleanup {
            [OneTimeTearDown] public void End() {
                Probe.Note("nested-cleanup");
            }
        }
    }
    [TestFixture] public class Failing : SharedBase {
        [OneTimeSetUp] public void Before() {
            Probe.Note("fixture-setup-fails");
            throw new InvalidOperationException("synthetic-failure");
        }
        [OneTimeTearDown] public void After() {
            Probe.Note("fixture-teardown");
        }
        [Test] public void A() { Probe.Note("test-should-not-run"); }
    }
}
"""

def run_one(root: Path, nunit: str, adapter: str,
            scenario: str = "baseline", source: str = SOURCE,
            expected_total: int = 1, expected_passed: int = 1):
    directory = root / ("nunit-" + nunit.replace(".", "-") + "-" + scenario)
    directory.mkdir()
    (directory / "Source.cs").write_text(source, encoding="utf-8")
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
    response = {"nunit": nunit, "adapter": adapter, "scenario": scenario,
                "exit_code": proc.returncode, "counters": counts,
                "events": events,
                "nested_cleanup_observed": "nested-cleanup" in events,
                "log_tail": (proc.stdout + "\n" + proc.stderr)[-2200:]}
    print(json.dumps(response, ensure_ascii=False), flush=True)
    if not counts or counts["total"] != expected_total or counts["passed"] != expected_passed:
        raise AssertionError("Synthetic NUnit test count/status differs from expected")
    if (proc.returncode == 0) != (expected_passed == expected_total):
        raise AssertionError("Synthetic NUnit test process exit disagrees with TRX")
    if scenario == "baseline":
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
        for nunit, adapter in (("3.14.0", "4.5.0"), ("4.3.2", "5.1.0")):
            observations.append(run_one(root, nunit, adapter, "multi",
                MULTI_SOURCE, expected_total=4, expected_passed=4))
            observations.append(run_one(root, nunit, adapter, "failure",
                FAILURE_SOURCE, expected_total=1, expected_passed=0))
        print("NESTED_PROBE_SUMMARY=" + json.dumps(
            [{"version": x["nunit"], "scenario": x["scenario"],
              "nested_cleanup_observed": x["nested_cleanup_observed"],
              "events": x["events"], "counters": x["counters"]}
             for x in observations]), flush=True)
