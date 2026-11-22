#requires -Version 5.1
<#
P1-O PRIVATE, read-only, opt-in Windows builder preflight.
Requires owner-approved isolated Windows AMD64 VM (not standard 14GiB Actions).
NEVER installs tools, changes global PATH/credentials, launches Chrome or P47.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ChromiumRoot,
    [Parameter(Mandatory=$true)][string]$DepotToolsRoot,
    [Parameter(Mandatory=$true)][string]$ScratchRoot,
    [Parameter(Mandatory=$true)][string]$PrivateReceipt,
    [switch]$OwnerApprovedIsolatedBuilder,
    [switch]$RequireReady
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$lockPath = Join-Path $repo 'docs/p1o-chromium156-toolchain.lock.json'
$lock = Get-Content -LiteralPath $lockPath -Raw -Encoding UTF8 | ConvertFrom-Json

function Sha([string]$path) {
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function GitHead([string]$path) {
    if (!(Test-Path -LiteralPath $path -PathType Container)) { return $null }
    $v = & git -C $path rev-parse HEAD 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    return ($v | Out-String).Trim()
}

$facts = [ordered]@{
    schema = 'migrator-p1o-windows-builder-facts/v1'
    os = 'unknown'
    architecture = [Environment]::GetEnvironmentVariable('PROCESSOR_ARCHITECTURE')
    scratchFilesystem = 'unknown'
    scratchFreeGiB = 0
    ramGiB = 0
    visualStudioVersion = 'unavailable'
    visualStudioBuild = 'unavailable'
    visualStudioChannel = 'unavailable'
    windowsSdk = 'unavailable'
    debugToolsFileVersion = 'unavailable'
    depotToolsCommit = 'unavailable'
    chromiumCommit = 'unavailable'
    crashpadRevision = 'unavailable'
    isolatedOwnedHost = [bool]$OwnerApprovedIsolatedBuilder
    depotToolsAutoUpdate = $true
    sourceTreeClean = $false
    filesSha256 = [ordered]@{}
    installedVisualStudioVersion = 'unavailable'
    toolchainBuildRecordSource = 'unverified'
}
$problems = @()
if ($env:OS -eq 'Windows_NT') { $facts.os = 'Windows' }
else { $problems += 'WINDOWS_BUILDER_REQUIRED' }
if ($facts.os -eq 'Windows') {
    $system = Get-CimInstance Win32_ComputerSystem
    if ($null -ne $system -and $system.TotalPhysicalMemory -gt 0) {
        $facts.ramGiB = [Math]::Floor([double]$system.TotalPhysicalMemory / 1GB)
    }
    $drivePath = [System.IO.Path]::GetPathRoot([System.IO.Path]::GetFullPath($ScratchRoot))
    $drive = [System.IO.DriveInfo]::new($drivePath)
    if ($drive.IsReady) {
        $facts.scratchFilesystem = $drive.DriveFormat
        $facts.scratchFreeGiB = [Math]::Floor([double]$drive.AvailableFreeSpace / 1GB)
    }
    $programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    if ([string]::IsNullOrWhiteSpace($programFilesX86)) {
        $problems += 'PROGRAM_FILES_X86_UNKNOWN'
    } else {
        $vswhere = Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path -LiteralPath $vswhere -PathType Leaf) {
            $output = & $vswhere -latest -products '*' -format json -requires 'Microsoft.VisualStudio.Workload.NativeDesktop' 'Microsoft.VisualStudio.Component.VC.ATLMFC'
            if ($LASTEXITCODE -eq 0 -and $output) {
                $instances = @($output | Out-String | ConvertFrom-Json)
                if ($instances.Count -gt 0 -and $null -ne $instances[0]) {
                    $instance = $instances[0]
                    $actual = [string]$instance.installationVersion
                    $facts.installedVisualStudioVersion = $actual
                    $facts.visualStudioChannel = [string]$instance.channelId
                    if ($actual -match '^18\.11\.4(\.|$)' -and
                        $facts.visualStudioChannel -eq 'VisualStudio.18.Release' -and
                        $instance.productId -eq 'Microsoft.VisualStudio.Product.BuildTools') {
                        $facts.visualStudioVersion = '18.11.4'
                        # Published release mapping, NOT a compiler binary digest.
                        $facts.visualStudioBuild = '12402.416'
                        $facts.toolchainBuildRecordSource = 'microsoft_stable_18_11_4_release_history'
                    }
                    $msbuild = Join-Path ([string]$instance.installationPath) 'MSBuild\Current\Bin\MSBuild.exe'
                    $facts.filesSha256['MSBuild.exe'] = Sha $msbuild
                }
            }
        }
        $kits = Join-Path $programFilesX86 'Windows Kits\10'
        $includes = Join-Path $kits 'Include\10.0.28000.0'
        if (Test-Path -LiteralPath $includes -PathType Container) {
            $facts.windowsSdk = '10.0.28000.0'
        }
        $dbghelp = Join-Path $kits 'Debuggers\x64\dbghelp.dll'
        if (Test-Path -LiteralPath $dbghelp -PathType Leaf) {
            $facts.debugToolsFileVersion = [string]((Get-Item -LiteralPath $dbghelp).VersionInfo.FileVersion)
            $facts.filesSha256['Debuggers.dbghelp.dll'] = Sha $dbghelp
            try {
                if ([version]$facts.debugToolsFileVersion -lt [version]$lock.builder.windowsSdk.minDebuggingToolsVersion) {
                    $problems += 'DEBUGGER_SDK_VERSION_TOO_OLD'
                }
            } catch {
                $problems += 'DEBUGGER_SDK_VERSION_UNATTESTABLE'
            }
        } else {
            $problems += 'DEBUGGING_TOOLS_NOT_INSTALLED'
        }
    }
    $facts.chromiumCommit = GitHead $ChromiumRoot
    $facts.depotToolsCommit = GitHead $DepotToolsRoot
    if ($facts.chromiumCommit -eq $lock.source.commit) {
        $readme = Join-Path $ChromiumRoot 'third_party/crashpad/README.chromium'
        if (Test-Path -LiteralPath $readme -PathType Leaf) {
            $txt = Get-Content -LiteralPath $readme -Raw -Encoding UTF8
            if ($txt.Contains('Revision: ' + $lock.source.crashpadRevision)) {
                $facts.crashpadRevision = $lock.source.crashpadRevision
            }
        }
    }
    if ($facts.chromiumCommit) {
        $dirty = & git -C $ChromiumRoot status --porcelain --untracked-files=no 2>$null
        $facts.sourceTreeClean = ($LASTEXITCODE -eq 0 -and -not $dirty)
    }
    if (!(Test-Path -LiteralPath (Join-Path $DepotToolsRoot 'gn.bat')) -or
        !(Test-Path -LiteralPath (Join-Path $DepotToolsRoot 'autoninja.bat'))) {
        $problems += 'PINNED_DEPOT_TOOLS_NOT_BOOTSTRAPPED'
    }
    # The future build script disables auto update in its child environment.
    # This is a process-scoped intent, never a persistent/global setting.
    $facts.depotToolsAutoUpdate = $false
}
$target = [System.IO.Path]::GetFullPath($PrivateReceipt)
$folder = [System.IO.Path]::GetDirectoryName($target)
if (!(Test-Path -LiteralPath $folder)) {
    [void](New-Item -ItemType Directory -Force -Path $folder)
}
$facts | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $target -Encoding UTF8
$assessmentPath = $target + '.assessment.json'
$checker = Join-Path $repo 'scripts/p1o_builder_and_witness_contract.py'
# Never print host identity, private absolute paths, environment, credentials.
& python $checker --lock $lockPath --source-root $ChromiumRoot --builder-facts $target --out $assessmentPath
$code = $LASTEXITCODE
if ($code -ne 0) {
    Write-Output 'P1O_PREREQUISITE_FAILURE: PINNED_SOURCE_VALIDATION'
    exit 21
}
$assessment = Get-Content -LiteralPath $assessmentPath -Raw | ConvertFrom-Json
if ($assessment.builder.status -ne 'ready_for_isolated_compile' -or $problems.Count -ne 0) {
    Write-Output 'P1O_PREREQUISITE_FAILURE: ISOLATED_PINNED_WINDOWS_BUILDER'
    if ($RequireReady) { exit 22 }
    exit 0
}
Write-Output 'P1O_PINNED_WINDOWS_BUILDER_PREFLIGHT_READY_NOT_COMPILED'
exit 0
