# Build Server Scripts

Helper scripts for automated CARLA builds on self-hosted GitHub Actions runners.

## Scripts

### `upload-to-artifactory`
Uploads files to JFrog Artifactory generic repositories with authentication.

**Usage:**
```bash
upload-to-artifactory <local-file> <repo-name/target/path>
```

**Example:**
```bash
upload-to-artifactory Build/Dist/carla.tar.gz cat-local-generic-dev/carla/releases/carla.tar.gz
```

### `prepare-ue-distribution`
Downloads, verifies, and extracts Unreal Engine distributions from Artifactory with smart caching.

**Usage:**
```bash
prepare-ue-distribution <artifactory-path> <cache-dir>
```

**Example:**
```bash
UE_PATH=$(prepare-ue-distribution \
  cat-local-generic-dev/unreal/UnrealEngine-5.7.4-carla-port-049f42955-Linux.tar.zst \
  /home/catgithubrunner/ue-cache)
```

### `sync-carla-content`
Clones or updates the private carla-content Git-LFS repository to a persistent location on the runner host. Content is cached across workflow runs and volume-mounted into build containers.

**Usage:**
```bash
sync-carla-content [--ref <branch>] [--target <path>] [--ssh-key <path>]
```

**Example:**
```bash
# Initial setup (run once)
sync-carla-content --ref ue5-dev --target /home/catgithubrunner/carla-content

# Update in workflow
sync-carla-content --verify-only || sync-carla-content --ref ue5-dev
```

## Installation

All scripts must be installed to `/usr/local/bin/` on the GitHub Actions runner host:

```bash
sudo cp upload-to-artifactory prepare-ue-distribution sync-carla-content /usr/local/bin/
sudo chown root:root /usr/local/bin/{upload-to-artifactory,prepare-ue-distribution,sync-carla-content}
sudo chmod 755 /usr/local/bin/{upload-to-artifactory,prepare-ue-distribution,sync-carla-content}
```

## Prerequisites

- Artifactory API key stored in `/home/catgithubrunner/.artifactory/credentials`
- CARLA content deploy key stored in `/home/catgithubrunner/.ssh/carla-content-deploy-key`
- `zstd` installed for UE distribution extraction
- `curl` for Artifactory communication
- `git` and `git-lfs` for content repository management

## How It Works

### Build Pipeline Overview

The GitHub Actions workflow (`.github/workflows/build-carla-ue5.yml`) orchestrates the build:

1. **Checkout** - Clone CARLA source to runner workspace
2. **Sync Content** - Update carla-content repo (43 GB Git-LFS, cached)
3. **Prepare UE** - Download/extract UE distribution (25 GB, cached)
4. **Build** - Run containerized build with volume mounts:
   - CARLA source at same path (for UE asset references)
   - carla-content mounted read-only
   - UE distribution mounted read-only
5. **Upload** - Push distribution tarball to Artifactory
6. **Archive** - Store artifacts in GitHub Actions (30 day retention)

### Script Mechanics

**`upload-to-artifactory`**
- Reads API key from `/home/catgithubrunner/.artifactory/credentials` (mode 600)
- Uses `curl` with `X-JFrog-Art-Api` header authentication
- Verifies upload with HEAD request
- Keeps credentials out of workflow YAML and logs

**`prepare-ue-distribution`**
- Downloads UE archive from Artifactory with SHA256 verification
- Caches both archives and extracted content in `<cache-dir>/`
- Uses parallel zstd decompression (`-T0` for all CPU cores)
- Atomic extraction (temp dir → verify → rename)
- Returns path to extracted UE distribution for `CARLA_UNREAL_ENGINE_PATH`

**`sync-carla-content`**
- Manages persistent Git-LFS clone in `<target-dir>/`
- SSH deploy key authentication (non-interactive, no host-key prompts)
- Smart LFS caching (only downloads new/changed objects)
- Atomic clone (temp → verify sentinel file → move)
- Verify-only mode for fast workflow validation
- Content directory is volume-mounted into containers at same path

### Volume Mount Strategy

The build uses bind-mounts to share data between host and container:

```bash
podman run \
  -v "$WORKSPACE:$WORKSPACE" \              # CARLA source (read-write)
  -v "$CONTENT_DIR:$WORKSPACE/Unreal/CarlaUnreal/Content/Carla:ro" \  # Content (read-only)
  -v "$UE_PATH:/opt/unreal-engine:ro" \     # UE distribution (read-only)
  ...
```

**Why same-path mounting?**
- Unreal Engine stores absolute paths to assets in cooked data
- Mounting CARLA source at the same path ensures asset references work
- Content must be at `Unreal/CarlaUnreal/Content/Carla` relative to CARLA root

## Security

**Credential Isolation:**
- Artifactory API key: `/home/catgithubrunner/.artifactory/credentials` (mode 600)
- Content deploy key: `/home/catgithubrunner/.ssh/carla-content-deploy-key` (mode 600)
- Both owned by `catgithubrunner` user
- Scripts read credentials, workflows never see them
- No secrets in workflow YAML or logs

**Deploy Key Permissions:**
- Content deploy key has read-only access to `CAT/carla-content`
- Cannot push or modify repository
- Scoped to single repository
