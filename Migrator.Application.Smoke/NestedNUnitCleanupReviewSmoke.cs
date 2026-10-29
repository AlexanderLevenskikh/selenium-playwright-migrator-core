using System.Diagnostics;
using System.Xml.Linq;
using Migrator.Application;

internal static class NestedNUnitCleanupReviewSmoke
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "synthetic-nested-cleanup-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        try
        {
            var fixture = """
                using NUnit.Framework;
                namespace Fictional.Review {
                    public abstract class SuiteBase {
                        [SetUpFixture]
                        public class CleanupScope {
                            [OneTimeTearDown]
                            public void After() {
                                Probe.Note("nested-cleanup");
                            }
                        }
                    }
                }
                """;
            File.WriteAllText(Path.Combine(source, "SuiteBase.cs"), fixture);
            File.WriteAllText(Path.Combine(source, "Probe.cs"), """
                using System;
                using System.IO;
                namespace Fictional.Review {
                    public static class Probe {
                        public static void Note(string what) {
                            var path = Environment.GetEnvironmentVariable("NUNIT_REVIEW_TRACE");
                            if (string.IsNullOrWhiteSpace(path))
                                throw new Exception("Synthetic trace missing");
                            File.AppendAllText(path, what + "\n");
                        }
                    }
                }
                """);
            File.WriteAllText(Path.Combine(source, "Tests.cs"), """
                using NUnit.Framework;
                namespace Fictional.Review {
                    [TestFixture] public class DerivedFixture : SuiteBase {
                        [Test] public void A() { Probe.Note("test-A"); }
                    }
                    [TestFixture] public class UnrelatedFixture {
                        [Test] public void B() { Probe.Note("test-B"); }
                    }
                }
                """);

            var engine = new MigrationEngine();
            var ledger = engine.InspectNUnitLifecycle(source);
            const string type = "Fictional.Review.SuiteBase.CleanupScope";
            var sourceScope = ledger.SetUpFixtures.Single();
            Check(sourceScope.SourceType == type && sourceScope.CleanupOnly
                && sourceScope.ContainingType == "Fictional.Review.SuiteBase",
                "source nested cleanup fingerprint and owner");
            var unmapped = engine.ReviewNestedNUnitCleanup(source, type,
                ledger.SourceTreeHash, null);
            Check(unmapped.Status == "blocked" && unmapped.Candidate == null
                && unmapped.Issues.Any(issue =>
                    issue.Code == "NESTED_CLEANUP_MAPPING_REQUIRED"),
                "missing reviewed target cleanup must never create executable code");

            var reviewed = engine.ReviewNestedNUnitCleanup(source, type,
                ledger.SourceTreeHash, new[]
                {
                    "Fictional.Review.Probe.Note(\"nested-cleanup\");"
                });
            Check(reviewed.SchemaVersion ==
                    "migrator-nunit-nested-cleanup-review/v1alpha1"
                && reviewed.Status == "requires_review"
                && !reviewed.BehavioralEquivalenceVerified
                && reviewed.Candidate is { Sha256.Length: 64 }
                && reviewed.Candidate.SourceType == type
                && reviewed.Candidate.SourceMethodSha256 ==
                    ledger.Hooks.Single(hook => hook.Kind == "OneTimeTearDown")
                        .MethodSha256
                && reviewed.TargetBuild == "not_run"
                && reviewed.TargetNUnitRuntime == "not_run"
                && reviewed.SourceNUnitRuntime == "not_run",
                "reviewed candidate retains source class, method hash and honest evidence");
            var emitted = reviewed.Candidate!.Content;
            Check(emitted.Contains("public abstract class SuiteBase", StringComparison.Ordinal)
                && emitted.Contains("public class CleanupScope", StringComparison.Ordinal)
                && emitted.Contains("[NUnit.Framework.SetUpFixture]", StringComparison.Ordinal)
                && emitted.Contains("[NUnit.Framework.OneTimeTearDown]", StringComparison.Ordinal)
                && !emitted.Contains("[assembly:", StringComparison.Ordinal),
                "nested NUnit scope must not be hoisted to an assembly hook");

            var evidence = engine.ReviewNestedNUnitCleanup(source, type,
                ledger.SourceTreeHash,
                new[] { "Fictional.Review.Probe.Note(\"nested-cleanup\");" },
                new string('a', 64));
            Check(evidence.Status == "requires_review"
                && evidence.SourceRuntimeEvidenceSha256 == new string('a', 64)
                && !evidence.BehavioralEquivalenceVerified
                && evidence.SourceNUnitRuntime == "not_run",
                "caller-provided external evidence hash never becomes a runtime claim");

            var wrongMethod = engine.ReviewNestedNUnitCleanup(source, type,
                ledger.SourceTreeHash, new[] { "if (true) Probe.Note(\"x\");" });
            Check(wrongMethod.Status == "blocked"
                && wrongMethod.Issues.Any(issue =>
                    issue.Code == "NESTED_CLEANUP_MAPPING_UNSAFE"),
                "target control flow must be explicitly reviewed, not injected");

            var missingType = engine.ReviewNestedNUnitCleanup(source,
                "Fictional.Review.Unknown", ledger.SourceTreeHash,
                new[] { "Fictional.Review.Probe.Note(\"nested-cleanup\");" });
            Check(missingType.Status == "blocked"
                && missingType.Issues.Any(issue =>
                    issue.Code == "NESTED_CLEANUP_SOURCE_NOT_UNIQUE"),
                "unrecognized type cannot be rehosted");

            var sibling = Path.Combine(source, "OtherScope.cs");
            File.WriteAllText(sibling, """
                using NUnit.Framework;
                namespace Fictional.Review {
                    [SetUpFixture] public class OtherScope {
                        [OneTimeSetUp] public void Before() {}
                    }
                }
                """);
            var changed = engine.InspectNUnitLifecycle(source);
            var conflict = engine.ReviewNestedNUnitCleanup(source, type,
                changed.SourceTreeHash,
                new[] { "Fictional.Review.Probe.Note(\"nested-cleanup\");" });
            Check(conflict.Status == "blocked" && conflict.Issues.Any(issue =>
                    issue.Code == "NESTED_CLEANUP_SAME_SCOPE_ORDER_REVIEW"),
                "unknown overlapping namespace cleanup order must fail closed");
            File.Delete(sibling);

            try
            {
                engine.ReviewNestedNUnitCleanup(source, type, ledger.SourceTreeHash,
                    new[] { "Fictional.Review.Probe.Note(\"nested-cleanup\");" });
                throw new Exception("stale source accepted");
            }
            catch (InvalidOperationException exception) when (
                exception.Message.StartsWith("STALE_NESTED_CLEANUP_SOURCE",
                    StringComparison.Ordinal)) { }

            var fresh = engine.InspectNUnitLifecycle(source);
            for (var version = 0; version < 2; version++)
            {
                var nunit = version == 0 ? "3.14.0" : "4.3.2";
                var adapter = version == 0 ? "4.5.0" : "5.1.0";
                var original = Path.Combine(root, "original-" + version);
                var target = Path.Combine(root, "target-" + version);
                Directory.CreateDirectory(original);
                Directory.CreateDirectory(target);
                foreach (var sourceFile in new[] { "SuiteBase.cs", "Probe.cs", "Tests.cs" })
                    File.Copy(Path.Combine(source, sourceFile),
                        Path.Combine(original, sourceFile));
                File.Copy(Path.Combine(source, "Probe.cs"),
                    Path.Combine(target, "Probe.cs"));
                File.WriteAllText(Path.Combine(target,
                    reviewed.Candidate.RelativePath), reviewed.Candidate.Content);
                File.WriteAllText(Path.Combine(target, "Tests.cs"), """
                    using NUnit.Framework;
                    namespace Fictional.Review {
                        // Generated Playwright tests no longer need to derive
                        // from the original Selenium SuiteBase.
                        [TestFixture] public class DerivedFixture {
                            [Test] public void A() { Probe.Note("test-A"); }
                        }
                        [TestFixture] public class UnrelatedFixture {
                            [Test] public void B() { Probe.Note("test-B"); }
                        }
                    }
                    """);
                var project = $$"""
                    <Project Sdk="Microsoft.NET.Sdk">
                    <PropertyGroup>
                      <TargetFramework>net10.0</TargetFramework>
                      <IsTestProject>true</IsTestProject>
                    </PropertyGroup>
                    <ItemGroup>
                      <PackageReference Include="NUnit" Version="{{nunit}}" />
                      <PackageReference Include="NUnit3TestAdapter" Version="{{adapter}}" />
                      <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                    </ItemGroup>
                    </Project>
                    """;
                File.WriteAllText(Path.Combine(original, "Synthetic.csproj"),
                    project);
                File.WriteAllText(Path.Combine(target, "Synthetic.csproj"),
                    project);
                var originalEvents = RunNUnit(original);
                var targetEvents = RunNUnit(target);
                Check(originalEvents.OrderBy(x => x)
                        .SequenceEqual(targetEvents.OrderBy(x => x)),
                    "original and nested-wrapper target must preserve all synthetic observables");
                Check(originalEvents.Count(x => x == "nested-cleanup") == 1
                    && targetEvents.Count(x => x == "nested-cleanup") == 1
                    && originalEvents.IndexOf("nested-cleanup") >
                        originalEvents.IndexOf("test-A")
                    && originalEvents.IndexOf("nested-cleanup") >
                        originalEvents.IndexOf("test-B")
                    && targetEvents.IndexOf("nested-cleanup") >
                        targetEvents.IndexOf("test-A")
                    && targetEvents.IndexOf("nested-cleanup") >
                        targetEvents.IndexOf("test-B"),
                    "nested cleanup must be called once after all namespace tests");
            }
            File.AppendAllText(Path.Combine(source, "SuiteBase.cs"),
                "\n// Source bytes changed\n");
            try
            {
                engine.ReviewNestedNUnitCleanup(source, type, fresh.SourceTreeHash,
                    new[] { "Fictional.Review.Probe.Note(\"nested-cleanup\");" });
                throw new Exception("stale method accepted");
            }
            catch (InvalidOperationException ex) when (
                ex.Message.StartsWith("STALE_NESTED_CLEANUP_SOURCE",
                    StringComparison.Ordinal)) {}
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static List<string> RunNUnit(string directory)
    {
        var trace = Path.Combine(directory, "order.txt");
        var results = Path.Combine(directory, "results");
        Directory.CreateDirectory(results);
        var project = Path.Combine(directory, "Synthetic.csproj");
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("test");
        psi.ArgumentList.Add(project);
        psi.ArgumentList.Add("--configuration");
        psi.ArgumentList.Add("Release");
        psi.ArgumentList.Add("--logger");
        psi.ArgumentList.Add("trx;LogFileName=result.trx");
        psi.ArgumentList.Add("--results-directory");
        psi.ArgumentList.Add(results);
        psi.ArgumentList.Add("--verbosity");
        psi.ArgumentList.Add("quiet");
        psi.Environment["NUNIT_REVIEW_TRACE"] = trace;
        using var proc = Process.Start(psi)
            ?? throw new Exception("dotnet test not started");
        var output = proc.StandardOutput.ReadToEndAsync();
        var errors = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(240_000))
        {
            proc.Kill(entireProcessTree: true);
            throw new Exception("synthetic NUnit runtime timed out");
        }
        var standard = output.GetAwaiter().GetResult();
        var stderr = errors.GetAwaiter().GetResult();
        Check(proc.ExitCode == 0, "synthetic NUnit runtime failed: "
            + standard[^Math.Min(900, standard.Length)..]
            + stderr[^Math.Min(900, stderr.Length)..]);
        var trx = XDocument.Load(Path.Combine(results, "result.trx"));
        var counters = trx.Descendants().Single(element =>
            element.Name.LocalName == "Counters");
        Check(counters.Attribute("total")?.Value == "2"
            && counters.Attribute("passed")?.Value == "2"
            && counters.Attribute("failed")?.Value == "0",
            "NUnit TRX must attest exactly two passing synthetic tests");
        return File.ReadAllLines(trace).ToList();
    }

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(
            "Nested NUnit cleanup review smoke: " + message);
    }
}
