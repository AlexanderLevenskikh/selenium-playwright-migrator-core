using Migrator.Application;

internal static class PomPreview35ImmediateAssertionSmoke
{
    public static void Run()
    {
        var root=Path.Combine(Path.GetTempPath(),"fictional-p35-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root,"Page.cs"),"""
                using NUnit.Framework;
                using OpenQA.Selenium;
                using System;
                namespace Fictional.P35;
                public sealed class Page {
                    private readonly IWebDriver driver;
                    public Page(IWebDriver driver) { this.driver=driver; }
                    public void CountEqual() {
                        Assert.That(driver.FindElements(By.Id("row")).Count,Is.EqualTo(2));
                    }
                    public void CountMore() {
                        Assert.That(driver.FindElements(By.Id("row")).Count,Is.GreaterThan(1));
                    }
                    public void CountLess() {
                        Assert.That(driver.FindElements(By.Id("row")).Count,Is.LessThan(3));
                    }
                    public void VisibleText(string expected) {
                        Assert.That(driver.FindElement(By.Id("selected")).Text,Is.EqualTo(expected));
                    }
                    public void IndexedText(string expected) {
                        Assert.That(driver.FindElements(By.CssSelector(".cell"))[0].Text,Is.EqualTo(expected));
                    }
                    public void UnsafeDynamicText() {
                        Assert.That(driver.FindElement(By.Id("selected")).Text,
                            Is.EqualTo(Environment.GetEnvironmentVariable("PRIVATE")!));
                    }
                    public void UnsafeComputedCount() {
                        Assert.That(driver.FindElements(By.Id("row")).Count,
                            Is.EqualTo(Environment.TickCount));
                    }
                }
                """);
            var hash=new PomSourceInspector().Inspect(root).SourceTreeHash;
            var engine=new MigrationEngine();
            PomActionPlan Plan(string method)=>engine.PlanPomActions(root,"Fictional.P35.Page",method,hash);
            foreach(var (name,clause) in new[] {
                ("CountEqual","EqualTo(2)"),("CountMore","GreaterThan(1)"),("CountLess","LessThan(3)")
            })
            {
                var p=Plan(name);
                Check(p.Status=="requires_review" && !p.RuntimeVerified &&
                    !p.CanAutoApply && p.Blockers.Count==0 && p.TargetStatements.Count==1 &&
                    p.TargetStatements.Single().Contains(".CountAsync(), NUnit.Framework.Is."+clause) &&
                    !p.TargetStatements.Single().Contains("ToHaveCountAsync") &&
                    p.AdapterCandidate is { RequiresReview:true },name+" must take one snapshot.");
            }
            foreach(var name in new[]{"VisibleText","IndexedText"})
            {
                var p=Plan(name);
                Check(p.Status=="requires_review" && !p.RuntimeVerified && !p.CanAutoApply &&
                    p.Blockers.Count==0 && p.Actions.Single().Effect=="text_assertion" &&
                    p.TargetStatements.Single().Contains(".EvaluateAllAsync<string>(") &&
                    p.TargetStatements.Single().Contains("POM_SOURCE_ELEMENT_ABSENT") &&
                    p.TargetStatements.Single().Contains("NUnit.Framework.Is.EqualTo(expected)") &&
                    !p.TargetStatements.Single().Contains("ToHaveTextAsync") &&
                    !p.TargetStatements.Single().Contains("InnerTextAsync") &&
                    p.AdapterCandidate is { RequiresReview:true } &&
                    p.AdapterCandidate.TargetStatementTemplates.Single().Contains("{expected}") &&
                    p.Actions.Single().SourceSha256.Length==64, name+" must snapshot DOM once.");
            }
            Check(Plan("IndexedText").TargetStatements.Single().Contains(".Nth(0).EvaluateAllAsync<string>"),
                "Source literal row index was not preserved.");
            foreach(var name in new[]{"UnsafeDynamicText","UnsafeComputedCount"})
            {
                var p=Plan(name);
                Check(p.Status=="blocked" && p.Blockers.Count>0 &&
                      p.TargetStatements.Count==0 && p.AdapterCandidate==null,
                      "Unsafe expression became an executable candidate: "+name);
            }
            File.AppendAllText(Path.Combine(root,"Page.cs"),"\n// stale");
            try {Plan("CountEqual");throw new Exception("Accepted stale hash");}
            catch(InvalidOperationException e) when (e.Message.StartsWith("STALE_POM_ACTION_PLAN")) {}
        }
        finally{Directory.Delete(root,true);}
    }
    private static void Check(bool ok,string reason)
    {
        if(!ok)throw new InvalidOperationException("POM_PREVIEW35_IMMEDIATE_AST: "+reason);
    }
}
