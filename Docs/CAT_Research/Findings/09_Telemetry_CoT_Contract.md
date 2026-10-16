# Telemetry CoT Contract (v0) — the shared truth/detection schema

**Date:** 2026-06-12 · **Status:** v0 AGREED 2026-06-12 (decisions in §8); truth producer implementing.
**Decided:** Option **B** (pull helper + CoT formatter + reference WinTAK emitter) — see
[../Discussions/2026-06-12_Telemetry_Serving_Options.md](../Discussions/2026-06-12_Telemetry_Serving_Options.md).
**Datum:** ellipsoidal WGS84 (HAE) — `project_datum_decision`.

> **Revision (2026-10-01):** During a SUMO drive the live pull (`get_vehicle_telemetry`), and so the
> live CoT feed and a recorder in any process, lists only the bodies each frame drew, each named by
> its SUMO vehicle, and no parked body — from the render set the server now carries on every
> world-observer snapshot (§5.2). A SUMO-driven body's `role_name` is `sumo` (§5).

> **Revision (2026-10-01):** A SUMO-driven body is seated on the ground where its road is at grade and
> on the road's profile where the road is a structure, a bridge deck included, so on a deck its
> `point.hae` is the deck's altitude in the bare-earth datum and the sidecar's `hae_dtm` the ground
> beneath it (§3); at grade it is the ground's, as before. Nothing in the computation changed.

> **Revision (2026-10-02):** Every truth record carries `heading_deg` in `_carla`, the direction the
> body points, beside `track.course`, the direction it moves (§3, §5); the live pull's dicts carry it as
> `heading_deg`. During a SUMO drive the body's heading is the heading of its own path and its velocity
> the path's, so course and speed are the motion the imagery shows; a recorder beside the session also
> writes `sumo_angle_deg`, SUMO's reported angle, for audit (§5).

> **Revision (2026-10-02):** A SUMO drive offers two optional performance controls, both off by
> default. Under a draw distance every vehicle is still reported, and a recorded sidecar states the
> distance and marks each vehicle its camera did not draw with `beyond_draw_distance` and
> `camera_range_m` (§5.3); the live pull and the live feed, which have no camera, are unchanged. Under
> a limit on which vehicles get a body, a vehicle outside it has no body and is in no truth record,
> live or recorded, exactly as any vehicle the session did not draw (§5.2). With neither, nothing
> changes.

> **Revision (2026-10-05):** A vehicle's `base_type` and `special_type` come from the measured vehicle
> catalogue (`CarlaControl/catalogue/vehicles.catalogue.json`) wherever its blueprint is one a
> catalogue class draws: the class's `cot_base_type` and `cot_special_type`, an empty kind included,
> whatever the blueprint declares, and the callsign `<base_type>-<id>` with them (§4). This holds in
> the recorded sidecar and the live pull of the process running a SUMO drive, which hands the client it
> drives through its catalogue's tables, and in the SUMO-side producers given the catalogue. Any other
> blueprint, and every vehicle a client no drive handed a catalogue reports, keeps what its blueprint
> declares. Owner's rulings of 2026-10-02 (`special_type`) and 2026-10-05 (`base_type`), recorded as
> [06 D6.18](../Plans/SUMO_Behavioral_Capture/06_Truth_And_Annotation.md).

> **Revision (2026-10-05):** Every vehicle record of a recorded sidecar says where the vehicle's box
> fell against that capture's picture, `in_frame`, and carries its apparent size wherever the box has a
> footprint, from the box's projection alone; where the five occlusion fields are absent,
> `occlusion_unmeasured` says why in one word (§5.1). The owner ruled that an absent fraction was being
> read as "not hidden", and that a truth field carries only what was declared, what happened, or a
> measurement from the frame's geometry by a fixed published method with no pass mark. The live pull
> and the live feed, which have no camera, are unchanged.

> **Revision (2026-10-06):** A vehicle record of a recorded sidecar whose `in_frame` is `wholly` or
> `partly` carries the vehicle's box, as the owner ruled: `box_px`, `box_oriented_px`, `truncation`,
> `camera_range_m`, `pitch_deg` and `roll_deg` in `_carla`, and a `<_box3d frame="geodetic">` beside it
> holding the box's eight corners as explicit corners, converted as the record's own point is (§5.1). A
> vehicle outside the picture or behind the lens carries none of them. The box is on the vehicle's
> record in the sidecar; there is no separate label file per image. The live pull and the live feed,
> which have no camera, are unchanged.

