<#
.SYNOPSIS
    Install a world packaged by PackageWorld.ps1 into an existing CARLA package.

.DESCRIPTION
    Unpacks a world into the package's Plugins\GeneratedWorlds. The server discovers it on the next
    launch and it can be loaded by name.

    Before unpacking, the world's recorded build is checked against the package's own VERSION file. A
    world cooked against one build will not load against another -- cooked files carry package
    versions and name base content by id -- and the failure that would otherwise reach the user is an
    unexplained crash at load. Checking here turns that into a sentence.

.PARAMETER Package
    The .zip written by PackageWorld.ps1.

.PARAMETER Into
    Root of the CARLA package to install into: the directory holding CarlaUnreal\ and VERSION.

.PARAMETER Force
    Install even when the build check fails. For the case where you know two builds are compatible
    despite differing hashes -- a documentation-only commit, say. If the world then fails to load,
    this is why.

.EXAMPLE
    .\InstallWorld.ps1 -Package Build\WorldPackages\Arapahoe_I25.zip -Into D:\Carla-0.10.0-Win64
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory = $true)]
    [string]$Package,

    [Parameter(Mandatory = $true)]
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

if ($Help) {
    @'
InstallWorld.ps1 - install a packaged world into an existing CARLA package.

USAGE:
  .\InstallWorld.ps1 -Package <world.zip> -Into <package directory> [-Force]

The package directory is the one holding CarlaUnreal\ and VERSION.
-Force installs despite a build mismatch; the world may then fail to load.
'@ | Write-Host
    exit 0
}

if (-not (Test-Path $Package)) { Write-Fail "No such package: $Package"; exit 1 }
if (-not (Test-Path $Into))    { Write-Fail "No such directory: $Into";   exit 1 }

$PluginsDir = Join-Path $Into 'CarlaUnreal\Plugins\GeneratedWorlds'
$VersionFile = Join-Path $Into 'VERSION'
if (-not (Test-Path (Join-Path $Into 'CarlaUnreal'))) {
    Write-Fail "$Into does not look like a CARLA package (no CarlaUnreal\ inside)."
    exit 1
}

$Unpacked = Join-Path ([System.IO.Path]::GetTempPath()) "carla-install-$PID"
if (Test-Path $Unpacked) { Remove-Item -Recurse -Force $Unpacked }
try {
    Expand-Archive -Path $Package -DestinationPath $Unpacked

    $ManifestPath = Join-Path $Unpacked 'world.json'
    if (-not (Test-Path $ManifestPath)) {
        Write-Fail "$Package carries no world.json; it was not written by PackageWorld.ps1."
        exit 1
    }
    $m = Get-Content $ManifestPath -Raw | ConvertFrom-Json
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
    Write-Info "  .\Scripts\Windows\RunCarlaServer.ps1 -Map $($m.mapPackage)"
}
finally {
    if (Test-Path $Unpacked) { Remove-Item -Recurse -Force $Unpacked -ErrorAction SilentlyContinue }
}
