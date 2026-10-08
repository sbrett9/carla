# Cameras and missions from your own code

In this guide, a mission is a plan for cameras: where they are, where they look, and what they record.\
[Running a capture](Running_A_Capture.md) shows how to state a mission in a run file and record it with `carla-capture`.\
This page shows the other ways to work with cameras:

- from your own Python code, with `carlanet`;
- live, beside a drive, with `carla-camera-follower`;
- by flying a camera yourself and recording from it, with `carla-drive --view free`.

It ends with the drive lease, which decides who may move vehicles while SUMO drives them.

These pages use the installed command names.\
[Getting started](Getting_Started.md#running-the-commands-from-a-checkout) lists the script each one is in a checkout.

## Where things are

Positions are in CARLA's frame: meters, x east, y **south**, z up.\
North is -y.\
Angles follow CARLA: yaw 0 faces east and -90 faces north, and a negative pitch looks down.\
A bearing is a compass direction, in degrees clockwise from north, so a bearing maps to a yaw as `yaw = bearing - 90`.

`carla-sctmv`'s heads-up display, which `carla-free-camera` shares, shows north as `N`, which is -y, and the height as `elev`, in feet above the ellipsoid.\
Convert before you copy a position from it; [Where things are](Running_A_Capture.md#where-things-are) shows how.

## The Python API: `carlanet`

`carlanet` is a Python client for the CARLA server that matches CARLA's own `carla` module.\
It runs on .NET 10 rather than on CARLA's native library, and adds the calls this fork's server has, such as named cameras, camera exposure and server-flown orbits.\
Import it in place of `carla`:

```python
import carlanet as carla

client = carla.Client("127.0.0.1", 2000)
client.set_timeout(30.0)
world = client.get_world()
```

`setup-venv` installs it into the distribution's `venv/`.\
`carlacontrol`, installed beside it, holds the helpers the commands use, and you can use them too.

### Spawning a camera and setting its exposure

This spawns an RGB camera 150 m south of the Arapahoe underpass dwell spot and 150 m up, looking north at it, with the `Default` profile's exposure stated in full:

```python
import carlanet as carla
from carlacontrol.ChannelExposure import ChannelExposure
from carlacontrol.StareAim import StareAim

client = carla.Client("127.0.0.1", 2000)
client.set_timeout(30.0)
world = client.get_world()

# The pose that looks at a point from a standoff, worked out as carla-capture works it out.
aim = StareAim.looking_at(-171.6, -672.0, 0.0, altitude_m=150.0, standoff_m=150.0,
                          bearing_deg=0.0)
transform = carla.Transform(carla.Location(x=aim.x_m, y=aim.y_m, z=aim.z_m),
                            carla.Rotation(pitch=aim.pitch_deg, yaw=aim.yaw_deg, roll=0.0))

blueprint = world.get_blueprint_library().find("sensor.camera.rgb")
blueprint.set_attribute("image_size_x", "1920")
blueprint.set_attribute("image_size_y", "1080")
blueprint.set_attribute("fov", "40")
blueprint.set_attribute("post_process_profile", "Default")
blueprint.set_attribute("exposure_mode", "manual")         # or "histogram"
blueprint.set_attribute("iso", "100.0")
blueprint.set_attribute("shutter_speed", "320.0")          # per second: 1/320 s
blueprint.set_attribute("fstop", "4.0")
blueprint.set_attribute("exposure_compensation", "0.0")    # EV

camera = world.spawn_camera(blueprint, transform, name="Underpass_Stare_1")
print(world.camera_name(camera))                # Underpass_Stare_1
print(world.camera_exposure(camera).ToJson())   # the exposure the camera was given
```

The pose works out to (-171.6, -522.0, 150.0) m, pitch -45, yaw -90.

**The exposure attributes** are CARLA's own names and units.\
Note that `shutter_speed` is per second, so `320` is 1/320 s, where a run file's `exposure_shutter_s` is in seconds.\
The camera applies them over its post-process profile.\
Give `post_process_profile` with its file's case, `Default`, `GoPro`, `Town10HD_Opt` or `Town_C`.\
[Exposure](Running_A_Capture.md#exposure) explains each value and why `histogram` is not for captures you mean to compare.

`ChannelExposure` turns a run file's exposure fields into these attributes, as `carla-capture` does:

```python
exposure = ChannelExposure(iso=200.0, shutter_s=1 / 500, fstop=5.6)
for name, value in exposure.blueprint_attributes().items():
    blueprint.set_attribute(name, value)
print(exposure.ev100)   # 12.94
```

`ChannelExposure()` with no arguments is the `Default` profile's exposure: manual, ISO 100, 1/320 s, f/4, EV100 12.32.\
`exposure.problems()` lists any value the camera cannot take.

**The camera's name.**\
`world.spawn_camera(blueprint, transform, name=...)` spawns a camera under a name.\
Every still recorded from it begins with that name, and it is the callsign of the camera's track.\
`name` follows the rules in [The camera's name](Running_A_Capture.md#the-cameras-name-sensor_id), and `carla.camera_name_problem(name)` says why a name breaks them, or returns `None`.\
The server refuses a name a live camera already holds, and `spawn_camera` then raises `ValueError` with the server's reason.\
With no name, the server names the camera `Camera_<n>`.\
Either way, `world.camera_name(camera)` reads back the name the camera holds, so every client agrees on it.

**Receiving frames.**\
`camera.listen(callback)` calls `callback` with an image for every frame the camera delivers.\
The callback runs on the stream's own thread, so keep it short.

```python
def on_frame(image):
    # image.frame, image.timestamp (simulated seconds), image.transform,
    # image.width, image.height, image.fov, and image.raw_data in BGRA order
    image.save_to_disk(f"frames/{image.frame}.png")

camera.listen(on_frame)
# ... later
camera.stop()
camera.destroy()
```

A camera renders on every tick unless you set its `sensor_tick` attribute, in simulated seconds between frames.

### An orbit the server flies

The server can fly a camera around a circle by itself.\
You send the circle once; the server moves the camera on every tick, before the frame is captured, so the picture, its header and the frame's record agree on the pose.\
Anything attached to the camera, such as a depth camera, moves with it.

```python
camera.set_orbit(carla.Location(x=-171.6, y=-672.0, z=0.0),
                 radius=200.0, altitude=518.2, period=240.0)

print(camera.get_orbit_state())   # OrbitState(angle=..., enabled=True, paused=False)
camera.set_orbit_paused(True)     # hold it where it is
camera.set_orbit_paused(False)    # and let it go on
camera.set_orbit_enabled(False)   # stop flying it; the camera stays where it is
```

`Sensor.set_orbit(centre, radius, altitude, period, *, clockwise=True, start_angle=0.0, pitch=None, enabled=True)` takes:

| Argument | Meaning |
|---|---|
| `centre` | The point circled and looked at: a `Location`, or `(x, y, z)`. |
| `radius` | Meters. |
| `altitude` | Meters above the center's height. |
| `period` | Simulated seconds per lap. |
| `clockwise` | The angle increases, from east through south, which is clockwise seen from above. `False` runs it the other way. |
| `start_angle` | Radians. 0 is due east of the center. |
| `pitch` | Degrees to hold the pitch at, instead of looking down at the center. |
| `enabled` | `True` starts at once. `False` sets the circle and leaves the camera where it is until `set_orbit_enabled(True)`. |

The angle advances by each tick's simulated time, so a lap takes `period` simulated seconds whatever the pace.\
While the orbit is enabled, the server owns the camera's pose, even when paused.\
Call `set_orbit_enabled(False)` before you move the camera yourself.\
A server built before it could fly orbits raises `carlanet.OrbitNotOnServerError`; nothing in the client moves the camera in its place.

**`OrbitSensorController`** is the `carlacontrol` class that `carla-camera-follower` and `carla-capture` use to give a camera its orbit.\
It keeps the circle, turns it on and off, and works out the current angle from the simulated clock without asking the server:

```python
from carlacontrol.OrbitSensorController import OrbitSensorController

orbit = OrbitSensorController(camera, world=world)
orbit.set_orbit_params(center_x=-171.6, center_y=-672.0, center_z=0.0,
                       radius=200.0, altitude=518.2, speed=240.0)
orbit.set_enabled(True)      # sends the circle to the server
print(orbit.current_angle()) # radians, from the parameters and world.get_sim_time()
orbit.set_paused(True)
orbit.set_paused(False)
orbit.set_enabled(False)
```

`set_orbit_params` takes `center_x`, `center_y`, `center_z`, `radius` and `altitude` in meters, `speed` in **seconds per lap** (it is a period, despite its name), `angle` in radians and `clockwise`.\
It also takes `radius_feet` and `altitude_feet`, and `center_lat` and `center_lon`, which it converts with the world's georeference when it was given `world`.\
New parameters take effect the next time the orbit is enabled.\
If the server refuses the orbit, `set_enabled(True)` logs the reason and leaves `orbit.orbit_enabled` false.\
`OrbitSensorController.orbit_transform(...)` gives the pose at any angle, by the same rule the server uses.

## Watching a drive live: `carla-camera-follower`

`carla-camera-follower` places one camera of your own in a running world and shows its picture in a window.\
Over each frame it prints the camera's name, its pattern, the frame number and the simulated time the frame carries.

It changes nothing else.\
It never advances the world's clock, never changes the world's settings, sun, weather, layers or map, never starts a traffic manager, and removes its camera when it closes.\
So you can start it before, during or after a drive.\
While a drive owns the clock, frames arrive at the drive's tick rate; when nothing does, at the server's own rate.\
If no frame arrives for 3 seconds it says so once in the log, rather than look frozen.

It records nothing.\
To record, use `carla-capture` or `carla-drive`.

Some things to know:

- Its camera is a plain RGB camera with the camera's default exposure, the `Default` profile's.\
  It renders on every tick, and has no depth camera.
- `--sensor-id` is shown over the picture only.\
  The follower does not give it to the server, which names the follower's camera `Camera_<n>`.\
  The name must still follow the camera-name rules: ASCII letters, digits, underscores and hyphens, up to 63.
- An orbit is flown by the server, as above.\
  A server that cannot fly orbits makes the follower close, saying so.
- A map load removes every actor, this camera too.\
  Start the follower again after one.
- Esc or Q closes it, as does closing the window or pressing Ctrl+C in the terminal.

Its options:

| Option | Default | Meaning |
|---|---|---|
| `--host HOST` | `127.0.0.1` | The server's address. |
| `--port PORT` | `2000` | The server's port. |
| `--timeout SECONDS` | `10` | Seconds to wait for the server to answer. |
| `--sensor-id NAME` | none | A name for the camera, shown over its picture. |
| `--pattern {stare,orbit}` | `stare` | `stare` holds one pose; `orbit` circles a center. |
| `--fov DEGREES` | `90.0` | Horizontal field of view. |
| `--width PIXELS`, `--height PIXELS` | `1280`, `720` | Picture size. |
| `--stare-look-at X Y` | none | The point a stare looks at, meters. |
| `--stare-look-at-z-m METERS` | `0.0` | The height of that point. |
| `--stare-altitude-m METERS` | `304.8` | How far above the point the camera is. |
| `--stare-standoff-m METERS` | `0.0` | How far back from the point, along the ground. `0` looks straight down. |
| `--stare-bearing-deg DEGREES` | `0.0` | The compass direction the camera looks along. |
| `--stare-pose X Y Z PITCH YAW` | none | An exact pose instead of a point: meters, then degrees. |
| `--orbit-centre X Y` | none | The point an orbit circles and looks at. Required for an orbit. |
| `--orbit-centre-z-m METERS` | `0.0` | The height the orbit's altitude is measured from. |
| `--orbit-radius-m METERS` | `200.0` | The circle's radius, in meters. `carla-sctmv`'s `--orbit-radius` is in feet. |
| `--orbit-altitude-m METERS` | `518.2` | The camera's height above the center. |
| `--orbit-period-s SECONDS` | `240.0` | Seconds per lap, on the simulation clock. |

A stare takes either a point to look at or a full pose, not both.\
These are the same channel fields a run file takes, with the same defaults.

### Worked example: the Arapahoe underpass

The `Arapahoe_I25_UnderpassDwell` scenario sends one marked vehicle north on I-25, off at Arapahoe Road, west on Arapahoe and north up South Yosemite Street, where it waits 30 minutes under the Yosemite Street bridge, among heavy freeway and arterial traffic.\
The dwell spot is at about (-171.6, -672.0) in CARLA's frame.\
This example orbits a camera over that spot while SUMO drives the traffic.\
The drive runs headless: with `--no-record` it opens no window and spawns no camera of its own.

The paths are from the repository's root, where the compiled scenario is in `Import/` and the world package in `Build/world-packages/`.\
With an installed distribution, give the paths to your own copies.

**1.\
Start the server with the Arapahoe world loaded.**\
See [Getting started](Getting_Started.md#set-up).\
The server runs without a window.

**2.\
Start the follower** in a terminal of its own, orbiting the dwell spot at the follower's defaults, a 200 m radius, 518.2 m up, 240 s per lap:

```sh
carla-camera-follower --sensor-id Underpass_Orbit_1 --pattern orbit --orbit-centre -171.6 -672.0
```

A window opens and shows the camera's picture.\
Until a drive ticks the world, frames come at the server's own rate.

**3.\
Start the drive** in another terminal.\
`--no-record` spawns no camera of the drive's own.\
`--real-time-factor 1.0` holds the ticks to the wall clock, so the traffic moves at its real pace and a lap takes 240 s on your clock too, as long as the machine keeps up.\
The drive prints the pace it held.\
`--steps 0` runs until the scenario ends.\
The drive reads the epoch and the sun's policy from the scenario's specification, so leave out `--illumination`; giving both is refused.

```sh
carla-drive \
    --scenario Import/Arapahoe_I25_UnderpassDwell.sumocfg \
    --world-package Build/world-packages/Arapahoe_I25.cwp \
    --epoch Import/Arapahoe_I25_UnderpassDwell.scenario.json \
    --no-record --real-time-factor 1.0 --steps 0
```

While the drive owns the clock, the follower's frames arrive at the drive's tick rate, and its overlay shows the scenario's simulated time.\
The marked vehicle leaves at t = 120 s.\
To start the picture nearer its dwell, add `--warm-up 600`, which fast-forwards SUMO to t = 600 s, without drawing, before the first tick.

**4.\
Stop.**\
Press Ctrl+C in the drive's terminal, and Esc in the follower's window.\
The order does not matter.\
When the drive ends, it gives the clock back, and the follower keeps showing frames at the server's own rate until you close it.

To stare at the spot instead of orbiting it, replace step 2 with:

```sh
carla-camera-follower --sensor-id Underpass_Stare_1 --stare-look-at -171.6 -672.0 \
    --stare-altitude-m 150 --stare-standoff-m 150 --stare-bearing-deg 0
```

That camera stands 150 m south of the spot and 150 m up, looking north at it.

### Flying a camera to find a view: `carla-free-camera`

`carla-free-camera` flies a camera of your own around a running world, with flight controls and a heads-up display.\
Like the follower, it changes nothing in the world and records nothing.\
Use it to find a view, then put the pose in a run file.\
It spawns two cameras, the picture and a depth camera for measuring, and removes both when it closes.

| Option | Default | Meaning |
|---|---|---|
| `--host HOST`, `--port PORT` | `127.0.0.1`, `2000` | The server. |
| `--timeout SECONDS` | `20` | How long one server call may take. |
| `--x X`, `--y Y` | `0`, `0` | Where the camera starts, CARLA meters. |
| `--z Z` | `1000` | Start altitude in **feet** above the world's origin. |
| `--fov FOV` | `90` | Horizontal field of view. |
| `--ev EV` | `0` | Exposure compensation added to the `Default` profile's exposure. |
| `--speed SPEED` | `60` | Starting move speed, m/s. |
| `--width WIDTH`, `--height HEIGHT` | `1280`, `720` | Picture size. |
| `--camera-name NAME` | none | The picture camera's name, given to the server. Without one the server names it `Camera_<n>`. |

Hold the right mouse button to fly.\
W, S, A, D, E and Q move; the mouse wheel sets the speed and Shift triples it.\
Ctrl and the left mouse button measure a point's latitude, longitude and elevation.\
B and M draw the perimeter and margin; Space returns to the start; Esc quits.

## Recording from a camera you fly: `carla-drive --view free`

`carla-drive --view free` drives the scenario from SUMO and opens a window with a camera you fly, in the same process.\
Unlike `carla-free-camera`, it can record what that camera sees.

- The camera starts over the center of the world's staging bounds, `--camera-z` meters up (300 by default), looking straight down.
- Press **F** to start a recording span, and F again to end it.\
  A span starts once the camera's photoreal tiles are in, waiting up to 90 s of wall-clock time for them.\
  It starts only from the capture window's opening, which is `--window-opens-at`, or by default the first frame drawn.\
  Pressing F while it waits cancels the span.
- Each span is written to a folder of its own under `--record-dir`, named by the camera and the span's start, `<camera name>-<UTC>`.\
  The stills inside are `<camera name>_<local capture time>.png` and `.xml`, with the same truth a fixed camera's stills carry, occlusion included.
- Esc, or closing the window, ends the drive.

```sh
carla-drive \
    --scenario Import/Arapahoe_I25_UnderpassDwell.sumocfg \
    --world-package Build/world-packages/Arapahoe_I25.cwp \
    --epoch Import/Arapahoe_I25_UnderpassDwell.scenario.json \
    --real-time-factor 1.0 --steps 0 \
    --view free --camera-name NapOfEarth_2 --record-dir captures/flown
```

The options that shape the flown camera and its recording:

| Option | Default | Meaning |
|---|---|---|
| `--view {fixed,free}` | `fixed` | `fixed` spawns one camera, aims it once and records the whole window. `free` gives you a camera to fly. |
| `--no-record` | off | Write no frames: no fixed camera is spawned, and a free view's F key records nothing. |
| `--record-dir RECORD_DIR` | `captures` under the current folder when installed, `Build/captures` from a checkout | Where captures go. |
| `--record-hz RECORD_HZ` | `2.0` | Captures per simulated second. |
| `--camera-name NAME` | none | The camera's name, given to the server. It begins every still's name and each span folder's name. Without one the server names the camera `Camera_<n>`. |
| `--camera-z CAMERA_Z` | `300` | Camera height, meters. A free view starts there. |
| `--flight-speed FLIGHT_SPEED` | `60` | The free camera's starting speed, m/s. The mouse wheel changes it. |
| `--width WIDTH`, `--height HEIGHT` | `1280`, `720` free; `1920`, `1080` fixed | Picture size. |
| `--fov FOV` | `90` free; `60` fixed | Horizontal field of view. |
| `--window-opens-at WINDOW_OPENS_AT` | the first frame drawn | The simulated second the capture window opens. A frozen sun is pinned here. |
| `--world-truth-track PATH`, `--run-manifest PATH` | not written | Write the world truth track and the run manifest too. `carla-capture` always writes both. |

`carla-drive` sets no exposure of its own: its cameras take the camera's default, the `Default` profile's exposure.\
For a stated exposure, use `carla-capture`.\
`carla-drive --help` lists every option, including the sun, the render set and SUMO's settings.\
On Linux, the window needs a display.

### Recording from your own process

`world.start_recording(camera, record_dir, ...)` in `carlanet` is the recorder the commands use.\
A recorder in a process of your own writes the supervision the server holds for each frame, the same as any other.\
But the list of which bodies each frame drew, and the declared sun, belong to the process that drives SUMO.\
Without them, a recorder lists every vehicle actor in the world, including the bodies parked out of sight between uses, which `carla-audit-sidecars` then reports as standing below the ground.\
Record SUMO-driven traffic from `carla-capture` or `carla-drive`.

## The drive lease

While a SUMO drive runs, no other traffic client may drive vehicles.\
Before SUMO starts, the drive takes the world's drive lease on the server, as `<holder> (process <pid> on <machine>)`.\
`carla-capture` and `carla-drive` both take it.\
While it is held, the server refuses these calls for every actor and every client, naming the holder:

- turning autopilot on (`set_autopilot(True)`);
- vehicle control, Ackermann control and physics control, direct or in a batch.

So a traffic manager in any process moves nothing, and a second drive is refused when it tries to take the lease.\
`carla-capture` then ends with exit status 4, `refused_authority`, and names the holder in `authority_holder`.\
`carla-drive` prints `refused: <holder> holds this world's drive lease.`

Cameras are not affected.\
Spawning, moving and orbiting cameras, and reading the world, all work as before, which is why the follower and the free camera work beside a drive.

From Python:

```python
holder = world.drive_lease_holder()   # None while nobody holds it
if holder is not None:
    print(f"{holder} is driving this world")
```

A traffic tool should ask before it spawns anything, as `PythonAPI/examples/generate_traffic_carlanet.py` does.\
The drive gives the lease back when it ends.\
A drive that dies without giving it back leaves it held until the world is reloaded, or until `world.break_drive_lease()` ends it.\
That call returns the holder whose lease it ended, and the server logs it as a warning.\
A server built before the lease answers `None` to both calls, and nothing on it stops another traffic system.
