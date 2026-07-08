#Requires -Version 5.1
<#
.SYNOPSIS
    Launch a HEADLESS CARLA RPC server (no editor, no cook) for the digital-twin
    pipeline. Loads a CARLA map (default Town10HD_Opt -> CarlaGameMode), which
    starts the RPC server on the given port. Runs in the foreground and streams
    its log; Ctrl+C to stop. Drive it from another terminal with the carlanet
    Python client (e.g. test_cesium_heights.py, or generate_world_from_osm_with_elevation).

.DESCRIPTION
    This uses the editor binary in -game mode (fast: no packaging step). The Cesium
    tileset does NOT need to be pre-placed — the client spawns it at runtime via
    configure_cesium_georeference (UCesiumHeightSampler::ConfigureCesiumForOrigin).
    Async mode (default) keeps the world ticking so Cesium streams + samples resolve.

.PARAMETER Map
    Startup map. Default Town10HD_Opt (a CARLA episode). The digital-twin build
    then generate_opendrive_world's its way to OpenDriveMap; this is just the boot map.
.PARAMETER RpcPort
    CARLA RPC port (default 2000).
.PARAMETER WithWindow
    Show a window instead of -RenderOffScreen (useful to eyeball Cesium streaming).
.PARAMETER ExtraArgs
    Extra arguments appended verbatim to the UnrealEditor command line.
.PARAMETER UnrealEngineRoot
    UE 5.7.4 engine root. Resolution order: -UnrealEngineRoot > $env:CARLA_UNREAL_ENGINE_PATH
    > <repo-parent>\UE_5_7_4. The CARLA project is found relative to this script's location.

.EXAMPLE
    .\RunCarlaServer.ps1
.EXAMPLE
    .\RunCarlaServer.ps1 -RpcPort 3000 -WithWindow
.EXAMPLE
    Get-Help .\RunCarlaServer.ps1 -Detailed
#>
# PositionalBinding=$false so stray tokens (e.g. legacy --flags) can't silently bind to a
# parameter; they land in $Remaining and are normalized below. Matches CarlaSetup/BuildCarla,
# so both PowerShell-native (-RpcPort 3000) and legacy (--rpc-port=3000) styles work.
[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$Map      = "/Game/Carla/Maps/Town10HD_Opt",
    [int]$RpcPort     = 2000,
    [switch]$WithWindow,
    [string]$ExtraArgs = "",
    [string]$UnrealEngineRoot,   # UE 5.7.4 root; env CARLA_UNREAL_ENGINE_PATH
    [int]$CesiumCacheItems = 0,  # >0 overrides Cesium's tile request-cache size (MaxCacheItems) for this run; 0 = engine default (4096)

    [Alias('h')]
    [switch]$Help,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Remaining
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Show-Usage {
    @'
RunCarlaServer.ps1 - launch a headless CARLA RPC server (editor binary in -game mode).

USAGE:
  .\RunCarlaServer.ps1 [options]

OPTIONS (PowerShell-native | legacy alias):
  -Map <path>                --map=<path>                Startup map (default /Game/Carla/Maps/Town10HD_Opt).
  -RpcPort <n>               --rpc-port=<n>              CARLA RPC port (default 2000).
  -WithWindow                --with-window               Show a window instead of -RenderOffScreen.
  -ExtraArgs <str>           --extra-args=<str>          Extra args appended to the UnrealEditor command line.
  -CesiumCacheItems <n>      --cesium-cache-items=<n>    Override Cesium tile request-cache size (MaxCacheItems) for this run; 0/omit = engine default (4096).
  -UnrealEngineRoot <dir>    --unreal-engine-root=<dir>  UE 5.7.4 root (else CARLA_UNREAL_ENGINE_PATH, else <repo-parent>\UE_5_7_4).
  -Help               / -h   --help                      Show this help.

EXAMPLES:
  .\RunCarlaServer.ps1
  .\RunCarlaServer.ps1 -RpcPort 3000 -WithWindow
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
            '^(--with-window)$'                  { $WithWindow = $true }
            '^(--map)$'                          { if ($null -eq $next) { throw "Argument '$key' requires a value." } $Map = $next;             if ($null -eq $val) { $idx++ } }
            '^(--rpc-port)$'                     { if ($null -eq $next) { throw "Argument '$key' requires a value." } $RpcPort = [int]$next;     if ($null -eq $val) { $idx++ } }
            '^(--extra-args)$'                   { if ($null -eq $next) { throw "Argument '$key' requires a value." } $ExtraArgs = $next;       if ($null -eq $val) { $idx++ } }
            '^(--cesium-cache-items|--max-cache-items)$' { if ($null -eq $next) { throw "Argument '$key' requires a value." } $CesiumCacheItems = [int]$next; if ($null -eq $val) { $idx++ } }
            '^(--unreal-engine-root|--ue-root)$' { if ($null -eq $next) { throw "Argument '$key' requires a value." } $UnrealEngineRoot = $next; if ($null -eq $val) { $idx++ } }
            default { Show-Usage; throw "Unknown argument '$arg'." }
        }
    }
}

