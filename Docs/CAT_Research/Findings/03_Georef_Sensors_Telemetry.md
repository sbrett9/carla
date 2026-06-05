# CARLA × Cesium Digital-Twin: Georeferencing, High-Altitude EO Capture, and Truthed Telemetry

Research findings for the CARLA→Cesium procedural digital-twin pipeline (high-altitude EO video +
georeferenced truth for detector training). All claims grounded in the CARLA UE5.7.4 source tree at
`g:\Projects\CarlaUE_5_7_4\carla`, the CarlaNet .NET client, and confirmed against CARLA's published
docs where noted.

---

## A. GEOREFERENCING RECONCILIATION

### A.1 How CARLA represents geo position

**`carla::geom::GeoLocation`** — `g:\Projects\CarlaUE_5_7_4\carla\LibCarla\source\carla\geom\GeoLocation.h`
- Three `double` members: `latitude`, `longitude`, `altitude` (lines 23–27).
- `MSGPACK_DEFINE_ARRAY(latitude, longitude, altitude)` (line 60).
- The core method is `GeoLocation::Transform(const Location&)` —
  `g:\Projects\CarlaUE_5_7_4\carla\LibCarla\source\carla\geom\GeoLocation.cpp` lines 66–73. This is
  the **UE-local → lat/lon converter**, and it is the heart of the whole georeference chain.

**The actual projection math** (`GeoLocation.cpp`):
- Earth modeled as a **sphere**, `EARTH_RADIUS_EQUA = 6378137.0 m` (line 23) — i.e. **spherical Web
  Mercator**, *not* the WGS84 ellipsoid.
- `LatLonToMercator` (lines 37–41):
  - `mx = scale * radians(lon) * R`
  - `my = scale * R * ln(tan((90 + lat)·π/360))`
  - `scale = cos(radians(lat_origin))` (`LatToScale`, lines 31–33).
- `Transform` adds the local offset in metres to the origin lat/lon:
  ```
  GeoLocation::Transform(location):
    result.altitude  = origin.altitude + location.z
    LatLonAddMeters(origin.lat, origin.lon,
                    location.x, -location.y,   //  <-- Y is INVERTED here
                    result.lat, result.lon)
  ```
  The `-location.y` (line 70) flips CARLA's left-handed Y so that **+Y north** maps to increasing
  latitude. This is the single most important handedness fact in the chain.

**GNSS sensor** — `...\Plugins\Carla\Source\Carla\Sensor\GnssSensor.cpp`:
- `PostPhysTick` (lines 37–85): `GetActorLocation()` (UE cm) → `LocalToGlobalLocation` via
  `ALargeMapManager` (un-rebases large-map tile origin shifts) → implicit `FVector→carla::geom::Location`
  (divides by 100, cm→m) → `CurrentGeoReference.Transform(Location)` → lat/lon/alt, then Gaussian
  noise/bias added (lines 50–58), serialized with `DataStream.SerializeAndSend`.
- The georeference origin is captured once in `BeginPlay` (lines 158–164):
  `CurrentGeoReference = episode->GetGeoReference()`.
- Serializer: `...\LibCarla\source\carla\sensor\s11n\GnssSerializer.{h,cpp}` — payload is a
  msgpack `GeoLocation`.

**Where the georeference origin comes from** — the OpenDRIVE `<geoReference>` PROJ string:
- `...\LibCarla\source\carla\opendrive\parser\GeoReferenceParser.cpp` (`ParseGeoReference`,
  lines 28–60). It **only extracts `+lat_0` and `+lon_0`** from the PROJ string. Everything else in
  the PROJ string (`+proj=tmerc`, `+x_0`, `+y_0`, datum, etc.) is **ignored**.
- If `+lat_0`/`+lon_0` are missing/unparseable it defaults to **lat 42.0, lon 2.0** (Barcelona),
  lines 51–55.
