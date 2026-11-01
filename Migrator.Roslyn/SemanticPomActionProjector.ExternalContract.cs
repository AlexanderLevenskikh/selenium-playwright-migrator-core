using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Migrator.Roslyn;

public static partial class SemanticPomActionProjector
{
    public sealed record ExternalCatchEvidence(
        string CatchSourcePath, int CatchSourceLine, string CatchSourceSha256,
        string SourceCaughtType, string TargetCaughtType,
        string SourceCallerSymbol, string CallerChain,
        string TargetCatchClause, string TargetCatchSha256);
    public sealed record ExternalContractProjection(
        string SchemaVersion, string Status, string SourceMethodSha256,
        IReadOnlyList<ExternalCatchEvidence> ExternalCatches,
        IReadOnlyList<Problem> Blockers,
        IReadOnlyList<string> Limitations);

    /// <summary>
    /// A separate, unapproved, source-backed catch CLAUSE candidate.
    /// Never emits a complete Playwright test or auto-applies it. The prior
    /// PlanPomActions stays blocked on affected external catch chains.
    /// </summary>
    public static ExternalContractProjection ProjectExternalExceptionContract(
        IReadOnlyList<(string Path, string Source)> sources,
        string sourceType, string sourceMethod, int sourceLine = 0,
        CancellationToken cancel = default)
    {
        const string schema = "migrator-pom-external-exception-contract/v1";
        var problems = new List<Problem>();
        var candidates = new List<ExternalCatchEvidence>();
        var methodSha = "";
        ExternalContractProjection Result() => new(schema,
            problems.Count == 0 && candidates.Count == 1
                ? "requires_review" : "blocked",
            methodSha, problems.Count == 0 ? candidates : Array.Empty<ExternalCatchEvidence>(),
            problems,
            new[] {
                "Source-only clause proposal; never automatically apply or execute code.",
                "The isolated catch clause must be reviewed together with the separately migrated source call and NUnit lifecycle.",
                "The emitted clause is NOT an adapter for the public Selenium CLR exception type.",
                "Only a captured exact-symbol synchronous forwarding chain is analyzed; external assemblies, dynamic dispatch, reflection, async and delegates are unverified.",
                "An unknown/duplicate caller or catch filter blocks the whole contract, never produces a partial target."
            });
        void Block(string code, SyntaxNode node, string reason) =>
            problems.Add(new Problem(code, node.SyntaxTree.FilePath,
                Line(node), Hash(node), reason));
        void InputBlock(string code, string reason) =>
            problems.Add(new Problem(code, "", 0, "", reason));

        if (sources.Count is < 1 or > MaxFiles ||
            sources.Sum(s => (long)s.Source.Length) > MaxSourceChars)
        {
            InputBlock("POM_EXTERNAL_CONTRACT_SCOPE_LIMIT", "Captured source tree exceeds the POM source limits.");
            return Result();
        }

        var trees = sources.OrderBy(x => x.Path, StringComparer.Ordinal)
            .Select(x => CSharpSyntaxTree.ParseText(x.Source,
                new CSharpParseOptions(LanguageVersion.Preview), path: x.Path))
            .ToArray();
        foreach (var tree in trees)
            if (tree.GetDiagnostics().Any(x => x.Severity == DiagnosticSeverity.Error))
            {
                InputBlock("POM_EXTERNAL_CONTRACT_SOURCE_PARSE", "A captured C# file contains parse errors.");
                return Result();
            }

        var compilation = SemanticCompilationSupport.CreateCompilation(trees);
        var declarations = trees.SelectMany(tree =>
            tree.GetRoot(cancel).DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Select(method => (Method: method,
                    Symbol: compilation.GetSemanticModel(tree, true)
                        .GetDeclaredSymbol(method) as IMethodSymbol)))
            .Where(x => x.Symbol != null).ToArray();
        var roots = declarations.Where(x => x.Method.Identifier.ValueText == sourceMethod
            && (sourceLine == 0 || Line(x.Method) == sourceLine)
            && (x.Symbol!.ContainingType.ToDisplayString() == sourceType
                || x.Symbol.ContainingType.Name == sourceType))
            .ToArray();
        if (roots.Length != 1)
        {
            InputBlock("POM_EXTERNAL_CONTRACT_SOURCE_AMBIGUOUS",
                "Choose one exactly typed Page Object method by source type, name, and declaration line.");
            return Result();
        }

        var root = roots[0];
        methodSha = Hash(root.Method);
        var srcMethod = root.Symbol!;
        if (!ValidMethod(srcMethod) || root.Method.Body?.Statements.Count != 1
            || root.Method.Body.Statements[0] is not TryStatementSyntax rootTry)
        {
            Block("POM_EXTERNAL_CONTRACT_INNER_UNSUPPORTED", root.Method,
                "Only one synchronous void source method with a single typed try/catch rethrow and no other actions is admitted.");
            return Result();
        }
        var sourceModel = compilation.GetSemanticModel(root.Method.SyntaxTree, true);
        if (!TryRecognizeSourceCatch(rootTry, sourceModel, out var catchShape)
            || catchShape?.TerminalRethrow == null)
        {
            Block("POM_EXTERNAL_CONTRACT_INNER_RETHROW_UNPROVEN", root.Method,
                "A single source-typed local NoSuchElement or WebDriverTimeout catch with bare throw; is required.");
            return Result();
        }
        var kind = catchShape.Kind;
        // Confirm the protected source call is not merely named like a
        // supported operation: it must project to the known one-shot Text
        // assertion or finite WebDriverWait, with no unknown side effects.
        if (!TryTranslate(catchShape.ProtectedCall, sourceModel, srcMethod,
                out var effect, out var projected,
                out _, out _, out _, out _)
            || (kind == SourceCatchKind.NoSuchElementFromImmediateText
                && effect != "text_assertion")
            || (kind == SourceCatchKind.WebDriverWaitTimeout
                && effect is not ("visible_wait" or "detached_wait"))
            || string.IsNullOrWhiteSpace(projected))
        {
            Block("POM_EXTERNAL_CONTRACT_INNER_ACTION_UNPROVEN",
                catchShape.ProtectedCall,
                "The inner source operation must be a fully projectable immediate Text assertion or finite WebDriverWait.");
            return Result();
        }
        if (srcMethod.IsVirtual || srcMethod.IsOverride ||
            srcMethod.IsAbstract || srcMethod.ContainingType.TypeKind == TypeKind.Interface)
        {
            Block("POM_EXTERNAL_CONTRACT_DYNAMIC_DISPATCH", root.Method,
                "Dynamic dispatch changes the exception path, so the external adapter is blocked.");
            return Result();
        }

        var allCalls = new List<(IMethodSymbol Called,
            IMethodSymbol Caller, InvocationExpressionSyntax Site,
            MethodDeclarationSyntax CallerDeclaration)>();
        foreach (var item in declarations)
        {
            cancel.ThrowIfCancellationRequested();
            var semantic = compilation.GetSemanticModel(item.Method.SyntaxTree, true);
            foreach (var call in item.Method.DescendantNodes()
                .OfType<InvocationExpressionSyntax>())
            {
                var owner = call.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                if (!ReferenceEquals(owner, item.Method)) continue;
                var invoked = semantic.GetSymbolInfo(call).Symbol as IMethodSymbol;
                if (invoked == null) continue;
                if (allCalls.Count >= MaxExternalExceptionCallSites)
                {
                    Block("POM_EXTERNAL_CONTRACT_GRAPH_LIMIT", root.Method,
                        "More than 4096 caller sites; do not emit an unverified external adapter.");
                    return Result();
                }
                allCalls.Add((invoked.OriginalDefinition,
                    item.Symbol!.OriginalDefinition, call, item.Method));
            }
        }

        var frontier = new Queue<(IMethodSymbol Method,
            int Depth, string Route)>();
        var visited = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        frontier.Enqueue((srcMethod.OriginalDefinition, 0, srcMethod.ToDisplayString()));
        visited.Add(srcMethod.OriginalDefinition);
        var endpoints = 0;
        while (frontier.Count != 0)
        {
            cancel.ThrowIfCancellationRequested();
            var (called, depth, route) = frontier.Dequeue();
            var sites = allCalls.Where(x =>
                SymbolEqualityComparer.Default.Equals(x.Called, called))
                .ToArray();
            foreach (var call in sites)
            {
                if (SymbolEqualityComparer.Default.Equals(call.Caller,
                    srcMethod.OriginalDefinition))
                {
                    Block("POM_EXTERNAL_CONTRACT_RECURSION", call.Site,
                        "Recursive source exception propagation is not proven.");
                    continue;
                }
                if (call.Site.Ancestors().OfType<LocalFunctionStatementSyntax>().Any()
                    || call.Site.Ancestors().OfType<AnonymousFunctionExpressionSyntax>().Any())
                {
                    Block("POM_EXTERNAL_CONTRACT_DEFERRED_CALL", call.Site,
                        "Local function, lambda or deferred execution is unverified.");
                    continue;
                }
                if (depth >= MaxExternalExceptionCallDepth)
                {
                    Block("POM_EXTERNAL_CONTRACT_DEPTH_LIMIT", call.Site,
                        "More than four caller hops; no partial external exception adapter.");
                    continue;
                }
                if (!ValidMethod(call.Caller)
                    || call.Caller.IsVirtual || call.Caller.IsOverride
                    || call.Caller.ContainingType.TypeKind == TypeKind.Interface)
                {
                    Block("POM_EXTERNAL_CONTRACT_CALLER_DYNAMIC", call.CallerDeclaration,
                        "Caller must be a synchronous non-polymorphic void method with no parameters.");
                    continue;
                }
                var expression = call.Site.Parent as ExpressionStatementSyntax;
                if (expression == null || expression.Expression.Span != call.Site.Span)
                {
                    Block("POM_EXTERNAL_CONTRACT_COMPOSED_CALL", call.Site,
                        "An exact synchronous call expression is required; do not infer side effects or values.");
                    continue;
                }
                var member = call.CallerDeclaration;
                var statements = member.Body?.Statements ?? default;
                var callerRoute = route + " -> " + call.Caller.ToDisplayString();
                if (statements.Count == 1
                    && statements[0].Span == expression.Span)
                {
                    if (!visited.Add(call.Caller))
                    {
                        Block("POM_EXTERNAL_CONTRACT_MULTIPATH", call.Site,
                            "Multiple or cyclic caller paths cannot be reduced to one safe external contract.");
                        continue;
                    }
                    frontier.Enqueue((call.Caller, depth + 1, callerRoute));
                    continue;
                }

                if (statements.Count != 1 ||
                    statements[0] is not TryStatementSyntax outer
                    || outer.Block.Statements.Count != 1
                    || outer.Block.Statements[0].Span != expression.Span
                    || outer.Finally != null
                    || outer.Catches.Count != 1)
                {
                    Block("POM_EXTERNAL_CONTRACT_CALLER_EFFECTS",
                        call.Site,
                        "The external caller must have one try with one source-bound call, exactly one catch, no finally and no other actions.");
                    continue;
                }

                var clause = outer.Catches[0];
                var model = compilation.GetSemanticModel(clause.SyntaxTree, true);
                var expected = kind == SourceCatchKind.NoSuchElementFromImmediateText
                    ? "OpenQA.Selenium.NoSuchElementException"
                    : "OpenQA.Selenium.WebDriverTimeoutException";
                var caught = clause.Declaration == null ? ""
                    : model.GetTypeInfo(clause.Declaration.Type).Type?.ToDisplayString();
                if (caught != expected || clause.Filter != null ||
                    clause.Declaration == null)
                {
                    Block("POM_EXTERNAL_CONTRACT_CATCH_TYPE_OR_FILTER",
                        clause,
                        "Only the exact Selenium exception type with no when-filter is allowed; broad/filtered/wrong catch is blocked.");
                    continue;
                }
                if (clause.Declaration.Identifier.ValueText.Length != 0)
                {
                    var symbol = model.GetDeclaredSymbol(clause.Declaration);
                    if (symbol == null ||
                        clause.Block.DescendantNodes().OfType<IdentifierNameSyntax>()
                            .Any(id => SymbolEqualityComparer.Default.Equals(
                                model.GetSymbolInfo(id).Symbol, symbol)))
                    {
                        Block("POM_EXTERNAL_CONTRACT_CATCH_VARIABLE_USED", clause,
                            "The original exception object/Message/StackTrace is used; target CLR identity differs.");
                        continue;
                    }
                }
                string handler;
                if (clause.Block.Statements.Count == 0)
                    handler = "";
                else if (clause.Block.Statements.Count == 1
                    && clause.Block.Statements[0] is ExpressionStatementSyntax
                    { Expression: InvocationExpressionSyntax recovery }
                    && TryTranslate(recovery, model, call.Caller,
                        out var recoveryEffect, out var translated,
                        out _, out _, out _, out _)
                    && recoveryEffect == "table_count_assertion")
                    handler = translated;
                else
                {
                    Block("POM_EXTERNAL_CONTRACT_CATCH_BODY_UNSUPPORTED", clause,
                        "Only an empty catch or one typed immediate NUnit Count equality is allowed; external throw/rethrow and side effects change CLR exception identity.");
                    continue;
                }

                var header = kind == SourceCatchKind.NoSuchElementFromImmediateText
                    ? "catch (Microsoft.Playwright.PlaywrightException externalMissing) " +
                      "when (externalMissing.Message.Contains(" +
                      "\"POM_SOURCE_ELEMENT_ABSENT\", System.StringComparison.Ordinal))"
                    : "catch (System.TimeoutException)";
                var resultClause = header + "\n{\n" + handler + "\n}";
                var sha = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(resultClause)))
                    .ToLowerInvariant();
                candidates.Add(new ExternalCatchEvidence(
                    clause.SyntaxTree.FilePath, Line(clause), Hash(clause),
                    expected, kind == SourceCatchKind.NoSuchElementFromImmediateText
                        ? "Microsoft.Playwright.PlaywrightException"
                        : "System.TimeoutException",
                    call.Caller.ToDisplayString(), callerRoute,
                    resultClause, sha));
                endpoints++;
            }
        }

        if (endpoints != 1 || candidates.Count != 1)
            Block("POM_EXTERNAL_CONTRACT_PATH_CARDINALITY", root.Method,
                "Exactly one statically confirmed external NUnit/TestBase catch path is required; missing or multiple paths are blocked.");
        return Result();
    }

    private static bool ValidMethod(IMethodSymbol method) =>
        method.ReturnsVoid && !method.IsAsync && !method.IsGenericMethod
        && !method.IsVirtual && !method.IsOverride
        && method.Parameters.Length == 0;
}
