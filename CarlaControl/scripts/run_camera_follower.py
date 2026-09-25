#!/usr/bin/env python3
"""Watch a running CARLA world through one camera of your own, live, without driving anything.

The camera either stares -- one fixed pose for as long as it runs -- or orbits a centre with its
view held on that centre. Its picture is shown in a window with the camera's name, its pattern, and
the frame number and simulated time stamped on each frame.

This script never advances the world's clock, never changes the world's settings, sun, weather,
layers or map, never starts a traffic manager, and never spawns or removes anything but its own
camera, which it removes when it closes. It can be started before, during or after a drive, and
keeps showing whatever the world delivers: frames at the drive's tick rate while a drive owns the
clock, frames at the server's own rate when nothing does. If no frame arrives for a few seconds it
says so once in the log, rather than looking frozen.

It records nothing. A recorder in a separate process would read an empty annotation registry and
write every vehicle as unlabelled (08_Collection_And_EPoL.md section 3.4). Recording from a viewer
like this waits until the annotation state that section rules must be published to the server
actually is; nothing publishes it yet. Record from the process that drives the world.

The three steps of watching SUMO drive a world, each its own script:

  1. build and load the world:       python run_SCTMV.py ... (as always)
  2. place a camera and watch it:    python run_camera_follower.py ...  (this script)
  3. drive the traffic from SUMO:    python ../../CarlaNet/python/run_sumo_drive.py ...

Positions are in CARLA's frame: metres, x east, y SOUTH (north is -y), z up. run_SCTMV.py's
heads-up display prints north as N = -y and height in feet above the ellipsoid, so convert before
copying a position from it. Angles follow CARLA: yaw 0 faces east and -90 faces north, and a
negative pitch looks down.

Stare at a point -- the camera stands back from the point opposite the bearing it looks along:

    python run_camera_follower.py --stare-look-at 120 -340 --stare-altitude-m 300 \\
        --stare-standoff-m 400 --stare-bearing-deg 0

Stare from a pose found by flying there:

    python run_camera_follower.py --stare-pose 120 60 300 -37 -90

Orbit a centre:

    python run_camera_follower.py --pattern orbit --orbit-centre 120 -340 \\
        --orbit-radius-m 250 --orbit-altitude-m 400 --orbit-period-s 180

Keys: Esc or Q quits, as does closing the window or pressing Ctrl+C in the terminal.
"""

import argparse
import logging
import os
import sys
from pathlib import Path

_THIS = Path(__file__).resolve().parent
_REPO = _THIS.parent.parent
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))
# pygame prints a banner on import unless told not to; the log is this script's only output.
os.environ.setdefault("PYGAME_HIDE_SUPPORT_PROMPT", "1")

import carlanet as carla  # noqa: E402  (needs the path above)

from carlacontrol.CameraFollower import CameraFollower  # noqa: E402
from carlacontrol.ChannelDescription import ChannelDescription  # noqa: E402

DEFAULT = ChannelDescription.default_of


