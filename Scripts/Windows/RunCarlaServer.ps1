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
#>
param(
    [string]$Map     = "/Game/Carla/Maps/Town10HD_Opt",
    [int]$RpcPort    = 2000,
    [switch]$WithWindow,
    [string]$ExtraArgs = ""
)
$ErrorActionPreference = "Stop"

$UE       = "G:\Projects\CarlaUE_5_7_4\UE_5_7_4\Engine\Binaries\Win64\UnrealEditor.exe"
$Uproject = "G:\Projects\CarlaUE_5_7_4\carla\Unreal\CarlaUnreal\CarlaUnreal.uproject"

if (-not (Test-Path $UE))       { throw "UnrealEditor.exe not found: $UE" }
if (-not (Test-Path $Uproject)) { throw "uproject not found: $Uproject" }

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
if ($ExtraArgs) { $argList += $ExtraArgs }

Write-Host "============================================================"
Write-Host " Headless CARLA server"
Write-Host "   map      : $Map"
Write-Host "   rpc port : $RpcPort"
Write-Host "   render   : $(if ($WithWindow) {'windowed'} else {'-RenderOffScreen'})"
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
$Log = "G:\Projects\CarlaUE_5_7_4\carla\Unreal\CarlaUnreal\Saved\Logs\CarlaUnreal.log"
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
