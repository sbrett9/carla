---
name: sumo-traffic-scenarios
description: Use when building a SUMO traffic scenario or a Cursor-on-Target (CoT) telemetry dataset for a CARLA world generated from OpenStreetMap — including orbit/dwell/pattern-of-life scenarios, planted anomalies, ambient traffic, guard postings, fenced (access-restricted) road networks, or standalone scenario zips. Also use when the question is about how the OSM → world package (.xodr + bareearth.bin drape) → SUMO network → routes → CoT pipeline fits together, how run_SCTMV.py and CarlaNet produce the world, or which of the make_*_scenario.py / sumo_cot_telemetry.py tools to reach for. Covers the netconvert flags, coordinate alignment, which vehicles a scenario may ask for, and the measured gotchas that make routes actually work.
metadata:
  version: 1.2.0
---

# SUMO traffic scenarios for generated CARLA worlds

You are building repeatable, verifiable SUMO traffic against a CARLA world that was generated from an
OpenStreetMap extract, and turning that traffic into a Cursor-on-Target telemetry dataset. The
guiding principle throughout is **measure, never assert**: read every claim back out of the
simulation's own output, and validate routes with SUMO's own router, not a graph-only check.

## The pipeline in one picture

There are two stages. They are joined by one invariant: the SUMO network is rebuilt from the *same*
clipped OSM and the *same* pinned origin the CARLA world was built from, so their coordinate frames
coincide — **SUMO (x, y) equals CARLA (x, -y)**, with no offset arithmetic.

```
  OSM extract (Import/<Name>.osm)
        │
        │  STAGE 1 — world generation (CARLA + CarlaNet; needs a running headless server)
        │  run_SCTMV.py --build ...  →  WorldBuilder.build_world
        │    · clip OSM to <bounds>                    → Build/sumo-smoketest/<Name>_clipped.osm
        │    · convert → sample heights → inject → mesh (generate_world_from_osm_with_elevation)
        │    · --height-align drape samples true ground height per cell
        ▼
  world package (Build/world-packages/<Name>.cwp  OR loose .world.json + .xodr + .bareearth.bin)
    · world.json   origin lat/lon, origin height, grid geometry, netconvert extra args, build settings
    · map.xodr     the elevated OpenDRIVE the CARLA map loads
    · bareearth.bin  per-cell ellipsoidal ground height (the "drape" grid) — the telemetry's altitude
        │
        │  STAGE 2 — SUMO scenario + telemetry (pure Python + SUMO; NO CARLA needed)
        │  make_<name>_scenario.py  →  SumoScenarioBuilder / SumoPatternOfLifeBuilder
        │    · netconvert rebuilds the SUMO .net.xml from <Name>_clipped.osm at the SAME origin
        │    · write routes (.rou.xml), config (.sumocfg), and a labels sidecar (.labels.json)
        ▼
  scenario files in carla/Import/  →  sumo_cot_telemetry.py (drives SUMO over TraCI)
    · reads bareearth.bin for each vehicle's ellipsoidal height
    · emits CoT to UDP (TAK), an XML file, and/or a CSV dataset
```

Stage 1 is the user's job (it needs Cesium imagery and a CARLA server). Stage 2 is the reusable
tooling this skill is mostly about, and it runs on any machine with SUMO — no CARLA, no GPU.

## Stage 1 — build the world package (what produces the .xodr and bareearth.bin)

Run from a machine with a **headless CARLA server up** and `CESIUM_ION_TOKEN` set:

```bash
python carla/CarlaControl/scripts/run_SCTMV.py \
    --build \                    # run the world-build phase (--no-build attaches to a loaded world)
    --osm carla/Import/<Name>.osm \
    --height-align drape \        # samples true ground height per cell → the bareearth.bin grid
    --emit-world-package carla/Build/world-packages \
    --no-road-filter             # keep access=private roads (see the fence note below)
```

Key flags (`CarlaControlArgumentParser`, "world build" group):

