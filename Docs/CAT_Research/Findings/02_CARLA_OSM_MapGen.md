# CARLA Map / OpenStreetMap Procedural Generation — Research Brief

Scope: the **CARLA map / OSM procedural generation** pillar for blending CARLA with Cesium
to build procedural digital-twin city simulations. Grounded in source under
`g:\Projects\CarlaUE_5_7_4\carla`. All paths absolute; line refs are from the files as read on 2026-06-02.

---

## 1. OSM ingest pipeline: OSM (.osm) -> OpenDRIVE (.xodr)

CARLA does **not** generate roads directly from OSM geometry at runtime. It runs a two-stage
pipeline: **(a)** convert `.osm` -> `.xodr` via SUMO's `osm2odr`, then **(b)** build all runtime
geometry and the routing graph from the `.xodr` (see section 2). OSM is purely an authoring input.

### 1a. osm2odr (SUMO / netconvert derivative) — external dependency
- `osm2odr` is an **external library**, not vendored source in this tree. It is fetched/built only
  when `ENABLE_OSM2ODR` is set. Its hard dependencies are pulled by CMake:
  - **PROJ** (cartographic projection) — `g:\Projects\CarlaUE_5_7_4\carla\CMake\Dependencies.cmake:221-232`
  - **Xerces-C** (XML) — `...\Dependencies.cmake:236-244`
  - The build guard `WITH_OSM2ODR` / `CARLA_PYTHON_API_HAS_OSM2ODR` is set in
    `...\Plugins\Carla\Source\Carla\Carla.Build.cs` and `...\Plugins\CarlaTools\...\CarlaTools.Build.cs:17-18,60-61,76,140`.
  - Option toggle: `g:\Projects\CarlaUE_5_7_4\carla\CMake\Options.cmake`.
- This is the same upstream osm2odr that CARLA documents (SUMO-based); it converts OSM `highway`
  ways into OpenDRIVE roads/lanes/junctions and optionally synthesizes traffic lights.

### 1b. Python API surface — `carla.Osm2Odr`
- `g:\Projects\CarlaUE_5_7_4\carla\PythonAPI\carla\src\OSM2ODR.cpp`
  - `class Osm2Odr` with static `convert(osm_file, settings)` -> xodr string (lines 51-54).
  - `class Osm2OdrSettings` exposes: `use_offsets`, `offset_x/y`, `default_lane_width`,
    `elevation_layer_height`, **`proj_string`**, `center_map`, `generate_traffic_lights`,
    `all_junctions_with_traffic_lights`, plus `set_osm_way_types` / `set_traffic_light_excluded_way_types`
    (lines 36-48). The `proj_string` is the bridge to real-world georeferencing (section 3).

### 1c. UE-side OSM importer (editor tooling) — `CarlaTools`
The in-editor "OSM -> drivable map" workflow lives in **CarlaTools** (an editor plugin), not in the
runtime Carla plugin:
- `g:\Projects\CarlaUE_5_7_4\carla\Unreal\CarlaUnreal\Plugins\CarlaTools\Source\CarlaTools\Private\OpenDriveToMap.cpp`
  - `ConvertOSMInOpenDrive()` (line 146) delegates to the downloader (line 149).
- `...\CarlaTools\Private\Online\CustomFileDownloader.cpp:48-66` performs the actual call:
  ```cpp
  osm2odr::OSM2ODRSettings Settings;
  Settings.proj_string += " +lat_0=" + ... Lat_0 + " +lon_0=" + ... Lon_0;   // origin injected
  Settings.center_map = false;
  std::string OpenDriveFile = osm2odr::ConvertOSMToOpenDRIVE(OsmFile, Settings);  // -> .xodr
  ```
  So the importer injects the **map origin (lat_0/lon_0)** into the OpenDRIVE `+proj` string. This is
  the single anchor that ties the generated map to real-world geography — directly relevant to Cesium.

