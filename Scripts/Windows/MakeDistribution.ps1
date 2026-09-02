#Requires -Version 5.1
<#
.SYNOPSIS
    Assemble a self-contained CARLA Windows distribution (Build\Dist\<name>.zip).

.DESCRIPTION
    Windows peer of Scripts\Linux\MakeDistribution.sh. Produces
    Build\Dist\Carla-<version>-Win64-<config>\  (and a matching .zip) containing everything
    needed to run the digital-twin single-client traffic-manager demo on another Windows machine:

      CarlaServer\   the cooked CARLA server (the packaged game; run with CarlaUnreal.exe)
      wheels\        the carlanet Python wheel (install into a venv)
      scripts\       SCTMV.py + osm_clip.py (the demo client + OSM clipper)
      osm\           the example OpenStreetMap maps SCTMV can build worlds from
      tools\sumo\    SUMO netconvert.exe + its DLLs + PROJ data (OSM -> OpenDRIVE conversion)
      setup-venv.ps1 / run-server.ps1 / run-sctmv.ps1 / README.md

    Run AFTER the build + cook have produced the artifacts:
      .\Scripts\Windows\BuildCarla.ps1                              # editor + carlanet wheel
      cmake --build Build --target package-development              # cook + stage the server
    then:
      .\Scripts\Windows\MakeDistribution.ps1                       # assemble the bundle
    or pass -Build to run those steps first:
      .\Scripts\Windows\MakeDistribution.ps1 -Build -Config Development

    Unlike the Linux SUMO bundling (which walks ldd), Windows SUMO ships netconvert.exe with its
    DLLs already beside it in Build\sumo-install\bin, so this just copies that folder + the PROJ
    data folder. The assembled Build\Dist\<name>\ folder is runnable in place; the .zip is only for
    shipping to another machine -- pass -SkipArchive to skip it during local test iterations.

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
foreach ($d in 'CarlaServer', 'wheels', 'scripts', 'osm', 'tools\sumo') {
    New-Item -ItemType Directory -Force -Path (Join-Path $dist $d) | Out-Null
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
    Write-Warn "[dist] no VERSION at $versionSrc; the distribution will not state its build."
}

# 2. carlanet wheel (newest).
$whl = Get-ChildItem (Join-Path $CarlaRoot 'CarlaNet\python\dist\*.whl') -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($whl) { Copy-Item -Force $whl.FullName (Join-Path $dist 'wheels'); Write-Info "[dist] wheel: $($whl.Name)" }
else { Write-Warning "no wheel under CarlaNet\python\dist (run build_wheel.ps1 / BuildCarla.ps1)" }

# 3. Demo client + its only local import.
foreach ($f in 'SCTMV.py', 'osm_clip.py') {
    $src = Join-Path $CarlaRoot "CarlaNet\python\$f"
    if (Test-Path $src) { Copy-Item -Force $src (Join-Path $dist 'scripts') }
    else { Write-Warning "missing $src" }
}

# 4. Example OSM maps.
$osm = Get-ChildItem (Join-Path $CarlaRoot 'Import\*.osm') -ErrorAction SilentlyContinue
if ($osm) { Copy-Item -Force $osm.FullName (Join-Path $dist 'osm') }
else { Write-Warning "no .osm files under Import\" }

# 5. SUMO netconvert + its DLLs + PROJ data. The Windows SUMO build already places every runtime
#    DLL beside netconvert.exe in Build\sumo-install\bin, so copying that folder is self-contained
#    (Windows resolves a binary's DLLs from its own directory) -- no ldd walk like the Linux peer.
$sumoBin = Join-Path $BuildDir 'sumo-install\bin'
$nc      = Join-Path $sumoBin 'netconvert.exe'
if (Test-Path $nc) {
    Copy-Item -Recurse -Force -Path (Join-Path $sumoBin '*') -Destination (Join-Path $dist 'tools\sumo')
    $projSrc = Join-Path $BuildDir 'sumo-install\share\proj'
    if (Test-Path (Join-Path $projSrc 'proj.db')) {
        New-Item -ItemType Directory -Force -Path (Join-Path $dist 'tools\sumo\proj') | Out-Null
        Copy-Item -Recurse -Force -Path (Join-Path $projSrc '*') -Destination (Join-Path $dist 'tools\sumo\proj')
        Write-Info "[dist] bundled netconvert + DLLs + PROJ data"
    } else {
        Write-Warning "proj.db not found under $projSrc; OSM geo-referencing may fail on the target"
    }
} else {
    Write-Warning "netconvert.exe not found at $nc (run CarlaSetup.ps1); OSM->OpenDRIVE unavailable"
}

# 6. Helper scripts + README for the target machine.
$setupVenv = @'
#Requires -Version 5.1
# Create a Python venv and install the carlanet wheel + the demo's Python dependencies.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
python -m venv "$here\venv"
$py = Join-Path $here 'venv\Scripts\python.exe'
& $py -m pip install --upgrade pip
$whl = Get-ChildItem "$here\wheels\*.whl" | Select-Object -First 1
& $py -m pip install $whl.FullName numpy pygame
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
$proj = Join-Path $here 'tools\sumo\proj'
if (Test-Path (Join-Path $proj 'proj.db')) { $env:PROJ_LIB = $proj; $env:PROJ_DATA = $proj }
& $py "$here\scripts\SCTMV.py" @args
'@
Set-Content -Path (Join-Path $dist 'run-sctmv.ps1') -Value $runSctmv -Encoding UTF8

$readmeVersion = $pkgName -replace '^Carla-', ''
$readme = @"
# CARLA $readmeVersion distribution (Windows)

Self-contained CARLA digital-twin bundle: the cooked server, the carlanet Python client, the SCTMV
demo, example OSM maps, and SUMO netconvert.

## Target prerequisites
- 64-bit Windows 10/11 with a GPU + up-to-date graphics drivers (the server renders even headless).
- **Python 3.11** (on PATH, for the venv).
- The **.NET 10 runtime** (carlanet loads .NET assemblies). Install e.g. ``winget install Microsoft.DotNet.Runtime.10``.
- netconvert's DLLs + PROJ data are bundled under ``tools\sumo``.

## Run it (PowerShell)
``````powershell
.\setup-venv.ps1                       # one-time: venv + carlanet wheel + numpy + pygame
.\run-server.ps1                       # start the CARLA server (new window or background job)
.\run-sctmv.ps1 --osm osm\Lakeview_Carson.osm   # build a world from an OSM map and run the demo
``````
``run-sctmv.ps1`` points carlanet at the bundled ``tools\sumo\netconvert.exe``; pass ``--help`` to SCTMV for options.
"@
Set-Content -Path (Join-Path $dist 'README.md') -Value $readme -Encoding UTF8

# ============================================================================
#  7. Archive (.zip). Prefer 7-Zip (fast, multithreaded), else Windows' bundled
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
