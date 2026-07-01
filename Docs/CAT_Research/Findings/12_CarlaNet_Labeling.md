# CarlaNet.Labeling — auto-bbox labels paired with the existing CoT recorder

**Date:** 2026-06-30 · **Status:** sketch / design proposal · **Sibling:** [09_Telemetry_CoT_Contract.md](09_Telemetry_CoT_Contract.md), [../../CarlaNet/src/CarlaNet.Recording/](../../CarlaNet/src/CarlaNet.Recording/)

## 1. Why this finding exists

Earlier discussion considered using CARLA's instance/semantic segmentation cameras to auto-derive
bounding boxes for detector training. That path is the wrong tool **for this stack**, for two
reasons specific to our derivation:

1. **The photoreal scenery is not a CARLA actor.** Cesium 3D Tiles (Cesium OSM Buildings / World
   Terrain / self-hosted photogrammetry) are streamed and rendered by `Cesium3DTileset`, not by
   any `AActor` registered with the CARLA episode. CARLA's semantic seg camera can therefore label
   *vehicles + the CARLA road mesh* but everything else (every building, every facade, every
   terrain pixel) falls into "Unlabeled". The instance seg camera is the same story. Whatever we'd
   intended to gain from semantic seg over the photoreal world is unavailable in principle, not
   just unwired.
2. **We already have a better source.** `CarlaNet.Recording` (`FrameRecorder` + `CotWriter` +
   `VehicleTelemetryService`) is already producing time-locked PNG + CoT-XML pairs in which each
   `<event>` carries the actor's geodetic position, heading, velocity, size, type, color, and
   role. Every field the bbox labeler needs is already being computed and persisted **per frame,
   on the same thread, against the same world-observer snapshot**. The labeler is a projection
   step on top of the data the recorder already assembles — not a parallel pipeline.

So this finding switches the auto-labeling source from "CARLA seg cameras" to "the CoT truth
pipeline" and sketches the C# module that does it.

## 2. The pivot in one paragraph

