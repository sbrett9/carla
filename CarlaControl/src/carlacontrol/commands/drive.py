"""Drive a CARLA world's vehicles from a SUMO microsimulation and record what a camera sees.

SUMO decides where every vehicle is; CARLA renders them and the native recorder writes frames. The
session owns the advance of simulated time on both sides once it is recording -- nothing here ticks
the world while the session is advancing it, and no traffic manager runs beside it.

What a run needs on disk:

  * a CARLA server with the world already built and loaded, built with `--emit-world-package` so
    there is a `.cwp` carrying the ground surface the poses are seated on and the road network they
    are interpolated along;
  * the `.sumocfg` of a scenario whose network is **that** package's network -- a scenario compiled
    against the package (carla-compile-scenario), which writes the package's own
    network beside its configuration. The session compares the two by canonical fingerprint, the
    parsed graph rather than the bytes, and refuses any other network before SUMO is started, naming
    both;
  * the measured vehicle catalogue, and a `carla:blueprint` parameter on every vType that is meant
    to be rendered. A vType naming no measured blueprint is simulated and never rendered -- no body
    of another shape stands in for it, so a scenario whose types carry no such parameter produces an
    empty road and says so in the report's refused-type count;
  * the scenario's epoch -- what simulated second zero means in civil time at the site -- as a JSON
    file holding the `epoch` object on its own or inside a scenario.json:

        {"epoch_version": 1, "civil_datetime": "2026-03-21T00:00:00-07:00",
         "utc_offset_hours": -7, "utc_datetime": "2026-03-21T07:00:00Z",
         "calendar_advances": true, "dst_in_effect": true,
         "time_zone_id": "America/Los_Angeles"}

    The offset is the declaration, daylight saving included -- Pacific time on 21 March is -07:00
    -- and the zone name is carried for a reader and never resolved. The epoch is required for
    every drive that renders a world, under every sun policy: every still is named and stamped
    from the scenario's civil time, and the session refuses a drive that declares none.

The sun advances unless the run says otherwise. With no `--illumination` and no `illumination` object
in the `--epoch` file, the session runs under the default, `advance` at a rate of 1.0: after SUMO is
fast-forwarded and before the first tick, the sun is set to the civil instant the window opens
(`--window-opens-at`, or the first rendered frame). From there it follows simulated time. The session
writes it for every frame and compares each tick's sun with the declared one. The run manifest records
that no policy was declared and that the default was in force. `--illumination freeze_at_window_start`
sets the sun the same way, reads it back to confirm the world took it and holds it there. The sun the
world was found with is given back at the end.

Usage (from a checkout, `python CarlaNet/python/run_sumo_drive.py` is the same tool):

    carla-drive \\
        --scenario <scenario dir>/<scenario_id>.sumocfg \\
        --world-package <build dir>/Gardnerville_Centerville_Lane.cwp \\
        --catalogue CarlaControl/catalogue/vehicles.catalogue.json \\
        --epoch gardnerville.epoch.json --illumination advance --solar-rate 1.0 \\
        --record-dir Build/captures/gardnerville

`--catalogue` defaults to the checkout's catalogue, or to the one installed with carlacontrol, and
`--record-dir` to `Build/captures` in a checkout, or `captures` under the current folder when
installed.

Pass `--no-record` to drive the world without spawning a camera, which is the shortest way to see
whether the vehicles are where SUMO says they are.

By default the world ticks as fast as the machine allows, so traffic moves at whatever pace the
machine manages. To watch it at the pace of real traffic from another process that holds the view,
hold the ticks to the wall clock and run until the scenario ends:

    carla-drive --scenario ... --world-package ... --epoch ... \\
        --illumination advance --solar-rate 1.0 --no-record --real-time-factor 1.0 --steps 0

The session measures the pace it actually held and this prints it once per pacing window; nothing
stops a run that falls behind, and the final report says by how much it did. The session refuses a
world package that does not describe the world the server has loaded, so a package from another
build of the world cannot put the traffic somewhere the world is not.

Which SUMO runs is named, not left to the environment. The session launches the installation this
tool names: `--sumo-home` where given, otherwise `CARLANET_SUMO_HOME`, otherwise, run from a
checkout, this repository's pinned build (`Build/sumo-install`, then `Build/sumo-src`); only where
none of those holds a `sumo` does the session search `SUMO_HOME` and then PATH itself. Installed,
there is no repository build, so `SUMO_HOME` decides unless one of the first two is given. The
session then compares that installation's release against the converter the world package records,
by release number --
`Eclipse SUMO netconvert 1.27.0` and `1.27.0` are the same release -- and refuses a different one
before SUMO is started, naming both. `--allow-sumo-version-mismatch` runs anyway, and the report
records that it did; a package that records no converter runs, and is reported unchecked. The
installation, its release and how it stood against the world's converter are logged every run.

`--sumo-gui` launches that installation's `sumo-gui` in place of `sumo`, so SUMO's own window shows
the very simulation the session is stepping, beside the CARLA world -- one SUMO process, not a second
one. The release pin holds for it by its own release, and the log names the binary that ran. An
installation with no `sumo-gui` is refused before anything starts; CarlaSetup.ps1 or CarlaSetup.sh
builds and stages it. A CARLA distribution bundles no `sumo-gui`: point `--sumo-home` (or
`CARLANET_SUMO_HOME`) at a SUMO installation of your own that has one, of the release that converted
the world. To watch a drive at the pace of real traffic:

    carla-drive --scenario ... --world-package ... --epoch ... \\
        --illumination advance --solar-rate 1.0 --no-record --real-time-factor 1.0 --steps 0 --sumo-gui

The window is live: pausing it pauses the drive, and a pause longer than `--sumo-answer-timeout`
stops the run as a SUMO that stopped answering; closing it stops the run as a SUMO that died. On
Linux it needs a display.

A compiled scenario is checked against the compile lock the compiler wrote beside its configuration
(`<stem>.lock.json`) before SUMO is started: its configuration, route file and network must be the
ones the lock digests, and the catalogue and epoch given here the ones it was compiled against. A
scenario with no lock runs and is logged as uncompiled. The lock also says whether the compiler ran the
scenario in SUMO alone before writing it, which is what finds a vehicle the supervision plan names that
never enters the simulation; a lock that says the run was skipped (`carla-compile-scenario
--skip-dry-run`), or records none, is refused unless `--accept-skipped-dry-run` is given, and the
report records the acceptance. A scenario whose configuration lets SUMO
teleport a waiting vehicle -- a positive `time-to-teleport`, or none, which SUMO takes as 300 s, or any
of SUMO's other teleport triggers -- is refused unless `--allow-teleporting` is given. One that sets
`ignore-route-errors` is refused outright, because SUMO then keeps a vehicle it cannot route standing at
the end of an edge and says nothing; so is one whose `collision.action` is not `warn` (SUMO's default
is `teleport`, and `none` or `ignore-accidents` would leave no record of collisions), one with a
positive `random-depart-offset`, and one that sets `random`. The report names the demand scale, the cap
on vehicles running and `max-depart-delay` the run ran under.

If either side fails part-way -- SUMO dies, closes the connection or does not answer within
`--sumo-answer-timeout`, the server drops the connection or leaves a tick unanswered, or the sun
disagrees with its declaration or goes away -- both stop together: the script logs the stage and the
cause, prints the report with the last frame whose truth holds, gives back everything it can reach and
exits 1. A collision does not stop the run; the report counts them, with the vehicles SUMO gave up
inserting and SUMO's own warnings. `--collision-detail` prints every collision as it ends, and lists
every collision and every collision warning SUMO wrote in the report; by default only their count is
printed. It changes what is printed and nothing else: the session records every collision either way.

One thing happens between the session starting and the recorder starting: the camera is aimed at the
vehicles rather than at the middle of the world, because a corridor scenario puts its traffic nowhere
near that middle.

Every capture measures, per vehicle, how much of it the camera cannot see. The fixed camera carries a
depth camera attached to it, rigidly and at the camera's own pose, with the camera's image size, field
of view and sensor tick and the capture run's depth range (`occlusion.depth_max_range_m`, 20 km), and
the recorder is started with it; the flown camera's rig carries its own. There is no switch to turn
the measurement off, and nothing in this process moves a depth camera itself: the server keeps an
attached actor at its pose relative to its parent. The depth camera leaves the world before the
camera it is attached to.

The first frames of a run whose camera looks somewhere Cesium has not streamed yet carry imagery
that is still arriving -- an unattended capture has nobody watching the view fill in, where an
operator flying the camera does. How many ticks the tiles take depends on the view and on what is
already cached -- from about 30 to over 700 ticks at 0.05 s have been measured -- so no fixed count
is safe. Open the window later with `--window-opens-at`: the ticks before it stream the camera's
tiles and nothing records them. carla-capture waits for each camera's tiles before its window opens,
and refuses the run when they are not in.

`--view free` replaces that one fixed camera with a camera you fly, in this process: the window,
flight controls and heads-up display of carla-free-camera, opened over
the center of the world's staging bounds at `--camera-z`, looking straight down. F starts a
recording span from that camera and F again ends it; each span is written to a folder of its own
under `--record-dir`, named by the camera and the span's start (`<camera name>-<UTC>`), with the
render set, illumination and occlusion the fixed camera's captures carry. A span starts once the
camera's photoreal tiles are in, waiting up to 90 s for them, and only from the capture window's
opening. Esc or closing the window ends the drive. The window runs on a thread of its own and never ticks the world, so the
drive keeps its clock and its pace; the pacing lines say what it held.

By default every vehicle SUMO has is rendered, wherever it is and however many there are
(`--render-set all`): the scenario is the only arbiter of population, so a camera flown anywhere over
the world finds the traffic SUMO has there. A scenario heavier than the machine is comfortable with
makes the drive slower on the wall clock, never thinner; the pacing lines say what pace it held. A free
view over the whole of Gardnerville at the pace of real traffic:

    carla-drive --scenario ... --world-package ... --epoch ... \\
        --illumination advance --solar-rate 1.0 --real-time-factor 1.0 --steps 0 --view free

Two optional performance controls trade fidelity for speed, and both are off unless asked for.
`--render-set` limits which vehicles get a body at all: `circle` only those inside the circle of
`--region-radius` around `--region-x`, `--region-y` (SUMO's projected meters), released
`--region-hysteresis` beyond it; `cameras` those inside or about to enter the ground footprint of the
camera this drive spawns or flies -- placed `--render-admit-lead` seconds of their own travel ahead of
the view and taken away `--render-release-lag` seconds after they leave it, a view reaching the horizon
capped where the catalogue's longest body covers fewer than `--render-min-pixels` pixels -- with the
circle deciding while no camera is registered where a radius is given, and every vehicle where none
is. `--capacity` caps how many hold a body at once, under any render set. A vehicle a limit leaves out
is still simulated by SUMO, so the traffic is the scenario's, but it is not in CARLA: no body, no
frame and no truth record; the launch says so, the pacing lines count what was left out and the report
counts it again with the reasons each track ended. Under `circle` a free view is told when the circle is
smaller than the world, with the arguments that take all of it in.

`--draw-distance METERS` is the other: no camera draws a vehicle's body farther than that from it. Rendering only -- every vehicle still gets its body, is
posed and is in the truth -- and each capture's sidecar marks the vehicles its camera did not draw
(`beyond_draw_distance`), so none is read as a vehicle the image shows. A server built before it
carried the call refuses it, and the drive goes on drawing every body at any range and says so.

`--world-truth-track PATH` writes the world truth track there: every vehicle SUMO has, drawn or not,
at each SUMO frame inside the capture window, one CSV row per vehicle at TraCI's clock, saying whether
a body drew it on the frame stamped with that instant and, where none did, why. It is the record of
what the world contained -- a camera's sidecars list only what was drawn -- and it is off unless asked
for here; `carla-capture` always writes one. Each SUMO frame's rows are flushed together once they are
written, so a drive cut off loses at most the rows of the frame it was writing, and
`<name>.summary.json` beside it states the rate and, once the drive ends, what the track holds and
why it ended. `--world-truth-track-interval SECONDS` samples every so many simulated seconds instead
of every SUMO frame, a whole number of SUMO steps. A path that already holds a track is refused before
anything starts.

`--run-manifest PATH` writes the run manifest there: one JSON object per line, opened with what the
run is, then every vehicle entering and leaving the render set, the events that change the population
at TraCI's clock, the sun at the window's first and last capture tick and, last, a `manifest_closed`
row saying why the drive ended. A manifest without that row is one whose drive was interrupted. The
fixed camera is named on it as it is spawned. Off unless asked for here; `carla-capture` always writes
one. A path that already holds a manifest is refused before anything starts.

`--cot-udp HOST:PORT` sends a live Cursor-on-Target feed to that address while the drive runs, one
event per UDP datagram: every vehicle with a body on each sampled frame, posed and seated as CARLA drew
it, which is every vehicle SUMO has unless its type names no measured blueprint, an optional limit
leaves it out, or it stands off the world's ground. Each event is the one a capture of that frame
writes for the vehicle in its truth sidecar, with the same uid (`CARLA-TRUTH-SUMO-<SUMO id>`, where
carla-cot-telemetry's is `SUMO-TRUTH-<SUMO id>`; `--cot-uid-prefix` changes its first part), point,
height and fields, less what depends on a picture, the vehicle's SUMO type, which names the scenario
author's class, and the angle SUMO gave it. Its height is the body origin's with the height-align
offset removed, so a vehicle on a bridge is reported on the bridge. A port left out is 6969, a
multicast group works as given, with `--cot-udp-ttl` its time-to-live, and a host name is looked up
once, as the drive starts. Each event is stamped with its frame's civil time, the epoch plus the
frame's simulated second, and goes stale `--cot-stale` seconds after that stamp. `--cot-hz` samples
per simulated second in whole world ticks, the newest frame of each interval, counted from the first
frame drawn, so the frames before the capture window opens are sent too. Each vehicle's affiliation is
the display convention's for its population: `--cot-display-convention`, or `<scenario>.display.json`
beside `--scenario`, with `--cot-affiliation` for any population it does not name. A send that fails is
counted and said, and the drive goes on. The feed writes no file: a capture's sidecars and
`--world-truth-track` record what it sends.

`--cot-emit in-view --cot-camera NAME` sends only the vehicles in the picture of the live camera named
NAME, compared without regard to case (a carla-camera-follower's `--sensor-id`, or this drive's
`--camera-name`), on each sampled frame: a body whose box falls wholly or partly in the picture, the
test a capture's `in_frame` records, and not wholly beyond a `--draw-distance`. A vehicle hidden behind
another is in view. It filters the feed and nothing else: which vehicles get a body is unchanged. While
no live camera holds the name nothing is sent, and the log says when the camera goes and when it is
found again. To watch the gate of the Bahonar port from a camera of its own and send what it sees:

    carla-camera-follower --sensor-id Gate_Orbit_1 --pattern orbit --orbit-centre 1998.4 -1035.0
    carla-drive --scenario ... --world-package ... --epoch ... --no-record --real-time-factor 1.0 \\
        --steps 0 --cot-udp 239.2.3.1:6969 --cot-emit in-view --cot-camera Gate_Orbit_1

Every still is named after its camera and its frame's simulated civil time at the epoch's offset,
`<camera name>_<yyyyMMdd_HHmmss.fff>.png` and `.xml`, and the camera's platform track carries the
name as its callsign. `--camera-name` names the camera, fixed or
free, so that cameras sharing a world -- this drive's and another client's -- are told apart in their
files and their telemetry; without it the server names the camera `Camera_<n>`, which no other camera
on the server holds. A name is short and plain -- `Overwatch_1`, `Southeast_1700m_orbit`,
`NapOfEarth_2`: 1 to 63 characters, each an ASCII letter, digit, underscore or hyphen, and not a
Windows device name, a stock sensor role name (`front`, `back`, ...), the server's own
`Camera_<number>` or `CARLA-SENSOR-<number>`. It is used as given or refused before the drive starts,
never rewritten. It is set as the camera's `role_name` when the camera is spawned, and the server
refuses it then where a live camera in the world already holds it, in any case. The name the camera
holds is read back from the spawned camera, whoever gave it.

Flying: hold the right mouse button and move the mouse to look; W/S A/D E/Q to fly; the wheel sets
the speed, Shift triples it; Ctrl+click measures a point; B/M draw the perimeter and margin; Space
returns to the start pose. carla-free-camera stays the separate viewer that records nothing.
On Linux the window needs a display.
"""
import argparse
import logging
import math
import os
import sys

