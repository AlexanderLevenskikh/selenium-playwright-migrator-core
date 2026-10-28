using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Migrator.Core;
using Migrator.PlaywrightDotNet;
using Migrator.Roslyn;
using Migrator.SeleniumCSharp;

internal static class MappingLiteralRoundTripSmoke
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "literal-mapping-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "SyntheticFixture.cs");
            File.WriteAllText(source, """
                using NUnit.Framework;
                [TestFixture] public class SyntheticFixture {
                    [Test] public void Select() { page.Combo.InputAndSelect("alpha"); }
                }
                """);
            var config = new ProjectAdapterConfig
            {
                SourceProjectName = "Synthetic",
                ParameterizedMethods = new[]
                {
                    new ParameterizedMethodMapping
                    {
                        SourceMethodPattern = "page.Combo.InputAndSelect({term})",
                        RequiresReview = true,
                        TargetStatements = new[]
                        {
                            """var combo = Page.Locator("[data-test=\"combo\"]");""",
                            "await combo.ClickAsync();",
                            "await combo.FillAsync({term});",
                            """var options = Page.Locator("[data-test=\"{term}\"]");""",
                            "await options.ClickAsync();"
                        }
                    }
                }
            };
            var result = new MigrationPipeline(
                new RoslynTestFileParser(config),
                new PlaywrightDotNetRenderer(),
                new DefaultProjectAdapter(config)).ProcessFile(source);
            var output = result.GeneratedOutput;
            Check(output.Contains(
                """var combo = Page.Locator("[data-test=\"combo\"]");""",
                StringComparison.Ordinal), "literal attribute quotes lost or over-escaped");
            Check(output.Contains(
                """var options = Page.Locator("[data-test=\"alpha\"]");""",
                StringComparison.Ordinal), "in-string placeholder changed quote escaping");
            Check(output.Contains("await combo.FillAsync(\"alpha\");",
                StringComparison.Ordinal), "raw method argument placeholder was not substituted");
            Check(!output.Contains(
                """[data-test=\\\"combo\\\"]""",
                StringComparison.Ordinal), "double-escaped selector survived emission");
            Check(!CSharpSyntaxTree.ParseText(output).GetDiagnostics()
                .Any(d => d.Severity == DiagnosticSeverity.Error), "generated target syntax invalid");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Check(bool ok, string explanation)
    {
        if (!ok)
            throw new InvalidOperationException("Mapped literal regression: " + explanation);
    }
}
