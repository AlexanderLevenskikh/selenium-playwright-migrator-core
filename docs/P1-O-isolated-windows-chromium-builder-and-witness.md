# P1-O — source-exact isolated Windows Chromium 156 builder and Crashpad witness

**Status (2026-11-22): PREREQUISITE_FAILURE / NOT RUN. NO FIX CLAIM.**
This is an opt-in, private test-owned build/runtime recipe **stacked on source-exact P1-N Core #77** at `b065d9d4b3d5bc664ec51b0008d40f4412a35410`. It is neither a product cleanup change nor permission to merge/release. Issue [#59](https://github.com/levencode-labs/selenium-playwright-migrator-core/issues/59) remains open.

## Why standard GitHub Windows CI cannot build this Chromium

The exact [Chromium 156.0.8078.80 Windows build instructions](https://github.com/chromium/chromium/blob/156.0.8078.80/docs/windows_build_instructions.md), canonical Git blob `224fe5df98bcdb612e1682791957327ea06b5b8a`, require **>=100 GB free NTFS** and Visual Studio **2026 (>=18.0.0)** with C++ Desktop and MFC/ATL, Windows SDK **10.0.28000.2270**, Debugging Tools **>=10.0.26100.3323**. Standard GitHub-hosted Windows runners are **14 GB SSD / 16 GB RAM** in public repositories; they can run existing small .NET/P47 CI, not a full Chromium build. The exact repository runner inventory is not accessible via the connected GitHub app; existence of a suitable self-hosted/large runner has **not** been proven. No self-hosted infrastructure was created and no full Chromium build/run is claimed.

For a separate *owner-approved* test-owned VM, choose Windows x64 with **>=32 GiB RAM and >=300 GiB free NTFS scratch** (conservative operational requirement to duplicate immutable source + build output). Use a fresh VM/isolated account and provider-managed egress controls; no customer code, privileged handle enumeration, creds or GitHub token needed by these scripts. Host setup must be performed independently of the running product P47. No public artifact upload of browser executable or raw private event file.

## Pin manifest (no floating toolchain)

See [toolchain lock](../docs/p1o-chromium156-toolchain.lock.json):

| Item | Exact identity |
| --- | --- |
| Chromium tag / git commit | `156.0.8078.80` / `fb096c0011f9d50f0a264a9376420cdea9c421e3` |
| Crashpad vendored revision | `53c4dcfae02976fe5c1b0b9e5347914b1ccfcd9f` |
| Chromium DEPS Git blob | `b902d611fe6a71d28add17ec663faa48e10f877d` |
| Exact depot_tools commit FROM THOSE DEPS | `a07c06fe67a1a9d64ac4728df3a11c1ceb0cf73e` |
| VS Build Tools 2026 Stable | 18.11.4, Microsoft release build `12402.416` |
| Windows SDK | include/lib version `10.0.28000.0`, release `10.0.28000.2270` |
| SDK Debugging Tools | minimum `10.0.26100.3323` |
| GN | `is_debug=false`, `is_component_build=false`, `is_official_build=false`, `symbol_level=1`, `target_cpu="x64"`, `use_remoteexec=false`, `use_siso=false` |
| Target | instrumented `chrome` (not just a standalone Crashpad handler) |
| Source-exact P47 Python Git blob | `f0d44e21d40b2cbf16397bc400671f77232004db` |

Pinned releases are confirmed against **tagged** Chromium 156 sources, not moving Chromium main. The fixed VS 18.11.4 Build Tools bootstrapper is listed in [Microsoft's VS 2026 release history](https://learn.microsoft.com/en-us/visualstudio/releases/2026/release-history); the operator must provision VS/MSVC and SDK components before running this repo's scripts. Depot tools must be prepared at the exact SHA with its bootstrap hooks/dependencies already available. **These scripts do not install or update Visual Studio, Python, SDK, depot_tools or Chromium. Preflight and build receipts use BOM-free UTF-8 so PowerShell 5.1 can hand them to the strict Python JSON verifier without ambiguous decoding.** Preflight records the actual MSBuild/Debuggers file SHA256; the Microsoft published build/version mapping does not replace an attested installed compiler bit hash. Capture `gn`/`autoninja` build and instrumented binary digests in the private receipt, and require matching IDs for A/B.

### Preparation outside repo automation, on approved VM only

Provision the full Chromium source and every gclient DEPS dependency from the exact tag/commit (gclient sync + runhooks), and a separate depot_tools checkout at the exact SHA above. Run those network-dependent bootstrap operations **before** entering the isolated build/test stage. Do not reuse global user credentials or change the developer workstation's system config. Chromium's official instructions note that gclient initialization on Windows should use a cmd.exe environment. Verify with read-only:

```powershell
git -C D:\P1O\pristine\src rev-parse HEAD
git -C D:\P1O\depot_tools rev-parse HEAD
# Expected EXACT: fb096c0011f9d50f0a264a9376420cdea9c421e3
#                 a07c06fe67a1a9d64ac4728df3a11c1ceb0cf73e
```

Obtain the matching original **ChromeDriver 156.0.8078.80** from a host-approved browser download, attest its SHA256, and use an isolated checkout of this P1-O draft (based on exact Core #77). The original 5-stage script remains unchanged.

### Step 1 — read-only Windows resource/toolchain/source preflight

```powershell
.\scripts\p1o_windows_builder_preflight.ps1 `
  -ChromiumRoot D:\P1O\pristine\src `
  -DepotToolsRoot D:\P1O\depot_tools `
  -ScratchRoot D:\P1O\scratch `
  -PrivateReceipt D:\P1O\private\preflight-facts.json `
  -OwnerApprovedIsolatedBuilder -RequireReady
```

This checks native Windows, architecture, NTFS/free bytes/RAM, VS Build Tools Stable release/channel/components, SDK and debugger, exact depot_tools/Chromium Git commits, selected Git blob bytes and a clean pristine source tree. It writes a private JSON facts+assessment receipt; if any prerequisite is wrong, exits with an explicit **PREREQUISITE_FAILURE** before copying/building/running. No source modification or data upload. Run-only build permission is separately gated; preflight success is NOT compile success.

### Step 2 — prepare isolated copy and compile **test-owned** Chromium

```powershell
.\scripts\p1o_build_test_owned_chromium156.ps1 `
  -PristineChromiumRoot D:\P1O\pristine\src `
  -DepotToolsRoot D:\P1O\depot_tools `
  -FreshTestOwnedBuildRoot D:\P1O\scratch\src-instrumented `
  -PrivateEvidenceDirectory D:\P1O\private `
  -PreflightFacts D:\P1O\private\preflight-facts.json `
  -ApproveOneInstrumentedBuild
```

This checks that the staging destination does not exist, verifies all pinned sources, invokes the existing exact [P1-N overlay producer](../scripts/p1n_prepare_crashpad_test_witness.py), copies the pristine Chromium source into a **separate fresh test-owned tree**, applies ONLY its three overlay files, defines `MIGRATOR_P1_TEST_OWNED_WITNESS` explicitly in the two test-owned C++ translation units (before the witness header), writes exact GN args, and runs `gn gen` plus `autoninja -C ... chrome`. The pristine source and original P47 Git blob are checked again after the build. No production cleanup source is modified. The per-process environment disables depot_tools auto-update and internal toolchain download, without persisting global config. Network egress must already be denied by the isolated host; these scripts do not install a firewall or guarantee provider egress policies. A failure is a **real failed build/prerequisite**, never accepted as PASS.

It computes exact `chrome.exe` SHA256 and optional `chrome.dll`, patched C++ source hashes, `args.gn` and Ninja manifest hashes, plus source/depot/VS/SDK provenance; private preflight additionally fingerprints the actual installed `gn`, `clang-cl`, `lld-link`, SDK `Windows.h`, depot bootstrap launchers, MSBuild and Debuggers file bytes (never assumes a release label equals a known tool binary digest) in `private/p1o-test-owned-build-receipt.json`. A build receipt sets `compiled=true`, `executed=false`, `rootCauseProven=false`, `productFixProven=false`. This digest describes the **new test-owned build**, NOT the unrecorded historical stock 156 executable SHA.

### Step 3 — ONE original P47 under test-owned browser, original first-FAIL

The original P47 source must already have its existing `Migrator.AssemblyCli` built. Set only per-process paths/versions. The wrapper verifies the test-owned build receipt, matching ChromeDriver version, exact P47 source SHA and fresh private directory, then launches the **unaltered** original `scripts/p2_preview47_one_time_real_chromium.py` **once** with `MIGRATOR_CHROME_BIN` set to the compiled instrumented `chrome.exe` and `MIGRATOR_P1_TEST_WITNESS_FILE` set to a private file. It never changes original NUnit `[SetUp]`, `[TearDown]`, `[OneTimeTearDown]`, true Node `os.kill(SIGTERM)`, original 12-second OS presence window, PID/start semantics, stage order, primary failure or first-FAIL stopping.

```powershell
python scripts/p1o_win32_original_p47_one_shot.py `
  --core-checkout D:\P1O\migrator-p1o `
  --chromium-exe D:\P1O\scratch\src-instrumented\out\P1O-156-instrumented\chrome.exe `
  --chrome-driver D:\P1O\tools\chromedriver.exe `
  --build-receipt D:\P1O\private\p1o-test-owned-build-receipt.json `
  --fresh-private-evidence D:\P1O\private\p47-one-shot `
  --approve-original-one-shot
```

A separate **passive** test-owned observer first requires an observed process ancestry path to the exact original P47 Python PID for any new Chromium PID; the matching executable path alone never proves test ownership. It restricts native Win32 `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` to those ancestry-bound Chrome PIDs (or previously registered PID identities checked for reuse with native FILETIME). It records real `GetProcessTimes` FILETIME and `WaitForSingleObject(0)` live/exit status without dumping raw command lines, URLs, HANDLEs or pipe names. Because this observer can perturb timing, it cannot substitute source-exact original P47 observations. P47 is permitted to fail, and its nonzero result is propagated: no blind rerun for cosmetics or after-FAIL extra stage. No host Job Objects or post-gate kill; the private VM owner handles final VM teardown after evidence is preserved.

The wrapper leaves original P47 TRX/ack artifacts unmodified, keeps raw console output **private**, and produces a new private ZIP with SHA256 plus generated handler events and a synthetic host-correlation receipt. **Do not upload raw logs or the Chrome binary publicly**.

### Step 4 — classify the direct runtime witness independently

`scripts/p1o_builder_and_witness_contract.py` validates allowlisted JSONL event fields and bounded event sequences per handler PID + **native FILETIME**, actual initial/pipe registration, private count, `persistentMode`, threadpool `OnProcessEnd`, completion-port posting/consumption/erasure and `UploadReport` begin/end. The host-correlation record must independently demonstrate a real native-LIVE handler after the ORIGINAL 12-second gate, exact matching handler identity, true Node SIGTERM, primary NUnit failure and fail-fast. Never infer an active upload or zero registrations from missing events, stopped logging or cap 512. Its results are `prerequisite_failure`, `not_run`, `not_observed`, `handler_identity_not_correlated`, `partial_handler_trace_unknown` or `test_owned_runtime_correlation_candidate`.

A test-owned handler witness **cannot retroactively prove private state in the historical stock executable**, whose SHA256 was not captured. It can guide a narrowly source-backed product hypothesis only if a genuine same-identity native-LIVE failure is observed. **Any** product fix still requires an actual pre-fix native-LIVE FAIL and post-fix original 12-second PASS under verifiably corresponding conditions, with independent Core 28/28 + exact Core-pinned MCP 2/2.

## Evidence inventory and rejection policy

- Historical untouched original FAIL artifacts [#14745840024](https://github.com/levencode-labs/selenium-playwright-migrator-core/actions/runs/44999689324/artifacts/14745840024), [#14753824405](https://github.com/levencode-labs/selenium-playwright-migrator-core/actions/runs/45020707917/artifacts/14753824405) and [#14754039147](https://github.com/levencode-labs/selenium-playwright-migrator-core/actions/runs/45021742696/artifacts/14754039147), with SHA256SUMS/TRX/native observations as documented in Core #77, remain intact.
- P1-O portable negative tests run on existing Linux/Windows jobs, with actual Windows builder **not** invoked. They prove only lock fail-closed classification, no runtime behavior.
- Do not update either main, close #59, merge P1 research, tag/release, publish NuGet, broaden user execution rights, weaken/fork original P47, use Win32 Job Object as a product fix, or rerun probabilistically.

**Release decision: NO-GO. Root cause, test-owned runtime state, shipped stock handler clients and product cleanup remain UNKNOWN until independently measured.**