from carlacontrol.DriveCotFeed import DriveCotFeed
from carlacontrol.DriveRun import DriveRun
from carlacontrol.ProducerRecord import ProducerRecord
from carlacontrol.ToolLayout import ToolLayout

# Where the defaults come from: the checkout this carlacontrol was imported from, or, installed, the
# current folder and the catalogue installed with the package.
LAYOUT = ToolLayout.current()

# The tool every file a drive writes names, the CarlaNet writers' included: this command.
TOOL = "carla-drive"

# Image width, height and horizontal field of view per view, where the command line gives none: the
# fixed camera's as it always was, and the free view's as carla-free-camera opens its window.
VIEW_OPTICS = {"fixed": (1920, 1080, 60.0), "free": (1280, 720, 90.0)}

# The live feed's options besides --cot-udp, with the value each takes when it is left out. Each is
# parsed with no default, so that one given without --cot-udp can be told from one left out.
FEED_DEFAULTS = {
    "--cot-udp-ttl": DriveCotFeed.DEFAULT_TTL,
    "--cot-hz": DriveCotFeed.DEFAULT_RATE_HZ,
    "--cot-stale": DriveCotFeed.DEFAULT_STALE_S,
    "--cot-affiliation": DriveCotFeed.DEFAULT_AFFILIATION,
    "--cot-display-convention": None,
    "--cot-uid-prefix": DriveCotFeed.DEFAULT_UID_PREFIX,
    "--cot-emit": DriveCotFeed.DEFAULT_EMIT,
    "--cot-camera": None,
}


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        prog=os.path.basename(sys.argv[0]), description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=2000)
    parser.add_argument("--setup-timeout", type=float, default=600.0,
                        help="seconds each server call may take while the session checks the "
                             "loaded world and starts. The check reads the loaded world's "
                             "OpenDRIVE, 8.3 MB on Bahonar, and the digests of its bare-earth "
                             "grids rather than the grids, whose two fetches took 146 s and 153 s "
                             "there. Once the session has started, every call gets 30 s again")
    parser.add_argument("--scenario", required=True, help="the scenario's .sumocfg")
    parser.add_argument("--world-package", required=True,
                        help="the .cwp the world was built as")
    parser.add_argument("--catalogue", default=str(LAYOUT.catalogue),
                        help="the measured vehicle catalogue (default: CarlaControl/catalogue/"
                             "vehicles.catalogue.json from a checkout, the one installed with "
                             "carlacontrol otherwise)")
    parser.add_argument("--sumo-home",
                        help="the SUMO installation to launch: the directory holding bin/sumo. "
                             "Default: CARLANET_SUMO_HOME if it holds one, else, from a checkout, "
                             "this repository's pinned build (Build/sumo-install, then "
                             "Build/sumo-src). SUMO_HOME is consulted only when none of those holds "
                             "a sumo")
    parser.add_argument("--allow-sumo-version-mismatch", action="store_true",
                        help="run with a SUMO whose release is not the one that converted the world, "
                             "instead of refusing. The run report records that the mismatch was "
                             "accepted and names both releases")
    parser.add_argument("--sumo-gui", action="store_true",
                        help="launch the installation's sumo-gui in place of sumo, to watch the "
                             "simulation the session is stepping. Refused before anything starts "
                             "where the installation has none (CarlaSetup builds and stages it). "
                             "Pausing the window pauses the drive; closing it stops the run")
    parser.add_argument("--allow-teleporting", action="store_true",
                        help="run a scenario whose configuration lets SUMO teleport a waiting vehicle "
                             "(a positive time-to-teleport, or none, which SUMO takes as 300 s; or "
                             "another teleport trigger, such as time-to-teleport.highways or a vehicle "
                             "type's own timeToTeleport) instead of refusing it. The run report records "
                             "that it was accepted. It accepts no collision.action but warn")
    parser.add_argument("--accept-skipped-dry-run", action="store_true",
                        help="run a compiled scenario whose compile skipped its SUMO-only run "
                             "(carla-compile-scenario --skip-dry-run), or whose lock was written "
                             "before the compiler ran one, instead of refusing it. That run is what "
                             "finds a vehicle the supervision plan names that never enters the "
                             "simulation; "
                             "without it the same fault stops a run only when SUMO drops the vehicle. "
                             "The run report records that the skipped run was accepted")
    parser.add_argument("--collision-detail", action="store_true",
                        help="print every collision as it ends, and list every collision and every "
                             "collision warning SUMO wrote in the report. Off by default, which prints "
                             "only their count. Printing only: every collision is recorded either way")
    parser.add_argument("--no-vehicle-lamps", action="store_true",
                        help="write no lamp to any body: no brake lights or indicators from SUMO and no "
                             "headlights from the sun. A control condition; lamps are driven by default")
    parser.add_argument("--headlight-on-below", type=float, default=3.0,
                        help="sun elevation in degrees below which every vehicle's headlights come on "
                             "(default 3)")
    parser.add_argument("--headlight-off-above", type=float, default=6.0,
                        help="sun elevation in degrees above which they go off again; above the first "
                             "(default 6)")
    parser.add_argument("--sumo-answer-timeout", type=float, default=60.0,
                        help="seconds to wait for SUMO to answer any one command, a step included, "
                             "before the run stops because SUMO has stopped answering. Must exceed "
                             "the slowest step the scenario produces (default 60)")

    parser.add_argument("--steps", type=int, default=600,
                        help="SUMO steps to run. 0 runs until the scenario ends -- until SUMO has "
                             "no vehicle left to simulate or insert -- which is what a live watcher "
                             "wants")
    parser.add_argument("--real-time-factor", type=float, default=0.0,
                        help="hold the world's ticks to the wall clock: simulated seconds per "
                             "wall-clock second. 1.0 is the pace of real traffic, 0.5 half of it, 2.0 "
                             "twice it. 0, the default, runs as fast as the machine allows. The "
                             "fast-forward of --warm-up is never paced")
    parser.add_argument("--warm-up", type=float, default=0.0,
                        help="simulated second to fast-forward SUMO to before the first tick")
    parser.add_argument("--window-opens-at", type=float, default=None,
                        help="simulated second the capture window opens, where the ticks from "
                             "--warm-up to it are a prewarm: a frozen sun is pinned here and an "
                             "advancing one anchored here. Default: the first rendered frame")
    parser.add_argument("--step-length", type=float, default=None,
                        help="override the scenario's SUMO step length, which changes how the "
                             "traffic behaves")
    parser.add_argument("--fixed-delta", type=float, default=0.05,
                        help="simulated seconds per CARLA tick")
    parser.add_argument("--render-set", choices=("all", "circle", "cameras"), default="all",
                        help="which vehicles get a body, an optional performance control: 'all' "
                             "(default) every vehicle SUMO has; 'circle' those inside the region "
                             "circle; 'cameras' those inside or approaching the ground footprint of the "
                             "camera this drive spawns or flies, with the circle deciding while no "
                             "camera is registered where --region-radius is given, and every vehicle "
                             "where it is not. A vehicle left out is simulated by SUMO and is not in "
                             "CARLA: no body, no frame, no truth record")
    parser.add_argument("--region-x", type=float, default=0.0,
                        help="circle and cameras: center of the region circle, SUMO easting in meters")
    parser.add_argument("--region-y", type=float, default=0.0,
                        help="circle and cameras: center of the region circle, SUMO northing in meters")
    parser.add_argument("--region-radius", type=float, default=None,
                        help="circle: inside this a vehicle gets a body; required. cameras: the circle "
                             "that decides while no camera is registered; without it every vehicle "
                             "does then")
    parser.add_argument("--region-hysteresis", type=float, default=60.0,
                        help="circle: how much further out than the radius a vehicle keeps its body; "
                             "cameras: the band beyond a view's admission threshold it is kept in "
                             "(default 60)")
    parser.add_argument("--render-min-pixels", type=float, default=2.0,
                        help="cameras: a view is capped at the range beyond which the catalogue's "
                             "longest body covers fewer than this many pixels along its length, "
                             "anywhere in the picture (default 2)")
    parser.add_argument("--render-admit-lead", type=float, default=3.0,
                        help="cameras: simulated seconds of its own travel ahead of a camera's view a "
                             "vehicle is placed, so it appears out of sight (default 3)")
    parser.add_argument("--render-release-lag", type=float, default=5.0,
                        help="cameras: simulated seconds a vehicle is held after it last was near a "
                             "camera's view, before it is taken away (default 5)")
    parser.add_argument("--capacity", type=int, default=None,
                        help="how many vehicles may hold a body at once, under any render set. "
                             "Default: no limit")
    parser.add_argument("--draw-distance", type=float, default=None, metavar="METERS",
                        help="an optional performance control, off unless given: how far from a "
                             "camera a vehicle's body is drawn. Rendering only: every vehicle still "
                             "has its body, is posed and is in the truth, and each capture marks "
                             "the vehicles its camera did not draw. Default: every body drawn at "
                             "any range")
    parser.add_argument("--world-truth-track", default=None, metavar="PATH",
                        help="write the world truth track to PATH: every vehicle SUMO has, drawn or "
                             "not, at each SUMO frame inside the capture window, one CSV row per "
                             "vehicle, with a summary beside it. Refused where PATH already holds a "
                             "track. Default: none written")
    parser.add_argument("--world-truth-track-interval", type=float, default=None,
                        metavar="SECONDS",
                        help="with --world-truth-track: simulated seconds between its samples, from "
                             "the window's opening, a whole number of SUMO steps. Default: every SUMO "
                             "frame")
    parser.add_argument("--run-manifest", default=None, metavar="PATH",
                        help="write the run manifest to PATH: one JSON object per line, from what the "
                             "run is to a closing row saying why it ended. Refused where PATH already "
                             "holds a manifest. Default: none written")
    parser.add_argument("--cot-udp", default=None, metavar="HOST:PORT",
                        help="send a live Cursor-on-Target feed to this address while the drive "
                             "runs, one event per UDP datagram, for every vehicle with a body on each "
                             "sampled frame: the event a capture of the frame writes in its truth "
                             "sidecar, less what depends on a picture. A port left out is 6969; a "
                             "multicast group, such as TAK's 239.2.3.1:6969, works as given. Default: "
                             "no feed")
    parser.add_argument("--cot-udp-ttl", type=int, default=None, metavar="N",
                        help="with --cot-udp: the multicast time-to-live, 0 to 255 hops (default 1)")
    parser.add_argument("--cot-hz", type=float, default=None, metavar="HZ",
                        help="with --cot-udp: samples per simulated second, counted in whole world "
                             "ticks; each is the newest frame of its interval (default 1)")
    parser.add_argument("--cot-stale", type=float, default=None, metavar="SECONDS",
                        help="with --cot-udp: seconds after its stamp, the frame's civil time, each "
                             "event goes stale (default 3)")
    parser.add_argument("--cot-affiliation", default=None, metavar="A",
                        help="with --cot-udp: the CoT affiliation letter of a vehicle population the "
                             "display convention does not name, and of every vehicle where there is "
                             "no convention: n neutral, f friend, h hostile, u unknown, or another of "
                             "a j k o p s x (default n)")
    parser.add_argument("--cot-display-convention", default=None, metavar="FILE",
                        help="with --cot-udp: the display convention, a JSON file giving each vehicle "
                             "population its CoT affiliation, a population being a vType's "
                             "carla:class_id or, where it has none, its id. Refused where it cannot be "
                             "read. Default: <scenario>.display.json beside --scenario, where there "
                             "is one")
    parser.add_argument("--cot-uid-prefix", default=None, metavar="P",
                        help="with --cot-udp: what every event's uid begins with, as "
                             "<P>-SUMO-<SUMO id>; no whitespace (default CARLA-TRUTH, the truth "
                             "sidecars' own)")
    parser.add_argument("--cot-emit", choices=(DriveCotFeed.ALL, DriveCotFeed.IN_VIEW), default=None,
                        help="with --cot-udp: which vehicles are sent: 'all' (default) every vehicle "
                             "with a body; 'in-view' only those in the picture of the --cot-camera "
                             "camera, wholly or partly, and not wholly beyond a --draw-distance. It "
                             "filters the feed and changes nothing about which vehicles get a body")
    parser.add_argument("--cot-camera", default=None, metavar="NAME",
                        help="with --cot-emit in-view: the name of the live camera whose picture the "
                             "feed keeps, compared without regard to case: a carla-camera-follower's "
                             "--sensor-id, this drive's --camera-name, or a server's Camera_<n>. "
                             "Looked for before the drive starts and after each step while none holds "
                             "the name; nothing is sent while none does")

    parser.add_argument("--show-road-mesh", action="store_true",
                        help="draw the generated road surface. Hidden by default: it is a flat gray "
                             "ribbon laid over the photogrammetry of the real road, so leaving it "
                             "on puts the same rendering artifact in every frame it records. "
                             "Turn it on to see where the network the vehicles drive on lies")
    parser.add_argument("--show-signals", action="store_true",
                        help="draw the generated traffic-light and sign actors. Hidden by default: "
                             "the available meshes are few and are frequently misaligned against "
                             "the photogrammetry. SUMO simulates the signals and its vehicles obey "
                             "them either way -- what is dropped is the rendering, not the signal")

    parser.add_argument("--epoch",
                        help="a JSON file declaring what simulated second zero means in civil time: "
                             "the scenario's `epoch` object on its own, or a scenario.json carrying "
                             "one, whose `illumination` object is then read as well. Required for a "
                             "drive that renders a world: every still is named and stamped from the "
                             "scenario's civil time, and the session refuses a drive without one")
    parser.add_argument("--illumination",
                        choices=("freeze_at_window_start", "advance", "freeze_at", "ignore"),
                        help="what the sun does across the window. Under advance the session "
                             "sets it to the civil instant the window opens. It carries the sun "
                             "forward at --solar-rate and writes it for every frame. Left out, the "
                             "sun advances at a rate of 1.0 by default. The run manifest says so. "
                             "Under freeze_at_window_start the session sets the sun the same way "
                             "and holds it. Under freeze_at it holds the sun at --freeze-at. Under "
                             "ignore it leaves the sun as the world holds it. The run then records "
                             "that the lighting honors no epoch")
    parser.add_argument("--solar-rate", type=float,
                        help="with --illumination advance: sun-clock seconds per simulated "
                             "second. 1.0 keeps the sun on civil time. Left out, the rate is 1.0")
    parser.add_argument("--freeze-at",
                        help="with --illumination freeze_at: the civil time of day, HH:MM:SS, the "
                             "sun is held at whatever time the window opens")
    parser.add_argument("--freeze-date-advances", action="store_true",
                        help="under a freeze, let the sun's date follow the civil date when the "
                             "epoch's calendar advances. Without it a frozen week of windows keeps "
                             "the epoch's own date, and so one seasonal sun geometry")
    parser.add_argument("--no-sun-required", action="store_true",
                        help="run on a world with no sun rather than refusing it. The run is then "
                             "lit by nothing anyone declared, and says so")

    parser.add_argument("--view", choices=("fixed", "free"), default="fixed",
                        help="the camera: 'fixed' spawns one camera, aims it once and records from "
                             "the window's opening to the end; 'free' opens a window with a camera "
                             "you fly, and F records spans from it, each to its own folder under "
                             "--record-dir once the camera's tiles are in. Esc or closing the window "
                             "ends the drive")
    parser.add_argument("--no-record", action="store_true",
                        help="write no frames: no fixed camera is spawned, and a free view's F key "
                             "records nothing")
    parser.add_argument("--record-dir", default=str(LAYOUT.capture_directory),
                        help="where captures are written; a free view writes each span to a folder "
                             "of its own under it (default: Build/captures from a checkout, "
                             "captures under the current folder when installed)")
    parser.add_argument("--record-hz", type=float, default=2.0)
    parser.add_argument("--camera-name", default=None, metavar="NAME",
                        help="the camera's name, fixed or free, given as it is created, such as "
                             "Overwatch_1 or NapOfEarth_2: 1 to 63 characters, each an ASCII letter, "
                             "digit, underscore or hyphen, and not a Windows device name, a stock "
                             "sensor role name (front, back, ...), the server's own Camera_<number>, "
                             "CARLA-SENSOR-<number> or a name a live camera in the world holds, which "
                             "the server refuses. Every still is written as <NAME>_<local capture "
                             "time>.png and .xml, a free view's span folders as <NAME>-<UTC>, and it "
                             "is the callsign of the camera's platform track. Default: the name the "
                             "server gives the camera, Camera_<n>")
    parser.add_argument("--camera-z", type=float, default=300.0,
                        help="camera height, meters; a free view starts there over the center of the "
                             "world's staging bounds, looking straight down")
    parser.add_argument("--camera-standoff", type=float, default=300.0,
                        help="fixed view: camera distance back from the point it looks at, meters")
    parser.add_argument("--camera-yaw", type=float, default=0.0,
                        help="fixed view: bearing the camera stands off along, degrees clockwise "
                             "from north")
    parser.add_argument("--camera-aim", choices=("traffic", "world-centre"), default="traffic",
                        help="fixed view: what the camera looks at: 'traffic' the mean position of "
                             "the vehicles rendered on the first step, 'world-centre' the center of "
                             "the world's staging bounds. The middle of a corridor scenario's world "
                             "is usually not where its traffic is")
    parser.add_argument("--flight-speed", type=float, default=60.0,
                        help="free view: the camera's starting flight speed, m/s; the mouse wheel "
                             "changes it")
    parser.add_argument("--width", type=int, default=None,
                        help="camera image width; default 1920 fixed, 1280 free (the window's size)")
    parser.add_argument("--height", type=int, default=None,
                        help="camera image height; default 1080 fixed, 720 free")
    parser.add_argument("--fov", type=float, default=None,
                        help="camera horizontal field of view, degrees; default 60 fixed, 90 free")
    args = parser.parse_args(argv)
    for name, default in zip(("width", "height", "fov"), VIEW_OPTICS[args.view], strict=True):
        if getattr(args, name) is None:
            setattr(args, name, default)
    _refuse_unusable_feed_options(parser, args)
    return args


