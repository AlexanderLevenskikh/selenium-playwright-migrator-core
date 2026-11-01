using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Migrator.Roslyn;

public static partial class SemanticPomActionProjector
{
    // This method verifies the EXACT source call, not the name of a logger.
    // It admits a fixed, bounded diagnostic code only; no exception object,
    // selectors, messages, stack traces, dynamic strings or user content.
    //
    // Source shape:
    // catch {
    //     try { System.Console.Error.WriteLine("MIGRATOR_CLEANUP_PAGE_CLOSE_FAILED"); }
    //     catch {}
    // }
    //
    // Even failure of the diagnostic sink is swallowed before the protected
    // outer bare throw; preserving original exception precedence.
    private static bool SafeFixedCleanupDiagnostic(
        SemanticModel model, CatchClauseSyntax catchBlock, string expectedCode)
    {
        if (catchBlock.Declaration != null || catchBlock.Filter != null
            || catchBlock.Block.Statements.Count != 1
            || catchBlock.Block.Statements[0] is not TryStatementSyntax logTry
            || logTry.Finally != null || logTry.Catches.Count != 1
            || logTry.Catches[0].Declaration != null
            || logTry.Catches[0].Filter != null
            || logTry.Catches[0].Block.Statements.Count != 0
            || logTry.Block.Statements.Count != 1
            || logTry.Block.Statements[0] is not ExpressionStatementSyntax
                { Expression: InvocationExpressionSyntax write }
            || write.Expression is not MemberAccessExpressionSyntax
                { Expression: MemberAccessExpressionSyntax errorProperty }
            || model.GetSymbolInfo(write).Symbol is not IMethodSymbol method
            || method.Name != "WriteLine"
            || method.ContainingType.ToDisplayString() != "System.IO.TextWriter"
            || method.Parameters.Length != 1
            || method.Parameters[0].Type.SpecialType != SpecialType.System_String
            || model.GetSymbolInfo(errorProperty).Symbol is not IPropertySymbol property
            || property.Name != "Error"
            || property.ContainingType.ToDisplayString() != "System.Console"
            || write.ArgumentList.Arguments.Count != 1
            || write.ArgumentList.Arguments[0].Expression
                is not LiteralExpressionSyntax literal
            || !literal.IsKind(SyntaxKind.StringLiteralExpression)
            || literal.Token.ValueText != expectedCode)
            return false;
        return true;
    }
}
