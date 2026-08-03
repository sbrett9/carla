# 18 — Scenario Fabrication for Pattern-of-Life Model Training

**Status:** Research / design note. No code changed. Three findings were validated against a running
server rather than inferred: the recorder and replayer (§5.4), the dwell mechanisms and the idle cull
(§4.4), and the authoring tool against a generated world (§8.3).
**Date:** 2026-07-28, amended 2026-07-30
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
That API does not exist in the shim (§4.6). Executing storyboards on CarlaNet's own primitives avoids
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

### 4.3 Dwell is not free: the Traffic Manager destroys stationary vehicles

Dwell — remaining stopped at a chosen place for a long period — is the elementary primitive of a
pattern of life, and the Traffic Manager actively works against it. `ALSM.Update` culls the most-idle
registered vehicle (`Stages/ALSM.cs:186-196`):

- `IsVehicleStuck` reports true once idle duration reaches `BLOCKED_TIME_THRESHOLD`, **90 seconds**, or
  `RED_TL_BLOCKED_TIME_THRESHOLD`, 180 seconds, when the vehicle is held at a red light
  (`Constants.cs:37-43`).
- The idle timer resets only while speed exceeds `STOPPED_VELOCITY_THRESHOLD`, 0.8 m/s
  (`Stages/ALSM.cs:551-559`). A deliberately parked vehicle never resets it.
- On firing, the vehicle is destroyed over RPC and deregistered. Destruction is throttled to one per
  `DELTA_TIME_BETWEEN_DESTRUCTIONS`, 10 seconds, and only the single most-idle vehicle is taken per
  pass — but a parked vehicle is by definition the most idle, so it is always first in line.

**A long dwell is therefore impossible for a Traffic-Manager-registered vehicle without intervention.**
Two exemptions exist in the same code path:

- **Hero actors are skipped** — the cull tests `!_heroActors.ContainsKey(...)`, and hero status is read
  from the `role_name` attribute. Marking a loitering vehicle as hero exempts it with no code change,
  at the cost of also making it an anchor for the hybrid-physics active radius.
- **Unregistered vehicles are never tracked** — idle bookkeeping covers registered vehicles only, so a
  vehicle taken off autopilot and held on its brakes falls outside the mechanism entirely.

The threshold is measured against ALSM's wall-clock timestamp (`Stages/ALSM.cs:218-225`), not
simulation time. At the 84% clock ratio measured in §5.4 the cull lands after roughly 76 seconds of
simulated time, and the ratio moves with load — so the removal point is not reproducible between runs.
This is an additional argument for the simulation clock of §7.

**Neither existing exemption is the right mechanism.** Hero status carries unrelated meaning — it
anchors the hybrid-physics active radius and marks the vehicle as the subject of the simulation — so
using it to mean "stationary on purpose" overloads a flag whose other effects are unwanted.
Deregistering the vehicle surrenders Traffic Manager control entirely, which then has to be
re-established on departure. What the scenario executor needs is a discrete per-vehicle exemption:
a flag saying this vehicle is stopped deliberately and must not be treated as stuck. It belongs beside
the other per-vehicle knobs in `Parameters.cs`, is set when an entity enters a dwell and cleared when it
leaves, and leaves the stuck-vehicle protection intact for ordinary traffic. The addition is purely
additive to the Traffic Manager's interface.

One implementation hazard: the exemption cannot be applied only at the cull site. `UpdateIdleTime`
(`Stages/ALSM.cs:545-563`) nominates a single most-idle vehicle per pass, and a deliberately parked
vehicle will always hold that slot. Skipping it at the point of destruction would leave it nominated,
shielding every genuinely stuck vehicle behind it and silently disabling stuck-vehicle removal for the
whole population. Exempt vehicles must be excluded from nomination, not from destruction.

**Commanding the stop is not what it appears either.** `SetDesiredSpeed(actor, 0)` is stored and
honoured literally — it is not treated as "unset" — but a zero target is special-cased so the
longitudinal controller sees no velocity error and emits **neither throttle nor brake**
(`Stages/MotionPlanStage.cs:303-305`; `PIDController.cs:52-68`). The vehicle coasts, and on any grade it
keeps rolling. `SetPercentageSpeedDifference(actor, 100)` reaches the same zero target by a different
route and behaves identically. Small non-zero targets are worse: the error is normalised by the target
(`MotionPlanStage.cs:305`), so a target near 0.5 m/s drives the controller into throttle-brake
oscillation. A genuine standstill requires taking the vehicle out of Traffic Manager control, because a
registered vehicle is issued a fresh control command every tick that overwrites anything the client
applies (`MotionPlanStage.cs:338-346`).

