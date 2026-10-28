using Migrator.Application;

internal static class ExistingPlaywrightGuardSmoke
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "migrator-existing-playwright-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var engine = new MigrationEngine();
        try
        {
            var project = Path.Combine(root, "Existing.Tests.csproj");
            File.WriteAllText(project, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Microsoft.Playwright.NUnit" Version="1.55.0" />
                  </ItemGroup>
                </Project>
                """);
            var tests = Path.Combine(root, "ExistingTests.cs");
            // This synthetic fixture reflects common already-Playwright patterns,
            // not any private customer's source or project identifiers.
            File.WriteAllText(tests, """
                using NUnit.Framework;
                [TestFixture] public class ExistingTests : ExistingBase {
                    [Test] public async System.Threading.Tasks.Task ExistingBehavior() {
                        var locator = Page.GetByTestId("example");
                        await Expect(locator).ToHaveTextAsync("example");
                    }
                }
                """);
            var current = engine.Analyze(root);
            Check(current.DiscoveryStatus == "partial", "already Playwright must block discovery completeness");
            Check(current.DeclaredTests == 1 && current.ParsedTests == 0, "count but do not parse as Selenium");
            Check(current.Files.Single().Disposition == "already_playwright", "target source disposition");
            Check(current.Findings.Any(f => f.Code == "ALREADY_PLAYWRIGHT_SOURCE"), "missing Playwright explanation");
            Check(engine.Preview(root).Files.Count == 0, "never regenerate already Playwright target");
            var described = engine.Explain(root, current.Findings.Single().Id,
                expectedAnalysisId: current.AnalysisId);
            Check(described.SuggestedNextAction.Contains("Do not run Selenium migration"),
                "finding should recommend target verification");

            File.Delete(project);
            // Standalone Playwright import/assertions must be blocked without csproj.
            File.WriteAllText(tests, """
                using Microsoft.Playwright;
                using NUnit.Framework;
                [TestFixture] public class ExistingTests {
                    [Test] public void ExistingBehavior() { var x = Page.GetByTestId("x"); }
                }
                """);
            var imports = engine.Analyze(tests);
            Check(imports.Files.Single().Disposition == "already_playwright", "direct Playwright import detection");

            File.WriteAllText(project, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Microsoft.Playwright.NUnit" Version="1.55.0" />
                    <PackageReference Include="Selenium.WebDriver" Version="4.0.0" />
                  </ItemGroup>
                </Project>
                """);
            var mixed = engine.Analyze(root);
            Check(mixed.Files.Single().Disposition == "mixed_frameworks", "mixed source should fail closed");
            Check(mixed.Findings.Any(f => f.Code == "MIXED_SELENIUM_PLAYWRIGHT_SOURCE"), "mixed diagnostic missing");
            Check(engine.Preview(root).Files.Count == 0, "mixed source may not generate unsupported target");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException("Playwright source guard smoke failed: " + message);
    }
}
