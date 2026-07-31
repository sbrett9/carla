# 19 — Turn-Restriction Obedience for Ambient Traffic

**Status:** Research / source audit. No code changed and no runtime measurement taken; every claim below
is read from the sources cited.
**Date:** 2026-07-30
**Scope:** Whether ambient traffic in this fork can be made to obey OpenStreetMap turn restrictions
(no-left-turn, no-U-turn and the like), which it does not today; where a movement through a junction is
actually chosen; what the CARLA road model can represent; the honest cost of the two routes to obedience;
and how obedience would be verified.
**Assumed fixed, not re-derived here:** the clip step that trims an OpenStreetMap extract to the sandbox
boundary rebuilds the document from bounds, nodes and ways only and emits no `<relation>` elements
(`CarlaNet/python/osm_clip.py:131-146`), so turn-restriction relations never reach the converter. That
data loss is tracked separately; this document is about what happens once it is fixed.
**Resolved since drafting:** prohibited movements *are* absent from the exported OpenDRIVE junction
records, so the structural route of §3.1 is the live one. Detail and the one exception are in §6.1.
**Related:** [10 — Intersection Navigation & Traffic Control](10_Intersection_Navigation_Traffic_Control.md)
(which this extends) · [18 — Scenario Fabrication for Pattern-of-Life Model Training](18_Scenario_Fabrication_For_EPoL_Training.md) ·
[09 — Telemetry CoT Contract](09_Telemetry_CoT_Contract.md)

---

## 1. How a movement is chosen at a junction today

The .NET Traffic Manager has no route planner. At a junction the next movement is a **uniform random
index into a precomputed successor list**: `LocalizationStage` takes the furthest waypoint in the
vehicle's buffer, reads its successors, and draws one (`Stages/LocalizationStage.cs:322-350`, successors
at `:325`, draw at `:332-334`). No property of the movement is consulted — not its turn direction, not a
sign, not a junction record.

That list is not computed per tick. It is the dense waypoint graph built once by `InMemoryMap.SetUp`,
whose only two sources are the chain along a lane (`InMemoryMap.cs:359-378`) and the road network's
lane-link graph, read straight off `lane.NextLanes` / `lane.PreviousLanes` (`:496-509`) — a 5-metre
resampling of the road model's own topology (`Constants.cs:148`), containing nothing the road model did
not supply.

The `RoadOption` label (Left / Right / Straight) is derived **after** the graph exists, geometrically,
from the yaw change across each junction path against a 19° straight-ahead threshold
(`InMemoryMap.cs:689-700`; `Constants.cs:157`). It describes a movement, never gates one — but it is the
exact quantity a restriction check would need, and it already exists.

Prescribed routes narrow that same list rather than extending it: a commanded path picks the successor
whose junction exit lies nearest the next commanded location (`LocalizationStage.cs:631-711`, selection
at `:664-675`); a commanded action sequence picks the first successor whose `RoadOption` matches the next
action (`:739-776`, `:749-756`). Both index `GetNextWaypoint()`.

**The movement is therefore selected from a topology derived from the road network's junction
connections, and from nothing else.**

## 2. What the road model represents

A junction is a set of `<connection>` records, each naming an incoming road and a connecting road, with
per-lane `<laneLink>` pairs (`LibCarla/source/carla/road/Junction.h:26-46`;
`CarlaNet.Map/Road/Junction.cs:13-30`). The parser reads `id`, `incomingRoad`, `connectingRoad`, the lane
links and the controller list — and no other attribute (`opendrive/parser/JunctionParser.cpp:50-95`;
`CarlaNet.Map/OpenDrive/Parser/JunctionParser.cs:25-38`).

Successors across a junction are enumerated by walking those records: `GetJunctionLanes`
(`CarlaNet.Map/Road/MapBuilder.cs:639-670`) iterates the junction's connections, keeps connecting roads
whose predecessor or successor is the incoming road, matches lane predecessor/successor ids, and the
result becomes `lane.NextLanes` (`MapBuilder.cs:556-565`). The engine is identical
(`road/MapBuilder.cpp:709-746`); the client-facing successor query reads the same field
(`road/Map.cpp:521-545`).

