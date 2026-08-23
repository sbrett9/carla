# Saving a generated world as an editable Unreal level

**Date:** 2026-08-23 · **Status:** research complete, no code changed. Development plan in §11.
**Question:** the world `run_SCTMV.py` builds is ephemeral — generated per run from an OSM extract and
discarded. Can it be persisted as an Unreal level that a person opens in the editor, edits by hand, and
then loads back into the simulator by name? And what artifact would that level be?
**Answer in one line:** yes, and the geometry is the easy part — the hard part is that the world's
*datum* lives only in the client process, so a naively saved level loads successfully and reports wrong
altitude truth with no error.

**Relates to:** [22_Digital_Twin_Feature_Port.md](22_Digital_Twin_Feature_Port.md) (its §11 guard rails
are binding here), [21_Road_Elevation_Profile_Continuity.md](21_Road_Elevation_Profile_Continuity.md)
(the road surface this must bake), [08_Layer_Architecture.md](08_Layer_Architecture.md),
[15_Automated_Build_Distribution_Pipeline.md](15_Automated_Build_Distribution_Pipeline.md) (where a
level would slot into the cook), [17_Photoreal_Occlusion_Metric.md](17_Photoreal_Occlusion_Metric.md).
**Code:** `Carla/OpenDrive/OpenDriveGenerator.*`, `Carla/BlueprintLibary/MapGenFunctionLibrary.*`,
`CesiumCarlaBridge/{DrapedTerrain,StagingBounds,CesiumHeightSampler}.*`,
`CarlaNet.Transport/CarlaClient.cs`, `CarlaControl/src/carlacontrol/WorldBuilder.py`,
`CarlaTools/.../OpenDriveToMap.cpp` (the reference implementation of this workflow).
**Survey basis:** HEAD on `feature/JNI-347-road-mesh-elevation-profile-continuity`, 2026-08-23.

---

## 1. What the world is actually made of

`OpenDriveMap.umap` contains **four actors**: `OpenDriveGenerator_2`, `PlayerStart_1`,
`DirectionalLight_1`, `SkyLight_1`, plus the level Blueprint. There is no Cesium georeference, no
tileset, no traffic-light manager, no draped terrain, no staging bounds. Every one of those is spawned
at runtime by an RPC handler.

The build sequence is one client call — `WorldBuilder.build_world` →
`client.generate_world_from_osm_with_elevation(...)` — which converts, samples, injects and builds
server-side, followed by a series of configuration RPCs the client issues *after* the level reload
(`configure_cesium_georeference`, `set_layer_offset`, `build_draped_terrain`, `set_staging_bounds`).

**So the world is a runtime construction over an almost-empty map.** Persisting it is not "save what's
there"; it is "decide what the level should contain instead of those RPCs".

Exactly one thing is already durable: the elevated `.xodr`, written to
`Build/sumo-smoketest/<name>_elevated.xodr` by the client and to
`Content/Carla/Maps/OpenDrive/OpenDriveMap.xodr` by the server.

---

## 2. Serialization audit

What survives a level save today, and the minimal fix where it does not. **Y** = state fully recovered,
**P** = actor survives with some state lost, **N** = state lost.

