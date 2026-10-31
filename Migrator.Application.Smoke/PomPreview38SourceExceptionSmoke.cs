using Migrator.Application;

internal static class PomPreview38SourceExceptionSmoke
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "p38-source-exception-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "ExceptionPage.cs"), """
                using OpenQA.Selenium;
                using OpenQA.Selenium.Support.UI;
                using NUnit.Framework;
                using System;
                namespace Fictional.P38;
                public sealed class ExceptionPage {
                    private readonly IWebDriver driver;
                    public ExceptionPage(IWebDriver driver) { this.driver = driver; }

                    public void CatchMissingRecovery() {
                        try {
                            Assert.That(driver.FindElement(By.Id("missing")).Text,
                                Is.EqualTo("ready"));
                        } catch (NoSuchElementException) {
                            Assert.That(driver.FindElements(By.Id("missing")).Count,
                                Is.EqualTo(0));
                        }
                    }
                    public void CatchMissingRethrow() {
                        try {
                            Assert.That(driver.FindElement(By.Id("missing")).Text,
                                Is.EqualTo("ready"));
                        } catch (NoSuchElementException) {
                            throw;
                        }
                    }
                    public void CatchMissingUnusedVariable() {
                        try {
                            Assert.That(driver.FindElement(By.Id("missing")).Text,
                                Is.EqualTo("ready"));
                        } catch (NoSuchElementException ignored) {
                            Assert.That(driver.FindElements(By.Id("missing")).Count,
                                Is.EqualTo(0));
                        }
                    }
                    public void CatchTimeoutRecovery() {
                        try {
                            new WebDriverWait(driver, TimeSpan.FromMilliseconds(80))
                                .Until(d => d.FindElements(By.Id("loader")).Count == 0);
                        } catch (WebDriverTimeoutException) {
                            Assert.That(driver.FindElements(By.Id("loader")).Count,
                                Is.EqualTo(1));
                        }
                    }
                    public void CatchTimeoutRethrow() {
                        try {
                            new WebDriverWait(driver, TimeSpan.FromMilliseconds(80))
                                .Until(d => d.FindElements(By.Id("loader")).Count == 0);
                        } catch (WebDriverTimeoutException) {
                            throw;
                        }
                    }

                    public void UnsafeMissingClick() {
                        try { driver.FindElement(By.Id("missing")).Click(); }
                        catch (NoSuchElementException) { throw; }
                    }
                    public void UnsafeWrongCatchType() {
                        try {
                            Assert.That(driver.FindElement(By.Id("missing")).Text,
                                Is.EqualTo("ready"));
                        } catch (WebDriverTimeoutException) { throw; }
                    }
                    public void UnsafeBroadCatch() {
                        try {
                            Assert.That(driver.FindElement(By.Id("missing")).Text,
                                Is.EqualTo("ready"));
                        } catch (Exception) { throw; }
                    }
                    public void UnsafeFilter() {
                        try {
                            Assert.That(driver.FindElement(By.Id("missing")).Text,
                                Is.EqualTo("ready"));
                        } catch (NoSuchElementException ex) when (ex.Message != "") {
                            throw;
                        }
                    }
                    public void UnsafeVariableRead() {
                        try {
                            Assert.That(driver.FindElement(By.Id("missing")).Text,
                                Is.EqualTo("ready"));
                        } catch (NoSuchElementException error) {
                            Assert.That(error.Message, Is.EqualTo("test"));
                        }
                    }
                    public void UnsafeFinally() {
                        try {
                            Assert.That(driver.FindElement(By.Id("missing")).Text,
                                Is.EqualTo("ready"));
                        } catch (NoSuchElementException) { throw; }
                        finally { driver.FindElement(By.Id("clicked")).Click(); }
                    }
                    public void UnsafeExplicitThrowNew() {
                        try {
                            Assert.That(driver.FindElement(By.Id("missing")).Text,
                                Is.EqualTo("ready"));
                        } catch (NoSuchElementException) { throw new Exception("modified"); }
                    }
                    public void UnsafeMultipleActions() {
                        try {
                            Assert.That(driver.FindElement(By.Id("missing")).Text,
                                Is.EqualTo("ready"));
                            driver.FindElement(By.Id("other")).Click();
                        } catch (NoSuchElementException) { throw; }
                    }
                    public void UnsafeCatchSideEffect() {
                        try {
                            Assert.That(driver.FindElement(By.Id("missing")).Text,
                                Is.EqualTo("ready"));
                        } catch (NoSuchElementException) {
                            driver.FindElement(By.Id("other")).Click();
                        }
                    }
                    public void UnsafeTwoCatches() {
                        try {
                            Assert.That(driver.FindElement(By.Id("missing")).Text,
                                Is.EqualTo("ready"));
                        } catch (NoSuchElementException) { throw; }
                          catch (Exception) { throw; }
                    }
                    public void UnsafeTimeoutCatchFilter() {
                        try {
                            new WebDriverWait(driver, TimeSpan.FromMilliseconds(80))
                                .Until(d => d.FindElements(By.Id("loader")).Count == 0);
                        } catch (WebDriverTimeoutException) when (DateTime.UtcNow.Year > 0) {
                            throw;
                        }
                    }
                    public void UnsafeTimeoutDynamicWait(string selector) {
                        try {
                            new WebDriverWait(driver, TimeSpan.FromMilliseconds(80))
                                .Until(d => d.FindElements(By.Id(selector)).Count == 0);
                        } catch (WebDriverTimeoutException) { throw; }
                    }
                    public void UnsafeNonRethrow() {
                        try {
                            Assert.That(driver.FindElement(By.Id("missing")).Text,
                                Is.EqualTo("ready"));
                        } catch (NoSuchElementException) {
                            throw new ArgumentException("unproven");
                        }
                    }
                }
                """);
            var engine = new MigrationEngine();
            var hash = new PomSourceInspector().Inspect(root).SourceTreeHash;
            PomActionPlan Plan(string method) => engine.PlanPomActions(root,
                "Fictional.P38.ExceptionPage", method, hash);
            foreach (var (method, sourceKind, mappedHeader, recovery) in new[] {
                ("CatchMissingRecovery", "NoSuch", "catch (Microsoft.Playwright.PlaywrightException sourceMissing)", true),
                ("CatchMissingRethrow", "NoSuch", "catch (Microsoft.Playwright.PlaywrightException sourceMissing)", false),
                ("CatchMissingUnusedVariable", "NoSuch", "catch (Microsoft.Playwright.PlaywrightException sourceMissing)", true),
                ("CatchTimeoutRecovery", "Timeout", "catch (System.TimeoutException)", true),
                ("CatchTimeoutRethrow", "Timeout", "catch (System.TimeoutException)", false)
            })
            {
                var p = Plan(method);
                var stmt = p.TargetStatements.SingleOrDefault() ?? "";
                Check(p.Status == "requires_review" && !p.RuntimeVerified
                    && !p.CanAutoApply && p.Blockers.Count == 0
                    && p.AdapterCandidate is { RequiresReview: true }
                    && p.SourceMethodSha256.Length == 64
                    && p.Actions.Count >= 3
                    && p.Actions.Any(a => a.Effect == "source_exception_catch"
                        && a.ControlFlowPath == "root")
                    && stmt.StartsWith("try\n{", StringComparison.Ordinal)
                    && stmt.Contains(mappedHeader, StringComparison.Ordinal)
                    && stmt.Contains(sourceKind == "NoSuch"
                        ? "POM_SOURCE_ELEMENT_ABSENT" : "WaitForSelectorState.Detached",
                        StringComparison.Ordinal)
                    && stmt.Contains(recovery
                        ? ".CountAsync(), NUnit.Framework.Is.EqualTo("
                        : "throw;", StringComparison.Ordinal)
                    && !stmt.Contains("OpenQA.Selenium", StringComparison.Ordinal)
                    && !stmt.Contains("WebDriverTimeoutException", StringComparison.Ordinal)
                    && !stmt.Contains("NoSuchElementException", StringComparison.Ordinal)
                    && p.Actions.All(a => a.SourceSha256.Length == 64
                        && a.TargetSha256.Length == 64),
                    method + " lost exact typed source catch or unapproved target: "
                    + System.Text.Json.JsonSerializer.Serialize(p));
                if (!recovery)
                    Check(p.Actions.Any(a => a.Effect == "exception_rethrow"
                        && a.ControlFlowPath.Contains("exception:catch", StringComparison.Ordinal)),
                        method + " lost source rethrow identity and provenance.");
                if (sourceKind == "NoSuch")
                    Check(stmt.Contains("when (sourceMissing.Message.Contains(",
                        StringComparison.Ordinal),
                        "NoSuch must guard the precise emitted missing-element marker.");
            }
            foreach (var method in new[] {
                "UnsafeMissingClick", "UnsafeWrongCatchType", "UnsafeBroadCatch",
                "UnsafeFilter", "UnsafeVariableRead", "UnsafeFinally",
                "UnsafeExplicitThrowNew", "UnsafeMultipleActions",
                "UnsafeCatchSideEffect", "UnsafeTwoCatches",
                "UnsafeTimeoutCatchFilter", "UnsafeTimeoutDynamicWait",
                "UnsafeNonRethrow"
            })
            {
                var p = Plan(method);
                Check(p.Status == "blocked" && p.Blockers.Count > 0
                    && p.TargetStatements.Count == 0 && p.AdapterCandidate == null
                    && p.Blockers.All(b => b.SourceSha256.Length == 64),
                    method + " must reject unsafe source catch with no executable draft.");
            }
            Check(new PomSourceInspector().Inspect(root).SourceTreeHash == hash,
                "Source files changed during read-only planning.");
            File.AppendAllText(Path.Combine(root, "ExceptionPage.cs"), "\n//stale");
            try { Plan("CatchMissingRecovery"); throw new Exception("Accepted stale source"); }
            catch (InvalidOperationException e) when (
                e.Message.StartsWith("STALE_POM_ACTION_PLAN", StringComparison.Ordinal)) { }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("POM_PREVIEW38_SOURCE_EXCEPTION_AST: " + message);
    }
}