### Documented workflow (confirmed)
CARLA's docs ("Generate maps with OpenStreetMap") describe exactly this: export `.osm` ->
`carla.Osm2Odr.convert()` -> `.xodr` -> ingest via **OpenDRIVE Standalone Mode**
(`client.generate_opendrive_world()`). Sources:
- https://carla-ue5.readthedocs.io/en/latest/tuto_G_openstreetmap/ (UE5 fork — matches this tree)
- https://carla.readthedocs.io/en/latest/tuto_G_openstreetmap/

---

## 2. Building a navigable/renderable map from OpenDRIVE at runtime

Two runtime paths exist, both consuming `.xodr` via the same LibCarla road model.

### 2a. Parse `.xodr` -> in-memory `road::Map`
- `g:\Projects\CarlaUE_5_7_4\carla\LibCarla\source\carla\opendrive\OpenDriveParser.cpp` builds a
  `carla::road::Map` (the canonical road model: roads, lane sections, lanes, junctions, signals,
  elevation, georeference). This is the same structure your **CarlaNet.Map** port mirrors.

### 2b. Procedural mesh generation — `road::MeshFactory`
- `g:\Projects\CarlaUE_5_7_4\carla\LibCarla\source\carla\road\MeshFactory.cpp` / `.h` is the generator.
  What it produces (mesh, vertices/triangles/normals only — **no textures/UVs**):
  - **Drivable road surface** — `Generate(Road/LaneSection/Lane)` (cpp lines 34-110), tessellated
    variants `GenerateTesselated` (lines 56, 112).
  - **Sidewalks** — `GenerateSidewalk(...)` (cpp lines 232-355).
  - **Curb/boundary walls** — `GenerateWalls`, `GenerateRightWall`, `GenerateLeftWall`
    (cpp lines 357-480), height from `wall_height` param.
  - **Lane markings** — `GenerateLaneMarkForRoad`, `...ForNotCenterLine`, `...ForCenterLine`
    (cpp lines 719+).
  - Chunked/ordered emitters for large maps: `GenerateChunkedMesh`, `GenerateOrderedWithMaxLen`,
    `GenerateAllOrderedWithMaxLen` (cpp lines 481-718).
- `road::Map::GenerateChunkedMesh(...)` — `...\road\Map.cpp:1117` — is what the runtime actor calls.

### 2c. Runtime spawning of the generated geometry — `AOpenDriveGenerator`
- `g:\Projects\CarlaUE_5_7_4\carla\Unreal\CarlaUnreal\Plugins\Carla\Source\Carla\OpenDrive\OpenDriveGenerator.cpp`
  - `BeginPlay()` (line 168): loads XODR via `UOpenDrive::GetXODR`, then `GenerateAll()`.
  - `GenerateRoadMesh()` (lines 61-130): for each chunk mesh, spawns an `AProceduralMeshActor`
    backed by a `UProceduralMeshComponent`, with:
    - `bUseComplexAsSimpleCollision = true` and `SetCollisionEnabled(QueryAndPhysics)` (lines 90-91)
    - `CreateMeshSection_LinearColor(..., /*createCollision=*/true)` (lines 94-102)
    - **This is the collision surface vehicles drive on** (see section 4).
    - If `enable_mesh_visibility == false`, meshes are hidden but **collision is retained** (lines 107-113).
      => CARLA already supports an "invisible drivable road" mode (key Cesium lever).
  - `GenerateSpawnPoints()` (lines 142-159): from `Map::GenerateWaypointsOnRoadEntries()`
    (`Map.cpp:729`) it places `AVehicleSpawnPoint` actors at `ComputeTransform(waypoint)` + a fixed
    `SpawnersHeight` z offset (line 156).
  - `GeneratePoles()` (line 132) — TODO/stub.

### 2d. Routing / navigation graph
- The **routing graph is the OpenDRIVE waypoint topology itself**, not a separate nav structure.
  `road::Map` exposes `GetWaypoint`, `ComputeTransform`, lane successors/predecessors, junction
  connectivity (`Map.cpp:214-1117`). This is what TrafficManager and `agents/` route on, and what
  CarlaNet.Map ports.
- A separate Recast nav-mesh (pedestrian crowd) exists as a dependency (`Dependencies.cmake:215`,
  recastnavigation) but is independent of the road graph.

