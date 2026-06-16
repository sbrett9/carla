#Requires -Version 5.1
<#
.SYNOPSIS
    Launch the CARLA Unreal editor against the correct UE 5.7.4 engine, load the
    CarlaUnreal.uproject, and wait until the editor reports it is up.

.DESCRIPTION
    Designed as a repeatable workflow for the CARLA x Cesium digital-twin work.

    Non-blocking-by-contract: the editor is launched with -unattended, so any
    startup modal dialog (e.g. "modules are out of date - rebuild?", source-control
    login, etc.) is NOT shown and NOT allowed to block. Instead the engine either
    auto-proceeds or exits. This script then treats a blocked/unhealthy start as an
    OUTRIGHT FAILURE:
      * editor process exits before becoming ready  -> fail (non-zero exit)
      * a fatal/assert line appears in the log       -> kill + fail
      * readiness marker not seen within -TimeoutSec -> assume a modal hung it,
                                                        kill + fail

    Success = the editor log emits
        "Engine is initialized. Leaving FEngineLoop::Init()"
    which only happens after the module-load / modal-danger phase has passed.
    On success the editor is left running and the script exits 0.

.PARAMETER Build
    Build CarlaUnrealEditor (via .\BuildCarla.ps1) before launching, to avoid the
    stale-module prompt entirely. Aborts the open if the build fails.

.PARAMETER TimeoutSec
    Max seconds to wait for the readiness marker before declaring failure. Default 600.

.PARAMETER Force
    Launch even if an UnrealEditor process is already running.

.PARAMETER AllowModals
    Drop -unattended (vanilla interactive launch). Modals CAN then block; the
    timeout guard still applies. Use only if you specifically want prompt dialogs.
.PARAMETER UnrealEngineRoot
    UE 5.7.4 engine root. Resolution order: -UnrealEngineRoot > $env:CARLA_UNREAL_ENGINE_PATH
    > <repo-parent>\UE_5_7_4. The CARLA project is found relative to this script's location.

.EXAMPLE
    .\OpenCarlaEditor.ps1
.EXAMPLE
    .\OpenCarlaEditor.ps1 -Build -TimeoutSec 900
