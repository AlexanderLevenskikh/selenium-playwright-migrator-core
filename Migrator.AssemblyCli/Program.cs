using System.Text.Json;
using Migrator.Application;

// P2 read-only POM source plan. This CLI never edits source or target.
// --emit-target is explicit because templates can contain private selectors.
if (args.Length >= 2 && args[0] == "pom-inspect")
{
    var pomInspection = new PomSourceInspector().Inspect(args[1]);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        pomInspection.SchemaVersion, pomInspection.SourceTreeHash,
        pomInspection.InspectedFiles, pomInspection.TotalLocatorMembers,
        runtime = "not_run"
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    return 0;
}
if (args.Length >= 5 && args[0] == "pom-plan")
{
    try
    {
        var emit = args.Skip(5).Contains("--emit-target", StringComparer.Ordinal);
        var plan = new MigrationEngine().PlanPomActions(
            args[1], args[2], args[3], args[4]);
        var result = new
        {
            plan.SchemaVersion, plan.Status, plan.SourceTreeHash,
            plan.SourceType, plan.SourceMethod, plan.SourceMethodSha256,
            plan.SourcePath, plan.SourceLine, plan.SuggestedSourcePattern,
            actions = plan.Actions.Select(action => new
            {
                action.Order, action.SourcePath, action.SourceLine,
                action.SourceSha256, action.SourceSymbol,
                action.ReceiverType, action.SourceReturnType,
                action.Effect, action.TargetSha256, action.RequiresReview,
                action.ControlFlowPath
            }),
            plan.Blockers, plan.RuntimeVerified,
            plan.CanAutoApply,
            adapterCandidate = emit && plan.Status == "requires_review"
                ? plan.AdapterCandidate : null,
            targetStatements = emit && plan.Status == "requires_review"
                ? plan.TargetStatements : Array.Empty<string>()
        };
        Console.WriteLine(JsonSerializer.Serialize(result,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return plan.Status == "requires_review" ? 0 : 3;
    }
    catch (Exception ex) when (ex is ArgumentException
        or InvalidOperationException or IOException)
    {
        Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
        return 5;
    }
}

const string Usage = """
    Usage:
      dotnet run --project Migrator.AssemblyCli -- plan <single-project-root> [--config path]
      dotnet run --project Migrator.AssemblyCli -- generate <single-project-root> <new-target-root> [--config path] [--build] [--test]
      dotnet run --project Migrator.AssemblyCli -- verify <single-project-root> <target-root> [--config path] [--build] [--test]

      dotnet run --project Migrator.AssemblyCli -- solution-plan <multi-project-root> [--config path]
      dotnet run --project Migrator.AssemblyCli -- solution-generate <multi-project-root> <new-target-root> [--config path] [--build] [--test]
      dotnet run --project Migrator.AssemblyCli -- solution-verify <multi-project-root> <target-root> [--config path] [--build] [--test]

    Source and target trees must never overlap. All plans are read-only.
    Generate creates a NEW target directory and never overwrites.
    dotnet build/test are opt-in only. Runtime passes are review-required,
    not proof of Selenium-to-Playwright business equivalence.
    """;
var modes = new[] { "plan", "generate", "verify",
    "solution-plan", "solution-generate", "solution-verify" };
if (args.Length < 2 || !modes.Contains(args[0], StringComparer.Ordinal))
{
    Console.Error.WriteLine(Usage);
    return 2;
}
var operation = args[0];
var multi = operation.StartsWith("solution-", StringComparison.Ordinal);
var planMode = operation is "plan" or "solution-plan";
var source = args[1];
string? destination = null;
string? config = null;
var runBuild = false;
var runTests = false;
var index = 2;
if (!planMode)
{
    if (args.Length <= index || args[index].StartsWith("--", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("TARGET_DIRECTORY_REQUIRED");
        return 2;
    }
    destination = args[index++];
}
for (; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--config" when index + 1 < args.Length:
            config = args[++index];
            break;
        case "--build":
            runBuild = true;
            break;
        case "--test":
            runBuild = true;
            runTests = true;
            break;
        default:
            Console.Error.WriteLine("UNKNOWN_ARGUMENT: " + args[index]);
            return 2;
    }
}
if (planMode && (runBuild || runTests))
{
    Console.Error.WriteLine("PLAN_IS_READ_ONLY: --build/--test require generate/verify");
    return 2;
}
try
{
    var engine = new MigrationEngine();
    var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { WriteIndented = true };
    if (multi)
    {
        var plan = engine.PlanTargetSolution(source, config);
        if (planMode)
        {
            // Private generated C# source is not written to console.
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                plan.SchemaVersion, plan.SourceTreeHash, plan.ConfigurationHash,
                plan.PlanId, plan.TargetTreeHash, plan.Status, plan.SolutionFile,
                plan.ManifestFile, plan.Projects,
                files = plan.Files.Select(f => new
                    { f.RelativePath, f.Sha256, f.SourcePath }),
                plan.Issues, plan.Limitations
            }, jsonOptions));
            return plan.Status == "ready_for_build" ? 0 : 3;
        }
        var result = operation == "solution-generate"
            ? engine.MaterializeTargetSolution(source, destination!,
                plan.SourceTreeHash, plan.PlanId, config, runBuild, runTests)
            : engine.VerifyTargetSolution(source, destination!,
                plan.SourceTreeHash, plan.PlanId, config, runBuild, runTests);
        Console.WriteLine(JsonSerializer.Serialize(result, jsonOptions));
        return result.Completeness.Status == "passed"
            && result.Build.Status is not ("failed" or "blocked")
            && result.NUnitRuntime.Status is not ("failed" or "blocked") ? 0 : 4;
    }
    else
    {
        var plan = engine.PlanTargetAssembly(source, config);
        if (planMode)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                plan.SchemaVersion, plan.AnalysisId, plan.CandidateId,
                plan.SourceTreeHash, plan.PreviewTargetTreeHash, plan.TargetTreeHash,
                plan.Status, plan.DeclaredTests, plan.ScopedSetUpFixtures,
                plan.ProjectFile, plan.ManifestFile, plan.Issues, plan.Limitations,
                files = plan.Files.Select(f => new
                    { f.RelativePath, f.Sha256, f.SourcePath })
            }, jsonOptions));
            return plan.Status == "ready_for_build" ? 0 : 3;
        }
        var result = operation == "generate"
            ? engine.MaterializeTargetAssembly(source, destination!,
                plan.AnalysisId, plan.CandidateId, config, runBuild, runTests)
            : engine.VerifyTargetAssembly(source, destination!,
                plan.AnalysisId, plan.CandidateId, config, runBuild, runTests);
        Console.WriteLine(JsonSerializer.Serialize(result, jsonOptions));
        return result.Completeness.Status == "passed"
            && result.Build.Status is not ("failed" or "blocked")
            && result.NUnitRuntime.Status is not ("failed" or "blocked") ? 0 : 4;
    }
}
catch (Exception ex) when (ex is InvalidOperationException
    or ArgumentException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
    return 5;
}
