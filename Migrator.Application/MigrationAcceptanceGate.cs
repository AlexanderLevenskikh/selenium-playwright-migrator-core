using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Migrator.Application;

/// <summary>
/// One source NUnit test/lifecycle obligation and its best matching target syntax.
/// A candidate match is NEVER a proof of observable or behavioral equivalence.
/// Source/target snippets are intentionally not exported; hashes bind the exact syntax.
/// </summary>
public sealed record SourceTargetLineage(
    string Id, string SourcePath, string Method, string Phase, int SourceLine,
    string Kind, string SourceSha256, string? TargetPath, int? TargetLine,
    string? TargetSha256, string Status, string Reason);

public sealed record MigrationAcceptanceReport(
    string SchemaVersion, string Status, string AnalysisId, string CandidateId,
    string SourceTreeHash, string TargetTreeHash, int SourceObligations,
    int CandidateMatches, int MissingObligations, int ReviewRequired,
    IReadOnlyList<SourceTargetLineage> Lineage,
    VerificationStage Syntax, VerificationStage Compile,
    VerificationStage TestDiscovery, VerificationStage Runner,
    VerificationStage RealBrowser, VerificationStage BehavioralEquivalence,
    IReadOnlyList<string> Blockers);

/// <summary>
/// Fail-closed, read-only source-to-CAPTURED-target inventory. It deliberately
/// cannot emit "accepted": static AST matches, Roslyn compilation and even a
/// green dotnet test runner do not establish source behavior preservation.
/// No user project, MSBuild target, script, browser or NUnit test is executed.
/// </summary>
public static partial class MigrationAcceptanceGate
{
    public const string SchemaVersion = "migrator-acceptance/v1";
    public const string RejectedIncomplete = "rejected_incomplete";
    public const string BlockedUnverified = "blocked_unverified";

    public static MigrationAcceptanceReport Evaluate(
        MigrationAnalysis analysis, MigrationPreview preview,
        IReadOnlyDictionary<string, string> capturedTargetSources,
        VerificationStage syntax, VerificationStage compile,
        string candidateId, string targetTreeHash)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(capturedTargetSources);
        if (analysis.AnalysisId != preview.AnalysisId)
            throw new InvalidOperationException("STALE_ACCEPTANCE_ANALYSIS: analysis and preview differ.");
        if (string.IsNullOrWhiteSpace(candidateId) || string.IsNullOrWhiteSpace(targetTreeHash))
            throw new ArgumentException("CANDIDATE_IDENTITY_REQUIRED");

        var rows = new List<SourceTargetLineage>();
        var blockers = new SortedSet<string>(StringComparer.Ordinal);
        var sourceMethods = 0;
        var sourceTests = 0;
        var filesBySource = preview.Files.ToDictionary(f => f.SourcePath, StringComparer.Ordinal);
        var parsedTargets = new Dictionary<string, SyntaxNode>(StringComparer.Ordinal);
        foreach (var file in preview.Files)
        {
            if (capturedTargetSources.TryGetValue(file.RelativePath, out var text))
                parsedTargets[file.RelativePath] = CSharpSyntaxTree.ParseText(text).GetRoot();
            else
                blockers.Add("CAPTURED_TARGET_FILE_MISSING");
        }

