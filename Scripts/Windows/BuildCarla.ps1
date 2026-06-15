#Requires -Version 5.1
<#
.SYNOPSIS
    Build CarlaUnrealEditor (C++) and/or CarlaNet (.NET) + the carlanet Python wheel.

.DESCRIPTION
    Two independent stages:
      1) Unreal  — compiles CarlaUnrealEditor (the Carla plugin, CesiumCarlaBridge, ...)
                   via the UE 5.7.4 Build.bat.
      2) CarlaNet — builds the .NET libcarla replacement and the carlanet Python wheel
                   via CarlaNet/python/build_wheel.ps1.
    CarlaNet runs even if the Unreal build failed, so you still get full diagnostics.

    The CARLA repo root is derived from this script's location (carla/Scripts/Windows/),
    two directories up -- it never needs to be passed. The UE engine is found via
    -UnrealEngineRoot, then $env:CARLA_UNREAL_ENGINE_PATH, then <repo-parent>\UE_5_7_4.

.PARAMETER Vs
    Visual Studio toolchain for the UE build: '2022' or '2026' (must have MSVC 14.44,
    which is enforced). Omit to use the newest installed VS with MSVC 14.44. Discovered
    via vswhere and passed to UnrealBuildTool as -2022/-2026 (matches CarlaSetup.ps1).
.PARAMETER SkipUnreal
    Skip the CarlaUnrealEditor C++ build.
.PARAMETER SkipCarlaNet
    Skip the CarlaNet (.NET) build + wheel.
.PARAMETER InstallWheel
    Also pip-install the freshly built wheel (--force-reinstall).
.PARAMETER UnrealEngineRoot
    UE 5.7.4 source-build root. Env: CARLA_UNREAL_ENGINE_PATH.
    Default: <repo-parent>\UE_5_7_4.

.EXAMPLE
    .\BuildCarla.ps1 -InstallWheel
.EXAMPLE
    .\BuildCarla.ps1 -Vs 2026                # build the editor with the VS2026 toolchain
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
    [string]$Vs,              # VS toolchain for the UE build; omit = newest with MSVC 14.44
    [switch]$SkipUnreal,      # skip the CarlaUnrealEditor C++ build
    [switch]$SkipCarlaNet,    # skip the CarlaNet (.NET) build + wheel
    [switch]$InstallWheel,    # also pip-install the freshly built wheel (--force-reinstall)
    [string]$UnrealEngineRoot,# UE 5.7.4 root; env CARLA_UNREAL_ENGINE_PATH

    [Alias('h')]
    [switch]$Help,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Remaining
)

function Show-Usage {
    @'
BuildCarla.ps1 - build CarlaUnrealEditor (C++) and/or CarlaNet (.NET) + the carlanet wheel.

USAGE:
  .\BuildCarla.ps1 [options]

OPTIONS (PowerShell-native | legacy alias):
  -Vs <2022|2026>            --vs=<2022|2026>            VS toolchain for the UE build (MSVC 14.44).
                                                         Omit to use the newest installed VS.
  -SkipUnreal                --skip-unreal               Skip the CarlaUnrealEditor C++ build.
  -SkipCarlaNet              --skip-carlanet             Skip the CarlaNet (.NET) build + wheel.
  -InstallWheel              --install-wheel             pip-install the freshly built wheel.
  -UnrealEngineRoot <dir>    --unreal-engine-root=<dir>  UE 5.7.4 source-build root.
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
            '^(--help|/\?|help)$'                 { $Help = $true }
            '^(--skip-unreal)$'                   { $SkipUnreal = $true }
            '^(--skip-carlanet|--skip-carla-net)$' { $SkipCarlaNet = $true }
            '^(--install-wheel)$'                 { $InstallWheel = $true }
            '^(--vs)$'                            { if ($null -eq $next) { throw "Argument '$key' requires a value." } $Vs = $next;              if ($null -eq $val) { $idx++ } }
            '^(--unreal-engine-root|--ue-root)$'  { if ($null -eq $next) { throw "Argument '$key' requires a value." } $UnrealEngineRoot = $next; if ($null -eq $val) { $idx++ } }
            default { Show-Usage; throw "Unknown argument '$arg'." }
        }
    }
}

if ($Help) { Show-Usage; return }

