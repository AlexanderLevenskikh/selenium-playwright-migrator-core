using System.Security.Cryptography;
using System.Text;
using Migrator.Core;
using Migrator.Roslyn;

namespace Migrator.Application;

public sealed record PomExternalExceptionCatchCandidate(
    string CatchSourcePath, int CatchSourceLine, string CatchSourceSha256,
    string SourceCaughtType, string TargetCaughtType,
    string SourceCallerSymbol, string CallerChain,
    string TargetCatchClause, string TargetCatchSha256);

/// <summary>
/// Review-only source-backed external catch clause plan. Never automatically
/// updates NUnit, TestBase or POM methods. A candidate is only a C# catch
/// clause, not a complete translated test or a runtime exception adapter.
/// </summary>
public sealed record PomExternalAsyncMethodCandidate(
    string SourcePath, int SourceLine, string SourceSha256,
    string SourceSymbol, string TargetSignature, string TargetBody);

public sealed record PomExternalFixtureLifecycleCandidate(
    string FixtureSourcePath, string FixtureSourceSha256,
    string PageConstructorSha256, string OneTimeSetUpSha256,
    string SetUpSha256, string TearDownSha256, string OneTimeTearDownSha256,
    string PageField, string ContextField, string BrowserField,
    string PlaywrightField, string FixtureType,
    bool ContextFromBrowser, bool PageFromContext,
    bool PageAndContextConstructorInjected,
    bool PerTestCleanupInFinally, bool OneTimeCleanupInFinally)
{
    public bool OneTimeSetUpFailureCleanup { get; init; }
    public bool SetUpFailureCleanup { get; init; }
    public bool PrimaryExceptionRethrown { get; init; }
    public bool CleanupFailuresDoNotReplacePrimary { get; init; }
    public bool SafeSecondaryDiagnosticCodes { get; init; }
    public bool DiagnosticSinkFailurePreservesPrimary { get; init; }
    public string DiagnosticSurface { get; init; } = "";
    public bool SourceSinkNotRebound { get; init; }
    public bool FaultWitnessSourceOnly { get; init; }
    public bool RealCleanupFailureCertified { get; init; }
    public bool OsProcessAncestryCertified { get; init; }
    public bool TypedTargetClosedTearDownSourceOnly { get; init; }
    public bool PageTargetClosedCatchGuarded { get; init; }
    public bool ContextTargetClosedCatchGuarded { get; init; }
    public bool TestFailurePrecedenceOnlyForTargetClosed { get; init; }
    public string PageTargetClosedCatchSha256 { get; init; } = "";
    public string ContextTargetClosedCatchSha256 { get; init; } = "";
    public int PageTargetClosedCatchLine { get; init; }
    public int ContextTargetClosedCatchLine { get; init; }
}

public sealed record PomExternalExceptionContractPlan(
    string SchemaVersion, string SourceTreeHash, string Status,
    string SourceType, string SourceMethod, string SourceMethodSha256,
    IReadOnlyList<PomExternalExceptionCatchCandidate> CatchCandidates,
    IReadOnlyList<PomActionBlocker> Blockers,
    IReadOnlyList<string> Limitations)
{
    public IReadOnlyList<PomExternalAsyncMethodCandidate> AsyncMethods { get; init; }
        = Array.Empty<PomExternalAsyncMethodCandidate>();
    public string VerifiedPageReceiver { get; init; } = "";
    public PomExternalFixtureLifecycleCandidate? FixtureLifecycle { get; init; }
    public bool RuntimeVerified => false;
    public bool CanAutoApply => false;
    public bool RequiresReview => true;
    public string Execution => "read_only_not_run";
}

public sealed partial class MigrationEngine
{
    public PomExternalExceptionContractPlan PlanPomExternalExceptionContract(
        string sourceDirectory, string sourceType, string sourceMethod,
        string expectedSourceTreeHash, int sourceLine = 0,
        CancellationToken cancellationToken = default)
        => PlanExternalCatchCore(sourceDirectory, sourceType, sourceMethod,
            expectedSourceTreeHash, sourceLine, false, "", cancellationToken);

