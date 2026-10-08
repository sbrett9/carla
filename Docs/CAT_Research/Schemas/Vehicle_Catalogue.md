# Vehicle catalogue (`vehicles.catalogue.json`)

The vehicle catalogue lists every vehicle body CARLA can draw, measured, and groups the bodies into
the classes a scenario asks for. It is the one source of three things:

- **what kind of vehicle each body is.** The truth records' `base_type` and `special_type` come from the
  catalogue's classes, not from what the content declares and not from SUMO's vehicle class;
- **how big each body is.** SUMO reserves road space for the measured body, so the vehicle SUMO drives
  and the body CARLA draws are the same size;
- **how SUMO drives each class.** Every class states its acceleration, braking, speed and gap
  explicitly.

It lives at `CarlaControl/catalogue/vehicles.catalogue.json` in a checkout, and an installed
`carlacontrol` carries a copy. SUMO's view of the same bodies is [`vehicles.vtypes.rou.xml`](Vehicle_Types.md).
The body widths without mirrors come from [`vehicle_body_widths.json`](Vehicle_Body_Widths.md).

- Schema: `CarlaControl/schemas/vehicle_catalogue.schema.json`
- Schema id: `urn:carla-sumo-capture:schema:vehicle-catalogue:1`

## Who writes it and who reads it

`CarlaControl/scripts/make_vehicle_catalogue.py` (`VehicleCatalogueBuilder`) writes it against a
running CARLA server. It spawns each vehicle blueprint alone, high above the map, reads its bounding
box, and destroys it. It spawns each again with a color set and reads the server log to see whether
the color reached the body. It renders each with every lamp commanded on and off and counts the pixels
that change. It merges the body widths, checks the whole document, computes its digest, and writes the
catalogue and the SUMO vehicle types. `CarlaControl/scripts/apply_vehicle_body_widths.py` merges a new
body-width table into the existing catalogue without a server.

These read it:

- the scenario compiler, which draws each vehicle's type from a class and records the catalogue's
  digest in the compiled scenario;
