#!/usr/bin/env bash
#
# MakeDistribution.sh - assemble a self-contained CARLA Linux distribution tarball.
#
# Produces  Build/Dist/Carla-<version>-Linux-<config>.tar.gz  containing everything needed to run the
# digital-twin single-client traffic-manager demo on another Linux machine:
#   CarlaServer/   the cooked CARLA server (the packaged game binary; run with CarlaUnreal.sh)
#   wheels/        the carlanet + carlacontrol Python wheels (install into a venv)
#   scripts/       run_SCTMV.py (the demo client; imports carlanet + carlacontrol)
#   osm/           the example OpenStreetMap maps SCTMV can build worlds from
#   tools/sumo/    SUMO netconvert + its shared libraries + PROJ data (OSM -> OpenDRIVE conversion)
#   setup-venv.sh / run-server.sh / run-sctmv.sh / README.md
#
# Run this AFTER the build + cook have produced the artifacts:
#   ./Scripts/Linux/BuildCarla.sh                                   # editor + carlanet & carlacontrol wheels
#   cmake --build Build --target package-development                # cook + stage the server
# then:
#   ./Scripts/Linux/MakeDistribution.sh                            # assemble the tarball
# or pass --build to run those steps first:
#   ./Scripts/Linux/MakeDistribution.sh --build --config Development
#
# Linux only (uses ldd to gather netconvert's libraries). Run inside the build container
# (Util/Docker/run.alma8.sh --non-root) or on a native Linux build host.

set -euo pipefail

config="Development"
do_build=0
while [ $# -gt 0 ]; do
    case "$1" in
        --config)   config="$2"; shift ;;
        --config=*) config="${1#*=}" ;;
        --build)    do_build=1 ;;
        -h|--help)
            sed -n '2,30p' "$0" | sed 's/^# \{0,1\}//'
            exit 0 ;;
        *) echo "Unknown argument: $1" >&2; exit 2 ;;
    esac
    shift
done

root="$(cd "$(dirname "$(realpath "${BASH_SOURCE[0]}")")/../.." && pwd)"
cd "$root"

cmake_target="package-development"
case "$config" in
    Development) cmake_target="package-development" ;;
    Shipping)    cmake_target="package-shipping" ;;
    Debug)       cmake_target="package-debug" ;;
    *)           cmake_target="package-$(echo "$config" | tr '[:upper:]' '[:lower:]')" ;;
esac

if [ "$do_build" -eq 1 ]; then
    echo "[dist] building editor + carlanet & carlacontrol wheels (BuildCarla.sh)"
    ./Scripts/Linux/BuildCarla.sh
    # Skip the cmake package target's own Compress.cmake step (CARLA_UNREAL_PACKAGE_NO_COMPRESSION):
    # it single-threaded-gzips the whole ~30-40 GB package into Build/Package/<name>.tar.gz, which is
    # both slow (it runs after "BUILD SUCCESSFUL" and looks like a hang) and redundant -- this script
    # assembles the real, richer bundle (game + wheel + scripts + osm + netconvert) into
    # Build/Dist/<name>.tar.gz below, so we only want to gzip once. The reconfigure is quick.
    echo "[dist] configuring package target to skip its redundant compress"
    cmake -DCARLA_UNREAL_PACKAGE_NO_COMPRESSION=ON -S "$root" -B "$root/Build"
    echo "[dist] cooking + staging the server (cmake --build Build --target $cmake_target)"
    cmake --build Build --target "$cmake_target"
fi

# Locate the cooked package (prefer the archived copy; fall back to the staging dir if the archive
# step was interrupted -- the staged tree is equally complete and runnable).
pkg_parent=""
for cand in \
    "$root/Build/Package/Carla-"*"-Linux-${config}/Linux" \
    "$root/Build/Package/StagedBuilds/Carla-"*"-Linux-${config}/Linux"; do
    if [ -d "$cand" ] && [ -e "$cand/CarlaUnreal.sh" ]; then pkg_parent="$cand"; break; fi
done
if [ -z "$pkg_parent" ]; then
    echo "ERROR: no cooked ${config} package found under Build/Package." >&2
    echo "       Run: cmake --build Build --target $cmake_target   (or pass --build)" >&2
    exit 1
