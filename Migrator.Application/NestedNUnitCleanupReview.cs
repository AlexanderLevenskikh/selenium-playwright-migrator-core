using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Migrator.Application;

/// <summary>
/// Read-only source-attested proposed preservation of a *nested* NUnit
/// cleanup-only SetUpFixture. A nested NUnit scope is executable in NUnit
/// 3 and 4: never drop it, turn it into a per-test teardown or hoist it to
/// a standalone namespace scope. The returned C# is a review candidate,
/// not a verified or automatically materialized target solution.
/// </summary>
public sealed record NestedNUnitCleanupIssue(
    string Code, string Severity, string Explanation,
    string? SourcePath = null, int? SourceLine = null);

public sealed record NestedNUnitCleanupCandidate(
    string RelativePath, string Content, string Sha256,
    string SourceType, string OriginalNamespace, string OwnerType,
    string HookMethod, string SourceMethodSha256);

public sealed record NestedNUnitCleanupReview(
    string SchemaVersion, string Status, string SourceTreeHash,
    string SourceType, string? SourceFile, int? SourceLine,
    string? OriginalNamespace, string? OuterType,
    string? SourceMethodSha256, string? SourceRuntimeEvidenceSha256,
    IReadOnlyList<string> SourceEffectKinds,
    IReadOnlyList<NestedNUnitCleanupIssue> Issues,
    NestedNUnitCleanupCandidate? Candidate,
    bool BehavioralEquivalenceVerified,
    string SourceNUnitRuntime, string TargetBuild, string TargetNUnitRuntime,
    string BrowserObservables,
    IReadOnlyList<string> Limitations);

public sealed partial class MigrationEngine
{
    private const string NestedCleanupSchema =
        "migrator-nunit-nested-cleanup-review/v1alpha1";

    /// <summary>
    /// An explicit opt-in candidate for *preserving the nested type shape*,
    /// not for promoting it to a namespace/assembly fixture. Callers must
    /// still independently prove source/target NUnit and business behavior.
    /// No filesystem output or code execution is performed.
    /// </summary>
    public NestedNUnitCleanupReview ReviewNestedNUnitCleanup(
        string sourceDirectory, string sourceType,
        string expectedSourceTreeHash, IReadOnlyList<string>? targetStatements,
        string? sourceRuntimeEvidenceSha256 = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSourceTreeHash);
        var root = Path.GetFullPath(sourceDirectory);
        var ledger = InspectNUnitLifecycle(root, cancellationToken);
        if (ledger.SourceTreeHash != expectedSourceTreeHash)
            throw new InvalidOperationException(
                "STALE_NESTED_CLEANUP_SOURCE: the source tree hash changed.");

        var problems = new List<NestedNUnitCleanupIssue>();
        void Problem(string code, string why, string? path = null,
            int? line = null) => problems.Add(new NestedNUnitCleanupIssue(
                code, "error", why, path, line));

        var matches = ledger.SetUpFixtures.Where(item =>
            string.Equals(item.SourceType, sourceType,
                StringComparison.Ordinal)).ToArray();
        NUnitSourceLifecycleScope? scope = matches.Length == 1
            ? matches[0] : null;
        NestedNUnitCleanupCandidate? artifact = null;
        string? methodHash = null;
        var effects = Array.Empty<string>();

