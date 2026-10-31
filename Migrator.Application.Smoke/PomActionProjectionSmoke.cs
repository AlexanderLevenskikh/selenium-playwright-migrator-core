using Migrator.Application;
using Migrator.Core;
using Migrator.Roslyn;
using Migrator.SeleniumCSharp;
using Migrator.PlaywrightDotNet;

internal static class PomActionProjectionSmoke
{
    public static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(),
            "pom-action-preview30-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var source = Path.Combine(folder, "SyntheticCatalogPage.cs");
            File.WriteAllText(source, """
                using OpenQA.Selenium;
                using OpenQA.Selenium.Support.UI;
                using NUnit.Framework;

                namespace Fictional.Pom;

                public sealed class SyntheticCatalogPage
                {
                    private readonly IWebDriver driver;
                    public SyntheticCatalogPage(IWebDriver driver) => this.driver = driver;

                    public void Open(string url) =>
                        driver.Navigate().GoToUrl(url);

                    public void InputAndSelect(string value)
                    {
                        driver.FindElement(By.Id("picker")).Click();
                        driver.FindElement(By.Id("search")).Clear();
                        driver.FindElement(By.Id("search")).SendKeys(value);
                        driver.FindElement(By.CssSelector("[data-test='option']")).Click();
                        driver.FindElement(By.Id("accept")).Click();
                    }

                    public void ValidateLoading()
                    {
                        new WebDriverWait(driver, System.TimeSpan.FromMilliseconds(1200))
                            .Until(d => d.FindElement(By.Id("ready")).Displayed);
                    }

                    public void AssertTableCount()
                    {
                        Assert.That(driver.FindElements(By.CssSelector("[data-test='row']")).Count,
                            Is.EqualTo(2));
                    }

                    public void AssertTableText(string expected)
                    {
                        Assert.That(driver.FindElements(By.CssSelector("[data-test='row']"))[0].Text,
                            Is.EqualTo(expected));
                    }

                    public void ClickFirstRow()
                    {
                        driver.FindElements(By.CssSelector("[data-test='row']"))[0].Click();
                    }

                    public void UnknownNavigation() =>
                        driver.Navigate().GoToUrl(System.Environment.GetEnvironmentVariable("PRIVATE_URL")!);

                    public void BranchingInput(string value)
                    {
                        if (value != null) driver.FindElement(By.Id("search")).SendKeys(value);
                    }

                    public void UnknownCombo(string value)
                    {
                        driver.FindElement(By.Id("search")).Click();
                        MakeSomethingHappen(value);
                    }

                    private void MakeSomethingHappen(string value) {}
                }
                """);
            var engine = new MigrationEngine();
            var snapshot = new PomSourceInspector().Inspect(folder);
            var hash = snapshot.SourceTreeHash;
            var selected = new[]
            {
                ("Open", "navigation", "Page.GotoAsync(url)", 1),
                ("InputAndSelect", "input_sendkeys", "PressSequentiallyAsync(value)", 5),
                ("ValidateLoading", "visible_wait", "Timeout = 1200f", 1),
                ("AssertTableCount", "table_count_assertion", ".CountAsync(), NUnit.Framework.Is.EqualTo(2)", 1),
                ("AssertTableText", "text_assertion", ".Nth(0)", 1),
                ("ClickFirstRow", "click", ".Nth(0).ClickAsync()", 1)
            };
            foreach (var (method, effect, needle, count) in selected)
            {
                var plan = engine.PlanPomActions(folder, "Fictional.Pom.SyntheticCatalogPage",
                    method, hash);
                Check(plan.SchemaVersion == "migrator-pom-action/v1alpha3"
                    && plan.Status == "requires_review" && !plan.CanAutoApply
                    && !plan.RuntimeVerified, method + " must never be auto-approved.");
                Check(plan.Actions.Count == count && plan.TargetStatements.Count == count
                    && plan.Actions.Any(x => x.Effect == effect)
                    && plan.TargetStatements.Any(x => x.Contains(needle, StringComparison.Ordinal)),
                    method + " lost one or more expected source effects.");
                Check(plan.Blockers.Count == 0
                    && plan.SourceMethodSha256.Length == 64 && plan.SourceLine > 0
                    && plan.Actions.All(x => x.SourcePath == "SyntheticCatalogPage.cs"
                        && x.SourceLine > 0 && x.SourceSha256.Length == 64
                        && x.TargetSha256.Length == 64
                        && (x.SourceSymbol.Contains("OpenQA.Selenium.", StringComparison.Ordinal)
                            || x.SourceSymbol.Contains("NUnit.Framework.", StringComparison.Ordinal))
                        && x.RequiresReview),
                    method + " method/step SHA, Roslyn symbols or review gate missing: "
                    + System.Text.Json.JsonSerializer.Serialize(new {
                        plan.SourceMethodSha256, plan.SourceLine, plan.Actions
                    }));
            }

