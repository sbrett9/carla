# Photoreal Occlusion Metric — per-vehicle % occlusion under unsegmented Cesium geometry

**Date:** 2026-07-10 · **Status:** the depth-based measurement and the arrival gate are BUILT
(2026-08-17, §12); pixel-exact differencing and the live sweep in §10 are still ahead.
**Datum:** ellipsoidal WGS84 (HAE), bare-earth referenced — `project_datum_decision`.
**Relates to:** [09_Telemetry_CoT_Contract.md](09_Telemetry_CoT_Contract.md) (the per-vehicle truth block this
extends), [16_Sensor_Pose_In_Recordings.md](16_Sensor_Pose_In_Recordings.md) (the camera pose/intrinsics an
occlusion test needs), [08_Layer_Architecture.md](08_Layer_Architecture.md) (the toggleable photoreal layer),
[01_Cesium_Integration.md](01_Cesium_Integration.md).

## 1. Purpose — the problem

The Cesium photoreal 3D tiles are **real meshes with vertical volume** — buildings, trees, terrain relief.
In hilly, densely-built scenes (SF Laurel Heights is the clearest case) they partially or fully **obstruct
vehicles** from an airborne electro-optical (EO) view, depending on the observing camera's angle. The
post-collect process that draws 2D bounding boxes over vehicles for YOLO-style training has no way to know a
vehicle is hidden behind a building or a tree, so it draws a box on a car the camera cannot actually see.
Training a detector on boxes over invisible objects teaches it to hallucinate.

We need a way, **for any vehicle and any observing camera, to quantify how much of that vehicle is obstructed**
(a continuous 0–1 fraction, and/or a coarse occlusion bucket), and to telemeter that value alongside the
existing per-vehicle truth so the bounding-box drawer can **gate** (drop fully-occluded boxes), **tag**
(annotate partials with an occlusion level), or **retighten** (draw the visible-region box instead of the full
projected box).

## 2. Why the standard CARLA occlusion filter does not work here

The mainstream CARLA dataset tools filter occluded boxes with **segmentation**. The two representative
approaches:

- **Semantic-segmentation pixel match** (Wang et al., *Improve bounding box in Carla Simulator*, arXiv
  2509.16773): compare the RGB frame's bounding-box region against a co-located semantic-segmentation camera
  and keep the box only if enough pixels carry the object's class colour (their thresholds: ≥10 % class-colour
  pixels for a normal box; ≥50 % for a box covering >70 % of the image). This is exactly the method that
  **breaks in our world**: it assumes *every occluder is segmented*. When a Cesium building sits in front of a
  car, the segmentation camera does not paint the building as "building" — the photoreal tiles are **not
  registered in the segmentation engine at all** (they are streamed by the Cesium plugin, outside CARLA's actor
  tagging). The car's expected class pixels simply are not there, and the filter has no principled way to know
  whether they are missing because the car is occluded, mis-projected, or off-frame.
- **Depth-camera distance comparison** (MukhlasAdib, *CARLA-2DBBox*): compare each vehicle's true
  camera-to-object distance against a depth-camera measurement; a vehicle is occluded where
  `real_distance − depth_measurement > depth_margin`. Parameters: `depth_margin` (absorbs the vehicle's own
  size), `patch_ratio` (default 0.5 — fraction of in-box pixels that must satisfy the margin), and
  `resize_ratio` (shrink the sampled box, e.g. 0.5, to avoid background pixels). **This one does transfer**,
  because it relies on depth, not class labels.

The crux: **our OSM-built CARLA content (roads, props, spawned vehicles) *is* segmented** — the port ships
[InstanceSegmentationCamera](../../../Unreal/CarlaUnreal/Plugins/Carla/Source/Carla/Sensor/InstanceSegmentationCamera.cpp)
and [SemanticSegmentationCamera](../../../Unreal/CarlaUnreal/Plugins/Carla/Source/Carla/Sensor/SemanticSegmentationCamera.cpp).
The **only** blind spot is the photoreal tileset. So the design question is narrow: *how do we recover an
occlusion signal for occluders that carry no segmentation identity?*

