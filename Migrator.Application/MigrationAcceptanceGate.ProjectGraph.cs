using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Migrator.Application;

/// <summary>
/// Additional fail-closed source obligations for NUnit fixture topology and a
/// bounded same-class private helper graph. AST correspondence is NEVER a
/// semantic or behavioral equivalence claim. No cross-assembly resolution.
/// </summary>
public static partial class MigrationAcceptanceGate
{
    private const int ProjectGraphMaxFiles = 128;
    private const int ProjectGraphMaxDepth = 4;
    private const int ProjectGraphMaxRows = 512;

    private static void AppendProjectGraphObligations(
        MigrationAnalysis analysis, MigrationPreview preview,
        IReadOnlyDictionary<string, string> targets,
        List<SourceTargetLineage> rows, SortedSet<string> blockers)
    {
        if (analysis.Files.Count > ProjectGraphMaxFiles)
        {
            blockers.Add("PROJECT_GRAPH_BUDGET_EXCEEDED");
            return;
        }

        var mapping = preview.Files.ToDictionary(f => f.SourcePath,
            f => f.RelativePath, StringComparer.Ordinal);
        foreach (var file in analysis.Files.OrderBy(f => f.RelativePath,
                     StringComparer.Ordinal))
        {
            var sourcePath = File.Exists(analysis.SourceScope)
                ? analysis.SourceScope
                : Path.Combine(analysis.SourceScope,
                    file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(sourcePath))
                continue;
            var sourceRoot = CSharpSyntaxTree.ParseText(
                File.ReadAllText(sourcePath), path: file.RelativePath).GetRoot();
            mapping.TryGetValue(file.RelativePath, out var targetPath);
            var targetRoot = targetPath != null
                && targets.TryGetValue(targetPath, out var targetText)
                ? CSharpSyntaxTree.ParseText(targetText, path: targetPath).GetRoot()
                : null;
            var targetClasses = targetRoot?.DescendantNodes()
                .OfType<ClassDeclarationSyntax>().ToArray()
                ?? Array.Empty<ClassDeclarationSyntax>();
            foreach (var sourceClass in sourceRoot.DescendantNodes()
                         .OfType<ClassDeclarationSyntax>())
            {
                if (rows.Count >= ProjectGraphMaxRows)
                {
                    blockers.Add("PROJECT_GRAPH_BUDGET_EXCEEDED");
                    return;
                }
                var hooks = sourceClass.Members.OfType<MethodDeclarationSyntax>()
                    .Where(m => TryPhase(m, out _)).ToArray();
                var isSetup = HasNUnitAttribute(sourceClass, "SetUpFixture");
                var isTestFixture = HasNUnitAttribute(sourceClass, "TestFixture")
                    || hooks.Any(m => TryPhase(m, out var phase)
                        && phase == "test");
                if (!isSetup && !isTestFixture)
                    continue;

                // One source SetUpFixture maps to exactly one target SetUpFixture.
                // A same-named class without the NUnit attribute is NOT enough.
                if (isSetup)
                {
                    var setupTargets = targetClasses.Where(c =>
                        HasNUnitAttribute(c, "SetUpFixture")).ToArray();
                    var matched = setupTargets.Length == 1 ? setupTargets[0] : null;
                    GraphRow(sourceClass, sourceClass.Identifier.ValueText,
                        "setup_fixture", "nunit_scope", file.RelativePath,
                        targetPath, matched, rows);
                    if (setupTargets.Length != 1)
                        blockers.Add(setupTargets.Length == 0
                            ? "NUNIT_SETUP_FIXTURE_MISSING"
                            : "NUNIT_SETUP_FIXTURE_TARGET_AMBIGUOUS");
                }

                // A source fixture deriving from an inherited NUnit class
                // cannot be accepted as a target fixture that silently drops
                // the base. We do not claim any base method has actually run.
                if (isTestFixture && sourceClass.BaseList != null)
                {
                    var targetOwners = targetClasses.Where(c =>
                        c.Identifier.ValueText == sourceClass.Identifier.ValueText
                        || hooks.Any(sourceHook => c.Members
                            .OfType<MethodDeclarationSyntax>().Any(targetHook =>
                                targetHook.Identifier.ValueText
                                    == sourceHook.Identifier.ValueText
                                && TryPhase(targetHook, out var phase)
                                && TryPhase(sourceHook, out var expected)
                                && phase == expected))).ToArray();
                    var owner = targetOwners.Length == 1 ? targetOwners[0] : null;
                    if (targetOwners.Length > 1)
                        blockers.Add("NUNIT_FIXTURE_TARGET_OWNER_AMBIGUOUS");
                    foreach (var baseType in sourceClass.BaseList.Types)
                    {
                        var baseName = baseType.Type.ToString();
                        var corresponding = owner?.BaseList?.Types.FirstOrDefault(t =>
                            string.Equals(t.Type.ToString(), baseName,
                                StringComparison.Ordinal));
                        GraphRow(baseType, sourceClass.Identifier.ValueText,
                            "fixture_base", "inherited_lifecycle",
                            file.RelativePath, targetPath,
                            corresponding, rows);
                        if (corresponding == null)
                            blockers.Add("NUNIT_INHERITED_BASE_NOT_PRESERVED");
                        else
                            blockers.Add("NUNIT_INHERITED_LIFECYCLE_UNVERIFIED");
                    }
                }

                // In-method matches cannot prove that a private helper
                // still performs the Selenium action/assertion it used to.
                var sourceHelpers = sourceClass.Members
                    .OfType<MethodDeclarationSyntax>()
                    .Where(m => IsPrivateHelper(m) && !TryPhase(m, out _))
                    .ToArray();
                foreach (var testOrHook in hooks)
                {
                    if (!TryPhase(testOrHook, out var phase))
                        continue;
                    var sourceBody = (SyntaxNode?)testOrHook.Body
                        ?? testOrHook.ExpressionBody;
                    if (sourceBody == null)
                        continue;
                    var possibleTargets = targetRoot?.DescendantNodes()
                        .OfType<MethodDeclarationSyntax>()
                        .Where(m => m.Identifier.ValueText ==
                            testOrHook.Identifier.ValueText
                            && TryPhase(m, out var targetPhase)
                            && targetPhase == phase).ToArray()
                        ?? Array.Empty<MethodDeclarationSyntax>();
                    var targetMethod = possibleTargets.Length == 1
                        ? possibleTargets[0] : null;
                    // Target invocation candidates may be either inlined
                    // into the test or in same-class helpers of that target.
                    var targetCalls = new List<InvocationExpressionSyntax>();
                    if (targetMethod != null)
                    {
                        var targetOwner = targetMethod.Ancestors()
                            .OfType<ClassDeclarationSyntax>().FirstOrDefault();
                        if (targetOwner != null)
                            targetCalls.AddRange(targetOwner.DescendantNodes()
                                .OfType<InvocationExpressionSyntax>());
                    }
                    var used = new HashSet<int>();
                    var active = new HashSet<MethodDeclarationSyntax>();
                    void Walk(SyntaxNode body, int depth)
                    {
                        if (depth > ProjectGraphMaxDepth)
                        {
                            blockers.Add("PROJECT_PRIVATE_HELPER_GRAPH_UNRESOLVED");
                            return;
                        }
                        foreach (var call in body.DescendantNodesAndSelf()
                                     .OfType<InvocationExpressionSyntax>())
                        {
                            var sourceCallName = CallName(call);
                            if (call.Expression is not IdentifierNameSyntax
                                    and not MemberAccessExpressionSyntax
                                    { Expression: ThisExpressionSyntax })
                                continue;
                            var candidates = sourceHelpers.Where(h =>
                                h.Identifier.ValueText == sourceCallName
                                && h.ParameterList.Parameters.Count ==
                                    call.ArgumentList.Arguments.Count).ToArray();
                            if (candidates.Length == 0)
                                continue;
                            if (candidates.Length != 1)
                            {
                                blockers.Add("PROJECT_PRIVATE_HELPER_GRAPH_UNRESOLVED");
                                continue;
                            }
                            var helper = candidates[0];
                            var helperBody = (SyntaxNode?)helper.Body
                                ?? helper.ExpressionBody;
                            if (helperBody == null || !active.Add(helper))
                            {
                                blockers.Add("PROJECT_PRIVATE_HELPER_GRAPH_UNRESOLVED");
                                continue;
                            }
                            if (rows.Count >= ProjectGraphMaxRows)
                            {
                                blockers.Add("PROJECT_GRAPH_BUDGET_EXCEEDED");
                                active.Remove(helper);
                                return;
                            }
                            // Reached helper effects are explicit source
                            // obligations, not inferred only from call names.
                            foreach (var effect in helperBody.DescendantNodesAndSelf())
                            {
                                var isCall = effect is InvocationExpressionSyntax;
                                var isMutation = effect is AssignmentExpressionSyntax
                                    or PrefixUnaryExpressionSyntax
                                    or PostfixUnaryExpressionSyntax;
                                var isPath = effect is ThrowStatementSyntax
                                    or CatchClauseSyntax
                                    or IfStatementSyntax;
                                if (!isCall && !isMutation && !isPath)
                                    continue;
                                if (rows.Count >= ProjectGraphMaxRows)
                                {
                                    blockers.Add("PROJECT_GRAPH_BUDGET_EXCEEDED");
                                    break;
                                }
                                var kind = isCall
                                    ? "helper_" + Classify(effect, phase)
                                    : isMutation ? "helper_state_mutation"
                                    : "helper_" + (ControlFlowKind(effect) ?? "path");
                                SyntaxNode? match = null;
                                if (effect is InvocationExpressionSyntax sourceInvocation)
                                {
                                    var names = TargetNames(CallName(sourceInvocation));
                                    for (var i = 0; i < targetCalls.Count; i++)
                                    {
                                        if (used.Contains(i)
                                            || !names.Contains(CallName(targetCalls[i]),
                                                StringComparer.Ordinal))
                                            continue;
                                        used.Add(i);
                                        match = targetCalls[i];
                                        break;
                                    }
                                }
                                else if (targetMethod != null)
                                {
                                    // Same-shape mutation/exception remains
                                    // candidate_unverified, never proven.
                                    match = targetMethod.DescendantNodes()
                                        .FirstOrDefault(n => n.Kind() == effect.Kind());
                                }
                                GraphRow(effect, helper.Identifier.ValueText,
                                    kind, phase, file.RelativePath,
                                    targetPath, match, rows);
                            }
                            Walk(helperBody, depth + 1);
                            active.Remove(helper);
                        }
                    }
                    Walk(sourceBody, 0);
                }
            }
        }
    }

