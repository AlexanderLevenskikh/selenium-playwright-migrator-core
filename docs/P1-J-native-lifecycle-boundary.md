# P1-J — Windows Chromium root exited, helpers remained LIVE

Research only for [Core #59](https://github.com/levencode-labs/selenium-playwright-migrator-core/issues/59). **No product fix, no merge/release approval.**

## Exact original evidence

| Evidence | Artifact | Original ZIP SHA256 |
| --- | --- | --- |
| P1-H original P47, cycle 4 disconnect_pass FAIL | [14747793425](https://github.com/levencode-labs/selenium-playwright-migrator-core/actions/runs/45005924305/artifacts/14747793425) | 8324128313000b8ebbeffb289c2a1d7a210fe6217407efa98b13ff43d0e5fd8e |
| P1-I original P47, cycle 7 disconnect_primary_repeat FAIL | [14749126920](https://github.com/levencode-labs/selenium-playwright-migrator-core/actions/runs/45008909752/artifacts/14749126920) | ef911c0ebb047243aecd9d09c753c7b0b460841b4cb2c367df2e71f3cbbcdecf |
| P1-H independent pre-create Job B | [14748260739](https://github.com/levencode-labs/selenium-playwright-migrator-core/actions/runs/45005924305/artifacts/14748260739) | a4f416c3d0ab54b340490428165fca7838d04e890b9121ff37623d6ba8ccdd99 |

Original P47 is source-exact: real Node os.kill(SIGTERM), real NUnit, **unchanged original runner-exit-anchored 12-second gate**, native PID/FILETIME and process ancestry. Historical FAIL and TRX are preserved.

**P1-H A original:** Chromium root PID 3472 **native exited, exit code 0**; the root's crashpad-handler PID 5028 (FILETIME 134398122442621909), its crashpad child PID 9800 (134398122442731653), and StorageService PID 10200 (134398122444310696) were native-LIVE after gate. NUnit disconnect_pass was **Passed**, original process gate was **Failed**.

**P1-I A original:** Chromium root PID 5644 **native exited, exit code 0**; crashpad PIDs 820/2652 (FILETIME 134398145370572429 / 134398145370478573) and StorageService PID 3788 (134398145371694536) were native-LIVE after gate. Primary NUnit failure was preserved.

**P1-H independent B:** 27/27 pre-fault source-linked Node and Chrome processes across three disconnects exited natively **before NUnit runner exit**, while host Job handle remained open. This is **natural exit timing**, not post-gate KILL_ON_JOB_CLOSE remediation. Independent P1-I A [14749092658](https://github.com/levencode-labs/selenium-playwright-migrator-core/actions/runs/45008909752/artifacts/14749092658) did not reproduce a survivor and did not attempt Job adoption. Different Windows runners do not constitute a randomized A/B comparison.

## Source-backed conclusion

The Chromium root has exited while separate helper processes persist. This excludes a PID-only/CIM phantom and falsifies the assumption that the whole browser root is still alive. It does **not** identify a proven reason for crashpad/StorageService persistence (IPC pipes, inherited handles, exit ordering or another Windows cause). No real post-gate host Job-close termination of a source-attested original survivor is proven. **Root cause UNKNOWN; remediation UNKNOWN; NO FIX CLAIM.**

## Reproduce read-only

Download the three immutable ZIPs from the links above. Invoke scripts/p1_preview47_j_offline.py with repeated --original LABEL SHA256 ZIP and --job-b LABEL SHA256 ZIP arguments (and optional --output FILE). It verifies outer SHA256, **every** internal and nested SHA256SUMS entry, original TRX digest, genuine Node SIGTERM, original 12-second gate, native process identity and exact parent chain. Invalid evidence exits nonzero. Even valid evidence unconditionally sets rootCauseProven=false, productCleanupProven=false, postGateHostRemediationProven=false, fixClaim=false.

Portable negative regression: python scripts/test_p1_preview47_j_offline.py. No browser launch, ETW, synthetic fault, subprocess termination, extra CI dispatch, private data or changing original P47. A future product fix requires a new controlled pre-fault-attested native-LIVE B survivor after the gate and independent actual last-handle-close proof, with the original FAIL kept intact and full CI unchanged.

**#59 OPEN; draft research only; NO MERGE / NO RELEASE.**
