# P2/P3: actual public SampleApp POM and original NUnit project preflight

**Research/product implementation in a new draft stacked on green Core #83.
Not full-project acceptance and not Release Candidate approval.** All original
28 Core jobs and Windows P47 remain unchanged. P1 #59 stays OPEN with
NO FIX CLAIM.

## Source-backed implementation

The exact immutable desh2001/Selenium-NUnit-Automation commit
edacb9feb41a7e21d9b6bf6924f24c11698a4427 contains:

- HomePage.GoToSampleApp(): By.LinkText("Sample App") + Click.
- SampleAppPage.Login(username,password): By.Name("UserName"),
  By.Name("Password"), By.Id("login"); ordered SendKeys, SendKeys, Click.
- SampleAppPage.GetMessage(): By.Id("loginstatus").Text.
- SampleAppTests with two NUnit test cases, real DriverManager,
  SetUp navigation and TearDown cleanup.

The source-only Roslyn Selenium By anchor previously omitted the existing
By.Name method, despite the Core projector already mapping a bound literal
Name selector to a CSS attribute locator. That missing identity prevented
trusted binding of these real public method calls. Add the compile-only
identity, not a relaxed heuristic. Dynamic Name values, local aliases,
wrong action order, stale source hash, mutable/ambiguous properties or
unresolved calls still BLOCK; generated statements remain requires_review.

## Reproducible evidence being requested from CI

Inside the original two Linux/Windows p2-pom-ast-fast jobs:

1. Resolve exact Git blob and worktree hashes of the three original public
   HomePage.cs, SampleAppPage.cs and SampleAppTests.cs files.
2. Invoke the **actual Core pom-plan --emit-target** for
   GoToSampleApp/Login/GetMessage and compile its emitted C#.
3. Independently run the unchanged original Selenium POM source and the
   actual generated Playwright methods in the same real Chrome against
   localhost. Assert identical DOM, username/password argument identity,
   success and failure messages, action order and browser events.
4. Collect seven separate NUnit TRX. Two mutations deliberately retain
   NUnit PASS after deleting the Click or swapping username/password;
   independent DOM oracle must reject both. A wrong Name selector must
   produce real NUnit FAIL. A changed original Name selector must reject
   the old sourceTreeHash. Preserve the target C# SHA256 and action
   lineage in separate Linux/Windows artifact ZIPs.
5. For the **full original** 10-file NUnit project, verify exact .csproj,
   seven package versions and source Git blobs; copy to an ephemeral
   workspace and attempt bounded .NET 10 restore, build and test discovery.
   Do not run original tests against the third-party external website.
   Record prerequisite failure without pretending that a target exists.

These checks extend proof from TextInput to another real page and
establish original-project build prerequisites, but they do NOT
transform the full upstream test assembly or run its original
DriverManager and TestFixture lifecycle.

## Remaining acceptance blockers

The original full NUnit tests, Alert/Confirm/Prompt and delay scenarios,
NUnit source/target TRX parity, inherited/scoped lifecycle, source
Assertion/lifecycle graph closure, exact AnalysisId/CandidateId,
target solution build, all failure-path semantics and independent
behavioral-equivalence receipt remain **UNKNOWN/BLOCKED**.

No autoapply or release authorization follows from a green method-slice CI.
MCP consumers must retain method-level, source-only review semantics.
General RC tracker: Core issue #80; independent P1 blocker: #59.

## Complete original POM method coverage ledger

After the exact Core CLI build, a new read-only 15-method sweep covers all
five pinned source classes: four HomePage navigations; three TextInputPage
methods; two SampleAppPage login/message methods; two ClientSideDelayPage
methods; and four AlertsPage methods, including source IAlert WaitForAlert.
Each method is analyzed using the **same complete five-file POM source tree**
and recorded as requires_review, blocked or UNKNOWN with method SHA,
source-to-target effect SHA, and blocker code. Unsupported IAlert return
must remain blocked/UNKNOWN, never silently produce target browser code.
The sweep separately runs the actual Core single-project assembly `plan`
on the complete ten-file original NUnit project and reports its AnalysisId,
CandidateId, declared test count, source/target hashes and plan issues.

All results remain read-only. Neither a successful method projection nor an
assembly `ready_for_build` status means a real full NUnit project migration,
and runtime behavioral equivalence remains UNKNOWN. The ledger has no
MSBuild execution permission and cannot autoapply a candidate.

## Original full-project CandidateId-bound static acceptance

The new local CLI command is deliberately review-only:

`dotnet Migrator.AssemblyCli.dll acceptance-inspect <sourceProjectRoot> <AnalysisId> <CandidateId>`

The two identity arguments MUST match those obtained from the exact original
project `plan`. The CLI invokes the Core's actual
`MigrationEngine.VerifyArtifact` and prints
`migrator-acceptance/v1`: complete source/target operation/lifecycle
lineage hashes, missing obligations, blockers and six independent stage
statuses. A stale CandidateId returns an error, and neither
`rejected_incomplete` nor `blocked_unverified` returns exit 0.
No target source is emitted, no project is built/run, no browser or fixture
executed, and no autoapply is available.

The existing 15-method full public POM ledger must be present and still
record `WaitForAlert` as blocked/UNKNOWN. The original project acceptance
stage is bound to all 10 source test declarations, the exact
AnalysisId/CandidateId and current source/target hashes, independently of
the `ready_for_build` structural assembly plan. On its own this does not
certify the original test fixture execution or a complete Playwright
runtime migration.

## Real original-project -> Core-generated target build attempt

A separate bounded opt-in CI stage now invokes the **actual**
`Migrator.AssemblyCli generate <original-project-root> <empty-temp-target> --build`,
using the original SDK project with 10 discovered tests at the pinned public
commit. The exact parent AnalysisId/CandidateId from the full assembly plan
and the separate `rejected_incomplete`/blocked static acceptance must
match. The generated project is never placed inside original source and
is never autoapplied. It is removed from the ephemeral runtime after
archiving generated C#, fixed .csproj and target manifest with SHA256.

A real `dotnet build` is attempted; if it succeeds, `dotnet test
--list-tests --no-build --no-restore` may enumerate **target** NUnit cases.
No source or generated NUnit test, external-site URL or Playwright browser
action is executed by this stage. Any build/discovery issue stays an exact
BLOCKED/UNKNOWN stage; a successful compile/test discovery cannot override
the static rejected_incomplete completeness report or independent missing
behavioral oracle.
