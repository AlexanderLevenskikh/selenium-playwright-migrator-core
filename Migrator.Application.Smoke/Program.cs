using Migrator.Application;
if (args.Contains("--p2-preview40-only",StringComparer.Ordinal))
{
    PomPreview40ExternalContractSmoke.Run();
    Console.WriteLine("POM_PREVIEW40_EXTERNAL_EXCEPTION_CONTRACT_AST_PASSED");
    return;
}
if (args.Contains("--p2-preview39-only", StringComparer.Ordinal))
{
    PomPreview39ExceptionFlowSmoke.Run();
    Console.WriteLine("POM_PREVIEW39_EXCEPTION_CALLGRAPH_AST_PASSED");
    return;
}
if (args.Contains("--p2-preview38-only", StringComparer.Ordinal))
{
    PomPreview38SourceExceptionSmoke.Run();
    PomPreview39ExceptionFlowSmoke.Run();
    Console.WriteLine("POM_PREVIEW38_SOURCE_EXCEPTION_AST_PASSED");
    return;
}
if (args.Contains("--p2-preview37-only", StringComparer.Ordinal))
{
    PomPreview37UnicodeWhitespaceSmoke.Run();
    PomPreview38SourceExceptionSmoke.Run();
    Console.WriteLine("POM_PREVIEW37_UNICODE_WHITESPACE_AST_PASSED");
    return;
}
if (args.Contains("--p2-preview36-only", StringComparer.Ordinal))
{
    PomPreview36RenderedTextSmoke.Run();
    PomPreview37UnicodeWhitespaceSmoke.Run();
    Console.WriteLine("POM_PREVIEW36_RENDERED_TEXT_AST_PASSED");
    return;
}
if (args.Contains("--p2-preview35-only", StringComparer.Ordinal))
{
    PomPreview35ImmediateAssertionSmoke.Run();
    PomPreview36RenderedTextSmoke.Run();
    Console.WriteLine("POM_PREVIEW35_IMMEDIATE_ASSERTION_AST_PASSED");
    return;
}
if (args.Contains("--p2-preview33-only", StringComparer.Ordinal))
{
    PomPreview33SourceBoundSmoke.Run();
    PomPreview35ImmediateAssertionSmoke.Run();
    Console.WriteLine("POM_PREVIEW33_SOURCE_BOUND_AST_PASSED");
    return;
}
if (args.Contains("--p2-typed-retry-only", StringComparer.Ordinal))
{
    PomTypedHelperRetrySmoke.Run();
    Console.WriteLine("POM_PREVIEW32_TYPED_HELPER_RETRY_AST_PASSED");
    return;
}
if (args.Contains("--p2-structured-only", StringComparer.Ordinal))
{
    PomStructuredActionsSmoke.Run();
    Console.WriteLine("POM_STRUCTURED_PREVIEW31_AST_PASSED");
    return;
}
if (args.Contains("--p2-action-only", StringComparer.Ordinal))
{
    PomActionProjectionSmoke.Run();
    Console.WriteLine("POM_ACTION_PREVIEW30_AST_PASSED");
    return;
}
if (args.Contains("--real-chromium-only", StringComparer.Ordinal))
{
    RealChromiumBridgeSmoke.Run();
    Console.WriteLine("REAL_CHROMIUM_BRIDGE_RUNTIME_PASSED");
    return;
}
if (args.Contains("--multi-project-only", StringComparer.Ordinal))
{
    MultiProjectSolutionSmoke.Run();
    Console.WriteLine("Multi-project target solution build and NUnit TRX passed.");
    return;
}
if (args.Contains("--project-assembly-only", StringComparer.Ordinal))
{
    ProjectTargetAssemblySmoke.Run();
    NestedNUnitCleanupReviewSmoke.Run();
    ReviewedPlaywrightLifetimeSmoke.Run();
    ConcretePlaywrightDiBridgeSmoke.Run();
    Console.WriteLine("Generated NUnit target assembly build and runtime smoke passed.");
    return;
}
var work = Path.Combine(Path.GetTempPath(), "migrator-app-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
try
{
    var multi = Path.Combine(work, "Multiple.cs");
    File.WriteAllText(multi, """
        using NUnit.Framework;
        [TestFixture] public class First {
            [Test] public void A() {}
        }
        [TestFixture] public class Second {
            [Test] public void B() {}
        }
        """);
    var engine = new MigrationEngine();
    var first = engine.Analyze(work);
    Require(first.DiscoveryStatus == "partial", "multi fixture must be partial");
    Require(first.DeclaredTests == 2, "must count both test declarations");
    Require(first.Files.Single().Disposition == "multiple_fixtures", "must reject lossy generation");
    var firstExplanation = engine.Explain(work, first.Findings.Single().Id, expectedAnalysisId: first.AnalysisId);
    Require(firstExplanation.Code == "MULTIPLE_FIXTURES", "finding explanation code");
    Require(firstExplanation.AnalysisId == first.AnalysisId, "explanation must match source snapshot");
    Require(firstExplanation.SuggestedNextAction.Contains("Split fixtures"), "missing next action");
    try
    {
        engine.Explain(work, "unknown-id", expectedAnalysisId: first.AnalysisId);
        throw new Exception("unknown finding was accepted");
    }
    catch (KeyNotFoundException ex) when (ex.Message.StartsWith("UNKNOWN_FINDING", StringComparison.Ordinal)) {}

    Require(engine.Preview(work).Files.Count == 0, "blocked files cannot generate outputs");
    File.Delete(multi);
    var xunit = Path.Combine(work, "Xunit.cs");
    File.WriteAllText(xunit, """
        using Xunit;
        public class X { [Fact] public void FactTest() {} }
        """);
    var second = engine.Analyze(work);
    Require(second.DiscoveryStatus == "partial" && second.DeclaredTests == 1, "xunit counted but blocked");
    Require(second.Findings.Any(f => f.Code == "UNSUPPORTED_SOURCE_FRAMEWORK"), "missing framework finding");
    File.Delete(xunit);
    var nunit = Path.Combine(work, "Nunit.cs");
    File.WriteAllText(nunit, """
        using NUnit.Framework;
        [TestFixture] public class SimpleFixture {
            [Test] public void SimpleTest() {}
        }
        """);
    var third = engine.Analyze(work);
    Require(third.DiscoveryStatus == "complete" && third.ParsedTests == 1, "single NUnit test must parse");
    var preview = engine.Preview(work, expectedAnalysisId: third.AnalysisId);
    Require(preview.Files.Count == 1 && preview.TargetTreeHash.Length == 64, "expected one target");
    Require(!string.IsNullOrWhiteSpace(preview.Files.Single().Content), "rendered output missing");
    ArtifactVerificationSmoke.Run(engine, work, third, preview);
    File.AppendAllText(nunit, "\n// changed");
    try
    {
        engine.Preview(work, expectedAnalysisId: third.AnalysisId);
        throw new Exception("stale analysis was accepted");
    }
    catch (InvalidOperationException ex) when (ex.Message.StartsWith("STALE_ANALYSIS", StringComparison.Ordinal)) {}
    ExistingPlaywrightGuardSmoke.Run();
    RenderedTodoCompletenessSmoke.Run();
    PomNavigationAndStorageSmoke.Run();
    PomSourceEvidenceSmoke.Run();
    TableRowActionSmoke.Run();
    PomMethodLineageSmoke.Run();
    PomMappingReviewerSmoke.Run();
    PomActionProjectionSmoke.Run();
    PomStructuredActionsSmoke.Run();
    PomTypedHelperRetrySmoke.Run();
    PomPreview33SourceBoundSmoke.Run();
    PomLoaderControlFlowSmoke.Run();
    PomSourceBoundRetrySmoke.Run();
    MappingLiteralRoundTripSmoke.Run();
    MappedTableAssertionWithSetupSmoke.Run();
    FixturePageLifecycleSmoke.Run();
    InheritedNUnitLifecycleSmoke.Run();
    ScopedSetUpFixtureSmoke.Run();
    NUnitExecutionGateSmoke.Run();
    NUnitLifecycleSourceSmoke.Run();
    Console.WriteLine("Core alpha smoke passed.");
}
finally { Directory.Delete(work, recursive: true); }
static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("Smoke failed: " + message);
}
