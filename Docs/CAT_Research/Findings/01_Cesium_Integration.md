# Cesium for Unreal — Research Brief

Research pillar for the CARLA × Cesium digital-twin EO-drone/satellite simulation project.
Prepared 2026-06-02. Target engine: vanilla-ish UE 5.7.4 (CARLA-patched) at `g:\Projects\CarlaUE_5_7_4\UE_5_7_4`.

> **TL;DR biggest risk:** Google's Photorealistic 3D Tiles **Maps Platform Terms of Service explicitly prohibit non-visualization use** — image analysis, object detection, geodata extraction, offline use, and caching of content. Generating a georeferenced, truthed synthetic *training dataset* from Google tiles is almost certainly a ToS violation. The whole "render Google photoreal city + export truth telemetry for ML" premise needs an open-data tileset (Cesium OSM Buildings, or self-hosted photogrammetry) to be legally defensible. See **§6** and **Key Risks**.

---

## 0. What's already in the workspace

A Cesium for Unreal plugin is **already bundled in the engine build**:

- Path: `g:\Projects\CarlaUE_5_7_4\UE_5_7_4\Engine\Plugins\Marketplace\Cesiumfo9eaf76ca58f3V10\`
- Version: **v2.26.0 (2026-05-01)** per its `CHANGES.md`. (Latest public release is v2.27.0, 2026-06-01.)
- License: **Apache 2.0** — free for commercial and non-commercial use (the *plugin* is free; the *content* you stream is separately licensed — see §2/§6).
- It builds on **cesium-native** (this version bundles cesium-native v0.59.0→v0.60.0 era code).

So integration is mostly already done at the engine level; the open questions are content licensing, georeferencing strategy, high-altitude rendering quality, and determinism inside CARLA's synchronous loop.

---

## 1. Plugin version, UE compatibility, installation

**Version & compatibility**
- Latest release: **v2.27.0 (2026-06-01)**. Bundled here: **v2.26.0**.
- Cesium's standard policy: support the **three most recent UE versions**. Current releases support **UE 5.5, 5.6, and 5.7** (Windows/Linux/macOS/Android/iOS). **UE 5.7 is fully supported.** Confirmed via release notes and the Cesium community thread "Plugin for UE5.7".
- v2.22.0 **dropped UE 5.4** (UE 5.5 is now the floor).
- The cesium-native core is C++ and links against Unreal's SDK; recent versions deliberately consume Unreal's bundled third-party libs (e.g. tinyxml2 on Win/Linux as of v2.24.0) to avoid symbol conflicts with other plugins.

**Source-build / custom-engine considerations (relevant here — CARLA-patched engine)**
- The Marketplace/Fab binary build is compiled against Epic's *official* engine binaries. A **custom-compiled / source-patched engine (like CARLA's) will usually require recompiling the plugin from source** so its module ABI matches your engine. Use the GitHub releases (which ship the plugin source + precompiled cesium-native libs) and let UBT compile the Unreal-side modules, OR clone `CesiumGS/cesium-unreal` and follow the Developer Setup Guide (it fetches/builds cesium-native via a vendored extern step).
- Install method (from README): extract the release zip into `Engine/Plugins/Marketplace/` (already done here), or place under a project's `Plugins/` folder. For a source engine you typically drop it in `Plugins/` of the project (or `Engine/Plugins/Marketplace`) and it compiles on next build.
- **Action item:** verify the bundled v2.26.0 actually compiles/links against the CARLA-patched 5.7.4. Because CARLA patches the engine, the safest path is rebuilding cesium-unreal from source against this exact engine rather than trusting a Fab binary.

**Installation/integration at runtime**
- Add a **`Cesium3DTileset`** actor + a **`CesiumGeoreference`** actor to the level, set an ion access token (Project Settings → Plugins → Cesium, or per-tileset), pick a tileset (ion asset ID or URL), done. The plugin adds a "Cesium" panel to the editor for ion login and one-click "Quick Add" of Google Photorealistic Tiles / World Terrain / OSM Buildings.

---

## 2. 3D Tiles streaming architecture & available tilesets

**Core actors/classes**
- **`ACesium3DTileset`** — the streaming tileset actor. Key props: `Url` or ion `IonAssetID`+`IonAccessToken`; **`MaximumScreenSpaceError`** (LOD knob, see §4); `PreloadAncestors`/`PreloadSiblings`; `ForbidHoles`; `CulledScreenSpaceError` & `EnableFrustumCulling`; `MaximumCachedBytes`; `ReceiveDecals` (added v2.25.0); `UnloadEditorTilesInPlayMode` (added v2.26.0 — useful to avoid editor/PIE resource duplication).
- **`ACesiumCameraManager`** — tells the tile selector which cameras drive LOD. As of v2.24.0 it has `AdditionalCameras`, `UsePlayerCameras`, `UseEditorCameras`, `UseSceneCapturesInLevel`, and explicit `SceneCaptures` (`ASceneCapture2D` list). **This is the hook for off-screen / headless EO capture** — register your capture camera so tiles stream for that viewpoint even with no player viewport.
- Raster overlays: `UCesiumRasterOverlay` subclasses (`CesiumIonRasterOverlay`, `CesiumBingMapsRasterOverlay`, `CesiumTileMapServiceRasterOverlay`, `CesiumWebMapServiceRasterOverlay`, etc.) drape imagery onto terrain tiles. `CesiumVectorTilesRasterOverlay` (recent) renders vector data.

**Available tilesets / content (via Cesium ion)**
| Content | ion source | Notes / licensing |
|---|---|---|
| **Google Photorealistic 3D Tiles** | Google Maps Platform, brokered through ion | Photoreal mesh+texture of most of Earth. **Heavily ToS-restricted — see §6.** Billed per "root tile request" (session-like). |
| **Cesium World Terrain** | ion asset 1 | Global terrain mesh. Open/commercial under ion subscription. |
| **Bing Maps imagery** | ion (raster overlay) | Drapes over terrain. (Note: Cesium/Microsoft have been migrating default imagery; check current default in the ion asset depot.) |
| **Cesium OSM Buildings** | ion asset 96188 | Global building footprints extruded from OpenStreetMap. **ODbL-licensed, open** — the legally-clean choice for derivative/ML datasets. Lower fidelity (boxy, untextured/flat-shaded) but georeferenced and free of Google's restrictions. |
| **Self-hosted 3D Tiles** | your own URL | Photogrammetry/CityGML you tile yourself (ion on-prem, or open tilers). No third-party ToS. |

**Cost / tokens**
- **Cesium ion token** required for ion-brokered content (Google tiles, World Terrain, Bing, OSM Buildings). Free **Community** tier = individual non-commercial only, limited streaming quota. Commercial tiers exist; a commonly cited paid tier is ~**$499/mo ($5,988/yr)**; enterprise is custom-quoted. (Pricing changes — verify at cesium.com/platform/cesium-ion/pricing.)
- **Google Photorealistic 3D Tiles** billing is per **root-tile request** (≈ a "session"), passed through ion or via a direct Google Maps Platform API key. There is a free monthly quota then usage-based billing.
- **For commercial / training-data use you need a commercial ion plan AND a compliant content license** — and for Google tiles, see §6.

---

## 3. Georeferencing model

**`ACesiumGeoreference`** — the singleton that maps the WGS84 globe (ECEF) into the UE level. It places a chosen geographic "origin" at UE world `(0,0,0)`.
- Origin props: `OriginPlacement` (e.g. *CartographicOrigin* using lat/long/height, vs *True Origin* at Earth center), **`OriginLatitude` / `OriginLongitude` / `OriginHeight`** (degrees / degrees / meters above ellipsoid). Getter/setter pairs `GetOriginLatitude()`/`SetOriginLatitude()` etc.
- Coordinate frames: **ECEF** (Earth-Centered Earth-Fixed, meters) is the canonical global frame; **ENU/ESU** (East-North-Up / East-South-Up) is the local tangent frame at a point; **UE local** is the engine's left-handed, Z-up, cm-scaled world centered on the georeference origin.

**`UCesiumGlobeAnchorComponent`** — attach to any actor (a CARLA vehicle, a capture rig) to pin it to a geographic location. You can set its position in **ECEF** or **lat/long/height**, and when the actor is moved by normal UE transforms the component back-computes its geocoordinates. **Critical rule:** when origin shifting is active, *every* placed object must have a globe anchor, or it will appear to slide when the origin moves. Recent props: `DetectTransformChanges` (v2.24.0), `HeightReference`/`ReferencedTileset`/`HeightUpdateInterval` (v2.25.0 — auto-maintain height above a tileset across LOD changes; handy for keeping ground vehicles glued to streamed terrain).

**Placing objects geo-accurately** — give the actor a `CesiumGlobeAnchorComponent`, set lat/long/height; the georeference converts to the correct UE position. This is exactly how you'd inject CARLA traffic at real-world coordinates and later export truth.

**Interaction with UE Large World Coordinates (LWC) & origin rebasing** — *confirmed against the engine source at `UE_5_7_4\Engine\Source\Runtime\Engine\Classes\Engine\World.h`*:
- `UWorld` holds `FIntVector OriginLocation` (current world origin) and `RequestedOriginLocation`; rebasing goes through **`RequestNewWorldOrigin(FIntVector)`** / **`SetNewWorldOrigin(FIntVector)`**; `FVector OriginOffsetThisFrame` is non-zero for the single frame an origin shift happens. This is the classic UE "world origin rebasing" mechanism Cesium drives.
- UE 5 uses **Large World Coordinates** (double-precision `FVector`/`FLargeWorldRenderPosition`) so the world can span the ~40,000 km globe without precision loss; even so, Cesium still rebases near the camera to keep *render* (single-precision) precision tight and to keep physics stable. (Note: UE 5.7 here did not expose the older `bEnableLargeWorlds`/world-composition toggles I searched for — LWC is on by default in modern UE.)
- **`UCesiumOriginShiftComponent`** (attach to the camera/pawn) is what triggers shifting. Properties: **`Distance`** (max distance from origin before a shift; `0.0` = shift continuously every frame) and **`Mode`** (`ECesiumOriginShiftMode`): *disabled / switch-sublevels-only* vs *continuous shifting*. It shifts either by moving the **CesiumGeoreference origin** or by setting **`UWorld::OriginLocation`** (UE rebasing). `UCesiumSubLevelComponent` ties sub-levels to fixed georeferenced origins so the origin is stable within a sublevel.
- **Implication for our pipeline:** for deterministic, georeferenced *capture* you almost certainly want to **disable continuous per-frame origin shifting** (set `Mode` to disabled, or a large `Distance`) and instead pin a **fixed georeference origin** at the city center, anchoring the EO camera and all vehicles with globe anchors. A moving origin every frame would needlessly perturb floating-point positions and complicate truth export. UE 5.7 LWC double precision means a city-scale (tens of km) fixed origin is fine without rebasing.

---

## 4. High-altitude (500 m – 10 km+) nadir rendering

**LOD/streaming behavior**
- Tile LOD is governed by **`MaximumScreenSpaceError` (SSE)**: higher = coarser tiles (faster), lower = finer tiles (slower, more memory). Default ~16; users drop to 4 or 2 for higher quality at performance cost.
- At high altitude the camera covers a *huge* ground footprint, so the tile selector naturally serves **coarse LOD over a very wide area** — this means many tiles but each at low detail. This is the *good* regime for our use case: from 500 m–10 km the coarse Google LOD is acceptable, and you avoid the well-documented "muddy/blocky below 500 m" close-range problem (community thread: Google tiles are "very rough and muddy and not usable for distances below 500 meters" — that limitation is *inherent to the dataset*, not a settings issue, but it mostly doesn't bite at drone/satellite altitude).
- **Known nadir caveat:** Google Photorealistic 3D Tiles are photogrammetry built primarily from **oblique aerial captures**. Straight-down (nadir) views expose photogrammetry weaknesses — **"melted"/draped building tops, smeared rooftops, car-on-road texture baked into the mesh, holes/cavities under bridges and overhangs.** Vertical surfaces look better than horizontal tops from directly above. For a true satellite/nadir EO look this is a fidelity risk; slightly oblique angles look markedly better. Cesium OSM Buildings (clean extruded prisms) can actually read *more plausibly* as a stylized nadir map for some EO/SAR-style synthetic data.
- **Atmosphere / sun / sky:** Cesium provides a **`CesiumSunSky`** actor (georeferenced sun position by date/time/lat-long) and integrates with UE's `SkyAtmosphere`. At 10 km the atmospheric scattering / aerial perspective is visible and must be tuned to match real EO sensor characteristics (you'll likely *reduce* artistic haze for a sensor-accurate look, and drive sun azimuth/elevation from the scene's real time-of-day for correct shadows in truth data).
- **Capture quality controls:** lower SSE on the tileset for the capture pass; ensure the capture camera is registered with `ACesiumCameraManager` (`SceneCaptures`/`AdditionalCameras`) so tiles stream for the off-screen viewpoint; use Movie Render Queue / high-res screenshot with the "wait for tiles" path (§7).

---

## 5. Coordinate-conversion APIs (for georeferenced telemetry export)

All on **`ACesiumGeoreference`** (Blueprint + C++ callable). Exact names:

| Method | Converts |
|---|---|
| `TransformLongitudeLatitudeHeightPositionToUnreal` | lon/lat/height (deg, deg, m) → UE world position |
| `TransformUnrealPositionToLongitudeLatitudeHeight` | UE world position → lon/lat/height |
| `TransformEarthCenteredEarthFixedPositionToUnreal` | ECEF (m) → UE world position |
| `TransformUnrealPositionToEarthCenteredEarthFixed` | UE world position → ECEF (m) |
| `TransformEarthCenteredEarthFixedDirectionToUnreal` / `TransformUnrealDirectionToEarthCenteredEarthFixed` | direction vectors ECEF ↔ UE |
| `TransformUnrealRotatorToEastSouthUp` / `TransformEastSouthUpRotatorToUnreal` | orientation ↔ ESU local frame |

Also: `UCesiumWgs84Ellipsoid` static helpers (`LongitudeLatitudeHeightToEarthCenteredEarthFixed`, and inverse) for pure geodesy without a georeference. `UCesiumGlobeAnchorComponent` exposes per-actor `GetLongitudeLatitudeHeight()` / ECEF getters — **the cleanest way to emit truth**: each frame, read every anchored vehicle's lat/long/height directly, plus the camera's pose, into the telemetry export. ESU rotators give you correct heading/pitch/roll in a local tangent frame for sensor-model truth.

---

## 6. Licensing / ToS — Google Photorealistic 3D Tiles vs synthetic training data ⚠️

This is the central legal risk. From **Google Maps Platform / Map Tiles API Policies & ToS** (developers.google.com/maps/documentation/tile/policies, and the Photoreal 3D Tiles FAQ):

- **"You may not use Map Tiles API for any non-visualization use cases, such as: image analysis, machine interpretation, object detection or identification, geodata extraction or resale."** — Rendering EO frames to build a labeled detection/training dataset is squarely *non-visualization* use.
- **"Offline uses, including for any of the above"** are prohibited.
- **"You must not pre-fetch, index, store, or cache any Content except under the limited conditions"** of the agreement. Persisting rendered tiles or derived imagery is restricted.
- **Programmatically reading/recording measurements** (heights, distances, elevations) from the imagery is deemed derivative and **prohibited** — directly conflicts with exporting *georeferenced truth telemetry* derived from the tiles.
- **Allowed exception (overlays):** "You may overlay your own 3D objects on Photorealistic 3D Tiles **as long as the 3D objects aren't extracted, traced, or otherwise derived** by hand or machine from Photorealistic 3D Tiles." — So putting CARLA vehicles *on top* is fine, but the *background imagery itself* can't become a training asset.

**Bottom line:** producing a redistributable / model-training EO dataset whose pixels are Google's photoreal tiles is very likely a ToS violation regardless of having paid for the tiles. Visualization/demo is fine; **training data is not**.

**Open / compliant alternatives (recommend these for the actual dataset):**
- **Cesium OSM Buildings** — ODbL (OpenStreetMap-derived), open for commercial + derivative use with attribution/share-alike. Lower fidelity but unrestricted; pairs with Cesium World Terrain + an openly-licensed imagery overlay.
- **Self-hosted 3D Tiles from your own photogrammetry / open city models** (CityGML, USGS 3DEP/LiDAR-derived meshes, ESA/NASA open imagery) — no third-party ToS at all; full rights to export truth and train models. This is the cleanest long-term path for a deliverable digital-twin/ML pipeline.
- Note Cesium World Terrain and (historically) Bing imagery have their own commercial-subscription terms via ion — confirm imagery-overlay redistribution rights for any non-Google raster too. Self-hosted open imagery sidesteps this.

---

## 7. Determinism inside a headless CARLA fixed-timestep server ⚠️

The architectural tension: **CARLA runs synchronous, fixed-`delta_seconds`, often headless, and demands frame-perfect reproducibility.** **Cesium streams tiles asynchronously over the network on worker threads**, decoupled from the sim tick. Naively, frame N's capture may contain holes/wrong-LOD because tiles for the current camera haven't finished loading.

What's known / available to manage this:
- Cesium **does** have a "wait for all tile loads before snapping each frame" mode used by **Sequencer / Movie Render Queue**. **Caveat (community-documented):** it only activates when the **Level Sequence actually exists in the level**, not merely as a content-browser asset — otherwise Cesium never switches into the synchronous-wait mode and you get missing tiles/wrong LOD in the render. So a capture pipeline must drive frames through a path Cesium recognizes (MRQ / in-level Level Sequence) or implement an explicit "tiles loaded?" gate.
- **Off-screen / headless cameras** won't drive tile selection unless registered: use `ACesiumCameraManager` `SceneCaptures` / `AdditionalCameras` (v2.24.0) so the streamer prioritizes the capture viewpoint even with no player viewport. (Headless CARLA renders via off-screen/`-RenderOffScreen`; the capture camera must be a registered Cesium camera.)
- **Tiles-loaded signal:** the tileset exposes load progress (a `LoadProgress` / "tiles loaded %" indicator and `OnTileLoadProgress`-style events; cesium-native exposes `getRootTileAvailableEvent` and per-frame load-queue counts). A robust integration **polls until load progress == 100% / load queue empty for the current camera before triggering frame capture and advancing the deterministic tick.**
- **Recommended pattern for our pipeline:** decouple *capture* from real-time. For each fixed sim step: (1) advance CARLA physics deterministically, (2) update the registered Cesium capture camera, (3) **block/pump the game thread until Cesium reports tiles fully loaded** for that camera at the target SSE, (4) capture the EO frame + read globe-anchor truth, (5) proceed. This trades throughput for determinism — acceptable for offline dataset generation but **not** real-time. Because tile streaming is network-bound and non-deterministic in *timing*, you must gate on the *loaded state*, not on a fixed number of ticks.
- **Pre-warming / fully-offline option:** stage all needed tiles into Cesium's local request cache first (the SQLite request cache; v2.26.0 added a "Clear Request Cache" button + Blueprint call), then run capture from cache for repeatability. **But** the Google ToS caching prohibition (§6) collides with persistently caching Google tiles for offline batch rendering — another reason to prefer self-hosted/open tilesets, which can be cached/pre-warmed freely and even served from localhost for true determinism and offline operation.
- **Memory:** `MaximumCachedBytes` + `UnloadEditorTilesInPlayMode` (v2.26.0) help avoid OOM when both editor and PIE/headless instances hold tiles.

---

## Key risks / open questions

1. **(Showstopper) Google tiles ToS vs training-data goal.** Using Google Photorealistic 3D Tiles to generate a redistributable/ML training EO dataset with extracted georeferenced truth almost certainly violates Google Maps Platform ToS (non-visualization use, offline use, caching, derived measurements all prohibited). **Recommendation: build the dataset on Cesium OSM Buildings and/or self-hosted open photogrammetry; reserve Google tiles for non-training visualization/demos only.** This is the single biggest feasibility risk.

2. **Nadir fidelity of photogrammetry.** Even setting ToS aside, Google tiles look "melted"/smeared from straight-down and are muddy below ~500 m. For a convincing satellite/nadir EO look, slight obliquity helps, or use cleaner geometry sources. Confirm the EO sensor model's required GSD/altitude against achievable tile detail.

3. **Determinism vs async streaming.** No turnkey "frame-perfect synchronous tile load" for a headless CARLA fixed-step loop. Requires custom gating on Cesium's tiles-loaded signal before each capture (and registering the off-screen camera with `ACesiumCameraManager`). The known Sequencer "wait-for-tiles only if sequence is in the level" gotcha shows the synchronous path is fragile — validate it early.

4. **Plugin build against CARLA-patched engine.** Bundled v2.26.0 may need recompiling from source against this custom 5.7.4. Verify it links cleanly; budget time for a source build of cesium-unreal + cesium-native.

5. **ion commercial licensing & cost.** Commercial use needs a paid ion plan (~$499/mo tier commonly cited; verify) plus per-root-tile Google billing. Quota/cost at high capture volume is unquantified — model expected tile-request volume for batch dataset generation.

6. **Origin-shift vs georeferenced truth.** Recommend a *fixed* georeference origin at city center with all entities globe-anchored (disable continuous `UCesiumOriginShiftComponent` shifting) so truth export is stable and reproducible; UE 5.7 LWC double precision makes a fixed city-scale origin viable. Validate physics/precision at the city's far edges.

7. **Open question:** Does the CARLA-patched UE 5.7.4 retain standard world-origin rebasing (`UWorld::SetNewWorldOrigin`)? Source confirms the API exists; confirm CARLA didn't disable/alter rebasing in a way that conflicts with Cesium's origin shift component.

---

## Sources

- Cesium for Unreal releases & changelog: https://github.com/CesiumGS/cesium-unreal/releases ; https://cesium.com/learn/cesium-unreal/ref-doc/changes.html ; local `…\Cesiumfo9eaf76ca58f3V10\CHANGES.md` (v2.26.0) and `README.md`
- UE 5.7 support: https://community.cesium.com/t/plugin-for-ue5-7/44119 ; https://www.fab.com/listings/76c295fe-0dc6-4fd6-8319-e9833be427cd
- Georeference / origin shift / globe anchor: https://cesium.com/learn/cesium-unreal/ref-doc/classACesiumGeoreference.html ; https://cesium.com/learn/cesium-unreal/ref-doc/classUCesiumOriginShiftComponent.html ; https://cesium.com/learn/cesium-unreal/ref-doc/classUCesiumGlobeAnchorComponent.html ; https://cesium.com/learn/unreal/unreal-placing-objects/
- UE source: `g:\Projects\CarlaUE_5_7_4\UE_5_7_4\Engine\Source\Runtime\Engine\Classes\Engine\World.h` (`OriginLocation`, `RequestNewWorldOrigin`, `SetNewWorldOrigin`, `OriginOffsetThisFrame`)
- Photoreal tiles in Unreal: https://cesium.com/learn/unreal/unreal-photorealistic-3d-tiles/ ; quality forum: https://community.cesium.com/t/google-photorealistic-3d-tiles-poor-rendering-in-ue5/31437
- High-altitude LOD / SSE & FAQ: https://cesium.com/learn/unreal/unreal-faq/ ; https://community.cesium.com/t/resolution-streaming-google-3d-tiles/44919
- Google ToS / policies: https://developers.google.com/maps/documentation/tile/policies ; https://mapsplatform.google.com/resources/blog/commonly-asked-questions-about-our-recently-launched-photorealistic-3d-tiles/ ; https://developers.google.com/maps/documentation/tile/3d-tiles-overview
- Pricing: https://cesium.com/platform/cesium-ion/pricing/ ; https://community.cesium.com/t/google-3d-tiles-cost/27625
- Determinism / tile-load wait: https://community.cesium.com/t/force-tiles-loading-while-rendering/27360 ; https://cesium.com/learn/cesium-unreal/ref-doc/three-d-tiles-in-unreal.html
- CARLA synchronous mode: https://carla.readthedocs.io/en/latest/adv_synchrony_timestep/
