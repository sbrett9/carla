# Vehicle body widths (`vehicle_body_widths.json`)

A vehicle body's bounding box includes its mirrors.\
SUMO's width is the body without them.\
On a bus the difference is large: the Fuso Rosa's box is 3.93 m wide and its body 3.23 m, on 3.35 m lanes.

A server cannot see a mesh's vertices, so the width without mirrors is measured from the mesh in the Unreal Editor and kept in this table.\
The [vehicle catalog](Vehicle_Catalogue.md) merges it as `body_width_m`, and the [SUMO vehicle types](Vehicle_Types.md) use it as their `width`.

- Schema: `CarlaControl/schemas/vehicle_body_widths.schema.json`
- Schema id: `urn:carla-sumo-capture:schema:vehicle-body-widths:1`

## Who writes it and who reads it

`CarlaControl/scripts/measure_vehicle_body_widths.py` writes it from a folder of meshes exported from the editor as ASCII FBX, one `<blueprint id>.fbx` per vehicle.\
It is kept at `CarlaControl/catalogue/vehicle_body_widths.json`.\
It is not installed with `carlacontrol`.

`VehicleCatalogueBuilder.load_body_widths` reads it, when a catalog is built and when `apply_vehicle_body_widths.py` applies it to the existing catalog.\
The merge refuses the table when a row's `full_width_m` differs from the catalog's `width_m` by more than 1 mm, because the two then measured different meshes.

## How a width is measured

Every vertex of the mesh's first level of detail is binned along the vehicle's length in 5 cm bins, each side separately.\
A side's body half-width is the largest half-width held over at least 0.6 m of length, which removes mirrors and other short protrusions.\
The body width is the two half-widths added.\
The full width is the plain vertex extent.

The table's `method` says the same.

## Fields

| Field | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `body_widths_version` | integer, always 1 | | yes | The format of this file. |
| `method` | string | | yes | How the widths were measured. The catalog copies it into `body_width.method`. |
| `measured` | string | | yes | When, and from what, the meshes were exported. |
| `vehicles` | object | | yes | One row per CARLA blueprint id. |
| `vehicles.<id>.length_m` | number | meters | yes | The mesh's length. |
| `vehicles.<id>.full_width_m` | number | meters | yes | The mesh's whole width, mirrors included. |
| `vehicles.<id>.body_width_m` | number | meters | yes | The width without mirrors. |
| `vehicles.<id>.height_m` | number | meters | yes | The mesh's height. |

All values are rounded to 0.1 mm.

## Format version

`body_widths_version` is 1, and there is no other version.\
`load_body_widths` refuses any other value, or none, and then refuses a table that does not match the schema, naming each problem.

## Example

```json
{
  "body_widths_version": 1,
  "method": "LOD0 vertices of the vehicle's own skeletal mesh (the mesh the blueprint drives), exported from the editor as ASCII FBX; ...",
  "measured": "2026-10-02, UE 5.7.4 editor via VibeUE, the CARLA content as loaded",
  "vehicles": {
    "vehicle.ambulance.ford": {"length_m": 6.3569, "full_width_m": 2.3512, "body_width_m": 2.2856, "height_m": 2.431}
  }
}
```