#>
# PositionalBinding=$false so stray tokens (e.g. legacy --flags) can't silently bind to a
# parameter; they land in $Remaining and are normalized below. Matches CarlaSetup/BuildCarla,
# so both PowerShell-native (-TimeoutSec 900) and legacy (--timeout-sec=900) styles work.
[CmdletBinding(PositionalBinding = $false)]
param (
    [switch]$Build,
    [int]$TimeoutSec = 600,
    [switch]$Force,
    [switch]$AllowModals,
    [string]$UnrealEngineRoot,   # UE 5.7.4 root; env CARLA_UNREAL_ENGINE_PATH

    [Alias('h')]
    [switch]$Help,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Remaining
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Show-Usage {
    @'
OpenCarlaEditor.ps1 - launch the CARLA Unreal editor and wait until it reports ready.

USAGE:
  .\OpenCarlaEditor.ps1 [options]

OPTIONS (PowerShell-native | legacy alias):
  -Build                     --build                     Build CarlaUnrealEditor (BuildCarla.ps1) before launching.
  -TimeoutSec <n>            --timeout-sec=<n>           Max seconds to wait for the ready marker (default 600).
  -Force                     --force                     Launch even if an UnrealEditor process is already running.
  -AllowModals               --allow-modals              Drop -unattended (let startup modals show).
  -UnrealEngineRoot <dir>    --unreal-engine-root=<dir>  UE 5.7.4 root (else CARLA_UNREAL_ENGINE_PATH, else <repo-parent>\UE_5_7_4).
  -Help               / -h   --help                      Show this help.

EXAMPLES:
  .\OpenCarlaEditor.ps1
  .\OpenCarlaEditor.ps1 -Build -TimeoutSec 900
'@ | Write-Host
}

# Normalize legacy "--flag" / "--flag=value" args (matches CarlaSetup/BuildCarla).
if ($Remaining) {
    for ($idx = 0; $idx -lt $Remaining.Count; $idx++) {
        $arg = $Remaining[$idx]
        if ($arg -match '^(--[^=]+)=(.*)$') { $key = $matches[1]; $val = $matches[2] }
        else { $key = $arg; $val = $null }
        if ($null -ne $val) { $next = $val }
        elseif ($idx + 1 -lt $Remaining.Count) { $next = $Remaining[$idx + 1] }
        else { $next = $null }
        switch -Regex ($key) {
            '^(--help|/\?|help)$'                { $Help = $true }
            '^(--build)$'                        { $Build = $true }
            '^(--force)$'                        { $Force = $true }
            '^(--allow-modals)$'                 { $AllowModals = $true }
            '^(--timeout-sec|--timeout)$'        { if ($null -eq $next) { throw "Argument '$key' requires a value." } $TimeoutSec = [int]$next;   if ($null -eq $val) { $idx++ } }
            '^(--unreal-engine-root|--ue-root)$' { if ($null -eq $next) { throw "Argument '$key' requires a value." } $UnrealEngineRoot = $next; if ($null -eq $val) { $idx++ } }
            default { Show-Usage; throw "Unknown argument '$arg'." }
        }
    }
}

if ($Help) { Show-Usage; return }

# --- Paths: CARLA repo root from THIS script's location (carla/Scripts/Windows), not hard-coded.
# Engine override: -UnrealEngineRoot > $env:CARLA_UNREAL_ENGINE_PATH > <repo-parent>\UE_5_7_4. ---
$CarlaRoot  = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$RepoParent = Split-Path $CarlaRoot -Parent
if (-not $UnrealEngineRoot) { $UnrealEngineRoot = $env:CARLA_UNREAL_ENGINE_PATH }
if (-not $UnrealEngineRoot) { $UnrealEngineRoot = Join-Path $RepoParent "UE_5_7_4" }

$UE_ROOT   = $UnrealEngineRoot
# [IO.Path]::Combine (pure string join) instead of Join-Path: Join-Path resolves the drive
# qualifier and would throw "Cannot find drive" for a bad -UnrealEngineRoot before we report it.
$EditorExe = [System.IO.Path]::Combine($UE_ROOT, "Engine\Binaries\Win64\UnrealEditor.exe")
$Uproject  = Join-Path $CarlaRoot "Unreal\CarlaUnreal\CarlaUnreal.uproject"
$ProjDir   = Split-Path $Uproject -Parent
$LogPath   = Join-Path $ProjDir "Saved\Logs\CarlaUnreal.log"

# Readiness / failure markers in the editor log
$ReadyPattern = 'Engine is initialized\. Leaving FEngineLoop::Init'
$FatalPattern = 'Fatal error|Assertion failed|LowLevelFatalError|=== Critical error|appError'

function Fail([string]$msg) {
    Write-Host "`n[OPEN-FAIL] $msg" -ForegroundColor Red
    exit 1
}

# Read the tail of a log file even while UE holds it open for writing.
function Get-LogTailText([string]$path, [int]$maxBytes = 262144) {
    if (-not (Test-Path $path)) { return "" }
    try {
        $fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Open,
              [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        try {
            $start = [Math]::Max(0, $fs.Length - $maxBytes)
            [void]$fs.Seek($start, [System.IO.SeekOrigin]::Begin)
            $sr = New-Object System.IO.StreamReader($fs)
            return $sr.ReadToEnd()
        } finally { $fs.Dispose() }
    } catch { return "" }
}

# --- Validate ---
# [IO.File]::Exists returns $false for a missing path -- including a non-existent DRIVE -- without
# throwing, so a bad -UnrealEngineRoot yields our message instead of "Cannot find drive".
if (-not [System.IO.File]::Exists($EditorExe)) { Fail "UnrealEditor.exe not found: $EditorExe`n           Set -UnrealEngineRoot or `$env:CARLA_UNREAL_ENGINE_PATH to your UE 5.7.4 root." }
if (-not [System.IO.File]::Exists($Uproject))  { Fail "CarlaUnreal.uproject not found: $Uproject" }

$existing = Get-Process -Name "UnrealEditor" -ErrorAction SilentlyContinue
if ($existing -and -not $Force) {
    Fail "An UnrealEditor process is already running (PID $($existing.Id -join ', ')). Use -Force to launch anyway."
}

# --- Optional pre-build (avoids the stale-module modal at its source) ---
if ($Build) {
    Write-Host "[1/3] Building CarlaUnrealEditor first..."
    # BuildCarla.ps1 is a sibling of this script in carla/Scripts/Windows/. Forward the resolved
    # engine root so the build targets the same UE as the launch.
    & (Join-Path $PSScriptRoot "BuildCarla.ps1") -UnrealEngineRoot $UE_ROOT
    if ($LASTEXITCODE -ne 0) { Fail "Build failed (exit $LASTEXITCODE); not opening editor." }
}

# --- Rotate the current log so we only watch THIS session ---
if (Test-Path $LogPath) {
    $stamp = Get-Date -Format "yyyy.MM.dd-HH.mm.ss"
    $rot   = Join-Path $ProjDir "Saved\Logs\CarlaUnreal-preopen-$stamp.log"
    try { Move-Item -LiteralPath $LogPath -Destination $rot -Force } catch { }
}

# --- Launch ---
$flags = @("`"$Uproject`"", "-nosplash", "-nopause")
if (-not $AllowModals) { $flags += "-unattended" }

Write-Host "[2/3] Launching CARLA editor"
Write-Host "      engine : $EditorExe"
Write-Host "      project: $Uproject"
Write-Host "      flags  : $($flags -join ' ')"

$proc = Start-Process -FilePath $EditorExe -ArgumentList $flags -PassThru
Write-Host "      PID    : $($proc.Id)"

# --- Wait for readiness, fail fast on anything else ---
Write-Host "[3/3] Waiting up to $TimeoutSec s for the editor to come up (watching $LogPath)..."
$deadline = (Get-Date).AddSeconds($TimeoutSec)

while ((Get-Date) -lt $deadline) {
    if ($proc.HasExited) {
        Fail "Editor process exited early (code $($proc.ExitCode)) before it was ready -- likely a failed/auto-declined module rebuild or a startup error. Inspect: $LogPath"
    }
    $tail = Get-LogTailText $LogPath
    if ($tail) {
        if ($tail -match $FatalPattern) {
            try { if (-not $proc.HasExited) { $proc.Kill() } } catch { }
            Fail "Editor hit a fatal error during startup. Inspect: $LogPath"
        }
        if ($tail -match $ReadyPattern) {
            Write-Host "`n[OPEN-OK] CARLA editor is up (PID $($proc.Id))." -ForegroundColor Green
            Write-Host "          Log: $LogPath"
            exit 0
        }
    }
    Start-Sleep -Milliseconds 1500
}

# Timed out -> assume a blocking modal and fail outright (per contract).
try { if (-not $proc.HasExited) { $proc.Kill() } } catch { }
Fail "Editor did not report ready within $TimeoutSec s -- assuming a blocking modal dialog. Killed PID $($proc.Id). Inspect: $LogPath"