    /// <summary>
    /// Dedicated preview.41 opt-in review-only projection. Requires explicit
    /// source-typed constructor-bound IPage receiver, NUnit [Test] and exact
    /// Page Object -> TestBase -> NUnit forwarding path.
    /// </summary>
    public PomExternalExceptionContractPlan PlanPomExternalAsyncCountContract(
        string sourceDirectory, string sourceType, string sourceMethod,
        string expectedSourceTreeHash, string explicitPageReceiver,
        int sourceLine = 0, CancellationToken cancellationToken = default)
        => PlanExternalCatchCore(sourceDirectory, sourceType, sourceMethod,
            expectedSourceTreeHash, sourceLine, true, explicitPageReceiver,
            cancellationToken);

    /// <summary>
    /// Preview.42-A: source-attested, opt-in NUnit fixture ownership and
    /// Playwright page/context constructor flow. Still review-only.
    /// </summary>
    public PomExternalExceptionContractPlan PlanPomExternalAsyncLifecycleCountContract(
        string sourceDirectory, string sourceType, string sourceMethod,
        string expectedSourceTreeHash, string pageField, string contextField,
        string browserField, string playwrightField,
        int sourceLine = 0, CancellationToken cancellationToken = default)
        => PlanExternalCatchCore(sourceDirectory, sourceType, sourceMethod,
            expectedSourceTreeHash, sourceLine, true, pageField,
            cancellationToken, true, contextField, browserField,
            playwrightField);

    /// <summary>
    /// preview.43: opt-in source-only failure-atomic OneTimeSetUp/SetUp.
    /// Original exceptions win even if both resource cleanup attempts fail.
    /// </summary>
    public PomExternalExceptionContractPlan PlanPomExternalFailureAtomicCountContract(
        string sourceDirectory, string sourceType, string sourceMethod,
        string expectedSourceTreeHash, string pageField, string contextField,
        string browserField, string playwrightField,
        int sourceLine = 0, CancellationToken cancellationToken = default)
        => PlanExternalCatchCore(sourceDirectory, sourceType, sourceMethod,
            expectedSourceTreeHash, sourceLine, true, pageField,
            cancellationToken, true, contextField, browserField,
            playwrightField, true);

    /// <summary>
    /// preview.44-A: fixed diagnostic codes only (no exception messages,
    /// selectors, source text or dynamic content); local read-only review.
    /// </summary>
    public PomExternalExceptionContractPlan PlanPomSafeCleanupDiagnosticsContract(
        string sourceDirectory, string sourceType, string sourceMethod,
        string expectedSourceTreeHash, string pageField, string contextField,
        string browserField, string playwrightField,
        int sourceLine = 0, CancellationToken cancellationToken = default)
        => PlanExternalCatchCore(sourceDirectory, sourceType, sourceMethod,
            expectedSourceTreeHash, sourceLine, true, pageField,
            cancellationToken, true, contextField, browserField,
            playwrightField, true, true);

    /// <summary>
    /// preview.45-A: captured-source-only fault witness. Explicitly does
    /// NOT certify actual browser exceptions or process ancestry.
    /// </summary>
    public PomExternalExceptionContractPlan PlanPomCleanupFaultWitness(
        string sourceDirectory, string sourceType, string sourceMethod,
        string expectedSourceTreeHash, string pageField, string contextField,
        string browserField, string playwrightField,
        int sourceLine = 0, CancellationToken cancellationToken = default)
        => PlanExternalCatchCore(sourceDirectory, sourceType, sourceMethod,
            expectedSourceTreeHash, sourceLine, true, pageField,
            cancellationToken, true, contextField, browserField,
            playwrightField, true, true, true);

