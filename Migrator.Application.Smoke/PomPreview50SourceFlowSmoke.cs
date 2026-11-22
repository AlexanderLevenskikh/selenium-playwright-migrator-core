using Migrator.Application;

internal static class PomPreview50SourceFlowSmoke
{
    // Public fictional source. These are syntax-only declarations, not a
    // substitute for a private business app or a verified target project.
    private const string Source = """
        using System.Linq;
        using NUnit.Framework;

        public class FiltersFixture : FixtureBase
        {
            private FiltersPage page;
            [SetUp]
            public void SetUp()
            {
                var selected = Navigation.OpenFiltersPage();
                selected.Loader.ValidateLoading();
                page = selected;
            }
            [Test]
            public void CheckListFilter()
            {
                page.Filter.InputAndSelect("sample");
                page.Loader.ValidateLoading();
                page.Table.Items.ElementAt(0).Text.Get().Should().Be("sample");
                page.Table.Items.ElementAt(1).Text.Get().Should().Contain("group");
            }
        }
        public class FixtureBase
        {
            protected Navigation Navigation =>
                DiContainer.GetScopedRequiredService<Navigation>();
        }
        public class Navigation
        {
            public FiltersPage OpenFiltersPage()
            {
                return OpenPage<FiltersPage>(Routes.FilterPage);
            }
            public TPage OpenPage<TPage>(string url)
            {
                driver.Navigate().GoToUrl(url);
                Thread.Sleep(400);
                return factory.CreatePage<TPage>(driver);
            }
        }
        public class FiltersPage
        {
            public ComboInput Filter { get; }
            public LoadingGuard Loader { get; }
            public RowsTable Table { get; }
            public FiltersPage()
            {
                Filter = factory.CreateControl<ComboInput>(selector);
                Loader = factory.CreateControl<LoadingGuard>(selector);
                Table = factory.CreateControl<RowsTable>(selector);
            }
        }
        public class ComboInput
        {
            public void InputAndSelect(string value)
            {
                Open();
                input.Visible.Wait().EqualTo(true);
                input.ClearAndInputText(value);
                MenuItems.Wait().Single(x => x.Text, Contains.Substring(value)).Click();
                acceptButton.Click();
            }
            public void Open()
            {
                dropdown.Click();
            }
        }
        public class LoadingGuard
        {
            public void ValidateLoading()
            {
                var maxAttempts = 2;
                var attempts = 0;
                while (attempts < maxAttempts)
                    try
                    {
                        if (error.Displayed().Get())
                        {
                            error.ExecuteJs("window.location.reload()");
                            loading.Visible.Wait().EqualTo(false);
                        }
                        marker.Visible.Wait().EqualTo(true);
                        break;
                    }
                    catch (WebDriverTimeoutException)
                    {
                        attempts++;
                        if (attempts == maxAttempts)
                            Assert.Fail("synthetic load failure");
                    }
            }
        }
        public class RowsTable
        {
            public ElementsCollection<CellLabel> Items { get; }
        }
        public class CellLabel
        {
            public IProp<string> Text => Container.Text();
        }
        """;

