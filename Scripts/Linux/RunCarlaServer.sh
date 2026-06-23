#!/usr/bin/env bash
#
# RunCarlaServer.sh — Linux equivalent of Scripts/Windows/RunCarlaServer.ps1
#
# Launch a HEADLESS CARLA RPC server (no editor, no cook) for the digital-twin
# pipeline. Loads a CARLA map (default Town10HD_Opt -> CarlaGameMode), which starts
# the RPC server on the given port. Runs in the foreground and streams its log;
# Ctrl+C to stop. Drive it from another terminal with the carlanet Python client
# (e.g. test_cesium_heights.py, or generate_world_from_osm_with_elevation).
#
# Uses the editor binary in -game mode (fast: no packaging step). The Cesium tileset
# does NOT need to be pre-placed — the client spawns it at runtime via
# configure_cesium_georeference. Async mode keeps the world ticking so Cesium streams
# and height samples resolve.
#
# Paths are derived from this script's location (it lives at carla/Scripts/Linux/, so
# the CARLA repo root is two directories up). The engine is found via --unreal-engine-root,
# then $CARLA_UNREAL_ENGINE_PATH, then <repo-parent>/UE_5_7_4.

set -uo pipefail

# ── Paths derived from script location (carla/Scripts/Linux) ─────────────────
script_dir="$(cd "$(dirname "$(realpath "${BASH_SOURCE[0]}")")" && pwd)"
carla_root="$(cd "$script_dir/../.." && pwd)"
repo_parent="$(cd "$carla_root/.." && pwd)"

map="/Game/Carla/Maps/Town10HD_Opt"
rpc_port=2000
with_window=0
extra_args=""
unreal_engine_root="${CARLA_UNREAL_ENGINE_PATH:-}"
ready_timeout=180

usage() {
    cat <<'EOF'
Usage: RunCarlaServer.sh [options]

Launch a headless CARLA RPC server (editor binary in -game mode) and stream its log.
Ctrl+C stops the server.

Options:
  --map <path>               Startup map. Default /Game/Carla/Maps/Town10HD_Opt.
  --rpc-port <n>             CARLA RPC port. Default 2000.
  --with-window              Show a window instead of -RenderOffScreen (eyeball Cesium streaming).
  --extra-args "<args>"      Extra arguments appended to the UnrealEditor command line.
  --unreal-engine-root <path>
                             UE 5.7.4 source-build root.
                             Env: CARLA_UNREAL_ENGINE_PATH. Default: <repo-parent>/UE_5_7_4.
  -h, --help                 Show this help and exit.

Examples:
  ./RunCarlaServer.sh
  ./RunCarlaServer.sh --rpc-port 3000 --map /Game/Carla/Maps/Town01
  CARLA_UNREAL_ENGINE_PATH=/opt/UE_5_7_4 ./RunCarlaServer.sh
EOF
}

# ── Parse arguments ─────────────────────────────────────────────────────────
while [ $# -gt 0 ]; do
    case "$1" in
        --map)                  map="$2"; shift ;;
        --map=*)                map="${1#*=}" ;;
        --rpc-port)             rpc_port="$2"; shift ;;
        --rpc-port=*)           rpc_port="${1#*=}" ;;
        --with-window)          with_window=1 ;;
        --extra-args)           extra_args="$2"; shift ;;
        --extra-args=*)         extra_args="${1#*=}" ;;
        --unreal-engine-root)   unreal_engine_root="$2"; shift ;;
        --unreal-engine-root=*) unreal_engine_root="${1#*=}" ;;
        -h|--help)              usage; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
    esac
    shift
done

# ── Resolve engine path (flag > env > default) ──────────────────────────────
[ -n "$unreal_engine_root" ] || unreal_engine_root="$repo_parent/UE_5_7_4"

ue_bin="$unreal_engine_root/Engine/Binaries/Linux/UnrealEditor"
uproject="$carla_root/Unreal/CarlaUnreal/CarlaUnreal.uproject"
log_file="$carla_root/Unreal/CarlaUnreal/Saved/Logs/CarlaUnreal.log"

if [ ! -x "$ue_bin" ] && [ ! -f "$ue_bin" ]; then
    echo "ERROR: UnrealEditor not found: $ue_bin" >&2
    echo "       Set --unreal-engine-root or \$CARLA_UNREAL_ENGINE_PATH." >&2
    exit 1
