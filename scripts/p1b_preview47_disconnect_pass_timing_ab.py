#!/usr/bin/env python3
"""P1-B: preregistered 8-trial ABBA/BAAB source-exact P47 disconnect_pass timing comparison.
A: original NUnit fixture. B: ONLY a 1500ms delay after real Node-kill ACK,
before NUnit TearDown. Identical source-derived TearDown/OneTimeTearDown.
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
SCHEMA = "migrator-p1b-p47-disconnect-pass-timing-ab/v1"
ANCHOR = '        if(Stage=="disconnect_sink_primary") {'
ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "artifacts/p1b-disconnect-pass-timing"

def prepare_source():
    one_source = (ROOT / "Migrator.Application.Smoke/PomPreview47OneTimeCleanupSmoke.cs").read_text(encoding="utf-8")
    tear_source = (ROOT / "Migrator.Application.Smoke/PomPreview46TypedTargetClosedSmoke.cs").read_text(encoding="utf-8")
    one = p42.synthetic("OneTimeTearDown", one_source)
    tear = p42.synthetic("TypedTearDown", tear_source)
    assert "TargetClosedException" in one and "TargetClosedException" in tear
    original = p47.TEMPLATE.replace("__TEARDOWN_SOURCE__", tear).replace("__ONE_TIME_SOURCE__", one)
    assert original.count(ANCHOR) == 1, "P1B_TIMING_ANCHOR_NOT_UNIQUE"
    assert original.count("ready-"+chr(34)) == 1, "P1B_ORIGINAL_ACK_HANDSHAKE_UNPROVEN"
    variant = original.replace(ANCHOR, "        await Task.Delay("+str(DELAY_MS)+");\n"+ANCHOR)
    assert variant.replace("        await Task.Delay("+str(DELAY_MS)+");\n", "", 1) == original
    return original, variant, {"fixtureSha256A": hashlib.sha256(original.encode()).hexdigest(),
        "fixtureSha256B": hashlib.sha256(variant.encode()).hexdigest(),
        "tearDownSourceSha256": hashlib.sha256(tear.encode()).hexdigest(),
        "oneTimeTearDownSourceSha256": hashlib.sha256(one.encode()).hexdigest(),
        "singleDelta": "await Task.Delay(1500) after driver-killed ACK before NUnit TearDown"}

def main():
    assert os.name == "nt", "P1B_WINDOWS_ONLY"
    chrome = Path(os.environ["MIGRATOR_CHROME_BIN"]).resolve()
    assert chrome.is_file() and os.environ.get("MIGRATOR_CHROME_VERSION") == "156.0.8078.80"
    OUT.mkdir(parents=True, exist_ok=True)
    base, delayed, source = prepare_source()
    report = {"schema": SCHEMA, "status": "running", "order": ORDER,
        "preRegisteredTrials": len(ORDER), "delayMs": DELAY_MS,
        "hypothesis": "Allowing Chrome 1.5s after Node fault before NUnit teardown changes orphan survival",
        "fixtureSource": source, "originalP47Unmodified": True,
        "originalGateSeconds": ev.PASSIVE_WINDOW_SECONDS, "productFixProven": False,
        "historicalFailsRetained": True, "results": []}
    summary = OUT / "summary.json"
    try:
        with tempfile.TemporaryDirectory(prefix="p1b-source-exact-") as tmp:
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
                assert build["code"] == 0, "P1B_BUILD_FAILED_"+arm+":"+str(build)[-1500:]
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
                        report["results"].append({
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
                        assert raw["originalPresenceGate"] == "passed" and raw["survivingObservedProcessCount"] == 0, "P1B_ORIGINAL_GATE_FAILED"
                finally:
                    server.shutdown()
        report["status"] = "completed_no_product_fix_claim"
    finally:
        ev.atomic_json(summary, report)
        ev.write_manifest(OUT)
    print("P1B_TIMING_AB_COMPLETE_NO_PRODUCT_FIX_CLAIM")
    print(json.dumps({"status": report["status"], "trials": len(report["results"]),
        "survivors": [x.get("survivors") for x in report["results"]]}, sort_keys=True))

if __name__ == "__main__":
    main()
