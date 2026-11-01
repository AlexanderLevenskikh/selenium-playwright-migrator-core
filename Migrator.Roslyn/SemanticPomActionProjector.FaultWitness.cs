using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Migrator.Roslyn;

public static partial class SemanticPomActionProjector
{
    /// <summary>
    /// Extra preview.45 *source-scope* safety gate. We can prove absence
    /// of Console.SetError in the entire captured source snapshot, not
    /// absence of mutations from external assemblies or NUnit itself.
    /// Rebinding Console.Error is a side effect that makes even the fixed
    /// per-resource safe diagnostic path unverified for this snapshot.
    /// </summary>
    private static bool HasCapturedConsoleSinkOverride(
        CSharpCompilation compilation,
        CancellationToken cancellationToken,
        out Problem? problem)
    {
        problem = null;
        foreach (var tree in compilation.SyntaxTrees
            .Where(x => !x.FilePath.StartsWith("__Migrator.", StringComparison.Ordinal)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var model = compilation.GetSemanticModel(tree, true);
            foreach (var call in tree.GetRoot(cancellationToken)
                .DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var target = model.GetSymbolInfo(call).Symbol as IMethodSymbol;
                if (target?.Name != "SetError"
                    || target.ContainingType.ToDisplayString()
                        != "System.Console")
                    continue;
                problem = new Problem(
                    "POM_FAULT_WITNESS_CONSOLE_ERROR_REBOUND",
                    tree.FilePath, Line(call), Hash(call),
                    "Captured source replaces Console.Error. Source-only sink identity and exact safe diagnostic routing are not proven; no partial fault witness may be emitted.");
                return true;
            }
        }
        return false;
    }
}
