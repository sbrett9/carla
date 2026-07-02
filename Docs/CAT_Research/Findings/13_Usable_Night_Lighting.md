# 13 — Usable Night-Time Lighting for the Cesium Photoreal Digital Twin

Status: Research / planning. No engine, plugin, or game code changed by this document.
Scope: visible-spectrum (electro-optical) low-light rendering of the Google/Cesium
photorealistic 3D-tiles world plus CARLA vehicles. Thermal/IR is explicitly out of scope.

## 1. Problem statement

The digital-twin scene is lit by a single `ACesiumSunSky` actor. Its solar position is
computed from the georeference latitude/longitude, the calendar date, and a `SolarTime`
(hours from midnight). We are adding a time-of-day system in which `SolarTime` can advance
to and past midnight. When it does, the scene goes **near-black**, and the result is not a
usable "night" for EO imagery. There are two independent reasons:

1. **CesiumSunSky ships no night model.** It has exactly one directional light (the sun)
   plus a real-time-captured `SkyLight` and a `SkyAtmosphere`. When the sun drops below the
   horizon there is no moon, no artificial-light contribution, and the atmosphere stops
   scattering, so the physically-based result of "no light source above the horizon" is
   darkness. This is correct behaviour for the model it implements; it just isn't the model
   we need.

2. **The photoreal tiles have daytime lighting baked in.** Asset 2275207 (Google-style
   photogrammetry) is an *unlit* textured mesh: its albedo textures already contain the
   sun-lit colour, cast shadows, and ambient occlusion from the aerial capture (a clear
   daytime pass). Even if we add plausible night illumination, those baked highlights and
   hard shadows remain in the texture and will read as "sunlit" no matter what direction our
   virtual moon points. "Night" here is fundamentally a **re-lighting** problem, not a
   "turn the sun off" problem — and re-lighting baked-in radiance is not something we can
   fully undo.

## 2. Current behaviour (evidence from source)

- Sun/sky is a single `ACesiumSunSky` actor.
  - `ACesiumSunSky()` constructor creates one `UDirectionalLightComponent` (the sun,
    intensity 111000 lux), one `USkyLightComponent` (real-time capture,
    `bLowerHemisphereIsBlack = false`), and one `USkyAtmosphereComponent`. There is **no**
    moon light and **no** second directional light.
    `carla\Unreal\CarlaUnreal\Plugins\CesiumForUnreal\Source\CesiumRuntime\Private\CesiumSunSky.cpp:46-110`
  - `UpdateSun_Implementation()` computes the sun elevation/azimuth from georeference
    lat/lon + `TimeZone` + `SolarTime` + Day/Month/Year via
    `USunPositionFunctionLibrary::GetSunPosition`, then rotates only the sun directional
    light. Nothing else is driven by time of day (no dusk tint, no artificial lights, no
    moon). `CesiumSunSky.cpp:405-467`
  - The header exposes `SolarTime` (hours from midnight, `ClampMin 0 / ClampMax 23.9999`),
    `TimeZone`, Day/Month/Year, and DST as the entire time-of-day surface. There is **no**
    moon, night-intensity, ambient-floor, or exposure property.
    `carla\Unreal\CarlaUnreal\Plugins\CesiumForUnreal\Source\CesiumRuntime\Public\CesiumSunSky.h:60-142`
- The sun/sky is spawned programmatically by the Cesium↔CARLA bridge, not placed in a level.
  A `CesiumSunSky` is find-or-spawned during `ConfigureCesiumForOrigin`, defaulting to
  `SolarTime 13:00`, `TimeZone -5` (Chicago daytime), and `UpdateSun()` is called once.
  `carla\Unreal\CarlaUnreal\Plugins\CesiumCarlaBridge\Source\CesiumCarlaBridge\Private\CesiumHeightSampler.cpp:283-314`
- There is **no time-of-day RPC today.** A repo-wide search for `SetSolarTime` /
  `set_time_of_day` finds only the CesiumSunSky C++/header itself; nothing in the bridge RPC
  surface (`CesiumHeightSampler.h/.cpp`) nor in the Python layer sets `SolarTime` at runtime.
  So advancing time-of-day is itself unbuilt — it needs a bridge function (analogous to the
  existing `SetLayerVisible` / `SetLayerCollision` / `SetLayerVerticalOffset` RPCs in
  `CesiumHeightSampler`) that sets `SolarTime`/date and calls `UpdateSun()`.
- The only night-relevant lever that exists today is a **fixed camera exposure**:
  `--ev` maps to the CARLA camera `exposure_compensation` attribute in both the observer and
  the SCTMV harness. `carla\CarlaNet\python\eo_observer.py:56,246-247` and
  `carla\CarlaNet\python\SCTMV.py:167-168`. This brightens the final image but does not add
  any light to the scene.
