<#
.SYNOPSIS
    PowerShell port of CarlaSetup.bat: provisions prerequisites, fetches content,
    builds the bundled SUMO `netconvert`, fetches the optional VibeUE plugin, then
    configures and builds CARLA against a source UE 5.7.4.

.DESCRIPTION
    This is a faithful but modernized rewrite of CarlaSetup.bat. Notable differences:

      * Visual Studio is discovered with vswhere (works for installs in ANY location,
        not just %PROGRAMFILES%\Microsoft Visual Studio\...). VS2022 and VS2026 are
        both supported; MSVC toolset 14.44 is REQUIRED and enforced.
      * Sequential PowerShell control flow eliminates the cmd "caret continuation
        inside a parenthesized block" footgun that caused the .bat to silently stop
        after the SUMO build on a fresh checkout.
      * Adds -Clean / -CleanAll to wipe SUMO build artifacts before rebuilding.

    REQUIREMENTS:
      * CMake on PATH. The SUMO step uses the Visual Studio generator, so:
          - VS2022 ("Visual Studio 17 2022") needs CMake >= 3.21
          - VS2026 ("Visual Studio 18 2026") needs CMake >= 4.1   (winget install -e --id Kitware.CMake)
      * Visual Studio 2022 or 2026 with the MSVC 14.44 toolset (enforced).
      * Ninja (for the main CARLA build); installed by the prerequisites step.

.PARAMETER Vs
    Force a Visual Studio toolchain: '2022' or '2026'. If the requested version
    (with MSVC 14.44) is not installed, the script ERRORS OUT rather than falling
    back -- an explicit request that can't be honored means the environment is not
    what the caller thinks it is. When omitted, the NEWEST installed VS that has
    MSVC 14.44 is used (or the current VS dev prompt if one is already active).

.PARAMETER Clean
    Remove generated/built SUMO artifacts (Build\sumo-build, Build\sumo-install)
    before building, forcing a fresh CMake configure + netconvert build. The SUMO
    source checkout and the ~3 GB SUMOLibraries clone are kept for speed.

.PARAMETER CleanAll
    Everything -Clean does, PLUS removes Build\sumo-src and Build\SUMOLibraries for
    a fully pristine, from-scratch re-clone (slow).

.PARAMETER CleanCarla
    Clear ONLY the CARLA CMake configuration (Build\CMakeCache.txt + Build\CMakeFiles)
    so the next configure re-detects the compiler/toolset. Preserves SUMO and the
    downloaded Build\_deps sources. Note: the script ALSO does this automatically when
    it detects the cached CMAKE_CXX_COMPILER differs from the just-activated compiler
    (e.g. after switching between VS2022 and VS2026), which otherwise mixes toolsets and
    fails with STL1001 "Unexpected compiler version".

.PARAMETER SkipPrerequisites
    Skip the InstallPrerequisites step.    (.bat equivalent: --skip-prerequisites / -p)

.PARAMETER Launch
    Launch the CARLA Unreal Editor after a successful build.  (.bat: --launch / -l)

.PARAMETER WithPythonApi
    Build the legacy Boost.Python `carla` extension module. OFF BY DEFAULT: this is a
    CarlaNet-first setup, and the pure-Python `carlanet` shim needs none of it. By
    default the script passes -DBUILD_PYTHON_API=OFF and skips carla-python-api-install,
    avoiding the legacy module's numpy<2 / Python<=3.12 constraints. Pass -WithPythonApi
    (or --with-python-api) only if you specifically need the legacy module; it then
    requires a Python <=3.12 with numpy<2.

.PARAMETER WithTests
    Build LibCarla's C++ unit tests. OFF BY DEFAULT: the tests pull in googletest, which is
    compiled with /Wall + /WX and fails under VS2026's stricter STL (pedantic warnings such
    as C4710/C4711 become errors). A CarlaNet-only build does not need them. By default the
    script passes -DBUILD_LIBCARLA_TESTS=OFF. Pass -WithTests (or --with-tests) to build them.

.PARAMETER Interactive
    Reserved for parity with the .bat (--interactive / -i); currently unused.

.PARAMETER PythonRoot
    Root directory of the Python install to build the API against. (.bat: --python-root)

.PARAMETER VibeUeSshKey
    Path to the SSH private key used to fetch the private VibeUE mirror. Falls back
    to $env:VIBEUE_SSH_KEY.   (.bat: --vibeue-ssh-key)

.EXAMPLE
    .\CarlaSetup.ps1 -SkipPrerequisites
    Build using the newest installed VS with MSVC 14.44.

.EXAMPLE
    .\CarlaSetup.ps1 -Vs 2026 -Clean -SkipPrerequisites
    Force the VS2026 toolchain and rebuild SUMO from a clean CMake configure.
#>

