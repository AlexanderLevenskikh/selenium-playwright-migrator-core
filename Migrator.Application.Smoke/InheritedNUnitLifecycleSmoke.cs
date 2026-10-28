using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Migrator.Core;
using Migrator.Core.Models;
using Migrator.PlaywrightDotNet;
using Migrator.Roslyn;
using Migrator.SeleniumCSharp;

internal static class InheritedNUnitLifecycleSmoke
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "nunit-life-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // A real SDK-style compilation lets Roslyn resolve inherited
            // source methods across different .cs files without heuristics.
            var project = Path.Combine(root, "Synthetic.csproj");
            File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(root, "FixtureBase.cs"), """
                using NUnit.Framework;
                public class FixtureBase
                {
                    [OneTimeSetUp] public void BaseOnce() { Recorder.Note("once-base"); }
                    [SetUp] public void BaseBefore() { Recorder.Note("before-base"); }
                    [TearDown] public void BaseAfter() { Recorder.Note("after-base"); }
                    [OneTimeTearDown] public void BaseOnceAfter() { Recorder.Note("once-after-base"); }
                }
                """);
            File.WriteAllText(Path.Combine(root, "FixtureMiddle.cs"), """
                using NUnit.Framework;
                public class FixtureMiddle : FixtureBase
                {
                    [OneTimeSetUp] public void MiddleOnce() { Recorder.Note("once-middle"); }
                    [SetUp] public void MiddleBefore() { Recorder.Note("before-middle"); }
                    [TearDown] public void MiddleAfter() { Recorder.Note("after-middle"); }
                    [OneTimeTearDown] public void MiddleOnceAfter() { Recorder.Note("once-after-middle"); }
                }
                """);
            var fixtureFile = Path.Combine(root, "FixtureLeaf.cs");
            File.WriteAllText(fixtureFile, """
                using NUnit.Framework;
                [TestFixture]
                public class FixtureLeaf : FixtureMiddle
                {
                    [OneTimeSetUp] public void LeafOnce() { Recorder.Note("once-leaf"); }
                    [SetUp] public void LeafBefore() { Recorder.Note("before-leaf"); }
                    [TearDown] public void LeafAfter() { Recorder.Note("after-leaf"); }
                    [OneTimeTearDown] public void LeafOnceAfter() { Recorder.Note("once-after-leaf"); }
                    [Test] public void RunsAfterAllSetup() { Recorder.Note("test"); }
                }
                """);

            var tokens = new[]
            {
                "once-base", "once-middle", "once-leaf",
                "before-base", "before-middle", "before-leaf",
                "after-leaf", "after-middle", "after-base",
                "once-after-leaf", "once-after-middle", "once-after-base",
                "test"
            };
            var config = new ProjectAdapterConfig
            {
                SourceProjectName = "Synthetic",
                Methods = tokens.Select(token => new MethodMapping
                {
                    SourceMethod = "Recorder.Note(\"" + token + "\")",
                    TargetStatements = new[] { "System.Console.WriteLine(\"" + token + "\");" },
                    RequiresReview = true
                }).ToArray()
            };
            PipelineResult RunPipeline(string path, ProjectAdapterConfig cfg) =>
                new MigrationPipeline(
                    new RoslynTestFileParser(cfg),
                    new PlaywrightDotNetRenderer(),
                    new DefaultProjectAdapter(cfg)).ProcessFile(path);

            var sourceModel = new RoslynTestFileParser(config).Parse(fixtureFile);
            var lifecycle = sourceModel.NUnitLifecycle;
            Check(lifecycle is { CanRenderOrderedHooks: true, SourceTypeLevels: 3 }
                  && lifecycle.Hooks.Count == 12,
                "three-level source ancestry and all four hook kinds must be discovered");
            Check(lifecycle!.Ordered(NUnitLifecycleKind.SetUp).Select(h => h.MethodName)
                    .SequenceEqual(new[] { "BaseBefore", "MiddleBefore", "LeafBefore" }),
                "base-to-derived NUnit SetUp order");
            Check(lifecycle.Ordered(NUnitLifecycleKind.TearDown).Select(h => h.MethodName)
                    .SequenceEqual(new[] { "LeafAfter", "MiddleAfter", "BaseAfter" }),
                "derived-to-base NUnit TearDown order");

            var result = RunPipeline(fixtureFile, config);
            var output = result.GeneratedOutput;
            foreach (var kind in new[] { "SetUp", "TearDown", "OneTimeSetUp", "OneTimeTearDown" })
                Check(output.Contains("[" + kind + "]", StringComparison.Ordinal),
                    "target NUnit hook missing: " + kind);
            Check(output.Contains("__migratorPerTestReached = 0", StringComparison.Ordinal)
                && output.Contains("__migratorPerTestReached = 1", StringComparison.Ordinal)
                && output.Contains("__migratorPerTestReached = 2", StringComparison.Ordinal)
                && output.Contains("if (__migratorPerTestReached >= 2)", StringComparison.Ordinal)
                && output.Contains("finally", StringComparison.Ordinal),
                "partial setup failure must gate teardown and preserve base finally");
            Check(output.Contains("[MIGRATOR:NUNIT_LIFECYCLE_REQUIRES_REVIEW]", StringComparison.Ordinal)
                && result.Report.FullyConvertedTests == 0,
                "inherited lifecycle cannot be accepted by compile success alone");
            Check(!output.Contains("[MIGRATOR:NUNIT_LIFECYCLE_SAME_LEVEL_ORDER]", StringComparison.Ordinal),
                "separate source levels must not be mistaken for unordered same-level hooks");

            int Index(string token) => output.IndexOf("System.Console.WriteLine(\"" + token + "\");",
                StringComparison.Ordinal);
            Check(tokens.All(token => Index(token) >= 0),
                "all effectful source hook calls must remain executable target operations");
            foreach (var ordered in new[]
            {
                new[] { "once-base", "once-middle", "once-leaf" },
                new[] { "before-base", "before-middle", "before-leaf" },
                new[] { "after-leaf", "after-middle", "after-base" },
                new[] { "once-after-leaf", "once-after-middle", "once-after-base" }
            })
                Check(ordered.Zip(ordered.Skip(1))
                        .All(pair => Index(pair.First) < Index(pair.Second)),
                    "source hook effects must retain NUnit inheritance order");
            Check(!CSharpSyntaxTree.ParseText(output).GetDiagnostics()
                .Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
                "generated lifecycle wrappers must be C# syntax valid");

            // Multiple hooks on the same source level have undefined NUnit order.
            var ambiguous = Path.Combine(root, "Ambiguous.cs");
            File.WriteAllText(ambiguous, """
                using NUnit.Framework;
                [TestFixture] public class Ambiguous
                {
                    [SetUp] public void Alpha() { Recorder.Note("alpha"); }
                    [SetUp] public void Beta() { Recorder.Note("beta"); }
                    [Test] public void T() {}
                }
                """);
            CheckBlocked(ambiguous, "NUNIT_LIFECYCLE_SAME_LEVEL_ORDER");

            var overrideFile = Path.Combine(root, "OverrideFixture.cs");
            File.WriteAllText(overrideFile, """
                using NUnit.Framework;
                public class OverrideBase {
                    [SetUp] public virtual void Prepare() { Recorder.Note("before-base"); }
                }
                [TestFixture] public class OverrideFixture : OverrideBase {
                    [SetUp] public override void Prepare() { Recorder.Note("before-leaf"); }
                    [Test] public void T() {}
                }
                """);
            CheckBlocked(overrideFile, "NUNIT_LIFECYCLE_OVERRIDE");

            var unpaired = Path.Combine(root, "Unpaired.cs");
            File.WriteAllText(unpaired, """
                using NUnit.Framework;
                [TestFixture] public class Unpaired {
                    [TearDown] public void After() { Recorder.Note("after-base"); }
                    [Test] public void T() {}
                }
                """);
            CheckBlocked(unpaired, "NUNIT_LIFECYCLE_UNPAIRED_TEARDOWN");

            var missingBase = Path.Combine(root, "UnresolvedBase.cs");
            File.WriteAllText(missingBase, """
                using NUnit.Framework;
                [TestFixture] public class UnresolvedBase : MissingSourceBase {
                    [SetUp] public void Before() { Recorder.Note("before-leaf"); }
                    [Test] public void T() {}
                }
                """);
            CheckBlocked(missingBase, "NUNIT_LIFECYCLE_BASE_UNRESOLVED");

            var hidden = Path.Combine(root, "HiddenFixture.cs");
            File.WriteAllText(hidden, """
                using NUnit.Framework;
                public class HiddenBase {
                    [SetUp] public void Prepare() { Recorder.Note("before-base"); }
                }
                [TestFixture] public class HiddenFixture : HiddenBase {
                    [SetUp] public new void Prepare() { Recorder.Note("before-leaf"); }
                    [Test] public void T() {}
                }
                """);
            CheckBlocked(hidden, "NUNIT_LIFECYCLE_HIDDEN_METHOD");

            var custom = Path.Combine(root, "CustomAttributeFixture.cs");
            File.WriteAllText(custom, """
                using NUnit.Framework;
                namespace SyntheticAttrs { public class SetUpAttribute : System.Attribute { } }
                [TestFixture] public class CustomAttributeFixture {
                    [SyntheticAttrs.SetUp] public void Before() { Recorder.Note("before-leaf"); }
                    [Test] public void T() {}
                }
                """);
            CheckBlocked(custom, "NUNIT_LIFECYCLE_ATTRIBUTE_AMBIGUOUS");

            var incompatibleHost = new ProjectAdapterConfig
            {
                SourceProjectName = "Synthetic",
                Methods = config.Methods,
                TestHost = new TestHostConfig
                {
                    SetUpStatements = new[] { "await Page.GotoAsync(\"https://example.invalid/host\");" }
                }
            };
            var hostResult = RunPipeline(fixtureFile, incompatibleHost);
            Check(hostResult.GeneratedOutput.Contains("[MIGRATOR:NUNIT_LIFECYCLE_HOST_CONFLICT]",
                      StringComparison.Ordinal)
                  && hostResult.GeneratedOutput.Contains("Assert.Inconclusive(", StringComparison.Ordinal),
                "a custom target host setup may not erase inherited source hooks");

            var xunitHost = new ProjectAdapterConfig
            {
                SourceProjectName = "Synthetic",
                Methods = config.Methods,
                TestHost = new TestHostConfig { TargetTestFramework = "xunit" }
            };
            var xunitResult = RunPipeline(fixtureFile, xunitHost);
            Check(xunitResult.GeneratedOutput.Contains("[MIGRATOR:NUNIT_LIFECYCLE_FRAMEWORK_MISMATCH]",
                      StringComparison.Ordinal)
                  && xunitResult.GeneratedOutput.Contains("throw new NotSupportedException(", StringComparison.Ordinal),
                "NUnit inherited cleanup cannot silently become xUnit initialization");

            void CheckBlocked(string path, string code)
            {
                var outcome = RunPipeline(path, config);
                var plan = outcome.SourceModel.NUnitLifecycle;
                Check(plan != null && !plan.CanRenderOrderedHooks
                    && plan.Diagnostics.Any(d => d.Code == code),
                    "Roslyn must identify lifecycle blocker " + code);
                Check(outcome.GeneratedOutput.Contains("[MIGRATOR:" + code + "]", StringComparison.Ordinal)
                    && outcome.GeneratedOutput.Contains("Assert.Inconclusive(", StringComparison.Ordinal)
                    && outcome.Report.FullyConvertedTests == 0,
                    "ambiguous lifecycle must fail closed at runtime: " + code);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException("Inherited NUnit lifecycle smoke: " + message);
    }
}
