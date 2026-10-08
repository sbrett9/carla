# Launch echo

| | |
|---|---|
| File | none of its own: the `launch_echo` object in `run.result.json` and `run.resolution.json` |
| Schema | `CarlaControl/schemas/launch_echo.schema.json` |
| Schema id | `urn:carla-sumo-capture:schema:launch-echo:1` |
| Format version | 1, in `launch_echo_version` |

## What it is

The launch echo is what a capture run says it is about to do, before it acquires the world or starts
SUMO: the simulated and civil span of the window, how many captures it will make, the sun it will
set, the world, which vehicles get a body, the disk it will cost, where it writes, the wait for each
camera's view, and the warnings raised. It lets you see that a run is not the run you
meant before any time is spent.

Every figure comes from the code that will act on it. It also says what it cannot predict, in
`not_predicted`: for example the wall-clock duration and how many vehicles will be drawn.

## Who writes it and who reads it

- **`carla-capture` computes it** once, after the offline checks accept and before anything is
  acquired. An attended run prints it at the terminal.
- It is written into the run's resolution report and its result, as `launch_echo`. Both are null
  when the offline checks refused before the echo was computed.
- **An `expect` entry in the run configuration reads it**: `expect` can name any value here as
  `launch_echo.<path>`, such as `launch_echo.captures.total`, and the launch is refused if the value
  is not what the caller expected (run check 35).

## Fields

| Field | Type | Unit | Meaning |
|---|---|---|---|
| `launch_echo_version` | constant `1` | | The format version of this block. |
| `session_id` | string | | The run's identity. |
| `caller` | `attended` or `unattended` | | Whether a person watches the launch. |
| `scenario.scenario_id` | string | | The scenario's id. |
| `scenario.lock` | string | | The scenario as `<scenario_id>@<first 12 hex digits of its lock's SHA-256>`. |
| `scenario.window` | string | | The declared window's id, or `explicit`. |
| `scenario.dry_run.ran` | boolean | | Whether the compile ran the scenario in SUMO alone. |
| `scenario.dry_run.skipped_accepted` | boolean | | Whether a skipped SUMO-only run was accepted for this launch. |
| `scenario.dry_run.statement` | string | | What the scenario lock records of that run, in words. |
| `simulated.begin_s` | number | s | Where the window begins, in simulated seconds. |
| `simulated.end_s` | number | s | Where it ends. |
| `simulated.length_s` | number | s | Its length. |
| `simulated.end_source` | string | | Where the end came from: the scenario's window, an explicit end, or the scenario's own end. |
| `simulated.prewarm_s` | number | s | Seconds rendered before the window and not recorded. |
| `simulated.first_rendered_s` | number | s | The first simulated second rendered. |
| `captures.capture_hz` | number | Hz | Captures per simulated second on each channel. |
| `captures.channels` | integer | | How many channels. |
| `captures.per_channel` | integer | | Captures each channel will make. |
| `captures.total` | integer | | Captures over every channel. |
| `captures.frames_per_hour` | integer | | Captures per simulated hour over every channel. |
| `civil.begin`, `civil.end` | string | | The window's begin and end in civil time, ISO 8601 with offset. |
| `civil.first_rendered` | string | | The first rendered instant in civil time. |
| `civil.epoch` | string | | The scenario's epoch in one line. |
| `sun.policy` | one of `freeze_at_window_start`, `advance`, `freeze_at`, `ignore` | | What the sun does across the window. |
| `sun.binds` | boolean | | Whether the policy sets the sun. False only for `ignore`, which leaves the world's sun alone and adds a `statement`. |
| `sun.advances` | boolean | | Whether the sun moves across the window. |
| `sun.rate` | number or null | sun s per sim s | Under `advance`, the sun's rate. |
| `sun.elevation_kind` | string | | What the elevations are: `refraction_corrected`. |
| `sun.window_open_s` | number | s | The simulated second the window opens and the sun is set. |
| `sun.at_begin`, `sun.at_end` | object | | The sun at the window's first and last instant: `seconds`, `sun_date`, `sun_clock`, `elevation_deg`, `geometric_elevation_deg`, `azimuth_deg` (degrees clockwise from north) and `band`. |
| `sun.held_at` | string | | For a frozen sun, the date and clock it is held at. |
| `sun.held_at_note` | string | | When the sun is pinned, and what the prewarm is lit by. |
| `world.map_name` | string | | The world's map name. |
| `world.package` | string | | The world package's file name. |
| `world.network_fingerprint` | string | | The canonical fingerprint of the world's SUMO network. |
| `world.origin` | array of 2 numbers | degrees | Latitude and longitude of the map's origin. |
| `render.set` | `all`, `circle` or `cameras` | | Which vehicles get a body. |
| `render.region` | object or null | m | The render region, `{x_m, y_m, radius_m}`. |
| `render.hysteresis_m`, `render.min_pixels`, `render.admit_lead_s`, `render.release_lag_s`, `render.cap` | number | | The render set's settings as resolved. |
| `render.limited` | boolean | | Whether an optional limit leaves vehicles without a body. |
| `render.vehicles` | string | | Which vehicles get a body, in words. |
| `render.left_out` | string or null | | What a limit leaves out. Null with no limit. |
| `render.road_layer_visible`, `render.signal_layer_visible` | boolean | | Whether the generated road and signal meshes are drawn. |
| `render.draw_distance_m` | number or null | m | The draw distance. |
| `render.draw_distance` | string | | The draw distance in words. |
| `cost.bytes_per_captured_second` | number | bytes | Disk per captured second over every channel. |
| `cost.estimated_bytes` | number | bytes | The window's estimated size on disk. |
| `cost.free_bytes` | number | bytes | Free space where captures are written. |
| `cost.headroom_s` | number or null | s | Captured seconds that free space holds. |
| `cost.basis` | string | | What the estimate rests on. |
| `writes.capture_directory` | string | | The capture folder. |
| `writes.result_path` | string | | Where the run result will be written. |
| `pacing.mode` | `as_available` or `wall_clock` | | How ticks are paced. |
| `pacing.real_time_factor`, `pacing.min_achieved_factor` | number or null | sim s per wall s | Under `wall_clock`, the factor and its floor. |
| `readiness.waits` | constant `true` | | Every run waits for each camera's view before the window. |
| `readiness.rule` | string | | The rule the wait follows. |
| `readiness.tiles` | string | | What it means for a view's tiles to be in. |
| `readiness.picture_settled_wait` | boolean | | Whether the picture is also waited on. |
| `readiness.picture` | string | | What the picture's wait compares, or that it is not run. |
| `readiness.tiles_ceiling_s` | number | s | Wall-clock seconds a view has for its tiles. |
| `readiness.tiles_hold_s` | number | s | When the picture is not waited on: the tiles' lead before the window, in simulated seconds. |
| `readiness.vehicles`, `readiness.picture_ceiling_frames`, `readiness.picture_ceiling_s`, `readiness.picture_tolerance_levels` | | | Only when the picture-settled wait is on: how the comparison skips the parts of the picture that drawn vehicles cover, and the wait's ceiling and tolerance. |
| `readiness.from_s`, `readiness.until_s` | number | s | Where the wait begins, and the window's opening. |
| `readiness.traffic_stare_holds_from_s` | number or null | s | Where a stare aimed at the traffic stops following it. Null with no such stare. |
| `readiness.not_ready`, `readiness.per_capture` | string | | What happens to a view that is not ready, and what is not recorded per capture. |
| `warnings` | array of strings | | The codes of the warnings the offline checks raised. |
| `not_predicted` | array of strings | | What the echo cannot say before the run. |