| Actor / component | State holder | Survives? | Note and fix |
|---|---|---|---|
| `AProceduralMeshActor` + `UProceduralMeshComponent` | `UPROPERTY() TArray<FProcMeshSection> ProceduralMeshSections` (`ProceduralMeshComponent.h:333`) | **Y** state, **N** viability | The geometry genuinely round-trips — it is absent from saved levels only because the actor is runtime-spawned. But measured on `Arapahoe_I25`: **18 pieces, 1,043,465 vertices, 1,977,154 triangles, 46 s**. `FProcMeshVertex` is eight tagged properties with no custom serializer, so an editor `.umap` would store it as tagged properties per vertex — inferred **200–300 MB**. No Nanite, no LODs, no lightmap UVs. Bake to `UStaticMesh`. |
| `AOpenDriveGenerator` | `OpenDriveData`, `ActorMeshList`, `VehicleSpawners`, `SpawnersHeight` — all `UPROPERTY` | **Y**, and that is the hazard | All four persist, including the whole `.xodr` as a string. But `BeginPlay` is unguarded (`OpenDriveGenerator.cpp:187-204`): it re-reads the `.xodr` and calls `GenerateAll()`, appending a **second complete set** of road meshes and spawn points. Fix: a saved `bGeometryBaked` guard, or omit the actor from baked levels. |
| `AVehicleSpawnPoint` | transform only | **Y** | Correct as-is once the double-generation above is fixed. |
| `ADrapedTerrainActor` | `UPROPERTY() TObjectPtr<UDrapedTerrainComponent>` | **P** | Actor, tag and collision profile survive. |
| **`UDrapedTerrainComponent`** | `OriginXCm, OriginYCm, CellSizeCm, NumCols, NumRows, TArray<double> HeightsCm, LocalBox` — **plain private members, no `UPROPERTY`** (`DrapedTerrain.h:48-54`) | **N** | On load `NumCols == 0`, `OnCreatePhysicsState` early-returns, and there is **no collision body at all** — a correctly-tagged actor with no ground under it. Fix: mark the seven members `UPROPERTY()`, rebuild in `PostLoad`, and store heights as `float` metres rather than `double` centimetres. |
| `AStagingBoundsActor` | five `UPROPERTY() double` | **Y** | The one bridge actor already save-clean. |
| `ACesium3DTileset` ×2 | `TilesetSource`, `IonAssetID`, **`IonAccessToken`**, `CreatePhysicsMeshes`, tags — all `UPROPERTY(EditAnywhere)` | **Y**, with a hazard | Persists — **including the Cesium ion access token, written into the `.umap`**. That is a credential in a committed and shipped asset. Fix: clear before save, re-inject at load from the environment. |
| `ACesiumGeoreference` (default) | `OriginLatitude/Longitude/Height`, `OriginPlacement`, `Scale`, `Ellipsoid` | **Y** | Persists. Write through the setters, not the private fields — only the setters call `UpdateGeoreference()`. |
| `ACesiumGeoreference` (offset) | same | **Y** | The constant height-align offset is recoverable as `defaultOriginHeight − offsetGeorefOriginHeight`. Useful as a cross-check, not as the record. |
| `ACesiumSunSky`, `ACesiumTimeOfDayController` | all `UPROPERTY(EditAnywhere)` | **Y** | Persist, including the longitude-derived time zone. |
| `ACesiumSensorViewPublisher` | `TrackedCaptures` weak refs | **P** | Saves a stale snapshot. Mark `Transient`. |
| `ATrafficLightManager` | most fields `UPROPERTY`; **`TArray<ATrafficSignBase*> TrafficSigns` is raw** | **P** | `TrafficLightsGenerated` persists, so regeneration is correctly skipped. But `TrafficSigns` is neither GC-rooted nor saved, and the teardown path iterates it destructively. Fix: add `UPROPERTY()` — this is a latent GC bug today, independent of level saving. |
| `ATrafficLightGroup`, `ATrafficLightBase`, `USignComponent`, `USpeedLimitComponent` | all `UPROPERTY` | **Y** | Including runtime-created trigger volumes, which are held by a `UPROPERTY` array and re-adopted on load. |
| **`UTrafficLightController`** | `NewObject<UTrafficLightController>()` — **no outer** (`TrafficLightManager.cpp:186, 203`) | **N** | Lands in the transient package. Every reference deserializes **null** while `TrafficLightsGenerated` claims success, and the group's reset path indexes `Controllers[CurrentController]` unguarded. Fix: pass an outer — one word per call site. |
| `ADecalActor` (transparency hack), actor factories | game-mode spawns, unguarded | **P** | Each save/load cycle accumulates another set. Fix: skip when present, or spawn `RF_Transient`. |

**Verdict:** only two things are outright broken, and both are one-line-per-field fixes. Two more are
latent defects that exist today regardless of this feature. The serialization problem is small. §4 is
the real one.

---

## 3. Two capability consequences of baking

**Baking *fixes* semantic segmentation.** `ATagger::TagActor` collects `UStaticMeshComponent` and
`USkeletalMeshComponent` (`Tagger.cpp:124`). `UProceduralMeshComponent` derives from `UMeshComponent`
and matches neither, so it never receives a stencil value: **generated roads are currently labelled
`None` in semantic segmentation.** Baking to `AStaticMeshActor` corrects this for free, provided the
package path's fifth token is a tagger key (guard rail 22 §11 item 5) or an explicit component tag is
set.

**Baking *breaks* three live RPCs unless they are widened.** `set_road_rendered`,
`set_layer_visible("road")` and `set_layer_collision("road")` all iterate
`TActorIterator<AProceduralMeshActor>` (`CarlaServer.cpp:601, 626, 668`). Baked roads become
`AStaticMeshActor` and silently drop out of all three. Under the standing directive that no capability
may be lost, **the iterators must be widened before any baked road reaches a level.**

---

## 4. The non-geometry payload — the load-bearing loss

Some of what the world needs has no native Unreal slot, and one item is not merely absent but
*dangerously* absent.

