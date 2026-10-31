using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Migrator.Roslyn;

public static partial class SemanticPomActionProjector
{
    private enum SourceCatchKind
    {
        NoSuchElementFromImmediateText,
        WebDriverWaitTimeout
    }

    private sealed record SourceCatchShape(
        InvocationExpressionSyntax ProtectedCall,
        CatchClauseSyntax Clause,
        SourceCatchKind Kind,
        ThrowStatementSyntax? TerminalRethrow,
        InvocationExpressionSyntax? RecoveryAssertion);

    /// <summary>
    /// Accept precisely one source-bound throwing operation and one narrowly
    /// typed catch. We must not use broad PlaywrightException catch mappings:
    /// Playwright click auto-wait, navigation, page-close, etc. are not
    /// Selenium NoSuchElementException. Catch filters, use of the exception
    /// object, finally, explicit throw-new and additional effects fail closed.
    /// </summary>
    private static bool TryRecognizeSourceCatch(
        TryStatementSyntax source, SemanticModel model,
        out SourceCatchShape? catchShape)
    {
        catchShape = null;
        if (source.Finally != null || source.Catches.Count != 1
            || source.Block.Statements.Count != 1
            || source.Block.Statements[0] is not ExpressionStatementSyntax
                { Expression: InvocationExpressionSyntax protectedCall })
            return false;

        var clause = source.Catches[0];
        if (clause.Filter != null || clause.Declaration == null
            || clause.Block.Statements.Count != 1)
            return false;

        if (clause.Declaration.Identifier.ValueText.Length != 0)
        {
            // A source catch variable may be declared but must not be used:
            // its .Message, .StackTrace, identity or rethrow-by-name would
            // change when the target uses a different exception type.
            var variable = model.GetDeclaredSymbol(clause.Declaration);
            if (variable == null
                || clause.Block.DescendantNodes().OfType<IdentifierNameSyntax>()
                    .Any(id => SymbolEqualityComparer.Default.Equals(
                        model.GetSymbolInfo(id).Symbol, variable)))
                return false;
        }

        var caughtType = model.GetTypeInfo(clause.Declaration.Type)
            .Type?.ToDisplayString();
        SourceCatchKind kind;
        if (caughtType == "OpenQA.Selenium.NoSuchElementException")
        {
            if (!IsDirectSeleniumFindElementTextAssertion(protectedCall, model))
                return false;
            kind = SourceCatchKind.NoSuchElementFromImmediateText;
        }
        else if (caughtType == "OpenQA.Selenium.WebDriverTimeoutException")
        {
            if (!IsDirectSeleniumWebDriverWait(protectedCall, model))
                return false;
            kind = SourceCatchKind.WebDriverWaitTimeout;
        }
        else
            return false;

        var only = clause.Block.Statements[0];
        if (only is ThrowStatementSyntax { Expression: null } rethrow)
        {
            catchShape = new SourceCatchShape(protectedCall, clause, kind,
                rethrow, null);
            return true;
        }

        if (only is ExpressionStatementSyntax
                { Expression: InvocationExpressionSyntax recover }
            && IsImmediateSeleniumCountNUnitAssertion(recover, model))
        {
            catchShape = new SourceCatchShape(protectedCall, clause, kind,
                null, recover);
            return true;
        }
        return false;
    }

    private static bool IsDirectSeleniumFindElementTextAssertion(
        InvocationExpressionSyntax call, SemanticModel model)
    {
        if (model.GetSymbolInfo(call).Symbol is not IMethodSymbol asserted
            || asserted.Name != "That"
            || asserted.ContainingType.ToDisplayString() != "NUnit.Framework.Assert"
            || call.ArgumentList.Arguments.Count != 2
            || call.ArgumentList.Arguments[0].Expression
                is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Text" } text
            || (model.GetSymbolInfo(text).Symbol as IPropertySymbol)?
                .ContainingType.ToDisplayString() != "OpenQA.Selenium.IWebElement"
            || text.Expression is not InvocationExpressionSyntax find
            || !IsSeleniumCall(find, model, "FindElement", "OpenQA.Selenium.IWebDriver"))
            return false;

        // A typed direct FindElement, not FindElements[0], a cached element,
        // an implicit wait wrapper or a private helper. The projector still
        // checks the exact source By selector and NUnit EqualTo constraint.
        return true;
    }

    private static bool IsDirectSeleniumWebDriverWait(
        InvocationExpressionSyntax call, SemanticModel model) =>
        model.GetSymbolInfo(call).Symbol is IMethodSymbol wait
        && wait.Name == "Until"
        && wait.ContainingType.ToDisplayString() ==
            "OpenQA.Selenium.Support.UI.WebDriverWait"
        && call.Expression is MemberAccessExpressionSyntax
            { Expression: ObjectCreationExpressionSyntax };

    private static bool IsImmediateSeleniumCountNUnitAssertion(
        InvocationExpressionSyntax call, SemanticModel model)
    {
        if (model.GetSymbolInfo(call).Symbol is not IMethodSymbol asserted
            || asserted.Name != "That"
            || asserted.ContainingType.ToDisplayString() != "NUnit.Framework.Assert"
            || call.ArgumentList.Arguments.Count != 2
            || call.ArgumentList.Arguments[0].Expression
                is not MemberAccessExpressionSyntax
                { Name.Identifier.ValueText: "Count",
                  Expression: InvocationExpressionSyntax findMany }
            || !IsSeleniumCall(findMany, model, "FindElements",
                "OpenQA.Selenium.IWebDriver"))
            return false;

        return true;
    }

    private static string RenderSourceCatchHeader(SourceCatchKind kind) =>
        kind switch
        {
            SourceCatchKind.NoSuchElementFromImmediateText =>
                "catch (Microsoft.Playwright.PlaywrightException sourceMissing) " +
                "when (sourceMissing.Message.Contains(" +
                "\"POM_SOURCE_ELEMENT_ABSENT\", System.StringComparison.Ordinal))",
            SourceCatchKind.WebDriverWaitTimeout =>
                "catch (System.TimeoutException)",
            _ => throw new InvalidOperationException("Unknown source catch kind")
        };
}
