---
name: sumo-traffic-scenarios
description: Use when building a SUMO traffic scenario or a Cursor-on-Target (CoT) telemetry dataset for a CARLA world generated from OpenStreetMap — including orbit/dwell/pattern-of-life scenarios, planted anomalies, ambient traffic, guard postings, fenced (access-restricted) road networks, or standalone scenario zips. Also use when the question is about how the OSM → world package (.xodr + bareearth.bin drape) → SUMO network → routes → CoT pipeline fits together, how run_SCTMV.py and CarlaNet produce the world, or which of the make_*_scenario.py / sumo_cot_telemetry.py tools to reach for. Covers the netconvert flags, coordinate alignment, which vehicles a scenario may ask for, and the measured gotchas that make routes actually work. Also use when writing or compiling a scenario specification (compile_scenario.py): the epoch that says what civil time t = 0 is, civil-time literals, named places, rotas, supervision labels and vocabulary, capture windows, the illumination default, sweeps and counterfactual pairs.
metadata:
  version: 1.5.0
---

# SUMO traffic scenarios for generated CARLA worlds

You are building repeatable, verifiable SUMO traffic against a CARLA world that was generated from an
OpenStreetMap extract, and turning that traffic into a Cursor-on-Target telemetry dataset. The
guiding principle throughout is **measure, never assert**: read every claim back out of the
simulation's own output, and validate routes with SUMO's own router, not a graph-only check.

## The pipeline in one picture

There are two stages. They are joined by one invariant: the SUMO network comes out of the *same*
netconvert invocation as the CARLA world's OpenDRIVE, at the *same* pinned origin, and travels in the
world package, so the two are one road graph in one frame — **SUMO (x, y) equals CARLA (x, -y)**, with
no offset arithmetic. The network is never rebuilt: a second netconvert run with identical flags gives
a different graph.

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
    · map.net.xml  the SUMO network, from the same netconvert run as map.xodr
    · places.json, areas.resolved.json, solar.json  the authoring reference set
        │
        │  STAGE 2 — SUMO scenario (pure Python + SUMO; NO CARLA needed)
        │  <Scenario>.scenario.json  →  compile_scenario.py  (ScenarioCompiler)
        │    · resolve places and civil times, route with duarouter, check, emit
        │    · .rou.xml (routed) · .sumocfg · the world's .net.xml · .supervision.json · .lock.json
        │    · .add.xml — the lane closures, where the specification declares any
        │    · .resolution.json — what everything resolved to
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
    --osm carla/Import/<Name>.osm \
    --height-align drape \
    --emit-world-package carla/Build/world-packages \
    --no-road-filter \
    --type-map carla/Import/<Name>.typ.xml
```

The world is built unless `--no-build` is given, which attaches to the world already loaded.
`--no-road-filter` and `--type-map` are for a secure site (see **The fence** below); the type map is
found beside the extract without the flag.

Key flags (`CarlaControlArgumentParser`, "world build" group):

- `--height-align drape` — the **drape** operation. It matches the road/ground to the photoreal
  point-by-point and, as a side effect, writes the per-cell `bareearth.bin` grid. Telemetry altitude
  is always true bare-earth; the grid is what Stage 2 reads for `hae`. Without drape, no grid.
- `--emit-world-package DIR` — writes the durable record (`WorldBuilder._write_world_package` →
  `client.write_world_package`): `world.json`, `map.xodr`, `map.net.xml`, `bareearth.bin`, then the
  authoring reference set.
- `--no-road-filter` — see **The fence** below. Off by default the build passes
  `--keep-edges.by-vclass passenger`, which **deletes every road a passenger car may not drive**:
  every `highway=service` road (SUMO's own type map opens it to delivery vans, pedestrians and
  bicycles only) and every `access=no` road. netconvert does not read `access=private` at all, so a
  private residential road survives the filter and a public service road does not. For a secure site
  whose interior is service roads, the filter removes the interior — turn it off.
- `--type-map FILE` — the world's own road types, layered over SUMO's OSM type map; default
  `<extract>.typ.xml` beside `--osm`. See **The fence** below.
- `--ion-asset-id` (photoreal, default 2275207) and `--ground-asset-id` (bare-earth heights,
  default 1 = Cesium World Terrain).

Outputs are written to `Build/sumo-smoketest/<Name>_clipped.osm` + `<Name>_elevated.xodr` and, with the
package flag, `Build/world-packages/<Name>.cwp` (newer maps: a zip) or loose files (older maps).
The `world.json` records the exact **origin latitude/longitude** and the **NetconvertExtraArgs** —
copy those into Stage 2 so the frames align.

## Stage 2 — write a scenario specification and compile it

A scenario is a **specification**, `<Scenario>.scenario.json`, compiled against the world package by
one compiler that checks everything it can before a capture is spent:

```bash
python carla/CarlaControl/scripts/compile_scenario.py Import/<Scenario>.scenario.json \
    --out-dir Build/scenarios/<Scenario>