> **Revision (2026-10-06):** A vehicle record of a recorded sidecar in the picture also carries
> `lights`, the lights commanded on for the vehicle in words, and, where it names its SUMO vehicle,
> `pose_source` -- `simulated`, `interpolated` or `held` -- as the owner ruled (§5.1). Both are read from
> the world-observer snapshot of the capture's own frame, so a recorder in any process writes the same;
> a sidecar whose snapshot did not carry one says `lights="unknown"` or `pose_source="unknown"` on
> `<events>` and writes it on no record. A vehicle outside the picture carries neither; the live pull
> and the live feed are unchanged.

> **Revision (2026-10-06):** `pose_source` is written in the owner's four words -- `sumo`,
> `interpolated`, `jump` and `stale` -- in place of `simulated`, `interpolated` and `held` (§5.1). A body
> shown at SUMO's later position across a step too far to drive in one, written `simulated` until now, is
> `jump`. A capture from a SUMO drive against a server built before the jump state writes such a body
> `sumo`, and the run's report and closeout say how many.

## 1. Purpose

One CoT event schema emitted by **both** producers so they are directly comparable in WinTAK and in a
scoring harness:
- **TRUTH** — pulled from CARLA (exact): vehicle actor → `Geodesy.CarlaLocalToGeodetic` → CoT.
- **DETECTION** (future YOLO) — pixels → 2D bbox → depth pixel→world (the `eo_observer` Ctrl+LMB math) →
  `Geodesy` → CoT.

Same shape ⇒ truth-vs-detection scoring is a direct diff (position error, class confusion, miss/false).

## 2. Event schema (annotated)

```xml
<event version="2.0"
       uid="CARLA-TRUTH-<actor_id>"          <!-- stable per track; DETECTION uses CARLA-DET-<track_id> -->
       type="a-n-G-E-V"                       <!-- 2525 CoT type; see §4 -->
       how="m-g"                              <!-- machine / GPS-derived -->
       time="2026-06-12T18:00:00.000Z"        <!-- generated at (UTC, ms) -->
       start="2026-06-12T18:00:00.000Z"       <!-- valid from -->
       stale="2026-06-12T18:00:03.000Z">      <!-- ages off after STALE_SECONDS (default 3 s) -->
  <point lat="37.7841234" lon="-122.4567890"
         hae="61.2"                            <!-- ELLIPSOIDAL height m (our datum) -->
         ce="0.0" le="0.0"/>                   <!-- circular/linear error m; TRUTH = 0 (exact) -->
  <detail>
    <track course="182.4" speed="11.3"/>       <!-- deg true (0-360), m/s -->
    <contact callsign="car-123"/>
    <_carla source="truth" actor_id="123"
            type_id="vehicle.audi.tt" base_type="car" special_type=""
            length_m="4.5" width_m="2.0" height_m="1.4"
            color="0,0,0" role_name="autopilot"
            vx="11.2" vy="-1.4" vz="0.0"
            occlusion="0.420" occlusion_level="2" occlusion_samples="96"
            apparent_width_px="48" apparent_height_px="21"/> <!-- truth extras; WinTAK ignores unknown detail -->
  </detail>
</event>
```

## 3. Field conventions

