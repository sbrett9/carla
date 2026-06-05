# CARLA × Cesium Procedural Digital‑Twin Simulation — Options & Feasibility Study

**Prepared:** 2026‑06‑02
**Goal:** Blend CARLA, Cesium, OpenStreetMap, Unreal Engine 5.7.4 and CarlaNet into a system that
**procedurally generates digital‑twin city simulations**, renders **high‑altitude electro‑optical (EO)
drone/satellite‑style video**, and exports **georeferenced, truthed traffic telemetry** for training a
downstream detection model (the model itself is out of scope).

**Engine target:** CARLA‑patched UE 5.7.4 at [UE_5_7_4/](UE_5_7_4/) · **CARLA project:** [carla/](carla/) ·
**Scripting/runtime client:** CarlaNet at [carla/CarlaNet/](carla/CarlaNet/)

> This report was produced by a three‑agent research team. The full per‑pillar briefs are retained at
> [01_Cesium_Integration.md](01_Cesium_Integration.md),
> [02_CARLA_OSM_MapGen.md](02_CARLA_OSM_MapGen.md), and
> [03_Georef_Sensors_Telemetry.md](03_Georef_Sensors_Telemetry.md).

---

## 1. Verdict (TL;DR)

**Feasible — with one hard constraint and three solvable engineering problems.**

The technology stack is sufficient. Every major piece already exists in this workspace: Cesium for
Unreal is *already bundled in the engine build*, CARLA's procedural OSM map pipeline already produces
exactly "roads + traffic, no buildings" (the ideal split for letting Cesium supply the scenery), the
high‑altitude nadir camera rig already exists in [CaptureVideo.py](CaptureVideo.py), and CarlaNet
already exposes the full scenario/traffic/sensor API needed to drive everything from C#/Python.

The blockers, in order of severity:

| # | Issue | Severity | Resolution |
|---|-------|----------|------------|
| **1** | **Google Photorealistic 3D Tiles ToS forbids training‑data / non‑visualization use** | 🔴 **Hard constraint** | Build the actual dataset on **open content** (Cesium OSM Buildings / self‑hosted photogrammetry). Reserve Google tiles for demos only. |
| **2** | **Z / elevation reconciliation** — flat CARLA roads vs full‑3D Cesium terrain | 🟠 Solvable | Keep CARLA's road collision mesh (invisible) as the drivable ground; OR inject real elevation into the `.xodr`; OR drape onto Cesium tiles. |
| **3** | **Spherical‑Mercator (CARLA) vs WGS84 ellipsoid (Cesium)** georef mismatch | 🟠 Solvable | Use a shared local‑tangent‑plane (ENU) origin; keep the city small/near origin; let Cesium own ellipsoid math. |
| **4** | **Async tile streaming vs deterministic fixed‑step capture** | 🟠 Solvable | Gate each frame capture on Cesium's "tiles‑loaded" signal; pre‑warm/cache from a self‑hosted tileset. |

None of 2–4 is a research‑grade unknown; each has a concrete, documented mitigation below. Issue 1 is a
**legal/licensing** constraint, not a technical one — it dictates *which* 3D content you may train on,
and the answer is "not Google's." With an open content source, the whole pipeline is buildable.

---

## 2. The core architectural problem: two georeferenced worlds in one Unreal level

CARLA and Cesium are both "the world," and they disagree about three things — **who renders the
ground/buildings, what the drivable surface is, and where a given lat/lon actually lands in UE space.**
The entire design reduces to reconciling those three axes:

```
                         ┌──────────────────────────────────────────────┐
                         │              One UE 5.7.4 level               │
                         │                                              │
   CARLA owns:           │   Cesium owns:                               │
   • OpenDRIVE road net  │   • Photoreal/OSM buildings (3D Tiles)       │
   • Traffic + walkers   │   • Terrain + imagery                        │
   • Vehicle physics     │   • Globe georeference (WGS84 ellipsoid)     │
   • Drivable collision  │   • Floating‑origin rebasing                 │
   • Sensors (EO camera) │                                              │
   • Truth telemetry     │                                              │
   (spherical Mercator)  │                                              │
                         └──────────────────────────────────────────────┘
        The reconciliation = a single shared geodetic origin + a unit/handedness transform
```