```

It writes, into the output directory: `<scenario_id>.rou.xml` (every vehicle and flow **already routed**
by `duarouter`, times in plain seconds, no supervision), `<scenario_id>.sumocfg`, the world's own
`<MapName>.net.xml` copied byte for byte, `<scenario_id>.supervision.json` (the only place labels go),
`<scenario_id>.lock.json` (every digest, the seed, the epoch) and `<scenario_id>.resolution.json` /
`.md` — **what everything resolved to, and every warning**. A refused compile writes only the report,
naming every refusal by check id. Read the report back and check it against what was meant; it is the
only place an annotation, a date or a sun can be checked, because `sumo-gui` shows elapsed seconds.

The schema is `schemas/scenario.schema.json` beside this file; every check, with its id, what it
compares and whether it refuses or warns, is `checks.json`. Both are generated from the compiler. The
design is `Docs/CAT_Research/Plans/SUMO_Behavioral_Capture/07_Scenario_Authoring.md` §3.5–§7.

Beside this file, and held to the compiler by the test suite:

- `examples/` — worked specifications with their recorded resolution reports: a minimal one, one with
  every kind of supervision and a counterfactual sweep, and three epochs (whole hour, daylight saving,
  +03:30). `examples/README.md` says what each shows.
- `references/resolution.md` — every place form and instant form, what each resolves to and refuses.
- `references/time.md` — the epoch object and the six conventions for writing civil time.
- `references/illumination.md` — the illumination default, windows, doc 11's six bands, night, and the
  illumination–label association.
- `references/gotchas.md` — the measured gotchas, each with the code that enforces it.

**The blocks.** `world` (the package path and the network fingerprint it was authored against), `epoch`,
`illumination`, `seeds` (`sumo`), `simulation` (`end`, `step_length_s`), `catalogue`, `vehicle_classes`,
`vehicle_mix` and `vehicle_mixes` (the vehicle mapping contract below, as data), `places`, `place_sets`,
`instants`, `flows`, `actors`, `rotas`, `lane_closures`, `vocabulary`, `supervision`,
`capture_windows`. A field the schema does not name is refused.

A lane closure is an incident's: `{"id", "place", "lanes", "notify", "begin", "end"}` closes the lanes
of the one edge `place` names, by index from the right, to everything but class `authority` for the
window, and `notify` names the edges where a vehicle learns of it (the closed edge when absent). It
compiles into `<scenario_id>.add.xml`, which the configuration names and the lock digests; the route
file cannot carry it. `make_arapahoe_scenario.py` closes five of I-25's six northbound lanes for three
minutes this way.

`make_bahonar_scenario.py` is the worked pattern of life: a week scheduled in civil clocks under a
07:00 epoch, a guard rota whose one skip plants the no-show, five annotated instances, a nominal series
over the rota with the no-show as its absence, each sited at the world's areas of interest
(`Import/<Name>.aoi.geojson`, published into the package), and three named mixes.

### The epoch — ask for it, never assume it

Every scenario declares what civil instant `t = 0` is — the `epoch` object:

```json
{"epoch_version": 1, "civil_datetime": "2026-03-21T00:00:00+03:30", "utc_offset_hours": 3.5,
 "utc_datetime": "2026-03-20T20:30:00Z", "calendar_advances": true, "dst_in_effect": false,
 "time_zone_id": "Asia/Tehran"}
