# P1-L — original Windows Crashpad client-evidence boundary

Research only. Core issue #59 remains OPEN. This branch comes from current main and does NOT import the experimental Job Object stack.

## Source-backed limit

On Windows, Crashpad ExceptionHandlerServer registers client process handles and crash events. OnProcessEnd posts a completion-port message, and the main loop removes a private ClientData entry from its internal clients_ set. Process parentage and the Win32 aggregate GetProcessHandleCount cannot reveal which registered clients remain, nor prove completion-port callbacks were consumed.

Sources:
- https://chromium.googlesource.com/crashpad/crashpad/+/refs/heads/main/util/win/exception_handler_server.cc
- https://chromium.googlesource.com/crashpad/crashpad/+/main/util/win/exception_handler_server.h
- https://github.com/chromium/crashpad/blob/main/handler/crashpad_handler.md

No client-registration/OnProcessEnd witness exists in the historical original ZIPs. No precise client identity can safely be claimed from their process trees. Upload configuration is not proof of pending uploads.

## New read-only verifier

Run the portable script:

    python scripts/p1l_crashpad_client_boundary.py --original LABEL SHA256 original-P47.zip --out result.json

It checks outer ZIP SHA256, CRC, root and nested SHA256SUMS, the original NUnit TRX hash, true driver SIGTERM, unchanged 12-second gate, source-bound PID + native creation FILETIME, native WAIT_TIMEOUT=258, Chromium root exit and original fail-fast limit. An invalid identity, missing evidence or second original failure is rejected.

Output separates original native-LIVE Crashpad survivors from other observable Chromium helpers. It NEVER labels these helpers as registered Crashpad clients, and never infers that no registered clients remain merely because all observed source-owned child processes exited. Every report retains registeredClientIdentityProven=false, rootCauseProven=false, productFixProven=false and fixClaim=false.

No process access, ETW session, handle enumeration, debug privilege, browser execution, test execution, script mutation or new random P47 trials are introduced.

## Original public evidence, verified locally

| Original Windows ZIP | Artifact | Observed native-LIVE Crashpad | Other surviving helper | Classification |
| --- | --- | ---: | --- | --- |
| P1-H | 14747793425 | 2 | StorageService | original native-LIVE FAIL |
| P1-I | 14749126920 | 2 | StorageService | original native-LIVE FAIL |
| P1-J | 14753824405 | 1 | StorageService | original native-LIVE FAIL |
| P1-J | 14754039147 | 1 | StorageService | original native-LIVE FAIL |
| P1-K | 14755774447 | 0 | none after gate | non-reproduction, NOT fix |

All five were checked using their exact full outer SHA256, CRC and internal manifest hashes. Historical original ZIPs are not rewritten and not required in public CI.

## Why so many PRs?

At inspection, **12 Core and 2 MCP** draft PRs were open. Most are old research checkpoints, not ready-to-merge features.

| Group | PRs | Disposition |
| --- | --- | --- |
| Product Roslyn/POM source-flow | Core #58, MCP #54 | KEEP as product work; each is two commits behind current main and needs integration acceptance |
| P2 false-green acceptance | Core #68, MCP #56 | KEEP; Core 28/28 and MCP 2/2 exact-SHA CI passed, but real behavior parity NOT proven |
| Old independent timing A/B | Core #60, #61 | CLOSE unmerged after archiving refs and ZIPs; not in current P1 stack |
| Stacked Win32 Job/native experiments | Core #62, #63, #64, #65, #67, #71, #72 | HISTORY only; keep original failures, exact SHA and evidence, never merge as a product fix |
| Independent original-ZIP verifier | Core #69 | Complementary tool; evaluate retention or archive after evidence consolidation |

The main P1 research chain is Core #58 -> #62 -> #63 -> #64 -> #65 -> #67 -> #71 -> #72. Several are based on other experimental branches, not current main. A green stage in that chain cannot establish that post-gate KILL_ON_JOB_CLOSE actually remedied a native-LIVE orphan.

Closing a historical PR without merge is different from deleting its branch; preserve original evidence and commit references. GitHub Actions artifacts may expire under retention policy. This index does not close, merge or delete anything.

## CI and release boundary

P1-L adds only synthetic portable read-only tests to BOTH existing Core Linux and Windows smoke jobs. The existing 28-job matrix is unchanged. Original P47, NUnit, real Node SIGTERM, 12-second gate and first-FAIL rule remain untouched.

Issue #59 OPEN. **NO FIX CLAIM, NO MERGE, NO RELEASE.**