The workable sequence is therefore: ramp the target down while it remains above zero, so the controller
brakes for real; then unregister and hold the vehicle on brake and handbrake. Unregistering also removes
it from the idle-cull population, which is why the exemption flag matters most for dwells that keep the
vehicle registered.

Three further behaviours constrain the executor:

- **Reaching the end of an itinerary is silent.** When the path buffer empties, `LocalizationStage`
  clears the stored path and route (`LocalizationStage.cs:713-716`, `:778-781`) and the vehicle reverts
  to choosing random successors at junctions — it never halts, and there is no arrival event to
  subscribe to. Consumption happens a full horizon ahead of the vehicle, so the parameter clears before
  physical arrival. The client must detect arrival by position. Worse, a route that ends at a graph
  dead-end marks the actor for removal and ALSM destroys it, and the OSM mode that enables this is on by
  default (`Parameters.cs:77`; `ALSM.cs:197-208`).
- **Per-vehicle settings are never purged.** Unregistering clears stage state, buffers and controller
  state but leaves `Parameters` untouched, so a stale desired speed, custom path or imported route
  re-arms the moment the vehicle is re-registered. Clearing a path takes *two* calls, because the path
  and the empty-buffer flag are independent (`Parameters.cs:260-266`), and neither is exposed on the
  Python shim.
- **A stopped vehicle blocks followers indefinitely.** Collision negotiation is purely geometric and
  exempts nothing (`CollisionStage.cs:279-311`); followers creep and brake in a stable queue, and the
  lane-change escape needs the obstacle to be seen between 20 m and 50 m out with free adjacent lanes
  (`LocalizationStage.cs:544-586`). **The only mechanism that ever clears such a queue is the 90-second
  idle cull** — the very thing the exemption disables. Exempting a vehicle parked in a travel lane
  therefore creates a permanent jam. The exemption is only safe in combination with off-lane placement,
  which is what §4.5 provides.

Two client-facing defects surfaced here: `SetDesiredSpeed` is documented as metres per second
(`Parameters.cs:110`) but the value is divided by 3.6 before use (`MotionPlanStage.cs:254`), so it is
actually km/h; and the path-clearing calls are absent from the Python shim.

### 4.4 Measured dwell behaviour (2026-07-30)

Each candidate mechanism was exercised against a running world: spawn an ordinary sedan, drive under
Traffic Manager control for 20 s, command the stop, hold 120 s while sampling speed and displacement,
then release and observe. One vehicle at a time on a dedicated Traffic Manager port, with no ambient
traffic, so the only registered vehicle was the subject.

| Mechanism | Time to stop | Mean speed held | Drift | Survived | Resumed |
|---|---|---|---|---|---|
| Ramp target down, then unregister and brake | 5.1 s | 0.00 m/s | **0.00 m** | **yes** | 8.37 m/s |
| Unregister and brake immediately | 1.1 s | 0.00 m/s | 0.00 m | **yes** | 8.45 m/s |
| Desired speed 0 | 22.5 s | 0.00 m/s | 0.00 m | no — destroyed | — |
| Speed difference 100% | 22.5 s | 0.00 m/s | 0.01 m | no — destroyed | — |
| Constant velocity 0 | 0.5 s | 0.05 m/s | **2.94 m** | no — destroyed | — |

**The idle cull is confirmed at its documented threshold.** The three mechanisms that leave the vehicle
registered were all destroyed mid-hold; the two that unregister survived. Timing corroborates the
90-second figure directly: the constant-velocity vehicle stopped 0.5 s after the command, was still
present at 61 s, and was gone by 91 s.

**A zero speed target coasts rather than brakes, as §4.3 predicts, but still reaches rest on level
ground.** The evidence is the stopping time — 22.5 s from 8.4 m/s, against 1.1 s when the brake is
actually applied. That is deceleration by rolling resistance alone. It stops, but the stopping point
cannot be placed and a gradient would carry the vehicle onward, so it is unusable for sited dwell even
though it appears to work on flat terrain.

