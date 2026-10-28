using Migrator.Application;

internal static class PomMethodLineageSmoke
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "pom-method-lineage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "ScCombobox.cs"), """
                public class ScCombobox {
                    public void InputAndSelect(string value) {
                        Click();
                        acceptButton.Visible().Wait().EqualTo(true);
                        input.ClearAndInputText(value);
                        menuItems.Wait().Single(x => x.Text, Contains.Substring(value)).Click();
                        acceptButton.Click();
                    }
                    public void SelectElementItemByNumber(int number) {
                        Click();
                        menuItems.ElementAt(number).Click();
                        acceptButton.Click();
                    }
                }
                """);
            File.WriteAllText(Path.Combine(root, "Loader.cs"), """
                public class Loader {
                    public void ValidateLoading() {
                        var attempts = 0;
                        while (attempts < 2)
                            try {
                                if (error.Displayed().Get()) {
                                    Reload();
                                }
                                marker.Visible.Wait().EqualTo(true);
                                break;
                            } catch (WebDriverTimeoutException) {
                                attempts++;
                                if (attempts == 2) Assert.Fail("not ready");
                            }
                    }
                }
                """);
            File.WriteAllText(Path.Combine(root, "DatePicker.cs"), """
                public class DatePicker {
                    public void SetBeginDate(string value) {
                        Click();
                        input.SendKeys(value);
                        Blur();
                    }
                    private void Blur() {
                        input.ExecuteJs("x.blur();");
                    }
                }
                """);
            File.WriteAllText(Path.Combine(root, "LocalStorage.cs"), """
                public class LocalStorage {
                    public void SettingPeriod(string from, string to) {
                        SetItem("period-begin", from);
                        SetItem("period-end", to);
                    }
                    public void SetItem(string key, string value) {
                        executor.ExecuteScript("localStorage.setItem(...)");
                    }
                    public void Clear() {
                        executor.ExecuteScript("localStorage.clear()");
                    }
                    public void RemoveItem(string key) {
                        executor.ExecuteScript("localStorage.removeItem(...)");
                    }
                }
                """);
            File.WriteAllText(Path.Combine(root, "StorageTests.cs"), """
                using NUnit.Framework;
                [TestFixture] public class StorageTests {
                    [SetUp] public void Prepare() {
                        LocalStorage.Clear();
                    }
                    [Test] public void BeforeAndAfterNavigation() {
                        LocalStorage.SettingPeriod("first", "last");
                        var page = Navigation.OpenCatalogPage();
                        page.Refresh();
                        LocalStorage.RemoveItem("period-begin");
                    }
                    [Test] public void NoNavigation() {
                        LocalStorage.SetItem("other", "value");
                    }
                }
                """);

            var inspector = new PomSourceInspector();
            var report = inspector.Inspect(root, new[] { "ScCombobox", "Loader", "DatePicker" });
            Require(report.SchemaVersion == "migrator-pom-evidence/v1alpha2",
                "new source evidence schema");
            Require(report.InspectedFiles == 5 && report.SourceTreeHash.Length == 64,
                "input count and exact byte identity");

            var combo = report.Helpers.Single(h => h.SourceType == "ScCombobox"
                && h.Method == "InputAndSelect");
            Require(combo.OrderedCalls.Count >= 5, "all meaningful combo-box actions retained");
            Require(combo.OrderedCalls.Select(c => c.Order).SequenceEqual(
                Enumerable.Range(1, combo.OrderedCalls.Count)), "lexical call order stable");
            Require(combo.OrderedCalls.Any(s => s.SourceText.Contains("ClearAndInputText")),
                "input action must remain in source flow");
            Require(combo.OrderedCalls.Any(s => s.SourceText.Contains(".Single(")
                && s.CallChain.Contains("Click")),
                "menu selection and its final click must remain in the same chain");
            Require(combo.MethodSha256.Length == 64, "method-level fingerprint missing");
            Require(combo.ReviewFlags.Contains("syntax_only_not_behavioral_proof"),
                "must never imply a proven mapping");

            var loader = report.Helpers.Single(h => h.SourceType == "Loader"
                && h.Method == "ValidateLoading");
            Require(loader.ControlFlow.Any(x => x.Kind == "while")
                && loader.ControlFlow.Any(x => x.Kind == "try")
                && loader.ControlFlow.Any(x => x.Kind == "catch"),
                "retry/exception control flow was lost");
            Require(loader.ReviewFlags.Contains("loop_or_retry")
                && loader.ReviewFlags.Contains("exception_or_retry_path"),
                "loader must be flagged as complex rather than one wait");

            var picker = report.Helpers.Single(h => h.SourceType == "DatePicker"
                && h.Method == "SetBeginDate");
            Require(picker.OrderedCalls.Any(x => x.Method == "Blur"),
                "private helper dependency not retained");
            Require(report.Helpers.Any(h => h.SourceType == "DatePicker"
                && h.Method == "Blur" && h.IsReachablePrivateHelper
                && h.OrderedCalls.Any(c => c.Method == "ExecuteJs")),
                "reachable private helper should expose its script action");

            var storage = report.Helpers.Single(h => h.SourceType == "LocalStorage"
                && h.Method == "SettingPeriod");
            Require(storage.LiteralStorageKeys.Contains("period-begin")
                && storage.LiteralStorageKeys.Contains("period-end"),
                "source literal storage keys missing");
            Require(storage.OrderedCalls.Count(c => c.Method == "SetItem") == 2,
                "multiple writes must not collapse to one operation");

            var flows = report.StorageAndNavigationFlows;
            var setup = flows.Single(f => f.Method == "Prepare");
            Require(setup.IsSetup
                && setup.StorageOperations.Single().RelativeToNavigation == "setup_before_test",
                "fixture setup storage must retain its lifecycle classification");
            var test = flows.Single(f => f.Method == "BeforeAndAfterNavigation");
            Require(test.HasNavigationCall && test.StorageOperations.Count == 2,
                "in-method storage/navigation ordering missing");
            Require(test.StorageOperations[0].RelativeToNavigation == "before_first_navigation"
                && test.StorageOperations[1].RelativeToNavigation == "after_first_navigation",
                "storage ordering relative to navigation incorrect");
            Require(test.RuntimeOrderQualification.Contains("not verified"),
                "lexical source analysis must not promise browser origin");
            Require(flows.Single(f => f.Method == "NoNavigation").StorageOperations
                .Single().RelativeToNavigation == "no_navigation_in_method",
                "missing-navigation case cannot infer an origin");

            File.AppendAllText(Path.Combine(root, "DatePicker.cs"), "// modified");
            var changed = inspector.Inspect(root, new[] { "DatePicker" });
            Require(changed.SourceTreeHash != report.SourceTreeHash,
                "source modifications must invalidate method evidence");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("POM method lineage smoke: " + message);
    }
}