        foreach (var file in analysis.Files.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
        {
            if (file.Disposition != "supported")
            {
                blockers.Add("SOURCE_DISCOVERY_INCOMPLETE");
                continue;
            }

            var path = File.Exists(analysis.SourceScope) ? analysis.SourceScope
                : Path.Combine(analysis.SourceScope,
                    file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                blockers.Add("SOURCE_FILE_UNAVAILABLE");
                continue;
            }

            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot();
            if (root.ContainsDiagnostics)
                blockers.Add("SOURCE_AST_DIAGNOSTICS");

            filesBySource.TryGetValue(file.RelativePath, out var generated);
            var targetPath = generated?.RelativePath;
            var targetRoot = targetPath != null && parsedTargets.TryGetValue(targetPath, out var found)
                ? found : null;
            if (targetRoot == null)
                blockers.Add("SOURCE_HAS_NO_CAPTURED_TARGET");

            foreach (var source in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (!TryPhase(source, out var phase))
                    continue;
                sourceMethods++;
                if (phase == "test")
                    sourceTests++;

                // No symbol/name-only assertion is promoted to a semantic proof.
                var possible = targetRoot?.DescendantNodes().OfType<MethodDeclarationSyntax>()
                    .Where(m => m.Identifier.ValueText == source.Identifier.ValueText
                        && TryPhase(m, out var targetPhase) && targetPhase == phase).ToArray()
                    ?? Array.Empty<MethodDeclarationSyntax>();
                var target = possible.Length == 1 ? possible[0] : null;
                if (possible.Length > 1)
                    blockers.Add("TARGET_METHOD_AMBIGUOUS");

                Append(source, source, "declaration", "method", target,
                    target, file.RelativePath, targetPath, phase, rows);

                var body = (SyntaxNode?)source.Body ?? source.ExpressionBody;
                var targetBody = (SyntaxNode?)target?.Body ?? target?.ExpressionBody;
                if (body == null)
                {
                    blockers.Add("SOURCE_METHOD_BODY_UNAVAILABLE");
                    continue;
                }

                // The complete reachable helper/POM call graph is NOT inferred
                // from method names. Unknown invocations remain review-only.
                var targetCalls = targetBody?.DescendantNodesAndSelf()
                    .OfType<InvocationExpressionSyntax>().ToArray()
                    ?? Array.Empty<InvocationExpressionSyntax>();
                var used = new HashSet<int>();
                foreach (var node in body.DescendantNodesAndSelf().Where(n =>
                    n is InvocationExpressionSyntax or AssignmentExpressionSyntax
                      or PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax))
                {
                    var kind = Classify(node, phase);
                    SyntaxNode? candidate = null;
                    if (node is InvocationExpressionSyntax invocation)
                    {
                        var acceptedNames = TargetNames(CallName(invocation));
                        for (var i = 0; i < targetCalls.Length; i++)
                        {
                            if (!used.Contains(i) && acceptedNames.Contains(CallName(targetCalls[i]),
                                StringComparer.Ordinal))
                            {
                                used.Add(i);
                                candidate = targetCalls[i];
                                break;
                            }
                        }
                    }
                    else
                    {
                        // Assignment and mutation have effects, but cannot be
                        // safely matched by equal counts or similar-looking text.
                        blockers.Add("SOURCE_STATE_MUTATION_REQUIRES_PROOF");
                    }

                    Append(source, node, "operation", kind, target,
                        candidate, file.RelativePath, targetPath, phase, rows);
                }

                // A matching Click/Assert invocation cannot compensate for a deleted
                // guard, catch/throw path or loop. Each source control-flow construct
                // is its own obligation. Matching syntax is only candidate_unverified:
                // conditions, order, reachability and observable effects are NOT proven.
                var targetControlFlow = targetBody?.DescendantNodesAndSelf()
                    .Where(node => ControlFlowKind(node) != null).ToArray()
                    ?? Array.Empty<SyntaxNode>();
                var usedControlFlow = new HashSet<int>();
                foreach (var node in body.DescendantNodesAndSelf())
                {
                    var flowKind = ControlFlowKind(node);
                    if (flowKind == null)
                        continue;
                    SyntaxNode? candidateFlow = null;
                    for (var i = 0; i < targetControlFlow.Length; i++)
                    {
                        if (usedControlFlow.Contains(i) ||
                            ControlFlowKind(targetControlFlow[i]) != flowKind)
                            continue;
                        usedControlFlow.Add(i);
                        candidateFlow = targetControlFlow[i];
                        break;
                    }
                    Append(source, node, "control_flow", flowKind, target,
                        candidateFlow, file.RelativePath, targetPath, phase, rows);
                }
            }
        }

        // Project-wide coverage that name-matched test statements alone miss.
        // The source snapshot is read-only; no project, browser or test is run.
        AppendProjectGraphObligations(analysis, preview, capturedTargetSources,
            rows, blockers);

        if (sourceTests == 0 || sourceTests != analysis.ParsedTests)
            blockers.Add("TEST_DECLARATION_LINEAGE_INCOMPLETE");
        if (sourceMethods == 0 || rows.Count == 0)
            blockers.Add("NO_SOURCE_OBLIGATIONS");
        if (analysis.DiscoveryStatus != "complete" ||
            analysis.BlockedNUnitSources > 0 ||
            preview.TransformationStatus != "complete" ||
            preview.Files.Sum(f => f.Tests) != preview.Files.Sum(f => f.FullyConvertedTests) ||
            preview.Files.Any(f => f.TodoComments > 0) ||
            preview.Findings.Any(f => f.Code is "GENERATION_ERROR" or "GENERATED_TODO" or "UNSUPPORTED_ACTION"))
            blockers.Add("TRANSFORMATION_INCOMPLETE");

        var missing = rows.Count(r => r.Status == "missing_target");
        if (missing > 0)
            blockers.Add("SOURCE_OPERATIONS_MISSING_FROM_TARGET");
        if (syntax.Status != "passed")
            blockers.Add("SYNTAX_NOT_PROVEN");
        if (compile.Status != "passed")
            blockers.Add("COMPILE_NOT_PROVEN");
        blockers.Add("TEST_DISCOVERY_NOT_RUN");
        blockers.Add("NUNIT_RUNNER_NOT_RUN");
        blockers.Add("REAL_BROWSER_NOT_RUN");
        blockers.Add("SOURCE_TARGET_BEHAVIOR_NOT_ATTESTED");
        blockers.Add("AST_NAME_MATCH_IS_NOT_SEMANTIC_EQUIVALENCE");

        var incomplete = missing > 0 ||
            blockers.Contains("SOURCE_DISCOVERY_INCOMPLETE") ||
            blockers.Contains("SOURCE_FILE_UNAVAILABLE") ||
            blockers.Contains("SOURCE_AST_DIAGNOSTICS") ||
            blockers.Contains("SOURCE_HAS_NO_CAPTURED_TARGET") ||
            blockers.Contains("CAPTURED_TARGET_FILE_MISSING") ||
            blockers.Contains("TARGET_METHOD_AMBIGUOUS") ||
            blockers.Contains("SOURCE_METHOD_BODY_UNAVAILABLE") ||
            blockers.Contains("SOURCE_STATE_MUTATION_REQUIRES_PROOF") ||
            blockers.Contains("PROJECT_GRAPH_BUDGET_EXCEEDED") ||
            blockers.Contains("PROJECT_PRIVATE_HELPER_GRAPH_UNRESOLVED") ||
            blockers.Contains("TEST_DECLARATION_LINEAGE_INCOMPLETE") ||
            blockers.Contains("NO_SOURCE_OBLIGATIONS") ||
            blockers.Contains("TRANSFORMATION_INCOMPLETE");
        return new MigrationAcceptanceReport(
            SchemaVersion, incomplete ? RejectedIncomplete : BlockedUnverified,
            analysis.AnalysisId, candidateId, analysis.SourceTreeHash, targetTreeHash,
            rows.Count, rows.Count - missing, missing,
            rows.Count(r => r.Status == "candidate_unverified"),
            rows, syntax, compile, NotRun("nunit-discovery"),
            NotRun("nunit-runner"), NotRun("real-chromium"),
            NotRun("source-target-behavior"), blockers.ToArray());
    }

