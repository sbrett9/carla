<#
.SYNOPSIS
    Install a world packaged by PackageWorld.ps1 into an existing CARLA package.

.DESCRIPTION
    Unpacks a world into the package's Plugins\GeneratedWorlds. The server discovers it on the next
    launch and it can be loaded by name.

    Before unpacking, the world interface version the world was packaged against (world.json in the
    zip) is checked against the one the package declares (CarlaUnreal\Config\DefaultWorldInterface.ini):
    the world installs where the package's Major equals the world's and the package's Minor is at
    least the world's. A world that needs content the package does not have will not load -- cooked
    files name base content by id -- and the failure that would otherwise reach the user is an
    unexplained crash at load. Checking here turns that into a sentence. The commits the world and the
    package record are shown to identify them, and never compared.

    A world.json without formatVersion is format 1. One that declares a newer format was written by a
    newer PackageWorld, and is refused whatever -Force says: its fields may not mean what this
    installer reads them as.

.PARAMETER Package
    The .zip written by PackageWorld.ps1, or a .tar.xz holding the same contents.

.PARAMETER Into
    The CARLA package to install into: a cooked package's root (the directory holding CarlaUnreal\
    and VERSION), or a CARLA distribution's root (the one holding CarlaServer\ and VERSION). Run from
    a distribution's world-tools folder, it defaults to that distribution.

.PARAMETER Force
    Install even when the world interface check fails: the package declares another Major, an older
    Minor, or no version at all. For the case where you know the package has everything the world
    needs despite its declaration. If the world then fails to load, this is why.

.EXAMPLE
    .\InstallWorld.ps1 -Package Build\WorldPackages\Arapahoe_I25.zip -Into D:\Carla-0.10.0-Win64

.EXAMPLE
    .\world-tools\InstallWorld.ps1 -Package D:\Downloads\Arapahoe_I25.zip
    Install into the distribution this script came with.
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory = $true)]
    [string]$Package,

    [string]$Into,

    [switch]$Force,

    [Alias('h')]
    [switch]$Help
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Info { param([Parameter(ValueFromPipeline)][string]$Message) Write-Host $Message -ForegroundColor Green }
function Write-Warn { param([Parameter(ValueFromPipeline)][string]$Message) Write-Host $Message -ForegroundColor Yellow }
function Write-Fail { param([Parameter(ValueFromPipeline)][string]$Message) Write-Host $Message -ForegroundColor Red }

# A level pack is the .zip that PackageWorld writes. The example packs under a distribution's
# Scenarios\ hold the same contents as a .tar.xz, about half the size. Windows' own tar.exe unpacks
# a .tar.xz on current builds. Where it cannot, Python's tarfile does: Python is already a
# prerequisite of the distribution.
function Expand-WorldPackage {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Destination)
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    if ($Path -match '\.zip$') {
        Expand-Archive -Path $Path -DestinationPath $Destination
        return
    }
    if ($Path -notmatch '\.(tar\.xz|txz)$') {
        Write-Fail "$Path is neither a .zip nor a .tar.xz level pack."
        exit 1
    }
    $tar = Join-Path $env:SystemRoot 'System32\tar.exe'
    if (Test-Path $tar) {
        & $tar -xf $Path -C $Destination 2>$null
        if ($LASTEXITCODE -eq 0) { return }
    }
    $unpack = "import sys, tarfile`n" +
              "with tarfile.open(sys.argv[1], 'r:xz') as pack:`n" +
              "    try: pack.extractall(sys.argv[2], filter='data')`n" +
              "    except TypeError: pack.extractall(sys.argv[2])"
    & python -c $unpack $Path $Destination
    if ($LASTEXITCODE -ne 0) {
        Write-Fail "Could not unpack $Path. Neither tar.exe nor Python could read it."
        exit 1
    }
}

if ($Help) {
    @'
InstallWorld.ps1 - install a packaged world into an existing CARLA package.

USAGE:
  .\InstallWorld.ps1 -Package <world.zip|world.tar.xz> -Into <package directory> [-Force]

The package directory is a cooked package's root (holding CarlaUnreal\ and VERSION) or a CARLA
distribution's root (holding CarlaServer\ and VERSION). Run from a distribution's world-tools
folder, -Into defaults to that distribution.
-Force installs despite a world interface version that does not allow it; the world may then fail
to load. A world.json of a newer format than this script reads is refused regardless.
'@ | Write-Host
    exit 0
}

if (-not (Test-Path $Package)) { Write-Fail "No such package: $Package"; exit 1 }

# Where the server's CarlaUnreal\ is. A cooked package holds it at its root beside VERSION; a
# distribution holds it under CarlaServer\, with VERSION at the distribution's root. Run from a
# distribution's world-tools folder with no -Into, the distribution it came with is the one.
if (-not $Into) {
    $Enclosing = Join-Path $PSScriptRoot '..'
    if (-not (Test-Path (Join-Path $Enclosing 'CarlaServer\CarlaUnreal'))) {
        Write-Fail "-Into is required: the CARLA package or distribution to install the world into."
        exit 1
    }
    $Into = $Enclosing
}
if (-not (Test-Path $Into)) { Write-Fail "No such directory: $Into"; exit 1 }
$Into = (Resolve-Path $Into).Path
$VersionFile = Join-Path $Into 'VERSION'
if (Test-Path (Join-Path $Into 'CarlaServer\CarlaUnreal')) {
    $Into = Join-Path $Into 'CarlaServer'
}
$PluginsDir = Join-Path $Into 'CarlaUnreal\Plugins\GeneratedWorlds'
if (-not (Test-Path (Join-Path $Into 'CarlaUnreal'))) {
    Write-Fail "$Into does not look like a CARLA package (no CarlaUnreal\ inside)."
    exit 1
}
if (-not (Test-Path $VersionFile)) {
    # Named the distribution's CarlaServer\ itself: its VERSION is one folder up.
    $VersionFile = Join-Path (Split-Path $Into -Parent) 'VERSION'
}
# A distribution starts its server with run-server.ps1, beside its VERSION.
$RunServer = Join-Path (Split-Path $VersionFile -Parent) 'run-server.ps1'

