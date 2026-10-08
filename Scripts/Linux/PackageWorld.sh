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
# Install the result with InstallWorld.sh, which sits beside this script.
#
# This script needs a CARLA checkout with the editor project: it is for whoever makes worlds. A CARLA
# distribution ships it in world-tools/, which is inside no checkout, so there it takes --carla-root.
#
# --distribution <folder> records the base release from a CARLA distribution instead of needing a
# base cook on this machine: the distribution's VERSION names the CARLA commit it was built from, the
# release is named as the base cook names it (the short form of that commit), and its
# CarlaServer/CarlaUnreal/AssetRegistry.bin -- the list of everything the distribution already
# contains -- is copied to Unreal/CarlaUnreal/Releases/<release>/Linux/AssetRegistry.bin. Then the
# world is cooked as usual. Refused unless the checkout is at the distribution's CARLA commit, because
# the cook reads the checkout's content and subtracts the distribution's.

set -uo pipefail

script_dir="$(cd "$(dirname "$(realpath "${BASH_SOURCE[0]}")")" && pwd)"

# Two names for the same target, not interchangeable. UAT's -Platform and -TargetPlatform want the
# TARGET name; the directories a cook writes -- Releases/ and Saved/StagedBuilds/ -- are named for the
# COOK PLATFORM. On Linux they happen to be the same word, unlike Windows where the target is Win64
# and the directory is Windows. Kept as two variables so the distinction survives.
platform="Linux"
cook_platform="Linux"

world=""
based_on_release=""
distribution=""
carla_root=""
output_dir=""
config="Development"
config_given=0
skip_cook=0
unreal_engine_root="${CARLA_UNREAL_ENGINE_PATH:-}"

usage() {
    cat <<'EOF'
Usage: PackageWorld.sh --world <name> [options]

Cook one generated world and package it as a single deliverable file.

Options:
  --world <name>             Exported world to package (required).
  --based-on-release <name>  Release to cook against (default: current short Carla commit).
  --distribution <folder>    Record the release from this CARLA distribution, then cook against it.
                             The checkout must be at the distribution's CARLA commit; the config is
                             taken from the folder name (Carla-<version>-Linux-<config>) when
                             --config is not given.
  --carla-root <path>        The CARLA checkout (default: the one this script is in; required when
                             run from a distribution's world-tools folder).
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
        --distribution)       distribution="$2"; shift ;;
        --distribution=*)     distribution="${1#*=}" ;;
        --carla-root)         carla_root="$2"; shift ;;
        --carla-root=*)       carla_root="${1#*=}" ;;
        --output-directory)   output_dir="$2"; shift ;;
        --output-directory=*) output_dir="${1#*=}" ;;
        --config)             config="$2"; config_given=1; shift ;;
        --config=*)           config="${1#*=}"; config_given=1 ;;
        --skip-cook)          skip_cook=1 ;;
        --unreal-engine-root) unreal_engine_root="$2"; shift ;;
        --unreal-engine-root=*) unreal_engine_root="${1#*=}" ;;
        -h|--help)            usage; exit 0 ;;
        *) echo "ERROR: unknown argument '$1'" >&2; usage; exit 1 ;;
    esac
    shift
done

[ -n "$world" ] || { echo "ERROR: --world is required." >&2; usage; exit 1; }

# The checkout: named, or the one this script sits in (Scripts/Linux, two folders down). A
# distribution ships this script in world-tools/, which is inside no checkout, so there it is named.
[ -n "$carla_root" ] || carla_root="$script_dir/../.."
if [ ! -f "$carla_root/Unreal/CarlaUnreal/CarlaUnreal.uproject" ]; then
    echo "ERROR: no CARLA checkout at $carla_root (no Unreal/CarlaUnreal/CarlaUnreal.uproject)." >&2
    echo "       Pass --carla-root <checkout>. Run from a distribution's world-tools folder, this script" >&2
    echo "       is inside no checkout." >&2
    exit 1
fi
carla_root="$(cd "$carla_root" && pwd)"
repo_parent="$(cd "$carla_root/.." && pwd)"
project_dir="$carla_root/Unreal/CarlaUnreal"
uproject="$project_dir/CarlaUnreal.uproject"

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
    echo "            $carla_root/Scripts/Linux/MakeDistribution.sh --build" >&2
    echo "         3. run this again" >&2
    echo "" >&2
    echo "       Or leave it as it is and deliver the base package, which already contains the world." >&2
    exit 1
fi

# ── The release, recorded from a distribution when one is named ─────────────
#
# A DLC cook subtracts what the base already holds, and the base's asset registry is that list. The
# base cook archives it under Releases/<release>/<platform>/ on the machine that cooked the base; a
# distribution carries the same registry, byte for byte, as CarlaServer/CarlaUnreal/AssetRegistry.bin.
# The cook reads Metadata/DevelopmentAssetRegistry.bin there first and falls back to AssetRegistry.bin
# (CookOnTheFlyServer.cpp, RecordDLCPackagesFromBaseGame), so the copy alone is a release to cook
# against. The release is named the way the base cook names it -- the short form of the CARLA commit
# (Unreal/Package/CookGeneratedWorlds.cmake.in) -- and the world cooks the checkout's content, so the
# checkout has to be at that same commit.

