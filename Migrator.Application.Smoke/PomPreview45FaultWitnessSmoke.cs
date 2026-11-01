using Migrator.Application;

/// <summary>
/// Only captured-source attestation. The deliberately injected failing
/// TextWriter and real Playwright errors are tested separately in Chromium CI.
/// </summary>
internal static class PomPreview45FaultWitnessSmoke
{
    public static void Run()
    {
        var root=Path.Combine(Path.GetTempPath(),
            "fictional-p45-witness-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture=PomPreview44SafeDiagnosticsSmoke.CanonicalFixture();
            void Write(string source)
            {
                File.WriteAllText(Path.Combine(root,"Page.cs"),
                    PomPreview42FixtureLifecycleSmoke.Page);
                File.WriteAllText(Path.Combine(root,"TestBase.cs"),
                    PomPreview42FixtureLifecycleSmoke.Base);
                File.WriteAllText(Path.Combine(root,"NUnitFixture.cs"),source);
            }
            var engine=new MigrationEngine();
            string Hash()=>new PomSourceInspector().Inspect(root).SourceTreeHash;
            PomExternalExceptionContractPlan Plan(string method,string sha,
                string page="Page",string context="Context",
                string browser="Browser",string playwright="PW") =>
                engine.PlanPomCleanupFaultWitness(root,
                    "Fictional.P42.SourcePage",method,sha,
                    page,context,browser,playwright);
            static void Check(bool ok,string why)
            {
                if(!ok)throw new InvalidOperationException(
                    "POM_PREVIEW45_FAULT_WITNESS_AST: "+why);
            }
            Write(fixture);
            var sha=Hash();
            foreach(var method in new[]{"Missing","Timeout"})
            {
                var p=Plan(method,sha);
                var proof=p.FixtureLifecycle;
                Check(p.Status=="requires_review"
                    && p.SchemaVersion=="migrator-pom-external-cleanup-fault-witness/v1"
                    && p.SourceTreeHash==sha && p.Blockers.Count==0
                    && p.CatchCandidates.Count==1 && p.AsyncMethods.Count==3
                    && proof!=null && proof.FaultWitnessSourceOnly
                    && proof.SourceSinkNotRebound
                    && proof.SafeSecondaryDiagnosticCodes
                    && proof.DiagnosticSinkFailurePreservesPrimary
                    && proof.PrimaryExceptionRethrown
                    && proof.OneTimeSetUpFailureCleanup
                    && proof.SetUpFailureCleanup
                    && !proof.RealCleanupFailureCertified
                    && !proof.OsProcessAncestryCertified
                    && proof.DiagnosticSurface=="fixed_code_stderr_only"
                    && proof.FixtureSourceSha256.Length==64
                    && !p.RuntimeVerified && !p.CanAutoApply && p.RequiresReview,
                    "Incorrect source-only proof or unsupported runtime claim "+
                        System.Text.Json.JsonSerializer.Serialize(p));
                Check(engine.PlanPomSafeCleanupDiagnosticsContract(root,
                    "Fictional.P42.SourcePage",method,sha,
                    "Page","Context","Browser","PW").Status=="requires_review",
                    "preview.44 historical safe diagnostic contract changed");
            }

            void Block(string changed,string label)
            {
                Write(changed);
                var p=Plan("Missing",Hash());
                Check(p.Status=="blocked" && p.FixtureLifecycle==null
                    && p.AsyncMethods.Count==0 && p.CatchCandidates.Count==0
                    && p.Blockers.Count>0 && !p.CanAutoApply,
                    "Unproven "+label+" emitted partial candidate "+ 
                    System.Text.Json.JsonSerializer.Serialize(p));
            }
            const string classAnchor="private IPage Page = null!;";
            Check(fixture.Contains(classAnchor,StringComparison.Ordinal),
                "Captured NUnit field marker absent");
            Block(fixture.Replace(classAnchor,classAnchor+
                "\n            [Test] public void ChangeSink() {"+
                " System.Console.SetError(System.Console.Out); }"),"SetError");
            Block(fixture.Replace(classAnchor,classAnchor+
                "\n            [Test] public void HiddenSinkRebind() {"+
                " System.Console.SetError(new System.IO.StringWriter()); }"),
                "additional Console.SetError");
            Block(fixture.Replace("MIGRATOR_CLEANUP_PAGE_CLOSE_FAILED",
                "UNTRUSTED_DYNAMIC_SOURCE_TEXT",StringComparison.Ordinal),
                "non-allowlisted literal");
            Block(fixture.Replace("catch {} }","catch { throw; } }",
                StringComparison.Ordinal),"diagnostic sink failure escapes");
            Block(fixture.Replace("if (Context != null) await Context.CloseAsync();",
                "if (Context != null) await Page.CloseAsync();",
                StringComparison.Ordinal),"wrong cleanup receiver");
            Write(fixture);
            Check(Hash()==sha,"Planner mutated original source bytes");
            File.AppendAllText(Path.Combine(root,"NUnitFixture.cs"),"\n// stale");
            try
            {
                Plan("Missing",sha);
                throw new InvalidOperationException("STALE_SOURCE_WAS_ACCEPTED");
            }
            catch(InvalidOperationException ex) when (
                ex.Message.StartsWith("STALE_POM_EXCEPTION_CONTRACT",
                    StringComparison.Ordinal)) {}
        }
        finally { Directory.Delete(root,recursive:true); }
    }
}
