using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Migrator.Application;

/// <summary>
/// Whitelisted, source-backed NUnit assembly scheduling policy. Never infers
/// test parallelism or copies arbitrary assembly attributes. The result is
/// syntax-level evidence only; runtime scheduling still needs verification.
/// </summary>
internal sealed record NUnitAssemblyScheduleAttribute(
    string SourcePath,
    int SourceLine,
    string SourceExpression,
    string TargetExpression);

internal sealed record NUnitAssemblySchedulingResult(
    IReadOnlyList<NUnitAssemblyScheduleAttribute> Attributes,
    IReadOnlyList<TargetAssemblyIssue> Issues)
{
    public const string GeneratedFileName = "Migrator.Generated.NUnitAssemblyScheduling.cs";
    public bool CanEmit => Issues.All(issue => issue.Severity != "error");
    public string? RenderTarget()
    {
        if (!CanEmit || Attributes.Count == 0) return null;
        var lines = new List<string>
        {
            "// Source-backed NUnit assembly attributes. Verify actual scheduling in NUnit runtime.",
            "// Do not move these attributes onto a fixture or per-test method."
        };
        lines.AddRange(Attributes.Select(attribute =>
            "[assembly: " + attribute.TargetExpression + "]"));
        return string.Join("\n", lines) + "\n";
    }
}

internal static class NUnitAssemblySchedulingInspector
{
    private static readonly HashSet<string> SchedulingNames = new(
        new[] { "Parallelizable", "NonParallelizable", "LevelOfParallelism" },
        StringComparer.Ordinal);

