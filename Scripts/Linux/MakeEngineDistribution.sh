#!/usr/bin/env bash
#
# MakeEngineDistribution.sh - package an Unreal Engine Installed Build into a redistributable archive.
#
# Turns the directory produced by
#     RunUAT.sh BuildGraph -Script=Engine/Build/InstalledEngineBuild.xml \
#         -Target="Make Installed Build Linux" -set:WithLinux=true -set:WithDDC=true
# (which lands under  <engine>/LocalBuilds/Engine/Linux ) into a single, versioned tarball plus a
# checksum and a provenance sidecar, ready to publish to Artifactory. A colleague extracts the archive,
# points CARLA_UNREAL_ENGINE_PATH at the extracted directory, and builds CARLA WITHOUT checking out or
# compiling the engine from source.
#
#   UnrealEngine-<engineversion>-<branch>-<commit>-Linux.tar.zst
#   UnrealEngine-<engineversion>-<branch>-<commit>-Linux.tar.zst.sha256
#   UnrealEngine-<engineversion>-<branch>-<commit>-Linux.tar.zst.metadata.txt
#
# The Installed Build must be BUILD-CAPABLE (able to compile the CARLA editor + cesium-native), not just
# run cooked games. This script's preflight checks whether the tree retained the engine's bundled clang
# toolchain (Engine/Extras/ThirdPartyNotUE/SDKs) -- if that was stripped, CARLA will not build against
# the archive and the preflight warns loudly. It does not build CARLA itself; --verify-extract only
# proves the tarball round-trips and the toolchain survived compression.
#
# Usage:
#   ./Scripts/Linux/MakeEngineDistribution.sh                       # uses $CARLA_UNREAL_ENGINE_PATH
#   ./Scripts/Linux/MakeEngineDistribution.sh --engine-dir /opt/ue/UnrealEngine --output-dir /mnt/artifacts
#   ./Scripts/Linux/MakeEngineDistribution.sh --compress gzip --verify-extract
#
# Options:
#   --engine-dir DIR      Source engine root (has .git and LocalBuilds/). Default: $CARLA_UNREAL_ENGINE_PATH
#   --installed-dir DIR   The Installed Build tree. Default: <engine-dir>/LocalBuilds/Engine/Linux
#   --output-dir DIR      Where to write the archive. Default: current directory
#   --name NAME           Override the archive base name (no extension)
#   --compress MODE       zstd | gzip | none. Default: zstd if available, else gzip
#   --level N             Compression level. Default: 12 (zstd) / 6 (gzip)
#   --jobs N              Compression threads. Default: nproc
#   --no-checksum         Skip the .sha256 sidecar
#   --verify-extract      After packaging, extract to a temp dir and re-check the tree (integrity)
#   --strict              Treat a missing bundled toolchain as a hard error instead of a warning
#
# Linux only. Run on the build box (or inside Util/Docker/run.alma8.sh) after Make Installed Build Linux.

set -euo pipefail

log()  { printf '[engine-dist] %s\n' "$*"; }
warn() { printf '[engine-dist] WARNING: %s\n' "$*" >&2; }
die()  { printf '[engine-dist] ERROR: %s\n' "$*" >&2; exit 1; }

engine_dir="${CARLA_UNREAL_ENGINE_PATH:-}"
installed_dir=""
output_dir="$(pwd)"
name_override=""
compress="auto"
level=""
jobs="$(nproc 2>/dev/null || echo 4)"
do_checksum=1
do_verify=0
strict=0

while [ $# -gt 0 ]; do
    case "$1" in
        --engine-dir)     engine_dir="$2"; shift ;;
        --engine-dir=*)   engine_dir="${1#*=}" ;;
        --installed-dir)  installed_dir="$2"; shift ;;
        --installed-dir=*) installed_dir="${1#*=}" ;;
        --output-dir)     output_dir="$2"; shift ;;
        --output-dir=*)   output_dir="${1#*=}" ;;
        --name)           name_override="$2"; shift ;;
        --name=*)         name_override="${1#*=}" ;;
        --compress)       compress="$2"; shift ;;
        --compress=*)     compress="${1#*=}" ;;
        --level)          level="$2"; shift ;;
        --level=*)        level="${1#*=}" ;;
        --jobs)           jobs="$2"; shift ;;
        --jobs=*)         jobs="${1#*=}" ;;
        --no-checksum)    do_checksum=0 ;;
        --verify-extract) do_verify=1 ;;
        --strict)         strict=1 ;;
        -h|--help)
            awk 'NR>1 && /^#/ {sub(/^# ?/,""); print; next} NR>1 {exit}' "$0"
            exit 0 ;;
        *) die "unknown argument: $1  (try --help)" ;;
    esac
    shift