fi
pkgname="$(basename "$(dirname "$pkg_parent")")"   # e.g. Carla-0.10.0-Linux-Development
echo "[dist] using cooked package: $pkg_parent"

dist="$root/Build/Dist/$pkgname"
echo "[dist] staging into $dist"
rm -rf "$dist"
mkdir -p "$dist"/{CarlaServer,wheels,scripts,osm,tools/sumo/lib}

# 1. Cooked server.
cp -a "$pkg_parent/." "$dist/CarlaServer/"

# 1b. What this build is. The cook writes VERSION at the archive root, one level above the platform
# directory copied above, so without this the distribution -- the thing actually handed to someone --
# carries no statement of which CARLA it is or which worlds it accepts. InstallWorld.sh reads it to
# name both sides when a world and a package disagree.
version_src="$(dirname "$pkg_parent")/VERSION"
if [ -f "$version_src" ]; then
    cp -a "$version_src" "$dist/VERSION"
    echo "[dist] VERSION: $(head -2 "$version_src" | tr '
' '; ')"
else
    echo "[dist] WARNING: no VERSION at $version_src; the distribution will not state its build." >&2
fi

# 2. Python client wheels (newest of each): carlanet (the .NET bridge) and carlacontrol (the
#    run_SCTMV client package). carlacontrol depends on carlanet, so both must be bundled.
copy_newest_wheel() {   # <dist-dir>
    local w
    w="$(ls -t "$1"/*.whl 2>/dev/null | head -1 || true)"
    if [ -n "$w" ]; then cp "$w" "$dist/wheels/"; echo "[dist] wheel: $(basename "$w")"
    else echo "[dist] WARNING: no wheel under $1 (run build_wheel.sh / BuildCarla.sh)"; fi
}
copy_newest_wheel "$root/CarlaNet/python/dist"
copy_newest_wheel "$root/CarlaControl/dist"

# 3. Demo client. run_SCTMV.py imports carlanet + carlacontrol (both installed from wheels/ above);
#    it has no sibling-file imports, and reads netconvert/PROJ paths from the env set by run-sctmv.sh.
cp "$root/CarlaControl/scripts/run_SCTMV.py" "$dist/scripts/"

# 4. Example OSM maps.
cp "$root"/Import/*.osm "$dist/osm/" 2>/dev/null || echo "[dist] WARNING: no .osm files under Import/"

# 5. SUMO netconvert + its non-core shared libraries + PROJ data.
nc="$root/Build/sumo-install/bin/netconvert"
if [ -x "$nc" ]; then
    cp "$nc" "$dist/tools/sumo/netconvert.bin"
    # Bundle every resolved library except the ones tied to the target's own kernel/glibc/loader
    # (copying those across hosts is unsafe); the target supplies those, we supply xerces/proj/etc.
    ldd "$nc" | awk '/=> \//{print $3}' | while read -r l; do
        case "$l" in
            */libc.so.*|*/libm.so.*|*/libpthread.so.*|*/libdl.so.*|*/librt.so.*|*/ld-linux*|*/libresolv.so.*) ;;
            *) cp -Lu "$l" "$dist/tools/sumo/lib/" 2>/dev/null || true ;;
        esac
    done
    # PROJ coordinate database (netconvert geo-references via PROJ). Check known locations only --
    # never scan the whole filesystem, which can crawl for many minutes on a host with large or
    # networked mounts (e.g. a RAID array).
    projdir=""
    for cand in \
        "$root/Build/sumo-install/share/proj" \
        /usr/share/proj /usr/local/share/proj /usr/share/proj-data /usr/share/proj9 ; do
        if [ -f "$cand/proj.db" ]; then projdir="$cand"; break; fi
    done
    if [ -f "$projdir/proj.db" ]; then
        mkdir -p "$dist/tools/sumo/proj"; cp -a "$projdir/." "$dist/tools/sumo/proj/"
        echo "[dist] bundled PROJ data from $projdir"
    else
        echo "[dist] WARNING: proj.db not found; OSM geo-referencing may need 'dnf install proj' on the target"
    fi
    # Self-contained netconvert launcher (points PROJ + the bundled libs at the bundle).
    cat > "$dist/tools/sumo/netconvert" <<'NETC'