- `--height-align drape` — the **drape** operation. It matches the road/ground to the photoreal
  point-by-point and, as a side effect, writes the per-cell `bareearth.bin` grid. Telemetry altitude
  is always true bare-earth; the grid is what Stage 2 reads for `hae`. Without drape, no grid.
- `--emit-world-package DIR` — writes the durable record (`WorldBuilder._write_world_package` →
  `client.write_world_package`): `world.json`, `map.xodr`, `bareearth.bin`.
- `--no-road-filter` — see **The fence** below. Off by default the build passes
  `--keep-edges.by-vclass passenger`, which **deletes every `access=private` road**. For a secure
  site (a port, a base) that removes the whole interior — turn the filter off.
- `--ion-asset-id` (photoreal, default 2275207) and `--ground-asset-id` (bare-earth heights,
  default 1 = Cesium World Terrain).

Outputs land in `Build/sumo-smoketest/<Name>_clipped.osm` + `<Name>_elevated.xodr` and, with the
package flag, `Build/world-packages/<Name>.cwp` (newer maps: a zip) or loose files (older maps).
The `world.json` records the exact **origin latitude/longitude** and the **NetconvertExtraArgs** —
copy those into Stage 2 so the frames align.

## Stage 2 — the reusable SUMO tooling (carla/CarlaControl/src/carlacontrol/)

All pure standard library. One public class per file (repo convention, see `carla/AGENTS.md`).

