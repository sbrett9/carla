# Dynamic Georeferenced World — ENGINE-INTEGRATION findings

Scope: how the engine (a) hosts/streams Cesium inside the CARLA world, (b) samples
Cesium terrain heights for a list of (lat,lon) points, and (c) where those steps slot
into the CARLA runtime world-generation sequence. Read-only code/web research. The
data-pipeline side (OSM/SUMO/CarlaNet, producing/consuming the sample points and
injecting elevation into the .xodr) is covered by a parallel agent.

All file:line citations are absolute paths under `g:\Projects\CarlaUE_5_7_4\`.

---

## 0. TL;DR verdict

- **Cesium height sampling is the right primitive and it is decoupled from the visible
  viewport.** `ACesium3DTileset::SampleHeightMostDetailed` loads the tiles it needs
  *on demand* (it calls `LoadTileset()` itself and the cesium-native height sampler
  streams the tiles covering the query points regardless of camera frustum). It does
  NOT require the points to be on-screen.
- **BUT it does require the tileset actor to keep TICKING** until the async future
  resolves. The cesium-native contract is explicit: `Tileset::updateView` must be called
  periodically or the returned future never resolves; for non-visualization use you may
  call it "with an empty list of frustums." In Cesium-for-Unreal that periodic pump is
  `ACesium3DTileset::Tick` (which calls `updateViewGroup` + `loadTiles` +
  `dispatchMainThreadTasks` every frame). So the requirement is *a ticking game world*,
  not *a rendering viewport*.
- **The observed "tiles only stream when viewport visible" constraint applies to the
  VISUAL overlay (rendering the photoreal mesh), not to height sampling.** Visual
  streaming is driven by camera frustums collected from visible viewports / player
  cameras / scene-capture; height sampling uses a separate frustum-independent path.
- **Ordering is NOT actually circular** once you split the two Cesium roles. The origin
  (lat/lon) is a user parameter known up front, so a Cesium georeference + tileset can be
  created and *sampled* before any road exists. Sample → return heights → data side
  injects Z into the .xodr → call `generate_opendrive_world` → roads are built at correct
  elevation → Cesium stays as the visual overlay.
- **No existing Cesium↔CARLA integration code exists in the plugins** (grep for `Cesium`
  across `carla\Unreal\CarlaUnreal\Plugins` returns nothing). This is greenfield.
- **Biggest engine-side risk:** whether a packaged/headless `-RenderOffScreen` server
  reliably ticks the tileset and drives the cesium-native async system to completion
  for sampling. The code path strongly suggests yes (no rendering dependency in the
  sample path), but it is unverified on this build and is the one thing to prototype.

---

## 1. Programmatic Cesium height sampling

### 1.1 The two entry points (C++ and Python/Blueprint)

**C++ (direct, with a delegate):**
`UE_5_7_4\Engine\Plugins\Marketplace\Cesiumfo9eaf76ca58f3V10\Source\CesiumRuntime\Public\Cesium3DTileset.h:147`

```cpp
void SampleHeightMostDetailed(
    const TArray<FVector>& LongitudeLatitudeHeightArray,   // X=lon°, Y=lat°, Z ignored
    FCesiumSampleHeightMostDetailedCallback OnHeightsSampled);  // game-thread callback
```
- Callback type declared at `Cesium3DTileset.h:65` —
  `DECLARE_DELEGATE_ThreeParams(FCesiumSampleHeightMostDetailedCallback, ACesium3DTileset*, const TArray<FCesiumSampleHeightResult>&, const TArray<FString>&)`.
- Header note (`Cesium3DTileset.h:144`): "A callback that is invoked **in the game
  thread** when heights have been sampled for all positions." Output height is meters
  above the **ellipsoid (WGS84)**, NOT mean sea level (`Cesium3DTileset.h:137-139`).

**Blueprint / Python (async-action node, what VibeUE-Python would call):**
`...\Public\CesiumSampleHeightMostDetailedAsyncAction.h:48`

```cpp
static UCesiumSampleHeightMostDetailedAsyncAction* SampleHeightMostDetailed(
    ACesium3DTileset* Tileset, const TArray<FVector>& LongitudeLatitudeHeightArray);
