# World package manifest (`world.json` in a `.cwp`)

`world.json` says what a generated world is:

- where it sits on the Earth;
- how its roads were seated on the photoreal imagery;
- which imagery it streams;
- where traffic may enter and leave;
- how it was built.

It is the one entry every world package must have.\
See [World_Package.md](World_Package.md) for the other entries.

This is a different file from the `world.json` in a level package.\
That one is described in [Level_Package_Manifest.md](Level_Package_Manifest.md).

- Schema: `CarlaControl/schemas/world_package_manifest.schema.json`
- Schema id: `urn:carla-sumo-capture:schema:world-package-manifest:1`

## Who writes it and who reads it

CarlaNet's `WorldPackage.Write` writes it when the world build writes the package.\
The build reads the origin and the staging rectangle back from the server, so the file records what the world ended up with, not what was asked for.

The file is indented JSON, UTF-8, with PascalCase keys.\
The writer writes every field, in the order of the table below.

These read it:

- CarlaNet's `WorldPackage.ReadManifest`, which a SUMO drive and a capture run use to check the package against the world the server has loaded;
- carlacontrol's `WorldPackageReader`, which the scenario compiler, the capture run's configuration and `carla-publish-reference-set` use;
- the Unreal Editor's World Package Importer, which copies the origin, the height settings, the imagery layers, the staging rectangle and the provenance fields into the level's world settings asset.

## Frames and units

- Positions are in CARLA's frame: meters, x east, y south, z up.\
  CARLA's (0, 0) is at `OriginLatitude`, `OriginLongitude`.
- Heights are WGS84 ellipsoidal heights in meters.\
  CARLA's z = 0 is `OriginHeightMeters`.
- Latitude and longitude are WGS84 degrees.

## Fields

"Required" means CarlaNet's reader refuses a manifest without the field.\
The current writer writes every field.