## 3. Key insight — depth is segmentation-agnostic

Cesium photoreal meshes are ordinary opaque geometry: they **write to the depth buffer** and win the depth
test whenever they are nearer the camera than a vehicle. The occlusion information is therefore fully present in
a **depth capture**, regardless of whether the tiles carry a class or instance label. We do not need Cesium in
the segmentation engine to measure occlusion — we need the depth buffer, which we already have.

This lets us adopt the standard amodal/modal definition used across occlusion datasets (KINS, COCOA, KITTI-360,
OccludedVehicles):

- **Amodal silhouette `A`** = the pixels the vehicle *would* occupy if nothing were in front of it.
- **Modal (visible) silhouette `V`** = the pixels where the vehicle is actually the front-most surface in the
  full scene.
- **Occlusion fraction = 1 − |V| / |A|** (the "percentage of occluded instance" / ratio-of-occluded-region
  metrics from the amodal-segmentation literature).

## 4. What this project already has (the building blocks)

Almost every primitive an occlusion test needs is already implemented:

- **A co-located depth camera.** Both [eo_observer.py](../../../CarlaNet/python/eo_observer.py) and
  [SCTMV.py](../../../CarlaNet/python/SCTMV.py) spawn a `sensor.camera.depth` at the same pose/FOV as the RGB
  camera and keep it slaved to it every frame. The depth decode (`R + G·256 + B·65536`, normalised, ×1000 m
  far plane) and the pixel→world reconstruction basis (`fwd/right/up`, `f = width/(2·tan(fov/2))`) are already
  written and validated by the Ctrl+LMB world-picker.
- **Per-vehicle 3D bounding boxes.** `vehicle.bounding_box` (extent + transform) is already read in the spawn
  path ([SCTMV.py](../../../CarlaNet/python/SCTMV.py) `_spawn_one`), so the 8 oriented corners are a cheap
  transform away.
- **A toggleable photoreal layer.** `world.set_layer_visible("photoreal", on/off)` already exists and is bound
  to the `C` key in [eo_observer.py](../../../CarlaNet/python/eo_observer.py); ground and road have the same
  controls. This is the enabler for the render-differencing method (§5.2).
- **Instance/semantic segmentation cameras** (§2) for the pixel-exact path.
- **Frame-coherent native recording.** [FrameRecorder](../../../CarlaNet/src/CarlaNet.Recording/FrameRecorder.cs)
  + [VehicleTelemetryService](../../../CarlaNet/src/CarlaNet.Recording/VehicleTelemetryService.cs) already pair
  each captured frame with per-vehicle truth and the sensor pose (16) in synchronous mode — the natural place
  to compute a frame-coherent occlusion value.

## 5. Methods (ranked, with trade-offs)

### 5.1 Depth-margin point-sampling test — recommended first increment

Pure post-process; no engine change. Per camera frame, per vehicle:

1. Build the vehicle's oriented bounding box (OBB) from `bounding_box` + actor transform.
2. Sample points on the box — at minimum the 8 corners; better, a **grid over the three camera-facing faces**
   (or a coarse surface point cloud) for a smoother fraction.
3. Project each point to pixel `(u, v)` and compute its true camera-space depth `Z` using the existing
   `_project_pt` basis.
4. Read the depth map at `(u, v)`; the point is **occluded** when `depth_map(u,v) < Z − margin`.
5. `occlusion ≈ occluded_points / sampled_points`.

This generalises the two community recipes: it is the CARLA-2DBBox `depth_margin` / `patch_ratio` /
`resize_ratio` idea (use the OBB rather than an axis-aligned box so `resize_ratio` is rarely needed), and it
subsumes the vertex-count occlusion levels used by arXiv 2509.16773 (they bucket by visible vertices — 0/1/2
for ≥6 / 4–5 / <4 of 8 visible; a surface grid just gives a continuous version of the same thing).

