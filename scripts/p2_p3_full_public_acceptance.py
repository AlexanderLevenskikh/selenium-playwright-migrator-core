#!/usr/bin/env python3
"""Real original NUnit project: Core static acceptance on exact parent IDs.

Calls new read-only acceptance-inspect on 10 pinned original C# source files;
has NO authorization to build, run NUnit, launch Chrome or autoapply target.
Artifact is source/target lineage and blockers only, never runtime parity.
"""
import hashlib
import json
from pathlib import Path
import subprocess
import sys

import p2_real_public_text_input_slice as baseline

OUT = Path("artifacts/p2p3-original-nunit-acceptance")


def require(ok, message):
    if not ok:
        raise RuntimeError("ORIGINAL_NUNIT_ACCEPTANCE_FAIL_CLOSED: " + message)


def invoke(cmd, cwd):
    try:
        proc = subprocess.run([str(x) for x in cmd], cwd=str(cwd),
            capture_output=True, text=True, errors="replace", timeout=120)
        require(len(proc.stdout) < 4_000_000,
                "unbounded acceptance inspection output")
        return {"exitCode":proc.returncode,
                "payload":json.loads(proc.stdout) if proc.stdout.strip() else None,
                "stderrTail":proc.stderr[-1200:]}
    except (ValueError, subprocess.TimeoutExpired) as exc:
        return {"exitCode":None,"payload":None,
                "stderrTail":type(exc).__name__}


