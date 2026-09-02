#!/usr/bin/env bash
#
# PackageWorld.sh — Linux equivalent of Scripts/Windows/PackageWorld.ps1
#
# Cook one generated world on its own and package it as a single file for delivery.
#
# Produces a .zip holding just one world -- roughly a hundred megabytes -- that somebody else can add
# to an existing CARLA package without being sent the whole 30+ GB build.
#
# The world is cooked as DLC against a release the base cook archived. That archive lists what the
# base package already contains, so this cook can leave out the shared materials, textures and engine
# content and emit only what the world itself adds. Without it there is nothing to subtract from, and
# the cook cannot tell "already shipped" from "new".
#
# The world must already have been exported as a plugin under
# Unreal/CarlaUnreal/Plugins/GeneratedWorlds -- that is what the World Package Importer's
# "Make this world available to packaged builds" checkbox does.
#
# Install the result with InstallWorld.sh.

set -uo pipefail

script_dir="$(cd "$(dirname "$(realpath "${BASH_SOURCE[0]}")")" && pwd)"
carla_root="$(cd "$script_dir/../.." && pwd)"
repo_parent="$(cd "$carla_root/.." && pwd)"

project_dir="$carla_root/Unreal/CarlaUnreal"
uproject="$project_dir/CarlaUnreal.uproject"

# Two names for the same target, not interchangeable. UAT's -Platform and -TargetPlatform want the
# TARGET name; the directories a cook writes -- Releases/ and Saved/StagedBuilds/ -- are named for the
# COOK PLATFORM. On Linux they happen to be the same word, unlike Windows where the target is Win64
# and the directory is Windows. Kept as two variables so the distinction survives.
platform="Linux"
cook_platform="Linux"

world=""
based_on_release=""
output_dir=""
config="Development"
skip_cook=0
unreal_engine_root="${CARLA_UNREAL_ENGINE_PATH:-}"

usage() {
    cat <<'EOF'
Usage: PackageWorld.sh --world <name> [options]

Cook one generated world and package it as a single deliverable file.

Options:
  --world <name>             Exported world to package (required).
  --based-on-release <name>  Release to cook against (default: current short Carla commit).
  --output-directory <path>  Where to write the .zip (default: Build/WorldPackages).
  --config <cfg>             Development (default) | Shipping | Debug.
  --skip-cook                Package an existing cook without re-cooking.
  --unreal-engine-root <p>   Engine root (default: $CARLA_UNREAL_ENGINE_PATH, else <repo-parent>/UE_5_7_4).
  -h, --help                 This text.

The world must already be exported as a plugin - use the World Package Importer's
"Make this world available to packaged builds" checkbox. Install the result with InstallWorld.sh.
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --world)              world="$2"; shift ;;
        --world=*)            world="${1#*=}" ;;
        --based-on-release)   based_on_release="$2"; shift ;;
        --based-on-release=*) based_on_release="${1#*=}" ;;
        --output-directory)   output_dir="$2"; shift ;;
        --output-directory=*) output_dir="${1#*=}" ;;
        --config)             config="$2"; shift ;;
        --config=*)           config="${1#*=}" ;;
        --skip-cook)          skip_cook=1 ;;
        --unreal-engine-root) unreal_engine_root="$2"; shift ;;
        --unreal-engine-root=*) unreal_engine_root="${1#*=}" ;;
        -h|--help)            usage; exit 0 ;;
        *) echo "ERROR: unknown argument '$1'" >&2; usage; exit 1 ;;
    esac
    shift
done

[ -n "$world" ] || { echo "ERROR: --world is required." >&2; usage; exit 1; }
[ -n "$unreal_engine_root" ] || unreal_engine_root="$repo_parent/UE_5_7_4"
[ -n "$output_dir" ] || output_dir="$carla_root/Build/WorldPackages"

run_uat="$unreal_engine_root/Engine/Build/BatchFiles/RunUAT.sh"
plugin_dir="$project_dir/Plugins/GeneratedWorlds/$world"
uplugin="$plugin_dir/$world.uplugin"

