using Migrator.Application;

internal static class PomStructuredActionsSmoke
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "fictional-p31-structured-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "SyntheticPage.cs");
        try
        {
            File.WriteAllText(source, """
                using OpenQA.Selenium;
                using OpenQA.Selenium.Support.UI;
                using NUnit.Framework;
                using System;
                using System.Threading;
                namespace Fictional.P31;
                public sealed class SyntheticPage {
                    private readonly IWebDriver driver;
                    public SyntheticPage(IWebDriver driver) { this.driver = driver; }

                    public void Choose(string value) {
                        OpenPicker();
                        driver.FindElement(By.Id("search")).Clear();
                        driver.FindElement(By.Id("search")).SendKeys(value);
                        if (driver.FindElements(By.CssSelector("[data-test='option']")).Count > 0) {
                            driver.FindElements(By.CssSelector("[data-test='option']"))[0].Click();
                            driver.FindElement(By.Id("accept")).Click();
                        } else {
                            Assert.That(driver.FindElements(By.CssSelector("[data-test='option']")).Count,
                                Is.EqualTo(0));
                        }
                    }
                    private void OpenPicker() {
                        driver.FindElement(By.Id("picker")).Click();
                    }

                    public void WaitReady(int timeoutMs) {
                        if (timeoutMs < 1 || timeoutMs > 3000)
                            throw new ArgumentOutOfRangeException(nameof(timeoutMs));
                        new WebDriverWait(driver, TimeSpan.FromMilliseconds(timeoutMs))
                            .Until(d => d.FindElement(By.Id("ready")).Displayed);
                    }

                    public void ChooseConditional(string value) {
                        if (value == "alpha") {
                            driver.FindElements(By.CssSelector("[data-test='row']"))[0].Click();
                        } else {
                            Assert.That(driver.FindElements(By.CssSelector("[data-test='row']")).Count,
                                Is.GreaterThan(1));
                        }
                    }

                    public void CheckFewRows() {
                        Assert.That(driver.FindElements(By.CssSelector("[data-test='row']")).Count,
                            Is.LessThan(3));
                    }
                    public void UnsafeDynamicWait(int timeoutMs) {
                        new WebDriverWait(driver, TimeSpan.FromMilliseconds(timeoutMs))
                            .Until(d => d.FindElement(By.Id("ready")).Displayed);
                    }
                    public void UnsafeDynamicUrl() {
                        driver.Navigate().GoToUrl(Environment.GetEnvironmentVariable("PRIVATE_URL")!);
                    }
                    public void UnsafeBranch() {
                        if (driver.FindElements(By.Id("picker")).Count > 0) {
                            driver.FindElement(By.Id("picker")).Click();
                        } else {
                            Thread.Sleep(9);
                        }
                    }
                    public void UnsafeCondition() {
                        if (Environment.TickCount > 0) driver.FindElement(By.Id("picker")).Click();
                    }
                    public void UnsafeLoop() {
                        while (true) driver.FindElement(By.Id("picker")).Click();
                    }
                    public void UnsafePrivateRecursion() { Recursive(); }
                    private void Recursive() { Recursive(); }
                    public void UnsafeSideEffect() { Mutate(); }
                    private void Mutate() { Environment.SetEnvironmentVariable("X", "Y"); }
                    public void UnsafeMissingTimeoutGuard(int timeoutMs) {
                        if (timeoutMs > 5000) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
                        new WebDriverWait(driver, TimeSpan.FromMilliseconds(timeoutMs))
                            .Until(d => d.FindElement(By.Id("ready")).Displayed);
                    }
                }
                """);
            var engine = new MigrationEngine();
            var captured = new PomSourceInspector().Inspect(root);
            var hash = captured.SourceTreeHash;
            Check(hash.Length == 64, "No source tree SHA-256.");

            var combo = engine.PlanPomActions(root, "Fictional.P31.SyntheticPage",
                "Choose", hash);
            Check(combo.SchemaVersion == "migrator-pom-action/v1alpha2"
                && combo.Status == "requires_review" && !combo.RuntimeVerified
                && !combo.CanAutoApply && combo.Blockers.Count == 0
                && combo.TargetStatements.Count == 4
                && combo.Actions.Count >= 7
                && combo.Actions.Any(a => a.Effect == "private_helper_inline"
                    && a.SourcePath == "SyntheticPage.cs")
                && combo.Actions.Any(a => a.Effect == "conditional_guard")
                && combo.TargetStatements.Last().Contains("else", StringComparison.Ordinal)
                && combo.TargetStatements.Last().Contains("CountAsync()", StringComparison.Ordinal),
                "Complex ComboBox must preserve private OpenPicker + all condition branches.");
            Check(combo.AdapterCandidate is { RequiresReview: true }
                && combo.AdapterCandidate.TargetStatementTemplates.Any(t =>
                    t.Contains("PressSequentiallyAsync({value})", StringComparison.Ordinal))
                && combo.Actions.All(a =>
                    a.SourcePath == "SyntheticPage.cs" && a.SourceLine > 0
                    && a.SourceSha256.Length == 64 && a.TargetSha256.Length == 64
                    && a.RequiresReview),
                "Complex source action ordering, hashes or value forwarding missing.");

            var loader = engine.PlanPomActions(root, "SyntheticPage", "WaitReady", hash);
            Check(loader.Status == "requires_review" && loader.Blockers.Count == 0
                && loader.Actions.Any(x => x.Effect == "timeout_bound_guard")
                && loader.Actions.Any(x => x.Effect == "visible_wait")
                && loader.TargetStatements.Count == 2
                && loader.TargetStatements[0].Contains("timeoutMs > 3000",
                    StringComparison.Ordinal)
                && loader.TargetStatements[1].Contains("Timeout = (float)timeoutMs",
                    StringComparison.Ordinal),
                "Loader must preserve exact source-bound timeout guard and original ms parameter.");

            var row = engine.PlanPomActions(root, "SyntheticPage",
                "ChooseConditional", hash);
            Check(row.Status == "requires_review"
                && row.Actions.Any(x => x.Effect == "conditional_guard")
                && row.Actions.Any(x => x.Effect == "table_count_comparison")
                && row.TargetStatements.Single().Contains("else",
                    StringComparison.Ordinal)
                && row.TargetStatements.Single().Contains("value == \"alpha\"",
                    StringComparison.Ordinal),
                "Typed conditional rows must retain both source branches.");
            var fewRows = engine.PlanPomActions(root, "SyntheticPage",
                "CheckFewRows", hash);
            Check(fewRows.Status == "requires_review"
                && fewRows.TargetStatements.Single().Contains(
                    "NUnit.Framework.Is.LessThan(3)", StringComparison.Ordinal),
                "NUnit strict inequality must not be changed to equality.");

            foreach (var (method, blocker) in new[]
            {
                ("UnsafeDynamicWait", "POM_DYNAMIC_WAIT_UNBOUNDED"),
                ("UnsafeDynamicUrl", "POM_ACTION_UNRESOLVED"),
                ("UnsafeBranch", "POM_ACTION_UNRESOLVED"),
                ("UnsafeCondition", "POM_CONDITIONAL_GUARD_UNRESOLVED"),
                ("UnsafeLoop", "POM_UNSUPPORTED_STATEMENT"),
                ("UnsafePrivateRecursion", "POM_HELPER_RECURSION"),
                ("UnsafeSideEffect", "POM_ACTION_UNRESOLVED"),
                ("UnsafeMissingTimeoutGuard", "POM_UNSUPPORTED_STATEMENT")
            })
            {
                var blocked = engine.PlanPomActions(root, "SyntheticPage", method, hash);
                Check(blocked.Status == "blocked"
                    && blocked.TargetStatements.Count == 0
                    && blocked.AdapterCandidate == null
                    && blocked.Blockers.Any(b => b.Code == blocker
                        && b.SourceSha256.Length == 64 && b.SourceLine > 0),
                    "Unsupported source effect was silently promoted: " + method
                    + " expected " + blocker + " actual " +
                    string.Join(",", blocked.Blockers.Select(x => x.Code)));
            }

            File.AppendAllText(source, "\n// mutation");
            try
            {
                engine.PlanPomActions(root, "SyntheticPage", "Choose", hash);
                throw new InvalidOperationException("Stale source was accepted.");
            }
            catch (InvalidOperationException ex) when (
                ex.Message.StartsWith("STALE_POM_ACTION_PLAN", StringComparison.Ordinal)) {}
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Check(bool valid, string details)
    {
        if (!valid)
            throw new InvalidOperationException(
                "POM_STRUCTURED_PREVIEW31_SMOKE: " + details);
    }
}
