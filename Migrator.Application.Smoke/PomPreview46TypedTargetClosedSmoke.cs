using Migrator.Application;

/// <summary>Source-only: real Playwright typed TargetClosed, exact owned resources.</summary>
internal static class PomPreview46TypedTargetClosedSmoke
{
    internal const string OriginalTearDown = """
        [TearDown] public async Task StopEach() {
            try { await Page.CloseAsync(); }
            finally { await Context.CloseAsync(); }
        }
        """;
    internal const string TypedTearDown = """
        [TearDown] public async Task StopEach() {
            try { await Page.CloseAsync(); }
            catch (Microsoft.Playwright.TargetClosedException) {
                try { System.Console.Error.WriteLine(
                    "MIGRATOR_CLEANUP_PAGE_CLOSE_FAILED"); } catch {}
            }
            finally {
                try { await Context.CloseAsync(); }
                catch (Microsoft.Playwright.TargetClosedException) {
                    try { System.Console.Error.WriteLine(
                        "MIGRATOR_CLEANUP_CONTEXT_CLOSE_FAILED"); } catch {}
                }
            }
        }
        """;
    internal static string CanonicalFixture()
    {
        var prev = PomPreview44SafeDiagnosticsSmoke.CanonicalFixture();
        var old = OriginalTearDown.Trim();
        var replacement = TypedTearDown.Trim();
        if (prev.Split(old, StringSplitOptions.None).Length != 2)
            throw new InvalidOperationException("PREVIEW46_NUNIT_SOURCE_TEARDOWN_AMBIGUOUS");
        return prev.Replace(old,replacement,StringComparison.Ordinal);
    }

