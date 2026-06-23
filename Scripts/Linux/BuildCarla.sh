#!/usr/bin/env bash
#
# BuildCarla.sh — Linux equivalent of Scripts/Windows/BuildCarla.ps1
#
# Build CarlaUnrealEditor (C++) and/or CarlaNet (.NET) + the carlanet Python wheel.
#   1) Unreal  — compiles CarlaUnrealEditor via the UE 5.7.4 Linux Build.sh.
#   2) CarlaNet — delegates to CarlaNet/python/build_wheel.sh, which `dotnet publish`es
#                 the .NET libcarla replacement into the python shim, then builds (and
#                 optionally installs) the carlanet wheel.
# CarlaNet runs even if the Unreal build failed, so you still get full diagnostics.
#
# Paths are derived from this script's location (it lives at carla/Scripts/Linux/, so the
# CARLA repo root is two directories up). The engine is found via --unreal-engine-root,
# then $CARLA_UNREAL_ENGINE_PATH, then <repo-parent>/UE_5_7_4.

set -uo pipefail

# ── Paths derived from script location (carla/Scripts/Linux) ─────────────────
script_dir="$(cd "$(dirname "$(realpath "${BASH_SOURCE[0]}")")" && pwd)"
carla_root="$(cd "$script_dir/../.." && pwd)"
repo_parent="$(cd "$carla_root/.." && pwd)"

skip_unreal=0
skip_carlanet=0
install_wheel=0
clean_unreal=0
clean_wheel=0
unreal_engine_root="${CARLA_UNREAL_ENGINE_PATH:-}"

usage() {
    cat <<'EOF'
Usage: BuildCarla.sh [options]

Build CarlaUnrealEditor (C++) and/or CarlaNet (.NET) + the carlanet Python wheel.

Options:
  --skip-unreal              Skip the CarlaUnrealEditor C++ build.
  --clean-unreal, --rebuild  Wipe editor + plugin Intermediate/Binaries first for a full
                             from-scratch rebuild (keeps the built cesium-native ThirdParty).
  --skip-carlanet            Skip the CarlaNet (.NET) build + wheel.
  --install-wheel            Also pip-install the freshly built wheel (--force-reinstall).
  --clean-wheel              Wipe CarlaNet/python build/dist/dlls/egg-info before building the wheel.
  --unreal-engine-root <path>
                             UE 5.7.4 source-build root.
                             Env: CARLA_UNREAL_ENGINE_PATH. Default: <repo-parent>/UE_5_7_4.
  -h, --help                 Show this help and exit.

Examples:
  ./BuildCarla.sh --install-wheel
  ./BuildCarla.sh --skip-unreal          # just rebuild the CarlaNet wheel
  ./BuildCarla.sh --clean-unreal         # full from-scratch editor rebuild
  CARLA_UNREAL_ENGINE_PATH=/opt/UE_5_7_4 ./BuildCarla.sh
EOF
}

# ── Parse arguments ─────────────────────────────────────────────────────────
while [ $# -gt 0 ]; do
    case "$1" in
        --skip-unreal)          skip_unreal=1 ;;
        --clean-unreal|--rebuild) clean_unreal=1 ;;
        --skip-carlanet)        skip_carlanet=1 ;;
        --install-wheel)        install_wheel=1 ;;
        --clean-wheel)          clean_wheel=1 ;;
        --unreal-engine-root)   unreal_engine_root="$2"; shift ;;
        --unreal-engine-root=*) unreal_engine_root="${1#*=}" ;;
        -h|--help)              usage; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
    esac
    shift
done

# ── Resolve engine path (flag > env > default) ──────────────────────────────
[ -n "$unreal_engine_root" ] || unreal_engine_root="$repo_parent/UE_5_7_4"

carla_uproject="$carla_root/Unreal/CarlaUnreal/CarlaUnreal.uproject"
log_file="$repo_parent/Carla_build.log"
python_dir="$carla_root/CarlaNet/python"

echo "CARLA repo: $carla_root"
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
        echo "       This script must live under <repo>/carla/Scripts/Linux; the checkout looks incomplete." | tee -a "$log_file"
        ue_result=1
    else
        if [ "$clean_unreal" -eq 1 ]; then
            echo "[clean] Full rebuild: removing editor Intermediate/Binaries (close any running editor first)..." | tee -a "$log_file"
            # Project UBT outputs + UHT-generated headers, plus each plugin's. Source/ThirdParty is
            # under Source/ (not touched), so the built cesium-native is preserved; only C++ recompiles.
            project_dir="$carla_root/Unreal/CarlaUnreal"
            rm -rf "$project_dir/Intermediate" "$project_dir/Binaries"
            if [ -d "$project_dir/Plugins" ]; then
                for plugin in "$project_dir/Plugins"/*/; do
                    rm -rf "${plugin}Intermediate" "${plugin}Binaries"
                done
            fi
        fi

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
#     Delegates to CarlaNet/python/build_wheel.sh (the Linux peer of build_wheel.ps1),
#     so the wheel logic stays in one place. Runs even if the Unreal build failed.
# ============================================================================
if [ "$skip_carlanet" -eq 0 ]; then
    echo ""
    echo "============================================================"
    echo " Building CarlaNet (.NET) + Python wheel"
    echo "============================================================"

    build_wheel_sh="$python_dir/build_wheel.sh"
    if [ ! -f "$build_wheel_sh" ]; then
        echo "CarlaNet wheel script not found: $build_wheel_sh" | tee -a "$log_file"
        net_result=1
    else
        wheel_args=()
        [ "$install_wheel" -eq 1 ] && wheel_args+=(--install)
        [ "$clean_wheel" -eq 1 ]   && wheel_args+=(--clean)
        bash "$build_wheel_sh" "${wheel_args[@]}" 2>&1 | tee -a "$log_file"
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
