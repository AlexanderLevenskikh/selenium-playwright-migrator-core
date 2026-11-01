using Migrator.Application;

internal static class PomPreview41AsyncCountSmoke
{
    private const string Page = """
        using NUnit.Framework;
        using OpenQA.Selenium;
        using OpenQA.Selenium.Support.UI;
        using System;
        namespace Fictional.P41;
        public sealed class SourcePage {
            private readonly IWebDriver driver;
            public SourcePage(IWebDriver driver) { this.driver=driver; }
            public void Missing() {
                try { Assert.That(driver.FindElement(By.Id("missing")).Text, Is.EqualTo("ready")); }
                catch (NoSuchElementException) { throw; }
            }
            public void Timeout() {
                try { new WebDriverWait(driver, TimeSpan.FromMilliseconds(80))
                    .Until(d => d.FindElements(By.Id("loading")).Count == 0); }
                catch (WebDriverTimeoutException) { throw; }
            }
        }
        """;
    private const string Base = """
        namespace Fictional.P41;
        public class TestBase {
            protected readonly SourcePage page;
            public TestBase(SourcePage page) { this.page = page; }
            protected void ForwardMissing() { page.Missing(); }
            protected void ForwardTimeout() { page.Timeout(); }
        }
        """;
    private const string Tests = """
        using NUnit.Framework;
        using OpenQA.Selenium;
        namespace Fictional.P41;
        [TestFixture]
        public sealed class NUnitTests : TestBase {
            private readonly IWebDriver driver;
            private readonly Microsoft.Playwright.IPage Page;
            public NUnitTests(SourcePage sourcePage, IWebDriver driver,
                Microsoft.Playwright.IPage explicitTargetPage):base(sourcePage) {
                this.driver = driver;
                this.Page = explicitTargetPage;
            }
            [Test] public void CatchMissing() {
                try { ForwardMissing(); }
                catch (NoSuchElementException) {
                    Assert.That(driver.FindElements(By.Id("missing")).Count, Is.EqualTo(0));
                }
            }
            [Test] public void CatchTimeout() {
                try { ForwardTimeout(); }
                catch (WebDriverTimeoutException) {
                    Assert.That(driver.FindElements(By.Id("loading")).Count, Is.EqualTo(1));
                }
            }
        }
        """;

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "fictional-p41-"+
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            void Write(string outer) {
                File.WriteAllText(Path.Combine(root,"Page.cs"),Page);
                File.WriteAllText(Path.Combine(root,"TestBase.cs"),Base);
                File.WriteAllText(Path.Combine(root,"NUnitTests.cs"),outer);
            }
            Write(Tests);
            var engine = new MigrationEngine();
            string Sha() => new PomSourceInspector().Inspect(root).SourceTreeHash;
            var sourceHash = Sha();
            PomExternalExceptionContractPlan Plan(string method,
                string receiver = "Page") =>
                engine.PlanPomExternalAsyncCountContract(root,
                    "Fictional.P41.SourcePage", method, Sha(), receiver);

            foreach (var (method,expected) in new[] {
                ("Missing","POM_SOURCE_ELEMENT_ABSENT"),
                ("Timeout","System.TimeoutException")
            })
            {
                var p = Plan(method);
                Check(p.Status=="requires_review"
                    && p.SchemaVersion=="migrator-pom-external-async-count/v1"
                    && p.CatchCandidates.Count==1 && p.AsyncMethods.Count==3
                    && p.VerifiedPageReceiver=="Page"
                    && p.AsyncMethods.All(e => e.SourceLine>0
                        && e.SourceSha256.Length==64
                        && e.TargetSignature.Contains("async System.Threading.Tasks.Task",
                            StringComparison.Ordinal)
                        && !e.TargetBody.Contains("OpenQA.Selenium",StringComparison.Ordinal))
                    && p.AsyncMethods[0].SourcePath=="Page.cs"
                    && p.AsyncMethods[1].SourcePath=="TestBase.cs"
                    && p.AsyncMethods[2].SourcePath=="NUnitTests.cs"
                    && p.AsyncMethods[2].TargetBody.Contains("await Forward",StringComparison.Ordinal)
                    && p.AsyncMethods[2].TargetBody.Contains("await Page.Locator(",StringComparison.Ordinal)
                    && p.CatchCandidates[0].TargetCatchClause.Contains(expected,StringComparison.Ordinal)
                    && !p.RuntimeVerified && !p.CanAutoApply && p.RequiresReview,
                    "Positive typed Count + NUnit/TestBase async proof failed: "+method+" "+
                    System.Text.Json.JsonSerializer.Serialize(p));
                var historical=engine.PlanPomExternalExceptionContract(
                    root,"Fictional.P41.SourcePage",method,sourceHash);
                Check(historical.Status=="blocked"
                    && historical.CatchCandidates.Count==0,
                    "preview.40 historical blocked Count handler was weakened");
            }
            void Block(string changed, string method="Missing", string receiver="Page") {
                Write(changed);
                var p=Plan(method,receiver);
                Check(p.Status=="blocked" && p.CatchCandidates.Count==0
                    && p.AsyncMethods.Count==0 && p.Blockers.Count>0
                    && !p.CanAutoApply,
                    "Unproven async receiver/flow emitted candidate: "+
                    System.Text.Json.JsonSerializer.Serialize(p));
            }
            Block(Tests,"Missing","GuessedPage");
            Block(Tests.Replace(
                "private readonly Microsoft.Playwright.IPage Page;",
                "private readonly object Page;"));
            Block(Tests.Replace("this.Page = explicitTargetPage;",
                "this.Page = null!;"));
            Block(Tests.Replace("this.Page = explicitTargetPage;",
                "this.Page = explicitTargetPage; ForwardMissing();"));
            Block(Tests.Replace("[Test] public void CatchMissing()",
                "public void CatchMissing()"));
            Block(Tests.Replace("Is.EqualTo(0)","Is.GreaterThan(0)"));
            Block(Tests.Replace("catch (NoSuchElementException) {",
                "catch (NoSuchElementException ex) when(ex.Message.Length > 0) {"));
            Block(Tests.Replace("try { ForwardMissing(); }",
                "try { ForwardMissing(); ForwardTimeout(); }"));
            Block(Tests.Replace(
                "    [Test] public void CatchTimeout() {",
                "    [Test] public void Extra() { try { ForwardMissing(); }"+
                " catch (NoSuchElementException) { } }\n"+
                "    [Test] public void CatchTimeout() {"));
            Write(Tests);
            Check(Sha()==sourceHash,"Source bytes changed during review-only planning");
            File.AppendAllText(Path.Combine(root,"NUnitTests.cs"),"\n//stale");
            try {
                engine.PlanPomExternalAsyncCountContract(root,
                    "Fictional.P41.SourcePage","Missing",sourceHash,"Page");
                throw new InvalidOperationException("STALE_SOURCE_WAS_ACCEPTED");
            } catch(InvalidOperationException ex) when(
                ex.Message.StartsWith("STALE_POM_EXCEPTION_CONTRACT",
                    StringComparison.Ordinal)) { }
        }
        finally { Directory.Delete(root,recursive:true); }
    }
    private static void Check(bool ok, string error)
    {
        if (!ok) throw new InvalidOperationException(
            "POM_PREVIEW41_ASYNC_COUNT: "+error);
    }
}
