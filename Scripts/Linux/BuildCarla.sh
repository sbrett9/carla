#!/usr/bin/env bash
#
# BuildCarla.sh — Linux equivalent of Scripts/Windows/BuildCarla.ps1
#
# Build CarlaUnrealEditor (C++) and/or CarlaNet (.NET) + the carlanet & carlacontrol Python wheels.
#   1) Unreal  — compiles CarlaUnrealEditor via the UE 5.7.4 Linux Build.sh.
#   2) CarlaNet — delegates to CarlaNet/python/build_wheel.sh, which `dotnet publish`es
#                 the .NET libcarla replacement into the python shim, then builds (and
#                 optionally installs) the carlanet wheel.
#   3) CarlaControl — delegates to CarlaControl/build_wheel.sh to build (and optionally
#                 install) the carlacontrol wheel (the run_SCTMV.py client package).
# CarlaNet and CarlaControl run even if the Unreal build failed, so you still get full diagnostics.
#
# Paths are derived from this script's location (it lives at carla/Scripts/Linux/, so the
# CARLA repo root is two directories up). The engine is found via --unreal-engine-root,
# then $CARLA_UNREAL_ENGINE_PATH, then <repo-parent>/UE_5_7_4.

set -uo pipefail

# ── Paths derived from script location (carla/Scripts/Linux) ─────────────────
script_dir="$(cd "$(dirname "$(realpath "${BASH_SOURCE[0]}")")" && pwd)"
carla_root="$(cd "$script_dir/../.." && pwd)"
repo_parent="$(cd "$carla_root/.." && pwd)"

skip_libcarla=0
skip_unreal=0
skip_carlanet=0
skip_carlacontrol=0
install_wheel=0
clean_unreal=0
clean_wheel=0
allow_uba=0
unreal_engine_root="${CARLA_UNREAL_ENGINE_PATH:-}"

usage() {
    cat <<'EOF'
Usage: BuildCarla.sh [options]

Build CarlaUnrealEditor (C++) and/or CarlaNet (.NET) + the carlanet & carlacontrol Python wheels.

Options:
  --skip-libcarla            Skip the LibCarla (C++) build the Carla plugin links.
  --skip-unreal              Skip the CarlaUnrealEditor C++ build.
  --clean-unreal, --rebuild  Wipe editor + plugin Intermediate/Binaries first for a full
                             from-scratch rebuild (keeps the built cesium-native ThirdParty).
  --skip-carlanet            Skip the CarlaNet (.NET) build + wheel.
  --skip-carlacontrol        Skip the CarlaControl (carlacontrol) Python wheel.
  --install-wheel            Also pip-install the freshly built wheels (carlanet + carlacontrol, --force-reinstall).
  --clean-wheel              Wipe each package's build/dist/dlls/egg-info before building the wheels.
  --allow-uba                Keep the Unreal Build Accelerator enabled. UBA is disabled by default
                             because it crashes (NullReferenceException) under non-root Linux builds.
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
        --skip-libcarla)        skip_libcarla=1 ;;
        --skip-unreal)          skip_unreal=1 ;;
        --clean-unreal|--rebuild) clean_unreal=1 ;;
        --skip-carlanet)        skip_carlanet=1 ;;
        --skip-carlacontrol)    skip_carlacontrol=1 ;;
        --install-wheel)        install_wheel=1 ;;
        --clean-wheel)          clean_wheel=1 ;;
        --allow-uba)            allow_uba=1 ;;
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
# Log inside the repo's Build/ dir (always writable -- it is the bind-mounted checkout in a container
# build). The repo PARENT is not mounted, and the mount-parent the container synthesizes is root-owned,
# so writing the log there fails with "permission denied" under a non-root container build.
log_file="$carla_root/Build/Carla_build.log"
python_dir="$carla_root/CarlaNet/python"
carlacontrol_dir="$carla_root/CarlaControl"

echo "CARLA repo: $carla_root"
echo "UE engine : $unreal_engine_root"
mkdir -p "$(dirname "$log_file")" 2>/dev/null || true
echo "Build started: $(date)" > "$log_file"

