# P2 follow-up: structural control-flow lineage (draft)

This is a **stacked**, independent follow-up based on Core P2 #68 exact head
`a5393624497f9fad9bedc17a84605caa9e2df857`. It does not touch Core main, P1 P47, or the MCP/Skill
consumer's exact pin. Full 28-job Linux/Windows Core matrix is unchanged.

## Scope

The original `migrator-acceptance/v1` gate already records NUnit declaration,
invocation and mutation candidates. This slice also records **individual source
control-flow obligations** inside annotated NUnit test/lifecycle methods:
`if`, `else`, switch, ternary/conditional access, `try`, `catch`,
`finally`, `throw`, for/foreach/while/do, break/continue and explicit
return. Each has a hash-only source→target lineage entry.

A source branch/path with **no target node of the same syntactic category**
is `missing_target` and yields `rejected_incomplete` even when the target
still contains corresponding action/assertion names and its NUnit tests
could be green. A matching shape is strictly `candidate_unverified`;
different predicates, reordered effects, extra target paths, inherited
lifecycle, POM/helper transitive calls and independent browser observables
are **not established**. Matching shapes NEVER produce `accepted`.

## Regression controls

The portable Core smoke creates a fictional NUnit source with guarded
`Click`, an `else` assertion, and `try/catch/throw/finally`.
- **Negative:** flatten every branch while keeping all invocations.
  Expect `rejected_incomplete`, missing flow lineage and no inherited
  behavioral stage.
- **Non-proof:** preserve the same syntax categories. Expect
  `blocked_unverified`, flow candidates only and `not_run` behavior.
- **Changed guard:** mutate `if (true)` to `if (false)` with the same
  structure. Expect *no success* or behavioral claim.

This detects a narrow, observable omission. It is **not** a replacement for
an independent source/target behavioral oracle, explicit opt-in runtime,
semantic call graph, or conditional equivalence proof. There is no execution
of user source, additional permission, release or merge claim.

## Acceptance

This branch requires the original Core **28/28** CI jobs on the exact final
head. MCP #56 remains pinned to its separately verified Core #68 SHA;
it must not silently be cited as validation for this newer Core head.