# Validate -Vs manually (so --vs=foo gives a clear message instead of a binder error).
if ($Vs -and $Vs -notin @('2022', '2026')) {
    throw "Invalid -Vs value '$Vs'. Expected 2022 or 2026."
}

# Locate vswhere.exe (ships with the VS Installer since VS2017).
function Get-VsWhere {
    $c = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $c) { return $c }
    $cmd = Get-Command vswhere.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw 'vswhere.exe not found. A Visual Studio 2022/2026 installation is required.'
}

# Resolve the VS toolchain for UnrealBuildTool. Mirrors CarlaSetup.ps1's vswhere-based
# selection (VS2022/2026, MSVC 14.44 enforced). Returns the UBT -20xx flag + toolset version.
# UBT finds its own compiler from these flags, so no vcvars activation is needed here.
function Resolve-VsForUbt {
    param([string]$Wanted)   # '', '2022', or '2026'
    $raw = & (Get-VsWhere) -all -prerelease -products * -format json | ConvertFrom-Json
    $found = @()
    foreach ($inst in $raw) {
        $year = switch ([int]($inst.installationVersion.Split('.')[0])) { 17 { '2022' } 18 { '2026' } default { $null } }
        if (-not $year) { continue }
        $msvcRoot = Join-Path $inst.installationPath 'VC\Tools\MSVC'
        $toolset = if (Test-Path $msvcRoot) {
            Get-ChildItem $msvcRoot -Directory -Filter '14.44*' | Sort-Object Name -Descending | Select-Object -First 1
        } else { $null }
        if (-not $toolset) { continue }   # require MSVC 14.44
        $found += [pscustomobject]@{
            Year = $year; UbtFlag = "-$year"; Toolset = $toolset.Name
            Version = [version]$inst.installationVersion; Path = $inst.installationPath
        }
    }
    $found = $found | Sort-Object Version -Descending
    if ($Wanted) {
        $pick = $found | Where-Object Year -EQ $Wanted | Select-Object -First 1
        if (-not $pick) {
            $have = (($found | ForEach-Object { "VS$($_.Year)" }) -join ', ')
            throw "Requested Visual Studio $Wanted with MSVC 14.44 was not found. Detected: $have."
        }
        return $pick
    }
    $pick = $found | Select-Object -First 1
    if (-not $pick) { throw 'No Visual Studio 2022/2026 with MSVC toolset 14.44 was found.' }
    return $pick
}

# ── Paths: the CARLA repo root is two dirs up from this script (carla/Scripts/Windows).
# The UE engine is a sibling of the repo: -UnrealEngineRoot > $env:CARLA_UNREAL_ENGINE_PATH
# > <repo-parent>\UE_5_7_4.
$CarlaRoot   = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$RepoParent  = Split-Path $CarlaRoot -Parent
if (-not $UnrealEngineRoot) { $UnrealEngineRoot = $env:CARLA_UNREAL_ENGINE_PATH }
if (-not $UnrealEngineRoot) { $UnrealEngineRoot = Join-Path $RepoParent "UE_5_7_4" }

$UE_ROOT         = $UnrealEngineRoot
$CARLA_UPROJECT  = Join-Path $CarlaRoot "Unreal\CarlaUnreal\CarlaUnreal.uproject"
$LOG_FILE        = Join-Path $RepoParent "Carla_build.log"
$CARLANET_WHEEL  = Join-Path $CarlaRoot "CarlaNet\python\build_wheel.ps1"

Write-Host "CARLA repo: $CarlaRoot"
Write-Host "UE engine : $UE_ROOT"
"Build started: $(Get-Date)" | Set-Content $LOG_FILE

$ueResult  = 0   # 0 = success/skipped
$netResult = 0