- **Pros:** reuses the depth camera + projection math already in the codebase; runs in the recorder *or* fully
  offline over a recorded depth product; no C++ and no extra render passes; works for Cesium, trees, and
  vehicle-on-vehicle occlusion uniformly.
- **Cons / cautions:**
  - **Self-occlusion.** Sampling the whole OBB counts the vehicle's own back faces as "occluded." Sample only
    camera-facing faces, or compare against a vehicle-only expected-depth range, so self-occlusion is not
    conflated with external occlusion.
  - **Far-plane clamp.** The depth sensor's far plane is 1000 m (this port's decode); fine for EO altitudes
    here but a fixed ceiling to keep in mind.
  - **Granularity.** 8 corners is coarse; density is a tunable cost/accuracy knob.
  - **Box vs silhouette.** An OBB over-covers a car's true silhouette; a point grid biased to the hull reduces
    the over-count.

### 5.2 Amodal/modal differencing via the photoreal toggle + instance segmentation — the pixel-exact upgrade

Two captures at the **same pose**, same frame:

1. **Instance seg, photoreal ON** → count each vehicle's visible instance pixels `V_i`. A Cesium building in
   front simply leaves those pixels unlabelled — which is *exactly* the modal mask. (The very fact that put us
   off segmentation — tiles carry no label — is harmless here: unlabelled-because-in-front = correctly-not-visible.)
2. **Instance seg, photoreal HIDDEN** (`set_layer_visible("photoreal", False)`) → the vehicle silhouette against
   only CARLA-native geometry, giving `A_i`. (Vehicles still occlude each other in this pass — that is genuine
   occlusion you likely want counted; for a *pure* per-vehicle amodal you would additionally isolate each
   vehicle or project its OBB depth.)
3. `occlusion_i = 1 − |V_i| / |A_i|`, pixel-accurate. Differencing the two passes also **attributes** occlusion
   to photoreal vs vehicle-vs-vehicle.

- **Pros:** exact and continuous; yields the actual **visible mask**, so the drawer can emit a true modal
  (visible-region) box, not just a scalar; directly comparable to the amodal-dataset metrics.
- **Cons:** one or two extra render passes per recorded frame; toggling a streaming tileset per frame has cost
  and must stay frame-coherent under synchronous ticking; hiding photoreal changes tile-streaming state, so the
  toggle wants to be a cheap visibility flip, not a reload.

### 5.3 Ray-cast visibility — cross-check only

Line-trace from the camera to each vehicle sample point; occluded if the first hit is not the target actor.
This needs the **occluders to have collision meshes**. Our ground tileset is collidable, but photoreal
*building/tree* collision is not guaranteed (`project_layer_architecture`, `project_telemetry_dtm_decoupling`).
Depth is the *visual* truth and does not depend on collision geometry; treat ray-casting as an optional
sanity cross-check, not the primary metric.

## 6. Where the value plugs in

- **Schema (09).** Add an occlusion field to the per-vehicle `_carla` truth block, e.g.
  `occlusion="0.42"` (continuous, camera-relative) plus an optional `occlusion_level="2"` bucket, and — if the
  differencing method is used — `occlusion_src` distinguishing photoreal- vs vehicle-caused. Because occlusion
  is **camera-relative**, it is a property of the (vehicle, sensor) pair, so it belongs with the frame that the
  sensor pose (16) already stamps — not a global vehicle attribute.
- **Producer.** Compute it in the native recorder
  ([VehicleTelemetryService](../../../CarlaNet/src/CarlaNet.Recording/VehicleTelemetryService.cs) /
  [FrameRecorder](../../../CarlaNet/src/CarlaNet.Recording/FrameRecorder.cs)), where the depth frame, vehicle
  poses, and sensor transform are already frame-coherent in synchronous mode — so the number is
  self-describing and travels in the CoT sidecar. (A purely offline variant is possible if a per-frame depth
  product is recorded alongside the RGB, but recorder-side keeps it coherent and avoids a second depth export.)
  Occlusion rides the per-vehicle telemetry record, so it is only ever computed for **telemetered (established)
  vehicles** — see the telemetry gate in §9.
