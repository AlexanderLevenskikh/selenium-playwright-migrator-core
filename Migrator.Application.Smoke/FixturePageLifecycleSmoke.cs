using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Migrator.Core;
using Migrator.Core.Models;
using Migrator.PlaywrightDotNet;
using Migrator.Roslyn;
using Migrator.SeleniumCSharp;

internal static class FixturePageLifecycleSmoke
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "fixture-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "SyntheticFixture.cs");
            File.WriteAllText(file, """
                using NUnit.Framework;
                using FluentAssertions;
                [TestFixture]
                public class SyntheticFixture
                {
                    private SyntheticPage page;

                    [SetUp]
                    public void Prepare()
                    {
                        var pagef = Navigation.OpenSyntheticPage();
                        pagef.Loader.ValidateLoading();
                        page = pagef;
                    }

                    [Test]
                    public void PreservesMappedAndUnmappedOperations()
                    {
                        page.Table.Items.ElementAt(0).Text.Get().Should().Be("sample");
                        page.Save.Click();
                        page.Unknown.SideEffect();
                    }
                }
                """);

            var navigation = new MethodMapping
            {
                SourceMethod = "Navigation.OpenSyntheticPage()",
                TargetStatements = new[] { "await Page.GotoAsync(\"https://example.invalid/synthetic\");" },
                RequiresReview = true
            };
            var loader = new MethodMapping
            {
                SourceMethod = "pagef.Loader.ValidateLoading()",
                TargetStatements = new[] {
                    "await Expect(Page.Locator(\"#loader\")).ToBeHiddenAsync();"
                },
                RequiresReview = true
            };
            var targets = new[]
            {
                new UiTargetMapping("page.Table.Items", "Page.Locator(\"[data-test='row']\")", "RawExpression"),
                new UiTargetMapping("page.Save", "Page.Locator(\"#save\")", "RawExpression")
            };
            ProjectAdapterConfig Config(params MethodMapping[] methods) => new()
            {
                SourceProjectName = "Synthetic",
                Methods = methods,
                UiTargets = targets
            };
            (string Generated, TestFileModel Parsed, TestFileModel Adapted, int Complete) RunCase(
                ProjectAdapterConfig cfg, string source)
            {
                var parser = new RoslynTestFileParser(cfg);
                var parsed = parser.Parse(source);
                var adapted = new DefaultProjectAdapter(cfg).Adapt(parsed);
                var output = new MigrationPipeline(
                    parser, new PlaywrightDotNetRenderer(), new DefaultProjectAdapter(cfg))
                    .ProcessFile(source);
                return (output.GeneratedOutput, parsed, adapted, output.Report.FullyConvertedTests);
            }

            var complete = RunCase(Config(navigation, loader), file);
            var parsedAlias = complete.Parsed.SetUpActions.OfType<FixturePageAliasAction>().Single();
            Check(parsedAlias.SourceField == "page" && parsedAlias.SourceLocal == "pagef"
                && !parsedAlias.HasExplicitNavigationMapping,
                "AST must retain source field/local identity without pretending to bind Playwright");
            var adaptedAlias = complete.Adapted.SetUpActions.OfType<FixturePageAliasAction>().Single();
            Check(adaptedAlias.HasExplicitNavigationMapping,
                "explicit target navigation preceding the alias should be recognized");
            var generated = complete.Generated;
            Check(generated.Contains("[MIGRATOR:FIXTURE_PAGE_ALIAS_REQUIRES_REVIEW]", StringComparison.Ordinal),
                "fixture lifetime must remain review-visible even after binding");
            Check(!generated.Contains("page = pagef;", StringComparison.Ordinal)
                || generated.Contains("// source:", StringComparison.Ordinal),
                "source POM field assignment must not be emitted as active target C#");
            Check(generated.Contains("await Page.GotoAsync(\"https://example.invalid/synthetic\");", StringComparison.Ordinal)
                && generated.Contains("ToBeHiddenAsync();", StringComparison.Ordinal),
                "explicit navigation and loader effects must execute in setup");
            Check(generated.Contains("await Page.Locator(\"#save\").ClickAsync();", StringComparison.Ordinal),
                "independently mapped fixture field action should execute as a Playwright click");
            Check(generated.Contains("ToHaveTextAsync(\"sample\")", StringComparison.Ordinal),
                "source table assertion must remain executable");
            Check(!generated.Contains("[MIGRATOR:TABLE_ASSERTION_REQUIRES_SETUP_REVIEW]", StringComparison.Ordinal),
                "lifecycle marker in setup should replace redundant per-table setup blockers");
            Check(generated.Contains("[MIGRATOR:UNRESOLVED_SYMBOL]", StringComparison.Ordinal),
                "unknown POM methods must remain blocked even when the fixture alias is known");
            Check(complete.Complete == 0,
                "review-only navigation and missing POM methods cannot yield fully converted tests");
            Check(!CSharpSyntaxTree.ParseText(generated).GetDiagnostics()
                .Any(x => x.Severity == DiagnosticSeverity.Error),
                "generated mapped fixture code must be C# syntax valid");
            var gotoAt = generated.IndexOf("await Page.GotoAsync", StringComparison.Ordinal);
            var waitAt = generated.IndexOf("ToBeHiddenAsync();", StringComparison.Ordinal);
            var aliasAt = generated.IndexOf("[MIGRATOR:FIXTURE_PAGE_ALIAS_REQUIRES_REVIEW]", StringComparison.Ordinal);
            Check(gotoAt >= 0 && waitAt > gotoAt && aliasAt > waitAt,
                "navigation, loader and alias must keep source setup order");

            var missingNav = RunCase(Config(loader), file);
            Check(!missingNav.Adapted.SetUpActions.OfType<FixturePageAliasAction>()
                .Single().HasExplicitNavigationMapping
                && !missingNav.Generated.Contains("await Page.Locator(\"#save\").ClickAsync();", StringComparison.Ordinal)
                && !missingNav.Generated.Contains("[MIGRATOR:FIXTURE_PAGE_ALIAS_REQUIRES_REVIEW]", StringComparison.Ordinal),
                "a POM assignment cannot establish fixture state without explicit navigation");

            var missingLoader = RunCase(Config(navigation), file);
            Check(!missingLoader.Adapted.SetUpActions.OfType<FixturePageAliasAction>()
                .Single().HasExplicitNavigationMapping
                && !missingLoader.Generated.Contains("await Page.Locator(\"#save\").ClickAsync();", StringComparison.Ordinal),
                "unmapped source setup helper on navigation local invalidates alias binding");

            var pretendNavigation = new MethodMapping
            {
                SourceMethod = navigation.SourceMethod,
                TargetStatements = new[] { "await Page.WaitForTimeoutAsync(10);" },
                RequiresReview = true
            };
            var fake = RunCase(Config(pretendNavigation, loader), file);
            Check(!fake.Adapted.SetUpActions.OfType<FixturePageAliasAction>()
                .Single().HasExplicitNavigationMapping,
                "a target wait is not source-backed navigation");

            var overwritten = Path.Combine(root, "Overwritten.cs");
            File.WriteAllText(overwritten, """
                using NUnit.Framework;
                [TestFixture] public class Overwritten
                {
                    private SyntheticPage page;
                    [SetUp] public void Prepare()
                    {
                        var pagef = Navigation.OpenSyntheticPage();
                        pagef = UnknownFactory.CreatePage();
                        page = pagef;
                    }
                    [Test] public void Test() { page.Save.Click(); }
                }
                """);
            var overwrittenCase = RunCase(Config(navigation), overwritten);
            Check(!overwrittenCase.Adapted.SetUpActions.OfType<FixturePageAliasAction>()
                .Single().HasExplicitNavigationMapping,
                "reassigning the source local must revoke navigation provenance");

            var shadowed = Path.Combine(root, "Shadowed.cs");
            File.WriteAllText(shadowed, """
                using NUnit.Framework;
                [TestFixture] public class Shadowed
                {
                    private SyntheticPage page;
                    [SetUp] public void Prepare()
                    {
                        var pagef = Navigation.OpenSyntheticPage();
                        SyntheticPage page = pagef;
                        page = pagef;
                    }
                    [Test] public void Test() {}
                }
                """);
            Check(!new RoslynTestFileParser(Config(navigation)).Parse(shadowed)
                .SetUpActions.OfType<FixturePageAliasAction>().Any(),
                "a shadowing setup local is not the fixture field");

            var lifecycle = Path.Combine(root, "Lifecycle.cs");
            File.WriteAllText(lifecycle, """
                using NUnit.Framework;
                [TestFixture] public class Lifecycle
                {
                    [OneTimeSetUp] public void BeforeAll() { ResetTenant(); }
                    [SetUp] public void PrepareOne() { PrepareEnvironment(); }
                    [SetUp] public void PrepareTwo() { SetCookie(); }
                    [TearDown] public void Cleanup() { DeleteCookie(); }
                    [Test] public void Test() { Assert.That(true); }
                }
                """);
            var lifecycleCase = RunCase(Config(), lifecycle);
            Check(lifecycleCase.Parsed.SetUpActions.OfType<UnsupportedAction>().Count() == 3
                && lifecycleCase.Generated.Contains("BeforeAll", StringComparison.Ordinal)
                && lifecycleCase.Generated.Contains("PrepareTwo", StringComparison.Ordinal)
                && lifecycleCase.Generated.Contains("Cleanup", StringComparison.Ordinal)
                && lifecycleCase.Complete == 0,
                "OneTimeSetUp, multiple SetUp and TearDown cannot silently disappear");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    static void Check(bool pass, string explanation)
    {
        if (!pass)
            throw new InvalidOperationException("Fixture Page lifecycle smoke: " + explanation);
    }
}
