# PNG text chunk `carla:illumination`

**Schema:** `CarlaControl/schemas/png_chunk_illumination.schema.json` (JSON Schema 2020-12)\
**Identifier:** `urn:carla-sumo-capture:schema:png-chunk-illumination:1`\
**Format version described:** 1

## What it is

`carla:illumination` is what the run declared the sun to be for this still's frame:

- the scenario's epoch (the civil time that simulated second zero stands for);
- the illumination policy;
- the frame's civil time and the sun declared for it;
- how far the world's sun was from that declaration.

`carla:solar` says what the sun was; this chunk says what it was meant to be, so a still's lighting can be traced to the declaration it was checked against.\
The truth sidecar's `<_illumination>` element carries the same values.

The chunk is a PNG `tEXt` chunk with the keyword `carla:illumination`, holding one line of compact JSON.

## Who writes it and when

The CarlaNet recorder writes it into a still when the run declares an illumination policy: a SUMO drive given one records a declaration for every frame it renders.\
A run with no policy, such as traffic-manager traffic in `carla-sctmv`, writes none.\
A still whose frame has no declaration has no chunk.\
Fields that need an epoch, or an audit of the frame, are left out where there is none.

## Fields

| Name | Type | Unit | Required | Meaning |
|---|---|---|---|---|
| `format_version` | integer, always 1 | | No | The chunk's format version; absent from a chunk written before chunks carried one, which is version 1. |
| `policy` | string | | Yes | The illumination policy: `freeze_at_window_start`, `advance`, `freeze_at` or `ignore`. |
| `epoch_honoured` | boolean | | Yes | Whether the frame was lit by the sun of its own declared civil time. |
| `audited` | boolean | | Yes | Whether the world's sun was compared with the declaration on this frame's tick. |
| `rate` | number | seconds per second | No | Sun-clock seconds per simulated second; under `advance` only. |
| `freeze_at_civil_time` | string | | No | The time of day the sun is held at, `hh:mm:ss`; under `freeze_at` only. |
| `epoch_digest` | string | | No | SHA-256 of the scenario's epoch, 64 lowercase hex digits. |
| `epoch_civil` | string | | No | The civil time simulated second zero stands for, with its UTC offset. |
| `utc_offset_hours` | number | hours | No | The epoch's declared UTC offset. |
| `declared_civil` | string | | No | This frame's simulated instant as a civil time, with its UTC offset. |
| `declared_utc` | string | | No | The same instant in UTC, ending in `Z`: use it to join this frame to anything outside the simulation. |
| `sun_declared` | string | | No | The date and clock the sun was declared to hold for this frame: the frame's own civil time under a policy that honors the epoch, the window's opening time under a freeze. |
| `sun_elevation_declared_deg` | number | degrees | No | The declared sun's geometric elevation. |
| `sun_corrected_elevation_declared_deg` | number | degrees | No | The declared sun's refraction-corrected elevation. |
| `declared_elevation` | string | | No | Whether the sun elevation declared for the window is the refraction-corrected or the geometric one: `refraction_corrected` or `geometric`. |
| `residual_clock_s` | number | seconds | No | The world's sun clock minus the declared one. |
| `residual_deg` | number | degrees | No | The angle between the world's sun and the declared sun. |
| `residual_corrected_deg` | number | degrees | No | The world's refraction-corrected elevation minus the declared one, where the world reports it. |

A civil time looks like `2026-09-29T07:27:00-06:00`, with a fraction of a second where there is one.

**The policies:**

- `freeze_at_window_start` holds the sun at the civil time the capture window opens;
- `advance` starts it there and carries it forward at `rate`;
- `freeze_at` holds it at a declared time of day;
- `ignore` leaves the world's sun alone.

## Format version

This page describes format version 1.\
A chunk without `format_version` was written before chunks carried one and is version 1.\
Readers read a version they know, refuse a newer one by name rather than reading it in part and read a chunk with no version as version 1.

## Example

```json
{"format_version":1,"policy":"freeze_at_window_start","epoch_honoured":true,"audited":true,"epoch_digest":"b92057175df8fcf7d3f5070d76aac03c301b672e891bd7b044b888efa8a24831","epoch_civil":"2026-09-29T07:26:00-06:00","utc_offset_hours":-6,"declared_civil":"2026-09-29T07:27:00-06:00","declared_utc":"2026-09-29T13:27:00Z","sun_declared":"2026-09-29T07:27:00-06:00","sun_elevation_declared_deg":5.5491,"sun_corrected_elevation_declared_deg":5.694,"declared_elevation":"refraction_corrected","residual_clock_s":0.001,"residual_deg":0,"residual_corrected_deg":0}
```

## Checking a file

`carla-validate <capture folder>` checks this chunk in every still of a capture.
