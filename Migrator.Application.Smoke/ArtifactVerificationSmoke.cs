using Migrator.Application;

static class ArtifactVerificationSmoke
{
    public static void Run(MigrationEngine engine, string scope, MigrationAnalysis analysis, MigrationPreview preview)
    {
        Check(preview.Files.Count == 1, "one file needed for verification");
        var generated = engine.VerifyArtifact(scope, analysis.AnalysisId, preview.CandidateId);
        Check(generated.Candidate.CandidateId == preview.CandidateId, "generated candidate identity");
        Check(generated.Candidate.TargetTreeHash == preview.TargetTreeHash, "generated target hash");
        Check(generated.Candidate.Origin == "generated", "generated origin");
        Check(generated.Compile.Status == "not_run", "compile must never pass implicitly");
        Check(generated.Runtime.Status == "not_run" && generated.Observables.Status == "not_run",
            "no runtime or observables verification in syntax check");

        var targetDir = Path.Combine(Path.GetTempPath(), "migrator-target-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(targetDir);
        try
        {
            var name = preview.Files.Single().RelativePath;
            var file = Path.Combine(targetDir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, preview.Files.Single().Content);
            var identical = engine.VerifyArtifact(scope, analysis.AnalysisId, preview.CandidateId, targetDir);
            Check(identical.Candidate.CandidateId == preview.CandidateId, "byte-identical disk candidate id");
            Check(identical.Candidate.Origin == "generated", "identical bytes are not an edit");
            File.WriteAllText(file, "public sealed class AgentEdit { public int Value => 123; }");
            var edited = engine.VerifyArtifact(scope, analysis.AnalysisId, preview.CandidateId,
                targetDirectory: targetDir, runCompile: true, compilationReferencePaths: Array.Empty<string>());
            Check(edited.Candidate.Origin == "agent-edited" && edited.Candidate.ParentCandidateId == preview.CandidateId,
                "edited candidate must identify its parent");
            Check(edited.Candidate.CandidateId != preview.CandidateId, "new revision must have new id");
            Check(edited.Syntax.Status == "passed" && edited.Compile.Status == "passed", "edited self-contained code must compile");
            Check(edited.ReferenceContextHash is { Length: 64 }, "reference context must be captured");
            Check(edited.Runtime.Status == "not_run" && edited.Observables.Status == "not_run", "compile is not runtime");

            var unconfigured = engine.VerifyArtifact(scope, analysis.AnalysisId, preview.CandidateId,
                targetDirectory: targetDir, runCompile: true);
            Check(unconfigured.Compile.Status == "blocked", "missing compile reference profile must block");
            File.WriteAllText(file, "public sealed class AgentEdit { public int Value => MissingMember; }");
            var semanticError = engine.VerifyArtifact(scope, analysis.AnalysisId, preview.CandidateId,
                targetDirectory: targetDir, runCompile: true, compilationReferencePaths: Array.Empty<string>());
            Check(semanticError.Syntax.Status == "passed" && semanticError.Compile.Status == "failed",
                "semantic compilation error must fail, not be misclassified as syntax");
            File.WriteAllText(file, "public sealed class Broken { ");
            var syntaxError = engine.VerifyArtifact(scope, analysis.AnalysisId, preview.CandidateId,
                targetDirectory: targetDir, runCompile: true, compilationReferencePaths: Array.Empty<string>());
            Check(syntaxError.Syntax.Status == "failed" && syntaxError.Compile.Status == "blocked",
                "invalid syntax must block compile stage");
            Check(syntaxError.Candidate.CandidateId != edited.Candidate.CandidateId,
                "invalid edit cannot inherit previously verified identity");

            File.WriteAllText(Path.Combine(targetDir, "Unexpected.cs"), "public class Extra {}");
            try
            {
                engine.VerifyArtifact(scope, analysis.AnalysisId, preview.CandidateId, targetDir);
                throw new Exception("unexpected target file was accepted");
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("TARGET_FILE_SET_MISMATCH", StringComparison.Ordinal)) {}
            File.Delete(Path.Combine(targetDir, "Unexpected.cs"));

            try
            {
                engine.VerifyArtifact(scope, analysis.AnalysisId, "bad-parent", targetDir);
                throw new Exception("unknown parent candidate was accepted");
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("STALE_CANDIDATE", StringComparison.Ordinal)) {}
        }
        finally { Directory.Delete(targetDir, recursive: true); }
    }

    static void Check(bool value, string description)
    {
        if (!value) throw new InvalidOperationException("Artifact verification smoke failed: " + description);
    }
}