        if (scope == null)
            Problem("NESTED_CLEANUP_SOURCE_NOT_UNIQUE",
                "Require exactly one source-backed nested SetUpFixture type.");
        else if (scope.ContainingType == null || !scope.CleanupOnly)
            Problem("NESTED_CLEANUP_SHAPE_UNSUPPORTED",
                "Only a nested type with exactly one OneTimeTearDown "
                + "and no other NUnit lifecycle hook can be reviewed.",
                scope.SourcePath, scope.SourceLine);
        else
        {
            var source = Path.GetFullPath(Path.Combine(root,
                scope.SourcePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!Path.GetRelativePath(root, source)
                    .Replace('\\', '/').Equals(scope.SourcePath,
                        StringComparison.Ordinal) || !File.Exists(source))
                Problem("NESTED_CLEANUP_SOURCE_OUTSIDE_SCOPE",
                    "Nested fixture path was not a current source file.");
            else
            {
                var syntax = CSharpSyntaxTree.ParseText(
                    File.ReadAllText(source), path: source).GetRoot();
                var nested = syntax.DescendantNodes()
                    .OfType<ClassDeclarationSyntax>()
                    .Where(type => TypeName(type) == sourceType)
                    .ToArray();
                if (nested.Length != 1)
                    Problem("NESTED_CLEANUP_DECLARATION_NOT_UNIQUE",
                        "The exact nested source type could not be bound.",
                        scope.SourcePath, scope.SourceLine);
                else
                {
                    var declaration = nested[0];
                    var owner = declaration.Ancestors()
                        .OfType<ClassDeclarationSyntax>().FirstOrDefault();
                    var depth = declaration.Ancestors()
                        .OfType<TypeDeclarationSyntax>().Count();
                    var members = declaration.Members;
                    var hooks = members.OfType<MethodDeclarationSyntax>()
                        .Where(method => method.AttributeLists.SelectMany(
                            list => list.Attributes).Any(attr =>
                            AttributeName(attr) == "OneTimeTearDown"))
                        .ToArray();
                    if (depth != 1 || owner == null
                        || owner.TypeParameterList != null
                        || owner.Modifiers.Any(t => t.ValueText is "static"
                            or "partial")
                        || !owner.Modifiers.Any(t => t.ValueText == "public")
                        || owner.AttributeLists.Count > 0
                        || !declaration.Modifiers.Any(t =>
                            t.ValueText == "public")
                        || declaration.TypeParameterList != null
                        || declaration.BaseList != null
                        || members.Count != 1
                        || hooks.Length != 1
                        || hooks[0].ParameterList.Parameters.Count != 0
                        || hooks[0].ReturnType.ToString() != "void"
                        || hooks[0].Body == null
                        || hooks[0].Body!.Statements.Count is < 1 or > 32
                        || hooks[0].Body!.Statements.Any(stmt =>
                            stmt is not ExpressionStatementSyntax
                                { Expression: InvocationExpressionSyntax })
                        || !declaration.AttributeLists.SelectMany(
                            list => list.Attributes).All(attr =>
                            AttributeName(attr) == "SetUpFixture"))
                        Problem("NESTED_CLEANUP_SHAPE_UNSUPPORTED",
                            "Nested source type is not an isolated public, "
                            + "non-generic, synchronous cleanup-only scope "
                            + "with direct invocation statements and no state.",
                            scope.SourcePath, scope.SourceLine);
                    else
                    {
                        methodHash = Hash(hooks[0].ToFullString());
                        var recorded = ledger.Hooks.Where(item =>
                            item.SourceType == sourceType
                            && item.Kind == "OneTimeTearDown").ToArray();
                        if (recorded.Length != 1
                            || recorded[0].MethodSha256 != methodHash)
                            Problem("NESTED_CLEANUP_SOURCE_FINGERPRINT_MISMATCH",
                                "Source hook no longer matches Roslyn ledger.");
                        else
                        {
                            effects = ledger.Effects.Where(item =>
                                    item.SourceType == sourceType
                                    && item.SourceMethod ==
                                        hooks[0].Identifier.ValueText)
                                .Select(item => item.Kind)
                                .Distinct(StringComparer.Ordinal)
                                .OrderBy(item => item, StringComparer.Ordinal)
                                .ToArray();
                            if (ledger.SetUpFixtures.Count(item =>
                                item.SourceType != sourceType &&
                                item.Namespace == scope.Namespace) > 0)
                                Problem("NESTED_CLEANUP_SAME_SCOPE_ORDER_REVIEW",
                                    "Other SetUpFixtures in the same source "
                                    + "namespace can change cleanup ordering; "
                                    + "no automatic target mapping is authorized.",
                                    scope.SourcePath, scope.SourceLine);
                            else
                                artifact = RenderCandidate(scope, owner,
                                    declaration, hooks[0], targetStatements,
                                    problems, methodHash);
                        }
                    }
                }
            }
        }

        if (sourceRuntimeEvidenceSha256 != null && (
            sourceRuntimeEvidenceSha256.Length != 64 ||
            !sourceRuntimeEvidenceSha256.All(c =>
                char.IsAsciiHexDigit(c))))
            Problem("NESTED_CLEANUP_RUNTIME_EVIDENCE_HASH_INVALID",
                "If supplied, the source NUnit runtime evidence must have "
                + "a literal 64-character SHA-256 digest.");
        if (problems.Count > 0) artifact = null;

        return new NestedNUnitCleanupReview(
            NestedCleanupSchema,
            problems.Count > 0 ? "blocked" : "requires_review",
            ledger.SourceTreeHash, sourceType,
            scope?.SourcePath, scope?.SourceLine,
            scope?.Namespace, scope?.ContainingType, methodHash,
            sourceRuntimeEvidenceSha256, effects,
            problems, artifact,
            false, "not_run", "not_run", "not_run", "not_run",
            new[]
            {
                "The source nested NUnit SetUpFixture is executable in synthetic NUnit3/4; never silently remove it or hoist it to namespace scope.",
                "The candidate retains source outer and nested type names, namespace and one-time cleanup hook; no original body or selector is copied.",
                "Target statements are supplied by an explicit reviewer, and structural checks cannot prove equivalent DI, disposal or browser behavior.",
                "Source runtime evidence hash, when supplied, is an external provenance claim, not an execution performed or validated by this tool.",
                "This is a review candidate separate from the full target solution. The normal project migration remains blocked until approved integration and runtime comparison.",
                "Source and target NUnit timing, failure paths, log/screenshot operations and product observables require separate actual tests."
            });
    }