- **Consumer.** The post-collect bounding-box drawer reads the occlusion field and applies the label policy
  (§7): drop, tag, or retighten.

## 7. Thresholds and label policy

Continuous fraction is the primitive; buckets are for filtering and stats. Prior art for the buckets:

| Dataset | Occlusion buckets |
|---|---|
| KITTI | fully visible / partly occluded / largely occluded / unknown |
| KINS (KITTI amodal) | 0 % · 1–30 % · 30–60 % · 60–90 % |
| COCOA-cls | 0 % · 1–20 % · 20–40 % · 40–70 % |
| OccludedVehicles (synthetic) | 20–40 / 40–60 / 60–80 % foreground bands |

Recommended policy for the YOLO prototype:

- **Emit both** the continuous fraction and a small bucket set.
- **Drop** boxes above a heavy-occlusion cutoff (≈70–80 %) so the detector is never trained on effectively
  invisible cars.
- **Keep and tag** partials — occluded examples improve robustness; the bucket lets a training run filter later.
- **Choose modal vs amodal box** per training goal: a tight box on the *visible* region (modal, the usual YOLO
  target) is available directly from §5.2; the full projected box (amodal) from the 3D bbox projection. Report
  dataset-level occlusion stats (percentage-of-occluded-instances, mean ratio-of-occluded-region) for
  provenance.

## 8. Recommended phasing

*Step 1 below is built, together with the arrival gate §9 requires ahead of it; see §12 for what
shipped and where it departed from this plan. Steps 2 and 3 are still ahead.*

1. **Phase 1 — depth-margin point grid in the recorder.** Continuous occlusion fraction + bucket into the
   `_carla` block; gate/tag the drawer. No engine change; reuses the existing depth camera and projection math.
2. **Phase 2 — amodal/modal instance-seg differencing.** Pixel-exact ground truth and true modal masks; use it
   to validate Phase 1's cheaper scalar and to enable visible-region boxes. Gated on making the photoreal
   toggle a cheap per-frame visibility flip under sync recording.
3. **Phase 3 (optional) — per-pixel visible-mask export** for amodal training or segmentation labels, once a
   Cesium segmentation strategy exists (out of scope now).

## 9. Decisions and open questions

### Resolved (2026-07-10)

- **Telemetry is gated to established vehicles.** Truth telemetry — and therefore any occlusion value — is
  emitted **only for vehicles that have entered the interior** (SCTMV's latched `entered` flag: the vehicle has
  crossed the inward staging-ring margin, `SCTMV.py` `_spawn_one` / reconcile). Vehicles still fading in from
  the staging ring emit **nothing**. *This is a defect to fix, not current behaviour:*
  [VehicleTelemetryService.Compute](../../../CarlaNet/src/CarlaNet.Recording/VehicleTelemetryService.cs) today
  emits a record for **every** `vehicle.*` actor with no fade/interior gate. Implementation: the recorder must
  learn each vehicle's `entered` state — plumbed from SCTMV (the client authority for it) or re-derived from
  the staging bounds + vehicle position server-side; resolve at build time. **Built** — the defect is
  fixed and the plumbing turned out to be neither of those options; see §12.2.
- **Translucency counts as partial occlusion, linear in opacity.** An occluder at opacity α contributes **α**
  to the target's occlusion (α=0 → none, α=1 → full); fully-opaque occluders (buildings, trees, *established*
  vehicles) always count fully. In practice a not-yet-entered faded vehicle occluding an established target is
  expected to be rare and mostly avoided by camera framing — the linear rule is the completeness safeguard for
  when it is not. It requires **per-vehicle opacity in the recorder**: surface the last `set_actor_fade` value
  (α = 1 − `hide`) via the world-observer snapshot, the same way vehicle light state is surfaced. The *only*
  translucent occluders are mid-fade vehicles, so the vehicle-vs-not split needed to apply the weight is exactly
  the instance-segmentation signal (§5.2) — **not** the building/tree attribution deferred below. The
  depth-margin method (§5.1) cannot weight by occluder opacity without attribution: it either leans on the
  dithered dissolve stochastically blocking ≈α of samples (fragile under synchronous ticking, where the
  dither/TAA is unreliable) or over-counts a faded occluder as fully blocking. Exact linear weighting is
  therefore delivered by the instance-seg method (§5.2); the depth method documents this as an approximation.
