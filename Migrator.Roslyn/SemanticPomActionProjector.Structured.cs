using System.Globalization;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Migrator.Roslyn;

public static partial class SemanticPomActionProjector
{
    /// <summary>
    /// Closed, source-backed structural subset. All branches and every reachable
    /// same-class private helper are visited. An unsupported node blocks the entire
    /// method, rather than silently dropping its side effects.
    /// </summary>
    private static IReadOnlyList<string> ProjectStructuredStatements(
        MethodDeclarationSyntax root, SemanticModel semantic,
        IMethodSymbol? methodSymbol, CSharpCompilation compilation,
        List<Step> effects, List<Problem> blockers, CancellationToken cancel)
    {
        var activeHelpers = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        if (methodSymbol != null)
            activeHelpers.Add(methodSymbol);
        var boundedTimeouts = new HashSet<string>(StringComparer.Ordinal);
        var topLevel = new List<string>();
        var controlFlow = new Stack<string>();

        void Block(string code, SyntaxNode node, string explanation)
        {
            blockers.Add(new Problem(code, node.SyntaxTree.FilePath,
                Line(node), Hash(node), explanation));
        }

        void Evidence(SyntaxNode node, ISymbol? called, string effect,
            string rendered)
        {
            if (effects.Count >= MaxSteps)
            {
                Block("POM_COMPLEXITY_LIMIT", node, "More than 64 ordered source effects.");
                return;
            }
            effects.Add(new Step(effects.Count + 1,
                node.SyntaxTree.FilePath, Line(node), Hash(node),
                called == null ? "syntax:" + effect
                    : called.ContainingSymbol?.ToDisplayString(
                        SymbolDisplayFormat.FullyQualifiedFormat) + "." + called.Name,
                called?.ContainingType?.ToDisplayString() ?? "",
                called switch
                {
                    IMethodSymbol m => m.ReturnType.ToDisplayString(),
                    IPropertySymbol p => p.Type.ToDisplayString(),
                    IParameterSymbol p => p.Type.ToDisplayString(),
                    _ => effect == "conditional_guard" ? "bool" : "void"
                },
                effect, rendered, true)
            {
                ControlFlowPath = controlFlow.Count == 0 ? "root"
                    : string.Join("/", controlFlow.Reverse())
            });
        }

        string? RenderStatements(
            IEnumerable<StatementSyntax> statements, SemanticModel model,
            IMethodSymbol? owner, int depth, bool rootBody)
        {
            var rendered = new List<string>();
            var index = 0;
            foreach (var statement in statements)
            {
                cancel.ThrowIfCancellationRequested();
                var value = RenderStatement(statement, model, owner, depth,
                    rootBody && index == 0);
                if (value != null)
                {
                    rendered.Add(value);
                    if (rootBody) topLevel.Add(value);
                }
                index++;
                if (index > MaxSteps)
                {
                    Block("POM_COMPLEXITY_LIMIT", statement,
                        "Statement list exceeds the bounded source limit.");
                    break;
                }
            }
            return rendered.Count > 0 ? string.Join("\n", rendered) : null;
        }

        string? RenderStatement(StatementSyntax statement, SemanticModel model,
            IMethodSymbol? owner, int depth, bool firstAtRoot)
        {
            cancel.ThrowIfCancellationRequested();
            if (depth > 4)
            {
                Block("POM_NESTED_CONTROL_FLOW_LIMIT", statement,
                    "Nested helpers and conditional flows exceed the four-level limit.");
                return null;
            }

            if (firstAtRoot && statement is IfStatementSyntax firstGuard
                && TryTimeoutGuard(firstGuard, model, owner,
                    out var paramName, out var sourceGuard))
            {
                boundedTimeouts.Add(paramName);
                Evidence(firstGuard, owner, "timeout_bound_guard", sourceGuard);
                return sourceGuard;
            }

            if (statement is IfStatementSyntax condition)
            {
                if (!TryCondition(condition.Condition, model, owner,
                    out var targetGuard, out var guardSymbol))
                {
                    Block("POM_CONDITIONAL_GUARD_UNRESOLVED", condition.Condition,
                        "Only typed string parameter comparisons, literal FindElements.Count predicates and Boolean short-circuit composition are safe source facts.");
                    return null;
                }
                Evidence(condition.Condition, guardSymbol,
                    "conditional_guard", targetGuard);
                controlFlow.Push("if:true@" + Line(condition));
                var trueBody = RenderBody(condition.Statement, model,
                    owner, depth + 1);
                controlFlow.Pop();
                string? falseBody = null;
                if (condition.Else != null)
                {
                    controlFlow.Push("if:false@" + Line(condition));
                    falseBody = RenderBody(condition.Else.Statement, model,
                        owner, depth + 1);
                    controlFlow.Pop();
                }
                if (trueBody == null || (condition.Else != null && falseBody == null))
                {
                    Block("POM_CONDITIONAL_BRANCH_UNRESOLVED", condition,
                        "Every conditional arm must preserve all source effects.");
                    return null;
                }
                var statementText = "if (" + targetGuard + ")\n{\n"
                    + trueBody + "\n}";
                if (condition.Else != null)
                    statementText += "\nelse\n{\n" + falseBody + "\n}";
                return statementText;
            }

            if (statement is ForStatementSyntax loop)
            {
                if (!TryRecognizeBoundedRetry(loop, model, out var retry)
                    || retry == null)
                {
                    Block("POM_RETRY_UNSUPPORTED", loop,
                        "Only a source-literal 2..4 attempt for-loop with one Selenium WebDriverWait.Until, break on success, and a last-attempt rethrow of WebDriverTimeoutException is accepted.");
                    return null;
                }
                var header = "for (var " + retry.LocalName + " = 0; "
                    + retry.LocalName + " < " + retry.Attempts + "; "
                    + retry.LocalName + "++)";
                Evidence(loop, null, "bounded_retry", header);
                controlFlow.Push("retry:" + retry.Attempts + "@" + Line(loop));
                controlFlow.Push("retry:try@" + Line(retry.Until));
                var waited = RenderInvocation(retry.Until, model, owner, depth + 1);
                controlFlow.Pop();
                if (waited == null)
                {
                    controlFlow.Pop();
                    Block("POM_RETRY_WAIT_UNRESOLVED", retry.Until,
                        "The retry loop wait must be a supported typed Selenium WebDriverWait.Until expression.");
                    return null;
                }
                var rethrow = "if (" + retry.LocalName + " == "
                    + (retry.Attempts - 1) + ") throw;";
                controlFlow.Push("retry:catch@" + Line(retry.Catch));
                Evidence(retry.Rethrow, null, "retry_failure_rethrow", rethrow);
                controlFlow.Pop();
                controlFlow.Pop();
                return header + "\n{\n"
                    + "try\n{\n" + waited + "\nbreak;\n}\n"
                    + "catch (System.TimeoutException)\n{\n"
                    + rethrow + "\n}\n}";
            }

            if (statement is not ExpressionStatementSyntax expressionStatement
                || expressionStatement.Expression is not InvocationExpressionSyntax call)
            {
                Block("POM_UNSUPPORTED_STATEMENT", statement,
                    "No loops, local writes, switches, arbitrary throws, returns, exception handlers, or unrecognized source operations can be dropped.");
                return null;
            }

            return RenderInvocation(call, model, owner, depth);
        }

        string? RenderInvocation(InvocationExpressionSyntax call,
            SemanticModel model, IMethodSymbol? owner, int depth)
        {
            if (TryTranslate(call, model, owner, out var effect, out var target,
                out var receiverType, out var returnType,
                out var callSymbol, out var diagnostic, boundedTimeouts))
            {
                var called = model.GetSymbolInfo(call).Symbol as IMethodSymbol;
                Evidence(call, called, effect, target);
                return target;
            }

            if (model.GetSymbolInfo(call).Symbol is IMethodSymbol helper
                && owner != null
                && SymbolEqualityComparer.Default.Equals(
                    helper.ContainingType, owner.ContainingType)
                && helper.DeclaredAccessibility == Accessibility.Private
                && helper.Parameters.Length <= 2
                && helper.Parameters.Length == call.ArgumentList.Arguments.Count
                && helper.ReturnsVoid && !helper.IsStatic && !helper.IsVirtual
                && !helper.IsOverride && !helper.IsAsync && !helper.IsGenericMethod
                && (call.Expression is IdentifierNameSyntax
                    || call.Expression is MemberAccessExpressionSyntax
                    { Expression: ThisExpressionSyntax })
                && helper.DeclaringSyntaxReferences.Length == 1
                && helper.DeclaringSyntaxReferences[0].GetSyntax(cancel)
                    is MethodDeclarationSyntax helperSyntax
                && compilation.SyntaxTrees.Contains(helperSyntax.SyntaxTree))
            {
                // Bind all formal identifiers simultaneously, never by sequential
                // text replacement: e.g. helper(a, b) called as helper(b, a)
                // must not capture or overwrite the other argument.
                var boundValues = new Dictionary<string, string>(StringComparer.Ordinal);
                for (var i = 0; i < helper.Parameters.Length; i++)
                {
                    var formal = helper.Parameters[i];
                    var expression = call.ArgumentList.Arguments[i].Expression;
                    var valid = formal.RefKind == RefKind.None
                        && (formal.Type.SpecialType switch
                        {
                            SpecialType.System_String => SafeString(
                                expression, model, owner, out var resolvedString)
                                && boundValues.TryAdd(formal.Name, resolvedString),
                            SpecialType.System_Int32 => SafeInt(
                                expression, model, owner, out var resolvedInt)
                                && boundValues.TryAdd(formal.Name, resolvedInt),
                            _ => false
                        });
                    if (!valid)
                    {
                        Block("POM_HELPER_ARGUMENT_UNSUPPORTED", call,
                            "At most two source-bound string/int arguments, literal or unchanged owning-method parameters; no ref/out, dynamic values, conversions or side effects.");
                        return null;
                    }
                }
                var effectCheckpoint = effects.Count;
                if (!activeHelpers.Add(helper))
                {
                    Block("POM_HELPER_RECURSION", call,
                        "Recursive source helper cannot be expanded deterministically.");
                    return null;
                }
                var sourceModel = compilation.GetSemanticModel(helperSyntax.SyntaxTree, true);
                controlFlow.Push("helper:" + helper.Name + "@" + Line(call));
                var expanded = RenderMethod(helperSyntax, sourceModel,
                    helper, depth + 1, false);
                controlFlow.Pop();
                activeHelpers.Remove(helper);
                if (expanded == null)
                {
                    Block("POM_PRIVATE_HELPER_UNRESOLVED", call,
                        "Same-class private helper has an unresolved effect or control-flow path.");
                    return null;
                }
                if (boundValues.Count != 0)
                {
                    var boundBody = BindHelperArguments(expanded, boundValues);
                    if (boundBody == null)
                    {
                        Block("POM_HELPER_BINDING_INVALID", call,
                            "Generated helper statements could not be parsed for token-safe simultaneous binding.");
                        return null;
                    }
                    expanded = boundBody;
                    for (var i = effectCheckpoint; i < effects.Count; i++)
                    {
                        var boundEffect = BindHelperArguments(
                            effects[i].TargetStatement, boundValues);
                        if (boundEffect == null)
                        {
                            Block("POM_HELPER_BINDING_INVALID", call,
                                "Source helper effect cannot safely bind its arguments.");
                            return null;
                        }
                        effects[i] = effects[i] with { TargetStatement = boundEffect };
                    }
                }
                Evidence(call, helper, "private_helper_inline", expanded);
                return expanded;
            }

            Block(call.Expression is MemberAccessExpressionSyntax
                    { Name.Identifier.ValueText: "Until" }
                    && call.DescendantNodes().OfType<IdentifierNameSyntax>()
                        .Any(identifier => model.GetSymbolInfo(identifier).Symbol
                            is IParameterSymbol p && p.Type.SpecialType ==
                            SpecialType.System_Int32 && owner != null
                            && SymbolEqualityComparer.Default.Equals(
                                p.ContainingSymbol, owner))
                    ? "POM_DYNAMIC_WAIT_UNBOUNDED"
                    : "POM_ACTION_UNRESOLVED",
                call, diagnostic);
            return null;
        }

        string? RenderBody(StatementSyntax body, SemanticModel model,
            IMethodSymbol? owner, int depth) =>
            body is BlockSyntax block
                ? RenderStatements(block.Statements, model, owner, depth, false)
                : RenderStatement(body, model, owner, depth, false);

        string? RenderMethod(MethodDeclarationSyntax method, SemanticModel model,
            IMethodSymbol? owner, int depth, bool isRoot)
        {
            if (method.Body != null)
                return RenderStatements(method.Body.Statements, model,
                    owner, depth, isRoot);
            if (method.ExpressionBody?.Expression is InvocationExpressionSyntax invocation)
            {
                var single = RenderInvocation(invocation, model, owner, depth);
                if (single != null && isRoot) topLevel.Add(single);
                return single;
            }
            Block("POM_METHOD_BODY_UNRESOLVED", method,
                "Only expression-body invocations and explicit source blocks are supported.");
            return null;
        }

        var text = RenderMethod(root, semantic, methodSymbol, 0, true);
        return text == null || blockers.Count > 0
            ? Array.Empty<string>()
            : topLevel.ToArray();
    }

