namespace Migrator.Application;

/// <summary>Public DTOs intentionally do not expose mutable Roslyn/renderer objects.</summary>
public static class ContractVersions
{
    public const string Analysis = "migrator-analysis/v1alpha1";
    public const string Preview = "migrator-preview/v1alpha1";
    public const string Explanation = "migrator-explanation/v1alpha1";
    public const string Verification = "migrator-verification/v1alpha1";
}

public sealed record MigrationFinding(
    string Id,
    string Code,
    string SourcePath,
    int Line,
    string Message,
    string EvidenceKind);

/// <summary>Static source-backed evidence, never an NUnit discovery or execution claim.</summary>
public sealed record NUnitAttributeEvidence(
    string SourcePath, int Line, string Kind, string SourceSha256,
    string TargetExpression, bool RequiresReview, string? Reason);

public sealed record NUnitCaseInventory(
    string SourcePath, int Line, string Method, string TargetExpression,
    string RunState, string? Reason);

public sealed record FileSurvey(
    string RelativePath,
    string Disposition,
    int DeclaredTests,
    int ParsedTests,
    IReadOnlyList<string> TestNames,
    IReadOnlyList<MigrationFinding> Findings)
{
    public IReadOnlyList<NUnitAttributeEvidence> NUnitAttributes { get; init; }
        = Array.Empty<NUnitAttributeEvidence>();
    public IReadOnlyList<NUnitCaseInventory> NUnitCases { get; init; }
        = Array.Empty<NUnitCaseInventory>();
}

public sealed record MigrationAnalysis(
    string SchemaVersion,
    string AnalysisId,
    string SourceScope,
    string SourceTreeHash,
    string ConfigurationHash,
    string DiscoveryStatus,
    int DeclaredTests,
    int ParsedTests,
    IReadOnlyList<FileSurvey> Files,
    IReadOnlyList<MigrationFinding> Findings,
    IReadOnlyList<string> Limitations)
{
    public int StaticallyResolvedNUnitCases => Files.Sum(file => file.NUnitCases.Count);
    public int BlockedNUnitSources => Files.Sum(file =>
        file.NUnitAttributes.Count(attribute => attribute.RequiresReview));
}

public sealed record GeneratedFile(
    string SourcePath,
    string RelativePath,
    string Content,
    string ContentSha256,
    int Tests,
    int FullyConvertedTests,
    int TodoComments);

public sealed record MigrationPreview(
    string SchemaVersion,
    string AnalysisId,
    string CandidateId,
    string TargetTreeHash,
    string TransformationStatus,
    IReadOnlyList<GeneratedFile> Files,
    IReadOnlyList<MigrationFinding> Findings)
{
    public IReadOnlyList<NUnitAttributeEvidence> NUnitAttributes { get; init; }
        = Array.Empty<NUnitAttributeEvidence>();
    public IReadOnlyList<NUnitCaseInventory> NUnitCases { get; init; }
        = Array.Empty<NUnitCaseInventory>();
}

public sealed record SourceExcerptLine(int Line, string Text);

/// <summary>
/// Evidence grounded in the current analysis snapshot. A recommendation is not a proof
/// of source/target semantic equivalence.
/// </summary>
public sealed record FindingExplanation(
    string SchemaVersion,
    string AnalysisId,
    string FindingId,
    string Code,
    string Message,
    string EvidenceKind,
    string SourcePath,
    int Line,
    IReadOnlyList<SourceExcerptLine> SourceExcerpt,
    string SuggestedNextAction,
    string? CandidateId,
    IReadOnlyList<string> Limitations);

public sealed record CandidateFileEvidence(string RelativePath, string ContentSha256, long ByteLength);

/// <summary>Content-addressed revision of exact target file bytes.</summary>
public sealed record CandidateRevision(
    string CandidateId,
    string? ParentCandidateId,
    string Origin,
    string AnalysisId,
    string SourceTreeHash,
    string ConfigurationHash,
    string TargetTreeHash,
    IReadOnlyList<CandidateFileEvidence> Files);

public sealed record VerificationDiagnostic(
    string Code,
    string RelativePath,
    int Line,
    int Column,
    string Message);

/// <summary>Independent evidence for one verification level; not a combined migration-success flag.</summary>
public sealed record VerificationStage(
    string Status,
    string EvidenceKind,
    IReadOnlyList<VerificationDiagnostic> Diagnostics);

public sealed record ArtifactVerification(
    string SchemaVersion,
    CandidateRevision Candidate,
    VerificationStage Syntax,
    VerificationStage Compile,
    VerificationStage Runtime,
    VerificationStage Observables,
    string? ReferenceContextHash,
    IReadOnlyList<string> Limitations);