    /// <summary>
    /// Preview.46: exact typed real Playwright TargetClosedException only,
    /// per-test NUnit TearDown Page/Context, never auto-applied or executed.
    /// </summary>
    public PomExternalExceptionContractPlan PlanPomTypedTargetClosedTearDown(
        string sourceDirectory, string sourceType, string sourceMethod,
        string expectedSourceTreeHash, string pageField, string contextField,
        string browserField, string playwrightField,
        int sourceLine = 0, CancellationToken cancellationToken = default)
        => PlanExternalCatchCore(sourceDirectory, sourceType, sourceMethod,
            expectedSourceTreeHash, sourceLine, true, pageField,
            cancellationToken, true, contextField, browserField,
            playwrightField, true, true, true, true);

    private PomExternalExceptionContractPlan PlanExternalCatchCore(
        string sourceDirectory, string sourceType, string sourceMethod,
        string expectedSourceTreeHash, int sourceLine, bool verifiedAsyncCount,
        string explicitPageReceiver, CancellationToken cancellationToken,
        bool requireFixtureLifecycle = false,
        string explicitContextReceiver = "", string explicitBrowserReceiver = "",
        string explicitPlaywrightReceiver = "",
        bool requireFailureAtomic = false,
        bool requireSafeDiagnostics = false,
        bool requireFaultWitness = false,
        bool requireTypedTargetClosedTearDown = false)
    {
        if (string.IsNullOrWhiteSpace(expectedSourceTreeHash))
            throw new ArgumentException(
                "EXPECTED_POM_SOURCE_HASH_REQUIRED: inspect_pom_sources first.");

        var root = Path.GetFullPath(sourceDirectory);
        var inspector = new PomSourceInspector();
        var source = inspector.Inspect(root, cancellationToken: cancellationToken);
        if (!string.Equals(source.SourceTreeHash, expectedSourceTreeHash,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "STALE_POM_EXCEPTION_CONTRACT: source snapshot differs from the caller-supplied SHA.");

        // Re-read all files under the same bounded source rules as POM
        // inspection. The plan never modifies or executes customer C#.
        var files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(root, path).Split(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part is "bin" or "obj" or ".git"
                    or "node_modules" or "private-generated"))
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (files.Length != source.InspectedFiles)
            throw new InvalidOperationException(
                "POM_SCOPE_CHANGED: source files changed during exception contract planning.");

        var utf8 = new UTF8Encoding(false, true);
        var snapshot = files.Select(file =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("POM_SYMLINK_REJECTED");
            var bytes = File.ReadAllBytes(file);
            return (Path: Path.GetRelativePath(root, file).Replace('\\', '/'),
                Bytes: bytes, Source: utf8.GetString(bytes));
        }).ToArray();
        if (ContentTreeHasher.ComputeBytes(snapshot.Select(x =>
                (x.Path, x.Bytes))) != expectedSourceTreeHash)
            throw new InvalidOperationException(
                "POM_SOURCE_CHANGED_DURING_CONTRACT: source bytes changed.");

        var result = SemanticPomActionProjector.ProjectExternalExceptionContract(
            snapshot.Select(x => (x.Path, x.Source)).ToArray(),
            sourceType, sourceMethod, sourceLine, cancellationToken,
            verifiedAsyncCount, explicitPageReceiver,
            requireFixtureLifecycle, explicitContextReceiver,
            explicitBrowserReceiver, explicitPlaywrightReceiver,
            requireFailureAtomic, requireSafeDiagnostics,
            requireFaultWitness,
            requireTypedTargetClosedTearDown);
        if (inspector.Inspect(root, cancellationToken: cancellationToken)
            .SourceTreeHash != expectedSourceTreeHash)
            throw new InvalidOperationException(
                "POM_SOURCE_CHANGED_DURING_CONTRACT: source bytes changed.");