- Origin propagation: `CarlaGameModeBase.cpp:462` `Episode->MapGeoReference = Map->GetGeoReference()`;
  exposed via `CarlaEpisode::GetGeoReference()` (`CarlaEpisode.h:138`, backing field
  `MapGeoReference` at line 402).

**CarlaNet equivalents:**
- `GeoLocation` type: `carla\CarlaNet\src\CarlaNet.Types\Geom\GeoLocation.cs` (lat/lon/alt doubles,
  `[Key 0/1/2]`).
- Origin parser **ported**: `carla\CarlaNet\src\CarlaNet.Map\OpenDrive\Parser\GeoReferenceParser.cs`
  (same `+lat_0/+lon_0`-only extraction, same 42/2 default), exposed as
  `CarlaNet.Map.Road.Map.GeoReference` (`Road\Map.cs:29`).
- **GAP:** The actual Mercator `GeoLocation.Transform()` math (`LatLonToMercator` / `LatLonAddMeters`)
  is **NOT ported** to CarlaNet. A grep for `Mercator/LatLonAddMeters/EARTH_RADIUS` in
  `CarlaNet/src` returns nothing. CarlaNet has the **origin** but not the **local→geo converter**.
  We must port those ~30 lines (trivial) OR read lat/lon straight off a GNSS sensor attached to the
  drone (the server already does the transform). See risks.

### A.2 Concrete transform chain CARLA-local → Cesium

Units & handedness facts (source-confirmed):
- **CARLA Python/API coordinates are metres, left-handed (X fwd, Y right, Z up).**
- **UE world is centimetres, left-handed.** `Location(const FVector&)` divides by 100;
  `operator FVector()` multiplies by 1e2 —
  `...\LibCarla\source\carla\geom\Location.h` lines 82–87.
- CARLA's lat/lon uses **spherical** Web Mercator (R=6378137). Cesium uses the **WGS84 ellipsoid**
  (ECEF). These two earth models do not agree; see risk R1.

Full chain to align a CARLA actor with Cesium-for-Unreal:

```
(1) CARLA actor location  P_carla = (x, y, z)  [metres, left-handed, Y-right]
        observer cache / get_transform() give this directly

(2) Local → geodetic (CARLA's own math, GeoLocation::Transform):
        scale = cos(rad(lat0))
        mx0,my0 = LatLonToMercator(lat0, lon0, scale)
        mx = mx0 + x ;  my = my0 + (-y)          // Y FLIP
        lon = mx*180 / (π·R·scale)
        lat = 360·atan(exp(my/(R·scale)))/π − 90
        h   = alt0 + z
   => WGS84-ish (lat, lon, h)   [degrees, metres-above-origin]

(3) Cesium ingest: set
        CesiumGeoreference.OriginLatitude  = lat0   (CARLA +lat_0)
        CesiumGeoreference.OriginLongitude = lon0   (CARLA +lon_0)
        CesiumGeoreference.OriginHeight    = alt0
   Then Cesium's GeoTransforms maps (lon, lat, h) → ECEF → Unreal (relative to floating origin).

(4) Cesium ECEF is right-handed WGS84; Cesium-for-Unreal already converts ECEF→UE (left-handed,
    cm) internally, and applies its own floating-origin rebase. So once (3) matches, a CARLA actor's
    (lat,lon,h) handed to Cesium lands at the correct Cesium-Unreal spot.
```

**What MUST match between the two systems:**
1. `CesiumGeoreference.OriginLatitude/Longitude/Height` **==** the CARLA `.xodr` `<geoReference>`
   `+lat_0`/`+lon_0` (and chosen altitude datum). If they differ, the two worlds slide apart
   linearly with distance from origin.
2. Handedness: CARLA's `-Y = north`. Cesium's ENU has `+Y_east, +X... ` handled internally, but the
   actor pose you feed Cesium must already be in the lat/lon frame from step (2), so the only flip
   you own is the `-Y` already baked into `GeoLocation::Transform`.
