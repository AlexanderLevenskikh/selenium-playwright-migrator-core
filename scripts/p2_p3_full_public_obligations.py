#!/usr/bin/env python3
"""Source-bound coverage ledger for an entire fixed public NUnit project.

Never executes upstream tests, changes source, or emits executable target C#.
This is not a full-project behavioral parity receipt. It records real Core
assembly-plan status plus all 15 pinned public POM method statuses separately.
"""
import hashlib
import json
from pathlib import Path
import subprocess
import sys

import p2_real_public_text_input_slice as baseline

METHODS = {
    "HomePage": ("GoToTextInput", "GoToSampleApp",
                 "GoToClientSideDelay", "GoToAlerts"),
    "TextInputPage": ("EnterText", "ClickButton", "GetButtonText"),
    "SampleAppPage": ("Login", "GetMessage"),
    "ClientSideDelayPage": ("ClickButton", "GetResult"),
    "AlertsPage": ("ClickAlert", "ClickConfirm", "ClickPrompt", "WaitForAlert"),
}
OUT = Path("artifacts/p2p3-public-full-source-obligations")


def require(ok, message):
    if not ok:
        raise RuntimeError("PUBLIC_FULL_SOURCE_OBLIGATION_FAIL_CLOSED: " + message)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def invoke(cli, cwd, args):
    try:
        r = subprocess.run(("dotnet", str(cli), *[str(x) for x in args]),
                           cwd=str(cwd), capture_output=True, text=True,
                           errors="replace", timeout=75)
        require(len(r.stdout) < 2_000_000, "unbounded Core CLI stdout")
        value = json.loads(r.stdout) if r.stdout.strip() else None
        return {"exitCode":r.returncode, "payload":value,
                "stderrTail":r.stderr[-1200:]}
    except (ValueError, subprocess.TimeoutExpired) as exc:
        return {"exitCode":None, "payload":None,
                "stderrTail":type(exc).__name__}


