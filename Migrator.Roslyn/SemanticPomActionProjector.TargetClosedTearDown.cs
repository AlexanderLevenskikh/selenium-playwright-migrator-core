using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Migrator.Roslyn;

public static partial class SemanticPomActionProjector
{
    // Playwright 1.63's TargetClosedException is NOT public (CS0122).
    // The only admitted external contract is the real, accessible
    // PlaywrightException with a strict CLR identity + assembly filter:
    //
    // catch (Microsoft.Playwright.PlaywrightException e) when (
    //     e.GetType().FullName == "Microsoft.Playwright.TargetClosedException" &&
    //     e.GetType().Assembly == typeof(Microsoft.Playwright.IPage).Assembly)
    //
    // The exact observed target-closed subtype is handled, without
    // matching other PlaywrightException subclasses or any exception text.
    private static bool TryVerifyTargetClosedClrTearDown(
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
                requireTargetClosedClrIdentity: true) ||
            !SafeFixedCleanupDiagnostic(model, inner.Catches[0],
                "MIGRATOR_CLEANUP_CONTEXT_CLOSE_FAILED",
                requireTargetClosedClrIdentity: true))
            return false;
        pageCatch = outer.Catches[0];
        contextCatch = inner.Catches[0];
        return true;
    }

    // Source-backed proof of the CLR exception identity predicate, not
    // structural ToString matching. Check the exact exception variable,
    // real System.Object.GetType, System.Type.FullName/Assembly properties,
    // constant full name and the assembly of the real IPage interface.
    private static bool ExactRealTargetClosedFilter(
        SemanticModel model, CatchClauseSyntax clause)
    {
        var dec=clause.Declaration;
        if (dec == null || dec.Identifier.ValueText.Length == 0
            || model.GetDeclaredSymbol(dec) is not ILocalSymbol caught
            || model.GetTypeInfo(dec.Type).Type is not INamedTypeSymbol catchType
            || catchType.ToDisplayString() !=
                "Microsoft.Playwright.PlaywrightException"
            || catchType.ContainingAssembly?.Name != "Microsoft.Playwright"
            || catchType.DeclaredAccessibility != Accessibility.Public
            || clause.Filter?.FilterExpression
                is not BinaryExpressionSyntax logical
            || !logical.IsKind(SyntaxKind.LogicalAndExpression)
            || logical.Left is not BinaryExpressionSyntax byName
            || !byName.IsKind(SyntaxKind.EqualsExpression)
            || logical.Right is not BinaryExpressionSyntax byAssembly
            || !byAssembly.IsKind(SyntaxKind.EqualsExpression)
            || !IsExactCatchTypeProperty(model, byName.Left, caught,
                "FullName")
            || byName.Right is not LiteralExpressionSyntax literal
            || !literal.IsKind(SyntaxKind.StringLiteralExpression)
            || literal.Token.ValueText !=
                "Microsoft.Playwright.TargetClosedException"
            || !IsExactCatchTypeProperty(model, byAssembly.Left, caught,
                "Assembly")
            || byAssembly.Right is not MemberAccessExpressionSyntax right
            || right.Expression is not TypeOfExpressionSyntax typeOf
            || model.GetSymbolInfo(right).Symbol is not IPropertySymbol property
            || property.Name != "Assembly"
            || property.ContainingType.ToDisplayString() != "System.Type"
            || model.GetTypeInfo(typeOf.Type).Type is not INamedTypeSymbol iPage
            || iPage.ToDisplayString() != "Microsoft.Playwright.IPage"
            || iPage.ContainingAssembly?.Name != "Microsoft.Playwright")
            return false;
        return true;
    }

    private static bool IsExactCatchTypeProperty(SemanticModel model,
        ExpressionSyntax expression, ILocalSymbol caught, string propertyName)
    {
        if (expression is not MemberAccessExpressionSyntax access
            || access.Name.Identifier.ValueText != propertyName
            || model.GetSymbolInfo(access).Symbol is not IPropertySymbol property
            || property.Name != propertyName
            || property.ContainingType.ToDisplayString() != "System.Type"
            || access.Expression is not InvocationExpressionSyntax call
            || call.ArgumentList.Arguments.Count != 0
            || call.Expression is not MemberAccessExpressionSyntax methodAccess
            || methodAccess.Name.Identifier.ValueText != "GetType"
            || model.GetSymbolInfo(call).Symbol is not IMethodSymbol method
            || method.Name != "GetType"
            || method.Parameters.Length != 0
            || method.ContainingType.SpecialType != SpecialType.System_Object)
            return false;
        return SymbolEqualityComparer.Default.Equals(
            model.GetSymbolInfo(methodAccess.Expression).Symbol, caught);
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