### What is NOT generated
- **No buildings.** MeshFactory emits only road/sidewalk/wall/marking geometry.
- **No ground terrain** in the runtime standalone generator (only the road ribbon).
  The CarlaTools OSM importer adds a flat default terrain plane + scattered trees, see 2e — but the
  pure standalone path (`AOpenDriveGenerator`) does not.
- No facades, no street furniture (poles stubbed), no textures/UVs.

### 2e. CarlaTools full importer (editor, asset-baking path)
- `OpenDriveToMap.cpp::GenerateAll()` (lines 508-524) produces, in order:
  `GenerateRoadMesh` -> `GenerateLaneMarks` -> `CreateTerrain(12800, 256)` (flat tile) ->
  `GenerateTreePositions` -> finish. **No building generation anywhere in this file** (grep for
  "building" returns nothing). Buildings, when present in stock CARLA towns, come from
  RoadRunner-authored content or hand-placed assets — never from the OSM/OpenDRIVE pipeline.

---

## 3. Coordinate + georeferencing model (critical for Cesium alignment)

### 3a. Local UE frame
- UE world is **left-handed, centimeters**. LibCarla geometry is right-handed-ish meters at the road
  level; conversions multiply/divide by 100 and flip Y. Example: `OpenDriveToMap.cpp:533-534`
  divides UE cm by 100 to get LibCarla meters.

### 3b. The georeference anchor lives in the `.xodr`
- The OpenDRIVE `<header><geoReference>` holds a PROJ `+proj` string. CARLA's runtime parser is
  **minimal**: it extracts ONLY `+lat_0` and `+lon_0`:
  - `g:\Projects\CarlaUE_5_7_4\carla\LibCarla\source\carla\opendrive\parser\GeoReferenceParser.cpp:28-60`
    splits on spaces/`=`, reads `+lat_0` -> latitude, `+lon_0` -> longitude; **everything else in the
    proj string is ignored**. Falls back to lat 42.0 / lon 2.0 (Barcelona) if missing (lines 51-55).
  - Stored on `road::MapData::_geo_reference` (`MapData.h:32-33,92`), surfaced via
    `road::Map::GetGeoReference()` (`Map.h:46-47`).

### 3c. UE (x,y,z) <-> lat/lon math — Mercator, NOT full PROJ at runtime
- `g:\Projects\CarlaUE_5_7_4\carla\LibCarla\source\carla\geom\GeoLocation.cpp`:
  - `GeoLocation::Transform(const Location&)` (lines 66-73) takes the map origin (lat_0/lon_0 as a
    `GeoLocation`) and adds the local offset **in meters** via spherical **Mercator**:
    `LatLonToMercator` / `MercatorToLatLon` / `LatLonAddMeters` (lines 31-64),
    `EARTH_RADIUS_EQUA = 6378137.0` (line 23).
  - **Y is inverted** (`location.x, -location.y`, line 70) to make +latitude point north.
  - Altitude is `origin.altitude + location.z` (line 67) — a flat additive offset, no geoid/ellipsoid.
- **Key implication:** at runtime CARLA does NOT use the projected coordinate system encoded in the
  full `+proj` string. It treats local meters as planar offsets from (lat_0, lon_0) under a simple
  Mercator scaled at the origin latitude. This is a **tangent-plane / Mercator approximation**, good
  for city-scale extents (errors grow with distance from origin and away from the equator).

### 3d. GNSS sensor — same transform
- `g:\Projects\CarlaUE_5_7_4\carla\Unreal\CarlaUnreal\Plugins\Carla\Source\Carla\Sensor\GnssSensor.cpp:37-85`:
  reads actor UE location, converts to global via `ALargeMapManager::LocalToGlobalLocation` (large-map
  tiling), then `CurrentGeoReference.Transform(Location)` — the **exact same Mercator path** — to emit
  lat/lon/alt (lines 45-58). Georeference seeded from `episode->GetGeoReference()` in `BeginPlay` (162-163).
- `carla.GeoLocation` (PythonAPI) is the same `geom::GeoLocation` struct (`GeoLocation.h:16-61`).

