using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Migrator.Application;

/// <summary>
/// Bounded, read-only necessary-condition review for a proposed POM Methods mapping.
/// A matching count and order of operation categories is NEVER proof of equivalence.
/// Deliberately does not execute, compile, approve, or apply target code.
/// </summary>
public sealed class PomMappingReviewer
{
    private const int MaxStatements = 80;
    private const int MaxTargetLength = 32768;
    private const int MaxHelperDepth = 4;

    private readonly PomSourceInspector _inspector;

    public PomMappingReviewer(PomSourceInspector inspector)
    {
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
    }

    public PomMappingReview Review(
        string sourceDirectory,
        string sourceType,
        string sourceMethod,
        int sourceLine,
        string expectedSourceTreeHash,
        IReadOnlyList<string> targetStatements,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceType) || string.IsNullOrWhiteSpace(sourceMethod))
            throw new ArgumentException("A source type and method must be supplied.");
        if (string.IsNullOrWhiteSpace(expectedSourceTreeHash))
            throw new ArgumentException("EXPECTED_POM_HASH_REQUIRED: call inspect_pom_sources first.");
        ArgumentNullException.ThrowIfNull(targetStatements);
        if (targetStatements.Count == 0 || targetStatements.Count > MaxStatements
            || targetStatements.Any(x => x == null)
            || targetStatements.Sum(x => (long)x.Length) > MaxTargetLength)
            throw new ArgumentException("TARGET_STATEMENTS_LIMIT: supply 1..80 concrete C# statements with at most 32 KiB total.");

        cancellationToken.ThrowIfCancellationRequested();
        var evidence = _inspector.Inspect(sourceDirectory, new[] { sourceType },
            cancellationToken: cancellationToken);
        if (!string.Equals(evidence.SourceTreeHash, expectedSourceTreeHash, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "STALE_POM_INSPECTION: source bytes changed. Inspect source again before reviewing the mapping.");

        var candidates = evidence.Helpers.Where(h => h.SourceType == sourceType
            && h.Method == sourceMethod
            && (sourceLine <= 0 || h.SourceLine == sourceLine)).ToArray();

        if (candidates.Length != 1)
            throw new InvalidOperationException(candidates.Length == 0
                ? "UNKNOWN_POM_METHOD: no source method matched. Check type, method and line."
                : "AMBIGUOUS_POM_METHOD: specify sourceLine to select an overload.");

        var method = candidates[0];
        var obligations = new List<PomMappingObligation>();
        var flags = new HashSet<string>(method.ReviewFlags, StringComparer.Ordinal)
        {
            "necessary_conditions_only",
            "runtime_behavior_not_verified"
        };
        var helperKeys = new HashSet<string>(StringComparer.Ordinal);
        ExpandMethod(method, 0);

        var parse = ParseTargets(targetStatements, method.ParameterNames);
        // The operation sequence alone can be complete while an agent
        // hardcodes the input instead of forwarding the source argument.
        // Compare AST identifier occurrences inside effect-producing call
        // expressions, excluding string literals and unused local declarations.
        var parameterFlows = method.ParameterNames
            .Distinct(StringComparer.Ordinal)
            .Select(name =>
            {
                var sourceUses = method.OrderedCalls.Sum(call =>
                    CountSourceArgumentUses(call.SourceText, name));
                parse.ParameterUses.TryGetValue(name, out var targetUses);
                return new PomParameterFlowEvidence(
                    name, sourceUses, targetUses, targetUses >= sourceUses);
            })
            .Where(flow => flow.SourceArgumentUses > 0)
            .ToArray();
        if (parameterFlows.Any(flow => !flow.Preserved))
            flags.Add("source_parameter_usage_not_preserved");
        var targetEffects = parse.Effects;
        foreach (var diagnostic in parse.Diagnostics)
            flags.Add(diagnostic);

        // A source action must have a corresponding target operation AFTER all
        // preceding obligations, otherwise a mapping can reorder application state.
        // This is deliberately stronger than comparing global click/Fill totals.
        var missing = new List<PomMappingObligation>();
        var cursor = 0;
        foreach (var obligation in obligations)
        {
            var position = -1;
            for (var i = cursor; i < targetEffects.Count; i++)
            {
                if (string.Equals(targetEffects[i].Effect, obligation.Effect, StringComparison.Ordinal))
                {
                    position = i;
                    break;
                }
            }
            if (position < 0)
                missing.Add(obligation);
            else
                cursor = position + 1;
        }

        if (targetStatements.Any(s => s.Contains("TODO", StringComparison.OrdinalIgnoreCase)
            || s.Contains("MIGRATOR:", StringComparison.Ordinal)))
        {
            flags.Add("target_contains_todo_or_placeholder");
        }

        if (targetEffects.Any(e => e.Effect == "unclassified_target_call"))
            flags.Add("unclassified_target_calls");
        if (targetEffects.Count > obligations.Count)
            flags.Add("extra_target_effects_need_review");

        var blockedTarget = parse.Diagnostics.Count > 0
            || flags.Contains("target_contains_todo_or_placeholder")
            || flags.Contains("source_obligations_truncated")
            || flags.Contains("recursive_source_helper")
            || flags.Contains("truncated_invocations");
        var status = blockedTarget ? "blocked"
            : missing.Count > 0 || parameterFlows.Any(flow => !flow.Preserved)
                ? "rejected_incomplete"
            : "requires_review";

        cancellationToken.ThrowIfCancellationRequested();
        // The inspection is bound to a source tree hash. A repeated inspection
        // ensures changed files did not slip in between source and target review.
        var after = _inspector.Inspect(sourceDirectory, new[] { sourceType },
            cancellationToken: cancellationToken);
        if (after.SourceTreeHash != evidence.SourceTreeHash)
            throw new InvalidOperationException("POM_SOURCE_CHANGED_DURING_REVIEW: retry from a new source snapshot.");

        return new PomMappingReview(
            "migrator-pom-mapping-review/v1alpha1",
            evidence.SourceTreeHash, sourceType, method.Method,
            method.SourceLine, method.MethodSha256,
            status, obligations, targetEffects, missing,
            flags.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            new[]
            {
                "Operation categories and lexical order are necessary conditions, not proof of matching selectors, arguments or business effects.",
                "Source method parameters used by invocation arguments must remain referenced in target action/filter invocation arguments; matching identifier counts do not prove correct binding or values.",
                "CSS locator string literals containing a backslash immediately before an attribute quote are blocked as likely double-escaped selectors.",
                "Control flow and retry structures are compared by presence/order categories, not equivalent branch predicates or exception behavior.",
                "A C# syntax-only review cannot prove target project compilation or NUnit test success.",
                "Browser origin, authentication, timing, DOM matches and application observables remain unverified.",
                "An unresolved helper or unsupported source operation is a missing obligation; never silently discard it.",
                "This tool never executes or applies the target statements and never returns a passed/approved mapping."
            })
        {
            ParameterFlows = parameterFlows
        };

        void ExpandMethod(SourceHelperEvidence current, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = current.SourceType + "|" + current.Method + "|" + current.SourceLine;
            if (!helperKeys.Add(key))
            {
                flags.Add("recursive_source_helper");
                return;
            }
            foreach (var item in current.ReviewFlags)
                flags.Add(item);

            if (depth >= MaxHelperDepth)
            {
                Add(current.SourceLine, current.Method, "unresolved_source_helper",
                    "helper-depth-limit");
                helperKeys.Remove(key);
                return;
            }

            // Lexical control-flow constructs must precede the source calls
            // they govern; appending them after all calls would reject a correct
            // target loop/try solely due to inconsistent ordering in the ledger.
            var events = current.OrderedCalls
                .Select(c => (Line: c.SourceLine, Flow: (PomControlFlowEvidence?)null, Call: (PomMethodCallEvidence?)c))
                .Concat(current.ControlFlow.Select(f =>
                    (Line: f.SourceLine, Flow: (PomControlFlowEvidence?)f, Call: (PomMethodCallEvidence?)null)))
                .OrderBy(x => x.Line).ThenBy(x => x.Flow == null ? 1 : 0);
            foreach (var entry in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Flow is PomControlFlowEvidence structure)
                {
                    Add(structure.SourceLine, structure.Kind + ": " + structure.Expression,
                        StructureEffect(structure.Kind),
                        current.SourceType + "." + current.Method + ".control_flow");
                    continue;
                }

                var call = entry.Call!;
                // Follow only receiverless/this direct same-class invocations.
                // Inherited methods or overloaded dispatch without a resolved symbol
                // must remain a separate review obligation.
                var sameClass = string.IsNullOrWhiteSpace(call.Receiver)
                    || call.Receiver is "this" or "base";
                if (sameClass && !IsBuiltInEffect(call, current.SourceType))
                {
                    var next = evidence.Helpers.Where(x =>
                        x.SourceType == current.SourceType && x.Method == call.Method)
                        .ToArray();
                    if (next.Length == 1 && next[0].MethodSha256.Length == 64)
                    {
                        ExpandMethod(next[0], depth + 1);
                        continue;
                    }
                }

                foreach (var effect in SourceEffects(call, current.SourceType))
                    Add(call.SourceLine, call.SourceText, effect,
                        current.SourceType + "." + current.Method);
            }
            helperKeys.Remove(key);
        }

        void Add(int line, string source, string kind, string provenance)
        {
            if (obligations.Count >= MaxStatements)
            {
                flags.Add("source_obligations_truncated");
                return;
            }
            obligations.Add(new PomMappingObligation(
                "op-" + (obligations.Count + 1).ToString("D3"),
                line, source.Length <= 320 ? source : source[..320],
                kind, provenance));
        }
    }

    private static bool IsBuiltInEffect(PomMethodCallEvidence call, string sourceType)
    {
        var effects = SourceEffects(call, sourceType).ToArray();
        return effects.Length > 0 && !effects.Contains("unresolved_source_helper", StringComparer.Ordinal);
    }

    private static string StructureEffect(string kind) => kind switch
    {
        "while" or "do" or "for" or "foreach" => "loop",
        "if" or "switch" or "conditional" => "conditional",
        "try" => "try",
        "catch" => "catch",
        "finally" => "finally",
        "throw" => "throw",
        "break" => "loop_exit",
        "retry_increment" => "retry_counter_update",
        _ => "unresolved_source_structure"
    };

    private static IEnumerable<string> SourceEffects(
        PomMethodCallEvidence call, string sourceType)
    {
        var name = call.Method;
        var chain = call.CallChain;
        var isStorage = sourceType == "LocalStorage"
            || call.Receiver.Contains("LocalStorage", StringComparison.Ordinal);
        if (isStorage && name is ("SetItem" or "SettingPeriod" or "RemoveItem" or "Clear"))
        {
            yield return name switch
            {
                "Clear" => "storage_clear",
                "RemoveItem" => "storage_remove",
                _ => "storage_write"
            };
            yield break;
        }

        if (name is "ExecuteJs" or "ExecuteJavaScript" or "ExecuteScript"
            or "Evaluate" or "EvaluateAsync" or "RunScript")
        {
            yield return "script_execution";
            yield break;
        }

        // A state getter is observational, not a wait. Dropping it may
        // silently turn a conditional reload into unconditional navigation.
        if (name == "Get" && chain.Any(n => n is "Displayed" or "Visible"
                or "Enabled" or "Exists"))
        {
            yield return "state_check";
            yield break;
        }

        var hasWaitChain = chain.Any(n => n is "Wait" or "WaitFor" or "Until")
            || name is "Wait" or "WaitFor";
        if (hasWaitChain)
        {
            yield return "state_wait";
            // A wait.EqualTo(true) is an expected state condition, not a
            // separate business assertion. A terminal Click after a wait
            // remains a distinct browser side effect and is counted below.
            if (name is "EqualTo" or "Wait" or "WaitFor" or "Until")
                yield break;
        }

        if (name is "ClearAndInputText" or "InputText" or "SendKeys" or "SendKeysAsync"
            or "Fill" or "FillAsync" or "Type" or "TypeAsync")
        {
            yield return "input";
            yield break;
        }

        if (name is "Click" or "ClickAsync")
        {
            yield return "click";
            yield break;
        }

        if (name is "Refresh" or "Reload" or "ReloadAsync")
        {
            yield return "reload";
            yield break;
        }

        if (name is "Fail" or "Throw" or "AreEqual" or "That"
            or "NotBe" or "Be" or "EqualTo" or "NotEqualTo")
        {
            yield return "assertion";
            yield break;
        }

        // For real project helper calls there is no lossless inference from name.
        // Their definitions must be found and expanded or the proposal is incomplete.
        if (!chain.Any(n => n is "Wait" or "WaitFor" or "Until"))
        {
            yield return "unresolved_source_helper";
        }
    }

    private sealed record ParsedTarget(
        IReadOnlyList<PomMappingTargetEffect> Effects,
        IReadOnlyList<string> Diagnostics,
        IReadOnlyDictionary<string, int> ParameterUses,
        IReadOnlyList<string> CatchTypes,
        IReadOnlyList<string> WhileConditions,
        IReadOnlyList<bool> ReloadInsideConditional);

    private static int CountSourceArgumentUses(string expression, string parameter)
    {
        var syntax = SyntaxFactory.ParseExpression(expression);
        return syntax.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
            .Count(x => string.Equals(x.Identifier.ValueText, parameter, StringComparison.Ordinal));
    }

    private static bool IsTargetParameterCarrier(string method) =>
        method is "FillAsync" or "TypeAsync" or "PressAsync" or "Filter"
            or "GetByText" or "GetByRole" or "GetByLabel"
            or "GetByPlaceholder" or "GetByTestId" or "SelectOptionAsync"
            or "SetInputFilesAsync" or "EvaluateAsync" or "AddInitScriptAsync"
            or "ToContainTextAsync" or "ToHaveTextAsync" or "ToHaveValueAsync";

    private static string TargetMethodName(InvocationExpressionSyntax inv) =>
        inv.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            _ => string.Empty
        };

    private static ParsedTarget ParseTargets(IReadOnlyList<string> statements,
        IReadOnlyList<string> parameterNames)
    {
        var source = "class __Candidate { async System.Threading.Tasks.Task Run() {\n"
            + string.Join("\n", statements) + "\n} }";
        var tree = CSharpSyntaxTree.ParseText(source);
        var errors = tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Take(10).Select(d => "target_syntax_" + d.Id).ToArray();
        if (errors.Length > 0)
            return new ParsedTarget(Array.Empty<PomMappingTargetEffect>(), errors,
                new Dictionary<string, int>(StringComparer.Ordinal),
                Array.Empty<string>(), Array.Empty<string>(), Array.Empty<bool>());

        var method = tree.GetRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>().First();
        var effects = new List<PomMappingTargetEffect>();
        var diagnostics = new List<string>();
        var parameterUses = parameterNames.Distinct(StringComparer.Ordinal)
            .ToDictionary(name => name, _ => 0, StringComparer.Ordinal);

        foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var name = TargetMethodName(invocation);
            if (name == "Locator"
                && invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression
                    is LiteralExpressionSyntax locatorLiteral
                && locatorLiteral.Token.ValueText.Contains("\\\"", StringComparison.Ordinal)
                && locatorLiteral.Token.ValueText.Contains("[", StringComparison.Ordinal))
                diagnostics.Add("target_selector_suspicious_double_escaping");

            if (!IsTargetParameterCarrier(name))
                continue;
            foreach (var identifier in invocation.ArgumentList.DescendantNodes()
                         .OfType<IdentifierNameSyntax>())
            {
                var variable = identifier.Identifier.ValueText;
                if (parameterUses.ContainsKey(variable))
                    parameterUses[variable]++;
            }
        }
        // Include control-flow in source order. Do not count nested invocations
        // twice: a ClickAsync(GetByText(...)) is one terminal click.
        var nodes = method.Body!.DescendantNodes().Where(n =>
            n is InvocationExpressionSyntax or IfStatementSyntax or WhileStatementSyntax
                or ForStatementSyntax or ForEachStatementSyntax or DoStatementSyntax
                or TryStatementSyntax or CatchClauseSyntax or FinallyClauseSyntax
                or ThrowStatementSyntax or BreakStatementSyntax
            || ((n is PostfixUnaryExpressionSyntax or PrefixUnaryExpressionSyntax)
                && n.Ancestors().Any(parent => parent is CatchClauseSyntax)
                && (n.IsKind(SyntaxKind.PostIncrementExpression)
                    || n.IsKind(SyntaxKind.PreIncrementExpression)
                    || n.IsKind(SyntaxKind.PostDecrementExpression)
                    || n.IsKind(SyntaxKind.PreDecrementExpression))))
            .Where(n => n is not InvocationExpressionSyntax
                || !n.Ancestors().OfType<InvocationExpressionSyntax>().Any())
            .OrderBy(n => n.SpanStart).ThenBy(n => n.Span.Length)
            .Take(200);
        foreach (var node in nodes)
        {
            var effect = node switch
            {
                WhileStatementSyntax or ForStatementSyntax or ForEachStatementSyntax
                    or DoStatementSyntax => "loop",
                IfStatementSyntax => "conditional",
                TryStatementSyntax => "try",
                CatchClauseSyntax => "catch",
                FinallyClauseSyntax => "finally",
                ThrowStatementSyntax => "throw",
                BreakStatementSyntax => "loop_exit",
                PostfixUnaryExpressionSyntax or PrefixUnaryExpressionSyntax => "retry_counter_update",
                InvocationExpressionSyntax invocation => TargetEffect(invocation),
                _ => "unclassified_target_call"
            };
            if (effect == null)
                continue;
            var line = node.GetLocation().GetLineSpan().StartLinePosition.Line;
            effects.Add(new PomMappingTargetEffect(effects.Count + 1,
                Math.Max(1, line), effect,
                node.ToString().Length <= 250 ? node.ToString() : node.ToString()[..250]));
        }
        return new ParsedTarget(effects, diagnostics.Distinct(StringComparer.Ordinal).ToArray(),
            parameterUses,
            method.DescendantNodes().OfType<CatchClauseSyntax>()
                .Select(c => c.Declaration?.Type.ToString() ?? string.Empty).ToArray(),
            method.DescendantNodes().OfType<WhileStatementSyntax>()
                .Select(c => c.Condition.ToString()).ToArray(),
            method.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(inv => TargetMethodName(inv) == "ReloadAsync")
                .Select(inv => inv.Ancestors().Any(ancestor => ancestor is IfStatementSyntax
                    or ConditionalExpressionSyntax or SwitchSectionSyntax)).ToArray());
    }

    private static string? TargetEffect(InvocationExpressionSyntax inv)
    {
        var name = inv.Expression switch
        {
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
            IdentifierNameSyntax id => id.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            _ => string.Empty
        };

        if (name is "IsVisibleAsync" or "IsHiddenAsync"
            or "IsEnabledAsync" or "IsDisabledAsync") return "state_check";
        if (name is "ClickAsync") return "click";
        if (name is "FillAsync" or "PressAsync" or "TypeAsync") return "input";
        if (name is "WaitForAsync" or "WaitForSelectorAsync"
            or "ToBeVisibleAsync" or "ToBeHiddenAsync"
            or "ToBeEnabledAsync" or "ToBeDisabledAsync"
            or "ToHaveCountAsync") return "state_wait";
        if (name is "ReloadAsync") return "reload";
        if (name is "Fail" or "Throws" or "ThrowsAsync" or "AreEqual" or "That"
            or "ToHaveTextAsync" or "ToContainTextAsync"
            or "ToEqualAsync" or "ToHaveValueAsync") return "assertion";
        if (name is "EvaluateAsync" or "AddInitScriptAsync")
        {
            var text = inv.ToString();
            if (text.Contains("localStorage.setItem", StringComparison.Ordinal))
                return "storage_write";
            if (text.Contains("localStorage.removeItem", StringComparison.Ordinal))
                return "storage_remove";
            if (text.Contains("localStorage.clear", StringComparison.Ordinal))
                return "storage_clear";
            return "script_execution";
        }
        if (name is "GotoAsync" or "NavigateAsync") return "navigation";
        // Locator builders in a terminal action chain are not separate state
        // changes. Preserve only meaningful unclassified calls for review.
        if (name is "Locator" or "GetByText" or "GetByRole" or "GetByTestId"
            or "GetByLabel" or "GetByPlaceholder" or "Filter"
            or "Nth" or "First" or "Last" or "Expect")
            return null;
        return "unclassified_target_call";
    }
}
