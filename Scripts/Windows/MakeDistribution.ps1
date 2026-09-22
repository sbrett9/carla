#Requires -Version 5.1
<#
.SYNOPSIS
    Assemble a self-contained CARLA Windows distribution (Build\Dist\<name>.zip).

.DESCRIPTION
    Windows peer of Scripts\Linux\MakeDistribution.sh. Produces
    Build\Dist\Carla-<version>-Win64-<config>\  (and a matching .zip) containing everything
    needed to run the digital-twin single-client traffic-manager demo on another Windows machine:

      CarlaServer\   the cooked CARLA server (the packaged game; run with CarlaUnreal.exe)
      wheels\        the carlanet + carlacontrol Python wheels (install into a venv)
      scripts\       run_SCTMV.py (the demo client; imports carlanet + carlacontrol)
      osm\           the example OpenStreetMap maps the demo can build worlds from
      tools\sumo\    the SUMO toolchain: netconvert, sumo, duarouter, the DLLs they import,
                     SUMO's typemap/xsd data, its traci/sumolib modules, and PROJ data
      skills\        this repository's own authoring skills, describing how to build scenarios for a
                     generated world. The vendored third-party skills are developer aids and stay out
      licenses\      the licence text of every third-party component in the bundle
      MANIFEST.md    what is in here, where it came from and under what terms (generated)
      setup-venv.ps1 / run-server.ps1 / run-sctmv.ps1 / README.md

    Run AFTER the build + cook have produced the artifacts:
      .\Scripts\Windows\BuildCarla.ps1                              # editor + carlanet wheel
      cmake --build Build --target package-development              # cook + stage the server
    then:
      .\Scripts\Windows\MakeDistribution.ps1                       # assemble the bundle
    or pass -Build to run those steps first:
      .\Scripts\Windows\MakeDistribution.ps1 -Build -Config Development

    The SUMO libraries are an explicit list read from what the binaries import, the Windows peer of
    the Linux script's ldd walk: the build directory holds release and debug variants of every
    library in the SUMOLibraries bundle, and shipping the lot means a licence obligation for each.
    The assembled Build\Dist\<name>\ folder is runnable in place; the .zip is only for shipping to
    another machine -- pass -SkipArchive to skip it during local test iterations.

.PARAMETER Config
    Build configuration: Development (default), Shipping, or Debug. Selects the cooked package and
    the cmake package target (package-development / package-shipping / package-debug).

.PARAMETER Build
    Run BuildCarla.ps1 (editor + carlanet wheel) and the cook/stage (cmake package target) before
    assembling. BuildCarla.ps1 activates the Visual Studio toolchain in this process, which the cook
    then inherits. Without -Build, assembles from already-built artifacts.

.PARAMETER SkipArchive
    Assemble the Build\Dist\<name>\ folder but do NOT create the .zip. The folder is runnable in
    place; skipping the multi-GB compression makes local test iterations much faster.

.PARAMETER UnrealEngineRoot
    UE 5.7.4 source-build root, forwarded to BuildCarla.ps1 under -Build.
    Env: CARLA_UNREAL_ENGINE_PATH. Default: <repo-parent>\UE_5_7_4.

.PARAMETER MaxParallelActions
    Under -Build, forwarded to BuildCarla.ps1 to cap the editor build's parallel actions. Omit to use
    BuildCarla's default (4); pass 0 to uncap (UBT auto-scales to CPU/RAM), or e.g. 16 to widen it.
    (The cook's own game-target build is already run at UBT's default width.)

.EXAMPLE
    .\MakeDistribution.ps1
    Assemble the bundle from an already-cooked Development package.

.EXAMPLE
    .\MakeDistribution.ps1 -Build
    Build the editor + wheel, cook + stage the server, then assemble.

.EXAMPLE
    .\MakeDistribution.ps1 -Build -SkipArchive
    Full build + cook + assemble into Build\Dist\<name>\, but skip the slow .zip (run it in place
    for local debugging).
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [ValidateSet('Development', 'Shipping', 'Debug')]
    [string]$Config = 'Development',
    [switch]$Build,
    [switch]$SkipArchive,
    [string]$UnrealEngineRoot,
    [int]$MaxParallelActions = -1, # under -Build, forward to BuildCarla.ps1; -1 = use its default

    [Alias('h')]
    [switch]$Help,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Remaining
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Show-Usage {
    @'
MakeDistribution.ps1 - assemble a self-contained CARLA Windows distribution.

USAGE:
  .\MakeDistribution.ps1 [options]

OPTIONS (PowerShell-native | legacy alias):
  -Config <Development|Shipping|Debug>  --config=<...>   Build configuration (default Development).
  -Build                                --build          Build editor + wheel and cook/stage first.
  -SkipArchive                          --skip-archive   Assemble the folder but skip the .zip.
  -UnrealEngineRoot <dir>               --unreal-engine-root=<dir>  UE 5.7.4 root (for -Build).
  -MaxParallelActions <n>               --max-parallel-actions=<n>  Under -Build, cap the editor build's parallel actions (0 = uncapped).
  -Help                          / -h   --help           Show this help.

EXAMPLES:
  .\MakeDistribution.ps1 -Build
  .\MakeDistribution.ps1 -Build -SkipArchive
'@ | Write-Host
}

