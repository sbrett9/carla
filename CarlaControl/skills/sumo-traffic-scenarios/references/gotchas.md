# Measured gotchas, and where each is enforced

Each of these cost real time once. Each states whether the compiler enforces it on a specification,
the world build does, or it stays a judgement, and cites the enforcement site as `path::symbol` from
the repository root. `test_skill_references.py` fails when a cited file or symbol no longer exists, or
a cited check is not in the check list, so a citation here cannot outlive the code it names.

Design record: `Docs/CAT_Research/Plans/SUMO_Behavioral_Capture/07_Scenario_Authoring.md` §6.

## 1. A `<stop speed>` waypoint binds only its own edge

It caps speed between its own `startPos` and `endPos` on its own edge, so holding a phase to a speed
takes one waypoint spanning each edge of the phase.

**Enforced by the compiler:** an actor's `phases[]` with `hold` —
`CarlaControl/src/carlacontrol/ScenarioCompiler.py::_resolve_phases`.

## 2. A scalar `speedFactor` is a distribution unless `speedDev` is `0`

SUMO draws each vehicle's factor around the scalar. A marked vehicle whose speed multiple must be exact
declares `"speedDev": "0"` in its class's `behaviour`.

**A judgement.** A class's `behaviour` is copied through as written.

## 3. The route file must be departure-sorted

SUMO drops an entry that departs earlier than one it has already read, with only a warning. Entries
that depart together keep the specification's order, flows before actors, because SUMO inserts and
draws its random numbers in reading order.

**Enforced by the compiler:** `CarlaControl/src/carlacontrol/ScenarioCompiler.py::_routes_xml`,
check 29.

## 4. `--opposites.guess` yields nothing on our networks

netconvert declines to pair lanes trimmed differently at their junctions, which is most of them, so
overtaking across the centre line needs the pairs named.

**Not in a specification.** Naming pairs edits a network, which is a world-build decision, and a
compiled scenario runs the world's network byte for byte, so no compiled scenario has them; the Arapahoe
dwell parks its vehicle off the running lane instead (gotcha 7). The legacy builder's edit is
`CarlaControl/src/carlacontrol/SumoScenarioBuilder.py::allow_opposite_overtaking`.

## 5. Guessed traffic lights are fixed-time 90 s programmes

A busy interchange cannot discharge through them.

**Enforced by the world build:** `--tls.default-type actuated` —
`CarlaNet/src/CarlaNet.Map/OsmConverter.cs::tls.default-type`.

## 6. A near-zero-length edge spanning real geometry blocks merging

**Enforced by the world build:** `--junctions.join-dist 25` —
`CarlaNet/src/CarlaNet.Map/OsmConverter.cs::junctions.join-dist`. A place on a degenerate edge is not
refused yet.

## 7. A long dwell in a running lane blocks the road

On a single-lane road a vehicle stopped on the carriageway blocks it for the whole dwell; measured on
the Arapahoe underpass, traffic drops from 12.9 m/s to 0.6 and queues both ways.

**A judgement.** A stop's `parking` is written as declared; use `"parking": true` for a long stop where
traffic passes.

## 8. An XML comment cannot contain `--`

SUMO rejects the file.

**Enforced by the compiler:** `CarlaControl/src/carlacontrol/ScenarioCompiler.py::_comment`, check 30.

## 9. `--device.fcd.explicit` is comma-separated

**Not needed:** the compiler writes no device options. Type it by hand as `--device.fcd.explicit a,b`.

## 10. Validate routes with `duarouter`, never `sumolib`

`sumolib`'s shortest path traverses one-way edges the real router refuses, and `duarouter` itself
returns a one-edge route for a trip to an edge that does not exist.

**Enforced by the compiler:** `CarlaControl/src/carlacontrol/RouteValidator.py::route`, checks 11 and
12: every routed result starts on its origin, ends on its destination and passes every via and stop in
order.

## 11. A permission rewrite after netconvert must clear the junction lanes too

netconvert builds each internal lane from the permissions of the lanes it joins, so rewriting the
normal lanes afterwards leaves the junctions closed to the new class.

**Does not arise at world build:** a world's type map sets the permissions before netconvert builds the
junctions — `CarlaControl/src/carlacontrol/NetconvertTypeMap.py::netconvert_arguments`. The legacy
rewrite still clears them —
`CarlaControl/src/carlacontrol/SumoScenarioBuilder.py::restrict_private_roads`.

## 12. A SUMO `H:M:S` literal is an elapsed offset, not a clock

`sumo --begin 7:00:00` runs from step 25 200, which is 07:00 only when `t = 0` is midnight.

**Enforced by the compiler:** civil times are resolved against the epoch —
`CarlaControl/src/carlacontrol/CivilTimeResolver.py::instant`, check 47 — and no attribute of an
emitted file is a clock — `CarlaControl/src/carlacontrol/ScenarioCompiler.py::_CLOCK_VALUE`, check 44.
