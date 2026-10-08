# CoT display convention

| | |
|---|---|
| File | `<scenario>.display.json`, beside the scenario's `.sumocfg` |
| Schema | `CarlaControl/schemas/display_convention.schema.json` |
| Schema id | `urn:carla-sumo-capture:schema:display-convention:1` |
| Format version | 1, in `convention_version` |

## What it is

A Cursor-on-Target (CoT) type carries an affiliation: `a-n-G-E-V` is neutral, `a-f-G-E-V` friendly.\
A TAK client colors each track by it.\
A display convention gives each vehicle population the affiliation letter its tracks are drawn with, so a live picture is readable at a glance: a port's civilian traffic neutral and its naval traffic friendly, for example.

It is a choice about how one run is displayed.\
It is not part of the scenario, is not covered by the scenario's digests and is not checked by the compiler.

A convention names **populations, never single vehicles**.\
In a compiled scenario a population is a vehicle class: the `carla:class_id` parameter of its vehicle types (see [SUMO files](SUMO_Files.md)).\
In a hand-written route file, whose types name no class, it is the vehicle type's id.

A population the convention does not name takes the run's default, `carla-cot-telemetry --affiliation`.

## Who writes it and who reads it

- **A scenario developer writes it**, beside the scenario, named for the stem of the scenario's `.sumocfg`: `Shahid_Bahonar_Port_PatternOfLife.display.json` for `Shahid_Bahonar_Port_PatternOfLife.sumocfg`.
- **`carla-cot-telemetry` reads it**: the file `--display-convention` names, or the one beside `--config` when there is one.\
  A file that cannot be read is refused.\
  The run records which convention it drew with.
- The reader refuses a file in any of these cases:
  - it is not a JSON object;
  - it names planted vehicles (`marked_ids`);
  - it has a key other than the three below;
  - its `convention_version` is not 1;
  - it has an affiliation letter that is not a CoT affiliation;
  - it does not conform to the schema.

## Fields

| Field | Type | Required | Meaning |
|---|---|---|---|
| `convention_version` | constant `1` | yes | The format version of this file. |
| `description` | string | no | What the convention is for, in a sentence or two. Not read. |
| `affiliation_by_type` | object of population to letter | yes | Each population's affiliation letter, the second part of a CoT type. |

The affiliation letters, as a CoT type spells them:

- `p` pending
- `u` unknown
- `a` assumed friend
- `f` friend
- `n` neutral
- `s` suspect
- `h` hostile
- `j` joker
- `k` faker
- `o` none specified
- `x` other

## Versions

This page describes version 1, the only version.\
A file with no `convention_version`, or any other value, is refused.

## Example

`Import/Shahid_Bahonar_Port_PatternOfLife.display.json`, shortened:

```json
{
  "convention_version": 1,
  "description": "How a TAK client draws the Shahid Bahonar pattern of life: civilian and port traffic neutral, the naval base's traffic friendly.",
  "affiliation_by_type": {
    "civ_car": "n",
    "civ_truck": "n",
    "port_vehicle": "n",
    "mil_jeep": "f",
    "mil_truck": "f",
    "guard": "f"
  }
}
```