// then bind the multicast delegate:
UPROPERTY(BlueprintAssignable) FCesiumSampleHeightMostDetailedComplete OnHeightsSampled;
```
- `OnHeightsSampled` is `DECLARE_DYNAMIC_MULTICAST_DELEGATE_TwoParams(... const TArray<FCesiumSampleHeightResult>& Result, const TArray<FString>& Warnings)` (`AsyncAction.h:21`).
- Result struct `FCesiumSampleHeightResult` (`CesiumSampleHeightResult.h:11`): `FVector LongitudeLatitudeHeight` (lon X, lat Y, sampled-or-passthrough height Z) + `bool SampleSuccess`. On failure the *input* Z is passed through and `SampleSuccess=false`.

The async-action `.cpp` (`...\Private\CesiumSampleHeightMostDetailedAsyncAction.cpp:18-46`)
just forwards to the tileset's `SampleHeightMostDetailed` and rebroadcasts the result,
then `SetReadyToDestroy()`. So from Python you bind `OnHeightsSampled`, call the static
factory, and let the editor/game tick until the delegate fires — exactly the BP
async-node pattern.

### 1.2 Does it auto-stream the needed tiles? (YES)

`...\Private\Cesium3DTileset.cpp:157-200` — the implementation:
- `ResolveGeoreference / ResolveCameraManager / ResolveCreditSystem` then
  **`if (this->_pTileset == nullptr) this->LoadTileset();`** (`Cesium3DTileset.cpp:166-168`)
  — it will create the native tileset if it isn't loaded yet.
- It then calls cesium-native `_pTileset->sampleHeightMostDetailed(positions)`
  (`Cesium3DTileset.cpp:182`) and on resolution converts results + executes the callback
  on the game thread (`Cesium3DTileset.cpp:202-238`).

cesium-native header
`...\Source\ThirdParty\include\Cesium3DTilesSelection\Tileset.h:347-366`:
> "The most detailed available tiles are used to determine each height ... Note that
> `Tileset::updateView` must be called periodically, or else the returned `Future` will
> never resolve. If you are not using this tileset for visualization, you can call
> `updateView` with an empty list of frustums."

Web confirmation (CesiumJS moderator, same algorithm): sampleHeightMostDetailed "should
work even if a tileset isn't loaded" and clamps "even when it's not all in view" — it
streams the tiles covering the query points on demand, frustum-independent.
(`community.cesium.com/t/.../8492`). cesium-native also guarantees a tile is not
destroyed while a height query is using it.

**Conclusion:** tiles do NOT need to be pre-streamed and the points do NOT need to be in
camera view. The *only* runtime requirement is that the tileset keeps ticking until the
future resolves.

### 1.3 The tick dependency = the headless question (see §4)

The "periodic updateView" pump in UE is `ACesium3DTileset::Tick`
(`...\Private\Cesium3DTileset.cpp:2097`), which every frame:
- `getAsyncSystem().dispatchMainThreadTasks()` (`:2166`) — drains the async continuations
  that resolve the sample future,
- builds `frustums` from `GetCameras()` (`:2149`, `:2181-2187`) — *may be empty*,
- `updateViewGroup(... frustums ...)` (`:2197`) and `loadTiles()` (`:2205`).

`GetCameras()` (`:1378-1411`) returns player cameras + scene captures (+ editor cameras
only `#if WITH_EDITOR` and only in non-game worlds, `:1395-1403`, `:1706-1708`). If there
are none, `frustums` is empty — which is exactly the cesium-native "empty list of
frustums" sampling-only mode. So **an empty-frustum tick still drives the sample to
completion**; what matters is that `Tick` runs (i.e. the actor ticks in the world).
`PrimaryActorTick.bCanEverTick = true` is set for the tileset (`Cesium3DTileset.cpp:121`,
TickGroup `TG_PostUpdateWork`).

---

## 2. Inserting Cesium into the CARLA world

### 2.1 The CARLA dynamic-world path (where roads come from)

Client RPC → server, the OpenDriveMap "special episode":
- `LibCarla\source\carla\client\detail\Simulator.cpp:116-125` — `LoadOpenDriveEpisode`
  calls `_client.CopyOpenDriveToServer(...)` then `LoadEpisode("OpenDriveMap", ...)`.
  Comment: *"The 'OpenDriveMap' is an '.umap' located in carla/Unreal/CarlaUnreal/Content/Carla/Maps/"*.
- Server RPC handler: `...\Plugins\Carla\Source\Carla\Server\CarlaServer.cpp:381` —
  `BIND_SYNC(copy_opendrive_to_file)` → `Episode->LoadNewOpendriveEpisode(...)`.
- `...\Plugins\Carla\Source\Carla\Game\CarlaEpisode.cpp:132-212` —
  `UCarlaEpisode::LoadNewOpendriveEpisode`:
  - parses the xodr (`OpenDriveParser::Load`, `:143`),
  - **writes the xodr to disk** at
    `ProjectContentDir()/Carla/Maps/OpenDrive/OpenDriveMap.xodr` (`:168-176`),
  - stores `OpendriveGenerationParameters` on the game instance (`:184-188`),
  - optionally kicks off Recast nav build.
  It does **not** itself build the world; it stages the xodr+params on the server.

