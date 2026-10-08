# Place index (`places.json` in a `.cwp`)

`places.json` lists which edges of a world's SUMO network carry which street name, and which way each edge heads.\
With it, a scenario that names a place as "eastbound on Centerville Lane" can be resolved to edges.\
The index also says how much of the network its names cover, because on some maps few roads are named.

- Schema: `CarlaControl/schemas/place_index.schema.json`
- Schema id: `urn:carla-sumo-capture:schema:place-index:1`

## Who writes it and who reads it

carlacontrol's `PlaceIndex` writes it when the world build publishes the authoring reference set, or when `carla-publish-reference-set` publishes it again.\
It is made from `map.net.xml` alone, so it always describes the network beside it.

The scenario compiler reads it through `WorldPackageReader.place_index()` to resolve street places.\
A scenario author can read it to see what names a world has.

The file is JSON, UTF-8, two-space indent, keys sorted.\
Non-Latin names are written as they are, not escaped.

## Which edges it covers

Only normal edges: edges with no `function` attribute, or `function="normal"`.\
Edges inside junctions are left out.

An edge is listed under its `name` attribute, which netconvert writes from the OpenStreetMap street names because the world build passes `--output.street-names`.\
An edge without a name is counted but not listed.

A name is not an edge.\
One street name is often carried by many edges, so a scenario narrows a street by direction and position before it names one edge.

## Fields

### Top level

| Field | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `place_index_version` | integer, always 1 | | no | The format of this file. An index without it is version 1. |
| `network_fingerprint` | string | | yes | Fingerprint of the network the index was made from, the same as `NetworkFingerprint` in `world.json`. |
| `coverage` | object | | yes | How much of the network the names cover. |
| `warnings` | array of strings | | yes | What the index warned about when it was made. |
| `streets` | array of objects | | yes | One entry per street name, ordered by the name's UTF-8 bytes. |

### `coverage`

| Field | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `normal_edges` | integer | count | yes | Edges not inside a junction. |
| `named_edges` | integer | count | yes | Of those, the edges with a street name. |
| `named_fraction` | number | fraction | yes | `named_edges / normal_edges`, rounded to four decimals. 0 for a network with no edges. |
| `distinct_names` | integer | count | yes | Different street names. |
| `names_by_script` | object | count | yes | For each writing system, how many names use it. Keys are the first word of each letter's Unicode name, such as `LATIN` or `ARABIC`. `NONE` counts names with no letters. A name in two scripts counts under both. |
| `largest_name` | object or null | | yes | The name carried by the most edges: `name` and `edge_count`. Null when no edge is named. |
| `warn_below_named_fraction` | number | fraction | yes | Below this `named_fraction`, 0.5, the index warns that names contribute little on this map. |

### `streets[]`

| Field | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `name` | string | | yes | The street name. |
| `scripts` | array of strings | | yes | The writing systems of the name's letters, sorted. |
| `edge_count` | integer | count | yes | Edges carrying the name. |
| `length_m` | number | meters | yes | Their lengths added up, rounded to 0.01 m. |
| `extent_carla_m` | array of 4 numbers | meters | yes | The rectangle around all their lanes in CARLA's frame: min x, min y, max x, max y. |
| `directions` | object | | yes | For each of `north`, `east`, `south` and `west` that any edge heads, the edge ids heading that way, in the order a vehicle traveling that way meets them. A direction with no edge is left out. |
| `edges` | array of objects | | yes | Every edge carrying the name, ordered by edge id. |

### `streets[].edges[]`

| Field | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `edge_id` | string | | yes | The SUMO edge id. |
| `from_junction` | string | | yes | The SUMO junction the edge leaves. |
| `to_junction` | string | | yes | The SUMO junction the edge reaches. |
| `bearing_deg` | number | degrees | yes | Compass bearing from the rightmost lane's first point to its last, clockwise from grid north, rounded to 0.1. From 0 up to but not including 360. |
| `direction` | string | | yes | `north`, `east`, `south` or `west`: the quarter of the compass the bearing falls in. Each owns the 90 degrees centered on it. |
| `length_m` | number | meters | yes | The rightmost lane's SUMO length. |
| `lane_count` | integer | count | yes | Lanes on the edge. |
| `lane_ids` | array of strings | | yes | The SUMO lane ids, rightmost first. |
| `speed_mps` | number | meters per second | yes | The rightmost lane's speed limit. |
| `extent_carla_m` | array of 4 numbers | meters | yes | The rectangle around the edge's lanes in CARLA's frame: min x, min y, max x, max y, rounded to 0.01 m. |

CARLA's frame has x east and y south, so CARLA (x, y) = SUMO (x, −y).\
A bearing is the net direction of a whole edge.\
On a curving edge it says little about the heading at any one point.

## Format version

`place_index_version` is 1, and there is no other version.\
A file without it is version 1.

`WorldPackageReader` and CarlaNet's `WorldPackage` refuse a file that declares a newer version.\
They name the version and the newest they read, rather than reading part of the file.\
`WorldPackageReader` then checks the file against the schema.\
If the file does not match, it refuses the file and names each problem.

## Example

Trimmed to one street:

```json
{
  "coverage": {
    "distinct_names": 32,
    "largest_name": {"edge_count": 65, "name": "South Yosemite Street"},
    "named_edges": 288,
    "named_fraction": 0.9028,
    "names_by_script": {"LATIN": 32},
    "normal_edges": 319,
    "warn_below_named_fraction": 0.5
  },
  "network_fingerprint": "ffe490b1ee677d5b48ac2350f3a579e8112f68155bbd80893eebd137c724bfdc",
  "place_index_version": 1,
  "streets": [
    {
      "directions": {"east": ["-572762876#8"], "west": ["572762876#1"]},
      "edge_count": 2,
      "edges": [
        {"bearing_deg": 48.2, "direction": "east", "edge_id": "-572762876#8",
         "extent_carla_m": [-84.68, -872.67, 110.72, -697.78],
         "from_junction": "582786872", "lane_count": 1, "lane_ids": ["-572762876#8_0"],
         "length_m": 349.32, "speed_mps": 13.89, "to_junction": "cluster_5502379254_5945761229"},
        {"bearing_deg": 228.4, "direction": "west", "edge_id": "572762876#1",
         "extent_carla_m": [-87.46, -872.64, 107.37, -699.65],
         "from_junction": "cluster_5502379254_5945761229", "lane_count": 1,
         "lane_ids": ["572762876#1_0"], "length_m": 347.39, "speed_mps": 13.89,
         "to_junction": "582786872"}
      ],
      "extent_carla_m": [-87.46, -872.67, 110.72, -697.78],
      "length_m": 696.71,
      "name": "E Caley Way",
      "scripts": ["LATIN"]
    }
  ],
  "warnings": [
    "a street name is one-to-many: 'South Yosemite Street' is carried by 65 edges, so a name must be narrowed by direction and position before it names one"
  ]
}
```
