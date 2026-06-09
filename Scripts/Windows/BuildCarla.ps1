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

    Paths are resolved in priority order: explicit parameters, then environment
    variables, then defaults derived from this script's location. This script lives at
    carla/Scripts/Windows/, so the workspace root is three directories up.

.PARAMETER SkipUnreal
    Skip the CarlaUnrealEditor C++ build.
.PARAMETER SkipCarlaNet
    Skip the CarlaNet (.NET) build + wheel.
.PARAMETER InstallWheel
    Also pip-install the freshly built wheel (--force-reinstall).
.PARAMETER WorkspaceRoot
    Repo workspace root (the folder that contains 'carla' and the UE engine).
    Env: CARLA_WORKSPACE_ROOT. Default: three levels up from this script.
.PARAMETER UnrealEngineRoot
    UE 5.7.4 source-build root. Env: CARLA_UNREAL_ENGINE_PATH.
    Default: <WorkspaceRoot>\UE_5_7_4.

.EXAMPLE
    .\BuildCarla.ps1 -InstallWheel
.EXAMPLE
    .\BuildCarla.ps1 -SkipUnreal            # just rebuild the CarlaNet wheel
.EXAMPLE
    Get-Help .\BuildCarla.ps1 -Detailed     # full usage (PowerShell's -? / -Detailed)
#>
[CmdletBinding()]
param(
    [switch]$SkipUnreal,      # skip the CarlaUnrealEditor C++ build
    [switch]$SkipCarlaNet,    # skip the CarlaNet (.NET) build + wheel
    [switch]$InstallWheel,    # also pip-install the freshly built wheel (--force-reinstall)
    [string]$WorkspaceRoot,   # repo root (contains 'carla' + engine); env CARLA_WORKSPACE_ROOT
    [string]$UnrealEngineRoot # UE 5.7.4 root; env CARLA_UNREAL_ENGINE_PATH
)

# ── Path resolution: param > env var > default-from-script-location ──────────
if (-not $WorkspaceRoot)    { $WorkspaceRoot    = $env:CARLA_WORKSPACE_ROOT }
if (-not $WorkspaceRoot)    { $WorkspaceRoot    = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path }
if (-not $UnrealEngineRoot) { $UnrealEngineRoot = $env:CARLA_UNREAL_ENGINE_PATH }
if (-not $UnrealEngineRoot) { $UnrealEngineRoot = Join-Path $WorkspaceRoot "UE_5_7_4" }

$CarlaRoot       = Join-Path $WorkspaceRoot "carla"
$UE_ROOT         = $UnrealEngineRoot
$CARLA_UPROJECT  = Join-Path $CarlaRoot "Unreal\CarlaUnreal\CarlaUnreal.uproject"
$LOG_FILE        = Join-Path $WorkspaceRoot "Carla_build.log"
$CARLANET_WHEEL  = Join-Path $CarlaRoot "CarlaNet\python\build_wheel.ps1"

Write-Host "Workspace : $WorkspaceRoot"
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
    if (-not (Test-Path $CARLA_UPROJECT)) { throw "CarlaUnreal.uproject not found: $CARLA_UPROJECT (set -WorkspaceRoot or `$env:CARLA_WORKSPACE_ROOT)" }

    & $BuildBat `
        CarlaUnrealEditor Win64 Development `
        "$CARLA_UPROJECT" `
        -WaitMutex `
        -2022 `
        "-CompilerVersion=14.44.35207" `
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
