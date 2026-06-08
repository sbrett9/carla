# CARLA × Cesium Digital-Twin — Project State (2026-06-08)

A point-in-time snapshot to survive context loss. **What actually works**, what's only
**planned**, the **open decisions**, and where the **georeferenced telemetry** stands (mostly
greenfield). Sibling docs: `Findings/06_Elevation_Strategy.md`, `Findings/07_RoadNetwork_Filtering.md`,
`DYNAMIC_WORLD_PIPELINE_PLAN.md`, and the feasibility report.

---

## 0. The goal (one paragraph)

Procedurally generate georeferenced digital-twin cities from OpenStreetMap, overlay Cesium
photogrammetry, run CARLA traffic **fully headless** on a multi-GPU server, and produce two
deliverables: (a) **high-altitude nadir electro-optical (EO) video**, and (b) **≥5 Hz per-vehicle
georeferenced "ADS-B-like" truth telemetry** (lat/lon/elevation/velocity/heading). These feed a
downstream object detector and a separate "cognitive threat-assessment EPoL model" (internals
out of scope). Google Photorealistic 3D Tiles are **demo-only** (ToS forbids training-data use);
the real dataset must use open content (Cesium OSM Buildings / self-hosted).

---

## 1. WHAT WORKS TODAY (implemented + verified headless, no editor)

The whole OSM→elevated-Cesium-aligned-world→traffic→EO-view loop runs against a **headless** CARLA
server with **no editor and no VibeUE**:

- **OSM → flat OpenDRIVE** offline via bundled SUMO `netconvert` (`CarlaNet.Map.OsmConverter`),
  origin pinned to world (0,0) with `+proj=tmerc +lat_0/+lon_0`.
- **Extract road reference-line samples + reproject to WGS84** offline
  (`ElevationInjector.ExtractCenterlineSamples` + `Geodesy.CarlaLocalToGeodetic`).
- **Runtime Cesium spawn** (no pre-placed actors): `UCesiumHeightSampler::ConfigureCesiumForOrigin`
  find-or-creates the `CesiumGeoreference`, spawns a `Cesium3DTileset` (ion asset, token), and
  spawns an `ACesiumSunSky` for real georeferenced lighting (the generated OpenDriveMap has no
  weather/sun actor).
- **Sample terrain heights headless** via `ACesium3DTileset::SampleHeightMostDetailed` (the
  `request_terrain_heights`/`poll_terrain_heights` two-call RPC; sync handlers can't block on the
  async game-thread callback). Validated: origin height matched a standalone probe to the mm.
- **Inject `<elevationProfile>`** into the .xodr (`ElevationInjector.InjectElevation`) with
  slope-robust outlier rejection (drops L-track/tree/awning spikes), then `generate_opendrive_world`.
- **EO observer** (`CarlaNet/python/eo_observer.py`): unparented nadir RGB camera streamed to a
  pygame window, Unreal-style flycam (RMB+WASD/EQ), background mover thread (UI never blocks on RPC),
  `elev` + signed `AGL` readout, and toggles: **C** cesium overlay, **V** photogrammetry collision,
  **R** road-mesh rendering (collision intact). [R / elev / AGL need the next rebuild — see §6.]
- **Traffic**: existing CarlaNet TrafficManager (`generate_traffic_carlanet.py --asynch`,
  `test_tm_motion.py`) — cars drive the generated roads.
- **Validated maps**: Wrigleyville (flat, dense — exposed the elevation-spike + road-tangle issues);
  Lakeview/Carson City NV (hilly, sparse — `Import/Lakeview_Carson.osm`, origin auto-derived).

### Server-side RPCs added (CarlaServer.cpp + CesiumCarlaBridge)
`request_terrain_heights`, `poll_terrain_heights`, `configure_cesium_georeference`,
`set_cesium_visible`, `set_cesium_collision`, `set_road_rendered`, `get_cesium_origin`.
Mirrored in `CarlaClient.cs` and the `carlanet` Python shim.

