# 18 — Scenario Fabrication for Pattern-of-Life Model Training

**Status:** Research / design note. No code changed. The recorder and replayer findings were validated
against a running server on 2026-07-29 (§5.4).
**Date:** 2026-07-28, amended 2026-07-29
**Scope:** Whether `carla-simulator/scenario_runner` (and the ASAM OpenSCENARIO storyboard model it
parses) is the right foundation for a scenario-fabrication suite whose purpose is to train an
**estimated pattern-of-life (EPoL)** model for vehicles; the code changes such a suite would need in
this fork; an audit of whether the CARLA recorder/replayer survived the CarlaNet translation; the
additions the Cursor-on-Target telemetry needs; and a survey of existing scenario-authoring tools.
**Supersedes/extends:** [11 — ScenarioRunner as a Validation & Scripting Layer](11_ScenarioRunner_Validation_Layer.md)
(which framed ScenarioRunner as a driving-stack validation harness; this doc re-frames it for
synthetic-imagery and behavior-pattern generation).
**Related:** [09 — Telemetry CoT Contract](09_Telemetry_CoT_Contract.md) ·
[10 — Intersection Navigation & Traffic Control](10_Intersection_Navigation_Traffic_Control.md) ·
[16 — Sensor Pose In Recordings](16_Sensor_Pose_In_Recordings.md) ·
[17 — Photoreal Occlusion Metric](17_Photoreal_Occlusion_Metric.md)

---

## 1. The problem this is solving

The training pipeline is:

```
fabricated scenario  →  simulated world  →  EO imagery (PNG)  →  detector/tracker (YOLO family)
                                         →  CoT truth (engine state)         ↓
                                                                          tracks  →  EPoL model
```

The EPoL model learns behavior patterns from **tracks**, not from truth — because a fielded system has
cameras, not omniscience. Truth exists in this pipeline to check the detector and to label the training
set, never to feed the EPoL directly.

That imposes requirements a driving-simulation harness does not normally have:

1. **Reproducible behavior for every actor**, not just one subject vehicle. Deviations and permutations
   are only meaningful against a fixed baseline.
2. **Long time horizons.** Patterns of interest include dwell, loiter, revisit cadence, and time-of-day
   habit — minutes to hours, not a 30-second driving episode.
3. **No ego.** The observer is an airborne or fixed camera; there is no protagonist vehicle.
4. **Arbitrary geography.** Any OSM extract, converted to OpenDRIVE at build time.
5. **Permutation at volume.** One authored pattern must yield many labeled variants.

## 2. The OpenSCENARIO storyboard model

### 2.1 File formats and extensions