# Disable the Unreal Build Accelerator by default. UBA NREs under non-root Linux builds
# (EpicGames.Core.FileSystemReference.CombineStrings NullReferenceException via UBAExecutor), which
# breaks both this editor build and the later package cook. Turning it off makes UBT fall back to its
# standard local executor -- same compiler, same output, just no acceleration. Written to the engine's
# user-level BuildConfiguration.xml so every UBT invocation against this engine (editor + cook) honors
# it. Pass --allow-uba to keep UBA on. This only disables an accelerator; it never changes build output.
if [ "$allow_uba" -eq 0 ] && [ -d "$unreal_engine_root/Engine" ]; then
    ubt_cfg_dir="$unreal_engine_root/Engine/Saved/UnrealBuildTool"
    ubt_cfg="$ubt_cfg_dir/BuildConfiguration.xml"
    if [ ! -f "$ubt_cfg" ]; then
        mkdir -p "$ubt_cfg_dir"
        cat > "$ubt_cfg" <<'XML'
<?xml version="1.0" encoding="utf-8" ?>
<Configuration xmlns="https://www.unrealengine.com/BuildConfiguration">
	<BuildConfiguration>
		<bAllowUBAExecutor>false</bAllowUBAExecutor>
	</BuildConfiguration>
</Configuration>
XML
        echo "[unreal] disabled UBA via $ubt_cfg (pass --allow-uba to keep it on)"
    elif ! grep -q "bAllowUBAExecutor" "$ubt_cfg" 2>/dev/null; then
        echo "WARNING: $ubt_cfg exists without a UBA setting; if the build/cook hits a UBA"
        echo "         NullReferenceException, add <bAllowUBAExecutor>false</bAllowUBAExecutor> to it."
    fi
fi

ue_result=0    # 0 = success/skipped
net_result=0
ctl_result=0

# ============================================================================
#  1) LibCarla — the C++ the Carla plugin links against
# ============================================================================
# The Carla plugin links Build/LibCarla/libcarla-server.a, listed in the plugin's
# Libraries.def. That library holds the server-side road model — OpenDriveParser, Map,
# Lane and MeshFactory — which is what turns an .xodr into the road mesh inside the
# engine. It is CARLA's own source, not a third-party dependency, so an edit to it has
# to be compiled here: without this the Unreal step below happily relinks the plugin
# against whatever carla-server was last produced, and a C++ change appears to have had
# no effect at all.
lib_result=0
if [ "$skip_libcarla" -eq 0 ]; then
    echo "============================================================"
    echo " Building LibCarla (carla-server)"
    echo "============================================================"
    if [ ! -f "$carla_root/Build/CMakeCache.txt" ]; then
        echo "No CMake cache in Build/ — run CarlaSetup.sh first to configure it." | tee -a "$log_file"
        exit 1
    fi
    cmake --build "$carla_root/Build" --target carla-server 2>&1 | tee -a "$log_file"
    lib_result=${PIPESTATUS[0]}
    if [ "$lib_result" -ne 0 ]; then
        echo "" | tee -a "$log_file"
        echo "LIBCARLA BUILD FAILED" | tee -a "$log_file"
        exit "$lib_result"
    fi
    echo "" | tee -a "$log_file"
    echo "LIBCARLA BUILD SUCCEEDED" | tee -a "$log_file"
else
    echo "Skipping LibCarla build (--skip-libcarla)."
fi

