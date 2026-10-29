using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Migrator.Roslyn;

/// <summary>
/// Narrow, source-only Roslyn projector for Selenium Page Object method bodies.
/// Its output is an unapproved template; it is NEVER automatically installed
/// as an active adapter mapping. One unsupported statement blocks the entire
/// method, so a partial browser action sequence cannot masquerade as complete.
/// </summary>
public static class SemanticPomActionProjector
{
    private const int MaxFiles = 1200;
    private const int MaxSteps = 64;
    private const int MaxSourceChars = 12 * 1024 * 1024;

    public sealed record Problem(string Code, string SourcePath, int SourceLine,
        string SourceSha256, string Explanation);
    public sealed record Step(int Order, string SourcePath, int SourceLine,
        string SourceSha256, string SourceSymbol, string ReceiverType,
        string SourceReturnType, string Effect, string TargetStatement,
        bool RequiresReview);
    public sealed record Projection(string SchemaVersion, string Status,
        string SourceType, string SourceMethod, string SourceMethodSymbol,
        string SourceMethodSha256, string SourcePath, int SourceLine,
        string SuggestedSourcePattern, IReadOnlyList<Step> Steps,
        IReadOnlyList<Problem> Blockers, IReadOnlyList<string> TargetStatements,
        IReadOnlyList<string> Limitations);

    public static Projection Project(
        IReadOnlyList<(string Path, string Source)> sources,
        string sourceType, string sourceMethod, int sourceLine = 0,
        string receiver = "page", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0 || sources.Count > MaxFiles
            || sources.Sum(s => (long)s.Source.Length) > MaxSourceChars)
            throw new ArgumentException("POM_PROJECTION_SOURCE_LIMIT: choose a narrower source project.");
        if (string.IsNullOrWhiteSpace(sourceType) || string.IsNullOrWhiteSpace(sourceMethod)
            || !SyntaxFacts.IsValidIdentifier(sourceMethod)
            || !Regex.IsMatch(receiver, @"^@?[a-zA-Z_]\w*$"))
            throw new ArgumentException("POM_PROJECTION_SELECTOR_INVALID: use exact type/method and one receiver identifier.");
        var syntaxTrees = sources.OrderBy(s => s.Path, StringComparer.Ordinal)
            .Select(s => CSharpSyntaxTree.ParseText(s.Source,
                new CSharpParseOptions(LanguageVersion.Preview), path: s.Path)).ToArray();
        var invalid = syntaxTrees.FirstOrDefault(t => t.GetDiagnostics()
            .Any(d => d.Severity == DiagnosticSeverity.Error));
        if (invalid != null)
            throw new InvalidOperationException(
                "POM_PROJECTION_PARSE_ERROR: " + invalid.FilePath);

        var compilation = SemanticCompilationSupport.CreateCompilation(syntaxTrees);
        var matching = syntaxTrees.SelectMany(t => t.GetRoot().DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .SelectMany(c => c.Members.OfType<MethodDeclarationSyntax>()
                    .Where(m => m.Identifier.ValueText == sourceMethod
                        && (sourceLine == 0 || Line(m) == sourceLine))
                    .Select(m => (Tree: t, Class: c, Method: m))))
            .Where(x =>
            {
                var model = compilation.GetSemanticModel(x.Tree, true);
                var type = model.GetDeclaredSymbol(x.Class) as INamedTypeSymbol;
                return type != null && (type.ToDisplayString() == sourceType
                    || type.Name == sourceType);
            }).ToArray();

        var empty = Array.Empty<Step>();
        var problems = new List<Problem>();
        var details = new List<Step>();
        var noMethod = matching.Length != 1;
        if (noMethod)
        {
            return new Projection("migrator-pom-action/v1alpha1", "blocked",
                sourceType, sourceMethod, "", "", "", 0, "", empty,
                new[] { new Problem(matching.Length == 0
                    ? "POM_METHOD_NOT_RESOLVED" : "POM_METHOD_AMBIGUOUS",
                    "", sourceLine, "", "Select one project-owned method by exact type, name and declaration line.") },
                Array.Empty<string>(),
                new[] { "No source C# was executed. Unresolved or overloaded methods never receive target actions." });
        }