# -- Normalize legacy "--flag" / "--flag=value" arguments (matches BuildCarla.ps1) ----------
if ($Remaining) {
    for ($idx = 0; $idx -lt $Remaining.Count; $idx++) {
        $arg = $Remaining[$idx]
        if ($arg -match '^(--[^=]+)=(.*)$') { $key = $matches[1]; $val = $matches[2] }
        else { $key = $arg; $val = $null }
        if ($null -ne $val) { $next = $val }
        elseif ($idx + 1 -lt $Remaining.Count) { $next = $Remaining[$idx + 1] }
        else { $next = $null }
        switch -Regex ($key) {
            '^(--help|/\?|help)$'        { $Help = $true }
            '^(--build)$'                { $Build = $true }
            '^(--skip-archive)$'         { $SkipArchive = $true }
            '^(--config)$'               { if ($null -eq $next) { throw "Argument '$key' requires a value." } $Config = $next; if ($null -eq $val) { $idx++ } }
            '^(--unreal-engine-root|--ue-root)$' { if ($null -eq $next) { throw "Argument '$key' requires a value." } $UnrealEngineRoot = $next; if ($null -eq $val) { $idx++ } }
            '^(--max-parallel-actions|--max-parallel)$' { if ($null -eq $next) { throw "Argument '$key' requires a value." } $MaxParallelActions = [int]$next; if ($null -eq $val) { $idx++ } }
            default { Show-Usage; throw "Unknown argument '$arg'." }
        }
    }
}

if ($Help) { Show-Usage; return }
if ($Config -notin @('Development', 'Shipping', 'Debug')) {
    throw "Invalid -Config '$Config'. Expected Development, Shipping, or Debug."
}

# ── Console colour convention (matches BuildCarla.ps1): green = info, red = failure. ─────────
function Write-Info { param([Parameter(ValueFromPipeline)][string]$Message) Write-Host $Message -ForegroundColor Green }
function Write-Fail { param([Parameter(ValueFromPipeline)][string]$Message) Write-Host $Message -ForegroundColor Red }

# ── What a binary imports, read from its own PE import table ─────────────────────────────────
# The Linux peer walks ldd to bundle exactly the libraries its binaries load; this is the Windows
# equivalent, and it is deliberately not dumpbin: dumpbin needs a Visual Studio developer
# environment, which this script does not have unless it was invoked with -Build. Reading the header
# directly needs nothing but the file. Returns the imported module names, or an empty list for
# anything that is not a PE image.
function Get-ImportedDllName {
    param([Parameter(Mandatory)][string]$Path)
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 64 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) { return @() }     # "MZ"
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
    if ($peOffset -le 0 -or $peOffset + 24 -ge $bytes.Length) { return @() }
    if ([BitConverter]::ToUInt32($bytes, $peOffset) -ne 0x00004550) { return @() }             # "PE\0\0"

    $coff               = $peOffset + 4
    $sectionCount       = [BitConverter]::ToUInt16($bytes, $coff + 2)
    $optionalHeaderSize = [BitConverter]::ToUInt16($bytes, $coff + 16)
    $optional           = $coff + 20
    # The data directories follow the optional header's fixed part: 96 bytes for PE32, 112 for PE32+.
    $magic            = [BitConverter]::ToUInt16($bytes, $optional)
    $dataDirectories  = $optional + $(if ($magic -eq 0x20B) { 112 } else { 96 })
    $importRva        = [BitConverter]::ToUInt32($bytes, $dataDirectories + 8)   # directory 1: imports
    if ($importRva -eq 0) { return @() }

    $sections = @()
    $sectionTable = $optional + $optionalHeaderSize
    for ($i = 0; $i -lt $sectionCount; $i++) {
        $entry = $sectionTable + ($i * 40)
        $sections += [pscustomobject]@{
            VirtualAddress = [BitConverter]::ToUInt32($bytes, $entry + 12)
            VirtualSize    = [BitConverter]::ToUInt32($bytes, $entry + 8)
            RawSize        = [BitConverter]::ToUInt32($bytes, $entry + 16)
            RawPointer     = [BitConverter]::ToUInt32($bytes, $entry + 20)
        }
    }
    # An address in the loaded image maps back to a file offset through the section that contains it.
    $toFileOffset = {
        param([uint32]$Rva)
        foreach ($s in $sections) {
            $span = [Math]::Max($s.VirtualSize, $s.RawSize)
            if ($Rva -ge $s.VirtualAddress -and $Rva -lt $s.VirtualAddress + $span) {
                return [int]($Rva - $s.VirtualAddress + $s.RawPointer)
            }
        }
        return -1
    }

    $names = @()
    $descriptor = & $toFileOffset $importRva
    if ($descriptor -lt 0) { return @() }
    # Import descriptors are 20 bytes each and the table ends at an all-zero one; the module's name
    # is at offset 12, as an address to a null-terminated string.
    while ($descriptor + 20 -le $bytes.Length) {
        $nameRva = [BitConverter]::ToUInt32($bytes, $descriptor + 12)
        if ($nameRva -eq 0) { break }
        $nameOffset = & $toFileOffset $nameRva
        if ($nameOffset -lt 0) { break }
        $end = $nameOffset
        while ($end -lt $bytes.Length -and $bytes[$end] -ne 0) { $end++ }
        $names += [System.Text.Encoding]::ASCII.GetString($bytes, $nameOffset, $end - $nameOffset)
        $descriptor += 20
    }
    return $names
}

# ── Paths: CARLA repo root is two dirs up from this script (carla\Scripts\Windows), derived by
# location so it survives a renamed/relocated checkout. ──────────────────────────────────────
$CarlaRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$BuildDir  = Join-Path $CarlaRoot 'Build'

# Map the configuration to the cmake package target (identical scheme to MakeDistribution.sh).
$cmakeTarget = 'package-' + $Config.ToLowerInvariant()

Write-Info "CARLA repo : $CarlaRoot"
Write-Info "Config     : $Config  (cmake target: $cmakeTarget)"