**Constant velocity does not pin the vehicle**: it creeps at 5 cm/s and accumulated 2.94 m over the
hold.

**Ramping the target down before unregistering is the recommended mechanism.** Both surviving
mechanisms hold at exactly zero and resume cleanly — confirming the handbrake releases itself on the
first control frame after re-registration — but the immediate brake is a 1.1 s emergency stop from
8.4 m/s, which reads as a slam when observed from altitude. The 5.1 s ramp is the one to build on.

The run also **confirms the speed-unit defect by measurement rather than by reading**: a commanded
value of 30.0 produced a steady 8.35–8.44 m/s across all five runs, and 30 ÷ 3.6 = 8.33.

**A full 45-minute dwell was then held.** The same mechanism was run with a 2,700-second hold: it
stopped in 5.1 s and reported 0.00 m/s on every one of the 89 samples taken across the period — mean and
maximum both zero — with 0.00 m of accumulated drift. The vehicle survived and resumed to 8.39 m/s. No
threshold beyond the 90-second idle cull exists, and the hold is genuinely static rather than a slow
creep; for contrast, the constant-velocity mechanism's 2.94 m of drift in 90 s would have accumulated to
roughly 90 m over the same period.

The clean resume is evidence that the restoration sequence works, **not** that stale state is harmless:
the speed target was explicitly re-applied before re-registering, which is precisely the mitigation §4.3
requires because per-vehicle settings survive deregistration. A scenario executor must do the same.

### 4.5 Where a vehicle can legitimately stop

A vehicle stopped in a travel lane blocks following traffic and, viewed from altitude, reads as an
anomaly in itself. Roads generated from OSM extracts offer nowhere else to stop, and the reason is not
the road-class filter: **netconvert has no representation of a shoulder or a parking bay at all.** The
key `shoulder` appears nowhere in its sources; shoulders are way *tags*, present on roughly 0.14% of
`highway=*` ways, discarded before their value is read. Its OpenDRIVE writer emits only `sidewalk`,
`biking`, `none`, `rail`, `tram`, `driving` and `restricted` — never `parking`, never `shoulder`.

Relaxing `--keep-edges.by-vclass passenger` cannot create a pull-over lane; it can only admit
parking-lot aisles, and that route is obstructed twice over. The `service=*` subtype is erased during
type resolution, so `service=parking_aisle`, `service=driveway` and `service=alley` all become the
identical edge type `highway.service` and no filter can separate them — only a custom type file can.
And admitting aisles alone orphans each parking lot into its own weak component, which
`--keep-edges.components 1` then deletes; the lots reach the street through driveways, which are 62% of
all service ways and would split parent streets into new junctions, perturbing the junction-joining and
traffic-light guessing that the traffic-light grouping work depends on.

Two viable placements remain, and they are complementary rather than exclusive:

| Placement | Mechanism | Properties |
|---|---|---|
| **Off-network, on the draped terrain** | Position the vehicle on the drape's collidable surface, outside the road network | Available immediately with no pipeline change; works where OSM has no parking data at all; independent of any scenario-format decision. No semantic guarantee the spot is legal — it may fall in a garden or intersect a building baked into the photoreal tiles — and it is invisible to waypoint queries, so siting is explicit per scenario |
| **An injected `type="parking"` lane** | Append a parking lane outboard of the rightmost driving lane in the generated OpenDRIVE, in the manner of the existing elevation, sign and traffic-light injectors | Works on every generated map regardless of OSM coverage; **ambient traffic cannot route onto it by construction**, since CARLA's waypoint graph filters on `LaneType::Driving` (`LibCarla/source/carla/road/Map.cpp:88`) and spawn points default to the same (`LibCarla/source/carla/road/Map.h:136`), while the mesh factory builds all lane types, giving solid collidable ground; queryable from Python as `carla.LaneType.Parking`. Requires no netconvert change. Inherits the sidewalk mesh profile, so its elevation must be reconciled with the drape |

Admitting driveways is deferred. They are private infrastructure, altered without public record, whereas
changes to the public road network are documented — so a scenario bound to a map that includes driveways
would decay in a way the rest of the network does not.

Two defects in the conversion pipeline surfaced during this investigation, neither specific to
scenarios and both worth tracking separately:

- **`osm_clip.py` discards every relation.** It rebuilds the clipped document from `<bounds>`, way-member
  nodes and ways only (`CarlaNet/python/osm_clip.py:131-146`). Multipolygon parking areas are lost, and
  so are **OSM turn-restriction relations**, which netconvert does import — meaning every clipped map is
  built without them. This bears directly on intersection behaviour, see
  [10 — Intersection Navigation & Traffic Control](10_Intersection_Navigation_Traffic_Control.md).
- **`--default.sidewalk-width 2.80`** (`CarlaNet/src/CarlaNet.Map/OsmConverter.cs:252`) has no effect,
  because no sidewalk-import option is enabled.

### 4.6 Work ledger

| Tier | Work | Where |
|---|---|---|
| 1 | Pattern-spec schema + loader; seeded generator; permutation sweep | Tooling (language open) |
| 1 | Scenario-executor service: trigger evaluation, entity state machine (transit → dwell → depart), commands to TM | C# — new project beside `CarlaNet.TrafficManager` |
| 1 | Geographic-to-lane resolution (lat/lon → world → nearest drivable lane) | C# — `CarlaNet.TrafficManager/InMemoryMap.cs:69` `GetWaypoint(Location)` already does this and is already loaded; needs exposure |
| 1 | Dwell as a controlled stop: ramp the speed target down, unregister, hold on brake and handbrake, and restore the target before re-registering (§4.4) | Scenario executor |
| — | Per-vehicle exemption from idle removal (§4.3), applied at the nomination site — optional, only for dwells that must stay registered | C# — `Parameters.cs`, `Stages/ALSM.cs`, plumbed through `TrafficManagerLocal.cs` / `TrafficManager.cs` / `ITrafficManagerCallback.cs` and the Python shim |
| 2 | Off-network placement: project a geographic position onto the draped terrain surface (§4.5) | C# — drape query, already present |
| 2 | Expose the path-clearing calls and correct the `SetDesiredSpeed` unit documentation (§4.3) | C# — `Parameters.cs:110`; Python shim `TrafficManager` |
| 3 | Injected `type="parking"` lane in the generated OpenDRIVE (§4.5) | C# — new injector beside `CarlaNet.Map/OpenDrive/{Elevation,Sign,TrafficLight}Injector` |
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

**The digest must be defined on a canonical byte source.** The same world already exists on disk in two
byte-different forms: the copy the server stages carries a UTF-8 byte-order mark, while the copy SCTMV
saves has doubled carriage returns, because it writes with `open(..., "w")` and no `newline=""` so
Python's text mode appends a second `\r` to line endings that already have one. The two differ by
10,977 bytes for an identical document. Hashing whichever file is to hand would report a mismatch for
the same world, so the digest is taken from the bytes the server holds — those returned by `GetXODR` —
and never from a client-written copy. The `newline=""` omission is worth fixing regardless.

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
- Timestamps were **wall-clock UTC** in both emitters. Two runs of the same scenario therefore carried
  unrelated times and could not be diffed without re-alignment — and wall clock does not track the world
  in any case. **Implemented and verified 2026-07-31**: every recorded capture and every emitted track
  now carries the simulation tick that produced it. Three properties of the implementation are worth
  recording, each measured rather than assumed:

  - **The clock ratio is not a constant.** One session ran at 16.9 ticks per second against a 20 Hz
    target (84% of real time); another ran at 19.8 (99%). Had the offset been fixed it could have been
    calibrated away and timestamps kept; it is not, so it cannot.
  - **The tick is episode-scoped, not run-scoped.** It belongs to the server's episode and keeps
    advancing between sessions — the world free-runs once an interactive session hands it back, observed
    at roughly 171 ticks per second with no client driving it, so consecutive runs began 190,000 ticks
    apart. The absolute value pairs artifacts within a run; **comparing runs requires normalising against
    each run's first tick.** This is what D3's "from a defined starting event" means in practice.
  - **The two emission paths agree.** The recorder takes the tick from the C# sensor frame header; the
    live feed takes it from a world-observer tick subscription in Python. A live event and a sidecar
    written at the same moment reported ticks 9761 and 9760 — one tick, 50 ms apart — so both track the
    world clock rather than drifting independently.

  Consecutive captures advance by exactly the expected number of ticks (ten, at a 0.05 s step recording
  at 2 Hz), across every session measured.
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

