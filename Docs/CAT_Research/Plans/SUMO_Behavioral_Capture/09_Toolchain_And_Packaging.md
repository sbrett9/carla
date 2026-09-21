# 09 — Toolchain and Packaging

**Status:** planning only, scoped to `_TEAM_BRIEF.md` §3a. No code changed, no build/cook/engine run.
Every claim below is either cited to `path:line` in the tree as it stands today, or marked as measured
with the command that produced it, or explicitly labelled an inference.
**Date:** 2026-09-18.
**Owner role:** build, toolchain and packaging engineer. This document only.
**Reads:** [`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) (binding, especially §3a — the added time-of-day and
operator-control-surface requirement); `Findings/23_SUMO_Traffic_Integration.md` §1, 1.2, 6.1–6.3, 6.12,
§8; `Findings/15_Automated_Build_Distribution_Pipeline.md`; `Findings/07_RoadNetwork_Filtering.md` §1.3;
`Findings/20_Behavioral_Annotation_And_Areas_Of_Interest.md` §5.6, §6.2;
`Findings/22_Digital_Twin_Feature_Port.md` §14; `.github/workflows/build-carla-ue5.yml` and
`Util/Docker/Base.alma8.Dockerfile` (the CI pipeline as it actually runs today, not as doc 15 proposed
it); [`07_Scenario_Authoring.md`](07_Scenario_Authoring.md) §2.8–2.9, §8, §9.4 (the one dependency it
hands to this document); [`11_Time_And_Illumination.md`](11_Time_And_Illumination.md) (ground truth and
its own §0 ownership boundary); [`12_Operator_Control_Surface.md`](12_Operator_Control_Surface.md) §3.9,
§10 (what it has already decided must ship — read and treated as fact here, not redesigned).
**Does not cover:** the co-simulation runtime shape (`03`), the render-set / lifecycle / tick contracts
(`04`, `05`), truth and annotation schema (`06`), scenario-authoring content (`07`), what a civil time
means or why one illumination policy is chosen over another
([`11_Time_And_Illumination.md`](11_Time_And_Illumination.md)), the operator's flags, layering and
validation ([`12_Operator_Control_Surface.md`](12_Operator_Control_Surface.md)), EPoL interfaces (`08`),
or scale and performance (`10`). Where a decision here depends on one of those, it is named and linked
rather than made silently. The vehicle catalogue, annotation vocabulary, illumination reference and
authoring skill are designed elsewhere; this section only specifies what has to be true of them for them
to *ship*. Likewise the run-configuration schema and the site profile are `12`'s design; this section
specifies only their packaging shape.

**Change history**

| Date | Change |
|---|---|
| 2026-09-17 | Initial: SUMO build-target guard, `SUMO_HOME` collision, C# binding design, licensing, distribution gaps. |
| 2026-09-18 | Added time-of-day and operator-control-surface packaging scope (§2.4, §4.4, §5.6); corrected three drifted citations. |


**Change history**

| Revision | Change |
|---|---|
| 1 · 2026-09-18 | First draft. |
| 2 · 2026-09-21 | One distribution, no packaging mode, generated licence manifest; `CarlaSetup.bat` retired; skill moves into the repo. |

---

## 1. What is actually on disk today, against doc 23's table

Doc 23 §1/§1.2 was measured 2026-08-21. Re-measured today against the same four directories
(`Build/sumo-src`, `Build/SUMOLibraries`, `Build/sumo-build`, `Build/sumo-install/bin`):

| Thing | Doc 23 said | Measured today | Drift |
|---|---|---|---|
| `Build/sumo-src` | complete, v1.27.0 pinned `e238ea04b7` | confirmed: `git -C Build/sumo-src rev-parse HEAD` pin matches `CarlaSetup.ps1:616`'s `$sumoSrcPin` exactly; `bin/` holds `sumo.exe` (6.9 MB), `duarouter.exe` (2.6 MB), `netconvert.exe`, `libtracics.dll` + `.lib`/`.exp`, `libtracics-sources.zip`, `libsumostatic.lib`, `libtracistatic.lib` | none |
| `Build/SUMOLibraries` | pinned tag `1.27.0` | confirmed present, matches `CarlaSetup.ps1:617-618` pins | none |
| `Build/sumo-build` | fully configured — `sumo.vcxproj`, `duarouter.vcxproj`, `libtracics.vcxproj`, `libsumocs.vcxproj` all generated | confirmed all four exist: `src/sumo.vcxproj`, `src/duarouter/duarouter.vcxproj`, `src/libtraci/libtracics.vcxproj`, `src/libsumo/libsumocs.vcxproj` | none |
| `Build/sumo-install/bin` | **`netconvert.exe` only** staged; `sumo`/`duarouter`/`libtracics` exist in the source tree's `bin/` but are not staged | confirmed identical: `netconvert.exe` + 32 DLLs + `share/proj`; no `sumo.exe`, `duarouter.exe`, or `libtracics.dll` in `sumo-install/bin` | none |
| Generated C# bindings | **94 generated C# files** in `Eclipse.Sumo.Libtraci/` | **93 `.cs` files.** The 94th file in that directory is `libtraciCSHARP_wrap.cxx` — the SWIG-generated **C++** glue that gets compiled into the native `libtracics.dll`, not a C# source file. Confirmed by unzipping `Build/sumo-src/bin/libtracics-sources.zip` (95 zip entries = 1 directory entry + 93 `.cs` + 1 `.cxx`) and by `find … -iname '*.cs' \| wc -l` = 93 | doc 23 off by one; harmless, but the `.cxx` must not be swept into the C# wrapper project (§4) |
| `data/`, `tools/` staged | present in source, **unstaged** | confirmed: `Build/sumo-src/data` and `Build/sumo-src/tools` exist and are populated (type maps, XSDs, `traci`/`sumolib`); nothing under `Build/sumo-install` or `Build/Dist/*/tools/sumo` | none |
| Distribution slot | `MakeDistribution.ps1:237` creates `tools\sumo\` | the directory list is now at `Scripts/Windows/MakeDistribution.ps1:211`; the actual SUMO copy block is `:248-260`. Contents of the built `Build/Dist/Carla-0.10.0-Win64-Development/tools/sumo/`: `netconvert.exe` + its DLLs + a `proj/` subfolder (**PROJ's own data**, not SUMO's `data/typemap/`). No `sumo.exe`, no `duarouter.exe`, no `libtracics.dll`, no SUMO `data/`, no SUMO `tools/` | line-number drift only (script has grown since 2026-08-21); the packaging gap doc 23 described is confirmed unchanged |
| `SUMO_HOME` | "set nowhere" | **Wrong as a blanket statement — see §3.3.** It is set nowhere *by any script in this repo*, but it exists as an ambient developer environment variable on this machine, pointed at an entirely separate SUMO installation | material correction, not cosmetic |

One additional disk fact doc 23 does not mention: **`Build/sumo-src/share/proj` also holds a complete
PROJ data set** (16 entries, identical to `Build/sumo-install/share/proj`). No script in this repo
writes there — `CarlaSetup.ps1:687-691` only writes `Build/sumo-install/share/proj`. This is most likely
a side effect of SUMO's own CMake build copying PROJ data next to its test-suite binaries when `sumo`/
`duarouter` were built 2026-08-21 (**inferred, not confirmed** — SUMO's build system, not ours, would
have done this). It matters because `carlacontrol.SumoInstallation` (§3) resolves `Build/sumo-src` as a
candidate installation and would find working PROJ data there too, **by accident**, not by any staging
this repo performs. Do not rely on it for the distribution path (§5) — a distribution recipient has no
`Build/sumo-src`.

**Net conclusion: doc 23's disk-state table holds.** The only correction of substance is `SUMO_HOME`
(§3.3), which changes the shape of §3 below but not §1's inventory.

**Every disk-state claim in the table above, and every `path:line` citation anywhere in this document, is
checked against the live tree as of 2026-09-18.** Two citations sit on lines that moved because the
tracked files themselves grew: `build-carla-ue5.yml`'s `CONTAINER_IMAGE` is at `:51` (§2.3), and
`Util/Docker/Base.alma8.Dockerfile`'s `powertools` repo enablement is at `:94` (§2.3) — both are cited at
those lines wherever they appear below. `Findings/22_Digital_Twin_Feature_Port.md`'s `CarlaControl/` row
(§4.3) is at `:529`; the netconvert row two lines above it, quoted in full in §6, is at `:528`. Every other citation in this document — `CarlaSetup.ps1`/`.sh`/`.bat`, `run_SCTMV.py`,
`Scripts/Windows/MakeDistribution.ps1`, `Scripts/Linux/MakeDistribution.sh`,
`CarlaControl/src/carlacontrol/SumoInstallation.py`, `OsmClipper.py:154,219`,
`Unreal/Package/CreateCarlaVersionFile.cmake`, the CarlaNet `.csproj` files, and the
`CarlaServer.cpp:611-661` / `carlanet/__init__.py` solar citations in §2.4 — lands on the exact line
cited.

**The build and staging pipeline, source pin to staged install to distribution bundle** — current state
(solid) and what §2/§5 add (dashed):

```mermaid
flowchart TD
    P1["SUMO source pin\ngit clone + checkout e238ea04b7\n(CarlaSetup.ps1/.sh)"] --> CFG
    P2["SUMOLibraries pin 1.27.0\n(Windows only — SUMO_LIBRARIES)"] -.-> CFG
    P3["system libxerces-c / libproj\n(Linux only)"] -.-> CFG
    CFG["cmake configure\n(VS generator / Unix Makefiles)"] --> BUILD

    BUILD["cmake --build --target …\ntoday: netconvert only"]
    NEWTARGETS["NEW targets: sumo, duarouter, libtracics\n(§2 — same invocation, longer list)"]
    BUILD -.-> NEWTARGETS

    BUILD --> SRCBIN["Build/sumo-src/bin/\n(SUMO's own build convention)"]
    NEWTARGETS -.-> SRCBIN

    SRCBIN --> GUARD{"idempotence guard\n(§2 stateDiagram)"}
    GUARD --> STAGE["Copy-Item into\nBuild/sumo-install/{bin,share/proj}"]
    NEWSTAGE["NEW: also stage data/, tools/\n(§3)"]
    STAGE -.-> NEWSTAGE

    STAGE --> DIST["MakeDistribution.ps1/.sh\ntools\\sumo\\ (netconvert+DLLs+proj today)"]
    NEWSTAGE -.-> DISTFULL["NEW: + sumo, duarouter, libtracics,\ndata/, tools/, SUMO_HOME in launcher\n(§5)"]
    DIST -.-> DISTFULL
    DISTFULL --> BUNDLE["Distribution tarball / zip"]
```

---

## 2. The build target change

### 2.1 What changes, and why the guard has to change with it

`CarlaSetup.ps1:677` builds exactly one target:

```powershell
cmake --build $sumoBuild --target netconvert --config Release -- -m
```

and the idempotence guard at `CarlaSetup.ps1:632-636` is:

```powershell
$netconvert = Join-Path $sumoInstall 'bin\netconvert.exe'
if (Test-Path $netconvert) { Write-Host "Found SUMO netconvert ... Skipping SUMO build." }
```

`CarlaSetup.sh:261-264` does the platform-appropriate equivalent (`test -f "$sumo_install/bin/netconvert"`).
Doc 23 §6.1 already names the trap: keying the guard on `netconvert.exe` alone means a developer who
has that one file staged is told the SUMO step is done, even with `sumo`, `duarouter` and `libtracics`
never built.

**Do not replace the single-file check with a different single-file check.** Doc 23 §6.1 phrases the
fix as keying on "the newest required binary." That phrasing does not survive contact with how the
build actually runs: `-- -m` passes MSBuild `/m` (parallel project build), so on a multi-target build
there is no dependable single "newest" output file — parallel projects can finish in any order, and a
partial failure can leave an arbitrary subset staged. The guard has to check **presence of the whole
required set**, not the freshness of one member of it.

**The idempotence guard, current versus proposed:**

```mermaid
stateDiagram-v2
    state "CURRENT" as cur {
        [*] --> CheckNetconvert
        CheckNetconvert: Test-Path sumo-install/bin/netconvert.exe
        CheckNetconvert --> SkipBuild: exists
        CheckNetconvert --> BuildNetconvertOnly: missing
        SkipBuild --> HalfToolchain: sumo/duarouter/libtracics\nnever checked, never built
        BuildNetconvertOnly --> [*]
        HalfToolchain --> [*]
    }
    state "PROPOSED" as prop {
        [*] --> CheckAllRequired
        CheckAllRequired: Test-Path on ALL of\nnetconvert, sumo, duarouter, libtracics
        CheckAllRequired --> SkipBuild2: all present
        CheckAllRequired --> BuildFullSet: any missing
        BuildFullSet: cmake --build --target\nnetconvert sumo duarouter libtracics\n(CMake skips already-built objects —\nnot a full rebuild in practice)
        BuildFullSet --> StageAll: copy the whole required set\n+ data/ + tools/
        StageAll --> [*]
        SkipBuild2 --> [*]
    }
    cur --> prop: this document's change
```

Required set (built and staged together, one invocation): `netconvert`, `sumo`, `duarouter`,
`libtracics` (its native library — `.dll` on Windows, `.so` on Linux). `jtrrouter` and `polyconvert`
(doc 23 §6.1 lists them as optional) are **not** added to the default target list: nothing in this plan
or in `carlacontrol` invokes either of them today (verified — no reference to `jtrrouter` or
`polyconvert` anywhere in `CarlaControl/` or `CarlaNet/`), so building them by default only lengthens
every clean build for no consumer. Leave them reachable via the existing `-CleanAll`-style flag surface
if a future author needs them, rather than building them unconditionally.

`duarouter` was already going to be in this target list as a build-time convenience; the
scenario-authoring engineer's plan for `07_Scenario_Authoring.md` makes it **load-bearing**: route
validation for authored scenarios calls `duarouter` unconditionally as part of scenario compilation
(measured at 0.27 s for the 52 Arapahoe routes — cost is not a reason to make it optional). `duarouter`
therefore moves from "nice to have while we're building sumo anyway" to **a required, staged, verified
artifact**, and §8's acceptance check treats it that way. `07_Scenario_Authoring.md` §9.4 independently
reaches the same conclusion from the authoring side — see §3.5 below.

### 2.2 Windows (`CarlaSetup.ps1`)

```powershell
# Guard: ALL required binaries staged, not just netconvert.
$requiredBins = 'netconvert.exe','sumo.exe','duarouter.exe'
$requiredNative = 'libtracics.dll'
$haveAll = ($requiredBins | ForEach-Object { Test-Path (Join-Path $sumoInstall "bin\$_") }) -notcontains $false `
           -and (Test-Path (Join-Path $sumoInstall "bin\$requiredNative"))
if ($haveAll) {
    Write-Host "Found the full SUMO toolchain (netconvert, sumo, duarouter, libtracics) staged. Skipping SUMO build."
} else {
    ...
    cmake --build $sumoBuild --target netconvert sumo duarouter libtracics --config Release -- -m
    ...
    # Stage the whole required set, not just netconvert.exe.
    foreach ($bin in $requiredBins) {
        Copy-Item -Force (Join-Path $sumoSrc "bin\$bin") $installBin
    }
    Copy-Item -Force (Join-Path $sumoSrc "bin\$requiredNative") $installBin
    Copy-Item -Force (Join-Path $sumoSrc 'bin\*.dll') $installBin   # unchanged: runtime DLLs
    # NEW: stage data/ and tools/ beside bin/ (see §3).
    Copy-Item -Recurse -Force (Join-Path $sumoSrc 'data') (Join-Path $sumoInstall 'data')
    Copy-Item -Recurse -Force (Join-Path $sumoSrc 'tools') (Join-Path $sumoInstall 'tools')
}
```

`-Clean`/`-CleanAll` (`CarlaSetup.ps1:159-160`) already remove `Build\sumo-build` + `Build\sumo-install`
(and, for `-CleanAll`, the source and library pins too); nothing about their behavior needs to change —
they force exactly the fuller rebuild this section now performs by default when the guard fails. Needing
that rebuild is not a cost to weigh against anything; it is the guard doing its job.

### 2.3 Linux (`CarlaSetup.sh`)

Mirrors §2.2 exactly, same required set, same guard shape:

```sh
required_bins="netconvert sumo duarouter"
required_native="libtracics.so"
have_all=1
for b in $required_bins; do [ -f "$sumo_install/bin/$b" ] || have_all=0; done
[ -f "$sumo_install/bin/$required_native" ] || have_all=0

if [ "$have_all" -eq 1 ]; then
    echo "Found the full SUMO toolchain staged. Skipping SUMO build."
else
    ...
    cmake --build "$sumo_build" --target netconvert sumo duarouter libtracics -j"$(nproc)"
    mkdir -p "$sumo_install/bin"
    for b in $required_bins "$required_native"; do
        cp "$sumo_src/bin/$b" "$sumo_install/bin/$b"
    done
    # NEW: stage data/ and tools/.
    cp -a "$sumo_src/data" "$sumo_install/data"
    cp -a "$sumo_src/tools" "$sumo_install/tools"
fi
```

**Prerequisite gap found beyond doc 23's scope.** Doc 23 §6.1 says "Linux additionally needs `swig` in
`InstallPrerequisites.sh`." That is necessary but **not sufficient**, because it is the wrong file for
the path this repo's own CI actually uses:

- `Util/SetupUtils/InstallPrerequisites.sh:29-42` detects an **Ubuntu** version (`VERSION_ID` from
  `/etc/os-release`) and installs via `apt-get`. It already carries `libxerces-c-dev`, `libproj-dev`
  (`:70-71`) but no `swig`. This is the path a bare-metal Ubuntu developer box uses.
- The CI container is **AlmaLinux 8**, provisioned by `Util/Docker/Base.alma8.Dockerfile`, which
  `.github/workflows/build-carla-ue5.yml` pulls pre-built and never runs `InstallPrerequisites.sh`
  against at all — the CI job calls `CarlaSetup.sh --skip-prerequisites` (workflow step "Build CARLA
  distribution", inline script). The Dockerfile's own package block (`RUN dnf -y groupinstall
  "Development Tools" && dnf -y install … xerces-c-devel proj-devel …`) already has `xerces-c-devel`
  and `proj-devel` but **no `swig`** either.

Both places need it:

| Path | File | Package | Consumed by |
|---|---|---|---|
| Windows | — **no change needed** | `swig` already ships inside the pinned `SUMOLibraries` bundle as `Build/SUMOLibraries/swigwin-4.3.1/` (`CarlaSetup.ps1:617-618`) | any Windows developer |
| Bare-metal Ubuntu dev box | `Util/SetupUtils/InstallPrerequisites.sh` | `swig` (apt) | a developer running `CarlaSetup.sh` without `--skip-prerequisites` |
| CI container (AlmaLinux 8 / RHEL8-compatible) | `Util/Docker/Base.alma8.Dockerfile` | `swig` (dnf; ships in AlmaLinux 8's base or PowerTools repo, already enabled at `:94`) | `build-carla-ue5.yml`'s "Build CARLA distribution" step, which always runs `--skip-prerequisites` |

Missing the Dockerfile side would make the target-list change work for every developer laptop and fail
silently — or rather, fail loudly with a SWIG-not-found CMake error — the first time CI tries to build
`libtracics` from a clean `Build/` tree, which happens whenever `clean_build: true` is dispatched or the
runner's cache is ever cleared. Adding the package means rebuilding and re-pushing `carla-base:alma8` to
the private registry (`iasartifact.sncorp.com:8443/cat-docker-dev/carla-base:alma8`, per
`build-carla-ue5.yml:51`); that rebuild is not a cost, it is the fix.

### 2.4 What §3a — simulated time of day — does not change here

[`_TEAM_BRIEF.md`](_TEAM_BRIEF.md) §3a adds simulated time of day, driven with playback and toggleable
per run. Stated plainly, because a reader of a toolchain document should not have to infer it: **this
requirement changes nothing above, and nothing else this document is responsible for.**

*Read, directly, not carried from the brief's own citations:* the four solar RPCs are bound at
`Unreal/CarlaUnreal/Plugins/Carla/Source/Carla/Server/CarlaServer.cpp:611-661`
(`set_solar_time`, `set_solar_date`, `get_solar_state`, `set_time_advance`), calling into
`UCesiumHeightSampler` in `Unreal/CarlaUnreal/Plugins/CesiumCarlaBridge/Source/CesiumCarlaBridge/`, and
exposed by the Python shim at `carlanet/__init__.py:1500` (`set_solar_time`), `:1506` (`set_solar_date`),
`:1511` (`get_solar_state`), `:1535` (`set_time_advance`) — confirmed by grep directly against the live
file, matching the team brief's own citations exactly.

Three consequences follow, all measured rather than assumed:

- **`CesiumCarlaBridge` is an ordinary Unreal plugin, not a toolchain component.** It lives under
  `Unreal/CarlaUnreal/Plugins/CesiumCarlaBridge/` and is built by UnrealBuildTool as part of the same
  editor/server build every other CARLA plugin already goes through — confirmed by the presence of its
  own prior build output in the tree
  (`Unreal/CarlaUnreal/Plugins/CesiumCarlaBridge/Intermediate/Build/Win64/UnrealEditor/Inc/CesiumCarlaBridge/UHT/CesiumHeightSampler.gen.cpp`).
  It is not a CMake target, `CarlaSetup.ps1`/`.sh` do not stage it, and it ships inside the cooked server
  exactly the way the rest of the `Carla` and `CesiumForUnreal` plugins already do — §5.1's "Cooked
  server" row already covers it. There is no new distribution slot for it.
- **The SUMO toolchain has no part in it.** `netconvert`, `sumo`, `duarouter` and `libtracics` do not
  compute, store or read solar state anywhere. `01_Architecture.md`'s own design (`:170`, `:173`, `:439`,
  `D1.24`) confirms the split this document would otherwise have to assume: motion-derived vehicle
  lights (brake, indicator) come from SUMO's existing per-vehicle signal bitmask over the same `traci`
  subscription CarlaNet already needs for pose — an existing, already-compiled `libtracics` capability,
  not a new one — while illumination-derived lamps come from the published solar state, and a new C#
  component (`SumoSignalProjector`, `11`'s and `01`'s design, not this document's) composes the two.
  Nothing in that design proposes a change to SUMO's own build, and `CarlaNet/src/CarlaNet.Sumo` (§4.1,
  which does not exist yet in the tree — confirmed) needs nothing added to it for time-of-day.
- **Nothing in §6's licensing table changes.** No new SUMO artifact is redistributed on account of
  §3a, so no new redistribution boundary is crossed.

The one thing that *is* new on account of §3a, and is emphatically not a SUMO matter, is a small Python
dependency for civil-time handling on the scenario-authoring side — §4.4. The other new thing — the
operator control surface's distribution footprint — is not a toolchain matter either, but it is a
packaging one, and it is addressed at §5.6.

---

## 3. `SUMO_HOME`, the data directory, and a live version collision

### 3.1 Where `CARLA_NETCONVERT` / `PROJ_LIB` are actually set today (re-resolved, not carried from doc 23)

Doc 23 cited `SCTMV.py:102-106`. That file is gone (§0). Grepping the live tree for `CARLA_NETCONVERT`,
`PROJ_LIB`, `SUMO_HOME` finds these, and only these:

| File:lines | What it does |
|---|---|
| `CarlaControl/scripts/run_SCTMV.py:60-78` | The live entry point. Defaults `CARLA_NETCONVERT` to `Build/sumo-install/bin/netconvert{.exe}` and `PROJ_LIB`/`PROJ_DATA` to `Build/sumo-install/share/proj`, **but only if the env var isn't already set** (`os.environ.get(...) or ...`, then `setdefault`) |
| `Scripts/Windows/MakeDistribution.ps1:298-300` | The generated `run-sctmv.ps1` launcher sets `CARLA_NETCONVERT`/`PROJ_LIB` to the bundled `tools\sumo\` paths, for a distribution recipient who has no `Build\` tree at all |
| `Scripts/Linux/MakeDistribution.sh:194-195` | Same, for the generated `run-sctmv.sh` |
| `CarlaSetup.ps1:696-702` / `CarlaSetup.sh:281-287` / `CarlaSetup.bat:184-189` | Print-only reminders (`Write-Host`/`echo`) telling the developer what to set by hand. **These do not persist anything** — a fresh shell after running `CarlaSetup.ps1` still has no `CARLA_NETCONVERT` unless the developer copies the printed line into their profile, or unless `run_SCTMV.py`'s own default (above) covers it, which today it does |

All three setup scripts' comments point at a doc that does not exist: `# see NETCONVERT_INTEGRATION.md`
(`CarlaSetup.ps1:697`, `CarlaSetup.sh:282`, `CarlaSetup.bat:184`). `find . -iname NETCONVERT_INTEGRATION.md`
returns nothing anywhere in the tracked tree. This is pre-existing doc rot, unrelated to SUMO's expansion,
but it is the file a developer is told to go read for exactly the environment-variable contract this
section extends — worth fixing in the same change rather than adding a fourth reference to a file that
has never existed in this repo's history that we can find.

`SUMO_HOME` is set **nowhere in any of the above** — confirmed by the same grep. `carlacontrol.SumoInstallation`
(`CarlaControl/src/carlacontrol/SumoInstallation.py:1-108`) reads it (§3.2) but nothing writes it.

### 3.2 `SumoInstallation`'s discovery order, and what it resolves to right now

`SumoInstallation.locate` (`SumoInstallation.py:32-56`) tries, in order:

1. `explicit` — an argument the caller passed (every CLI here exposes `--sumo-home`, defaulting to `None`)
2. `os.environ.get("SUMO_HOME")`
3. `extra_candidates` — every caller (`make_arapahoe_scenario.py:56,277`, `make_bahonar_scenario.py:331`,
   `make_sumo_scenario.py:167`, `sumo_cot_telemetry.py:48,142`) passes exactly one:
   `REPO_SUMO = Build/sumo-src` (the **source tree**, not `Build/sumo-install` — it already has `bin/`,
   `data/`, `tools/` in one place, so it already resolves as a complete installation today, by accident
   of SUMO's own build/source layout, not by design)
4. `shutil.which("sumo")` / `shutil.which("netconvert")` on `PATH`

A directory qualifies (`_is_installation`, `:59-62`) if it has `bin/sumo{.exe}` **or** `bin/netconvert{.exe}`
— lenient by design, so a netconvert-only directory (today's `Build/sumo-install`) already counts as "an
installation" even though `.sumo`, `.tools`/`import_traci()` would raise on it. That is intentional
lenience for callers that only need `netconvert`, not a defect — but it means **the check that gates
"is this installation usable" is weaker than what most callers actually need**, and a caller that calls
`.sumo` or `import_traci()` first discovers the shortfall at that call site, not at `locate()` time.

### 3.3 A live version collision, measured on this machine

**Measured just now, on this machine, exactly as the scenario-authoring engineer reported:**

```
$ echo $SUMO_HOME
G:\Sumo\
$ ls $SUMO_HOME
bin  data  doc  include  tools
$ "$SUMO_HOME/bin/netconvert.exe" --version
Eclipse SUMO netconvert 1.27.1
 Build features: Windows-10.0.17763 AMD64 MSVC 19.29.30133.0 Release ...
```

against the repo's own staged/pinned build:

```
$ Build/sumo-install/bin/netconvert.exe --version
Eclipse SUMO netconvert 1.27.0
 Build features: Windows-10.0.26200 AMD64 MSVC 19.44.35227.0 Release ...
```

`SUMO_HOME=G:\Sumo\` is a **full, independent, official SUMO 1.27.1 installation** (it has `include/`,
which nothing in this repo's build produces — this was installed by some other means, on this machine,
unrelated to `CarlaSetup.ps1`). Under §3.2's order, `SUMO_HOME` is checked **before** `extra_candidates`,
and no caller passes `explicit`, so **every `SumoInstallation.locate()` call on this machine today
silently resolves to the external 1.27.1 install, never to this repo's pinned 1.27.0 build**, with zero
output saying which one it picked.

This is exactly the failure the coordinating message named: the CARLA world for a map is built by
`CarlaNet.Map.OsmConverter` via `CARLA_NETCONVERT` (§3.1 — resolves to the repo's pinned `sumo-install`
netconvert, **1.27.0**), while the SUMO network authored against that same map by `SumoScenarioBuilder`
/ the `make_*_scenario.py` tools resolves through `SumoInstallation` to **1.27.1**, on this machine,
today. Two different netconvert builds are producing what the team brief's ground truth (§5) asserts is
"the same clipped OSM at the same pinned origin" — the origin-pinning and flag set are identical, but
"same netconvert run" is not true when it is not even the same netconvert *version*. Doc 23's
`netOffset = 0.00,0.00` coordinate-identity measurement was taken with one specific netconvert build; it
is not a property guaranteed across versions, only re-verified for the one that happened to run.

**This has to become a reported, refusing condition, not something reordering hides.** Two designs were
considered:

- **Flip precedence so the repo-pinned build always wins by default.** Rejected as the default: it
  breaks the one SUMO-wide convention `SumoInstallation`'s own docstring names — `SUMO_HOME` is "what
  `traci` itself checks" (`SumoInstallation.py:9`) — and would make this repo's tooling resolve
  differently from raw `traci` run against the same environment, which is a worse kind of silent
  divergence than the one being fixed.
- **Keep the existing precedence, but make the resolution and any mismatch impossible to miss.**
  Recommended. Concretely:
  1. `SumoInstallation` gains a `.version` property: run the resolved `netconvert --version` once, parse
     the `Eclipse SUMO netconvert X.Y.Z` line, cache it.
  2. Every tool built on `SumoInstallation.locate()` logs the resolved `home` and `.version` at startup,
     unconditionally — not behind a verbose flag. Silence is what let this stand unnoticed.
  3. The world-build provenance record already carries netconvert's argument set (team brief §5, ground
     truth on `world.json`); it must also carry the **resolved netconvert version string** at the time
     the world was built. The field's home is `WorldPackageManifest` in
     `CarlaNet/src/CarlaNet.Map/WorldPackage/WorldPackage.cs:48-107`, beside the existing
     `NetconvertExtraArgs` — which records the arguments but not the converter. `04_Contracts.md` owns
     the schema; what I specify is that the *source* of that string is
     `OsmConverter`'s own invocation of the netconvert it just ran, recorded at build time, not inferred
     later.
  4. A scenario-authoring tool that opens an existing world package reads that recorded version and
     compares it against its own resolved `SumoInstallation.version`. On mismatch: **hard error**, not a
     warning — "world was built with netconvert 1.27.0; this SUMO installation resolves to 1.27.1 at
     `G:\Sumo`; pass `--sumo-home` to point at a matching installation or rebuild the world" — with an
     explicit `--allow-version-mismatch` escape hatch for a developer who has a specific reason to accept
     the risk. `SumoInstallation` is the natural home for the comparison primitive itself (e.g. a
     `require_version(expected: str) -> None` method), since it already owns version resolution; whether
     the *caller* refuses or warns by default is `07_Scenario_Authoring.md`'s call to finalize, since
     `SumoScenarioBuilder` is where that consumption happens.

This is a real, present bug independent of everything else in this document — it would exist even if no
line of the SUMO-driven-traffic plan were ever built, because it already affects today's Arapahoe/Bahonar
scenario authoring. It belongs in this document because the fix is a discovery/packaging change
(`SumoInstallation` + the world-package provenance field it reads), not a runtime co-simulation change.

```mermaid
flowchart TD
    A["SumoInstallation.locate()"] --> B{"explicit path given?"}
    B -- yes --> R["resolved"]
    B -- no --> C{"SUMO_HOME set?"}
    C -- "yes (this machine: G:\Sumo, v1.27.1)" --> R
    C -- no --> D{"extra_candidates\n(Build/sumo-src)?"}
    D -- yes --> R
    D -- no --> E["PATH search"] --> R
    R --> F["log resolved home + version — NEW, unconditional"]
    F --> G{"caller supplied an\nexpected version\n(from world.json)?"}
    G -- "match" --> H["proceed"]
    G -- "mismatch" --> I["refuse — hard error\n(--allow-version-mismatch escapes it)"]
    G -- "no expectation given" --> H
```

### 3.4 What must be staged, and what changes once the real type map exists

Doc 23 §6.2 and doc 07 §1.3 (`07_RoadNetwork_Filtering.md:84-96`) already measured why this matters:
`Build/sumo-install` ships no `data/typemap/`, so `netconvert` silently falls back to its **compiled-in**
default OSM type map. Doc 07's own build note is explicit that this is not currently a problem for the
vClass filter approach — the compiled-in defaults already give `passenger` the right allow/disallow set
(`07_RoadNetwork_Filtering.md:88-96`).

What changes once `data/` is staged and `SUMO_HOME` points at a complete installation:

- **Nothing changes by default.** `netconvert` without `--type-files` uses its compiled-in table
  regardless of what sits on disk at `$SUMO_HOME/data/typemap/`. Staging `data/` does not, by itself,
  alter any existing conversion's output.
- **It becomes possible to pass `--type-files $SUMO_HOME/data/typemap/osmNetconvert.typ.xml`
  deliberately** — to diff the compiled-in defaults against the shipped file (they are expected to
  agree, but "expected" is not "measured" — this is a cheap, worthwhile one-time check once staging
  lands), or to hand `netconvert` a **customized** type map (e.g. one that changes vClass permissions
  for a road class this fork cares about) without that customization living nowhere on disk.
- **Other SUMO tools need `data/` and `tools/` for reasons unrelated to netconvert's type map**:
  `sumo`/`duarouter` reference `data/` for XSD validation of generated files under some invocations,
  and `tools/` is where `traci`, `sumolib`, `randomTrips.py` and `routeSampler.py` (doc 23 §9 question 1)
  live — none of that is optional once the Python authoring path (§4.2) is expected to work from a
  staged install rather than a source checkout.

Staging shape (both platforms, added in §2's build step): `Build/sumo-install/{bin,data,tools,share/proj}`.
`SUMO_HOME` is not set by `CarlaSetup.ps1`/`.sh` themselves (§3.1's precedent — they print a reminder,
they do not persist an env var into the calling shell, and persisting a machine-wide env var from an
unattended CI run is the wrong tool for that job regardless). It is set:

- In `run_SCTMV.py`'s own defaulting block (`:60-78`), extended with the same `setdefault` pattern:
  `os.environ.setdefault("SUMO_HOME", _INSTALL)` — respecting an operator's own `SUMO_HOME` exactly the
  way `CARLA_NETCONVERT` already does, which is what makes §3.3's fix meaningful rather than circular.
- In the generated distribution launchers (`run-sctmv.ps1`/`.sh`), alongside `CARLA_NETCONVERT`/`PROJ_LIB`
  (§5).
- In `CarlaSetup.ps1`/`.sh`'s printed reminder (§3.1), extended to mention `SUMO_HOME` alongside
  `CARLA_NETCONVERT`/`PROJ_LIB` so a developer who does set env vars by hand sets all three together.

### 3.5 Corroboration, and one interaction with the new operator control surface

**Corroboration.** [`07_Scenario_Authoring.md`](07_Scenario_Authoring.md) §9.4 ("One netconvert, named")
independently measures the same collision from the scenario-authoring side: `SumoInstallation.locate`
preferring `$SUMO_HOME` (`G:\Sumo`, netconvert 1.27.1) over the repo-staged 1.27.0 that `run_SCTMV.py:66-71`
uses to build the world, and reaches the same conclusion — "today the world and its scenarios are built
by different converters, and nothing says so." That document also independently confirms that `duarouter`
staging is a prerequisite for route validation becoming a compile step, matching §2.1's inclusion of
`duarouter` in the required build-target set.

**The interaction.** [`12_Operator_Control_Surface.md`](12_Operator_Control_Surface.md) open question 2
asks where its proposed *site profile* — the layer that fixes host, ports and roots for a run
configuration — should get its SUMO installation path from, and names three options: an explicit file,
`SUMO_HOME`, or derivation from the distribution's own layout. It states plainly that the choice
"interacts with [`09`]'s `D9.6` `SUMO_HOME` precedence and should not be decided without it." This
document does not choose the site profile's shape — that is `12`'s call — but the property `12` needs
from this one is simple to state: **whatever the site profile's default source is, it must resolve to
the same installation `SumoInstallation.locate()` would resolve on the same machine, or the two surfaces
will silently disagree about which SUMO built, or is authoring against, a given world.** Concretely: if
the site profile derives its SUMO path from "the distribution's own layout" (`12`'s recommended option),
that is already exactly what §5.4 has `run-sctmv.ps1`/`.sh` do for `SUMO_HOME` — the same derivation, not
a second one — so `12` can adopt it with no new packaging mechanism. If instead the site profile is a
file an operator edits by hand, `D9.6`'s precedence and refusal behaviour apply to it exactly as they
apply to any other caller of `SumoInstallation.locate`, because it is one.

---

## 4. The C# binding wrapper, and the Python path it sits beside

### 4.1 The C# path

**`libtraci` (out-of-process), not `libsumo`** — doc 23 §6.3's recommendation stands; §6 below adds the
licensing angle it did not carry.

What a consuming .NET project needs, concretely:

1. **A new project, `CarlaNet/src/CarlaNet.Sumo/CarlaNet.Sumo.csproj`.** This holds only the generated
   SWIG proxy classes and the thinnest possible native-load glue — nothing CARLA-specific. It has **no**
   `ProjectReference` to any other `CarlaNet.*` assembly, which keeps it regenerable in isolation and
   gives the eventual bridge (doc 23 §5's proposed `CarlaNet.CoSim`, owned by `03_CoSimulation_Runtime.md`)
   a clean, acyclic dependency: `CarlaNet.CoSim` → `CarlaNet.Sumo` + `CarlaNet.Types` + `CarlaNet.Transport`
   + `CarlaNet.Map` + `CarlaNet.TrafficManager`, mirroring the existing pattern at
   `CarlaNet.Scenario.csproj:4-7`. `TargetFramework` is `net10.0`, matching every existing CarlaNet
   assembly (verified: `CarlaNet.Types.csproj:4`, `CarlaNet.Scenario.csproj:11`). Confirmed by directory
   listing: `CarlaNet/src/CarlaNet.Sumo/` does not exist yet — this is a proposal, not a description of
   something already built.
2. **The generated sources reproducibly, from a clean clone, not from a build tree someone happens to
   have.** SUMO's own build already produces exactly the right artifact for this:
   `Build/sumo-src/bin/libtracics-sources.zip`, which contains the full `Eclipse.Sumo.Libtraci/` folder
   (93 `.cs` files — §1's correction — plus the harmless `.cxx`, which a default `**/*.cs` compile glob
   never touches). Once §2/§3 stage the toolchain into `Build/sumo-install/bin/`, that zip is staged
   there too, and `CarlaNet.Sumo`'s build extracts it into its own source-generation output directory as
   an MSBuild pre-build step (unzip a fixed, versioned artifact — not "copy whatever loose files are
   sitting in someone's `Build/sumo-build`"). This makes the wrapper buildable from a clean clone that
   has run `CarlaSetup.ps1`/`.sh` once, with no manual file placement.
3. **`libtracics.dll`/`.so` on the native load path.** A post-build MSBuild target copies it from
   `Build/sumo-install/bin/` next to `CarlaNet.Sumo.dll`'s own output — the same mechanism already
   needed for any native interop in a `dotnet build` output directory, no RID-specific NuGet packaging
   required for an in-repo `ProjectReference`.
4. **Regeneration, not maintenance.** Nothing in `CarlaNet.Sumo` is hand-written; a SUMO version bump
   re-runs §2's build, produces a new `libtracics-sources.zip`, and the next `dotnet build` picks it up.
   There is no drift to reconcile because there is no hand-maintained copy.

### 4.2 The Python path — needed regardless of `03`'s C#-vs-Python decision

`03_CoSimulation_Runtime.md` decides whether the *per-tick bridge* is C# or Python. Independent of that:
today's **working** SUMO tooling (`SumoScenarioBuilder`, `SumoPatternOfLifeBuilder`, `SumoCotBridge`, the
`make_*_scenario.py` CLIs) is Python, uses `carlacontrol.SumoInstallation` (§3.2) to resolve `traci`/
`sumolib` from `$SUMO_HOME/tools`, and will keep needing that regardless of what `03` decides for the
bridge. Once §2/§3 stage `data/` and `tools/` into `Build/sumo-install`, this path needs nothing new
structurally — `SumoInstallation.tools` already resolves to `<home>/tools` and `import_traci()` already
adds it to `sys.path` (`SumoInstallation.py:93-97`). What changes is that `Build/sumo-install` becomes a
**genuinely complete** installation matching what `_is_installation`'s lenient check already implies is
possible, closing the gap where `REPO_SUMO = Build/sumo-src` (the source tree) is the only candidate that
currently works end-to-end for this path (§3.2).

### 4.3 The licensing obligations this section must not silently violate

`Findings/22_Digital_Twin_Feature_Port.md` §14 records `CarlaControl/` as SNC proprietary and to be
excluded from any external distribution. **That label came from a plan that did not manifest; there is
no exclusion to honour**, and the row is corrected at its source. `carlacontrol.SumoInstallation`,
`SumoScenarioBuilder`, `SumoPatternOfLifeBuilder` and every `make_*_scenario.py` CLI live in
`CarlaControl/` and ship in the single distribution (`D9.7`), as do `12`'s operator-surface classes
(§5.6).

The obligations that *are* real are third-party, and the distribution meets none of them today.
**Measured** against the staged Windows distribution at `Build/Dist/`:

| Obligation | State today |
|---|---|
| **No licence statement of any kind ships.** The distribution root is `CarlaServer/ README.md VERSION osm/ run-sctmv.ps1 run-server.ps1 scripts/ setup-venv.ps1 tools/ wheels/` | No `LICENSE`, no `NOTICE`, no third-party listing, and no precedent anywhere in the tree to copy |
| **`tools/sumo/` ships 42 DLLs spanning nine or more licences**, including LGPL (`fox-16.dll`, `iconv-2`/`intl-8`), OpenSSL, Apache-2.0 (Arrow, Parquet, Thrift, Xerces), PROJ, and the MS redistributables — debug *and* release variants of several | Redistributed with no notice. The set arrives via `CarlaSetup.ps1`'s `bin\*.dll` glob and `MakeDistribution.ps1`'s recursive copy; `fox` is SUMO's **GUI** toolkit, which `netconvert` never loads |
| **SUMO is EPL-2.0** — notice plus a source offer | The pinned upstream commit is already recorded in `CarlaSetup.ps1`, so the offer can cite it rather than duplicate it. `D9.5`'s SWIG-generated C# redistributes EPL-2.0 source as well |
| **The OSM extracts and every generated `.xodr` are ODbL**, the `.xodr` as a Derivative Database | `Findings/22` §14 establishes this; nothing in the package states it |

§5.4 carries these into the bundle list as a generated `MANIFEST.md` and a `licenses/` directory.

### 4.4 One adjacent, non-SUMO Python dependency: `tzdata`

Not a SUMO artifact, not a build-target change, and not a new licensing category — recorded here because
it is a real, measured, present packaging gap the epoch requirement exposes, and because "toolchain and
packaging" is the natural place to catch a new declared Python dependency, the same way §3.3's
`SUMO_HOME` collision was caught here rather than left for a scenario-authoring engineer to keep
rediscovering.

*Read, from [`07_Scenario_Authoring.md`](07_Scenario_Authoring.md) §2.8:* resolving a scenario's declared
civil offset against an IANA zone name (`Asia/Tehran`, for the sizing scenario's site) needs a time-zone
database, and *measured* there: on that machine, Python 3.14.4, `import tzdata` raises
`ModuleNotFoundError`, `zoneinfo.ZoneInfo("Asia/Tehran")` raises `ZoneInfoNotFoundError`, and
`zoneinfo.available_timezones()` returns zero entries — Windows ships no IANA database, and CPython's
`zoneinfo` falls back to the `tzdata` wheel, which is not installed. *Confirmed here, directly:*
`CarlaControl/pyproject.toml:12-15` declares exactly

```
dependencies = [
    "carlanet>=0.1.0",
    "numpy>=1.24.0",
]
```

and nothing else — no `tzdata`. [`11_Time_And_Illumination.md`](11_Time_And_Illumination.md) `D11.3`
formalises a `utc_offset_policy` with a `zone_database` option that resolves the offset from the IANA
zone at each instant; whether that makes `tzdata` a hard requirement, or (as `07` frames it) an optional
cross-check against a normative numeric offset, is `07`'s and `11`'s call to settle — see Open question 4
— not this document's. **What packaging needs to do is the same regardless of that answer.**

**The packaging consequence is small and rides an existing mechanism, not a new one.** `tzdata` is a
pure-Python data package with no native extension — nothing to compile, nothing to stage into
`Build/sumo-install` or anywhere like it. Adding it to `CarlaControl/pyproject.toml`'s `dependencies` is
sufficient: both distribution scripts already `pip install` the `carlacontrol` wheel and its declared
dependencies into the recipient's venv —

```powershell
& $py -m pip install $whl.FullName numpy pygame      # Scripts/Windows/MakeDistribution.ps1:277
```
```sh
pip install --find-links "$here/wheels" "$here"/wheels/*.whl numpy pygame   # Scripts/Linux/MakeDistribution.sh:174
```

— the exact mechanism that already resolves `numpy` and `pygame` for every distribution recipient today.
`tzdata` needs no new mechanism, no new build target, and no new distribution slot; it needs one line
added to a dependencies list that already exists, and it flows through `setup-venv.ps1`/`.sh` exactly as
`numpy` does. Its own licensing status (the PyPI `tzdata` package, distinct from the public-domain IANA
database it packages) has not been checked here and belongs beside §6's other open licensing items if
`07`/`11` decide it is required rather than optional — flagged, not resolved, per this document's own
licensing practice (§6).

---

## 5. Distribution

### 5.1 What `MakeDistribution` bundles today, verified against both platforms

| Slot | Windows (`Scripts/Windows/MakeDistribution.ps1`) | Linux (`Scripts/Linux/MakeDistribution.sh`) |
|---|---|---|
| Cooked server | `:212-213` | `:90` |
| `carlanet` wheel | `:230` | `:104-114` (`copy_newest_wheel "$root/CarlaNet/python/dist"`) |
| `carlacontrol` wheel | **not bundled** | `:104-114` (`copy_newest_wheel "$root/CarlaControl/dist"`) |
| Demo client | `foreach ($f in 'SCTMV.py', 'osm_clip.py') { … CarlaNet\python\$f … }` (`:237-241`) | `cp "$root/CarlaControl/scripts/run_SCTMV.py" "$dist/scripts/"` (`:117`) |
| SUMO netconvert + DLLs + PROJ | `:248-260` | `:122-158` (walks `ldd`) |
| `SUMO_HOME` in launcher | not set | not set |

### 5.2 A currently-broken distribution, found while verifying this table

`Scripts/Windows/MakeDistribution.ps1:237-241` copies `CarlaNet\python\SCTMV.py` into the distribution's
`scripts/` folder. **That file does not exist** (§0/§3.1 — deleted 2026-09-15, superseded by
`CarlaControl/scripts/run_SCTMV.py`). The copy loop's own guard (`if (Test-Path $src) { … } else {
Write-Warning "missing $src" }`) means the build does not fail — it silently omits the file and keeps
going. The generated `run-sctmv.ps1` launcher (`:295-303`) then unconditionally does:

```powershell
& $py "$here\scripts\SCTMV.py" @args
```

which will fail the moment anyone runs a distribution built from the current tree, with "file not
found," not with anything that names the real cause. **This predates and is independent of the SUMO
toolchain work**, but it sits in the exact script section this plan already has to touch (the SUMO
copy block a few lines below it), so it should be fixed in the same change: point the Windows copy at
`CarlaControl\scripts\run_SCTMV.py`, matching what Linux already does correctly.

[`12_Operator_Control_Surface.md`](12_Operator_Control_Surface.md)
§10 measures the identical defect independently and adds the second half:
`MakeDistribution.sh:112-113` bundles **both** wheels, while the Windows script bundles
only `carlanet`, so even a path fix alone would leave a Windows distribution unable to `import
carlacontrol`. `12` states that its own new launcher must ship "on both platforms in the same change" as
this repair. §5.6 works out what that means for packaging.

### 5.3 Why Windows and Linux disagree, and what settles it

Today Windows and Linux disagree about bundling `carlacontrol` — Linux bundles it (`:104-114`), Windows
doesn't (§5.1) — but **by accident**, not by design: Windows doesn't bundle it because its demo-script
reference is broken and was never updated to the file that needs `carlacontrol`. Nobody decided it.

**Parity settles it, and there is one distribution containing all of the tools** (`D9.7`). Fixing §5.2
the straightforward way — point at `run_SCTMV.py`, which imports `carlacontrol` — makes Windows bundle
the wheel too, which is the intended end state rather than a boundary being crossed. §5.6 records that
`12`'s launcher forces the same bundling independently, so it was never avoidable.

**Measured, and not previously recorded: there is a third break, and repairing the first two without it
makes things worse.** `MakeDistribution.ps1:268-280` generates `setup-venv.ps1`, whose install line is

```powershell
$whl = Get-ChildItem "$here\wheels\*.whl" | Select-Object -First 1
& $py -m pip install $whl.FullName numpy pygame
```

— **one arbitrary wheel**. Add `carlacontrol` to `wheels/` without changing this and
`carlacontrol-0.1.0…whl` sorts first, pip resolves its `carlanet>=0.1.0` dependency against PyPI, and the
install fails on the recipient's machine at setup time. Linux is already correct
(`MakeDistribution.sh:174` passes `--find-links` and every wheel). The repair is three lines, not one.

**Also measured: the break is latent, not visible.** `Build/Dist/Carla-0.10.0-Win64-Development/` was
staged 2026-09-02, thirteen days *before* `d2c666c23` deleted the script it references, so the artifact
on disk works and contains `scripts/SCTMV.py`. Anyone judging the Windows distribution from what is
staged concludes it is healthy. The next Windows build produces a broken one, behind a `Write-Warning`
in a long cook log — which is why §5.2's repair makes the missing wheel a terminating error.

### 5.4 What must be bundled once the toolchain is complete

Extending both scripts' SUMO block (§5.1's row 5) from "netconvert + its DLLs + PROJ data" to the
staged install — **a named subset, not a recursive copy**. Measured: `data/` and `tools/` in full are
89 MB to deliver the **3.2 MB** anything in this plan consumes, and `tools/contributed` alone is 47 MB
of third-party contributions that would each need a `MANIFEST.md` row. Add to the list when something
consumes it, and record the reason beside the list, or a future reader will "fix" the omission.

The binary rows are likewise an explicit list derived from what the binaries import, not `bin\*`
(§4.3). Linux already walks `ldd` per binary at `MakeDistribution.sh:125-144`; Windows needs the
equivalent `dumpbin /dependents` pass in place of its glob.

| Artifact | Windows source | Linux source | Destination |
|---|---|---|---|
| `sumo`, `duarouter`, `netconvert` + runtime DLLs/`.so`s | `Build\sumo-install\bin\*` | `Build/sumo-install/bin/*` (Linux already walks `ldd` per-binary at `:125-144`; extend the walk to all three, not just `netconvert`) | `tools\sumo\` |
| `libtracics.dll`/`.so` | same | same | `tools\sumo\` |
| SUMO `data/`, **named subset**: `typemap`, `xsd` | `Build\sumo-install\data\{typemap,xsd}` | same | `tools\sumo\data\` |
| SUMO `tools/`, **named subset**: `traci`, `sumolib` | `Build\sumo-install\tools\{traci,sumolib}` | same | `tools\sumo\tools\` |
| `libtracics-sources.zip` — `D9.5` builds `CarlaNet.Sumo` from it, and a recipient has no `Build/` tree | `Build\sumo-install\bin\libtracics-sources.zip` | same | `tools\sumo\` |
| PROJ data | already bundled | already bundled | `tools\sumo\proj\` (unchanged) |
| `CarlaNet.Sumo` wrapper assembly + `libtracics` native lib | `CarlaNet\src\CarlaNet.Sumo\bin\Release\net10.0\*` | equivalent | `wheels\` is Python-only — this needs a new `dotnet\` (or similar) slot, since it is a managed assembly, not a wheel |
| `SUMO_HOME` in the generated launcher | new line in `run-sctmv.ps1`: `$env:SUMO_HOME = Join-Path $here 'tools\sumo'` | new line in `run-sctmv.sh`: `export SUMO_HOME="$here/tools/sumo"` | — |
| **`MANIFEST.md`** — generated at staging time from what was actually copied, never hand-maintained (§4.3) | generated | generated | distribution root |
| **`licenses/`** — CARLA MIT, `CarlaControl/LICENSE`, SUMO EPL-2.0 + `NOTICE.md`, the native third-party table, OpenStreetMap ODbL | copied | copied | `licenses\` |
| **The authoring skill bundle** (§5.5, `D9.10`) | `CarlaControl\skills\sumo-traffic-scenarios\*` | same | `skills\sumo-traffic-scenarios\` |

### 5.5 The new, non-toolchain artifacts this plan introduces

Per the team brief, this section states what must be shippable, not their formats: the vehicle catalogue
and the annotation vocabulary (doc 20), the authoring-skill bundle and the illumination reference that
`§3a` and its sibling sections introduce, and the run-configuration schema with its site-profile
template (`12`).

- **The vehicle catalogue.** Doc 20 §5.6 already states the requirement plainly: "**Generated from a
  running server, never hand-maintained**… **Versioned and shipped with the distribution**, so a
  storyboard authored against one content build can be validated against the world it is run in"
  (`20_Behavioral_Annotation_And_Areas_Of_Interest.md:647,651`). The distribution already has a natural
  versioning hook for this: `Unreal/Package/CreateCarlaVersionFile.cmake:51-59` writes a `VERSION` file
  recording `Carla version`, `Carla git hash`, **`Content git hash`** and `UnrealEngine git hash`
  separately, precisely because "a CARLA release can change nothing a world depends on" (`:47-50`). The
  vehicle catalogue is a property of the content build, so its version stamp belongs next to `Content
  git hash`, not `Carla version` — the exact schema and file format are `04_Contracts.md`'s call; what I
  specify is that it needs a distribution slot (e.g. a new `catalogs\` alongside `wheels\`/`osm\`) and
  that its version must be checkable against the `VERSION` file already shipped at the distribution root.
- **The annotation vocabulary.** Doc 20 §6.2: "a term list carried with the annotation set… `vocabulary_version`
  lets a consumer refuse a corpus it does not understand" (`:702-707`). Same packaging need: a
  discoverable, versioned file shipped with the distribution (or with a captured corpus — that's `06`'s
  and `08`'s call), not embedded in code.
- **The authoring skill — a bundle, not a single file, and it moves into the repository.**
  `.agents/skills/sumo-traffic-scenarios/SKILL.md` is a single 14 KB file with no supporting files,
  measured directly. [`07_Scenario_Authoring.md`](07_Scenario_Authoring.md) §8 specifies it as a versioned
  bundle — `schemas/scenario.schema.json`, `schemas/sweep.schema.json`,
  `vocabulary.json`, `checks.json`, `examples/`, `references/gotchas.md`, `references/resolution.md` —
  most of it generated from the compiler so it cannot drift from the code it describes. **`07` §8.4
  states it lives "in the repository … where it is now."** Measured 2026-09-18: it does not. The
  workspace root (`G:\Projects\CarlaUE_5_7_4`) is not a git repository at all (`git rev-parse
  --is-inside-work-tree` fails there), and there is no `.agents/` directory anywhere inside the `carla`
  repository itself — `carla` *is* a git repository (confirmed), and
  `carla/.agents/skills/sumo-traffic-scenarios/` does not exist in it. This is not a disagreement to
  paper over: `07`'s design for the bundle's *content* stands regardless of where it lives, but the
  bundle cannot be given a real path for `MakeDistribution` to reference, on either platform, until it is
  physically moved inside `carla/`, tracked, and versioned there. **Target: `carla/CarlaControl/skills/
  sumo-traffic-scenarios/`** — beside the compiler `07` §8 says generates most of its contents, so
  generator and generated output sit under one directory and one licence, and `MakeDistribution` gets a
  real source path on both platforms (§5.4, `D9.10`). The 27 `ue-*` directories beside it in
  `.agents/skills/` are **not** part of this move: measured byte-identical to the MIT third-party
  repository `quodsoler/unreal-engine-skills`, already cloned at the workspace root with its remote,
  pinned commit and `LICENSE` intact. They stay there.
- **The illumination reference.** [`07_Scenario_Authoring.md`](07_Scenario_Authoring.md)'s pre-authoring
  artifact list (item 12, §2.9) names a per-world, per-date sunrise/sunset/sun-elevation table and a
  night-viability verdict, computed from the world's origin and the scenario's epoch — owned by
  [`11_Time_And_Illumination.md`](11_Time_And_Illumination.md), consumed by `07`'s compiler before an
  author writes anything, so it must be readable without a running server. The packaging need is the same
  shape as the vehicle catalogue: a stable, versioned, discoverable representation that travels with (or
  is derivable from) a world package. The exact schema, file and whether it is precomputed or computed on
  demand are `11`'s and `07`'s calls; what this document specifies is only that it needs a real path once
  one of them chooses it — the same requirement the other three artifacts here already carry.
- **The run-configuration schema and the site-profile template.**
  [`12_Operator_Control_Surface.md`](12_Operator_Control_Surface.md) §3.9 defines `RunConfiguration` (the
  parsed, unresolved document an operator writes or a tool emits) and `SiteProfile` (the per-machine
  layer that keeps a run configuration portable across platforms) as new classes in `CarlaControl/`, and
  its §10 already states that both a schema and a site-profile *template* must ship in the distribution,
  alongside the vehicle catalogue and the annotation vocabulary, on both platforms. §5.6 states the
  packaging shape this requires.

```mermaid
flowchart TD
    subgraph ship["Ships in the distribution"]
        NC2[netconvert/sumo/duarouter + libs]
        DATA[SUMO data/]
        TOOLS[SUMO tools/ - traci, sumolib]
        LTC[libtracics native lib]
        WRAP[CarlaNet.Sumo wrapper assembly]
        CNW[carlanet wheel]
        CCW["carlacontrol wheel - ships on both platforms (D9.7);
named in MANIFEST.md (S4.3)"]
        VCAT[vehicle catalogue - versioned w/ content build]
        VOCAB[annotation vocabulary - versioned]
        SKILL["authoring skill - grows (07 §8);\nrepo-home mismatch, see above"]
        ILLUM["illumination reference - versioned\n(11 / 07 §2.9)"]
        RUNCFG["run-configuration schema (12)"]
        SITEPROF["site-profile template (12)"]
        RUNCAP["run-capture launcher pair - NEW,\nbeside run-sctmv (12 §10)"]
        VERFILE[VERSION file]
    end
    subgraph consume["Consumers"]
        OSMC[OsmConverter.cs - world build]
        COSIM["CarlaNet.CoSim (03) - per-tick bridge"]
        SSB["SumoScenarioBuilder / make_*_scenario.py"]
        SCB[SumoCotBridge - telemetry]
        AUTH[Scenario-authoring workflow]
        OPCTRL["run_capture / operator\ncontrol surface (12)"]
        EPOL["Corpus handover (08)"]
    end
    NC2 --> OSMC
    NC2 --> SSB
    LTC --> COSIM
    WRAP --> COSIM
    DATA --> SSB
    TOOLS --> SSB
    TOOLS --> SCB
    CNW --> COSIM
    CNW --> OPCTRL
    CCW --> SSB
    CCW --> SCB
    CCW --> OPCTRL
    VCAT --> AUTH
    VCAT --> OPCTRL
    SKILL --> AUTH
    VOCAB --> EPOL
    VOCAB --> OPCTRL
    ILLUM --> AUTH
    RUNCFG --> OPCTRL
    SITEPROF --> OPCTRL
    RUNCAP --> OPCTRL
    VERFILE -. version-checked against .-> VCAT
```

### 5.6 The operator control surface's packaging consequence

This subsection answers three questions in order: what this document needs from `12`, what `12` has
already decided that this document must therefore package, and how that decision relates to §5.2's
already-broken launcher.

**What this document needs from `12`, stated generically because `12` did not exist when this was first
asked for.** Whatever shape the operator control surface takes — a run-configuration file format, one or
more new launchers, a replacement for the generated `run-sctmv.ps1`, or (as it turns out) all of the
above except the replacement — packaging needs to know, for each artifact it produces or consumes:

1. Is it a static file this repository already has a path for, or something `MakeDistribution` must
   generate at build time, the way `run-sctmv.ps1` itself is a heredoc the script writes, not a file it
   copies from the repo?
2. Does it introduce a new runtime dependency (a Python package, a native library, a .NET assembly) that
   is not already declared somewhere this document stages or bundles?
3. Does it replace an existing generated launcher, or does it coexist beside one? The two have different
   packaging shapes — a replacement removes a slot; a coexisting one adds a slot, and both must keep
   working, on both platforms, forever after.
4. Does it need versioning against the content build the way the vehicle catalogue does (§5.5), because
   it encodes assumptions about a world package's schema that can change?

**What `12` has already decided, read here as fact rather than designed.**
[`12_Operator_Control_Surface.md`](12_Operator_Control_Surface.md) §10 answers all four questions
concretely, and this document packages the answer, not a hypothetical:

| Artifact | Shape | Windows | Linux |
|---|---|---|---|
| Capture launcher (source-side tool) | new script | `Scripts/Windows/RunCapture.ps1` | `Scripts/Linux/RunCapture.sh` |
| Distribution launcher | generated; **coexists** beside `run-sctmv.ps1`/`.sh`, does not replace it (`D12.17`: `run_SCTMV.py`'s 86 arguments and 13 hotkeys are all kept) | `run-capture.ps1` | `run-capture.sh` |
| Run-configuration schema + site-profile template | new files, versioned with the content build the same way the vehicle catalogue is (§5.5's answer to question 4) | staged alongside the vehicle catalogue and vocabulary | same |
| `carlacontrol` wheel | required on **both** platforms — `run_capture` is built on the `RunConfiguration`/`SiteProfile`/`EffectiveRunConfiguration` classes `12` §3.9 places in `CarlaControl/`, so a distribution shipping the new launcher without the wheel ships a launcher that cannot import its own dependency | must be bundled — see `D9.7` | already bundled today |
| `MakeDistribution` header comment | updated tree description | `:8-20` | `:9-12` |
| Distribution README | a capture quick-start section added | — | — |
| CI | a parity check invoking both launchers with `--help` and comparing option sets (`12` `D12.19`) | one check, exercises both platforms' generated launchers | — |

**The relationship to §5.2.** `12`'s own measurement (`D12.19`) is the same defect this document found
independently — `MakeDistribution.ps1:237` copying a file deleted in `d2c666c23`, `:301` then writing a
launcher that runs it, and the Windows script bundling only `carlanet` where Linux already bundles both
wheels. `12` states plainly that its new launcher "ships on both platforms in the same change" as that
repair. This document agrees and sharpens it: **both repairs touch the identical few lines of each
`MakeDistribution` script — the wheel-copy block and the launcher-heredoc block — so doing them as two
separate changes means editing the same lines twice: once to fix `run-sctmv.ps1`, and once more, shortly
after, to add `run-capture.ps1` beside it.** One change landing both is not a convenience; it is the only
way to avoid a second pass over code the first pass just touched.

**The consequence for `D9.7`.** `12`'s new launcher makes bundling `carlacontrol` a hard requirement
for the capture path to work at all, on both platforms, independent of whether §5.2 is ever fixed on
its own — so the single-distribution answer was never avoidable. The `CarlaControl/` footprint is
larger than the wheel alone — `12` §3.9 adds
`RunConfiguration.py`, `SiteProfile.py`, `RunConfigurationResolver.py`, `EffectiveRunConfiguration.py`,
`RunConfigurationValidator.py`, `SessionMonitor.py`, `RunCloseoutReport.py` and `WorldBuildConfiguration.py`
to `CarlaControl/`, all covered by the same `CarlaControl/LICENSE` exclusion §4.3 already names. `D9.7`'s
recommendation is an explicit internal/external packaging mode, not a default, and the case
for deciding it before an external distribution is ever attempted is concrete. See `D9.11`.

---

## 6. Licensing — facts, not advice

SUMO is Eclipse Public License 2.0 (`Build/sumo-src/LICENSE`, `Build/sumo-src/NOTICE.md`). Prior analysis
already exists and is not being redone here: `Findings/22_Digital_Twin_Feature_Port.md:528` records the
current position for the one SUMO artifact already redistributed —

> `SUMO netconvert binary | EPL-2.0 | notice + source offer; separate-process invocation keeps our code
> outside file-level copyleft`

Extending that to every binding choice this plan puts in front of the user:

| Choice | What is redistributed | Boundary | Consequence, stated plainly |
|---|---|---|---|
| `netconvert` (already shipped) | compiled binary only | separate process, invoked by argv | notice + source offer (doc 22's existing conclusion, unchanged) |
| `sumo` + `duarouter` (new) | compiled binaries only | separate processes | same as `netconvert` — no new category |
| **`libtraci` (recommended bridge)** | compiled native `libtracics.dll`/`.so` **and generated C# source** (`Eclipse.Sumo.Libtraci/*.cs`) | out-of-process TraCI protocol between our code and the `sumo` process; the native `libtracics` library and its generated `.cs` proxies are SWIG output from SUMO's own `.i` interface files, which are themselves part of SUMO's EPL-2.0 source tree | the *binary* half is the same notice-plus-source-offer situation as `netconvert`. The *generated source* half is materially different from anything doc 22 already covers: this plan ships **EPL-2.0-derived source code**, not only an opaque binary, inside our own build/distribution. Whether that requires anything beyond notice + retaining SUMO's own copyright headers in the generated files (EPL-2.0 is file-level copyleft — modifications to an EPL-covered *file* carry obligations for that file, it does not by itself extend to code that merely calls it) is **flagged for legal review, not decided here** |
| `libsumo` (rejected in doc 23 §6.3 on architecture grounds) | `libsumocpp`/`libsumostatic.lib` **statically linked** into the same native module our own code loads, plus the same category of generated C# source as `libtraci` | in-process; EPL-2.0 is explicit that mere aggregation and dynamic linking do not extend copyleft to the linking program, but **static linking into the same binary is a closer, more contested boundary** than a separate process talking over TCP | staying out-of-process (§4.1's recommendation) is therefore **also** the licensing-conservative choice, not only the crash-isolation one — the architecture decision and the licensing posture point the same direction, which is worth knowing when the choice is made, not after |
| `CarlaControl/` (existing, unrelated to SUMO; growing on account of `12`, §5.6) | source, `CarlaControl/LICENSE` | notice | **Ships in the single distribution** (`D9.7`) and is named in `MANIFEST.md`. `Findings/22` §14's "exclude from any external distribution" came from a plan that did not manifest and is corrected at source; there is no exclusion to honour |

No conclusion above should be read as legal sign-off. The two flagged rows — generated EPL-2.0 source
bundled as source, and the internal/external distribution boundary around `CarlaControl/` — are the two
items worth a licensing review before this ships, stated as facts so that review has something concrete
to look at rather than a summary.

**Two items are minor and flagged rather than resolved, consistent with the rest of this table.**
`tzdata` (§4.4) is a pure-Python data package with no native code and no
redistribution boundary of the kind the rest of this table addresses; it is listed here only so a
licensing pass sees it — its own PyPI packaging terms (distinct from the public-domain IANA database it
carries) have not been checked. And `12`'s new classes (§5.6) add source volume to the `CarlaControl/`
row above without changing its category.

---

## 7. CI and reproducibility

### 7.1 The actual pipeline, which has moved past doc 15's plan

Doc 15 is a **plan** document; `.github/workflows/build-carla-ue5.yml` is the **implementation that
exists and runs today** (manual `workflow_dispatch` only, self-hosted GHE runner, per project memory
confirmed working end-to-end since 2026-07-28). Where they differ, the workflow file is ground truth for
this section:

- Doc 15 imagined building the engine in the same pipeline (§4 there); the actual workflow **fetches a
  pre-built engine tarball from Artifactory** (`prepare-ue-distribution`, workflow step "Prepare Unreal
  Engine distribution") — the engine pipeline doc 15 designed is a separate, not-yet-shown concern.
- Doc 15 assumed a raw `docker`/registry credential flow; the actual workflow uses **podman**, rootless,
  with a per-job copied auth file, and mounts the content snapshot with an overlay-probe fallback
  (`Select content mount mode` step) — considerably more defensive than the plan.
- Doc 15 flagged GHES forbidding `upload-artifact` v4 as an open risk; the actual workflow has **already
  designed around it** — its own comment states this explicitly ("GitHub Enterprise Server does not
  support `actions/upload-artifact` v4 at all") and publishes exclusively to Artifactory with a `.sha256`
  sidecar instead.
- `Build/` **persists between runs** on the runner (the "Clean tracked workspace" step deliberately does
  not `git clean -ffdx`, precisely to avoid re-downloading the ~43 GB content cache and Cesium/vcpkg
  dependencies every run). This is the detail that makes §2's idempotence-guard fix matter for CI, not
  only for a developer's laptop (§7.2).

### 7.2 What has to change for the SUMO toolchain to be built and shipped by this pipeline

1. **`carla-base:alma8` needs `swig`** (§2.3) — a Dockerfile change, rebuild, and re-push to
   `iasartifact.sncorp.com:8443/cat-docker-dev/carla-base:alma8`. The workflow's "Pull container image"
   step already pulls by a fixed tag with no digest pin visible in the workflow, so a rebuild under the
   same tag reaches the next run automatically; whether that tag should move to a digest pin so a SUMO
   toolchain change can't silently ride in on an unrelated base-image rebuild is worth a decision (`D9.8`).
2. **No workflow change is needed to trigger the new targets.** `CarlaSetup.sh --skip-prerequisites`
   (the only build-relevant flag the workflow passes) skips `InstallPrerequisites.sh`, not the SUMO build
   block — the two are separate steps in the script. Once §2's target-list change lands and the container
   has `swig`, the existing "Build CARLA distribution" step picks it up with no workflow edit.
3. **The persistent `Build/` tree means §2's guard fix is a CI-correctness fix, not only a developer
   convenience.** Before this change, a CI box that already has `netconvert.exe` staged from an earlier
   run would skip the SUMO step forever, silently shipping an incomplete `tools/sumo/` in every
   distribution until someone dispatches `clean_build: true` and happens to notice. After this change,
   the guard itself detects the shortfall on the very next run, with no `clean_build` required.
4. **`clean_build: true` (the workflow's existing input) already exercises the from-scratch path** — it
   removes `Build/` entirely (`rm -rf "$GITHUB_WORKSPACE/Build"`), which includes `Build/sumo-*`. This is
   the reproducibility check (§7.3), not a new mechanism to add.
5. **A new CI check owned by `12`, not this document, but worth naming so CI's growing checklist is
   visible in one place.** `12` `D12.19` adds a parity check that invokes both distribution launchers
   (`run-sctmv`, `run-capture`) with `--help` and compares their option sets, so the exact parity break
   §5.2/§5.6 describe cannot recur unnoticed. It belongs in the same "Build CARLA distribution" job as
   this document's own checks (§9) once `12`'s launcher exists to be checked.

### 7.3 A clean-clone reproducibility check, defined concretely

A `workflow_dispatch` run with `clean_build: true` should reproduce, byte-for-byte where that is a
meaningful comparison and semantically where it is not:

- `Build/sumo-install/bin/` containing all four required binaries, each reporting the pinned version
  (`sumo --version` → `1.27.0`, built from commit `e238ea04b7150ba23a348a285d3048919fa4830b` — the same
  pin `CarlaSetup.ps1:616` carries)
- `Build/sumo-install/{data,tools}` populated
- the distribution tarball's `tools/sumo/` mirroring the same set

**This check must not hash OSM-derived content and call it toolchain reproducibility** — a finding from
the scenario-authoring engineer, verified here, and worth stating because it is exactly the kind of thing
a reproducibility check would naively do. `CarlaControl/src/carlacontrol/OsmClipper.py:154` builds
`used_orig = set()` and iterates it at `:219` (`for nid in used_orig: newroot.append(...)`) to decide
node emission order in the clipped `.osm` output. Python randomizes string hashing per process by
default, so **the same input OSM, clipped three separate times, produces three different byte sequences
and three different SHA-256 digests while the parsed graph is identical** — confirmed by reading the
code path, not yet re-run three times independently, but the mechanism (per-process hash randomization
driving `set` iteration order of string node IDs) is not in doubt. Any check in this plan that hashes a
`.osm`, `.net.xml` or `.xodr` file to detect toolchain drift will fail spuriously for this reason,
independent of whether the toolchain build itself is reproducible. §7.3's check therefore only hashes
**toolchain artifacts** (the SUMO binaries, which are ordinary compiler output and do not have this
problem) and, for anything downstream of `OsmClipper`, compares **semantic** properties instead — exactly
the shape doc 23 §2 already used (edge count, junction count, `<request>` row count, `tlLogic` count) —
or a canonicalized serialization (sort node IDs before comparing) rather than raw bytes. The actual fix
(`sorted(used_orig, key=int)` at the call site) is a one-line change in
`CarlaControl/`, outside this document's "no code changes" mandate and outside SUMO toolchain packaging
proper; it is named here as a decision to hand to whoever owns `OsmClipper.py` (`D9.9`), and worked around
in the verification design regardless of whether that fix ever lands.

---

## 8. Verification — concrete, runnable, and living somewhere a person or CI will actually run it

Doc 23 §8 proposes "`sumo --version` from the staged install" plus "a trivial console app stepping an
empty simulation through the binding." Turned into two runnable checks:

### 8.1 C# path

**New:** `CarlaNet/test/CarlaNet.Sumo.Tests/` (mirrors the existing `CarlaNet/test/CarlaNet.Tests`
convention). Contents:

```csharp
// Skips (not fails) when no SUMO installation resolves, so the wider `dotnet test` suite
// doesn't require SUMO_HOME on every machine that runs it.
[Fact]
public void StagedSumoAndDuarouterReportThePinnedVersion() { /* Process.Start(sumo/duarouter, "--version"),
    assert stdout contains "1.27.0" and the pinned commit if --version-verbose is used */ }

