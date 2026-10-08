# World truth track

**Schema:** `CarlaControl/schemas/world_truth_track.tableschema.json` (Frictionless Table Schema)\
**Identifier:** `urn:carla-sumo-capture:schema:world-truth-track:2`\
**Format version described:** 2 (declared in the summary beside the track)

## What the file is

The world truth track is a table.\
It sits at `truth/world_truth_track.csv` in a capture folder.\
It lists every vehicle SUMO had at each sampled SUMO frame inside the capture window, whether or not any camera drew it.\
It has one row per vehicle per sample.\
Within a sample, rows are in order of `sumo_id`.

A truth sidecar lists the vehicles one frame drew.\
This file lists what the world contained.\
So counts over all the vehicles in the scene (how often a behavior happens, for example) are taken from here, not from the sidecars.\
The sidecars leave out every vehicle that had no body.

Every value but a vehicle type's base type and kind is SUMO's own, read from the state the run already holds.\
A vehicle's drawn pose and box are in the sidecars.\
Join a row to a sidecar's vehicle event by `sumo_id` and frame (this file's `frame`, the sidecar's `tick`).

The file is plain CSV, UTF-8, with a header line and LF line endings.\
A field holding a comma, such as `color`, is quoted.\
An empty field is a missing value.

## Who writes it and when

If its caller names a path, the SUMO drive session in CarlaNet writes it.\
`carla-capture` always does, at `truth/world_truth_track.csv` under the capture folder.\
`carla-drive --world-truth-track <path>` writes one too.\
With `--world-truth-track-interval <seconds>`, the track samples every few SUMO steps instead of every one.

The header is written as the track opens.\
When the CARLA frame that renders a sampled SUMO frame completes, the sample's rows are written and flushed together.\
Frames before the capture window opens write nothing.

A file cut off by a killed run holds the rows already written.\
After them comes at most one line without its line break.\
A reader ignores that line.

## Columns

Every row has all 41 columns, in this order.\
In the Required column, "Yes" means the cell is never empty.

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `time_utc` | date-time | | No | The row's instant as a UTC time, from the scenario's epoch: simulated civil time, not the wall clock. The epoch is the civil time that simulated second zero stands for. Empty where the run declared no epoch. |
| `sim_time_s` | number | seconds | Yes | Simulated time of the SUMO frame the row describes, on TraCI's clock. |
| `uid` | string | | Yes | `CARLA-TRUTH-SUMO-<sumo_id>`: the same uid a sidecar gives the vehicle. |
| `callsign` | string | | Yes | `<base_type>-<sumo_id>`, as in a sidecar. |
| `cot_type` | string, always `a-n-G-E-V` | | Yes | The Cursor-on-Target type: a ground vehicle. |
| `how` | string, always `m-g` | | Yes | Machine generated. |
| `lat` | number | degrees | Yes | Latitude, WGS84, of SUMO's position for the vehicle: the middle of its front bumper. |
| `lon` | number | degrees | Yes | Longitude of the same point. |
| `hae_m` | number | meters | No | Height above the WGS84 ellipsoid of the bare-earth ground under that point. Empty where the vehicle is off the world's ground grid. |
| `ce_m` | number, always 0 | meters | Yes | Circular error: none, this is truth. |
| `le_m` | number, always 0 | meters | Yes | Linear error: none. |
| `course_deg` | number | degrees | Yes | SUMO's heading for the vehicle, clockwise from true north. |
| `speed_mps` | number | meters per second | Yes | SUMO's speed for the vehicle. |
| `vx` | number | meters per second | Yes | Velocity east, from SUMO's speed and heading, in CARLA's frame (x east, y south). |
| `vy` | number | meters per second | Yes | Velocity south. |
| `vz` | number, always 0 | meters per second | Yes | Velocity up: SUMO moves vehicles on the plane. |
| `base_type` | string | | Yes | The vehicle's base type, such as `car`, `van` or `truck`: the vehicle catalog's for the blueprint its SUMO type names, or the one its SUMO vehicle class maps to. |
| `type_id` | string | | Yes | The SUMO vehicle type. A sidecar calls this `vtype_id`. The sidecar's own `type_id` is the CARLA blueprint. |
| `special_type` | string | | No | The vehicle's kind from the catalog. Empty where it has none. |
| `length_m` | number | meters | Yes | The length the SUMO type declares. SUMO's car-following used this length. |
| `width_m` | number | meters | Yes | The width the SUMO type declares. |
| `height_m` | number | meters | Yes | The height the SUMO type declares. |
| `color` | string | | Yes | The SUMO type's color as red,green,blue, each 0 to 255. |
| `role_name` | string | | Yes | The SUMO flow the vehicle came from: its id up to the last dot, or the whole id where it has none. |
| `edge` | string | | Yes | The SUMO edge the vehicle was on. |
| `lane` | string | | No | The SUMO lane the vehicle was on. Empty where it was on none, as a vehicle parked off the road is. |
| `sumo_x` | number | meters | Yes | Position east in SUMO's network coordinates. |
| `sumo_y` | number | meters | Yes | Position north in SUMO's network coordinates. |
| `carla_x` | number | meters | Yes | Position east in CARLA's frame: `sumo_x`. |
| `carla_y` | number | meters | Yes | Position south in CARLA's frame: minus `sumo_y`. |
| `sumo_id` | string | | Yes | The SUMO vehicle: the key that joins the row to the sidecars and the supervision plan. |
| `entity_id` | string | | Yes | The vehicle's entity id: its SUMO id. |
| `frame` | integer | | Yes | The CARLA frame that rendered this SUMO frame. |
| `render_state` | string | | Yes | `rendered` where a body drew the vehicle on the frame. `simulated_only` where none did. |
| `render_reason` | string | | No | Why no body drew the vehicle (below). Empty where it was rendered. |
| `actor_id` | integer | | No | The CARLA body that drew the vehicle. Empty where none did. |
| `in_window` | true or false, always 1 | | Yes | The row is inside the capture window, written `1`. |
| `sun_elevation_deg` | number | degrees | No | The sun's geometric elevation the world reported on the frame's tick. Empty where it reported none. |
| `sun_corrected_elevation_deg` | number | degrees | No | The sun's refraction-corrected elevation, where the world reports it. |
| `illumination_band` | string | | No | The sun's illumination band, cut as a still's is. Empty where there is no sun. |
| `illumination_band_elevation` | string | | No | Which elevation the band was cut from: `refraction_corrected` wherever the reading carries it, `geometric` otherwise. |

