#! /bin/bash
#
# CarlaSetup.sh — provision prerequisites, fetch content, build the bundled SUMO
# `netconvert`, fetch the optional VibeUE plugin, build Cesium for Unreal from source,
# then configure and build CARLA against a source UE 5.7.4.
#
# Linux peer of CarlaSetup.ps1. It is NOT a line-for-line port: Visual Studio / MSVC
# toolset selection has no Linux equivalent (the UE Linux build and cesium-native both
# use the engine's BUNDLED clang toolchain), and the SUMO build links the system
# libxerces-c / libproj instead of the Windows SUMOLibraries bundle.
#
# Paths are derived from this script's location (the repo root is this script's dir).
# The engine is taken from $CARLA_UNREAL_ENGINE_PATH (required).

set -e

# ── Defaults ────────────────────────────────────────────────────────────────
interactive=0
skip_prerequisites=0
launch=0
python_root=
vibeue_ssh_key=
content_ssh_key=
content_repo=

clean_sumo=0
clean_all=0
clean_carla=0
clean_content=0
skip_cesium=0
clean_cesium=0
force_deps_rebuild=0
with_python_api=0
with_tests=0

workspace_path="$(cd "$(dirname "$(realpath "${BASH_SOURCE[0]}")")" && pwd)"

usage() {
    cat <<'EOF'
CarlaSetup.sh - provision + build CARLA against a source UE 5.7.4.

USAGE:
  ./CarlaSetup.sh [options]

OPTIONS:
  -i, --interactive          Allow interactive prompts (sudo password).
  -p, --skip-prerequisites   Skip the InstallPrerequisites step.
  -l, --launch               Launch the Unreal Editor after building.
      --clean                Remove Build/sumo-build + Build/sumo-install (rebuild SUMO).
      --clean-all            Also remove Build/sumo-src (full SUMO re-clone).
      --clean-carla          Clear the CARLA CMake cache (force a re-configure).
      --clean-content        Remove and re-clone the CARLA content (fixes a broken/partial checkout).
      --skip-cesium          Skip building Cesium for Unreal from source.
      --clean-cesium         Force a cesium-native rebuild (keeps the source checkout).
      --force-deps-rebuild   Also clear the ezvcpkg cache so vcpkg deps recompile (implies --clean-cesium).
      --with-python-api      Build the legacy Boost.Python `carla` module (off by default).
      --with-tests           Build LibCarla C++ tests (off by default; pulls in googletest).
      --content-repo <url>   Content repository URL (else $CARLA_CONTENT_REPO;
                             defaults to git@github.sncorp.com:CAT/carla-content.git).
      --content-ssh-key <path> SSH key for the private carla-content mirror (else $CARLA_CONTENT_SSH_KEY;
                             falls back to your default SSH agent/keys if neither is given).
      --python-root <dir>    Python install root for the API build.
      --vibeue-ssh-key <path> SSH key for the private VibeUE mirror (else $VIBEUE_SSH_KEY).
  -h, --help                 Show this help and exit.

The engine is taken from $CARLA_UNREAL_ENGINE_PATH (must be set to your UE 5.7.4 root).
The CARLA content is cloned over SSH from the configured repository (defaults to the CAT
organization mirror on github.sncorp.com). Provide a deploy key via --content-ssh-key=<path>
or $CARLA_CONTENT_SSH_KEY, or have a working SSH agent/key for the target host.

EXAMPLES:
  ./CarlaSetup.sh --skip-prerequisites
  ./CarlaSetup.sh --clean --skip-prerequisites
EOF
}

# ── Parse arguments (supports "--flag value" and "--flag=value") ─────────────
while [ $# -gt 0 ]; do
    case "$1" in
        -i|--interactive)        interactive=1 ;;
        -p|--skip-prerequisites) skip_prerequisites=1 ;;
        -l|--launch)             launch=1 ;;
        --clean)                 clean_sumo=1 ;;
        --clean-all)             clean_all=1 ;;
        --clean-carla)           clean_carla=1 ;;
        --clean-content)         clean_content=1 ;;
        --skip-cesium)           skip_cesium=1 ;;
        --clean-cesium)          clean_cesium=1 ;;
        --force-deps-rebuild)    force_deps_rebuild=1 ;;
        --with-python-api)       with_python_api=1 ;;
        --with-tests)            with_tests=1 ;;
        -pyroot|--python-root)   python_root="$2"; shift ;;
        --python-root=*)         python_root="${1#*=}" ;;
        --content-repo)          content_repo="$2"; shift ;;
        --content-repo=*)        content_repo="${1#*=}" ;;
        --content-ssh-key)       content_ssh_key="$2"; shift ;;
        --content-ssh-key=*)     content_ssh_key="${1#*=}" ;;
        --vibeue-ssh-key)        vibeue_ssh_key="$2"; shift ;;
        --vibeue-ssh-key=*)      vibeue_ssh_key="${1#*=}" ;;
        -h|--help)               usage; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
    esac
    shift
