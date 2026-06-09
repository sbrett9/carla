#!/usr/bin/env bash
#
# BuildCarla.sh — Linux equivalent of Scripts/Windows/BuildCarla.ps1
#
# Build CarlaUnrealEditor (C++) and/or CarlaNet (.NET) + the carlanet Python wheel.
#   1) Unreal  — compiles CarlaUnrealEditor via the UE 5.7.4 Linux Build.sh.
#   2) CarlaNet — `dotnet publish` the .NET libcarla replacement into the python shim,
#                 then builds (and optionally installs) the carlanet wheel.
# CarlaNet runs even if the Unreal build failed, so you still get full diagnostics.
#
# Paths resolve in priority order: CLI flag > environment variable > default derived
# from this script's location (this script lives at carla/Scripts/Linux/, so the
# workspace root is three directories up).

set -uo pipefail

# ── Defaults (derived from script location) ─────────────────────────────────
script_dir="$(cd "$(dirname "$(realpath "${BASH_SOURCE[0]}")")" && pwd)"
default_workspace="$(cd "$script_dir/../../.." && pwd)"

skip_unreal=0
skip_carlanet=0
install_wheel=0
workspace_root="${CARLA_WORKSPACE_ROOT:-}"
unreal_engine_root="${CARLA_UNREAL_ENGINE_PATH:-}"

usage() {
    cat <<'EOF'
Usage: BuildCarla.sh [options]

Build CarlaUnrealEditor (C++) and/or CarlaNet (.NET) + the carlanet Python wheel.

Options:
  --skip-unreal              Skip the CarlaUnrealEditor C++ build.
  --skip-carlanet            Skip the CarlaNet (.NET) build + wheel.
  --install-wheel            Also pip-install the freshly built wheel (--force-reinstall).
  --workspace-root <path>    Repo root (contains 'carla' + engine).
                             Env: CARLA_WORKSPACE_ROOT. Default: three levels up from this script.
  --unreal-engine-root <path>
                             UE 5.7.4 source-build root.
                             Env: CARLA_UNREAL_ENGINE_PATH. Default: <workspace-root>/UE_5_7_4.
  -h, --help                 Show this help and exit.

Examples:
  ./BuildCarla.sh --install-wheel
  ./BuildCarla.sh --skip-unreal          # just rebuild the CarlaNet wheel
  CARLA_UNREAL_ENGINE_PATH=/opt/UE_5_7_4 ./BuildCarla.sh
EOF
}

# ── Parse arguments ─────────────────────────────────────────────────────────
while [ $# -gt 0 ]; do
    case "$1" in
        --skip-unreal)        skip_unreal=1 ;;
        --skip-carlanet)      skip_carlanet=1 ;;
        --install-wheel)      install_wheel=1 ;;
        --workspace-root)     workspace_root="$2"; shift ;;
        --workspace-root=*)   workspace_root="${1#*=}" ;;
        --unreal-engine-root)   unreal_engine_root="$2"; shift ;;
        --unreal-engine-root=*) unreal_engine_root="${1#*=}" ;;
        -h|--help)            usage; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
    esac
    shift
done

# ── Resolve paths (flag > env > default) ────────────────────────────────────
[ -n "$workspace_root" ]     || workspace_root="$default_workspace"
[ -n "$unreal_engine_root" ] || unreal_engine_root="$workspace_root/UE_5_7_4"

carla_root="$workspace_root/carla"
carla_uproject="$carla_root/Unreal/CarlaUnreal/CarlaUnreal.uproject"
log_file="$workspace_root/Carla_build.log"
python_dir="$carla_root/CarlaNet/python"

echo "Workspace : $workspace_root"
echo "UE engine : $unreal_engine_root"
echo "Build started: $(date)" > "$log_file"

ue_result=0    # 0 = success/skipped
net_result=0

