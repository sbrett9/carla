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
(`SM_TrafficLight__VPole_Main`, base at Z=0) and a horizontal arm (`SM_TrafficLight__HPole01_Long1`) —
and it shipped carrying **one signal head per boom**. The ~12 `SM_BackPlate_*` components are the modular
*segments of that single head's backplate*, not separate heads.

The primary cause of the "several poles in the road" look: `ATrafficLightManager::SpawnTrafficLights`
spawns **one entire `BP_TLOpenDrive` per `<signal>`**, and netconvert emits **one `<signal>` per
head/movement** (verified on SF: all heads `orientation="+"`, no `hOffset`, `t` from −1.7 to −8.4 m
spread across the road, `zOffset="5"`). So an approach with N heads produced N full mast-arm assemblies
stacked across the road. §2 collapses that to one pole per approach; §2.1 puts the heads back on its arm.

## 1.1 The blueprint is data-driven — the head count is just an array length

`BP_TLOpenDrive_RHT` builds its heads procedurally, which is what makes a multi-head boom a *data*
change rather than new geometry. Its `Heads` array (of `ST_TrafficLightHead`: `Description`, `Position`
transform, `Lights[]` of mesh + relative transform + colour enum, and an optional `Support` mesh) is
walked by the Construction Script, which for each entry adds the head, builds its three lamp modules,
creates their dynamic material instances, and registers them into the `RedLights`/`YellowLights`/
`GreenLights` arrays that the EventGraph's `LightChanged` drives. **Any head added to the array is
therefore automatically state-driven** — no wiring required. It simply shipped with a single entry.

Town10HD's `BP_TrafficLightNew_T10_master_largeBIG_rsc` is the proof and the reference: five entries
(two vehicle heads on one arm at X=−930 and −550, a pedestrian head, two street signs), and only eight
components. Note its lamps (`SM_TrafficLight_Signal01_A`) are self-contained housings, whereas
`BP_TLOpenDrive`'s are bare lens modules (`SM_TrafficLights_Black_Module_01`, 27×30×26) whose backplate
is a separate assembly of 1-unit-thick panels — see the deferred item in §2.

## 2. Pole placement — implemented (`TrafficLightInjector`)

The fix is a data transform in the injector (no engine change), applied to the traffic-light `<signal>`s:

1. **One pole per approach.** Group heads by approach (parent road + rounded stop-line station) and keep
   only the **roadside-most head** (largest `|t|`) as the representative pole; drop the rest and prune
   the controller references. Trade-off: per-head protected-turn arrows are lost (see §3); the pole shows
   the approach's through phase (the head is assigned to the first phase it is green in).

   **The survivor must inherit the dropped heads' `<validity>`.** CARLA builds one stop-line trigger box
   *per lane listed in a signal's validity* (`TrafficLightComponent::InitializeSign` walks
   `GetValidities()` → `Map.GetWaypoint(RoadId, lane, GetS())`, placing the box at
   `s − (BoxLength + AdditionalDistance)`, ~3 m before the line). netconvert scopes each head's validity
   to the single lane it hangs over (`<validity fromLane="-2" toLane="-2"/>`), so collapsing the heads
   without merging validity leaves every other lane of the approach **with no trigger box at all** — that
   traffic never registers the light and drives straight through it, while the one surviving lane still
   stops. The symptom reads like a timing bug and is actually lane coverage; the diagnostic that settles
   it is *which lane* a misbehaving vehicle is in. The survivor's validity is therefore set to the union
   (min/max) of its approach's heads' lanes; `Math::GenerateRange(a, b)` accepts either direction.
   Regression-tested by `Inject_CollapsingHeads_KeepsValidityOverEveryLaneOfTheApproach`.
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

## 2.1 One head per lane on the arm — implemented

With one pole per approach, the arm carries one head per **driving lane of that approach**, as a real
mast arm does. Because the blueprint is data-driven (§1.1), this needed only a head count plus array
entries:

- **C++** — `ATrafficLightBase::NumSignalHeads` (a `BlueprintReadOnly` UPROPERTY) is set by
  `ATrafficLightManager::SpawnTrafficLights` from `CountDrivingLanesOnSide()`, which counts the driving
  lanes on the same side of the road as the signal's lane. It is applied with
  `FActorSpawnParameters::bDeferConstruction` + `FinishSpawning`, so the value is in place **before** the
  Construction Script runs. It defaults to 1, so hand-placed lights that no manager configures are
  unaffected.
- **Blueprint** — `Heads` holds three entries at X = −640, −305, −975 (one lane, 335 units, apart). The
  Construction Script gates each on `Array Index < NumSignalHeads`, so a pole builds only the heads its
  approach has lanes for. Head [0] keeps its original X=−640, so a one-head pole is identical to the
  pre-existing asset — including its backplate.
- **Arm length** — the arm mesh spans 504.4 local units at X=−235.7, rotated 180°, so its reach is
  `−235.7 − 504.4·scale` (−622.8 at the shipped 0.7675 scale, matching the end cap at −628.9). Two heads
  fit that arm; three do not, so a second gate (`2 < NumSignalHeads`) scales the arm to 1.4437 and moves
  the end cap to −963.9 for three-head poles only.

Verified by spawning actors at each count: N = 1/2/3 → 3/6/9 lamp modules, with the arm extending only at
N = 3. SF_LaurelHeights yields 34 one-head, 24 two-head, and 13 three-head poles.

**Deferred (cosmetic):** added heads carry no backplate — the 11 `SM_BackPlate_Black_*` panels (1 unit
thick, purely the visibility panel behind the head) are static components at head [0] only, and the lamps
are self-contained, so extra heads render as proper 3-lamp heads without them. The boom-to-head junction
also reads awkwardly. `BP_TLOpenDrive_LHT` (left-hand traffic) is untouched and still single-head.

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
