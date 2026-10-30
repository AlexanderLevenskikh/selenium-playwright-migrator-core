using Migrator.Application;

internal static class PomTypedHelperRetrySmoke
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "fictional-pom-preview32-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "Synthetic.cs");
        try
        {
            File.WriteAllText(file, """
                using System;
                using OpenQA.Selenium;
                using OpenQA.Selenium.Support.UI;
                using NUnit.Framework;
                namespace Fictional.P32;
                public sealed class SyntheticPage {
                    readonly IWebDriver driver;
                    public SyntheticPage(IWebDriver driver) => this.driver = driver;
                    public void Choose(string value) {
                        Open();
                        ResetAndType(value);
                        if (driver.FindElements(By.CssSelector("[data-test='option']")).Count > 0) {
                            driver.FindElements(By.CssSelector("[data-test='option']"))[0].Click();
                        } else {
                            Assert.That(driver.FindElements(By.CssSelector("[data-test='option']")).Count,
                                Is.EqualTo(0));
                        }
                    }
                    private void Open() { driver.FindElement(By.Id("picker")).Click(); }
                    private void ResetAndType(string search) {
                        driver.FindElement(By.Id("search")).Clear();
                        TypeValue(search);
                    }
                    private void TypeValue(string text) {
                        driver.FindElement(By.Id("search")).SendKeys(text);
                    }
                    public void ChooseLiteral() { TypeValue("literal-choice"); }

                    public void WaitReadyRetry() {
                        for (var attempt = 0; attempt < 2; attempt++) {
                            try {
                                new WebDriverWait(driver, TimeSpan.FromMilliseconds(120))
                                    .Until(d => d.FindElement(By.Id("ready")).Displayed);
                                break;
                            } catch (WebDriverTimeoutException) {
                                if (attempt == 1) throw;
                            }
                        }
                    }
                    public void UnsafeRetryWrongCatch() {
                        for (var attempt = 0; attempt < 2; attempt++) {
                            try {
                                new WebDriverWait(driver, TimeSpan.FromMilliseconds(120))
                                    .Until(d => d.FindElement(By.Id("ready")).Displayed);
                                break;
                            } catch (Exception) {
                                if (attempt == 1) throw;
                            }
                        }
                    }
                    public void UnsafeRetryWrongFinal() {
                        for (var attempt = 0; attempt < 2; attempt++) {
                            try {
                                new WebDriverWait(driver, TimeSpan.FromMilliseconds(120))
                                    .Until(d => d.FindElement(By.Id("ready")).Displayed);
                                break;
                            } catch (WebDriverTimeoutException) {
                                if (attempt == 0) throw;
                            }
                        }
                    }
                    public void UnsafeRetryTooMany() {
                        for (var attempt = 0; attempt < 42; attempt++) {
                            try {
                                new WebDriverWait(driver, TimeSpan.FromMilliseconds(120))
                                    .Until(d => d.FindElement(By.Id("ready")).Displayed);
                                break;
                            } catch (WebDriverTimeoutException) {
                                if (attempt == 41) throw;
                            }
                        }
                    }
                    public void UnsafeRetrySideEffect() {
                        for (var attempt = 0; attempt < 2; attempt++) {
                            try {
                                System.Threading.Thread.Sleep(1);
                                new WebDriverWait(driver, TimeSpan.FromMilliseconds(120))
                                    .Until(d => d.FindElement(By.Id("ready")).Displayed);
                                break;
                            } catch (WebDriverTimeoutException) {
                                if (attempt == 1) throw;
                            }
                        }
                    }
                    public void UnsafeRetryUnbounded() {
                        while (true) {
                            new WebDriverWait(driver, TimeSpan.FromMilliseconds(120))
                                .Until(d => d.FindElement(By.Id("ready")).Displayed);
                        }
                    }
                    public void UnsafeTypedHelperArgument() {
                        TypeValue(Environment.GetEnvironmentVariable("PRIVATE")!);
                    }
                    public void UnsafeTypedHelperSideEffect(string value) {
                        Unknown(value);
                    }
                    private void Unknown(string value) {
                        Environment.SetEnvironmentVariable("A", value);
                    }
                    public void UnsafeTypedHelperInt(int count) { TypeInt(count); }
                    private void TypeInt(int value) { driver.FindElement(By.Id("search")).Click(); }
                    public void UnsafeRecursion(string value) { Recursive(value); }
                    private void Recursive(string value) { Recursive(value); }
                }
                """);
            var engine = new MigrationEngine();
            var hash = new PomSourceInspector().Inspect(root).SourceTreeHash;
            var combo = engine.PlanPomActions(root, "Fictional.P32.SyntheticPage",
                "Choose", hash);
            Check(combo.SchemaVersion == "migrator-pom-action/v1alpha3"
                && combo.Status == "requires_review"
                && !combo.CanAutoApply && !combo.RuntimeVerified
                && combo.Blockers.Count == 0
                && combo.Actions.Count >= 7
                && combo.Actions.Count(x => x.Effect == "private_helper_inline") == 3
                && combo.TargetStatements.Count == 3
                && combo.TargetStatements[1].Contains("PressSequentiallyAsync(value)",
                    StringComparison.Ordinal)
                && combo.TargetStatements[2].Contains("else", StringComparison.Ordinal),
                "Nested typed helper must be fully inlined with parameter forwarding and preserved branches: "
                    + System.Text.Json.JsonSerializer.Serialize(combo));
            Check(combo.AdapterCandidate is { RequiresReview: true }
                && combo.AdapterCandidate.TargetStatementTemplates[1].Contains(
                    "PressSequentiallyAsync({value})", StringComparison.Ordinal)
                && combo.Actions.Any(x => x.ControlFlowPath.Contains(
                    "helper:TypeValue@", StringComparison.Ordinal)
                    && x.TargetStatement.Contains("PressSequentiallyAsync(value)",
                        StringComparison.Ordinal))
                && combo.Actions.All(x => x.SourcePath == "Synthetic.cs"
                    && x.SourceLine > 0 && x.SourceSha256.Length == 64
                    && x.TargetSha256.Length == 64
                    && x.RequiresReview),
                "Typed helper source provenance or token-safe substitution failed.");
            var literal = engine.PlanPomActions(root, "SyntheticPage", "ChooseLiteral", hash);
            Check(literal.Status == "requires_review"
                && literal.TargetStatements.Single().Contains(
                    "PressSequentiallyAsync(\\\"literal-choice\\\")", StringComparison.Ordinal),
                "Literal helper argument must be bound as a C# quoted literal.");

            var retry = engine.PlanPomActions(root, "SyntheticPage", "WaitReadyRetry", hash);
            Check(retry.Status == "requires_review"
                && retry.TargetStatements.Count == 1
                && retry.Actions.Any(x => x.Effect == "bounded_retry")
                && retry.Actions.Any(x => x.Effect == "visible_wait"
                    && x.ControlFlowPath.Contains("retry:try@", StringComparison.Ordinal))
                && retry.Actions.Any(x => x.Effect == "retry_failure_rethrow"
                    && x.ControlFlowPath.Contains("retry:catch@", StringComparison.Ordinal))
                && retry.TargetStatements[0].Contains(
                    "catch (Microsoft.Playwright.TimeoutException)", StringComparison.Ordinal)
                && retry.TargetStatements[0].Contains("if (attempt == 1) throw;",
                    StringComparison.Ordinal)
                && retry.TargetStatements[0].Contains("Timeout = 120f",
                    StringComparison.Ordinal)
                && retry.AdapterCandidate is { RequiresReview: true },
                "Finite source retry must preserve exact attempt count, timeout, exception type, break and terminal rethrow: "
                    + System.Text.Json.JsonSerializer.Serialize(retry));

            foreach (var method in new[]
            {
                "UnsafeRetryWrongCatch", "UnsafeRetryWrongFinal",
                "UnsafeRetryTooMany", "UnsafeRetrySideEffect",
                "UnsafeRetryUnbounded", "UnsafeTypedHelperArgument",
                "UnsafeTypedHelperSideEffect", "UnsafeTypedHelperInt",
                "UnsafeRecursion"
            })
            {
                var blocked = engine.PlanPomActions(root, "SyntheticPage", method, hash);
                Check(blocked.Status == "blocked" && blocked.TargetStatements.Count == 0
                    && blocked.AdapterCandidate == null && blocked.Blockers.Count > 0
                    && blocked.Blockers.All(x => x.SourceLine > 0
                        && x.SourceSha256.Length == 64),
                    method + " must fail closed, with source hash and no partial runnable target: "
                        + System.Text.Json.JsonSerializer.Serialize(blocked));
            }
            File.AppendAllText(file, "\n// stale");
            try
            {
                engine.PlanPomActions(root, "SyntheticPage", "Choose", hash);
                throw new InvalidOperationException("Stale POM source was accepted.");
            }
            catch (InvalidOperationException e) when (
                e.Message.StartsWith("STALE_POM_ACTION_PLAN", StringComparison.Ordinal)) { }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(
            "POM_PREVIEW32_TYPED_HELPER_RETRY_SMOKE: " + reason);
    }
}