```

1. **If the author has not said what civil date and time `t = 0` is, ask**, offering a candidate from
   the site. A guessed epoch compiles and asserts the wrong thing.
2. **Write civil times; never multiply.** `"d0 07:00"`, not `25200`. Forms: seconds; `"dN HH:MM[:SS]"`;
   `"HH:MM"` alone only on a run of one day or less; `"2026-03-21T07:00:00+03:30"` at the epoch's own
   offset; durations `"8h"`, `"30m"`, `"1h30m"`, `"7d"`; `{"instant": "name"}`; either with
   `"plus": <duration>`.
3. `t = 0` is conventionally midnight, and need not be: day N is the epoch's date plus N days.
4. **Never write a SUMO `H:M:S` literal anywhere**, even on a command line: `--begin 7:00:00` is step
   25 200, an offset that looks like a clock.
5. **Half-hour and quarter-hour offsets are ordinary** — Bahonar is Iran, **+03:30**. The offset is a
   whole number of quarter hours; the zone name is carried and never resolved.
6. **Declare the offset in force on the scenario's dates**, with `dst_in_effect` saying whether it
   includes daylight saving: Colorado in late March is −06:00 with `dst_in_effect: true`. One offset holds
   for the whole run.

The utc_datetime must be the civil instant minus the offset; the commonest error — the offset applied
in the wrong direction — is refused and named.

### Places — name them, describe them, let the compiler resolve them

A route never carries a bare edge id: flows and actors name places. A place is `{"edge": …}` (optionally
`"offset_m"`), `{"lane": …, "offset_m": …}`, `{"area": "<area of interest id>"}`, or
`{"street": "East Arapahoe Road", "direction": "west", "at": "<cross street>"}`. A street name is never
one edge (`South Yosemite Street` is 65), so narrow it with `direction` and `at`; the compiler refuses
ambiguity and lists the candidates rather than guessing. A stop needs a lane position: a lane with an
offset, an edge with an offset, a geographic point, or an area holding one lane. On a map with few
street names (Bahonar: 4.5 %), use points, gateways and areas:

- `{"lat": …, "lon": …, "max_snap_m": 25, "vclass": "army"}` — the position on the nearest lane
  admitting the class (any road vehicle without one); refused past `max_snap_m`, and refused when two
  roads are equally near. The report states the snap distance: read it.
- `{"street": …, "direction": …, "near": {"lat": …, "lon": …}}` — the one edge of that run nearest
  the point.
- `{"gateway": "south", "travel": "in", "street": "South Valley Highway"}` — where a road enters
  (`in`) or leaves (`out`) the world on that side. On Arapahoe this finds the four I-25 edges the
  script had found by hand.
- `{"from_street": …, "to_street": …}` with optional `from_direction`, `to_direction` — a turn: the
  two edges one connection joins. Use it in `via`, where it contributes both.

### Repetition — a rota, not a loop

```json
{"id": "guard_posting", "days": "0..6", "at": ["07:00", "15:00", "23:00"],
 "subjects": {"place_set": "guard_towers"}, "id_pattern": "guard_d{day}_h{hour}_t{subject_index}",
 "template": {"type": "guard", "from": "guard_base", "to": "guard_base", "via": ["$subject"],
              "stops": [{"place": "$subject", "duration": "8h", "parking": true}]},
 "skip": [{"day": 4, "at": "07:00", "subject_index": 3, "because": "the no-show anomaly"}]}
```

A skip must match exactly one occasion and say why: it is how an absence is planted.

### An orbit — an explicit route in phases, a held phase waypointed per edge

```json
{"id": "orbiter", "type": "orbiter", "depart": 60, "depart_lane": "free", "depart_speed": "20.12",
 "arrival_speed": "current",
 "phases": [{"route": ["108141475#0", "108141475#2", "108141475#3", "108141475#4", "-219060582#2"],
             "hold": "posted"},
            {"route": ["219060581#4", "-219060584#0", "219060581#1", "219060581#2", "219060581#3"],
             "repeat": 20, "hold": 11.0},
            {"route": ["219060582#2", "-108141475#4", "-108141475#3", "-108141475#2", "-108141475#1"]}]}
