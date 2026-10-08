# Run configuration

| | |
|---|---|
| File | `<name>.run.json`, and a run's `run.effective.json` |
| Schema | `CarlaControl/schemas/run_configuration.schema.json` |
| Schema id | `urn:carla-sumo-capture:schema:run-configuration:1` |
| Format version | 1, in `run_configuration_version` |

## What it is

A run configuration describes one capture run: which compiled scenario to bind, which window of
simulated time to render, the cameras, and how the run is paced, lit and recorded. You pass it to
`carla-capture --run`. Any field can also be set on the command line with `--set <path>=<value>`, and
a few have short options such as `--window` and `--scenario`.

A run is resolved from six layers, lowest first: the tool defaults, the site profile, the world
package, the scenario package, the run configuration, and command-line overrides. A field the run
configuration leaves out takes its value from a lower layer. Fields marked "world" or "scenario" in
the table below are bound by those packages. You may restate a bound field only with the value the
package gives it; any other value is refused (run check 3).

Every run also writes `run.effective.json` beside its result. It is a run configuration too: every
field the run resolved, the scenario named by its id, and the machine facts (server, paths, SUMO)
left out. Passing it back to `carla-capture --run` on any machine reproduces the run.

Positions are in CARLA's frame: meters, x east, y south. North is -y.

## Who writes it and who reads it

- **You write it**, by hand or from a script. A scenario developer usually writes one per scenario
  window; a camera or mission developer adds or changes the channels.
- **`carla-capture` writes `run.effective.json`** beside the run result when the offline checks
  accept the launch. It adds a `producer` record that says what wrote it.
- **`carla-capture --run` reads it** before anything else. An unknown key is refused with the nearest
  field names (run check 1). A camera blueprint attribute such as `iso` or `shutter_speed` is refused
  with the name of the channel field that sets it. A `world_build` block is refused, because a
  capture run never builds a world (run check 38).

`carla-capture --help` prints every field with its default. `carla-capture --write-schema PATH` or
`carla-capture --write-schemas DIR` writes the schema.

## Fields

The document is a JSON object. Dotted paths below are nested objects: `capture.window` is
`{"capture": {"window": ...}}`. No field is required by the schema itself. A field with no default
must come from some layer, or the launch is refused (run check 2). "From" names the lower layer that
normally supplies a field: the site profile, the world package or the scenario package.

