using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Migrator.Core.Models;

namespace Migrator.Roslyn;

/// <summary>
/// Bound, read-only NUnit lifecycle inspection over the same Roslyn compilation
/// as the test fixture. Never infer a base method by its name or a flattened
/// teardown order from incidental source file order.
/// </summary>
internal static class NUnitLifecycleInspector
{
    private const int MaxLevels = 24;
    private static readonly NUnitLifecycleKind[] Kinds =
    {
        NUnitLifecycleKind.SetUp, NUnitLifecycleKind.TearDown,
        NUnitLifecycleKind.OneTimeSetUp, NUnitLifecycleKind.OneTimeTearDown
    };

    public static NUnitLifecyclePlan? Inspect(
        ClassDeclarationSyntax fixture,
        SemanticModel semanticModel,
        Compilation compilation,
        Func<MethodDeclarationSyntax, SemanticModel, IReadOnlyList<TestAction>> parseActions)
    {
        var hooks = new List<NUnitLifecycleHook>();
        var diagnostics = new List<NUnitLifecycleDiagnostic>();

        if (semanticModel.GetDeclaredSymbol(fixture) is not INamedTypeSymbol fixtureType)
        {
            return new NUnitLifecyclePlan(Array.Empty<NUnitLifecycleHook>(),
                new[] { new NUnitLifecycleDiagnostic("NUNIT_LIFECYCLE_TYPE_UNRESOLVED",
                    fixture.Identifier.ValueText,
                    "Roslyn could not bind the fixture class; inherited NUnit hooks cannot be verified.",
                    Line(fixture)) }, 0);
        }

        var chain = new List<INamedTypeSymbol>();
        var visited = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        for (var type = fixtureType; type != null && type.SpecialType != SpecialType.System_Object;
             type = type.BaseType)
        {
            if (chain.Count >= MaxLevels || !visited.Add(type))
            {
                Error("NUNIT_LIFECYCLE_DEPTH_UNRESOLVED", type.Name,
                    "The inherited fixture chain is cyclic or exceeds the supported bound.",
                    Line(fixture));
                break;
            }
            if (type.TypeKind == TypeKind.Error)
            {
                Error("NUNIT_LIFECYCLE_BASE_UNRESOLVED", type.Name,
                    "A base type cannot be bound. Its NUnit hooks may affect setup and teardown.",
                    Line(fixture));
                break;
            }
            chain.Add(type);
        }
        chain.Reverse();

        var previouslySeen = new Dictionary<string, (int Level, string Kind)>(StringComparer.Ordinal);
        for (var level = 0; level < chain.Count; level++)
        {
            var type = chain[level];
            var declarations = type.DeclaringSyntaxReferences
                .Select(reference => reference.GetSyntax())
                .OfType<ClassDeclarationSyntax>()
                .Where(node => compilation.SyntaxTrees.Contains(node.SyntaxTree))
                .OrderBy(node => node.SyntaxTree.FilePath, StringComparer.Ordinal)
                .ThenBy(node => node.SpanStart)
                .ToArray();

            if (declarations.Length == 0)
            {
                // A known metadata base with no annotated lifecycle members is
                // harmless. An external annotated hook is not an implementation:
                // it may have unobservable state or external side effects.
                var externalHooks = type.GetMembers().OfType<IMethodSymbol>()
                    .Where(method => method.GetAttributes().Any(attr => AttributeKind(
                        attr.AttributeClass?.Name) != null))
                    .ToArray();
                if (externalHooks.Length > 0)
                    Error("NUNIT_LIFECYCLE_EXTERNAL_SOURCE", type.Name,
                        "Base NUnit lifecycle methods exist only in metadata; source bodies and effects are unavailable.",
                        Line(fixture));
                continue;
            }

            var found = new List<(NUnitLifecycleKind Kind, MethodDeclarationSyntax Node, IMethodSymbol? Symbol)>();
            foreach (var declaration in declarations)
            {
                var model = compilation.GetSemanticModel(declaration.SyntaxTree, ignoreAccessibility: true);
                foreach (var method in declaration.Members.OfType<MethodDeclarationSyntax>())
                {
                    var symbol = model.GetDeclaredSymbol(method) as IMethodSymbol;
                    var attributes = method.AttributeLists.SelectMany(list => list.Attributes).ToArray();
                    var kinds = attributes
                        .Select(attr => AttributeKind(attr.Name.ToString().Split('.').Last()))
                        .Where(kind => kind != null)
                        .Select(kind => kind!.Value).Distinct().ToArray();
                    foreach (var attr in attributes)
                    {
                        var possibleKind = AttributeKind(attr.Name.ToString().Split('.').Last());
                        if (possibleKind == null) continue;
                        var constructor = model.GetSymbolInfo(attr).Symbol as IMethodSymbol;
                        var attrType = constructor?.ContainingType;
                        if (attrType != null && attrType.TypeKind != TypeKind.Error
                            && !string.Equals(attrType.ToDisplayString(),
                                "NUnit.Framework." + possibleKind.Value + "Attribute",
                                StringComparison.Ordinal))
                            Error("NUNIT_LIFECYCLE_ATTRIBUTE_AMBIGUOUS", type.Name,
                                "A non-NUnit attribute has a lifecycle-like name. Do not infer NUnit execution from spelling alone.",
                                Line(attr));
                    }

                    if (kinds.Length > 1)
                        Error("NUNIT_LIFECYCLE_MULTIPLE_ATTRIBUTES", type.Name,
                            "A method has more than one lifecycle kind; its NUnit execution scope is ambiguous.",
                            Line(method));

                    if (symbol?.IsOverride == true
                        && (kinds.Length > 0
                            || previouslySeen.ContainsKey(method.Identifier.ValueText)))
                        Error("NUNIT_LIFECYCLE_OVERRIDE", type.Name,
                            "Overridden inherited NUnit hook may not invoke the base implementation.",
                            Line(method));
                    if (level > 0 && previouslySeen.ContainsKey(method.Identifier.ValueText)
                        && symbol?.IsOverride != true)
                        Error("NUNIT_LIFECYCLE_HIDDEN_METHOD", type.Name,
                            "A derived method hides an inherited lifecycle name; NUnit may skip the base implementation.",
                            Line(method));

                    foreach (var kind in kinds)
                    {
                        if (method.ParameterList.Parameters.Count != 0
                            || method.TypeParameterList != null
                            || (method.Modifiers.Any(token => token.ValueText == "async")
                                && method.ReturnType.ToString() == "void"))
                            Error("NUNIT_LIFECYCLE_SIGNATURE", type.Name,
                                "The lifecycle method has parameters, type parameters, or async void semantics.",
                                Line(method));
                        if (symbol == null)
                            Error("NUNIT_LIFECYCLE_METHOD_UNRESOLVED", type.Name,
                                "Roslyn cannot resolve a lifecycle method symbol.",
                                Line(method));
                        found.Add((kind, method, symbol));
                    }
                }
            }

            foreach (var group in found.GroupBy(item => item.Kind))
            {
                if (group.Count() > 1)
                    Error("NUNIT_LIFECYCLE_SAME_LEVEL_ORDER", type.Name,
                        "Multiple hooks of the same kind in one class have no defined NUnit order.",
                        group.Min(item => Line(item.Node)));
            }

            foreach (var item in found.OrderBy(x => x.Node.SyntaxTree.FilePath, StringComparer.Ordinal)
                         .ThenBy(x => x.Node.SpanStart))
            {
                previouslySeen[item.Node.Identifier.ValueText] = (level, item.Kind.ToString());
                var model = compilation.GetSemanticModel(item.Node.SyntaxTree, ignoreAccessibility: true);
                hooks.Add(new NUnitLifecycleHook(
                    item.Kind, type.ToDisplayString(), item.Node.Identifier.ValueText,
                    item.Node.SyntaxTree.FilePath, Line(item.Node), level,
                    parseActions(item.Node, model)));
            }
        }

        // Pairing at a source level matters on setup failure: in NUnit a
        // teardown at that level runs only when its matching setup level was
        // entered. Never simulate that with an unconditional finally.
        foreach (var teardown in hooks.Where(h => h.Kind is
                     NUnitLifecycleKind.TearDown or NUnitLifecycleKind.OneTimeTearDown))
        {
            var partner = teardown.Kind == NUnitLifecycleKind.TearDown
                ? NUnitLifecycleKind.SetUp : NUnitLifecycleKind.OneTimeSetUp;
            if (!hooks.Any(h => h.InheritanceLevel == teardown.InheritanceLevel
                && h.Kind == partner))
            {
                Error("NUNIT_LIFECYCLE_UNPAIRED_TEARDOWN", teardown.DeclaringType,
                    "This teardown has no same-level setup; failure-path invocation cannot be inferred safely.",
                    teardown.SourceLine);
            }
        }

        // NUnit InstancePerTestCase one-time hooks require static methods and
        // a shared fixture scope, which cannot be represented by instance flags.
        var instancePerTest = fixture.AttributeLists.SelectMany(list => list.Attributes)
            .Any(attribute => attribute.Name.ToString().Contains("FixtureLifeCycle", StringComparison.Ordinal)
                && attribute.ToString().Contains("InstancePerTestCase", StringComparison.Ordinal));
        if (instancePerTest && hooks.Any(h => h.Kind is NUnitLifecycleKind.OneTimeSetUp
                          or NUnitLifecycleKind.OneTimeTearDown))
        {
            Error("NUNIT_LIFECYCLE_PER_TEST_INSTANCE", fixtureType.Name,
                "One-time hooks with InstancePerTestCase require static, suite-scoped behavior.",
                Line(fixture));
        }

        // Existing one-SetUp fixtures keep the previous rendering path so this
        // feature is a safe, opt-in extension for inherited/extra phases only.
        var needsPlan = diagnostics.Count > 0
            || hooks.Any(h => h.Kind != NUnitLifecycleKind.SetUp
                              || h.InheritanceLevel < chain.Count - 1)
            || hooks.Count(h => h.Kind == NUnitLifecycleKind.SetUp) > 1;
        return needsPlan
            ? new NUnitLifecyclePlan(hooks, diagnostics, chain.Count)
            : null;

        void Error(string code, string sourceType, string reason, int line) =>
            diagnostics.Add(new NUnitLifecycleDiagnostic(code, sourceType, reason, line));
    }

    private static NUnitLifecycleKind? AttributeKind(string? name)
    {
        if (name == null) return null;
        if (name.EndsWith("Attribute", StringComparison.Ordinal))
            name = name[..^"Attribute".Length];
        return name switch
        {
            "SetUp" => NUnitLifecycleKind.SetUp,
            "TearDown" => NUnitLifecycleKind.TearDown,
            "OneTimeSetUp" => NUnitLifecycleKind.OneTimeSetUp,
            "OneTimeTearDown" => NUnitLifecycleKind.OneTimeTearDown,
            _ => null
        };
    }

    private static int Line(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
}