done

# Resolve the Installed Build tree and the source engine root (for git provenance).
if [ -z "$installed_dir" ]; then
    [ -n "$engine_dir" ] || die "no engine dir: set CARLA_UNREAL_ENGINE_PATH or pass --engine-dir / --installed-dir"
    installed_dir="$engine_dir/LocalBuilds/Engine/Linux"
fi
[ -d "$installed_dir" ] || die "Installed Build not found: $installed_dir
       Run Make Installed Build Linux first (produces <engine>/LocalBuilds/Engine/Linux)."
installed_dir="$(realpath "$installed_dir")"

# If only --installed-dir was given, infer the source engine root as three levels up
# (LocalBuilds/Engine/Linux -> engine root) so we can still read the git commit.
if [ -z "$engine_dir" ]; then
    engine_dir="$(realpath "$installed_dir/../../..")"
fi

log "installed build : $installed_dir"
log "source engine   : $engine_dir"

# --- Preflight: is this a real, build-capable Installed Build? ---------------------------------------
[ -f "$installed_dir/Engine/Build/InstalledBuild.txt" ] \
    || warn "no Engine/Build/InstalledBuild.txt -- '$installed_dir' may be a source tree, not an Installed Build."

bvfile="$installed_dir/Engine/Build/Build.version"
[ -f "$bvfile" ] || die "no Engine/Build/Build.version under $installed_dir -- not a usable engine tree."

# The bundled clang/libc++ toolchain is what CARLA (cesium-native + the editor target) compiles against.
# A stock Installed Build historically strips these host SDKs; without them the archive cannot build CARLA.
toolchain_dir="$installed_dir/Engine/Extras/ThirdPartyNotUE/SDKs/HostLinux"
toolchain_present=0
if [ -d "$toolchain_dir" ] && [ -n "$(ls -A "$toolchain_dir" 2>/dev/null || true)" ]; then
    toolchain_present=1
    log "bundled toolchain: present ($toolchain_dir)"
else
    if [ "$strict" -eq 1 ]; then
        die "bundled toolchain MISSING ($toolchain_dir). The archive will NOT be able to build CARLA.
       Overlay Engine/Extras/ThirdPartyNotUE/SDKs from the source engine, or ship the from-source tree."
    fi
    warn "bundled toolchain MISSING ($toolchain_dir)."
    warn "The archive will likely NOT build CARLA (cesium-native/editor target need the engine clang)."
    warn "Overlay Engine/Extras/ThirdPartyNotUE/SDKs from the source engine before trusting this archive."
fi

# --- Version / provenance stamp ----------------------------------------------------------------------
ver_field() { grep -oE "\"$1\"[[:space:]]*:[[:space:]]*[0-9]+" "$bvfile" 2>/dev/null | grep -oE '[0-9]+' | head -1 || true; }
major="$(ver_field MajorVersion)"; minor="$(ver_field MinorVersion)"; patch="$(ver_field PatchVersion)"
engine_ver="${major:-0}.${minor:-0}.${patch:-0}"

git_hash="unknown"; branch="engine"; dirty=""
if git -C "$engine_dir" rev-parse --git-dir >/dev/null 2>&1; then
    git_hash="$(git -C "$engine_dir" rev-parse --short HEAD 2>/dev/null || echo unknown)"
    b="$(git -C "$engine_dir" rev-parse --abbrev-ref HEAD 2>/dev/null || echo HEAD)"
    # Sanitize the branch for a filename (slashes, spaces -> '-'); fall back if detached.
    [ "$b" = "HEAD" ] && b="detached"
    branch="$(printf '%s' "$b" | tr '/ ' '--' | tr -cd 'A-Za-z0-9._-')"
    # A build box dirties the source tree with regenerated binaries; note it in metadata, not the name.
    git -C "$engine_dir" diff --quiet --ignore-submodules HEAD 2>/dev/null || dirty="dirty"
fi

base="${name_override:-UnrealEngine-${engine_ver}-${branch}-${git_hash}-Linux}"
log "archive base    : $base"

# --- Choose compression ------------------------------------------------------------------------------
if [ "$compress" = "auto" ]; then
    if command -v zstd >/dev/null 2>&1; then compress="zstd"; else compress="gzip"; fi
fi
case "$compress" in
    zstd) command -v zstd >/dev/null 2>&1 || die "zstd not found (use --compress gzip)"; ext="tar.zst"; [ -n "$level" ] || level=12 ;;
    gzip) ext="tar.gz"; [ -n "$level" ] || level=6 ;;
    none) ext="tar" ;;
    *) die "unknown --compress: $compress (zstd|gzip|none)" ;;
esac

mkdir -p "$output_dir"
output_dir="$(realpath "$output_dir")"
archive="$output_dir/$base.$ext"

