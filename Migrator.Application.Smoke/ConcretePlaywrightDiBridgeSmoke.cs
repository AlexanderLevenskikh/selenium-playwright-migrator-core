using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using Migrator.Application;

internal static class ConcretePlaywrightDiBridgeSmoke
{
    private static void Assert(bool ok, string why)
    {
        if (!ok) throw new InvalidOperationException(
            "Concrete Playwright DI smoke: " + why);
    }

    public static void Run()
    {
        var parent = Path.Combine(Path.GetTempPath(),
            "migrator-concrete-runtime-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(parent, "original");
        var target = Path.Combine(parent, "target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        try
        {
            File.WriteAllText(Path.Combine(source, "Base.cs"), """
                using NUnit.Framework;
                namespace Synthetic.PlaywrightHost;
                public abstract class FixtureBase
                {
                    protected IWebDriver Driver =>
                        DiContainer.GetScopedRequiredService<IPooledWebDriver>().WrappedDriver;
                    protected Navigation Navigation =>
                        DiContainer.GetScopedRequiredService<Navigation>();
                    protected LocalStorage LocalStorage =>
                        DiContainer.GetScopedRequiredService<LocalStorage>();
                    private Screenshoter Screenshoter =>
                        DiContainer.GetScopedRequiredService<Screenshoter>();
                    [OneTimeSetUp] public void Initialize() {
                        InstallListener();
                    }
                    [TearDown] public void SaveFailureArtifacts() {
                        TestContext.CurrentContext.WriteBrowserLogs(Driver);
                        Screenshoter.CreateAndSaveScreenshot();
                    }
                    [OneTimeTearDown] public void Release() {
                        DiContainer.DisposeScope();
                    }
                    private void InstallListener() {
                        var listener = new RetryTestListener();
                        TestExecutionContext.CurrentContext.GetType().GetProperties();
                    }
                    [SetUpFixture] public class NestedCleanup {
                        [OneTimeTearDown] public void FullDispose() {
                            DiContainer.FullDispose();
                        }
                    }
                }
                """);
            File.WriteAllText(Path.Combine(source, "DI.cs"), """
                using NUnit.Framework;
                namespace Synthetic.PlaywrightHost;
                public static class DiContainer
                {
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
                            throw new Exception("missing source scope");
                        scope.Dispose();
                    }
                    public static void FullDispose() {
                        ServiceProvider.GetRequiredService<IBrowserPool>().Clear();
                        ServiceProvider.Dispose();
                    }
                }
                """);
            File.WriteAllText(Path.Combine(source, "Test.cs"), """
                using NUnit.Framework;
                namespace Synthetic.PlaywrightHost;
                [TestFixture] public sealed class FixtureA : FixtureBase
                {
                    [Test] public void A() {}
                }
                """);

            var engine = new MigrationEngine();
            var ledger = engine.InspectNUnitLifecycle(source);
            const string baseType = "Synthetic.PlaywrightHost.FixtureBase";
            const string nested = baseType + ".NestedCleanup";
            const string di = "Synthetic.PlaywrightHost.DiContainer";
            var roleMap = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["di_scoped_service"] = "FixtureService",
                ["browser_driver_scope"] = "BrowserContextPage",
                ["browser_navigation_scope"] = "PageNavigation",
                ["browser_storage_scope"] = "PageStorage",
                ["browser_logs"] = "FailedTestBrowserLogs",
                ["browser_screenshot"] = "FailedTestScreenshot",
                ["di_scope_dispose"] = "FixtureScopeDispose",
                ["di_global_dispose"] = "GlobalServiceDispose",
                ["nunit_listener_reflection"] = "RetryListenerReview"
            };
            var bindings = ledger.Effects.Where(effect =>
                effect.SourceType == baseType || effect.SourceType == nested)
                .Select(effect => new ReviewedPlaywrightLifetimeBinding(
                    effect.Kind, effect.SourcePath, effect.SourceLine,
                    effect.ExpressionSha256, roleMap[effect.Kind], true))
                .ToArray();
            var plan = engine.ReviewPlaywrightFixtureLifetime(
                source, baseType, nested, di, ledger.SourceTreeHash, bindings);
            Assert(plan.Status == "requires_review"
                && plan.Candidate != null && plan.ConcreteBridge != null
                && plan.Candidate.Sha256.Length == 64
                && plan.ConcreteBridge.Sha256.Length == 64
                && plan.Candidate.RelativePath != plan.ConcreteBridge.RelativePath
                && !plan.BehavioralEquivalenceVerified
                && plan.TargetBuild == "not_run"
                && plan.TargetNUnitRuntime == "not_run",
                "new concrete artifact must remain source-attested and unexecuted");
            Assert(plan.ConcreteBridge.Content.Contains(
                    "Playwright.CreateAsync()", StringComparison.Ordinal)
                && plan.ConcreteBridge.Content.Contains(
                    "IBrowserContext", StringComparison.Ordinal)
                && plan.ConcreteBridge.Content.Contains(
                    "IServiceScope", StringComparison.Ordinal)
                && plan.ConcreteBridge.Content.Contains(
                    "PageScreenshotOptions", StringComparison.Ordinal),
                "concrete bridge must own real Playwright and DI types");

            File.WriteAllText(Path.Combine(target, plan.Candidate.RelativePath),
                plan.Candidate.Content);
            File.WriteAllText(Path.Combine(target,
                plan.ConcreteBridge.RelativePath), plan.ConcreteBridge.Content);
            File.WriteAllText(Path.Combine(target, "Fixtures.cs"),
                TargetFixtureSource);
            File.WriteAllText(Path.Combine(target, "Target.csproj"), """
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
                    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.0" />
                    <PackageReference Include="NUnit" Version="4.3.2" />
                    <PackageReference Include="NUnit3TestAdapter" Version="5.1.0" />
                    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                  </ItemGroup>
                </Project>
                """);

            var success = Execute(target, "success", expectedPass: 2,
                expectedFail: 0, expectedExit: 0);
            Assert(success.Count(x => x.StartsWith("context-open:",
                    StringComparison.Ordinal)) == 2
                && success.Count(x => x.StartsWith("context-close:",
                    StringComparison.Ordinal)) == 2
                && success.Count(x => x.StartsWith("scope-create:",
                    StringComparison.Ordinal)) == 2
                && success.Count(x => x.StartsWith("scope-release:",
                    StringComparison.Ordinal)) == 2
                && success.Count(x => x == "browser-close") == 1
                && success.Count(x => x == "root-dispose") == 1
                && !success.Any(x => x.StartsWith("screenshot:",
                    StringComparison.Ordinal))
                && success.Last() == "root-dispose",
                "real typed concrete bridge must isolate 2 contexts and release shared resources once");

            var retried = Execute(target, "retry-once", expectedPass: 2,
                expectedFail: 0, expectedExit: 0);
            var retryAttempts = retried.Count(x => x == "primary-attempt");
            var intermediateScreenshots = retried.Count(x =>
                x.StartsWith("screenshot:", StringComparison.Ordinal));
            Console.WriteLine("NUNIT_RETRY_OBSERVATION: attempts="
                + retryAttempts + " intermediate_screenshots="
                + intermediateScreenshots + " events="
                + string.Join("|", retried));
            Assert(retryAttempts >= 2
                && retried.Count(x => x == "browser-close") == 1,
                "explicit NUnit [Retry(4)] must retry while preserving "
                + "one shared browser cleanup. Events: "
                + string.Join("|", retried));
            // NUnit's [Retry] can invoke TearDown after unsuccessful
            // intermediate attempts. The original listener may not:
            // record those artifacts but do not mistake this for parity.

            var failed = Execute(target, "failed-test", expectedPass: 1,
                expectedFail: 1, expectedExit: 1);
            var logAt = failed.FindIndex(x => x.StartsWith(
                "artifact-logs:", StringComparison.Ordinal));
            var shotAt = failed.FindIndex(x => x.StartsWith(
                "screenshot:", StringComparison.Ordinal));
            var releaseAt = failed.FindIndex(x => x.StartsWith(
                "scope-release:", StringComparison.Ordinal));
            var logFiles = Directory.GetFiles(
                Path.Combine(target, "artifacts-failed-test"),
                "migrator-browser-logs-*.txt");
            Assert(logFiles.Length > 0
                && logFiles.Any(file => File.ReadAllText(file).Contains(
                    "synthetic browser message", StringComparison.Ordinal))
                && logAt >= 0 && shotAt > logAt && releaseAt > shotAt
                && failed.Count(x => x == "browser-close") == 1
                && failed.Last() == "root-dispose",
                "failed test logs must precede screenshot and fixture/global disposal. "
                + string.Join("|", failed));

            var teardownFailure = Execute(target, "scope-failure",
                expectedPass: 2, expectedFail: 0, expectedExit: 1);
            Assert(teardownFailure.Count(x => x.StartsWith(
                    "context-close:", StringComparison.Ordinal)) == 2
                && teardownFailure.Count(x => x == "browser-close") == 1,
                "DI dispose exception must not skip Context.CloseAsync and shared browser cleanup");

            var pageFailure = Execute(target, "page-failure",
                expectedPass: 1, expectedFail: 1, expectedExit: 1);
            Assert(pageFailure.Contains("page-create-failed")
                && pageFailure.Count(x => x.StartsWith("scope-create:",
                    StringComparison.Ordinal)) ==
                    pageFailure.Count(x => x.StartsWith("scope-release:",
                        StringComparison.Ordinal))
                && pageFailure.Count(x => x.StartsWith("context-open:",
                    StringComparison.Ordinal)) ==
                    pageFailure.Count(x => x.StartsWith("context-close:",
                        StringComparison.Ordinal))
                && pageFailure.Count(x => x == "browser-close") == 1
                && pageFailure.Last() == "root-dispose",
                "failed NewPageAsync must not leak scope or context before "
                + "nested global cleanup. Events: "
                + string.Join("|", pageFailure));

            // Remove only the first fixture's [Retry(4)] attribute to
            // prove the bridge fails the suite closed rather than silently
            // changing the original listener's retry policy.
            var testPath = Path.Combine(target, "Fixtures.cs");
            var originalTests = File.ReadAllText(testPath);
            const string annotated = "[Test, Retry(4)]";
            var firstRetry = originalTests.IndexOf(annotated,
                StringComparison.Ordinal);
            Assert(firstRetry >= 0, "synthetic NUnit retry attribute missing");
            File.WriteAllText(testPath,
                originalTests[..firstRetry] + "[Test]"
                + originalTests[(firstRetry + annotated.Length)..]);
            var missingRetry = Execute(target, "missing-retry",
                expectedPass: 1, expectedFail: 1, expectedExit: 1);
            Assert(!missingRetry.Any(x => x.StartsWith("context-open:",
                    StringComparison.Ordinal)),
                "without explicit method-level NUnit [Retry(N)] do not silently activate test suite");
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    private static List<string> Execute(string target, string scenario,
        int expectedPass, int expectedFail, int expectedExit)
    {
        var trace = Path.Combine(target, "trace-" + scenario + ".txt");
        var artifacts = Path.Combine(target, "artifacts-" + scenario);
        var results = Path.Combine(target, "results-" + scenario);
        Directory.CreateDirectory(artifacts);
        Directory.CreateDirectory(results);
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in new[]
        {
            "test", Path.Combine(target, "Target.csproj"),
            "--configuration", "Release",
            "--logger", "trx;LogFileName=result.trx",
            "--results-directory", results,
            "--verbosity", "quiet"
        }) psi.ArgumentList.Add(arg);
        psi.Environment["MIGRATOR_SYNTH_TRACE"] = trace;
        psi.Environment["MIGRATOR_SYNTH_SCENARIO"] = scenario;
        psi.Environment["MIGRATOR_SYNTH_ARTIFACTS"] = artifacts;
        using var p = Process.Start(psi)
            ?? throw new Exception("dotnet synthetic test did not start");
        var output = p.StandardOutput.ReadToEndAsync();
        var errors = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(240_000))
        {
            p.Kill(entireProcessTree: true);
            throw new Exception("dotnet synthetic bridge test timeout");
        }
        var stdout = output.GetAwaiter().GetResult();
        var stderr = errors.GetAwaiter().GetResult();
        Assert((p.ExitCode == 0) == (expectedExit == 0),
            "unexpected dotnet runner exit: " + stdout[
                ^Math.Min(1800, stdout.Length)..] + stderr[
                ^Math.Min(1800, stderr.Length)..]);
        var report = XDocument.Load(Path.Combine(results, "result.trx"));
        var c = report.Descendants().Single(x => x.Name.LocalName == "Counters");
        Assert(c.Attribute("total")?.Value == "2"
            && c.Attribute("passed")?.Value == expectedPass.ToString()
            && c.Attribute("failed")?.Value == expectedFail.ToString(),
            "real synthetic NUnit TRX mismatch: "
            + JsonSerializer.Serialize(c.Attributes().ToDictionary(x =>
                x.Name.LocalName, x => x.Value)) + " stdout="
            + stdout[^Math.Min(1400, stdout.Length)..]);
        return File.ReadAllLines(trace).ToList();
    }

    private const string TargetFixtureSource = """
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Synthetic.PlaywrightHost;

public static class Trace
{
    private static readonly object Sync = new();
    public static string Scenario =>
        Environment.GetEnvironmentVariable("MIGRATOR_SYNTH_SCENARIO")!;
    public static void Note(string text)
    {
        var path = Environment.GetEnvironmentVariable("MIGRATOR_SYNTH_TRACE")!;
        lock (Sync) File.AppendAllText(path, text + "\n");
    }
}

public class RecordedProxy : DispatchProxy
{
    public string Kind = "";
    public IBrowserContext? ContextObject;
    private EventHandler<IPage>? _page;
    private EventHandler<IConsoleMessage>? _console;

    public static T New<T>(string kind) where T : class
    {
        var created = DispatchProxy.Create<T, RecordedProxy>();
        ((RecordedProxy)(object)created).Kind = kind;
        return created;
    }

    protected override object? Invoke(MethodInfo? m, object?[]? args)
    {
        switch (Kind, m?.Name)
        {
            case ("browser", "NewContextAsync"):
                var context = New<IBrowserContext>("context");
                ((RecordedProxy)(object)context).ContextObject = context;
                Trace.Note("context-open:" + Guid.NewGuid().ToString("N"));
                return Task.FromResult(context);
            case ("browser", "CloseAsync"):
                Trace.Note("browser-close");
                return Task.CompletedTask;
            case ("context", "add_Page"):
                _page += (EventHandler<IPage>)args![0]!;
                return null;
            case ("context", "remove_Page"):
                _page -= (EventHandler<IPage>)args![0]!;
                return null;
            case ("context", "NewPageAsync"):
                if (Trace.Scenario == "page-failure"
                    && TestContext.CurrentContext.Test.ClassName?.Contains(
                        "PrimaryFixture", StringComparison.Ordinal) == true)
                {
                    Trace.Note("page-create-failed");
                    throw new InvalidOperationException(
                        "synthetic failure after context creation");
                }
                var page = New<IPage>("page");
                ((RecordedProxy)(object)page).ContextObject = ContextObject;
                _page?.Invoke(null, page);
                Trace.Note("page-create");
                return Task.FromResult(page);
            case ("context", "CloseAsync"):
                Trace.Note("context-close:" + Guid.NewGuid().ToString("N"));
                return Task.CompletedTask;
            case ("page", "get_Context"):
                return ContextObject;
            case ("page", "add_Console"):
                _console += (EventHandler<IConsoleMessage>)args![0]!;
                return null;
            case ("page", "remove_Console"):
                _console -= (EventHandler<IConsoleMessage>)args![0]!;
                return null;
            case ("page", "add_PageError"):
            case ("page", "remove_PageError"):
                return null;
            case ("page", "GotoAsync"):
                var msg = New<IConsoleMessage>("message");
                _console?.Invoke(null, msg);
                return Task.FromResult<IResponse?>(null);
            case ("message", "get_Type"):
                return "log";
            case ("message", "get_Text"):
                return "synthetic browser message";
            case ("page", "ScreenshotAsync"):
                var opts = (PageScreenshotOptions)args![0]!;
                var artifactDir = Environment.GetEnvironmentVariable(
                    "MIGRATOR_SYNTH_ARTIFACTS")!;
                var browserLogs = Directory.GetFiles(artifactDir,
                    "migrator-browser-logs-*.txt");
                if (browserLogs.Length > 0)
                    Trace.Note("artifact-logs:" +
                        Path.GetFileName(browserLogs[0]));
                File.WriteAllBytes(opts.Path!, new byte[] { 137, 80, 78, 71 });
                Trace.Note("screenshot:" + Path.GetFileName(opts.Path!));
                return Task.FromResult(new byte[] { 137, 80, 78, 71 });
            default:
                throw new InvalidOperationException(
                    "Unexpected fake Playwright call: " + Kind + "." + m?.Name);
        }
    }
}

public sealed class RecordedRoot : IServiceProvider, IServiceScopeFactory,
    IDisposable
{
    public object? GetService(Type type) =>
        type == typeof(IServiceScopeFactory) ? this : null;

    public IServiceScope CreateScope()
    {
        Trace.Note("scope-create:" + Guid.NewGuid().ToString("N"));
        return new RecordedScope(this);
    }

    public void Dispose() => Trace.Note("root-dispose");

    private sealed class RecordedScope : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; }
        public RecordedScope(RecordedRoot root) => ServiceProvider = root;
        public void Dispose()
        {
            Trace.Note("scope-release:" + Guid.NewGuid().ToString("N"));
            if (Trace.Scenario == "scope-failure" &&
                Interlocked.Increment(ref ReleasedCount) == 2)
                throw new InvalidOperationException("synthetic DI release failure");
        }
    }
    private static int ReleasedCount;
}

public static class Bootstrap
{
    [ModuleInitializer]
    public static void Init()
    {
        var options = new MigratorPlaywrightBridgeOptions
        {
            ArtifactDirectory =
                Environment.GetEnvironmentVariable("MIGRATOR_SYNTH_ARTIFACTS")!,
            RequiredRetryAttempts = 4
        };
        FixtureBase.Bridge = new MigratorPlaywrightDiBridge(
            new RecordedRoot(), RecordedProxy.New<IBrowser>("browser"),
            options, ownsBrowser: true, ownsProvider: true);
    }
}

[TestFixture]
public sealed class PrimaryFixture : FixtureBase
{
    private static int Attempts;
    [Test, Retry(4)]
    public async Task Runs()
    {
        Trace.Note("primary-attempt");
        var page = await GetPageAsync();
        Assert.That(page, Is.Not.Null);
        await NavigateAsync("https://example.invalid/synthetic");
        if (Trace.Scenario == "failed-test")
            Assert.Fail("synthetic failure for screenshot");
        if (Trace.Scenario == "retry-once" &&
            Interlocked.Increment(ref Attempts) == 1)
            Assert.Fail("synthetic first attempt only");
    }
}

[TestFixture]
public sealed class SecondaryFixture : FixtureBase
{
    [Test, Retry(4)]
    public async Task Runs()
    {
        Assert.That(await GetPageAsync(), Is.Not.Null);
    }
}
""";
}
