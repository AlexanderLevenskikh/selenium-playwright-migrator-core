using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Migrator.Application;

/// <summary>
/// Explicit, source-exact opt-in for copying literal package references from
/// shared libraries. The source's project and packages must match the review
/// manifest exactly: this does not evaluate MSBuild or approve package safety.
/// </summary>
internal static class TargetSolutionPackageReview
{
    internal static Dictionary<string, TargetSolutionPackage[]> Read(
        string? configPath, List<TargetAssemblyIssue> issues)
    {
        var approved = new Dictionary<string, TargetSolutionPackage[]>(
            StringComparer.Ordinal);
        if (configPath == null) return approved;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(
                    "TargetSolutionReviewedPackages", out var entries))
                return approved;
            if (entries.ValueKind != JsonValueKind.Array)
                throw new FormatException("Expected an array of reviewed project package entries.");
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("SourceProject", out var project)
                    || project.ValueKind != JsonValueKind.String
                    || !entry.TryGetProperty("Reviewed", out var reviewed)
                    || reviewed.ValueKind != JsonValueKind.True
                    || !entry.TryGetProperty("Packages", out var packages)
                    || packages.ValueKind != JsonValueKind.Array)
                    throw new FormatException("Each entry needs SourceProject, Reviewed=true and Packages.");
                var name = project.GetString()!;
                if (name.Length == 0 || name.StartsWith("/", StringComparison.Ordinal)
                    || name.Contains('\\') || name.Split('/').Any(part =>
                        part.Length == 0 || part == "." || part == "..")
                    || !name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                    || approved.ContainsKey(name))
                    throw new FormatException("SourceProject must be a unique normalized relative csproj path.");

                var result = new List<TargetSolutionPackage>();
                foreach (var package in packages.EnumerateArray())
                {
                    if (package.ValueKind != JsonValueKind.Object
                        || !package.TryGetProperty("Id", out var id)
                        || id.ValueKind != JsonValueKind.String
                        || !package.TryGetProperty("Version", out var version)
                        || version.ValueKind != JsonValueKind.String)
                        throw new FormatException("Package entry must have literal Id and Version.");
                    var value = new TargetSolutionPackage(id.GetString()!, version.GetString()!);
                    if (!Valid(value))
                        throw new FormatException("Package Id or Version is unsafe or not literal.");
                    if (result.Any(other => string.Equals(
                        other.Id, value.Id, StringComparison.OrdinalIgnoreCase)))
                        throw new FormatException("Duplicate reviewed package Id.");
                    result.Add(value);
                }
                approved.Add(name, result.OrderBy(x => x.Id, StringComparer.Ordinal)
                    .ToArray());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or JsonException or FormatException)
        {
            approved.Clear();
            issues.Add(new TargetAssemblyIssue(
                "SOLUTION_PACKAGE_REVIEW_PROFILE_INVALID", "error",
                "The explicit reviewed library package profile is invalid: "
                    + ex.GetType().Name));
        }
        return approved;
    }

    internal static TargetSolutionPackage[] Validate(
        XDocument document, string project,
        IReadOnlyDictionary<string, TargetSolutionPackage[]> profile,
        List<TargetAssemblyIssue> issues)
    {
        var packages = document.Descendants()
            .Where(node => node.Name.LocalName == "PackageReference").ToArray();
        if (document.Descendants().Any(node =>
            node.Name.LocalName is "Reference" or "FrameworkReference"))
            issues.Add(new TargetAssemblyIssue("SOLUTION_LIBRARY_PACKAGE_UNSUPPORTED",
                "error", "Non-NuGet assembly/framework references in shared libraries need a separate reviewed projection.",
                project));
        if (packages.Length == 0)
        {
            if (profile.TryGetValue(project, out var extra) && extra.Length > 0)
                issues.Add(new TargetAssemblyIssue("SOLUTION_PACKAGE_REVIEW_MISMATCH",
                    "error", "Reviewed packages are absent from the source library.", project));
            return Array.Empty<TargetSolutionPackage>();
        }
        if (!profile.TryGetValue(project, out var expected))
        {
            issues.Add(new TargetAssemblyIssue("SOLUTION_LIBRARY_PACKAGE_UNSUPPORTED",
                "error", "An external NuGet dependency needs explicit TargetSolutionReviewedPackages approval.",
                project));
            return Array.Empty<TargetSolutionPackage>();
        }
        var source = new List<TargetSolutionPackage>();
        foreach (var node in packages)
        {
            var id = node.Attribute("Include")?.Value;
            var version = node.Attribute("Version")?.Value;
            if (id == null || version == null || node.HasElements
                || node.Attributes().Any(attr =>
                    attr.Name.LocalName is not ("Include" or "Version")))
            {
                issues.Add(new TargetAssemblyIssue("SOLUTION_PACKAGE_REFERENCE_DYNAMIC",
                    "error", "Only literal Include/Version-only PackageReference declarations can be reviewed.",
                    project));
                return Array.Empty<TargetSolutionPackage>();
            }
            var package = new TargetSolutionPackage(id, version);
            if (!Valid(package))
            {
                issues.Add(new TargetAssemblyIssue("SOLUTION_PACKAGE_REFERENCE_DYNAMIC",
                    "error", "Floating, variable, unsafe or test-framework package references cannot be copied.",
                    project));
                return Array.Empty<TargetSolutionPackage>();
            }
            source.Add(package);
        }
        bool same = expected.Length == source.Count
            && source.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() == source.Count
            && source.All(item => expected.Any(reviewed =>
                string.Equals(reviewed.Id, item.Id,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(reviewed.Version, item.Version,
                    StringComparison.Ordinal)));
        if (!same)
        {
            issues.Add(new TargetAssemblyIssue("SOLUTION_PACKAGE_REVIEW_MISMATCH",
                "error", "The reviewed package IDs/versions do not exactly match the source project.",
                project));
            return Array.Empty<TargetSolutionPackage>();
        }
        return source.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
    }

    private static bool Valid(TargetSolutionPackage package)
    {
        if (package.Id.Length is < 1 or > 120
            || !Regex.IsMatch(package.Id, @"^[A-Za-z0-9][A-Za-z0-9_.-]*$")
            || package.Version.Length is < 3 or > 80
            || !Regex.IsMatch(package.Version,
                @"^[0-9]+(?:\.[0-9]+){1,3}(?:-[A-Za-z0-9][A-Za-z0-9.-]*)?$"))
            return false;
        var id = package.Id;
        // Test/browser infrastructure in a shared library must not be
        // transplanted with only an adapter configuration endorsement.
        return !id.Contains("Selenium", StringComparison.OrdinalIgnoreCase)
            && !id.Contains("Playwright", StringComparison.OrdinalIgnoreCase)
            && !id.StartsWith("NUnit", StringComparison.OrdinalIgnoreCase)
            && !id.StartsWith("xUnit", StringComparison.OrdinalIgnoreCase)
            && !id.Contains("Test.Sdk", StringComparison.OrdinalIgnoreCase);
    }
}
