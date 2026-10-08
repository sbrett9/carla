# Truth sidecar

**Schema:** `CarlaControl/schemas/truth_sidecar.xsd` (XML Schema 1.0)
**Identifier:** `urn:carla-sumo-capture:schema:truth-sidecar:1`
**Format version described:** 1

## What the file is

The truth sidecar is an XML file written beside every still. It holds the ground truth of the
simulation frame the still was rendered on: where the camera was and how it was set up, the sun, and
every vehicle with its position, motion, size and, for a vehicle in the picture, its box in pixels and
in latitude, longitude and height.

The still and its sidecar share a name: `<camera>/<camera>_<local capture time>.png` and `.xml`, for
example `Check_Overhead_1/Check_Overhead_1_2026.10.07_10.34.51.318.xml`. The time in the name is the
capture computer's local clock, to the millisecond. The same instant in UTC is the `captured`
attribute inside the file.

The truth in a sidecar is always the truth of the still's own frame. A still whose frame's truth could
not be read is not written at all, so you never get a still paired with a neighboring frame's truth.

Each vehicle is a Cursor-on-Target (CoT) `<event>`, the same kind of event the live telemetry feed
sends, with extra detail elements whose names begin with `_`.

## Who writes it, and when

The CarlaNet recorder writes one sidecar for every still it saves, in the same moment as the PNG.
`carla-capture` runs a recorder for each camera of a capture run and puts each camera's stills in a
folder named after the camera, inside the capture folder (`Build/captures/<session id>/` from a
checkout). `carla-drive` and `carla-sctmv` write stills and sidecars the same way.

## Structure

Elements appear in this order. Elements marked "optional" may be absent.

```
<events>                             the capture, one camera, one frame
  <_producer>                        optional: what made the file
    <_server/>                       optional: the CARLA server's build
  </_producer>
  <_solar/>                          optional: the sun the world reported
  <_illumination/>                   optional: what the run declared the sun to be
  <event>                            optional: the camera platform
    <point/> <detail> <contact/> <track/> <sensor/> <_carla_intrinsics/> <_carla_exposure/> </detail>
  </event>
  <event>                            one per vehicle
    <point/> <detail> <track/> <contact/> <_carla/> <_box3d>8 x <corner/></_box3d> <_supervision>
      <annotation/> ... </_supervision> </detail>
  </event>
</events>
```

The camera platform's `<detail>` begins with `<contact>`; a vehicle's begins with `<track>`. That is how
the two kinds of event are told apart.

## Conventions

- **Positions** are WGS84 latitude and longitude in degrees.
- **Heights** (`hae`) are meters above the WGS84 ellipsoid, in the bare-earth convention: the height of
  the bare ground model the world was built on, without the offset that lines the road up with the
  photographic terrain. A camera's physical height is its `hae` plus `align_offset_m`.
- **Directions** (`course`, `heading_deg`, `azimuth`) are degrees clockwise from true north.
- **Velocities** (`vx`, `vy`, `vz`) are in CARLA's world frame: x east, y south, z up.
- **Pixels** are counted from the picture's top-left corner: x to the right, y down. Boxes are not
  clipped to the picture, so a coordinate can be negative or larger than the picture.
- **Times** are UTC to the millisecond, such as `2026-10-07T17:34:49.411Z`.
- **True or false** is written `true` or `false`.

In the tables, **Required** is "Yes" for an attribute every element of that kind carries, "No" for one
that may be absent, and "In picture" for one that a vehicle carries exactly when its box fell in the
picture (`in_frame` is `wholly` or `partly`) and never otherwise. The schema marks these last ones
with `cap:onlyInPicture` and gives every unit in `cap:unit`.