Then the episode reloads the **base umap** `Carla/Maps/OpenDriveMap.umap` (confirmed to
exist: `...\Content\Carla\Maps\OpenDriveMap.umap`). On load:
- `AOpenDriveGenerator::BeginPlay` (`...\Plugins\Carla\Source\Carla\OpenDrive\OpenDriveGenerator.cpp:168-185`):
  `GetXODR(World)` reads the on-disk xodr back, `LoadOpenDrive`, then `GenerateAll()`.
- `GenerateRoadMesh` (`OpenDriveGenerator.cpp:61-130`) calls
  `CarlaMap->GenerateChunkedMesh(Parameters)` and spawns `AProceduralMeshActor`s with the
  resulting vertices — **the mesh Z comes straight from the xodr geometry/elevation**, so
  injecting elevation into the .xodr is sufficient to raise the road mesh.
- `GenerateSpawnPoints` (`:142-159`) places spawn points from waypoint transforms
  (`+ SpawnersHeight` Z offset), also following xodr elevation.
- `GameMode` (`...\Game\CarlaGameModeBase.cpp:77` `InitGame`, `:179` `BeginPlay`) owns the
  episode lifecycle; it spawns weather/factories/traffic-light manager. There is no
  Cesium here today.

Important: the **georeference origin is carried inside the .xodr `<geoReference>` proj
string** (lat_0/lon_0 — per the project's OSM georeferencing work,
`carla\CarlaNet\docs\OSM_Georeferencing.md`), NOT in `OpendriveGenerationParameters`
(`LibCarla\source\carla\rpc\OpendriveGenerationParameters.h:15-52` has only
vertex_distance/road length/wall/width/visibility/nav flags — no geo fields). So whoever
sets the Cesium origin must read it from the same lat/lon the .xodr was generated with.

### 2.2 Where to insert Cesium — options evaluated

The hard constraint: `generate_opendrive_world` **replaces the level** with
`OpenDriveMap.umap`, so any Cesium actors must either live in that umap or be
(re)spawned after each load.

**(a) Pre-place Cesium in the OpenDriveMap base umap — RECOMMENDED.**
Add a `CesiumGeoreference` + a `Cesium3DTileset` (Google Photoreal, ion asset 2275207)
directly into `Carla/Maps/OpenDriveMap.umap`. Pros: survives every episode load by
construction (it *is* the loaded level); no respawn race. Cons: origin must be set at
runtime to match the .xodr origin (the actor's serialized OriginLatitude/Longitude is a
fixed default), and the ion token must be present. Both are solvable (see §2.3). This is
the cleanest "every generated world has Cesium" answer.

**(b) C++ in the Carla plugin spawns/owns Cesium when OpenDriveMap loads.**
Best hook: a small new actor/component, or code in `AOpenDriveGenerator::BeginPlay` /
`ACarlaGameModeBase::BeginPlay`, that spawns `ACesiumGeoreference` + `ACesium3DTileset`,
sets the origin from the parsed map's geo-reference, and sets the ion token. Pros:
origin/token wired programmatically from the authoritative xodr; deterministic ordering
relative to road generation. Cons: adds a hard compile-time dependency from the Carla
plugin onto the CesiumRuntime module (must add `CesiumRuntime` to Carla.Build.cs
PrivateDependencyModuleNames, and Cesium must be enabled for the project). This is the
right place for the **height-sample call** even if the actors are pre-placed via (a).

**(c) Python / VibeUE post-load.** Works for the editor-driven interactive case (already
validated: spawn georeference+tileset, set token+origin via Python). Good for the
human-in-the-loop dataset authoring flow and for prototyping the sample step without a
C++ rebuild. Cons: not viable for a packaged headless server (no Python editor); racey
relative to episode reload (must re-run after every `generate_opendrive_world`).

**(d) A CarlaNet RPC / new CARLA command.** Useful as the *control-plane* glue: a new
server command "ensure Cesium present + set origin (lat,lon,height) + sample these
(lat,lon) points → return heights" that the data side calls. Under the hood it would do
(a)/(b). This is how the height result crosses back to the data/CarlaNet side
cleanly (see §3). Recommended as the *interface*, implemented on top of (a)+(b).

