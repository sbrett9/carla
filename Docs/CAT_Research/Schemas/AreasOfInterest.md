# Areas of interest

| | |
|---|---|
| File | `<extract>.aoi.geojson`, beside the OpenStreetMap extract |
| Schema | `CarlaControl/schemas/area_of_interest.schema.json` |
| Schema id | `urn:carla-sumo-capture:schema:area-of-interest:1` |
| Format version | 1. The file carries no version field. |

## What it is

An area of interest is a named place that a scenario can site behavior on and an annotation can
refer to: a guard tower, a gate, a parking lot. You declare areas in GeoJSON (RFC 7946) beside the
OpenStreetMap extract a world is built from. `Import/Arapahoe_I25.osm` has its areas in
`Import/Arapahoe_I25.aoi.geojson`.

An area is one of three shapes: a `Polygon`, a `MultiPolygon` (holes are allowed in either), or a
`Point` with a radius, which is a circle. GeoJSON has no circle, so the radius is one of our
properties.

Positions are `[longitude, latitude]` in WGS84 degrees, in that order. The reader names the most
common mistake, `[latitude, longitude]`, when swapping the two would put an area inside the extract.

## Who writes it and who reads it

- **A scenario developer writes it**, by hand or in a GIS tool.
- **The world build reads it** (`carla-build-world`, `carla-sctmv`): the file `--aoi` names, or the
  one found beside `--osm`. It checks the file before any time is spent building the world, resolves
  each area into the world's frame, and carries the file into the world package as
  `areas.aoi.geojson`, beside the resolved table `areas.resolved.json`.
- **`carla-publish-reference-set` reads it** the same way.
- The scenario compiler does not read this file. It reads the resolved table in the world package,
  and refuses an `aoi_ref` that names no area there (check 20).
- The reader refuses the whole file, naming every problem. It refuses a file that does not conform to
  the schema too. It warns, and carries on, about an altitude on a position, a property it does not
  read, and a `crs` member.

## Fields

The file is a GeoJSON `FeatureCollection`. Other members GeoJSON allows, such as `bbox` or a
Feature's own `id`, are allowed and not read.

| Field | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `type` | constant `"FeatureCollection"` | | yes | A GeoJSON feature collection. |
| `features` | array of features | | yes | The areas, in file order. |
| `crs` | any | | no | Removed from GeoJSON by RFC 7946. Positions are read as WGS84 degrees whatever it says, with a warning. |
| `features[].type` | constant `"Feature"` | | yes | A GeoJSON feature. |
| `features[].geometry.type` | `Polygon`, `MultiPolygon` or `Point` | | yes | The area's shape. |
| `features[].geometry.coordinates` | array | degrees | yes | A Polygon's rings, a MultiPolygon's polygons, or a Point's position. |
| `features[].properties.id` | string | | yes | The area's id: a lower-case letter, then up to 63 lower-case letters, digits and underscores. Unique in the file. Annotations name the area by it. |
| `features[].properties.name` | string | | yes | The area's name for a person. Not blank. |
| `features[].properties.kind` | string or null | | no | What kind of place it is, such as `bahonar:guard_post`. Carried into the world unchanged. |
| `features[].properties.radius_m` | number above 0 | m | for a Point | The circle's radius. Refused on a Polygon or MultiPolygon. |

A position is two numbers, longitude then latitude, or three, the third an altitude that is ignored
with a warning. A ring has at least four positions, the last repeating the first.

The reader also checks what the schema cannot say:

- a ring is closed, has at least three distinct corners, does not cross or touch itself, and encloses
  at least one square meter;
- a hole does not cross another ring of its polygon and lies inside its exterior;
- every area reaches into the extract's `<bounds>`, and positions are inside WGS84's ranges.

## Versions

The file is GeoJSON and carries no version of its own. Every file is read as version 1 of our
properties, which this page describes.

## Example

A circle and a polygon:

```json
{
  "type": "FeatureCollection",
  "features": [
    {
      "type": "Feature",
      "geometry": {"type": "Point", "coordinates": [56.1887954, 27.1592876]},
      "properties": {"id": "tower_00", "name": "Guard tower 0", "kind": "bahonar:guard_post",
                     "radius_m": 30}
    },
    {
      "type": "Feature",
      "geometry": {"type": "Polygon", "coordinates": [[
        [-104.8850, 39.5940], [-104.8840, 39.5940], [-104.8840, 39.5950],
        [-104.8850, 39.5950], [-104.8850, 39.5940]]]},
      "properties": {"id": "school_lot", "name": "School parking lot", "kind": "site:parking"}
    }
  ]
}
```