### Orchestration one-liner
`CarlaClient.GenerateWorldFromOsmWithElevationAsync(...)` / shim
`client.generate_world_from_osm_with_elevation(...)` runs the whole pipeline; `test_digital_twin.py`
drives it (auto-origin from OSM `<bounds>`, drivable-only road filter on by default).

---

## 2. BUILD / RUN QUICK REFERENCE

- **Repos**: `carla` (branch `ue5-dev`, remote `origin` = github.com/sbrett9/carla, private; `upstream`
  = carla-simulator/carla). Engine = `UE_5_7_4` (separate repo, branch `carla-port`).
- **Build everything**: `.\BuildCarla.ps1 -InstallWheel` (workspace root) — compiles
  CarlaUnrealEditor (C++) AND CarlaNet + the `carlanet` wheel. Switches: `-SkipUnreal`,
  `-SkipCarlaNet`, `-InstallWheel`. **Close the EO viewer first** (it locks the wheel DLLs).
- **Headless server**: `.\carla\Scripts\Windows\RunCarlaServer.ps1` (prints `SERVER READY` when
  port 2000 listens; Ctrl+C stops it). Editor binary `-game -RenderOffScreen`, Town10HD_Opt boot map.
- **Editor (rarely needed)**: `.\carla\Scripts\Windows\OpenCarlaEditor.ps1`.
- **Run the pipeline** (3 terminals): server → `python carla\CarlaNet\python\test_digital_twin.py`
  (needs `$env:CESIUM_ION_TOKEN`) → `generate_traffic_carlanet.py --asynch -n 40 -w 0` →
  `eo_observer.py`.
- **CarlaNet tests**: `dotnet test carla/CarlaNet/test/CarlaNet.Tests` (94/94).
- **VibeUE** (editor MCP dev tooling): now a pinned private-mirror dependency fetched by
  `CarlaSetup.{bat,sh} --vibeue-ssh-key=<path>` (or `$VIBEUE_SSH_KEY`); optional, gitignored. Mirror
  `sbrett9/VibeUE` branch `carla-5-7` @ `379373709e68ce7f2c4e3a26ff931f703d87b817`.

### Commit log (origin/ue5-dev)
`aa564b622` Phase A+B · `9c82623a6` Phase C–E RPC · `9fdf4b848` EO observer + toggles + lighting +
elevation cleanup · `718ad4cc1` Scripts/Windows · `b2d96b9ea` CarlaSetup VibeUE mirror.
Engine repo `carla-port`: `dbf80f792` Cesium v2.26 GaussianSplat null-world crash patch — **committed
but NOT pushed** (separate repo). Cesium upgrade to v2.27.0 (drops this patch) is an open TODO.

---

## 3. ELEVATION — status of `Findings/06_Elevation_Strategy.md` (PLAN ONLY)

**Nothing in 06 is implemented yet. It is undecided.** Current reality:

- **Roads are still elevation-injected** from the **Google Photoreal surface mesh** (ion asset
  `2275207`). Because that mesh includes buildings/L-tracks/trees, road samples spike; we apply
  **outlier rejection** as a band-aid (helps isolated spikes, not sustained structures).
- The 06 recommendation — **flat roads + bare-earth DEM telemetry** — is **not built**. Neither is:
  - flat-road mode (skip/zero the injection),
  - the two-tileset approach (display Google, **sample** a hidden **Cesium World Terrain** ion-1
    bare-earth tileset via `RequestSample`'s existing `TilesetActorName` filter),
  - the `CarlaNet.Geo` offline SRTM `.hgt` reader,
  - any geoid (EGM96) correction.

**User's current lean (not finalized):** keep sampling **Cesium-provided** terrain for now (defer
offline DTED) and judge the hilly Lakeview result before changing the source. So the immediate
question is really: **keep Google-surface sampling, or switch road/telemetry sampling to Cesium
World Terrain (ion 1, bare earth) via the two-tileset path?**

