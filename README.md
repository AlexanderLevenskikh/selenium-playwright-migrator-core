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

## Multi-project NUnit solution generation (preview.18)

Core supports a **separate project graph**, not a flattened assembly: `PlanTargetSolution(sourceRoot, configPath?)` discovers 2–20 SDK-style `.csproj` under a source root, validates literal `ProjectReference` edges, checks cycles, external/missing references, source ownership, target-safe shared libraries and all independent NUnit `SetUpFixture` boundaries. Each original NUnit test project becomes a **distinct target NUnit assembly**, including only its own global/namespace `SetUpFixture` types. Each shared, target-safe source library becomes its own target SDK library, with the original source types and package-free `ProjectReference` edges preserved. Test-to-test `ProjectReference` is blocked.

The target solution contains `Migrator.Generated.slnx`, one generated `Migrator.Generated.Tests.csproj` per test project, one `Migrator.Generated.Library.csproj` per shared library, every exact source-backed generated/copied C# file, and `migrator-solution-manifest.json`. Each generated project disables SDK auto-Compile globs and lists every local C# file plus every referenced target library explicitly. The content-addressed plan binds source/project/config hashes, per-file SHA-256, a solution manifest and graph identity. `MaterializeTargetSolution` publishes only to a new, non-overlapping target directory. `VerifyTargetSolution` rejects a missing `SetUpFixture`, library file, `.csproj`, `.slnx`, manifest, added/unexpected C# file or changed `ProjectReference` before any build runs.

`dotnet build Migrator.Generated.slnx` and `dotnet test` per individual test assembly are opt-in stages with separate, mandatory per-assembly NUnit TRX counts. Runtime status is always `runtime_passed_review_required` — compilation/parallel project isolation does not prove source/target business equivalence.

CLI:
```bash
dotnet run --project Migrator.AssemblyCli -- solution-plan ./source-solution --config ./adapter.json
dotnet run --project Migrator.AssemblyCli -- solution-generate ./source-solution ./new-target-solution --config ./adapter.json --build --test
dotnet run --project Migrator.AssemblyCli -- solution-verify ./source-solution ./new-target-solution --config ./adapter.json --build
```

**Boundaries:** This preview intentionally fails closed on dynamic MSBuild imports/conditions, `Compile` glob overrides, nested or same-directory project ownership, unresolved/duplicate/cyclic/external references, test-project-to-test-project references, and libraries carrying NUnit/Selenium/Playwright lifecycle or external NuGet packages. Shared libraries must be reviewed as **target-safe unchanged C#**, not transplanted Selenium framework code. Test projects retain their source namespace identity. The synthetic Core regression generates four target projects (two test assemblies plus two shared libraries), compiles the complete `.slnx`, executes three NUnit tests with 2/2 and 1/1 per-assembly TRX, and checks missing/injected files and altered project graphs. All source code and business data are fictitious; private ArBilling E2E remains not run.

## NUnit opt-in and ignore safety (preview.19)

Source NUnit `[Ignore]` and `[Explicit]` attributes, including `Ignore` and `Explicit` named arguments on `[TestCase]` or `[TestFixture]`, can change whether a test executes at all. Until a source-backed attribute-preserving renderer is implemented, `Analyze` returns `unsupported_nunit_execution_gate` and `NUNIT_EXECUTION_GATE_REQUIRES_REVIEW` with source line evidence. `Preview` refuses to produce any runnable target file for that fixture. Declared tests remain counted, preventing false-green loss.

Only local syntax-recognizable gates are covered by this preview; inherited aliases, dynamic `TestCaseData` skip logic and other NUnit scheduling/selection attributes still require explicit source/project review. Synthetic Core regression tests six common gate spellings plus an unaffected regular NUnit test. Private customer code is not shipped.

## Preview.20 — fast source-graph preflight and reviewed shared NuGet libraries