### 3e. osm2odr origin handshake
- During import, `CustomFileDownloader.cpp:51` writes `+lat_0=<Lat> +lon_0=<Lon>` into the osm2odr
  proj string with `center_map=false`. So the **same origin** flows: importer -> xodr geoReference ->
  runtime parser -> GeoLocation::Transform -> GNSS. One consistent anchor, end to end.

---

## 4. Traffic + drivable surface: does CARLA need collision geometry under the wheels?

**Answer: it depends on the TrafficManager mode. There are two distinct regimes.**

### 4a. Physics mode (default) — REQUIRES ground collision
- TrafficManager `MotionPlanStage` runs a PID controller and emits **throttle/brake/steer**:
  `g:\Projects\CarlaUE_5_7_4\carla\LibCarla\source\carla\trafficmanager\MotionPlanStage.cpp:155-217`
  (`vehicle_physics_enabled` branch -> `ApplyVehicleControl`, line 217).
- Vehicles are **Chaos wheeled vehicles** with suspension that raycasts to the ground:
  `g:\Projects\CarlaUE_5_7_4\carla\Unreal\CarlaUnreal\Plugins\Carla\Source\Carla\Vehicle\CarlaWheeledVehicle.cpp`
  uses `UChaosWheeledVehicleMovementComponent` (lines 53, 122-150), per-wheel friction/`FChaosWheelSetup`.
- Chaos wheels need a **collision surface** beneath them; the road ProceduralMesh provides it
  (collision created in `OpenDriveGenerator.cpp:90-102`). **Remove that collision and physics-mode
  vehicles fall / behave wrongly.**

### 4b. Hybrid physics mode — KINEMATIC, does NOT need collision
- `MotionPlanStage.cpp:88-129`: when in hybrid/dormant mode the stage **teleports** the vehicle by
  emitting `Command::ApplyTransform` (line 120). The pose is taken straight from the waypoint graph:
  `teleportation_transform = teleport_waypoint->GetTransform(); teleportation_transform.location.z += 0.5f;`
  (lines 112-113). **z comes from the OpenDRIVE waypoint, not from a ground raycast.**
- `ALSM.cpp:47,169-242` toggles physics on/off per actor based on distance to the hero
  (`hybrid_physics_radius`); out-of-range vehicles run kinematic and read/write transforms directly.
- In hybrid mode, vehicles ride at the **OpenDRIVE-defined elevation**, so they do not depend on
  Cesium (or any) ground mesh — but they also will NOT conform to Cesium terrain.

### 4c. Where the road z comes from
- `road::Lane::ComputeTransform(s)` derives elevation from OpenDRIVE `<elevation>` polynomials:
  `g:\Projects\CarlaUE_5_7_4\carla\LibCarla\source\carla\road\Lane.cpp:88-90,131,259-260`. Where OSM
  provided no elevation, roads are **flat (z=0 plane)** — osm2odr rarely synthesizes good elevation.

**Bottom line for Cesium:** If we delete CARLA's procedural ground/road meshes and rely on Cesium
3D Tiles for the visual ground, physics-mode AI vehicles lose their collision surface. Either
(i) keep CARLA's road collision mesh but make it invisible (`enable_mesh_visibility=false`,
already supported), or (ii) run vehicles in hybrid/kinematic mode following waypoint z, or
(iii) add collision to (or raycast against) the Cesium tileset. Each has tradeoffs in section 7.

---

## 5. The StreetMap plugin — what it is, and whether CARLA uses it at runtime

- Location: `g:\Projects\CarlaUE_5_7_4\carla\Unreal\CarlaUnreal\Plugins\StreetMap`.
- Provenance: the **Mike Fricker / ue4plugins "StreetMap"** weekend project (README line 1-11),
  vendored by CARLA via `Dependencies.cmake:268-277` (`ENABLE_STREETMAP`).
- What it does: imports `.osm` XML into a `UStreetMap` asset and generates a **UE-native primitive
  mesh** of streets **and simple buildings** directly from OSM ways/polygons:
  - `...\StreetMap\Source\StreetMapRuntime\Private\StreetMapComponent.cpp::GenerateMesh()`
    (line 228) builds road quad-strips + triangulated building footprints/roofs
    (`bWant3DBuildings`, `Buildings` loop lines 234-313).
  - README lines 87-93, 119-123: meshes are simple, single draw call, **no collision, no nav-mesh,
    no UVs**, single-precision, projected to a local plane centered on the map bbox (loses
    geographic precision after import).
