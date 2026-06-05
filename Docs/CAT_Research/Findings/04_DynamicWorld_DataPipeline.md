# Dynamic Georeferenced World — DATA-PIPELINE side of the elevation chicken-and-egg

Research scope: the **data side** of injecting Cesium-sampled elevation into a dynamically
generated, georeferenced CARLA world. Engine-side Cesium height sampling is being researched
separately; this doc assumes the engine can be handed a list of `(lat, lon)` points and will
return ground heights. Every claim is grounded in source `file:line` or SUMO docs.

Date: 2026-06-05. Author: data-pipeline research agent.

---

## 0. TL;DR

- The circular dependency is **breakable cheaply**: we can parse the flat `.xodr` *in-process*
  with the existing `CarlaNet.Map` road model **before loading any world**, walk every road's
  centerline at fixed `s` steps to get world `(x,y,z)`, convert `(x,y)`→`(lat,lon)`, hand those
  to the engine, get heights back, and write them into the `.xodr` `<elevation>` records.
- **The road model already consumes `<elevation>` correctly** for both the road *mesh* and the
  *waypoint z*. Injection is purely a `.xodr` text rewrite; no engine/road-model change needed.
- **CarlaNet.Map is parser-only — there is no `.xodr` serializer.** But we do **not** need a
  full serializer: elevation injection is a *targeted XML rewrite* of `<elevationProfile>`
  sub-trees, which is far simpler and safer (preserves netconvert's exact output for everything
  else). This is the recommended approach.
- **netconvert can import elevation** (`--osm.elevation`, `--osm.layer-elevation`,
  `--heightmap.geotiff`, `--heightmap.shapefiles`) but **none of these read Cesium** — they read
  OSM tags or a raster DEM. A DEM path reintroduces the exact "DEM disagrees with Cesium" problem
  the feasibility doc rejected (Option 3). So netconvert elevation is **not** the answer; we
  post-process the `.xodr`.
- **Biggest risk (see §9):** a *projection/datum mismatch*. netconvert pins horizontal position
  with `+proj=tmerc` (lat_0/lon_0), but CARLA's runtime `GeoLocation::Transform` uses **spherical
  Web-Mercator**, and the elevation we sample from Cesium is an **ellipsoidal/geoid height** with
  no vertical datum pinned. If the `(lat,lon)` we feed Cesium is computed with a different formula
  than the one that defines where the road actually renders, the sampled height lands at the wrong
  spot → roads still float/sink. This must be resolved before any of the rest matters.

---

## 1. OSM bounds + road sample points, early & cheap

### 1a. OSM `<bounds>` — available before conversion, but we mostly don't need it
The OSM file carries a `<bounds minlat=… minlon=… maxlat=… maxlon=…>` tag. Reading it is a
trivial XML scan of the `.osm` (no engine, no netconvert). It is useful as a *coarse* AABB to
prefetch / pre-stream Cesium tiles for the region, but it is **not** the sample set — it's just
the corners.

### 1b. The xodr `<header>` bounds — produced by netconvert, post-conversion
After conversion, `OsmConverter` returns the `.xodr` text whose `<header>` carries
`north/south/east/west` in **projected metres** (e.g. `OSM_Georeferencing.md:108-112`,
`<header … north="702.93" south="-703.76" east="715.24" west="-678.93">`). With a pinned origin
these straddle zero. This is a metric AABB of the road network in world frame — handy for a
tile-prefetch bounding box, again not the sample set.

### 1c. The real win — enumerate per-road centerline points IN PROCESS, before world load
This is the crux. CarlaNet already contains a complete in-process OpenDRIVE road model and the
**exact same s→world-position evaluation** that the runtime/TM uses. We can drive it on the flat
`.xodr` with **no server and no world load**:

- `CarlaNet.Map.OpenDrive.OpenDriveParser.Load(xodr)` → `CarlaNet.Map.Road.Map`
  (`OpenDriveParser.cs:19-46`). Pure CPU text→object; this is what `TrafficManager.FetchOrBuildMap`
  already does (`CarlaNet.TrafficManager/TrafficManager.cs:91`,
  `var parsedMap = OpenDriveParser.Load(xml)`).
