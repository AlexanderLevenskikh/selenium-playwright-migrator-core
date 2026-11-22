#!/usr/bin/env python3
"""P1-D, one fixed A/B pair of full P47 sequences using the original gate."""
import ctypes
import hashlib
import json
import os
from pathlib import Path
import tempfile
import threading
from http.server import ThreadingHTTPServer

import p1_preview47_job_postgate_proof as proof
import p1_preview47_source_exact_job_probe as exact
import p2_preview47_one_time_real_chromium as p47
import p2_preview47_process_evidence as evidence
import p2_preview47_windows_job_object_probe as jobs

ARMS = ("B", "A")


class StageJob:
    def __init__(self, arm, out):
        self.arm = arm
        self.out = out
        self.api = jobs.win32()
        self.ref = {"handle": self.api.CreateJobObjectW(None, None)}
        self.runner = None
        self.released = False
        self.late = {}

    def __enter__(self):
        handle = self.ref["handle"]
        exact.require(bool(handle), "P1D_CREATE_JOB")
        try:
            limits = jobs.ExtendedLimits()
            limits.BasicLimitInformation.LimitFlags = jobs.KILL_ON_JOB_CLOSE
            exact.require(self.api.SetInformationJobObject(
                handle, 9, ctypes.byref(limits), ctypes.sizeof(limits)),
                "P1D_CONFIGURE_JOB")
            exact.require(self.api.QueryInformationJobObject(
                handle, 9, ctypes.byref(limits), ctypes.sizeof(limits), None),
                "P1D_QUERY_JOB")
            self.flags = limits.BasicLimitInformation.LimitFlags
            exact.require(self.flags & jobs.KILL_ON_JOB_CLOSE and
                          not self.flags & (jobs.BREAKAWAY_OK | jobs.SILENT_BREAKAWAY_OK),
                          "P1D_JOB_FLAGS")
        except BaseException:
            self.api.CloseHandle(handle)
            raise
        return self

    def __exit__(self, *_):
        if self.ref["handle"]:
            exact.require(self.api.CloseHandle(self.ref["handle"]), "P1D_CLOSE_JOB")
        if self.runner is not None and self.runner.poll() is None:
            self.runner.kill()
            self.runner.communicate(timeout=15)

    def identity(self, pid, audit, expected_time, assign=False, creation_utc=None):
        rights = exact.ASSIGN_RIGHTS if assign else exact.CHILD_RIGHTS
        handle = self.api.OpenProcess(rights, False, pid)
        exact.require(bool(handle), "P1D_OPEN_PROCESS")
        try:
            native = audit.native.inspect_handle(handle, pid, expected_time)
            exact.require(native["state"] == "live", "P1D_NATIVE_LIVE_REQUIRED")
            job = self.ref["handle"]
            if assign:
                exact.require(evidence.cim_matches_native(creation_utc, native["creationFileTime"]),
                              "P1D_TESTHOST_FILETIME")
            if assign and self.arm == "B":
                exact.require(self.api.AssignProcessToJobObject(job, handle),
                              "P1D_ASSIGN_TESTHOST")
            native["inHostJob"] = jobs.member(self.api, handle, job)
            exact.require(native["inHostJob"] == (self.arm == "B"), "P1D_MEMBERSHIP")
            return native
        finally:
            self.api.CloseHandle(handle)

    def poll(self, process, data, precise, observed, audit, faulted):
        self.runner = process
        gate = self.out / "testhost-gate"
        if not self.released and gate.is_file():
            marker = gate.read_text(encoding="ascii")
            if not marker:
                return
            pid = int(marker)
            if pid not in data:
                return  # CIM snapshot may predate publication of the marker.
            exact.require(p47.p45.ancestry(data, pid, process.pid) is not None,
                          "P1D_TESTHOST_ANCESTRY")
            exact.require(not exact.owned(data, pid), "P1D_PRECREATE_CHILD_PRESENT")
            native = self.identity(pid, audit, None, assign=True,
                                   creation_utc=precise.get(pid, {}).get("creationUtc"))
            audit.report["testhostBeforeCreate"] = native
            audit.report["jobLimitFlags"] = self.flags
            audit.persist()
            (self.out / "job-assigned").write_text("released", encoding="ascii")
            self.released = True
        if faulted:
            for key, kind in exact.owned(data, process.pid).items():
                if key not in observed:
                    pid, start = key
                    native = audit.native.inspect(pid)
                    if native["state"] == "live" and evidence.cim_matches_native(
                            precise.get(pid, {}).get("creationUtc"),
                            native.get("creationFileTime")):
                        handle = self.api.OpenProcess(exact.CHILD_RIGHTS, False, pid)
                        if handle:
                            try:
                                checked = audit.native.inspect_handle(
                                    handle, pid, native["creationFileTime"])
                                if checked["state"] == "live":
                                    checked["inHostJob"] = jobs.member(
                                        self.api, handle, self.ref["handle"])
                                native = checked
                            finally:
                                self.api.CloseHandle(handle)
                    self.late[key] = dict(pid=pid, startIdentity=start, kind=kind,
                                          firstObservedAt=evidence.utc_now(),
                                          firstObservedPhase="runner_after_node_fault",
                                          nativeAtObservation=native)
            if self.late:
                audit.report["postFaultNewObserved"] = list(self.late.values())

    def before_fault(self, data, observed, audit):
        exact.require(self.released, "P1D_PRECREATE_HANDSHAKE_MISSING")
        verified = []
        for (pid, start), kind in sorted(observed.items()):
            filetime = audit.native_starts.get((pid, start))
            if not filetime or pid not in data or str(data[pid][1]) != start:
                continue
            native = audit.native.inspect(pid, filetime)
            if native["state"] == "live":
                row = self.identity(pid, audit, filetime)
                verified.append(dict(row, kind=kind, startIdentity=start))
        exact.require({row["kind"] for row in verified} == {"chrome", "driver"},
                      "P1D_PREFAULT_NODE_CHROME_NOT_PROVEN")
        audit.report["verifiedMembersBeforeFault"] = verified
        audit.persist()

    def after_gate(self, audit, observed, survivors):
        exact.require(self.released, "P1D_PRECREATE_HANDSHAKE_MISSING")
        audit.report["postFaultNewObserved"] = list(self.late.values())
        audit.report["unassistedSurvivorsBeforeHostClose"] = [
            dict(pid=pid, startIdentity=start, kind=observed[(pid, start)])
            for pid, start in sorted(survivors)]
        audit.report["stageContractVerified"] = True
        audit.persist()
        if self.arm == "B":
            audit.report["unassistedGateBeforeHostClose"] = audit.report["originalPresenceGate"]
            proof.prove_after_gate(
                self.api, audit.native, self.ref,
                audit.report.get("verifiedMembersBeforeFault", []),
                [dict(pid=pid, startIdentity=start, kind=observed[(pid, start)])
                 for pid, start in sorted(survivors)], audit.report,
                audit.persist, jobs.member)


