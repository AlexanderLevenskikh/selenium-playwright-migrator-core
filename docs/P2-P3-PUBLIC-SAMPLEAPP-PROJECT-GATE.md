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
