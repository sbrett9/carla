# The vehicles behind the truth

This page is for people who build pattern-of-life models from our captures. Every vehicle in a capture
is drawn with one of a small set of measured CARLA bodies, and the vehicle catalogue lists them. This
page explains what the vehicle fields of a truth record mean in terms of those bodies. Every field of the
catalogue is described on the schema page [Vehicle catalogue](../Schemas/Vehicle_Catalogue.md). The
files are described on [What a capture folder holds](Capture_Folder.md), and the labels on
[Behavioral annotations](Behavioral_Annotations.md).

**We label; we never score.** The catalogue records measurements of each body. It says nothing about
how well a body suits a behavior or a model.

## The catalogue

The catalogue is `CarlaControl/catalogue/vehicles.catalogue.json`. The current one is
`carla-0.10.0-windows`, with the digest
`6037e3bb2bde6f45de45e31925236593d16653989293fe414d9060a78bfbe90d`. A run records the digest of the
catalogue it used on the manifest's first row, as `scenario.catalogue_digest`. The sample capture
`cap-20261008-041347-270d6d` used this one. `carla-validate CarlaControl/catalogue` checks the catalogue
against its schema.

## The classes and their bodies

The catalogue holds 19 bodies, each named by its CARLA blueprint, and groups them into 10 classes. Each
body belongs to exactly one class.

| Class | `base_type` | `special_type` | Bodies |
|---|---|---|---|
| `civ_car` | `car` | (empty) | `vehicle.dodge.charger`, `vehicle.lincoln.mkz`, `vehicle.mini.cooper`, `vehicle.nissan.patrol`, `vehicle.ue4.audi.tt`, `vehicle.ue4.bmw.grantourer`, `vehicle.ue4.chevrolet.impala`, `vehicle.ue4.ford.crown`, `vehicle.ue4.ford.mustang`, `vehicle.ue4.mercedes.ccc` |
| `offroad` | `car` | (empty) | `vehicle.jeep.wrangler_rubicon` |
| `civ_van` | `van` | (empty) | `vehicle.sprinter.mercedes` |
| `civ_truck` | `truck` | (empty) | `vehicle.carlacola.actors` |
| `heavy_truck` | `truck` | (empty) | `vehicle.carlamotors.european_hgv` |
| `bus` | `bus` | (empty) | `vehicle.fuso.mitsubishi` |
| `taxi` | `car` | `taxi` | `vehicle.taxi.ford` |
| `police` | `car` | `emergency` | `vehicle.dodgecop.charger` |
| `ambulance` | `van` | `emergency` | `vehicle.ambulance.ford` |
| `fire_appliance` | `truck` | `emergency` | `vehicle.firetruck.actors` |

