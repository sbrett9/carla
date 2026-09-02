<#
.SYNOPSIS
    Cook one generated world on its own and package it as a single file for delivery.

.DESCRIPTION
    Produces a .zip holding just one world -- roughly 100 MB -- that somebody else can add to an
    existing CARLA package without being sent the whole 30+ GB build.

    The world is cooked as DLC against a release the base cook archived. That archive is a list of
    everything already in the base package, so this cook can leave out the shared material, textures
    and engine content and emit only what the world itself adds. Without it there is nothing to
    subtract from, and the cook has no way to tell "already shipped" from "new".

    The world must already have been exported as a plugin under
    Unreal\CarlaUnreal\Plugins\GeneratedWorlds -- that is what the World Package Importer's
    "Make this world available to packaged builds" checkbox does.

    Install the result with InstallWorld.ps1.

.PARAMETER World
    Name of the exported world, e.g. Arapahoe_I25. Matches the plugin directory name.

.PARAMETER BasedOnRelease
    Release to cook against. Defaults to the current short Carla commit, which is what the base cook
    names its release. Pass this explicitly when packaging a world for a base build that was cooked
    at a different commit than the one checked out now.

.PARAMETER OutputDirectory
    Where to write the .zip. Default: Build\WorldPackages.

.PARAMETER Config
    Build configuration the target package was cooked in. Must match, or the world's cooked files
    will not be loadable by it.

.PARAMETER SkipCook
    Package whatever a previous run already cooked, without cooking again. For iterating on the
    packaging step itself.

.EXAMPLE
    .\PackageWorld.ps1 -World Arapahoe_I25
    Cook and package that world against the current commit's release.

.EXAMPLE
    .\PackageWorld.ps1 -World Arapahoe_I25 -BasedOnRelease 6874d569b
    Package it for a base build that was cooked at commit 6874d569b.
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory = $true)]
    [string]$World,

    [string]$BasedOnRelease,

    [string]$OutputDirectory,

    [ValidateSet('Development', 'Shipping', 'Debug')]
    [string]$Config = 'Development',

    [switch]$SkipCook,

    [string]$UnrealEngineRoot,

    [Alias('h')]
    [switch]$Help
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Info { param([Parameter(ValueFromPipeline)][string]$Message) Write-Host $Message -ForegroundColor Green }
function Write-Warn { param([Parameter(ValueFromPipeline)][string]$Message) Write-Host $Message -ForegroundColor Yellow }
function Write-Fail { param([Parameter(ValueFromPipeline)][string]$Message) Write-Host $Message -ForegroundColor Red }

if ($Help) {
    @'
PackageWorld.ps1 - cook one generated world and package it as a single deliverable file.

USAGE:
  .\PackageWorld.ps1 -World <name> [options]

OPTIONS:
  -World <name>                 Exported world to package (required).
  -BasedOnRelease <name>        Release to cook against (default: current short Carla commit).
  -OutputDirectory <path>       Where to write the .zip (default: Build\WorldPackages).
  -Config <cfg>                 Development (default) | Shipping | Debug.
  -SkipCook                     Package an existing cook without re-cooking.
  -UnrealEngineRoot <path>      Engine root (default: CARLA_UNREAL_ENGINE_PATH or <repo-parent>\UE_5_7_4).
  -h, -Help                     This text.

The world must already be exported as a plugin - use the World Package Importer's
"Make this world available to packaged builds" checkbox. Install the result with InstallWorld.ps1.
'@ | Write-Host
    exit 0
}

$CarlaRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$ProjectDir = Join-Path $CarlaRoot 'Unreal\CarlaUnreal'
$UProject = Join-Path $ProjectDir 'CarlaUnreal.uproject'
$PluginDir = Join-Path $ProjectDir "Plugins\GeneratedWorlds\$World"
$Platform = 'Windows'

if (-not $UnrealEngineRoot) {
    $UnrealEngineRoot = $env:CARLA_UNREAL_ENGINE_PATH
    if (-not $UnrealEngineRoot) {
        $UnrealEngineRoot = (Resolve-Path (Join-Path $CarlaRoot '..\UE_5_7_4') -ErrorAction SilentlyContinue).Path
    }
}
if (-not $UnrealEngineRoot -or -not (Test-Path $UnrealEngineRoot)) {
    Write-Fail "Unreal Engine root not found. Pass -UnrealEngineRoot or set CARLA_UNREAL_ENGINE_PATH."
    exit 1
}
$RunUAT = Join-Path $UnrealEngineRoot 'Engine\Build\BatchFiles\RunUAT.bat'