- **Prior art (do not resurrect):** CARLA's own abstract sky base already declares a
  `UDirectionalLightComponentMoon`, a `UVolumetricCloudComponent`, and a `USkyLightComponent`
  — `carla\Unreal\CarlaUnreal\Plugins\Carla\Source\Carla\Weather\Sky.h:13,38,44`. This shows
  the moon-as-second-directional-light pattern is idiomatic in UE, but CARLA weather is inert
  here ("Missing weather class") and is a separate research track. Use `ASkyBase` only as a
  reference for the moon-light pattern, not as something to re-enable.

## 3. What a "usable night" needs, with options

For an EO context the goal is a **physically plausible low-light visible-spectrum look**:
the scene is dark but readable, moonlit surfaces have a cool low key, shadows are soft, and
the noise/adaptation behaviour resembles a low-light sensor. It does **not** need
photometric night accuracy, and it explicitly does not include thermal/IR.

| # | Approach | What it buys | Fidelity | Effort | Where it lives |
|---|----------|--------------|----------|--------|----------------|
| A | **Ambient floor**: raise `SkyLight` intensity floor + a small below-horizon atmosphere/sky-luminance term so the scene never reaches pure black | Scene is dim-but-visible instead of 0; cheapest "not black" | Low (flat, moonless-overcast look) | Low | CesiumSunSky C++ (add a night `SkyLight` floor) or a bridge tweak of the spawned actor |
| B | **Moon as a second low-intensity directional light** + cool tint, casting soft shadows on vehicles/props. Angle either (b1) a cheap fixed offset from the anti-sun vector, or (b2) a real lunar ephemeris direction | A believable key light: vehicles are modelled, cast moon shadows, cool colour | Medium (b1) / Medium-High (b2) | Medium (b1) / High (b2, needs an ephemeris) | CesiumSunSky C++ (add `UDirectionalLightComponent` Moon, driven in `UpdateSun`), pattern per `ASkyBase` |
| C | **Night exposure / eye-adaptation**: enable auto-exposure (histogram eye-adaptation) with a night min/max EV, or drive the fixed `--ev` per time-of-day | Makes A/B actually readable; without it any floor is either crushed or blown | Medium | Low-Med | Post-process (level/CesiumSunSky) for auto-exposure; `--ev` already exists as the manual lever in Python/SCTMV |
| D | **Artificial lights** (street lights, building emissive windows) placed as *separate* actors from OSM (e.g. `highway=street_lamp` nodes → point/spot lights; building footprints → emissive facades) | The single biggest driver of a *convincing* city night; localized pools of light | High | High | New content/actors + OSM ingestion (Python world-gen + engine spawn); NOT bakeable into the tiles |
| E | **Accept baked-daytime limitation**: document that tile albedo/shadows read as day-lit and cannot be fully de-lit | Honesty about the ceiling of realism | n/a | n/a | Documentation |

Notes on each:

- **A (ambient floor).** `SkyLight` already has `bLowerHemisphereIsBlack = false`, so a floor
  is a natural knob. Cheapest path to "not pure black." On its own it looks like a flat,
  moonless overcast — flat and shadowless — but it guarantees a usable base image.

- **B (moon light).** The idiomatic UE approach (and what `ASkyBase` anticipates). A second
  directional light at low intensity (order ~0.1–0.5 lux equivalent, cool ~4100K) pointed
  roughly opposite the sun gives modelled vehicles and real cast shadows on the CARLA actors
  and any added props. **b1 (cheap):** derive the moon direction as a fixed offset from the
  anti-sun vector — good enough visually, ephemeris-incorrect. **b2 (accurate):** compute the
  true lunar azimuth/elevation from date/time/lat-lon (same inputs `UpdateSun` already has);
  needed only if the EO product must be geometrically truthful about moon direction/phase.
  Caveat: the moon light will correctly light *vehicles and added actors*, but it lights the
  *tiles* on top of their baked daytime radiance — see §4.

- **C (exposure / adaptation).** Dark scenes are unusable without exposure that adapts to the
  low key. Two sub-options: engine **auto-exposure** (eye-adaptation) with night min/max EV
  (hands-off, "camera adjusts to the dark"), or keep the deterministic **fixed `--ev`** lever
  and schedule it by time-of-day. For a *reproducible* EO dataset, fixed `--ev` per time step
  is preferable (auto-exposure makes frames non-deterministic); for interactive viewing,
  auto-exposure is nicer. Both are cheap because the plumbing (`exposure_compensation`)
  already exists.

- **D (artificial lights).** This is what actually sells a city at night, and it is also the
  most work. The photoreal tiles are unlit meshes we cannot edit, so emissive windows and
  street-lamp pools must be added as **separate** actors. OSM already gives us the geometry we
  need: `highway=street_lamp` nodes → small point/spot lights along roads;
  building footprints (the OSM Buildings path already contemplated in doc 08) → emissive
  facade cards or window-grid materials. This is feasible and reuses the existing OSM
  ingestion, but it is a content + engine + Python effort and should be phased last.

