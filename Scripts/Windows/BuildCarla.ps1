#Requires -Version 5.1
<#
.SYNOPSIS
    Build CarlaUnrealEditor (C++) and/or CarlaNet (.NET) + the carlanet & carlacontrol Python wheels.

.DESCRIPTION
    Three independent stages:
      1) Unreal  — compiles CarlaUnrealEditor (the Carla plugin, CesiumCarlaBridge, ...)
                   via the UE 5.7.4 Build.bat.
      2) CarlaNet — builds the .NET libcarla replacement and the carlanet Python wheel
                   via CarlaNet/python/build_wheel.ps1.
      3) CarlaControl — builds the carlacontrol Python wheel (the run_SCTMV.py client
                   package) via CarlaControl/build_wheel.ps1.
    CarlaNet and CarlaControl run even if the Unreal build failed, so you still get full diagnostics.

    The CARLA repo root is derived from this script's location (carla/Scripts/Windows/),
    two directories up -- it never needs to be passed. The UE engine is found via
    -UnrealEngineRoot, then $env:CARLA_UNREAL_ENGINE_PATH, then <repo-parent>\UE_5_7_4.

.PARAMETER SkipUnreal
    Skip the CarlaUnrealEditor C++ build.
.PARAMETER CleanUnreal
    Force a full from-scratch editor rebuild: delete the project's and every plugin's
    Intermediate and Binaries (UBT outputs + UHT-generated headers) before building, so UBT
    recompiles everything. Preserves Plugins\CesiumForUnreal\Source\ThirdParty, so the already-
    built cesium-native (vcpkg) is NOT rebuilt -- only the C++ modules recompile. Close any
    running editor first (locked binaries can't be deleted).
.PARAMETER SkipCarlaNet
    Skip the CarlaNet (.NET) build + wheel.
.PARAMETER SkipCarlaControl
    Skip the CarlaControl (carlacontrol) Python wheel.
.PARAMETER InstallWheel
    Also pip-install the freshly built wheels (--force-reinstall). Applies to both the
    carlanet and carlacontrol wheels.
.PARAMETER CleanWheel
    Wipe each package's build artifacts (build/, dist/, *.egg-info; carlanet also \dlls) before
    building the wheels, by passing -Clean through to the build_wheel.ps1 scripts. Use this when a
    stale or corrupted build dir is producing a bad/failed wheel under -InstallWheel.
.PARAMETER Vs
    Force a Visual Studio toolchain: '2022' or '2026'. If omitted, uses the
    newest installed VS that has MSVC 14.44 (or current VS dev prompt if active).
.PARAMETER UnrealEngineRoot
    UE 5.7.4 source-build root. Env: CARLA_UNREAL_ENGINE_PATH.
    Default: <repo-parent>\UE_5_7_4.
.PARAMETER MaxParallelActions
    Maximum compile/link actions UBT runs in parallel for the editor build (UBT's
    -MaxParallelActions). Default 4 (conservative). Raise it to use more cores, e.g.
    -MaxParallelActions 16, or pass 0 to omit the cap entirely and let UBT choose from CPU/RAM.

.EXAMPLE
    .\BuildCarla.ps1 -InstallWheel
.EXAMPLE
    .\BuildCarla.ps1 -SkipUnreal -InstallWheel -CleanWheel   # clean-rebuild + install just the wheel
.EXAMPLE
    .\BuildCarla.ps1 -Vs 2026               # build the editor with the VS2026 toolchain
.EXAMPLE
    .\BuildCarla.ps1 -SkipUnreal            # just rebuild the CarlaNet wheel
.EXAMPLE
    Get-Help .\BuildCarla.ps1 -Detailed     # full usage (PowerShell's -? / -Detailed)
#>
# PositionalBinding=$false so stray tokens (e.g. the legacy `--help`) can't silently bind
# to a parameter; they land in $Remaining and are normalized below. This matches
# CarlaSetup.ps1, so both PowerShell-native (-Vs 2026) and legacy (--vs=2026) styles work.
[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$Vs,              # force VS toolchain: '2022' or '2026'; omit to auto-detect
    [switch]$SkipUnreal,      # skip the CarlaUnrealEditor C++ build
    [switch]$CleanUnreal,     # wipe editor Intermediate/Binaries first for a full from-scratch rebuild
    [switch]$SkipCarlaNet,    # skip the CarlaNet (.NET) build + wheel
    [switch]$SkipCarlaControl,# skip the CarlaControl (carlacontrol) Python wheel
    [switch]$InstallWheel,    # also pip-install the freshly built wheels (--force-reinstall)
    [switch]$CleanWheel,      # wipe CarlaNet\python build artifacts (build/dist/dlls/egg-info) first
    [string]$UnrealEngineRoot,# UE 5.7.4 root; env CARLA_UNREAL_ENGINE_PATH
    [int]$MaxParallelActions = 4, # cap parallel UBT editor-build actions; 0 = uncapped (UBT decides)

    [Alias('h')]
    [switch]$Help,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Remaining
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Show-Usage {
    @'
BuildCarla.ps1 - build CarlaUnrealEditor (C++) and/or CarlaNet (.NET) + the carlanet & carlacontrol wheels.

USAGE:
  .\BuildCarla.ps1 [options]

OPTIONS (PowerShell-native | legacy alias):
  -Vs <2022|2026>            --vs=<2022|2026>            VS toolchain for the UE build (MSVC 14.44).
                                                         Omit to use the newest installed VS.
  -SkipUnreal                --skip-unreal               Skip the CarlaUnrealEditor C++ build.
  -CleanUnreal               --clean-unreal / --rebuild  Wipe Intermediate/Binaries first (full editor rebuild; keeps cesium-native).
  -SkipCarlaNet              --skip-carlanet             Skip the CarlaNet (.NET) build + wheel.
  -SkipCarlaControl          --skip-carlacontrol         Skip the CarlaControl (carlacontrol) Python wheel.
  -InstallWheel              --install-wheel             pip-install the freshly built wheels (carlanet + carlacontrol).
  -CleanWheel                --clean-wheel               Wipe each package's build/dist/dlls before building the wheels.
  -UnrealEngineRoot <dir>    --unreal-engine-root=<dir>  UE 5.7.4 source-build root.
  -MaxParallelActions <n>    --max-parallel-actions=<n>  Cap parallel editor-build actions (default 4; 0 = uncapped).
  -Help               / -h   --help                      Show this help.

EXAMPLES:
  .\BuildCarla.ps1 -Vs 2026
  .\BuildCarla.ps1 -SkipUnreal
'@ | Write-Host
}

# -- Normalize legacy "--flag" / "--flag=value" arguments (matches CarlaSetup.ps1) ----------
# Tokens PowerShell couldn't bind natively arrive in $Remaining. Walk them (supporting both
# "--key=value" and "--key value") and fold them onto the real parameters.
if ($Remaining) {
    for ($idx = 0; $idx -lt $Remaining.Count; $idx++) {
        $arg = $Remaining[$idx]
        if ($arg -match '^(--[^=]+)=(.*)$') { $key = $matches[1]; $val = $matches[2] }
        else { $key = $arg; $val = $null }
        if ($null -ne $val) { $next = $val }
        elseif ($idx + 1 -lt $Remaining.Count) { $next = $Remaining[$idx + 1] }
        else { $next = $null }
        switch -Regex ($key) {
            '^(--help|/\?|help)$'                  { $Help = $true }
            '^(--skip-unreal)$'                    { $SkipUnreal = $true }
            '^(--clean-unreal|--rebuild)$'         { $CleanUnreal = $true }
            '^(--skip-carlanet|--skip-carla-net)$' { $SkipCarlaNet = $true }
            '^(--skip-carlacontrol|--skip-carla-control)$' { $SkipCarlaControl = $true }
            '^(--install-wheel)$'                  { $InstallWheel = $true }
            '^(--clean-wheel)$'                    { $CleanWheel = $true }
            '^(--vs)$'                             { if ($null -eq $next) { throw "Argument '$key' requires a value." } $Vs = $next;               if ($null -eq $val) { $idx++ } }
            '^(--unreal-engine-root|--ue-root)$'   { if ($null -eq $next) { throw "Argument '$key' requires a value." } $UnrealEngineRoot = $next; if ($null -eq $val) { $idx++ } }
            '^(--max-parallel-actions|--max-parallel)$' { if ($null -eq $next) { throw "Argument '$key' requires a value." } $MaxParallelActions = [int]$next; if ($null -eq $val) { $idx++ } }
            default { Show-Usage; throw "Unknown argument '$arg'." }
        }
    }
}

if ($Help) { Show-Usage; return }

# Validate -Vs parameter
if ($Vs -and $Vs -notin @('2022', '2026')) {
    throw "Invalid -Vs value '$Vs'. Expected 2022 or 2026."
}

# ── Console colour convention ───────────────────────────────────────────────
# green = info/success, yellow = warning (use Write-Warning), red = error/failure.
# (Native tools like pip/dotnet colour their own output; these only affect this script's messages.)
function Write-Info { param([Parameter(ValueFromPipeline)][string]$Message) Write-Host $Message -ForegroundColor Green }
function Write-Fail { param([Parameter(ValueFromPipeline)][string]$Message) Write-Host $Message -ForegroundColor Red }

# ── Visual Studio Detection & Activation Helpers ────────────────────────────
# Simplified from CarlaSetup.ps1 to auto-activate VS with MSVC 14.44 toolset.

function Get-VsWhere {
    $candidate = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $candidate) { return $candidate }
    $cmd = Get-Command vswhere.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw "vswhere.exe not found. A Visual Studio 2022/2026 installation is required."
}

function ConvertTo-VsYear {
    param([int]$Major)
    switch ($Major) {
        17 { @{ Year = '2022'; Generator = 'Visual Studio 17 2022' } }
        18 { @{ Year = '2026'; Generator = 'Visual Studio 18 2026' } }
        default { $null }
    }
}

function Get-VsInstalls {
    $vswhere = Get-VsWhere
    $raw = & $vswhere -all -prerelease -products * -format json | ConvertFrom-Json
    $result = @()
    foreach ($inst in $raw) {
        $major = [int]($inst.installationVersion.Split('.')[0])
        $map = ConvertTo-VsYear -Major $major
        if (-not $map) { continue }

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
            Version      = [version]$inst.installationVersion
            Path         = $inst.installationPath
            Vcvars       = $vcvars
            HasToolset   = ($null -ne $toolset -and (Test-Path $vcvars))
            Toolset      = if ($toolset) { $toolset.Name } else { $null }
            IsBuildTools = ($productId -like '*BuildTools*')
        }
    }
    $result | Sort-Object @{ Expression = 'Version'; Descending = $true },
                          @{ Expression = 'IsBuildTools' },
                          @{ Expression = 'Path' }
}