## `<events>`: the capture

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `format_version` | integer, always 1 | | No | The file's format version; absent from a file written before files carried one, which is version 1. |
| `captured` | UTC time | | Yes | When the still was captured, by the recording computer's clock. |
| `count` | integer | | Yes | How many vehicle events follow; the camera platform's event is not counted. |
| `source` | text, always `truth` | | Yes | The events are the simulator's ground truth. |
| `tick` | integer | | No | The simulation frame the image was rendered on; every recorder writes it. |
| `sim_time_s` | decimal | seconds | No | Simulated time at that frame; written with `tick`. |
| `run_id` | text | | No | The run the capture belongs to; every recorder writes one. |
| `scenario_id` | text | | No | The scenario driving the run, where there is one. |
| `seed` | integer | | No | The seed the run was started with, where one was given. |
| `vehicles` | word | | No | `rendered`: the events are exactly the bodies this frame drew, each named by the SUMO vehicle it drew. `unknown`: the frame's set of drawn bodies was no longer held, so no vehicle is listed, and the empty list does not mean an empty scene. Absent: the events are every vehicle actor the world held. |
| `draw_distance_m` | decimal | meters | No | The draw distance the image was rendered under; absent where vehicles were drawn at any range. |
| `supervision` | text, always `unknown` | | No | A supervision plan was in force but this frame's supervision could not be read, so no vehicle carries any. |
| `plan_id` | text | | No | The supervision plan in force on this frame, where one was. |
| `vocabulary` | integer | | No | The annotation vocabulary's core version, written with `plan_id`. |
| `vocabulary_digest` | text, 64 hex digits | | No | The digest of the plan's vocabulary, which pins what every label means, written with `plan_id`. |
| `lights` | text, always `unknown` | | No | A vehicle in the picture has no `lights` because the frame's snapshot did not carry them. |
| `pose_source` | text, always `unknown` | | No | A SUMO vehicle in the picture has no `pose_source` because the frame's snapshot did not carry one. |

## `<_producer>` and `<_server>`: what made the file

`<_producer>` is the first child of `<events>` in every sidecar written since the producer record was
added. A sidecar written before it has none.

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `tool` | text | | Yes | The component that wrote the file, such as `carlacontrol.CaptureSession`. |
| `tool_version` | text | | No | The release of the package the tool comes from, where the tool said. |
| `carlanet` | text | | Yes | The carlanet release that wrote the file: `0.10.0` for a tagged release, `0.10.0+g<commit>` for any other build. |
| `sumo` | text | | No | The SUMO release, such as `1.27.0`, where SUMO drove the vehicles. |
| `written_utc` | UTC time | | No | When the file was written. |

`<_server>`, inside `<_producer>`, is the CARLA server's own account of its build:

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `available` | true or false | | Yes | Whether the server answered the build identity call; a server built before the call did not. |
| `release` | text | | Yes | The CARLA release the server was compiled with, or `unknown`. |
| `world_interface` | text | | Yes | The world interface version the server declares, major.minor, or `unknown`. |
| `build` | text | | No | `package` or `editor`; only where `available` is `true`. |
| `configuration` | text | | No | The build configuration, such as `Shipping`; only where `available` is `true`. |
| `carla_commit` | text | | No | The CARLA commit the server was built from; only where `available` is `true`. |
| `content_commit` | text | | No | The content commit its package was cooked from; only where `available` is `true`. |
| `engine_commit` | text | | No | The Unreal Engine commit; only where `available` is `true`. |
| `commits_from` | text | | No | Where the three commits came from: `version_file`, `compiled` or `none`; only where `available` is `true`. |
| `reason` | text | | No | Why the identity is not available; only where `available` is `false`. |

A value the server cannot know is `unknown`.

## `<_solar>`: the sun the world reported

