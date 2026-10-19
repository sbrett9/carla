# Run configuration

| | |
|---|---|
| File | A run file, usually `<name>.run.json`, written by hand or by a scenario's generator beside its specification. `carla-capture --run` reads it. |
| Schema | `CarlaControl/schemas/run_configuration.schema.json` |
| Schema id | `urn:carla-sumo-capture:schema:run-configuration:1` |
| Format version | 1 |
| Version field | `run_configuration_version` |

## Description

A run file says what one capture run does: which compiled scenario, which window of simulated time, which cameras and how the sun behaves.\
It holds only what the caller chooses.\
Everything else comes from the scenario, the world package and the site profile, in that order.

## Writers and readers

A person writes it, or a scenario's generator writes one beside the specification for each window the scenario declares.\
`carla-capture` reads it, resolves it against the scenario and the site, and refuses a file it cannot read in full.\
The run lock records the file as read, with its SHA-256.

## Fields

### Top level

| Field | Type | Unit | Default | Meaning |
|---|---|---|---|---|
| `run_configuration_version` | integer | | `1` | The version of this schema the document is written against. |
| `caller_label` | string | | none | A label for the run, copied into every record it writes. |
| `scenario_package` | string | | none | The compiled scenario: its id, resolved under `paths.scenario_root`, or a path to its lock. |

### `capture`

| Field | Type | Unit | Default | Meaning |
|---|---|---|---|---|
| `window` | string | | none | The simulated time to render: a window the scenario declares, by id, or `<begin_s>:<end_s>`. |
| `prewarm_s` | number | s | `300.0` | Simulated seconds rendered before the window and not recorded. |
| `channels` | array | | none | The cameras, one object each. |

## Example

The run file shipped beside the supervision check, `Scenarios/Arapahoe/Arapahoe_I25_SupervisionCheck.run.json`:

```json
{
  "run_configuration_version": 1,
  "caller_label": "supervision check",
  "scenario_package": "Arapahoe_I25_SupervisionCheck",
  "capture": {
    "window": "dwell_golden",
    "prewarm_s": 30.0,
    "channels": [
      {
        "sensor_id": "Check_Overhead_1",
        "stare_altitude_m": 70.0,
        "stare_standoff_m": 25.0,
        "stare_bearing_deg": 90.0,
        "fov": 50.0,
        "width": 1920,
        "height": 1080,
        "stare_look_at_x_m": -374.2,
        "stare_look_at_y_m": -313.7
      }
    ]
  }
}
```

## Checks

`carla-capture --run <file> --validate-only` resolves the file and runs every offline check without touching the server.\
`carla-validate <file>` checks it against the schema alone.

## Versions

| Version | What it is |
|---|---|
| 1 | The first shape, written by carlacontrol 0.10.0. |

## Related pages

- [Running a capture](../Guides/Running_A_Capture.md) walks through writing one.
- [Run lock](Run_Lock.md) records the file a run read.