3. Units: convert UE cm ↔ CARLA m (×/÷100) at every UE boundary; feed Cesium metres/degrees.

**Practical recommendation:** drive the geo conversion off CARLA's own `GeoLocation::Transform`
(either via a GNSS sensor on the drone, or by porting the 30-line Mercator block into CarlaNet) so the
truth pipeline and Cesium share *exactly* the same (spherical-Mercator) origin convention, then set
the Cesium origin to the same lat0/lon0. Accept the spherical-vs-ellipsoid error (R1) or correct it.

---

## B. HIGH-ALTITUDE EGO CAMERA + SENSOR CAPTURE

### B.1 Modeling the drone ego

CARLA cameras are *sensor actors* (`sensor.camera.rgb`). They can be spawned **with or without a
parent** — `SpawnActor(camera_bp, transform)` (no `attach_to`) gives a free-floating world-anchored
camera. This is exactly what we want for a nadir drone; no vehicle is required.

Evidence in-repo: `g:\Projects\CarlaUE_5_7_4\CaptureVideo.py` already implements this. With
`--overhead` (lines 50–58) it spawns the RGB camera at map-centroid `(cx, cy, altitude_m)` with
`Rotation(pitch=-90)` (straight-down nadir) **and no `attach_to`** — a pure camera rig. Oblique views
are just a different pitch/yaw.

Three viable rig models:
- **Free camera-only actor** (recommended): spawn `sensor.camera.rgb` at world transform, no parent.
  Move it each tick via `SetActorTransform` (CarlaNet `SetActorTransformAsync`,
  `CarlaClient.cs:237`) to fly a trajectory. Cleanest; this is the CaptureVideo `--overhead` path.
- **Camera attached to an invisible/dummy actor** ("drone" actor) with `SpringArmGhost` attachment
  for smoothing — useful if we want a physical drone body in truth data.
- **Spectator** — `GetSpectatorAsync` (`CarlaClient.cs`, §8.6) gives the viewport camera but it is not
  a streaming sensor, so it cannot deliver frames to the capture pipeline. Not suitable.

Camera config (`MakeCameraDefinition`,
`...\Actor\ActorBlueprintFunctionLibrary.cpp:313–342`): attributes `fov` (default 90), `image_size_x`
(800), `image_size_y` (600), plus `sensor_tick` (added in `AddVariationsForSensor`,
`ActorBlueprintFunctionLibrary.cpp:248`) and a full set of lens/PP attributes. `bRestrictToRecommended
= false` on resolution and FOV → **no hard cap** in the blueprint definition; resolution is limited
only by GPU/render-target memory and UE's texture size limits.

Attachment / spawn delivery: `ASceneCaptureSensor` is the base; FOV → `CaptureComponent2D->FOVAngle`
(`SceneCaptureSensor.cpp:90–99`); width/height → `ImageWidth/ImageHeight`
(`SceneCaptureSensor.h:602,606`). Captured image is delivered as a **BGRA** stream
(`ImageSerializer`: 12-byte sub-header `width,height,fov` + `W·H·4` BGRA, A=255), which
**`CarlaNet.Sensors.ImageSensorData`** (`carla\CarlaNet\src\CarlaNet.Sensors\ImageSensorData.cs`)
deserializes. The 48-byte sensor header carries the camera **world transform** at capture time
(SensorHeader §10.1) — important for truth (see C).

### B.2 Feasibility of high-altitude views

- **No far-clip override in CARLA's scene capture.** Grep of `SceneCaptureSensor.cpp` for
  `far/Clip/Ortho/Projection` shows CARLA never sets a custom far plane or projection — it only sets
  `FOVAngle` and post-process. UE's `USceneCaptureComponent2D` therefore uses the engine default
  perspective projection with a **reversed-Z infinite far plane**, so "looking from km altitude" does
  **not** clip the ground geometry. Depth *precision* far away is a non-issue for an EO **color**
  image (we are not consuming the depth buffer for the drone RGB).
