# Running a capture

`carla-capture` renders one window of a compiled scenario's simulated time through cameras you describe.\
SUMO moves the vehicles and CARLA draws them.

For every capture of every camera, the command writes a PNG still and a truth sidecar.\
The sidecar holds each vehicle's position, size and box in pixels.\
The command also writes a manifest of the run and a truth track of every vehicle SUMO had.

[Getting started](Getting_Started.md) covers the install and the site profile.\
A site profile is a JSON file of the facts about this machine.\
These pages use the installed command names.\
Getting started lists the script behind each one in a checkout.

## Before you start

`carla-capture` binds what already exists.\
It never compiles a scenario or builds a world.\
You need:

- **The server running, with the world loaded.**\
  The session checks that the loaded world is the world package's (run check 22).
- **A compiled scenario.**\
  `carla-compile-scenario` writes it: a folder holding `<id>.lock.json` and the SUMO files the lock lists.\
  The scenario declares the epoch (what simulated second zero is in civil time) and the sun's policy.\
  It usually declares some named windows too.
- **The world package** (`.cwp`) the scenario was compiled against, in the site profile's `paths.world_package_root`.\
  A world package is one generated world in one file.
- **A site profile.**\
  See [Getting started](Getting_Started.md#the-site-profile).

## A first capture

Write a run file.\
This one, `overhead.run.json`, puts one camera 70 m above a point on the Arapahoe world and 25 m west of it.\
The camera looks east.\
It records the scenario's `dwell_golden` window:

```json
{
  "run_configuration_version": 1,
  "caller_label": "first capture",
  "scenario_package": "Arapahoe_I25_SupervisionCheck",
  "capture": {
    "window": "dwell_golden",
    "prewarm_s": 30.0,
    "channels": [
      {
        "sensor_id": "Check_Overhead_1",
        "stare_look_at_x_m": -374.2,
        "stare_look_at_y_m": -313.7,
        "stare_altitude_m": 70.0,
        "stare_standoff_m": 25.0,
        "stare_bearing_deg": 90.0,
        "fov": 50.0,
        "width": 1920,
        "height": 1080
      }
    ]
  }
}
```

Check it without touching the server:

```sh
carla-capture --site-profile site.json --run overhead.run.json --validate-only
```

`--validate-only` resolves the run, runs the offline checks and prints the launch echo.\
It also writes the resolution report, the lock and the effective configuration.\
The launch echo says what the run will do before it starts:

```text
Arapahoe_I25_SupervisionCheck@30706c0d3453  ::  window dwell_golden        caller: attended
  simulated   60 - 240 s      (180 s, 360 captures per channel at 2 Hz x 1)
              end: declared by the scenario as window dwell_golden; prewarm 30 s from t=30
  civil       2026-09-29T07:27:00-06:00  ->  2026-09-29T07:30:00-06:00
  sun         elevation +5.69 deg -> +5.69 deg (refraction_corrected), golden -> golden    policy: freeze_at_window_start
              held at 2026-09-29 07:27:00: the session pins the sun at the window's opening, t=60, and the prewarm from t=30 is lit by it
  world       Arapahoe_I25 (Arapahoe_I25.cwp)   net ffe490b1ee67   origin 39.59431, -104.88449
  render      every vehicle SUMO has   road hidden, signals hidden
              draw distance none: every body is drawn at any range
  cost        ~1.9 GB estimated; 52.6 GB free is 1.38 h of capture
  pacing      as_available
  readiness   every view's tiles (ceiling 90 s), from t=30 to t=60; not ready by then refuses at pre-roll
              picture not waited on: capture.picture_settled_wait is false; the tiles' lead is 10 s (capture.tiles_hold_s)
  writes      <distribution>\captures\cap-20261008-070825-6cdae3
              result <distribution>\runs\cap-20261008-070825-6cdae3\run.result.json
  warnings    0

the offline checks accepted: <distribution>\runs\cap-20261008-070825-6cdae3\run.result.json
```

An accepted `--validate-only` names the result's path but writes no result.\
A refused one writes the result, with the reasons.

Then run it for real by leaving out `--validate-only`:

```sh
carla-capture --site-profile site.json --run overhead.run.json
```

Every launch gets a session id of its own.\
So the real run's folder is not the one the check printed.\
The command's last line names the result.\
The result names the capture folder.\
When the run ends, check what it wrote (see [Checking a capture](#checking-a-capture)).\
For example:

```sh
carla-validate captures/cap-20261008-041347-270d6d
carla-audit-sidecars captures/cap-20261008-041347-270d6d
```

## The run file

A run file is a JSON document, usually named `<name>.run.json`.\
The [Run configuration](../Schemas/Run_Configuration.md) page describes every field, its type, unit and default.\
`carla-capture --help` prints them all too, with their defaults.

The fields you set most often are these:

| Field | What it is |
|---|---|
| `scenario_package` | The compiled scenario: a scenario id, a package folder, or the path of its `.lock.json`. |
| `capture.window` | Which simulated time to render. See [The window](#the-window). |
| `capture.prewarm_s` | Simulated seconds rendered before the window and not recorded. See [The prewarm](#the-prewarm). |
| `capture.channels` | The cameras, one object each. See [Camera channels](#camera-channels). |
| `caller`, `result_path`, `on_warning`, `expect` | How the run is watched and judged. See [Attended and unattended runs](#attended-and-unattended-runs). |

A run is resolved from six layers, lowest first:

- the tool defaults
- the site profile
- the world package
- the scenario package
- the run file
- the command line

A higher layer wins.\
The world and scenario packages bind some fields, such as the epoch, the SUMO step, the SUMO seed and the world's fingerprint.\
A bound field can be restated only with the value the package gives.\
Any other value is refused (run check 3).

**How `scenario_package` is found.**\
A scenario id, such as `Arapahoe_I25_SupervisionCheck`, is looked up as `<id>/<id>.lock.json` under the site profile's `paths.scenario_root`.\
A path is read from the folder you run the command in, not from the run file's folder.\
So a run file that names `Import/Arapahoe_I25_SupervisionCheck.lock.json` works only from a folder that has `Import/`.\
An id avoids this.

**Mistakes the run file is checked for.**\
An unknown key is refused with the nearest field names (run check 1).\
A camera attribute name such as `iso` or `shutter_speed` is refused with the name of the channel field that sets it.\
A `world_build` block is refused, because a capture never builds a world (run check 38).

### Setting fields on the command line

Any field can be set with `--set PATH=VALUE`, given as many times as you like.\
A channel's field is set by its index:

```sh
carla-capture --site-profile site.json --run overhead.run.json \
    --set capture.prewarm_s=60 --set "capture.channels[0].fov=40"
```

A few fields have short options of their own.\
They are applied before `--set`.\
The `--set` options are applied in the order given:

| Option | Sets | Values |
|---|---|---|
| `--scenario VALUE` | `scenario_package` | a scenario id, a package folder, or a `.lock.json` |
| `--window VALUE` | `capture.window` | a declared window's id, `<begin_s>:<end_s>`, or `<begin_s>:` |
| `--caller {attended,unattended}` | `caller` | default `attended` |
| `--caller-label VALUE` | `caller_label` | any label, recorded and never read |
| `--result VALUE` | `result_path` | where the run result is written |
| `--solar {freeze_at_window_start,advance,freeze_at,ignore}` | `solar.policy` | overrides the scenario's sun policy. The override is recorded. |
| `--collision-detail {on,off}` | `collision_detail` | `on` prints every collision. The record is complete either way. |

The other options:

| Option | What it does |
|---|---|
| `--run FILE` | The run file. |
| `--site-profile FILE` | This machine's site profile. Unset, the values come from the folder layout the command runs from. |
| `--validate-only` | Stop after the offline checks: resolve, check, print the echo, write the resolution report and the lock. |
| `--write-schema PATH` | Write the run file's JSON schema. The command then exits. |
| `--write-schemas DIR` | Write the schemas of the run file, the run's records and the site profile into `DIR`. The command then exits. |
| `--write-site-profile PATH` | Write this machine's site profile as a file to edit. The command then exits. |
| `--log-level {DEBUG,INFO,WARNING,ERROR}` | How much the command prints. |

## Camera channels

Each object in `capture.channels` is one camera, a channel.\
Every channel records at the same rate, `capture.capture_hz` (2 per simulated second by default).

Each camera also carries a depth camera, attached at the same pose with the same picture size and field of view.\
The depth camera measures how much of each vehicle something hides.\
Nothing turns it off.

### Where things are

Positions are in CARLA's frame: meters, x east, y south, z up.\
North is -y.

Angles follow CARLA: yaw 0 faces east and -90 faces north.\
A negative pitch looks down.\
A bearing is a compass direction, in degrees clockwise from north.

To find a view, fly there with `carla-free-camera` (see [Cameras and missions](Cameras_And_Missions.md#flying-a-camera-to-find-a-view-carla-free-camera)).\
Its heads-up display, like `carla-sctmv`'s, shows `x`, `N`, `yaw` and `pitch`.\
It gives the height as `elev` and `AGL`, in feet.\
Convert before you copy a pose into a run file:

- `x`, `yaw` and `pitch` copy across as they are.
- y is `-N`.
- z in meters is `elev` divided by 3.28084, less the height of the world's origin.\
  `elev` is the height above the ellipsoid.\
  `world.get_cesium_origin()` gives the origin's height as its third value.

### The camera's name: `sensor_id`

`sensor_id` names the camera.\
It names the channel's folder and begins every still's file name (`<sensor_id>_<local capture time>.png` and `.xml`).\
It is also the callsign of the camera's track in the truth.\
The rules:

- 1 to 63 characters, each an ASCII letter, a digit, a hyphen or the _ character, such as `Overwatch_1` or `Southeast_1700m_orbit`.
- Not a Windows device name (`CON`, `NUL`, `COM1` and so on).
- Not a role name the server gives sensors (`front`, `back`, `left`, `right` and so on).
- Not one of the server's own forms, `Camera_<n>` and `CARLA-SENSOR-<n>`.
- Required in a run with more than one channel (run check 11).\
  The same check requires the names to be unique among the channels.\
  Names that differ only in case count as the same.
- A single channel can leave it out.\
  The server then names the camera `Camera_<n>`.

The server refuses a name that a live camera in the world already holds.\
Here too, names that differ only in case count as the same.\
If the server refuses the name, the run is refused before the window (exit status 5).

### Stare at a point

A stare holds one pose for the whole run.\
`pattern` is `stare` by default.\
The simplest stare names the point to look at.\
It stands the camera off from that point:

| Field | Default | Meaning |
|---|---|---|
| `stare_look_at_x_m`, `stare_look_at_y_m` | none | The point the camera looks at. Both are needed. |
| `stare_look_at_z_m` | `0.0` | The point's height. |
| `stare_altitude_m` | `304.8` | How far above the point the camera is. |
| `stare_standoff_m` | `0.0` | How far back from the point the camera stands, along the ground. `0` looks straight down. |
| `stare_bearing_deg` | `0.0` | The compass direction the camera looks along. The camera stands on the opposite side of the point. With no standoff, it sets which way is up in the picture. |

The first capture above looks east (`stare_bearing_deg` 90) at (-374.2, -313.7) from 25 m back and 70 m up.\
The run worked out this pose and recorded it in its result:

```json
"pose": {"x_m": -399.2, "y_m": -313.7, "z_m": 70.0, "pitch_deg": -70.3461759419467, "yaw_deg": 0.0}
```

### Stare from a pose

When you found a view by flying there, give the pose itself.\
Give all five fields, or none:

```json
{
  "sensor_id": "Flown_Pose_1",
  "stare_x_m": -399.2, "stare_y_m": -313.7, "stare_z_m": 70.0,
  "stare_pitch_deg": -70.3, "stare_yaw_deg": 0.0
}
```

`stare_pitch_deg` must lie between -90 and 90.

### Stare at the traffic

`stare_look_at_target` set to `rendered_traffic` aims the camera at the traffic instead of at a point you give.\
The point is the center of the vehicles the run drew.\
Its height comes from the vehicles too.\
So `stare_look_at_z_m` is not used.\
The camera stands off from that point by `stare_altitude_m`, `stare_standoff_m` and `stare_bearing_deg`, as for a point.

```json
{
  "sensor_id": "Traffic_Stare_1",
  "stare_look_at_target": "rendered_traffic",
  "stare_altitude_m": 150.0,
  "stare_standoff_m": 100.0,
  "stare_bearing_deg": 0.0,
  "fov": 60.0
}
```

How it moves:

- The camera starts over the center of the world's staging bounds.
- Through the prewarm it follows the traffic: after each SUMO step it moves to the center of the vehicles that step drew.
- At `capture.tiles_hold_s` before the window (10 s by default, rounded up to whole SUMO steps) it stops following.\
  It holds that pose through the rest of the prewarm and the whole window.
- If the step before the hold drew no vehicle, there is nothing to aim at.\
  The run is then refused before the window (exit status 5).

It needs a prewarm of at least one SUMO step to measure the traffic on (run check 47).\
It also needs one SUMO step of prewarm more than a fixed camera needs for its tiles (run check 51).\
The run result records the point it aimed at under `produced.cameras[].look_at`.\
The result also gives the same point as stare fields under `as_look_at_point`.\
Paste those into a later run file to repeat the view.

### An orbit flown by the server

An orbit circles a center at a fixed height.\
The camera looks at the center the whole way.\
The server flies it: the run gives the circle to the server once.\
The server then moves the camera on every tick.\
Nothing in the client sends a pose per frame.

| Field | Default | Meaning |
|---|---|---|
| `pattern` | `"stare"` | Set it to `"orbit"`. |
| `orbit_centre_x_m`, `orbit_centre_y_m` | none | The center it circles and looks at. Both are required. |
| `orbit_centre_z_m` | `0.0` | The height the altitude is measured from. |
| `orbit_radius_m` | `200.0` | The circle's radius. |
| `orbit_altitude_m` | `518.2` | The camera's height above `orbit_centre_z_m`. |
| `orbit_period_s` | `240.0` | Seconds per lap, on the simulation clock. |

How it moves:

- The camera starts due east of the center.\
  It goes from east through south, west and north, which is clockwise seen from above.
- It is held at that opening pose through the prewarm.\
  It starts moving as the window opens.
- It advances by each tick's simulated time.\
  So `orbit_period_s` counts simulated seconds.\
  At the default 2 captures per second, a 240 s lap is 480 captures, 0.75 degrees apart.
- The tile wait sees only the opening view.\
  The ground the orbit sweeps after the window opens is sometimes still streaming in its first frames.
- A server too old to fly orbits refuses the orbit as the camera is placed, before the window (exit status 5).

The run result records each orbit's center, `radius_m`, `altitude_m` and `period_s` under `produced.cameras`.

This run file has two channels on the Arapahoe underpass scenario.\
One is an orbit around the dwell spot under the Yosemite Street bridge.\
The other is a stare at the same spot from 150 m south and 150 m up, with its own exposure.\
The scenario declares no windows.\
So the window is given as seconds:

```json
{
  "run_configuration_version": 1,
  "caller_label": "underpass orbit",
  "scenario_package": "Arapahoe_I25_UnderpassDwell",
  "capture": {
    "window": "600:840",
    "prewarm_s": 60.0,
    "channels": [
      {
        "sensor_id": "Underpass_Orbit_1",
        "pattern": "orbit",
        "orbit_centre_x_m": -171.6,
        "orbit_centre_y_m": -672.0,
        "orbit_radius_m": 200.0,
        "orbit_altitude_m": 518.2,
        "orbit_period_s": 240.0,
        "fov": 30.0,
        "width": 1920,
        "height": 1080
      },
      {
        "sensor_id": "Underpass_Stare_1",
        "stare_look_at_x_m": -171.6,
        "stare_look_at_y_m": -672.0,
        "stare_altitude_m": 150.0,
        "stare_standoff_m": 150.0,
        "stare_bearing_deg": 0.0,
        "fov": 40.0,
        "exposure_iso": 200.0,
        "exposure_shutter_s": 0.002,
        "exposure_fstop": 5.6
      }
    ]
  }
}
```

### Picture size and field of view

| Field | Default | Meaning |
|---|---|---|
| `width` | `1280` | Picture width, pixels. |
| `height` | `720` | Picture height, pixels. |
| `fov` | `90.0` | Horizontal field of view, degrees, above 0 and below 180. The vertical field follows from the picture's shape. |

Bigger pictures cost disk.\
The launch echo estimates the run's size from a measured 2.25 MiB per 1280 x 720 PNG, scaled by pixel count.\
It also says how many hours of capture the free space holds.

When the free space under the capture root cannot hold the window, run check 19 warns (`capture_may_outrun_disk`).\
If the free space falls below `write_headroom_floor_s` of capture during the run, the run stops itself cleanly (run check 46).\
That floor is 600 s by default.

### Exposure

Each channel states its camera's exposure in numbers.\
Every one of them is sent to the camera.\
The exposure is set over the channel's post-process profile.\
The profile sets the rest of the picture.

| Field | Default | Meaning |
|---|---|---|
| `post_process_profile` | `"Default"` | The profile the camera starts from: `Default`, `GoPro`, `Town10HD_Opt` or `Town_C`, spelled with that case. It sets the tone curve, bloom, lens flare, vignette and motion blur. |
| `exposure_method` | `"manual"` | `manual` fixes the exposure with the ISO, shutter and aperture below. `histogram` lets the engine meter each frame, which is auto-exposure. |
| `exposure_iso` | `100.0` | Sensitivity, ISO, at least 1. Under `manual`, doubling it brightens the picture by one stop. |
| `exposure_shutter_s` | `0.003125` | Shutter time in seconds, from 1/8000 s to 100 s. `0.003125` is 1/320 s. Under `manual`, doubling it brightens the picture by one stop. |
| `exposure_fstop` | `4.0` | Aperture as an f-number, from 1 to 32. Under `manual`, each doubling darkens the picture by two stops. It also sets the depth of field. |
| `exposure_compensation_ev` | `0.0` | Compensation in EV, from -15 to +15, added under either method. +1 doubles the brightness. |

**The `Default` profile's exposure.**\
The defaults are the `Default` profile's own: manual, ISO 100, 1/320 s, f/4 and no compensation.\
That is an EV100 of 12.32.\
A channel that sets none of these renders as the `Default` profile does.

**Values the camera cannot take are refused.**\
Run check 16 refuses:

- an ISO below 1
- a shutter outside 1/8000 s to 100 s
- an f-stop outside 1 to 32
- a compensation beyond 15 EV

A common mistake is the shutter as a rate.\
The camera's own `shutter_speed` attribute is per second, but the run file's field is in seconds:

```text
check 16 REFUSE capture.channels[0].exposure_shutter_s: 320 is not a value the camera takes as stated; it takes a shutter from 1/8000 s (0.000125) to 100 s, in seconds: 1/320 s is 0.003125, where the camera's shutter_speed of 320 is per second, the range UE's own camera settings accept
```

**Why auto-exposure warns.**\
`histogram` is allowed, but it raises warning `exposure_follows_the_scene` (run check 16).\
Under `histogram` the engine meters each frame and sets its own exposure.\
The exposure then follows what is in the picture.\
A bright vehicle entering the frame darkens the rest.\
Two windows under different suns can come out alike.\
That suits an operator's live picture, not captures you mean to compare.\
Each capture then records the method and no EV100.

**Where the exposure is recorded.**\
Every still's sidecar carries the exposure its camera was given, in a `<_carla_exposure>` element:

- the profile
- the method
- ISO
- shutter in seconds
- f-stop
- compensation
- under `manual`, the EV100

The run result records the exposure per camera under `produced.cameras[].exposure`.\
`carla-audit-sidecars` checks that every still of a camera carries the same one.

## Timing

### The clock

The world ticks `capture.world_delta_s` of simulated time at a time, 0.05 s by default.\
The scenario fixes SUMO's step.\
That step and the time between captures must each be a whole number of ticks (run check 9).\
At the defaults, a capture is taken every 10 ticks, twice per simulated second.

### The window

`capture.window` says which simulated time to record.\
It takes three forms:

| Form | Example | Meaning |
|---|---|---|
| A declared window's id | `dwell_golden` | A window the scenario declares, by name. |
| `<begin_s>:<end_s>` | `600:840` | From one simulated second to another, inside the scenario's span. |
| `<begin_s>:` | `600:` | From a second to the scenario's end, unless you stop it first. |

If you leave the window out, the refusal names the scenario's declared windows: `'capture.window' has no value and no default. Supply it with --window, or name one of the scenario's declared windows: dwell_golden, stop_day`.\
The launch echo shows the window's civil times.

### The prewarm

Before the window, the run does two things that record nothing:

1. **It fast-forwards SUMO** to the start of the prewarm.\
   Nothing is drawn.
2. **It renders the prewarm**, `capture.prewarm_s` simulated seconds (300 by default), with the cameras in place.\
   This is where every camera's view gets ready.

Under the sun policy `freeze_at_window_start`, which the example scenarios use, the sun is pinned at the window's opening and lights the prewarm too.

A prewarm longer than the time before the window is cut to fit, with warning `prewarm_clipped` (run check 8).\
For example, the default 300 s against a window that opens at t=60 gives: `prewarm 300 s clipped to 60 s: the window begins at t=60`.

### The tile wait

The world's imagery is photoreal tiles streamed from Cesium.\
A camera pointed at new ground draws it while the tiles still arrive.\
So the run waits for them.\
After every SUMO step of the prewarm, it asks the server whether each camera's tiles are in.\
"In" means every visible tileset is fully loaded with no failed tile in view.

- The wait starts once every camera holds the pose the window opens on.
- It has a ceiling of 90 s of wall-clock time.
- A view that is not ready as the window opens refuses the run before the window (run check 50, exit status 5).\
  The window is never moved.

The run result records the time each channel's tiles came in, under `produced.readiness.channels`.

### The hold before the window

`capture.tiles_hold_s`, 10 s by default, is how long before the window the views must stand still with their tiles asked about.\
It is rounded up to whole SUMO steps.

- Every camera's prewarm must be at least this long (run check 51).
- A stare at the traffic stops following the traffic this long before the window.\
  It then holds its pose.\
  Its prewarm must be one SUMO step longer.

Measured cold tile loads took 1.5 to 6.2 s of simulated time; one took 35.6 s.\
If your views load slowly, lengthen the prewarm.\
The prewarm is the time the tiles have.\
For a stare at the traffic, lengthen `capture.tiles_hold_s` as well.\
Its view stands still only that long before the window.

### The picture-settled wait, off by default

`capture.picture_settled_wait` adds a second wait: after a camera's tiles are in, the run compares its frames until the picture stops changing.\
It is `false` by default.\
Leave it that way.\
It proved too strict.\
With many ticks to a SUMO step, such as 20 ticks at a 1 s step, every comparison comes back unknown.\
The run is then refused.

When it is `true`, two more fields are read:

- `capture.picture_ceiling_frames` (60 by default, 30 s at 2 per second) is how many of its own frames a camera has to settle.
- `capture.picture_tolerance_levels` (0.5 gray levels by default) is the largest difference from the frame ten ticks earlier that still counts as settled.\
  The difference is measured in the frame's worst 80-pixel block that no vehicle covers.

The prewarm must then hold the ceiling's frames (run check 51).\
In that case `capture.tiles_hold_s` is not read.

### Pacing

By default (`pacing.mode` `as_available`) the world ticks as fast as the machine allows.

To hold the ticks to the wall clock, set these fields:

- `pacing.mode` to `wall_clock`
- `pacing.real_time_factor` to the simulated seconds per wall-clock second
- `pacing.min_achieved_factor` to the pace below which the run fails

`pacing.min_achieved_factor` is required under `wall_clock` (run check 2).\
A prewarm that does not hold it refuses the window (run check 44).

## Attended and unattended runs

### Attended

`caller` is `attended` by default: a person watches the launch at a terminal.

- The run prints the launch echo.
- It stops for the person only on a raised warning that no `on_warning` entry covers.\
  It then asks: `Proceed past 1 warning(s) (prewarm_clipped)? Type 'proceed' to continue:`.\
  Any other answer refuses the run (run check 34).\
  Having no terminal to ask also refuses it.

### Unattended

`caller` set to `unattended` is for a script or a scheduler, with no person at the terminal.

- `result_path` is required (run check 2).
- The run never stops to ask.\
  Every warning raised must already have an `on_warning` entry, either `proceed` or `refuse` (run check 34).
- A value must not come from an environment variable the site profile does not list (run check 36).\
  The same check bars searching the machine for SUMO.\
  Name `sumo.home` in the site profile.
- A loud condition stops the run: a channel whose recorder dropped stills (`loud:recorder_dropped`), or, under `wall_clock` pacing, a pace below the floor (`loud:pace_below_floor`).\
  An attended run shows the condition and goes on.
- The launch echo is not printed.

The warnings a run can raise, by the code `on_warning` uses:

| Code | Run check | Raised when |
|---|---|---|
| `prewarm_clipped` | 8 | The prewarm is cut to fit before the window. |
| `lighting_honours_no_epoch` | 15 | The sun's policy is `ignore`, so the lighting is not the scenario's time. |
| `exposure_follows_the_scene` | 16 | A channel uses `histogram` exposure. |
| `capture_may_outrun_disk` | 19 | It is possible that the free space does not hold the window. |

`expect` states what you believe a field or a launch echo value resolves to.\
A value is compared for equality, or bounded with `{"at_most": x}` or `{"at_least": x}`.\
A failed expectation refuses the launch (run check 35).\
A launch echo value is named by its path, such as `launch_echo.captures.total`.

This unattended run file allows the auto-exposure warning in advance.\
It also refuses to start a run of more than 400 stills:

```json
{
  "run_configuration_version": 1,
  "caller": "unattended",
  "caller_label": "nightly histogram check",
  "scenario_package": "Arapahoe_I25_SupervisionCheck",
  "result_path": "runs/nightly.result.json",
  "on_warning": {
    "exposure_follows_the_scene": "proceed"
  },
  "expect": {
    "launch_echo.captures.total": {"at_most": 400}
  },
  "capture": {
    "window": "60:120",
    "prewarm_s": 30.0,
    "channels": [
      {
        "sensor_id": "Operator_View_1",
        "stare_look_at_x_m": -374.2,
        "stare_look_at_y_m": -313.7,
        "stare_altitude_m": 300.0,
        "stare_standoff_m": 300.0,
        "stare_bearing_deg": 45.0,
        "exposure_method": "histogram"
      }
    ]
  }
}
```

Without the `on_warning` entry it is refused: `warning 'exposure_follows_the_scene' was raised and the caller is unattended. Set on_warning.exposure_follows_the_scene to 'proceed' ...`.\
The run's lock records who allowed each warning: the run file, or "the operator at the terminal".

### While it runs

The monitor shows how the run is going:

- the simulated time
- how far through the window the run is
- the civil time
- the sun's elevation
- the pace
- the population
- each channel's stills written and dropped

On a terminal the monitor is a panel.\
Otherwise it is a log line every few seconds, like this one:

```text
t=103.8 (24.4%) civil 2026-09-29T07:27:43.8-06:00 sun +5.69 deg; pace 3.456; population 43, all rendered; Check_Overhead_1 87 written 0 dropped
```

`monitor` set to `off` turns it off.

### Stopping a run

To stop a run cleanly, press Ctrl+C, or send `SIGINT` or `SIGTERM` on Linux.\
On Windows, a program that starts `carla-capture` stops it with `CTRL_BREAK_EVENT`.\
`taskkill` without `/F` sends nothing the run can catch.

The run then:

1. finishes the SUMO step in progress
2. drains every channel's recorder to write every still it took
3. writes the run result
4. gives the world back: it destroys its cameras and returns the bodies, the drive lease, the layers, the sun and the clock

It exits with status 6, `run_stopped`.\
The result's `closed_by` names the signal, such as `signal:SIGINT`.\
A second signal abandons the rest of the shutdown and exits at once.\
It leaves the files as they are.

## Results

### The run result

Every run writes a run result, `run.result.json`, in every outcome the tool survives.\
That includes a refusal before any server was contacted.\
The one exception is a `--validate-only` launch that the checks accept.\
That launch writes the other records and no result.

The [Run result](../Schemas/Run_Result.md) page describes every field.\
The command's exit status is read from the result's `outcome`.\
So the two always agree:

| Exit status | `outcome` | Meaning |
|---|---|---|
| 0 | `run_finished` | The window's end, or the scenario's, was reached. |
| 1 | `usage_error` | Resolving the invocation failed: an unknown key, a package that cannot be found or read, an override of a bound field. |
| 2 | `refused_offline` | The offline checks refused. No server was contacted. |
| 3 | `refused_server` | The server checks refused, or the session refused while starting. |
| 4 | `refused_authority` | Another drive holds the world: its drive lease on the server, or the population lease in this process. `authority_holder` names the holder. |
| 5 | `refused_preroll` | Refused after the lease was taken and before the window: the fast-forward, the sun, a prewarm tick, the cameras, the pace, or the traffic a stare aims at. |
| 6 | `run_stopped` | Stopped before its end: a signal, a loud condition, or the disk running out of room. |
| 7 | `internal_error` | An unexpected fault. |

`closed_by` says what closed the run: `window_end`, `scenario_end`, `aborted_at_preroll`, `operator_stop`, `write_headroom`, `signal:<name>`, `loud:<condition>` or `fault:<type>`.

When a run is refused, the command prints only its outcome and the result's path, attended or not:

```text
run result: runs\overhead.result.json (usage_error, exit status 1)
```

Read why in the result's `detail` and `refusals`.

The parts you read most:

| Part | What it says |
|---|---|
| `outcome`, `exit_status`, `closed_by`, `detail` | How the run ended. The last field gives the first refusal or fault in one line. |
| `refusals`, `warnings` | Every finding, with its run check number, subject and message. |
| `produced.capture_directory` | The capture folder. |
| `produced.channels` | Per channel: stills `written`, `recorder_dropped` and counts of stills that lacked part of their truth. |
| `produced.cameras` | Each camera's view and the exposure it was given. |
| `produced.readiness` | How each view became ready before the window. |
| `produced.gates` | The closing checks, each with what was observed, its threshold and whether it was `met`. |
| `producer` | What wrote the result: the tool's release, `carlanet`'s, the server's build and SUMO's release. |

A finished run's result holds, among the rest:

```json
{
  "result_version": 1,
  "outcome": "run_finished",
  "exit_status": 0,
  "closed_by": "window_end",
  "detail": null,
  "caller": "unattended",
  "caller_label": "supervision check",
  "session_id": "cap-20261008-041347-270d6d"
}
```

The result is written before the world is given back.\
So in `produced.termination`, the steps after "write the run result" read `"ran": false` even in a run that ended well.

### What is written where

The capture goes under the site profile's `paths.capture_root`, one folder per run, named by the run's session id:

```text
<paths.capture_root>/cap-20261008-041347-270d6d/
    Check_Overhead_1/                                  one folder per channel, named by sensor_id
        Check_Overhead_1_2026.10.07_21.14.04.926.png   the still
        Check_Overhead_1_2026.10.07_21.14.04.926.xml   its truth sidecar
        ...
    truth/
        manifest.jsonl                                 the run manifest
        world_truth_track.csv                          every vehicle SUMO had, drawn or not
        world_truth_track.summary.json                 what the track holds, and why the run ended
```

The time in a still's name is the wall-clock time on the capturing machine, in its own time zone, to the millisecond.\
It is not the scenario's simulated or civil time.\
Those are inside the sidecar.

The run's records go beside the result, outside the capture folder, named after the result's file:

| File | What it is |
|---|---|
| `<stem>.result.json` | The run result. |
| `<stem>.resolution.json` | What the launch resolved and what its checks found. See [Run resolution report](../Schemas/Run_Resolution_Report.md). |
| `<stem>.lock.json` | What the run is bound to: scenario, world, catalog, epoch, the effective configuration and the site profile's values. See [Run lock](../Schemas/Run_Lock.md). |
| `<stem>.effective.json` | Every field the run resolved, as a run file. |

With `--result out/check.result.json` the stem is `check`.\
Without `--result`, the result is `<paths.runs_root>/<session id>/run.result.json`.\
The stem is then `run`.

### Repeating a run

The effective configuration is itself a run file.\
Pass it back to repeat the run:

```sh
carla-capture --site-profile site.json --run runs/cap-20261008-041347-270d6d/run.effective.json
```

It names the scenario by its id.\
So the scenario must be under `paths.scenario_root` as `<id>/<id>.lock.json`.\
If the compiled scenario is somewhere else, add `--scenario` with the path of its `.lock.json`.\
The file leaves out this machine's facts.\
So the same file runs on another machine with that machine's site profile.

## Checking a capture

### `carla-validate`

`carla-validate` checks every file in a folder against its published schema.\
Give it a capture folder:

```sh
carla-validate captures/cap-20261008-041347-270d6d
```

```text
truth sidecars                  360 checked, 0 failed
PNG text chunks                 360 checked, 0 failed
run manifests                     1 checked, 0 failed
world truth tracks                1 checked, 0 failed
world truth track summaries       1 checked, 0 failed
every file keeps its schema
```

To check the run's records too, give it the folder that holds them.\
It then checks the result, the resolution report, the lock and the effective configuration.\
It checks the files, not the pixels.\
A manifest with no closing row is noted as an interrupted run.\
That is not a failure.

| Exit status | Meaning |
|---|---|
| 0 | Every file keeps its schema. |
| 1 | At least one file does not. |
| 2 | The path holds no file any published schema describes. |

| Option | What it does |
|---|---|
| `path` | A capture folder, one camera's folder in it, or a folder of run records or inputs. It also takes a world package (`.cwp`), a vehicle catalog folder or its `vehicles.catalogue.json`, or any folder holding them. |
| `--show SHOW` | How many failures to list, 50 by default. Every one is counted. |
| `--schemas SCHEMAS` | The folder of schemas to check against. By default, the ones installed with `carlacontrol`, or a checkout's `CarlaControl/schemas`. |
| `--write-schemas DIR` | Write every schema this release makes into `DIR`. Nothing is checked. |

### `carla-audit-sidecars`

`carla-audit-sidecars` reads every truth sidecar in a capture, or in one channel's folder.\
It holds the vehicle records to the rules a SUMO-driven capture must keep.\
In each of these cases, it fails with exit status 1:

- a vehicle record stands below the ground band the moving vehicles draw, which means a parked pool body was listed as a vehicle
- a record carries no SUMO vehicle id, one id names two SUMO vehicles, or one SUMO vehicle appears under two ids
- a record does not say whether the vehicle is in the picture (`wholly`, `partly`, `none` or `behind_camera`)
- a vehicle in the picture lacks its box, its lights or its pose source
- the occlusion fields and the reason they are missing disagree
- a camera's stills disagree about its exposure, or one lacks it
- under a supervision plan, a SUMO vehicle record lacks its supervision state

```sh
carla-audit-sidecars captures/cap-20261008-041347-270d6d
```

```text
360 truth sidecars, 17992 vehicle records
  made by: carlacontrol.CaptureSession 0.10.0+g90c0f69d2, carlanet 0.10.0+g90c0f69d2, server 0.10.0, SUMO 1.27.0: 360; with no record of it: 0
  listing their frame's rendered set: 360; listing none because the frame's set was no longer held: 0
  ground band: hae 1669.37 to 1808.01 m
  below the ground band: 0 of 17992 (0.0%), 0 of them standing still
  without a SUMO id: 0 of 17992 (0.0%)
  in the picture: wholly 400, partly 72, none 17520; not said: 0 of 17992
  occlusion measured: 471 of 17992; unmeasured by reason: no_sample 1, outside_frame 17520; neither measured nor a reason given: 0
  boxes: whole on 472 of 472 records in the picture; on records outside it: 0
  lights: on 472 of 472 records in the picture; sidecars saying their lights were unknown: 0; missing elsewhere: 0
  pose source in the picture: sumo 472; sidecars saying it was unknown: 0; SUMO records missing it elsewhere: 0
  exposure: carried by 360 sidecar(s) of 1 of 1 camera(s); missing on a camera that has it: 0
    CARLA-SENSOR-104: Default, manual, ISO 100, 0.003125 s, f/4, 0 EV, EV100 12.322
  supervision: 360 sidecar(s) under plan Arapahoe_I25_SupervisionCheck, 0 with it unknown; vehicle states annotated 539, unlabelled 17453; SUMO vehicle records with none: 0
  uids naming more than one SUMO vehicle: 0
  SUMO vehicles under more than one uid: 0
  bodies that rendered more than one SUMO vehicle (a pool reusing its bodies, expected): 19
  uids on the road, parked and on the road again (a body lent again under one uid): 0
every vehicle record stands on the ground, says whether it is in the picture, carries its box, lights and pose source where it is, every capture of a camera with an exposure carries it and names one SUMO vehicle under one uid, and carries its supervision
```

The exposure line names the camera by its track's id, `CARLA-SENSOR-<actor id>`, not by its `sensor_id`.

| Option | What it does |
|---|---|
| `capture` | A capture folder, or one channel's folder in it. |
| `--margin MARGIN` | Meters below the lowest moving vehicle a record can stand and still count as on the ground, 50 by default. |
| `--floor-hae FLOOR_HAE` | The lowest likely bare-earth height in meters, for a capture with no moving vehicle to draw the band from. |
| `--traffic-manager` | The capture is of traffic-manager traffic, whose records carry no SUMO id. |

## What a capture folder holds

The estimated pattern of life (EPoL) pages describe a capture folder file by file: see [What a capture folder holds](../EPOL/Capture_Folder.md).\
That page also shows how to read the truth for training and testing.\
The schema pages describe each file's fields:

- [Truth sidecar](../Schemas/Truth_Sidecar.md)
- the four PNG text chunks: [`carla:capture`](../Schemas/PNG_Chunk_Capture.md), [`carla:solar`](../Schemas/PNG_Chunk_Solar.md), [`carla:illumination`](../Schemas/PNG_Chunk_Illumination.md), [`carla:sensor`](../Schemas/PNG_Chunk_Sensor.md)
- [Run manifest](../Schemas/Run_Manifest.md)
- [World truth track](../Schemas/World_Truth_Track.md) and [its summary](../Schemas/World_Truth_Track_Summary.md)

The [schema index](../Schemas/README.md#what-a-capture-writes) lists them all.