def parse_args() -> argparse.Namespace:
    """The command line. Every channel default shown here is read from ChannelDescription."""
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)

    server = parser.add_argument_group("server")
    server.add_argument("--host", default="127.0.0.1",
                        help="CARLA server address (default 127.0.0.1)")
    server.add_argument("--port", type=int, default=2000, metavar="PORT",
                        help="CARLA server port (default 2000)")
    server.add_argument("--timeout", type=float, default=10.0, metavar="SECONDS",
                        help="seconds to wait for the server to answer a request (default 10)")

    camera = parser.add_argument_group("camera")
    camera.add_argument("--sensor-id", metavar="NAME",
                        help="a name for this camera, shown over its picture "
                             "(letters, digits and _ . : -, up to 63; default: none)")
    camera.add_argument("--pattern", choices=ChannelDescription.PATTERNS,
                        help=f"'stare' holds one pose; 'orbit' circles a centre "
                             f"(default {DEFAULT('pattern')})")
    camera.add_argument("--fov", type=float, metavar="DEGREES",
                        help=f"horizontal field of view, degrees (default {DEFAULT('fov')})")
    camera.add_argument("--width", type=int, metavar="PIXELS",
                        help=f"picture width, pixels (default {DEFAULT('width')})")
    camera.add_argument("--height", type=int, metavar="PIXELS",
                        help=f"picture height, pixels (default {DEFAULT('height')})")

    stare = parser.add_argument_group(
        "stare", "Give either a point to look at, or a full pose -- not both.")
    stare.add_argument("--stare-look-at", type=float, nargs=2, metavar=("X", "Y"),
                       help="the point the camera looks at, metres")
    stare.add_argument("--stare-look-at-z-m", type=float, metavar="METRES",
                       help=f"the height of that point, metres "
                            f"(default {DEFAULT('stare_look_at_z_m')})")
    stare.add_argument("--stare-altitude-m", type=float, metavar="METRES",
                       help=f"how far above the point the camera sits, metres "
                            f"(default {DEFAULT('stare_altitude_m')})")
    stare.add_argument("--stare-standoff-m", type=float, metavar="METRES",
                       help=f"how far back from the point the camera stands, measured along the "
                            f"ground, metres; 0 looks straight down "
                            f"(default {DEFAULT('stare_standoff_m')})")
    stare.add_argument("--stare-bearing-deg", type=float, metavar="DEGREES",
                       help=f"the compass direction the camera looks along, degrees clockwise from "
                            f"north (default {DEFAULT('stare_bearing_deg')}, looking north)")
    stare.add_argument("--stare-pose", type=float, nargs=5,
                       metavar=("X", "Y", "Z", "PITCH", "YAW"),
                       help="an exact camera pose instead of a point to look at: metres, then "
                            "degrees")

    orbit = parser.add_argument_group("orbit")
    orbit.add_argument("--orbit-centre", type=float, nargs=2, metavar=("X", "Y"),
                       help="the point the orbit circles and looks at, metres "
                            "(required for an orbit)")
    orbit.add_argument("--orbit-centre-z-m", type=float, metavar="METRES",
                       help=f"the height the orbit's altitude is measured from, metres "
                            f"(default {DEFAULT('orbit_centre_z_m')})")
    orbit.add_argument("--orbit-radius-m", type=float, metavar="METRES",
                       help=f"radius of the circle, metres (default {DEFAULT('orbit_radius_m')}). "
                            f"Metres, where run_SCTMV.py's --orbit-radius is feet")
    orbit.add_argument("--orbit-altitude-m", type=float, metavar="METRES",
                       help=f"height of the camera above the centre, metres "
                            f"(default {DEFAULT('orbit_altitude_m')})")
    orbit.add_argument("--orbit-period-s", type=float, metavar="SECONDS",
                       help=f"seconds per revolution (default {DEFAULT('orbit_period_s')})")
    return parser.parse_args()


def channel_from(args: argparse.Namespace) -> ChannelDescription:
    """The channel the command line describes. Only what was given is passed, so every value not
    given takes ChannelDescription's own default."""
    given = {name: getattr(args, name) for name in ChannelDescription.field_names()
             if getattr(args, name, None) is not None}
    if args.stare_look_at is not None:
        given["stare_look_at_x_m"], given["stare_look_at_y_m"] = args.stare_look_at
    if args.stare_pose is not None:
        (given["stare_x_m"], given["stare_y_m"], given["stare_z_m"], given["stare_pitch_deg"],
         given["stare_yaw_deg"]) = args.stare_pose
    if args.orbit_centre is not None:
        given["orbit_centre_x_m"], given["orbit_centre_y_m"] = args.orbit_centre
    return ChannelDescription(**given)


def main() -> int:
    args = parse_args()
    logging.basicConfig(level=logging.INFO, datefmt="%H:%M:%S",
                        format="%(asctime)s %(levelname)s %(name)s: %(message)s")
    try:
        channel = channel_from(args)
    except ValueError as refusal:
        logging.getLogger(__name__).error(
            "%s (--help names the flag that sets each of these)", refusal)
        return 2
    client = carla.Client(args.host, args.port)
    client.set_timeout(args.timeout)
    return CameraFollower(client, channel).run()


if __name__ == "__main__":
    sys.exit(main())
