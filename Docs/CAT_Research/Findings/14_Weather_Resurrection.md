# 14 — CARLA Weather Resurrection (fog / rain / wind / clouds / wetness / dust), decoupled from the Cesium sun

Status: research + planning only. No engine/plugin/game code was changed by this pass.

Related: doc `13_Usable_Night_Lighting.md` and issue sbrett9/carla #4 (night lighting)
cover the *sun/sky lighting* track and explicitly defer weather. This doc covers the
*weather effects* track (precipitation, fog, wind, clouds, wetness, dust) and how to
restore it **without** reintroducing a second sun that would fight `ACesiumSunSky`.

---

## 1. Root cause: `WeatherClass` is null, so no `AWeather` spawns

The generated OpenDriveMap digital-twin world never creates an `AWeather` actor, so
every `set_weather` / `get_weather` call fails with "weather is disabled". The chain:

1. `ACarlaGameModeBase::InitGame` decides the weather actor. It first looks for an
   `AWeather` already placed in the level, else spawns one from `WeatherClass`, else logs
   the error:
   - `Unreal/CarlaUnreal/Plugins/Carla/Source/Carla/Game/CarlaGameModeBase.cpp:137-147`
     ```
     WeatherActor = UGameplayStatics::GetActorOfClass(World, AWeather::StaticClass());
     if (WeatherActor != nullptr)      Episode->Weather = ...;   // placed-in-level path
     else if (WeatherClass != nullptr) Episode->Weather = World->SpawnActor<AWeather>(WeatherClass);
     else UE_LOG(LogCarla, Error, TEXT("Missing weather class!"));
     ```
2. `WeatherClass` is a `UPROPERTY TSubclassOf<AWeather>` on the C++ base and is **never
   assigned in C++** — a repo-wide search for `SetWeatherClass` / `WeatherClass =` returns
   zero hits. It can only be set as a default on a Blueprint subclass of the game mode.
   - `Unreal/CarlaUnreal/Plugins/Carla/Source/Carla/Game/CarlaGameModeBase.h:171`
3. The project-global game mode is the `CarlaGameMode` Blueprint:
   - `Unreal/CarlaUnreal/Config/DefaultEngine.ini:15`
     `GlobalDefaultGameMode=/Game/Carla/Blueprints/Game/CarlaGameMode.CarlaGameMode_C`
   - That Blueprint's dependency table references only its three actor factories
     (`BP_VehicleFactory`, `BP_WalkerFactory`, `BP_BlueprintFactory`) and the spectator —
     **no weather Blueprint**. Its `WeatherClass` default is therefore empty.
     (byte-grep of `Content/Carla/Blueprints/Game/CarlaGameMode.uasset`)
4. The `OpenDriveMap` template level places **no** `AWeather` actor and sets **no** GameMode
   override, so neither InitGame branch fires:
   - `Content/Carla/Maps/OpenDriveMap.umap` — actors are `OpenDriveGenerator_2`,
     `PlayerStart_1`, `DirectionalLight_1`, `SkyLight_1`, `LevelScriptActor` only.
5. Every digital-twin run reuses this same weather-less template. `LoadOpenDriveEpisode`
   copies the client OpenDRIVE to the server and then `LoadEpisode("OpenDriveMap")`:
   - `LibCarla/source/carla/client/detail/Simulator.cpp:116-125`

The Cesium bridge already documents this exact gap in a code comment:
- `Unreal/CarlaUnreal/Plugins/CesiumCarlaBridge/Source/CesiumCarlaBridge/Private/CesiumHeightSampler.cpp:283`
  *"The generated OpenDriveMap has no weather/sun actor ('Missing weather class'), so the scene is unlit."*