### ⚠️ The datum gotcha (flagged by 06, still unresolved)
`Geodesy`, `CesiumGeoreference.OriginHeight`, and both tilesets are **ellipsoidal WGS84 (HAE)**.
SRTM/DTED/3DEP are **orthometric (MSL/geoid)** — mixing them sinks telemetry ~34 m (Chicago) / varies
elsewhere. The EO viewer's `elev` is currently **HAE**. **Decision pending** (see §5).

---

## 4. ROAD NETWORK — status of `Findings/07_RoadNetwork_Filtering.md` (PARTLY WIRED)

- **Done (via ExtraArgs, no code in CarlaNet yet):** `test_digital_twin.py` passes
  `--keep-edges.by-vclass passenger --keep-edges.components 1 --remove-edges.isolated true` to
  netconvert → drivable-only network (drops footways, rail/subway, parking/service). Toggle with
  `--no-road-filter`. **Needs a visual confirm on Lakeview** that the "mess of road" is gone and
  nothing important was over-pruned.
- **Not done:** promoting these into typed `OsmConversionOptions` (`DrivableOnly`,
  `KeepVehicleClasses`, `PruneDisconnected`) per 07 §3 (currently only the loose `ExtraArgs` path).

---

## 5. OPEN DECISIONS (carry these forward)

1. **Elevation source** — Google surface (now) vs **Cesium World Terrain** (ion-1 bare earth,
   two-tileset) vs offline SRTM/DTED. Affects road-following realism + telemetry quality.
2. **Roads: injected vs flat** — keep injecting elevation (visual road-following on hills) or go
   **flat roads** (stable, nadir-EO-adequate) + telemetry-only elevation. (06 recommends flat.)
3. **Telemetry datum** — **ellipsoidal HAE** (ADS-B GNSS style; Cesium-clean, zero conversion) vs
   **orthometric MSL** (SRTM + EGM96 geoid). Decides the source and the `elev`/AGL readout meaning.
4. **Tileset collision default** — currently ON (cars can get stuck on the photoreal surface). The
   `V` toggle exists; should the *spawn default* be collision-OFF (visual-only overlay)?
5. **Road-filter typed options** — promote ExtraArgs → `OsmConversionOptions` (07).

---

## 6. IMMEDIATE NEXT STEPS

1. **Rebuild** `.\BuildCarla.ps1 -InstallWheel` (viewer closed) to activate the **R** road-hide
   toggle, **get_cesium_origin**, and **elev/AGL** on the live world. (C++ changed; not yet built.)
2. **Judge Lakeview**: road-filter cleanliness, how the hilly Google-surface elevation reads, whether
   flat-roads (Mode A) would look fine from nadir, and the collision A/B (V) for stuck cars.
3. Make decisions §5 (esp. elevation source + datum), then implement (likely: two-tileset
   bare-earth sampling and/or flat-road mode; then telemetry — §7).

---

## 7. TELEMETRY (≥5 Hz per vehicle) — MOSTLY GREENFIELD

**Status: not implemented.** No telemetry emitter exists. Only the *math* is in place.

### What we already have
- **`Geodesy.CarlaLocalToGeodetic(origin, x, y, z)`** — vehicle CARLA (x,y,z) → WGS84 lat/lon/alt.
  This is the core transform; validated (84/84 + tests). Same projection used for the elevation
  hand-off, so it's coherent.
- **The data source already streams**: `CarlaClient.StartWorldObserverAsync()` subscribes to CARLA's
  episode-state stream and caches **every actor's transform + velocity + angular velocity +
  acceleration** each server tick (`_actorCache`, `OnTick` event). So per-vehicle position/velocity
  at tick rate is *already available client-side in CarlaNet* — a telemetry emitter just consumes it,
  transforms to geo, throttles to 5 Hz, formats, and transmits. **No engine change required.**
- **`get_cesium_origin` RPC** gives the georeference origin (lat/lon/HAE height) for the transform.

### What is UNDECIDED (the user's open questions)
- **Message format** — **CoT (Cursor-on-Target, XML, TAK ecosystem)** is a strong candidate given the
  threat-assessment/C2 framing; alternatives: ADS-B-style fields, or a custom compact JSON/protobuf.
  **Not decided.** (CoT carries lat/lon/HAE, course/speed, a UID, and a time/stale — a clean fit for
  per-track vehicle data.)
