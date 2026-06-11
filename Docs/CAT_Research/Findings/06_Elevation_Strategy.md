# Elevation Strategy — Decouple Simulation Geometry from Telemetry Elevation

**Prepared:** 2026-06-08 · R&D plan of attack (no code changes; server not launched).
**Context:** The digital-twin pipeline georeferences a city from OSM, overlays Google Photorealistic
3D Tiles (ion 2275207), and must emit (a) per-vehicle georeferenced telemetry at ~5 Hz —
lat / lon / **elevation** / velocity — and (b) high-altitude **NADIR EO** video.

**Related:**
[../DYNAMIC_WORLD_PIPELINE_PLAN.md](../DYNAMIC_WORLD_PIPELINE_PLAN.md) ·
[04_DynamicWorld_DataPipeline.md](04_DynamicWorld_DataPipeline.md) ·
[05_DynamicWorld_EngineIntegration.md](05_DynamicWorld_EngineIntegration.md) ·
code: `CarlaNet.Map/OpenDrive/ElevationInjector.cs`, `CarlaNet.Types/Geom/Geodesy.cs`,
`CesiumCarlaBridge/.../CesiumHeightSampler.cpp`, `Carla/Server/CarlaServer.cpp`,
`CarlaNet.Transport/CarlaClient.cs`, `python/carlanet/__init__.py`.

---

## 1. The problem: we are sampling a SURFACE, not the ground

The current pipeline (`GenerateWorldFromOsmWithElevationAsync`, plan §2 P2→P6) calls
`ACesium3DTileset::SampleHeightMostDetailed` on the **Google photoreal tileset** and injects the
returned ellipsoidal height into the `.xodr` `<elevationProfile>` at each road-centerline sample.

Google Photorealistic 3D Tiles are a **photogrammetric SURFACE mesh** — a single skin draped over
*everything*: buildings, the CTA "L" elevated tracks, tree canopy, awnings, vehicles, bridge decks.
When a road centerline passes **under** any over-street structure, `SampleHeightMostDetailed` returns
the height of **that structure**, not the street. Result: roads spike up to rooftop / track height,
and cars driving the injected profile launch off the spike or fall into the gap on the far side.

`ElevationInjector.RejectOutliers` (the just-added mitigation) catches *isolated* spikes by rejecting
any sample more than `outlierThresholdMeters` (4 m) above **both** valid neighbours, then re-interpolates
the street level. This is a good band-aid but it is fundamentally fighting the data source:

- It fails where the over-street structure is **wider than the sample step** (10 m default) — a long
  run under the "L" or a multi-span overpass reads as a sustained plateau, not an isolated peak, so
  both neighbours are *also* elevated and nothing is rejected.