# ============================================================================
#  Optional build + cook (-Build). BuildCarla.ps1 activates the VS toolchain in
#  THIS process (process-scope env vars), so the subsequent cmake cook inherits it.
# ============================================================================
if ($Build) {
    $buildCarla = Join-Path $PSScriptRoot 'BuildCarla.ps1'
    if (-not (Test-Path $buildCarla)) { throw "BuildCarla.ps1 not found beside this script: $buildCarla" }

    Write-Info "`n[dist] building editor + carlanet wheel (BuildCarla.ps1)"
    $bcArgs = @{}
    if ($UnrealEngineRoot) { $bcArgs['UnrealEngineRoot'] = $UnrealEngineRoot }
    if ($MaxParallelActions -ge 0) { $bcArgs['MaxParallelActions'] = $MaxParallelActions }
    & $buildCarla @bcArgs
    if ($LASTEXITCODE -ne 0) { throw "BuildCarla.ps1 failed (exit $LASTEXITCODE); aborting distribution." }

    # Skip the cmake package target's own Compress.cmake step (CARLA_UNREAL_PACKAGE_NO_COMPRESSION):
    # it single-threaded-zips the whole package under Build\Package and looks like a hang after
    # "BUILD SUCCESSFUL"; this script assembles the richer bundle (game + wheel + scripts + osm +
    # netconvert) and zips that once below. The reconfigure is quick.
    Write-Info "[dist] configuring package target to skip its redundant compress"
    & cmake -DCARLA_UNREAL_PACKAGE_NO_COMPRESSION=ON -S "$CarlaRoot" -B "$BuildDir"
    if ($LASTEXITCODE -ne 0) { throw "cmake reconfigure failed (exit $LASTEXITCODE)." }

    Write-Info "[dist] cooking + staging the server (cmake --build Build --target $cmakeTarget)"
    & cmake --build "$BuildDir" --target "$cmakeTarget"
    if ($LASTEXITCODE -ne 0) { throw "cook/stage failed (exit $LASTEXITCODE)." }
}

# ============================================================================
#  Locate the cooked package. Prefer the archived copy; fall back to the staging
#  tree if the archive step was interrupted (the staged tree is equally runnable).
#  On Windows the package dir is Carla-<ver>-Win64-<config>\ with a Windows\ subdir
#  containing CarlaUnreal.exe.
# ============================================================================
$pkgServer = $null      # the platform dir holding CarlaUnreal.exe + Engine\ + CarlaUnreal\
$pkgName   = $null      # e.g. Carla-0.10.0-Win64-Development
foreach ($base in @((Join-Path $BuildDir 'Package'),
                    (Join-Path $BuildDir 'Package\StagedBuilds'))) {
    if (-not (Test-Path $base)) { continue }
    $cand = Get-ChildItem -Path $base -Directory -Filter "Carla-*-Win64-$Config" -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending
    foreach ($c in $cand) {
        # The launcher lives at <pkg>\Windows\CarlaUnreal.exe; accept any single-level platform
        # subdir to stay robust if UE renames it.
        $platform = Join-Path $c.FullName 'Windows'
        if (Test-Path (Join-Path $platform 'CarlaUnreal.exe')) {
            $pkgServer = $platform; $pkgName = $c.Name; break
        }
        $alt = Get-ChildItem -Path $c.FullName -Directory -ErrorAction SilentlyContinue |
               Where-Object { Test-Path (Join-Path $_.FullName 'CarlaUnreal.exe') } | Select-Object -First 1
        if ($alt) { $pkgServer = $alt.FullName; $pkgName = $c.Name; break }
    }
    if ($pkgServer) { break }
}
if (-not $pkgServer) {
    Write-Fail "ERROR: no cooked $Config package found under Build\Package (expected Carla-*-Win64-$Config\Windows\CarlaUnreal.exe)."
    Write-Fail "       Run: cmake --build Build --target $cmakeTarget    (or pass -Build)"
    exit 1
}
Write-Info "[dist] using cooked package: $pkgServer"

$dist = Join-Path $BuildDir "Dist\$pkgName"
Write-Info "[dist] staging into $dist"
if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
foreach ($d in 'CarlaServer', 'wheels', 'scripts', 'osm', 'tools\sumo', 'licenses') {
    New-Item -ItemType Directory -Force -Path (Join-Path $dist $d) | Out-Null
}

# ── Component and licence inventory ──────────────────────────────────────────────────────────
# The distribution used to ship no LICENSE, no NOTICE and no third-party listing of any kind while
# redistributing a few dozen native libraries under nine or more licences. It now carries a
# MANIFEST.md and a licenses\ directory, both GENERATED FROM WHAT THIS SCRIPT ACTUALLY COPIES: each
# staging step below records its own rows, so the inventory cannot describe a bundle other than the
# one on disk. A hand-maintained list is wrong the first time a slot changes.
$manifestRows = [System.Collections.Generic.List[object]]::new()
function Add-ManifestRow {
    param([Parameter(Mandatory)][string]$Component, [Parameter(Mandatory)][string]$Provenance,
          [Parameter(Mandatory)][string]$License, [Parameter(Mandatory)][string]$Location)
    $manifestRows.Add([pscustomobject]@{ Component = $Component; Provenance = $Provenance
                                         License = $License; Location = $Location })
}

