using Migrator.Application;

internal static class NUnitExecutionGateSmoke
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "nunit-gates-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "FictionalFixture.cs");
            var cases = new[]
            {
                """
                using NUnit.Framework;
                [TestFixture]
                [Ignore("disabled fixture")]
                public class FictionalFixture
                {
                    [Test] public void Example() {}
                }
                """,
                """
                using NUnit.Framework;
                [TestFixture] public class FictionalFixture
                {
                    [Test] [Explicit("opt-in only")]
                    public void Example() {}
                }
                """,
                """
                using NUnit.Framework;
                [TestFixture] public class FictionalFixture
                {
                    [TestCase(7, Ignore = "temporarily disabled")]
                    public void Example(int value) {}
                }
                """,
                """
                using NUnit.Framework;
                [TestFixture] public class FictionalFixture
                {
                    [TestCase(7, Explicit = true)]
                    public void Example(int value) {}
                }
                """,
                """
                using NUnit.Framework;
                [TestFixture(Explicit = true)]
                public class FictionalFixture
                {
                    [Test] public void Example() {}
                }
                """,
                """
                using NUnit.Framework;
                [TestFixture] public class FictionalFixture
                {
                    [Test] [NUnit.Framework.IgnoreAttribute("maintenance")]
                    public void Example() {}
                }
                """
            };
            var engine = new MigrationEngine();
            foreach (var source in cases)
            {
                File.WriteAllText(file, source);
                var analysis = engine.Analyze(file);
                Check(analysis.DiscoveryStatus == "partial"
                    && analysis.DeclaredTests == 1
                    && analysis.ParsedTests == 0,
                    "skip/explicit NUnit gate must be inventoried but never migrated");
                var finding = analysis.Findings.Single(f =>
                    f.Code == "NUNIT_EXECUTION_GATE_REQUIRES_REVIEW");
                Check(finding.Line > 0,
                    "gate must have a source line for explicit review");
                var preview = engine.Preview(file,
                    expectedAnalysisId: analysis.AnalysisId);
                Check(preview.TransformationStatus == "blocked"
                    && preview.Files.Count == 0,
                    "no runnable NUnit fixture may be emitted after dropping a gate");
            }

            File.WriteAllText(file, """
                using NUnit.Framework;
                [TestFixture] public class FictionalFixture
                {
                    [Test] public void Example() {}
                }
                """);
            var safe = engine.Analyze(file);
            Check(safe.DiscoveryStatus == "complete"
                && safe.DeclaredTests == 1 && safe.ParsedTests == 1,
                "ordinary NUnit tests must remain supported");
            Check(engine.Preview(file, expectedAnalysisId: safe.AnalysisId)
                .Files.Count == 1, "ordinary NUnit preview must still generate");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Check(bool ok, string explanation)
    {
        if (!ok) throw new InvalidOperationException(
            "NUnit execution gate smoke: " + explanation);
    }
}
