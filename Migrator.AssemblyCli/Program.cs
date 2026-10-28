using System.Text.Json;
using Migrator.Application;

if (args.Length < 2 || args[0] is not ("plan" or "generate" or "verify"))
{
    Console.Error.WriteLine("""
        Usage:
          dotnet run --project Migrator.AssemblyCli -- plan <source-root> [--config <adapter.json>]
          dotnet run --project Migrator.AssemblyCli -- generate <source-root> <new-target-root> [--config <adapter.json>] [--build] [--test]
          dotnet run --project Migrator.AssemblyCli -- verify <source-root> <existing-target-root> [--config <adapter.json>] [--build] [--test]

        Sources and targets must be separate trees. 'generate' creates a new
        output directory only; it never overwrites existing target files.
        '--test' implies build and runs NUnit tests only with explicit consent.
        'plan' only reads source files and returns SHA-bound metadata.
        """);
    return 2;
}

var command = args[0];
var source = args[1];
string? target = null;
string? config = null;
bool runBuild = false;
bool runNUnit = false;
var i = 2;
if (command != "plan")
{
    if (args.Length <= i || args[i].StartsWith("--", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("TARGET_DIRECTORY_REQUIRED");
        return 2;
    }
    target = args[i++];
}
for (; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--config" when i + 1 < args.Length:
            config = args[++i];
            break;
        case "--build":
            runBuild = true;
            break;
        case "--test":
            runBuild = true;
            runNUnit = true;
            break;
        default:
            Console.Error.WriteLine("UNKNOWN_ARGUMENT: " + args[i]);
            return 2;
    }
}

try
{
    var engine = new MigrationEngine();
    var plan = engine.PlanTargetAssembly(source, config);
    var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    if (command == "plan")
    {
        // Avoid dumping generated source code or private selectors to stdout.
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            plan.SchemaVersion, plan.AnalysisId, plan.CandidateId,
            plan.SourceTreeHash, plan.PreviewTargetTreeHash, plan.TargetTreeHash,
            plan.Status, plan.DeclaredTests, plan.ScopedSetUpFixtures,
            plan.ProjectFile, plan.ManifestFile, plan.Issues, plan.Limitations,
            files = plan.Files.Select(file => new { file.RelativePath, file.Sha256,
                file.SourcePath })
        }, jsonOptions));
        return plan.Status == "ready_for_build" ? 0 : 3;
    }
    var result = command == "generate"
        ? engine.MaterializeTargetAssembly(source, target!,
            plan.AnalysisId, plan.CandidateId, config, runBuild, runNUnit)
        : engine.VerifyTargetAssembly(source, target!,
            plan.AnalysisId, plan.CandidateId, config, runBuild, runNUnit);
    Console.WriteLine(JsonSerializer.Serialize(result, jsonOptions));
    return result.Completeness.Status != "passed"
        || result.Build.Status is "failed" or "blocked"
        || result.NUnitRuntime.Status is "failed" or "blocked" ? 4 : 0;
}
catch (Exception ex) when (ex is InvalidOperationException
    or ArgumentException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
    return 5;
}