def main():
    exact.require(os.name == "nt" and ctypes.sizeof(ctypes.c_void_p) == 8,
                  "P1D_REQUIRES_WINDOWS_X64")
    repo = Path(__file__).resolve().parent.parent
    out = repo / "artifacts/pom-preview47-p1d"
    out.mkdir(parents=True, exist_ok=False)
    chrome = Path(os.environ["MIGRATOR_CHROME_BIN"]).resolve()
    _, fixture, fixture_proof = exact.source_exact_fixture(repo)
    budget = {"arms": ARMS, "stages": p47.STAGES, "maxStageLaunches": 10,
              "retries": 0, "gateSeconds": 12, "fixture": fixture_proof,
              "membership": {"B": "assigned_before_Playwright_CreateAsync",
                             "A": "never_assigned_to_experiment_job"},
              "orderRationale": "Run B first so an uncontained A survivor cannot contaminate B.",
              "chromeSha256": hashlib.sha256(chrome.read_bytes()).hexdigest()}
    evidence.atomic_json(out / "plan.json", budget)
    try:
        with tempfile.TemporaryDirectory(prefix="p47-sequence-job-ab-") as directory:
            run_sequence(out, chrome, fixture, Path(directory))
    finally:
        evidence.write_manifest(out)


def run_sequence(out, chrome, fixture, runtime):
    package = '<PackageReference Include="Selenium.WebDriver" Version="4.33.0" />'
    exact.require(package in exact.p37.CSPROJ, "P1D_PROJECT_TEMPLATE_CHANGED")
    (runtime / "Synthetic.Tests.csproj").write_text(
        exact.p37.CSPROJ.replace(package, ""), encoding="utf-8")
    (runtime / "RealNUnitFixture.cs").write_bytes(fixture.encode("utf-8"))
    build = exact.p37.run(["dotnet", "build", "-c", "Release", "--nologo"],
                          runtime, timeout=360)
    evidence.atomic_json(out / "build.json", build)
    exact.require(build["code"] == 0, "P1D_NUNIT_BUILD")
    server = ThreadingHTTPServer(("127.0.0.1", 0), exact.p37.Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    url = f"http://127.0.0.1:{server.server_port}/"
    rows = []
    try:
        for arm in ARMS:
            for index, (stage, outcome, disconnect) in enumerate(p47.STAGES, 1):
                stage_out = out / arm / f"{index}-{stage}"
                stage_out.mkdir(parents=True)
                row = dict(arm=arm, stage=stage, status="running")
                rows.append(row)
                evidence.atomic_json(out / "stages.json", rows)
                try:
                    with StageJob(arm, stage_out) as job:
                        p47.run_probe(stage, outcome, disconnect, runtime, stage_out,
                                      str(chrome), url, job)
                    row.update(status="passed", originalPresenceGate="passed",
                               nativeLiveChrome=[])
                except Exception as error:
                    receipt = stage_out / f"process-{stage}.json"
                    report = json.loads(receipt.read_text(encoding="utf-8")) if receipt.is_file() else {}
                    gate = report.get("originalPresenceGate")
                    classified = report.get("nativeClassificationOfOriginalPresence", {})
                    live = [item for item in classified.get("live", [])
                            if any(process["pid"] == item["pid"] and process.get("kind") == "chrome"
                                   for process in report.get("finalObservedProcesses", []))]
                    fifth_gate_failure = index == 5 and report.get("stageContractVerified") and gate == "failed"
                    row.update(status="fifth_stage_gate_failure" if fifth_gate_failure
                               else "prerequisite_failure", error=str(error),
                               nativeLiveChrome=live, originalPresenceGate=gate)
                finally:
                    evidence.write_manifest(stage_out)
                    evidence.atomic_json(out / "stages.json", rows)
                if row["status"] == "prerequisite_failure":
                    break
            if rows[-1]["status"] == "prerequisite_failure":
                break
    finally:
        server.shutdown()
        server.server_close()
        evidence.atomic_json(out / "stages.json", rows)
    control = next((row for row in rows if row["arm"] == "A"
                    and row["stage"] == "disconnect_primary_repeat"), {})
    if any(row["status"] == "prerequisite_failure" for row in rows):
        result = "prerequisite_failure"
    elif control.get("nativeLiveChrome"):
        result = "native_live_control_reproduced_inspect_treatment_receipt"
    else:
        result = "inconclusive_no_native_live_control_failure"
    evidence.atomic_json(out / "result.json", {"status": result, "fixClaim": False})
    print(result)
    exact.require(all(row["status"] == "passed" for row in rows), result)


if __name__ == "__main__":
    main()
