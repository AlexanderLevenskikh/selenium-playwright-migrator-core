"""P1-I: strict post-gate Win32 Job adoption of an original native-LIVE A orphan.

No new failure injection, no pre-gate process action. This experiment is NOT
pre-create B mitigation. All processes must be source- and native-identity
attested before fault AND live beyond the unchanged original 12-second gate.
"""
import ctypes
from ctypes import wintypes
import json
from pathlib import Path

import p2_preview47_process_evidence as evidence
import p2_preview47_windows_job_object_probe as jobs

SCHEMA = "migrator-preview47-p1i-real-orphan-adoption/v1"
ADOPT_RIGHTS = 0x00100000 | 0x1000 | 0x0100 | 0x0001  # sync, query, set quota, terminate (assignment only)
HOLD_BEFORE_ASSIGN_MS = 300
HOLD_AFTER_ASSIGN_MS = 300
EXIT_WAIT_MS = 8000


class ProofBlocked(Exception):
    pass


def require(condition, code):
    if not condition:
        raise ProofBlocked(code)


class BasicAccounting(ctypes.Structure):
    _fields_ = [("TotalUserTime", ctypes.c_int64), ("TotalKernelTime", ctypes.c_int64),
                ("ThisPeriodTotalUserTime", ctypes.c_int64),
                ("ThisPeriodTotalKernelTime", ctypes.c_int64),
                ("TotalPageFaultCount", wintypes.DWORD),
                ("TotalProcesses", wintypes.DWORD),
                ("ActiveProcesses", wintypes.DWORD),
                ("TotalTerminatedProcesses", wintypes.DWORD)]


def verify_original_survivors(failure, source, wrapper, original):
    """Preflight ALL survivors without touching a process; fail closed on gaps."""
    require(failure.get("status") == "native_live_fail", "P1I_NOT_ORIGINAL_NATIVE_LIVE_FAIL")
    require(failure.get("disconnect") is True, "P1I_NOT_REAL_DISCONNECT_STAGE")
    require(failure.get("originalPresenceGate") == "failed", "P1I_ORIGINAL_GATE_NOT_FAILED")
    require(failure.get("prefaultMembership") == "attested", "P1I_PREFAULT_MEMBERSHIP_UNPROVEN")
    require(original.get("originalPresenceGate") == "failed" and
            original.get("status") == "failed" and original.get("realDriverKilled") is True,
            "P1I_ORIGINAL_FAULT_OR_GATE_UNVERIFIED")
    require(original.get("failure", "").startswith("P47_OWNED_OBSERVED_CHILD_PIDS_SURVIVED:"),
            "P1I_ORIGINAL_FAILURE_WRONG_TYPE")
    require(original.get("hostOwnedBrowserCleanup") == "not_attempted",
            "P1I_ORIGINAL_HOST_CLEANUP_ALREADY_ATTEMPTED")
    require(wrapper.get("inHostJob") is False and wrapper.get("creationFileTime"),
            "P1I_A_WRAPPER_NOT_UNCONTAINED")
    survivors = failure.get("nativeLiveChrome") or []
    originals = {(r["pid"], r["startIdentity"]) for r in
                 original.get("originalPresenceSurvivors", [])}
    keys = {(r["pid"], r["startIdentity"]) for r in survivors}
    require(bool(survivors) and len(keys) == len(survivors) and keys == originals,
            "P1I_ORIGINAL_SURVIVOR_SET_NOT_EXACT")
    joined = {(x["pid"], x["startIdentity"]): x for x in
              failure.get("verifiedBeforeFault", [])}
    owned = {(x["observedPid"], x["startIdentity"]): x for x in
             original.get("observedOwnedAncestry", [])}
    finals = {(x["pid"], x["startIdentity"]): x for x in
              original.get("finalObservedProcesses", [])}
    require(any(x.get("kind") == "driver" and x.get("sourceBackedBeforeFault")
                for x in joined.values()), "P1I_NO_SOURCE_ATTESTED_NODE")
    verified = []
    for survivor in survivors:
        key = survivor["pid"], survivor["startIdentity"]
        pre, origin, last = joined.get(key), owned.get(key), finals.get(key)
        require(pre is not None and origin is not None and last is not None,
                "P1I_SURVIVOR_MISSING_PREFAULT_OR_ORIGINAL_LINEAGE")
        filetime = str(survivor["creationFileTime"])
        require(pre.get("sourceBackedBeforeFault") is True and
                pre.get("inHostJob") is False and pre.get("kind") == "chrome" and
                str(pre.get("creationFileTime")) == filetime,
                "P1I_PREFAULT_NATIVE_IDENTITY_OR_MEMBERSHIP_UNATTESTED")
        path = pre.get("pathToWrapper") or []
        require(bool(path) and path[0].get("pid") == wrapper["pid"] and
                str(path[0].get("creationFileTime")) == str(wrapper["creationFileTime"]) and
                path[-1].get("pid") == survivor["pid"],
                "P1I_WIN32_ANCESTRY_TO_WRAPPER_UNPROVEN")
        original_path = origin.get("pathToTestProcess") or []
        require(origin.get("wasInBaselineWithOriginalIdentity") is False and
                str(origin.get("creationFileTime")) == filetime and
                any(p.get("kind") == "driver" for p in original_path),
                "P1I_ORIGINAL_P47_OWNERSHIP_UNPROVEN")
        nat = last.get("native") or {}
        require(last.get("kind") == "chrome" and
                last.get("executableMatchesConfiguredChrome") is True and
                last.get("presentWithOriginalIdentity") is True and
                nat.get("state") == "live" and nat.get("waitResult") == 258 and
                str(nat.get("creationFileTime")) == filetime and
                str(nat.get("expectedCreationFileTime")) == filetime,
                "P1I_POST_GATE_NATIVE_LIVE_IDENTITY_UNPROVEN")
        verified.append({"pid": survivor["pid"], "startIdentity": survivor["startIdentity"],
                         "creationFileTime": filetime, "kind": "chrome",
                         "chromiumRole": last.get("chromiumRole"),
                         "chromiumUtilitySubtype": last.get("chromiumUtilitySubtype"),
                         "sourceBackedBeforeFault": True, "inHostJobBeforeFault": False,
                         "observedOriginalPostGateNativeLive": True})
    return sorted(verified, key=lambda x: x["pid"])


