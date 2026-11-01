using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Migrator.Roslyn;

public static partial class SemanticPomActionProjector
{
    // Strictly typed source-attestation for one ordinary NUnit TearDown.
    // Exact source shape:
    // try { await Page.CloseAsync(); }
    // catch (Microsoft.Playwright.TargetClosedException) {
    //     try { System.Console.Error.WriteLine("MIGRATOR_CLEANUP_PAGE_CLOSE_FAILED"); }
    //     catch {}
    // }
    // finally {
    //     try { await Context.CloseAsync(); }
    //     catch (Microsoft.Playwright.TargetClosedException) {
    //         try { System.Console.Error.WriteLine("MIGRATOR_CLEANUP_CONTEXT_CLOSE_FAILED"); }
    //         catch {}
    //     }
    // }
    // Non-TargetClosed exceptions remain explicitly unsupported.
    private static bool TryVerifyTypedTargetClosedTearDown(
        SemanticModel model, MethodDeclarationSyntax teardown,
        IFieldSymbol page, IFieldSymbol context,
        out CatchClauseSyntax? pageCatch, out CatchClauseSyntax? contextCatch)
    {
        pageCatch = null;
        contextCatch = null;
        if (teardown.Body?.Statements.Count != 1 ||
            teardown.Body.Statements[0] is not TryStatementSyntax outer ||
            outer.Block.Statements.Count != 1 ||
            outer.Catches.Count != 1 ||
            outer.Finally?.Block.Statements.Count != 1 ||
            outer.Finally.Block.Statements[0] is not TryStatementSyntax inner ||
            inner.Finally != null || inner.Catches.Count != 1 ||
            inner.Block.Statements.Count != 1 ||
            !ExactOwnedClose(model, outer.Block.Statements[0], page, "IPage") ||
            !ExactOwnedClose(model, inner.Block.Statements[0], context,
                "IBrowserContext") ||
            !SafeFixedCleanupDiagnostic(model, outer.Catches[0],
                "MIGRATOR_CLEANUP_PAGE_CLOSE_FAILED",
                requireTypedTargetClosed: true) ||
            !SafeFixedCleanupDiagnostic(model, inner.Catches[0],
                "MIGRATOR_CLEANUP_CONTEXT_CLOSE_FAILED",
                requireTypedTargetClosed: true))
            return false;
        pageCatch = outer.Catches[0];
        contextCatch = inner.Catches[0];
        return true;
    }

    private static bool ExactOwnedClose(SemanticModel model,
        StatementSyntax statement, IFieldSymbol expected, string typeName)
    {
        if (statement is not ExpressionStatementSyntax
            { Expression: AwaitExpressionSyntax
                { Expression: InvocationExpressionSyntax invocation } } ||
            invocation.ArgumentList.Arguments.Count != 0 ||
            invocation.Expression is not MemberAccessExpressionSyntax member ||
            model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method ||
            method.Name != "CloseAsync" ||
            method.ContainingType.ToDisplayString() !=
                "Microsoft.Playwright." + typeName ||
            method.ContainingAssembly?.Name != "Microsoft.Playwright")
            return false;
        var referenced = model.GetSymbolInfo(member.Expression).Symbol;
        if (member.Expression is ParenthesizedExpressionSyntax paren)
            referenced = model.GetSymbolInfo(paren.Expression).Symbol;
        return referenced is IFieldSymbol field
            && SymbolEqualityComparer.Default.Equals(field, expected);
    }
}
