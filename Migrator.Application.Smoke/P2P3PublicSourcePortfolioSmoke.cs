using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static class P2P3PublicSourcePortfolioSmoke
{
    // Read only public source files from independently SHA-pinned checkout.
    // This exercises AST observations, NOT target behavior or project execution.
    public static void Run(string baseDirectory)
    {
        var root = Path.GetFullPath(baseDirectory);
        var deshTest = Parse(root, "desh",
            "AssignmentProject/Tests/TextInputTests.cs");
        var deshPage = Parse(root, "desh",
            "AssignmentProject/Pages/TextInputPage.cs");
        var testClass = deshTest.DescendantNodes()
            .OfType<ClassDeclarationSyntax>().Single(c =>
                c.Identifier.ValueText == "TextInputTests");
        Check(HasAttribute(testClass.AttributeLists, "TestFixture")
            && HasMethod(testClass, "VerifyTextInput", "Test")
            && HasMethod(testClass, "Setup", "SetUp")
            && HasMethod(testClass, "TearDown", "TearDown"),
            "real NUnit POM test inventory lost");
        var testCalls = Calls(testClass);
        foreach (var required in new[] {
                     "EnterText", "ClickButton", "GetButtonText", "That",
                     "StartBrowser", "GoToUrl", "CloseBrowser"})
            Check(testCalls.Contains(required),
                "external POM/lifecycle effect missing: " + required);
        var pageClass = deshPage.DescendantNodes()
            .OfType<ClassDeclarationSyntax>().Single(c =>
                c.Identifier.ValueText == "TextInputPage");
        var pageCalls = Calls(pageClass);
        foreach (var required in new[] { "FindElement", "Id", "Clear",
                     "SendKeys", "Click" })
            Check(pageCalls.Contains(required),
                "external PageObject action/selector missing: " + required);
        Check(pageClass.Members.OfType<PropertyDeclarationSyntax>()
            .Count(p => p.Type.ToString() == "IWebElement") == 2,
            "external POM source property selectors need independent binding");

        var selenioTest = Parse(root, "selenio",
            "Selenio/Selenio.NUnit/NUnitTests.cs");
        var selenioPage = Parse(root, "selenio",
            "Selenio/Selenio.NUnit/PageObjects/SeleniumHomePage.cs");
        var inherited = selenioTest.DescendantNodes()
            .OfType<ClassDeclarationSyntax>().Single(c =>
                c.Identifier.ValueText == "NUnitTests");
        Check(HasAttribute(inherited.AttributeLists, "TestFixture")
            && inherited.BaseList?.Types.Any(t =>
                t.Type.ToString() == "TestClass") == true
            && HasMethod(inherited, "SeleniumHqTest", "Test"),
            "inherited external NUnit fixture was not detected");
        var externalCalls = Calls(inherited);
        Check(externalCalls.Contains("Search")
            && externalCalls.Contains("Click")
            && externalCalls.Contains("WaitToLoad")
            && externalCalls.Contains("WaitForScreen"),
            "real external PageFactory graph not visible");
        var pageObject = selenioPage.DescendantNodes()
            .OfType<ClassDeclarationSyntax>().Single(c =>
                c.Identifier.ValueText == "SeleniumHomePage");
        Check(pageObject.BaseList?.Types.Any(t =>
            t.Type.ToString() == "PageObject") == true,
            "external PageObject base not recorded");
        Check(pageObject.DescendantNodes().OfType<AttributeSyntax>()
            .Count(a => a.Name.ToString() == "FindsBy") >= 3,
            "FindsBy proxy selector evidence not observed");

        var lifecycle = Parse(root, "remotion",
            "Remotion/Web/IntegrationTests/SetUpFixture.cs");
        var global = lifecycle.DescendantNodes()
            .OfType<ClassDeclarationSyntax>().Single(c =>
                c.Identifier.ValueText == "SetUpFixture");
        Check(HasAttribute(global.AttributeLists, "SetUpFixture")
            && HasMethod(global, "OneTimeSetUp", "OneTimeSetUp")
            && HasMethod(global, "OneTimeTearDown", "OneTimeTearDown"),
            "actual assembly-scoped NUnit SetUpFixture lifecycle missing");
        var lifecycleCalls = Calls(global);
        foreach (var required in new[] {
                     "CreateFromConfiguration", "Configure",
                     "OnSetUp", "OnTearDown", "Delete" })
            Check(lifecycleCalls.Contains(required),
                "external fixture side effect not inventoried: " + required);
        // Don't claim that matching method names prove source-target parity.
        var summary = new {
            schema = "migrator-p2p3-public-roslyn-boundary/v1",
            publicSamples = 3,
            sourcePOMCallGraph = "partially_visible_unresolved_cross_type",
            inheritedNUnitLifecycle = "source_seen_target_unknown",
            setupFixtureAndSideEffects = "source_seen_target_unknown",
            externalBrowserTests = "not_run",
            targetCandidates = "not_generated",
            behavioralEquivalence = "unknown",
            canAutoApply = false,
            accepted = false
        };
        Console.WriteLine(JsonSerializer.Serialize(summary));
        Console.WriteLine("P2_P3_PUBLIC_REAL_SOURCE_BOUNDARIES_PASSED");
    }

    private static Microsoft.CodeAnalysis.SyntaxNode Parse(
        string root, string repo, string path)
    {
        var full = Path.GetFullPath(Path.Combine(root, repo,
            path.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.GetFullPath(Path.Combine(root, repo))
            + Path.DirectorySeparatorChar;
        Check(full.StartsWith(prefix,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal), "source path escape");
        var bytes = File.ReadAllBytes(full);
        Check(bytes.Length < 1024 * 1024, "external file too large");
        var text = new UTF8Encoding(false, true).GetString(bytes);
        var parsed = CSharpSyntaxTree.ParseText(text, path: path);
        Check(!parsed.GetDiagnostics().Any(d =>
            d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error),
            "external public C# source parser reported an error: " + path);
        return parsed.GetRoot();
    }

    private static bool HasAttribute(
        Microsoft.CodeAnalysis.SyntaxList<AttributeListSyntax> attributes,
        string name) => attributes.SelectMany(x => x.Attributes)
            .Any(x => x.Name.ToString().Split('.').Last() == name);

    private static bool HasMethod(ClassDeclarationSyntax owner,
        string name, string attribute) =>
        owner.Members.OfType<MethodDeclarationSyntax>().Any(m =>
            m.Identifier.ValueText == name
            && HasAttribute(m.AttributeLists, attribute));

    private static HashSet<string> Calls(ClassDeclarationSyntax owner) =>
        owner.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(x => x.Expression switch
            {
                MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
                IdentifierNameSyntax i => i.Identifier.ValueText,
                _ => x.Expression.ToString()
            }).ToHashSet(StringComparer.Ordinal);

    private static void Check(bool ok, string message)
    {
        if (!ok)
            throw new InvalidOperationException(
                "P2P3_PUBLIC_SOURCE_BOUNDARY_SMOKE: " + message);
    }
}