    public static void Run()
    {
        var root=Path.Combine(Path.GetTempPath(),
            "fictional-preview46-targetclosed-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var canonical=CanonicalFixture();
            void Write(string fixture)
            {
                File.WriteAllText(Path.Combine(root,"Page.cs"),
                    PomPreview42FixtureLifecycleSmoke.Page);
                File.WriteAllText(Path.Combine(root,"TestBase.cs"),
                    PomPreview42FixtureLifecycleSmoke.Base);
                File.WriteAllText(Path.Combine(root,"NUnitFixture.cs"),fixture);
            }
            var engine=new MigrationEngine();
            string Hash()=>new PomSourceInspector().Inspect(root).SourceTreeHash;
            PomExternalExceptionContractPlan Plan(string method,string hash,
                string page="Page",string context="Context",string browser="Browser",
                string pw="PW") => engine.PlanPomTypedTargetClosedTearDown(
                    root,"Fictional.P42.SourcePage",method,hash,
                    page,context,browser,pw);
            static void Require(bool valid,string description)
            {
                if(!valid)throw new InvalidOperationException(
                    "PREVIEW46_TYPED_TARGET_CLOSED_AST: "+description);
            }
            Write(canonical);
            var frozen=Hash();
            foreach(var method in new[]{"Missing","Timeout"})
            {
                var p=Plan(method,frozen);
                var f=p.FixtureLifecycle;
                Require(p.Status=="requires_review"&&
                    p.SchemaVersion=="migrator-pom-typed-target-closed-teardown/v1"
                    &&p.SourceTreeHash==frozen
                    &&p.Blockers.Count==0&&p.CatchCandidates.Count==1
                    &&p.AsyncMethods.Count==3
                    &&f!=null &&f.TypedTargetClosedTearDownSourceOnly
                    &&f.PageTargetClosedCatchGuarded
                    &&f.ContextTargetClosedCatchGuarded
                    &&f.TestFailurePrecedenceOnlyForTargetClosed
                    &&f.PageTargetClosedCatchSha256.Length==64
                    &&f.ContextTargetClosedCatchSha256.Length==64
                    &&f.PageTargetClosedCatchLine>0
                    &&f.ContextTargetClosedCatchLine>f.PageTargetClosedCatchLine
                    &&f.SourceSinkNotRebound
                    &&f.PrimaryExceptionRethrown
                    &&f.PerTestCleanupInFinally
                    &&f.PageAndContextConstructorInjected
                    &&!f.RealCleanupFailureCertified
                    &&!p.CanAutoApply && !p.RuntimeVerified && p.RequiresReview
                    &&p.Execution=="read_only_not_run",
                    "Typed TargetClosed source did not prove exact ownership "+
                        System.Text.Json.JsonSerializer.Serialize(p));
                Require(engine.PlanPomCleanupFaultWitness(root,
                    "Fictional.P42.SourcePage",method,frozen,
                    "Page","Context","Browser","PW").Status=="blocked",
                    "Historical preview.45 TearDown safety gate changed");
            }
            void Block(string label,string source)
            {
                Require(source!=canonical,"Negative fixture did not change source "+label);
                Write(source);
                var plan=Plan("Missing",Hash());
                Require(plan.Status=="blocked"
                    &&plan.CatchCandidates.Count==0
                    &&plan.AsyncMethods.Count==0
                    &&plan.FixtureLifecycle==null
                    &&plan.Blockers.Count>0 &&!plan.CanAutoApply,
                    "Unsupported "+label+" emitted partial contract "+
                        System.Text.Json.JsonSerializer.Serialize(plan));
            }
            void Changed(string label,string old,string modified)
            {
                Require(canonical.Contains(old,StringComparison.Ordinal),
                    "Negative fixture marker absent "+label);
                Block(label,canonical.Replace(old,modified,StringComparison.Ordinal));
            }
            Changed("broad exception type",
                "catch (Microsoft.Playwright.TargetClosedException)",
                "catch (System.Exception)");
            Changed("other Playwright exception type",
                "catch (Microsoft.Playwright.TargetClosedException)",
                "catch (Microsoft.Playwright.PlaywrightException)");
            Changed("filtered TargetClosed",
                "catch (Microsoft.Playwright.TargetClosedException)",
                "catch (Microsoft.Playwright.TargetClosedException e) when (e.Message.Length>0)");
            Changed("caught exception object",
                "catch (Microsoft.Playwright.TargetClosedException)",
                "catch (Microsoft.Playwright.TargetClosedException e)");
            Changed("wrong cleanup receiver",
                "await Context.CloseAsync();",
                "await Page.CloseAsync();");
            Changed("no context finally",
                "finally {",
                "catch {");
            Changed("wrong Page fixed code",
                "MIGRATOR_CLEANUP_PAGE_CLOSE_FAILED",
                "MIGRATOR_CLEANUP_CONTEXT_CLOSE_FAILED");
            Changed("dynamic diagnostic string",
                "System.Console.Error.WriteLine(\n                    \"MIGRATOR_CLEANUP_CONTEXT_CLOSE_FAILED\")",
                "System.Console.Error.WriteLine(System.Guid.NewGuid().ToString())");
            Changed("diagnostic sink exception escapes",
                "catch {}",
                "catch { throw; }");
            Changed("untrusted source side effect",
                "try { await Context.CloseAsync(); }",
                "try { await Context.CloseAsync(); Page = null!; }");
            Changed("different context injection",
                "page = new SourcePage(driver,Page,Context);",
                "page = new SourcePage(driver,Page,null!);");
            const string fieldAnchor="private IPage Page = null!;";
            Changed("Console.Error replaced in fixture",
                fieldAnchor,fieldAnchor+
                "\n [Test] public void RebindSink() { System.Console.SetError(System.Console.Out); }");
            Write(canonical);
            Require(Hash()==frozen,"Read-only analysis modified customer source");
            File.AppendAllText(Path.Combine(root,"NUnitFixture.cs"),"\n// stale");
            try
            {
                Plan("Missing",frozen);
                throw new InvalidOperationException("PREVIEW46_STALE_SHA_ACCEPTED");
            }
            catch(InvalidOperationException e) when(e.Message.StartsWith(
                "STALE_POM_EXCEPTION_CONTRACT",StringComparison.Ordinal)){}
        }
        finally {Directory.Delete(root,true);}
    }
}
