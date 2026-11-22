#requires -Version 5.1
<#
P1-O isolated test-owned Chromium 156 builder. OPT-IN only.
Input is a separately provisioned exact tagged Chromium+DEPS checkout,
a pinned depot_tools checkout, Visual Studio 2026 and the SDK. NO installs,
network downloads, global config/credentials, native P47 modification,
host Job Objects or product cleanup changes. Never asserts a product fix.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PristineChromiumRoot,
    [Parameter(Mandatory=$true)][string]$DepotToolsRoot,
    [Parameter(Mandatory=$true)][string]$FreshTestOwnedBuildRoot,
    [Parameter(Mandatory=$true)][string]$PrivateEvidenceDirectory,
    [Parameter(Mandatory=$true)][string]$PreflightFacts,
    [switch]$ApproveOneInstrumentedBuild
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$lockPath = Join-Path $repo 'docs/p1o-chromium156-toolchain.lock.json'
$lock = Get-Content -LiteralPath $lockPath -Raw -Encoding UTF8 | ConvertFrom-Json
$pristine = [IO.Path]::GetFullPath($PristineChromiumRoot).TrimEnd('\')
$depot = [IO.Path]::GetFullPath($DepotToolsRoot).TrimEnd('\')
$stage = [IO.Path]::GetFullPath($FreshTestOwnedBuildRoot).TrimEnd('\')
$privateDir = [IO.Path]::GetFullPath($PrivateEvidenceDirectory).TrimEnd('\')
function Require([bool]$valid, [string]$why) {
    if (!$valid) { throw ("P1O_PREREQUISITE_FAILURE: " + $why) }
}
function HashFile([string]$path) {
    Require (Test-Path -LiteralPath $path -PathType Leaf) 'BINARY_OR_SOURCE_MISSING'
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}
Require ($env:OS -eq 'Windows_NT') 'WINDOWS_AMD64_ISOLATED_REQUIRED'
Require ([bool]$ApproveOneInstrumentedBuild) 'OWNER_APPROVAL_REQUIRED'
Require ($stage -ne $pristine -and $stage -ne $depot -and
    !$stage.StartsWith($pristine + '\', [StringComparison]::OrdinalIgnoreCase) -and
    !$pristine.StartsWith($stage + '\', [StringComparison]::OrdinalIgnoreCase) -and
    !$stage.StartsWith($depot + '\', [StringComparison]::OrdinalIgnoreCase)) 'BUILD_TREE_NOT_ISOLATED'
Require (!(Test-Path -LiteralPath $stage)) 'BUILD_STAGING_MUST_BE_FRESH'
Require ($privateDir -ne $stage -and
    !$privateDir.StartsWith($pristine + '\', [StringComparison]::OrdinalIgnoreCase) -and
    !$privateDir.StartsWith($stage + '\', [StringComparison]::OrdinalIgnoreCase)) 'PRIVATE_EVIDENCE_TREE_NOT_ISOLATED'
Require (Test-Path -LiteralPath $PreflightFacts -PathType Leaf) 'PREPARED_PREFLIGHT_FACTS_REQUIRED'
$lockChecker = Join-Path $repo 'scripts/p1o_builder_and_witness_contract.py'
& python $lockChecker --lock $lockPath --source-root $pristine --builder-facts $PreflightFacts --require-builder-ready
Require ($LASTEXITCODE -eq 0) 'PINNED_SOURCE_OR_WINDOWS_TOOLCHAIN_NOT_READY'
$facts = Get-Content -LiteralPath $PreflightFacts -Raw | ConvertFrom-Json
Require ($facts.isolatedOwnedHost -eq $true -and
    $facts.chromiumCommit -eq $lock.source.commit -and
    $facts.depotToolsCommit -eq $lock.builder.depotTools.exactCommit) 'CHROMIUM_AND_DEPOT_SOURCE_PINS'
Require ((& git -C $pristine status --porcelain --untracked-files=no) -eq $null) 'PRISTINE_CHROMIUM_SOURCE_DIRTY'
Require (Test-Path -LiteralPath (Join-Path $depot 'gn.bat') -PathType Leaf) 'GN_BOOTSTRAP_MISSING'
Require (Test-Path -LiteralPath (Join-Path $depot 'autoninja.bat') -PathType Leaf) 'AUTONINJA_BOOTSTRAP_MISSING'

# The only build workspace is a fresh test-owned copy. Reusing --MIR against
# an existing directory or overwriting the immutable Chromium checkout is forbidden.
[void](New-Item -ItemType Directory -Force -Path $privateDir)
$overlay = Join-Path $privateDir 'new-test-owned-overlay'
Require (!(Test-Path -LiteralPath $overlay)) 'OVERLAY_MUST_BE_NEW'
$prepared = Join-Path $repo 'scripts/p1n_prepare_crashpad_test_witness.py'
& python $prepared --tagged-chromium $pristine --new-overlay $overlay | Out-Null
Require ($LASTEXITCODE -eq 0) 'TEST_OWNED_OVERLAY_EXACT_PIN_FAILED'

# robocopy is bounded to a NONEXISTENT destination; never use /MIR on an
# arbitrary pre-existing user directory, even an apparently empty one.
& robocopy $pristine $stage /E /COPY:DAT /DCOPY:DAT /R:0 /W:0 /NFL /NDL /NJH /NJS /NP | Out-Null
Require ($LASTEXITCODE -le 7) 'ISOLATED_CHROMIUM_SOURCE_COPY_FAILED'
Require (Test-Path -LiteralPath (Join-Path $stage '.git')) 'STAGING_GIT_METADATA_MISSING'
$stageHead = (& git -C $stage rev-parse HEAD | Out-String).Trim()
Require ($stageHead -eq $lock.source.commit) 'STAGED_CHROMIUM_HEAD_CHANGED'

$paths = @(
    'third_party/crashpad/crashpad/util/win/exception_handler_server.cc',
    'third_party/crashpad/crashpad/handler/crash_report_upload_thread.cc',
    'third_party/crashpad/crashpad/util/win/p1n_test_owned_witness.h'
)
$patchHashes = [ordered]@{}
foreach ($relative in $paths) {
    $src = Join-Path $overlay $relative
    $dst = Join-Path $stage $relative
    Require (Test-Path -LiteralPath $src -PathType Leaf) 'OVERLAY_FILE_MISSING'
    if ($relative.EndsWith('.cc')) {
        $data = [IO.File]::ReadAllText($src)
        $marker = '#include "util/win/p1n_test_owned_witness.h"'
        Require ($data.Split(@($marker),[StringSplitOptions]::None).Length -eq 2) 'MACRO_ANCHOR_NOT_EXACT'
        # Explicit compile macro exists ONLY in the two stage-only translation
        # units, without touching project GN flags or production code.
        $data = $data.Replace($marker,
            ('#define MIGRATOR_P1_TEST_OWNED_WITNESS 1' + [Environment]::NewLine + $marker))
        [IO.File]::WriteAllText($dst, $data, ([Text.UTF8Encoding]::new($false)))
    } else {
        Copy-Item -LiteralPath $src -Destination $dst
    }
    $patchHashes[$relative] = HashFile $dst
}
$outDir = Join-Path $stage 'out\P1O-156-instrumented'
[void](New-Item -ItemType Directory -Force -Path $outDir)
$gnArgs = (($lock.gn.args | ForEach-Object { [string]$_ }) -join [Environment]::NewLine) + [Environment]::NewLine
[IO.File]::WriteAllText((Join-Path $outDir 'args.gn'), $gnArgs, ([Text.UTF8Encoding]::new($false)))

$oldPath = $env:PATH
$oldDepotUpdate = $env:DEPOT_TOOLS_UPDATE
$oldWinToolchain = $env:DEPOT_TOOLS_WIN_TOOLCHAIN
$oldVs2026 = $env:vs2026_install
try {
    $env:PATH = $depot + ';' + $oldPath
    $env:DEPOT_TOOLS_UPDATE = '0'
    $env:DEPOT_TOOLS_WIN_TOOLCHAIN = '0'
    $programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    $vswhere = Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
    $entry = @(& $vswhere -latest -products 'Microsoft.VisualStudio.Product.BuildTools' -property installationPath)
    Require ($LASTEXITCODE -eq 0 -and $entry.Count -eq 1) 'PINNED_VS2026_PATH_UNKNOWN'
    $env:vs2026_install = $entry[0].Trim()
    Push-Location $stage
    try {
        & (Join-Path $depot 'gn.bat') gen 'out\P1O-156-instrumented'
        Require ($LASTEXITCODE -eq 0) 'GN_GEN_FAILED'
        & (Join-Path $depot 'autoninja.bat') -C 'out\P1O-156-instrumented' 'chrome'
        Require ($LASTEXITCODE -eq 0) 'INSTRUMENTED_CHROMIUM_COMPILATION_FAILED'
    } finally {
        Pop-Location
    }
} finally {
    $env:PATH = $oldPath
    $env:DEPOT_TOOLS_UPDATE = $oldDepotUpdate
    $env:DEPOT_TOOLS_WIN_TOOLCHAIN = $oldWinToolchain
    $env:vs2026_install = $oldVs2026
}

$chrome = Join-Path $outDir 'chrome.exe'
$binarySha = HashFile $chrome
$buildNinja = Join-Path $outDir 'build.ninja'
$originalP47 = Join-Path $repo $lock.p47.path
$originalBlob = (& git -C $repo rev-parse ('HEAD:' + $lock.p47.path) | Out-String).Trim()
Require ($originalBlob -eq $lock.p47.originalGitBlobSha1) 'ORIGINAL_P47_SOURCE_CHANGED'
$sourceFinal = (& git -C $pristine rev-parse HEAD | Out-String).Trim()
Require ($sourceFinal -eq $lock.source.commit) 'PRISTINE_CHROMIUM_REVISION_CHANGED'
& python $lockChecker --lock $lockPath --source-root $pristine | Out-Null
Require ($LASTEXITCODE -eq 0) 'ORIGINAL_SOURCE_BYTES_CHANGED_AFTER_BUILD'

$receipt = [ordered]@{
    schema = 'migrator-p1o-test-owned-build-receipt/v1'
    status = 'instrumented_test_binary_compiled'
    sourceGitSha = $lock.source.commit
    crashpadRevision = $lock.source.crashpadRevision
    depotToolsGitSha = $lock.builder.depotTools.exactCommit
    originalP47GitBlobSha1 = $originalBlob
    originalP47Sha256 = HashFile $originalP47
    visualStudioVersion = $facts.visualStudioVersion
    visualStudioBuildRecord = $facts.visualStudioBuild
    visualStudioBinarySha256 = $facts.filesSha256.'MSBuild.exe'
    buildToolBinariesSha256 = $facts.filesSha256
    windowsSdk = $facts.windowsSdk
    gnArgumentsSha256 = HashFile (Join-Path $outDir 'args.gn')
    ninjaManifestSha256 = HashFile $buildNinja
    patchedTranslationUnitsSha256 = $patchHashes
    chromeExeSha256 = $binarySha
    chromeDllSha256 = $(if(Test-Path -LiteralPath (Join-Path $outDir 'chrome.dll')) {
        HashFile (Join-Path $outDir 'chrome.dll')
    } else { $null })
    compiled = $true
    executed = $false
    binaryDigestForOriginalHistoricalP47 = 'unknown'
    directClientCompletionWitness = 'not_run'
    originalP47Changed = $false
    productFixProven = $false
    rootCauseProven = $false
    fixClaim = $false
    releaseAllowed = $false
}
$receiptPath = Join-Path $privateDir 'p1o-test-owned-build-receipt.json'
$receiptJson = $receipt | ConvertTo-Json -Depth 12
[IO.File]::WriteAllText($receiptPath, $receiptJson, ([Text.UTF8Encoding]::new($false)))
Write-Output 'P1O_TEST_OWNED_CHROMIUM_COMPILED_RUNTIME_NOT_YET_EXECUTED_NO_FIX_CLAIM'