done

# --force-deps-rebuild rebuilds the vcpkg deps from the ezvcpkg cache; that is only
# meaningful alongside a cesium-native rebuild, so it implies --clean-cesium.
[ "$force_deps_rebuild" -eq 1 ] && clean_cesium=1

# Run everything relative to the repository root (this script's directory).
cd "$workspace_path"
echo "workspace_path=$workspace_path"

# ── Preflight: sudo + git credentials ────────────────────────────────────────
# Check if root is asking for a password:
if [ -z "${EUID:-}" ]; then
    EUID=$(id -u)
fi
if [ "$EUID" -ne 0 ] && [ $interactive -eq 0 ] && [ $skip_prerequisites -eq 0 ]; then
    if ! sudo -n true 2>/dev/null; then
        echo "Please run 'sudo -v' before running this script, or pass --interactive or --skip-prerequisites."
        exit 1
    fi
fi

# ── PREREQUISITES INSTALL STEP ───────────────────────────────────────────────
if [ $skip_prerequisites -eq 0 ]; then
    python_path=python3
    if [ "$python_root" != "" ]; then
        python_path=${python_root}/python3
    fi
    echo "Installing prerequisites..."
    bash "$workspace_path/Util/SetupUtils/InstallPrerequisites.sh" --python-path=$python_path
else
    echo "Skipping prerequisites install step."
fi

# ── CLONE CONTENT ────────────────────────────────────────────────────────────
# Private mirror of carla-simulator/carla-content (ue5-dev). The default mirror is hosted
# in the CAT organization on github.sncorp.com and carries vehicle staging-fade asset changes.
# Cloned over SSH (same mechanism as the VibeUE plugin): supply a deploy key with
# --content-ssh-key=<path> or $CARLA_CONTENT_SSH_KEY; without one, the clone uses your default
# SSH agent/keys for the target host. The content repository URL can be overridden via
# --content-repo or $CARLA_CONTENT_REPO. The upstream Bitbucket repo
# (https://bitbucket.org/carla-simulator/carla-content.git, branch ue5-dev) is the original
# public source if a pristine pull is ever needed.
content_dir="$workspace_path/Unreal/CarlaUnreal/Content"
content_repo="${content_repo:-${CARLA_CONTENT_REPO:-git@github.sncorp.com:CAT/carla-content.git}}"
content_key="${content_ssh_key:-${CARLA_CONTENT_SSH_KEY:-}}"

# Shared non-interactive ssh base for the private-repo clones (the content repo below and the VibeUE
# plugin later). Runs ssh non-interactively so a fresh container/batch run never blocks on the
# github.com host-key prompt ("Are you sure you want to continue connecting?"). StrictHostKeyChecking=no
# + UserKnownHostsFile=/dev/null means it neither prompts nor depends on (or writes) a known_hosts file
# -- this also covers the `git lfs pull` calls below, which spawn ssh for git-lfs-authenticate.
ssh_noninteractive="ssh -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null"

# git wrapper for the content repo. Uses the deploy key when one is provided.
content_git() {
    if [ -n "$content_key" ]; then
        GIT_SSH_COMMAND="$ssh_noninteractive -i $content_key -o IdentitiesOnly=yes" git "$@"
    else
        GIT_SSH_COMMAND="$ssh_noninteractive" git "$@"
    fi
}

# --clean-content forces a fresh re-clone (use when a previous clone left a broken/partial tree).
if [ "$clean_content" -eq 1 ] && [ -d "$content_dir/Carla" ]; then
    echo "Removing existing CARLA content (--clean-content)..."
    rm -rf "$content_dir/Carla"