- `Map.Roads` exposes every `Road` (`Road/Map.cs:31`), each with `Road.Length` (`Road/Road.cs:21`).
- For any `s`, `Map.GetDirectedPointIn(road, s)` (`Road/Map.cs:228-252`) evaluates the active
  geometry primitive (`Geometry.PosFromDist`, e.g. `Geom/GeometryLine.cs:9-15`) and returns the
  road-center world `(x,y,z)` + tangent + pitch. On a flat `.xodr` z=0 there, which is fine — we
  only need `(x,y)`.
- The proven loop already exists in `InMemoryMap.BuildSegmentMap`
  (`CarlaNet.TrafficManager/InMemoryMap.cs:206-250`): `for (double s = EPS; s < road.Length - EPS;
  s += step)` then `_worldMap.ComputeTransform(wp)` → `Transform.Location`. That is precisely a
  per-road, per-`s` enumeration of world points. We don't even need lane fan-out for elevation —
  the road **centerline** (lane 0 reference line via `GetDirectedPointIn`) is the right sample set,
  because `<elevation>` is a *road-level* profile (one per road, applied to all lanes —
  `Road/Map.cs:244-250` reads it as a road-level `RoadInfoElevation`).

So the early & cheap sample-point producer is: **parse flat `.xodr` → for each road, sample
`GetDirectedPointIn(road, s)` at `s = 0, step, 2·step, …, Length`** → list of world `(x,y)` per
`(roadId, s)`.

### 1d. world `(x,y)` → `(lat,lon)` for Cesium
We have the projection in two places:
- The `<geoReference>` PROJ string (the pinned `+proj=tmerc +lat_0 +lon_0`), parsed today by
  `GeoReferenceParser` — but it only extracts `lat_0`/`lon_0` into a `GeoLocation`
  (`OpenDrive/Parser/GeoReferenceParser.cs:29-58`); it does **not** keep the full proj string or
  do inverse projection.
- `carla::geom::GeoLocation::Transform` (LibCarla `geom/GeoLocation.cpp:66-73`) — the runtime
  local→geo transform CARLA actually uses. **It is spherical Web-Mercator** (`EARTH_RADIUS_EQUA =
  6378137`, `LatLonToMercator`/`MercatorToLatLon`, `geom/GeoLocation.cpp:22-48`), with the Y-flip
  `location.x, -location.y` and `alt = altitude + z`. **This is NOT ported into CarlaNet** (the
  feasibility doc flags this exact gap: `CARLA_CESIUM_DIGITAL_TWIN_FEASIBILITY.md:196-200`).

**Implication:** there are two candidate `(x,y)→(lat,lon)` functions and they disagree away from
the origin (spherical Mercator vs the tmerc that actually placed the roads). See §9 — this is the
top risk and must be decided first. The sample points we feed Cesium MUST be computed with the
projection that matches where the road geometry physically sits, else heights land off-target.

---

## 2. OpenDRIVE elevation representation + how CARLA consumes it

### 2a. Representation
`<elevationProfile>` holds one or more `<elevation s a b c d>` records; each is a cubic
`z(ds) = a + b·ds + c·ds² + d·ds³`, `ds = s − record_s`, keyed at start-`s` along the road.
Confirmed by a real CARLA xodr:
`Town01.xodr … <elevationProfile><elevation s="0.0…" a="0.0…" b… c… d…/></elevationProfile>`.
SUMO emits exactly one zero record per road by default (all-zero → flat), which is what we
overwrite.

CarlaNet model: `RoadInfoElevation` wraps a `CubicPolynomial(a,b,c,d,s)`
(`Road/Element/RoadInfoElevation.cs:7-16`); `CubicPolynomial.Evaluate`/`Tangent`
(`Geom/CubicPolynomial.cs:48-51`).

