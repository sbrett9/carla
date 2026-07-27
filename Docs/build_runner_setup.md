# CARLA build runner — host setup

How to provision the self-hosted GitHub Actions runner account that builds the CARLA Linux
distribution. Follow this end to end when creating the account for the first time, or when
rebuilding the host.

Everything here is one-time host configuration. Once it is in place the workflows need no
privileged operations: the build container runs with the runner account's own uid, so every
file it produces is already owned by that account and nothing in the pipeline needs `sudo`.

Reference values used throughout — substitute your own where they differ:

| Setting | Value |
|---|---|
| Runner account | `catgithubrunner` |
| Account home | `/mnt/raid5/home/catgithubrunner` |
| Runner working directory | `<home>/actions-runner` |
| Build cache root | `<home>/carla-build-cache` |
| Artifactory | `https://iasartifact.sncorp.com:8443/artifactory` |

The workflows never hardcode the home directory. `Scripts/Linux/BuildServer/runner-env.sh`
resolves it from the passwd database and derives every cache path from it, so relocating the
account is a matter of moving the directory and updating passwd.

---

## 1. Account and disk

```sh
sudo useradd -m -d /mnt/raid5/home/catgithubrunner -g SNC catgithubrunner
```

Budget roughly **500 GB** on the account's filesystem:

| Consumer | Size |
|---|---|
| carla-content clone | ~43 GB |
| Content snapshots (3 retained, hardlinked) | ~43 GB plus deltas |
| Unreal Engine archives (2 retained) | ~50 GB |
| Unreal Engine extractions (2 retained) | ~160 GB |
| Derived Data Cache | ~40 GB, grows |
| Third-party dependency cache (vcpkg) | ~20 GB |
| Workspace with `Build/` | ~120 GB |
| Podman image storage | ~20 GB |

Snapshots are cheaper than they look: `sync-carla-content` materialises them with hardlinks,
so a snapshot shares inodes with the clone until git replaces a file. Three snapshots of
unchanged content cost one copy, not three.

## 2. Rootless Podman

**User namespace ranges** in `/etc/subuid` and `/etc/subgid`:

```
catgithubrunner:951968:65536
```

**Setuid helpers** — without these, `newuidmap` fails with `Operation not permitted`:

```sh
sudo chmod u+s /usr/bin/newuidmap /usr/bin/newgidmap
ls -la /usr/bin/new{u,g}idmap        # expect -rwsr-xr-x
```

**Lingering session** — creates `/run/user/<uid>`, which rootless Podman requires and which
`runner-env.sh` checks for:

```sh
sudo loginctl enable-linger catgithubrunner
loginctl list-users
ls -ld /run/user/$(id -u catgithubrunner)
```

**Registry credentials** — log in once as the runner account so the workflows find a stored
auth file. They copy it per job rather than using it in place, so a failed run cannot corrupt it:

```sh
sudo -u catgithubrunner -i podman login iasartifact.sncorp.com:8443
```

## 3. Runner service

`Delegate=yes` is required. Interactive sessions get cgroup delegation from `pam_systemd`;
services run in `system.slice` without it, and rootless Podman cannot create sub-cgroups.

```ini
[Service]
Type=simple
User=catgithubrunner
Group=SNC
WorkingDirectory=/mnt/raid5/home/catgithubrunner/actions-runner
ExecStart=/bin/bash -c 'cd /mnt/raid5/home/catgithubrunner/actions-runner && ./run.sh'
Restart=always
RestartSec=10

# Required for rootless containers.
Delegate=yes
```

Register **one** runner on this host. The build caches are shared mutable state and the
workflows declare `concurrency` groups on the assumption that only one job runs at a time.

## 4. SELinux

The build mounts three large host directories into the container. Under SELinux those mounts
are only accessible if the files carry a container-accessible type.

