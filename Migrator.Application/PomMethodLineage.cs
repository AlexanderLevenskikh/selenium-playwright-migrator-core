using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Migrator.Application;

/// <summary>
/// Syntax-only evidence for multi-step Page Object and browser storage helpers.
/// Never creates executable mappings or claims to know runtime order across branches.
/// </summary>
internal static class PomMethodLineage
{
    private const int MaxStepsPerMethod = 64;
    private const int MaxTestFlows = 160;
    private const int MaxPrivateDepth = 3;

    public static IReadOnlyList<SourceHelperEvidence> Expand(
        IEnumerable<(string Source, ClassDeclarationSyntax Class)> allClasses,
        IEnumerable<SourceHelperEvidence> publicHelpers)
    {
        var classes = allClasses.ToArray();
        var publicEvidence = publicHelpers.ToArray();
        var enriched = new List<SourceHelperEvidence>(publicEvidence.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var evidence in publicEvidence)
        {
            var parent = classes.FirstOrDefault(c =>
                c.Source == evidence.SourcePath
                && c.Class.Identifier.ValueText == evidence.SourceType);
            if (parent.Class == null)
                continue;
            var matchingMethod = parent.Class.Members.OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.ValueText == evidence.Method
                    && Line(m) == evidence.SourceLine);
            if (matchingMethod == null)
                continue;

            Add(evidence, matchingMethod, privateHelper: false);
            FollowPrivate(parent.Class, matchingMethod, evidence.SourcePath,
                evidence.SourceType, depth: 0);
        }
        return enriched;

        void Add(SourceHelperEvidence evidence, MethodDeclarationSyntax method, bool privateHelper)
        {
            var key = evidence.SourcePath + "|" + evidence.SourceType
                + "|" + method.SpanStart;
            if (!seen.Add(key))
                return;
            enriched.Add(WithTrace(evidence, method, privateHelper));
        }

