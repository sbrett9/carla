# Sweep index

| | |
|---|---|
| File | `<sweep_id>.sweep-index.json`, in the sweep's output folder |
| Schema | `CarlaControl/schemas/sweep_index.schema.json` |
| Schema id | `urn:carla-sumo-capture:schema:sweep-index:1` |
| Format version | 1, in `sweep_version` (the version of the sweep compiled) |

## What it is

The sweep index lists what a sweep compiled: every member with the axis values it took, the epoch and
illumination it has, the SHA-256 of each file its lock digests, its capture windows with their civil
dates and the sun each opens under, every counterfactual pair, and every finding. It is how you find
the member that holds a given combination of values, and how you tell two members apart.

## Who writes it and who reads it

- **`carla-compile-scenario --sweep FILE --out-dir DIR`** writes it into `DIR`, beside the members'
  folders. Each member is compiled in full into `DIR/<member id>/`, with its own specification,
  lock, resolution report and supervision plan.
- It is written whether the sweep compiled or was refused. A sweep refused before its members were
  made, for example by its own schema, has only `sweep_version`, `producer`, `sweep_id`, empty
  `members` and `pairs`, its `findings` and the `outcome`.
- No tool reads it as input. It is for a scenario developer and for the scripts that schedule
  captures of the members.

## Fields

| Field | Type | Required | Meaning |
|---|---|---|---|
| `sweep_version` | constant `1` | yes | The format version of the sweep compiled, and of this index. |
| `producer` | object | no | What wrote the file, with the SUMO release that routed the members. See [Run result](RunResult.md#the-producer-record). |
| `sweep_id` | string or null | yes | The sweep's id. Null when the sweep file gave none. |
| `members` | array | yes | Every member, the twins of counterfactual pairs included. See below. |
| `pairs` | array | yes | Every counterfactual pair. See below. |
| `findings` | array | yes | The sweep's own findings and every member's refusals: `check`, `outcome`, `subject`, `message`. |
| `base` | string | no | The base specification's file name. |
| `base_sha256` | string, 64 hex digits | no | SHA-256 of the base specification. |
| `pairing` | `cross` or `zip` | no | How the axes were combined. |
| `illumination` | `hold`, `vary` or `factorial` | no | How the sweep treats axes that change the light. |
| `axes` | array | no | Each axis: `path`, `values`, and `illumination_axis`, whether it changes the light. |
| `outcome` | `compiled` or `refused` | yes | Whether every member compiled. |

### A member (`members[]`)

| Field | Type | Required | Meaning |
|---|---|---|---|
| `member_id` | string | yes | `<base scenario id>.base` with no axes, or `<base scenario id>.m<10 hex digits>` from the member's axis values. A twin's id is its pair id. |
| `assignments` | array | yes | The axis values the member took: `{path, value}`. |
| `outcome` | `compiled` or `refused` | yes | Whether the member compiled. |
| `specification` | string | yes | The member's specification, relative to the output folder, written with the path separator of the machine that compiled it (`\` on Windows). |
| `findings` | array | yes | The member's own findings. |
| `epoch_block_sha256` | string | compiled only | SHA-256 of the member's epoch. |
| `illumination` | object | compiled only | The member's illumination default. |
| `files` | object | compiled only | Each file the member's lock digests, by role, to its SHA-256. |
| `windows` | array | compiled only | Each capture window: `id`, `civil_begin`, `civil_date`, and `sun_open`, the sun at its opening (or null). |
| `counterfactual` | object | twins only | The pair this member is the twin in. |

### A pair (`pairs[]`)

| Field | Type | Meaning |
|---|---|---|
| `counterfactual_pair_id` | string | `<base member>.cf.<actor>.<mode>`, which is also the twin's member id. |
| `base_member`, `counterfactual_member` | string | The two members. |
| `actor` | string | The actor the twin changes. |
| `mode` | `absent`, `nominal` or `displaced` | How the twin differs. |
| `displaced_in` | array of `time`, `space` | For `displaced`: how it was moved. |
| `illumination_differs` | boolean | Whether the twin is lit differently: true only when displaced in time. |
| `trajectories_expected_to_match` | constant `false` | Car-following reacts to what is in front, so later trajectories differ. |
| `statement` | string | The same, in words. |

## Versions

This page describes version 1, the only version. No tool reads an index as input. A reader should
read version 1 and refuse a newer version rather than read it in part. The skill's recorded example
leaves out `producer`; it is still version 1.

## Example

The authoring skill's `examples/counterfactual/probe_standoff_pairs.sweep-index.json`, shortened:

```json
{
  "sweep_version": 1,
  "sweep_id": "probe_standoff_pairs",
  "members": [
    {"member_id": "street_layout_probe.base", "assignments": [], "outcome": "compiled",
     "specification": "street_layout_probe.base\\street_layout_probe.base.scenario.json",
     "findings": ["..."], "epoch_block_sha256": "...", "illumination": {"...": "..."},
     "files": {"routes": "...", "config": "...", "network": "...", "supervision": "..."},
     "windows": [{"id": "morning", "civil_begin": "2026-03-21T07:00:00-06:00",
                  "civil_date": "2026-03-21", "sun_open": {"...": "..."}}]},
    {"member_id": "street_layout_probe.base.cf.probe.absent", "...": "...",
     "counterfactual": {"counterfactual_pair_id": "street_layout_probe.base.cf.probe.absent",
                        "...": "..."}}
  ],
  "pairs": [
    {"counterfactual_pair_id": "street_layout_probe.base.cf.probe.absent",
     "base_member": "street_layout_probe.base",
     "counterfactual_member": "street_layout_probe.base.cf.probe.absent",
     "actor": "probe", "mode": "absent", "displaced_in": [], "illumination_differs": false,
     "trajectories_expected_to_match": false,
     "statement": "identical inputs except one vehicle; a car-following model reacts to what is in front of it, so downstream trajectories are not expected to match"}
  ],
  "findings": ["..."],
  "base": "street_layout_probe.scenario.json",
  "base_sha256": "...",
  "pairing": "cross",
  "illumination": "hold",
  "axes": [],
  "outcome": "compiled"
}
```