**Do not use the `:z` or `:Z` volume suffixes on these paths.** They relabel every inode on
every run — ruinous on a 43 GB tree — and `:Z` additionally applies a private MCS category that
locks every other consumer out of the directory. Label once instead:

```sh
# Content snapshots are read-only to the container.
sudo semanage fcontext -a -t container_ro_file_t \
  "/mnt/raid5/home/catgithubrunner/carla-build-cache/content(/.*)?"

# The engine, the dependency cache and the DDC are written during the build.
sudo semanage fcontext -a -t container_file_t \
  "/mnt/raid5/home/catgithubrunner/carla-build-cache/(ue|build-home|ddc)(/.*)?"

# The workspace receives all build output.
sudo semanage fcontext -a -t container_file_t \
  "/mnt/raid5/home/catgithubrunner/actions-runner/_work(/.*)?"

# Podman's own storage, which lives off the default path on this host.
sudo semanage fcontext -a -t container_file_t \
  "/mnt/raid5/home/catgithubrunner/containers(/.*)?"

sudo restorecon -R \
  /mnt/raid5/home/catgithubrunner/carla-build-cache \
  /mnt/raid5/home/catgithubrunner/actions-runner/_work \
  /mnt/raid5/home/catgithubrunner/containers

sudo setsebool -P container_manage_cgroup on
```

Files created later inside these trees inherit the parent directory's type, so `restorecon`
does not need re-running after a content update.

Verify:

```sh
ls -Zd /mnt/raid5/home/catgithubrunner/carla-build-cache/content
sudo ausearch -m avc -ts recent          # expect no denials after a build
```

The distribution workflow currently also passes `--security-opt label=disable`, which is a
blunt fallback covering hosts where the labels above have not been applied. Once the labelling
is verified, remove that option from `.github/workflows/build-carla-ue5.yml` so the build
container runs confined again.

## 5. Credentials

Two secrets live on the host, read only by the helper scripts. Nothing reaches the workflow
YAML, a command line, or the logs.

**Artifactory token:**

```sh
sudo -u catgithubrunner mkdir -p ~catgithubrunner/.artifactory
sudo -u catgithubrunner install -m 600 /dev/null ~catgithubrunner/.artifactory/credentials
# paste the token, no trailing newline concerns -- it is stripped on read
sudo -u catgithubrunner tee ~catgithubrunner/.artifactory/credentials >/dev/null
```

Prefer an **identity token** over a legacy API key: API keys are removed in Artifactory 7.77
and later. `artifactory-common.sh` detects which kind it was given (identity tokens are JWTs
and begin with `eyJ`) and picks the matching authentication header, so no code changes when
you rotate to one.

The token needs deploy and delete permission on `cat-local-generic-dev/carla/releases/` — the
delete is what lets the workflow enforce its retention limit — and read on
`cat-local-generic-dev/unreal/`.

**carla-content deploy key** — read-only, scoped to `CAT/carla-content`:

```sh
sudo -u catgithubrunner ssh-keygen -t ed25519 -N '' \
  -f ~catgithubrunner/.ssh/carla-content-deploy-key
sudo -u catgithubrunner cat ~catgithubrunner/.ssh/carla-content-deploy-key.pub
```

Add the public key as a deploy key on the `CAT/carla-content` repository, **without** write
access.

## 6. Prime the caches

The helper scripts are executed straight out of the repository checkout — nothing is installed
into `/usr/local/bin`. Priming is optional; the first workflow run does the same work. Doing it
by hand keeps the first build from spending an hour on downloads before it compiles anything.

```sh
sudo -u catgithubrunner -i
git clone git@github.sncorp.com:CAT/carla.git ~/carla-priming
cd ~/carla-priming
chmod +x Scripts/Linux/BuildServer/*
. Scripts/Linux/BuildServer/runner-env.sh

# ~43 GB on first run; publishes an immutable snapshot and prints its path.
Scripts/Linux/BuildServer/sync-carla-content --ref ue5-dev

# ~25 GB download, verified against its published SHA256, then extracted.
Scripts/Linux/BuildServer/prepare-ue-distribution \
  cat-local-generic-dev/unreal/UnrealEngine-5.7.4-carla-port-049f42955-Linux.tar.zst
```

