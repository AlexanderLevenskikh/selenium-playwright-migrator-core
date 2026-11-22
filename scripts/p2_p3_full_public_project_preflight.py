#!/usr/bin/env python3
"""Exact original public NUnit .csproj compile/discovery only; no test execution.

This is NOT a Core-produced Playwright target or full-project runtime parity.
Package restore/build are isolated in a temporary copy on hosted CI, bounded
by subprocess timeouts. External-site tests never execute at this stage.
"""
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

import p2_real_public_text_input_slice as baseline

OUT = Path("artifacts/p3-original-public-project-preflight")
PROJECT = "AssignmentProject/CSE2522_Selenium_NUnit.csproj"
PACKAGES = {
    "Microsoft.NET.Test.Sdk": "17.9.0",
    "NUnit": "3.14.0",
    "NUnit3TestAdapter": "4.5.0",
    "Selenium.WebDriver": "4.40.0",
    "Selenium.Support": "4.40.0",
    "Selenium.WebDriver.ChromeDriver": "144.0.7559.13300",
    "DotNetSeleniumExtras.WaitHelpers": "3.11.0",
}


def require(ok, why):
    if not ok:
        raise RuntimeError("ORIGINAL_NUNIT_PREFLIGHT_FAIL_CLOSED: " + why)


def sha(blob):
    return hashlib.sha256(blob).hexdigest()


def run(args, cwd, seconds):
    try:
        result = subprocess.run([str(a) for a in args], cwd=str(cwd),
            capture_output=True, text=True, errors="replace", timeout=seconds)
        return {"status": "passed" if result.returncode == 0 else "failed",
                "exitCode": result.returncode,
                "stdoutTail": result.stdout[-1600:],
                "stderrTail": result.stderr[-1600:]}
    except subprocess.TimeoutExpired:
        return {"status":"prerequisite_failure",
                "exitCode":None,"stderrTail":"timeout","stdoutTail":""}


def main(argument):
    checkout = Path(argument).resolve()
    root = Path(__file__).resolve().parent.parent
    output = root / OUT
    output.mkdir(parents=True, exist_ok=True)
    require(checkout.is_dir() and not checkout.is_symlink(), "source missing")
    require(baseline.git(checkout, "rev-parse", "HEAD").decode().strip()
            == baseline.COMMIT, "public source commit changed")
    csproj = checkout / PROJECT
    require(csproj.is_file() and not csproj.is_symlink(), "csproj missing")
    msbuild = ET.parse(csproj).getroot()
    require(msbuild.tag == "Project" and
            msbuild.attrib == {"Sdk":"Microsoft.NET.Sdk"}, "unsafe SDK root")
    require(msbuild.findtext(".//TargetFramework") == "net10.0",
            "upstream target framework changed")
    require(not any(node.tag in {"Exec","Target","UsingTask","Import",
            "ImportGroup","Choose"} for node in msbuild.iter()),
            "upstream imperative/dynamic MSBuild is not accepted")
    actual = {p.attrib.get("Include"):p.attrib.get("Version") for p in
              msbuild.findall(".//PackageReference")}
    require(actual == PACKAGES and
            len(msbuild.findall(".//PackageReference")) == len(PACKAGES) and
            not msbuild.findall(".//ProjectReference"),
            "unreviewed package/reference graph changed")
    sources = sorted((checkout / "AssignmentProject").rglob("*.cs"))
    require(len(sources) == 10 and not any(p.is_symlink() for p in sources),
            "source count/ownership/symlink policy changed")
    source_files = []
    for path in [csproj, *sources]:
        rel = path.relative_to(checkout).as_posix()
        blob = baseline.git(checkout, "rev-parse", "HEAD:"+rel).decode().strip()
        canonical = baseline.git(checkout, "cat-file", "blob", blob)
        require(len(canonical) < 1024*1024 and
                canonical.replace(b"\r\n",b"\n") ==
                path.read_bytes().replace(b"\r\n",b"\n"),
                "Git blob identity mismatch outside line endings: "+rel)
        source_files.append({"path":rel,"gitBlobSha1":blob,
                             "canonicalSha256":sha(canonical)})
    manifest_hash = sha(b"".join(
        row["path"].encode()+b"\0"+row["canonicalSha256"].encode()+b"\n"
        for row in source_files))
    stages = {"restore":{"status":"not_run"},
              "build":{"status":"not_run"},"discovery":{"status":"not_run"}}
    with tempfile.TemporaryDirectory(prefix="migrator-original-nunit-") as d:
        target = Path(d)/"AssignmentProject"
        shutil.copytree(checkout / "AssignmentProject", target)
        project = target / Path(PROJECT).name
        stages["restore"] = run(("dotnet","restore",project,"--nologo"),target,180)
        if stages["restore"]["status"] == "passed":
            stages["build"] = run(("dotnet","build",project,"-c","Release",
                                   "--no-restore","--nologo"),target,180)
        if stages["build"]["status"] == "passed":
            stages["discovery"] = run(("dotnet","test",project,"-c","Release",
                "--no-build","--no-restore","--nologo","--list-tests"),target,120)
    status = ("source_compilation_and_discovery_passed" if all(
        part["status"] == "passed" for part in stages.values()
    ) and "The following Tests are available:" in stages["discovery"]["stdoutTail"]
              else "prerequisite_failure_or_source_build_unknown")
    receipt = {
        "schema":"migrator-original-public-project-preflight/v1",
        "sourceCommit":baseline.COMMIT, "sourceFileCount":len(sources),
        "projectFile":PROJECT,"sourceFileIdentities":source_files,
        "sourceInventorySha256":manifest_hash,
        "status":status,"stages":stages,
        "originalNUnitTestsExecuted":False,
        "originalTestDiscovery":stages["discovery"]["status"],
        "originalNUnitTRX":"not_generated",
        "targetProjectBuild":"not_run",
        "fullProjectBehavioralEquivalence":"UNKNOWN",
        "candidateId":"not_created",
        "acceptedForRC":False,"canAutoApply":False}
    (output/"receipt.json").write_text(
        json.dumps(receipt,sort_keys=True,indent=2)+"\n",encoding="utf-8")
    print("ORIGINAL_PUBLIC_NUNIT_PROJECT_STAGE="+status)
    print(json.dumps({"source":baseline.COMMIT,"inventorySha256":manifest_hash,
        "stage":status,"fullProject":"UNKNOWN"},sort_keys=True))


if __name__=="__main__":
    require(len(sys.argv)==2,"full original public project checkout required")
    main(sys.argv[1])
