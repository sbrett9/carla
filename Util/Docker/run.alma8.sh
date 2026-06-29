#!/usr/bin/env bash
#
# run.alma8.sh — start an interactive CARLA build container (carla-base:alma8).
#
# Two ways to provide the source:
#   1) Default: a persistent podman volume mounted at /workspaces, into which you clone UnrealEngine
#      and carla inside the container.
#   2) --carla-dir/--engine-dir: bind-mount an EXISTING host checkout at the SAME absolute path
#      inside the container. Same-path mounts matter -- a host-built engine has its own absolute
#      paths baked into its binaries, and the CARLA artifacts built here get baked with the host
#      paths too, so you can run the result NATIVELY on the host afterward (no extraction).
#
# An SSH key for the private repos (carla-content, VibeUE, UnrealEngine) is mounted READ-ONLY and
# installed to 0600 inside the container -- never baked into the image. It is optional: if the
# content is already present in your checkout, no key is needed. See Docs/build_container_rhel8.md.
#
# Run this on the Podman host. Examples:
#   ./run.alma8.sh                                   # volume-based, clone inside
#   ./run.alma8.sh --ssh-key ~/.ssh/id_ed25519       # volume-based with a key
#   ./run.alma8.sh --carla-dir ~/Projects/Carla_UE/carla \
#                  --engine-dir ~/Projects/Carla_UE/UnrealEngine \
#                  --ssh-key ~/.ssh/id_ed25519       # build an existing host checkout in place

set -euo pipefail

ssh_key="${SSH_KEY:-/mnt/c/Users/sbret/.ssh/VibeUEKey}"
volume="${CARLA_WS_VOLUME:-carla-ws}"
image="${CARLA_IMAGE:-carla-base:alma8}"
name="${CARLA_CONTAINER:-carla-build}"
cpus="${CARLA_BUILD_CPUS:-}"     # number of logical CPUs the build may use (default: all)
carla_dir="${CARLA_DIR:-}"       # existing host carla checkout to bind-mount at the same path
engine_dir="${CARLA_ENGINE_DIR:-}" # existing host UnrealEngine checkout to bind-mount at the same path
non_root="${CARLA_NON_ROOT:-0}"  # run as the host user (non-root) so the UE cook/package works

usage() {
    cat <<EOF
Usage: run.alma8.sh [options]

Start an interactive carla-base:alma8 build container.

Options:
  --carla-dir <path>   Bind-mount an existing host carla checkout at the same path in the container
                       (instead of the volume). Env: \$CARLA_DIR.
  --engine-dir <path>  Bind-mount an existing host UnrealEngine checkout at the same path and set
                       CARLA_UNREAL_ENGINE_PATH to it. Env: \$CARLA_ENGINE_DIR.
  --non-root           Run the container as the host user (non-root) instead of root. REQUIRED for
                       packaging: the UE cook (UnrealEditor-Cmd) refuses to run as root. Uses
                       --userns=keep-id (rootless podman) or --user <owner> (rootful) so the
                       in-container UID matches the mounted files' owner -- writable, no chown.
                       Use together with --carla-dir/--engine-dir (the root-owned volume is not
                       writable by a non-root user). Env: \$CARLA_NON_ROOT=1.
  --ssh-key <path>     SSH private key for the private repos (default: $ssh_key, or \$SSH_KEY).
                       Optional -- skipped if the file is absent (private clones then need the
                       content already present in the checkout).
  --volume <name>      Workspace podman volume, used only when --carla-dir is NOT given
                       (default: $volume, or \$CARLA_WS_VOLUME).
  --image <ref>        Image to run (default: $image, or \$CARLA_IMAGE).
  --name <name>        Container name (default: $name, or \$CARLA_CONTAINER).
  --cpus <N>           Limit the build to N logical CPUs (cpuset 0..N-1), leaving the rest free
                       (default: all, or \$CARLA_BUILD_CPUS). nproc inside reflects this, so
                       -j\$(nproc)/Ninja/UBT all self-cap. Applies only when CREATING the container;
                       for a running one use: podman update --cpuset-cpus=0-<N-1> $name
  -h, --help           Show this help and exit.

SELinux note: the container runs with 'label=disable' so it can read the bind-mounted source dirs
and SSH key on enforcing hosts (RHEL/Alma) without relabelling them.
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --carla-dir)   carla_dir="$2"; shift ;;
        --carla-dir=*) carla_dir="${1#*=}" ;;
        --engine-dir)   engine_dir="$2"; shift ;;
        --engine-dir=*) engine_dir="${1#*=}" ;;
        --ssh-key)   ssh_key="$2"; shift ;;
        --ssh-key=*) ssh_key="${1#*=}" ;;
        --volume)    volume="$2"; shift ;;
        --volume=*)  volume="${1#*=}" ;;
        --image)     image="$2"; shift ;;
        --image=*)   image="${1#*=}" ;;
        --name)      name="$2"; shift ;;
        --name=*)    name="${1#*=}" ;;
        --cpus)      cpus="$2"; shift ;;
        --cpus=*)    cpus="${1#*=}" ;;
        --non-root)  non_root=1 ;;
        -h|--help)   usage; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
    esac
    shift
done

if [ -n "$carla_dir" ] && [ ! -d "$carla_dir" ]; then
    echo "ERROR: --carla-dir not found: $carla_dir" >&2; exit 1
