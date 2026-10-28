using Migrator.Application;

internal static class PomSourceBoundRetrySmoke
{
    public static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pom-source-bound-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            // Fictional classes/selectors: retain only the generic retry shape.
            File.WriteAllText(Path.Combine(folder, "Loader.cs"), """
                public class Loader
                {
                    public void VerifyReady()
                    {
                        var limit = 2;
                        var retries = 0;
                        while (retries < limit)
                            try
                            {
                                if (error.Displayed().Get())
                                {
                                    error.ExecuteJs("window.location.reload()");
                                    loading.Visible.Wait().EqualTo(false);
                                }
                                marker.Visible.Wait().EqualTo(true);
                                loading.Visible.Wait().EqualTo(false);
                                break;
                            }
                            catch (WebDriverTimeoutException)
                            {
                                retries++;
                                if (retries == limit) Assert.Fail("not ready");
                            }
                    }
                }
                """);

            var inspector = new PomSourceInspector();
            var evidence = inspector.Inspect(folder, new[] { "Loader" });
            var helper = evidence.Helpers.Single(h => h.SourceType == "Loader" && h.Method == "VerifyReady");
            Check(helper.NumericLocalConstants.TryGetValue("limit", out var value) && value == "2",
                "literal, immutable retry limit must be recorded");

            string[] candidate =
            {
                "var retries = 0;",
                "while (retries < 2)",
                "{",
                "  try",
                "  {",
                "    if (await Page.Locator(\"#error\").IsVisibleAsync())",
                "    {",
                "      await Page.ReloadAsync();",
                "      await Page.Locator(\"#loading\").WaitForAsync(new() { State = WaitForSelectorState.Hidden });",
                "    }",
                "    await Page.Locator(\"#marker\").WaitForAsync(new() { State = WaitForSelectorState.Visible });",
                "    await Page.Locator(\"#loading\").WaitForAsync(new() { State = WaitForSelectorState.Hidden });",
                "    break;",
                "  }",
                "  catch (System.TimeoutException)",
                "  {",
                "    retries++;",
                "    if (retries == 2) Assert.Fail(\"not ready\");",
                "  }",
                "}"
            };
            PomMappingReview Review(string[] items) => new PomMappingReviewer(inspector).Review(
                folder, "Loader", "VerifyReady", helper.SourceLine, evidence.SourceTreeHash, items);
            string[] Change(string from, string to) => candidate
                .Select(item => item.Replace(from, to, StringComparison.Ordinal)).ToArray();

            var good = Review(candidate);
            Check(good.Status == "requires_review" && good.MissingObligations.Count == 0,
                "equivalent numeric limit forms and explicit hidden/visible wait categories must remain review-only");
            Check(good.Obligations.Count(o => o.Effect == "reload") == 1,
                "literal JS location.reload is a navigation side effect, not an unclassified helper");
            Check(!good.ReviewFlags.Contains("source_retry_guard_changed")
                && !good.ReviewFlags.Contains("source_retry_failure_guard_changed")
                && !good.ReviewFlags.Contains("source_wait_visibility_inverted"),
                "source aliases and required waiting directions should match");

            var wrongLoop = Review(Change("while (retries < 2)", "while (retries < 3)"));
            Check(wrongLoop.Status == "rejected_incomplete"
                && wrongLoop.ReviewFlags.Contains("source_retry_guard_changed"),
                "numeric aliases cannot disguise a changed retry cap");

            var wrongFailure = Review(Change("if (retries == 2)", "if (retries >= 2)"));
            Check(wrongFailure.Status == "rejected_incomplete"
                && wrongFailure.ReviewFlags.Contains("source_retry_failure_guard_changed"),
                "changing failure guard comparison cannot be silently accepted");

            var wrongWait = Review(Change("WaitForSelectorState.Hidden", "WaitForSelectorState.Visible"));
            Check(wrongWait.Status == "rejected_incomplete"
                && wrongWait.ReviewFlags.Contains("source_wait_visibility_inverted")
                && wrongWait.MissingObligations.Any(o => o.Effect == "state_wait"),
                "hidden-state waits must not be converted to visible-state waits");

            var wrongReload = Review(Change("await Page.ReloadAsync();", "await Page.Locator(\"#noop\").ClickAsync();"));
            Check(wrongReload.Status == "rejected_incomplete"
                && wrongReload.MissingObligations.Any(o => o.Effect == "reload"),
                "literal reload cannot be replaced with a click");

            var changedConditional = Review(Change(
                "      await Page.ReloadAsync();",
                "      ;").SelectMany((item, index) => index == 9
                    ? new[] { item, "    await Page.ReloadAsync();" } : new[] { item }).ToArray());
            Check(changedConditional.Status == "rejected_incomplete"
                && changedConditional.ReviewFlags.Contains("conditional_reload_moved_outside_branch"),
                "moved reload outside conditional branch must be rejected");

            // An opaque JavaScript expression remains unclassified, not a reload.
            var otherFile = Path.Combine(folder, "Opaque.cs");
            File.WriteAllText(otherFile, """
                public class Opaque {
                    public void ExecuteUnknown(string script) {
                        element.ExecuteJs(script);
                    }
                }
                """);
            var updated = inspector.Inspect(folder, new[] { "Opaque" });
            var opaque = updated.Helpers.Single(h => h.SourceType == "Opaque");
            var opaqueReview = new PomMappingReviewer(inspector).Review(folder, "Opaque",
                opaque.Method, opaque.SourceLine, updated.SourceTreeHash,
                new[] { "await Page.ReloadAsync();" });
            Check(opaqueReview.Status == "rejected_incomplete"
                && opaqueReview.MissingObligations.Any(o => o.Effect == "script_execution"),
                "dynamic JavaScript cannot be assumed to reload the page");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static void Check(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException("Source-bound retry smoke: " + message);
    }
}
