# Release Candidate readiness — Selenium → Playwright Migrator

**Decision (2026-11-22): NO-GO.** This document is a release checklist, not a release announcement. No merge to main, no version tag, no public NuGet or GitHub release. The product is not qualified for unrestricted automated migration.

## Exact baseline and integration sources

- Baseline Core main: `0745e6a9046dff8edb684931a84e7074a2382788`; baseline MCP main: `9069a6f019426362e10fe0ff992011c00a59e0c0`.
- Core P2+P3 product-stack source: [draft #78](https://github.com/levencode-labs/selenium-playwright-migrator-core/pull/78), exact `d8600b158d6e871d8bf6048b8910b19a1f9481d6`, [full original 28-job CI #45039742816](https://github.com/levencode-labs/selenium-playwright-migrator-core/actions/runs/45039742816), **28/28 SUCCESS on this source SHA**.
- MCP+Skill P2+P3 product-stack source: [draft #60](https://github.com/levencode-labs/selenium-playwright-migrator-mcp/pull/60), exact `f94654b8ea494b8c269d07abe67faaa85edc1638`, [full 2-job CI #45040204967](https://github.com/levencode-labs/selenium-playwright-migrator-mcp/actions/runs/45040204967), **2/2 SUCCESS on this source SHA**; both package CI consumers pin Core #78 exact source commit.
- This clean integration candidate must pass a **fresh** full 28-job Core CI, and the matching MCP integration commit must pass both Linux/Windows package/stdio jobs with **the final Core integration SHA pinned**. Prior stacked-green CI does not automatically certify a new commit.
- Product source Core stack `#68 → #74 → #75 → #78`, MCP/Skill `#56 → #57 → #58 → #60`, is preserved without importing the experimental P1 Job Object code. The active P1 read-only branch `#73 → #76 → #77` remains separate.

## Verified bounded product evidence (NOT general equivalence)

Core #78 evidence from [CI #45039742816](https://github.com/levencode-labs/selenium-playwright-migrator-core/actions/runs/45039742816):

| Evidence | Linux | Windows | Meaning |
| --- | --- | --- | --- |
| P2/P3 public source portfolio | artifact `14761596037` | `14761576456` | Three unrelated public projects, five exact Git blobs. **Only source identity and structural obligations**, source-target runtime `not_run`, parity `UNKNOWN` |
| Real Selenium↔Playwright fictional localhost oracle | artifact `14761940096` | `14761800773` | Eight positive real NUnit/DOM/ordered-action pairs; inverted guard yields expected NUnit FAIL; missing Click yields **NUnit PASS but behavioral mismatch rejected** |
| Original Windows P47 | artifact `14761850461` | — | Green non-reproduction on this SHA, NOT proof of orphan cleanup |

Independent ZIP verification: both browser archives have valid CRC and full 26/26 SHA256SUMS; exact derived synthetic `oracleCandidateSha256=f8aa2b6e09ff9fbf6fee35b7bdf22282b7c205cf45c9ac1f2ee83b5b9f69c317` matches across Linux/Windows and explicitly differs from product `VerifyArtifact.CandidateId`. This *does not* prove a migrated third-party project. Both public source portfolios carry real `UNKNOWN` behavioral status, `accepted=false`, `canAutoApply=false`.

The initial public source report contained platform-specific working-tree SHA256 after Linux LF vs Windows CRLF checkout, despite matching canonical Git blobs. The clean integration fixes the report to use **canonical Git blob content SHA256**, retaining distinct checked-out byte SHA256 and verifying that any difference is *only* LF↔CRLF. This prevents false byte-parity claims.

## Mandatory release gates — none may be waived for a green label

1. **P1 product correctness (HARD BLOCKER):** [Core issue #59](https://github.com/levencode-labs/selenium-playwright-migrator-core/issues/59) remains OPEN, with real original Windows native-LIVE orphan ZIP #14745840024, #14753824405 and #14754039147. Need a directly observed registered-client / `OnProcessEnd` completion / active upload witness tied to the exact deployed Chromium/Crashpad binary, a *source-backed product-owned* fix, and an actual before/after native-LIVE FAIL→PASS with original Node SIGTERM, unchanged 12-second gate, exact PID+native FILETIME, real NUnit primary exception and fail-fast. **Current P1 #77 has an uncompiled test-owned instrumentation overlay, not a fix**. Independent host Job Object cleanup cannot substitute product correction.
2. **Behavioral acceptance (HARD BLOCKER):** complete transitive Selenium Page Object/helper graph, inherited and `SetUpFixture` NUnit lifecycle, ordered browser effects, selectors/arguments, waits, exception/failure paths for a specifically selected project. Compare **independent** real Selenium vs Playwright execution with exact source/target hashes, negative mutation controls, no missing obligations and explicitly authorized isolated runtime. Three sampled public real projects remain source-only `UNKNOWN`.
3. **Identity and fail closed:** `AnalysisId`, parent/new `CandidateId`, `SourceTreeHash`, `TargetTreeHash`, `migrator-acceptance/v1`, per-obligation `missing_target`/ `candidate_unverified`. `rejected_incomplete` or `blocked_unverified` is not accepted. `syntax`, `compile`, `testDiscovery`, `runner`, `realBrowser`, `behavioralEquivalence` stages remain separate. Unexecuted stages = `not_run`.
4. **Unchanged acceptance matrices:** all original **28 Core Linux/Windows jobs**, including P47 and real Chromium, **and 2 exact-Core-pinned MCP/Skill package+stdio jobs** on the final candidate commit. Never narrow/remove jobs after failure, rerun blindly, or substitute an older SHA.
5. **Package/docs/security:** exact .NET 10 SDK and versioned local NuGet feed; reproducible self-contained Linux/Windows MCP; MIT license; complete English/Russian guides, no private ArBilling, secrets, raw pipe/handle/URL data or public customer code. No public publication or main change until a separate explicit release decision.

**Release flag: BLOCKED.** A successful CI validates *tested scope*, not the product release gates above.
