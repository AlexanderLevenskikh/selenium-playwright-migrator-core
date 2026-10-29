using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Migrator.Application;

/// <summary>
/// A concrete, opt-in Playwright browser/DI host. Its owned C# is returned to
/// local reviewers only; MCP exposes hash/path metadata. The user must still
/// configure it with a real application service provider and browser policy.
/// </summary>
internal static class ConcretePlaywrightDiBridgeRenderer
{
    internal const string FileName = "Migrator.Generated.ConcretePlaywrightDiBridge.cs";

    internal static string Render(string targetNamespace)
    {
        if (targetNamespace.Split('.').Any(segment =>
                !SyntaxFacts.IsValidIdentifier(segment)))
            throw new InvalidOperationException(
                "PLAYWRIGHT_BRIDGE_TARGET_NAMESPACE_INVALID");
        const string source = """
// REVIEW-ONLY concrete Playwright/NUnit DI bridge.
// The application must explicitly configure this bridge; no auth, app URL,
// selectors or customer services are inferred. NUnit Retry attributes on test
// methods are required and independently reviewed.
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;

namespace __TARGET_NAMESPACE__;

public sealed class MigratorPlaywrightBridgeOptions
{
    public required string ArtifactDirectory { get; init; }
    public int RequiredRetryAttempts { get; init; } = 4;
    public int MaxConsoleMessages { get; init; } = 500;
    public int Width { get; init; } = 2560;
    public int Height { get; init; } = 1440;
    public bool Headless { get; init; } = true;
    public bool FullPageScreenshots { get; init; } = true;
    // Optional opt-in lifecycle event hook for real browser/resource audits.
    // Emits semantic event names only, never selectors/URLs/credentials.
    public Action<string>? LifecycleObserver { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ArtifactDirectory)
            || !Path.IsPathFullyQualified(ArtifactDirectory)
            || RequiredRetryAttempts is < 1 or > 20
            || MaxConsoleMessages is < 1 or > 10000
            || Width is < 320 or > 8192 || Height is < 240 or > 8192)
            throw new ArgumentException(
                "PLAYWRIGHT_BRIDGE_OPTIONS_INVALID");
    }
}

/// <summary>
/// A real IBrowser-backed implementation of the reviewed NUnit lifecycle
/// interface. It uses a singleton IBrowser and one isolated BrowserContext
/// per fixture class, with an explicit IServiceScope for each fixture.
/// The surrounding generated base owns Context.CloseAsync in a finally.
/// </summary>
public sealed class MigratorPlaywrightDiBridge :
    IMigratorReviewedPlaywrightLifecycleBridge, IAsyncDisposable
{
    private sealed record FixtureScope(
        IServiceScope ServiceScope,
        IBrowserContext Context,
        ConcurrentQueue<string> ConsoleMessages);

    private readonly IServiceProvider _rootProvider;
    private readonly IBrowser _browser;
    private readonly IPlaywright? _playwright;
    private readonly MigratorPlaywrightBridgeOptions _options;
    private readonly bool _ownsBrowser;
    private readonly bool _ownsProvider;
    private readonly ConcurrentDictionary<string, FixtureScope> _fixtures =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _globalGate = new(1, 1);
    private int _disposed;

    public IBrowser Browser => _browser;
    public int LiveFixtureScopes => _fixtures.Count;
    private void Observe(string eventName) =>
        _options.LifecycleObserver?.Invoke(eventName);

    public MigratorPlaywrightDiBridge(IServiceProvider rootProvider,
        IBrowser sharedBrowser, MigratorPlaywrightBridgeOptions options,
        bool ownsBrowser = false, bool ownsProvider = false)
        : this(rootProvider, sharedBrowser, null, options,
            ownsBrowser, ownsProvider)
    {
    }

    private MigratorPlaywrightDiBridge(IServiceProvider rootProvider,
        IBrowser sharedBrowser, IPlaywright? playwright,
        MigratorPlaywrightBridgeOptions options,
        bool ownsBrowser, bool ownsProvider)
    {
        _rootProvider = rootProvider
            ?? throw new ArgumentNullException(nameof(rootProvider));
        _browser = sharedBrowser
            ?? throw new ArgumentNullException(nameof(sharedBrowser));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _rootProvider.GetRequiredService<IServiceScopeFactory>();
        _playwright = playwright;
        _ownsBrowser = ownsBrowser;
        _ownsProvider = ownsProvider;
    }

    // Real Chromium launch path, explicitly invoked by application boot code.
    // CI can inject an IBrowser to test typed lifecycle without a browser
    // download or external credentials.
    public static async Task<MigratorPlaywrightDiBridge> LaunchChromiumAsync(
        IServiceProvider rootProvider, MigratorPlaywrightBridgeOptions options,
        bool ownsProvider = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var playwright = await Playwright.CreateAsync();
        try
        {
            var browser = await playwright.Chromium.LaunchAsync(
                new BrowserTypeLaunchOptions { Headless = options.Headless });
            return new MigratorPlaywrightDiBridge(rootProvider, browser,
                playwright, options, ownsBrowser: true, ownsProvider);
        }
        catch
        {
            playwright.Dispose();
            throw;
        }
    }

    public async Task<IBrowserContext> OpenFixtureContextAsync(
        string fixtureClassName)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(fixtureClassName))
            throw new ArgumentException(
                "PLAYWRIGHT_BRIDGE_FIXTURE_KEY_REQUIRED");
        if (_fixtures.ContainsKey(fixtureClassName))
            throw new InvalidOperationException(
                "PLAYWRIGHT_BRIDGE_FIXTURE_ALREADY_ACQUIRED");

        var services = _rootProvider.CreateScope();
        Observe("fixture-scope-created");
        IBrowserContext? context = null;
        try
        {
            context = await _browser.NewContextAsync(
                new BrowserNewContextOptions
                {
                    ViewportSize = new ViewportSize
                    {
                        Width = _options.Width, Height = _options.Height
                    }
                });
            Observe("context-opened");
            context.Close += (_, _) => Observe("context-closed");
            var messages = new ConcurrentQueue<string>();
            context.Page += (_, page) =>
            {
                page.Console += (_, message) =>
                    AddMessage(messages, "console:" + message.Type
                        + ":" + message.Text);
                page.PageError += (_, error) =>
                    AddMessage(messages, "page-error:" + error);
            };
            var entry = new FixtureScope(services, context, messages);
            if (!_fixtures.TryAdd(fixtureClassName, entry))
                throw new InvalidOperationException(
                    "PLAYWRIGHT_BRIDGE_FIXTURE_ALREADY_ACQUIRED");
            return context;
        }
        catch
        {
            if (context != null)
                await context.CloseAsync();
            try
            {
                services.Dispose();
            }
            finally
            {
                Observe("fixture-scope-disposed");
            }
            throw;
        }
    }

    // NUnit supplies the actual retry mechanism through explicit [Retry(N)]
    // annotations. This validates the policy, it does not mutate private
    // NUnit listeners or claim the source listener's failure semantics.
    public Task InstallRetryPolicyAsync(string fixtureClassName)
    {
        ThrowIfDisposed();
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(
                fixtureClassName, throwOnError: false))
            .FirstOrDefault(candidate => candidate != null);
        if (type == null)
            throw new InvalidOperationException(
                "PLAYWRIGHT_BRIDGE_NUNIT_FIXTURE_NOT_FOUND");

        var methods = type.GetMethods(BindingFlags.Public
                | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(method => method.GetCustomAttributesData().Any(attr =>
                attr.AttributeType.Name is "TestAttribute" or "TestCaseAttribute"
                    or "TestCaseSourceAttribute"))
            .ToArray();
        if (methods.Length == 0)
            throw new InvalidOperationException(
                "PLAYWRIGHT_BRIDGE_NUNIT_TESTS_NOT_FOUND");
        foreach (var method in methods)
        {
            var retries = method.GetCustomAttributesData()
                .Where(attr => attr.AttributeType.FullName
                    == "NUnit.Framework.RetryAttribute")
                .ToArray();
            if (retries.Length != 1
                || retries[0].ConstructorArguments.Count != 1
                || retries[0].ConstructorArguments[0].Value is not int attempts
                || attempts != _options.RequiredRetryAttempts)
                throw new InvalidOperationException(
                    "PLAYWRIGHT_BRIDGE_RETRY_POLICY_REQUIRES_REVIEW");
        }
        return Task.CompletedTask;
    }

    public async Task WriteFailedBrowserLogsAsync(
        IPage page, string fixtureClassName)
    {
        ThrowIfDisposed();
        var entry = GetScope(fixtureClassName, page);
        if (entry.ConsoleMessages.IsEmpty)
            return; // source writes no browser log artifact when empty
        var path = ArtifactPath("browser-logs", ".txt");
        await File.WriteAllLinesAsync(path,
            entry.ConsoleMessages.ToArray());
        TestContext.AddTestAttachment(path, "Playwright browser console logs");
        Observe("failed-logs-saved");
    }

    public async Task SaveFailedScreenshotAsync(
        IPage page, string fixtureClassName)
    {
        ThrowIfDisposed();
        GetScope(fixtureClassName, page);
        var path = ArtifactPath("screenshot", ".png");
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = path, FullPage = _options.FullPageScreenshots
        });
        TestContext.AddTestAttachment(path, "Playwright failure screenshot");
        Observe("failed-screenshot-saved");
    }

    // The generated base closes IBrowserContext itself in finally, including
    // when this service scope release throws. The shared IBrowser stays alive.
    public Task ReleaseFixtureServicesAsync(string fixtureClassName)
    {
        ThrowIfDisposed();
        if (!_fixtures.TryRemove(fixtureClassName, out var lease))
            throw new InvalidOperationException(
                "PLAYWRIGHT_BRIDGE_FIXTURE_SCOPE_MISSING");
        try
        {
            lease.ServiceScope.Dispose();
        }
        finally
        {
            Observe("fixture-scope-disposed");
        }
        return Task.CompletedTask;
    }

    public async Task FullDisposeAsync()
    {
        await _globalGate.WaitAsync();
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            if (!_fixtures.IsEmpty)
                throw new InvalidOperationException(
                    "PLAYWRIGHT_BRIDGE_LIVE_FIXTURE_SCOPES");
            if (_ownsBrowser)
            {
                await _browser.CloseAsync();
                Observe("browser-closed");
            }
            _playwright?.Dispose();
            if (_ownsProvider && _rootProvider is IDisposable disposable)
            {
                disposable.Dispose();
                Observe("provider-disposed");
            }
            Volatile.Write(ref _disposed, 1);
            Observe("full-dispose-complete");
        }
        finally
        {
            _globalGate.Release();
        }
    }

    public ValueTask DisposeAsync() => new(FullDisposeAsync());

    private FixtureScope GetScope(string key, IPage page)
    {
        if (!_fixtures.TryGetValue(key, out var scope)
            || !ReferenceEquals(page.Context, scope.Context))
            throw new InvalidOperationException(
                "PLAYWRIGHT_BRIDGE_PAGE_SCOPE_MISMATCH");
        return scope;
    }

    private string ArtifactPath(string label, string suffix)
    {
        Directory.CreateDirectory(_options.ArtifactDirectory);
        var name = TestContext.CurrentContext.Test.FullName;
        var digest = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(name + ":" + label))).ToLowerInvariant();
        return Path.Combine(_options.ArtifactDirectory,
            "migrator-" + label + "-" + digest[..20] + suffix);
    }

    private void AddMessage(ConcurrentQueue<string> messages, string text)
    {
        messages.Enqueue(text.Length > 4096 ? text[..4096] : text);
        while (messages.Count > _options.MaxConsoleMessages)
            messages.TryDequeue(out _);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(MigratorPlaywrightDiBridge));
    }
}
""";
        var result = source.Replace("__TARGET_NAMESPACE__", targetNamespace,
            StringComparison.Ordinal) + "\n";
        if (CSharpSyntaxTree.ParseText(result).GetDiagnostics()
            .Any(issue => issue.Severity == DiagnosticSeverity.Error))
            throw new InvalidOperationException(
                "PLAYWRIGHT_BRIDGE_CONCRETE_TARGET_SYNTAX_INVALID");
        return result;
    }
}
