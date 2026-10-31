using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Migrator.Roslyn;

public static partial class SemanticPomActionProjector
{
    /// <summary>
    /// Replace only Roslyn identifier tokens in a generated target statement.
    /// Selector literals, URL literals and member names are never rewritten.
    /// Caller argument was independently symbol-verified as an enclosing
    /// string parameter or literal by SafeString.
    /// </summary>
    private static string? BindHelperArguments(
        string targetStatements, IReadOnlyDictionary<string, string> bindings)
    {
        const string prefix = "{\n";
        var suffix = "\n}";
        var syntax = SyntaxFactory.ParseStatement(prefix + targetStatements + suffix);
        if (syntax.ContainsDiagnostics)
        {
            // Evidence for an if-guard is an expression ("a == b"),
            // not a standalone C# statement. Parse it with a synthetic
            // semicolon AFTER the original span, preserving all offsets.
            suffix = ";\n}";
            syntax = SyntaxFactory.ParseStatement(prefix + targetStatements + suffix);
            if (syntax.ContainsDiagnostics)
                return null;
        }

        // Coordinates refer to the original syntax. Rewrite from right to
        // left so swaps and overlapping identifiers cannot cascade.
        var references = syntax.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Where(node => bindings.ContainsKey(node.Identifier.ValueText)
                && !(node.Parent is MemberAccessExpressionSyntax access
                     && ReferenceEquals(access.Name, node)))
            .OrderByDescending(node => node.SpanStart)
            .ToArray();
        var text = targetStatements;
        foreach (var node in references)
        {
            var start = node.SpanStart - prefix.Length;
            var end = node.Span.End - prefix.Length;
            if (start < 0 || end > text.Length || start > end)
                return null;
            text = text[..start] + bindings[node.Identifier.ValueText] + text[end..];
        }
        return SyntaxFactory.ParseStatement(prefix + text + suffix)
            .ContainsDiagnostics ? null : text;
    }

    private sealed record BoundedRetrySource(
        string LocalName, int Attempts, InvocationExpressionSyntax Until,
        CatchClauseSyntax Catch, IfStatementSyntax Rethrow);

    /// <summary>
    /// Exact finite-loop shape:
    /// for (var attempt = 0; attempt &lt; 2; attempt++) {
    ///   try { wait.Until(...); break; }
    ///   catch (WebDriverTimeoutException) { if (attempt == 1) throw; }
    /// }
    /// Refuses all other control flow, catch filters, exception mappings,
    /// unbounded loops, failure branches and external side effects.
    /// </summary>
    private static bool TryRecognizeBoundedRetry(
        ForStatementSyntax source, SemanticModel model,
        out BoundedRetrySource? retry)
    {
        retry = null;
        if (source.Declaration is not { Variables.Count: 1 } declaration
            || source.Initializers.Count != 0
            || source.Incrementors.Count != 1
            || declaration.Variables[0].Initializer?.Value is not ExpressionSyntax initialization
            || !LiteralInt(initialization, out var initialValue) || initialValue != 0
            || model.GetDeclaredSymbol(declaration.Variables[0]) is not ILocalSymbol symbol
            || symbol.Type.SpecialType != SpecialType.System_Int32
            || source.Condition is not BinaryExpressionSyntax condition
            || !condition.IsKind(SyntaxKind.LessThanExpression)
            || condition.Left is not IdentifierNameSyntax left
            || !SymbolEqualityComparer.Default.Equals(
                model.GetSymbolInfo(left).Symbol, symbol)
            || !LiteralInt(condition.Right, out var attempts)
            || attempts is < 2 or > 4
            || source.Incrementors[0] is not PostfixUnaryExpressionSyntax inc
            || !inc.IsKind(SyntaxKind.PostIncrementExpression)
            || inc.Operand is not IdentifierNameSyntax incr
            || !SymbolEqualityComparer.Default.Equals(
                model.GetSymbolInfo(incr).Symbol, symbol)
            || source.Statement is not BlockSyntax block
            || block.Statements.Count != 1
            || block.Statements[0] is not TryStatementSyntax tryStatement
            || tryStatement.Finally != null
            || tryStatement.Catches.Count != 1
            || tryStatement.Block.Statements.Count != 2
            || tryStatement.Block.Statements[0] is not ExpressionStatementSyntax
                { Expression: InvocationExpressionSyntax until }
            || tryStatement.Block.Statements[1] is not BreakStatementSyntax
            || model.GetSymbolInfo(until).Symbol is not IMethodSymbol waitMethod
            || waitMethod.Name != "Until"
            || waitMethod.ContainingType.ToDisplayString() !=
                "OpenQA.Selenium.Support.UI.WebDriverWait")
            return false;

        var catchClause = tryStatement.Catches[0];
        if (catchClause.Filter != null
            || catchClause.Declaration == null
            || catchClause.Declaration.Identifier.ValueText.Length != 0
            || model.GetTypeInfo(catchClause.Declaration.Type).Type?.ToDisplayString()
                != "OpenQA.Selenium.WebDriverTimeoutException"
            || catchClause.Block.Statements.Count != 1
            || catchClause.Block.Statements[0] is not IfStatementSyntax guard
            || guard.Else != null
            || guard.Condition is not BinaryExpressionSyntax eq
            || !eq.IsKind(SyntaxKind.EqualsExpression)
            || eq.Left is not IdentifierNameSyntax counter
            || !SymbolEqualityComparer.Default.Equals(
                model.GetSymbolInfo(counter).Symbol, symbol)
            || !LiteralInt(eq.Right, out var lastAttempt)
            || lastAttempt != attempts - 1
            || guard.Statement is not ThrowStatementSyntax { Expression: null })
            return false;
        retry = new BoundedRetrySource(
            symbol.Name, attempts, until, catchClause, guard);
        return true;
    }
}