if (-not $OutputDirectory) { $OutputDirectory = Join-Path $CarlaRoot 'Build\WorldPackages' }

# ── Preconditions, each with the remedy rather than just the complaint ───────

if (-not (Test-Path $PluginDir)) {
    Write-Fail "No exported world named '$World'."
    Write-Fail "  Looked in: $PluginDir"
    Write-Fail "  Export one with the World Package Importer, leaving"
    Write-Fail "  'Make this world available to packaged builds' ticked."
    exit 1
}
$UPluginFile = Join-Path $PluginDir "$World.uplugin"
if (-not (Test-Path $UPluginFile)) {
    Write-Fail "'$World' has no $World.uplugin; the export did not finish."
    exit 1
}

if (-not $BasedOnRelease) {
    $BasedOnRelease = (& git -C $CarlaRoot log -1 --format=%h 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $BasedOnRelease) {
        Write-Fail "Could not read the current commit to name the release. Pass -BasedOnRelease."
        exit 1
    }
}

# The base cook writes this. Its absence means the package this world would be installed into was
# cooked without -CreateReleaseVersion, and cannot host a separately cooked world at all.
$ReleaseDir = Join-Path $ProjectDir "Releases\$BasedOnRelease\$Platform"
if (-not (Test-Path $ReleaseDir)) {
    Write-Fail "No release '$BasedOnRelease' to cook against."
    Write-Fail "  Looked in: $ReleaseDir"
    Write-Fail "  The base package has to be cooked first, with CARLA_COOK_CREATE_RELEASE_VERSION on"
    Write-Fail "  (it is on by default): .\Scripts\Windows\MakeDistribution.ps1 -Build"
    exit 1
}

Write-Info "world        : $World"
Write-Info "release      : $BasedOnRelease"
Write-Info "config       : $Config"
Write-Info "output       : $OutputDirectory"

# ── Cook the world on its own ────────────────────────────────────────────────
#
# -iterate is deliberately absent: UAT throws outright when it is combined with
# -BasedOnReleaseVersion. So is -CreateReleaseVersion, which cannot be combined with -DLCName.
# -DLCIncludeEngineContent is NOT passed: the world is self-contained inside its plugin, so the
# default restriction to the plugin's own content is exactly what we want, and it fails loudly if
# something has escaped it.
#
# -stagingdirectory is left unset on purpose. With -DLCName, UAT stages into the plugin's own
# Saved\StagedBuilds; naming the base package's directory instead would stage this world on top of it.

$StageRoot = Join-Path $PluginDir "Saved\StagedBuilds\$Platform"

if (-not $SkipCook) {
    Write-Info "`n[world] cooking $World against release $BasedOnRelease"
    $uatArgs = @(
        'BuildCookRun',
        "-project=$UProject",
        '-nocompileeditor',
        '-nop4',
        '-cook',
        '-stage',
        '-package',
        "-clientconfig=$Config",
        "-TargetPlatform=$Platform",
        "-Platform=$Platform",
        "-BasedOnReleaseVersion=$BasedOnRelease",
        "-DLCName=$UPluginFile"
    )
    $prevEAP = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $RunUAT @uatArgs }
    finally { $ErrorActionPreference = $prevEAP }
    if ($LASTEXITCODE -ne 0) {
        Write-Fail "`nCook failed (exit $LASTEXITCODE)."
        Write-Fail "If it complained that content is 'being referenced by DLC', something the world"
        Write-Fail "needs lives outside its plugin. Re-export the world and try again."
        exit $LASTEXITCODE
    }
}

if (-not (Test-Path $StageRoot)) {
    Write-Fail "The cook produced no staged output at $StageRoot."
    exit 1
}

# What the cook staged for this world, wherever UAT put it under the stage root.
$Payload = Get-ChildItem -Path $StageRoot -Recurse -Directory -Filter $World |
    Where-Object { Test-Path (Join-Path $_.FullName "$World.uplugin") } |
    Select-Object -First 1