# PositionalBinding=$false: stray tokens (e.g. the .bat-style `--help`) must never
# silently bind to a parameter like -Vs. They land in $Remaining and are normalized
# below, so both PowerShell-native (-Vs 2026) and legacy .bat (--vs=2026) styles work.
[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$Vs,

    [switch]$Clean,
    [switch]$CleanAll,
    [switch]$CleanCarla,

    [Alias('p')]
    [switch]$SkipPrerequisites,

    [Alias('l')]
    [switch]$Launch,

    [Alias('i')]
    [switch]$Interactive,

    [switch]$WithPythonApi,
    [switch]$WithTests,

    [string]$PythonRoot,

    [string]$VibeUeSshKey,

    [Alias('h')]
    [switch]$Help,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Remaining
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Show-Usage {
    @'
CarlaSetup.ps1 - provision + build CARLA against a source UE 5.7.4.

USAGE:
  .\CarlaSetup.ps1 [options]

OPTIONS (PowerShell-native | legacy .bat alias):
  -Vs <2022|2026>            --vs=<2022|2026>        Force VS toolchain (must have MSVC 14.44).
                                                     Omit to use the newest installed VS.
  -Clean                     --clean                 Remove Build\sumo-build + Build\sumo-install.
  -CleanAll                  --clean-all             Also remove Build\sumo-src + Build\SUMOLibraries.
  -CleanCarla                --clean-carla           Clear the CARLA CMake cache (force compiler/toolset re-detect).
                                                     (Auto-triggered anyway if the cached compiler != active one.)
  -SkipPrerequisites  / -p   --skip-prerequisites    Skip the InstallPrerequisites step.
  -Launch             / -l   --launch                Launch the Unreal Editor after building.
  -WithPythonApi             --with-python-api       Build the legacy Boost.Python `carla` module.
                                                     (Off by default; CarlaNet-only builds don't need it.)
  -WithTests                 --with-tests            Build LibCarla C++ tests (pulls in googletest).
                                                     (Off by default; googletest fails under VS2026 /Wall /WX.)
  -Interactive        / -i   --interactive           (Reserved; parity with the .bat.)
  -PythonRoot <dir>          --python-root=<dir>     Python install root for the API build.
  -VibeUeSshKey <path>       --vibeue-ssh-key=<path> SSH key for the private VibeUE mirror.
  -Help               / -h   --help                  Show this help.

EXAMPLES:
  .\CarlaSetup.ps1 -SkipPrerequisites
  .\CarlaSetup.ps1 -Vs 2026 -Clean -SkipPrerequisites
'@ | Write-Host
}

# -- Normalize legacy .bat-style "--flag" / "--flag=value" arguments ---------
# Tokens that PowerShell couldn't bind natively arrive in $Remaining. Walk them
# (supporting both "--key=value" and "--key value" forms) and fold them onto the
# real parameters, so habits from CarlaSetup.bat keep working.
if ($Remaining) {
    for ($idx = 0; $idx -lt $Remaining.Count; $idx++) {
        $arg = $Remaining[$idx]
        # Split "--key=value" once; otherwise the value (if any) is the next token.
        if ($arg -match '^(--[^=]+)=(.*)$') {
            $key = $matches[1]; $val = $matches[2]
        } else {
            $key = $arg; $val = $null
        }
        # For value-taking flags: use the inline "=value", else consume the next token.
        # Runs in the loop's own scope so advancing $idx skips the consumed token.
        if ($null -ne $val) {
            $next = $val
        } elseif ($idx + 1 -lt $Remaining.Count) {
            $next = $Remaining[$idx + 1]   # only committed (++$idx) by value-taking cases
        } else {
            $next = $null
        }
        switch -Regex ($key) {
            '^(--help|/\?|help)$'        { $Help = $true }
            '^(--interactive)$'          { $Interactive = $true }
            '^(--skip-prerequisites)$'   { $SkipPrerequisites = $true }
            '^(--launch)$'               { $Launch = $true }
            '^(--with-python-api)$'      { $WithPythonApi = $true }
            '^(--with-tests)$'           { $WithTests = $true }
            '^(--clean)$'                { $Clean = $true }
            '^(--clean-all)$'            { $CleanAll = $true }
            '^(--clean-carla)$'          { $CleanCarla = $true }
            '^(--vs)$'                   { if ($null -eq $next) { throw "Argument '$key' requires a value." } $Vs = $next;          if ($null -eq $val) { $idx++ } }
            '^(--python-root|-pyroot)$'  { if ($null -eq $next) { throw "Argument '$key' requires a value." } $PythonRoot = $next;  if ($null -eq $val) { $idx++ } }
            '^(--vibeue-ssh-key)$'       { if ($null -eq $next) { throw "Argument '$key' requires a value." } $VibeUeSshKey = $next; if ($null -eq $val) { $idx++ } }
            default {
                Show-Usage
                throw "Unknown argument '$arg'."
            }
        }
    }
}

if ($Help) { Show-Usage; return }

# Validate -Vs here (manual, so --vs=foo gives a clear message instead of a binder error).
if ($Vs -and $Vs -notin @('2022', '2026')) {
    throw "Invalid -Vs value '$Vs'. Expected 2022 or 2026."
}

# Run everything relative to the repository root (this script's directory), the way
# the .bat relied on %cd% being the repo root.
$RepoRoot = $PSScriptRoot
Set-Location $RepoRoot

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

# Run a native command and abort the whole script if it returns a non-zero exit
# code. ($ErrorActionPreference='Stop' does NOT catch native exit codes, only
# PowerShell errors, so every external invocation is gated through here.)
function Invoke-Checked {
    param(
        [Parameter(Mandatory)][string]$What,
        [Parameter(Mandatory)][scriptblock]$Action
    )
    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw "$What failed (exit code $LASTEXITCODE)."
    }
}

# Parse the version of the cmake on PATH (used to gate the VS2026 generator below).
function Get-CMakeVersion {
    $line = & cmake --version 2>$null | Select-Object -First 1
    if ($line -match 'cmake version ([\d.]+)') { return [version]$matches[1] }
    throw "Could not determine CMake version. Is cmake on PATH? (https://cmake.org/download/)"
}

# Clear ONLY the CARLA CMake configuration (cache + CMakeFiles) so the next configure
# re-detects compilers/toolset. Preserves SUMO (Build\sumo-*, Build\SUMOLibraries) and the
# downloaded FetchContent sources (Build\_deps), so it does not re-download or rebuild SUMO.
function Clear-CarlaCmakeCache {
    foreach ($rel in @('Build\CMakeCache.txt', 'Build\CMakeFiles')) {
        $p = Join-Path $RepoRoot $rel
        if (Test-Path $p) { Write-Host "  removing $p"; Remove-Item -Recurse -Force $p }
    }
}

# Locate vswhere.exe (ships with the VS Installer since VS2017).
function Get-VsWhere {
    $candidate = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $candidate) { return $candidate }
    $cmd = Get-Command vswhere.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw "vswhere.exe not found. A Visual Studio 2022/2026 installation is required."
}

