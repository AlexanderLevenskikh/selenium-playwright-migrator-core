using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Migrator.Core;

namespace Migrator.Application;

public sealed partial class MigrationEngine
{
    /// <summary>
    /// Atomically publish a complete target .slnx only into a NEW directory,
    /// outside the source graph. Real dotnet build/test is opt-in and separate.
    /// </summary>
    public TargetSolutionVerification MaterializeTargetSolution(
        string sourceRoot, string targetRoot, string expectedSourceTreeHash,
        string expectedPlanId, string? configPath = null,
        bool runBuild = false, bool runNUnit = false,
        CancellationToken cancellationToken = default)
    {
        var plan = PlanTargetSolution(sourceRoot, configPath,
            expectedSourceTreeHash, cancellationToken);
        if (plan.PlanId != expectedPlanId)
            throw new InvalidOperationException(
                "STALE_SOLUTION_PLAN: source or target graph no longer matches.");
        if (plan.Status != "ready_for_build")
            throw new InvalidOperationException(
                "TARGET_SOLUTION_PLAN_BLOCKED: "
                + string.Join(", ", plan.Issues.Select(issue => issue.Code)));

        var output = Path.GetFullPath(targetRoot);
        EnsureTargetAssemblyDestination(plan.SourceRoot, output);
        var parent = Path.GetDirectoryName(output)
            ?? throw new InvalidOperationException("TARGET_SOLUTION_PARENT_REQUIRED");
        if (!Directory.Exists(parent))
            throw new DirectoryNotFoundException("TARGET_SOLUTION_PARENT_MISSING");
        if (Directory.Exists(output) || File.Exists(output))
            throw new InvalidOperationException(
                "TARGET_ALREADY_EXISTS: never overwrite an existing solution tree.");

        var staging = Path.Combine(parent,
            ".migrator-solution-stage-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            foreach (var file in plan.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(staging, file.RelativePath
                    .Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, Encoding.UTF8.GetBytes(file.Content));
            }
            var captured = CaptureSolutionFiles(staging, cancellationToken);
            if (!SameSolutionFiles(plan, captured, out var mismatch))
                throw new InvalidOperationException(
                    "TARGET_SOLUTION_STAGE_MISMATCH: " + mismatch);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, output);
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
        return VerifyTargetSolution(sourceRoot, output, expectedSourceTreeHash,
            expectedPlanId, configPath, runBuild, runNUnit, cancellationToken);
    }

    /// <summary>
    /// No write/execution unless specifically requested. First validates exact
    /// file set/SHA, .slnx project list, per-project Compile and ProjectReference
    /// closure, plus source scope hashes. Then (opt-in) builds the entire
    /// solution and runs each test assembly separately with NUnit TRX evidence.
    /// </summary>
    public TargetSolutionVerification VerifyTargetSolution(
        string sourceRoot, string targetRoot, string expectedSourceTreeHash,
        string expectedPlanId, string? configPath = null,
        bool runBuild = false, bool runNUnit = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedSourceTreeHash)
            || string.IsNullOrWhiteSpace(expectedPlanId))
            throw new ArgumentException(
                "EXPECTED_SOLUTION_SOURCE_AND_PLAN_ID_REQUIRED");
        var plan = PlanTargetSolution(sourceRoot, configPath,
            expectedSourceTreeHash, cancellationToken);
        if (plan.PlanId != expectedPlanId)
            throw new InvalidOperationException(
                "STALE_SOLUTION_PLAN: target manifest no longer has the expected parent.");
        var output = Path.GetFullPath(targetRoot);
        EnsureTargetAssemblyDestination(plan.SourceRoot, output);

        var issues = plan.Issues.ToList();
        var completeness = new TargetAssemblyStage("blocked",
            "solution-content-and-graph",
            "Source solution planning was blocked.");
        var build = new TargetAssemblyStage("not_run", "dotnet-slnx-build",
            "dotnet build was not requested.");
        var runtime = new TargetAssemblyStage("not_run", "nunit-trx-per-assembly",
            "NUnit test execution was not requested.");
        var executions = new List<TargetSolutionProjectExecution>();
        if (plan.Status == "ready_for_build")
        {
            try
            {
                var captured = CaptureSolutionFiles(output, cancellationToken);
                if (!SameSolutionFiles(plan, captured, out var mismatch))
                {
                    issues.Add(new("TARGET_SOLUTION_FILE_SET_OR_HASH_MISMATCH",
                        "error", mismatch));
                    completeness = new TargetAssemblyStage("failed",
                        "solution-content-and-graph", mismatch);
                }
                else if (!SolutionGraphMatches(plan, output, out mismatch))
                {
                    issues.Add(new("TARGET_SOLUTION_GRAPH_MISMATCH",
                        "error", mismatch));
                    completeness = new TargetAssemblyStage("failed",
                        "explicit-msbuild-graph", mismatch);
                }
                else
                    completeness = new TargetAssemblyStage("passed",
                        "sha256-files-and-explicit-msbuild-graph",
                        $"Verified {plan.Files.Count} exact solution files, "
                        + $"{plan.Projects.Count} assemblies and explicit Compile/ProjectReference closure.");
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException or System.Xml.XmlException
                or InvalidOperationException)
            {
                completeness = new TargetAssemblyStage("failed",
                    "solution-content-and-graph", ex.Message);
                issues.Add(new("TARGET_SOLUTION_CAPTURE_FAILED",
                    "error", ex.Message));
            }
        }

        if (completeness.Status == "passed" && (runBuild || runNUnit))
        {
            build = ExecuteDotNet(output,
                new[] { "build", plan.SolutionFile, "-c", "Release",
                    "--nologo" }, "dotnet-slnx-build", cancellationToken);
            if (runNUnit && build.Status == "passed")
            {
                var totalExecuted = 0;
                var totalPassed = 0;
                var anyFailure = false;
                foreach (var project in plan.Projects
                    .Where(project => project.Kind == "nunit")
                    .OrderBy(project => project.ProjectFile, StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var absProject = Path.Combine(output,
                        project.ProjectFile.Replace('/', Path.DirectorySeparatorChar));
                    var dir = Path.GetDirectoryName(absProject)!;
                    var evidence = ExecuteDotNet(dir,
                        new[] { "test", Path.GetFileName(absProject), "-c",
                            "Release", "--no-build", "--no-restore", "--nologo",
                            "--results-directory", "TestResults", "--logger",
                            "trx;LogFileName=migrator-target-assembly.trx" },
                        "nunit-trx-per-assembly", cancellationToken);
                    if (evidence.Status == "passed")
                        evidence = ValidateNUnitTrx(dir, evidence,
                            project.DeclaredTests);
                    executions.Add(new TargetSolutionProjectExecution(
                        project.ProjectFile, project.Kind, evidence));
                    if (evidence.Status != "passed") anyFailure = true;
                    totalExecuted += evidence.ExecutedTests ?? 0;
                    totalPassed += evidence.PassedTests ?? 0;
                }
                runtime = new TargetAssemblyStage(anyFailure
                    ? "failed" : "passed", "nunit-trx-per-assembly",
                    $"Executed {totalExecuted} NUnit tests in "
                        + $"{executions.Count} independent target assemblies; "
                        + $"{totalPassed} passed. Per-assembly TRX is mandatory.",
                    null, totalExecuted, totalPassed);
            }
            else if (runNUnit)
                runtime = new TargetAssemblyStage("blocked",
                    "nunit-trx-per-assembly",
                    "NUnit test execution is blocked until the complete .slnx builds.");
        }
        else if (completeness.Status != "passed" && (runBuild || runNUnit))
        {
            build = new TargetAssemblyStage("blocked", "dotnet-slnx-build",
                "The target solution file set or project graph was incomplete.");
            if (runNUnit)
                runtime = new TargetAssemblyStage("blocked",
                    "nunit-trx-per-assembly",
                    "NUnit execution is forbidden on an incomplete target graph.");
        }

        if (completeness.Status == "passed")
        {
            try
            {
                var after = CaptureSolutionFiles(output, cancellationToken);
                if (!SameSolutionFiles(plan, after, out var mismatch))
                {
                    completeness = new TargetAssemblyStage("failed",
                        "solution-content-and-graph",
                        "Generated source mutated during build/test: " + mismatch);
                    issues.Add(new("TARGET_SOLUTION_MUTATED_DURING_VERIFICATION",
                        "error", mismatch));
                }
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException or InvalidOperationException)
            {
                completeness = new TargetAssemblyStage("failed",
                    "solution-content-and-graph",
                    "Target files cannot be re-read after build.");
                issues.Add(new("TARGET_SOLUTION_MUTATED_DURING_VERIFICATION",
                    "error", ex.Message));
            }
        }

        var fresh = PlanTargetSolution(plan.SourceRoot, configPath,
            plan.SourceTreeHash, cancellationToken);
        if (fresh.PlanId != plan.PlanId)
            throw new InvalidOperationException(
                "SOLUTION_SOURCE_CHANGED_DURING_VERIFICATION: reject stale evidence.");

        var status = completeness.Status != "passed" ? "blocked"
            : build.Status == "failed" || runtime.Status == "failed" ? "failed"
            : build.Status == "blocked" || runtime.Status == "blocked" ? "blocked"
            : runtime.Status == "passed" ? "runtime_passed_review_required"
            : build.Status == "passed" ? "build_passed_review_required"
            : "materialized_review_required";
        return new TargetSolutionVerification(
            "migrator-target-solution-verification/v1alpha1",
            plan.PlanId, plan.SourceTreeHash, plan.TargetTreeHash,
            output, status, completeness, build, runtime, executions,
            issues,
            new[]
            {
                "Generated target tree/graph integrity is not proof that Selenium and Playwright are functionally equivalent.",
                "dotnet build evidence is for the generated solution and its exact package references only.",
                "Each NUnit TRX is independently checked in its original per-project assembly boundary.",
                "Business selectors, authentication, browser state and product observables require independent private E2E review.",
                "MCP project tools are read-only; code execution is explicit through the trusted Core CLI."
            });
    }

    private static bool SolutionGraphMatches(TargetSolutionPlan plan,
        string root, out string mismatch)
    {
        var slnx = XDocument.Load(Path.Combine(root, plan.SolutionFile));
        var projectList = slnx.Descendants().Where(node =>
                node.Name.LocalName == "Project")
            .Select(node => node.Attribute("Path")?.Value)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var expected = plan.Projects.Select(p => p.ProjectFile)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (!projectList.SequenceEqual(expected, StringComparer.Ordinal))
        {
            mismatch = "Generated .slnx project membership is incomplete.";
            return false;
        }
        foreach (var project in plan.Projects)
        {
            var path = Path.Combine(root,
                project.ProjectFile.Replace('/', Path.DirectorySeparatorChar));
            var doc = XDocument.Load(path);
            var defaultItems = doc.Descendants().FirstOrDefault(n =>
                n.Name.LocalName == "EnableDefaultCompileItems")?.Value;
            var compiled = doc.Descendants().Where(n =>
                    n.Name.LocalName == "Compile")
                .Select(n => n.Attribute("Include")?.Value?.Replace('\\', '/'))
                .Where(x => x != null).Select(x => x!)
                .OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var dir = Path.GetDirectoryName(project.ProjectFile)!
                .Replace('\\', '/');
            var prefix = dir is "" or "." ? "" : dir + "/";
            var expectedSource = plan.Files.Where(f =>
                    f.RelativePath.StartsWith(prefix, StringComparison.Ordinal)
                    && f.RelativePath.EndsWith(".cs", StringComparison.Ordinal))
                .Select(f => f.RelativePath[prefix.Length..])
                .OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var refs = doc.Descendants().Where(n =>
                    n.Name.LocalName == "ProjectReference")
                .Select(n => n.Attribute("Include")?.Value?.Replace('\\', '/'))
                .Where(x => x != null).Select(x => x!)
                .Select(pathValue => Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(path)!, pathValue)))
                .Select(abs => Path.GetRelativePath(root, abs).Replace('\\', '/'))
                .OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (defaultItems != "false"
                || !compiled.SequenceEqual(expectedSource, StringComparer.Ordinal)
                || !refs.SequenceEqual(project.ProjectReferences.OrderBy(r => r,
                        StringComparer.Ordinal), StringComparer.Ordinal))
            {
                mismatch = "Compile or ProjectReference set differs for "
                    + project.ProjectFile;
                return false;
            }
        }
        mismatch = string.Empty;
        return true;
    }

    private static IReadOnlyList<(string Path, byte[] Bytes)> CaptureSolutionFiles(
        string root, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("TARGET_SOLUTION_ROOT_MISSING");
        var entries = new List<(string Path, byte[] Bytes)>();
        long totalBytes = 0;
        Walk(root);
        return entries.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();

        void Walk(string dir)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException(
                    "TARGET_SOLUTION_SYMLINK_REJECTED");
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir)
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException(
                        "TARGET_SOLUTION_SYMLINK_REJECTED");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (Path.GetFileName(entry) is "bin" or "obj" or "TestResults")
                        continue;
                    Walk(entry);
                }
                else
                {
                    var relative = Path.GetRelativePath(root, entry)
                        .Replace('\\', '/');
                    var bytes = File.ReadAllBytes(entry);
                    totalBytes += bytes.Length;
                    entries.Add((relative, bytes));
                    if (entries.Count > SolutionFileLimit ||
                        totalBytes > SolutionBytesLimit)
                        throw new InvalidOperationException(
                            "TARGET_SOLUTION_TOO_LARGE: 256 files / 24 MiB limit.");
                }
            }
        }
    }

    private static bool SameSolutionFiles(
        TargetSolutionPlan plan,
        IReadOnlyList<(string Path, byte[] Bytes)> captured,
        out string mismatch)
    {
        var actual = captured.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
        var expected = plan.Files.OrderBy(f => f.RelativePath,
            StringComparer.Ordinal).ToArray();
        if (!actual.Select(f => f.Path).SequenceEqual(expected.Select(f =>
                f.RelativePath), StringComparer.Ordinal))
        {
            mismatch = "Expected [" + string.Join(", ", expected
                .Select(f => f.RelativePath).Take(24)) + "], found ["
                + string.Join(", ", actual.Select(f => f.Path).Take(24)) + "].";
            return false;
        }
        for (var i = 0; i < actual.Length; i++)
        {
            var sha = Convert.ToHexString(SHA256.HashData(
                actual[i].Bytes)).ToLowerInvariant();
            if (sha != expected[i].Sha256)
            {
                mismatch = "Changed target bytes: " + expected[i].RelativePath;
                return false;
            }
        }
        var hash = ContentTreeHasher.ComputeBytes(actual.Select(f =>
            (f.Path, f.Bytes)));
        if (hash != plan.TargetTreeHash)
        {
            mismatch = "Target tree hash does not match the planned solution revision.";
            return false;
        }
        mismatch = string.Empty;
        return true;
    }
}
