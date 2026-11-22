#!/usr/bin/env python3
"""Generate the ACTUAL full Core NUnit target for fixed public source.

Opt-in Core materialization + real dotnet build; NUnit *discovery* only.
Never run original external-site tests, generated target NUnit/browser tests,
or arbitrary source project build scripts. This is NOT behavioral equivalence.
"""
import hashlib
import json
from pathlib import Path
import subprocess
import shutil
import sys
import tempfile

import p2_real_public_text_input_slice as baseline

OUT = Path("artifacts/p2p3-public-full-generated-project")


def require(ok, why):
    if not ok:
        raise RuntimeError("PUBLIC_FULL_GENERATION_FAIL_CLOSED: " + why)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def invoke(args, where, seconds=180):
    try:
        p = subprocess.run([str(x) for x in args], cwd=str(where),
            capture_output=True, text=True, errors="replace", timeout=seconds)
        require(len(p.stdout) < 3_000_000, "Core generation result too large")
        data = None
        try:
            data = json.loads(p.stdout) if p.stdout.strip() else None
        except ValueError:
            pass
        return {"code":p.returncode,"payload":data,
                "stdoutTail":p.stdout[-2400:],"stderrTail":p.stderr[-1800:]}
    except subprocess.TimeoutExpired:
        return {"code":None,"payload":None,
                "stdoutTail":"","stderrTail":"bounded subprocess timeout"}