- **World bounds / large-world-coordinates:** This is the real altitude concern. At multi-km
  altitude + large horizontal extent, single-precision UE world coordinates lose positional
  precision and can jitter. CARLA's answer is `ALargeMapManager` (origin rebasing). The root analysis
  (`CARLA_SENSOR_INTEGRATION_ANALYSIS.md` §5) flags that CARLA does **not** set
  `bIgnoreLocalPlayerOnRebase` and recommends adding it in `LargeMapManager::BeginPlay()` so rebasing
  follows the hero, not a spectator. For a high-altitude rig the drone camera (or its dummy parent)
  should be the rebase hero. Cesium-for-Unreal independently does floating-origin rebasing; the two
  rebasing systems must be reconciled (risk R2).
- **Stable nadir frame per fixed timestep:** Use **synchronous mode** (`EpisodeSettings.SynchronousMode
  = true`, `FixedDeltaSeconds` set) + `world.tick()` (`SendTickCueAsync`, §8.2) and a
  per-sensor `listen` callback (`SubscribeToStream`, §8.15). The camera renders on `PostPhysTick`
  (`SceneCaptureSensor.cpp:944–948` → `EnqueueRenderSceneImmediate`), so exactly one frame is produced
  per tick. The existing `CaptureVideo.py` uses `wait_for_tick()` (async/non-deterministic); for
  truthed training data we should switch to deterministic sync-mode tick + a sensor queue (CARLA's
  documented `sensor_synchronization` pattern) so each image is paired 1:1 with a known frame id and
  truth snapshot.

### B.3 EO (electro-optical) approximation

Out-of-the-box, via the RGB camera's PostProcess attributes + weather:
- **Grayscale / NIR look:** there is no native single-band EO sensor. Approximate with a post-process
  material or by converting BGRA→luma client-side. `CaptureVideo.py:78` already does
  `ColorConverter.Raw` then takes BGR. A custom PP material (the project already ships a PP/segmentation
  material pipeline, see `CARLA_SENSOR_INTEGRATION_ANALYSIS.md` §1) can desaturate / tone-map to mimic
  panchromatic EO.
- **Motion blur, bloom, exposure, chromatic aberration, lens distortion** — all exposed as camera PP
  attributes (`MakeCameraDefinition` lens block, `ActorBlueprintFunctionLibrary.cpp:343+`) and verified
  present in UE5.7.4 `Scene.h` (root analysis §3). Auto-exposure: note the
  `AutoExposureCalibrationConstant_DEPRECATED` caveat (root analysis §3) — deprecated but compiles,
  silently no-ops; pin manual exposure for repeatable EO radiometry.
- **Atmospheric haze / scattering:** `WeatherParameters` (FogDensity, FogDistance, FogFalloff,
  MieScatteringScale, RayleighScatteringScale, ScatteringIntensity — `CarlaNet.md` §6.9) drive
  atmospheric haze, which is the dominant high-altitude EO cue. Sun position
  (SunAzimuthAngle/SunAltitudeAngle) controls shadows/illumination.
- **GSD (ground sample distance)** is *derived*, not a sensor setting:
  `GSD ≈ (2 · altitude · tan(FOV/2)) / image_width` (per pixel, nadir). It is fully determined by
  altitude + FOV + resolution, which we control. e.g. 2000 m altitude, FOV 30°, width 4096 px →
  ground swath ≈ 1072 m, GSD ≈ 0.26 m/px. Choose FOV/resolution to hit the target GSD of the real EO
  sensor being emulated.
- **Custom needed:** true narrow-band NIR spectral response, sensor noise model (shot/read noise),
  and MTF blur are not native — add as a PP material + client-side noise if radiometric fidelity
  matters. For detector training, geometric + haze + grayscale approximation is usually sufficient.