The table's primary key is `(sumo_id, frame)`: no vehicle appears twice in one sample.

**`render_reason`**, the first that holds:

| Word | Meaning |
|---|---|
| `no_world` | The run renders no world. |
| `outside_limit` | An optional render-set limit left it no body. |
| `no_blueprint` | Its type names no CARLA blueprint. |
| `unknown_extent` | Its type's blueprint has no measured size. |
| `no_ground` | It is off the world's ground grid, where no body can be placed. |
| `not_drawn` | None of those: the frame drew no body for it, for a reason the track cannot name. |

The annotation vocabulary also has `left_the_simulation` and `vanished`.\
This track never writes them: a vehicle's last SUMO frame is drawn like any other.

**Illumination bands**, cut by the sun's refraction-corrected elevation in degrees:

- `day` above 6
- `golden` above 0
- `civil_twilight` above -6
- `nautical_twilight` above -12
- `astronomical_twilight` above -18
- `night` at -18 and below

Each band includes its upper edge.

**Two things differ from a sidecar.**\
The track's dimensions and color are the SUMO type's.\
The sidecar's are the CARLA body's.\
So the two can differ slightly for the same vehicle.\
The track's position is also SUMO's, at the front bumper.\
The sidecar's `<point>` is the CARLA body's origin instead.

## Format version

The CSV carries no version of its own.\
This keeps its header to the column names alone.\
Its version is the summary's `world_truth_track_version` (see the World truth track summary page).\
This page describes version 2.

A summary without that field is version 1, an older shape with fewer columns that this schema does not describe.\
Readers read a version they know and refuse a newer one by name rather than reading it in part.

## Example

```
time_utc,sim_time_s,uid,callsign,cot_type,how,lat,lon,hae_m,ce_m,le_m,course_deg,speed_mps,vx,vy,vz,base_type,type_id,special_type,length_m,width_m,height_m,color,role_name,edge,lane,sumo_x,sumo_y,carla_x,carla_y,sumo_id,entity_id,frame,render_state,render_reason,actor_id,in_window,sun_elevation_deg,sun_corrected_elevation_deg,illumination_band,illumination_band_elevation
2026-09-29T13:27:00.000Z,60,CARLA-TRUTH-SUMO-arapahoe_east_to_west.0,van-arapahoe_east_to_west.0,a-n-G-E-V,m-g,39.5951904,-104.8806465,1748.60,0.0,0.0,320.3,0.00,0.00,0.00,0.00,van,van.vehicle.sprinter.mercedes,,5.92,1.98,2.73,"230,230,230",arapahoe_east_to_west,427819530#0,427819530#0_1,330.23,97.79,330.23,-97.79,arapahoe_east_to_west.0,arapahoe_east_to_west.0,23674,rendered,,109,1,5.549088,5.694,golden,refraction_corrected
```

## Checking a file

`carla-validate <capture folder>` checks every row against this schema and the primary key.\
It also checks that the summary written at the run's end counts the rows the track holds.\
Frictionless tools can also read the Table Schema directly.\
The schema adds two annotations, `x-unit` and `x-format-version`, that such tools ignore.