fi

# The content assets are Git-LFS objects. A plain clone usually smudges them, but an interrupted or
# partial smudge silently leaves assets absent or zero-byte (which then crashes the cook). So always
# run an explicit `git lfs pull` -- on a fresh clone AND when content already exists -- so re-running
# this script repairs an incomplete checkout instead of skipping it.
if [ -d "$content_dir/Carla/.git" ]; then
    if [ "${CARLA_CONTENT_CACHE_MANAGED:-0}" = "1" ]; then
        echo "Found CARLA content; using existing host-managed cache."
    else
        echo "Found CARLA content; ensuring LFS assets are present..."
        content_git -C "$content_dir/Carla" lfs install --local || true
        content_git -C "$content_dir/Carla" lfs pull \
            || echo "WARNING: 'git lfs pull' failed; content may be incomplete (a key/agent for the private mirror is required)."
    fi
else
    echo "Could not find CARLA content. Downloading..."
    mkdir -p "$content_dir"
    content_git -C "$content_dir" clone -b ue5-dev "$content_repo" Carla
    content_git -C "$content_dir/Carla" lfs install --local || true
    content_git -C "$content_dir/Carla" lfs pull \
        || echo "WARNING: 'git lfs pull' failed; content may be incomplete."
fi

# Completeness sanity check: a known LFS asset that must exist and be non-empty. If it is missing the
# cook will crash later (e.g. pedestrian animations with a null skeleton), so fail loudly here.
content_sentinel="$content_dir/Carla/Static/Pedestrian/00_GenericComponents/Definitions/Skel_Pedestrian_G3.uasset"
if [ -d "$content_dir/Carla" ] && [ ! -s "$content_sentinel" ]; then
    echo "WARNING: CARLA content looks incomplete -- missing/empty Skel_Pedestrian_G3.uasset."
    echo "         Re-run with --clean-content, or run 'git lfs pull' in $content_dir/Carla."
fi

# Remove known-broken upstream content assets that cannot be cooked. BP_Signs references a deleted
# UserDefinedStruct ("SignsStructure"), so it fails to compile and makes the package cook abort with
# 10 errors once the prop library is cooked. It is an editor-only sign-PLACEMENT tool (not spawned at
# runtime, and no shipped map references it), so deleting it is safe and lets the cook succeed. Done
# here (post-clone, before build) so every machine/setup self-heals without diverging the mirror.
for broken in \
    "Static/Static/BP_Signs.uasset" \
    "Static/Static/Blueprints/BP_Signs.uasset"; do
    bf="$content_dir/Carla/$broken"
    if [ -f "$bf" ]; then
        echo "Removing known-broken content asset (uncookable): $broken"
        # On a build host the content is a shared, read-only cache snapshot. Report the
        # failure instead of aborting the setup: the cook will fail later with a clear
        # error, and the host-side fix is to make the mount writable (an overlay mount
        # keeps the shared cache pristine while letting this removal succeed).
        rm -f "$bf" || echo "WARNING: could not remove $broken (content is read-only); the cook will fail on it."
    fi
done

# ── VERIFY UNREAL ENGINE ─────────────────────────────────────────────────────
if [ -n "${CARLA_UNREAL_ENGINE_PATH:-}" ] && [ -d "$CARLA_UNREAL_ENGINE_PATH/Engine" ]; then
    echo "Found Unreal Engine 5 at $CARLA_UNREAL_ENGINE_PATH"
else
    echo "ERROR: CARLA_UNREAL_ENGINE_PATH is not set or does not exist."
    echo "Please set CARLA_UNREAL_ENGINE_PATH to the root of your UE 5.7.4 source build."
    echo "Example: export CARLA_UNREAL_ENGINE_PATH=/path/to/UE_5_7_4"
    exit 1
fi

# ── BUILD SUMO netconvert (OSM -> OpenDRIVE converter, bundled for CarlaNet) ──
# CarlaNet shells out to stock SUMO `netconvert` at runtime to convert OSM maps
# to OpenDRIVE, replacing CARLA's old in-tree osm2odr fork. We build ONLY the
# `netconvert` target from SUMO release v1_27_0; that target's only real
# dependencies are Xerces-C and PROJ (FOX/GUI and GDAL are NOT needed). The apt
# prerequisites (cmake g++ libxerces-c-dev libproj-dev; proj.db ships with
# libproj-dev/proj-data) are installed by Util/SetupUtils/InstallPrerequisites.sh.
sumo_src=$workspace_path/Build/sumo-src
sumo_build=$workspace_path/Build/sumo-build
sumo_install=$workspace_path/Build/sumo-install