# Map a VS installationVersion major number to its product year + CMake generator.
function ConvertTo-VsYear {
    param([int]$Major)
    switch ($Major) {
        17 { @{ Year = '2022'; Generator = 'Visual Studio 17 2022' } }
        18 { @{ Year = '2026'; Generator = 'Visual Studio 18 2026' } }
        default { $null }
    }
}

# Enumerate VS installs that have the MSVC 14.44 toolset, newest first.
function Get-VsInstalls {
    $vswhere = Get-VsWhere
    $raw = & $vswhere -all -prerelease -products * -format json | ConvertFrom-Json
    $result = @()
    foreach ($inst in $raw) {
        $major = [int]($inst.installationVersion.Split('.')[0])
        $map = ConvertTo-VsYear -Major $major
        if (-not $map) { continue }   # VS2019 and older are not supported here.

        # Require MSVC 14.44.* under VC\Tools\MSVC.
        $msvcRoot = Join-Path $inst.installationPath 'VC\Tools\MSVC'
        $toolset = $null
        if (Test-Path $msvcRoot) {
            $toolset = Get-ChildItem $msvcRoot -Directory -Filter '14.44*' |
                Sort-Object Name -Descending | Select-Object -First 1
        }
        $vcvars = Join-Path $inst.installationPath 'VC\Auxiliary\Build\vcvars64.bat'
        $productId = if ($inst.PSObject.Properties.Name -contains 'productId') { $inst.productId } else { '' }

        $result += [pscustomobject]@{
            Year         = $map.Year
            Generator    = $map.Generator
            Version      = [version]$inst.installationVersion
            Path         = $inst.installationPath
            Vcvars       = $vcvars
            HasToolset   = ($null -ne $toolset -and (Test-Path $vcvars))
            Toolset      = if ($toolset) { $toolset.Name } else { $null }
            IsBuildTools = ($productId -like '*BuildTools*')
        }
    }
    # Newest version first; at equal version prefer a full IDE over Build Tools;
    # Path as a final tiebreaker so selection is deterministic (5.1's Sort is unstable).
    $result | Sort-Object @{ Expression = 'Version'; Descending = $true },
                          @{ Expression = 'IsBuildTools' },
                          @{ Expression = 'Path' }
}

