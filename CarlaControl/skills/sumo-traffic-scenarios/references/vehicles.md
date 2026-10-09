# Vehicles: the bodies a scenario may draw, and which of their lights work

Generated from the measured vehicle catalogue by `compile_scenario.py --write-vehicles-reference`; do not edit by hand. Catalogue `carla-0.10.0-windows`, digest `6037e3bb2bde6f45de45e31925236593d16653989293fe414d9060a78bfbe90d`, content build `carla-0.10.0-windows`. A test holds this file equal to what the catalogue generates.

A vehicle class draws the bodies below it, one `vType` each, sized from the measurement: `length` and `height` are the body's measured box, `width` the body's without its wing mirrors. Declare a class by its `class_id` and the blueprints it draws; a blueprint not listed here has no measured body and is never rendered (checks 14, 15).

**Lights.** The co-simulation session drives every rendered body's lights by one rule: headlights on below +3 degrees of the sun's geometric elevation and off above +6 by default, read from the sun the world reports, and brake lights and turn signals from the signals SUMO reports for the vehicle. Whether a light *shows* depends on the body's own lamp meshes, which the catalogue's optical pass measured against the dark: `lit` where switching the lamp changed the picture, `unlit` where it changed nothing, `unknown` where it was not measured. A body whose lights are `unlit` is commanded like every other and shows nothing, so a night scenario that wants visible headlights, brake lights or turn signals draws a body whose column reads `lit`. The run manifest's opening row records the rule each run ran under; per-vehicle light state is not in the truth record.

Of the 19 measured bodies, 0 show any of the three lights lit.

## `civ_car`

Ordinary civilian passenger cars. The default for background traffic.

SUMO class `passenger`, base type `car`.

| Body | Length (m) | Body width (m) | Height (m) | Headlights | Brake lights | Turn signals |
|---|---|---|---|---|---|---|
| `vehicle.dodge.charger` | 5.006 | 1.854 | 1.540 | unlit | unlit | unlit |
| `vehicle.lincoln.mkz` | 4.892 | 1.833 | 1.524 | unlit | unlit | unlit |
| `vehicle.mini.cooper` | 4.553 | 2.090 | 1.772 | unlit | unlit | unlit |
| `vehicle.nissan.patrol` | 5.591 | 2.147 | 2.059 | unlit | unlit | unlit |
| `vehicle.ue4.audi.tt` | 4.181 | 1.967 | 1.385 | unlit | unlit | unlit |
| `vehicle.ue4.bmw.grantourer` | 4.611 | 2.144 | 1.667 | unlit | unlit | unlit |
| `vehicle.ue4.chevrolet.impala` | 5.357 | 1.779 | 1.411 | unlit | unlit | unlit |
| `vehicle.ue4.ford.crown` | 5.366 | 1.782 | 1.575 | unlit | unlit | unlit |
| `vehicle.ue4.ford.mustang` | 4.718 | 1.835 | 1.301 | unlit | unlit | unlit |
| `vehicle.ue4.mercedes.ccc` | 4.674 | 1.805 | 1.442 | unlit | unlit | unlit |

## `offroad`

Short-wheelbase four-wheel-drive utility vehicles: an open-topped jeep.

SUMO class `passenger`, base type `car`.

| Body | Length (m) | Body width (m) | Height (m) | Headlights | Brake lights | Turn signals |
|---|---|---|---|---|---|---|
| `vehicle.jeep.wrangler_rubicon` | 3.866 | 1.855 | 1.878 | unlit | unlit | unlit |

## `civ_van`

Panel vans and light commercial bodies on a van chassis.

SUMO class `delivery`, base type `van`.

| Body | Length (m) | Body width (m) | Height (m) | Headlights | Brake lights | Turn signals |
|---|---|---|---|---|---|---|
| `vehicle.sprinter.mercedes` | 5.915 | 1.982 | 2.726 | unlit | unlit | unlit |

## `civ_truck`

Two-axle rigid lorries: a box truck. Slower, longer and taller than anything civilian.

SUMO class `truck`, base type `truck`.

| Body | Length (m) | Body width (m) | Height (m) | Headlights | Brake lights | Turn signals |
|---|---|---|---|---|---|---|
| `vehicle.carlacola.actors` | 8.004 | 2.787 | 4.055 | unlit | unlit | unlit |

## `heavy_truck`

Three-axle rigid heavy goods vehicles: a cab-over lorry. Not articulated -- there is no trailer.

SUMO class `truck`, base type `truck`.

| Body | Length (m) | Body width (m) | Height (m) | Headlights | Brake lights | Turn signals |
|---|---|---|---|---|---|---|
| `vehicle.carlamotors.european_hgv` | 7.924 | 2.787 | 3.783 | unlit | unlit | unlit |

## `bus`

Buses. The content's one bus is a light bus modelled at the size of a lorry.

SUMO class `bus`, base type `bus`.

| Body | Length (m) | Body width (m) | Height (m) | Headlights | Brake lights | Turn signals |
|---|---|---|---|---|---|---|
| `vehicle.fuso.mitsubishi` | 10.174 | 3.233 | 4.241 | unlit | unlit | unlit |

## `taxi`

Licensed taxis. Same body as a civilian saloon, different livery policy.

SUMO class `taxi`, base type `car`, kind `taxi`.

| Body | Length (m) | Body width (m) | Height (m) | Headlights | Brake lights | Turn signals |
|---|---|---|---|---|---|---|
| `vehicle.taxi.ford` | 5.354 | 1.779 | 1.575 | unlit | unlit | unlit |

## `police`

Marked police cars.

SUMO class `authority`, base type `car`, kind `emergency`.

| Body | Length (m) | Body width (m) | Height (m) | Headlights | Brake lights | Turn signals |
|---|---|---|---|---|---|---|
| `vehicle.dodgecop.charger` | 5.237 | 1.854 | 1.644 | unlit | unlit | unlit |

## `ambulance`

Ambulances on a van chassis.

SUMO class `emergency`, base type `van`, kind `emergency`.

| Body | Length (m) | Body width (m) | Height (m) | Headlights | Brake lights | Turn signals |
|---|---|---|---|---|---|---|
| `vehicle.ambulance.ford` | 6.357 | 2.286 | 2.431 | unlit | unlit | unlit |

## `fire_appliance`

Fire appliances. The largest emergency body in the content build.

SUMO class `emergency`, base type `truck`, kind `emergency`.

| Body | Length (m) | Body width (m) | Height (m) | Headlights | Brake lights | Turn signals |
|---|---|---|---|---|---|---|
| `vehicle.firetruck.actors` | 8.580 | 2.896 | 3.827 | unlit | unlit | unlit |
