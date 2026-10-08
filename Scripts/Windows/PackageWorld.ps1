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

    Install the result with InstallWorld.ps1, which sits beside this script.

    This script needs a CARLA checkout with the editor project: it is for whoever makes worlds. A
    CARLA distribution ships it in world-tools\, which is inside no checkout, so there it takes
    -CarlaRoot.

.PARAMETER World
    Name of the exported world, e.g. Arapahoe_I25. Matches the plugin directory name.

.PARAMETER BasedOnRelease
    Release to cook against. Defaults to the current short Carla commit, which is what the base cook
    names its release. Pass this explicitly when packaging a world for a base build that was cooked
    at a different commit than the one checked out now.

.PARAMETER Distribution
    A CARLA distribution the world is for: the folder holding VERSION and CarlaServer\. Instead of
    needing a base cook on this machine, the release is recorded from that distribution: its VERSION
    names the CARLA commit it was built from, the release is named as the base cook names it (the
    short form of that commit), and its CarlaServer\CarlaUnreal\AssetRegistry.bin -- the list of
    everything the distribution already contains -- is copied to
    Unreal\CarlaUnreal\Releases\<release>\Windows\AssetRegistry.bin. Then the world is cooked as
    usual. Refused unless this checkout is at the distribution's CARLA commit, because the cook
    reads the checkout's content and subtracts the distribution's. The configuration is taken from
    the distribution's folder name (Carla-<version>-Win64-<config>) when -Config is not given.

.PARAMETER CarlaRoot
    The CARLA checkout holding Unreal\CarlaUnreal\CarlaUnreal.uproject. Default: the checkout this
    script is in. Required when the script is run from a distribution's world-tools folder.

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

.EXAMPLE
    .\PackageWorld.ps1 -World Arapahoe_I25 -Distribution D:\Carla-0.10.0-Win64-Development
    Package it for that distribution, recording the distribution's release in this checkout first.

