"""P1-H native lifetime witness for the UNMODIFIED original P47.

This is read-only Windows process observation. It never signals, suspends,
restarts or changes the original Node/Chrome/NUnit processes. A separate
25-ms observer samples SYNCHRONIZE|QUERY-only retained handles, allowing
an exact FILETIME identity to be followed after the process leaves Toolhelp.
An original source-owned timeline is joined only AFTER its 12-s gate returns.
"""
import ctypes
from datetime import datetime, timezone
import json
from pathlib import Path
import threading

import p1_preview47_native_tree as native_tree
import p2_preview47_windows_job_object_probe as jobs

SCHEMA = "migrator-preview47-p1h-native-lifetime/v1"
POLL_SECONDS = 0.025
MAX_HANDLES = 96


def duration_ms(before, after):
    if not before or not after:
        return None
    return round((datetime.fromisoformat(after) - datetime.fromisoformat(before)).total_seconds() * 1000, 1)


def source_lifetime_join(tracks, stages, directory):
    """Source-linked roles and phase timing; never join on PID without FILETIME."""
    directory = Path(directory)
    results = []
    for stage, _expected, disconnect in stages:
        if not disconnect:
            continue
        path = directory / ("process-" + stage + ".jsonl")
        receipt = directory / ("process-" + stage + ".json")
        if not path.is_file() or not receipt.is_file():
            continue
        events = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]
        fault = next((e["utc"] for e in events if e["phase"] == "driver_termination_before_signal"), None)
        runner = next((e["utc"] for e in events if e["phase"] == "runner_exited"), None)
        stage_status = json.loads(receipt.read_text(encoding="utf-8"))
        original = {}
        for event in events:
            if event["phase"] != "runner_active" or not fault or event["utc"] >= fault:
                continue
            for p in event.get("processes", []):
                n = p.get("native") or {}
                filetime = n.get("expectedCreationFileTime")
                if (p.get("kind") in ("driver", "chrome") and filetime
                        and p.get("presentWithOriginalIdentity") and n.get("state") == "live"):
                    original[(p["pid"], str(filetime))] = p
        entries = []
        for t in tracks:
            if t["stage"] != stage:
                continue
            p = original.get((t["pid"], str(t["creationFileTime"])))
            end = t.get("firstNativeExitUtc")
            last = t.get("lastNativeLiveUtc")
            if not p or not fault or not t["firstNativeLiveUtc"] < fault:
                source_state = "source_or_prefault_identity_unattested"
            elif end and runner and fault < end < runner:
                source_state = "native_exit_after_fault_before_runner_exit"
            elif end and fault and end <= fault:
                source_state = "native_exit_before_or_at_fault"
            elif end and runner and end >= runner:
                source_state = "native_exit_at_or_after_runner_exit"
            elif not end and runner and last and last >= runner:
                source_state = "native_live_at_or_after_runner_exit"
            else:
                source_state = "exit_timing_unknown"
            entries.append({
                "pid": t["pid"], "creationFileTime": str(t["creationFileTime"]),
                "kind": t["kind"], "sourceLinked": bool(p),
                "chromiumRole": p.get("chromiumRole") if p else None,
                "chromiumUtilitySubtype": p.get("chromiumUtilitySubtype") if p else None,
                "inHostJobBeforeFault": t["inHostJobAtFirstLive"],
                "firstNativeLiveUtc": t["firstNativeLiveUtc"],
                "lastNativeLiveUtc": last, "firstNativeExitUtc": end,
                "startToFaultMilliseconds": duration_ms(t["nativeStartUtc"], fault),
                "faultToNativeExitMilliseconds": duration_ms(fault, end),
                "timingClassification": source_state,
                "nativeAnomaly": t.get("nativeAnomaly"),
            })
        results.append({"stage": stage, "originalGate": stage_status.get("originalPresenceGate"),
                        "originalStatus": stage_status.get("status"),
                        "faultMarkerUtc": fault, "originalRunnerExitUtc": runner,
                        "nativeTracks": entries,
                        "allObservedTracksSourceLinked": bool(entries) and all(
                            e["sourceLinked"] for e in entries)})
    return results