The **good news from the source survey:** the split is *natural*, not forced. CARLA's procedural OSM
pipeline (`MeshFactory` → `AOpenDriveGenerator`) emits **only** road surface, sidewalks, curb walls and
lane markings — **never buildings, never real terrain**
([carla/.../road/MeshFactory.cpp](carla/LibCarla/source/carla/road/MeshFactory.cpp),
[carla/.../OpenDrive/OpenDriveGenerator.cpp](carla/Unreal/CarlaUnreal/Plugins/Carla/Source/Carla/OpenDrive/OpenDriveGenerator.cpp)).
So Cesium fills the building/terrain gap with **zero suppression work**. And CARLA already supports an
**invisible‑but‑collidable road** mode (`enable_mesh_visibility=false` keeps collision, hides the visual),
which is the key lever for letting Cesium render the look while CARLA keeps the physics.

---

## 3. Technology‑stack feasibility assessment

| Component | Status in this workspace | Verdict |
|-----------|--------------------------|---------|
| **Unreal Engine 5.7.4** | CARLA‑patched custom build; Large‑World‑Coordinates (double precision) on by default; `UWorld::SetNewWorldOrigin` rebasing confirmed in source. | ✅ Sufficient. LWC makes a fixed city‑scale origin viable without per‑frame rebasing. |
| **Cesium for Unreal** | **Already bundled**: `…/Engine/Plugins/Marketplace/Cesiumfo9eaf76ca58f3V10/` **v2.26.0**, Apache‑2.0. Officially supports UE 5.5/5.6/5.7. | ✅ Sufficient. ⚠️ May need recompiling **from source** against the CARLA‑patched engine (binary built vs stock Epic). |
| **CARLA (OSM→OpenDRIVE)** | `carla.Osm2Odr.convert()` (SUMO `osm2odr`) → `.xodr` → `AOpenDriveGenerator` builds roads + waypoint graph at runtime. | ✅ Sufficient. Produces roads+traffic only — ideal. ⚠️ osm2odr output is noisy + flat (no elevation). |
| **OpenStreetMap** | Authoring input only — converted to OpenDRIVE offline; **not** used live. StreetMap plugin is **disabled** (legacy). | ✅ As an authoring source. ❌ StreetMap UE plugin is not a runtime path. |
| **CarlaNet (.NET 10)** | Complete libcarla replacement: RPC client, TrafficManager, road graph (`CarlaNet.Map`), walker AI (`CarlaNet.Nav`), sensor deserializers. 76/76 tests pass. | ✅ Sufficient to script scenarios, drive traffic, fly the drone ego, and pull truth. ⚠️ Lacks the Mercator local→geo transform (≈30 lines to port). |
| **High‑altitude EO camera** | `sensor.camera.rgb` with no parent at altitude, `pitch=-90` — **already implemented** in [CaptureVideo.py](CaptureVideo.py) (`--overhead`). No far‑clip problem (reversed‑Z infinite far). | ✅ Sufficient. ⚠️ Needs sync‑mode for deterministic 1:1 image↔truth pairing. |
| **Truthed telemetry** | World‑observer stream → per‑actor transform/velocity/accel; static bbox from `rpc::Actor`; 2D projection via pinhole `K`. All exposed by CarlaNet. | ✅ Sufficient. The full bbox‑to‑pixel math is pure client‑side. |

**Overall: the stack is sufficient for the task.** No component is missing a capability that forces a
redesign; the work is integration and reconciliation, not invention.

---

## 4. Option analysis (the decisions you actually have to make)

### 4.1 Content source — *who supplies the photoreal buildings/terrain?* (drives the legal answer)

| Option | Fidelity | Licensing for ML training data | Determinism / offline | Recommendation |
|--------|----------|-------------------------------|------------------------|----------------|
| **A. Google Photorealistic 3D Tiles** | Highest (real photogrammetry) | 🔴 **Prohibited** — Google Maps Platform ToS bars non‑visualization use (object detection, geodata extraction, derived measurements), offline use, and caching. | ❌ Cannot cache | **Demos only.** Do **not** generate training pixels from it. |
| **B. Cesium OSM Buildings** | Medium (extruded OSM prisms, untextured) | 🟢 **ODbL — open**, derivative/commercial OK with attribution | 🟡 ion‑brokered; cacheable | ✅ **Primary** for v1 dataset. Boxy geometry actually reads plausibly as a stylized nadir EO map. |
| **C. Self‑hosted 3D Tiles** (your own photogrammetry, CityGML, USGS 3DEP/LiDAR meshes) | High (you control it) | 🟢 **Full rights** — no third‑party ToS | 🟢 Serve from localhost → fully deterministic, offline, cacheable | ✅ **Best long‑term.** The clean path to a redistributable, high‑fidelity, reproducible dataset. |

