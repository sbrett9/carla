# 15 — Automated CARLA + Unreal Engine Build/Distribution Pipeline

Status: research + planning only. No build, CI, or engine code was changed by this pass.

Related: `Docs/build_container_rhel8.md` (the manual container build this automates), `Util/Docker/Base.alma8.Dockerfile`,
`Util/Docker/run.alma8.sh`, `CarlaSetup.sh`, `Scripts/Linux/BuildCarla.sh`, `Scripts/Linux/MakeDistribution.sh`.

Goal driving this doc: **a push to the `ue5-dev` branch should produce a Linux digital-twin distribution
archive automatically, published to an Artifactory repository.** This is a plan + options writeup, not an
implementation.

---

## 1. The single constraint that shapes everything: build size

The build is far too large for GitHub-*hosted* runners. From this repo's own docs:

| Cost | Source |
|------|--------|
| Unreal Engine from source: **> 1 hr, ~225 GB disk** | `Docs/build_linux_ue5.md:65` |
| UE + CARLA together: **well over 150 GB**, recommend 250 GB disk / 32 GB RAM / 8 CPU | `Docs/build_container_rhel8.md:172`, `:26` |
| Cooked CARLA package: **30–40 GB**; multi-minute compression | `README.md:120`, `Scripts/Linux/MakeDistribution.sh:200` |
| Bundled clang toolchain the build depends on | `v26_clang-20.1.8-rockylinux8` (`Docs/build_container_rhel8.md:95`) |

GitHub-hosted runners provide ~14 GB disk, 7 GB RAM, and a 6-hour job cap — impossible. **Therefore
"GitHub Actions" here means: GitHub Actions as the trigger/orchestration layer, running on a
*self-hosted* runner installed on your persistent build box.** The heavy lifting is your hardware; GitHub
only schedules it.

Decisions taken for this plan (from the planning conversation):

- **Runner**: one persistent, self-hosted Linux build box.
- **Engine**: a **build-capable** formal *Installed Engine Build*, serving two roles — a standalone
  deliverable *and* the payload baked into a container image.
- **Distribution produced per push**: the **Linux digital-twin bundle** (`Scripts/Linux/MakeDistribution.sh`).
- **Archive storage**: an **Artifactory** repository (not the upstream Backblaze path — see §2).
- **Container registry**: a **private / self-hosted** registry.

---

## 2. What already exists — and what is *not* yours

A crucial distinction discovered while mapping the repo: several build/CI assets are **inherited from the
upstream CARLA maintainers' infrastructure** and do **not** apply here.

**Upstream plumbing (reference only — do not build on it):**

- `.github/workflows/_ci-ubuntu.yml` — targets `runs-on: self-hosted:ubuntu-gpu`, a container image
  `carlasim/carla-builder:ue5-22.04`, and a **prebuilt engine volume-mounted from a Jenkins host**
  (`/home/jenkins/unreal-engine/ue5`, `_ci-ubuntu.yml:43-48`). This is the original maintainers' Jenkins
  farm. **You have no Jenkins system and this runner label does not exist for you.**
