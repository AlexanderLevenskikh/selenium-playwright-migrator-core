using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Migrator.Core;

namespace Migrator.Application;

public sealed partial class MigrationEngine
{
    private const string SolutionArtifactName = "Migrator.Generated.slnx";
    private const string SolutionManifestName = "migrator-solution-manifest.json";
    private const string SolutionSchema = "migrator-target-solution/v1alpha3";
    private const int SolutionProjectLimit = 20;
    private const int SolutionFileLimit = 256;
    private const long SolutionBytesLimit = 24 * 1024 * 1024;

    private sealed record SourceSolutionNode(
        string ProjectFile,
        string RelativeProject,
        string Directory,
        string RelativeDirectory,
        string AssemblyName,
        string Kind,
        string[] Sources,
        string[] References,
        TargetAssemblyPlan? NUnitPlan,
        TargetSolutionPackage[] Packages,
        int DeclaredTestMethods);

    /// <summary>
    /// Read-only complete graph planning. Every NUnit source project gets one
    /// target NUnit assembly; every target-safe project library gets its own
    /// target library. ProjectReference edges are rewritten without flattening
    /// classes or crossing NUnit assembly-level SetUpFixture boundaries.
    /// </summary>
    public TargetSolutionPlan PlanTargetSolution(
        string sourceRoot,
        string? configPath = null,
        string? expectedSourceTreeHash = null,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(sourceRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("TARGET_SOLUTION_ROOT_MISSING");
        var issues = new List<TargetAssemblyIssue>();
        var projectPaths = Directory.GetFiles(root, "*.csproj",
                SearchOption.AllDirectories)
            .Where(path => !SolutionExcluded(path, root))
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (projectPaths.Length < 2 || projectPaths.Length > SolutionProjectLimit)
            issues.Add(new("SOLUTION_PROJECT_COUNT_INVALID", "error",
                "Project-level solution migration supports 2..20 source SDK projects."));

        var rootFiles = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !SolutionExcluded(path, root))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (rootFiles.Any(path => (File.GetAttributes(path) &
                FileAttributes.ReparsePoint) != 0)
            || Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
                .Any(path => (File.GetAttributes(path) &
                    FileAttributes.ReparsePoint) != 0))
            issues.Add(new("SOLUTION_SOURCE_SYMLINK", "error",
                "A symlink in source may escape the project/assembly ownership boundary."));
        if (rootFiles.Any(path => Path.GetFileName(path) is
                "Directory.Build.props" or "Directory.Build.targets"
                or "Directory.Packages.props"))
            issues.Add(new("SOLUTION_IMPORTS_UNSUPPORTED", "error",
                "Shared implicit MSBuild imports are not evaluated by the source-preserving planner."));

        var sourceTree = ContentTreeHasher.ComputeBytes(rootFiles
            .Where(path => Path.GetExtension(path) is ".cs" or ".csproj"
                or ".props" or ".targets")
            .Select(path => (Relative: Path.GetRelativePath(root, path)
                    .Replace('\\', '/'), Bytes: File.ReadAllBytes(path))));
        if (expectedSourceTreeHash != null
            && !string.Equals(expectedSourceTreeHash, sourceTree,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "STALE_SOLUTION_SOURCE: source project graph changed since the requested plan.");
        var configHash = HashConfig(configPath);
        var byPath = new Dictionary<string, SourceSolutionNode>(
            StringComparer.OrdinalIgnoreCase);
        var byDirectory = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reviewedPackages = TargetSolutionPackageReview.Read(configPath, issues);
        foreach (var project in projectPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projectDir = Path.GetDirectoryName(project)!;
            var relativeProject = Path.GetRelativePath(root, project)
                .Replace('\\', '/');
            var relativeDir = Path.GetRelativePath(root, projectDir)
                .Replace('\\', '/');
            if (!byDirectory.Add(projectDir))
                issues.Add(new("SOLUTION_PROJECT_DIRECTORY_AMBIGUOUS", "error",
                    "Two .csproj files in one directory cannot own distinct default C# source files.",
                    relativeProject));
            if (projectPaths.Any(other => !string.Equals(other, project,
                    StringComparison.OrdinalIgnoreCase)
                && PathInside(projectDir, Path.GetDirectoryName(other)!)))
                issues.Add(new("SOLUTION_NESTED_PROJECT_UNSUPPORTED", "error",
                    "Nested SDK projects would make Compile and source ownership ambiguous.",
                    relativeProject));

            XDocument? doc = null;
            try { doc = XDocument.Load(project); }
            catch (Exception ex) when (ex is System.Xml.XmlException or IOException)
            {
                issues.Add(new("SOLUTION_PROJECT_XML_INVALID", "error", ex.Message,
                    relativeProject));
            }
            if (doc?.Root == null)
                continue;
            if (!string.Equals(doc.Root.Name.LocalName, "Project",
                    StringComparison.Ordinal)
                || !string.Equals(doc.Root.Attribute("Sdk")?.Value,
                    "Microsoft.NET.Sdk", StringComparison.Ordinal))
                issues.Add(new("SOLUTION_PROJECT_SDK_UNSUPPORTED", "error",
                    "Only a literal <Project Sdk=\"Microsoft.NET.Sdk\"> is supported.",
                    relativeProject));
            if (doc.Descendants().Any(node => node.Attribute("Condition") != null
                || node.Name.LocalName is "Import" or "ImportGroup" or "Choose"
                    or "Target" or "UsingTask" or "Exec"))
                issues.Add(new("SOLUTION_DYNAMIC_MSBUILD_UNSUPPORTED", "error",
                    "Conditional/imported/imperative MSBuild can alter source ownership or references.",
                    relativeProject));
            if (doc.Descendants().Any(node => node.Name.LocalName == "Compile")
                || doc.Descendants().Any(node => node.Name.LocalName
                    == "EnableDefaultCompileItems" && node.Value.Trim() == "false"))
                issues.Add(new("SOLUTION_CUSTOM_COMPILE_UNSUPPORTED", "error",
                    "Project source inclusion must use the SDK default Compile inventory.",
                    relativeProject));
            var references = new List<string>();
            foreach (var reference in doc.Descendants().Where(node =>
                node.Name.LocalName == "ProjectReference"))
            {
                var include = reference.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include)
                    || include.IndexOfAny(new[] { '$', '*', '?', ';' }) >= 0
                    || reference.HasElements
                    || reference.Attributes().Any(attr =>
                        attr.Name.LocalName != "Include"))
                {
                    issues.Add(new("SOLUTION_REFERENCE_DYNAMIC", "error",
                        "A ProjectReference must have exactly one literal Include path, without conditions or metadata.",
                        relativeProject));
                    continue;
                }
                var resolved = Path.GetFullPath(Path.Combine(projectDir,
                    include.Replace('\\', Path.DirectorySeparatorChar)
                        .Replace('/', Path.DirectorySeparatorChar)));
                if (!PathInside(root, resolved))
                    issues.Add(new("SOLUTION_REFERENCE_EXTERNAL", "error",
                        "ProjectReference escapes the solution root.",
                        relativeProject));
                else if (!File.Exists(resolved))
                    issues.Add(new("SOLUTION_REFERENCE_MISSING", "error",
                        "A ProjectReference target is absent.",
                        relativeProject));
                else if (string.Equals(project, resolved,
                    StringComparison.OrdinalIgnoreCase))
                    issues.Add(new("SOLUTION_REFERENCE_SELF", "error",
                        "A project may not reference itself.", relativeProject));
                else
                    references.Add(resolved);
            }
            if (references.Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != references.Count)
                issues.Add(new("SOLUTION_REFERENCE_DUPLICATE", "error",
                    "Duplicate project references are not accepted.", relativeProject));

            var files = Directory.GetFiles(projectDir, "*.cs", SearchOption.AllDirectories)
                .Where(path => !SolutionExcluded(path, projectDir))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            if (files.Length == 0)
                issues.Add(new("SOLUTION_PROJECT_WITHOUT_SOURCE", "error",
                    "A project without source files cannot be preserved.", relativeProject));
            var hasTests = false;
            var hasScopes = false;
            var declaredTestMethods = 0;
            // Keep Roslyn source trees from the single preflight parse: do
            // not reparse all library files a second time.
            var parsedRoots = new List<(string File, SyntaxNode Root)>();
            var assemblyScheduling = new List<(string File, string Attribute, int Line)>();
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativeFile = Path.GetRelativePath(root, file).Replace('\\', '/');
                var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file),
                    path: file);
                if (tree.GetDiagnostics().Any(d =>
                    d.Severity == DiagnosticSeverity.Error))
                    issues.Add(new("SOLUTION_SOURCE_SYNTAX_ERROR", "error",
                        "A source C# file has syntax errors.", relativeFile));
                var syntax = tree.GetRoot();
                parsedRoots.Add((file, syntax));

                var methods = syntax.DescendantNodes()
                    .OfType<MethodDeclarationSyntax>().ToArray();
                var NUnitTest(MethodDeclarationSyntax method) =>
                    method.AttributeLists.SelectMany(list => list.Attributes)
                        .Any(attr => AttributeMatches(attr,
                            "Test", "TestCase", "TestCaseSource"));
                var count = methods.Count(NUnitTest);
                declaredTestMethods += count;
                hasTests |= count > 0;

                if (methods.Any(method => method.AttributeLists
                    .SelectMany(list => list.Attributes).Any(attr =>
                        AttributeMatches(attr, "Fact", "Theory"))))
                    issues.Add(new("SOLUTION_UNSUPPORTED_XUNIT_SOURCE", "error",
                        "xUnit methods cannot be copied into a non-xUnit shared library "
                        + "or silently interpreted as NUnit tests.", relativeFile));

                if (methods.Any(method => method.AttributeLists
                    .SelectMany(list => list.Attributes).Any(attr =>
                        AttributeMatches(attr, "TestCaseSource"))))
                    issues.Add(new("SOLUTION_DYNAMIC_TEST_DATA_REQUIRES_REVIEW",
                        "error", "NUnit TestCaseSource data generation and provider "
                        + "lifetime have not been proven in the target.",
                        relativeFile));

                foreach (var gate in NUnitExecutionGateGuard.Find(syntax))
                    issues.Add(new("SOLUTION_NUNIT_EXECUTION_GATE_REQUIRES_REVIEW",
                        "error", "NUnit execution gate " + gate.GateName
                        + " on " + gate.Scope + " at source line "
                        + gate.SourceLine + " is not automatically preserved.",
                        relativeFile));

                foreach (var type in syntax.DescendantNodes()
                    .OfType<ClassDeclarationSyntax>())
                {
                    if (!type.AttributeLists.SelectMany(list => list.Attributes)
                        .Any(attr => AttributeMatches(attr, "SetUpFixture")))
                        continue;
                    hasScopes = true;
                    if (type.Ancestors().OfType<TypeDeclarationSyntax>().Any())
                        issues.Add(new("SOLUTION_NESTED_SETUPFIXTURE_UNSUPPORTED",
                            "error", "A nested NUnit SetUpFixture has ambiguous/invalid "
                            + "suite ownership; never invent an assembly-scope hook.",
                            relativeFile));
                }

                if (syntax is CompilationUnitSyntax unit)
                    foreach (var list in unit.AttributeLists.Where(list =>
                        list.Target?.Identifier.ValueText == "assembly"))
                        foreach (var attr in list.Attributes)
                            if (AttributeMatches(attr, "Parallelizable",
                                    "NonParallelizable", "LevelOfParallelism"))
                                assemblyScheduling.Add((relativeFile,
                                    attr.Name.ToString(),
                                    attr.GetLocation().GetLineSpan()
                                        .StartLinePosition.Line + 1));
            }
            var kind = hasTests || hasScopes ? "nunit" : "library";
            if (kind == "nunit")
                foreach (var setting in assemblyScheduling)
                    issues.Add(new("SOLUTION_ASSEMBLY_SCHEDULING_REQUIRES_REVIEW",
                        "error", "Assembly-level NUnit scheduling attribute "
                        + setting.Attribute + " at line " + setting.Line
                        + " is not safely carried into the target assembly.",
                        setting.File));
            // Finish inexpensive, source-backed graph validation before any
            // expensive Roslyn/NUnit target generation. Invalid solutions
            // must fail quickly instead of repeatedly recompiling fixtures.
            var packages = kind == "library"
                ? TargetSolutionPackageReview.Validate(doc, relativeProject,
                    reviewedPackages, issues)
                : Array.Empty<TargetSolutionPackage>();
            TargetAssemblyPlan? nunit = null;
            if (kind == "library")
            {
                foreach (var (file, sourceRoot) in parsedRoots)
                {
                    var tokens = sourceRoot.DescendantTokens()
                        .Where(token => token.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.IdentifierToken))
                        .Select(token => token.ValueText).ToHashSet(StringComparer.Ordinal);
                    var forbidden = sourceRoot.DescendantNodes()
                        .OfType<UsingDirectiveSyntax>().Any(u =>
                            u.Name?.ToString() is "NUnit.Framework"
                                or "OpenQA.Selenium" or "Microsoft.Playwright")
                        || tokens.Contains("SetUpFixture")
                        || tokens.Contains("TestFixture")
                        || tokens.Contains("IWebDriver")
                        || tokens.Contains("IWebElement")
                        || tokens.Contains("PageTest");
                    if (forbidden)
                        issues.Add(new("SOLUTION_LIBRARY_TEST_LIFECYCLE_UNSAFE", "error",
                            "Shared library source contains NUnit/Selenium/Playwright fixtures or driver code; it cannot be copied as target-safe shared code.",
                            Path.GetRelativePath(root, file).Replace('\\', '/')));
                }
            }
            var assemblyName = doc.Descendants().Where(element =>
                    element.Name.LocalName == "AssemblyName")
                .Select(element => element.Value.Trim()).LastOrDefault()
                ?? Path.GetFileNameWithoutExtension(project);
            if (string.IsNullOrWhiteSpace(assemblyName)
                || assemblyName.Contains("$(", StringComparison.Ordinal)
                || assemblyName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                issues.Add(new("SOLUTION_ASSEMBLY_NAME_INVALID", "error",
                    "AssemblyName must be a literal, portable assembly identifier.",
                    relativeProject));
            byPath[project] = new SourceSolutionNode(project, relativeProject,
                projectDir, relativeDir, assemblyName, kind,
                files, references.ToArray(), nunit, packages,
                declaredTestMethods);
        }

        foreach (var node in byPath.Values)
        {
            foreach (var dep in node.References)
            {
                if (!byPath.TryGetValue(dep, out var target))
                    issues.Add(new("SOLUTION_REFERENCE_NOT_IN_GRAPH", "error",
                        "A reference is not an owned project in the exact solution graph.",
                        node.RelativeProject));
                else if (target.Kind != "library")
                    issues.Add(new("SOLUTION_REFERENCE_TO_TEST_ASSEMBLY", "error",
                        "Target NUnit assemblies cannot be referenced as shared libraries; this could change SetUpFixture scope.",
                        node.RelativeProject));
            }
        }
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void DetectCycle(string project)
        {
            if (completed.Contains(project)) return;
            if (!visiting.Add(project))
            {
                issues.Add(new("SOLUTION_PROJECT_REFERENCE_CYCLE", "error",
                    "The source ProjectReference graph has a cycle.",
                    byPath[project].RelativeProject));
                return;
            }
            foreach (var dep in byPath[project].References)
                if (byPath.ContainsKey(dep)) DetectCycle(dep);
            visiting.Remove(project);
            completed.Add(project);
        }
        foreach (var project in byPath.Keys.OrderBy(path => path, StringComparer.Ordinal))
            DetectCycle(project);

        var assemblyNames = byPath.Values.GroupBy(node => node.AssemblyName,
            StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1);
        foreach (var group in assemblyNames)
            issues.Add(new("SOLUTION_ASSEMBLY_NAME_COLLISION", "error",
                "Two source projects have the same assembly name: " + group.Key));

        if (!byPath.Values.Any(node => node.Kind == "nunit"))
            issues.Add(new("SOLUTION_NUNIT_PROJECT_MISSING", "error",
                "No source NUnit test project was found."));


        foreach (var entry in reviewedPackages.Keys)
            if (!byPath.Values.Any(node => string.Equals(
                node.RelativeProject, entry, StringComparison.Ordinal)
                && node.Kind == "library"))
                issues.Add(new("SOLUTION_PACKAGE_REVIEW_PROFILE_INVALID", "error",
                    "A reviewed package profile references an absent or non-library project.",
                    entry));

        // Two phases: first validate every source .csproj, NuGet review profile,
        // project owner and graph edge. Only a globally safe graph may begin
        // expensive Roslyn analysis, repeated fixture previews or assembly
        // planning. This also produces useful blockers on large source trees
        // that cannot currently be migrated.
        if (!issues.Any(issue => issue.Severity == "error"))
        {
            foreach (var node in byPath.Values.Where(node =>
                         node.Kind == "nunit")
                     .OrderBy(node => node.RelativeProject, StringComparer.Ordinal)
                     .ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var plan = PlanTargetAssembly(node.Directory, configPath,
                        cancellationToken: cancellationToken);
                    byPath[node.ProjectFile] = node with { NUnitPlan = plan };
                    if (plan.Status != "ready_for_build")
                        issues.Add(new("SOLUTION_NUNIT_PROJECT_BLOCKED", "error",
                            "NUnit assembly has incomplete source or scoped "
                            + "SetUpFixture evidence: "
                            + string.Join(", ",
                                plan.Issues.Select(issue => issue.Code)),
                            node.RelativeProject));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    issues.Add(new("SOLUTION_NUNIT_PROJECT_FAILED", "error",
                        "Source NUnit assembly cannot be planned: "
                        + ex.GetType().Name, node.RelativeProject));
                }
            }
        }
        else
        {
            issues.Add(new("SOLUTION_NUNIT_PLANNING_DEFERRED", "warning",
                "Source graph already has blocking errors; expensive NUnit "
                + "candidate generation was not run, and no runnable NUnit "
                + "target files were produced."));
        }

        var plannedFiles = new List<TargetSolutionFile>();
        var projects = new List<TargetSolutionProject>();
        foreach (var node in byPath.Values.OrderBy(node =>
                     node.RelativeProject, StringComparer.Ordinal))
        {
            var targetFile = TargetSolutionProjectPath(node);
            var dependencies = node.References
                .Where(byPath.ContainsKey)
                .Select(dep => byPath[dep])
                .OrderBy(item => item.RelativeProject, StringComparer.Ordinal)
                .Select(item => TargetSolutionProjectPath(item)).ToArray();
            var sources = node.Kind == "nunit"
                ? node.NUnitPlan?.Files.Where(f =>
                        f.RelativePath.EndsWith(".cs", StringComparison.Ordinal))
                    .Select(f => (Name: f.RelativePath, Content: f.Content,
                        Original: f.SourcePath == null ? null
                            : CombineSourceRelative(node.RelativeDirectory, f.SourcePath)))
                    .ToArray()
                    ?? Array.Empty<(string Name, string Content, string? Original)>()
                : node.Sources.Select(path =>
                    (Name: Path.GetRelativePath(node.Directory, path).Replace('\\', '/'),
                        Content: File.ReadAllText(path),
                        Original: Path.GetRelativePath(root, path).Replace('\\', '/')))
                    .ToArray();

            var relativeDir = node.RelativeDirectory == "." ? "" : node.RelativeDirectory + "/";
            foreach (var (name, body, original) in sources)
                plannedFiles.Add(SolutionFile(relativeDir + name, body, original));
            var csproj = RenderSolutionProject(node, targetFile,
                dependencies, sources.Select(source => source.Name));
            plannedFiles.Add(SolutionFile(targetFile, csproj,
                node.RelativeProject));
            projects.Add(new TargetSolutionProject(node.RelativeProject,
                targetFile, node.AssemblyName,
                node.Kind, dependencies,
                node.NUnitPlan?.DeclaredTests ?? node.DeclaredTestMethods,
                node.NUnitPlan?.ScopedSetUpFixtures ?? 0,
                node.NUnitPlan?.AnalysisId, node.NUnitPlan?.CandidateId,
                node.Packages));
        }

        // SLNX requires unique project display names within each solution
        // folder. Generated project file names are intentionally stable and
        // repeated across assemblies, so preserve the source project folders
        // instead of placing every Migrator.Generated.Library in the root.
        var slnxLines = new List<string> { "<Solution>" };
        foreach (var group in projects
            .OrderBy(project => project.ProjectFile, StringComparer.Ordinal)
            .GroupBy(project => Path.GetDirectoryName(project.ProjectFile)?
                .Replace('\\', '/') ?? string.Empty, StringComparer.Ordinal))
        {
            if (group.Key.Length > 0)
                slnxLines.Add("  <Folder Name=\"/"
                    + SecurityElement.Escape(group.Key) + "/\">");
            foreach (var project in group)
                slnxLines.Add((group.Key.Length > 0 ? "    " : "  ")
                    + "<Project Path=\""
                    + SecurityElement.Escape(project.ProjectFile) + "\" />");
            if (group.Key.Length > 0)
                slnxLines.Add("  </Folder>");
        }
        slnxLines.Add("</Solution>");
        var slnx = string.Join("\n", slnxLines) + "\n";
        plannedFiles.Add(SolutionFile(SolutionArtifactName, slnx, null));
        var manifest = JsonSerializer.Serialize(new
        {
            schemaVersion = SolutionSchema,
            sourceTreeHash = sourceTree,
            configHash,
            solutionFile = SolutionArtifactName,
            projects = projects.OrderBy(p => p.ProjectFile, StringComparer.Ordinal),
            files = plannedFiles.OrderBy(file => file.RelativePath,
                    StringComparer.Ordinal).Select(f => new
                    { f.RelativePath, f.Sha256, f.SourcePath })
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { WriteIndented = true }) + "\n";
        plannedFiles.Add(SolutionFile(SolutionManifestName, manifest, null));
        var all = plannedFiles.OrderBy(file => file.RelativePath,
            StringComparer.Ordinal).ToArray();
        if (all.Length > SolutionFileLimit
            || all.Sum(file => (long)Encoding.UTF8.GetByteCount(file.Content))
                > SolutionBytesLimit)
            issues.Add(new("SOLUTION_TARGET_TOO_LARGE", "error",
                "The generated solution exceeds 256 files or 24 MiB."));
        if (all.Select(file => file.RelativePath).Distinct(
                StringComparer.OrdinalIgnoreCase).Count() != all.Length
            || all.Any(file => !ValidTargetAssemblyPath(file.RelativePath)))
            issues.Add(new("SOLUTION_TARGET_FILE_COLLISION", "error",
                "Output paths must be safe and unique even on case-insensitive systems."));

        var targetHash = ContentTreeHasher.ComputeBytes(all.Select(file =>
            (file.RelativePath, Encoding.UTF8.GetBytes(file.Content))));
        var planId = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(SolutionSchema + "\n" + sourceTree + "\n"
                + configHash + "\n" + targetHash))).ToLowerInvariant();
        var again = ContentTreeHasher.ComputeBytes(
            Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => !SolutionExcluded(path, root))
                .Where(path => Path.GetExtension(path) is ".cs" or ".csproj"
                    or ".props" or ".targets")
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => (Relative: Path.GetRelativePath(root, path)
                    .Replace('\\', '/'), Bytes: File.ReadAllBytes(path))));
        if (again != sourceTree || HashConfig(configPath) != configHash)
            throw new InvalidOperationException(
                "SOLUTION_SOURCE_CHANGED_DURING_PLAN: retry from stable source/config.");
        return new TargetSolutionPlan(SolutionSchema, root, sourceTree,
            configHash, planId, targetHash, SolutionArtifactName,
            SolutionManifestName,
            issues.Any(issue => issue.Severity == "error") ? "blocked"
                : "ready_for_build",
            projects, all, issues,
            new[]
            {
                "Each source NUnit project stays a separate target NUnit assembly; global SetUpFixtures do not cross ProjectReference boundaries.",
                "Shared source libraries require safe C# and exact reviewed literal NuGet package IDs/versions; unsupported dependencies block before NUnit planning.",
                "Dynamic MSBuild, custom Compile items, unreviewed/dynamic dependencies, external references, cycles and nested project ownership fail closed.",
                "DeclaredTests counts source NUnit methods, even when target generation was deferred; it never implies target tests were generated.",
                "Ready for build is source closure evidence, not compilation or business behavior.",
                "dotnet build and NUnit runtime require explicit opt-in and are reported separately."
            });
    }

    private static bool AttributeMatches(AttributeSyntax attribute,
        params string[] names)
    {
        var value = attribute.Name.ToString().Split('.').Last();
        if (value.EndsWith("Attribute", StringComparison.Ordinal))
            value = value[..^"Attribute".Length];
        return names.Contains(value, StringComparer.Ordinal);
    }

    private static string CombineSourceRelative(string projectDir,
        string? source) =>
        projectDir is "" or "." ? source ?? string.Empty
            : projectDir + "/" + source;

    private static bool SolutionExcluded(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Split(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)
            .Any(segment => segment is "bin" or "obj" or ".git"
                or ".vs" or ".idea" or "artifacts");
    }

    private static string TargetSolutionProjectPath(SourceSolutionNode node) =>
        (node.RelativeDirectory == "." ? "" : node.RelativeDirectory + "/")
        + (node.Kind == "nunit" ? "Migrator.Generated.Tests.csproj"
            : "Migrator.Generated.Library.csproj");

    private static TargetSolutionFile SolutionFile(string relative,
        string content, string? source)
    {
        var digest = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
        return new TargetSolutionFile(relative.Replace('\\', '/'),
            content, digest, source);
    }

    private static string RenderSolutionProject(SourceSolutionNode node,
        string projectFile, IReadOnlyList<string> dependencies,
        IEnumerable<string> sources)
    {
        var from = Path.GetDirectoryName(projectFile)?.Replace('\\', '/')
            ?? string.Empty;
        var dir = from.Length == 0 ? "." : from;
        var lines = new List<string>
        {
            "<Project Sdk=\"Microsoft.NET.Sdk\">",
            "  <PropertyGroup>",
            "    <TargetFramework>net10.0</TargetFramework>",
            "    <AssemblyName>" + SecurityElement.Escape(
                node.AssemblyName) + "</AssemblyName>",
            "    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>",
            "    <Nullable>enable</Nullable>",
            "    <ImplicitUsings>enable</ImplicitUsings>",
            "    <Deterministic>true</Deterministic>",
            "    <IsPackable>false</IsPackable>",
            "    <IsTestProject>" + (node.Kind == "nunit" ? "true"
                : "false") + "</IsTestProject>",
            "  </PropertyGroup>"
        };
        if (node.Kind == "nunit")
        {
            lines.Add("  <ItemGroup>");
            lines.Add("    <PackageReference Include=\"Microsoft.Playwright.NUnit\" Version=\"1.63.0\" />");
            lines.Add("    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.14.1\" />");
            lines.Add("    <PackageReference Include=\"NUnit3TestAdapter\" Version=\"4.5.0\" />");
            lines.Add("    <PackageReference Include=\"NUnit\" Version=\"3.14.0\" />");
            lines.Add("  </ItemGroup>");
        }
        if (node.Kind == "library" && node.Packages.Length > 0)
        {
            lines.Add("  <ItemGroup>");
            foreach (var package in node.Packages)
                lines.Add("    <PackageReference Include=\"" + SecurityElement.Escape(
                    package.Id) + "\" Version=\"" + SecurityElement.Escape(
                    package.Version) + "\" />");
            lines.Add("  </ItemGroup>");
        }
        lines.Add("  <ItemGroup>");
        foreach (var source in sources.OrderBy(x => x, StringComparer.Ordinal))
            lines.Add("    <Compile Include=\"" + SecurityElement.Escape(
                source) + "\" />");
        lines.Add("  </ItemGroup>");
        if (dependencies.Count > 0)
        {
            lines.Add("  <ItemGroup>");
            foreach (var dependency in dependencies)
            {
                var path = Path.GetRelativePath(dir, dependency)
                    .Replace('\\', '/');
                lines.Add("    <ProjectReference Include=\""
                    + SecurityElement.Escape(path) + "\" />");
            }
            lines.Add("  </ItemGroup>");
        }
        lines.Add("</Project>");
        return string.Join("\n", lines) + "\n";
    }
}
