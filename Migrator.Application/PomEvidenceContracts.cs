namespace Migrator.Application;

public sealed record LocatorPredicateEvidence(
    string SourceMethod,
    string HtmlAttribute,
    string LiteralValue,
    string HelperSourcePath,
    int HelperLine);

public sealed record PomLocatorEvidence(
    string SourceType,
    string SourceMemberPath,
    string SourcePath,
    int SourceLine,
    string ControlType,
    string SelectorExpression,
    IReadOnlyList<LocatorPredicateEvidence> Predicates,
    string? ProposedPlaywrightExpression,
    string ProposalStatus,
    string Rationale);

public sealed record SourceHelperEvidence(
    string SourceType,
    string Method,
    string SourcePath,
    int SourceLine,
    IReadOnlyList<string> CalledMethods,
    IReadOnlyList<string> LiteralStorageKeys,
    string Classification,
    string RequiredReview);

public sealed record PomSourceInspection(
    string SchemaVersion,
    string SourceScope,
    string SourceTreeHash,
    int InspectedFiles,
    int TotalLocatorMembers,
    IReadOnlyList<PomLocatorEvidence> Locators,
    IReadOnlyList<SourceHelperEvidence> Helpers,
    IReadOnlyList<string> Limitations);
