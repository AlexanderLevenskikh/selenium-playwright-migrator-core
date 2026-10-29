using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Migrator.Application;

internal static class RealChromiumBridgeSmoke
{
    private const string SourceBase = "Fictional.RealBrowser.FixtureBase";
    private const string Nested = SourceBase + ".NestedCleanup";
    private const string Container = "Fictional.RealBrowser.DiContainer";

    public static void Run()
    {
        var parent = Path.Combine(Path.GetTempPath(),
            "migrator-real-browser-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(parent, "source");
        var target = Path.Combine(parent, "target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        try
        {
            File.WriteAllText(Path.Combine(source, "Base.cs"), """
                using NUnit.Framework;
                namespace Fictional.RealBrowser;
                public abstract class FixtureBase {
                    protected IWebDriver Driver =>
                        DiContainer.GetScopedRequiredService<IPooledWebDriver>().WrappedDriver;
                    protected Navigation Navigation =>
                        DiContainer.GetScopedRequiredService<Navigation>();
                    protected LocalStorage LocalStorage =>
                        DiContainer.GetScopedRequiredService<LocalStorage>();
                    private Screenshoter Screenshoter =>
                        DiContainer.GetScopedRequiredService<Screenshoter>();

                    [OneTimeSetUp] public void Initialize() { InstallListener(); }
                    [TearDown] public void AfterTest() {
                        TestContext.CurrentContext.WriteBrowserLogs(Driver);
                        Screenshoter.CreateAndSaveScreenshot();
                    }
                    [OneTimeTearDown] public void AfterFixture() {
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
            File.WriteAllText(Path.Combine(source, "Container.cs"), """
                using NUnit.Framework;
                namespace Fictional.RealBrowser;
                public static class DiContainer {
                    private static readonly ConcurrentDictionary<string,IServiceScope>
                        ScopeMap = new();
                    private static ServiceProvider Provider;
                    private static string ScopeKey =>
                        TestContext.CurrentContext.Test.ClassName;
                    public static T GetScopedRequiredService<T>() {
                        var scope = ScopeMap.GetOrAdd(ScopeKey,
                            key => Provider.CreateScope());
                        return scope.ServiceProvider.GetRequiredService<T>();
                    }
                    public static void DisposeScope() {
                        if (!ScopeMap.TryRemove(ScopeKey, out var scope))
                            throw new Exception("Missing synthetic fixture scope");
                        scope.Dispose();
                    }
                    public static void FullDispose() {
                        Provider.GetRequiredService<IBrowserPool>().Clear();
                        Provider.Dispose();
                    }
                }
                """);
            File.WriteAllText(Path.Combine(source, "Tests.cs"), """
                using NUnit.Framework;
                namespace Fictional.RealBrowser;
                [TestFixture] public class First : FixtureBase {
                    [Test] public void One() {}
                }
                """);
            var engine = new MigrationEngine();
            var ledger = engine.InspectNUnitLifecycle(source);
            var roles = new Dictionary<string, string>(StringComparer.Ordinal)
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
            var bindings = ledger.Effects
                .Where(effect => effect.SourceType == SourceBase
                    || effect.SourceType == Nested)
                .Select(effect => new ReviewedPlaywrightLifetimeBinding(
                    effect.Kind, effect.SourcePath, effect.SourceLine,
                    effect.ExpressionSha256, roles[effect.Kind], true))
                .ToArray();
            var review = engine.ReviewPlaywrightFixtureLifetime(
                source, SourceBase, Nested, Container,
                ledger.SourceTreeHash, bindings);
            Check(review.Status == "requires_review"
                && review.ConcreteBridge is { Sha256.Length: 64 }
                && review.Candidate is { Sha256.Length: 64 }
                && !review.BehavioralEquivalenceVerified,
                "source-backed browser artifacts must remain review-only");
            File.WriteAllText(Path.Combine(target,
                review.Candidate!.RelativePath), review.Candidate.Content);
            File.WriteAllText(Path.Combine(target,
                review.ConcreteBridge!.RelativePath),
                review.ConcreteBridge.Content);
            File.WriteAllText(Path.Combine(target, "RealFixtures.cs"),
                BrowserFixtureSource);
            File.WriteAllText(Path.Combine(target, "RealBrowser.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <IsTestProject>true</IsTestProject>
                    <IsPackable>false</IsPackable>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
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

            Console.WriteLine("REAL_BROWSER_STAGE=compile_target");
            var build = RunProcess("dotnet",
                ["build", Path.Combine(target, "RealBrowser.csproj"),
                    "-c", "Release", "--verbosity", "quiet"],
                target, TimeSpan.FromMinutes(4));
            Check(build.ExitCode == 0,
                "real browser target must compile: " + build.Tail);

            var installer = Path.Combine(target, "bin", "Release",
                "net10.0", "playwright.ps1");
            Check(File.Exists(installer),
                "Playwright NuGet generated install helper is missing.");
            Console.WriteLine("REAL_BROWSER_STAGE=install_chromium");
            var install = RunProcess("pwsh",
                [installer, "install", "chromium"], target,
                TimeSpan.FromMinutes(8));
            Check(install.ExitCode == 0,
                "Playwright Chromium installation failed: " + install.Tail);

            var results = new List<object>();
            var published = Path.Combine(Environment.CurrentDirectory,
                "artifacts", "real-chromium-preview28");
            Directory.CreateDirectory(published);
            foreach (var scenario in new[] { "success", "retry-once",
                         "final-failure" })
            {
                var artifacts = Path.Combine(parent, "artifacts-" + scenario);
                var trace = Path.Combine(parent, "trace-" + scenario + ".txt");
                var trxDir = Path.Combine(parent, "trx-" + scenario);
                Directory.CreateDirectory(artifacts);
                Directory.CreateDirectory(trxDir);

                Console.WriteLine("REAL_BROWSER_STAGE=run_" + scenario);
                var test = RunProcess("dotnet",
                    ["test", Path.Combine(target, "RealBrowser.csproj"),
                        "-c", "Release", "--no-build", "--no-restore",
                        "--logger", "trx;LogFileName=result.trx",
                        "--results-directory", trxDir,
                        "--verbosity", "quiet"], target,
                    TimeSpan.FromMinutes(2),
                    new Dictionary<string, string>
                    {
                        ["MIGRATOR_REAL_TRACE"] = trace,
                        ["MIGRATOR_REAL_ARTIFACTS"] = artifacts,
                        ["MIGRATOR_REAL_SCENARIO"] = scenario
                    });
                var expectedPass = scenario == "final-failure" ? 1 : 2;
                var expectedFailed = scenario == "final-failure" ? 1 : 0;
                Check((test.ExitCode == 0) == (expectedFailed == 0),
                    "unexpected real Chromium NUnit exit: " + test.Tail);
                var trx = XDocument.Load(Path.Combine(trxDir, "result.trx"));
                var counts = trx.Descendants().Single(node =>
                    node.Name.LocalName == "Counters");
                Check(counts.Attribute("total")?.Value == "2"
                    && counts.Attribute("passed")?.Value
                        == expectedPass.ToString()
                    && counts.Attribute("failed")?.Value
                        == expectedFailed.ToString(),
                    "real Chromium test TRX disagrees: " +
                    JsonSerializer.Serialize(counts.Attributes()
                        .ToDictionary(a => a.Name.LocalName, a => a.Value))
                    + " output=" + test.Tail);
                var events = File.ReadAllLines(trace);
                int Count(string name) => events.Count(item => item == name);
                Check(Count("fixture-scope-created") == 2
                    && Count("fixture-scope-disposed") == 2
                    && Count("context-opened") == 2
                    && Count("context-closed") == 2
                    && Count("browser-closed") == 1
                    && Count("provider-disposed") == 1
                    && Count("full-dispose-complete") == 1
                    && events.ToList().IndexOf("browser-closed") >
                        Array.LastIndexOf(events, "context-closed"),
                    "real Chromium must release two fixture scopes/contexts "
                    + "before one shared browser and provider. Events="
                    + string.Join("|", events));

                var screenshots = Directory.GetFiles(artifacts,
                    "migrator-screenshot-*.png");
                var logFiles = Directory.GetFiles(artifacts,
                    "migrator-browser-logs-*.txt");
                if (scenario == "success")
                    Check(screenshots.Length == 0 && logFiles.Length == 0,
                        "successful real browser tests must not capture failure artifacts");
                else
                {
                    if (scenario == "final-failure")
                        Check(screenshots.Length >= 1 && logFiles.Length >= 1,
                            "failed real browser test needs screenshot/log attachments");
                    foreach (var screenshot in screenshots)
                    {
                        var png = File.ReadAllBytes(screenshot);
                        Check(png.Length > 100 &&
                            png.AsSpan(0, 8).SequenceEqual(
                                new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
                            "screenshot must be a genuine Chromium PNG");
                    }
                    foreach (var log in logFiles)
                        Check(File.ReadAllText(log).Contains(
                            "synthetic-console-boot", StringComparison.Ordinal),
                            "browser logs must include local JS console events");
                    if (scenario == "final-failure")
                        Check(Array.IndexOf(events, "failed-logs-saved") >= 0
                            && Array.IndexOf(events, "failed-screenshot-saved") >
                               Array.IndexOf(events, "failed-logs-saved"),
                            "failed browser logs must be saved before screenshot");
                }
                if (scenario == "final-failure")
                {
                    File.Copy(screenshots[0],
                        Path.Combine(published, "synthetic-final-failure.png"),
                        overwrite: true);
                    File.Copy(logFiles[0],
                        Path.Combine(published, "synthetic-browser-console.txt"),
                        overwrite: true);
                }
                var attempts = events.Count(e => e == "fixture-a-attempt");
                Check(attempts == (scenario == "success" ? 1
                    : scenario == "retry-once" ? 2 : 3),
                    "real Chromium NUnit retry count differs from explicit [Retry(3)]: "
                    + attempts + " scenario=" + scenario);
                results.Add(new
                {
                    scenario,
                    chromiumReal = true,
                    chromiumVersion = events.FirstOrDefault(x =>
                        x.StartsWith("browser-version:", StringComparison.Ordinal))
                        ?["browser-version:".Length..],
                    sourceBound = true,
                    executedTests = 2, passedTests = expectedPass,
                    failedTests = expectedFailed, attempts,
                    observedContextOpen = Count("context-opened"),
                    observedContextClosed = Count("context-closed"),
                    browserClose = Count("browser-closed"),
                    scopeDisposals = Count("fixture-scope-disposed"),
                    screenshotCount = screenshots.Length,
                    logFileCount = logFiles.Length,
                    browserE2E = "synthetic_localhost_passed"
                });
            }

            var summary = JsonSerializer.Serialize(new
            {
                schema = "migrator-real-chromium-smoke/v1",
                playwrightPackage = "1.63.0",
                chromium = "real_installed_playwright_chromium",
                targetMode = "real_IBrowser_localhost",
                application = "synthetic_loopback_http_only",
                customerE2E = "not_run",
                behavioralEquivalence = "not_verified",
                coreReviewStatus = review.Status,
                results
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(published, "summary.json"),
                summary);
            Console.WriteLine("REAL_CHROMIUM_SMOKE_PASSED=" + summary);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    private static (int ExitCode, string Tail) RunProcess(
        string program, string[] args, string workingDirectory,
        TimeSpan timeout, IReadOnlyDictionary<string, string>? environment = null)
    {
        var psi = new ProcessStartInfo(program)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        if (environment != null)
            foreach (var (key, value) in environment)
                psi.Environment[key] = value;
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException(
                "REAL_CHROMIUM_PROCESS_START_FAILED");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
            var partialStdout = output.IsCompletedSuccessfully
                ? output.GetAwaiter().GetResult() : "";
            var partialStderr = error.IsCompletedSuccessfully
                ? error.GetAwaiter().GetResult() : "";
            var partial = partialStdout + "\n" + partialStderr;
            var tracePath = environment != null &&
                environment.TryGetValue("MIGRATOR_REAL_TRACE", out var path)
                ? path : null;
            var lifecycleTrace = tracePath != null && File.Exists(tracePath)
                ? File.ReadAllText(tracePath) : "";
            throw new InvalidOperationException(
                "REAL_CHROMIUM_PROCESS_TIMEOUT: " + program + " "
                + string.Join(" ", args)
                + " output=" + partial[^Math.Min(1800, partial.Length)..]
                + " lifecycle=" + lifecycleTrace[^Math.Min(2200,
                    lifecycleTrace.Length)..]);
        }
        var body = output.GetAwaiter().GetResult() + "\n"
            + error.GetAwaiter().GetResult();
        return (process.ExitCode, body[^Math.Min(3500, body.Length)..]);
    }

    private static void Check(bool condition, string why)
    {
        if (!condition)
            throw new InvalidOperationException(
                "REAL_CHROMIUM_SMOKE: " + why);
    }

    private const string BrowserFixtureSource = """"
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Fictional.RealBrowser;

public static class Audit
{
    private static readonly object Lock = new();
    public static string Scenario =>
        Environment.GetEnvironmentVariable("MIGRATOR_REAL_SCENARIO")!;
    public static void Record(string eventName)
    {
        var trace = Environment.GetEnvironmentVariable(
            "MIGRATOR_REAL_TRACE")!;
        lock (Lock)
            File.AppendAllText(trace, eventName + "\n");
    }
}

internal sealed class LocalApplication : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Task _acceptLoop;
    public string Url { get; }

    public LocalApplication()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Url = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(Url);
        _listener.Start();
        _acceptLoop = Task.Run(Loop);
    }

    private async Task Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext request;
            try { request = await _listener.GetContextAsync(); }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            var html = """
                <!doctype html><html><head>
                <meta charset="utf-8"><title>Synthetic Playwright Test App</title>
                </head><body>
                <h1 id="app">Synthetic local browser app</h1>
                <button id="action" onclick="document.getElementById('status').textContent='clicked'">Action</button>
                <p id="status">ready</p>
                <script>console.log('synthetic-console-boot');</script>
                </body></html>
                """;
            var body = Encoding.UTF8.GetBytes(html);
            request.Response.StatusCode = 200;
            request.Response.ContentType = "text/html; charset=utf-8";
            request.Response.ContentLength64 = body.Length;
            try { await request.Response.OutputStream.WriteAsync(body); }
            finally { request.Response.Close(); }
        }
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
    }
}

// .NET module initializers run under CLR loader synchronization. Never
// synchronously block the loader thread on Playwright's asynchronous driver
// startup. NUnit OneTimeSetUp awaits this lazy bridge instead.
public sealed class AsyncChromiumBridge :
    IMigratorReviewedPlaywrightLifecycleBridge
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MigratorPlaywrightDiBridge? _concrete;

    private async Task<MigratorPlaywrightDiBridge> RealAsync()
    {
        if (_concrete != null) return _concrete;
        await _gate.WaitAsync();
        try
        {
            if (_concrete == null)
            {
                Audit.Record("browser-launch-enter");
                var provider = new ServiceCollection().BuildServiceProvider();
                var options = new MigratorPlaywrightBridgeOptions
                {
                    ArtifactDirectory = Environment.GetEnvironmentVariable(
                        "MIGRATOR_REAL_ARTIFACTS")!,
                    RequiredRetryAttempts = 3,
                    Width = 1280, Height = 720,
                    LifecycleObserver = Audit.Record
                };
                _concrete = await MigratorPlaywrightDiBridge.LaunchChromiumAsync(
                    provider, options, ownsProvider: true);
                Audit.Record("browser-version:" + _concrete.Browser.Version);
                _concrete.Browser.Disconnected += (_, _) =>
                    Audit.Record("browser-disconnected");
                Audit.Record("browser-launch-complete");
            }
            return _concrete;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task InstallRetryPolicyAsync(string fixtureClassName) =>
        await (await RealAsync()).InstallRetryPolicyAsync(fixtureClassName);

    public async Task<IBrowserContext> OpenFixtureContextAsync(string fixtureClassName) =>
        await (await RealAsync()).OpenFixtureContextAsync(fixtureClassName);

    public async Task WriteFailedBrowserLogsAsync(IPage page, string fixtureClassName) =>
        await (await RealAsync()).WriteFailedBrowserLogsAsync(page, fixtureClassName);

    public async Task SaveFailedScreenshotAsync(IPage page, string fixtureClassName) =>
        await (await RealAsync()).SaveFailedScreenshotAsync(page, fixtureClassName);

    public async Task ReleaseFixtureServicesAsync(string fixtureClassName) =>
        await (await RealAsync()).ReleaseFixtureServicesAsync(fixtureClassName);

    public async Task FullDisposeAsync() =>
        await (await RealAsync()).FullDisposeAsync();
}

public static class RuntimeBootstrap
{
    private static LocalApplication? App;
    public static string Url => App!.Url;

    [ModuleInitializer]
    public static void Configure()
    {
        Audit.Record("module-initializer-enter");
        App = new LocalApplication();
        Audit.Record("loopback-http-listening");
        FixtureBase.Bridge = new AsyncChromiumBridge();
        Audit.Record("module-initializer-complete");
    }
}

[TestFixture, NonParallelizable]
public sealed class FirstFixture : FixtureBase
{
    private static int Attempt;

    [Test, Retry(3)]
    public async Task StorageConsoleAndRetry()
    {
        Audit.Record("fixture-a-attempt");
        var context = await GetBrowserContextAsync();
        var page = await GetPageAsync();
        Assert.That(page.Context, Is.SameAs(context));
        await page.GotoAsync(RuntimeBootstrap.Url);
        Assert.That(await page.Locator("#app").TextContentAsync(),
            Is.EqualTo("Synthetic local browser app"));
        await page.Locator("#action").ClickAsync();
        Assert.That(await page.Locator("#status").TextContentAsync(),
            Is.EqualTo("clicked"));
        await page.EvaluateAsync(
            "localStorage.setItem('synthetic-fixture-isolation', 'A')");
        Assert.That(await page.EvaluateAsync<string>(
            "localStorage.getItem('synthetic-fixture-isolation')"),
            Is.EqualTo("A"));
        await page.EvaluateAsync("console.log('synthetic-console-A')");
        if (Audit.Scenario == "retry-once" &&
            Interlocked.Increment(ref Attempt) == 1)
            Assert.Fail("Synthetic first attempt failed by design");
        if (Audit.Scenario == "final-failure")
            Assert.Fail("Synthetic final failure by design");
    }
}

[TestFixture, NonParallelizable]
public sealed class SecondFixture : FixtureBase
{
    [Test, Retry(3)]
    public async Task FreshContextAndStorage()
    {
        var context = await GetBrowserContextAsync();
        var page = await GetPageAsync();
        Assert.That(page.Context, Is.SameAs(context));
        await page.GotoAsync(RuntimeBootstrap.Url);
        Assert.That(await page.EvaluateAsync<string?>(
            "localStorage.getItem('synthetic-fixture-isolation')"),
            Is.Null, "A different NUnit fixture must have isolated localStorage");
        await page.EvaluateAsync(
            "localStorage.setItem('synthetic-fixture-isolation', 'B')");
        Assert.That(await page.EvaluateAsync<string>(
            "localStorage.getItem('synthetic-fixture-isolation')"),
            Is.EqualTo("B"));
        await page.EvaluateAsync("console.error('synthetic-console-B')");
    }
}
"""";
}
