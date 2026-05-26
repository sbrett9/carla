# TRAFFIC_MANAGER_PORT_SPEC

Authoritative specification for porting CARLA's C++ TrafficManager + OpenDRIVE/Road
subsystem to .NET 10, inside the CarlaNet project. Two new projects:

- `CarlaNet.Map` — OpenDRIVE XML parser, road graph, waypoint generation
- `CarlaNet.TrafficManager` — AI runtime (ALSM + 6 stages + orchestrator)

Goal: **full feature parity in one pass**, perf target **50 AI vehicles at 20-30 FPS**
on the same machine running the CARLA server in sync mode @ 20 Hz fixed step.

Project precedent: `CarlaNet/MANUAL_CONTROL_PORT_SPEC.md` (format) +
`CarlaNet/CarlaNetSupplementary.md` (protocol conventions). Reuse existing CarlaNet
types where possible; do not duplicate `Location`, `Transform`, `Actor`, etc.

All file/line references are absolute paths under `g:\Projects\CarlaUE_5_7_4\carla\`.

---

## 1. File Inventory + LoC Counts

### 1.1 `LibCarla/source/carla/trafficmanager/` — Total: **8,830 LoC** (16 .cpp + 28 .h)

| File | LoC | Purpose |
|------|----:|---------|
| ALSM.cpp / ALSM.h | 396 / 116 | **Agent Lifecycle & State Management**: discover spawned actors, push kinematic state into `SimulationState`, evict idle vehicles |
| AtomicActorSet.h | 113 | Mutex-guarded `map<ActorId, ActorPtr>` for the registered vehicle pool |
| AtomicMap.h | 59 | Mutex-guarded `unordered_map<K,V>`. Used by `Parameters` per-actor maps |
| CachedSimpleWaypoint.cpp / .h | 163 / 58 | Binary serialization of cooked `InMemoryMap` (`Cook`/`Load`) |
| CollisionStage.cpp / .h | 428 / 113 | Geodesic-bounding-box collision detection between vehicles + walkers |
| Constants.h | 165 | All tuning constants (PID, hybrid mode, collision radii, etc.) |
| DataStructures.h | 71 | `LocalizationData`, `CollisionHazardData`, `StateEntry`, `ActuationSignal`, `ControlFrame` aliases |
| InMemoryMap.cpp / .h | 588 / 125 | Discretized waypoint graph: dense interpolation of `client::Map` topology + boost-rtree spatial index |
| LocalizationStage.cpp / .h | 680 / 92 | Maintain a per-vehicle horizon buffer (`Buffer = deque<SimpleWaypointPtr>`); handles lane changes |
| LocalizationUtils.cpp / .h | 97 / 60 | `GetTargetWaypoint`, `DeviationDotProduct`, `PopWaypoint` helpers |
| MotionPlanStage.cpp / .h | 479 / 96 | PID throttle/brake/steer + hybrid-physics teleportation + traffic-light slow-down |
| PIDController.h | 63 | `PID::RunStep()` (header-only). Plain longitudinal+lateral PID |
| Parameters.cpp / .h | 485 / 299 | Holds **all** runtime knobs (per-actor + global). The biggest pure-data class |
| RandomGenerator.h | 22 | Wrapper around `std::mt19937`, returns float in [0,100) |
| SimpleWaypoint.cpp / .h | 146 / 140 | Wraps `client::Waypoint` + adds successor/predecessor + lane-change links + `RoadOption` |
| SimulationState.cpp / .h | 103 / 106 | Per-tick snapshot of every actor's kinematics + TL state, indexed by `ActorId` |
| SnippetProfiler.h | 80 | Optional perf timer (not on hot path) |
| Stage.h | 29 | Pure-virtual base `Update(index)` / `RemoveActor` / `Reset` |
| TrackTraffic.cpp / .h | 194 / 66 | Per-waypoint and per-geodesic-grid occupancy maps. Drives `CollisionStage` candidate filter |
| TrafficLightStage.cpp / .h | 194 / 64 | TL state lookup; non-signalized junction priority FIFO; stop-sign timer |
| TrafficManager.cpp / .h | 236 / 406 | **Public facade**: per-port singleton map; constructs either `TrafficManagerLocal` (own a server) or `TrafficManagerRemote` (client to existing server) |
| TrafficManagerBase.h | 177 | Pure-virtual interface shared by Local + Remote |
| TrafficManagerClient.h | 324 | Header-only rpclib client to a `TrafficManagerServer` running in another process |
| TrafficManagerLocal.cpp / .h | 496 / 285 | **Orchestrator**: owns all stages, drives the worker thread, ApplyBatch the per-frame commands |
| TrafficManagerRemote.cpp / .h | 301 / 195 | Thin pass-through: forwards every method to a `TrafficManagerClient` |
| TrafficManagerServer.h | 314 | Header-only rpclib server (`port 8000`) bound to a `TrafficManagerBase*` |
| VehicleLightStage.cpp / .h | 160 / 46 | Optional: turn on headlights/blinkers based on weather + planned turn |

### 1.2 `LibCarla/source/carla/opendrive/` — Total: **1,845 LoC** (12 .cpp + 11 .h)

| File | LoC | Purpose |
|------|----:|---------|
| OpenDriveParser.cpp / .h | 53 / 25 | `Load(xml_string) -> optional<road::Map>`. Sequences the 10 sub-parsers, returns built map |
| parser/ControllerParser.cpp / .h | 57 / 33 | `<controller>` → traffic-light controller groupings |
| parser/GeoReferenceParser.cpp / .h | 71 / 32 | `<georeference>` PROJ string → lat/lon origin |
| parser/GeometryParser.cpp / .h | 166 / 33 | `<planView>` → Line / Arc / Spiral / Poly3 / ParamPoly3 |
| parser/JunctionParser.cpp / .h | 102 / 33 | `<junction>` connections + lane-links |
| parser/LaneParser.cpp / .h | 226 / 32 | `<lanes>`, `<laneSection>`, `<lane>`, `<width>`, `<roadMark>` |
| parser/ObjectParser.cpp / .h | 125 / 32 | `<objects>` (crosswalks, props) |
| parser/ProfilesParser.cpp / .h | 147 / 33 | `<elevationProfile>`, `<lateralProfile>` |
| parser/RoadParser.cpp / .h | 334 / 35 | `<road>` metadata, predecessor/successor links, road-type/speed |
| parser/SignalParser.cpp / .h | 154 / 32 | `<signals>` and `<signalReference>` |
| parser/TrafficGroupParser.cpp / .h | 57 / 32 | `<userData>` traffic group definitions |

External XML lib: **pugixml** (`#include <third-party/pugixml/pugixml.hpp>` at
`OpenDriveParser.cpp:22`). In .NET we use **`System.Xml.Linq`** (`XDocument`).

### 1.3 `LibCarla/source/carla/road/` — Total: **9,379 LoC** (8 .cpp + 18 .h root, 4 .cpp + 18 .h in `element/`, 1 .h in `object/`)

**Root** (`carla/road/`):

| File | LoC | Purpose |
|------|----:|---------|
| Controller.h | 64 | TL controller signal-group struct |
| Deformation.h | 66 | Static helper for terrain Z deformation (unused by TM) |
| InformationSet.h | 73 | Templated container `<T : RoadInfo>` keyed by `s` — looks up the active record for any distance along the road |
| Junction.h | 108 | Junction with `Connection` (incoming road → connecting road → lane links) + `_road_conflicts` |
| Lane.cpp / .h | 279 / 148 | Lane geometry (`GetWidth(s)`, `ComputeTransform(s)`), 22-value `LaneType` flag enum, next/prev lane lists |
| LaneSection.cpp / .h | 66 / 67 | Set of lanes with a shared `lane_offset` cubic polynomial |
| LaneSectionMap.h | 53 | `multimap<double, LaneSection>` keyed by `s`; finds the section active at `s` |
| LaneValidity.h | 30 | Trivial struct |
| Map.cpp / .h | 1707 / 262 | **The big one.** `GetClosestWaypointOnRoad`, `GetNext(distance)`, `GenerateTopology`, `GenerateWaypoints`, signal search, lane-marking calculation. Hosts an r-tree of `(SegmentCloudRtree<Waypoint>)`. ~600 of those LoC are mesh generation — **not needed for TM** |
| MapBuilder.cpp / .h | 1158 / 432 | Builder used **only by the parser**. Calls `AddRoad`, `AddGeometryLine`, etc., then `Build()` resolves successor/predecessor links and lane connectivity |
| MapData.cpp / .h | 45 / 104 | Container: `unordered_map<RoadId, Road>` + `unordered_map<JuncId, Junction>` + signals + controllers + georeference |
| MeshFactory.cpp / .h | 1164 / 164 | Generates 3D meshes for road geometry. **NOT NEEDED — skip entirely.** TM never calls it |
| Object.h | 40 | Object metadata struct |
| Road.cpp / .h | 322 / 215 | `GetLength`, `GetLaneByDistance(s, lane_id)`, `GetNexts/GetPrevs`, `GetDirectedPointIn(s)` (the master function that resolves `s` along a road into world (x,y,z,heading) using all the lateral/elevation profiles) |
| RoadElementSet.h | 120 | Templated sorted container of `RoadInfo` records, sorted by `s` |
| RoadTypes.h | 32 | `using RoadId = uint32_t; using JuncId = int32_t; using LaneId = int32_t;` etc. — **load-bearing for hashing** |
| Signal.h | 231 | Traffic-light signal: position, controller link, type code |
| SignalType.cpp / .h | 139 / 58 | Mapping from OpenDRIVE signal-type codes to TL/stop/yield semantics |

