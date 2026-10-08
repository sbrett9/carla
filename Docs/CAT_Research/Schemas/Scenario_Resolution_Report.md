# Scenario resolution report

| | |
|---|---|
| File | `<scenario_id>.resolution.json`, with `<scenario_id>.resolution.md` beside it |
| Schema | `CarlaControl/schemas/scenario_resolution.schema.json` |
| Schema id | `urn:carla-sumo-capture:schema:scenario-resolution:1` |
| Format version | 1, in `resolution_version` |

## What it is

The resolution report states what a scenario compile resolved, so its author can check it against what they meant.\
It lists:

- every place and the edge or lane it became
- every named time with its second and its civil time
- every schedule and skip
- every route as `duarouter` produced it
- every lane closure
- every vehicle type and the body it binds
- every capture window with its civil date and the sun it opens under
- the supervision
- what a SUMO-only run showed
- every finding in full
- the lock

`sumo-gui` shows elapsed seconds and knows nothing about labels, dates or the sun.\
So this report is the one place to check a label, a date or a sun before a capture is run.\
The `.md` file is the same report formatted for reading.

**This is not the run resolution report.**\
A capture run writes `run.resolution.json` about one launch (see [Run resolution report](Run_Resolution_Report.md)).

## Who writes it and who reads it

- **`carla-compile-scenario` writes it** into the output folder on every compile.\
  A refused compile writes only the report, marked `refused`, with every refusal.\
  When the specification gives no `scenario_id`, the report is named after the specification file instead.
- **A scenario developer and the authoring skill read it.**\
  The skill's examples keep a recorded report beside each example.\
  A test compiles each example and compares.
- No tool reads it as input.

## Fields

The report's sections appear in the order below.\
Only the sections the compile reached appear.\
A compile refused at its first stage has only `resolution_version`, `producer`, `outcome`, `scenario` and `findings`.\
When the specification is not JSON, `scenario` is left out too.

