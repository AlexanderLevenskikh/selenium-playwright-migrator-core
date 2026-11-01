using Migrator.Application;

internal static class PomPreview47OneTimeCleanupSmoke
{
    internal const string OneTimeTearDown = """
        [OneTimeTearDown] public async Task StopAll() {
            try {
                try { await Browser.CloseAsync(); }
                catch (Microsoft.Playwright.PlaywrightException closed)
                    when (closed.GetType().FullName ==
                        "Microsoft.Playwright.TargetClosedException" &&
                        closed.GetType().Assembly ==
                        typeof(Microsoft.Playwright.IPage).Assembly) {
                    try { System.Console.Error.WriteLine(
                        "MIGRATOR_CLEANUP_BROWSER_CLOSE_FAILED"); } catch {}
                }
            }
            finally {
                try { PW.Dispose(); }
                catch (Microsoft.Playwright.PlaywrightException closed)
                    when (closed.GetType().FullName ==
                        "Microsoft.Playwright.TargetClosedException" &&
                        closed.GetType().Assembly ==
                        typeof(Microsoft.Playwright.IPage).Assembly) {
                    try { System.Console.Error.WriteLine(
                        "MIGRATOR_CLEANUP_PLAYWRIGHT_DISPOSE_FAILED"); } catch {}
                }
            }
        }
        """;

    internal static string CanonicalFixture()
    {
        var source = PomPreview46TypedTargetClosedSmoke.CanonicalFixture();
        var start = source.IndexOf("[OneTimeTearDown]", StringComparison.Ordinal);
        var end = source.IndexOf("[Test]", start, StringComparison.Ordinal);
        if (start < 0 || end <= start ||
            source.IndexOf("[OneTimeTearDown]", start + 1,
                StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("P47_UNIQUE_ONE_TIME_TEARDOWN_UNPROVEN");
        if (!source[start..end].Contains("finally { PW.Dispose(); }",
                StringComparison.Ordinal))
            throw new InvalidOperationException("P47_ORIGINAL_SOURCE_NOT_ATTESTED");
        return source[..start] + OneTimeTearDown.Trim()
            + "\n            " + source[end..];
    }

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "fictional-p47-one-time-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var canonical = CanonicalFixture();
            void Write(string source)
            {
                File.WriteAllText(Path.Combine(root,"Page.cs"),
                    PomPreview42FixtureLifecycleSmoke.Page);
                File.WriteAllText(Path.Combine(root,"TestBase.cs"),
                    PomPreview42FixtureLifecycleSmoke.Base);
                File.WriteAllText(Path.Combine(root,"NUnitFixture.cs"),source);
            }
            var engine = new MigrationEngine();
            string Hash()=>new PomSourceInspector().Inspect(root).SourceTreeHash;
            PomExternalExceptionContractPlan Plan(string method,string hash,
                string browser="Browser",string playwright="PW") =>
                engine.PlanPomOneTimeTargetClosedCleanup(root,
                    "Fictional.P42.SourcePage",method,hash,
                    "Page","Context",browser,playwright);
            static void Require(bool good,string reason)
            {
                if(!good)throw new InvalidOperationException("P47_AST: "+reason);
            }
            Write(canonical);
            var original=Hash();
            foreach(var name in new[]{"Missing","Timeout"})
            {
                var result=Plan(name,original);
                var f=result.FixtureLifecycle;
                Require(result.SchemaVersion==
                    "migrator-pom-one-time-targetclosed-cleanup/v1"
                    && result.Status=="requires_review"
                    && result.Blockers.Count==0 && result.CatchCandidates.Count==1
                    && result.AsyncMethods.Count==3 && f!=null
                    && f.OneTimeTargetClosedSourceOnly
                    && f.BrowserCloseTargetClosedGuarded
                    && f.PlaywrightDisposeTargetClosedGuarded
                    && f.BrowserCloseCatchSha256.Length==64
                    && f.PlaywrightDisposeCatchSha256.Length==64
                    && f.BrowserCloseCatchLine>0
                    && f.PlaywrightDisposeCatchLine>f.BrowserCloseCatchLine
                    && f.PageTargetClosedClrFilterGuarded
                    && f.ContextTargetClosedClrFilterGuarded
                    && f.ContextFromBrowser && f.PageFromContext
                    && f.SourceSinkNotRebound
                    && !f.RealCleanupFailureCertified
                    && !result.RuntimeVerified && !result.CanAutoApply,
                    "OneTimeTearDown proof: "+result.Status+" "+
                    string.Join(" / ",result.Blockers.Select(x=>x.Code+": "+x.Explanation)));
                Require(engine.PlanPomTargetClosedClrTearDown(root,
                    "Fictional.P42.SourcePage",name,original,
                    "Page","Context","Browser","PW").Status=="blocked",
                    "Historical P46 source gate widened");
            }
            void Block(string label,string altered)
            {
                Require(altered!=canonical,"Ineffective negative "+label);
                Write(altered);
                var p=Plan("Missing",Hash());
                Require(p.Status=="blocked" && p.Blockers.Count>0
                    && p.CatchCandidates.Count==0
                    && p.AsyncMethods.Count==0 && p.FixtureLifecycle==null,
                    "Unsupported OneTimeTearDown emitted plan "+label);
            }
            void Change(string label,string old,string modified)
            {
                var index=canonical.IndexOf("[OneTimeTearDown]",StringComparison.Ordinal);
                var before=canonical[..index];
                var tail=canonical[index..];
                Require(tail.Contains(old,StringComparison.Ordinal),
                    "Negative fixture marker absent "+label);
                Block(label,before+tail.Replace(old,modified,StringComparison.Ordinal));
            }
            Change("broad catch","catch (Microsoft.Playwright.PlaywrightException closed)",
                "catch (System.Exception closed)");
            Change("wrong CLR type","\"Microsoft.Playwright.TargetClosedException\"",
                "\"Microsoft.Playwright.PlaywrightException\"");
            Change("wrong assembly","typeof(Microsoft.Playwright.IPage).Assembly",
                "typeof(string).Assembly");
            Change("wrong browser receiver","await Browser.CloseAsync();",
                "await Context.CloseAsync();");
            Change("wrong disposal receiver","PW.Dispose();","Browser.Dispose();");
            Change("missing finally","finally {\n                try { PW.Dispose(); }",
                "catch {\n                try { PW.Dispose(); }");
            Change("unsafe logger","MIGRATOR_CLEANUP_PLAYWRIGHT_DISPOSE_FAILED",
                "P47_PRIVATE_EXCEPTION_MESSAGE");
            Change("cleanup failure rethrow","} catch {}","} catch {throw;}");
            Change("extra resource effect","try { PW.Dispose(); }",
                "try { PW.Dispose(); Browser = null!; }");
            Change("unproven filtered type","closed.GetType().FullName ==",
                "closed.Message ==");
            Write(canonical);
            Require(Hash()==original,"Planner mutated source");
            File.AppendAllText(Path.Combine(root,"NUnitFixture.cs"),"\n// stale");
            try
            {
                Plan("Missing",original);
                throw new InvalidOperationException("P47_STALE_SHA_ACCEPTED");
            }
            catch(InvalidOperationException e) when(e.Message.StartsWith(
                "STALE_POM_EXCEPTION_CONTRACT",StringComparison.Ordinal)){}
        }
        finally {Directory.Delete(root,true);}
    }
}
