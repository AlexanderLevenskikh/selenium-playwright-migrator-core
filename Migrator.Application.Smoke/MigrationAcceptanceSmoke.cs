using Migrator.Application;

internal static class MigrationAcceptanceSmoke
{
    public static void Run()
    {
        var sourceRoot = Path.Combine(Path.GetTempPath(),
            "migrator-p2-acceptance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceRoot);
        try
        {
            var source = Path.Combine(sourceRoot, "FictionalFixture.cs");
            var original = """
                using NUnit.Framework;
                [TestFixture] public class FictionalFixture {
                    [SetUp] public void Prepare() { SeedSession(); }
                    [Test] public void PreservesAction() {
                        driver.FindElement("button").Click();
                        Assert.That(true, Is.True);
                    }
                    }
                """;
            File.WriteAllText(source, original);
            var engine = new MigrationEngine();
            var analysis = engine.Analyze(source);
            var preview = engine.Preview(source, expectedAnalysisId: analysis.AnalysisId);
            Check(preview.Files.Count == 1, "P2 requires an actual generated preview");
            var targetPath = preview.Files.Single().RelativePath;
            var generated = engine.VerifyArtifact(source, analysis.AnalysisId, preview.CandidateId);
            Check(generated.Acceptance != null, "every candidate must carry mandatory acceptance");
            Check(generated.Acceptance!.Status != "accepted", "syntax must not mean accepted");
            Check(generated.Acceptance.TestDiscovery.Status == "not_run"
                && generated.Acceptance.Runner.Status == "not_run"
                && generated.Acceptance.RealBrowser.Status == "not_run"
                && generated.Acceptance.BehavioralEquivalence.Status == "not_run",
                "unexecuted stages must remain distinct and not_run");

            // Synthetic target NUnit would PASS: its sole assertion is true,
            // while the source Click and SetUp SeedSession effects disappeared.
            // The test-runner proof is independently established in CI via
            // scripts/p2_false_green_real_nunit.py, not inferred here.
            var falseGreenTarget = """
                using NUnit.Framework;
                [TestFixture] public class FictionalFixturePlaywright {
                    [Test] public void PreservesAction() {
                        Assert.That(true, Is.True);
                    }
                    }
                """;
            var noAction = MigrationAcceptanceGate.Evaluate(analysis,
                preview with { TransformationStatus = "complete",
                    Files = preview.Files.Select(f => f with {
                        Tests = 1, FullyConvertedTests = 1, TodoComments = 0 }).ToArray(),
                    Findings = Array.Empty<MigrationFinding>() },
                new Dictionary<string, string> { [targetPath] = falseGreenTarget },
                Passed("syntax"), Passed("compile"), "edited-candidate", "target-hash");
            Check(noAction.Status == MigrationAcceptanceGate.RejectedIncomplete
                && noAction.MissingObligations >= 2
                && noAction.Lineage.Any(x => x.Kind == "action" && x.Status == "missing_target")
                && noAction.Lineage.Any(x => x.Kind == "lifecycle_effect" && x.Status == "missing_target"),
                "green NUnit cannot hide lost Click or SetUp helper");

            var noAssertion = falseGreenTarget.Replace(
                "Assert.That(true, Is.True);",
                "System.Console.WriteLine(\"no assertion\");", StringComparison.Ordinal);
            var missingAssertion = MigrationAcceptanceGate.Evaluate(analysis,
                preview, new Dictionary<string, string> { [targetPath] = noAssertion },
                Passed("syntax"), Passed("compile"), "edited-candidate2", "target-hash2");
            Check(missingAssertion.Status == MigrationAcceptanceGate.RejectedIncomplete
                && missingAssertion.Lineage.Any(x =>
                    x.Kind == "assertion" && x.Status == "missing_target"),
                "missing assertion must be explicit, regardless of NUnit PASS");

            var sameShape = """
                using NUnit.Framework;
                [TestFixture] public class FictionalFixturePlaywright {
                    [SetUp] public void Prepare() { SeedSession(); }
                    [Test] public void PreservesAction() {
                        Page.Locator("#other-button").ClickAsync();
                        Assert.That(true, Is.True);
                    }
                    }
                """;
            var candidate = MigrationAcceptanceGate.Evaluate(analysis,
                preview with { TransformationStatus = "complete",
                    Files = preview.Files.Select(f => f with {
                        Tests = 1, FullyConvertedTests = 1, TodoComments = 0 }).ToArray(),
                    Findings = Array.Empty<MigrationFinding>() },
                new Dictionary<string, string> { [targetPath] = sameShape },
                Passed("syntax"), Passed("compile"), "edited-candidate3", "target-hash3");
            Check(candidate.Status != "accepted"
                && candidate.Lineage.Any(x => x.Status == "candidate_unverified")
                && candidate.BehavioralEquivalence.Status == "not_run",
                "same operation count and a different selector cannot prove behavior");

            var deletedTest = sameShape.Replace(
                "public void PreservesAction()", "public void RenamedTest()", StringComparison.Ordinal);
            var missingMethod = MigrationAcceptanceGate.Evaluate(analysis,
                preview, new Dictionary<string, string> { [targetPath] = deletedTest },
                Passed("syntax"), Passed("compile"), "edited-candidate4", "target-hash4");
            Check(missingMethod.Status == MigrationAcceptanceGate.RejectedIncomplete
                && missingMethod.Lineage.Any(x => x.Kind == "method"
                    && x.Method == "PreservesAction" && x.Status == "missing_target"),
                "removed NUnit test declaration must not disappear from lineage");

            Check(generated.Acceptance.AnalysisId == analysis.AnalysisId
                && generated.Acceptance.CandidateId == preview.CandidateId
                && generated.Acceptance.SourceTreeHash == analysis.SourceTreeHash,
                "acceptance must bind the exact analysis/candidate/source snapshot");
        }
        finally { Directory.Delete(sourceRoot, recursive: true); }
    }

    private static VerificationStage Passed(string kind) =>
        new("passed", kind, Array.Empty<VerificationDiagnostic>());

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException("P2 acceptance smoke: " + message);
    }
}
