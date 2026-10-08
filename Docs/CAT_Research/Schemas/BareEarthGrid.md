# Bare-earth grid (`bareearth.bin` in a `.cwp`)

`bareearth.bin` holds two height grids over a draped world:

- the **offset** grid: how far the drivable surface was raised or lowered, at each grid point, to sit on
  the photoreal imagery;
- the **ground** grid: the bare-earth ground height at each grid point.

A world built with `--height-align drape` has the file. A world built in any other mode does not; its
one height shift is `HeightAlignOffsetMeters` in [`world.json`](WorldPackageManifest.md).

Tools use the grids to turn the height a vehicle is drawn at into its true bare-earth height, to seat
SUMO-driven vehicles on the ground, and to give the SUMO bridge's telemetry a ground height.

## Who writes it and who reads it

CarlaNet's `WorldPackage.Write` writes it with the rest of the package. The same values are published
to the server when the world is built, and again when a level made from the world loads.

These read it:

- CarlaNet: `WorldPackage.TryReadGrids` and `WorldPackage.TryReadGridDigests`, used by a SUMO drive to
  seat vehicles and to check the package against the world the server has loaded;
- the Unreal Editor's World Package Importer, which saves the grids in the level as a
  `UBareEarthOffsetField` asset;
- carlacontrol's `BareEarthGrid`, which `carla-cot-telemetry` uses for ground heights. It reads the
  entry from a `.cwp`, or the same bytes from a loose `.bin` file.

## Layout

All values are little-endian. There is no padding. The header is 60 bytes. Then come the two grids,
each `N = columns × rows` 32-bit floats. The file is exactly `60 + 8 × N` bytes long.

| Offset (bytes) | Size (bytes) | Type | Field | Unit | Meaning |
|---|---|---|---|---|---|
| 0 | 4 | int32 | magic | | `0x43575031`. Written little-endian, so the first four bytes are `31 50 57 43`, the ASCII text `1PWC`. |
| 4 | 8 | float64 | origin latitude | degrees | `OriginLatitude` from `world.json`. |
| 12 | 8 | float64 | origin longitude | degrees | `OriginLongitude`. |
| 20 | 8 | float64 | origin height | meters | `OriginHeightMeters`: the WGS84 ellipsoidal height of CARLA's z = 0. |
| 28 | 8 | float64 | minimum x | meters | CARLA x of column 0. `GridMinXMeters`. |
| 36 | 8 | float64 | minimum y | meters | CARLA y of row 0. `GridMinYMeters`. |
| 44 | 8 | float64 | cell size | meters | Spacing between grid points, the same along x and y. `GridCellSizeMeters`. |
| 52 | 4 | int32 | columns | count | Grid points along x. `GridNumCols`. At least 2. |
| 56 | 4 | int32 | rows | count | Grid points along y. `GridNumRows`. At least 2. |
| 60 | 4 × N | float32 × N | offset grid | meters | Draped surface height minus ground height, at each point. |
| 60 + 4 × N | 4 × N | float32 × N | ground grid | meters | Bare-earth ground height at each point, WGS84 ellipsoidal. |

The struct format, as Python's `struct` module spells it, is `<iddddddii` for the header, then
`<Nf` twice.

The header repeats seven values of `world.json` so the file describes itself. A reader that has both
checks that they agree; the Unreal importer refuses a file whose columns or rows differ from the
manifest's.

## Grid order and position

Both grids are in row-major order. The value for column `c` and row `r` is at index `r × columns + c`,
which is byte `60 + 4 × (r × columns + c)` in the offset grid and byte
`60 + 4 × N + 4 × (r × columns + c)` in the ground grid.

Values are at grid points, not cell centers. Point `(c, r)` is at:

- CARLA x = minimum x + c × cell size
- CARLA y = minimum y + r × cell size

CARLA's frame has x east and y south. So column 0 is the west edge, row 0 is the north edge, and rows
run from north to south. The last point is at minimum x + (columns − 1) × cell size and minimum y +
(rows − 1) × cell size. The grid's points span the staging rectangle in `world.json`: in every package
built so far the minimum corner equals `StagingMinXMeters`, `StagingMinYMeters` and the far corner
equals `StagingMaxXMeters`, `StagingMaxYMeters`. The cell size is `TerrainResolutionMeters`.

