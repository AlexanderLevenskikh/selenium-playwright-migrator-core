using Migrator.Core;
using Migrator.PlaywrightDotNet;
using Migrator.Roslyn;
using Migrator.SeleniumCSharp;

internal static class MappedTableAssertionWithSetupSmoke
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "mapped-table-setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "CatalogFixture.cs");
            File.WriteAllText(source, """
                using NUnit.Framework;
                using FluentAssertions;
                [TestFixture]
                public sealed class CatalogFixture {
                    private CatalogPage page;
                    [SetUp] public void Prepare() {
                        var pagef = Navigation.OpenCatalogPage();
                        pagef.Loader.ValidateLoading();
                        page = pagef;
                    }
                    [Test] public void FiltersTableRows() {
                        page.Table.Items.ElementAt(0).Text.Get().Should().Be("code");
                        page.Table.Items.ElementAt(1).Text.Get().Should().Contain("region");
                    }
                }
                """);

            var config = new ProjectAdapterConfig
            {
                SourceProjectName = "Synthetic",
                Methods = new[]
                {
                    new MethodMapping
                    {
                        SourceMethod = "Navigation.OpenCatalogPage()",
                        TargetStatements = new[] {
                            "await Page.GotoAsync(\"https://example.invalid/catalog\");"
                        },
                        RequiresReview = true
                    }
                },
                UiTargets = new[]
                {
                    new UiTargetMapping(
                        "page.Table.Items",
                        "Page.Locator(\"[data-test='table'] [data-test='row']\")",
                        "RawExpression")
                }
            };
            var result = new MigrationPipeline(
                new RoslynTestFileParser(config), new PlaywrightDotNetRenderer(),
                new DefaultProjectAdapter(config)).ProcessFile(source);
            var rendered = result.GeneratedOutput;

            Check(rendered.Contains(
                "await Expect(Page.Locator(\"[data-test='table'] [data-test='row']\").Nth(0)).ToHaveTextAsync(\"code\");",
                StringComparison.Ordinal), "source .Should().Be should remain an executable target assertion");
            Check(rendered.Contains(
                "await Expect(Page.Locator(\"[data-test='table'] [data-test='row']\").Nth(1)).ToContainTextAsync(\"region\");",
                StringComparison.Ordinal), "source .Should().Contain should retain substring semantics");
            Check(Count(rendered, "[MIGRATOR:TABLE_ASSERTION_REQUIRES_SETUP_REVIEW]") == 2,
                "unmigrated fixture setup must remain a visible review blocker for both assertions");
            Check(rendered.Contains("[MIGRATOR:UNRESOLVED_SYMBOL]", StringComparison.Ordinal),
                "unresolved source fixture assignment must remain disclosed");
            Check(result.Report.FullyConvertedTests == 0,
                "active assertions without verified setup are never complete");

            var unsupported = new ProjectAdapterConfig
            {
                SourceProjectName = "Synthetic",
                Methods = config.Methods
            };
            var missing = new MigrationPipeline(
                new RoslynTestFileParser(unsupported), new PlaywrightDotNetRenderer(),
                new DefaultProjectAdapter(unsupported)).ProcessFile(source);
            Check(!missing.GeneratedOutput.Contains("ToHaveTextAsync(\"code\")", StringComparison.Ordinal)
                && !missing.GeneratedOutput.Contains("ToContainTextAsync(\"region\")", StringComparison.Ordinal),
                "missing selector evidence must not bypass blocked source alias");

            var sourceOnlyTarget = new ProjectAdapterConfig
            {
                SourceProjectName = "Synthetic",
                Methods = config.Methods,
                UiTargets = new[]
                {
                    new UiTargetMapping("page.Table.Items",
                        "SeleniumSource.Page.FindElement()", "RawExpression")
                }
            };
            var unsafeResult = new MigrationPipeline(
                new RoslynTestFileParser(sourceOnlyTarget), new PlaywrightDotNetRenderer(),
                new DefaultProjectAdapter(sourceOnlyTarget)).ProcessFile(source);
            Check(!unsafeResult.GeneratedOutput.Contains(
                    "await Expect(SeleniumSource.Page.FindElement()", StringComparison.Ordinal),
                "source-like target expression may not bypass a blocked fixture root");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static int Count(string text, string marker) =>
        text.Split(marker, StringSplitOptions.None).Length - 1;

    private static void Check(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException("Mapped-table assertion smoke: " + message);
    }
}