Observed on the live feed rather than argued from the standard: two stationary vehicles in a single emit
cycle broadcast `course="271.8"` at 0.01 m/s and `course="299.3"` at 0.00 m/s. Both bearings are noise
from a near-zero velocity vector, and this is the ordinary case for every loitering vehicle a pattern
contains.

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
| D6 | **Dwell is achieved by unregistering the vehicle**, after ramping its speed target down so the stop is controlled — measured in §4.4. A per-vehicle exemption from idle removal remains desirable for dwells that must stay under Traffic Manager control, but is no longer a precondition; if built it must exempt at the nomination site, not the destruction site (§4.3). |
| D7 | **Off-network placement on the draped terrain is adopted now**; an injected parking lane is accepted in principle and can follow. Driveways stay excluded from the road network, being private infrastructure that changes without public record (§4.5). This is not merely aesthetic: a dwell in a travel lane combined with the D6 exemption produces a permanent traffic queue, because the idle cull is the only thing that ever clears one (§4.3). |

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
| [drawtonomy](https://github.com/kosuke55/drawtonomy) | Apache-2.0 (ecosystem); application itself not published | 92★, active | Browser canvas for road and scenario authoring, with OSM-to-lane generation, OpenDRIVE import/export, OpenSCENARIO export and in-browser esmini-WASM playback | **Selected** — see §8.3 |

Commercial options exist (MathWorks RoadRunner Scenario, MSC VTD, IPG CarMaker) and are not evaluated
here.

### 8.3 drawtonomy, validated against a generated world (2026-07-29)

An initial reading of this survey concluded that no mature open-source graphical OpenSCENARIO editor
existed and that one would have to be built. That conclusion was wrong: drawtonomy is a browser canvas
that authors road networks and scenarios directly, and it was tested against a world produced by this
fork's own pipeline.

**What was verified.** The Gardnerville extract's generated `.xodr` was imported and rendered correctly
at full extent. With the satellite background enabled, the imported lane geometry lay on the real road
surfaces, and the junction structure matched the actual intersection. Comparing the exported
`geoReference` against the original confirms this at the data level — same projection, same origin, same
false eastings:

```
generated:  +proj=tmerc +lat_0=38.91108 +lon_0=-119.76459650000001 +k=1 +x_0=0 +y_0=0 +ellps=WGS84 +units=m +no_defs
round-trip: +proj=tmerc +lat_0=38.91108000 +lon_0=-119.76459650    +k=1 +x_0=0 +y_0=0 +datum=WGS84 +units=m
```

The only change, `+ellps=WGS84 +no_defs` to `+datum=WGS84`, is functionally equivalent for WGS84.
**Coordinates round-trip without displacement.** This is also the first independent confirmation that
the origin-pinning of the OSM conversion produces a conformant georeference — every previous check was
this fork's own code reading back what it had written.

**The road network does not round-trip, and must not be brought back.** Comparing the exported `.xodr`
against the input:

| | generated | round-tripped |
|---|---|---|
| OpenDRIVE version | 1.4 | 1.8 |
| roads | 213 | 509 |
| lanes | 426 | 1018 |
| junctions | 24 | 1 |
| **elevation records** | **1675** | **0** |
| signals | 4 | 4 |

Every elevation profile is lost, so the exported map is flat, and the road and junction decomposition
differs substantially. This is a 2D canvas behaving as a 2D canvas, not a defect. **The integration is
therefore asymmetric: import the generated `.xodr` as an authoring backdrop, keep it authoritative on
this side, and take only the scenario out.** The Lanelet2 `.osm` export is a different lane model again
and equally not a return path.

**Scenario export supports this directly.** The scenario editing mode offers an OpenSCENARIO export with
an *include* choice of `xosc` alone or `xosc + xodr`, a version selector for 1.0, 1.1 or 1.2, and a
target-simulator choice of esmini or Generic. Exporting `xosc` alone, Generic, at 1.1 or later, gives
exactly the artifact wanted — and versions from 1.1 define `GeoPosition`, which may close the
map-portability gap identified in §2.3 without any extension work.

**Dependency profile.** The published repository contains the extension SDK, a development server, an
MCP server, extensions, templates and documentation. The canvas application itself is not in it: the
workspace declares only `packages/*` and `docs-site`, the sole CI workflow tests the SDK, and the
development server downloads a prebuilt bundle from the vendor's site. The application therefore cannot
be built from source or forked.

There are **two OpenSCENARIO exporters**, and the distinction matters. The SDK carries one, in
`packages/drawtonomy-sdk/src/exporter/openscenario.ts`, which turns drawn paths into
`FollowTrajectoryAction` stories — that one is open, Apache-2.0, and is the documented extension point
for new target formats. The scenario editing mode uses a different, richer exporter that lives in the
closed application and emits phase-sequenced `SpeedAction`s with act chaining. Anything needing changes
to *scenario* output is therefore not a local change. As §8.4 records, nothing does.

Three consequences follow, of which the first two are already mitigated:

1. *No independent deployment* — resolved by snapshotting the bundle and serving it locally; tooling for
   this is staged in `drawtonomy-offline-build/`, with a standard-library-only server that makes no
   network requests.
2. *No version pinning* — the same snapshot pins it, which matters because an authoring tool that
   changes between training runs undermines reproducibility.
3. *A hosted product from a single maintainer* sits in the authoring path. This is acceptable because it
   is **not** in the execution path, and because the artifact crossing the boundary is a standard
   `.xosc` file rather than a proprietary format. The authoring tool is replaceable without disturbing
   anything downstream — which is the practical payoff of keeping the interchange format standard.

### 8.4 The authorable scenario vocabulary

Read from the application bundle rather than inferred from samples. Twenty trigger types are offered to
the author, **each valid as a phase-start trigger** as well as on an event and as a scenario end
condition:

| Group | Triggers |
|---|---|
| Timing | `immediately`, `afterTime` (simulation time), **`standStill` (with a duration)**, `elementState` (phase or event state) |
| Spatial | `reachPosition`, `distanceToPosition`, `distanceToActor`, `traveledDistance`, `endOfRoad` |
| Kinematic | `speed`, `relativeSpeed`, `acceleration`, `timeHeadway`, `timeToCollision` |
| Environment | `trafficSignal`, `collision`, `offroad` |
| Parametric | `parameter`, `variable`, `advanced` |

**This closes the question of whether a long dwell is expressible: it is, with no exporter work.** The
`standStill` trigger carries a duration and serialises to `<StandStillCondition duration="..."/>`. A
dwell is authored as three phases — decelerate to zero, then a phase whose start trigger is *stand
still* for the dwell length, then a phase returning to speed — with the scenario end condition raised
past the total.

Two sample exports informed this. A two-phase file confirmed the emitted shape: OpenSCENARIO 1.1, each
authored phase becoming its own `Story` and `Act`, the act's `StartTrigger` carrying the real timing
while the inner event fires immediately, and a stop emitted as a `SpeedAction` with
`dynamicsShape="linear"` over two seconds to `AbsoluteTargetSpeed value="0"` — which is precisely the
ramped stop measured as correct in §4.4. A three-phase file showed that the *default* phase-start
trigger is `elementState` on the preceding act, which completes as soon as its speed ramp finishes, so
a resume authored that way follows immediately and no dwell occurs. That is an authoring choice, not a
tool limitation.

**Road and lane identifiers survive the round trip.** The samples emit
`LanePosition roadId="243" laneId="-1" s="70"`, and road 243 in the generating world is Centerville
Lane, 191.998 m long, carrying exactly one driving lane at id −1. Every field resolves against the
original network, so an exported scenario runs against the generated `.xodr` with no translation step.
Identifiers are stable within a build but not guaranteed across rebuilds, which is what the world
binding of §5.5 and decision D1 exist to police.

Two smaller observations: no `ObjectController` is emitted, so nothing vendor-specific has to be
stripped and the executor owns how actions are realised; and vendor properties such as
`drawtonomy:template="sedan"` survive, giving a hook for blueprint selection. `ParameterDeclarations`
came out empty in both samples, but `parameter` and `variable` triggers exist in the vocabulary, so
the permutation layer may have a native hook rather than needing to be supplied entirely from this
side.

### 8.5 The remaining build

With authoring, visualization and preview covered, what remains to be built is the execution side:

1. **Generation and sweeps** — scenariogeneration for programmatic `.xosc` emission with parameter
   variation, or Scenic where a probabilistic scene distribution is wanted. Complements hand-authoring
   rather than replacing it.
2. **Compilation** — resolve the authored scenario against the loaded world, bind it to the world digest
   of §5.5, and attach the training metadata `.xosc` cannot carry (§4.2).
3. **Execution** — the native scenario executor of §4.1.

A purpose-built editor is no longer on the critical path. If one is ever needed — because the dependency
in §8.3 becomes unacceptable, or because the canvas cannot express something — the .NET and Blazor
option remains the lowest-risk route, since it can reference the existing map assemblies directly and
the geometry work would not have to cross a language boundary.

### 8.6 The authoring workflow, end to end

Where a human sits in the pipeline, and what is machine work:

| Step | Who | What happens |
|---|---|---|
| 1. Choose the area | Trainer | Select an OSM extract for the region of interest |
| 2. Build the world | Machine | The existing conversion produces the elevated, Cesium-aligned OpenDRIVE, its draped terrain, and the build recipe of §5.5 |
| 3. Author the pattern | Trainer | Import the generated `.xodr` into the canvas (§8.3) and place entities, routes, dwell sites and schedules directly on it, against a satellite backdrop; export the scenario alone |
| 4. Compile | Machine | Resolve positions against *this* world, bind the result to the world digest, and attach the training metadata the scenario file cannot carry |
| 5. Sweep | Trainer sets bounds, machine expands | Cross the behaviour parameters with the appearance parameters — time of day, weather, camera track — into a run list |
| 6. Execute | Machine | The scenario executor drives the entities; the behaviour log and the imagery-plus-truth recordings are captured together over a shared tick range |
| 7. Post-process | Machine | The detector labels the imagery; the resulting tracks feed the pattern-of-life model |

The trainer's work is confined to steps 1, 3 and 5. Everything else is derived.

**Step 4 is where placement is decided, and it is why the storyboard model needs no extension to support
off-network stops.** OpenSCENARIO positions are not required to be lane-relative: `WorldPosition`
carries an absolute pose, while `LanePosition` and `RoadPosition` are road-referenced. A dwell on the
draped terrain is therefore an ordinary `WorldPosition`, and a dwell in an injected parking lane is a
`LanePosition` — the same grammar covers both placements described in §4.5.

What the pattern spec adds is the *intent*, so the choice survives a rebuild. A `park` step records a
geographic position and a placement mode — snap to the nearest driving lane, snap to a parking lane, or
project onto the terrain surface — and the compiler resolves that against the world actually loaded.
Authoring in geographic coordinates with a declared placement mode is what makes a pattern portable
across map rebuilds; resolving to a fixed `WorldPosition` at authoring time would weld it to one build,
which is precisely the failure mode §5.5 exists to prevent.

## 9. Open questions

Resolved on 2026-07-29: record and replay work end to end (§5.4), and the replayer's map guard offers
no protection for generated worlds (§5.3), which §5.5 addresses.

Also resolved: the dwell mechanism and the idle-cull threshold are measured and a full 45-minute
dwell demonstrated (§4.4), and a graphical
authoring tool exists and works against a generated world (§8.3), which settles question 2 below in
favour of `.xosc` as the authored artifact with the pattern spec reduced to a wrapper for the training
metadata it cannot carry. The authorable trigger vocabulary is now read from the application itself
(§8.4) and covers a long dwell without exporter work.

Still open:

1. Does replay reproduce vehicle motion closely enough to serve as the appearance-permutation
   mechanism, or is re-execution of the scenario required? Needs the two-run diff harness.
2. What acceptance threshold defines "reproducible" — proposed starting bar: maximum per-vehicle
   positional deviation under half a vehicle length over ten minutes of simulated time, measured from
   CoT truth across two runs of the same seed.
3. How much does a rebuild from an identical recipe perturb the generated `.xodr`? This sets the
   tolerance policy for the two-tier world-binding gate of §5.5, and is measured by rebuilding the same
   OSM extract twice and diffing the elevation profiles.
4. How does an injected parking lane (§4.5) reconcile with the draped terrain? It inherits the sidewalk
   mesh profile — a flat top with a downward skirt — so its elevation and the widened cross-section both
   interact with the drape, and whether it reads as a usable surface or a raised curb strip is
   unestablished.