**A movement absent from the junction records is simply unreachable in the resulting graph.** Two
independent records must name it — the `<connection>` and the connecting road's own `<link>` — and
deleting either removes it.

The junction *surface* is built from the same records. Non-junction roads are meshed directly
(`Map.cpp:1123-1133`); junction interiors are meshed by iterating the junction's connections
(`:1141-1153`), the path runtime-generated worlds take (`Carla/OpenDrive/OpenDriveGenerator.cpp:81`). So
removing a connection removes its ribbon of asphalt with the movement, keeping graph and geometry
consistent by construction; the connecting road itself must stay in the file, since the mesh path looks
it up unchecked (`Map.cpp:1143`).

The model has **no field for a prohibition**. Turn-restriction sign codes exist in the OpenDRIVE type
table — 209 mandatory direction, 211, 214, 272 no U-turn (`road/SignalType.h:25-38`;
`CarlaNet.Map/Road/SignalType.cs:17-30`) — but nothing consumes them; the only types wired to a spawned
actor or to any behaviour are stop, yield, traffic light and maximum speed
(`Traffic/TrafficLightManager.cpp:418-435`). A restriction expressed as a sign would render as nothing
and mean nothing.

## 3. The two routes to obedience

### 3.1 Structural — the prohibited movement never exists

Conversion emits no junction connection for it, the lane-link graph never contains it, the waypoint graph
never offers it, and **no Traffic Manager code changes at all**. Cost sits in conversion, plus two
hazards in the graph builder:

- **A lane whose every movement is prohibited becomes a dead end.** `SetUpRoadOption` marks a
  successor-less waypoint `RoadEnd` (`InMemoryMap.cs:666-670`), `LocalizationStage` flags the vehicle for
  removal (`:336-339`, `:678-682`, `:758-762`), and `ALSM` destroys it in the
  OpenStreetMap-derived-world mode that is on by default (`Stages/ALSM.cs:197-208`; `Parameters.cs:420`).
- **An emptied lane can silently regain the movement.** `PatchIsolatedNexts` gives a successor-less
  waypoint the successors of its lane-change neighbour (`InMemoryMap.cs:644-658`), so a restriction that
  empties one lane of a multi-lane approach hands that lane the prohibited successor from the lane beside
  it. This is the one place the structural route fails quietly.

### 3.2 Rule-based — restrictions carried as data, checked at selection

A table keyed on (junction, incoming road, incoming lane) yielding forbidden connecting roads, consulted
at the three selection sites (`LocalizationStage.cs:325`, `:635`, `:744`). Costs are additive: the
OpenDRIVE cannot carry the table (§2), so it needs a sidecar file or a non-standard extension plus parser
changes; a filtered-successor helper runs per vehicle per tick on the hot path; a fallback is needed when
filtering empties the set; and the junction keeps geometry for a movement nothing uses. What it buys is a
per-vehicle override, in the manner of the existing running-a-red-light and running-a-sign percentages
(`Parameters.cs:196-199`, consumed at `Stages/TrafficLightStage.cs:151`, `:175`).

### 3.3 Structural is preferable

It adds no runtime cost and no new state. The road model has nowhere to put a rule, so the rule-based
route needs a parallel data channel that the world-binding contract of
[18 §5.5](18_Scenario_Fabrication_For_EPoL_Training.md) would then also have to cover, or a recorded run
replays against a world carrying different restrictions. Structural removal is agreed to automatically by
every consumer of the road graph — ambient traffic, the junction mesh, any future client waypoint surface
— not just the one that remembers to run a check. And obedience here is not stochastic the way running a
red light is, so the per-vehicle override is worth little for ambient traffic.

## 4. Authored scenarios that deliberately depict the illegal manoeuvre

**Motion prescribed outside the Traffic Manager is unaffected by either route.** The stage pipeline runs
only over the registered-vehicle set (`TrafficManagerLocal.cs:348-354`), so an unregistered vehicle is
never localized on the waypoint graph and receives no control
([18 §4.3](18_Scenario_Fabrication_For_EPoL_Training.md)); neither a missing junction connection nor a
restriction table can constrain it. A scenario staging a left turn across a no-left-turn sign drives the
vehicle directly, exactly as it stages a long dwell.

