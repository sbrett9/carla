#!/usr/bin/env python3
"""Drive a CARLA world's vehicles from a SUMO microsimulation and record what a camera sees.

SUMO decides where every vehicle is; CARLA renders them and the native recorder writes frames. The
session owns the advance of simulated time on both sides once it is recording -- nothing here ticks
the world while the session is advancing it, and no traffic manager runs beside it.

What a run needs on disk:

  * a CARLA server with the world already built and loaded, built with `--emit-world-package` so
    there is a `.cwp` carrying the ground surface the poses are seated on and the road network they
    are interpolated along;
  * the `.sumocfg` of a scenario whose network is **that** package's network -- a scenario compiled
    against the package (CarlaControl/scripts/compile_scenario.py), which writes the package's own
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

Usage:

    python run_sumo_drive.py \\
        --scenario <scenario dir>/<scenario_id>.sumocfg \\
        --world-package <build dir>/Gardnerville_Centerville_Lane.cwp \\
        --catalogue ../../CarlaControl/catalogue/vehicles.catalogue.json \\
        --epoch gardnerville.epoch.json --illumination freeze_at_window_start \\
        --record-dir ../../Build/captures/gardnerville

Pass `--no-record` to drive the world without spawning a camera, which is the shortest way to see
whether the vehicles are where SUMO says they are.

By default the world ticks as fast as the machine allows, so traffic moves at whatever pace the
machine manages. To watch it at the pace of real traffic from another process that holds the view,
hold the ticks to the wall clock and run until the scenario ends:

    python run_sumo_drive.py --scenario ... --world-package ... --epoch ... \\
        --illumination freeze_at_window_start --no-record --real-time-factor 1.0 --steps 0

The session measures the pace it actually held and this prints it once per pacing window; nothing
stops a run that falls behind, and the final report says by how much it did. The session refuses a
world package that does not describe the world the server has loaded, so a package from another
build of the world cannot put the traffic somewhere the world is not.

Which SUMO runs is named, not left to the environment. The session launches the installation this
script names: `--sumo-home` where given, otherwise `CARLANET_SUMO_HOME`, otherwise this repository's
pinned build (`Build/sumo-install`, then `Build/sumo-src`); only where none of those holds a `sumo`
does the session search `SUMO_HOME` and then PATH itself. The session then compares that
installation's release against the converter the world package records, by release number --
`Eclipse SUMO netconvert 1.27.0` and `1.27.0` are the same release -- and refuses a different one
before SUMO is started, naming both. `--allow-sumo-version-mismatch` runs anyway, and the report
records that it did; a package that records no converter runs, and is reported unchecked. The
installation, its release and how it stood against the world's converter are logged every run.

`--sumo-gui` launches that installation's `sumo-gui` in place of `sumo`, so SUMO's own window shows
the very simulation the session is stepping, beside the CARLA world -- one SUMO process, not a second
one. The release pin holds for it by its own release, and the log names the binary that ran. An
installation with no `sumo-gui` is refused before anything starts; CarlaSetup.ps1 or CarlaSetup.sh
builds and stages it. To watch a drive at the pace of real traffic:

    python run_sumo_drive.py --scenario ... --world-package ... --epoch ... \\
        --illumination freeze_at_window_start --no-record --real-time-factor 1.0 --steps 0 --sumo-gui

The window is live: pausing it pauses the drive, and a pause longer than `--sumo-answer-timeout`
stops the run as a SUMO that stopped answering; closing it stops the run as a SUMO that died. On
Linux it needs a display.

A compiled scenario is checked against the compile lock the compiler wrote beside its configuration
(`<stem>.lock.json`) before SUMO is started: its configuration, route file and network must be the
ones the lock digests, and the catalogue and epoch given here the ones it was compiled against. A
scenario with no lock runs and is logged as uncompiled. A scenario whose configuration lets SUMO
teleport a blocked vehicle -- a positive `time-to-teleport`, or none, which SUMO takes as 300 s -- is
refused unless `--allow-teleporting` is given, and one that sets `ignore-route-errors` is refused
outright, because SUMO then keeps a vehicle it cannot route standing at the end of an edge and says
nothing.

If either side fails part-way -- SUMO dies, closes the connection or does not answer within
`--sumo-answer-timeout`, the server drops the connection or leaves a tick unanswered, or the sun
disagrees with its declaration or goes away -- both stop together: the script logs the stage and the
cause, prints the report with the last frame whose truth holds, gives back everything it can reach and
exits 1. A collision does not stop the run; the report counts them, with the vehicles SUMO gave up
inserting and SUMO's own warnings.

One thing happens between the session starting and the recorder starting: the camera is aimed at the
vehicles rather than at the middle of the rendered region, because a corridor scenario puts its
traffic nowhere near that middle.

The first frames of a run whose camera looks somewhere Cesium has not streamed yet carry imagery
that is still arriving -- an unattended capture has nobody watching the view fill in, where an
operator flying the camera does. What that costs, and why the number of ticks it takes is not a
caller's to supply, is in
`Docs/CAT_Research/Plans/SUMO_Behavioral_Capture/03_CoSimulation_Runtime.md` section 9.5.1.
"""
import argparse
import json
import logging
import math
import os
import sys
import time

