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
