# Scenario specification

| | |
|---|---|
| File | `<scenario>.scenario.json` |
| Schema | `CarlaControl/skills/sumo-traffic-scenarios/schemas/scenario.schema.json` (`scenario.schema.json`) |
| Schema id | `urn:carla-sumo-capture:schema:scenario:1` |
| Format version | 1, in `spec_version` |

## What it is

A scenario specification is a SUMO scenario as its author writes it.\
It describes:

- the world the scenario runs in;
- what civil time simulated second zero is;
- the vehicles;
- the places they drive between;
- the actors and flows;
- schedules of repeated trips;
- lane closures;
- the windows worth capturing;
- the labels the scenario asserts.

You never write SUMO's XML.\
The compiler turns the specification into the SUMO files, routes every vehicle, and checks everything it can before a capture is run.

Times are written as civil times and places by name, and the compiler resolves both.\
Its resolution report (see [Scenario resolution report](Scenario_Resolution_Report.md)) states what each became.

## Who writes it and who reads it

- **A scenario developer writes it**, by hand, from a script (the `make_*_scenario.py` examples), or with the authoring skill's help.
- **`carla-compile-scenario` reads it** and checks it against this schema first (check 53).\
  A field the schema does not name is refused.\
  Every later check is listed in [Scenario compiler checks](Scenario_Checks.md).
- A sweep names a specification as its `base` (see [Sweep](Sweep.md)).
- `carla-drive --epoch` can read the `epoch` and `illumination` blocks out of a specification.

The schema ships with the authoring skill.\
`carla-compile-scenario --write-schema PATH` writes it.

## Fields

A time can be written in any of these forms:

