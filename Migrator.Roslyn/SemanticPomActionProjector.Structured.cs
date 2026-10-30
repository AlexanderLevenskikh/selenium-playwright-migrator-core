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

        void Block(string code, SyntaxNode node, string explanation)
        {
            blockers.Add(new Problem(code, node.SyntaxTree.FilePath,
                Line(node), Hash(node), explanation));
        }

        void Evidence(SyntaxNode node, IMethodSymbol? called, string effect,
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
                    : called.ContainingType.ToDisplayString(
                        SymbolDisplayFormat.FullyQualifiedFormat) + "." + called.Name,
                called?.ContainingType.ToDisplayString() ?? "",
                called?.ReturnType.ToDisplayString() ?? "void",
                effect, rendered, true));
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
                var trueBody = RenderBody(condition.Statement, model,
                    owner, depth + 1);
                var falseBody = condition.Else == null ? null
                    : RenderBody(condition.Else.Statement, model,
                        owner, depth + 1);
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

            if (statement is not ExpressionStatementSyntax expressionStatement
                || expressionStatement.Expression is not InvocationExpressionSyntax call)
            {
                Block("POM_UNSUPPORTED_STATEMENT", statement,
                    "No loops, local writes, switches, arbitrary throws, returns, exception handlers, or unrecognized source operations can be dropped.");
                return null;
            }

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
                && helper.Parameters.Length == 0 && call.ArgumentList.Arguments.Count == 0
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
                if (!activeHelpers.Add(helper))
                {
                    Block("POM_HELPER_RECURSION", call,
                        "Recursive source helper cannot be expanded deterministically.");
                    return null;
                }
                var sourceModel = compilation.GetSemanticModel(helperSyntax.SyntaxTree, true);
                var expanded = RenderMethod(helperSyntax, sourceModel,
                    helper, depth + 1, false);
                activeHelpers.Remove(helper);
                if (expanded == null)
                {
                    Block("POM_PRIVATE_HELPER_UNRESOLVED", call,
                        "Same-class private helper has an unresolved effect or control-flow path.");
                    return null;
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
                var single = RenderStatement(SyntaxFactory.ExpressionStatement(invocation),
                    model, owner, depth, false);
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
        out string target, out IMethodSymbol? sourceSymbol)
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
            + ") throw new System.ArgumentOutOfRangeException(nameof("
            + symbol.Name + "));";
        return true;
    }
}