**Recommended combination:** (a) pre-place the Cesium actors in OpenDriveMap.umap for
survival, plus (b) C++ that on load sets origin+token and exposes a sample entry point,
fronted by (d) a CarlaNet/RPC command so the data side can request "set origin + sample
points → heights" and later "set origin to final value" for the visual overlay.

### 2.3 Setting origin + ion token programmatically

- Origin: `ACesiumGeoreference` exposes `OriginLatitude` / `OriginLongitude` /
  `OriginHeight` with Blueprint getters/setters and
  `OriginPlacement = EOriginPlacement::CartographicOrigin`
  (`...\Public\CesiumGeoreference.h:143,153-198`). Set these (BP/Python/C++) so the chosen
  lat/lon → UE (0,0,0). `OriginHeight` is the ellipsoidal height of the origin; it must
  match the vertical datum used when injecting xodr Z (this is the known vertical-offset
  to reconcile — sampled heights are ellipsoidal meters per §1.1).
- ion token: set on the `CesiumIonServer` / `CesiumIonSaaS` asset (already validated in
  this project per memory). Tileset uses ion asset id 2275207.
- Axis convention: Cesium georeference uses +X=East / -Y=North at the origin, matching
  CARLA (per project validation), so sampled lon/lat ↔ CARLA X/Y are consistent.

---

## 3. Engine-side ordering (the de-circularized sequence)

The dependency is only circular if Cesium-for-roads and Cesium-for-visuals are conflated.
Split them:

```
[user params]  origin lat/lon (+ OSM file)            ← known up front
        │
        ▼
(P1) DATA SIDE parses OSM → road sample points (lat,lon)   [other agent]
        │  (points only need horizontal position; Z is what we want)
        ▼
(P2) ENGINE: ensure a CesiumGeoreference + Cesium3DTileset exist in a TICKING world,
     origin set to user lat/lon, ion token set.  Roads need NOT exist yet.
        │
        ▼
(P3) ENGINE: ACesium3DTileset::SampleHeightMostDetailed(points)   (§1)
     → tick the world until OnHeightsSampled fires (tiles stream on demand)
why     → returns [(lon,lat,ellipsoidalHeight), success] per point
        │
        ▼
(P4) ENGINE → DATA SIDE: hand heights back (RPC / file / shared process)
        │
        ▼
(P5) DATA SIDE injects Z (height − OriginHeight datum reconciliation) into the .xodr
     elevationProfile / road geometry                              [other agent]
        │
        ▼
(P6) ENGINE: generate_opendrive_world(elevated .xodr)  → OpenDriveMap.umap reloads,
     AOpenDriveGenerator builds road mesh at correct Z (§2.1)
        │
        ▼
(P7) ENGINE: Cesium remains as the visual overlay (georef origin == .xodr origin);
     traffic (CarlaNet) drives on the elevated roads aligned to the photoreal terrain.
```

### 3.1 Where the height-sample step executes

Two viable hosts for P2+P3:

- **Pre-flight world (preferred for headless/automation):** run P2/P3 in a lightweight
  world whose only job is to host the tileset and tick — this can be the OpenDriveMap.umap
  itself loaded *before* the elevated xodr is staged (the road generator just builds an
  empty/old map; ignore it), or a dedicated minimal level. The sample call is the same.
- **In-editor (interactive authoring):** the already-validated Python flow — spawn
  georeference+tileset, call the async action, let the editor viewport tick.

Because P3 is frustum-independent, the pre-flight world does not need a player or a
visible viewport — only ticking (§4).

### 3.2 How results cross back to the data/CarlaNet side

- **Recommended: a CarlaNet/RPC command** (option 2.d). Add a server command that takes
  origin + the (lat,lon) array, ensures the tileset, calls
  `SampleHeightMostDetailed`, blocks/awaits the delegate (pumping ticks), and returns the
  `FCesiumSampleHeightResult` array. This keeps everything in-process and matches how the
  rest of the dynamic-world pipeline already talks to the server (`copy_opendrive_to_file`
  is a sibling `BIND_SYNC`). cesium-native completion is async on the game thread, so the
  RPC handler must drive ticks until the future resolves rather than block the game thread.
- **Fallback: file handoff.** Engine writes sampled heights to a json/csv next to
  `Carla/Maps/OpenDrive/`; the data side reads it. Simplest to prototype, no C++ binding,
  works even from the Python/VibeUE path.
- **Shared process:** if the data tooling (CarlaNet .NET) is co-resident, an in-memory
  return is possible, but the RPC route is the natural fit given existing plumbing.

---

## 4. Headless / automation feasibility (KEY RISK)

