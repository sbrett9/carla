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
    -- and the zone name is carried for a reader and never resolved.

The sun's policy has no default. A frozen run and a run nobody configured write identical records,
so the session refuses to render a world whose run does not say what its sun is doing.
`--illumination freeze_at_window_start` is the recommended one: after SUMO is fast-forwarded and
before the first tick, the sun is set to the civil instant the window opens -- `--window-opens-at`, or
the first rendered frame -- read back to confirm the world took it, and held there. The sun the world
was found with is given back at the end.

Usage (from a checkout, `python CarlaNet/python/run_sumo_drive.py` is the same tool):

    carla-drive \\
        --scenario <scenario dir>/<scenario_id>.sumocfg \\
        --world-package <build dir>/Gardnerville_Centerville_Lane.cwp \\
        --catalogue CarlaControl/catalogue/vehicles.catalogue.json \\
        --epoch gardnerville.epoch.json --illumination freeze_at_window_start \\
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
        --illumination freeze_at_window_start --no-record --real-time-factor 1.0 --steps 0

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
        --illumination freeze_at_window_start --no-record --real-time-factor 1.0 --steps 0 --sumo-gui

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
operator flying the camera does. What that costs, and why the number of ticks it takes is not a
caller's to supply, is in
`Docs/CAT_Research/Plans/SUMO_Behavioral_Capture/03_CoSimulation_Runtime.md` section 9.5.1.

`--view free` replaces that one fixed camera with a camera you fly, in this process: the window,
flight controls and heads-up display of carla-free-camera, opened over
the centre of the world's staging bounds at `--camera-z`, looking straight down. F starts a
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
        --illumination freeze_at_window_start --real-time-factor 1.0 --steps 0 --view free