> **This is the single most consequential decision in the project.** The premise "render Google's
> photoreal city and export truth telemetry to train a detector" is, as literally stated, almost
> certainly a Google ToS violation *even if you pay for the tiles*. Overlaying your CARLA vehicles on
> Google tiles is allowed; turning the underlying imagery into a training dataset is not. Plan the
> dataset pipeline on **B and/or C** from day one. Use Google tiles only for non‑training visual demos.

**Nadir‑fidelity nuance (independent of licensing):** Google tiles are built from *oblique* aerial
capture and look "melted/smeared" from straight down and muddy below ~500 m — a real problem for a
true nadir EO look. At 500 m–10 km the coarse‑LOD regime is mostly acceptable, and a *slightly oblique*
view looks markedly better than pure nadir. Clean extruded geometry (Option B) or your own
photogrammetry (Option C) is often a *better* match for stylized EO/SAR‑like synthetic data anyway.

### 4.2 Drivable surface — *what do the wheels touch?*

CARLA traffic runs in one of two regimes; the choice changes whether you need ground collision at all:

| Option | How it works | Needs ground collision? | Terrain conformance | Recommendation |
|--------|--------------|--------------------------|---------------------|----------------|
| **A. Keep CARLA road collision, hide the visual** (`enable_mesh_visibility=false`) | Physics‑mode Chaos vehicles raycast suspension onto CARLA's invisible road mesh; Cesium renders the look on top. | ✅ Yes (CARLA's own, hidden) | Roads stay at CARLA's (flat) z — must align with Cesium ground | ✅ **Default.** Lowest risk, full vehicle physics, sensors see Cesium. |
| **B. Hybrid / kinematic TrafficManager** | `MotionPlanStage` teleports vehicles to waypoint poses, z from OpenDRIVE — no raycast, no collision needed. | ❌ No | Rides OpenDRIVE z (still flat unless elevation injected) | 🟡 Good for dense background traffic; loses physics realism for the studied vehicles. |
| **C. Make Cesium tiles collidable, raycast onto them** | Enable collision on the Cesium tileset; vehicles drive on real 3D terrain. | ✅ Yes (Cesium's) | 🟢 True terrain conformance | 🟠 Highest fidelity but: cooking cost, async‑collision timing, and **road *semantics* still come only from OpenDRIVE** — geometry ≠ lane graph. |

The realistic v1 answer is **A + B**: hidden CARLA road collision for the hero/studied vehicles, hybrid
kinematic for distant ambient traffic (the regime CarlaNet's ported TrafficManager already supports).

### 4.3 Elevation reconciliation — *do roads sit on the Cesium ground?* (Issue 2)

CARLA's OSM‑generated roads are essentially **planar** (osm2odr rarely synthesizes elevation; road z
falls back to a flat plane — [carla/.../road/Lane.cpp](carla/LibCarla/source/carla/road/Lane.cpp)), and
CARLA's georef altitude is a *flat additive offset* with no geoid/ellipsoid. Cesium terrain has real 3D
elevation. Left unreconciled, vehicles float above or sink into the photoreal ground. Options:

1. **Sample Cesium's surface height into the `.xodr` `<elevation>`** — at map‑bake time (an offline pass),
   for each OpenDRIVE road's centerline sample points (s‑coordinates) — or each lane reference point —
   query the Cesium tileset's surface height at that point's lat/lon, then write the sampled heights into
   the OpenDRIVE `<elevation>` cubic‑polynomial records per geometry segment. CARLA's road model already
   reads those elevation polynomials (`road::Lane::ComputeTransform`), so the generated road mesh +
   drivable collision then conform to Cesium's ground **by construction**. Cesium exposes a batch async
   "sample height most detailed" facility for exactly this: `UCesiumSampleHeightMostDetailedAsyncAction`
   / `ACesium3DTileset::SampleHeightMostDetailed(LongitudeLatitudeHeightArray, …)` (cesium‑native
   `Cesium3DTilesSelection::Tileset::sampleHeightMostDetailed` under the hood), which returns sampled
   heights + per‑point success flags for a set of cartographic positions. **Key advantage over Option 2:**
   because Cesium is *both* the rendered ground *and* the elevation source, the road can never contradict
   the terrain you see — whereas an independent DEM (NASADEM/SRTM ~30 m posting) risks (a) disagreeing
   with Cesium's terrain and (b) being too coarse to grade individual streets. Sampling Cesium sidesteps
   both, and works for **self‑hosted** tiles, not just Cesium ion content. **Tradeoffs:** the relevant
   tiles must be streamed/loaded to sufficient LOD at sample time (async) — so it is best run as an
   offline pre‑bake; sampled precision is bounded by tile LOD; and it is one‑directional (roads follow
   Cesium, you are not changing Cesium). (A complementary runtime facility — `UCesiumGlobeAnchorComponent`
   with `HeightReference = Tileset`, `ReferencedTileset`, `HeightUpdateInterval`, added v2.25.0 — clamps an
   *actor's* height onto a referenced tileset, useful for props but not for baking road geometry.)
2. **Flatten Cesium locally** — clamp/flatten the Cesium terrain under the road network (Cesium supports
   polygon‑clipping/cartographic‑polygon rasterization to flatten terrain). Simplest; works for flat cities.
3. **Inject real elevation into the `.xodr`** `<elevation>` records from an *independent* DEM (USGS 3DEP,
   Copernicus) at OSM‑import time, so CARLA roads follow real terrain. High fidelity, but risks
   disagreeing with Cesium's own terrain (Option 1 avoids this) and is the most work.
4. **Drape road collision onto Cesium** — raycast/snap the CARLA road mesh vertices onto the collidable
   Cesium tileset at load. Couples the two systems; needs collision on tiles.

For a first city (typically near‑flat downtown, e.g. Wrigleyville/Chicago), **Option 2 (flatten Cesium
under the roads)** remains the pragmatic starting point. For terrain conformance on hilly sites (e.g. San
Francisco), **Option 1 (sample Cesium tileset height into the `.xodr` `<elevation>`)** is the preferred
path — contradiction‑free by construction — and supersedes the independent‑DEM option (Option 3).

### 4.4 Georeferencing alignment — *where does a lat/lon land in UE?* (Issue 3)

The transform chain (from the georef brief) is concrete and small:

```
CARLA actor (x,y,z) [m, left‑handed, Y‑right]
   └─► GeoLocation::Transform  (spherical Mercator, Y‑flip: north = −Y, alt = alt0 + z)
          → (lat, lon, h)  [WGS84‑ish degrees]
                └─► Cesium: set CesiumGeoreference.Origin{Latitude,Longitude,Height}
                            == CARLA .xodr <geoReference> +lat_0 / +lon_0 / datum
                       → Cesium maps (lon,lat,h) → ECEF → UE (its own floating origin)
```

**What must match:** the Cesium georeference origin **must equal** the `.xodr` `+lat_0/+lon_0` that the
OSM→OpenDRIVE import injected ([carla/.../Online/CustomFileDownloader.cpp](carla/Unreal/CarlaUnreal/Plugins/CarlaTools/Source/CarlaTools/Private/Online/CustomFileDownloader.cpp)).
Units convert at every UE boundary (UE cm ↔ CARLA m, ×/÷100); the only handedness flip you own is the
`-Y` already baked into `GeoLocation::Transform`.

**The catch:** CARLA uses a *spherical* Web‑Mercator (R = 6378137), Cesium uses the *WGS84 ellipsoid*.
Over a city block they agree to sub‑meter; over many km they diverge (tens–hundreds of meters at
mid/high latitude — likely larger than your GSD). Mitigations, best first:

- **(Recommended) Treat CARLA local meters as a local ENU tangent plane and let Cesium do the
  ellipsoidal ENU→ECEF**, bypassing CARLA's spherical Mercator entirely. Cleanest reconciliation.
- Keep the simulated city **small and near the origin** so the error stays sub‑GSD.
- Replace CARLA's spherical formula with a proper ellipsoidal projection in the *truth* path so both
  systems agree numerically.

> ⚠️ **CarlaNet gap (small):** CarlaNet ports the georef *origin* parser
> ([CarlaNet.Map/.../GeoReferenceParser.cs](carla/CarlaNet/src/CarlaNet.Map/OpenDrive/Parser/GeoReferenceParser.cs))
> but **not** the Mercator local→geo math. To emit lat/lon truth client‑side, port the ~30 lines from
> [GeoLocation.cpp](carla/LibCarla/source/carla/geom/GeoLocation.cpp) (or implement the ENU‑tangent
> approach above). Trivial, but currently missing.

### 4.5 Determinism — *async tile streaming vs fixed‑step capture* (Issue 4)

CARLA runs synchronous, fixed‑`delta_seconds`, often headless, and demands reproducibility. Cesium
streams tiles **asynchronously over the network** on worker threads. Naively, frame N can capture holes
or wrong‑LOD because tiles haven't finished loading. Resolution pattern:

1. Register the off‑screen EO capture camera with **`ACesiumCameraManager`** (`SceneCaptures` /
   `AdditionalCameras`, v2.24.0+) so the tile selector streams for the *capture* viewpoint even with no
   player viewport (headless `-RenderOffScreen`).
2. Per fixed step: advance CARLA physics → move the Cesium capture camera → **block the game thread until
   Cesium reports tiles fully loaded** for that camera at the target screen‑space error → capture frame
   + read truth → advance tick. Gate on *loaded state*, not a fixed tick count (timing is network‑bound).
3. **Pre‑warm / fully offline:** with a **self‑hosted tileset (Option 4.1‑C)** you can serve from
   localhost and pre‑populate Cesium's request cache → true determinism and repeatability. (Google's
   caching prohibition blocks this for Google tiles — another reason to self‑host.)

Disable Cesium's continuous per‑frame origin shifting (`UCesiumOriginShiftComponent` → `Mode=disabled`
or a large `Distance`) and pin a **fixed georeference origin at city center**; UE 5.7 LWC double
precision makes a tens‑of‑km fixed origin safe. This also keeps truth export stable and reconciles with
CARLA's `ALargeMapManager` rebasing (Issue: two floating‑origin systems — pick one; for a single city,
disable both and use a fixed origin).