if [ "$clean_sumo" -eq 1 ] || [ "$clean_all" -eq 1 ]; then
    for d in "$sumo_build" "$sumo_install"; do
        [ -d "$d" ] && { echo "Cleaning $d"; rm -rf "$d"; }
    done
    if [ "$clean_all" -eq 1 ] && [ -d "$sumo_src" ]; then
        echo "Cleaning $sumo_src"; rm -rf "$sumo_src"
    fi
fi

if [ -f "$sumo_install/bin/netconvert" ]; then
    echo "Found SUMO netconvert at $sumo_install/bin/netconvert. Skipping SUMO build."
else
    echo "Building SUMO netconvert..."
    if [ ! -d "$sumo_src" ]; then
        echo "Cloning SUMO v1_27_0..."
        git clone --depth 1 --branch v1_27_0 \
            https://github.com/eclipse-sumo/sumo.git "$sumo_src"
    fi
    # Pin the exact commit (the tag already points here; this is an explicit guard).
    git -C "$sumo_src" checkout e238ea04b7150ba23a348a285d3048919fa4830b
    # Configure + build ONLY the netconvert target (Release).
    cmake -B "$sumo_build" -S "$sumo_src" -DCMAKE_BUILD_TYPE=Release
    cmake --build "$sumo_build" --target netconvert -j"$(nproc)"
    # The SUMO build emits binaries into Build/sumo-src/bin/netconvert.
    # Stage it (and a note about PROJ data) under Build/sumo-install for CarlaNet.
    mkdir -p "$sumo_install/bin"
    cp "$sumo_src/bin/netconvert" "$sumo_install/bin/netconvert"
    echo "Staged netconvert at $sumo_install/bin/netconvert."
fi
# CarlaNet locates the tool via env vars (see NETCONVERT_INTEGRATION.md):
#   CARLA_NETCONVERT -> the netconvert binary
#   PROJ_LIB (a.k.a. PROJ_DATA) -> the directory containing proj.db (from libproj-dev,
#     typically /usr/share/proj) so PROJ can resolve the +proj=tmerc projection.
echo "To use netconvert from CarlaNet, export:"
echo "  export CARLA_NETCONVERT=$sumo_install/bin/netconvert"
echo "  export PROJ_LIB=/usr/share/proj   # dir containing proj.db (from libproj-dev)"

# ── VibeUE editor MCP plugin (OPTIONAL, private mirror, pinned) ──────────────
# VibeUE is the in-editor MCP bridge used during digital-twin development. We pull a
# PRIVATE mirror of kevinpbuckley/VibeUE with the vibeue.com API-key validation removed
# (offline build). It is NOT referenced by the .uproject, so it is an optional, auto-
# discovered UE project plugin -- the CARLA build proceeds fine without it. Pinned to an
# exact commit; fetched over SSH using a key from --vibeue-ssh-key=<path> or $VIBEUE_SSH_KEY.
vibeue_dir="$workspace_path/Unreal/CarlaUnreal/Plugins/VibeUE"
vibeue_repo="git@github.com:sbrett9/VibeUE.git"
vibeue_pin="379373709e68ce7f2c4e3a26ff931f703d87b817"
vibeue_key="${vibeue_ssh_key:-${VIBEUE_SSH_KEY:-}}"
if [ -d "$vibeue_dir/.git" ]; then
    # Non-interactive ssh (with the deploy key when present) so a fresh container/batch run never
    # blocks on the github.com host-key prompt; without a key it still uses any ssh agent keys.
    if [ -n "$vibeue_key" ]; then
        export GIT_SSH_COMMAND="$ssh_noninteractive -i $vibeue_key -o IdentitiesOnly=yes"
    else
        export GIT_SSH_COMMAND="$ssh_noninteractive"
    fi
    if git -C "$vibeue_dir" fetch origin && git -C "$vibeue_dir" checkout "$vibeue_pin"; then
        echo "VibeUE pinned to $vibeue_pin."
    else
        echo "WARNING: VibeUE fetch/checkout failed; continuing (optional plugin)."
    fi