### Upstream parity: this port dropped a `LoadClass` fallback
Upstream `carla-simulator/carla` on `ue5-dev` added a runtime fallback in `InitGame`: when
`WeatherClass` is null it does
`LoadClass<AWeather>(nullptr, TEXT("/Game/Carla/Blueprints/Weather/BP_CarlaWeather.BP_CarlaWeather_C"))`
before spawning, so weather works even if a game-mode BP never set `WeatherClass`. Our
port's `CarlaGameModeBase.cpp:143-147` lacks that branch — it goes straight from
`WeatherClass == nullptr` to `UE_LOG(... "Missing weather class!")`. Restoring that upstream
fallback is the single smallest fix and gives weather across every map, not just the digital
twin. (Upstream also confirms `WeatherClass` is normally set on the *Blueprint defaults* of a
game-mode subclass, and that a placed `AWeather`/`ASkyBase` actor in a town level is the
alternate path — matching what the shipped town `.umap`s here do for the sky.)

### The full RPC path is intact except for the missing actor
`world.set_weather` works end to end until it reaches the null actor:
- Python: `CarlaNet/python/carlanet/__init__.py:1710` `set_weather` → `SetWeatherParametersAsync`
- C++ RPC: `Unreal/CarlaUnreal/Plugins/Carla/Source/Carla/Server/CarlaServer.cpp:995-1006`
  `set_weather_parameters` → `Episode->GetWeather()` returns `nullptr` →
  `RESPOND_ERROR("... weather is disabled")`.
- `get_weather_parameters` (`CarlaServer.cpp:983-993`) returns a default-constructed
  `WeatherParameters` when the actor is null.

**Conclusion:** the fix is to give the OpenDriveMap world an `AWeather` actor. Nothing in
the transport, RPC, or preset table is broken.

---

## 2. Required content inventory — all present in this port

The weather content ships in the tree; nothing needs to be ported from upstream to get a
minimal system running.

| Asset | Path | Role | Present |
|---|---|---|---|
| `BP_CarlaWeather` | `Content/Carla/Blueprints/Weather/BP_CarlaWeather.uasset` | `AWeather` subclass (the missing `WeatherClass`). Its `RefreshWeather` event drives the sky. | Yes |
| `BP_Carla_Sky` | `Content/Carla/Blueprints/LevelDesign/BP_Carla_Sky.uasset` | `ASkyBase` subclass — owns DirectionalLight Sun+Moon, `SkyAtmosphere`, `VolumetricCloud`, `ExponentialHeightFog`, `SkyLight`, `PostProcess`. | Yes |
| `M_screenDrops` | `Content/Carla/Static/GenericMaterials/FX/ScreenDust/M_screenDrops.uasset` | Screen-space rain-on-lens post-process (precipitation). | Yes |
| `M_screenDust_wind` | `Content/.../FX/ScreenDust/M_screenDust_wind.uasset` | Screen-space dust-storm post-process. | Yes |
| Sky curves / cloud textures / material params | `Content/Carla/Blueprints/Weather/**` | Time-of-day and cloud driving curves used by `BP_Carla_Sky`. | Yes |

Evidence that the shipped towns wire this via a *placed* sky actor (not `WeatherClass`):
- `Town10HD_Opt.umap`, `Town_C.umap`, `Mine_01.umap` each place a `BP_Carla_Sky_C_*`
  instance in the level. None place a `BP_CarlaWeather` instance and no game-mode BP sets
  `WeatherClass`, so **weather (precip/dust) is inert in this port's towns too** — only the
  sky lighting works there. This is a port-wide regression, not OpenDriveMap-specific.

The `AWeather` C++ base only stores `FWeatherParameters`, applies the two screen-space
blendables to `ASceneCaptureCamera` sensors, and fires `RefreshWeather`
(`BlueprintImplementableEvent`); all real fog/cloud/wetness work lives in `BP_CarlaWeather`
→ `BP_Carla_Sky`:
- `Unreal/CarlaUnreal/Plugins/Carla/Source/Carla/Weather/Weather.cpp:31-86`
- `Unreal/CarlaUnreal/Plugins/Carla/Source/Carla/Weather/Sky.h` (`ASkyBase`, abstract)

---

## 3. Sun-separation strategy (do NOT reintroduce a competing sun)

The digital-twin sun is owned by a single runtime-spawned `ACesiumSunSky`:
- Find-or-spawn in `CesiumHeightSampler.cpp:287-307` (via the `configure_cesium_georeference`
  RPC, `CarlaServer.cpp:469-492`), then one `UpdateSun()`; defaults SolarTime 13:00, TZ -5.
