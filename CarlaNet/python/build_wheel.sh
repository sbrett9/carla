#!/usr/bin/env bash
#
# build_wheel.sh — Linux equivalent of build_wheel.ps1
#
# Publish the CarlaNet .NET assemblies into the python shim, then build (and
# optionally install) the carlanet wheel. Lives next to build_wheel.ps1 so the
# two platforms' wheel logic stays side by side; BuildCarla.sh calls this.
#
# Paths are derived from this script's location, so it needs no arguments to
# find the CarlaNet tree.

set -euo pipefail

do_install=0
do_editable=0
do_clean=0

usage() {
    cat <<'EOF'
Usage: build_wheel.sh [options]

Publish CarlaNet (.NET) into the python shim and build the carlanet wheel.

Options:
  --install     pip-install the freshly built wheel (--force-reinstall).
  --editable    Perform an editable install (pip install -e .) and stop.
  --clean       Wipe previous build artifacts (carlanet/dlls, build/, dist/, *.egg-info) first.
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
carlanet_root="$(cd "$script_dir/.." && pwd)"
pkg_dir="$script_dir/carlanet"
dlls_dir="$pkg_dir/dlls"
build_dir="$script_dir/build"
dist_dir="$script_dir/dist"
csproj="$carlanet_root/src/CarlaNet.Python/CarlaNet.Python.csproj"
python_bin="${PYTHON:-python3}"

echo "[build_wheel] script dir : $script_dir"
echo "[build_wheel] carlanet   : $carlanet_root"
echo "[build_wheel] csproj     : $csproj"

if [ ! -f "$csproj" ]; then
    echo "[build_wheel] CarlaNet csproj not found: $csproj" >&2
    exit 1
fi

if [ "$do_clean" -eq 1 ]; then
    echo "[build_wheel] cleaning previous build artifacts"
    rm -rf "$dlls_dir" "$build_dir" "$dist_dir"
    find "$script_dir" -maxdepth 1 -name '*.egg-info' -type d -exec rm -rf {} +
    mkdir -p "$dlls_dir"
fi

mkdir -p "$dlls_dir"

echo "[build_wheel] running dotnet publish -> $dlls_dir"
dotnet publish "$csproj" -c Release -o "$dlls_dir"

# Shim is python/carlanet/__init__.py (canonical); no stray carlanet.py is published.

if [ "$do_editable" -eq 1 ]; then
    echo "[build_wheel] performing editable install (pip install -e .)"
    "$python_bin" -m pip install -e "$script_dir"
    echo "[build_wheel] editable install complete"
    exit 0
fi

echo "[build_wheel] ensuring 'build' package is available"
"$python_bin" -m pip install --upgrade build

# Always wipe build/ before building the wheel. setuptools' package discovery would otherwise
# pick up any stale carlanet copy left under build/lib and re-nest it (build/lib/build/lib/.../
# carlanet), compounding every run and polluting the wheel. (pyproject also filters discovery to
# carlanet*.)
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
    "$python_bin" -m pip install --force-reinstall "$wheel_path"
    echo "[build_wheel] install complete"
fi