        var selected = matching[0];
        var method = selected.Method;
        var semantic = compilation.GetSemanticModel(selected.Tree, true);
        var symbol = semantic.GetDeclaredSymbol(method) as IMethodSymbol;
        var origin = selected.Tree.FilePath;
        var methodSha = Hash(method);
        var methodSymbol = symbol == null ? string.Empty
            : symbol.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                + "." + symbol.Name + "("
                + string.Join(", ", symbol.Parameters.Select(parameter =>
                    parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)))
                + ")";
        var parameters = method.ParameterList.Parameters.Select(p => p.Identifier.ValueText).ToArray();
        var pattern = receiver + "." + sourceMethod + "("
            + string.Join(", ", parameters.Select(p => "{" + p + "}")) + ")";
        void Block(string code, SyntaxNode at, string explanation) =>
            problems.Add(new Problem(code, origin, Line(at), Hash(at), explanation));

        if (symbol == null || symbol.ReturnsVoid != true
            || symbol.IsAsync || symbol.IsGenericMethod
            || method.Modifiers.Any(SyntaxKind.AbstractKeyword)
            || method.Modifiers.Any(SyntaxKind.UnsafeKeyword)
            || method.ParameterList.Parameters.Any(p =>
                p.Modifiers.Any() || p.Default != null)
            || symbol.Parameters.Any(p => p.Type.SpecialType is not
                (SpecialType.System_String or SpecialType.System_Int32)))
            Block("POM_SIGNATURE_REQUIRES_REVIEW", method,
                "Only synchronous, non-generic void methods with simple string/int inputs have projected browser statements.");

        var expressions = method.Body != null
            ? method.Body.Statements.Select(s => s is ExpressionStatementSyntax expression
                ? expression.Expression : null).ToArray()
            : method.ExpressionBody != null
                ? new ExpressionSyntax?[] { method.ExpressionBody.Expression }
                : Array.Empty<ExpressionSyntax?>();
        if (expressions.Length is 0 or > MaxSteps)
            Block("POM_BODY_REQUIRES_REVIEW", method,
                "An empty or oversized POM method is not safely portable.");

        for (var i = 0; i < expressions.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expression = expressions[i];
            if (expression == null
                || expression is not InvocationExpressionSyntax invocation)
            {
                Block("POM_UNSUPPORTED_STATEMENT", (SyntaxNode?)expression ?? method,
                    "Control flow, local mutations, returns, branching and non-invocation statements require explicit side-effect review.");
                continue;
            }
            if (!TryTranslate(invocation, semantic, symbol, out var effect,
                out var target, out var receiverType, out var returnType,
                out var callSymbol, out var explanation))
            {
                Block("POM_ACTION_UNRESOLVED", invocation, explanation);
                continue;
            }
            details.Add(new Step(i + 1, origin, Line(invocation), Hash(invocation),
                callSymbol, receiverType, returnType, effect, target, true));
        }

        return new Projection("migrator-pom-action/v1alpha1",
            problems.Count == 0 && details.Count == expressions.Length
                ? "requires_review" : "blocked",
            sourceType, sourceMethod, methodSymbol, methodSha, origin, Line(method),
            pattern, details, problems,
            problems.Count == 0 ? details.Select(x => x.TargetStatement).ToArray()
                : Array.Empty<string>(),
            new[]
            {
                "This is a deterministic Roslyn source-to-target template, not behavioral verification.",
                "A single unknown statement prevents emission of any active target sequence.",
                "Typed Selenium/NUnit calls are source facts; simulated compile anchors are not actual Selenium restore evidence.",
                "Playwright actionability, wait timeout/polling, input key behavior, selector scope and DOM state need runtime review.",
                "The caller must explicitly attach reviewed mappings; this projector never edits tests/config or runs user C#."
            });
    }

    private static bool TryTranslate(InvocationExpressionSyntax invocation,
        SemanticModel model, IMethodSymbol? enclosingMethod,
        out string effect, out string target, out string receiverType,
        out string returnType, out string callSymbol, out string explanation)
    {
        effect = target = receiverType = returnType = callSymbol = string.Empty;
        explanation = "The method call or its Selenium/NUnit identity is unresolved; do not infer a locator, state or side effect.";
        var called = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        if (called == null) return false;
        callSymbol = called.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            + "." + called.Name + "("
            + string.Join(", ", called.Parameters.Select(p =>
                p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)))
            + ")";
        receiverType = called.ContainingType.ToDisplayString();
        returnType = called.ReturnType.ToDisplayString();
        var member = invocation.Expression as MemberAccessExpressionSyntax;
        if (member == null) return false;

        if (called.Name is "Click" or "Clear" or "SendKeys"
            && receiverType == "OpenQA.Selenium.IWebElement")
        {
            if (!TryElementLocator(member.Expression, model, out var locator))
            {
                explanation = "Element action has no symbol-bound IWebDriver.FindElement/FindElements with a literal Selenium By selector.";
                return false;
            }
            if (called.Name == "SendKeys")
            {
                if (invocation.ArgumentList.Arguments.Count != 1
                    || !SafeString(invocation.ArgumentList.Arguments[0].Expression,
                        model, enclosingMethod, out var value))
                {
                    explanation = "SendKeys argument is dynamic or not a source method string parameter.";
                    return false;
                }
                target = "await " + locator + ".PressSequentiallyAsync(" + value + ");";
                effect = "input_sendkeys";
                return true;
            }
            if (invocation.ArgumentList.Arguments.Count != 0) return false;
            effect = called.Name == "Clear" ? "input_clear" : "click";
            target = "await " + locator + (called.Name == "Clear"
                ? ".ClearAsync();" : ".ClickAsync();");
            return true;
        }

        if (called.Name == "GoToUrl" && receiverType == "OpenQA.Selenium.INavigation")
        {
            if (member.Expression is not InvocationExpressionSyntax navigate
                || !IsSeleniumCall(navigate, model, "Navigate", "OpenQA.Selenium.IWebDriver")
                || invocation.ArgumentList.Arguments.Count != 1
                || !SafeString(invocation.ArgumentList.Arguments[0].Expression,
                    model, enclosingMethod, out var url))
            {
                explanation = "Navigation must use IWebDriver.Navigate().GoToUrl with a literal URL or an unchanged string parameter.";
                return false;
            }
            effect = "navigation";
            target = "await Page.GotoAsync(" + url + ");";
            return true;
        }

        if (called.Name == "Until"
            && (receiverType == "OpenQA.Selenium.Support.UI.WebDriverWait"
                || receiverType.StartsWith("OpenQA.Selenium.Support.UI.DefaultWait<", StringComparison.Ordinal)))
        {
            if (member.Expression is not ObjectCreationExpressionSyntax creation
                || !IsWaitConstructor(creation, model, out var timeout)
                || invocation.ArgumentList.Arguments.Count != 1
                || invocation.ArgumentList.Arguments[0].Expression
                    is not SimpleLambdaExpressionSyntax lambda
                || lambda.Body is not MemberAccessExpressionSyntax displayed
                || displayed.Name.Identifier.ValueText != "Displayed"
                || (model.GetSymbolInfo(displayed).Symbol as IPropertySymbol)?
                    .ContainingType.ToDisplayString() != "OpenQA.Selenium.IWebElement"
                || displayed.Expression is not InvocationExpressionSyntax find
                || !TryFindBy(find, model, out var locator, expectMany: false))
            {
                explanation = "Wait must have an explicit finite TimeSpan and simple Until(d => d.FindElement(By.literal).Displayed) lambda.";
                return false;
            }
            effect = "visible_wait";
            target = "await " + locator
                + ".WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Visible, Timeout = "
                + timeout + " });";
            return true;
        }

        if (called.Name == "That" && receiverType == "NUnit.Framework.Assert")
        {
            if (invocation.ArgumentList.Arguments.Count != 2
                || invocation.ArgumentList.Arguments[1].Expression
                    is not InvocationExpressionSyntax equals
                || !IsSeleniumCall(equals, model, "EqualTo", "NUnit.Framework.Is")
                || equals.ArgumentList.Arguments.Count != 1)
            {
                explanation = "Assertion must be NUnit Assert.That(actual, Is.EqualTo(expected)); other constraints require review.";
                return false;
            }
            var actual = invocation.ArgumentList.Arguments[0].Expression;
            if (actual is not MemberAccessExpressionSyntax read)
                return false;

            var expected = equals.ArgumentList.Arguments[0].Expression;
            if (read.Name.Identifier.ValueText == "Text"
                && (model.GetSymbolInfo(read).Symbol as IPropertySymbol)?
                    .ContainingType.ToDisplayString() == "OpenQA.Selenium.IWebElement"
                && TryElementLocator(read.Expression, model, out var textLocator)
                && SafeString(expected, model, enclosingMethod, out var textValue))
            {
                effect = "text_assertion";
                target = "await Microsoft.Playwright.Assertions.Expect(" + textLocator
                    + ").ToHaveTextAsync(" + textValue + ");";
                return true;
            }
            if (read.Name.Identifier.ValueText == "Count"
                && read.Expression is InvocationExpressionSyntax findMany
                && TryFindBy(findMany, model, out var countLocator, expectMany: true)
                && LiteralInt(expected, out var count) && count >= 0)
            {
                effect = "table_count_assertion";
                target = "await Microsoft.Playwright.Assertions.Expect(" + countLocator
                    + ").ToHaveCountAsync(" + count.ToString(CultureInfo.InvariantCulture) + ");";
                return true;
            }
            explanation = "Only typed IWebElement.Text equality and typed FindElements(...).Count literal equality are projected.";
            return false;
        }
        return false;
    }

    private static bool TryElementLocator(ExpressionSyntax expression,
        SemanticModel model, out string locator)
    {
        locator = "";
        if (expression is InvocationExpressionSyntax find
            && TryFindBy(find, model, out locator, expectMany: false))
            return true;
        if (expression is ElementAccessExpressionSyntax access
            && access.Expression is InvocationExpressionSyntax many
            && access.ArgumentList.Arguments.Count == 1
            && LiteralInt(access.ArgumentList.Arguments[0].Expression, out var index)
            && index >= 0
            && TryFindBy(many, model, out var parent, expectMany: true)
            && model.GetTypeInfo(expression).Type?.ToDisplayString()
                == "OpenQA.Selenium.IWebElement")
        {
            locator = parent + ".Nth(" + index.ToString(CultureInfo.InvariantCulture) + ")";
            return true;
        }
        return false;
    }

    private static bool TryFindBy(InvocationExpressionSyntax find,
        SemanticModel model, out string locator, bool expectMany)
    {
        locator = "";
        if (!IsSeleniumCall(find, model, expectMany ? "FindElements" : "FindElement",
                "OpenQA.Selenium.IWebDriver")
            || find.ArgumentList.Arguments.Count != 1)
            return false;
        var argument = find.ArgumentList.Arguments[0].Expression;
        if (argument is not InvocationExpressionSyntax byCall
            || (model.GetSymbolInfo(byCall).Symbol as IMethodSymbol)?
                .ContainingType.ToDisplayString() != "OpenQA.Selenium.By"
            || byCall.Expression is not MemberAccessExpressionSyntax byMember
            || byCall.ArgumentList.Arguments.Count != 1
            || byCall.ArgumentList.Arguments[0].Expression is not LiteralExpressionSyntax literal
            || !literal.IsKind(SyntaxKind.StringLiteralExpression)
            || string.IsNullOrWhiteSpace(literal.Token.ValueText))
            return false;
        var value = literal.Token.ValueText;
        string? selector = byMember.Name.Identifier.ValueText switch
        {
            "CssSelector" => value,
            "XPath" => "xpath=" + value,
            "Id" => "[id=\"" + EscapeCss(value) + "\"]",
            "Name" => "[name=\"" + EscapeCss(value) + "\"]",
            _ => null
        };
        if (selector == null) return false;
        locator = "Page.Locator(" + JsonSerializer.Serialize(selector) + ")";
        return true;
    }

    private static bool IsSeleniumCall(InvocationExpressionSyntax call,
        SemanticModel model, string method, string owner) =>
        model.GetSymbolInfo(call).Symbol is IMethodSymbol symbol
        && symbol.Name == method && symbol.ContainingType.ToDisplayString() == owner;

    private static bool IsWaitConstructor(ObjectCreationExpressionSyntax creation,
        SemanticModel model, out string timeout)
    {
        timeout = "";
        if (model.GetSymbolInfo(creation).Symbol is not IMethodSymbol ctor
            || ctor.ContainingType.ToDisplayString()
                != "OpenQA.Selenium.Support.UI.WebDriverWait"
            || creation.ArgumentList?.Arguments.Count != 2
            || model.GetTypeInfo(creation.ArgumentList.Arguments[0].Expression)
                .Type?.ToDisplayString() != "OpenQA.Selenium.IWebDriver"
            || creation.ArgumentList.Arguments[1].Expression
                is not InvocationExpressionSyntax duration
            || duration.Expression is not MemberAccessExpressionSyntax span
            || (model.GetSymbolInfo(duration).Symbol as IMethodSymbol)?
                .ContainingType.ToDisplayString() != "System.TimeSpan"
            || duration.ArgumentList.Arguments.Count != 1)
            return false;
        if (duration.ArgumentList.Arguments[0].Expression
            is not LiteralExpressionSyntax number
            || !number.IsKind(SyntaxKind.NumericLiteralExpression)
            || !double.TryParse(number.Token.ValueText,
                NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            return false;
        var factor = span.Name.Identifier.ValueText switch
        {
            "FromMilliseconds" => 1.0,
            "FromSeconds" => 1000.0,
            "FromMinutes" => 60000.0,
            _ => 0.0
        };
        var ms = n * factor;
        if (!double.IsFinite(ms) || ms < 1 || ms > 300000) return false;
        timeout = ms.ToString("0.###", CultureInfo.InvariantCulture) + "f";
        return true;
    }

    private static bool SafeString(ExpressionSyntax expression,
        SemanticModel model, IMethodSymbol? method, out string target)
    {
        target = "";
        if (expression is LiteralExpressionSyntax literal
            && literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            target = JsonSerializer.Serialize(literal.Token.ValueText);
            return true;
        }
        if (expression is IdentifierNameSyntax id
            && model.GetSymbolInfo(id).Symbol is IParameterSymbol param
            && method != null && SymbolEqualityComparer.Default.Equals(param.ContainingSymbol, method)
            && param.Type.SpecialType == SpecialType.System_String)
        {
            target = param.Name;
            return true;
        }
        return false;
    }

    private static bool LiteralInt(ExpressionSyntax expression, out int number)
    {
        if (expression is LiteralExpressionSyntax literal
            && literal.IsKind(SyntaxKind.NumericLiteralExpression)
            && literal.Token.Value is int n)
        { number = n; return true; }
        number = 0;
        return false;
    }

    private static string EscapeCss(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\a ", StringComparison.Ordinal)
            .Replace("\r", "\\d ", StringComparison.Ordinal);

    private static int Line(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static string Hash(SyntaxNode node) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(node.ToFullString())))
            .ToLowerInvariant();
}