| Datum | Lives today in | On a saved level |
|---|---|---|
| Elevated `.xodr` | server-written `Content/Carla/Maps/OpenDrive/OpenDriveMap.xodr` | **Already solved.** `UOpenDrive::GetXODR` searches `<MapDir>/OpenDrive/<MapName>.xodr` and stock towns already ship that way, staged by an existing `+DirectoriesToAlwaysStageAsUFS` entry. Drop the file in a sibling folder and it works. |
| `OpendriveGenerationParameters` | a **plain member** on `UCarlaGameInstance`, set only by the RPC | **Lost.** A fresh server falls back to struct defaults (`max_road_length 50`, `wall_height 1.0`, `additional_width 0.6`) where our build uses `500 / 0 / 0`. A regenerated road comes back with walls and ten times the fragments. |
| Georeference origin | `configure_cesium_georeference` RPC | Recoverable from the saved georeference actor — as a side effect, not a record. |
| Height-align mode and scalar offset | client-side string; offset applied to road Z and the ground layer | Scalar recoverable from the offset georeference; **the mode name is lost entirely**. |
| **Per-cell bare-earth offset field and DTM grid** | `LastDrapedOffsetBytes` / `LastDrapedDtmBytes` — **properties on the in-process C# client object, never sent to the server** (`CarlaClient.cs:190-191`) | **Gone, silently.** |
| Provenance (source OSM, clip bounds, netconvert args, sample step, ion asset ids, tool version) | nowhere — no metadata file is emitted today | Gone. |

### The silent-truth failure

Telemetry HAE is defined as *physical minus offset*, and the offset field exists **only in the client
process that built the world**. Load a saved level with a fresh client and both `_drape_grid()` and
`_bare_earth_dtm_table()` return `None`, so the shim falls through to `hae = physical_hae - 0.0` and
`hae_dtm = None`. On a drape-mode map "physical" is the *draped photoreal* surface — so the system
reports photoreal-referenced altitude as bare-earth truth, **with no warning anywhere**. The native
recording path has the identical dependency.

This is the one failure mode here that corrupts data rather than breaking a feature, and it is worth
fixing on its own merits: **the same loss happens today whenever a client reconnects to a running
server.** It is not caused by level saving; level saving merely makes it permanent.

A partial artifact already exists — the drape cache persists origin, grid spec, ion asset ids and the
raw DSM/DTM grids. But it is an *input* cache holding raw sampled surfaces, not the de-spiked `DrapedZ`
or the `Offset` field, which are computed later. A new output artifact is genuinely required.

### Proposed shape

Two data assets, split by lifetime and bulk:

- **`UGeoreferencedWorldSettings`** — the world's contract, small and human-diffable: origin lat/lon and
  height, the `<geoReference>` string verbatim, the `.xodr` relative path and its SHA-256, the road-mesh
  parameters, `bRoadGeometryBaked`, the height-align mode and offset, per-layer vertical offsets, the
  Cesium layer table (ion asset ids, visibility, collision — **token deliberately absent**), the staging
  bounds, and a provenance block the runtime never reads.
- **`UBareEarthOffsetField`** — the bulk grid: grid spec plus three `TArray<float>` row-major grids
  (`OffsetMeters`, `BareEarthDtmMeters`, `DrapedZLocalMeters`). Measured grid sizes imply ~5.6 MB
  typical, ~32 MB for the largest area sampled so far. `TArray<float>` serializes as a contiguous block,
  unlike `FProcMeshVertex`.

Consumed by a small `AGeoreferencedWorldInitializer` placed in the template map, which replays the six
calls the client makes today — all of which are already `WorldContextObject`-based and world-agnostic —
in the correct order (`SetLayerVerticalOffset` must follow `ConfigureCesiumForOrigin`).

Plus **three read-only RPCs** — `get_world_settings`, `get_bare_earth_offset_grid`,
`get_bare_earth_dtm_grid` — returning the grids as float32 little-endian byte arrays, which is the wire
shape the client already caches, so the shim change is a fallback branch rather than a rewrite.

**Belt and braces:** when no settings asset is present, the shim must log a warning and report
`hae = None`, never `physical`. Losing truth loudly is a capability preserved; losing it silently is a
capability lost.

---

## 5. Where the generation runs

The generation is client-side C# over RPC and the editor is not a CARLA client. Three ways to bridge
that; the third is recommended, and the reasons are technical rather than aesthetic.