# ============================================================================
#  1) Unreal — CarlaUnrealEditor (C++: Carla plugin, CesiumCarlaBridge, etc.)
# ============================================================================
if [ "$skip_unreal" -eq 0 ]; then
    echo "============================================================"
    echo " Building CarlaUnrealEditor - Development Linux"
    echo " Log: $log_file"
    echo "============================================================"

    build_sh="$unreal_engine_root/Engine/Build/BatchFiles/Linux/Build.sh"
    if [ ! -f "$build_sh" ]; then
        echo "ERROR: UE Build.sh not found: $build_sh" | tee -a "$log_file"
        echo "       Set --unreal-engine-root or \$CARLA_UNREAL_ENGINE_PATH." | tee -a "$log_file"
        ue_result=1
    elif [ ! -f "$carla_uproject" ]; then
        echo "ERROR: CarlaUnreal.uproject not found: $carla_uproject" | tee -a "$log_file"
        echo "       Set --workspace-root or \$CARLA_WORKSPACE_ROOT." | tee -a "$log_file"
        ue_result=1
    else
        "$build_sh" \
            CarlaUnrealEditor Linux Development \
            "$carla_uproject" \
            -WaitMutex \
            -Unattended \
            2>&1 | tee -a "$log_file"
        ue_result=${PIPESTATUS[0]}
    fi

    if [ "$ue_result" -eq 0 ]; then
        echo "UNREAL BUILD SUCCEEDED - $(date)" | tee -a "$log_file"
    else
        echo "UNREAL BUILD FAILED (exit code $ue_result) - $(date)" | tee -a "$log_file"
    fi
else
    echo "Skipping Unreal build (--skip-unreal)."
    echo "UNREAL BUILD SKIPPED - $(date)" >> "$log_file"
fi

# ============================================================================
#  2) CarlaNet — .NET publish into the python shim, then build/install wheel.
#     (Port of build_wheel.ps1; runs even if the Unreal build failed.)
# ============================================================================
if [ "$skip_carlanet" -eq 0 ]; then
    echo ""
    echo "============================================================"
    echo " Building CarlaNet (.NET) + Python wheel"
    echo "============================================================"

    csproj="$carla_root/CarlaNet/src/CarlaNet.Python/CarlaNet.Python.csproj"
    dlls_dir="$python_dir/carlanet/dlls"
    dist_dir="$python_dir/dist"
    python_bin="${PYTHON:-python3}"

    if [ ! -f "$csproj" ]; then
        echo "CarlaNet csproj not found: $csproj" | tee -a "$log_file"
        net_result=1
    else
        (
            set -e
            mkdir -p "$dlls_dir"
            echo "[build_wheel] dotnet publish -> $dlls_dir"
            dotnet publish "$csproj" -c Release -o "$dlls_dir"

            echo "[build_wheel] ensuring 'build' package is available"
            "$python_bin" -m pip install --upgrade build

            echo "[build_wheel] building wheel"
            "$python_bin" -m build --wheel "$python_dir"

            if [ "$install_wheel" -eq 1 ]; then
                wheel_path="$(ls -t "$dist_dir"/*.whl 2>/dev/null | head -n1)"
                [ -n "$wheel_path" ] || { echo "[build_wheel] no wheel produced in $dist_dir" >&2; exit 1; }
                echo "[build_wheel] installing wheel with --force-reinstall: $wheel_path"
                "$python_bin" -m pip install --force-reinstall "$wheel_path"
            fi
        ) 2>&1 | tee -a "$log_file"
        net_result=${PIPESTATUS[0]}
    fi

    if [ "$net_result" -eq 0 ]; then
        echo "CARLANET BUILD SUCCEEDED - $(date)" | tee -a "$log_file"
    else
        echo "CARLANET BUILD FAILED (exit code $net_result) - $(date)" | tee -a "$log_file"
    fi
else
    echo "Skipping CarlaNet build (--skip-carlanet)."
    echo "CARLANET BUILD SKIPPED - $(date)" >> "$log_file"
fi

# ============================================================================
#  Summary
# ============================================================================
echo ""
echo "============================================================"
if [ "$skip_unreal" -eq 1 ]; then ue_msg="skipped"; elif [ "$ue_result" -eq 0 ]; then ue_msg="OK"; else ue_msg="FAILED ($ue_result)"; fi
if [ "$skip_carlanet" -eq 1 ]; then net_msg="skipped"; elif [ "$net_result" -eq 0 ]; then net_msg="OK"; else net_msg="FAILED ($net_result)"; fi
echo " Unreal : $ue_msg"
echo " CarlaNet: $net_msg"
echo "============================================================"
echo "Log: $log_file"

if [ "$ue_result" -ne 0 ] || [ "$net_result" -ne 0 ]; then
    exit 1
fi
exit 0
