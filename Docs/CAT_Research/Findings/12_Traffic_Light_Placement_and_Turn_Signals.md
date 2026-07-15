# 12 — Traffic-Light Pole Placement & Turn-Arrow Signals

**Status:** Implemented (pole placement) + research/future-option (turn arrows). 2026-07-14.
**Scope:** How injected traffic lights (doc 10, `TrafficLightInjector`) are rendered in the digital-twin
world — pole count, position, and facing — and whether protected-turn (arrow) signal phases can be
represented. Grounded in editor (VibeUE) inspection of the CARLA traffic-light blueprints and meshes,
and the netconvert/SUMO phase data.
**Related:** [10 — Intersection Navigation & Traffic Control](10_Intersection_Navigation_Traffic_Control.md)
(the injectors and the grouping fix), `sbrett9/carla#7`.

---

## 1. Pole count vs. the mesh

Initial symptom (SF_LaurelHeights): each signalized junction showed **several poles standing in the road,
one light each**, rather than one mast-arm pole at the corner with several heads on the beam — and the
heads faced the wrong way.

The spawned blueprint `BP_TLOpenDrive_RHT` is a mast-arm assembly — a vertical pole
(`SM_TrafficLight__VPole_Main`, base at Z=0), a horizontal arm (`SM_TrafficLight__HPole01_Long1`) — but
it carries **one signal head per boom**: the ~12 `SM_BackPlate_*` components are the modular *segments of
that single head's backplate*, not separate heads. (Town10HD's `BP_TrafficLightNew_T10` clearly mounts
several distinct heads on one arm; adopting or spawning a multi-head boom is a future mesh option — it is
hardcoded in `ATrafficLightManager::SpawnTrafficLights`, so it would need an engine change.)

The primary cause of the "several poles in the road" look: `ATrafficLightManager::SpawnTrafficLights`
spawns **one entire `BP_TLOpenDrive` per `<signal>`**, and netconvert emits **one `<signal>` per
head/movement** (verified on SF: all heads `orientation="+"`, no `hOffset`, `t` from −1.7 to −8.4 m
spread across the road, `zOffset="5"`). So an approach with N heads produced N full mast-arm assemblies
stacked across the road.

## 2. Pole placement — implemented (`TrafficLightInjector`)

The fix is a data transform in the injector (no engine change), applied to the traffic-light `<signal>`s:

1. **One pole per approach.** Group heads by approach (parent road + rounded stop-line station) and keep
   only the **roadside-most head** (largest `|t|`) as the representative pole; drop the rest and prune
   the controller references. The surviving pole's own mast arm and backplates then provide the
   multiple-heads-over-the-lanes look. Trade-off: per-head protected-turn arrows are lost (see §3); the
   pole shows the approach's through phase (the head is assigned to the first phase it is green in).
2. **Far-side, roadside placement.** A real signal sits across the intersection from the driver who
   obeys it, at the curb. Using the parsed road geometry, the pole is placed in two components:
   - *forward* — the approach's road tangent (`GetDirectedPointInNoLaneOffset(...).Tangent`) is oriented
     toward the junction, and the pole is stepped to the far side. The distance is measured from the
     junction's **connecting roads**: each `<connection>` is an internal road tracing one movement (this
     approach → an exit), and the farthest any of *this approach's own* connecting roads reaches along the
     travel direction is where the approach leaves the intersection — the true far side (`FarExitDistance`,
     + a small crosswalk clearance). Using only the approach's own connecting roads keeps this correct on
     **clustered junctions** (`cluster_…_#Nmore`, several merged OSM nodes), where a whole-junction
     centroid would measure across the entire blob. A capped reflection through the junction centroid
     (`min(2·|centre−stopline|, 35 m)`) remains as the fallback when the connecting roads don't resolve.
   - *lateral* — the pole is seated just beyond the drivable edge, at `RoadEdgeDistance` (sum of the lane
     widths on the head's side) + a sidewalk margin, on the head's `t` side. This matters twice over:
     `GetDirectedPointInNoLaneOffset` returns the **centreline** point (so an unoffset pole sits mid-road),
     and the head's own `t` is *mid-lane* (so reusing it drops the pole in the roadway / crosswalk box).

   Emitted as `<positionInertial x y z hdg>` (absolute), which CARLA honours (`SignalParser.cpp`,
   `MapBuilder::AddSignalPositionInertial` sets `_using_inertial_position` and skips the s/t path); the
   inertial frame is planView (+Y=North) and CARLA flips Y / negates hdg internally. `hdg` faces back
   toward the oncoming approach (the tangent-toward-junction heading renders as facing the driver —
   confirmed empirically). Falls back to near-side road-relative placement when geometry is unavailable.
3. **Height.** `zOffset` is zeroed (netconvert's 5 m floated the pole, since the BP models the mast from
   its base); near-side fallbacks get `hOffset=π` to face oncoming.

**Known refinements (visual-tuning, not correctness):**
- The **lateral side** uses the head's own `t` sign, which puts the pole at the approach's own curb; if a
  pole lands on the wrong corner it is a sign convention to confirm against the running world.
- The **forward measure** uses connecting-road *centreline* extent; the very far edge is ~half a lane
  beyond, covered by the crosswalk-clearance constant.
- **Remaining higher-fidelity option (not done): true curb-corner detection.** Order the roads meeting a
  junction angularly and intersect adjacent outer-edge lines to get the actual curb-return corner, then
  seat the pole exactly there (no lateral guess). More correct on skewed intersections, but brittle on
  merged clusters (many roads, ambiguous "which corner"); the connecting-road far-exit above degrades
  gracefully there, which is why it was chosen first.

## 3. Turn-arrow (protected-movement) signals — future option, not currently feasible

Real intersections show a **green left-turn arrow** for a protected left while the through movement is
red. Representing that faithfully was considered and is **not achievable without significant custom
work**, for two independent reasons:

- **The data exists.** netconvert's `<tlLogic>` phase strings distinguish every movement and even encode
  protected (`G`) vs permissive (`g`) green, and `<connection tl linkIndex>` ties each link to a
  from/to movement. So which link is a left turn, and when it is protected-green, is known.
- **The engine state model does not support it.** CARLA's traffic-light state is a single enum —
  `Red / Yellow / Green / Off` (`carla::rpc::TrafficLightState`). There is no "green arrow" state, and
  `UTrafficLightComponent` can only push one of those four. The spawned `BP_TLOpenDrive` uses round
  3-light modules (`SM_TrafficLights_Module_01/02`), not arrow heads.
- **Assets do exist** if this is ever pursued: `AmericanLightsTurn`, `BP_TurnBasedStop`, and green/amber/
  red turn-arrow icons (`.../TrafficLights2025/TrafficLight_Icons/uturnflecha{verde,ambar,roja}`).

So a faithful protected-turn signal would require: a **new/extended blueprint** with arrow heads, and
**custom state-driving logic that goes around CARLA's four-state model** to light an arrow independently
of the through head. There is also a modelling snag even then: collapsing an approach that has a
protected-left phase into one head is ambiguous (the left can be green while the through is red — one
head cannot show both), so true arrows require keeping the turn head as a **separate** pole/head, not the
one-pole-per-approach simplification of §2.

**Recommendation:** for a high-altitude EO digital twin, individual signal heads are effectively
sub-pixel and vehicle *behavior* (stopping) is what matters, so the one-pole-per-approach through-phase
model in §2 is sufficient. Protected-turn arrow rendering is logged here as a scoped future effort:
new arrow blueprint + custom arrow-state handling + per-turn-head placement.