# Import the environment exported by a vcvars64.bat into the current PS session.
#
# Why this is more involved than a one-liner (all learned the hard way on PS 5.1):
#   * PS 5.1 escapes embedded quotes as \" when calling a native exe, which cmd.exe
#     mangles -> the quoted vcvars path (spaces + "(x86)") breaks. So we never put
#     the path on the cmd command line; we write a temp .cmd wrapper instead.
#   * For installs in non-standard locations (E:\VS2026, G:\VS2022), vcvarsall.bat
#     shells out to bare `vswhere`; if the Installer dir isn't on PATH it fails and
#     bails before exporting anything. We prepend that dir to PATH in the wrapper.
#   * Redirecting the `call` itself (`>nul 2>&1`) breaks vcvarsall's env propagation
#     for these installs, so we DON'T redirect it; the banner is discarded later.
#   * vcvars emits benign stderr; with $ErrorActionPreference='Stop' the first line
#     would be promoted to a fatal NativeCommandError, so we neutralize EAP and dump
#     the resulting `set` to a file (not stdout) to keep the streams clean.
function Import-VcVars {
    param(
        [Parameter(Mandatory)][string]$Vcvars,
        [string]$ToolsetVersion   # e.g. '14.44.35207' -> passed as -vcvars_ver to pin MSVC
    )
    $vsWhereDir = Split-Path -Parent (Get-VsWhere)
    $verArg     = if ($ToolsetVersion) { " -vcvars_ver=$ToolsetVersion" } else { '' }
    # GetTempFileName CREATES the .tmp file; keep the originals so we can delete them too
    # (ChangeExtension only rewrites the string), otherwise every call leaks two .tmp files.
    $tmpCmd     = [System.IO.Path]::GetTempFileName()
    $tmpEnv     = [System.IO.Path]::GetTempFileName()
    $cmdFile    = [System.IO.Path]::ChangeExtension($tmpCmd, 'cmd')
    $envFile    = [System.IO.Path]::ChangeExtension($tmpEnv, 'env.txt')
    try {
        Set-Content -LiteralPath $cmdFile -Encoding Ascii -Value @"
@echo off
set "PATH=$vsWhereDir;%PATH%"
call "$Vcvars"$verArg
set > "$envFile"
"@
        $prevEAP = $ErrorActionPreference
        $ErrorActionPreference = 'SilentlyContinue'
        cmd.exe /c $cmdFile 2>&1 | Out-Null
        $ErrorActionPreference = $prevEAP

        if (-not (Test-Path -LiteralPath $envFile)) {
            throw "Failed to activate VS environment via `"$Vcvars`" (no environment captured)."
        }
        foreach ($line in (Get-Content -LiteralPath $envFile)) {
            $eq = $line.IndexOf('=')               # split on FIRST '='; values may contain '='
            if ($eq -gt 0) {                        # '> 0' skips cmd's hidden "=C:" drive vars
                [Environment]::SetEnvironmentVariable($line.Substring(0, $eq), $line.Substring($eq + 1), 'Process')
            }
        }
        if (-not $env:VCINSTALLDIR) {
            throw "vcvars activation did not set VCINSTALLDIR (`"$Vcvars`")."
        }
    } finally {
        Remove-Item -LiteralPath $tmpCmd, $tmpEnv, $cmdFile, $envFile -ErrorAction SilentlyContinue
    }
}