fi
if [ -n "$engine_dir" ] && [ ! -d "$engine_dir" ]; then
    echo "ERROR: --engine-dir not found: $engine_dir" >&2; exit 1
fi
if [ -n "$carla_dir" ] && [ -z "$engine_dir" ]; then
    echo "NOTE: --carla-dir given without --engine-dir; CARLA_UNREAL_ENGINE_PATH will not be set by"
    echo "      this wrapper -- set it inside the container before building."
fi

# Optional CPU cap: pin to logical CPUs 0..N-1 so the build leaves the rest free. nproc honors the
# cpuset, so -j$(nproc)/Ninja/UBT all self-limit.
cpuset_arg=()
if [ -n "$cpus" ]; then
    if ! [ "$cpus" -ge 1 ] 2>/dev/null; then echo "ERROR: --cpus must be a positive integer" >&2; exit 2; fi
    cpuset_arg=(--cpuset-cpus="0-$((cpus - 1))")
fi

# Build the mount/env list.
mount_args=()
if [ -n "$carla_dir" ]; then
    # Bind-mount the existing checkout(s) at the SAME absolute path (so host-built engine binaries and
    # the CARLA artifacts built here resolve identically inside the container and on the host).
    mount_args+=(-v "$carla_dir:$carla_dir")
    ws_hint="$carla_dir"
else
    podman volume inspect "$volume" >/dev/null 2>&1 || podman volume create "$volume"
    mount_args+=(-v "$volume:/workspaces")
    ws_hint="/workspaces"
fi

env_args=(-e "CARLA_WS_HINT=$ws_hint")
if [ -n "$engine_dir" ]; then
    mount_args+=(-v "$engine_dir:$engine_dir")
    env_args+=(-e "CARLA_UNREAL_ENGINE_PATH=$engine_dir")
fi

# The SSH key is optional. When present, mount it read-only at /tmp/id_key; the bootstrap installs it
# to ~/.ssh/id_ed25519 at 0600 (host-mounted files arrive world-readable and ssh refuses loose perms).
if [ -f "$ssh_key" ]; then
    mount_args+=(-v "$ssh_key:/tmp/id_key:ro")
else
    echo "WARNING: SSH key not found: $ssh_key -- continuing without it. Private repo clones"
    echo "         (carla-content/VibeUE) will fail unless already present in the checkout."
fi

# Non-root mode: required for packaging because the UE cook (UnrealEditor-Cmd) refuses to run as root.
# Make the in-container UID equal the host UID that OWNS the mounted source, so files stay writable
# without chown:
#   - rootless podman: --userns=keep-id maps the host user to the same UID inside (and runs as it).
#   - rootful  podman: --user <uid>:<gid> of the mounted source's owner (the wrapper may itself be
#     running under sudo as root, so use the dir's owner, not `id -u`).
# UE writes to HOME (DDC cache, logs); the matched UID has no home in the image, so point HOME at a
# writable container-private dir.
userns_arg=()
if [ "$non_root" = "1" ]; then
    rootless=$(podman info --format '{{.Host.Security.Rootless}}' 2>/dev/null || echo "")
    if [ "$rootless" = "true" ]; then
        userns_arg=(--userns=keep-id)
    else
        if [ -n "$carla_dir" ]; then owner_spec="$(stat -c '%u:%g' "$carla_dir")"; else owner_spec="$(id -u):$(id -g)"; fi
        userns_arg=(--user "$owner_spec")
    fi
    env_args+=(-e "HOME=/tmp/ue-home")
    if [ -z "$carla_dir" ]; then
        echo "WARNING: --non-root with the root-owned '$volume' volume will hit permission errors."
        echo "         Use --carla-dir/--engine-dir (host dirs you own) for a non-root build/package."
    fi
fi

# Reuse the named container across runs if it already exists.
if podman container exists "$name"; then
    echo "Container '$name' exists; starting/attaching. (Remove with: podman rm -f $name)"
    [ -n "$cpus" ] && echo "NOTE: --cpus only applies to a NEW container; for this one run: podman update --cpuset-cpus=0-$((cpus - 1)) $name"
    exec podman start -ai "$name"
fi

# --security-opt label=disable: on SELinux-enforcing hosts (RHEL/Alma) the container is otherwise
# denied access to bind-mounted host files/dirs. Disabling SELinux labelling for this build container
# avoids that without relabelling the host paths (which a ':Z' mount option would do). No-op on
# non-SELinux hosts.
exec podman run -it --name "$name" \
    --security-opt label=disable \
    "${cpuset_arg[@]}" \
    "${userns_arg[@]}" \
    "${mount_args[@]}" \
    "${env_args[@]}" \
    "$image" bash -lc '
        mkdir -p "$HOME" 2>/dev/null || true
        if [ -f /tmp/id_key ]; then
            mkdir -p ~/.ssh && chmod 700 ~/.ssh
            install -m 600 /tmp/id_key ~/.ssh/id_ed25519
            ssh-keyscan -t ed25519,rsa,ecdsa github.com >> ~/.ssh/known_hosts 2>/dev/null || true
            echo "SSH key installed for github.com."
        fi
        echo "Build container ready. Workspace: $CARLA_WS_HINT"
        [ -n "${CARLA_UNREAL_ENGINE_PATH:-}" ] && echo "CARLA_UNREAL_ENGINE_PATH=$CARLA_UNREAL_ENGINE_PATH"
        echo "See Docs/build_container_rhel8.md for the build steps."
        exec bash'