$licenseDir = Join-Path $dist 'licenses'
# Copies a licence text into licenses\ and returns the name it was filed under, or $null when the
# source is absent -- in which case the manifest says the text is missing rather than staying quiet.
function Copy-LicenseText {
    param([Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$Name)
    if (-not (Test-Path $Source)) {
        Write-Fail "[dist] WARNING: licence text not found at $Source; MANIFEST.md will record it as missing."
        return $null
    }
    Copy-Item -Force $Source (Join-Path $licenseDir $Name)
    return $Name
}

# 1. Cooked server (contents of the platform dir: CarlaUnreal.exe, Engine\, CarlaUnreal\).
Write-Info "[dist] copying cooked server (this is the large step)..."
Copy-Item -Recurse -Force -Path (Join-Path $pkgServer '*') -Destination (Join-Path $dist 'CarlaServer')

# 1b. What this build is. The cook writes VERSION at the archive root, one level above the platform
# directory copied above, so without this the distribution -- the thing actually handed to someone --
# carries no statement of which CARLA it is or which worlds it accepts.
$versionSrc = Join-Path (Split-Path $pkgServer -Parent) 'VERSION'
if (Test-Path $versionSrc) {
    Copy-Item -Force $versionSrc (Join-Path $dist 'VERSION')
    Write-Info "[dist] VERSION: $((Get-Content $versionSrc | Select-Object -First 2) -join '; ')"
} else {
    Write-Fail "[dist] WARNING: no VERSION at $versionSrc; the distribution will not state its build."
}

# 2. Python client wheels (newest of each): carlanet (the .NET bridge) and carlacontrol (the
#    run_SCTMV client package). carlacontrol depends on carlanet, so both must be bundled.
#    A missing wheel is fatal rather than a warning: the distribution cannot install itself without
#    it, and a warning buried in a long cook log is how a broken bundle shipped before.
function Copy-NewestWheel {
    param([Parameter(Mandatory)][string]$SourceDir)
    $wheel = Get-ChildItem (Join-Path $SourceDir '*.whl') -ErrorAction SilentlyContinue |
             Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $wheel) {
        throw "no wheel under $SourceDir (run BuildCarla.ps1). A distribution missing a wheel cannot install itself."
    }
    Copy-Item -Force $wheel.FullName (Join-Path $dist 'wheels')
    Write-Info "[dist] wheel: $($wheel.Name)"
    return $wheel.Name
}
$carlanetWheel     = Copy-NewestWheel (Join-Path $CarlaRoot 'CarlaNet\python\dist')
$carlacontrolWheel = Copy-NewestWheel (Join-Path $CarlaRoot 'CarlaControl\dist')
Add-ManifestRow -Component 'carlanet (CARLA .NET client)' -Provenance "built from this repository, $carlanetWheel" `
                -License 'MIT (licenses\CARLA-LICENSE.txt)' -Location 'wheels\'
Add-ManifestRow -Component 'carlacontrol (world building, scenarios, telemetry)' `
                -Provenance "built from this repository, $carlacontrolWheel" `
                -License 'Sierra Nevada Corporation (licenses\CarlaControl-LICENSE.txt)' -Location 'wheels\'

# 3. Demo client. run_SCTMV.py imports carlanet + carlacontrol (both installed from wheels\ above);
#    it has no sibling-file imports -- it clips OSM through carlacontrol.OsmClipper from the wheel,
#    which is why no clipper script is copied beside it -- and reads its netconvert, PROJ and SUMO
#    paths from the environment run-sctmv.ps1 sets.
$demoClient = Join-Path $CarlaRoot 'CarlaControl\scripts\run_SCTMV.py'
if (-not (Test-Path $demoClient)) { throw "demo client not found at $demoClient" }
Copy-Item -Force $demoClient (Join-Path $dist 'scripts')

# 3b. The authoring skills: the reference bundles that describe how to build scenarios against a
#     world this distribution generates. They travel with the tools so the description and the tool
#     are always the same version.
#
#     CarlaControl\skills\third-party\ is skipped. It holds vendored copies of somebody else's
#     skills -- Unreal Engine C++ reference, for developers working on this repository -- which a
#     distribution recipient has no use for, and shipping them would attach a third-party
#     attribution obligation to the package. It would also make the manifest row below false: that
#     row states one provenance and one licence for the whole skills\ slot.
$skillsSrc = Join-Path $CarlaRoot 'CarlaControl\skills'
$thirdPartySkills = 'third-party'
if (Test-Path $skillsSrc) {
    $skillItems = @(Get-ChildItem $skillsSrc | Where-Object { $_.Name -ne $thirdPartySkills })
    New-Item -ItemType Directory -Force -Path (Join-Path $dist 'skills') | Out-Null
    foreach ($item in $skillItems) {
        Copy-Item -Recurse -Force -Path $item.FullName -Destination (Join-Path $dist 'skills')
    }
    $skillNames = @($skillItems | Where-Object { $_.PSIsContainer } | ForEach-Object { $_.Name })
    if ($skillNames.Count -eq 0) { Write-Warning "no first-party authoring skills under $skillsSrc" }
    Write-Info "[dist] skills: $($skillNames -join ', ')"
    Add-ManifestRow -Component "authoring skills ($($skillNames -join ', '))" `
                    -Provenance 'built from this repository, CarlaControl\skills\' `
                    -License 'Sierra Nevada Corporation (licenses\CarlaControl-LICENSE.txt)' -Location 'skills\'
} else { Write-Warning "no authoring skills under $skillsSrc" }