For the existing CoT contract we map each vehicle's CARLA-local pose through
`Geodesy.CarlaLocalToGeodetic` to get (lat, lon, HAE). For training labels we map that **same**
vehicle's CARLA-local 3D bounding box (8 corners) through the EO camera's intrinsics + extrinsics
to get a 2D OBB in image pixels. Same actor cache, same snapshot, same instant, same world. The
detector and the truth feed are then co-derived from one source and stay coherent by construction
— exactly the property §1.1 of the CoT contract asks for ("Same shape ⇒ truth-vs-detection
scoring is a direct diff").

## 3. What's already in place (do not re-implement)

- **`SensorHeader` carries the camera world pose at capture.** Bytes 24..47 of every sensor frame
  are `(Location.{X,Y,Z}, Rotation.{Pitch,Yaw,Roll})` for the sensor at capture. `FrameRecorder`
  already reads frames via `client.SubscribeToStream(streamToken, OnFrame)`; the EO camera's
  world transform per captured frame is right there in `frame.Header.Transform`.
- **`ImageSensorData` carries the intrinsics.** `Width`, `Height`, `FovAngle` arrive in the same
  payload `FrameRecorder` is already deserialising. K is one line:
  `f = W / (2·tan(fov_h/2));  cx = W/2;  cy = H/2`. (This is the same formula `eo_observer.py`
  uses for its Ctrl+LMB world-pick math, line 216 — invert that and you have world→pixel.)
- **`VehicleTelemetryService.Compute(origin)`** already iterates `GetCachedActorIds()`, refreshes
  per-actor descriptions on first sight (`GetActorsByIdAsync` once per new id), reads each
  vehicle's live `Transform`/`Velocity` from the zero-RPC actor cache, and returns the per-vehicle
  record. The bbox extent is right there on `meta.BoundingBox.Extent` and the yaw on
  `snap.Transform.Rotation.Yaw`. Today those two fields stop short of being projected; that's the
  only gap.
- **`FrameRecorder` already pairs PNG + CoT XML by filename stem** (`SCTMV_<UTC>.png` /
  `SCTMV_<UTC>.xml`). The labeler emits one more sidecar against the same stem.

The whole patch is a few hundred lines of C# living next to `CotWriter.cs`.

## 4. The sketch — `CarlaNet.Labeling`

### 4.1 Records

```csharp
namespace CarlaNet.Labeling;

/// One vehicle's CARLA-local pose-plus-extent at the capture instant — the projection input.
/// Mirrors VehicleTelemetry but stays in CARLA-local coordinates (no geodetic transform), because
/// the projection wants Location.{X,Y,Z}+Rotation.Yaw and the half-extents directly.
public sealed record VehicleLabelSource(
    uint Id,
    string TypeId,
    string BaseType,
    string SpecialType,
    double X, double Y, double Z,             // CARLA-local metres, left-handed, +X=East, -Y=North
    double YawDeg,                            // world yaw (CARLA convention)
    double HalfLengthX, double HalfWidthY, double HalfHeightZ);  // from BoundingBox.Extent

/// One per visible vehicle per captured frame. The "modal" bbox (depth-trimmed) is what the
/// detector trains against; the "amodal" 8-corner hull is retained for later amodal heads / for
/// scoring partially-occluded predictions.
public sealed record VehicleLabel(
    uint ActorId,
    string Class,                              // base_type [+ "-emergency" suffix when special]
    double Cx, double Cy, double Wpx, double Hpx, double AngleRad,   // 2D OBB (image pixels)
    double AmodalCxAabb, double AmodalCyAabb, double AmodalWAabb, double AmodalHAabb,
    double VisibleFraction,                    // 0..1, depth-test pass / projected-area
    double YawWorldDeg,                        // for CoT track.course parity
    double RangeMetres);                       // camera→actor centre, useful as a weight
```

### 4.2 The service — `VehicleLabelService`

```csharp
namespace CarlaNet.Labeling;

public sealed class VehicleLabelService
{
    private readonly CarlaClient _client;
    private readonly Dictionary<ActorId, Actor> _meta = new();

    public VehicleLabelService(CarlaClient client) => _client = client;

    /// Source data for the labeller. Same loop shape as VehicleTelemetryService.Compute, but stops
    /// at CARLA-local pose + bbox extent (no Geodesy call).
    public IReadOnlyList<VehicleLabelSource> Snapshot()
    {
        IReadOnlyList<ActorId> ids = _client.GetCachedActorIds();

        List<ActorId>? unknown = null;
        foreach (var id in ids)
            if (!_meta.ContainsKey(id)) (unknown ??= new()).Add(id);
        if (unknown is { Count: > 0 })
        {
            var fetched = _client.GetActorsByIdAsync(unknown).GetAwaiter().GetResult();
            foreach (var a in fetched) _meta[a.Id] = a;
        }

        var outp = new List<VehicleLabelSource>(ids.Count);
        foreach (var id in ids)
        {
            if (!_meta.TryGetValue(id, out var meta)) continue;
            string typeId = meta.Description.Id;
            if (!typeId.StartsWith("vehicle.", StringComparison.Ordinal)) continue;

            var snap = _client.GetActorSnapshot(id);
            if (snap is null) continue;
            var loc = snap.Transform.Location;
            double yaw = snap.Transform.Rotation.Yaw;
            var ext = meta.BoundingBox.Extent;

            var attrs = meta.Description.Attributes;
            string baseType = Attr(attrs, "base_type", "")
                            ?? (Attr(attrs, "number_of_wheels", "4") == "2" ? "motorcycle" : "car");

            outp.Add(new VehicleLabelSource(
                id, typeId, baseType, Attr(attrs, "special_type", ""),
                loc.X, loc.Y, loc.Z, yaw, ext.X, ext.Y, ext.Z));
        }
        return outp;
    }

    private static string Attr(IReadOnlyList<ActorAttributeValue> a, string id, string dflt)
    { foreach (var x in a) if (x.Id == id) return x.Value; return dflt; }
}
```

### 4.3 The projector — `BboxProjector`

```csharp
namespace CarlaNet.Labeling;

/// World→pixel projection for one EO camera frame. Inputs come straight off the SensorFrame the
/// recorder is already reading: Header.Transform is the camera world pose at capture,
/// ImageSensorData carries Width/Height/FovAngle. K and the camera basis are local to one call.
public static class BboxProjector
{
    private const double FtToM = 0.3048;

    /// Returns null if the actor projects entirely behind the camera or fully off-image.
    public static VehicleLabel? Project(
        VehicleLabelSource v,
        Transform camWorld, int imgW, int imgH, float fovDeg,
        DepthFrame? depth, double sizeFloorPx = 6.0)
    {
        // ---- 1) intrinsics: horizontal-FOV → focal in pixels, principal point at image centre.
        double f  = imgW / (2.0 * Math.Tan(DegToRad(fovDeg) * 0.5));
        double cx = imgW * 0.5, cy = imgH * 0.5;

        // ---- 2) the 8 world corners of the vehicle's OBB (CARLA-local frame).
        //  CARLA bbox is axis-aligned in the actor's local frame, centred on the actor origin,
        //  with half-extents (lx, ly, lz). Build the corners, rotate by world yaw, translate to
        //  world position. (Pitch/roll on traffic vehicles is negligible — extend if needed.)
        double lx = v.HalfLengthX, ly = v.HalfWidthY, lz = v.HalfHeightZ;
        double cYaw = Math.Cos(DegToRad(v.YawDeg)), sYaw = Math.Sin(DegToRad(v.YawDeg));
        Span<(double X, double Y, double Z)> corners = stackalloc (double, double, double)[8];
        int k = 0;
        for (int sx = -1; sx <= 1; sx += 2)
        for (int sy = -1; sy <= 1; sy += 2)
        for (int sz = -1; sz <= 1; sz += 2)
        {
            double xL = sx * lx, yL = sy * ly, zL = sz * lz;
            double xW = v.X + xL * cYaw - yL * sYaw;
            double yW = v.Y + xL * sYaw + yL * cYaw;
            double zW = v.Z + zL;
            corners[k++] = (xW, yW, zW);
        }

        // ---- 3) world → camera. CARLA Transform = world FROM camera-local; invert for the
        //  forward projection. (For pure nadir captures pitch≈-90° and the maths simplifies, but
        //  build it general — the recorder supports arbitrary EO poses.)
        var (R, t) = InvertTransform(camWorld);   // R rotates world points into camera-local
        // CARLA camera convention: +X forward, +Y right, +Z up (left-handed). Standard pinhole
        // expects +Z forward, +X right, +Y down. The CarlaCamToPinhole basis change is a fixed
        // axis remap — keep it in one place.

        double u_min = double.PositiveInfinity, u_max = double.NegativeInfinity;
        double v_min = double.PositiveInfinity, v_max = double.NegativeInfinity;
        Span<(double U, double V)> px = stackalloc (double, double)[8];
        int infront = 0;
        for (int i = 0; i < 8; i++)
        {
            var (xc, yc, zc) = WorldToCamPinhole(corners[i], R, t);
            if (zc <= 0.05) { px[i] = (double.NaN, double.NaN); continue; }
            double u = cx + f * xc / zc;
            double w = cy + f * yc / zc;
            px[i] = (u, w);
            infront++;
            if (u < u_min) u_min = u; if (u > u_max) u_max = u;
            if (w < v_min) v_min = w; if (w > v_max) v_max = w;
        }
        if (infront < 4) return null;
        if (u_max < 0 || u_min > imgW || v_max < 0 || v_min > imgH) return null;

        // ---- 4) modal OBB via min-area rect of the in-image, in-front pixels. (Andrew/Rotating
        //  Calipers on ≤8 points is O(1); inline it rather than pulling a CV library.)
        var (cxBox, cyBox, wBox, hBox, ang) = MinAreaRect(px);

        // ---- 5) visibility via the co-located depth camera. Sample on a small grid inside the
        //  modal box; count "near actor depth ±tolerance" as visible. Vehicle range = distance
        //  from camera to actor centre; tolerance scales with range (LOD/noise at altitude).
        var centerW = (v.X, v.Y, v.Z);
        var (_, _, zActor) = WorldToCamPinhole(centerW, R, t);
        double range = zActor;
        double tol = Math.Max(2.0, 0.02 * range);   // 2 m or 2% of range
        double vis = depth is null ? 1.0
                   : depth.VisibleFraction(cxBox, cyBox, wBox, hBox, ang, zActor, tol, samples: 9);

        // ---- 6) amodal AABB hull (still useful — see §5.4).
        double aW = Math.Max(0.0, u_max - u_min);
        double aH = Math.Max(0.0, v_max - v_min);
        double aCx = u_min + aW * 0.5, aCy = v_min + aH * 0.5;

        // ---- 7) size floor: drop labels smaller than the detector can learn.
        if (Math.Min(wBox, hBox) < sizeFloorPx) return null;

        string cls = v.SpecialType.Length > 0 ? $"{v.BaseType}-{v.SpecialType}" : v.BaseType;
        return new VehicleLabel(v.Id, cls,
            cxBox, cyBox, wBox, hBox, ang,
            aCx, aCy, aW, aH,
            vis, v.YawDeg, range);
    }

    // helpers: InvertTransform, WorldToCamPinhole, MinAreaRect, DegToRad — all small & local
}
```

### 4.4 The writer — `YoloObbWriter` (and friends)

```csharp
namespace CarlaNet.Labeling;

/// One .txt per frame, paired with the recorder's SCTMV_<UTC>.png/.xml by filename stem.
/// DOTA-style polygon format keeps every downstream tool (Ultralytics YOLO-OBB, mmrotate) happy:
///   class x1 y1 x2 y2 x3 y3 x4 y4 visible_fraction range_m yaw_world_deg
public static class YoloObbWriter
{
    public static void WriteToFile(string path, int imgW, int imgH,
        IReadOnlyList<VehicleLabel> labels)
    {
        using var sw = new StreamWriter(path);
        sw.Write($"# imagesize {imgW} {imgH}\n");
        foreach (var L in labels)
        {
            var (p1, p2, p3, p4) = ObbCorners(L.Cx, L.Cy, L.Wpx, L.Hpx, L.AngleRad);
            sw.Write($"{L.Class} {p1.X:0.0} {p1.Y:0.0} {p2.X:0.0} {p2.Y:0.0} " +
                     $"{p3.X:0.0} {p3.Y:0.0} {p4.X:0.0} {p4.Y:0.0} " +
                     $"{L.VisibleFraction:0.000} {L.RangeMetres:0.0} {L.YawWorldDeg:0.0}\n");
        }
    }
}

/// Optional: an axis-aligned YOLO sidecar for plain YOLO (no OBB) — same stem, .yolo.txt:
///   class cx_norm cy_norm w_norm h_norm
public static class YoloAabbWriter { /* ... */ }
```

### 4.5 Wiring into `FrameRecorder`

Two surgical changes to the existing `FrameRecorder` (full text at
[../../CarlaNet/src/CarlaNet.Recording/FrameRecorder.cs](../../CarlaNet/src/CarlaNet.Recording/FrameRecorder.cs)):

1. **Field add:** an optional `VehicleLabelService _labels` + an optional `byte[]? _depthToken` for
   the co-located depth camera's stream token. Constructor signature gains
   `byte[]? depthStreamToken = null, bool writeLabels = false`.
2. **In `OnFrame`:** after the existing telemetry `recs = _telemetry.Compute(_origin)`, also call
   `var src = _labels.Snapshot()`. Carry `(SensorFrame frame, src, latestDepth)` into the `Job`.
   In the worker, after the PNG + CoT writes, project each `VehicleLabelSource` with
   `BboxProjector.Project(...)` using `frame.Header.Transform`, `img.Width/Height/FovAngle`, and
   the latest depth frame, then `YoloObbWriter.WriteToFile(stem + ".obb.txt", ...)`.

The depth-camera plumbing exists as a model in `eo_observer.py` (§261–282): a co-located depth
camera spawned at the same pose with the same FOV, listener stores the latest frame. Port that
into a `DepthFrame` deserialiser sibling to `ImageSensorData` (CARLA depth is BGR-encoded
`R + G·256 + B·65536` over 24-bit fixed-point), keep "the latest frame" by reference, and the
labeler's visibility check is local.

### 4.6 What the on-disk artifacts look like

For one captured instant, the recorder now emits four files under `_dir`:

```
SCTMV_2026.06.30_14.07.22.000.png        # the EO RGB image (existing)
SCTMV_2026.06.30_14.07.22.000.xml        # CoT truth events (existing)
SCTMV_2026.06.30_14.07.22.000.obb.txt    # YOLO-OBB labels, DOTA polygon format (new)
SCTMV_2026.06.30_14.07.22.000.depth.png  # 24-bit depth, optional (new, off by default)
```

The PNG + .obb.txt pair drops directly into Ultralytics YOLO-OBB or mmrotate training. The .xml is
the same CoT events that already drive scoring. They share `actor_id` so every label round-trips
to its CoT event without ambiguity.

## 5. Specific design notes

### 5.1 Why piggyback on `FrameRecorder` instead of a separate stream subscription

Both options work, but piggybacking has one decisive property: **time-alignment is guaranteed by
construction**. The image, the CoT events, and the bbox labels all read from the same
`SensorFrame.Header.Timestamp` and the same `WorldObserver` snapshot. A parallel subscription
would re-read the actor cache at a slightly different instant and you'd get sub-tick drift — small,
but it shows up as a systematic label-vs-image skew of a couple of pixels at altitude. Avoid it.

### 5.2 The intrinsic gotcha — `FovAngle` is horizontal, square pixels assumed

`ImageSensorData.FovAngle` is the horizontal field-of-view, and CARLA's RGB camera uses square
pixels. So `fx = fy = W / (2·tan(fov/2))` and the principal point is the image centre. If a future
EO config introduces a different aspect ratio or a non-centred principal point (rare for the
simulated sensor, common for real EO), the K-matrix should be parameterised in one place — the
`BboxProjector` should accept an injected `CameraIntrinsics` struct rather than recomputing inline.

### 5.3 Axis convention — get the basis change wrong and everything is rotated 90°

CARLA's camera local axes are **+X forward, +Y right, +Z up** (left-handed). The pinhole maths
above assumes **+Z forward, +X right, +Y down** (the OpenCV convention). The fixed remap
`(x_pin, y_pin, z_pin) = (y_carla, -z_carla, x_carla)` belongs in *one* place inside
`BboxProjector.WorldToCamPinhole`. Verify empirically with one scripted scene: spawn one vehicle
due-north of the EO camera, project, and confirm the box lands above the principal point (because
"north" appears "up" in a nadir pygame frame). The "verify empirically" caveat is the same one the
CoT contract §3 already calls out for `track.course`.

### 5.4 Modal vs amodal — keep both, they cost almost nothing

The depth-trimmed (modal) box is what YOLO trains against — that's what the pixels actually show.
The hull of all 8 projected corners (amodal) is what a downstream amodal head, an occluder-aware
loss, or an OBB metric like rotated-IoU-with-occlusion can use. Storing both adds ~40 bytes per
label and gives you the option to train either way without re-running the labeler.

### 5.5 Range as a sample weight

`VehicleLabel.RangeMetres` makes a clean **per-sample loss weight** during training. At 18 kft the
camera-to-vehicle range varies 5.5–7+ km across the frame (corners vs centre); without weighting,
the detector implicitly over-fits to centre-of-frame examples. Weight ∝ 1/range² is the natural
choice if you're training a global detector; you can also bucket by range and balance.

### 5.6 What this finding intentionally does *not* include

- **Walkers / cyclists.** Same pattern, `VehicleLabelSource` becomes `ActorLabelSource`, and the
  blueprint filter widens from `vehicle.*` to also accept `walker.pedestrian.*`. v1 stays
  vehicles-only to match the v0 CoT contract (vehicles only).
- **Static infrastructure detection.** Buildings, trees, lampposts are Cesium-rendered, not CARLA
  actors — they're invisible to this pipeline (per §1). If we ever need them, the source is
  Cesium's own picking / depth and not a CARLA sensor; that's a separate finding.
- **Self-training mechanics.** The labels above are *fully truthed* (CARLA simulator ground truth)
  — there's no pseudo-labeling here. Self-training/semi-supervised on real EO frames is a separate
  workstream that *consumes* this dataset as the supervised seed.

## 6. Implementation order

1. **Add `DepthFrame` deserialiser** in `CarlaNet.Sensors` (24-bit fixed-point decode + a
   `VisibleFraction(...)` sampler). Unit-test against a known synthetic depth frame.
2. **Stand up `CarlaNet.Labeling`** with `VehicleLabelSource`, `VehicleLabelService.Snapshot()`,
   `BboxProjector.Project(...)`, `YoloObbWriter`. Unit-test `Project` with a known transform on
   one vehicle at known pose — assert the centre pixel within ±1 px.
3. **Extend `FrameRecorder`** with the optional depth-token + label-writer wiring (§4.5).
4. **Visual sanity script.** A small Python that opens one PNG + its `.obb.txt`, draws the OBBs,
   and saves a debug overlay. Run on 20 frames from Lakeview at 3 km nadir; eyeball.
5. **Quantitative sanity.** Cross-check: for each label, reconstruct its CoT event from the
   recorder's `.xml` by `actor_id`. Project the CoT (lat,lon,HAE) back through the camera and
   compare the centre pixel to the label's `(Cx, Cy)` — agreement to <1 px confirms the labeler
   and the recorder are consuming the same snapshot.

## 7. What this buys us downstream

- **A YOLO-OBB-ready dataset** generated entirely from the simulator with zero manual annotation,
  scaling with traffic density × capture duration × scenario count.
- **One scoring contract.** Truth and detection both round-trip the same CoT schema (§9 of the
  contract). The labeler doesn't change the scoring story; it just gives the detector something
  to learn from in the first place.
- **Heading supervision for free.** OBB angle + `YawWorldDeg` lets a detector learn `track.course`
  directly from pixels, instead of relying on multi-frame velocity differencing — useful at high
  altitude where positional noise is the dominant error source.
- **Range-aware training.** The same simulator that places the vehicle gives you the camera-to-
  vehicle range; you can weight, bucket, or curriculum-train on it.

## 8. Open questions to resolve before implementation

1. **Depth-camera default.** Always spawn (cost = a second camera stream) or opt-in via
   `FrameRecorder` flag? *Lean: opt-in, default off; required for `writeLabels=true`.*
2. **Modal box derivation when depth is unavailable.** Fall back to amodal? Or skip the label?
   *Lean: emit amodal with `VisibleFraction = -1` sentinel so training can either consume or
   exclude them by filter.*
3. **OBB convention.** DOTA polygon (4 points) vs YOLO-OBB (cx, cy, w, h, θ)? *Lean: write
   polygon; a one-line converter produces the YOLO-OBB form on demand.*
4. **Pitch/roll non-zero.** The §4.3 sketch assumes vehicle pitch/roll are negligible. For hilly
   maps (Lakeview/Carson) this is wrong at junctions and on grade changes. *Resolution: rotate
   the local corners by the full Rotation matrix, not just yaw — three lines.*

---

**Adjacency:** depends on the world-observer cache + `Geodesy` (already shipped), the recorder
plumbing (already shipped), and the existing CoT contract (09). Adds: depth deserialiser
(`CarlaNet.Sensors`), `CarlaNet.Labeling` project, optional flag on `FrameRecorder`. No engine
changes; no CARLA-fork changes; no Cesium changes.