    private static VerificationStage NotRun(string kind) =>
        new("not_run", kind, Array.Empty<VerificationDiagnostic>());

    private static void Append(
        MethodDeclarationSyntax sourceMethod, SyntaxNode source, string scope,
        string kind, MethodDeclarationSyntax? targetMethod, SyntaxNode? target,
        string sourcePath, string? targetPath, string phase,
        List<SourceTargetLineage> rows)
    {
        var line = source.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var id = Digest(sourcePath + "\n" + sourceMethod.Identifier.ValueText + "\n" +
            phase + "\n" + scope + "\n" + line + "\n" + rows.Count + "\n" + source.ToFullString());
        rows.Add(new SourceTargetLineage(
            id, sourcePath, sourceMethod.Identifier.ValueText, phase, line,
            kind, Digest(source.ToFullString()),
            target == null ? null : targetPath,
            target?.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            target == null ? null : Digest(target.ToFullString()),
            target == null ? "missing_target" : "candidate_unverified",
            target == null ? "No exact candidate operation/method in captured target."
                : "Syntactic correspondence only; effects, operands and behavior remain unverified."));
    }

    private static bool TryPhase(MethodDeclarationSyntax method, out string phase)
    {
        var attributes = method.AttributeLists.SelectMany(l => l.Attributes)
            .Select(a => a.Name.ToString().Split('.').Last())
            .Select(n => n.EndsWith("Attribute", StringComparison.Ordinal) ? n[..^9] : n)
            .ToHashSet(StringComparer.Ordinal);
        phase = attributes.Contains("OneTimeSetUp") ? "one_time_setup"
            : attributes.Contains("OneTimeTearDown") ? "one_time_teardown"
            : attributes.Contains("SetUp") ? "setup"
            : attributes.Contains("TearDown") ? "teardown"
            : attributes.Overlaps(new[] { "Test", "TestCase", "TestCaseSource" }) ? "test" : "";
        return phase.Length > 0;
    }

