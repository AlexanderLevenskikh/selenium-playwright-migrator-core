#!/usr/bin/env python3
"""P1-C: preregistered 8-trial ABBA/BAAB source-exact P47 disconnect_pass timing comparison.
A: original NUnit fixture. B: ONLY a 1500ms delay before writing the ready marker,
so the real Node driver remains active longer before the same external SIGTERM. Identical source-derived TearDown/OneTimeTearDown.
This is a diagnostic experiment, not an OS cleanup or a production fix.
"""
import hashlib
import json
import os
from pathlib import Path
import re
import tempfile
import textwrap
import threading
import time
from http.server import ThreadingHTTPServer

import p2_preview37_rendered_text_unicode_real_chromium as p37
import p2_preview42_fixture_lifecycle_real_chromium as p42
import p2_preview47_one_time_real_chromium as p47
import p2_preview47_process_evidence as ev

ORDER = "ABBAABBA"
DELAY_MS = 1500
SCHEMA = "migrator-p1c-p47-disconnect-pass-timing-ab/v1"
ANCHOR = '            File.WriteAllText(ready,"ready");'
ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "artifacts/p1c-disconnect-pass-timing"

def prepare_source():
    one_source = (ROOT / "Migrator.Application.Smoke/PomPreview47OneTimeCleanupSmoke.cs").read_text(encoding="utf-8")
    tear_source = (ROOT / "Migrator.Application.Smoke/PomPreview46TypedTargetClosedSmoke.cs").read_text(encoding="utf-8")
    one = p42.synthetic("OneTimeTearDown", one_source)
    tear = p42.synthetic("TypedTearDown", tear_source)
    assert "TargetClosedException" in one and "TargetClosedException" in tear
    original = p47.TEMPLATE.replace("__TEARDOWN_SOURCE__", tear).replace("__ONE_TIME_SOURCE__", one)
    assert original.count(ANCHOR) == 1, "P1C_TIMING_ANCHOR_NOT_UNIQUE"
    assert original.count("ready-"+chr(34)) == 1, "P1C_ORIGINAL_ACK_HANDSHAKE_UNPROVEN"
    variant = original.replace(ANCHOR, "            await Task.Delay("+str(DELAY_MS)+");\n"+ANCHOR)
    assert variant.replace("            await Task.Delay("+str(DELAY_MS)+");\n", "", 1) == original
    return original, variant, {"fixtureSha256A": hashlib.sha256(original.encode()).hexdigest(),
        "fixtureSha256B": hashlib.sha256(variant.encode()).hexdigest(),
        "tearDownSourceSha256": hashlib.sha256(tear.encode()).hexdigest(),
        "oneTimeTearDownSourceSha256": hashlib.sha256(one.encode()).hexdigest(),
        "singleDelta": "await Task.Delay(1500) before the fixture writes ready; Node remains live until original external SIGTERM"}

