using Migrator.Core;
using Migrator.Core.Models;

internal static class RenderedTodoCompletenessSmoke
{
    public static void Run()
    {
        // Both IR bodies look executable in isolation. Only the actual rendered
        // target proves that one was downgraded to a comment.
        var action = new LocalDeclarationAction(7, "value", "var", "1");
        var tests = new[]
        {
            new TestModel("SafeCase", null, Array.Empty<TestCaseData>(),
                Array.Empty<MethodParameterModel>(), new TestAction[] { action }),
            new TestModel("IncompleteCase", null, Array.Empty<TestCaseData>(),
                Array.Empty<MethodParameterModel>(), new TestAction[] { action })
        };
        var model = new TestFileModel("synthetic.cs", "Synthetic", "Example", null,
            Array.Empty<TestAction>(), tests);
        var rendered = """
            public class Example : PageTest
            {
                [Test]
                public async Task SafeCase()
                {
                    var value = 1;
                }

                [Test]
                public async Task IncompleteCase()
                {
                    // TODO: depends on unresolved symbol 'page' [MIGRATOR:UNRESOLVED_SYMBOL]
                }
            }
            """;
        Check(ReportBuilder.Build(model, rendered).FullyConvertedTests == 1,
            "Renderer TODO must disqualify only its owning test");
        var noDebt = rendered.Replace(
            "// TODO: depends on unresolved symbol 'page' [MIGRATOR:UNRESOLVED_SYMBOL]",
            "var value = 1;", StringComparison.Ordinal);
        Check(ReportBuilder.Build(model, noDebt).FullyConvertedTests == 2,
            "Do not penalize fully converted tests without renderer debt");
        var fixtureDebt = rendered.Replace("public class Example : PageTest",
            "// TODO: unresolved fixture setup\npublic class Example : PageTest", StringComparison.Ordinal);
        Check(ReportBuilder.Build(model, fixtureDebt).FullyConvertedTests == 0,
            "Shared setup/fixture debt must disqualify every test");
        var unattributable = rendered.Replace("public async Task IncompleteCase()",
            "public async Task RenamedOrMissing()", StringComparison.Ordinal);
        Check(ReportBuilder.Build(model, unattributable).FullyConvertedTests == 0,
            "Unknown method ownership must fail closed");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Rendered TODO coverage regression: " + message);
    }
}
