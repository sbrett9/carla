#!/usr/bin/env bash
#
# run.alma8.sh — start an interactive CARLA build container (carla-base:alma8) with a persistent
# workspace volume and an SSH key for the private repos (UnrealEngine, carla-content, VibeUE).
#
# Run this inside the Podman host (e.g. the AlmaLinux WSL distro). The SSH key is mounted READ-ONLY
# and copied to 0600 inside the container at start -- it is never baked into the image (a private key
# in an image layer is permanently extractable). See Docs/build_container_rhel8.md.
#
# Usage:
#   ./run.alma8.sh                              # defaults below
#   ./run.alma8.sh --ssh-key /path/to/key      # different key
#   SSH_KEY=/path/to/key ./run.alma8.sh        # same, via env

set -euo pipefail

ssh_key="${SSH_KEY:-/mnt/c/Users/sbret/.ssh/VibeUEKey}"
volume="${CARLA_WS_VOLUME:-carla-ws}"
image="${CARLA_IMAGE:-carla-base:alma8}"
name="${CARLA_CONTAINER:-carla-build}"
cpus="${CARLA_BUILD_CPUS:-}"   # number of logical CPUs the build may use (default: all)

usage() {
    cat <<EOF
Usage: run.alma8.sh [options]

Start an interactive carla-base:alma8 build container with a workspace volume and an SSH key.

Options:
  --ssh-key <path>     SSH private key for the private repos (default: $ssh_key, or \$SSH_KEY).
  --volume <name>      Workspace podman volume (default: $volume, or \$CARLA_WS_VOLUME).
  --image <ref>        Image to run (default: $image, or \$CARLA_IMAGE).
  --name <name>        Container name (default: $name, or \$CARLA_CONTAINER).
  --cpus <N>           Limit the build to N logical CPUs (cpuset 0..N-1), leaving the rest free
                       for other work (default: all, or \$CARLA_BUILD_CPUS). nproc inside the
                       container reflects this, so -j\$(nproc)/Ninja/UBT all self-cap.
                       NOTE: applies only when CREATING the container; for an already-running one
                       use: podman update --cpuset-cpus=0-<N-1> $name
  -h, --help           Show this help and exit.

The key is mounted read-only and installed to /root/.ssh/id_ed25519 (0600) inside the container;
github.com is added to known_hosts. The workspace volume persists UE + CARLA across runs.
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
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
        -h|--help)   usage; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
    esac
    shift
done

if [ ! -f "$ssh_key" ]; then
    echo "ERROR: SSH key not found: $ssh_key" >&2
    echo "       Pass --ssh-key <path> or set \$SSH_KEY (path must be visible to the Podman host)." >&2
    exit 1
fi

podman volume inspect "$volume" >/dev/null 2>&1 || podman volume create "$volume"

# Optional CPU cap: pin to logical CPUs 0..N-1 so the build leaves the rest free. nproc honors
# the cpuset, so -j$(nproc)/Ninja/UBT all self-limit. (P/E cores are not distinguishable inside
# WSL2, so this caps the COUNT, not which physical cores are used.)
cpuset_arg=()
if [ -n "$cpus" ]; then
    if ! [ "$cpus" -ge 1 ] 2>/dev/null; then echo "ERROR: --cpus must be a positive integer" >&2; exit 2; fi
    cpuset_arg=(--cpuset-cpus="0-$((cpus - 1))")
fi

# Reuse the named container across runs if it already exists.
if podman container exists "$name"; then
    echo "Container '$name' exists; starting/attaching. (Remove with: podman rm -f $name)"
    [ -n "$cpus" ] && echo "NOTE: --cpus only applies to a NEW container; for this one run: podman update --cpuset-cpus=0-$((cpus - 1)) $name"
    exec podman start -ai "$name"
fi

# The key is mounted at /tmp/id_key:ro, then installed to ~/.ssh/id_ed25519 with 0600 because
# host-mounted files (especially from a Windows filesystem) come in world-readable and ssh refuses
# keys with loose permissions. known_hosts is seeded so the clones don't prompt.
#
# --security-opt label=disable: on SELinux-enforcing hosts (RHEL/Alma) the container is otherwise
# denied access to bind-mounted host files ("install: cannot stat '/tmp/id_key': permission denied").
# Disabling SELinux labelling for this build container avoids that without relabelling your real SSH
# key (which a ':Z' mount option would do). It's a no-op on non-SELinux hosts.
exec podman run -it --name "$name" \
    --security-opt label=disable \
    "${cpuset_arg[@]}" \
    -v "$volume:/workspaces" \
    -v "$ssh_key:/tmp/id_key:ro" \
    "$image" bash -lc '
        mkdir -p ~/.ssh && chmod 700 ~/.ssh
        install -m 600 /tmp/id_key ~/.ssh/id_ed25519
        ssh-keyscan -t ed25519,rsa,ecdsa github.com >> ~/.ssh/known_hosts 2>/dev/null || true
        echo "Workspace: /workspaces   SSH key installed for github.com"
        echo "Next: see Docs/build_container_rhel8.md (clone UnrealEngine @ carla-port, then CARLA)."
        exec bash'
