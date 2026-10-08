# What a capture folder holds

This page is for people who build pattern-of-life models from the captures these tools make.\
It says what a capture folder holds, how its files relate, and how to read the truth about one vehicle.

The labels are described on [Behavioral annotations](Behavioral_Annotations.md), and the vehicles on [The vehicles behind the truth](Vehicle_Catalogue.md).\
Every field is described in full on the schema pages listed in [`../Schemas/README.md`](../Schemas/README.md).\
This page links them rather than repeating them.

**The files hold labels and truth, never scores or verdicts.**\
A truth file holds only what a scenario's author declared, what happened in the simulation, and what was measured.\
No file holds a pass mark, a quality score or a judgment of whether a still or a label is good enough.\
Those depend on your model, and they are yours to make.

The examples on this page come from one real capture, `cap-20261008-041347-270d6d`, made on 2026-10-08 from the scenario `Arapahoe_I25_SupervisionCheck`.\
One camera, `Check_Overhead_1`, looked down at South Yosemite Street in Arapahoe County, Colorado, for three simulated minutes and took two stills each simulated second: 360 stills.\
Every file in it is valid against its schema.

## The layout

```
cap-20261008-041347-270d6d/
  Check_Overhead_1/                                  one folder per camera
    Check_Overhead_1_2026.10.07_21.14.04.926.png     a still
    Check_Overhead_1_2026.10.07_21.14.04.926.xml     its truth sidecar
    ...                                              359 more pairs
  truth/
    manifest.jsonl                                   the run manifest
    world_truth_track.csv                            the world truth track
    world_truth_track.summary.json                   the track's summary
```

- **A folder per camera**, named after the camera.\
  Each still is a PNG, and its truth sidecar is an XML file with the same name.\
  The time in the name is the recording computer's local clock, to the millisecond.\
  It is not the simulated time, and it is not UTC: the first still's sidecar says it was captured at `2026-10-08T04:14:04.926Z`.\
  Order stills by their frame number, `tick`, not by their names.