- **Occluder attribution is out of scope.** The prototype trains a single **vehicle-identification** class, so
  *what* occludes (building vs tree vs vehicle) is not recorded — only *how much*. Attribution would be
  revisited with segmentation in a later step (e.g. if occluder class ever matters). The one coarse split that
  is free and used is vehicle-vs-not (instance seg), solely to apply the opacity weight above.

### Open (implementation tuning, resolve empirically)

- ~~**Self-occlusion handling**~~ — settled by the build: sampling in image space against the box's ray
  entry point makes self-occlusion structurally impossible (§12.1).
- **Sample density and `margin`** vs the 1000 m depth far-plane and pixel quantisation. Defaulted at 24
  samples across and 1 m; still untuned against real captures.
- **Per-frame photoreal-toggle cost** under synchronous recording (§5.2) — measure before adopting the two-pass
  method.
- ~~**Exact plumbing** of `entered` and opacity into the recorder~~ — settled by the build: neither client
  push nor a server snapshot, because the client that issues the fade already holds it (§12.2).
- **Physical vs bare-earth altitude** for exact projection — the doc-16 §5 caveat applies (pixel-geometry
  reconstruction is done in physical space).

## 10. Verification (the payoff)

Build a controlled scene in SF Laurel Heights, park a vehicle behind a building, and sweep the EO camera
through angles from clear line-of-sight to fully blocked. Confirm the reported occlusion fraction moves 0→1
monotonically, that the bounding-box drawer drops the box past the cutoff and tags it in the partial band, and
that the depth-margin scalar (§5.1) and the instance-seg differencing (§5.2) agree within tolerance on a
sampled set. With occlusion in the CoT truth, each still becomes self-describing for the planned
truth-vs-detection scoring harness: a "missed" detection can be adjudicated as a true miss vs a legitimately
occluded target.

## 11. Sources

- MukhlasAdib, *CARLA 2D Bounding Box Annotation Module* — depth-camera occlusion filter (`depth_margin`,
  `patch_ratio`, `resize_ratio`): https://mukhlasadib.github.io/CARLA-2DBBox/ ·
  https://github.com/MukhlasAdib/CARLA-2DBBox
- Wang et al., *Improve bounding box in Carla Simulator* — semantic-segmentation pixel-match filter (the
  approach that fails on unsegmented occluders): https://arxiv.org/html/2509.16773v1
- Amodal/occlusion-level definitions (amodal vs modal mask, percentage-of-occluded-instance / ratio-of-occluded
  -region): KINS — *Amodal Instance Segmentation With KINS Dataset*; *BLADE* (arXiv 2401.01642); *Amodal
  Cityscapes* (arXiv 2206.00527); *Robust Instance Segmentation through Reasoning about Multi-Object Occlusion*
  (arXiv 2012.02107).
- CARLA dataset-generation tooling with occlusion handling: *CarFree* and *CADET* (Carla Automated Dataset
  Extraction Tool).

## 12. What was built (2026-08-17)

The depth-based measurement (§5.1) and the arrival gate (§9) are implemented and unit-tested; the
pixel-exact differencing (§5.2) is not. Where the build resolved something this document left open,
it is recorded here.

### 12.1 The measurement

[OcclusionEstimator](../../../CarlaNet/src/CarlaNet.Recording/OcclusionEstimator.cs) subscribes to a
depth camera held at the recorded camera's pose, pairs each depth capture with the recorded frame by
simulation frame number (falling back to simulation time, and refusing the pair outright if the two
cameras have drifted apart in position or boresight — silently mismeasuring is worse than reporting
nothing), and measures each vehicle against it.

