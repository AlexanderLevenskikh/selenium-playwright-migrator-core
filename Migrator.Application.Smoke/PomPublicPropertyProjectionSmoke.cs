using Migrator.Application;

internal static class PomPublicPropertyProjectionSmoke
{
    private const string Source = """
        using OpenQA.Selenium;
        namespace PublicLike;
        public sealed class TextInputPage
        {
            private readonly IWebDriver driver;
            public TextInputPage(IWebDriver driver) => this.driver = driver;
            public IWebElement TextBox => driver.FindElement(By.Id("newButtonName"));
            public IWebElement Button => driver.FindElement(By.Id("updatingButton"));

            public void EnterText(string text)
            {
                TextBox.Clear();
                TextBox.SendKeys(text);
            }

            public void ClickButton() => Button.Click();
            public string GetButtonText() => Button.Text;
            public string GetButtonTextBlock() { return Button.Text; }
            public void GoToTextInput() =>
                driver.FindElement(By.LinkText("Text Input")).Click();

            public IWebElement Mutable { get; set; } = default!;
            public void BadMutable() => Mutable.Click();
            public IWebElement Dynamic => driver.FindElement(
                By.Id(System.Environment.GetEnvironmentVariable("DYNAMIC_ID")!));
            public void BadDynamic() => Dynamic.Click();
            public IWebElement ViaHelper => Helper();
            private IWebElement Helper() => driver.FindElement(By.Id("updatingButton"));
            public void BadHelper() => ViaHelper.Click();
            public string BadReturn() => "invented";
            public string BadReturnSideEffects()
            {
                driver.FindElement(By.Id("updatingButton")).Click();
                return Button.Text;
            }
            public void BadPartialLink() =>
                driver.FindElement(By.PartialLinkText("Text")).Click();
        }
        """;

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "public-pom-property-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Combine(root, "TextInputPage.cs");
            File.WriteAllText(sourcePath, Source);
            var engine = new MigrationEngine();
            var hash = new PomSourceInspector().Inspect(root).SourceTreeHash;
            var expected = new[]
            {
                ("EnterText", 2, "input_sendkeys", "PressSequentiallyAsync(text)"),
                ("ClickButton", 1, "click", "ClickAsync()"),
                ("GetButtonText", 1, "rendered_text_return", "InnerTextAsync()"),
                ("GetButtonTextBlock", 1, "rendered_text_return", "return await"),
                ("GoToTextInput", 1, "click", "AriaRole.Link")
            };
            foreach (var (method, count, effect, expectedToken) in expected)
            {
                var result = engine.PlanPomActions(root, "PublicLike.TextInputPage",
                    method, hash);
                Check(result.Status == "requires_review" &&
                      result.Blockers.Count == 0 &&
                      result.Actions.Count == count &&
                      result.Actions.All(action =>
                          action.SourcePath == "TextInputPage.cs" &&
                          action.SourceLine > 0 &&
                          action.SourceSha256.Length == 64 &&
                          action.TargetSha256.Length == 64 &&
                          action.RequiresReview) &&
                      result.Actions.Any(action => action.Effect == effect) &&
                      result.TargetStatements.Any(statement =>
                          statement.Contains(expectedToken, StringComparison.Ordinal)) &&
                      result.AdapterCandidate is { RequiresReview: true } &&
                      result.SourceTreeHash == hash &&
                      !result.RuntimeVerified && !result.CanAutoApply,
                    "typed public POM projection incorrectly accepted, blocked or omitted effects: " +
                    method + " " + string.Join(", ", result.Blockers.Select(x => x.Code)));
            }

            // Compare the selector's AST literal VALUE, not the C# string
            // escape spelling (JSON serializer may use \\u0022 vs \\" ).
            // This is STRICTER than matching a rendered source substring.
            var input = engine.PlanPomActions(root, "TextInputPage", "EnterText", hash);
            Check(input.TargetStatements.Count == 2 &&
                  HasExactLocator(input.TargetStatements[0],
                      "[id=\"newButtonName\"]") &&
                  HasExactLocator(input.TargetStatements[1],
                      "[id=\"newButtonName\"]") &&
                  input.TargetStatements[0].Contains(".ClearAsync()", StringComparison.Ordinal) &&
                  input.TargetStatements[1].Contains(
                      ".PressSequentiallyAsync(text)", StringComparison.Ordinal) &&
                  input.AdapterCandidate!.TargetStatementTemplates[1]
                      .Contains("PressSequentiallyAsync({text})", StringComparison.Ordinal),
                  "property source selector/clear/sendkeys/argument was not preserved");