def postgate_adopt(api, native, job_ref, failure, out, source_dir, wrapper):
    """Perform NO actions before a real, persisted original P47 native-live FAIL.

    Adoption is an OS mechanism experiment only. The original failed presence
    gate is immutable; even a successful Job-close yields no product fix claim.
    """
    out, source_dir = Path(out), Path(source_dir)
    report = {"schema": SCHEMA, "status": "pending", "arm": "A",
              "productPrecreateBMitigationProven": False, "fixClaim": False,
              "originalGateReclassified": False, "actionsBeforeOriginalGate": "none",
              "originalStage": failure.get("stage"),
              "originalGate": failure.get("originalPresenceGate"),
              "originalFailure": failure.get("originalFailure"),
              "postGateAssignmentOnly": True,
              "newHostJobWasEmptyBeforeAdoption": True,
              "terminationMethod": "last_host_job_handle_close_only",
              "noDirectTerminateProcess": True,
              "adoption": [], "nativeExitsAfterClose": []}
    receipt = out / "postgate-adoption.json"
    def persist():
        evidence.atomic_json(receipt, report)
    persist()
    handles = []
    try:
        stage = failure.get("stage")
        require(stage and (source_dir / ("process-" + stage + ".json")).is_file(),
                "P1I_ORIGINAL_STAGE_SOURCE_MISSING")
        original = json.loads((source_dir / ("process-" + stage + ".json")).read_text(encoding="utf-8"))
        require(job_ref.get("handle"), "P1I_HOST_JOB_NOT_OWNED")
        limits = jobs.ExtendedLimits()
        require(bool(api.QueryInformationJobObject(job_ref["handle"], 9,
                ctypes.byref(limits), ctypes.sizeof(limits), None)),
                "P1I_JOB_LIMIT_QUERY_FAILED")
        require(limits.BasicLimitInformation.LimitFlags == jobs.KILL_ON_JOB_CLOSE,
                "P1I_JOB_CLOSE_KILL_POLICY_UNVERIFIED")
        verified = verify_original_survivors(failure, source_dir, wrapper, original)
        report["verifiedOriginalSurvivors"] = verified
        report["status"] = "original_native_live_source_attested"
        persist()
        for row in verified:
            handle = api.OpenProcess(ADOPT_RIGHTS, False, row["pid"])
            require(bool(handle), "P1I_POSTGATE_PROCESS_HANDLE_UNAVAILABLE")
            handles.append((row, handle))
            state = native.inspect_handle(handle, row["pid"], row["creationFileTime"])
            require(state.get("state") == "live" and
                    str(state.get("creationFileTime")) == row["creationFileTime"],
                    "P1I_SURVIVOR_DIED_OR_REUSED_BEFORE_ADOPTION")
            row["inAnyExistingJobBeforeAdoption"] = jobs.member(api, handle, None)
            row["inExperimentJobBeforeAdoption"] = jobs.member(api, handle, job_ref["handle"])
            require(row["inExperimentJobBeforeAdoption"] is False,
                    "P1I_ALREADY_IN_EXPERIMENT_JOB")
        # Control: all the same original Chrome children remain live while the
        # brand-new host Job has no members. This is strictly *post-gate*.
        for row, handle in handles:
            wait = api.WaitForSingleObject(handle, HOLD_BEFORE_ASSIGN_MS)
            row["waitBeforeAdoption"] = int(wait)
            require(wait == 258 and native.inspect_handle(
                handle, row["pid"], row["creationFileTime"])["state"] == "live",
                "P1I_NATURAL_EXIT_BEFORE_ADOPTION")
        report["nativeLiveHeldBeforeAdoption"] = True
        report["holdBeforeAdoptionMsPerSurvivor"] = HOLD_BEFORE_ASSIGN_MS
        persist()
        for row, handle in handles:
            before = native.inspect_handle(handle, row["pid"], row["creationFileTime"])
            require(before.get("state") == "live", "P1I_EXIT_BEFORE_ASSIGN")
            if not api.AssignProcessToJobObject(job_ref["handle"], handle):
                error = ctypes.get_last_error()
                report["adoption"].append({"pid": row["pid"],
                                           "creationFileTime": row["creationFileTime"],
                                           "status": "win32_rejected", "win32Error": error,
                                           "memberOfAnyJobBefore": row["inAnyExistingJobBeforeAdoption"]})
                persist()
                raise ProofBlocked("P1I_WIN32_ASSIGN_REJECTED_" + str(error))
            member = jobs.member(api, handle, job_ref["handle"])
            state = native.inspect_handle(handle, row["pid"], row["creationFileTime"])
            report["adoption"].append({"pid": row["pid"],
                                       "creationFileTime": row["creationFileTime"],
                                       "status": "assigned", "inExperimentJob": member,
                                       "nativeStateAfterAssignment": state["state"],
                                       "memberOfAnyJobBefore": row["inAnyExistingJobBeforeAdoption"]})
            persist()
            require(member and state["state"] == "live", "P1I_ASSIGN_NOT_NATIVE_LIVE")
        accounting = BasicAccounting()
        require(ctypes.sizeof(BasicAccounting) == 48, "P1I_JOB_ACCOUNTING_LAYOUT_UNKNOWN")
        require(bool(api.QueryInformationJobObject(job_ref["handle"], 1,
                ctypes.byref(accounting), ctypes.sizeof(accounting), None)),
                "P1I_JOB_ACCOUNTING_QUERY_FAILED")
        report["activeJobProcessesBeforeClose"] = int(accounting.ActiveProcesses)
        require(accounting.ActiveProcesses >= len(handles),
                "P1I_ADOPTED_JOB_ACTIVE_COUNT_INCOMPLETE")
        for row, handle in handles:
            wait = api.WaitForSingleObject(handle, HOLD_AFTER_ASSIGN_MS)
            row["waitAfterAdoptionBeforeClose"] = int(wait)
            state = native.inspect_handle(handle, row["pid"], row["creationFileTime"])
            require(wait == 258 and state["state"] == "live" and
                    jobs.member(api, handle, job_ref["handle"]),
                    "P1I_NATURAL_EXIT_OR_MEMBERSHIP_LOSS_BEFORE_CLOSE")
        report["nativeLiveHeldAfterAdoptionUntilClose"] = True
        report["holdAfterAdoptionMsPerSurvivor"] = HOLD_AFTER_ASSIGN_MS
        report["status"] = "same_native_live_members_held_until_close"
        report["beforeCloseUtc"] = evidence.utc_now()
        persist()
        require(bool(api.CloseHandle(job_ref["handle"])), "P1I_CLOSE_LAST_HOST_JOB_FAILED")
        job_ref["handle"] = None
        report["lastHostJobHandleClosedUtc"] = evidence.utc_now()
        report["status"] = "job_closed_waiting_for_exits"
        persist()
        for row, handle in handles:
            wait = api.WaitForSingleObject(handle, EXIT_WAIT_MS)
            final = native.inspect_handle(handle, row["pid"], row["creationFileTime"])
            report["nativeExitsAfterClose"].append({
                "pid": row["pid"], "creationFileTime": row["creationFileTime"],
                "waitResult": int(wait), "nativeState": final["state"],
                "exitCode": final.get("exitCode")})
            persist()
            require(wait == 0 and final["state"] == "exited",
                    "P1I_NATIVE_EXIT_AFTER_JOB_CLOSE_NOT_PROVED")
        report["status"] = "real_orphan_exited_after_postgate_job_close"
        report["osMechanismProvedForAdoptedAOnly"] = True
        report["originalUnassistedGateStillFailed"] = True
        persist()
    except (ProofBlocked, OSError, AssertionError, ValueError, KeyError) as error:
        report.update(status="blocked_or_unproven", detail=str(error),
                      osMechanismProvedForAdoptedAOnly=False)
        persist()
    finally:
        for _row, handle in handles:
            api.CloseHandle(handle)
    return report
