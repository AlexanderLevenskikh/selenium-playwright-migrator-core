# P2 — strict migration acceptance (issue #2)

This is a **fail-closed** gate for source → captured target candidates. A green
NUnit runner is not evidence of migrated Selenium behavior.

The existing \`migrator-coverage/v1\` describes the adapted IR and renderer
coverage. It does **not** attest source/runtime equivalence. The existing
\`VerifyArtifact\` binds source analysis and a content-addressed target candidate
and optionally runs **Roslyn assembly compilation** (not MSBuild).

\`ArtifactVerification.Acceptance\` now adds \`migrator-acceptance/v1\`:
- Every NUnit \`[Test]\`, \`[TestCase]\`, \`[TestCaseSource]\` and
  \`[SetUp]\`/\`[TearDown]\`/\`[OneTimeSetUp]\`/\`[OneTimeTearDown]\`
  declaration is inventoried with source SHA256, method and line.
- Within those methods, invocations, assignments and increments/decrements
  are inventoried. Source calls are classified as action, locator, assertion,
  lifecycle effect or opaque helper. Only one target AST invocation may be
  assigned to any given source invocation; unmatched obligations remain visible.
- Each potential source→target match is \`candidate_unverified\`, **never**
  proof of equivalent selectors, operands, side effects or POM call graphs.
  Missing methods or operations are \`missing_target\`.
- \`rejected_incomplete\` if anything is missing, source discovery or preview
  is partial, a TODO survives, or mutable state effects lack verified parity.
  Otherwise \`blocked_unverified\`: no automatic success at this stage.
- Syntax and Roslyn compilation are separate from NUnit test discovery,
  actual runner, real browser and behavioral equivalence. The last four
  are explicitly \`not_run\` because VerifyArtifact never executes user code.
- Identity binds \`AnalysisId\`, \`CandidateId\`, source tree hash and exact
  target tree hash, including any agent edits. A different target must be
  reviewed again, never inherit its parent's evidence.

**Limitations:** This is not a semantic interpreter, a complete cross-project
POM call graph, a verified operation equivalence checker, or real NUnit browser
parity. Same-name AST matches are review-only. A future explicitly authorized
sandboxed runner and independent source/target observation oracle are required
before any \`accepted\` result can be introduced.

## Counterexample

\`scripts/p2_false_green_real_nunit.py\` runs two public synthetic NUnit 3
projects in CI. Both produce a real 1/1 **Passed** TRX, but the target has
deleted \`new Button().Click()\` from the source. The smoke
\`MigrationAcceptanceSmoke\` proves that this is \`rejected_incomplete\`, not
accepted; it also checks lost assertion, lost SetUp effect, renamed test, and
different-selector candidate paths. The evidence artifact contains only
invented public NUnit TRX and a hashed summary, never private ArBilling data.

No change to original P47, Chromium, Windows Job experiments, Core or MCP
release gates is made by this PR. This is a Core-only API change; MCP/Skill
must deliberately consume the new acceptance field in a separately pinned PR.
