# Writing a scenario

A scenario says what traffic SUMO drives through a world: which vehicles, where they go, when they
go, and what the scenario asserts about them. CARLA draws the vehicles SUMO moves, and a capture
writes the imagery and the truth.

You write a scenario as one JSON file, a scenario specification, named `<name>.scenario.json`. You
never write SUMO's XML. `carla-compile-scenario` checks the file against its world package, routes
every vehicle, and writes the SUMO files and a report of what everything resolved to.

This page covers the file, its labels, compiling it, sweeps, and running it. Every field is listed on
[Scenario specification](../Schemas/Scenario_Specification.md). This page explains how to use them.

The commands on this page are installed with the carlacontrol wheel. From a source checkout, the same
tools are `python CarlaControl/scripts/compile_scenario.py` (`carla-compile-scenario`),
`python CarlaNet/python/run_sumo_drive.py` (`carla-drive`) and
`python CarlaControl/scripts/run_camera_follower.py` (`carla-camera-follower`). Every command takes
`--help`.

## Before you start

You need:

- **A world package** (`.cwp`) for the place. [Building a world](Building_A_World.md) shows how to make
  one.
- **The package's network fingerprint.** A scenario names the network it was written for. The
  fingerprint is `NetworkFingerprint` in the package's `world.json`:

  ```
  python -c "import json, zipfile; print(json.loads(zipfile.ZipFile('Build/world-packages/Arapahoe_I25.cwp').read('world.json'))['NetworkFingerprint'])"
  ```

- **The vehicle catalog**, `vehicles.catalogue.json`: every vehicle body CARLA can draw, measured. It
  is `CarlaControl/catalogue/vehicles.catalogue.json` in a source checkout and
  `catalogue/vehicles.catalogue.json` in a distribution.
  [The vehicles reference](../../../CarlaControl/skills/sumo-traffic-scenarios/references/vehicles.md)
  lists every class and body in it.
- **SUMO 1.27.0**, the release that built the shipped worlds. The compiler routes with its
  `duarouter`, and refuses a release other than the one that built the world (check 6). From a source
  checkout it uses `Build/sumo-install`. Installed, it uses `SUMO_HOME`, then `PATH`; a distribution's
  `carla-env` script sets `SUMO_HOME`. `--sumo-home` names another installation.

In a source checkout, keep scenario files in `Import/` and compile them into `Build/scenarios/`. Paths
inside a scenario file are relative to the file.

## The scenario file

Here is a complete scenario. A shop on East Street gets a van delivery at 08:00, 09:00 and 10:00.
Traffic passes on East Street and on Cross Street. The 09:00 delivery is not made: the van due then
leaves on time and parks on Cross Street instead.

