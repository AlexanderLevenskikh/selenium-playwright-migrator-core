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
public sealed record PomExternalExceptionContractPlan(
    string SchemaVersion, string SourceTreeHash, string Status,
    string SourceType, string SourceMethod, string SourceMethodSha256,
    IReadOnlyList<PomExternalExceptionCatchCandidate> CatchCandidates,
    IReadOnlyList<PomActionBlocker> Blockers,
    IReadOnlyList<string> Limitations)
{
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
            sourceType, sourceMethod, sourceLine, cancellationToken);
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
            result.Limitations);
    }
}