            var clickPlan = engine.PlanPomActions(root, "TextInputPage", "ClickButton", hash);
            var textPlan = engine.PlanPomActions(root, "TextInputPage", "GetButtonText", hash);
            Check(HasExactLocator(clickPlan.TargetStatements.Single(),
                      "[id=\"updatingButton\"]") &&
                  HasExactLocator(textPlan.TargetStatements.Single(),
                      "[id=\"updatingButton\"]") &&
                  textPlan.TargetStatements.Single().Contains(
                      "return await", StringComparison.Ordinal) &&
                  textPlan.TargetStatements.Single().Contains(
                      ".InnerTextAsync()", StringComparison.Ordinal),
                  "source property getter lost exact Button selector or rendered Text return");

            var nav = engine.PlanPomActions(root, "TextInputPage", "GoToTextInput", hash);
            Check(nav.TargetStatements.Single().Contains(
                      "Exact = true", StringComparison.Ordinal) &&
                  nav.TargetStatements.Single().Contains(
                      "Name = \"Text Input\"", StringComparison.Ordinal) &&
                  nav.TargetStatements.Single().Contains(
                      "Microsoft.Playwright.AriaRole.Link", StringComparison.Ordinal),
                  "By.LinkText was rendered as a generic/sub-string selector");

            foreach (var method in new[]
            {
                "BadMutable", "BadDynamic", "BadHelper", "BadReturn",
                "BadReturnSideEffects", "BadPartialLink"
            })
            {
                var blocked = engine.PlanPomActions(root, "TextInputPage", method, hash);
                Check(blocked.Status == "blocked" && blocked.Blockers.Count > 0 &&
                      blocked.TargetStatements.Count == 0 &&
                      blocked.AdapterCandidate == null && !blocked.CanAutoApply,
                    "unsupported property, dynamic selector, getter, or side effect was silently emitted: " +
                    method);
            }

            // The same project graph with two plausible IWebDriver fields
            // cannot infer which receiver the projected property belongs to.
            File.WriteAllText(sourcePath, Source.Replace(
                "private readonly IWebDriver driver;",
                "private readonly IWebDriver driver;\n            private IWebDriver secondDriver;"));
            var ambiguousHash = new PomSourceInspector().Inspect(root).SourceTreeHash;
            var ambiguous = engine.PlanPomActions(root, "TextInputPage",
                "EnterText", ambiguousHash);
            Check(ambiguous.Status == "blocked" &&
                  ambiguous.TargetStatements.Count == 0,
                  "ambiguous two-driver POM was guessed");
            try
            {
                engine.PlanPomActions(root, "TextInputPage", "EnterText", hash);
                throw new InvalidOperationException("Stale source hash accepted");
            }
            catch (InvalidOperationException ex) when (
                ex.Message.StartsWith("STALE_POM_ACTION_PLAN", StringComparison.Ordinal))
            {
                // Expected: changed source is not allowed under old hash.
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static bool HasExactLocator(string statement, string selector)
    {
        var syntax = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParseStatement(statement);
        return !syntax.ContainsDiagnostics &&
            syntax.DescendantNodesAndSelf()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>()
                .Any(call =>
                    call.Expression is Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax access &&
                    access.Name.Identifier.ValueText == "Locator" &&
                    call.ArgumentList.Arguments.Count == 1 &&
                    call.ArgumentList.Arguments[0].Expression is
                        Microsoft.CodeAnalysis.CSharp.Syntax.LiteralExpressionSyntax literal &&
                    literal.Token.ValueText == selector);
    }

    private static void Check(bool ok, string detail)
    {
        if (!ok)
            throw new InvalidOperationException(
                "PUBLIC_POM_PROPERTY_PROJECTION_SMOKE: " + detail);
    }
}