    public static NUnitAssemblySchedulingResult Inspect(
        IEnumerable<(string SourcePath, SyntaxNode Root)> sourceTrees)
    {
        var sources = sourceTrees.OrderBy(item => item.SourcePath,
            StringComparer.Ordinal).ToArray();
        var output = new List<NUnitAssemblyScheduleAttribute>();
        var issues = new List<TargetAssemblyIssue>();

        foreach (var (sourcePath, node) in sources)
        {
            if (node is not CompilationUnitSyntax root) continue;
            var hasNUnitUsing = root.Usings.Any(usingDirective =>
                usingDirective.Alias == null &&
                usingDirective.Name?.ToString() == "NUnit.Framework");
            var aliases = root.Usings.Where(usingDirective =>
                    usingDirective.Alias != null &&
                    usingDirective.Name?.ToString()
                        .Contains("NUnit.Framework.", StringComparison.Ordinal) == true)
                .ToDictionary(usingDirective =>
                    usingDirective.Alias!.Name.Identifier.ValueText,
                    usingDirective => usingDirective.Name!.ToString(),
                    StringComparer.Ordinal);

            var shadowing = sources.SelectMany(source => source.Root
                    .DescendantNodes().OfType<TypeDeclarationSyntax>())
                .Select(type => type.Identifier.ValueText)
                .Where(name => name is "ParallelScope" or "Parallelizable"
                    or "ParallelizableAttribute" or "NonParallelizable"
                    or "NonParallelizableAttribute" or "LevelOfParallelism"
                    or "LevelOfParallelismAttribute")
                .ToHashSet(StringComparer.Ordinal);

            void Report(string code, string explanation, int line) =>
                issues.Add(new TargetAssemblyIssue(code, "error",
                    explanation + " (source line " + line + ")", sourcePath));

            foreach (var attrList in root.AttributeLists)
            {
                var target = attrList.Target?.Identifier.ValueText;
                foreach (var attribute in attrList.Attributes)
                {
                    var raw = attribute.Name.ToString();
                    var normalized = SimpleName(raw);
                    var alias = aliases.TryGetValue(raw, out var aliasTarget)
                        && SchedulingNames.Contains(SimpleName(aliasTarget));
                    if (!SchedulingNames.Contains(normalized) && !alias)
                        continue;

                    var line = SourceLine(attribute);
                    if (target != "assembly")
                    {
                        Report("NUNIT_ASSEMBLY_SCHEDULING_SCOPE_UNSUPPORTED",
                            "A NUnit scheduling attribute is not assembly-scoped.",
                            line);
                        continue;
                    }
                    if (alias)
                    {
                        Report("NUNIT_ASSEMBLY_SCHEDULING_ALIAS_UNRESOLVED",
                            "An aliased scheduling attribute cannot be source-attested.",
                            line);
                        continue;
                    }
                    var qualified = raw.StartsWith("NUnit.Framework.",
                        StringComparison.Ordinal)
                        || raw.StartsWith("global::NUnit.Framework.",
                            StringComparison.Ordinal);
                    if (!qualified && !hasNUnitUsing)
                    {
                        Report("NUNIT_ASSEMBLY_SCHEDULING_TYPE_UNRESOLVED",
                            "An unqualified NUnit scheduling attribute has no explicit NUnit import.",
                            line);
                        continue;
                    }
                    if (shadowing.Count > 0)
                    {
                        Report("NUNIT_ASSEMBLY_SCHEDULING_TYPE_AMBIGUOUS",
                            "A source type shadows a scheduling attribute or ParallelScope.",
                            line);
                        continue;
                    }

                    var arguments = attribute.ArgumentList?.Arguments;
                    if (arguments is { } candidate &&
                        candidate.Any(argument => argument.NameEquals != null
                            || argument.NameColon != null))
                    {
                        Report("NUNIT_ASSEMBLY_SCHEDULING_ARGUMENT_UNSUPPORTED",
                            "Named scheduling arguments are not automatically proven.",
                            line);
                        continue;
                    }

                    var count = arguments?.Count ?? 0;
                    string? emitted = null;
                    if (normalized == "NonParallelizable" && count == 0)
                        emitted = "NUnit.Framework.NonParallelizable";
                    else if (normalized == "Parallelizable")
                    {
                        if (count == 0)
                            emitted = "NUnit.Framework.Parallelizable";
                        else if (count == 1)
                        {
                            var value = arguments![0].Expression.ToString()
                                .Replace("global::", "", StringComparison.Ordinal);
                            // Whitelist direct, literal enum access only. No
                            // combined flags, casts, computed values or aliases.
                            if (value.StartsWith("NUnit.Framework.",
                                    StringComparison.Ordinal))
                                value = value["NUnit.Framework.".Length..];
                            if (value is "ParallelScope.Self"
                                or "ParallelScope.Children"
                                or "ParallelScope.Fixtures"
                                or "ParallelScope.All"
                                or "ParallelScope.None")
                                emitted = "NUnit.Framework.Parallelizable("
                                    + "NUnit.Framework." + value + ")";
                        }
                    }
                    else if (normalized == "LevelOfParallelism" && count == 1
                        && arguments![0].Expression is LiteralExpressionSyntax literal
                        && literal.IsKind(SyntaxKind.NumericLiteralExpression)
                        && literal.Token.Value is int level
                        && level is >= 1 and <= 256)
                        emitted = "NUnit.Framework.LevelOfParallelism("
                            + level + ")";

                    if (emitted == null)
                    {
                        Report("NUNIT_ASSEMBLY_SCHEDULING_ARGUMENT_UNSUPPORTED",
                            "Only literal ParallelScope values and positive "
                            + "integer LevelOfParallelism are supported.",
                            line);
                        continue;
                    }
                    output.Add(new NUnitAssemblyScheduleAttribute(sourcePath,
                        line, attribute.ToString(), emitted));
                }
            }

            // Fixture/method scheduling is not an assembly policy: dropping
            // it could change test concurrency despite a valid target build.
            foreach (var type in root.DescendantNodes()
                .OfType<TypeDeclarationSyntax>())
                foreach (var attr in type.AttributeLists.SelectMany(
                    list => list.Attributes))
                    if (SchedulingNames.Contains(SimpleName(attr.Name.ToString())))
                        Report("NUNIT_FIXTURE_SCHEDULING_REQUIRES_REVIEW",
                            "Fixture-level NUnit scheduling cannot be silently lost.",
                            SourceLine(attr));
            foreach (var method in root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>())
                foreach (var attr in method.AttributeLists.SelectMany(
                    list => list.Attributes))
                    if (SchedulingNames.Contains(SimpleName(attr.Name.ToString())))
                        Report("NUNIT_TEST_SCHEDULING_REQUIRES_REVIEW",
                            "Test-level NUnit scheduling cannot be silently lost.",
                            SourceLine(attr));
        }

        var kinds = output.Select(attribute =>
            SimpleName(attribute.TargetExpression
                .Split('(')[0])).ToArray();
        foreach (var kind in SchedulingNames)
            if (kinds.Count(item => item == kind) > 1)
                issues.Add(new TargetAssemblyIssue(
                    "NUNIT_ASSEMBLY_SCHEDULING_DUPLICATE", "error",
                    "Multiple assembly-level " + kind
                    + " declarations have ambiguous precedence."));
        if (kinds.Contains("NonParallelizable")
            && (kinds.Contains("Parallelizable")
                || kinds.Contains("LevelOfParallelism")))
            issues.Add(new TargetAssemblyIssue(
                "NUNIT_ASSEMBLY_SCHEDULING_CONFLICT", "error",
                "Contradictory parallel and nonparallel assembly policies are not copied."));

        return new NUnitAssemblySchedulingResult(output
            .OrderBy(item => item.SourcePath, StringComparer.Ordinal)
            .ThenBy(item => item.SourceLine)
            .ThenBy(item => item.TargetExpression, StringComparer.Ordinal)
            .ToArray(), issues);
    }

    private static int SourceLine(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static string SimpleName(string raw)
    {
        var name = raw.Replace("global::", "", StringComparison.Ordinal)
            .Split('.').Last();
        return name.EndsWith("Attribute", StringComparison.Ordinal)
            ? name[..^"Attribute".Length] : name;
    }
}
