# Layer Architecture — N toggleable world layers (3D Tiles · Ground · Road · …)

**Prepared:** 2026-06-11 · Design / plan of record (no code yet — chosen "record the plan first").
**Builds on:** [06_Elevation_Strategy.md](06_Elevation_Strategy.md) §5 (two-tileset terrain sampling) ·
[05_DynamicWorld_EngineIntegration.md](05_DynamicWorld_EngineIntegration.md).
**Code touched (future):**
`CesiumCarlaBridge/.../CesiumHeightSampler.{h,cpp}`, `Carla/Server/CarlaServer.cpp`,
`CarlaNet.Transport/CarlaClient.cs`, `python/carlanet/__init__.py`, `python/eo_observer.py`.

---

## 1. What motivated this — the World Terrain result

A/B test on `Import/SF_LaurelHeights.osm` (Tier-1, zero code change, just `--ion-asset-id 1`):

| Run | Sampled tileset | Road Z result |
|---|---|---|
| `--ion-asset-id 2275207` | Google Photoreal **surface** | jagged spikes / torn ribbons (roofs, canopy, melted mesh) — see §1 of 06 |
| `--ion-asset-id 1` | Cesium **World Terrain** (bare-earth DTM) | **smooth, driveable**; few residual jaggies (≈10 m DTM posts); vehicles navigate cleanly; full physics retained |

**Conclusion:** bare-earth (World Terrain) is the right **road-Z + sample source**. The only thing that
run gave up was the photoreal *visual* (you see the terrain skin). The goal now is to have **both**:
Google photoreal as the visual, World Terrain as the (hidden) height truth, the OpenDRIVE road on top —
each independently toggleable — and to generalize that to **N** layers so future sources (e.g. OSM
Buildings) slot in for free.

---

## 2. The unifying insight

**Google Photoreal, Cesium World Terrain, and Cesium OSM Buildings are all the same kind of object — a
Cesium 3D Tiles tileset** (`ACesium3DTileset`), differing only by ion asset id:

| Source | ion asset id | Geometry |
|---|---|---|
| Google Photorealistic 3D Tiles | **2275207** | textured surface (DSM) |
| Cesium World Terrain | **1** | bare-earth terrain (DTM) |
| Cesium OSM Buildings | **96188** | extruded building shells |

They spawn, stream, sample (`SampleHeightMostDetailed`), hide (`SetActorHiddenInGame`), and collide
(`SetCreatePhysicsMeshes`) through one identical API. So there are really only **two layer kinds**:

- **`CesiumTileset`** — Google / Terrain / OSM Buildings / any future ion tileset. Scales to N for free.
- **`ProceduralActor`** — the OpenDRIVE **road** mesh (`AProceduralMeshActor`); later possibly
  procedurally-extruded OSM building meshes if we don't use Cesium's.

Planning for N now is therefore nearly free: **OSM Buildings later = "add a layer with asset 96188."**

---

## 3. The layer model

A **Layer** = `{ name, kind, visible, collision, isSampleSource, source }`:

| Field | Meaning |
|---|---|
| `name` | stable id and toggle handle — `photoreal` / `ground` / `road` / `buildings` |
| `kind` | `CesiumTileset` or `ProceduralActor` |
| `visible` | render on/off — **independent of collision** |
| `collision` | physics on/off — **independent of visibility** |
| `isSampleSource` | exactly **one** layer is the height truth for road-Z + telemetry (the `ground`) |
| `source` | for `CesiumTileset`: ion asset id + access token |

`visible` ⟂ `collision` per-layer *is* the generalization of today's `C` / `V` / `R` keys — the
primitives already exist, they are just currently **global**.

---

## 4. What changes (mostly: generalize 3 functions that already exist)

Today the engine has three **ad-hoc, global** operations:
`SetCesiumTilesetsVisible` and `SetCesiumCollisionEnabled` iterate **all** tilesets; `set_road_rendered`
iterates the road mesh. With two tilesets in the world, `C`/`V` would hit **both** — wrong. The fix is to
**address layers by name** via an actor **tag**.

### Engine — `CesiumCarlaBridge`
- **`ConfigureLayers(origin, layers[])`** — replaces the single-tileset spawn in
  `ConfigureCesiumForOrigin`. For each `CesiumTileset` layer: spawn it, **`Tags.Add(FName(name))`**, set
  georeference + token + asset id, set `SetActorHiddenInGame(!visible)` and
  `SetCreatePhysicsMeshes(collision)`. (Critical: skip already-tagged tilesets on re-configure so each
  layer keeps its own asset id — the current loop applies one asset id to *all* tilesets.)
- **`SetLayerVisible(name, bool)` / `SetLayerCollision(name, bool)`** — the existing iterators, now
  **filtered by tag** instead of touching everything.
