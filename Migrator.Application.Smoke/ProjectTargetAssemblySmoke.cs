using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Migrator.Application;
using Migrator.Core;

internal static class ProjectTargetAssemblySmoke
{
    public static void Run()
    {
        var parent = Path.Combine(Path.GetTempPath(), "migrator-generated-assembly-"
            + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(parent, "source");
        var target = Path.Combine(parent, "target");
        Directory.CreateDirectory(source);
        try
        {
            File.WriteAllText(Path.Combine(source, "Example.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            WriteSource("GlobalHooks.cs", """
                using NUnit.Framework;
                [SetUpFixture] public class GlobalHooks {
                    [OneTimeSetUp] public void Start() { Recorder.GlobalStart(); }
                    [OneTimeTearDown] public void End() { Recorder.GlobalEnd(); }
                }
                """);
            WriteSource("ParentHooks.cs", """
                using NUnit.Framework;
                namespace Demo {
                    [SetUpFixture] public class ParentHooks {
                        [OneTimeSetUp] public void Start() { Recorder.ParentStart(); }
                        [OneTimeTearDown] public void End() { Recorder.ParentEnd(); }
                    }
                }
                """);
            WriteSource("ChildHooks.cs", """
                using NUnit.Framework;
                namespace Demo.Tests {
                    [SetUpFixture] public class ChildHooks {
                        [OneTimeSetUp] public void Start() { Recorder.ChildStart(); }
                        [OneTimeTearDown] public void End() { Recorder.ChildEnd(); }
                    }
                }
                """);
            WriteSource("AlphaTests.cs", """
                using NUnit.Framework;
                namespace Demo.Tests {
                    [TestFixture] public class AlphaTests {
                        [Test] public void One() { Recorder.FirstTest(); }
                    }
                }
                """);
            WriteSource("BetaTests.cs", """
                using NUnit.Framework;
                namespace Demo.Tests {
                    [TestFixture] public class BetaTests {
                        [Test] public void Two() { Recorder.SecondTest(); }
                    }
                }
                """);
            WriteSource("SiblingTests.cs", """
                using NUnit.Framework;
                namespace DemoElse {
                    [TestFixture] public class SiblingTests {
                        [Test] public void OnlyGlobal() { Recorder.SiblingTest(); }
                    }
                }
                """);

            var mappings = new Dictionary<string, string[]>
            {
                ["Recorder.GlobalStart()"] = new[]
                {
                    "System.AppContext.SetData(\"rootCount\", ((int?)System.AppContext.GetData(\"rootCount\") ?? 0) + 1);"
                },
                ["Recorder.ParentStart()"] = new[]
                {
                    "NUnit.Framework.Assert.That(System.AppContext.GetData(\"rootCount\"), NUnit.Framework.Is.EqualTo(1));",
                    "System.AppContext.SetData(\"parentCount\", ((int?)System.AppContext.GetData(\"parentCount\") ?? 0) + 1);"
                },
                ["Recorder.ChildStart()"] = new[]
                {
                    "NUnit.Framework.Assert.That(System.AppContext.GetData(\"parentCount\"), NUnit.Framework.Is.EqualTo(1));",
                    "System.AppContext.SetData(\"childCount\", ((int?)System.AppContext.GetData(\"childCount\") ?? 0) + 1);"
                },
                ["Recorder.FirstTest()"] = new[]
                {
                    "NUnit.Framework.Assert.That(System.AppContext.GetData(\"rootCount\"), NUnit.Framework.Is.EqualTo(1));",
                    "NUnit.Framework.Assert.That(System.AppContext.GetData(\"parentCount\"), NUnit.Framework.Is.EqualTo(1));",
                    "NUnit.Framework.Assert.That(System.AppContext.GetData(\"childCount\"), NUnit.Framework.Is.EqualTo(1));",
                    "System.AppContext.SetData(\"firstDone\", true);"
                },
                ["Recorder.SecondTest()"] = new[]
                {
                    "NUnit.Framework.Assert.That(System.AppContext.GetData(\"rootCount\"), NUnit.Framework.Is.EqualTo(1));",
                    "NUnit.Framework.Assert.That(System.AppContext.GetData(\"parentCount\"), NUnit.Framework.Is.EqualTo(1));",
                    "NUnit.Framework.Assert.That(System.AppContext.GetData(\"childCount\"), NUnit.Framework.Is.EqualTo(1));",
                    "System.AppContext.SetData(\"secondDone\", true);"
                },
                ["Recorder.SiblingTest()"] = new[]
                {
                    "NUnit.Framework.Assert.That(System.AppContext.GetData(\"rootCount\"), NUnit.Framework.Is.EqualTo(1));",
                    "System.AppContext.SetData(\"siblingDone\", true);"
                },
                ["Recorder.ChildEnd()"] = new[]
                {
                    "NUnit.Framework.Assert.That(System.AppContext.GetData(\"firstDone\"), NUnit.Framework.Is.EqualTo(true));",
                    "NUnit.Framework.Assert.That(System.AppContext.GetData(\"secondDone\"), NUnit.Framework.Is.EqualTo(true));",
                    "System.AppContext.SetData(\"childEnd\", true);"
                },
                ["Recorder.ParentEnd()"] = new[]
                {
                    "NUnit.Framework.Assert.That(System.AppContext.GetData(\"childEnd\"), NUnit.Framework.Is.EqualTo(true));",
                    "System.AppContext.SetData(\"parentEnd\", true);"
                },
                ["Recorder.GlobalEnd()"] = new[]
                {
                    "NUnit.Framework.Assert.That(System.AppContext.GetData(\"parentEnd\"), NUnit.Framework.Is.EqualTo(true));",
                    "NUnit.Framework.Assert.That(System.AppContext.GetData(\"siblingDone\"), NUnit.Framework.Is.EqualTo(true));",
                    "System.AppContext.SetData(\"globalEnd\", true);"
                }
            };
            var config = new ProjectAdapterConfig
            {
                SourceProjectName = "SyntheticAssembly",
                TestHost = new TestHostConfig
                {
                    BaseClass = "object",
                    ClassAttributes = new[] { "TestFixture", "NonParallelizable" }
                },
                Methods = mappings.Select(entry => new MethodMapping
                {
                    SourceMethod = entry.Key,
                    TargetStatements = entry.Value,
                    RequiresReview = true
                }).ToArray()
            };
            var configPath = Path.Combine(parent, "adapter.json");
            File.WriteAllText(configPath, JsonSerializer.Serialize(config));

            var engine = new MigrationEngine();
            var plan = engine.PlanTargetAssembly(source, configPath);
            Check(plan.Status == "ready_for_build"
                && plan.ScopedSetUpFixtures == 3 && plan.DeclaredTests == 3
                && plan.Files.Count == 8,
                "one target assembly must contain all 6 generated C# sources, one .csproj and one manifest");
            var project = plan.Files.Single(file =>
                file.RelativePath == plan.ProjectFile).Content;
            Check(project.Contains("<EnableDefaultCompileItems>false</EnableDefaultCompileItems>",
                StringComparison.Ordinal) && Occurrences(project, "<Compile Include=") == 6,
                "target .csproj must list every generated file with no implicit glob");
            Check(plan.Files.Count(file => file.Content.Contains("[SetUpFixture]",
                StringComparison.Ordinal)) == 3,
                "all three SetUpFixtures must be included in one target assembly");
            Check(plan.Files.Single(file => file.RelativePath == "SiblingTestsPlaywright.cs")
                    .Content.Contains("namespace DemoElse;", StringComparison.Ordinal),
                "a similarly named sibling namespace cannot inherit Demo scope");

            var generated = engine.MaterializeTargetAssembly(source, target,
                plan.AnalysisId, plan.CandidateId, configPath,
                runBuild: true, runNUnit: true);
            Check(generated.Completeness.Status == "passed"
                && generated.Build.Status == "passed"
                && generated.NUnitRuntime.Status == "passed"
                && generated.NUnitRuntime.ExecutedTests == 3
                && generated.NUnitRuntime.PassedTests == 3
                && generated.Status == "runtime_passed_review_required",
                "real dotnet build and exactly three NUnit tests must pass: "
                + generated.Build.Details + "\n" + generated.NUnitRuntime.Details);

            var verified = engine.VerifyTargetAssembly(source, target,
                plan.AnalysisId, plan.CandidateId, configPath);
            Check(verified.Completeness.Status == "passed"
                && verified.Build.Status == "not_run"
                && verified.NUnitRuntime.Status == "not_run"
                && verified.TargetTreeHash == plan.TargetTreeHash,
                "a later read-only verification must preserve content identity without inventing execution");

            try
            {
                engine.MaterializeTargetAssembly(source, target,
                    plan.AnalysisId, plan.CandidateId, configPath);
                throw new Exception("Existing target was overwritten");
            }
            catch (InvalidOperationException ex) when (
                ex.Message.StartsWith("TARGET_ALREADY_EXISTS", StringComparison.Ordinal)) { }

            var missingFile = Path.Combine(target, "ChildHooksPlaywright.cs");
            var original = File.ReadAllBytes(missingFile);
            File.Delete(missingFile);
            CheckBlocked("missing SetUpFixture source file");
            File.WriteAllBytes(missingFile, original);

            var rogue = Path.Combine(target, "Unexpected.cs");
            File.WriteAllText(rogue, "public class Unexpected {}");
            CheckBlocked("unexpected C# file in strict target tree");
            File.Delete(rogue);

            var assemblyProject = Path.Combine(target, plan.ProjectFile);
            var originalProject = File.ReadAllBytes(assemblyProject);
            File.WriteAllText(assemblyProject,
                File.ReadAllText(assemblyProject).Replace(
                    "    <Compile Include=\"ChildHooksPlaywright.cs\" />\n",
                    "", StringComparison.Ordinal));
            CheckBlocked("mutated SDK project compile item list");
            File.WriteAllBytes(assemblyProject, originalProject);

            var newProject = Path.Combine(source, "Other.csproj");
            File.WriteAllText(newProject,
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            var rejected = engine.PlanTargetAssembly(source, configPath);
            Check(rejected.Status == "blocked"
                && rejected.Issues.Any(i => i.Code == "SOURCE_PROJECT_OWNERSHIP_AMBIGUOUS"),
                "two source .csproj files cannot be silently merged into one NUnit assembly");
            File.Delete(newProject);

            try
            {
                engine.MaterializeTargetAssembly(source,
                    Path.Combine(source, "Generated"),
                    plan.AnalysisId, plan.CandidateId, configPath);
                throw new Exception("Target was written into source directory");
            }
            catch (InvalidOperationException ex) when (
                ex.Message.StartsWith("OVERLAPPING_SOURCE_AND_TARGET",
                    StringComparison.Ordinal)) { }

            void WriteSource(string name, string body) =>
                File.WriteAllText(Path.Combine(source, name), body);

            void CheckBlocked(string reason)
            {
                var result = engine.VerifyTargetAssembly(source, target,
                    plan.AnalysisId, plan.CandidateId, configPath,
                    runBuild: true, runNUnit: true);
                Check(result.Completeness.Status != "passed"
                    && result.Build.Status == "blocked"
                    && result.NUnitRuntime.Status == "blocked",
                    "incomplete assembly must never build/run: " + reason);
            }
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    private static int Occurrences(string text, string token) =>
        text.Split(token, StringSplitOptions.None).Length - 1;

    private static void Check(bool pass, string why)
    {
        if (!pass)
            throw new InvalidOperationException("Project assembly smoke: " + why);
    }
}
