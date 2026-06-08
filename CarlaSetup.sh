#! /bin/bash

set -e

interactive=0
skip_prerequisites=0
launch=0
python_root=
vibeue_ssh_key=

workspace_path="$(dirname $(realpath "${BASH_SOURCE[-1]}"))"
echo "workspace_path=$workspace_path"

options=$(\
    getopt \
    -o "i,p,l,pyroot:" \
    --long "interactive,skip-prerequisites,launch,python-root:,vibeue-ssh-key:" \
    -n 'CarlaSetup.sh' -- "$@")

eval set -- "$options"
while true; do
    case "$1" in
        -i|--interactive)
            interactive=1
            shift
            ;;
        -p|--skip-prerequisites)
            skip_prerequisites=1
            shift
            ;;
        -l|--launch)
            launch=1
            shift
            ;;
        -pyroot|--python-root)
            python_root=$2
            shift 2
            ;;
        --vibeue-ssh-key)
            vibeue_ssh_key=$2
            shift 2
            ;;
        --)
            shift
            break
            ;;
        *)
            ;;
    esac
done

# Check if root is asking for a password:
if [ -z "$EUID" ]; then
    EUID=$(id -u)
fi
if [ "$EUID" -ne 0 ] && [ $interactive -eq 0 ] && [ $skip_prerequisites -eq 0 ]; then
    if ! sudo -n true 2>/dev/null; then
        echo "Please run 'sudo -v' before running this script, or pass --interactive or --skip-prerequisites."
        exit 1
    fi
fi

# Check for Git credentials:
if [ -z "$GIT_LOCAL_CREDENTIALS" ]; then
    if [ $interactive -eq 1 ]; then
        echo "Warning: git credentials are not set. You may be required to manually enter them later."
    else
        echo "Git credentials are not set, can not continue setup in unattended mode."
        exit 1
    fi
else
    echo "Found git credentials."
fi

# -- PREREQUISITES INSTALL STEP --
if [ $skip_prerequisites -eq 0 ]; then
    python_path=python3
    if [ "$python_root" != "" ]; then
        python_path=${python_root}/python3
    fi
    echo "Installing prerequisites..."
    bash -x Util/SetupUtils/InstallPrerequisites.sh --python-path=$python_path
else
    echo "Skipping prerequisites install step."
fi

# -- CLONE CONTENT --
if [ -d $workspace_path/Unreal/CarlaUnreal/Content ]; then
    echo "Found CARLA content."
else
    echo "Could not find CARLA content. Downloading..."
    mkdir -p $workspace_path/Unreal/CarlaUnreal/Content
    git \
        -C $workspace_path/Unreal/CarlaUnreal/Content \
        clone \
        -b ue5-dev \
        https://bitbucket.org/carla-simulator/carla-content.git \
        Carla
fi

# -- DOWNLOAD + BUILD UNREAL ENGINE --
if [ -n "$CARLA_UNREAL_ENGINE_PATH" ] && [ -d "$CARLA_UNREAL_ENGINE_PATH" ]; then
    echo "Found Unreal Engine 5 at $CARLA_UNREAL_ENGINE_PATH"
else
    echo "ERROR: CARLA_UNREAL_ENGINE_PATH is not set or does not exist."
    echo "Please set CARLA_UNREAL_ENGINE_PATH to the root of your UE 5.7.4 source build."
    echo "Example: export CARLA_UNREAL_ENGINE_PATH=/path/to/UE_5_7_4"
    exit 1
fi

# -- BUILD SUMO netconvert (OSM -> OpenDRIVE converter, bundled for CarlaNet) --
# CarlaNet shells out to stock SUMO `netconvert` at runtime to convert OSM maps
# to OpenDRIVE, replacing CARLA's old in-tree osm2odr fork. We build ONLY the
# `netconvert` target from SUMO release v1_27_0; that target's only real
# dependencies are Xerces-C and PROJ (FOX/GUI and GDAL are NOT needed).
# apt prerequisites belong in Util/SetupUtils/InstallPrerequisites.sh:
#   cmake g++ libxerces-c-dev libproj-dev   (proj.db ships with libproj-dev/proj-data)
sumo_src=$workspace_path/Build/sumo-src
sumo_build=$workspace_path/Build/sumo-build
sumo_install=$workspace_path/Build/sumo-install
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

# -- VibeUE editor MCP plugin (OPTIONAL, private mirror, pinned) --
# VibeUE is the in-editor MCP bridge used during digital-twin development. We pull a
# PRIVATE mirror of kevinpbuckley/VibeUE with the vibeue.com API-key validation removed
# (offline build). It is NOT referenced by the .uproject, so it is an optional, auto-
# discovered UE project plugin -- the CARLA build proceeds fine without it. Pinned to an
# exact commit; fetched over SSH using a key from --vibeue-ssh-key=<path> or $VIBEUE_SSH_KEY.
vibeue_dir="$workspace_path/Unreal/CarlaUnreal/Plugins/VibeUE"
vibeue_repo="git@github.com:sbrett9/VibeUE.git"
vibeue_pin="379373709e68ce7f2c4e3a26ff931f703d87b817"
vibeue_key="${vibeue_ssh_key:-$VIBEUE_SSH_KEY}"
if [ -d "$vibeue_dir/.git" ]; then
    [ -n "$vibeue_key" ] && export GIT_SSH_COMMAND="ssh -i $vibeue_key -o IdentitiesOnly=yes"
    if git -C "$vibeue_dir" fetch origin && git -C "$vibeue_dir" checkout "$vibeue_pin"; then
        echo "VibeUE pinned to $vibeue_pin."
    else
        echo "WARNING: VibeUE fetch/checkout failed; continuing (optional plugin)."
    fi
elif [ -n "$vibeue_key" ]; then
    echo "Cloning VibeUE private mirror (pinned $vibeue_pin)..."
    vibeue_tmp="$vibeue_dir.tmp.$$"
    if GIT_SSH_COMMAND="ssh -i $vibeue_key -o IdentitiesOnly=yes" git clone "$vibeue_repo" "$vibeue_tmp" \
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

# -- BUILD CARLA --
echo "Configuring the CARLA CMake project..."
cmake -G Ninja -S . -B Build \
    --toolchain=$PWD/CMake/Toolchain.cmake \
    -DLAUNCH_ARGS="-prefernvidia" \
    -DCMAKE_BUILD_TYPE=Release \
    -DENABLE_ROS2=ON \
    -DPython_ROOT_DIR=${python_root} \
    -DPython3_ROOT_DIR=${python_root} \
    -DCARLA_UNREAL_ENGINE_PATH=$CARLA_UNREAL_ENGINE_PATH
echo "Building CARLA..."
cmake --build Build
echo "Installing Python API..."
cmake --build Build --target carla-python-api-install
echo "CARLA Python API build+install succeeded."

# -- POST-BUILD STEPS --
if [ $launch -eq 1 ]; then
    echo "Launching Carla - Unreal Editor..."
    cmake --build Build --target launch
fi
