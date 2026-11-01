using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Migrator.Roslyn;

public static partial class SemanticPomActionProjector
{
    // Strict preview.47 opt-in. No blanket exception handling, no synthetic
    // failure promoted as a real Playwright API failure.
    private static bool TryVerifyOneTimeTargetClosedCleanup(
        SemanticModel model, MethodDeclarationSyntax oneDown,
        IFieldSymbol browser, IFieldSymbol playwright,
        out CatchClauseSyntax? closeCatch,
        out CatchClauseSyntax? disposeCatch, out string reason)
    {
        closeCatch = null;
        disposeCatch = null;
        reason = "P47_ONE_TIME_TEARDOWN_SHAPE_UNPROVEN";
        if (oneDown.Body?.Statements.Count != 1
            || oneDown.Body.Statements[0] is not TryStatementSyntax outer
            || outer.Catches.Count != 0
            || outer.Block.Statements.Count != 1
            || outer.Finally?.Block.Statements.Count != 1
            || outer.Block.Statements[0] is not TryStatementSyntax close
            || close.Catches.Count != 1 || close.Finally != null
            || close.Block.Statements.Count != 1
            || outer.Finally.Block.Statements[0] is not TryStatementSyntax dispose
            || dispose.Catches.Count != 1 || dispose.Finally != null
            || dispose.Block.Statements.Count != 1)
            return false;

        if (!ExactOwnedClose(model, close.Block.Statements[0],
                browser, "IBrowser"))
        {
            reason = "P47_BROWSER_CLOSE_SOURCE_OWNER_UNPROVEN";
            return false;
        }
        if (!ExactOwnedDispose(model, dispose.Block.Statements[0], playwright))
        {
            reason = "P47_PLAYWRIGHT_DISPOSE_SOURCE_OWNER_UNPROVEN";
            return false;
        }
        if (!ExactRealTargetClosedFilter(model, close.Catches[0],
                out var browserReason))
        {
            reason = "P47_BROWSER_" + browserReason;
            return false;
        }
        if (!ExactRealTargetClosedFilter(model, dispose.Catches[0],
                out var disposeReason))
        {
            reason = "P47_DISPOSE_" + disposeReason;
            return false;
        }
        if (!SafeFixedCleanupDiagnostic(model, close.Catches[0],
                "MIGRATOR_CLEANUP_BROWSER_CLOSE_FAILED",
                requireTargetClosedClrIdentity: true))
        {
            reason = "P47_BROWSER_FIXED_GUARDED_DIAGNOSTIC_UNPROVEN";
            return false;
        }
        if (!SafeFixedCleanupDiagnostic(model, dispose.Catches[0],
                "MIGRATOR_CLEANUP_PLAYWRIGHT_DISPOSE_FAILED",
                requireTargetClosedClrIdentity: true))
        {
            reason = "P47_DISPOSE_FIXED_GUARDED_DIAGNOSTIC_UNPROVEN";
            return false;
        }
        closeCatch = close.Catches[0];
        disposeCatch = dispose.Catches[0];
        reason = "";
        return true;
    }

    private static bool ExactOwnedDispose(SemanticModel model,
        StatementSyntax statement, IFieldSymbol playwright)
    {
        if (statement is not ExpressionStatementSyntax
            { Expression: InvocationExpressionSyntax call }
            || call.ArgumentList.Arguments.Count != 0
            || call.Expression is not MemberAccessExpressionSyntax member
            || model.GetSymbolInfo(call).Symbol is not IMethodSymbol method
            || method.Name != "Dispose" || !method.ReturnsVoid
            || method.Parameters.Length != 0 || method.IsStatic
            || method.ContainingType.ToDisplayString()
                is not ("System.IDisposable" or "Microsoft.Playwright.IPlaywright"))
            return false;
        return model.GetSymbolInfo(member.Expression).Symbol is IFieldSymbol field
            && SymbolEqualityComparer.Default.Equals(field, playwright);
    }
}