import carlanet as carla

# isort: split
# The .NET namespaces exist only once carlanet has loaded their assemblies, so this import has to
# follow it; an import sorter would put it first and break the script at start.
from CarlaNet.CoSim import CoSimSessionRefusedException

_THIS = os.path.dirname(os.path.abspath(__file__))
_REPO = os.path.normpath(os.path.join(_THIS, "..", ".."))

# How long any one server call may take once the session is driving.
RUN_TIMEOUT_S = 30.0

logger = logging.getLogger("run_sumo_drive")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
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
    parser.add_argument("--catalogue",
                        default=os.path.join(_REPO, "CarlaControl", "catalogue",
                                             "vehicles.catalogue.json"),
                        help="the measured vehicle catalogue")
    parser.add_argument("--sumo-home",
                        help="the SUMO installation to launch: the directory holding bin/sumo. "
                             "Default: CARLANET_SUMO_HOME if it holds one, else this repository's "
                             "pinned build (Build/sumo-install, then Build/sumo-src). SUMO_HOME is "
                             "consulted only when none of those holds a sumo")
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
                        help="run a scenario whose configuration lets SUMO teleport a blocked vehicle "
                             "(a positive time-to-teleport, or none, which SUMO takes as 300 s) "
                             "instead of refusing it. The run report records that it was accepted")
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

    parser.add_argument("--region-x", type=float, default=0.0,
                        help="centre of the rendered region, SUMO easting in metres")
    parser.add_argument("--region-y", type=float, default=0.0,
                        help="centre of the rendered region, SUMO northing in metres")
    parser.add_argument("--region-radius", type=float, default=400.0,
                        help="inside this a vehicle is rendered")
    parser.add_argument("--region-hysteresis", type=float, default=60.0,
                        help="how much further out it is released, and subscribed again")
    parser.add_argument("--capacity", type=int, default=128,
                        help="how many vehicles may be rendered at once")
    parser.add_argument("--maximum-bodies", type=int, default=192,
                        help="how many CARLA actors the session may own")

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

    parser.add_argument("--no-record", action="store_true",
                        help="drive the world without spawning a camera or writing frames")
    parser.add_argument("--record-dir", default=os.path.join(_REPO, "Build", "captures"))
    parser.add_argument("--record-hz", type=float, default=2.0)
    parser.add_argument("--camera-z", type=float, default=300.0,
                        help="camera height above the region centre, metres")
    parser.add_argument("--camera-standoff", type=float, default=300.0,
                        help="camera distance back from the region centre, metres")
    parser.add_argument("--camera-yaw", type=float, default=0.0,
                        help="bearing the camera stands off along, degrees clockwise from north")
    parser.add_argument("--camera-aim", choices=("traffic", "region-centre"), default="traffic",
                        help="what the camera looks at: 'traffic' the mean position of the vehicles "
                             "rendered on the first step, 'region-centre' the middle of the "
                             "rendered region. The middle of a corridor scenario's region is "
                             "usually not where its traffic is")
    parser.add_argument("--width", type=int, default=1920)
    parser.add_argument("--height", type=int, default=1080)
    parser.add_argument("--fov", type=float, default=60.0)
    return parser.parse_args()


