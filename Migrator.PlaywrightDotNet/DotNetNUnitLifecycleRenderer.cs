using System.Text;
using Migrator.Core.Models;

namespace Migrator.PlaywrightDotNet;

public partial class PlaywrightDotNetRenderer
{
    /// <summary>
    /// A single generated NUnit hook of each kind is essential: multiple
    /// [SetUp] methods on the same generated class have undefined mutual order.
    /// Each hook body is an explicit, source-level ordered chain. For cleanup,
    /// try/finally preserves the reverse chain even if a derived cleanup fails.
    /// Reached-level flags stop cleanup of levels not entered after setup failed.
    /// Exact source exception paths remain marked review-required.
    /// </summary>
    void RenderNUnitLifecycle(StringBuilder sb, NUnitLifecyclePlan plan, bool hasHostSetUp)
    {
        if (_targetTestFramework != DotNetTargetTestFramework.NUnit
            || hasHostSetUp || !plan.CanRenderOrderedHooks)
        {
            RenderBlockedNUnitLifecycle(sb, plan, hasHostSetUp);
            return;
        }

        sb.AppendLine($"{_indent}private int __migratorPerTestReached = -1;");
        sb.AppendLine($"{_indent}private int __migratorOneTimeReached = -1;");
        sb.AppendLine();

        // OneTimeSetUp and per-test SetUp are deliberately separate scopes.
        // A Page Object local created in one may not be used in the other.
        var oneTime = plan.Ordered(NUnitLifecycleKind.OneTimeSetUp).ToArray();
        if (oneTime.Length > 0)
        {
            ResetMethodScope();
            RenderLifecycleSetUp(sb, oneTime, NUnitLifecycleKind.OneTimeSetUp,
                "__migratorOneTimeReached");
            sb.AppendLine();
            foreach (var symbol in _blockedSymbols)
                _setupBlockedSymbols.Add(symbol);
            _setupActiveNavigationResults.Clear();
            _fixturePageAliases.Clear();
        }

        var perTest = plan.Ordered(NUnitLifecycleKind.SetUp).ToArray();
        if (perTest.Length > 0)
        {
            ResetMethodScope();
            foreach (var symbol in _setupBlockedSymbols)
                _blockedSymbols.Add(symbol);
            RenderLifecycleSetUp(sb, perTest, NUnitLifecycleKind.SetUp,
                "__migratorPerTestReached");
            sb.AppendLine();
            foreach (var symbol in _blockedSymbols)
                _setupBlockedSymbols.Add(symbol);
        }

        foreach (var kind in new[] { NUnitLifecycleKind.TearDown,
                                     NUnitLifecycleKind.OneTimeTearDown })
        {
            var hooks = plan.Ordered(kind).ToArray();
            if (hooks.Length == 0) continue;

            ResetMethodScope();
            var reached = kind == NUnitLifecycleKind.TearDown
                ? "__migratorPerTestReached" : "__migratorOneTimeReached";
            sb.AppendLine($"{_indent}[{kind}]");
            sb.AppendLine($"{_indent}public async Task __Migrator{kind}Async()");
            sb.AppendLine($"{_indent}{{");
            RenderLifecycleCleanup(sb, hooks, reached, 0);
            sb.AppendLine($"{_indent}}}");
            sb.AppendLine();
        }

        // This is a necessary ordered source-action translation, not proof that
        // all NUnit setup-failure and browser lifetime observable paths match.
        // Placed before test declarations so rendered TODO attribution is shared
        // across every generated test in this fixture.
        sb.AppendLine($"{_indent}// TODO: inherited NUnit hook failure and fixture state semantics require review [MIGRATOR:NUNIT_LIFECYCLE_REQUIRES_REVIEW]");
        sb.AppendLine($"{_indent}//   Verify runtime cleanup on partial setup failure, external SetUpFixture scope, inheritance and state lifetime.");
        sb.AppendLine();
    }

    void RenderLifecycleSetUp(
        StringBuilder sb, NUnitLifecycleHook[] hooks,
        NUnitLifecycleKind kind, string reachedField)
    {
        var methodName = kind == NUnitLifecycleKind.SetUp
            ? "SetUp" : "__MigratorOneTimeSetUpAsync";
        sb.AppendLine($"{_indent}[{kind}]");
        sb.AppendLine($"{_indent}public async Task {methodName}()");
        sb.AppendLine($"{_indent}{{");
        sb.AppendLine($"{_indent}{_indent}{reachedField} = -1;");
        _renderingSetUp = kind == NUnitLifecycleKind.SetUp;
        try
        {
            foreach (var hook in hooks)
            {
                sb.AppendLine($"{_indent}{_indent}// Source {hook.Kind}: {EscapeComment(hook.DeclaringType)}.{EscapeComment(hook.MethodName)} (inheritance level {hook.InheritanceLevel})");
                sb.AppendLine($"{_indent}{_indent}{reachedField} = {hook.InheritanceLevel};");
                foreach (var action in hook.Actions)
                    RenderActionWithSafety(sb, action);
            }
        }
        finally
        {
            _renderingSetUp = false;
        }
        sb.AppendLine($"{_indent}}}");
    }

