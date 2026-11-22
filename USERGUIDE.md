# Selenium → Playwright Migrator Core — User Guide (RC preparation)

**Status: pre-release / NO-GO for production.** This repository provides conservative source analysis, read-only proposals and explicit opt-in generation/build/test workflows. The Windows Chromium driver-loss orphan in [Core issue #59](https://github.com/levencode-labs/selenium-playwright-migrator-core/issues/59) has real historical native-LIVE FAIL and **no proved product fix**. Do not interpret a green CI or a passing migrated NUnit test as full behavioral equivalence.

## 1. Requirements and scope

- .NET SDK 10 (see `global.json`), Windows or Linux, a trusted local Selenium C# NUnit workspace, and explicit adapter mappings for project-specific operations.
- Migration accepts Selenium C# NUnit. Existing Playwright tests, xUnit, multiple conflicting fixtures, unsupported POM/virtual dispatch, unknown selectors or other ambiguous effects must be BLOCKED and reviewed rather than silently rewritten.
- This guide describes the CLI and direct API, not a fully automatic business-system migration. Do not execute customer source on an untrusted runner.

## 2. First read-only analysis

From a clean checkout of this exact RC branch, build the assembly CLI:

```bash
dotnet restore Migrator.AssemblyCli/Migrator.AssemblyCli.csproj
dotnet build Migrator.AssemblyCli/Migrator.AssemblyCli.csproj -c Release
dotnet run --project Migrator.AssemblyCli -c Release -- plan /absolute/path/to/selenium-project
```

Read the generated source-tree digest, findings, discovery/completeness blockers and project scope. A plan performs no source edits, no browser runs and no NUnit execution.

For a bounded Page Object:

```bash
dotnet run --project Migrator.AssemblyCli -c Release -- pom-inspect /absolute/path/to/POM-project
dotnet run --project Migrator.AssemblyCli -c Release -- pom-plan /absolute/path/to/POM-project Namespace.PageObject MethodName EXACT_SOURCE_TREE_HASH
```

`pom-plan` returns `requires_review` or `blocked`. The optional `--emit-target` is **local, explicit** and can reveal project selectors; never paste its output into public CI or logs.

## 3. Controlled generation / verification

Only after reviewing adapters and the source scope, request a **NEW**, separate target tree. The tool never overwrites an existing target directory.

```bash
dotnet run --project Migrator.AssemblyCli -c Release -- generate /absolute/source-project /absolute/new-target --config /absolute/adapter-config.json
dotnet run --project Migrator.AssemblyCli -c Release -- verify /absolute/source-project /absolute/new-target --config /absolute/adapter-config.json
```

`--build` and `--test` are opt-in and can execute build hooks/tests; use **only** for explicitly trusted or disposable test projects. Never add these flags merely to obtain a green status.

The Core API offers `Analyze(source)`, `Preview(source, config, expectedAnalysisId)`, and `VerifyArtifact(source, expectedAnalysisId, expectedCandidateId, targetDirectory?, configPath?, runCompile?, compilationReferencePaths?)`. Every edited candidate gets a new `CandidateId`, `SourceTreeHash` and `TargetTreeHash`. Do not reuse acceptance from a previous revision.

## 4. Acceptance: what is proven and what is not

- `rejected_incomplete`: mandatory source action/assertion, NUnit `SetUpFixture`, inherited base, helper branch/effect or method is missing. **STOP.**
- `blocked_unverified` / `candidate_unverified`: a syntactic candidate exists, but semantics/behavior are **not** proven. **REVIEW.**
- `syntax`, `compile`, `testDiscovery`, `runner`, `realBrowser` and `behavioralEquivalence` are independent stages. A skipped stage is `not_run`, never PASS.
- Independent Selenium↔Playwright observable acceptance requires exact source and target bytes, selected scenario/fixture IDs, real browser, matched DOM/state/ordered effects, exception/failure paths, original NUnit lifecycle and negative mutation controls. Production approval requires a separately authorized runtime and an oracle for the *specific* project.

Current CI uses a fictional localhost real Selenium/Playwright slice with eight positive cases, a deliberately failed guard mutation, and a **false-green NUnit PASS** target with missing Click. The separate SHA-bound oracle rejects the latter. Three unrelated public projects are checked at exact Git SHAs for source obligations only; their migration runtime and behavioral parity remain `UNKNOWN`.

## 5. Release and troubleshooting

See [RC gates](docs/RC-READINESS.md). Do not merge/release while issue #59 remains OPEN or while any required scenario is `UNKNOWN`/FAIL. Original Windows P47 retains real Node `os.kill(SIGTERM)`, an unmodified 12-second gate, exact PID + native FILETIME, fail-fast, historical original ZIP/TRX. The test-owned Crashpad instrumentation overlay in P1 #77 is **uncompiled** and cannot prove a fix.

If an adapter is blocked, narrow the scope or collect source-backed selector/call-graph evidence. Do **not** weaken CI, rewrite historical failures, substitute an external Win32 Job cleanup, or silently skip operations.


## Public NUnit project: bounded P2/P3 evidence (draft #85, 2026-11-22)

The SHA-pinned `desh2001/Selenium-NUnit-Automation@edacb9feb41a7e21d9b6bf6924f24c11698a4427`
**original .NET 10 project** restores, compiles and discovers 10 NUnit tests on
Linux and Windows; its original external-site NUnit tests are **not run**.
Core's **actual** `generate --build` produces a separate four-file
Playwright/NUnit target with an SDK `.csproj`, manifests and 10 discovered
target tests. Those target tests are **not executed**; all four C# source
files match across Linux/Windows after canonical CRLF→LF normalization.
The generated project contains unresolved POM/DriverManager/action/assertion/
lifecycle TODOs. Passing compilation and identical test discovery **do not**
establish behavioral equivalence.

`pom-inspect`/`pom-plan` cover all 15 original Page Object methods: 14
`requires_review`; `AlertsPage.WaitForAlert` is `blocked` (source
`IAlert`/method signature). The exact public SampleApp source method bodies
were independently executed with Selenium vs Core-generated Playwright on
localhost real Chromium: positive/negative login and seven NUnit TRX with
three negative mutation controls. This is **bounded POM evidence**, never
an original project-level fixture/DriverManager acceptance.

Use `acceptance-inspect <original-project-root> <AnalysisId> <CandidateId>`
from the exact Core `plan`: its source-target lineage reports 125 source
obligations and 115 missing, with **`rejected_incomplete`** on both OS.
A stale CandidateId fails; even a complete AST match cannot bypass
`testDiscovery`, `runner`, `realBrowser` and
`behavioralEquivalence` stages. This inspection is strictly read-only
and exits nonzero for every non-accepted status. See
[public source/target gate](docs/P2-P3-PUBLIC-SAMPLEAPP-PROJECT-GATE.md),
[draft #85](https://github.com/levencode-labs/selenium-playwright-migrator-core/pull/85)
and [RC #80](https://github.com/levencode-labs/selenium-playwright-migrator-core/issues/80).
`selenio` and `re-motion/Framework` remain public source-only inputs;
full NUnit runtime parity for all three is **UNKNOWN**, as is P1 #59.
No release, merge or unrestricted autoapply.
