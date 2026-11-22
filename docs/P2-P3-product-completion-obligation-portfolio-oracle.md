# P2 + P3 Product Completion: exact source inventory, fail-closed project graph and observable evidence

Stacked product draft on Core #75 exact `cf417ade4d0d836dd1866bf1d0e3a1a86720057b`. This is an additive bounded acceptance slice: it does **not** change original P47, Node SIGTERM, 12-second Windows gate, PID/FILETIME or the original 28-job Linux/Windows matrix. P1 orphan issue #59 remains OPEN. No merge/release.

## Independent public sources — source inspection, NOT migration success

Three real unrelated public sources are fetched with `actions/checkout@v4` at immutable SHA and sparse file paths, both Linux and Windows. The Python audit requires exact `git rev-parse HEAD`, **all five Git blob SHA-1 identities**, size bounds and emits SHA-256 byte receipts; Roslyn independently parses the actual source files and observes mandatory source facts. Nothing from these projects is executed, adapted or published.

| Project | exact commit | sample and obligation | outcome |
| --- | --- | --- | --- |
| desh2001/Selenium-NUnit-Automation | `edacb9feb41a7e21d9b6bf6924f24c11698a4427` | TextInputTests + TextInputPage; SetUp/Test/TearDown, POM EnterText → Clear/SendKeys, ClickButton → Click, selector and assertion | source facts verified; cross-type helper graph and runtime parity UNKNOWN |
| jorgedelacruz/selenio | `0ca128dfaf32c00f944b756fea9dbf9b6c8964a2` | NUnitTests + SeleniumHomePage; inherited TestClass, PageFactory FindsBy, proxy calls | dynamic dispatch/lifecycle UNKNOWN |
| re-motion/Framework | `27716889ad2d26f4fa4b442207460c570bd253db` | Web IntegrationTests SetUpFixture; OneTimeSetUp/TearDown, external setup/cleanup side effects | setup/cleanup order and runtime parity UNKNOWN |

The script never promotes these projects to PASS behavioral equivalence, and reports `canAutoApply=false`, `accepted=false`, external NUnit/browser `not_run`. No private ArBilling material was used.

## New bounded fail-closed P2 project graph

An additional `MigrationAcceptanceGate.ProjectGraph.cs` contributes hash-only source→target lineage for:
- `[SetUpFixture]` presence and scoped lifecycle hooks; removing the scope is `missing_target` / `rejected_incomplete`
- NUnit fixture inheritance edges; losing the base is `missing_target`, identical AST remains `candidate_unverified`
- same-class, nonvirtual private helper invocations reachable from NUnit tests and lifecycle methods (up to 4 levels/512 rows/128 source files), with action/assertion/locator and failure/mutation side effects

The transformation never emits `accepted`. Symbol/name candidates are **not semantic equivalence**; cross-class POM calls, inherited base implementation, dynamic dispatch, method overloads, selector/argument binding, exception identity and execution order remain unresolved. An unsupported graph depth/overload is fail-closed. No user project is compiled or executed by the acceptance gate. Exact AnalysisId, CandidateId, SourceTreeHash and TargetTreeHash remain mandatory.

Synthetic Core smoke checks a source SetUpFixture, inherited TestFixture, multi-hop private helpers and Click. A compilable target that retains the outer test but drops the setup fixture, inheritance or nested Click is `rejected_incomplete`. Preserving AST shape is `blocked_unverified`; selector and inheritance mutations never become success.

## Independent real Selenium → Playwright fixture with two negative controls

The existing same-Chrome/localhost NUnit runner remains an explicitly authorized **CI-only** runtime, outside the read-only MCP/verify_artifact path. Eight real source/target scenarios compare NUnit TRX, DOM and ordered user events on Linux and Windows. The original negative mutates `>= minimum` to `< minimum`, producing **NUnit FAIL** and accept→reject. A second negative suppresses mandatory Click while retaining a trivial NUnit assertion: its target NUnit **PASSES**, but source/target observable comparison must reject the behavior difference. This directly falsifies `dotnet test PASS ⇒ successful migration`.

The runner now stores the exact generated **synthetic target C# bytes**, their SHA-256, the matching source bytes and an independently derived `oracleCandidateSha256=SHA256("migrator-public-oracle/v1\n"+sourceTreeSha+"\n"+sourceFileSha+"\n"+targetFileSha)`. This is deliberately **NOT** a Core `VerifyArtifact` CandidateId or authorization to apply changes. A separate read-only verifier checks exact manifests, source/target hashes, the eight paired JSON observations and NUnit TRX, the expected failed mutation, the expected green-but-wrong NUnit result, PNG hash and the no-autoapply/no-general-parity boundary. Tampering any one of these fails closed; synthetic negative unit tests run on Linux and Windows.

## Strict completion gates and limits

This is useful evidence for a bounded public local scenario, NOT complete equivalence of arbitrary public or private projects. Future production acceptance still requires real selected customer-project execution (explicit permission), exact source/target revision and externally defined observable oracle, full helper/POM/inherited lifecycle resolution, independent source→target behavioral result, and the Windows P1 #59 product cleanup fix.

Existing original Core CI: 28 Linux/Windows jobs, full original P47, all prior browser and NUnit controls; no matrix reduction. MCP + Skill must re-pin the exact unmerged Core SHA, build self-contained Windows and Linux versions, and prove actual stdio fail-closed status in a separate draft. No merge/release until full acceptance and separate decision.