---

## C. TRUTHED TELEMETRY EXPORT VIA CARLANET

### C.1 Per-frame truth available

The **world observer stream** (`FWorldObserver` / EpisodeState, §10.14) is the per-tick truth feed.
CarlaNet parses it in two places:
- `carla\CarlaNet\src\CarlaNet.Sensors\EpisodeStateSensorData.cs` — full parse: every actor's
  `Id`, `State`, `Transform` (loc+rot), `Velocity`, `AngularVelocity`, `Acceleration`, plus the
  54-byte `TypeDependentState` union (vehicle control/speed-limit/traffic-light/failure state).
- `carla\CarlaNet\src\CarlaNet.Transport\CarlaClient.cs` — the live cache: `OnWorldObserverFrame`
  (line 515) fills `_actorCache` (`ConcurrentDictionary<ActorId, ActorSnapshot>`, line 44). Accessors:
  `GetActorTransform/Velocity/AngularVelocity/Acceleration` (lines 589–592), `GetActorSnapshot`
  (593), `GetCachedActorIds` (599). `OnTick` event (line 62) fires once per observer frame with
  frame id + timestamp — the hook to pair truth with each image.

Per captured image we can therefore export, time-synced by frame id: every actor's world transform,
velocity, angular velocity, acceleration, actor state, and (via the union) vehicle dynamic state.
Type/label: actor type-id and semantic tags come from the `rpc::Actor` record
(`Actor.SemanticTags`, `Actor.Description.Id` e.g. `vehicle.tesla.model3`), retrieved once at spawn
or via `GetActorsByIdAsync` (§8.6), then joined by `ActorId`.

**Lat/lon truth:** feed each actor's observer `Transform.Location` through the A.2 chain
(`GeoLocation::Transform`) to get georeferenced lat/lon/alt per actor per frame. (Requires porting
the Mercator math into CarlaNet, or running the actors through the server GNSS path — see R3.)

### C.2 The bounding-box gotcha (CONFIRMED)

The observer cache **does not carry per-actor bounding boxes**. Confirmed:
- `ActorSnapshot` (`CarlaClient.cs:26–36`) has NO BoundingBox field.
- `ActorDynamicState` (`EpisodeStateSensorData.cs:22–31`) has NO BoundingBox field — the 119-byte
  record is pose + kinematics + 54-byte union only (matches `static_assert(sizeof==119)`,
  `CarlaNet.md` §13.6).

The bounding box lives on the **`rpc::Actor` record** instead:
- `CarlaNet.Types.Rpc.Actors.Actor` has `BoundingBox` at `[Key 3]` (`CarlaNet.md` §6.5,
  `carla\CarlaNet\src\CarlaNet.Types\Rpc\Actors\Actor.cs`). `BoundingBox` =
  `{Location, Extent, Rotation}` (`carla\CarlaNet\src\CarlaNet.Types\Geom\BoundingBox.cs`).
- This `BoundingBox` is the actor-**local** box (centre offset + half-extents in the actor's own
  frame), returned by `SpawnActor` / `GetActorsById` (§8.6/§8.7). It is **static** per actor, so
  fetch it **once** and cache it keyed by `ActorId`.

**Accurate boxes per frame = static local box (from `rpc::Actor`) + dynamic world transform (from
observer cache).** Compose: `world_box_corner = ActorWorldTransform ∘ (bbox.Location ± bbox.Extent)`.
This is exactly CARLA's documented approach (`actor.bounding_box` + `actor.get_transform()`). Walkers
have the same `BoundingBox` field. Level/static-object boxes are available via
`GetLevelBoundingBoxesAsync` / `GetEnvironmentObjectsAsync` (§8.3) if buildings/props need truthing.

### C.3 2D image-space ground truth (bbox → pixels)

