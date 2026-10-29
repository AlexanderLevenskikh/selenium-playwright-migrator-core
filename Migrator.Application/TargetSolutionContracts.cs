namespace Migrator.Application;

/// <summary>One target assembly. Each NUnit test project keeps its own
/// global and namespace SetUpFixture scope; library projects are independent.</summary>
public sealed record TargetSolutionPackage(string Id, string Version);

public sealed record TargetSolutionProject(
    string SourceProject,
    string ProjectFile,
    string AssemblyName,
    string Kind,
    IReadOnlyList<string> ProjectReferences,
    int DeclaredTests,
    int ScopedSetUpFixtures,
    string? AnalysisId,
    string? CandidateId,
    IReadOnlyList<TargetSolutionPackage>? PackageReferences = null);

public sealed record TargetSolutionFile(
    string RelativePath,
    string Content,
    string Sha256,
    string? SourcePath);

public sealed record TargetSolutionSourceScope(
    bool Explicit,
    IReadOnlyList<string> EntryProjects,
    IReadOnlyList<string> IncludedProjects,
    IReadOnlyList<string> ExcludedProjects);

public sealed record TargetSolutionPlan(
    string SchemaVersion,
    string SourceRoot,
    string SourceTreeHash,
    string ConfigurationHash,
    string PlanId,
    string TargetTreeHash,
    string SolutionFile,
    string ManifestFile,
    string Status,
    IReadOnlyList<TargetSolutionProject> Projects,
    IReadOnlyList<TargetSolutionFile> Files,
    IReadOnlyList<TargetAssemblyIssue> Issues,
    IReadOnlyList<string> Limitations,
    TargetSolutionSourceScope? Scope = null);

public sealed record TargetSolutionProjectExecution(
    string ProjectFile,
    string Kind,
    TargetAssemblyStage NUnitRuntime);

public sealed record TargetSolutionVerification(
    string SchemaVersion,
    string PlanId,
    string SourceTreeHash,
    string TargetTreeHash,
    string TargetRoot,
    string Status,
    TargetAssemblyStage Completeness,
    TargetAssemblyStage Build,
    TargetAssemblyStage NUnitRuntime,
    IReadOnlyList<TargetSolutionProjectExecution> ProjectExecutions,
    IReadOnlyList<TargetAssemblyIssue> Issues,
    IReadOnlyList<string> Limitations);
