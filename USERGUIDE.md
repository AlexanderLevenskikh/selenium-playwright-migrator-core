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