if (-not $Payload) {
    Write-Fail "Could not find the cooked $World plugin under $StageRoot."
    exit 1
}

# ── Describe what this is, so an installer can refuse the wrong package ──────
#
# What decides installability is the declared world interface version, not a hash. A hash only ever
# answers "identical?", so it refuses builds that differ in ways no world can observe -- a
# documentation commit, say -- while saying nothing about whether two builds are actually compatible.
# The declaration states what a build promises; see Config\DefaultWorldInterface.ini.
#
# The commit hashes are recorded too, but only so a person can identify exactly which build produced
# a world. Nothing compares them.

function Get-WorldInterfaceVersion([string]$IniPath) {
    if (-not (Test-Path $IniPath)) { return $null }
    $text = Get-Content $IniPath -Raw
    if ($text -match '(?ms)^\s*\[WorldInterface\](.*?)(^\s*\[|\z)') {
        $body = $Matches[1]
        $maj = if ($body -match '(?m)^\s*Major\s*=\s*(\d+)') { [int]$Matches[1] } else { $null }
        $min = if ($body -match '(?m)^\s*Minor\s*=\s*(\d+)') { [int]$Matches[1] } else { $null }
        if ($null -ne $maj -and $null -ne $min) { return @{ Major = $maj; Minor = $min } }
    }
    return $null
}

$InterfaceIni = Join-Path $ProjectDir 'Config\DefaultWorldInterface.ini'
$Interface = Get-WorldInterfaceVersion $InterfaceIni
if (-not $Interface) {
    Write-Fail "Could not read the world interface version from $InterfaceIni."
    Write-Fail "Without it there is nothing to record for an installer to check against."
    exit 1
}

function Get-GitHash([string]$Path) {
    if (-not (Test-Path $Path)) { return '' }
    $h = (& git -C $Path log -1 --format=%H 2>$null)
    if ($LASTEXITCODE -ne 0) { return '' }
    return $h
}

$manifest = [ordered]@{
    formatVersion         = 1
    world                 = $World
    mapPackage            = "/$World/Maps/$World"
    worldInterfaceMajor   = $Interface.Major
    worldInterfaceMinor   = $Interface.Minor
    basedOnRelease        = $BasedOnRelease
    config                = $Config
    platform              = $Platform
    # Identification only. Never compared -- see the note above.
    carlaGitHash          = Get-GitHash $CarlaRoot
    contentGitHash        = Get-GitHash (Join-Path $ProjectDir 'Content\Carla')
    unrealGitHash         = Get-GitHash $UnrealEngineRoot
    packagedAtUtc         = (Get-Date).ToUniversalTime().ToString('o')
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$StagingCopy = Join-Path ([System.IO.Path]::GetTempPath()) "carla-world-$World-$PID"
if (Test-Path $StagingCopy) { Remove-Item -Recurse -Force $StagingCopy }
New-Item -ItemType Directory -Force -Path $StagingCopy | Out-Null
try {
    Copy-Item -Recurse -Force $Payload.FullName (Join-Path $StagingCopy $World)
    $manifest | ConvertTo-Json | Set-Content -Path (Join-Path $StagingCopy 'world.json') -Encoding UTF8

    $ZipPath = Join-Path $OutputDirectory "$World.zip"
    if (Test-Path $ZipPath) { Remove-Item -Force $ZipPath }
    Compress-Archive -Path (Join-Path $StagingCopy '*') -DestinationPath $ZipPath
}
finally {
    if (Test-Path $StagingCopy) { Remove-Item -Recurse -Force $StagingCopy -ErrorAction SilentlyContinue }
}

$SizeMB = [math]::Round((Get-Item $ZipPath).Length / 1MB, 1)
Write-Info "`nPackaged $World"
Write-Info "  file    : $ZipPath"
Write-Info "  size    : $SizeMB MB"
Write-Info "  needs   : world interface $($Interface.Major).x, minor $($Interface.Minor) or later; $Config, $Platform"
Write-Info "`nInstall it with:"
Write-Info "  .\Scripts\Windows\InstallWorld.ps1 -Package '$ZipPath' -Into <package directory>"