---

## 5. Recommended architecture (synthesis)

```
┌─────────────────────────── AUTHORING (offline) ───────────────────────────┐
│  OSM extract ──► carla.Osm2Odr.convert() ──► .xodr  (inject +lat_0/+lon_0) │
│                                   │                                        │
│        DEM (USGS 3DEP/Copernicus) ─┴─► (optional) inject <elevation>        │
│  Self‑host / select 3D Tiles  (Cesium OSM Buildings  or  own photogrammetry)│
└────────────────────────────────────────────────────────────────────────────┘
                                   │
┌──────────────────────────── RUNTIME (UE 5.7.4) ───────────────────────────┐
│  CARLA server (headless, sync mode, fixed Δt)                             │
│    • AOpenDriveGenerator: roads + waypoint graph, enable_mesh_visibility=0 │
│      (invisible collision = drivable surface)                             │
│    • Cesium: Cesium3DTileset (buildings+terrain) + CesiumGeoreference      │
│      origin == .xodr +lat_0/+lon_0, fixed origin, continuous shift OFF     │
│    • Flatten Cesium terrain under road net  (v1)                          │
│                                                                            │
│  CarlaNet (C#/Python) drives the scenario:                                │
│    • spawn + autopilot traffic (ported TrafficManager) + walkers (Nav)    │
│    • fly the high‑altitude EO drone (free sensor.camera.rgb, no parent)    │
│    • sync‑mode tick loop, gate on Cesium tiles‑loaded, then capture        │
└────────────────────────────────────────────────────────────────────────────┘
                                   │
┌──────────────────────────── OUTPUT (per frame) ───────────────────────────┐
│  • EO frame  (BGRA → luma/EO post‑process; haze/exposure tuned)           │
│  • Truth:  per actor  {id, type/label, world transform, velocity,         │
│            lat/lon/alt, 3D bbox, 2D image bbox(pixels)}  keyed by frame id │
└────────────────────────────────────────────────────────────────────────────┘
```

