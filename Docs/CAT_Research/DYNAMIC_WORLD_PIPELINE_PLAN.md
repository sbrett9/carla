# Dynamic Georeferenced World — Plan of Action (the elevation "chicken‑and‑egg")

**Prepared:** 2026‑06‑05 · synthesizes two research briefs +the Track‑B port.
**Sources:** [Findings/04_DynamicWorld_DataPipeline.md](Findings/04_DynamicWorld_DataPipeline.md) (data side),
[Findings/05_DynamicWorld_EngineIntegration.md](Findings/05_DynamicWorld_EngineIntegration.md) (engine side),
and the new transform [../../CarlaNet/src/CarlaNet.Types/Geom/Geodesy.cs](../../CarlaNet/src/CarlaNet.Types/Geom/Geodesy.cs).

---

## 1. Verdict: the dependency is **not actually circular**

The apparent deadlock — *can't inject elevation without Cesium heights → can't sample heights without
road points → can't get road points without converting the OSM* — dissolves once two facts are used:

1. **The origin (lat/lon) is a user parameter, known up front.** So Cesium can be georeferenced and
   **height‑sampled before any road exists.**
2. **Road sample points can be produced in‑process from the *flat* `.xodr`, with no world load.**
   CarlaNet already parses OpenDRIVE and evaluates `s → world (x,y)` (the same code TrafficManager uses).

So the real shape is a **linear 6‑pass pipeline**, not a cycle. Both the road *mesh* and the *waypoint z*
already follow OpenDRIVE `<elevation>` — so the entire fix is **post‑processing the `.xodr` text** plus a
**height‑sampling RPC**; **no CARLA road‑model or mesh‑code change is required.**

---

## 2. The resolved ordering

```
[user] OSM file + origin (lat0, lon0)
   │
P1 DATA  OsmConverter.ConvertFileAsync(osm, opts)  ── flat .xodr  (netconvert, +proj=tmerc, origin→(0,0))   [no server]
   │        └─(parallel) read OSM <bounds> / xodr <header> AABB  ──► tile‑prefetch hint to engine
P2 DATA  ElevationInjector.ExtractCenterlineSamples(flat_xodr, step)  ── per‑road (roadId, s, x, y)         [no server]
   │
P3 DATA  ToGeo(samples)  via  Geodesy.CarlaLocalToGeodetic  ── (roadId, s, lat, lon)   ★ uses Track‑B transform
   │  ───────────────────────────────── ENGINE BOUNDARY ─────────────────────────────────
P4 ENGINE  sample_terrain_heights(origin, [(lat,lon)…])  ── ACesium3DTileset.SampleHeightMostDetailed
   │        (tileset ticks until OnHeightsSampled fires; tiles stream on demand, no viewport needed)
   │        → [(lon, lat, ellipsoidalHeight, ok)…] aligned by index
   │  ───────────────────────────────── ENGINE BOUNDARY ─────────────────────────────────
P5 DATA  InjectElevation(flat_xodr, {(roadId,s): z})  ── targeted XDocument rewrite of <elevationProfile> only
   │        (z reconciled against the vertical datum; piecewise‑linear cubic records)  → elevated .xodr
P6 ENGINE  generate_opendrive_world(elevated_xodr)  ── OpenDriveMap.umap reload → AOpenDriveGenerator builds
            road mesh + spawn points at correct Z; Cesium stays as the visual overlay; CarlaNet drives traffic.
```

Hard deps: P4←P3, P5←P4&P1, P6←P5. P2/P3 need only P1 (no server). The prefetch hint fires after P1 in
parallel to warm Cesium before P4.

---

## 3. The ONE decision that unblocks everything — and it's already half‑done

**Both briefs independently flag the same #1 risk: projection coherence.** Roads are *physically placed* by
netconvert's **ellipsoidal `+proj=tmerc`**, but CARLA's runtime `GeoLocation::Transform` uses **spherical
Web‑Mercator** — and Cesium returns **ellipsoidal** heights. If `ToGeo` (P3) uses a *different* projection
than the one that placed the road, the sampled height lands at the wrong spot (drift grows with distance
from origin), so roads still float/sink — subtly and position‑dependently.