if ($Help) { Show-Usage; return }

# Paths: the CARLA repo root is two dirs up from this script (carla/Scripts/Windows), derived by
# LOCATION (not hard-coded). The UE engine: -UnrealEngineRoot > $env:CARLA_UNREAL_ENGINE_PATH >
# <repo-parent>\UE_5_7_4.
$CarlaRoot  = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$RepoParent = Split-Path $CarlaRoot -Parent
if (-not $UnrealEngineRoot) { $UnrealEngineRoot = $env:CARLA_UNREAL_ENGINE_PATH }
if (-not $UnrealEngineRoot) { $UnrealEngineRoot = Join-Path $RepoParent "UE_5_7_4" }

# [IO.Path]::Combine (pure string join) instead of Join-Path: Join-Path resolves the drive
# qualifier and would throw "Cannot find drive" for a bad -UnrealEngineRoot before we can
# report it nicely. $CarlaRoot is always a real, resolved path so Join-Path is fine there.
$UE       = [System.IO.Path]::Combine($UnrealEngineRoot, "Engine\Binaries\Win64\UnrealEditor.exe")
$Uproject = Join-Path $CarlaRoot "Unreal\CarlaUnreal\CarlaUnreal.uproject"

# [IO.File]::Exists returns $false for a missing path -- including a non-existent DRIVE -- without
# throwing, so a bad -UnrealEngineRoot yields our message instead of "Cannot find drive".
if (-not [System.IO.File]::Exists($UE))       { throw "UnrealEditor.exe not found: $UE`nSet -UnrealEngineRoot or `$env:CARLA_UNREAL_ENGINE_PATH to your UE 5.7.4 root." }
if (-not [System.IO.File]::Exists($Uproject)) { throw "CarlaUnreal.uproject not found: $Uproject" }

$ed = Get-Process -Name "UnrealEditor" -ErrorAction SilentlyContinue
if ($ed) { throw "An UnrealEditor process is already running (PID $($ed.Id -join ', ')). Close it first — two instances on one project conflict." }

$renderArg = if ($WithWindow) { "" } else { "-RenderOffScreen" }

# -game with the CARLA map => CarlaGameMode => FCarlaEngine starts the RPC server.
$argList = @(
    "`"$Uproject`"",
    "`"$Map`"",
    "-game",
    $renderArg,
    "-carla-rpc-port=$RpcPort",
    "-nosound",
    "-unattended",
    "-nopause"
) | Where-Object { $_ -ne "" }
# Per-run override of the Cesium tile request-cache size. MaxCacheItems is a CesiumRuntimeSettings
# value (Config = Engine), read once when the SQLite cache is first built at startup; the engine
# default (4096) is small for a large draped OSM sandbox. A command-line -ini: override is applied
# during config load — before the cache is built — so a fresh server launch honors it. 0 = leave default.
if ($CesiumCacheItems -gt 0) {
    $argList += "-ini:Engine:[/Script/CesiumRuntime.CesiumRuntimeSettings]:MaxCacheItems=$CesiumCacheItems"
}
if ($ExtraArgs) { $argList += $ExtraArgs }

