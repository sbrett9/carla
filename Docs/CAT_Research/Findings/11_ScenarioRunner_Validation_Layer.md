# 11 — ScenarioRunner as a Validation & Scripting Layer

**Status:** Research / forward-looking note. No code changed; ScenarioRunner not yet integrated.
**Date:** 2026-06-25
**Scope:** Record what `carla-simulator/scenario_runner` is, how it relates to the Traffic Manager, and
the concrete API-parity prerequisites for using it against this fork's CarlaNet pipeline — so the option
is on record for when OSM-derived intersection behavior lands.
**Related:** [10 — Intersection Navigation & Traffic Control](10_Intersection_Navigation_Traffic_Control.md)
(the gaps this doc depends on); `Docs/adv_traffic_manager.md`.
**Upstream:** <https://github.com/carla-simulator/scenario_runner> (versioned lock-step with CARLA, e.g.
SR v0.9.16 ↔ CARLA 0.9.16).

---

## 1. What ScenarioRunner is

A **traffic-scenario definition and execution engine** for CARLA. It is the scripted, deterministic,
*repeatable* counterpart to the Traffic Manager's emergent ambient traffic: a defined ego task, specific
other actors triggered at specific points, and automatic **pass/fail criteria**. It is a
testing/validation harness, not a traffic populator.

| | Traffic Manager | ScenarioRunner |
|---|---|---|
| Role | Populates background AI traffic | Orchestrates a specific test situation |
| Behavior | Emergent, stochastic | Scripted, deterministic, triggered |
| Control | Per-vehicle speed/lane/ignore-% knobs | py_trees behavior trees of explicit maneuvers |
| Output | Just driving | Driving **+ success/failure metrics** |

## 2. Relationship to the Traffic Manager

Complementary, and ScenarioRunner **uses the TM as a building block.** Its `background_activity.py`
(~116 KB, the largest scenario file) is a managed ambient-traffic layer around the ego route; most
scenarios hand the "filler" actors to TM autopilot and script only the *challenge* actor (the one that
cuts in, runs the red, jaywalks) via behavior trees. A typical run = TM-driven background + SR-scripted
hero event + SR-evaluated criteria.

## 3. How scenarios are defined

- **Python scenarios** — subclasses of `basic_scenario.py`, composed as py_trees behavior trees (atomic
  behaviors + trigger conditions + criteria).
- **OpenSCENARIO 1.x** — ASAM XML standard, parsed by `open_scenario.py` (the interoperable,
  tool-agnostic path).
- **OpenSCENARIO 2.0** — a DSL, in `osc2_scenario.py`.
- **Route-based** — `route_scenario.py`: a long ego route with scenarios sprinkled along it; this is the
  format the **CARLA Autonomous Driving Leaderboard** uses (the former "CARLA Challenge" moved there).

## 4. What it ships / was intended for

ScenarioRunner's purpose is to **regression-test an autonomous-driving stack** against a fixed battery of
safety-critical situations with reproducible pass/fail, and to benchmark agents on a leaderboard. Real
shipped scenarios (`srunner/scenarios/`), grouped:

- **Intersection / signal protocol** (directly aligned with [doc 10](10_Intersection_Navigation_Traffic_Control.md)):
  `signalized_junction_left_turn.py`, `signalized_junction_right_turn.py`,
  `no_signal_junction_crossing.py`, `opposite_vehicle_taking_priority.py`, `green_traffic_light.py`,
  `blocked_intersection.py`.
- **Hazards / reactions:** `follow_leading_vehicle.py`, `hard_break.py`,
  `cut_in.py` / `highway_cut_in.py` / `parking_cut_in.py`, `control_loss.py`,
  `construction_crash_vehicle.py`, `object_crash_intersection.py` / `object_crash_vehicle.py`,
  `maneuver_opposite_direction.py`, `invading_turn.py`, `vehicle_opens_door.py`.
- **Vulnerable road users / special:** `pedestrian_crossing.py`, `cross_bicycle_flow.py`,
  `yield_to_emergency_vehicle.py`.

## 5. Bringing it to bear in this fork — the prerequisite

ScenarioRunner is written against the **upstream `carla` Python API and the in-engine C++ Traffic
Manager**. This fork runs **CarlaNet + the Python shim**, and the surface SR depends on is exactly the
surface [doc 10](10_Intersection_Navigation_Traffic_Control.md) found missing or stubbed:

| SR dependency | State in this fork (per doc 10) |
|---|---|
| Waypoint / map navigation (`Map.get_waypoint`, `get_topology`, junctions, lanes) | **Absent** from the shim — SR uses this pervasively to place triggers and route actors |
| Traffic-light query/control (`get_traffic_light`, `set_state`, `freeze`) | C# RPCs exist but **unbound** in the shim; the .NET TM hardcodes green |
| Speed limit (`get_speed_limit`) | C# RPC exists, **unbound** in Python |
| `set_path` / `set_route`, light-state, collision/obstacle sensors | **Partially present** in the shim |

So SR cannot run against this fork today. It becomes viable only after the API-parity work in doc 10
(notably: un-stub `ALSM.cs`, bind the traffic-light / speed-limit RPCs, and expose a waypoint/junction/
landmark map API). The intersection-protocol scenarios in §4 also presuppose that signals/signs actually
exist in the world — i.e. doc 10's sign-injection step — or they have nothing to test against.

## 6. Two realistic uses once parity exists

1. **Validation harness.** After OSM-derived stops/lights/limits are implemented, SR's intersection
   scenarios (or custom ones authored on our generated maps) become the natural way to *prove* vehicles
   obey them — automatic pass/fail instead of eyeballing. This is the missing "did the fix actually
   work?" layer for doc 10.
2. **Deterministic event scripting for EO capture.** Its OpenSCENARIO parser is a standards-based way to
   script repeatable, deterministic events on procedurally generated cities for sensor/EO-sim capture
   (see the telemetry contract, [09](09_Telemetry_CoT_Contract.md)), instead of relying solely on the
   stochastic staging-ring traffic.

## 7. Open questions / risks

- **Version skew.** SR pins to a specific upstream CARLA Python API version; our shim is a partial,
  reshaped surface. Even after parity, method signatures/semantics may differ enough that SR needs a
  compatibility shim rather than running unmodified.
- **In-engine TM assumption.** SR expects the TM to *honor* lights/signs; our .NET TM must reach the
  un-stubbed state (doc 10 §3) first, or scripted intersection scenarios will pass/fail for the wrong
  reasons.
- **Scope.** Adopting SR pulls in py_trees and a large scenario/criteria framework. For our digital-twin
  goals a thin custom criteria checker may be cheaper than full SR integration — decide before committing
  to the dependency.
</content>
