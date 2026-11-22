using Migrator.Application;

internal static class P2ProjectGraphAcceptanceSmoke
{
    public static void Run()
    {
        var temp = Path.Combine(Path.GetTempPath(),
            "migrator-p2project-graph-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var path = Path.Combine(temp, "GraphSource.cs");
            File.WriteAllText(path, """
                using NUnit.Framework;
                [SetUpFixture]
                public sealed class AllFixtures {
                    [OneTimeSetUp] public void Begin() { Registry.Start(); }
                    [OneTimeTearDown] public void End() { Registry.Stop(); }
                }
                public class SharedBase {
                    [SetUp] public void Before() { Registry.Seed(); }
                }
                [TestFixture]
                public sealed class SampleTests : SharedBase {
                    [Test] public void ClickAndAssert() { Perform(); }
                    private void Perform() { ThenClick(); }
                    private void ThenClick() {
                        driver.FindElement("#submit").Click();
                        Assert.That(true, Is.True);
                    }
                }
                """);
            var analysis = new MigrationAnalysis(
                "migrator-analysis/v1alpha2", "fixture-analysis", path,
                "source-hash", "config-hash", "complete", 1, 1,
                new[] { new FileSurvey("GraphSource.cs", "supported",
                    1, 1, new[] { "ClickAndAssert" },
                    Array.Empty<MigrationFinding>()) },
                Array.Empty<MigrationFinding>(), Array.Empty<string>());
            const string targetPath = "GraphTarget.cs";
            var preview = new MigrationPreview("migrator-preview/v1alpha2",
                analysis.AnalysisId, "parent-candidate", "preview-target",
                "complete",
                new[] { new GeneratedFile("GraphSource.cs", targetPath, "",
                    "preview-file", 1, 1, 0) },
                Array.Empty<MigrationFinding>());
            var missing = """
                using NUnit.Framework;
                [TestFixture] public sealed class SampleTests {
                    [Test] public void ClickAndAssert() { Perform(); }
                    private void Perform() { ThenClick(); }
                    private void ThenClick() { Assert.That(true, Is.True); }
                }
                """;
            MigrationAcceptanceReport Gate(string target, string candidate) =>
                MigrationAcceptanceGate.Evaluate(analysis, preview,
                    new Dictionary<string, string> { [targetPath] = target },
                    Passed("syntax"), Passed("roslyn-compile"),
                    candidate, "target-tree-" + candidate);

            var lost = Gate(missing, "mutant-loss");
            Check(lost.Status == MigrationAcceptanceGate.RejectedIncomplete
                && lost.Lineage.Any(x => x.Kind == "setup_fixture"
                    && x.Status == "missing_target")
                && lost.Lineage.Any(x => x.Kind == "fixture_base"
                    && x.Status == "missing_target")
                && lost.Lineage.Any(x => x.Kind == "helper_action"
                    && x.Status == "missing_target")
                && lost.Blockers.Contains("NUNIT_SETUP_FIXTURE_MISSING")
                && lost.Blockers.Contains("NUNIT_INHERITED_BASE_NOT_PRESERVED")
                && lost.BehavioralEquivalence.Status == "not_run",
                "green test invocation must not mask lost setup fixture,"
                + " inherited base or private transitive Click");

            var intact = """
                using NUnit.Framework;
                [SetUpFixture]
                public sealed class AllFixtures {
                    [OneTimeSetUp] public void Begin() { Registry.Start(); }
                    [OneTimeTearDown] public void End() { Registry.Stop(); }
                }
                public class SharedBase {
                    [SetUp] public void Before() { Registry.Seed(); }
                }
                [TestFixture]
                public sealed class SampleTests : SharedBase {
                    [Test] public void ClickAndAssert() { Perform(); }
                    private void Perform() { ThenClick(); }
                    private void ThenClick() {
                        Page.Locator("#submit").ClickAsync();
                        Assert.That(true, Is.True);
                    }
                }
                """;
            var preserved = Gate(intact, "matching-shape");
            Check(preserved.Status == MigrationAcceptanceGate.BlockedUnverified
                && preserved.Lineage.Any(x => x.Kind == "setup_fixture"
                    && x.Status == "candidate_unverified")
                && preserved.Lineage.Any(x => x.Kind == "fixture_base"
                    && x.Status == "candidate_unverified")
                && preserved.Lineage.Any(x => x.Kind == "helper_action"
                    && x.Status == "candidate_unverified")
                && preserved.BehavioralEquivalence.Status == "not_run",
                "identical AST with NUnit lifecycle is still not behavioral proof. "
                + "status=" + preserved.Status
                + " blockers=" + string.Join(",", preserved.Blockers)
                + " missing=" + string.Join(";", preserved.Lineage
                    .Where(x => x.Status == "missing_target")
                    .Select(x => x.Kind + ":" + x.Method)));

            var inverted = intact.Replace("\"#submit\"", "\"#other\"",
                StringComparison.Ordinal);
            var changed = Gate(inverted, "selector-mutation");
            Check(changed.Status != "accepted"
                && changed.BehavioralEquivalence.Status == "not_run",
                "mutated selector with same call count must not be accepted");

            var noBase = intact.Replace("SampleTests : SharedBase",
                "SampleTests : OtherBase", StringComparison.Ordinal);
            var wrongInheritance = Gate(noBase, "changed-base");
            Check(wrongInheritance.Status ==
                    MigrationAcceptanceGate.RejectedIncomplete
                && wrongInheritance.Lineage.Any(x =>
                    x.Kind == "fixture_base" && x.Status == "missing_target"),
                "changed inherited lifecycle must fail closed");

            // A target declaring [SetUpFixture] without the original
            // lifecycle hooks does not satisfy the source hook inventory.
            var noGlobalTeardown = intact.Replace(
                "[OneTimeTearDown] public void End() { Registry.Stop(); }",
                "public void End() { Registry.Stop(); }",
                StringComparison.Ordinal);
            var missingHook = Gate(noGlobalTeardown, "missing-hook");
            Check(missingHook.Status == MigrationAcceptanceGate.RejectedIncomplete
                && missingHook.Lineage.Any(x => x.Kind == "method"
                    && x.Method == "End" && x.Status == "missing_target"),
                "assembly-scoped cleanup must be preserved as a NUnit hook");

            var sameHash = Gate(missing, "second-target-candidate");
            Check(sameHash.CandidateId != lost.CandidateId
                && sameHash.TargetTreeHash != lost.TargetTreeHash
                && sameHash.AnalysisId == lost.AnalysisId
                && sameHash.SourceTreeHash == lost.SourceTreeHash,
                "project graph report must bind input and target revision identities");
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    private static VerificationStage Passed(string kind) =>
        new("passed", kind, Array.Empty<VerificationDiagnostic>());

    private static void Check(bool ok, string description)
    {
        if (!ok)
            throw new InvalidOperationException(
                "P2_PROJECT_GRAPH_ACCEPTANCE_SMOKE: " + description);
    }
}