# ============================================================================
#  1) Unreal — CarlaUnrealEditor (C++: Carla plugin, CesiumCarlaBridge, etc.)
# ============================================================================
if (-not $SkipUnreal) {
    Write-Host "============================================================"
    Write-Host " Building CarlaUnrealEditor - Development Win64"
    Write-Host " Log: $LOG_FILE"
    Write-Host "============================================================"

    $BuildBat = Join-Path $UE_ROOT "Engine\Build\BatchFiles\Build.bat"
    if (-not (Test-Path $BuildBat))       { throw "UE Build.bat not found: $BuildBat (set -UnrealEngineRoot or `$env:CARLA_UNREAL_ENGINE_PATH)" }
    if (-not (Test-Path $CARLA_UPROJECT)) { throw "CarlaUnreal.uproject not found: $CARLA_UPROJECT" }

    # NB: not $vs -- PowerShell var names are case-insensitive, so $vs would alias the
    # [string]-typed parameter $Vs and coerce this object to a string.
    $vsInfo = Resolve-VsForUbt -Wanted $Vs
    Write-Host " Toolchain: VS$($vsInfo.Year) (UBT $($vsInfo.UbtFlag)), MSVC $($vsInfo.Toolset)"

    & $BuildBat `
        CarlaUnrealEditor Win64 Development `
        "$CARLA_UPROJECT" `
        -WaitMutex `
        $vsInfo.UbtFlag `
        "-CompilerVersion=$($vsInfo.Toolset)" `
        -Unattended `
        -MaxParallelActions=4 `
        2>&1 | ForEach-Object { $_ -replace "`0", "" } | Tee-Object -FilePath $LOG_FILE -Append

    $ueResult = $LASTEXITCODE

    if ($ueResult -eq 0) {
        "UNREAL BUILD SUCCEEDED - $(Get-Date)" | Add-Content $LOG_FILE
        Write-Host "`nUNREAL BUILD SUCCEEDED"
    } else {
        "UNREAL BUILD FAILED (exit code $ueResult) - $(Get-Date)" | Add-Content $LOG_FILE
        Write-Host "`nUNREAL BUILD FAILED - exit code $ueResult"
    }
} else {
    Write-Host "Skipping Unreal build (-SkipUnreal)."
    "UNREAL BUILD SKIPPED - $(Get-Date)" | Add-Content $LOG_FILE
}

# ============================================================================
#  2) CarlaNet — .NET build + Python wheel (publishes DLLs into the shim,
#     then produces carlanet-*.whl). Independent of the Unreal build, so it
#     runs even if the C++ build failed (you still get full diagnostics).
# ============================================================================
if (-not $SkipCarlaNet) {
    Write-Host "`n============================================================"
    Write-Host " Building CarlaNet (.NET) + Python wheel"
    Write-Host "============================================================"

    if (-not (Test-Path $CARLANET_WHEEL)) {
        Write-Host "CarlaNet wheel script not found: $CARLANET_WHEEL"
        "CARLANET BUILD FAILED (build_wheel.ps1 missing) - $(Get-Date)" | Add-Content $LOG_FILE
        $netResult = 1
    } else {
        try {
            if ($InstallWheel) {
                & $CARLANET_WHEEL -Install 2>&1 | Tee-Object -FilePath $LOG_FILE -Append
            } else {
                & $CARLANET_WHEEL          2>&1 | Tee-Object -FilePath $LOG_FILE -Append
            }
            # build_wheel.ps1 throws on any failure; reaching here means success.
            $netResult = 0
            "CARLANET BUILD SUCCEEDED - $(Get-Date)" | Add-Content $LOG_FILE
            Write-Host "`nCARLANET BUILD SUCCEEDED"
        } catch {
            $netResult = 1
            "CARLANET BUILD FAILED: $_ - $(Get-Date)" | Add-Content $LOG_FILE
            Write-Host "`nCARLANET BUILD FAILED: $_"
        }
    }
} else {
    Write-Host "Skipping CarlaNet build (-SkipCarlaNet)."
    "CARLANET BUILD SKIPPED - $(Get-Date)" | Add-Content $LOG_FILE
}

# ============================================================================
#  Summary
# ============================================================================
Write-Host "`n============================================================"
Write-Host (" Unreal : {0}" -f $(if ($SkipUnreal)   { "skipped" } elseif ($ueResult  -eq 0) { "OK" } else { "FAILED ($ueResult)" }))
Write-Host (" CarlaNet: {0}" -f $(if ($SkipCarlaNet){ "skipped" } elseif ($netResult -eq 0) { "OK" } else { "FAILED ($netResult)" }))
Write-Host "============================================================"
Write-Host "Log: $LOG_FILE"
Write-Host "UBT detail: $UE_ROOT\Engine\Programs\UnrealBuildTool\Log.txt"

$final = if (($ueResult -ne 0) -or ($netResult -ne 0)) { 1 } else { 0 }
exit $final
