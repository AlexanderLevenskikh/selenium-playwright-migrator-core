using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Migrator.Application;
using Migrator.Core;
using Migrator.Core.Models;
using Migrator.PlaywrightDotNet;
using Migrator.Roslyn;
using Migrator.SeleniumCSharp;

internal static class ScopedSetUpFixtureSmoke
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "nunit-scoped-"
            + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "Synthetic.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            var global = Path.Combine(root, "AssemblyHooks.cs");
            var outer = Path.Combine(root, "OuterHooks.cs");
            var inner = Path.Combine(root, "InnerHooks.cs");
            var test = Path.Combine(root, "ProjectTests.cs");
            var sibling = Path.Combine(root, "UnrelatedTests.cs");
            File.WriteAllText(global, """
                using NUnit.Framework;
                [SetUpFixture] public class AssemblyHooks
                {
                    [OneTimeSetUp] public void Start() { Recorder.Track("assembly-start"); }
                    [OneTimeTearDown] public void Stop() { Recorder.Track("assembly-stop"); }
                }
                """);
            File.WriteAllText(outer, """
                using NUnit.Framework;
                namespace Sample {
                    [SetUpFixture] public class OuterHooks
                    {
                        [OneTimeSetUp] public void Start() { Recorder.Track("outer-start"); }
                        [OneTimeTearDown] public void Stop() { Recorder.Track("outer-stop"); }
                    }
                }
                """);
            File.WriteAllText(inner, """
                using NUnit.Framework;
                namespace Sample.Tests {
                    [SetUpFixture] public class InnerHooks
                    {
                        [OneTimeSetUp] public void Start() { Recorder.Track("inner-start"); }
                        [OneTimeTearDown] public void Stop() { Recorder.Track("inner-stop"); }
                    }
                }
                """);
            File.WriteAllText(test, """
                using NUnit.Framework;
                namespace Sample.Tests {
                    [TestFixture] public class ProjectTests {
                        [Test] public void ShouldKeepAssemblyScope() {
                            Recorder.Track("test");
                        }
                    }
                }
                """);
            File.WriteAllText(sibling, """
                using NUnit.Framework;
                namespace SampleElse {
                    [TestFixture] public class UnrelatedTests {
                        [Test] public void MustNotInheritSiblingScope() {
                            Recorder.Track("test");
                        }
                    }
                }
                """);

            var tokens = new[] { "assembly-start", "assembly-stop",
                "outer-start", "outer-stop", "inner-start", "inner-stop", "test" };
            var config = new ProjectAdapterConfig
            {
                SourceProjectName = "Synthetic",
                Methods = tokens.Select(token => new MethodMapping
                {
                    SourceMethod = "Recorder.Track(\"" + token + "\")",
                    TargetStatements = new[] { "System.Console.WriteLine(\"" + token + "\");" },
                    RequiresReview = true
                }).ToArray()
            };

            PipelineResult Migrate(string file, ProjectAdapterConfig? current = null) =>
                new MigrationPipeline(new RoslynTestFileParser(current ?? config),
                    new PlaywrightDotNetRenderer(),
                    new DefaultProjectAdapter(current ?? config)).ProcessFile(file);

            var parser = new RoslynTestFileParser(config);
            var parsed = parser.Parse(test);
            var scoped = parsed.ScopedSetUp;
            Check(scoped is { Declaration: null } && scoped.Applicable.Count == 3,
                "dependent source fixture must discover assembly + outer + inner scope");
            Check(scoped!.Applicable.Select(e => e.SourceNamespace)
                .SequenceEqual(new[] { "", "Sample", "Sample.Tests" }),
                "scope dependencies must be ordered outermost to innermost by namespace segments");
            var testResult = Migrate(test);
            var rendered = testResult.GeneratedOutput;
            Check(rendered.Contains("namespace Sample.Tests;", StringComparison.Ordinal)
                && !rendered.Contains("namespace Sample.Tests.Playwright;", StringComparison.Ordinal),
                "scoped target test namespace must preserve original containment hierarchy");
            var a = rendered.IndexOf("AssemblyHooks", StringComparison.Ordinal);
            var b = rendered.IndexOf("OuterHooks", StringComparison.Ordinal);
            var c = rendered.IndexOf("InnerHooks", StringComparison.Ordinal);
            Check(a >= 0 && a < b && b < c,
                "dependency evidence must preserve global → outer → inner order");
            Check(Count(rendered, "[MIGRATOR:NUNIT_SETUPFIXTURE_DEPENDENCY_REQUIRES_REVIEW]") == 3
                && !rendered.Contains("assembly-start", StringComparison.Ordinal)
                && !rendered.Contains("outer-start", StringComparison.Ordinal)
                && !rendered.Contains("inner-start", StringComparison.Ordinal),
                "scoped effects must never be duplicated in individual test fixture code");
            Check(testResult.Report.FullyConvertedTests == 0,
                "a suite deployment dependency is never silently accepted");

            var unrelated = parser.Parse(sibling).ScopedSetUp;
            Check(unrelated != null && unrelated.Applicable.Count == 1
                && unrelated.Applicable.Single().SourceNamespace == "",
                "namespace prefix 'Sample' must NOT apply to 'SampleElse'");

            foreach (var (source, expected, ns) in new[] {
                (global, "assembly-start", ""),
                (outer, "outer-start", "Sample"),
                (inner, "inner-start", "Sample.Tests")
            })
            {
                var result = Migrate(source);
                Check(result.SourceModel.Tests.Count() == 0
                    && result.SourceModel.ScopedSetUp?.Declaration?.SourceNamespace == ns,
                    "standalone SetUpFixture must be first-class migration input");
                var output = result.GeneratedOutput;
                Check(output.Contains("[SetUpFixture]", StringComparison.Ordinal)
                    && output.Contains("[OneTimeSetUp]", StringComparison.Ordinal)
                    && output.Contains("[OneTimeTearDown]", StringComparison.Ordinal)
                    && output.Contains("System.Console.WriteLine(\"" + expected + "\");",
                        StringComparison.Ordinal)
                    && !output.Contains(": PageTest", StringComparison.Ordinal),
                    "standalone target scope must retain one-time effects without PageTest inheritance");
                Check(!output.Contains("namespace " + ns + ".Playwright;", StringComparison.Ordinal),
                    "generated scoped namespace cannot change assembly containment");
                Check(!CSharpSyntaxTree.ParseText(output).GetDiagnostics()
                    .Any(d => d.Severity == DiagnosticSeverity.Error),
                    "standalone generated SetupFixture must have valid C# syntax");
                Check(result.Report.TodoComments > 0,
                    "source/target once-per-scope behavior must remain reviewed");
            }

            var engine = new MigrationEngine();
            var analysis = engine.Analyze(root);
            Check(analysis.Files.Any(f => f.RelativePath == "AssemblyHooks.cs"
                    && f.Disposition == "supported" && f.DeclaredTests == 0),
                "MCP discovery must not discard no-test SetUpFixture declarations");
            var preview = engine.Preview(root, expectedAnalysisId: analysis.AnalysisId);
            Check(preview.Files.Count(f => f.SourcePath.EndsWith("Hooks.cs", StringComparison.Ordinal)) == 3
                && preview.TransformationStatus == "partial",
                "preview must produce one distinct artifact for each source scoped SetUpFixture");

            // A forgotten one-time teardown must prevent *all* suite
            // execution, not report failure after tests appeared successful.
            var missingTeardown = new ProjectAdapterConfig
            {
                SourceProjectName = "Synthetic",
                Methods = config.Methods.Where(method =>
                    method.SourceMethod != "Recorder.Track(\"outer-stop\")").ToArray()
            };
            var incomplete = Migrate(outer, missingTeardown).GeneratedOutput;
            Check(incomplete.Contains("[MIGRATOR:NUNIT_SETUPFIXTURE_ACTION_REQUIRES_MAPPING]",
                      StringComparison.Ordinal)
                && incomplete.Contains("Assert.Inconclusive(", StringComparison.Ordinal)
                && !incomplete.Contains("System.Console.WriteLine(\"outer-start\");",
                    StringComparison.Ordinal),
                "unmapped one-time cleanup must block scope before executing setup side effects");

            var pageDependent = new ProjectAdapterConfig
            {
                SourceProjectName = "Synthetic",
                Methods = config.Methods.Where(method =>
                    method.SourceMethod != "Recorder.Track(\"outer-start\")").Append(
                    new MethodMapping
                    {
                        SourceMethod = "Recorder.Track(\"outer-start\")",
                        TargetStatements = new[] {
                            "await Page.GotoAsync(\"https://example.invalid/source-only\");"
                        },
                        RequiresReview = true
                    }).ToArray()
            };
            var pageScope = Migrate(outer, pageDependent).GeneratedOutput;
            Check(pageScope.Contains("[MIGRATOR:NUNIT_SETUPFIXTURE_ACTION_REQUIRES_MAPPING]",
                      StringComparison.Ordinal)
                && !pageScope.Contains("await Page.GotoAsync(", StringComparison.Ordinal),
                "namespace setup has no per-test Playwright Page context");

            var incompatibleNamespace = new ProjectAdapterConfig
            {
                SourceProjectName = "Synthetic",
                Methods = config.Methods,
                TestHost = new TestHostConfig { Namespace = "DifferentNamespace" }
            };
            try
            {
                Migrate(test, incompatibleNamespace);
                throw new InvalidOperationException("Changed namespace scope was accepted");
            }
            catch (InvalidOperationException ex) when (
                ex.Message.StartsWith("NUNIT_SETUPFIXTURE_NAMESPACE_CHANGED",
                    StringComparison.Ordinal)) { }

            // Two setup fixtures in the same namespace: no stable order.
            var duplicate = Path.Combine(root, "DuplicateHooks.cs");
            File.WriteAllText(duplicate, """
                using NUnit.Framework;
                namespace Sample {
                    [SetUpFixture] public class DuplicateHooks {
                        [OneTimeSetUp] public void Start() { Recorder.Track("outer-start"); }
                    }
                }
                """);
            CheckBlocked(test, "NUNIT_SETUPFIXTURE_SAME_SCOPE_ORDER");
            CheckBlocked(outer, "NUNIT_SETUPFIXTURE_SAME_SCOPE_ORDER");
            File.Delete(duplicate);

            // A source setup fixture with nonportable state must not execute
            // a target hook that has lost constructor/field effects.
            var stateful = Path.Combine(root, "StatefulHooks.cs");
            File.WriteAllText(stateful, """
                using NUnit.Framework;
                namespace Sample.Tests {
                    [SetUpFixture] public class StatefulHooks {
                        public int Counter;
                        [OneTimeSetUp] public void Start() { Recorder.Track("inner-start"); }
                    }
                }
                """);
            CheckBlocked(stateful, "NUNIT_SETUPFIXTURE_STATE_REQUIRES_REVIEW");
            File.Delete(stateful);

            // NUnit SetUpFixture cannot own per-test setup methods.
            var invalid = Path.Combine(root, "InvalidHooks.cs");
            File.WriteAllText(invalid, """
                using NUnit.Framework;
                namespace Sample.Tests {
                    [SetUpFixture] public class InvalidHooks {
                        [OneTimeSetUp] public void Start() { Recorder.Track("inner-start"); }
                        [SetUp] public void BeforeEach() { Recorder.Track("test"); }
                    }
                }
                """);
            CheckBlocked(invalid, "NUNIT_SETUPFIXTURE_PER_TEST_HOOK_INVALID");
            File.Delete(invalid);

            void CheckBlocked(string source, string code)
            {
                var result = Migrate(source);
                Check(result.SourceModel.ScopedSetUp != null
                    && result.SourceModel.ScopedSetUp.Diagnostics.Any(d => d.Code == code)
                    && result.Report.FullyConvertedTests == 0
                    && result.GeneratedOutput.Contains("[MIGRATOR:" + code + "]",
                        StringComparison.Ordinal)
                    && result.GeneratedOutput.Contains("Assert.Inconclusive(", StringComparison.Ordinal),
                    "unsafe scoped fixture must fail closed: " + code);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    static int Count(string s, string needle) =>
        s.Split(needle, StringSplitOptions.None).Length - 1;

    static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException("NUnit scoped fixture regression: " + message);
    }
}
