# P1-K — source-owned native Crashpad activity, NOT a fix

This is a bounded read-only P1 follow-up to Core issue #59, stacked on draft
Core #71 at exact 142018c22dc29501eaa92cbc2b6314f841eb6792. The
original P47 Python source, NUnit contracts, true Node SIGTERM, original
runner-exit-anchored **12-second presence gate**, and first-FAIL stop are
byte-for-byte unchanged. The full original Linux/Windows CI matrix remains.

## Why measure these counters?

Official Crashpad documentation describes the Windows --initial-client-data
server as waiting for all registered clients to exit **and** any uploads in
progress to finish. Registration includes the client's process and crash-event
handles. The existing original P1-J Windows ZIPs #14753824405 and
#14754039147 show real native-LIVE crashpad-handler after root Chrome and
Playwright Node exit, with --initial-client-data present and --pipe-name
absent; --monitor-self also absent in the second FAIL. P1-F A observed a
crashpad-only orphan, so StorageService is not a necessary cause.

Sources:
- https://github.com/chromium/crashpad/blob/main/handler/crashpad_handler.md
- https://chromium.googlesource.com/crashpad/crashpad/+/main/util/win/exception_handler_server.h

The handler's **open handle count is not a registered-client count**;
process-wide I/O cannot identify network uploads, crash payloads or pending
requests. The presence of --url never certifies that uploads were enabled.
When --url is absent, uploads are disabled by default per Crashpad docs;
the source may opt into behavior independently. Do not infer an upload from
I/O counters or an unchanging handle count.

## Implementation and restrictions

On an existing source-owned live process handle **after exact native
GetProcessTimes creation FILETIME + WaitForSingleObject(0)=258**, capture
only kernel/user CPU cumulative 100ns, GetProcessHandleCount and
GetProcessIoCounters numeric totals. Both APIs support the existing
SYNCHRONIZE|PROCESS_QUERY_LIMITED_INFORMATION rights; no extra rights or
handles, no PIDs sampled outside original source-owned ancestry.

Sources:
- https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocesshandlecount
- https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getprocessiocounters

The existing sanitized CIM Chrome role capture now notes **presence only**
of Crashpad --url: present/absent/unknown/not_applicable. It does not emit
the value, pipe name, handle identities, target URLs or secrets. Legacy
7/10/11-field metadata remains accepted; newly absent fields are **unknown**.

An **offline, post-run** script reads only existing original
process-*.json + process-*.jsonl records to compare source-attested
(PID, creation FILETIME) live counter samples pre-fault and after runner
exit. It writes a **separate** P1-K artifact without changing the
original ZIP, TRX, original SHA256SUMS or test/gate timing. Invalid,
partial, missing or non-monotonic counter evidence fails closed.

## Acceptance boundaries

All findings are diagnostic only. No exact Crashpad client handle, IPC
registration, upload completion, or upload target is identified.
Unconditionally: registeredClientsProven=false,
pendingCrashUploadProven=false, productFixProven=false,
postGateHostRemediationProven=false, fixClaim=false.

Original historical real FAIL evidence must remain. A new all-green original
P47 or no native-live Job survivor is NOT a fix. No ad hoc randomized
experiments are added. No merge/release while #59 remains OPEN.
