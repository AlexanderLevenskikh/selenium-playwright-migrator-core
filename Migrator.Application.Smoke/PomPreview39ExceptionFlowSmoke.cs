using Migrator.Application;

internal static class PomPreview39ExceptionFlowSmoke
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "fictional-preview39-callgraph-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "Page.cs"), """
                using OpenQA.Selenium;
                using OpenQA.Selenium.Support.UI;
                using NUnit.Framework;
                using System;
                namespace Fictional.P39;
                public class SourcePage {
                    private readonly IWebDriver driver;
                    public SourcePage(IWebDriver driver) { this.driver=driver; }

                    public void FindDirect() {
                        Assert.That(driver.FindElement(By.Id("private-direct")).Text,
                            Is.EqualTo("ready"));
                    }
                    public void FindViaTestBase() {
                        Assert.That(driver.FindElement(By.Id("private-base")).Text,
                            Is.EqualTo("ready"));
                    }
                    public void FindFiltered() {
                        Assert.That(driver.FindElement(By.Id("private-filtered")).Text,
                            Is.EqualTo("ready"));
                    }
                    public void FindBroad() {
                        Assert.That(driver.FindElement(By.Id("private-broad")).Text,
                            Is.EqualTo("ready"));
                    }
                    public void FindLocalFunction() {
                        Assert.That(driver.FindElement(By.Id("private-local")).Text,
                            Is.EqualTo("ready"));
                    }
                    public void FindSafe() {
                        Assert.That(driver.FindElement(By.Id("private-safe")).Text,
                            Is.EqualTo("ready"));
                    }
                    public void FindNoExternal() {
                        Assert.That(driver.FindElement(By.Id("private-orphan")).Text,
                            Is.EqualTo("ready"));
                    }
                    public void WaitViaTestBase() {
                        new WebDriverWait(driver, TimeSpan.FromMilliseconds(80))
                            .Until(d => d.FindElements(By.Id("loader")).Count == 0);
                    }
                    public void WaitIrrelevantCatch() {
                        new WebDriverWait(driver, TimeSpan.FromMilliseconds(80))
                            .Until(d => d.FindElements(By.Id("loader")).Count == 0);
                    }
                    public virtual void FindVirtual() {
                        Assert.That(driver.FindElement(By.Id("private-virtual")).Text,
                            Is.EqualTo("ready"));
                    }
                    public void FindTypedRethrow() {
                        try {
                            Assert.That(driver.FindElement(By.Id("private-inner")).Text,
                                Is.EqualTo("ready"));
                        } catch (NoSuchElementException) { throw; }
                    }
                }
                public sealed class DecoyPage {
                    private readonly IWebDriver driver;
                    public DecoyPage(IWebDriver driver) { this.driver=driver; }
                    public void FindNoExternal() {
                        Assert.That(driver.FindElement(By.Id("private-decoy")).Text,
                            Is.EqualTo("ready"));
                    }
                }
                """);

            File.WriteAllText(Path.Combine(root, "Base.cs"), """
                using OpenQA.Selenium;
                using System;
                namespace Fictional.P39;
                public class TestBase {
                    protected readonly SourcePage page;
                    public TestBase(SourcePage page) { this.page=page; }
                    protected void InvokeFromBase() { page.FindViaTestBase(); }
                    protected void InvokeTimeoutFromBase() { page.WaitViaTestBase(); }
                    protected void InvokeRethrowFromBase() { page.FindTypedRethrow(); }
                }
                """);

            File.WriteAllText(Path.Combine(root, "Tests.cs"), """
                using OpenQA.Selenium;
                using NUnit.Framework;
                using System;
                namespace Fictional.P39;
                [TestFixture]
                public sealed class NUnitTests : TestBase {
                    private readonly DecoyPage decoy;
                    public NUnitTests(SourcePage page, DecoyPage decoy)
                        : base(page) { this.decoy=decoy; }

                    [Test] public void DirectExternalCatch() {
                        try { page.FindDirect(); }
                        catch (NoSuchElementException) { }
                    }
                    [Test] public void CatchThroughTestBase() {
                        try { InvokeFromBase(); }
                        catch (NoSuchElementException) { }
                    }
                    [Test] public void CatchTimeoutThroughTestBase() {
                        try { InvokeTimeoutFromBase(); }
                        catch (WebDriverTimeoutException) { }
                    }
                    [Test] public void CatchFiltered() {
                        try { page.FindFiltered(); }
                        catch (NoSuchElementException error)
                            when (error.Message.Length > 0) { }
                    }
                    [Test] public void CatchBroad() {
                        try { page.FindBroad(); }
                        catch (Exception) { }
                    }
                    [Test] public void NestedLocalFunction() {
                        void Deferred() { page.FindLocalFunction(); }
                        try { Deferred(); }
                        catch (NoSuchElementException) { }
                    }
                    [Test] public void IrrelevantCatch() {
                        try { page.FindSafe(); }
                        catch (InvalidOperationException) { }
                    }
                    [Test] public void UnrelatedExceptionKind() {
                        try { page.WaitIrrelevantCatch(); }
                        catch (NoSuchElementException) { }
                    }
                    [Test] public void OutermostRethrowCatch() {
                        try { InvokeRethrowFromBase(); }
                        catch (NoSuchElementException) { }
                    }
                    [Test] public void SameMethodNameDecoy() {
                        try { decoy.FindNoExternal(); }
                        catch (NoSuchElementException) { }
                    }
                }
                """);

            var engine = new MigrationEngine();
            var captured = new PomSourceInspector().Inspect(root);
            var identity = captured.SourceTreeHash;
            PomActionPlan Plan(string method) => engine.PlanPomActions(root,
                "Fictional.P39.SourcePage", method, identity);

            foreach (var (method, code, origin) in new[] {
                ("FindDirect", "POM_EXTERNAL_NO_SUCH_ELEMENT_CATCH", "Tests.cs"),
                ("FindViaTestBase", "POM_EXTERNAL_NO_SUCH_ELEMENT_CATCH", "Tests.cs"),
                ("WaitViaTestBase", "POM_EXTERNAL_WEBDRIVER_TIMEOUT_CATCH", "Tests.cs"),
                ("FindFiltered", "POM_EXTERNAL_EXCEPTION_FILTER", "Tests.cs"),
                ("FindBroad", "POM_EXTERNAL_EXCEPTION_BROAD_CATCH", "Tests.cs"),
                ("FindLocalFunction", "POM_EXTERNAL_EXCEPTION_LOCAL_FUNCTION", "Tests.cs"),
                ("FindVirtual", "POM_EXTERNAL_EXCEPTION_DYNAMIC_DISPATCH", "Page.cs"),
                ("FindTypedRethrow", "POM_EXTERNAL_NO_SUCH_ELEMENT_CATCH", "Tests.cs")
            })
            {
                var p = Plan(method);
                Check(p.Status == "blocked"
                    && p.TargetStatements.Count == 0
                    && p.AdapterCandidate == null
                    && !p.CanAutoApply && !p.RuntimeVerified
                    && p.SourceMethodSha256.Length == 64
                    && p.SourceTreeHash == identity
                    && p.Blockers.Any(b => b.Code == code
                        && b.SourcePath.EndsWith(origin, StringComparison.Ordinal)
                        && b.SourceLine > 0 && b.SourceSha256.Length == 64),
                    "Exception-sensitive call graph unexpectedly emitted a mapping "
                    + method + ": " + System.Text.Json.JsonSerializer.Serialize(p));
            }
            foreach (var method in new[] {
                "FindSafe", "FindNoExternal", "WaitIrrelevantCatch"
            })
            {
                var p = Plan(method);
                Check(p.Status == "requires_review"
                    && p.TargetStatements.Count == 1
                    && p.AdapterCandidate is { RequiresReview: true }
                    && !p.CanAutoApply && !p.RuntimeVerified
                    && !p.Blockers.Any(b => b.Code.StartsWith(
                        "POM_EXTERNAL_", StringComparison.Ordinal)),
                    "Unrelated call/catch falsely blocked exact source symbol "
                    + method + ": " + System.Text.Json.JsonSerializer.Serialize(p));
            }

            Check(new PomSourceInspector().Inspect(root).SourceTreeHash == identity,
                "Read-only graph inspection mutated a C# source.");
            File.AppendAllText(Path.Combine(root, "Tests.cs"), "\n//stale");
            try { Plan("FindDirect"); throw new Exception("Accepted stale source tree"); }
            catch (InvalidOperationException error) when (
                error.Message.StartsWith("STALE_POM_ACTION_PLAN", StringComparison.Ordinal)) { }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Check(bool yes, string reason)
    {
        if (!yes)
            throw new InvalidOperationException(
                "POM_PREVIEW39_EXCEPTION_CALLGRAPH_AST: " + reason);
    }
}
