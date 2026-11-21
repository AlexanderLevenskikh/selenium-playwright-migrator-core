"""Fail-preserving host Job containment proof for *observed* real Windows children.

All calls happen AFTER an unchanged 12-second unassisted presence gate.
Native process handles grant no terminate rights; only the separately owned
JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE Job may end the processes.
The unassisted gate result is immutable: a post-gate kill cannot make it PASS.
"""

SCHEMA = "migrator-preview47-postgate-real-job-proof/v1"
CHILD_RIGHTS = 0x00100000 | 0x1000  # SYNCHRONIZE | QUERY_LIMITED_INFORMATION
HOLD_MILLISECONDS = 300  # separate post-gate control, NEVER extends original gate
CLOSE_WAIT_MILLISECONDS = 8000


def require(condition, code):
    if not condition:
        raise AssertionError(code)


def prove_after_gate(api, native, job_ref, members, survivors, report, persist, is_member):
    """Verify exact native-live members, hold, close Job and observe their exits.

    job_ref={"handle": owned host HANDLE} becomes None after normal close.
    Caller retains emergency close ownership if a check raises. This function
    may only run AFTER the original source/fault/unassisted gate was persisted.
    """
    original_status = report.get("unassistedGateBeforeHostClose")
    require(original_status in ("passed", "failed"), "P47_HOST_GATE_NOT_COMPLETED")
    require(bool(survivors) == (original_status == "failed"),
            "P47_HOST_GATE_RESULT_DISAGREES_WITH_SURVIVORS")
    require(job_ref.get("handle") is not None, "P47_HOST_JOB_NOT_OWNED")
    report["postGateHostCleanup"] = {
        "schema": SCHEMA,
        "scope": "actual_source_attested_job_members_only",
        "originalGate": original_status,
        "originalGateReclassified": False,
        "observedSurvivorCount": len(survivors),
        "status": "pending",
        "actionsBeforeGate": "none",
        "terminationAuthority": "last_host_owned_job_handle_close_only",
    }
    persist()
    proof = report["postGateHostCleanup"]
    if not survivors:
        proof["status"] = "no_survivor_not_a_remediation_proof"
        proof["hostTerminatedRealSurvivor"] = False
        persist()
        require(api.CloseHandle(job_ref["handle"]), "P47_HOST_JOB_CLOSE_FAILED")
        job_ref["handle"] = None
        proof["jobClosedAfterGate"] = True
        persist()
        return False

    by_key = {(m["pid"], m["startIdentity"]): m for m in members}
    handles = []
    try:
        for survivor in survivors:
            pid, start = survivor["pid"], survivor["startIdentity"]
            prior = by_key.get((pid, start))
            require(prior is not None and prior.get("inHostJob") is True,
                    "P47_HOST_SURVIVOR_NOT_PROVEN_BEFORE_DRIVER_LOSS")
            require(prior.get("creationFileTime"),
                    "P47_HOST_SURVIVOR_MISSING_NATIVE_IDENTITY")
            handle = api.OpenProcess(CHILD_RIGHTS, False, pid)
            require(bool(handle), "P47_HOST_SURVIVOR_NATIVE_HANDLE_UNAVAILABLE")
            handles.append((survivor, prior, handle))
            before = native.inspect_handle(handle, pid, prior["creationFileTime"])
            require(before["state"] == "live" and
                    before.get("creationFileTime") == prior["creationFileTime"],
                    "P47_HOST_SURVIVOR_NOT_SAME_LIVE_PROCESS")
            require(is_member(api, handle, job_ref["handle"]),
                    "P47_HOST_SURVIVOR_NO_LONGER_IN_JOB")
        proof["verifiedBeforeClose"] = [
            {"pid": s["pid"], "startIdentity": s["startIdentity"],
             "kind": s["kind"], "creationFileTime": p["creationFileTime"],
             "isProcessInJob": True, "nativeState": "live"}
            for s, p, _ in handles]
        proof["status"] = "verified_live_before_host_close"
        persist()
        # Negative control: these SAME real Chrome processes must stay alive
        # while the host retains the Job. Natural exit => unproven, not a pass.
        for survivor, prior, handle in handles:
            wait = api.WaitForSingleObject(handle, HOLD_MILLISECONDS)
            require(wait == 258, "P47_HOST_SURVIVOR_EXITED_BEFORE_JOB_CLOSE")
            still_live = native.inspect_handle(
                handle, survivor["pid"], prior["creationFileTime"])
            require(still_live["state"] == "live",
                    "P47_HOST_SURVIVOR_NOT_LIVE_AT_JOB_CLOSE")
        proof["controlHoldMillisecondsPerSurvivor"] = HOLD_MILLISECONDS
        proof["nativeLiveAfterHold"] = True
        proof["status"] = "native_live_held_until_close"
        persist()
        require(api.CloseHandle(job_ref["handle"]), "P47_HOST_JOB_CLOSE_FAILED")
        job_ref["handle"] = None
        proof["jobClosedAfterGate"] = True
        proof["status"] = "job_closed_waiting_on_native_handles"
        persist()
        exits = []
        for survivor, prior, handle in handles:
            wait = api.WaitForSingleObject(handle, CLOSE_WAIT_MILLISECONDS)
            state = native.inspect_handle(
                handle, survivor["pid"], prior["creationFileTime"])
            exits.append({"pid": survivor["pid"],
                          "startIdentity": survivor["startIdentity"],
                          "creationFileTime": prior["creationFileTime"],
                          "waitResult": int(wait), "nativeState": state["state"]})
            proof["observedExitsAfterJobClose"] = exits
            persist()
            require(wait == 0 and state["state"] == "exited",
                    "P47_HOST_SURVIVOR_DID_NOT_EXIT_AFTER_JOB_CLOSE")
        proof["status"] = "real_survivors_terminated_after_failed_unassisted_gate"
        proof["hostTerminatedRealSurvivor"] = True
        proof["unassistedGateStillFailed"] = True
        persist()
        require(report["unassistedGateBeforeHostClose"] == "failed",
                "P47_HOST_CLEANUP_RECLASSIFIED_ORIGINAL_FAILURE")
        return True
    finally:
        for _survivor, _prior, handle in handles:
            api.CloseHandle(handle)