### What the code says
- The **visual** photoreal overlay streams from camera frustums collected via visible
  viewports / player cameras / scene captures (`Cesium3DTileset.cpp:1378-1411`,
  editor-viewport `IsVisible()` gating `:1721-1725`). This is the observed
  "minimized ⇒ load_progress 0" behavior — it is a *visualization* property.
- **Height sampling is independent of that.** It loads on demand and only needs the
  tileset to tick so the cesium-native async system advances
  (`Tick` → `dispatchMainThreadTasks` + `loadTiles`, `:2166`,`:2205`; cesium-native
  contract `Tileset.h:356-359`). Empty frustums are explicitly supported for
  sampling-only use.

### Implication
- **For dataset generation we only need the SAMPLE path headless, not the visual path.**
  A packaged/headless server that *ticks* the world should be able to run
  `SampleHeightMostDetailed` to completion even with no rendering, because the sample path
  has no rendering dependency and tolerates empty frustums. This is the strong, code-backed
  expectation.
- **The visual overlay headless is the harder part** and may genuinely require a rendering
  surface. Mitigations if visuals are needed off-screen: run with `-RenderOffScreen`
  (UE still renders to an off-screen target, which keeps a real viewport/camera and should
  satisfy frustum-driven streaming), and/or add a `CesiumCameraManager` camera / a
  `SceneCapture2D` so the tileset has a frustum to stream against (Cesium explicitly
  streams for scene-capture components). Cesium issue #801 ("invisible editor viewports
  still affect tile selection") and the movie-render-queue threads show streaming is tied
  to *some* active view existing, not necessarily a visible window.

### Verdict
- **Sampling headless: very likely YES** (no rendering dependency in the sample code path;
  needs only a ticking world). **Unverified on this exact build → prototype it first.**
- **Visual overlay headless: conditional** — needs `-RenderOffScreen` and/or an explicit
  camera/scene-capture so a frustum exists; if the dataset only needs *high-altitude EO
  video*, that off-screen camera is the EO sensor itself, which doubles as the streaming
  driver, so this is consistent with the broader project goal.
- The single thing to de-risk: confirm that `SampleHeightMostDetailed`'s
  `OnHeightsSampled` delegate actually fires on a packaged `-RenderOffScreen` (or even
  `-nullrhi`?) CARLA server while ticking, with valid (SampleSuccess=true) heights.
  `-nullrhi` is the riskier unknown — tile *content decode* may need an RHI; if so use
  `-RenderOffScreen` (real RHI, no window) rather than `-nullrhi`.

---

## 5. Already solved? — NO

- Grep for `Cesium` across `carla\Unreal\CarlaUnreal\Plugins` → **no matches**. There is
  no existing Cesium↔CARLA bridge, no georeference wiring, no sample-height plumbing in the
  CARLA/CarlaTools plugins. This integration is greenfield.
- Cesium for Unreal v2.26.0 is bundled and provides everything needed
  (`...\Cesiumfo9eaf76ca58f3V10\Source\CesiumRuntime\Public\` headers above).
- The CARLA side already has the staging mechanism (xodr→disk→OpenDriveMap.umap→
  AOpenDriveGenerator) and elevation flows from xodr geometry into the road mesh with no
  changes required — only the .xodr needs the Z, which is the data side's job once heights
  are returned.

---

## 6. Concrete engine-side recommendation

1. Enable CesiumRuntime as a project dependency for the Carla plugin (Build.cs) OR keep
   the sample step in editor-Python for the first prototype.
2. Pre-place `CesiumGeoreference` + `Cesium3DTileset` (ion 2275207) in
   `Carla/Maps/OpenDriveMap.umap` so they survive every `generate_opendrive_world` reload.
3. On level load (C++ in `ACarlaGameModeBase::BeginPlay` or `AOpenDriveGenerator::BeginPlay`):
   set georeference Origin{Lat,Lon,Height} from the active xodr's geoReference + ion token.
4. Expose a server command (CarlaNet/RPC, sibling to `copy_opendrive_to_file`):
   `sample_terrain_heights(origin, [(lat,lon)...]) -> [(lon,lat,height,ok)...]` that calls
   `ACesium3DTileset::SampleHeightMostDetailed`, pumps ticks until `OnHeightsSampled`
   fires, and returns results. Reconcile ellipsoidal-height vs OriginHeight datum.
5. Sequence the pipeline per §3: sample BEFORE generating the elevated world; regenerate
   the world from the elevated xodr; keep Cesium as the visual overlay.
6. De-risk headless FIRST: run the sample call on a packaged `-RenderOffScreen` server and
   confirm the delegate fires with valid heights before building the full automation.