- **E (baked-daytime ceiling).** State the honest limit — see §4.

## 4. The hard limit: baked daytime lighting in the tiles

This must be stated plainly because it caps the achievable realism regardless of how much
lighting we add:

- Asset 2275207 is *unlit* photogrammetry. The mesh textures already encode the aerial
  capture's **sun-lit albedo, cast shadows, and ambient occlusion** from a daytime pass.
- Any night lighting we add is *additive on top of* that baked radiance. Roofs that were lit
  by the midday sun will still look sun-lit; hard building shadows baked into the pavement
  will still point the way the capture sun pointed — which will not agree with our virtual
  moon. There is no per-texel "un-shadow" operation that recovers the true surface albedo
  from a single baked capture.
- Practical consequence: with A+B+C you get a *credibly dark, moody* image, but it is best
  described as **"dusk/low-key over a day-captured city,"** not a physically faithful night.
  The added actors from D (street/window light) read as genuinely night-correct because they
  are real light sources we control; the *tile surfaces between them* remain day-baked.
- For EO deliverables this means: night truth-telemetry (geometry, positions, solar/lunar
  angles) can be fully correct, but the **pixels** carry an irreducible daytime-albedo bias
  in the tile surfaces. Document this as a known limitation on any night EO product.

## 5. EO-specific framing

- Target is a **visible-spectrum low-light** look, not thermal/IR. Thermal would require an
  entirely different emissive/material model and is out of scope here.
- "Physically plausible" (dark, cool, readable, plausible moon key + local artificial pools)
  is the goal — not photometric night accuracy.
- Determinism matters for datasets: prefer scheduled fixed `--ev` (option C, fixed) over
  auto-exposure when generating reproducible EO frames; the truth telemetry (positions,
  solar/lunar angles) stays independent of the render look either way.

## 6. Phased recommendation

Ordered minimum-viable → richer. Each phase is independently shippable.

- **Phase 0 — Time-of-day plumbing (prerequisite).** There is no runtime `SolarTime` setter
  today (§2). Add a bridge RPC (mirroring the existing `SetLayer*` functions in
  `CesiumHeightSampler`) that sets `SolarTime`/date on the spawned `CesiumSunSky` and calls
  `UpdateSun()`, plus a Python/SCTMV knob to drive it. *Engine C++ (bridge) + Python.*
  Without this, "advance to midnight" cannot even be exercised.

- **Phase 1 — "Not pure black" (ambient floor + night exposure).** Add a night `SkyLight`
  intensity floor (option A) so the scene never crushes to zero, and schedule a night `--ev`
  (option C, fixed) so it is readable. This is the cheapest usable night and touches only the
  CesiumSunSky spawn config / a small C++ floor plus existing Python exposure. *CesiumSunSky
  C++ (small) + Python knob.*

- **Phase 2 — Moon key light.** Add a second, low-intensity, cool directional light to
  CesiumSunSky, driven in `UpdateSun`, starting with the **cheap anti-sun offset (b1)**;
  upgrade to a **real lunar ephemeris (b2)** only if EO truth requires correct moon
  direction/phase. Gives modelled vehicles and real cast shadows. *Engine C++ (CesiumSunSky),
  pattern per `ASkyBase`.*

- **Phase 3 — Artificial city lights from OSM.** Street lamps (`highway=street_lamp`) and
  emissive building facades as separate actors, reusing the OSM ingestion path (doc 08's OSM
  Buildings). Highest realism payoff, highest effort. *Python world-gen + engine actor spawn
  + content.*

- **Cross-cutting — Document the baked-daytime ceiling (§4)** in any night EO product notes
  so consumers know tile surfaces carry a daytime-albedo bias.

### Where each piece lives (summary)

| Layer | Pieces |
|-------|--------|
| Engine C++ (CesiumSunSky) | Moon directional light (Phase 2), night SkyLight floor (Phase 1) |
| Engine C++ (Cesium↔CARLA bridge) | `SolarTime`/date RPC + `UpdateSun()` (Phase 0), spawn-config night defaults (Phase 1) |
| Cesium config | Ion assets unchanged; tiles are unlit and uneditable (the §4 limit) |
| Python / SCTMV knobs | time-of-day setter call (Phase 0), scheduled `--ev` night exposure (Phase 1), OSM lamp/building ingestion (Phase 3) |
| Content / actors | Street-lamp lights, emissive facades (Phase 3) |

## 7. Recommendation in one line

Build Phase 0 (time-of-day RPC) + Phase 1 (ambient floor + night exposure) first for a
cheap, usable "not-black" night; add the Phase 2 moon key light for modelled vehicles and
shadows; treat Phase 3 (OSM street/building lights) as the high-effort realism payoff — and
document up front that the photoreal tiles' baked daytime albedo/shadows set a hard ceiling
on how "night" the tile surfaces themselves can ever look.