| Field | Convention |
|---|---|
| `version` | CoT `2.0` |
| `uid` | stable per (source, track). TRUTH: `CARLA-TRUTH-<actor_id>`. DETECTION: `CARLA-DET-<track_id>`. (Scoring associates truth↔detection by position/time, **not** uid.) In a SUMO drive, where a pooled actor renders a succession of SUMO vehicles, the track is the SUMO vehicle: TRUTH `CARLA-TRUTH-SUMO-<sumo_id>` (§5.2), in the recorded sidecar and on the live feed alike. |
| `type` | 2525 CoT atom type — §4 |
| `how` | TRUTH `m-g` (machine/GPS). DETECTION `m-f` (machine/fused) so the provenance differs. |
| `time`/`start` | generation instant, ISO-8601 UTC ("Zulu"), millisecond precision |
| `stale` | `time + STALE_SECONDS` (default **3 s** = 15 missed updates at 5 Hz) |
| `point.lat`/`lon` | WGS84 degrees |
| `point.hae` | **ellipsoidal** height, metres (matches datum; = ground sample + local Z). On a bridge deck it is the deck's altitude, not the ground's: the body is seated on the deck's road profile there, and the surface shift taken off under a deck is the same systematic one the deck's height was measured against, so a deck vehicle's `hae` stands the deck's height above the bare-earth `hae_dtm` beneath it — 3–7 m on Arapahoe — and is not a vehicle in the air |
| `point.ce`/`le` | error metres. **TRUTH = 0.0** (exact). DETECTION = estimated. (CoT "unknown" sentinel 9999999 is NOT used.) |
| `track.course` | **degrees true north, 0–360**. Course-over-ground from velocity: `bearing = atan2(East, North) = atan2(vx, -vy)` (CARLA +X=East, −Y=North); fall back to vehicle yaw below a speed threshold. *Verify empirically (drive north ⇒ ~0°), as with the pick math.* The direction the vehicle moves; the direction its body points is `_carla.heading_deg` (§5), and the two differ through a turn and a lane change |
| `track.speed` | horizontal ground speed `sqrt(vx²+vy²)`, **m/s** |
| `contact.callsign` | human-readable; default `<base_type>-<actor_id>` (e.g. `car-123`); `<base_type>-<sumo_id>` in a SUMO drive (e.g. `car-escort_0`) |

## 4. 2525 / CoT `type` mapping

CoT atom type format: `a-{affiliation}-G-E-V[-subtype]` — atom / standard-identity / **G**round /
**E**quipment / **V**ehicle.

- **Affiliation** `{affiliation}`: `f` friend · `h` hostile · `n` neutral · `u` unknown · `p` pending · … .
  Default below is an **open decision (§8)**; per-vehicle override (from `role_name` or a scenario map)
  is supported regardless of the default.
- **v0 (recommended): one symbol for every vehicle — `a-{aff}-G-E-V`** ("ground equipment vehicle"). The
  fine classification still rides along in `_carla` (`base_type`, `type_id`), so nothing is lost and we
  avoid shipping imperfect subtype SIDCs.

**v1 (later) per-class subtypes** — to refine against an authoritative 2525 table before use:

| CARLA `base_type` / `special_type` | candidate CoT type | note |
|---|---|---|
| `car` | `a-{aff}-G-E-V-C` | civilian vehicle |
| `truck` | `a-{aff}-G-E-V-U-T` | utility / truck (verify SIDC) |
| `van` | `a-{aff}-G-E-V-C` | treat as civilian vehicle |
| `motorcycle` / `bicycle` (2 wheels) | `a-{aff}-G-E-V-m` | motorcycle (verify SIDC) |
| `bus` | `a-{aff}-G-E-V-U-B` | bus (verify SIDC) |
| `special_type=emergency` (fire/ambulance/police) | emergency-management symbol | distinct hierarchy; refine |

Vehicle class source: the measured vehicle catalogue's class for the vehicle's blueprint —
`cot_base_type` (car/truck/van/bus/motorcycle/bicycle) and `cot_special_type` (emergency/taxi/electric,
empty for none) — wherever the producer holds the catalogue and a class draws the blueprint
(2026-10-05; the content build's own attributes are hand-edited, and when first swept `base_type` was
wrong or absent on 7 of 17 blueprints and `special_type` empty on all). Otherwise the CARLA blueprint
attributes `base_type` and `special_type` when present; else infer from `number_of_wheels` / `type_id`.
A SUMO-side producer with no blueprint in the catalogue takes the base type from the SUMO vehicle class.

## 5. The `_carla` truth-extras block (custom `<detail>` child)