# 4. Example OSM maps. These are OpenStreetMap extracts, so they and every .xodr derived from them
#    carry the Open Database License; MANIFEST.md names the files that actually shipped.
$osm = @(Get-ChildItem (Join-Path $CarlaRoot 'Import\*.osm') -ErrorAction SilentlyContinue)
if ($osm.Count -gt 0) {
    Copy-Item -Force ($osm | ForEach-Object { $_.FullName }) (Join-Path $dist 'osm')
    Add-ManifestRow -Component 'OpenStreetMap extracts' `
                    -Provenance "openstreetmap.org contributors: $(($osm | ForEach-Object { $_.Name }) -join ', ')" `
                    -License 'ODbL 1.0 (licenses\OpenStreetMap-ODbL-NOTICE.txt)' -Location 'osm\'
} else { Write-Warning "no .osm files under Import\" }

# 5. The SUMO toolchain: the binaries, the runtime DLLs they actually import, the named data/ and
#    tools/ subsets, and PROJ's data. CarlaNet talks to `sumo` over the TraCI wire protocol from
#    managed code that ships in the carlanet wheel, so there is nothing native to bundle for it.
$sumoInstall = Join-Path $BuildDir 'sumo-install'
$sumoBin     = Join-Path $sumoInstall 'bin'
$sumoDest    = Join-Path $dist 'tools\sumo'
$sumoExecutables = @('netconvert.exe', 'sumo.exe', 'duarouter.exe')
$sumoNativeStaged = @()   # the DLLs actually copied; the licence inventory covers exactly these
$sumoVersion = 'unknown'
$nc = Join-Path $sumoBin 'netconvert.exe'
if (Test-Path $nc) {
    foreach ($binary in $sumoExecutables) {
        $src = Join-Path $sumoBin $binary
        if (Test-Path $src) { Copy-Item -Force $src $sumoDest }
        else { Write-Warning "$binary is missing from $sumoBin (run CarlaSetup.ps1 to build the whole toolchain)" }
    }

    # Runtime DLLs: an explicit list derived from what the binaries import, not a bin\*.dll glob.
    # The glob shipped every DLL the SUMOLibraries bundle left in the build directory -- release and
    # debug variants of the same library, and libraries nothing here loads -- each of which would
    # need a row in the licence inventory below whether or not anything used it.
    # The imports are read straight out of the PE header rather than through dumpbin, so this works
    # without a Visual Studio developer environment (the cook does not activate one). Measured on the
    # pinned toolchain: none of these binaries has a delay-load import directory, so the plain import
    # table is the whole dependency set. Anything not sitting in sumo-install\bin is a system library
    # the target machine supplies, and is skipped by never being found there.
    $needed = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $pending = [System.Collections.Generic.Queue[string]]::new()
    foreach ($binary in $sumoExecutables) {
        $src = Join-Path $sumoBin $binary
        if (Test-Path $src) { $pending.Enqueue($src) }
    }
    while ($pending.Count -gt 0) {
        foreach ($imported in Get-ImportedDllName -Path $pending.Dequeue()) {
            $candidate = Join-Path $sumoBin $imported
            if ((Test-Path $candidate) -and $needed.Add($imported)) { $pending.Enqueue($candidate) }
        }
    }
    foreach ($library in ($needed | Sort-Object)) {
        Copy-Item -Force (Join-Path $sumoBin $library) $sumoDest
        $sumoNativeStaged += $library
    }
    Write-Info "[dist] bundled $($sumoNativeStaged.Count) runtime DLLs the SUMO binaries import (of $(@(Get-ChildItem "$sumoBin\*.dll").Count) present)"

    # The named data/ and tools/ subsets, so tools\sumo is a usable SUMO_HOME on the target.
    foreach ($subset in @(@{ Kind = 'data'; Items = @('typemap', 'xsd') },
                          @{ Kind = 'tools'; Items = @('traci', 'sumolib') })) {
        foreach ($item in $subset.Items) {
            $src = Join-Path $sumoInstall "$($subset.Kind)\$item"
            if (-not (Test-Path $src)) {
                Write-Warning "$($subset.Kind)\$item is missing from $sumoInstall (run CarlaSetup.ps1)"
                continue
            }
            $dstParent = Join-Path $sumoDest $subset.Kind
            New-Item -ItemType Directory -Force -Path $dstParent | Out-Null
            Copy-Item -Recurse -Force $src (Join-Path $dstParent $item)
        }
    }

    $projSrc = Join-Path $sumoInstall 'share\proj'
    if (Test-Path (Join-Path $projSrc 'proj.db')) {
        New-Item -ItemType Directory -Force -Path (Join-Path $sumoDest 'proj') | Out-Null
        Copy-Item -Recurse -Force -Path (Join-Path $projSrc '*') -Destination (Join-Path $sumoDest 'proj')
    } else {
        Write-Warning "proj.db not found under $projSrc; OSM geo-referencing may fail on the target"
    }

    # Run each staged binary from the staged directory. Windows resolves a binary's DLLs from its own
    # folder first, so this is a direct check that the explicit DLL list above is sufficient -- the
    # one way the list can be wrong is by being short, and this is what would say so.
    foreach ($binary in $sumoExecutables) {
        $staged = Join-Path $sumoDest $binary
        if (-not (Test-Path $staged)) { continue }
        # Collect the whole output before reading the exit code: stopping the pipeline early (with
        # Select-Object -First) can end it before the native command's status is recorded, which
        # under Set-StrictMode leaves $LASTEXITCODE unset and throws on the next read.
        $output   = @(& $staged --version 2>&1)
        $exitCode = $LASTEXITCODE
        $reported = if ($output.Count -gt 0) { "$($output[0])" } else { '(no output)' }
        if ($exitCode -ne 0) {
            throw "$binary does not run from the staged bundle (exit $exitCode): $reported. The bundled DLL set is incomplete."
        }
        Write-Info "[dist] staged $binary : $reported"
        if ($binary -eq 'netconvert.exe' -and "$reported" -match 'Eclipse SUMO \S+ v?(\d+(?:\.\d+)*)') {
            $sumoVersion = $matches[1]
        }
    }
} else {
    Write-Warning "netconvert.exe not found at $nc (run CarlaSetup.ps1); OSM->OpenDRIVE unavailable"
}

# ============================================================================
#  6. Licence texts, and the inventory rows for everything staged above.
# ============================================================================
# CARLA's own licence, and CarlaControl's as it is carried in the repository.
$carlaLicense = Copy-LicenseText (Join-Path $CarlaRoot 'LICENSE') 'CARLA-LICENSE.txt'
Copy-LicenseText (Join-Path $CarlaRoot 'CarlaControl\LICENSE') 'CarlaControl-LICENSE.txt' | Out-Null
Add-ManifestRow -Component 'CARLA server (cooked)' -Provenance 'built from this repository; see VERSION' `
                -License "MIT (licenses\$carlaLicense)" -Location 'CarlaServer\'

# OpenStreetMap's terms are not a file in this tree, so the notice is written rather than copied; it
# states the obligation and where the licence text lives, for the extracts and for every .xodr
# derived from them, which are a Derivative Database under the same terms.
@'
OpenStreetMap data and works derived from it
============================================

The .osm extracts under osm\, and every OpenDRIVE (.xodr) road network this distribution generates
from one, are derived from OpenStreetMap.

  (c) OpenStreetMap contributors, available under the Open Database License (ODbL) v1.0.
  Licence text: https://opendatacommons.org/licenses/odbl/1-0/
  Attribution:  https://www.openstreetmap.org/copyright

A generated road network is a Derivative Database under that licence. Anything published from it
must carry the attribution above.
'@ | Set-Content -Path (Join-Path $licenseDir 'OpenStreetMap-ODbL-NOTICE.txt') -Encoding UTF8

if ($sumoNativeStaged.Count -gt 0 -or (Test-Path (Join-Path $sumoDest 'netconvert.exe'))) {
    # SUMO itself: Eclipse Public License 2.0, which carries a source offer. The offer cites the
    # commit CarlaSetup.ps1 pins rather than repeating it, so the two cannot drift apart.
    $sumoPin = 'unrecorded'
    $setupText = Get-Content (Join-Path $CarlaRoot 'CarlaSetup.ps1') -Raw -ErrorAction SilentlyContinue
    if ($setupText -match "\`$sumoSrcPin\s*=\s*'([0-9a-f]{7,40})'") { $sumoPin = $matches[1] }
    Copy-LicenseText (Join-Path $BuildDir 'sumo-src\LICENSE') 'SUMO-LICENSE.txt' | Out-Null
    Copy-LicenseText (Join-Path $BuildDir 'sumo-src\NOTICE.md') 'SUMO-NOTICE.md' | Out-Null
    Add-ManifestRow -Component "Eclipse SUMO $sumoVersion (netconvert, sumo, duarouter)" `
                    -Provenance "github.com/eclipse-sumo/sumo at $sumoPin; source available from that commit" `
                    -License 'EPL-2.0 (licenses\SUMO-LICENSE.txt, licenses\SUMO-NOTICE.md)' -Location 'tools\sumo\'
    # data/ and tools/ are redistributed as SOURCE, not as binaries: the traci and sumolib modules are
    # EPL-2.0 Python files carrying SUMO's own headers. They keep the source half of the obligation.
    Add-ManifestRow -Component 'Eclipse SUMO data and Python tools (typemap, xsd, traci, sumolib)' `
                    -Provenance "github.com/eclipse-sumo/sumo at $sumoPin" `
                    -License 'EPL-2.0 (licenses\SUMO-LICENSE.txt)' -Location 'tools\sumo\data\, tools\sumo\tools\'

    # Each staged DLL's upstream project and the licence text the pinned SUMOLibraries bundle carries
    # for it. This maps a file name to metadata that cannot be derived from the file; WHICH rows
    # appear is still decided by what the import walk above actually copied. A staged DLL missing
    # from this table is reported as unattributed, so the inventory cannot quietly omit one.
    $sumoLibs = Join-Path $BuildDir 'SUMOLibraries'
    $apacheText = @{ From = 'xerces-c-3.3.0\LICENSE'; As = 'Apache-2.0.txt' }
    $nativeLicenses = @{
        'xerces-c_3_3.dll'    = @{ Component = 'Apache Xerces-C++ 3.3.0'; License = 'Apache-2.0'; Text = $apacheText }
        'arrow.dll'           = @{ Component = 'Apache Arrow 22.0.0';     License = 'Apache-2.0'; Text = $apacheText }
        'parquet.dll'         = @{ Component = 'Apache Parquet C++ 22.0.0'; License = 'Apache-2.0'; Text = $apacheText }
        'thriftmd.dll'        = @{ Component = 'Apache Thrift 0.22.0';    License = 'Apache-2.0'; Text = $apacheText }
        'proj_9.dll'          = @{ Component = 'PROJ 9.5.0';              License = 'PROJ licence (MIT-style)'; Text = @{ From = 'proj-9.5.0\LICENSE'; As = 'PROJ-LICENSE.txt' } }
        'sqlite3.dll'         = @{ Component = 'SQLite 3.46.1';           License = 'public domain'; Text = @{ From = '3rdPartyLibs\sqlite-3.46.1\LICENSE'; As = 'SQLite-LICENSE.txt' } }
        'tiff.dll'            = @{ Component = 'libtiff 4.7.0';           License = 'libtiff licence (BSD-style)'; Text = @{ From = '3rdPartyLibs\tiff-4.7.0\LICENSE'; As = 'libtiff-LICENSE.txt' } }
        'libcurl.dll'         = @{ Component = 'curl 8.10.1';             License = 'curl licence (MIT-style)'; Text = @{ From = '3rdPartyLibs\curl-8.10.1\LICENSE'; As = 'curl-LICENSE.txt' } }
        'libssh2.dll'         = @{ Component = 'libssh2 1.11.1';          License = 'BSD-3-Clause'; Text = @{ From = '3rdPartyLibs\libssh2-1.11.1\LICENSE'; As = 'libssh2-LICENSE.txt' } }
        'libssl-3-x64.dll'    = @{ Component = 'OpenSSL 3.3.2';           License = 'Apache-2.0'; Text = @{ From = '3rdPartyLibs\openssl-3.3.2\LICENSE'; As = 'OpenSSL-LICENSE.txt' } }
        'libcrypto-3-x64.dll' = @{ Component = 'OpenSSL 3.3.2';           License = 'Apache-2.0'; Text = @{ From = '3rdPartyLibs\openssl-3.3.2\LICENSE'; As = 'OpenSSL-LICENSE.txt' } }
        'zlib.dll'            = @{ Component = 'zlib 1.3.1';              License = 'Zlib'; Text = @{ From = '3rdPartyLibs\zlib-1.3.1\LICENSE'; As = 'zlib-LICENSE.txt' } }
        'bz2-1.dll'           = @{ Component = 'bzip2 1.1.0';             License = 'bzip2 licence (BSD-style)'; Text = @{ From = '3rdPartyLibs\bzip2-1.1.0\LICENSE'; As = 'bzip2-LICENSE.txt' } }
        'libpng16.dll'        = @{ Component = 'libpng 1.6.44';           License = 'PNG Reference Library License'; Text = @{ From = '3rdPartyLibs\libpng-1.6.44\LICENSE'; As = 'libpng-LICENSE.txt' } }
        'freetype.dll'        = @{ Component = 'FreeType 2.13.3';         License = 'FreeType licence or GPL-2.0'; Text = @{ From = '3rdPartyLibs\freetype-2.13.3\LICENSE'; As = 'FreeType-LICENSE.txt' } }
        # GNU components: the bundle carries the text shown, which is the one that ships.
        'fox-16.dll'          = @{ Component = 'FOX toolkit 1.6.59 (SUMO GUI toolkit; sumo and duarouter import it, netconvert does not)'
                                   License = 'LGPL-2.1 with the addendum the project carries'
                                   Text = @{ From = 'fox-1.6.59\LICENSE'; As = 'FOX-LICENSE.txt' }
                                   AlsoText = @{ From = 'fox-1.6.59\LICENSE_ADDENDUM'; As = 'FOX-LICENSE_ADDENDUM.txt' } }
        'iconv-2.dll'         = @{ Component = 'GNU libiconv 1.17';       License = 'the bundle carries a GPL-3.0 text; the libiconv runtime is LGPL-2.1-or-later'; Text = @{ From = '3rdPartyLibs\libiconv-1.17\LICENSE'; As = 'libiconv-LICENSE.txt' } }
        'intl-8.dll'          = @{ Component = 'GNU gettext runtime 0.21'; License = 'the bundle carries a GPL-3.0 text; the libintl runtime is LGPL-2.1-or-later'; Text = @{ From = 'gettext-0.21\LICENSE'; As = 'gettext-LICENSE.txt' } }
        # Microsoft's redistributables carry no text in the bundle; their terms come with Visual Studio.
        'MSVCP140.dll'        = @{ Component = 'Microsoft Visual C++ runtime'; License = 'Microsoft Visual Studio redistributable terms'; Text = $null }
        'VCRUNTIME140.dll'    = @{ Component = 'Microsoft Visual C++ runtime'; License = 'Microsoft Visual Studio redistributable terms'; Text = $null }
        'VCRUNTIME140_1.dll'  = @{ Component = 'Microsoft Visual C++ runtime'; License = 'Microsoft Visual Studio redistributable terms'; Text = $null }
    }
    $attributed = @{}
    foreach ($library in $sumoNativeStaged) {
        $entry = $nativeLicenses[$library]
        if (-not $entry) {
            Write-Fail "[dist] WARNING: $library has no licence row; MANIFEST.md will list it as unattributed."
            Add-ManifestRow -Component "$library (UNATTRIBUTED - add it to the licence table)" `
                            -Provenance 'DLR-TS/SUMOLibraries bundle' -License 'unknown' -Location 'tools\sumo\'
            continue
        }
        $filed = 'text not carried'
        $texts = @($entry['Text'])
        if ($entry.ContainsKey('AlsoText')) { $texts += $entry['AlsoText'] }
        foreach ($text in $texts) {
            if (-not $text) { continue }
            $copied = Copy-LicenseText (Join-Path $sumoLibs $text.From) $text.As
            if ($copied) { $filed = if ($filed -eq 'text not carried') { "licenses\$copied" } else { "$filed, licenses\$copied" } }
        }
        # One row per upstream component, listing every file that came from it.
        if ($attributed.ContainsKey($entry.Component)) { $attributed[$entry.Component].Files += $library }
        else { $attributed[$entry.Component] = @{ Files = @($library); License = $entry.License; Filed = $filed } }
    }
    foreach ($component in ($attributed.Keys | Sort-Object)) {
        $row = $attributed[$component]
        Add-ManifestRow -Component $component -Provenance "DLR-TS/SUMOLibraries $($row.Files -join ', ')" `
                        -License "$($row.License) ($($row.Filed))" -Location 'tools\sumo\'
    }

    if (Test-Path (Join-Path $sumoDest 'proj')) {
        Add-ManifestRow -Component 'PROJ coordinate database' -Provenance 'PROJ 9.5.0 data files' `
                        -License 'PROJ licence (licenses\PROJ-LICENSE.txt)' -Location 'tools\sumo\proj\'
    }
}

