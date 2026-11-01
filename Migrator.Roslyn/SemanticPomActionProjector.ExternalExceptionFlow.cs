using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Migrator.Roslyn;

public static partial class SemanticPomActionProjector
{
    private const int MaxExternalExceptionCallSites = 4096;
    private const int MaxExternalExceptionCallDepth = 4;

    private sealed record ExternalExceptionCallSite(
        IMethodSymbol Callee, IMethodSymbol Caller,
        InvocationExpressionSyntax Invocation,
        bool InsideLocalFunction);

    /// <summary>
    /// Fail-closed static caller-chain scan, over exactly the immutable source
    /// trees already captured by Project(). No reflection, execution, or
    /// package access. Roslyn SYMBOL identity (not method names) is required
    /// at every call edge, including inherited TestBase methods.
    ///
    /// Absence of findings is not proof of complete runtime call coverage:
    /// unscanned assemblies, virtual/interface dispatch, delegates, reflection,
    /// constructors and asynchronous continuations require separate review.
    /// </summary>
    private static IReadOnlyList<Problem> InspectExternalExceptionCatchChains(
        CSharpCompilation compilation,
        IMethodSymbol sourceMethod, MethodDeclarationSyntax sourceDeclaration,
        IReadOnlyList<Step> projectedSourceEffects, CancellationToken cancel)
    {
        bool possibleMissing = projectedSourceEffects.Any(step =>
            step.Effect is "text_assertion" or "click" or "input_clear"
                or "input_sendkeys");
        bool possibleTimeout = projectedSourceEffects.Any(step =>
            step.Effect is "visible_wait" or "detached_wait"
                or "bounded_retry" or "retry_failure_rethrow");
        if (!possibleMissing && !possibleTimeout)
            return Array.Empty<Problem>();

        var findings = new List<Problem>();
        var keys = new HashSet<string>(StringComparer.Ordinal);

        void Finding(string code, SyntaxNode source, string message)
        {
            var path = source.SyntaxTree.FilePath;
            var line = Line(source);
            var key = code + ":" + path + ":" + line;
            if (!keys.Add(key)) return;
            findings.Add(new Problem(code, path, line, Hash(source), message));
        }

        if (sourceMethod.IsVirtual || sourceMethod.IsOverride
            || sourceMethod.IsAbstract
            || sourceMethod.ContainingType.TypeKind == TypeKind.Interface)
            Finding("POM_EXTERNAL_EXCEPTION_DYNAMIC_DISPATCH", sourceDeclaration,
                "The selected source method uses dynamic dispatch; static caller chains cannot prove the exception contract.");

        var sites = new List<ExternalExceptionCallSite>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            cancel.ThrowIfCancellationRequested();
            // Synthetic compiler anchors are not captured customer sources.
            if (!tree.FilePath.EndsWith(".cs", StringComparison.Ordinal)
                || tree.FilePath.StartsWith("__Migrator.", StringComparison.Ordinal))
                continue;

            var model = compilation.GetSemanticModel(tree, true);
            foreach (var invocation in tree.GetRoot(cancel)
                .DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                cancel.ThrowIfCancellationRequested();
                var callerMethod = invocation.Ancestors()
                    .OfType<MethodDeclarationSyntax>().FirstOrDefault();
                if (callerMethod == null) continue;
                var caller = model.GetDeclaredSymbol(callerMethod) as IMethodSymbol;
                var callee = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                if (caller == null || callee == null) continue;
                if (sites.Count == MaxExternalExceptionCallSites)
                {
                    Finding("POM_EXTERNAL_EXCEPTION_GRAPH_LIMIT", sourceDeclaration,
                        "More than 4096 exact caller invocations in the captured source tree; external exception handlers cannot be fully assessed.");
                    return findings;
                }
                var isLocal = invocation.Ancestors()
                    .OfType<LocalFunctionStatementSyntax>().Any(local =>
                        callerMethod.Span.Contains(local.Span));
                sites.Add(new ExternalExceptionCallSite(callee.OriginalDefinition,
                    caller.OriginalDefinition, invocation, isLocal));
            }
        }

        var queue = new Queue<(IMethodSymbol Method, int Depth)>();
        var visited = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        queue.Enqueue((sourceMethod.OriginalDefinition, 0));
        visited.Add(sourceMethod.OriginalDefinition);

        while (queue.Count != 0)
        {
            cancel.ThrowIfCancellationRequested();
            var (current, depth) = queue.Dequeue();
            foreach (var site in sites)
            {
                if (!SymbolEqualityComparer.Default.Equals(site.Callee, current))
                    continue;

                // Looking at the selected method's own recursive call does
                // not constitute an external caller.
                if (SymbolEqualityComparer.Default.Equals(site.Caller,
                        sourceMethod.OriginalDefinition))
                    continue;

                if (site.InsideLocalFunction)
                {
                    Finding("POM_EXTERNAL_EXCEPTION_LOCAL_FUNCTION", site.Invocation,
                        "A deferred or local-function call into the source method is not proved to execute in the enclosing catch scope.");
                    continue;
                }

                foreach (var enclosingTry in site.Invocation.Ancestors()
                    .OfType<TryStatementSyntax>())
                {
                    if (!enclosingTry.Block.Span.Contains(site.Invocation.Span))
                        continue;
                    foreach (var clause in enclosingTry.Catches)
                    {
                        var semantic = compilation.GetSemanticModel(
                            clause.SyntaxTree, true);
                        var type = clause.Declaration == null ? null
                            : semantic.GetTypeInfo(clause.Declaration.Type).Type;
                        var caught = type?.ToDisplayString() ?? "";
                        var broad = clause.Declaration == null
                            || caught is "System.Exception"
                                or "OpenQA.Selenium.WebDriverException";
                        var catchesMissing = possibleMissing
                            && caught == "OpenQA.Selenium.NoSuchElementException";
                        var catchesTimeout = possibleTimeout
                            && caught == "OpenQA.Selenium.WebDriverTimeoutException";
                        if (!broad && !catchesMissing && !catchesTimeout)
                            continue;

                        var category = clause.Filter != null
                            ? "POM_EXTERNAL_EXCEPTION_FILTER"
                            : broad
                                ? "POM_EXTERNAL_EXCEPTION_BROAD_CATCH"
                                : catchesMissing
                                    ? "POM_EXTERNAL_NO_SUCH_ELEMENT_CATCH"
                                    : "POM_EXTERNAL_WEBDRIVER_TIMEOUT_CATCH";
                        Finding(category, clause,
                            "A source-symbol-linked caller up to four method hops away has a Selenium-specific, broad or filtered exception handler. Rewritten Playwright CLR exception types and filter behavior cannot be inferred. Block the entire target candidate.");
                    }
                }

                // A relevant caller catch has already blocked the source
                // method. Still follow callers to reveal further affected
                // TestBase/NUnit scopes and avoid missing an outer filter.
                if (depth >= MaxExternalExceptionCallDepth)
                {
                    Finding("POM_EXTERNAL_EXCEPTION_DEPTH_LIMIT",
                        site.Invocation,
                        "The source exception may propagate beyond four statically resolved caller hops; manual whole-project exception-flow review required.");
                    continue;
                }
                if (!visited.Add(site.Caller))
                    continue;
                queue.Enqueue((site.Caller, depth + 1));
            }
        }
        return findings;
    }
}
