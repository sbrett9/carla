#!/usr/bin/env python3
"""A moving camera's captures record the pose each image was rendered from, not where it went next.

The recorder derives a capture's platform pose -- the sidecar's air-track point and its sensor
azimuth and elevation -- from the transform the server stamps into the image's sensor header, in the
same game-thread call that captures the frame. A camera flown by hand is moved between ticks while
its images reach the recorder several ticks after their frames, so a pose read from the camera
actor at that moment would be a later pose than the image's. Plan:
Docs/CAT_Research/Plans/SUMO_Behavioral_Capture/12_Operator_Control_Surface.md section 9.6.

What it does, on the generated world the server has loaded (the recorder needs its georeference):

  1. takes the world's clock (synchronous at 0.05 s) and spawns one RGB camera;
  2. before every tick, moves the camera to a new pose on a circle -- about 100 m and tens of
     degrees from the last, so neighbouring poses cannot be mistaken for each other -- and keeps
     which pose was commanded for which frame;
  3. records it at 10 Hz, then reads every sidecar back and checks that its platform point and
     boresight are the pose commanded for that capture's own frame, and not the pose commanded for
     any of the frames after it, where the camera had been moved to by the time the recorder
     handled the image.

Run it on its own, not beside a drive: it ticks the world. It gives the world's settings back and
destroys its camera on every path out, and removes its captures unless told to keep them.

Prereqs: a generated world loaded on the server, and the carlanet wheel built from this tree.

Usage:
    python test_moving_camera_pose.py [--host h] [--port p] [--x 0] [--y 0] [--altitude 400]
                                      [--ticks 60] [--keep]
"""
import argparse
import logging
import math
import shutil
import sys
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path

import carlanet as carla

# isort: split
# The .NET namespaces exist only once carlanet has loaded their assemblies.
from CarlaNet.Types.Geom import Geodesy, GeoLocation

DELTA_S = 0.05
RECORD_HZ = 10.0
PLATFORM_UID = "CARLA-SENSOR-MOVING-POSE-CHECK"
# The sidecar writes lat/lon to seven decimals (about a centimetre) and angles to three.
DEGREES_TOLERANCE = 2e-7
ANGLE_TOLERANCE = 0.01
# Frames after a capture's own whose poses it must not carry.
LATER_FRAMES = 3


