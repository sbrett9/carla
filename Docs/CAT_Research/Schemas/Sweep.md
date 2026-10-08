# Sweep

| | |
|---|---|
| File | `<sweep>.sweep.json` |
| Schema | `CarlaControl/skills/sumo-traffic-scenarios/schemas/sweep.schema.json` (`sweep.schema.json`) |
| Schema id | `urn:carla-sumo-capture:schema:sweep:1` |
| Format version | 1, in `sweep_version` |

## What it is

A sweep is many runs of one scenario with parameters varied.\
It names a base specification, the axes to vary over it and counterfactual pairs.\
The compiler compiles every member in full, each in its own folder.\
So a member that does not route fails at compile time, not partway through a set of captures.

A member's id comes from the base scenario's id and the member's axis values.\
It holds no counter and no time, so members can be joined across rebuilds.

Light is an axis of its own.\
Any axis whose path touches `epoch`, `illumination` or a capture window's `begin` changes the light.\
This is true whatever kind the axis is declared as.

`illumination` says how the sweep treats such axes (check 43).\
`hold` refuses them.\
`vary` refuses any other kind.\
`factorial` allows both and warns that the result is not a controlled comparison of either.

A counterfactual pair holds the inputs fixed except one actor.\
The mode says how the twin (the member that changes the actor) differs:

- `absent` removes the actor.
- `nominal` keeps its type, route and timing, removes what you name (`stops` or `via`) and labels it nominal.
- `displaced` moves it by a time `shift` and/or by place substitutions (`places`).

Car-following models react to what is in front of them, so a pair is identical inputs except one vehicle, never identical trajectories.

## Who writes it and who reads it

- A scenario developer writes it, beside the base specification.
- `carla-compile-scenario --sweep FILE --out-dir DIR` reads it, checks it against this schema (check 53), compiles each member into `DIR/<member id>/` and writes the [Sweep index](Sweep_Index.md) into `DIR`.

The schema ships with the authoring skill.\
`carla-compile-scenario --write-sweep-schema PATH` writes it.

## Fields

| Field | Type | Required | Default | Meaning |
|---|---|---|---|---|
| `sweep_version` | constant `1` | yes | | The format version of this file. |
| `sweep_id` | string | yes | | The sweep's id: a letter or digit, then letters, digits, `_`, `.` or `-`. It names the index. |
| `base` | string | yes | | The base specification, relative to this file. |
| `axes` | array | no | none | The parameters varied. |
| `axes[].path` | string | yes | | The specification field to vary, such as `seeds.sumo` or `actors.probe.stops[0].duration`. A list entry is named by its `id`, `name`, `class_id`, `series_id` or `flow`. `epoch.date` moves the epoch's date and keeps it valid. |
| `axes[].values` | array, at least one | yes | | The values the field takes. |
| `axes[].kind` | `illumination` or `behaviour` | no | | What the author says the axis varies. An axis declared `behaviour` that changes the light is refused. |
| `pairing` | `cross` or `zip` | no | `cross` | `cross` makes every combination of the axes' values. `zip` takes the n-th value of each axis together. It needs axes of equal length. |
| `illumination` | `hold`, `vary` or `factorial` | no | `hold` | How the sweep treats axes that change the light. |
| `counterfactuals` | array | no | none | Pairs made for every member. |
| `counterfactuals[].actor` | string | yes | | The actor the twin changes. |
| `counterfactuals[].mode` | `absent`, `nominal` or `displaced` | yes | | How the twin differs. |
| `counterfactuals[].remove` | array of `stops`, `via` | no | | Under `nominal`: what to take away from the actor. |
| `counterfactuals[].labels` | array of strings | no | | Under `nominal`: labels for the twin's nominal instance. |
| `counterfactuals[].shift` | number or duration | no | | Under `displaced`: how far to move the actor in time. |
| `counterfactuals[].places` | object of place name to place name | no | | Under `displaced`: places to substitute in the actor's route and stops. |

## Versions

This page describes version 1, the only version.\
The compiler refuses a sweep with no `sweep_version` or any other value (check 53).

## Example

The authoring skill's `examples/counterfactual/probe_standoff_pairs.sweep.json`: three twins of the base, one per mode.

```json
{
  "sweep_version": 1,
  "sweep_id": "probe_standoff_pairs",
  "base": "street_layout_probe.scenario.json",
  "counterfactuals": [
    {"actor": "probe", "mode": "nominal", "remove": ["stops"]},
    {"actor": "probe", "mode": "absent"},
    {"actor": "probe", "mode": "displaced", "shift": "30m"}
  ]
}
```

With axes, every combination is a member:

```json
{"sweep_version": 1, "sweep_id": "probe_dwell", "base": "probe.scenario.json",
 "axes": [{"path": "actors.probe.stops[0].duration", "values": ["5m", "10m", "20m"]},
          {"path": "seeds.sumo", "values": [42, 43]}]}
```