Full multi-project planning now validates the complete .csproj graph, source
library safety, and package review profile **before** calling expensive
per-project Roslyn/NUnit analysis. When a graph is already blocked, output
contains \`SOLUTION_NUNIT_PLANNING_DEFERRED\` and no runnable NUnit target files;
the plan cannot be materialized. This prevents impractical repeated source
compilation on large, already-invalid solutions. Valid graphs retain independent
NUnit assemblies and source-bound content-addressed plans.

A shared library with **literal, pinned** NuGet PackageReferences can now be
projected into its generated target .csproj *only after* an explicit source-exact
review approval. Add this optional section to the existing adapter JSON (all
values are fictional):

\`\`\`json
{
  "SourceProjectName": "Synthetic",
  "TargetSolutionReviewedPackages": [
    {
      "SourceProject": "Shared/Shared.csproj",
      "Reviewed": true,
      "Packages": [{ "Id": "System.Collections.Immutable", "Version": "9.0.0" }]
    }
  ]
}
\`\`\`

The exact source project path, every package ID and version must match the
source project, with no unapproved extras. Floating/ranged versions, central
package management, conditions, non-NuGet references, package metadata,
unknown profile projects, unsafe browser/test-framework dependencies, and
driver-bearing shared C# code remain blocked. The entire adapter config hash
is part of the target plan ID and generated .csproj is included in the exact
target SHA manifest. A reviewer approval **does not** assert third-party
package trust, source/target API compatibility, real MSBuild equivalence, or
runtime/business behavior. Restore/build/test and source-specific review are
still required. The plan never silently invents dependencies.

Core smoke includes synthetic positive exact dependency projection plus
negative changed version, floating version, invalid project path and
NUnit-planning short circuit. No private/customer code is used.

## Preview.21 — complete source test inventory before NUnit planning

A blocked multi-project graph now retains the **lexically declared NUnit method
count** per source project, even when target generation is correctly deferred.
A null candidate ID means no target candidate was built; the nonzero
\`DeclaredTests\` field represents original NUnit methods, not runtime-expanded
parameter cases or generated tests. The solution schema is now v1alpha3.

The inexpensive one-pass Roslyn preflight also detects the source behaviors
that would otherwise become false-green or expensive late failures:
nested \`[SetUpFixture]\` ownership,
assembly-level NUnit \`[Parallelizable]\` / \`[NonParallelizable]\` /
\`[LevelOfParallelism]\` scheduling attributes,
source \`[Ignore]\` / \`[Explicit]\` execution gates,
dynamic \`[TestCaseSource]\` data-provider semantics and xUnit source.
All produce source-backed, actionable blockers before costly fixture plans.
They are not silently dropped, reproduced by guesswork or counted as converted.

Shared libraries are checked using the syntax trees already parsed for
project preflight, avoiding a duplicate source parse. Public synthetic
regressions cover the extra diagnostics, genuine declared method counts,
source hash safety and ordinary generated NUnit assembly behavior.
These gates preserve source/test safety; they do **not** automatically migrate
customer hooks, data providers, browser state or test concurrency.

## Preview.22 — explicit entry-project scope and dependency closure

A large SDK source graph may include independent API test assemblies, code
generators or tools that are **not** part of the requested UI migration. Set
\`TargetSolutionEntryProjects\` in the same adapter JSON to select one or more
**NUnit entry projects** explicitly. Only the selected entry projects and
**all reachable literal in-root ProjectReference dependencies** are planned;
nothing is inferred or silently dropped:

\`\`\`json
{
  "SourceProjectName": "Synthetic",
  "TargetSolutionEntryProjects": ["UiTests/UiTests.csproj"],
  "TargetSolutionReviewedPackages": [
    {
      "SourceProject": "Shared/Shared.csproj",
      "Reviewed": true,
      "Packages": [
        { "Id": "System.Collections.Immutable", "Version": "9.0.0" }
      ]
    }
  ]
}
\`\`\`

The library list is illustrative, **not automatically approved**. Entry paths
must be unique, exact, normalized source-relative .csproj paths, and each
entry must own NUnit tests or a source-backed SetUpFixture. The scope closure
is computed from literal ProjectReference edges. Missing/dynamic/external
dependencies fail closed. Referencing another test assembly pulls that project
into scope and still fails the test-to-test reference rule. Nested projects
and multiple project files in an included project's source directory remain
blocked even when the nested/sibling project is excluded, because SDK default
Compile globs make ownership ambiguous.

The v1alpha4 plan includes \`scope.explicit\`, \`entryProjects\`,
\`includedProjects\` and \`excludedProjects\`, and an
\`SOLUTION_SCOPE_EXCLUDED_PROJECTS\` warning. Excluded NUnit tests are **not
migrated or counted**. The content-addressed plan manifest includes the
scope; changing the adapter invalidates plan and verification IDs. The
source-tree hash continues to cover the original full workspace for
conservative provenance, including excluded files. An explicit scope can
contain one to twenty projects; the implicit whole-solution mode retains
its original 2–20 project requirement and behavior.

A synthetic regression runs a *real* independently generated scoped NUnit
assembly with 2/2 tests and a separate full 3/3 solution. It verifies target
.csproj/.slnx closure, omitted assembly/global SetUpFixture exclusion,
missing/invalid entries and reachable test-to-test references. A real
product's behavior, external APIs, auth and browser UI remain unverified.

## Preview.23 — source-backed NUnit assembly scheduling

The previous source-graph preflight deliberately blocked assembly-level
\`[Parallelizable]\`, \`[NonParallelizable]\` and
\`[LevelOfParallelism]\` because per-file target previews dropped them.
Core now recognizes a small, strict syntax whitelist of **literal**
NUnit assembly scheduling attributes, including
\`ParallelScope.Fixtures\` and positive integer worker counts.
Accepted source attributes produce a distinct
\`Migrator.Generated.NUnitAssemblyScheduling.cs\` file in **that source
NUnit assembly only**. Its full text, SHA-256 and explicit
\`<Compile Include>\` participate in the target assembly/solution hashes
and strict materialization verification.

Both \`PlanTargetAssembly\` (v1alpha2) and
\`PlanTargetSolution\` (v1alpha5) expose an \`assemblyScheduling\`
ledger with original source paths and lines, original attribute
expressions, and emitted fully-qualified NUnit expressions. The exact
manifest embeds the same source-backed provenance. No arbitrary source
assembly annotations are copied. An unsupported enum expression,
nonliteral worker count, duplicate/conflicting attributes, scheduling
alias, shadowing type or fixture/method-level scheduling produces an
explicit blocker rather than a guessed default.

CI generates two separate NUnit test assemblies with independent
source parallelism values. It verifies actual \`dotnet build\` and
per-assembly NUnit TRX, reflection-observed assembly attributes,
source/target manifest hashes, missing/altered generated scheduling
files and fail-closed negative cases. **These checks establish source
attribute preservation and synthetic runtime tests, not behavioral
equivalence for real browser/application scheduling**, thread safety,
timeout behavior, or inherited setup/teardown effects.

A nested \`SetUpFixture\` remains blocked rather than being silently
hoisted into assembly-global scope. Source-specific fixture DI,
listener reflection, pooled browser lifetime, log/screenshot teardown
and third-party extensions require independent migration and review.
No private customer source or test evidence is used in GitHub/CI.


## Preview.24 — NUnit lifecycle source evidence and standalone cleanup scopes

\`MigrationEngine.InspectNUnitLifecycle(sourceDirectory)\` provides a
**read-only, source-hashed Roslyn inventory** of the original NUnit lifecycle,
including hooks in abstract base classes and nested \`SetUpFixture\` declarations.
The bounded ledger records method SHA-256, source paths/lines, zero-argument
same-class private helper lineage (up to five levels), original test-method
counts, and source evidence of project DI/browser effects:

- Scoped service and pooled WebDriver ownership, navigation, storage
- OneTimeSetUp/SetUp/TearDown/OneTimeTearDown declarations
- NUnit internal listener/reflection and retry helpers
- Browser log and screenshot side effects
- Per-fixture and suite-wide DI cleanup, including nested cleanup-only scope

The inspector does not return original C# bodies, URLs, selectors or literal
configuration values. \`sourceTreeHash\` changes whenever included source
bytes change. The ledger **does not** evaluate dynamic dispatch, external DI
registrations or NUnit runtime results. All report execution stages remain
\`not_run\`. A nested \`[SetUpFixture]\` is always an error-level issue:
its \`OneTimeTearDown\` cannot be promoted into an assembly-global cleanup
by naming convention or an invented dependency injection mapping.

Separately, a **valid standalone namespace/assembly SetUpFixture** can now
declare only \`[OneTimeTearDown]\` with no matching
\`[OneTimeSetUp]\`. NUnit owns that cleanup lifetime; the Roslyn
lifecycle parser exempts *only* that scoped one-time cleanup from the
ordinary fixture-level paired setup guard. This is **not** permission to
move invalid nested classes or dispose shared browser resources from an
arbitrary target fixture. Explicit mapped actions still require a
source-backed review. Synthetic Core CI verifies generated NUnit test
assemblies, namespace and assembly cleanup ordering, and exact target file
closure using real \`dotnet build\` and NUnit TRX.

A public fictional \`TestBase\` regression verifies source-bound DI/browser
dependencies, reachable private reflection listener logic, nested
cleanup-only blockers, source hash invalidation, and honest
\`not_run\`/unverified statuses. No proprietary customer source or
observations are sent to GitHub/CI.

**Remaining work:** reviewed browser-context/page binding to original DI,
safe listener/retry replacement, teardown screenshot/log fidelity and
independent failure-path NUnit + real product/browser execution.


## Preview.25 — observed nested NUnit cleanup and source-bound review candidate

A synthetic NUnit runtime probe runs **NUnit 3.14.0** and **NUnit
4.3.2**, including unrelated and inherited fixtures in the same
namespace plus a failing \`[OneTimeSetUp]\`. Both runtimes execute
a nested \`[SetUpFixture]\` \`[OneTimeTearDown]\` once after the
namespace tests; omitting it or treating it as a per-test TearDown
would silently lose behavior. The probe never uses customer source.

\`MigrationEngine.ReviewNestedNUnitCleanup(sourceDirectory, sourceType,
expectedSourceTreeHash, targetStatements, sourceRuntimeEvidenceSha256?)\`
returns a read-only, source-attested
\`migrator-nunit-nested-cleanup-review/v1alpha1\` contract. Supported
cases are strictly limited to a single public, non-generic nested
\`[SetUpFixture]\` inside a public, non-generic owner, with exactly one
parameter-free synchronous \`[OneTimeTearDown]\`, no state and only
direct invocation statements. Callers must supply one explicitly
reviewed target call per source call. Candidate C# **retains the
original outer/nested type envelope and NUnit namespace**; it never
automatically moves the hook to an assembly fixture or into a test's
ordinary teardown. Source tree hash, method SHA-256, original namespace,
owner, method name and target SHA-256 attest provenance.

Missing mappings, changed source, arbitrary control flow,
invalid/missing types, competing same-scope SetUpFixtures, extra
lifecycle methods and unsafe target statements are blocked. A valid
target artifact remains \`requires_review\`, with
\`BehavioralEquivalenceVerified=false\` and source/target build,
NUnit and browser observations all \`not_run\`. An optional external
runtime evidence SHA-256 is only a caller-supplied provenance
identifier — the reviewer cannot authenticate or execute that run.
The normal solution planner does **not** automatically include this
candidate or clear the nested-scope blocker.

The synthetic Core smoke uses the actual API to generate the candidate,
then independently \`dotnet test\`-executes original and target NUnit
3/4 projects, comparing successful two-test scope cleanup and failed
fixture \`OneTimeSetUp\` paths. These are framework-focused checks, not
evidence for any private product's DI, pooled browser, screenshot,
listener, logs or global resource lifetime. A privately reviewed
\`FullDispose\` mapping and source/target NUnit+browser parity are
required before integrating a real cleanup candidate into a target
solution. **Never ship a guessed cleanup action merely to unblock
generation.**

## Preview.26 — reviewed Playwright fixture DI and BrowserContext/Page lifetime

\`MigrationEngine.ReviewPlaywrightFixtureLifetime(root, fixtureBaseType,
nestedCleanupType, infrastructureType, expectedSourceTreeHash, bindings)\`
checks an exact source-hashed Roslyn inventory of an inherited NUnit fixture,
its nested cleanup-only SetUpFixture and the **separate source DI container**.
Each source effect must be explicitly reviewed and bound one-to-one using
source relative path, line number, expression SHA-256 and the exact target
role. The role whitelist covers fixture DI, BrowserContext/Page, navigation,
localStorage page binding, failure logs/screenshots, fixture release, global
release and retry listener replacement. Missing, extra, stale, mismatched or
unreviewed mappings are blocked. Additionally, the source DI implementation
must prove a class-name-based NUnit scope key and source-backed GetOrAdd /
CreateScope, TryRemove / Dispose, and global Clear / Dispose operations.
Changed source bytes invalidate the review.

On complete explicitly reviewed **structural** bindings, Core returns a
separate \`requires_review\` candidate
\`Migrator.Generated.PlaywrightFixtureLifetime.cs\`, never a silently
materialized project or a claim of behavioral equivalence. It compiles against
real \`Microsoft.Playwright.IBrowserContext\` and \`IPage\` types and defines
an external \`IMigratorReviewedPlaywrightLifecycleBridge\`. The caller must
supply and verify that bridge in the target project's actual DI container.
The generated NUnit abstract base owns a lazy fixture-context/page pair
keyed by \`TestContext.CurrentContext.Test.ClassName\`, resolves the page on
per-test TearDown, obtains browser logs **before** screenshot only on failed
tests, returns fixture services on OneTimeTearDown, always closes the owned
browser context even after a release failure, and delegates global provider/
browser disposal to the **original nested cleanup-only SetUpFixture**.
It does not invent URLs, selectors, credentials or a shared browser instance.

A real Selenium WebDriver pool (driver recycling) is **not inherently
equivalent** to a Playwright BrowserContext.CloseAsync. Retry-listener
replacement, navigation/POM mapping, JavaScript storage, screenshot/log
artifacts and the concrete shared provider lifecycle require explicit,
source-specific implementation and runtime evidence. In particular, target
review \`Reviewed=true\` attests a supplied source-to-role mapping, **not**
a verified target implementation. Source NUnit, target build, NUnit runtime,
browser E2E and behavioral equivalence stay \`not_run\` /
\`not_verified\` in the review API.

The public synthetic smoke validates complete/partial/extra/stale role
mapping, source DI ownership changes, and uses an actual Playwright NuGet
type-compatible target assembly with a recorded fake browser provider to
execute NUnit success, failed test and failed fixture release. It checks
per-fixture BrowserContext creation/closure, failure-only logs and screenshots,
nested one-time provider disposal, and real NUnit TRX counters. This does
**not** use an actual browser and is not customer E2E evidence. No private
customer source, URLs, selectors or secrets enter GitHub or CI.


## Preview.27 — concrete, opt-in Playwright / .NET DI browser runtime

\`ReviewPlaywrightFixtureLifetime(...)\` now returns **two** separate,
SHA-256-bound source-backed review artifacts after every source role is
explicitly attested:

- \`Candidate\`: original NUnit fixture base plus nested cleanup-only
  \`SetUpFixture\` with \`IBrowserContext\`/\`IPage\` ownership.
- \`ConcreteBridge\`: a real \`MigratorPlaywrightDiBridge\` implementation
  of \`IMigratorReviewedPlaywrightLifecycleBridge\`, compiling against
  Microsoft.Playwright and Microsoft.Extensions.DependencyInjection.

The concrete bridge accepts an externally created shared \`IBrowser\` or
can launch an owned Chromium instance through
\`LaunchChromiumAsync(rootProvider, options)\`. It acquires an
\`IServiceScope\` for each NUnit fixture, opens a separate
\`IBrowserContext\` via \`IBrowser.NewContextAsync\`, records bounded
per-page console/page-error messages, and exposes the page through the
generated fixture. It captures failure logs **before** a full-page
screenshot at test teardown (not on successful tests); artifacts are
saved under an explicitly selected absolute directory and registered
with NUnit. At fixture teardown, the bridge releases the DI scope,
while the generated base closes its context in a \`finally\` even if
the service scope fails. Nested source-style
\`[OneTimeTearDown]\` performs shared browser/provider disposal only
after all fixture scopes have returned.

### Explicit retry contract

Unlike Selenium's private listener reflection, NUnit retry attributes
cannot be injected retrospectively by a fixture-level
\`OneTimeSetUp\`. The concrete bridge therefore validates **explicit
per-test \`[Retry(N)]\`** annotations against
\`RequiredRetryAttempts\` (for example, three source retries after
the first attempt require explicit NUnit \`[Retry(4)]\`). A missing
annotation fails closed before test execution. NUnit's own retry
command may repeat SetUp/TearDown differently from an original
custom listener; migration is **not behaviorally equivalent** merely
because the attempt counts match.

### Integration and test status

This is a compile-capable, opt-in runtime implementation but still a
\`requires_review\` proposal, **not automatically materialized** into
the full target solution. A real customer source must explicitly
provide the application-specific DI registrations, browser launch
environment, authentication, navigation, storage/POM implementations,
exact retry policy and required artifact directory. The engine does
not invent URLs, selectors, credentials or per-environment settings.

Public synthetic Core CI builds against real \`Microsoft.Playwright\`
and \`Microsoft.Extensions.DependencyInjection\` packages, injects an
instrumented \`IBrowser\` proxy (no browser download or real website)
and runs NUnit/TRX for success, final failure, retry success, DI
release failure and missing retry policy. It verifies one context
per fixture, context closure and single global browser/provider
disposal; logs-before-screenshot and screenshot attachment only
on failures; and conservative review-only statuses. This is
**type-compatible NUnit runtime evidence**, not an actual browser
session or private application E2E. The real Chromium launch path
and its environmental dependencies require a separate browser
smoke before production usage.


## Preview.28 — actual Chromium / localhost Playwright browser smoke

Core CI now builds both **source-attested generated** fixture artifacts
and runs them against a real Playwright-installed Chromium binary, rather
than only a recorded fake \`IBrowser\`. A synthetic in-process HTTP
server listens on a random \`127.0.0.1\` port; it serves a small HTML
page with an interactive button and JavaScript console message.

The new \`--real-chromium-only\` smoke compiles a generated
\`MigratorPlaywrightDiBridge\` and NUnit fixture, installs the exact
Playwright 1.63.0 Chromium revision with the .NET CLI, and performs
three independent \`dotnet test\` / TRX runs:

1. Passing two-fixture scenario: real DOM click, two separate
   BrowserContexts/Pages, isolated \`localStorage\`, browser console,
   two fixture service scopes and context closure before one nested
   shared browser/provider dispose. No failure artifacts are emitted.
2. Successful \`[Retry(3)]\` after one failed attempt: actual two
   invocations with real browser and fixture lifetime; intermediate
   failure screenshot/log behavior is recorded, not assumed identical
   to Selenium's custom retry listener.
3. Final failure after three attempts: real console log file with
   JavaScript message, genuine PNG screenshot checked by PNG signature,
   NUnit TRX with one passed and one failed case, and the same
   closure/disposal counts. Logs must be saved before the screenshot.

\`MigratorPlaywrightBridgeOptions.LifecycleObserver\` is an opt-in
diagnostic callback emitting semantic event names only (context open/
close, fixture DI scope create/dispose, failure attachments and
shared browser/provider dispose). It does not log source C#, URLs,
selectors, credentials or customer service data. Browser startup is
triggered asynchronously from NUnit setup via a synthetic lazy bridge,
never synchronously from a CLR module initializer.

CI publishes versioned
\`migrator-real-chromium-smoke/v1\` JSON evidence, a **synthetic** PNG
and browser console log artifact. The separate MCP CI reads and
validates this evidence after independently running pinned Core's
real-browser acceptance; Windows MCP CI still runs its original
standalone protocol and NUnit checks.

This is real browser automation **on an invented loopback web app**,
not ArBilling E2E. Customer infrastructure, authentication,
Original Selenium browser pooling/retry and Playwright target behavior
are still \`not_run\` / \`not_verified\`. The source-bound review
contract remains \`requires_review\`: genuine synthetic Chromium
success never automatically unblocks a private NUnit solution.

## preview.29 — NUnit execution gates and bounded TestCaseSource

Core preserves source-attested fixture/method `[Ignore("reason")]` and
`[Explicit("reason")]`, plus NUnit `[TestCase(..., Ignore = "...")]`
and `[TestCase(..., Explicit = true)]`, without silently activating
previously disabled cases. Each safe attribute has a source relative path,
line and SHA-256 digest; static NUnit cases report method, run-state and
reason. A declared annotated method is **not** a discovered parameterized
NUnit test case. Analysis and preview expose additive inventories using
`migrator-analysis/v1alpha2` and `migrator-preview/v1alpha2`.

`TestCaseSource` is lowered only when a local, uniquely named static
readonly field or expression-bodied static property provides a nonempty
literal-only array of object[] rows or plain `new TestCaseData(literal)`
rows. Parameter count and conservative primitive type matching must agree.
The parser never invokes user code to evaluate `IEnumerable` providers.
Environment-dependent, external/inherited, side-effectful and ambiguous
sources stay blocked with line-bound `NUNIT_TEST_CASE_SOURCE_UNSAFE`
diagnostics. Inherited Ignore/Explicit fixture gates discovered in other
source files stay blocked, not silently dropped. Broader NUnit compatibility
is future work and must be separately runtime-proven.

The synthetic CI explicitly runs `dotnet test --list-tests`, TRX default
runs, selected `Explicit` success and a deliberately failing selected
`Explicit` case on NUnit 3.14 and 4.3 source assemblies. It also compares
NUnit 3 source with the actual generated target, including discovered
case names, per-case outcomes, and skip reasons on Linux and Windows.
The trusted CLI `--build --test` path validates actual target TRX.
**NUnit3TestAdapter may report `notExecuted=0` in TRX Counters even when
five `UnitTestResult` entries have outcome `NotExecuted`**; the assembly
verifier reads individual outcomes and rejects missing, activated, unknown
or failing cases. Passing synthetic tests does **not** mean a private customer-application build, NUnit runtime, browser E2E or semantic equivalence ran.
Only explicitly executed and recorded test commands may establish runtime
evidence.

## preview.30 P2 — source-backed Page Object actions (bounded)

`MigrationEngine.PlanPomActions(sourceDirectory, sourceType, sourceMethod,
expectedSourceTreeHash, sourceLine?, receiver?)` uses a bounded Roslyn AST
and semantic compilation. It returns source-relative path, line, SHA-256,
typed source method/callee identities, return/receiver types and ordered
target Playwright statement candidates for a selected source method.
A nonempty result is always `requires_review`; the engine **does not
automatically apply its proposals**. One unknown call, branch, loop,
dynamic selector, unsupported signature or environmental URL blocks the
entire target sequence, never a partially runnable migration.

The closed translation subset covers literal Selenium `By.Id`,
`By.Name`, `By.CssSelector`, `By.XPath` on typed
`IWebDriver.FindElement/FindElements`, click, clear, SendKeys,
`Navigate().GoToUrl`, a bounded `WebDriverWait.Until` visible-lambda
with explicit TimeSpan, NUnit `Assert.That` text/count equality and
constant-index table row clicks. ComboBox and Loader methods are not
special-cased by name: **every** source effect must be accounted for.
Unknown or dynamic POM code remains blocked. Compile-only Selenium
type anchors assist Roslyn resolution but do not attest installed
customer Selenium assemblies. Review Playwright's actionability,
wait/polling behavior, locator scoping and input semantics in Chromium.

The trusted Core CLI additionally exposes read-only
`pom-inspect <directory>` and
`pom-plan <directory> <type> <method> <exact-source-hash> [--emit-target]`.
By default POM target expression text is redacted; explicit
`--emit-target` shows review-required statements locally without
editing the source or target. MCP's `inspect_pom_action_plan` always
returns only source line/hash, typed effect kinds and target SHA-256
(no private source bodies, selectors or URLs).

Public Linux and Windows CI runs genuine NUnit 4 discovery/TRX on
fictional source and target suites and a separately installed
Playwright Chromium against a loopback HTTP app. The source-side
suite is an NUnit + HttpClient **control**, not Selenium WebDriver
browser execution. The generated target executes the actual
source-attested POM statements under Chromium; default cases, selected
Explicit success/failure, PNG screenshot, console events and nested
browser/context close evidence are verified. This is **not** proof
of private product Selenium/Playwright behavioral equivalence.

## preview.31 P2 — structured, source-bound Page Object migration

The read-only Roslyn `MigrationEngine.PlanPomActions` supports a **bounded
source subset**, not general interpretation of customer C#. It now retains
both branches of typed Selenium `FindElements(By.literal).Count` predicates
and literal string parameter `==` / `!=` guards (including source
short-circuit `&&` and `||`). Every action reports a relative source path,
line, AST SHA-256, Roslyn source type/symbol and `ControlFlowPath` such as
`if:true@line`, `if:false@line` or `helper:OpenPicker@line`.

Non-virtual, same-class, zero-argument private POM methods may be inlined to
a depth of four, but **every** statement of every reached method must be
supported. A recursive helper, unknown method, complex condition, script
side effect, local mutation, loop, arbitrary exception path or missing
branch blocks the **entire** target sequence. It never executes customer C#
or guesses selector/URL/behavior.

A dynamic `WebDriverWait` timeout is eligible only when a source-matched
`int timeoutMs` has an explicit first-statement guard of the exact form
`if (timeoutMs < 1 || timeoutMs > 3000) throw new
ArgumentOutOfRangeException(nameof(timeoutMs));` (the finite bound and
variable are taken from source). The resulting Playwright timeout uses
`(float)timeoutMs` and remains **review-required**, not behaviorally
verified. Literal and compiler-constant `TimeSpan.FromMilliseconds/Seconds`
waits remain supported; unbounded dynamic waits block.

NUnit `Is.EqualTo` text/table assertions and strict `Is.GreaterThan` /
`Is.LessThan` table counts are retained without changing comparison
polarity. Runtime polling/actionability differences, locator matching,
error paths, fixture lifetime and side effects still need review.
The POM adapter candidate is opt-in; no TODO or review candidate is
counted as a fully migrated test.

The CI `scripts/p2_structured_source_target_chromium_runtime.py` drives
a **real installed Playwright Chromium** on a fictional loopback app,
covering both arms of a conditional ComboBox, a finite guarded Loader
wait, table assertions, default NUnit discovery/TRX, selected Explicit
success, intentionally failing Explicit test, screenshots, console events
and browser/context cleanup on Linux and Windows. Source-side NUnit
controls are HttpClient-based, **not Selenium WebDriver execution**;
matching NUnit cases/exit codes do not prove Selenium-to-Playwright
business equivalence. No customer source/evidence is uploaded to CI.

## preview.32 P2 — typed nested POM helpers and source-bounded Loader retries

`MigrationEngine.PlanPomActions` adds a closed, **review-required** AST
subset for exact same-class nonvirtual private POM helper calls with
zero or one typed `string` argument. Roslyn must resolve that argument
to a source parameter or literal; generated C# identifier-token
substitution forwards it through nested helpers without rewriting
selector/URL literal bytes. Every reached helper statement is inspected.
The whole POM method remains `blocked` on unknown calls, arbitrary
side effects, recursion, dynamic helper arguments or nested flow beyond
the bounded source scope. Every effect has source path/line/SHA,
Roslyn symbol and `ControlFlowPath`.

The only newly reviewed retry shape is a **literal 2–4 attempt**
Selenium `for` loop containing precisely one `WebDriverWait.Until`
with source-derived finite timeout, a `break` on success, and an
unfiltered `catch (WebDriverTimeoutException)` whose last iteration
`if (attempt == N-1) throw;` is preserved. The candidate catch uses
the documented .NET `System.TimeoutException` path; actual
Selenium-to-Playwright exception/polling equivalence is **not**
established by Roslyn and requires Chromium/runtime review. Changed
catch filters, failure conditions, extra calls, unbounded loops,
unknown URLs/selectors and implicit retries are **blocked**.

A POM method whose name looks like a wait (e.g. `page.WaitReadyRetry()`)
can be recognized by the legacy frontend as `WaitForAction`.
Attach the **exact source-call** reviewed `MethodMapping` for such
zero-argument waits; otherwise the conservative fallback intentionally
renders `WAIT_REQUIRES_STATE_ASSERTION`. String-argument POM methods
use reviewed `ParameterizedMethodMapping`. Do not treat an omitted
wait as migrated. The isolated Core smoke tests both mapping paths
through Roslyn parser → adapter → Playwright renderer and requires
`FullyConvertedTests == 0` until behavioral review.

`scripts/p2_typed_retry_source_target_chromium_runtime.py` runs
actual installed Playwright Chromium on a fictional localhost-only
application for both ComboBox branches, a typed nested helper,
successful finite retry, selected timeout failure, browser console,
real PNG screenshot, and context/browser cleanup. Synthetic NUnit
source control and target discovery/TRX are compared case-by-case
on Linux and Windows (6 cases: 2 passed / 4 not executed by default;
selected Explicit success and both deliberate/timeout failure paths).
Source control uses NUnit/HttpClient, **not Selenium WebDriver**,
so it is not private product behavioral equivalence evidence.


## Preview.34 — actual Selenium WebDriver vs Playwright .NET, same Chromium-family executable

The public real-browser parity job is separate from older HttpClient NUnit
controls. It installs an explicit Chrome for Testing binary and matching
ChromeDriver on Linux and Windows. Actual C# Selenium tests execute source
Page Object methods, while an independent Playwright .NET fixture executes
their review-only Roslyn-projected statements on the same localhost application
and exact same Chrome executable. The CI job compares TRX discovery/outcomes,
ordered browser application events, DOM values, Loader detachment, conditional
ComboBox branches, terminal timeout type, failure PNG and cleanup.

**Important negative control:** a source NUnit Count assertion is immediate,
but the candidate Playwright ToHaveCountAsync auto-retries. The synthetic DOM
race therefore intentionally yields Selenium Failed vs Playwright Passed.
This is a source/target behavioral **non-equivalence**, and the proposed
assertion remains review-only; never auto-apply it based on superficial
selector matching. Retry attempt bounds are source AST evidence; individual
attempts are not independently counted by this runtime test. Other timing,
exception, browser and product differences remain possible. No private
ArBilling code/credentials are used, and passing synthetic parity is not
proof of general automated migration correctness.


## Preview.35 — preserve immediate NUnit Count/Text assertion timing

Previous preview.34 real Selenium WebDriver versus Playwright .NET on one
Chrome binary established that the source
\`Assert.That(driver.FindElements(By.Id(...)).Count, Is.EqualTo(N))\`
fails immediately before a delayed insertion, whereas the candidate
\`Expect(locator).ToHaveCountAsync(N)\` retries and passes. The
source-backed review-only Roslyn candidate now emits a **single**
\`Assert.That(await locator.CountAsync(), Is.EqualTo(N))\` snapshot
for EqualTo, GreaterThan and LessThan. It does not claim that all
Selenium/Playwright locator behavior is equivalent.

Similarly, a source \`IWebElement.Text\` equality must not silently
become an auto-retrying \`ToHaveTextAsync\`. A new review-only candidate
takes a single non-waiting DOM query using
\`locator.EvaluateAllAsync<string>(...innerText...)\`, throws if the
element is absent, and then uses immediate NUnit EqualTo. The source
selector and literal row index are preserved, with no guessed
URL/side effects. **Important**: Selenium's rendered-text algorithm is
not universally equivalent to DOM \`innerText\` (notably hidden nodes
and whitespace). An absent element also throws a different exception
type. Both are explicitly measured and remain review-required, not
automatically accepted migrations.

The Linux/Windows preview.35 real Selenium WebDriver/ChromeDriver and
Playwright .NET job uses the same installed Chrome for Testing and
localhost synthetic app, compares normal Count/Text success and
positive DOM observables, and exercises selected negative controls:
delayed Count insertion (both fail), changing visible text (both fail),
missing text element (both fail with distinct exceptions), and hidden
text (observed residual non-equivalence). Runtime NUnit TRX, actual
browser snapshots, screenshots, browser binary/source method SHA-256 and
immutable artifacts are published. The old preview.34 fixture remains
in the repository as historical evidence; preview.35 CI uses the
newest assertion-corrected script.

No automatic conversion approval is implied. Private ArBilling is
\`not_run\`; independent retry-attempt counting remains unverified.


## Preview.36 — narrow source-backed rendered-text subset (real Selenium comparison)

Preview.35 removed auto-retrying Count/Text expectations, but an immediate
DOM \`innerText\` read was NOT Selenium \`IWebElement.Text\`: a hidden
\`display:none\` element yielded \`""\` under real Selenium and its secret
text under the old Playwright candidate. WebDriver Get Element Text
intends *rendered text* semantics, not raw DOM text.

Preview.36 updates only the review-required Roslyn \`IWebElement.Text\`
candidate to a single \`EvaluateAllAsync<string>\` DOM snapshot that checks
the source-selected element and ancestor CSS \`display:none\` /
\`visibility:hidden\` or \`visibility:collapse\` and returns an empty
string for those cases. It normalizes a narrow CSS normal-whitespace
subset (collapse spaces/tabs, trim line edges). For \`white-space: pre\`,
\`pre-wrap\` and \`break-spaces\`, the target deliberately throws
\`POM_TEXT_PRESERVED_WHITESPACE_REVIEW\`; it does NOT guess behavior.
Missing elements still fail immediately with a different exception type
than Selenium (PlaywrightException versus NoSuchElementException);
the exact source exception identity remains unproven.

The public Linux/Windows Chrome-for-Testing fixture executes the **same**
source Selenium WebDriver and projected Playwright .NET test methods,
including hidden root/ancestor, CSS visibility hidden, normal whitespace,
Count and visible Text races, an absent element, and the intentionally
unresolved preformatted text control. The checker requires normal and
hidden-subset matching DOM/events, matching source/target NUnit outcomes
on positives, and explicit non-equivalence on the preserved-white-space
negative. Exact source tree/method hashes, Chrome binaries, TRX, real PNG
screenshots and SHA256SUMS are retained. It is a public synthetic
subset, not an implementation or proof of the full Selenium/WebDriver
rendered-text algorithm.

**All proposed target statements remain \`requires_review\`;
\`CanAutoApply=false\`.** This code does not read or execute private
ArBilling; unsupported CSS and exception semantics must never be
treated as automatically migrated.


## preview.37 — narrow pre/pre-wrap and Unicode rendered-text candidate

Real WebDriver Get Element Text reports **rendered** text; it is
not interchangeable with DOM \`innerText\`, a universal JavaScript
regex, or Playwright's auto-retrying expectations. The review-only
Roslyn text candidate continues to inspect the exactly source-backed
locator **once** without auto-wait and preserves original literals,
parameters, source path/line and SHA.

The candidate now includes narrowly bounded, **runtime-tested**
single-line, leaf-node \`white-space: pre\` and \`pre-wrap\` cases.
It normalizes the simple tested Unicode \`U+00A0\` non-breaking
space to ordinary space, removes zero-width \`U+200B\` and bidi
markers \`U+200E/U+200F\`, and leaves combining codepoints unchanged.

The following fail closed with explicit review markers rather
than guessing behavior: \`break-spaces\`; preformatted multiline
and nested child elements; CSS text-transform; unknown computed
whitespace modes; unverified Unicode spacing such as narrow no-break
space (\`U+202F\`). An absent element fails immediately, but the
specific \`OpenQA.Selenium.NoSuchElementException\` vs
\`Microsoft.Playwright.PlaywrightException\` identity is **not
preserved** and must remain a manual catch/exception compatibility
review. No Selenium runtime dependency is introduced into the
production target candidate.

The public preview.37 GitHub Actions jobs execute real Selenium
WebDriver/ChromeDriver and source-projected Playwright .NET against
the **same Chrome for Testing executable** and localhost app on both
Linux and Windows. NUnit TRX includes positive pre/pre-wrap and
Unicode cases, negative source-observed CSS cases, and selected
count/text race, missing-element and timeout controls. It records
SHA-256 for the source tree, each source method, actual browser
binaries, screenshots, browser DOM events and resource cleanup.
Only explicitly compared bounded synthetic cases count as passed.
Private ArBilling remains \`not_run\`. The migration plan stays
\`requires_review\`, \`CanAutoApply=false\`, \`RuntimeVerified=false\`
regardless of fixture outcome.


## Preview.38 — exact source-backed NoSuchElement/Timeout catches (review only)

Before preview.38 the only accepted catch was an exact WebDriverTimeoutException
inside a bounded retry, while standalone C# try/catch was entirely blocked.

A new strict source-only Roslyn subset recognizes exactly one NUnit
\`Assert.That(driver.FindElement(By.literal).Text, Is.EqualTo(...))\`
under \`try\` caught by typed
\`OpenQA.Selenium.NoSuchElementException\`, or one finite symbol-bound
\`WebDriverWait.Until\` caught by
\`OpenQA.Selenium.WebDriverTimeoutException\`. The catch body may contain
one source-backed immediate \`FindElements.Count\` NUnit assertion or
bare \`throw;\`. The emitted draft remains **requires_review** and
\`CanAutoApply=false\`; no customer test source is executed by Core.

For a missing-text assertion only, the draft PlaywrightException catch has
a guarded \`when\` on the emitted \`POM_SOURCE_ELEMENT_ABSENT\` JavaScript
marker. It must not catch other Playwright errors (e.g., click auto-wait
timeout). For a source finite WebDriverWait, the mapped catch is
\`System.TimeoutException\`, as measured in Playwright .NET runtime.
A bare \`throw;\` rethrows the target exception object, **not** a Selenium
exception CLR type. External source callers that catch
NoSuchElementException/WebDriverTimeoutException still require review.
No hidden Selenium package dependency enters emitted target C#.

Catch filters, broad catches, catch-variable reads, finally, arbitrary
catch side effects, explicit \`throw new\`, multi-catch, Click and
unproven/dynamic selectors or wait predicates remain blocked, with
zero active partial output. The public same-Chrome Linux/Windows CI
executes real Selenium WebDriver/ChromeDriver versus Playwright .NET:
positive recoveries, negative rethrow types, unrelated assertion failures,
NUnit TRX, DOM/browser events, failure PNGs and cleanup. Source/method
SHA256 and binary fingerprints are retained in public synthetic evidence.
Private ArBilling is not run; general exception identity equivalence is
not proven or claimed.


## preview.39 — source-bound external exception catch chains

Preview.38 established limited inner-page \`NoSuchElementException\`
and \`WebDriverTimeoutException\` catch/rethrow conversions, but an
external NUnit test or TestBase catch may still expect the original
Selenium exception CLR type. The target throws different CLR types
(PlaywrightException or System.TimeoutException). Returning a locally
plausible candidate when an outer catch depends on this distinction
would silently alter test behavior.

The source-only Roslyn POM projector now follows **exact symbol-linked
caller edges** through the captured *.cs tree, up to four enclosing
methods. External matching Selenium NoSuch/Timeout catches, broad
exception catches and catch filters **block the entire projected
method**. The resulting blocker includes the caller's exact
relative source path, line and SHA-256 of the catch syntax, without
returning its source body. Direct calls, same-class helpers, inherited
TestBase and NUnit caller cases are covered by synthetic AST
regressions. Same-named unrelated methods or irrelevant catch types
must not yield false positives. Local-function invocations, virtual
dispatch, excessive graph depth and oversized call graphs also
fail closed instead of guessing.

This is a **bounded in-scope graph, not a full MSBuild/runtime call
graph**. No findings is not proof about external assemblies, unknown
dependencies, dynamic interface dispatch, delegates, reflection or
customer runtime behavior. All remaining source-backed drafts stay
\`requires_review\`, \`RuntimeVerified=false\`,
\`CanAutoApply=false\`. When a relevant external catch is present,
status is \`blocked\`, target statements are empty and no adapter
candidate is returned. No guessed Selenium exception adapter or
hidden Selenium runtime dependency is introduced.

CI keeps prior real Selenium/Playwright Chromium tests on Linux and
Windows, adds exact-source caller-chain regressions and records
exception risk in read-only MCP. Private ArBilling: not_run.


## P2 preview.40-A — source-backed external exception catch contract

Preview.39 blocks the ordinary Page Object projection when a TestBase
or NUnit caller catches Selenium's CLR exception type. Preview.40 adds
a separate opt-in source-only proposal without changing that blocker.

The only source operation supported is an immediate typed FindElement.Text
NUnit assertion or a finite WebDriverWait, locally caught and rethrown
with an exact Selenium NoSuchElementException/WebDriverTimeoutException
catch. Each external TestBase forwarding method must be a synchronous
void method with one exact symbol-bound call. There must be precisely
one external caller with one try containing that call, one exact
source catch, and no finally, filters, external rethrows or side effects.
Only an empty external catch can be proposed. An outer NUnit Count
recovery is explicitly blocked: its projected CountAsync needs an awaited
async method and a verified IPage receiver that this clause-only planner
does not source-prove or rewrite. No partial, non-compilable catch candidate
is exposed for nonempty source handlers.

The proposal is only a C# catch clause, not an invented converted test:
a narrow PlaywrightException catch constrained by the emitted
POM_SOURCE_ELEMENT_ABSENT marker or a System.TimeoutException catch
for the source-proven finite wait. It does not retain original
Selenium CLR exception identity, support other Playwright exceptions,
or add a hidden Selenium target package.

The trusted read-only API is PlanPomExternalExceptionContract;
the CLI is pom-exception-contract DIR FULL_TYPE METHOD TREE_SHA
with optional --emit-target for local trusted review. Default
responses redact private target clauses. SHA-256 source tree, method,
catch syntax and target clause evidence are retained. No plan is
runtime-verified or auto-approved. The old PlanPomActions remains
blocked on external Selenium catches even when this separate plan
has a review-required catch clause.

Linux and Windows CI runs actual source Selenium WebDriver and
Playwright .NET on the same Chrome for Testing binary against a
localhost synthetic application. Two guarded external recovery
cases must pass on both, while two deliberately unadapted controls
must preserve their failure on Playwright. Real NUnit TRX, actual
failure PNGs, browser snapshots and immutable source SHA are kept.
Other unknown CSS/exception/future behavior requires manual review.
Private ArBilling is not run.


## preview.41-A — opt-in source-backed NUnit/TestBase async Count recovery

`PlanPomExternalAsyncCountContract(sourceDir, fullyQualifiedPageType,
sourceMethod, exactTreeSHA, explicitPageReceiver)` is a **separate**
read-only source-only proposal. The preview.40
`PlanPomExternalExceptionContract` remains fail-closed on *all* nonempty
outer catches.

A narrow `requires_review` plan requires one exact Roslyn-resolved
Page Object void method with locally typed catch/rethrow of a projectable
Text assertion or finite wait; one no-side-effect TestBase forwarding
method; one [NUnit Test] outer method with matching, unfiltered external
catch and a single typed immediate FindElements.Count == literal recovery.
All three signatures must be convertible to async Task with one awaited
call edge. The caller must explicitly name a readonly **real**
Microsoft.Playwright.IPage field assigned from exactly one typed
constructor parameter; Roslyn verifies its external assembly identity,
not a same-named customer interface. The source Selenium IWebDriver
receiver for Count must be a field of the same NUnit type.

The plan contains precisely three target method-signature/body **snippets**
and one source-bound catch clause, never an automatically patched project.
CLI `pom-async-count-contract DIR TYPE METHOD TREE_SHA PAGE_RECEIVER`
redacts target C# by default; `--emit-target` requires trusted local
inspection. An absent, unbound or guessed receiver, async lifecycle
ambiguity, unsupported filter/rethrow, multiple invocation paths or
stale hash returns `blocked` with no partial candidates.

Actual browser parity and target assembly compiles are separate evidence;
constructor injection, exact target Page Object field wiring, missing
exception CLR identity and arbitrary side effects are not inferred.
No Selenium package is generated in the Playwright-only target.
Private ArBilling remains not_run.


## P1 preview.42-A — source-attested fixture page/context lifecycle

The opt-in, read-only plan \`PlanPomExternalAsyncLifecycleCountContract\`
and CLI \`pom-lifecycle-count-contract DIR TYPE METHOD TREE_SHA PAGE CONTEXT BROWSER PLAYWRIGHT\`
require **all four explicit field identifiers**; none are inferred.

The narrow source contract demands one sealed ordinary NUnit fixture,
one \`[OneTimeSetUp]\` creating Playwright and Chromium, one
\`[SetUp]\` creating \`IBrowserContext\` from exactly that \`IBrowser\`,
then \`IPage\` from the same context, then constructing the source
Page Object using exactly those fields and the source IWebDriver.
The Page Object constructor must assign one real assembly-typed IPage
and IBrowserContext into readonly fields, and its source-proven
TestBase forwarder must read the same source Page Object slot.
\`[TearDown]\` must await \`IPage.CloseAsync\` and unconditionally
await \`IBrowserContext.CloseAsync\` in \`finally\`.
\`[OneTimeTearDown]\` must await \`IBrowser.CloseAsync\` and
unconditionally dispose IPlaywright in \`finally\`.
Roslyn checks field identity and rejects extra writers, wrong
receiver, missing lifecycle hook and extra effects.

This accepts **only the captured synchronous source action/Count
exception chain** and source-authored ownership shape. It does not
invent a DI container, NUnit fixture constructor arguments, browser
launch configuration or a new cleanup action. Three async Task
method proposals remain review-only. The CLI redacts target C#
unless explicitly invoked with \`--emit-target\`; no auto-apply.
The old preview.40 and preview.41 APIs remain unchanged.
Real runtime success/failure, browser close and setup-failure
resilience require independent evidence; partial setup failures
and external assemblies remain unverified. Private ArBilling not_run.


## P1 preview.43-A — opt-in failure-atomic NUnit setup contract

\`PlanPomExternalFailureAtomicCountContract\` and local
\`pom-failure-atomic-count-contract DIR TYPE METHOD TREE_SHA PAGE CONTEXT BROWSER PLAYWRIGHT\`
add a **separate** strict Roslyn proof beyond preview.42. In each
\`[OneTimeSetUp]\` and \`[SetUp]\`, exactly the previously proved
Playwright.CreateAsync -> Browser.LaunchAsync and
Browser.NewContextAsync -> Context.NewPageAsync -> Page Object constructor
source assignments must appear inside one \`try\`. The sole \`catch\`
must attempt cleanup of each possibly acquired resource in reverse order,
with an explicit field-null guard, an independently protected attempt
that cannot prevent the next cleanup, and a final bare \`throw;\`.
This establishes primary-exception precedence for the admitted
**source syntax**: secondary cleanup failures are intentionally
discarded rather than replacing the original exception.

Existing NUnit \`TearDown\`/\`OneTimeTearDown\` and the exact Page/
Context constructor flow remain required. Any extra side effects,
filters, cleanup handlers that rethrow secondary errors, missing guards,
wrong order, wrong receiver, extra allocations, unsupported lifecycle,
or stale SHA are \`blocked\` with zero partial target snippets.
Read-only, \`CanAutoApply=false\`, \`RuntimeVerified=false\`.
The preview.40/41/42 APIs remain unchanged. Local CLI output redacts
target code unless \`--emit-target\` is explicitly requested; MCP
must never expose source bodies or generated C#.

The code cannot dispose resources an external API never returned,
prove arbitrary source projects, or guarantee NUnit calls teardown
after a failed OneTimeSetUp. It therefore requires explicit
source-level rollback, independent real browser experiments, and
manual review for production admission. Private ArBilling not_run.


## P1 preview.44-A — source-proven safe cleanup diagnostics

The opt-in read-only Core
\`PlanPomSafeCleanupDiagnosticsContract(DIR, TYPE, METHOD, SHA,
PAGE_FIELD, CONTEXT_FIELD, BROWSER_FIELD, PLAYWRIGHT_FIELD)\`
requires the complete previously source-attested failure-atomic
NUnit OneTimeSetUp/SetUp, same page/context ownership and exception
recovery; no cleanup code or diagnostic sink is invented.

Every second-order cleanup \`catch\` must itself contain exactly
\`try { System.Console.Error.WriteLine("MIGRATOR_CLEANUP_*_FAILED"); }
catch {}\`, with a **fixed** per-resource literal drawn from:
\`BROWSER_CLOSE\`, \`PLAYWRIGHT_DISPOSE\`, \`PAGE_CLOSE\`,
\`CONTEXT_CLOSE\`. Exact BCL Roslyn symbols are required:
\`System.Console.Error\` and \`System.IO.TextWriter.WriteLine(string)\`.
Exception messages, stack traces, types, user data, locators, dynamic
values, incorrect resource codes or a secondary sink failure escaping
the guard are **blocked** with zero partial candidates. Terminal
\`throw;\` keeps the primary error and stack. Logging may be captured
by the NUnit runner; its underlying sink could still fail, but this
source shape prevents that failure from replacing the primary.

This is a strictly **review-only** source contract, never target
auto-patching. CLI \`pom-safe-cleanup-diagnostics DIR TYPE METHOD
TREE_SHA PAGE CONTEXT BROWSER PLAYWRIGHT\` redacts target C# by
default; trusted local \`--emit-target\` is explicit. All old
preview.40–43 API contracts are unchanged. No private source or
exception messages cross MCP; no inference of OS process ownership.
A separate browser/OS probe distinguishes actual Playwright.CreateAsync
driver startup errors from synthetic fault injection. Absence of
surviving observed Chrome PIDs is NOT a complete process-tree
audit or proof about resources an API failed to return. ArBilling
not_run.
