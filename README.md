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

### Explicit control type selection

When `pageTypes` contains a specific class name (for example a project's `Combobox`), the inspector includes its public methods even when the name is not in the built-in list. A zero-call public method is preserved as evidence for review rather than silently omitted. This is only source inspection; no method is automatically mapped or executed.

## Review proposed Page Object Methods mappings (preview.9)

`PomMappingReviewer.Review(sourceDirectory, sourceType, sourceMethod, sourceLine, expectedSourceTreeHash, targetStatements)` checks an **explicit proposed mapping** against the source-backed POM method ledger. Call `PomSourceInspector.Inspect` first to get the source hash, method name and line. Supply concrete target-side C# statements; placeholders and TODOs are not validated as executable mappings.

The reviewer expands bounded same-class helper calls, then compares required operation classes in source lexical order with the target C# syntax: clicks, inputs, waits, assertions, storage writes/removals, scripts, reloads, and loop/conditional/exception structures. It reports each source-lined obligation, target effect, missing obligation and risk, with statuses `rejected_incomplete`, `blocked` or `requires_review`. **There is deliberately no `passed` or `approved` status.** Matching effect counts/orders are necessary but not sufficient: selectors, argument equivalence, branching predicates, browser origin, auth and app observables remain unverified.

This prevents common false-green substitutions: `InputAndSelect` reduced to one `FillAsync`, DatePicker without its private blur script, retrying Loader replaced by one wait, and multi-key LocalStorage helper collapsed to one write. No user files are copied into the public regression fixtures.

## Source-argument and locator literal checks (preview.10)

`PomMappingReviewer` now requires every source helper parameter used in effect-producing calls to remain represented in target invocation/filter arguments (source/target counts are returned as `parameterFlows`). A category-complete mapping with `FillAsync("hardcoded")` or a missing menu-filter parameter is `rejected_incomplete`, not `requires_review`. C# literals in `Page.Locator(...)` containing likely double-escaped CSS attribute quotes are `blocked`. These conservative checks do not establish correct values, selector uniqueness, browser state or behavior; zero missing obligations remains `requires_review`.

## Table assertions with mapped locators and incomplete Selenium setup (preview.11)

When an original NUnit fixture still has an unresolved Selenium Page Object alias in setup, Core ordinarily blocks downstream `page.*` calls. An explicitly configured target locator for `page.Table.Items.ElementAt(N)` can nonetheless preserve source `Text.Get().Should().Be(expected)` and `Should().Contain(expected)` as executable `ToHaveTextAsync` and `ToContainTextAsync` assertions, **without treating the original setup as migrated**.

The narrow guard requires a fully resolved `Page.Locator(...).Nth(N)` target, a literal row index, a valid expected-value expression and an alias blocked by the original fixture setup (not a locally blocked test operation). Every such assertion is accompanied by `TABLE_ASSERTION_REQUIRES_SETUP_REVIEW` TODO. No equivalent behavior or complete migration is claimed; the remaining setup/navigation and loader dependencies must be verified independently. Unmapped rows and arbitrary source-side expressions still fail closed.

### Explicit mappings for project-specific wait helpers

A syntax-fallback recognizer may classify `page.Loader.ValidateLoading()` as a generic `WaitForAction` when the external project class is not available to the lightweight parser. Core now gives precedence to an **exact, zero-argument `Methods.SourceMethod` mapping** over that generic wait policy. This allows source-reviewed multiline `while/try/catch` retry implementations in target statements to be preserved instead of reduced to an unverified wait or TODO. Argument-bearing/custom wait helpers without explicit matching still fail closed. These mappings should use `RequiresReview: true` until their DOM and browser error/reload behavior has been independently checked.

## Negated table assertions and retry-sensitive POM review (preview.12)

Source `Should().NotContain(expected)` no longer falls through to equality. Core preserves it as an executable `Expect(locator).Not.ToContainTextAsync(expected)` where an explicit locator is safe, or an unresolved-source TODO otherwise. The table-row regression retains its original index and a visible setup-review blocker.

`review_pom_mapping` now includes source state-getter obligations, successful-loop exits, and retry-counter mutations inside catch paths. It also rejects a changed numeric/comparison retry guard, a previously conditional reload moved outside its branch, or a widened/narrowed catch-all boundary. Cross-framework typed exception translation remains `requires_review`, not approved. Synthetic negative cases exercise each protection; they cannot prove private backend equivalence.

## Source-backed Loader retry safeguards (preview.13)

The POM inspector records immutable numeric local aliases such as `var limit = 2;`. Review normalizes syntax-proven literal retry bounds before comparing source and target `while` and numerical failure-branch guards. A changing numeric limit/operator or missing failure condition rejects the mapping, while `while (n < limit)` and `while (n < 2)` no longer generate a false mismatch when the source limit is immutable.

An exact literal JavaScript `window.location.reload()` or `location.reload()` call now counts as a reload obligation. Arbitrary scripts stay `script_execution`. Source explicit `Visible.Wait().EqualTo(false/true)` directions are checked against explicit Playwright Hidden/Visible waits; inversion is rejected and unspecified target states are flagged for review. Source-based selectors, actual DOM state, default timeouts, exception translation and browser load timing remain unverified. The synthetic regression includes source aliases, inverted waits, dynamic script, changed retry limits and conditional reload movement; no private code is committed.

## NUnit fixture Page Object lifecycle and setup aliases (preview.14)

A Roslyn symbol-backed assignment from a source `[SetUp]` local to a Page Object **field** (for example `page = pagef;`) is represented by `FixturePageAliasAction`, not emitted as invalid target C# or silently removed. This is opt-in-by-evidence, never a blanket POM alias: the adapter requires a preceding explicit `Navigation.Open*Page()` method mapping with an active target `await Page.GotoAsync(...)`, no intervening unmapped source-side access to the local, and a true field/local symbol relationship (not a shadowing local or arbitrary setter). The renderer double-checks that target navigation was actually emitted before accepting the alias.

Even when linked, the source field is **not** instantiated as a Playwright IPage. Only independently mapped, target-Page-rooted locator actions can use the alias in test methods. Unknown POM methods, unsafe source locals, and unmapped targets stay blocked. Generated setup includes `[MIGRATOR:FIXTURE_PAGE_ALIAS_REQUIRES_REVIEW]` and `FullyConvertedTests` remains 0 until actual setup, navigation, DOM, loader and observables are independently reviewed. No fake Page Object field is emitted.

The parser also no longer silently drops multiple `[SetUp]`, `[OneTimeSetUp]`, `[TearDown]` or `[OneTimeTearDown]` declarations: unsupported extra lifecycle hooks produce explicit blocking TODOs. These scopes require a follow-up implementation and target-specific review; NUnit order/lifetime is not inferred. The synthetic Core smoke tests positive fixture sequencing and negative controls for unconfigured navigation, unknown setup side effects, changed result locals, fake target navigation, shadowed fields, and additional lifecycle hooks. No private selectors or application sources are used.

### Fixture alias invalidation hardening

Later `page = other` or `page = null` assignments revoke the existing alias even if a previous `page = pagef` was source-backed. An unmapped setup helper that touches the field also revokes it. A previous navigation/alias is never retained as evidence for subsequent tests after source setup changes the Page Object. Synthetic negative regressions lock down these cases.

## Inherited NUnit lifecycle (preview.15)

A Roslyn-symbol-bound `NUnitLifecyclePlan` discovers `[SetUp]`, `[TearDown]`, `[OneTimeSetUp]` and `[OneTimeTearDown]` across the complete source inheritance chain, including partial and project-local base types. For one hook per kind per class, Core emits **one target hook of each kind**: setup phases execute base-to-derived; cleanup executes derived-to-base, with reached-level guards and nested `try/finally` so a failing derived cleanup does not simply drop base cleanup. An inherited lifecycle is always marked `NUNIT_LIFECYCLE_REQUIRES_REVIEW`: source/target exception behavior, package-level NUnit SetUpFixtures, fixture instance lifetime and browser semantics require actual testing.

No runtime order is invented for multiple identical hooks within one class. A hidden/overridden inherited hook, unresolved base type, inherited metadata-only lifecycle, invalid hook signature, unpaired teardown, `InstancePerTestCase` one-time hooks, xUnit target or conflicting host SetUpStatements creates blocking diagnostics and an inconclusive NUnit SetUp rather than an apparently passing test. Plain single-level NUnit SetUp without inherited/extra phases retains the old rendering path. All lifecycle actions are included in quality accounting. Synthetic Core tests verify three inheritance levels and source-order effects, plus negative cases.

This release does not translate namespace/assembly SetUpFixture scope or external source-only hooks; it fails closed when the inherited source chain cannot be resolved. No private ArBilling implementation or selectors are committed.

## NUnit SetUpFixture namespace and assembly scopes (preview.16)

`[SetUpFixture]` is a standalone NUnit suite component, **not** a fixture `[OneTimeSetUp]` copied into every migrated test class. The Roslyn analyzer discovers source `SetUpFixtureAttribute` by NUnit symbol identity within a single assembly compilation, including global (assembly) and nested namespace scopes. It records the containing suite dependency outermost-first (global → parent namespace → child namespace), with teardown naturally owned in reverse by NUnit.

Core now supports a separately generated `[SetUpFixture]` target class from source files with **zero test methods**. `analyze_migration` includes those files in discovery, and `preview_migration` produces a distinct, content-hashed C# output. The target keeps the source namespace rather than adding a trailing `.Playwright` that would break ancestor scope relations. Test fixtures under a scoped declaration also keep source namespaces; their generated code references source-backed scope dependencies by name but **does not repeat** one-time effects in per-test or per-class setup. Include all generated scoped files in one NUnit target assembly to execute the correct once-per-scope behavior.

Only explicit standalone, Page-independent mapped actions are active in target `SetUpFixture` hooks. Unmapped source operations become visible `NUNIT_SETUPFIXTURE_ACTION_REQUIRES_MAPPING` blockers plus `Assert.Inconclusive`. A duplicate setup fixture at one namespace level, invalid per-test hook, non-public/no-default-constructor type, source member state and unsupported inherited behavior fail closed. Host target namespaces that do not preserve source containment, xUnit conversion, and experimental IR V2 produce explicit errors rather than fake successful tests.

`NUNIT_SETUPFIXTURE_DEPENDENCY_REQUIRES_REVIEW` and `NUNIT_SETUPFIXTURE_SCOPE_REQUIRES_REVIEW` remain until generated suite artifacts, actual NUnit execution, authentication/context and browser observables are validated. Even source-complete mappings are only review candidates, never automatic proof of behavior. This alpha slice does not infer missing setup fixtures outside the known project source compilation. No private ArBilling source is committed.

## Project-level NUnit target assembly (preview.17)

Core now produces a **content-addressed assembly plan** from the whole source C# SDK project, not a set of unrelated test files. `MigrationEngine.PlanTargetAssembly(sourceDirectory, configPath)` calls current analysis/preview and inspects source-backed namespace/assembly `SetUpFixture` dependencies. It requires one source `.csproj`, checks every generated source, expected fixture/test count, namespace identity and scoped dependency closure, then generates an exact `Migrator.Generated.Tests.csproj` plus `migrator-assembly-manifest.json` containing source analysis ID, candidate ID, source/preview tree hashes and per-file SHA-256.

The generated SDK project sets `EnableDefaultCompileItems=false` and lists **every** target `.cs` file in explicit `<Compile Include>` entries. No implicit MSBuild glob can silently include a stray source file or mask a missing `SetUpFixture`. All SetUpFixture files are compiled in the same NUnit assembly; their original global/namespace scopes remain intact.

`MaterializeTargetAssembly` is explicit and atomic: it writes only to a **new directory outside the source tree**, without overwriting an existing target. `VerifyTargetAssembly` checks the exact tree, manifest SHA, project bytes and explicit Compile set, refusing missing/extra/edited `.cs` or project files and symlinked paths. Build output directories `obj/`, `bin/`, `TestResults/` are excluded from source-artifact comparison only after the generated file set is fixed.

Real `dotnet build` and `dotnet test` are separate opt-in stages. NUnit runtime is `passed` only after a real nonzero TRX count confirms all tests passed (no failures/skips). No runtime or behavior success is invented from syntax or successful project generation. Actual target browser access, credentials and business observables are not managed by the generator.

CLI (from the Core repository):
```bash
dotnet run --project Migrator.AssemblyCli -- plan ./source-project --config ./adapter.json
dotnet run --project Migrator.AssemblyCli -- generate ./source-project ./new-target-project --config ./adapter.json --build --test
dotnet run --project Migrator.AssemblyCli -- verify ./source-project ./new-target-project --config ./adapter.json --build
```

The new generated-assembly smoke runs a full synthetic project with assembly-global, parent and nested namespace SetUpFixtures, two nested test classes and one sibling-namespace fixture: **3/3 real NUnit tests** assert once-per-scope state, parent-to-child setup and reverse teardown. Negative tests delete a required SetUpFixture, add an unexpected source, modify the target csproj and attempt cross-project or overlapping source/target operations; none may be reported buildable. No customer source, selector or URL is included.