`band` is one of `day`, `golden`, `civil_twilight`, `nautical_twilight`, `astronomical_twilight` and
`night`.

## Versions

This page describes version 1, the only version. No tool reads the echo back from a file. A reader
should read version 1 and refuse a newer version rather than read it in part.

## Example

Shortened from a real run:

```json
{
  "launch_echo_version": 1,
  "session_id": "cap-20261007-173433-41f49b",
  "caller": "unattended",
  "scenario": {"scenario_id": "Arapahoe_I25_SupervisionCheck",
               "lock": "Arapahoe_I25_SupervisionCheck@51ff04af5747", "window": "dwell_golden",
               "dry_run": {"ran": true, "skipped_accepted": false,
                           "statement": "ran with SUMO 1.27.0 over 360.0 s: 133 vehicles loaded, 133 inserted, 0 discarded, 0 waiting at the end; 3 of 3 planned vehicles inserted; 0 collisions"}},
  "simulated": {"begin_s": 60.0, "end_s": 240.0, "length_s": 180.0,
                "end_source": "declared by the scenario as window dwell_golden",
                "prewarm_s": 60.0, "first_rendered_s": 0.0},
  "captures": {"capture_hz": 2.0, "channels": 1, "per_channel": 360, "total": 360,
               "frames_per_hour": 7200},
  "civil": {"begin": "2026-09-29T07:27:00-06:00", "end": "2026-09-29T07:30:00-06:00",
            "first_rendered": "2026-09-29T07:26:00-06:00", "epoch": "t = 0 is ..."},
  "sun": {"policy": "freeze_at_window_start", "binds": true, "advances": false, "rate": null,
          "elevation_kind": "refraction_corrected", "window_open_s": 60.0,
          "at_begin": {"seconds": 60.0, "sun_date": "2026-09-29", "sun_clock": "07:27:00",
                       "elevation_deg": 5.694, "geometric_elevation_deg": 5.5491,
                       "azimuth_deg": 97.8266, "band": "golden"},
          "at_end": {"...": "..."},
          "held_at": "2026-09-29 07:27:00"},
  "warnings": [],
  "not_predicted": ["wall-clock duration: no measured tick rate exists for a configuration before it runs"]
}
```
