using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Migrator.Application;

/// <summary>Source syntax and local declaration witnesses only. NO target C# or runtime claim.</summary>
public sealed record PomFlowSourceWitness(
    string Role, string Sha256, int SourceLine, int SourceOperations);

public sealed record PomFlowSourcePlan(
    string SchemaVersion, string SourceTreeHash, string Status,
    string TargetStatus, IReadOnlyList<PomFlowSourceWitness> Witnesses,
    IReadOnlyList<string> Blockers)
{
    public bool CanAutoApply => false;
    public bool RuntimeVerified => false;
    public string Execution => "read_only_not_run";
    public IReadOnlyList<string> Limitations => new[] {
        "Exact local C# syntax/declaration flow only; external DI container and reflection factories are not resolved.",
        "Source Navigation URL expression and Thread.Sleep are recorded as obligations, never translated or guessed.",
        "ComboBox clicks, input, wait and confirmation are lexical source effects, not a proven Playwright action sequence.",
        "Loader retry/catch/reload and failure path need independent timing, exception and browser validation.",
        "Table ElementAt/Be/Contain are source assertion obligations; DOM, locator uniqueness and assertion semantics are unverified.",
        "No target C# or locator mappings are generated. Selenium source behavior, NUnit lifecycle and browser equivalence remain BLOCKED.",
        "Source-tree SHA ties the witness to exact bytes, not a complete MSBuild semantic compilation."
    };
}

/// <summary>
/// Opt-in preview.50 proof of the narrow fixture -> DI navigation -> typed
/// page -> ComboBox/Loader/table assertion source dependency graph.
/// A nonmatching graph blocks without partial evidence. Does not migrate.
/// </summary>
public sealed class PomFlowSourceInspector
{
    private sealed class Unsupported(string code) : Exception(code)
    {
        public string Code { get; } = code;
    }

