namespace Migrator.Application;

/// <summary>
/// Exact content-hashed target assembly preview, including one SDK .csproj,
/// all generated NUnit fixture and SetUpFixture source files, and a manifest.
/// Ready for build does NOT mean runtime or business equivalence.
/// </summary>
public sealed record TargetAssemblyFile(
    string RelativePath,
    string Content,
    string Sha256,
    string? SourcePath);

public sealed record TargetAssemblyIssue(
    string Code,
    string Severity,
    string Explanation,
    string? SourcePath = null);

public sealed record TargetAssemblySchedulingSource(
    string SourcePath,
    int SourceLine,
    string SourceExpression,
    string TargetExpression);

public sealed record TargetAssemblyPlan(
    string SchemaVersion,
    string SourceRoot,
    string AnalysisId,
    string CandidateId,
    string SourceTreeHash,
    string PreviewTargetTreeHash,
    string TargetTreeHash,
    string ProjectFile,
    string ManifestFile,
    string Status,
    int DeclaredTests,
    int ScopedSetUpFixtures,
    IReadOnlyList<TargetAssemblyFile> Files,
    IReadOnlyList<TargetAssemblyIssue> Issues,
    IReadOnlyList<string> Limitations,
    IReadOnlyList<TargetAssemblySchedulingSource>? AssemblyScheduling = null)
{
    /// <summary>Static estimate only; neither NUnit discovery nor runtime.</summary>
    public IReadOnlyList<NUnitCaseInventory> NUnitCases { get; init; }
        = Array.Empty<NUnitCaseInventory>();
}

/// <summary>
/// Stage-specific evidence. An NUnit run is reported only after dotnet test
/// actually executes tests and a TRX confirms positive, fully passing counts.
/// </summary>
public sealed record TargetAssemblyStage(
    string Status,
    string EvidenceKind,
    string Details,
    int? ExitCode = null,
    int? ExecutedTests = null,
    int? PassedTests = null);

public sealed record TargetAssemblyVerification(
    string SchemaVersion,
    string AnalysisId,
    string CandidateId,
    string TargetTreeHash,
    string TargetRoot,
    string Status,
    TargetAssemblyStage Completeness,
    TargetAssemblyStage Build,
    TargetAssemblyStage NUnitRuntime,
    IReadOnlyList<TargetAssemblyIssue> Issues,
    IReadOnlyList<string> Limitations);
