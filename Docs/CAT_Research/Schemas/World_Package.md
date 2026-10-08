# World package (`.cwp`)

A world package is one generated world in one file. It holds the road network, the SUMO network that
was built with it, the bare-earth height grids, and what a scenario author needs to know about the
world before writing a scenario. The file is named `<MapName>.cwp`.

A `.cwp` file is a zip archive. Every entry is stored, not compressed, because the Unreal Editor's
importer reads only stored entries. To look inside, copy the file, rename the copy to `.zip`, and open
it with any zip tool.

## Who writes it and who reads it

The world build writes it, in two steps:

1. `run_SCTMV.py --emit-world-package` (or `carla-build-world`) builds the world on a running CARLA
   server. CarlaNet's `WorldPackage.Write` then writes `world.json`, `map.xodr`, `map.net.xml`, and, when
   they apply, `bareearth.bin` and `map.tll.xml`. It writes the whole package to `<name>.cwp.partial` and
   then renames it, so a package that exists is always complete.
2. The same build then publishes the authoring reference set into the package
   (`carlacontrol.AuthoringReferenceSet`): `places.json`, `solar.json`, `areas.resolved.json` and, when
   areas of interest were declared, `areas.aoi.geojson`. `carla-publish-reference-set` publishes it again
   for an existing package. Publishing replaces the whole set. Writing the world again drops the set
   until it is published again.

These programs read it:

- The scenario compiler (`carla-compile-scenario`) reads `world.json`, `map.net.xml` and the reference
  set. It needs no server.
- A capture run and a SUMO drive read `world.json`, `map.xodr`, `map.net.xml` and `bareearth.bin`. They
  check that the package describes the world the server has loaded. They seat each vehicle on the
  ground the grids describe.
- The Unreal Editor's World Package Importer reads `world.json`, `map.xodr` and `bareearth.bin`, and
  saves them as assets of a new level.
- `carla-cot-telemetry` reads the ground heights from `bareearth.bin`.

## Entries

| Entry | Format | Required | Present when | Described in |
|---|---|---|---|---|
| `world.json` | JSON | yes | always | [World_Package_Manifest.md](World_Package_Manifest.md) |
| `map.xodr` | ASAM OpenDRIVE 1.4 XML | yes | always | the OpenDRIVE standard |
| `map.net.xml` | SUMO network XML | no | in every package written since the network was carried. A scenario cannot be built against a package without it. | SUMO's `net_file.xsd` |
| `bareearth.bin` | binary | no | when `DrapeActive` is `true` in `world.json`, and only then | [Bare_Earth_Grid.md](Bare_Earth_Grid.md) |
| `map.tll.xml` | SUMO traffic light program XML | no | when the OpenStreetMap extract has ramp meters | SUMO's `tllogic_file.xsd` |
| `places.json` | JSON | no | once the reference set is published | [Place_Index.md](Place_Index.md) |
| `solar.json` | JSON | no | once the reference set is published | [Solar_Frame.md](Solar_Frame.md) |
| `areas.resolved.json` | JSON | no | once the reference set is published, unless the areas were refused | [Areas_Resolved.md](Areas_Resolved.md) |
| `areas.aoi.geojson` | GeoJSON (RFC 7946) | no | when areas of interest were declared and published | [Areas_Of_Interest.md](Areas_Of_Interest.md), `area_of_interest.schema.json` |

A package holds no other entry. Entry names do not include the map's name, because the package's file
name already does.

Notes on the entries that follow external standards:

- `map.xodr` is the OpenDRIVE document the server was given, with heights added to its roads. Its
  `<geoReference>` holds the projection, which `world.json` repeats as `GeoReferenceString`.
- `map.net.xml` is the SUMO network from the same netconvert run as `map.xodr`. It is kept because it
  cannot be made again: a second netconvert run with the same arguments builds different junctions.
  `world.json` records its fingerprint as `NetworkFingerprint`.
- `map.tll.xml` is the file the netconvert run read with `--tllogic-files`, byte for byte. Its root is
  `<tlLogics>`. Each ramp meter is one `<tlLogic>` with four phases. The network carries the same
  programs, so this entry is only the record of what netconvert was given.
- `areas.aoi.geojson` is the author's areas file, copied byte for byte. `areas.resolved.json` records its
  SHA-256 digest, and a reader refuses the table when the two do not match.

## Missing entries

An entry that is absent means something different from an entry that is empty:

- No `bareearth.bin`: the world was not draped, so it has no grid. The height shift is the one value in
  `world.json`.
- No `map.net.xml`: the package was written before the network was kept. Rebuild the world from its
  OpenStreetMap extract.
- No `areas.resolved.json`: the reference set was not published, or its areas were refused. A world
  with no areas declared has an `areas.resolved.json` with an empty `areas` list.

## Format version

The container has no version of its own. Each JSON entry carries one, and `bareearth.bin` carries its
version in its first four bytes. Readers check each entry's version before they read the entry. Each
entry's page says what a reader does with an older or newer version.

`WorldPackageReader` (Python) also checks every JSON entry against its published schema in
`CarlaControl/schemas/`, and refuses an entry that does not match, naming each problem.

`carlacontrol.WorldFileValidator` checks a whole package: the entries it holds and how they are
stored, every JSON entry against its schema and version, `bareearth.bin` against its format page and
the digests in `world.json`, and the network fingerprints and area digest the entries record. Given
a folder, it checks every package in it. `carla-validate <name>.cwp`, or `carla-validate` given a folder
holding packages, runs it.

## Example

The entries of a package built from an OpenStreetMap extract with ramp meters and no areas of interest:

```
Arapahoe_I25.cwp
  world.json             2,925 bytes
  map.xodr           3,035,115 bytes
  map.net.xml        1,141,667 bytes
  map.tll.xml            1,201 bytes
  bareearth.bin      3,713,164 bytes
  places.json          167,050 bytes
  solar.json               381 bytes
  areas.resolved.json      659 bytes
```
