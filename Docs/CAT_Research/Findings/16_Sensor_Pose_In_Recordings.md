# Sensor Pose in Recordings — the EO platform as a CoT air track per captured frame

**Date:** 2026-07-07 · **Status:** PROPOSED (design for review; implementation to follow).
**Datum:** ellipsoidal WGS84 (HAE), bare-earth referenced — `project_datum_decision`.
**Relates to:** [09_Telemetry_CoT_Contract.md](09_Telemetry_CoT_Contract.md) (the vehicle-truth schema this extends),
[03_Georef_Sensors_Telemetry.md](03_Georef_Sensors_Telemetry.md).

## 1. Purpose

The native recorder (`FrameRecorder`) already pairs each captured frame's pixels with frame-coherent
vehicle truth (a CoT-XML sidecar) and the solar state (an XML `<_solar>` block plus a `carla:solar` PNG
tEXt chunk). It does **not** record the **collection platform** — the airborne camera's own geodetic
pose, pointing, and optics. For an EO/ISR truth product this is a gap: capturing these scenes in the real
world requires hardware occupying volume in the atmosphere (a UAS, a pod-equipped RPA, a manned ISR
aircraft), and a truth product should carry that platform as a first-class track so a consumer can
reconstruct look angles, validate target geolocation, render the sensor field-of-view, and treat each
still as a fully self-describing capture. This finding specifies the schema and a concrete implementation.