Written for every frame whose world reported a sun. The same values are in the still's `carla:solar`
text chunk.

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `solar_time` | decimal | hours | Yes | The sun's clock in its own time zone, from 0 up to 24. |
| `date` | date | | Yes | The sun's calendar date, year-month-day. |
| `time_zone` | decimal | hours | Yes | The time zone the sun's clock is read in, hours from UTC. |
| `lat` | decimal | degrees | Yes | The latitude the sun is computed for: the world's georeference origin. |
| `lon` | decimal | degrees | Yes | The longitude the sun is computed for. |
| `sun_elevation_deg` | decimal | degrees | Yes | The sun's geometric elevation above the horizon, with no atmosphere. |
| `sun_corrected_elevation_deg` | decimal | degrees | No | The elevation with atmospheric refraction applied, which is the elevation the frame was lit at; absent where the server does not report it. |
| `sun_azimuth_deg` | decimal | degrees | Yes | The sun's azimuth, clockwise from true north. |
| `advancing` | true or false | | Yes | Whether the engine itself moved the sun's clock; `false` in a SUMO drive, which sets the sun on every tick itself. |
| `rate` | decimal | seconds per second | Yes | Sun-clock seconds per simulated second while the engine advances it. |
| `illumination_band` | word | | No | The sun's illumination band (see below); absent where the elevation is not a real sun's. |
| `illumination_band_elevation` | word | | No | Which elevation the band was cut from: `refraction_corrected` wherever the block carries it, `geometric` otherwise. Written with `illumination_band`. |

**Illumination bands**, cut by the sun's refraction-corrected elevation in degrees: `day` above 6,
`golden` above 0, `civil_twilight` above -6, `nautical_twilight` above -12, `astronomical_twilight`
above -18, `night` at -18 and below. Each band includes its upper edge.

## `<_illumination>`: what the run declared the sun to be

Written only in a run that declares its illumination. `<_solar>` is what the world did; this is what
the run said it should be, and how far apart the two were. The same values are in the still's
`carla:illumination` text chunk.

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `policy` | word | | Yes | The illumination policy: `freeze_at_window_start`, `advance`, `freeze_at` or `ignore`. |
| `epoch_honoured` | true or false | | Yes | Whether the frame was lit by the sun of its own declared civil time. |
| `audited` | true or false | | Yes | Whether the world's sun was compared with the declaration on this frame's tick. |
| `rate` | decimal | seconds per second | No | Sun-clock seconds per simulated second; under `advance` only. |
| `freeze_at_civil_time` | time of day | | No | The time of day the sun is held at, hh:mm:ss; under `freeze_at` only. |
| `epoch_digest` | text, 64 hex digits | | No | SHA-256 of the scenario's epoch. |
| `epoch_civil` | civil time | | No | The civil time that simulated second zero stands for, with its UTC offset. |
| `utc_offset_hours` | decimal | hours | No | The epoch's declared UTC offset. |
| `declared_civil` | civil time | | No | This frame's simulated instant as a civil time, with its UTC offset. |
| `declared_utc` | UTC time | | No | The same instant in UTC, for joining this frame to records outside the simulation. |
| `sun_declared` | civil time | | No | The date and clock the sun was declared to hold for this frame: the frame's own civil time under a policy that honors the epoch, the window's opening time under a freeze. |
| `sun_elevation_declared_deg` | decimal | degrees | No | The declared sun's geometric elevation. |
| `sun_corrected_elevation_declared_deg` | decimal | degrees | No | The declared sun's refraction-corrected elevation. |
| `declared_elevation` | word | | No | Which elevation a declared window elevation means: `refraction_corrected` or `geometric`. |
| `residual_clock_s` | decimal | seconds | No | The world's sun clock minus the declared one. |
| `residual_deg` | decimal | degrees | No | The angle between the world's sun and the declared sun. |
| `residual_corrected_deg` | decimal | degrees | No | The world's refraction-corrected elevation minus the declared one. |

A civil time looks like `2026-09-29T07:27:00-06:00`, with a fraction of a second where there is one.

## The camera platform's event

Written first, before the vehicles, wherever the recorder was told about the camera platform. The same
values are in the still's `carla:sensor` text chunk.

