# P1-J — Crashpad/StorageService lifetime hypothesis, NOT A FIX

Stacked on draft Core #67 at exact `91f8e160cb73584025ef4b78f26275648f062b68`. This branch changes neither the original P47 script (canonical LF SHA256 `53e4fa5babf70dfd7434cfc813cde685be1a77fbbe814d366c154005325632bc`; Windows CRLF checkout SHA256 `fb4f7287c93e5406ff5616afa67ab10d041ab303476a25547ff3815b6e7bb8b4`) nor NUnit, Node SIGTERM, the 12-second gate, Job membership/close, the fixed fail-fast plan, or the full CI matrix.

## Existing independently verified control ZIPs

- Original P1-H #14747793425 — SHA256 `8324128313000b8ebbeffb289c2a1d7a210fe6217407efa98b13ff43d0e5fd8e`. On original A `disconnect_pass`, 3 source-linked **native-LIVE** Chromium descendants (crashpad PIDs 5028, 9800; StorageService 10200) survived the original 12-second gate. The original root 3472 and Node 4120 were independently signaled **exited** at the gate. SIGTERM and failing original presence check are both recorded; no host browser cleanup occurred.
- Separate P1-H B #14748260739 — SHA256 `a4f416c3d0ab54b340490428165fca7838d04e890b9121ff37623d6ba8ccdd99`. 27/27 native source-linked members including crashpad and StorageService exited **before** NUnit exit, 69.5–105.3ms after fault for the Chromium root/crashpad samples. Job handle still held, so no Job-close proof.
- Original P1-I #14749126920 — SHA256 `ef911c0ebb047243aecd9d09c753c7b0b460841b4cb2c367df2e71f3cbbcdecf`. A new original P47 `disconnect_primary_repeat` native-LIVE failure at PID 820, 2652 (crashpad) and 3788 (StorageService). Separate P1-I A #14749092658 did not reproduce and did not adopt any process.

**Causal limit:** These were distinct runner instances. A/B difference, natural B exit and source-owned orphaning are observed, but none proves why the Windows helpers persist or that `KILL_ON_JOB_CLOSE` removes the actual orphan. Issue #59 remains OPEN.

## Mechanistic hypothesis to check

Chromium's Windows Crashpad handler supports `--initial-client-data` and `--pipe-name`, and has self-monitoring/periodic-task modes. The handler's lifetime depends on registered client handles and any pending uploads, not merely the immediate browser parent PID. Sources:
- https://github.com/chromium/crashpad/blob/main/handler/crashpad_handler.md
- https://chromium.googlesource.com/chromium/src/+/main/chrome/app/chrome_exe_main_win.cc

This offers a **testable hypothesis**, not a confirmed cause: original Node loss and root Chrome exit may strand one or more Crashpad client/pipe registrations or other helper shutdown dependencies. StorageService lifetime may depend on an independent Mojo lifetime; its specific cause is UNKNOWN.



### More specific, source-backed discriminator

Chromium's embedded Windows Crashpad code launches `chrome.exe --type=crashpad-handler` and may request `--monitor-self`; upstream Crashpad documents that `--monitor-self` spawns **a second handler** to monitor the first, and the child carries `--no-periodic-tasks`. See:
- https://chromium.googlesource.com/chromium/src/+/HEAD/components/crash/core/app/crashpad_win.cc
- https://chromium.googlesource.com/crashpad/crashpad/+/HEAD/handler/crashpad_handler.md

Historical original P1-H observed **PID 5028 → PID 9800**, both native-LIVE Crashpad; this topology is *consistent* with self-monitoring, but those historical ZIPs contain no sanitized `--monitor-self` switch evidence. P1-J now records its **presence only**, alongside child `--no-periodic-tasks`, so subsequent already-scheduled original P47 CI may discriminate this hypothesis. The complete P47 process identities, root exit, Crashpad client registration/handle closure and StorageService behavior remain separate; neither matching switches nor a green experiment proves product cleanup.

## Minimal read-only diagnostic implemented

The existing **single** CIM process snapshot now records four **presence-only** switches for *source-owned* `chrome.exe --type=crashpad-handler`: `--initial-client-data`, `--pipe-name`, `--no-periodic-tasks`, `--monitor-self`. Values, pipe names, process command lines, paths, profiles, URLs and client payloads are **never emitted**. A missing command line or a legacy archive is `unknown`, never `absent`. These flags propagate through exact PID+creation FILETIME observations. No additional polling, Win32 rights, process control, Job assignment, retry or changed gate.

The original 10-cycle diagnostic plan remains original fail-fast and does not continue after first original FAIL. Linux behavior is unchanged. New portable negative tests cover allowlisted statuses, raw-data rejection, source SHA identity and failure to infer missing metadata.

**Next discriminating experiment (not silently launched):** compare actual P47 A native-LIVE crashpad switch modes at ready, immediately pre-fault, post-fault and post-gate with equivalent B roles; if still inconclusive, add an explicitly authorized bounded read-only ETW process/handle capture for client exit/pipe closure ordering. No PID-only joins, forced orphan, registry changes or artificial PASS. If a B same-process native-LIVE orphan never survives through the gate, last-handle-close proof remains NOT_OBSERVED.

**NO FIX CLAIM; NO MERGE; NO RELEASE; PR stays draft.**


### OS newline boundary in the original P47 provenance

The original Python harness in Core #67 has identical Git **blob** across P1-I and P1-J. Linux CI reads LF bytes (SHA256 `53e4fa5babf70dfd7434cfc813cde685be1a77fbbe814d366c154005325632bc`); Windows checkout uses CRLF bytes, historically pinned and proven by P1-I (SHA256 `fb4f7287c93e5406ff5616afa67ab10d041ab303476a25547ff3815b6e7bb8b4`). The P1-J test checks **both** exact raw per-EOL SHA and normalized-LF SHA. No P47 source bytes, SIGTERM logic or gate changed in this PR. The initial P1-J Ubuntu failure on SHA `3e0a154a` was a too-narrow Windows-only raw-hash test, not a source change or P47 remediation outcome.
