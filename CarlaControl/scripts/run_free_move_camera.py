#!/usr/bin/env python3
"""Fly a camera of your own around a running CARLA world, live, without changing anything in it.

run_SCTMV.py's view on its own: the same Unreal-style flight controls and heads-up display, and none
of its world controls. It never advances the world's clock, never changes the world's settings, sun,
weather, layers or map, starts no traffic manager, records nothing, and spawns nothing but its own
two cameras -- the picture, and the depth camera Ctrl+click measures with -- which it removes when it
closes. So it can be started before, during or after a drive (run_sumo_drive.py) and flown anywhere
while the drive owns the clock: frames arrive at the drive's tick rate while it runs, and at the
server's own rate when nothing does.

A drive renders vehicles only inside its region (its --region-x, --region-y and --region-radius), so
fly there to see traffic.

Controls (hold RIGHT MOUSE to fly):
    RMB + mouse   look around
    Ctrl + LMB    measure the lat/lon/elevation of a world point
    W/S A/D E/Q   forward/back, strafe, up/down
    Mouse wheel   adjust move speed
    Shift         move faster (x3)
    B / M         perimeter / margin overlay, drawn in this window only
    Space         back to the start pose
    Esc           quit, as does closing the window or Ctrl+C in the terminal

Positions are in CARLA's frame: metres, x east, y SOUTH (north is -y). The start altitude is in feet
above the world's origin, as run_SCTMV.py takes it, so a start pose copies across between the two.

    python run_free_move_camera.py --x 500 --y -1300 --z 1200
"""
import argparse
import logging
import os
import signal
import sys
from pathlib import Path

_THIS = Path(__file__).resolve().parent
_REPO = _THIS.parent.parent
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

# pygame prints a banner on import unless told not to; the log is this script's only output.
os.environ.setdefault("PYGAME_HIDE_SUPPORT_PROMPT", "1")

import carlanet as carla  # noqa: E402  (needs the path above)

from carlacontrol.PygameInterface import PygameInterface  # noqa: E402
from carlacontrol.PyGameSensorController import PyGameSensorController  # noqa: E402
from carlacontrol.SensorRig import SensorRig  # noqa: E402

logger = logging.getLogger("run_free_move_camera")


def parse_args() -> argparse.Namespace:
    """The command line. The camera's arguments mean what run_SCTMV.py's of the same name do."""
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    server = parser.add_argument_group("server")
    server.add_argument("--host", default="127.0.0.1", help="CARLA server host")
    server.add_argument("--port", type=int, default=2000, metavar="PORT", help="CARLA RPC port")
    server.add_argument("--timeout", type=float, default=20.0, metavar="SECONDS",
                        help="how long one server call may take")
    camera = parser.add_argument_group("camera")
    camera.add_argument("--x", type=float, default=0.0, help="start x, CARLA metres (east)")
    camera.add_argument("--y", type=float, default=0.0, help="start y, CARLA metres (-y is north)")
    camera.add_argument("--z", type=float, default=1000.0,
                        help="start altitude in FEET above the world's origin (default 1000)")
    camera.add_argument("--fov", type=float, default=90.0, help="horizontal field of view, degrees")
    camera.add_argument("--ev", type=float, default=0.0,
                        help="camera exposure compensation (EV); above 0 brightens")
    camera.add_argument("--speed", type=float, default=60.0, help="initial move speed, m/s")
    camera.add_argument("--width", type=int, default=1280)
    camera.add_argument("--height", type=int, default=720)
    args = parser.parse_args()
    # The rig delivers frames as they arrive rather than on ticks of its own, because this viewer
    # never ticks; the heads-up display reads a sun rate it never sets.
    args.asynchronous = True
    args.time_rate = 1.0
    return args


def main() -> int:
    args = parse_args()
    logging.basicConfig(level=logging.INFO,
                        format="%(asctime)s %(levelname)s %(name)s: %(message)s")

    client = carla.Client(args.host, args.port)
    client.set_timeout(args.timeout)
    world = client.get_world()
    logger.info("server %s, map %s", client.get_server_version(), world.get_map().name)

    rig = SensorRig(world=world, args=args, client=client)
    controller = PyGameSensorController(rig, world, rig.get_initial_pose(), speed=args.speed)
    window = PygameInterface(
        args=args,
        world=world,
        window_title="Free-move camera",
        sync=False,
        sensors=rig,
        controller=controller,
        read_only=True,
    )

    # A clean stop on Ctrl+C even while blocked inside a .NET call, which can swallow the interrupt.
    stop = {"flag": False}
    try:
        signal.signal(signal.SIGINT, lambda *_: stop.__setitem__("flag", True))
    except ValueError as failure:
        logger.warning("Ctrl+C will not stop this cleanly: %r", failure)

    controller.start_async()
    try:
        while window.running and not stop["flag"]:
            if window.process_events():
                break
            controller.apply_transform_async()
            window.render()
    except KeyboardInterrupt:
        pass
    finally:
        # The mover first, so nothing moves a camera while it is being removed.
        controller.stop_async()
        rig.cleanup()
        window.quit()
        logger.info("cameras removed; the world is as it was found")
    return 0


if __name__ == "__main__":
    sys.exit(main())
