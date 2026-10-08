# SUMO vehicle types (`vehicles.vtypes.rou.xml`)

`vehicles.vtypes.rou.xml` is the [vehicle catalogue](VehicleCatalogue.md) as SUMO reads it. Each
measured CARLA body is one SUMO vehicle type, `<vType>`, and each class is one type distribution,
`<vTypeDistribution>`, over its members. A scenario asks for a class; SUMO draws a member from it; the
member is the CARLA body. So the size SUMO reserves on the road and the body CARLA draws are the same.

The file is a SUMO route file that holds only types, so SUMO's own `routes_file.xsd` accepts it. Our
schema is narrower: it describes exactly what the writer writes.

- Schema: `CarlaControl/schemas/vehicle_types.xsd` (XSD 1.0)
- Schema id: `urn:carla-sumo-capture:schema:vehicle_types:1`

## Who writes it and who reads it

`SumoVehicleTypeWriter` writes it from the catalogue, beside it, whenever the catalogue is built
(`make_vehicle_catalogue.py`) or its body widths are applied (`apply_vehicle_body_widths.py`). Do not edit
it by hand: edit the catalogue and write it again.

The scenario compiler writes the types a scenario draws into the scenario's own route file, with the
same parameters. SUMO reads them. The SUMO drive and `carla-cot-telemetry` read the parameters back
from the running simulation to find each vehicle's body, class and catalogue.

## Structure

```
<routes>
  <vType ...> <param .../> ... </vType>      one per measured body, in class order
  ...
  <vTypeDistribution .../>                   one per class, in class order
  ...
</routes>
```

No element is in a namespace. The file is UTF-8 with a comment saying where it came from.

## `<vType>` attributes

Every attribute is written on every type, in this order.

| Attribute | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `id` | string | | yes | The CARLA blueprint id, such as `vehicle.lincoln.mkz`. |
| `vClass` | string | | yes | The class's `sumo_vclass`. |
| `length` | number | meters | yes | The catalogue's `length_m`. |
| `width` | number | meters | yes | The catalogue's `body_width_m`, the width without mirrors; its `width_m` where it holds no body width. |
| `height` | number | meters | yes | The catalogue's `height_m`. |
| `minGap` | number | meters | yes | The class's `min_gap_m`. |
| `maxSpeed` | number | m/s | yes | The class's `max_speed_mps`. |
| `accel` | number | m/s² | yes | The class's `accel_mps2`. |
| `decel` | number | m/s² | yes | The class's `decel_mps2`. |
| `sigma` | number | | yes | The class's `sigma`. |
| `speedFactor` | number | | yes | The class's `speed_factor_mean`. |
| `speedDev` | number | | yes | The class's `speed_factor_dev`. |
| `guiShape` | string | | yes | The class's `gui_shape`. sumo-gui only. |
| `color` | string | | yes | The class's `gui_colour`, `#RRGGBB`. sumo-gui only: it never reaches the rendered body, whose color comes from its own palette. |

Numbers are written with up to six significant digits and no trailing `.0`.

## `<param>` children

Each `<vType>` has two or three `<param key="..." value="..."/>` children, each key once:

| Key | Required | Meaning |
|---|---|---|
| `carla:blueprint` | yes | The CARLA blueprint the type renders as. A vehicle whose type has none is refused a body. |
| `carla:class_id` | yes | The class the type belongs to. A run's display convention names a vehicle's population by it. |
| `carla:catalogue_digest` | when the catalogue has a digest | The `catalogue_digest` of the catalogue the type was written from. A reader holding another catalogue warns once. |

## `<vTypeDistribution>` attributes

| Attribute | Type | Required | Meaning |
|---|---|---|---|
| `id` | string | yes | The class id, such as `civ_car`. |
| `vTypes` | list of type ids | yes | The class's members, separated by spaces. Each names a `<vType>` in the file. |
| `probabilities` | list of numbers | yes | Each member's weight, in the same order. |

## Format version

The file carries no version. It is version 1. Its shape changes only with the catalogue's
`catalogue_version`, and every type records which catalogue it came from.

## Example

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!-- Generated from vehicles.catalogue.json by carlacontrol.VehicleCatalogueBuilder. ... -->
<routes>
    <vType id="vehicle.lincoln.mkz" vClass="passenger" length="4.892" width="1.8335" height="1.5241" minGap="2.5" maxSpeed="35" accel="2.6" decel="4.5" sigma="0.5" speedFactor="1" speedDev="0.1" guiShape="passenger" color="#CCCCD1">
        <param key="carla:blueprint" value="vehicle.lincoln.mkz"/>
        <param key="carla:class_id" value="civ_car"/>
        <param key="carla:catalogue_digest" value="6037e3bb2bde6f45de45e31925236593d16653989293fe414d9060a78bfbe90d"/>
    </vType>
    <vTypeDistribution id="taxi" vTypes="vehicle.taxi.ford" probabilities="1"/>
</routes>
```
