# OSM → OpenDRIVE → World: Georeferencing & Origin Pinning

How CarlaNet converts an OpenStreetMap extract into a runtime CARLA world, and how it
**pins a chosen real-world coordinate to the world origin (0,0)** so the simulation stays
georeferenced (and round-trips cleanly to Cesium).

Code: [`CarlaNet.Map.OsmConverter`](../src/CarlaNet.Map/OsmConverter.cs),
[`CarlaClient.GenerateWorldFromOsmAsync`](../src/CarlaNet.Transport/CarlaClient.cs).

---

## The two stages (recap)

1. **OSM → OpenDRIVE (`.osm` → `.xodr`)** — done by shelling out to SUMO `netconvert`
   (bundled, built from SUMO v1_27_0). Pure CPU text transform; no engine.
2. **OpenDRIVE → runtime world** — `GenerateOpenDriveWorldAsync` ships the `.xodr` text to
   the server (`copy_opendrive_to_file`) and loads the special `OpenDriveMap` level. Works in
   cooked/packaged builds.

This document is about getting stage 1's **coordinate frame** right.

---

## The problem: a bare projection auto-centres on the bounding box

netconvert projects OSM lat/lon into metric XY using a PROJ string. With a **bare**
`--proj "+proj=tmerc"`, two things happen that break a stable origin:

1. The transverse-Mercator projection has **no fixed origin** — PROJ picks defaults.
2. netconvert then applies an **offset normalization** that shifts the whole network so its
   bounding-box minimum corner sits at `(0,0)`.

Result: the world origin is "the south-west corner of whatever you happened to select." Pan
your OSM selection by a block and the origin moves. The `<geoReference>` ends up without a
usable origin, so you **cannot** convert a world `(x,y)` back to real-world lat/lon. The
header bounds are the tell: `west="0.00" south="0.00"` means "corner at origin."

## The fix: pin the projection origin AND disable normalization (a package deal)

We hand netconvert a **fully-specified** transverse-Mercator projection whose origin
(`lat_0`/`lon_0`) is the chosen point, and we turn normalization **off** so that origin is
not shifted:

```
--proj "+proj=tmerc +lat_0=<LAT> +lon_0=<LON> +k=1 +x_0=0 +y_0=0 +ellps=WGS84 +units=m +no_defs"
--offset.disable-normalization
```

- `lat_0`/`lon_0` = the semantic origin → that lat/lon projects to `(0,0)` by definition of
  the projection.
- `x_0=0 y_0=0` = no false easting/northing.
- `--offset.disable-normalization` = **mandatory**. Without it netconvert re-shifts the map
  and the pinned origin lands somewhere else. **Origin-pinning and disable-normalization must
  always travel together.**

After this, the header bounds **straddle zero** (the origin is interior), and the
`<geoReference>` CDATA carries the full proj string — which is exactly what makes the
world↔WGS84 round-trip well-defined.

---

## How to use it from CarlaNet

`OsmConversionOptions` exposes the origin as two nullable fields. Set **both** and the
converter builds the pinned proj string and forces normalization off automatically
(overriding `ProjString` and `CenterMap`):

```csharp
var opts = new OsmConversionOptions
{
    OriginLatitude  = 41.94813,   // Wrigley Field home plate
    OriginLongitude = -87.65593,
    // DefaultLaneWidth 3.35, DefaultSidewalkWidth 2.80, GenerateTrafficLights true … (CARLA defaults)
};
await client.GenerateWorldFromOsmAsync("Import/Maps/WrigleyVille.osm", opts);
```

**Design intent:** the caller chooses a semantic origin (a landmark); the OSM bounding-box
selection can be drawn **anywhere** and need not be centred on it. This deliberately removes
the tedium of "center your selection on your intended origin." See *Complexities* below for
what that convenience costs.

If `OriginLatitude`/`OriginLongitude` are left null, behaviour is unchanged: bare `ProjString`
with `CenterMap` controlling normalization (legacy CARLA-osm2odr-like auto-centring).

---

## How to VERIFY home plate lands at (0,0)

Three independent checks (all confirmed for WrigleyVille on 2026-06-03):

