using Migrator.Application;

internal static class PomPreview43FailureAtomicSmoke
{
    private const string AtomicHooks = """
            [OneTimeSetUp] public async Task StartAll() {
                try {
                    PW = await Microsoft.Playwright.Playwright.CreateAsync();
                    Browser = await PW.Chromium.LaunchAsync(new BrowserTypeLaunchOptions {
                        Headless = true
                    });
                }
                catch {
                    try { if (Browser != null) await Browser.CloseAsync(); }
                    catch {}
                    try { if (PW != null) PW.Dispose(); }
                    catch {}
                    throw;
                }
            }
            [SetUp] public async Task StartEach() {
                try {
                    Context = await Browser.NewContextAsync();
                    Page = await Context.NewPageAsync();
                    page = new SourcePage(driver,Page,Context);
                }
                catch {
                    try { if (Page != null) await Page.CloseAsync(); }
                    catch {}
                    try { if (Context != null) await Context.CloseAsync(); }
                    catch {}
                    throw;
                }
            }
        """;

    internal static string CanonicalFixture()
    {
        var source = PomPreview42FixtureLifecycleSmoke.Fixture;
        var start = source.IndexOf("[OneTimeSetUp]", StringComparison.Ordinal);
        var end = source.IndexOf("[TearDown]", StringComparison.Ordinal);
        if (start < 0 || end <= start || source.IndexOf("[OneTimeSetUp]",
                start + 1, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("PREVIEW43_FIXTURE_MARKERS_AMBIGUOUS");
        return source[..start] + AtomicHooks + "\n            " + source[end..];
    }

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "p43-atomic-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string type = "Fictional.P42.SourcePage";
            const string pageFile = "Page.cs";
            const string baseFile = "TestBase.cs";
            const string fixtureFile = "NUnitFixture.cs";
            var sourceFixture = CanonicalFixture();
            void Write(string fixture, string sourcePage = PomPreview42FixtureLifecycleSmoke.Page)
            {
                File.WriteAllText(Path.Combine(root,pageFile),sourcePage);
                File.WriteAllText(Path.Combine(root,baseFile),
                    PomPreview42FixtureLifecycleSmoke.Base);
                File.WriteAllText(Path.Combine(root,fixtureFile),fixture);
            }
            var engine = new MigrationEngine();
            string Hash() => new PomSourceInspector().Inspect(root).SourceTreeHash;
            PomExternalExceptionContractPlan Plan(string method,string sha,
                string page="Page", string context="Context", string browser="Browser",
                string pw="PW")
                => engine.PlanPomExternalFailureAtomicCountContract(
                    root,type,method,sha,page,context,browser,pw);
            void Require(bool yes, string why)
            {
                if (!yes) throw new InvalidOperationException("PREVIEW43_ATOMIC_AST: "+why);
            }
            Write(sourceFixture);
            var frozen=Hash();
            foreach(var method in new[] {"Missing","Timeout"})
            {
                var p=Plan(method,frozen);
                var f=p.FixtureLifecycle;
                Require(p.Status=="requires_review"
                    && p.SchemaVersion=="migrator-pom-external-failure-atomic-count/v1"
                    && p.SourceTreeHash==frozen
                    && !p.RuntimeVerified && !p.CanAutoApply && p.RequiresReview
                    && p.Execution=="read_only_not_run"
                    && p.CatchCandidates.Count==1 && p.AsyncMethods.Count==3
                    && f!=null && f.OneTimeSetUpFailureCleanup
                    && f.SetUpFailureCleanup && f.PrimaryExceptionRethrown
                    && f.CleanupFailuresDoNotReplacePrimary
                    && f.ContextFromBrowser && f.PageFromContext
                    && f.PageAndContextConstructorInjected
                    && f.OneTimeSetUpSha256.Length==64
                    && f.SetUpSha256.Length==64 && f.TearDownSha256.Length==64
                    && f.OneTimeTearDownSha256.Length==64,
                    "Exact guarded source cleanup missing for "+method+
                    ": "+System.Text.Json.JsonSerializer.Serialize(p));
                Require(engine.PlanPomExternalAsyncLifecycleCountContract(
                    root,type,method,frozen,"Page","Context","Browser","PW")
                    .Status=="blocked",
                    "preview.42 acceptance wrongly inferred on new atomic setup shape");
            }
            void Block(string fixture,string label, string page="Page",
                string context="Context", string browser="Browser",string pw="PW")
            {
                Write(fixture);
                var p=Plan("Missing",Hash(),page,context,browser,pw);
                Require(p.Status=="blocked" && p.FixtureLifecycle==null
                    && p.CatchCandidates.Count==0 && p.AsyncMethods.Count==0
                    && p.Blockers.Count>0 && !p.CanAutoApply,
                    "Unverified "+label+" emitted a partial plan: "+
                    System.Text.Json.JsonSerializer.Serialize(p));
            }
            void Negative(string label,string from,string to)
            {
                Require(sourceFixture.Contains(from,StringComparison.Ordinal),
                    "Missing negative fixture marker "+label);
                Block(sourceFixture.Replace(from,to,StringComparison.Ordinal),label);
            }
            Negative("missing original rethrow", "                    throw;",
                "                    return;");
            Negative("wrong cleanup order",
                "try { if (Browser != null) await Browser.CloseAsync(); }\n                    catch {}\n                    try { if (PW != null) PW.Dispose(); }",
                "try { if (PW != null) PW.Dispose(); }\n                    catch {}\n                    try { if (Browser != null) await Browser.CloseAsync(); }");
            Negative("missing one-time PW dispose",
                "try { if (PW != null) PW.Dispose(); }",
                "try { if (PW != null) _ = PW; }");
            Negative("missing context cleanup",
                "try { if (Context != null) await Context.CloseAsync(); }",
                "try { if (Context != null) _ = Context; }");
            Negative("cleanup escapes catch",
                "catch {}\n                    try { if (Context != null)",
                "catch { throw; }\n                    try { if (Context != null)");
            Negative("missing null guard",
                "if (Page != null) await Page.CloseAsync();",
                "await Page.CloseAsync();");
            Negative("wrong receiver",
                "if (Context != null) await Context.CloseAsync();",
                "if (Context != null) await Page.CloseAsync();");
            Negative("rethrow new loses original exception",
                "                    throw;",
                "                    throw new InvalidOperationException(\"new failure\");");
            Negative("filter alters exception precedence",
                "                catch {\n                    try { if (Page != null)",
                "                catch (Exception error) when (error.Message != \"\") {\n                    try { if (Page != null)");
            Negative("extra side effects in setup catch",
                "                    throw;\n                }\n            }\n            [TearDown]",
                "                    Page = null!;\n                    throw;\n                }\n            }\n            [TearDown]");
            Negative("missing setup try",
                "                try {\n                    Context = await Browser.NewContextAsync();",
                "                if (true) {\n                    Context = await Browser.NewContextAsync();");
            Negative("unknown generated lifecycle receiver",
                "Context = await Browser.NewContextAsync();",
                "Context = null!;");
            Block(sourceFixture,"guessed Page",page:"Guessed");
            Block(sourceFixture,"guessed Context",context:"Guessed");
            Write(sourceFixture);
            Require(Hash()==frozen,"Source changed during read-only AST planning");
            File.AppendAllText(Path.Combine(root,fixtureFile),"\n// stale");
            try
            {
                Plan("Missing",frozen);
                throw new InvalidOperationException("STALE_SHA_ACCEPTED");
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith(
                "STALE_POM_EXCEPTION_CONTRACT",StringComparison.Ordinal)) {}
        }
        finally {Directory.Delete(root,true);}
    }
}