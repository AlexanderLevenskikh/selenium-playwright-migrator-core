using Migrator.Application;

internal static class PomLoaderControlFlowSmoke
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "pom-loader-flow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Entirely synthetic source; no customer Page Object, selector or URL.
            File.WriteAllText(Path.Combine(root, "Loader.cs"), """
                public sealed class Loader
                {
                    public void ValidateLoading()
                    {
                        var attempt = 0;
                        while (attempt < 2)
                        {
                            try
                            {
                                if (error.Displayed().Get()) { Reload(); }
                                marker.Visible().Wait().EqualTo(true);
                                break;
                            }
                            catch (WebDriverTimeoutException)
                            {
                                attempt++;
                                if (attempt == 2) Assert.Fail("retry exhausted");
                            }
                        }
                    }
                }
                """);

            var inspector = new PomSourceInspector();
            var evidence = inspector.Inspect(root, new[] { "Loader" });
            var helper = evidence.Helpers.Single(x => x.SourceType == "Loader"
                && x.Method == "ValidateLoading");
            var reviewer = new PomMappingReviewer(inspector);
            string[] candidate =
            {
                "var attempt = 0;",
                "while (attempt < 2) {",
                "  try {",
                "    if (await Page.Locator(\"#error\").IsVisibleAsync()) { await Page.ReloadAsync(); }",
                "    await Page.Locator(\"#ready\").WaitForAsync();",
                "    break;",
                "  } catch (System.TimeoutException) {",
                "    attempt++;",
                "    if (attempt == 2) Assert.Fail(\"retry exhausted\");",
                "  }",
                "}"
            };

            PomMappingReview Review(string[] statements) => reviewer.Review(
                root, "Loader", helper.Method, helper.SourceLine,
                evidence.SourceTreeHash, statements);

            var complete = Review(candidate);
            Check(complete.Status == "requires_review"
                && complete.MissingObligations.Count == 0
                && !complete.BehaviorallyVerified,
                "ordered Loader mapping remains review-only, never behaviorally verified");
            foreach (var effect in new[] { "state_check", "reload", "state_wait",
                         "loop_exit", "retry_counter_update", "catch", "assertion" })
            {
                Check(complete.Obligations.Any(o => o.Effect == effect)
                    && complete.TargetEffects.Any(o => o.Effect == effect),
                    "Loader needs both source and target obligation: " + effect);
            }
            Check(complete.ReviewFlags.Contains("exception_type_translation_requires_review"),
                "cross-framework timeout exception translation needs review");

            string[] Replace(string from, string to) => candidate
                .Select(statement => statement.Replace(from, to, StringComparison.Ordinal)).ToArray();

            var catchAll = Review(Replace("System.TimeoutException", "System.Exception"));
            Check(catchAll.Status == "rejected_incomplete"
                && catchAll.ReviewFlags.Contains("typed_source_catch_widened"),
                "a broad catch must not swallow new failure types");

            var unconditionalReload = Review(Replace(
                "if (await Page.Locator(\"#error\").IsVisibleAsync()) { await Page.ReloadAsync(); }",
                "if (await Page.Locator(\"#error\").IsVisibleAsync()) { } await Page.ReloadAsync();"));
            Check(unconditionalReload.Status == "rejected_incomplete"
                && unconditionalReload.ReviewFlags.Contains("conditional_reload_moved_outside_branch"),
                "conditional source reload must not become unconditional");

            var changedGuard = Review(Replace("while (attempt < 2)", "while (attempt <= 2)"));
            Check(changedGuard.Status == "rejected_incomplete"
                && changedGuard.ReviewFlags.Contains("source_retry_guard_changed"),
                "changing a retry bound/comparator must be rejected");

            var missingCounterUpdate = Review(Replace("attempt++;", ";"));
            Check(missingCounterUpdate.Status == "rejected_incomplete"
                && missingCounterUpdate.MissingObligations.Any(x => x.Effect == "retry_counter_update"),
                "loss of catch-path counter increment must not be silently accepted");

            var missingBreak = Review(Replace("    break;", "    ;"));
            Check(missingBreak.Status == "rejected_incomplete"
                && missingBreak.MissingObligations.Any(x => x.Effect == "loop_exit"),
                "early exit on a successful retry must remain observable");

            var missingStateCheck = Review(Replace(
                "if (await Page.Locator(\"#error\").IsVisibleAsync())",
                "if (true)"));
            Check(missingStateCheck.Status == "rejected_incomplete"
                && missingStateCheck.MissingObligations.Any(x => x.Effect == "state_check"),
                "state read controlling reload must not disappear");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException("POM Loader review regression: " + message);
    }
}