| module | responsibility |
|---|---|
| `SumoInstallation.py` | Finds SUMO: `--sumo-home` → `$SUMO_HOME` → repo `Build/sumo-src` → `PATH`. Gives `netconvert`, `sumo`, `duarouter`, the `tools/` dir (traci, sumolib), and `proj` data. |
| `SumoScenarioBuilder.py` | `NetconvertSettings` (the flag set), `build_network` (OSM→.net.xml, origin-pinned), `RoadNetwork` (reads a net for lane geometry + connections), `AmbientFlow` (a time-windowed traffic stream), and network post-processors: `restrict_private_roads` (the fence), `allow_opposite_overtaking`, `write_config`, plus the orbit/dwell route writers. |
| `SumoPatternOfLifeBuilder.py` | A multi-day timeline: `ScheduledVehicle` + `ScheduleStop`, and `write_routes` that merges time-windowed flows and scheduled vehicles onto one departure-sorted timeline. For week-long "pattern of life" scenarios. |
| `ScenarioVehicleMix.py` | `VehicleClassSpec` (one kind of vehicle a scenario asks for: which measured blueprints it draws, its share of the traffic, and the author's own driving attributes) and `ScenarioVehicleMix`, which writes those as `<vType>`s sized from the catalogue plus the per-class and whole-mix `<vTypeDistribution>`s. `check_route_file` reads a written `.rou.xml` back and refuses one whose types name no measured body. This is how a scenario satisfies the mapping contract below. |
| `VehicleCatalogue.py` | Read side of `vehicles.catalogue.json`: measured extent per blueprint, the bumper-to-origin shift the pose conversion needs, and the refusal reason for a type it cannot answer for. |
| `SumoCotBridge.py` | Drives a scenario through **TraCI** and emits CoT. `BareEarthGrid` (reads `bareearth.bin`, loose or inside a `.cwp`), per-type affiliation + multi-marked support, real-time pacing. |
| `CotUdpEmitter.py` | The CoT event formatter (shared with the CARLA truth producer, so datasets are comparable) and the UDP socket. Schema: `Docs/CAT_Research/Findings/09_Telemetry_CoT_Contract.md`. |

CLIs in `carla/CarlaControl/scripts/`:

- `make_sumo_scenario.py` — Gardnerville orbit (one marked vehicle laps a block N times).
- `make_arapahoe_scenario.py` — Arapahoe I-25 dwell (freeway + underpass, an incident, a long dwell).
- `make_bahonar_scenario.py` — Shahid Bahonar 7-day pattern of life (fenced port, guard postings,
  six anomalies).
- `sumo_cot_telemetry.py` — run any `.sumocfg`, emit CoT to `--udp` / `--xml` / `--csv`, with
  `--labels <name>.labels.json` for ground truth and `--bare-earth <grid>` for height.
- `listen_cot.py` (in the bundles) — a minimal UDP receiver to confirm the live feed.

Each scenario is also shipped as a **standalone zip at the workspace root** (`GardnervilleOrbit.zip`,
`ArapahoeUnderpass.zip`, `BahonarPatternOfLife.zip`): the four Stage-2 modules vendored into a pure
`sumo_orbit/` package (imports rewritten), the scenario files, the source OSM + bare-earth grid, a
CoT sample, and a README. Rebuilt by the `make_*_bundle.py` scripts in the scratchpad. The zips run
on any box with `SUMO_HOME` set — no repo, no CARLA.

## Which vehicles a scenario may ask for

A SUMO `vType` is a behaviour model *and* a body: its `length` and `width` set car-following gaps and
junction occupancy, so the vehicle SUMO reserved space for and the vehicle CARLA draws have to be the
same one. That is what the **vehicle mapping contract** fixes
(`Docs/CAT_Research/Plans/SUMO_Behavioral_Capture/04_Contracts.md`, contract `C1`):

- **One `vType` names exactly one CARLA blueprint**, by an explicit
  `<param key="carla:blueprint" value="vehicle.mini.cooper"/>` child — never by its id, its `vClass`,
  its `guiShape` or any resemblance between them. Variety within a kind of vehicle comes from a
  `<vTypeDistribution>` over several such types, not from one type standing for several vehicles.
- **The type's `length`, `width` and `height` are the blueprint's measured bounding box**, copied
  verbatim from the vehicle catalogue. The catalogue is produced by spawning each blueprint against a
  running server and measuring it, because a blueprint carries no dimension until it is spawned. SUMO's
  own class defaults are large and silent — declare all three rather than letting them apply.
- **A `vType` with no catalogue entry is not rendered.** No blueprint named, a blueprint the catalogue
  could not measure, or a name the catalogue does not hold: the vehicle stays in SUMO and in the
  behavioural truth record, and CARLA draws nothing for it. There is no nearest match and no
  substitution, because a substituted body makes the imagery and the truth disagree while both stay
  internally consistent, and nothing downstream can detect that.

**Motorcycles and other two-wheelers are outside the contract.** Do not declare a `motorcycle`,
`moped` or `bicycle` `vType`, and do not add one to a `vTypeDistribution`. Two reasons, and either
alone is enough:

- A motorcycle carries a rider. Riders are not rendered, and a riderless motorcycle moving down a road
  is a worse thing to put in a training corpus than no motorcycle at all.
- The content build registers no two-wheeled blueprint. `VehicleParameters.json` holds 17 vehicles,
  every one of them four-wheeled, and the two-wheeler identifiers that appear elsewhere in this
  repository (`harley`, `yamaha`, `crossbike` and the rest) match nothing in it, so there is nothing to
  measure and nothing to draw.

If a user asks for motorcycle traffic, say plainly that this content build has none and that a
two-wheeler is out of scope — never quietly render it as a car. Same answer for bicycles, and for
pedestrians, which the world-generation pipeline produces no footway meshes for.

**How a scenario satisfies this in practice.** Declare each kind of vehicle as a
`VehicleClassSpec` — the blueprints it draws, its share of the traffic, and the driving attributes
the scenario wants — and hand the list to `ScenarioVehicleMix` with the catalogue. It writes one
`<vType>` per blueprint with the measured box and the `carla:blueprint` param, one
`<vTypeDistribution>` per class, and the flat whole-mix distribution whose probabilities are share ×
member weight; shares that do not sum to one are normalised, so dropping a class the content build
has no body for redistributes it across the rest in the proportions already authored. A class naming
a body the catalogue does not hold, restating a dimension the measurement supplies, or declaring a
two-wheeler `vClass` stops the build. Then read the written file back with
`ScenarioVehicleMix.check_route_file` and validate it against
`Build/sumo-install/data/xsd/routes_file.xsd`; `make_sumo_scenario.py` does both and is the worked
example.

**What the catalogue cannot fill.** It measured 17 bodies and there is **no pickup** among them,
and exactly one sport utility (`vehicle.nissan.patrol`). A scenario that wants a pickup does not get
one: say so, drop the class, and let the shares redistribute — do not reach for the nearest-sized
body, because a substituted body makes the imagery and the behavioural record disagree while each
stays internally consistent.

## The recipe for a new scenario

1. **Get the world package** (Stage 1), or confirm one exists in `Build/world-packages/`. Read its
   `world.json` for `OriginLatitude`/`OriginLongitude` and `NetconvertExtraArgs`.
2. **Build the network** with `SumoScenarioBuilder.build_network` using a `NetconvertSettings` that
   pins that origin. Verify the frame: the net's `convBoundary` must equal the `.xodr` header's
   `north`/`south`/`east`/`west`.
3. **Reconnoitre against the real net.** Find the edges for every source, sink, gate, and waypoint,
   and their access class. Use `duarouter` (below) to confirm every origin-destination and waypoint
   route the scenario needs. Save the validated edge IDs as named constants in the CLI.
4. **Write the traffic.** `AmbientFlow`s for background streams (with `via` edges where the shortest
   path would differ); `OrbitRoute`/`DwellTrip`/`ScheduledVehicle` for the marked vehicles. Build the
   vehicle types with `ScenarioVehicleMix` so every `vType` obeys the mapping contract above; no
   two-wheelers. The marked vehicle is a class like any other — one of one body, with a share of zero
   so it stays out of the ambient mix — so it is bound to a measurement by the same rule.
5. **Write config + labels.** For a labelled dataset, emit a `.labels.json`:
   `{marked_ids, affiliation_by_type, anomaly_notes}`.
6. **Run and verify.** Simulate; read back the marked vehicles' fcd and the tripinfo; confirm each
   intended behaviour actually happened, with numbers. Confirm the population is stable (not a
   monotonically climbing count) and there are no route errors.
7. **Package** into a standalone zip if the user will run telemetry elsewhere.
8. **Regression-check** the other scenarios still regenerate identically after any shared-code edit
   (Gardnerville is the canary).

## The fence: two populations meeting at gates (secure sites)

When a map's interior is `access=private` (a port, a base), model it as two vehicle populations:

- Build the net with the private roads **kept** (`NetconvertSettings(drivable_edges_only=False,
  remove_edge_types=(pedestrian ways))`).
- Call `SumoScenarioBuilder.restrict_private_roads(net, osm, allow="army authority")`. It sets every
  OSM-private edge to allow only the given vehicle classes and clears the public roads to allow all.
- **Critical:** it also clears the *internal junction-connector lanes*. netconvert built those for
  the classes the roads allowed at the time (a civilian remainder that excludes the military
  classes); if you leave them, the restricted class cannot cross any junction and the interior
  fragments. This was the single subtlest bug in the Bahonar build.
- Civilian traffic (`passenger`) is then physically locked to the public roads; `army`/`authority`
  vehicles move everywhere; the gates are the junctions where public meets private.

## Measured gotchas (each cost real time; do not relearn them)

- **Validate routes with `duarouter`, not `sumolib.getShortestPath`.** sumolib gives false positives
  (it will traverse one-way edges the real router refuses), so hand-picked routes then fail at SUMO
  load with "no valid route". Batch candidate trips through
  `duarouter -n net -r trips.rou.xml -o out.rou.xml --ignore-errors` and keep only those that
  produce a `<vehicle>`.
- **A `<stop speed=…>` waypoint caps speed only between its own `startPos`/`endPos` on its own edge**
  (`MSVehicle.cpp` "process all stops and waypoints on the current edge"), not from the previous
  waypoint onward. Holding a whole phase to one speed needs one waypoint per edge in that phase.
- **A scalar `speedFactor` is a distribution, not a value** — SUMO applies the default `speedDev`
  0.1, so `speedFactor="1.25"` measured 1.31. Set `speedDev="0"` when the multiple must be exact.
- **The route file must be sorted by departure time** across flows and vehicles, or SUMO silently
  drops the out-of-order entries with only a warning. `SumoPatternOfLifeBuilder` merges both onto one
  timeline before writing for exactly this reason.
- **`--opposites.guess` yields zero opposite lanes on our netconvert output** (it compares
  junction-trimmed lane-shape endpoints). For centre-line overtaking, name the pairs explicitly with
  `allow_opposite_overtaking`. It does not rescue a two-way jam: SUMO refuses the manoeuvre while
  anything is oncoming, and once both directions queue, something always is.
- **netconvert's guessed traffic lights are fixed-time 90 s programs** that a busy interchange cannot
  discharge (population climbs, never settles). Use `NetconvertSettings(traffic_light_type="actuated")`.
- **A near-zero-length edge that spans real geometry** (two junctions netconvert failed to merge)
  blocks merging and stalls a ramp. `NetconvertSettings(junction_join_distance=25)` removes it.
- **A long dwell blocks a single-lane road.** Model "parked on the shoulder" with a `<stop
  parking="true">`, not a stop in the running lane. A parked vehicle is still reported by TraCI, so
  it stays in the dataset — verified.
- **XML comments cannot contain `--`.** An em-dash written as `--` in a routes/config comment makes
  SUMO reject the file ("'--' sequence is illegal in comment").
- **`--device.fcd.explicit` takes a comma-separated list**, not space-separated.

## CoT dataset shape and ground truth

Events are CoT `<event>`s (schema in `Docs/CAT_Research/Findings/09_Telemetry_CoT_Contract.md`),
emitted identically to UDP, an XML file, and a 31-column CSV (one row per vehicle per update). The
`_carla` detail block name is kept even though the source is SUMO, so the two producers are directly
comparable. Ground truth rides in the `.labels.json` the telemetry tool reads:

- `marked_ids` — vehicle IDs flagged as anomalies (`marked=1`, and a distinct affiliation).
- `affiliation_by_type` — CoT affiliation per SUMO vehicle type: civilian `n` (neutral), military
  `f` (friendly), anomaly `u` (unknown). The letter appears in `cot_type` = `a-<letter>-G-E-V`.
- `anomaly_notes` — anomalies that are *absences* (e.g. a guard who never arrives) have no vehicle,
  so they are documented here as a described gap (location + time window). A run carries them out to
  a `*.supervision.json` beside its dataset, each window placed on the epoch that run stamped
  (`SupervisionSidecar`, written by `sumo_cot_telemetry`). It is written beside the dataset and never
  into it: a note saying which post stood unmanned between which hours is the answer to the question
  the dataset asks.

Height (`hae_m`) is ellipsoidal, read from `bareearth.bin`. Coordinates convert through the running
simulation (`traci.simulation.convertGeo`), which uses SUMO's own PROJ and the network's projection
string — no `pyproj`, no second implementation to disagree. Course is degrees clockwise from true
north; `vx`/`vy` are east and *negated* north, so `atan2(vx, -vy)` recovers the course. `--rate`
sets sampling density (events per vehicle per second of simulation); `--real-time-factor` paces the
run against the wall clock (0 = as fast as possible, for datasets; 1 = real time, for a live feed).

## Related

- `Docs/CAT_Research/Findings/23_SUMO_Traffic_Integration.md` — the original scouting and rationale.
- `Docs/CAT_Research/Findings/09_Telemetry_CoT_Contract.md` — the CoT event schema.
- `carla/AGENTS.md` — the Python conventions these modules follow.
