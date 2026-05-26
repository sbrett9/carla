# NAV_PORT_SPEC

Authoritative specification for porting CARLA's pedestrian-navigation subsystem
(`LibCarla/source/carla/nav/` + `LibCarla/source/carla/client/detail/WalkerNavigation.{h,cpp}`
+ `LibCarla/source/carla/client/WalkerAIController.{h,cpp}` + walker bindings in
`PythonAPI/carla/src/Actor.cpp`) to .NET 10, inside the CarlaNet project.

One new project:

- `CarlaNet.Nav` — Detour-based navmesh loading + crowd-managed walker AI

Goal: **feature parity in one pass** for the upstream `WalkerAIController.start /
go_to_location / set_max_speed` flow used by `generate_traffic.py -w N`, plus
`World.get_random_location_from_navigation`, `set_pedestrians_seed`, and
`set_pedestrians_cross_factor` (currently stubbed no-ops in
`CarlaNet/python/carlanet/__init__.py`).

Project precedent: `CarlaNet/TRAFFIC_MANAGER_PORT_SPEC.md` (format / strict
layering / single-worker-thread orchestrator / `internal sealed` default /
`_to_cs()` mutable-wrapper pattern in the Python shim). Reuse existing CarlaNet
types where possible.

All file/line references are absolute paths under
`g:\Projects\CarlaUE_5_7_4\carla\`.

Third-party dependency: **DotRecast** (BSD-flavour ZLib license — compatible
with CARLA MIT). The latest release as of this writing is **2026.1.3**
(2026-02-28). NuGet package IDs (all in the same release line):

- `DotRecast.Core`
- `DotRecast.Detour`
- `DotRecast.Detour.Crowd`

Source repo: <https://github.com/ikpil/DotRecast>. Active maintenance, used in
commercial Unity / Stride / Godot projects. Pin to exact version (no floating
ranges) so a DotRecast schema bump does not silently break the navmesh blob
parse.

---

## 1. C++ file inventory + algorithmic summary

| File | LoC | Purpose |
|------|----:|---------|
| `nav/Navigation.h` | 156 | Public facade: navmesh load + path / crowd query + walker AI top layer |
| `nav/Navigation.cpp` | 1249 | Implementation built on Detour (`dtNavMesh`, `dtNavMeshQuery`, `dtCrowd`) |
| `nav/WalkerManager.h` | 99 | Per-walker route + state-machine bookkeeping; pre-computed traffic-light waypoints |
| `nav/WalkerManager.cpp` | 328 | Route construction, state machine tick, event dispatch via `std::visit` |
| `nav/WalkerEvent.h` | 71 | `std::variant<WalkerEventIgnore, WalkerEventWait, WalkerEventStopAndCheck>` + visitor |
| `nav/WalkerEvent.cpp` | 67 | Event handler implementations |
| `client/detail/WalkerNavigation.h` | 114 | Episode-level holder; subscribes the simulator to `_nav`, owns the walker registration list |
| `client/detail/WalkerNavigation.cpp` | 193 | Per-tick driver: refreshes crowd, batches `ApplyWalkerState`, evicts dead walkers |
| `client/WalkerAIController.h` | 34 | Thin actor subclass; the public binding surface |
| `client/WalkerAIController.cpp` | 84 | Implementations call into `WalkerNavigation` via `Simulator` |
| `PythonAPI/carla/src/Actor.cpp` (lines 223–232) | 9 | boost::python bindings — **the contract** for the Python shim |

**Total in scope: ~2,400 LoC C++.** Expect ~2,000 LoC C# (~80 % of C++ — the
mutex bookkeeping disappears under the single-worker model, but DotRecast API
shims and the OBB / paused / dead patches we must reimplement add some back).

### 1.1 `Navigation.cpp` algorithmic summary

| Method | C++ lines | Role |
|---|---|---|
| `Load(filename)` | 83-97 | File → byte vector → `Load(content)` |
| `Load(content)` | 100-199 | **Deserialize CARLA navmesh blob** (§3). On success, also calls `CreateCrowd()` and stores the binary so it survives a re-load |
| `CreateCrowd` | 201-264 | Construct `dtCrowd` with `MAX_AGENTS=500`, `max_agent_radius=AGENT_RADIUS*20=6.0f`, sets up 2 query filters (0 = cannot cross roads, 1 = can) and 4 obstacle-avoidance tiers |
| `GetPath` | 267-370 | Detour path query: `findNearestPoly` → `findPath` → `findStraightPath`. Coordinate swap `(x,z,y)` Unreal↔Recast. Returns path + per-poly area type |
| `GetAgentRoute` | 372-470 | Variant that reads the agent's queryFilter from the crowd; used by `WalkerManager::SetWalkerRoute` |
| `AddWalker` | 473-531 | Insert pedestrian into the crowd. Random filter assignment (cross-roads probability). Hook into `WalkerManager::AddWalker` |
| `AddOrUpdateVehicle` | 534-660 | **Insert/update a vehicle as a non-moving OBB obstacle in the crowd** — relies on CARLA-patched `useObb` + `obb[12]` fields on the crowd agent (§10 R1) |
| `RemoveAgent` | 663-706 | Crowd `removeAgent` + Walker/Vehicle map cleanup |
| `UpdateVehicles` | 709-732 | Set-diff: insert / update / remove |
| `SetWalkerMaxSpeed` | 735-762 | Mutates `dtCrowdAgent.params.maxSpeed` |
| `SetWalkerTarget` | 765-779 | Delegates to `_walker_manager.SetWalkerRoute(id, to)` (event-aware route) |
| `SetWalkerDirectTarget(Index)` | 782-831 | `findNearestPoly` + `dtCrowd::requestMoveTarget` (bypasses the event machinery) |
| `UpdateCrowd` | 834-920 | Once per simulator tick. Calls `_crowd->update(dt)`, then `_walker_manager.Update(dt)`. Every 4 seconds (`AGENT_UNBLOCK_TIME`) checks if any agent has moved < 0.5 m; stuck agents get reassigned to a new random target + possibly new road-crossing filter |
| `GetWalkerTransform` | 923-984 | Reads `agent->npos` + `agent->vel`. Interpolates yaw via shortest-angle wrap at `rotation_speed = (speed/1.5) * 6` rad/s |
| `GetWalkerPosition` / `GetWalkerSpeed` | 987-1059 | Pure reads off the crowd agent |
| `GetRandomLocation` | 1062-1100 | `dtNavMeshQuery::findRandomPoint` (sidewalk-only by default), up to 10 attempts |
| `SetAgentFilter` | 1103-1113 | Mutates `agent->params.queryFilterType` |
| `SetPedestriansCrossFactor` | 1119-1122 | Sets `_probability_crossing`; consulted by `AddWalker` and the unblock path |
| `PauseAgent` | 1125-1155 | Sets CARLA-patched `agent->paused` (§10 R1) |
| `HasVehicleNear` | 1157-1175 | Calls CARLA-patched `dtCrowd::hasVehicleNear` — scans neighbours with `useObb=true` (§10 R1) |
| `SetWalkerLookAt` | 1178-1212 | Forces the agent's velocity vectors to (very small) direction-only values, so the yaw-interpolation reads a "looking-at" angle |
| `IsWalkerAlive` | 1214-1246 | Reads CARLA-patched `agent->dead` (§10 R1) |

### 1.2 `WalkerManager.cpp` algorithmic summary

| Method | Lines | Role |
|---|---|---|
| `AddWalker(id)` | 37-47 | One-shot `GetAllTrafficLightWaypoints()` then registers a default-state `WalkerInfo` |
| `RemoveWalker(id)` | 50-58 | Erases from `_walkers` |
| `Update(delta)` | 61-111 | **The walker tick** — per-walker state-machine dispatch (§4) |
| `SetWalkerRoute(id)` | 114-125 | New random target via `Navigation::GetRandomLocation` |
| `SetWalkerRoute(id, to)` | 128-181 | Build the route from `GetAgentRoute` output. Emit a `WalkerEventStopAndCheck(60s)` whenever the path enters a road / crosswalk from a safe area; emit `WalkerEventIgnore` for sidewalks |
| `SetWalkerNextPoint(id)` | 184-216 | Advance route index; on route-end set state to `WALKER_STOP`+pause+request a new random route |
| `GetWalkerNextPoint` | 219-239 | Look-ahead helper |
| `GetWalkerCrosswalkEnd` | 241-265 | Scan forward in the route to find where the current crosswalk ends — used by `WalkerEventStopAndCheck` to look down the crosswalk axis for incoming traffic |
| `ExecuteEvent` | 267-275 | `std::visit(visitor, rp.event)` |
| `GetAllTrafficLightWaypoints` | 277-302 | One-shot enumerate every `traffic.traffic_light` actor + cache stop-waypoints (used by `GetTrafficLightAffecting`) |
| `GetTrafficLightAffecting(loc, max_dist)` | 306-324 | Nearest-TL search; returns `nullptr` if outside `max_distance` |

### 1.3 `WalkerEvent.cpp` algorithmic summary

| Visitor overload | Lines | Behaviour |
|---|---|---|
| `WalkerEventIgnore` | 15-17 | Immediately returns `EventResult::End` — advance to next route point |
| `WalkerEventWait` | 19-26 | Countdown timer. `End` when expired |
| `WalkerEventStopAndCheck` | 28-64 | **The traffic-light + crosswalk-check stop event.** Decrement timer; if it hits zero return `TimeOut` (route replanned). First tick: pause agent, look up nearest TL via `WalkerManager::GetTrafficLightAffecting`. If green/yellow keep waiting. Otherwise unpause and check `HasVehicleNear(6.0, crosswalk_axis)` — `End` if clear, else `Continue` |

### 1.4 `WalkerNavigation.cpp` algorithmic summary

| Method | Lines | Role |
|---|---|---|
| ctor | 24-31 | Fetch `GetRequiredFiles("Nav")` from server (this is the navmesh-blob filename), `GetCacheFile` (downloads + caches it), pass bytes to `Navigation::Load`. Sets `_nav.SetSimulator(simulator)` so the walker manager can query world / actor snapshots |
| `Tick(episode)` | 33-81 | **Per simulator tick.** (1) Check 1 walker for existence (round-robin via `_next_check_index`), evict if missing. (2) `UpdateVehiclesInCrowd` — push every vehicle's bounding box into the crowd as OBB obstacles. (3) `_nav.UpdateCrowd(state)`. (4) For every walker: `GetWalkerTransform` + `GetWalkerSpeed`, batch into `Cmd::ApplyWalkerState`. (5) `ApplyBatchSync(commands)`. (6) For every walker check `IsWalkerAlive`; on death enable collisions, mark dead, destroy controller, unregister |
| `RegisterWalker` / `UnregisterWalker` / `RemoveWalker` / `AddWalker` | header inline | Atomic-list mutators called by `WalkerAIController::Start/Stop` |

### 1.5 `WalkerAIController.cpp` algorithmic summary

| Method | Lines | Role |
|---|---|---|
| `Start` | 18-32 | Tell episode to `RegisterAIController` + `nav->AddWalker(parent.id, parent.location)`. **Also disables physics and collisions on the walker actor** (Detour drives the position via `ApplyWalkerState`) |
| `Stop` | 34-45 | `UnregisterAIController` + `nav->RemoveWalker` |
| `GetRandomLocation` | 47-53 | `nav->GetRandomLocation` |
| `GoToLocation(dest)` | 55-67 | `nav->SetWalkerTarget(parent.id, dest)` |
| `SetMaxSpeed(speed)` | 69-81 | `nav->SetWalkerMaxSpeed(parent.id, speed)` |

---

## 2. Detour API surface used (and DotRecast equivalents)

Source: `nav/Navigation.cpp` + `nav/WalkerEvent.cpp`. DotRecast file paths
are under `src/DotRecast.*` in <https://github.com/ikpil/DotRecast>.

### 2.1 Mesh loading

| C++ Detour | DotRecast | Notes |
|---|---|---|
| `#pragma pack(push,1) struct NavMeshSetHeader { magic, version, num_tiles, dtNavMeshParams params; }` | `DotRecast.Detour.Io.NavMeshSetHeader` (`NAVMESHSET_MAGIC = 'MSET' = 0x4D534554`, `NAVMESHSET_VERSION = 1`) | **EXACT byte layout match** (see §3) |
| `dtAllocNavMesh / mesh->init(&params)` | `new DtNavMesh(); mesh.Init(option, maxVertsPerPoly)` | `DotRecast.Detour/DtNavMesh.cs` |
| `mesh->addTile(data, size, DT_TILE_FREE_DATA, tile_ref, 0)` | `mesh.AddTile(data, flags, tileRef, out resultRef)` | The high-level `DtMeshSetReader.Read` does this loop internally |
| `dtAllocNavMeshQuery / query->init(mesh, MAX_QUERY_SEARCH_NODES=2048)` | `new DtNavMeshQuery(mesh); query.Init(mesh, maxNodes)` | `DotRecast.Detour/DtNavMeshQuery.cs` (constructor takes the mesh; init sets the node-pool size) |
| `dtAllocCrowd / crowd->init(MAX_AGENTS=500, max_agent_radius=6.0, mesh)` | `new DtCrowd(new DtCrowdConfig(maxAgentRadius), mesh)` | `DotRecast.Detour.Crowd/DtCrowd.cs`. `DtCrowdConfig` carries the `maxAgents` + `maxAgentRadius` |

