# Supervision plan

| | |
|---|---|
| File | `<scenario_id>.supervision.json`, in the compiled scenario's folder |
| Schema | `CarlaControl/schemas/supervision_plan.schema.json` |
| Schema id | `urn:carla-sumo-capture:schema:supervision-plan:1` |
| Format version | 1, in `supervision_plan_version` |

## What it is

The supervision plan carries a scenario's labels, and it is the only place they travel.\
Every labeled assertion about authored vehicles and flows is fixed here at compile time.

A run only binds these rows to the vehicles as they appear; it never adds one.\
The route file carries no labels, and the compiler refuses one that does (check 52).

A plan has three kinds of rows:

- an **instance** is an assertion about one or more authored vehicles (actors): `annotated`, executing the labeled pattern, or `nominal`, executing no target pattern (a matched negative), with its participants, their roles and the intervals of each phase;
- a **series** is a schedule read as a recurring pattern, with one slot for each vehicle the schedule sends;
- a **cohort** is a flow's whole-life label: `annotated` or `unlabelled`, never `nominal`, and never with intervals.

Every actor no row names is listed as `unlabelled` in `entities`, and every flow no cohort names as an `unlabelled` cohort, because a missing row must never stand for an asserted negative.

The plan is bound to the compiled files by their digests, and carries no time of writing, so two compiles of one specification write byte-for-byte identical plans.

## Who writes it and who reads it

- **`carla-compile-scenario` writes it** with the scenario's other files.\
  The scenario lock digests it.
- **The co-simulation session reads it** when a compiled scenario starts, under `carla-capture` and `carla-drive`.\
  It refuses a plan whose digests do not match the files it runs.\
  During the run, it reports each interval opening and closing in the run manifest, and each still's sidecar carries the supervision of its own frame.
- The scenario resolution report repeats the plan's rows (see [Scenario resolution report](Scenario_Resolution_Report.md)).

## Fields

