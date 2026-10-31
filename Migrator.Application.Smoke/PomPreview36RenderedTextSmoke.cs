using Migrator.Application;

internal static class PomPreview36RenderedTextSmoke
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "fictional-p36-rendered-text-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "TextPage.cs"), """
                using OpenQA.Selenium;
                using NUnit.Framework;
                using System;
                namespace Fictional.P36;
                public sealed class TextPage {
                    private readonly IWebDriver driver;
                    public TextPage(IWebDriver driver) { this.driver = driver; }
                    public void HiddenRoot() {
                        Assert.That(driver.FindElement(By.Id("hidden-root")).Text,
                            Is.EqualTo(""));
                    }
                    public void HiddenAncestor() {
                        Assert.That(driver.FindElement(By.Id("hidden-child")).Text,
                            Is.EqualTo(""));
                    }
                    public void NormalWhitespace() {
                        Assert.That(driver.FindElement(By.Id("spaced")).Text,
                            Is.EqualTo("alpha beta gamma"));
                    }
                    public void IndexedWhitespace(string expected) {
                        Assert.That(driver.FindElements(By.CssSelector(".space"))[0].Text,
                            Is.EqualTo(expected));
                    }
                    public void MissingText() {
                        Assert.That(driver.FindElement(By.Id("absent")).Text,
                            Is.EqualTo("ready"));
                    }
                    public void PreservedWhitespace() {
                        Assert.That(driver.FindElement(By.Id("pre")).Text,
                            Is.EqualTo("alpha  beta"));
                    }
                    public void DynamicExpected() {
                        Assert.That(driver.FindElement(By.Id("spaced")).Text,
                            Is.EqualTo(Environment.GetEnvironmentVariable("SECRET")!));
                    }
                    public void UnsafeTextSource() {
                        Console.WriteLine("side effect");
                        Assert.That(driver.FindElement(By.Id("spaced")).Text,
                            Is.EqualTo("value"));
                    }
                }
                """);
            var hash = new PomSourceInspector().Inspect(root).SourceTreeHash;
            var engine = new MigrationEngine();
            PomActionPlan Plan(string method) =>
                engine.PlanPomActions(root, "Fictional.P36.TextPage", method, hash);

            foreach (var method in new[] { "HiddenRoot", "HiddenAncestor",
                "NormalWhitespace", "IndexedWhitespace", "MissingText", "PreservedWhitespace" })
            {
                var plan = Plan(method);
                Check(plan.Status == "requires_review" && !plan.RuntimeVerified
                    && !plan.CanAutoApply && plan.Blockers.Count == 0
                    && plan.AdapterCandidate is { RequiresReview: true }
                    && plan.Actions.Count == 1
                    && plan.Actions[0].Effect == "text_assertion"
                    && plan.Actions[0].SourceSha256.Length == 64
                    && plan.Actions[0].TargetSha256.Length == 64
                    && plan.TargetStatements.Count == 1
                    && plan.TargetStatements[0].Contains(".EvaluateAllAsync<string>(",
                        StringComparison.Ordinal)
                    && plan.TargetStatements[0].Contains("getComputedStyle",
                        StringComparison.Ordinal)
                    && plan.TargetStatements[0].Contains("POM_TEXT_PRESERVED_WHITESPACE_REVIEW",
                        StringComparison.Ordinal)
                    && plan.TargetStatements[0].Contains("POM_SOURCE_ELEMENT_ABSENT",
                        StringComparison.Ordinal)
                    && plan.TargetStatements[0].Contains("NUnit.Framework.Is.EqualTo(",
                        StringComparison.Ordinal)
                    && !plan.TargetStatements[0].Contains("ToHaveTextAsync",
                        StringComparison.Ordinal),
                    method + " lost one-shot rendered-text evidence or review-only guard.");
            }
            Check(Plan("IndexedWhitespace").TargetStatements.Single()
                .Contains(".Nth(0).EvaluateAllAsync<string>", StringComparison.Ordinal)
                && Plan("IndexedWhitespace").AdapterCandidate!
                    .TargetStatementTemplates.Single().Contains("{expected}",
                        StringComparison.Ordinal),
                "Indexed source element and parameter were not preserved.");

            foreach (var method in new[] { "DynamicExpected", "UnsafeTextSource" })
            {
                var blocked = Plan(method);
                Check(blocked.Status == "blocked"
                    && blocked.Blockers.Count > 0
                    && blocked.TargetStatements.Count == 0
                    && blocked.AdapterCandidate == null,
                    method + " must fail closed with no partial executable target.");
            }
            var stable = new PomSourceInspector().Inspect(root).SourceTreeHash;
            Check(hash == stable, "Read-only planning modified source.");
            File.AppendAllText(Path.Combine(root, "TextPage.cs"), "\n// stale");
            try { Plan("HiddenRoot"); throw new Exception("Stale source accepted."); }
            catch (InvalidOperationException error) when (
                error.Message.StartsWith("STALE_POM_ACTION_PLAN", StringComparison.Ordinal)) { }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Check(bool ok, string explanation)
    {
        if (!ok)
            throw new InvalidOperationException("POM_PREVIEW36_RENDERED_TEXT_AST: " + explanation);
    }
}