    private static void Require(bool ok, string code)
    {
        if (!ok) throw new Unsupported(code);
    }
    private static string Hash(SyntaxNode node) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(node.ToFullString()))).ToLowerInvariant();
    private static int Line(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    private static string Name(TypeSyntax syntax) =>
        syntax is NullableTypeSyntax nullable ? Name(nullable.ElementType)
            : syntax.ToString().Trim();
    private static string NameOfCall(InvocationExpressionSyntax call) =>
        call.Expression switch {
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            IdentifierNameSyntax id => id.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            _ => ""
        };
    private static InvocationExpressionSyntax? Inv(StatementSyntax statement) =>
        (statement as ExpressionStatementSyntax)?.Expression as InvocationExpressionSyntax;
    private static bool Attr(MethodDeclarationSyntax method, string name) =>
        method.AttributeLists.SelectMany(x => x.Attributes).Any(a =>
            a.Name.ToString() is var n && (n == name || n == name + "Attribute"
                || n.EndsWith("." + name, StringComparison.Ordinal)));
    private static IReadOnlyList<InvocationExpressionSyntax> Calls(SyntaxNode node) =>
        node.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().ToArray();

    public PomFlowSourcePlan Inspect(
        string sourceDirectory, string fixtureType, string sourceMethod,
        string expectedSourceTreeHash, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedSourceTreeHash)
            || string.IsNullOrWhiteSpace(fixtureType) || string.IsNullOrWhiteSpace(sourceMethod))
            throw new ArgumentException("P50_SOURCE_TREE_AND_EXACT_METHOD_REQUIRED");
        if (!SyntaxFacts.IsValidIdentifier(fixtureType)
            || !SyntaxFacts.IsValidIdentifier(sourceMethod))
            throw new ArgumentException("P50_EXACT_IDENTIFIER_REQUIRED");
        var inspector = new PomSourceInspector();
        var initial = inspector.Inspect(sourceDirectory, cancellationToken: cancellationToken);
        if (initial.SourceTreeHash != expectedSourceTreeHash)
            throw new InvalidOperationException("P50_STALE_SOURCE_TREE_HASH");

        var paths = Directory.GetFiles(Path.GetFullPath(sourceDirectory), "*.cs",
                SearchOption.AllDirectories)
            .Where(p => !Path.GetRelativePath(sourceDirectory, p)
                .Replace('\\', '/').Split('/')
                .Any(x => x is "bin" or "obj" or ".git" or "node_modules" or "private-generated"))
            .OrderBy(p => p, StringComparer.Ordinal).ToArray();
        if (paths.Length != initial.InspectedFiles)
            throw new InvalidOperationException("P50_SOURCE_SCOPE_CHANGED");

        var classes = new List<ClassDeclarationSyntax>();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = File.ReadAllBytes(path);
            var text = new UTF8Encoding(false, true).GetString(bytes);
            var relative = Path.GetRelativePath(sourceDirectory, path).Replace('\\','/');
            var tree = CSharpSyntaxTree.ParseText(text, path: relative);
            Require(!tree.GetDiagnostics().Any(x => x.Severity == DiagnosticSeverity.Error),
                "P50_INVALID_SOURCE_SYNTAX");
            classes.AddRange(tree.GetRoot().DescendantNodes()
                .OfType<ClassDeclarationSyntax>());
        }
        var witnesses = new List<PomFlowSourceWitness>();
        try
        {
            ClassDeclarationSyntax Unique(string name)
            {
                var found = classes.Where(x => x.Identifier.ValueText == name).ToArray();
                Require(found.Length == 1, "P50_CLASS_UNRESOLVED_OR_AMBIGUOUS");
                return found[0];
            }
            MethodDeclarationSyntax Method(ClassDeclarationSyntax type, string name)
            {
                var found = type.Members.OfType<MethodDeclarationSyntax>()
                    .Where(x => x.Identifier.ValueText == name).ToArray();
                Require(found.Length == 1 && found[0].Body != null,
                    "P50_METHOD_UNRESOLVED_OR_AMBIGUOUS");
                return found[0];
            }
            PropertyDeclarationSyntax Property(ClassDeclarationSyntax type, string name)
            {
                var found = type.Members.OfType<PropertyDeclarationSyntax>()
                    .Where(x => x.Identifier.ValueText == name).ToArray();
                Require(found.Length == 1, "P50_MEMBER_UNRESOLVED_OR_AMBIGUOUS");
                return found[0];
            }
            void Add(string kind, SyntaxNode node, int operations) =>
                witnesses.Add(new PomFlowSourceWitness(kind, Hash(node), Line(node), operations));

            var fixture = Unique(fixtureType);
            Require(fixture.BaseList?.Types.Count == 1,
                "P50_FIXTURE_BASE_UNPROVEN");
            var baseType = Unique(fixture.BaseList!.Types[0].Type.ToString());
            var test = Method(fixture, sourceMethod);
            Require(Attr(test, "Test") && test.ParameterList.Parameters.Count == 0
                && test.Body!.Statements.Count == 4,
                "P50_TEST_METHOD_SHAPE_UNPROVEN");
            var setup = fixture.Members.OfType<MethodDeclarationSyntax>()
                .Where(x => Attr(x, "SetUp")).ToArray();
            Require(setup.Length == 1 && setup[0].Body?.Statements.Count == 3,
                "P50_SETUP_SHAPE_UNPROVEN");
            var hook = setup[0];
            var setupStatements = hook.Body!.Statements;
            var declare = setupStatements[0] as LocalDeclarationStatementSyntax;
            Require(declare?.Declaration.Variables.Count == 1
                && declare.Declaration.Variables[0].Initializer?.Value
                    is InvocationExpressionSyntax,
                "P50_NAVIGATION_FACTORY_BINDING_UNPROVEN");
            var localName = declare!.Declaration.Variables[0].Identifier.ValueText;
            var opened = (InvocationExpressionSyntax)declare.Declaration.Variables[0]
                .Initializer!.Value;
            Require(opened.ArgumentList.Arguments.Count == 0
                && opened.Expression is MemberAccessExpressionSyntax
                { Expression: IdentifierNameSyntax nav },
                "P50_NAVIGATION_CALL_UNPROVEN");
            var navMember = (MemberAccessExpressionSyntax)opened.Expression;
            var navProperty = ((IdentifierNameSyntax)((MemberAccessExpressionSyntax)opened.Expression).Expression).Identifier.ValueText;
            var diProp = Property(baseType, navProperty);
            Require(diProp.ExpressionBody?.Expression is InvocationExpressionSyntax diCall
                && diCall.Expression is MemberAccessExpressionSyntax
                    { Name: GenericNameSyntax generic }
                && generic.Identifier.ValueText == "GetScopedRequiredService"
                && generic.TypeArgumentList.Arguments.Count == 1,
                "P50_TESTBASE_DI_BOUNDARY_UNPROVEN");
            var dependencyType = ((GenericNameSyntax)
                ((MemberAccessExpressionSyntax)((InvocationExpressionSyntax)
                    diProp.ExpressionBody!.Expression).Expression).Name)
                .TypeArgumentList.Arguments[0].ToString();
            Require(dependencyType == Name(diProp.Type),
                "P50_TESTBASE_DI_TYPE_MISMATCH");
            var navigation = Unique(dependencyType);
            var openPage = Method(navigation, navMember.Name.Identifier.ValueText);
            var field = fixture.Members.OfType<FieldDeclarationSyntax>()
                .Where(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == "page"))
                .ToArray();
            Require(field.Length == 1 && field[0].Declaration.Variables.Count == 1,
                "P50_FIXTURE_PAGE_FIELD_UNPROVEN");
            var pageType = Name(field[0].Declaration.Type);
            var returnStmt = openPage.Body!.Statements.SingleOrDefault()
                as ReturnStatementSyntax;
            Require(returnStmt?.Expression is InvocationExpressionSyntax navReturn
                && navReturn.Expression is GenericNameSyntax
                    { Identifier.ValueText: "OpenPage" } genericCall
                && genericCall.TypeArgumentList.Arguments.Count == 1
                && genericCall.TypeArgumentList.Arguments[0].ToString() == pageType
                && navReturn.ArgumentList.Arguments.Count == 1
                && Name(openPage.ReturnType) == pageType,
                "P50_NAVIGATION_PAGE_RETURN_UNPROVEN");
            var genericFactory = navigation.Members.OfType<MethodDeclarationSyntax>()
                .Where(m => m.Identifier.ValueText == "OpenPage").ToArray();
            Require(genericFactory.Length == 1
                && genericFactory[0].TypeParameterList?.Parameters.Count == 1
                && genericFactory[0].Body?.Statements.Count == 3
                && Calls(genericFactory[0]).Any(c => NameOfCall(c) == "GoToUrl")
                && Calls(genericFactory[0]).Any(c => NameOfCall(c) == "Sleep")
                && Calls(genericFactory[0]).Any(c => NameOfCall(c) == "CreatePage"),
                "P50_NAVIGATION_FACTORY_EFFECTS_UNPROVEN");
            // Preserve the two-stage source setup: navigation then loader then field assignment.
            var setupLoad = Inv(setupStatements[1]);
            Require(setupLoad != null && NameOfCall(setupLoad) == "ValidateLoading"
                && setupLoad.Expression is MemberAccessExpressionSyntax
                    { Expression: MemberAccessExpressionSyntax
                        { Expression: IdentifierNameSyntax owner,
                          Name.Identifier.ValueText: "Loader" }}
                && owner.Identifier.ValueText == localName,
                "P50_SETUP_LOADER_ORDER_UNPROVEN");
            Require(setupStatements[2] is ExpressionStatementSyntax
                    { Expression: AssignmentExpressionSyntax assign }
                && assign.Left.ToString() == "page"
                && assign.Right is IdentifierNameSyntax rhs
                && rhs.Identifier.ValueText == localName,
                "P50_SETUP_PAGE_ALIAS_UNPROVEN");
            var page = Unique(pageType);
            Require(page.Members.OfType<ConstructorDeclarationSyntax>().Count() == 1,
                "P50_PAGE_OBJECT_CONSTRUCTOR_UNPROVEN");
            Add("navigation_di_factory_and_page_alias", hook, 3);

            var steps = test.Body!.Statements.Select(Inv).ToArray();
            Require(steps.All(x => x != null), "P50_TEST_HAS_NONCALL_STATEMENT");
            Require(steps[0]!.ArgumentList.Arguments.Count == 1
                && NameOfCall(steps[0]!) == "InputAndSelect"
                && steps[0]!.Expression is MemberAccessExpressionSyntax
                    { Expression: MemberAccessExpressionSyntax
                        { Expression: IdentifierNameSyntax codeRoot }}
                && codeRoot.Identifier.ValueText == "page"
                && steps[0]!.ArgumentList.Arguments[0].Expression
                    is LiteralExpressionSyntax comboLiteral
                && comboLiteral.IsKind(SyntaxKind.StringLiteralExpression),
                "P50_COMBO_CALL_UNPROVEN");
            var comboPropertyName = ((MemberAccessExpressionSyntax)
                ((MemberAccessExpressionSyntax)steps[0]!.Expression).Expression)
                .Name.Identifier.ValueText;
            var comboProp = Property(page, comboPropertyName);
            var combo = Unique(Name(comboProp.Type));
            var input = Method(combo, "InputAndSelect");
            Require(input.ParameterList.Parameters.Count == 1
                && Name(input.ParameterList.Parameters[0].Type!) == "string"
                && input.Body!.Statements.Count == 5,
                "P50_COMBO_INPUT_CONTRACT_UNPROVEN");
            var expectedCombo = new[] { "Open", "EqualTo", "ClearAndInputText", "Click", "Click" };
            Require(input.Body.Statements.Select(Inv)
                .Select(x => x == null ? "" : NameOfCall(x))
                .SequenceEqual(expectedCombo, StringComparer.Ordinal)
                && Calls(input).Any(c => NameOfCall(c) == "Single")
                && Calls(input).Any(c => NameOfCall(c) == "Substring")
                && Calls(input).Any(c => NameOfCall(c) == "ClearAndInputText"
                    && c.ArgumentList.Arguments.Count == 1
                    && c.ArgumentList.Arguments[0].Expression
                        is IdentifierNameSyntax typedValue
                    && typedValue.Identifier.ValueText ==
                        input.ParameterList.Parameters[0].Identifier.ValueText)
                && Calls(input).Any(c => NameOfCall(c) == "Substring"
                    && c.ArgumentList.Arguments.Count == 1
                    && c.ArgumentList.Arguments[0].Expression
                        is IdentifierNameSyntax filteredValue
                    && filteredValue.Identifier.ValueText ==
                        input.ParameterList.Parameters[0].Identifier.ValueText),
                "P50_COMBO_ORDER_OR_FILTER_UNPROVEN");
            // The Open() helper may have additional effects, never erase it.
            var comboOpen = Method(combo, "Open");
            Require(comboOpen.ParameterList.Parameters.Count == 0
                && comboOpen.Body!.Statements.Count == 1
                && Inv(comboOpen.Body.Statements[0]) is InvocationExpressionSyntax openCall
                && NameOfCall(openCall) == "Click",
                "P50_COMBO_OPEN_EFFECTS_UNPROVEN");
            Add("combo_order_filter_confirmation", input, 5);
            Add("combo_open_helper_not_inlined", comboOpen, comboOpen.Body!.Statements.Count);

            Require(NameOfCall(steps[1]!) == "ValidateLoading"
                && steps[1]!.Expression is MemberAccessExpressionSyntax
                    { Expression: MemberAccessExpressionSyntax
                        { Expression: IdentifierNameSyntax loaderRoot,
                          Name.Identifier.ValueText: "Loader" }}
                && loaderRoot.Identifier.ValueText == "page",
                "P50_LOADER_CALL_UNPROVEN");
            var loader = Unique(Name(Property(page, "Loader").Type));
            var loaderMethod = Method(loader, "ValidateLoading");
            var whiles = loaderMethod.DescendantNodes()
                .OfType<WhileStatementSyntax>().ToArray();
            var numeric = loaderMethod.Body!.Statements
                .OfType<LocalDeclarationStatementSyntax>()
                .Select(x => x.Declaration).ToArray();
            Require(numeric.Length == 2
                && numeric.All(x => x.Variables.Count == 1)
                && numeric[0].Variables[0].Identifier.ValueText == "maxAttempts"
                && numeric[0].Variables[0].Initializer?.Value
                    is LiteralExpressionSyntax bound
                && bound.Token.Value is int limit && limit >= 1 && limit <= 10
                && numeric[1].Variables[0].Identifier.ValueText == "attempts"
                && numeric[1].Variables[0].Initializer?.Value
                    is LiteralExpressionSyntax initial
                && initial.Token.Value is int initialCount && initialCount == 0,
                "P50_LOADER_BOUND_NOT_STATIC");
            Require(whiles.Length == 1
                && whiles[0].Condition is BinaryExpressionSyntax guard
                && guard.IsKind(SyntaxKind.LessThanExpression)
                && guard.Left is IdentifierNameSyntax counter
                && counter.Identifier.ValueText == "attempts"
                && guard.Right is IdentifierNameSyntax max
                && max.Identifier.ValueText == "maxAttempts"
                && whiles[0].Statement is TryStatementSyntax retry
                && retry.Catches.Count == 1
                && retry.Catches[0].Declaration?.Type.ToString()
                    == "WebDriverTimeoutException"
                && retry.Block.Statements.OfType<IfStatementSyntax>()
                    .Any(i => Calls(i.Statement).Any(c =>
                        NameOfCall(c) == "ExecuteJs"
                        && c.ArgumentList.Arguments.Count == 1
                        && c.ArgumentList.Arguments[0].Expression
                            is LiteralExpressionSyntax script
                        && script.IsKind(SyntaxKind.StringLiteralExpression)))
                && retry.Block.DescendantNodes().OfType<BreakStatementSyntax>().Any()
                && retry.Catches[0].Block.Statements.Count == 2
                && retry.Catches[0].Block.Statements[0] is ExpressionStatementSyntax
                    { Expression: PostfixUnaryExpressionSyntax inc }
                && inc.IsKind(SyntaxKind.PostIncrementExpression)
                && inc.Operand is IdentifierNameSyntax incremented
                && incremented.Identifier.ValueText == "attempts"
                && retry.Catches[0].Block.Statements[1] is IfStatementSyntax failure
                && failure.Condition is BinaryExpressionSyntax equal
                && equal.IsKind(SyntaxKind.EqualsExpression)
                && equal.Left is IdentifierNameSyntax leftCount
                && leftCount.Identifier.ValueText == "attempts"
                && equal.Right is IdentifierNameSyntax rightBound
                && rightBound.Identifier.ValueText == "maxAttempts"
                && Calls(failure.Statement).Any(c => NameOfCall(c) == "Fail"),
                "P50_LOADER_RETRY_OR_FAILURE_UNPROVEN");
            Add("loader_bounded_retry_conditional_reload_failure", loaderMethod, 1);

            var table = Unique(Name(Property(page, "Table").Type));
            var items = Property(table, "Items");
            Require(items.Type is GenericNameSyntax { Identifier.ValueText: "ElementsCollection" }
                && ((GenericNameSyntax)items.Type).TypeArgumentList.Arguments.Count == 1,
                "P50_TABLE_ITEM_TYPE_UNPROVEN");
            var rowType = ((GenericNameSyntax)items.Type).TypeArgumentList
                .Arguments[0].ToString();
            var label = Unique(rowType);
            Require(Name(Property(label, "Text").Type) == "IProp<string>",
                "P50_TABLE_TEXT_PROPERTY_UNPROVEN");
            var expectations = new[] { "Be", "Contain" };
            for (var i = 0; i < 2; i++)
            {
                var call = steps[2 + i]!;
                Require(NameOfCall(call) == expectations[i]
                    && call.ArgumentList.Arguments.Count == 1
                    && call.ArgumentList.Arguments[0].Expression
                        is LiteralExpressionSyntax textLiteral
                    && textLiteral.IsKind(SyntaxKind.StringLiteralExpression),
                    "P50_TABLE_ASSERTION_TERMINAL_UNPROVEN");
                var sequence = Calls(call).Select(NameOfCall).Reverse().ToArray();
                Require(sequence.SequenceEqual(
                    new[] { "ElementAt", "Get", "Should", expectations[i] },
                    StringComparer.Ordinal),
                    "P50_TABLE_ASSERTION_CHAIN_UNPROVEN");
                var element = Calls(call).SingleOrDefault(c => NameOfCall(c) == "ElementAt");
                Require(element?.ArgumentList.Arguments.Count == 1
                    && element.ArgumentList.Arguments[0].Expression
                        is LiteralExpressionSyntax index
                    && index.IsKind(SyntaxKind.NumericLiteralExpression)
                    && element.Expression is MemberAccessExpressionSyntax
                        { Expression: MemberAccessExpressionSyntax
                            { Expression: MemberAccessExpressionSyntax
                                { Expression: IdentifierNameSyntax tableRoot,
                                  Name.Identifier.ValueText: "Table" },
                              Name.Identifier.ValueText: "Items" }}
                    && tableRoot.Identifier.ValueText == "page"
                    && call.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
                        .Any(x => x.Name.Identifier.ValueText == "Text"),
                    "P50_TABLE_ROW_RECEIVER_OR_INDEX_UNPROVEN");
            }
            Add("table_indexed_immediate_assertions", test, 2);

            // Proven SOURCE obligations only. No executable target statements.
            var final = inspector.Inspect(sourceDirectory, cancellationToken: cancellationToken);
            if (final.SourceTreeHash != expectedSourceTreeHash)
                throw new InvalidOperationException("P50_SOURCE_CHANGED_DURING_INSPECTION");
            return new PomFlowSourcePlan("migrator-pom-source-flow/v1",
                initial.SourceTreeHash, "source_verified_requires_review", "blocked",
                witnesses, Array.Empty<string>());
        }
        catch (Unsupported e)
        {
            if (inspector.Inspect(sourceDirectory, cancellationToken: cancellationToken)
                .SourceTreeHash != expectedSourceTreeHash)
                throw new InvalidOperationException("P50_SOURCE_CHANGED_DURING_INSPECTION");
            return new PomFlowSourcePlan("migrator-pom-source-flow/v1",
                initial.SourceTreeHash, "blocked", "blocked",
                Array.Empty<PomFlowSourceWitness>(), new[] { e.Code });
        }
    }
}