        return new PomExternalExceptionContractPlan(
            result.SchemaVersion, expectedSourceTreeHash, result.Status,
            sourceType, sourceMethod, result.SourceMethodSha256,
            result.ExternalCatches.Select(c => new PomExternalExceptionCatchCandidate(
                c.CatchSourcePath, c.CatchSourceLine, c.CatchSourceSha256,
                c.SourceCaughtType, c.TargetCaughtType, c.SourceCallerSymbol,
                c.CallerChain, c.TargetCatchClause, c.TargetCatchSha256)).ToArray(),
            result.Blockers.Select(b => new PomActionBlocker(
                b.Code, b.SourcePath, b.SourceLine,
                b.SourceSha256, b.Explanation)).ToArray(),
            result.Limitations)
        {
            AsyncMethods = result.AsyncMethods.Select(m =>
                new PomExternalAsyncMethodCandidate(
                    m.SourcePath, m.SourceLine, m.SourceSha256,
                    m.SourceSymbol, m.TargetSignature, m.TargetBody)).ToArray(),
            VerifiedPageReceiver = result.VerifiedPageReceiver,
            FixtureLifecycle = result.FixtureLifecycle == null ? null
                : new PomExternalFixtureLifecycleCandidate(
                    result.FixtureLifecycle.FixtureSourcePath,
                    result.FixtureLifecycle.FixtureSourceSha256,
                    result.FixtureLifecycle.PageConstructorSha256,
                    result.FixtureLifecycle.OneTimeSetUpSha256,
                    result.FixtureLifecycle.SetUpSha256,
                    result.FixtureLifecycle.TearDownSha256,
                    result.FixtureLifecycle.OneTimeTearDownSha256,
                    result.FixtureLifecycle.PageField,
                    result.FixtureLifecycle.ContextField,
                    result.FixtureLifecycle.BrowserField,
                    result.FixtureLifecycle.PlaywrightField,
                    result.FixtureLifecycle.FixtureType,
                    result.FixtureLifecycle.ContextFromBrowser,
                    result.FixtureLifecycle.PageFromContext,
                    result.FixtureLifecycle.PageAndContextConstructorInjected,
                    result.FixtureLifecycle.PerTestCleanupInFinally,
                    result.FixtureLifecycle.OneTimeCleanupInFinally)
                {
                    OneTimeSetUpFailureCleanup =
                        result.FixtureLifecycle.OneTimeSetUpFailureCleanup,
                    SetUpFailureCleanup =
                        result.FixtureLifecycle.SetUpFailureCleanup,
                    PrimaryExceptionRethrown =
                        result.FixtureLifecycle.PrimaryExceptionRethrown,
                    CleanupFailuresDoNotReplacePrimary =
                        result.FixtureLifecycle.CleanupFailuresDoNotReplacePrimary,
                    SafeSecondaryDiagnosticCodes =
                        result.FixtureLifecycle.SafeSecondaryDiagnosticCodes,
                    DiagnosticSinkFailurePreservesPrimary =
                        result.FixtureLifecycle.DiagnosticSinkFailurePreservesPrimary,
                    DiagnosticSurface = result.FixtureLifecycle.DiagnosticSurface,
                    SourceSinkNotRebound = result.FixtureLifecycle.SourceSinkNotRebound,
                    FaultWitnessSourceOnly = result.FixtureLifecycle.FaultWitnessSourceOnly,
                    RealCleanupFailureCertified = result.FixtureLifecycle.RealCleanupFailureCertified,
                    OsProcessAncestryCertified = result.FixtureLifecycle.OsProcessAncestryCertified,
                    TypedTargetClosedTearDownSourceOnly =
                        result.FixtureLifecycle.TypedTargetClosedTearDownSourceOnly,
                    PageTargetClosedCatchGuarded =
                        result.FixtureLifecycle.PageTargetClosedCatchGuarded,
                    ContextTargetClosedCatchGuarded =
                        result.FixtureLifecycle.ContextTargetClosedCatchGuarded,
                    TestFailurePrecedenceOnlyForTargetClosed =
                        result.FixtureLifecycle.TestFailurePrecedenceOnlyForTargetClosed,
                    PageTargetClosedCatchSha256 =
                        result.FixtureLifecycle.PageTargetClosedCatchSha256,
                    ContextTargetClosedCatchSha256 =
                        result.FixtureLifecycle.ContextTargetClosedCatchSha256,
                    PageTargetClosedCatchLine =
                        result.FixtureLifecycle.PageTargetClosedCatchLine,
                    ContextTargetClosedCatchLine =
                        result.FixtureLifecycle.ContextTargetClosedCatchLine
                }
        };
    }
}