Traffic-Manager-mediated prescription is not the same thing, and the distinction is easy to lose. A
commanded path and a commanded action sequence (`Parameters.cs:249`, `:279`) both resolve against
`GetNextWaypoint()` (§1), so under the structural route a Traffic-Manager-driven vehicle cannot be routed
through a removed movement, whereas the rule-based route could permit it by override. A deliberate
violation is therefore realised the way a dwell is, by taking the vehicle out of Traffic Manager control
for its duration — a property of prescribed motion generally, not a constraint introduced here.

## 5. Verification

| Check | Method | Pass condition |
|---|---|---|
| **Graph** (per build) | Re-run the `GetJunctionLanes` enumeration (`MapBuilder.cs:639-670`) against the built graph — not the file alone, so §3.1's neighbour-inheritance hazard is covered — classifying turn direction with the yaw arithmetic and 19° threshold the graph builder itself uses (`InMemoryMap.cs:689-700`) | Zero enumerated movements that the source relations prohibit |
| **Behaviour** (runtime) | Ambient traffic over a fixed tick budget on an extract with a known restriction; gate polygons on the approach and each exit, scored from the Cursor-on-Target truth stream ([09](09_Telemetry_CoT_Contract.md)). Classification must be geometric, the client having no lane query ([10 §4](10_Intersection_Navigation_Traffic_Control.md)) | Zero traversals of the prohibited exit **and** many of that junction's permitted exits in the same run — absence proves nothing without the control |
| **Non-regression** | Junction surface as rendered (§2); vehicles destroyed at graph dead ends (§3.1); stock Town10HD, which shares the road-graph code | No visible gap; no rise in dead-end destructions; Town10HD unchanged |

## 6. Open dependencies

1. **Whether restrictions reach the OpenDRIVE as removed junction connections — settled, and they do.**
   A prohibited movement is deleted from the connection set during conversion rather than flagged
   (`applyRestriction`, `Build/sumo-src/src/netimport/NIImporter_OpenStreetMap.cpp:2927-2938`, calling
   `removeFromConnections`), and the OpenDRIVE writer builds junction content solely from the surviving
   connections (`src/netwrite/NWWriter_OpenDrive.cpp:188`). It enumerates no "all possible movements" set.
   **§3.1 is therefore the live route**, and the remaining work is the two graph-builder hazards plus §5.

   One class of restriction escapes this. A relation carrying `except=` — no left turn *except* buses —
   retains its connection and only narrows the permissions on it (`:2931`), and the OpenDRIVE lane type
   is derived from the outgoing edge rather than from the connection
   (`NWWriter_OpenDrive.cpp:541`), so the movement is written as an ordinary connecting road and appears
   fully permitted. Excepted restrictions are consequently invisible to the structural route and would
   need the rule-based treatment of §3.2 if they ever matter. Restrictions routed via a way rather than a
   node are unimplemented in the converter irrespective of any of this (`:2939-2943`).

   Had the answer gone the other way, the movements could have been pruned after conversion: the pipeline
   already writes the SUMO network file alongside the OpenDRIVE (`CarlaNet.Map/OsmConverter.cs:282-283`)
   and already rewrites the OpenDRIVE afterwards, `SignInjector` and `TrafficLightInjector` being working
   precedents (`CarlaNet.Transport/CarlaClient.cs:505`, `:514`) — the latter already harvesting from that
   network file (`OpenDrive/TrafficLightInjector.cs:49`). That option remains available for the excepted
   class above.
2. **How much restriction data the fork's working extracts carry.** Unmeasured, and it bounds the visible
   benefit of either route.
3. **Whether removing a ribbon leaves a visible gap in the junction surface.** Not settled by the
   sources: the surface is the union of the remaining ribbons (§2), and whether the removed one was
   redundant depends on junction geometry. Needs a rendered comparison.
