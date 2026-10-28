using Migrator.Application;

internal static class PomSourceEvidenceSmoke
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "pom-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "SelectorExtensions.cs"), """
                public static class SelectorExtensions {
                    public static XPathBy WithTid(this ByDummy source, string tid) {
                        return source.WithAttribute("data-tid", tid);
                    }
                    public static XPathBy WithDataTestId(this ByDummy source, string tid) {
                        return source.WithAttribute("data-test-id", tid);
                    }
                    public static XPathBy WithDataTest(this ByDummy source, string tid) {
                        return source.WithAttribute("data-test", tid);
                    }
                    public static XPathBy WithId(this ByDummy source, string id) {
                        return source.WithId(id);
                    }
                }
                """);
            File.WriteAllText(Path.Combine(root, "TableItem.cs"), """
                public class TableItem {
                    public ElementsCollection<Label> Items { get; }
                    public TableItem(IContextBy container, ControlFactory factory) {
                        Items = factory.CreateElementsCollection<Label>(container,
                            x => x.WithDataTest("row").FixedByIndex());
                    }
                }
                """);
            File.WriteAllText(Path.Combine(root, "CatalogProjectsPage.cs"), """
                public class CatalogProjectsPage {
                    public TableItem Table { get; }
                    public ScCombobox Code { get; }
                    public Button Unknown { get; }
                    public CatalogProjectsPage(IWebDriver webDriver, ControlFactory factory) {
                        Table = factory.CreateControl<TableItem>(webDriver.Search(x => x.WithDataTest("table")));
                        Code = factory.CreateControl<ScCombobox>(webDriver.Search(x =>
                            x.WithDataTestId("project-code").WithTid("caption")));
                        Unknown = factory.CreateControl<Button>(webDriver.Search(x => x.WithMystery("unknown")));
                    }
                }
                """);
            File.WriteAllText(Path.Combine(root, "LocalStorage.cs"), """
                public class LocalStorage {
                    public void Clear() { ExecuteScript("localStorage.clear();"); }
                    public void SettingPeriod(string month) { SetItem("period-month", month); }
                }
                """);
            var inspector = new PomSourceInspector();
            var report = inspector.Inspect(root, new[] { "CatalogProjectsPage" });
            Check(report.InspectedFiles == 4 && report.SourceTreeHash.Length == 64, "source identity");
            var byPath = report.Locators.ToDictionary(x => x.SourceMemberPath);
            Check(byPath["page.Code"].Predicates.Count == 2, "compound selector evidence");
            Check(byPath["page.Code"].Predicates.Any(x => x.HtmlAttribute == "data-test-id"), "source helper attribute");
            Check(byPath["page.Table.Items"].Predicates.Count == 2, "nested table selectors");
            Check(byPath["page.Unknown"].ProposedPlaywrightExpression == null, "unknown helper must not be mapped");
            Check(report.Locators.Where(x => x.ProposedPlaywrightExpression != null)
                .All(x => x.ProposalStatus == "requires_review"), "all proposals need review");
            Check(report.Helpers.Any(x => x.SourceType == "LocalStorage" && x.Method == "SettingPeriod"
                && x.LiteralStorageKeys.Contains("period-month")), "storage keys preserved");
            var shifted = inspector.Inspect(root, new[] { "CatalogProjectsPage" }, "view");
            Check(shifted.Locators.Any(x => x.SourceMemberPath == "view.Table.Items"), "caller alias");
            File.AppendAllText(Path.Combine(root, "SelectorExtensions.cs"), "// changed");
            Check(inspector.Inspect(root).SourceTreeHash != report.SourceTreeHash, "no stale evidence");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException("POM source evidence smoke: " + message);
    }
}