def region_centre(args: argparse.Namespace) -> tuple[float, float]:
    """The middle of the rendered region, in CARLA's frame.

    The region is given in SUMO's projected frame and the camera lives in CARLA's, which is the same
    frame with the northing negated -- the one conversion this script performs, and the same sign
    the bridge applies to every vehicle.
    """
    return args.region_x, -args.region_y


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
    REPOSITORY_BUILDS = (
        ("this repository's staged build", os.path.join(_REPO, "Build", "sumo-install")),
        ("this repository's source build", os.path.join(_REPO, "Build", "sumo-src")),
    )

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
            logger.info("SUMO: none named -- no --sumo-home, %s holds none and this repository has "
                        "no build under Build/ -- so the session searches SUMO_HOME, then PATH, and "
                        "launches %s", self.OVERRIDE_VARIABLE, binary)
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

    The middle of the rendered region is a poor thing to point a camera at. On a corridor scenario
    the region is drawn around a road that runs through it, so its middle is whatever that road
    happens to pass: a run aimed there framed a builder's yard while the traffic was on a highway a
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

    def after_step(self, session, steps: int, worst_metres: float) -> None:
        """Log a line if a pacing window closed during the step just taken."""
        pacing = session.Report.Pacing
        if pacing.CompletedWindows == self.windows_seen:
            return
        self.windows_seen = pacing.CompletedWindows
        # The velocity figure is the report's own, live: how far the velocity the world reports for
        # a body -- the one the truth telemetry reads -- has been from the velocity it was given.
        logger.info("  %d steps, t=%.1f s, %d rendered, pace %s, worst divergence %.4f m, "
                    "%.4f m/s", steps, session.RenderedTimeSeconds,
                    session.RenderedVehicleIds.Count, self.achieved(pacing), worst_metres,
                    session.Report.WorstVelocityDivergenceMetresPerSecond)
        admission = session.Report.LastAdmissionPass
        if admission is not None:
            logger.info("  sumo population %d, eligible %d, admitted %d, shed %d, cap %d",
                        admission.Population, admission.Eligible, admission.Admitted,
                        admission.Shed, admission.Capacity)


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
    camera = world.spawn_actor(blueprint, transform)
    logger.info("camera %s at (%.1f, %.1f, %.1f), pitch %.1f, yaw %.1f, looking at (%.1f, %.1f)",
                camera.id, transform.location.x, transform.location.y, transform.location.z,
                transform.rotation.pitch, transform.rotation.yaw, centre[0], centre[1])
    return camera