- a number of seconds;
- `"dN HH:MM[:SS]"` (day N after the epoch's date);
- `"HH:MM[:SS]"`, only in a run of one day or less;
- an ISO 8601 instant at the epoch's offset;
- `{"instant": <name>}` or `{"at": <time>}`, either with `"plus": <duration>`.

A duration is seconds or `"[Nd][Nh][Nm][Ns]"`, such as `"1h30m"`.\
Never write a SUMO `H:M:S` value: SUMO reads it as an offset from second zero.

### Top level

| Field | Type | Required | Meaning |
|---|---|---|---|
| `spec_version` | constant `1` | yes | The format version of this file. |
| `scenario_id` | string | yes | The scenario's stable id: a letter or digit, then letters, digits, `_`, `.` or `-`. It names every compiled file. |
| `scenario_name` | string | yes | A short name for a person. |
| `description` | string | yes | What happens in the scenario. |
| `world.package` | string | yes | The world package (`.cwp`), relative to this file. |
| `world.network_fingerprint` | string, 64 hex digits | yes | The fingerprint of the network the scenario was written against. It must equal the package's (check 1). |
| `epoch` | object | yes | What simulated second zero is in civil time. See [Epoch](Epoch.md). The schema requires it; the compiler refuses a specification without one under check 33, which says why no epoch is assumed. |
| `illumination` | object | yes | The default for what the sun does across a capture window, which a run may override: `illumination_version` 1, a `policy` (`freeze_at_window_start`, `advance`, `freeze_at` or `ignore`) and that policy's fields. The schema requires it; the compiler refuses a specification without one under check 39. |
| `seeds.sumo` | integer, 0 or more | yes | SUMO's seed. |
| `simulation.end` | time | yes | When the scenario ends. |
| `simulation.step_length_s` | number above 0, seconds | yes | SUMO's step. |
| `catalogue` | string | yes | The measured vehicle catalog, relative to this file. |
| `vehicle_classes` | array, at least one | yes | The kinds of vehicle the scenario asks for. |
| `vehicle_mix` | string | no | The id given to the whole traffic mix, drawn by each class's `share`. |
| `vehicle_mixes` | array | no | More named mixes, each drawing on declared classes by its own shares. |
| `places` | object of name to place | no | Named places. A route never carries a bare edge id. |
| `place_sets` | object of name to place names | no | Named lists of places, for a schedule's subjects. |
| `instants` | object of name to time | no | Named times, for `{"instant": <name>}`. |
| `flows` | array | no | Streams of vehicles at a rate. |
| `actors` | array | no | Single vehicles, each with its own route and stops. |
| `rotas` | array | no | Schedules: one trip template repeated on given days and clock times. |
| `lane_closures` | array | no | Lanes taken out of service for a time, as an incident takes them. |
| `capture_windows` | array | no | The windows worth capturing, by id. A capture run names one. |
| `vocabulary` | object | no | The terms the labels use. |
| `supervision` | object | no | The labels the scenario asserts. |

### A vehicle class (`vehicle_classes[]`)

| Field | Type | Required | Meaning |
|---|---|---|---|
| `class_id` | string | yes | The class's id. Each body it draws becomes a vehicle type `<class_id>.<blueprint>`. |
| `blueprints` | array of strings, at least one | yes | CARLA blueprints the catalog measured. A class drawing one body warns (check 17). |
| `sumo_vclass` | string | yes | The SUMO vehicle class, such as `passenger` or `army`. It decides which lanes the vehicle may use. |
| `behaviour` | object of attribute to string | no | SUMO `vType` attributes copied through as written, such as `maxSpeed` or `speedFactor`. The dimensions always come from the catalog. |
| `share` | number, 0 or more | no | The class's weight in the whole mix. 0 keeps it out of the mix, for a class only a named vehicle uses. |
| `weights` | array of numbers above 0 | no | Each body's weight inside the class. Equal when left out. |
| `gui_shape`, `gui_colour` | string | no | How `sumo-gui` draws the class. Nothing else reads them. |
| `note` | string | no | A note written into the route file as a comment. |

A named mix (`vehicle_mixes[]`) has an `id`, `shares` (class id to a weight above 0), and an optional `note`.\
A flow's or actor's `type` is a class id, one of its vehicle types, the whole mix, or a named mix (check 16).

### A place (`places.<name>`)

A place takes exactly one of these forms:

| Form | Fields | Meaning |
|---|---|---|
| Edge | `edge`, optional `offset_m` | A network edge by id, and a position along it in meters. |
| Lane | `lane`, `offset_m` | A lane by id and a position along it. |
| Area | `area` | An area of interest by id (see [Areas of interest](Areas_Of_Interest.md)). |
| Street | `street`, `direction` (`north`, `east`, `south`, `west`), optional `at` (a cross street) or `near` (`{lat, lon}`) | One run of a named street, narrowed by direction and a cross street or a point. |
| Point | `lat`, `lon`, `max_snap_m`, optional `vclass` | The position on the nearest lane the class may use, refused past `max_snap_m` meters. |
| Gateway | `gateway` (a side), `travel` (`in` or `out`), optional `street` | Where a road enters or leaves the world on that side. |
| Turn | `from_street`, `to_street`, optional `from_direction`, `to_direction` | The two edges one connection joins. Use it in `via`. |

### Flows, actors and stops

| Field | Type | Required | Meaning |
|---|---|---|---|
| `flows[].id` | string | yes | The flow's id. |
| `flows[].type` | string | yes | A vehicle class, type or mix. |
| `flows[].from`, `flows[].to` | place name | yes | Where its vehicles enter and leave. |
| `flows[].via` | array of place names | no | Places every route passes, in order. |
| `flows[].vehs_per_hour` | number above 0 | yes | Vehicles per hour. |
| `flows[].begin`, `flows[].end` | time | yes | When the flow runs. |
| `flows[].depart_lane`, `flows[].depart_speed` | string | no | SUMO's `departLane` and `departSpeed`. Default `best` and `max`. |
| `actors[].id` | string | yes | The vehicle's id, which SUMO and the truth use. |
| `actors[].type` | string | yes | A vehicle class, type or mix. |
| `actors[].depart` | time | yes | When it departs. |
| `actors[].from`, `actors[].to`, `actors[].via` | place names | no | Its route, which the compiler routes with `duarouter`. |
| `actors[].route` | array of at least 2 place names | no | An explicit route instead, each place naming one edge. |
| `actors[].phases` | array | no | An explicit route in phases instead: each phase a `route` of places, an optional `repeat` (default 1), and an optional `hold`, a speed in m/s or `"posted"` for each edge's own limit. |
| `actors[].stops` | array | no | Its stops, in order. |
| `actors[].stops[].place` | place name | yes | Where it stops. The place must give a lane position. |
| `actors[].stops[].duration` | duration | no | How long it stops. |
| `actors[].stops[].until` | time | no | When it leaves the stop. |
| `actors[].stops[].parking` | boolean | no | Whether it leaves the lane while stopped. |
| `actors[].depart_lane`, `depart_speed`, `arrival_speed` | string | no | SUMO's `departLane`, `departSpeed` and `arrivalSpeed`. |

### A schedule (`rotas[]`)

| Field | Type | Required | Meaning |
|---|---|---|---|
| `id` | string | yes | The schedule's id. |
| `days` | `"N..M"` or array of integers | yes | The days, counted from the epoch's date. |
| `at` | array of `"HH:MM[:SS]"` | yes | The clock times on each day. |
| `subjects` | array of place names, or `{"place_set": <name>}` | yes | The places the trips serve. The template names the current one as `$subject`. |
| `id_pattern` | string | yes | Each trip's vehicle id, from `{day}`, `{hour}`, `{minute}`, `{subject_index}` and `{subject}`. |
| `template` | object | yes | The trip: `type`, and optionally `from`, `to`, `via`, `stops`, `depart_lane`, `depart_speed`, `arrival_speed`, as an actor has them. |
| `skip` | array | no | Occasions left out: `day`, `at`, `subject_index` or `subject`, and `because`, the reason. A skip must match exactly one occasion (check 48). |

### A lane closure (`lane_closures[]`) and a capture window (`capture_windows[]`)

| Field | Type | Required | Meaning |
|---|---|---|---|
| `lane_closures[].id` | string | yes | The closure's id. |
| `lane_closures[].place` | place name | yes | The one edge whose lanes close. |
| `lane_closures[].lanes` | array of integers, at least one | yes | The lanes that close, counted from 0 on the right. A closed lane admits only class `authority`. |
| `lane_closures[].notify` | array of place names | no | Where a vehicle learns of the closure and may be rerouted. The closed edge when left out. |
| `lane_closures[].begin`, `end` | time | yes | When the lanes are closed. |
| `capture_windows[].id` | string | yes | The window's id. |
| `capture_windows[].begin` | time | yes | When it opens. |
| `capture_windows[].length` | duration | yes | How long it lasts. |

### The vocabulary and the supervision

| Field | Type | Required | Meaning |
|---|---|---|---|
| `vocabulary.import` | array of paths | no | Namespace files to read, relative to this file. |
| `vocabulary.namespaces[]` | object | no | A namespace: `namespace` (lower case), `version`, `authority`, `terms`, and optional `roles` and `area_kinds`. |
| `...terms[].term` | string `ns:name` | yes | The term. |
| `...terms[].definition` | string | yes | What it means. |
| `...terms[].applies_to` | array of `entity`, `cohort` | yes | What it may label: one authored vehicle, or every vehicle of a flow. |
| `...terms[].since`, `status` | integer; `active` or `deprecated` | yes | The namespace version it appeared in, and its state. |
| `...terms[]` other fields | | no | `superseded_by`, `broader`, `parameters` (key to `type`, `unit`, `definition`), `counterfactual`, `contrast_with`, `hard_negative_for`, `exemplar_instances`. |
| `supervision.instances[]` | object | no | An assertion about one or more actors: `name`, `supervision` (`annotated` or `nominal`), `labels`, `participants` (`actor`, `role`), and optional `intervals`, `aoi_refs`, `parameters`, `hard_negative_for`, `counterfactual`. |
| `supervision.instances[].intervals[]` | object | no | A phase: `participant`, `phase`, and either `begin` with `end` or `duration`, or an `anchor` naming the participant's events (`depart`, `stop:<i>`, `stop_end:<i>`, `phase:<i>`). Never both (check 58). |
| `supervision.cohorts[]` | object | no | A flow's whole-life label: `flow`, `supervision` (`annotated`, `unlabelled` or `nominal`, which is refused), `labels`, `parameters`. |
| `supervision.series[]` | object | no | A schedule read as a recurring series: `series_id`, `rota`, `member_role`, `slot_length`, `slot_aoi_refs`, `supervision`, and optional `labels`, `parameters`, `hard_negative_for`. |

Every actor that no instance names, and every flow that no cohort names, is written into the supervision plan as `unlabelled`.\
See [Supervision plan](Supervision_Plan.md).

## Versions

This page describes version 1, the only version.\
The compiler refuses a specification with no `spec_version` or any other value (check 53).

## Example

The authoring skill's minimal example, `examples/minimal/street_layout_minimal.scenario.json`, shortened:

```json
{
  "spec_version": 1,
  "scenario_id": "street_layout_minimal",
  "scenario_name": "Street layout, minimal",
  "description": "Ambient traffic runs east; one saloon halts five minutes at a surveyed point and drives on.",
  "world": {"package": "StreetLayout.cwp",
            "network_fingerprint": "44194b18d22c91a25e85babe0469d43d064f3fb9aabe15fa62a486cd8fb4e2e0"},
  "epoch": {"epoch_version": 1, "civil_datetime": "2026-03-21T06:00:00-06:00",
            "utc_offset_hours": -6, "utc_datetime": "2026-03-21T12:00:00Z",
            "calendar_advances": true, "dst_in_effect": true, "time_zone_id": "America/Denver"},
  "illumination": {"illumination_version": 1, "policy": "freeze_at_window_start"},
  "seeds": {"sumo": 42},
  "simulation": {"end": "d0 08:00", "step_length_s": 0.05},
  "catalogue": "../../../../catalogue/vehicles.catalogue.json",
  "vehicle_classes": [
    {"class_id": "car", "blueprints": ["vehicle.lincoln.mkz", "vehicle.dodge.charger", "vehicle.mini.cooper"],
     "sumo_vclass": "passenger", "behaviour": {"maxSpeed": "40"}, "share": 1.0},
    {"class_id": "saloon", "blueprints": ["vehicle.lincoln.mkz"], "sumo_vclass": "passenger",
     "behaviour": {"maxSpeed": "40", "speedDev": "0"}, "share": 0.0}
  ],
  "vehicle_mix": "ambient_mix",
  "places": {"west_gate": {"gateway": "west", "travel": "in"},
             "east_gate": {"gateway": "east", "travel": "out"},
             "survey_point": {"lat": 39.49998486682519, "lon": -104.89941869548137, "max_snap_m": 5}},
  "flows": [{"id": "ambient", "type": "ambient_mix", "from": "west_gate", "to": "east_gate",
             "vehs_per_hour": 200, "begin": "d0 06:00", "end": "d0 08:00"}],
  "actors": [{"id": "probe", "type": "saloon", "depart": "d0 07:00", "from": "west_gate",
              "to": "east_gate", "stops": [{"place": "survey_point", "duration": "5m"}]}],
  "capture_windows": [{"id": "morning", "begin": "d0 06:55", "length": "15m"}]
}
```

```
carla-compile-scenario street_layout_minimal.scenario.json --out-dir Build/scenarios/street_layout_minimal
```
