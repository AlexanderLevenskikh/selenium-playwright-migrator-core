using Migrator.Application;

internal static class PomPreview40ExternalContractSmoke
{
    public static void Run()
    {
        var root=Path.Combine(Path.GetTempPath(),"fictional-p40-contract-"+
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root,"Page.cs"), """
                using NUnit.Framework;
                using OpenQA.Selenium;
                using OpenQA.Selenium.Support.UI;
                using System;
                namespace Fictional.P40;
                public sealed class SourcePage {
                    private readonly IWebDriver driver;
                    public SourcePage(IWebDriver driver) {this.driver=driver;}
                    public void MissingEmpty() {
                        try { Assert.That(driver.FindElement(By.Id("missing")).Text, Is.EqualTo("ready")); }
                        catch (NoSuchElementException) { throw; }
                    }
                    public void MissingCount() {
                        try { Assert.That(driver.FindElement(By.Id("missing2")).Text, Is.EqualTo("ready")); }
                        catch (NoSuchElementException) { throw; }
                    }
                    public void MissingFilter() {
                        try { Assert.That(driver.FindElement(By.Id("filtered")).Text, Is.EqualTo("ready")); }
                        catch (NoSuchElementException) { throw; }
                    }
                    public void MissingBroad() {
                        try { Assert.That(driver.FindElement(By.Id("broad")).Text, Is.EqualTo("ready")); }
                        catch (NoSuchElementException) { throw; }
                    }
                    public void MissingRethrow() {
                        try { Assert.That(driver.FindElement(By.Id("rethrow")).Text, Is.EqualTo("ready")); }
                        catch (NoSuchElementException) { throw; }
                    }
                    public void MissingUsedVariable() {
                        try { Assert.That(driver.FindElement(By.Id("used")).Text, Is.EqualTo("ready")); }
                        catch (NoSuchElementException) { throw; }
                    }
                    public void MissingFinally() {
                        try { Assert.That(driver.FindElement(By.Id("finally")).Text, Is.EqualTo("ready")); }
                        catch (NoSuchElementException) { throw; }
                    }
                    public void MissingExtraEffect() {
                        try { Assert.That(driver.FindElement(By.Id("extra")).Text, Is.EqualTo("ready")); }
                        catch (NoSuchElementException) { throw; }
                    }
                    public void MissingTwoPaths() {
                        try { Assert.That(driver.FindElement(By.Id("multi")).Text, Is.EqualTo("ready")); }
                        catch (NoSuchElementException) { throw; }
                    }
                    public void MissingNoCaller() {
                        try { Assert.That(driver.FindElement(By.Id("orphan")).Text, Is.EqualTo("ready")); }
                        catch (NoSuchElementException) { throw; }
                    }
                    public void MissingWrongType() {
                        try { Assert.That(driver.FindElement(By.Id("wrong")).Text, Is.EqualTo("ready")); }
                        catch (NoSuchElementException) { throw; }
                    }
                    public void MissingLocalFunction() {
                        try { Assert.That(driver.FindElement(By.Id("local")).Text, Is.EqualTo("ready")); }
                        catch (NoSuchElementException) { throw; }
                    }
                    public void TimeoutEmpty() {
                        try {
                            new WebDriverWait(driver, TimeSpan.FromMilliseconds(80))
                                .Until(d => d.FindElements(By.Id("loader")).Count == 0);
                        } catch (WebDriverTimeoutException) {throw;}
                    }
                    public void TimeoutCount() {
                        try {
                            new WebDriverWait(driver, TimeSpan.FromMilliseconds(80))
                                .Until(d => d.FindElements(By.Id("loader2")).Count == 0);
                        } catch (WebDriverTimeoutException) {throw;}
                    }
                    public void TimeoutFilter() {
                        try {
                            new WebDriverWait(driver, TimeSpan.FromMilliseconds(80))
                                .Until(d => d.FindElements(By.Id("loader3")).Count == 0);
                        } catch (WebDriverTimeoutException) {throw;}
                    }
                }
                """);
            File.WriteAllText(Path.Combine(root,"TestBase.cs"), """
                namespace Fictional.P40;
                public class TestBase {
                    protected readonly SourcePage page;
                    public TestBase(SourcePage page) { this.page=page; }
                    protected void ForwardMissing() { page.MissingEmpty(); }
                    protected void ForwardCount() { page.MissingCount(); }
                    protected void ForwardTimeout() { page.TimeoutEmpty(); }
                    protected void ForwardTimeoutCount() { page.TimeoutCount(); }
                }
                """);
            File.WriteAllText(Path.Combine(root,"NUnitTests.cs"), """
                using System;
                using NUnit.Framework;
                using OpenQA.Selenium;
                namespace Fictional.P40;
                [TestFixture]
                public sealed class NUnitTests : TestBase {
                    private readonly IWebDriver driver;
                    public NUnitTests(SourcePage page,IWebDriver driver) : base(page) {
                        this.driver=driver;
                    }
                    [Test] public void SafeMissing() {
                        try { ForwardMissing(); }
                        catch (NoSuchElementException) { }
                    }
                    [Test] public void SafeMissingCount() {
                        try { ForwardCount(); }
                        catch (NoSuchElementException) {
                            Assert.That(driver.FindElements(By.Id("missing2")).Count, Is.EqualTo(0));
                        }
                    }
                    [Test] public void SafeTimeout() {
                        try { ForwardTimeout(); }
                        catch (WebDriverTimeoutException) { }
                    }
                    [Test] public void SafeTimeoutCount() {
                        try { ForwardTimeoutCount(); }
                        catch (WebDriverTimeoutException) {
                            Assert.That(driver.FindElements(By.Id("loader2")).Count,Is.EqualTo(1));
                        }
                    }
                    [Test] public void Filtered() {
                        try { page.MissingFilter(); }
                        catch (NoSuchElementException ex) when (ex.Message.Length > 0) { }
                    }
                    [Test] public void Broad() {
                        try { page.MissingBroad(); }
                        catch (Exception) { }
                    }
                    [Test] public void Rethrow() {
                        try { page.MissingRethrow(); }
                        catch (NoSuchElementException) { throw; }
                    }
                    [Test] public void UsedVariable() {
                        try { page.MissingUsedVariable(); }
                        catch (NoSuchElementException ex) {
                            Assert.That(ex.Message, Is.EqualTo("some message"));
                        }
                    }
                    [Test] public void Finally() {
                        try { page.MissingFinally(); }
                        catch (NoSuchElementException) { }
                        finally { Assert.That(true, Is.EqualTo(true)); }
                    }
                    [Test] public void ExtraEffect() {
                        try { page.MissingExtraEffect(); Assert.That(true, Is.EqualTo(true)); }
                        catch (NoSuchElementException) { }
                    }
                    [Test] public void MultiplePath1() {
                        try { page.MissingTwoPaths(); }
                        catch (NoSuchElementException) { }
                    }
                    [Test] public void MultiplePath2() {
                        try { page.MissingTwoPaths(); }
                        catch (NoSuchElementException) { }
                    }
                    [Test] public void WrongType() {
                        try { page.MissingWrongType(); }
                        catch (WebDriverTimeoutException) { }
                    }
                    [Test] public void LocalFunction() {
                        void Deferred() {page.MissingLocalFunction();}
                        try {Deferred();}
                        catch (NoSuchElementException) { }
                    }
                    [Test] public void FilteredTimeout() {
                        try { page.TimeoutFilter(); }
                        catch (WebDriverTimeoutException ex) when(ex.Message.Length > 0) { }
                    }
                }
                """);
            var hash=new PomSourceInspector().Inspect(root).SourceTreeHash;
            var engine=new MigrationEngine();
            PomExternalExceptionContractPlan Plan(string method) =>
                engine.PlanPomExternalExceptionContract(root,
                    "Fictional.P40.SourcePage", method, hash);