elif [ -n "$vibeue_key" ]; then
    echo "Cloning VibeUE private mirror (pinned $vibeue_pin)..."
    vibeue_tmp="$vibeue_dir.tmp.$$"
    if GIT_SSH_COMMAND="$ssh_noninteractive -i $vibeue_key -o IdentitiesOnly=yes" git clone "$vibeue_repo" "$vibeue_tmp" \
        && git -C "$vibeue_tmp" checkout "$vibeue_pin"; then
        rm -rf "$vibeue_dir" && mv "$vibeue_tmp" "$vibeue_dir"
        echo "VibeUE installed at $vibeue_pin."
    else
        rm -rf "$vibeue_tmp"
        echo "WARNING: VibeUE clone failed; leaving any existing copy. Continuing (optional plugin)."
    fi
elif [ -d "$vibeue_dir" ]; then
    echo "VibeUE present as a non-git copy; leaving as-is (no SSH key to convert it to a pinned clone)."
else
    echo "VibeUE skipped (optional MCP plugin). Pass --vibeue-ssh-key=<path> or set VIBEUE_SSH_KEY to fetch it."
fi

# ── Cesium for Unreal (SOURCE, pinned) + cesium-native build ─────────────────
# Build Cesium for Unreal FROM SOURCE at a pinned tag/commit instead of using the
# precompiled Marketplace plugin baked into the engine. cesium-unreal is NOT a
# clone-and-compile plugin: its CesiumRuntime/CesiumEditor modules link against
# cesium-native, a large C++ library that must be CMake-built and installed into the
# plugin's Source/ThirdParty BEFORE Unreal compiles the plugin (that compile happens
# later, in the editor build -- Scripts/Linux/BuildCarla.sh).
#
# The recipe mirrors cesium-unreal's own Linux CI for tag v2.27.0 (.github/workflows/
# buildLinux.yml + extern/unreal-linux-toolchain.cmake):
#   * The build uses the ENGINE's bundled clang toolchain via a CMake toolchain file,
#     so cesium-native and the UE-compiled plugin share one ABI (the engine's libc++).
#   * UNREAL_ENGINE_ROOT -> the engine (cesium-native borrows UE's bundled OpenSSL).
#   * CESIUM_VCPKG_RELEASE_ONLY=TRUE -- skip debug vcpkg deps (release-only, faster).
#   * No MSVC toolset pin / vcpkg toolset version (those are Windows-only).
cesium_tag="v2.27.0"
cesium_pin="c1214cbe002ea0c5c4d6a9c9032da0c97fe89d2c"   # commit v2.27.0 points at
cesium_repo="https://github.com/CesiumGS/cesium-unreal.git"
cesium_dir="$workspace_path/Unreal/CarlaUnreal/Plugins/CesiumForUnreal"
cesium_extern="$cesium_dir/extern"
cesium_extern_build="$cesium_extern/build"
cesium_thirdparty="$cesium_dir/Source/ThirdParty"

# Resolve the engine's bundled clang toolchain directory (the one the engine was built
# with). Priority: explicit env override -> the SDK version UBT itself pins -> newest
# installed toolchain -> error. This matches cesium-unreal's Linux CI, which derives
# UNREAL_ENGINE_COMPILER_DIR from <engine>/Engine/Extras/ThirdPartyNotUE/SDKs/HostLinux/
# Linux_x64/<clang-version>/x86_64-unknown-linux-gnu.
resolve_unreal_compiler_dir() {
    if [ -n "${UNREAL_ENGINE_COMPILER_DIR:-}" ]; then
        printf '%s\n' "$UNREAL_ENGINE_COMPILER_DIR"; return 0
    fi
    if [ -n "${LINUX_MULTIARCH_ROOT:-}" ]; then
        printf '%s\n' "${LINUX_MULTIARCH_ROOT%/}/x86_64-unknown-linux-gnu"; return 0
    fi
    local sdk_root="$CARLA_UNREAL_ENGINE_PATH/Engine/Extras/ThirdPartyNotUE/SDKs/HostLinux/Linux_x64"
    [ -d "$sdk_root" ] || return 1
    # Prefer the SDK version UBT pins (its name IS the toolchain dir name under Linux_x64).
    local ubt_src="$CARLA_UNREAL_ENGINE_PATH/Engine/Source/Programs/UnrealBuildTool/Platform/Linux"
    if [ -d "$ubt_src" ]; then
        local v
        for v in $(grep -rhoE 'v[0-9]+_clang-[0-9A-Za-z._-]+' "$ubt_src" 2>/dev/null | sort -Vru); do
            if [ -d "$sdk_root/$v" ]; then
                printf '%s\n' "$sdk_root/$v/x86_64-unknown-linux-gnu"; return 0
            fi
        done
    fi
    # Fall back to the newest installed toolchain dir (ls -d leaves a trailing slash).
    local newest
    newest="$(ls -d "$sdk_root"/*/ 2>/dev/null | sort -V | tail -n1)"
    [ -n "$newest" ] && { printf '%s\n' "${newest%/}/x86_64-unknown-linux-gnu"; return 0; }
    return 1
}

