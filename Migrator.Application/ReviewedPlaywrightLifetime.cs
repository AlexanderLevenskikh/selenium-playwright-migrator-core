using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Migrator.Application;

/// <summary>One exact source observation mapped to an explicit target operation.</summary>
public sealed record ReviewedPlaywrightLifetimeBinding(
    string SourceKind, string SourcePath, int SourceLine,
    string ExpressionSha256, string TargetRole, bool Reviewed);

public sealed record ReviewedPlaywrightInfrastructureMethod(
    string SourceMethod, string SourcePath, int SourceLine,
    string SourceMethodSha256);

public sealed record ReviewedPlaywrightLifetimeIssue(
    string Code, string Explanation, string? SourcePath = null,
    int? SourceLine = null);

public sealed record ReviewedPlaywrightLifetimeCandidate(
    string RelativePath, string Content, string Sha256,
    string FixtureBaseType, string NestedCleanupType,
    string InfrastructureType);

public sealed record ReviewedPlaywrightLifetimePlan(
    string SchemaVersion, string Status, string SourceTreeHash,
    string FixtureBaseType, string NestedCleanupType,
    string InfrastructureType,
    IReadOnlyList<ReviewedPlaywrightLifetimeBinding> ValidatedBindings,
    IReadOnlyList<ReviewedPlaywrightInfrastructureMethod> InfrastructureMethods,
    IReadOnlyList<ReviewedPlaywrightLifetimeIssue> Issues,
    ReviewedPlaywrightLifetimeCandidate? Candidate,
    bool BehavioralEquivalenceVerified,
    string SourceNUnitRuntime, string TargetBuild,
    string TargetNUnitRuntime, string BrowserE2E,
    IReadOnlyList<string> Limitations);

/// <summary>
/// Review only. Maps every observed lifecycle side effect to one of a
/// narrowly defined typed Playwright bridge responsibilities. No arbitrary
/// C# snippet insertion; the bridge must be implemented and actually tested
/// independently. Never automatically unblock or materialize a target.
/// </summary>
public sealed partial class MigrationEngine
{
    private const string ReviewedLifetimeSchema =
        "migrator-reviewed-playwright-lifetime/v1alpha1";