Then apply the SELinux labels from section 4, since the directories now exist.

## 7. Running a build

The distribution workflow is **manual only**. It executes the checked-out revision's own build
scripts on this host with access to both secrets above, so it must not be wired to a push or
pull-request trigger until the branch and author rules for that are settled.

Actions → *Build CARLA UE5* → *Run workflow*:

| Input | Meaning |
|---|---|
| `ue_distribution` | Engine archive filename in `cat-local-generic-dev/unreal/` |
| `content_ref` | carla-content ref to build against |
| `content_mount_mode` | `auto` probes for overlay support; force `overlay`/`readonly`/`readwrite` to override |
| `clean_build` | Discard `Build/` and the vcpkg dependency cache before building |
| `skip_upload` | Build without publishing |

Output lands at `cat-local-generic-dev/carla/releases/Carla-<version>-Linux-<config>-g<sha>.tar.gz`
with a `.sha256` sidecar. The three most recent releases are retained; older ones are deleted
automatically after each successful upload.

## 8. Caching model

**Content.** One clone at `<cache>/content/repo`, plus immutable per-commit snapshots at
`<cache>/content/snapshots/<commit>`. Builds mount a snapshot, never the clone, so a build
cannot dirty the cache. When the content commit has not changed — nearly always — the sync
costs one `ls-remote` and no transfer.

Roll back to earlier content by repointing the published symlink:

```sh
ls -lt ~/carla-build-cache/content/snapshots
ln -sfn ~/carla-build-cache/content/snapshots/<commit> ~/carla-build-cache/content/current.new
mv -Tf ~/carla-build-cache/content/current.new ~/carla-build-cache/content/current
```

A build still re-syncs to the requested ref, so to pin content, run the workflow with
`content_ref` set to a commit SHA rather than a branch name.

**Engine.** Archives and extractions are keyed on the archive's SHA256 and marked complete
only after they succeed, so an interrupted download or extraction is never reused. Two of each
are retained.

**Compilation.** `Build/` persists in the workspace between runs, and the container's `HOME`
points at `<cache>/build-home` so the vcpkg dependency cache survives too. The Unreal Derived
Data Cache is mounted at `<cache>/ddc`. Together these are the difference between a warm build
and recompiling every dependency and shader from scratch; use `clean_build` when you
deliberately want the cold path.

## 9. Troubleshooting

**`newuidmap: write to uid_map failed: Operation not permitted`**
Check the setuid bits (section 2), `Delegate=yes` (section 3), and the subuid/subgid ranges.

**`runner-env.sh: /run/user/<uid> does not exist`**
Lingering is not enabled: `sudo loginctl enable-linger catgithubrunner`.

**Permission denied on a bind mount, or `ausearch` shows AVC denials**
The SELinux labels in section 4 are missing or were applied before the directory existed.
Re-run `restorecon` on the affected tree.

**`a stale bind mount is still active at .../Content/Carla`**
A container was killed and left its mount behind. Anything that cleans the workspace would
delete through the mount into the content cache, so the workflow refuses to start. Clear it:

```sh
fusermount -u <path> || sudo umount <path>
```

**The cook fails writing into the content directory**
The mount is read-only because the overlay probe failed. Re-run with `content_mount_mode` set
to `readwrite` to confirm that is the cause, then investigate overlay support — `readwrite`
lets the build modify the shared snapshot and should not be left in place.

**`another sync-carla-content holds the cache lock`**
Another sync is running, or one was killed while holding the lock. Confirm nothing is running
(`pgrep -af sync-carla-content`) and remove `<cache>/content/.lock` if not.