- `ACesiumSunSky` owns its own `DirectionalLight`, `SkyLight`, and `SkyAtmosphere`
  (`CesiumForUnreal/.../Public/CesiumSunSky.h:40-46`).

`BP_Carla_Sky` (the CARLA sky) *also* owns a DirectionalLight (Sun + Moon), `SkyLight`, and
`SkyAtmosphere` (`Weather/Sky.h:35-47`). Placing it as-is would create a **second sun,
second sky-atmosphere, and second sky-light** competing with Cesium — the outcome the user
wants to avoid.

Three options, compared:

**Option 1 — Leaner C++ weather-effects actor (recommended for MVP).**
Add a small non-abstract `AWeather` subclass in C++ (or a trimmed Blueprint) that keeps
only the effects the Cesium sun does *not* provide, and touches no lighting:
- Precipitation + dust screen-space blendables — already implemented in the C++ base
  (`Weather.cpp:31-51`), so this needs no Blueprint at all for MVP.
- One `ExponentialHeightFog` and one `VolumetricCloud` component created/owned by this
  actor and driven from `FWeatherParameters` in an overridden `RefreshWeather`/native tick.
- **No DirectionalLight, no SkyAtmosphere, no SkyLight** — those stay with Cesium.
Pros: no risk of a second sun; smallest surface; deterministic; no dependency on the
opaque `BP_Carla_Sky` graph. Cons: reimplements the fog/cloud parameter mapping that
`BP_Carla_Sky` already contains.

**Option 2 — Reuse `BP_Carla_Sky` but disable its sun/atmosphere.**
Set `WeatherClass = BP_CarlaWeather` on a game-mode Blueprint (or place the actors), then in
`BP_Carla_Sky` disable/detach `DirectionalLightComponentSun`, `DirectionalLightComponentMoon`,
`SkyAtmosphereComponent`, and `SkyLightComponent` (leave `ExponentialHeightFog`,
`VolumetricCloud`, `PostProcess`). Cesium keeps atmosphere + sun.
Pros: reuses the existing, tuned fog/cloud/wetness curves. Cons: `BP_Carla_Sky`'s graph is
built assuming it owns the sun — its cloud/fog colours are driven off *its* sun angle and
time-of-day curves, so with the sun stripped the clouds/fog may be mislit relative to the
Cesium sun unless the sun-angle inputs are re-sourced from Cesium (see §4). Higher content
risk; harder to verify from disk.

**Option 3 — Keep `BP_Carla_Sky` intact, make it defer to Cesium every frame.**
Keep the components but drive its DirectionalLight rotation and SkyAtmosphere to *mirror*
`ACesiumSunSky` rather than compute independently. This is effectively "two suns kept in
lockstep" — brittle (double-lighting, doubled atmosphere scattering) and contrary to the
single-authority design. Not recommended.

**Recommendation:** MVP with **Option 1** (fog + rain/dust, zero lighting). If richer
volumetric clouds tied to the tuned CARLA curves are wanted later, migrate to **Option 2**
with the sun-angle inputs re-sourced from Cesium (§4). Either way, the `WeatherClass` /
placed-actor wiring is the same one-line enablement (§6, Phase 0).

The `FWeatherParameters.SunAzimuthAngle` / `SunAltitudeAngle` fields
(`WeatherParameters.h:29,32`) must **not** drive any light in the resurrected system. Treat
them as read-only status mirrored from Cesium (§4). Conflating sun angle into weather is the
design mistake this track deliberately reverses.

---

## 4. Informatics sync — mirror the Cesium sun into `FWeatherParameters`

Goal: `get_weather()` should report `SunAltitudeAngle` / `SunAzimuthAngle` that match what
`ACesiumSunSky` actually computed, so downstream consumers see one consistent sun — without
those fields driving a light.

`ACesiumSunSky` exposes the current sun geometry as `BlueprintReadOnly` outputs, refreshed by
`UpdateSun()`:
- `double Elevation` — degrees above horizon from the georeference origin —
  `CesiumSunSky.h:391-392`.