            foreach(var (method,sourceType,targetType,fragment) in new[] {
                ("MissingEmpty","OpenQA.Selenium.NoSuchElementException",
                    "Microsoft.Playwright.PlaywrightException","POM_SOURCE_ELEMENT_ABSENT"),
                ("MissingCount","OpenQA.Selenium.NoSuchElementException",
                    "Microsoft.Playwright.PlaywrightException",".CountAsync(), NUnit.Framework.Is.EqualTo(0)"),
                ("TimeoutEmpty","OpenQA.Selenium.WebDriverTimeoutException",
                    "System.TimeoutException","catch (System.TimeoutException)"),
                ("TimeoutCount","OpenQA.Selenium.WebDriverTimeoutException",
                    "System.TimeoutException",".CountAsync(), NUnit.Framework.Is.EqualTo(1)")
            })
            {
                var p=Plan(method);
                var c=p.CatchCandidates.SingleOrDefault();
                Check(p.Status=="requires_review" && !p.RuntimeVerified
                    && !p.CanAutoApply && p.RequiresReview
                    && p.Execution=="read_only_not_run"
                    && p.Blockers.Count==0 && c!=null
                    && c.SourceCaughtType==sourceType
                    && c.TargetCaughtType==targetType
                    && c.CatchSourcePath=="NUnitTests.cs"
                    && c.CatchSourceLine>0
                    && c.CatchSourceSha256.Length==64
                    && c.TargetCatchSha256.Length==64
                    && p.SourceMethodSha256.Length==64
                    && c.CallerChain.Contains("SourcePage",StringComparison.Ordinal)
                    && c.CallerChain.Contains("NUnitTests",StringComparison.Ordinal)
                    && c.TargetCatchClause.Contains(fragment,StringComparison.Ordinal)
                    && !c.TargetCatchClause.Contains("OpenQA.Selenium",StringComparison.Ordinal)
                    && !c.TargetCatchClause.Contains("throw;",StringComparison.Ordinal),
                    "Exact external catch clause missing or unapproved: "+
                    method+" "+System.Text.Json.JsonSerializer.Serialize(p));
            }
            foreach(var method in new[] {
                "MissingFilter","MissingBroad","MissingRethrow",
                "MissingUsedVariable","MissingFinally","MissingExtraEffect",
                "MissingTwoPaths","MissingNoCaller","MissingWrongType",
                "MissingLocalFunction","TimeoutFilter"
            })
            {
                var p=Plan(method);
                Check(p.Status=="blocked" && p.CatchCandidates.Count==0
                    && p.Blockers.Count>0 && !p.RuntimeVerified && !p.CanAutoApply
                    && p.Blockers.All(b => b.Code.StartsWith("POM_EXTERNAL_CONTRACT_",
                        StringComparison.Ordinal)),
                    "Unsupported or ambiguous external contract emitted candidate: "+
                    method+" "+System.Text.Json.JsonSerializer.Serialize(p));
            }
            Check(new PomSourceInspector().Inspect(root).SourceTreeHash==hash,
                "Read-only contract planning modified source bytes.");
            File.AppendAllText(Path.Combine(root,"NUnitTests.cs"),"\n//stale");
            try {
                Plan("MissingEmpty");
                throw new Exception("Accepted stale hash");
            }
            catch (InvalidOperationException e) when (
                e.Message.StartsWith("STALE_POM_EXCEPTION_CONTRACT",StringComparison.Ordinal)) { }
        }
        finally { Directory.Delete(root,recursive:true); }
    }

    private static void Check(bool yes,string description)
    {
        if(!yes) throw new InvalidOperationException(
            "POM_PREVIEW40_EXTERNAL_CONTRACT_AST: "+description);
    }
}