**Resolution (decided): use one ellipsoidal ENU‑tangent projection end‑to‑end — which the Track‑B port now
provides.** [Geodesy.cs](../../CarlaNet/src/CarlaNet.Types/Geom/Geodesy.cs) (`CarlaNet.Types.Geom.Geodesy`,
84/84 tests passing) gives `CarlaLocalToGeodetic` / `GeodeticToCarlaLocal` via local‑ENU → ECEF → WGS84.
This matches Cesium's own ENU/ECEF model to sub‑mm over a city patch **and** matches netconvert's tmerc
placement to sub‑cm near the origin (tmerc ≈ ENU on the central meridian). So:

- **P3 `ToGeo` must call `Geodesy.CarlaLocalToGeodetic(origin, x, y, 0)`** — NOT CARLA's spherical Mercator.
  (Geodesy also ports the spherical formula as `SphericalMercatorLocalToGeodetic`, *only* for GNSS parity /
  residual measurement — do not use it for the elevation hand‑off.)
- **Track‑B telemetry uses the same transform**, so georeferenced truth and road‑elevation sampling share
  one coherent projection. This kills the drift class at the source.

**Vertical datum (the remaining calibration):** Cesium heights are **ellipsoidal meters**; the `.xodr` `z`
and `CesiumGeoreference.OriginHeight` must agree. Pin it with **one calibration sample at the origin**: set
`OriginHeight` = sampled ellipsoidal height at (lat0,lon0); inject `z_road = sampledEllipsoidal − OriginHeight`
so the origin is z=0 and everything else is relative. (This also finally gives the testbed its exact
`OriginHeight` instead of the ~149 m estimate.)

---

## 4. Work items

### 4A. Data side (CarlaNet — C#/.NET, offline)
- **`CarlaNet.Map.OpenDrive.ElevationInjector`** (new):
  - `ExtractCenterlineSamples(xodr, stepMeters)` → `(roadId, s, x, y)` — reuse `OpenDriveParser.Load` +
    `Map.GetDirectedPointIn(road, s)` (the proven `InMemoryMap.BuildSegmentMap` loop).
  - `ToGeo(samples, origin)` → `(roadId, s, lat, lon)` — **uses `Geodesy.CarlaLocalToGeodetic`** (§3). ✅ transform already built.
  - `InjectElevation(xodr, {(roadId,s):z}, PiecewiseLinear)` → elevated xodr — **targeted `XDocument` rewrite of
    `<elevationProfile>` only** (no full serializer needed; preserve netconvert output byte‑for‑byte elsewhere).
- **Orchestration**: a `generate_world_from_osm_with_elevation(osm, opts, height_sampler)` Python/CarlaNet
  one‑liner that runs P1→P6, taking the engine sampler as a callback. Keep flat `generate_world_from_osm` as legacy.
- (Optional) extend `GeoReferenceParser` to retain the full proj string (not just lat0/lon0).

### 4B. Engine side (Carla plugin + Cesium — C++/UE)
- **Pre‑place** `CesiumGeoreference` + `Cesium3DTileset` (ion 2275207, or open content) in
  `Carla/Maps/OpenDriveMap.umap` so they survive every `generate_opendrive_world` reload.
- **C++ on level load** (`ACarlaGameModeBase::BeginPlay` or `AOpenDriveGenerator::BeginPlay`): set
  georeference `Origin{Lat,Lon,Height}` from the active xodr `<geoReference>` + set ion token. Requires adding
  `CesiumRuntime` to `Carla.Build.cs`.
