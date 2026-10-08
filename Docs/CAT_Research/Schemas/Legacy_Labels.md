# Legacy scenario labels (`*.labels.json`)

Before scenarios were compiled, a scenario generator wrote its route file and, beside it, a labels file saying which vehicles it planted, which affiliation each vehicle type should be shown with, and which gaps it described.

Nothing in the tools writes this file anymore.\
A compiled scenario's labels are its compiled supervision plan.\
The tools still read a labels file, so that runs of the old scenarios can be repeated.

- Schema: `CarlaControl/schemas/legacy_labels.schema.json`
- Schema id: `urn:carla-sumo-capture:schema:legacy-labels:1`

## Who reads it

- `carla-cot-telemetry --labels <file>` reads all three keys:
  - `marked_ids`: the vehicles recorded as planted, in the `marked` field of its XML and CSV;
  - `affiliation_by_type`: the display affiliations, used only when the run has no display convention.\
    The `u` the generator gave every anomaly type is withheld: it marked the answer, not a display choice.\
    Those types take `--affiliation`;
  - `anomaly_notes`: the described gaps, written to a [gap file](Supervision_Gaps.md) beside the run's output.
- `carla-check-label-leaks --labels <file>` reads `marked_ids`, to group the run's records into planted and not planted.
- `carla-validate`, given a folder holding it, checks it against this schema.

## Fields

Every key may be absent.\
A reader ignores a key it does not name.

| Field | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `marked_ids` | array of strings | | no | SUMO ids of the vehicles the scenario planted. |
| `affiliation_by_type` | object | | no | A CoT affiliation letter (`p u a f n s h j k o x`) per SUMO vehicle type id. `u` marks an anomaly type. |
| `anomaly_notes` | array of objects | | no | The described gaps. Each is an object with the author's own keys; `begin_s` and `end_s`, when given, are its window in seconds of simulation time. |

## Format version

The file carries no version.\
It is version 1, like any file written before its kind carried a version.\
No newer version will be made.

## Example

```json
{
  "marked_ids": ["escort_0", "escort_1", "probe_d2", "shadow", "staybehind"],
  "affiliation_by_type": {"civ_car": "n", "civ_truck": "n", "mil_jeep": "f", "guard": "f",
                          "anomaly_probe": "u", "anomaly_shadow": "u"},
  "anomaly_notes": [
    {"kind": "guard_no_show", "tower_index": 3, "edge": "26413459", "edge_pos_m": 58.9,
     "begin_s": 370800, "end_s": 399600,
     "note": "one tower left unmanned for a shift; the detectable signal is a missing guard arrival while the other fifteen towers are relieved as usual"}
  ]
}
```
