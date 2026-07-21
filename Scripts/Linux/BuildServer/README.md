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

## Installation

Both scripts must be installed to `/usr/local/bin/` on the GitHub Actions runner host:

```bash
sudo cp upload-to-artifactory prepare-ue-distribution /usr/local/bin/
sudo chown root:root /usr/local/bin/upload-to-artifactory /usr/local/bin/prepare-ue-distribution
sudo chmod 755 /usr/local/bin/upload-to-artifactory /usr/local/bin/prepare-ue-distribution
```

## Prerequisites

- Artifactory API key stored in `/home/catgithubrunner/.artifactory/credentials`
- `zstd` installed for UE distribution extraction
- `curl` for Artifactory communication

## Security

The Artifactory API key is read from a protected file owned by the runner user. Secrets never appear in workflow logs or YAML files.