- **New server command `sample_terrain_heights(origin, [(lat,lon)…]) → [(lon,lat,height,ok)…]`** (sibling to
  `copy_opendrive_to_file`): calls `ACesium3DTileset::SampleHeightMostDetailed`, **pumps ticks until
  `OnHeightsSampled` fires** (don't block the game thread), returns results. This is the P4 hand‑off.
- Per‑point failure fallback (water/missing tiles): interpolate from neighbors so `InjectElevation` never writes NaN.

### 4C. Height hand‑off
RPC is the natural route (in‑process, mirrors existing plumbing). File‑handoff (engine writes JSON next to
`Carla/Maps/OpenDrive/`) is the zero‑C++ prototype fallback and works from the editor‑Python path.

---

## 5. The one thing to de‑risk FIRST: headless sampling

Both briefs converge: **`SampleHeightMostDetailed` loads tiles on demand and is frustum‑independent — it
needs a *ticking world*, not a *rendering viewport*.** The "tiles only stream when the viewport is visible"
behavior we hit applies to the **visual overlay**, not to sampling. So headless dataset generation is
**very likely viable** — but **unverified on this build.**

**De‑risk task (do before building the full pipeline):** on a packaged **`-RenderOffScreen`** CARLA server
(real RHI, no window — *not* `-nullrhi`, which may break tile decode), confirm `OnHeightsSampled` fires with
`SampleSuccess=true` heights while the world ticks. If yes, the whole pipeline can run headless for batch
dataset generation. The *visual* EO overlay headless separately needs `-RenderOffScreen` + the EO capture
camera (which is the drone sensor anyway — so it doubles as the streaming driver).

---

## 6. Risk register

| # | Risk | Severity | Resolution |
|---|------|----------|------------|
| R1 | Projection mismatch (tmerc vs spherical Mercator) → heights sampled at wrong spot | 🟠→🟢 | **Resolved by design**: use `Geodesy` ENU‑tangent for `ToGeo` *and* telemetry (Track‑B done) |
| R2 | Vertical datum (ellipsoidal vs orthometric vs CARLA flat offset) | 🟠 | Single origin calibration sample → set `OriginHeight`, inject z relative to it |
| R3 | Headless tile streaming for sampling unverified on this build | 🟠 | Prototype `SampleHeightMostDetailed` on `-RenderOffScreen` first (§5) |
| R4 | Cesium↔CARLA is greenfield (no existing bridge) + Carla.Build.cs needs CesiumRuntime | 🟡 | Pre‑place actors in OpenDriveMap.umap; add module dep; or prototype via editor‑Python first |
| R5 | Per‑point sample failures (water, missing/over‑LOD tiles) | 🟡 | Neighbor interpolation / hold‑previous fallback in `InjectElevation` |
| R6 | Junction connecting‑road height discontinuities | 🟡 | Sample their own centerlines; smooth at road boundaries if needed |
| R7 | netconvert road‑network quality (junction/lane artifacts) | 🟡 | Out of scope here; manual `.xodr` cleanup per CARLA tuning docs |

---

## 7. Phased implementation roadmap

1. **Phase A — de‑risk headless sampling (§5).** Prototype `SampleHeightMostDetailed` on `-RenderOffScreen`
   (or first via editor‑Python on the testbed). Gate: delegate fires with valid heights. *Also yields the
   exact `OriginHeight` for the testbed vertical datum.*
2. **Phase B — data side (offline, no engine):** build `ElevationInjector` (`ExtractCenterlineSamples`,
   `ToGeo` via `Geodesy`, `InjectElevation`). Unit‑test on `WrigleyVille` flat `.xodr` (round‑trip + the
   injected profile parses back). No engine needed.
3. **Phase C — height hand‑off:** implement `sample_terrain_heights` (RPC, or JSON file fallback for v1).
4. **Phase D — Cesium‑in‑OpenDriveMap:** pre‑place actors + C++ origin/token wiring; or editor‑Python overlay
   for the first end‑to‑end.
5. **Phase E — orchestrate P1→P6:** the `generate_world_from_osm_with_elevation` one‑liner; spawn CarlaNet
   traffic; confirm vehicles drive on the photoreal streets at correct elevation.

---

## 8. Already done (feeds this plan)

- **Origin pinning** OSM→`.xodr` (`OsmConverter`, `+proj=tmerc … --offset.disable-normalization`) — see
  [../../CarlaNet/docs/OSM_Georeferencing.md](../../CarlaNet/docs/OSM_Georeferencing.md).
- **Track‑B transform** `CarlaNet.Types.Geom.Geodesy` (ENU‑tangent ellipsoidal local↔geo) — **resolves R1**,
  serves both telemetry and the elevation `ToGeo` step. 84/84 tests pass.
- **Georeference co‑registration validated** on the testbed (origin pinned to home plate; axis convention
  +X=East/−Y=North matches CARLA; ≤~1.1 m spherical‑vs‑ellipsoid residual quantified).
- **Consumption side fully present**: CARLA road mesh + waypoint z already follow `<elevation>`; only
  production (injection) is new.
