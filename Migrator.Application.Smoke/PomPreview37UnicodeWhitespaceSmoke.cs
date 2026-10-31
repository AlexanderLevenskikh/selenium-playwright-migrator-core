using Migrator.Application;

internal static class PomPreview37UnicodeWhitespaceSmoke
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "fictional-p37-unicode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "TextPage.cs"), """
                using OpenQA.Selenium;
                using NUnit.Framework;
                using System;
                namespace Fictional.P37;
                public sealed class TextPage {
                    private readonly IWebDriver driver;
                    public TextPage(IWebDriver driver) { this.driver = driver; }
                    public void PreSimple() {
                        Assert.That(driver.FindElement(By.Id("space-pre")).Text,
                            Is.EqualTo("alpha  beta"));
                    }
                    public void PreWrapSimple() {
                        Assert.That(driver.FindElement(By.Id("space-pre-wrap")).Text,
                            Is.EqualTo("alpha  beta"));
                    }
                    public void UnicodeNbsp() {
                        Assert.That(driver.FindElement(By.Id("unicode-nbsp")).Text,
                            Is.EqualTo("Привет мир 🙂"));
                    }
                    public void UnicodeZeroWidth() {
                        Assert.That(driver.FindElement(By.Id("unicode-zero-width")).Text,
                            Is.EqualTo("alpha beta"));
                    }
                    public void UnicodeCombining() {
                        Assert.That(driver.FindElement(By.Id("unicode-combining")).Text,
                            Is.EqualTo("Café"));
                    }
                    public void BreakSpacesReview() {
                        Assert.That(driver.FindElement(By.Id("space-break")).Text,
                            Is.EqualTo("alpha  beta"));
                    }
                    public void MultiLinePreReview() {
                        Assert.That(driver.FindElement(By.Id("space-pre-multiline")).Text,
                            Is.EqualTo("alpha\nbeta"));
                    }
                    public void CssTransformReview() {
                        Assert.That(driver.FindElement(By.Id("uppercase")).Text,
                            Is.EqualTo("UPPERCASE"));
                    }
                    public void NarrowNoBreakReview() {
                        Assert.That(driver.FindElement(By.Id("narrow-nbsp")).Text,
                            Is.EqualTo("A B"));
                    }
                    public void MissingElement() {
                        Assert.That(driver.FindElement(By.Id("missing")).Text,
                            Is.EqualTo("ready"));
                    }
                    public void UnsupportedExpected() {
                        Assert.That(driver.FindElement(By.Id("space-pre")).Text,
                            Is.EqualTo(Environment.GetEnvironmentVariable("SECRET")!));
                    }
                    public void UnsupportedSideEffect() {
                        Console.WriteLine("unexpected effect");
                        Assert.That(driver.FindElement(By.Id("space-pre")).Text,
                            Is.EqualTo("alpha  beta"));
                    }
                }
                """);
            var hash = new PomSourceInspector().Inspect(root).SourceTreeHash;
            var engine = new MigrationEngine();
            PomActionPlan Plan(string method) =>
                engine.PlanPomActions(root, "Fictional.P37.TextPage", method, hash);

            foreach (var method in new[] {
                "PreSimple", "PreWrapSimple", "UnicodeNbsp",
                "UnicodeZeroWidth", "UnicodeCombining", "BreakSpacesReview",
                "MultiLinePreReview", "CssTransformReview",
                "NarrowNoBreakReview", "MissingElement"
            })
            {
                var plan = Plan(method);
                var target = plan.TargetStatements.SingleOrDefault() ?? "";
                Check(plan.Status == "requires_review"
                    && !plan.RuntimeVerified && !plan.CanAutoApply
                    && plan.Blockers.Count == 0
                    && plan.Actions.Count == 1
                    && plan.Actions.Single().Effect == "text_assertion"
                    && plan.Actions.Single().SourceSha256.Length == 64
                    && plan.Actions.Single().TargetSha256.Length == 64
                    && plan.AdapterCandidate is { RequiresReview: true }
                    && target.Contains(".EvaluateAllAsync<string>(",
                        StringComparison.Ordinal)
                    && target.Contains("POM_TEXT_PRESERVED_WHITESPACE_REVIEW",
                        StringComparison.Ordinal)
                    && target.Contains("POM_TEXT_PRESERVED_MULTILINE_REVIEW",
                        StringComparison.Ordinal)
                    && target.Contains("POM_TEXT_TRANSFORM_REVIEW",
                        StringComparison.Ordinal)
                    && target.Contains("POM_TEXT_UNVERIFIED_UNICODE_WHITESPACE_REVIEW",
                        StringComparison.Ordinal)
                    && target.Contains("POM_SOURCE_ELEMENT_ABSENT",
                        StringComparison.Ordinal)
                    && target.Contains("200b", StringComparison.OrdinalIgnoreCase)
                    && target.Contains("NUnit.Framework.Is.EqualTo(",
                        StringComparison.Ordinal)
                    && !target.Contains("ToHaveTextAsync", StringComparison.Ordinal),
                    method + " did not preserve source-proven immediate text and conservative CSS/Unicode guards.");
            }
            foreach (var method in new[] { "UnsupportedExpected", "UnsupportedSideEffect" })
            {
                var plan = Plan(method);
                Check(plan.Status == "blocked" && plan.Blockers.Count > 0
                    && plan.TargetStatements.Count == 0 && plan.AdapterCandidate == null,
                    method + " became an executable partial mapping.");
            }
            Check(new PomSourceInspector().Inspect(root).SourceTreeHash == hash,
                "Planning unexpectedly changed source files.");
            File.AppendAllText(Path.Combine(root, "TextPage.cs"), "\n//stale");
            try { Plan("PreSimple"); throw new Exception("Accepted stale source hash"); }
            catch (InvalidOperationException e) when (
                e.Message.StartsWith("STALE_POM_ACTION_PLAN", StringComparison.Ordinal)) { }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("POM_PREVIEW37_UNICODE_AST: " + message);
    }
}
