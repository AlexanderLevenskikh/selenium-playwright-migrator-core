using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
                        page.Loader.ValidateLoading();
                        page.Table.Items.ElementAt(0).Text.Get().Should().Be("code");
                        page.Table.Items.ElementAt(1).Text.Get().Should().Contain("region");
                        page.Table.Items.ElementAt(1).Text.Get().Should().NotContain("deprecated");
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
            Check(Count(rendered, ".Not.ToContainTextAsync(\\\"deprecated\\\")") == 1,
                "NotContain must become one executable negated substring assertion");
            Check(Count(rendered, "[MIGRATOR:TABLE_ASSERTION_REQUIRES_SETUP_REVIEW]") == 3,
                "unmigrated fixture setup must remain a visible review blocker for both assertions");
            Check(rendered.Contains("[MIGRATOR:UNRESOLVED_SYMBOL]", StringComparison.Ordinal),
                "unresolved source fixture assignment must remain disclosed");
            Check(result.Report.FullyConvertedTests == 0,
                "active assertions without verified setup are never complete");


            // Explicit, multiline source-backed helper mapping must retain
            // retry, conditional reload, state waits and assertion failure.
            // Source semantics are not declared equivalent until browser review.
            var loaderStatements = new[]
            {
                "var attempt = 0;",
                "while (attempt < 2)",
                "{",
                "try",
                "{",
                "if (await Page.Locator(\"#load-error\").IsVisibleAsync())",
                "{",
                "await Page.ReloadAsync();",
                "await Page.Locator(\"#loading\").WaitForAsync(new() { State = WaitForSelectorState.Hidden });",
                "}",
                "await Page.Locator(\"#ready\").WaitForAsync(new() { State = WaitForSelectorState.Visible });",
                "break;",
                "}",
                "catch (System.TimeoutException)",
                "{",
                "attempt++;",
                "if (attempt == 2)",
                "{",
                "Assert.Fail(\"Page did not become ready\");",
                "}",
                "}",
                "}"
            };
            var withLoader = new ProjectAdapterConfig
            {
                SourceProjectName = "Synthetic",
                UiTargets = config.UiTargets,
                Methods = config.Methods.Concat(new[]
                {
                    new MethodMapping
                    {
                        SourceMethod = "page.Loader.ValidateLoading()",
                        TargetStatements = loaderStatements,
                        RequiresReview = true
                    },
                    new MethodMapping
                    {
                        SourceMethod = "pagef.Loader.ValidateLoading()",
                        TargetStatements = loaderStatements,
                        RequiresReview = true
                    }
                }).ToArray()
            };
            var withLoaderResult = new MigrationPipeline(
                new RoslynTestFileParser(withLoader), new PlaywrightDotNetRenderer(),
                new DefaultProjectAdapter(withLoader)).ProcessFile(source);
            var loaderRendered = withLoaderResult.GeneratedOutput;
            Check(Count(loaderRendered, "while (attempt < 2)") == 2,
                "both fixture and test helper calls must preserve retry loops: got "
                + Count(loaderRendered, "while (attempt < 2)")
                + "\n" + loaderRendered[..Math.Min(loaderRendered.Length, 5200)]);
            Check(Count(loaderRendered, "await Page.ReloadAsync();") == 2,
                "both retry helpers must retain source navigation side effects");
            Check(Count(loaderRendered, "catch (System.TimeoutException)") == 2,
                "retry catch path may not disappear during multiline mapping rendering");
            Check(!CSharpSyntaxTree.ParseText(loaderRendered).GetDiagnostics()
                .Any(d => d.Severity == DiagnosticSeverity.Error),
                "multiline Loader mapping must remain syntactically valid C#");
            Check(withLoaderResult.Report.FullyConvertedTests == 0,
                "review-only loader and unresolved setup still block migration acceptance");

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
