using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Migrator.Core;
using Migrator.Core.Models;

namespace Migrator.Application;

/// <summary>One source-model obligation; IR projection is not semantic equivalence.</summary>
public sealed record MigrationSourceObligation(
    string Id, string SourcePath, string Owner, int SourceLine, string SourceKind,
    string Projection, string? Reason);

/// <summary>
/// Fail-closed acceptance, independent of syntax, Roslyn emission, NUnit
/// discovery, runner, Chromium and real business/browser observations.
/// No alpha verification stage can label a migration behaviorally accepted.
/// </summary>
public sealed record MigrationAcceptance(
    string SchemaVersion, string Status, string TransformationStatus,
    IReadOnlyList<MigrationSourceObligation> SourceObligations,
    IReadOnlyList<VerificationDiagnostic> MissingTargetOperations,
    VerificationStage TestDiscovery,
    VerificationStage TestRunner,
    VerificationStage RealBrowser,
    VerificationStage BehavioralEquivalence,
    IReadOnlyList<string> Limitations);

public static class MigrationAcceptanceGate
{
    public const string SchemaVersion = "migrator-acceptance/v1";
    public const string RejectedIncomplete = "rejected_incomplete";
    public const string BlockedUnverified = "blocked_unverified_behavior";

    /// <summary>
    /// Projects every source action (including nested and NUnit lifecycle
    /// actions) onto a source-line identity. A missing/reordered IR position
    /// blocks the claim. An aligned model is only provisional evidence.
    /// </summary>
    public static IReadOnlyList<MigrationSourceObligation> SourceLedger(
        IReadOnlyList<PipelineResult> results, string sourceScope)
    {
        var obligations = new List<MigrationSourceObligation>();
        foreach (var result in results.OrderBy(r => r.SourceModel.FilePath, StringComparer.Ordinal))
        {
            var source = result.SourceModel;
            var target = result.TargetModel;
            var relative = Path.GetRelativePath(
                File.Exists(sourceScope) ? Path.GetDirectoryName(sourceScope)! : sourceScope,
                source.FilePath).Replace('\\', '/');
            Add("SetUp", source.SetUpActions, target.SetUpActions);
            foreach (var hook in source.NUnitLifecycle?.Hooks ?? Array.Empty<NUnitLifecycleHook>())
            {
                var adapted = target.NUnitLifecycle?.Hooks.FirstOrDefault(h =>
                    h.Kind == hook.Kind && h.DeclaringType == hook.DeclaringType &&
                    h.MethodName == hook.MethodName && h.SourceLine == hook.SourceLine);
                Add("NUnit:" + hook.Kind + ":" + hook.DeclaringType + "." + hook.MethodName,
                    hook.Actions, adapted?.Actions ?? Array.Empty<TestAction>());
            }
            foreach (var hook in source.ScopedSetUp?.Declaration?.Hooks
                         ?? Array.Empty<NUnitLifecycleHook>())
            {
                var adapted = target.ScopedSetUp?.Declaration?.Hooks.FirstOrDefault(h =>
                    h.Kind == hook.Kind && h.MethodName == hook.MethodName
                    && h.SourceLine == hook.SourceLine);
                Add("SetUpFixture:" + hook.Kind + ":" + hook.MethodName,
                    hook.Actions, adapted?.Actions ?? Array.Empty<TestAction>());
            }
            foreach (var test in source.Tests)
            {
                var adapted = target.Tests.FirstOrDefault(t => t.Name == test.Name);
                Add("Test:" + test.Name, test.BodyActions,
                    adapted?.BodyActions ?? Array.Empty<TestAction>());
            }

            void Add(string owner, IEnumerable<TestAction> sourceActions,
                IEnumerable<TestAction> targetActions)
            {
                var before = TestActionTraversal.Flatten(sourceActions).ToArray();
                var after = TestActionTraversal.Flatten(targetActions).ToArray();
                for (var index = 0; index < before.Length; index++)
                {
                    var src = before[index];
                    var dst = index < after.Length ? after[index] : null;
                    var unsupported = src is UnsupportedAction or RawStatementAction
                        || dst is UnsupportedAction or RawStatementAction;
                    var aligned = dst is not null && src.SourceLine == dst.SourceLine;
                    var projection = !aligned ? "missing_or_reordered_in_ir"
                        : unsupported ? "requires_review"
                        : "projected_ir_only";
                    var reason = !aligned
                        ? "No same-position/same-source-line target-model obligation."
                        : unsupported ? "Unexecutable or unsupported source/target action."
                        : "Model position exists; rendered operation and behavior remain unverified.";
                    var identity = $"{relative}\n{owner}\n{index}\n{src.SourceLine}\n{src.GetType().Name}";
                    obligations.Add(new MigrationSourceObligation(
                        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
                            .ToLowerInvariant(),
                        relative, owner, src.SourceLine, src.GetType().Name,
                        projection, reason));
                }
            }
        }
        return obligations;
    }