Carries the richer-than-ADS-B fields for the scoring harness / any TAK plugin; WinTAK ignores unknown
detail children. Attributes: `source` (`truth`|`detection`), `actor_id`, `type_id`, `base_type`,
`special_type`, `length_m`/`width_m`/`height_m` (from `bounding_box.extent × 2`), `color`, `role_name`,
raw `vx`/`vy`/`vz`, and `heading_deg` -- the direction the body points, degrees true north from its yaw
(`atan2(cos yaw, −sin yaw)`), beside `track.course`, the direction it moves (2026-10-02). (Detection
fills what it can: `source="detection"`, confidence, predicted class.) During a SUMO drive the body's
heading is the heading of its own path, the rear axle trailing the front bumper, and its velocity the
path's, lateral movement included; a recorder handed the session's render set also writes
`sumo_angle_deg`, the angle SUMO reported for the vehicle at the frame, for audit. The live pull and a
recorder in another process carry `heading_deg` and no SUMO angle, which the server is not told.
In a SUMO drive a recorded sidecar adds, per vehicle, `sumo_id` (the SUMO vehicle the body rendered
on that frame), `vtype_id` (its declared vType) and `admitted_tick` (the first frame of its current
rendered span); `actor_id` is then the body that drew it on that frame (§5.2). The live pull's
records carry the same three, as `sumo_id`, `vtype_id` and `admitted_tick` keys, and the live CoT
emitters (`CotUdpEmitter`, `cot_telemetry.py`) write them into `_carla` (2026-10-01). A SUMO-driven
body's `role_name` is `sumo`, the authority that drives it
([04 D4.9](../Plans/SUMO_Behavioral_Capture/04_Contracts.md)); traffic-manager traffic reads
`autopilot`.

### 5.1 In the picture, apparent size, box and occlusion (recorded captures only)

| Attribute | Written | Meaning |
|---|---|---|
| `in_frame` | on every vehicle record a recorder writes | Where the vehicle's bounding box fell against **this capture's picture**, from its eight corners projected through the camera's pinhole (the capture's camera pose, the picture's width and height, and the horizontal field of view the recorder was started with): `wholly` -- every corner inside the picture; `partly` -- the corners' rectangle crosses an edge; `none` -- the rectangle lies wholly outside; `behind_camera` -- a corner is at or behind the lens, so the box has no projection. The only lines it is read against are the picture's edges and the camera's near plane (0.1 m). |
| `apparent_width_px`, `apparent_height_px` | wherever the box has a footprint: every place but `behind_camera` | How large the vehicle appears in the picture -- its full projected footprint, including any part outside the picture. From the projection alone; needs no depth capture. |
| `box_px` | on a vehicle whose `in_frame` is `wholly` or `partly`, and on no other | The axis-aligned rectangle the box's eight projected corners span, in pixels: `x_min y_min x_max y_max`, x across from the picture's left edge and y down from its top edge, to two places. Not clipped to the picture, so a `partly` vehicle's runs past the edge. |
| `box_oriented_px` | with `box_px` | The minimum-area rectangle enclosing the same eight projected corners: four `x y` pairs, clockwise in the picture from the top-most corner (of two within a thousandth of a pixel in height, the left one). It is found by trying a side along every edge of the corners' convex hull, which is where the least-area rectangle has one (Freeman and Shapira, 1975). Not clipped; where the body is turned against the picture it reaches past `box_px` along the axis it is not aligned with. |
| `truncation` | with `box_px` | The share of `box_px`'s area outside the picture, `0.000` to `1.000`: one less the product of each axis's share inside the picture. `0` on every `wholly` vehicle. |
| `camera_range_m` | with `box_px`, and beside `beyond_draw_distance` (§5.3) | The range in meters from the camera to the center of the vehicle's box. One range, computed by one method from one center and one camera, written once whichever needs it. |
| `pitch_deg`, `roll_deg` | with `box_px` | The body's pitch (positive nose up) and roll (positive right side down) from its transform, degrees in (−180, 180], to two places. With `heading_deg` they are the body's attitude. |
| `<_box3d frame="geodetic">` | a child of `<detail>` beside `_carla`, with `box_px` | The box's eight corners, each a `<corner lat="…" lon="…" hae="…"/>`, in this order: the bottom face front left, front right, back right, back left, then the top face in the same order, so corner *n* + 4 stands above corner *n*. Front is the body's forward axis, the way `heading_deg` points; left and right are as seen from the driver's seat facing forward; bottom and top are along the body's up axis. |

**The corners are the record's own point's conversion.** Each corner of `<_box3d>` is converted from
the CARLA frame exactly as the record's `point` is: the same ENU → ECEF → WGS84 conversion
(`Geodesy.CarlaLocalToGeodetic`) from the same georeference origin, less the height-align offset taken
off the point -- one offset, the point's, taken off all eight -- so `hae` is in the bare-earth
convention of §3 and the box is the body's box shifted whole, never warped by a drape's gradient across
it. A corner at the point's height has the point's `hae`, and where the box stands on the point its
bottom four average to the point. Latitude and longitude are written to seven places and `hae` to two,
as the point is.