def main(arg):
    checkout = Path(arg).resolve()
    root = Path(__file__).resolve().parent.parent
    out = root / OUT
    out.mkdir(parents=True, exist_ok=True)
    require(checkout.is_dir() and not checkout.is_symlink(),
            "original source checkout missing")
    require(baseline.git(checkout, "rev-parse", "HEAD").decode().strip()
            == baseline.COMMIT, "original source Git HEAD drifted")
    project = checkout / "AssignmentProject"
    prior = root/"artifacts/p2p3-original-nunit-acceptance/receipt.json"
    require(prior.is_file(), "original full static acceptance receipt missing")
    accepted = json.loads(prior.read_text("utf-8"))
    require(accepted["sourceCommit"]==baseline.COMMIT and
            accepted["status"] in ("rejected_incomplete","blocked_unverified") and
            accepted["acceptedForRC"] is False and
            len(accepted["analysisId"])==64 and
            len(accepted["candidateId"])==64,
            "original full source not bound to a NO-GO acceptance gate")
    ledger=json.loads((root/"artifacts/p2p3-public-full-source-obligations/receipt.json")
        .read_text("utf-8"))
    plan=ledger["actualCoreOriginalProjectPlan"]
    require(plan["status"]=="ready_for_build" and
            plan["declaredTests"]==10 and
            plan["analysisId"]==accepted["analysisId"] and
            plan["candidateId"]==accepted["candidateId"],
            "original full assembly/acceptance parent mismatch")
    cli=root/"Migrator.AssemblyCli"/"bin"/"Release"/"net10.0"/"Migrator.AssemblyCli.dll"
    require(cli.is_file(),"Core CLI not compiled")
    with tempfile.TemporaryDirectory(prefix="migrator-full-public-playwright-") as directory:
        dst=Path(directory)/"generated"
        generate=invoke(("dotnet",cli,"generate",project,dst,"--build"),
                        root, 240)
        payload=generate["payload"] or {}
        require(generate["code"] in (0,4) and
                payload.get("schemaVersion")==
                "migrator-target-assembly-verification/v1alpha1" and
                payload["analysisId"]==accepted["analysisId"] and
                payload["candidateId"]==accepted["candidateId"] and
                payload["targetTreeHash"]==plan["targetTreeHash"] and
                payload["completeness"]["status"]=="passed" and
                payload["nUnitRuntime"]["status"]=="not_run" and
                payload["status"] in (
                    "build_passed_review_required","failed","blocked"),
                "Core did not produce the exact complete source-locked target "+
                str(generate))
        require(dst.is_dir(), "Core full generated project absent")
        files=sorted(p for p in dst.rglob("*") if p.is_file() and
            not any(part in ("bin","obj","TestResults") for part in
                    p.relative_to(dst).parts))
        rel=[p.relative_to(dst).as_posix() for p in files]
        require(len(rel)>2 and
                "Migrator.Generated.Tests.csproj" in rel and
                "migrator-assembly-manifest.json" in rel and
                len([p for p in rel if p.endswith(".cs")])>=4,
                "Core target did not emit source-locked NUnit assembly")
        manifest=json.loads((dst/"migrator-assembly-manifest.json").read_text())
        require(manifest["analysisId"]==accepted["analysisId"] and
                manifest["candidateId"]==accepted["candidateId"] and
                manifest["sourceTreeHash"]==plan["sourceTreeHash"],
                "Core generated target manifest not parent/source-bound")
        discovery={"status":"not_run"}
        if payload["build"]["status"]=="passed":
            attempt=invoke(("dotnet","test",dst/"Migrator.Generated.Tests.csproj",
                "--configuration","Release","--no-build","--no-restore",
                "--nologo","--list-tests"),dst,120)
            names=[]
            if (attempt["code"] == 0 and
                    "The following Tests are available:" in attempt["stdoutTail"]):
                marker=attempt["stdoutTail"].split("The following Tests are available:",1)[1]
                names=[s.strip() for s in marker.splitlines() if s.strip()]
            discovery={"status":"passed" if attempt["code"]==0 else "failed",
                       "exitCode":attempt["code"],
                       "discoveredCases":names,
                       "discoveredCount":len(names),
                       "originalCaseCount":10,
                       "countParity":len(names)==10,
                       "stdoutTail":attempt["stdoutTail"],
                       "stderrTail":attempt["stderrTail"]}
        # Preserve the actual Core-generated C#/.csproj/manifest, NOT bin/obj
        # or opaque upstream source packages. Independent consumers can verify
        # every source hash and compile a pinned emitted target from the ZIP.
        for path in files:
            relative=path.relative_to(dst)
            archived=out/"target"/relative
            archived.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(path,archived)
        contents=[{"file":p.relative_to(dst).as_posix(),
                   "sha256":sha(p.read_bytes()),
                   "size":p.stat().st_size} for p in files]
        receipt={
            "schema":"migrator-public-full-core-generated-project/v1",
            "sourceCommit":baseline.COMMIT,
            "sourceInventorySha256":accepted["originalSourceInventorySha256"],
            "analysisId":accepted["analysisId"],
            "candidateId":accepted["candidateId"],
            "sourceTreeHash":accepted["sourceTreeHash"],
            "targetTreeHash":payload["targetTreeHash"],
            "originalDeclaredTests":10,
            "generatedFiles":contents,
            "generatedCsFileCount":len([p for p in rel if p.endswith(".cs")]),
            "coreMaterializationStatus":payload["status"],
            "exactFileSet":payload["completeness"],
            "targetBuild":payload["build"],
            "targetNUnitRuntime":payload["nUnitRuntime"],
            "targetTestDiscovery":discovery,
            "staticAcceptanceStatus":accepted["status"],
            "staticMissingObligations":accepted["missingObligations"],
            "sourceNUnitRuntime":"not_run_external_website",
            "independentBehavioralEquivalence":"UNKNOWN",
            "canAutoApply":False,"acceptedForRC":False}
        (out/"receipt.json").write_text(
            json.dumps(receipt,sort_keys=True,indent=2)+"\n",encoding="utf-8")
        print("PUBLIC_FULL_ORIGINAL_TO_CORE_TARGET_PROJECT_GENERATED_REVIEW_ONLY")
        print(json.dumps({
            "source":baseline.COMMIT,
            "targetTreeHash":payload["targetTreeHash"],
            "projectSourceFiles":receipt["generatedCsFileCount"],
            "materialization":payload["status"],
            "build":payload["build"]["status"],
            "NUnitDiscovery":discovery["status"],
            "cases":discovery.get("discoveredCount"),
            "staticAcceptance":accepted["status"],
            "behavioralEquivalence":"UNKNOWN"},sort_keys=True))


if __name__=="__main__":
    require(len(sys.argv)==2,"exact public source root required")
    main(sys.argv[1])
