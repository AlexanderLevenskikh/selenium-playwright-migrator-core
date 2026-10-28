using Migrator.Application;

internal static class PomMappingReviewerSmoke
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "pom-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "ComboBox.cs"), """
                public class ComboBox {
                    public void InputAndSelect(string value) {
                        Open();
                        input.Visible().Wait().EqualTo(true);
                        input.ClearAndInputText(value);
                        menuItems.Wait().Single(x => x.Text, Contains.Substring(value)).Click();
                        accept.Click();
                    }
                    public void Open() { Click(); }
                }
                """);
            File.WriteAllText(Path.Combine(root, "DatePicker.cs"), """
                public class DatePicker {
                    public void SetBeginDate(string text) {
                        Click();
                        input.Displayed().Wait().EqualTo(true);
                        input.SendKeys(text);
                        Blur();
                    }
                    private void Blur() { input.ExecuteJs("x.blur();"); }
                }
                """);
            File.WriteAllText(Path.Combine(root, "Loader.cs"), """
                public class Loader {
                    public void ValidateLoading() {
                        var attempt = 0;
                        while (attempt < 2) {
                            try {
                                if (error.Displayed().Get()) Reload();
                                marker.Visible().Wait().EqualTo(true);
                                break;
                            }
                            catch (WebDriverTimeoutException) {
                                attempt++;
                                if (attempt == 2) Assert.Fail("not ready");
                            }
                        }
                    }
                }
                """);
            File.WriteAllText(Path.Combine(root, "LocalStorage.cs"), """
                public class LocalStorage {
                    public void SettingPeriod(string a, string b, string c, string d, string e) {
                        SetItem("first", a);
                        SetItem("second", b);
                        SetItem("third", c);
                        SetItem("fourth", d);
                        SetItem("fifth", e);
                    }
                    public void SetItem(string key, string value) {
                        executor.ExecuteScript("localStorage.setItem");
                    }
                }
                """);
            File.WriteAllText(Path.Combine(root, "Other.cs"), """
                public class Other {
                    public void Overloaded(int n) { Click(); }
                    public void Overloaded(string n) { Click(); }
                }
                """);

            var inspector = new PomSourceInspector();
            var reviewer = new PomMappingReviewer(inspector);
            var source = inspector.Inspect(root, new[]
            { "ComboBox", "DatePicker", "Loader", "LocalStorage", "Other" });
            var hash = source.SourceTreeHash;
            var combo = source.Helpers.Single(h => h.SourceType == "ComboBox"
                && h.Method == "InputAndSelect");
            var simple = reviewer.Review(root, "ComboBox", combo.Method, combo.SourceLine,
                hash, new[] { "await Page.Locator(\"#input\").FillAsync(value);" });
            Assert(simple.Status == "rejected_incomplete"
                && simple.MissingObligations.Any(x => x.Effect == "click")
                && simple.MissingObligations.Any(x => x.Effect == "state_wait"),
                "Fill-only shortcut must not pass as a complete combo-box mapping");
            Assert(simple.Obligations.Count >= 6, "Open helper and menu wait/click must not disappear");
            Assert(simple.Obligations[0].Effect == "click", "Open must expand to source click");

            var full = reviewer.Review(root, "ComboBox", combo.Method, combo.SourceLine, hash,
                new[]
                {
                    "await Page.Locator(\"#picker\").ClickAsync();",
                    "await Page.Locator(\"#input\").WaitForAsync();",
                    "await Page.Locator(\"#input\").FillAsync(value);",
                    "var options = Page.Locator(\"#menu\").Filter(new() { HasTextString = value });",
                    "await options.WaitForAsync();",
                    "await options.ClickAsync();",
                    "await Page.Locator(\"#accept\").ClickAsync();"
                });
            Assert(full.MissingObligations.Count == 0
                && full.Status == "requires_review"
                && !full.BehaviorallyVerified,
                "Complete operation-category sequence still requires human/DOM review");
            Assert(full.SourceMethodSha256 == combo.MethodSha256,
                "mapping review must be tied to actual source method");
            Assert(full.ParameterFlows.Single(x => x.ParameterName == "value") is
                { SourceArgumentUses: 2, TargetArgumentUses: 2, Preserved: true },
                "both input and menu filter must retain the source argument");

            var wrongArgument = reviewer.Review(root, "ComboBox", combo.Method,
                combo.SourceLine, hash, new[]
                {
                    "await Page.Locator(\"#picker\").ClickAsync();",
                    "await Page.Locator(\"#input\").WaitForAsync();",
                    "await Page.Locator(\"#input\").FillAsync(\"wrong literal\");",
                    "var options = Page.Locator(\"#menu\").Filter(new() { HasTextString = \"wrong literal\" });",
                    "await options.WaitForAsync();",
                    "await options.ClickAsync();",
                    "await Page.Locator(\"#accept\").ClickAsync();"
                });
            Assert(wrongArgument.MissingObligations.Count == 0
                && wrongArgument.Status == "rejected_incomplete"
                && wrongArgument.ReviewFlags.Contains("source_parameter_usage_not_preserved"),
                "category-complete but hardcoded argument must not pass");
            Assert(wrongArgument.ParameterFlows.Single().TargetArgumentUses == 0,
                "string literal names do not count as source argument forwarding");

            var halfBound = reviewer.Review(root, "ComboBox", combo.Method,
                combo.SourceLine, hash, new[]
                {
                    "await Page.Locator(\"#picker\").ClickAsync();",
                    "await Page.Locator(\"#input\").WaitForAsync();",
                    "await Page.Locator(\"#input\").FillAsync(value);",
                    "var options = Page.Locator(\"#menu\").Filter(new() { HasTextString = \"wrong\" });",
                    "await options.WaitForAsync();",
                    "await options.ClickAsync();",
                    "await Page.Locator(\"#accept\").ClickAsync();"
                });
            Assert(halfBound.Status == "rejected_incomplete"
                && halfBound.ParameterFlows.Single().TargetArgumentUses == 1,
                "losing the filter argument must fail even if FillAsync forwards it");

            var escapedSelector = reviewer.Review(root, "ComboBox", combo.Method,
                combo.SourceLine, hash, new[]
                {
                    """var combo = Page.Locator("[data-test=\\\"picker\\\"]");""",
                    "await combo.ClickAsync();",
                    "await Page.Locator(\"#input\").WaitForAsync();",
                    "await Page.Locator(\"#input\").FillAsync(value);",
                    "var options = Page.Locator(\"#menu\").Filter(new() { HasTextString = value });",
                    "await options.WaitForAsync();",
                    "await options.ClickAsync();",
                    "await Page.Locator(\"#accept\").ClickAsync();"
                });
            Assert(escapedSelector.Status == "blocked"
                && escapedSelector.ReviewFlags.Contains("target_selector_suspicious_double_escaping"),
                "double-escaped CSS attribute quotes must be blocked rather than compile-green");

            var picker = source.Helpers.Single(h => h.SourceType == "DatePicker"
                && h.Method == "SetBeginDate");
            var lostBlur = reviewer.Review(root, "DatePicker", picker.Method,
                picker.SourceLine, hash, new[] { "await Page.Locator(\"#input\").FillAsync(text);" });
            Assert(lostBlur.Status == "rejected_incomplete"
                && lostBlur.MissingObligations.Any(x => x.Effect == "script_execution"),
                "DatePicker private blur must not silently vanish");

            var loader = source.Helpers.Single(h => h.SourceType == "Loader"
                && h.Method == "ValidateLoading");
            var lostRetry = reviewer.Review(root, "Loader", loader.Method,
                loader.SourceLine, hash, new[] { "await Page.Locator(\"#loading\").WaitForAsync();" });
            Assert(lostRetry.Status == "rejected_incomplete"
                && lostRetry.MissingObligations.Any(x => x.Effect == "loop")
                && lostRetry.MissingObligations.Any(x => x.Effect == "catch"),
                "one wait cannot replace original retry and exception paths");

            var storage = source.Helpers.Single(h => h.SourceType == "LocalStorage"
                && h.Method == "SettingPeriod");
            var oneWrite = reviewer.Review(root, "LocalStorage", storage.Method,
                storage.SourceLine, hash, new[] {
                    "await Page.EvaluateAsync(\"localStorage.setItem('first', a)\");"
                });
            Assert(oneWrite.Status == "rejected_incomplete"
                && oneWrite.Obligations.Count(x => x.Effect == "storage_write") == 5,
                "multi-key storage helper cannot collapse to one write");

            var completeStorage = reviewer.Review(root, "LocalStorage", storage.Method,
                storage.SourceLine, hash, new[] {
                    "await Page.EvaluateAsync(\"([key,value]) => localStorage.setItem(key,value)\", new object[] { \"first\", a });",
                    "await Page.EvaluateAsync(\"([key,value]) => localStorage.setItem(key,value)\", new object[] { \"second\", b });",
                    "await Page.EvaluateAsync(\"([key,value]) => localStorage.setItem(key,value)\", new object[] { \"third\", c });",
                    "await Page.EvaluateAsync(\"([key,value]) => localStorage.setItem(key,value)\", new object[] { \"fourth\", d });",
                    "await Page.EvaluateAsync(\"([key,value]) => localStorage.setItem(key,value)\", new object[] { \"fifth\", e });"
                });
            Assert(completeStorage.Status == "requires_review"
                && completeStorage.ParameterFlows.Count == 5
                && completeStorage.ParameterFlows.All(x => x.Preserved)
                && !completeStorage.BehaviorallyVerified,
                "matching storage write counts do NOT prove origin, ordering or serialized values");

            var invalid = reviewer.Review(root, "ComboBox", combo.Method,
                combo.SourceLine, hash, new[] { "await Page.Locator(\"#input\").ClickAsync(;" });
            Assert(invalid.Status == "blocked"
                && invalid.ReviewFlags.Any(x => x.StartsWith("target_syntax_", StringComparison.Ordinal)),
                "invalid C# target template must not be reviewed as complete");

            try
            {
                reviewer.Review(root, "Other", "Overloaded", 0, hash,
                    new[] { "await Page.ClickAsync();" });
                throw new Exception("Ambiguous overload accepted");
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("AMBIGUOUS_POM_METHOD", StringComparison.Ordinal)) {}

            File.AppendAllText(Path.Combine(root, "ComboBox.cs"), "\n// stale");
            try
            {
                reviewer.Review(root, "ComboBox", combo.Method, combo.SourceLine, hash,
                    new[] { "await Page.ClickAsync();" });
                throw new Exception("Stale source inspection accepted");
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("STALE_POM_INSPECTION", StringComparison.Ordinal)) {}
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    static void Assert(bool value, string explanation)
    {
        if (!value)
            throw new InvalidOperationException("POM mapping coverage regression: " + explanation);
    }
}