def _refuse_unusable_feed_options(parser: argparse.ArgumentParser, args: argparse.Namespace) -> None:
    """Exit 2 naming every live feed option that cannot be used, before anything connects; then give
    each one left out its default."""
    given = [option for option in FEED_DEFAULTS if getattr(args, _destination(option)) is not None]
    if args.cot_udp is None:
        if given:
            parser.error(f"{', '.join(given)} {'sets' if len(given) == 1 else 'set'} the live feed "
                         "that --cot-udp HOST:PORT turns on: give --cot-udp, or leave "
                         f"{'it' if len(given) == 1 else 'them'} out")
        return
    problems = []
    try:
        DriveCotFeed.address(args.cot_udp)
    except ValueError as malformed:
        problems.append(f"--cot-udp {malformed}")
    if args.cot_udp_ttl is not None and not 0 <= args.cot_udp_ttl <= 255:
        problems.append(f"--cot-udp-ttl {args.cot_udp_ttl} is outside 0 to 255")
    for option, value, unit in (("--cot-hz", args.cot_hz, "samples per simulated second"),
                                ("--cot-stale", args.cot_stale, "seconds")):
        if value is not None and not (math.isfinite(value) and value > 0.0):
            problems.append(f"{option} {value:g} must be a positive number of {unit}")
    if args.cot_affiliation is not None:
        refused = DriveCotFeed.affiliation_problem(args.cot_affiliation)
        if refused is not None:
            problems.append(f"--cot-affiliation {refused}")
    if args.cot_uid_prefix is not None:
        refused = DriveCotFeed.uid_prefix_problem(args.cot_uid_prefix)
        if refused is not None:
            problems.append(f"--cot-uid-prefix {refused}")
    # Not held to the camera-name rule, which refuses the server's own Camera_<n>: any name a live
    # camera holds can be followed.
    if args.cot_camera is not None and not args.cot_camera:
        problems.append("--cot-camera names no camera")
    if args.cot_emit == DriveCotFeed.IN_VIEW and args.cot_camera is None:
        problems.append("--cot-emit in-view needs --cot-camera NAME, the live camera whose picture it "
                        "keeps")
    if args.cot_emit != DriveCotFeed.IN_VIEW and args.cot_camera:
        problems.append("--cot-camera needs --cot-emit in-view: every vehicle is sent otherwise, and "
                        "no camera decides which")
    if problems:
        parser.error("; ".join(problems))
    for option, default in FEED_DEFAULTS.items():
        if getattr(args, _destination(option)) is None:
            setattr(args, _destination(option), default)


def _destination(option: str) -> str:
    """The attribute argparse stores an option under."""
    return option.removeprefix("--").replace("-", "_")


def main(argv: list[str] | None = None) -> int:
    # First, so the stills, the world truth track and the run manifest all name this command and
    # carlacontrol's release.
    ProducerRecord.declare_tool(TOOL)
    args = parse_args(argv)
    logging.basicConfig(level=logging.INFO, format="%(message)s", stream=sys.stdout)
    return DriveRun(args, TOOL).run()