`<event>` attributes, for this event and every vehicle event:

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `version` | text, always `2.0` | | Yes | The Cursor-on-Target version. |
| `uid` | text | | Yes | The track's identifier: the camera's, such as `CARLA-SENSOR-107`, or a vehicle's (see below). |
| `type` | text | | Yes | The CoT type: an air track such as `a-f-A-M-F-Q` for the camera, `a-<affiliation>-G-E-V` (default `a-n-G-E-V`) for a vehicle. |
| `how` | text, always `m-g` | | Yes | Machine generated. |
| `time` | UTC time | | Yes | The capture instant, the same as `captured`. |
| `start` | UTC time | | Yes | The same instant. |
| `stale` | UTC time | | Yes | When the event goes stale: the capture instant plus 3 seconds by default. |

`<point>`, for this event and every vehicle event:

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `lat` | decimal | degrees | Yes | Latitude. For a vehicle, of the CARLA actor's origin. |
| `lon` | decimal | degrees | Yes | Longitude. |
| `hae` | decimal | meters | Yes | Height above the WGS84 ellipsoid, bare-earth convention. |
| `ce` | decimal, always 0.0 | meters | Yes | Circular error: none, this is truth. |
| `le` | decimal, always 0.0 | meters | Yes | Linear error: none. |

The camera's `<contact>`, `<track>` and `<sensor>`:

| Element and name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `contact/callsign` | text | | Yes | The camera's name, which its stills are named after. |
| `track/course` | decimal | degrees | Yes | The camera's direction of movement since the previous capture; its boresight azimuth when it moved slower than 0.5 m/s. |
| `track/speed` | decimal | meters per second | Yes | The camera's ground speed since the previous capture. |
| `sensor/azimuth` | decimal | degrees | Yes | Boresight azimuth, clockwise from true north. |
| `sensor/elevation` | decimal | degrees | Yes | Boresight elevation: 0 level, -90 straight down. |
| `sensor/roll` | decimal | degrees | Yes | Roll about the boresight. |
| `sensor/fov` | decimal | degrees | Yes | Horizontal field of view. |
| `sensor/vfov` | decimal | degrees | Yes | Vertical field of view. |
| `sensor/range` | decimal, always 0 | meters | Yes | No range is declared. |
| `sensor/type` | text, always `EO` | | Yes | Electro-optical. |
| `sensor/model` | text | | Yes | The camera blueprint, such as `sensor.camera.rgb`. |

`<_carla_intrinsics>`: the camera's pinhole model, with square pixels and the principal point at the
picture's center.

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `width` | integer | pixels | Yes | The picture's width. |
| `height` | integer | pixels | Yes | The picture's height. |
| `fx` | decimal | pixels | Yes | Focal length across: width / (2 tan(hfov_deg / 2)). |
| `fy` | decimal | pixels | Yes | Focal length down, equal to `fx`. |
| `cx` | decimal | pixels | Yes | Principal point across: width / 2. |
| `cy` | decimal | pixels | Yes | Principal point down: height / 2. |
| `hfov_deg` | decimal | degrees | Yes | Horizontal field of view. |
| `vfov_deg` | decimal | degrees | Yes | Vertical field of view, from the height and the focal length. |
| `model` | text | | Yes | The projection model: `pinhole`. |
| `distortion` | text | | Yes | `none` at CARLA's defaults, otherwise CARLA's own lens parameters. |
| `align_offset_m` | decimal | meters | Yes | The height-align offset under the camera: its physical height is `hae` plus this. |

`<_carla_exposure>`: the exposure the camera was given. Absent for a camera that does not publish its
exposure.

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `post_process_profile` | text | | Yes | The post-process profile the camera loaded, such as `Default`. |
| `method` | word | | Yes | `manual`, a fixed exposure set by ISO, shutter and aperture, or `histogram`, metered by the engine from each frame. |
| `iso` | decimal | | Yes | The sensor's sensitivity, ISO. |
| `shutter_s` | decimal | seconds | Yes | The shutter time. |
| `fstop` | decimal | | Yes | The aperture, as an f-number. |
| `compensation_ev` | decimal | EV | Yes | Exposure compensation; above 0 brightens. |
| `ev100` | decimal | EV | No | The exposure value at ISO 100 that the ISO, shutter and aperture make, log2(fstop² / shutter_s) - log2(iso / 100); under `manual` only. |