| Field | Type | Unit | Default | From | Meaning |
|---|---|---|---|---|---|
| `run_configuration_version` | constant `1` | | `1` | | The format version of the document. |
| `producer` | object or null | | | | What wrote the document. Only `run.effective.json` carries it; it is never read as a field. |
| `mode` | one of `sumo_driven_playback`, `traffic_manager_ambient`, `storyboard_execution`, `recorded_replay` | | none | scenario | Which system drives the world. A scenario package means `sumo_driven_playback`, the only mode this tool runs (run check 4). |
| `caller` | one of `attended`, `unattended` | | `"attended"` | | Whether a person watches the launch at a terminal (`attended`) or not. `unattended` needs `result_path` and an `on_warning` entry for every warning. |
| `caller_label` | string or null | | `null` | | A label the caller uses to recognize its own run. Recorded and never interpreted. |
| `scenario_package` | string | | none | | The compiled scenario: a scenario id, a scenario package folder, or its `.lock.json`. |
| `world_package` | string or null | | `null` | | The `.cwp` the world was built as. Unset, it is the package the scenario lock names, under `paths.world_package_root`. |
| `result_path` | string or null | | `null` | | Where the run result is written, outside the capture root. Unset, it is `<paths.runs_root>/<session id>/run.result.json`. |
| `write_headroom_floor_s` | number | s | `600.0` | | Captured seconds of free disk below which the run stops itself cleanly. |
| `monitor` | one of `on`, `off` | | `"on"` | | The live monitor: a panel on a terminal, a log line every few seconds otherwise. |
| `collision_detail` | one of `on`, `off` | | `"off"` | | Whether the run prints every collision as it ends. The record of collisions is complete either way. |
| `on_warning` | object of code to `proceed` or `refuse` | | `{}` | | How each warning is to be handled, decided in advance, by warning code. |
| `expect` | object | | `{}` | | Values the caller expects a field or a launch echo value to resolve to, by path. A failed expectation refuses the launch. |
| `capture.window` | string | | none | | The simulated time to render: a window the scenario declares, by id; `<begin_s>:<end_s>`; or `<begin_s>:` to run to the scenario's end. |
| `capture.prewarm_s` | number | s | `300.0` | | Simulated seconds rendered before the window and not recorded, so each camera's view is ready by the first capture. |
| `capture.world_delta_s` | number | s | `0.05` | | Simulated seconds per world tick. The SUMO step must be a whole number of ticks. |
| `capture.capture_hz` | number | Hz | `2.0` | | Captures per simulated second on every channel. One capture must be a whole number of ticks. |
| `capture.picture_settled_wait` | boolean | | `false` | | Whether the prewarm also waits for each camera's picture to settle after its tiles are in (run checks 50 and 51). |
| `capture.tiles_hold_s` | number | s | `10.0` | | How long before the window the photoreal tiles must be in. Read only when `capture.picture_settled_wait` is false. |
| `capture.picture_ceiling_frames` | integer | frames | `60` | | How many of its own frames a camera has to settle its picture. Read only when `capture.picture_settled_wait` is true. |
| `capture.picture_tolerance_levels` | number | gray levels | `0.5` | | How far a frame may differ from one ten ticks earlier and count as settled. Read only when `capture.picture_settled_wait` is true. |
| `capture.road_layer_visible` | boolean | | `false` | | Whether to draw the generated road surface. It is hidden by default because it lies over the real road in the photogrammetry. |
| `capture.signal_layer_visible` | boolean | | `false` | | Whether to draw the generated traffic light and sign meshes. SUMO runs the signals either way. |
| `capture.render_set` | one of `all`, `circle`, `cameras` | | `"all"` | | Which vehicles get a body: every vehicle SUMO has, those inside `capture.render_region`, or those in or near a camera's view. |
| `capture.render_region` | object `{x_m, y_m, radius_m}` or null | m | `null` | | The circle vehicles get a body inside, for `circle`, and for `cameras` until the cameras are placed. |
| `capture.render_hysteresis_m` | number | m | `60.0` | | How far past the limit a vehicle keeps its body before it loses it. |
| `capture.render_cap` | integer or null | | `null` | | The most vehicles that may hold a body at once. Null means no limit. |
| `capture.render_min_pixels` | number | pixels | `2.0` | | Under `cameras`: a camera's footprint ends where the longest body would cover fewer pixels than this. |
| `capture.render_admit_lead_s` | number | s | `3.0` | | Under `cameras`: how many seconds of its own travel ahead of a camera's footprint a vehicle gets its body. |
| `capture.render_release_lag_s` | number | s | `5.0` | | Under `cameras`: how long a vehicle keeps its body after it was last near a camera's footprint. |
| `capture.draw_distance_m` | number or null | m | `null` | | How far from a camera a body is drawn. Null draws every body at any range. Rendering only: every vehicle keeps its truth. |
| `capture.channels` | array of channel objects | | none | | The cameras, one object each. See the channel fields below. |
| `occlusion.margin_m` | number | m | `1.0` | | How much nearer than a vehicle's own surface something must be to block it. |
| `occlusion.samples` | integer | | `24` | | How finely each vehicle's outline is sampled for occlusion. |
| `occlusion.depth_max_range_m` | number | m | `20000.0` | | The range of the depth camera occlusion is measured with. A surface beyond it reads as sky. |
| `bridge.position_divergence_limit_m` | number | m | `0.01` | | The largest gap between a commanded pose and the pose the world applied that the closing gate accepts. |
| `bridge.velocity_divergence_limit_m_per_s` | number | m/s | `0.01` | | The largest gap between a commanded velocity and the velocity the world reported that the closing gate accepts. |
| `pacing.mode` | one of `as_available`, `wall_clock` | | `"as_available"` | | Whether the world ticks as fast as it can or is held to the wall clock. |
| `pacing.real_time_factor` | number or null | sim s per wall s | `1.0` | | Under `wall_clock`, simulated seconds per wall-clock second. |
| `pacing.min_achieved_factor` | number or null | sim s per wall s | none | | Under `wall_clock`, required: the achieved factor below which the prewarm refuses the window. |
| `pacing.window_s` | number | s | `5.0` | | Wall-clock seconds the achieved factor is measured over. |
| `solar.policy` | one of `freeze_at_window_start`, `advance`, `freeze_at`, `ignore` | | none | scenario | What the sun does across the window. The scenario's illumination default supplies it. |
| `solar.rate_sun_s_per_sim_s` | number or null | sun s per sim s | `null` | scenario | Under `advance`, always 1.0. Not settable. |
| `solar.freeze_at_civil_time` | string `HH:MM:SS` or null | | `null` | scenario | Under `freeze_at`, the civil time of day the sun is held at. |
| `solar.freeze_date_advances` | boolean or null | | `null` | scenario | Under a freeze, whether the sun's date follows the civil date when the epoch's calendar advances. |
| `solar.require_sun` | boolean or null | | `null` | scenario | Whether a world with no sun refuses the run. Unset means a sun is required. |
| `solar.note` | string or null | | `null` | scenario | Why this policy, in one sentence. |
| `sumo.allow_version_mismatch` | boolean | | `false` | | Run with a SUMO release other than the one that converted the world, instead of refusing. |
| `sumo.home` | string or null | | `null` | site | The SUMO installation to launch: the folder holding `bin/sumo`. |
| `server.host` | string | | `"127.0.0.1"` | site | The CARLA server's address. |
| `server.port` | integer | | `2000` | site | The CARLA server's RPC port. |
| `server.timeout_s` | number | s | `30.0` | site | Seconds to wait for the server to answer a request. |
| `paths.scenario_root` | string | | none | site | Where compiled scenario packages are found by id. |
| `paths.world_package_root` | string | | none | site | Where world packages are found by the name a scenario lock records. |
| `paths.catalogue` | string | | none | site | The measured vehicle catalogue. Its digest must match the scenario lock's. |
| `paths.capture_root` | string | | none | site | Where captures are written: one folder per run, and one per channel inside it. |
| `paths.runs_root` | string | | none | site | Where a run's result, resolution report and lock are written by default. |
| `world.map_name` | string | | none | world | The world's map name. |
| `world.network_fingerprint` | string, 64 hex digits | | none | world | The canonical fingerprint of the SUMO network the world carries. |
| `world.opendrive_sha256` | string | | none | world | The SHA-256 of the world's OpenDRIVE. |
| `world.origin_latitude` | number | degrees | none | world | The latitude of the map's origin. |
| `world.origin_longitude` | number | degrees | none | world | The longitude of the map's origin. |
| `world.netconvert_version` | string | | none | world | The netconvert release that converted the world. |
| `scenario.scenario_id` | string | | none | scenario | The scenario's id. |
| `scenario.lock_sha256` | string, 64 hex digits | | none | scenario | The SHA-256 of the scenario lock the run binds. |
| `scenario.epoch` | object | | none | scenario | What simulated second zero means in civil time. See [Epoch](Epoch.md). |
| `scenario.epoch_block_sha256` | string, 64 hex digits | | none | scenario | The SHA-256 of the epoch object. |
| `scenario.sumo_step_s` | number | s | none | scenario | SUMO's step. |
| `scenario.sumo_seed` | integer | | none | scenario | SUMO's seed. |
| `scenario.end_s` | number | s | none | scenario | The simulated second the scenario ends at. |
| `scenario.catalogue_digest` | string | | none | scenario | The digest of the catalogue the scenario was compiled against. |
| `scenario.accept_skipped_dry_run` | boolean | | `false` | | Run a scenario whose compile skipped its SUMO-only run, instead of refusing it (run check 54). |

