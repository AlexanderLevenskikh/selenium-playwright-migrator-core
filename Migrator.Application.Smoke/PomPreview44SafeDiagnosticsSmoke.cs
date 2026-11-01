using Migrator.Application;

internal static class PomPreview44SafeDiagnosticsSmoke
{
    private static readonly (string Code, string Operation)[] Codes =
    {
        ("MIGRATOR_CLEANUP_BROWSER_CLOSE_FAILED","Browser.CloseAsync"),
        ("MIGRATOR_CLEANUP_PLAYWRIGHT_DISPOSE_FAILED","PW.Dispose"),
        ("MIGRATOR_CLEANUP_PAGE_CLOSE_FAILED","Page.CloseAsync"),
        ("MIGRATOR_CLEANUP_CONTEXT_CLOSE_FAILED","Context.CloseAsync")
    };

    public static string CanonicalFixture()
    {
        // Reuse the exact public preview.43 source tree, not a fabricated
        // alternate setup or an adapted target compiled without evidence.
        var fixture = PomPreview43FailureAtomicSmoke.CanonicalFixture();
        var cursor = 0;
        foreach (var (code, _) in Codes)
        {
            var index = fixture.IndexOf("catch {}",cursor,StringComparison.Ordinal);
            if (index < 0)
                throw new InvalidOperationException("PREVIEW44_MISSING_SOURCE_CATCH");
            const string oldCatch = "catch {}";
            var added = "catch { try { System.Console.Error.WriteLine(\"" + code
                + "\"); } catch {} }";
            fixture = fixture[..index] + added + fixture[(index+oldCatch.Length)..];
            cursor = index + added.Length;
        }
        if (fixture.IndexOf("catch {}",cursor,StringComparison.Ordinal)>=0)
            throw new InvalidOperationException("PREVIEW44_UNVERIFIED_EXTRA_CATCH");
        return fixture;
    }