    public static void Run()
    {
        var dir = Path.Combine(Path.GetTempPath(),
            "p50-public-flow-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // The public fixture is intentionally byte-identical across
            // checkout core.autocrlf settings on Windows and Linux.
            var canonical=Source.Replace("\r\n","\n",StringComparison.Ordinal)
                .Replace("\r","\n",StringComparison.Ordinal);
            var path = Path.Combine(dir,"PublicFixture.cs");
            void Write(string text) => File.WriteAllText(path, text);
            string Hash()=>new PomSourceInspector().Inspect(dir).SourceTreeHash;
            var inspector = new PomFlowSourceInspector();
            PomFlowSourcePlan Plan(string sha, string method = "CheckListFilter")
                => inspector.Inspect(dir, "FiltersFixture", method, sha);
            static void Must(bool actual, string reason)
            {
                if(!actual)throw new InvalidOperationException("P50_SOURCE_FLOW: "+reason);
            }
            Write(canonical);
            var original=Hash();
            var plan=Plan(original);
            Must(plan.Status=="source_verified_requires_review"
                && plan.TargetStatus=="blocked"
                && plan.SourceTreeHash==original
                && plan.SchemaVersion=="migrator-pom-source-flow/v1"
                && plan.Witnesses.Count==5 && plan.Blockers.Count==0
                && plan.Witnesses.All(x=>x.Sha256.Length==64 && x.SourceLine>0)
                && plan.Witnesses.Select(x=>x.Role).SequenceEqual(new[]{
                    "navigation_di_factory_and_page_alias",
                    "combo_order_filter_confirmation",
                    "combo_open_helper_not_inlined",
                    "loader_bounded_retry_conditional_reload_failure",
                    "table_indexed_immediate_assertions" })
                && !plan.CanAutoApply && !plan.RuntimeVerified
                && plan.Execution=="read_only_not_run",
                "positive source evidence was not accepted or target was auto-approved");
            Must(plan.Witnesses.Count(x=>x.Role.Contains("combo",StringComparison.Ordinal))==2,
                "helper provenance lost");

            void Negative(string label, string old, string changed)
            {
                Must(canonical.Contains(old,StringComparison.Ordinal),
                    "missing fixture control "+label);
                var mutated=canonical.Replace(old,changed,StringComparison.Ordinal);
                Must(mutated!=canonical,"ineffective control "+label);
                Write(mutated);
                var bad=Plan(Hash());
                Must(bad.Status=="blocked" && bad.TargetStatus=="blocked"
                    && bad.Blockers.Count>0 && bad.Witnesses.Count==0
                    && !bad.CanAutoApply && !bad.RuntimeVerified,
                    "blocked mutation produced partial target: "+label);
            }
            Negative("broken page alias","page = selected;","page = unknown;");
            Negative("wrong generic page","OpenPage<FiltersPage>(Routes.FilterPage)",
                "OpenPage<OtherPage>(Routes.FilterPage)");
            Negative("unproven scoped DI","GetScopedRequiredService<Navigation>()",
                "GetRequiredService<Navigation>()");
            Negative("hardcoded input","input.ClearAndInputText(value)",
                "input.ClearAndInputText(\"fixed\")");
            Negative("hardcoded filter","Contains.Substring(value)",
                "Contains.Substring(\"fixed\")");
            Negative("missing confirmation","acceptButton.Click();",
                "acceptButton.Confirm();");
            Negative("nonliteral retry bound","var maxAttempts = 2;",
                "var maxAttempts = ReadEnvironment();");
            Negative("broadened catch","catch (WebDriverTimeoutException)",
                "catch (Exception)");
            Negative("removed conditional reload",
                "error.ExecuteJs(\"window.location.reload()\")",
                "error.OtherCall(\"window.location.reload()\")");
            Negative("wrong table terminal", ".Should().Contain(\"group\")",
                ".Should().Be(\"group\")");
            Negative("dynamic table index", "ElementAt(1)", "ElementAt(index)");
            Negative("extra test effect",
                "page.Filter.InputAndSelect(\"sample\");",
                "page.Filter.InputAndSelect(\"sample\"); UnverifiedEffect();");
            Negative("unbound control factory",
                "Filter = factory.CreateControl<ComboInput>(selector);",
                "Filter = factory.CreateControl<OtherControl>(selector);");
            Negative("duplicate page assignment",
                "page = selected;","page = selected; page = selected;");
            Write(canonical);
            Must(Hash()==original,"source not restored");
            var badUnknown=Plan(original,"MissingTest");
            Must(badUnknown.Status=="blocked" && badUnknown.Witnesses.Count==0,
                "unknown source method accepted");
            File.AppendAllText(path, "\n// changed source");
            try
            {
                Plan(original);
                throw new InvalidOperationException("P50_STALE_HASH_ACCEPTED");
            }
            catch (InvalidOperationException e) when(
                e.Message.StartsWith("P50_STALE_SOURCE_TREE_HASH",StringComparison.Ordinal)){}
            Write(canonical);
            Must(Hash()==original,"source mutated by planner");
            var output=Path.Combine("artifacts","pom-preview50");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output,"receipt.json"),
                System.Text.Json.JsonSerializer.Serialize(new {
                    schema="migrator-preview50-public-source-flow-evidence/v1",
                    sourceTreeHash=original,
                    sourceStatus=plan.Status,
                    targetStatus=plan.TargetStatus,
                    witnessCount=plan.Witnesses.Count,
                    roleHashes=plan.Witnesses.Select(w=>new {
                        w.Role,w.Sha256,w.SourceLine,w.SourceOperations
                    }).ToArray(),
                    negativeControlsPassed=14,
                    unknownMethodBlocked=true,
                    staleShaRejected=true,
                    sourceBytesUnchanged=true,
                    canAutoApply=false,
                    runtime="not_run",
                    privateUserSource="not_used"
                },new System.Text.Json.JsonSerializerOptions { WriteIndented=true })+"\n");
        }
        finally { Directory.Delete(dir,true); }
    }
}
