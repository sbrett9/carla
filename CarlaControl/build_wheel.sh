#!/usr/bin/env bash
#
# build_wheel.sh — Build the CarlaControl wheel
#
# Build (and optionally install) the carlacontrol wheel. Adapted from the
# CarlaNet build_wheel.sh pattern.
#
# Paths are derived from this script's location, so it needs no arguments.

set -euo pipefail

do_install=0
do_editable=0
do_clean=0

usage() {
    cat <<'EOF'
Usage: build_wheel.sh [options]

Build the carlacontrol wheel.

Options:
  --install     pip-install the freshly built wheel (--force-reinstall).
  --editable    Perform an editable install (pip install -e .) and stop.
  --clean       Wipe previous build artifacts (build/, dist/, *.egg-info) first.
  -h, --help    Show this help and exit.

Environment:
  PYTHON        Python interpreter to use (default: python3).
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --install)  do_install=1 ;;
        --editable) do_editable=1 ;;
        --clean)    do_clean=1 ;;
        -h|--help)  usage; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
    esac
    shift
done

# ── Paths (derived from script location) ────────────────────────────────────
script_dir="$(cd "$(dirname "$(realpath "${BASH_SOURCE[0]}")")" && pwd)"
build_dir="$script_dir/build"
dist_dir="$script_dir/dist"
python_bin="${PYTHON:-python3}"

echo "[build_wheel] script dir : $script_dir"
echo "[build_wheel] python     : $python_bin"

if [ "$do_clean" -eq 1 ]; then
    echo "[build_wheel] cleaning previous build artifacts"
    rm -rf "$build_dir" "$dist_dir"
    find "$script_dir" -maxdepth 1 -name '*.egg-info' -type d -exec rm -rf {} +
fi

if [ "$do_editable" -eq 1 ]; then
    echo "[build_wheel] performing editable install (pip install -e .)"
    "$python_bin" -m pip install -e "$script_dir"
    echo "[build_wheel] editable install complete"
    exit 0
fi

echo "[build_wheel] ensuring 'build' package is available"
"$python_bin" -m pip install --upgrade build

# Always wipe build/ before building the wheel to avoid stale artifacts
if [ -d "$build_dir" ]; then
    echo "[build_wheel] wiping stale build/ before wheel build"
    rm -rf "$build_dir"
fi

echo "[build_wheel] building wheel"
"$python_bin" -m build --wheel "$script_dir"

wheel_path="$(ls -t "$dist_dir"/*.whl 2>/dev/null | head -n1 || true)"
if [ -z "$wheel_path" ]; then
    echo "[build_wheel] no wheel produced in $dist_dir" >&2
    exit 1
fi
echo "[build_wheel] wheel built: $wheel_path"

if [ "$do_install" -eq 1 ]; then
    echo "[build_wheel] installing wheel with --force-reinstall"
    # carlacontrol depends on carlanet, a locally-built wheel that is published to no package
    # index. A plain 'pip install --force-reinstall <wheel>' re-resolves the WHOLE dependency
    # tree and dies trying to fetch carlanet from an index ("No matching distribution found for
    # carlanet"). So install in two steps: (1) force-reinstall carlacontrol itself with --no-deps
    # to refresh its code without disturbing deps, then (2) a normal (non-force) install that pulls
    # only *missing* deps, with CarlaNet's dist dir on --find-links so a not-yet-installed carlanet
    # resolves from the local wheel instead of an index.
    "$python_bin" -m pip install --force-reinstall --no-deps "$wheel_path"
    carlanet_dist="$script_dir/../CarlaNet/python/dist"
    dep_args=()
    [ -d "$carlanet_dist" ] && dep_args+=(--find-links "$carlanet_dist")
    "$python_bin" -m pip install "$wheel_path" "${dep_args[@]}"
    echo "[build_wheel] install complete"
fi