- It cannot distinguish a real steep grade from a structure edge at the threshold boundary.
- It is tuned per-scene (Chicago's "L"); it will not generalise.

**Root cause:** a surface mesh is the wrong instrument for a *bare-earth* (terrain-only) question.

### Why this is acceptable to fix by decoupling

The **primary deliverable is high-altitude NADIR EO video.** From directly overhead, a vehicle's small
vertical offset from the true ground (road kept flat vs. a few metres of real grade) projects to a
**negligible horizontal pixel error** — for a sensor at altitude *H* and a vehicle height error Δz,
the nadir ground-sample displacement is ~Δz·tan(off-nadir angle) ≈ 0 at true nadir and tiny near it.
The photogrammetry (which *does* carry real building/terrain relief) remains the visual truth in the
image; only the thin road ribbon and the cars on it sit at a slightly idealised height, which is
invisible from overhead.

**Telemetry elevation, however, must still be correct** (it is an explicit deliverable). So we
**decouple** the two:

| Concern | Source | Rationale |
|---|---|---|
| **Simulation geometry** (road mesh + vehicle Z) | **FLAT** (z = 0), or optional bare-earth Mode B | Stable driving, no spikes, no cars launched |
| **Telemetry elevation** (per-vehicle) | **Bare-earth DEM lookup** at vehicle lat/lon | Correct ground truth, independent of road mesh |
| **Visual scene** (EO imagery) | Google photoreal tileset (unchanged) | Real relief where it matters — in the picture |

This is the reframe being adopted. The rest of this document validates and details it.

---

## 2. Surface (Google) vs. bare-earth — what each height means

| Property | Google Photoreal 3D Tiles (ion 2275207) | Cesium World Terrain (ion **1**) | Bare-earth DEM (SRTM / DTED / 3DEP) |
|---|---|---|---|
| Geometry | Textured **surface** mesh (DSM-like) | Terrain mesh (**DTM**, bare-earth-ish) | Gridded bare-earth (**DTM**) |
| Includes buildings / trees / "L" | **Yes** (the bug) | No | No |
| Samplable via `SampleHeightMostDetailed` | Yes (in use) | **Yes — same RPC** | No (offline file read) |
| Vertical datum | **Ellipsoidal** (WGS84) | **Ellipsoidal** (WGS84) | SRTM≈EGM96 geoid; 3DEP/DTED = NAVD88/MSL (**orthometric**) |
| Coherent with our `Geodesy` ENU/ellipsoid | Yes | Yes | **No** — needs geoid-undulation correction (see §6) |

**Key insight (confirmed by Cesium docs):** Cesium World Terrain and Google Photoreal Tiles use
*different reference systems* and are "not recommended to be used together" visually — but that is a
**rendering** caveat, not a sampling one. For *height queries* both expose the same
`sampleHeight*` API. This is what makes the **two-tileset engine option** (§5) attractive: keep Google
visible, sample a **hidden Cesium World Terrain** tileset for bare-earth heights — zero new file I/O,
zero datum conversion (both ellipsoidal, already coherent with `Geodesy`).

---

## 3. Bare-earth DEM source comparison

| Source | Native res | Format | Datum (vertical) | US coverage | License | Offline ease | Notes |
|---|---|---|---|---|---|---|---|
| **SRTM 1-arc-sec (v3 "Void-Filled" / SRTMGL1)** | ~30 m | `.hgt` (raw 16-bit BE int, 3601×3601 per 1°×1° tile) | EGM96 geoid (orthometric) | 60°N–56°S (all CONUS) | Public domain (USGS/NASA) | **Trivial** — fixed-size raw grid, no parser lib | Best default. v3 voids filled w/ ASTER. |
| **DTED Level 0 / 1 / 2** | 900 m / 90 m / 30 m | `.dt0/.dt1/.dt2` (1°×1° grid, 16-bit signed, −32767=void; UHL/DSI/ACC headers) | MSL/EGM96 (orthometric) | DTED-0 global; 1/2 export-restricted | Mil/NGA; DTED-0 open | Easy (raw grid + ~3.4 KB header to skip) | Only adds value at L2 (=SRTM res). L0/L1 coarser. Avoid unless a DTED tile is already on hand. |
| **USGS 3DEP 1 m (lidar bare-earth)** | **1 m** | Cloud-Optimized **GeoTIFF** | NAVD88 (orthometric) | CONUS where lidar flown (Chicago: yes) | Public domain, no restrictions | **Hard** — needs a GeoTIFF reader + UTM/NAD83 reprojection | Highest fidelity. Tiles are UTM-projected, per-project, irregular footprints. |
| **USGS 3DEP 1/3 arc-sec seamless** | ~10 m | GeoTIFF / IMG | NAVD88 | Seamless CONUS | Public domain | Medium (GeoTIFF, but geographic grid) | Good middle ground if 1 m overkill. |
| **Cesium World Terrain (ion 1)** | ~variable (≤~10 m urban) | Served tiles (no local file) | **Ellipsoidal WGS84** | Global | Cesium ion (token already held) | **N/A offline** — sampled via engine RPC | Already coherent w/ Geodesy & Cesium georef. The §5 option. |
| **EPQS** (USGS Elevation Point Query Service) | dynamic (down to 1 m) | REST JSON (`x`,`y`,`units`) | NAVD88 | CONUS | Public, online | Online only | Great for *one-shot calibration / validation*, not 5 Hz batch (rate-limited, network). |

**Resolution adequacy for nadir EO:** at ~5 Hz a city vehicle moves ≤ ~6 m/sample; SRTM's 30 m post
spacing with bilinear interpolation gives sub-metre vertical error on typical urban grades — far below
what a nadir frame can resolve. **30 m SRTM is sufficient for the telemetry deliverable.** 3DEP 1 m is
"nice to have" for oblique/validation work, not required for v1.

**Recommendation:** **SRTM 1-arc-sec v3 (`.hgt`)** as the offline telemetry source. Public-domain,
dependency-free reader, global, exactly the precision the deliverable needs. Keep **3DEP GeoTIFF** as
an optional high-fidelity provider behind the same interface, and **Cesium World Terrain (ion 1)** as
the engine-side alternative (§5).

---

## 4. Recommended telemetry-elevation architecture

### 4.1 Where it lives — new module `CarlaNet.Geo`

Create a new project **`CarlaNet.Geo`** (sibling to `CarlaNet.Map`). Rationale:

- The DEM lookup is **pure data** — no OpenDRIVE, no transport, no engine. It must be usable from the
  telemetry path *without* dragging in `CarlaNet.Map`'s OpenDRIVE parser.
- It depends only on `CarlaNet.Types` (for `GeoLocation` and `Geodesy`).
- `CarlaNet.Map.OpenDrive.ElevationInjector` (Mode B, §7) and the telemetry emitter both consume it.

(If a new project is unwanted, the second-best home is `CarlaNet.Types/Geo/` since `Geodesy` already
lives in `CarlaNet.Types.Geom` — but a dedicated module keeps the DEM file-reading dependencies and any
optional GeoTIFF package out of the always-loaded Types assembly.)

### 4.2 Interface + classes

```csharp
namespace CarlaNet.Geo;

/// Bare-earth elevation provider: lat/lon -> ground height (metres).
public interface IElevationSource
{
    /// Ellipsoidal height (WGS84) at the point, or double.NaN if outside coverage.
    /// Implementations that read an orthometric DEM apply the geoid correction
    /// internally so callers always get ELLIPSOIDAL metres (coherent with Geodesy/Cesium).
    double SampleEllipsoidal(double latitudeDeg, double longitudeDeg);
}

/// Reads NASA SRTM .hgt tiles (1- or 3-arc-second) from a directory, bilinearly
/// interpolated, with on-demand tile load + LRU cache. Dependency-free.
public sealed class SrtmElevationSource : IElevationSource { ... }

/// Optional: reads USGS 3DEP / generic GeoTIFF DEMs (needs a GeoTIFF reader, §4.4).
public sealed class GeoTiffElevationSource : IElevationSource { ... }

/// Optional online fallback: USGS EPQS REST (single-point; for calibration/validation).
public sealed class EpqsElevationSource : IElevationSource { ... }

/// Applies an EGM96/EGM2008 geoid-undulation grid so orthometric DEMs return
/// ellipsoidal heights. Wraps any IElevationSource that yields orthometric metres.
public sealed class GeoidCorrectedSource : IElevationSource { ... }
```

### 4.3 The SRTM `.hgt` reader (the recommended default)

The `.hgt` format is the simplest possible raster — **no library needed**:

- Filename encodes the SW corner: `n41w088.hgt` = tile spanning 41–42°N, 88–87°W.
- 1-arc-sec → **3601×3601** cells; 3-arc-sec → 1201×1201. (File size disambiguates:
  3601² × 2 bytes = 25,934,402 B.)
- Cells are **16-bit big-endian signed** integers (metres), row-major **north→south**, each row
  **west→east**. Sentinel `−32768` = void (use neighbour fill / NaN).
- Pixel (row,col) → lat/lon:
  `lat = tileLat + (rows-1-row)/(rows-1)`, `lon = tileLon + col/(cols-1)`.

**Lookup algorithm (bilinear):**
1. Resolve tile from floor(lat), floor(lon); memory-map / cache the tile (LRU, a handful of tiles).
2. Compute fractional cell coordinates within the tile.
3. Read the four surrounding posts; if any is void, fall back to nearest-valid or NaN.
4. Bilinearly interpolate; convert big-endian; return.
5. (If `GeoidCorrectedSource` wraps it) add the geoid undulation N(lat,lon) to convert the
   EGM96-orthometric SRTM height to ellipsoidal.

This is ~150 lines of C#, zero NuGet dependencies, fully unit-testable against known posts. **Strongly
preferred** over pulling in a DEM library.

### 4.4 .NET options for GeoTIFF (only if 3DEP 1 m is wanted)

| Option | Managed? | License | Verdict |
|---|---|---|---|
| **Custom `.hgt` reader** (above) | 100% managed | — | **Use for SRTM/DTED.** No GeoTIFF needed. |
| **BitMiracle.LibTiff.NET** | managed | BSD | Reads TIFF tags/strips; you decode geo-tags + samples yourself. Viable for a thin COG reader. |
| **GeoTiffCOG** (fabric-io-rodrigues) | managed (uses LibTiff) | MIT | Purpose-built: local + Cloud-Optimized GeoTIFF → elevation at lat/lon. Good fit for 3DEP. |
| **DEM.Net** (dem-net) | managed (LibTiff) | **Restrictive** — free only < $100k/yr revenue | Feature-rich (SRTM `.hgt` + GeoTIFF, auto-download, interpolation) but **license risk**; avoid for shipping. |
| **MaxRev.Gdal / GDAL C# bindings** | **native GDAL** dependency | MIT/LGPL | Most capable (any format + reprojection) but heavy native deps; overkill. |

**Recommendation:** Ship the **custom `.hgt`** reader. If/when 3DEP 1 m is needed, add
`GeoTiffElevationSource` backed by **GeoTiffCOG** (or a thin LibTiff.NET reader) behind the same
`IElevationSource` — and handle the UTM/NAD83 → WGS84 reprojection + NAVD88→ellipsoidal there.

### 4.5 Wiring into telemetry

The telemetry path already has everything it needs:

- `CarlaClient` caches each actor's CARLA-local `Transform` from the world observer
  (`GetActorTransform`, `_actorCache`).
- `Geodesy.CarlaLocalToGeodetic(origin, x, y, z)` → vehicle **lat/lon** (the exact transform the
  elevation hand-off already uses, so it is coherence-safe).

Telemetry elevation = `elevationSource.SampleEllipsoidal(lat, lon)` — **independent of the vehicle's
simulated Z** (which is flat). Emit `(lat, lon, elevation, velocity)` at 5 Hz. The vehicle's CARLA Z is
*not* used for the reported elevation; this is the whole point of decoupling. (Optionally also report
the raw simulated Z for debugging.)

---

## 5. Engine alternative — the two-tileset terrain-sampling option

Instead of an offline DEM, spawn **two** Cesium tilesets in the world:

1. **Visible Google** photoreal tileset (ion 2275207) — the EO visual, as today.
2. **Hidden Cesium World Terrain** tileset (ion **1**, bare-earth) — `SetActorHiddenInGame(true)`.

Then make `UCesiumHeightSampler::RequestSample` **target the terrain tileset** for height queries
(it already accepts a `TilesetActorName` filter — currently passed empty `FString()` from
`request_terrain_heights`, so it grabs the *first* tileset found). The fix is small:

- In `ConfigureCesiumForOrigin`, after spawning Google, also spawn the World-Terrain tileset
  (ion 1), hidden, sharing the same georeference.
- Plumb a tileset-name selector from `request_terrain_heights` → `RequestSample` and point it at the
  terrain actor (e.g. tag it / name-match "WorldTerrain"). This makes
  `SampleHeightMostDetailed` return **bare-earth** ellipsoidal heights while Google stays on screen.

**Pros**
- **Zero datum conversion** — World Terrain is ellipsoidal WGS84, already coherent with `Geodesy`,
  `CesiumGeoreference`, and the existing `originHeight` calibration. No geoid grid.
- Reuses the entire existing async sampling plumbing (`request_/poll_terrain_heights`).
- Global coverage; nothing to download or bundle.
- Works for **both** telemetry *and* the optional Mode-B bare-earth road elevation, unchanged.

**Cons**
- Requires the **server running + ticking + Cesium tiles streaming** for *every* telemetry elevation —
  not viable for a pure-offline / pre-flight telemetry pass, and adds per-query latency (tile stream +
  multi-tick callback). Fine for road-elevation pre-bake (one batch up front); awkward for live 5 Hz
  per-vehicle telemetry (would need batching every tick).
- Headless streaming of the terrain tileset must be verified (same `-RenderOffScreen` caveat as plan §5;
  note prior finding: tiles stream for *sampling* without a viewport, but this is unverified on this build).
- Two tilesets = more memory + bandwidth.
- World Terrain urban fidelity (~10 m) ≈ SRTM; no accuracy win over the offline DEM.

**Verdict:** Best as the **road-elevation (Mode B) source** — a one-time batch sample at world-gen,
where the server is already ticking and Cesium already configured (drop-in: just change which tileset
`RequestSample` hits). For **live 5 Hz telemetry**, the **offline SRTM DEM is better** (no server
dependency, no latency, deterministic). Implement both behind `IElevationSource` and a
`CesiumTerrainElevationSource` that wraps the RPC.

---

## 6. The datum trap (must get right for any orthometric DEM)

`Geodesy`, `CesiumGeoreference.OriginHeight`, Google tiles, and Cesium World Terrain are all
**ellipsoidal WGS84**. The injected road `z` and the calibration `originHeight` are ellipsoidal.

**SRTM, DTED, and 3DEP are ORTHOMETRIC** (height above the geoid / MSL — EGM96 for SRTM, NAVD88 for
3DEP). To mix them with the ellipsoidal pipeline you must add the **geoid undulation** N:
`h_ellipsoidal = H_orthometric + N(lat,lon)`. In Chicago N ≈ −34 m (EGM96) — a *constant ~34 m offset*
if ignored, which would sink every telemetry point and (in Mode B) the whole road network.

**Implementation:** bundle a coarse EGM96 15-arc-minute undulation grid (~2 MB, public domain) and a
tiny bilinear sampler (`GeoidCorrectedSource`). For a *single city* the undulation is nearly constant,
so a cheaper alternative is a **single calibration constant**: sample N once at the origin (or derive
it from the existing origin calibration — the difference between the Cesium-sampled ellipsoidal origin
height and the DEM orthometric origin height **is** N at the origin) and add it to all DEM samples.
The two-tileset option (§5) sidesteps this entirely (ellipsoidal end-to-end).

---

## 7. Implementation: flat-road mode + optional bare-earth road elevation

### 7.1 Mode A — FLAT ROADS (the new default)

Skip elevation injection entirely (or inject zeros). The flat `.xodr` from netconvert already has **no
`<elevationProfile>`**, so the simplest flat mode is: **convert OSM → flat `.xodr` → generate world,
and do not sample/inject.** This is exactly the existing `GenerateWorldFromOsmAsync` path — but we want
one entry point with a flag so the Cesium overlay is still configured.

**Flag plumbing** (least-change, additive):

- **`CarlaClient.GenerateWorldFromOsmWithElevationAsync`** — add `bool flatRoads = true`
  (default flat — the new safe default). When `flatRoads`:
  - Still run step 1 (OSM→flat `.xodr`) and step 3/7 (configure Cesium overlay at the origin so the
    visual + datum are set, using the origin-calibration sample for `originHeight`).
  - **Skip** steps 2,4,5 (extract / sample / inject). Generate the world from the **flat** `.xodr`.
  - Still sample **one** point (the origin) to set `originHeight` for the Cesium georeference vertical
    alignment, OR accept `originHeightOverride`.
- **`ElevationInjector`** — add a trivial `InjectZeroElevation(xodr)` / or simply branch before P2.
  (No new injector logic strictly needed if we just skip injection; the zero-inject helper is only for
  callers that want an explicit flat `<elevationProfile>`.)
- **Shim `generate_world_from_osm_with_elevation`** — add `flat_roads=True` kwarg forwarded to the C#
  call. Optionally keep the plain `generate_world_from_osm` as the no-Cesium flat path.

### 7.2 Mode B — bare-earth road elevation (optional, reuses ElevationInjector)

For hilly / oblique visual road-following, keep the **existing injection pipeline** but feed it a
**bare-earth source instead of Google**:

- Add a `source` selector to `GenerateWorldFromOsmWithElevationAsync`:
  `ElevationSourceKind { FlatRoads, GoogleSurface (legacy), CesiumWorldTerrain, OfflineDem }`.
- For `CesiumWorldTerrain`: identical flow to today, but `request_terrain_heights` targets the hidden
  terrain tileset (§5). **No datum conversion** (ellipsoidal). Drop-in.
- For `OfflineDem`: replace the `SampleTerrainHeightsAsync` engine call in step 4 with
  `IElevationSource.SampleEllipsoidal` over the same `geo` lat/lon list (offline, no server tick
  needed for sampling). Apply geoid correction (§6). The rest (`InjectElevation`, outlier rejection,
  gap fill) is **unchanged** — and with a true bare-earth source the outlier rejection becomes a cheap
  safety net rather than the primary defence.

`ElevationInjector.InjectElevation` needs **no change** — it already takes a parallel
`ellipsoidalHeights` array regardless of where the heights came from. Only the *producer* of that array
changes.

---

## 8. Phased plan

### Phase 1 — Flat roads + DEM telemetry (the deliverable-critical path)
1. **`CarlaClient` + shim:** add `flatRoads` / `flat_roads=True` (default). Flat-road world gen that
   still configures the Cesium visual overlay and sets `originHeight` from a single origin calibration
   sample (or override). *(small, no new module)*
2. **`CarlaNet.Geo` module:** `IElevationSource` + `SrtmElevationSource` (custom `.hgt` reader,
   bilinear, LRU tile cache) + `GeoidCorrectedSource` (EGM96 grid or single-constant calibration).
   Unit-test against known posts and an EPQS/Cesium cross-check at the origin.
3. **Telemetry wiring:** at 5 Hz, `CarlaLocalToGeodetic` → lat/lon → `SampleEllipsoidal` → emit
   `(lat, lon, elevation, velocity)`. Elevation is DEM-derived, independent of the flat sim Z.
4. **Acquire the SRTM tile(s)** covering the testbed (Chicago: `n41w088.hgt` neighbourhood) from the
   USGS SRTMGL1 v3 archive; document the bundle path.
5. **Validate:** compare DEM telemetry elevation at a few known points vs. EPQS / Cesium origin sample;
   confirm < ~1 m agreement after geoid correction.

### Phase 2 — Optional bare-earth ROAD elevation (visual road-following)
6. **Two-tileset engine option:** spawn hidden Cesium World Terrain (ion 1) in `ConfigureCesiumForOrigin`;
   plumb a tileset-name selector so `request_terrain_heights` samples the terrain tileset. (Verify
   headless streaming on this build per plan §5.)
7. **`ElevationSourceKind` selector** in `GenerateWorldFromOsmWithElevationAsync` + shim:
   `FlatRoads` (default) / `CesiumWorldTerrain` / `OfflineDem` / `GoogleSurface` (legacy, deprecated).
   Mode B reuses `ElevationInjector` unchanged; only the height producer swaps.
8. **`GeoTiffElevationSource`** (3DEP 1 m, via GeoTiffCOG/LibTiff) behind `IElevationSource` — only if
   1 m fidelity is later required for oblique work.

---

## 9. Top recommendation (summary)

**Adopt the decoupling. Make FLAT roads the default; compute telemetry elevation from an offline SRTM
`.hgt` bare-earth DEM via a new `CarlaNet.Geo` module.**

- Flat roads kill the spike/launch bug outright and are visually invisible from nadir EO.
- A dependency-free custom `.hgt` reader (bilinear, LRU cache) gives correct, deterministic, offline,
  server-independent telemetry elevation at exactly the precision the deliverable needs (30 m posts ≫
  adequate for nadir).
- Mind the **datum**: SRTM is orthometric — add the EGM96 geoid undulation (or a single origin-
  calibrated constant) to stay coherent with the ellipsoidal `Geodesy` / Cesium pipeline.
- Keep the **two-tileset (Cesium World Terrain, ion 1)** path as the optional **road-elevation** source
  for Mode B hilly/oblique visuals — ellipsoidal, zero datum conversion, reuses the existing
  `ElevationInjector` and async sampling plumbing untouched; best run as a one-time batch at world-gen,
  not for live 5 Hz telemetry.
- The Google surface tileset stays as the **visual overlay only** and is **removed from the
  elevation-sampling role** (legacy/deprecated).

---

## 10. Overpasses / grade separation — OSM topology + a bridge-offset pass

*Added 2026-06-11. Triggered by the Bellevue Rd / I-580 overpass test (`Import/Bellvue_Overpass.osm`,
center 39.247132, -119.813761) and an Overpass-turbo `way["bridge"]` / `way["highway"]` pull around it.*

### The defect
Sampling a surface and writing **one Z per road-centerline point** cannot represent two roads at the
same plan position and different height. At the Bellevue Rd / I-580 crossing the deck and the freeway
both drape to ~the same surface Z and **merge into a flat X** — cars route/drive through a phantom
at-grade crossing.

### Correction: OpenDRIVE already supports overpasses
`<elevationProfile>` is **per-road, parameterized by `s`** (distance along that road) — **not** a shared
(x,y) heightfield. Two roads crossing at the same plan position can carry independent `z(s)`. So the
format is *not* the limit; **our sampling method is** (surface → one Z per planar point). A per-way
elevation source that knows which ways are decks resolves it within stock OpenDRIVE.

### What OSM / Overpass actually supplies (verified on the Bellevue pull)
No `ele` on any road node — but the bridge is fully described *topologically*:

| Need | OSM datum | Bellevue value |
|---|---|---|
| **Which** segment is a deck | `bridge=yes` | way `71498338` "Bellevue Road" |
| **Direction** of offset | `layer=N` vs implicit `0` | `layer=1` → deck **over** freeway |
| **Where** (span to lift) | bridge way's node list | nodes `4111142224 ↔ 850445020` (~92 m, E–W at lat 39.24714) |
| **Transitions** | approach ways sharing the deck end-nodes | ways `71498350` (E end `850445020`), `409213510` (W end `4111142224`) |
| **Road under** | motorway ways | I-580/US-395 "Carson City Freeway"; crosses at lon ≈ −119.8136, **between** the deck endpoints |

**Two simplifications this confirms:**
1. **Topology is already correct.** The bridge way and the freeway ways share **no node** → OSM models
   them as separate (no junction). Nothing to fix in connectivity/routing; the defect is *purely* the
   elevation profile.
2. **Which / where / direction are fully specified by tags.** Only the **metric clearance** is missing.

### Overpass-turbo's role — verdict
- **On its own: cannot solve it.** It returns the *same* `bridge`/`layer` tags already in a plain `.osm`
  export, with **no road `ele`**. It adds no vertical data.
- **In tandem with the bridge-offset pass: yes — it is the enabling metadata harvester / validator.**
  It cleanly extracts bridge structures + interchange topology + exact span node IDs around a point —
  exactly what a general "lift decks" pass needs. For production you read the same tags straight from
  the `.osm` during conversion; Overpass-turbo is the **inspection/validation** tool (and a way to
  enrich a too-sparse export).

### Proposed fix (records the bridge-offset idea)
A bridge-aware elevation pass, at OSM level (pre-netconvert) or as a post-inject correction:
1. For every `bridge=yes` way, offset its elevation profile by `layer × clearance`. Default
   **clearance ≈ 5 m** (AASHTO min vertical clearance over an interstate ≈ 16′6″ ≈ 5.0 m); `layer`
   generalizes stacked structures.
2. **Ramp the approaches** on the ways sharing the deck's end-nodes so the deck is a smooth hump, not a
   step.
3. *Optional refinement* — **differential Cesium sampling**: densify the deck span and sample the Google
   mesh (which *does* contain the physical deck) to recover the *real* clearance instead of the
   constant. Noisier (deck thickness / mesh see-through) → gate behind a sanity range.
4. Generalizes to **every layered crossing in OSM worldwide** — fits procedural-city generation, not a
   Bellevue one-off.

### Integration caveat (the main implementation cost)
netconvert's OSM→`.xodr` almost certainly does **not** emit a "bridge" attribute, and `ElevationInjector`
currently sees only the `.xodr`. To know which `.xodr` road is a deck, either **(a)** do the lift as
**OSM preprocessing before netconvert** (raise deck nodes / split + tag), or **(b)** map `.xodr` road →
OSM way id (netconvert can emit original IDs) so the injector can cross-reference the OSM CarlaNet
already holds. Recorded, not built.

---

## Sources
- SRTM .hgt format & v3 void-filled: [OSM Wiki – SRTM](https://wiki.openstreetmap.org/wiki/SRTM) ·
  [USGS EROS SRTM 1 Arc-Second](https://www.usgs.gov/centers/eros/science/usgs-eros-archive-digital-elevation-shuttle-radar-topography-mission-srtm-1) ·
  [vterrain SRTM](http://vterrain.org/Elevation/SRTM/)
- DTED levels/format: [vterrain DTED](http://vterrain.org/Elevation/dted.html) ·
  [Golden Software DTED](https://voxlerhelp.goldensoftware.com/File_Formats/DTED.htm) ·
  [dted (PyPI)](https://pypi.org/project/dted/)
- USGS 3DEP 1 m bare-earth GeoTIFF: [USGS 3DEP Products & Services](https://www.usgs.gov/3d-elevation-program/about-3dep-products-services) ·
  [Seamless 1 m DEM catalog](https://data.usgs.gov/datacatalog/data/USGS:4f34caac-f28f-4ea0-8d82-eafb2b8f9a5d) ·
  [TNM Downloader](https://apps.nationalmap.gov/downloader/)
- USGS EPQS (calibration/validation): [EPQS](https://apps.nationalmap.gov/epqs/) ·
  [EPQS API docs](https://epqs.nationalmap.gov/v1/docs)
- Cesium World Terrain vs Google tiles / sampling: [Cesium World Terrain](https://cesium.com/platform/cesium-ion/content/cesium-world-terrain/) ·
  [Cesium for Unreal – Photorealistic 3D Tiles](https://cesium.com/learn/unreal/unreal-photorealistic-3d-tiles/) ·
  [Community: Google tileset terrain data](https://community.cesium.com/t/google-photorealistic-3d-tileset-terrain-data/23924)
- .NET GeoTIFF / DEM readers: [DEM.Net](https://github.com/dem-net/DEM.Net) ·
  [GeoTiffCOG](https://github.com/fabric-io-rodrigues/GeoTiffCOG) ·
  [GeoTIFF in .NET without GDAL](http://build-failed.blogspot.com/2014/12/processing-geotiff-files-in-net-without.html)
- Overpasses / OSM bridge tagging (§10): [OSM Wiki – `bridge`](https://wiki.openstreetmap.org/wiki/Key:bridge) ·
  [OSM Wiki – `layer`](https://wiki.openstreetmap.org/wiki/Key:layer) ·
  [Overpass turbo](https://overpass-turbo.eu/) ·
  AASHTO min vertical clearance over interstates ≈ 16 ft (16′6″ design) — [FHWA Bridges & Structures](https://www.fhwa.dot.gov/bridge/)
