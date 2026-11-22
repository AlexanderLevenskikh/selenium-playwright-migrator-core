# P2/P3 — actual Core-projected public Selenium POM, bounded browser runtime

**Research and product-coverage draft, never Release Candidate approval.** This change is stacked on the previously green [Core #82](https://github.com/levencode-labs/selenium-playwright-migrator-core/pull/82), and preserves **all original 28 Core jobs**, original Windows P47, Node SIGTERM, 12-second native process gate, first-FAIL handling, exact PID/native FILETIME and historical native-LIVE failed ZIPs. [Windows P1 #59](https://github.com/levencode-labs/selenium-playwright-migrator-core/issues/59) is not fixed.

## Direct source-backed POM gaps closed in bounded scope

The original immutable `desh2001/Selenium-NUnit-Automation` commit `edacb9feb41a7e21d9b6bf6924f24c11698a4427` defines `HomePage.GoToTextInput()` and `TextInputPage.EnterText(string)`, `ClickButton()`, `GetButtonText()`.

This stage changes the **real Roslyn projector**, not a handwritten target:

- `IWebElement` read-only expression-bodied properties are followed to a **single** symbol-bound `IWebDriver.FindElement(By.<literal>)` getter. Property and getter must be in the same declared class and source compilation with exactly one owned non-static `IWebDriver` field. Writable/virtual/overridden/inherited/dynamic/multiple-driver/alias getters **block** instead of guessing a selector.
- Exact `By.LinkText("Text Input")` becomes a **review-only** Playwright link-role candidate with exact accessible name. Partial/dynamic link text and arbitrary DOM matching remain unresolved; visible-text vs accessible-name differences still require external browser proof.
- A source `string` method with precisely one typed `IWebElement.Text` result can project `return await ...InnerTextAsync();`. It is never promoted from a syntactic map into full behavioral equivalence: CSS whitespace, visibility, empty/missing element, exception identity and other return shapes are unproven.
- Existing `Clear`, `SendKeys`, `Click` and method parameter ordering are preserved. The emitted source/target method and ordered action SHA256 lineage, tree hash and explicit `requires_review`/no-autoapply contract are required.

Synthetic Core smoke uses an explicit Roslyn source with positives plus negatives: mutable and computed properties, helper aliases, string-returning side effects, literal body without source read, `PartialLinkText`, two plausible `IWebDriver` fields and a stale source-tree hash. None may silently produce active target statements.

## Real public source → actual Core-projected target browser oracle

A new **isolated, self-authored** .NET 10 NUnit harness copies exactly the two unchanged public `HomePage.cs` and `TextInputPage.cs` files from the already sparse-checkout-pinned source. The third pinned original `TextInputTests.cs` is used for source workflow lineage, but its original TestFixture / DriverManager / SetUp/TearDown are not executed.

1. Independently verify the exact public commit/Git blob bytes; reject checkout mutations except LF↔CRLF. Inspect the captured two-file POM source tree with the **actual Core assembly CLI**.
2. Project each real method using `pom-plan --emit-target` and the exact `sourceTreeHash`. Require `requires_review`, all source effects in order, exact method/step SHA256, no blockers, no runtime auto-acceptance.
3. Generate C# methods from **those unmodified emitted target statements**, compile them with real Playwright .NET and exercise them against a deterministic loopback-only HTML fixture on the same Chrome executable as original Selenium.
4. Independently compare actual NUnit TRX, resulting DOM input/button state, click count and application event order. Missing Click must remain NUnit PASS but **fail independent DOM oracle**; wrong-selector mutation must be an actual NUnit FAIL. Changing the public property selector invalidates the original sourceTreeHash and blocks any stale `pom-plan`.
5. Retain per-platform evidence ZIP including real TRX/JSON, Git blob/captured source SHA256, emitted generated target SHA256, each plan's method/effect hashes and all negative statuses. No upstream project source files, customer code, external website request or third-party build scripts are uploaded/executed.

This is the **first actual Core-produced browser-tested public POM method slice**, not a full project migration or an accepted `migrator-acceptance/v1` candidate. The emitter is a review-only POM planner and does not create a P2 `CandidateId`. The original NUnit lifecycle, transitive helper graph, exception/failure paths outside the selected methods, inherited `TestClass` and public `SetUpFixture` remain `not_run` or `UNKNOWN`. These are independent exit conditions. `canAutoApply=false`, `acceptedForRC=false`, `fullRealProjectBehavioralEquivalence=unknown`.

## Gating and security

The new static controls and real runtime test are inserted only in the **two existing** Linux/Windows P2 AST jobs; no job is removed, shortened or retried for cosmetic green. All original real Chromium/NUnit cases, Windows orphan P47 and package provenance assertions remain. The test environment is GitHub-hosted isolated runner with read-only repository permissions, a pinned public Git checkout, controlled local NUnit SDK project, same Chrome/ChromeDriver and loopback-only HTML. A failed real source, generated target, negative oracle or package/CI step must remain FAIL.

A successful bounded browser result must be accompanied by the exact final 28/28 Core acceptance and exact-Core pinned 2/2 MCP. It cannot close #59 or authorize merge/release. Full multi-public-project independent behavioral acceptance remains **UNKNOWN** until original fixture lifecycle and full target generation/identity are independently proven.
