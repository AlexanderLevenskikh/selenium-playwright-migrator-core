using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Migrator.Core;

namespace Migrator.Application;

/// <summary>
/// Conservative guard for already-Playwright NUnit test files. This engine's source
/// frontend is Selenium C#, not a general Playwright-to-Playwright normalizer.
/// Only inspect input files and project metadata inside the requested source scope;
/// no MSBuild evaluation, package restore, or external file access is performed.
/// </summary>
internal static class SourceFrameworkGuard
{
    internal sealed record Result(string Disposition, string Code, string Explanation, string EvidenceKind);

    private static readonly HashSet<string> PlaywrightAssertionMethods = new(StringComparer.Ordinal)
    {
        "ToHaveTextAsync", "ToContainTextAsync", "ToHaveCountAsync",
        "ToHaveAttributeAsync", "ToBeVisibleAsync", "ToBeHiddenAsync",
        "ToBeEnabledAsync", "ToHaveURLAsync", "ToHaveValueAsync",
        "ToBeCheckedAsync", "ToContainClassAsync"
    };

    public static Result? Classify(SyntaxNode root, string filePath, string sourceScope)
    {
        var imports = root.DescendantNodes().OfType<UsingDirectiveSyntax>()
            .Select(u => u.Name?.ToString() ?? string.Empty).ToArray();

        var usesPlaywrightNamespace = imports.Any(n =>
            n.Equals("Microsoft.Playwright", StringComparison.Ordinal)
            || n.StartsWith("Microsoft.Playwright.", StringComparison.Ordinal));
        var usesSeleniumNamespace = imports.Any(n =>
            n.Equals("OpenQA.Selenium", StringComparison.Ordinal)
            || n.StartsWith("OpenQA.Selenium.", StringComparison.Ordinal));

        var memberCalls = root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(i => i.Expression)
            .OfType<MemberAccessExpressionSyntax>()
            .Select(m => m.Name.Identifier.ValueText)
            .ToHashSet(StringComparer.Ordinal);
        var playwrightAssertion = memberCalls.Overlaps(PlaywrightAssertionMethods);

        // A Playwright-flavored assertion is stronger evidence than a generic
        // async helper (e.g. ClickAsync), which alone must not block Selenium.
        var sourceContainsStrongPlaywrightEvidence = usesPlaywrightNamespace || playwrightAssertion;
        var sourceContainsSeleniumEvidence = usesSeleniumNamespace ||
            (memberCalls.Contains("FindElement") || memberCalls.Contains("FindElements"));

        var (projectPlaywright, projectSelenium) =
            ReadNearestProjectReferencesWithinScope(filePath, sourceScope);

        if (!sourceContainsStrongPlaywrightEvidence && !projectPlaywright)
            return null;

        if (sourceContainsSeleniumEvidence || projectSelenium)
            return new Result("mixed_frameworks", "MIXED_SELENIUM_PLAYWRIGHT_SOURCE",
                "Both Selenium and Playwright evidence are present. Migration requires selecting an unambiguous Selenium-only source scope.",
                projectPlaywright || projectSelenium ? "project-file+syntax" : "syntax");

        return new Result("already_playwright", "ALREADY_PLAYWRIGHT_SOURCE",
            "This NUnit source already uses Microsoft.Playwright/Playwright assertions. Selenium-to-Playwright regeneration is blocked to avoid destructive double migration.",
            projectPlaywright ? "project-file+syntax" : "syntax");
    }

    internal static string ComputeInputHash(
        IEnumerable<string> sourceFiles, string sourceScope, CancellationToken cancellationToken)
    {
        var paths = sourceFiles.Select(file => FindNearestProjectWithinScope(file, sourceScope))
            .Where(path => path != null).Select(path => path!)
            .Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal);
        return ContentTreeHasher.ComputeBytes(paths.Select(path =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return (path, File.ReadAllBytes(path));
        }));
    }

    private static (bool Playwright, bool Selenium) ReadNearestProjectReferencesWithinScope(
        string filePath, string sourceScope)
    {
        var project = FindNearestProjectWithinScope(filePath, sourceScope);
        if (project == null) return (false, false);
        try
        {
            using var reader = XmlReader.Create(new StringReader(File.ReadAllText(project)),
                new XmlReaderSettings { XmlResolver = null, DtdProcessing = DtdProcessing.Prohibit });
            var xml = XDocument.Load(reader);
            var references = xml.Descendants()
                .Where(e => e.Name.LocalName is "PackageReference" or "Using")
                .Select(e => (string?)e.Attribute("Include") ?? string.Empty)
                .ToArray();
            return (
                references.Any(name => name.Equals("Microsoft.Playwright", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Microsoft.Playwright.NUnit", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("Playwright.", StringComparison.OrdinalIgnoreCase)),
                references.Any(name => name.Equals("Selenium.WebDriver", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("Selenium.", StringComparison.OrdinalIgnoreCase)));
        }
        catch (XmlException)
        {
            // Syntax evidence is still assessed; never claim full project semantics.
            return (false, false);
        }
    }

    // Share this selector with freshness capture. The framework guard also runs
    // for standalone/generated-path inputs for which Roslyn selects no project.
    private static string? FindNearestProjectWithinScope(string filePath, string sourceScope)
    {
        var boundary = Directory.Exists(sourceScope)
            ? Path.GetFullPath(sourceScope)
            : Path.GetDirectoryName(Path.GetFullPath(filePath))!;
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        while (directory.Equals(boundary, comparison) ||
               directory.StartsWith(boundary.TrimEnd(Path.DirectorySeparatorChar)
                   + Path.DirectorySeparatorChar, comparison))
        {
            var project = Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.Ordinal).FirstOrDefault();
            if (project != null) return project;
            if (directory.Equals(boundary, comparison))
                break;
            var parent = Path.GetDirectoryName(directory);
            if (parent is null || parent.Equals(directory, comparison))
                break;
            directory = parent;
        }
        return null;
    }
}
