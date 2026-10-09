# Legacy supervision gaps (`<name>.supervision.json` beside a SUMO bridge run)

Some of what a scenario asserts is an absence: a guard who never arrives at a tower.\
No vehicle record can carry it, because there is no vehicle.

A legacy scenario's labels file describes such gaps in its `anomaly_notes`.\
When `carla-cot-telemetry` runs that scenario, it writes the gaps to this file, each with its time window placed on the run's own clock.\
This lets whoever holds the run's output find the hours a gap covers.

This file is for legacy scenarios only.\
A compiled scenario states its supervision in its compiled supervision plan, a different file that also ends in `.supervision.json`.\
It labels the vehicle that deviates rather than describing a gap.

- Schema: `CarlaControl/schemas/supervision_gaps.schema.json`
- Schema id: `urn:carla-sumo-capture:schema:supervision-gaps:1`

## Who writes it and who reads it

If the labels describe at least one gap, `carla-cot-telemetry --labels <file>.labels.json` writes it (`carlacontrol.SupervisionSidecar`).\
If `--supervision <file>` is given, the file goes there.\
Otherwise it goes beside the `--xml` or `--csv` output, named for its stem: `orbit_cot.xml` gets `orbit_cot.supervision.json`.\
A run with neither file prints the gaps to the log instead.

The gaps are never written into the event file or the CSV.\
This is because the answer a behavior model is asked for is a note that names the unmanned post and the time.

No tool uses the data in this file.\
It is for whoever scores a model against the run.

Given a folder holding it, `carla-validate` checks it against this schema.\
A `.supervision.json` that declares `supervision_plan_version` is a compiled [supervision plan](Supervision_Plan.md) instead.

The file is JSON with a one-space indent and no final newline.

## Fields

| Field | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `scenario` | string | | yes | The scenario: the `.sumocfg` file's name without its extension. |
| `epoch` | string | | yes | The UTC instant simulation time 0 was stamped with, ISO 8601 to the millisecond. The same as the event file's `epoch`. |
| `source_labels` | string or null | | yes | The labels file's name. |
| `supervision_gaps` | array of objects | | yes | Every gap the labels describe, in their order. |

Each gap is the labels file's note with every key the author gave it, unchanged.\
It also holds the window on the run's clock:

| Field | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `kind` | string | | no | What kind of gap it is, such as `guard_no_show`. |
| `note` | string | | no | The author's description. |
| `begin_s` | number | seconds | no | Where the window begins, in simulation time. |
| `end_s` | number | seconds | no | Where the window ends, in simulation time. |
| `begin_utc` | string | | no | `epoch + begin_s`, ISO 8601 UTC to the millisecond. Present for a note that gives `begin_s`. |
| `end_utc` | string | | no | `epoch + end_s`. Present for a note that gives `end_s`. |
| any other key | any | | no | The author's own, such as `tower_index`, `edge` or `edge_pos_m`. |

## Format version

The file carries no version.\
It is version 1, like any file written before its kind carried a version.\
It will not change again, because it serves only scenarios made before compiled scenarios.

## Example

```json
{
 "scenario": "Shahid_Bahonar_Port_PatternOfLife",
 "epoch": "2026-03-21T05:00:00.000Z",
 "source_labels": "Shahid_Bahonar_Port_PatternOfLife.labels.json",
 "supervision_gaps": [
  {
   "kind": "guard_no_show",
   "tower_index": 3,
   "edge": "26413459",
   "edge_pos_m": 58.9,
   "begin_s": 370800,
   "end_s": 399600,
   "note": "one tower left unmanned for a shift; the detectable signal is a missing guard arrival while the other fifteen towers are relieved as usual",
   "begin_utc": "2026-03-25T12:00:00.000Z",
   "end_utc": "2026-03-25T20:00:00.000Z"
  }
 ]
}
```