    /// <summary>
    /// Agent edits cannot silently delete emitted executable statements.
    /// Even an apparently safe rewrite is unproven and must get an explicit
    /// new source-backed review; a compile/test PASS never overrides loss.
    /// </summary>
    public static IReadOnlyList<VerificationDiagnostic> MissingTargetOperations(
        IReadOnlyList<GeneratedFile> generated,
        IReadOnlyList<(string RelativePath, string Content)> captured)
    {
        var result = new List<VerificationDiagnostic>();
        var actual = captured.ToDictionary(f => f.RelativePath, f => f.Content,
            StringComparer.Ordinal);
        foreach (var file in generated.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
        {
            if (!actual.TryGetValue(file.RelativePath, out var text))
            {
                result.Add(new VerificationDiagnostic("TARGET_FILE_MISSING",
                    file.RelativePath, 0, 0, "Original target file is absent."));
                continue;
            }
            var required = OperationInventory(file.Content);
            var observed = OperationInventory(text);
            foreach (var entry in required)
            {
                var remaining = observed.TryGetValue(entry.Key, out var count) ? count : 0;
                for (var n = remaining; n < entry.Value; n++)
                    result.Add(new VerificationDiagnostic("TARGET_OPERATION_REMOVED",
                        file.RelativePath, 0, 0,
                        "Previously emitted executable operation removed: " + entry.Key.Owner
                        + " / " + entry.Key.Kind + " / sha256=" + entry.Key.Digest));
            }
        }
        return result;
    }

    private readonly record struct OperationKey(string Owner, string Kind, string Digest);

    private static Dictionary<OperationKey, int> OperationInventory(string content)
    {
        var root = CSharpSyntaxTree.ParseText(content).GetRoot();
        var inventory = new Dictionary<OperationKey, int>();
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var type = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            var owner = (type?.Identifier.Text ?? "unknown") + "." + method.Identifier.Text;
            // Executable leaf statements; TODO comments and nested control
            // containers are not counted as preserved operations.
            foreach (var statement in method.DescendantNodes().OfType<StatementSyntax>())
            {
                if (statement is BlockSyntax or IfStatementSyntax or ForStatementSyntax
                    or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax
                    or TryStatementSyntax or SwitchStatementSyntax or UsingStatementSyntax
                    or LockStatementSyntax or FixedStatementSyntax or CheckedStatementSyntax
                    or UnsafeStatementSyntax or LabeledStatementSyntax)
                    continue;
                var normalized = statement.NormalizeWhitespace().ToFullString();
                var digest = Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
                var key = new OperationKey(owner, statement.Kind().ToString(), digest);
                inventory.TryGetValue(key, out var existing);
                inventory[key] = existing + 1;
            }
        }
        return inventory;
    }

    public static MigrationAcceptance Evaluate(
        MigrationPreview preview, IReadOnlyList<(string RelativePath, string Content)> captured)
    {
        var missing = MissingTargetOperations(preview.Files, captured);
        var unresolved = preview.TransformationStatus != "complete"
            || preview.SourceObligations.Any(o => o.Projection != "projected_ir_only")
            || missing.Count > 0;
        var notRun = new VerificationStage("not_run", "not_executed",
            Array.Empty<VerificationDiagnostic>());
        return new MigrationAcceptance(
            SchemaVersion, unresolved ? RejectedIncomplete : BlockedUnverified,
            preview.TransformationStatus, preview.SourceObligations, missing,
            notRun, notRun, notRun, notRun,
            new[]
            {
                "Matching source/target IR is not an action/assertion semantic proof.",
                "Byte-exact preview operations guard deletions, not source equivalence.",
                "No source-to-target behavior equivalence is verified by this alpha API.",
                "NUnit discovery, runner, Chromium and application observables require independently bound evidence."
            });
    }
}
