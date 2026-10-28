using Migrator.Application;
using Migrator.Core;
using Migrator.Core.Models;
using Migrator.PlaywrightDotNet;
using Migrator.Roslyn;
using Migrator.SeleniumCSharp;

internal static class PomNavigationAndStorageSmoke
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "migrator-pom-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "ProjectFixture.cs");
            File.WriteAllText(file, """
                using NUnit.Framework;
                [TestFixture] public class ProjectFixture {
                    [SetUp] public void SetUp() { LocalStorage.Clear(); }
                    [Test] public void ProjectNavigation() {
                        var page = Navigation.OpenDashboardPage();
                        page.Button.Click();
                    }
                    [Test] public void BrowserStorage() {
                        LocalStorage.SetItem("k", "v");
                    }
                }
                """);

            var noConfig = new ProjectAdapterConfig { SourceProjectName = "Synthetic" };
            var parser = new RoslynTestFileParser(noConfig);
            var source = parser.Parse(file);
            var method = source.Tests.First(t => t.Name == "ProjectNavigation")
                .BodyActions.OfType<MethodInvocationAction>().FirstOrDefault();
            Check(method is { ReceiverExpression: "Navigation", MethodName: "OpenDashboardPage", ResultVariable: "page" },
                "project navigation must retain receiver, method and result binding");

            var engine = new MigrationEngine();
            var analysis = engine.Analyze(file);
            var preview = engine.Preview(file, expectedAnalysisId: analysis.AnalysisId);
            Check(preview.Files.Count == 1, "one target file");
            Check(preview.TransformationStatus == "partial", "incomplete POM/storage never fully converted");
            Check(preview.Findings.Any(f => f.Code == "POM_NAVIGATION_REQUIRES_MAPPING" && f.Line > 0),
                "source-lined POM finding missing");
            Check(preview.Findings.Any(f => f.Code == "LOCAL_STORAGE_REQUIRES_MAPPING" && f.Line > 0),
                "source-lined LocalStorage finding missing");
            var generated = preview.Files.Single().Content;
            Check(generated.Contains("[MIGRATOR:POM_NAVIGATION_REQUIRES_MAPPING]", StringComparison.Ordinal),
                "POM TODO missing");
            Check(generated.Contains("[MIGRATOR:LOCAL_STORAGE_REQUIRES_MAPPING]", StringComparison.Ordinal),
                "storage TODO missing");
            Check(generated.Contains("[MIGRATOR:UNRESOLVED_SYMBOL]", StringComparison.Ordinal),
                "dependent source Page Object accesses should remain blocked");
            Check(preview.Files.Single().FullyConvertedTests == 0,
                "unmapped POM/storage cannot be marked fully converted");

            // Target navigation statement is explicit evidence, not a license
            // to invent a Page Object alias. The nav itself should become active
            // while its dependent source Page Object remains blocked.
            var mapping = new ProjectAdapterConfig
            {
                SourceProjectName = "Synthetic",
                Methods = new[]
                {
                    new MethodMapping
                    {
                        SourceMethod = "Navigation.OpenDashboardPage()",
                        TargetStatements = new[] { "await Page.GotoAsync(\"https://example.invalid/dashboard\");" },
                        RequiresReview = true
                    }
                }
            };
            var configured = new MigrationPipeline(
                new RoslynTestFileParser(mapping), new PlaywrightDotNetRenderer(),
                new DefaultProjectAdapter(mapping)).ProcessFile(file);
            Check(configured.GeneratedOutput.Contains("await Page.GotoAsync(\"https://example.invalid/dashboard\");",
                StringComparison.Ordinal), "explicit Navigation mapping was not rendered");
            Check(configured.GeneratedOutput.Contains("[MIGRATOR:UNRESOLVED_SYMBOL]", StringComparison.Ordinal),
                "mapping without POM alias must not revive source POM dependent calls");
            Check(configured.Report.FullyConvertedTests == 0, "unmapped source Page Object semantics still partial");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    static void Check(bool ok, string why)
    {
        if (!ok) throw new InvalidOperationException("Synthetic POM navigation smoke: " + why);
    }
}
