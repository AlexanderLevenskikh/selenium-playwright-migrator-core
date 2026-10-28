namespace Migrator.Core.Models;

/// <summary>
/// An ordered, source-backed NUnit fixture lifecycle phase. Source inheritance
/// levels increase from the oldest base type to the concrete test fixture.
/// Each hook is separate so a renderer can preserve level-sensitive teardown.
/// </summary>
public enum NUnitLifecycleKind
{
    SetUp,
    TearDown,
    OneTimeSetUp,
    OneTimeTearDown
}

public sealed record NUnitLifecycleHook(
    NUnitLifecycleKind Kind,
    string DeclaringType,
    string MethodName,
    string SourcePath,
    int SourceLine,
    int InheritanceLevel,
    IReadOnlyList<TestAction> Actions);

/// <summary>
/// A failure to prove an inherited lifecycle or its NUnit execution order.
/// Missing methods, same-level ambiguous hooks, overrides, and inaccessible
/// implementations must not be silently eliminated.
/// </summary>
public sealed record NUnitLifecycleDiagnostic(
    string Code,
    string SourceType,
    string Description,
    int SourceLine);

/// <summary>
/// Conservative per-fixture source lifecycle ledger from Roslyn symbol identity.
/// Rendering can proceed only when the complete inheritance chain is resolved,
/// one hook of each kind exists per inheritance level, and no overrides hide
/// NUnit methods. Even a complete ledger requires runtime behavior review.
/// </summary>
public sealed record NUnitLifecyclePlan(
    IReadOnlyList<NUnitLifecycleHook> Hooks,
    IReadOnlyList<NUnitLifecycleDiagnostic> Diagnostics,
    int SourceTypeLevels)
{
    public bool CanRenderOrderedHooks => Diagnostics.Count == 0;

    public IEnumerable<NUnitLifecycleHook> Ordered(NUnitLifecycleKind kind) =>
        kind is NUnitLifecycleKind.SetUp or NUnitLifecycleKind.OneTimeSetUp
            ? Hooks.Where(h => h.Kind == kind).OrderBy(h => h.InheritanceLevel)
            : Hooks.Where(h => h.Kind == kind).OrderByDescending(h => h.InheritanceLevel);
}
