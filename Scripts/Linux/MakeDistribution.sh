#!/usr/bin/env bash
#
# MakeDistribution.sh - assemble a self-contained CARLA Linux distribution tarball.
#
# Produces  Build/Dist/Carla-<version>-Linux-<config>.tar.gz  containing everything needed to run the
# digital-twin single-client traffic-manager demo on another Linux machine:
#   CarlaServer/   the cooked CARLA server (the packaged game binary; run with CarlaUnreal.sh)
#   wheels/        the carlanet + carlacontrol Python wheels (install into a venv)
#   scripts/       run_SCTMV.py (the demo client; imports carlanet + carlacontrol)
#   osm/           the example OpenStreetMap maps the demo can build worlds from
#   tools/sumo/    the SUMO toolchain: netconvert, sumo, duarouter, libtracics, the shared libraries
#                  they load, SUMO's typemap/xsd data, its traci/sumolib modules, and PROJ data
#   licenses/      the licence text of every third-party component in the bundle
#   MANIFEST.md    what is in here, where it came from and under what terms (generated)
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
# Linux only (uses ldd to gather the SUMO binaries' libraries). Run inside the build container
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
            sed -n '2,26p' "$0" | sed 's/^# \{0,1\}//'
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
mkdir -p "$dist"/{CarlaServer,wheels,scripts,osm,tools/sumo/lib,licenses}

# ── Component and licence inventory ──────────────────────────────────────────────────────────
# The distribution used to ship no LICENSE, no NOTICE and no third-party listing of any kind while
# redistributing a few dozen native libraries under nine or more licences. It now carries a
# MANIFEST.md and a licenses/ directory, both GENERATED FROM WHAT THIS SCRIPT ACTUALLY COPIES: each
# staging step below records its own rows, so the inventory cannot describe a bundle other than the
# one on disk. A hand-maintained list is wrong the first time a slot changes.
manifest_rows=""
add_manifest_row() {   # <component> <provenance> <licence> <location>
    manifest_rows="${manifest_rows}| $1 | $2 | $3 | \`$4\` |
"
}

# Copies a licence text into licenses/ and echoes the name it was filed under, or nothing when the
# source is absent -- in which case the manifest says the text is missing rather than staying quiet.
copy_license_text() {   # <source> <name>
    if [ -f "$1" ]; then cp "$1" "$dist/licenses/$2"; echo "$2"
    else echo "[dist] WARNING: licence text not found at $1; MANIFEST.md will record it as missing." >&2; fi
}

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
# A missing wheel is fatal rather than a warning: the distribution cannot install itself without it,
# and a warning buried in a long cook log is how a broken bundle shipped before.
copy_newest_wheel() {   # <dist-dir>
    local w
    w="$(ls -t "$1"/*.whl 2>/dev/null | head -1 || true)"
    if [ -z "$w" ]; then
        echo "[dist] ERROR: no wheel under $1 (run build_wheel.sh / BuildCarla.sh). A distribution missing a wheel cannot install itself." >&2
        exit 1
    fi
    cp "$w" "$dist/wheels/"
    echo "[dist] wheel: $(basename "$w")"
    bundled_wheel="$(basename "$w")"
}
copy_newest_wheel "$root/CarlaNet/python/dist"
add_manifest_row "carlanet (CARLA .NET client)" "built from this repository, $bundled_wheel" \
                 "MIT (licenses/CARLA-LICENSE.txt)" "wheels/"
copy_newest_wheel "$root/CarlaControl/dist"
add_manifest_row "carlacontrol (world building, scenarios, telemetry)" \
                 "built from this repository, $bundled_wheel" \
                 "Sierra Nevada Corporation (licenses/CarlaControl-LICENSE.txt)" "wheels/"

# 3. Demo client. run_SCTMV.py imports carlanet + carlacontrol (both installed from wheels/ above);
#    it has no sibling-file imports -- it clips OSM through carlacontrol.OsmClipper from the wheel,
#    which is why no clipper script is copied beside it -- and reads its netconvert, PROJ and SUMO
#    paths from the environment run-sctmv.sh sets.
cp "$root/CarlaControl/scripts/run_SCTMV.py" "$dist/scripts/"