- `double CorrectedElevation` — atmospheric-diffraction-corrected — `CesiumSunSky.h:398-399`.
- `double Azimuth` — degrees clockwise from North toward East — `CesiumSunSky.h:405-406`.

Mapping to CARLA:
- `SunAltitudeAngle` ← `Elevation` (both are degrees above horizon; use `Elevation`, not
  `CorrectedElevation`, for a geometric truth value — flag the choice in code).
- `SunAzimuthAngle` ← convert `Azimuth`. CARLA azimuth is 0..360; Cesium is
  North-clockwise-toward-East. Verify the zero/rotation-direction convention against
  `Weather.cpp` logging and adjust (likely identical or a fixed offset). Round-trip test.

**Where the sync lives (recommended):** in the Cesium bridge, right after each
`SunSky->UpdateSun()`. `ConfigureCesiumForOrigin` already spawns/updates the sun
(`CesiumHeightSampler.cpp:297-300`) and is the natural owner of "sun state changed". Have it
push `Elevation`/`Azimuth` into the episode's `AWeather` via
`AWeather::SetWeather(...)` (the non-notifying setter, `Weather.h:37-38` / `Weather.cpp:88-91`)
so it updates the stored parameters *without* firing `RefreshWeather` (which would re-apply
effects). This keeps the mapping one-directional: Cesium → weather status only.

When a future time-of-day RPC advances `SolarTime` and calls `UpdateSun()` (the Phase 0 of
doc 13 / issue #4), the same hook re-syncs the angles. This makes the two tracks compose:
the night-lighting track owns time-of-day, the weather track reads it.

Do **not** apply the sync in `set_weather_parameters`: if a client sends sun angles they
should be ignored/overwritten by the Cesium value, so the client's angles never move a light.

---

## 5. Volumetric clouds + ExponentialHeightFog vs Cesium — visual-conflict risks

`ACesiumSunSky` renders a physically-based `SkyAtmosphere` with aerial perspective. Adding
CARLA's atmospheric elements on top carries these risks:

- **Two `SkyAtmosphere` components double the scattering.** Only one `SkyAtmosphere` should
  be active. Cesium's must remain the authority; any CARLA-side `SkyAtmosphere` (from
  `BP_Carla_Sky`) must be disabled (Options 1 and 2 both do this).
- **`VolumetricCloud` lighting depends on the active DirectionalLight.** Volumetric clouds
  are lit by the scene's directional light(s). With the CARLA sun disabled, clouds must be
  lit by the **Cesium** `DirectionalLight`. Confirm the CARLA sky's clouds reference the
  correct (Cesium) light, or clouds render unlit/black. A single VolumetricCloud in the
  world, lit by the Cesium sun, is the target.
- **Fog vs aerial perspective double-count.** `ExponentialHeightFog` and Cesium's aerial
  perspective both attenuate distance. Stacking them over-fogs the far field. Keep fog
  density modest and validate against the photoreal tiles; consider disabling Cesium aerial
  perspective when CARLA fog is active, or vice-versa. Fog is the MVP effect, so this needs
  early visual sign-off (user-verified, per project convention).
- **Cloud shadows on unlit photoreal tiles.** The Cesium 3D tiles carry daytime-baked
  radiance (see doc 13). VolumetricCloud shadows cast by the Cesium sun will be *additive*
  over already-lit albedo — plausible but not physically exact. Acceptable for effects;
  note it on any EO product.
- **`PostProcess` volume conflicts.** `BP_Carla_Sky` and the weather actor both carry
  `PostProcessComponent`s; overlapping unbounded post-process volumes fight over exposure /
  color grading, which the EO exposure knobs (`--ev`) also touch. Keep weather post-process
  scoped to the screen-FX blendables only; leave exposure to the EO/night track.

---

## 6. Phased plan (layer tags: [engine-C++] / [BP+content] / [CarlaNet/Python])