$Unpacked = Join-Path ([System.IO.Path]::GetTempPath()) "carla-install-$PID"
if (Test-Path $Unpacked) { Remove-Item -Recurse -Force $Unpacked }
try {
    Expand-WorldPackage -Path $Package -Destination $Unpacked

    $ManifestPath = Join-Path $Unpacked 'world.json'
    if (-not (Test-Path $ManifestPath)) {
        Write-Fail "$Package carries no world.json; it was not written by PackageWorld.ps1."
        exit 1
    }
    $m = Get-Content $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json

    # The manifest's own format, read before anything else in it. One without formatVersion is
    # format 1; a newer one was written by a newer PackageWorld, whose fields this script may read
    # wrongly, so it is refused rather than read in part. -Force does not override this.
    $SupportedFormatVersion = 1
    $declared = $m.PSObject.Properties['formatVersion']
    $formatVersion = if ($null -eq $declared -or $null -eq $declared.Value) { 1 } else { $declared.Value }
    if (-not ($formatVersion -is [int] -or $formatVersion -is [long]) -or $formatVersion -lt 1) {
        Write-Fail "$Package declares formatVersion '$formatVersion' in world.json, which is not a format version."
        exit 1
    }
    if ($formatVersion -gt $SupportedFormatVersion) {
        Write-Fail "$Package declares formatVersion $formatVersion in world.json, and this InstallWorld reads formatVersion $SupportedFormatVersion and earlier."
        Write-Fail "It was packaged by a newer release; install it with that release's InstallWorld."
        exit 1
    }
    $WorldDir = Join-Path $Unpacked $m.world
    if (-not (Test-Path $WorldDir)) {
        Write-Fail "$Package says it holds '$($m.world)' but does not contain it."
        exit 1
    }

    Write-Info "world   : $($m.world)"
    Write-Info "needs   : world interface $($m.worldInterfaceMajor).x, minor $($m.worldInterfaceMinor) or later"

    # What this package promises, read from the package itself rather than from anything derived.
    # A version says what a build supports; a hash could only say whether two builds are identical,
    # which refuses compatible pairs and still cannot confirm an incompatible one.
    $InterfaceIni = Join-Path $Into 'CarlaUnreal\Config\DefaultWorldInterface.ini'
    $baseMajor = $null; $baseMinor = $null
    if (Test-Path $InterfaceIni) {
        $text = Get-Content $InterfaceIni -Raw
        if ($text -match '(?ms)^\s*\[WorldInterface\](.*?)(^\s*\[|\z)') {
            $body = $Matches[1]
            if ($body -match '(?m)^\s*Major\s*=\s*(\d+)') { $baseMajor = [int]$Matches[1] }
            if ($body -match '(?m)^\s*Minor\s*=\s*(\d+)') { $baseMinor = [int]$Matches[1] }
        }
    }

    $problems = @()
    if ($null -eq $baseMajor -or $null -eq $baseMinor) {
        $problems += "this package does not declare a world interface version, so what it supports is unknown"
    }
    else {
        Write-Info "package : world interface $baseMajor.$baseMinor"
        # Major is the break; minor is additive, so the base may run ahead but not behind.
        if ($baseMajor -ne $m.worldInterfaceMajor) {
            $problems += "this package is world interface $baseMajor.x, the world needs $($m.worldInterfaceMajor).x"
        }
        elseif ($baseMinor -lt $m.worldInterfaceMinor) {
            $problems += "this package is minor $baseMinor, the world needs $($m.worldInterfaceMinor) or later"
        }
    }
    if ($problems.Count -gt 0) {
        Write-Fail "`nThis world was not built for this package:"
        foreach ($p in $problems) { Write-Fail "  - $p" }
        # Identification, so both sides can be named when someone has to work out which is wrong.
        if ($m.carlaGitHash) {
            Write-Fail "  world  built from Carla commit $($m.carlaGitHash.Substring(0, [Math]::Min(9, $m.carlaGitHash.Length)))"
        }
        if (Test-Path $VersionFile) {
            $line = (Get-Content $VersionFile | Where-Object { $_ -match 'Carla git hash' } | Select-Object -First 1)
            if ($line) { Write-Fail "  package $($line.Trim())" }
        }
        Write-Fail "`nInstalling it anyway would most likely fail to load rather than misbehave subtly."
        if (-not $Force) {
            Write-Fail "Re-package the world against this build, or pass -Force if you know they are compatible."
            exit 1
        }
        Write-Warn "`n-Force given; installing regardless."
    }

    New-Item -ItemType Directory -Force -Path $PluginsDir | Out-Null
    $Target = Join-Path $PluginsDir $m.world
    if (Test-Path $Target) {
        Write-Warn "Replacing the copy of '$($m.world)' already installed."
        Remove-Item -Recurse -Force $Target
    }
    Copy-Item -Recurse -Force $WorldDir $Target

    Write-Info "`nInstalled $($m.world)"
    Write-Info "  into  : $Target"
    Write-Info "`nLoad it with:"
    if (Test-Path $RunServer) {
        Write-Info "  $RunServer $($m.mapPackage)"
    } else {
        Write-Info "  .\Scripts\Windows\RunCarlaServer.ps1 -Map $($m.mapPackage)"
    }
}
finally {
    if (Test-Path $Unpacked) { Remove-Item -Recurse -Force $Unpacked -ErrorAction SilentlyContinue }
}
