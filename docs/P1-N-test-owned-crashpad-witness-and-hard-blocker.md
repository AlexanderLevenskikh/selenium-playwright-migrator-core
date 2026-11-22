# P1-N — test-owned Crashpad handler witness; production root cause still UNKNOWN

This is an independent research-only follow-up to P1-M Core #76 at f099b60fdb9c6b82f902bf067df215c0c49342e0. It DOES NOT fix or touch the original Windows preview.47 test, genuine Node os.kill(SIGTERM), NUnit lifecycle, original failure precedence, PID + startIdentity + native FILETIME pairing, original 12-second gate, historical evidence ZIPs or any original CI job.

## Real original Windows P47 archives independently revalidated

All hashes below refer to immutable original public GitHub artifacts, not new synthetic tests.

| Original archive | Original ZIP SHA256 | CRC / internal manifests | Actual original failure |
| --- | --- | --- | --- |
| P1-J #14753824405, run #45020707917 | 959dd70aad15203d12f2d1164b0ec0e021b1b79f5241cd8b719094637c8a439b | PASS / 94 of 94 | cycle-002 disconnect_primary: Crashpad PID 3436 native FILETIME 134398240007207002, utility PID 8080 native FILETIME 134398240009020979, both live WaitForSingleObject(0)=258 |
| P1-J repeat #14754039147, run #45021742696 | 19eb70aa794bf8828e13d0c14c3db855f0897e0af50aff39a6db42974839ad46 | PASS / 200 of 200 | cycle-004 disconnect_primary_repeat: Crashpad PID 9288 FILETIME 134398242040452709, StorageService PID 10072 FILETIME 134398242042097957, both native-LIVE |
| P1-F A #14745840024, run #44999689324 | 96c805f7913991a6ab60fe18e7b26ed52ac7c5689e312156c3b20fd4fb8e8abe | PASS / 218 of 218 | disconnect_primary_repeat: Crashpad PID 9480 FILETIME 134398073839406434 live without StorageService survivor |
| New P1-M non-reproduction #14758935667, run #45032945625 | 051a8df0983e4ed4a923cda627af2ae370efacd7ee7070288d47f4e810d14d4d | PASS / 12 of 12 | no original native-LIVE gate failure in this run only; NOT a repair |

Original Windows job #137202737613 for P1-J explicitly configured Chrome and ChromeDriver 156.0.8078.80. P1-J PID 3436 reported initialClientData=present, pipeName=absent. P1-J repeat PID 9288 reported initialClientData=present, pipeName=absent, monitorSelf=absent. These process-switch observations are source-bound but not private server-state attestations. Host cleanup was not attempted during the original gate. The source original ZIPs have not been edited, replaced or republished.

## Exact source-backed causal boundary

Chromium 156.0.8078.80, vendor Crashpad 53c4dcfae02976fe5c1b0b9e5347914b1ccfcd9f:
- components/crash/core/app/crashpad_win.cc, Git blob c5b54c43492be1d1c36af79620d1dc965534dae7: initial Chromium client starts the handler and passes CHROME_CRASHPAD_PIPE_NAME to children. Non-initial Chrome clients call CrashpadClient::SetHandlerIPCPipe.
- third_party/crashpad/crashpad/client/crashpad_client_win.cc, blob 3b4b34b5a6ba74c6dda112b6d129f68b13006a1e: that method registers the child's real process ID.
- third_party/crashpad/crashpad/util/win/exception_handler_server.cc, blob 8af53a62b088fececd558f1436a84d4c2c4feabd: initial and later registrations create private ClientData and wait on process handles. OnProcessEnd only POSTS to an I/O completion port; the main loop CONSUMES, ERASES and exits only when NOT persistent and zero registered clients remain.
- third_party/crashpad/crashpad/handler/handler_main.cc, blob 072176d161de2dd9e45388daa61b67add1cbd497: persistent mode depends on pipe-name command option, uploader exists only if URL configured.
- third_party/crashpad/crashpad/handler/crash_report_upload_thread.cc, blob bec8714263779d57bfff5ba83cfe26776acf3732: only actual UploadReport activity can attest an upload in flight.