# True if this process is already activated for the given install + 14.44 toolset. Used to
# make activation idempotent: re-running the script in the SAME shell would otherwise import
# vcvars on top of an already-imported env each time, compounding PATH until cmd/vcvars choke.
function Test-VsAlreadyActive {
    param($Pick)
    if (-not $env:VCINSTALLDIR -or -not $env:VCToolsVersion) { return $false }
    if ($env:VCToolsVersion -ne $Pick.Toolset) { return $false }
    $active = $env:VCINSTALLDIR.Replace('\', '/').TrimEnd('/').ToLowerInvariant()  # <path>/VC
    $want   = $Pick.Path.Replace('\', '/').TrimEnd('/').ToLowerInvariant()
    return $active.StartsWith($want)
}

# Resolve + activate the Visual Studio toolchain per the selection rules.
function Initialize-VisualStudio {
    param([string]$Wanted)   # '', '2022', or '2026'

    $installs = Get-VsInstalls
    $usable = @($installs | Where-Object HasToolset)

    if ($Wanted) {
        # Explicit request: must exist WITH 14.44, otherwise hard error.
        $pick = $usable | Where-Object Year -EQ $Wanted | Select-Object -First 1
        if (-not $pick) {
            $have = ($installs | ForEach-Object { "VS$($_.Year) @ $($_.Path) (14.44: $($_.HasToolset))" }) -join "`n  "
            throw @"
Requested Visual Studio $Wanted with MSVC 14.44 was not found.
Installations detected:
  $have
Fix your -Vs argument or install the missing toolset before retrying.
"@
        }
        if (Test-VsAlreadyActive $pick) {
            Write-Host "VS$($pick.Year) (MSVC $($pick.Toolset)) already active in this shell; skipping re-activation."
            return $pick
        }
        Write-Host "Activating requested VS$($pick.Year) at `"$($pick.Path)`" (pinning MSVC $($pick.Toolset))."
        Import-VcVars -Vcvars $pick.Vcvars -ToolsetVersion $pick.Toolset
        return $pick
    }

    # No explicit request: honor an already-active dev prompt if it has 14.44.
    if ($env:VSINSTALLDIR) {
        $current = $usable | Where-Object { $_.Path.TrimEnd('\') -eq $env:VSINSTALLDIR.TrimEnd('\') } |
            Select-Object -First 1
        if ($current) {
            Write-Host "Using active VS dev environment: VS$($current.Year) at `"$($current.Path)`" (MSVC $($current.Toolset))."
            return $current
        }
        Write-Warning "Active VSINSTALLDIR ($env:VSINSTALLDIR) lacks MSVC 14.44; selecting a different install."
    }

    # Default: newest install that has MSVC 14.44.
    $pick = $usable | Select-Object -First 1
    if (-not $pick) {
        throw "No Visual Studio install with MSVC toolset 14.44 was found. Install VS2022 or VS2026 with component VC.14.44."
    }
    if (Test-VsAlreadyActive $pick) {
        Write-Host "VS$($pick.Year) (MSVC $($pick.Toolset)) already active in this shell; skipping re-activation."
        return $pick
    }
    Write-Host "Selected newest VS$($pick.Year) at `"$($pick.Path)`" (pinning MSVC $($pick.Toolset))."
    Import-VcVars -Vcvars $pick.Vcvars -ToolsetVersion $pick.Toolset
    return $pick
}

# ---------------------------------------------------------------------------
# Derived config
# ---------------------------------------------------------------------------

$pythonPath = if ($PythonRoot) { Join-Path $PythonRoot 'python' } else { 'python' }
$vibeueKey  = if ($VibeUeSshKey) { $VibeUeSshKey } else { $env:VIBEUE_SSH_KEY }

# ---------------------------------------------------------------------------
# PREREQUISITES INSTALL STEP
# ---------------------------------------------------------------------------

if (-not $SkipPrerequisites) {
    Write-Host 'Installing prerequisites...'
    $prereq = Join-Path $RepoRoot 'Util\SetupUtils\InstallPrerequisites.bat'
    Invoke-Checked 'InstallPrerequisites' { & $prereq "--python-path=$pythonPath" }
} else {
    Write-Host 'Skipping prerequisites install step.'
}

# ---------------------------------------------------------------------------
# CLONE CONTENT
# ---------------------------------------------------------------------------

$contentDir = Join-Path $RepoRoot 'Unreal\CarlaUnreal\Content'
if (Test-Path $contentDir) {
    Write-Host 'Found CARLA content.'
} else {
    Write-Host 'Could not find CARLA content. Downloading...'
    New-Item -ItemType Directory -Force -Path $contentDir | Out-Null
    Invoke-Checked 'git clone carla-content' {
        git -C $contentDir clone -b ue5-dev https://bitbucket.org/carla-simulator/carla-content.git Carla
    }
}

# ---------------------------------------------------------------------------
# ACTIVATE VISUAL STUDIO TOOLCHAIN (VS2022 / VS2026, MSVC 14.44)
# ---------------------------------------------------------------------------

# NB: do not name this $vs -- PowerShell variable names are case-insensitive, so $vs
# would alias the [string]-typed parameter $Vs and coerce this object back to a string.
$vsInfo = Initialize-VisualStudio -Wanted $Vs
$cmakeGenerator = $vsInfo.Generator
Write-Host "Using CMake generator: $cmakeGenerator (toolset v143, version 14.44)."

# CMake version requirements (the SUMO step below uses the VS generator):
#   * "Visual Studio 17 2022"  -> CMake >= 3.21
#   * "Visual Studio 18 2026"  -> CMake >= 4.1   (generator added in CMake 4.1, Aug 2025)
# Fail fast with actionable guidance instead of CMake dumping its whole generator list.
$cmakeVersion = Get-CMakeVersion
if ($vsInfo.Year -eq '2026' -and $cmakeVersion -lt [version]'4.1') {
    throw @"
The VS2026 generator ("$cmakeGenerator") requires CMake >= 4.1, but found CMake $cmakeVersion.
Upgrade CMake, then open a FRESH shell so PATH points at the new cmake:
  winget install -e --id Kitware.CMake
"@
}
Write-Host "CMake $cmakeVersion detected."

# ---------------------------------------------------------------------------
# VERIFY UNREAL ENGINE
# ---------------------------------------------------------------------------

if ($env:CARLA_UNREAL_ENGINE_PATH -and (Test-Path $env:CARLA_UNREAL_ENGINE_PATH)) {
    Write-Host "Found Unreal Engine 5 at `"$env:CARLA_UNREAL_ENGINE_PATH`"."
} else {
    throw @"
CARLA_UNREAL_ENGINE_PATH is not set or does not exist.
Set it to the root of your UE 5.7.4 source build, e.g.:
  `$env:CARLA_UNREAL_ENGINE_PATH = 'g:\Projects\CarlaUE_5_7_4\UE_5_7_4'
"@
}

# ---------------------------------------------------------------------------
# BUILD SUMO netconvert (OSM -> OpenDRIVE converter, bundled for CarlaNet)
# ---------------------------------------------------------------------------
# CarlaNet shells out to stock SUMO `netconvert` at runtime to convert OSM maps to
# OpenDRIVE, replacing CARLA's old in-tree osm2odr fork. We build ONLY the
# `netconvert` target from SUMO release v1_27_0 (commit e238ea04). On Windows the
# build deps (Xerces-C, PROJ, sqlite3, ...) come from the prebuilt DLR-TS
# SUMOLibraries bundle; the build copies the needed DLLs next to netconvert.exe.

$sumoSrc     = Join-Path $RepoRoot 'Build\sumo-src'
$sumoBuild   = Join-Path $RepoRoot 'Build\sumo-build'
$sumoInstall = Join-Path $RepoRoot 'Build\sumo-install'
$sumoLibs    = Join-Path $RepoRoot 'Build\SUMOLibraries'

# Pins. The SUMOLibraries bundle MUST be pinned to match the SUMO source, because its
# directory layout changes over time. DLR-TS/SUMOLibraries HEAD moved zlib from
# `3rdPartyLibs/zlib-*` (what SUMO 1.27.0's CMake globs) to the top level; cloning HEAD
# therefore left find_package(ZLIB) empty -> HAVE_ZLIB undefined -> SUMO's no-zlib code
# path failed to compile (missing <fstream> transitive include + a latent bug referencing
# a non-existent `compressed` identifier). Pinning the bundle to its matching `1.27.0` tag
# restores the expected layout (zlib found, HAVE_ZLIB=1, and proj at the expected version).
$sumoSrcPin   = 'e238ea04b7150ba23a348a285d3048919fa4830b'   # SUMO v1_27_0
$sumoLibsTag  = '1.27.0'                                      # DLR-TS/SUMOLibraries tag
$sumoLibsPin  = 'a71441cce51dea77cabe135ce010b1863f4a4700'   # commit the tag points at

# -- CLEAN ------------------------------------------------------------------
if ($Clean -or $CleanAll) {
    foreach ($d in @($sumoBuild, $sumoInstall)) {
        if (Test-Path $d) { Write-Host "Cleaning $d"; Remove-Item -Recurse -Force $d }
    }
    if ($CleanAll) {
        foreach ($d in @($sumoSrc, $sumoLibs)) {
            if (Test-Path $d) { Write-Host "Cleaning $d"; Remove-Item -Recurse -Force $d }
        }
    }
}

$netconvert = Join-Path $sumoInstall 'bin\netconvert.exe'
if (Test-Path $netconvert) {
    Write-Host "Found SUMO netconvert at `"$netconvert`". Skipping SUMO build."
} else {
    Write-Host 'Building SUMO netconvert...'

    if (-not (Test-Path $sumoLibs)) {
        Write-Host "Cloning SUMOLibraries prebuilt Windows deps, pinned $sumoLibsTag (~3 GB, one-time)..."
        Invoke-Checked 'git clone SUMOLibraries' {
            git clone --depth 1 --branch $sumoLibsTag https://github.com/DLR-TS/SUMOLibraries.git $sumoLibs
        }
    } else {
        # Self-heal an existing clone at the wrong commit (e.g. an old unpinned HEAD clone),
        # since a mismatched bundle layout silently breaks the build (see note above).
        $libsHead = (git -C $sumoLibs rev-parse HEAD 2>$null | Out-String).Trim()
        if ($libsHead -ne $sumoLibsPin) {
            Write-Host "SUMOLibraries is at '$libsHead'; re-pinning to $sumoLibsTag ($sumoLibsPin)..."
            Invoke-Checked 'git fetch SUMOLibraries pin' {
                git -C $sumoLibs fetch --depth 1 origin $sumoLibsPin
            }
            Invoke-Checked 'git checkout SUMOLibraries pin' {
                git -C $sumoLibs checkout --force $sumoLibsPin
            }
        } else {
            Write-Host "SUMOLibraries already pinned to $sumoLibsTag."
        }
    }
    if (-not (Test-Path $sumoSrc)) {
        Write-Host 'Cloning SUMO v1_27_0...'
        Invoke-Checked 'git clone sumo' {
            git clone --depth 1 --branch v1_27_0 https://github.com/eclipse-sumo/sumo.git $sumoSrc
        }
    }
    # Pin the exact commit (the tag already points here; this is an explicit guard).
    Invoke-Checked 'git checkout sumo pin' {
        git -C $sumoSrc checkout $sumoSrcPin
    }

    # Configure + build ONLY the netconvert target (Release) with the VS generator.
    $env:SUMO_LIBRARIES = $sumoLibs
    Invoke-Checked 'cmake configure sumo' {
        cmake -B $sumoBuild -S $sumoSrc -G $cmakeGenerator `
            -T v143,version=14.44 -A x64 -DCHECK_OPTIONAL_LIBS=false
    }
    Invoke-Checked 'cmake build netconvert' {
        cmake --build $sumoBuild --target netconvert --config Release -- -m
    }

    # The build emits netconvert.exe + its runtime DLLs into Build\sumo-src\bin.
    # Stage the binary, its DLLs, and the PROJ data (proj.db) for CarlaNet.
    $installBin = Join-Path $sumoInstall 'bin'
    New-Item -ItemType Directory -Force -Path $installBin | Out-Null
    Copy-Item -Force (Join-Path $sumoSrc 'bin\netconvert.exe') $installBin
    Copy-Item -Force (Join-Path $sumoSrc 'bin\*.dll') $installBin
    # PROJ data (proj.db); glob the version dir so a bundle proj bump doesn't break staging.
    $projDir = Get-ChildItem (Join-Path $sumoLibs 'proj-*') -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | Select-Object -First 1
    if (-not $projDir) { throw "Could not find a proj-* directory under `"$sumoLibs`"." }
    $projSrc = Join-Path $projDir.FullName 'share\proj'
    $projDst = Join-Path $sumoInstall 'share\proj'
    New-Item -ItemType Directory -Force -Path $projDst | Out-Null
    Copy-Item -Recurse -Force (Join-Path $projSrc '*') $projDst
    Write-Host "Staged netconvert at `"$netconvert`"."
}

# CarlaNet locates the tool via env vars (see NETCONVERT_INTEGRATION.md):
#   CARLA_NETCONVERT  -> the netconvert binary
#   PROJ_LIB / PROJ_DATA -> the directory containing proj.db
Write-Host 'To use netconvert from CarlaNet, set:'
Write-Host "  `$env:CARLA_NETCONVERT = '$netconvert'"
Write-Host "  `$env:PROJ_LIB = '$(Join-Path $sumoInstall 'share\proj')'"

# ---------------------------------------------------------------------------
# VibeUE editor MCP plugin (OPTIONAL, private mirror, pinned)
# ---------------------------------------------------------------------------
# In-editor MCP bridge for digital-twin development: a PRIVATE mirror of
# kevinpbuckley/VibeUE with the vibeue.com API-key validation removed (offline).
# NOT referenced by the .uproject, so it is an optional auto-discovered plugin and
# the CARLA build proceeds without it. Pinned to an exact commit; fetched over SSH
# with a key from -VibeUeSshKey <path> or $env:VIBEUE_SSH_KEY.

$vibeueDir  = Join-Path $RepoRoot 'Unreal\CarlaUnreal\Plugins\VibeUE'
$vibeueRepo = 'git@github.com:sbrett9/VibeUE.git'
$vibeuePin  = '379373709e68ce7f2c4e3a26ff931f703d87b817'

if (Test-Path (Join-Path $vibeueDir '.git')) {
    if ($vibeueKey) { $env:GIT_SSH_COMMAND = "ssh -i $vibeueKey -o IdentitiesOnly=yes" }
    Write-Host "Pinning VibeUE to $vibeuePin..."
    git -C $vibeueDir fetch origin
    git -C $vibeueDir checkout $vibeuePin
} elseif ($vibeueKey) {
    Write-Host "Cloning VibeUE private mirror (pinned $vibeuePin)..."
    if (Test-Path $vibeueDir) { Remove-Item -Recurse -Force $vibeueDir }
    $env:GIT_SSH_COMMAND = "ssh -i $vibeueKey -o IdentitiesOnly=yes"
    git clone $vibeueRepo $vibeueDir
    git -C $vibeueDir checkout $vibeuePin
} elseif (Test-Path $vibeueDir) {
    Write-Host 'VibeUE present as a non-git copy; leaving as-is (no SSH key to convert it to a pinned clone).'
} else {
    Write-Host 'VibeUE skipped (optional MCP plugin). Pass -VibeUeSshKey <path> or set VIBEUE_SSH_KEY to fetch it.'
}

# ---------------------------------------------------------------------------
# BUILD CARLA
# ---------------------------------------------------------------------------

# Clear the CARLA CMake cache if requested, or if it was configured with a different
# compiler than the one just activated. A cached compiler is never re-detected by CMake,
# so switching VS versions otherwise mixes e.g. VS2022 14.38 cl.exe with VS2026 14.44 STL
# headers -> STL1001 "Unexpected compiler version".
$carlaCache = Join-Path $RepoRoot 'Build\CMakeCache.txt'
if ($CleanCarla) {
    Write-Host 'Clearing CARLA CMake cache (-CleanCarla)...'
    Clear-CarlaCmakeCache
} elseif (Test-Path $carlaCache) {
    $activeCl = (Get-Command cl.exe -ErrorAction SilentlyContinue).Source
    $cacheLine = Select-String -LiteralPath $carlaCache -Pattern '^CMAKE_CXX_COMPILER:' | Select-Object -First 1
    $cachedCl = if ($cacheLine -and $cacheLine.Line -match '=(.+)$') { $matches[1] } else { $null }
    if ($activeCl -and $cachedCl) {
        $normActive = $activeCl.Replace('\', '/').ToLowerInvariant().Trim()
        $normCached = $cachedCl.Replace('\', '/').ToLowerInvariant().Trim()
        if ($normActive -ne $normCached) {
            Write-Warning "CARLA cache was configured with a different compiler than the active one:"
            Write-Warning "  cached: $cachedCl"
            Write-Warning "  active: $activeCl"
            Write-Host 'Clearing CARLA CMake cache to re-detect the compiler...'
            Clear-CarlaCmakeCache
        }
    }
}

Write-Host 'Configuring the CARLA CMake project...'
$carlaConfigureArgs = @(
    '-G', 'Ninja'
    '-S', '.'
    '-B', 'Build'
    '--toolchain=CMake/Toolchain.cmake'
    '-DCMAKE_BUILD_TYPE=Release'
    "-DCARLA_UNREAL_ENGINE_PATH=$env:CARLA_UNREAL_ENGINE_PATH"
)
if ($PythonRoot) {
    $carlaConfigureArgs += "-DPython_ROOT_DIR=$PythonRoot"
    $carlaConfigureArgs += "-DPython3_ROOT_DIR=$PythonRoot"
}
if (-not $WithPythonApi) {
    # DEFAULT: CarlaNet-only. Skip the legacy Boost.Python `carla` extension -- it is
    # independent of the UE editor and CarlaNet, and its numpy<2 / Python<=3.12 build
    # constraints are irrelevant to the pure-Python `carlanet` shim. Pass -WithPythonApi
    # to build it.
    Write-Host 'Legacy PythonAPI disabled by default (-DBUILD_PYTHON_API=OFF). Pass -WithPythonApi to build it.'
    $carlaConfigureArgs += '-DBUILD_PYTHON_API=OFF'
}
if (-not $WithTests) {
    # CarlaNet-only: skip LibCarla's C++ unit tests. They pull in googletest, which is built
    # with /Wall + /WX and fails under VS2026's stricter STL (C4710/C4711/... become errors).
    Write-Host 'LibCarla tests disabled by default (-DBUILD_LIBCARLA_TESTS=OFF). Pass -WithTests to build them.'
    $carlaConfigureArgs += '-DBUILD_LIBCARLA_TESTS=OFF'
}
# The StreetMap UE plugin is fetched by CARLA into Unreal\CarlaUnreal\Plugins\StreetMap.
# If it is already present in-tree, reuse it instead of letting FetchContent re-download:
# its archive ref can drift/404 upstream, and a cache clear (-CleanCarla) forces a
# re-populate. FETCHCONTENT_SOURCE_DIR_<NAME> tells CMake the source is already provided,
# so it skips the download and never clobbers the existing checkout.
$streetMapDir = Join-Path $RepoRoot 'Unreal\CarlaUnreal\Plugins\StreetMap'
if ((Test-Path $streetMapDir) -and (Get-ChildItem -Force $streetMapDir -ErrorAction SilentlyContinue | Select-Object -First 1)) {
    Write-Host 'Reusing existing in-tree StreetMap plugin (skipping FetchContent download).'
    $carlaConfigureArgs += "-DFETCHCONTENT_SOURCE_DIR_STREETMAP=$($streetMapDir.Replace('\', '/'))"
}
Invoke-Checked 'cmake configure carla' { cmake @carlaConfigureArgs }

Write-Host 'Building CARLA...'
Invoke-Checked 'cmake build carla' { cmake --build Build }

if ($WithPythonApi) {
    Write-Host 'Installing Python API...'
    Invoke-Checked 'cmake build carla-python-api-install' { cmake --build Build --target carla-python-api-install }
    Write-Host 'CARLA Python API build+install succeeded.'
} else {
    Write-Host 'CARLA build succeeded (legacy PythonAPI skipped).'
}

# ---------------------------------------------------------------------------
# POST-BUILD STEPS
# ---------------------------------------------------------------------------

if ($Launch) {
    Write-Host 'Launching Carla Unreal Editor...'
    Invoke-Checked 'cmake build launch' { cmake --build Build --target launch }
}
