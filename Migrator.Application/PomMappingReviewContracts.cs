namespace Migrator.Application;

/// <summary>
/// A necessary source action or control-flow shape; a matching action category
/// cannot establish browser or business semantic equivalence.
/// </summary>
public sealed record PomMappingObligation(
    string Id,
    int SourceLine,
    string SourceCall,
    string Effect,
    string Provenance);

public sealed record PomMappingTargetEffect(
    int Sequence,
    int TargetLine,
    string Effect,
    string TargetCall);

public sealed record PomMappingReview(
    string SchemaVersion,
    string SourceTreeHash,
    string SourceType,
    string SourceMethod,
    int SourceLine,
    string SourceMethodSha256,
    string Status,
    IReadOnlyList<PomMappingObligation> Obligations,
    IReadOnlyList<PomMappingTargetEffect> TargetEffects,
    IReadOnlyList<PomMappingObligation> MissingObligations,
    IReadOnlyList<string> ReviewFlags,
    IReadOnlyList<string> Limitations)
{
    public bool BehaviorallyVerified => false;
}