### 2b. Both MESH and WAYPOINT z follow the profile — confirmed in LibCarla
The single chokepoint is `Road::GetDirectedPointIn(s)` (LibCarla `road/Road.cpp:184-204`):
```cpp
const auto elevation_info = GetElevationOn(s);
p.location.z = static_cast<float>(elevation_info.Evaluate(s));
p.pitch      = elevation_info.Tangent(s);
```
- **Waypoint z**: `Lane::ComputeTransform(s)` (`road/Lane.cpp:131-180`) calls
  `road->GetDirectedPointIn(s)` (`Lane.cpp:180`) → carries the elevation `z` into the waypoint
  transform. (CarlaNet mirrors this in `Map.ComputeLaneTransform` →
  `GetDirectedPointIn`, `Road/Map.cs:114-115, 244-250`.)
- **Road mesh**: `MeshFactory.cpp` builds road geometry from `road.GetDirectedPointIn(s_current)`
  (`road/MeshFactory.cpp:891, 991`) — so the visible/collision mesh vertices inherit the same `z`.
- Bonus: `MeshFactory` even checks elevation `c`/`d` coefficients to decide flatness
  (`MeshFactory.cpp:88-93`).

**Conclusion:** injecting non-zero `<elevation>` records is sufficient and complete — the
generated mesh *and* the CarlaNet/TM waypoint z both follow the injected profile. No road-model
change required. (Matches feasibility doc §4.3 Option 1, lines 136-154.)

---

## 3. Does SUMO netconvert support elevation input?

Yes — several ways, **none of which read Cesium**:

- **`--osm.elevation`** — import z from OSM `ele`/node elevation tags. Users report z is often not
  emitted even with it set; OSM elevation data is sparse. (SUMO docs: Networks/Elevation,
  Networks/Import/OpenStreetMap.)
- **`--osm.layer-elevation`** (+ `--osm.layer-elevation.max-grade`) — heuristic z from `layer`
  tags (bridges/tunnels), NOT real terrain; "manual correction may be necessary".
- **`--heightmap.geotiff`** — apply z from a greyscale GeoTIFF DEM raster.
- **`--heightmap.shapefiles`** — z from a shapefile mesh.
- Native z on **OpenDRIVE/Shapefile import** and in `*_edg.xml` shape definitions.

The SUMO Elevation page documents these as *import/assignment* paths; it does **not** document
whether z round-trips into the OpenDRIVE `<elevation>` export, but since netconvert keeps z as a
network attribute it will emit an `<elevationProfile>` from it. **Regardless, this is the wrong
tool for us:** the only "real terrain" netconvert path is a GeoTIFF DEM, and the feasibility doc
explicitly rejected an *independent DEM* (Option 3) because it disagrees with Cesium's own terrain
and is too coarse for individual streets (`CARLA_CESIUM_DIGITAL_TWIN_FEASIBILITY.md:145-159,
163-166`). The whole point of "sample Cesium" (Option 1) is contradiction-free-by-construction.
**→ We must post-process the `.xodr`, not feed netconvert a heightmap.**

