# Building a world

This page shows how to turn an OpenStreetMap extract into a world package with `carla-build-world`.
A scenario runs on a world package, so this is the first step for a new location.

The commands on this page are installed with the carlacontrol wheel. From a source checkout, the same
tools are `python CarlaControl/scripts/build_world.py`, `python CarlaControl/scripts/publish_reference_set.py`
and `python CarlaControl/scripts/validate_capture.py` (for `carla-validate`). Every command takes
`--help`.

## What a world package is

A world package is one generated world in one file, named `<MapName>.cwp`. The map name is the
extract's file name without `.osm`: `Arapahoe_I25.osm` gives `Arapahoe_I25.cwp`.

The file is a zip archive. It holds:

- `world.json`, the manifest: where the world sits on the Earth and how it was built;
- `map.xodr`, the road network as OpenDRIVE, with heights, which CARLA loads;
- `map.net.xml`, the SUMO network from the same netconvert run as `map.xodr`;
- `bareearth.bin`, the ground height grids, in a draped world;
- `map.tll.xml`, the ramp meter signal programs, when the extract has ramp meters;
- the authoring reference set: `places.json` (which edges carry which street name), `solar.json`
  (facts about the world's sun), `areas.resolved.json` (the areas of interest placed on the roads)
  and `areas.aoi.geojson` (the areas file you wrote, when you wrote one).

The SUMO network in the package is the network every scenario on this world runs, byte for byte. A
scenario names it by its fingerprint, and the scenario compiler refuses a scenario whose fingerprint
is not the package's.

The scenario compiler, `carla-drive`, `carla-capture` and the Unreal Editor's importer all read the
package. For every entry, see [World package](../Schemas/World_Package.md) and
[World package manifest](../Schemas/World_Package_Manifest.md).

## What the build needs

- **A running CARLA server.** Start it headless: `run-server.ps1` or `run-server.sh` in a CARLA
  distribution, or `Scripts/Windows/RunCarlaServer.ps1` or `Scripts/Linux/RunCarlaServer.sh` in a
  source checkout. The build connects to `127.0.0.1:2000` unless you give `--host` and `--port`. It
  builds the world on the server and leaves it loaded there.
- **A Cesium ion access token**, in the `CESIUM_ION_TOKEN` environment variable, or given with
  `--ion-token`. The build streams the photoreal imagery and the terrain heights from Cesium ion.
  Without a token, the build warns and the height sampling fails.
- **The extract**: an OpenStreetMap `.osm` file with a `<bounds>` element. An extract taken with the
  OpenStreetMap website's Export button has one. The build cuts the roads at those bounds, and the
  draped ground needs them. In a source checkout, keep extracts in `Import/`. A distribution ships
  examples in `osm/`.
- **netconvert**, which the build runs to convert the extract. From a source checkout it is the one
  under `Build/sumo-install`, which `CarlaSetup` builds. In a distribution, dot-source
  `carla-env.ps1` (or source `carla-env.sh`) first: it sets `CARLA_NETCONVERT` and `SUMO_HOME`.

Two optional files sit beside the extract and are found by name:

- `<name>.aoi.geojson`, the areas of interest (see [Areas of interest](#areas-of-interest));
- `<name>.typ.xml`, the type map (see [The type map](#the-type-map)).

## Build the world

```
carla-build-world --osm Import/Gardnerville_Centerville_Lane.osm --height-align drape --drape-cache-dir Build/drape-cache
```

**Always give `--height-align drape`.** It seats the roads on the photoreal imagery point by point
and writes the ground height grids into the package. A SUMO drive and a capture take each vehicle's
height, pitch and roll from those grids. They refuse a package built without them. The default,
`none`, builds a world you can fly over but not drive SUMO traffic in.

The build prints what it found before it starts: the origin, whether the road filter is on, the areas
of interest, the road types, and whether the token is set. Read those lines. When it finishes, the
last line names the package: `world built; its package is <path of the .cwp>`.

The exit status is 0 when the world was built and its package written, and 1 when either was not.

`carla-build-world` takes all the options of `carla-sctmv`. The options for the interactive view,
traffic, telemetry and recording are accepted and do nothing here. `carla-sctmv` builds a world the
same way and then opens a view to fly over it.

## The options that matter

### The road filter

By default, the build keeps only roads a passenger car may drive. It passes netconvert
`--keep-edges.by-vclass passenger`, keeps only the largest connected piece of the network, and drops
roads that connect to nothing.

That removes every road that SUMO's own type map closes to passenger cars:

- every `highway=service` road, which SUMO opens only to delivery vans, pedestrians and bicycles;
- every road tagged `access=no`.

netconvert does not read `access=private`, so a private residential road stays in.

**Turn the filter off with `--no-road-filter`** when your scenario needs those roads. A port, an
airfield or a depot often has its whole interior mapped as service roads. With the filter on, the
interior is gone. A delivery van stopping at a loading dock on a service road needs the filter off
too.

With the filter off, netconvert also keeps footways, paths, steps and cycleways. Remove them by type
with `--netconvert-arg`. The Shahid Bahonar port world was built with these options:

```
carla-build-world --osm Import/Shahid_Bahonar_Port.osm --height-align drape --no-road-filter --netconvert-arg "--remove-edges.by-type highway.footway,highway.path,highway.steps,highway.cycleway,highway.pedestrian,highway.bridleway"
```

Its type map and areas of interest sit beside the extract, where the build finds them. Its
`world.json` records the extra netconvert arguments in `NetconvertExtraArgs`.

`--netconvert-arg` takes one netconvert option and its value, quoted together. Repeat it for more
options. For an option that takes no value, write `--netconvert-arg=--no-turnarounds`.

### Areas of interest

An area of interest is a named place where a scenario can put behavior and a label can point: a gate,
a guard tower, a parking lot, a shop front. You declare areas in a GeoJSON file beside the extract,
named `<name>.aoi.geojson`, or name the file with `--aoi`.

An area is a `Polygon`, a `MultiPolygon`, or a `Point` with a `radius_m`, which is a circle. Each one
has an `id` (lower-case letters, digits and underscores, starting with a letter) and a `name`. It can
have a `kind`, such as `shop:storefront`. Positions are `[longitude, latitude]`, in that order.

```json
{
  "type": "FeatureCollection",
  "features": [
    {
      "type": "Feature",
      "geometry": {"type": "Point", "coordinates": [-104.8994187, 39.4999849]},
      "properties": {"id": "shop_front", "name": "Shop front on East Street",
                     "kind": "shop:storefront", "radius_m": 1.5}
    }
  ]
}
```

The build checks the file before it builds anything, and refuses a file with a problem, naming every
problem. It then places each area on the world's lanes and publishes it into the package. A scenario
uses an area as a place (`{"area": "shop_front"}`). A stop at an area needs the area to hold exactly
one lane, so keep a stop's area small.

Every field and rule is on [Areas of interest](../Schemas/Areas_Of_Interest.md). To change areas
later without building the world again, see
[Refreshing a package's areas](#refreshing-a-packages-areas).

### The type map

The type map says which vehicle classes each kind of road admits. It is a SUMO `<types>` file beside
the extract, named `<name>.typ.xml`, or named with `--type-map`.

The build gives netconvert SUMO's own OSM type map first, and then yours. Your file changes only the
attributes it states, so a line can change one road type's permissions and leave its lanes and speed
alone. The Shahid Bahonar port's `Import/Shahid_Bahonar_Port.typ.xml` sets one road type. Without
its comment, it reads:

```xml
<types>
    <type id="highway.service" allow="delivery pedestrian bicycle army authority"/>
</types>
```

It opens the service roads of the airfield and the port to the `army` and `authority` classes. On the
world built without it, the compiler refuses the guard schedule's trips because their class may not
drive those roads (check 10).

What a road admits is part of the world. The network in the package carries it, and every scenario on
the world inherits it. Change it by building the world again, not by editing a network.

Things to know:

- A type map cannot key on `access`. It sets what every road of a type admits, private or not.
- The build refuses a file that is not XML, a root other than `<types>`, and a `<type>` without an
  `id`. netconvert refuses an unknown vehicle class when the conversion starts.
- Do not also pass `--type-files` through `--netconvert-arg`. The build refuses the two together.
- A type map is often needed together with `--no-road-filter`. Opening service roads to `army` does
  not open them to passenger cars, so the road filter still removes them.

### Terrain resolution and the drape cache

`--terrain-res` is the spacing of the draped ground grid, in meters. The default is 2.0. The ground
height grids in the package have this spacing. Halving it gives four times as many points to sample,
and a package four times as large in its grids.

The terrain sampling is the slow part of a build. `--drape-cache-dir DIR` keeps the sampled heights
in `DIR`, one file per grid. A later build with the same origin, bounds, terrain resolution and
Cesium assets reads the file instead of sampling again. Use one cache folder for all your builds.

Two more settings decide heights:

- `--step` is the spacing of the height samples along each road, in meters (default 10).
- `--ion-asset-id` is the Cesium ion asset for the photoreal imagery (default 2275207), and
  `--ground-asset-id` the asset for the bare-earth heights (default 1, Cesium World Terrain).

### Other options

| Option | What it does |
|---|---|
| `--lat`, `--lon` | The world's origin. The default is the center of the extract's `<bounds>`. |
| `--emit-world-package DIR` | Where the package goes. The default is `Build/world-packages` from a source checkout, and `world-packages` under the current folder when installed. |
| `--no-clip-bounds` | Keep roads that run past the extract's `<bounds>`. |
| `--terrain-margin` | The width, in meters, of the staging ring just inside the map edge (default 30.48). |
| `--road-offset-east`, `--road-offset-north` | Slide the whole road network a few meters when the roads come out beside the roadway in the imagery. Measure the error first. Keep it under about 10 m. |
| `--save` | Where the elevated OpenDRIVE file goes. |
| `--log FILE` | Also write the console output to `FILE`, with timestamps. |
| `--timeout` | How long, in seconds, each server call of the build may take (default 300). |

## How long a build takes

A build takes minutes, and the time varies from one build to the next. On the development machine,
seven drape builds of the Arapahoe I-25 world (about 1.9 km by 0.95 km) took from 25 seconds to 8
minutes; five of them took between 1 and 3.5 minutes. Three drape builds of the Gardnerville world
(1.7 km by 0.9 km) took from 1 to 2.2 minutes.

A larger area takes longer. The Shahid Bahonar port world's ground grid has about 16 times as many
points as Arapahoe's. The drape cache skips the terrain sampling on a rebuild of the same area.

## What comes out

- **The world package**, `<MapName>.cwp`, in the `--emit-world-package` folder.
- **The intermediate files**, in `Build/sumo-smoketest/` from a source checkout (`sumo-smoketest/`
  under the current folder when installed): `<name>_clipped.osm`, the extract cut at its bounds, and
  `<name>_elevated.xodr`, the OpenDRIVE file with heights.
- **The world, loaded on the server.** You can drive SUMO traffic in it right away (see
  [Writing a scenario](Writing_A_Scenario.md#running-and-looking-at-it)).

To check a package, run `carla-validate Build/world-packages/<MapName>.cwp`. It checks every entry
against its schema, and the grids and digests against the manifest.

To find the network fingerprint a scenario must name, read `NetworkFingerprint` in the package's
`world.json`:

```
python -c "import json, zipfile; print(json.loads(zipfile.ZipFile('Build/world-packages/Gardnerville_Centerville_Lane.cwp').read('world.json'))['NetworkFingerprint'])"
```

## Building a world again

Each build writes a new network. When its fingerprint differs from the one a scenario names, the
compiler refuses the scenario (check 1) and names the package's fingerprint. Copy it into the
scenario's `world.network_fingerprint`, compile again, and read the resolution report: an edge or
lane your places named may have moved or changed its id.

## Refreshing a package's areas

`carla-publish-reference-set` publishes the authoring reference set into an existing package: the
areas of interest, the place index and the solar frame. It needs no CARLA server. It starts SUMO on
the package's own network to place the areas, and closes it.

Use it when you add or edit areas after the world is built, or for a world built before the reference
set existed. An area edit changes no road, so it is no reason to build the world again.

```
carla-publish-reference-set --package Build/world-packages/Gardnerville_Centerville_Lane.cwp --aoi Import/Gardnerville_Centerville_Lane.aoi.geojson
```

| Option | What it does |
|---|---|
| `--package` | The world package to publish into. Required. |
| `--aoi` | The areas file. Without it: `<name>.aoi.geojson` beside the extract, then the areas the package already carries, then none. |
| `--osm` | The extract the world was built from, whose `<bounds>` every area must reach into. The default is `Import/<MapName>.osm` from a source checkout, and `<MapName>.osm` in the current folder when installed. Without an extract, the network's own extent is used. |
| `--output` | Publish into a copy at this path and leave `--package` as it is. |
| `--near-m` | How close, in meters, a lane must pass to count as near an area. |
| `--sumo-home` | The SUMO installation to place the areas with. |
| `--allow-version-mismatch` | Continue when that SUMO is not the release that built the world. |

Publishing replaces the whole reference set. The command exits 1 when the areas file has a problem
or an area cannot be placed, and names every problem. A file with a problem publishes nothing. When
the file is sound but an area cannot be placed, the place index and the solar frame are still
published.

After you publish new areas, compile your scenarios again. A place written as an area is resolved
against the areas in the package.

## When the build stops

The build stops before building anything when:

- no `--osm` was given (from an installed command), or the extract is not found;
- netconvert is not found;
- the areas file or the type map has a problem, or the type map is given together with
  `--type-files`.

It stops with exit status 1 when the world was built but the package could not be written. The line
before says why.

## Next: making a level

To turn a world package into an Unreal level, see [Making a level](Making_A_Level.md).