## A vehicle's event

A vehicle's `uid` is `CARLA-TRUTH-SUMO-<sumo_id>` where a SUMO drive lent the body, and
`CARLA-TRUTH-<actor_id>` otherwise. During a SUMO drive one CARLA body draws a succession of vehicles
over a run, so always follow a vehicle by its `uid` or `sumo_id`, never by `actor_id`.

| Element and name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `track/course` | decimal | degrees | Yes | The direction the vehicle moves; the direction its body points when it moves slower than 0.5 m/s. |
| `track/speed` | decimal | meters per second | Yes | Its horizontal speed. |
| `contact/callsign` | text | | Yes | `<base_type>-<sumo_id>`, or `<base_type>-<actor_id>` where no SUMO vehicle is named. |

### `<_carla>`: the vehicle's truth record

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `source` | text, always `truth` | | Yes | Ground truth. |
| `actor_id` | integer | | Yes | The CARLA actor that drew the vehicle on this frame. |
| `type_id` | text | | Yes | The body's CARLA blueprint, such as `vehicle.audi.tt`. |
| `base_type` | text | | Yes | The vehicle's base type, such as `car`, `van` or `truck`, from the vehicle catalogue for its blueprint. |
| `special_type` | text | | Yes | The vehicle's kind from the catalogue or the blueprint; often empty. |
| `length_m` | decimal | meters | Yes | The body's bounding box, front to back. |
| `width_m` | decimal | meters | Yes | The body's bounding box, side to side. |
| `height_m` | decimal | meters | Yes | The body's bounding box, bottom to top. |
| `color` | text | | Yes | The body's color as red,green,blue, each 0 to 255; empty where the blueprint has none. |
| `role_name` | text | | Yes | The actor's role name: `sumo` for a body a SUMO drive lent. |
| `vx` | decimal | meters per second | Yes | Velocity east. |
| `vy` | decimal | meters per second | Yes | Velocity south. |
| `vz` | decimal | meters per second | Yes | Velocity up. |
| `heading_deg` | decimal | degrees | No | The direction the body points: its yaw. It differs from `track/course` while the vehicle turns or changes lane. Written wherever the body's pose was read, which is every record a recorder writes. |
| `pitch_deg` | decimal | degrees | In picture | The body's pitch, positive nose up. |
| `roll_deg` | decimal | degrees | In picture | The body's roll, positive right side down. |
| `in_frame` | word | | No | Where the vehicle's box fell against the picture (see below). Written on every record a recorder writes. |
| `occlusion` | decimal, 0 to 1 | | No | The share of the vehicle's outline hidden from the camera by anything nearer: 0 fully visible, 1 fully hidden. Only where occlusion was measured. |
| `occlusion_level` | integer, 0 to 4 | | No | The occlusion as a band: 0 nothing hidden, 1 less than 30 %, 2 from 30 % to less than 60 %, 3 from 60 % to less than 90 %, 4 90 % or more. Only where measured. |
| `occlusion_samples` | integer | | No | How many points across the outline the occlusion was measured over; few samples mean few possible values. Only where measured. |
| `apparent_width_px` | integer | pixels | No | How wide the box appears, its whole projected footprint, any part outside the picture included. Written wherever the box was projected and is not behind the camera. |
| `apparent_height_px` | integer | pixels | No | How tall the box appears; written with `apparent_width_px`. |
| `box_px` | four decimals | pixels | In picture | The upright rectangle the box's eight projected corners span: x min, y min, x max, y max. |
| `box_oriented_px` | eight decimals | pixels | In picture | The smallest rectangle around the eight projected corners, turned with the vehicle: four x y corners, clockwise from the top-most. |
| `truncation` | decimal, 0 to 1 | | In picture | The share of `box_px`'s area outside the picture. |
| `lights` | words | | In picture | The lights commanded on for the vehicle on this frame, or `none` (see below). Absent only where the container says `lights="unknown"`. |
| `pose_source` | word | | In picture | Where the drawn pose came from on this frame (see below). Only for a SUMO vehicle; absent where the container says `pose_source="unknown"`. |
| `occlusion_unmeasured` | word | | No | Why there is no occlusion (see below). Only where occlusion was not measured, so a missing fraction never reads as "nothing in the way". |
| `beyond_draw_distance` | word | | No | `partly` or `wholly`: the capture's draw distance kept the vehicle out of the image in part or entirely. Only where the draw distance reached it. |
| `camera_range_m` | decimal | meters | No | Distance from the camera to the center of the vehicle's box. Written for a vehicle in the picture and beside `beyond_draw_distance`. |
| `sumo_id` | text | | No | The SUMO vehicle this body drew on this frame; only where a SUMO drive lent the body. It joins the record to the world truth track and the supervision plan. |
| `vtype_id` | text | | No | The SUMO vehicle type; written with `sumo_id`. |
| `admitted_tick` | integer | | No | The frame on which this body began drawing this vehicle; written with `sumo_id`. |
| `sumo_angle_deg` | decimal | degrees | No | The angle SUMO reported for the vehicle on this frame, clockwise from north, for comparison with `heading_deg`; written with `sumo_id` where the recorder knows it. |