.EXAMPLE
    .\world-tools\PackageWorld.ps1 -World Arapahoe_I25 -CarlaRoot D:\carla -Distribution .
    The same, run from inside the distribution, against the checkout at D:\carla.
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory = $true)]
    [string]$World,

    [string]$BasedOnRelease,

    [string]$Distribution,

    [string]$CarlaRoot,

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
  -Distribution <folder>        Record the release from this CARLA distribution, then cook against
                                it. The checkout must be at the distribution's CARLA commit.
  -CarlaRoot <path>             The CARLA checkout (default: the one this script is in; required
                                when run from a distribution's world-tools folder).
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

# The checkout: named, or the one this script sits in (Scripts\Windows, two folders down). A
# distribution ships this script in world-tools\, which is inside no checkout, so there it is named.
if (-not $CarlaRoot) { $CarlaRoot = Join-Path $PSScriptRoot '..\..' }
if (-not (Test-Path (Join-Path $CarlaRoot 'Unreal\CarlaUnreal\CarlaUnreal.uproject'))) {
    Write-Fail "No CARLA checkout at $CarlaRoot (no Unreal\CarlaUnreal\CarlaUnreal.uproject)."
    Write-Fail "  Pass -CarlaRoot <checkout>. Run from a distribution's world-tools folder, this script"
    Write-Fail "  is inside no checkout."
    exit 1
}
$CarlaRoot = (Resolve-Path $CarlaRoot).Path
$ProjectDir = Join-Path $CarlaRoot 'Unreal\CarlaUnreal'
$UProject = Join-Path $ProjectDir 'CarlaUnreal.uproject'
$PluginDir = Join-Path $ProjectDir "Plugins\GeneratedWorlds\$World"
# Two different names for the same target, and they are not interchangeable. UAT's -Platform and
# -TargetPlatform want the TARGET name (Win64); the directories a cook writes -- Releases\ and
# Saved\StagedBuilds\ -- are named for the COOK PLATFORM (Windows). Passing 'Windows' as the target
# fails with "The platform name Windows is not a valid platform name".
$Platform = 'Win64'
$CookPlatform = 'Windows'

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

# A world ships one way or the other. Left unmarked it is cooked into the base package, and cooking
# it separately against that same base yields nothing, because every one of its packages is already
# there. Checking up front means saying that plainly instead of producing an empty package or an
# unexplained one.
$MarkerFile = Join-Path $PluginDir 'DeliverSeparately.txt'
if (-not (Test-Path $MarkerFile)) {
    Write-Fail "'$World' is not marked for separate delivery, so it is cooked into the base package."
    Write-Fail "Packaging it as an addition to that base would produce an empty world."
    Write-Fail ""
    Write-Fail "To deliver it separately instead:"
    Write-Fail "  1. create $MarkerFile"
    Write-Fail "  2. re-cook the base so it no longer contains the world:"
    Write-Fail "     $(Join-Path $CarlaRoot 'Scripts\Windows\MakeDistribution.ps1') -Build"
    Write-Fail "  3. run this again"
    Write-Fail ""
    Write-Fail "Or leave it as it is and deliver the base package, which already contains the world."
    exit 1
}

# ── The release, recorded from a distribution when one is named ─────────────
#
# A DLC cook subtracts what the base already holds, and the base's asset registry is that list. The
# base cook archives it under Releases\<release>\<platform>\ on the machine that cooked the base; a
# distribution carries the same registry, byte for byte, as CarlaServer\CarlaUnreal\AssetRegistry.bin.
# The cook reads Metadata\DevelopmentAssetRegistry.bin there first and falls back to AssetRegistry.bin
# (CookOnTheFlyServer.cpp, RecordDLCPackagesFromBaseGame), so the copy alone is a release to cook
# against. The release is named the way the base cook names it -- the short form of the CARLA commit
# (Unreal\Package\CookGeneratedWorlds.cmake.in) -- and the world cooks the checkout's content, so the
# checkout has to be at that same commit.

if ($Distribution) {
    if (-not (Test-Path $Distribution -PathType Container)) {
        Write-Fail "No distribution folder at $Distribution."
        exit 1
    }
    $Distribution = (Resolve-Path $Distribution).Path
    $DistVersion = Join-Path $Distribution 'VERSION'
    if (-not (Test-Path $DistVersion -PathType Leaf)) {
        Write-Fail "$Distribution has no VERSION file, so which CARLA it was built from is unknown."
        Write-Fail "  Name the distribution's root: the folder holding VERSION and CarlaServer\."
        exit 1
    }
    $DistCommit = $null
    foreach ($Line in (Get-Content $DistVersion)) {
        if ($Line -match '^\s*Carla git hash:\s*([0-9a-fA-F]{40})\s*$') {
            $DistCommit = $Matches[1].ToLowerInvariant()
            break
        }
    }
    if (-not $DistCommit) {
        Write-Fail "$DistVersion names no Carla git hash, so the release it was cooked as cannot be named."
        exit 1
    }

    $DistServer = Join-Path $Distribution 'CarlaServer'
    if (-not (Test-Path (Join-Path $DistServer 'CarlaUnreal.exe'))) {
        if (Test-Path (Join-Path $DistServer 'CarlaUnreal.sh')) {
            Write-Fail "$Distribution is a Linux distribution, and this script cooks for Windows. Use PackageWorld.sh."
        } else {
            Write-Fail "$Distribution holds no CarlaServer\CarlaUnreal.exe; it is not a CARLA distribution."
        }
        exit 1
    }
    $DistRegistry = Join-Path $DistServer 'CarlaUnreal\AssetRegistry.bin'
    if (-not (Test-Path $DistRegistry -PathType Leaf)) {
        Write-Fail "$Distribution carries no CarlaServer\CarlaUnreal\AssetRegistry.bin, the list of what it holds."
        Write-Fail "  Without it there is nothing to cook a world against."
        exit 1
    }

    # A distribution's folder is named Carla-<version>-Win64-<config>, and a world must be cooked in
    # the configuration of the package it is installed into.
    $DistLeaf = Split-Path $Distribution -Leaf
    if ($DistLeaf -match '-(Development|Shipping|Debug)$') {
        $DistConfig = $Matches[1]
        if ($PSBoundParameters.ContainsKey('Config') -and $Config -ne $DistConfig) {
            Write-Fail "-Config $Config, but $DistLeaf is a $DistConfig distribution; the world would not load in it."
            exit 1
        }
        $Config = $DistConfig
    }

    $CheckoutCommit = "$(& git -C $CarlaRoot rev-parse HEAD 2>$null)".Trim().ToLowerInvariant()
    if ($LASTEXITCODE -ne 0 -or -not $CheckoutCommit) {
        Write-Fail "Could not read the commit $CarlaRoot is at."
        exit 1
    }
    if ($CheckoutCommit -ne $DistCommit) {
        Write-Fail "This checkout is not at the commit the distribution was built from:"
        Write-Fail "  checkout     : $CheckoutCommit ($CarlaRoot)"
        Write-Fail "  distribution : $DistCommit ($DistVersion)"
        Write-Fail "  The cook reads the checkout's content and subtracts the distribution's, so both must"
        Write-Fail "  be one build. Check out $DistCommit, or package against a release cooked here."
        exit 1
    }
    $DistRelease = "$(& git -C $CarlaRoot rev-parse --short $DistCommit 2>$null)".Trim()
    if ($LASTEXITCODE -ne 0 -or -not $DistRelease) {
        Write-Fail "Could not name the release for commit $DistCommit."
        exit 1
    }
    if ($BasedOnRelease -and $BasedOnRelease -ne $DistRelease) {
        Write-Fail "-BasedOnRelease $BasedOnRelease, but the distribution's release is $DistRelease. Give one or the other."
        exit 1
    }
    $BasedOnRelease = $DistRelease

    $RecordDir = Join-Path $ProjectDir "Releases\$BasedOnRelease\$CookPlatform"
    $Recorded = Join-Path $RecordDir 'AssetRegistry.bin'
    if (Test-Path $Recorded) {
        if ((Get-FileHash $Recorded -Algorithm SHA256).Hash -ne (Get-FileHash $DistRegistry -Algorithm SHA256).Hash) {
            Write-Fail "Release '$BasedOnRelease' is already recorded here, from another build:"
            Write-Fail "  $Recorded differs from $DistRegistry."
            Write-Fail "  Remove $RecordDir to record the release from this distribution."
            exit 1
        }
        Write-Info "release '$BasedOnRelease' is already recorded from this distribution's asset registry"
    } elseif (Test-Path (Join-Path $RecordDir 'Metadata\DevelopmentAssetRegistry.bin')) {
        # The cook would read that file first, and it did not come from the distribution.
        Write-Fail "Release '$BasedOnRelease' holds a registry from a cook on this machine but no AssetRegistry.bin:"
        Write-Fail "  $RecordDir"
        Write-Fail "  Remove it to record the release from this distribution."
        exit 1
    } else {
        New-Item -ItemType Directory -Force -Path $RecordDir | Out-Null
        Copy-Item -Force $DistRegistry $Recorded
        Write-Info "recorded release '$BasedOnRelease' from $DistRegistry"
    }
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
$ReleaseDir = Join-Path $ProjectDir "Releases\$BasedOnRelease\$CookPlatform"
if (-not (Test-Path $ReleaseDir)) {
    Write-Fail "No release '$BasedOnRelease' to cook against."
    Write-Fail "  Looked in: $ReleaseDir"
    Write-Fail "  The base package has to be cooked first, with CARLA_COOK_CREATE_RELEASE_VERSION on"
    Write-Fail "  (it is on by default): $(Join-Path $CarlaRoot 'Scripts\Windows\MakeDistribution.ps1') -Build"
    Write-Fail "  Or record it from the distribution the world is for: -Distribution <folder>."
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

$StageRoot = Join-Path $PluginDir "Saved\StagedBuilds\$CookPlatform"

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

# A DLC cook that produces only a descriptor and a registry SUCCEEDS. The commonest cause is that the
# world is already in the base release, so every one of its packages is correctly treated as already
# cooked and there is nothing left to add. Packaging that would hand somebody a world that installs
# and then fails to load, so refuse here: an empty world package is worse than a failed cook, because
# it fails at the recipient instead of at the person who made it.
$CookedMap = Get-ChildItem -Path $Payload.FullName -Recurse -File -Filter '*.umap' -ErrorAction SilentlyContinue |
    Select-Object -First 1
$CookedAssets = @(Get-ChildItem -Path $Payload.FullName -Recurse -File -Include '*.uasset', '*.uexp' -ErrorAction SilentlyContinue)
if (-not $CookedMap -or $CookedAssets.Count -eq 0) {
    Write-Fail "`nThe cook produced no content for '$World' -- $($CookedAssets.Count) asset file(s), no level."
    Write-Fail "The world is almost certainly already part of release '$BasedOnRelease', so cooking it"
    Write-Fail "again as an addition to that release correctly yields nothing."
    Write-Fail ""
    Write-Fail "A world ships one way or the other, not both. Either cook a base that excludes it and"
    Write-Fail "package it against that, or leave it baked into the base and deliver the base."
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
    # A folder that is not a git checkout -- an engine installed from a launcher -- has no commit.
    # Windows PowerShell 5.1 turns git's complaint on stderr into a terminating error under 'Stop',
    # so it is let pass here and the exit code decides.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $h = (& git -C $Path log -1 --format=%H 2>$null) } finally { $ErrorActionPreference = $previous }
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
    # The distribution's release version, CARLA_VERSION in the top-level CMakeLists.txt.
    releaseVersion        = (@('MAJOR', 'MINOR', 'PATCH') | ForEach-Object { if ((Get-Content (Join-Path $CarlaRoot 'CMakeLists.txt') -Raw) -match "set\s*\(\s*CARLA_VERSION_$_\s+(\d+)") { $Matches[1] } }) -join '.'
    config                = $Config
    platform              = $Platform
    # Identification only. Never compared -- see the note above.
    carlaGitHash          = Get-GitHash $CarlaRoot
    contentGitHash        = Get-GitHash (Join-Path $ProjectDir 'Content\Carla')
    unrealGitHash         = Get-GitHash $UnrealEngineRoot
    # UTC to the millisecond, as PackageWorld.sh writes it.
    packagedAtUtc         = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$StagingCopy = Join-Path ([System.IO.Path]::GetTempPath()) "carla-world-$World-$PID"
if (Test-Path $StagingCopy) { Remove-Item -Recurse -Force $StagingCopy }
New-Item -ItemType Directory -Force -Path $StagingCopy | Out-Null
try {
    Copy-Item -Recurse -Force $Payload.FullName (Join-Path $StagingCopy $World)
    # UTF-8 without a byte-order mark, which Set-Content -Encoding UTF8 writes under Windows
    # PowerShell 5.1: a JSON reader need not accept one.
    [System.IO.File]::WriteAllText((Join-Path $StagingCopy 'world.json'), ($manifest | ConvertTo-Json),
                                   (New-Object System.Text.UTF8Encoding $false))

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
Write-Info "  $(Join-Path $PSScriptRoot 'InstallWorld.ps1') -Package '$ZipPath' -Into <package directory>"