| Field | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `FormatVersion` | integer, always 1 | | no | The format of this file. A manifest without it is version 1. |
| `Producer` | object | | no | What made the package: the tool, the CarlaNet release, the server's build identity and the SUMO release. See "The producer record" below. Absent from packages written before it was recorded. |
| `MapName` | string | | yes | The world's name. The package file is `<MapName>.cwp`. |
| `OriginLatitude` | number | degrees | yes | Latitude of CARLA's (0, 0). |
| `OriginLongitude` | number | degrees | yes | Longitude of CARLA's (0, 0). |
| `OriginHeightMeters` | number | meters | yes | Ellipsoidal height that CARLA's z = 0 stands for. |
| `GeoReferenceString` | string | | no | The OpenDRIVE projection, a PROJ string, copied from `map.xodr`. Empty when `map.xodr` has none. |
| `HeightAlignMode` | string | | yes | How roads were seated on the photoreal imagery: `none`, `area`, `origin` or `drape`. |
| `DrapeActive` | boolean | | yes | `true` when the drivable surface was fitted to the imagery point by point. Then `bareearth.bin` holds the grids. |
| `HeightAlignOffsetMeters` | number | meters | yes | The one height added to the road surface by the `area` and `origin` modes. 0 for `none` and for `drape`. |
| `GridMinXMeters` | number | meters | no | CARLA x of the grid's first column. 0 when there is no grid. |
| `GridMinYMeters` | number | meters | no | CARLA y of the grid's first row. 0 when there is no grid. |
| `GridCellSizeMeters` | number | meters | no | Spacing between grid points. 0 when there is no grid. |
| `GridNumCols` | integer | | no | Grid points along x. 0 when there is no grid. |
| `GridNumRows` | integer | | no | Grid points along y. 0 when there is no grid. |
| `BareEarthOffsetSha1` | string | | no | SHA-1, lowercase hexadecimal, of the offset plane's bytes in `bareearth.bin`. Empty when there is no grid, and on packages written before it was recorded. |
| `BareEarthDtmSha1` | string | | no | SHA-1 of the ground height plane's bytes in `bareearth.bin`. Empty as above. |
| `PhotorealIonAssetId` | integer | | no | Cesium ion asset id of the photoreal imagery. |
| `GroundIonAssetId` | integer | | no | Cesium ion asset id of the bare-earth terrain the road heights came from. 1 is Cesium World Terrain. 0 means the heights came from the photoreal surface. |
| `StagingMinXMeters` | number | meters | no | West edge of the staging rectangle, CARLA x. |
| `StagingMinYMeters` | number | meters | no | North edge of the staging rectangle, CARLA y. |
| `StagingMaxXMeters` | number | meters | no | East edge of the staging rectangle, CARLA x. |
| `StagingMaxYMeters` | number | meters | no | South edge of the staging rectangle, CARLA y. |
| `StagingMarginMeters` | number | meters | no | Width of the ring inside the staging rectangle where traffic enters and leaves. |
| `SourceOsmFileName` | string | | no | File name of the OpenStreetMap extract the world was built from. |
| `SourceOsmSha256` | string | | no | Fingerprint of that extract: SHA-256 over its parsed content, so two copies of one extract match whatever order their elements are in. Older packages hold a SHA-256 of the file's bytes instead. |
| `OpenDriveSha256` | string | | no | SHA-256 of `map.xodr` from its root element on, with the header's `date` blanked, so two builds of one world compare equal. |
| `NetworkFingerprint` | string | | no | Fingerprint of `map.net.xml`: SHA-256 over its parsed content. Empty on a package without a network. A scenario records it and is refused against a package whose network differs. |
| `NetconvertArgv` | array of strings | | no | Every argument netconvert was given, in order. Its output files appear as `<opendrive-output>` and `<output-file>`, and a ramp meter program file as `<tllogic-files>`. The input extract appears as the path the build used. |
| `NetconvertPath` | string | | no | The netconvert program that ran. Empty on older packages. |
| `NetconvertVersion` | string | | no | What that netconvert reported as its version, such as `Eclipse SUMO netconvert 1.27.0`. Empty on older packages. |
| `SampleStepMeters` | number | meters | no | Spacing of the height samples along each road's reference line (`--step`). |
| `TerrainResolutionMeters` | number | meters | no | Spacing of the drivable surface points in `drape` mode (`--terrain-res`). The grid's cell size. |
| `TerrainMarginMeters` | number | meters | no | The staging margin the build was asked for (`--terrain-margin`). |
| `GeneratedAtUtc` | string | | no | When the package was written, ISO 8601 UTC with seven decimal places, such as `2026-10-07T17:32:32.1970674Z`. |
| `GeneratorVersion` | string | | no | Assembly version of the CarlaNet client that wrote the package, such as `1.0.0.0`. |
| `NetconvertExtraArgs` | array of strings | | no | The arguments, among `NetconvertArgv`, that the build added to the standard set, such as the road filter. |

No other field is allowed.\
The Cesium ion access token is never recorded.

### The height modes

| `HeightAlignMode` | `DrapeActive` | `HeightAlignOffsetMeters` | `bareearth.bin` |
|---|---|---|---|
| `none` | `false` | 0 | absent |
| `area`, `origin` | `false` | the one shift added to roads and ground | absent |
| `drape` | `true` | 0 | present |

In every mode the truth a capture records is bare-earth height.\
The shift exists only so that vehicles are drawn on the photoreal surface.

### The producer record

`Producer` is the record every file these tools write carries.\
Its keys are snake_case:

| Field | Type | Required | Meaning |
|---|---|---|---|
| `tool` | string | yes | The component that wrote the file, or the program's name where no component declared itself. |
| `tool_version` | string or null | yes | The release of the package the tool comes from, such as `0.10.0+g1a2b3c4d5`. Null where it did not say. |
| `carlanet` | string or null | yes | The CarlaNet release the process loaded. |
| `server` | object or null | yes | The CARLA server's build identity. Null where no server was used. |
| `sumo` | string or null | yes | The SUMO release where SUMO ran, such as `1.27.0`. |
| `written_utc` | string | no | When the file was written, ISO 8601 UTC to the millisecond. |

A server that answered has `available` `true` and:

- `release`;
- `world_interface`;
- `build` (`package` or `editor`);
- `configuration`;
- `carla_commit`, `content_commit` and `engine_commit`;
- `commits_from` (`version_file`, `compiled` or `none`).

A server that could not answer has `available` `false`, `release`, `world_interface` and `reason`.\
A value the server cannot know is the word `unknown`.

## Format version