# 4. Example OSM maps. These are OpenStreetMap extracts, so they and every .xodr derived from them
#    carry the Open Database License; MANIFEST.md names the files that actually shipped.
if cp "$root"/Import/*.osm "$dist/osm/" 2>/dev/null; then
    add_manifest_row "OpenStreetMap extracts" \
                     "openstreetmap.org contributors: $(ls "$dist/osm" | tr '\n' ' ')" \
                     "ODbL 1.0 (licenses/OpenStreetMap-ODbL-NOTICE.txt)" "osm/"
else
    echo "[dist] WARNING: no .osm files under Import/"
fi

# 5. The SUMO toolchain: the binaries, the shared libraries they actually load, the SWIG-generated
#    C# the CarlaNet TraCI binding is built from, the named data/ and tools/ subsets, and PROJ data.
sumo_install="$root/Build/sumo-install"
sumo_bin="$sumo_install/bin"
sumo_dest="$dist/tools/sumo"
sumo_executables="netconvert sumo duarouter"
sumo_version="unknown"

# Bundle every library the given binary resolves, except the ones tied to the target's own
# kernel/glibc/loader (copying those across hosts is unsafe); the target supplies those, we supply
# xerces/proj/etc. The set is what the binaries import, not a directory glob: shipping libraries
# nothing loads means a licence obligation for each one that is never used.
bundle_libraries_of() {   # <binary>
    ldd "$1" | awk '/=> \//{print $3}' | while read -r l; do
        case "$l" in
            */libc.so.*|*/libm.so.*|*/libpthread.so.*|*/libdl.so.*|*/librt.so.*|*/ld-linux*|*/libresolv.so.*) ;;
            *) cp -Lu "$l" "$sumo_dest/lib/" 2>/dev/null || true ;;
        esac
    done
}

if [ -x "$sumo_bin/netconvert" ]; then
    for b in $sumo_executables; do
        if [ -x "$sumo_bin/$b" ]; then
            cp "$sumo_bin/$b" "$sumo_dest/$b.bin"
            bundle_libraries_of "$sumo_bin/$b"
            # Self-contained launcher (points PROJ + the bundled libraries at the bundle).
            cat > "$sumo_dest/$b" <<NETC
#!/usr/bin/env bash
here="\$(cd "\$(dirname "\${BASH_SOURCE[0]}")" && pwd)"
export LD_LIBRARY_PATH="\$here/lib:\${LD_LIBRARY_PATH:-}"
[ -f "\$here/proj/proj.db" ] && export PROJ_LIB="\$here/proj" PROJ_DATA="\$here/proj"
exec "\$here/$b.bin" "\$@"
NETC
            chmod +x "$sumo_dest/$b"
        else
            echo "[dist] WARNING: $b is missing from $sumo_bin (run CarlaSetup.sh to build the whole toolchain)"
        fi
    done
    # The native TraCI library the C# binding loads, and the generated C# sources it is built from --
    # a recipient has no Build/ tree to regenerate them in.
    [ -f "$sumo_bin/libtracics.so" ] && { cp "$sumo_bin/libtracics.so" "$sumo_dest/lib/"; bundle_libraries_of "$sumo_bin/libtracics.so"; }
    [ -f "$sumo_bin/libtracics-sources.zip" ] && cp "$sumo_bin/libtracics-sources.zip" "$sumo_dest/"

    # The named data/ and tools/ subsets, so tools/sumo is a usable SUMO_HOME on the target.
    for item in data/typemap data/xsd tools/traci tools/sumolib; do
        if [ -d "$sumo_install/$item" ]; then
            mkdir -p "$sumo_dest/$(dirname "$item")"
            rm -rf "${sumo_dest:?}/$item"
            cp -a "$sumo_install/$item" "$sumo_dest/$item"
        else
            echo "[dist] WARNING: $item is missing from $sumo_install (run CarlaSetup.sh)"
        fi
    done

    # PROJ coordinate database (netconvert geo-references via PROJ). Check known locations only --
    # never scan the whole filesystem, which can crawl for many minutes on a host with large or
    # networked mounts (e.g. a RAID array).
    projdir=""
    for cand in \
        "$sumo_install/share/proj" \
        /usr/share/proj /usr/local/share/proj /usr/share/proj-data /usr/share/proj9 ; do
        if [ -f "$cand/proj.db" ]; then projdir="$cand"; break; fi
    done
    if [ -f "$projdir/proj.db" ]; then
        mkdir -p "$sumo_dest/proj"; cp -a "$projdir/." "$sumo_dest/proj/"
        echo "[dist] bundled PROJ data from $projdir"
    else
        echo "[dist] WARNING: proj.db not found; OSM geo-referencing may need 'dnf install proj' on the target"
    fi

    # Run each staged binary through its own launcher. The one way the bundled library set can be
    # wrong is by being short, and this is what would say so.
    for b in $sumo_executables; do
        [ -x "$sumo_dest/$b" ] || continue
        if ! reported="$("$sumo_dest/$b" --version 2>&1 | head -1)"; then
            echo "[dist] ERROR: $b does not run from the staged bundle: $reported" >&2
            echo "       The bundled shared-library set is incomplete." >&2
            exit 1
        fi
        echo "[dist] staged $b : $reported"
        if [ "$b" = "netconvert" ]; then
            sumo_version="$(echo "$reported" | sed -n 's/.*Eclipse SUMO [^ ]* v\{0,1\}\([0-9][0-9.]*\).*/\1/p')"
            [ -n "$sumo_version" ] || sumo_version="unknown"
        fi
    done
    echo "[dist] bundled the SUMO toolchain + $(ls "$sumo_dest/lib" | wc -l) shared libraries"
