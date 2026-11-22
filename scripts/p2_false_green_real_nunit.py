#!/usr/bin/env python3
"""Real synthetic green NUnit counterexample for P2. No private source or browser."""
import hashlib
import json
from pathlib import Path
import shutil
import tempfile
import xml.etree.ElementTree as ET

from nunit_execution_runtime import check, project, run

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "artifacts" / "p2-false-green"
SRC = r"""
using NUnit.Framework;
[TestFixture] public sealed class GreenButIncomplete {
    private static int _clicks;
    private sealed class Button { public void Click() { _clicks++; } }
    [Test] public void LooksGreen() {
        new Button().Click(); // required source side effect, lost in target
        Assert.That(true, Is.True);
    }
}
"""
TARGET = r"""
using NUnit.Framework;
[TestFixture] public sealed class GreenButIncomplete {
    [Test] public void LooksGreen() {
        Assert.That(true, Is.True); // action deleted yet test still passes
    }
}
"""

def verified_trx(path):
    root = ET.parse(path).getroot()
    cases = [(x.attrib.get("testName"), x.attrib.get("outcome")) for x in root.iter()
             if x.tag.endswith("UnitTestResult")]
    check(cases == [("LooksGreen", "Passed")], "expected exactly one real passing NUnit test: " + str(cases))
    return {"sha256": hashlib.sha256(path.read_bytes()).hexdigest(), "cases": cases}

def main():
    OUT.mkdir(parents=True, exist_ok=True)
    observations = {}
    with tempfile.TemporaryDirectory(prefix="migrator-p2-nunit-") as temporary:
        for label, text in (("source", SRC), ("target_missing_click", TARGET)):
            folder = Path(temporary) / label
            folder.mkdir()
            (folder / "Suite.cs").write_text(text, encoding="utf-8")
            (folder / "Synthetic.csproj").write_text(project("3.14.0", "4.5.0"), encoding="utf-8")
            built = run(["dotnet", "build", "-c", "Release", "--nologo"], folder)
            check(built["exit_code"] == 0, "synthetic build failed: " + str(built))
            trx_name = label + ".trx"
            results = folder / "TestResults"
            results.mkdir()
            execution = run(["dotnet", "test", "-c", "Release", "--no-build",
                             "--no-restore", "--nologo",
                             "--logger", "trx;LogFileName=" + trx_name,
                             "--results-directory", str(results)], folder)
            check(execution["exit_code"] == 0, "green NUnit precondition failed: " + str(execution))
            raw = results / trx_name
            observations[label] = {"dotnetTestExitCode": execution["exit_code"],
                                   "trx": verified_trx(raw)}
            shutil.copyfile(raw, OUT / trx_name)
    check("new Button().Click()" in SRC and "Click()" not in TARGET,
          "false green counterexample lost the missing source action")
    observations["acceptanceExpected"] = "rejected_incomplete"
    observations["lineageGate"] = "checked_by_MigrationAcceptanceSmoke"
    (OUT / "summary.json").write_text(json.dumps(observations, indent=2) + "\n", encoding="utf-8")
    print("P2_REAL_NUNIT_GREEN_WITH_MISSING_CLICK_CONFIRMED_NOT_ACCEPTED")
    print(json.dumps(observations, sort_keys=True))

if __name__ == "__main__":
    main()
