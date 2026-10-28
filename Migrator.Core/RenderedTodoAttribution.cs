using System.Text.RegularExpressions;

namespace Migrator.Core;

/// <summary>
/// Conservative attribution of renderer-produced TODOs to generated test methods.
/// An intermediate IR can be semantically plausible while the actual renderer
/// comments out actions due to unavailable symbols. We never grant "fully converted"
/// on model evidence alone when the rendered target contains unresolved debt.
/// </summary>
internal static class RenderedTodoAttribution
{
    private static readonly Regex MethodHeader = new(
        @"^\s*public\s+(?:async\s+)?(?:Task(?:<[^>]*>)?|void)\s+(?<name>@?[A-Za-z_]\w*)\s*\(",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly string[] AdditionalRiskMarkers =
    {
        "[MIGRATOR:NAVIGATION_FALLBACK_DECLARATION]",
        "[MIGRATOR:CLASS_MEMBER_REQUIRES_REVIEW]",
        "[MIGRATOR:SOURCE_ONLY_IDENTIFIER]"
    };

    public sealed record Result(bool SharedDebt, IReadOnlySet<string> AffectedTests);

    public static Result Analyze(string generatedOutput, IEnumerable<string> expectedTestNames)
    {
        ArgumentNullException.ThrowIfNull(generatedOutput);
        ArgumentNullException.ThrowIfNull(expectedTestNames);

        var expected = expectedTestNames.ToHashSet(StringComparer.Ordinal);
        var lines = generatedOutput.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n').Split('\n');
        var ownerByLine = new string?[lines.Length];
        var discovered = new HashSet<string>(StringComparer.Ordinal);
        var structuralUncertainty = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var header = MethodHeader.Match(lines[i]);
            if (!header.Success)
                continue;

            var method = header.Groups["name"].Value.TrimStart('@');
            if (!expected.Contains(method))
                continue;

            if (!discovered.Add(method))
            {
                structuralUncertainty = true;
                continue;
            }

            var indent = lines[i][..(lines[i].Length - lines[i].TrimStart().Length)];
            var open = i + 1;
            while (open < lines.Length && string.IsNullOrWhiteSpace(lines[open]))
                open++;
            if (open >= lines.Length || lines[open].Trim() != "{")
            {
                structuralUncertainty = true;
                continue;
            }

            var close = open + 1;
            // The .NET renderer uses one extra indentation level for statements,
            // and a method-level closing brace on its own line. If the shape
            // changes, mark the report incomplete rather than guessing ownership.
            while (close < lines.Length && lines[close] != indent + "}")
                close++;
            if (close >= lines.Length)
            {
                structuralUncertainty = true;
                continue;
            }

            for (var line = i; line <= close; line++)
                ownerByLine[line] = method;
            i = close;
        }

        var sharedDebt = structuralUncertainty || discovered.Count != expected.Count;
        var affected = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimStart();
            if (!line.StartsWith("// TODO:", StringComparison.Ordinal)
                && !AdditionalRiskMarkers.Any(marker => line.Contains(marker, StringComparison.Ordinal)))
                continue;

            if (ownerByLine[i] is string testName)
                affected.Add(testName);
            else
                sharedDebt = true;
        }

        return new Result(sharedDebt, affected);
    }
}
