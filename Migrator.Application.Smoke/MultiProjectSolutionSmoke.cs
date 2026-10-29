using System.Text.Json.Nodes;
using System.Text.Json;
using Migrator.Application;
using Migrator.Core;

internal static class MultiProjectSolutionSmoke
{
    public static void Run()
    {
        var parent = Path.Combine(Path.GetTempPath(),
            "migrator-multiproject-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(parent, "source");
        var target = Path.Combine(parent, "target");
        Directory.CreateDirectory(source);
        try
        {
            Put("Foundation/Foundation.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            Put("Foundation/Sub/Counter.cs", """
                namespace Shared.Foundation;
                public static class Counter {
                    public static int RootCount;
                    public static int ScopeCount;
                }
                """);
            Put("Middle/Middle.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../Foundation/Foundation.csproj" />
                  </ItemGroup>
                </Project>
                """);
            Put("Middle/Checker.cs", """
                namespace Shared.Middle;
                public static class Checker {
                    public static bool Scoped() =>
                        Shared.Foundation.Counter.RootCount == 1 &&
                        Shared.Foundation.Counter.ScopeCount == 1;
                    public static bool Unscoped() =>
                        Shared.Foundation.Counter.RootCount == 1 &&
                        Shared.Foundation.Counter.ScopeCount == 0;
                }
                """);
            Put("TestsA/TestsA.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../Middle/Middle.csproj" />
                  </ItemGroup>
                </Project>
                """);
            Put("TestsB/TestsB.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../Middle/Middle.csproj" />
                  </ItemGroup>
                </Project>
                """);
            Put("TestsA/GlobalScope.cs", """
                using NUnit.Framework;
                [SetUpFixture] public class RootA {
                    [OneTimeSetUp] public void Before() { Recorder.StartRoot(); }
                    [OneTimeTearDown] public void After() { Recorder.StopRoot(); }
                }
                """);
            Put("TestsA/NamespaceScope.cs", """
                using NUnit.Framework;
                namespace Synthetic.A {
                    [SetUpFixture] public class NamespaceA {
                        [OneTimeSetUp] public void Before() { Recorder.StartScope(); }
                        [OneTimeTearDown] public void After() { Recorder.StopScope(); }
                    }
                }
                """);
            Put("TestsA/Tests.cs", """
                using NUnit.Framework;
                namespace Synthetic.A.Tests {
                    [TestFixture] public class SharedProjectFixture {
                        [Test] public void First() { Recorder.CheckScoped(); }
                        [Test] public void Second() { Recorder.CheckScoped(); }
                    }
                }
                """);
            Put("TestsB/GlobalScope.cs", """
                using NUnit.Framework;
                [SetUpFixture] public class RootB {
                    [OneTimeSetUp] public void Before() { Recorder.StartRoot(); }
                    [OneTimeTearDown] public void After() { Recorder.StopRoot(); }
                }
                """);
            Put("TestsB/Tests.cs", """
                using NUnit.Framework;
                namespace Synthetic.B.Tests {
                    [TestFixture] public class IndependentFixture {
                        [Test] public void Alone() { Recorder.CheckUnscoped(); }
                    }
                }
                """);
            // The source NUnit assemblies own independent scheduling policies.
            // They are declared in no-test source files and must survive
            // conversion as separate, hash-attested assembly-level artifacts.
            Put("TestsA/AssemblyScheduling.cs", """
                using NUnit.Framework;
                [assembly: Parallelizable(ParallelScope.Fixtures)]
                [assembly: LevelOfParallelism(2)]
                """);
            Put("TestsB/AssemblyScheduling.cs", """
                [assembly: NUnit.Framework.Parallelizable(NUnit.Framework.ParallelScope.Fixtures)]
                [assembly: NUnit.Framework.LevelOfParallelism(4)]
                """);

            var operations = new Dictionary<string, string[]>
            {
                ["Recorder.StartRoot()"] = new[] {
                    "Shared.Foundation.Counter.RootCount++;"
                },
                ["Recorder.StopRoot()"] = new[] {
                    "NUnit.Framework.Assert.That(Shared.Foundation.Counter.RootCount, NUnit.Framework.Is.EqualTo(1));"
                },
                ["Recorder.StartScope()"] = new[] {
                    "Shared.Foundation.Counter.ScopeCount++;"
                },
                ["Recorder.StopScope()"] = new[] {
                    "NUnit.Framework.Assert.That(Shared.Foundation.Counter.ScopeCount, NUnit.Framework.Is.EqualTo(1));"
                },
                ["Recorder.CheckScoped()"] = new[] {
                    "NUnit.Framework.Assert.That(Shared.Middle.Checker.Scoped(), NUnit.Framework.Is.True);",
                    "NUnit.Framework.Assert.That(System.Reflection.Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(NUnit.Framework.ParallelizableAttribute), false).Length, NUnit.Framework.Is.EqualTo(1));",
                    "NUnit.Framework.Assert.That(System.Reflection.Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(NUnit.Framework.LevelOfParallelismAttribute), false).Length, NUnit.Framework.Is.EqualTo(1));"
                },
                ["Recorder.CheckUnscoped()"] = new[] {
                    "NUnit.Framework.Assert.That(Shared.Middle.Checker.Unscoped(), NUnit.Framework.Is.True);",
                    "NUnit.Framework.Assert.That(System.Reflection.Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(NUnit.Framework.ParallelizableAttribute), false).Length, NUnit.Framework.Is.EqualTo(1));",
                    "NUnit.Framework.Assert.That(System.Reflection.Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(NUnit.Framework.LevelOfParallelismAttribute), false).Length, NUnit.Framework.Is.EqualTo(1));"
                }
            };
            var config = new ProjectAdapterConfig
            {
                SourceProjectName = "FictionalMultiProject",
                TestHost = new TestHostConfig
                {
                    BaseClass = "object",
                    ClassAttributes = new[] { "TestFixture", "NonParallelizable" }
                },
                Methods = operations.Select(pair => new MethodMapping
                {
                    SourceMethod = pair.Key,
                    TargetStatements = pair.Value,
                    RequiresReview = true
                }).ToArray()
            };
            var configPath = Path.Combine(parent, "adapter.json");
            File.WriteAllText(configPath, JsonSerializer.Serialize(config));
            var engine = new MigrationEngine();
            var plan = engine.PlanTargetSolution(source, configPath);
            Check(plan.Status == "ready_for_build"
                && plan.Projects.Count == 4
                && plan.Projects.Count(p => p.Kind == "library") == 2
                && plan.Projects.Count(p => p.Kind == "nunit") == 2
                && plan.Projects.Sum(p => p.DeclaredTests) == 3
                && plan.Projects.Sum(p => p.ScopedSetUpFixtures) == 3,
                "four separate projects and three NUnit scoped fixtures must remain owned");
            Check(plan.Files.Any(file => file.RelativePath
                    == "Foundation/Sub/Counter.cs"),
                "nested source library must be preserved as source-backed target C#");
            Check(plan.Files.Any(file => file.RelativePath == plan.SolutionFile)
                && plan.Files.Any(file => file.RelativePath == plan.ManifestFile),
                "target .slnx and exact manifest must be included");
            Check(plan.Projects.Single(p => p.SourceProject == "TestsA/TestsA.csproj")
                    .ProjectReferences.Single()
                == "Middle/Migrator.Generated.Library.csproj",
                "source ProjectReference must be rewritten to a distinct target library");
            Check(plan.Projects.Single(p => p.SourceProject == "Middle/Middle.csproj")
                    .ProjectReferences.Single()
                == "Foundation/Migrator.Generated.Library.csproj",
                "transitive shared library edges must remain in the target project graph");
            Check(plan.Files.Count(file => file.Content.Contains("[SetUpFixture]",
                    StringComparison.Ordinal)) == 3,
                "two global SetUpFixtures in DIFFERENT test assemblies may not merge or collide");
            Check(plan.Files.Single(file => file.RelativePath
                    == "TestsA/Migrator.Generated.Tests.csproj")
                .Content.Contains("<ProjectReference Include=\"../Middle/Migrator.Generated.Library.csproj\" />",
                    StringComparison.Ordinal),
                "generated NUnit project must reference the generated library, not the source .csproj");

            var schedulingA = plan.Files.Single(file =>
                file.RelativePath == "TestsA/Migrator.Generated.NUnitAssemblyScheduling.cs");
            var schedulingB = plan.Files.Single(file =>
                file.RelativePath == "TestsB/Migrator.Generated.NUnitAssemblyScheduling.cs");
            Check(schedulingA.Content.Contains(
                    "[assembly: NUnit.Framework.Parallelizable(NUnit.Framework.ParallelScope.Fixtures)]",
                    StringComparison.Ordinal)
                && schedulingA.Content.Contains(
                    "[assembly: NUnit.Framework.LevelOfParallelism(2)]",
                    StringComparison.Ordinal)
                && schedulingB.Content.Contains(
                    "[assembly: NUnit.Framework.LevelOfParallelism(4)]",
                    StringComparison.Ordinal)
                && !schedulingB.Content.Contains(
                    "LevelOfParallelism(2)", StringComparison.Ordinal)
                && plan.Files.Single(file =>
                    file.RelativePath == "TestsA/Migrator.Generated.Tests.csproj")
                    .Content.Contains(
                        "<Compile Include=\"Migrator.Generated.NUnitAssemblyScheduling.cs\" />",
                        StringComparison.Ordinal)
                && plan.AssemblyScheduling?.Count == 4
                && plan.AssemblyScheduling.Count(entry =>
                    entry.SourceProject == "TestsA/TestsA.csproj") == 2
                && plan.AssemblyScheduling.Count(entry =>
                    entry.SourceProject == "TestsB/TestsB.csproj") == 2
                && plan.Files.Single(file => file.RelativePath ==
                    plan.ManifestFile)
                    .Content.Contains("assemblyScheduling", StringComparison.Ordinal)
                && plan.Files.Single(file => file.RelativePath ==
                    plan.ManifestFile)
                    .Content.Contains("TestsA/AssemblyScheduling.cs", StringComparison.Ordinal),
                "independent NUnit assemblies require exact source-backed scheduling "
                + "attributes, provenance and explicit compile closure");

            var run = engine.MaterializeTargetSolution(
                source, target, plan.SourceTreeHash, plan.PlanId,
                configPath, runBuild: true, runNUnit: true);
            Check(run.Completeness.Status == "passed"
                && run.Build.Status == "passed"
                && run.NUnitRuntime.Status == "passed"
                && run.NUnitRuntime.ExecutedTests == 3
                && run.NUnitRuntime.PassedTests == 3
                && run.ProjectExecutions.Count == 2
                && run.ProjectExecutions.All(p => p.NUnitRuntime.Status == "passed"),
                "real multi-project build and per-assembly NUnit TRX must pass: "
                + run.Build.Details + "\n" + run.NUnitRuntime.Details
                + "\n" + string.Join("\n", run.ProjectExecutions.Select(p => p.NUnitRuntime.Details)));
            var reread = engine.VerifyTargetSolution(source, target,
                plan.SourceTreeHash, plan.PlanId, configPath);
            Check(reread.Completeness.Status == "passed"
                && reread.Build.Status == "not_run"
                && reread.NUnitRuntime.Status == "not_run"
                && reread.TargetTreeHash == plan.TargetTreeHash,
                "read-only verification cannot claim runtime by inheriting a prior run");

            // A migration entry selects the UI NUnit project and its complete
            // two-library transitive dependency closure. The unrelated test
            // assembly and its global SetUpFixture must NOT be migrated,
            // and their omission must be explicit in the plan/manifest.
            var scopedConfigPath = Path.Combine(parent, "scoped-adapter.json");
            var scopedConfig = JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
            scopedConfig["TargetSolutionEntryProjects"] =
                JsonSerializer.SerializeToNode(new[] { "TestsA/TestsA.csproj" });
            File.WriteAllText(scopedConfigPath, scopedConfig.ToJsonString());
            var selected = engine.PlanTargetSolution(source, scopedConfigPath);
            Check(selected.Status == "ready_for_build"
                && selected.Scope is {
                    Explicit: true,
                    EntryProjects.Count: 1,
                    IncludedProjects.Count: 3,
                    ExcludedProjects.Count: 1
                }
                && selected.Scope.EntryProjects[0] == "TestsA/TestsA.csproj"
                && selected.Scope.ExcludedProjects[0] == "TestsB/TestsB.csproj"
                && selected.Projects.Count == 3
                && selected.Projects.Sum(p => p.DeclaredTests) == 2
                && selected.Projects.Where(p => p.Kind == "nunit")
                    .All(p => p.SourceProject == "TestsA/TestsA.csproj")
                && selected.Files.All(file => !file.RelativePath.StartsWith(
                    "TestsB/", StringComparison.Ordinal))
                && selected.Files.Single(file =>
                    file.RelativePath == selected.ManifestFile).Content
                    .Contains("excludedProjects", StringComparison.OrdinalIgnoreCase)
                && selected.PlanId != plan.PlanId
                && selected.SourceTreeHash == plan.SourceTreeHash,
                "project scope must include all dependencies, reveal omissions, "
                + "keep original source hash, and exclude unrelated NUnit assembly");

            var scopedTarget = Path.Combine(parent, "scoped-target");
            var scopedExecuted = engine.MaterializeTargetSolution(
                source, scopedTarget, selected.SourceTreeHash, selected.PlanId,
                scopedConfigPath, runBuild: true, runNUnit: true);
            Check(scopedExecuted.Completeness.Status == "passed"
                && scopedExecuted.Build.Status == "passed"
                && scopedExecuted.NUnitRuntime.Status == "passed"
                && scopedExecuted.NUnitRuntime.ExecutedTests == 2
                && scopedExecuted.NUnitRuntime.PassedTests == 2
                && scopedExecuted.ProjectExecutions.Count == 1,
                "selected NUnit assembly must independently build and execute "
                + "2/2 tests; unrelated test project must not execute");

            var selectedVerification = engine.VerifyTargetSolution(source,
                scopedTarget, selected.SourceTreeHash, selected.PlanId,
                scopedConfigPath);
            Check(selectedVerification.Completeness.Status == "passed"
                && selectedVerification.Build.Status == "not_run"
                && selectedVerification.NUnitRuntime.Status == "not_run",
                "read-only scoped verification cannot inherit runtime evidence");

            // Adding another test project as a transitive dependency must
            // re-include it (and then reject a test-to-test reference), not
            // let explicit scope erase the edge or global SetUpFixture.
            var originalTestsA = File.ReadAllText(Path.Combine(
                source, "TestsA/TestsA.csproj"));
            Put("TestsA/TestsA.csproj", originalTestsA.Replace(
                "</ItemGroup>", "  <ProjectReference Include=\"../TestsB/TestsB.csproj\" />\n</ItemGroup>",
                StringComparison.Ordinal));
            var contaminated = engine.PlanTargetSolution(source, scopedConfigPath);
            Check(contaminated.Status == "blocked"
                && contaminated.Scope!.IncludedProjects.Count == 4
                && contaminated.Scope.ExcludedProjects.Count == 0
                && contaminated.Issues.Any(issue =>
                    issue.Code == "SOLUTION_REFERENCE_TO_TEST_ASSEMBLY"),
                "scope must reinclude and reject reachable NUnit test dependencies");
            Put("TestsA/TestsA.csproj", originalTestsA);

            // Unknown, invalid, empty or library-only entry definitions are
            // errors, never implicit permission to migrate everything.
            void ScopedNegative(string[] projects, string expected)
            {
                var changed = JsonNode.Parse(
                    File.ReadAllText(scopedConfigPath))!.AsObject();
                changed["TargetSolutionEntryProjects"] =
                    JsonSerializer.SerializeToNode(projects);
                File.WriteAllText(scopedConfigPath, changed.ToJsonString());
                var result = engine.PlanTargetSolution(source, scopedConfigPath);
                Check(result.Status == "blocked"
                    && result.Issues.Any(issue => issue.Code == expected),
                    "invalid entry list must fail closed: " + expected);
            }
            ScopedNegative(new[] { "../outside/Other.csproj" },
                "SOLUTION_SCOPE_CONFIG_INVALID");
            ScopedNegative(Array.Empty<string>(),
                "SOLUTION_SCOPE_CONFIG_INVALID");
            ScopedNegative(new[] { "TestsC/Unknown.csproj" },
                "SOLUTION_SCOPE_ENTRY_MISSING");
            ScopedNegative(new[] { "Foundation/Foundation.csproj" },
                "SOLUTION_SCOPE_ENTRY_NOT_TEST_PROJECT");
            File.WriteAllText(scopedConfigPath, scopedConfig.ToJsonString());

            void NegativeFile(string relative)
            {
                var path = Path.Combine(target, relative.Replace('/',
                    Path.DirectorySeparatorChar));
                var original = File.ReadAllBytes(path);
                File.Delete(path);
                ExpectIncomplete("deleted " + relative);
                File.WriteAllBytes(path, original);
                File.AppendAllText(path, "\n// unsafe change");
                ExpectIncomplete("edited " + relative);
                File.WriteAllBytes(path, original);
            }
            NegativeFile("TestsA/RootAPlaywright.cs");
            NegativeFile("TestsA/Migrator.Generated.NUnitAssemblyScheduling.cs");
            NegativeFile("TestsB/Migrator.Generated.Tests.csproj");
            NegativeFile(plan.SolutionFile);
            NegativeFile(plan.ManifestFile);
            NegativeFile("Foundation/Sub/Counter.cs");

            var rogue = Path.Combine(target, "TestsA", "Unlisted.cs");
            File.WriteAllText(rogue, "public class Unlisted {}");
            ExpectIncomplete("unexpected target C#");
            File.Delete(rogue);

            try
            {
                engine.MaterializeTargetSolution(source, target,
                    plan.SourceTreeHash, plan.PlanId, configPath);
                throw new Exception("Existing target overwritten");
            }
            catch (InvalidOperationException ex) when (
                ex.Message.StartsWith("TARGET_ALREADY_EXISTS", StringComparison.Ordinal)) { }

            // Unsafe expressions, duplicate declarations and fixture/test
            // scheduling are blockers. Never replace their behavior with a
            // convenient assembly-level default.
            var originalScheduling = File.ReadAllText(Path.Combine(source,
                "TestsA/AssemblyScheduling.cs"));
            Put("TestsA/AssemblyScheduling.cs", originalScheduling.Replace(
                "ParallelScope.Fixtures", "ParallelScope.Self | ParallelScope.Fixtures",
                StringComparison.Ordinal));
            ExpectProblem("NUNIT_ASSEMBLY_SCHEDULING_ARGUMENT_UNSUPPORTED");
            Put("TestsA/AssemblyScheduling.cs", originalScheduling);

            Put("TestsA/AssemblyScheduling.cs", originalScheduling.Replace(
                "LevelOfParallelism(2)", "LevelOfParallelism(0)",
                StringComparison.Ordinal));
            ExpectProblem("NUNIT_ASSEMBLY_SCHEDULING_ARGUMENT_UNSUPPORTED");
            Put("TestsA/AssemblyScheduling.cs", originalScheduling);

            Put("TestsA/DuplicateScheduling.cs", """
                using NUnit.Framework;
                [assembly: LevelOfParallelism(3)]
                """);
            ExpectProblem("NUNIT_ASSEMBLY_SCHEDULING_DUPLICATE");
            File.Delete(Path.Combine(source, "TestsA/DuplicateScheduling.cs"));

            Put("TestsA/ConflictingScheduling.cs", """
                using NUnit.Framework;
                [assembly: NonParallelizable]
                """);
            ExpectProblem("NUNIT_ASSEMBLY_SCHEDULING_CONFLICT");
            File.Delete(Path.Combine(source, "TestsA/ConflictingScheduling.cs"));

            Put("TestsA/FixtureScheduling.cs", """
                using NUnit.Framework;
                [TestFixture, Parallelizable(ParallelScope.All)]
                public class ScheduledFixture {
                    [Test] public void A() {}
                }
                """);
            ExpectProblem("NUNIT_FIXTURE_SCHEDULING_REQUIRES_REVIEW");
            File.Delete(Path.Combine(source, "TestsA/FixtureScheduling.cs"));

            Put("TestsA/AliasScheduling.cs", """
                using PN = NUnit.Framework.ParallelizableAttribute;
                [assembly: PN(NUnit.Framework.ParallelScope.Fixtures)]
                """);
            ExpectProblem("NUNIT_ASSEMBLY_SCHEDULING_ALIAS_UNRESOLVED");
            File.Delete(Path.Combine(source, "TestsA/AliasScheduling.cs"));

            var originalMiddle = File.ReadAllText(Path.Combine(source,
                "Middle/Middle.csproj"));
            Put("Middle/Middle.csproj", originalMiddle.Replace("</ItemGroup>",
                "  <ProjectReference Include=\"../TestsA/TestsA.csproj\" />\n</ItemGroup>",
                StringComparison.Ordinal));
            ExpectProblem("SOLUTION_REFERENCE_TO_TEST_ASSEMBLY");
            Put("Middle/Middle.csproj", originalMiddle);

            var originalFoundation = File.ReadAllText(Path.Combine(source,
                "Foundation/Foundation.csproj"));
            Put("Foundation/Foundation.csproj", originalFoundation.Replace(
                "</Project>", "  <ItemGroup><ProjectReference Include=\"../Middle/Middle.csproj\" /></ItemGroup>\n</Project>",
                StringComparison.Ordinal));
            ExpectProblem("SOLUTION_PROJECT_REFERENCE_CYCLE");
            Put("Foundation/Foundation.csproj", originalFoundation);

            // Package-bearing shared libraries cannot be copied to a package-free
            // target library even if their C# source is syntactically harmless.
            Put("Foundation/Foundation.csproj", originalFoundation.Replace(
                "</Project>",
                "  <ItemGroup><PackageReference Include=\"Synthetic.Optional\" Version=\"1.0.0\" /></ItemGroup>\n</Project>",
                StringComparison.Ordinal));
            ExpectProblem("SOLUTION_LIBRARY_PACKAGE_UNSUPPORTED");
            var failFast = engine.PlanTargetSolution(source, configPath);
            Check(failFast.Status == "blocked"
                && failFast.Projects.Where(project => project.Kind == "nunit")
                    .All(project => project.CandidateId == null)
                && failFast.Issues.Any(issue =>
                    issue.Code == "SOLUTION_NUNIT_PLANNING_DEFERRED"),
                "invalid shared dependency must be detected before any expensive NUnit plan");

            // A reviewer may opt in to copying only a literal, exact package
            // version from a source-safe shared library. The allowlist itself
            // is part of the content-addressed configuration, never inferred
            // from the source dependency name alone.
            var safePackageProject = originalFoundation.Replace(
                "</Project>",
                "  <ItemGroup><PackageReference Include=\"System.Collections.Immutable\" Version=\"9.0.0\" /></ItemGroup>\n</Project>",
                StringComparison.Ordinal);
            Put("Foundation/Foundation.csproj", safePackageProject);
            var configOriginal = File.ReadAllText(configPath);
            var configReviewed = JsonNode.Parse(configOriginal)!.AsObject();
            configReviewed["TargetSolutionReviewedPackages"] =
                JsonSerializer.SerializeToNode(new[] {
                    new {
                        SourceProject = "Foundation/Foundation.csproj",
                        Reviewed = true,
                        Packages = new[] {
                            new { Id = "System.Collections.Immutable", Version = "9.0.0" }
                        }
                    }
                });
            File.WriteAllText(configPath, configReviewed.ToJsonString());
            var reviewed = engine.PlanTargetSolution(source, configPath);
            Check(reviewed.Status == "ready_for_build"
                && reviewed.Projects.Single(project => project.SourceProject
                        == "Foundation/Foundation.csproj")
                    .PackageReferences!.Single().Version == "9.0.0"
                && reviewed.Files.Single(file => file.RelativePath ==
                        "Foundation/Migrator.Generated.Library.csproj")
                    .Content.Contains(
                        "<PackageReference Include=\"System.Collections.Immutable\" Version=\"9.0.0\" />",
                        StringComparison.Ordinal),
                "exact reviewer-approved source package must appear in target library");

            Put("Foundation/Foundation.csproj", safePackageProject.Replace(
                "Version=\"9.0.0\"", "Version=\"9.0.1\"",
                StringComparison.Ordinal));
            ExpectProblem("SOLUTION_PACKAGE_REVIEW_MISMATCH");
            Put("Foundation/Foundation.csproj", safePackageProject.Replace(
                "Version=\"9.0.0\"", "Version=\"9.*\"",
                StringComparison.Ordinal));
            ExpectProblem("SOLUTION_PACKAGE_REFERENCE_DYNAMIC");

            Put("Foundation/Foundation.csproj", safePackageProject);
            var unsafeConfig = JsonNode.Parse(
                File.ReadAllText(configPath))!.AsObject();
            unsafeConfig["TargetSolutionReviewedPackages"] =
                JsonSerializer.SerializeToNode(new[] {
                    new {
                        SourceProject = "../outside.csproj",
                        Reviewed = true,
                        Packages = new[] {
                            new { Id = "System.Collections.Immutable", Version = "9.0.0" }
                        }
                    }
                });
            File.WriteAllText(configPath, unsafeConfig.ToJsonString());
            ExpectProblem("SOLUTION_PACKAGE_REVIEW_PROFILE_INVALID");

            // Both the source graph and config must return to their exact
            // original state for the remaining independent negative tests.
            File.WriteAllText(configPath, configOriginal);
            Put("Foundation/Foundation.csproj", originalFoundation);

            // A blocked source graph must still report the true number of
            // NUnit method declarations; zero generated candidates is not
            // evidence that the source contains zero tests.
            Put("TestsA/ExtraAndNested.cs", """
                using NUnit.Framework;
                public class SuiteContainer {
                    [SetUpFixture] public class InvalidNestedHook {
                        [OneTimeTearDown] public void Stop() {}
                    }
                }
                [TestFixture] public class ExtraFixture {
                    [Test] public void Extra() {}
                }
                """);
            var unsafeScope = engine.PlanTargetSolution(source, configPath);
            Check(unsafeScope.Status == "blocked"
                && unsafeScope.Issues.Any(issue =>
                    issue.Code == "SOLUTION_NESTED_SETUPFIXTURE_UNSUPPORTED")
                && unsafeScope.Projects.Single(project => project.SourceProject
                    == "TestsA/TestsA.csproj").DeclaredTests == 3
                && unsafeScope.Projects.Single(project => project.SourceProject
                    == "TestsA/TestsA.csproj").CandidateId == null,
                "lexical NUnit method count survives a blocked nested setup scope");
            File.Delete(Path.Combine(source, "TestsA/ExtraAndNested.cs"));

            Put("TestsA/AssemblyScheduling.cs", """
                using NUnit.Framework;
                [assembly: Parallelizable(ParallelScope.Fixtures)]
                [assembly: LevelOfParallelism(3)]
                """);
            var parallel = engine.PlanTargetSolution(source, configPath);
            Check(parallel.Status == "blocked"
                && parallel.Issues.Count(issue =>
                    issue.Code == "SOLUTION_ASSEMBLY_SCHEDULING_REQUIRES_REVIEW") == 2,
                "source assembly parallel policy cannot disappear silently");
            File.Delete(Path.Combine(source, "TestsA/AssemblyScheduling.cs"));

            Put("TestsA/SourceCases.cs", """
                using NUnit.Framework;
                [TestFixture] public class DataCases {
                    [TestCaseSource("Rows")] public void A(int value) {}
                }
                """);
            ExpectProblem("SOLUTION_DYNAMIC_TEST_DATA_REQUIRES_REVIEW");
            File.Delete(Path.Combine(source, "TestsA/SourceCases.cs"));

            Put("TestsA/Disabled.cs", """
                using NUnit.Framework;
                [TestFixture] public class Disabled {
                    [Test] [Ignore("paused")] public void A() {}
                }
                """);
            ExpectProblem("SOLUTION_NUNIT_EXECUTION_GATE_REQUIRES_REVIEW");
            File.Delete(Path.Combine(source, "TestsA/Disabled.cs"));

            Put("TestsA/Unsupported.cs", """
                public class Incompatible {
                    [Fact] public void XUnitOnly() {}
                }
                """);
            ExpectProblem("SOLUTION_UNSUPPORTED_XUNIT_SOURCE");
            File.Delete(Path.Combine(source, "TestsA/Unsupported.cs"));

            // A package-free library carrying Selenium driver types also
            // remains blocked; no implicit Selenium runtime may be transplanted.
            Put("Foundation/DriverLeak.cs", """
                using OpenQA.Selenium;
                public sealed class DriverLeak {
                    public IWebDriver Driver { get; }
                }
                """);
            ExpectProblem("SOLUTION_LIBRARY_TEST_LIFECYCLE_UNSAFE");
            File.Delete(Path.Combine(source, "Foundation/DriverLeak.cs"));

            var sourceAProject = File.ReadAllText(Path.Combine(source,
                "TestsA/TestsA.csproj"));
            Put("TestsA/TestsA.csproj", sourceAProject.Replace("</ItemGroup>",
                "  <ProjectReference Include=\"../../../outside/Foreign.csproj\" />\n</ItemGroup>",
                StringComparison.Ordinal));
            ExpectProblem("SOLUTION_REFERENCE_EXTERNAL");
            Put("TestsA/TestsA.csproj", sourceAProject);
            Put("TestsA/TestsA.csproj", sourceAProject.Replace(
                "</Project>", "<Import Project=\"outside.props\" /></Project>",
                StringComparison.Ordinal));
            ExpectProblem("SOLUTION_DYNAMIC_MSBUILD_UNSUPPORTED");
            Put("TestsA/TestsA.csproj", sourceAProject);

            Put("Foundation/HiddenScope.cs", """
                using NUnit.Framework;
                [SetUpFixture] public class UnsafeLibraryScopedHook {
                    [OneTimeSetUp] public void Start() {}
                }
                """);
            ExpectProblem("SOLUTION_REFERENCE_TO_TEST_ASSEMBLY");
            File.Delete(Path.Combine(source, "Foundation/HiddenScope.cs"));

            try
            {
                engine.PlanTargetSolution(source, configPath, "stale-sha");
                throw new Exception("Stale source accepted");
            }
            catch (InvalidOperationException ex) when (
                ex.Message.StartsWith("STALE_SOLUTION_SOURCE", StringComparison.Ordinal)) { }

            try
            {
                engine.MaterializeTargetSolution(source,
                    Path.Combine(source, "generated"),
                    plan.SourceTreeHash, plan.PlanId, configPath);
                throw new Exception("Overlapping target accepted");
            }
            catch (InvalidOperationException ex) when (
                ex.Message.StartsWith("OVERLAPPING_SOURCE_AND_TARGET",
                    StringComparison.Ordinal)) { }

            void Put(string relative, string content)
            {
                var path = Path.Combine(source, relative.Replace('/',
                    Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content);
            }

            void ExpectProblem(string code)
            {
                var p = engine.PlanTargetSolution(source, configPath);
                Check(p.Status == "blocked" && p.Issues.Any(i => i.Code == code),
                    "unsafe solution graph must be blocked: " + code
                    + "; actual " + string.Join(", ", p.Issues.Select(i => i.Code)));
            }

            void ExpectIncomplete(string reason)
            {
                var verified = engine.VerifyTargetSolution(source, target,
                    plan.SourceTreeHash, plan.PlanId, configPath,
                    runBuild: true, runNUnit: true);
                Check(verified.Completeness.Status != "passed"
                    && verified.Build.Status == "blocked"
                    && verified.NUnitRuntime.Status == "blocked",
                    "incomplete target graph must not build or execute NUnit: " + reason);
            }
        }
        finally
        {
            if (Directory.Exists(parent))
                Directory.Delete(parent, recursive: true);
        }
    }

    static void Check(bool pass, string reason)
    {
        if (!pass)
            throw new InvalidOperationException(
                "Multi-project solution smoke: " + reason);
    }
}