Sources: [SUMO Elevation](https://sumo.dlr.de/docs/Networks/Elevation.html),
[SUMO OSM import](https://sumo.dlr.de/docs/Networks/Import/OpenStreetMap.html),
[netconvert](https://sumo.dlr.de/docs/netconvert.html).

---

## 4. Elevation injection path (post-conversion `.xodr` rewrite)

### 4a. There is NO xodr serializer in CarlaNet.Map — only a parser
Confirmed: `CarlaNet.Map` has `OpenDriveParser` + a tree of `Parser/*Parser.cs` and a `MapBuilder`
that *constructs* the in-memory `Map`. There is **no** `ToXodr`/`Serialize`/`Write` anywhere in
`src/CarlaNet.Map` (grep for serialize/ToXodr/WriteXodr/ToOpenDrive/Serializer → no hits).
`MapBuilder.Build()` returns a `Map`, never text (`Road/MapBuilder.cs:39-67`).

### 4b. We do NOT need a full serializer — do a targeted XML rewrite
A full round-trip serializer would have to faithfully re-emit geometry, lanes, junctions,
signals, controllers, objects — high risk of diverging from netconvert's accepted dialect (and
CARLA's runtime parser is picky; `OSM_Georeferencing.md:145-147`). Instead:

1. Load the `.xodr` as an `XDocument` (the parser already uses `System.Xml.Linq`,
   `OpenDriveParser.cs:6, 24`).
2. Build, per `roadId`, the elevation polynomial(s) from the sampled heights (see §4c).
3. For each `<road id=…>`, replace (or create) its `<elevationProfile>` child with the new
   `<elevation s a b c d/>` records, leaving **everything else byte-for-byte** as netconvert
   wrote it.
4. Serialize the `XDocument` back to a string and hand it to the existing
   `GenerateOpenDriveWorldAsync` (`CarlaClient.cs:147-156`).

This touches only the `<elevationProfile>` subtree — minimal blast radius, preserves the
geoReference/header/geometry exactly.

### 4c. Heights → cubic `<elevation>` records (the fitting step)
We have, per road, a set of `(s_i, z_i)` (z_i = Cesium ground height at that centerline point,
adjusted for vertical datum — see §9). Options, simplest first:
- **Piecewise-linear** (`b` = local grade, `c=d=0`): one `<elevation>` record per sample
  interval. `z(ds) = z_i + ((z_{i+1}−z_i)/Δs)·ds`. Robust, monotone-safe, trivially exact at
  samples. Recommended for v1. Produces N records per road (fine; CARLA reads N records natively —
  `Town01.xodr` already has multiple per road).
- **Cubic spline / least-squares cubic fit** per road — smoother grade but can overshoot; defer.
  CarlaNet's `CubicPolynomial.Set(a,b,c,d,s)` (`Geom/CubicPolynomial.cs:38-45`) already shifts a
  polynomial to a record-`s` origin, so emitting properly-keyed records is straightforward.

Note an important subtlety: an `<elevation>` record's coefficients are evaluated with
`ds = s − record_s` in CARLA (`Road.cpp:200` evaluates at *absolute* `s` via the polynomial that
was constructed shifted to `record_s` — see `CubicPolynomial.Set(…, s)`). So when emitting,
write `a = z_i`, `b = grade`, `s = s_i`, and ensure the consumer's `Evaluate(s)` reproduces `z_i`
at `s = s_i`. The piecewise-linear form makes this unambiguous.

### 4d. Where the code lives
New static class, e.g. `CarlaNet.Map.OpenDrive.ElevationInjector`:
- `IReadOnlyList<RoadSample> ExtractCenterlineSamples(string xodr, double stepMeters)` — parses
  with `OpenDriveParser.Load`, walks roads via `GetDirectedPointIn`, returns
  `(RoadId, s, worldX, worldY)`. (Reuses the §1c loop.)
- `string InjectElevation(string xodr, IReadOnlyDictionary<(RoadId,double s), double z>)` — the
  §4b XDocument rewrite.
The `(x,y)→(lat,lon)` projection helper (the missing Mercator/tmerc inverse, §1d/§9) is a third
piece — put it in `CarlaNet.Types.Geom` next to `GeoLocation` or a new `Georef` helper, ported
from `GeoLocation.cpp` (or, preferably, a proper tmerc inverse matching the pinned proj string).

---

## 5. CarlaNet API surface + proposed restructure

### 5a. Current one-shot flow (the thing to break apart)
Python `client.generate_world_from_osm(osm, osm_options)`
(`python/carlanet/__init__.py:1515-1528`)
→ C# `CarlaClient.GenerateWorldFromOsmAsync` (`CarlaClient.cs:160-170`):
```csharp
var xodr = await new OsmConverter(osmOptions).ConvertFileAsync(osmPath, ct);  // OSM→xodr (netconvert)
await GenerateOpenDriveWorldAsync(xodr, parameters, resetSettings);            // xodr→server→load
```
→ `GenerateOpenDriveWorldAsync` (`CarlaClient.cs:147-156`) =
`CopyOpenDriveToServerAsync` (RPC `copy_opendrive_to_file`, `CarlaClient.cs:136-137`)
+ `LoadEpisodeAsync("OpenDriveMap")`.
`OsmConverter.ConvertFileAsync` (`OsmConverter.cs:85-103`) writes the xodr to a temp file, reads
it back, and **deletes it** — so the intermediate is currently thrown away.

### 5b. Proposed multi-pass API (expose intermediate artifacts)
Keep `OsmConverter` and `GenerateOpenDriveWorldAsync` as-is; add new building-block methods and
make `GenerateWorldFromOsmAsync` an *opt-in* one-shot that callers can bypass.

New C# (CarlaNet.Map / CarlaNet.Transport):

```csharp
// CarlaNet.Map.OsmConverter — already returns the xodr text; just USE it (don't discard).
//   existing: Task<string> ConvertFileAsync(osmPath, ct)            // PASS 1 (OSM→flat xodr)

// CarlaNet.Map.OpenDrive.ElevationInjector  (NEW)
public readonly record struct RoadSample(uint RoadId, double S, double WorldX, double WorldY);
public readonly record struct GeoSample(uint RoadId, double S, double Lat, double Lon);

static IReadOnlyList<RoadSample>  ExtractCenterlineSamples(string xodr, double stepMeters = 5.0);
static IReadOnlyList<GeoSample>   ToGeo(IReadOnlyList<RoadSample> samples, string xodr);   // uses geoReference
static string InjectElevation(string xodr,
        IReadOnlyDictionary<(uint RoadId, double S), double> heightsMeters,
        ElevationFitMode mode = ElevationFitMode.PiecewiseLinear);

// CarlaNet.Transport.CarlaClient  (NEW thin wrapper, optional)
Task GenerateWorldFromElevatedXodrAsync(string elevatedXodr, …);   // == GenerateOpenDriveWorldAsync
```

The height hand-off itself (PASS 2) is **engine-side**: CarlaNet produces `GeoSample[]`
(lat/lon per `(roadId,s)`), the engine returns `double[]` heights aligned by index (plus
per-point success flags, mirroring Cesium's `SampleHeightMostDetailed` result —
`CARLA_CESIUM_DIGITAL_TWIN_FEASIBILITY.md:141-145`). CarlaNet zips them back into the
`(roadId,s)→z` dictionary for `InjectElevation`.

### 5c. How the Python call looks (orchestrated 4-pass)
```python
import carlanet as carla
from CarlaNet.Map import OsmConverter, OsmConversionOptions
from CarlaNet.Map.OpenDrive import ElevationInjector

opts = OsmConversionOptions(); opts.OriginLatitude=41.94813; opts.OriginLongitude=-87.65593
# PASS 1 — OSM → flat xodr (no server)
flat_xodr = OsmConverter(opts).ConvertFileAsync(osm).GetAwaiter().GetResult()
samples   = ElevationInjector.ExtractCenterlineSamples(flat_xodr, 5.0)   # (roadId,s,x,y)
geo       = ElevationInjector.ToGeo(samples, flat_xodr)                  # (roadId,s,lat,lon)
# PASS 2 — engine samples Cesium heights for [(lat,lon), …]  (separate agent's tool)
heights   = engine.sample_cesium_heights([(g.Lat, g.Lon) for g in geo]) # aligned by index
hmap      = { (g.RoadId, g.S): h for g, h in zip(geo, heights) }
# PASS 3 — inject elevation into the xodr (pure text rewrite)
elevated  = ElevationInjector.InjectElevation(flat_xodr, hmap)
# PASS 4 — load the elevated world
client = carla.Client(host, port)
client.generate_opendrive_world(elevated)        # existing path (CarlaClient.cs:147)
```
Keep `generate_world_from_osm` as the flat/legacy convenience; add an orchestration helper
(`generate_world_from_osm_with_elevation(osm, opts, height_sampler_callback)`) once the engine
exposes the sampler, so Python users get a one-liner again but with the multi-pass under the hood.

---

## 6. Already solved? — existing elevation hooks

- **Consumption side: fully solved.** The road model reads `<elevation>` for mesh and waypoints
  (§2). `RoadInfoElevation`, `CubicPolynomial`, `GetDirectedPointIn`, `ComputeTransform`,
  `ProfilesParser` all exist and are wired.
- **Production side: not started.** `ProfilesParser` only *reads* elevation and *defaults to a
  zero record* when absent (`OpenDrive/Parser/ProfilesParser.cs:43-47`). `OsmConverter` has **no**
  elevation option and discards the xodr file. No serializer/injector exists. The feasibility doc
  and `OSM_Georeferencing.md:143, 151-159` both list elevation as an open TODO ("no vertical
  origin is pinned yet; flat or OSM-tag-derived heights only").
- **Projection inverse: not ported.** `GeoReferenceParser` keeps only `lat_0/lon_0`; the full
  `(x,y)→(lat,lon)` math (`GeoLocation::Transform`) is **absent from CarlaNet**
  (`CARLA_CESIUM_DIGITAL_TWIN_FEASIBILITY.md:196-200`).

---

## 7. Proposed ordered DATA sub-pipeline (dependency graph)

```
            ┌──────────────────────────────────────────────────────────────┐
            │ INPUT: osm file + origin (lat0,lon0)                          │
            └──────────────────────────────────────────────────────────────┘
                                  │
   PASS 1 (data) ── OsmConverter.ConvertFileAsync(osm,opts) ──► flat .xodr  [no server]
        │   │  (netconvert: +proj=tmerc +lat_0/+lon_0, --offset.disable-normalization)
        │   └──► (cheap) read OSM <bounds> + xodr <header> AABB ─► tile prefetch hint (engine)
        ▼
   PASS 1b (data) ── ElevationInjector.ExtractCenterlineSamples(flat_xodr, step)
        │   = OpenDriveParser.Load → per road, GetDirectedPointIn(s) for s in [0..Length]
        ▼  RoadSample[] : (roadId, s, worldX, worldY)
   PASS 1c (data) ── ElevationInjector.ToGeo(samples, flat_xodr)   ⚠ uses the §9 projection
        ▼  GeoSample[] : (roadId, s, lat, lon)        ───────────────┐
                                                                     │  hand-off (lat,lon)[]
   ════════════════════════════════════ ENGINE BOUNDARY ════════════╪═══════════════════════
                                                                     ▼
   PASS 2 (engine) ── sampleCesiumHeights( (lat,lon)[] )  [ASYNC; needs tileset
        │              georeferenced + streamed to LOD ]            (separate agent)
        ▼  heights[] aligned by index (+ success flags)  ───────────┐
   ════════════════════════════════════ ENGINE BOUNDARY ════════════╪═══════════════════════
                                                                     ▼
   PASS 3 (data) ── zip → {(roadId,s): z}  ──► ElevationInjector.InjectElevation(flat_xodr, …)
        │            (XDocument rewrite of <elevationProfile> only; fit z→cubic records)
        ▼  elevated .xodr
   PASS 4 (data) ── CarlaClient.GenerateOpenDriveWorldAsync(elevated_xodr)
                    = copy_opendrive_to_file + LoadEpisode("OpenDriveMap")   [server]
        ▼
   RESULT: world whose road mesh + waypoints sit at Cesium ground height
```

Hard dependencies: 2 needs 1c (the lat/lon points); 3 needs 2 (the heights) AND 1 (the xodr to
rewrite); 4 needs 3. Pass 1b/1c need only the flat xodr (1), no server. The tile-prefetch hint
(from OSM bounds / xodr header) can fire as soon as Pass 1 finishes, in parallel with 1b/1c, to
warm the Cesium stream before Pass 2's sampling.

---

## 8. Concrete change list (data side)

1. **Stop discarding the xodr** — already returned as text by `ConvertFileAsync`; the new flow
   keeps the string and never needs the legacy one-shot to delete it. (No change to OsmConverter
   needed beyond reuse; optionally add a `ConvertToFileAsync` that leaves the file on disk for
   debugging.)
2. **New `CarlaNet.Map.OpenDrive.ElevationInjector`** with `ExtractCenterlineSamples`, `ToGeo`,
   `InjectElevation` (§4d, §5b). Reuses `OpenDriveParser`, `Map.GetDirectedPointIn`,
   `CubicPolynomial`.
3. **Port the projection inverse** (`(x,y)→(lat,lon)`) into CarlaNet — either spherical-Mercator
   parity with `GeoLocation::Transform` (LibCarla `geom/GeoLocation.cpp`) OR a proper tmerc
   inverse matching the pinned proj string. **Decide which (see §9) before writing `ToGeo`.**
   Extend `GeoReferenceParser` to retain the full proj string, not just lat_0/lon_0.
4. **New `CarlaClient` / Python orchestration** wrapper exposing the 4 passes + a callback-based
   one-liner once the engine sampler API is known (§5c).
5. **(Optional) tile-prefetch hint API** — surface the OSM `<bounds>` and xodr header AABB so the
   engine can pre-stream the region before Pass 2.

---

## 9. THE single biggest risk / unknown (data side): projection & vertical-datum coherence

There are **three** coordinate definitions in play and they are not currently guaranteed to agree:

1. **Where roads physically are**: netconvert places road `(x,y)` with the pinned
   `+proj=tmerc +lat_0 +lon_0` (`OsmConverter.cs:199-204`). This is the ground truth for geometry.
2. **What CARLA's runtime thinks lat/lon is**: `GeoLocation::Transform` uses **spherical
   Web-Mercator**, not tmerc (`geom/GeoLocation.cpp:38-48, 66-73`). Over a city block these agree
   sub-metre; over several km they diverge by tens–hundreds of metres
   (`CARLA_CESIUM_DIGITAL_TWIN_FEASIBILITY.md:186-194`).
3. **What Cesium height we get**: Cesium returns an **ellipsoidal/geoid-referenced** height for a
   `(lat,lon)`. CARLA's `.xodr` elevation `z` is a bare metric height with **no vertical datum
   pinned** (`OSM_Georeferencing.md:126-128, 143`). Real CARLA maps even carry
   `+geoidgrids=egm96_15.gtx +vunits=m` in their geoReference (observed in `TownBig.xodr` header) —
   our SUMO-generated geoReference does **not**.

**Why it's the top risk:** the elevation injection is only correct if the `(lat,lon)` we send to
Cesium for road point P is the lat/lon where P *actually renders*. If `ToGeo` uses spherical
Mercator while the road was placed by tmerc, the sampled point drifts (worse the farther from
origin), so we read Cesium's height at the wrong location → the road conforms to the *wrong* part
of the terrain → still floats/sinks, but now subtly and position-dependently (hard to debug).
Additionally, even with perfect horizontal lat/lon, a vertical-datum mismatch (ellipsoid vs
geoid vs CARLA's flat `altitude` offset) applies a *constant-ish but unknown* z bias.

**Mitigations / decisions needed (in priority order):**
- **Pick ONE horizontal projection for the whole loop** and use it for both road placement and
  Cesium sampling. Cleanest: do the inverse with the **same tmerc** string the xodr carries
  (port a tmerc inverse, or reuse PROJ via the bundled `proj.exe` already used for verification —
  `OSM_Georeferencing.md:93-103`), NOT spherical Mercator. Equivalently, the feasibility doc's
  preferred fix is to treat CARLA metres as a **local ENU tangent plane and let Cesium do
  ellipsoidal ENU→ECEF** (`…FEASIBILITY.md:190-194`) — that sidesteps both Mercator formulas.
- **Pin a vertical datum**: decide whether injected z is ellipsoidal height or orthometric, and
  apply the constant offset so road z and Cesium ground agree at the origin (a single calibration
  sample at the pinned origin can absorb the bulk of any constant bias).
- **Validate empirically**: sample one known point (the pinned origin) both ways and confirm the
  round-trip lands on itself, exactly as the existing PROJ CLI check does for horizontal
  (`OSM_Georeferencing.md:90-103`).

Secondary unknowns: (a) Cesium height sampling is async and LOD-bounded — sample precision depends
on tiles being streamed to detail at sample time (engine-side concern, but it gates Pass 3
quality); (b) per-point sample failures (water, missing tiles) need a fallback (interpolate from
neighbours / hold previous z) so InjectElevation never writes a NaN; (c) junction connecting-roads
must stay height-consistent with the roads they join — sampling their own centerlines should
handle this, but discontinuities at road boundaries may need smoothing.