def main() -> int:
    args = parse_args()
    logging.basicConfig(level=logging.INFO, format="%(message)s", stream=sys.stdout)
    for label, path in (("scenario", args.scenario), ("world package", args.world_package),
                        ("catalogue", args.catalogue)):
        if not os.path.isfile(path):
            logger.error("no %s at %s", label, path)
            return 2

    sun = SunDeclaration(args)
    sumo = SessionSumo(args.sumo_home)
    sumo.announce(args.sumo_gui)

    client = carla.Client(args.host, args.port)
    client.set_timeout(RUN_TIMEOUT_S)
    world = client.get_world()
    logger.info("server %s, map %s", client.get_server_version(), world.get_map().name)

    camera = None
    session = None
    recorder = None
    aim = RenderedVehicleCentre()
    progress = PacingProgress()
    aims_at_traffic = args.camera_aim == "traffic" and not args.no_record
    worst = {"metres": 0.0, "vehicle": "", "tick": 0}

    def on_divergence(divergence):
        # Kept as one number rather than a list: a capture run produces one of these per vehicle per
        # tick, and the summary a run needs is on the report anyway.
        if divergence.PositionMetres > worst["metres"]:
            worst["metres"] = divergence.PositionMetres
            worst["vehicle"] = divergence.VehicleId
            worst["tick"] = divergence.TickIndex

    try:
        client.set_timeout(max(args.setup_timeout, RUN_TIMEOUT_S))
        session = world.start_sumo_drive(
            args.scenario, args.world_package, args.catalogue,
            region_centre=(args.region_x, args.region_y),
            admit_radius_m=args.region_radius,
            hysteresis_m=args.region_hysteresis,
            capacity=args.capacity,
            maximum_bodies=args.maximum_bodies,
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
            sumo_answer_timeout_s=args.sumo_answer_timeout,
            vehicle_lamps=not args.no_vehicle_lamps,
            headlight_on_below_deg=args.headlight_on_below,
            headlight_off_above_deg=args.headlight_off_above,
            # Bound only where the aim needs it: the session hands out a pose per rendered
            # vehicle per tick, and a callback that spends the whole run declining them is a
            # crossing into Python per vehicle per tick for nothing.
            on_pose=aim.collect if aims_at_traffic else None,
            on_divergence=on_divergence)
        client.set_timeout(RUN_TIMEOUT_S)
        if session is None:
            return 1

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

        steps = 0
        # Everything up to the recorder starting happens with the world already in synchronous mode:
        # the session takes the clock before the camera exists, so every image the camera delivers is
        # of a tick the session issued.
        if not args.no_record:
            centre = region_centre(args)
            if aims_at_traffic:
                # One step, to see where the bodies actually went. Bought rather than assumed, and
                # it costs the run its first SUMO step.
                aim.open = True
                session.Advance()
                aim.open = False
                steps += 1
                if aim.centre() is None:
                    logger.warning("nothing was rendered on the first step, so the camera is aimed "
                                   "at the region centre instead")
                else:
                    centre = aim.centre()
                    logger.info("aimed at %d rendered vehicles", aim.count)
            camera = spawn_camera(world, args, centre)

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
            # said it should be.
            recorder = world.start_recording(camera, args.record_dir, args.record_hz, fov=args.fov,
                                             illumination=session.Illumination)
            if recorder is None:
                return 1
            logger.info("recording -> %s", args.record_dir)

        started = time.time()
        try:
            while session.Advance():
                steps += 1
                if args.steps and steps >= args.steps:
                    break
                progress.after_step(session, steps, worst["metres"])
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
        if worst["vehicle"]:
            logger.info("\nworst divergence %.6f m on %s at tick %d",
                        worst["metres"], worst["vehicle"], worst["tick"])
        if session.Report.PosesComputed == 0:
            # The likeliest reason by far, and the one that produces a run that looks healthy and
            # renders an empty road: the scenario's vTypes name no blueprint the catalogue has
            # measured, so every vehicle is simulated and none is rendered.
            logger.error("\nNOTHING WAS RENDERED. The refused-type counts above say why; the usual "
                         "cause is vTypes with no carla:blueprint parameter, which are simulated and "
                         "never given a body.")
        return 0
    finally:
        # Order matters on the way out: stop tapping the camera, take the camera out of the world,
        # then let the session give back the bodies, the population lease and the world's clock. The
        # session restores the settings it found whatever happened above, which is what keeps an
        # editor from being left waiting for a tick from a process that has stopped.
        try:
            world.stop_recording()
        except Exception as failure:
            logger.error("could not stop the recorder: %r", failure)
        if recorder is not None:
            # Read once the recorder has flushed, so the counts are the run's and not a moment's.
            logger.info("captures           %s written, %s dropped; %s carry their frame's "
                        "illumination declaration, %s do not", recorder.Saved, recorder.Dropped,
                        recorder.IlluminationPaired, recorder.IlluminationUnpaired)
        if camera is not None:
            try:
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


if __name__ == "__main__":
    sys.exit(main())