**One-call shortcut:** `var mesh = new DtMeshSetReader().Read32Bit(byteBuffer);`
loads the navmesh in one go. **Must use `Read32Bit`** because CARLA's
`recastnavigation` is built without `DT_POLYREF64`, so `dtTileRef = unsigned int`
(4 bytes); the default `Read` reads 8 bytes per tile-ref and corrupts everything
after the first tile (§10 R2).

### 2.2 Path queries

| C++ | DotRecast | Notes |
|---|---|---|
| `dtQueryFilter; setIncludeFlags / setExcludeFlags / setAreaCost(area, cost)` | `DtQueryDefaultFilter(includeFlags, excludeFlags, areaCost[64])` | **AMBIGUOUS — Field 1**: stock DotRecast filter's area-costs are constructor-only (no `setAreaCost`). Confirm during impl whether we must subclass `IDtQueryFilter` to mutate per-area cost at runtime (CARLA's `Navigation::GetPath` sets two area costs each call — once per query). Workaround: build the two filters CARLA uses (filter-0 = can't cross roads, filter-1 = can) up front and pass references. Per-call mutation isn't actually used — `GetPath` allocates a fresh `dtQueryFilter` each call, so reuse a cached one |
| `findNearestPoly(pos, half_ext, filter, &ref, nearest)` | `query.FindNearestPoly(centerPos, halfExtents, filter, out nearestRef, out nearestPt, out _)` | `DtNavMeshQuery.cs` |
| `findPath(start_ref, end_ref, start_pos, end_pos, filter, polys[], &n, maxPolys)` | `query.FindPath(startRef, endRef, startPos, endPos, filter, ref polys, options)` (returns `DtStatus`; `polys` is a `List<long>`) | `DtNavMeshQuery.cs` ~line 4500 |
| `findStraightPath(start, end, polys, n, straight[], flags[], polys[], &n2, max, options)` | `query.FindStraightPath(startPos, endPos, path, ref straightPath, maxStraightPath, options)` (output is `List<DtStraightPath>` w/ `pos / flags / refs` fields) | `DT_STRAIGHTPATH_AREA_CROSSINGS = 0x02` exists in `DotRecast.Detour/DtStraightPathOptions.cs` |
| `closestPointOnPoly(ref, pos, closest, isOverPoly)` | `query.ClosestPointOnPoly(ref, pos, out closestPt, out posOverPoly)` | |
| `findRandomPoint(filter, frand, &ref, point)` | `query.FindRandomPoint(filter, frand, out randomRef, out randomPt)` | `frand` is a `Func<float>` delegate (DotRecast uses `IRcRand`) |
| `dtNavMesh::getPolyArea(ref, &area)` | `mesh.GetPolyArea(polyRef, out area)` or `mesh.GetTileAndPolyByRef(ref, out tile, out poly); area = poly.GetArea()` | Used to attach per-route-point area type to `WalkerRoutePoint` |

### 2.3 Crowd management

| C++ | DotRecast | Notes |
|---|---|---|
| `dtCrowdAgentParams { radius, height, maxAcceleration, maxSpeed, collisionQueryRange, obstacleAvoidanceType, separationWeight, queryFilterType, updateFlags }` | `DtCrowdAgentParams { radius, height, maxAcceleration, maxSpeed, collisionQueryRange, pathOptimizationRange, separationWeight, updateFlags, obstacleAvoidanceType, queryFilterType, userData }` | **Field-for-field match.** Note DotRecast adds `pathOptimizationRange` (set to e.g. `radius*30` per stock samples) |
| `dtCrowdAgentParams::useObb / obb[12]` | **NOT IN DOTRECAST** | CARLA-only patch to support vehicle-OBB-as-obstacle. See §10 R1 — must reimplement outside the crowd (proximity-grid scan in `Navigation`) |
| `crowd->addAgent(pos, &params) -> int` | `crowd.AddAgent(pos, option) -> int` | |
| `crowd->removeAgent(idx)` | `crowd.RemoveAgent(idx)` | |
| `crowd->getAgent(idx) -> const dtCrowdAgent*` | `crowd.GetAgent(idx) -> DtCrowdAgent` | |
| `crowd->getEditableAgent(idx) -> dtCrowdAgent*` | (same — class is mutable in DotRecast) | |
| `crowd->getAgentCount()` | `crowd.GetAgentCount()` | |
| `crowd->requestMoveTarget(idx, target_ref, target_pos)` | `crowd.RequestMoveTarget(idx, refs, pos)` | |
| `crowd->update(dt, debug)` | `crowd.Update(dt, debug)` | `debug` is a `DtCrowdAgentDebugInfo?` |
| `crowd->getFilter(i) -> const dtQueryFilter*` | `crowd.GetFilter(i) -> IDtQueryFilter` | |
| `crowd->getEditableFilter(i)` | (same — interface methods are mutable) | |
| `crowd->getQueryHalfExtents()` | `crowd.GetQueryExtents()` | |
| `crowd->getObstacleAvoidanceParams(i) / setObstacleAvoidanceParams(i, &params)` | `crowd.GetObstacleAvoidanceParams(i) / SetObstacleAvoidanceParams(i, ¶ms)` | `DtObstacleAvoidanceParams` — all fields present |
| `crowd->pauseAgent(idx, bool) / agent->paused` | **NOT IN DOTRECAST** | §10 R1 |
| `crowd->hasVehicleNear(idx, distSq, dir, setLookAt)` | **NOT IN DOTRECAST** | §10 R1 |
| `agent->dead` | **NOT IN DOTRECAST** | §10 R1 |
| `agent->active` | `agent.state != DT_CROWDAGENT_STATE_INVALID` (use `DtCrowdAgentState`) | DotRecast tracks lifecycle via the enum, not a bool |
| `agent->npos / vel / nvel / dvel` (float[3]) | `agent.npos / vel / nvel / dvel` (`RcVec3f`) | Same semantics |

### 2.4 Update flags

| C++ enum `UpdateFlags` (`Navigation.cpp:23`) | DotRecast `DtCrowdAgentUpdateFlags` |
|---|---|
| `DT_CROWD_ANTICIPATE_TURNS = 1` | same name |
| `DT_CROWD_OBSTACLE_AVOIDANCE = 2` | same name |
| `DT_CROWD_SEPARATION = 4` | same name |
| `DT_CROWD_OPTIMIZE_VIS = 8` | same name |
| `DT_CROWD_OPTIMIZE_TOPO = 16` | same name |

### 2.5 Constants

| C++ (`Navigation.cpp:33-44`) | C# equivalent |
|---|---|
| `MAX_POLYS = 256` | `const int MaxPolys = 256` |
| `MAX_AGENTS = 500` | `const int MaxAgents = 500` |
| `MAX_QUERY_SEARCH_NODES = 2048` | `const int MaxQuerySearchNodes = 2048` |
| `AGENT_HEIGHT = 1.8f` | `const float AgentHeight = 1.8f` |
| `AGENT_RADIUS = 0.3f` | `const float AgentRadius = 0.3f` |
| `AGENT_UNBLOCK_DISTANCE = 0.5f`, `AGENT_UNBLOCK_TIME = 4.0f` | same |
| `AREA_GRASS_COST = 1.0`, `AREA_ROAD_COST = 10.0` | same |

---

## 3. Navmesh blob format

`Navigation::Load(content)` at `LibCarla/source/carla/nav/Navigation.cpp:100-199`
is the authoritative source. The format is the **standard recastnavigation
`Sample::saveAll`** dump — verified against
`Build/_deps/recastnavigation-src/RecastDemo/Source/Sample.cpp:421-490`. There
is **no CARLA wrapper** around it. The server-side path: `recastnavigation`'s
`Sample::saveAll` writes the file (via `RecastBuilder.exe` at map cook time);
the file is shipped under the map's `Nav/` folder; the client RPC
`get_required_files("Nav")` returns the filename, `get_cache_file(name, true)`
downloads the bytes verbatim, the result is fed straight to `Navigation::Load`.

### 3.1 Byte layout

`#pragma pack(push, 1)` is **in force for both header structs** in
`Navigation.cpp:103-115`. Sizes:

```
NavMeshSetHeader (40 bytes, little-endian):
  int    magic       = 0x4D534554 'MSET'             (offset 0,  4 bytes)
  int    version     = 1                             (offset 4,  4 bytes)
  int    num_tiles                                   (offset 8,  4 bytes)
  dtNavMeshParams params {                           (offset 12, 28 bytes)
    float orig[3]                                    (offset 12, 12 bytes)
    float tileWidth                                  (offset 24,  4 bytes)
    float tileHeight                                 (offset 28,  4 bytes)
    int   maxTiles                                   (offset 32,  4 bytes)
    int   maxPolys                                   (offset 36,  4 bytes)
  }

Per tile (repeated num_tiles times):
NavMeshTileHeader (8 bytes, pack(1)):
  dtTileRef tile_ref   (= uint32_t with CARLA's build, no DT_POLYREF64)
  int       data_size
followed by data_size bytes of tile blob (a serialized dtMeshHeader + verts +
polys + links + detail mesh + BV tree + off-mesh connections in
DT_NAVMESH_VERSION = 7 format)
```

### 3.2 DotRecast deserialization

DotRecast's `DotRecast.Detour.Io.DtMeshSetReader.Read32Bit(RcByteBuffer)` reads
**exactly this format**. The magic + version constants match
(`NavMeshSetHeader.NAVMESHSET_MAGIC = 'MSET'`, `NAVMESHSET_VERSION = 1`). The
reader auto-detects endianness via `RcIO.SwapEndianness` on the magic.

Critical correctness notes (extracted from
`src/DotRecast.Detour/Io/DtMeshSetReader.cs:69-126` and
`DtMeshDataReader.cs`):

1. **MUST call `Read32Bit`, not `Read`.** The default `Read` path reads
   `bb.GetLong()` (8 bytes) for `tileRef`, which corrupts every byte after
   tile #0. CARLA builds recastnavigation without `DT_POLYREF64`, so
   `dtTileRef = unsigned int = 4 bytes` (`Detour/Include/DetourNavMesh.h:54-56`).
2. **`cCompatibility` auto-detects** from `version == NAVMESHSET_VERSION = 1`.
   In this branch the `is32Bit && cCompatibility` combination is the one that
   matches: 4-byte tileRef + 4-byte dataSize, NO extra padding (because CARLA's
   `#pragma pack(1)` already removed the natural 4-byte alignment pad that the
   non-32-bit branch tries to skip).
3. **`maxVertsPerPoly` defaults to a derived value when `header.version != NAVMESHSET_VERSION_RECAST4J` (0x8802)**. For CARLA's `version=1` blobs, DotRecast uses `maxVertPerPoly = -1` which is then carried as `mesh.GetMaxVertsPerPoly() == -1`; this is what `DtMeshDataReader` propagates through `ReadPolys`. **AMBIGUOUS — Field 2**: confirm the polys read correctly with `maxVertsPerPoly == -1` at runtime. Stock recastnavigation defaults `DT_VERTS_PER_POLYGON = 6` and writes that many `unsigned short` indices per poly. If we see corruption in `ReadPolys`, pass `6` explicitly via `Read32Bit(bb, 6)`.
4. The reader leaves the cursor at the byte right after the navmesh data.
   CARLA's blob has nothing after it, so we're done.

### 3.3 Pseudocode for the port

```csharp
public bool Load(ReadOnlySpan<byte> content)
{
    var bb = new RcByteBuffer(content.ToArray());   // DotRecast wraps the buffer
    var reader = new DtMeshSetReader();
    try {
        _navMesh = reader.Read32Bit(bb, maxVertPerPoly: 6);  // §3.2 note 3
    }
    catch (IOException ex) {
        _logger?.LogWarning(ex, "Nav: failed loading binary");
        return false;
    }
    _navQuery = new DtNavMeshQuery(_navMesh);
    _navQuery.Init(_navMesh, MaxQuerySearchNodes);
    _binaryMesh = content.ToArray();
    _ready = true;
    CreateCrowd();
    return true;
}
```

---

## 4. WalkerManager state machine

States (`WalkerManager.h:23-28`): `WALKER_IDLE`, `WALKER_WALKING`,
`WALKER_IN_EVENT`, `WALKER_STOP`.

`WalkerInfo` (`WalkerManager.h:37-43`): `from`, `to`, `currentIndex`, `state`,
`route: vector<WalkerRoutePoint>`. Each `WalkerRoutePoint` carries `{event,
location, areaType}`.

### 4.1 Per-tick transitions (`WalkerManager::Update`, lines 61-111)

```
WALKER_IDLE      → (no action) — exits when SetWalkerRoute(id, to) lands
WALKER_WALKING   → measure distance² to route[currentIndex].location:
                     ≤ 1.0 → transition to WALKER_IN_EVENT
                     > 1.0 → stay
WALKER_IN_EVENT  → dispatch route[currentIndex].event via std::visit:
                     Continue → stay (re-tick next frame)
                     End      → SetWalkerNextPoint(id)
                     TimeOut  → SetWalkerRoute(id)   (re-plan from scratch)
WALKER_STOP      → transition immediately to WALKER_IDLE
```

`SetWalkerNextPoint` (`WalkerManager.cpp:184-216`) advances `currentIndex`,
unpauses, calls `Navigation::SetWalkerDirectTarget` to set the crowd
target. When `currentIndex >= route.size()` it sets state to `WALKER_STOP`,
pauses, then immediately requests a new random route via `SetWalkerRoute(id)`
(which itself transitions back to `WALKER_WALKING`).

### 4.2 Route construction (`WalkerManager::SetWalkerRoute(id, to)`)

For each path point returned by `Navigation::GetAgentRoute`:

| Source `areaType` | Previous `areaType` | Emitted event |
|---|---|---|
| `CARLA_AREA_SIDEWALK (1)` | any | `WalkerEventIgnore` |
| `CARLA_AREA_ROAD (3)` or `CROSSWALK (2)` | not `ROAD`/`CROSSWALK` (i.e. coming from sidewalk/grass) | `WalkerEventStopAndCheck(60.0)` |
| `CARLA_AREA_ROAD` or `CROSSWALK` | already `ROAD`/`CROSSWALK` | (skipped — not pushed at all) |
| default | any | `WalkerEventIgnore` |

After building the route, immediately calls `SetWalkerNextPoint(id)` to move
the walker to point index 1 (skips point 0 because the agent is already there).

### 4.3 C# translation

Use a `sealed record` hierarchy + `switch` expression for events
(no `std::variant` / no visitor object — C# pattern matching handles dispatch):

```csharp
internal abstract record WalkerEvent;
internal sealed record WalkerEventIgnore() : WalkerEvent;
internal sealed record WalkerEventWait(double TimeRemaining) : WalkerEvent;
internal sealed record WalkerEventStopAndCheck(
    double TimeRemaining,
    bool CheckForTrafficLight,
    TrafficLightActor? Actor) : WalkerEvent;

internal enum EventResult { Continue, End, TimeOut }

private EventResult ExecuteEvent(ActorId id, ref WalkerInfo info, double delta)
{
    var rp = info.Route[info.CurrentIndex];
    switch (rp.Event) {
        case WalkerEventIgnore:
            return EventResult.End;
        case WalkerEventWait w:
            var rem = w.TimeRemaining - delta;
            rp = rp with { Event = w with { TimeRemaining = rem } };
            info.Route[info.CurrentIndex] = rp;
            return rem <= 0 ? EventResult.End : EventResult.Continue;
        case WalkerEventStopAndCheck s:
            return HandleStopAndCheck(id, ref rp, s, delta);
        default: throw new InvalidOperationException();
    }
}
```

The `WalkerRoutePoint` is a mutable `struct` rather than a record so the
in-place edit pattern (the C++ event countdown mutates `event.time`) translates
cleanly. Alternative: hold a flat `List<WalkerEvent>` parallel to a list of
locations and area types — implementer's choice.

State enum:

```csharp
internal enum WalkerState : byte { Idle, Walking, InEvent, Stop }
```

Use **`ConcurrentDictionary<ActorId, WalkerInfo>`** for the registry — the
worker thread runs `Update` while RPC server threads may receive
`go_to_location` / `set_max_speed` from the Python shim concurrently. Or, mirror
the TM pattern: a `_registrationGate` lock and a plain `Dictionary`. The TM
project already chose the lock pattern; consistency argues for that.

---

## 5. WalkerAIController Python binding

From `PythonAPI/carla/src/Actor.cpp:226-232`:

```cpp
class_<cc::WalkerAIController, bases<cc::Actor>, ...>("WalkerAIController", no_init)
    .def("start", &cc::WalkerAIController::Start)
    .def("stop",  &cc::WalkerAIController::Stop)
    .def("go_to_location", &cc::WalkerAIController::GoToLocation, (arg("destination")))
    .def("set_max_speed",  &cc::WalkerAIController::SetMaxSpeed,  (arg("speed")))
```

Plus the world-level methods called from `generate_traffic.py` (verified by
grep on `set_pedestrians_*` / `get_random_location_from_navigation`):

| Python | Underlying | Currently in CarlaNet? |
|---|---|---|
| `controller.start()` | `WalkerAIController::Start` | **NO** — needs port |
| `controller.stop()` | `WalkerAIController::Stop` | **NO** |
| `controller.go_to_location(loc)` | `WalkerAIController::GoToLocation` | **NO** |
| `controller.set_max_speed(speed)` | `WalkerAIController::SetMaxSpeed` | **NO** |
| `world.get_random_location_from_navigation()` | `Simulator::GetRandomLocationFromNavigation` → `WalkerNavigation::GetRandomLocation` → `Navigation::GetRandomLocation` | **stub** in `carlanet/__init__.py:965` (returns `None`) |
| `world.set_pedestrians_cross_factor(p)` | `WalkerNavigation::SetPedestriansCrossFactor` | **stub** (line 962, no-op) |
| `world.set_pedestrians_seed(s)` | `WalkerNavigation::SetPedestriansSeed` | **stub** (line 958, no-op) |

### 5.1 Required Python shim additions

The Python shim (`carlanet/__init__.py`) needs:

1. `WalkerAIController.start/stop/go_to_location/set_max_speed` methods (the
   class currently exists as a marker subclass at line 586 with no methods)
2. Wire `World.set_pedestrians_cross_factor`, `set_pedestrians_seed`,
   `get_random_location_from_navigation` to a new
   `CarlaNet.Nav.WalkerNavigation` C# facade exposed off the `CarlaClient`
3. The walker controller needs a reference back to the underlying
   `WalkerNavigation` instance. The Python shim already has `_inner._client`
   (the C# `CarlaClient`); add `WalkerNavigation` as a lazy property on the
   C# client (analogous to how the TM is constructed via `Client.get_trafficmanager`)
4. Apply the **mutable wrapper pattern** documented in the prior
   carlanet memo: `WalkerAIController.start()` is a Python method that calls a
   C# `WalkerNavigation.StartController(walkerId, controllerId)` method; same
   for stop/go_to_location/set_max_speed

---

## 6. Integration with TrafficManagerLocal

Walkers do not use the 7-stage vehicle pipeline — none of
`Localization / Collision / MotionPlan / VehicleLight / TrafficLight / ALSM`
applies to pedestrians. The integration model **does not extend the TM**; it is
a parallel subsystem owned by the `CarlaClient`.

### 6.1 Ownership model

```
CarlaClient (in CarlaNet.Transport)
  ├── world observer subscription      (existing)
  ├── TrafficManager dictionary        (existing, port→TM instance)
  └── WalkerNavigation?  (lazy)        (NEW)
        ├── Navigation                 (DotRecast wrapper)
        ├── WalkerManager
        └── ticker Thread              (single worker, daemon)
```

`WalkerNavigation` constructs lazily on the first call that needs it
(`StartController`, `GetRandomLocation`, `SetPedestriansCrossFactor`, …). On
construction it:

1. Calls `_client.GetRequiredFilesAsync("Nav")` to find the nav blob filename
2. Calls (NEW RPC §8.1) `GetCacheFileAsync(name)` to download the bytes
3. Hands them to `Navigation.Load`
4. Spins a daemon `Thread` running `Tick()` at the world-observer cadence
   (subscribes to the same `CarlaClient.OnTick` event the TM uses — no
   independent timer)

### 6.2 Per-tick driver (`WalkerNavigation.Tick`)

Direct C# port of `WalkerNavigation::Tick(episode)`
(`client/detail/WalkerNavigation.cpp:33-81`):

```csharp
private void Tick(TickTimestamp ts)
{
    if (_walkers.IsEmpty) return;
    CheckIfWalkerExists();                         // round-robin eviction
    UpdateVehiclesInCrowd();                       // sync vehicle OBBs
    _nav.UpdateCrowd(ts.DeltaSeconds);

    var commands = new List<Command>(_walkers.Count);
    foreach (var handle in _walkers) {
        if (_nav.TryGetWalkerTransform(handle.WalkerId, out var trans)) {
            var speed = _nav.GetWalkerSpeed(handle.WalkerId);
            commands.Add(new ApplyWalkerStateCommand(handle.WalkerId, trans, speed));
        }
    }
    if (commands.Count > 0)
        _client.ApplyBatchSyncAsync(commands, doTickCue: false).GetAwaiter().GetResult();

    // Per-walker IsWalkerAlive eviction (lines 65-79 of upstream)
    ...
}
```

The TM and the walker subsystem **both** end up issuing `ApplyBatchSync` per
tick. They run on independent threads — that is upstream-correct (vehicle and
walker control are independent in libcarla too — see how
`WalkerNavigation::Tick` issues its own batch separately from the TM's
control_frame). Verify no command-frame conflict during the verification phase
(§11).

### 6.3 NEW command: `ApplyWalkerStateCommand`

`Cmd::ApplyWalkerState { walker_id, transform, speed }` is referenced at
`WalkerNavigation.cpp:59`. Check whether `CarlaNet.Types.Rpc.Commands` already
has it — most likely not. Need to add (1 record) and serialize over msgpack
with the upstream tag. Source struct definition is at
`LibCarla/source/carla/rpc/Command.h` — implementer should grep for
`ApplyWalkerState` to extract the field order and tag index.

---

## 7. New CarlaNet.Nav project layout

```
CarlaNet/src/CarlaNet.Nav/
├── CarlaNet.Nav.csproj            # references DotRecast.Detour, DotRecast.Detour.Crowd, CarlaNet.Types, CarlaNet.Transport
├── GlobalUsings.cs
├── Navigation.cs                  # main facade (~700-800 LoC C#)
├── NavigationConstants.cs         # MaxPolys, AgentHeight, etc. (§2.5)
├── NavAreas.cs                    # CarlaNavArea / SamplePolyFlags enums
├── VehicleCollisionInfo.cs        # struct mirroring nav::VehicleCollisionInfo
├── WalkerEvent.cs                 # record hierarchy + EventResult enum
├── WalkerManager.cs               # state machine + route construction (~400 LoC)
├── WalkerInfo.cs                  # mutable struct: from/to/index/state/route
├── WalkerRoutePoint.cs            # mutable struct
├── WalkerNavigation.cs            # episode-level driver (~250 LoC)
├── DetourInterop/
│   ├── VehicleObbTracker.cs       # CARLA-only OBB-as-obstacle workaround (§10 R1)
│   ├── CarlaQueryFilter.cs        # IDtQueryFilter impl matching CARLA's per-area costs
│   └── PointAdapter.cs            # coordinate-swap (Unreal x,y,z ↔ Recast x,z,y) helpers
└── README.md                      # one-page architecture note
```

`.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\CarlaNet.Types\CarlaNet.Types.csproj" />
    <ProjectReference Include="..\CarlaNet.Transport\CarlaNet.Transport.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="DotRecast.Core"          Version="2026.1.3" />
    <PackageReference Include="DotRecast.Detour"        Version="2026.1.3" />
    <PackageReference Include="DotRecast.Detour.Crowd"  Version="2026.1.3" />
    <PackageReference Include="Microsoft.Extensions.Logging" Version="10.0.8" />
  </ItemGroup>
</Project>
```

**Layering:** `CarlaNet.Types ← CarlaNet.Transport ← CarlaNet.Nav` and
`CarlaNet.Python` adds a reference to `CarlaNet.Nav`. No circular references;
`CarlaNet.TrafficManager` and `CarlaNet.Nav` are independent siblings
(both depend on `Transport` and `Types`).

Add the project to `CarlaNet/CarlaNet.sln` and update
`CarlaNet/python/carlanet/__init__.py` to `_ref("CarlaNet.Nav")` (with a
graceful fallback similar to the TM block at lines 74-82).

---

## 8. RPC additions

`get_navigation_mesh` already exists in
`CarlaNet.Transport.CarlaClient.GetNavigationMeshAsync` (line 121-122 of
`CarlaClient.cs`). `get_required_files` already exists (line 144-145). These
are sufficient for the navmesh load path.

### 8.1 NEW client RPCs needed

| RPC | Signature | Server-side method | Used by |
|---|---|---|---|
| `get_cache_file` | `(string name, bool request_otherwise) → byte[]` | `Server::GetCacheFile` | `WalkerNavigation` ctor (preferred over `get_navigation_mesh` so we go through the upstream cache path; either works — see Risk R5) |
| `register_ai_controller` | `(ActorId)` → void | (server-side ATI registration; **AMBIGUOUS — Field 3**: check whether the server actually exposes a `register_ai_controller` RPC or whether it is a purely client-side bookkeeping call — `WalkerAIController::Start` at line 19 calls `RegisterAIController(*this)` on the simulator, which then routes back through the episode. If client-only, no RPC needed) |
| `unregister_ai_controller` | `(ActorId)` → void | likewise |

The two state-mutation methods on `WalkerNavigation` are **purely client-side**
(set_pedestrians_cross_factor / set_pedestrians_seed both mutate fields on
`_nav` locally — no server round-trip in upstream).

### 8.2 NEW command type

`ApplyWalkerStateCommand(ActorId Walker, Transform Transform, float Speed)`
in `CarlaNet.Types.Rpc.Commands`. Mirror the layout of the existing
`ApplyTransformCommand` / `ApplyVehicleControlCommand` records. Tag/index per
upstream `rpc/Command.h`.

---

## 9. Recommended port team

**1 surveyor (this doc) + 2 implementers + 1 integrator + 1 verifier = 5 agents.**

| Role | Files / tasks | Estimated LoC |
|------|---------------|--------------:|
| Implementer A — Navigation core | `Navigation.cs`, `NavigationConstants.cs`, `NavAreas.cs`, `VehicleCollisionInfo.cs`, `DetourInterop/*.cs` | ~800 |
| Implementer B — Walker state machine | `WalkerEvent.cs`, `WalkerManager.cs`, `WalkerInfo.cs`, `WalkerRoutePoint.cs`, `WalkerNavigation.cs` | ~700 |
| Integrator — wiring | `ApplyWalkerStateCommand` (Types), `WalkerNavigation` property on `CarlaClient`, `GetCacheFileAsync` RPC, `_ref("CarlaNet.Nav")` block in shim, `WalkerAIController.start/stop/go_to_location/set_max_speed` Python methods, `World.set_pedestrians_*` un-stubbing, sln update | ~200 |
| Verifier | `test/test_nav_walkers.py` (§11), confirm `generate_traffic.py -w 30 -n 0` end-to-end against live PIE | — |

### 9.1 Justification for parallelization

- **A and B can run in parallel.** `Navigation.cs` (A) is pure DotRecast +
  state. `WalkerManager.cs` (B) only depends on a `Navigation` *interface*
  (handful of methods: `GetRandomLocation`, `GetAgentRoute`, `PauseAgent`,
  `SetWalkerDirectTarget`, `GetWalkerPosition`, `HasVehicleNear`,
  `GetTrafficLightAffecting`). They agree on the interface up-front from this
  spec and stub the other side until they meet.
- **Integrator runs after A+B.** Needs both files compiling.
- **Verifier runs after Integrator.** Needs the Python shim wired.
- Total wall-clock: ~2× (A or B, whichever is longer) + 1× Integrator + 1×
  Verifier. A single-implementer approach would be ~1.5× that.

### 9.2 Anti-pattern: do NOT make the Navigation port one agent

Mixing the DotRecast learning curve, the OBB-workaround design, and the
state-machine port in one agent guarantees that one of the three gets shortcut.
Two implementers also gives a natural code-review pair (each reviews the
other's interface assumptions during their final merge).

---

## 10. Risks

Ranked by **probability × impact**.

### R1. CARLA-patched DetourCrowd fields (`useObb`, `obb[12]`, `paused`, `dead`, `hasVehicleNear`, `pauseAgent`)

**Probability HIGH · Impact HIGH · Confirmed by grep on
`Build/_deps/recastnavigation-src/DetourCrowd/{Include,Source}/DetourCrowd.{h,cpp}`.**

CARLA's vendored `recastnavigation` carries non-upstream patches. The
following do NOT exist in stock DotRecast:

- `dtCrowdAgentParams::useObb` and `obb[3*4]` — used by `AddOrUpdateVehicle`
  (Navigation.cpp:534-660) to inject vehicles as oriented-bounding-box obstacles
  the crowd's separation step avoids
- `dtCrowdAgent::paused` — toggled by `WalkerManager` while a walker is stopped
  at a crosswalk
- `dtCrowdAgent::dead` — set when the agent has been killed by a vehicle (so
  the client can despawn the actor)
- `dtCrowd::hasVehicleNear(idx, distSq, dir, setAgentLookAt)` — proximity check
  used by `WalkerEventStopAndCheck` and `Navigation::HasVehicleNear`
- `dtCrowd::pauseAgent` — convenience setter

**Mitigation:** reimplement these on top of stock DotRecast, **outside** the
crowd object:

| CARLA-patched feature | C# replacement |
|---|---|
| `agent->paused = true` | `Navigation.PausedAgents : HashSet<int>` checked before issuing `RequestMoveTarget`; when paused, skip the agent in our per-tick walker batch (do not emit `ApplyWalkerState`) |
| `agent->dead` | `Navigation.DeadAgents : HashSet<int>` — set when a CollisionEvent sensor (or upstream kill condition) flips it; `IsWalkerAlive` reads from here |
| `useObb` / `obb[12]` (vehicle obstacles) | Track vehicles in `VehicleObbTracker` (`Dictionary<ActorId, OrientedBoundingBox>`). Each tick, BEFORE `crowd.Update`, walk every walker's neighborhood (use a `DtProximityGrid` if perf demands) and **push each walker's position outwards** from intersecting OBBs — a manual repulsion. This replicates the separation-step behaviour without modifying DotRecast. Stub initially; verify visually whether walkers visibly avoid vehicles (they do in upstream); if not, fall back to a fork of DotRecast |
| `hasVehicleNear` | `VehicleObbTracker.AnyVehicleNear(walkerPos, distSq, dir)` — simple bbox + direction-dot-product scan |

**Backup plan if the OBB workaround proves insufficient:** vendor DotRecast
source (its license permits this — ZLib) under `CarlaNet/src/DotRecast/` and
re-apply the CARLA patches. Adds ~5,000 LoC to the maintenance surface but
guarantees exact behavioural match. Estimate: +1 day for one implementer.

### R2. Navmesh tile-ref width (32-bit vs 64-bit) and `#pragma pack(1)` interaction

**Probability MEDIUM · Impact HIGH if we get it wrong (silent corruption — the
mesh "loads" but produces wrong paths).**

CARLA builds recastnavigation without `DT_POLYREF64`, so `dtTileRef =
unsigned int` (4 bytes). The CARLA blob has `#pragma pack(push, 1)` so the
`NavMeshTileHeader` is exactly 8 bytes (no alignment padding). DotRecast's
default `DtMeshSetReader.Read` assumes `tileRef = long` (8 bytes), which would
mis-align everything. **Must call `Read32Bit`** (§3.2 note 1). Mitigation: a
unit test that round-trips a known-good `.bin` file (grab one from
`Build/Package/Carla-*-Win64-Shipping/CarlaUnreal/Content/Carla/Maps/Nav/`
during dev) through the loader and asserts:
- `nav_mesh.GetTileCount() > 0`
- A `FindNearestPoly` at a known on-mesh location returns a non-zero polyRef
- A `FindRandomPoint` returns a finite vector

### R3. UE5 walker physics + collisions disabled by `WalkerAIController::Start`

**Probability MEDIUM · Impact MEDIUM.**

Upstream relies on `SetActorSimulatePhysics(walker, false)` +
`SetActorCollisions(walker, false)` so Detour can drive position via
`ApplyWalkerState`. UE5.7 walker pawns may behave differently than UE4.26
(the C++ port lineage). If the walker doesn't visually move despite Detour
producing valid transforms, the issue is server-side actor physics — out of
scope for this port but blocks verification. Mitigation: `verify.skill` step
checks "spawn 1 walker + start AI + sample position 5× over 5 s; assert non-zero
delta". If broken, the C# nav code is correct but the server actor handling
needs a separate fix.

### R4. DotRecast API surface mismatch (filter mutation, custom areas, FindPath signature)

**Probability MEDIUM · Impact LOW.**

DotRecast tracks recast4j which has some signature drift from upstream
recastnavigation. Examples already identified:

- `IDtQueryFilter` is interface-only — area-cost mutation requires
  constructor-time injection or subclassing (§2.2). CARLA's per-call
  `dtQueryFilter` allocation maps cleanly to a cached `CarlaQueryFilter`
  instance per (includeFlags, excludeFlags, costs) tuple.
- `FindPath` returns a `DtStatus` (enum-bitmask) and writes the path into a
  `List<long>` rather than a fixed `dtPolyRef[]`. Easier than C++.
- `FindStraightPath` writes a `List<DtStraightPath>` — each entry carries `pos,
  flags, refs` so we don't need three parallel arrays.

Mitigation: implementer reads each DotRecast call site in
`src/DotRecast.Detour.Test/FindPathTest.cs` (and crowd tests) to confirm
signatures before writing equivalent C# in `Navigation.cs`.

### R5. `get_navigation_mesh` RPC vs `get_required_files`/`get_cache_file` path

**Probability LOW · Impact LOW.**

Upstream `WalkerNavigation` uses `GetRequiredFiles("Nav") + GetCacheFile`. We
have `GetRequiredFilesAsync` but not `GetCacheFileAsync` yet (RPC `get_cache_file`).
CarlaNet does have `GetNavigationMeshAsync` which returns the navmesh bytes
directly — this works equally well as a one-shot. Decision: **use
`GetNavigationMeshAsync`** for the initial port; skip implementing
`GetCacheFile`. Drop-back: if the server returns empty bytes for some maps
(it shouldn't), fall through to required-files + manual download.

---

## 11. Verification plan

Add `CarlaNet/test/test_nav_walkers.py`, analogous to the existing
`test_tm_motion.py` pattern. Run against a live CARLA 0.10 PIE in sync mode
@ 20 Hz.

### 11.1 Test script structure

```python
"""
test_nav_walkers.py
Spawn N walkers + AI controllers, register destinations, sample positions
each second for T seconds, assert each walker has moved at least D meters
toward its goal (or has reached a stable end state).

Pass criteria:
  - >= 80% of walkers move > 2.0 m within 10 s
  - 0 walker actor IDs become invalid (no implicit destruction)
  - World.get_random_location_from_navigation() returns non-None at least
    once per call for 5 consecutive calls (proves navmesh load worked)
"""

import time
import math
import carlanet as carla

NUM_WALKERS = 10
DURATION_S  = 15
MIN_DISPLACEMENT_M = 2.0

def main():
    client = carla.Client("localhost", 2000)
    client.set_timeout(20.0)
    world = client.get_world()

    # Sync mode
    settings = world.get_settings()
    original = (settings.synchronous_mode, settings.fixed_delta_seconds)
    settings.synchronous_mode = True
    settings.fixed_delta_seconds = 0.05
    world.apply_settings(settings)

    # Smoke: navmesh load worked
    rand_locs = [world.get_random_location_from_navigation() for _ in range(5)]
    assert all(loc is not None for loc in rand_locs), "navmesh not loaded"

    blueprints_w = world.get_blueprint_library().filter("walker.pedestrian.*")
    controller_bp = world.get_blueprint_library().find("controller.ai.walker")

    # 1. Spawn walkers at random nav locations
    walker_actors = []
    for i in range(NUM_WALKERS):
        loc = world.get_random_location_from_navigation()
        if loc is None: continue
        bp = blueprints_w[i % len(blueprints_w)]
        if bp.has_attribute("is_invincible"):
            bp.set_attribute("is_invincible", "false")
        actor = world.try_spawn_actor(bp, carla.Transform(loc))
        if actor is not None:
            walker_actors.append(actor)

    # 2. Spawn + start controllers
    controller_actors = []
    for w in walker_actors:
        c = world.spawn_actor(controller_bp, carla.Transform(), attach_to=w)
        controller_actors.append(c)
    world.tick()
    for c in controller_actors:
        c.start()
        dest = world.get_random_location_from_navigation()
        c.go_to_location(dest)
        c.set_max_speed(1.4)

    # 3. Sample positions per second
    start_positions = {w.id: w.get_location() for w in walker_actors}
    for t in range(DURATION_S):
        for _ in range(20):  # 1 s / 0.05 = 20 ticks
            world.tick()
        # Optional: log positions

    # 4. Assert displacement
    moved = 0
    for w in walker_actors:
        s, e = start_positions[w.id], w.get_location()
        dx, dy = e.x - s.x, e.y - s.y
        if math.hypot(dx, dy) >= MIN_DISPLACEMENT_M:
            moved += 1
    pct = moved / len(walker_actors)
    print(f"walkers moved {moved}/{len(walker_actors)} ({pct*100:.0f}%)")
    assert pct >= 0.80, f"only {pct*100:.0f}% of walkers moved"

    # 5. Teardown
    for c in controller_actors: c.stop()
    client.apply_batch([carla.command.DestroyActor(x) for x in controller_actors])
    client.apply_batch([carla.command.DestroyActor(x) for x in walker_actors])

    settings.synchronous_mode, settings.fixed_delta_seconds = original
    world.apply_settings(settings)

if __name__ == "__main__":
    main()
```

### 11.2 Acceptance criteria

| Check | Pass condition |
|---|---|
| Build | `dotnet build CarlaNet.sln` → 0 errors / 0 warnings |
| Unit tests | All existing tests still pass + new `NavTests` (load known navmesh, `FindRandomPoint` returns non-NaN) |
| Smoke | `python test_nav_walkers.py` against PIE → exits 0, ≥ 80% walkers moved ≥ 2 m in 15 s |
| Regression | `python generate_traffic_carlanet.py -w 30 -n 10` runs 60 s without unhandled exceptions; walkers visible moving in PIE |
| Manual visual | Spawn 50 walkers near a busy intersection, observe `WalkerEventStopAndCheck` behaviour at the crosswalk |

### 11.3 Performance target

50 walkers + 30 vehicles @ 20 Hz sync mode on the same dev box that runs the
TM tests today. Walker subsystem CPU budget ≤ 5 ms / tick (it's not on the
critical path the way the TM is — `Navigation.UpdateCrowd` is the bulk and
DotRecast's crowd update is O(agents²) but with the proximity grid the
constant is small).

---

## End of spec.
