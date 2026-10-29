using Migrator.Application;

internal static class NUnitLifecycleSourceSmoke
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "nunit-lifecycle-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "Synthetic.csproj"),
                """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
            File.WriteAllText(Path.Combine(root, "FixtureBase.cs"), """
                using NUnit.Framework;
                namespace Fictional.App;
                public abstract class FixtureBase
                {
                    protected IWebDriver WebDriver =>
                        DiContainer.GetScopedRequiredService<IPooledWebDriver>().WrappedDriver;
                    protected Navigation Navigation =>
                        DiContainer.GetScopedRequiredService<Navigation>();
                    protected LocalStorage LocalStorage =>
                        DiContainer.GetScopedRequiredService<LocalStorage>();
                    private Screenshoter Screenshoter =>
                        DiContainer.GetScopedRequiredService<Screenshoter>();

                    [OneTimeSetUp] public void BeforeSuite() {
                        SetCustomListener();
                    }
                    [TearDown] public void AfterTest() {
                        TestContext.CurrentContext.WriteBrowserLogs(WebDriver);
                        Screenshoter.CreateAndSaveScreenshot();
                    }
                    [OneTimeTearDown] public void AfterSuite() {
                        DiContainer.DisposeScope();
                    }
                    private void SetCustomListener() {
                        var listener = new RetryTestListener();
                        TestExecutionContext.CurrentContext.GetType()
                            .GetProperties().Single(x => x.Name == "Listener");
                    }

                    [SetUpFixture] public class InvalidNestedCleanup {
                        [OneTimeTearDown] public void Stop() {
                            DiContainer.FullDispose();
                        }
                    }
                }
                """);
            File.WriteAllText(Path.Combine(root, "DerivedTests.cs"), """
                using NUnit.Framework;
                namespace Fictional.App;
                [TestFixture] public sealed class DerivedTests : FixtureBase {
                    [Test] public void A() {}
                }
                """);

            var inspector = new MigrationEngine();
            var report = inspector.InspectNUnitLifecycle(root);
            Check(report.SchemaVersion == "migrator-nunit-lifecycle-source/v1alpha1"
                && report.SourceTreeHash.Length == 64
                && report.InspectedFiles == 2
                && report.DeclaredTestMethods == 1
                && report.Status == "blocked", "source scope identity + test count");
            Check(report.Hooks.Count == 4
                && report.Hooks.Count(h => h.Kind == "OneTimeTearDown") == 2
                && report.Hooks.All(h => h.MethodSha256.Length == 64),
                "once/test/cleanup hook source-line and bytes");
            Check(report.SetUpFixtures.Single().CleanupOnly
                && report.SetUpFixtures.Single().ContainingType
                    == "Fictional.App.FixtureBase"
                && report.Issues.Any(issue =>
                    issue.Code == "NUNIT_NESTED_SETUPFIXTURE_REQUIRES_REVIEW"
                    && issue.SourceLine > 0),
                "nested cleanup-only SetUpFixture must not be hoisted");
            var kinds = report.Effects.Select(effect => effect.Kind)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var required in new[]
            {
                "di_scoped_service", "di_scope_dispose", "di_global_dispose",
                "browser_logs", "browser_screenshot", "browser_driver_scope",
                "browser_navigation_scope", "browser_storage_scope",
                "nunit_listener_reflection"
            })
                Check(kinds.Contains(required), "source obligation missing: " + required);
            Check(report.Effects.Any(effect => effect.Kind ==
                    "nunit_listener_reflection"
                    && effect.ReachedThrough == "SetCustomListener"),
                "reachable private listener helper evidence");
            Check(report.Effects.All(effect =>
                    effect.ExpressionSha256.Length == 64 &&
                    effect.SourceLine > 0 && effect.SourcePath.EndsWith(".cs",
                        StringComparison.Ordinal)),
                "source-backed effect coordinates and fingerprints");
            Check(report.Build == "not_run"
                && report.NUnitRuntime == "not_run"
                && report.BrowserObservables == "not_run",
                "read-only inspector must not claim build, test or browser");
            var json = System.Text.Json.JsonSerializer.Serialize(report);
            Check(!json.Contains("IPooledWebDriver>().WrappedDriver",
                StringComparison.Ordinal)
                && !json.Contains("GetProperties().Single",
                    StringComparison.Ordinal),
                "never expose original private hook bodies");

            var previous = report.SourceTreeHash;
            File.AppendAllText(Path.Combine(root, "FixtureBase.cs"),
                "\n// Source revision changed\n");
            Check(inspector.InspectNUnitLifecycle(root).SourceTreeHash != previous,
                "source edits must invalidate lifecycle evidence");

            var standalone = Path.Combine(root, "StandaloneFixture.cs");
            File.WriteAllText(standalone, """
                using NUnit.Framework;
                namespace Fictional.Suite {
                    [SetUpFixture] public class SafeScope {
                        [OneTimeSetUp] public void Prepare() {}
                        [OneTimeTearDown] public void Cleanup() {}
                    }
                }
                """);
            var expanded = inspector.InspectNUnitLifecycle(root);
            Check(expanded.SetUpFixtures.Count == 2
                && expanded.SetUpFixtures.Any(scope =>
                    scope.ContainingType == null
                    && scope.Hooks.Contains("OneTimeSetUp")
                    && scope.Hooks.Contains("OneTimeTearDown")),
                "standalone namespace scopes remain independent");

            try
            {
                inspector.InspectNUnitLifecycle(Path.Combine(root, "missing"));
                throw new Exception("missing source was accepted");
            }
            catch (DirectoryNotFoundException) {}
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Check(bool condition, string reason)
    {
        if (!condition)
            throw new InvalidOperationException("NUnit lifecycle source smoke: "
                + reason);
    }
}