A remaining registered client is a source-backed possible reason for the non-persistent handler to remain live after its initial Chrome parent exits. But the original ZIPs do not identify registered clients, whether OnProcessEnd was posted and consumed, internal persistent_ at the actual gate, in-flight upload, or exact shipped handler binary digest/build flags. StorageService, monitor-self and pipe-name flags are NOT necessary universal causes. A product root cause remains UNKNOWN.

## Minimal test-owned instrumentation overlay

Run the following command only against an ALREADY PRESENT exact Chromium 156.0.8078.80 checkout with the six P1-M Git blob pins and the independently pinned upload-thread blob. No network, privileges or client data access is performed by this tool.

    python scripts/p1n_prepare_crashpad_test_witness.py --tagged-chromium EXACT_TAGGED_SOURCE --new-overlay FRESH_DISPOSABLE_OUTPUT

The script fails if a source blob, anchor, destination isolation or expected file set differs. It produces three files in a NEW overlay: ExceptionHandlerServer.cc, crash_report_upload_thread.cc and a test-only header; plus a source/overlay SHA256 receipt. NEVER copy the overlay into the production repository or original P47. The original checkout remains byte-for-byte unchanged.

With an explicit separate Chromium build defining MIGRATOR_P1_TEST_OWNED_WITNESS and setting a private MIGRATOR_P1_TEST_WITNESS_FILE path, instrumentation records only:
- handler process PID and native FILETIME, monotonic ticks and bounded event sequence;
- initial and pipe client registration PID/native FILETIME and count;
- internal persistent-mode bit; OnProcessEnd fired, completion-port post success/failure, consumption and ClientData erasure;
- actual upload_begin and upload_end around UploadReport, not merely the presence of a URL.

No raw process HANDLE value, pipe name, command line, dump/database path, upload URL, browser content, or customer identifier is logged. The 512-event bound may truncate the trace, so a missing/partial gate, log or event MUST be UNKNOWN; it cannot serve as a negative proof. Instrumentation file access and timing also may perturb the experiment. A separately built test-owned Chromium is NOT the original shipped binary.

**Current hard blocker:** the overlay is not built into Chromium and no instrumented handler has been executed. The historical stock Chrome binary hash was not attested. Therefore direct registered-client identity, callback consumption, persistent_ runtime state and active uploads for original P47 are still UNKNOWN. An overlay recipe or passing synthetic tests is NOT a direct crashpad-state observation.

## Single next authorized causal experiment

1. Build the separate test-owned Chromium 156.0.8078.80 with the exact overlay, pinned MSVC/SDK/GN/ninja toolchain and complete binary SHA256. Independently validate emitted event file, exact handler PID/native FILETIME and source flags without private paths or raw handles. A build/prerequisite failure must be recorded and stopped.
2. On a fixed independent Windows runner, execute ONE predeclared source-equivalent five-stage real Node SIGTERM + NUnit sequence and the unchanged 12-second observation gate. Stop at first original FAIL, never retry green/red merely for status. Do not alter the original P47 script. If no same-PID/native-FILETIME real native-LIVE survivor exists at the gate, report NOT_OBSERVED and do not call this a fix.
3. If an actual native-LIVE survivor is captured with complete test-owned registered-client/callback/upload witness, decide its exact cause before touching product-owned cleanup. A test-owned observation is source evidence for that build only; a product repair must separately demonstrate an actual pre-fix native-LIVE FAIL and post-fix original source-exact gate PASS, without host-owned Job Object substitution.
4. Require the full unchanged 28-job Core CI AND exact Core-pinned 2-job MCP/Skill CI of any real product candidate. Do not merge/release unless both succeed and the actual cleanup cause/fix evidence is accepted.

Issue #59 remains OPEN. NO FIX CLAIM, no original P47 change, no privileged ETW/foreign handle enumeration, NO MERGE, NO RELEASE.
