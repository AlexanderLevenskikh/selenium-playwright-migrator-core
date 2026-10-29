using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Migrator.Core;
using Migrator.Core.Models;
using Migrator.Roslyn;

namespace Migrator.Application;

public sealed partial class MigrationEngine
{
    private const string TargetAssemblyProjectName = "Migrator.Generated.Tests.csproj";
    private const string TargetAssemblyManifestName = "migrator-assembly-manifest.json";
    private const string TargetAssemblySchema = "migrator-target-assembly/v1alpha3";
    private const int MaximumAssemblyFiles = 128;

    /// <summary>
    /// Pure, read-only project-level plan based on an exact analysis/preview
    /// snapshot. The SDK project explicitly includes EVERY generated fixture
    /// and SetUpFixture source; implicit compile globs are disabled.
    /// </summary>
    public TargetAssemblyPlan PlanTargetAssembly(
        string sourceDirectory,
        string? configPath = null,
        string? expectedAnalysisId = null,
        string? expectedCandidateId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        var root = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(root))
            throw new ArgumentException(
                "TARGET_ASSEMBLY_REQUIRES_DIRECTORY: pass the root of one source C# project.");
        var analysis = Analyze(root, configPath, cancellationToken);
        if (expectedAnalysisId != null && analysis.AnalysisId != expectedAnalysisId)
            throw new InvalidOperationException(
                "STALE_ANALYSIS: assembly plan requires the current source/config snapshot.");
        var preview = Preview(root, configPath, analysis.AnalysisId, cancellationToken);
        if (expectedCandidateId != null && preview.CandidateId != expectedCandidateId)
            throw new InvalidOperationException(
                "STALE_CANDIDATE: the target assembly parent candidate changed.");