if [ "$skip_cesium" -eq 1 ]; then
    echo "Skipping Cesium for Unreal source build (--skip-cesium)."
else
    # -- Fetch + pin the plugin source (with submodules) --
    # .gitmodules pulls cesium-native + MikkTSpace/tidy-html5/swl-variant; --recursive
    # also gets cesium-native's own nested submodules.
    if [ -d "$cesium_dir/.git" ]; then
        echo "Pinning Cesium for Unreal to $cesium_tag ($cesium_pin)..."
        git -C "$cesium_dir" fetch --tags origin
        git -C "$cesium_dir" checkout --force "$cesium_pin"
        git -C "$cesium_dir" submodule update --init --recursive
    else
        echo "Cloning Cesium for Unreal $cesium_tag (pinned $cesium_pin, with submodules)..."
        rm -rf "$cesium_dir"
        git clone "$cesium_repo" "$cesium_dir"
        git -C "$cesium_dir" checkout --force "$cesium_pin"
        git -C "$cesium_dir" submodule update --init --recursive
    fi

    # The source .uplugin targets UE 5.5 by default; stamp it to 5.7 (exactly as the CI
    # does) so the editor doesn't flag an engine-version mismatch on this 5.7.4 build.
    uplugin="$cesium_dir/CesiumForUnreal.uplugin"
    [ -f "$uplugin" ] && sed -i 's/"EngineVersion": *"5\.5\.0"/"EngineVersion": "5.7.0"/' "$uplugin"

    # Build cesium-native WITH RTTI. cesium's extern/CMakeLists.txt forces -fno-rtti on Linux, but
    # the CARLA editor compiles with RTTI enabled (CarlaUnreal/Carla/CarlaTools set bUseRTTI=true for
    # rpclib/boost), which propagates to CesiumRuntime. CesiumRuntime then emits RTTI typeinfo for its
    # subclass of cesium-native's TileOcclusionRendererProxyPool and needs the base class typeinfo --
    # which a -fno-rtti cesium-native never emits, so the editor link fails with
    # "undefined symbol: typeinfo for Cesium3DTilesSelection::TileOcclusionRendererProxyPool".
    # Strip -fno-rtti so cesium-native emits typeinfo, matching the RTTI-on editor. (Re-applied each
    # run because the pinned checkout above resets extern/CMakeLists.txt; idempotent.)
    cesium_extern_cmakelists="$cesium_extern/CMakeLists.txt"
    [ -f "$cesium_extern_cmakelists" ] && sed -i 's/ -fno-rtti//g' "$cesium_extern_cmakelists"

    # -- Retire the precompiled Marketplace Cesium baked into the engine --
    # UE refuses to load two plugins named "CesiumForUnreal". Move the engine's Marketplace
    # copy OUT of the plugin search path (reversible) so our source plugin is the only one
    # discovered. DisabledPlugins sits at the engine ROOT (a sibling of Engine/), which UE
    # does not scan; merely renaming the folder in place would NOT work, since UE discovers
    # plugins by *.uplugin anywhere under Engine/Plugins regardless of folder name.
    mkt_root="$CARLA_UNREAL_ENGINE_PATH/Engine/Plugins/Marketplace"
    if [ -d "$mkt_root" ]; then
        mkt_uplugin="$(find "$mkt_root" -name 'CesiumForUnreal.uplugin' -print -quit 2>/dev/null || true)"
        if [ -n "$mkt_uplugin" ]; then
            mkt_cesium_dir="$(dirname "$mkt_uplugin")"
            disabled_root="$CARLA_UNREAL_ENGINE_PATH/DisabledPlugins"
            mkdir -p "$disabled_root"
            dest="$disabled_root/$(basename "$mkt_cesium_dir")"
            rm -rf "$dest"
            echo "Disabling Marketplace Cesium to avoid a duplicate-plugin conflict:"
            echo "  moving $mkt_cesium_dir"
            echo "      -> $dest"
            echo "  (restore by moving it back into Engine/Plugins/Marketplace)"
            mv "$mkt_cesium_dir" "$dest"
        else
            echo "No Marketplace Cesium plugin found in the engine (already disabled or never installed)."
        fi
    fi

    # -- Determine whether cesium-native is already built --
    # Built marker: a Linux-x86_64-Release lib dir holding .a files (mirrors the install dir
    # CMAKE_INSTALL_LIBDIR = Source/ThirdParty/lib/${SYSTEM}-${PROCESSOR}-Release).
    cesium_built=0
    cesium_lib_dir="$cesium_thirdparty/lib/Linux-x86_64-Release"
    if [ -d "$cesium_lib_dir" ] && ls "$cesium_lib_dir"/*.a >/dev/null 2>&1; then
        cesium_built=1
    fi

    if [ "$clean_cesium" -eq 1 ]; then
        for d in "$cesium_extern_build" "$cesium_thirdparty"; do
            [ -d "$d" ] && { echo "Cleaning $d"; rm -rf "$d"; }
        done
        cesium_built=0
    fi

    # --force-deps-rebuild: clear the ezvcpkg build outputs so vcpkg recompiles EVERY
    # dependency (keeping the vcpkg clone + downloads). On Linux ezvcpkg defaults its base
    # dir to $HOME/.ezvcpkg (or $EZVCPKG_BASEDIR); each vcpkg commit gets its own subdir.
    if [ "$force_deps_rebuild" -eq 1 ]; then
        ez_base="${EZVCPKG_BASEDIR:-$HOME/.ezvcpkg}"
        if [ -d "$ez_base" ]; then
            echo "Forcing a full vcpkg dependency rebuild (--force-deps-rebuild): clearing ezvcpkg cache under $ez_base..."
            for commit_dir in "$ez_base"/*/; do
                [ -d "$commit_dir" ] || continue
                for sub in installed packages buildtrees; do
                    [ -d "${commit_dir}${sub}" ] && { echo "  removing ${commit_dir}${sub}"; rm -rf "${commit_dir}${sub}"; }
                done
            done
        else
            echo "ezvcpkg cache not found at $ez_base; nothing to clear (vcpkg will build fresh)."
        fi
        cesium_built=0
    fi

    if [ "$cesium_built" -eq 1 ]; then
        echo "cesium-native already built (Source/ThirdParty present). Skipping (use --clean-cesium to rebuild)."
    else
        compiler_dir="$(resolve_unreal_compiler_dir || true)"
        if [ -z "$compiler_dir" ] || [ ! -x "$compiler_dir/bin/clang++" ]; then
            echo "ERROR: could not locate the engine's bundled clang toolchain." >&2
            echo "       Looked under $CARLA_UNREAL_ENGINE_PATH/Engine/Extras/ThirdPartyNotUE/SDKs/HostLinux/Linux_x64" >&2
            echo "       (resolved: ${compiler_dir:-<none>})." >&2
            echo "       Set UNREAL_ENGINE_COMPILER_DIR or LINUX_MULTIARCH_ROOT, or pass --skip-cesium." >&2
            exit 1
        fi
        echo "Building cesium-native with the engine clang toolchain at:"
        echo "  $compiler_dir"
        echo "(the first run downloads/compiles many vcpkg deps -- expect many minutes and several GB)..."

        # Env consumed by extern/unreal-linux-toolchain.cmake + the vcpkg overlay builds.
        export UNREAL_ENGINE_ROOT="$CARLA_UNREAL_ENGINE_PATH"
        export UNREAL_ENGINE_COMPILER_DIR="$compiler_dir"
        export UNREAL_ENGINE_LIBCXX_DIR="$CARLA_UNREAL_ENGINE_PATH/Engine/Source/ThirdParty/Unix/LibCxx"
        export CESIUM_VCPKG_RELEASE_ONLY=TRUE

        # Configured from extern/ (NOT extern/cesium-native/) so CMAKE_INSTALL_PREFIX lands in
        # ../Source/ThirdParty, where Cesium for Unreal expects cesium-native. The toolchain
        # file pins the compiler/sysroot/libc++ to the engine's; pass an absolute path so it
        # resolves regardless of the build dir.
        cmake -B "$cesium_extern_build" -S "$cesium_extern" -G Ninja \
            -DCMAKE_TOOLCHAIN_FILE="$cesium_extern/unreal-linux-toolchain.cmake" \
            -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
            -DCMAKE_BUILD_TYPE=Release
        cmake --build "$cesium_extern_build" --config Release --target install -j"$(nproc)"
        echo "cesium-native installed into $cesium_thirdparty."
    fi