This work also **removes the legacy in-Python recorder fallback** (§8): recording now lives entirely in
CarlaNet (C#) for performance, and the pure-Python path is obsolete.

## 2. Current state (the gap)

- **Pixels ↔ vehicle truth ↔ sun** are already frame-coherent. In synchronous mode the world is frozen
  between ticks, so the world-observer snapshot the telemetry is drawn from corresponds to the same tick
  as the frame; the solar block rides in the same world-observer datagram.
  ([FrameRecorder.OnFrame](../../../CarlaNet/src/CarlaNet.Recording/FrameRecorder.cs),
  [VehicleTelemetryService.Compute](../../../CarlaNet/src/CarlaNet.Recording/VehicleTelemetryService.cs)).
- **What is written today:** the CoT XML holds `<events>` → one `<_solar>` + one `<event><point lat lon
  hae>` **per vehicle**; the PNG carries only a `carla:solar` tEXt chunk
  ([CotWriter](../../../CarlaNet/src/CarlaNet.Recording/CotWriter.cs),
  [SolarMetadata](../../../CarlaNet/src/CarlaNet.Recording/SolarMetadata.cs)).
- **What is missing:** the camera is an *unparented sensor actor*, so it never appears in the
  vehicle-filtered telemetry (`Compute` skips any actor whose `type_id` is not `vehicle.*`). Its pose,
  pointing, and optics are recorded nowhere. The only `lat`/`lon` in a recording besides the per-vehicle
  points is inside the solar block, and that is the **map georeference origin** used to compute the sun,
  not the sensor.

## 3. Where the pose comes from (the clean source)

Every sensor frame carries its **world capture transform** in the 48-byte sensor header — the exact pose
at which those pixels were rendered — exposed as `SensorFrame.SensorTransform`
([SensorFrame.cs](../../../CarlaNet/src/CarlaNet.Transport/Streaming/SensorFrame.cs), header fields
`LocationX/Y/Z`, `RotationPitch/Yaw/Roll`). `OnFrame` already receives this `frame`; today it reads only
the timestamp and payload. Using the header transform binds the pose to the pixels at the tightest
possible level (same frame, same header) — no world-observer cache lookup, no cross-stream tick-matching.
The local→geodetic conversion is the same `Geodesy.CarlaLocalToGeodetic(origin, x, y, z)` already used for
vehicle truth and for the interactive pixel picker.

The **optics** (horizontal FOV, any lens-distortion parameters) are camera-blueprint attributes, known
client-side at spawn; the **platform identity** (airframe class, callsign) is collection configuration,
also client-side (§6). The recorder combines the frame-header pose with these client-supplied inputs and
derives the full pinhole intrinsics.

## 4. Schema — the platform as a CoT air track

The platform is emitted as a first-class CoT `<event>` air track (not a scene annotation), so it renders
in TAK like any other track and its sensor field-of-view can be drawn natively. It is written into the
same `<events>` sidecar as the vehicle tracks (and mirrored into a PNG `carla:sensor` tEXt chunk so the
image alone is self-describing). Boresight and FOV use CoT's **standard `<sensor>` detail element**; the
full camera intrinsics ride in a custom `<_carla_intrinsics>` child (TAK ignores unknown detail).

### 4.1 CoT XML — an air-track `<event>`

```xml
<events captured="2026-07-07T18:00:00.000Z" count="3" source="truth">
  <_solar .../>
  <event version="2.0" uid="CARLA-SENSOR-42" type="a-f-A-M-F-Q" how="m-g"
         time="2026-07-07T18:00:00.000Z" start="2026-07-07T18:00:00.000Z"
         stale="2026-07-07T18:00:03.000Z">
    <point lat="38.9612345" lon="-119.7654321"
           hae="304.80"                          <!-- bare-earth ellipsoidal height m (§5) -->
           ce="0.0" le="0.0"/>                    <!-- truth = exact -->
    <detail>
      <contact callsign="OVERWATCH"/>
      <track course="270.0" speed="0.00"/>        <!-- platform course/speed, derived from pose deltas -->
      <sensor azimuth="270.0" elevation="-90.0" roll="0.0"
              fov="90.0" vfov="58.72" range="0" type="EO"
              model="sensor.camera.rgb"/>          <!-- standard CoT sensor FOV element -->
      <_carla_intrinsics width="1280" height="720"
              fx="640.00" fy="640.00" cx="640.00" cy="360.00"
              hfov_deg="90.0" vfov_deg="58.72" model="pinhole"
              distortion="none"                    <!-- or the raw CARLA lens params; see §4.4 -->
              align_offset_m="0.00"/>              <!-- physical_hae = hae + align_offset_m -->
    </detail>
  </event>
  <event uid="CARLA-TRUTH-123" .../>              <!-- vehicle tracks follow, unchanged -->
  ...
</events>
```

### 4.2 PNG tEXt — a `carla:sensor` chunk

Alongside the existing `carla:solar` chunk, compact JSON carrying the same content (so a consumer holding
only the image can recover the platform):

```json
{"uid":"CARLA-SENSOR-42","type":"a-f-A-M-F-Q","callsign":"OVERWATCH",
 "lat":38.9612345,"lon":-119.7654321,"hae":304.80,"align_offset_m":0.0,
 "az_deg":270.0,"el_deg":-90.0,"roll_deg":0.0,"course_deg":270.0,"speed_mps":0.0,
 "intrinsics":{"width":1280,"height":720,"fx":640.0,"fy":640.0,"cx":640.0,"cy":360.0,
               "hfov_deg":90.0,"vfov_deg":58.72,"model":"pinhole","distortion":"none"}}
```

### 4.3 Field conventions

| Field | Convention |
|---|---|
| `event.uid` | stable per platform; default `CARLA-SENSOR-<camera actor id>` (client-overridable) |
| `event.type` | CoT air-track type — platform classification, §4.4 (default `a-f-A-M-F-q`) |
| `event.how` | `m-g` (machine / GPS-derived), matching the truth vehicles |
| `point.lat`/`lon` | WGS84 degrees, 7 dp |
| `point.hae` | **bare-earth** ellipsoidal height, metres — camera physical altitude minus the height-align offset (§5), so the datum matches the vehicle `hae` |
| `point.ce`/`le` | truth = `0.0` (exact), as with the vehicle tracks |
| `sensor.azimuth` | boresight heading, deg true north, 0–360, cw from N. From yaw: `atan2(cos yaw, −sin yaw)` (CARLA +X=East, −Y=North) — same convention as the SCTMV compass |
| `sensor.elevation` | boresight elevation above horizon. CARLA pitch is +up, so `elevation = pitch` (−90 = nadir) |
| `sensor.roll` | camera roll, degrees |
| `sensor.fov`/`vfov` | horizontal / vertical field of view, degrees |
| `sensor.range` | slant range if a ground intersect is computed; `0` when not (optional) |
| `sensor.type`/`model` | `EO`; `sensor.camera.rgb` |
| `track.course`/`speed` | platform course-over-ground (deg true) and speed (m/s), derived client-side from successive pose deltas; course falls back to boresight azimuth when ~stationary |
| `_carla_intrinsics` | full pinhole intrinsics (§4.4) + `align_offset_m` (so `physical_hae = hae + align_offset_m`) |

### 4.4 Intrinsics and lens model

CARLA's RGB camera is a pinhole model with a centered principal point. From the horizontal FOV and the
frame width/height the recorder derives:

- `fx = fy = width / (2·tan(hfov/2))` (square pixels)
- `vfov = 2·atan(height / (2·fx))`
- `cx = width/2`, `cy = height/2`
- implied `K = [[fx, 0, cx], [0, fy, cy], [0, 0, 1]]`

`width`/`height` come from the decoded frame; `hfov` and any lens parameters are camera-blueprint
attributes passed in client-side. **Distortion:** CARLA's RGB sensor exposes lens parameters
(`lens_k`, `lens_kcube`, `lens_circle_falloff`/`_multiplier`, chromatic aberration) that are a
**non-standard** lens model — *not* Brown-Conrady. Default is effectively distortion-free. Record
`distortion="none"` at defaults; when non-default, capture the raw CARLA parameters and set `model`
explicitly to flag that downstream undistortion must use CARLA's lens model, not a standard one.

## 5. Datum — bare-earth, consistent with the vehicles

The vehicle `hae` in the telemetry is **bare-earth** referenced (physical altitude minus the height-align
seating offset), so it is directly comparable to ADS-B/GNSS truth (`project_telemetry_dtm_decoupling`).
The platform's `point.hae` is recorded in the **same** datum, so sensor and targets share one frame
end-to-end (honoring the locked "ellipsoidal WGS84 HAE end-to-end" decision, `project_datum_decision`).

**The offset is well-defined for the camera even though it is airborne.** The height-align offset is the
vertical gap `DSM − DTM` (photoreal surface minus bare-earth terrain) at a ground column — a function of
horizontal position **(X, Y) only**, independent of altitude, applied as a rigid vertical translation of
the road/ground geometry (`project_height_align_mechanism`). So the offset for "the column beneath the
camera" is defined regardless of how high the camera flies. It is looked up exactly as the vehicle path
does it ([VehicleTelemetryService.Compute](../../../CarlaNet/src/CarlaNet.Recording/VehicleTelemetryService.cs)):

- `none` → offset = 0 (physical already *is* bare-earth).
- `area` / `origin` → the scalar `_client.LastHeightAlignOffset`.
- `drape` → the per-cell grid sampled at the camera XY: `Sample(_offGrid, cam_x, cam_y)` (edge-clamped).

Then `hae = physical_hae − offset(cam_x, cam_y)`, and `align_offset_m` records the offset so the physical
platform altitude is always recoverable (`physical = hae + align_offset_m`).

**Caveats to document, not hide:**
- The EO camera is a *free* observer and may fly **outside** the OSM sandbox / drape grid, where the grid
  sample is edge-clamped — beyond the mapped area the drape offset is the nearest-edge value, not an exact
  local gap. In `area`/`origin` the scalar applies everywhere; in `none` it is moot.
- Exact *pixel-geometry* reconstruction (sensor→target rays that match the imagery) is done in **physical**
  space: use `physical_hae` for the camera (`hae + align_offset_m`) and the physical altitude of each
  target. Per-vehicle physical altitude is not currently emitted (only bare-earth); adding it is a separate
  enhancement. In the default `none` mode bare-earth and physical coincide, so this is a non-issue there.

## 6. Platform classification — client-attributed

The server (CARLA/UE) only renders pixels from an unparented sensor actor; it has no concept of the
airframe the sensor is notionally mounted on. Platform identity is **collection semantics, not simulation
state**, so it is attributed **client-side** at collection-config time — SCTMV CLI arguments flowing
through `start_recording` into the recorder, exactly as `affiliation` and `stale` already flow. Clean seam:
the server owns the physics/pixels (the pose), the client owns the collection identity (the platform).

Client-supplied parameters:

| parameter | default | meaning |
|---|---|---|
| `platform_type` | `uas-fixed` | airframe class (alias below) or a raw CoT type string |
| `platform_affiliation` | `f` (friend) | the platform is our own collection asset — distinct from the vehicles' default neutral `n` |
| `platform_callsign` | `OVERWATCH` | `contact.callsign` |
| `platform_uid` | `CARLA-SENSOR-<camera id>` | stable track uid |

Airframe-class aliases → CoT type. Values verified against the MITRE-authored CoT type catalog
`CoTtypes.xml` (the `.` is the affiliation placeholder, filled from `platform_affiliation`). Note the
case convention in the catalog: **uppercase `-Q`** is the *military* drone/RPV/UAV leaf, **lowercase `-q`**
is the *civil* one:

| alias | example airframe | CoT type | catalog nomenclature |
|---|---|---|---|
| `uas-fixed` (default) | MQ-9 + EO/IR pod | `a-{aff}-A-M-F-Q` | Air/Mil/Fixed/Drone,RPV,UAV |
| `uas-rotary` | rotary-wing UAS | `a-{aff}-A-M-H-Q` | Air/Mil/Rotor/Drone,RPV,UAV |
| `manned-fixed` | Twin Otter, King Air ISR | `a-{aff}-A-M-F` | Air/Mil/Fixed |
| `manned-rotary` | helicopter | `a-{aff}-A-M-H` | Air/Mil/Rotor |

Civil variants exist in the same catalog for a commodity/civilian collection asset and can be selected via
the raw-string escape hatch: fixed-wing civil drone `a-{aff}-A-C-F-q` (Air/Civ/fixed/rpv,drone,uav), civil
fixed manned `a-{aff}-A-C-F`, civil rotary `a-{aff}-A-C-H`. The catalog has no civil *rotary drone* leaf, so
a small civilian quad (e.g. a DJI) is best represented as civil rotary `a-{aff}-A-C-H` or, if treated as a
friendly organic ISR asset, the military rotary UAV `a-{aff}-A-M-H-Q`. A raw CoT type string always passes
through verbatim (escape hatch for anything the alias table doesn't cover).
If the camera is ever *attached* to a modeled airframe actor rather than flown unparented, the
classification could instead derive from that actor's blueprint attributes — the same mechanism the
vehicle `base_type`/`special_type` symbol uses. Out of scope now (the EO observer is a free sensor).

## 7. Implementation sketch (native path)

Touch points, entirely within the existing "read frame → build Job → workers write it" flow; no new RPCs
in the hot path (pose derives from the frame header; optics + identity are client-supplied config).

1. **`SensorPlatformOptions` (new, C# record)** — the client-supplied config bundle: `hfov_deg`, optional
   lens-distortion parameters, `platform_type` (resolved to a CoT type), `platform_affiliation`,
   `platform_callsign`, `platform_uid`. Passed to the `FrameRecorder` ctor.

2. **`SensorPose.cs` (new)** — immutable record of the *derived* per-frame platform state: geodetic
   position (`Lat, Lon, Hae, AlignOffsetM`), pointing (`AzimuthDeg, ElevationDeg, RollDeg`), motion
   (`CourseDeg, SpeedMps`), and full intrinsics (`Width, Height, Fx, Fy, Cx, Cy, HFovDeg, VFovDeg, Model,
   Distortion`).

3. **`VehicleTelemetryService.cs`** — factor the offset lookup out of `Compute` into a public
   `double OffsetAt(double x, double y)` (none/scalar/drape, reused by both vehicles and the camera), and
   add `ComputeSensorPose(GeoLocation origin, Transform tf, Transform? prevTf, double dtSeconds,
   SensorPlatformOptions opt, int w, int h)` that runs `Geodesy.CarlaLocalToGeodetic`, subtracts
   `OffsetAt`, computes az/el/roll and the derived intrinsics, and derives course/speed from the pose delta.

4. **`FrameRecorder.cs`** — take `SensorPlatformOptions` in the ctor; add `SensorPose? Sensor` to the
   `Job` record; in `OnFrame`, after decode, call `_telemetry.ComputeSensorPose(...)` with the current
   `frame.SensorTransform` and the previous frame's transform + Δt (null when `!_haveOrigin`) and put it on
   the `Job`; in `WorkerLoopAsync`, pass it to the PNG text chunks and to `CotWriter`.

5. **`SensorMetadata.cs` (new)** — parallel to `SolarMetadata`: `PngTextChunks(SensorPose?)` yielding the
   `carla:sensor` chunk (empty when null), plus a `ToJson`. `FrameRecorder` concatenates the solar and
   sensor chunk sequences for `PngEncoder.WriteBgraToFile`.

6. **`CotWriter.cs`** — take a `SensorPose? sensor = null` param; when present, write the platform
   `<event>` (§4.1) with its `<point>`, `<contact>`, `<track>`, standard `<sensor>`, and
   `<_carla_intrinsics>` children, before the vehicle events. Reuse the existing `Iso`/`F` helpers.

7. **Plumb the config** — extend `world.start_recording(...)` in the Python shim
   ([carlanet/__init__.py](../../../CarlaNet/python/carlanet/__init__.py)) to accept the sensor/platform
   options (read from the camera blueprint attributes + CLI), building `SensorPlatformOptions`; SCTMV's
   `NativeRecorder.apply_want` supplies the FOV/lens attributes and new `--platform-*` CLI args.

## 8. Removal of the in-Python recorder fallback

SCTMV currently ships two recorder backends and picks one at startup
([SCTMV.py](../../../CarlaNet/python/SCTMV.py)):

- **`NativeRecorder`** — drives the C# `FrameRecorder` (encoding on the .NET thread pool, off the GIL). The
  production path.
- **`Recorder`** — a pure-Python fallback (pygame `image.save` + ElementTree) used only when the
  `CarlaNet.Recording` assembly is absent (`_CARLANET_RECORDING_AVAILABLE` is false).

The fallback is **obsolete** and is removed as part of this work. If the CarlaNet recording assembly is not
built, effectively nothing else in the port runs either (the whole client is CarlaNet), so a
recording-only Python fallback guards a state that does not occur in practice; it also carries limitations
the native path does not (it cannot embed PNG tEXt, so it drops `.solar.json` sidecars). Removing it also
eliminates a dual-path burden for the new platform-track data — only one writer needs it.

**Changes:** delete the `Recorder` class and the `_CARLANET_RECORDING_AVAILABLE` branch that selects it;
`NativeRecorder` becomes the sole recorder. If the recording assembly is genuinely missing, recording is
reported unavailable (the existing `NativeRecorder.apply_want` already handles and messages that case)
rather than silently falling back.

## 9. Open questions / decisions

Resolved:
- **Datum** — bare-earth, consistent with the vehicle `hae`; `align_offset_m` recorded for physical
  recoverability (§5).
- **Track vs. annotation** — the platform is a **CoT air-track `<event>`** with a standard `<sensor>`
  FOV element (§4), not a scene annotation.
- **FOV source** — **full pinhole intrinsics** (`fx/fy/cx/cy`, `hfov/vfov`, `K`) plus the lens-model note
  (§4.4).
- **In-Python fallback** — removed (§8).
- **Classification attribution** — client-side, at collection-config time (§6).
- **CoT type letters (§6)** — verified against the MITRE `CoTtypes.xml` catalog: military drone leaf is
  uppercase `-Q` (`a-{aff}-A-M-F-Q` fixed, `a-{aff}-A-M-H-Q` rotary); lowercase `-q` is the civil branch.
- **Default platform class** — `uas-fixed` = `a-f-A-M-F-Q` (Air/Mil/Fixed/Drone,RPV,UAV).
- **Distortion depth (§4.4)** — record `distortion="none"` at defaults; capture the raw CARLA lens
  parameters (flagged by `model`) when non-default.

Open for review: none — design is settled; proceeding to implementation.

## 10. Verification (the payoff)

With the platform recorded as a track, each still is self-describing: platform position + pointing + FOV +
full intrinsics + the sun + every vehicle's geodetic truth, all at one frame-coherent instant and in one
datum. That enables sensor→target look-angle reconstruction, projection of any target's geodetic point
back to pixels (via the recorded `K` and pose) to validate the pixel-picker geolocation math, native TAK
rendering of the sensor field-of-view cone, and a complete input for the planned truth-vs-CV scoring
harness.
