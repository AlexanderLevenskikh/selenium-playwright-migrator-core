using Microsoft.CodeAnalysis.CSharp;

namespace Migrator.Application;

/// <summary>
/// Renders an exact, review-only NUnit fixture adapter. The user supplies a
/// real typed bridge implementing external DI/browser ownership and artifacts.
/// This never guesses application URLs, selectors, credentials or services.
/// </summary>
internal static class PlaywrightFixtureLifetimeRenderer
{
    public static string Render(string namespaceName, string baseName,
        string nestedName, string once, string after, string onceAfter,
        string nestedAfter)
    {
        var identifiers = new[] { baseName, nestedName, once, after,
            onceAfter, nestedAfter };
        if (namespaceName.Length == 0 ||
            namespaceName.Split('.').Any(segment =>
                !SyntaxFacts.IsValidIdentifier(segment)) ||
            identifiers.Any(identifier =>
                !SyntaxFacts.IsValidIdentifier(identifier)))
            throw new InvalidOperationException(
                "PLAYWRIGHT_LIFETIME_UNSAFE_TARGET_IDENTIFIERS");
        // C# member names must be unique only *within the same class*.
        // A valid nested SetUpFixture may define End() while its enclosing
        // abstract fixture has its own End() teardown method. Conflating
        // these two independent scopes caused an unnecessary MCP review error.
        if (baseName == nestedName ||
            new[] { once, after, onceAfter }.Distinct(
                StringComparer.Ordinal).Count() != 3 ||
            new[] { once, after, onceAfter }.Contains(baseName,
                StringComparer.Ordinal) ||
            nestedAfter == nestedName)
            throw new InvalidOperationException(
                "PLAYWRIGHT_LIFETIME_TARGET_NAME_COLLISION");

        const string template = """
// REVIEW-ONLY Playwright fixture lifecycle candidate.
// Source-attested structure, NOT source/target behavior equivalence.
// A reviewer must provide a real NUnit/DI/browser bridge before use.
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;
using NUnit.Framework;
using NUnit.Framework.Interfaces;

namespace __NAMESPACE__;

public interface IMigratorReviewedPlaywrightLifecycleBridge
{
    // Returns a fresh, fixture-owned context for a shared browser/provider.
    Task<IBrowserContext> OpenFixtureContextAsync(string fixtureClassName);
    // Explicit replacement for the source's internal NUnit listener/retry policy.
    Task InstallRetryPolicyAsync(string fixtureClassName);
    Task WriteFailedBrowserLogsAsync(IPage page, string fixtureClassName);
    Task SaveFailedScreenshotAsync(IPage page, string fixtureClassName);
    // Does NOT close IBrowserContext; generated code owns Context.CloseAsync.
    Task ReleaseFixtureServicesAsync(string fixtureClassName);
    // Clears shared browser/provider after fixture scopes return resources.
    Task FullDisposeAsync();
}

public abstract class __BASE__
{
    public static IMigratorReviewedPlaywrightLifecycleBridge Bridge
        { get; set; } = default!;

    private sealed record FixtureLease(
        IBrowserContext Context, IPage Page);

    private static readonly ConcurrentDictionary<string,
        Lazy<Task<FixtureLease>>> FixtureScopes = new(
            StringComparer.Ordinal);
    private static int GlobalReleased;

    private static IMigratorReviewedPlaywrightLifecycleBridge RequiredBridge() =>
        Bridge ?? throw new InvalidOperationException(
            "PLAYWRIGHT_LIFETIME_BRIDGE_NOT_CONFIGURED");

    private static string FixtureKey
    {
        get
        {
            var value = TestContext.CurrentContext.Test.ClassName;
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException(
                    "PLAYWRIGHT_LIFETIME_FIXTURE_KEY_UNAVAILABLE");
            return value;
        }
    }

    private static async Task<FixtureLease> AcquireAsync(string fixtureKey)
    {
        if (Volatile.Read(ref GlobalReleased) != 0)
            throw new InvalidOperationException(
                "PLAYWRIGHT_LIFETIME_ALREADY_GLOBAL_RELEASED");
        var bridge = RequiredBridge();
        var lazy = FixtureScopes.GetOrAdd(fixtureKey,
            key => new Lazy<Task<FixtureLease>>(async () =>
            {
                var context = await bridge.OpenFixtureContextAsync(key);
                try
                {
                    var page = await context.NewPageAsync();
                    return new FixtureLease(context, page);
                }
                catch
                {
                    await context.CloseAsync();
                    throw;
                }
            }, LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await lazy.Value;
        }
        catch
        {
            FixtureScopes.TryRemove(fixtureKey, out _);
            throw;
        }
    }

    protected async Task<IPage> GetPageAsync() =>
        (await AcquireAsync(FixtureKey)).Page;

    protected async Task<IBrowserContext> GetBrowserContextAsync() =>
        (await AcquireAsync(FixtureKey)).Context;

    // Navigation and LocalStorage now bind explicitly to the same IPage.
    // Actual POM methods and DOM timing still require source-backed mapping.
    protected async Task NavigateAsync(string url)
    {
        await (await GetPageAsync()).GotoAsync(url);
    }

    protected async Task<T> EvaluatePageAsync<T>(string expression) =>
        await (await GetPageAsync()).EvaluateAsync<T>(expression);

    [OneTimeSetUp]
    public async Task __ONCE__()
    {
        await RequiredBridge().InstallRetryPolicyAsync(FixtureKey);
    }

    [TearDown]
    public async Task __AFTER__()
    {
        // Source resolves WebDriver at TearDown even on successful tests.
        // The fixture-scoped Playwright context is therefore acquired here
        // even if a test body never requested its page.
        var key = FixtureKey;
        var lease = await AcquireAsync(key);
        if (TestContext.CurrentContext.Result.Outcome != ResultState.Success)
        {
            // Preserve source ordering: logs precede screenshot. If logs
            // throw, do not silently conceal that failure and proceed.
            await RequiredBridge().WriteFailedBrowserLogsAsync(lease.Page, key);
            await RequiredBridge().SaveFailedScreenshotAsync(lease.Page, key);
        }
    }

    [OneTimeTearDown]
    public async Task __ONCE_AFTER__()
    {
        var key = FixtureKey;
        if (!FixtureScopes.TryRemove(key, out var scoped))
            throw new InvalidOperationException(
                "PLAYWRIGHT_LIFETIME_SCOPE_NOT_ACQUIRED");
        var lease = await scoped.Value;
        try
        {
            await RequiredBridge().ReleaseFixtureServicesAsync(key);
        }
        finally
        {
            // Always close the context, even when DI scope release fails.
            await lease.Context.CloseAsync();
        }
    }

    // NUnit 3 and 4 execute this nested source-style fixture once at
    // namespace scope, including failed setup. Do not make it per-test
    // or silently promote it to assembly-global scope.
    [SetUpFixture]
    public class __NESTED__
    {
        [OneTimeTearDown]
        public async Task __NESTED_AFTER__()
        {
            if (!FixtureScopes.IsEmpty)
                throw new InvalidOperationException(
                    "PLAYWRIGHT_LIFETIME_LIVE_FIXTURE_SCOPES");
            if (Interlocked.CompareExchange(ref GlobalReleased, 1, 0) != 0)
                return;
            try
            {
                await RequiredBridge().FullDisposeAsync();
            }
            catch
            {
                Volatile.Write(ref GlobalReleased, 0);
                throw;
            }
        }
    }
}
""";
        return template
            .Replace("__NAMESPACE__", namespaceName,
                StringComparison.Ordinal)
            .Replace("__BASE__", baseName, StringComparison.Ordinal)
            .Replace("__NESTED__", nestedName, StringComparison.Ordinal)
            .Replace("__ONCE__", once, StringComparison.Ordinal)
            .Replace("__AFTER__", after, StringComparison.Ordinal)
            .Replace("__ONCE_AFTER__", onceAfter, StringComparison.Ordinal)
            .Replace("__NESTED_AFTER__", nestedAfter, StringComparison.Ordinal)
            + "\n";
    }
}