**Why this shape:** it leans entirely on capabilities that already exist. CARLA stays the authority for
roads/traffic/physics/sensors/truth (the things it's good at and CarlaNet already controls), Cesium is a
*pure visual + georeference* layer, and the two meet at exactly one number — the shared geodetic origin.

---

## 6. Sensor & telemetry pipeline (the dataset itself)

This part of the stack is the **most mature** — little new work, mostly wiring.

**High‑altitude EO drone:** a free `sensor.camera.rgb` at altitude with `pitch=-90` (nadir) or a small
negative pitch (oblique, better for photoreal tiles). Already implemented in
[CaptureVideo.py](CaptureVideo.py) (`--overhead`). FOV/resolution are uncapped attributes; UE's
reversed‑Z infinite far plane means km‑altitude views **do not clip**. Move the rig each tick via
`SetActorTransform` to fly a trajectory. The 48‑byte sensor header carries the camera's world transform
*at capture time* — i.e. extrinsics come free with every frame.

**GSD control:** `GSD ≈ (2·altitude·tan(FOV/2)) / image_width`. e.g. 2000 m, FOV 30°, 4096 px →
~0.26 m/px swath ≈ 1072 m. Pick FOV/resolution to match the real EO sensor's GSD.

**EO look:** grayscale/panchromatic via client‑side luma or a post‑process material (the project already
ships a PP/segmentation material pipeline); atmospheric haze via `WeatherParameters` (Fog*, Mie/Rayleigh
scattering); sun azimuth/altitude for correct shadows. Pin **manual exposure** for repeatable radiometry
(auto‑exposure calibration is a deprecated no‑op in this UE build). True NIR spectral response + sensor
noise/MTF are custom add‑ons if radiometric fidelity matters; for detector training, geometric + haze +
grayscale is usually enough.

**Truth telemetry per frame (time‑synced by frame id):**

- **Pose/kinematics:** world‑observer stream → every actor's transform, velocity, angular velocity,
  acceleration ([CarlaNet.Sensors/EpisodeStateSensorData.cs](carla/CarlaNet/src/CarlaNet.Sensors/EpisodeStateSensorData.cs),
  cache in [CarlaClient.cs](carla/CarlaNet/src/CarlaNet.Transport/CarlaClient.cs)).
- **Label/type:** `rpc::Actor.Description.Id` (e.g. `vehicle.tesla.model3`) + `SemanticTags`, joined by actor id.
- **Lat/lon/alt:** run each actor's world location through the §4.4 transform.
- **3D bbox:** ⚠️ the observer cache **does not** carry bounding boxes — fetch the *static local* box
  once from `rpc::Actor.BoundingBox` and compose with the per‑frame world transform
  (`world_corner = ActorTransform ∘ (bbox.Location ± bbox.Extent)`).
- **2D image bbox (pixels):** pure client‑side pinhole projection — `focal = w/(2·tan(fov·π/360))`,
  `K = [[f,0,w/2],[0,f,h/2],[0,0,1]]`, `w2c = inverse(camera_world_transform)`, UE→CV axis swap
  `(x,y,z)→(y,-z,x)`, cull points behind camera. This is CARLA's documented `get_image_point` path,
  reproduced on CarlaNet‑fed data — no server feature required.

**Wiring to fix for clean labels:** switch capture from async `wait_for_tick()` to **sync‑mode tick +
sensor queue** for deterministic 1:1 image↔truth pairing; start the CarlaNet world‑observer and wait one
tick before trusting the cache.

---

## 7. CarlaNet's role — fast scenario authoring

CarlaNet is what makes "quickly put together new scenarios" real. It already provides, in managed code:
spawn/despawn, autopilot + the full ported **TrafficManager** (7 AI stages, road graph in
`CarlaNet.Map`), **walker AI** (`CarlaNet.Nav`), sensor subscription/deserialization, weather, and sync
ticking — all importable from Python via `import carlanet as carla` (near drop‑in for the CARLA API).
A scenario becomes a short C#/Python script: choose the city `.xodr` + matching Cesium origin, set
weather/time‑of‑day, spawn N vehicles + M walkers on the OpenDRIVE graph, define the drone flight path,
run the deterministic capture loop. The **only CarlaNet addition needed** for this project is the ~30‑line
Mercator (or ENU‑tangent) local→geo transform for client‑side lat/lon truth (§4.4).

---

## 8. Feasibility risk register

| ID | Risk | Sev | Mitigation | Owner pillar |
|----|------|-----|------------|--------------|
| **R1** | Google 3D Tiles ToS bars training‑data use | 🔴 | Use Cesium OSM Buildings / self‑hosted photogrammetry for the dataset; Google = demos only | Content/legal |
| **R2** | Flat CARLA roads vs 3D Cesium terrain (float/clip) | 🟠 | Flatten Cesium under roads (v1, flat sites); for hilly sites, sample Cesium tileset height (`SampleHeightMostDetailed`) into the `.xodr` `<elevation>` rather than an independent DEM, so roads conform to Cesium by construction | Map |
| **R3** | Spherical‑Mercator vs WGS84 projection drift | 🟠 | Shared ENU tangent‑plane origin; keep city near origin; Cesium owns ellipsoid | Georef |
| **R4** | Async tile streaming breaks fixed‑step determinism | 🟠 | Register capture cam with `ACesiumCameraManager`; gate capture on tiles‑loaded; self‑host + pre‑warm cache | Cesium/capture |
| **R5** | Two floating‑origin systems (CARLA `ALargeMapManager` + Cesium origin shift) collide at altitude | 🟠 | Disable both; fixed city‑center origin (UE 5.7 LWC makes this safe) | Engine |
| **R6** | Cesium plugin (binary v2.26.0) may not link against CARLA‑patched engine | 🟡 | Rebuild cesium‑unreal + cesium‑native from source against this engine; validate early | Build |
| **R7** | CarlaNet lacks local→geo transform | 🟢 | Port ~30 lines from `GeoLocation.cpp` (or ENU approach) | CarlaNet |
| **R8** | bbox not in observer cache | 🟢 | Fetch static `rpc::Actor.BoundingBox` once; compose with per‑frame transform | Telemetry |
| **R9** | Default georef origin silently = Barcelona (42,2) if `.xodr` lacks `+lat_0/+lon_0` | 🟢 | Always author `<geoReference>` explicitly; assert Cesium origin == `.xodr` origin | Georef |
| **R10** | osm2odr road networks are noisy (junction artifacts, missing lanes) | 🟡 | Manual `.xodr` cleanup per CARLA tuning‑maps docs; or author key corridors in RoadRunner | Map |
| **R11** | Google/photoreal tiles look "melted" from pure nadir | 🟡 | Use slight obliquity; or clean extruded geometry (reads better as stylized EO) | Cesium |
| **R12** | ion commercial licensing + per‑tile cost at dataset scale | 🟡 | Self‑host open tiles → no per‑request billing; model volume before committing to ion | Content/cost |
| **R13** | GBuffer/depth aux streams not forward‑ported in this UE5.7.4 build | 🟢 | RGB + semantic segmentation work; no cheap per‑pixel depth truth from the drone cam (not needed for EO+bbox) | Sensor |

---

## 9. Recommended proof‑of‑concept roadmap

A phased plan that retires the biggest risks first and produces a usable artifact early:

1. **Phase 0 — Build validation (retire R6).** Recompile cesium‑unreal + cesium‑native from source
   against the CARLA‑patched UE 5.7.4. Confirm a `Cesium3DTileset` + `CesiumGeoreference` renders inside
   the CARLA editor/PIE. *Exit:* Cesium tiles visible in a CARLA map.
2. **Phase 1 — Georef alignment (retire R3/R9).** Import one small, flat city via OSM→`.xodr` with an
   explicit `<geoReference>`. Set the Cesium origin to the same lat0/lon0. Spawn a few vehicles; verify
   they land on the correct streets under the Cesium buildings (visually and by lat/lon round‑trip).
   *Exit:* CARLA roads + Cesium buildings co‑register to sub‑GSD near the origin.
3. **Phase 2 — Drivable surface + elevation (retire R2).** Set `enable_mesh_visibility=false`; flatten
   Cesium terrain under the road net; confirm physics‑mode traffic drives correctly with Cesium scenery
   on top and no float/clip. *Exit:* traffic flows on invisible roads beneath photoreal buildings.
4. **Phase 3 — Deterministic EO capture (retire R4/R5).** Register the nadir drone camera with
   `ACesiumCameraManager`; pin a fixed origin; switch to sync mode; gate capture on tiles‑loaded.
   *Exit:* reproducible EO frames at altitude, no tile holes, identical across reruns.
5. **Phase 4 — Truth pipeline (retire R7/R8).** Port the local→geo transform into CarlaNet; emit per‑frame
   {id, label, transform, velocity, lat/lon, 3D bbox, 2D pixel bbox}. Validate 2D boxes overlay correctly
   on the EO frames. *Exit:* a labeled EO frame + truth JSON pair.
6. **Phase 5 — Open‑content dataset (retire R1/R12).** Swap demo Google tiles for Cesium OSM Buildings or
   self‑hosted photogrammetry; verify offline/cached determinism; generate a first batch. *Exit:* a
   legally‑clean, reproducible EO+truth dataset, scriptable into new scenarios via CarlaNet.

---

## 10. Bottom line

The vision is **technically sound and largely pre‑assembled** in this workspace. The stack — UE 5.7.4 +
Cesium + CARLA/OSM + CarlaNet — is sufficient; the work is *reconciliation and wiring*, not new research.
Three engineering problems (elevation, projection, determinism) each have a concrete, documented fix, and
the sensor/telemetry half is nearly turnkey. The **one decision that shapes everything** is content
licensing: train on **open or self‑hosted 3D tiles**, not Google's — and the system becomes a flexible,
reproducible, georeferenced EO digital‑twin generator that CarlaNet can drive into new scenarios on demand.

---

### Appendix — primary source index

- **Cesium:** bundled plugin `…/Engine/Plugins/Marketplace/Cesiumfo9eaf76ca58f3V10/` (v2.26.0); UE
  `World.h` origin rebasing (`OriginLocation`, `SetNewWorldOrigin`); APIs `ACesium3DTileset`,
  `ACesiumGeoreference`, `UCesiumGlobeAnchorComponent`, `ACesiumCameraManager`, `UCesiumOriginShiftComponent`.
- **CARLA map/OSM:** [PythonAPI/.../OSM2ODR.cpp](carla/PythonAPI/carla/src/OSM2ODR.cpp),
  [CarlaTools/.../OpenDriveToMap.cpp](carla/Unreal/CarlaUnreal/Plugins/CarlaTools/Source/CarlaTools/Private/OpenDriveToMap.cpp),
  [road/MeshFactory.cpp](carla/LibCarla/source/carla/road/MeshFactory.cpp),
  [OpenDrive/OpenDriveGenerator.cpp](carla/Unreal/CarlaUnreal/Plugins/Carla/Source/Carla/OpenDrive/OpenDriveGenerator.cpp),
  [opendrive/parser/GeoReferenceParser.cpp](carla/LibCarla/source/carla/opendrive/parser/GeoReferenceParser.cpp).
- **Georef/sensors/telemetry:** [geom/GeoLocation.cpp](carla/LibCarla/source/carla/geom/GeoLocation.cpp),
  [Sensor/GnssSensor.cpp](carla/Unreal/CarlaUnreal/Plugins/Carla/Source/Carla/Sensor/GnssSensor.cpp),
  [Sensor/SceneCaptureSensor.cpp](carla/Unreal/CarlaUnreal/Plugins/Carla/Source/Carla/Sensor/SceneCaptureSensor.cpp),
  [CaptureVideo.py](CaptureVideo.py), [CarlaNet.md](CarlaNet.md),
  [CarlaNet.Transport/CarlaClient.cs](carla/CarlaNet/src/CarlaNet.Transport/CarlaClient.cs).
- **Full research briefs:** [01_Cesium_Integration.md](01_Cesium_Integration.md),
  [02_CARLA_OSM_MapGen.md](02_CARLA_OSM_MapGen.md),
  [03_Georef_Sensors_Telemetry.md](03_Georef_Sensors_Telemetry.md).
</content>
</invoke>