- a capture run, which refuses a scenario compiled against another catalogue;
- the SUMO drive (CarlaNet's `VehicleCatalogue`), which places each SUMO vehicle's body from its
  measured box;
- the truth recorder, which takes each vehicle's `base_type` and `special_type` from it;
- `carla-cot-telemetry`, for the same two fields.

The file is JSON in one canonical form: keys sorted, two-space indent, UTF-8, a final newline. The
digest is defined over that form.

## The classes

A scenario asks for a class. SUMO draws a member body from it, with the member's weight as its chance.
Every member of a class has the class's `base_type` and `special_type` in the truth, and SUMO drives it
with the class's parameters. Each body belongs to exactly one class.

| Class | Description | SUMO vClass | `base_type` | `special_type` | Bodies |
|---|---|---|---|---|---|
| `civ_car` | Ordinary civilian passenger cars. The default for background traffic. | passenger | car | (empty) | 10 |
| `offroad` | Short-wheelbase four-wheel-drive utility vehicles: an open-topped jeep. | passenger | car | (empty) | 1 |
| `civ_van` | Panel vans and light commercial bodies on a van chassis. | delivery | van | (empty) | 1 |
| `civ_truck` | A two-axle box truck. | truck | truck | (empty) | 1 |
| `heavy_truck` | A three-axle cab-over heavy truck, with no trailer. | truck | truck | (empty) | 1 |
| `bus` | A light bus the size of a truck. | bus | bus | (empty) | 1 |
| `taxi` | Licensed taxis, the same body as a civilian sedan. | taxi | car | taxi | 1 |
| `police` | Marked police cars. | authority | car | emergency | 1 |
| `ambulance` | Ambulances on a van chassis. | emergency | van | emergency | 1 |
| `fire_appliance` | Fire trucks. The largest emergency body. | emergency | truck | emergency | 1 |

The descriptions for `civ_truck`, `heavy_truck`, `bus`, `taxi` and `fire_appliance` are paraphrased
here; the catalogue holds the original wording.

How SUMO drives each class:

| Class | maxSpeed (m/s) | accel (m/s²) | decel (m/s²) | sigma | speedFactor | speedDev | minGap (m) |
|---|---|---|---|---|---|---|---|
| `civ_car` | 35 | 2.6 | 4.5 | 0.5 | 1 | 0.1 | 2.5 |
| `offroad` | 35 | 2.6 | 4.5 | 0.5 | 1 | 0.1 | 2.5 |
| `civ_van` | 30 | 2.6 | 4.5 | 0.5 | 1 | 0.05 | 2.5 |
| `civ_truck` | 25 | 1.3 | 4.0 | 0.5 | 1 | 0.05 | 3.0 |
| `heavy_truck` | 25 | 1.3 | 4.0 | 0.5 | 1 | 0.05 | 3.0 |
| `bus` | 25 | 1.2 | 4.0 | 0.5 | 1 | 0.05 | 3.0 |
| `taxi` | 35 | 2.6 | 4.5 | 0.5 | 1 | 0.05 | 2.5 |
| `police` | 40 | 2.6 | 4.5 | 0.4 | 1 | 0.05 | 2.5 |
| `ambulance` | 35 | 2.6 | 4.5 | 0.4 | 1 | 0.05 | 2.5 |
| `fire_appliance` | 25 | 1.3 | 4.0 | 0.4 | 1 | 0.05 | 3.0 |

The builder takes these from SUMO's own defaults for each vehicle class and writes them out, so none
is left implicit. The top speeds are set for a mixed city and freeway network instead, and the bus
takes a speed spread of 0.05 where SUMO's default is zero.

### How a body's kind is decided

The builder derives a base type from the measured box, then applies a short list of curated
corrections:

- height under 2.0 m: `car`;
- height under 3.0 m and length under 7.0 m: `van`;
- otherwise: `truck`. The derivation never gives `bus`.

The corrections are for what a box cannot show: the Nissan Patrol is a sport utility vehicle on a car
chassis whose 2.06 m roof reads as a van; the Fuso Rosa is a bus; the ambulance, the police car and
the fire truck are `emergency`; the taxi is `taxi`. Each correction carries its reason in the builder's
code.

## The bodies

| Blueprint | Class | Length (m) | Width with mirrors (m) | Body width (m) | Height (m) |
|---|---|---|---|---|---|
| `vehicle.ambulance.ford` | `ambulance` | 6.36 | 2.35 | 2.29 | 2.43 |
| `vehicle.carlacola.actors` | `civ_truck` | 8.00 | 2.91 | 2.79 | 4.05 |
| `vehicle.carlamotors.european_hgv` | `heavy_truck` | 7.92 | 2.86 | 2.79 | 3.78 |
| `vehicle.dodge.charger` | `civ_car` | 5.01 | 1.88 | 1.85 | 1.54 |
| `vehicle.dodgecop.charger` | `police` | 5.24 | 1.92 | 1.85 | 1.64 |
| `vehicle.firetruck.actors` | `fire_appliance` | 8.58 | 2.90 | 2.90 | 3.83 |
| `vehicle.fuso.mitsubishi` | `bus` | 10.17 | 3.93 | 3.23 | 4.24 |
| `vehicle.jeep.wrangler_rubicon` | `offroad` | 3.87 | 1.91 | 1.86 | 1.88 |
| `vehicle.lincoln.mkz` | `civ_car` | 4.89 | 1.84 | 1.83 | 1.52 |
| `vehicle.mini.cooper` | `civ_car` | 4.55 | 2.10 | 2.09 | 1.77 |
| `vehicle.nissan.patrol` | `civ_car` | 5.59 | 2.15 | 2.15 | 2.06 |
| `vehicle.sprinter.mercedes` | `civ_van` | 5.92 | 1.99 | 1.98 | 2.73 |
| `vehicle.taxi.ford` | `taxi` | 5.35 | 1.79 | 1.78 | 1.58 |
| `vehicle.ue4.audi.tt` | `civ_car` | 4.18 | 1.99 | 1.97 | 1.39 |
| `vehicle.ue4.bmw.grantourer` | `civ_car` | 4.61 | 2.24 | 2.14 | 1.67 |
| `vehicle.ue4.chevrolet.impala` | `civ_car` | 5.36 | 2.03 | 1.78 | 1.41 |
| `vehicle.ue4.ford.crown` | `civ_car` | 5.37 | 1.80 | 1.78 | 1.57 |
| `vehicle.ue4.ford.mustang` | `civ_car` | 4.72 | 1.89 | 1.84 | 1.30 |
| `vehicle.ue4.mercedes.ccc` | `civ_car` | 4.67 | 1.81 | 1.80 | 1.44 |

## How the catalogue meets the truth

For a model developer reading a capture's truth:

- A vehicle's `base_type` and `special_type` are its class's `cot_base_type` and `cot_special_type`.
  An empty `special_type` is the normal case and means "no special type". A planted vehicle has the
  same kind as any other vehicle of its body; which vehicles were planted is recorded separately.
- A vehicle's `length_m`, `width_m` and `height_m` in the truth are the bounding box CARLA measures
  on the drawn body. They match the catalogue's `length_m`, `width_m` and `height_m`, so the width
  includes the mirrors. SUMO was given `body_width_m`, the width without them.
- A vehicle's `type_id` in the truth is its CARLA blueprint id. In a SUMO-driven capture, `vtype_id`
  is the SUMO type, which for a compiled scenario names the blueprint, and the type's
  `carla:class_id` parameter names the class.
- SUMO reports a vehicle's position at the center of its front bumper. CARLA places the body's origin
  behind it by `length_m / 2 + bbox_centre_m[0]` along the heading. The truth's latitude, longitude and
  height are the body origin's.
- The lights the truth records are the lights commanded on. `lamp_capability` says whether commanding
  a lamp changed the image when the catalogue was built. In this catalogue the optical pass found
  only one lamp that changes the image, the fire truck's high beams; every other lamp is `unlit`.

## Fields

### Header

| Field | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `catalogue_version` | integer, always 1 | | yes | The format of this file. |
| `catalogue_id` | string | | yes | The catalogue's name: `carla-<server version>-<os>` unless the builder was given one. |
| `catalogue_digest` | string | | yes | SHA-256, lowercase hexadecimal, of the canonical file with this field empty. Compiled scenarios record it. |
| `content_build_id` | string | | yes | Which content build was measured. Defaults to `catalogue_id`. |
| `blueprint_set_digest` | string | | yes | SHA-256 over every blueprint and attribute the server declared. A server can recompute it to show its content has not changed. |
| `generated_at_utc` | string | | yes | When the sweep ran, ISO 8601 UTC to the millisecond. |
| `generator` | string | | yes | The tool and its version: `carlacontrol.VehicleCatalogueBuilder/1.0.0`. |
| `server_version` | string | | yes | The CARLA server's version. |
| `producer` | object | | no | What made the catalogue, as in [World_Package_Manifest.md](World_Package_Manifest.md#the-producer-record). Absent from catalogues built before it was recorded. |
| `lamp_probe` | object | | yes | The conditions of the lamp pass, below. |
| `body_width` | object | | no | How the body widths were measured: `method`, `measured` (when and from what) and `source` (the table's file name). Present whenever body widths are. |
| `vehicles` | array of objects | | yes | One entry per vehicle blueprint the server offered, in blueprint id order. |
| `classes` | array of objects | | yes | The classes. |

### `vehicles[]`

Every entry has these fields, measured or not:

| Field | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `blueprint_id` | string | | yes | The CARLA blueprint id, such as `vehicle.lincoln.mkz`. |
| `uid` | integer | | yes | The blueprint's numeric id on the measured server. |
| `tags` | string | | yes | The blueprint's tags, comma separated. |
| `declared_base_type` | string | | yes | The base type the content declares. Truth does not use it. |
| `declared_special_type` | string | | yes | The special type the content declares. Truth does not use it. |
| `number_of_wheels` | integer | | yes | Wheels the blueprint declares; −1 when it declares none. |
| `generation` | integer | | yes | The CARLA generation the blueprint declares; −1 when none. |
| `declared_has_lights` | boolean | | yes | Whether the blueprint declares lights. |
| `settable_attributes` | array of objects | | yes | Each attribute a spawn may set: `id`, `type`, `restrict_to_recommended` and `recommended_values`. |
| `colour_settable` | boolean | | yes | Whether the blueprint has a color attribute. |
| `colour_palette` | array of strings | | yes | The body colors it recommends, each `R,G,B` from 0 to 255. A rendered body takes its color from here, never from SUMO. |
| `colour_applied` | string | | yes | `true` when a requested color reached the body, `false` when it did not, `unknown` when the server log could not say. |
| `lamp_capability` | object | | yes | For each of the eleven lamps (`position`, `low_beam`, `high_beam`, `brake`, `right_blinker`, `left_blinker`, `reverse`, `fog`, `interior`, `special_1`, `special_2`): `lit`, the image changed; `unlit`, the lamp was commanded and the image did not change; `unknown`, not measured. |
| `measurement` | string | | yes | `measured` or `failed`. |

A measured entry also has:

| Field | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `length_m` | number | meters | yes | Length of the spawned body's bounding box, front to back. |
| `width_m` | number | meters | yes | Width of the bounding box, mirrors included. |
| `height_m` | number | meters | yes | Height of the bounding box. |
| `bbox_centre_m` | array of 3 numbers | meters | yes | The box's center in the vehicle's own frame: forward, right, up from the actor's origin. |
| `body_width_m` | number | meters | no | Width of the body without mirrors, measured from the mesh. SUMO is given this width. Every measured entry has it in the current catalogue; without it, no SUMO type can be written for the body. |

A failed entry has `measurement_note`, the reason, instead, and no dimensions. A reader refuses to
place a body whose measurement failed.

### `classes[]`

| Field | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `class_id` | string | | yes | The class's id: a lowercase letter, then up to 31 lowercase letters, digits or underscores. |
| `description` | string | | yes | The class in words. |
| `sumo_vclass` | string | | yes | SUMO's vehicle class, which decides the lanes the vehicle may use. |
| `cot_base_type` | string | | yes | The `base_type` truth records for every member: `car`, `van`, `truck`, `bus`, `motorcycle` or `bicycle`. |
| `cot_special_type` | string | | no | The `special_type` truth records: `emergency`, `taxi`, `electric`, or empty. Absent means empty. |
| `members` | array of objects | | yes | The bodies the class draws: `blueprint_id`, and `weight`, its relative chance, above 0. |
| `max_speed_mps` | number | m/s | yes | SUMO `maxSpeed`: the top speed. |
| `accel_mps2` | number | m/s² | yes | SUMO `accel`: the most the vehicle accelerates. |
| `decel_mps2` | number | m/s² | yes | SUMO `decel`: how hard it brakes when it wants to. |
| `sigma` | number | | yes | SUMO `sigma`: the driver's imperfection in the Krauss car-following model, 0 (perfect) to 1. |
| `speed_factor_mean` | number | | yes | SUMO `speedFactor`: the mean multiple of the speed limit the driver keeps. |
| `speed_factor_dev` | number | | yes | SUMO `speedDev`: how much that multiple varies between drivers. |
| `min_gap_m` | number | meters | yes | SUMO `minGap`: the gap left to the vehicle ahead when stopped. |
| `gui_shape` | string | | yes | SUMO `guiShape`. Read by sumo-gui only. |
| `gui_colour` | string | | yes | The `#RRGGBB` color sumo-gui draws the class in. It never reaches the rendered body. |
| `render_colour_policy` | string | | yes | How a rendered body's color is chosen: `palette`, from the body's `colour_palette`. Nothing reads it yet. |

### `lamp_probe`

| Field | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `ran` | boolean | | yes | Whether the pass ran. When it did not, every lamp is `unknown`. |
| `reason` | string | | no | Why it did not run, or stopped. |
| `solar_date`, `solar_time_hours` | string, number | date, hours | no | The date and solar time the sun was set to, at night so lamps stand out. |
| `sun_elevation_deg` | number or null | degrees | no | The sun's elevation the server reported. |
| `positive_control_pixels` | integer | pixels | no | Pixels that changed when the sun was moved to daylight, which shows the camera sees a change. |
| `camera_poses` | array of objects | meters, degrees | no | Where the probe camera stood: `standoff_m`, `height_offset_m`, `pitch_deg`, `yaw_deg`, `fov_deg`. |
| `image_size` | array of 2 integers | pixels | no | Probe image width and height. |
| `luminance_threshold` | integer | of 255 | no | A pixel counts as gained above this brightness. |
| `region` | array of 4 numbers | fraction | no | The part of the image counted. |
| `minimum_lit_pixels` | integer | pixels | no | A lamp needs more gained pixels than this to be lit. |
| `drift_margin` | number | | no | A lamp also needs more than this many times the largest difference between two images of the same state. |
| `average_frames` | integer | | no | Frames averaged per image. |

## Format version

`catalogue_version` is 1, and there is no other version. Every catalogue the builder wrote carries it,
so a file without it is not a catalogue.

- carlacontrol's `VehicleCatalogue` and CarlaNet's `VehicleCatalogue` read only a catalogue that
  declares 1. They refuse any other value, or none. For a newer version the message says the file was
  written by a newer release.
- `VehicleCatalogue.load` then checks the file against the schema and refuses it, naming each problem.
  Every vehicle and class entry must be whole. A header field that is present must have the right
  shape; one that is absent reads as empty.

`carlacontrol.WorldFileValidator`, given the catalogue's folder, checks the whole catalogue against
its schema and the builder's rules, checks that its digest is its content's, checks
`vehicles.vtypes.rou.xml` against `vehicle_types.xsd` and against the types the catalogue gives, and
checks `vehicle_body_widths.json` where it is there. `carla-validate CarlaControl/catalogue` runs it.

## Example

Trimmed to one body and one class:

```json
{
  "blueprint_set_digest": "a51cfaacc5ceb15351f445575e8d3208e47dfe5c4c239c9805717be6cc9770b4",
  "body_width": {"measured": "2026-10-02, UE 5.7.4 editor via VibeUE, the CARLA content as loaded",
                 "method": "LOD0 vertices of the vehicle's own skeletal mesh ...",
                 "source": "vehicle_body_widths.json"},
  "catalogue_digest": "6037e3bb2bde6f45de45e31925236593d16653989293fe414d9060a78bfbe90d",
  "catalogue_id": "carla-0.10.0-windows",
  "catalogue_version": 1,
  "classes": [
    {"accel_mps2": 2.6, "class_id": "ambulance", "cot_base_type": "van",
     "cot_special_type": "emergency", "decel_mps2": 4.5,
     "description": "Ambulances on a van chassis.", "gui_colour": "#D94F4F",
     "gui_shape": "emergency", "max_speed_mps": 35.0,
     "members": [{"blueprint_id": "vehicle.ambulance.ford", "weight": 1.0}],
     "min_gap_m": 2.5, "render_colour_policy": "palette", "sigma": 0.4,
     "speed_factor_dev": 0.05, "speed_factor_mean": 1.0, "sumo_vclass": "emergency"}
  ],
  "content_build_id": "carla-0.10.0-windows",
  "generated_at_utc": "2026-09-29T19:13:25.921Z",
  "generator": "carlacontrol.VehicleCatalogueBuilder/1.0.0",
  "lamp_probe": {"average_frames": 5, "drift_margin": 4.0, "image_size": [480, 360],
                 "luminance_threshold": 24, "minimum_lit_pixels": 8,
                 "positive_control_pixels": 157082, "ran": true, "region": [0.0, 0.0, 1.0, 1.0],
                 "solar_date": "2026-03-21", "solar_time_hours": 1.0,
                 "sun_elevation_deg": -59.88318634033203,
                 "camera_poses": [{"fov_deg": 60.0, "height_offset_m": -2.5, "pitch_deg": 22.0,
                                   "standoff_m": 4.0, "yaw_deg": 180.0}]},
  "server_version": "0.10.0",
  "vehicles": [
    {"bbox_centre_m": [-0.2929, 0.0019, 1.2158], "blueprint_id": "vehicle.ambulance.ford",
     "body_width_m": 2.2856, "colour_applied": "true", "colour_palette": ["255,255,255"],
     "colour_settable": true, "declared_base_type": "van", "declared_has_lights": true,
     "declared_special_type": "emergency", "generation": 3, "height_m": 2.431,
     "lamp_capability": {"brake": "unlit", "fog": "unlit", "high_beam": "unlit", "interior": "unlit",
                         "left_blinker": "unlit", "low_beam": "unlit", "position": "unlit",
                         "reverse": "unlit", "right_blinker": "unlit", "special_1": "unlit",
                         "special_2": "unlit"},
     "length_m": 6.3569, "measurement": "measured", "number_of_wheels": 4,
     "settable_attributes": [{"id": "color", "recommended_values": ["255,255,255"],
                              "restrict_to_recommended": false, "type": "RGBColor"}],
     "tags": "vehicle,ambulance,ford", "uid": 6, "width_m": 2.3512}
  ]
}
```