# --- Package -----------------------------------------------------------------------------------------
# Archive from the parent so members are 'Linux/...', renamed to '<base>/...' so extraction yields a
# single, self-describing top-level directory. The transform anchors on the leading path component only
# and never matches the tree's relative/absolute symlink targets, so internal links are preserved.
leaf="$(basename "$installed_dir")"
parent="$(dirname "$installed_dir")"
size_h="$(du -sh "$installed_dir" | cut -f1)"
log "packaging $size_h -> $archive  (compress=$compress level=${level:-none} jobs=$jobs)"

tar_cmd=(tar --numeric-owner --owner=0 --group=0
         --transform "s,^${leaf},${base},"
         -C "$parent" -cf - "$leaf")

case "$compress" in
    zstd) "${tar_cmd[@]}" | zstd -q -T"$jobs" -"$level" -o "$archive" ;;
    gzip) if command -v pigz >/dev/null 2>&1; then "${tar_cmd[@]}" | pigz -p "$jobs" -"$level" > "$archive"
          else "${tar_cmd[@]}" | gzip -"$level" > "$archive"; fi ;;
    none) tar --numeric-owner --owner=0 --group=0 --transform "s,^${leaf},${base}," -C "$parent" -cf "$archive" "$leaf" ;;
esac
log "wrote $(du -h "$archive" | cut -f1)  ($archive)"

# --- Checksum ----------------------------------------------------------------------------------------
sha=""
if [ "$do_checksum" -eq 1 ]; then
    ( cd "$output_dir" && sha256sum "$(basename "$archive")" > "$(basename "$archive").sha256" )
    sha="$(cut -d' ' -f1 < "$archive.sha256")"
    log "sha256          : $sha"
fi

# --- Provenance sidecar ------------------------------------------------------------------------------
{
    echo "archive           : $(basename "$archive")"
    echo "engine_version    : $engine_ver"
    echo "source_branch     : $branch"
    echo "source_commit     : $git_hash${dirty:+ ($dirty)}"
    echo "bundled_toolchain : $([ "$toolchain_present" -eq 1 ] && echo present || echo MISSING)"
    echo "build_capable     : $([ "$toolchain_present" -eq 1 ] && echo yes || echo 'unverified (toolchain missing)')"
    echo "packaged_utc      : $(date -u +%Y-%m-%dT%H:%M:%SZ)"
    echo "packaged_host     : $(hostname 2>/dev/null || echo unknown)"
    echo "uncompressed_size : $size_h"
    echo "compressed_size   : $(du -h "$archive" | cut -f1)"
    echo "sha256            : ${sha:-'(skipped)'}"
} > "$archive.metadata.txt"
log "wrote provenance: $(basename "$archive").metadata.txt"

# --- Optional round-trip integrity check -------------------------------------------------------------
if [ "$do_verify" -eq 1 ]; then
    tmp="$(mktemp -d)"
    trap 'rm -rf "$tmp"' EXIT
    log "verify-extract  : extracting to $tmp ..."
    case "$compress" in
        zstd) zstd -dq -c "$archive" | tar -C "$tmp" -xf - ;;
        gzip) tar -C "$tmp" -xzf "$archive" ;;
        none) tar -C "$tmp" -xf "$archive" ;;
    esac
    [ -f "$tmp/$base/Engine/Build/Build.version" ] || die "verify-extract: Build.version missing after extraction."
    if [ "$toolchain_present" -eq 1 ]; then
        [ -d "$tmp/$base/Engine/Extras/ThirdPartyNotUE/SDKs/HostLinux" ] \
            || die "verify-extract: bundled toolchain did not survive the archive round-trip."
    fi
    log "verify-extract  : OK (tree + toolchain intact)"
fi

# --- Summary + next steps ----------------------------------------------------------------------------
log "done."
echo
echo "  Artifact : $archive"
echo "  Verify build-capability (does the archive actually build CARLA?):"
echo "    tar -C /tmp -xf '$archive'"
echo "    export CARLA_UNREAL_ENGINE_PATH=/tmp/$base"
echo "    cd <carla checkout> && ./CarlaSetup.sh --skip-prerequisites \\"
echo "        --content-ssh-key=\$HOME/.ssh/id_ed25519 --vibeue-ssh-key=\$HOME/.ssh/id_ed25519"
echo "    ./Scripts/Linux/BuildCarla.sh && cmake --build Build --target package-development"
if [ "$toolchain_present" -eq 0 ]; then
    echo
    echo "  NOTE: bundled toolchain was MISSING -- the CARLA build above is expected to fail until you"
    echo "        overlay Engine/Extras/ThirdPartyNotUE/SDKs from the source engine into the archive tree."
fi
