using Migrator.Application;

internal static class PomParameterizedCountGuardSmoke
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "fictional-p3-pom-count-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "CountPage.cs");
        try
        {
            File.WriteAllText(file, """
                using OpenQA.Selenium;
                using NUnit.Framework;
                using System;
                namespace Fictional.P3;
                public sealed class CountPage {
                    readonly IWebDriver driver;
                    private readonly int cached;
                    private readonly string dynamicSelector;
                    public CountPage(IWebDriver driver) {
                        this.driver=driver;
                        cached=0;dynamicSelector=".row";
                    }
                    public void Choose(int minimum,int maximum) {
                        if (driver.FindElements(By.CssSelector(".row")).Count >= minimum
                            && maximum > driver.FindElements(By.CssSelector(".row")).Count) {
                            Accept();
                        } else {
                            Reject();
                        }
                    }
                    public void Reverse(int threshold) {
                        if (threshold < driver.FindElements(By.CssSelector(".row")).Count)
                            Accept();
                        else Reject();
                    }
                    public void Confirm(string decision) {
                        if ("yes" == decision) Accept(); else Reject();
                    }
                    private void Accept() { driver.FindElement(By.Id("accept")).Click(); }
                    private void Reject() { driver.FindElement(By.Id("reject")).Click(); }
                    public void UnsafeCapturedOtherDriver(IWebDriver otherDriver,int minimum) {
                        if (otherDriver.FindElements(By.CssSelector(".row")).Count >= minimum)
                            Accept(); else Reject();
                    }
                    public void UnsafeDynamicSelector(int minimum) {
                        if (driver.FindElements(By.CssSelector(dynamicSelector)).Count >= minimum)
                            Accept(); else Reject();
                    }
                    public void UnsafeCachedCount(int minimum) {
                        if (cached >= minimum) Accept(); else Reject();
                    }
                    public void UnsafeArithmeticThreshold(int minimum) {
                        if (driver.FindElements(By.CssSelector(".row")).Count >= minimum+1)
                            Accept(); else Reject();
                    }
                    public void UnsafeUnrelatedEffect(int minimum) {
                        if (driver.FindElements(By.CssSelector(".row")).Count >= minimum)
                            Environment.SetEnvironmentVariable("PRIVATE", "1");
                    }
                }
                public sealed class AmbiguousCountPage {
                    readonly IWebDriver a;
                    readonly IWebDriver b;
                    public AmbiguousCountPage(IWebDriver a,IWebDriver b) {
                        this.a=a;this.b=b;
                    }
                    public void UnsafeCount(int minimum) {
                        if (a.FindElements(By.CssSelector(".row")).Count >= minimum)
                            a.FindElement(By.Id("accept")).Click();
                    }
                }
                """);
            var engine = new MigrationEngine();
            var snapshot = new PomSourceInspector().Inspect(root).SourceTreeHash;
            PomActionPlan Plan(string method) =>
                engine.PlanPomActions(root, "Fictional.P3.CountPage", method, snapshot);
            var choose = Plan("Choose");
            Check(choose.Status == "requires_review"
                && !choose.RuntimeVerified && !choose.CanAutoApply
                && choose.Blockers.Count == 0 && choose.TargetStatements.Count == 1
                && choose.Actions.Any(a => a.Effect == "conditional_guard")
                && choose.Actions.Count(a => a.Effect == "private_helper_inline") == 2
                && choose.TargetStatements[0].Contains("CountAsync()) >= minimum",
                    StringComparison.Ordinal)
                && choose.TargetStatements[0].Contains(
                    "maximum > (await Page.Locator(", StringComparison.Ordinal)
                && choose.TargetStatements[0].Contains("else", StringComparison.Ordinal)
                && choose.AdapterCandidate is { RequiresReview: true }
                && choose.AdapterCandidate.TargetStatementTemplates[0]
                    .Contains("{maximum}", StringComparison.Ordinal)
                && choose.AdapterCandidate.TargetStatementTemplates[0]
                    .Contains("{minimum}", StringComparison.Ordinal)
                && choose.Actions.All(a => a.SourceSha256.Length == 64
                    && a.TargetSha256.Length == 64 && a.SourceLine > 0
                    && a.RequiresReview),
                "source-owned ordered double Count guard, private helpers and exact inputs lost");
            var reverse = Plan("Reverse");
            Check(reverse.Status == "requires_review"
                && reverse.TargetStatements.Single().Contains(
                    "threshold < (await Page.Locator(", StringComparison.Ordinal)
                && reverse.TargetStatements.Single().Contains("else",
                    StringComparison.Ordinal), "reversed int/Count predicate lost");
            var stringReverse = Plan("Confirm");
            Check(stringReverse.Status == "requires_review"
                && stringReverse.TargetStatements.Single().Contains(
                    "\"yes\" == decision", StringComparison.Ordinal),
                "reversed string-parameter comparison lost");

            foreach (var method in new[] {
                "UnsafeCapturedOtherDriver", "UnsafeDynamicSelector",
                "UnsafeCachedCount", "UnsafeArithmeticThreshold",
                "UnsafeUnrelatedEffect" })
            {
                var negative = Plan(method);
                Check(negative.Status == "blocked"
                    && negative.TargetStatements.Count == 0
                    && negative.AdapterCandidate == null
                    && negative.Blockers.Count > 0,
                    "unknown selector/receiver/state/side effect leaked into active target: "
                    + method);
            }
            var ambiguous = engine.PlanPomActions(root,
                "Fictional.P3.AmbiguousCountPage", "UnsafeCount", snapshot);
            Check(ambiguous.Status == "blocked"
                && ambiguous.TargetStatements.Count == 0
                && ambiguous.AdapterCandidate == null
                && ambiguous.Blockers.Any(x => x.Code ==
                    "POM_CONDITIONAL_GUARD_UNRESOLVED"),
                "multiple IWebDriver fields must not implicitly select target Page");
            File.AppendAllText(file, "\n// exact source mutation");
            try
            {
                Plan("Choose");
                throw new InvalidOperationException("Stale source hash passed.");
            }
            catch (InvalidOperationException ex) when (
                ex.Message.StartsWith("STALE_POM_ACTION_PLAN", StringComparison.Ordinal)) {}
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Check(bool ok, string explanation)
    {
        if (!ok)
            throw new InvalidOperationException(
                "P3_PARAMETERIZED_POM_COUNT_SMOKE: " + explanation);
    }
}
