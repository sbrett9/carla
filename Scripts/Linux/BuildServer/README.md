# Build server scripts

Helper scripts used by the CARLA distribution workflow on the self-hosted GitHub Actions
runner.

They are executed **from the repository checkout**, not installed to the host:

```yaml
- run: |
    . "$GITHUB_WORKSPACE/Scripts/Linux/BuildServer/runner-env.sh"
    "$GITHUB_WORKSPACE/Scripts/Linux/BuildServer/sync-carla-content" --ref ue5-dev
```

There is no bootstrap problem in doing so — every step that needs them runs after
`actions/checkout` — and it means the scripts version with the workflow that calls them
instead of drifting as a separate copy in `/usr/local/bin` that unrelated users of the build
host can also see and run.

Host provisioning (accounts, rootless Podman, SELinux labels, credentials) is documented
separately in [`Docs/build_runner_setup.md`](../../../Docs/build_runner_setup.md).

## Scripts

### `runner-env.sh` — sourced, not executed

Resolves the runner account's real home from the passwd database and restores `HOME` and
`XDG_RUNTIME_DIR`, which GitHub Actions overrides per step and rootless Podman requires.
Derives every cache path from that home, so no workflow hardcodes a directory.

Exports `CARLA_CACHE_ROOT`, `UE_CACHE_DIR`, `CONTENT_CACHE_ROOT`, `BUILD_HOME_DIR`, `DDC_DIR`,
`ARTIFACTORY_CREDENTIALS` and `CONTENT_SSH_KEY`. All are overridable from the environment.

### `artifactory-common.sh` — sourced, not executed

Shared Artifactory access: `art_curl` (authenticated, retrying) and `art_url`. Reads the token
from a mode-600 file and passes it through a header file rather than the command line, where
other users could read it out of `/proc`. Detects whether the credential is a legacy API key or
an identity token and selects the matching header, so rotating to an identity token — required
from Artifactory 7.77 — needs no code change.

### `sync-carla-content`

Maintains the ~43 GB Git-LFS content cache and publishes an immutable per-commit snapshot for
builds to mount. Prints the snapshot path on stdout.

```sh
CONTENT=$(sync-carla-content --ref ue5-dev --keep 3)
```

- One clone at `<cache>/repo`; builds never touch it.
- Snapshots at `<cache>/snapshots/<commit>`, materialised with hardlinks, so a snapshot costs
  inodes rather than a second 43 GB copy. Git replaces files rather than editing them in place,
  so updating the clone leaves older snapshots intact.
- A snapshot is marked complete only after its sentinel asset verifies as real content and not
  an unsmudged LFS pointer, so an interrupted run is never reused.
- Fast path: resolves the ref with `ls-remote` first and exits immediately when that commit is
  already snapshotted, which is the usual case for a repository that changes rarely.
- All mutation is serialised behind a `flock`, which is also what makes removing a leftover git
  lock file safe — the lock proves no other git process is working in the clone.

### `prepare-ue-distribution`

Downloads, verifies and extracts an Unreal Engine distribution. Prints the engine root.

```sh
UE_PATH=$(prepare-ue-distribution cat-local-generic-dev/unreal/UnrealEngine-5.7.4-...-Linux.tar.zst)
```

- The archive is hashed locally with `sha256sum` and compared against the published sidecar.
  A truncated download fails the build instead of being cached as authoritative.
- Downloads resume where they stopped and retry on transient failures; a dropped connection
  must not restart a 25 GB transfer or fail an hours-long build.
- Extractions are keyed on the archive checksum and marked complete only on success.
- Retains the newest two archives and extractions and sweeps interrupted ones.

### `upload-to-artifactory`

Publishes an artifact plus a `.sha256` sidecar, and sends the checksum with the upload so
Artifactory rejects a truncated transfer server-side.

```sh
upload-to-artifactory Build/Dist/Carla-...-g1a2b3c4.tar.gz \
  cat-local-generic-dev/carla/releases/Carla-...-g1a2b3c4.tar.gz
```

### `prune-artifactory-releases`

Caps the release folder, since each distribution is tens of gigabytes. Ranks artifacts by
Artifactory creation time — filenames carry a commit hash and do not sort chronologically — and
deletes everything past the newest N along with its sidecar.

```sh
prune-artifactory-releases cat-local-generic-dev/carla/releases --keep 3 --dry-run
```

## Volume mount strategy

```sh
podman run \
  -v "$WORKSPACE:$WORKSPACE" \                       # CARLA source, read-write
  -v "$SNAPSHOT:$WORKSPACE/Unreal/CarlaUnreal/Content/Carla:O" \   # content, overlay
  -v "$UE_PATH:/opt/unreal-engine" \                 # engine, read-write (UBT writes here)
  -v "$BUILD_HOME_DIR:/home/builder" \               # persistent dependency cache
  -v "$DDC_DIR:/ddc"                                 # persistent Derived Data Cache
```

**Same-path workspace mount.** Unreal records absolute paths in cooked data, so the source has
to appear at the same path inside the container as outside it.

**Overlay content mount (`:O`).** The build must be able to write inside the content directory
— `CarlaSetup.sh` deletes an uncookable asset there — without those writes reaching the shared
cache. An overlay mount gives the container a throwaway writable layer over a read-only lower
directory, which is exactly that. The workflow probes for overlay support and falls back to a
plain read-only mount, where the deletion warns instead of aborting.

**Persistent container `HOME`.** Pointing `HOME` at a real directory instead of a throwaway
path inside the container is what makes the vcpkg dependency cache survive between builds.
