using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Migrator.Roslyn;

public static partial class SemanticPomActionProjector
{
    public sealed record ExternalFixtureLifecycleEvidence(
        string FixtureSourcePath, string FixtureSourceSha256,
        string PageConstructorSha256,
        string OneTimeSetUpSha256, string SetUpSha256,
        string TearDownSha256, string OneTimeTearDownSha256,
        string PageField, string ContextField, string BrowserField,
        string PlaywrightField, string FixtureType,
        bool ContextFromBrowser, bool PageFromContext,
        bool PageAndContextConstructorInjected,
        bool PerTestCleanupInFinally, bool OneTimeCleanupInFinally);

    // No runtime code or lifecycle methods are synthesized. This accepts
    // only explicitly captured source with exact Roslyn symbol identities.
    // In particular NUnit fixture construction and BrowserContext/Page
    // identity must be present in the SAME fixture's own source members.
    private static bool TryVerifyFixtureLifecycle(
        CSharpCompilation compilation, IMethodSymbol pageMethod,
        IMethodSymbol forwardingMethod, IMethodSymbol nunitMethod,
        string pageName, string contextName, string browserName,
        string playwrightName,
        out ExternalFixtureLifecycleEvidence? evidence, out string reason)
    {
        evidence = null;
        reason = "Lifecycle shape, fixture ownership or typed constructor flow is not proven.";
        var fixture = nunitMethod.ContainingType;
        var root = pageMethod.ContainingType;
        var testBase = forwardingMethod.ContainingType;
        if (fixture.TypeKind != TypeKind.Class || !fixture.IsSealed
            || !SymbolEqualityComparer.Default.Equals(fixture.BaseType, testBase)
            || !HasAttribute(fixture, "NUnit.Framework.TestFixtureAttribute")
            || !HasAttribute(nunitMethod, "NUnit.Framework.TestAttribute")
            || fixture.InstanceConstructors.Count(c => !c.IsImplicitlyDeclared) > 0
            || fixture.InstanceConstructors.All(c => c.Parameters.Length != 0)
            || testBase.GetMembers().OfType<IMethodSymbol>()
                .Any(m => IsLifecycleMethod(m))
            || fixture.GetMembers().OfType<IMethodSymbol>()
                .Any(m => m.IsOverride || m.IsVirtual))
        {
            reason = "Require one sealed ordinary zero-argument NUnit fixture inheriting the exact TestBase; no custom fixture constructor, inherited hooks or polymorphic methods.";
            return false;
        }

        static bool RealPlaywright(ITypeSymbol? type, string name) =>
            type?.ToDisplayString() == "Microsoft.Playwright." + name
            && type.ContainingAssembly?.Name == "Microsoft.Playwright";
        static IFieldSymbol? Field(INamedTypeSymbol type, string name)
        {
            var fields = type.GetMembers(name).OfType<IFieldSymbol>().ToArray();
            return fields.Length == 1 && !fields[0].IsStatic
                ? fields[0] : null;
        }
        static bool Same(ISymbol? a, ISymbol? b) =>
            a != null && b != null
            && SymbolEqualityComparer.Default.Equals(a, b);
        static ISymbol? Symbol(SemanticModel model, ExpressionSyntax expr)
        {
            while (expr is PostfixUnaryExpressionSyntax
                   { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } postfix)
                expr = postfix.Operand;
            while (expr is ParenthesizedExpressionSyntax parenthesized)
                expr = parenthesized.Expression;
            return model.GetSymbolInfo(expr).Symbol;
        }
        static bool Attr(IMethodSymbol method, string attribute) =>
            HasAttribute(method, attribute);
        static bool AsyncLifecycle(IMethodSymbol method) =>
            method.Parameters.Length == 0 && method.IsAsync
            && method.ReturnType.ToDisplayString() == "System.Threading.Tasks.Task"
            && !method.IsGenericMethod && !method.IsVirtual && !method.IsOverride
            && !method.IsAbstract;
        static MethodDeclarationSyntax? Declaration(IMethodSymbol method) =>
            method.DeclaringSyntaxReferences.Length == 1
                ? method.DeclaringSyntaxReferences[0].GetSyntax()
                    as MethodDeclarationSyntax : null;
        static bool IsLifecycleMethod(IMethodSymbol method) =>
            new[] { "NUnit.Framework.OneTimeSetUpAttribute",
                "NUnit.Framework.SetUpAttribute",
                "NUnit.Framework.TearDownAttribute",
                "NUnit.Framework.OneTimeTearDownAttribute" }
                .Any(name => Attr(method, name));

        var page = Field(fixture, pageName);
        var context = Field(fixture, contextName);
        var browser = Field(fixture, browserName);
        var playwright = Field(fixture, playwrightName);
        var driver = fixture.GetMembers().OfType<IFieldSymbol>()
            .Where(f => !f.IsStatic &&
                f.Type.ToDisplayString() == "OpenQA.Selenium.IWebDriver")
            .ToArray();
        var sourcePageField = Field(testBase, "page");
        if (page == null || context == null || browser == null
            || playwright == null || driver.Length != 1
            || sourcePageField == null || sourcePageField.IsReadOnly
            || !Same(sourcePageField.Type, root)
            || !RealPlaywright(page.Type, "IPage")
            || !RealPlaywright(context.Type, "IBrowserContext")
            || !RealPlaywright(browser.Type, "IBrowser")
            || !RealPlaywright(playwright.Type, "IPlaywright")
            || page.IsReadOnly || context.IsReadOnly || browser.IsReadOnly
            || playwright.IsReadOnly)
        {
            reason = "Fixture must declare explicitly named mutable real Playwright IPage/IBrowserContext/IBrowser/IPlaywright fields and one IWebDriver field; TestBase must own the source Page Object slot.";
            return false;
        }

        var attrs = new[] {
            "NUnit.Framework.OneTimeSetUpAttribute",
            "NUnit.Framework.SetUpAttribute",
            "NUnit.Framework.TearDownAttribute",
            "NUnit.Framework.OneTimeTearDownAttribute" };
        var hooks = new MethodDeclarationSyntax[4];
        for (var i = 0; i < attrs.Length; i++)
        {
            var matches = fixture.GetMembers().OfType<IMethodSymbol>()
                .Where(m => Attr(m, attrs[i])).ToArray();
            if (matches.Length != 1 || !AsyncLifecycle(matches[0])
                || Declaration(matches[0]) is not { Body: not null } syntax)
            {
                reason = "Require exactly one source-backed async Task NUnit OneTimeSetUp, SetUp, TearDown and OneTimeTearDown with no parameters.";
                return false;
            }
            hooks[i] = syntax;
        }
        var oneSet = hooks[0];
        var setup = hooks[1];
        var teardown = hooks[2];
        var oneDown = hooks[3];
        var oneModel = compilation.GetSemanticModel(oneSet.SyntaxTree, true);
        var setupModel = compilation.GetSemanticModel(setup.SyntaxTree, true);
        var downModel = compilation.GetSemanticModel(teardown.SyntaxTree, true);
        var oneDownModel = compilation.GetSemanticModel(oneDown.SyntaxTree, true);

        static bool AssignAwait(SemanticModel model, StatementSyntax statement,
            IFieldSymbol field, string calledName, string containingType,
            ISymbol? receiver, out InvocationExpressionSyntax? invocation)
        {
            invocation = null;
            if (statement is not ExpressionStatementSyntax
                { Expression: AssignmentExpressionSyntax assignment }
                || !assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                || !Same(Symbol(model, assignment.Left), field)
                || assignment.Right is not AwaitExpressionSyntax
                    { Expression: InvocationExpressionSyntax call }
                || model.GetSymbolInfo(call).Symbol is not IMethodSymbol method
                || method.Name != calledName
                || method.ContainingType.ToDisplayString() != containingType)
                return false;
            if (receiver != null)
            {
                if (call.Expression is not MemberAccessExpressionSyntax member
                    || !Same(Symbol(model, member.Expression), receiver))
                    return false;
            }
            invocation = call;
            return true;
        }
        static bool AwaitClose(SemanticModel model, StatementSyntax statement,
            IFieldSymbol field, string type)
        {
            if (statement is not ExpressionStatementSyntax
                { Expression: AwaitExpressionSyntax
                    { Expression: InvocationExpressionSyntax call } }
                || model.GetSymbolInfo(call).Symbol is not IMethodSymbol m
                || m.Name != "CloseAsync" ||
                    m.ContainingType.ToDisplayString() != "Microsoft.Playwright." + type
                || call.ArgumentList.Arguments.Count != 0
                || call.Expression is not MemberAccessExpressionSyntax member)
                return false;
            return Same(Symbol(model, member.Expression), field);
        }
        static bool CallDispose(SemanticModel model, StatementSyntax statement,
            IFieldSymbol field)
        {
            if (statement is not ExpressionStatementSyntax
                { Expression: InvocationExpressionSyntax call }
                || model.GetSymbolInfo(call).Symbol is not IMethodSymbol m
                || m.Name != "Dispose" || call.ArgumentList.Arguments.Count != 0
                || call.Expression is not MemberAccessExpressionSyntax member)
                return false;
            return Same(Symbol(model, member.Expression), field);
        }

        if (oneSet.Body!.Statements.Count != 2
            || !AssignAwait(oneModel, oneSet.Body.Statements[0],
                playwright, "CreateAsync", "Microsoft.Playwright.Playwright",
                null, out var create)
            || create?.ArgumentList.Arguments.Count != 0
            || !AssignAwait(oneModel, oneSet.Body.Statements[1],
                browser, "LaunchAsync", "Microsoft.Playwright.IBrowserType",
                null, out var launch)
            || launch?.Expression is not MemberAccessExpressionSyntax
                { Expression: MemberAccessExpressionSyntax chromium }
            || !Same(Symbol(oneModel, chromium.Expression), playwright)
            || oneModel.GetSymbolInfo(chromium).Symbol is not IPropertySymbol chromeProperty
            || chromeProperty.Name != "Chromium"
            || chromeProperty.ContainingType.ToDisplayString()
                != "Microsoft.Playwright.IPlaywright"
            || launch.ArgumentList.Arguments.Count != 1
            || launch.ArgumentList.Arguments[0].Expression is not
                ObjectCreationExpressionSyntax
                { Initializer: not null } options
            || oneModel.GetTypeInfo(options).Type?.ToDisplayString()
                != "Microsoft.Playwright.BrowserTypeLaunchOptions")
        {
            reason = "OneTimeSetUp must explicitly create Playwright, then launch its Chromium browser from the same typed field and source-supplied options; no inferred launch arguments.";
            return false;
        }

        // The one-time browser and Playwright must be cleaned in a finally
        // even if CloseAsync itself fails. This is about test outcome paths,
        // not claims concerning a partially failed OneTimeSetUp.
        if (oneDown.Body!.Statements.Count != 1
            || oneDown.Body.Statements[0] is not TryStatementSyntax oneTry
            || oneTry.Catches.Count != 0
            || oneTry.Block.Statements.Count != 1
            || oneTry.Finally?.Block.Statements.Count != 1
            || !AwaitClose(oneDownModel, oneTry.Block.Statements[0],
                browser, "IBrowser")
            || !CallDispose(oneDownModel,
                oneTry.Finally.Block.Statements[0], playwright))
        {
            reason = "OneTimeTearDown must await browser.CloseAsync with source Playwright.Dispose in a finally, with no swallowed exception.";
            return false;
        }

        // SetUp must bind exactly the context returned by this browser, then
        // bind the page returned by that context, then construct the source
        // Page Object with those SAME two fields and the source driver.
        if (setup.Body!.Statements.Count != 3
            || !AssignAwait(setupModel, setup.Body.Statements[0],
                context, "NewContextAsync", "Microsoft.Playwright.IBrowser",
                browser, out var contextCall)
            || contextCall?.ArgumentList.Arguments.Count != 0
            || !AssignAwait(setupModel, setup.Body.Statements[1],
                page, "NewPageAsync", "Microsoft.Playwright.IBrowserContext",
                context, out var pageCall)
            || pageCall?.ArgumentList.Arguments.Count != 0
            || setup.Body.Statements[2] is not ExpressionStatementSyntax
                { Expression: AssignmentExpressionSyntax init }
            || !init.IsKind(SyntaxKind.SimpleAssignmentExpression)
            || !Same(Symbol(setupModel, init.Left), sourcePageField)
            || init.Right is not ObjectCreationExpressionSyntax creation
            || setupModel.GetSymbolInfo(creation).Symbol is not IMethodSymbol constructor
            || !Same(constructor.ContainingType, root)
            || creation.ArgumentList?.Arguments.Count != 3
            || !Same(Symbol(setupModel, creation.ArgumentList.Arguments[0].Expression),
                driver[0])
            || !Same(Symbol(setupModel, creation.ArgumentList.Arguments[1].Expression),
                page)
            || !Same(Symbol(setupModel, creation.ArgumentList.Arguments[2].Expression),
                context))
        {
            reason = "SetUp must assign Browser.NewContextAsync -> Context, Context.NewPageAsync -> Page, then new SourcePage(driver, the same Page, the same Context) to the exact TestBase field; no alternate receiver or effects.";
            return false;
        }

        // Source Page Object must itself retain the injected page and context
        // through a single exact constructor with three same-symbol assignments.
        var ctors = root.InstanceConstructors
            .Where(c => !c.IsImplicitlyDeclared).ToArray();
        var rootPage = Field(root, pageName);
        var rootContext = Field(root, contextName);
        var rootDriver = root.GetMembers().OfType<IFieldSymbol>()
            .Where(f => !f.IsStatic &&
                f.Type.ToDisplayString() == "OpenQA.Selenium.IWebDriver")
            .ToArray();
        if (ctors.Length != 1 || !Same(ctors[0], constructor)
            || ctors[0].Parameters.Length != 3
            || rootPage == null || rootContext == null
            || rootDriver.Length != 1
            || !rootPage.IsReadOnly || !rootContext.IsReadOnly
            || !RealPlaywright(rootPage.Type, "IPage")
            || !RealPlaywright(rootContext.Type, "IBrowserContext")
            || ctors[0].DeclaringSyntaxReferences.Length != 1
            || ctors[0].DeclaringSyntaxReferences[0].GetSyntax()
                is not ConstructorDeclarationSyntax { Body: not null } ctorSyntax
            || ctorSyntax.Body.Statements.Count != 3)
        {
            reason = "Source Page Object must have one typed (IWebDriver, real IPage, real IBrowserContext) constructor assigning readonly IPage/IBrowserContext fields.";
            return false;
        }
        var ctorModel = compilation.GetSemanticModel(ctorSyntax.SyntaxTree, true);
        var expectedFields = new[] { rootDriver[0], rootPage, rootContext };
        for (var i = 0; i < 3; i++)
        {
            if (ctorSyntax.Body.Statements[i] is not ExpressionStatementSyntax
                { Expression: AssignmentExpressionSyntax a }
                || !a.IsKind(SyntaxKind.SimpleAssignmentExpression)
                || !Same(Symbol(ctorModel, a.Left), expectedFields[i])
                || !Same(Symbol(ctorModel, a.Right), ctors[0].Parameters[i]))
            {
                reason = "Source Page Object constructor must bind each typed argument to its exact matching field; aliases, swaps and additional effects are blocked.";
                return false;
            }
        }

        if (teardown.Body!.Statements.Count != 1
            || teardown.Body.Statements[0] is not TryStatementSyntax downTry
            || downTry.Catches.Count != 0
            || downTry.Block.Statements.Count != 1
            || downTry.Finally?.Block.Statements.Count != 1
            || !AwaitClose(downModel, downTry.Block.Statements[0],
                page, "IPage")
            || !AwaitClose(downModel, downTry.Finally.Block.Statements[0],
                context, "IBrowserContext"))
        {
            reason = "TearDown must await Page.CloseAsync and ALWAYS await Context.CloseAsync from finally, even after a failed test or page close exception.";
            return false;
        }

        // No re-assignment of the resource chain elsewhere in captured code.
        // This is not a whole-program proof against reflection/external
        // assemblies, so the output remains requires_review.
        var expectedWrites = new[] {
            (Field: playwright, Count: 1), (Field: browser, Count: 1),
            (Field: context, Count: 1), (Field: page, Count: 1),
            (Field: sourcePageField, Count: 1),
            (Field: rootPage, Count: 1), (Field: rootContext, Count: 1)
        };
        foreach (var pair in expectedWrites)
        {
            var writes = compilation.SyntaxTrees
                .Where(t => !t.FilePath.StartsWith("__Migrator.", StringComparison.Ordinal))
                .Sum(t =>
                {
                    var model = compilation.GetSemanticModel(t, true);
                    return t.GetRoot().DescendantNodes()
                        .OfType<AssignmentExpressionSyntax>()
                        .Count(a => Same(Symbol(model, a.Left), pair.Field));
                });
            if (writes != pair.Count)
            {
                reason = "A captured source field has extra or missing writers; fixture and injected receiver ownership cannot be proven.";
                return false;
            }
        }

        var fixtureNode = fixture.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax()).OfType<ClassDeclarationSyntax>().FirstOrDefault();
        if (fixtureNode == null || fixture.DeclaringSyntaxReferences.Length != 1)
            return false;
        evidence = new ExternalFixtureLifecycleEvidence(
            fixtureNode.SyntaxTree.FilePath, Hash(fixtureNode),
            Hash(ctorSyntax), Hash(oneSet), Hash(setup), Hash(teardown),
            Hash(oneDown), pageName, contextName, browserName,
            playwrightName, fixture.ToDisplayString(),
            true, true, true, true, true);
        reason = "";
        return true;
    }

    private static bool HasAttribute(ISymbol symbol, string name) =>
        symbol.GetAttributes().Any(a =>
            a.AttributeClass?.ToDisplayString() == name);
}