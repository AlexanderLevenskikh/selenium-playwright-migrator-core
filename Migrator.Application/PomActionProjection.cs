using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Migrator.Core;
using Migrator.Roslyn;

namespace Migrator.Application;

/// <summary>
/// Additive read-only planning API. Projection is explicitly review-required;
/// agent-generated config must never be promoted to a completed migration from
/// a source-only compiler pass or an operation-count comparison.
/// </summary>
public sealed record PomProjectedAction(
    int Order, string SourcePath, int SourceLine, string SourceSha256,
    string SourceSymbol, string ReceiverType, string SourceReturnType,
    string Effect, string TargetStatement, string TargetSha256, bool RequiresReview);

public sealed record PomActionBlocker(
    string Code, string SourcePath, int SourceLine, string SourceSha256,
    string Explanation);

public sealed record PomActionPlan(
    string SchemaVersion, string SourceTreeHash, string Status,
    string SourceType, string SourceMethod, string SourceMethodSymbol,
    string SourceMethodSha256, string SourcePath, int SourceLine,
    string SuggestedSourcePattern,
    IReadOnlyList<PomProjectedAction> Actions,
    IReadOnlyList<PomActionBlocker> Blockers,
    IReadOnlyList<string> TargetStatements,
    IReadOnlyList<string> Limitations)
{
    public bool RuntimeVerified => false;
    public bool CanAutoApply => false;
    public int ProvenSourceActions => Actions.Count;
    public PomReviewedAdapterCandidate? AdapterCandidate { get; init; }
}

/// <summary>
/// An opt-in adapter candidate, never an automatically installed mapping.
/// Roslyn source parameters become explicit {placeholder} references only at
/// identifier tokens in translated target syntax, never inside URL/selector
/// string literal content.
/// </summary>
public sealed record PomReviewedAdapterCandidate(
    string SourceMethodPattern, IReadOnlyList<string> TargetStatementTemplates,
    bool RequiresReview, string SourceMethodSha256);

/// <summary>Project-scoped AST projection over stable, exactly captured UTF-8 bytes.</summary>
public sealed partial class MigrationEngine
{
    private static string ToAdapterTemplate(
        string statement, string sourcePattern)
    {
        // Source method pattern was emitted solely from source parameters:
        // page.Method({name}, ...). Never interpolate arbitrary expression
        // content into a selector string or C# statement without parsing.
        var parameters = System.Text.RegularExpressions.Regex.Matches(
            sourcePattern, @"\{(?<name>[A-Za-z_]\w*)\}")
            .Cast<System.Text.RegularExpressions.Match>()
            .Select(match => match.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);
        if (parameters.Count == 0)
            return statement;
        var parsed = SyntaxFactory.ParseStatement(statement);
        if (parsed.ContainsDiagnostics)
            throw new InvalidOperationException(
                "POM_ADAPTER_TEMPLATE_PARSE_FAILED: target statement must parse before parameter binding.");
        var references = parsed.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Where(id => parameters.Contains(id.Identifier.ValueText)
                && !(id.Parent is MemberAccessExpressionSyntax member
                     && ReferenceEquals(member.Name, id)))
            .OrderByDescending(id => id.SpanStart)
            .ToArray();
        var value = statement;
        foreach (var id in references)
            value = value[..id.SpanStart] + "{" + id.Identifier.ValueText + "}"
                + value[id.Span.End..];
        return value;
    }

    public PomActionPlan PlanPomActions(
        string sourceDirectory, string sourceType, string sourceMethod,
        string expectedSourceTreeHash, int sourceLine = 0,
        string receiver = "page", CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedSourceTreeHash))
            throw new ArgumentException(
                "EXPECTED_POM_SOURCE_HASH_REQUIRED: inspect_pom_sources first.");
        var root = Path.GetFullPath(sourceDirectory);
        var inspector = new PomSourceInspector();
        var survey = inspector.Inspect(root, cancellationToken: cancellationToken);
        if (!string.Equals(survey.SourceTreeHash, expectedSourceTreeHash, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "STALE_POM_ACTION_PLAN: captured source differs from supplied SHA-256 tree identity.");

        // Recheck the same stable source set with exactly the hashing rule
        // already used by PomSourceInspector. No target code runs here.
        var files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(root, path).Split(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part is "bin" or "obj" or ".git"
                    or "node_modules" or "private-generated"))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (files.Length != survey.InspectedFiles)
            throw new InvalidOperationException(
                "POM_SCOPE_CHANGED: source file inventory changed during action planning.");
        var utf8 = new UTF8Encoding(false, true);
        var capture = files.Select(path =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("POM_SYMLINK_REJECTED");
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            var bytes = File.ReadAllBytes(path);
            return (Path: relative, Bytes: bytes, Source: utf8.GetString(bytes));
        }).ToArray();
        var actualHash = ContentTreeHasher.ComputeBytes(
            capture.Select(x => (x.Path, x.Bytes)));
        if (actualHash != expectedSourceTreeHash)
            throw new InvalidOperationException(
                "POM_SOURCE_CHANGED_DURING_PLAN: do not project a stale source snapshot.");

        var projected = SemanticPomActionProjector.Project(
            capture.Select(x => (x.Path, x.Source)).ToArray(),
            sourceType, sourceMethod, sourceLine, receiver, cancellationToken);
        var after = inspector.Inspect(root, cancellationToken: cancellationToken);
        if (after.SourceTreeHash != expectedSourceTreeHash)
            throw new InvalidOperationException(
                "POM_SOURCE_CHANGED_DURING_PLAN: file bytes changed while planning.");

        static string Sha(string text) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))
                .ToLowerInvariant();
        var steps = projected.Steps.Select(x => new PomProjectedAction(
            x.Order, x.SourcePath, x.SourceLine, x.SourceSha256,
            x.SourceSymbol, x.ReceiverType, x.SourceReturnType,
            x.Effect, x.TargetStatement, Sha(x.TargetStatement),
            x.RequiresReview)).ToArray();
        var blockers = projected.Blockers.Select(x => new PomActionBlocker(
            x.Code, x.SourcePath, x.SourceLine, x.SourceSha256,
            x.Explanation)).ToArray();
        var candidate = projected.Status == "requires_review"
            ? new PomReviewedAdapterCandidate(
                projected.SuggestedSourcePattern,
                projected.TargetStatements.Select(statement => ToAdapterTemplate(
                    statement, projected.SuggestedSourcePattern)).ToArray(),
                true, projected.SourceMethodSha256)
            : null;
        return new PomActionPlan(projected.SchemaVersion, expectedSourceTreeHash,
            projected.Status, projected.SourceType, projected.SourceMethod,
            projected.SourceMethodSymbol, projected.SourceMethodSha256,
            projected.SourcePath, projected.SourceLine,
            projected.SuggestedSourcePattern, steps, blockers,
            projected.TargetStatements, projected.Limitations)
        {
            AdapterCandidate = candidate
        };
    }
}