### 1. PROJ CLI round-trip — the decisive proof
PROJ ships its own `proj.exe` in the SUMOLibraries bundle
(`Build/SUMOLibraries/proj-9.5.0/bin/proj.exe`). Set `PROJ_LIB` to the proj data dir, then:

```powershell
$p = @("+proj=tmerc","+lat_0=41.94813","+lon_0=-87.65593","+k=1","+x_0=0","+y_0=0","+ellps=WGS84","+units=m","+no_defs")
# forward: lon lat -> x y   (expect 0 0)
"-87.65593 41.94813" | & proj.exe $p          # ->  0.00    0.00
# inverse: x y -> lon lat   (expect home plate)
"0 0" | & proj.exe -I $p                        # ->  87d39'21.348"W   41d56'53.268"N
```
`87°39'21.348"W = -87.65593`, `41°56'53.268"N = 41.94813`. Exact, both directions.

> Note `proj` reads **lon lat** (x then y), not lat lon.

### 2. Header bounds straddle zero
```
<header … north="702.93" south="-703.76" east="715.24" west="-678.93">
```
Negative `west`/`south` ⇒ the origin is interior (≈700 m to each edge), not a corner. A bare
auto-centred run instead shows `west="0.00" south="0.00"`.

### 3. geoReference carries the origin
The `<geoReference>` CDATA must contain `+lat_0=41.94813 +lon_0=-87.65593`. If it's a bare
`+proj=tmerc`, the origin was **not** pinned.

---

## Units / axes (CARLA + Cesium)

- OpenDRIVE coordinates are **metres**, right-handed, Z up.
- CARLA/UE world is **centimetres**, left-handed (Y flipped). CARLA's OpenDRIVE loader applies
  the m→cm scale and axis flip, so xodr `(0,0)` → UE `(0,0,0)`. With a pinned origin, **home
  plate is the UE world origin too** — which matches the intended procedural-generation usage.
- **Z / elevation origin is separate** from the tmerc x/y origin. tmerc pins horizontal
  position only; vertical datum/height handling is a TODO (see Open questions).

## Cesium round-trip

Because `<geoReference>` records the full pinned projection, any CARLA world `(x,y)` →
inverse-project through that string → WGS84 lat/lon → Cesium. The origin being captured
(rather than auto-centred) is precisely what makes this bridge deterministic.

---

## Complexities the "pick any origin" convenience invites (to investigate)

- **tmerc distortion grows with east-west distance from `lon_0`.** Fine at city scale
  (≤ a few tens of km); for very wide extents scale error at the edges becomes non-negligible.
- **Origin far outside the extent** is legal but means all coordinates carry a large constant
  offset — watch float precision if the origin is very far from the data.
- **Elevation:** no vertical origin is pinned yet; flat or OSM-tag-derived heights only.
- **Antimeridian / UTM-zone-spanning extents** need care (not relevant to single-city tiles).
- **CARLA parser acceptance:** SUMO emits OpenDRIVE rev 1.4 (`<header revMajor="1"
  revMinor="4">`); confirm CARLA's runtime `OpenDriveMap` parser is happy with SUMO's dialect
  on real maps (geometry, junctions, signals).
- **Flag-mapping gaps** (separate doc `NETCONVERT_INTEGRATION.md`): `all_junctions_traffic_lights`,
  OSM highway-type filtering, sidewalk import vs guess — tune against CARLA osm2odr output.

## Open questions

1. Should the **UE streaming origin / world-partition** also be anchored to the geo origin for
   large tiles, or only the OpenDRIVE frame?
2. Vertical datum: do we need real elevation (e.g. from a DEM) pinned to the same origin for
   the Cesium digital-twin blend?
3. Per-map persisted profile (origin + flags) vs. per-call options — where should the chosen
   origin live so a given map always regenerates identically?

---

*Verified end-to-end on Windows, 2026-06-03: WrigleyVille.osm (2.9 MB) → OpenDRIVE 1.4,
2646 roads / 315 junctions, home plate (41.94813, -87.65593) → (0,0) confirmed via PROJ CLI.*
