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
            var engine = new MigrationEngine();

            var supported = new (string Source, string Attribute, string State, int Cases)[]
            {
                ("""
                    using NUnit.Framework;
                    [TestFixture] [Ignore("fixture maintenance")]
                    public class FictionalFixture {
                        [Test] public void Example() { Assert.That(true, Is.True); }
                    }
                    """, "[Ignore(\"fixture maintenance\")]", "ignored", 1),
                ("""
                    using NUnit.Framework;
                    [TestFixture] public class FictionalFixture {
                        [Test] [Explicit("opt-in")]
                        public void Example() { Assert.That(true, Is.True); }
                    }
                    """, "[Explicit(\"opt-in\")]", "explicit", 1),
                ("""
                    using NUnit.Framework;
                    [TestFixture] public class FictionalFixture {
                        [TestCase(7, Ignore = "disabled row")]
                        [TestCase(8)]
                        public void Example(int value) { Assert.That(value, Is.EqualTo(value)); }
                    }
                    """, "[TestCase(7, Ignore = \"disabled row\")]", "ignored", 2),
                ("""
                    using NUnit.Framework;
                    [TestFixture] public class FictionalFixture {
                        [TestCase(7, Explicit = true)]
                        public void Example(int value) { Assert.That(value, Is.EqualTo(7)); }
                    }
                    """, "[TestCase(7, Explicit = true)]", "explicit", 1),
                ("""
                    using NUnit.Framework;
                    [TestFixture] public class FictionalFixture {
                        [Test] [NUnit.Framework.IgnoreAttribute("qualified")]
                        public void Example() { Assert.That(true, Is.True); }
                    }
                    """, "[NUnit.Framework.IgnoreAttribute(\"qualified\")]", "ignored", 1),
                ("""
                    using NUnit.Framework;
                    [TestFixture] public class FictionalFixture {
                        private static readonly object[][] Rows = new object[][] {
                            new object[] { 2 }, new object[] { 4 }
                        };
                        [TestCaseSource(nameof(Rows))]
                        public void Example(int value) { Assert.That(value > 0, Is.True); }
                    }
                    """, "[TestCase(2)]", "runnable", 2),
                ("""
                    using NUnit.Framework;
                    [TestFixture] public class FictionalFixture {
                        public static System.Collections.Generic.IEnumerable<object[]> Rows =>
                            new[] { new object[] { 5 }, new object[] { 6 } };
                        [TestCaseSource("Rows")]
                        public void Example(int value) { Assert.That(value > 0, Is.True); }
                    }
                    """, "[TestCase(5)]", "runnable", 2),
                ("""
                    using NUnit.Framework;
                    [TestFixture] public class FictionalFixture {
                        private static readonly NUnit.Framework.TestCaseData[] Rows = new[] {
                            new TestCaseData(9), new TestCaseData(10)
                        };
                        [TestCaseSource(nameof(Rows))]
                        public void Example(int value) { Assert.That(value > 0, Is.True); }
                    }
                    """, "[TestCase(9)]", "runnable", 2)
            };

            foreach (var (source, expectedAttribute, expectedState, expectedCount) in supported)
            {
                File.WriteAllText(file, source);
                var analysis = engine.Analyze(file);
                Check(analysis.DiscoveryStatus == "complete"
                    && analysis.DeclaredTests == 1 && analysis.ParsedTests == 1,
                    "Safe NUnit gate/static source must parse: "
                    + string.Join(";", analysis.Findings.Select(f => f.Code)));
                Check(analysis.StaticallyResolvedNUnitCases == expectedCount,
                    "Static case inventory differs from expected row count.");
                Check(analysis.Files.Single().NUnitCases.Any(c => c.RunState == expectedState),
                    "Case gate state lost.");
                Check(analysis.BlockedNUnitSources == 0
                    && analysis.Files.Single().NUnitAttributes.All(a =>
                        a.SourceSha256.Length == 64 && a.Line > 0
                        && !a.RequiresReview),
                    "Evidence must be line/hash backed and review-free.");
                var preview = engine.Preview(file, expectedAnalysisId: analysis.AnalysisId);
                Check(preview.Files.Count == 1 && preview.TransformationStatus != "blocked",
                    "Safe NUnit data/gate must render.");
                Check(preview.Files.Single().Content.Contains(expectedAttribute,
                    StringComparison.Ordinal), "Target lost a reviewed NUnit attribute.");
                Check(!preview.Files.Single().Content.Contains("TestCaseSource",
                    StringComparison.Ordinal), "Unresolved source member escaped into the target.");
                Check(preview.NUnitCases.Count == expectedCount,
                    "Preview case inventory disagrees with analysis.");
            }

            var blocked = new (string Source, string ExpectedCode)[]
            {
                ("""
                    using NUnit.Framework;
                    [TestFixture(Explicit = true)]
                    public class FictionalFixture {
                        [Test] public void Example() {}
                    }
                    """, "NUNIT_FIXTURE_NAMED_GATE_REQUIRES_REVIEW"),
                ("""
                    using NUnit.Framework;
                    [TestFixture] public class FictionalFixture {
                        [Ignore(System.Environment.MachineName)]
                        [Test] public void Example() {}
                    }
                    """, "NUNIT_EXECUTION_GATE_REQUIRES_REVIEW"),
                ("""
                    using NUnit.Framework;
                    [TestFixture] public class FictionalFixture {
                        private static object[][] Rows => Generate();
                        private static object[][] Generate() => new[] { new object[] { 1 } };
                        [TestCaseSource(nameof(Rows))]
                        public void Example(int value) {}
                    }
                    """, "NUNIT_TEST_CASE_SOURCE_UNSAFE"),
                ("""
                    using NUnit.Framework;
                    [TestFixture] public class FictionalFixture {
                        private static readonly object[][] Rows = new object[][] {
                            new object[] { System.DateTime.UtcNow.Day }
                        };
                        [TestCaseSource(nameof(Rows))]
                        public void Example(int value) {}
                    }
                    """, "NUNIT_TEST_CASE_SOURCE_UNSAFE"),
                ("""
                    using NUnit.Framework;
                    [TestFixture] public class FictionalFixture {
                        [TestCaseSource(typeof(ExternalCases), "Rows")]
                        public void Example(int value) {}
                    }
                    """, "NUNIT_TEST_CASE_SOURCE_UNSAFE"),
                ("""
                    using NUnit.Framework;
                    [Ignore("inherited block")] public class FixtureBase {}
                    [TestFixture] public class FictionalFixture : FixtureBase {
                        [Test] public void Example() {}
                    }
                    """, "NUNIT_INHERITED_GATE_REQUIRES_REVIEW"),
                ("""
                    using NUnit.Framework;
                    [TestFixture] public class FictionalFixture {
                        private static readonly object[][] Rows = new[] { new object[] { "oops" } };
                        [TestCaseSource(nameof(Rows))]
                        public void Example(int value) {}
                    }
                    """, "NUNIT_TEST_CASE_SOURCE_UNSAFE")
            };
            foreach (var (source, code) in blocked)
            {
                File.WriteAllText(file, source);
                var analysis = engine.Analyze(file);
                Check(analysis.DiscoveryStatus == "partial"
                    && analysis.DeclaredTests == 1
                    && analysis.ParsedTests == 0, "Unsafe NUnit shape became supported.");
                Check(analysis.Findings.Any(f => f.Code == code && f.Line > 0),
                    "Expected source-line diagnostic missing: " + code);
                var preview = engine.Preview(file, expectedAnalysisId: analysis.AnalysisId);
                Check(preview.TransformationStatus == "blocked"
                    && preview.Files.Count == 0,
                    "Unsafe source emitted a runnable target.");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Check(bool ok, string explanation)
    {
        if (!ok) throw new InvalidOperationException(
            "NUnit execution gate smoke: " + explanation);
    }
}