Two optional performance controls trade fidelity for speed, and both are off unless asked for.
`--render-set` limits which vehicles get a body at all: `circle` only those inside the circle of
`--region-radius` around `--region-x`, `--region-y` (SUMO's projected metres), released
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

`--draw-distance METRES` is the other: no camera draws a vehicle's body farther than that from it. Rendering only -- every vehicle still gets its body, is
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

Every still is named after its camera, `<camera name>_<local capture time>.png` and `.xml`, and the
camera's platform track carries the name as its callsign. `--camera-name` names the camera, fixed or
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
import json
import logging
import math
import os
import sys
import time
from datetime import UTC, datetime

import carlanet as carla

# isort: split
# The .NET namespaces exist only once carlanet has loaded their assemblies, so this import has to
# follow it; an import sorter would put it first and break the tool at start.
from CarlaNet.CoSim import CoSimSessionRefusedException

# isort: split
from carlacontrol.FreeView import FreeView
from carlacontrol.PyGameSensorController import PyGameSensorController
from carlacontrol.RenderRegionCoverage import RenderRegionCoverage
from carlacontrol.RunConfiguration import RunConfiguration
from carlacontrol.SensorRig import SensorRig
from carlacontrol.SpanRecorder import SpanRecorder
from carlacontrol.ToolLayout import ToolLayout
from carlacontrol.WorldPackageReader import WorldPackageReader

# Where the defaults come from: the checkout this carlacontrol was imported from, or, installed, the
# current folder and the catalogue installed with the package.
LAYOUT = ToolLayout.current()

# How long any one server call may take once the session is driving.
RUN_TIMEOUT_S = 30.0

# Image width, height and horizontal field of view per view, where the command line gives none: the
# fixed camera's as it always was, and the free view's as carla-free-camera opens its window.
VIEW_OPTICS = {"fixed": (1920, 1080, 60.0), "free": (1280, 720, 90.0)}

logger = logging.getLogger("run_sumo_drive")


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
                        help="override the scenario's SUMO step length (behaviour-changing)")
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
                        help="circle and cameras: centre of the region circle, SUMO easting in metres")
    parser.add_argument("--region-y", type=float, default=0.0,
                        help="circle and cameras: centre of the region circle, SUMO northing in metres")
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
    parser.add_argument("--draw-distance", type=float, default=None, metavar="METRES",
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

    parser.add_argument("--show-road-mesh", action="store_true",
                        help="draw the generated road surface. Hidden by default: it is a flat grey "
                             "ribbon laid over the photogrammetry of the real road, so leaving it "
                             "on puts the same rendering artefact in every frame of the corpus. "
                             "Turn it on to see where the network the vehicles drive on lies")
    parser.add_argument("--show-signals", action="store_true",
                        help="draw the generated traffic-light and sign actors. Hidden by default: "
                             "the available meshes are few and are frequently misaligned against "
                             "the photogrammetry. SUMO simulates the signals and its vehicles obey "
                             "them either way -- what is dropped is the rendering, not the signal")

    parser.add_argument("--epoch",
                        help="a JSON file declaring what simulated second zero means in civil time: "
                             "the scenario's `epoch` object on its own, or a scenario.json carrying "
                             "one, whose `illumination` object is then read as well")
    parser.add_argument("--illumination",
                        choices=("freeze_at_window_start", "advance", "freeze_at", "ignore"),
                        help="what the sun does across the window. No default: "
                             "freeze_at_window_start (recommended) sets it to the civil instant the "
                             "window opens and holds it; advance carries it forward at --solar-rate, "
                             "the session writing it for every frame; freeze_at holds it at "
                             "--freeze-at; ignore leaves it as "
                             "the world holds it and records that the lighting honours no epoch")
    parser.add_argument("--solar-rate", type=float,
                        help="with --illumination advance: sun-clock seconds per simulated second; "
                             "1.0 keeps the sun on civil time")
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
                        help="camera height, metres; a free view starts there over the centre of the "
                             "world's staging bounds, looking straight down")
    parser.add_argument("--camera-standoff", type=float, default=300.0,
                        help="fixed view: camera distance back from the point it looks at, metres")
    parser.add_argument("--camera-yaw", type=float, default=0.0,
                        help="fixed view: bearing the camera stands off along, degrees clockwise "
                             "from north")
    parser.add_argument("--camera-aim", choices=("traffic", "world-centre"), default="traffic",
                        help="fixed view: what the camera looks at: 'traffic' the mean position of "
                             "the vehicles rendered on the first step, 'world-centre' the centre of "
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
    return args


def world_centre(world) -> tuple[float, float]:
    """The centre of the world's staging bounds in CARLA's frame, or CARLA's origin where the world
    publishes none.

    A fact about the world -- the middle of the area it was built from -- rather than a setting, so a
    camera that has nothing yet to look at starts over the world itself.
    """
    bounds = world.get_staging_bounds()
    if bounds is None:
        return 0.0, 0.0
    return (bounds["min_x"] + bounds["max_x"]) / 2.0, (bounds["min_y"] + bounds["max_y"]) / 2.0


class SessionSumo:
    """Which SUMO installation this script names for the session, and the rule that chose it.

    Named rather than left to the session's own search, because that search finds the repository by
    walking upward from the CarlaNet assemblies, and assemblies loaded from an installed wheel sit in
    site-packages with no repository above them: the walk finds nothing and `SUMO_HOME` decides --
    on a machine with a system-wide SUMO, a different release from the one that converted the world.
    The rules and their order are the session's own (`CarlaNet.Sumo.SumoInstallation`):
    `CARLANET_SUMO_HOME`, the repository's staged build, its source build. `--sumo-home` goes ahead
    of all of them and is passed on as given, so a directory with no SUMO in it is refused by the
    session by name rather than skipped here. Where no rule finds a `sumo`, nothing is named and the
    session searches `SUMO_HOME`, then PATH.

    This chooses the installation and nothing else. Whether its release is the world's converter is
    decided by the session, which is the one place that comparison is made.
    """

    OVERRIDE_VARIABLE = "CARLANET_SUMO_HOME"
    EXECUTABLE = "sumo.exe" if os.name == "nt" else "sumo"
    # The checkout's own builds, staged then source; none when carlacontrol is installed.
    REPOSITORY_BUILDS = tuple((rule, str(home)) for rule, home in LAYOUT.repository_sumo_builds())

    def __init__(self, explicit: str | None) -> None:
        self.home: str | None = None
        self.rule = "no rule found one"
        if explicit:
            self.home, self.rule = explicit, "--sumo-home"
            return
        candidates = ((self.OVERRIDE_VARIABLE, os.environ.get(self.OVERRIDE_VARIABLE)),
                      *self.REPOSITORY_BUILDS)
        for rule, home in candidates:
            if home and os.path.isfile(os.path.join(home, "bin", self.EXECUTABLE)):
                self.home, self.rule = os.path.abspath(home), rule
                return

    def announce(self, gui: bool) -> None:
        """Say which installation is being named, and which of its binaries, before anything is
        started. Whether the installation has a `sumo-gui` is the session's to say."""
        binary = "sumo-gui in place of sumo" if gui else "sumo"
        if self.home is None:
            logger.info("SUMO: none named -- no --sumo-home, %s holds none and %s -- so the "
                        "session searches SUMO_HOME, then PATH, and launches %s",
                        self.OVERRIDE_VARIABLE,
                        "there is no repository build, carlacontrol being installed"
                        if LAYOUT.installed else "this repository has no build under Build/",
                        binary)
        else:
            logger.info("SUMO: naming %s for the session (%s), which launches %s", self.home,
                        self.rule, binary)


class SunDeclaration:
    """The scenario's epoch and the run's sun policy, as the session is handed them.

    The session reads both and is the one validator of either; this only gathers them. An epoch file
    may be the `epoch` object on its own or a whole scenario.json, whose `illumination` object is
    then the policy. A policy given in the file and on the command line is refused rather than
    resolved: which of two declarations wins is the operator surface's decision to make, not this
    script's.
    """

    def __init__(self, args: argparse.Namespace) -> None:
        self.epoch: dict | None = None
        self.illumination: dict | None = None
        from_file: dict | None = None
        if args.epoch:
            with open(args.epoch, encoding="utf-8") as handle:
                document = json.load(handle)
            if isinstance(document, dict) and isinstance(document.get("epoch"), dict):
                self.epoch = document["epoch"]
                from_file = document.get("illumination")
            else:
                self.epoch = document

        from_command_line = self.from_command_line(args)
        if from_file is not None and from_command_line is not None:
            raise SystemExit(
                f"{args.epoch} declares the '{from_file.get('policy')}' illumination policy and the "
                f"command line declares '{from_command_line.get('policy')}'. Declare it in one place.")
        self.illumination = from_file if from_file is not None else from_command_line

    @staticmethod
    def from_command_line(args: argparse.Namespace) -> dict | None:
        """The `illumination` object the flags describe, or None where none of them was given.

        Every flag given is passed on, including one that belongs to another policy, so the session
        refuses the combination by name rather than this script quietly dropping half of it.
        """
        given = (args.illumination, args.solar_rate, args.freeze_at)
        if all(value is None for value in given) and not (
                args.freeze_date_advances or args.no_sun_required):
            return None
        declared: dict = {"illumination_version": 1}
        if args.illumination is not None:
            declared["policy"] = args.illumination
        if args.solar_rate is not None:
            declared["rate_sun_s_per_sim_s"] = args.solar_rate
        if args.freeze_at is not None:
            declared["freeze_at_civil_time"] = args.freeze_at
        if args.freeze_date_advances:
            declared["freeze_date_advances"] = True
        if args.no_sun_required:
            declared["require_sun"] = False
        return declared


class RenderedVehicleCentre:
    """Where the vehicles are, measured from the poses the bridge wrote to bodies.

    The middle of the world is a poor thing to point a camera at. On a corridor scenario the world is
    built around a road that runs through it, so its middle is whatever that road happens to pass: a
    run aimed at the middle of its area framed a builder's yard while the traffic was on a highway a
    hundred metres away. The mean of the poses is not a guess about where the traffic ought to be --
    it is where the bodies were put.

    The session hands out one pose per rendered vehicle per tick, from the tick thread, so a run's
    worth of them kept in a list is a run-length leak. This keeps a running sum instead, and only
    while it is open: the camera is aimed once, before the recorder starts, and does not move again.
    Poses that were written to no body are left out, because a vehicle with no body is not in frame.
    """

    def __init__(self) -> None:
        self.open = False
        self.total_x = 0.0
        self.total_y = 0.0
        self.count = 0

    def collect(self, record) -> None:
        """Take one computed pose. Bound to the session's pose callback for the whole run."""
        if self.open and record.Actor != 0:
            self.total_x += record.Pose.X
            self.total_y += record.Pose.Y
            self.count += 1

    def centre(self) -> tuple[float, float] | None:
        """The mean position in CARLA's frame, or None where nothing was rendered."""
        if self.count == 0:
            return None
        return self.total_x / self.count, self.total_y / self.count


class PacingProgress:
    """Says, once per closed pacing window, how far the run has got and what pace it held.

    Every figure is the session's own (`session.Report.Pacing`), read between advances: nothing here
    times anything, so what this prints and what the final report records cannot disagree. A line
    per window of wall clock rather than per so many steps, because at real time a scenario with a
    one-second SUMO step would otherwise print once every few minutes.
    """

    def __init__(self) -> None:
        self.windows_seen = 0

    @staticmethod
    def declared(pacing) -> str:
        """The pace the session was declared to hold, as it echoes it back."""
        if pacing.Paced:
            return f"held to {pacing.DeclaredFactor:g}x real time"
        return "not paced, as fast as the machine allows"

    @staticmethod
    def achieved(pacing) -> str:
        """The pace held over the last window and the whole run, and for a paced run the slip."""
        if pacing.AchievedFactor is None:
            return "nothing timed yet"
        text = f"{pacing.AchievedFactor:.3f}x over the whole run"
        if pacing.LastWindowFactor is not None:
            text = (f"{pacing.LastWindowFactor:.3f}x over the last {pacing.WindowSeconds:g} s, "
                    f"{text}, worst window {pacing.WorstWindowFactor:.3f}x")
        if pacing.Paced:
            text += f", {pacing.BehindScheduleSeconds:.2f} s behind schedule"
        return text

    def after_step(self, session, steps: int) -> None:
        """Log a line if a pacing window closed during the step just taken."""
        pacing = session.Report.Pacing
        if pacing.CompletedWindows == self.windows_seen:
            return
        self.windows_seen = pacing.CompletedWindows
        # The divergence figures are the report's own, live: how far a body's applied pose has been
        # from the pose it was given, and how far the velocity the world reports for it -- the one
        # the truth telemetry reads -- from the velocity it was given.
        logger.info("  %d steps, t=%.1f s, %d rendered, pace %s, worst divergence %.4f m, "
                    "%.4f m/s", steps, session.RenderedTimeSeconds,
                    session.RenderedVehicleIds.Count, self.achieved(pacing),
                    session.Report.WorstPositionDivergenceMetres,
                    session.Report.WorstVelocityDivergenceMetresPerSecond)
        admission = session.Report.LastAdmissionPass
        if admission is None:
            return
        if not admission.Limited:
            logger.info("  sumo population %d, all in the render set; %d admitted and %d released at "
                        "the last pass, %d admitted in all", admission.Population,
                        admission.NewlyAdmitted, admission.Released, admission.TotalAdmissions)
            return
        logger.info("  sumo population %d, eligible %d, drawn %d, shed %d%s; %d without a body; %s",
                    admission.Population, admission.Eligible, admission.Admitted, admission.Shed,
                    "" if admission.Capacity is None else f", capacity {admission.Capacity}",
                    admission.Population - admission.Admitted, self.rule(admission))

    @staticmethod
    def rule(admission) -> str:
        """Which rule the pass decided by, as the log says it."""
        if str(admission.Rule) == "Cameras":
            return (f"by {admission.Cameras} camera footprint(s), {admission.Held} held by the "
                    "release lag")
        if str(admission.Rule) == "Circle":
            return "by the circle"
        return "every vehicle, up to the capacity"


class CameraFootprints:
    """Says each camera's footprint and range cap once, the first time the session follows it.

    The session measures a camera's footprint at the first step after it is registered, so this reads
    the report after every step; a camera it has already said is not said again. Only a render set
    that follows the cameras measures one.
    """

    def __init__(self) -> None:
        self.said: set[int] = set()

    def after_step(self, session) -> None:
        footprints = session.Report.CameraFootprints
        if footprints.Count == len(self.said):
            return
        for footprint in footprints.Values:
            if int(footprint.Actor) not in self.said:
                self.said.add(int(footprint.Actor))
                logger.info("render set follows %s", footprint)


def describe_render_set(report) -> str:
    """The render set as the launch states it: every vehicle, or the limit and what it leaves out."""
    if not report.RenderSetLimits:
        return f"{report.RenderSetPolicy}; SUMO runs under seed {report.SumoSeed}"
    return (f"{report.RenderSetPolicy}; SUMO runs under seed {report.SumoSeed}. An optional limit, "
            "chosen for speed: a vehicle outside it is simulated by SUMO and is not in CARLA -- no "
            "body, no frame, no truth record")


def describe_draw_distance(metres: float | None) -> str:
    """The draw distance as the launch states it: none, or what it does and does not change."""
    if metres is None:
        return "none; every body is drawn at any range"
    return (f"{metres:g} m, rendering only: every vehicle has its body, is posed and is in the "
            "truth; a body farther than that from a camera is not drawn in that camera's image, and "
            "each capture's sidecar marks it")


def print_collision(span) -> None:
    """One collision as it ends, under --collision-detail. Called from the session's tick thread; a log
    line does not block it."""
    logger.info("collision: %s", span)


def describe_world_truth_track(track) -> str:
    """The world truth track as the launch states it: none, or where it goes and how often."""
    if track is None:
        return "none written; give --world-truth-track to write one"
    every = ("every SUMO frame" if track.SumoStepsPerSample == 1
             else f"every {track.IntervalSeconds:g} s")
    return (f"{track.Path}, {every} inside the capture window, every vehicle SUMO has, drawn or not; "
            f"summary {track.SummaryPath}")


def camera_transform(args: argparse.Namespace, centre: tuple[float, float]) -> carla.Transform:
    """An oblique view of a point, standing off along a bearing and looking back at it."""
    centre_x, centre_y = centre
    bearing = math.radians(args.camera_yaw)
    camera_x = centre_x - (args.camera_standoff * math.sin(bearing))
    camera_y = centre_y + (args.camera_standoff * math.cos(bearing))

    look_x = centre_x - camera_x
    look_y = centre_y - camera_y
    horizontal = math.hypot(look_x, look_y)
    pitch = math.degrees(math.atan2(-args.camera_z, horizontal)) if horizontal > 0 else -90.0
    yaw = math.degrees(math.atan2(look_y, look_x))
    return carla.Transform(carla.Location(x=camera_x, y=camera_y, z=args.camera_z),
                           carla.Rotation(pitch=pitch, yaw=yaw, roll=0.0))


def spawn_camera(world, args: argparse.Namespace, centre: tuple[float, float]):
    blueprint = world.get_blueprint_library().find("sensor.camera.rgb")
    blueprint.set_attribute("image_size_x", str(args.width))
    blueprint.set_attribute("image_size_y", str(args.height))
    if blueprint.has_attribute("fov"):
        blueprint.set_attribute("fov", str(args.fov))
    # Render at the rate the recorder keeps, not at the world's. Left unset, the camera renders and
    # streams a frame every tick while the recorder keeps one in ten, and the frames the recorder
    # discards are discarded after they have been rendered, read back off the GPU and sent. At
    # 1920x1080 that was measured at 35.1 ms of a 43.0 ms tick, and the backlog stalls the server
    # until an apply_batch times out. sensor_tick suppresses the render itself, not merely the
    # delivery, so the cost goes with it.
    if blueprint.has_attribute("sensor_tick") and args.record_hz > 0:
        blueprint.set_attribute("sensor_tick", str(1.0 / args.record_hz))
    transform = camera_transform(args, centre)
    # Spawned under its name as the camera's role_name, which the server refuses where a live camera
    # in the world holds it; given none, the server names it. The name is read back from the camera.
    camera = world.spawn_camera(blueprint, transform, name=args.camera_name)
    logger.info("camera %s (%s) at (%.1f, %.1f, %.1f), pitch %.1f, yaw %.1f, looking at (%.1f, %.1f)",
                world.camera_name(camera), camera.id, transform.location.x, transform.location.y,
                transform.location.z, transform.rotation.pitch, transform.rotation.yaw, centre[0],
                centre[1])
    return camera


def spawn_depth_camera(world, args: argparse.Namespace, camera, range_m: float):
    """The depth camera the fixed camera's occlusion is measured against, attached to the camera.

    A rigid attachment at the identity pose, as `CaptureSession._attach_depth_camera` spawns a
    channel's: the server keeps an attached actor at its pose relative to its parent and reports its
    world pose on every snapshot and in every image header, so the camera's pose is the depth
    camera's by construction and the recorder's depth pose check compares two poses nothing set
    apart. It takes the camera's image size, field of view and sensor tick, so its captures pair with
    the camera's frame for frame, and the capture run's one depth range, so occlusion is measured as
    far as a capture run's is rather than to the depth camera's stock 1000 m. Destroyed before the
    camera it is attached to.
    """
    blueprint = world.get_blueprint_library().find("sensor.camera.depth")
    blueprint.set_attribute("image_size_x", str(args.width))
    blueprint.set_attribute("image_size_y", str(args.height))
    if blueprint.has_attribute("fov"):
        blueprint.set_attribute("fov", str(args.fov))
    if blueprint.has_attribute("sensor_tick") and args.record_hz > 0:
        blueprint.set_attribute("sensor_tick", str(1.0 / args.record_hz))
    if blueprint.has_attribute("max_range"):
        blueprint.set_attribute("max_range", str(range_m))
    else:
        logger.warning("the server's depth camera has no max_range attribute, so occlusion is "
                       "measured to its built-in range and no farther")
    depth = world.spawn_actor(blueprint, carla.Transform(), attach_to=camera,
                              attachment_type=carla.AttachmentType.Rigid)
    logger.info("depth camera %s attached to camera %s, range %g m", depth.id, camera.id, range_m)
    return depth


def depth_range_m() -> float:
    """The capture run's one depth range, `occlusion.depth_max_range_m`, read from the run
    configuration's field table so a drive and a capture run measure occlusion to one range."""
    return float(RunConfiguration.field("occlusion.depth_max_range_m").default)


def report_captures(recorder) -> None:
    """Say what a recorder wrote. Read once it has flushed, so the counts are its run's and not a
    moment's."""
    logger.info("captures           %s written, %s dropped; %s carry their frame's "
                "illumination declaration, %s do not", recorder.Saved, recorder.Dropped,
                recorder.IlluminationPaired, recorder.IlluminationUnpaired)
    # A capture whose snapshot carried no sun was written with no _solar and no carla:solar: a still
    # with no recorded illumination and no band, so any is said louder.
    missing_sun = recorder.SolarBlockMissing
    (logger.warning if missing_sun else logger.info)(
        "sun                %s captures were written without a solar block", missing_sun)
    drawn_under = getattr(recorder, "DrawDistanceCaptures", 0)
    if drawn_under:
        # Vehicles in the world and in the truth that a capture's image did not show, each marked in
        # its sidecar so none is read as seen.
        logger.info("draw distance      %s captures drawn under it; %s vehicle records marked "
                    "wholly beyond it, %s partly", drawn_under, recorder.VehiclesBeyondDrawDistance,
                    recorder.VehiclesPartlyBeyondDrawDistance)
    # A capture whose frame's render set the session no longer held lists no vehicle rather
    # than a guessed set, so any such capture is truth missing, and is said louder.
    (logger.warning if recorder.RenderSetUnpaired or recorder.RenderSetBodiesMissing
     else logger.info)(
        "render set         %s captures list their frame's rendered vehicles, %s list none "
        "because the frame's set was no longer held; %s rendered bodies had no truth "
        "record", recorder.RenderSetPaired, recorder.RenderSetUnpaired,
        recorder.RenderSetBodiesMissing)
    # The supervision each capture carries is the server's for its own frame. A capture of a frame a
    # plan was in force on whose supervision was not to be had says so and carries none, which is
    # truth missing, and is said louder. A recorder built before it counted none.
    unknown = getattr(recorder, "SupervisionUnpaired", None)
    if unknown is not None:
        (logger.warning if unknown else logger.info)(
            "supervision        %s captures carry their frame's supervision, %s say it was unknown",
            recorder.SupervisionPaired, unknown)
    # Each vehicle in the picture carries its lights and, drawn by the drive, its pose source from its
    # frame's snapshot; a capture whose snapshot carried neither -- a server built before it did -- says
    # so and carries none, which is truth missing, and is said louder. A recorder built before it counted
    # none.
    lights_unknown = getattr(recorder, "LightsUnknown", None)
    if lights_unknown is not None:
        poses_unknown = recorder.PoseSourceUnknown
        (logger.warning if lights_unknown or poses_unknown else logger.info)(
            "lights, poses      %s captures say their vehicles' lights were unknown, %s say their "
            "pose source was", lights_unknown, poses_unknown)
    if recorder.ChecksSensorPose:
        # A capture is placed where its frame's snapshot holds the camera, and its image header is
        # checked against that. A header that disagreed is a server stamping the image's pose after
        # the frame, which the snapshot's pose has covered for but the server should not do, so it
        # is said louder.
        (logger.warning if recorder.SensorPoseHeaderDisagreed else logger.info)(
            "sensor pose        %s captures placed from their own frame's snapshot, %s of them with "
            "an image header that disagreed; %s placed from the header because their frame was no "
            "longer held", recorder.SensorPoseFromSnapshot, recorder.SensorPoseHeaderDisagreed,
            recorder.SensorPoseFromHeader)
    if recorder.MeasuresOcclusion:
        # A capture the depth camera could not be paired with carries no occlusion, which a
        # consumer counting unoccluded vehicles has to leave out, so any such capture is said louder.
        (logger.warning if recorder.OcclusionUnmatched else logger.info)(
            "occlusion          measured on %s captures, not on %s (%s with no depth frame, %s with "
            "the depth stream out of step, %s with the cameras at different poses)",
            recorder.OcclusionMeasured, recorder.OcclusionUnmatched,
            recorder.OcclusionNoDepthCaptures, recorder.OcclusionDepthOutOfStep,
            recorder.OcclusionDepthWrongPose)
        if recorder.ChecksDepthPose:
            (logger.warning if recorder.OcclusionDepthPoseHeaderDisagreed else logger.info)(
                "depth pose         %s depth captures projected from their own frame's snapshot, "
                "%s of them with a header that disagreed; %s from the header",
                recorder.OcclusionDepthPoseFromSnapshot, recorder.OcclusionDepthPoseHeaderDisagreed,
                recorder.OcclusionDepthPoseFromHeader)


def warn_of_an_uncovered_world(args: argparse.Namespace) -> None:
    """Say what a free view will find rendered under a limit: under the cameras, whatever it flies
    over; under the circle, only the circle, and then whether the circle is smaller than the world.

    A circle is fixed and a free camera can go anywhere, so the operator is told before the drive
    starts, with the region that would take in the whole world package. With no limit there is
    nothing to say: every vehicle SUMO has is rendered.
    """
    if args.render_set == "all":
        return
    if args.render_set == "cameras":
        logger.info("free view: vehicles are rendered where the flown camera looks, placed %g s of "
                    "their travel ahead of its view and taken away %g s after they leave it; until "
                    "the camera is registered %s decides", args.render_admit_lead,
                    args.render_release_lag,
                    "the region circle" if args.region_radius is not None else "every vehicle")
        return
    coverage = RenderRegionCoverage.from_manifest(WorldPackageReader(args.world_package).manifest)
    if coverage is None:
        logger.info("free view: the world package records no extent to check the region against")
        return
    advice = coverage.advice(args.region_x, args.region_y, args.region_radius, args.capacity)
    if advice is None:
        logger.info("free view: the render region takes in the whole world")
    else:
        logger.warning("free view: %s", advice)


def free_view_settings(args: argparse.Namespace, centre: tuple[float, float], feet_per_metre: float,
                       depth_range_m: float) -> argparse.Namespace:
    """The flight window's settings, in the shape `SensorRig` and `PygameInterface` read them, with
    the camera starting over `centre` in CARLA's frame."""
    x, y = centre
    return argparse.Namespace(
        x=x, y=y,
        # The rig takes its start altitude in feet, as run_SCTMV.py does.
        z=args.camera_z * feet_per_metre,
        width=args.width, height=args.height, fov=args.fov,
        # The camera keeps its blueprint's exposure, the Default profile's: manual, ISO 100, 1/320 s,
        # f/4 and no compensation. Every capture records it from the camera's attributes.
        ev=None,
        # The run configuration's one depth range, so occlusion is measured as far as a capture
        # run's is rather than to the depth camera's stock 1000 m.
        depth_max_range=depth_range_m,
        # Frames are kept as they arrive: this window never ticks the world.
        asynchronous=True,
        fixed_delta=args.fixed_delta,
        time_rate=1.0,
        # The flown camera is spawned under it, and every span from it is named after it.
        camera_name=args.camera_name)


class FreeViewParts:
    """What a free view is made of, filled in as each part is made.

    The way out takes down exactly what was made, in order: the recorder first, flushing its span
    while the cameras still exist; then the window and the flight controller's mover, so nothing
    moves a camera being destroyed; then the cameras.
    """

    def __init__(self) -> None:
        self.rig = None
        self.recorder = None
        self.view = None
        self.session = None

    @property
    def closed(self) -> bool:
        """Whether the operator has closed the window."""
        return self.view is not None and self.view.closed.is_set()

    def open(self, client, world, session, args: argparse.Namespace, run_id: str) -> None:
        settings = free_view_settings(args, world_centre(world), SensorRig.FT_PER_M,
                                      depth_range_m())
        # Spawned with the world already the session's, so every frame either camera delivers is of
        # a tick the session issued.
        self.rig = SensorRig(world=world, args=settings, client=client)
        if args.render_set == "cameras":
            self.follow(session)
        controller = PyGameSensorController(self.rig, world, self.rig.get_initial_pose(),
                                            speed=args.flight_speed)
        if not args.no_record:
            self.recorder = self.span_recorder(client, session, args, run_id)
            self.recorder.run_in_background()
        self.view = FreeView(settings, world, self.rig, controller, recorder=self.recorder)
        self.view.open()
        logger.info("free view: camera %s over (%.1f, %.1f) at %.0f m, %dx%d, fov %g; %s",
                    self.rig.camera.id, settings.x, settings.y, args.camera_z, args.width,
                    args.height, args.fov,
                    "F records a span once the camera's tiles are in, from the capture window's "
                    f"opening at t={session.WindowOpensAtSeconds:g} s; Esc ends the drive"
                    if self.recorder is not None else "nothing is recorded; Esc ends the drive")

    def span_recorder(self, client, session, args: argparse.Namespace, run_id: str):
        """The recorder F toggles: the fixed camera's recording, from the flown camera.

        It records through a world object of its own, so its recorder is the only one that
        object's stop_recording ever stops. Every span shares this drive's run id, so the spans of
        one drive can be gathered back into it, and every span's folder and stills are named after
        the camera: the name it was spawned under, or its default.
        """
        recording = client.get_world()
        camera, depth = self.rig.camera, self.rig.depth_cam

        def start(directory: str):
            # As the fixed camera records, and with the rig's depth camera for occlusion.
            return recording.start_recording(camera, directory, args.record_hz, fov=args.fov,
                                             run_id=run_id, depth_camera=depth,
                                             illumination=session.Illumination,
                                             render_set=session.RenderSet)

        def window_open() -> str | None:
            opens = session.WindowOpensAtSeconds
            if session.RenderedTimeSeconds < opens - 1e-6:
                return f"the capture window opens at t={opens:g} s"
            return None

        return SpanRecorder(
            args.record_dir, recording.camera_name(camera), args.record_hz,
            start_recording=start, stop_recording=recording.stop_recording,
            view_readiness=lambda: recording.get_view_readiness(camera),
            may_record=window_open,
            on_closed=lambda handle, _directory: report_captures(handle))

    def follow(self, session) -> None:
        """Register the flown camera with the session, so a render set that follows the cameras
        follows its view from the next step on. Its depth camera shares its pose and view, so it is
        not registered as well."""
        session.AddCamera(self.rig.camera.id)
        self.session = session

    def close(self) -> None:
        """Take down what was made. Each part is tried whatever the ones before it did.

        The session stops following the camera first: a camera destroyed while still registered is
        left out of every step after, and counted, rather than followed.
        """
        if self.session is not None and self.rig is not None:
            try:
                self.session.RemoveCamera(self.rig.camera.id)
            except Exception as failure:
                logger.error("could not stop the session following the free view's camera: %r",
                             failure)
        for part, take_down in ((self.recorder, "close"), (self.view, "close"),
                                (self.rig, "cleanup")):
            if part is None:
                continue
            try:
                getattr(part, take_down)()
            except Exception as failure:
                logger.error("could not take down the free view's %s: %r",
                             type(part).__name__, failure)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    logging.basicConfig(level=logging.INFO, format="%(message)s", stream=sys.stdout)
    for label, path in (("scenario", args.scenario), ("world package", args.world_package),
                        ("catalogue", args.catalogue)):
        if not os.path.isfile(path):
            logger.error("no %s at %s", label, path)
            return 2
    # Refused before anything starts rather than when the camera is spawned, after the prewarm. A
    # name a live camera holds is the server's to refuse, at the spawn.
    if args.camera_name is not None:
        refused = carla.camera_name_problem(args.camera_name)
        if refused is not None:
            logger.error("--camera-name: %s", refused)
            return 2

    sun = SunDeclaration(args)
    sumo = SessionSumo(args.sumo_home)
    sumo.announce(args.sumo_gui)
    if args.view == "free":
        warn_of_an_uncovered_world(args)

    client = carla.Client(args.host, args.port)
    client.set_timeout(RUN_TIMEOUT_S)
    world = client.get_world()
    logger.info("server %s, map %s", client.get_server_version(), world.get_map().name)

    camera = None
    depth = None
    session = None
    recorder = None
    free = FreeViewParts()
    run_id = f"run-{datetime.now(UTC):%Y%m%d-%H%M%S}"
    aim = RenderedVehicleCentre()
    progress = PacingProgress()
    footprints = CameraFootprints()
    aims_at_traffic = args.view == "fixed" and args.camera_aim == "traffic" and not args.no_record

    try:
        client.set_timeout(max(args.setup_timeout, RUN_TIMEOUT_S))
        session = world.start_sumo_drive(
            args.scenario, args.world_package, args.catalogue,
            fixed_delta=args.fixed_delta,
            record_hz=args.record_hz,
            warm_up_to=args.warm_up,
            window_opens_at=args.window_opens_at,
            step_length=args.step_length,
            road_layer_visible=args.show_road_mesh,
            signal_layer_visible=args.show_signals,
            epoch=sun.epoch,
            illumination=sun.illumination,
            real_time_factor=args.real_time_factor,
            sumo_home=sumo.home,
            allow_sumo_version_mismatch=args.allow_sumo_version_mismatch,
            sumo_gui=args.sumo_gui,
            allow_teleporting=args.allow_teleporting,
            accept_skipped_dry_run=args.accept_skipped_dry_run,
            sumo_answer_timeout_s=args.sumo_answer_timeout,
            vehicle_lamps=not args.no_vehicle_lamps,
            headlight_on_below_deg=args.headlight_on_below,
            headlight_off_above_deg=args.headlight_off_above,
            draw_distance_m=args.draw_distance,
            render_set=args.render_set,
            region_centre=(args.region_x, args.region_y),
            region_radius_m=args.region_radius,
            region_hysteresis_m=args.region_hysteresis,
            capacity=args.capacity,
            render_min_pixels=args.render_min_pixels,
            render_admit_lead_s=args.render_admit_lead,
            render_release_lag_s=args.render_release_lag,
            world_truth_track=args.world_truth_track,
            world_truth_track_interval_s=args.world_truth_track_interval,
            run_manifest=args.run_manifest,
            run_manifest_header={"run_id": run_id, "tool": "run_sumo_drive.py",
                                 "view": args.view} if args.run_manifest else None,
            # What is printed about collisions, and nothing else: the session keeps every one either
            # way, and a line per collision is bound only where every collision is to be printed.
            collision_detail=args.collision_detail,
            on_collision=print_collision if args.collision_detail else None,
            # Bound only where the aim needs it: the session hands out a pose per rendered
            # vehicle per tick, and a callback that spends the whole run declining them is a
            # crossing into Python per vehicle per tick for nothing. No divergence callback for
            # the same reason: the report keeps the worst divergence and names its vehicle and
            # tick, and a crossing per vehicle per tick is a wait on the interpreter inside the
            # tick loop whenever another thread of this process -- a free view's window -- holds it.
            on_pose=aim.collect if aims_at_traffic else None)
        client.set_timeout(RUN_TIMEOUT_S)
        if session is None:
            return 1

        # Whether the server holds the drive lease for this run. Without it nothing on the server
        # stops a traffic manager or a second drive putting vehicles into the capture; the run report
        # says so, and so does the console, which an operator reads first.
        if session.Report.DriveLeaseRefused is not None:
            logger.warning("drive lease NOT HELD: this server has no drive lease, so nothing on it "
                           "stops another traffic system driving vehicles into this run (the server "
                           "said: %s)", session.Report.DriveLeaseRefused)
        else:
            logger.info("drive lease: held as %s", session.Report.DriveLeaseHolder)

        # What the session launched -- which installation, and which of its binaries -- and how that
        # binary stood against the world's converter, as the session established it. An accepted
        # mismatch and an unchecked world both ran, and both are said louder than a match.
        launched = session.Report.Sumo
        (logger.info if launched.Agrees else logger.warning)(
            "sumo: %s, launching %s; %s", launched.Installation, launched.Binary, launched.Verdict)
        # A compiled scenario was checked against its lock before SUMO started; an uncompiled one
        # ran with nothing to check it against, and says so louder.
        compiled = session.Report.CompileLock
        (logger.info if compiled.Compiled else logger.warning)("compile lock: %s", compiled)
        if compiled.Compiled:
            logger.info("routed by: %s", compiled.RoutedByText)
            # A skipped run the drive accepted is said louder: the compile never checked that every
            # planned vehicle enters.
            (logger.warning if compiled.SkippedDryRunAccepted else logger.info)(
                "dry run: %s", compiled.DryRunText)
        teleporting = session.Report.Teleporting
        (logger.warning if teleporting.Enabled else logger.info)("teleporting: %s", teleporting)
        logger.info("clock: %s", session.Clock)
        # The pace as the session declared it, not as this script asked for it: the session read
        # the factor once and is the one thing that holds the run to it.
        logger.info("pace: %s", PacingProgress.declared(session.Report.Pacing))
        logger.info("sun: %s", session.Sun if session.Sun is not None
                    else "left as the world holds it; the run's lighting honours no epoch")
        logger.info("layers: %s", ", ".join(
            f"{layer} {'drawn' if session.Report.LayerVisibility[layer] else 'hidden'}"
            for layer in session.Report.LayerVisibility.Keys))
        logger.info("render set: %s", describe_render_set(session.Report))
        logger.info("draw distance: %s", describe_draw_distance(session.Report.DrawDistanceMetres))
        logger.info("world truth track: %s", describe_world_truth_track(session.WorldTruthTrack))
        logger.info("run manifest: %s", session.RunManifest.Path if session.RunManifest is not None
                    else "none written; give --run-manifest to write one")

        steps = 0
        # Everything up to the recorder starting happens with the world already in synchronous mode:
        # the session takes the clock before the camera exists, so every image the camera delivers is
        # of a tick the session issued.
        if args.view == "free":
            # The window is up for the whole drive, the prewarm included: nothing is recorded until
            # F is pressed, and F records nothing before the capture window opens.
            logger.info("run id: %s", run_id)
            try:
                free.open(client, world, session, args, run_id)
            except RuntimeError as failure:
                # A window that cannot open -- no display on Linux, most often -- ends the run
                # before its first step, with everything given back on the way out.
                logger.error("%s", failure)
                return 1
        elif not args.no_record:
            centre = world_centre(world)
            if aims_at_traffic:
                # One step, to see where the bodies actually went. Bought rather than assumed, and
                # it costs the run its first SUMO step.
                aim.open = True
                session.Advance()
                aim.open = False
                steps += 1
                if aim.centre() is None:
                    logger.warning("nothing was rendered on the first step, so the camera is aimed "
                                   "at the centre of the world instead")
                else:
                    centre = aim.centre()
                    logger.info("aimed at %d rendered vehicles", aim.count)
            camera = spawn_camera(world, args, centre)
            depth = spawn_depth_camera(world, args, camera, depth_range_m())
            if session.RunManifest is not None:
                session.RunManifest.PlaceSensor(world.camera_name(camera), camera.id,
                                                world.camera_exposure(camera))
            if args.render_set == "cameras":
                # From the next step a render set that follows the cameras follows this one's view.
                session.AddCamera(camera.id)

            # The prewarm is ticked with the camera in the world, so its tiles stream, and recorded by
            # nothing: the window's first capture is the frame at the instant it opens.
            prewarm_steps = 0
            while session.RenderedTimeSeconds < session.WindowOpensAtSeconds - 1e-6:
                if not session.Advance():
                    logger.error("the scenario ended at t=%.2f s, before the window opened at "
                                 "t=%.2f s", session.RenderedTimeSeconds,
                                 session.WindowOpensAtSeconds)
                    return 1
                prewarm_steps += 1
            if prewarm_steps:
                steps += prewarm_steps
                logger.info("prewarmed %d SUMO steps; the window opens at t=%.2f s",
                            prewarm_steps, session.WindowOpensAtSeconds)

            os.makedirs(args.record_dir, exist_ok=True)
            # Every capture carries the declaration of its own frame's sun and the audit's residual
            # on the tick that rendered it, so a still's illumination is traceable to what this run
            # said it should be. And it lists the bodies its own frame rendered, each by the SUMO
            # vehicle it rendered, rather than every vehicle actor: the bodies parked between loans
            # stand below the ground and are no vehicle anyone could see. Each vehicle's occlusion
            # is measured against the depth camera attached to the camera.
            recorder = world.start_recording(camera, args.record_dir, args.record_hz, fov=args.fov,
                                             depth_camera=depth,
                                             illumination=session.Illumination,
                                             render_set=session.RenderSet)
            if recorder is None:
                return 1
            logger.info("recording %s -> %s", recorder.Name, args.record_dir)

        started = time.time()
        try:
            while session.Advance():
                steps += 1
                footprints.after_step(session)
                if args.steps and steps >= args.steps:
                    break
                progress.after_step(session, steps)
                if free.closed:
                    logger.info("the free view was closed at t=%.1f s; the drive ends there",
                                session.RenderedTimeSeconds)
                    break
        except CoSimSessionRefusedException as refused:
            # SUMO, the server or the sun failed part-way, and both sides stopped together. The report
            # says where, and which frame was the last whose truth holds.
            logger.error("\nthe run stopped at %s (%s): %s", refused.StageName, refused.CauseName,
                         refused.Message)
            logger.info("%s", session.Report)
            return 1

        elapsed = time.time() - started
        pacing = session.Report.Pacing
        logger.info("\n%d SUMO steps in %.1f s wall clock; pace %s: %s\n", steps, elapsed,
                    PacingProgress.declared(pacing), PacingProgress.achieved(pacing))
        logger.info("%s", session.Report)
        track = session.WorldTruthTrack
        if track is not None:
            # Its summary is written as the session ends, below, saying why the track ended.
            logger.info("world truth track  %d rows over %d samples -> %s", track.Rows, track.Samples,
                        track.Path)
        worst = session.Report.WorstDivergence
        if worst is not None and worst.PositionMetres > 0.0:
            logger.info("\nworst divergence %.6f m on %s at tick %d",
                        worst.PositionMetres, worst.VehicleId, worst.TickIndex)
        if session.Report.PosesComputed == 0:
            # The likeliest reason by far, and the one that produces a run that looks healthy and
            # renders an empty road: the scenario's vTypes name no blueprint the catalogue has
            # measured, so every vehicle is simulated and none is rendered.
            logger.error("\nNOTHING WAS RENDERED. The refused-type counts above say why; the usual "
                         "cause is vTypes with no carla:blueprint parameter, which are simulated and "
                         "never given a body.")
        return 0
    except CoSimSessionRefusedException as refused:
        # Refused before the first tick -- at the start, or on a prewarm step -- with everything the
        # session took given back. A world another drive holds gets its own line: the holder is who
        # to stop, and one SUMO drive is the only traffic system a world admits.
        holder = getattr(refused, "HeldBy", None)
        if holder is not None:
            logger.error("\nrefused: %s holds this world's drive lease. Stop that drive, or run "
                         "against a different world; where the drive is gone and did not release "
                         "the lease, world.break_drive_lease() ends it.", holder)
        logger.error("the session refused at %s: %s", refused.StageName, refused.Message)
        return 1
    finally:
        # Order matters on the way out: stop tapping the cameras, take them out of the world, then
        # let the session give back the bodies, the population lease and the world's clock. The
        # session restores the settings it found whatever happened above, which is what keeps an
        # editor from being left waiting for a tick from a process that has stopped.
        free.close()
        try:
            world.stop_recording()
        except Exception as failure:
            logger.error("could not stop the recorder: %r", failure)
        if recorder is not None:
            report_captures(recorder)
        if depth is not None:
            # The depth camera leaves the world before the camera it is attached to.
            try:
                depth.destroy()
            except Exception as failure:
                logger.error("could not destroy the depth camera: %r", failure)
        if camera is not None:
            try:
                # A session following the camera stops before the camera leaves the world.
                if session is not None and args.render_set == "cameras":
                    session.RemoveCamera(camera.id)
                camera.destroy()
            except Exception as failure:
                logger.error("could not destroy the camera: %r", failure)
        if session is not None:
            # Disposal attempts every step of the shutdown whatever the ones before it did, and
            # reports the ones that failed together. Logging them is all a harness can do, and it
            # is more than losing them.
            try:
                session.Dispose()
            except Exception as failure:
                logger.error("the session did not shut down cleanly: %r", failure)