class LifetimeObserver(native_tree.NativeTreeObserver):
    """One passive native wait loop outside Job; no original P47 JSON opens during writes."""
    def __init__(self, api, native, job, wrapper_pid, wrapper_filetime, arm,
                 source, stages, utc_now, child_rights):
        super().__init__(api, native, job, wrapper_pid, wrapper_filetime, arm,
                         source, stages, utc_now, child_rights)
        self._tracks = {}
        self._misses = []
        self._missed_keys = set()
        self._lock = threading.Lock()
        self._stop = threading.Event()
        self._closed = False
        self._worker_error = None
        self._thread = threading.Thread(target=self._watch_loop, daemon=True,
                                        name="p1h-native-lifetime-only")
        self._thread.start()

    def poll(self):
        super().poll()
        stage = self.active_stage()
        if not stage:
            return
        for sample in self.samples.get(stage, {}).values():
            key = (stage, sample["pid"], str(sample["creationFileTime"]))
            with self._lock:
                if key in self._tracks:
                    continue
            handle = self.api.OpenProcess(self.child_rights, False, sample["pid"])
            if not handle:
                if key not in self._missed_keys:
                    self._missed_keys.add(key)
                    self._misses.append({"stage": stage, "pid": sample["pid"],
                                         "reason": "native_handle_unavailable"})
                continue
            try:
                checked = self.native.inspect_handle(handle, sample["pid"],
                                                    str(sample["creationFileTime"]))
                if checked.get("state") != "live":
                    if key not in self._missed_keys:
                        self._missed_keys.add(key)
                        self._misses.append({"stage": stage, "pid": sample["pid"],
                                             "reason": "not_native_live_at_handle_acquire"})
                    continue
                track = {"stage": stage, "pid": sample["pid"], "kind": sample["kind"],
                         "creationFileTime": str(sample["creationFileTime"]),
                         "nativeStartUtc": filetime_utc(sample["creationFileTime"]),
                         "inHostJobAtFirstLive": sample["inHostJob"],
                         "firstNativeLiveUtc": self.utc_now(),
                         "lastNativeLiveUtc": self.utc_now(),
                         "firstNativeExitUtc": None, "nativeAnomaly": None,
                         "handle": handle}
                with self._lock:
                    if len(self._tracks) >= MAX_HANDLES:
                        raise AssertionError("P1H_NATIVE_HANDLE_BUDGET_EXCEEDED")
                    if key not in self._tracks:
                        self._tracks[key] = track
                        handle = None
            finally:
                if handle:
                    self.api.CloseHandle(handle)

    def _watch_loop(self):
        while not self._stop.wait(POLL_SECONDS):
            with self._lock:
                tracks = list(self._tracks.values())
            for item in tracks:
                if item["firstNativeExitUtc"] is not None or item["nativeAnomaly"]:
                    continue
                try:
                    native = self.native.inspect_handle(item["handle"], item["pid"],
                                                        item["creationFileTime"])
                except (OSError, AssertionError, ValueError) as error:
                    self._worker_error = str(error)
                    self._stop.set()
                    return
                now = self.utc_now()
                with self._lock:
                    if native["state"] == "live":
                        item["lastNativeLiveUtc"] = now
                    elif native["state"] == "exited":
                        item["firstNativeExitUtc"] = now
                    else:
                        item["nativeAnomaly"] = native["state"]

    def report(self):
        self._stop.set()
        self._thread.join(timeout=2)
        if self._thread.is_alive():
            raise AssertionError("P1H_NATIVE_OBSERVER_THREAD_DID_NOT_STOP")
        if self._worker_error:
            raise AssertionError("P1H_NATIVE_OBSERVER_ERROR: " + self._worker_error)
        with self._lock:
            tracks = [{k: v for k, v in entry.items() if k != "handle"}
                      for entry in self._tracks.values()]
        source_results = source_lifetime_join(tracks, self.stages, self.source)
        limits = jobs.ExtendedLimits()
        held = bool(self.api.QueryInformationJobObject(
            self.job, 9, ctypes.byref(limits), ctypes.sizeof(limits), None))
        if not held or limits.BasicLimitInformation.LimitFlags != jobs.KILL_ON_JOB_CLOSE:
            raise AssertionError("P1H_HOST_JOB_NOT_OWNED_AFTER_ORIGINAL_GATE")
        out = super().report()
        out.update(hostJobHandleHeldAfterOriginalGate=True,
                   hostJobLimitFlagsAtReport=int(limits.BasicLimitInformation.LimitFlags),
                   hostJobObservedUtc=self.utc_now(), schema=SCHEMA,
                   sampleIntervalMilliseconds=int(POLL_SECONDS * 1000),
                   observerModifiesProcesses=False, retainedProcessHandlesReadOnly=True,
                   nativeLifetimeTracks=tracks, sourceLinkedStages=source_results,
                   misses=self._misses, noRetry=True)
        return out

    def close(self):
        if self._closed:
            return
        self._stop.set()
        self._thread.join(timeout=2)
        if self._thread.is_alive():
            raise AssertionError("P1H_NATIVE_OBSERVER_THREAD_DID_NOT_STOP")
        with self._lock:
            handles = [t["handle"] for t in self._tracks.values()]
        for handle in handles:
            if not self.api.CloseHandle(handle):
                raise OSError(ctypes.get_last_error(), "P1H_CLOSE_OWNED_PROCESS_HANDLE")
        self._closed = True


def filetime_utc(value):
    timestamp = (int(value) - 116444736000000000) / 10000000
    return datetime.fromtimestamp(timestamp, tz=timezone.utc).isoformat(timespec="microseconds")