A SUMO position is in the network's own meters, where y points north. To look one up, negate its y:
CARLA (x, y) = SUMO (x, −y).

## Values

- The **ground** grid holds absolute WGS84 ellipsoidal heights, not heights relative to the origin.
  On a world near Denver they are near 1,700 m. To get CARLA z, subtract the origin height.
- The **offset** grid holds the draped surface height minus the ground height. The drivable surface at
  a point is ground + offset, as an ellipsoidal height. Offsets are a few meters, positive or negative.
- A vehicle's bare-earth height is the height it is drawn at minus the offset under it.

Readers sample the grids differently between points:

- CARLA truth splits each cell into two triangles along the diagonal from point (c, r) to point
  (c + 1, r + 1), as Unreal's physics height field does, and interpolates on the triangle. Outside the
  grid it uses the nearest edge.
- The SUMO bridge takes the point at the minimum-x, minimum-y corner of the cell the position falls in,
  without interpolating. Outside the grid it has no answer and reports the configured constant height.

## Digests

`BareEarthOffsetSha1` and `BareEarthDtmSha1` in `world.json` are the SHA-1 digests, lowercase
hexadecimal, of the offset grid's `4 × N` bytes and the ground grid's `4 × N` bytes exactly as stored.
A server holding the world returns the same two digests from `get_bare_earth_digest`, so a client can
prove a package's grids are the loaded world's without fetching them.

## Format version

The magic carries the version: it reads `CWP1` as a big-endian word, and the trailing `1` is the
format version. There is only version 1. Every reader refuses a file whose first four bytes are not
the magic:

- CarlaNet refuses it as "not a world-package grid", and refuses a grid with fewer than 2 columns or
  rows.
- The Unreal importer refuses it, and refuses a file whose length is not exactly `60 + 8 × N` bytes.
- carlacontrol's `BareEarthGrid` refuses it, and refuses a file whose length is not exactly
  `60 + 8 × N` bytes.

A new layout would get a new magic, so an older reader refuses it rather than reading it wrongly.

## Example

The first 76 bytes of a world's grid, 478 columns by 971 rows at 2 m:

```
0000  31 50 57 43 c7 d7 9e 59 12 cc 43 40 e6 e8 f1 7b
0010  9b 38 5a c0 a0 25 8d eb 9c 4d 9b 40 00 00 00 c0
0020  9d cc 7d c0 00 00 00 00 3d 4a 8e c0 00 00 00 00
0030  00 00 00 40 de 01 00 00 cb 03 00 00 b6 bd 84 bf
0040  1a 0f 95 bf f1 bf 4b bf 7b 3e 2f bf 8f c0 4c bf
```

Read as:

| Field | Value |
|---|---|
| magic | `0x43575031` |
| origin latitude | 39.59431 |
| origin longitude | −104.88449 |
| origin height | 1747.4032 m |
| minimum x | −476.7885 m |
| minimum y | −969.2798 m |
| cell size | 2.0 m |
| columns | 478 (`de 01 00 00`) |
| rows | 971 (`cb 03 00 00`) |
| first offsets | −1.037, −1.165, −0.796, −0.685 m |
| first ground heights (from byte 1,856,612) | 1733.639, 1733.610, 1733.000, 1732.720 m |

The file is 60 + 8 × 464,138 = 3,713,164 bytes.

Reading the grids in Python:

```python
import struct, zipfile
import numpy as np

raw = zipfile.ZipFile("Arapahoe_I25.cwp").read("bareearth.bin")
magic, lat, lon, h0, min_x, min_y, cell, cols, rows = struct.unpack_from("<iddddddii", raw, 0)
assert magic == 0x43575031 and len(raw) == 60 + 8 * cols * rows
offset = np.frombuffer(raw, "<f4", cols * rows, 60).reshape(rows, cols)
ground = np.frombuffer(raw, "<f4", cols * rows, 60 + 4 * cols * rows).reshape(rows, cols)
# ground[r, c] is the bare-earth height at CARLA (min_x + c * cell, min_y + r * cell)
```