else
    echo "[dist] WARNING: netconvert not found at $sumo_bin/netconvert (run CarlaSetup.sh); OSM->OpenDRIVE unavailable"
fi

# 6. Licence texts, and the inventory rows for everything staged above.
carla_license="$(copy_license_text "$root/LICENSE" "CARLA-LICENSE.txt")"
copy_license_text "$root/CarlaControl/LICENSE" "CarlaControl-LICENSE.txt" >/dev/null
add_manifest_row "CARLA server (cooked)" "built from this repository; see VERSION" \
                 "MIT (licenses/${carla_license:-missing})" "CarlaServer/"

# OpenStreetMap's terms are not a file in this tree, so the notice is written rather than copied; it
# states the obligation and where the licence text lives, for the extracts and for every .xodr
# derived from them, which are a Derivative Database under the same terms.
cat > "$dist/licenses/OpenStreetMap-ODbL-NOTICE.txt" <<'ODBL'
OpenStreetMap data and works derived from it
============================================

The .osm extracts under osm/, and every OpenDRIVE (.xodr) road network this distribution generates
from one, are derived from OpenStreetMap.

  (c) OpenStreetMap contributors, available under the Open Database License (ODbL) v1.0.
  Licence text: https://opendatacommons.org/licenses/odbl/1-0/
  Attribution:  https://www.openstreetmap.org/copyright

A generated road network is a Derivative Database under that licence. Anything published from it
must carry the attribution above.
ODBL

