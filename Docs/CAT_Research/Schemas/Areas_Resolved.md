# Resolved areas of interest (`areas.resolved.json` in a `.cwp`)

An area of interest is a named place where a scenario can put behavior and that a label can refer to: a guard post, a gate, a parking lot.\
An author declares areas in a GeoJSON file beside the OpenStreetMap extract, `<extract>.aoi.geojson`, in latitude and longitude.\
`areas.resolved.json` holds those areas placed on the built world: in CARLA meters, and on the SUMO lanes that lie inside, cross, or pass near each one.\
The lane positions can be written straight into a SUMO `<stop>`.

The areas file itself is described in [Areas_Of_Interest.md](Areas_Of_Interest.md), and its schema is `area_of_interest.schema.json`.\
The package holds a byte-for-byte copy of the areas file as `areas.aoi.geojson`.

- Schema: `CarlaControl/schemas/areas_resolved.schema.json`
- Schema id: `urn:carla-sumo-capture:schema:areas-resolved:1`

## Who writes it and who reads it

carlacontrol's `AreaOfInterestResolver` writes it when the authoring reference set is published.\
It converts each vertex with SUMO's own projection, run by a SUMO process holding the world's network, and converts it again with the world's own geographic frame.\
If the two disagree by more than `geodesy_agreement_limit_m` at any vertex, the areas are refused and the package gets no `areas.resolved.json`; the build log names every problem.

The file is always written when the set is published and the areas are not refused.\
A world built with no areas declared gets a table with an empty `areas` list, so a world with no areas can be told apart from one that was never published.

The scenario compiler reads it through `WorldPackageReader.areas_of_interest()`, to locate places named by area.\
The reader refuses the table when its `source_sha256` is not the digest of the `areas.aoi.geojson` beside it, because the table would then describe other areas.

The file is JSON, UTF-8, two-space indent, keys sorted.

## How lanes are classified

Every lane of a normal edge is tested against every area:

- `inside`: the whole lane lies in the area.
- `crossing`: part of the lane lies in the area.\
  A lane that leaves a concave area and comes back has more than one stretch in `intervals_m`.
- `near`: no part lies in the area, but the lane passes within `near_m` of it.

Positions on a lane are SUMO lane positions in meters: the distance along the lane's shape, scaled by the lane's `length` over its shape length, as SUMO maps positions.\
An edge is `inside` when all its lanes are, `crossing` when any listed lane is inside or crossing, and `near` otherwise.

## Fields

### Top level

| Field | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `resolved_version` | integer, always 1 | | no | The format of this file. A table without it is version 1. |
| `source_file_name` | string | | yes | The areas file's name. Empty when no areas were declared. |
| `source_sha256` | string | | yes | SHA-256, lowercase hexadecimal, of `areas.aoi.geojson`'s bytes. Empty when no areas were declared. |
| `world_map_name` | string | | yes | `MapName` from `world.json`. |
| `world_georeference` | string | | yes | `GeoReferenceString` from `world.json`. |
| `world_origin_latitude` | number or null | degrees | yes | `OriginLatitude` from `world.json`. |
| `world_origin_longitude` | number or null | degrees | yes | `OriginLongitude` from `world.json`. |
| `network_fingerprint` | string | | yes | Fingerprint of the network the lanes are from. |
| `near_m` | number | meters | yes | A lane within this distance of an area is near it. 50 unless the build was told otherwise. |
| `frame` | object | | yes | How the positions were converted. |
| `areas` | array of objects | | yes | One entry per declared area, in the areas file's order. |

### `frame`

| Field | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `carla_from_sumo` | string | | yes | `carla(x, y) = sumo(x, -y)`: CARLA's frame has y south, SUMO's has y north. |
| `projected_by` | string | | yes | What converted degrees to SUMO meters, such as `SUMO 1.27.0, traci.simulation.convertGeo`. Empty when no areas were declared. |
| `net_offset_m` | array of 2 numbers | meters | yes | The network's `netOffset`. 0, 0 unless the roads were deliberately shifted. |
| `geodesy_agreement_limit_m` | number | meters | yes | How far apart the two conversions may place a vertex, 0.05 m. |
| `geodesy_worst_residual_m` | number or null | meters | yes | The largest distance measured between them. Null when no areas were declared. |

### `areas[]`