**And what else a vehicle in the picture shows (the owner's ruling of 2026-10-06).** Both are the
server's truth for the capture's own frame, read from that frame's world-observer snapshot, never asked
for per vehicle, which would answer the moment asked:

| Attribute | Written | Meaning |
|---|---|---|
| `lights` | on a vehicle whose `in_frame` is `wholly` or `partly`, where the frame's snapshot carried the light state | The lights commanded on for the vehicle on the frame, one word per light in CARLA's flag order, separated by single spaces: `position`, `low_beam`, `high_beam`, `brake`, `right_blinker`, `left_blinker`, `reverse`, `fog`, `interior`, `special1`, `special2`; `none` where no light was on; `bit<n>` for a light CARLA declares and no word names. What was commanded -- by the SUMO drive's session from SUMO's signals and the sun, or by the traffic manager -- not what a body draws. For example `position low_beam brake left_blinker` |
| `pose_source` | with `lights`'s place rule, on a record that names its SUMO vehicle (`sumo_id`), where the frame's snapshot carried a pose source | Where the body's drawn pose on the frame came from, in the owner's words: `sumo` -- the frame falls on a SUMO step, and the position is SUMO's own; `interpolated` -- a frame between SUMO steps, the position filled in along the lane; `jump` -- SUMO reported a step too far from the last to drive in one step, and the body is shown at SUMO's later position for the frames of that step; `stale` -- the body could not be placed on the frame and stands where it was last drawn. A capture from a server built before the jump state writes a jump `sumo`, the one name that server has for it, and its run report says how many |

A sidecar whose frame's snapshot did not carry the light state -- one from a server built before it
did, whose vehicle bytes are zero and would read as every light off -- writes `lights` on no record and
says `lights="unknown"` on `<events>`; one whose snapshot carried no pose source for a drawn SUMO vehicle
in the picture -- an older server, or one that refused the session's -- says `pose_source="unknown"`.

| Attribute | Written | Meaning |
|---|---|---|
| `occlusion` | where occlusion was measured | Fraction of the vehicle's silhouette hidden from this capture's camera by anything nearer — photoreal buildings and trees, terrain relief, other vehicles — 0 (wholly visible) to 1 (wholly hidden). |
| `occlusion_level` | with `occlusion` | The same value as a coarse band: `0` wholly visible · `1` up to 30 % · `2` 30–60 % · `3` 60–90 % · `4` over 90 % (the bands the amodal-segmentation datasets report against). |
| `occlusion_samples` | with `occlusion` | How many points across the vehicle's outline the fraction was measured over. |
| `occlusion_unmeasured` | where `occlusion` is absent, and never beside it | Why, in one word: `behind_camera` · `outside_frame` · `beyond_draw_distance` (§5.3) · `no_depth_camera` (the recorder was started without one) · `no_depth_capture` (the depth camera delivered none to pair the frame with) · `depth_out_of_step` (every depth capture held was of another instant) · `depth_pose_mismatch` (the depth capture of the instant was taken from another pose) · `beyond_depth_range` (every sampled point of the vehicle lies at or beyond the range the depth camera reports over, where its readings saturate) · `no_sample` (no ray of the sampling grid over the box's pixel rectangle met the box: a vehicle narrower than a pixel, or a box with no extent). |

**One reason, the one nearest the vehicle where several hold:** the vehicle's own geometry against
the picture first (`behind_camera`, `outside_frame`), the draw distance it was drawn under next, then
what the depth camera delivered for the whole capture, and last what its sampling met. So in a capture
with no depth camera a vehicle outside the picture says `outside_frame` and one in the picture says
`no_depth_camera`. Occlusion fields are present only on a vehicle whose `in_frame` is `wholly` or
`partly`; a `partly` vehicle is measured over the part of it in the picture.

**Read the fraction against the sample count.** A vehicle far enough away to cover a few pixels yields
a few samples, and can then only report coarse values — a half, a third — however many decimal places
it is written to. Measured at ~1.1 km with a 90° camera, vehicles are about 3 px long and every
reported fraction is a simple ratio, where vehicles inside 150 m give fine-grained values. The
apparent size is written so a consumer can read a fraction against the size it was measured over; the
sidecar applies no size cutoff of its own.

All of these are **camera-relative** — properties of the (vehicle, sensor) pair, not of the vehicle —
so they are emitted only in the recorded sidecar, where the frame already carries a sensor pose (16),
and never on the live UDP feed or the live pull, which have no camera. A record no camera projected
carries none of them. Before 2026-10-05 the five occlusion fields were simply absent in six different
situations and nothing said which; a sidecar from before then carries no `in_frame`, which
`audit_truth_sidecars.py` reports as a defect, as it does occlusion fields on a vehicle whose
`in_frame` says the picture has no view of it, a record with neither occlusion nor a reason, and a
reason beside a measurement. A sidecar from before 2026-10-06 carries no box on a vehicle in the
picture, which the audit reports as a defect, as it does any box field or `<_box3d>` on a vehicle
outside the picture or behind the lens (`camera_range_m` beside `beyond_draw_distance` apart). The audit
also holds every vehicle in the picture to its `lights`, and every SUMO vehicle in it to its
`pose_source`, unless the sidecar says it was unknown, and every vehicle outside the picture to neither.
Measurement and tuning:
[17_Photoreal_Occlusion_Metric.md](17_Photoreal_Occlusion_Metric.md); the projection is
`CarlaNet.Recording.BoxProjector`, the sampling `OcclusionEstimator.Sample`.

### 5.2 Which vehicles are reported

Truth is emitted only for vehicles that have **arrived in the scene**. Boundary-aware staging traffic
spawns a vehicle transparent out in the entry ring and dissolves it in as it crosses into the
interior; one that has never been fully opaque has not arrived and is left out, because a
half-dissolved car is not something a sensor should be told is there. A vehicle that has arrived stays
reported while it dissolves back out on its way off the map. Vehicles nothing fades — a hero vehicle,
scenario traffic, anything spawned by hand — are reported from the moment they spawn, so this makes no
difference to a run without staging traffic.

**A SUMO drive reports its render set.** Its vehicles are drawn by a pool of bodies, each lent to a
SUMO vehicle while it is rendered and parked out of sight, about 300 m below the ground, in between
([03 §8.2](../Plans/SUMO_Behavioral_Capture/03_CoSimulation_Runtime.md)). A recorded sidecar lists
exactly the bodies its frame drew, as the session publishes them per frame, each named by its SUMO
vehicle, and marks its container `vehicles="rendered"`; a parked body is not reported. A frame whose
set is no longer held lists none and says `vehicles="unknown"`, which is not a claim that the scene
was empty (03 §8.9). A run with no render set is reported as above, unchanged.

**Every other reader gets the same set from the server (2026-10-01).** The session names each body
to the server as it lends it and as it gives it back, before the tick cue of the frame the change is
drawn in, and the world observer carries every named body on each snapshot
([03 §8.9, D3.39](../Plans/SUMO_Behavioral_Capture/03_CoSimulation_Runtime.md)). So the live pull
(`get_vehicle_telemetry`) of any process — the live CoT feed, `cot_telemetry.py`, CarlaControl's
`TelemetryController` — lists only the bodies its frame drew, each with its `sumo_id`, `vtype_id`
and `admitted_tick`, and no parked body; it reads the newest frame's actors and render set together,
so a body lent or given back between two ticks is never paired with the other frame's naming. A
recorder with no source of its own writes the same set and says `vehicles="rendered"`. Only bodies a
session named are left out, and the server holds the naming on each body's own record, so it ends
when the session destroys its bodies; a world no session has named a body in — traffic-manager
traffic, scenario entities — is reported exactly as above. Against a server built before this, the
session records the server's refusal on its report and the live pull lists every vehicle actor, as
it did before.

**Under an optional render-set limit the set is smaller, and the shape is the same (2026-10-02).** By
default a SUMO drive draws every vehicle SUMO has. A run may choose to draw fewer -- those inside a
circle, those inside or approaching a registered camera's ground footprint, or no more than a
capacity ([03 §8.3.2, D3.42](../Plans/SUMO_Behavioral_Capture/03_CoSimulation_Runtime.md)) -- and a
vehicle outside the limit has no body, so no frame draws it and no reader, live or recorded, reports
it: the rule above, "exactly the bodies its frame drew", is unchanged. Its absence is not the record
of it: the run that chose the limit names it, and counts the vehicles it left out, in its own report
and manifest ([06 §4.4, D6.40](../Plans/SUMO_Behavioral_Capture/06_Truth_And_Annotation.md)). A
consumer counting vehicles from these events under a limit is counting the drawn vehicles, not the
scenario's.

### 5.3 Draw distance (recorded captures only)

A SUMO drive may set a draw distance, an optional performance control that is off by default
([03 §8.3.3, D3.41](../Plans/SUMO_Behavioral_Capture/03_CoSimulation_Runtime.md)): no camera draws a
body farther than the distance from it. Every vehicle keeps its body and its pose, and is reported
everywhere exactly as with no distance; what changes is that a camera's image may lack it. A recorded
sidecar says so:

| Attribute | Where | Meaning |
|---|---|---|
| `draw_distance_m` | on the sidecar's `<events>` container | The distance, in metres, the frame was drawn under. Absent where none was in force, which is the default, or where the server refused it: the image then drew every vehicle at any range. |
| `beyond_draw_distance` | in a vehicle's `_carla` | `wholly` -- the whole of the vehicle's bounding sphere lies beyond the distance from **this capture's camera**, so the image shows nothing of it; `partly` -- the distance falls across the sphere, so the image may lack the vehicle's far parts. Absent where the vehicle lies wholly inside the distance. |
| `camera_range_m` | in a vehicle's `_carla`, beside `beyond_draw_distance`; and on every vehicle in the picture, as a box field (§5.1) | The range in metres from the camera to the centre of the vehicle's box that the mark rests on. |

The renderer culls each primitive by the nearest point of its bounding sphere, so the sidecar judges
the vehicle the same way, by the sphere around its box, from the camera pose of the capture's own
frame; the mark is taken at the server's default `r.ViewDistanceScale`, 1, which multiplies every draw
distance. **A vehicle marked `wholly` is listed but was not seen**: a consumer building image labels
drops it, and its occlusion attributes are absent with `occlusion_unmeasured="beyond_draw_distance"`
(§5.1), because the depth capture did not draw it either; its `in_frame` still says where its box fell
against the picture, and a vehicle outside the picture or behind the lens says that instead. One marked
`partly` is drawn, perhaps without its far parts, and measured as usual. Like occlusion, the mark is camera-relative -- the same vehicle on the same
frame may be drawn by a nearer camera -- so it is written only in the recorded sidecar and never on
the live pull or the UDP feed, which have no camera.

## 6. Producers

- **Truth (this work):** a shim helper `world.get_vehicle_telemetry()` returns structured records (the §3/§5
  fields); a `to_cot(record)` formatter renders the §2 event; a reference emitter pushes them.
- **Detection (future):** reuses `to_cot()`; only the record producer differs (YOLO + pixel→world).

## 7. Transport (Option B reference emitter)

- **Pull:** `get_vehicle_telemetry()` is a plain client call — any consumer polls at its own rate (≥5 Hz)
  and formats/sends however it likes (honors the "pull and serve however you want" goal).
- **Reference emitter:** a small script streams CoT to **WinTAK** over **UDP** (WinTAK ingests CoT/UDP
  natively; TCP optional). This is a ~50-line swappable shim — not coupled to CARLA.
- **Not building:** an in-engine SignalR/WebSocket broker (Option C) — only if a fan-out consumer later
  demands it.

## 8. Decisions (resolved 2026-06-12)

1. **Default affiliation = neutral `n`** (civilian sim traffic). Per-vehicle override (from `role_name` /
   a scenario map) remains supported. ⇒ truth `type` = **`a-n-G-E-V`**.
2. **Symbol granularity = v0 single `a-n-G-E-V`** for every vehicle; fine class rides in `_carla`
   (`base_type`, `type_id`). Per-class SIDCs deferred to v1 (§4 table, pending an authoritative 2525 check).
3. Heading = **course-over-ground** from velocity (`atan2(vx, -vy)`), vehicle-yaw fallback below ~0.5 m/s;
   callsign = **`<base_type>-<id>`**; **`STALE_SECONDS = 3`**; truth **`ce = le = 0`**. The track's
   course stays the course; the body's own heading rides in `_carla.heading_deg` (2026-10-02, §5).

## 9. Verification (the payoff)

Truth and detection emit identical-shape CoT. A scoring harness associates tracks (position/time gate),
then reports: horizontal position error (truth.point vs detection.point), HAE error, classification
confusion (truth `base_type` vs detection class), and missed/false tracks — the core of the planned
truth-vs-CV comparison.