# ── Preconditions, each with the remedy rather than just the complaint ───────

if [ ! -d "$plugin_dir" ]; then
    echo "ERROR: no exported world named '$world'." >&2
    echo "       Looked in: $plugin_dir" >&2
    echo "       Export one with the World Package Importer, leaving" >&2
    echo "       'Make this world available to packaged builds' ticked." >&2
    exit 1
fi
if [ ! -f "$uplugin" ]; then
    echo "ERROR: '$world' has no $world.uplugin; the export did not finish." >&2
    exit 1
fi

# A world ships one way or the other. Left unmarked it is cooked into the base package, and cooking it
# separately against that same base yields nothing, because every one of its packages is already there.
marker="$plugin_dir/DeliverSeparately.txt"
if [ ! -f "$marker" ]; then
    echo "ERROR: '$world' is not marked for separate delivery, so it is cooked into the base package." >&2
    echo "       Packaging it as an addition to that base would produce an empty world." >&2
    echo "" >&2
    echo "       To deliver it separately instead:" >&2
    echo "         1. create $marker" >&2
    echo "         2. re-cook the base so it no longer contains the world:" >&2
    echo "            ./Scripts/Linux/MakeDistribution.sh --build" >&2
    echo "         3. run this again" >&2
    echo "" >&2
    echo "       Or leave it as it is and deliver the base package, which already contains the world." >&2
    exit 1
fi

if [ -z "$based_on_release" ]; then
    based_on_release="$(git -C "$carla_root" log -1 --format=%h 2>/dev/null)"
    if [ -z "$based_on_release" ]; then
        echo "ERROR: could not read the current commit to name the release. Pass --based-on-release." >&2
        exit 1
    fi
fi

release_dir="$project_dir/Releases/$based_on_release/$cook_platform"
if [ ! -d "$release_dir" ]; then
    echo "ERROR: no release '$based_on_release' to cook against." >&2
    echo "       Looked in: $release_dir" >&2
    echo "       The base package has to be cooked first, with CARLA_COOK_CREATE_RELEASE_VERSION on" >&2
    echo "       (it is on by default): ./Scripts/Linux/MakeDistribution.sh --build" >&2
    exit 1
fi

# What this build promises a delivered world. A declaration, not a fingerprint: see
# Unreal/CarlaUnreal/Config/DefaultWorldInterface.ini.
interface_ini="$project_dir/Config/DefaultWorldInterface.ini"
iface_major="$(sed -n 's/^[[:space:]]*Major[[:space:]]*=[[:space:]]*\([0-9]\+\).*/\1/p' "$interface_ini" 2>/dev/null | head -1)"
iface_minor="$(sed -n 's/^[[:space:]]*Minor[[:space:]]*=[[:space:]]*\([0-9]\+\).*/\1/p' "$interface_ini" 2>/dev/null | head -1)"
if [ -z "$iface_major" ] || [ -z "$iface_minor" ]; then
    echo "ERROR: could not read the world interface version from $interface_ini." >&2
    echo "       Without it there is nothing to record for an installer to check against." >&2
    exit 1
fi

echo "world        : $world"
echo "release      : $based_on_release"
echo "config       : $config"
echo "output       : $output_dir"

# ── Cook the world on its own ────────────────────────────────────────────────
#
# -iterate is deliberately absent: UAT throws outright when it is combined with
# -BasedOnReleaseVersion. So is -CreateReleaseVersion, which cannot be combined with -DLCName.
# -DLCIncludeEngineContent is NOT passed: the world is self-contained inside its plugin, so the
# default restriction to the plugin's own content is what we want, and it fails loudly if something
# has escaped it. -stagingdirectory is left unset because with -DLCName UAT stages into the plugin's
# own Saved/StagedBuilds; naming the base package's would stage this world on top of it.

stage_root="$plugin_dir/Saved/StagedBuilds/$cook_platform"