```

Each entry names a place (here, places named by their edge ids). `hold` is a speed in m/s capped at each
edge's limit, or `"posted"` for each edge's own limit; the compiler writes one waypoint spanning each
held edge, because a `<stop speed>` binds only its own edge. A phase without `hold` runs on the vehicle's
speedFactor. `repeat` counts passes over the phase; the joins are checked for connection. A stop beside
phases is refused — on a repeated route it names no one pass. `make_sumo_scenario.py` is the worked
example: it writes the Gardnerville orbit this way and compiles it.

### Supervision — the only annotation channel, and the author's words

Labels go in the `supervision` block and reach only `<scenario_id>.supervision.json`; the route file
carries none, and the compiler refuses a route file carrying anything but the vehicle-type binding.

- **Three states:** `annotated` (executing the named pattern), `nominal` (executing no target pattern —
  a hard negative), `unlabelled` (no assertion; everything not declared). A flow may be `annotated`
  whole-life or `unlabelled`, **never `nominal`** and never with intervals.
- `instances[]` carry participants (`actor`, `role`), `intervals`, `aoi_refs`, `labels`. A
  one-participant instance names its participant `subject`. Never author the phase `vacancy`.
- **An interval is declared in civil time** (`begin` with `end` or `duration`) **or by an `anchor`**,
  never both (check 58). An anchor names the events of the participant that commit the interval:
  `{"start": "stop:0", "end": "stop_end:0"}` for a dwell at its first stop, `{"start": "depart"}` for
  a transit from its insertion, `phase:<i>` for entering the i-th of its `phases[]`; indices count from
  0. Prefer an anchor where the pattern is a stop or a phase: SUMO decides when a vehicle arrives, so a
  civil begin there is a guess, and a `duration` stop declares only its length.
- `series[]` reads a rota as a recurring series; `absences[]` annotate a skipped occasion — an anomaly
  with no vehicle.
- **Terms are the author's**, declared in `vocabulary.namespaces[]` as `<namespace>:<name>` with a
  definition, `applies_to` (`entity`/`cohort`/`slot`), `realisation` (`present`/`absent`), `since` and
  `status`. **Invent no terms on the author's behalf** — the label is a contract between the author and
  the model trainer, and this pipeline carries it without judging it. Ask for the author's words.
- **A subject's magnitudes go in `parameters`** — on an instance, an absence, a series or a cohort — and
  each key must be declared in the `parameters{}` of one of its labels' terms, with a `type`
  (`number`, `integer`, `string`, `boolean`), a `unit` where it has one and a `definition`. An
  undeclared key, or a value of another type, is refused (check 56); there is no free-form attribute map.
- A term carried by `nominal` subjects may declare `hard_negative_for`; the plan copies it onto them, so
  restate it on a subject only exactly, or not at all (check 57). A term's `exemplar_instances` name
  instances of this scenario by their authored names, and each must resolve (check 8).
- Some subject should be `nominal` when any is `annotated` — hard negatives are the most valuable output.

### Light — derived context, never a label

- `illumination` is **required** and is the authored default the operator may override:
  `freeze_at_window_start` (recommended), `advance` with `rate_sun_s_per_sim_s`, `freeze_at` with
  `freeze_at_civil_time`, or `ignore`.
- `capture_windows[]` are **candidates**, not run instructions; a window may not cut a declared
  interval. The report states each window's civil date and the sun it opens under.
- **Expect the illumination–label association (check 41) to be non-zero, and report it to the author
  in words** with its degenerate bands and remedies. In a pattern of life the correlation is
  structural — the sizing scenario measures 0.600 — and it is a warning, never a refusal.
- A window below −6° is not corpus-eligible (doc 11 D11.7): night gives complete behavioural truth and
  no usable imagery. It warns; the choice stays the author's.

### Sweeps and counterfactual pairs

`compile_scenario.py --sweep <Sweep>.sweep.json --out-dir …` compiles every member in full
(`schemas/sweep.schema.json`). A sweep that varies behaviour **holds illumination** unless it says
`vary` or `factorial`; the compiler decides which axes change the light (`epoch…`, `illumination…`, a
window's `begin`). **To sweep illumination, sweep `epoch.date`, not the window hour**: the date moves
the sun while the traffic stays identical. Counterfactuals: `absent`, `nominal` (with the fields the
author names in `remove`), `displaced` (a `shift` in time, a `places` substitution in space). A pair is
identical inputs, never identical trajectories.

## The Python builders (carla/CarlaControl/src/carlacontrol/)

All pure standard library. One public class per file (repo convention, see `carla/AGENTS.md`).

| module | responsibility |
|---|---|
| `SumoInstallation.py` | Finds SUMO: `--sumo-home` → `$SUMO_HOME` → repo `Build/sumo-src` → `PATH`. Gives `netconvert`, `sumo`, `duarouter`, the `tools/` dir (traci, sumolib), and `proj` data. Compares its release with a world's converter through `CarlaNet.Sumo.SumoRelease`, the session's own comparison. |
| `SumoScenarioBuilder.py` | `NetconvertSettings` (the flag set), `build_network` (the world package's network, checked and copied byte for byte), `RoadNetwork` (reads a net for lane geometry + connections), `AmbientFlow` (a time-windowed traffic stream), and network post-processors: `restrict_private_roads` (an access-keyed fence the session refuses; nothing calls it), `allow_opposite_overtaking` (the Arapahoe builder's), `write_config`, plus the orbit/dwell route writers. |
| `NetconvertTypeMap.py` | The world's own road types (`<extract>.typ.xml`, `--type-map`), validated before a build and passed to netconvert after SUMO's own map: the fence as a world-build decision. |
| `ScenarioVehicleMix.py` | `VehicleClassSpec` (one kind of vehicle a scenario asks for: which measured blueprints it draws, its share of the traffic, and the author's own driving attributes), `VehicleMixSpec` (a named population drawn from declared classes at its own shares) and `ScenarioVehicleMix`, which writes those as `<vType>`s sized from the catalogue plus the per-class, whole-mix and named-mix `<vTypeDistribution>`s. `check_route_file` reads a written `.rou.xml` back and refuses one whose types name no measured body. This is how a scenario satisfies the mapping contract below. |
| `VehicleCatalogue.py` | Read side of `vehicles.catalogue.json`: measured extent per blueprint, the bumper-to-origin shift the pose conversion needs, and the refusal reason for a type it cannot answer for. |
| `SumoCotBridge.py` | Drives a scenario through **TraCI** and emits CoT. `BareEarthGrid` (reads `bareearth.bin`, loose or inside a `.cwp`), per-population affiliation (a compiled type's class, read from its `carla:class_id`) + multi-marked support, real-time pacing. |
| `CotDisplayConvention.py` | A run's display convention: the CoT affiliation per vehicle population, read from a file beside the scenario (`<scenario>.display.json`, `beside()` names it), never part of it. Refuses a file naming vehicles; reads a legacy `.labels.json`'s display half and withholds its anomaly `u`. |
| `CotUdpEmitter.py` | The CoT event formatter (shared with the CARLA truth producer, so datasets are comparable) and the UDP socket. Schema: `Docs/CAT_Research/Findings/09_Telemetry_CoT_Contract.md`. |

CLIs in `carla/CarlaControl/scripts/`:

- `make_sumo_scenario.py` — Gardnerville orbit (one marked vehicle laps a block N times). Writes a
  specification and compiles it.
- `make_arapahoe_scenario.py` — Arapahoe I-25 dwell (freeway + underpass, an incident, a long dwell).
  Writes a specification and compiles it; the incident is a lane closure.
- `make_bahonar_scenario.py` — Shahid Bahonar 7-day pattern of life (the port's guard postings, ferry
  pulses and shift changes, six anomalies). Writes a specification and compiles it against the world
  package; refused by name while the catalogue lacks a body it draws.
- `sumo_cot_telemetry.py` — run any `.sumocfg`, emit CoT to `--udp` / `--xml` / `--csv`, with
  `--labels <name>.labels.json` for a legacy scenario's ground truth and `--bare-earth <grid>` for
  height. A compiled scenario's ground truth is its `.supervision.json`, which this tool does not read.
  `--display-convention <name>.display.json` gives each vehicle class its CoT affiliation, a run
  setting kept beside the scenario (the Bahonar port's is
  `Import/Shahid_Bahonar_Port_PatternOfLife.display.json`). Without the flag the tool uses
  `<scenario>.display.json` beside the `--config` `.sumocfg` when there is one and logs which file it
  used, or that there was none; a named file wins over a found one, and either wins outright over a
  `--labels` file's affiliations.
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
- **The type's `length` and `height` are the blueprint's measured bounding box, and its `width` the
  body's without its wing mirrors**, copied verbatim from the vehicle catalogue. The catalogue is
  produced by spawning each blueprint against a running server and measuring it, because a blueprint
  carries no dimension until it is spawned; the box spans the mirrors, so the body width is measured
  separately from the mesh in the editor and carried beside it (`body_width_m`). SUMO's width is the
  body's: with its mirrors the Fuso bus is wider than a lane and deadlocks traffic behind it. SUMO's
  own class defaults are large and silent — declare all three rather than letting them apply. The
  compiler writes them; a class whose body has no measured body width is refused.
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
`Build/sumo-install/data/xsd/routes_file.xsd`. The scenario compiler does all of this from a
specification's `vehicle_classes` (checks 14, 15, 51); `make_sumo_scenario.py` is the worked example.

**Several populations.** Where flows draw from different compositions — corridor traffic from cars,
taxis and lorries, port traffic from cleared cars and freight — declare `vehicle_mixes[]`, each
`{"id": "port_mix", "shares": {"port_vehicle": 0.7, "port_truck": 0.3}}`: a flat distribution of its own
by the same share × member weight arithmetic, named by a flow's `type`. A class's own `share` belongs
to the single `vehicle_mix` and plays no part in a named one. A mix naming an undeclared class, a
share that is not positive, or an id a class, type or other mix already has is refused (check 14), and
a flow drawing a mix is held to every member class's road permissions (check 10).

**A planted vehicle belongs to the class of the population it moves among** unless its driving model
is the behaviour itself: a vehicle type carried by planted vehicles alone reaches the truth record as
their label. In Bahonar the gate probe is a civilian car and the escort military jeeps; only the
perimeter shadow's crawl and the stay-behind keep classes of their own.

**What the catalogue cannot fill.** It measured 17 bodies and there is **no pickup** among them,
and exactly one sport utility (`vehicle.nissan.patrol`). A scenario that wants a pickup does not get
one: say so, drop the class, and let the shares redistribute — do not reach for the nearest-sized
body, because a substituted body makes the imagery and the behavioural record disagree while each
stays internally consistent.

## The recipe for a new scenario

Prefer a specification compiled with `compile_scenario.py` (above): it resolves places, routes and checks everything below itself. This recipe is for the Python builders, which write SUMO XML directly.

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

## The fence: what a secure site's roads admit (a world-build decision)

What a road admits is part of the **world**: a scenario runs the world's network byte for byte, and
the co-simulation session refuses any other. So the fence is set when the world is built, never by
editing a network afterwards:

- Build with `--no-road-filter`, and give the world a **type map** — `Import/<Name>.typ.xml` beside
  the extract (found by name) or `--type-map` — naming the classes a road type admits. It is layered
  over SUMO's own map, so a line states only what it changes:
  `<types><type id="highway.service" allow="delivery pedestrian bicycle army authority"/></types>`.
- `Import/Shahid_Bahonar_Port.typ.xml` is the worked one. The guard towers and the apron are untagged
  `highway=service` roads, which SUMO's map closes to `army`; that line opens them, and the 335-entry
  guard rota compiles on the world converted with it and is refused by check 10 on the world without.
- netconvert builds the junction-connector lanes from the permissions it assigned, so nothing has to
  clear them afterwards.
- **What it cannot do: key on `access`.** netconvert reads `access` only as `access=no` (public
  transport, emergency and authority only), never `access=private`, so a type map sets what every road
  of a type admits, private or not. A fence that keeps civilians off `access=private` residential
  roads is not expressible in a world today.
- `SumoScenarioBuilder.restrict_private_roads` still does that access-keyed rewrite, and nothing calls
  it: the Bahonar generator now runs the world's own network. A network it rewrites is **not** the
  world's: the session refuses a scenario on it and a specification cannot use it. Do not use it for
  anything CARLA renders.
- *Measured* on Bahonar's rebuilt world: no civilian route crosses a formerly private way, but the
  `access=no` service connector (way 26413344) admits `authority` and not `army`, so the naval
  vehicles' routes to the western towers are longer than under the access-keyed fence.

## Measured gotchas (each cost real time; do not relearn them)

The compiler enforces four of these on a specification — departure order, `--` in comments, `duarouter` validation with the false-accept guard, and plain seconds instead of `H:M:S` — and the rest remain judgements.

- **Validate routes with `duarouter`, not `sumolib.getShortestPath`.** sumolib gives false positives
  (it will traverse one-way edges the real router refuses), so hand-picked routes then fail at SUMO
  load with "no valid route". Batch candidate trips through
  `duarouter -n net -r trips.rou.xml -o out.rou.xml --ignore-errors`, and keep only those whose routed
  `<vehicle>` starts on the origin, ends on the destination and passes every `via` in order: a trip to
  an edge that does not exist still comes back as a `<vehicle>`, with a one-edge route.
- **A `<stop speed=…>` waypoint caps speed only between its own `startPos`/`endPos` on its own edge**
  (`MSVehicle.cpp` "process all stops and waypoints on the current edge"), not from the previous
  waypoint onward. Holding a whole phase to one speed needs one waypoint per edge in that phase.
- **A scalar `speedFactor` is a distribution, not a value** — SUMO applies the default `speedDev`
  0.1, so `speedFactor="1.25"` measured 1.31. Set `speedDev="0"` when the multiple must be exact.
- **The route file must be sorted by departure time** across flows and vehicles, or SUMO silently
  drops the out-of-order entries with only a warning. The compiler writes one departure-sorted
  timeline and refuses a route file out of order (check 29).
- **`--opposites.guess` yields zero opposite lanes on our netconvert output** (it compares
  junction-trimmed lane-shape endpoints). For centre-line overtaking, name the pairs explicitly with
  `allow_opposite_overtaking`. It does not rescue a two-way jam: SUMO refuses the manoeuvre while
  anything is oncoming, and once both directions queue, something always is.
- **netconvert's guessed traffic lights are fixed-time 90 s programs** that a busy interchange cannot
  discharge (population climbs, never settles). Use `NetconvertSettings(traffic_light_type="actuated")`.
- **A near-zero-length edge that spans real geometry** (two junctions netconvert failed to merge)
  blocks merging and stalls a ramp. `NetconvertSettings(junction_join_distance=25)` removes it.
- **A world's ramp meters are meters, not junctions.** The world build keeps every OSM
  `traffic_signals=ramp_meter` out of junction joining, so no meter signals the freeway, and runs it
  on a 6 s cycle of 2 s greens, a two-lane meter's lanes in turn; the green is SUMO's `s`, so a vehicle
  stops at the line and one leaves per green. Expect a few seconds' wait and a short queue at each.
- **A lane closure's `notify` must name every edge a vehicle can stand on waiting for the closed
  lanes**, at least the edge entering the closed one. SUMO refreshes a driver's choice of lanes only on
  those edges and the closed edge when the closure begins and ends; a driver stopped anywhere else
  keeps the choice it made while the lanes were shut, and with teleporting off it stays stopped.
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
comparable. The XML and CSV are the truth sidecar: they say which vehicles were planted in `marked`
(`1`/`0`, a `_carla` attribute in the XML and a column in the CSV, not a contract field), and
`special_type` is the vehicle's kind and nothing else: the kind the measured vehicle catalogue
(`--catalogue`, this repository's by default) curates for the CARLA blueprint a compiled vehicle
type names, and empty for a type that names none (06 D6.18). The UDP feed carries neither.
`base_type`, in every sink and in the callsign, is the catalogue's for the same blueprint, and the
vehicle class's (`passenger` a car, `delivery` a van) only for a type that names none of its
blueprints. A compiled scenario's ground truth is its `.supervision.json` (instances,
series, absences, cohorts), which this tool does not read and which joins to the sidecar by vehicle
id. A legacy scenario's rides in the `.labels.json` the telemetry tool reads:

- `marked_ids` — vehicle IDs flagged as anomalies (`marked=1`; a distinct affiliation only on the
  live feed, and only with `--marked-affiliation`).
- `affiliation_by_type` — CoT affiliation per SUMO vehicle type: civilian `n` (neutral), military
  `f` (friendly). The letter appears in `cot_type` = `a-<letter>-G-E-V`. It is the run's display
  convention when no display convention is given or found beside the scenario. The `u` it gave every
  anomaly type is not applied: that letter wrote the answer into the CoT type (06 §9.1).
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