if [ -n "$distribution" ]; then
    if [ ! -d "$distribution" ]; then
        echo "ERROR: no distribution folder at $distribution." >&2
        exit 1
    fi
    distribution="$(cd "$distribution" && pwd)"
    dist_version="$distribution/VERSION"
    if [ ! -f "$dist_version" ]; then
        echo "ERROR: $distribution has no VERSION file, so which CARLA it was built from is unknown." >&2
        echo "       Name the distribution's root: the folder holding VERSION and CarlaServer/." >&2
        exit 1
    fi
    dist_commit="$(sed -n 's/^[[:space:]]*Carla git hash:[[:space:]]*\([0-9a-fA-F]\{40\}\)[[:space:]]*$/\1/p' "$dist_version" | head -1 | tr '[:upper:]' '[:lower:]')"
    if [ -z "$dist_commit" ]; then
        echo "ERROR: $dist_version names no Carla git hash, so the release it was cooked as cannot be named." >&2
        exit 1
    fi

    dist_server="$distribution/CarlaServer"
    if [ ! -f "$dist_server/CarlaUnreal.sh" ]; then
        if [ -f "$dist_server/CarlaUnreal.exe" ]; then
            echo "ERROR: $distribution is a Windows distribution, and this script cooks for Linux. Use PackageWorld.ps1." >&2
        else
            echo "ERROR: $distribution holds no CarlaServer/CarlaUnreal.sh; it is not a CARLA distribution." >&2
        fi
        exit 1
    fi
    dist_registry="$dist_server/CarlaUnreal/AssetRegistry.bin"
    if [ ! -f "$dist_registry" ]; then
        echo "ERROR: $distribution carries no CarlaServer/CarlaUnreal/AssetRegistry.bin, the list of what it holds." >&2
        echo "       Without it there is nothing to cook a world against." >&2
        exit 1
    fi

    # A distribution's folder is named Carla-<version>-Linux-<config>, and a world must be cooked in the
    # configuration of the package it is installed into.
    dist_leaf="$(basename "$distribution")"
    case "$dist_leaf" in
        *-Development) dist_config="Development" ;;
        *-Shipping)    dist_config="Shipping" ;;
        *-Debug)       dist_config="Debug" ;;
        *)             dist_config="" ;;
    esac
    if [ -n "$dist_config" ]; then
        if [ "$config_given" -eq 1 ] && [ "$config" != "$dist_config" ]; then
            echo "ERROR: --config $config, but $dist_leaf is a $dist_config distribution; the world would not load in it." >&2
            exit 1
        fi
        config="$dist_config"
    fi

    checkout_commit="$(git -C "$carla_root" rev-parse HEAD 2>/dev/null | tr '[:upper:]' '[:lower:]')"
    if [ -z "$checkout_commit" ]; then
        echo "ERROR: could not read the commit $carla_root is at." >&2
        exit 1
    fi
    if [ "$checkout_commit" != "$dist_commit" ]; then
        echo "ERROR: this checkout is not at the commit the distribution was built from:" >&2
        echo "       checkout     : $checkout_commit ($carla_root)" >&2
        echo "       distribution : $dist_commit ($dist_version)" >&2
        echo "       The cook reads the checkout's content and subtracts the distribution's, so both must" >&2
        echo "       be one build. Check out $dist_commit, or package against a release cooked here." >&2
        exit 1
    fi
    dist_release="$(git -C "$carla_root" rev-parse --short "$dist_commit" 2>/dev/null)"
    if [ -z "$dist_release" ]; then
        echo "ERROR: could not name the release for commit $dist_commit." >&2
        exit 1
    fi
    if [ -n "$based_on_release" ] && [ "$based_on_release" != "$dist_release" ]; then
        echo "ERROR: --based-on-release $based_on_release, but the distribution's release is $dist_release. Give one or the other." >&2
        exit 1
    fi
    based_on_release="$dist_release"

    record_dir="$project_dir/Releases/$based_on_release/$cook_platform"
    recorded="$record_dir/AssetRegistry.bin"
    if [ -f "$recorded" ]; then
        if ! cmp -s "$recorded" "$dist_registry"; then
            echo "ERROR: release '$based_on_release' is already recorded here, from another build:" >&2
            echo "       $recorded differs from $dist_registry." >&2
            echo "       Remove $record_dir to record the release from this distribution." >&2
            exit 1
        fi
        echo "release '$based_on_release' is already recorded from this distribution's asset registry"
    elif [ -f "$record_dir/Metadata/DevelopmentAssetRegistry.bin" ]; then
        # The cook would read that file first, and it did not come from the distribution.
        echo "ERROR: release '$based_on_release' holds a registry from a cook on this machine but no AssetRegistry.bin:" >&2
        echo "       $record_dir" >&2
        echo "       Remove it to record the release from this distribution." >&2
        exit 1
    else
        mkdir -p "$record_dir" || { echo "ERROR: could not create $record_dir." >&2; exit 1; }
        cp "$dist_registry" "$recorded" || { echo "ERROR: could not copy $dist_registry." >&2; exit 1; }
        echo "recorded release '$based_on_release' from $dist_registry"
    fi
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
    echo "       (it is on by default): $carla_root/Scripts/Linux/MakeDistribution.sh --build" >&2
    echo "       Or record it from the distribution the world is for: --distribution <folder>." >&2
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
  "releaseVersion": "$(for part in MAJOR MINOR PATCH; do sed -nE "s/^[[:space:]]*set[[:space:]]*\([[:space:]]*CARLA_VERSION_${part}[[:space:]]+([0-9]+)[[:space:]]*\).*/\1/p" "$carla_root/CMakeLists.txt" | head -1; done | paste -sd. -)",
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
echo "  $script_dir/InstallWorld.sh --package '$zip_path' --into <package directory>"