# 7. Helper scripts + README for the target machine.
$setupVenv = @'
#Requires -Version 5.1
# Create a Python venv and install the carlanet + carlacontrol wheels + the demo's Python deps.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
python -m venv "$here\venv"
$py = Join-Path $here 'venv\Scripts\python.exe'
& $py -m pip install --upgrade pip
# Every wheel is passed together (and --find-links points at wheels\) so carlacontrol's dependency
# on the local-only carlanet wheel resolves from the bundle rather than from a package index.
$wheels = @(Get-ChildItem "$here\wheels\*.whl" | ForEach-Object { $_.FullName })
if ($wheels.Count -eq 0) { throw "no wheels under $here\wheels; this distribution is incomplete." }
& $py -m pip install --find-links "$here\wheels" @wheels numpy pygame
Write-Host "venv ready: $here\venv  (activate: $here\venv\Scripts\Activate.ps1)" -ForegroundColor Green
'@
Set-Content -Path (Join-Path $dist 'setup-venv.ps1') -Value $setupVenv -Encoding UTF8

$runServer = @'
#Requires -Version 5.1
# Launch the CARLA server. -RenderOffScreen runs headless; remove it to get a render window.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
& "$here\CarlaServer\CarlaUnreal.exe" -RenderOffScreen -nosound @args
'@
Set-Content -Path (Join-Path $dist 'run-server.ps1') -Value $runServer -Encoding UTF8