CARLA's documented client-side projection (confirmed against
`carla-ue5.readthedocs.io/.../tuto_G_bounding_boxes/`):

1. **Intrinsics K from FOV** (no calibration needed — pinhole from the camera's `fov`/`w`/`h`):
   ```
   focal = w / (2 · tan(fov · π / 360))     # fov in degrees
   K = [[focal, 0,     w/2],
        [0,     focal,  h/2],
        [0,     0,      1  ]]
   ```
   Square pixels, principal point at image centre. `fov`, `w`, `h` are exactly our camera attributes
   (B.1), so K is fully determined by config — and the captured frame's image sub-header even carries
   `width,height,fov_angle` (§10.2), so K can be rebuilt from the frame itself.

2. **World → camera:** `w2c = inverse(camera_world_transform)`. The camera world transform per frame
   is available **in the 48-byte sensor header** of every image frame (`SensorHeader.Location/Rotation`,
   §10.1) — CarlaNet exposes it on `SensorFrame`/`SensorHeader`
   (`carla\CarlaNet\src\CarlaNet.Sensors\SensorHeader.cs`). So we have the camera extrinsics per frame
   with zero extra RPC.

3. **Project** (CARLA's `get_image_point`):
   ```
   p_cam = w2c · [x, y, z, 1]
   p_cam = (p_cam.y, -p_cam.z, p_cam.x)      # UE(left-handed) → standard CV axes
   p_img = K · p_cam ;  u = p_img[0]/p_img[2] ; v = p_img[1]/p_img[2]
   ```
   2D box = min/max of the 8 projected world-corners of the actor's 3D box (C.2). Cull points with
   `p_cam.x ≤ 0` (behind camera).

**CarlaNet exposes everything needed for this:** camera transform + fov (sensor header), image w/h
(image sub-header), actor world transforms (observer cache), actor local boxes (`rpc::Actor`). The
projection itself is pure client-side math we run in Python or C# — no server feature required. CARLA
even ships the reference implementation; we replicate it on the CarlaNet-fed data.

---

## Key risks / open questions

- **R1 — Spherical Mercator vs WGS84 ellipsoid (BIGGEST GEOREF RISK).** CARLA's `GeoLocation::Transform`
  uses a *spherical* Web-Mercator (R=6378137, `GeoLocation.cpp:23,37-48`); Cesium uses the *WGS84
  ellipsoid*. Feeding CARLA's Mercator lat/lon straight into Cesium introduces a projection mismatch
  that grows with distance from origin and with latitude (Mercator scale distortion ∝ 1/cos(lat); and
  ellipsoid-vs-sphere northing error can reach tens-to-hundreds of metres over a city-scale extent at
  mid/high latitude). **Mitigations:** (a) keep the city small and near the origin so the error is
  sub-GSD; (b) replace CARLA's spherical formula with a proper ellipsoidal/local-tangent-plane
  projection in the truth path so both systems agree; (c) treat CARLA local metres as a local ENU
  tangent plane and let *Cesium* do the ellipsoidal ENU→ECEF (recommended — bypasses CARLA's spherical
  Mercator entirely and is the cleanest reconciliation). This is the single biggest feasibility risk
  in the georef+sensor+telemetry chain.

- **R2 — Two competing floating-origin/rebase systems.** CARLA `ALargeMapManager` rebases on the hero;
  Cesium-for-Unreal rebases on its own georeference origin. At multi-km altitude both want to move the
  world origin. They must be coordinated (shared rebase trigger, or disable one). `bIgnoreLocalPlayerOnRebase`
  is not set in CARLA (root analysis §5) — recommended fix already documented there.

- **R3 — CarlaNet has the georef *origin* but not the *transform*.** `GeoReferenceParser` and
  `Map.GeoReference` are ported, but the Mercator `local→lat/lon` math is **not** in CarlaNet.
  To emit lat/lon truth client-side we must either port ~30 lines from `GeoLocation.cpp`, or attach a
  GNSS sensor to each actor (impractical at scale), or attach one GNSS to the drone and transform other
  actors relative to it. Porting the math (or R1's tangent-plane approach) is the right call.

- **R4 — Default georeference origin is Barcelona (42,2).** If a procedurally generated map's `.xodr`
  lacks `+lat_0/+lon_0`, CARLA silently uses lat 42/lon 2 (`GeoReferenceParser.cpp:51-55`,
  mirrored in CarlaNet). The Cesium origin must be set to whatever the `.xodr` actually contains, or
  truth will be globally offset. Always author `<geoReference>` explicitly.

- **R5 — Bounding box must be joined, not read from observer.** Confirmed: observer cache has no
  bbox. Pipeline must fetch `rpc::Actor.BoundingBox` once per actor and compose with the per-frame
  observer transform. Forgetting this yields pose-only truth with no boxes.

- **R6 — Observer cache must be explicitly started and warmed.** `world.get_actors()` / the cache
  return 0/empty until `start_observer()` runs and the first frame lands (~33 ms); the observer was
  made opt-in to avoid racing initial RPCs (carlanet memory, `CarlaClient.cs:505-515`). Capture loop
  must start the observer and wait one tick before trusting truth.

- **R7 — Sync-mode capture not yet implemented in CaptureVideo.py.** Current script uses async
  `wait_for_tick()`; deterministic 1:1 image↔truth pairing needs sync mode + a sensor queue. Low
  effort, but required for clean training labels.

- **R8 — GBuffer auxiliary streams unavailable in this UE5.7.4 port.** `GBufferView.h` /
  `CaptureSceneWithGBuffer` were not forward-ported (root analysis §3). Depth/normals GBuffer streams
  for the drone are unavailable; RGB + semantic segmentation cameras are fine. Not needed for EO RGB +
  bbox truth, but rules out cheap per-pixel depth truth from the same camera.

### Source-file index
- `LibCarla\source\carla\geom\GeoLocation.{h,cpp}` — geo type + Mercator transform
- `LibCarla\source\carla\geom\Location.h` (82-87) — cm↔m unit conversion
- `LibCarla\source\carla\opendrive\parser\GeoReferenceParser.cpp` — origin extraction
- `...\Plugins\Carla\Source\Carla\Sensor\GnssSensor.cpp` — server geo conversion path
- `...\Plugins\Carla\Source\Carla\Sensor\SceneCaptureSensor.{h,cpp}` — camera/FOV/render
- `...\Plugins\Carla\Source\Carla\Actor\ActorBlueprintFunctionLibrary.cpp` (302-342, 1360-1368) — camera attrs
- `...\Plugins\Carla\Source\Carla\MapGen\LargeMapManager.cpp` — rebasing
- `g:\Projects\CarlaUE_5_7_4\CaptureVideo.py` — existing nadir capture pattern
- `carla\CarlaNet\src\CarlaNet.Sensors\EpisodeStateSensorData.cs`, `ImageSensorData.cs`, `SensorHeader.cs`
- `carla\CarlaNet\src\CarlaNet.Transport\CarlaClient.cs` (observer cache 505-599)
- `carla\CarlaNet\src\CarlaNet.Map\OpenDrive\Parser\GeoReferenceParser.cs`, `Road\Map.cs`
- `carla\CarlaNet\src\CarlaNet.Types\Geom\{GeoLocation,BoundingBox}.cs`, `Rpc\Actors\Actor.cs`

### External confirmations
- CARLA bbox→image projection (K matrix, get_image_point, axis swap): carla-ue5.readthedocs.io
  tuto_G_bounding_boxes.
- Cesium georeference origin params (OriginLatitude/Longitude/Height, ECEF/ENU, WGS84 ellipsoid):
  cesium.com cesium-unreal ACesiumGeoreference / GeoTransforms reference.
</content>
</invoke>