- **Transport** — **broadcast (UDP multicast, the native CoT/TAK model)** vs **pub/sub** vs the
  existing CARLA sensor stream. **Not decided.**
- **Datum** — lat/lon are solid; **altitude** depends on Open Decision §5.3 (HAE vs MSL).
- **Tick → 5 Hz** — async server ticks at a variable rate; a 5 Hz emitter downsamples the world
  observer (or run sync mode at `fixed_delta_seconds = 0.2`).

### Does CARLA already provide a transport? (partial)
- **Sensor streaming** (TCP, port 2001+) — for sensor data; not a general broadcast.
- **World observer stream** — gives the *source data* (above), not an external publish format.
- **Native ROS2 / DDS** — CARLA 0.10 supports it and `CarlaSetup.sh` sets `-DENABLE_ROS2=ON`, **but
  the Windows `CarlaSetup.bat` does NOT** — so on the current Windows headless box ROS2/DDS is most
  likely **not built**. Treat ROS2 as Linux-only for now.
- **No built-in CoT/ADS-B emitter** exists.

### Recommended starting architecture (proposal, not yet chosen)
A **CarlaNet-side telemetry publisher** (e.g. new `CarlaNet.Telemetry`): subscribe to the world
observer → for each vehicle, `Geodesy.CarlaLocalToGeodetic` → assemble a track record → emit at 5 Hz.
Start with **CoT over UDP multicast** (simplest, TAK-compatible, broadcast) behind an `ITelemetrySink`
interface so a pub/sub or ROS2 sink can be added later. Altitude follows the §5.3 datum decision.
This keeps telemetry entirely client-side and engine-agnostic.

---

## 8. KNOWN ISSUES / HAZARDS

- **Elevation spikes** on Google-surface sampling (L-tracks/trees/awnings) — band-aided, real fix is
  bare-earth source or flat roads (§3).
- **Photogrammetry collision** ON by default → cars can wedge on the photoreal surface (use `V`, or
  reconsider the spawn default — §5.4).
- **Junction/connecting-road elevation discontinuities** (cars fall off at turns; partly the spikes).
- **Google Photoreal ToS** — demos only; real dataset needs open content.
- **Cesium v2.26 GaussianSplat crash** patched locally (engine repo, unpushed); upgrade to v2.27.0 to
  drop the patch.
- **Traffic-light grouping bug** (known, deferred): OSM worlds with TLs spam the log; we generate with
  `GenerateTrafficLights=false`.

---

## 9. KEY FILE MAP

- `CarlaNet/src/CarlaNet.Types/Geom/Geodesy.cs` — WGS84 local↔geo transform (telemetry + elevation).
- `CarlaNet/src/CarlaNet.Map/OpenDrive/ElevationInjector.cs` — extract/ToGeo/inject + outlier reject.
- `CarlaNet/src/CarlaNet.Map/OsmConverter.cs` — netconvert wrapper (+ `OsmConversionOptions.ExtraArgs`).
- `CarlaNet/src/CarlaNet.Transport/CarlaClient.cs` — all RPCs + `GenerateWorldFromOsmWithElevationAsync`
  + the world-observer actor cache (telemetry source).
- `CarlaNet/python/carlanet/__init__.py` — Python shim (`World.*`, `Client.*`).
- `CarlaNet/python/{test_digital_twin,eo_observer,test_tm_motion}.py` — drivers.
- `Unreal/.../Plugins/CesiumCarlaBridge/.../CesiumHeightSampler.{h,cpp}` — sampler + spawn + toggles +
  origin (the C++ Cesium bridge).
- `Unreal/.../Plugins/Carla/Source/Carla/Server/CarlaServer.cpp` — RPC bindings.
- `Scripts/Windows/{RunCarlaServer,OpenCarlaEditor}.ps1` — launchers. `BuildCarla.ps1` at workspace root.
