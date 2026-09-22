#!/usr/bin/env python3
"""Drive a CARLA world's vehicles from a SUMO microsimulation and record what a camera sees.

SUMO decides where every vehicle is; CARLA renders them and the native recorder writes frames. The
session owns the advance of simulated time on both sides -- nothing here calls `world.tick()`, and
no traffic manager runs beside it.

What a run needs on disk:

  * a CARLA server with the world already built and loaded, built with `--emit-world-package` so
    there is a `.cwp` carrying the ground surface the poses are seated on and the road network they
    are interpolated along;
  * the `.sumocfg` of a scenario authored against **that** network;
  * the measured vehicle catalogue, and a `carla:blueprint` parameter on every vType that is meant
    to be rendered. A vType naming no measured blueprint is simulated and never rendered -- no body
    of another shape stands in for it, so a scenario whose types carry no such parameter produces an
    empty road and says so in the report's refused-type count.

Usage:

    python run_sumo_drive.py \\
        --scenario ../../Import/Gardnerville_Centerville_Lane_NeighborhoodOrbit.sumocfg \\
        --world-package <build dir>/Gardnerville_Centerville_Lane.cwp \\
        --catalogue ../../CarlaControl/catalogue/vehicles.catalogue.json \\
        --record-dir ../../Build/captures/gardnerville

Pass `--no-record` to drive the world without spawning a camera, which is the shortest way to see
whether the vehicles are where SUMO says they are.
"""
import argparse
import math
import os
import sys
import time

_THIS = os.path.dirname(os.path.abspath(__file__))
_REPO = os.path.normpath(os.path.join(_THIS, "..", ".."))
_INSTALL = os.path.join(_REPO, "Build", "sumo-install")
# The bridge launches SUMO itself and resolves the installation the same way the rest of the
# toolchain does: the repository's own pinned build first, then SUMO_HOME. Default the latter so a
# machine with no system-wide SUMO still runs against the build that converted the world.
os.environ.setdefault("SUMO_HOME", _INSTALL)

import carlanet as carla  # noqa: E402 -- SUMO_HOME must be set before the bridge resolves its toolchain


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=2000)
    parser.add_argument("--scenario", required=True, help="the scenario's .sumocfg")
    parser.add_argument("--world-package", required=True,
                        help="the .cwp the world was built as")
    parser.add_argument("--catalogue",
                        default=os.path.join(_REPO, "CarlaControl", "catalogue",
                                             "vehicles.catalogue.json"),
                        help="the measured vehicle catalogue")

    parser.add_argument("--steps", type=int, default=600,
                        help="SUMO steps to run, or 0 for the whole scenario")
    parser.add_argument("--warm-up", type=float, default=0.0,
                        help="simulated second to fast-forward SUMO to before the first tick")
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
    parser.add_argument("--width", type=int, default=1920)
    parser.add_argument("--height", type=int, default=1080)
    parser.add_argument("--fov", type=float, default=60.0)
    return parser.parse_args()


def camera_transform(args: argparse.Namespace) -> carla.Transform:
    """An oblique view of the rendered region, standing off along a bearing and looking back at it.

    The region centre is given in SUMO's projected frame and the camera in CARLA's, which is the
    same frame with the northing negated -- the one conversion this script performs, and the same
    sign the bridge applies to every vehicle.
    """
    centre_x = args.region_x
    centre_y = -args.region_y
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


def spawn_camera(world, args: argparse.Namespace):
    blueprint = world.get_blueprint_library().find("sensor.camera.rgb")
    blueprint.set_attribute("image_size_x", str(args.width))
    blueprint.set_attribute("image_size_y", str(args.height))
    if blueprint.has_attribute("fov"):
        blueprint.set_attribute("fov", str(args.fov))
    transform = camera_transform(args)
    camera = world.spawn_actor(blueprint, transform)
    print(f"camera {camera.id} at ({transform.location.x:.1f}, {transform.location.y:.1f}, "
          f"{transform.location.z:.1f}), pitch {transform.rotation.pitch:.1f}, "
          f"yaw {transform.rotation.yaw:.1f}")
    return camera


def main() -> int:
    args = parse_args()
    for label, path in (("scenario", args.scenario), ("world package", args.world_package),
                        ("catalogue", args.catalogue)):
        if not os.path.isfile(path):
            print(f"no {label} at {path}", file=sys.stderr)
            return 2

    client = carla.Client(args.host, args.port)
    client.set_timeout(30.0)
    world = client.get_world()
    print(f"server {client.get_server_version()}, map {world.get_map().name}")

    camera = None
    session = None
    worst = {"metres": 0.0, "vehicle": "", "tick": 0}

    def on_divergence(divergence):
        # Kept as one number rather than a list: a capture run produces one of these per vehicle per
        # tick, and the summary a run needs is on the report anyway.
        if divergence.PositionMetres > worst["metres"]:
            worst["metres"] = divergence.PositionMetres
            worst["vehicle"] = divergence.VehicleId
            worst["tick"] = divergence.TickIndex

    try:
        if not args.no_record:
            camera = spawn_camera(world, args)

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
            step_length=args.step_length,
            on_divergence=on_divergence)
        if session is None:
            return 1

        print(f"clock: {session.Clock}")

        # The recorder is started after the session has the world in synchronous mode, because a
        # camera in an asynchronous world delivers no frames at all.
        if camera is not None:
            os.makedirs(args.record_dir, exist_ok=True)
            if world.start_recording(camera, args.record_dir, args.record_hz,
                                     fov=args.fov) is None:
                return 1
            print(f"recording -> {args.record_dir}")

        started = time.time()
        steps = 0
        while session.Advance():
            steps += 1
            if args.steps and steps >= args.steps:
                break
            if steps % 200 == 0:
                print(f"  {steps} steps, {session.RenderedVehicleIds.Count} rendered, "
                      f"worst divergence {worst['metres']:.4f} m")

        elapsed = time.time() - started
        print(f"\n{steps} SUMO steps in {elapsed:.1f} s wall clock\n")
        print(session.Report)
        if worst["vehicle"]:
            print(f"\nworst divergence {worst['metres']:.6f} m on {worst['vehicle']} "
                  f"at tick {worst['tick']}")
        if session.Report.PosesComputed == 0:
            # The likeliest reason by far, and the one that produces a run that looks healthy and
            # renders an empty road: the scenario's vTypes name no blueprint the catalogue has
            # measured, so every vehicle is simulated and none is rendered.
            print("\nNOTHING WAS RENDERED. The refused-type counts above say why; the usual cause "
                  "is vTypes with no carla:blueprint parameter, which are simulated and never "
                  "given a body.", file=sys.stderr)
        return 0
    finally:
        # Order matters on the way out: stop tapping the camera, take the camera out of the world,
        # then let the session give back the bodies, the population lease and the world's clock. The
        # session restores the settings it found whatever happened above, which is what keeps an
        # editor from being left waiting for a tick from a process that has stopped.
        try:
            world.stop_recording()
        except Exception as failure:
            print(f"could not stop the recorder: {failure!r}", file=sys.stderr)
        if camera is not None:
            try:
                camera.destroy()
            except Exception as failure:
                print(f"could not destroy the camera: {failure!r}", file=sys.stderr)
        if session is not None:
            # Disposal attempts every step of the shutdown whatever the ones before it did, and
            # reports the ones that failed together. Printing them is all a harness can do, and it
            # is more than losing them.
            try:
                session.Dispose()
            except Exception as failure:
                print(f"the session did not shut down cleanly: {failure!r}", file=sys.stderr)


if __name__ == "__main__":
    sys.exit(main())