- **`RequestSample(points, name)`** — **already accepts a tileset selector**
  (`TilesetActorName`, [CesiumHeightSampler.cpp L95](../../../Unreal/CarlaUnreal/Plugins/CesiumCarlaBridge/Source/CesiumCarlaBridge/Private/CesiumHeightSampler.cpp)).
  Extend the match to also check `Actor->Tags`, and point sampling at the `ground` layer.
- The **road** stays a `ProceduralMesh`, but answers to layer name `road` in the same dispatcher
  (`SetLayerVisible/Collision` route `ProceduralActor` kinds to the road path).

### RPC + client + shim
- A **`layers` config list** passed at configure time (name, asset id, visible, collision, sample-source).
- `set_layer_visible(name, bool)` / `set_layer_collision(name, bool)` (generalize the existing
  `set_cesium_visible` / `set_cesium_collision` / `set_road_rendered`; keep thin back-compat shims).

### eo_observer — the N-layer UI
- Enumerate layers from the server; bind **`1..N` → toggle visibility**, **`Shift+1..N` → toggle
  collision**; HUD prints one row per layer with its `V`/`C` state.
- The fixed `C`/`V`/`R` keys retire (`road` becomes just another layer). Keep `R`→road etc. as optional
  aliases if convenient.

---

## 5. Default configuration (the plan-of-record)

Keeps today's behavior, brings photoreal back as visual-only, adds the bare-earth ground:

| # | Layer | Source (asset) | Visible | Collision | Sample src |
|---|---|---|---|---|---|
| 1 | `photoreal` | Google `2275207` | **ON** | **OFF** (melted mesh would snag cars) | — |
| 2 | `ground` | World Terrain `1` | OFF (hidden) | **ON** | **YES** |
| 3 | `road` | OpenDRIVE mesh | ON | **ON** | — |
| 4 | `buildings` *(future)* | OSM Buildings `96188` | ON | optional | — |

**Default collision policy: `ground + road` collidable, with `height_align = none`** (FINAL, 2026-06-12).
With no road-Z offset (`none`), the bare-earth `ground` and the OpenDRIVE `road` are **coincident**, so
both collidable gives off-road safety (vehicles don't fall off the world) **with no float** (the brief
`ground + road` → `road`-only detour was only needed when an offset *separated* the two; see §10). The
whole driveable surface sits ~sub-meter above the photoreal street — **invisible from nadir** (the EO
deliverable). `photoreal` stays visual-only. `height_align` (area/origin) and per-layer collision remain
**flags/`V`-toggles** for experiments, but the default is `none` + `ground + road`.

---

## 6. Datum coherence (why this is clean)

All three Cesium tilesets and `Geodesy` / `CesiumGeoreference` are **ellipsoidal WGS84**. Sampling the
`ground` (World Terrain) for road-Z needs **zero datum conversion** — unlike the offline-SRTM path,
which is orthometric and needs the EGM96 geoid correction (06 §6). The two-tileset approach is the
datum-free option; SRTM remains the offline alternative behind the same sample interface.

---

## 7. Future layers (the "N" payoff)

- **Cesium OSM Buildings (asset 96188)** — drop in as a `CesiumTileset` layer; toggles + sampling work
  unchanged. Gives building shells everywhere OSM has footprints (global), complementing or replacing
  Google's photoreal where photoreal is poor.
- **Procedural extruded buildings** — if Cesium's are unwanted, a `ProceduralActor` layer built from OSM
  `building` ways + `height`/`building:levels`, same toggle plumbing as `road`.
- **Additional ion tilesets / overlays** — any ion asset id is one more layer row.

---

## 8. Risks / things to verify in the build
- **Hidden-tileset sampling:** `SampleHeightMostDetailed` is frustum-independent and streams tiles on
  demand, so a *hidden* World Terrain should sample fine — but Tier-1 sampled it **visible**, so Tier-2
  must confirm sampling works while `ground` is hidden. (If not: sample during a brief visible window at
  world-gen, then hide.)
- **Two/three tilesets = more memory + bandwidth** than one; watch headless streaming budget.
- **Collision coincidence:** ground + road both collidable and ~coplanar is fine for driving, but verify
  no z-fighting/physics jitter where they diverge at the residual DTM jaggies.
- **Re-configure idempotency:** tag-skip logic must not re-point an existing layer's asset id on repeat
  `ConfigureLayers` (the OpenDriveMap reloads each `generate_opendrive_world`).

---

## 9. Phased plan (when implementation is greenlit)

1. **Engine layer registry:** `ConfigureLayers` (tagged spawn, per-layer visible/collision/asset),
   tag-filtered `SetLayerVisible/Collision`, tag-aware `RequestSample` → `ground`.