**Phase 0 — Enablement: give the OpenDriveMap world an `AWeather` actor.**
- Smallest, upstream-parity fix: restore the `LoadClass<AWeather>(".../BP_CarlaWeather.
  BP_CarlaWeather_C")` fallback in `CarlaGameModeBase.cpp` `InitGame` (the branch upstream
  `ue5-dev` has and this port dropped, §1). One code change, fixes every map, git-visible,
  no .uasset edit. [engine-C++]
- Digital-twin-scoped alternative: in the Cesium bridge's `ConfigureCesiumForOrigin`, after
  the sun-sky find-or-spawn, also find-or-spawn the episode weather actor (mirrors the
  existing sun-spawn block, `CesiumHeightSampler.cpp:287-307`) and register it on the episode
  (`Episode->Weather`, see `CarlaEpisode.h:154,395`). Use this if a *leaner effects-only*
  actor (Option 1) is wanted instead of `BP_CarlaWeather`. Other alternatives: set
  `WeatherClass` on the `CarlaGameMode` Blueprint default, or place the actor in
  `OpenDriveMap.umap` (both opaque .uasset edits — less preferred per project convention).
- Acceptance: `world.get_weather()` succeeds; `world.set_weather(...)` no longer errors
  "weather is disabled".

**Phase 1 — MVP: fog + rain/dust screen FX via `set_weather`.**
- Use the effects-only weather actor (Option 1). Precipitation + dust blendables already
  work in the C++ base (`Weather.cpp:31-51`) once sensors exist. Add one
  `ExponentialHeightFog` driven from `FogDensity`/`FogDistance`/`FogFalloff`. No lighting.
  [engine-C++]
- Wire the informatics sync (§4): push Cesium `Elevation`/`Azimuth` into the weather actor
  after `UpdateSun`. [engine-C++]
- Verify presets flow through unchanged; the CarlaNet preset table already exists
  (`CarlaNet/python/carlanet/__init__.py:2939`). [CarlaNet/Python] (likely no change)
- Acceptance (user-verified): `HardRainNoon` shows rain-on-lens on an RGB sensor + fog in
  the scene; `get_weather()` sun angles match the Cesium sun.

**Phase 2 — Clouds + wind.**
- Add a single `VolumetricCloud` lit by the Cesium `DirectionalLight`, driven by
  `Cloudiness`; ensure exactly one SkyAtmosphere (Cesium) remains active (§5). Wind
  (`WindIntensity`) drives cloud/precip motion and any foliage response. If reusing
  `BP_Carla_Sky` (Option 2), disable its sun/atmosphere and re-source its sun-angle inputs
  from the synced values. [engine-C++] and/or [BP+content]
- Acceptance (user-verified): `CloudyNoon` vs `ClearNoon` visibly differ; clouds are lit,
  not black; far field is not double-fogged.

**Phase 3 — Wetness + dust polish.**
- `Wetness`/`PrecipitationDeposits` road/material response (wet-surface roughness), dust
  storm tuning. Wetness on the photoreal tiles is limited (unlit baked albedo) — apply to
  CARLA-authored surfaces (roads, vehicles) and note the tile limitation. [engine-C++]/[BP+content]
- Acceptance (user-verified): `WetNoon`/`DustStorm` read correctly on controllable surfaces.

Cross-cutting: never let the weather actor own a DirectionalLight/SkyAtmosphere/SkyLight;
run all visual checks via `HighResShot` (per project screenshot convention) and let the user
confirm rendering — `is_compiled_ok` and self-screenshots do not prove correct rendering.

---

## 7. Open questions / to verify during implementation

- Cesium `Azimuth` vs CARLA `SunAzimuthAngle` convention (offset/handedness) — round-trip test.
- Whether the synchronous world-tick path (SCTMV) renders the screen-FX blendables and
  volumetric clouds correctly (dither/TAA-dependent effects are known-fragile under sync;
  cf. the vehicle-fade findings).
- Whether disabling Cesium aerial perspective is needed when CARLA `ExponentialHeightFog`
  is active, or if modest fog density coexists acceptably.
- Confirm the placed-actor regression scope: shipped towns also lack `WeatherClass`, so a
  general fix (game-mode default or bridge-spawn) would restore weather across all maps.