def main(arg):
    checkout = Path(arg).resolve()
    root = Path(__file__).resolve().parent.parent
    output = root / OUT
    output.mkdir(parents=True, exist_ok=True)
    require(checkout.is_dir() and not checkout.is_symlink(), "no source tree")
    require(baseline.git(checkout, "rev-parse", "HEAD").decode().strip()
            == baseline.COMMIT, "public source Git HEAD changed")
    pages = checkout/"AssignmentProject"/"Pages"
    project = checkout/"AssignmentProject"
    require(pages.is_dir() and project.is_dir(), "full public project missing")
    names = sorted(p.name for p in pages.glob("*.cs") if not p.is_symlink())
    require(names == sorted(owner+".cs" for owner in METHODS),
            "full public POM class/file inventory drifted")
    cli = root/"Migrator.AssemblyCli"/"bin"/"Release"/"net10.0"/"Migrator.AssemblyCli.dll"
    require(cli.is_file(), "actual Core assembly CLI is required")
    inventory = invoke(cli, root, ("pom-inspect", pages))
    require(inventory["exitCode"] == 0 and inventory["payload"] is not None,
            "Core cannot inspect full original POM file set")
    scope = inventory["payload"]
    tree = scope["sourceTreeHash"]
    require(scope["inspectedFiles"] == 5 and len(tree) == 64,
            "Core full POM source hash or file scope changed")
    records = []
    for owner, methods in METHODS.items():
        for method in methods:
            result = invoke(cli, root, ("pom-plan", pages,
                "CSE2522_Selenium_NUnit.Pages."+owner, method, tree))
            item = result["payload"] or {}
            status = item.get("status", "UNKNOWN")
            if result["exitCode"] not in (0, 3):
                status = "UNKNOWN"
            elif status not in ("requires_review", "blocked"):
                status = "UNKNOWN"
            actions = item.get("actions", [])
            blockers = item.get("blockers", [])
            require(status == "UNKNOWN" or
                    (item["sourceTreeHash"] == tree and
                     item["sourceMethod"] == method and
                     item["sourceType"] ==
                     "CSE2522_Selenium_NUnit.Pages."+owner and
                     item["runtimeVerified"] is False and
                     item["canAutoApply"] is False and
                     ((status == "blocked" and len(blockers) > 0 and
                       result["exitCode"] == 3) or
                      (status == "requires_review" and len(actions) > 0 and
                       len(blockers) == 0 and result["exitCode"] == 0))),
                    "Core method was silently partial or auto-certified: "+owner+"."+method)
            records.append({
                "type":owner,"method":method,
                "sourceMethodSha256":item.get("sourceMethodSha256"),
                "sourceTreeHash":item.get("sourceTreeHash"),
                "status":status,"exitCode":result["exitCode"],
                "effects":[{"order":a["order"],"effect":a["effect"],
                            "sourceSha256":a["sourceSha256"],
                            "targetSha256":a["targetSha256"],
                            "controlFlowPath":a["controlFlowPath"]} for a in actions],
                "blockerCodes":[b["code"] for b in blockers],
                "error":result["stderrTail"] if status == "UNKNOWN" else ""})
    require(len(records) == 15 and
            len({(r["type"],r["method"]) for r in records}) == 15,
            "public POM method count/identity mismatch")
    # The original source alert-handling IAlert return cannot be projected as
    # a synchronous string/void POM method. Never allow a partial success.
    alert = next(r for r in records if
                 r["type"]=="AlertsPage" and r["method"]=="WaitForAlert")
    require(alert["status"] in ("blocked","UNKNOWN"),
            "unsupported IAlert return accepted as a complete target")
    assembly = invoke(cli, root, ("plan", project))
    full = assembly["payload"] or {}
    assembly_status = (full.get("status", "UNKNOWN") if
                       assembly["exitCode"] in (0,3) else "UNKNOWN")
    receipt = {
        "schema":"migrator-public-full-source-obligation-ledger/v1",
        "sourceCommit":baseline.COMMIT,
        "sourcePomTreeHash":tree,
        "sourcePomCount":5,
        "sourcePomMethodCount":len(records),
        "pomMethods":records,
        "summary":{"requiresReview":sum(r["status"]=="requires_review"
                                        for r in records),
                   "blocked":sum(r["status"]=="blocked" for r in records),
                   "unknown":sum(r["status"]=="UNKNOWN" for r in records)},
        "actualCoreOriginalProjectPlan":{
            "status":assembly_status,"exitCode":assembly["exitCode"],
            "schemaVersion":full.get("schemaVersion"),
            "analysisId":full.get("analysisId"),
            "candidateId":full.get("candidateId"),
            "sourceTreeHash":full.get("sourceTreeHash"),
            "targetTreeHash":full.get("targetTreeHash"),
            "previewTargetTreeHash":full.get("previewTargetTreeHash"),
            "declaredTests":full.get("declaredTests"),
            "issueCodes":[i.get("code") for i in full.get("issues", [])],
            "error":assembly["stderrTail"] if assembly_status=="UNKNOWN" else ""},
        "sourceTestFixtureExecution":"not_run",
        "originalProjectSourceBuild":"separately_attested_in_preflight",
        "playwrightFullTargetSolutionBuild":"not_run",
        "sourceTargetBehavioralEquivalence":"UNKNOWN",
        "acceptanceStatus":"BLOCKED",
        "canAutoApply":False,"acceptedForRC":False}
    (output/"receipt.json").write_text(
        json.dumps(receipt,indent=2,sort_keys=True)+"\n",encoding="utf-8")
    print("PUBLIC_FULL_SOURCE_PROJECT_GATE_REQUIRES_REVIEW")
    print(json.dumps({"sourceCommit":baseline.COMMIT,
        "pomTreeHash":tree,"methods":receipt["summary"],
        "assemblyPlanStatus":assembly_status,
        "assemblyDeclaredTests":full.get("declaredTests"),
        "projectParity":"UNKNOWN"},sort_keys=True))


if __name__=="__main__":
    require(len(sys.argv)==2, "full SHA-pinned public checkout required")
    main(sys.argv[1])