| Field | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `id` | string | | yes | The area's id from the areas file: a lowercase letter, then up to 63 lowercase letters, digits or underscores. |
| `name` | string | | yes | The area's name. |
| `kind` | string or null | | yes | The area's kind, such as `bahonar:guard_post`. Null when the file gives none. |
| `geographic` | object | degrees | yes | The area's GeoJSON geometry, copied from the areas file: a `Polygon`, a `MultiPolygon`, or a `Point` whose radius is `radius_m` in the file. Positions are `[longitude, latitude]`. |
| `carla_local` | object | meters | yes | The area in CARLA's frame, rounded to the millimeter: `{"type": "Circle", "centre": [x, y], "radius_m": r}`, `{"type": "Polygon", "coordinates": [ring, ...]}`, or `{"type": "MultiPolygon", "coordinates": [[ring, ...], ...]}`. A ring is a list of `[x, y]` positions, first and last the same. |
| `envelope_carla_m` | array of 4 numbers | meters | yes | The rectangle around the area in CARLA's frame: min x, min y, max x, max y. |
| `sumo` | object | | yes | `edges` and `lanes`, below. |
| `warnings` | array of strings | | yes | What resolving the area warned about: crossing the edge of the staging rectangle, lying wholly in the staging ring, or no lane a four-wheeled vehicle can use reaching it. |

### `areas[].sumo.edges[]`

| Field | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `edge_id` | string | | yes | A SUMO edge with at least one listed lane. |
| `containment` | string | | yes | `inside`, `crossing` or `near`. |

### `areas[].sumo.lanes[]`

Ordered by edge id, then lane id.\
A lane inside or crossing the area:

| Field | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `lane_id` | string | | yes | The SUMO lane id. |
| `edge_id` | string | | yes | Its edge. |
| `containment` | string | | yes | `inside` or `crossing`. |
| `s_begin_m` | number | meters | yes | SUMO lane position where the first stretch inside begins. |
| `s_end_m` | number | meters | yes | SUMO lane position where the last stretch inside ends. |
| `intervals_m` | array of `[begin, end]` | meters | yes | Every stretch of the lane inside the area, in SUMO lane positions, rounded to 0.01 m. |
| `allowed_vclasses` | array of strings | | yes | The SUMO vehicle classes allowed on the lane, or `["all"]` when the lane declares no restriction. |

A lane near the area:

| Field | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `lane_id` | string | | yes | The SUMO lane id. |
| `edge_id` | string | | yes | Its edge. |
| `containment` | string | | yes | `near`. |
| `distance_m` | number | meters | yes | Shortest distance from the lane to the area, rounded to 0.01 m. |
| `allowed_vclasses` | array of strings | | yes | As above. |

The allowed classes are the world network's.\
A scenario that changes lane permissions checks its own network.

## Format version

`resolved_version` is 1, and there is no other version.\
A file without it is version 1.\
`WorldPackageReader` and CarlaNet's `WorldPackage` refuse a file that declares a newer version.\
They name the version and the newest they read, rather than reading part of the file.\
`WorldPackageReader` then checks the file against the schema.\
If the file does not match, it refuses the file and names each problem.

## Examples

A world with no areas declared:

```json
{
  "areas": [],
  "frame": {
    "carla_from_sumo": "carla(x, y) = sumo(x, -y)",
    "geodesy_agreement_limit_m": 0.05,
    "geodesy_worst_residual_m": null,
    "net_offset_m": [0.0, 0.0],
    "projected_by": ""
  },
  "near_m": 50.0,
  "network_fingerprint": "ffe490b1ee677d5b48ac2350f3a579e8112f68155bbd80893eebd137c724bfdc",
  "resolved_version": 1,
  "source_file_name": "",
  "source_sha256": "",
  "world_georeference": "+proj=tmerc +lat_0=39.59431 +lon_0=-104.88449 +k=1 +x_0=0 +y_0=0 +ellps=WGS84 +units=m +no_defs",
  "world_map_name": "Arapahoe_I25",
  "world_origin_latitude": 39.59431,
  "world_origin_longitude": -104.88449
}
```

One area of a world with areas, trimmed to two of its lanes:

```json
{
  "carla_local": {"centre": [807.329, -1015.851], "radius_m": 30.0, "type": "Circle"},
  "envelope_carla_m": [777.329, -1045.851, 837.329, -985.851],
  "geographic": {"coordinates": [56.1887954, 27.1592876], "type": "Point"},
  "id": "tower_00",
  "kind": "bahonar:guard_post",
  "name": "Guard tower 0",
  "sumo": {
    "edges": [{"containment": "near", "edge_id": "-26413409#0"},
              {"containment": "crossing", "edge_id": "-26413411"}],
    "lanes": [
      {"allowed_vclasses": ["authority", "army", "pedestrian", "delivery", "bicycle"],
       "containment": "near", "distance_m": 48.58, "edge_id": "-26413409#0",
       "lane_id": "-26413409#0_0"},
      {"allowed_vclasses": ["authority", "army", "pedestrian", "delivery", "bicycle"],
       "containment": "crossing", "edge_id": "-26413411", "intervals_m": [[0.0, 35.0]],
       "lane_id": "-26413411_0", "s_begin_m": 0.0, "s_end_m": 35.0}
    ]
  },
  "warnings": []
}
```