| Section | Type | Meaning |
|---|---|---|
| `resolution_version` | constant `1` | The format version of this file. |
| `producer` | object | What wrote the file. See [Run result](Run_Result.md#the-producer-record). |
| `outcome` | `compiled` or `refused` | Whether the compile succeeded. |
| `scenario` | object | The specification: `scenario_id`, `scenario_name`, `description`, `specification` (its file name), `specification_sha256`, `spec_version`. |
| `findings` | array | Every refusal and warning, in full: `check` (its id in [Scenario compiler checks](Scenario_Checks.md)), `outcome` (`refuse` or `warn`), `subject` (in the specification's own names) and `message`. |
| `epoch` | object | The epoch as `declared`, its `epoch_block_sha256`, a one-line `statement` and `t0_civil`. It also has the run's `end_s` and `end_civil`, then `time_zone_id`. Its `time_zone_id_resolved` is always false. |
| `zone` | object | The declared offset against the zone the world's georeference sets: `declared_offset_hours`, `engine_time_zone_hours` (or null), `difference_hours` (or null) and `written_by_the_session`. |
| `illumination_default` | object | The specification's illumination default: `declared`, its `policy`, its `status` and `declared_elevation_kind`. |
| `capture_windows` | array | Each capture window, resolved. See below. |
| `illumination_label_association` | object | How far the illumination band (a named range of sun elevation) predicts the supervision state (check 41). See below. |
| `world` | object | The world package: `package`, `map_name`, `network_fingerprint`, `netconvert_version`, `origin` (latitude and longitude), `georeference` and `routing_sumo` (`version`, `matched_by`, `release_agreement`, `verdict`). |
| `instants` | object | Each named time: `authored`, `form`, `seconds` and `civil`. |
| `places` | object | Each place and what it became. See below. |
| `rotas` | array | Each schedule: `id`, how many `entries` it made and its `skips` (`entry`, `seconds`, `civil`, `because`). |
| `routes` | array | Each actor and flow as routed. See below. |
| `lane_closures` | array | Each lane closure: `id`, `edge`, `street`, the closed `lanes`, how many `open_lanes`, `notify` and the class it still `allow`s. Its `begin` and `end` are resolved times. |
| `vehicle_types` | object | The `catalogue` read. One line per class in `classes`. The whole `mix` and each named mix in `mixes`, with each vehicle type's probability. Each vehicle type id in `types`, with the body it binds. |
| `supervision` | object | The supervision plan's `instances`, `cohorts` and `series`, each series with its count of `slots`. See [Supervision plan](Supervision_Plan.md). |
| `dry_run` | object | What the SUMO-only run showed. See below. |
| `lock` | object | The scenario lock, as written. See [Scenario lock](Scenario_Lock.md). |

A resolved time (`depart`, `begin`, `end` and the like) is `{authored, form, seconds, civil}`:

- what the specification wrote
- which form it was written in
- simulated seconds from zero
- the civil instant, ISO 8601 with the epoch's offset

### A capture window (`capture_windows[]`)

| Field | Type | Unit | Meaning |
|---|---|---|---|
| `id` | string | | The window's id. |
| `begin` | resolved time | | When it opens. |
| `length_s` | number | s | How long it lasts. |
| `end_s` | number | s | When it closes. |
| `end_civil` | string | | When it closes, in civil time. |
| `civil_date` | string | | The civil date it opens on, `YYYY-MM-DD`. |
| `sun_open`, `sun_close` | object or null | | The sun a run will set at the window's opening and close: `seconds`, `sun_date`, `sun_clock`, `elevation_deg` (refraction-corrected), `geometric_elevation_deg`, `azimuth_deg` (clockwise from north). Null under `ignore`. |
| `sun` | string | | Present under `ignore`: no sun is set. |
| `sun_lowest` | object | | The lowest the sun reaches over the window: `elevation_deg`, `elevation_kind`, `band` and `band_source`. A fact, never a finding. |

### The illumination-label association

| Field | Type | Meaning |
|---|---|---|
| `statistic` | string | What is computed: the mutual information between band and supervision state, over the state's entropy. |
| `band_source`, `band_edges` | string, array of `{band, above_deg}` | The band table used. |
| `elevation_kind`, `presence_estimate` | string | What the elevations are. How a vehicle's presence is estimated. |
| `over_windows`, `over_span` | object | The association over the declared windows and the one over the whole span at each departure. Each has `normalized_mutual_information`, `entries`, `table`, `degenerate_bands` and `mixed_bands`, or `not_computed` and why. |
| `normalized_mutual_information` | number or null | The headline figure. If there is only one supervision state, it is null. |
| `bands` | object or null | The headline table: for each band, the count of `annotated`, `nominal` and `unlabelled` route entries and the `total`. |
| `entries` | integer or null | How many route entries the headline counts. |
| `measured_over` | `windows` or `span` | Which the headline is. If any windows are declared, it is the windows. |
| `remedies` | array of strings | Ways to change the figures. |

### A place (`places.<name>`)

| Field | Type | Meaning |
|---|---|---|
| `name` | string | The place's name. |
| `authored` | object | The place as the specification wrote it. |
| `form` | string | Which form it was written in. |
| `edges` | array of strings | The edges it resolved to. |
| `lane`, `start_pos`, `end_pos` | string, number, number, or null | The lane and position, where the place names one. |
| `street` | string | The street name of the edge. |
| `detail` | string | How it was resolved, such as the distance a point was snapped. |
| `edge_details` | array | Each edge's `edge`, `street`, `length_m` and `speed_mps`. |

### A route (`routes[]`)

| Field | Type | Unit | Meaning |
|---|---|---|---|
| `id`, `type` | string | | The actor's or flow's id and vehicle type. |
| `from`, `to`, `via` | edges | | What was asked for. |
| `route` | array of edge ids, or null | | The route `duarouter` produced. |
| `route_length_m` | number | m | The route's length. |
| `free_flow_s` | number | s | Its time at each edge's speed limit. |
| `stops` | array | | Each stop as emitted: `place`, `lane`, `start_pos`, `end_pos`, `parking` and its `duration` or `until`. |
| `phases`, `waypoints` | array, integer | | For an actor routed in phases. |
| `depart`, `origin` | resolved time, string | | For an actor: its departure time and its origin, `actor` or `rota:<id>`. |
| `begin`, `end`, `vehs_per_hour` | resolved time, resolved time, number | | For a flow. |

### `dry_run`

The lock's record (see [Scenario lock](Scenario_Lock.md)) and, beside it:

- `teleports`
- `emergency_stops`
- `emergency_braking`
- `other_vehicles_discarded`
- `other_vehicles_waiting_at_end`
- `planned` (each planned vehicle: `vehicle_id`, `refs`, `declared_depart_s`, `declared_depart_civil`, `entrance`, `inserted`, `outcome`, `depart_s`, `waited_s`)
- `collision_list` (each collision: `time_s`, `civil`, `type`, `collider`, `victim`, `lane`, `pos_m`)

When the run was skipped, it is `{"ran": false, "reason": "..."}`.

## Versions

This page describes version 1, the only version.\
No tool reads the report as input.\
In a reader of your own, read version 1 and refuse a newer version rather than read it in part.

The skill's recorded examples leave out `producer`, which names the build and the time.\
They are still version 1.

## Example

`Import/Arapahoe_I25_SupervisionCheck.resolution.json`, a few sections:

```json
{
  "resolution_version": 1,
  "producer": {"tool": "carlacontrol.ScenarioCompiler", "tool_version": "0.10.0+g8ef0978cb",
               "carlanet": "0.10.0+g7d218c48c", "server": null, "sumo": "1.27.0",
               "written_utc": "2026-10-08T02:15:44.761Z"},
  "outcome": "compiled",
  "scenario": {"scenario_id": "Arapahoe_I25_SupervisionCheck",
               "scenario_name": "Arapahoe supervision check", "description": "...",
               "specification": "Arapahoe_I25_SupervisionCheck.scenario.json",
               "specification_sha256": "8a31c197058ad87c473f7cf502df52883429f42bcbd91feab18693925c4b0a77",
               "spec_version": 1},
  "findings": [{"check": 17, "outcome": "warn", "subject": "vehicle class suv",
                "message": "draws one body, vehicle.nissan.patrol, so every vehicle of the class looks the same and its appearance can become its label"}],
  "capture_windows": [{"id": "dwell_golden",
                       "begin": {"authored": 60, "form": "seconds", "seconds": 60.0,
                                 "civil": "2026-09-29T07:27:00-06:00"},
                       "length_s": 180.0, "end_s": 240.0, "end_civil": "2026-09-29T07:30:00-06:00",
                       "civil_date": "2026-09-29",
                       "sun_open": {"seconds": 60.0, "sun_date": "2026-09-29", "sun_clock": "07:27:00",
                                    "elevation_deg": 5.694, "geometric_elevation_deg": 5.5491,
                                    "azimuth_deg": 97.8266},
                       "sun_close": {"...": "..."},
                       "sun_lowest": {"elevation_deg": 5.694, "elevation_kind": "refraction_corrected",
                                      "band": "golden",
                                      "band_source": "the sun's refraction-corrected elevation in degrees: day above 6, golden above 0, ..."}}],
  "...": "..."
}
```