Write-Host "============================================================"
Write-Host " Headless CARLA server"
Write-Host "   map      : $Map"
Write-Host "   rpc port : $RpcPort"
Write-Host "   render   : $(if ($WithWindow) {'windowed'} else {'-RenderOffScreen'})"
Write-Host "   cesium cache : $(if ($CesiumCacheItems -gt 0) {"$CesiumCacheItems items"} else {'engine default (4096)'})"
Write-Host "------------------------------------------------------------"
Write-Host " Once you see the server is up, in ANOTHER terminal run e.g.:"
Write-Host "   python carla\CarlaNet\python\test_cesium_heights.py"
Write-Host " Ctrl+C here to stop the server."
Write-Host "============================================================"
Write-Host "$UE $($argList -join ' ')"
Write-Host ""

# UnrealEditor.exe is a GUI-subsystem binary, so `& $UE` returns immediately (PowerShell
# does not wait for GUI processes). Launch DETACHED (no -NoNewWindow) so Ctrl+C reaches THIS
# script rather than being swallowed by Unreal, then poll in an interruptible loop. On Ctrl+C
# the loop is interrupted and `finally` stops the child cleanly.
$Log = Join-Path $CarlaRoot "Unreal\CarlaUnreal\Saved\Logs\CarlaUnreal.log"
$proc = Start-Process -FilePath $UE -ArgumentList $argList -PassThru
Write-Host "[server] PID $($proc.Id) launched; Ctrl+C here to stop it.`n"

# ── Readiness detection ──────────────────────────────────────────────────────
# Poll until the RPC port is accepting connections (engine takes 30-90 s to
# start). Print elapsed seconds so the user knows we're not hung.
Write-Host "[server] Waiting for RPC port $RpcPort to open on 127.0.0.1 ..."
$readyTimeout = 180   # seconds
$elapsed      = 0
$ready        = $false
while ($elapsed -le $readyTimeout) {
    if ($proc.HasExited) {
        Write-Host ""
        Write-Host "============================================================"
        Write-Host " SERVER CRASHED during startup (exit code $($proc.ExitCode)) after ${elapsed}s"
        Write-Host "============================================================"
        if (Test-Path $Log) {
            Write-Host "--- last 30 lines of $Log ---"
            Get-Content $Log -Tail 30 | ForEach-Object { $_ }
        }
        exit 1
    }

    $listening = Get-NetTCPConnection -LocalPort $RpcPort -State Listen -ErrorAction SilentlyContinue
    if ($listening) {
        $ready = $true
        break
    }

    Write-Host -NoNewline "`r[server] still starting... ${elapsed}s elapsed"
    Start-Sleep -Seconds 1
    $elapsed++
}

Write-Host ""   # newline after the \r progress line

if ($ready) {
    Write-Host "============================================================"
    Write-Host " SERVER READY — listening on 127.0.0.1:$RpcPort  (after ${elapsed}s)"
    Write-Host "============================================================"
    Write-Host ""
} else {
    Write-Host "============================================================"
    Write-Host " TIMEOUT — port $RpcPort not open after ${readyTimeout}s"
    Write-Host " The server may still be loading; watch the log below."
    Write-Host "============================================================"
    Write-Host ""
}
# ── End readiness detection ──────────────────────────────────────────────────

try {
    while (-not $proc.HasExited) {
        Start-Sleep -Milliseconds 300
    }
} finally {
    if (-not $proc.HasExited) {
        Write-Host "`n[server] stopping PID $($proc.Id) ..."
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        try { $proc.WaitForExit(8000) | Out-Null } catch {}
    }
}

$code = if ($proc.HasExited) { $proc.ExitCode } else { -1 }
Write-Host "`n============================================================"
Write-Host "[server] exited with code $code"
if (Test-Path $Log) {
    Write-Host "--- last 30 lines of $Log ---"
    Get-Content $Log -Tail 30 | ForEach-Object { $_ }
}
Write-Host "============================================================"
exit $code
