using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Migrator.Application;

/// <summary>
/// A source-only, immutable inventory. Neither a migration mapping nor an
/// attestation that NUnit/DI/browser side effects survived in target code.
/// </summary>
public sealed record NUnitSourceLifecycleHook(
    string SourceType, string Kind, string Method, string SourcePath,
    int SourceLine, string MethodSha256, bool IsNestedScope);

public sealed record NUnitSourceLifecycleEffect(
    string Kind, string SourceType, string SourceMethod, string SourcePath,
    int SourceLine, string ReachedThrough, string ExpressionSha256);

public sealed record NUnitSourceLifecycleScope(
    string SourceType, string SourcePath, int SourceLine,
    string Namespace, string? ContainingType, bool CleanupOnly,
    IReadOnlyList<string> Hooks);

public sealed record NUnitSourceLifecycleIssue(
    string Code, string Severity, string SourcePath, int SourceLine,
    string Description);

public sealed record NUnitSourceLifecycleReport(
    string SchemaVersion, string SourceTreeHash, int InspectedFiles,
    int DeclaredTestMethods, IReadOnlyList<NUnitSourceLifecycleHook> Hooks,
    IReadOnlyList<NUnitSourceLifecycleEffect> Effects,
    IReadOnlyList<NUnitSourceLifecycleScope> SetUpFixtures,
    IReadOnlyList<NUnitSourceLifecycleIssue> Issues,
    string Status, string Build, string NUnitRuntime, string BrowserObservables,
    IReadOnlyList<string> Limitations);

/// <summary>
/// Syntax-backed ledger for inherited NUnit source hooks, helpers, DI/browser
/// resources and teardown. Complements the symbol-bound NUnitLifecyclePlan,
/// and deliberately cannot turn an invalid nested SetUpFixture into a runnable
/// assembly hook. Source bodies, URLs and selector literals are never returned.
/// </summary>
public sealed class NUnitLifecycleSourceInspector
{
    private const int MaximumFiles = 512;
    private const int MaximumHooks = 512;
    private const int MaximumEffects = 1024;
    private const int MaximumHelperDepth = 5;
    private const int MaximumFileBytes = 2 * 1024 * 1024;

    private static readonly HashSet<string> HookNames = new(
        new[] { "SetUp", "TearDown", "OneTimeSetUp", "OneTimeTearDown" },
        StringComparer.Ordinal);

    public NUnitSourceLifecycleReport Inspect(string sourceDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        var root = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("NUNIT_LIFECYCLE_SOURCE_MISSING");
        var directories = Directory.GetDirectories(root, "*",
            SearchOption.AllDirectories);
        if (directories.Any(dir =>
                (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0))
            throw new InvalidOperationException(
                "NUNIT_LIFECYCLE_SOURCE_SYMLINK_REJECTED");
        var files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !Excluded(Path.GetRelativePath(root, path)))
            .OrderBy(path => Path.GetRelativePath(root, path),
                StringComparer.Ordinal).ToArray();
        if (files.Length > MaximumFiles || files.Any(path =>
            new FileInfo(path).Length > MaximumFileBytes))
            throw new InvalidOperationException(
                "NUNIT_LIFECYCLE_SOURCE_TOO_LARGE");
        if (files.Any(path => (File.GetAttributes(path) &
            FileAttributes.ReparsePoint) != 0))
            throw new InvalidOperationException(
                "NUNIT_LIFECYCLE_SOURCE_SYMLINK_REJECTED");

        var roots = new List<(string Path, CompilationUnitSyntax Root)>();
        var issues = new List<NUnitSourceLifecycleIssue>();
        using var treeDigest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            var bytes = File.ReadAllBytes(path);
            treeDigest.AppendData(Encoding.UTF8.GetBytes(relative + "\n"
                + bytes.Length + "\n"));
            treeDigest.AppendData(bytes);
            treeDigest.AppendData(new byte[] { 0 });
            var parsed = CSharpSyntaxTree.ParseText(Encoding.UTF8.GetString(bytes),
                path: path);
            foreach (var error in parsed.GetDiagnostics().Where(d =>
                d.Severity == DiagnosticSeverity.Error).Take(5))
                issues.Add(new NUnitSourceLifecycleIssue(
                    "NUNIT_LIFECYCLE_SOURCE_SYNTAX_ERROR", "error", relative,
                    error.Location.GetLineSpan().StartLinePosition.Line + 1,
                    "Cannot rely on source hooks when this C# syntax is invalid."));
            roots.Add((relative, (CompilationUnitSyntax)parsed.GetRoot()));
        }

