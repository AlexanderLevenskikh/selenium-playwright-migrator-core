using System.Text.Json;
using System.Xml.Linq;

namespace Migrator.Application;

/// <summary>
/// An explicit migration scope consists of NUnit entry projects and the
/// complete transitive closure of literal ProjectReference edges. Omission
/// is reported: it can never silently remove a reachable source dependency.
/// Reads only SDK project XML, never evaluates or executes MSBuild.
/// </summary>
internal static class TargetSolutionEntryScope
{
    internal sealed record Selection(
        string[] Included,
        string[] Entries,
        string[] Excluded,
        bool Explicit);

    internal static Selection Resolve(string root, string[] discoveredProjects,
        string? configPath, List<TargetAssemblyIssue> issues,
        CancellationToken cancellationToken)
    {
        var names = discoveredProjects.ToDictionary(
            path => Path.GetRelativePath(root, path).Replace('\\', '/'),
            path => path, StringComparer.Ordinal);
        string[] entries;
        try
        {
            if (configPath == null)
                return All();
            using var json = JsonDocument.Parse(File.ReadAllText(configPath));
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                throw new FormatException("The adapter root must be a JSON object.");
            if (!json.RootElement.TryGetProperty(
                "TargetSolutionEntryProjects", out var requested))
                return All();
            if (requested.ValueKind != JsonValueKind.Array)
                throw new FormatException("TargetSolutionEntryProjects must be an array.");
            var parsed = new List<string>();
            foreach (var entry in requested.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String)
                    throw new FormatException("Entry projects must be literal paths.");
                var name = entry.GetString()!;
                if (!ValidRelativeProject(name))
                    throw new FormatException("An entry path is not normalized or safe.");
                if (parsed.Contains(name, StringComparer.Ordinal))
                    throw new FormatException("Duplicate project entry.");
                parsed.Add(name);
            }
            if (parsed.Count == 0)
                throw new FormatException("An explicit entry-project list cannot be empty.");
            entries = parsed.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        }
        catch (Exception ex) when (ex is FormatException or IOException
            or UnauthorizedAccessException or JsonException)
        {
            issues.Add(new TargetAssemblyIssue("SOLUTION_SCOPE_CONFIG_INVALID",
                "error", "Cannot parse explicit project entries: "
                    + ex.GetType().Name));
            return new Selection(Array.Empty<string>(), Array.Empty<string>(),
                names.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray(), true);
        }

        var included = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(entries.Reverse());
        foreach (var entry in entries)
            if (!names.ContainsKey(entry))
                issues.Add(new TargetAssemblyIssue("SOLUTION_SCOPE_ENTRY_MISSING",
                    "error", "Selected project does not exist within the source graph.",
                    entry));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = pending.Pop();
            if (!names.TryGetValue(name, out var path) || !included.Add(name))
                continue;
            XDocument document;
            try { document = XDocument.Load(path); }
            catch (Exception ex) when (ex is System.Xml.XmlException or IOException
                or UnauthorizedAccessException)
            {
                issues.Add(new TargetAssemblyIssue("SOLUTION_SCOPE_PROJECT_XML_INVALID",
                    "error", "A selected project cannot be parsed: "
                        + ex.GetType().Name, name));
                continue;
            }

            var projectDir = Path.GetDirectoryName(path)!;
            foreach (var node in document.Descendants().Where(node =>
                node.Name.LocalName == "ProjectReference"))
            {
                var include = node.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include)
                    || include.IndexOfAny(new[] { '$', '*', '?', ';' }) >= 0
                    || node.HasElements || node.Attributes().Any(attr =>
                        attr.Name.LocalName != "Include"))
                {
                    issues.Add(new TargetAssemblyIssue("SOLUTION_SCOPE_REFERENCE_DYNAMIC",
                        "error", "A selected project uses a dynamic or conditional "
                        + "ProjectReference; closure cannot be proven.", name));
                    continue;
                }
                string absolute;
                try
                {
                    absolute = Path.GetFullPath(Path.Combine(projectDir,
                        include.Replace('\\', Path.DirectorySeparatorChar)
                            .Replace('/', Path.DirectorySeparatorChar)));
                }
                catch (Exception ex) when (ex is ArgumentException or
                    NotSupportedException or PathTooLongException)
                {
                    issues.Add(new TargetAssemblyIssue("SOLUTION_SCOPE_REFERENCE_INVALID",
                        "error", "An invalid reference path prevents dependency closure.",
                        name));
                    continue;
                }
                var relative = Path.GetRelativePath(root, absolute)
                    .Replace('\\', '/');
                if (!names.ContainsKey(relative)
                    || !string.Equals(names[relative], absolute,
                        StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new TargetAssemblyIssue(
                        "SOLUTION_SCOPE_REFERENCE_UNAVAILABLE", "error",
                        "Selected source project depends on a missing, excluded "
                        + "by discovery, or out-of-root project.", name));
                    continue;
                }
                pending.Push(relative);
            }
        }

        var excluded = names.Keys.Where(name => !included.Contains(name))
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (excluded.Length > 0)
            issues.Add(new TargetAssemblyIssue(
                "SOLUTION_SCOPE_EXCLUDED_PROJECTS", "warning",
                "An explicit scope omitted " + excluded.Length
                + " unrelated source project(s). Their tests and SetUpFixtures "
                + "are NOT migrated or counted in the target plan."));

        return new Selection(included.OrderBy(x => x, StringComparer.Ordinal)
            .Select(name => names[name]).ToArray(), entries, excluded, true);

        Selection All() => new(discoveredProjects, Array.Empty<string>(),
            Array.Empty<string>(), false);
    }

    private static bool ValidRelativeProject(string name) =>
        name.Length > 0 && name.Length <= 1024
        && !name.StartsWith("/", StringComparison.Ordinal)
        && !name.Contains('\\') && !name.Contains(':')
        && name.EndsWith(".csproj", StringComparison.Ordinal)
        && name.Split('/').All(part =>
            part.Length > 0 && part is not ("." or ".."));
}