**The sampling is in image space, not on the box surface.** §5.1 proposed sampling points on the
camera-facing faces of the oriented bounding box; the built version instead samples a pixel grid over
the box's projected footprint and intersects each pixel's camera ray with the box. This is the same
depth-margin test, re-parameterised, and it is better on three counts:

- **Self-occlusion becomes structurally impossible.** The comparison is against the ray's *entry*
  point into the box, and everything belonging to the vehicle lies between entry and exit, so the
  vehicle's own bodywork can never read as an occluder. This removes the first open question in §9
  rather than tuning around it.
- **It measures the silhouette, not the box.** Rays that miss the box are not sampled, so the
  denominator is the vehicle's actual projected outline rather than its bounding rectangle — the
  "box vs silhouette" over-count in §5.1 largely goes away.
- **The fraction is an area fraction**, uniform in pixels, which is what the amodal literature's
  ratio-of-occluded-region actually means, so it is directly comparable to the §5.2 upgrade when that
  arrives.

Sampling density is a step over that footprint chosen to give a set number of samples across the
longer side (default 24), so cost per vehicle is bounded however large it appears. Levels use the
KINS bands plus a fifth for the effectively-invisible remainder (§7), and both the fraction and the
band ride in the `_carla` block — see [09](09_Telemetry_CoT_Contract.md) §5.1.

Occlusion is measured for **telemetered vehicles only**, and only on recorded captures: it is a
property of the (vehicle, camera) pair, and the live UDP feed has no camera.

**Cost.** The measurement takes its own subscription to the depth camera's stream, so recording with
occlusion doubles that camera's stream traffic. It is off unless a depth camera is handed to the
recorder, and `--no-occlusion` turns it off for a run that does not want it.

**Translucency remains the documented approximation** (§9). A mid-fade vehicle occluding another is
counted as whatever the depth capture shows of it, which depends on how far the dithered dissolve has
resolved. Exact linear weighting needs the occluder's identity, which depth alone does not carry;
that is §5.2's to deliver. Per-vehicle opacity is now available to the recorder (below) ready for it.

### 12.2 The arrival gate and per-vehicle opacity

§9 left the plumbing of `entered` and opacity open between "client push" and "server-side snapshot or
derivation". **Neither: the client already holds the value.** `set_actor_fade` writes straight to
render state and the server keeps no readable copy, so the client that issued the fade is its only
holder. [CarlaClient](../../../CarlaNet/src/CarlaNet.Transport/CarlaClient.cs) now records each
`set_actor_fade` it sends and latches the moment a vehicle first reaches full opacity, exposing
`GetActorOpacity` and `IsActorEstablished`; the record is dropped when the actor leaves the
world-observer snapshot, so a recycled actor id never inherits the previous vehicle's arrival state.

This is exact, costs nothing, and needs no engine change — where widening the world-observer packet
would have added four bytes per actor per tick, forever, to carry a value this very process had just
sent. Its one assumption is that a single client owns the fades, which is what SCTMV is; a second
client fading the same vehicles would not be seen.

[VehicleTelemetryService](../../../CarlaNet/src/CarlaNet.Recording/VehicleTelemetryService.cs) then
skips vehicles that have never been established, closing the defect §9 recorded (a record for every
`vehicle.*` actor regardless of fade). The gate is inert without staging traffic, since a vehicle
nobody fades is established from the start. The same gate is applied on the shim's own telemetry path
so both agree.

### 12.3 Still open

- The **live verification in §10** — park a vehicle behind a building in SF Laurel Heights and sweep
  the camera from clear to blocked — has not been run. The unit tests assert the same monotonic
  0 → 1 behaviour against synthetic depth captures, which checks the arithmetic, not the world.
- **`margin` and sample density** are defaulted (1 m, 24 across), not tuned against real captures.
- **§5.2 differencing**, and with it exact opacity weighting and true visible-region boxes.
- Whether to also carry per-vehicle **opacity** into the sidecar. It is a "do not train on this"
  signal of the same kind as occlusion, and the recorder now has it, but it is not part of the
  contract yet.

