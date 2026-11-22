# P2-A — fail-closed migration candidate acceptance (Core issue #2)

This draft adds an **explicit acceptance result** to the existing Core alpha verify_artifact contract without pretending that Roslyn syntax, Roslyn emission, green NUnit tests or a successful Playwright browser launch imply migrated Selenium behavior. It is a safety gate, not a behavioral equivalence prover.

## Separate stages

Existing syntax, in-process Roslyn compile, runtime and observables verification remain unchanged. The new acceptance object has independent NUnit test discovery, NUnit runner, real browser and behavioral equivalence stages. All four are **not_run** in this read-only alpha. No project MSBuild, arbitrary script, test runner, browser launch, process termination or external network call is initiated.

Possible acceptance.status:
- **rejected_incomplete**: preview is partial/blocked, source model action/lifecycle obligation is missing or marked for review, or exact emitted target operation was removed in the current candidate edit.
- **blocked_unverified_behavior**: no known incomplete transformation or deleted emitted operations, but independent Selenium-to-Playwright behavioral evidence is unavailable. This is **NOT success**.
- **accepted**: **not emitted by this alpha implementation**. A future evidence-backed verifier may implement it only after explicit opt-in, full bound source/target lineage and independent acceptance gates.

The immutable source obligation ledger contains relative source path, test or lifecycle owner, source line, source action kind, stable identity and model projection state. This includes nested actions, source SetUp and source NUnit fixture/SetUpFixture lifecycle hooks. Its **projected_ir_only** state is explicitly *not* a proven target effect. Agent-edited candidates receive a byte-exact preview-vs-target Roslyn executable-leaf-statement inventory: losing the originally generated ClickAsync, assertion, assignment or other active statement is reported with TARGET_OPERATION_REMOVED and rejected even when remaining code compiles/tests green. Syntax normalization does not certify an alternative implementation; rewrites are blocked for further review, not automatically treated as equivalent.

All source snapshots, candidate tree hashes and edit parent identities remain enforced by existing Core. No private ArBilling data is introduced.

## Public negative regressions

Migrator.Application.Smoke/MigrationAcceptanceSmoke.cs demonstrates:
1. An unchanged complete-looking preview is still blocked for missing behavioral evidence.
2. Removing ClickAsync from a target that still asserts true is rejected.
3. Removing Assert.That is rejected.
4. A partial preview remains rejected regardless of target shape.
5. A source action lost between source and target IR is explicitly marked missing.
6. NUnit discovery, runner, browser and behavior statuses remain not_run.

ArtifactVerificationSmoke separately confirms successful in-process Roslyn compilation does not grant acceptance or inherit source behavior proof. Run normal existing Core smoke on Linux and Windows; do not narrow the 28-job CI matrix.

## Limitations (do not overclaim)

This **does not yet provide complete Roslyn semantic symbol/argument and side-effect equivalence**, independent action-to-DOM effect mapping, browser application observables, NUnit discovery/TRX, or positive acceptance of a fully migrated public Selenium suite. The source ledger identifies projected model units, not a guaranteed one-to-one executable target effect; rendered TODO guardrails remain necessary. The edit guard is intentionally conservative and can reject semantically valid rewrites. These limits must be addressed before closing Core #2 or claiming production readiness.

The publicly documented Selenium NUnit external case with **10 tests, 3/10 fully migrated and 96 TODOs, yet 10/10 NUnit PASS**, MUST be **rejected_incomplete** and not marked equivalent. A source/test runner PASS does not override partial transformation. This first step is suitable for a draft **NO MERGE / NO RELEASE** while P1 Windows issue #59 is unresolved.
