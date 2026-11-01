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
    public sealed record ExternalAsyncMethodEvidence(
        string SourcePath, int SourceLine, string SourceSha256,
        string SourceSymbol, string TargetSignature, string TargetBody);
    public sealed record ExternalContractProjection(
        string SchemaVersion, string Status, string SourceMethodSha256,
        IReadOnlyList<ExternalCatchEvidence> ExternalCatches,
        IReadOnlyList<Problem> Blockers,
        IReadOnlyList<string> Limitations)
    {
        public IReadOnlyList<ExternalAsyncMethodEvidence> AsyncMethods { get; init; }
            = Array.Empty<ExternalAsyncMethodEvidence>();
        public string VerifiedPageReceiver { get; init; } = "";
        public ExternalFixtureLifecycleEvidence? FixtureLifecycle { get; init; }
    }

    /// <summary>
    /// A separate, unapproved, source-backed catch CLAUSE candidate.
    /// Never emits a complete Playwright test or auto-applies it. The prior
    /// PlanPomActions stays blocked on affected external catch chains.
    /// </summary>
    public static ExternalContractProjection ProjectExternalExceptionContract(
        IReadOnlyList<(string Path, string Source)> sources,
        string sourceType, string sourceMethod, int sourceLine = 0,
        CancellationToken cancel = default,
        bool requireVerifiedAsyncCount = false, string explicitPageReceiver = "",
        bool requireFixtureLifecycle = false,
        string explicitContextReceiver = "", string explicitBrowserReceiver = "",
        string explicitPlaywrightReceiver = "",
        bool requireFailureAtomic = false,
        bool requireSafeDiagnostics = false)
    {
        var schema = requireSafeDiagnostics
            ? "migrator-pom-external-safe-diagnostics-count/v1"
            : requireFailureAtomic
                ? "migrator-pom-external-failure-atomic-count/v1"
            : requireFixtureLifecycle
                ? "migrator-pom-external-async-lifecycle-count/v1"
            : requireVerifiedAsyncCount
                ? "migrator-pom-external-async-count/v1"
                : "migrator-pom-external-exception-contract/v1";
        var problems = new List<Problem>();
        var candidates = new List<ExternalCatchEvidence>();
        var asyncMethods = new List<ExternalAsyncMethodEvidence>();
        ExternalFixtureLifecycleEvidence? lifecycleEvidence = null;
        var methodSha = "";
        ExternalContractProjection Result() => new(schema,
            problems.Count == 0 && candidates.Count == 1
                && (!requireVerifiedAsyncCount || asyncMethods.Count == 3)
                && (!requireFixtureLifecycle || lifecycleEvidence != null)
                && (!requireFailureAtomic ||
                    lifecycleEvidence?.PrimaryExceptionRethrown == true)
                && (!requireSafeDiagnostics ||
                    lifecycleEvidence?.SafeSecondaryDiagnosticCodes == true)
                ? "requires_review" : "blocked",
            methodSha, problems.Count == 0
                && (!requireVerifiedAsyncCount || asyncMethods.Count == 3)
                && (!requireFixtureLifecycle || lifecycleEvidence != null)
                && (!requireFailureAtomic ||
                    lifecycleEvidence?.PrimaryExceptionRethrown == true)
                && (!requireSafeDiagnostics ||
                    lifecycleEvidence?.SafeSecondaryDiagnosticCodes == true)
                ? candidates : Array.Empty<ExternalCatchEvidence>(),
            problems,
            new[] {
                "Source-only clause proposal; never automatically apply or execute code.",
                "The isolated catch clause must be reviewed together with the separately migrated source call and NUnit lifecycle.",
                "The emitted clause is NOT an adapter for the public Selenium CLR exception type.",
                "Only a captured exact-symbol synchronous forwarding chain is analyzed; external assemblies, dynamic dispatch, reflection, async and delegates are unverified.",
                "An unknown/duplicate caller or catch filter blocks the whole contract, never produces a partial target.",
                "Async conversion proposals require explicit source-typed Playwright IPage constructor binding; external runtime lifecycle and same-DOM semantics are not automatically proven.",
                "Three method bodies/signatures are review-only snippets; target Page Object and TestBase instance wiring require independent build and real browser comparison.",
                "Preview.42-A proves only the captured exact NUnit fixture's typed setup/cleanup and constructor flow; other assemblies, NUnit scheduling, failure of lifecycle setup itself, reflection and runtime browser parity are not inferred.",
                "Preview.43-A validates explicit guarded setup cleanup followed by bare rethrow. Secondary cleanup failures are deliberately suppressed to preserve the primary exception, not logged; cleanup of resources an API never returned is not proven.",
                "Preview.44-A admits only source-proven constant stderr codes for secondary cleanup failure and a guarded diagnostic write that cannot replace the primary; external API internals and unrelated OS processes are not covered."
            })
        {
            AsyncMethods = problems.Count == 0 && candidates.Count == 1
                ? asyncMethods : Array.Empty<ExternalAsyncMethodEvidence>(),
            VerifiedPageReceiver = problems.Count == 0 && candidates.Count == 1
                && requireVerifiedAsyncCount ? explicitPageReceiver : "",
            FixtureLifecycle = problems.Count == 0 && candidates.Count == 1
                && requireFixtureLifecycle
                && (!requireFailureAtomic ||
                    lifecycleEvidence?.PrimaryExceptionRethrown == true)
                && (!requireSafeDiagnostics ||
                    lifecycleEvidence?.SafeSecondaryDiagnosticCodes == true)
                ? lifecycleEvidence : null
        };
        void Block(string code, SyntaxNode node, string reason) =>
            problems.Add(new Problem(code, node.SyntaxTree.FilePath,
                Line(node), Hash(node), reason));
        void InputBlock(string code, string reason) =>
            problems.Add(new Problem(code, "", 0, "", reason));

        if (requireSafeDiagnostics && !requireFailureAtomic)
        {
            InputBlock("POM_EXTERNAL_DIAGNOSTICS_REQUIRE_ATOMIC",
                "Only failure-atomic setup with exact ownership may emit safe diagnostics evidence.");
            return Result();
        }
        if (requireFailureAtomic && !requireFixtureLifecycle)
        {
            InputBlock("POM_EXTERNAL_ATOMIC_REQUIRES_LIFECYCLE",
                "An atomic setup proof must also prove exact source lifecycle ownership.");
            return Result();
        }
        if (requireFixtureLifecycle
            && (!requireVerifiedAsyncCount
                || new[] { explicitPageReceiver, explicitContextReceiver,
                    explicitBrowserReceiver, explicitPlaywrightReceiver }
                    .Any(value => !SyntaxFacts.IsValidIdentifier(value)
                        || value.StartsWith("@", StringComparison.Ordinal))
                || new[] { explicitPageReceiver, explicitContextReceiver,
                    explicitBrowserReceiver, explicitPlaywrightReceiver }
                    .Distinct(StringComparer.Ordinal).Count() != 4))
        {
            InputBlock("POM_EXTERNAL_LIFECYCLE_RECEIVERS_REQUIRED",
                "The exact page, context, browser and Playwright source field identifiers must all be supplied, distinct and valid.");
            return Result();
        }

        if (requireVerifiedAsyncCount && (
            !SyntaxFacts.IsValidIdentifier(explicitPageReceiver)
            || explicitPageReceiver.StartsWith("@", StringComparison.Ordinal)))
        {
            InputBlock("POM_EXTERNAL_ASYNC_RECEIVER_REQUIRED",
                "Pass one explicit C# IPage field identifier; never infer a receiver.");
            return Result();
        }

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
            int Depth, string Route, IReadOnlyList<MethodDeclarationSyntax> Nodes)>();
        var visited = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        frontier.Enqueue((srcMethod.OriginalDefinition, 0,
            srcMethod.ToDisplayString(), new[] { root.Method }));
        visited.Add(srcMethod.OriginalDefinition);
        var endpoints = 0;
        while (frontier.Count != 0)
        {
            cancel.ThrowIfCancellationRequested();
            var (called, depth, route, nodes) = frontier.Dequeue();
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
                    frontier.Enqueue((call.Caller, depth + 1, callerRoute,
                        nodes.Concat(new[] { member }).ToArray()));
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
                string handler = "";
                if (requireVerifiedAsyncCount)
                {
                    // Explicit opt-in: exactly Page Object -> one TestBase
                    // forwarder -> one source NUnit [Test]. All three are
                    // non-virtual synchronous zero-argument void methods.
                    if (nodes.Count != 2
                        || !SymbolEqualityComparer.Default.Equals(
                            call.Caller.ContainingType.BaseType,
                            compilation.GetSemanticModel(nodes[1].SyntaxTree, true)
                                .GetDeclaredSymbol(nodes[1]) is IMethodSymbol helper
                                    ? helper.ContainingType : null)
                        || !call.Caller.GetAttributes().Any(a =>
                            a.AttributeClass?.ToDisplayString() ==
                                "NUnit.Framework.TestAttribute")
                        || !nodes.Append(member).All(AsyncConvertibleSignature))
                    {
                        Block("POM_EXTERNAL_ASYNC_NUNIT_CHAIN_UNPROVEN", member,
                            "Require one exact non-polymorphic TestBase forwarder and one ordinary NUnit [Test] void method; async Task conversion cannot be inferred for other methods.");
                        continue;
                    }
                    if (clause.Block.Statements.Count != 1
                        || clause.Block.Statements[0] is not ExpressionStatementSyntax
                            { Expression: InvocationExpressionSyntax recovery }
                        || !TryTranslate(recovery, model, call.Caller,
                            out var recoveryEffect, out var translated,
                            out _, out _, out _, out _)
                        || recoveryEffect != "table_count_assertion"
                        || !SourceCountDriverField(recovery, model, call.Caller)
                        || (!requireFixtureLifecycle &&
                            !TryVerifiedPageField(model, call.Caller,
                                explicitPageReceiver, out _)))
                    {
                        Block("POM_EXTERNAL_ASYNC_COUNT_OR_RECEIVER_UNPROVEN", clause,
                            "Require one typed immediate NUnit FindElements.Count equality over the source driver and an explicit readonly Microsoft.Playwright.IPage field assigned from a same-typed constructor parameter.");
                        continue;
                    }
                    if (requireFixtureLifecycle)
                    {
                        if (!TryVerifyFixtureLifecycle(compilation, srcMethod,
                            compilation.GetSemanticModel(nodes[1].SyntaxTree, true)
                                .GetDeclaredSymbol(nodes[1]) as IMethodSymbol
                                    ?? throw new InvalidOperationException(
                                        "TESTBASE_METHOD_SYMBOL_UNAVAILABLE"),
                            call.Caller,
                            explicitPageReceiver, explicitContextReceiver,
                            explicitBrowserReceiver, explicitPlaywrightReceiver,
                            out var verified, out var reason,
                            requireFailureAtomic, requireSafeDiagnostics))
                        {
                            Block("POM_EXTERNAL_LIFECYCLE_FLOW_UNPROVEN", member,
                                reason);
                            continue;
                        }
                        lifecycleEvidence = verified;
                    }
                    // Only the current project's own source-typed IPage
                    // receiver is allowed; TryTranslate emits Page.Locator.
                    // There is no target receiver guessing or source selector
                    // emitted over MCP.
                    const string originalPrefix = "Page.Locator(";
                    if (translated.Split(originalPrefix, StringSplitOptions.None).Length != 2)
                    {
                        Block("POM_EXTERNAL_ASYNC_LOCATOR_UNVERIFIED", recovery,
                            "Expected exactly one source-projected Page.Locator Count snapshot.");
                        continue;
                    }
                    handler = translated.Replace(originalPrefix,
                        explicitPageReceiver + ".Locator(", StringComparison.Ordinal);

                    asyncMethods.Add(new ExternalAsyncMethodEvidence(
                        root.Method.SyntaxTree.FilePath, Line(root.Method),
                        Hash(root.Method), srcMethod.ToDisplayString(),
                        AsyncSignature(root.Method),
                        "try {\n" + projected + "\n}\n"
                            + RenderSourceCatchHeader(kind) + " { throw; }"));
                    var forwarding = nodes[1];
                    var forwardedCall = (InvocationExpressionSyntax)
                        ((ExpressionStatementSyntax)forwarding.Body!.Statements[0]).Expression;
                    asyncMethods.Add(new ExternalAsyncMethodEvidence(
                        forwarding.SyntaxTree.FilePath, Line(forwarding),
                        Hash(forwarding),
                        compilation.GetSemanticModel(forwarding.SyntaxTree, true)
                            .GetDeclaredSymbol(forwarding)!.ToDisplayString(),
                        AsyncSignature(forwarding),
                        "await " + forwardedCall + ";"));
                    // NUnit conversion gets its complete try/catch body below.
                }
                else if (clause.Block.Statements.Count != 0)
                {
                    Block("POM_EXTERNAL_CONTRACT_CATCH_BODY_UNSUPPORTED", clause,
                        "Nonempty outer catch (including NUnit Count recovery) requires an unverified async conversion, target receiver or side-effect mapping; block rather than emit a partial clause.");
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
                if (requireVerifiedAsyncCount)
                    asyncMethods.Add(new ExternalAsyncMethodEvidence(
                        member.SyntaxTree.FilePath, Line(member), Hash(member),
                        call.Caller.ToDisplayString(), AsyncSignature(member),
                        "try {\nawait " + call.Site + ";\n}\n" + resultClause));
                endpoints++;
            }
        }

        if (requireVerifiedAsyncCount)
        {
            // Constructor, property getter, field initializer and other
            // out-of-method calls are not represented by the method-edge
            // traversal. A relevant call at one of those sites is NOT
            // evidence that the only known NUnit path is complete.
            foreach (var tree in trees)
            {
                cancel.ThrowIfCancellationRequested();
                var semantic = compilation.GetSemanticModel(tree, true);
                foreach (var invocation in tree.GetRoot(cancel)
                    .DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (semantic.GetSymbolInfo(invocation).Symbol
                        is not IMethodSymbol invoked
                        || !visited.Contains(invoked.OriginalDefinition))
                        continue;
                    if (!allCalls.Any(site =>
                        site.Site.SyntaxTree == invocation.SyntaxTree
                        && site.Site.Span == invocation.Span))
                        Block("POM_EXTERNAL_ASYNC_UNCAPTURED_CALL", invocation,
                            "An invocation from a constructor, initializer or another non-method scope reaches the candidate chain; no partial async contract is safe.");
                }
            }
        }
        if (endpoints != 1 || candidates.Count != 1)
            Block("POM_EXTERNAL_CONTRACT_PATH_CARDINALITY", root.Method,
                "Exactly one statically confirmed external NUnit/TestBase catch path is required; missing or multiple paths are blocked.");
        return Result();
    }

    private static bool AsyncConvertibleSignature(MethodDeclarationSyntax method) =>
        method.Body != null && method.ExpressionBody == null
        && method.Modifiers.All(m => m.IsKind(SyntaxKind.PublicKeyword)
            || m.IsKind(SyntaxKind.ProtectedKeyword)
            || m.IsKind(SyntaxKind.InternalKeyword)
            || m.IsKind(SyntaxKind.PrivateKeyword));

    private static string AsyncSignature(MethodDeclarationSyntax method) =>
        string.Join(" ", method.Modifiers.Select(m => m.ValueText))
        + " async System.Threading.Tasks.Task "
        + method.Identifier.ValueText + "()";

    private static bool SourceCountDriverField(
        InvocationExpressionSyntax recovery, SemanticModel model,
        IMethodSymbol owner)
    {
        if (recovery.ArgumentList.Arguments.Count == 0
            || recovery.ArgumentList.Arguments[0].Expression
                is not MemberAccessExpressionSyntax count
            || count.Expression is not InvocationExpressionSyntax find
            || find.Expression is not MemberAccessExpressionSyntax findMember)
            return false;
        return model.GetSymbolInfo(findMember.Expression).Symbol is IFieldSymbol field
            && !field.IsStatic
            && field.Type.ToDisplayString() == "OpenQA.Selenium.IWebDriver"
            && SymbolEqualityComparer.Default.Equals(field.ContainingType,
                owner.ContainingType);
    }

    private static bool TryVerifiedPageField(
        SemanticModel model, IMethodSymbol nunit,
        string receiver, out IFieldSymbol? field)
    {
        field = null;
        var matches = nunit.ContainingType.GetMembers(receiver)
            .OfType<IFieldSymbol>().ToArray();
        if (matches.Length != 1 || !matches[0].IsReadOnly
            || matches[0].IsStatic || matches[0].Type.NullableAnnotation
                == NullableAnnotation.Annotated
            || matches[0].Type.ToDisplayString() != "Microsoft.Playwright.IPage"
            || matches[0].Type.ContainingAssembly?.Name != "Microsoft.Playwright")
            return false;
        field = matches[0];
        var boundField = field;
        var constructors = nunit.ContainingType.InstanceConstructors
            .Where(c => c.DeclaringSyntaxReferences.Length != 0).ToArray();
        if (constructors.Length != 1 || constructors[0].Parameters
            .Count(p => p.Type.ToDisplayString() == "Microsoft.Playwright.IPage"
                && p.Type.ContainingAssembly?.Name == "Microsoft.Playwright") != 1)
            return false;
        var ctor = constructors[0];
        var syntax = ctor.DeclaringSyntaxReferences.Single().GetSyntax()
            as ConstructorDeclarationSyntax;
        if (syntax?.Body == null) return false;
        var ctorModel = model.Compilation.GetSemanticModel(syntax.SyntaxTree, true);
        return syntax.Body.Statements.OfType<ExpressionStatementSyntax>()
            .Select(s => s.Expression)
            .OfType<AssignmentExpressionSyntax>()
            .Count(a => SymbolEqualityComparer.Default.Equals(
                    ctorModel.GetSymbolInfo(a.Left).Symbol, boundField)
                && ctorModel.GetSymbolInfo(a.Right).Symbol is IParameterSymbol p
                && SymbolEqualityComparer.Default.Equals(p.ContainingSymbol, ctor)
                && p.Type.ToDisplayString() == "Microsoft.Playwright.IPage") == 1;
    }

    private static bool ValidMethod(IMethodSymbol method) =>
        method.ReturnsVoid && !method.IsAsync && !method.IsGenericMethod
        && !method.IsVirtual && !method.IsOverride
        && method.Parameters.Length == 0;
}
