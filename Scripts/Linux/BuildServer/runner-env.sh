#!/usr/bin/env bash
#
# runner-env.sh - single source of truth for the build runner's host paths.
#
# Source this (do not execute it) from every workflow step that touches the runner
# account's home, the persistent caches, or rootless Podman:
#
#   . "$GITHUB_WORKSPACE/Scripts/Linux/BuildServer/runner-env.sh"
#
# GitHub Actions rewrites HOME to a per-job temporary directory, which breaks rootless
# Podman (it looks for its storage and registry auth under HOME). Sourcing this restores
# HOME to the runner account's real home and derives every cache path from it, so the
# workflows never hardcode a home directory that may or may not be the real one.
#
# Everything is overridable from the environment, so a second runner account or a
# relocated cache needs no edits here.
#
# Exports:
#   RUNNER_ACCOUNT           runner service account name          (default: catgithubrunner)
#   RUNNER_ACCOUNT_HOME      its real home, resolved from passwd
#   HOME                     set to RUNNER_ACCOUNT_HOME
#   XDG_RUNTIME_DIR          /run/user/<uid>, required by rootless Podman
#   CARLA_CACHE_ROOT         parent of all persistent build caches
#   UE_CACHE_DIR             Unreal Engine archive + extraction cache
#   CONTENT_CACHE_ROOT       carla-content clone + hardlink snapshots
#   BUILD_HOME_DIR           persistent HOME for the build container (vcpkg, UE user dirs)
#   DDC_DIR                  persistent Unreal Derived Data Cache
#   ARTIFACTORY_CREDENTIALS  path to the Artifactory API token file
#   CONTENT_SSH_KEY          path to the carla-content deploy key
#

# ── Runner account ──────────────────────────────────────────────────────────
# Resolve the home directory from the passwd database rather than assuming a path.
# The account's home lives on the RAID array on this host, but /home/<account> may
# also exist as a symlink; passwd is the only authoritative answer.
RUNNER_ACCOUNT="${RUNNER_ACCOUNT:-catgithubrunner}"

if [ -z "${RUNNER_ACCOUNT_HOME:-}" ]; then
    RUNNER_ACCOUNT_HOME="$(getent passwd "$RUNNER_ACCOUNT" 2>/dev/null | cut -d: -f6)"
fi

if [ -z "$RUNNER_ACCOUNT_HOME" ] || [ ! -d "$RUNNER_ACCOUNT_HOME" ]; then
    echo "runner-env.sh: cannot resolve home directory for account '$RUNNER_ACCOUNT'" >&2
    echo "               set RUNNER_ACCOUNT_HOME explicitly if the account is not local" >&2
    return 1 2>/dev/null || exit 1
fi

export RUNNER_ACCOUNT RUNNER_ACCOUNT_HOME
export HOME="$RUNNER_ACCOUNT_HOME"

# Rootless Podman needs the per-user runtime directory that `loginctl enable-linger`
# creates. Without it, Podman falls back to a path it cannot write and the run fails
# with an opaque storage error.
export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR_OVERRIDE:-/run/user/$(id -u)}"

if [ ! -d "$XDG_RUNTIME_DIR" ]; then
    echo "runner-env.sh: $XDG_RUNTIME_DIR does not exist." >&2
    echo "               Run: sudo loginctl enable-linger $RUNNER_ACCOUNT" >&2
    return 1 2>/dev/null || exit 1
fi

# ── Persistent caches ───────────────────────────────────────────────────────
# All caches hang off one root so a single SELinux fcontext rule and a single disk
# budget cover them. See Docs/build_runner_setup.md for the labelling commands.
export CARLA_CACHE_ROOT="${CARLA_CACHE_ROOT:-$RUNNER_ACCOUNT_HOME/carla-build-cache}"

export UE_CACHE_DIR="${UE_CACHE_DIR:-$CARLA_CACHE_ROOT/ue}"
export CONTENT_CACHE_ROOT="${CONTENT_CACHE_ROOT:-$CARLA_CACHE_ROOT/content}"

# The build container runs with HOME pointed at this directory instead of a throwaway
# path inside the container. That makes the vcpkg/ezvcpkg dependency cache and the
# Unreal user directories survive between runs, which is the difference between a warm
# build and recompiling every third-party dependency and shader from scratch.
export BUILD_HOME_DIR="${BUILD_HOME_DIR:-$CARLA_CACHE_ROOT/build-home}"
export DDC_DIR="${DDC_DIR:-$CARLA_CACHE_ROOT/ddc}"

# ── Credentials ─────────────────────────────────────────────────────────────
# Read by the helper scripts, never by the workflow YAML, so tokens stay out of logs.
export ARTIFACTORY_CREDENTIALS="${ARTIFACTORY_CREDENTIALS:-$RUNNER_ACCOUNT_HOME/.artifactory/credentials}"
export CONTENT_SSH_KEY="${CONTENT_SSH_KEY:-$RUNNER_ACCOUNT_HOME/.ssh/carla-content-deploy-key}"

mkdir -p "$CARLA_CACHE_ROOT" "$UE_CACHE_DIR" "$CONTENT_CACHE_ROOT" "$BUILD_HOME_DIR" "$DDC_DIR"