    public static void Run()
    {
        var root=Path.Combine(Path.GetTempPath(),"fictional-p44-diagnostic-"+
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            void Write(string fixture)
            {
                File.WriteAllText(Path.Combine(root,"Page.cs"),
                    PomPreview42FixtureLifecycleSmoke.Page);
                File.WriteAllText(Path.Combine(root,"TestBase.cs"),
                    PomPreview42FixtureLifecycleSmoke.Base);
                File.WriteAllText(Path.Combine(root,"NUnitFixture.cs"),fixture);
            }
            var source=CanonicalFixture();
            var engine=new MigrationEngine();
            string Hash()=>new PomSourceInspector().Inspect(root).SourceTreeHash;
            PomExternalExceptionContractPlan Plan(string method,string digest,
                string page="Page",string context="Context",
                string browser="Browser",string playwright="PW") =>
                engine.PlanPomSafeCleanupDiagnosticsContract(root,
                    "Fictional.P42.SourcePage",method,digest,
                    page,context,browser,playwright);
            static void Check(bool ok,string reason)
            {
                if(!ok)throw new InvalidOperationException(
                    "POM_PREVIEW44_SAFE_DIAGNOSTICS_AST: "+reason);
            }
            Write(source);
            var sourceSha=Hash();
            foreach(var method in new[]{"Missing","Timeout"})
            {
                var plan=Plan(method,sourceSha);
                var proof=plan.FixtureLifecycle;
                Check(plan.Status=="requires_review"
                    && plan.SchemaVersion=="migrator-pom-external-safe-diagnostics-count/v1"
                    && plan.SourceTreeHash==sourceSha
                    && plan.CatchCandidates.Count==1 && plan.AsyncMethods.Count==3
                    && proof!=null
                    && proof.SafeSecondaryDiagnosticCodes
                    && proof.DiagnosticSinkFailurePreservesPrimary
                    && proof.PrimaryExceptionRethrown
                    && proof.CleanupFailuresDoNotReplacePrimary
                    && proof.OneTimeSetUpFailureCleanup && proof.SetUpFailureCleanup
                    && proof.DiagnosticSurface=="fixed_code_stderr_only"
                    && proof.OneTimeSetUpSha256.Length==64
                    && proof.SetUpSha256.Length==64
                    && !plan.CanAutoApply && !plan.RuntimeVerified
                    && plan.RequiresReview && plan.Execution=="read_only_not_run",
                    "Source-safe diagnostic contract rejected "+method+" "+
                    System.Text.Json.JsonSerializer.Serialize(plan));
                Check(engine.PlanPomExternalFailureAtomicCountContract(root,
                    "Fictional.P42.SourcePage",method,sourceSha,
                    "Page","Context","Browser","PW").Status=="blocked",
                    "Earlier preview.43 admitted an unrecognized diagnostic side effect");
                Check(engine.PlanPomExternalAsyncLifecycleCountContract(root,
                    "Fictional.P42.SourcePage",method,sourceSha,
                    "Page","Context","Browser","PW").Status=="blocked",
                    "Earlier preview.42 changed its lifecycle contract");
            }
            void Block(string label,string modified)
            {
                Write(modified);
                var plan=Plan("Missing",Hash());
                Check(plan.Status=="blocked" && plan.Blockers.Count>0
                    && plan.FixtureLifecycle==null
                    && plan.AsyncMethods.Count==0
                    && plan.CatchCandidates.Count==0
                    && !plan.CanAutoApply,
                    "Unknown "+label+" emitted partial target evidence: "+
                    System.Text.Json.JsonSerializer.Serialize(plan));
            }
            void Change(string label,string from,string to)
            {
                Check(source.Contains(from,StringComparison.Ordinal),
                    "Invalid negative test marker "+label);
                Block(label,source.Replace(from,to,StringComparison.Ordinal));
            }
            Change("untrusted diagnostic content",
                "MIGRATOR_CLEANUP_PAGE_CLOSE_FAILED","PRIVATE_SELECTOR_CODE");
            Change("original exception text logged",
                "System.Console.Error.WriteLine(\"MIGRATOR_CLEANUP_PAGE_CLOSE_FAILED\")",
                "System.Console.Error.WriteLine(ex.Message)");
            Change("dynamic diagnostic text",
                "System.Console.Error.WriteLine(\"MIGRATOR_CLEANUP_CONTEXT_CLOSE_FAILED\")",
                "System.Console.Error.WriteLine(System.Guid.NewGuid().ToString())");
            Change("logging sink not independently guarded",
                "catch { try { System.Console.Error.WriteLine(\"MIGRATOR_CLEANUP_CONTEXT_CLOSE_FAILED\"); } catch {} }",
                "catch { System.Console.Error.WriteLine(\"MIGRATOR_CLEANUP_CONTEXT_CLOSE_FAILED\"); }");
            Change("diagnostic failure can replace primary",
                "catch { try { System.Console.Error.WriteLine(\"MIGRATOR_CLEANUP_PAGE_CLOSE_FAILED\"); } catch {} }",
                "catch { try { System.Console.Error.WriteLine(\"MIGRATOR_CLEANUP_PAGE_CLOSE_FAILED\"); } catch { throw; } }");
            Change("missing page cleanup",
                "if (Page != null) await Page.CloseAsync();",
                "if (Page != null) _ = Page;");
            Change("modified cleanup order",
                "if (Context != null) await Context.CloseAsync();",
                "if (Context != null) await Page.CloseAsync();");
            Change("broad filter",
                "catch { try { System.Console.Error.WriteLine(\"MIGRATOR_CLEANUP_BROWSER_CLOSE_FAILED\"); } catch {} }",
                "catch (System.Exception e) when(e.Message.Length>0) { try { System.Console.Error.WriteLine(\"MIGRATOR_CLEANUP_BROWSER_CLOSE_FAILED\"); } catch {} }");
            Change("bare rethrow lost","throw;","throw new System.Exception();");
            Change("constructor side effect",
                "page = new SourcePage(driver,Page,Context);",
                "page = new SourcePage(driver,Page,Context); Page = null!;");
            Block("guessed receiver",source.Replace(
                "Page = await Context.NewPageAsync();","Page = null!;",
                StringComparison.Ordinal));
            Write(source);
            Check(Hash()==sourceSha,"Source mutated by read-only planning");
            File.AppendAllText(Path.Combine(root,"NUnitFixture.cs"),"\n// stale");
            try
            {
                Plan("Missing",sourceSha);
                throw new InvalidOperationException("STALE_SOURCE_ACCEPTED");
            }
            catch(InvalidOperationException ex) when(
                ex.Message.StartsWith("STALE_POM_EXCEPTION_CONTRACT",
                    StringComparison.Ordinal)) {}
        }
        finally{Directory.Delete(root,true);}
    }
}