$runSctmv = @'
#Requires -Version 5.1
# Run the single-client traffic-manager / EO demo against a running server.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$py = Join-Path $here 'venv\Scripts\python.exe'
if (-not (Test-Path $py)) { throw "venv missing - run .\setup-venv.ps1 first." }
$env:CARLA_NETCONVERT = Join-Path $here 'tools\sumo\netconvert.exe'
$env:SUMO_HOME = Join-Path $here 'tools\sumo'
$proj = Join-Path $here 'tools\sumo\proj'
if (Test-Path (Join-Path $proj 'proj.db')) { $env:PROJ_LIB = $proj; $env:PROJ_DATA = $proj }
& $py "$here\scripts\run_SCTMV.py" @args
'@
Set-Content -Path (Join-Path $dist 'run-sctmv.ps1') -Value $runSctmv -Encoding UTF8

$readmeVersion = $pkgName -replace '^Carla-', ''
$readme = @"
# CARLA $readmeVersion distribution (Windows)

Self-contained CARLA digital-twin bundle: the cooked server, the carlanet + carlacontrol Python
client packages, the run_SCTMV demo, example OSM maps, and SUMO netconvert.

## Target prerequisites
- 64-bit Windows 10/11 with a GPU + up-to-date graphics drivers (the server renders even headless).
- **Python 3.11** (on PATH, for the venv).
- The **.NET 10 runtime** (carlanet loads .NET assemblies). Install e.g. ``winget install Microsoft.DotNet.Runtime.10``.
- netconvert's DLLs + PROJ data are bundled under ``tools\sumo``.