fi
if [ ! -f "$uproject" ]; then
    echo "ERROR: CarlaUnreal.uproject not found: $uproject" >&2
    echo "       This script must live under <repo>/carla/Scripts/Linux; the checkout looks incomplete." >&2
    exit 1
fi

if pgrep -x UnrealEditor >/dev/null 2>&1; then
    echo "ERROR: An UnrealEditor process is already running (PID $(pgrep -x UnrealEditor | tr '\n' ' '))." >&2
    echo "       Close it first — two instances on one project conflict." >&2
    exit 1
fi

# ── Build argument list ─────────────────────────────────────────────────────
args=("$uproject" "$map" "-game")
[ "$with_window" -eq 1 ] || args+=("-RenderOffScreen")
args+=("-carla-rpc-port=$rpc_port" "-nosound" "-unattended" "-nopause")
[ -n "$extra_args" ] && args+=($extra_args)

render_desc=$([ "$with_window" -eq 1 ] && echo "windowed" || echo "-RenderOffScreen")
echo "============================================================"
echo " Headless CARLA server"
echo "   map      : $map"
echo "   rpc port : $rpc_port"
echo "   render   : $render_desc"
echo "------------------------------------------------------------"
echo " Once the server is up, in ANOTHER terminal run e.g.:"
echo "   python carla/CarlaNet/python/test_cesium_heights.py"
echo " Ctrl+C here to stop the server."
echo "============================================================"
echo "$ue_bin ${args[*]}"
echo ""

# ── Launch + clean shutdown on Ctrl+C ───────────────────────────────────────
proc_pid=""
cleanup() {
    if [ -n "$proc_pid" ] && kill -0 "$proc_pid" 2>/dev/null; then
        echo ""
        echo "[server] stopping PID $proc_pid ..."
        kill -TERM "$proc_pid" 2>/dev/null
        for _ in $(seq 1 8); do
            kill -0 "$proc_pid" 2>/dev/null || break
            sleep 1
        done
        kill -0 "$proc_pid" 2>/dev/null && kill -KILL "$proc_pid" 2>/dev/null
    fi
}
trap cleanup INT TERM EXIT

"$ue_bin" "${args[@]}" &
proc_pid=$!
echo "[server] PID $proc_pid launched; Ctrl+C here to stop it."
echo ""

# ── Readiness detection: poll until the RPC port accepts connections ────────
port_open() {
    (exec 3<>"/dev/tcp/127.0.0.1/$rpc_port") 2>/dev/null || return 1
    exec 3>&- 3<&-
    return 0
}

echo "[server] Waiting for RPC port $rpc_port to open on 127.0.0.1 ..."
elapsed=0
ready=0
while [ "$elapsed" -le "$ready_timeout" ]; do
    if ! kill -0 "$proc_pid" 2>/dev/null; then
        wait "$proc_pid" 2>/dev/null; code=$?
        echo ""
        echo "============================================================"
        echo " SERVER CRASHED during startup (exit code $code) after ${elapsed}s"
        echo "============================================================"
        [ -f "$log_file" ] && { echo "--- last 30 lines of $log_file ---"; tail -n 30 "$log_file"; }
        exit 1
    fi
    if port_open; then ready=1; break; fi
    printf '\r[server] still starting... %ss elapsed' "$elapsed"
    sleep 1
    elapsed=$((elapsed + 1))
done
echo ""

if [ "$ready" -eq 1 ]; then
    echo "============================================================"
    echo " SERVER READY — listening on 127.0.0.1:$rpc_port  (after ${elapsed}s)"
    echo "============================================================"
    echo ""
else
    echo "============================================================"
    echo " TIMEOUT — port $rpc_port not open after ${ready_timeout}s"
    echo " The server may still be loading; watch the log below."
    echo "============================================================"
    echo ""
fi

# ── Run until the server exits (or Ctrl+C triggers cleanup) ─────────────────
wait "$proc_pid"
code=$?
proc_pid=""   # already reaped; stop cleanup from re-killing
trap - INT TERM EXIT

echo ""
echo "============================================================"
echo "[server] exited with code $code"
[ -f "$log_file" ] && { echo "--- last 30 lines of $log_file ---"; tail -n 30 "$log_file"; }
echo "============================================================"
exit $code