            foreach (var (method, expected) in new[]
            {
                ("UnknownNavigation", "POM_ACTION_UNRESOLVED"),
                ("BranchingInput", "POM_CONDITIONAL_GUARD_UNRESOLVED"),
                ("UnknownCombo", "POM_PRIVATE_HELPER_UNRESOLVED")
            })
            {
                var plan = engine.PlanPomActions(folder, "SyntheticCatalogPage",
                    method, hash);
                Check(plan.Status == "blocked" && plan.Blockers.Any(x =>
                        x.Code == expected && x.SourceLine > 0
                        && x.SourceSha256.Length == 64)
                    && plan.TargetStatements.Count == 0,
                    method + " must block the entire sequence, not emit an active partial migration.");
            }
            var unknown = engine.PlanPomActions(folder, "SyntheticCatalogPage",
                "Absent", hash);
            Check(unknown.Status == "blocked" && unknown.TargetStatements.Count == 0,
                "Unknown method must never inherit another method's target actions.");

            // An opt-in reviewed template must integrate with the existing
            // adapter pipeline rather than only succeeding in isolated POM
            // review. Source parameter "value" must resolve to caller "input",
            // but a source literal selector must remain untouched.
            var comboPlan = engine.PlanPomActions(folder, "SyntheticCatalogPage",
                "InputAndSelect", hash);
            var candidate = comboPlan.AdapterCandidate;
            Check(candidate is { RequiresReview: true }
                && candidate.SourceMethodPattern == "page.InputAndSelect({value})"
                && candidate.TargetStatementTemplates.Count == 5
                && candidate.TargetStatementTemplates.Any(statement =>
                    statement.Contains("PressSequentiallyAsync({value})", StringComparison.Ordinal))
                && candidate.TargetStatementTemplates.Any(statement =>
                    statement.Contains("data-test=", StringComparison.Ordinal)
                    && statement.Contains("option", StringComparison.Ordinal)),
                "Source-derived review candidate must bind parameters, preserve literal selectors: "
                + System.Text.Json.JsonSerializer.Serialize(candidate));

            var fixture = Path.Combine(folder, "MappingFixture.cs");
            File.WriteAllText(fixture, """
                using NUnit.Framework;
                using Fictional.Pom;
                [TestFixture] public class MappingFixture {
                    private SyntheticCatalogPage page;
                    [TestCase("alpha")]
                    public void PicksValue(string input) {
                        page.InputAndSelect(input);
                    }
                }
                """);
            var mappingConfig = new ProjectAdapterConfig
            {
                SourceProjectName = "FictionalPreview30",
                ParameterizedMethods = new[]
                {
                    new ParameterizedMethodMapping
                    {
                        SourceMethodPattern = candidate!.SourceMethodPattern,
                        TargetStatements = candidate.TargetStatementTemplates.ToArray(),
                        RequiresReview = true
                    }
                },
                TestHost = new TestHostConfig
                {
                    BaseClass = "object",
                    ClassAttributes = new[] { "TestFixture" }
                }
            };
            var migrated = new MigrationPipeline(
                new RoslynTestFileParser(mappingConfig),
                new PlaywrightDotNetRenderer(),
                new DefaultProjectAdapter(mappingConfig)).ProcessFile(fixture);
            Check(migrated.GeneratedOutput.Contains("PressSequentiallyAsync(input)",
                    StringComparison.Ordinal)
                && migrated.GeneratedOutput.Contains("data-test=", StringComparison.Ordinal)
                && migrated.GeneratedOutput.Contains("option", StringComparison.Ordinal)
                && migrated.Report.FullyConvertedTests == 0,
                "Parameterized reviewed POM candidate must preserve target effects, "
                + "forward source argument and remain not fully migrated.");
            File.Delete(fixture);

            File.AppendAllText(source, "\n// stale");
            try
            {
                engine.PlanPomActions(folder, "SyntheticCatalogPage", "Open", hash);
                throw new InvalidOperationException("Stale POM plan was accepted.");
            }
            catch (InvalidOperationException e) when (
                e.Message.StartsWith("STALE_POM_ACTION_PLAN", StringComparison.Ordinal)) {}
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static void Check(bool valid, string reason)
    {
        if (!valid)
            throw new InvalidOperationException("POM_ACTION_PREVIEW30_SMOKE: " + reason);
    }
}