Each object in `capture.channels` takes these fields:

| Field | Type | Unit | Default | Meaning |
|---|---|---|---|---|
| `sensor_id` | string or null | | `null` | The camera's name: 1 to 63 ASCII letters, digits, underscores or hyphens. It names the channel's folder and begins every still's file name. Required when there is more than one channel. |
| `pattern` | one of `stare`, `orbit` | | `"stare"` | A stare holds one pose; an orbit circles a center with the view held on it. |
| `fov` | number | degrees | `90.0` | Horizontal field of view. |
| `width` | integer | pixels | `1280` | Picture width. |
| `height` | integer | pixels | `720` | Picture height. |
| `orbit_centre_x_m` | number or null | m | `null` | Orbit: x of the center it circles. Required for an orbit. |
| `orbit_centre_y_m` | number or null | m | `null` | Orbit: y of the center (south). Required for an orbit. |
| `orbit_centre_z_m` | number | m | `0.0` | Orbit: the height the altitude is measured from. |
| `orbit_radius_m` | number | m | `200.0` | Orbit: radius. |
| `orbit_altitude_m` | number | m | `518.2` | Orbit: height above the center. |
| `orbit_period_s` | number | s | `240.0` | Orbit: simulated seconds per revolution. |
| `stare_look_at_x_m` | number or null | m | `null` | Stare: x of the point looked at. |
| `stare_look_at_y_m` | number or null | m | `null` | Stare: y of the point looked at (south). |
| `stare_look_at_z_m` | number | m | `0.0` | Stare: height of the point looked at. |
| `stare_look_at_target` | string or null | | `null` | Stare: `rendered_traffic` aims at the center of the vehicles drawn just before the window, instead of a given point. |
| `stare_altitude_m` | number | m | `304.8` | Stare: height above the point. |
| `stare_standoff_m` | number | m | `0.0` | Stare: horizontal distance back from the point. 0 looks straight down. |
| `stare_bearing_deg` | number | degrees | `0.0` | Stare: the compass direction the camera looks along, clockwise from north. |
| `stare_x_m`, `stare_y_m`, `stare_z_m` | number or null | m | `null` | Stare: the camera's position, given directly. Give all five pose fields or none. |
| `stare_pitch_deg` | number or null | degrees | `null` | Stare: pitch. Negative looks down. |
| `stare_yaw_deg` | number or null | degrees | `null` | Stare: yaw. 0 faces east, -90 faces north. |
| `post_process_profile` | one of `Default`, `GoPro`, `Town10HD_Opt`, `Town_C` | | `"Default"` | The post-process profile the camera spawns with. |
| `exposure_method` | one of `manual`, `histogram` | | `"manual"` | `manual` fixes the exposure with the three fields below; `histogram` lets the engine meter each frame, and warns. |
| `exposure_iso` | number | ISO | `100.0` | Sensitivity, at least 1. |
| `exposure_shutter_s` | number | s | `0.003125` | Shutter time, 1/8000 s to 100 s. 0.003125 is 1/320 s. |
| `exposure_fstop` | number | f-number | `4.0` | Aperture, 1 to 32. |
| `exposure_compensation_ev` | number | EV | `0.0` | Exposure compensation, -15 to +15, under either method. |

## Versions

This page describes version 1, the only version. A document without `run_configuration_version` is
read as version 1. A document that declares a newer version is refused, and the message says to use
the release that wrote it. Any other value is refused as not a field value (run check 1).

## Example

`Import/Arapahoe_I25_SupervisionCheck.run.json`: one stare camera 70 m above a point, 25 m back from
it, looking east, over the scenario's `dwell_golden` window.

```json
{
  "run_configuration_version": 1,
  "caller_label": "supervision check",
  "scenario_package": "Import/Arapahoe_I25_SupervisionCheck.lock.json",
  "capture": {
    "window": "dwell_golden",
    "prewarm_s": 30.0,
    "channels": [
      {"sensor_id": "Check_Overhead_1", "stare_altitude_m": 70.0, "stare_standoff_m": 25.0,
       "stare_bearing_deg": 90.0, "fov": 50.0, "width": 1920, "height": 1080,
       "stare_look_at_x_m": -374.2, "stare_look_at_y_m": -313.7}
    ]
  }
}
```

Run it unattended, with the result written to a chosen place:

```
carla-capture --run Import/Arapahoe_I25_SupervisionCheck.run.json --caller unattended --result out/check.result.json
```
