# Building CARLA UE5 in an AlmaLinux 8 container (for a RHEL 8 target)

This builds Unreal Engine 5.7.4 and CARLA inside an **AlmaLinux 8** container with Podman. AlmaLinux 8
is binary-compatible with RHEL 8 (glibc 2.28), so the artifacts run on a RHEL 8 deployment target.

## Why AlmaLinux 8 specifically (not 9/10)

Linux binaries are backward- but **not** forward-compatible across glibc versions. Most of CARLA
compiles with the engine's *hermetic* bundled clang toolchain (its own rockylinux8 sysroot), so those
parts are RHEL 8-safe regardless of the build host. But some parts use the **container's system
compiler** — most importantly SUMO `netconvert` (CarlaSetup.sh builds it with system gcc). Built on
AlmaLinux 10 (glibc 2.39) those would fail on RHEL 8 with `version 'GLIBC_2.xx' not found`. Building in
an `:8` image keeps everything RHEL 8-compatible.

> The container is for **building** and validating the build scripts. **Run** the CARLA simulator on
> the real RHEL 8 machine — it needs a GPU/Vulkan, which is far simpler natively than through a
> container (and through WSL2 on Windows in particular).

## Host: Podman on Windows vs native Linux

Either works; the base image, not the host, determines RHEL 8 compatibility.
- **Podman on Windows (WSL2):** convenient. Size the machine up first — the UE + CARLA build needs a
  lot of disk/RAM, and the WSL2 default is too small:
  ```sh
  podman machine stop
  podman machine set --cpus 8 --memory 32768 --disk-size 250
  podman machine start
  ```
  Keep the workspace in a **podman volume** (below), never a `/mnt/c` bind mount (huge builds are
  painfully slow there).
- **Podman on a native Linux host:** better throughput, no machine sizing. Still use the `:8` image.

## 1. Build the base image

The base image installs prerequisites only (no source, tiny context):

```sh
cd carla
podman build -f Util/Docker/Base.alma8.Dockerfile -t carla-base:alma8 Util/Docker
```

This encodes the full RHEL 8 prerequisite set: EPEL + PowerTools, the build toolchain and `-devel`
libraries, `nasm`/`patchelf`, `xerces-c`/`proj` (for SUMO), CMake ≥ 3.28 (Kitware binary), .NET SDK 10
(Microsoft feed), and Python 3.11 — i.e. the same packages you'd `dnf install` on bare RHEL 8.

## 2. Start a build container with a persistent workspace

UE and CARLA are built into a **volume** so they survive container restarts and aren't baked into image
layers. Private repos (the UE fork, carla-content, VibeUE) authenticate over SSH with a single key.

Use the wrapper, which creates the volume, mounts the key read-only and installs it at 0600 inside the
container (host-mounted keys come in world-readable and `ssh` refuses those), and seeds `known_hosts`:

```sh
Util/Docker/run.alma8.sh --ssh-key /mnt/c/Users/sbret/.ssh/VibeUEKey
```

Re-running attaches to the same `carla-build` container (so UE/CARLA work persists). The private key is
never copied into the image — only mounted at runtime. A single account-wide GitHub SSH key covers all
three repos; a per-repo *deploy* key would not authenticate the UE fork.

## 3. Build Unreal Engine 5.7.4 (first time only)

Inside the container. The engine fork is private — clone over SSH (`git@`) with the key/agent above:

```sh
cd /workspaces
git clone -b carla-port git@github.com:sbrett9/UnrealEngine.git unreal-engine
cd unreal-engine
./Setup.sh                  # downloads the bundled clang toolchain + commit dependencies (~tens of GB)
./GenerateProjectFiles.sh
make                        # multi-hour; needs ~100+ GB free in the volume
```

`Setup.sh` installs the engine's bundled clang toolchain (e.g. `v26_clang-20.1.8-rockylinux8`) — that
hermetic toolchain is what makes the CARLA build RHEL 8-portable. The container's pre-installed `-devel`
libraries cover the system dependencies `Setup.sh` would otherwise try to `apt-get` (it is Debian-centric
and a no-op on AlmaLinux).

```sh
export CARLA_UNREAL_ENGINE_PATH=/workspaces/unreal-engine
```

## 4. Build CARLA

Clone the CARLA repo (or bind-mount it) into the workspace and run the setup script with
`--skip-prerequisites` (the image already has them). Content is cloned over SSH from the private mirror:

```sh
cd /workspaces
git clone -b ue5-dev git@github.com:sbrett9/carla.git carla     # or bind-mount your checkout
cd carla
./CarlaSetup.sh --skip-prerequisites \
  --content-ssh-key=/root/.ssh/id_ed25519 \
  --vibeue-ssh-key=/root/.ssh/id_ed25519      # optional MCP plugin
```

`CarlaSetup.sh` then: builds SUMO `netconvert` (system gcc → RHEL 8 glibc), clones the private content,
builds cesium-native (engine clang toolchain), and configures + builds CARLA. To iterate on just the
editor/CarlaNet afterward:

```sh
./Scripts/Linux/BuildCarla.sh                 # editor (C++) + CarlaNet wheel
```

## 5. Persisting / reusing

- The `carla-ws` volume keeps the built engine and CARLA across runs. Re-enter with:
  ```sh
  podman start -ai carla-build        # or: podman exec -it carla-build bash
  ```
- To inspect or copy artifacts out: `podman cp carla-build:/workspaces/carla/Build ./Build-out`.

## Notes / gotchas

- **Disk**: UE + CARLA together need well over 150 GB in the volume. Size `--disk-size` accordingly on
  Windows.
- **Rootless Podman**: running as `root` *inside* the container is fine; rootless maps it to your host
  user, and volume files are owned by you on the host.
- **Don't run the built binaries on a different-glibc host.** They target RHEL 8 (glibc 2.28); they run
  on RHEL 8 and newer, not older.
- **GPU/runtime**: do the actual `RunCarlaServer.sh` / simulator run on the RHEL 8 box. The container
  path is for building and for validating the `.sh` scripts end-to-end on a RHEL 8-compatible userland.