def main():
    assert os.name == "nt", "P1C_WINDOWS_ONLY"
    chrome = Path(os.environ["MIGRATOR_CHROME_BIN"]).resolve()
    assert chrome.is_file() and os.environ.get("MIGRATOR_CHROME_VERSION") == "156.0.8078.80"
    OUT.mkdir(parents=True, exist_ok=True)
    base, delayed, source = prepare_source()
    report = {"schema": SCHEMA, "status": "running", "order": ORDER,
        "preRegisteredTrials": len(ORDER), "delayMs": DELAY_MS,
        "hypothesis": "A pre-ready delay while real Node and Chrome are live changes post-disconnect native-live Chromium survival",
        "fixtureSource": source, "interventionScope": "only pre-ready delay inside Operation; source-exact NUnit TearDown and OneTimeTearDown", "originalP47Unmodified": True, "originalP1BUnmodified": True, "driverFaultMechanism": "original os.kill(SIGTERM) after NUnit ready marker",
        "originalGateSeconds": ev.PASSIVE_WINDOW_SECONDS, "productFixProven": False,
        "historicalFailsRetained": True, "results": []}
    summary = OUT / "summary.json"
    try:
        with tempfile.TemporaryDirectory(prefix="p1c-source-exact-") as tmp:
            dirs = {}
            for arm, content in (("A", base), ("B", delayed)):
                work = Path(tmp) / arm
                work.mkdir()
                csproj = p37.CSPROJ.replace(
                    '<PackageReference Include="Selenium.WebDriver" Version="4.33.0" />', "")
                assert csproj != p37.CSPROJ and "Selenium.WebDriver" not in csproj
                (work / "Synthetic.Tests.csproj").write_text(csproj, encoding="utf-8")
                (work / "RealNUnitFixture.cs").write_text(content, encoding="utf-8")
                build = p37.run(["dotnet", "build", "-c", "Release", "--nologo"], work, timeout=360)
                assert build["code"] == 0, "P1C_BUILD_FAILED_"+arm+":"+str(build)[-1500:]
                deps = work / "bin/Release/net10.0/Synthetic.Tests.deps.json"
                assert deps.is_file() and "Selenium.WebDriver" not in deps.read_text(encoding="utf-8")
                dirs[arm] = work
            with ThreadingHTTPServer(("127.0.0.1", 0), p37.Handler) as server:
                threading.Thread(target=server.serve_forever, daemon=True).start()
                url = "http://127.0.0.1:"+str(server.server_port)+"/"
                try:
                    for ordinal, arm in enumerate(ORDER, 1):
                        path = OUT / f"trial-{ordinal:02d}-{arm}"
                        path.mkdir()
                        started = time.monotonic()
                        try:
                            result = p47.run_probe("disconnect_pass", "Passed", True,
                                dirs[arm], path, str(chrome), url)
                        except BaseException as e:
                            report["results"].append({"index": ordinal, "arm": arm,
                                "status": "failed_or_leak", "error": str(e),
                                "evidenceDirectory": path.name})
                            report["status"] = "stopped_at_first_failure"
                            raise
                        raw = json.loads((path / "process-disconnect_pass.json").read_text(encoding="utf-8"))
                        events = [json.loads(line) for line in
                            (path / "process-disconnect_pass.jsonl").read_text(encoding="utf-8").splitlines()]
                        def event(phase):
                            return next((x for x in events if x["phase"] == phase), None)
                        root = event("runner_exited")
                        signal = event("driver_termination_before_signal")
                        roles = sorted({(item.get("chromiumRole"), item.get("chromiumUtilitySubtype")) for event_row in events for item in event_row.get("processes", []) if item.get("kind") == "chrome"}, key=repr)
                        report["results"].append({
                            "chromiumRolesObserved": [{"role": r, "utilitySubtype": u} for r, u in roles],
                            "index": ordinal, "arm": arm, "status": "observed",
                            "originalPresenceGate": raw["originalPresenceGate"],
                            "survivors": raw["survivingObservedProcessCount"],
                            "nunit": raw["actual"], "driver": raw["driverTermination"],
                            "driverSignalUtc": signal["utc"] if signal else None,
                            "testRunnerExitedUtc": root["utc"] if root else None,
                            "elapsedFromSignalToRunnerExitSec": (
                                round(root["elapsedSec"]-signal["elapsedSec"], 3)
                                if root and signal else None),
                            "trxSha256": raw["trxSha256"],
                            "capturedProcessEvents": len(events),
                            "elapsedTotalSec": round(time.monotonic()-started, 3),
                            "evidenceDirectory": path.name})
                        ev.atomic_json(summary, report)
                        assert raw["originalPresenceGate"] == "passed" and raw["survivingObservedProcessCount"] == 0, "P1C_ORIGINAL_GATE_FAILED"
                finally:
                    server.shutdown()
        report["status"] = "completed_no_product_fix_claim"
    finally:
        ev.atomic_json(summary, report)
        ev.write_manifest(OUT)
    print("P1C_TIMING_AB_COMPLETE_NO_PRODUCT_FIX_CLAIM")
    print(json.dumps({"status": report["status"], "trials": len(report["results"]),
        "survivors": [x.get("survivors") for x in report["results"]]}, sort_keys=True))

if __name__ == "__main__":
    main()
