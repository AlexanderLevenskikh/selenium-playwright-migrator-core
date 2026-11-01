using Migrator.Application;

internal static class PomPreview42FixtureLifecycleSmoke
{
    internal const string Page = """
        using System;
        using NUnit.Framework;
        using OpenQA.Selenium;
        using OpenQA.Selenium.Support.UI;
        using Microsoft.Playwright;
        namespace Fictional.P42;
        public sealed class SourcePage {
            private readonly IWebDriver driver;
            private readonly IPage Page;
            private readonly IBrowserContext Context;
            public SourcePage(IWebDriver driver,IPage Page,IBrowserContext Context) {
                this.driver=driver;
                this.Page=Page;
                this.Context=Context;
            }
            public void Missing() {
                try { Assert.That(driver.FindElement(By.Id("never-created")).Text,Is.EqualTo("ready")); }
                catch (NoSuchElementException) { throw; }
            }
            public void Timeout() {
                try {
                    new WebDriverWait(driver,TimeSpan.FromMilliseconds(80))
                        .Until(d=>d.FindElements(By.Id("loader")).Count==0);
                } catch (WebDriverTimeoutException) { throw; }
            }
        }
        """;
    internal const string Base = """
        namespace Fictional.P42;
        public class TestBase {
            protected SourcePage page = null!;
            protected void ForwardMissing(){page.Missing();}
            protected void ForwardTimeout(){page.Timeout();}
        }
        """;
    internal const string Fixture = """
        using System;
        using System.Threading.Tasks;
        using NUnit.Framework;
        using OpenQA.Selenium;
        using Microsoft.Playwright;
        namespace Fictional.P42;
        [TestFixture] public sealed class NUnitFixture:TestBase {
            private readonly IWebDriver driver = null!;
            private IPlaywright PW = null!;
            private IBrowser Browser = null!;
            private IBrowserContext Context = null!;
            private IPage Page = null!;
            [OneTimeSetUp] public async Task StartAll() {
                PW = await Microsoft.Playwright.Playwright.CreateAsync();
                Browser = await PW.Chromium.LaunchAsync(new BrowserTypeLaunchOptions {
                    Headless = true
                });
            }
            [SetUp] public async Task StartEach() {
                Context = await Browser.NewContextAsync();
                Page = await Context.NewPageAsync();
                page = new SourcePage(driver,Page,Context);
            }
            [TearDown] public async Task StopEach() {
                try { await Page.CloseAsync(); }
                finally { await Context.CloseAsync(); }
            }
            [OneTimeTearDown] public async Task StopAll() {
                try { await Browser.CloseAsync(); }
                finally { PW.Dispose(); }
            }
            [Test] public void CatchMissing() {
                try { ForwardMissing(); }
                catch(NoSuchElementException) {
                    Assert.That(driver.FindElements(By.Id("never-created")).Count,Is.EqualTo(0));
                }
            }
            [Test] public void CatchTimeout() {
                try { ForwardTimeout(); }
                catch(WebDriverTimeoutException) {
                    Assert.That(driver.FindElements(By.Id("loader")).Count,Is.EqualTo(1));
                }
            }
        }
        """;