    private static bool HasNUnitAttribute(MemberDeclarationSyntax member,
        string expected) => member.AttributeLists.SelectMany(x => x.Attributes)
            .Select(x => x.Name.ToString().Split('.').Last())
            .Any(name => string.Equals(name, expected,
                    StringComparison.Ordinal)
                || string.Equals(name, expected + "Attribute",
                    StringComparison.Ordinal));

    private static bool IsPrivateHelper(MethodDeclarationSyntax method)
    {
        if (method.Modifiers.Any(SyntaxKind.PublicKeyword)
            || method.Modifiers.Any(SyntaxKind.ProtectedKeyword)
            || method.Modifiers.Any(SyntaxKind.InternalKeyword)
            || method.Modifiers.Any(SyntaxKind.StaticKeyword)
            || method.Modifiers.Any(SyntaxKind.VirtualKeyword)
            || method.Modifiers.Any(SyntaxKind.OverrideKeyword))
            return false;
        return method.ExplicitInterfaceSpecifier == null;
    }

    private static void GraphRow(SyntaxNode source, string method,
        string kind, string phase, string sourcePath, string? targetPath,
        SyntaxNode? target, List<SourceTargetLineage> rows)
    {
        var line = source.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var id = Digest(sourcePath + "\n" + method + "\n" + kind
            + "\n" + source.ToFullString() + "\n" + rows.Count);
        rows.Add(new SourceTargetLineage(id, sourcePath, method, phase,
            line, kind, Digest(source.ToFullString()),
            target == null ? null : targetPath,
            target?.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            target == null ? null : Digest(target.ToFullString()),
            target == null ? "missing_target" : "candidate_unverified",
            target == null
                ? "Source project-level obligation has no corresponding target AST node."
                : "Syntactic candidate only: transitive calls, lifecycle order, "
                  + "selectors, operands and browser behavior are not proven."));
    }
}
