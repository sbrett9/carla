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

## Security

The Artifactory API key is read from a protected file owned by the runner user. Secrets never appear in workflow logs or YAML files.