        void FollowPrivate(ClassDeclarationSyntax type, MethodDeclarationSyntax method,
            string path, string owner, int depth)
        {
            if (depth >= MaxPrivateDepth)
                return;
            var methods = type.Members.OfType<MethodDeclarationSyntax>().ToArray();
            var invocations = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(IsDirectSameClassCall)
                .Select(MethodName).Where(name => name != null)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            foreach (var name in invocations)
            {
                foreach (var child in methods.Where(m =>
                    m.Identifier.ValueText == name
                    && !m.Modifiers.Any(t => t.IsKind(SyntaxKind.PublicKeyword))))
                {
                    var evidence = new SourceHelperEvidence(
                        owner, child.Identifier.ValueText, path, Line(child),
                        child.DescendantNodes().OfType<InvocationExpressionSyntax>()
                            .Select(MethodName).Where(n => n != null)
                            .Select(n => n!).Distinct(StringComparer.Ordinal).Take(30).ToArray(),
                        StorageKeys(child),
                        "reachable-private-helper",
                        "Private helper is reachable by a same-class source invocation. Its behavior must be inspected and verified.");
                    var key = evidence.SourcePath + "|" + evidence.SourceType
                        + "|" + child.SpanStart;
                    if (seen.Add(key))
                    {
                        enriched.Add(WithTrace(evidence, child, privateHelper: true));
                        FollowPrivate(type, child, path, owner, depth + 1);
                    }
                }
            }
        }
    }

    public static IReadOnlyList<PomTestFlowEvidence> StorageFlows(
        IEnumerable<(string Source, ClassDeclarationSyntax Class)> allClasses)
    {
        var collected = new List<PomTestFlowEvidence>();
        foreach (var (source, type) in allClasses)
        {
            foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
            {
                var attrs = method.AttributeLists.SelectMany(list => list.Attributes)
                    .Select(a => a.Name.ToString().Split('.').Last())
                    .Select(name => name.EndsWith("Attribute", StringComparison.Ordinal)
                        ? name[..^"Attribute".Length] : name)
                    .ToArray();
                var setup = attrs.Any(a => a is "SetUp" or "OneTimeSetUp");
                var test = attrs.Any(a => a is "Test" or "TestCase" or "TestCaseSource");
                if (!setup && !test)
                    continue;

                var calls = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
                    .OrderBy(inv => inv.SpanStart).ToArray();
                var navigation = calls.Where(IsPageNavigation).ToArray();
                var firstNavigation = navigation.FirstOrDefault()?.SpanStart;
                var storage = new List<PomStorageOrderEvidence>();
                foreach (var call in calls.Where(IsStorageCall))
                {
                    var name = MethodName(call)!;
                    var timing = setup ? "setup_before_test"
                        : firstNavigation == null ? "no_navigation_in_method"
                        : call.SpanStart < firstNavigation.Value
                            ? "before_first_navigation"
                            : "after_first_navigation";
                    storage.Add(new PomStorageOrderEvidence(Line(call), name, timing,
                        Clip(call.ToString(), 240)));
                }

                if (storage.Count == 0 && navigation.Length == 0)
                    continue;
                var complex = HasComplexControlFlow(method);
                collected.Add(new PomTestFlowEvidence(
                    type.Identifier.ValueText, method.Identifier.ValueText,
                    source, Line(method), setup, navigation.Length > 0, complex,
                    storage.Take(60).ToArray(),
                    complex
                        ? "Only lexical order is known; conditions/loops/exception paths may change runtime order."
                        : "Syntax-level order within this method only. Fixture inheritance, prior navigation, origin, storage lifetime and actual browser execution are not verified."));
                if (collected.Count >= MaxTestFlows)
                    return collected;
            }
        }
        return collected;
    }

    private static SourceHelperEvidence WithTrace(SourceHelperEvidence evidence,
        MethodDeclarationSyntax method, bool privateHelper)
    {
        var steps = RootInvocations(method).Take(MaxStepsPerMethod).ToArray();
        var risks = new List<string> { "syntax_only_not_behavioral_proof" };
        var flags = ControlFlow(method);
        if (flags.Any(f => f.Kind is "while" or "do" or "for" or "foreach"))
            risks.Add("loop_or_retry");
        if (flags.Any(f => f.Kind is "try" or "catch" or "finally"))
            risks.Add("exception_or_retry_path");
        if (flags.Any(f => f.Kind is "if" or "switch" or "conditional"))
            risks.Add("conditional_behavior");
        if (steps.Any(s => s.InsideConditional || s.InsideLoop || s.InsideExceptionHandler))
            risks.Add("call_order_is_not_runtime_order");
        if (steps.Any(s => s.Role == "script_execution"))
            risks.Add("javascript_source_execution");
        if (steps.Any(s => s.Role == "storage_mutation_candidate"))
            risks.Add("storage_origin_and_timing_unverified");
        if (method.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Count(inv => !inv.Ancestors().OfType<InvocationExpressionSyntax>().Any())
            > MaxStepsPerMethod)
            risks.Add("truncated_invocations");
        return evidence with
        {
            OrderedCalls = steps,
            ControlFlow = flags,
            MethodSha256 = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(method.ToFullString()))).ToLowerInvariant(),
            IsReachablePrivateHelper = privateHelper,
            ReviewFlags = risks
        };
    }

    private static IReadOnlyList<PomMethodCallEvidence> RootInvocations(MethodDeclarationSyntax method)
    {
        return method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(inv => !inv.Ancestors().OfType<InvocationExpressionSyntax>().Any())
            .OrderBy(inv => inv.SpanStart)
            .Take(MaxStepsPerMethod)
            .Select((inv, index) =>
            {
                var name = MethodName(inv) ?? "unknown";
                var receiver = inv.Expression is MemberAccessExpressionSyntax ma
                    ? ma.Expression.ToString() : string.Empty;
                var chain = inv.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
                    .OrderBy(node => node.SpanStart)
                    .ThenBy(node => node.Span.Length)
                    .Select(MethodName).Where(n => !string.IsNullOrEmpty(n))
                    .Select(n => n!).Distinct().Take(24).ToArray();
                var ancestors = inv.Ancestors().TakeWhile(n => n != method).ToArray();
                return new PomMethodCallEvidence(index + 1, Line(inv),
                    Clip(receiver, 170), name, chain,
                    Clip(inv.ToString(), 330), Role(name, receiver, chain),
                    ancestors.Any(IsConditional),
                    ancestors.Any(IsLoop),
                    ancestors.Any(a => a is TryStatementSyntax or CatchClauseSyntax or FinallyClauseSyntax));
            }).ToArray();
    }

    private static IReadOnlyList<PomControlFlowEvidence> ControlFlow(MethodDeclarationSyntax method)
    {
        var flow = method.DescendantNodes()
            .Where(n => n is IfStatementSyntax or WhileStatementSyntax or ForStatementSyntax
                or ForEachStatementSyntax or DoStatementSyntax or SwitchStatementSyntax
                or TryStatementSyntax or CatchClauseSyntax or FinallyClauseSyntax
                or ThrowStatementSyntax or ConditionalExpressionSyntax)
            .OrderBy(n => n.SpanStart).Take(50)
            .Select(n => new PomControlFlowEvidence(FlowKind(n), Line(n),
                Clip(n switch
                {
                    IfStatementSyntax a => a.Condition.ToString(),
                    WhileStatementSyntax a => a.Condition.ToString(),
                    ForStatementSyntax a => a.Condition?.ToString() ?? "",
                    ForEachStatementSyntax a => a.Expression.ToString(),
                    DoStatementSyntax a => a.Condition.ToString(),
                    SwitchStatementSyntax a => a.Expression.ToString(),
                    ConditionalExpressionSyntax a => a.Condition.ToString(),
                    CatchClauseSyntax a => a.Declaration?.ToString() ?? "catch",
                    _ => ""
                }, 170)))
            .ToArray();
        return flow;
    }

    private static string FlowKind(SyntaxNode node) => node switch
    {
        IfStatementSyntax => "if",
        WhileStatementSyntax => "while",
        ForStatementSyntax => "for",
        ForEachStatementSyntax => "foreach",
        DoStatementSyntax => "do",
        SwitchStatementSyntax => "switch",
        ConditionalExpressionSyntax => "conditional",
        TryStatementSyntax => "try",
        CatchClauseSyntax => "catch",
        FinallyClauseSyntax => "finally",
        ThrowStatementSyntax => "throw",
        _ => "unknown"
    };

    private static bool IsConditional(SyntaxNode node) =>
        node is IfStatementSyntax or SwitchStatementSyntax or ConditionalExpressionSyntax;

    private static bool IsLoop(SyntaxNode node) =>
        node is WhileStatementSyntax or ForStatementSyntax or ForEachStatementSyntax or DoStatementSyntax;

    private static bool HasComplexControlFlow(MethodDeclarationSyntax method) =>
        method.DescendantNodes().Any(n => IsConditional(n) || IsLoop(n)
            || n is TryStatementSyntax or CatchClauseSyntax or FinallyClauseSyntax);

    private static string Role(string name, string receiver, IReadOnlyList<string> chain)
    {
        if (name is "SetItem" or "RemoveItem" or "Clear" or "SettingPeriod"
            && (receiver is "LocalStorage" or "this" or ""
                || receiver.Contains("LocalStorage", StringComparison.Ordinal)))
            return "storage_mutation_candidate";
        if (name is "ExecuteScript" or "ExecuteJavaScript" or "ExecuteJs"
            or "Evaluate" or "EvaluateAsync" or "RunScript" or "JavaScriptExecutor")
            return "script_execution";
        if (name is "Click" or "ClickAsync" or "ClearAndInputText" or "InputText"
            or "SendKeys" or "SendKeysAsync" or "FillAsync"
            or "Navigate" or "GotoAsync" or "Refresh" or "Reload")
            return "browser_action_candidate";
        if (chain.Any(n => n is "Wait" or "WaitFor" or "Until" or "Visible" or "Displayed"))
            return "wait_or_state_check_candidate";
        if (name is "Be" or "NotBe" or "EqualTo" or "NotEqualTo"
            or "BeGreaterThan" or "Fail" or "Contains")
            return "assertion_or_failure_candidate";
        return "project_helper_or_unknown";
    }

    private static bool IsDirectSameClassCall(InvocationExpressionSyntax inv) =>
        inv.Expression is IdentifierNameSyntax
        || inv.Expression is MemberAccessExpressionSyntax
            { Expression: ThisExpressionSyntax };

    private static bool IsPageNavigation(InvocationExpressionSyntax inv)
    {
        if (inv.Expression is not MemberAccessExpressionSyntax member)
            return false;
        var receiver = member.Expression.ToString();
        var method = member.Name.Identifier.ValueText;
        return (receiver is "Navigation" or "Browser")
            && ((method.StartsWith("Open", StringComparison.Ordinal)
                   && method.EndsWith("Page", StringComparison.Ordinal))
                || method.StartsWith("GoToPage", StringComparison.Ordinal));
    }

    private static bool IsStorageCall(InvocationExpressionSyntax inv) =>
        inv.Expression is MemberAccessExpressionSyntax member
        && (member.Expression.ToString() is "LocalStorage"
            || member.Expression.ToString().EndsWith(".LocalStorage", StringComparison.Ordinal))
        && member.Name.Identifier.ValueText is "Clear" or "SetItem" or "RemoveItem"
            or "SettingPeriod" or "GetItem" or "Key";

    private static string? MethodName(InvocationExpressionSyntax inv) => inv.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        _ => null
    };

    private static IReadOnlyList<string> StorageKeys(MethodDeclarationSyntax method) =>
        method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => MethodName(i) == "SetItem" && i.ArgumentList.Arguments.Count >= 1)
            .Select(i => i.ArgumentList.Arguments[0].Expression)
            .OfType<LiteralExpressionSyntax>()
            .Where(e => e.IsKind(SyntaxKind.StringLiteralExpression))
            .Select(e => e.Token.ValueText)
            .Distinct(StringComparer.Ordinal).Take(24).ToArray();

    private static int Line(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static string Clip(string text, int maximum) =>
        text.Length <= maximum ? text : text[..maximum] + "…";
}