A vehicle whose box fell wholly beyond the draw distance is in the world and in the truth, but it is
not in the image. Check `beyond_draw_distance` before treating a record as something the picture shows.

### `<_box3d frame="geodetic">`: the vehicle's box in the world

Only for a vehicle in the picture. It holds exactly eight `<corner>` elements, converted to latitude,
longitude and height exactly as the event's `<point>` is, in this order: `front_left_bottom`,
`front_right_bottom`, `back_right_bottom`, `back_left_bottom`, `front_left_top`, `front_right_top`,
`back_right_top`, `back_left_top`. Corner n + 4 is above corner n. Front is the way `heading_deg` points;
left and right are as seen from the driver's seat.

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `frame` (on `_box3d`) | text, always `geodetic` | | Yes | The corners are geodetic. |
| `lat` | decimal | degrees | Yes | The corner's latitude. |
| `lon` | decimal | degrees | Yes | The corner's longitude. |
| `hae` | decimal | meters | Yes | The corner's height above the WGS84 ellipsoid, bare-earth convention. |

### `<_supervision>` and `<annotation>`: what the scenario's author asserts

Written for every SUMO vehicle a frame drew, wherever a supervision plan was in force and its state
for the frame was read. A vehicle the author says nothing about still gets one, with state
`unlabelled`.

| Element and name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `_supervision/state` | word | | Yes | `annotated`: the vehicle carries out the named pattern. `nominal`: an authored negative. `unlabelled`: no assertion, and never a negative. |
| `_supervision/vocabulary` | integer | | Yes | The annotation vocabulary's core version. |
| `_supervision/vocabulary_digest` | text, 64 hex digits | | Yes | The digest of the plan's vocabulary. |
| `annotation/instance` | text | | Yes | The pattern instance in force for the vehicle: `<scenario_id>/<name>`. |
| `annotation/labels` | terms | | No | The instance's labels, `namespace:name`, separated by spaces; absent where it has none. |
| `annotation/phase` | text | | No | The phase of the instance's interval in force; absent where none is declared. |
| `annotation/role` | text | | No | The vehicle's role in the instance, `subject` for a single-vehicle instance; absent where none is declared. |

An `annotated` or `nominal` vehicle can carry several annotations, one per instance in force. The run
manifest's `instance` and `interval_*` rows give the full plan and each interval's start and end.

## Word lists

The schema takes each of these lists from the code that writes the words.

**`in_frame`** — where the box's eight corners, projected through the camera, fell:

| Word | Meaning |
|---|---|
| `wholly` | Every corner is inside the picture. |
| `partly` | The box crosses an edge of the picture. |
| `none` | The box is wholly outside the picture. |
| `behind_camera` | A corner is at or behind the lens, so the box has no projection. |

**`occlusion_unmeasured`** — the reason nearest the vehicle where several hold:

| Word | Meaning |
|---|---|
| `behind_camera` | A corner of the box is at or behind the lens. |
| `outside_frame` | The box is wholly outside the picture. |
| `beyond_draw_distance` | The vehicle is wholly beyond the draw distance, so neither the image nor the depth image shows it. |
| `no_depth_camera` | The recorder had no depth camera, so nothing was measured for any vehicle. |
| `no_depth_capture` | The depth camera delivered no image to pair the frame with. |
| `depth_out_of_step` | Every depth image held was of another instant. |
| `depth_pose_mismatch` | The depth image of the instant was taken from another pose than the picture. |
| `beyond_depth_range` | Every sampled point lies at or beyond the depth camera's range. |
| `no_sample` | No sampling ray met the box: the vehicle is narrower than the sampling step. |

**`lights`** — `none`, or one word per light commanded on, in this order: `position`, `low_beam`,
`high_beam`, `brake`, `right_blinker`, `left_blinker`, `reverse`, `fog`, `interior`, `special1`,
`special2`. A light CARLA declares and no word names is written `bit<n>`, with its bit number. These are
the lights commanded on; whether a blueprint draws a lamp for each is up to the 3D model.

**`pose_source`**:

| Word | Meaning |
|---|---|
| `sumo` | The frame falls on a SUMO step and the position is SUMO's own. |
| `interpolated` | The frame falls between SUMO steps; the position is filled in along the lane. |
| `jump` | SUMO reported a step too far to drive in one step; the body is shown at SUMO's later position. |
| `stale` | The body could not be placed on this frame and stands where it was last drawn. |

## Joining a sidecar to the other files

- To the still: same folder, same name, `.png` instead of `.xml`. The still's `carla:capture` chunk
  carries the same `tick`.
- To the world truth track: by `sumo_id` and the frame (`tick` here, `frame` there).
- To the run manifest: by `run_id`; the manifest's `instance` and `interval_*` rows explain the
  `<annotation>`s.

## Format version

This page describes format version 1. A sidecar names its version in `format_version` on `<events>`; a
sidecar without one was written before sidecars carried it and is version 1. The sample capture of
2026-10-07, written before the version and the producer record, is valid against this schema.

A reader reads a version it knows. It refuses a newer version by name, saying the file was written by a
newer release, rather than reading it in part, because a field that silently moved is worse than one
that is missing. The CarlaControl readers and `carla-validate` follow this rule.

## Example

A sidecar with the camera platform and one vehicle in the picture, shortened from a capture of the
Arapahoe world:

```xml
<?xml version="1.0" encoding="utf-8"?>
<events format_version="1" captured="2026-10-07T17:34:51.318Z" count="1" source="truth" tick="23804" sim_time_s="200.679475" run_id="cap-20261007-173433-41f49b" vehicles="rendered" plan_id="Arapahoe_I25_SupervisionCheck" vocabulary="3" vocabulary_digest="19beb3e43bec2220772e14f501b0657e24c0902246ac6fd0e9ce28a579e0f197">
  <_producer tool="carlacontrol.CaptureSession" tool_version="0.10.0+g252f459d0" carlanet="0.10.0+g252f459d0" sumo="1.27.0" written_utc="2026-10-07T17:34:51.402Z">
    <_server available="false" release="0.10.0" world_interface="1.0" reason="the server answers no get_build_identity: it was built before the call" />
  </_producer>
  <_solar solar_time="7.45" date="2026-09-29" time_zone="-6" lat="39.5943100" lon="-104.8844900" sun_elevation_deg="5.549" sun_corrected_elevation_deg="5.694" sun_azimuth_deg="97.827" advancing="false" rate="0" illumination_band="golden" illumination_band_elevation="refraction_corrected" />
  <event version="2.0" uid="CARLA-SENSOR-107" type="a-f-A-M-F-Q" how="m-g" time="2026-10-07T17:34:51.318Z" start="2026-10-07T17:34:51.318Z" stale="2026-10-07T17:34:54.318Z">
    <point lat="39.5971345" lon="-104.8891363" hae="1818.06" ce="0.0" le="0.0" />
    <detail>
      <contact callsign="Check_Overhead_1" />
      <track course="90.0" speed="0.00" />
      <sensor azimuth="90" elevation="-70.346" roll="0" fov="50" vfov="29.395" range="0" type="EO" model="sensor.camera.rgb" />
      <_carla_intrinsics width="1920" height="1080" fx="2058.73" fy="2058.73" cx="960" cy="540" hfov_deg="50" vfov_deg="29.395" model="pinhole" distortion="none" align_offset_m="-0.63" />
      <_carla_exposure post_process_profile="Default" method="manual" iso="100" shutter_s="0.003125" fstop="4" compensation_ev="0" ev100="12.322" />
    </detail>
  </event>
  <event version="2.0" uid="CARLA-TRUTH-SUMO-yosemite_north_to_south.0" type="a-n-G-E-V" how="m-g" time="2026-10-07T17:34:51.318Z" start="2026-10-07T17:34:51.318Z" stale="2026-10-07T17:34:54.318Z">
    <point lat="39.5973969" lon="-104.8889602" hae="1735.95" ce="0.0" le="0.0" />
    <detail>
      <track course="180.4" speed="16.43" />
      <contact callsign="car-yosemite_north_to_south.0" />
      <_carla source="truth" actor_id="115" type_id="vehicle.ue4.audi.tt" base_type="car" special_type="" length_m="4.18" width_m="1.99" height_m="1.39" color="0,0,0" role_name="sumo" vx="-0.11" vy="16.43" vz="0.07" heading_deg="180.4" pitch_deg="0.24" roll_deg="0.39" in_frame="wholly" occlusion="0.000" occlusion_level="0" occlusion_samples="174" apparent_width_px="123" apparent_height_px="57" box_px="165.41 842.97 288.37 899.83" box_oriented_px="165.74 842.91 288.41 843.72 288.04 899.87 165.37 899.06" truncation="0.000" lights="none" pose_source="sumo" camera_range_m="87.9" sumo_id="yosemite_north_to_south.0" vtype_id="car.vehicle.ue4.audi.tt" admitted_tick="22475" sumo_angle_deg="180.4" />
      <_box3d frame="geodetic">
        <corner lat="39.5973780" lon="-104.8889488" hae="1735.97" />
        <corner lat="39.5973782" lon="-104.8889720" hae="1735.96" />
        <corner lat="39.5974158" lon="-104.8889717" hae="1735.94" />
        <corner lat="39.5974157" lon="-104.8889485" hae="1735.95" />
        <corner lat="39.5973781" lon="-104.8889489" hae="1737.35" />
        <corner lat="39.5973782" lon="-104.8889721" hae="1737.34" />
        <corner lat="39.5974159" lon="-104.8889718" hae="1737.32" />
        <corner lat="39.5974157" lon="-104.8889486" hae="1737.34" />
      </_box3d>
      <_supervision state="unlabelled" vocabulary="3" vocabulary_digest="19beb3e43bec2220772e14f501b0657e24c0902246ac6fd0e9ce28a579e0f197" />
    </detail>
  </event>
</events>
```

## Checking a file

`carla-validate <capture folder>` checks every sidecar of a capture against this schema, with every
other file of the capture, and lists each failure with its file and line. Any XML Schema 1.0 validator
can check a single sidecar, for example `xmllint --schema truth_sidecar.xsd <file>.xml`. The schema
cannot check which attributes go together, such as the box appearing exactly when `in_frame` is
`wholly` or `partly`; `carla-audit-sidecars` checks those rules across a capture.
