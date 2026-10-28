# Selenium → Playwright Migrator Core

Standalone, reusable **.NET 10 AST migration engine** for Selenium C# NUnit → Playwright .NET. It includes the original `Migrator.Core` domain models, Roslyn parser, Selenium C# adapter, Playwright .NET renderer, and the direct `Migrator.Application` API.

Source history: imported from [selenium-playwright-ast-migrator](https://github.com/AlexanderLevenskikh/selenium-playwright-ast-migrator), pinned source commit `e9e7a7e67def1a1db7d02a6b12ac0921b2e232a2`. Original upstream master was not changed.

## Use the API

```csharp
using Migrator.Application;

var engine = new MigrationEngine();
var analysis = engine.Analyze("./OldTests", "./adapter-config.json");
var preview = engine.Preview("./OldTests", "./adapter-config.json", analysis.AnalysisId);
var explanation = engine.Explain("./OldTests", analysis.Findings[0].Id,
    "./adapter-config.json", analysis.AnalysisId);
```

- `Analyze`: independently inventories annotated test declarations and produces diagnostic findings.
- `Preview`: conservative, read-only generation of exact target files, hashes and residual findings.
- `Explain`: returns source-backed diagnostic evidence and a recommended next step.

Unsupported xUnit source and multiple fixtures per file are **counted but blocked** rather than silently dropped. Preview does **not** constitute compile, runtime or behavior verification. Lightweight project semantics are not equivalent to full MSBuild evaluation. Java/Python experimental parsers remain in the shared domain source, not a supported MCP capability.

## Development / tests

```bash
dotnet restore Migrator.Application.Smoke/Migrator.Application.Smoke.csproj
dotnet run --project Migrator.Application.Smoke/Migrator.Application.Smoke.csproj -c Release
```

The Core CI runs smoke tests, builds five version-matched NuGet preview packages, and tests a separate NuGet-only consumer. CI artifacts are **not publicly published NuGet releases**.

## Package boundary

Five packages are built together at version `0.1.0-preview.1`: `Migrator.Core`, `Migrator.Roslyn`, `Migrator.SeleniumCSharp`, `Migrator.PlaywrightDotNet`, and `SeleniumPlaywrightMigrator.Application`. They are MIT licensed.

## Exact candidate verification (preview.2)

`MigrationEngine.VerifyArtifact(source, expectedAnalysisId, expectedCandidateId, targetDirectory?, configPath?, runCompile?, compilationReferencePaths?)` regenerates a read-only current preview **only to attest the parent identity**, then captures the exact target bytes. For `targetDirectory`, every expected .cs file must be present and unexpected .cs files are rejected. An edited tree obtains a new content-addressed `CandidateRevision` with `origin=agent-edited` and a parent candidate ID.

- Syntax verification uses Roslyn on the exact captured bytes.
- Compilation is opt-in. Supply `compilationReferencePaths` (an empty list explicitly means .NET framework-only) to run in-memory Roslyn assembly compilation against runtime framework assemblies and supplied .dll references.
- Missing reference context returns `blocked`, an unresolved C# type returns `failed`, and skipped compile returns `not_run`.
- Neither successful parsing nor Roslyn assembly emission is a product build, runtime test, observed behavioral equivalence, or full migration acceptance.
- No target or source files are written by verification. Old candidates cannot acquire evidence for new edited bytes.

Preview.2 is a pre-release contract; the Core CI builds version-matched NuGet packages but does not publish them publicly.

## Existing Playwright source guard (preview.3)

This engine's production frontend migrates **Selenium C# NUnit inputs**. It must not re-migrate tests already written in Playwright .NET.

`Analyze` returns `already_playwright` / `ALREADY_PLAYWRIGHT_SOURCE` for test files with strong Playwright AST evidence or an in-scope project reference to `Microsoft.Playwright` / `Microsoft.Playwright.NUnit`. A project combining Selenium and Playwright references returns `mixed_frameworks` / `MIXED_SELENIUM_PLAYWRIGHT_SOURCE` and needs an explicit Selenium-only scope. Both dispositions are counted in discovery and block `Preview`; existing tests are never silently regenerated. This is a conservative syntax/project reference guard and is **not** a complete MSBuild evaluation.

A synthetic no-proprietary-source regression proves already-Playwright tests with ordinary filenames and parent project metadata are rejected. The private project supplied for the follow-up evaluation is not copied into this repository.

## Renderer-backed completeness (preview.4)

`FullyConvertedTests` now requires **both** a safe intermediate action model and no unresolved TODO/risk markers in the generated test method. TODOs in shared setup, class members or unrecognized scopes prevent declaring any test in that file fully converted. Ambiguous method attribution fails closed. This prevents the ArBilling pilot false-green case where the model reported 10/10 complete despite 72 renderer TODO comments. The ordinary `SuccessfullyConvertedTests` compatibility counter retains its historical meaning and **must not** be used as an acceptance signal.

The regression tests are synthetic and contain no third-party source.

## Project Page Object and LocalStorage evidence (preview.5)

The Roslyn frontend now retains `var result = Navigation.Open*Page()` as a structured, result-producing invocation, allowing **explicit** `Methods` mappings and correct result binding. When unmapped, its returned Selenium Page Object variable is blocked and a targeted `POM_NAVIGATION_REQUIRES_MAPPING` TODO/finding is emitted. An explicit mapped navigation statement that does not produce a result variable also leaves downstream POM accesses blocked; URL navigation alone cannot prove Page Object equivalence.

`LocalStorage.Clear/SetItem/RemoveItem/SettingPeriod` invocations produce actionable `LOCAL_STORAGE_REQUIRES_MAPPING` TODOs and findings unless explicitly mapped. No automatic conversion to `Page.EvaluateAsync` is attempted: browser origin, pre-navigation timing, serialized values and side effects must first be verified from source truth. These findings carry source line numbers for agent review.

These are **quality and mapping extensibility improvements**, not automatic business-semantic preservation. Private ArBilling source is intentionally absent from repository regressions; only synthetic fixtures are committed.

## Source-backed POM evidence (preview.6)

Use `new PomSourceInspector().Inspect(projectDirectory, pageTypes?, pageVariable?)` to examine actual source definitions of Page Object locators and LocalStorage helpers. The inspector collects direct members and one nested control level (for example `page.Table.Items`), with source paths, lines, literals and the definitions of selector extensions such as `WithDataTest` and `WithTid`.

Every `ProposedPlaywrightExpression` is marked `requires_review`. Unknown selector helpers and non-literal values are `insufficient_evidence` rather than guessed. Suggestions are **not automatically applied** to the adapter configuration. A reviewer must confirm DOM matching, authorization, context, wait/retry semantics and browser origin before using them. Hashes bind proposals to the scanned source bytes. No customer code is included in regression fixtures.

## Terminal table-row actions (preview.7)

A source `Items.ElementAt(N).Click()` represents a browser side effect. The Roslyn table recognizer now preserves it as a ClickAction with the original index, so a reviewed selector mapping can emit `await locator.Nth(N).ClickAsync()` rather than a passive locator variable.

Unrecognized operations/assertions after `ElementAt(N)` are explicitly marked `TABLE_ROW_OPERATION_REQUIRES_MAPPING`. Their behavior must not disappear merely because a target locator is available. A synthetic regression covers preserved clicks and fail-closed assertion tails.

## Multi-step Page Object method lineage (preview.8)

The read-only `PomSourceInspector.Inspect(...)` now emits `migrator-pom-evidence/v1alpha2` with ordered invocation chains for Page Object methods (combobox, loader, date picker and storage helpers). It follows direct same-class private helper calls to a bounded depth and records source file/line, per-method SHA-256, lexical call sequence, control-flow markers (branches, loops, try/catch), and review-risk flags.

`StorageAndNavigationFlows` captures NUnit test and setup calls to LocalStorage relative to the first navigation within the method. These are only syntax-level relationships: fixture inheritance, dynamic dispatch, browser origins and runtime order through loops or exception paths are not attested. Nothing is automatically translated or marked behavior-preserving; evidence is input to explicit reviewed `Methods` mappings.

Synthetic regression covers multi-step combobox interactions, loader retries, a date picker with reachable private blur helper, multi-key storage writes and storage before/after navigation. No private input is committed.