Each body's measured size is listed on the schema page, under
[The bodies](../Schemas/Vehicle_Catalogue.md#the-bodies).

### What `base_type` and `special_type` tell you

- A truth record's `base_type` and `special_type` are the catalogue class's, for the body that drew the
  vehicle. They do not come from what the CARLA content declares about itself, from SUMO's vehicle
  class, or from the names a scenario gives its own classes.
- An empty `special_type` is the usual case. It means the body has no special type.
- `base_type` never says whether a vehicle was planted by the scenario's author. A planted car has the
  same `base_type` as every other car of its body.

### A scenario's own classes

A scenario defines its own vehicle classes, each drawing bodies from the catalogue with its own
driving. The Arapahoe supervision check has `car_quick`, `car`, `suv`, `van`, `truck` and `semi`.
`car_quick` and `car` draw the same nine cars and differ only in how they drive, so you cannot tell
them apart in a picture.

The scenario's class is the first part of the SUMO vehicle type: `vtype_id` in a sidecar and `type_id`
in the world truth track. A compiled scenario names each type `<class>.<blueprint>`, such as
`suv.vehicle.nissan.patrol`. The truth still gives that vehicle the catalogue's kind. In the sample
capture, a record with `vtype_id="suv.vehicle.nissan.patrol"` has `base_type="car"`.

**Names can state the author's intent.** A scenario's class names, its vehicle ids and its flow ids are
written by its author, and they can say what a vehicle is for. In the Shahid Bahonar Port scenario the
guard vehicles are of type `guard.vehicle.jeep.wrangler_rubicon`, and the guard that misses its post is
`offpost_d4_h7_t3`. These names are for joining truth to truth. Keep them out of anything your model
sees.

**The bodies are few.** Most classes hold one body, so every vehicle of such a class looks alike. In
the Arapahoe check, every `suv` is the same Nissan Patrol.

## Body size

- **In a sidecar**, `length_m`, `width_m` and `height_m` are the body's measured bounding box, mirrors
  included: the catalogue's `length_m`, `width_m` and `height_m`, rounded to the centimeter. For the
  Ford Mustang the catalogue holds 4.7175 × 1.8948 × 1.3009 m, and the sidecar says 4.72 × 1.89 ×
  1.30. The record's boxes, `box_px`, `box_oriented_px` and `<_box3d>`, are this box placed at the
  body's pose.
- **In the world truth track**, they are the SUMO vehicle type's. Length and height are the same. The
  width is `body_width_m`, the body without its mirrors, which is the width SUMO keeps clear on the
  road. For the Mustang the track says 4.72 × 1.84 × 1.30.
- **Where the position is.** SUMO places a vehicle by the middle of its front bumper. CARLA places a
  body by its origin, and `bbox_centre_m` gives the box's center from that origin: forward, right, up.
  So SUMO's point lies `length_m / 2 + bbox_centre_m[0]` ahead of the body's origin, along the way the
  body points. For the Mustang that is 4.7175 / 2 + 0.0321 = 2.39 m. In the sample capture, the track's
  point and the sidecar's `<point>` for the parked `dweller`, a Mustang, are 2.39 m apart.
- **A class can be drawn smaller or larger than its name suggests.** The content has no articulated
  truck, so the Arapahoe check's `semi` class is drawn as `vehicle.carlamotors.european_hgv`, 7.92 m
  long. The scenario specification's notes on each class say so.

## Lights

A sidecar's `lights` lists the lights commanded on for a vehicle on that frame. Whether the picture
shows a lit lamp depends on the body's 3D model.

The catalogue's `lamp_capability` records, for each body and each of eleven lamps, whether commanding
the lamp changed the picture when the catalogue was built: `lit` if it did, `unlit` if it was commanded
and the picture did not change, `unknown` if it was not measured. The pass ran with the sun set far below
the horizon (`lamp_probe.sun_elevation_deg` -59.88), and first checked that its camera could see a
change (`lamp_probe.positive_control_pixels` 157082).

In the current catalogue, one lamp is `lit`: the `high_beam` of `vehicle.firetruck.actors`. Every other
lamp of every body is `unlit`. So with this catalogue, a `brake` or a `right_blinker` in the truth means
the lamp was commanded. The picture is not expected to show it lit.

The lamp names match between the two files except two. The sidecar writes `special1` and `special2`;
the catalogue writes `special_1` and `special_2`.

## Color

- **In a sidecar**, `color` is the body's color attribute, as red,green,blue from 0 to 255. It is empty
  for a body with no color attribute: in this catalogue, `vehicle.carlamotors.european_hgv` and
  `vehicle.jeep.wrangler_rubicon` (`colour_settable` is `false`).
- **Every body of one blueprint has the same color.** Each body is created with its blueprint's own
  color attribute, unchanged. In the sample capture every Ford Mustang is `0,0,0`, every Nissan Patrol
  `2,35,54` and every Mercedes Sprinter `233,234,236`: for each body, the first entry of its
  `colour_palette`. So color does not tell two vehicles of one body apart. The class's
  `render_colour_policy`, `palette`, is declared, but nothing reads it yet.
- **The color attribute may not be the paint.** Where the catalogue says `colour_applied` is `false`,
  setting a color did not reach the body when the catalogue was built. For those bodies the recorded
  `color` may not be what the picture shows. In this catalogue they are `vehicle.dodgecop.charger`,
  `vehicle.fuso.mitsubishi`, `vehicle.lincoln.mkz`, `vehicle.mini.cooper` and
  `vehicle.sprinter.mercedes`.
- **In the world truth track**, `color` is the color sumo-gui draws the SUMO vehicle type in: the
  scenario class's `gui_colour`. It never reaches the picture. The Arapahoe check's `car` class is
  `#B3B8C7`, so every one of its cars is `179,184,199` in the track.

## Finding a record's body in the catalogue

- A sidecar's `type_id` is the catalogue's `blueprint_id`.
- The track's `type_id` is the SUMO vehicle type. In a compiled scenario the part after the class name
  is the blueprint, and the type's `carla:blueprint` parameter names it too. You can also join the row
  to a sidecar record by `sumo_id` and frame.
- From there, the body's entry in `vehicles[]` gives its size, `bbox_centre_m`, `body_width_m`,
  `colour_palette`, `colour_applied` and `lamp_capability`. Its class in `classes[]` gives the
  `base_type` and `special_type`. See
  [How the catalogue meets the truth](../Schemas/Vehicle_Catalogue.md#how-the-catalogue-meets-the-truth).
