#!/usr/bin/env bash
#
# InstallWorld.sh — Linux equivalent of Scripts/Windows/InstallWorld.ps1
#
# Install a world packaged by PackageWorld.sh into an existing CARLA package.
#
# Unpacks a world into the package's CarlaUnreal/Plugins/GeneratedWorlds. The server discovers it on
# the next launch and it can be loaded by name.
#
# Before unpacking, the world's recorded build is checked against the package's own declaration. A
# world cooked against one build will not load against another -- cooked files carry package versions
# and name base content by id -- and the failure that would otherwise reach the user is an unexplained
# crash at load. Checking here turns that into a sentence.

set -uo pipefail

package=""
into=""
force=0

usage() {
    cat <<'EOF'
Usage: InstallWorld.sh --package <world.zip> --into <package directory> [--force]

Install a packaged world into an existing CARLA package.

Options:
  --package <path>   The .zip written by PackageWorld.sh (required).
  --into <path>      Root of the CARLA package: the directory holding CarlaUnreal/ (required).
  --force            Install despite a build mismatch; the world may then fail to load.
  -h, --help         This text.
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --package)   package="$2"; shift ;;
        --package=*) package="${1#*=}" ;;
        --into)      into="$2"; shift ;;
        --into=*)    into="${1#*=}" ;;
        --force)     force=1 ;;
        -h|--help)   usage; exit 0 ;;
        *) echo "ERROR: unknown argument '$1'" >&2; usage; exit 1 ;;
    esac
    shift
done

[ -n "$package" ] || { echo "ERROR: --package is required." >&2; usage; exit 1; }
[ -n "$into" ]    || { echo "ERROR: --into is required." >&2; usage; exit 1; }
[ -f "$package" ] || { echo "ERROR: no such package: $package" >&2; exit 1; }
[ -d "$into" ]    || { echo "ERROR: no such directory: $into" >&2; exit 1; }

if [ ! -d "$into/CarlaUnreal" ]; then
    echo "ERROR: $into does not look like a CARLA package (no CarlaUnreal/ inside)." >&2
    exit 1
fi

plugins_dir="$into/CarlaUnreal/Plugins/GeneratedWorlds"
version_file="$into/VERSION"
interface_ini="$into/CarlaUnreal/Config/DefaultWorldInterface.ini"

unpacked="$(mktemp -d)"
trap 'rm -rf "$unpacked"' EXIT

unzip -q "$package" -d "$unpacked" || { echo "ERROR: could not unpack $package (is 'unzip' installed?)" >&2; exit 1; }

manifest="$unpacked/world.json"
[ -f "$manifest" ] || { echo "ERROR: $package carries no world.json; it was not written by PackageWorld.sh." >&2; exit 1; }

json_str() { sed -n "s/.*\"$1\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p" "$manifest" | head -1; }
json_num() { sed -n "s/.*\"$1\"[[:space:]]*:[[:space:]]*\([0-9]\+\).*/\1/p" "$manifest" | head -1; }

world="$(json_str world)"
map_package="$(json_str mapPackage)"
carla_hash="$(json_str carlaGitHash)"
want_major="$(json_num worldInterfaceMajor)"
want_minor="$(json_num worldInterfaceMinor)"

[ -n "$world" ] || { echo "ERROR: $package does not name a world." >&2; exit 1; }
[ -d "$unpacked/$world" ] || { echo "ERROR: $package says it holds '$world' but does not contain it." >&2; exit 1; }

echo "world   : $world"
echo "needs   : world interface ${want_major}.x, minor ${want_minor} or later"

# What this package promises, read from the package itself rather than from anything derived. A
# version says what a build supports; a hash could only say whether two builds are identical, which
# refuses compatible pairs and still cannot confirm an incompatible one.
problems=()
if [ -f "$interface_ini" ]; then
    have_major="$(sed -n 's/^[[:space:]]*Major[[:space:]]*=[[:space:]]*\([0-9]\+\).*/\1/p' "$interface_ini" | head -1)"
    have_minor="$(sed -n 's/^[[:space:]]*Minor[[:space:]]*=[[:space:]]*\([0-9]\+\).*/\1/p' "$interface_ini" | head -1)"
fi
if [ -z "${have_major:-}" ] || [ -z "${have_minor:-}" ]; then
    problems+=("this package does not declare a world interface version, so what it supports is unknown")
else
    echo "package : world interface ${have_major}.${have_minor}"
    # Major is the break; minor is additive, so the base may run ahead but not behind.
    if [ "$have_major" != "$want_major" ]; then
        problems+=("this package is world interface ${have_major}.x, the world needs ${want_major}.x")
    elif [ "$have_minor" -lt "$want_minor" ]; then
        problems+=("this package is minor ${have_minor}, the world needs ${want_minor} or later")
    fi
fi

if [ ${#problems[@]} -gt 0 ]; then
    echo "" >&2
    echo "This world was not built for this package:" >&2
    for p in "${problems[@]}"; do echo "  - $p" >&2; done
    # Identification, so both sides can be named when someone has to work out which is wrong.
    [ -n "$carla_hash" ] && echo "  world  built from Carla commit ${carla_hash:0:9}" >&2
    if [ -f "$version_file" ]; then
        line="$(grep -m1 'Carla git hash' "$version_file" 2>/dev/null)"
        [ -n "$line" ] && echo "  package $line" >&2
    fi
    echo "" >&2
    echo "Installing it anyway would most likely fail to load rather than misbehave subtly." >&2
    if [ "$force" -eq 0 ]; then
        echo "Re-package the world against this build, or pass --force if you know they are compatible." >&2
        exit 1
    fi
    echo "" >&2
    echo "--force given; installing regardless." >&2
fi

mkdir -p "$plugins_dir"
target="$plugins_dir/$world"
if [ -d "$target" ]; then
    echo "Replacing the copy of '$world' already installed."
    rm -rf "$target"
fi
cp -a "$unpacked/$world" "$target"

echo ""
echo "Installed $world"
echo "  into  : $target"
echo ""
echo "Load it with:"
echo "  ./Scripts/Linux/RunCarlaServer.sh --map $map_package"