# ============================================================================
#  2) Unreal — CarlaUnrealEditor (C++: Carla plugin, CesiumCarlaBridge, etc.)
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

        # Carla.Build.cs reads generated .def files (Definitions/Includes/Libraries/Options) from the
        # plugin dir. CarlaSetup's CMake configure generates them under Build/Unreal and the
        # carla-unreal-configure target symlinks them into the plugin dir. We invoke Build.sh directly
        # (not the cmake carla-unreal-editor target), so run that configure step first -- otherwise UBT
        # aborts with "Could not find ... Definitions.def" before any compilation.
        if [ -d "$carla_root/Build" ]; then
            echo "[unreal] ensuring Carla plugin .def files (carla-unreal-configure)..." | tee -a "$log_file"
            cmake --build "$carla_root/Build" --target carla-unreal-configure 2>&1 | tee -a "$log_file"
        else
            echo "WARNING: $carla_root/Build not found -- run CarlaSetup.sh first; the editor build will fail on missing .def files." | tee -a "$log_file"
        fi

        # When ROS2 is enabled, the Carla plugin links libcarla-ros2-native.so (+ Fast-DDS runtime
        # .so's) from its Binaries/Linux dir. The carla-ros2-native cmake target builds them into
        # Build/Ros2Native/install/lib, but it is a standalone target (not a dependency of anything)
        # and its POST_BUILD copy doesn't run on this direct-Build.sh path -- so build it and stage
        # the .so's into the plugin Binaries dir before the editor links, else UBT fails with
        # "ld.lld: unable to find library -lcarla-ros2-native".
        if [ -d "$carla_root/Build/Ros2Native" ]; then
            echo "[unreal] building + staging ROS2 native libs (carla-ros2-native)..." | tee -a "$log_file"
            cmake --build "$carla_root/Build" --target carla-ros2-native 2>&1 | tee -a "$log_file" \
                || echo "WARNING: carla-ros2-native build returned nonzero; continuing to stage any existing libs." | tee -a "$log_file"
            # Stage from both install/lib and install/lib64: Fast-DDS/Fast-CDR install to lib, but its
            # transitive dependency foonathan_memory installs to lib64. libcarla-ros2-native.so loads
            # all of them via its $ORIGIN rpath, so every .so must sit beside it in the plugin Binaries
            # dir. Missing libfoonathan_memory-0.7.4.so makes the Carla module fail to load at cook and
            # at packaged runtime ("dlopen failed: libfoonathan_memory-0.7.4.so").
            plugin_bin="$carla_root/Unreal/CarlaUnreal/Plugins/Carla/Binaries/Linux"
            staged_any=0
            for ros2_lib_dir in "$carla_root/Build/Ros2Native/install/lib" "$carla_root/Build/Ros2Native/install/lib64"; do
                if [ -d "$ros2_lib_dir" ] && ls "$ros2_lib_dir"/*.so* >/dev/null 2>&1; then
                    mkdir -p "$plugin_bin"
                    cp -a "$ros2_lib_dir"/*.so* "$plugin_bin"/
                    echo "[unreal] staged ROS2 native libs from $ros2_lib_dir into $plugin_bin" | tee -a "$log_file"
                    staged_any=1
                fi
            done
            if [ "$staged_any" = "0" ]; then
                echo "WARNING: no ROS2 native libs under Build/Ros2Native/install/{lib,lib64}; the editor link may fail on -lcarla-ros2-native." | tee -a "$log_file"
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
#  3) CarlaNet — .NET publish into the python shim, then build/install wheel.
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
#  4) CarlaControl — carlacontrol Python wheel (the run_SCTMV.py client package).
#     Delegates to CarlaControl/build_wheel.sh. Pure Python (no .NET/native step),
#     independent of both builds above.
# ============================================================================
if [ "$skip_carlacontrol" -eq 0 ]; then
    echo ""
    echo "============================================================"
    echo " Building CarlaControl (carlacontrol) Python wheel"
    echo "============================================================"

    ctl_wheel_sh="$carlacontrol_dir/build_wheel.sh"
    if [ ! -f "$ctl_wheel_sh" ]; then
        echo "CarlaControl wheel script not found: $ctl_wheel_sh" | tee -a "$log_file"
        ctl_result=1
    else
        ctl_args=()
        [ "$install_wheel" -eq 1 ] && ctl_args+=(--install)
        [ "$clean_wheel" -eq 1 ]   && ctl_args+=(--clean)
        bash "$ctl_wheel_sh" "${ctl_args[@]}" 2>&1 | tee -a "$log_file"
        ctl_result=${PIPESTATUS[0]}
    fi

    if [ "$ctl_result" -eq 0 ]; then
        echo "CARLACONTROL BUILD SUCCEEDED - $(date)" | tee -a "$log_file"
    else
        echo "CARLACONTROL BUILD FAILED (exit code $ctl_result) - $(date)" | tee -a "$log_file"
    fi
else
    echo "Skipping CarlaControl build (--skip-carlacontrol)."
    echo "CARLACONTROL BUILD SKIPPED - $(date)" >> "$log_file"
fi

# ============================================================================
#  Summary
# ============================================================================
echo ""
echo "============================================================"
if [ "$skip_unreal" -eq 1 ]; then ue_msg="skipped"; elif [ "$ue_result" -eq 0 ]; then ue_msg="OK"; else ue_msg="FAILED ($ue_result)"; fi
if [ "$skip_carlanet" -eq 1 ]; then net_msg="skipped"; elif [ "$net_result" -eq 0 ]; then net_msg="OK"; else net_msg="FAILED ($net_result)"; fi
if [ "$skip_carlacontrol" -eq 1 ]; then ctl_msg="skipped"; elif [ "$ctl_result" -eq 0 ]; then ctl_msg="OK"; else ctl_msg="FAILED ($ctl_result)"; fi
echo " Unreal : $ue_msg"
echo " CarlaNet: $net_msg"
echo " CarlaControl: $ctl_msg"
echo "============================================================"
echo "Log: $log_file"

if [ "$ue_result" -ne 0 ] || [ "$net_result" -ne 0 ] || [ "$ctl_result" -ne 0 ]; then
    exit 1
fi
exit 0