- **It is a SEPARATE, UE-native OSM road-mesh generator — NOT the osm2odr/OpenDRIVE pipeline** and
  NOT the road model TrafficManager drives on.
- **Is it used in the runtime map pipeline? No.** Evidence:
  - Only `CarlaTools` (editor) references StreetMap at all
    (`grep StreetMap` across `Carla\Source` + `CarlaTools\Source` -> only `CarlaTools.Build.cs`).
  - And in `CarlaTools.Build.cs` the StreetMap modules are **commented out** (lines 112-113:
    `// "StreetMapImporting", // "StreetMapRuntime"`), i.e. not linked into the active build.
  - The runtime Carla plugin (`AOpenDriveGenerator`) never touches StreetMap.
- Verdict: **legacy/auxiliary.** It produces its own (collisionless) road+building meshes with no
  OpenDRIVE semantics, so it is not a path to drivable maps. It is only interesting to us as a
  reference for **extracting OSM building footprints** (its building triangulation code) if we ever
  want CARLA-native footprints — but Cesium supersedes that.

---

## 6. CARLA building geometry — and suppressing it for Cesium

- **The OSM/OpenDRIVE procedural pipeline never creates buildings** (sections 2d, 2e). So for any
  procedurally generated OSM map, CARLA already ships with **roads + traffic only, no buildings** —
  which is exactly the configuration we want alongside Cesium.
- Buildings in stock CARLA towns (Town01-15) are **authored content** (RoadRunner / hand-placed
  static-mesh actors), not generated. In a procedural OSM digital-twin they are simply absent.
- The StreetMap plugin *can* emit simple OSM buildings, but it is disabled (section 5) and would be
  redundant with Cesium.
- **Suppression levers already in the engine:**
  - Road/ground visual mesh can be made invisible while keeping collision:
    `OpenDriveGenerator.cpp:107-113` (`enable_mesh_visibility=false` -> `SetActorHiddenInGame(true)`).
  - Standard UE actor hiding/streaming applies to any authored building actors if present.
- => Keeping "CARLA = roads + traffic, Cesium = buildings + ground visuals" is the **natural** split:
  CARLA's procedural map is already building-free, and its road geometry can be hidden while retaining
  drivable collision.

---

## Reconciliation implications for Cesium

1. **One shared georeference anchor exists and is simple.** CARLA pins its entire local frame to a
   single `(lat_0, lon_0)` origin via Mercator (`GeoLocation.cpp:66-73`; `GeoReferenceParser.cpp:44-47`).
   To align with Cesium, set the Cesium `CesiumGeoreference` origin to the **same lat_0/lon_0** the
   OSM->xodr import used (`CustomFileDownloader.cpp:51`). Then CARLA UE meters ≈ Cesium ENU meters near
   the origin. Y-axis inversion (CARLA flips Y for north) and UE cm vs m must be reconciled in the
   transform between the two origins.

2. **Projection mismatch is the core technical hazard.** CARLA uses a spherical-Mercator tangent
   approximation scaled at origin latitude and IGNORES the full `+proj` string at runtime. Cesium 3D
   Tiles are georeferenced on the WGS84 ellipsoid (ECEF). Over a city-block these agree to sub-meter;
   over many km they **diverge** (Mercator scale distortion + ellipsoid-vs-sphere + flat altitude).
   Digital-twin alignment is good locally, degrades with extent.

3. **Altitude / terrain conformance is unsolved by CARLA.** Road z comes from OpenDRIVE `<elevation>`,
   which OSM->xodr usually leaves flat (`Lane.cpp:88-90`; flat z=0 otherwise). Cesium photorealistic
   terrain has real elevation. Flat CARLA roads will float above / sink below Cesium terrain unless we
   (a) inject real elevation into the xodr, or (b) drape/raycast roads onto the Cesium tileset.