    private static bool TryCondition(ExpressionSyntax expression,
        SemanticModel model, IMethodSymbol? owner,
        out string target, out ISymbol? sourceSymbol)
    {
        target = "";
        sourceSymbol = null;
        if (expression is ParenthesizedExpressionSyntax parenthesized)
            return TryCondition(parenthesized.Expression, model, owner,
                out target, out sourceSymbol);
        if (expression is BinaryExpressionSyntax logical
            && (logical.IsKind(SyntaxKind.LogicalAndExpression)
                || logical.IsKind(SyntaxKind.LogicalOrExpression)))
        {
            if (!TryCondition(logical.Left, model, owner,
                    out var lhs, out var leftSymbol)
                || !TryCondition(logical.Right, model, owner,
                    out var rhs, out _))
                return false;
            target = "(" + lhs + ") "
                + (logical.IsKind(SyntaxKind.LogicalAndExpression) ? "&&" : "||")
                + " (" + rhs + ")";
            sourceSymbol = leftSymbol;
            return true;
        }
        if (expression is PrefixUnaryExpressionSyntax negation
            && negation.IsKind(SyntaxKind.LogicalNotExpression)
            && TryCondition(negation.Operand, model, owner,
                out var operand, out var operandSymbol))
        {
            target = "!(" + operand + ")";
            sourceSymbol = operandSymbol;
            return true;
        }
        if (expression is not BinaryExpressionSyntax binary)
            return false;

        var op = binary.Kind() switch
        {
            SyntaxKind.EqualsExpression => "==",
            SyntaxKind.NotEqualsExpression => "!=",
            SyntaxKind.GreaterThanExpression => ">",
            SyntaxKind.GreaterThanOrEqualExpression => ">=",
            SyntaxKind.LessThanExpression => "<",
            SyntaxKind.LessThanOrEqualExpression => "<=",
            _ => ""
        };
        if (op.Length == 0) return false;

        if (binary.Left is IdentifierNameSyntax numericName
            && model.GetSymbolInfo(numericName).Symbol is IParameterSymbol numeric
            && owner != null
            && SymbolEqualityComparer.Default.Equals(numeric.ContainingSymbol, owner)
            && numeric.Type.SpecialType == SpecialType.System_Int32
            && LiteralInt(binary.Right, out var integer))
        {
            target = numeric.Name + " " + op + " "
                + integer.ToString(CultureInfo.InvariantCulture);
            sourceSymbol = numeric;
            return true;
        }

        if (op is "==" or "!=" && binary.Left is IdentifierNameSyntax name
            && model.GetSymbolInfo(name).Symbol is IParameterSymbol parameter
            && owner != null
            && SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, owner)
            && parameter.Type.SpecialType == SpecialType.System_String
            && binary.Right is LiteralExpressionSyntax literal
            && literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            target = parameter.Name + " " + op + " "
                + JsonSerializer.Serialize(literal.Token.ValueText);
            sourceSymbol = parameter;
            return true;
        }