It is written for StreetLayout, a small test world that the compiler's tests build
(`CarlaControl/test/ScenarioWorldFixture.py`). West Street and East Street run east and west through
the origin. Cross Street crosses East Street at a signal about 100 m east of the origin. The example
needs one area of interest, `shop_front`, on East Street's eastbound lane, published into the package
from the areas file on [Building a world](Building_A_World.md#areas-of-interest). The scenario file
is in `Import/` and the package in `Build/world-packages/`.

The test world has no ground grids, so you can compile against it but not drive it. To use the
example on your own world, change `world`, the places and the times.

```json
{
  "spec_version": 1,
  "scenario_id": "shop_deliveries",
  "scenario_name": "Shop deliveries on East Street",
  "description": "A van delivers to a shop on East Street at 08:00, 09:00 and 10:00 while traffic passes. The 09:00 delivery is not made: the van due then parks on Cross Street instead.",
  "world": {
    "package": "../Build/world-packages/StreetLayout.cwp",
    "network_fingerprint": "44194b18d22c91a25e85babe0469d43d064f3fb9aabe15fa62a486cd8fb4e2e0"
  },
  "epoch": {
    "epoch_version": 1,
    "civil_datetime": "2026-05-12T07:30:00-06:00",
    "utc_offset_hours": -6,
    "utc_datetime": "2026-05-12T13:30:00Z",
    "calendar_advances": true,
    "dst_in_effect": true,
    "time_zone_id": "America/Denver",
    "note": "Simulated second zero is 07:30 Mountain Daylight Time on May 12, 2026."
  },
  "illumination": {"illumination_version": 1, "policy": "freeze_at_window_start"},
  "seeds": {"sumo": 7},
  "simulation": {"end": "d0 10:30", "step_length_s": 0.05},
  "catalogue": "../CarlaControl/catalogue/vehicles.catalogue.json",
  "vehicle_classes": [
    {"class_id": "car", "sumo_vclass": "passenger", "share": 0.85,
     "blueprints": ["vehicle.lincoln.mkz", "vehicle.dodge.charger", "vehicle.ue4.chevrolet.impala"]},
    {"class_id": "van", "sumo_vclass": "delivery", "share": 0.15,
     "blueprints": ["vehicle.sprinter.mercedes"], "behaviour": {"maxSpeed": "30"}}
  ],
  "vehicle_mix": "ambient_mix",
  "vehicle_mixes": [{"id": "cars_only", "shares": {"car": 1.0}}],
  "places": {
    "west_in": {"gateway": "west", "travel": "in"},
    "east_out": {"gateway": "east", "travel": "out"},
    "south_in": {"gateway": "south", "travel": "in"},
    "north_out": {"gateway": "north", "travel": "out"},
    "east_at_cross": {"street": "East Street", "direction": "east", "at": "Cross Street"},
    "shop_front": {"area": "shop_front"},
    "cross_street_bay": {"lat": 39.5004503, "lon": -104.8988105, "max_snap_m": 5}
  },
  "instants": {"missed_delivery": "d0 09:00"},
  "flows": [
    {"id": "eastbound", "type": "ambient_mix", "from": "west_in", "to": "east_out",
     "via": ["east_at_cross"], "vehs_per_hour": 300, "begin": "d0 07:30", "end": "d0 10:30"},
    {"id": "northbound", "type": "cars_only", "from": "south_in", "to": "north_out",
     "vehs_per_hour": 120, "begin": "d0 07:30", "end": "d0 10:30"}
  ],
  "actors": [
    {"id": "van_due_d0_h9", "type": "van", "depart": {"instant": "missed_delivery"},
     "from": "west_in", "to": "north_out",
     "stops": [{"place": "cross_street_bay", "duration": "4m", "parking": true}]}
  ],
  "rotas": [
    {"id": "deliveries", "days": [0], "at": ["08:00", "09:00", "10:00"],
     "subjects": ["shop_front"], "id_pattern": "delivery_d{day}_h{hour}",
     "template": {"type": "van", "from": "west_in", "to": "east_out",
                  "stops": [{"place": "$subject", "duration": "4m", "parking": true}]},
     "skip": [{"day": 0, "at": "09:00", "subject_index": 0,
               "because": "the van due at 09:00, van_due_d0_h9, parks on Cross Street instead"}]}
  ],
  "vocabulary": {
    "namespaces": [{
      "namespace": "shop", "version": 1, "authority": "the scenario's author",
      "terms": [
        {"term": "shop:scheduled_delivery", "since": 1, "status": "active", "applies_to": ["entity"],
         "definition": "A van makes a scheduled delivery: it parks at the shop front for a few minutes and drives on.",
         "hard_negative_for": ["shop:delivery_not_made"]},
        {"term": "shop:delivery_not_made", "since": 1, "status": "active", "applies_to": ["entity"],
         "definition": "The van due to make a scheduled delivery leaves on time but parks somewhere else, so the shop gets no delivery.",
         "parameters": {
           "expected_area": {"type": "string", "definition": "the id of the area of interest where the van was due"},
           "expected_time": {"type": "string", "definition": "the civil date and time, with its UTC offset, the van was due to leave"}},
         "counterfactual": {"kind": "term", "ref": "shop:scheduled_delivery"}}
      ],
      "roles": [{"role": "shop:delivery_van", "definition": "the van making a scheduled delivery"}],
      "area_kinds": [{"kind": "shop:storefront"}]
    }]
  },
  "supervision": {
    "instances": [
      {"name": "delivery_not_made_d0_h9", "supervision": "annotated",
       "labels": ["shop:delivery_not_made"],
       "participants": [{"actor": "van_due_d0_h9", "role": "subject"}],
       "intervals": [{"participant": "van_due_d0_h9", "phase": "parked_elsewhere",
                      "anchor": {"start": "stop:0", "end": "stop_end:0"}}],
       "aoi_refs": ["shop_front"],
       "parameters": {"expected_area": "shop_front", "expected_time": "2026-05-12T09:00:00-06:00"}}
    ],
    "series": [
      {"series_id": "scheduled_deliveries", "rota": "deliveries", "member_role": "shop:delivery_van",
       "slot_length": "5m", "slot_aoi_refs": {"shop_front": "shop_front"},
       "supervision": "nominal", "labels": ["shop:scheduled_delivery"]}
    ]
  },
  "capture_windows": [{"id": "around_nine", "begin": "d0 08:55", "length": "15m"}]
}
```

Compile it:

```
carla-compile-scenario Import/shop_deliveries.scenario.json --out-dir Build/scenarios/shop_deliveries
```

It compiles with two warnings, which [Reading the resolution report](#reading-the-resolution-report)
explains. The sections below go through the file block by block.

### Name, world, seed and run

- `spec_version` is always 1.
- `scenario_id` names every compiled file. It starts with a letter or digit, followed by letters,
  digits, `_`, `.` or `-`. `scenario_name` and `description` are for people.
- `world.package` is the path of the world package. `world.network_fingerprint` must equal the
  package's (check 1).
- `seeds.sumo` is SUMO's random seed. The same file, seed and world give the same traffic.
- `simulation.end` is when the run ends. `simulation.step_length_s` is SUMO's step, in seconds.
- `catalogue` is the path of the vehicle catalog.

### The epoch

SUMO counts seconds from zero and knows nothing else about time. The epoch says what simulated second
zero is in civil time at the site. Every time you write is read against it, and the sun is computed
from it.

- `civil_datetime` is second zero in local time, with its offset.
- `utc_offset_hours` is that offset in hours, daylight saving included: -6 for -06:00, 3.5 for +03:30.
- `utc_datetime` is the same instant in UTC. It must equal the local time minus the offset. The common
  mistake, the offset applied the wrong way, is refused by name (check 33).
- `dst_in_effect` says whether the offset includes daylight saving. Colorado in May is -06:00 with
  daylight saving. One offset holds for the whole run.
- `calendar_advances` says whether the date moves on when the run passes midnight.
- `time_zone_id` is for a reader. Nothing looks it up.
- `note` is one sentence saying what second zero is.

Second zero need not be midnight. In the example it is 07:30, when the traffic starts, so the run does
not simulate an empty road from midnight. Half-hour offsets are ordinary: Iran is +03:30. The compiler
warns when your offset is more than an hour from the one the world's longitude gives (check 40). See
[Epoch](../Schemas/Epoch.md).

**Write times as civil times.** The forms are:

| Form | Example | Means |
|---|---|---|
| Day and clock | `"d0 09:00"`, `"d2 23:30:15"` | that clock time on the epoch's date plus N days |
| Clock alone | `"09:00"` | day 0; only in a run of one day or less |
| Absolute | `"2026-05-12T09:00:00-06:00"` | that instant; it must carry the epoch's offset |
| Seconds | `5400` | simulated seconds from zero |
| Duration | `"4m"`, `"1h30m"`, `"7d"`, `"274s"` | a length of time |
| Named | `{"instant": "missed_delivery"}`, with `"plus": "15m"` if you like | a time from `instants` |

In the example, `"d0 09:00"` is second 5400. Never write SUMO's `H:M:S` form: SUMO reads `9:00:00` as
nine hours after second zero, which is not 09:00 unless second zero is midnight.

### Illumination

`illumination` is required. It is the default for what the sun does during a capture window. A run
may override it.

| `policy` | The sun during a window |
|---|---|
| `freeze_at_window_start` | set to the civil time the window opens, and held there. Recommended: one lighting condition per window. |
| `advance` | moves on at `rate_sun_s_per_sim_s` seconds of sun time per simulated second. |
| `freeze_at` | held at `freeze_at_civil_time`, written `HH:MM:SS`. |
| `ignore` | left as the world has it. |

Light is never a label. The report gives the sun's elevation for each capture window.

### Vehicle classes and mixes

A vehicle class is one kind of vehicle the scenario asks for:

- `class_id` names it.
- `blueprints` lists the CARLA bodies it draws. Each must be in the vehicle catalog (check 14).
- `sumo_vclass` is the SUMO vehicle class, such as `passenger`, `delivery` or `army`. It decides
  which lanes the vehicle may use.
- `share` is the class's weight in the whole traffic mix. A share of 0 keeps it out of the mix.
- `weights` gives each body's weight inside the class. They are equal when you leave it out.
- The `behaviour` field holds SUMO vehicle type attributes, copied as written, such as `maxSpeed`,
  `speedFactor` or `speedDev`. Do not give a length, width or height: they come from the catalog.

The compiler writes one SUMO vehicle type per body, named `<class_id>.<blueprint>`, with the body's
measured size and the CARLA body it binds. That way the vehicle SUMO makes room for is the vehicle
CARLA draws.

`vehicle_mix` names the whole mix, drawn from every class by its `share`. `vehicle_mixes` declares
more mixes, each with its own `shares`. In the example, the eastbound flow draws `ambient_mix` (cars
and vans) and the northbound flow draws `cars_only`. A flow's or actor's `type` is a class, one of its
vehicle types, the whole mix, or a named mix.

Things to know:

- A class that draws one body warns (check 17): every vehicle of the class looks the same, so its look
  can stand in for its label. The catalog has one van, so the example's `van` class warns.
- Give a planted vehicle the class of the traffic around it, unless its driving is the behavior you are
  labeling. A vehicle type that only planted vehicles use tells a model which vehicles are planted.
  The example's van that skips its delivery is a `van`, like the routine deliveries and the vans in
  the eastbound traffic.
- The catalog has no motorcycles, bicycles or pedestrians, and no pickup truck. Do not ask for them,
  and do not swap in a body of another kind. The drive never draws a body the catalog did not measure.
- In the current catalog, no body's headlights, brake lights or turn signals show when switched on.
  The vehicles reference lists this for each body.

### Places

Flows and actors never name edges directly. They name places from `places`, and the compiler resolves
each place to edges and lanes. It refuses a place rather than guess: a street that runs over several
edges is refused, with every candidate listed.

| Form | Example | Resolves to |
|---|---|---|
| Edge | `{"edge": "901#0"}`, optionally `"offset_m"` | that edge |
| Lane | `{"lane": "901#0_0", "offset_m": 50}` | that position on that lane |
| Area | `{"area": "shop_front"}` | the lanes in that area of interest |
| Street | `{"street": "East Street", "direction": "east", "at": "Cross Street"}` | the one edge of the street, heading east, that arrives at Cross Street |
| Street near a point | `{"street": "...", "direction": "...", "near": {"lat": ..., "lon": ...}}` | the edge of the street nearest the point |
| Point | `{"lat": 39.5004503, "lon": -104.8988105, "max_snap_m": 5}`, optionally `"vclass"` | the position on the nearest lane, refused past `max_snap_m` meters |
| Gateway | `{"gateway": "west", "travel": "in"}`, optionally `"street"` | where a road enters (`in`) or leaves (`out`) the world on that side |
| Turn | `{"from_street": "...", "to_street": "..."}` | the two edges one turn joins; use it in `via` |

What each use needs:

- An origin (`from`), a destination (`to`) and each `via` need one edge.
- A stop needs a position on a lane: a lane with an offset, an edge with an offset, a point, or an
  area that holds exactly one lane.

Street names come from the world's place index, `places.json` in the package. On a map with few street
names, use gateways, points, areas and lanes. Only 4.5% of the Shahid Bahonar port's edges have a
name.

`place_sets` names lists of places, for a schedule that serves several places.

### Flows

A flow is a stream of vehicles at a rate: `id`, `type`, `from`, `to`, an optional `via`,
`vehs_per_hour`, `begin` and `end`. `depart_lane` and `depart_speed` default to SUMO's `best` and
`max`.

The compiler routes each flow with SUMO's `duarouter`, and every vehicle of the flow takes that route.

### Actors, routes and stops

An actor is one vehicle:

- `id` is its id in SUMO and in the truth.
- `type` and `depart` are its vehicle type and its departure time.
- Its route is one of:
  - `from`, `to` and an optional `via`, which the compiler routes;
  - `route`, a list of places, each naming one edge;
  - `phases`, a route in parts, each with an optional `repeat` count and an optional `hold` speed.
    This is how a vehicle circles a block.
- `stops` lists its stops in order. Each has a `place`, a `duration` or an `until` time, and
  `parking`.

Set `"parking": true` for any long stop on a road where traffic must get past. A vehicle stopped in a
single running lane blocks it for the whole stop.

### Schedules

A schedule repeats one trip on given days at given clock times. The field is named `rotas`.

- `days` is `"0..6"` or a list of day numbers, counted from the epoch's date.
- `at` lists the clock times on each day.
- `subjects` lists the places the trips serve, or names a place set: `{"place_set": "towers"}`.
  There is one trip per day, per time, per subject.
- `template` is the trip: a `type` and optionally `from`, `to`, `via`, `stops` and the departure
  settings. `"$subject"` stands for the trip's subject place.
- `id_pattern` makes each trip's vehicle id from `{day}`, `{hour}`, `{minute}`, `{subject_index}` and
  `{subject}`. The numbers are not padded with zeros. Make the pattern give every trip its own id:
  with trips at 09:00 and 09:30, `{hour}` alone gives both the same id, and the compile is refused
  (check 48).
- `skip` leaves occasions out. Each skip gives the `day`, the `at` time and the `subject_index` or
  `subject`, and `because`, your reason. A skip must match exactly one occasion (check 48).

A skipped occasion writes no trip and no label. In the example, the schedule has three occasions and
skips one, so it sends two vans: `delivery_d0_h8` and `delivery_d0_h10`.

### Capture windows

`capture_windows` lists the windows worth capturing: an `id`, a `begin` time and a `length`. A
window does not start a capture. A capture run names the window it captures. A window must lie
inside the run and may not cut through a labeled interval (check 38).

### Lane closures

`lane_closures` takes lanes out of service for a time, as an incident does: the edge, the lanes
counted from 0 on the right, the edges where vehicles learn of the closure, and the begin and end
times. A closed lane admits only the `authority` class. See
[Scenario specification](../Schemas/Scenario_Specification.md). The Arapahoe underpass dwell closes
five of the six northbound lanes of I-25 for three minutes.

## The labels

Labels are what the scenario asserts about its vehicles, for whoever trains a model on the result.
They go in the `supervision` block, and the terms they use go in the `vocabulary` block. The compiler
writes them only into `<scenario_id>.supervision.json`, the supervision plan. The SUMO files carry
none (check 52). See [Supervision plan](../Schemas/Supervision_Plan.md).

### Three states

- `annotated`: the vehicle carries out the labeled pattern.
- `nominal`: the vehicle carries out no target pattern. It is a matched negative: it looks like the
  pattern in most ways and is not it.
- `unlabelled`: no assertion. Every actor and flow you do not label is written as `unlabelled`.

When something is annotated, label something nominal too. The compiler warns when nothing is
(check 24).

### The vocabulary

The terms are yours. Declare them in `vocabulary.namespaces`. A namespace has a lower-case
`namespace`, a `version`, an `authority`, its `terms`, and optionally `roles` and `area_kinds`.

A term has:

- `term`, written `<namespace>:<name>`, such as `shop:delivery_not_made`;
- `definition`, what it means, in words someone who has never met you can follow;
- `applies_to`: `entity` for one authored vehicle, `cohort` for every vehicle of a flow;
- `since`, the namespace version it first appeared in, and `status`, `active` or `deprecated`.

A term may also relate to others: `broader`, `contrast_with`, `hard_negative_for`, `counterfactual`,
`superseded_by`. It may list `exemplar_instances`, and it may declare `parameters` (below).

The compiler refuses a label that is not a declared term (check 18), a term attached to a kind of
subject its `applies_to` excludes (check 45), and a namespace nobody declared (check 46).
`vocabulary.import` reads namespaces from files, so several scenarios can share one vocabulary.

### Instances, participants and roles

An instance is an assertion about one or more actors:

- `name`, unique in the scenario;
- `supervision`: `annotated` or `nominal`;
- `labels`: the terms it asserts;
- `participants`: each an `actor` and its `role`;
- optionally `intervals`, `aoi_refs` (the areas it is about), and `parameters`.

Every instance has at least one participant (check 19). An instance with one participant gives it the
role `subject` (check 50). With several participants, each role other than `subject` is declared in
the namespace's `roles`, such as `escort:lead_vehicle`.

### Phases and intervals

An interval marks a phase of a participant's part in the pattern: its `participant`, a `phase` name,
and either a civil time (`begin` with `end` or `duration`) or an `anchor`, never both (check 58).

An anchor ties the interval to the vehicle's own events:

- `depart`: when it enters the simulation;
- `stop:0`, `stop:1`, ...: when it arrives at that stop;
- `stop_end:0`, ...: when it leaves that stop;
- `phase:0`, ...: when it enters that part of a `phases` route.

Indexes count from 0. Use an anchor for a stop. SUMO decides when a vehicle arrives, so a civil begin
time for a stop is a guess. The run records when each anchored interval actually opened and closed.

In the example, `parked_elsewhere` runs from the van's arrival at its stop to its departure from it.

### Series

A series reads a schedule as a recurring pattern, with one slot for each trip the schedule sends.

- `series_id` names it. Its `rota` field names the schedule it reads, by the schedule's `id`.
- `member_role` is the role of each slot's vehicle.
- `slot_length` is how long each slot lasts. A slot starts when its vehicle departs, so make it cover
  the drive to the place as well as the stop. The example uses 5 minutes for a 4-minute stop.
- `slot_aoi_refs` maps each subject place to the id of its area of interest.
- `supervision` and `labels` say what each slot asserts.

A skipped occasion has no slot.

### Cohorts

A cohort labels every vehicle of a flow, for its whole life: `flow`, `supervision` and `labels`. A
cohort is `annotated` or `unlabelled`, never `nominal` (check 49), and has no intervals (check 23). A
flow that no cohort names is `unlabelled`.

### A label follows its vehicle

Every label is about a vehicle. SUMO reports vehicles, and each row of the supervision plan names the
vehicle it is about. Nothing labels an empty place, and nothing is written for each frame.

### An omission: label the vehicle that deviates

To plant a missing event, such as a delivery not made or a post left unmanned, label the vehicle that
should have filled it and did something else. The example does it this way:

1. **Keep the schedule's skip.** It removes the routine 09:00 delivery, so no ordinary van fills it.
   The skip's `because` says which vehicle deviates. It is your note, not a label.
2. **Add the deviating vehicle as an actor.** Make it match the routine trips in everything but the
   behavior: the same class (`van`), the same start (`west_in`), the occasion's departure time
   (09:00), and the same stay (4 minutes, parked). Only where it parks differs. Any other difference
   would be a second signal the label does not name.
3. **Label it** with a term that says what it does instead: `shop:delivery_not_made`.
4. **Say where and when it was expected** in the instance's `parameters`: `expected_area` and
   `expected_time`. The empty place reaches the record as a value on the vehicle's label, never as a
   label of its own.
5. **Anchor its interval** to its stop: from `stop:0` to `stop_end:0`.
6. **List the deviation in the routine term's `hard_negative_for`.** `shop:scheduled_delivery` lists
   `shop:delivery_not_made`, so each routine delivery is a matched negative for it.

### Parameters

Parameters hold a subject's values, on an instance, a series or a cohort. Each key must be declared in
the `parameters` of one of the subject's label terms, with a `type` (`number`, `integer`, `string` or
`boolean`), a `unit` where it has one, and a `definition`. The compiler refuses a key no label
declares, and a value of the wrong type (check 56).

`hard_negative_for` on a term is copied onto every nominal subject that carries the term. Do not
restate it on a subject, or restate it exactly (check 57).

## Compiling

```
carla-compile-scenario Import/shop_deliveries.scenario.json --out-dir Build/scenarios/shop_deliveries
```

The compiler writes into the `--out-dir` folder:

| File | What it is |
|---|---|
| `<scenario_id>.rou.xml` | the vehicle types and every vehicle and flow, already routed, with times in plain seconds |
| `<scenario_id>.sumocfg` | the SUMO configuration: the seed, the step, the end and the traffic options |
| `<scenario_id>.add.xml` | the lane closures, only when the scenario has some |
| `<MapName>.net.xml` | the world's own network, copied from the package |
| `<scenario_id>.supervision.json` | the labels |
| `<scenario_id>.lock.json` | the digests of every file, the seed, the epoch and the versions, which a run checks |
| `<scenario_id>.resolution.md` and `.json` | what everything resolved to, and every finding |

The exit status is 0 when the scenario compiled and 1 when it was refused. A refused compile writes
only the resolution report, which names every refusal.

The compiler sets these SUMO options in the configuration. You do not set them:

| Option | Value | Why |
|---|---|---|
| `time-to-teleport` | -1 | SUMO never moves a stuck vehicle ahead, so a traffic jam stays where it is. |
| `max-depart-delay` | 900 | A vehicle that cannot enter within 900 s is dropped. |
| `collision.action` | `warn` | Collisions are reported, not hidden. |
| `lanechange.duration` | 3 | A lane change takes 3 seconds instead of one step. |

### What the compiler checks

The compiler runs its checks in stages: the file's shape, the world, the epoch, times, places and
vehicles, the routes, the labels, the light, the files it writes, and a run in SUMO alone. Every check
in a stage runs, so one compile reports all the problems that stage can see. A stage that refuses
stops the compile.

`CarlaControl/skills/sumo-traffic-scenarios/checks.json` lists every check, by a number that never
changes. Each entry says what the check establishes, what it compares against, whether it refuses or
warns, and the failure it prevents. A finding in a report cites a check by that number. See
[Scenario compiler checks](../Schemas/Scenario_Checks.md).

- A **refusal** stops the compile. Nothing but the report is written.
- A **warning** lets the compile go on. The report carries it in full.

### Reading the resolution report

Read the report after every compile and check it against what you meant. `sumo-gui` shows elapsed
seconds and knows nothing of labels, dates or the sun, so the report is the one place to check them.
`<scenario_id>.resolution.md` is laid out for reading. `<scenario_id>.resolution.json` holds every
field. See [Scenario resolution report](../Schemas/Scenario_Resolution_Report.md).

Check, in order:

- **Findings.** Every refusal and warning, with its check number.
- **Epoch.** The civil time of second zero and of the end.
- **Capture windows.** Each window's civil times and the sun's elevation when it opens and closes.
  The JSON's `capture_windows[].sun_lowest` gives the lowest elevation and its band, such as `day` or
  `night`. Below -6 degrees, the scene has no light source.
- **Places.** What each place became. For the example, `shop_front` became lane `901#0_0` at 51.5 m
  and `east_at_cross` became edge `901#0`. For a point, the JSON's `places.<name>.detail` gives how
  far it was moved to reach a lane: `snapped 0.01 m to -902#0_0`. The `.md` file does not show it.
- **Schedules.** How many trips each sent, and each skip with its civil time and reason.
- **Routes.** Each vehicle's and flow's route as `duarouter` found it, its length, and its time at
  the speed limit.
- **Vehicle types.** Each type and the body it binds, with its size.
- **Supervision.** Each instance, its participants and intervals, and each series with its slots.
- **Dry run.** What the SUMO-only run showed (see below).

The example compiles with two warnings:

- **Check 17**, because the `van` class draws one body.
- **Check 41**, the illumination-label association: how much the light band tells about the label,
  over the capture windows, or over the whole run when there are none. It is always a warning and
  never a refusal. In a pattern of life, routine behavior often follows the clock, and so does the
  light. The report gives the figure, a table of labels by band, and ways to change the figure. In the
  example, everything in the window is in the `day` band, so the figure is 0.000.

### A refusal, and how to fix it

Take the example and drop `"at": "Cross Street"` from `east_at_cross`. East Street then names two
eastbound edges, one on each side of Cross Street. The compile is refused:

```
check 17 WARN vehicle class van: draws one body, vehicle.sprinter.mercedes, so every vehicle of the class looks the same and its appearance can become its label
check 7 REFUSE flow eastbound via: place 'east_at_cross' names 2 edges and one is needed here: 901#0 (east, 93.29 m, extent [0.0, 1.68, 93.29, 1.68]); 901#1 (east, 93.28 m, extent [107.99, 1.68, 201.27, 1.68]). Narrow it -- with 'at' for a street, or by naming the edge or lane
resolution    Build\scenarios\shop_deliveries\shop_deliveries.resolution.json
resolution_md Build\scenarios\shop_deliveries\shop_deliveries.resolution.md
REFUSED
```

The compiler writes only `shop_deliveries.resolution.md` and `.json`, with the refusal in their
findings. The message lists both candidates and how to narrow the place. Put `"at": "Cross Street"`
back, or name the edge, and compile again.

A refused compile does not remove the files of an earlier compile in the same folder. Only the report
is new. Do not run the SUMO files left there as if they were the scenario you just changed.

Common refusals:

| Check | What it means | What to do |
|---|---|---|
| 1 | The scenario's network fingerprint is not the package's. | The message gives the package's fingerprint. Copy it in, compile, and check the places in the report. |
| 6 | The SUMO doing the routing is not the release that built the world. | Use SUMO 1.27.0 with `--sumo-home`. `--allow-sumo-version-mismatch` compiles anyway and records that in the lock. |
| 7 | A place is not in the world, or names several things where one is needed. | Read the candidates in the message and narrow the place. |
| 10 | A vehicle's class may not drive an edge of its route. | Choose another route or class. To open a road to a class, build the world with a type map. |
| 33 | The epoch's UTC time is not the local time minus the offset. | The message gives the right UTC time. A sign error is named as one. |
| 48 | A skip matches no occasion of its schedule. | Match its `day`, `at` and subject to an occasion. |
| 53 | A field is unknown, or has the wrong type. | Check the spelling against the schema. |
| 59 | A labeled vehicle never got into SUMO. | Read the dry run section for its wait. Give it room: another departure time, another entrance, or less traffic there. |

### The SUMO-only run

Before writing anything, the compiler runs the compiled scenario in SUMO alone over its whole length.
It refuses the scenario when a vehicle the supervision plan names never gets in: dropped after
waiting 900 s at its entrance, or still waiting when the run ends (check 59). The report's dry run
section gives every labeled vehicle's wait, the other vehicles dropped, and every collision.

The run takes seconds to minutes. Measured: the example's 3 hours took 2.4 s; the Arapahoe underpass
dwell (45 minutes, 7,433 vehicles at a 0.05 s step) took 153 s; the Shahid Bahonar pattern of life
(7 days, 69,246 vehicles at a 1 s step) took 146 s.

`--skip-dry-run` skips it while you draft. The lock records that it was skipped, and `carla-capture`
and `carla-drive` refuse such a scenario unless told to accept it. Compile without it before you
capture.

## Sweeps

A sweep compiles one base scenario many times with values changed. Each version is a member, compiled
in full into its own folder, so a member that does not route fails now and not halfway through a set
of captures.

A sweep is a file, `<name>.sweep.json`, beside the base scenario:

```json
{
  "sweep_version": 1,
  "sweep_id": "shop_deliveries_seeds",
  "base": "shop_deliveries.scenario.json",
  "axes": [{"path": "seeds.sumo", "values": [7, 8, 9]}],
  "counterfactuals": [
    {"actor": "van_due_d0_h9", "mode": "absent"},
    {"actor": "van_due_d0_h9", "mode": "nominal", "remove": ["stops"]}
  ]
}
```

```
carla-compile-scenario --sweep Import/shop_deliveries_seeds.sweep.json --out-dir Build/scenarios/shop_deliveries_seeds
```

This sweep has nine members: three seeds, and for each seed the base and two counterfactual twins.

- `axes` lists what to vary. `path` names a field, such as `seeds.sumo` or
  `actors.van_due_d0_h9.stops[0].duration`. A list entry is named by its `id`, `name`, `class_id`,
  `series_id` or `flow`. `values` lists the values.
- `pairing`: `cross` (the default) makes every combination of the axes' values; `zip` takes the first
  value of each axis together, then the second, and so on.
- `counterfactuals` makes, for every member, a twin that differs in one actor:
  - `absent` removes the actor;
  - `nominal` keeps its type, route and time, takes away what `remove` names (`stops`, `via`), and
    labels it nominal;
  - `displaced` moves it in time (`shift`) or swaps places in its route and stops (`places`).

  A pair has the same inputs except one vehicle. Its trajectories are not expected to match, because
  the vehicles behind react to what is in front of them.

The compiler writes each member into `<out-dir>/<member id>/`, with the member's own scenario file,
and writes `<sweep_id>.sweep-index.json` into the output folder. The index lists every member, its
axis values, its outcome and its findings, and every pair. A member's id is the base scenario's id and
a short code for its values, such as `shop_deliveries.mcb1102d16d`; a twin adds `.cf.<actor>.<mode>`.
The console does not print a member's warnings. Read them in the index. See [Sweep](../Schemas/Sweep.md)
and [Sweep index](../Schemas/Sweep_Index.md).

### Sweeping the light

An axis that touches the `epoch`, the `illumination` or a window's `begin` changes the light. The
sweep's `illumination` setting decides what is allowed:

- `hold`, the default, refuses such an axis. A sweep that varies behavior keeps the light fixed.
- `vary` allows only such axes.
- `factorial` allows both, and warns that the result does not compare either one cleanly (check 43).

To vary the light, sweep `epoch.date`, not the window's hour. The date moves the sun and leaves the
traffic alone; the hour moves both.

```json
{
  "sweep_version": 1,
  "sweep_id": "shop_deliveries_dates",
  "base": "shop_deliveries.scenario.json",
  "illumination": "vary",
  "axes": [{"path": "epoch.date", "values": ["2026-05-12", "2026-06-21", "2026-09-22"],
            "kind": "illumination"}]
}
```

An axis's `kind` says what you mean it to vary. An axis declared `behaviour` that changes the light
is refused. The example's window then opens with the sun at 34.2, 36.2 and 23.4 degrees.

A parameter that holds a civil date is text. An `epoch.date` sweep does not change it: in every
member above, `expected_time` still reads `2026-05-12T09:00:00-06:00`. A `vary` sweep also refuses an
axis that would change it.

### Keep the output path short on Windows

A member's SUMO-only run writes a file at
`<out-dir>\<member id>\.<member id>.dry-run\<member id>.sumocfg`, which repeats the member id three
times. When that full path passes 260 characters, SUMO cannot open it. The member is then refused
with check 11 or 59, and a message that a file "is not accessible" or that SUMO "Could not access
configuration".

The example's twins have 52-character ids, such as
`shop_deliveries.mcb1102d16d.cf.van_due_d0_h9.absent`, so the full path of its output folder must
stay under about 80 characters. A longer scenario id or actor id leaves less room. Work in a folder
with a short path, such as `C:\carla`, and keep `--out-dir` short.

## The shipped examples

The authoring skill's examples are in `CarlaControl/skills/sumo-traffic-scenarios/examples/`. Each
is written for the StreetLayout test world and comes with the resolution report its compile
produced. The test suite compiles every one and compares the result with that report.

| Example | What it shows |
|---|---|
| `minimal/street_layout_minimal.scenario.json` | the least a scenario declares: one flow, and one actor that stops at a point; no labels |
| `counterfactual/street_layout_probe.scenario.json` and `probe_standoff_pairs.sweep.json` | every kind of label (an annotated instance, a nominal instance, an unlabeled cohort, a series with a skipped occasion), and a sweep pairing the annotated actor three ways |
| `epoch/street_layout_epoch_whole_hour.scenario.json` | Mountain Standard Time, -07:00 |
| `epoch/street_layout_epoch_daylight_saving.scenario.json` | Mountain Daylight Time, -06:00 with daylight saving |
| `epoch/street_layout_epoch_half_hour.scenario.json` | Iran's +03:30 on a Colorado world: it compiles as declared, check 40 warns that the offset is far from the world's, and the window's sun is 25.7 degrees below the horizon |

`Import/` holds four scenarios on real worlds, each compiled, with its resolution report beside it.
A script in `CarlaControl/scripts/` writes each one and compiles it:

| Scenario | Script | What it shows |
|---|---|---|
| `Gardnerville_Centerville_Lane_NeighborhoodOrbit` | `make_sumo_scenario.py` | one vehicle circling a block 20 times, written as a route in phases |
| `Arapahoe_I25_UnderpassDwell` | `make_arapahoe_scenario.py` | heavy freeway traffic, a lane closure, and a vehicle parked under a bridge for 30 minutes |
| `Arapahoe_I25_SupervisionCheck` | `make_supervision_check_scenario.py` | six minutes with an anchored dwell, a transit in phases, and a nominal stop |
| `Shahid_Bahonar_Port_PatternOfLife` | `make_bahonar_scenario.py` | a week of port life in civil clocks at +03:30, a guard schedule over 16 towers, and six planted anomalies, one of them an omission |

These scenarios name their packages at `../Build/world-packages/<MapName>.cwp`. Build each world first
(see [Building a world](Building_A_World.md)). When your package's fingerprint differs from the one a
scenario names, run its script: it writes the scenario against the package it is given
(`--world-package`) and compiles it.

## Running and looking at it

`carla-drive` drives the vehicles of a compiled scenario from SUMO, in a world loaded on a running
CARLA server. With `--sumo-gui`, SUMO's own window shows the simulation the drive is stepping.

The drive needs:

- a CARLA server with the world loaded, such as the one `carla-build-world` just built. The drive
  refuses a package that does not describe the loaded world.
- SUMO 1.27.0, the release that built the world. The drive refuses another release. In a source
  checkout, `Build/sumo-install` has `sumo-gui`. A distribution has no `sumo-gui`. Install SUMO 1.27.0
  yourself and point the drive at it with `--sumo-home <folder>`, or set `CARLANET_SUMO_HOME` to that
  folder.

To watch the Arapahoe supervision check at the pace of real traffic, from a source checkout:

```
carla-drive --scenario Import/Arapahoe_I25_SupervisionCheck.sumocfg --world-package Build/world-packages/Arapahoe_I25.cwp --epoch Import/Arapahoe_I25_SupervisionCheck.scenario.json --no-record --real-time-factor 1.0 --steps 0 --sumo-gui
```

- `--scenario` is the compiled `.sumocfg`. The drive checks it against the lock beside it.
- `--epoch` is the scenario file. The drive reads its epoch and its illumination. Do not also give
  `--illumination`: the drive refuses a policy given in two places, even when both agree.
- `--no-record` spawns no camera and writes no frames.
- `--real-time-factor 1.0` holds the run to the wall clock. The default, 0, runs as fast as the
  machine allows.
- `--steps 0` runs until the scenario ends. Without it, the drive stops after 600 SUMO steps.
- `--warm-up SECONDS` runs SUMO ahead to that second before the first CARLA tick, so the view starts
  near the part you want to see. The resolution report gives each time in seconds.

Pausing the `sumo-gui` window pauses the drive. Closing it stops the run.

To look at the vehicles in CARLA, place a camera of your own with `carla-camera-follower`, in another
terminal. It can start before, during or after the drive, and it records nothing. To stare at the
curb on South Yosemite Street that this scenario's capture looks at:

```
carla-camera-follower --stare-look-at -374.2 -313.7 --stare-altitude-m 70 --stare-standoff-m 25 --stare-bearing-deg 90 --fov 50
```

Positions are CARLA meters: x east, y south, so north is negative y. The package's
`areas.resolved.json` gives each area of interest's position in CARLA meters (see
[Resolved areas of interest](../Schemas/Areas_Resolved.md)). Esc or Q closes the window.

Capturing imagery and truth from a compiled scenario uses `carla-capture` and a run file (see
[Run configuration](../Schemas/Run_Configuration.md)), and has its own guide.

## Writing scenarios with an AI assistant

The `sumo-traffic-scenarios` skill is a set of instructions an AI coding assistant loads to write and
compile scenarios with you. It is `CarlaControl/skills/sumo-traffic-scenarios/` in a source checkout
and `skills/sumo-traffic-scenarios/` in a distribution. It holds
[`SKILL.md`](../../../CarlaControl/skills/sumo-traffic-scenarios/SKILL.md), `checks.json`, the
scenario and sweep schemas, the examples above, and references on places and times, the epoch,
illumination, vehicles, and known SUMO problems.

What to expect from an assistant that uses it:

- It asks you what civil date and time second zero is, rather than guess.
- It asks you for each label's term and definition, and does not make up terms for you.
- It compiles the scenario and reads the report back to you, including the light each window is
  under and the illumination-label figure.

[Authoring skills](../Skills/Authoring_Skills.md) says where the skills live and how an assistant
finds them.
