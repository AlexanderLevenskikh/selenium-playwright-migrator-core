using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Migrator.Core.Models;

namespace Migrator.Roslyn;

/// <summary>
/// Reuses the Roslyn-bound compilation that owns the concrete test fixture.
/// SetUpFixture scope is assembly-local: a global namespace fixture surrounds
/// the entire target assembly, and namespace fixtures surround child namespaces.
/// Never flatten these one-time hooks into each test class.
/// </summary>
internal static class NUnitScopedSetUpInspector
{
    private const string NUnitAttribute = "NUnit.Framework.SetUpFixtureAttribute";

    public static NUnitScopedSetUpPlan? Inspect(
        ClassDeclarationSyntax current,
        SemanticModel semanticModel,
        Compilation compilation,
        Func<MethodDeclarationSyntax, SemanticModel, IReadOnlyList<TestAction>> parseActions)
    {
        var owner = semanticModel.GetDeclaredSymbol(current) as INamedTypeSymbol;
        if (owner == null)
            return null;

        var classes = compilation.SyntaxTrees
            .SelectMany(tree => tree.GetRoot().DescendantNodes()
                .OfType<ClassDeclarationSyntax>())
            .OrderBy(type => type.SyntaxTree.FilePath, StringComparer.Ordinal)
            .ThenBy(type => type.SpanStart)
            .ToArray();

        var discovered = new Dictionary<INamedTypeSymbol, ClassDeclarationSyntax>(
            SymbolEqualityComparer.Default);
        foreach (var node in classes)
        {
            var model = compilation.GetSemanticModel(node.SyntaxTree, ignoreAccessibility: true);
            var symbol = model.GetDeclaredSymbol(node) as INamedTypeSymbol;
            if (symbol == null || !HasSetUpFixture(symbol))
                continue;

            // A partial type is one scoped fixture, not multiple declarations.
            discovered.TryAdd(symbol, node);
        }

        if (discovered.Count == 0)
            return null;

        var scopes = new List<NUnitScopedSetUpFixture>();
        foreach (var (symbol, declaration) in discovered)
        {
            var model = compilation.GetSemanticModel(declaration.SyntaxTree,
                ignoreAccessibility: true);
            var problems = new List<NUnitLifecycleDiagnostic>();
            var line = Line(declaration);
            var typeName = symbol.ToDisplayString();
            var ns = Namespace(symbol);

            void Problem(string code, string explanation, int? sourceLine = null) =>
                problems.Add(new NUnitLifecycleDiagnostic(
                    code, typeName, explanation, sourceLine ?? line));

            if (symbol.ContainingType != null)
                Problem("NUNIT_SETUPFIXTURE_NESTED_TYPE",
                    "An NUnit SetUpFixture must be an independent namespace-level or global class.");
            if (symbol.DeclaredAccessibility != Accessibility.Public)
                Problem("NUNIT_SETUPFIXTURE_NOT_PUBLIC",
                    "NUnit SetUpFixture must be publicly exported.");
            if (!symbol.IsStatic && !symbol.InstanceConstructors.Any(ctor =>
                    ctor.Parameters.Length == 0
                    && ctor.DeclaredAccessibility == Accessibility.Public))
                Problem("NUNIT_SETUPFIXTURE_CTOR_UNAVAILABLE",
                    "NUnit cannot create the source SetUpFixture without a public default constructor.");
            if (symbol.GetMembers().OfType<IFieldSymbol>().Any(field => !field.IsImplicitlyDeclared)
                || symbol.GetMembers().OfType<IPropertySymbol>().Any())
                Problem("NUNIT_SETUPFIXTURE_STATE_REQUIRES_REVIEW",
                    "Stateful source SetUpFixture has fields/properties not automatically portable to target scope.");

            var inherited = NUnitLifecycleInspector.Inspect(
                declaration, model, compilation, parseActions,
                allowUnpairedScopedOneTimeTearDown: true);
            var hooks = inherited?.Hooks
                .OrderBy(hook => hook.InheritanceLevel)
                .ThenBy(hook => hook.SourcePath, StringComparer.Ordinal)
                .ThenBy(hook => hook.SourceLine)
                .ToArray() ?? Array.Empty<NUnitLifecycleHook>();
            if (inherited != null)
                problems.AddRange(inherited.Diagnostics);

            if (hooks.Any(hook => hook.Kind is NUnitLifecycleKind.SetUp
                or NUnitLifecycleKind.TearDown))
                Problem("NUNIT_SETUPFIXTURE_PER_TEST_HOOK_INVALID",
                    "[SetUpFixture] cannot contain per-test SetUp or TearDown methods.");
            if (!hooks.Any(hook => hook.Kind == NUnitLifecycleKind.OneTimeSetUp))
                Problem("NUNIT_SETUPFIXTURE_SETUP_MISSING",
                    "Scoped setup requires a source OneTimeSetUp to preserve lifecycle dependencies.");
            if (hooks.Any(hook => hook.InheritanceLevel < (inherited?.SourceTypeLevels ?? 1) - 1))
                Problem("NUNIT_SETUPFIXTURE_INHERITED_HOOKS_REVIEW",
                    "Inherited scoped hooks require explicit implementation/lifetime review.");

            scopes.Add(new NUnitScopedSetUpFixture(
                typeName, declaration.SyntaxTree.FilePath, ns, line,
                hooks, problems));
        }

        var allProblems = new List<NUnitLifecycleDiagnostic>();
        foreach (var group in scopes.GroupBy(scope => scope.SourceNamespace,
                     StringComparer.Ordinal))
        {
            if (group.Count() > 1)
            {
                allProblems.Add(new NUnitLifecycleDiagnostic(
                    "NUNIT_SETUPFIXTURE_SAME_SCOPE_ORDER",
                    group.Key.Length == 0 ? "<assembly>" : group.Key,
                    "Multiple SetUpFixtures at one namespace/assembly scope have unspecified NUnit order; do not choose by filename.",
                    group.Min(scope => scope.SourceLine)));
            }
        }

        var currentNs = Namespace(owner);
        var declarationHere = scopes.FirstOrDefault(scope =>
            string.Equals(scope.SourceType, owner.ToDisplayString(),
                StringComparison.Ordinal));

        var applicable = scopes
            .Where(scope => IsAncestorScope(scope.SourceNamespace, currentNs))
            .OrderBy(scope => ScopeDepth(scope.SourceNamespace))
            .ThenBy(scope => scope.SourceNamespace, StringComparer.Ordinal)
            .ThenBy(scope => scope.SourceType, StringComparer.Ordinal)
            .ToArray();

        // A multiple-fixture problem in an unrelated sibling namespace does not
        // block this test fixture; its own SetUpFixture file gets that diagnostic.
        var applicableNamespaces = applicable.Select(scope => scope.SourceNamespace)
            .ToHashSet(StringComparer.Ordinal);
        var applicableProblems = allProblems
            .Where(problem => applicableNamespaces.Contains(
                problem.SourceType == "<assembly>" ? "" : problem.SourceType))
            .Concat(applicable.SelectMany(scope => scope.Diagnostics))
            .ToArray();

        if (declarationHere == null && applicable.Length == 0)
            return null;

        return new NUnitScopedSetUpPlan(
            declarationHere,
            applicable,
            applicableProblems);
    }

    private static bool HasSetUpFixture(INamedTypeSymbol type) =>
        type.GetAttributes().Any(attribute =>
            string.Equals(attribute.AttributeClass?.ToDisplayString(),
                NUnitAttribute, StringComparison.Ordinal));

    private static bool IsAncestorScope(string outer, string inner) =>
        outer.Length == 0 || string.Equals(outer, inner, StringComparison.Ordinal)
        || inner.StartsWith(outer + ".", StringComparison.Ordinal);

    private static string Namespace(INamedTypeSymbol type) =>
        type.ContainingNamespace.IsGlobalNamespace
            ? string.Empty : type.ContainingNamespace.ToDisplayString();

    private static int ScopeDepth(string ns) =>
        ns.Length == 0 ? 0 : ns.Count(c => c == '.') + 1;

    private static int Line(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
}
