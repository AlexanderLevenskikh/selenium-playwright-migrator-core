using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Migrator.Roslyn;

public static partial class SemanticPomActionProjector
{
    // A helper may forward an unmodified int parameter or a source int literal.
    // Arithmetic, property reads, conversions, invocations and negative unary
    // expressions require independent proof and are intentionally rejected.
    private static bool SafeInt(ExpressionSyntax expression, SemanticModel model,
        IMethodSymbol? owner, out string target)
    {
        target = "";
        if (LiteralInt(expression, out var value))
        {
            target = value.ToString(CultureInfo.InvariantCulture);
            return true;
        }
        if (expression is IdentifierNameSyntax name
            && model.GetSymbolInfo(name).Symbol is IParameterSymbol parameter
            && owner != null
            && SymbolEqualityComparer.Default.Equals(
                parameter.ContainingSymbol, owner)
            && parameter.Type.SpecialType == SpecialType.System_Int32)
        {
            target = parameter.Name;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Only two boolean WebDriverWait.Until lambda shapes are admitted.
    /// Both must read exclusively from the lambda's actual IWebDriver
    /// parameter (not another captured driver). In particular,
    /// !FindElement(...).Displayed cannot map to Playwright Hidden because
    /// Hidden also succeeds when the element is missing.
    /// </summary>
    private static bool TrySourceWaitPredicate(
        SimpleLambdaExpressionSyntax lambda, SemanticModel model,
        out string locator, out string state)
    {
        locator = state = "";
        var parameter = model.GetDeclaredSymbol(lambda.Parameter) as IParameterSymbol;
        if (parameter?.Type.ToDisplayString() != "OpenQA.Selenium.IWebDriver")
            return false;

        bool SourceReceiver(InvocationExpressionSyntax call)
        {
            return call.Expression is MemberAccessExpressionSyntax member
                && member.Expression is IdentifierNameSyntax receiver
                && SymbolEqualityComparer.Default.Equals(
                    model.GetSymbolInfo(receiver).Symbol, parameter);
        }

        if (lambda.Body is MemberAccessExpressionSyntax visible
            && visible.Name.Identifier.ValueText == "Displayed"
            && (model.GetSymbolInfo(visible).Symbol as IPropertySymbol)?
                .ContainingType.ToDisplayString() == "OpenQA.Selenium.IWebElement"
            && visible.Expression is InvocationExpressionSyntax one
            && SourceReceiver(one)
            && TryFindBy(one, model, out locator, expectMany: false))
        {
            state = "Visible";
            return true;
        }

        if (lambda.Body is BinaryExpressionSyntax absent
            && absent.IsKind(SyntaxKind.EqualsExpression)
            && absent.Left is MemberAccessExpressionSyntax countRead
            && countRead.Name.Identifier.ValueText == "Count"
            && (model.GetSymbolInfo(countRead).Symbol as IPropertySymbol)?
                .Type.SpecialType == SpecialType.System_Int32
            && countRead.Expression is InvocationExpressionSyntax many
            && SourceReceiver(many)
            && TryFindBy(many, model, out locator, expectMany: true)
            && LiteralInt(absent.Right, out var zero) && zero == 0)
        {
            state = "Detached";
            return true;
        }
        return false;
    }
}
