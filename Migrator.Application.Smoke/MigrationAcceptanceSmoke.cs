using Migrator.Core;
using Migrator.Core.Models;
using Migrator.Application;

internal static class MigrationAcceptanceSmoke
{
    public static void Run()
    {
        const string baseline = """
            using System.Threading.Tasks;
            public class BrowserTests {
              public async Task Test() {
                await page.Locator("#confirm").ClickAsync();
                Assert.That(true, Is.True);
              }
            }
            """;
        const string dropped = """
            using System.Threading.Tasks;
            public class BrowserTests {
              public async Task Test() {
                Assert.That(true, Is.True);
              }
            }
            """;
        var file = new GeneratedFile("Source.cs", "BrowserTests.cs", baseline,
            new string('0', 64), 1, 1, 0);
        var preview = new MigrationPreview(ContractVersions.Preview,
            new string('1', 64), new string('2', 64), new string('3', 64),
            "complete", new[] { file }, Array.Empty<MigrationFinding>());
        var intact = MigrationAcceptanceGate.Evaluate(preview,
            new[] { ("BrowserTests.cs", baseline) });
        Check(intact.Status == MigrationAcceptanceGate.BlockedUnverified,
            "even matching preview is not certified browser behavior");
        Check(intact.TestDiscovery.Status == "not_run" && intact.TestRunner.Status == "not_run"
            && intact.RealBrowser.Status == "not_run"
            && intact.BehavioralEquivalence.Status == "not_run",
            "never manufacture NUnit or browser evidence");
        var edited = MigrationAcceptanceGate.Evaluate(preview,
            new[] { ("BrowserTests.cs", dropped) });
        Check(edited.Status == MigrationAcceptanceGate.RejectedIncomplete
            && edited.MissingTargetOperations.Any(d => d.Code == "TARGET_OPERATION_REMOVED"),
            "silent removal of ClickAsync must reject even if assertion passes");
        var partial = preview with { TransformationStatus = "partial" };
        Check(MigrationAcceptanceGate.Evaluate(partial,
            new[] { ("BrowserTests.cs", baseline) }).Status == MigrationAcceptanceGate.RejectedIncomplete,
            "green target cannot override incomplete source migration");
        var root = Path.GetTempPath();
        var src = new TestFileModel(Path.Combine(root, "Source.cs"), "Synthetic", "Tests", null,
            Array.Empty<TestAction>(), new[] {
                new TestModel("Test", null, Array.Empty<TestCaseData>(),
                    Array.Empty<MethodParameterModel>(), new TestAction[] {
                        new ClickAction(12, "first"), new ClickAction(13, "second") }) });
        var tgt = new TestFileModel(src.FilePath, "Synthetic", "Tests", null,
            Array.Empty<TestAction>(), new[] {
                new TestModel("Test", null, Array.Empty<TestCaseData>(),
                    Array.Empty<MethodParameterModel>(), new TestAction[] {
                        new ClickAction(12, "first") }) });
        var units = MigrationAcceptanceGate.SourceLedger(
            new[] { new PipelineResult(src, tgt, baseline, ReportBuilder.Build(tgt, baseline)) }, root);
        Check(units.Count == 2 && units[0].Projection == "projected_ir_only"
            && units[1].Projection == "missing_or_reordered_in_ir",
            "removed source action must appear as incomplete obligation");
        var removedAssertion = baseline.Replace("Assert.That(true, Is.True);", "", StringComparison.Ordinal);
        var withoutAssertion = MigrationAcceptanceGate.Evaluate(preview,
            new[] { ("BrowserTests.cs", removedAssertion) });
        Check(withoutAssertion.Status == MigrationAcceptanceGate.RejectedIncomplete,
            "lost assertion must reject even when remaining action compiles");
    }

    private static void Check(bool good, string message)
    {
        if (!good) throw new InvalidOperationException("P2 acceptance smoke: " + message);
    }
}