fi

# ── BUILD CARLA ──────────────────────────────────────────────────────────────
# Clear the CARLA CMake cache if requested so the next configure re-detects everything.
if [ "$clean_carla" -eq 1 ]; then
    echo "Clearing CARLA CMake cache (--clean-carla)..."
    rm -f "$workspace_path/Build/CMakeCache.txt"
    rm -rf "$workspace_path/Build/CMakeFiles"
fi

echo "Configuring the CARLA CMake project..."
configure_args=(
    -G Ninja
    -S .
    -B Build
    "--toolchain=$workspace_path/CMake/Toolchain.cmake"
    -DLAUNCH_ARGS=-prefernvidia
    -DCMAKE_BUILD_TYPE=Release
    -DENABLE_ROS2=ON
    "-DCARLA_UNREAL_ENGINE_PATH=$CARLA_UNREAL_ENGINE_PATH"
)
if [ -n "$python_root" ]; then
    configure_args+=("-DPython_ROOT_DIR=$python_root" "-DPython3_ROOT_DIR=$python_root")
fi
if [ "$with_python_api" -eq 0 ]; then
    # DEFAULT: CarlaNet-only. Skip the legacy Boost.Python `carla` extension -- it is
    # independent of the UE editor and CarlaNet, and its numpy<2 / Python<=3.12 build
    # constraints are irrelevant to the pure-Python `carlanet` shim. Pass --with-python-api
    # to build it.
    echo "Legacy PythonAPI disabled by default (-DBUILD_PYTHON_API=OFF). Pass --with-python-api to build it."
    configure_args+=(-DBUILD_PYTHON_API=OFF)