**`element/`:**

| File | LoC | Purpose |
|------|----:|---------|
| Geometry.cpp / .h | 241 / 345 | **The five OpenDRIVE curve primitives**: `GeometryLine`, `GeometryArc`, `GeometrySpiral` (Fresnel integrals — the hardest one), `GeometryPoly3`, `GeometryParamPoly3`. Each implements `PosFromDist(s) -> DirectedPoint`, `DistanceTo(loc) -> (s, dist)` |
| LaneCrossingCalculator.cpp / .h | 111 / 32 | Computes lane-marking crossings between two locations (not needed by TM) |
| LaneMarking.cpp / .h | 84 / 85 | Lane-marking enum + visual properties (TM doesn't use, but Python API exposes) |
| RoadInfo*.h (16 files) | ~50–100 each | Polymorphic record types: width, speed, mark record, lane offset, elevation, geometry, etc. Visited via `RoadInfoVisitor` |
| RoadInfoIterator.h | 105 | Cursor over `RoadElementSet<T>` |
| RoadInfoVisitor.h | 63 | Visitor pattern for `RoadInfo` hierarchy |
| Waypoint.cpp / .h | 23 / 66 | **The `Waypoint` value type**: `{ road_id, section_id, lane_id, s }` — a tiny POD, the canonical "place on the road" identifier. `std::hash` quantizes `s` to half-cm precision |

**`object/`:** RepeatRecord.h (30 LoC) — props metadata, not needed.

### 1.4 Python binding source

| File | LoC | Purpose |
|------|----:|---------|
| `PythonAPI/carla/src/TrafficManager.cpp` | 115 | boost::python bindings — **the contract** for the user-facing C# TM facade (§3) |

### Aggregate totals to port

| Subsystem | LoC | Notes |
|-----------|----:|-------|
| TrafficManager | 8,830 | Drop ~80 LoC `TrafficManagerClient.h` boilerplate (we already have `CarlaNet.Transport.TrafficManager.TrafficManagerClient.cs` for the remote case) |
| opendrive parser | 1,845 | Replace pugixml with `XDocument` — should shrink to ~1,400 LoC C# |
| road graph (excluding `MeshFactory.cpp` + `MeshFactory.h` = 1,328 LoC and `LaneCrossingCalculator` = 143 LoC) | 9,379 − 1,471 = **7,908 LoC** in scope | Mesh gen is server-side only |
| **Total in scope** | **~18,500 LoC C++** | Expect ~14,000–16,000 LoC C# (terser, less template noise) |

---

## 2. Subsystem Map

```
                    [Server: CARLA UE5 simulator over msgpack-RPC]
                                    |
                  get_map_data ─────┴───── apply_batch_sync
                       │                       ▲
                       ▼                       │
   ┌────────────────────────────────┐    ┌─────────────────────┐
   │ CarlaNet.Map                   │    │ CarlaNet.TrafficMgr │
   │                                │    │                     │
   │ OpenDriveParser                │    │  TrafficManager     │  ← public facade
   │   ├─ GeoReferenceParser        │    │  (port→instance     │    (Section 3)
   │   ├─ RoadParser                │    │   singleton map)    │
   │   ├─ JunctionParser            │    │      │              │
   │   ├─ GeometryParser            │    │      │ owns         │
   │   ├─ LaneParser                │    │      ▼              │
   │   ├─ ProfilesParser            │    │  TrafficManagerLocal│  (Section 4)
   │   ├─ SignalParser              │    │  ┌────────────────┐ │
   │   ├─ ObjectParser              │    │  │ tick worker    │ │
   │   ├─ ControllerParser          │    │  │  thread        │ │
   │   └─ TrafficGroupParser        │    │  └────┬───────────┘ │
   │       │                        │    │       │             │
   │       ▼ MapBuilder.Build       │    │       │             │
   │                                │    │       ▼             │
   │ road::Map                      │◄───┤  ALSM (Section 5)   │
   │   ├─ MapData                   │    │   ├─ identify spawn/destroy
   │   │   ├─ unordered_map<RoadId, │    │   ├─ push state→SimulationState
   │   │   │     Road>              │    │   └─ vehicle eviction
   │   │   ├─ unordered_map<JuncId, │    │       │             │
   │   │   │     Junction>          │    │       │             │
   │   │   ├─ Signals               │    │       ▼  per-actor loop:
   │   │   └─ GeoReference          │    │  ┌───────────────┐  │
   │   ├─ Road                      │    │  │ Localization  │──┼──► waypoint buffer
   │   │   ├─ InformationSet (RoadI │    │  └───────┬───────┘  │     (BufferMap)
   │   │   │   nfo records)         │    │          │          │
   │   │   ├─ LaneSectionMap        │    │          ▼          │
   │   │   │   └─ LaneSection       │    │  ┌───────────────┐  │
   │   │   │       └─ Lane          │    │  │ Collision     │──┼──► CollisionFrame
   │   │   ├─ next/prev Roads       │    │  └───────┬───────┘  │
   │   │   └─ geometry: Line/Arc/   │    │          │          │
   │   │       Spiral/Poly3/ParamP3 │    │          ▼          │
   │   ├─ Junction                  │    │  ┌───────────────┐  │
   │   │   ├─ Connections           │    │  │ TrafficLight  │──┼──► TLFrame
   │   │   └─ Conflicts             │    │  └───────┬───────┘  │
   │   ├─ R-tree<Waypoint>          │    │          │          │
   │   ├─ GetClosest/Get/Next/Prev  │    │          ▼          │
   │   ├─ GenerateTopology          │    │  ┌───────────────┐  │
   │   └─ GenerateWaypoints         │    │  │ MotionPlan    │──┼──► ControlFrame
   │                                │    │  │   uses PID    │  │  (rpc::Command[])
   │ InMemoryMap   (TM's discrete   │◄───┤  └───────┬───────┘  │
   │   waypoint graph)              │    │          ▼          │
   │   ├─ dense_topology (NodeList) │    │  ┌───────────────┐  │
   │   ├─ R-tree spatial index      │    │  │ VehicleLight  │──┼──► ControlFrame
   │   ├─ SimpleWaypoint nodes:     │    │  └───────────────┘  │     (lights)
   │   │   next/prev/left/right,    │    │                     │
   │   │   road_option, geo_grid_id │    │  TrackTraffic       │
   │   ├─ GetWaypoint(loc)          │    │  Parameters         │
   │   └─ GetWaypointsInDelta       │    │  SimulationState    │
   └────────────────────────────────┘    └─────────────────────┘
                                                    │
                                                    │ network-mode only (rare)
                                                    ▼
                                          TrafficManagerRemote
                                          (RPC to remote TM @ tm_port)
```

### Strict layering

`CarlaNet.Types` (existing) ← `CarlaNet.Map` (new) ← `CarlaNet.TrafficManager` (new)

`CarlaNet.Transport` already references `CarlaNet.Types`. The Python shim
(`CarlaNet.Python` + `carlanet/__init__.py`) will reference all three.

---

## 3. Public API Surface

Source of truth: **`carla/PythonAPI/carla/src/TrafficManager.cpp`** (115 LoC, 30
public methods). Every method below MUST exist on `CarlaNet.TrafficManager.TrafficManager`
with matching semantics. Method names follow .NET PascalCase; the Python shim
re-exports them under the snake_case identifiers the Python API uses (third column).

| C++ method (TrafficManager.h) | Python binding name | C# method (proposed) | Args | Return |
|---|---|---|---|---|
| `Port()` | `get_port` | `Port` (property) | — | `ushort` |
| `SetPercentageSpeedDifference` | `vehicle_percentage_speed_difference` | `SetPercentageSpeedDifference(actor, perc)` | Actor, float | void |
| `SetLaneOffset` | `vehicle_lane_offset` | `SetLaneOffset` | Actor, float | void |
| `SetDesiredSpeed` | `set_desired_speed` | `SetDesiredSpeed` | Actor, float | void |
| `SetGlobalPercentageSpeedDifference` | `global_percentage_speed_difference` | `SetGlobalPercentageSpeedDifference` | float | void |
| `SetGlobalLaneOffset` | `global_lane_offset` | `SetGlobalLaneOffset` | float | void |
| `SetUpdateVehicleLights` | `update_vehicle_lights` | `SetUpdateVehicleLights` | Actor, bool | void |
| `SetCollisionDetection` | `collision_detection` | `SetCollisionDetection` | Actor, Actor, bool | void |
| `SetForceLaneChange` | `force_lane_change` | `SetForceLaneChange` | Actor, bool (true=left) | void |
| `SetAutoLaneChange` | `auto_lane_change` | `SetAutoLaneChange` | Actor, bool | void |
| `SetDistanceToLeadingVehicle` | `distance_to_leading_vehicle` | `SetDistanceToLeadingVehicle` | Actor, float | void |
| `SetPercentageIgnoreWalkers` | `ignore_walkers_percentage` | `SetPercentageIgnoreWalkers` | Actor, float | void |
| `SetPercentageIgnoreVehicles` | `ignore_vehicles_percentage` | `SetPercentageIgnoreVehicles` | Actor, float | void |
| `SetPercentageRunningLight` | `ignore_lights_percentage` | `SetPercentageRunningLight` | Actor, float | void |
| `SetPercentageRunningSign` | `ignore_signs_percentage` | `SetPercentageRunningSign` | Actor, float | void |
| `SetGlobalDistanceToLeadingVehicle` | `set_global_distance_to_leading_vehicle` | `SetGlobalDistanceToLeadingVehicle` | float | void |
| `SetKeepSlowLanePercentage` | `keep_slow_lane_rule_percentage` | `SetKeepSlowLanePercentage` | Actor, float | void |
| `SetRandomLeftLaneChangePercentage` | `random_left_lanechange_percentage` | `SetRandomLeftLaneChangePercentage` | Actor, float | void |
| `SetRandomRightLaneChangePercentage` | `random_right_lanechange_percentage` | `SetRandomRightLaneChangePercentage` | Actor, float | void |
| `SetSynchronousMode` | `set_synchronous_mode` | `SetSynchronousMode` | bool | void |
| `SetHybridPhysicsMode` | `set_hybrid_physics_mode` | `SetHybridPhysicsMode` | bool | void |
| `SetHybridPhysicsRadius` | `set_hybrid_physics_radius` | `SetHybridPhysicsRadius` | float | void |
| `SetRandomDeviceSeed` | `set_random_device_seed` | `SetRandomDeviceSeed` | ulong | void |
| `SetOSMMode` | `set_osm_mode` | `SetOsmMode` | bool | void |
| `SetCustomPath` (Path = `List<Location>`) | `set_path` | `SetCustomPath` | Actor, IReadOnlyList<Location>, bool empty_buffer=true | void |
| `SetImportedRoute` (Route = `List<byte>` of RoadOption) | `set_route` | `SetImportedRoute` | Actor, IReadOnlyList<byte>, bool empty_buffer=true | void |
| `SetRespawnDormantVehicles` | `set_respawn_dormant_vehicles` | `SetRespawnDormantVehicles` | bool | void |
| `SetBoundariesRespawnDormantVehicles` | `set_boundaries_respawn_dormant_vehicles` | `SetBoundariesRespawnDormantVehicles` | float, float | void |
| `GetNextAction` | `get_next_action` | `GetNextAction` | ActorId | (RoadOption, Waypoint) |
| `GetActionBuffer` | `get_all_actions` | `GetActionBuffer` | ActorId | List<(RoadOption, Waypoint)> |
| `ShutDown` | `shut_down` | `ShutDown` | — | void |
| `SynchronousTick` | — | `SynchronousTick` | — | bool. Driven internally by the worker thread when running sync mode — Python users don't call it. |

**`RoadOption` enum** (SimpleWaypoint.h:25–34): `Void=0, Left=1, Right=2,
Straight=3, LaneFollow=4, ChangeLaneLeft=5, ChangeLaneRight=6, RoadEnd=7`. Already
needed by `set_route` and `get_next_action`. Place in `CarlaNet.Map`.

**`Waypoint` (Python-facing)**: structurally `{ road_id: uint32, section_id:
uint32, lane_id: int32, s: double }` plus computed `transform`. Expose via the
shim as `client.Waypoint`. The internal `SimpleWaypoint` (the dense-graph node)
is **not** the same as the user-facing `Waypoint`; keep them separate (§5.2).

**Defaults to match upstream** (TrafficManager.cpp:139–143):
- `LONGITUDINAL_PARAM = {12.0, 0.05, 0.02}`
- `LONGITUDINAL_HIGHWAY_PARAM = {20.0, 0.05, 0.01}`
- `LATERAL_PARAM = {8.0, 0.04, 0.16}`
- `LATERAL_HIGHWAY_PARAM = {4.0, 0.04, 0.08}`
- `INITIAL_PERCENTAGE_SPEED_DIFFERENCE = 0.0f`

---

## 4. Threading Model

### Upstream (verified at TrafficManagerLocal.cpp)

- **One** dedicated worker thread is spun in `TrafficManagerLocal::Start()` at
  line 137: `worker_thread = std::make_unique<std::thread>(&TrafficManagerLocal::Run, this);`
- The whole stage pipeline runs **sequentially** in that one thread per tick
  (lines 222–234). All 6 stages × N vehicles are tight `for` loops:
  ```
  for (i = 0; i < N; ++i) localization_stage.Update(i);
  for (i = 0; i < N; ++i) collision_stage.Update(i);
  collision_stage.ClearCycleCache();
  vehicle_light_stage.UpdateWorldInfo();
  for (i = 0; i < N; ++i) {
      traffic_light_stage.Update(i);
      motion_plan_stage.Update(i);
      vehicle_light_stage.Update(i);
  }
  ```
  No parallelism inside a tick. There is **no `std::thread::hardware_concurrency()`**,
  no `std::async`, no fork/join. Comment at TrafficManagerLocal.h:111 confirms
  this: *"Single worker thread for sequential execution of sub-components."*
- Sync between user code (Python) and the worker:
  - `std::atomic<bool> step_begin`, `std::atomic<bool> step_end`
  - `std::condition_variable step_begin_trigger`, `step_end_trigger`
  - `std::mutex step_execution_mutex`
- In **synchronous mode**: the worker `wait(step_begin)`; `SynchronousTick()` sets
  `step_begin=true` + `notify_one()`; after the batch ApplyBatchSync completes
  the worker sets `step_end=true` + `notify_one()`; `SynchronousTick()` returns.
- In **asynchronous mode**: the worker loops freely. To prevent re-processing the
  same server snapshot it polls `world.GetSnapshot().GetTimestamp().frame` and
  skips if `frame == last_frame` (line 175–180). If `hybrid_physics_mode` is on,
  it `sleep_for(0.05s - elapsed)` between ticks (line 163–171).
- A **separate** mutex `registration_mutex` (line 118) prevents
  `RegisterVehicles`/`UnregisterVehicles` calls (from any RPC server thread or
  Python thread) from racing with the worker's frame array re-allocation.
- The `TrafficManagerServer` instance (if running) creates **a third set** of
  threads (rpclib's `async_run`) that proxy remote calls into the
  `TrafficManagerLocal` via the `TrafficManagerBase*` interface.

### C# port plan

- **One** dedicated `Thread` (not a pool thread) — set `IsBackground = true`,
  give it a name (`"CarlaNet.TM.Worker"`), and `Thread.Start(Run)`. We don't use
  `Task` because we don't want the work item to migrate threads or to occupy a
  thread-pool slot for hours.
- Replace `std::condition_variable` with a pair of `SemaphoreSlim` (one for
  step-begin, one for step-end) or a `ManualResetEventSlim` pair — semantically
  equivalent, simpler API. The `step_begin/step_end` atomics become the
  `Set()/Wait()` calls.
- Use `lock` (Monitor) on a private `_registrationGate` object instead of
  `std::mutex`.
- All `AtomicMap<K,V>` instances become **`ConcurrentDictionary<K,V>`** — they
  permit lock-free reads, which is what the stages need (every stage's
  `parameters.GetXxx(actor_id)` is a per-frame read).
- `AtomicActorSet` becomes a small wrapper around `ConcurrentDictionary<ActorId,
  Actor>` plus an `int` state counter (use `Interlocked.Increment`).
- **`Channels` are NOT needed**. There is no producer/consumer pattern in
  upstream — the stages share data via plain `List<T>`s (frames) and a single
  worker thread mutates them in turn. The only synchronization is the user
  → worker step trigger (above) and the registration mutex.
- **No `await`/`async` inside the tick.** RPC calls into CarlaNet.Transport
  (only one per tick: `ApplyBatchSync`) must use `.GetAwaiter().GetResult()` or
  a synchronous variant. The worker thread blocking on a TCP round-trip is fine
  — upstream does the same.

---

## 5. Stage-by-Stage Contracts

For each stage: inputs, outputs, internal state, key algorithms, hot-path note.
"Easy" = pure compute over `SimulationState` + `BufferMap`. "Hard" = touches
`InMemoryMap` or needs RPC introspection of TL state.

### 5.1 ALSM — Agent Lifecycle & State Management

- **Files**: ALSM.cpp (396), ALSM.h (116)
- **Difficulty**: **Hard** (needs `world.GetActors()` RPC every tick — the
  biggest per-tick RPC cost; needs hero-actor role-name attribute lookup)
- **Inputs**: `cc::World&` (RPC to server), `AtomicActorSet registered_vehicles`,
  `Parameters`, `LocalMapPtr local_map`
- **Outputs**: writes `SimulationState`, `BufferMap`, `track_traffic`. Calls
  `RemoveActor` on the four downstream stages when an actor disappears
- **State**: `unordered_map<ActorId, ActorPtr> unregistered_actors`,
  `IdleTimeMap idle_time`, `hero_actors`, `has_physics_enabled`,
  `elapsed_last_actor_destruction`
- **Algorithm** (Update() — ALSM.cpp:45–113):
  1. `world.GetSnapshot()` → `current_timestamp`. **RPC #1 per tick.**
  2. `world.GetActors()` → `ActorList`. **RPC #2 per tick.** Returns every actor
     in the world (vehicles, walkers, sensors, traffic lights).
  3. Diff against `registered_vehicles` + `unregistered_actors` to find created/destroyed.
  4. Cascade destroyed actor IDs to `localization_stage.RemoveActor`, etc.
  5. Find hero actors by scanning attributes for `role_name == "hero"`.
  6. For each registered vehicle: fetch transform/velocity/angular-velocity
     (already in the actor snapshot — no extra RPC), apply hybrid-physics
     dormant logic if it's beyond `physics_radius` from any hero.
  7. Update idle time; if a vehicle has been stuck > 90s (`BLOCKED_TIME_THRESHOLD`)
     mark for destruction; if marked-for-removal AND OSM mode, call
     `registered_vehicles.Destroy(actor_id)` (which RPCs `actor.Destroy()`).
- **Hot-path note**: `world.GetActors()` returns ALL actors. With 50 vehicles
  + 10 walkers + traffic lights + sensors this is ~100 entries; the RPC
  round-trip is ~1–3 ms on localhost. **This is the single biggest per-tick
  cost** and matches upstream's overhead.
- **C# port**: Re-use `CarlaNet.Transport.CarlaClient.GetActorsAsync()`. Cache
  the actor snapshot — do not re-RPC inside the stage.

### 5.2 LocalizationStage

- **Files**: LocalizationStage.cpp (680), LocalizationStage.h (92), LocalizationUtils.cpp (97)
- **Difficulty**: **Hard** (touches `InMemoryMap` every tick for every vehicle;
  contains the lane-change decision logic; handles custom-path/route imports)
- **Inputs**: `SimulationState`, `local_map (InMemoryMap)`, `Parameters`,
  `random_device`
- **Outputs**: `BufferMap buffer_map` (per-actor `deque<SimpleWaypointPtr>` of
  upcoming waypoints), `LocalizationFrame output_array` (per-vehicle: junction
  end-point, safe-point, is-at-junction-entrance)
- **State**: `LaneChangeSWptMap last_lane_change_swpt`, `vehicles_at_junction`,
  `vehicles_at_junction_entrance`
- **Algorithm** (Update — LocalizationStage.cpp:36+, ~680 LoC of dense logic):
  1. Compute `horizon_length = max(speed × HORIZON_RATE, MINIMUM_HORIZON_LENGTH)`
     — switches to `HIGH_SPEED_HORIZON_RATE` above highway speed (60 km/h).
  2. If the buffer is empty OR vehicle has drifted > `MAX_START_DISTANCE` (20 m)
     from front of buffer, rebuild by calling `local_map->GetWaypoint(loc)`.
  3. Pop waypoints already behind the vehicle (negative dot product between
     heading and waypoint direction).
  4. Detect junction entrance via `look_ahead_point->CheckJunction()` vs
     `front_waypoint->CheckJunction()` (note the Town03 roundabout fudge at
     line 92).
  5. Extend the buffer until cumulative length ≥ `horizon_length`. When the
     current waypoint has multiple `next_waypoints` (intersection), pick using
     `RoadOption` heuristics or imported route.
  6. **Lane changes**: `AssignLaneChange` is called probabilistically based on
     `keep_slow_lane`, `random_left/right`, `auto_lane_change` parameters and
     forced lane changes. It threads new lane-changed waypoints into the buffer.
  7. **Custom path / route**: if `parameters.GetUploadPath(actor_id)`, call
     `ImportPath` which interleaves user-supplied `Location`s with waypoints
     from `local_map` until horizon is filled.
  8. Call `track_traffic.UpdateGridPosition(actor_id, buffer)` so collision
     stage can find overlapping vehicles.
- **C# port notes**: `Buffer = deque<SimpleWaypointPtr>` → `LinkedList<SimpleWaypoint>`
  is the obvious choice but **slow**. Better: implement a custom
  `WaypointDeque` ring buffer over `SimpleWaypoint[]` since the buffer rarely
  exceeds 50 entries and we pop from front + push to back only. Avoids LinkedList
  node allocations on every tick.

### 5.3 CollisionStage

- **Files**: CollisionStage.cpp (428), CollisionStage.h (113)
- **Difficulty**: **Hard** (uses `boost::geometry` polygon intersection — must
  port to managed polygon math)
- **Inputs**: `SimulationState`, `BufferMap`, `TrackTraffic`, `Parameters`
- **Outputs**: `CollisionFrame output_array` (per-vehicle: `{
  available_distance_margin, hazard_actor_id, hazard:bool }`)
- **State**: `CollisionLockMap collision_locks` (track which vehicle we're
  yielding to so we don't oscillate), `geometry_cache` (per-tick pair cache),
  `geodesic_boundary_map` (per-tick boundary polygon cache)
- **Algorithm** (Update — CollisionStage.cpp:34+):
  1. For ego, compute `collision_radius_square = (COLLISION_RADIUS_RATE × v +
     COLLISION_RADIUS_MIN)²`; if `v < 2 m/s`, use `COLLISION_RADIUS_STOP + length`.
  2. Get `overlapping_actors = track_traffic.GetOverlappingVehicles(ego_id)`.
  3. For each overlapper within `collision_radius`:
     - Build a `LocationVector` *geodesic boundary*: a polygon stretched along
       the planned path of each vehicle (`GetGeodesicBoundary`).
     - Intersect using `boost::geometry::intersection`. If intersection area > 0
       and the other vehicle is *ahead* (positive dot product), report a hazard
       with `available_distance_margin = bbox-distance - safety-margin`.
  4. Pedestrians get `WALKER_TIME_EXTENSION` extra lookahead.
  5. `collision_locks` provides hysteresis: once locked onto a leader, keep
     yielding until distance opens up past `LOCKING_DISTANCE_PADDING`.
- **C# port**: Replace `boost::geometry::polygon` with a small custom
  2D polygon class. The polygons are convex strips (4–6 vertices), so SAT
  (Separating Axis Theorem) intersection is faster than a general algorithm.
  **Don't pull in `System.Drawing` or `Clipper2`** — overkill and they allocate.

### 5.4 TrafficLightStage

- **Files**: TrafficLightStage.cpp (194), TrafficLightStage.h (64)
- **Difficulty**: **Medium** (needs `SimpleWaypoint::GetWaypoint()->GetJunctionId()`
  + a way to look up TL state per junction)
- **Inputs**: `SimulationState`, `BufferMap`, `Parameters`, `cc::World&`
- **Outputs**: `TLFrame output_array` — a `vector<bool>`, one bool per vehicle
  ("should I brake for a TL/stop sign?")
- **State**: `entering_vehicles_map[junction_id] = deque<actor_id>` (priority
  queue for non-signalized junctions), `vehicle_last_junction`, `vehicle_stop_time`
- **Algorithm** (Update — TrafficLightStage.cpp):
  1. Look up ego's TL state via `simulation_state.GetTLS(actor_id)` (populated
     by ALSM from `actor.GetTrafficLightState()`).
  2. If TL is **Red** and ego is within braking distance of it → hazard = true.
  3. Apply `perc_run_traffic_light` chance to ignore.
  4. **Non-signalized junction handling** (the more interesting half):
     - Find the next junction id on ego's buffer (`HandleNonSignalisedJunction`).
     - Append ego to `entering_vehicles_map[junction_id]`.
     - Hazard = true unless ego is at the head of the deque.
     - Stop-sign: hazard stays true until `current_timestamp -
       vehicle_stop_time[actor_id] > MINIMUM_STOP_TIME (2s)`.
- **C# port**: Easy. Use `Dictionary<int, Queue<ActorId>>`.

### 5.5 MotionPlanStage

- **Files**: MotionPlanStage.cpp (479), MotionPlanStage.h (96), PIDController.h (63)
- **Difficulty**: **Hard** (the most complex stage logic; handles hybrid-physics
  teleportation; landmark-based slowdown; signed-circle-radius curvature; ...)
- **Inputs**: All three earlier frames (LocalizationFrame, CollisionFrame,
  TLFrame), `SimulationState`, `BufferMap`, `Parameters`, `local_map`, four
  PID parameter vectors
- **Outputs**: `ControlFrame output_array` — `vector<carla::rpc::Command>` with
  one `ApplyVehicleControl` per registered vehicle (+ optionally
  `ApplyTransform` for teleporting dormant vehicles in respawn mode)
- **State**: `unordered_map<ActorId, StateEntry> pid_state_map` (previous PID
  state — required because PID needs ∂error/∂t), `teleportation_instance`
- **Algorithm**:
  1. **Dormant + respawn mode** (line 86–~130): if vehicle is far from hero
     and `parameters.GetRespawnDormantVehicles()`, sample a random waypoint
     within `[lower_bound, upper_bound]` of hero, teleport.
  2. **Target velocity**: start with `parameters.GetVehicleTargetVelocity()`,
     reduce for:
     - Upcoming turn (3-point-circle curvature — `GetTurnTargetVelocity`)
     - Approaching TL/stop sign (`GetLandmarkTargetVelocity`)
     - Collision lead vehicle (`CollisionHandling` returns relative-approach
       speed bound)
     - Lane offset clamp
  3. Compute `velocity_deviation = target_v - current_v`,
     `angular_deviation = signed angle between heading and target waypoint
     direction`.
  4. Call `PID::RunStep(current_state, previous_state, longitudinal_params,
     lateral_params)` → `(throttle, brake, steer)`. Choose **highway** params
     if `speed > HIGHWAY_SPEED (60 km/h)`, urban otherwise.
  5. Pack a `rpc::Command::ApplyVehicleControl{actor_id, VehicleControl{...}}`
     into `output_array[index]`.
- **C# port**: PID is a pure inline function (~20 LoC) — direct port. The
  curvature math (`GetThreePointCircleRadius`) is 5 LoC. The teleportation
  logic needs `InMemoryMap.GetWaypointsInDelta`.

### 5.6 VehicleLightStage

- **Files**: VehicleLightStage.cpp (160), VehicleLightStage.h (46)
- **Difficulty**: **Easy**
- **Inputs**: `BufferMap`, `Parameters`, `cc::World&` (to fetch weather +
  current light states once per tick via `UpdateWorldInfo`)
- **Outputs**: appends `ApplyVehicleLightState` commands to `ControlFrame` for
  vehicles whose computed lights differ from current state
- **Algorithm**: based on `WeatherParameters.sun_altitude_angle`,
  `precipitation`, `fog_density`, plus the look-ahead waypoint's `RoadOption`
  to set blinkers for upcoming turns. Skip vehicle if `update_vehicle_lights`
  param is false for it.

### 5.7 PIDController

- **File**: PIDController.h (63, header-only)
- **Difficulty**: **Trivial**. Direct copy — 25 lines of arithmetic.

### Summary

| Stage | LoC (cpp+h) | Difficulty | Touches `InMemoryMap`? | Touches RPC? |
|-------|------:|---|---|---|
| ALSM | 512 | Hard | yes (only on respawn) | yes (GetSnapshot, GetActors, Destroy) |
| LocalizationStage | 772 | Hard | yes (GetWaypoint, next/prev) | no |
| CollisionStage | 541 | Hard | no | no |
| TrafficLightStage | 258 | Medium | no | no |
| MotionPlanStage | 575 | Hard | yes (GetWaypointsInDelta for respawn) | no |
| VehicleLightStage | 206 | Easy | no | yes (GetWeather once/tick) |
| PIDController | 63 | Trivial | no | no |

---

## 6. OpenDRIVE Port Scope

### In scope

**Parser (port the parsers, but use `XDocument` instead of pugixml)**:
- `OpenDriveParser` — orchestrator (52 LoC)
- `GeoReferenceParser` — PROJ string → lat/lon origin
- `RoadParser` — road element + link/predecessor/successor
- `JunctionParser` — junctions and connections
- `GeometryParser` — Line/Arc/Spiral/Poly3/ParamPoly3 emission to MapBuilder
- `LaneParser` — laneSections, lanes, widths, marks
- `ProfilesParser` — elevation + lateral profiles (CubicPolynomial coefficients)
- `SignalParser` — traffic-light signal positions + controller refs
- `ObjectParser` — crosswalks (some scripts use these)
- `ControllerParser` — `<controller>` → traffic-light groups
- `TrafficGroupParser` — `userData` extension

**Road graph (`carla/road/`)**:
- `Map` (minus mesh generation — strip out ~600 LoC)
- `MapData`, `MapBuilder`
- `Road`, `LaneSection`, `Lane`, `LaneSectionMap`
- `Junction`, `Signal`, `SignalType`, `Controller`
- `InformationSet`, `RoadElementSet`, `RoadInfoIterator`, `RoadInfoVisitor`
- All `RoadInfo*` record types in `element/` (16 small files, polymorphic
  records tagged by type)
- `element/Geometry.h/.cpp` — **the five curve primitives** (~600 LoC, the
  hardest C# port: Spiral needs Fresnel integrals via Clothoid approximation)
- `element/Waypoint.h/.cpp` — the POD value type (already trivial)
- `RoadTypes.h` — typedefs

**Geometric primitives needed by `Geometry.cpp`**:
- 2D rotation, vector math (already in `CarlaNet.Types.Geom`)
- `geom::CubicPolynomial` — small class, not yet in CarlaNet. Add to `CarlaNet.Map`
- `geom::Math::DistanceSegmentToPoint`, `DistanceArcToPoint`, `Math::Clamp` —
  add to `CarlaNet.Map.Geom.MathExtensions`
- `SegmentCloudRtree` (RTree of segment+payload) — `Map.cpp` r-tree.
  **No native .NET RTree.** Options: (a) port the boost::geometry rstar
  algorithm (~400 LoC), (b) use the open-source `RBush.NET` package, (c) ship
  a simple uniform-grid spatial hash. Recommendation: **uniform grid hash**
  (~80 LoC, equivalent perf at small/medium map sizes). For Town03/Town10
  with ~3k waypoints, an RTree is overkill.

### Out of scope (do NOT port)

- `MeshFactory.cpp/.h` (1,328 LoC) — generates 3D road meshes. Server-only.
- `LaneCrossingCalculator.cpp/.h` (143 LoC) — used by `client::Map::CalculateCrossedLanes`
  which is a Python-API utility, not needed for TM.
- `road::object::RepeatRecord` — props metadata
- `Deformation.h` — terrain Z deformation hook
- `rpc::OpendriveGenerationParameters` and `GenerateChunkedMesh` etc.

### Why these are needed by the TM specifically

The TM only really uses (verified from `InMemoryMap.h` includes + `SetUp()`):
- `Map::GenerateTopology()` — get the sparse waypoint graph
- `Map::GenerateWaypoints(approx_distance)` — populate dense interpolation
- `Map::GetClosestWaypointOnRoad(loc)` — when a vehicle is freshly spawned
- `Map::GetNext(wp, distance)`, `GetPrevious`, `GetLeft`, `GetRight` — used
  during `SetUp()` to build SimpleWaypoint's `next/prev/left/right`
- `Map::GetJunctionId(road_id)`, `IsJunction(road_id)` — for `SimpleWaypoint::is_junction`
- `Map::ComputeTransform(waypoint)` — translates `Waypoint` POD → world transform

The TM does NOT use `CalculateCrossedLanes`, `GetSignalsInDistance`,
`GenerateMesh`, `GetAllCrosswalkZones`. We can drop those from the C# port and
add later if some external script needs them.

### LoC budget for `CarlaNet.Map` (C# estimate)

| Component | C++ LoC | Est. C# LoC |
|---|---:|---:|
| OpenDriveParser + 10 parsers | 1,845 | ~1,400 (XDocument is terser) |
| Road graph types (Map sans mesh, MapBuilder, Road, Lane*, Junction, Signal, InformationSet) | ~3,800 | ~3,000 |
| element/Geometry + helpers (Spiral, Poly3, ParamPoly3) | 586 | ~550 |
| element/RoadInfo* records | ~1,200 | ~900 |
| element/Waypoint + RoadTypes | 89 | 60 |
| InMemoryMap + SimpleWaypoint + CachedSimpleWaypoint | 1,033 | ~900 |
| TrackTraffic | 260 | ~200 |
| Spatial index (replacement for boost rtree) | n/a | ~150 |
| **Total CarlaNet.Map** | ~8,800 | **~7,200 LoC C#** |

---

## 7. How the TM Gets the OpenDRIVE XML

**Verified path:**

1. `TrafficManagerLocal::SetupLocalMap()` (TrafficManagerLocal.cpp:116):
   ```cpp
   const carla::SharedPtr<const cc::Map> world_map = world.GetMap();
   local_map = std::make_shared<InMemoryMap>(world_map);
   ...
   local_map->SetUp();   // build dense waypoint graph from cc::Map
   ```
2. `cc::World::GetMap()` →  `carla::client::detail::Simulator::GetCurrentMap()` →
   triggers `Client::GetMapData()` (Client.cpp:209):
   ```cpp
   std::string Client::GetMapData() const {
     return _pimpl->CallAndWait<std::string>("get_map_data");
   }
   ```
3. The server returns a single ~300 KB OpenDRIVE XML string. Simulator parses
   it once via `OpenDriveParser::Load(xml)` and caches the result. Subsequent
   `world.GetMap()` calls return the cached `carla::client::Map` wrapper.

**CarlaNet already has this RPC wired**: `CarlaClient.cs:118`:
```csharp
public Task<string> GetMapDataAsync() => _rpc.CallAsync<string>("get_map_data");
```

There's also a TM-specific cache file mechanism via
`episode_proxy.Lock()->GetRequiredFiles("TM")` (TrafficManagerLocal.cpp:120),
which lets the server hand pre-cooked `.bin` waypoint graphs to the TM to skip
the expensive `SetUp()` step. **Recommendation: skip this optimization in the
initial port** — `SetUp()` on a Town map takes ~500 ms; do it once at TM
construction and live with it.

**Where the C# port should fetch the XML**: when
`CarlaNet.TrafficManager.TrafficManager` is constructed (or lazily, on first
`RegisterVehicles` call), call `CarlaClient.GetMapDataAsync()`, hand the string
to `CarlaNet.Map.OpenDriveParser.Load(string)`, and build the `InMemoryMap`.
The map name (used by `LocalizationStage` for the Town03 roundabout exception
on line 92) is available from the same XML's `<header name="...">` attribute.

---

## 8. C# Project Layout

### `CarlaNet.Map/`

```
CarlaNet.Map.csproj          (net10.0; references CarlaNet.Types)
src/
  Geom/
    CubicPolynomial.cs            (geom::CubicPolynomial)
    MathExtensions.cs             (DistanceSegmentToPoint, DistanceArcToPoint, ...)
    SpatialGrid.cs                (replacement for boost rstar rtree)
  OpenDrive/
    OpenDriveParser.cs            (entrypoint: Load(string) -> Map?)
    Parsers/
      ControllerParser.cs
      GeoReferenceParser.cs
      GeometryParser.cs
      JunctionParser.cs
      LaneParser.cs
      ObjectParser.cs
      ProfilesParser.cs
      RoadParser.cs
      SignalParser.cs
      TrafficGroupParser.cs
  Road/
    Map.cs                        (road::Map, minus mesh generation)
    MapBuilder.cs
    MapData.cs
    Road.cs
    Lane.cs
    LaneSection.cs
    LaneSectionMap.cs
    Junction.cs
    Signal.cs
    SignalType.cs
    Controller.cs
    InformationSet.cs
    RoadElementSet.cs
    RoadTypes.cs                  (RoadId, JuncId, LaneId, SectionId aliases)
    Element/
      Geometry.cs                 (Geometry abstract + 5 derived)
      LaneMarking.cs
      Waypoint.cs                 (the POD: { road_id, section_id, lane_id, s })
      RoadInfo.cs                 (base abstract)
      RoadInfoElevation.cs
      RoadInfoGeometry.cs
      RoadInfoLaneOffset.cs
      RoadInfoLaneWidth.cs
      RoadInfoSpeed.cs
      RoadInfoMarkRecord.cs
      RoadInfoMarkTypeLine.cs
      RoadInfoSignal.cs
      RoadInfoCrosswalk.cs
      RoadInfoLaneAccess.cs       (only fields needed; rest stubbed if unused)
      RoadInfoLaneBorder.cs
      RoadInfoLaneHeight.cs
      RoadInfoLaneMaterial.cs
      RoadInfoLaneRule.cs
      RoadInfoLaneVisibility.cs
      RoadInfoVisitor.cs
      RoadInfoIterator.cs
```

### `CarlaNet.TrafficManager/`

```
CarlaNet.TrafficManager.csproj   (net10.0; references CarlaNet.Map, CarlaNet.Transport, CarlaNet.Types)
src/
  TrafficManager.cs               (public facade, mirrors TrafficManager.h)
  TrafficManagerLocal.cpp         (orchestrator + worker thread)
  TrafficManagerRemote.cs         (wraps CarlaNet.Transport.TrafficManager.TrafficManagerClient)
  Constants.cs                    (port of Constants.h — all the tuning values)
  Parameters.cs                   (per-actor + global runtime knobs)
  RandomGenerator.cs              (wraps System.Random in a thread-safe way)
  SimulationState.cs              (per-tick kinematic snapshot)
  TrackTraffic.cs                 (waypoint occupancy)
  AtomicActorSet.cs               (ConcurrentDictionary wrapper)
  Map/
    InMemoryMap.cs                (dense waypoint graph)
    SimpleWaypoint.cs             (graph node)
    CachedSimpleWaypoint.cs       (binary serialization — only if needed)
    RoadOption.cs                 (enum)
  Stages/
    Stage.cs                      (base abstract)
    ALSM.cs
    LocalizationStage.cs
    CollisionStage.cs
    TrafficLightStage.cs
    MotionPlanStage.cs
    VehicleLightStage.cs
    PIDController.cs              (static class)
  DataStructures/
    LocalizationData.cs
    CollisionHazardData.cs
    StateEntry.cs
    ActuationSignal.cs
    Frames.cs                     (type aliases for LocalizationFrame, etc.)
    WaypointDeque.cs              (perf: ring-buffer for Buffer)
    Polygon2D.cs                  (SAT intersection for CollisionStage)
test/
  CarlaNet.Map.Tests/
    GeometryTests.cs              (verify Line/Arc/Spiral/Poly3/ParamPoly3 PosFromDist against known coords)
    OpenDriveParserTests.cs       (parse Town01/Town03 fixtures)
    MapTests.cs                   (GetClosestWaypointOnRoad/GetNext smoke)
  CarlaNet.TrafficManager.Tests/
    InMemoryMapTests.cs
    LocalizationStageTests.cs
    CollisionStageTests.cs
    PIDControllerTests.cs
    EndToEndTests.cs              (drives 5 dummy vehicles for 60 ticks)
```

### Reference graph

```
CarlaNet.Types             <─── CarlaNet.Map
                           <─── CarlaNet.TrafficManager
                                  │
                                  ├──→ CarlaNet.Transport (for ApplyBatchSync, GetMapData)
                                  └──→ CarlaNet.Map
```

`CarlaNet.Transport` keeps its existing `TrafficManager/TrafficManagerClient.cs`
(the remote-mode RPC client) — that file stays where it is and gets called by
the new `TrafficManagerRemote.cs`.

---

## 9. Hot-Path / Perf Considerations

### Target verified

Upstream's worker loop comment + ALSM design implies the TM is designed to run
at the same fixed delta as the simulator: 0.05 s (20 Hz). With 50 vehicles, each
tick must complete in **≤ 50 ms** to keep up. Splitting that budget:
- `world.GetSnapshot()` + `world.GetActors()` RPC: ~5 ms
- ALSM: ~5 ms
- LocalizationStage: ~10 ms (50 × 0.2 ms each — buffer maintenance dominates)
- CollisionStage: ~15 ms (worst case quadratic: 50 × 5 overlappers × polygon
  intersection)
- TrafficLightStage: ~2 ms
- MotionPlanStage: ~6 ms (PID + curvature)
- VehicleLightStage: ~1 ms
- `ApplyBatchSync` RPC of 50 commands: ~5 ms
- Total: ~49 ms — tight. The user's 20–30 FPS target says they can tolerate
  occasional drops to 40 ms / 25 FPS.

### Per-frame allocations to avoid (top 5)

1. **Buffer (`deque<SimpleWaypointPtr>`) modifications**. In C# `LinkedList<T>`
   allocates a node per `AddLast`. **Use a custom `WaypointDeque` ring buffer
   over `SimpleWaypoint[]`** with `Count`, `EnqueueBack`, `DequeueFront`. No
   per-frame allocations.
2. **`LocationVector` (`vector<cg::Location>`) for geodesic boundary polygons**.
   Each tick allocates 50 vectors × ~6 Locations. Use `ArrayPool<Location>` or
   pre-size as `Location[]` with a sentinel length field. CollisionStage already
   caches these via `geodesic_boundary_map` per tick (cleared at end). **Reuse
   the same `Dictionary<ActorId, Location[]>` across ticks**; resize only on
   growth.
3. **`ControlFrame = vector<rpc::Command>`** is rebuilt every tick. Upstream
   reuses `control_frame` via `clear()` + `reserve(2N)` + `resize(N)`
   (TrafficManagerLocal.cpp:213–219). Replicate with `List<Command>.Clear()` +
   `EnsureCapacity(2N)` — `List<T>.Clear` keeps the backing array.
4. **`ActorList = SharedPtr<ActorList>` from `world.GetActors()`**: this is the
   one allocation that's unavoidable per tick (it's the RPC response). It's a
   `List<Actor>`. Don't double-iterate or `ToArray()` it — process once.
5. **PID `StateEntry` per vehicle**. Already `struct` in C++. **Make it a
   `readonly struct`** in C# and store in `Dictionary<ActorId, StateEntry>`.
   `Dictionary` does NOT box value-type values.

### Value-type recommendations

`readonly struct` (no allocation):
- `Waypoint` (the POD)
- `Location`, `Vector3D`, `Rotation`, `Transform` (already structs in CarlaNet.Types)
- `LocalizationData` — pack as struct (currently `SimpleWaypointPtr` fields ⇒
  C# will hold `SimpleWaypoint?` references but the struct itself doesn't allocate)
- `CollisionHazardData` (3 fields, tiny)
- `StateEntry` (4 fields)
- `ActuationSignal` (3 floats)
- `KinematicState` (4 cg-types + 2 bools — boundary case; could go either way.
  Lean struct since we hold ~50–100 of these in a Dictionary.)
- `DirectedPoint` (location + tangent + pitch)
- `SegmentId` tuple

**Reference type (class)**:
- `SimpleWaypoint` — has back-references via `next/prev/left/right` lists. Class.
- `Road`, `Lane`, `LaneSection`, `Junction` — graph nodes with mutual references. Class.
- `InMemoryMap`, `Map` — top-level containers. Class.

### `Span<T>` / `Memory<T>` opportunities

- **Curve geometry**: `Geometry.PosFromDist(s)` is called inside `Map.GetNext()`
  during dense topology generation — that's a ~100k-call loop at startup.
  Pass intermediate computations on the stack via `Span<float>` where possible.
- **PID step**: takes two `vector<float>` of params. C# port takes
  `ReadOnlySpan<float>` — avoid array-bounds checks in the hot loop.
- **CollisionStage polygon vertex arrays**: build polygons on the stack as
  `Span<Location>` (`stackalloc Location[8]`) for the SAT intersection.

### Where parallelism could help (but upstream doesn't)

The `vehicle_id_list` for-loops in `Run()` are embarrassingly parallel **only
within a stage** (no two `LocalizationStage::Update(i)` calls share state — they
mutate different `BufferMap` entries). However:
- Switching to `Parallel.For` adds ~50–100 μs overhead per stage (work
  partitioning, sync barrier). With N=50 and ~0.2 ms per item, parallelism
  is a wash or worse.
- **Recommendation: stay sequential** to match upstream and avoid race risks.
  Re-evaluate only if N > 100.

### Hybrid-mode considerations

When `hybrid_physics_mode=true`, vehicles far from the hero are *teleported*
via `ApplyTransform` rather than physics-simulated. This halves the server-side
physics load. The TM side still runs all 6 stages per dormant vehicle. So
hybrid mode does **not** reduce the TM's CPU cost — make sure documentation
calls this out.

---

## 10. Python Shim Integration Plan

### Current state (verified `carlanet/__init__.py:825-1079`)

```python
class _NoOpTrafficManager:
    """Returned by Client.get_trafficmanager() when no TM server is running.
    Lets scripts that only use the TM for ambient AI continue without crashing.
    Every method is a no-op; getters return sensible defaults.
```

`Client.get_trafficmanager(port)` currently:
1. Tries `self._inner.GetTrafficManager(port)` — a `CarlaNet.Transport`
   factory that returns either a `TrafficManager` wrapper around a *remote*
   `TrafficManagerClient`, or throws.
2. On any exception, returns `_NoOpTrafficManager`.

### After the port

`Client.get_trafficmanager(port)` becomes:

```python
def get_trafficmanager(self, port: int = 8000):
    # 1. Try remote (existing behavior — pre-existing TM in the world)
    try:
        if self._inner.IsTrafficManagerRunning(port):
            remote = self._inner.GetTrafficManagerRemote(port)
            return TrafficManager(remote, port)
    except Exception:
        pass

    # 2. Fall back to creating a local TM in this process
    map_xml = _sync(self._inner.GetMapDataAsync())   # ~300 KB string
    tm_local = self._inner.CreateTrafficManagerLocal(map_xml, port)
    self._inner.RegisterTrafficManagerRunning(port)  # advertise to other clients
    return TrafficManager(tm_local, port)
```

Where on the C# side `CreateTrafficManagerLocal` does:

1. `var parsedMap = OpenDriveParser.Load(mapXml)` (one-time, ~50 ms)
2. `var inMemoryMap = new InMemoryMap(parsedMap)` + `inMemoryMap.SetUp()` (~300–500 ms)
3. `var tm = new TrafficManagerLocal(carlaClient, inMemoryMap, port, defaultPidParams)`
4. `tm.Start()` — spins the worker thread
5. Returns `tm`

### How `apply_batch_sync` interacts with the TM

`generate_traffic.py:190-193`:
```python
batch.append(
    SpawnActor(blueprint, transform)
       .then(SetAutopilot(FutureActor, True, traffic_manager.get_port()))
)
for response in client.apply_batch_sync(batch, synchronous_master):
    vehicles_list.append(response.actor_id)
```

`SetAutopilot(FutureActor, True, port)` is a server-side command — when the
spawned vehicle is created, the server itself calls back into the TM at
`port` via the TM's RPC server bindings (`TrafficManagerServer::register_vehicle`).

**This means the C# TM MUST run an RPC server on `port` (default 8000)** so the
CARLA server can reach in and call `register_vehicle`. CarlaNet already has
`CarlaNet.Transport.MsgPackRpc` — we need a **server-side** variant. The TM
server bindings are listed in `TrafficManagerServer.h:73-282` (32 bound methods).

**Implementer note**: building an msgpack-rpc *server* in C# is the largest
unknown in the integration. We have rpc-client code, but no server. Options:
- Port rpclib's server (it's small — maybe 500 LoC).
- Use `MessagePack.Server` (3rd-party Nuget). The rpc framing matches msgpack-rpc.
- Re-use the existing TCP code from `MsgPackRpcClient` and build a minimal
  server on top.

Alternatively, the simpler approach: when `SetAutopilot(actor, True, port)`
runs server-side, the server forwards to the TM's RPC server. If we **don't**
have an RPC server, autopilot via `apply_batch_sync` will fail. The
workaround: have the Python shim intercept `SetAutopilot(..., port)` calls and
*after* `apply_batch_sync` returns, explicitly call `tm.register_vehicle(actor)`
on the local TM. But that doesn't work for `FutureActor` (the chained command
form).

**Decision needed from product owner**: do we need full network parity (RPC
server on port 8000) or is "in-process TM only" acceptable for v1? In the
latter case, the Python shim rewrites `SetAutopilot(FutureActor, True, port)`
chains into a two-step: spawn → manually register. See risk register §12.

### Tick integration

In sync mode:
```python
world.tick()                          # advances the simulator one step
traffic_manager.synchronous_tick()    # wakes the TM worker, waits for it
```
Upstream's `world.tick()` does *not* drive the TM — they're independent. The
TM's `synchronous_tick()` is what unblocks the worker. **In `generate_traffic.py`
the user never explicitly calls `synchronous_tick`** — why does it work?
Because `set_synchronous_mode(True)` puts the TM in lockstep mode, and
**`world.tick()` on the server eventually triggers a `synchronous_tick` via
the server's `Tick()` static method on every registered TM port**. We need to
investigate (TrafficManager.cpp:62-67: `TrafficManager::Tick()` iterates
`_tm_map` and calls `SynchronousTick` on each). This static is called from
`Simulator::Tick()`. In CarlaNet we'll need the equivalent: `CarlaClient.Tick`
should invoke `TrafficManager.SynchronousTick` on every registered local TM
before returning.

---

## 11. Example-Script Call Sites

### `generate_traffic.py` — every TM method call

| Line | Call | Maps to | Exercises which stage |
|-----:|------|---------|------|
| 115 | `client.get_trafficmanager(args.tm_port)` | construct local TM | All — setup |
| 116 | `tm.set_global_distance_to_leading_vehicle(2.5)` | `SetGlobalDistanceToLeadingVehicle` | Parameters → CollisionStage |
| 118 | `tm.set_respawn_dormant_vehicles(True)` | `SetRespawnDormantVehicles` | Parameters → MotionPlanStage |
| 120-121 | `tm.set_hybrid_physics_mode(True)`, `set_hybrid_physics_radius(70.0)` | hybrid setters | ALSM (dormant logic) |
| 123 | `tm.set_random_device_seed(args.seed)` | `SetRandomDeviceSeed` | All random decisions |
| 127 | `tm.set_synchronous_mode(True)` | `SetSynchronousMode` | Worker thread |
| 191 | `SetAutopilot(FutureActor, True, tm.get_port())` | server-side → `register_vehicle` RPC | TM RPC server (§10) |
| 203 | `tm.update_vehicle_lights(actor, True)` | `SetUpdateVehicleLights` | VehicleLightStage |
| 290 | `tm.global_percentage_speed_difference(30.0)` | `SetGlobalPercentageSpeedDifference` | MotionPlanStage (target velocity) |

### `invertedai_traffic.py`

Imports `carla` and uses TM only for: `get_trafficmanager`, `set_synchronous_mode`,
`set_random_device_seed`. **Same surface as `generate_traffic.py`'s subset.**
This script's main value is being a second consumer for parity testing.

### `walker_ai_controller` (no TM involvement)

Walker AI is server-side. Not affected by the port.

### `behavior_agent.py` (carla/PythonAPI/carla/agents/)

Uses TM for **emergency-stop coordination** and **set_route**:
- `tm.set_route(vehicle, route_options)` — used to feed planned high-level
  decisions (turns) to the TM. Exercises `LocalizationStage::ImportRoute`.

### Verification checklist (drives agent #11)

- [ ] Run `generate_traffic.py -n 5 --asynch` against an in-process TM, observe
      vehicles drive for 60 s without crashing.
- [ ] Run `generate_traffic.py -n 30 -s 42 --hybrid` and confirm
      `set_random_device_seed` makes the run reproducible.
- [ ] Run sync mode `-n 50` and measure tick time; verify ≤ 50 ms/tick.
- [ ] Spawn one vehicle, call `tm.set_path(actor, [loc1, loc2, loc3], True)`,
      and verify it drives the path.
- [ ] Spawn 50 vehicles + 10 walkers, run for 5 min, ensure no allocations >
      a small steady-state baseline (GC observation).

---

## 12. Risk Register

| # | Risk | P | I | Mitigation |
|---|------|---|---|------------|
| 1 | OpenDRIVE Spiral curve (Fresnel integrals) precision in `float` C# vs `double` C++ produces shifted waypoint coordinates that cascade into wrong lane assignments | **High** | **High** | Use `double` everywhere in `Geometry.cs` (matches C++); cross-check `PosFromDist` against a reference scene with known coordinates in unit tests; for Town03 (lots of curves), produce ~50 reference waypoints from upstream binary and assert |
| 2 | `Map.GenerateTopology()` + `Map.GenerateWaypoints(approx_distance)` are recursive over road graph; subtle bugs in successor/predecessor wiring in `MapBuilder.Build()` produce disconnected graph → vehicles drive off the map | **High** | **High** | Build a CLI tool that dumps the C# topology as graphviz and compares against an `.osmgraph` dump from upstream's C++ side. Required for sign-off. |
| 3 | `TrafficManagerServer` (RPC server on port 8000) — no msgpack-rpc *server* exists in CarlaNet today, only the client. Building one is ~500 LoC and protocol-fragile | **High** | **Medium** | Decide early (§10): if v1 is in-process only, the shim handles `SetAutopilot` rewriting and we defer the server. Document explicitly that "external clients connecting to the TM on port 8000" is unsupported in v1. |
| 4 | `boost::geometry` polygon intersection in `CollisionStage` — a hand-rolled SAT in C# may give slightly different results on edge cases (touching but not overlapping polygons), leading to over- or under-aggressive braking | Med | Med | Unit test against ~20 hand-crafted scenarios (head-on, perpendicular, parallel, touching). Tolerance: agreement within `OVERLAP_THRESHOLD = 0.1` (Constants.h:77) |
| 5 | PID parameters are tuned to upstream simulator's response — if C# PID has any subtle dt difference (e.g. wall-clock vs simulation-clock confusion), vehicles oscillate | Med | Med | Use `current_state.time_instance = world.GetSnapshot().elapsed_seconds` everywhere; never use `Stopwatch` or `DateTime.Now`. Confirm by inspecting MotionPlanStage.cpp's `current_timestamp` usage |
| 6 | `cc::World::GetActors()` RPC every tick — if our `Actor` deserialization is slower than upstream's, we lose tick budget to it | Med | Med | Profile `CarlaClient.GetActorsAsync()` standalone. If > 5 ms for 100 actors, investigate `MessagePackSerializer` optimization (consider `MessagePackSerializerOptions.Standard.WithCompression(...)` or LZ4) |
| 7 | `InMemoryMap.SetUp()` takes ~500 ms — first `get_trafficmanager()` call blocks the Python script. User-facing UX surprise | Low | Low | Document; optionally run `SetUp()` on a background `Thread` and have `RegisterVehicles` wait. Skip the binary `Load()` cache path in v1 |
| 8 | The Town03 roundabout exception (LocalizationStage.cpp:92) is a magic-number hack. Other towns may have similar undocumented exceptions we miss | Low | Med | Grep upstream for `GetMapName()` and `Town\d+` — port every such exception verbatim with the same comment |
| 9 | Hybrid-physics teleportation needs `world.SetActorTransform(actor, transform)` for dormant vehicles. Our `CarlaNet.Transport` may not have it exposed; if it does, semantics around "is the actor dormant on the server side" require care | Med | Med | Confirm `ApplyTransform` rpc::Command exists in CarlaNet's Command set; verify by tracing MotionPlanStage.cpp:120 |
| 10 | `RandomGenerator` upstream is `std::mt19937` — C# `System.Random` produces different sequences from the same seed. With `set_random_device_seed`, scripts that compare results across implementations will diverge | Low | Low | Either port mt19937 (well-defined, ~80 LoC), or document that determinism within CarlaNet is preserved but **cross-impl reproducibility is not** |

---

## 13. Recommended Implementer-Agent Team

Total: **10 agent runs**, parallelized into **4 waves**. Critical-path
length: 3 waves (~3× the time of a single agent).

```
WAVE 1 (parallel, 3 agents)           — foundation
├── Agent A: CarlaNet.Map.Geom + element/Geometry (Line, Arc, Spiral, Poly3, ParamPoly3 + CubicPolynomial + Math helpers + SpatialGrid)
├── Agent B: CarlaNet.Map.Road skeleton (RoadTypes, Map/MapData/Road/Lane/LaneSection/Junction/Signal **as plain data classes without graph wiring** + InformationSet + RoadInfo* records)
└── Agent C: CarlaNet.TrafficManager.Constants + Parameters + RandomGenerator + DataStructures (the pure-data types; no logic depending on Map)

(All 3 produce code that compiles independently. C blocks on B for Waypoint POD only — trivially small dependency.)

WAVE 2 (parallel, 2 agents)           — parsers + InMemoryMap
├── Agent D: All 10 OpenDRIVE parsers + OpenDriveParser orchestrator + MapBuilder.Build (graph wiring)
└── Agent E: TM glue: SimulationState, TrackTraffic, AtomicActorSet, Stage base, RoadOption, SimpleWaypoint, CachedSimpleWaypoint
                                       (depends on Wave 1 B for Map types)

Synchronization point: end of Wave 2 we have a working OpenDriveParser.Load(xml) → Map → InMemoryMap chain. Verify with a Town01 fixture.

WAVE 3 (parallel, 4 agents)           — 6 stages + InMemoryMap
├── Agent F: InMemoryMap.SetUp / .GetWaypoint / .GetWaypointsInDelta + topology interpolation (the most complex single class besides Map.cpp)
├── Agent G: ALSM + VehicleLightStage (both touch world.GetActors / GetWeather — pair them)
├── Agent H: LocalizationStage + MotionPlanStage + PIDController (these are tightly coupled via LocalizationFrame; pair them)
└── Agent I: CollisionStage + TrafficLightStage + Polygon2D SAT

WAVE 4 (sequential, 2 agents)         — orchestration + verification
├── Agent J: TrafficManagerLocal (worker thread + Run loop + step semaphores) + TrafficManager facade + TrafficManagerRemote shim + Python integration (modify carlanet/__init__.py)
└── Agent K: VERIFICATION
            - Run generate_traffic.py n=5 asynch
            - Run generate_traffic.py n=30 sync hybrid
            - Run generate_traffic.py n=50 sync
            - Profile per-tick cost
            - Cross-check Town03 waypoints against C++ reference
            - Fix any regressions
```

### Why this split

- **Wave 1** gives every downstream agent a stable type surface. The three
  agents touch disjoint files; no merge conflicts.
- **Wave 2** is bounded — agent D works on parsing, agent E works on TM
  data primitives. Both feed Wave 3.
- **Wave 3** four agents pair up the stages that share frames. Specifically
  `LocalizationStage` writes `LocalizationFrame` which `MotionPlanStage`
  reads; co-locating them avoids cross-agent interface drift.
- **Wave 4** is necessarily sequential — the orchestrator can't be written
  before its inputs exist, and verification can't run before the orchestrator
  runs.

### Per-agent budget guidance

- Wave 1 agents: ~1,500–2,000 LoC each
- Wave 2 agents: D ~1,500 LoC, E ~700 LoC
- Wave 3 agents: F ~900 LoC, G ~700 LoC, H ~1,200 LoC, I ~700 LoC
- Wave 4: J ~600 LoC, K mostly verification
- **Total**: ~13,000–15,000 LoC C# spread across 10 agents.

### Risks to the team plan

- If Agent A's `Geometry.cs` ships with float-precision bugs (Risk #1), Wave 2
  D agent will produce a broken Map; cascading damage. **Build A's unit tests
  in Wave 1 itself; reject incomplete tests before Wave 2 starts.**
- Agent E's `SimpleWaypoint` next/prev/left/right wiring depends on Agent F's
  `InMemoryMap.SetUp` algorithm. Tentative split: Agent E ships the bare
  `SimpleWaypoint` data class; Agent F owns the wiring algorithm.