[Fact]
public void EmptySimulationStepsThroughLibtraci() {
    // Start `sumo` as a subprocess with --remote-port on an ephemeral port and a minimal
    // .sumocfg (an empty net + no vehicles — SUMO ships trivial fixtures under data/ for this).
    // Eclipse.Sumo.Libtraci.Simulation.init/step/close via CarlaNet.Sumo. Assert no exception
    // and that simulation time advanced by one step.
}
```

Run via `dotnet test CarlaNet/CarlaNet.sln --filter CarlaNet.Sumo.Tests`. This is the check that
answers "is the C# binding path actually usable," and it lives where `dotnet test` already runs it as
part of whatever CI or pre-commit step already runs the other 76 CarlaNet tests (project memory: "76/76
tests pass") rather than depending on someone remembering to run it by hand.

### 8.2 Python path

**New:** `CarlaControl/scripts/test_sumo_toolchain.py`, matching the existing direct-run-script
convention already used for `CarlaNet/python/test_*.py` (e.g. `test_left_turn_yield.py`,
`test_digital_twin.py`) rather than requiring a pytest harness:

```python
from carlacontrol.SumoInstallation import SumoInstallation

installation = SumoInstallation.locate(explicit=None, extra_candidates=[REPO_SUMO])
print(f"Resolved SUMO installation: {installation.home} (version {installation.version})")

