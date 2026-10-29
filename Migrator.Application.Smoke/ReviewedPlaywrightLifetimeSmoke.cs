using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using Migrator.Application;

internal static class ReviewedPlaywrightLifetimeSmoke
{
    private const string SourceBaseType = "Fictional.Fixture.FixtureBase";
    private const string SourceNestedType =
        "Fictional.Fixture.FixtureBase.NestedCleanup";
    private const string SourceInfrastructureType =
        "Fictional.Fixture.DiContainer";

    public static void Run()
    {
        var parent = Path.Combine(Path.GetTempPath(),
            "reviewed-playwright-lifetime-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(parent, "source");
        Directory.CreateDirectory(source);
        try
        {
            File.WriteAllText(Path.Combine(source, "FixtureBase.cs"), """
                using NUnit.Framework;
                namespace Fictional.Fixture;
                public abstract class FixtureBase {
                    protected IWebDriver Driver =>
                        DiContainer.GetScopedRequiredService<IPooledWebDriver>().WrappedDriver;
                    protected Navigation Navigation =>
                        DiContainer.GetScopedRequiredService<Navigation>();
                    protected LocalStorage LocalStorage =>
                        DiContainer.GetScopedRequiredService<LocalStorage>();
                    private Screenshoter Screenshoter =>
                        DiContainer.GetScopedRequiredService<Screenshoter>();
                    [OneTimeSetUp] public void BeforeFixture() {
                        ConfigureRetry();
                    }
                    [TearDown] public void AfterTest() {
                        TestContext.CurrentContext.WriteBrowserLogs(Driver);
                        Screenshoter.CreateAndSaveScreenshot();
                    }
                    [OneTimeTearDown] public void AfterFixture() {
                        DiContainer.DisposeScope();
                    }
                    private void ConfigureRetry() {
                        var listener = new RetryTestListener();
                        TestExecutionContext.CurrentContext.GetType().GetProperties();
                    }
                    [SetUpFixture] public class NestedCleanup {
                        [OneTimeTearDown] public void FinalCleanup() {
                            DiContainer.FullDispose();
                        }
                    }
                }
                """);
            File.WriteAllText(Path.Combine(source, "DiContainer.cs"), """
                using NUnit.Framework;
                namespace Fictional.Fixture;
                public static class DiContainer {
                    private static readonly ConcurrentDictionary<string, IServiceScope>
                        ScopeMap = new();
                    private static ServiceProvider ServiceProvider;
                    private static string ScopeKey =>
                        TestContext.CurrentContext.Test.ClassName;
                    public static T GetScopedRequiredService<T>() {
                        var scope = ScopeMap.GetOrAdd(ScopeKey,
                            key => ServiceProvider.CreateScope());
                        return scope.ServiceProvider.GetRequiredService<T>();
                    }
                    public static void DisposeScope() {
                        if (!ScopeMap.TryRemove(ScopeKey, out var scope))
                            throw new Exception("fixture scope missing");
                        scope.Dispose();
                    }
                    public static void FullDispose() {
                        ServiceProvider.GetRequiredService<IWebDriverPool>().Clear();
                        ServiceProvider.Dispose();
                    }
                }
                """);
            File.WriteAllText(Path.Combine(source, "SyntheticTests.cs"), """
                using NUnit.Framework;
                namespace Fictional.Fixture;
                [TestFixture] public class SourceTests : FixtureBase {
                    [Test] public void A() {}
                }
                """);
            var engine = new MigrationEngine();
            var observed = engine.InspectNUnitLifecycle(source);
            var effects = observed.Effects.Where(e =>
                e.SourceType is SourceBaseType or SourceNestedType).ToArray();
            Check(effects.Length >= 10, "fixture DI/browser source effects missing");
            var kinds = effects.Select(e => e.Kind).ToHashSet();
            foreach (var required in new[]
            {
                "di_scoped_service", "browser_driver_scope",
                "browser_navigation_scope", "browser_storage_scope",
                "browser_logs", "browser_screenshot", "di_scope_dispose",
                "di_global_dispose", "nunit_listener_reflection"
            })
                Check(kinds.Contains(required), "source effect missing " + required);
            string Role(string kind) => kind switch
            {
                "di_scoped_service" => "FixtureService",
                "browser_driver_scope" => "BrowserContextPage",
                "browser_navigation_scope" => "PageNavigation",
                "browser_storage_scope" => "PageStorage",
                "browser_logs" => "FailedTestBrowserLogs",
                "browser_screenshot" => "FailedTestScreenshot",
                "di_scope_dispose" => "FixtureScopeDispose",
                "di_global_dispose" => "GlobalServiceDispose",
                "nunit_listener_reflection" => "RetryListenerReview",
                _ => throw new Exception("unknown effect " + kind)
            };
            var mappings = effects.Select(e =>
                new ReviewedPlaywrightLifetimeBinding(e.Kind, e.SourcePath,
                    e.SourceLine, e.ExpressionSha256, Role(e.Kind), true))
                .ToArray();

            ReviewedPlaywrightLifetimePlan Review(
                IReadOnlyList<ReviewedPlaywrightLifetimeBinding>? mapped,
                string infrastructure = SourceInfrastructureType,
                string? expectedHash = null) =>
                engine.ReviewPlaywrightFixtureLifetime(source,
                    SourceBaseType, SourceNestedType, infrastructure,
                    expectedHash ?? observed.SourceTreeHash, mapped);

            var missing = Review(null);
            Check(missing.Status == "blocked" && missing.Candidate == null
                && missing.Issues.Any(issue =>
                    issue.Code == "PLAYWRIGHT_LIFETIME_SOURCE_BINDING_MISSING"),
                "missing bindings must never produce runnable code");
            var uncheckedMapping = mappings.ToArray();
            uncheckedMapping[0] = uncheckedMapping[0] with { Reviewed = false };
            Check(Review(uncheckedMapping).Issues.Any(issue =>
                    issue.Code == "PLAYWRIGHT_LIFETIME_ROLE_REVIEW_REQUIRED"),
                "unreviewed source role must block");
            var staleRole = mappings.ToArray();
            staleRole[0] = staleRole[0] with { TargetRole = "GlobalServiceDispose" };
            Check(Review(staleRole).Status == "blocked",
                "wrong source role must block");
            var duplicate = mappings.Concat(new[] { mappings[0] }).ToArray();
            Check(Review(duplicate).Issues.Any(issue =>
                    issue.Code == "PLAYWRIGHT_LIFETIME_EXTRA_BINDING"),
                "extra or duplicate mappings must block");
            var wrongInfrastructure = Review(mappings,
                "Fictional.Fixture.MissingContainer");
            Check(wrongInfrastructure.Status == "blocked"
                && wrongInfrastructure.Issues.Any(issue =>
                    issue.Code == "PLAYWRIGHT_LIFETIME_DI_SOURCE_UNRESOLVED"),
                "missing source DI container must block");

            var reviewed = Review(mappings);
            Check(reviewed.SchemaVersion ==
                "migrator-reviewed-playwright-lifetime/v1alpha1"
                && reviewed.Status == "requires_review"
                && reviewed.Candidate is { Sha256.Length: 64 }
                && reviewed.ValidatedBindings.Count == effects.Length
                && reviewed.InfrastructureMethods.Count == 3
                && reviewed.InfrastructureMethods.All(m =>
                    m.SourceMethodSha256.Length == 64)
                && reviewed.BehavioralEquivalenceVerified == false
                && reviewed.SourceNUnitRuntime == "not_run"
                && reviewed.TargetBuild == "not_run"
                && reviewed.TargetNUnitRuntime == "not_run"
                && reviewed.BrowserE2E == "not_run",
                "full source binding must remain a review-only lifecycle candidate");
            var generated = reviewed.Candidate!.Content;
            foreach (var required in new[]
            {
                "IBrowserContext", "IPage",
                "OpenFixtureContextAsync", "GetPageAsync",
                "WriteFailedBrowserLogsAsync",
                "SaveFailedScreenshotAsync", "ReleaseFixtureServicesAsync",
                "Context.CloseAsync()", "FullDisposeAsync",
                "public abstract class FixtureBase",
                "public class NestedCleanup",
                "[OneTimeTearDown]", "[TearDown]"
            })
                Check(generated.Contains(required, StringComparison.Ordinal),
                    "typed target lifecycle missing " + required);
            Check(!generated.Contains("IWebDriver", StringComparison.Ordinal)
                && !generated.Contains("IPooledWebDriver", StringComparison.Ordinal)
                && !generated.Contains("Selenium", StringComparison.Ordinal),
                "generated candidate must not retain Selenium implementation");

            File.AppendAllText(Path.Combine(source, "DiContainer.cs"),
                "\n// exact source changed\n");
            try
            {
                Review(mappings);
                throw new Exception("stale source hash accepted");
            }
            catch (InvalidOperationException exception) when (
                exception.Message.StartsWith("STALE_PLAYWRIGHT_LIFETIME_SOURCE",
                    StringComparison.Ordinal)) { }
            File.WriteAllText(Path.Combine(source, "DiContainer.cs"),
                File.ReadAllText(Path.Combine(source, "DiContainer.cs"))
                    .Replace("\n// exact source changed\n", "",
                        StringComparison.Ordinal));

            var dir = Path.Combine(parent, "target-synthetic");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, reviewed.Candidate.RelativePath),
                generated);
            File.WriteAllText(Path.Combine(dir, "SyntheticTests.cs"),
                SyntheticRuntime);
            File.WriteAllText(Path.Combine(dir, "Synthetic.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                <PropertyGroup>
                  <TargetFramework>net10.0</TargetFramework>
                  <IsTestProject>true</IsTestProject>
                  <IsPackable>false</IsPackable>
                  <Nullable>enable</Nullable>
                  <ImplicitUsings>enable</ImplicitUsings>
                </PropertyGroup>
                <ItemGroup>
                  <PackageReference Include="Microsoft.Playwright" Version="1.63.0" />
                  <PackageReference Include="NUnit" Version="4.3.2" />
                  <PackageReference Include="NUnit3TestAdapter" Version="5.1.0" />
                  <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                </ItemGroup>
                </Project>
                """);

            var passed = Execute(dir, "passed", 2, 0);
            Check(passed.Count(x => x.StartsWith("fixture-open:", StringComparison.Ordinal))
                    == 2
                && passed.Count(x => x == "global-dispose") == 1
                && passed.Count(x => x.StartsWith("context-close:", StringComparison.Ordinal))
                    == 2
                && !passed.Contains("failed-logs")
                && !passed.Contains("failed-screenshot")
                && passed.Last() == "global-dispose",
                "success: distinct fixture context/page, no failure artifacts, "
                + "one global cleanup after contexts");

            var failed = Execute(dir, "test-failure", 1, 1);
            var logIndex = failed.FindIndex(x => x == "failed-logs");
            var screenshotIndex = failed.FindIndex(x => x == "failed-screenshot");
            var releaseIndex = failed.FindIndex(x =>
                x.StartsWith("fixture-release:", StringComparison.Ordinal));
            Check(logIndex >= 0 && screenshotIndex > logIndex
                && releaseIndex > screenshotIndex
                && failed.Last() == "global-dispose",
                "test failure: logs before screenshot before fixture/global cleanup");

            var releaseError = Execute(dir, "release-failure", 1, 1);
            Check(releaseError.Count(x =>
                    x.StartsWith("context-close:", StringComparison.Ordinal)) == 2
                && releaseError.Last() == "global-dispose",
                "scope release exception must still close the typed context");

            var alteredSource = File.ReadAllText(Path.Combine(source, "DiContainer.cs"))
                .Replace("ScopeMap.TryRemove", "ScopeMap.TryGetValue",
                    StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(source, "DiContainer.cs"),
                alteredSource);
            var altered = engine.InspectNUnitLifecycle(source);
            var renewed = mappings.Select(m => m).ToArray();
            var changed = Review(renewed, expectedHash: altered.SourceTreeHash);
            Check(changed.Status == "blocked"
                && changed.Issues.Any(issue =>
                    issue.Code == "PLAYWRIGHT_LIFETIME_DI_OWNERSHIP_UNPROVEN"),
                "changing source DI scope release semantics must fail closed");
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    private static List<string> Execute(
        string directory, string scenario, int passed, int failed)
    {
        var trace = Path.Combine(directory, "events.txt");
        if (File.Exists(trace)) File.Delete(trace);
        var resultDir = Path.Combine(directory, "results-" + scenario);
        Directory.CreateDirectory(resultDir);
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in new[]
        {
            "test", Path.Combine(directory, "Synthetic.csproj"),
            "--configuration", "Release",
            "--logger", "trx;LogFileName=result.trx",
            "--results-directory", resultDir,
            "--verbosity", "quiet"
        }) psi.ArgumentList.Add(arg);
        psi.Environment["SYNTHETIC_LIFETIME_EVENTS"] = trace;
        psi.Environment["SYNTHETIC_LIFETIME_SCENARIO"] = scenario;
        using var proc = Process.Start(psi) ??
            throw new Exception("synthetic dotnet test failed to start");
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(240_000))
        {
            proc.Kill(entireProcessTree: true);
            throw new Exception("synthetic Playwright NUnit runtime timed out");
        }
        var outText = stdout.GetAwaiter().GetResult();
        var errText = stderr.GetAwaiter().GetResult();
        Check((proc.ExitCode == 0) == (failed == 0),
            "synthetic test process exit: " + outText[
                ^Math.Min(1500, outText.Length)..] + errText[
                ^Math.Min(1500, errText.Length)..]);
        var report = XDocument.Load(Path.Combine(resultDir, "result.trx"));
        var counters = report.Descendants().Single(node =>
            node.Name.LocalName == "Counters");
        Check(counters.Attribute("total")?.Value == (passed + failed).ToString()
            && counters.Attribute("passed")?.Value == passed.ToString()
            && counters.Attribute("failed")?.Value == failed.ToString(),
            "synthetic NUnit TRX count/status mismatch: "
            + JsonSerializer.Serialize(counters.Attributes()
                .ToDictionary(a => a.Name.LocalName, a => a.Value))
            + " expectedPassed=" + passed + " expectedFailed=" + failed
            + " stdout=" + outText[^Math.Min(1000, outText.Length)..]);
        return File.ReadAllLines(trace).ToList();
    }

    private static void Check(bool condition, string why)
    {
        if (!condition)
            throw new InvalidOperationException(
                "Reviewed Playwright lifetime smoke: " + why);
    }

    private const string SyntheticRuntime = """
using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;
using NUnit.Framework;
using Fictional.Fixture;

namespace Fictional.Fixture
{
    public static class Trace
    {
        private static readonly object Sync = new();
        public static void Note(string eventName)
        {
            var path = Environment.GetEnvironmentVariable(
                "SYNTHETIC_LIFETIME_EVENTS")!;
            lock (Sync) File.AppendAllText(path, eventName + "\n");
        }
        public static string Scenario =>
            Environment.GetEnvironmentVariable("SYNTHETIC_LIFETIME_SCENARIO")!;
    }

    public class FakeProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == "NewPageAsync")
            {
                Trace.Note("page-create");
                return Task.FromResult(DispatchProxy.Create<IPage, FakeProxy>());
            }
            if (method?.Name == "CloseAsync")
            {
                Trace.Note("context-close:" + TestContext.CurrentContext.Test.ClassName);
                return Task.CompletedTask;
            }
            if (method?.Name == "GotoAsync")
            {
                Trace.Note("page-navigation");
                return Task.FromResult<IResponse?>(null);
            }
            throw new InvalidOperationException(
                "Unexpected synthetic Playwright invocation: " + method?.Name);
        }
    }

    public sealed class FakeBridge : IMigratorReviewedPlaywrightLifecycleBridge
    {
        private static readonly FakeBridge InstanceValue = new();
        public static FakeBridge Instance => InstanceValue;
        private int _active;

        public Task<IBrowserContext> OpenFixtureContextAsync(string key)
        {
            Interlocked.Increment(ref _active);
            Trace.Note("fixture-open:" + key);
            return Task.FromResult(
                DispatchProxy.Create<IBrowserContext, FakeProxy>());
        }

        public Task InstallRetryPolicyAsync(string key)
        {
            Trace.Note("retry-policy:" + key);
            return Task.CompletedTask;
        }

        public Task WriteFailedBrowserLogsAsync(IPage page, string key)
        {
            Trace.Note("failed-logs");
            return Task.CompletedTask;
        }

        public Task SaveFailedScreenshotAsync(IPage page, string key)
        {
            Trace.Note("failed-screenshot");
            return Task.CompletedTask;
        }

        public Task ReleaseFixtureServicesAsync(string key)
        {
            Trace.Note("fixture-release:" + key);
            Interlocked.Decrement(ref _active);
            if (Trace.Scenario == "release-failure" &&
                key.Contains("Secondary", StringComparison.Ordinal))
                throw new InvalidOperationException("synthetic-release-failed");
            return Task.CompletedTask;
        }

        public Task FullDisposeAsync()
        {
            if (Volatile.Read(ref _active) != 0)
                throw new InvalidOperationException(
                    "Global cleanup before fixture scopes release");
            Trace.Note("global-dispose");
            return Task.CompletedTask;
        }
    }

    public static class BridgeBoot
    {
        [ModuleInitializer]
        public static void Configure()
        {
            FixtureBase.Bridge = FakeBridge.Instance;
        }
    }

    [TestFixture]
    public sealed class PrimaryFixture : FixtureBase
    {
        [Test] public async Task First()
        {
            var page = await GetPageAsync();
            Assert.That(page, Is.Not.Null);
            await NavigateAsync("https://example.invalid/test");
            if (Trace.Scenario == "test-failure")
                Assert.Fail("intentional synthetic failure");
        }
    }

    [TestFixture]
    public sealed class SecondaryFixture : FixtureBase
    {
        [Test] public async Task Second()
        {
            Assert.That(await GetPageAsync(), Is.Not.Null);
        }
    }
}
""";
}
