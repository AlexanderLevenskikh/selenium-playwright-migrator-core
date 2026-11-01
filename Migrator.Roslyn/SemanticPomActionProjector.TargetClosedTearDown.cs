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
        SemanticModel model, CatchClauseSyntax clause,
        out string failure)
    {
        failure = "P46_TYPED_CATCH_UNPROVEN";
        var declaration = clause.Declaration;
        if (declaration is null || declaration.Identifier.ValueText.Length == 0)
        {
            failure = "P46_CATCH_VARIABLE_MISSING";
            return false;
        }
        if (model.GetDeclaredSymbol(declaration) is not ILocalSymbol caught)
        {
            failure = "P46_CATCH_SYMBOL_UNAVAILABLE";
            return false;
        }
        if (model.GetTypeInfo(declaration.Type).Type is not INamedTypeSymbol type
            || type.ToDisplayString() != "Microsoft.Playwright.PlaywrightException"
            || type.ContainingAssembly?.Name != "Microsoft.Playwright"
            || type.DeclaredAccessibility != Accessibility.Public)
        {
            failure = "P46_PUBLIC_PLAYWRIGHT_CATCH_TYPE_UNPROVEN";
            return false;
        }
        if (clause.Filter?.FilterExpression is not BinaryExpressionSyntax logical
            || !logical.IsKind(SyntaxKind.LogicalAndExpression)
            || logical.Left is not BinaryExpressionSyntax byName
            || !byName.IsKind(SyntaxKind.EqualsExpression)
            || logical.Right is not BinaryExpressionSyntax byAssembly
            || !byAssembly.IsKind(SyntaxKind.EqualsExpression))
        {
            failure = "P46_EXACT_AND_FILTER_SHAPE_UNPROVEN";
            return false;
        }
        if (!IsExactCatchTypeProperty(model, byName.Left, caught, "FullName"))
        {
            failure = "P46_REAL_GETTYPE_FULLNAME_SYMBOL_UNPROVEN";
            return false;
        }
        if (byName.Right is not LiteralExpressionSyntax literal
            || !literal.IsKind(SyntaxKind.StringLiteralExpression)
            || literal.Token.ValueText != "Microsoft.Playwright.TargetClosedException")
        {
            failure = "P46_TARGET_CLOSED_CLR_NAME_NOT_EXACT";
            return false;
        }
        if (!IsExactCatchTypeProperty(model, byAssembly.Left, caught, "Assembly"))
        {
            failure = "P46_REAL_GETTYPE_ASSEMBLY_SYMBOL_UNPROVEN";
            return false;
        }
        if (byAssembly.Right is not MemberAccessExpressionSyntax right
            || right.Expression is not TypeOfExpressionSyntax typeOf
            || model.GetSymbolInfo(right).Symbol is not IPropertySymbol property
            || property.Name != "Assembly"
            || property.ContainingType.ToDisplayString() != "System.Type")
        {
            failure = "P46_REFERENCE_ASSEMBLY_PROPERTY_UNPROVEN";
            return false;
        }
        if (model.GetTypeInfo(typeOf.Type).Type is not INamedTypeSymbol page
            || page.ToDisplayString() != "Microsoft.Playwright.IPage"
            || page.ContainingAssembly?.Name != "Microsoft.Playwright")
        {
            failure = "P46_REAL_IPAGE_ASSEMBLY_REFERENCE_UNPROVEN";
            return false;
        }
        failure = "";
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