        if (binary.Left is MemberAccessExpressionSyntax read
            && read.Name.Identifier.ValueText == "Count"
            && read.Expression is InvocationExpressionSyntax find
            && model.GetSymbolInfo(read).Symbol is IPropertySymbol count
            && count.Type.SpecialType == SpecialType.System_Int32
            && TryFindBy(find, model, out var locator, expectMany: true)
            && LiteralInt(binary.Right, out var value) && value >= 0)
        {
            target = "(await " + locator + ".CountAsync()) " + op
                + " " + value.ToString(CultureInfo.InvariantCulture);
            sourceSymbol = count;
            return true;
        }
        return false;
    }

    private static bool TryTimeoutGuard(IfStatementSyntax guard,
        SemanticModel model, IMethodSymbol? method,
        out string parameter, out string target)
    {
        parameter = target = "";
        if (method == null || guard.Else != null
            || guard.Condition is not BinaryExpressionSyntax disjunction
            || !disjunction.IsKind(SyntaxKind.LogicalOrExpression)
            || disjunction.Left is not BinaryExpressionSyntax low
            || !low.IsKind(SyntaxKind.LessThanExpression)
            || disjunction.Right is not BinaryExpressionSyntax high
            || !high.IsKind(SyntaxKind.GreaterThanExpression)
            || low.Left is not IdentifierNameSyntax lower
            || high.Left is not IdentifierNameSyntax upper
            || lower.Identifier.ValueText != upper.Identifier.ValueText
            || !LiteralInt(low.Right, out var minimum)
            || !LiteralInt(high.Right, out var maximum)
            || minimum < 1 || maximum < minimum || maximum > 300000
            || model.GetSymbolInfo(lower).Symbol is not IParameterSymbol symbol
            || symbol.Type.SpecialType != SpecialType.System_Int32
            || !SymbolEqualityComparer.Default.Equals(
                symbol.ContainingSymbol, method)
            || !SymbolEqualityComparer.Default.Equals(
                symbol, model.GetSymbolInfo(upper).Symbol)
            || guard.Statement is not ThrowStatementSyntax thrown
            || thrown.Expression is not ObjectCreationExpressionSyntax construction
            || model.GetSymbolInfo(construction).Symbol is not IMethodSymbol ctor
            || ctor.ContainingType.ToDisplayString() !=
                "System.ArgumentOutOfRangeException"
            || construction.ArgumentList?.Arguments.Count != 1
            || construction.ArgumentList.Arguments[0].Expression
                is not InvocationExpressionSyntax nameofCall
            || nameofCall.Expression.ToString() != "nameof"
            || nameofCall.ArgumentList.Arguments.Count != 1
            || nameofCall.ArgumentList.Arguments[0].Expression
                is not IdentifierNameSyntax targetId
            || targetId.Identifier.ValueText != symbol.Name)
            return false;
        parameter = symbol.Name;
        target = "if (" + symbol.Name + " < "
            + minimum.ToString(CultureInfo.InvariantCulture) + " || "
            + symbol.Name + " > "
            + maximum.ToString(CultureInfo.InvariantCulture)
            + ") throw new System.ArgumentOutOfRangeException("
            + JsonSerializer.Serialize(symbol.Name) + ");";
        return true;
    }
}