        var issues = new List<TargetAssemblyIssue>();
        var projects = Directory.GetFiles(root, "*.csproj", SearchOption.TopDirectoryOnly);
        var nested = Directory.GetFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(project => !projects.Contains(project, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (projects.Length != 1 || nested.Length > 0)
            issues.Add(new("SOURCE_PROJECT_OWNERSHIP_AMBIGUOUS", "error",
                "The source root must contain exactly one SDK-style .csproj and no nested .csproj; "
                + "merging multiple NUnit assemblies changes global SetUpFixture behavior."));
        if (projects.Length == 1)
        {
            var sourceProject = File.ReadAllText(projects[0]);
            if (!sourceProject.Contains("Microsoft.NET.Sdk", StringComparison.Ordinal))
                issues.Add(new("SOURCE_PROJECT_NOT_SDK", "error",
                    "The source project must use an SDK-style .NET project declaration."));
        }

        foreach (var survey in analysis.Files)
        {
            if (survey.Disposition is not ("supported" or "no_test_declarations"))
                issues.Add(new("SOURCE_DISCOVERY_INCOMPLETE", "error",
                    "Source file was not safely parsed: " + survey.Disposition,
                    survey.RelativePath));
        }

        // Assembly attributes may be declared in a source file with no
        // [Test] methods. A per-test preview silently omits that file;
        // inspect the complete project source set before creating its exact
        // target assembly and retain source-path/line evidence.
        var scheduling = NUnitAssemblySchedulingInspector.Inspect(
            Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(path => !SolutionExcluded(path, root))
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => (SourcePath: Path.GetRelativePath(root, path)
                        .Replace('\\', '/'),
                    Root: (Microsoft.CodeAnalysis.SyntaxNode)CSharpSyntaxTree
                        .ParseText(File.ReadAllText(path), path: path).GetRoot())));
        issues.AddRange(scheduling.Issues);
        var assemblySchedulingSource = scheduling.RenderTarget();

        if (preview.Files.Count == 0 || preview.Files.Count > MaximumAssemblyFiles)
            issues.Add(new("TARGET_FILE_SET_INVALID", "error",
                "The target must contain 1..128 generated source files."));
        if (preview.Files.Sum(file => file.Tests) != analysis.DeclaredTests)
            issues.Add(new("TARGET_TEST_INVENTORY_MISMATCH", "error",
                "The generated test method count differs from the source inventory."));
        if (preview.Findings.Any(finding => finding.Code == "GENERATION_ERROR"))
            issues.Add(new("TARGET_GENERATION_INCOMPLETE", "error",
                "One or more target source files could not be generated."));

        var bySource = preview.Files.ToDictionary(file => file.SourcePath,
            file => file, StringComparer.Ordinal);
        var sourceModels = new List<(string Source, TestFileModel Model)>();
        var config = LoadConfig(configPath);
        foreach (var file in preview.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var absolute = Path.GetFullPath(Path.Combine(root,
                file.SourcePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!PathInside(root, absolute) || !File.Exists(absolute))
            {
                issues.Add(new("SOURCE_FILE_OUTSIDE_PROJECT", "error",
                    "A generated candidate references a missing/out-of-scope source.", file.SourcePath));
                continue;
            }
            try
            {
                sourceModels.Add((file.SourcePath,
                    new RoslynTestFileParser(config).Parse(absolute)));
            }
            catch (Exception ex)
            {
                issues.Add(new("TARGET_SCOPE_SOURCE_UNRESOLVED", "error",
                    "Cannot validate the target assembly's source scope: " + ex.GetType().Name,
                    file.SourcePath));
            }
        }

        var declaredScopes = sourceModels
            .Where(item => item.Model.ScopedSetUp?.Declaration != null)
            .ToArray();
        foreach (var item in sourceModels)
        {
            var target = bySource[item.Source];
            var targetTree = CSharpSyntaxTree.ParseText(target.Content);
            var syntax = targetTree.GetRoot();
            var actualNamespace = syntax.DescendantNodes()
                .OfType<BaseNamespaceDeclarationSyntax>()
                .FirstOrDefault()?.Name.ToString() ?? string.Empty;
            var declaredNamespace = item.Model.Namespace;
            var scoped = item.Model.ScopedSetUp;
            if (scoped == null)
                continue;

            // In NUnit, the assembly root and each child namespace are
            // identity-sensitive. ".Playwright" suffix changes containment.
            if (!string.Equals(actualNamespace, declaredNamespace,
                StringComparison.Ordinal))
                issues.Add(new("TARGET_SCOPE_NAMESPACE_CHANGED", "error",
                    "Generated C# namespace must exactly match the source scoped namespace.",
                    item.Source));

            foreach (var diagnostic in scoped.Diagnostics)
                issues.Add(new(diagnostic.Code, "error", diagnostic.Description,
                    item.Source));

            if (scoped.Declaration is { } declaration)
            {
                var scopeClasses = syntax.DescendantNodes()
                    .OfType<ClassDeclarationSyntax>()
                    .Count(cls => cls.AttributeLists.SelectMany(al => al.Attributes)
                        .Any(attr => attr.Name.ToString() is
                            "SetUpFixture" or "SetUpFixtureAttribute"));
                if (scopeClasses != 1 || target.Tests > 0)
                    issues.Add(new("TARGET_SCOPE_DECLARATION_MISSING", "error",
                        "Exactly one standalone [SetUpFixture] class with zero tests is required.",
                        item.Source));
                if (declaration.Diagnostics.Count > 0)
                    issues.Add(new("SOURCE_SCOPE_DECLARATION_UNSAFE", "error",
                        "Source scoped fixture has unresolved lifecycle constraints.",
                        item.Source));
            }

            foreach (var required in scoped.Applicable)
            {
                var relative = Path.GetRelativePath(root,
                    Path.GetFullPath(required.SourceFile)).Replace('\\', '/');
                if (relative.StartsWith("../", StringComparison.Ordinal)
                    || !bySource.TryGetValue(relative, out var needed))
                {
                    issues.Add(new("TARGET_SCOPE_DEPENDENCY_MISSING", "error",
                        "Source namespace/assembly dependency has no generated target file: "
                        + required.SourceType, item.Source));
                    continue;
                }
                if (!declaredScopes.Any(scope => scope.Source == relative)
                    || !needed.Content.Contains("[SetUpFixture]", StringComparison.Ordinal))
                    issues.Add(new("TARGET_SCOPE_DEPENDENCY_NOT_DECLARED", "error",
                        "A namespace/assembly dependency exists, but its independent generated "
                        + "SetUpFixture class is missing.", item.Source));
            }
        }

        var targetNames = preview.Files.Select(file => file.RelativePath)
            .Concat(assemblySchedulingSource == null
                ? Array.Empty<string>()
                : new[] { NUnitAssemblySchedulingResult.GeneratedFileName })
            .ToArray();
        if (targetNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != targetNames.Length
            || targetNames.Any(name => !ValidTargetAssemblyPath(name)))
            issues.Add(new("TARGET_FILE_NAME_COLLISION", "error",
                "Every source file must map to one safe, uniquely named target file."));

        var csharpSources = preview.Files
            .Select(file => new TargetAssemblyFile(file.RelativePath,
                file.Content, HashAssemblyText(file.Content), file.SourcePath))
            .ToList();
        if (assemblySchedulingSource != null)
            csharpSources.Add(new TargetAssemblyFile(
                NUnitAssemblySchedulingResult.GeneratedFileName,
                assemblySchedulingSource, HashAssemblyText(assemblySchedulingSource),
                null));
        var csharp = csharpSources.OrderBy(file => file.RelativePath,
            StringComparer.Ordinal).ToArray();
        if (csharp.Length > MaximumAssemblyFiles)
            issues.Add(new("TARGET_FILE_SET_INVALID", "error",
                "NUnit assembly plus scheduling sources exceed 128 C# files."));
        var projectContent = RenderAssemblyProject(csharp.Select(file => file.RelativePath));
        var projectFile = new TargetAssemblyFile(TargetAssemblyProjectName,
            projectContent, HashAssemblyText(projectContent), null);

        var manifestContent = JsonSerializer.Serialize(new
        {
            schemaVersion = TargetAssemblySchema,
            analysisId = analysis.AnalysisId,
            candidateId = preview.CandidateId,
            sourceTreeHash = analysis.SourceTreeHash,
            previewTargetTreeHash = preview.TargetTreeHash,
            projectFile = TargetAssemblyProjectName,
            declaredTests = analysis.DeclaredTests,
            assemblyScheduling = scheduling.Attributes.Select(attribute => new
            {
                attribute.SourcePath,
                attribute.SourceLine,
                attribute.SourceExpression,
                attribute.TargetExpression
            }),
            sourceProject = projects.Length == 1 ? Path.GetFileName(projects[0]) : null,
            files = csharp.Append(projectFile)
                .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                .Select(file => new { file.RelativePath, file.Sha256, file.SourcePath }),
            scopes = declaredScopes.Select(item => new
            {
                item.Source,
                namespaceName = item.Model.ScopedSetUp!.Declaration!.SourceNamespace,
                sourceType = item.Model.ScopedSetUp.Declaration.SourceType
            }).OrderBy(scope => scope.Source, StringComparer.Ordinal)
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) + "\n";

        var manifest = new TargetAssemblyFile(TargetAssemblyManifestName,
            manifestContent, HashAssemblyText(manifestContent), null);
        var files = csharp.Append(projectFile).Append(manifest)
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
        var totalSize = files.Sum(file => (long)Encoding.UTF8.GetByteCount(file.Content));
        if (totalSize > 16 * 1024 * 1024)
            issues.Add(new("TARGET_ASSEMBLY_TOO_LARGE", "error",
                "Target assembly content exceeds the conservative 16 MiB limit."));

        var treeHash = ContentTreeHasher.ComputeBytes(files.Select(file =>
            (file.RelativePath, Encoding.UTF8.GetBytes(file.Content))));
        var blockers = issues.Any(issue => issue.Severity == "error");
        var fresh = Analyze(root, configPath, cancellationToken);
        if (analysis.AnalysisId != fresh.AnalysisId)
            throw new InvalidOperationException(
                "SOURCE_CHANGED_DURING_ASSEMBLY_PLAN: repeat from a stable source snapshot.");

        return new TargetAssemblyPlan(TargetAssemblySchema, root,
            analysis.AnalysisId, preview.CandidateId, analysis.SourceTreeHash,
            preview.TargetTreeHash, treeHash, TargetAssemblyProjectName,
            TargetAssemblyManifestName, blockers ? "blocked" : "ready_for_build",
            analysis.DeclaredTests, declaredScopes.Length,
            files, issues,
            new[]
            {
                "Ready for build proves only source and target file-set closure, not test or business behavior.",
                "The target .csproj explicitly includes all generated sources and disables SDK compile globs.",
                "One original source SDK-style project is required; dynamic MSBuild item conditions and external suite dependencies are not fully evaluated.",
                "Playwright/NUnit package versions are fixed for reproducible target compilation.",
                "Scoped SetUpFixtures execute once per target assembly; per-suite namespace, state and failure-path semantics require runtime verification.",
                "Source-attested assembly NUnit scheduling attributes are copied into a separate compile-listed file; actual parallel execution still requires runtime review.",
                "Target test execution is opt-in, never performed by a read-only plan."
            }, scheduling.Attributes.Select(attribute =>
                new TargetAssemblySchedulingSource(attribute.SourcePath,
                    attribute.SourceLine, attribute.SourceExpression,
                    attribute.TargetExpression)).ToArray())
        {
            NUnitCases = analysis.Files.SelectMany(file => file.NUnitCases).ToArray()
        };
    }

    private static bool ValidTargetAssemblyPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)
            || path.Contains('\\') || path.IndexOfAny(new[] { ':', '*', '?', '"' }) >= 0)
            return false;
        return path.Split('/').All(component => component.Length > 0
            && component is not ("." or ".."));
    }

    private static string HashAssemblyText(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static string RenderAssemblyProject(IEnumerable<string> sourceFiles)
    {
        // Whitelist every Compile item instead of implicit **/*.cs. A missing
        // SetUpFixture then fails assembly verification rather than accidentally
        // being picked up or silently dropped by MSBuild.
        var parts = new List<string>
        {
            "<Project Sdk=\"Microsoft.NET.Sdk\">",
            "  <PropertyGroup>",
            "    <TargetFramework>net10.0</TargetFramework>",
            "    <AssemblyName>Migrator.Generated.NUnit.Tests</AssemblyName>",
            "    <IsTestProject>true</IsTestProject>",
            "    <IsPackable>false</IsPackable>",
            "    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>",
            "    <ImplicitUsings>enable</ImplicitUsings>",
            "    <Nullable>enable</Nullable>",
            "    <Deterministic>true</Deterministic>",
            "  </PropertyGroup>",
            "  <ItemGroup>",
            "    <PackageReference Include=\"Microsoft.Playwright.NUnit\" Version=\"1.63.0\" />",
            "    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.14.1\" />",
            "    <PackageReference Include=\"NUnit3TestAdapter\" Version=\"4.5.0\" />",
            "    <PackageReference Include=\"NUnit\" Version=\"3.14.0\" />",
            "  </ItemGroup>",
            "  <ItemGroup>"
        };
        foreach (var file in sourceFiles.OrderBy(name => name, StringComparer.Ordinal))
            parts.Add("    <Compile Include=\"" + SecurityElement.Escape(file) + "\" />");
        parts.Add("  </ItemGroup>");
        parts.Add("</Project>");
        return string.Join("\n", parts) + "\n";
    }
}