| Field | Type | Meaning |
|---|---|---|
| `supervision_plan_version` | constant `1` | The format version of this file. |
| `plan_id` | string | The plan's id: the scenario id. |
| `spec_version` | constant `1` | The specification format version compiled. |
| `scenario_id` | string | The scenario's id. |
| `routes_digest` | string, 64 hex digits | SHA-256 of the route file the plan was compiled with. |
| `network_digest` | string, 64 hex digits | The canonical fingerprint of the network: of the road graph, not the file's bytes. |
| `config_digest` | string, 64 hex digits | SHA-256 of the `.sumocfg`. |
| `additional_digest` | string or null | SHA-256 of the lane closures' `.add.xml`. Null where there is none. |
| `vocabulary_version` | integer | The core vocabulary's version. |
| `vocabulary_digest` | string, 64 hex digits | SHA-256 of `vocabulary`, keys sorted, two-space indent. |
| `vocabulary.core` | object | The closed core: `vocabulary_version`, `source`, and `terms`, the terms the pipeline's own code branches on, by family (`supervision_state`, `subject_kind`, `interval_onset`, `closed_by`, `illumination_band`, `cadence`, `reserved_role`, `interval_anchor`, `render_state`, `render_reason`). |
| `vocabulary.namespaces` | array | Every author namespace, declared or imported, as the specification declares one (see [Scenario specification](Scenario_Specification.md#the-vocabulary-and-the-supervision)). |
| `instances` | array | The instances. See below. |
| `series` | array | The series. See below. |
| `cohorts` | array | Every flow: `flow_id`, `supervision` (`annotated` or `unlabelled`), `labels` and `parameters`. |
| `entities` | array | Every actor: `entity_id`, its `supervision` states (for example `["nominal"]`, or `["unlabelled"]`), and `refs`, the rows that name it. |

### An instance (`instances[]`)

| Field | Type | Meaning |
|---|---|---|
| `instance_id` | string | `<scenario_id>/<authored name>`. |
| `supervision` | `annotated` or `nominal` | What the instance asserts. |
| `labels` | array of terms | The labels, each a term `namespace:name`. |
| `parameters` | object | Values for keys the labels' terms declare, each of its declared type (check 56). |
| `hard_negative_for` | array of terms, or null | On a nominal instance, the terms it is a matched negative for, copied from its labels' terms. Null means not specified, not "a negative for nothing". |
| `counterfactual` | object or null | `{kind, ref}`: the series, cohort, instance or term this one is the counterfactual of. |
| `aoi_refs` | array of strings | Areas of interest the instance is about. |
| `participants` | array | Each vehicle taking part: `entity_id`, `role` (`subject` for a one-vehicle instance) and `sumo_id`. |
| `intervals` | array | The phases. See below. |

### An interval (`instances[].intervals[]`)

| Field | Type | Unit | Meaning |
|---|---|---|---|
| `entity_id` | string | | The participant the phase is about. |
| `phase` | string | | The phase's name, as authored. |
| `anchor` | object or null | | Where the interval is anchored to the participant's own events: `start` and `end` (or null for an open end), each with its `event` (`depart`, `stop:<i>`, `stop_end:<i>` or `phase:<i>`, counted from 0) and what the session needs to recognize it: a stop's `lane` and `end_pos_m`, or a phase's `route_index` and `edge`. Null for an interval declared in civil time. |
| `declared_start_s` | number or null | s | The start as declared, in simulated seconds. Null where an anchored start declares no time, as a stop's arrival does. |
| `declared_start_civil` | string or null | | The same, in civil time. |
| `declared_end_s`, `declared_end_civil` | number or null, string or null | s | The end as declared. |
| `declared_duration_s` | number or null | s | The length as declared, such as a stop's duration. |

The actual start and end of an anchored interval are what the run observes, and the run manifest records them.

### A series (`series[]`)

| Field | Type | Meaning |
|---|---|---|
| `series_id` | string | The series' id. |
| `rota_ref` | string | The schedule it reads. |
| `cadence` | constant `"enumerated"` | Each slot is listed. |
| `member_role` | string | The role of each slot's vehicle. |
| `supervision` | `annotated`, `nominal` or `unlabelled` | What each slot asserts. |
| `labels`, `parameters`, `hard_negative_for` | as an instance's | |
| `slots` | array | One per vehicle the schedule sends: `slot_key`, `aoi_ref`, `declared_start_s`, `declared_start_civil`, `declared_end_s`, `declared_end_civil` and `entity_id`. A skipped occasion has no slot. |

## Versions

This page describes version 1, the only version.\
The co-simulation session reads only version 1 and refuses any other, including a plan with no version: a field that moved silently is worse than one that is absent.

## Example

`Import/Arapahoe_I25_SupervisionCheck.supervision.json`, shortened:

```json
{
  "supervision_plan_version": 1,
  "plan_id": "Arapahoe_I25_SupervisionCheck",
  "spec_version": 1,
  "scenario_id": "Arapahoe_I25_SupervisionCheck",
  "routes_digest": "016b810a19d4429fec8c65462b1937852e7b94379491d82ea603f1e839b21675",
  "network_digest": "ffe490b1ee677d5b48ac2350f3a579e8112f68155bbd80893eebd137c724bfdc",
  "config_digest": "79a64f258e881cdb5d81638b6c4a95b467d036551ccdefc74f823bd9b1a856ff",
  "additional_digest": null,
  "vocabulary_version": 3,
  "vocabulary_digest": "0fbcea06c4a28575486d05528b3bdf029d139a829c8ec7a3f0adc71527f9ece6",
  "vocabulary": {"core": {"vocabulary_version": 3, "...": "..."}, "namespaces": ["..."]},
  "instances": [
    {"instance_id": "Arapahoe_I25_SupervisionCheck/through_transit", "supervision": "annotated",
     "labels": ["check:through_transit"], "parameters": {}, "hard_negative_for": null,
     "counterfactual": null, "aoi_refs": [],
     "participants": [{"entity_id": "transit", "role": "subject", "sumo_id": "transit"}],
     "intervals": [{"entity_id": "transit", "phase": "transit",
                    "anchor": {"start": {"event": "depart"}, "end": null},
                    "declared_start_s": 90.0, "declared_start_civil": "2026-09-29T07:27:30-06:00",
                    "declared_end_s": null, "declared_end_civil": null,
                    "declared_duration_s": null}]}
  ],
  "series": [],
  "cohorts": [{"flow_id": "arapahoe_east_to_west", "supervision": "unlabelled", "labels": [],
               "parameters": {}}],
  "entities": [{"entity_id": "transit", "supervision": ["annotated"],
                "refs": ["Arapahoe_I25_SupervisionCheck/through_transit"]}]
}
```
