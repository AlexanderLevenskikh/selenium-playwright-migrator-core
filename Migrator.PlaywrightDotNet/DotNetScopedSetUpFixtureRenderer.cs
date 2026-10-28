using System.Text;
using System.Text.RegularExpressions;
using Migrator.Core.Models;

namespace Migrator.PlaywrightDotNet;

public partial class PlaywrightDotNetRenderer
{
    /// <summary>
    /// Standalone source SetUpFixture -> standalone target SetUpFixture.
    /// Never embed it inside the generated NUnit test fixture: the original
    /// hooks run once per namespace or once per assembly, not once per test
    /// class. The original namespace is preserved byte-for-byte as a name.
    /// </summary>
    string RenderScopedSetUpFixture(TestFileModel model, NUnitScopedSetUpPlan scope)
    {
        if (_targetTestFramework != DotNetTargetTestFramework.NUnit)
            throw new InvalidOperationException(
                "NUNIT_SETUPFIXTURE_FRAMEWORK_MISMATCH: scoped NUnit hooks cannot be converted to xUnit without an explicit suite lifecycle design.");
        if (!string.IsNullOrWhiteSpace(model.TestHost?.Namespace)
            && !string.Equals(model.TestHost.Namespace, model.Namespace, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "NUNIT_SETUPFIXTURE_NAMESPACE_CHANGED: configured target namespace would break source scoped setup boundaries.");

        var declaration = scope.Declaration
            ?? throw new InvalidOperationException("NUNIT_SETUPFIXTURE_DECLARATION_REQUIRED");
        var sb = new StringBuilder();
        sb.AppendLine("// Source-backed standalone NUnit SetUpFixture; never duplicate inside individual tests.");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine("using NUnit.Framework;");
        foreach (var us in model.TestHost?.Usings ?? Array.Empty<string>())
        {
            if (us is not ("System" or "System.Threading.Tasks" or "NUnit.Framework"))
                sb.AppendLine($"using {us};");
        }
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(declaration.SourceNamespace))
        {
            sb.AppendLine($"namespace {declaration.SourceNamespace};");
            sb.AppendLine();
        }

        sb.AppendLine("[SetUpFixture]");
        sb.AppendLine($"public class {model.ClassName}Playwright");
        sb.AppendLine("{");

        var problems = scope.Diagnostics;
        // Preflight both setup AND teardown before executing any scope
        // effects. An unmapped teardown must prevent the suite from running,
        // not merely report inconclusive after its tests have passed.
        var unsafeActions = declaration.Hooks
            .SelectMany(hook => hook.Actions)
            .Where(action => action is not MappedMethodInvocationAction mapped
                || mapped.GetTargetStatements("playwright-dotnet").Count == 0
                || mapped.GetTargetStatements("playwright-dotnet").Any(statement =>
                    Regex.IsMatch(statement, @"\bPage\s*\.",
                        RegexOptions.CultureInvariant)
                    || statement.Contains("{TARGET}", StringComparison.Ordinal)
                    || statement.Contains("{result}", StringComparison.Ordinal)
                    || statement.Contains("TODO", StringComparison.OrdinalIgnoreCase)
                    || statement.Contains("MIGRATOR:", StringComparison.Ordinal)))
            .ToArray();

        if (problems.Count > 0 || unsafeActions.Length > 0)
        {
            sb.AppendLine($"{_indent}[OneTimeSetUp]");
            sb.AppendLine($"{_indent}public Task __MigratorBlockedScopedSetupAsync()");
            sb.AppendLine($"{_indent}{{");
            foreach (var issue in problems)
                AppendSmartTodo(sb, $"scoped NUnit source needs review: {EscapeComment(issue.SourceType)}",
                    issue.Code, issue.Description,
                    "Resolve source scope/ordering before executing the generated assembly.", null);
            foreach (var action in unsafeActions)
                AppendSmartTodo(sb,
                    $"scope hook action lacks standalone target evidence: {EscapeComment(GetActionSourceText(action))}",
                    "NUNIT_SETUPFIXTURE_ACTION_REQUIRES_MAPPING",
                    "Namespace/assembly setup executes outside the individual Playwright PageTest context.",
                    "Supply explicit independent mappings for every source OneTimeSetUp and OneTimeTearDown action.");

            foreach (var hook in declaration.Hooks)
            {
                sb.AppendLine($"{_indent}{_indent}// Unexecuted source {hook.Kind}: {EscapeComment(hook.DeclaringType)}.{EscapeComment(hook.MethodName)}");
                foreach (var action in hook.Actions)
                    RenderActionAsComment(sb, action);
            }
            sb.AppendLine($"{_indent}{_indent}Assert.Inconclusive(\"MIGRATOR: NUnit SetUpFixture source scope not proven.\");");
            sb.AppendLine($"{_indent}{_indent}return Task.CompletedTask;");
            sb.AppendLine($"{_indent}}}");
        }
        else
        {
            foreach (var kind in new[] { NUnitLifecycleKind.OneTimeSetUp,
                                         NUnitLifecycleKind.OneTimeTearDown })
            {
                var hooks = declaration.Hooks.Where(hook => hook.Kind == kind)
                    .OrderBy(hook => kind == NUnitLifecycleKind.OneTimeSetUp
                        ? hook.InheritanceLevel : -hook.InheritanceLevel)
                    .ToArray();
                if (hooks.Length == 0)
                    continue;
                ResetMethodScope();
                sb.AppendLine($"{_indent}[{kind}]");
                sb.AppendLine($"{_indent}public async Task __MigratorScoped{kind}Async()");
                sb.AppendLine($"{_indent}{{");
                sb.AppendLine($"{_indent}{_indent}// TODO: verify source SetUpFixture and once-per-namespace/assembly semantics [MIGRATOR:NUNIT_SETUPFIXTURE_SCOPE_REQUIRES_REVIEW]");
                sb.AppendLine($"{_indent}{_indent}//   No per-test duplication; NUnit itself owns this one-time scope.");
                foreach (var hook in hooks)
                {
                    sb.AppendLine($"{_indent}{_indent}// Source {EscapeComment(hook.DeclaringType)}.{EscapeComment(hook.MethodName)}");
                    foreach (var action in hook.Actions)
                        RenderActionWithSafety(sb, action);
                }
                sb.AppendLine($"{_indent}}}");
                sb.AppendLine();
            }
        }
        sb.AppendLine("}");
        return sb.ToString();
    }
}