    void RenderLifecycleCleanup(
        StringBuilder sb, NUnitLifecycleHook[] hooks,
        string reachedField, int index)
    {
        if (index >= hooks.Length) return;
        var hook = hooks[index];

        if (index < hooks.Length - 1)
        {
            sb.AppendLine($"{_indent}{_indent}try");
            sb.AppendLine($"{_indent}{_indent}{{");
        }

        sb.AppendLine($"{_indent}{_indent}if ({reachedField} >= {hook.InheritanceLevel})");
        sb.AppendLine($"{_indent}{_indent}{{");
        sb.AppendLine($"{_indent}{_indent}    // Source {hook.Kind}: {EscapeComment(hook.DeclaringType)}.{EscapeComment(hook.MethodName)}");
        foreach (var action in hook.Actions)
            RenderActionWithSafety(sb, action);
        sb.AppendLine($"{_indent}{_indent}}}");

        if (index < hooks.Length - 1)
        {
            sb.AppendLine($"{_indent}{_indent}}}");
            sb.AppendLine($"{_indent}{_indent}finally");
            sb.AppendLine($"{_indent}{_indent}{{");
            RenderLifecycleCleanup(sb, hooks, reachedField, index + 1);
            sb.AppendLine($"{_indent}{_indent}}}");
        }
    }

    void RenderBlockedNUnitLifecycle(
        StringBuilder sb, NUnitLifecyclePlan plan, bool hasHostSetUp)
    {
        // Fail closed at the fixture boundary. Comments alone can leave empty,
        // accidentally green NUnit tests; an inconclusive SetUp makes the
        // unresolved inherited lifecycle impossible to overlook at runtime.
        if (_targetTestFramework == DotNetTargetTestFramework.NUnit)
        {
            sb.AppendLine($"{_indent}[SetUp]");
            sb.AppendLine($"{_indent}public Task SetUp()");
            sb.AppendLine($"{_indent}{{");
        }
        else
        {
            sb.AppendLine($"{_indent}public Task InitializeAsync()");
            sb.AppendLine($"{_indent}{{");
        }

        if (hasHostSetUp)
            AppendSmartTodo(sb, "host SetUpStatements conflict with inherited NUnit hooks",
                "NUNIT_LIFECYCLE_HOST_CONFLICT",
                "Host setup replaces the original source fixture pipeline.",
                "Reconcile source and host lifecycle in one reviewed target test host.");
        if (_targetTestFramework != DotNetTargetTestFramework.NUnit)
            AppendSmartTodo(sb, "NUnit lifecycle cannot be flattened into xUnit without proof",
                "NUNIT_LIFECYCLE_FRAMEWORK_MISMATCH",
                "The xUnit initialization and class lifetime protocol differs from NUnit.",
                "Use an explicit target lifecycle adapter or retain an NUnit test host.");

        foreach (var diagnostic in plan.Diagnostics)
        {
            AppendSmartTodo(sb,
                $"unresolved inherited NUnit lifecycle: {EscapeComment(diagnostic.SourceType)}",
                diagnostic.Code, diagnostic.Description,
                "Resolve the exact source type and hook order; avoid guessing inherited or overridden behavior.");
        }

        foreach (var hook in plan.Hooks)
        {
            sb.AppendLine($"{_indent}{_indent}// Source lifecycle (NOT executed): {EscapeComment(hook.Kind.ToString())} {EscapeComment(hook.DeclaringType)}.{EscapeComment(hook.MethodName)}");
            foreach (var action in hook.Actions)
                RenderActionAsComment(sb, action);
        }

        if (_targetTestFramework == DotNetTargetTestFramework.NUnit)
        {
            sb.AppendLine($"{_indent}{_indent}Assert.Inconclusive(\"MIGRATOR: inherited NUnit lifecycle requires source-backed review.\");");
            sb.AppendLine($"{_indent}{_indent}return Task.CompletedTask;");
        }
        else
            sb.AppendLine($"{_indent}{_indent}throw new NotSupportedException(\"MIGRATOR: NUnit lifecycle cannot be automatically migrated to xUnit.\");");
        sb.AppendLine($"{_indent}}}");
        sb.AppendLine();

        foreach (var hook in plan.Hooks)
        {
            // Target actions are not active, so source aliases may not become
            // safe merely because their source method was discovered.
            foreach (var action in hook.Actions)
            {
                var source = GetActionSourceText(action);
                foreach (var symbol in ExtractVariableNames(source))
                    _setupBlockedSymbols.Add(symbol);
            }
        }
    }
}
