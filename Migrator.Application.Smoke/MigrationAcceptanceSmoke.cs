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

            // Control-flow loss is independent of the invocation inventory.
            // Both target snippets keep every source invocation candidate.
            var flowSource = Path.Combine(sourceRoot, "FlowFixture.cs");
            File.WriteAllText(flowSource, """
                using NUnit.Framework;
                [TestFixture] public class FlowFixture {
                    [Test] public void GuardedClick() {
                        if (true) {
                            driver.FindElement("#save").Click();
                        } else {
                            Assert.Fail("blocked");
                        }
                        try {
                            driver.FindElement("#retry").Click();
                        } catch (System.InvalidOperationException) {
                            throw;
                        } finally {
                            Assert.That(true, Is.True);
                        }
                    }
                }
                """);
            var flowAnalysis = engine.Analyze(flowSource);
            var flowPreview = engine.Preview(flowSource, expectedAnalysisId: flowAnalysis.AnalysisId);
            Check(flowPreview.Files.Count == 1, "control-flow fixture must have a captured preview");
            var flowTargetPath = flowPreview.Files.Single().RelativePath;
            var completeFlowPreview = flowPreview with {
                TransformationStatus = "complete",
                Files = flowPreview.Files.Select(f => f with {
                    Tests = 1, FullyConvertedTests = 1, TodoComments = 0 }).ToArray(),
                Findings = Array.Empty<MigrationFinding>()
            };

            var flattened = """
                using NUnit.Framework;
                [TestFixture] public class FlowFixturePlaywright {
                    [Test] public void GuardedClick() {
                        Page.Locator("#save").ClickAsync();
                        Assert.Fail("blocked");
                        Page.Locator("#retry").ClickAsync();
                        Assert.That(true, Is.True);
                    }
                }
                """;
            var lostFlow = MigrationAcceptanceGate.Evaluate(
                flowAnalysis, completeFlowPreview,
                new Dictionary<string, string> { [flowTargetPath] = flattened },
                Passed("syntax"), Passed("compile"), "flow-edited", "flow-target-hash");
            var requiredFlow = new[] {
                "if_guard", "else_path", "try_scope", "catch_path",
                "throw_path", "finally_path"
            };
            Check(lostFlow.Status == MigrationAcceptanceGate.RejectedIncomplete
                && requiredFlow.All(kind => lostFlow.Lineage.Any(row =>
                    row.Kind == kind && row.Status == "missing_target"))
                && lostFlow.Lineage.Where(row =>
                    row.Kind is "action" or "assertion" or "locator")
                    .All(row => row.Status == "candidate_unverified")
                && lostFlow.BehavioralEquivalence.Status == "not_run",
                "flattened branches and failure paths must reject despite matching actions/assertions");

            var structured = """
                using NUnit.Framework;
                [TestFixture] public class FlowFixturePlaywright {
                    [Test] public void GuardedClick() {
                        if (true) {
                            Page.Locator("#save").ClickAsync();
                        } else {
                            Assert.Fail("blocked");
                        }
                        try {
                            Page.Locator("#retry").ClickAsync();
                        } catch (System.InvalidOperationException) {
                            throw;
                        } finally {
                            Assert.That(true, Is.True);
                        }
                    }
                }
                """;
            var preservedShape = MigrationAcceptanceGate.Evaluate(
                flowAnalysis, completeFlowPreview,
                new Dictionary<string, string> { [flowTargetPath] = structured },
                Passed("syntax"), Passed("compile"), "structured-edited", "structured-target-hash");
            Check(preservedShape.Status == MigrationAcceptanceGate.BlockedUnverified
                && preservedShape.Lineage.Where(row => requiredFlow.Contains(row.Kind))
                    .All(row => row.Status == "candidate_unverified")
                && preservedShape.BehavioralEquivalence.Status == "not_run",
                "a matching branch shape is not behavior equivalence");
            var changedGuard = MigrationAcceptanceGate.Evaluate(
                flowAnalysis, completeFlowPreview,
                new Dictionary<string, string> {
                    [flowTargetPath] = structured.Replace("if (true)", "if (false)", StringComparison.Ordinal)
                },
                Passed("syntax"), Passed("compile"), "changed-guard", "changed-guard-hash");
            Check(changedGuard.Status != "accepted"
                && changedGuard.BehavioralEquivalence.Status == "not_run"
                && changedGuard.Lineage.Any(row =>
                    row.Kind == "if_guard" && row.Status == "candidate_unverified"),
                "guard condition changed with same AST shape cannot silently become accepted");

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