4. **Drivable surface is the make-or-break.** CARLA's road ProceduralMesh carries the collision the
   Chaos vehicles need (`OpenDriveGenerator.cpp:90-102`). We can keep that collision and hide the
   visual (`enable_mesh_visibility=false`) so Cesium renders the look while CARLA keeps the physics
   surface. Alternatively run TrafficManager hybrid/kinematic so vehicles follow waypoint z with no
   collision at all (`MotionPlanStage.cpp:108-120`).

5. **Buildings are already absent** from procedural OSM maps — Cesium fills that gap cleanly with no
   suppression work needed (section 6).

## Key risks / open questions

- **BIGGEST RISK — Z/elevation reconciliation between flat CARLA roads and Cesium 3D terrain.**
  CARLA's OSM-generated roads are essentially planar (no real elevation), its georef altitude is a flat
  additive offset, and its drivable collision lives in those flat meshes. Cesium tiles are full-3D on
  the WGS84 ellipsoid. Without injecting real per-road elevation into the `.xodr` (or draping the road
  collision onto the Cesium tileset), roads and vehicles will not sit on the photorealistic ground —
  causing floating/clipping vehicles, broken sensor truth, and physics-mode vehicles raycasting onto
  the wrong surface. This is the single hardest reconciliation problem.
- **Mercator vs WGS84/ECEF projection drift** over large extents — quantify acceptable map radius
  (likely fine for one city, problematic for regional twins).
- **Coordinate handedness/units:** CARLA UE (left-handed, cm, Y-flipped for north) vs Cesium ENU/ECEF.
  Needs an explicit, tested transform; easy to get a mirrored or 90°-off map.
- **Collision strategy choice:** keep hidden CARLA road collision (simplest, but stays flat) vs make
  Cesium tiles collidable (Cesium for Unreal supports collision on tiles, but cooking cost + the road
  *semantics* still come only from OpenDRIVE) vs kinematic traffic (loses physics realism for ego).
- **osm2odr quality:** generated road networks are often noisy (junction artifacts, missing lanes,
  no elevation). Real digital twins frequently need manual `.xodr` cleanup — see CARLA tuning-maps docs.
- **Large maps:** UE/CARLA cap ingestible map size; `ALargeMapManager` tiling and Cesium tiling must be
  reconciled (`GnssSensor.cpp:42-45` shows local<->global tiling already in the georef path).
- **StreetMap plugin is disabled** (`CarlaTools.Build.cs:112-113`); do not rely on it as a runtime
  path. Useful only as reference code for OSM building footprint extraction if ever needed.

---

### Primary source map (quick index)
- OSM->ODR (Python): `carla\PythonAPI\carla\src\OSM2ODR.cpp`
- OSM->ODR (editor): `...\CarlaTools\...\OpenDriveToMap.cpp`, `...\Online\CustomFileDownloader.cpp:48-66`
- Deps/build flags: `carla\CMake\Dependencies.cmake:221-277`, `...\CarlaTools.Build.cs`, `...\Carla.Build.cs`
- Parse xodr: `carla\LibCarla\source\carla\opendrive\OpenDriveParser.cpp`
- Georef parse: `...\opendrive\parser\GeoReferenceParser.cpp:28-67`
- Mesh gen: `carla\LibCarla\source\carla\road\MeshFactory.cpp/.h`, `...\road\Map.cpp:1117`
- Road model + waypoint graph: `...\road\Map.cpp`, `...\road\Lane.cpp:88-90,131`
- Runtime spawn: `...\Plugins\Carla\...\OpenDrive\OpenDriveGenerator.cpp`
- Georef math: `carla\LibCarla\source\carla\geom\GeoLocation.cpp/.h`
- GNSS: `...\Plugins\Carla\...\Sensor\GnssSensor.cpp`
- Traffic control: `carla\LibCarla\source\carla\trafficmanager\MotionPlanStage.cpp`, `ALSM.cpp`
- Vehicle physics: `...\Plugins\Carla\...\Vehicle\CarlaWheeledVehicle.cpp`
- StreetMap (legacy): `...\Plugins\StreetMap\Source\StreetMapRuntime\Private\StreetMapComponent.cpp:228`