## Run it (PowerShell)
``````powershell
.\setup-venv.ps1                       # one-time: venv + carlanet & carlacontrol wheels + numpy + pygame
.\run-server.ps1                       # start the CARLA server (new window or background job)
.\run-sctmv.ps1 --osm osm\Lakeview_Carson.osm   # build a world from an OSM map and run the demo
``````
``run-sctmv.ps1`` points carlanet at the bundled ``tools\sumo\netconvert.exe`` and sets ``SUMO_HOME``
to ``tools\sumo``; pass ``--help`` to run-sctmv for options.

## What is in here, and under what terms
``MANIFEST.md`` lists every component this bundle carries, where it came from and its licence, with
the licence texts themselves under ``licenses\``. Both are generated from what the packaging script
actually copied, so they describe this bundle rather than an intended one.
"@
Set-Content -Path (Join-Path $dist 'README.md') -Value $readme -Encoding UTF8

# ============================================================================
#  8. MANIFEST.md -- the inventory the steps above built up, rendered last so it
#     covers everything that was actually staged.
# ============================================================================
$versionSummary = if (Test-Path (Join-Path $dist 'VERSION')) {
    ((Get-Content (Join-Path $dist 'VERSION')) -join '; ')
} else { 'no VERSION file was staged' }

$manifest = [System.Text.StringBuilder]::new()
[void]$manifest.AppendLine("# $pkgName - component and licence manifest")
[void]$manifest.AppendLine('')
[void]$manifest.AppendLine("Generated by ``Scripts\Windows\MakeDistribution.ps1`` on $(Get-Date -Format 'yyyy-MM-dd') from")
[void]$manifest.AppendLine('what it copied into this bundle. It is not hand-maintained, and it is an inventory for a')
[void]$manifest.AppendLine('licensing review rather than a legal determination.')
[void]$manifest.AppendLine('')
[void]$manifest.AppendLine("Build: $versionSummary")
[void]$manifest.AppendLine('')
[void]$manifest.AppendLine('| Component | Provenance | Licence | Location |')
[void]$manifest.AppendLine('|---|---|---|---|')
foreach ($row in $manifestRows) {
    [void]$manifest.AppendLine("| $($row.Component) | $($row.Provenance) | $($row.License) | ``$($row.Location)`` |")
}
[void]$manifest.AppendLine('')
[void]$manifest.AppendLine('Licence texts are under `licenses\`. Eclipse SUMO is distributed under the EPL-2.0, which')
[void]$manifest.AppendLine('carries a source offer: the exact commit every SUMO binary here was built from is named in')
[void]$manifest.AppendLine('its row above, and its source is available from that commit at github.com/eclipse-sumo/sumo.')
Set-Content -Path (Join-Path $dist 'MANIFEST.md') -Value $manifest.ToString() -Encoding UTF8
Write-Info "[dist] MANIFEST.md: $($manifestRows.Count) components, $(@(Get-ChildItem "$licenseDir\*").Count) licence texts"

# ============================================================================
#  9. Archive (.zip). Prefer 7-Zip (fast, multithreaded), else Windows' bundled
#     tar.exe (libarchive, makes a .zip from the extension), else Compress-Archive.
# ============================================================================
if ($SkipArchive) {
    Write-Info "[dist] -SkipArchive: assembled folder only."
    Write-Info "[dist] DONE: $dist"
    Write-Info "       Run it in place: $dist\run-server.ps1"
    return
}

$zip = Join-Path $BuildDir "Dist\$pkgName.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Write-Info "[dist] creating archive $zip (compresses the whole package; can take several minutes)"

$sevenZip = $null
foreach ($cand in @('7z.exe',
                    (Join-Path $env:ProgramFiles '7-Zip\7z.exe'),
                    (Join-Path ${env:ProgramFiles(x86)} '7-Zip\7z.exe'))) {
    $c = Get-Command $cand -ErrorAction SilentlyContinue
    if ($c) { $sevenZip = $c.Source; break }
    if (Test-Path $cand) { $sevenZip = $cand; break }
}

$distRoot = Join-Path $BuildDir 'Dist'
if ($sevenZip) {
    Write-Info "[dist] using 7-Zip: $sevenZip"
    Push-Location $distRoot
    try { & $sevenZip a -tzip -mmt=on "$zip" "$pkgName" | Out-Null; $rc = $LASTEXITCODE }
    finally { Pop-Location }
    if ($rc -ne 0) { throw "7-Zip failed (exit $rc)." }
} elseif (Get-Command tar.exe -ErrorAction SilentlyContinue) {
    Write-Info "[dist] using tar.exe (libarchive)"
    Push-Location $distRoot
    try { & tar.exe -a -c -f "$zip" "$pkgName"; $rc = $LASTEXITCODE }
    finally { Pop-Location }
    if ($rc -ne 0) { throw "tar.exe failed (exit $rc)." }
} else {
    Write-Warning "neither 7-Zip nor tar.exe found; falling back to Compress-Archive (slow for large bundles)."
    Compress-Archive -Path $dist -DestinationPath $zip -CompressionLevel Optimal
}

$sizeGB = [math]::Round((Get-Item $zip).Length / 1GB, 2)
Write-Info "[dist] DONE: $zip ($sizeGB GB)"