subprocess.run([installation.sumo, "--version"], check=True)
subprocess.run([installation.executable("duarouter"), "--version"], check=True)   # required, §2.1

traci = installation.import_traci()
traci.start([str(installation.sumo), "-c", <a trivial fixture .sumocfg>])
traci.simulationStep()
traci.close()
```

Run directly: `python CarlaControl/scripts/test_sumo_toolchain.py [--sumo-home PATH]`. Exercised against
both the repo-staged install (`Build/sumo-install`, once §2/§3 land) and, deliberately, against whatever
`SUMO_HOME` happens to be set to — the second run is what would have caught §3.3 immediately, since it
prints the resolved home and version unconditionally rather than assuming.

### 8.3 What "done" means, stated once

Doc 23 §8's "Phase 0" is done when both 8.1 and 8.2 pass **from a distribution's staged install**, not
from a developer's `Build/` tree — i.e., the same two checks, pointed at `tools/sumo/` inside an
extracted distribution tarball, with `SUMO_HOME` set the way `run-sctmv.ps1`/`.sh` sets it (§5.4), pass
identically. That is the actual acceptance bar: not "it compiles," but "a distribution recipient with no
`Build/` tree at all can run both checks and get the same answer." Nothing about §3a changes this bar —
there is no equivalent "Phase 0" for the solar mechanism to pass here, because there is nothing of it in
this document's scope to verify (§2.4).

---

## 9. Decisions

| # | Decision |
|---|---|
| D9.1 | Build target list becomes `netconvert`, `sumo`, `duarouter`, `libtracics` (required, staged together) on both platforms. `jtrrouter`/`polyconvert` stay out of the default target list — nothing in this plan consumes them. |
| D9.2 | The idempotence guard checks **presence of the entire required set** — the four binaries, `libtracics-sources.zip` and the staged `data`/`tools` subset — not the freshness of a single "newest" file. It reports *which* members are missing rather than only that the check failed. **Measured: the guard is firing on a developer machine today** — `netconvert.exe` is staged, so `CarlaSetup` prints "Skipping SUMO build" and the other three binaries are never built. The block exists in **three** scripts, not two; `CarlaSetup.bat` is retired rather than carried (`D9.13`) — parallel (`-m`/`-j`) builds have no dependable single last-built artifact, and CI's persistent `Build/` tree makes a wrong guard a standing, silent CI defect, not just a developer inconvenience. |
| D9.3 | A **named subset** of `data/` and `tools/` is staged into `Build/sumo-install/{data,tools}` by the same build step, on both platforms: `data/typemap`, `data/xsd`, `tools/traci`, `tools/sumolib`. Measured: the full copy is 89 MB to deliver the 3.2 MB anything in this plan consumes, and `tools/contributed` alone is 47 MB of third-party contributions that would each need a `MANIFEST.md` row (§4.3). Directories are added when something consumes them, with the reason recorded beside the list. |
| D9.4 | `libtraci` (out-of-process) is the binding boundary for the C# path, per doc 23 §6.3, reaffirmed here on licensing grounds independently of the architecture ones (§6). `libsumo` is rejected for both reasons together. |
| D9.5 | A new, dependency-free `CarlaNet/src/CarlaNet.Sumo` project holds only the generated bindings and native-load glue, built from `libtracics-sources.zip` staged alongside the toolchain. Any co-simulation bridge (`03`'s `CarlaNet.CoSim` or equivalent) depends on it; it depends on nothing CARLA-specific. |
| D9.6 | `SUMO_HOME` precedence in `SumoInstallation.locate` is **not** reordered — `SUMO_HOME` continues to win, matching raw `traci`'s own convention. Instead: resolution is always logged (home + version), a `.version` property is added, and a version-mismatch between the netconvert that built a world and the SUMO installation resolved to author/run a scenario against it becomes a **hard-refusing** condition with an explicit override flag. The consumer-side refuse-vs-warn UX is `07_Scenario_Authoring.md`'s call to finalize against `SumoScenarioBuilder`. Corroborated independently by `07` §9.4 (§3.5); its interaction with `12`'s proposed site profile is answered generically in §3.5. |
| D9.7 | **There is one distribution and it contains all of the tools**, and both platforms produce it (`13` §13.2). Neither script gains an internal/external packaging mode: there is no second distribution for one to gate, and an unused mode is a switch that eventually gets flipped by accident. The `carlacontrol` wheel ships on both platforms — which `12`'s capture launcher makes unavoidable regardless of §5.2 (§5.6). `Findings/22` §14's exclusion of `CarlaControl/` came from a plan that did not manifest and is corrected at source. What the package must carry instead is a **generated `MANIFEST.md` and a `licenses/` directory** (§4.3, §5.4), driven by third-party obligations the distribution meets none of today: 42 native DLLs spanning nine or more licences including LGPL, SUMO's EPL-2.0 notice and source offer, and ODbL on the OSM extracts and generated `.xodr`. Where the package may go is governed by access to the channel it is published to. |
| D9.8 | `carla-base:alma8`'s pull in `build-carla-ue5.yml` is by tag with no digest pin; adding `swig` (D9.1's prerequisite) means rebuilding and re-pushing that tag. Needing the rebuild is not a cost. Whether the workflow should move to a digest pin so future base-image changes can't silently ride into a build is an open question (below), independent of this rebuild. |
| D9.9 | `CarlaControl/src/carlacontrol/OsmClipper.py:154,219`'s non-deterministic `set`-ordered node emission is a real reproducibility hazard for anything that hashes its output, and the proprietary tools are part of the shipped system (`D9.7`), so it is this plan's defect to fix. The fix is: the fix is `sorted(used_orig, key=int)` (or equivalent) at the emission site; until it lands, no reproducibility or acceptance check in this plan hashes OSM-clip-derived file bytes (§7.3). |
| D9.10 | The authoring skill (`.agents/skills/sumo-traffic-scenarios/SKILL.md`, 14 KB, one file) sits outside any git repository, so it has no version, no history and no reproducible source, and cannot be bundled into a distribution (§5.5). It **moves into `carla/CarlaControl/skills/`**, beside the compiler `07` §8 says generates most of its contents, and ships from there (§5.4). The workspace copy becomes a stub naming the canonical path — not a directory junction, which is invisible in `git status` and does not survive a fresh clone. **The 27 `ue-*` directories beside it are not ours**: measured byte-identical to `quodsoler/unreal-engine-skills`, an MIT third-party repository already cloned at the workspace root with its remote, pinned commit and `LICENSE` intact. They are **not vendored** — the clone is already a better reproducible source, and copying 1.3 MB of third-party MIT content into `carla/` would add an attribution obligation for a recipient with no use for it. The unattributed copies are removed and the clone is recorded as a developer prerequisite. A **stage A item** (`13` §13.3): `07` §8.4 depends on the move. |
| D9.11 | **The operator control surface's distribution footprint is packaged generically only where `12` had not yet fixed a shape; where it has, this document packages that shape as fact.** The new capture launcher (`run-capture.ps1`/`.sh`) coexists beside `run-sctmv.ps1`/`.sh` rather than replacing it; the run-configuration schema and site-profile template stage alongside the vehicle catalogue and vocabulary (§5.5); the broken-launcher repair (§5.2) and the new launcher's introduction land as one change, not two, because they touch the same lines of the same scripts (§5.6); and `12`'s launcher makes bundling `carlacontrol` unavoidable on both platforms, which `D9.7` settles on both platforms. |
| D9.12 | **`tzdata` is added to `CarlaControl/pyproject.toml`'s dependencies once `07`/`11` settle whether IANA zone resolution is required or merely an optional cross-check (§4.4, Open question 4).** It needs no new packaging mechanism — it resolves through the same `pip install` step that already installs `numpy` and `pygame` for every distribution recipient — and it introduces no SUMO dependency, no native code, and no new distribution slot. |
| D9.13 | **`CarlaSetup.bat` is retired**, and `Docs/build_windows_ue5.md` is repointed at `CarlaSetup.ps1` in the same commit so no documented entry point is left dangling. It is the pre-port original — `CarlaSetup.ps1:3` describes itself as a PowerShell port of it — and it has already drifted: `:145` clones `SUMOLibraries` at HEAD with no tag where `CarlaSetup.ps1:641` pins the version, which is the exact failure `CarlaSetup.ps1:609-618`'s own comment records. Keeping it would mean a third copy of every build change, which is how that drift happened. The charter's parity rule then covers exactly the two scripts it names. |
| D9.14 | **`SUMO_HOME` resolution is logged unconditionally, once per process, and a mismatch against the world's converter refuses.** `SumoInstallation.py:36` prefers `SUMO_HOME` over the repo-pinned install and has no version member at all; measured on a developer machine, worlds are built by the pinned **1.27.0** while every scenario tool silently resolves an unrelated **1.27.1**. `SumoInstallation` gains a `version` property parsed from `netconvert --version` (not `sumo` — `_is_installation` accepts a netconvert-only directory), a `source` field recording *which* rule matched, an unconditional `INFO` line naming path, version and source, and `require_version()`. Precedence is **not** reordered; `D9.6` stands. The escapes are `--sumo-home`, `--allow-version-mismatch`, and a world package that records no converter, which warns rather than refuses. |

## Open questions

1. **Does the version-mismatch check (§3.3, `D9.6`) default to refuse or default to warn?** Recommended:
   refuse by default with an explicit override, on the grounds that a silently-divergent converter is
   worse than a blocked run — but this trades developer friction against correctness, and
   `07_Scenario_Authoring.md` is closer to how often a legitimate mismatch (e.g., deliberately testing
   forward-compatibility with a newer SUMO) would occur in practice.
2. **Should `carla-base:alma8` move to a digest-pinned reference in `build-carla-ue5.yml` (`D9.8`)?**
   Independent of this plan, but this plan is the first thing found that needs a base-image change and
   therefore the first thing that would notice if that pin is missing.
3. **Where does `libtracics-sources.zip` (or its unzipped contents) live for a build that has never run
   `CarlaSetup.ps1`/`.sh`** — e.g., a from-scratch CI run before §2's staging step has ever executed?
   Answered functionally in §4.1 (it is a build-order dependency: `CarlaNet.Sumo` cannot build before the
   SUMO toolchain step has run once), but whether `CarlaNet.sln`'s build order should encode that
   dependency explicitly (an MSBuild `Exec` that shells out to `CarlaSetup`) or leave it as documented
   operator sequencing is a call for whoever wires `CarlaNet.CoSim` into the existing build (`03`/`05`).
4. **Is `tzdata` a required dependency or an optional one (§4.4)?** `07_Scenario_Authoring.md` §2.8
   frames the IANA zone name as an optional cross-check against a normative numeric offset;
   `11_Time_And_Illumination.md` `D11.3` formalises a `utc_offset_policy` value (`zone_database`) whose
   whole point is resolving the offset from the zone at each instant, which reads as a harder requirement
   under that policy. This document packages either answer identically, so it is not blocked on the
   outcome — but `07` and `11` should agree on the wording before an author-facing error message has to
   explain why a scenario declaring `zone_database` refuses on a machine without `tzdata` installed.