def main(arg):
    checkout = Path(arg).resolve()
    root = Path(__file__).resolve().parent.parent
    output = root / OUT
    output.mkdir(parents=True, exist_ok=True)
    require(checkout.is_dir() and not checkout.is_symlink(),
            "source checkout missing")
    require(baseline.git(checkout,"rev-parse","HEAD").decode().strip()
            == baseline.COMMIT,"public source commit stale")
    ledger_path = root/"artifacts/p2p3-public-full-source-obligations/receipt.json"
    require(ledger_path.is_file(),"full original public POM ledger required")
    ledger = json.loads(ledger_path.read_text("utf-8"))
    require(ledger["sourceCommit"] == baseline.COMMIT and
            ledger["sourcePomMethodCount"] == 15 and
            ledger["actualCoreOriginalProjectPlan"]["declaredTests"] == 10,
            "ledger source or original NUnit declarations incomplete")
    record = ledger["actualCoreOriginalProjectPlan"]
    require(record["status"]=="ready_for_build" and
            len(record["analysisId"])==64 and
            len(record["candidateId"])==64 and
            len(record["sourceTreeHash"])==64,
            "exact original Core plan IDs unavailable")
    core = root/"Migrator.AssemblyCli"/"bin"/"Release"/"net10.0"/"Migrator.AssemblyCli.dll"
    require(core.is_file(),"Core CLI not built")
    source = checkout/"AssignmentProject"
    cmd = ("dotnet",core,"acceptance-inspect",source,
           record["analysisId"],record["candidateId"])
    result=invoke(cmd,root)
    require(result["exitCode"]==3 and result["payload"] is not None,
            "read-only original source acceptance did not return blocking result: "+
            str(result))
    report=result["payload"]
    require(report["schemaVersion"]=="migrator-acceptance/v1" and
            report["status"] in ("rejected_incomplete","blocked_unverified") and
            report["analysisId"]==record["analysisId"] and
            report["candidateId"]==record["candidateId"] and
            report["sourceTreeHash"]==record["sourceTreeHash"] and
            report["targetTreeHash"]==record["previewTargetTreeHash"] and
            report["sourceObligations"]>0 and
            report["missingObligations"]>=0 and
            report["candidateMatches"]>=0 and
            report["runtimeVerified"] is False and
            report["canAutoApply"] is False and
            report["acceptedForRC"] is False and
            report["execution"]=="read_only_not_run",
            "acceptance ID/status/inventory failed to bind actual original project: "+
            json.dumps({"plan":record,"gate":{k:report.get(k) for k in
                ("status","analysisId","candidateId","sourceTreeHash",
                 "targetTreeHash","sourceObligations","runtimeVerified",
                 "canAutoApply","acceptedForRC","execution")}},sort_keys=True))
    expected_not_run=("compile","testDiscovery","runner",
                      "realBrowser","behavioralEquivalence")
    require(all(report["stages"][s]=="not_run" for s in expected_not_run)
            and report["stages"]["syntax"] in ("passed","failed"),
            "read-only source acceptance falsely reported runtime certification")
    lines=report["lineage"]
    require(len(lines)==report["sourceObligations"] and
            all(len(line["sourceSha256"])==64 and
                line["status"] in ("missing_target","candidate_unverified")
                for line in lines),
            "source-target operation lineage missing hash/status")
    require(report["missingObligations"]==
            sum(line["status"]=="missing_target" for line in lines),
            "missing source obligations not counted exactly")
    # Wrong CandidateId must not be reusable for any source state, even when
    # the initial preview/target would have compiled without the browser.
    invalid_id=("0" if record["candidateId"][0]!="0" else "1") + \
        record["candidateId"][1:]
    wrong=invoke(("dotnet",core,"acceptance-inspect",source,
        record["analysisId"],invalid_id),root)
    require(wrong["exitCode"]==5 and
            "STALE_CANDIDATE" in wrong["stderrTail"],
            "wrong parent CandidateId accepted by project acceptance contract")
    # Original public POM WaitForAlert returned IAlert, and it is not
    # represented as a converted method. This is NOT hidden by a build-ready
    # assembly graph; annotate exact unresolved source graph in the receipt.
    blocked=[m for m in ledger["pomMethods"] if m["status"]!="requires_review"]
    require(any(m["type"]=="AlertsPage" and
                m["method"]=="WaitForAlert" for m in blocked),
            "source helper blocker vanished from complete original inventory")
    receipt={
        "schema":"migrator-public-original-nunit-acceptance/v1",
        "sourceCommit":baseline.COMMIT,
        "originalSourceInventorySha256":json.loads(
            (root/"artifacts/p3-original-public-project-preflight/receipt.json")
            .read_text("utf-8"))["sourceInventorySha256"],
        "analysisId":report["analysisId"],
        "candidateId":report["candidateId"],
        "sourceTreeHash":report["sourceTreeHash"],
        "previewTargetTreeHash":report["targetTreeHash"],
        "status":report["status"],
        "sourceObligations":report["sourceObligations"],
        "candidateMatches":report["candidateMatches"],
        "missingObligations":report["missingObligations"],
        "reviewRequired":report["reviewRequired"],
        "blockers":report["blockers"],
        "lineage":lines,
        "sourceBlockedPomMethods":[{
            "type":m["type"],"method":m["method"],
            "blockerCodes":m["blockerCodes"]
        } for m in blocked],
        "stages":report["stages"],
        "staleParentCandidateId":"REJECTED",
        "projectBuildReadyMeansBehaviorReady":False,
        "fullRuntimeEquivalence":"UNKNOWN",
        "acceptedForRC":False,"canAutoApply":False}
    (output/"receipt.json").write_text(
        json.dumps(receipt,sort_keys=True,indent=2)+"\n",encoding="utf-8")
    print("ORIGINAL_NUNIT_PROJECT_P2_ACCEPTANCE_NO_GO")
    print(json.dumps({"sourceCommit":baseline.COMMIT,
        "analysisId":report["analysisId"],
        "candidateId":report["candidateId"],
        "status":report["status"],
        "sourceObligations":report["sourceObligations"],
        "missingObligations":report["missingObligations"],
        "pomBlocked":len(blocked),"behavior":"UNKNOWN"},sort_keys=True))


if __name__=="__main__":
    require(len(sys.argv)==2,"full original public NUnit checkout required")
    main(sys.argv[1])
