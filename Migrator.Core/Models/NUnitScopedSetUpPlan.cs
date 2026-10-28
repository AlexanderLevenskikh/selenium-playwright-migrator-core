namespace Migrator.Core.Models;

/// <summary>
/// One source NUnit SetUpFixture. Its one-time hooks are executed by NUnit
/// once for the original namespace (or once for the assembly at global scope),
/// not copied into each dependent test fixture.
/// </summary>
public sealed record NUnitScopedSetUpFixture(
    string SourceType,
    string SourceFile,
    string SourceNamespace,
    int SourceLine,
    IReadOnlyList<NUnitLifecycleHook> Hooks,
    IReadOnlyList<NUnitLifecycleDiagnostic> Diagnostics);

/// <summary>
/// Separate, source-backed suite scope dependency. Declaration is populated
/// only for the file owning a SetUpFixture. Applicable entries are ordered from
/// outermost assembly scope to innermost namespace for the test fixture.
/// A missing generated scope artifact is a review blocker, never an implicit
/// per-test setup translation.
/// </summary>
public sealed record NUnitScopedSetUpPlan(
    NUnitScopedSetUpFixture? Declaration,
    IReadOnlyList<NUnitScopedSetUpFixture> Applicable,
    IReadOnlyList<NUnitLifecycleDiagnostic> Diagnostics)
{
    public bool HasScope => Declaration != null || Applicable.Count > 0 || Diagnostics.Count > 0;
    public bool CanRenderScope => Diagnostics.Count == 0
        && (Declaration?.Diagnostics.Count ?? 0) == 0;
}