    private static NestedNUnitCleanupCandidate? RenderCandidate(
        NUnitSourceLifecycleScope scope, ClassDeclarationSyntax owner,
        ClassDeclarationSyntax nested, MethodDeclarationSyntax hook,
        IReadOnlyList<string>? statements,
        List<NestedNUnitCleanupIssue> problems, string methodHash)
    {
        if (statements == null || statements.Count == 0)
        {
            problems.Add(new NestedNUnitCleanupIssue(
                "NESTED_CLEANUP_MAPPING_REQUIRED", "error",
                "An explicitly reviewed target cleanup body is required.",
                scope.SourcePath, scope.SourceLine));
            return null;
        }
        if (statements.Count != hook.Body!.Statements.Count)
        {
            problems.Add(new NestedNUnitCleanupIssue(
                "NESTED_CLEANUP_ACTION_COUNT_MISMATCH", "error",
                "Every original cleanup invocation needs one explicit target "
                + "invocation in this conservative candidate.",
                scope.SourcePath, scope.SourceLine));
            return null;
        }
        if (statements.Count > 32)
        {
            problems.Add(new NestedNUnitCleanupIssue(
                "NESTED_CLEANUP_MAPPING_TOO_LARGE", "error",
                "No more than 32 explicit target statements are allowed.",
                scope.SourcePath, scope.SourceLine));
            return null;
        }
        // Reject comments, arbitrary method bodies and unresolved TODOs;
        // accept only parsable, exact expression invocations, preserving order.
        foreach (var statement in statements)
        {
            if (statement.Length is < 4 or > 2000
                || statement.Contains("TODO", StringComparison.OrdinalIgnoreCase)
                || statement.Contains("MIGRATOR", StringComparison.OrdinalIgnoreCase)
                || statement.Contains("//", StringComparison.Ordinal)
                || statement.Contains("/*", StringComparison.Ordinal)
                || statement.Contains("Assert.Inconclusive",
                    StringComparison.Ordinal))
            {
                problems.Add(new NestedNUnitCleanupIssue(
                    "NESTED_CLEANUP_MAPPING_UNSAFE", "error",
                    "Unreviewed or placeholder cleanup statements cannot execute.",
                    scope.SourcePath, scope.SourceLine));
                return null;
            }
            var parsed = SyntaxFactory.ParseStatement(statement);
            if (parsed is not ExpressionStatementSyntax
                { Expression: InvocationExpressionSyntax }
                || parsed.ContainsDiagnostics)
            {
                problems.Add(new NestedNUnitCleanupIssue(
                    "NESTED_CLEANUP_MAPPING_UNSAFE", "error",
                    "Target cleanup must be a direct synchronous C# invocation "
                    + "statement without control-flow or syntax errors.",
                    scope.SourcePath, scope.SourceLine));
                return null;
            }
        }

        var ownerModifiers = owner.Modifiers.Any(m =>
            m.ValueText == "abstract") ? "public abstract class " :
            "public class ";
        var namespaceLine = scope.Namespace.Length == 0
            ? "" : "namespace " + scope.Namespace + ";\n";
        var result = new StringBuilder();
        result.AppendLine("// Explicit reviewed candidate; NUnit runtime and browser equivalence not verified.");
        if (namespaceLine.Length > 0)
            result.AppendLine(namespaceLine.TrimEnd());
        result.AppendLine(ownerModifiers + owner.Identifier.ValueText + " {");
        result.AppendLine("    [NUnit.Framework.SetUpFixture]");
        result.AppendLine("    public class " + nested.Identifier.ValueText + " {");
        result.AppendLine("        [NUnit.Framework.OneTimeTearDown]");
        result.AppendLine("        public void " + hook.Identifier.ValueText + "() {");
        foreach (var statement in statements)
            result.AppendLine("            " + statement.Trim());
        result.AppendLine("        }");
        result.AppendLine("    }");
        result.AppendLine("}");
        var source = result.ToString();
        if (CSharpSyntaxTree.ParseText(source).GetDiagnostics()
            .Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            problems.Add(new NestedNUnitCleanupIssue(
                "NESTED_CLEANUP_TARGET_SYNTAX_INVALID", "error",
                "Reviewed cleanup candidate did not parse as a C# compilation unit.",
                scope.SourcePath, scope.SourceLine));
            return null;
        }
        return new NestedNUnitCleanupCandidate(
            "Migrator.Generated.NestedCleanup." + Hash(scope.SourceType)[..12]
                + ".cs", source, Hash(source), scope.SourceType,
            scope.Namespace, owner.Identifier.ValueText,
            hook.Identifier.ValueText, methodHash);
    }

    private static string TypeName(ClassDeclarationSyntax declaration)
    {
        var names = declaration.Ancestors().OfType<ClassDeclarationSyntax>()
            .Reverse().Select(type => type.Identifier.ValueText)
            .Append(declaration.Identifier.ValueText);
        var ns = declaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse().Select(item => item.Name.ToString());
        return string.Join(".", ns.Concat(names));
    }

    private static string AttributeName(AttributeSyntax attribute)
    {
        var name = attribute.Name.ToString().Split('.').Last();
        return name.EndsWith("Attribute", StringComparison.Ordinal)
            ? name[..^"Attribute".Length] : name;
    }

    private static string Hash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))
            .ToLowerInvariant();
}