    private static readonly IReadOnlyDictionary<string, string> EffectRoles =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["di_scoped_service"] = "FixtureService",
            ["browser_driver_scope"] = "BrowserContextPage",
            ["browser_navigation_scope"] = "PageNavigation",
            ["browser_storage_scope"] = "PageStorage",
            ["browser_logs"] = "FailedTestBrowserLogs",
            ["browser_screenshot"] = "FailedTestScreenshot",
            ["di_scope_dispose"] = "FixtureScopeDispose",
            ["di_global_dispose"] = "GlobalServiceDispose",
            ["nunit_listener_reflection"] = "RetryListenerReview"
        };

    public ReviewedPlaywrightLifetimePlan ReviewPlaywrightFixtureLifetime(
        string sourceDirectory,
        string fixtureBaseType,
        string nestedCleanupType,
        string infrastructureType,
        string expectedSourceTreeHash,
        IReadOnlyList<ReviewedPlaywrightLifetimeBinding>? bindings,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureBaseType);
        ArgumentException.ThrowIfNullOrWhiteSpace(nestedCleanupType);
        ArgumentException.ThrowIfNullOrWhiteSpace(infrastructureType);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSourceTreeHash);

        var root = Path.GetFullPath(sourceDirectory);
        var ledger = InspectNUnitLifecycle(root, cancellationToken);
        if (!string.Equals(ledger.SourceTreeHash,
                expectedSourceTreeHash, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "STALE_PLAYWRIGHT_LIFETIME_SOURCE: inspect the exact current source.");

        var issues = new List<ReviewedPlaywrightLifetimeIssue>();
        void Block(string code, string reason, string? path = null,
            int? line = null) => issues.Add(new(code, reason, path, line));
        var baseHooks = ledger.Hooks.Where(h =>
            h.SourceType == fixtureBaseType).ToArray();
        var nestedHooks = ledger.Hooks.Where(h =>
            h.SourceType == nestedCleanupType).ToArray();
        var nested = ledger.SetUpFixtures.Where(s =>
            s.SourceType == nestedCleanupType).ToArray();

        if (nested.Length != 1 || !nested[0].CleanupOnly
            || nested[0].ContainingType != fixtureBaseType
            || nestedHooks.Length != 1
            || nestedHooks[0].Kind != "OneTimeTearDown")
            Block("PLAYWRIGHT_LIFETIME_NESTED_SCOPE_INVALID",
                "The exact original nested one-time cleanup owner is required.");
        foreach (var kind in new[] { "OneTimeSetUp", "TearDown",
                     "OneTimeTearDown" })
            if (baseHooks.Count(h => h.Kind == kind) != 1)
                Block("PLAYWRIGHT_LIFETIME_HOOK_SHAPE_UNSUPPORTED",
                    "Expected exactly one source base hook of kind " + kind);
        if (baseHooks.Length != 3)
            Block("PLAYWRIGHT_LIFETIME_HOOK_SHAPE_UNSUPPORTED",
                "Extra or missing base lifecycle hooks require a separate review.");
        if (ledger.Issues.Any(issue =>
            issue.Severity == "error" &&
            issue.Code != "NUNIT_NESTED_SETUPFIXTURE_REQUIRES_REVIEW"))
            Block("PLAYWRIGHT_LIFETIME_OTHER_SOURCE_ERRORS",
                "The source lifecycle ledger has additional blocking diagnostics.");

        var observed = ledger.Effects.Where(e =>
                e.SourceType == fixtureBaseType ||
                e.SourceType == nestedCleanupType)
            .OrderBy(e => e.SourcePath, StringComparer.Ordinal)
            .ThenBy(e => e.SourceLine)
            .ThenBy(e => e.Kind, StringComparer.Ordinal).ToArray();

        foreach (var required in EffectRoles.Keys)
            if (!observed.Any(effect => effect.Kind == required))
                Block("PLAYWRIGHT_LIFETIME_SOURCE_EFFECT_MISSING",
                    "The source does not attest required effect kind " + required);

        if (observed.Any(effect => !EffectRoles.ContainsKey(effect.Kind)))
            Block("PLAYWRIGHT_LIFETIME_EFFECT_UNSUPPORTED",
                "A source effect has no reviewed Playwright target operation.");

        var supplied = bindings?.ToArray() ??
            Array.Empty<ReviewedPlaywrightLifetimeBinding>();
        var matched = new HashSet<int>();
        var validated = new List<ReviewedPlaywrightLifetimeBinding>();
        foreach (var effect in observed)
        {
            var indexes = Enumerable.Range(0, supplied.Length).Where(i =>
                supplied[i].SourceKind == effect.Kind
                && supplied[i].SourcePath == effect.SourcePath
                && supplied[i].SourceLine == effect.SourceLine
                && supplied[i].ExpressionSha256 ==
                    effect.ExpressionSha256).ToArray();
            if (indexes.Length != 1 || matched.Contains(indexes[0]))
            {
                Block("PLAYWRIGHT_LIFETIME_SOURCE_BINDING_MISSING",
                    "Require exactly one source path/line/expression SHA-256 "
                    + "binding for each DI/browser/teardown obligation.",
                    effect.SourcePath, effect.SourceLine);
                continue;
            }
            matched.Add(indexes[0]);
            var binding = supplied[indexes[0]];
            if (!EffectRoles.TryGetValue(effect.Kind, out var role)
                || binding.TargetRole != role || !binding.Reviewed)
                Block("PLAYWRIGHT_LIFETIME_ROLE_REVIEW_REQUIRED",
                    "The exact source effect has no reviewer-attested "
                    + "target operation of role " + role + ".",
                    effect.SourcePath, effect.SourceLine);
            else
                validated.Add(binding);
        }
        if (matched.Count != supplied.Length)
            Block("PLAYWRIGHT_LIFETIME_EXTRA_BINDING",
                "Extra/stale duplicate target mappings are not permitted.");

        // The DI source is a separate, exact class: its fixture key and
        // scope/provider lifetimes must be inspected rather than inferred
        // from a method with the name FullDispose.
        var infrastructure = InspectInfrastructure(root,
            infrastructureType, cancellationToken);
        issues.AddRange(infrastructure.Issues);
        var methods = infrastructure.Methods;

        ReviewedPlaywrightLifetimeCandidate? candidate = null;
        if (issues.Count == 0)
        {
            var sourceBase = FindSourceType(root, fixtureBaseType,
                cancellationToken);
            var baseClass = sourceBase.Type;
            var originalNested = FindSourceType(root, nestedCleanupType,
                cancellationToken).Type;
            if (baseClass == null || originalNested == null
                || sourceBase.Count != 1
                || baseClass.TypeParameterList != null
                || !baseClass.Modifiers.Any(token =>
                    token.ValueText == "public")
                || !baseClass.Modifiers.Any(token =>
                    token.ValueText == "abstract")
                || baseClass.BaseList != null
                || originalNested.TypeParameterList != null
                || !originalNested.Modifiers.Any(token =>
                    token.ValueText == "public"))
                Block("PLAYWRIGHT_LIFETIME_SOURCE_CLASS_SHAPE_UNSAFE",
                    "Only a unique public, abstract, non-generic source base "
                    + "and public nested cleanup type can be rendered.");
            else if (!fixtureBaseType.Contains('.', StringComparison.Ordinal))
                Block("PLAYWRIGHT_LIFETIME_NAMESPACE_REQUIRED",
                    "A source namespace is needed to preserve the nested NUnit cleanup scope.");
            else
            {
                var namespaceName = fixtureBaseType[
                    ..fixtureBaseType.LastIndexOf('.')];
                var code = PlaywrightFixtureLifetimeRenderer.Render(
                    namespaceName, baseClass.Identifier.ValueText,
                    originalNested.Identifier.ValueText,
                    baseHooks.Single(h => h.Kind == "OneTimeSetUp").Method,
                    baseHooks.Single(h => h.Kind == "TearDown").Method,
                    baseHooks.Single(h => h.Kind == "OneTimeTearDown").Method,
                    nestedHooks[0].Method);
                if (CSharpSyntaxTree.ParseText(code).GetDiagnostics()
                    .Any(d => d.Severity ==
                        Microsoft.CodeAnalysis.DiagnosticSeverity.Error))
                    Block("PLAYWRIGHT_LIFETIME_TARGET_SYNTAX_INVALID",
                        "The generated typed C# bridge did not parse.");
                else
                    candidate = new ReviewedPlaywrightLifetimeCandidate(
                        "Migrator.Generated.PlaywrightFixtureLifetime.cs",
                        code, HashLifetime(code), fixtureBaseType,
                        nestedCleanupType, infrastructureType);
            }
        }

        return new ReviewedPlaywrightLifetimePlan(
            ReviewedLifetimeSchema,
            issues.Count > 0 ? "blocked" : "requires_review",
            ledger.SourceTreeHash, fixtureBaseType, nestedCleanupType,
            infrastructureType, validated.ToArray(), methods, issues,
            issues.Count > 0 ? null : candidate,
            false, "not_run", "not_run", "not_run", "not_run",
            new[]
            {
                "Reviewed=true is a caller assertion, not an independently validated source/target behavior claim.",
                "The target C# contains a typed Playwright IBrowserContext/IPage fixture bridge; implementing its service provider, failure logging, screenshot and retry policy is mandatory.",
                "The source maps fixture scopes by NUnit class name and uses a pooled Selenium driver; Playwright Context.CloseAsync does not itself prove identical pooling.",
                "Browser logs and screenshots must be observed after failed tests and before fixture scope release, including failure paths.",
                "Global FullDispose remains on the original nested NUnit OneTimeTearDown owner. Do not hoist the hook or dispose the browser per test.",
                "The code is a review-only candidate, not automatically materialized by PlanTargetSolution; runtime, browser and DI equivalence remain unverified."
            });
    }

    private static (ClassDeclarationSyntax? Type, int Count) FindSourceType(
        string root, string qualifiedName, CancellationToken cancellationToken)
    {
        var found = new List<ClassDeclarationSyntax>();
        foreach (var file in Directory.GetFiles(root, "*.cs",
                     SearchOption.AllDirectories).OrderBy(s => s,
                         StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Replace('\\', '/').Split('/').Any(part =>
                part is "bin" or "obj" or ".git")) continue;
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file);
            foreach (var type in tree.GetRoot().DescendantNodes()
                .OfType<ClassDeclarationSyntax>())
            {
                var ns = string.Join(".", type.Ancestors()
                    .OfType<BaseNamespaceDeclarationSyntax>().Reverse()
                    .Select(n => n.Name.ToString()));
                var parents = type.Ancestors()
                    .OfType<TypeDeclarationSyntax>().Reverse()
                    .Select(t => t.Identifier.ValueText);
                var name = string.Join(".", parents
                    .Prepend(ns).Append(type.Identifier.ValueText)
                    .Where(part => part.Length > 0));
                if (name == qualifiedName) found.Add(type);
            }
        }
        return (found.FirstOrDefault(), found.Count);
    }

    private static (ReviewedPlaywrightInfrastructureMethod[] Methods,
        ReviewedPlaywrightLifetimeIssue[] Issues) InspectInfrastructure(
        string root, string typeName, CancellationToken cancellationToken)
    {
        var issues = new List<ReviewedPlaywrightLifetimeIssue>();
        var methods = new List<ReviewedPlaywrightInfrastructureMethod>();
        var match = FindSourceType(root, typeName, cancellationToken);
        if (match.Count != 1 || match.Type == null)
        {
            issues.Add(new("PLAYWRIGHT_LIFETIME_DI_SOURCE_UNRESOLVED",
                "The source DI container type must be unique and present."));
            return (methods.ToArray(), issues.ToArray());
        }
        var type = match.Type;
        var path = Path.GetRelativePath(root, type.SyntaxTree.FilePath)
            .Replace('\\', '/');
        if (type.SyntaxTree.FilePath.Length == 0)
        {
            issues.Add(new("PLAYWRIGHT_LIFETIME_DI_SOURCE_UNRESOLVED",
                "The DI container source path is unavailable."));
            return (methods.ToArray(), issues.ToArray());
        }
        var body = type.ToFullString();
        foreach (var name in new[] { "GetScopedRequiredService",
                     "DisposeScope", "FullDispose" })
        {
            var matches = type.Members.OfType<MethodDeclarationSyntax>()
                .Where(m => m.Identifier.ValueText == name).ToArray();
            if (matches.Length != 1)
            {
                issues.Add(new("PLAYWRIGHT_LIFETIME_DI_METHOD_UNRESOLVED",
                    "Source DI provider must have exactly one " + name, path));
                continue;
            }
            var method = matches[0];
            var expression = method.ToFullString();
            var proof = name switch
            {
                "GetScopedRequiredService" =>
                    expression.Contains("GetOrAdd", StringComparison.Ordinal)
                    && expression.Contains("CreateScope",
                        StringComparison.Ordinal),
                "DisposeScope" =>
                    expression.Contains("TryRemove", StringComparison.Ordinal)
                    && expression.Contains("Dispose(", StringComparison.Ordinal),
                "FullDispose" =>
                    expression.Contains("Clear(", StringComparison.Ordinal)
                    && expression.Contains("Dispose(", StringComparison.Ordinal),
                _ => false
            };
            if (!proof)
                issues.Add(new("PLAYWRIGHT_LIFETIME_DI_OWNERSHIP_UNPROVEN",
                    "Could not attest source scope acquisition, return, or "
                    + "shared provider cleanup in method " + name, path,
                    method.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
            methods.Add(new(name, path,
                method.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                HashLifetime(expression)));
        }
        var key = type.Members.OfType<PropertyDeclarationSyntax>()
            .Where(p => p.Identifier.ValueText == "ScopeKey").ToArray();
        if (key.Length != 1
            || !key[0].ToFullString().Contains("Test.ClassName",
                StringComparison.Ordinal))
            issues.Add(new("PLAYWRIGHT_LIFETIME_SCOPE_KEY_UNPROVEN",
                "The fixture-scoped DI key must be source-backed by "
                + "NUnit Test.ClassName, not inferred from a file name.",
                path));
        return (methods.ToArray(), issues.ToArray());
    }

    private static string HashLifetime(string content) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
