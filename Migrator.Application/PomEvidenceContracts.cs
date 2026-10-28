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
    string RequiredReview)
{
    /// <summary>Outermost call expressions in lexical source order, not a runtime trace.</summary>
    public IReadOnlyList<PomMethodCallEvidence> OrderedCalls { get; init; } = Array.Empty<PomMethodCallEvidence>();
    public IReadOnlyList<PomControlFlowEvidence> ControlFlow { get; init; } = Array.Empty<PomControlFlowEvidence>();
    public string MethodSha256 { get; init; } = string.Empty;
    public bool IsReachablePrivateHelper { get; init; }
}

public sealed record PomMethodCallEvidence(
    int Order,
    int SourceLine,
    string Receiver,
    string Method,
    IReadOnlyList<string> CallChain,
    string SourceText,
    string Role,
    bool InsideConditional,
    bool InsideLoop,
    bool InsideExceptionHandler);

public sealed record PomControlFlowEvidence(string Kind, int SourceLine, string Expression);

public sealed record PomStorageOrderEvidence(int SourceLine, string Method, string RelativeToNavigation, string SourceText);

public sealed record PomTestFlowEvidence(
    string Fixture,
    string Method,
    string SourcePath,
    int SourceLine,
    bool IsSetup,
    bool HasNavigationCall,
    bool HasComplexFlow,
    IReadOnlyList<PomStorageOrderEvidence> StorageOperations,
    string RuntimeOrderQualification);

public sealed record PomSourceInspection(
    string SchemaVersion,
    string SourceScope,
    string SourceTreeHash,
    int InspectedFiles,
    int TotalLocatorMembers,
    IReadOnlyList<PomLocatorEvidence> Locators,
    IReadOnlyList<SourceHelperEvidence> Helpers,
    IReadOnlyList<string> Limitations)
{
    /// <summary>Syntax-only storage/navigation order, including NUnit setup methods.</summary>
    public IReadOnlyList<PomTestFlowEvidence> StorageAndNavigationFlows { get; init; } = Array.Empty<PomTestFlowEvidence>();
}