#!/usr/bin/env bash
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
export LD_LIBRARY_PATH="$here/lib:${LD_LIBRARY_PATH:-}"
[ -f "$here/proj/proj.db" ] && export PROJ_LIB="$here/proj" PROJ_DATA="$here/proj"
exec "$here/netconvert.bin" "$@"
NETC
    chmod +x "$dist/tools/sumo/netconvert"
    echo "[dist] bundled netconvert + $(ls "$dist/tools/sumo/lib" | wc -l) libraries"
else
    echo "[dist] WARNING: netconvert not found at $nc (run CarlaSetup.sh); OSM->OpenDRIVE unavailable"
fi

# 6. Helper scripts + README for the target machine.
cat > "$dist/setup-venv.sh" <<'VENV'
#!/usr/bin/env bash
# Create a Python venv and install the carlanet + carlacontrol wheels + the demo's Python deps.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
python3 -m venv "$here/venv"
. "$here/venv/bin/activate"
pip install --upgrade pip
# Both wheels are passed together (and --find-links points at wheels/) so carlacontrol's dependency
# on the local-only carlanet wheel resolves from the bundle rather than a package index.
pip install --find-links "$here/wheels" "$here"/wheels/*.whl numpy pygame
echo "venv ready: source $here/venv/bin/activate"
VENV
chmod +x "$dist/setup-venv.sh"

cat > "$dist/run-server.sh" <<'SRV'
#!/usr/bin/env bash
# Launch the CARLA server (headless rendering still needs a GPU + Vulkan on this machine).
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec "$here/CarlaServer/CarlaUnreal.sh" -RenderOffScreen -nosound "$@"
SRV
chmod +x "$dist/run-server.sh"

cat > "$dist/run-sctmv.sh" <<'RUN'
#!/usr/bin/env bash
# Run the single-client traffic-manager / EO demo against a running server.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$here/venv/bin/activate"
export CARLA_NETCONVERT="$here/tools/sumo/netconvert"
[ -f "$here/tools/sumo/proj/proj.db" ] && export PROJ_LIB="$here/tools/sumo/proj" PROJ_DATA="$here/tools/sumo/proj"
exec python "$here/scripts/run_SCTMV.py" "$@"
RUN
chmod +x "$dist/run-sctmv.sh"

cat > "$dist/README.md" <<README
# CARLA ${pkgname#Carla-} distribution

Self-contained CARLA digital-twin bundle: the cooked server, the carlanet + carlacontrol Python
client packages, the run_SCTMV demo, example OSM maps, and SUMO netconvert.

## Target prerequisites
- 64-bit Linux compatible with the build host (RHEL 8 / glibc 2.28 or newer).
- A GPU with **Vulkan** drivers (the server renders even when headless).
- **Python 3.11** (for the venv).
- The **.NET 10 runtime** (carlanet runs .NET assemblies). Install e.g. \`dnf install dotnet-runtime-10.0\`.
- netconvert's xerces/PROJ libraries are bundled; only core system libraries are expected on the host.

## Run it
\`\`\`sh
./setup-venv.sh                      # one-time: venv + carlanet & carlacontrol wheels + numpy + pygame
./run-server.sh &                    # start the CARLA server (needs GPU/Vulkan)
./run-sctmv.sh --osm osm/Lakeview_Carson.osm   # build a world from an OSM map and run the demo
\`\`\`
\`run-sctmv.sh\` points carlanet at the bundled \`tools/sumo/netconvert\`; pass \`--help\` to run-sctmv for options.
README

# 7. Tarball. Compressing ~30 GB with single-threaded gzip is slow; use pigz (parallel gzip) when
# it is available so this scales across cores.
echo "[dist] creating tarball (this compresses the whole package; it can take several minutes)"
if command -v pigz >/dev/null 2>&1; then
    tar -C "$root/Build/Dist" -cf - "$pkgname" | pigz > "$root/Build/Dist/${pkgname}.tar.gz"
else
    tar -C "$root/Build/Dist" -czf "$root/Build/Dist/${pkgname}.tar.gz" "$pkgname"
fi
echo "[dist] DONE: Build/Dist/${pkgname}.tar.gz ($(du -h "$root/Build/Dist/${pkgname}.tar.gz" | cut -f1))"