if [ "$skip_cook" -eq 0 ]; then
    echo ""
    echo "[world] cooking $world against release $based_on_release"
    "$run_uat" BuildCookRun \
        "-project=$uproject" \
        -nocompileeditor -nop4 -cook -stage -package \
        "-clientconfig=$config" \
        "-TargetPlatform=$platform" "-Platform=$platform" \
        "-BasedOnReleaseVersion=$based_on_release" \
        "-DLCName=$uplugin"
    rc=$?
    if [ $rc -ne 0 ]; then
        echo "" >&2
        echo "ERROR: cook failed (exit $rc)." >&2
        echo "       If it complained that content is 'being referenced by DLC', something the world" >&2
        echo "       needs lives outside its plugin. Re-export the world and try again." >&2
        exit $rc
    fi
fi

if [ ! -d "$stage_root" ]; then
    echo "ERROR: the cook produced no staged output at $stage_root." >&2
    exit 1
fi

payload="$(find "$stage_root" -type d -name "$world" -exec test -f '{}'/"$world.uplugin" \; -print 2>/dev/null | head -1)"
if [ -z "$payload" ]; then
    echo "ERROR: could not find the cooked $world plugin under $stage_root." >&2
    exit 1
fi

# A DLC cook that produces only a descriptor and a registry SUCCEEDS. The commonest cause is that the
# world is already in the base release, so every one of its packages is correctly already cooked and
# there is nothing left to add. Packaging that hands somebody a world that installs and then fails to
# load, so refuse: an empty world package is worse than a failed cook, because it fails at the
# recipient instead of at the person who made it.
cooked_map="$(find "$payload" -type f -name '*.umap' 2>/dev/null | head -1)"
cooked_assets="$(find "$payload" -type f \( -name '*.uasset' -o -name '*.uexp' \) 2>/dev/null | wc -l)"
if [ -z "$cooked_map" ] || [ "$cooked_assets" -eq 0 ]; then
    echo "" >&2
    echo "ERROR: the cook produced no content for '$world' -- $cooked_assets asset file(s), no level." >&2
    echo "       The world is almost certainly already part of release '$based_on_release', so cooking" >&2
    echo "       it again as an addition to that release correctly yields nothing." >&2
    echo "" >&2
    echo "       A world ships one way or the other, not both." >&2
    exit 1
fi

# ── Describe what this is, so an installer can refuse the wrong package ──────
#
# Installability is decided by the declared world interface version, not by a hash. A hash only
# answers "identical?", so it refuses builds differing in ways no world can observe while saying
# nothing about whether two builds are actually compatible. The commit hashes below are recorded for
# identification only; nothing compares them.

git_hash() { [ -d "$1" ] && git -C "$1" log -1 --format=%H 2>/dev/null || echo ""; }

mkdir -p "$output_dir"
staging="$(mktemp -d)"
trap 'rm -rf "$staging"' EXIT

cp -a "$payload" "$staging/$world"
cat > "$staging/world.json" <<EOF
{
  "formatVersion": 1,
  "world": "$world",
  "mapPackage": "/$world/Maps/$world",
  "worldInterfaceMajor": $iface_major,
  "worldInterfaceMinor": $iface_minor,
  "basedOnRelease": "$based_on_release",
  "config": "$config",
  "platform": "$platform",
  "carlaGitHash": "$(git_hash "$carla_root")",
  "contentGitHash": "$(git_hash "$project_dir/Content/Carla")",
  "unrealGitHash": "$(git_hash "$unreal_engine_root")",
  "packagedAtUtc": "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
}
EOF

zip_path="$output_dir/$world.zip"
rm -f "$zip_path"
( cd "$staging" && zip -qr "$zip_path" . ) || { echo "ERROR: zip failed (is 'zip' installed?)" >&2; exit 1; }

size_mb="$(du -m "$zip_path" | cut -f1)"
echo ""
echo "Packaged $world"
echo "  file    : $zip_path"
echo "  size    : ${size_mb} MB"
echo "  needs   : world interface ${iface_major}.x, minor ${iface_minor} or later; $config, $platform"
echo ""
echo "Install it with:"
echo "  ./Scripts/Linux/InstallWorld.sh --package '$zip_path' --into <package directory>"