    public static void Run()
    {
        var path = Path.Combine(Path.GetTempPath(),
            "fictional-p42-lifecycle-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            void Write(string page = Page, string helper = Base, string fixture = Fixture)
            {
                File.WriteAllText(Path.Combine(path,"Page.cs"), page);
                File.WriteAllText(Path.Combine(path,"TestBase.cs"), helper);
                File.WriteAllText(Path.Combine(path,"NUnitFixture.cs"), fixture);
            }
            var engine = new MigrationEngine();
            string Sha() => new PomSourceInspector().Inspect(path).SourceTreeHash;
            PomExternalExceptionContractPlan Plan(string method, string hash,
                string pageName = "Page", string contextName = "Context",
                string browserName = "Browser", string playwrightName = "PW")
                => engine.PlanPomExternalAsyncLifecycleCountContract(path,
                    "Fictional.P42.SourcePage",method,hash,
                    pageName,contextName,browserName,playwrightName);
            void Check(bool ok, string msg)
            {
                if (!ok) throw new InvalidOperationException(
                    "POM_PREVIEW42_LIFECYCLE: "+msg);
            }
            Write();
            var firstHash=Sha();
            foreach (var method in new[] {"Missing","Timeout"})
            {
                var plan=Plan(method,firstHash);
                var lifecycle=plan.FixtureLifecycle;
                Check(plan.Status=="requires_review" &&
                    plan.SchemaVersion=="migrator-pom-external-async-lifecycle-count/v1"
                    && plan.SourceTreeHash==firstHash
                    && plan.CatchCandidates.Count==1
                    && plan.AsyncMethods.Count==3
                    && plan.VerifiedPageReceiver=="Page"
                    && lifecycle!=null && lifecycle.FixtureSourcePath=="NUnitFixture.cs"
                    && lifecycle.PageConstructorSha256.Length==64
                    && lifecycle.OneTimeSetUpSha256.Length==64
                    && lifecycle.SetUpSha256.Length==64
                    && lifecycle.TearDownSha256.Length==64
                    && lifecycle.OneTimeTearDownSha256.Length==64
                    && lifecycle.ContextFromBrowser && lifecycle.PageFromContext
                    && lifecycle.PageAndContextConstructorInjected
                    && lifecycle.PerTestCleanupInFinally
                    && lifecycle.OneTimeCleanupInFinally
                    && plan.AsyncMethods[2].TargetBody.Contains(
                        "await Page.Locator(",StringComparison.Ordinal)
                    && !plan.CanAutoApply && !plan.RuntimeVerified && plan.RequiresReview,
                    "Exact fixture receiver flow and finally cleanup not proven "+
                        method+" "+System.Text.Json.JsonSerializer.Serialize(plan));
                Check(engine.PlanPomExternalExceptionContract(path,
                    "Fictional.P42.SourcePage",method,firstHash).Status=="blocked",
                    "Historical preview.40 changed");
                Check(engine.PlanPomExternalAsyncCountContract(path,
                    "Fictional.P42.SourcePage",method,firstHash,"Page").Status=="blocked",
                    "Historical preview.41 constructor-only contract changed");
            }
            void Negative(string editedFixture, string editedPage = Page,
                string editedBase = Base, string receiver = "Page")
            {
                Write(editedPage,editedBase,editedFixture);
                var plan=Plan("Missing",Sha(),receiver);
                Check(plan.Status=="blocked" && plan.FixtureLifecycle==null
                    && plan.CatchCandidates.Count==0
                    && plan.AsyncMethods.Count==0 && plan.Blockers.Count>0
                    && !plan.CanAutoApply,
                    "Unknown lifecycle path emitted partial target "+
                        System.Text.Json.JsonSerializer.Serialize(plan));
            }
            Negative(Fixture.Replace("Page = await Context.NewPageAsync();",
                "Page = null!;"));
            Negative(Fixture.Replace("page = new SourcePage(driver,Page,Context);",
                "page = new SourcePage(driver,Page,null!);"));
            Negative(Fixture.Replace("finally { await Context.CloseAsync(); }",
                "finally { }"));
            Negative(Fixture.Replace("finally { await Context.CloseAsync(); }",
                "catch (Exception) { }"));
            Negative(Fixture.Replace("finally { PW.Dispose(); }",
                "finally { }"));
            Negative(Fixture.Replace("private IPage Page = null!;",
                "private object Page = null!;"));
            Negative(Fixture.Replace("[SetUp] public async Task StartEach()",
                "public async Task StartEach()"));
            Negative(Fixture.Replace("[Test] public void CatchMissing()",
                "public void CatchMissing()"));
            Negative(Fixture.Replace("Page = await Context.NewPageAsync();",
                "Page = await Context.NewPageAsync(); Page = await Context.NewPageAsync();"));
            Negative(Fixture.Replace(
                "page = new SourcePage(driver,Page,Context);",
                "page = new SourcePage(driver,Page,Context); page = new SourcePage(driver,Page,Context);"));
            Negative(Fixture.Replace(
                "page = new SourcePage(driver,Page,Context);",
                "page = new SourcePage(driver,Page,Context); _ = new SourcePage(driver,Page,Context);"));
            Negative(Fixture.Replace(
                "Page = await Context.NewPageAsync();",
                "Page = await Context.NewPageAsync(); await Context.CloseAsync();"));
            Negative(Fixture, Page.Replace("this.Context=Context;",
                "this.Context=Context; this.Context=Context;"));
            Negative(Fixture, Page.Replace("this.Context=Context;",
                "this.Context=null!;"));
            Negative(Fixture, Base.Replace(
                "protected void ForwardMissing(){page.Missing();}",
                "protected void ForwardMissing(){page.Missing(); page.Timeout();}"));
            Negative(Fixture,"",Base);
            Write();
            Check(Sha()==firstHash,"Planner modified captured source tree");
            File.AppendAllText(Path.Combine(path,"NUnitFixture.cs"),"\n//changed");
            try
            {
                Plan("Missing",firstHash);
                throw new InvalidOperationException("STALE_SOURCE_ACCEPTED");
            }
            catch (InvalidOperationException ex) when (
                ex.Message.StartsWith("STALE_POM_EXCEPTION_CONTRACT",
                    StringComparison.Ordinal)) { }
        }
        finally { Directory.Delete(path,true); }
    }
}