**Running the whole pipeline natively in the editor** founders on the editor's tick policy. Two
encouraging findings first: `CesiumCarlaBridge` is a *Runtime* module with no `IsGameWorld()` gate
anywhere, so passing it the editor world is structurally fine; and height sampling does **not** depend
on the camera — the sampler maintains its own tile set by ray-versus-bounding-volume traversal, and for
quantized-mesh terrain it resolves without downloading tiles at all. (The familiar "Cesium only streams
when the viewport is foreground" constraint governs *visual* streaming, not sampling.)

The blocker is that the sampler is pumped from `ACesium3DTileset::Tick`, and actor ticking in an editor
world requires a **visible realtime viewport**. Without one the tick type degrades and
`SampleHeightMostDetailed`'s future **never resolves — it hangs rather than erroring**. A commandlet has
no viewport at all. So the headless mode, the one wanted for CI, is precisely the mode that does not
work. Two further obstacles: `AOpenDriveGenerator::IsOpenDriveValid()` dereferences the game mode
unguarded and an editor world has none, so the generator cannot be reused as-is; and reproducing the
~2,000 lines of C# elevation logic in C++ would fork the algorithm — doc 22 §11 item 4 documents
exactly what the editor tool's own height code does to a production `.xodr`.

**Making the editor an RPC client of a running server** fails for a blunt reason: the server *is* the
editor binary, and `RunCarlaServer.ps1` refuses to start when an editor is already running because two
instances on one project conflict. It also buys nothing — the editor would still have to pull a
million-vertex mesh and the drape grid back through msgpack, which is the artifact hand-off below with
a socket in the middle.

**Recommended — the existing pipeline emits artifacts; the editor tool is a pure importer.** Add an
`--emit-world-package` flag that writes three files: the `.xodr` (already written), an extended drape
binary carrying the *output* `DrapedZ`/`Offset`/`DTM` grids as float32, and a metadata JSON holding the
settings block. The editor tool reads exactly those three, parses the `.xodr` with the same parser
CarlaTools already uses, generates and bakes the road, spawns and configures the Cesium, drape and
staging actors from the JSON, writes the two data assets, and saves.

This is the only path where the editor never talks to Cesium; it keeps one implementation of despiking,
grade separation and the elevation fit; the artifacts are diffable, hashable and CI-checkable; and
nothing regresses if the editor tool is never run. The acknowledged cost is that the importer cannot
re-sample — changing the OSM means re-running the client build. That is the right trade against an
editor that hangs waiting on tiles.

---

## 6. The road surface, and what JNI-347 means for it

The concern that JNI-347's unified road surface is too coarse to hand-edit is **well founded, and it
applies to a different code path than the one a bake should use.**

The branch changed only the `smooth_junctions` branch of `Map::GenerateChunkedMesh`, which now
rasterises the whole drivable network into a 0.5 m height field and retriangulates it as one welded
shell per height layer. Measured against the same `.xodr`:

| | pieces | vertices | triangles |
|---|---:|---:|---:|
| per-lane ribbon path | 508 → 8 tiles | 84,484 | 81,226 |
| **resolved surface (production)** | **18** | **1,043,474** | **1,977,146** |
| ratio | 2.3× | 12.4× | **24.3×** |

For hand editing the resolved surface is genuinely unsuitable: road and lane identity are discarded at
the raster step, a road is no longer a connected component, there is one material for the entire
drivable network, and every road edge is quantised to 0.5 m — a staircase, which doc 21 already records
as open and which is the first artefact an artist would try and fail to fix.

But the premise needs one correction: **today's chunked mesh is not editable either.** 508 lane ribbons
collapse into 8 anonymous grid bins keyed on first-vertex position, no material is ever assigned, no
actor label or tag is set, and UV0 is explicitly discarded. JNI-347 replaced one un-editable
representation with a heavier one; nothing a human could use was lost.

**The editable path already exists and JNI-347 did not touch it.**
`Map::GenerateOrderedChunkedMeshInLocations` → `GenerateTesselated` yields roughly one mesh per lane,
**emits UVs**, honours the width resolution, and is exactly what `OpenDriveToMap` bakes through — one
`AStaticMeshActor` per piece, labelled `SM_DrivingLane_N` / `SM_Sidewalk_N`, tagged `RoadLane`, with
per-lane-type materials and geometry recentred on its own centroid.

This resolves the largest open risk in the bake design. A bake through `GenerateChunkedMesh` would
produce static meshes with **no texture coordinates and no lightmap UV channel**, because the
`carla::geom::Mesh` conversion emits only vertices, triangles and normals. Baking through the ordered
per-lane path avoids that entirely.

**Recommendation: do not adjust JNI-347.** It is doing the right thing for the driven and rendered
runtime surface — C1 profile, no z-fighting, no holes, grade separation preserved — and reverting any of
it would lose a capability. Bake through the ordered entry point instead. Three modest changes are worth
making anyway, each an improvement to the runtime path independently of any bake:

1. **Emit planar UVs from the resolve.** The tile assembly already computes the plan coordinates; adding
   the matching UV is about two lines. Without it every road vertex gets UV0 = (0,0) and no tiling
   asphalt material can work — and the runtime discard at `OpenDriveGenerator.cpp:118` should stop at the
   same time.
2. **Carry lane attribution through the raster.** The raster step *sees* every lane quad covering a cell
   and throws the identity away. Storing it alongside the height gives 0.5 m-resolution per-triangle lane
   semantics — strictly more than the *nothing* roads carry today.
3. **Decouple the surface tile size from `max_road_length`.** They are the same knob today (500 m in
   production). At 50 m tiles one test map goes from 18 pieces to ~359, at roughly 2 % edge-vertex
   duplication — the one knob that buys selection granularity without touching the welding.

Do not shrink the raster cell (0.25 m quadruples triangles and regresses a known case), do not restore
`additional_width` on the resolve path, and do not re-enable the straight-lane fast path (§12).

---

## 7. What a level artifact is, and how the server loads it

The container question largely dissolves on inspection.

**Our shipped package contains no `.pak`, no `.utoc`, no `.ucas`.** It is loose cooked files, because
`BuildCookRun` is invoked without `-pak` and without `-iostore` (`Unreal/CMakeLists.txt:449-466`), and
UAT reads those only from the command line — the `UsePakFile=True` / `bGenerateChunks=True` in
`DefaultGame.ini` are inert. **Adding a level to a shipped build is, mechanically, copying files into a
directory.**

Three further facts make the workflow reachable:

- The package ships `CarlaUnreal.uproject` and a real `Plugins/` tree, and has **no `.upluginmanifest`**.
  Plugin discovery therefore falls back to a recursive `.uplugin` directory scan — in the packaged build
  too — so a plugin folder dropped in after shipping is discovered.
- **CARLA already has a plugin-hosted map convention**: `UOpenDrive` falls back to
  `Plugins/<MapName>/Content/Maps/OpenDrive/<MapName>.xodr` (`OpenDrive.cpp:108-111`).
- **`-map=` already works.** `UGameInstance::GetMapOverrideName` accepts both a positional token and
  `-map=`, unconditionally in Development — which is what we ship. Our launchers already pass a *long*
  package path.

That last point conceals the trap that most likely explains upstream's failure to inject maps. `FURL`
resolves **short** map names exclusively through the AssetRegistry and, on failure, silently
*"Invalidat[es] and revert[s] to Default URL"*; **long package names bypass the registry entirely.**
CARLA's RPC protocol passes bare basenames, and `CarlaEpisode.cpp:97-108` resolves the real path and
then discards it (`FinalPath = MapString`). A level added after `AssetRegistry.bin` was written is
invisible to short-name lookup but loads fine by long path — a failure that looks nothing like its cause.

### Recommended artifact

**A content-only, explicitly-loaded plugin, staged as loose cooked files, addressed by long package
path:**

```
<Package>/CarlaUnreal/Plugins/<MapName>/
    <MapName>.uplugin                     CanContainContent, ExplicitlyLoaded, NoCode, no modules
    Content/Maps/<MapName>.umap  .uexp
    Content/Maps/OpenDrive/<MapName>.xodr        <- CARLA already looks here
    Content/DA_<MapName>_WorldSettings.uasset
    Content/DA_<MapName>_OffsetField.uasset
```

It needs no mounting (the base is loose), no AssetRegistry entry (long path), gets its own `/<MapName>/`
namespace so it is trivially removable and versionable, matches CARLA's existing `.xodr` convention, and
survives a future move to paks unchanged. Launch with
`-map=/<MapName>/Maps/<MapName>`.

Rejected: **chunking via `PrimaryAssetLabel`** — it partitions a single cook and cannot add a level
afterwards at all; **patch paks** — wrong shape, they replace base content; **dropping files straight
into `/Game`** — works today but pollutes the base namespace with no version identity, acceptable only
as a stopgap.

### One mandatory CARLA change

`UCarlaEpisode::LoadNewEpisode` must stop discarding the resolved path — convert it to a long package
name and pass *that* to `OpenLevel`, and have `GetAllMapNames` return long paths and `FindMapPath` search
mounted plugin content roots (it already enumerates them). This removes the AssetRegistry dependency for
every added level and makes `get_available_maps` and `load_world` work for plugin-hosted maps.

### The decisions that are irreversible

1. **Retain release metadata for every published build.** If DLC-style cooking is ever wanted,
   `-CreateReleaseVersion` must be on the cook *at base-build time*. It archives only two files, but
   without them UAT refuses to cook DLC against that build permanently. Note it is incompatible with
   `-iterate`, so this needs a decision about whether the published cook is a separate non-iterative pass.
2. **Lock the mount namespace now** — `/<MapName>/`, not `/Game/…`. The mount root is baked into cooked
   package references, so changing it later invalidates every already-exported level.
3. **Decide loose versus paked deliberately, and write it down.** Today the base is loose *by omission*
   while `DefaultGame.ini` says the opposite. Someone "fixing" that mismatch by adding `-pak -iostore`
   would silently invalidate every exported level. Loose is the better fit for a simulation server.
4. **Publish the compatibility key.** The build already writes CARLA, content and engine git hashes into
   a `VERSION` file. Have exported levels record the same triple and have the server refuse or loudly
   warn on mismatch — this turns a mystery crash into a clear message.

---

## 8. Non-destructive re-bake

CARLA already carries the mechanism: the traffic-light manager spawns into named sublevels
(`TrafficLights`, `TrafficSigns`) via a level lookup that falls back to the persistent level when absent,
and stock content uses `MapLayer` bit-flag streaming against sublevel name suffixes. The move API is
proven in this codebase — add streaming level, move actors, save, remove — with a LargeMap-free variant
already written.

```
/Game/Carla/Maps/Generated/<Name>.umap            persistent  — AUTHORED
    Sublevels/<Name>_Roads.umap                   GENERATED
    Sublevels/<Name>_Signals.umap                 GENERATED
    Sublevels/<Name>_SpawnPoints.umap             GENERATED
    Sublevels/<Name>_Terrain.umap                 GENERATED
    OpenDrive/<Name>.xodr                         GENERATED (loose, staged as UFS)
    DA_<Name>_WorldSettings.uasset                GENERATED
```

**Re-bake deletes and recreates the generated sublevels and never touches the persistent level.** Tag
every generated actor at spawn so a stray one dragged into the persistent level can be found, and an
authored actor left in a generated sublevel can be reported rather than silently destroyed.

**A template note:** CarlaTools' `LevelCreator` uses `NewLevelFromTemplate` against
`DigitalTwinsTemplate.umap`, which **contains an `ALargeMapManager`** — forbidden by guard rail 22 §11
item 2, since the game mode activates it on sight and origin rebasing would strand the drape and staging
actors. We need our own template modelled on `OpenDriveMap.umap`.

### What cannot be preserved

Each is a data-loss class, not a bug, and the tool should say so plainly:

1. **Anything positioned relative to generated geometry.** A prop snapped to a road keeps its absolute
   transform; if the new OSM moves that road, it floats or sinks. There is no re-anchoring information —
   OSM way ids do not survive `netconvert` into the `.xodr`, the same identity gap doc 19 records for
   turn restrictions.
2. **Edits to generated assets.** Chunk indices are positional, and measurably **non-deterministic**:
   successive builds of the *same* map produced 18/19 pieces and vertex counts of 1,043,401 / 1,043,415 /
   1,043,473 / 1,043,474. Per-chunk authoring is unsafe by construction. Author on instances, and only
   through a rule the tool can re-apply.
3. **References from authored actors into generated ones** — they deserialize null after the sublevel is
   replaced. The move API's reference warning exists for this and should be enabled interactively.
4. **Lighting build data**, invalidated by any geometry change. Low impact — the scene is Cesium-lit.
5. **A hand-edited `.xodr`** — detected by the stored hash; the tool must refuse to overwrite silently
   and offer a diff.

---

## 9. Risks

| # | Risk | Severity | Mitigation |
|---|---|---|---|
| 1 | **Silent telemetry-truth loss** (§4) | **Critical** | The three truth RPCs plus a shim that reports `None` with a warning, never `physical`. Fix before any level is saved. |
| 2 | **Ion access token baked into a committed asset** | **High** | Clear before save, re-inject at load, assert at save time. |
| 3 | **Double-generation on load** | **High** | `bGeometryBaked` guard; omit the generator from baked levels. |
| 4 | Three road RPCs break on baked meshes (§3) | **High** | Widen the actor iterators first. |
| 5 | Baked road has no UVs | **Resolved** | Bake through the ordered per-lane path, which emits them (§6). |
| 6 | Null traffic-light controllers | Medium | One-word outer fix; regression-test Town10HD. |
| 7 | Editor tick / Cesium hang | **Avoided** | The artifact-import path removes it entirely (§5). |
| 8 | Chunking non-determinism defeats incremental re-bake | Medium | Bake whole sublevels, never diff chunks. |
| 9 | A generated level acquires an `ALargeMapManager` | Medium | Own template; assert none present on load. |
| 10 | `map_logic.json` emitted beside the generated `.xodr` | Medium | Never write that filename; bake-time check (guard rail 22 §11 item 1). |
| 11 | Cook/stage regression — map not shipped, or `.xodr` missing | Medium | A `Generated/` directory entry rather than per-map lines; packaged-server smoke test. |
| 12 | Linux Installed-Build / non-unity compile of new editor code | Low, recurring | Build in the box early; explicit includes. |

---

## 10. Effort

| Component | Days |
|---|---:|
| Truth RPCs + shim fallback | 2–3 |
| Serialization fixes (drape, controller outer, sign array, generation guards) | 2–3 |
| Artifact emission (`--emit-world-package`, extended drape binary, metadata JSON) | 1.5–2 |
| Data assets + world initializer | 3–4 |
| Editor importer tool + template map | 5–7 |
| Cook/stage config + packaged-server validation | 2–3 |
| Sublevel split + non-destructive re-bake | 3–5 |
| **Total** | **19–27** |

Add 2–4 days if a real road material and its UV work are in scope, and 1 day for the Linux
Installed-Build pass.

---

## 11. Development plan

Ordered so each step is independently valuable and nothing is blocked on the step after it.

### Step 1 — recover telemetry truth for any client (2–3 days, no dependencies)

Add `get_world_settings`, `get_bare_earth_offset_grid` and `get_bare_earth_dtm_grid`, and a shim
fallback that reads them. Make the absent-field case report `hae = None` with a warning instead of
silently returning `physical`.

**Do this first even if the level tool is never built.** It fixes a live defect: telemetry truth is lost
today whenever a client reconnects to a running server.

**Verification:** the existing DTM-decoupling test passes with a client that did not build the world.

### Step 2 — make the world serialization-clean (2–3 days)

`UPROPERTY` the seven drape-component fields plus a `PostLoad` rebuild; give
`UTrafficLightController` an outer; `UPROPERTY` the `TrafficSigns` array; add the `bGeometryBaked`
guard to `AOpenDriveGenerator::BeginPlay`; guard the decal and factory spawns; mark the sensor
publisher's tracked captures transient.

Each is a one-liner. The cost is the Town10HD regression pass the standing directive requires, plus
re-verifying that the drape heightfield seats vehicles identically across a save/load cycle.

### Step 3 — widen the road RPC iterators (0.5 day)

`set_road_rendered`, `set_layer_visible("road")` and `set_layer_collision("road")` must find baked
static-mesh roads as well as procedural ones. Doing this before anything is baked keeps the capability
continuous rather than restoring it after a regression.

### Step 4 — emit the world package (1.5–2 days)

`--emit-world-package <dir>` writing `<Name>.xodr`, an extended drape binary carrying the output
`DrapedZ`/`Offset`/`DTM` grids as float32, and `<Name>.world.json`. All the data is already in scope at
the end of the build; this is a writer, not an algorithm.

**Verification:** artifacts round-trip to identical grids; hashes stable across runs.

### Step 5 — data assets and the world initializer (3–4 days)

`UGeoreferencedWorldSettings`, `UBareEarthOffsetField`, and `AGeoreferencedWorldInitializer` replaying
the six configuration calls in the correct order. At this point a hand-authored level can stand up a
correct world with no client present — which is testable before any importer exists.

### Step 6 — the editor importer and template map (5–7 days)

A `CarlaTools` importer plus an editor utility widget: read the three artifacts, parse the `.xodr`,
generate through `Map::GenerateOrderedChunkedMeshInLocations`, bake per-lane static meshes to a
tagger-correct package path, spawn and configure the Cesium, drape and staging actors from the JSON,
write the data assets, save. Modelled directly on `UOpenDriveToMap::GenerateTile`.

Needs its own template map — **not** `DigitalTwinsTemplate` (§8) — and two new module dependencies in
`CarlaTools.Build.cs`.

**Verification:** generate, save, reopen the level cold, and confirm road geometry, collision seating,
signals, telemetry HAE and semantic labels all match a freshly generated world.

### Step 7 — cook, stage and load by name (2–3 days)

Register the generated map for cook and staging, and make `LoadNewEpisode` carry long package paths
(§7). Then run the decisive experiment: cook one map as a content-only explicitly-loaded plugin, drop
the loose output into a shipped package's `Plugins/`, and launch with `-map=/<Name>/Maps/<Name>`.

That single experiment exercises plugin discovery without a manifest, long-path URL resolution and the
`.xodr` plugin fallback at once — i.e. every claim in the upstream "cannot inject maps" issue.

### Step 8 — sublevel split and non-destructive re-bake (3–5 days)

Move generated content into the four sublevels, implement delete-and-recreate re-bake, and surface the
preservation limits of §8 in the tool.

### Decide before Step 7 lands

- Whether the published cook gains `-CreateReleaseVersion` (irreversible per build, §7).
- Whether the base stays loose or moves to paks — and comment the decision at the cook invocation so the
  ini mismatch is not "fixed" by accident.

---

## 12. Corrections to earlier notes

- **`UMapGenFunctionLibrary::CreateMesh` does not save.** Its `UPackage::SavePackage` block is inside a
  `/* … */` comment (`MapGenFunctionLibrary.cpp:189-206`). It creates the package, builds the mesh,
  notifies the asset registry and calls `MarkPackageDirty()`; persistence depends on a later
  `SaveDirtyPackages`. It also allocates one material slot and leaves no source model, so the result
  cannot be rebuilt in the Static Mesh Editor — the procedural-building cook path is the correct in-repo
  pattern to copy.
- **`AProceduralMeshActor` is not transient.** `ProcMeshSections` is a `UPROPERTY` and the geometry does
  survive a level save. The case for baking is size, Nanite/LODs and semantic tagging — not
  serialization.
- **`ADrapedTerrainActor` does serialize**; it is `UDrapedTerrainComponent`'s seven plain members that do
  not, producing a correctly-tagged actor with no collision body.
- **`LevelCreator` uses `NewLevelFromTemplate`, not asset duplication**, and its template contains an
  `ALargeMapManager`.
- **The `.xodr` already has a native slot** and needs no new mechanism.
- **On the straight-lane fast path** (doc 22 §17, corrected there): losing it repairs a defect rather
  than costing anything. `Lane::IsStraight` tests only the elevation `c` and `d` coefficients, never `b`,
  so a road carrying many piecewise-*linear* records previously counted as straight, received two
  vertices for the whole lane, and discarded its elevation profile — measured at up to 1.68 m of chord
  error on a 402 m lane with 42 elevation records, and 26.2 m on `wrigley`. On the production resolved
  surface the added triangle cost is +0.36 %.

---

## 13. Open questions

- Whether the resolved surface's staircased road edge can be fixed by clipping boundary cells to the
  true lane polygon at the current cell size — doc 21 names it, nobody has built it.
- Whether a saved level's baked collision seats vehicles bit-identically to a freshly built world, or
  merely closely. Only a measurement answers this.
- Whether `CarlaLargeMapConvertCommandlet` (doc 22 §12) cherry-picks cleanly, should World Partition ever
  become desirable. Divergence measured; no trial merge run.
- The node-level behaviour of `LevelCreator` and the other editor utility Blueprints — analysed by
  byte-grepping asset strings, not by opening graphs.
- Whether any consumer depends on generated roads being `AProceduralMeshActor` beyond the three RPCs in
  §3. The grep was targeted, not exhaustive.

---

## Sources

- Unreal Engine 5.7.4 source, `UE_5_7_4/Engine/Source/` — `Editor/UnrealEd` (FileHelpers, SavePackage,
  EditorLevelUtils, EditorScriptingHelpers), `Editor/LevelEditor` (LevelEditorSubsystem),
  `Runtime/CoreUObject` (SavePackage2, Package, UObjectBaseUtility, ObjectMacros),
  `Runtime/Engine` (World, Level, Actor, GameInstance, GameplayStatics, URL),
  `Runtime/PakFile` (IPlatformFilePak), `Runtime/Projects` (PluginManager),
  `Programs/AutomationTool` (ProjectParams, CopyBuildToStagingDirectory),
  `Engine/Plugins/Runtime/ProceduralMeshComponent`.
- `cesium-native` `Cesium3DTilesSelection` — `Tileset.cpp`, `TilesetHeightQuery.cpp`.
- OpenDRIVE 1.4 §5.3.5 road elevation.
- `carla-simulator/carla-digitaltwins` issue #83.
- Workspace Unreal skill set, `.agents/skills/` — `ue-world-level-streaming`, `ue-editor-tools`,
  `ue-module-build-system`, `ue-actor-component-architecture`, `ue-serialization-savegames`,
  `ue-procedural-generation`.