    private static string CallName(InvocationExpressionSyntax call) => call.Expression switch
    {
        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax m => m.Name.Identifier.ValueText,
        IdentifierNameSyntax i => i.Identifier.ValueText,
        GenericNameSyntax g => g.Identifier.ValueText,
        _ => call.Expression.ToString()
    };

    // These are syntactic obligations, not a claim of equivalent branching.
    // Preserve individual else/catch/finally/throw paths, not just an outer if/try.
    private static string? ControlFlowKind(SyntaxNode node) => node switch
    {
        IfStatementSyntax => "if_guard",
        ElseClauseSyntax => "else_path",
        SwitchStatementSyntax => "switch_dispatch",
        SwitchExpressionSyntax => "switch_expression",
        ConditionalExpressionSyntax => "ternary_guard",
        ConditionalAccessExpressionSyntax => "conditional_access",
        TryStatementSyntax => "try_scope",
        CatchClauseSyntax => "catch_path",
        FinallyClauseSyntax => "finally_path",
        ThrowStatementSyntax => "throw_path",
        ThrowExpressionSyntax => "throw_expression",
        ForStatementSyntax => "for_loop",
        ForEachStatementSyntax => "foreach_loop",
        WhileStatementSyntax => "while_loop",
        DoStatementSyntax => "do_loop",
        BreakStatementSyntax => "break_path",
        ContinueStatementSyntax => "continue_path",
        ReturnStatementSyntax => "return_path",
        _ => null
    };

    private static string Classify(SyntaxNode node, string phase)
    {
        if (node is not InvocationExpressionSyntax call)
            return "state_mutation";
        if (phase != "test")
            return "lifecycle_effect";
        var name = CallName(call);
        if (name is "That" or "AreEqual" or "IsTrue" or "IsFalse" or "Should"
            or "Be" or "Contain" or "Equal" or "Matches" or "Fail")
            return "assertion";
        if (name is "FindElement" or "FindElements" or "Locator")
            return "locator";
        if (name is "Click" or "SendKeys" or "Clear" or "Submit" or "SelectByText"
            or "SelectByValue" or "GoToUrl" or "Refresh" or "ExecuteScript"
            or "SetItem" or "RemoveItem" or "Navigate")
            return "action";
        return "helper_or_effect";
    }

    private static string[] TargetNames(string sourceName) => sourceName switch
    {
        "Click" => new[] { "ClickAsync" },
        "SendKeys" => new[] { "FillAsync", "TypeAsync", "PressSequentiallyAsync" },
        "Clear" => new[] { "ClearAsync", "FillAsync" },
        "FindElement" or "FindElements" => new[] {
            "Locator", "GetByRole", "GetByText", "GetByLabel", "GetByTestId", "GetByPlaceholder" },
        "SelectByText" or "SelectByValue" => new[] { "SelectOptionAsync" },
        "GoToUrl" or "Navigate" => new[] { "GotoAsync" },
        "Refresh" => new[] { "ReloadAsync" },
        "Should" => new[] { "Should", "Expect" },
        _ => new[] { sourceName, sourceName + "Async" }
    };

    private static string Digest(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