- `Util/Tools/Deploy.sh` — uploads to **Backblaze B2** ("B2" = Backblaze's S3-compatible object store) at
  `s3.us-east-005.backblazeb2.com` using `AWS_ACCESS_KEY_ID`/`AWS_ACCESS_KEY` (`Deploy.sh:10-11,57-58`).
  **You have no AWS/B2 credentials; this is not your storage.** It is also brittle (hardcoded
  `Carla-0.10.0-Linux-Shipping.tar.gz`) and uploads the legacy `package` output, not the digital-twin
  `Build/Dist/*` bundle.
- `.github/workflows/ue5_dev.yml` / `ue5_pr.yml` — thin wrappers that call `_ci-ubuntu.yml`; both triggers
  are commented out (dispatch-only).

**Your own, reusable assets (the real foundation):**

| Asset | Role |
|-------|------|
| `Util/Docker/Base.alma8.Dockerfile` | RHEL8-compatible **build environment** image (prerequisites only — builds nothing) |
| `Util/Docker/run.alma8.sh` | Runs that image with SSH key, `--non-root`, bind-mounts, CPU cap |
| `CarlaSetup.sh` | Provision content + SUMO + Cesium + configure/build CARLA against a **pre-existing** engine |
| `Scripts/Linux/BuildCarla.sh` | Editor (C++) + CarlaNet wheel |
| `cmake --build Build --target package-development` | Cook + stage via `RunUAT BuildCookRun` (`Unreal/CMakeLists.txt:380-498`) |
| `Scripts/Linux/MakeDistribution.sh` | Assemble the self-contained `Build/Dist/Carla-<ver>-Linux-<cfg>.tar.gz` bundle |

**Bottom line:** the *local build path is complete and working*; what's missing is (a) an engine artifact
strategy and (b) the GitHub Actions + Artifactory glue. Nothing here needs the upstream Jenkins/B2 plumbing.

---

## 3. Architecture: two independent pipelines

The build cleanly separates into a slow, rarely-changing part and a fast, every-push part. The whole point
is to keep the engine build *out of* the per-push hot path.

```
   ENGINE PIPELINE  (rare: only on carla-port UE fork bumps)          CARLA PIPELINE  (every push to ue5-dev)
   ───────────────────────────────────────────────────              ───────────────────────────────────────
   build UnrealEngine (carla-port) from source                       checkout carla @ ue5-dev
        │  ~1 hr, 225 GB, one time                                        │
        ▼                                                                 ▼
   RunUAT BuildGraph InstalledEngineBuild  (build-capable)           CarlaSetup.sh --skip-prerequisites
        │  → compact, portable, engineer-usable engine                   │  (content + SUMO + cesium, using the
        ▼                                                                 │   on-disk engine)
   ┌─────────────────┬──────────────────────────┐                        ▼
   ▼                 ▼                                                BuildCarla.sh  → editor + carlanet wheel
   Artifactory       carla-engine:<enginehash>                            │
   (standalone       image → private registry                            ▼
    .tar.gz)         (engine baked in, for                           cmake --build Build --target package-development
                      containerized builds elsewhere)                     │  (cook + stage)
                                                                          ▼
   The persistent build box keeps the                                MakeDistribution.sh
   Installed Build ON DISK; the CARLA                                     │  → Build/Dist/Carla-0.10.0-Linux-Development.tar.gz
   pipeline points CARLA_UNREAL_ENGINE_PATH at it.                        ▼
                                                                     publish → Artifactory  (or fallback: volume-mounted dir)
```

Key insight from the planning conversation: a **formal Installed Engine Build is what makes the image
approach sane.** Baking the 225 GB *source* tree into an image is absurd; baking a compact Installed Build
is reasonable. So the two engine deliverables you wanted are not competing options — **A4 (Installed Build)
feeds A2 (image):** build the Installed Build once, ship it as an archive *and* bake the same artifact into
the image.

On the persistent box, the per-push CARLA pipeline doesn't even need the image — it points at the Installed
Build already on local disk. The image exists for the *other* consumer you named: containerized builds on
machines that aren't this box.

---

## 4. Part A — the Unreal Engine Installed Build (the hard, rare part)

### 4.1 What an "Installed Engine Build" is

Unreal ships a BuildGraph script, `Engine/Build/InstalledEngineBuild.xml`, that produces an *installed*
engine equivalent to what the Epic Launcher hands you — relocatable, without the full engine source, but
able to build **game/project C++ and plugins** (and project editor targets). Canonical invocation
(Linux, run from the engine root):

```sh
Engine/Build/BatchFiles/RunUAT.sh BuildGraph \
  -Script=Engine/Build/InstalledEngineBuild.xml \
  -Target="Make Installed Build Linux" \
  -set:HostPlatformOnly=true \
  -set:WithLinux=true \
  -set:WithDDC=true
# output lands under: LocalBuilds/Engine/Linux
```

That `LocalBuilds/Engine/Linux` tree is the artifact: tar it for Artifactory, and/or `COPY` it into the
image.

### 4.2 The highest-risk item: "build-capable" is not free

You chose a **build-capable** engine — it must compile the CARLA editor **and** cesium-native, not merely
run/cook games. This is the part that needs a validation spike before anything else, because CARLA reaches
*into the engine's internals* in ways a stock Installed Build may strip:

- `CarlaSetup.sh` builds **cesium-native** with the engine's **bundled clang toolchain** and **libc++**,
  resolved under
  `Engine/Extras/ThirdPartyNotUE/SDKs/HostLinux/Linux_x64/<ver>/x86_64-unknown-linux-gnu`
  (`CarlaSetup.sh:322-346`; env `UNREAL_ENGINE_COMPILER_DIR`, `UNREAL_ENGINE_LIBCXX_DIR`).
- `Scripts/Linux/BuildCarla.sh` builds the **`CarlaUnrealEditor` target** via the engine's `Build.sh`
  (`BuildCarla.sh:194-200`) and writes `Engine/Saved/UnrealBuildTool/BuildConfiguration.xml` into the engine
  tree to disable UBA (`:97-115`).
- The `package-*` targets invoke the engine's `RunUAT` / `Build.sh` (`Unreal/CMakeLists.txt:205-223`).

**Open question to resolve first:** does `Make Installed Build Linux` include
`Engine/Extras/ThirdPartyNotUE/SDKs/...` (the clang/libc++ toolchain) and permit writing
`Engine/Saved/...`? Installed builds are game-developer oriented and historically prune the bundled host
toolchain SDKs. Mitigations, in order of preference:

1. Tune the BuildGraph `-set:` options / a small fork overlay so the SDK/toolchain dirs are retained.
2. **Overlay** the toolchain onto the Installed Build after the fact — copy
   `Engine/Extras/ThirdPartyNotUE/SDKs` (and, if needed, whatever `BuildCarla.sh`/cesium touch) from the
   source engine into the installed tree. Cheap, robust, decouples you from BuildGraph internals.
3. If the editor target or cesium build fundamentally won't work against an installed engine, fall back to
   shipping the **full from-source engine tree** as the build-capable artifact (bigger, but guaranteed),
   and reserve the Installed Build purely as the lean *runtime/packaging* deliverable.

**Recommendation — default to the from-source tarball; treat the Installed Build as an optimization.**
The requirement is firm: the engine archive must be able to *build* CARLA. The **guaranteed** build-capable
artifact is simply a tarball of the **from-source engine tree** — the exact engine used for development
today, so we already know it compiles CARLA. A formal `Make Installed Build` is a *smaller, cleaner
deliverable*, but whether it retains the toolchain above is unproven. So: ship the from-source tarball
first (safe), and run the Installed-Build spike (below) as a later size/cleanliness optimization, adopting
it only if a probe CARLA build succeeds against it (with the §4.2 overlay mitigation if the toolchain is
missing).

Note on `BuildCarla.sh`: it does **not** build the engine — it compiles the `CarlaUnrealEditor` *project*
target via the engine's already-present `Engine/Build/BatchFiles/Linux/Build.sh` (`BuildCarla.sh:129`) plus
the CarlaNet wheel. The engine (`Setup.sh` → `GenerateProjectFiles.sh` → `make`) is a **separate,
one-time prerequisite build** (`Docs/build_container_rhel8.md:82-93`). Fresh-checkout order:
engine build → `CarlaSetup.sh --skip-prerequisites` (first CARLA build) → `MakeDistribution.sh --build`.

The Installed-Build spike (build an Installed Build, then run `CarlaSetup.sh --skip-prerequisites` +
`BuildCarla.sh` + `package-development` against it) is **the go/no-go for adopting the Installed Build**
over the from-source tarball.

### 4.3 Two deliverables from one artifact

- **Standalone archive** → Artifactory. A `tar.gz` (or `.tar.zst`) of `LocalBuilds/Engine/Linux`, named by
  engine version + fork commit (e.g. `UnrealEngine-5.7.4-carla-port-<shorthash>-Linux.tar.gz`). Directly
  usable by an engineer: extract, point `CARLA_UNREAL_ENGINE_PATH` at it.
- **`carla-engine` image** → private registry. `FROM carla-base:alma8` + `COPY` the Installed Build into a
  fixed path + `ENV CARLA_UNREAL_ENGINE_PATH=...`. Tag by the same engine commit hash. This is the
  "containerized build system" consumer. Expect a **large image (~tens of GB)** — acceptable per the
  planning decision; a private/self-hosted registry avoids external storage limits.

### 4.4 Engine build workflow (rare)

A `workflow_dispatch` workflow (optionally also triggered when the UE fork submodule/pin bumps), on the
self-hosted box:

1. Clone/refresh `git@github.com:sbrett9/UnrealEngine.git` branch `carla-port`; `Setup.sh` +
   `GenerateProjectFiles.sh` + `make` (the one-time source build — `Docs/build_container_rhel8.md:82-93`).
2. `RunUAT BuildGraph ... Make Installed Build Linux` (§4.1).
3. Toolchain-inclusion validation (§4.2) — hard-fail if a probe CARLA build can't compile against it.
4. Publish: tar → Artifactory (§6); build + push `carla-engine:<hash>` to the private registry.

This runs **rarely** — only when engine-side CARLA code changes (e.g. the planned instance/semantic
segmentation work). Everyday CARLA pushes never trigger it.

### 4.5 Pinning

The CARLA pipeline must record *which* engine it built against. `Package/CreateCarlaVersionFile.cmake:25`
already stamps the engine git hash into the package `VERSION` file — reuse that. Pin the CARLA pipeline to a
specific `carla-engine` tag / Installed-Build hash so a distribution is always reproducible.

---

## 5. Part B — the per-push CARLA distribution pipeline (the easy, frequent part)

Every push to `ue5-dev`, on the persistent box, using the on-disk Installed Build. The chain already exists;
it needs wiring + one script generalization.

### 5.1 The build chain

```sh
export CARLA_UNREAL_ENGINE_PATH=/opt/ue/installed-build   # the on-disk Installed Build
./CarlaSetup.sh --skip-prerequisites \
    --content-ssh-key="$RUNNER_SSH_KEY" --vibeue-ssh-key="$RUNNER_SSH_KEY"
./Scripts/Linux/MakeDistribution.sh --build --config Development
#   └─ runs BuildCarla.sh + `cmake --build Build --target package-development` + assembles Build/Dist/*.tar.gz
```

`MakeDistribution.sh --build` already chains BuildCarla + the cook and assembles the bundle (cooked server +
carlanet wheel + SCTMV + OSM maps + SUMO netconvert) — `Scripts/Linux/MakeDistribution.sh:53-65,83-208`.
Output: `Build/Dist/Carla-0.10.0-Linux-Development.tar.gz`.

### 5.2 Private repo access (net-new for CI)

The digital-twin build needs SSH to three private repos: `sbrett9/carla-content`, `sbrett9/VibeUE`, and the
engine fork. Provide a deploy key as a GitHub Actions **secret**, materialized at runtime (the way
`run.alma8.sh:133-138,197-201` already installs `/tmp/id_key` → `~/.ssh/id_ed25519` at 0600). Pass it to
`CarlaSetup.sh` via `--content-ssh-key` / `--vibeue-ssh-key` (`CarlaSetup.sh:136`, `:260-292`). Note the
upstream `_ci-ubuntu.yml` sidesteps this by cloning *public* bitbucket content — not an option for the
private mirror + VibeUE.

### 5.3 The non-root cook gotcha

`UnrealEditor-Cmd` **refuses to run as root** during the cook (`Docs/build_container_rhel8.md:126-160`). If
the CARLA job runs inside the alma8 container, it must run as a non-root UID that owns the workspace —
exactly what `run.alma8.sh --non-root` sets up (`--userns=keep-id` rootless / `--user <owner>` rootful,
`HOME=/tmp/ue-home`). The self-hosted-runner container invocation must replicate this. If the job runs
*directly on the box* (runner user is already non-root), this is moot.

### 5.4 GPU: needed for the cook? (validate)

The upstream CI packages on a GPU runner, but **cooking is a headless commandlet and typically does not need
a GPU** (`-nullrhi`/offscreen). A GPU is only strictly required to *run/smoke-test* the server
(`Docs/build_container_rhel8.md:15-17,179-180`). Worth confirming on your box: if the cook runs GPU-less,
the per-push pipeline needs no GPU, and you can reserve the GPU for an optional post-build smoke test
(`Scripts/Linux/RunCarlaServer.sh` + a short SCTMV run). **Validation spike, not an assumption.**

### 5.5 Per-push workflow shape

```yaml
name: CARLA Dist (ue5-dev)
on:
  push: { branches: [ue5-dev] }
concurrency: { group: carla-dist-ue5-dev, cancel-in-progress: true }
jobs:
  build:
    runs-on: [self-hosted, linux, carla-build-box]   # your persistent box's labels
    env:
      CARLA_UNREAL_ENGINE_PATH: /opt/ue/installed-build
    steps:
      - uses: actions/checkout@v4
      - name: Install runner SSH key         # from secrets.CARLA_DEPLOY_KEY → 0600
      - name: Setup + build + package         # CarlaSetup.sh + MakeDistribution.sh --build
      - name: Publish to Artifactory          # §6, with disk-fallback
```

---

## 6. Publishing to Artifactory (your storage)

Design principle: **credentials are supplied at runtime, never baked into an image.** A secret in a Docker
image layer is recoverable by anyone who can pull the image. So `Base.alma8.Dockerfile` should **not** gain
credential build-args. Instead:

- The runner provides Artifactory creds as GitHub Actions **secrets** → env vars in the job/container:
  `ARTIFACTORY_URL`, `ARTIFACTORY_REPO`, `ARTIFACTORY_USER`, `ARTIFACTORY_TOKEN` (an API token/identity
  token, not a password).
- A small publish step uploads the bundle with **`curl`** (already in the image, and a known-good approach
  in this environment; the JFrog CLI `jf` may not be retrievable through the corporate firewall):
  `curl -u "$ARTIFACTORY_USER:$ARTIFACTORY_TOKEN" -T <file>
  "$ARTIFACTORY_URL/$ARTIFACTORY_REPO/carla/linux/<name>.tar.gz"`. If Artifactory sits behind the corporate
  CA, this `curl` needs that CA trusted (see §7's CA-injection bullet) — otherwise it falls back to `-k`.
- **Do NOT put credentials in `Base.alma8.Dockerfile` build-args.** A build-arg secret is baked into the
  image layer history and recoverable by anyone who pulls the image, and it forces an image rebuild to
  rotate creds. Credentials are supplied at container **run time** (env from GitHub Actions secrets, or
  a config the runner already carries), never at image build time.
- **Fallback (your stated preference):** if the creds env vars are absent, the publish step writes the
  bundle to a **volume-mounted output directory** the runner provides (e.g. `--output-dir /mnt/artifacts`),
  and logs that it skipped upload. This keeps the pipeline green on a box that hasn't been given credentials
  yet. (The exact Artifactory repo path, auth-token type, and URL are TBD — you'll provide them later.)

A generalized `Publish.sh` (taking `--file`, `--repo-path`, and honoring the env creds with the disk
fallback) is the clean home for this — a small, credential-agnostic replacement for the upstream, B2-bound
`Deploy.sh`. Don't extend `Deploy.sh`; it's hardwired to Backblaze and the legacy package name.

---

## 7. The persistent build box

- **Install the GitHub Actions self-hosted runner** (`Settings → Actions → Runners`) on the box, with clear
  labels (e.g. `self-hosted, linux, carla-build-box`). The workflows target those labels.
- **Sizing** (per the docs): ≥ 250 GB free disk for engine + CARLA + package, 32 GB RAM, 8+ cores. Keep the
  Installed Build on fast local disk; keep CARLA checkouts local too (never on `/mnt/c`-style slow mounts —
  `Docs/build_container_rhel8.md:26-30`).
- **Security note for self-hosted runners:** a self-hosted runner will execute whatever workflow code a
  triggering event carries. **Do not enable it for `pull_request` from forks** — that would run untrusted
  code on your box with your Artifactory/registry credentials in scope. Restrict to `push` on `ue5-dev`
  (and `workflow_dispatch`). This is why the per-push design here deliberately omits a PR trigger.
- **Runner on the host; build inside the container against bind-mounted source (decided model).** The
  GitHub Actions runner agent runs on the **host** (which trusts the corporate CA and can reach github.com).
  The host step does `actions/checkout` of **carla**, then shells out to a **non-interactive container
  invocation** (a CI variant of `run.alma8.sh --carla-dir/--engine-dir`, its **bind-mount** mode —
  `run.alma8.sh:114-129`) that builds in the alma8 container against the host checkout mounted at the same
  path. This deliberately avoids GH Actions' native `container:` key (awkward with podman) and keeps full
  control of: the volume mounts, the non-root UID mapping that works around UBA-can't-run-as-root
  (`run.alma8.sh:148-162`), GPU flags (if ever needed), and CA injection (next bullet). The built
  distribution lands on a host-mounted volume, then the host step publishes it (§6).
  - **Only carla is re-checked-out per push; the engine is a persistent on-disk input.** The built engine
    (§4) lives on the box and is bind-mounted in; the per-push pipeline never re-clones or rebuilds it.
  - **Host checkout does NOT remove the container's CA need.** It only covers the two top-level repos. Inside
    the container, `CarlaSetup.sh` still fetches over **HTTPS** (cesium-unreal + submodules, SUMO,
    vcpkg/ezvcpkg deps, and the engine `Setup.sh` toolchain download) — all of which hit the corporate CA.
    The private content/VibeUE clones use **SSH** (`git@…`), which is unaffected by the CA and only needs
    the mounted key. So the CA-injection below is required regardless of where the top-level checkout runs.
- **Corporate CA / TLS — the environment's biggest CI constraint.** The build host sits behind a corporate
  firewall with a **self-signed root CA** that the stock RHEL8 container does **not** trust — which is why
  the image today is built with `INSECURE_SSL=1` (`Base.alma8.Dockerfile:22-31`). Disabling TLS verification
  is a blunt instrument that also has to be repeated for every in-container HTTPS user (git clones,
  `Setup.sh` downloads, the Artifactory `curl` upload). **Prefer injecting the corporate CA at runtime**
  instead: mount the CA PEM into the container and register it —
  ```sh
  cp corp-root-ca.pem /etc/pki/ca-trust/source/anchors/ && update-ca-trust extract
  ```
  This makes `git`, `curl` (including the Artifactory upload), `wget`, and `dotnet` all verify correctly —
  more secure than `-k`, and it fixes the upload's TLS too. **Caveat:** `pip` uses its own bundled
  `certifi`, so pip needs `PIP_CERT`/`REQUESTS_CA_BUNDLE` pointed at the same CA separately. Recommended
  change: add an optional CA-mount to `run.alma8.sh` (and the CI wrapper) so `INSECURE_SSL=1` becomes a
  fallback, not the default.

---

## 8. Gaps / net-new work

| # | Item | Where | Effort | Risk |
|---|------|-------|--------|------|
| 1 | Prove a build-capable Installed Build compiles CARLA + cesium-native | §4.2 spike | Med | **High** — go/no-go for Part A |
| 2 | Engine build workflow (source build → Installed Build → publish) | new `engine-build.yml` | Med | Med |
| 3 | `carla-engine` image + push to private registry | new `Dockerfile` + workflow step | Low | Low |
| 4 | Per-push CARLA dist workflow | new `carla-dist.yml` | Low | Low |
| 5 | Credential-agnostic `Publish.sh` (Artifactory + disk fallback) | new script | Low | Low |
| 6 | Wire private-repo deploy key as a runner secret | workflow + `CarlaSetup.sh` flags | Low | Low |
| 7 | Confirm cook runs GPU-less; optional post-build smoke test | §5.4 spike | Low | Low |
| 8 | Self-hosted runner install + labels + disk sizing on the box | box setup | Low | Low |

Everything from `CarlaSetup.sh` through `MakeDistribution.sh` already works — items 4–6 are mostly glue.
Item 1 is the only genuine unknown.

---

## 9. Recommended phased rollout

**Phase 1 — engine foundation (do the risky part first).**
Manually build the source engine on the box; produce a `Make Installed Build Linux`; run the §4.2 validation
(CarlaSetup + BuildCarla + `package-development` against it). If it fails, apply the §4.2 mitigation ladder.
Outcome: a known-good, on-disk, build-capable engine and a clear answer on the Installed-Build strategy.

**Phase 2 — automate the CARLA distribution (delivers the actual goal).**
Install the self-hosted runner. Add `carla-dist.yml` (push → `ue5-dev`) + `Publish.sh` (Artifactory with
disk fallback) + the deploy-key secret. This is the "push to dev → distribution archive" outcome you asked
for, using the Phase-1 engine on disk. No image needed yet.

**Phase 3 — package the engine as deliverables.**
Add `engine-build.yml` to automate the source-build → Installed-Build → **Artifactory archive** +
**`carla-engine` image → private registry**, triggered rarely (dispatch / engine bump). This gives you the
standalone engineer deliverable and the containerized-build image, and lets other machines run the CARLA
build without a from-source engine.

This ordering front-loads the only high-risk unknown (Phase 1), delivers the user-visible goal early
(Phase 2), and treats the heavier engine-packaging work (Phase 3) as an enhancement rather than a blocker.

---

## 10. Open questions — status

1. **Installed Build toolchain inclusion** (§4.2) — **still open; the one real unknown.** *Resolved
   direction:* the engine archive must be build-capable, so default to the **from-source engine tarball**
   (guaranteed) and validate the leaner formal Installed Build as a later optimization spike.
2. **GPU for cook** (§5.4) — **effectively resolved.** The box has a GPU, but the cook has never been
   GPU-driven; plan is GPU-less cook, GPU reserved for an optional post-build server smoke test. (GPU
   passthrough is a runtime podman flag, not something `Base.alma8.Dockerfile` provides — add to the
   container invocation only if the smoke test is wired in.) Quick empirical confirmation still worthwhile.
3. **Artifactory specifics** — **deferred (details later).** Decided: **`curl`** upload with credentials at
   container **run time** (not Dockerfile build-args); disk-fallback when creds absent. Repo path / token
   type / URL to be supplied later.
4. **Container run mode** (§7) — **resolved.** The build runs **inside the alma8 container**; the GH Actions
   runner lives on the **host** and a step shells into the container (CI variant of `run.alma8.sh`), which
   preserves volume mounts + non-root UID + CA injection. The end-user later runs the *CARLA server* on
   **bare metal**, not in a container.
5. **Corporate CA / TLS** (§7) — **new, high-impact.** Host is behind a corporate firewall with a
   self-signed CA the RHEL8 container doesn't trust (today handled by `INSECURE_SSL=1`). Recommended:
   inject the CA into the container trust store at runtime (`update-ca-trust`) so git/curl/dotnet verify
   correctly; `pip`'s `certifi` needs `PIP_CERT`/`REQUESTS_CA_BUNDLE` separately. Make `INSECURE_SSL=1` a
   fallback, not the default.