class MovingCameraPoseCheck:
    """Flies a camera on a scripted path under its own ticks, records it, and reads the sidecars."""

    def __init__(self, client, logger: logging.Logger, args: argparse.Namespace) -> None:
        self.world = client.get_world()
        self.logger = logger
        self.args = args
        self.failures: list[str] = []
        self.camera = None
        self.commanded: dict[int, carla.Transform] = {}

    def check(self, passed: bool, what: str) -> None:
        self.logger.info("%-86s %s", what, "ok" if passed else "FAIL")
        if not passed:
            self.failures.append(what)

    def pose(self, step: int) -> carla.Transform:
        """The pose commanded for the step'th tick: round a 300 m circle at 20 degrees a tick,
        turning and pitching by amounts that never repeat between neighbours."""
        angle = math.radians(20.0 * step)
        return carla.Transform(
            carla.Location(x=self.args.x + 300.0 * math.cos(angle),
                           y=self.args.y + 300.0 * math.sin(angle),
                           z=self.args.altitude),
            carla.Rotation(pitch=-30.0 - (step * 7) % 50, yaw=((step * 47) % 360) - 180.0,
                           roll=0.0))

    @staticmethod
    def azimuth(yaw_deg: float) -> float:
        """CARLA's yaw as a compass azimuth: +x is east and +y south, so yaw 0 is 90."""
        yaw = math.radians(yaw_deg)
        return math.degrees(math.atan2(math.cos(yaw), -math.sin(yaw))) % 360.0

    @staticmethod
    def angle_apart(a: float, b: float) -> float:
        return abs((a - b + 180.0) % 360.0 - 180.0)

    def matches(self, sidecar: dict, transform: carla.Transform, origin) -> bool:
        where = Geodesy.CarlaLocalToGeodetic(origin, float(transform.location.x),
                                             float(transform.location.y),
                                             float(transform.location.z))
        return (abs(sidecar["lat"] - where.Latitude) <= DEGREES_TOLERANCE
                and abs(sidecar["lon"] - where.Longitude) <= DEGREES_TOLERANCE
                and self.angle_apart(sidecar["azimuth"], self.azimuth(transform.rotation.yaw))
                <= ANGLE_TOLERANCE
                and abs(sidecar["elevation"] - transform.rotation.pitch) <= ANGLE_TOLERANCE)

    @staticmethod
    def read(directory: Path) -> list[dict]:
        captures = []
        for path in sorted(directory.glob("*.xml")):
            events = ET.parse(path).getroot()
            for event in events.iter("event"):
                if event.get("uid") != PLATFORM_UID:
                    continue
                point, sensor = event.find("point"), event.find("detail/sensor")
                captures.append({"file": path.name, "frame": int(events.get("tick")),
                                 "lat": float(point.get("lat")), "lon": float(point.get("lon")),
                                 "azimuth": float(sensor.get("azimuth")),
                                 "elevation": float(sensor.get("elevation"))})
        return captures

    def run(self) -> int:
        original = self.world.get_settings()
        directory = Path(tempfile.mkdtemp(prefix="moving-camera-pose-"))
        try:
            settings = self.world.get_settings()
            settings.synchronous_mode = True
            settings.fixed_delta_seconds = DELTA_S
            self.world.apply_settings(settings)
            self.world.tick()
            lat0, lon0, height0 = self.world.get_cesium_origin()
            origin = GeoLocation(lat0, lon0, height0)

            blueprint = self.world.get_blueprint_library().find("sensor.camera.rgb")
            blueprint.set_attribute("image_size_x", "640")
            blueprint.set_attribute("image_size_y", "360")
            blueprint.set_attribute("fov", "90")
            self.camera = self.world.spawn_actor(blueprint, self.pose(0))
            self.world.tick()
            recorder = self.world.start_recording(self.camera, str(directory), RECORD_HZ,
                                                  fov=90.0, platform_uid=PLATFORM_UID)
            self.check(recorder is not None, "the native recorder started")
            if recorder is None:
                return 1

            for step in range(1, self.args.ticks + 1):
                transform = self.pose(step)
                self.camera.set_transform(transform)
                self.commanded[int(self.world.tick())] = transform
            # A few more ticks at the last pose, so the last images reach the recorder.
            for _ in range(10):
                self.commanded[int(self.world.tick())] = transform
            self.world.stop_recording()
            self.logger.info("recorded %s captures, %s dropped, into %s", recorder.Saved,
                             recorder.Dropped, directory)

            captures = [c for c in self.read(directory) if c["frame"] in self.commanded]
            self.check(len(captures) >= self.args.ticks // 4,
                       f"at least {self.args.ticks // 4} captures of commanded frames "
                       f"({len(captures)})")
            own = [c for c in captures if self.matches(c, self.commanded[c["frame"]], origin)]
            self.check(len(own) == len(captures),
                       f"every capture carries the pose commanded for its own frame "
                       f"({len(own)} of {len(captures)})")
            for capture in captures:
                if capture not in own:
                    self.logger.error("  %s (frame %d): lat %.7f lon %.7f az %.3f el %.3f",
                                      capture["file"], capture["frame"], capture["lat"],
                                      capture["lon"], capture["azimuth"], capture["elevation"])
            later = [c for c in captures
                     if any(self.matches(c, self.commanded[c["frame"] + ahead], origin)
                            for ahead in range(1, LATER_FRAMES + 1)
                            if c["frame"] + ahead in self.commanded
                            and self.commanded[c["frame"] + ahead] is not self.commanded[c["frame"]])]
            self.check(not later, f"no capture carries a pose commanded for one of the {LATER_FRAMES} "
                                  f"frames after its own ({len(later)})")
        finally:
            try:
                self.world.stop_recording()
            except Exception as failure:  # noqa: BLE001 - cleanup reports and continues
                self.logger.warning("could not stop the recorder: %r", failure)
            if self.camera is not None:
                try:
                    self.camera.destroy()
                except Exception as failure:  # noqa: BLE001 - cleanup reports and continues
                    self.logger.warning("could not destroy the camera: %r", failure)
            self.world.apply_settings(original)
            if self.args.keep:
                self.logger.info("captures kept in %s", directory)
            else:
                shutil.rmtree(directory, ignore_errors=True)

        for failure in self.failures:
            self.logger.error("FAIL: %s", failure)
        if self.failures:
            return 1
        self.logger.info("PASS")
        return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=2000)
    parser.add_argument("--x", type=float, default=0.0, help="centre of the circle, CARLA x, metres")
    parser.add_argument("--y", type=float, default=0.0, help="centre of the circle, CARLA y, metres")
    parser.add_argument("--altitude", type=float, default=400.0, help="camera height, metres")
    parser.add_argument("--ticks", type=int, default=60, help="ticks the camera is moved on")
    parser.add_argument("--keep", action="store_true", help="keep the captures and say where")
    args = parser.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(message)s")
    client = carla.Client(args.host, args.port)
    client.set_timeout(30.0)
    return MovingCameraPoseCheck(client, logging.getLogger("test_moving_camera_pose"), args).run()


if __name__ == "__main__":
    sys.exit(main())