- **`truth/`** holds the [run manifest](../Schemas/Run_Manifest.md), the [world truth track](../Schemas/World_Truth_Track.md) and the [track's summary](../Schemas/World_Truth_Track_Summary.md).

Two things a capture depends on are kept outside the folder:

- **The run's own records.**\
  `carla-capture` writes how the run ended, what it resolved and what it was bound to in files beside the capture, never inside it.\
  See [Run result](../Schemas/Run_Result.md).
- **The scenario's supervision plan**, `<scenario_id>.supervision.json`, written when the scenario was compiled.\
  It holds the definition of every label.\
  The manifest's first row names its path and its SHA-256.\
  You need it to read what a label means.\
  See [Supervision plan](../Schemas/Supervision_Plan.md).

## What is per still and what is per run

| | Per still | Per run |
|---|---|---|
| Files | the PNG and its sidecar, in the camera's folder | the manifest, the track and the track's summary, in `truth/` |
| Time | one simulation frame | the whole run; the track covers the capture window |
| Vehicles | every body that frame drew | every vehicle SUMO had |
| Labels | what was in force for each drawn vehicle on that frame | every label the plan declares, and when each interval opened and closed |

**Per still.**\
The PNG holds the picture and up to four text chunks.\
[`carla:capture`](../Schemas/PNG_Chunk_Capture.md) is always there: it names the frame (`tick`), the run and the release that wrote the file.\
[`carla:solar`](../Schemas/PNG_Chunk_Solar.md), [`carla:illumination`](../Schemas/PNG_Chunk_Illumination.md) and [`carla:sensor`](../Schemas/PNG_Chunk_Sensor.md) repeat the sun and the camera from the sidecar.\
So a picture separated from its sidecar still says where it came from.

The sidecar ([Truth sidecar](../Schemas/Truth_Sidecar.md)) holds the truth of the still's own frame: the camera's position, direction, lens and exposure; the sun; and a record for every vehicle the frame drew, with its labels.\
A still whose frame's truth could not be read is not written at all.\
So a picture never comes with another frame's truth.

**Per run.**\
The manifest records the run as it happened, one JSON object per line.\
It says what the run was:

- the scenario and the digests of its files
- the SUMO settings
- the clocks
- the rule the vehicle lights follow
- the sun

It lists the plan's labels.\
Then it records:

- each camera
- every vehicle that started or stopped being drawn
- SUMO's events (collisions, emergency stops, teleports)
- the sun at the window's first and last capture
- every label interval opening and closing
- why the run ended

The world truth track lists every vehicle SUMO had at each SUMO step of the capture window, whether a body drew it or not.

## Reading a vehicle record in a sidecar

A sidecar is a list of Cursor-on-Target (CoT) events inside one `<events>` element.\
The first event is the camera.\
You can tell it apart because its `<detail>` starts with `<contact>`.\
Every other event is a vehicle.

Here is the container of the still at frame 151288, and the record of the car called `dweller`, which is parked at the curb:

```xml
<events format_version="1" captured="2026-10-08T04:14:22.253Z" count="44" source="truth" tick="151288" sim_time_s="344.799094" run_id="cap-20261008-041347-270d6d" vehicles="rendered" plan_id="Arapahoe_I25_SupervisionCheck" vocabulary="3" vocabulary_digest="0fbcea06c4a28575486d05528b3bdf029d139a829c8ec7a3f0adc71527f9ece6">
  ...
  <event version="2.0" uid="CARLA-TRUTH-SUMO-dweller" type="a-n-G-E-V" how="m-g" time="2026-10-08T04:14:22.253Z" start="2026-10-08T04:14:22.253Z" stale="2026-10-08T04:14:25.253Z">
    <point lat="39.5972480" lon="-104.8888075" hae="1735.88" ce="0.0" le="0.0" />
    <detail>
      <track course="0.4" speed="0.00" />
      <contact callsign="car-dweller" />
      <_carla source="truth" actor_id="117" type_id="vehicle.ue4.ford.mustang" base_type="car" special_type="" length_m="4.72" width_m="1.89" height_m="1.30" color="0,0,0" role_name="sumo" vx="0.00" vy="0.00" vz="0.00" heading_deg="0.4" pitch_deg="3.62" roll_deg="-5.09" in_frame="wholly" occlusion="0.112" occlusion_level="1" occlusion_samples="233" apparent_width_px="118" apparent_height_px="55" box_px="599.28 531.88 717.51 586.43" box_oriented_px="600.52 531.84 717.73 535.11 716.29 586.61 599.08 583.33" truncation="0.000" lights="none" pose_source="sumo" camera_range_m="87.0" sumo_id="dweller" vtype_id="car.vehicle.ue4.ford.mustang" admitted_tick="149499" sumo_angle_deg="0.4" />
      <_box3d frame="geodetic">
        <corner lat="39.5972696" lon="-104.8888183" hae="1735.94" />
        <corner lat="39.5972694" lon="-104.8887963" hae="1736.11" />
        <corner lat="39.5972270" lon="-104.8887967" hae="1735.81" />
        <corner lat="39.5972272" lon="-104.8888187" hae="1735.65" />
        <corner lat="39.5972688" lon="-104.8888196" hae="1737.24" />
        <corner lat="39.5972686" lon="-104.8887977" hae="1737.41" />
        <corner lat="39.5972262" lon="-104.8887981" hae="1737.11" />
        <corner lat="39.5972265" lon="-104.8888200" hae="1736.94" />
      </_box3d>
      <_supervision state="annotated" vocabulary="3" vocabulary_digest="0fbcea06c4a28575486d05528b3bdf029d139a829c8ec7a3f0adc71527f9ece6">
        <annotation instance="Arapahoe_I25_SupervisionCheck/kerbside_dwell" labels="check:kerbside_dwell" phase="dwell" role="subject" />
      </_supervision>
    </detail>
  </event>
```

Every attribute is described on the [Truth sidecar](../Schemas/Truth_Sidecar.md) page, in its table for [`<_carla>`](../Schemas/Truth_Sidecar.md#_carla-the-vehicles-truth-record) and its [word lists](../Schemas/Truth_Sidecar.md#word-lists).

### Which vehicle it is

- `uid` is `CARLA-TRUTH-SUMO-<sumo_id>`, and `sumo_id` names the SUMO vehicle.\
  Follow a vehicle from still to still by its `uid` or its `sumo_id`.
- `actor_id` names the CARLA body that drew the vehicle on this frame.\
  The world reuses its bodies, so one body draws a series of different vehicles over a run.\
  In the sample capture, 19 bodies drew more than one vehicle.\
  Never follow a vehicle by `actor_id`.
- `admitted_tick` is the frame on which this body began drawing this vehicle.
- `callsign` is `<base_type>-<sumo_id>`.
- The CoT `type`, here `a-n-G-E-V`, is how a map display colors the track.\
  It is not a label.

### Whether it is in the picture

- The sidecar lists every body the frame drew (`vehicles="rendered"`), not only the ones in the picture.\
  This still lists 44 vehicles (`count="44"`).\
  Two of them are in the picture.
- `in_frame` says where the vehicle's box fell: `wholly` in the picture, `partly`, `none`, or `behind_camera`.
- Only a vehicle `wholly` or `partly` in the picture carries its boxes, its pitch and roll, its lights and its pose source.\
  A vehicle outside the picture still has its position, motion, size and labels, and `apparent_width_px` and `apparent_height_px` say how large it would look.
- A run can draw vehicles only out to a set distance from the camera.\
  Then the container carries `draw_distance_m`, and a vehicle the distance kept out of the image carries `beyond_draw_distance`, `partly` or `wholly`.\
  A vehicle `wholly` beyond it is in the truth but not in the picture.\
  The sample capture had no draw distance.
- A container that says `vehicles="unknown"` lists no vehicle because the frame's set of drawn bodies was no longer held.\
  Its empty list does not mean an empty road.

### Its boxes, truncation and occlusion

- `box_px` is the upright rectangle around the vehicle in pixels: x min, y min, x max, y max.\
  Pixels are counted from the picture's top-left corner, x to the right and y down.
- `box_oriented_px` is the smallest rectangle around the vehicle turned with it: four x y corners, clockwise from the top-most.
- Both are the eight corners of the body's 3D box, projected through this still's own camera.\
  The same eight corners, in latitude, longitude and height, are in `<_box3d frame="geodetic">`, in the order the sidecar page gives.
- Boxes are not clipped to the picture, so a coordinate can be negative or larger than the picture.\
  `truncation` is the share of `box_px`'s area that lies outside the picture: 0 for a box wholly inside.
- `occlusion` is the share of the vehicle's outline hidden from the camera by anything nearer: 0 fully visible, 1 fully hidden.\
  `occlusion_level` gives it as a band from 0 to 4, and `occlusion_samples` says how many points it was measured over.\
  The parked `dweller` above is 0.112 hidden, level 1, measured over 233 points.
- Where occlusion was not measured, those three attributes are absent and `occlusion_unmeasured` says why, such as `outside_frame`.\
  An absent occlusion never means that nothing was in the way.\
  In the sample capture, one record in the picture says `no_sample`: the vehicle was narrower than the sampling step.
- `camera_range_m` is the distance from the camera to the center of the box.
- `heading_deg`, `pitch_deg` and `roll_deg` are the drawn body's yaw, pitch and roll, in degrees.\
  Pitch is positive nose up, and roll is positive right side down.

### Its lights

- `lights` lists the lights commanded on for the vehicle on this frame, in words, or says `none`.\
  The sample capture holds `none`, `brake`, `right_blinker` and `brake right_blinker`.\
  The `dweller` comes into the picture at 104.0 s with `right_blinker`, as it pulls toward the curb, and shows `brake right_blinker` from 106.5 s.
- The rule the lights follow is written once per run, as `vehicle_lights` on the manifest's first row.\
  In the sample, headlights come on below a geometric sun elevation of 3° and go off above 6°, and brake lights and turn signals follow SUMO's own signals for each vehicle (`sumo_signals`).\
  The sun stood at 5.5°, and no headlight was on.
- These are the lights commanded on.\
  Whether the picture shows a lit lamp depends on the body.\
  In the current vehicle catalog only the fire truck's high beams changed the picture when they were measured.\
  See [Lights](Vehicle_Catalogue.md#lights).
- A container that says `lights="unknown"` means a vehicle in the picture has no `lights` because the frame's data did not carry them.

### Its pose source

SUMO moves each vehicle in steps of fixed length, 0.05 s in the sample capture.\
CARLA can draw several frames between two SUMO steps.\
`pose_source` says where the drawn position came from on this frame:

- `sumo`: the frame falls on a SUMO step, and the position is SUMO's own.
- `interpolated`: the frame falls between two SUMO steps.\
  Its position is filled in along the lane, between where SUMO had the vehicle at the step before and at the step after.
- `jump`: SUMO moved the vehicle farther in one step than it could drive.\
  The body is shown at SUMO's later position.
- `stale`: the body could not be placed on this frame.\
  It stands where it was last drawn.

In the sample capture SUMO took one step for every frame (`world_ticks_per_sumo_step` is 1 in the manifest's `clock`), so every record says `sumo`.\
A container that says `pose_source="unknown"` means a SUMO vehicle in the picture has no `pose_source` because the frame's data did not carry one.

### Its labels

`<_supervision>` is what the scenario's author asserts about this vehicle on this frame.\
Every SUMO vehicle the frame drew has one, whether it is in the picture or not.\
Its `state` is `annotated`, `nominal` or `unlabelled`, and each `<annotation>` names a labeled behavior in force for the vehicle.\
[Behavioral annotations](Behavioral_Annotations.md) explains them.

## How the stills line up with the manifest's intervals

### The clocks

A capture uses five clocks.\
Two of them count simulated seconds, and they do not agree.

| Clock | Where you find it | What it counts |
|---|---|---|
| CARLA frame number | sidecar and `carla:capture` `tick`; track `frame`; manifest `frame`, `observed_start_frame`, `after_frame`; sidecar `admitted_tick` | One per world step. |
| SUMO's simulated time | manifest `sim_time_s`, `committed_start_s` and its other instants in seconds; track `sim_time_s`; the plan's `declared_start_s` and `declared_end_s` | Seconds from the scenario's second zero, on SUMO's clock as TraCI reports it. |
| CARLA's own elapsed time | sidecar and `carla:capture` `sim_time_s` | Seconds the CARLA server's world has run. Its zero is the server's, not the scenario's. |
| Simulated civil time | track `time_utc`; sidecar `<_illumination>` `declared_civil` and `declared_utc`; manifest `declared_start_civil`, `civil_begin` | SUMO's simulated time placed on the calendar by the scenario's [epoch](../Schemas/Epoch.md). |
| Wall clock | sidecar `captured` and each event's `time`, `start` and `stale`; file names; `written_utc`; manifest `opened_wall_utc` and `closed_wall_utc` | The recording computer's clock while the run was made. |

**A still's `sim_time_s` is not the manifest's.**\
The still at frame 151288 says `sim_time_s="344.799094"`.\
The same frame in the world truth track has `sim_time_s` 109.5, on SUMO's clock.\
In this capture the difference was 235.299 s for every still, and it changes from run to run.\
Never compare a still's `sim_time_s` with a time in the manifest or the track.

### Putting a still on SUMO's clock

Use the still's frame number, `tick`:

1. Find the row of the world truth track whose `frame` is the still's `tick`, and read its `sim_time_s`.\
   In the sample capture every still's `tick` is in the track.\
   A track that samples less often than every SUMO step may not hold every frame; then use the next way.
2. Or work it out from the manifest.\
   The `solar_window_open` row gives the window's first capture frame and its time: `frame` 150298 at `sim_time_s` 60.\
   The first row's `clock.world_delta_s` gives the length of one frame: 0.05 s.\
   A still at frame `tick` is at 60 + (`tick` − 150298) × 0.05 seconds.\
   For the still at frame 151288, that is 109.5.
3. Where the run declared an epoch and its illumination, the sidecar's `<_illumination>` `declared_utc` is the same instant as the track's `time_utc`: `2026-09-29T13:27:49.5Z` for this still, and `2026-09-29T13:27:49.500Z` in the track.

### Which stills an interval covers

The manifest names each label interval by its instance, participant and phase, and writes a row as it opens and another as it closes ([`interval_opened` and `interval_closed`](../Schemas/Run_Manifest.md#interval_opened-and-interval_closed)).\
The opening row's `sim_time_s` is the start, and the closing row's is the end, both on SUMO's clock.\
A label is in force from the frame at its start up to, but not including, the frame at its end.

In the sample, the `dwell` phase of `Arapahoe_I25_SupervisionCheck/kerbside_dwell` opened at 109.4 and closed at 229.4:

```json
{"row":"interval_opened","sim_time_s":109.4,"instance_id":"Arapahoe_I25_SupervisionCheck/kerbside_dwell","participant":"dweller","phase":"dwell","role":"subject","declared_start_s":null,"declared_end_s":null,"declared_duration_s":120,"committed_start_s":109.4,"observed_start_s":null,"observed_start_frame":null,"begun_before_window":false}
{"row":"interval_closed","sim_time_s":229.4,"instance_id":"Arapahoe_I25_SupervisionCheck/kerbside_dwell","participant":"dweller","phase":"dwell","closed_by":"trigger","declared_start_s":null,"declared_end_s":null,"declared_duration_s":120,"committed_start_s":109.4,"observed_start_s":109.4,"observed_start_frame":151286,"begun_before_window":false,"committed_end_s":229.4,"not_drawn":[]}
```

The stills from 109.5 s (frame 151288) to 229.0 s (frame 153678) carry the `dwell` annotation: 240 stills.\
The still at 229.5 s (frame 153688) does not.

An interval still open when the run ended has no `interval_closed` row.\
The last row, [`manifest_closed`](../Schemas/Run_Manifest.md#manifest_closed), lists it in `open_intervals` and gives the reason it closes with in `open_intervals_close_as`.\
In the sample, the `transit` phase of `Arapahoe_I25_SupervisionCheck/through_transit` opened at 90.05 s and was still open when the window closed at 240 s, so it closes as `capture_window_end`.

For a drawn vehicle you rarely need this arithmetic: each sidecar already carries the labels in force on its own frame.\
Use the manifest for the exact edges, which fall between stills, for the three onsets, and for vehicles no body drew.

## What the world truth track adds

The [world truth track](../Schemas/World_Truth_Track.md) lists every vehicle SUMO had at each sampled SUMO step of the capture window, whether a body drew it or not: one row per vehicle per sample.\
The sample capture sampled every SUMO step (`every_sumo_steps` 1 in the summary): 3,600 samples and 180,125 rows.

- **Counts over the whole scene come from here.**\
  A sidecar lists only the bodies its frame drew, and a picture shows only the few in view.\
  The still at frame 151288 lists 44 vehicles and shows two.
- **Whether a body drew the vehicle** is `render_state`: `rendered`, or `simulated_only` with a `render_reason`.\
  In the sample, 6 rows are `simulated_only` with `not_drawn`: three vehicles of the flow `clinton_to_arapahoe_west`, each on its first two SUMO steps.
- **Join a row to a sidecar's record** by `sumo_id` and the frame: the track's `frame` is the sidecar's `tick`.
- **The track carries no labels.**\
  Join labels to it by `sumo_id`, from the manifest's `instance`, `series` and `cohort` rows and its interval rows.
- **Its values are SUMO's.**\
  The drawn body's pose, size and boxes are in the sidecar.

The `dweller`'s row for frame 151288:

```
time_utc,sim_time_s,uid,callsign,cot_type,how,lat,lon,hae_m,ce_m,le_m,course_deg,speed_mps,vx,vy,vz,base_type,type_id,special_type,length_m,width_m,height_m,color,role_name,edge,lane,sumo_x,sumo_y,carla_x,carla_y,sumo_id,entity_id,frame,render_state,render_reason,actor_id,in_window,sun_elevation_deg,sun_corrected_elevation_deg,illumination_band,illumination_band_elevation
2026-09-29T13:27:49.500Z,109.5,CARLA-TRUTH-SUMO-dweller,car-dweller,a-n-G-E-V,m-g,39.5972695,-104.8888073,1735.85,0.0,0.0,0.4,0.00,0.00,0.00,0.00,car,car.vehicle.ue4.ford.mustang,,4.72,1.84,1.30,"179,184,199",dweller,427819553#0,,-370.93,328.68,-370.93,-328.68,dweller,dweller,151288,rendered,,117,1,5.549088,5.694,golden,refraction_corrected
```

Its `lane` is empty because the car is parked off the lane, at the curb.

## How to tell which release made a file

**The producer record.**\
Every file a capture writes says what made it:

- the sidecar in `<_producer>`
- the PNG in its `carla:capture` chunk's `producer`
- the manifest in its first row's `producer`
- the track in its summary's `producer`

The record has the same fields everywhere ([`<_producer>`](../Schemas/Truth_Sidecar.md#_producer-and-_server-what-made-the-file), [`carla:capture`](../Schemas/PNG_Chunk_Capture.md)).\
From the sample:

```xml
<_producer tool="carlacontrol.CaptureSession" tool_version="0.10.0+g90c0f69d2" carlanet="0.10.0+g90c0f69d2" sumo="1.27.0" written_utc="2026-10-08T04:14:22.265Z">
  <_server available="true" release="0.10.0" world_interface="1.0" build="editor" configuration="Development" carla_commit="90c0f69d29a6c5b8fd593d51ce9f82f4d165551a" content_commit="unknown" engine_commit="unknown" commits_from="compiled" />
</_producer>
```

- `tool` and `tool_version` name the tool that wrote the file.\
  `carlanet` is the CarlaNet release.\
  `0.10.0` is a tagged release.\
  `0.10.0+g90c0f69d2` is a build from commit `90c0f69d2` that is not a tagged release.
- `sumo` is the SUMO release that drove the vehicles.
- `<_server>` is the CARLA server's own account of its build.\
  Here it is an editor build in the Development configuration, from CARLA commit `90c0f69d29a6c5b8fd593d51ce9f82f4d165551a`.\
  A value the server cannot know is `unknown`.
- A file written before producer records existed has none.

**The format version.**\
Each file names its format: the sidecar's and the chunk's `format_version`, the manifest's `manifest_version`, and the summary's `world_truth_track_version`, which is also the track's.\
A file that names no version is version 1.\
The readers in these tools refuse a version newer than they know, by name, rather than reading it in part.

**`carla-validate`** checks every file of a capture folder against its schema.\
It is installed with carlacontrol.\
From a checkout, `python CarlaControl/scripts/validate_capture.py <folder>` is the same tool.\
On the sample capture it reports:

```
truth sidecars                  360 checked, 0 failed
PNG text chunks                 360 checked, 0 failed
run manifests                     1 checked, 0 failed
world truth tracks                1 checked, 0 failed
world truth track summaries       1 checked, 0 failed
every file keeps its schema
```

It exits with 0 when every file keeps its schema, 1 when any does not, and 2 when the folder holds no file a schema describes.\
It reports a file of a newer format version as written by a newer release.

It does not check the pixels.\
`carla-audit-sidecars <folder>` checks the rules a schema cannot, such as a box appearing exactly when the vehicle is in the picture.

## Fields that are easy to misread

**`type_id`.**\
In a sidecar, `type_id` is the CARLA body, its blueprint: `vehicle.ue4.ford.mustang`.\
In the track, `type_id` is the SUMO vehicle type: `car.vehicle.ue4.ford.mustang`.\
The sidecar calls the SUMO vehicle type `vtype_id`.

**`time_utc` and `captured`.**\
The track's `time_utc` is simulated civil time: the scenario's clock placed on the calendar by its epoch.\
The sidecar's times (`captured`, and each event's `time`, `start` and `stale`) are the real clock of the computer that recorded the still.\
For the still at frame 151288, the track says `2026-09-29T13:27:49.500Z` and the sidecar says `2026-10-08T04:14:22.253Z`.

**`sim_time_s`.**\
In the manifest and the track it is SUMO's simulated time.\
In a sidecar and its `carla:capture` chunk it is CARLA's own elapsed time, which has another zero.\
See [The clocks](#the-clocks).

**Position.**\
The track's `lat` and `lon` are SUMO's position for the vehicle: the middle of its front bumper.\
The sidecar's `<point>` is the CARLA body's origin, which lies behind the bumper by about half the body's length.\
For the `dweller` at frame 151288 the two points are 2.39 m apart.\
The track's point is the middle of the front face of the sidecar's `<_box3d>`.

The heights differ too: the track's `hae_m` is the bare ground under the bumper point, and the sidecar's `hae` is the body origin's.

**Size and color.**\
The sidecar's `length_m`, `width_m`, `height_m` and `color` are the drawn body's: its measured box, mirrors included, and its color attribute.\
The track's are the SUMO vehicle type's: the same length and height, the width without mirrors, and the color sumo-gui draws the type in.\
That color never reaches the picture.\
The `dweller` is 4.72 × 1.89 × 1.30 m and `0,0,0` in the sidecar, and 4.72 × 1.84 × 1.30 m and `179,184,199` in the track.\
See [The vehicles behind the truth](Vehicle_Catalogue.md).

**`role_name`.**\
In a sidecar it is the CARLA actor's role, `sumo` for every body a SUMO drive lent.\
In the track it is the SUMO flow the vehicle came from, such as `arapahoe_east_to_west`, or the vehicle's own id where it came from no flow, such as `dweller`.

**Direction.**\
The track's `course_deg` is SUMO's angle for the vehicle.\
The sidecar's `heading_deg` is the way the drawn body points, and its `<track course>` is the way the body moves; these two differ while the body turns or changes lanes.\
SUMO's angle can differ from both.\
The sidecar repeats it as `sumo_angle_deg`, so you can compare them on one record.