| Format | Extension | What it is |
|---|---|---|
| ASAM OpenSCENARIO 1.x | `.xosc` | XML storyboard — the interoperable, tool-agnostic scenario format |
| ASAM OpenDRIVE | `.xodr` | The road network a scenario is authored against (already produced by this fork's OSM pipeline) |
| ASAM OpenSCENARIO 2.0 | `.osc` | A textual domain-specific language; ScenarioRunner parses it in `srunner/tools/osc2_scenario.py` |
| ScenarioRunner native scenario config | `.xml` | ScenarioRunner's own non-standard scenario descriptor |
| ScenarioRunner route | `.xml` | A long route with scenarios attached; the Autonomous Driving Leaderboard format |

The storyboard model referred to throughout this document is **OpenSCENARIO 1.x / `.xosc`**.

### 2.2 It controls many actors, not one

This was the decisive question and the answer is unambiguous. `.xosc` has no privileged entity; "ego" is
a naming convention that downstream tooling (ScenarioRunner's criteria and manual-control layers) uses to
decide what to score. The structure:

```xml
<Entities>
  <ScenarioObject name="car_01"> <Vehicle .../> <ObjectController .../> </ScenarioObject>
  <ScenarioObject name="truck_02"> ... </ScenarioObject>          <!-- N of these -->
</Entities>
<Storyboard>
  <Init>
    <Private entityRef="car_01">                                   <!-- deterministic spawn table -->
      <TeleportAction><Position><LanePosition roadId=".." laneId=".." s=".."/></Position></TeleportAction>
      <LongitudinalAction><SpeedAction><AbsoluteTargetSpeed value="29.1"/></SpeedAction></LongitudinalAction>
    </Private>
  </Init>
  <Story><Act><ManeuverGroup>
    <Actors><EntityRef entityRef="car_01"/></Actors>               <!-- one OR many per group -->
    <Maneuver><Event><StartTrigger>...</StartTrigger>
      <LaneChangeAction/> | <SpeedAction/> | <FollowTrajectoryAction/> | <AcquirePositionAction/>
    </Event></Maneuver>
  </ManeuverGroup></Act></Story>
</Storyboard>
```

A nominal-highway description — mediocre density, named vehicles at named entry points, specific
velocities, scripted passes and lane changes, fully deterministic — is the canonical use of the format.

### 2.3 The trigger vocabulary already matches pattern-of-life semantics

Conditions verified as supported in ScenarioRunner's parser (`srunner/tools/openscenario_parser.py`):

`SimulationTimeCondition` · `TimeOfDayCondition` · `StandStillCondition` (dwell) ·
`ReachPositionCondition` · `TraveledDistanceCondition` · `DistanceCondition` ·
`RelativeDistanceCondition` (standoff from a site) · `StoryboardElementStateCondition` (act sequencing) ·
`SpeedCondition` · `RelativeSpeedCondition` · `AccelerationCondition` · `TimeHeadwayCondition` ·
`TimeToCollisionCondition` · `CollisionCondition` · `OffroadCondition` · `EndOfRoadCondition` ·
`TrafficSignalCondition` · `TrafficSignalControllerCondition` · `ParameterCondition` ·
`UserDefinedValueCondition`

Loiter, standoff, revisit, time-of-day and act chaining are all expressible without extension.

**Position types supported:** `WorldPosition`, `LanePosition`, `RoadPosition`, `RoutePosition`,
`RelativeWorldPosition`, `RelativeLanePosition`, `RelativeRoadPosition`, `RelativeObjectPosition`.
**`GeoPosition` is not implemented.** This matters here: patterns authored against `roadId`/`laneId`
are welded to one generated `.xodr` and break when the OSM pipeline renumbers roads. Authoring in
latitude/longitude and resolving to lanes at load time is the portable approach, and OpenSCENARIO 1.1
defines `GeoPosition`, so implementing it conforms to the standard rather than diverging from it.

### 2.4 Where determinism actually lives

Not in the format — in the **controller**. An entity whose `ObjectController` delegates to autopilot or
the Traffic Manager reintroduces exactly the stochasticity the storyboard was meant to remove.
Deterministic execution requires prescribed motion (`FollowTrajectoryAction`, or explicit speed and lane
actions), and nominal patterns should avoid vehicle-to-vehicle contact, which is where any physics
engine diverges between runs.

## 3. Verdict on ScenarioRunner: adopt the grammar, not the runtime

### 3.1 What is worth taking

- The **storyboard model** as the pattern description language. A pattern ("arrives 08:15, parks 400 m
  from the school, dwells 45 min, departs, returns Thursday") is structurally a storyboard: entities,
  init, acts, triggers, stop conditions. The alternative is inventing this grammar.
- The **trigger vocabulary** in §2.3.
- **`ParameterDeclarations`** plus `--openscenarioparams` overrides: one parameterized pattern becomes a
  sweep over dwell time, standoff distance, approach bearing, vehicle class, start time. This is the
  permutation engine, and it is declarative, so a trainer drives it rather than a programmer.
- The **atomic behavior/trigger/criteria catalogue** as a reference for what a complete primitive set
  looks like, even where reimplemented.
- MIT licence, so vendoring the parser is clean.

### 3.2 What is dead weight for this application

- **`ScenarioManager`** — an episode runner with a timeout and a pass/fail verdict. Pattern-of-life needs
  hours of simulated time with many independent patterns starting and ending asynchronously, and no
  verdict.
- **Criteria, `result_writer`, metrics, `autoagents`, Leaderboard integration** — scoring here is
  detector output versus CoT truth, computed downstream.
- **Route mode and `background_activity.py`** — ego-bubble machinery. There is no ego, and traffic is
  wanted across the whole camera footprint, which the existing staging-ring work already provides.

### 3.3 The decisive technical reason not to adopt the runtime

ScenarioRunner's actor-control layer exists to solve a problem CarlaNet has already solved natively:

| Capability | ScenarioRunner | This fork |
|---|---|---|
| Drive an actor along a prescribed route | `WaypointFollower` atomic, ticked in Python each frame | `SetCustomPath(actor, IReadOnlyList<Location>, emptyBuffer)` — `CarlaNet.TrafficManager/Parameters.cs:249`, `TrafficManager.cs:204` |
| Prescribe route + turn decisions | `LocalPlanner` + `RoadOption` | `SetImportedRoute(actor, RoadOption bytes, emptyBuffer)` — `Parameters.cs:279`, with `UpdateImportedRoute`/`RemoveImportedRoute` for live re-tasking |
| Consumed by | Python behavior tree | `CarlaNet.TrafficManager/Stages/LocalizationStage.cs:308` |
| Exposed to Python | n/a | `tm.set_path()` — `CarlaNet/python/carlanet/__init__.py:1171` |

Adopting ScenarioRunner would also require the client-side waypoint API it depends on pervasively — 42
`get_waypoint()` calls in `carla_data_provider.py`, `atomic_behaviors.py`, `atomic_criteria.py` and
`scenario_manager.py` alone, plus `agents.navigation.{global_route_planner,basic_agent,local_planner}`.
That API does not exist in the shim (§4.3). Executing storyboards on CarlaNet's own primitives avoids
that dependency entirely.

**Conclusion:** use OpenSCENARIO as the authoring/interchange format; implement the executor on
CarlaNet; harvest ScenarioRunner's parser as a reference implementation when standards interop is wanted.

## 4. Code changes this fork needs

### 4.1 Scenario execution belongs beside the Traffic Manager, not in Python

Design decision recorded: **no Python in the live control path.** The scenario is delivered as data and
interpreted natively.

One clarification this implies. The Traffic Manager is a *micro-behavior* engine — car following, gap
acceptance, lane changes, collision avoidance, traffic-light response. It has no concept of storyboards,
triggers, dwell states or acts, upstream or here. "Deliver the scenario to the Traffic Manager" therefore
means a **new scenario-executor service in C# that sits beside the Traffic Manager**, holds the compiled
pattern spec, evaluates triggers per tick, and issues `SetCustomPath` / `SetDesiredSpeed` /
lane-change / spawn / despawn commands — while the Traffic Manager continues to do what it is for. The
two are layered, not merged.

This is also the correct choice for determinism: a Python control loop puts RPC latency and interpreter
scheduling inside the control path.

### 4.2 The pattern spec (intermediate representation)

A serializable description of what actors do over time, decoupled from both the authoring format and the
execution mechanism:

```
authoring  →  PATTERN SPEC  →  executor
(editor, generator,            (scenario executor beside the TM)
 .xosc import)
```

The middle stage earns its place three ways: the authoring surface will change, the executor will
change, and the permutation sweep operates on the spec rather than on XML. Approximate shape:

```yaml
map:      { osm: Lakeview_Carson.osm, origin: {lat, lon} }   # scenario is bound to its map (§7, D1)
run:      { duration_s, fixed_delta, seed, spec_version }
entities:
  - id: subj_01                        # stable across runs; not the CARLA actor id
    blueprint: vehicle.audi.a2
    spawn:  { geo: [lat, lon], heading, speed_mps, t_start }
    itinerary:
      - { goto: [lat, lon], speed_mps: 13.4 }
      - { dwell: 2700, park: true }                          # the loiter
      - { goto: [lat, lon], speed_mps: 15.6 }
    labels: { pattern: loiter_near_site, role: subject }     # EPoL ground truth (§6.2)
    params: { dwell_s: 2700, standoff_m: 400 }               # swept
```

### 4.3 Work ledger

| Tier | Work | Where |
|---|---|---|
| 1 | Pattern-spec schema + loader; seeded generator; permutation sweep | Tooling (language open) |
| 1 | Scenario-executor service: trigger evaluation, entity state machine (transit → dwell → depart), commands to TM | C# — new project beside `CarlaNet.TrafficManager` |
| 1 | Geographic-to-lane resolution (lat/lon → world → nearest drivable lane) | C# — `CarlaNet.TrafficManager/InMemoryMap.cs:69` `GetWaypoint(Location)` already does this and is already loaded; needs exposure |
| 2 | **Synchronous-tick fix** (scoped in §7) | C# — `Stages/ALSM.cs` |
| 2 | Tick/simulation-time stamping into telemetry and recordings | C# — `CarlaNet.Recording`, `CarlaNet.Transport` |
| 3 | Client-side waypoint API — `GetWaypoint`, `Next`/`Previous`, `GetLeft/RightLane`, `GetTopology`, lane types, junctions, landmarks, crosswalks | C# — `CarlaNet.Map/Road/Map.cs` has the road graph and `ComputeTransform` but exposes none of these; Python `Map` is name + spawn points only (`carlanet/__init__.py:657`) |
| 3 | OpenSCENARIO 1.x front-end compiling to the pattern spec, including `GeoPosition` | Tooling |
| — | Engine (C++) | **No changes required** for any tier |

## 5. Recorder / replayer audit

Motivation: if server-side record-and-replay works, "same behavior, many appearances" (time of day,
weather, camera track) is available without re-running the scenario. §5.1–§5.3 are a source audit;
§5.4 records a live test against a running server.

### 5.1 Intact

| Layer | Finding |
|---|---|
| Engine | `Recorder/CarlaRecorder.*`, `CarlaReplayer.*`, `CarlaReplayerHelper.*` all present |
| Engine tick | Recorder ticked every frame — `Game/CarlaEngine.cpp:361-364` (`EpisodeRecorder->Ticking(DeltaSeconds)`) |
| RPC surface | All nine bound — `Server/CarlaServer.cpp:2824-2908` (`start_recorder`, `stop_recorder`, `show_recorder_file_info`, `show_recorder_collisions`, `show_recorder_actors_blocked`, `replay_file`, `set_replayer_time_factor`, `set_replayer_ignore_hero`, `set_replayer_ignore_spectator`, `stop_replayer`) |
| CarlaNet client | All implemented with matching RPC names — `CarlaNet.Transport/CarlaClient.cs:1071-1100` |

**The libcarla-to-CarlaNet translation did not drop the recorder.**

### 5.2 Two regressions

1. **Python shim exposes six of nine.** Missing: `show_recorder_collisions`,
   `show_recorder_actors_blocked`, `set_replayer_ignore_spectator`. Present in C#, unexposed in
   `carlanet/__init__.py:2218-2239`. Three wrapper methods. This is a capability present upstream and
   absent here — worth closing on principle.
2. **Vehicle wheel animation disabled in record and replay** — `#if 0 // @CARLAUE5` at
   `Recorder/CarlaRecorder.cpp:209` (`AddVehicleWheelsAnimation`) and
   `Recorder/CarlaReplayerHelper.cpp:348` (`SetWheelRotYaw`/`SetWheelPitchAngle`). The `@CARLAUE5`
   marker identifies this as inherited from the upstream Unreal Engine 5 port, not introduced by
   CarlaNet or by this fork. Effect: replayed vehicles translate correctly with static, unsteered
   wheels. Sub-pixel at EO collection altitudes; relevant only for ground-level replay.

### 5.3 The map-name guard is inert for generated worlds

`Recorder/CarlaReplayer.cpp:136-144` reads as a safety check: if the live episode's map name differs
from the recorded `Mapfile`, the replayer calls `LoadNewEpisode(RecInfo.Mapfile)` — which, in a
runtime-generated world, would drop the Cesium tilesets, draped terrain and ground layer that the build
spawns. It provides no safety at all, for two compounding reasons.

**The comparison is between a value and itself.** The recorder stores `UCarlaEpisode::MapName` verbatim
(`Game/CarlaEpisode.cpp:408` passes it to `ACarlaRecorder::Start`; `Recorder/CarlaRecorderQuery.cpp:121`
prints it unchanged), and the replayer tests it against `Episode->GetMapName()`, which returns that same
member (`Game/CarlaEpisode.h:97`). The two cannot differ for a given loaded level, so the reload branch
is unreachable and the overlay is never at risk.

**Every generated world shares one level name.** `LoadNewOpendriveEpisode` stages a fixed path
(`Game/CarlaEpisode.cpp:159,169`) whatever geography it was built from, so all OSM-derived worlds load
as `OpenDriveMap`. Even if the comparison drew its two sides from independent sources, it would still
pass unconditionally here. A log recorded over one city will replay into a world built from a different
city without complaint, placing vehicles on roads that do not exist there.

A practical note for anyone implementing an external check: the level name is not the string a client
sees. The `get_map_info` RPC returns the content-relative directory joined to the level name
(`Server/CarlaServer.cpp:869-880`), so `world.get_map().name` yields `Carla/Maps/OpenDriveMap` where the
recorder holds `OpenDriveMap`. Only the trailing segment is comparable, and comparing the full strings
reports a mismatch that does not exist.

There is no engine-side protection and none is expected upstream, where map names identify hand-authored
towns. Binding a recording to the world it was made in is therefore the calling tooling's
responsibility — see §5.5.

### 5.4 Live test result (2026-07-29)

Procedure: world built and driven by SCTMV (synchronous world clock, 0.05 s step); a second client
attached without touching world settings or ticking; traffic enabled; `start_recorder` for 60 s of wall
clock; `stop_recorder`; traffic disabled; `show_recorder_file_info`; `replay_file`; `stop_replayer`.

| Check | Result |
|---|---|
| Recorder produces a log | Yes — header, actor creation events and per-frame data all present |
| Actors captured | Vehicles, spectator, RGB camera, and injected `traffic.stop` signs |
| Replay spawns and drives vehicles | **Yes** — motion matched the live run |
| Level reload during replay | None, as §5.3 predicts |
| Spectator hijacked by playback | No — the server reports "Ignoring Spectator camera" by default |

**Record and replay work end to end in this fork.** The appearance-permutation axis — one recorded
behavior replayed under many times of day, weather states and camera tracks — is viable.

Incidental measurement of value to §6.1: 1014 ticks elapsed during 60 s of wall clock (≈16.9 Hz against
a 20 Hz target), and 1014 × 0.05 s = 50.7 s, matching the server's reported recording length of 50.6 s.
The world clock ran at roughly 84% of real time. Tick count and recorded duration agree with each other;
only wall clock disagrees with both, which is direct evidence for the tick-based time base of D3.

Still outstanding: whether replayed motion tracks the original closely enough to substitute for
re-execution. That requires the two-run diff harness of §6.1 and is not answerable by observation.

### 5.5 Binding a recording to the world it was made in

Since the engine's guard is inert (§5.3), fidelity has to be guaranteed by the tooling. The binding must
identify **the world**, not its inputs: the same OSM extract built with a different height-align mode,
sample step, clip boundary, ground elevation asset or road filter produces different road geometry and
different elevations, and a recorded vehicle would sit underground or float. Hashing the source OSM
alone would pass in every one of those cases.

The artifact that *is* the world is the generated `.xodr`; every upstream choice is baked into it. Two
distinct questions, two identifiers:

| Question | Identifier |
|---|---|
| Is the loaded world the exact one this log was recorded in? | SHA-256 of the generated `.xodr`, which the server already holds at `Content/Carla/Maps/OpenDrive/OpenDriveMap.xodr` and already serves to clients through `GetXODR` — no new RPC needed |
| Can an equivalent world be rebuilt from scratch? | The build recipe: source OSM digest, clip bounds, netconvert arguments, origin latitude/longitude, sample step, height-align mode and its parameters, ground and imagery asset ids, origin height |

The recipe must record **resolved** values, captured at build time, not the arguments the operator
typed. Several parameters are derived when omitted: the origin defaults to the centre of the OSM
`<bounds>`, the vertical datum defaults to a sampled height at that origin, and clipping produces a
derived OSM distinct from the file named on the command line. A recipe reconstructed from a command
line would therefore record blanks where the values that shaped the world actually lived, and would
silently drift as defaults change between releases. The digests of the converter and of the build
tooling belong in the recipe for the same reason — identical arguments to a different `netconvert`
build need not yield an identical network.

Recommended mechanics:

1. **At build time**, write the recipe into the generated `.xodr` under `<header><userData>`. OpenDRIVE
   sanctions the extension, the world then carries its own provenance, and hashing the file covers the
   recipe automatically. No recorder file-format change, so `show_recorder_file_info` and any upstream
   tooling keep working.
2. **At record time**, fetch the live `.xodr`, hash it, and write a manifest beside the `.log` holding
   that digest plus the parsed recipe. Refuse to treat a log without a manifest as replayable.
3. **At replay time**, fetch the live `.xodr` again, hash, and compare.

Because elevation sampling draws on streamed Cesium tiles, a rebuild from identical inputs is not
guaranteed to be byte-identical, so the gate should be two-tier rather than pass/fail: an exact digest
match proceeds silently; a recipe match with a digest mismatch warns and requires an explicit override;
a recipe mismatch is refused.

This also satisfies the stronger form of D1 — the scenario, its map and its recordings travel together,
and the tie is content-addressed rather than name-based, so it cannot be defeated by a coincidental or
forged level name.

## 6. Cursor-on-Target telemetry additions

Current emitters: `CarlaNet.Recording/CotWriter.cs` (native path, authoritative) and `to_cot()` in
`CarlaNet/python/SCTMV.py` (live UDP path). Both already emit per-vehicle position, course, speed,
dimensions, type, colour, role, velocity components, and a `<_solar>` block; the native path
additionally emits the collection platform as an air track with full camera intrinsics
(`fx`, `fy`, `cx`, `cy`, `width`, `height`, `hfov_deg`, `vfov_deg`, projection model, distortion) and
sensor pose (`azimuth`, `elevation`, `roll`) — `CotWriter.cs:86-108`.

Image-space bounding boxes are deliberately **not** added: a separate post-process step derives labels
from the imagery plus this truth, and that separation is retained. The additions below exist to give that
post-process, and the EPoL trainer, everything they cannot re-derive.

### 6.1 Association and reproducibility

- **`tick`** — frame counter from a defined epoch (scenario start, not server start), emitted as a root
  attribute of the CoT event, and written into the PNG `tEXt` metadata alongside the existing solar
  fields so an image and its truth are joinable without filename matching.
- **`sim_time_s`** — `tick × fixed_delta`. Emitted alongside `tick` rather than derived, because
  `fixed_delta` is a run parameter and a future run may change it.
- Current timestamps are **wall-clock UTC** in both emitters (`CotWriter.cs:170`; `datetime.now()` in
  `SCTMV.to_cot`). Two runs of the same scenario therefore carry unrelated times and cannot be diffed
  without re-alignment — and wall clock does not track the world in any case: the measurement in §5.4
  recorded 50.6 s of simulation during 60 s of wall clock. This is the single highest-value addition
  and a precondition for the reproducibility harness.
- **`run_id`, `scenario_id`, `seed`, `spec_version`** — grouping and reproduction.

### 6.2 The training label

- **Stable entity identity.** `uid` is `CARLA-TRUTH-{actor_id}` (`CotWriter.cs:119`) and CARLA actor ids
  are assigned at spawn, so they differ between runs. Carry the pattern-spec entity id so tracks are
  comparable run-to-run.
- **Pattern annotation** — which pattern the entity is executing, its current phase (transit / dwell /
  depart), whether it is subject or background, and the swept parameter values. Without this the corpus
  has tracks but no labels for the behavior being trained, and the trainer would re-derive ground truth
  it already possessed.
- **Motion state** — explicit stationary/parked flag and time-stationary. A loiter is *defined* by
  dwell; it is known exactly at the source and should not be re-inferred from speed downstream.

### 6.3 Orientation

Emit vehicle **yaw, pitch, roll** independently of `<track course>`.

`course` in CoT is course over ground: the direction of travel, derived from the velocity vector. A
stationary vehicle has no course — the velocity vector has zero length, so there is no direction to
report (distinct from *heading*, which is where the vehicle is pointed and remains perfectly well
defined at rest). Since the headline pattern class involves parked and loitering vehicles, the case
where `course` is undefined is exactly the case of interest, and an oriented bounding box needs true
yaw. Vehicle attitude is available in engine state and is currently discarded.

### 6.4 Note for detector-derived tracks

`ce`/`le` are hardcoded `"0.0"` for truth, which is correct. When the post-process emits
detector-derived tracks with `source="detection"`, those fields carry real error estimates — the Python
path already parameterizes this; the native path does not.

## 7. Decisions recorded

| # | Decision |
|---|---|
| D1 | A scenario is **bound to its map**. The `.osm` extract is a dependency of the scenario; roads define where waypoints can exist, and even off-road behavior is tied to terrain. Scenario and map are never separated. Enforcement is content-addressed and belongs to the tooling, because the engine's own map check is inert here — §5.3, §5.5. |
| D2 | **No live Python control of traffic.** The scenario is delivered as data and interpreted natively (see the layering clarification in §4.1). |
| D3 | **Tick is the time base.** Wall clock is insufficient; time is counted in ticks from a defined starting event at a known step. `tick` is acceptable as a CoT root attribute and is also to be recorded in PNG `tEXt`. |
| D4 | **Behavior is independent of appearance.** A scenario expresses the same behavior whether run at 09:00 or 23:00, in any weather. Time of day, weather and cinematography are render-time parameters, never encoded in the scenario spec. |
| D5 | **Image-space labeling stays in the post-process.** The CoT sidecar carries truth; it does not carry derived bounding boxes. |

On D3, the recommended scope of the Traffic-Manager clocking change is minimal:

1. Replace `Environment.TickCount64`-derived time in `Stages/ALSM.cs:218-225` with the simulation
   timestamp from the tick (the code already notes this was deferred).
2. Read actor state from the tick's snapshot rather than the free-running world-observer push cache
   (`Stages/ALSM.cs:145`).
3. Gate the stage pipeline on the synchronous handshake that already exists —
   `TrafficManagerLocal.cs:746` (`SynchronousTick`) and the wait at `TrafficManagerLocal.cs:288`.
4. Seed the Traffic Manager's random draws per tick from the run seed.

No restructuring of the stage pipeline is warranted beyond this.

**Why the synchronous fix is still required even if replay works.** Replay reproduces motion that was
already captured; it does not make the *generation* of that motion reproducible. Under the current hybrid
mode — synchronous world, free-running Traffic Manager (`SCTMV.py:1482-1498`) — the artifact being
recorded differs between runs, so a scenario such as "one vehicle tailgates another at five feet along a
named highway" cannot be reliably staged in the first place. A free-running world also has no fixed step,
which leaves `tick` (D3) undefined.

## 8. Authoring tools — survey before building

Existing tools were surveyed before considering a purpose-built trainer interface.

### 8.1 SUMO

`netedit` is SUMO's graphical network and demand editor (FOX toolkit, not Qt). Its demand mode does
create routes, vehicles, flows and trips visually — conceptually close to "nominal traffic pattern for a
stretch of highway" — but it writes **SUMO demand files (`.rou.xml`)**, not OpenSCENARIO. It is a demand
editor for SUMO's own microsimulation, not a storyboard editor.

Separately confirmed: upstream removed SUMO co-simulation on the `ue5-dev` branch.
`Co-Simulation/{Sumo,PTV-Vissim,Carsim}` exists on `carla-simulator/carla` `master` and is **absent from
`ue5-dev`**. Only `netconvert` is used here, as an offline OSM-to-network converter.

### 8.2 Candidates

| Tool | Licence | Vitality (2026-07-28) | What it does | Fit |
|---|---|---|---|---|
| [esmini](https://github.com/esmini/esmini) | MPL-2.0 | 930★, pushed today | OpenSCENARIO player + viewer; the de-facto reference implementation; several editors build on it | **High** — scenario visualization and validation without standing up CARLA |
| [scenariogeneration (pyoscx)](https://github.com/pyoscx/scenariogeneration) | MPL-2.0 | 379★, active | Python library generating linked `.xodr` + `.xosc`, with parametrization and sweep generation built in | **High** — directly matches the seeded-generator/permutation design |
| [Scenic](https://github.com/BerkeleyLearnVerify/Scenic) | BSD-style | 376★, active | Probabilistic scenario description language; a program defines a *distribution* over scenes, sampled to concrete scenarios; supports dynamic agent policies; an official CARLA scenario modeling language | **High conceptually** — this is the seeded statistical generator, already built; but it drives CARLA through the upstream Python API, so it inherits the waypoint dependency of §3.3 |
| [OpenScenarioEditor](https://github.com/ebadi/OpenScenarioEditor) | BSD-3 | 171★, moderate | Simple graphical `.xosc` editor built on esmini | Medium — starting point, not a finished product |
| [Scenario Studio](https://github.com/mljack/scenariostudio) | MPL-2.0 | 3★, last push 2025-12 | esmini-based `.xosc` authoring environment, Windows-only | Low — effectively single-author, minimal adoption |
| [drawtonomy](https://docs.drawtonomy.com/use-cases/openscenario-simulator/) | Hosted service | active | Browser-based `.xosc` simulation and recording via esmini-WASM | Reference only — hosted, not embeddable |

Commercial options exist (MathWorks RoadRunner Scenario, MSC VTD, IPG CarMaker) and are not evaluated
here.

### 8.3 Assessment

There is **no mature open-source WYSIWYG OpenSCENARIO editor**. The strong open-source assets are
*programmatic generation* (scenariogeneration, Scenic) and *playback/visualization* (esmini). That
matches §2.2's practical observation: real scenarios of this size are generated, not hand-drawn, so the
generator is the higher-value component and the graphical surface is a convenience over it.

An adopt-first path that avoids most of the build:

1. **Generation** — scenariogeneration for `.xosc` emission with parameter sweeps, or Scenic if a
   probabilistic scene distribution is wanted. Either produces the artifact; neither needs to execute it.
2. **Visualization and sanity-check** — esmini, which plays `.xosc` against `.xodr` with no CARLA
   involvement. This addresses the "author cannot see the scenario without running the full simulation"
   gap directly and cheaply, since this fork already produces the `.xodr`.
3. **Execution** — the native scenario executor of §4.1, reading the pattern spec compiled from `.xosc`.

### 8.4 If a purpose-built trainer interface is still wanted

Only the authoring front-end would be built; generation, playback and execution are covered above. Of the
implementation options considered:

- **.NET 10 + Blazor + Radzen, MVVM** — consistent with the existing CarlaNet stack, can reference the
  scenario-spec and map assemblies directly (no cross-language marshalling for lane resolution), and
  browser-hosted map rendering makes OSM-extract selection and route drawing straightforward. Lowest
  risk given existing expertise.
- **Unreal Slate/UMG** — puts the editor inside the renderer, so authoring happens against the actual
  photoreal world. Highest fidelity preview, but a new toolchain to learn and it couples authoring to a
  running editor session.
- **Agent-generated scenarios** — viable for producing spec or `.xosc` text from natural-language intent,
  and complementary rather than competing: it fills the authoring box, leaving the visualization gap that
  esmini closes.

The sequencing that preserves the most optionality: pattern spec first, esmini for visualization,
generation via an existing library, and a graphical front-end only once the spec has stabilized against
real training runs.

## 9. Open questions

Resolved on 2026-07-29: record and replay work end to end (§5.4), and the replayer's map guard offers
no protection for generated worlds (§5.3), which §5.5 addresses.

Still open:

1. Does replay reproduce vehicle motion closely enough to serve as the appearance-permutation
   mechanism, or is re-execution of the scenario required? Needs the two-run diff harness.
2. Is `.xosc` adopted as the authored artifact, or as an import/export format over a native spec?
3. What acceptance threshold defines "reproducible" — proposed starting bar: maximum per-vehicle
   positional deviation under half a vehicle length over ten minutes of simulated time, measured from
   CoT truth across two runs of the same seed.
4. How much does a rebuild from an identical recipe perturb the generated `.xodr`? This sets the
   tolerance policy for the two-tier world-binding gate of §5.5, and is measured by rebuilding the same
   OSM extract twice and diffing the elevation profiles.
</content>
</invoke>
