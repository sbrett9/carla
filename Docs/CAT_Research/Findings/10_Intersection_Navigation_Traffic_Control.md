# 10 — Intersection Navigation & Traffic Control: stops, lights, signs, priority

**Status:** Gap analysis (2026-06-25) → **IMPLEMENTATION largely complete (2026-07-14)**. The three-layer
gap is now closed for the core protocol: ambient traffic **obeys traffic lights and stop signs** in
generated digital-twin worlds. Sections §1–§8 are the original analysis (kept for the reasoning);
the **Implementation status** note immediately below records what has been built and runtime-validated.
**Date:** 2026-06-25
**Scope:** Determine how real-world intersection behavior (stopping at traffic lights and stop signs,
yielding, honoring speed limits) can be simulated for ambient traffic in the digital-twin pipeline now
driven by [`SCTMV.py`](../../../CarlaNet/python/SCTMV.py), what the existing system already supports,
where the gaps are, and whether the OSM file alone is a sufficient data source or other sampled sources
are required.
**Related:** [07 — Road-Network Filtering](07_RoadNetwork_Filtering.md) (the conversion flags),
[02 — CARLA OSM MapGen](02_CARLA_OSM_MapGen.md); the deferred grouping bug
[sbrett9/carla#1](https://github.com/sbrett9/carla/issues/1); `adv_traffic_manager.md`.

> **Revision (2026-07-06):** The world-build entry point moved from `test_digital_twin.py` (now retired —
> last touched 2026-06-23) to [`SCTMV.py`](../../../CarlaNet/python/SCTMV.py), which owns the identical
> pipeline: `make_options` (`SCTMV.py:252`) still sets `GenerateTrafficLights = False` (`:257`), and
> `build_world` (`:273`) calls `generate_world_from_osm_with_elevation` (`:328`). The three-layer gap
> below is unchanged — only the entry-point references and a few drifted line numbers are refreshed.
> Two newer facts are folded in: the .NET TM produces no motion under *synchronous* world ticking, so
> SCTMV runs a synchronous-world + asynchronous-TM hybrid (§3); and the CesiumSunSky time-of-day work
> ([#5](https://github.com/sbrett9/carla/issues/5)) proved the `FWorldObserver` snapshot-extension
> pattern that the §7/§8 ALSM un-stub would reuse.

---

## Implementation status (2026-07-14)

The three-layer gap of §1 is now largely closed. Ambient traffic **stops at red lights and at stop
signs** in worlds built by `SCTMV.py`. What follows §1–§8 is the original gap analysis, kept for its
reasoning; this note records what was built and validated.

**Layer B (runtime TM) — DONE + runtime-validated.** `CarlaNet.TrafficManager/Stages/ALSM.cs` no longer
hardcodes `Green`/`0`. It reads each vehicle's real `traffic_light_state`, `at_traffic_light`, and
`speed_limit` from the `FWorldObserver` snapshot's per-vehicle `VehicleData` union — the server already
serialized these (`WorldObserver.cpp`); ALSM just ignored them. Added `ActorSnapshot.ParseVehicleState`
(byte offsets: `speed_limit` f32 @ union+19, `traffic_light_state` u8 @ +23, `has_traffic_light` bool @
+24) and `CarlaClient.GetActorVehicleState`. A **separate runtime defect** was found while validating on
stock Town10HD: `UTrafficLightComponent`/`ATrafficLightBase` held an unbounded, duplicate-ridden
`Vehicles` broadcast list and pushed light state to it unconditionally, so a green light overwrote the
red state of cars stopped at a *different* light and they ran the red. Fixed by making the controller's
`TrafficLight` pointer authoritative (guard the `SetLightState` broadcast and the overlap-exit on
`GetTrafficLight()==GetOwner()`) plus `AddUnique`. The dormant `ATrafficLightBase::Vehicles` twin got the
same fix.

**Layer A (conversion) — DONE for stop/yield signs and traffic lights.** Two new post-netconvert passes
in `CarlaNet.Map/OpenDrive/`, both `string→string` `.xodr` rewrites modeled on `ElevationInjector`,
called inside `CarlaClient.GenerateWorldFromOsmWithElevationAsync` after the elevation injection:

- **`SignInjector`** — reads OSM `highway=stop`/`give_way` (and `traffic_sign=stop`) nodes from the
  clipped `.osm`, projects each via `Geodesy.GeodeticToCarlaLocal(map.GeoReference, ·)`, snaps to the
  nearest road-centerline sample (`ElevationInjector.ExtractCenterlineSamples`) → `(road, s)`, and writes
  `<signal type="206">`/`"205"` at a shoulder offset (side from the tangent×offset cross product). The
  native `ATrafficLightManager::SpawnSignals` spawns real `BP_Stop01`/`BP_Yield01` actors that drive
  behavior through `UStopSignComponent`/`UYieldSignComponent` + the TM junction FIFO. Runtime-validated:
  stop-sign actors spawn, are registered (telemetry-visible), and sit on the road.

- **`TrafficLightInjector`** — re-enables netconvert traffic-light generation (`GenerateTrafficLights=True`
  in `SCTMV.make_options`, which adds `--junctions.join`; `OsmConverter.ConvertFileWithNetworkAsync` also
  emits the SUMO `.net.xml` via `--output-file`), then **fixes grouping-bug #1 in data**. Root cause
  reconfirmed empirically: netconvert emits the light `<signal>`s and one all-heads `<controller>` per
  junction but **zero `<junction><controller>` links** and no phase split, so CARLA orphans every light
  (issue #1) and would flash whole junctions green. The injector harvests each `<tlLogic>`'s phase
  program from the `.net.xml`, emits **one `<controller>` per green phase**, and adds the
  `<junction><controller>` links — so CARLA's *existing* grouping path builds one `ATrafficLightGroup`
  per junction / one `UTrafficLightController` per phase, with **no engine change**. Correspondence used:
  xodr signal `{tlLogicId}_{k}` = tlLogic linkIndex k; xodr controller id = tlLogic id = junction `name`.
  It also (a) aliases netconvert's over-long clustered signal ids — `cluster_…_#Nmore_k` exceed CARLA's
  32-char `SignId` limit and spammed the log to ~16 GB — to short `t{n}` stand-ins with all refs
  rewritten, (b) zeroes netconvert's `zOffset="5"` (poles floated ~5 m), and (c) sets `hOffset=π` to face
  oncoming traffic. Runtime-validated on SF_LaurelHeights: lights spawn, are grouped, cycle, and
  **ambient traffic stops on red**; server log clean (`NO CONTROLLERS!` = 0, no 32-char spam).

**Layer C (client API) — deferred.** The traffic-light + speed-limit RPCs remain unbound in the Python
shim (§4). Not required for ambient traffic to obey controls, so deferred.

**Validation substrate.** Behavior confirmed via: (a) stock **Town10HD** (real pre-grouped lights) for
the ALSM un-stub and the runtime clobbering fix; (b) **SF_LaurelHeights** via `SCTMV.py` for the injected
signs and lights (headless `RunCarlaServer.ps1` = `UnrealEditor.exe -game -RenderOffScreen`, so VibeUE
cannot attach — verify via a `carlanet` client script polling the world-observer cache, or the server
log at `Unreal/CarlaUnreal/Saved/Logs/CarlaUnreal.log`).

**Open issues (quality/realism, not function):**

- **Traffic-light pole placement + mesh — resolved.** netconvert emits one `<signal>` per head
  (`t = -1.7 … -8.4 m`, all `orientation="+"`, no `hOffset`) and `BP_TLOpenDrive` renders one full
  pole+arm per signal, so a junction showed several poles *across the road*. Now: the injector collapses
  each approach's heads to one pole, places it on the far side of the junction at the roadside, and
  faces it at the oncoming approach; the blueprint hangs one signal head per approach lane from its mast
  arm. Runtime-validated. See [12 — Traffic-Light Placement & Turn Signals](12_Traffic_Light_Placement_and_Turn_Signals.md)
  for the full design, the geometry, and the deferred items (backplates on added heads, the left-hand-traffic
  blueprint, turn arrows).
- **Collapsing heads must merge `<validity>` — a trap worth stating plainly.** CARLA builds one
  stop-line trigger box *per lane listed in a signal's `<validity>`*
  (`TrafficLightComponent::InitializeSign`), and netconvert scopes each head's validity to the single
  lane it hangs over. Dropping the redundant heads without merging their validity onto the survivor
  therefore leaves every other lane of the approach with **no trigger box at all**, and traffic in those
  lanes drives straight through the light — while traffic in the one surviving lane still stops, which
  makes the failure look like a timing bug rather than a coverage bug. The survivor now inherits the
  union of its approach's lanes. Note that `MapBuilder::AddSignalPositionInertial` only sets the signal's
  `_transform`: it does **not** move `s`/road, so the far-side *visual* placement does not move the
  trigger boxes.
- **Vehicles must clear a junction they have entered.** A vehicle keeps reporting *at traffic light*
  while it overlaps the stop-line trigger box (~3 m), which is far shorter than a bus or truck, so the
  flag persists after its nose is into the junction. `TrafficLightStage` therefore tracked vehicles that
  entered while permitted to proceed and exempts them from braking, so a light changing mid-manoeuvre no
  longer halts them across the intersection — worst case a permissive left, which waits inside the
  junction for a gap and would otherwise block cross traffic until its own light cycled green. The
  commitment is *stateful, not geometric*: the waypoint buffer looks ahead, so a vehicle stopped at the
  stop line already has a junction waypoint in front of it and cannot be told apart from one genuinely
  across the line — inferring entry from position instead makes vehicles run reds.
- **Facing** confirmed: the tangent-toward-junction heading renders as facing the stopped driver.
- **Signal timing** uses CARLA defaults ({10 s green, 3 s yellow, 2 s red} per controller) — phases
  alternate correctly but do not reproduce netconvert's per-phase durations from the `.net.xml`.
- **Speed-limit signs (type 274)** not yet injected (per-way `maxspeed`, value-bucket snapping,
  way→road mapping — deferred; the vehicle speed limit comes from sign trigger volumes, not the lane
  `<speed>` data that already survives).
- **Sign coverage is map-dependent.** SF_LaurelHeights' interior is *signalized* (26 signals) with stop
  signs only near the edges, so stop injection there is inherently sparse; stop injection matters on
  residential/rural grids (e.g. Gardnerville), while signalized cities need the traffic-light path above.

---

## 1. The headline

Ambient traffic in worlds built by `SCTMV.py` obeys **nothing** at intersections — no
lights, no stop signs, no signalized priority — because control data is severed at **three independent
layers**, any one of which alone would defeat intersection behavior:

| Layer | Where | What happens | Effect |
|---|---|---|---|
| **A. Conversion** | `OsmConverter.cs` / netconvert | Traffic lights deliberately discarded; stop/yield/speed-limit **signs never emitted** | Generated `.xodr` has **0 `<signal>`, 0 `<controller>`** |
| **B. Runtime (TM)** | `CarlaNet.TrafficManager/Stages/ALSM.cs` | Traffic-light state hardcoded `Green`; speed limit hardcoded `0` | TM cannot *see* a red light or a speed limit even if one existed |
| **C. Client API** | `python/carlanet/__init__.py` | No traffic-light, waypoint, junction, landmark, or speed-limit surface bound | A client/script cannot query or drive intersection state |

The OSM source files *do* contain the needed information (traffic-signal nodes, stop/give-way nodes,
`maxspeed` tags). The loss is entirely downstream. **No additional sampled data source is required for
the core protocol** — see §6 for the one genuine exception (signal phase/timing & per-approach grouping,
which must be *synthesized*, not sampled).

---

## 2. What survives the world-creation pipeline (the data layer)

### 2.1 OSM → clipped OSM

`osm_clip.clip_osm_to_bounds` ([`osm_clip.py`](../../../CarlaNet/python/osm_clip.py)) **preserves tags**:
way `<tag>` children and interior-node `<tag>` children are carried through verbatim. So
`highway=traffic_signals`, `highway=stop`, `give_way`, `junction`, and `maxspeed` survive clipping *in
principle*. The loss it does cause is incidental: a signal node is kept only if it lands on a surviving
way run, so nodes on ways cut by the bounding box or removed by the `passenger`-vclass road filter are
dropped (measured: `SF_LaurelHeights.osm` 225 `traffic_signals` nodes → 107 after clip; way-level
`maxspeed` tags 25 → 1). Synthetic boundary-cut nodes carry no tags (correct).

### 2.2 Clipped OSM → OpenDRIVE (netconvert) — the decisive cut

`OsmConverter.BuildArguments` ([`OsmConverter.cs`](../../../CarlaNet/src/CarlaNet.Map/OsmConverter.cs))
shells out to bundled netconvert (SUMO 1.27.0). For the digital-twin path,
`OsmConversionOptions.GenerateTrafficLights` is set **`False`** in
[`SCTMV.py`](../../../CarlaNet/python/SCTMV.py) `make_options` (`SCTMV.py:257`; the retired
`test_digital_twin.py` set the same), with the explicit comment that this *"avoids the ungrouped-TL log
spam (known issue #1)."* That drives netconvert to:

```
--tls.guess false
--tls.discard-loaded        # actively throws away OSM-tagged traffic signals
# (and --junctions.join is omitted)
```

The in-code comment records the measurement: *"verified on WrigleyVille: join → 19 TLs, no-join → 0."*
So traffic lights are **intentionally suppressed** to dodge the grouping bug.

**Two losses are independent of that flag, however:**

1. **Stop/yield/speed-limit signs are never produced by netconvert at all.** Even on the
   traffic-lights-ON contrast builds, the only `<signal>` types emitted are the dynamic traffic-light
   codes (`1000001`, `1000011`). Across WrigleyVille there are **zero** type-`206` (Stop), `205` (Yield),
   or `274` (MaximumSpeed) signal elements, despite the source OSM carrying `highway=stop` / `give_way`
   nodes. This is a netconvert/OSM2ODR limitation, not a flag choice.
2. **Speed limits survive only as lane data, not as signs.** OSM `maxspeed` is written into per-lane
   `<speed max="…">` records (329–2678 per map) — which the road graph can read — but there is no
   `<signal type="274">`, so no speed-limit sign spawns and nothing feeds a sign-based slowdown.

### 2.3 Empirical confirmation — what the generated `.xodr` actually contains

| Generated world | `<signal>` | `<controller>` | `<junction>` | `<speed>` |
|---|---|---|---|---|
| `Lakeview_Carson_elevated.xodr` | **0** | **0** | 35 | 329 |
| `SF_LaurelHeights_elevated.xodr` | **0** | **0** | 54 | 676 |
| `Gardnerville_…_elevated.xodr` | **0** | **0** | 25 | 217 |
| *(contrast)* `WrigleyVille.xodr` (TL-ON) | 160 | 20 | 315 | 2678 |
| *(contrast)* `tlexp.xodr` (TL-ON) | 78 | 5 | 337 | 2771 |

**What survives:** drivable road geometry, junction connectivity (`<junction>` + lane links), and
per-lane speed values. **What is lost:** all traffic lights, all stop/yield/priority signs.

---

## 3. What the runtime does with it (the Traffic-Manager layer)

This fork's Traffic Manager is a full in-process .NET port (`CarlaNet.TrafficManager`), not a thin client
to an in-engine TM. Its pipeline mirrors upstream (ALSM → Localization → Collision → Traffic-Light →
Motion-Plan → Vehicle-Light; see `adv_traffic_manager.md`). The relevant facts:

- **Traffic-light observation is stubbed.** `ALSM.cs` builds every per-frame kinematic snapshot with
  `TlState = TLS.Green, AtTrafficLight = false` (≈`:428–429`, `:476`) and `speedLimit = 0f` (`:404–406`,
  comment *"For now leave 0"*). The `TrafficLightStage` header claims it would read
  `actor.GetTrafficLightState()` into `SimulationState.GetTLS()`, but ALSM never calls it. So
  `set_percentage_running_light` and any speed-limit-relative target are dead in practice — the TM never
  observes a non-green light or a non-zero limit.
- **The one functioning intersection control is the geometric junction FIFO.** `TrafficLightStage`'s
  `HandleNonSignalisedJunction` makes a vehicle entering a junction stop, records arrival order, and lets
  one vehicle through at a time after a `MINIMUM_STOP_TIME` (≈2 s). This is keyed off the waypoint's
  `GetJunctionId()` — pure geometry — so it runs even with zero signals. It is an **all-way-stop
  approximation applied to every junction**, not real right-of-way.
- **Stop vs. yield is not distinguished**, and both depend on OpenDRIVE landmarks (type 206/205) that the
  conversion never produced. The speed-limit-sign slowdown (`MotionPlanStage.GetLandmarkTargetVelocity`,
  type 274) is likewise dead for the same reason.
- **Vehicle lights** (turn indicators at junction `RoadOption` Left/Right, brake lights when
  `brake > 0.5`) work but are opt-in (`update_vehicle_lights`); no hazard/4-way logic exists.
- **Ticking-mode caveat (SCTMV).** The .NET TM produces no motion under *synchronous* world ticking — its
  ALSM reads a free-running world-observer cache whose clock is not advanced in lockstep with
  `world.tick()` (the synchronous tick-timestamp is an unfinished TODO). SCTMV therefore runs a
  **synchronous-world + asynchronous-TM hybrid**, so the geometric junction FIFO above does still run; but
  any intersection-control work must account for the TM half being asynchronous even when the world is not.

For reference, the **upstream native C++ TM** (`LibCarla/source/carla/trafficmanager/`) is more capable —
it genuinely reads `vehicle->GetTrafficLightState()`/`IsAtTrafficLight()` via ALSM and honors type-274
speed-limit landmarks — but the digital-twin pipeline runs the .NET port, where those reads are stubbed.
Even upstream, junction priority is FIFO-only and *"does not follow traffic regulations"*
(`adv_traffic_manager.md`), and stop≠yield is not modeled.

---

## 4. What the client can reach (the API layer)

The Python shim ([`carlanet/__init__.py`](../../../CarlaNet/python/carlanet/__init__.py)) is the
binding bottleneck. Verified by grep:

- **Exposed and usable:** the in-process TM knobs — `set_percentage_running_light` /
  `set_percentage_running_sign`, speed/distance/lane-change/ignore-percentage settings,
  `set_path` (custom route), `update_vehicle_lights`; vehicle control (`apply_control`,
  `apply_ackermann_control`, `set_autopilot`, `get/set_light_state`); collision & obstacle sensors;
  the fork's custom world-gen / Cesium / drape / telemetry RPCs.
- **Present in C# but NOT bound in Python:** the entire traffic-light control RPC surface exists in
  `CarlaClient.cs` (`set_traffic_light_state`, `freeze_traffic_light`, `reset_traffic_light_group`,
  `get_group_traffic_lights`, `get_light_boxes`, green/yellow/red-time setters) and
  `get_vehicle_speed_limit` — none are reachable from a script today.
- **Absent entirely:** any waypoint / map-navigation API (`Map.get_waypoint`, `get_topology`,
  junction/landmark enumeration, `Waypoint`/`Junction`/`Landmark` types). `get_spawn_points()` returns
  bare transforms with no lane/road/junction metadata. There is **no way to query "what controls this
  vehicle's next junction"** from the client.

`TrafficLight` and `TrafficSign` exist only as empty `isinstance` marker subclasses — no state or timing
methods.

---

## 5. What works today (the baseline to build on)

1. **Geometric junction stopping** runs already: ambient vehicles do a one-at-a-time FIFO stop at every
   junction. It is unrealistic (treats every junction as an all-way stop, no priority) but it is *not
   nothing* — it is a working hook in `TrafficLightStage` that consumes junction geometry the conversion
   *does* preserve.
2. **The road graph is rich internally.** `CarlaNet.Map` already parses junctions, lanes, and has
   `Signal`/`SignalType`/`Controller`/`SignalGroup` classes — they are simply unused because the `.xodr`
   carries no signals and the graph isn't surfaced over RPC.
3. **The traffic-light control RPCs already exist in C#** — setting/freezing/resetting light state is
   implemented server-side; only the Python binding and the actual spawned light actors are missing.
4. **The OSM source is intact on disk** at world-creation time, with signal/stop/maxspeed tags — it can
   be parsed directly by the same script that builds the world.

---

## 6. Is OSM a sufficient source, or is more sampling needed?

**Sufficient, for the core protocol:**

| Element | OSM source tag | Sufficient? |
|---|---|---|
| Traffic-light **location** | node `highway=traffic_signals` | Yes — present in source OSM |
| Stop sign | node `highway=stop` | Yes |
| Yield / give-way | node `highway=give_way` | Yes |
| Speed limit | way `maxspeed` (+ already in `<speed>`) | Yes |
| Junction topology | already in `.xodr` `<junction>` | Yes |

**NOT available from OSM — must be synthesized or assumed, not sampled:**

- **Signal phase plan & timing** (cycle length, green split, offsets). OSM almost never carries this.
  netconvert's `--tls.guess` *invents* a plausible plan; a custom controller would do the same. This is a
  modeling choice, not a missing data source — sampling a third party would not reliably provide it
  either.
- **Per-approach signal grouping** (which lanes/approaches a given light governs, and which phases
  conflict). Must be **inferred from junction geometry** (incoming roads, turn directions). This is
  exactly what the deferred grouping bug
  ([sbrett9/carla#1](https://github.com/sbrett9/carla/issues/1)) is about.
- **Right-of-way / priority at unsignalized junctions.** OSM `priority`/`give_way` coverage is sparse;
  major-vs-minor road priority is better derived from road class (`highway=primary` vs `residential`)
  than sampled.

So: the world-creation process already has, or can trivially re-read, everything needed for *where* to
stop. The intelligence to add is *how/when* (phase synthesis + geometric grouping + priority inference) —
generated, not fetched.

---

## 7. Implementation paths

Three routes, from most native to most client-side. They are not mutually exclusive; a likely plan is the
phased combination in §8.

### 7.1 Repair the native signal chain end-to-end

Re-emit signals into the world the way upstream intends, and make the .NET TM honor them.

1. **Inject signs the conversion drops.** Add a post-netconvert pass in `CarlaNet.Map` that reads the OSM
   `highway=stop` / `give_way` / `traffic_signals` nodes and the `maxspeed` ways, snaps each to the
   nearest road `s`/lane, and writes OpenDRIVE `<signal>` elements (type 206/205/274 and the traffic-light
   codes). The `Signal`/`SignalType`/`Controller` classes already exist to model these.
2. **Fix the grouping bug** ([#1](https://github.com/sbrett9/carla/issues/1)) so guessed/injected lights
   group one `ATrafficLightGroup` per junction instead of one orphan group per signal (the fragile
   no-controller `else` branch in `TrafficLightManager.cpp`). Only then is it safe to re-enable
   `GenerateTrafficLights` with `--junctions.join`.
3. **Un-stub `ALSM.cs`** so `TlState`/`AtTrafficLight`/`speedLimit` come from real data (the server-side
   vehicle's traffic-light affiliation + the road-graph speed limit), feeding `SimulationState`.
4. **Bind the existing C# traffic-light RPCs** (`set_traffic_light_state`, `freeze_*`, `get_group_*`,
   `get_vehicle_speed_limit`) into the Python shim.

*Cost:* highest (touches conversion, the UE plugin's grouping code, the TM port, and the shim) but yields
the most faithful, upstream-aligned result and unblocks #1.

### 7.2 Client-/RPC-side semantic control layer over OSM

Bypass the broken native chain. Keep the world geometry as-is (no spawned UE light actors) and build a
parallel **control map** at world-creation time:

1. Parse the OSM signal/stop/give-way/maxspeed nodes in the world-gen step (same script, same file),
   snap them to the road graph / junctions, and persist a "signal influence" model in `CarlaNet.Map`.
2. Add a virtual **signal-group scheduler** in `CarlaNet.TrafficManager` keyed to that model: synthesize a
   phase plan per junction (geometric grouping of conflicting approaches) and advance it each tick.
3. Feed that synthesized state into `SimulationState.GetTLS()` (the seam ALSM currently stubs) so the
   existing `TrafficLightStage` reacts to it — and route stop/give-way nodes into the existing junction
   FIFO with stop≠yield semantics.
4. Optionally expose the control map and per-junction phase over a new RPC so a client (or the CoT
   telemetry contract, [09](09_Telemetry_CoT_Contract.md)) can read/drive signal state.

*Cost:* medium; concentrated in `CarlaNet` and the shim, avoids the UE plugin and the spawn/grouping path
entirely. Best fit for a headless / EO-sim digital twin where you don't need visible light meshes.

### 7.3 Minimum viable: realistic stops without rebuilding signals

Smallest step that meaningfully improves behavior:

1. Improve the geometric junction FIFO so it isn't a blanket all-way stop — derive priority from road
   class so vehicles on the major road don't stop for empty minor approaches.
2. Inject **stop/give-way landmarks only** (cheaper than full traffic-light grouping) so
   `MotionPlanStage` applies the stop/yield target-velocity slowdown and stop≠yield is honored.
3. Defer traffic lights to 7.1/7.2.

*Cost:* lowest; uses data already preserved (junctions) plus a thin sign-injection. No phase synthesis, no
grouping-bug dependency.

---

## 8. Recommendation

A phased combination, ordered by value-per-effort:

1. **Un-stub `ALSM.cs`** (§7.1 step 3) — until the TM can *see* a light/limit, nothing else matters.
   Validate against a hand-authored `.xodr` that already has signals (the contrast files in §2.3). Note:
   the per-vehicle `traffic_light_state`/`speed_limit` is *already serialized* into the world-observer
   snapshot (`FWorldObserver`'s `TypeDependentState`) — ALSM just hardcodes green/0 instead of reading it.
   The time-of-day work ([#5](https://github.com/sbrett9/carla/issues/5)) is a working precedent for
   surfacing snapshot fields into the .NET side lock-free (there, an extended EpisodeState header).
2. **Sign injection from OSM** (§7.1 step 1 / §7.3 step 2) — stop/yield/speed-limit signs, since the OSM
   data is sufficient and netconvert never emits them. Gives real stop-sign and speed-limit behavior
   without the grouping bug.
3. **Bind the traffic-light + speed-limit RPCs** into the shim (§7.1 step 4) so scripts like
   `test_generate_fade_traffic.py` and the telemetry/observer clients can query and drive intersection
   state.
4. **Traffic lights** last, via either fixing the grouping bug (§7.1 step 2) or the virtual scheduler
   (§7.2) — the virtual scheduler is preferred for a headless EO digital twin because it needs neither
   visible light meshes nor the fragile UE grouping path, and it keeps signal logic in the same .NET
   layer that already owns the road graph and the TM.

**Net:** the information needed to make ambient traffic stop at intersections and obey limits is already
present in the OSM the pipeline ingests; realizing it is a matter of (a) not discarding it during
conversion, (b) letting the .NET TM observe it instead of hardcoding green/zero, and (c) exposing it to
the client. Only signal *phase/timing* and *approach grouping* must be synthesized rather than sampled.

---

## 9. Key source references

- World-gen & conversion: [`SCTMV.py`](../../../CarlaNet/python/SCTMV.py) (`make_options:252`,
  `build_world:273`; supersedes the retired `test_digital_twin.py`),
  [`OsmConverter.cs`](../../../CarlaNet/src/CarlaNet.Map/OsmConverter.cs)
  (`BuildArguments`), [`osm_clip.py`](../../../CarlaNet/python/osm_clip.py).
- Traffic demo (commands no intersection behavior):
  [`test_generate_fade_traffic.py`](../../../CarlaNet/python/test_generate_fade_traffic.py).
- TM runtime (.NET port): `CarlaNet/src/CarlaNet.TrafficManager/Stages/ALSM.cs` (the stubs),
  `…/Stages/TrafficLightStage.cs`, `…/Stages/MotionPlanStage.cs`; doc `Docs/adv_traffic_manager.md`.
- Native signal model & spawning (reference): `LibCarla/source/carla/road/SignalType.{h,cpp}`,
  `…/road/MapBuilder.cpp`, `Unreal/CarlaUnreal/Plugins/Carla/Source/Carla/Traffic/TrafficLightManager.{h,cpp}`.
- Client surface: `CarlaNet/src/CarlaNet.Transport/CarlaClient.cs` (TL RPCs ~`:905–932`,
  `get_vehicle_speed_limit` `:883`), [`carlanet/__init__.py`](../../../CarlaNet/python/carlanet/__init__.py).
- Grouping bug: [sbrett9/carla#1](https://github.com/sbrett9/carla/issues/1).