fi
if [ "$with_tests" -eq 0 ]; then
    # CarlaNet-only: skip LibCarla's C++ unit tests (they pull in googletest).
    echo "LibCarla tests disabled by default (-DBUILD_LIBCARLA_TESTS=OFF). Pass --with-tests to build them."
    configure_args+=(-DBUILD_LIBCARLA_TESTS=OFF)
fi
# Reuse an in-tree StreetMap plugin instead of letting FetchContent re-download it (its
# archive ref can drift/404 upstream). FETCHCONTENT_SOURCE_DIR_<NAME> tells CMake the
# source is already provided, so it skips the download and never clobbers the checkout.
streetmap_dir="$workspace_path/Unreal/CarlaUnreal/Plugins/StreetMap"
if [ -d "$streetmap_dir" ] && [ -n "$(ls -A "$streetmap_dir" 2>/dev/null)" ]; then
    echo "Reusing existing in-tree StreetMap plugin (skipping FetchContent download)."
    configure_args+=("-DFETCHCONTENT_SOURCE_DIR_STREETMAP=$streetmap_dir")
fi

cmake "${configure_args[@]}"

echo "Building CARLA..."
cmake --build Build

if [ "$with_python_api" -eq 1 ]; then
    echo "Installing Python API..."
    cmake --build Build --target carla-python-api-install
    echo "CARLA Python API build+install succeeded."
else
    echo "CARLA build succeeded (legacy PythonAPI skipped)."
fi

# ── POST-BUILD STEPS ─────────────────────────────────────────────────────────
if [ $launch -eq 1 ]; then
    echo "Launching Carla - Unreal Editor..."
    cmake --build Build --target launch
fi