if [ -x "$sumo_dest/netconvert" ]; then
    # SUMO itself: Eclipse Public License 2.0, which carries a source offer. The offer cites the
    # commit CarlaSetup.sh pins rather than repeating it, so the two cannot drift apart.
    sumo_pin="$(sed -n 's/.*checkout \([0-9a-f]\{40\}\).*/\1/p' "$root/CarlaSetup.sh" | head -1)"
    [ -n "$sumo_pin" ] || sumo_pin="unrecorded"
    copy_license_text "$root/Build/sumo-src/LICENSE" "SUMO-LICENSE.txt" >/dev/null
    copy_license_text "$root/Build/sumo-src/NOTICE.md" "SUMO-NOTICE.md" >/dev/null
    add_manifest_row "Eclipse SUMO $sumo_version (netconvert, sumo, duarouter, libtracics)" \
                     "github.com/eclipse-sumo/sumo at $sumo_pin; source available from that commit" \
                     "EPL-2.0 (licenses/SUMO-LICENSE.txt, licenses/SUMO-NOTICE.md)" "tools/sumo/"
    if [ -f "$sumo_dest/libtracics-sources.zip" ]; then
        add_manifest_row "Eclipse SUMO C# TraCI bindings (SWIG-generated source)" \
                         "generated by SUMO's own build at $sumo_pin" \
                         "EPL-2.0 (licenses/SUMO-LICENSE.txt)" "tools/sumo/libtracics-sources.zip"
    fi
    add_manifest_row "Eclipse SUMO data and Python tools (typemap, xsd, traci, sumolib)" \
                     "github.com/eclipse-sumo/sumo at $sumo_pin" \
                     "EPL-2.0 (licenses/SUMO-LICENSE.txt)" "tools/sumo/data/, tools/sumo/tools/"

    # Each bundled shared library, attributed through the package manager that owns the file it was
    # copied from. Which rows appear is decided by what the ldd walk above actually copied; a library
    # no package claims is listed as unattributed rather than quietly omitted.
    for lib in "$sumo_dest/lib"/*; do
        [ -f "$lib" ] || continue
        name="$(basename "$lib")"
        [ "$name" = "libtracics.so" ] && continue      # already covered by the SUMO row above
        origin="$(ldconfig -p 2>/dev/null | awk -v n="$name" '$1 == n {print $NF; exit}')"
        [ -n "$origin" ] || origin="$lib"
        package=""; license=""; text=""
        if command -v rpm >/dev/null 2>&1; then
            package="$(rpm -qf --queryformat '%{NAME} %{VERSION}' "$origin" 2>/dev/null || true)"
            if [ -n "$package" ]; then
                license="$(rpm -qf --queryformat '%{LICENSE}' "$origin" 2>/dev/null || true)"
                rpm_name="${package%% *}"
                for candidate in /usr/share/licenses/"$rpm_name"/*; do
                    [ -f "$candidate" ] || continue
                    text="$(copy_license_text "$candidate" "$rpm_name-$(basename "$candidate")")"
                    break
                done
            fi
        fi
        if [ -z "$package" ] && command -v dpkg >/dev/null 2>&1; then
            package="$(dpkg -S "$origin" 2>/dev/null | cut -d: -f1 | head -1 || true)"
            if [ -n "$package" ]; then
                license="see the carried copyright file"
                text="$(copy_license_text "/usr/share/doc/$package/copyright" "$package-copyright.txt")"
            fi
        fi
        if [ -z "$package" ]; then
            echo "[dist] WARNING: $name has no owning package; MANIFEST.md will list it as unattributed."
            add_manifest_row "$name (UNATTRIBUTED - no package owns $origin)" "bundled from $origin" \
                             "unknown" "tools/sumo/lib/"
        else
            add_manifest_row "$package ($name)" "system package on the build host" \
                             "${license:-unstated} (${text:+licenses/$text}${text:+ }${text:-text not carried})" \
                             "tools/sumo/lib/"
        fi
    done

    if [ -f "$sumo_dest/proj/proj.db" ]; then
        add_manifest_row "PROJ coordinate database" "PROJ data files from $projdir" \
                         "PROJ licence" "tools/sumo/proj/"
    fi
fi

# 7. Helper scripts + README for the target machine.
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
export SUMO_HOME="$here/tools/sumo"
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
\`run-sctmv.sh\` points carlanet at the bundled \`tools/sumo/netconvert\` and sets \`SUMO_HOME\` to
\`tools/sumo\`; pass \`--help\` to run-sctmv for options.

## What is in here, and under what terms
\`MANIFEST.md\` lists every component this bundle carries, where it came from and its licence, with
the licence texts themselves under \`licenses/\`. Both are generated from what the packaging script
actually copied, so they describe this bundle rather than an intended one.
README

# 8. MANIFEST.md -- the inventory the steps above built up, rendered last so it covers everything
# that was actually staged.
if [ -f "$dist/VERSION" ]; then version_summary="$(tr '\n' ';' < "$dist/VERSION")"
else version_summary="no VERSION file was staged"; fi
{
    echo "# $pkgname - component and licence manifest"
    echo
    echo "Generated by \`Scripts/Linux/MakeDistribution.sh\` on $(date +%Y-%m-%d) from what it copied"
    echo "into this bundle. It is not hand-maintained, and it is an inventory for a licensing review"
    echo "rather than a legal determination."
    echo
    echo "Build: $version_summary"
    echo
    echo '| Component | Provenance | Licence | Location |'
    echo '|---|---|---|---|'
    printf '%s' "$manifest_rows"
    echo
    echo 'Licence texts are under `licenses/`. Eclipse SUMO is distributed under the EPL-2.0, which'
    echo 'carries a source offer: the exact commit every SUMO binary here was built from is named in'
    echo 'its row above, and its source is available from that commit at github.com/eclipse-sumo/sumo.'
} > "$dist/MANIFEST.md"
echo "[dist] MANIFEST.md: $(grep -c '^| ' "$dist/MANIFEST.md") table rows, $(ls "$dist/licenses" | wc -l) licence texts"

# 9. Tarball. Compressing ~30 GB with single-threaded gzip is slow; use pigz (parallel gzip) when
# it is available so this scales across cores.
echo "[dist] creating tarball (this compresses the whole package; it can take several minutes)"
if command -v pigz >/dev/null 2>&1; then
    tar -C "$root/Build/Dist" -cf - "$pkgname" | pigz > "$root/Build/Dist/${pkgname}.tar.gz"
else
    tar -C "$root/Build/Dist" -czf "$root/Build/Dist/${pkgname}.tar.gz" "$pkgname"
fi
echo "[dist] DONE: Build/Dist/${pkgname}.tar.gz ($(du -h "$root/Build/Dist/${pkgname}.tar.gz" | cut -f1))"