        var hooks = new List<NUnitSourceLifecycleHook>();
        var effects = new List<NUnitSourceLifecycleEffect>();
        var scopes = new List<NUnitSourceLifecycleScope>();
        var declared = 0;

        foreach (var (path, syntax) in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var type in syntax.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var name = SourceType(type);
                var nested = type.Ancestors().OfType<TypeDeclarationSyntax>()
                    .FirstOrDefault();
                var methods = type.Members.OfType<MethodDeclarationSyntax>()
                    .ToArray();
                declared += methods.Count(method => Attributes(method)
                    .Any(attr => Name(attr) is "Test" or "TestCase"
                        or "TestCaseSource"));
                var lifecycle = methods.Where(method => Attributes(method)
                    .Any(attr => HookNames.Contains(Name(attr)))).ToArray();
                var setup = Attributes(type).Any(attr =>
                    Name(attr) == "SetUpFixture");
                if (setup)
                {
                    var kinds = lifecycle.SelectMany(method => Attributes(method)
                            .Select(Name).Where(HookNames.Contains))
                        .Distinct(StringComparer.Ordinal).OrderBy(x => x,
                            StringComparer.Ordinal).ToArray();
                    var cleanupOnly = kinds.Length == 1
                        && kinds[0] == "OneTimeTearDown";
                    scopes.Add(new NUnitSourceLifecycleScope(name, path,
                        Line(type), Namespace(type), nested == null ? null
                            : SourceType(nested), cleanupOnly, kinds));
                    if (nested != null)
                        issues.Add(new NUnitSourceLifecycleIssue(
                            "NUNIT_NESTED_SETUPFIXTURE_REQUIRES_REVIEW",
                            "error", path, Line(type),
                            "Nested SetUpFixture is not a standalone NUnit "
                            + "namespace/assembly scope. Do not hoist its "
                            + "cleanup, invent timing or copy it into a test."));
                    if (cleanupOnly)
                        issues.Add(new NUnitSourceLifecycleIssue(
                            "NUNIT_SETUPFIXTURE_CLEANUP_ONLY_REQUIRES_REVIEW",
                            "review", path, Line(type),
                            "Cleanup-only suite scope needs actual NUnit "
                            + "order, failure-path and ownership verification."));
                }

                foreach (var method in lifecycle)
                {
                    foreach (var attr in Attributes(method).Where(attr =>
                        HookNames.Contains(Name(attr))))
                        hooks.Add(new NUnitSourceLifecycleHook(
                            name, Name(attr), method.Identifier.ValueText,
                            path, Line(method), Hash(method.ToFullString()),
                            setup && nested != null));
                    // Follow only same-class, zero-argument private source
                    // helpers, with cycle/depth bounds. No dynamic dispatch
                    // or non-local method body is assumed to be understood.
                    var visited = new HashSet<string>(StringComparer.Ordinal);
                    InspectMethod(method, method.Identifier.ValueText, 0,
                        visited);
                }

                // Source DI-backed browser properties are lifecycle
                // dependencies even when an inherited hook uses them by name.
                if (lifecycle.Length > 0 || setup)
                    foreach (var property in type.Members
                        .OfType<PropertyDeclarationSyntax>())
                    {
                        var expression = property.ExpressionBody?.Expression
                            .ToString() ?? property.AccessorList?.ToString()
                            ?? string.Empty;
                        var propertyType = property.Type.ToString();
                        Emit(expression, name, property.Identifier.ValueText,
                            path, Line(property), "property_accessor");
                        if (propertyType.Contains("IWebDriver",
                                StringComparison.Ordinal)
                            || propertyType.Contains("IPooledWebDriver",
                                StringComparison.Ordinal))
                            EmitKind("browser_driver_scope", name,
                                property.Identifier.ValueText, path,
                                Line(property), "property_type", expression);
                        if (propertyType.Contains("Navigation",
                                StringComparison.Ordinal))
                            EmitKind("browser_navigation_scope", name,
                                property.Identifier.ValueText, path,
                                Line(property), "property_type", expression);
                        if (propertyType.Contains("LocalStorage",
                                StringComparison.Ordinal))
                            EmitKind("browser_storage_scope", name,
                                property.Identifier.ValueText, path,
                                Line(property), "property_type", expression);
                    }

                void InspectMethod(MethodDeclarationSyntax method,
                    string reachedThrough, int depth, HashSet<string> seen)
                {
                    if (depth > MaximumHelperDepth
                        || !seen.Add(method.Identifier.ValueText))
                        return;
                    foreach (var invocation in method.DescendantNodes()
                        .OfType<InvocationExpressionSyntax>())
                    {
                        var expression = invocation.Expression.ToString();
                        Emit(expression, name, method.Identifier.ValueText,
                            path, Line(invocation), reachedThrough);
                        var helper = invocation.Expression is IdentifierNameSyntax id
                            ? id.Identifier.ValueText : null;
                        if (helper == null || invocation.ArgumentList.Arguments.Count != 0)
                            continue;
                        var local = methods.FirstOrDefault(candidate =>
                            candidate.Identifier.ValueText == helper
                            && candidate.ParameterList.Parameters.Count == 0
                            && candidate.Modifiers.Any(token =>
                                token.ValueText == "private"));
                        if (local != null)
                            InspectMethod(local, helper, depth + 1, seen);
                    }
                    foreach (var creation in method.DescendantNodes()
                        .OfType<ObjectCreationExpressionSyntax>())
                        Emit(creation.Type.ToString(), name,
                            method.Identifier.ValueText, path, Line(creation),
                            reachedThrough);
                }
            }
        }

        var treeHash = Convert.ToHexString(treeDigest.GetHashAndReset())
            .ToLowerInvariant();
        if (hooks.Count > MaximumHooks || effects.Count > MaximumEffects)
            throw new InvalidOperationException(
                "NUNIT_LIFECYCLE_EVIDENCE_TOO_LARGE");
        return new NUnitSourceLifecycleReport(
            "migrator-nunit-lifecycle-source/v1alpha1", treeHash,
            files.Length, declared,
            hooks.OrderBy(h => h.SourcePath, StringComparer.Ordinal)
                .ThenBy(h => h.SourceLine).ThenBy(h => h.Kind,
                    StringComparer.Ordinal).ToArray(),
            effects.OrderBy(e => e.SourcePath, StringComparer.Ordinal)
                .ThenBy(e => e.SourceLine).ThenBy(e => e.Kind,
                    StringComparer.Ordinal).ToArray(),
            scopes.OrderBy(s => s.SourcePath, StringComparer.Ordinal)
                .ThenBy(s => s.SourceLine).ToArray(),
            issues.OrderBy(i => i.SourcePath, StringComparer.Ordinal)
                .ThenBy(i => i.SourceLine).ThenBy(i => i.Code,
                    StringComparer.Ordinal).ToArray(),
            issues.Any(i => i.Severity == "error") ? "blocked"
                : effects.Count > 0 || scopes.Any(s => s.CleanupOnly)
                    ? "requires_review" : "source_evidence_only",
            "not_run", "not_run", "not_run",
            new[]
            {
                "Source syntax and same-class helper lineage are not full Roslyn semantic binding or evaluated MSBuild.",
                "No source method body, browser URL, locator or secret is returned; hashes bind evidence to local source bytes.",
                "A nested SetUpFixture cannot be automatically hoisted into an assembly/namespace scope.",
                "DI, pooled browsers, listener reflection, screenshots, logs and cleanup effects require independently reviewed target implementations.",
                "This tool never generates, builds or executes a target; emitted evidence cannot prove NUnit/browser parity."
            });

        void Emit(string expression, string sourceType, string method,
            string path, int line, string through)
        {
            var kind = EffectKind(expression);
            if (kind != null)
                EmitKind(kind, sourceType, method, path, line, through,
                    expression);
        }
        void EmitKind(string kind, string sourceType, string method,
            string path, int line, string through, string expression)
        {
            if (effects.Any(effect => effect.Kind == kind &&
                effect.SourcePath == path && effect.SourceLine == line &&
                effect.SourceMethod == method))
                return;
            effects.Add(new NUnitSourceLifecycleEffect(kind,
                sourceType, method, path, line, through, Hash(expression)));
        }
    }

    private static string? EffectKind(string value)
    {
        if (value.Contains("DiContainer.FullDispose", StringComparison.Ordinal))
            return "di_global_dispose";
        if (value.Contains("DiContainer.DisposeScope", StringComparison.Ordinal))
            return "di_scope_dispose";
        if (value.Contains("GetScopedRequiredService", StringComparison.Ordinal))
            return "di_scoped_service";
        if (value.Contains("WriteBrowserLogs", StringComparison.Ordinal))
            return "browser_logs";
        if (value.Contains("CreateAndSaveScreenshot", StringComparison.Ordinal)
            || value.Contains("TakeScreenshot", StringComparison.Ordinal))
            return "browser_screenshot";
        if (value.Contains("RetryTestListener", StringComparison.Ordinal)
            || value.Contains("TestExecutionContext", StringComparison.Ordinal)
            || value.Contains("ITestListener", StringComparison.Ordinal)
            || value.Contains("GetProperties", StringComparison.Ordinal)
            || value.Contains("SetValue", StringComparison.Ordinal))
            return "nunit_listener_reflection";
        if (value.Contains("IPooledWebDriver", StringComparison.Ordinal)
            || value.Contains("WrappedDriver", StringComparison.Ordinal))
            return "browser_driver_scope";
        if (value.Contains("LocalStorage", StringComparison.Ordinal))
            return "browser_storage_scope";
        if (value.Contains("Navigation", StringComparison.Ordinal))
            return "browser_navigation_scope";
        return null;
    }

    private static bool Excluded(string relative) =>
        relative.Replace('\\', '/').Split('/').Any(part =>
            part is "bin" or "obj" or ".git" or ".vs");

    private static IEnumerable<AttributeSyntax> Attributes(
        MemberDeclarationSyntax member) =>
        member.AttributeLists.SelectMany(list => list.Attributes);

    private static string Name(AttributeSyntax attribute)
    {
        var name = attribute.Name.ToString().Split('.').Last();
        return name.EndsWith("Attribute", StringComparison.Ordinal)
            ? name[..^"Attribute".Length] : name;
    }

    private static string SourceType(TypeDeclarationSyntax type)
    {
        var parents = type.Ancestors().OfType<TypeDeclarationSyntax>()
            .Reverse().Select(t => t.Identifier.ValueText);
        var ns = Namespace(type);
        return (ns.Length == 0 ? "" : ns + ".")
            + string.Join(".", parents.Append(type.Identifier.ValueText));
    }

    private static string Namespace(SyntaxNode node)
    {
        var parts = node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse().Select(n => n.Name.ToString());
        return string.Join(".", parts);
    }

    private static int Line(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}

public sealed partial class MigrationEngine
{
    /// <summary>
    /// Read-only source lifecycle evidence; no generation or execution.
    /// </summary>
    public NUnitSourceLifecycleReport InspectNUnitLifecycle(
        string sourceDirectory, CancellationToken cancellationToken = default) =>
        new NUnitLifecycleSourceInspector().Inspect(sourceDirectory,
            cancellationToken);
}
