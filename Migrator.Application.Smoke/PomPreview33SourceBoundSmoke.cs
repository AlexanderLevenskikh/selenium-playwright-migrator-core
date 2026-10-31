using Migrator.Application;

internal static class PomPreview33SourceBoundSmoke
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "fictional-pom-preview33-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "Page.cs"), """
                using System;
                using OpenQA.Selenium;
                using OpenQA.Selenium.Support.UI;
                using NUnit.Framework;
                namespace Fictional.P33;
                public sealed partial class SyntheticPage {
                    private readonly IWebDriver driver;
                    public SyntheticPage(IWebDriver driver) { this.driver = driver; }
                    public void Lookup(string query, string confirmation) {
                        SearchThenConfirm(query, confirmation);
                    }
                    public void LookupSwap(string a, string b) {
                        SearchThenConfirm(b, a);
                    }
                    public void LookupLiteral() {
                        SearchThenConfirm("literal-value", "yes");
                    }
                    public void Guard(int minimum) {
                        if (!(minimum < 2) &&
                            (driver.FindElements(By.CssSelector("[data-test='option']")).Count > 0
                             || minimum == 7)) {
                            driver.FindElement(By.Id("accept")).Click();
                        } else {
                            Assert.That(driver.FindElements(By.Id("accept")).Count, Is.EqualTo(0));
                        }
                    }
                    public void WaitGone() {
                        new WebDriverWait(driver, TimeSpan.FromMilliseconds(75))
                            .Until(d => d.FindElements(By.Id("loader")).Count == 0);
                    }
                    public void RetryGone() {
                        for (var attempt = 0; attempt < 2; attempt++) {
                            try {
                                new WebDriverWait(driver, TimeSpan.FromMilliseconds(75))
                                    .Until(d => d.FindElements(By.Id("loader")).Count == 0);
                                break;
                            } catch (WebDriverTimeoutException) {
                                if (attempt == 1) throw;
                            }
                        }
                    }
                    public void UnsafeHidden() {
                        new WebDriverWait(driver, TimeSpan.FromMilliseconds(75))
                            .Until(d => !d.FindElement(By.Id("loader")).Displayed);
                    }
                    public void UnsafeCapturedDriver() {
                        new WebDriverWait(driver, TimeSpan.FromMilliseconds(75))
                            .Until(d => driver.FindElements(By.Id("loader")).Count == 0);
                    }
                    public void UnsafeCountPredicate() {
                        new WebDriverWait(driver, TimeSpan.FromMilliseconds(75))
                            .Until(d => d.FindElements(By.Id("loader")).Count < 1);
                    }
                    public void UnsafeDynamicArgument() {
                        SearchThenConfirm(Environment.GetEnvironmentVariable("SECRET")!, "yes");
                    }
                    public void UnsafeThreeArguments() { Three("a", "b", "c"); }
                    private void Three(string a, string b, string c) {
                        driver.FindElement(By.Id("accept")).Click();
                    }
                    public void UnsafeComputedInt(int minimum) { GuardHelper(minimum + 1); }
                    private void GuardHelper(int threshold) {
                        if (threshold > 0) driver.FindElement(By.Id("accept")).Click();
                    }
                    public void UnsafeRetryCatchFilter() {
                        for (var attempt = 0; attempt < 2; attempt++) {
                            try {
                                new WebDriverWait(driver, TimeSpan.FromMilliseconds(75))
                                    .Until(d => d.FindElements(By.Id("loader")).Count == 0);
                                break;
                            } catch (WebDriverTimeoutException) when (DateTime.UtcNow.Year > 0) {
                                if (attempt == 1) throw;
                            }
                        }
                    }
                    public void UnsafeExtraSideEffect() { BadHelper("choice", "yes"); }
                    private void BadHelper(string a, string b) {
                        System.Threading.Thread.Sleep(5);
                        SearchThenConfirm(a, b);
                    }
                }
                """);
            File.WriteAllText(Path.Combine(root, "Helpers.cs"), """
                using OpenQA.Selenium;
                using NUnit.Framework;
                namespace Fictional.P33;
                public sealed partial class SyntheticPage {
                    private void SearchThenConfirm(string a, string b) {
                        driver.FindElement(By.Id("picker")).Click();
                        driver.FindElement(By.Id("search")).Clear();
                        driver.FindElement(By.Id("search")).SendKeys(a);
                        if (b == "yes") {
                            driver.FindElement(By.Id("accept")).Click();
                        } else {
                            Assert.That(driver.FindElements(By.Id("accept")).Count,
                                Is.GreaterThan(0));
                        }
                    }
                }
                """);
            var inspector = new PomSourceInspector();
            var sha = inspector.Inspect(root).SourceTreeHash;
            var engine = new MigrationEngine();
            PomActionPlan Plan(string name) => engine.PlanPomActions(
                root, "Fictional.P33.SyntheticPage", name, sha);

            var main = Plan("Lookup");
            Check(main.Status == "requires_review"
                && !main.CanAutoApply && !main.RuntimeVerified
                && main.Blockers.Count == 0 && main.TargetStatements.Count == 1
                && main.TargetStatements.Single().Contains("PressSequentiallyAsync(query)")
                && main.TargetStatements.Single().Contains("if (confirmation == \"yes\")")
                && main.TargetStatements.Single().Contains("else")
                && main.AdapterCandidate is { RequiresReview: true }
                && main.AdapterCandidate.TargetStatementTemplates.Single()
                    .Contains("PressSequentiallyAsync({query})")
                && main.AdapterCandidate.TargetStatementTemplates.Single()
                    .Contains("{confirmation} == \"yes\"")
                && main.Actions.Any(a => a.SourcePath == "Helpers.cs"
                    && a.ControlFlowPath.Contains("helper:SearchThenConfirm@"))
                && main.Actions.All(a => a.SourceLine > 0
                    && a.SourceSha256.Length == 64 && a.TargetSha256.Length == 64),
                "Cross-file helper forwarding, actions, review boundary or SHA evidence lost.");

            Check(Plan("LookupSwap").TargetStatements.Single()
                .Contains("PressSequentiallyAsync(b)")
                && Plan("LookupSwap").TargetStatements.Single()
                .Contains("if (a == \"yes\")"), "Positional swap suffered name capture.");
            Check(Plan("LookupLiteral").TargetStatements.Single()
                .Contains("PressSequentiallyAsync(\"literal-value\")")
                && Plan("LookupLiteral").TargetStatements.Single()
                .Contains("if (\"yes\" == \"yes\")"), "Literal binding lost.");

            var guard = Plan("Guard");
            Check(guard.Status == "requires_review"
                && guard.TargetStatements.Single().Contains("!(minimum < 2)")
                && guard.TargetStatements.Single().Contains("&&")
                && guard.TargetStatements.Single().Contains("||")
                && guard.TargetStatements.Single().Contains(".CountAsync()")
                && guard.Actions.Any(a => a.ControlFlowPath.Contains("if:true@"))
                && guard.Actions.Any(a => a.ControlFlowPath.Contains("if:false@")),
                "Short-circuit numeric predicate or branch coverage lost.");

            var wait = Plan("WaitGone");
            Check(wait.Status == "requires_review"
                && wait.Actions.Any(a => a.Effect == "detached_wait")
                && wait.TargetStatements.Single()
                    .Contains("WaitForSelectorState.Detached, Timeout = 75f"),
                "Count==0 wait must be Detached, not Hidden.");
            var retry = Plan("RetryGone");
            Check(retry.Status == "requires_review"
                && retry.TargetStatements.Single().Contains("WaitForSelectorState.Detached")
                && retry.TargetStatements.Single().Contains("catch (System.TimeoutException)")
                && retry.TargetStatements.Single().Contains("if (attempt == 1) throw;")
                && retry.Actions.Any(a => a.Effect == "bounded_retry")
                && retry.Actions.Any(a => a.Effect == "retry_failure_rethrow"),
                "Finite retry terminal path disappeared.");

            foreach (var method in new[] {
                "UnsafeHidden", "UnsafeCapturedDriver", "UnsafeCountPredicate",
                "UnsafeDynamicArgument", "UnsafeThreeArguments",
                "UnsafeComputedInt", "UnsafeRetryCatchFilter", "UnsafeExtraSideEffect"
            })
            {
                var blocked = Plan(method);
                Check(blocked.Status == "blocked"
                    && blocked.TargetStatements.Count == 0
                    && blocked.AdapterCandidate == null
                    && blocked.Blockers.Any(b => b.SourceLine > 0
                        && b.SourceSha256.Length == 64), method + " must fail closed.");
            }
            Check(inspector.Inspect(root).SourceTreeHash == sha,
                "Read-only AST projection modified source.");
            File.AppendAllText(Path.Combine(root, "Helpers.cs"), "\n// stale");
            try { Plan("Lookup"); throw new Exception("Stale source accepted"); }
            catch (InvalidOperationException e) when (
                e.Message.StartsWith("STALE_POM_ACTION_PLAN")) { }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(
            "POM_PREVIEW33_SOURCE_BOUND_SMOKE: " + message);
    }
}
