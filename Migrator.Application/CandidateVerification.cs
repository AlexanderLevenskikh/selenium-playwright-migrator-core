using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Migrator.Core;

namespace Migrator.Application;

/// <summary>
/// Snapshot-based verification of exact generated or agent-edited target bytes.
/// Never regenerates or overwrites a candidate during syntax/compile checks.
/// No repository build, arbitrary command, browser execution or behavior claims.
/// </summary>
public sealed partial class MigrationEngine
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const int MaxCandidateFiles = 128;
    private const long MaxCandidateBytes = 16 * 1024 * 1024;
    private const long MaxReferenceBytes = 256 * 1024 * 1024;

    /// <summary>
    /// When targetDirectory is provided, all expected .cs target files must be present
    /// there, with no additional .cs files. The current preview is the required parent.
    /// runCompile is opt-in and requires a non-null list of DLL reference paths (empty
    /// explicitly means .NET framework-only compilation).
    /// </summary>
    public ArtifactVerification VerifyArtifact(
        string sourcePath,
        string expectedAnalysisId,
        string expectedCandidateId,
        string? targetDirectory = null,
        string? configPath = null,
        bool runCompile = false,
        IReadOnlyList<string>? compilationReferencePaths = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedAnalysisId) || string.IsNullOrWhiteSpace(expectedCandidateId))
            throw new ArgumentException("EXPECTED_ANALYSIS_AND_CANDIDATE_REQUIRED: both identities are mandatory.");

        var preview = Preview(sourcePath, configPath, expectedAnalysisId, cancellationToken);
        if (!string.Equals(preview.CandidateId, expectedCandidateId, StringComparison.Ordinal))
            throw new InvalidOperationException("STALE_CANDIDATE: requested parent does not match current source and configuration.");
        if (preview.Files.Count == 0)
            throw new InvalidOperationException("NO_TARGET_FILES: preview produced no candidate files to verify.");
        if (preview.Files.Count > MaxCandidateFiles)
            throw new InvalidOperationException("CANDIDATE_TOO_LARGE: maximum of 128 target files.");

        var snapshot = Analyze(sourcePath, configPath, cancellationToken);
        var targetRoot = targetDirectory == null ? null : Path.GetFullPath(targetDirectory);
        if (targetRoot != null && Directory.Exists(snapshot.SourceScope)
            && (PathInside(snapshot.SourceScope, targetRoot) || PathInside(targetRoot, snapshot.SourceScope)))
            throw new InvalidOperationException("OVERLAPPING_SOURCE_AND_TARGET: target tree must be outside source scope.");

        var files = targetRoot == null
            ? preview.Files.Select(file => new CapturedFile(file.RelativePath, Encoding.UTF8.GetBytes(file.Content))).ToArray()
            : CaptureTargetFiles(targetRoot, preview.Files.Select(file => file.RelativePath).ToArray(), cancellationToken);
        var total = files.Sum(file => (long)file.Bytes.Length);
        if (total > MaxCandidateBytes)
            throw new InvalidOperationException("CANDIDATE_TOO_LARGE: target content exceeds 16 MiB.");

        var hash = ContentTreeHasher.ComputeBytes(files.Select(file => (file.RelativePath, file.Bytes)));
        var generated = hash == preview.TargetTreeHash;
        // A generated candidate must be byte-for-byte equal to the preview, not just the filename set.
        if (targetRoot == null && !generated)
            throw new InvalidOperationException("GENERATED_ARTIFACT_HASH_MISMATCH: preview and captured bytes disagree.");

        var revisionId = generated
            ? preview.CandidateId
            : Digest("candidate-edit/v1\n" + preview.CandidateId + "\n" + hash);
        var revision = new CandidateRevision(
            revisionId, generated ? null : preview.CandidateId,
            generated ? "generated" : "agent-edited",
            preview.AnalysisId, snapshot.SourceTreeHash, snapshot.ConfigurationHash,
            hash, files.Select(file => new CandidateFileEvidence(file.RelativePath,
                Convert.ToHexString(SHA256.HashData(file.Bytes)).ToLowerInvariant(), file.Bytes.Length)).ToArray());

        var parsed = new List<SyntaxTree>(files.Length);
        var syntaxDiagnostics = new List<VerificationDiagnostic>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string text;
            try
            {
                var content = file.Bytes.AsSpan();
                if (content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
                    content = content[3..];
                text = StrictUtf8.GetString(content);
            }
            catch (DecoderFallbackException)
            {
                syntaxDiagnostics.Add(new VerificationDiagnostic("INVALID_UTF8", file.RelativePath, 0, 0,
                    "Target file is not valid UTF-8; its bytes have still been included in candidate identity."));
                continue;
            }

            var tree = CSharpSyntaxTree.ParseText(text, path: file.RelativePath);
            parsed.Add(tree);
            foreach (var error in tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Take(40))
                syntaxDiagnostics.Add(ToDiagnostic(error));
        }
        var syntax = new VerificationStage(syntaxDiagnostics.Count == 0 ? "passed" : "failed",
            "roslyn-syntax", syntaxDiagnostics.Take(80).ToArray());
        var compile = new VerificationStage("not_run", "roslyn-assembly", Array.Empty<VerificationDiagnostic>());
        string? referenceContextHash = null;

        if (runCompile)
        {
            if (syntax.Status != "passed")
                compile = new VerificationStage("blocked", "roslyn-assembly",
                    new[] { new VerificationDiagnostic("SYNTAX_FAILED", "", 0, 0,
                        "Compilation was not attempted because syntax verification failed.") });
            else if (compilationReferencePaths == null)
                compile = new VerificationStage("blocked", "roslyn-assembly",
                    new[] { new VerificationDiagnostic("REFERENCE_CONTEXT_REQUIRED", "", 0, 0,
                        "Supply reference assembly paths; use an empty array to request framework-only compilation.") });
            else
                (compile, referenceContextHash) = CompileCaptured(parsed, compilationReferencePaths, revisionId, cancellationToken);
        }

        // Input and target revisions must not move between capture and reporting evidence.
        cancellationToken.ThrowIfCancellationRequested();
        var current = Analyze(sourcePath, configPath, cancellationToken);
        if (current.AnalysisId != preview.AnalysisId)
            throw new InvalidOperationException("SOURCE_CHANGED_DURING_VERIFICATION: retry from a fresh preview.");
        if (targetRoot != null)
        {
            var after = CaptureTargetFiles(targetRoot, preview.Files.Select(f => f.RelativePath).ToArray(), cancellationToken);
            var afterHash = ContentTreeHasher.ComputeBytes(after.Select(file => (file.RelativePath, file.Bytes)));
            if (!string.Equals(afterHash, hash, StringComparison.Ordinal))
                throw new InvalidOperationException("TARGET_CHANGED_DURING_VERIFICATION: target bytes changed while being verified.");
        }

        return new ArtifactVerification(ContractVersions.Verification, revision, syntax, compile,
            new VerificationStage("not_run", "runtime", Array.Empty<VerificationDiagnostic>()),
            new VerificationStage("not_run", "observables", Array.Empty<VerificationDiagnostic>()),
            referenceContextHash,
            new[]
            {
                "Syntax passed means only that the captured C# files parse, not that migration is complete.",
                "Compile passed means Roslyn emitted an assembly against the supplied references; it is not a dotnet build of the product.",
                "No browser, runtime or behavioral observations are performed by this alpha verifier.",
                "Source snapshot context uses the alpha project's lightweight inventory; full transitive MSBuild inputs are not attested.",
                "Reference context identifies supplied DLL bytes and runtime version, not a hermetic project build."
            });
    }

    private sealed record CapturedFile(string RelativePath, byte[] Bytes);

    private static bool PathInside(string root, string path)
    {
        var compare = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(basePath, candidate, compare)
            || candidate.StartsWith(basePath + Path.DirectorySeparatorChar, compare);
    }

    private static CapturedFile[] CaptureTargetFiles(
        string directory, IReadOnlyList<string> expected, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException("TARGET_ROOT_MISSING: " + directory);
        var found = new List<string>();
        Walk(directory);
        var expectedSorted = expected.OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var foundSorted = found.Select(path => Path.GetRelativePath(directory, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (!expectedSorted.SequenceEqual(foundSorted, StringComparer.Ordinal))
            throw new InvalidOperationException("TARGET_FILE_SET_MISMATCH: target .cs files must exactly match preview paths. "
                + "Expected [" + string.Join(", ", expectedSorted.Take(16)) + "], found ["
                + string.Join(", ", foundSorted.Take(16)) + "].");

        var result = new List<CapturedFile>(found.Count);
        long count = 0;
        foreach (var path in found.OrderBy(p => p, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = File.ReadAllBytes(path);
            count += bytes.Length;
            if (count > MaxCandidateBytes)
                throw new InvalidOperationException("CANDIDATE_TOO_LARGE: target content exceeds 16 MiB.");
            result.Add(new CapturedFile(Path.GetRelativePath(directory, path).Replace('\\', '/'), bytes));
        }
        return result.ToArray();

        void Walk(string dir)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("TARGET_SYMLINK_REJECTED: target directory contains a symlink.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir).OrderBy(p => p, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attrs = File.GetAttributes(entry);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("TARGET_SYMLINK_REJECTED: target tree contains a symlink.");
                if ((attrs & FileAttributes.Directory) != 0)
                    Walk(entry);
                else if (entry.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(entry);
                    if (found.Count > MaxCandidateFiles)
                        throw new InvalidOperationException("CANDIDATE_TOO_LARGE: more than 128 C# files.");
                }
            }
        }
    }

    private static VerificationDiagnostic ToDiagnostic(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.GetLineSpan();
        return new VerificationDiagnostic(diagnostic.Id,
            span.Path ?? "", span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1,
            diagnostic.GetMessage());
    }

    private static (VerificationStage Stage, string? ReferenceContextHash) CompileCaptured(
        IReadOnlyList<SyntaxTree> parsed, IReadOnlyList<string> externalPaths,
        string revisionId, CancellationToken cancellationToken)
    {
        var platform = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (string.IsNullOrWhiteSpace(platform))
            return (new VerificationStage("blocked", "roslyn-assembly",
                new[] { new VerificationDiagnostic("FRAMEWORK_REFERENCES_MISSING", "", 0, 0,
                    "Runtime framework reference assemblies are not available to the in-process compiler.") }), null);
        if (externalPaths.Count > 64)
            throw new InvalidOperationException("TOO_MANY_REFERENCES: limit is 64 additional assemblies.");

        var evidence = new List<(string RelativePath, byte[] Content)>();
        var references = new List<MetadataReference>();
        foreach (var file in platform.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            references.Add(MetadataReference.CreateFromFile(file));
        }
        long total = 0;
        for (var i = 0; i < externalPaths.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(externalPaths[i]);
            if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                throw new FileNotFoundException("REFERENCE_DLL_REQUIRED: compilation references must be existing .dll files.", path);
            var bytes = File.ReadAllBytes(path);
            total += bytes.Length;
            if (total > MaxReferenceBytes)
                throw new InvalidOperationException("REFERENCE_CONTEXT_TOO_LARGE: additional assembly bytes exceed 256 MiB.");
            // Avoid loading referenced user code. Only portable executable metadata is read.
            references.Add(MetadataReference.CreateFromImage(ImmutableArray.Create<byte>(bytes), filePath: path));
            evidence.Add((i.ToString("D3") + "/" + Path.GetFileName(path), bytes));
        }
        evidence.Add(("context/runtime.txt",
            Encoding.UTF8.GetBytes(RuntimeInformation.FrameworkDescription + "\n"
                + RuntimeInformation.ProcessArchitecture + "\n"
                + typeof(CSharpCompilation).Assembly.GetName().Version)));
        var referenceHash = ContentTreeHasher.ComputeBytes(evidence);
        cancellationToken.ThrowIfCancellationRequested();

        var assembly = CSharpCompilation.Create("MigratorCandidate_" + revisionId[..Math.Min(16, revisionId.Length)],
            parsed, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true));
        using var stream = new MemoryStream();
        var emitted = assembly.Emit(stream, cancellationToken: cancellationToken);
        var errors = emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
            .Take(80).Select(ToDiagnostic).ToArray();
        var status = emitted.Success && errors.Length == 0 ? "passed" : "failed";
        return (new VerificationStage(status, "roslyn-assembly", errors), referenceHash);
    }
}
