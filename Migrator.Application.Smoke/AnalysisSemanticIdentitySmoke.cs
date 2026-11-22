using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Migrator.Application;
using System.Text.Json;

internal static class AnalysisSemanticIdentitySmoke
{
    const string ProjectStart = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>";
    const string Fixture = """
        using NUnit.Framework;
        namespace Fictional.Identity;
        [TestFixture] public class IdentityTests {
            [Test] public void Example() { SharedActions.Check(); }
        }
        """;
    const string Helper = "namespace Fictional.Identity; public static class SharedActions { public static void Check() {} }";

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "migrator-semantic-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var receiptPath = Path.Combine("artifacts", "analysis-identity", "receipt.json");
        if (File.Exists(receiptPath)) File.Delete(receiptPath);
        var scenarios = new List<object>();
        try
        {
            var engine = new MigrationEngine();
            CheckStability(engine, root, scenarios);
            CheckSiblingBase(engine, root, scenarios);
            CheckParentMetadata(engine, root, scenarios);
            CheckTransitiveProjectReference(engine, root, scenarios);
            CheckLinkedCompileSource(engine, root, scenarios);
            CheckHintPathBytes(engine, root, scenarios);
            CheckMissingDependencyAppears(engine, root, scenarios);
            CheckProjectSelection(engine, root, scenarios);
            CheckStandaloneFrameworkMetadata(engine, root, scenarios);
            Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
            File.WriteAllText(receiptPath, JsonSerializer.Serialize(new
            {
                schema = "migrator-analysis-semantic-identity-smoke/v1",
                status = "passed",
                scenarios
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    static void CheckStability(MigrationEngine engine, string root, List<object> scenarios)
    {
        var (directory, fixture, project) = CreateFixture(root, "stability");
        var helper = Path.Combine(directory, "SharedActions.cs");
        File.WriteAllText(helper, Helper);
        var before = engine.Analyze(fixture);
        var preview = engine.Preview(fixture, expectedAnalysisId: before.AnalysisId);
        Check(preview.Files.Count == 1, "stable fixture produces a target");
        var projectBytes = File.ReadAllBytes(project);
        var helperBytes = File.ReadAllBytes(helper);
        // Rewrites and timestamp changes are not semantic edits. The ID is
        // content-bound, never a last-write-time or directory-inventory token.
        File.WriteAllBytes(project, projectBytes);
        File.WriteAllBytes(helper, helperBytes);
        File.SetLastWriteTimeUtc(helper, DateTime.UtcNow.AddMinutes(-5));
        File.WriteAllText(Path.Combine(directory, "README.md"), "Unrelated project notes.");
        var after = engine.Analyze(fixture);
        Check(after.AnalysisId == before.AnalysisId && after.SourceTreeHash == before.SourceTreeHash,
            "unchanged bytes, changed timestamp and unrelated README keep identity stable");
        var repeated = engine.Preview(fixture, expectedAnalysisId: before.AnalysisId);
        Check(repeated.CandidateId == preview.CandidateId && repeated.TargetTreeHash == preview.TargetTreeHash,
            "unchanged inputs retain candidate and target identity");
        var verified = engine.VerifyArtifact(fixture, before.AnalysisId, preview.CandidateId);
        Check(verified.Candidate.CandidateId == preview.CandidateId,
            "current analysis and candidate remain acceptable for verification");
        scenarios.Add(new
        {
            scenario = "unchanged_bytes_rewrite_timestamp_and_unrelated_readme",
            status = "passed",
            sourceScope = "file",
            beforeSourceTreeHash = before.SourceTreeHash,
            afterSourceTreeHash = after.SourceTreeHash,
            beforeAnalysisId = before.AnalysisId,
            afterAnalysisId = after.AnalysisId,
            beforeCandidateId = preview.CandidateId,
            afterCandidateId = repeated.CandidateId,
            sameGeneratedTargetBytes = preview.TargetTreeHash == repeated.TargetTreeHash,
            currentCandidateVerified = verified.Candidate.CandidateId == preview.CandidateId
        });
    }

    static void CheckSiblingBase(MigrationEngine engine, string root, List<object> scenarios)
    {
        var (directory, fixture, _) = CreateFixture(root, "sibling-base", """
            using NUnit.Framework;
            namespace Fictional.Identity;
            [TestFixture] public class IdentityTests : TestBase {
                [Test] public void Example() { Assert.That(true, Is.True); }
            }
            """);
        var sibling = Path.Combine(directory, "TestBase.cs");
        File.WriteAllText(sibling, "namespace Fictional.Identity; public class TestBase {} ");
        var after = AssertInvalidated(engine, scenarios, fixture, () => File.WriteAllText(sibling, """
            using NUnit.Framework;
            namespace Fictional.Identity;
            [Ignore("base fixture maintenance")] public class TestBase {}
            """), "sibling base outside the selected file");
        Check(after.Files.Single().Disposition != "supported",
            "changed inherited NUnit gate affects actual parser result");
    }

    static void CheckParentMetadata(MigrationEngine engine, string root, List<object> scenarios)
    {
        var (directory, fixture, project) = CreateFixture(root, "parent-metadata", """
            using NUnit.Framework;
            namespace Fictional.Identity;
            [TestFixture] public class IdentityTests {
                [Test] public void Example() {
            #if CHANGED
                    SharedActions.Other();
            #else
                    SharedActions.Check();
            #endif
                }
            }
            """);
        File.WriteAllText(Path.Combine(directory, "SharedActions.cs"), Helper.Replace("void Check() {}", "void Check() {} public static void Other() {}"));
        // The owning project is above a directory source scope as well as above
        // a single fixture. DefineConstants changes which invocation is parsed.
        AssertInvalidated(engine, scenarios, Path.GetDirectoryName(fixture)!, () =>
            File.WriteAllText(project, ProjectStart + "<PropertyGroup><DefineConstants>CHANGED</DefineConstants></PropertyGroup></Project>"),
            "owning project metadata above a subdirectory scope");
    }

    static void CheckTransitiveProjectReference(MigrationEngine engine, string root, List<object> scenarios)
    {
        var (directory, fixture, project) = CreateFixture(root, "transitive-project-reference");
        var bridge = Path.Combine(directory, "..", "Bridge");
        var shared = Path.Combine(directory, "..", "Shared");
        Directory.CreateDirectory(bridge);
        Directory.CreateDirectory(shared);
        File.WriteAllText(project, ProjectStart + "<ItemGroup><ProjectReference Include=\"../Bridge/Bridge.csproj\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(bridge, "Bridge.csproj"), ProjectStart
            + "<ItemGroup><ProjectReference Include=\"../Shared/Shared.csproj\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(bridge, "BridgeActions.cs"), "namespace Fictional.Identity; public static class SharedActions { public static void Check() { SharedDependency.Check(); } }");
        File.WriteAllText(Path.Combine(shared, "Shared.csproj"), ProjectStart + "</Project>");
        var dependency = Path.Combine(shared, "SharedDependency.cs");
        File.WriteAllText(dependency, "namespace Fictional.Identity; public static class SharedDependency { public static void Check() {} }");
        AssertInvalidated(engine, scenarios, fixture, () => File.WriteAllText(dependency,
                "namespace Fictional.Identity; public static class SharedDependency { public static System.Threading.Tasks.Task Check() => System.Threading.Tasks.Task.CompletedTask; }"),
            "transitive external ProjectReference source");
    }

    static void CheckLinkedCompileSource(MigrationEngine engine, string root, List<object> scenarios)
    {
        var (directory, fixture, project) = CreateFixture(root, "linked-source");
        var linked = Path.Combine(directory, "..", "Linked");
        Directory.CreateDirectory(linked);
        var helper = Path.Combine(linked, "SharedActions.cs");
        File.WriteAllText(helper, Helper);
        File.WriteAllText(project, ProjectStart + "<ItemGroup><Compile Include=\"../Linked/SharedActions.cs\" Link=\"SharedActions.cs\" /></ItemGroup></Project>");
        AssertInvalidated(engine, scenarios, fixture, () => File.WriteAllText(helper,
                Helper.Replace("void Check() {}", "System.Threading.Tasks.Task Check() => System.Threading.Tasks.Task.CompletedTask;")),
            "external explicit Compile Include source");
    }

    static void CheckHintPathBytes(MigrationEngine engine, string root, List<object> scenarios)
    {
        var (directory, fixture, project) = CreateFixture(root, "hintpath");
        var library = Path.Combine(directory, "..", "lib");
        Directory.CreateDirectory(library);
        var assembly = Path.Combine(library, "ExternalActions.dll");
        var first = EmitReference(1);
        var second = EmitReference(2);
        Check(first.Length == second.Length && !first.SequenceEqual(second),
            "reference regression uses distinct managed assemblies with equal byte length");
        File.WriteAllBytes(assembly, first);
        File.WriteAllText(project, ProjectStart + "<ItemGroup><Reference Include=\"ExternalActions\"><HintPath>../lib/ExternalActions.dll</HintPath></Reference></ItemGroup></Project>");
        var timestamp = File.GetLastWriteTimeUtc(assembly);
        AssertInvalidated(engine, scenarios, fixture, () =>
        {
            // Atomic replacement also works if Roslyn retains a read handle.
            var replacement = assembly + ".replacement";
            File.WriteAllBytes(replacement, second);
            File.Move(replacement, assembly, overwrite: true);
            File.SetLastWriteTimeUtc(assembly, timestamp);
            Check(new FileInfo(assembly).Length == first.Length && File.GetLastWriteTimeUtc(assembly) == timestamp,
                "DLL replacement preserves size and timestamp");
        }, "same-path same-size same-timestamp HintPath DLL bytes");
    }

    static void CheckMissingDependencyAppears(MigrationEngine engine, string root, List<object> scenarios)
    {
        var (directory, fixture, project) = CreateFixture(root, "missing-reference");
        File.WriteAllText(Path.Combine(directory, "SharedActions.cs"), Helper);
        var dependencyDirectory = Path.GetFullPath(Path.Combine(directory, "..", "Later"));
        Directory.CreateDirectory(dependencyDirectory);
        var dependencyProject = Path.Combine(dependencyDirectory, "Later.csproj");
        // Both a previously missing project and a previously missing source
        // must be discovered afresh; caching the original file list is unsafe.
        File.WriteAllText(project, ProjectStart + "<ItemGroup><ProjectReference Include=\"../Later/Later.csproj\" /></ItemGroup></Project>");
        AssertInvalidated(engine, scenarios, fixture, () =>
        {
            File.WriteAllText(dependencyProject, ProjectStart + "<ItemGroup><Compile Include=\"../LinkedLater.cs\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(dependencyDirectory, "Dependency.cs"), "public class LaterDependency {}");
        }, "previously absent ProjectReference becomes resolvable");
        AssertInvalidated(engine, scenarios, fixture, () =>
            File.WriteAllText(Path.Combine(dependencyDirectory, "..", "LinkedLater.cs"), "public class LinkedLaterDependency {}"),
            "previously absent Compile Include source appears");
    }

    static void CheckProjectSelection(MigrationEngine engine, string root, List<object> scenarios)
    {
        var (directory, fixture, project) = CreateFixture(root, "project-selection");
        File.WriteAllText(Path.Combine(directory, "SharedActions.cs"), Helper);
        File.Delete(project);
        AssertInvalidated(engine, scenarios, fixture, () => File.WriteAllText(project, ProjectStart + "</Project>"),
            "standalone source gains an owning project");
        AssertInvalidated(engine, scenarios, fixture, () => File.WriteAllText(Path.Combine(directory, "Second.csproj"), ProjectStart + "</Project>"),
            "ambiguous nearest-project selection falls back to standalone");
    }

    static void CheckStandaloneFrameworkMetadata(MigrationEngine engine, string root, List<object> scenarios)
    {
        // Roslyn deliberately skips project discovery for generated-path input,
        // but the source framework guard still reads a same-directory project.
        var directory = Path.Combine(root, "standalone-framework-guard", "obj");
        Directory.CreateDirectory(directory);
        var fixture = Path.Combine(directory, "IdentityTests.cs");
        File.WriteAllText(fixture, "using NUnit.Framework; [TestFixture] public class IdentityTests { [Test] public void Example() { Assert.That(true, Is.True); } }");
        var project = Path.Combine(directory, "Identity.csproj");
        File.WriteAllText(project, ProjectStart + "</Project>");
        var after = AssertInvalidated(engine, scenarios, fixture, () =>
                File.WriteAllText(project, ProjectStart + "<ItemGroup><PackageReference Include=\"Microsoft.Playwright\" Version=\"1.51.0\" /></ItemGroup></Project>"),
            "framework guard metadata for standalone generated-path input");
        Check(after.Files.Single().Disposition == "already_playwright",
            "framework metadata changes actual source classification");
    }

    static MigrationAnalysis AssertInvalidated(MigrationEngine engine, List<object> scenarios, string scope, Action mutate, string description)
    {
        var before = engine.Analyze(scope);
        var preview = engine.Preview(scope, expectedAnalysisId: before.AnalysisId);
        Check(preview.Files.Count == 1, description + ": baseline produces a target");
        mutate();
        var after = engine.Analyze(scope);
        Check(after.SourceTreeHash == before.SourceTreeHash,
            description + ": selected source bytes did not change");
        Check(after.AnalysisId != before.AnalysisId,
            description + ": semantic dependency edit invalidates analysis identity");
        var stalePreviewRejected = ExpectStale(() => engine.Preview(scope, expectedAnalysisId: before.AnalysisId), description + ": preview");
        var finding = preview.Findings.FirstOrDefault()?.Id ?? "unreached-finding-lookup";
        var staleExplainRejected = ExpectStale(() => engine.Explain(scope, finding, expectedAnalysisId: before.AnalysisId), description + ": explain");
        var staleVerificationRejected = ExpectStale(() => engine.VerifyArtifact(scope, before.AnalysisId, preview.CandidateId),
            description + ": verify with old analysis and candidate");
        var freshPreview = engine.Preview(scope, expectedAnalysisId: after.AnalysisId);
        Check(freshPreview.CandidateId != preview.CandidateId,
            description + ": candidate belongs to the refreshed analysis even if generated bytes match");
        scenarios.Add(new
        {
            scenario = description,
            status = "passed",
            sourceScope = Directory.Exists(scope) ? "directory" : "file",
            beforeSourceTreeHash = before.SourceTreeHash,
            afterSourceTreeHash = after.SourceTreeHash,
            beforeAnalysisId = before.AnalysisId,
            afterAnalysisId = after.AnalysisId,
            beforeCandidateId = preview.CandidateId,
            afterCandidateId = freshPreview.CandidateId,
            sameGeneratedTargetBytes = preview.TargetTreeHash == freshPreview.TargetTreeHash,
            stalePreviewRejected,
            staleExplainRejected,
            staleVerificationRejected
        });
        return after;
    }

    static (string Directory, string Fixture, string Project) CreateFixture(string root, string name, string? source = null)
    {
        var directory = Path.Combine(root, name, "App");
        var fixtures = Path.Combine(directory, "Fixtures");
        Directory.CreateDirectory(fixtures);
        var fixture = Path.Combine(fixtures, "IdentityTests.cs");
        var project = Path.Combine(directory, "Identity.csproj");
        File.WriteAllText(fixture, source ?? Fixture);
        File.WriteAllText(project, ProjectStart + "</Project>");
        return (directory, fixture, project);
    }

    static byte[] EmitReference(int value)
    {
        var compilation = CSharpCompilation.Create("ExternalActions",
            new[] { CSharpSyntaxTree.ParseText("namespace Fictional.Identity; public static class SharedActions { public static int Check() => " + value + "; }") },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Check(result.Success, "valid HintPath reference must compile: " + string.Join("; ", result.Diagnostics));
        return stream.ToArray();
    }

    static bool ExpectStale(Action action, string description)
    {
        try { action(); }
        catch (InvalidOperationException error) when (error.Message.StartsWith("STALE_ANALYSIS:", StringComparison.Ordinal))
        {
            return true;
        }
        throw new InvalidOperationException("Analysis semantic identity smoke failed: " + description + " accepted a stale analysis");
    }

    static void Check(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException("Analysis semantic identity smoke failed: " + description);
    }
}