`FormatVersion` is 1.\
A manifest that has no `FormatVersion` is read as version 1, which is the shape packages written before the field existed have.

- CarlaNet's reader and carlacontrol's reader refuse a manifest whose `FormatVersion` is newer than they support, or is not an integer.\
  The message names the file, the version it declares and the newest the reader supports.\
  They check the version before any other field.
- The Unreal importer refuses a `FormatVersion` above 1.
- carlacontrol's reader then checks every field the manifest carries against the schema, and refuses a field of the wrong type or a field the schema does not name.\
  It does not insist on the required fields, because it reads only some of them.

## Example

A draped world.\
The `Producer` values are typical of a package written by `carla-build-world`, which names itself and carlacontrol's release.

```json
{
  "FormatVersion": 1,
  "Producer": {"tool": "carla-build-world", "tool_version": "0.10.0+g252f459d0", "carlanet": "0.10.0+g252f459d0",
               "server": {"available": true, "release": "0.10.0", "world_interface": "1.0",
                          "build": "package", "configuration": "Development",
                          "carla_commit": "252f459d0a1b2c3d4e5f60718293a4b5c6d7e8f9",
                          "content_commit": "6bcd042a91a54d9a2f2f002869fbf1c75f3768f4",
                          "engine_commit": "e5e266de195a2400a6a74180402fb1a3e8f75472",
                          "commits_from": "version_file"},
               "sumo": "1.27.0", "written_utc": "2026-10-07T17:32:32.197Z"},
  "MapName": "Arapahoe_I25",
  "OriginLatitude": 39.59431,
  "OriginLongitude": -104.88449,
  "OriginHeightMeters": 1747.4032423071112,
  "GeoReferenceString": "+proj=tmerc +lat_0=39.59431 +lon_0=-104.88449 +k=1 +x_0=0 +y_0=0 +ellps=WGS84 +units=m +no_defs",
  "HeightAlignMode": "drape",
  "DrapeActive": true,
  "HeightAlignOffsetMeters": 0,
  "GridMinXMeters": -476.78851318359375,
  "GridMinYMeters": -969.27978515625,
  "GridCellSizeMeters": 2,
  "GridNumCols": 478,
  "GridNumRows": 971,
  "BareEarthOffsetSha1": "47f5c51bd78519d1a85b58daa730a7d7039e72fb",
  "BareEarthDtmSha1": "9283bd4de8e6e8721bb690898c29d16e61f6049a",
  "PhotorealIonAssetId": 2275207,
  "GroundIonAssetId": 1,
  "StagingMinXMeters": -476.78851318359375,
  "StagingMinYMeters": -969.27978515625,
  "StagingMaxXMeters": 477.21148681640625,
  "StagingMaxYMeters": 970.72021484375,
  "StagingMarginMeters": 30.48,
  "SourceOsmFileName": "Arapahoe_I25_clipped.osm",
  "SourceOsmSha256": "f366f0d0e615ad4a663d93d672b8a95589ed3d8e58bdb60b146a423aabf2cecc",
  "OpenDriveSha256": "67157d5d2f52361a4570fcad34d2296c0c01443ad94b3253901c82ce8b3815be",
  "NetworkFingerprint": "ffe490b1ee677d5b48ac2350f3a579e8112f68155bbd80893eebd137c724bfdc",
  "NetconvertArgv": ["--osm-files", "Arapahoe_I25_clipped.osm", "--opendrive-output", "<opendrive-output>",
                     "--tllogic-files", "<tllogic-files>", "--output-file", "<output-file>",
                     "--keep-edges.by-vclass", "passenger"],
  "NetconvertPath": "C:\\sumo\\bin\\netconvert.exe",
  "NetconvertVersion": "Eclipse SUMO netconvert 1.27.0",
  "SampleStepMeters": 10,
  "TerrainResolutionMeters": 2,
  "TerrainMarginMeters": 30.48,
  "GeneratedAtUtc": "2026-10-07T17:32:32.1970674Z",
  "GeneratorVersion": "1.0.0.0",
  "NetconvertExtraArgs": ["--keep-edges.by-vclass", "passenger"]
}
```

The writer escapes some characters as `\uXXXX` (a `+` is written `\u002B`, `<` is `\u003C`).\
Any JSON reader decodes them.
