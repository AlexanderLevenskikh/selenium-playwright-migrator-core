using Migrator.Core;
using Migrator.Core.Models;
using Migrator.Roslyn;
using Migrator.SeleniumCSharp;
using Migrator.PlaywrightDotNet;

internal static class TableRowActionSmoke
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "table-action-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "TableFixture.cs");
            File.WriteAllText(source, """
                using NUnit.Framework;
                [TestFixture] public class TableFixture {
                    [Test] public void RealTableActions() {
                        var page = Navigation.OpenCatalogPage();
                        page.Table.Items.ElementAt(0).Click();
                        page.Table.Items.ElementAt(3).Should().NotBe("row");
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
                        TargetStatements = new[]
                        {
                            "await Page.GotoAsync(\"https://example.invalid/table\");",
                            "var {result} = Page;"
                        },
                        RequiresReview = true
                    }
                },
                UiTargets = new[]
                {
                    new UiTargetMapping("page.Table.Items",
                        "Page.Locator(\"[data-test='row']\")",
                        "RawExpression")
                }
            };
            var parsed = new RoslynTestFileParser(config).Parse(source);
            var actions = parsed.Tests.Single().BodyActions.ToArray();
            Check(actions.OfType<ClickAction>().Any(c =>
                    c.Target.SourceExpression.Contains("ElementAt(0)", StringComparison.Ordinal)),
                "Row click must be a semantic ClickAction with its row index");

            var result = new MigrationPipeline(
                new RoslynTestFileParser(config),
                new PlaywrightDotNetRenderer(),
                new DefaultProjectAdapter(config)).ProcessFile(source);
            var output = result.GeneratedOutput;
            Check(output.Contains(".Nth(0).ClickAsync();", StringComparison.Ordinal),
                "Mapped row click must execute, not merely declare a locator");
            Check(output.Contains("[MIGRATOR:TABLE_ROW_OPERATION_REQUIRES_MAPPING]",
                StringComparison.Ordinal),
                "Unrecognized row assertion must become a visible TODO");
            Check(!output.Contains("var row_1 =", StringComparison.Ordinal),
                "Unrecognized assertion must not silently emit locator-only proxy");
            Check(result.Report.FullyConvertedTests == 0,
                "Unimplemented row assertion must prevent full conversion success");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    static void Check(bool ok, string description)
    {
        if (!ok) throw new InvalidOperationException("Table-row action regression: " + description);
    }
}