function Import-VcVars {
    param(
        [Parameter(Mandatory)][string]$Vcvars,
        [string]$ToolsetVersion
    )
    $vsWhereDir = Split-Path -Parent (Get-VsWhere)
    $verArg     = if ($ToolsetVersion) { " -vcvars_ver=$ToolsetVersion" } else { '' }
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
            $eq = $line.IndexOf('=')
            if ($eq -gt 0) {
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

function Test-VsAlreadyActive {
    param($Pick)
    if (-not $env:VCINSTALLDIR -or -not $env:VCToolsVersion) { return $false }
    if ($env:VCToolsVersion -ne $Pick.Toolset) { return $false }
    $active = $env:VCINSTALLDIR.Replace('\', '/').TrimEnd('/').ToLowerInvariant()
    $want   = $Pick.Path.Replace('\', '/').TrimEnd('/').ToLowerInvariant()
    return $active.StartsWith($want)
}

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
            Write-Info "VS$($pick.Year) (MSVC $($pick.Toolset)) already active in this shell; skipping re-activation."
            return $pick
        }
        Write-Info "Activating requested VS$($pick.Year) at `"$($pick.Path)`" (pinning MSVC $($pick.Toolset))..."
        Import-VcVars -Vcvars $pick.Vcvars -ToolsetVersion $pick.Toolset
        return $pick
    }

    # Honor an already-active dev prompt if it has 14.44.
    if ($env:VSINSTALLDIR) {
        $current = $usable | Where-Object { $_.Path.TrimEnd('\') -eq $env:VSINSTALLDIR.TrimEnd('\') } |
            Select-Object -First 1
        if ($current) {
            Write-Info "Using active VS dev environment: VS$($current.Year) (MSVC $($current.Toolset))."
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
        Write-Info "VS$($pick.Year) (MSVC $($pick.Toolset)) already active; skipping re-activation."
        return $pick
    }
    Write-Info "Activating VS$($pick.Year) at `"$($pick.Path)`" (pinning MSVC $($pick.Toolset))..."
    Import-VcVars -Vcvars $pick.Vcvars -ToolsetVersion $pick.Toolset
    return $pick
}

# ── Paths: the CARLA repo root is two dirs up from this script (carla/Scripts/Windows),
# derived by LOCATION (not by folder name), so it survives a renamed/relocated checkout.
# The UE engine is a sibling of the repo: -UnrealEngineRoot > $env:CARLA_UNREAL_ENGINE_PATH
# > <repo-parent>\UE_5_7_4.
$CarlaRoot  = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$RepoParent = Split-Path $CarlaRoot -Parent
if (-not $UnrealEngineRoot) { $UnrealEngineRoot = $env:CARLA_UNREAL_ENGINE_PATH }
if (-not $UnrealEngineRoot) { $UnrealEngineRoot = Join-Path $RepoParent "UE_5_7_4" }

# ── Activate Visual Studio toolchain (required for UE Build.bat) ─────────────
$script:VsYear = $null
$script:VsToolset = $null
if (-not $SkipUnreal) {
    Write-Info "`nActivating Visual Studio toolchain..."
    $vsInfo = Initialize-VisualStudio -Wanted $Vs
    $script:VsYear = $vsInfo.Year
    $script:VsToolset = $vsInfo.Toolset
    Write-Info "Ready: VS$($vsInfo.Year), MSVC $($vsInfo.Toolset)`n"
}

$UE_ROOT         = $UnrealEngineRoot
$CARLA_UPROJECT  = Join-Path $CarlaRoot "Unreal\CarlaUnreal\CarlaUnreal.uproject"
$LOG_FILE        = Join-Path $RepoParent "Carla_build.log"
$CARLANET_WHEEL  = Join-Path $CarlaRoot "CarlaNet\python\build_wheel.ps1"
$CARLACONTROL_WHEEL = Join-Path $CarlaRoot "CarlaControl\build_wheel.ps1"

Write-Info "CARLA repo: $CarlaRoot"
Write-Info "UE engine : $UE_ROOT"
"Build started: $(Get-Date)" | Set-Content $LOG_FILE

$ueResult  = 0   # 0 = success/skipped
$netResult = 0
$ctlResult = 0

# ============================================================================
#  1) Unreal — CarlaUnrealEditor (C++: Carla plugin, CesiumCarlaBridge, etc.)
# ============================================================================
if (-not $SkipUnreal) {
    Write-Info "============================================================"
    Write-Info " Building CarlaUnrealEditor - Development Win64"
    Write-Info " Log: $LOG_FILE"
    Write-Info "============================================================"

    $BuildBat = Join-Path $UE_ROOT "Engine\Build\BatchFiles\Build.bat"
    if (-not (Test-Path $BuildBat))       { throw "UE Build.bat not found: $BuildBat (set -UnrealEngineRoot or `$env:CARLA_UNREAL_ENGINE_PATH)" }
    if (-not (Test-Path $CARLA_UPROJECT)) { throw "CarlaUnreal.uproject not found: $CARLA_UPROJECT" }

    if ($CleanUnreal) {
        Write-Info "[clean] Full rebuild: removing editor Intermediate/Binaries (close any running editor first)..."
        # Project UBT outputs + UHT-generated headers, plus each plugin's. Source\ThirdParty is
        # under Source\ (not touched), so the built cesium-native is preserved; only C++ recompiles.
        $cleanRoots = @(
            (Join-Path $CarlaRoot 'Unreal\CarlaUnreal\Intermediate'),
            (Join-Path $CarlaRoot 'Unreal\CarlaUnreal\Binaries')
        )
        $pluginsDir = Join-Path $CarlaRoot 'Unreal\CarlaUnreal\Plugins'
        if (Test-Path $pluginsDir) {
            foreach ($pl in (Get-ChildItem $pluginsDir -Directory -ErrorAction SilentlyContinue)) {
                $cleanRoots += (Join-Path $pl.FullName 'Intermediate')
                $cleanRoots += (Join-Path $pl.FullName 'Binaries')
            }
        }
        foreach ($d in $cleanRoots) {
            if (Test-Path $d) { Write-Host "  removing $d"; Remove-Item -Recurse -Force $d }
        }
    }

    # Carla.Build.cs reads generated .def files (Definitions/Includes/Libraries/Options) from the
    # plugin dir. CarlaSetup's CMake configure generates them under Build\Unreal and the
    # carla-unreal-configure target links them into the plugin dir. We invoke Build.bat directly (not
    # the cmake carla-unreal-editor target), so run that configure step first -- otherwise UBT aborts
    # with "Could not find ... Definitions.def" on a checkout where the links don't exist yet. The
    # target is idempotent (CMake skips it when the links are already present).
    $carlaBuildDir = Join-Path $CarlaRoot 'Build'
    if (Test-Path $carlaBuildDir) {
        Write-Info "[unreal] ensuring Carla plugin .def files (carla-unreal-configure)..."
        $prevEAPcfg = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try { cmake --build $carlaBuildDir --target carla-unreal-configure 2>&1 | Tee-Object -FilePath $LOG_FILE -Append }
        finally { $ErrorActionPreference = $prevEAPcfg }
        if ($LASTEXITCODE -ne 0) { Write-Warning "carla-unreal-configure failed (exit $LASTEXITCODE); the editor build may fail on missing .def files." }
    } else {
        Write-Warning "$carlaBuildDir not found - run CarlaSetup.ps1 first; the editor build will fail on missing .def files."
    }

    # Relax EAP around the native UE build: UBT/Build.bat can emit benign stderr that EAP='Stop'
    # would promote to a terminating NativeCommandError (the 2>&1 surfaces it into the pipeline),
    # which would abort the script before the exit-code check below. The build's real success or
    # failure signal is its exit code; warnings stay warnings.
    $prevEAP = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $buildArgs = @(
            'CarlaUnrealEditor', 'Win64', 'Development',
            "$CARLA_UPROJECT",
            '-WaitMutex',
            "-$script:VsYear",
            "-CompilerVersion=$script:VsToolset",
            '-Unattended'
        )
        # 0 => omit the flag so UBT auto-scales to the machine; >0 => cap the parallel actions.
        if ($MaxParallelActions -gt 0) {
            $buildArgs += "-MaxParallelActions=$MaxParallelActions"
            Write-Info "Editor build parallelism capped at $MaxParallelActions actions (-MaxParallelActions 0 to uncap)."
        } else {
            Write-Info "Editor build parallelism uncapped (UBT auto-scales to CPU/RAM)."
        }
        & $BuildBat @buildArgs `
            2>&1 | ForEach-Object { $_ -replace "`0", "" } | Tee-Object -FilePath $LOG_FILE -Append

        $ueResult = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $prevEAP
    }

    if ($ueResult -eq 0) {
        "UNREAL BUILD SUCCEEDED - $(Get-Date)" | Add-Content $LOG_FILE
        Write-Info "`nUNREAL BUILD SUCCEEDED"
    } else {
        "UNREAL BUILD FAILED (exit code $ueResult) - $(Get-Date)" | Add-Content $LOG_FILE
        Write-Fail "`nUNREAL BUILD FAILED - exit code $ueResult"
    }
} else {
    Write-Info "Skipping Unreal build (-SkipUnreal)."
    "UNREAL BUILD SKIPPED - $(Get-Date)" | Add-Content $LOG_FILE
}

# ============================================================================
#  2) CarlaNet — .NET build + Python wheel (publishes DLLs into the shim,
#     then produces carlanet-*.whl). Independent of the Unreal build, so it
#     runs even if the C++ build failed (you still get full diagnostics).
# ============================================================================
if (-not $SkipCarlaNet) {
    Write-Info "`n============================================================"
    Write-Info " Building CarlaNet (.NET) + Python wheel"
    Write-Info "============================================================"

    if (-not (Test-Path $CARLANET_WHEEL)) {
        Write-Fail "CarlaNet wheel script not found: $CARLANET_WHEEL"
        "CARLANET BUILD FAILED (build_wheel.ps1 missing) - $(Get-Date)" | Add-Content $LOG_FILE
        $netResult = 1
    } else {
        try {
            # Forward switches to build_wheel.ps1. -Clean wipes its build/dist/dlls/egg-info
            # first, guarding against a corrupted CarlaNet\python\build dir producing a bad wheel.
            # NB: switches must be splatted via a HASHTABLE -- array splatting passes '-Install'
            # as a positional VALUE, which errors ("positional parameter cannot be found").
            $wheelArgs = @{}
            if ($InstallWheel) { $wheelArgs['Install'] = $true }
            if ($CleanWheel)   { $wheelArgs['Clean']   = $true }
            & $CARLANET_WHEEL @wheelArgs 2>&1 | Tee-Object -FilePath $LOG_FILE -Append
            # build_wheel.ps1 throws on any failure; reaching here means success.
            $netResult = 0
            "CARLANET BUILD SUCCEEDED - $(Get-Date)" | Add-Content $LOG_FILE
            Write-Info "`nCARLANET BUILD SUCCEEDED"
        } catch {
            $netResult = 1
            "CARLANET BUILD FAILED: $_ - $(Get-Date)" | Add-Content $LOG_FILE
            Write-Fail "`nCARLANET BUILD FAILED: $_"
        }
    }
} else {
    Write-Info "Skipping CarlaNet build (-SkipCarlaNet)."
    "CARLANET BUILD SKIPPED - $(Get-Date)" | Add-Content $LOG_FILE
}

# ============================================================================
#  3) CarlaControl — carlacontrol Python wheel (the run_SCTMV.py client package).
#     Pure Python, so it has no .NET/native step; independent of both builds above.
# ============================================================================
if (-not $SkipCarlaControl) {
    Write-Info "`n============================================================"
    Write-Info " Building CarlaControl (carlacontrol) Python wheel"
    Write-Info "============================================================"

    if (-not (Test-Path $CARLACONTROL_WHEEL)) {
        Write-Fail "CarlaControl wheel script not found: $CARLACONTROL_WHEEL"
        "CARLACONTROL BUILD FAILED (build_wheel.ps1 missing) - $(Get-Date)" | Add-Content $LOG_FILE
        $ctlResult = 1
    } else {
        try {
            # Same interface as CarlaNet's build_wheel.ps1: switches must be splatted via a
            # HASHTABLE (array splatting passes '-Install' as a positional value and errors).
            $ctlArgs = @{}
            if ($InstallWheel) { $ctlArgs['Install'] = $true }
            if ($CleanWheel)   { $ctlArgs['Clean']   = $true }
            & $CARLACONTROL_WHEEL @ctlArgs 2>&1 | Tee-Object -FilePath $LOG_FILE -Append
            # build_wheel.ps1 throws on any failure; reaching here means success.
            $ctlResult = 0
            "CARLACONTROL BUILD SUCCEEDED - $(Get-Date)" | Add-Content $LOG_FILE
            Write-Info "`nCARLACONTROL BUILD SUCCEEDED"
        } catch {
            $ctlResult = 1
            "CARLACONTROL BUILD FAILED: $_ - $(Get-Date)" | Add-Content $LOG_FILE
            Write-Fail "`nCARLACONTROL BUILD FAILED: $_"
        }
    }
} else {
    Write-Info "Skipping CarlaControl build (-SkipCarlaControl)."
    "CARLACONTROL BUILD SKIPPED - $(Get-Date)" | Add-Content $LOG_FILE
}

# ============================================================================
#  Summary
# ============================================================================
Write-Info "`n============================================================"
$ueFailed  = (-not $SkipUnreal)    -and ($ueResult  -ne 0)
$netFailed = (-not $SkipCarlaNet)  -and ($netResult -ne 0)
$ctlFailed = (-not $SkipCarlaControl) -and ($ctlResult -ne 0)
$ueLine  = " Unreal : {0}" -f $(if ($SkipUnreal)   { "skipped" } elseif ($ueResult  -eq 0) { "OK" } else { "FAILED ($ueResult)" })
$netLine = " CarlaNet: {0}" -f $(if ($SkipCarlaNet){ "skipped" } elseif ($netResult -eq 0) { "OK" } else { "FAILED ($netResult)" })
$ctlLine = " CarlaControl: {0}" -f $(if ($SkipCarlaControl){ "skipped" } elseif ($ctlResult -eq 0) { "OK" } else { "FAILED ($ctlResult)" })
if ($ueFailed)  { Write-Fail $ueLine }  else { Write-Info $ueLine }
if ($netFailed) { Write-Fail $netLine } else { Write-Info $netLine }
if ($ctlFailed) { Write-Fail $ctlLine } else { Write-Info $ctlLine }
Write-Info "============================================================"
Write-Info "Log: $LOG_FILE"
Write-Info "UBT detail: $UE_ROOT\Engine\Programs\UnrealBuildTool\Log.txt"

$final = if (($ueResult -ne 0) -or ($netResult -ne 0) -or ($ctlResult -ne 0)) { 1 } else { 0 }
exit $final