2. **RPC + client + shim:** `layers` config list; `set_layer_visible/collision(name,bool)`; keep
   back-compat shims for the current `set_cesium_*` / `set_road_rendered`.
3. **Pipeline default:** 3-layer config (photoreal visible / ground hidden+sampled / road), sample-source
   = `ground` (World Terrain, asset 1); collision = ground + road.
4. **eo_observer N-layer UI:** enumerate layers; `1..N` visibility, `Shift+1..N` collision; per-layer HUD.
5. **Verify** the §8 risks (hidden sampling first).
6. **Later:** add `buildings` (asset 96188) as a 4th layer — exercises the N-generality with one row.

Effort: ~4 files + one `BuildCarla.ps1 -InstallWheel`. Additive — the current single-tileset path stays
as a fallback.

---

## 10. Phase 1 — built & verified (2026-06-11)

Implemented and verified live on SF Laurel Heights: **photoreal** (Google 2275207) *visible* +
**ground** (Cesium World Terrain, asset 1) *hidden + sampled* for road-Z + **road**, each per-layer
toggleable in `eo_observer` (**C** photoreal / **G** ground / **V** ground-collision / **R** road).
Engine: `EnsureTileset` tag-spawn, `RequestSample` tag selector (`"ground"`), `SetLayer{Visible,Collision}`.
RPC: `configure_cesium_georeference(+ground_ion_asset_id)`, `request_terrain_heights(+tileset_selector)`,
`set_layer_{visible,collision}`. Client/shim/`test_digital_twin` (`--ground-asset-id`, default 1). Road-Z
sampling reveals the ground layer during the sample then hides it, so hidden-tileset streaming is never on
the critical path. **Heights sample correctly (no zeros); roads markedly smoother; road ≈ ground, no z-fighting.**

### Height reconciliation — INVESTIGATED & CLOSED 2026-06-12 (default = `none`)
The road/car-Z (bare-earth DTM) doesn't match the visible photoreal surface (DSM). Two findings settled it:

1. **The gap is a *spatially-varying* DTM-vs-DSM divergence, not a constant offset.** Cesium World Terrain
   is a coarse (~10 m) smoothed DTM; Google photoreal is a high-res DSM. On SF's **hills** the two diverge
   by *several metres locally* even though the map-wide median is only **−0.75 m** (`area`; origin −0.82 m;
   `min −1.22, max 21.82` = a building spike the median ignored). A **single constant offset cannot fix a
   spatially-varying divergence** — confirmed: `area`/`origin`/`none` all looked ~the same on the slope.
2. **The dramatic "building floating above its street" was Google's tileset, not us.** A *pure-Cesium*
   editor view (no CARLA) at the same spot shows the identical melted/floating chunks; and a CARLA
   photoreal-only view (`R` off, `G` off) is pixel-identical to it. So our pipeline renders the photoreal
   faithfully — the melt is Google's data quality, amplified by grazing camera angles. Nothing to fix on
   our side.

**Decision — default `height_align = none` + `ground + road` collision.** No offset → road = ground
coincide → vehicles ride them with **no float** and **ground collision for off-road safety**; the whole
driveable surface sits ~sub-meter above the photoreal — **invisible from nadir** (the EO deliverable), and
chasing street-level perfection isn't worth it given Google's up-close melt anyway. The `area`/`origin`
offsets and `ground_collision=false` remain **flags** (the offset shifts road heights so the car sits on
the photoreal *and* telemetry HAE tracks it — useful if a future map needs it). **Path A** (texture the
DTM + OSM Buildings to make one surface) is **shelved** — it would dodge the divergence but Google's melt
makes it less compelling.

### Remaining minor follow-ups (not blocking)
- **Street-level DTM-vs-DSM divergence on hills** is accepted (invisible from nadir). Revisit only if an
  up-close/oblique product needs it → then Path A or photoreal-sampled road-Z (with spike rejection).
- **Traffic occasionally leaves the road on a U-turn.** Possibly TrafficManager pathing
  (`generate_traffic_carlanet`), unconfirmed; unrelated to the layer work.

---

## Sources
- Cesium ion asset ids: Google Photorealistic 3D Tiles (2275207), Cesium World Terrain (1),
  [Cesium OSM Buildings (96188)](https://cesium.com/platform/cesium-ion/content/cesium-osm-buildings/).
- 3D Tiles tileset API (spawn/sample/hide/collision): Cesium for Unreal `ACesium3DTileset`,
  `SampleHeightMostDetailed`, `SetCreatePhysicsMeshes`, `SetActorHiddenInGame`.
- Two-tileset terrain sampling rationale + datum coherence: [06_Elevation_Strategy.md](06_Elevation_Strategy.md) §5–6.
