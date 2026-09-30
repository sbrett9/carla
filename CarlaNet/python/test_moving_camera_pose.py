#!/usr/bin/env python3
"""A moving camera's captures record the pose each image was taken from, not where it went next.

The server wrote a camera image's sensor header when the image had been read back from the GPU, and
took the transform and clock in it from the camera as it stood then; only the frame number was put
back to the image's. Measured on Bahonar (2026-09-30) with the camera moved before every synchronous
tick, the pixels were their own frame's in 90 of 90 images and the client's snapshot of each frame
held the camera where it was commanded for that frame, but 89 of 90 headers carried the pose
commanded for the next frame, and this check found 29 of 34 captures carrying a later frame's pose.
Two fixes answer it, and this check proves each on its own:

  * the server stamps the header's frame, clock and transform in the game-thread call that captures
    the frame (ASensor::MakeCaptureHeader), so the header lines below pass once the plugin is rebuilt;
  * the recorder places each capture where the snapshot of the image's own frame holds the camera,
    checks the header against it and counts a disagreement (SensorPoseCheck), so the sidecar lines
    pass once the wheel is rebuilt, whatever the server does, and the recorder's disagreement count
    says what the server did.

Plan: Docs/CAT_Research/Plans/SUMO_Behavioral_Capture/12_Operator_Control_Surface.md section 9.6.

What it does, on the generated world the server has loaded (the recorder needs its georeference):

  1. takes the world's clock (synchronous at 0.05 s) and spawns one RGB camera;
  2. before every tick, moves the camera to a new pose on a circle -- about 100 m and tens of
     degrees from the last, so neighbouring poses cannot be mistaken for each other -- and keeps
     which pose was commanded for which frame, the camera's pose in the client's snapshot of that
     frame, and the frame's simulation time;
  3. reads every image's sensor header off a listener of its own, and records the camera at 10 Hz;
  4. reports, separately: the snapshots against the commanded poses (the control), the headers'
     poses and timestamps against their own frame's (the server), and every sidecar's platform
     point and boresight against the pose commanded for its own frame and for the frames after it,
     with the recorder's counts of where each capture's pose came from (the recorder).

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
from CarlaNet.Transport.Streaming import SensorFrame
from CarlaNet.Types.Geom import Geodesy, GeoLocation
from System import Action, UInt32

DELTA_S = 0.05
RECORD_HZ = 10.0
PLATFORM_UID = "CARLA-SENSOR-MOVING-POSE-CHECK"
# The sidecar writes lat/lon to seven decimals (about a centimetre) and angles to three.
DEGREES_TOLERANCE = 2e-7
ANGLE_TOLERANCE = 0.01
# A header or a snapshot holds the transform as the server encodes it, in single precision.
METRES_TOLERANCE = 0.01
# Two simulation times are the same frame's when they agree to far better than a tick.
SECONDS_TOLERANCE = 1e-6
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
        # frame -> (x, y, z, pitch, yaw, roll) of the camera in the client's snapshot of that frame.
        self.snapshot_pose: dict[int, tuple] = {}
        self.snapshot_time: dict[int, float] = {}
        self.not_held: list[int] = []
        # frame -> (timestamp, x, y, z, pitch, yaw, roll) from each image's sensor header.
        self.headers: dict[int, tuple] = {}

    def check(self, passed: bool, what: str) -> None:
        self.logger.info("%-100s %s", what, "ok" if passed else "FAIL")
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

    def same_pose(self, pose: tuple, transform: carla.Transform) -> bool:
        """Whether (x, y, z, pitch, yaw, roll) is `transform`, as a header or snapshot encodes it."""
        x, y, z, pitch, yaw, roll = pose
        location, rotation = transform.location, transform.rotation
        return (math.dist((x, y, z), (location.x, location.y, location.z)) <= METRES_TOLERANCE
                and self.angle_apart(pitch, rotation.pitch) <= ANGLE_TOLERANCE
                and self.angle_apart(yaw, rotation.yaw) <= ANGLE_TOLERANCE
                and self.angle_apart(roll, rotation.roll) <= ANGLE_TOLERANCE)

    @staticmethod
    def pose_of(transform) -> tuple:
        """A .NET Transform as plain numbers, so nothing .NET outlives the call that read it."""
        location, rotation = transform.Location, transform.Rotation
        return (float(location.X), float(location.Y), float(location.Z),
                float(rotation.Pitch), float(rotation.Yaw), float(rotation.Roll))

    def listen_to_headers(self):
        """Subscribe to the camera's stream and keep each image's header, and nothing else of it.

        The stream thread calls this for every image, so it copies six numbers and a timestamp and
        leaves the pixels alone.
        """
        def on_image(frame) -> None:
            self.headers[int(frame.Header.Frame)] = (float(frame.Header.Timestamp),
                                                     *self.pose_of(frame.SensorTransform))

        return self.world._client.SubscribeToStream(self.camera._actor.StreamToken,
                                                    Action[SensorFrame](on_image))

    def observe(self, frame: int) -> None:
        """Keep the camera's pose in the client's snapshot of `frame`, and the frame's simulation
        time, read as the tick returns: the frame has been observed by then."""
        net = self.world._client
        if int(net.LatestObservedFrame) == frame:
            self.snapshot_time[frame] = float(net.LatestElapsedSeconds)
        actors, served = net.GetSnapshotFrame(frame, 0)
        if actors is None or int(served) != frame:
            self.not_held.append(frame)
            return
        found, snapshot = actors.TryGetValue(UInt32(self.camera.id), None)
        if found and snapshot is not None:
            self.snapshot_pose[frame] = self.pose_of(snapshot.Transform)
        else:
            self.not_held.append(frame)

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

    def moved_after(self, frame: int) -> bool:
        """Whether the camera was commanded somewhere new for the frame after `frame`: only then
        can a pose stamped a frame late be told from the frame's own."""
        return (frame in self.commanded and frame + 1 in self.commanded
                and self.commanded[frame + 1] is not self.commanded[frame])

    def report_snapshots(self) -> None:
        """The control: the client's snapshot of each frame against the pose commanded for it."""
        held = sorted(self.snapshot_pose)
        own = [f for f in held if self.same_pose(self.snapshot_pose[f], self.commanded[f])]
        self.check(bool(held) and len(own) == len(held),
                   f"snapshot: the camera is at the pose commanded for the frame in every snapshot "
                   f"held ({len(own)} of {len(held)})")
        if self.not_held:
            self.logger.info("snapshot: %d frame(s) not held exactly when read: %s",
                             len(self.not_held), self.not_held[:10])

    def report_headers(self) -> None:
        """The server: every image header's pose and timestamp against its own frame's."""
        moving = sorted(f for f in self.headers if self.moved_after(f))
        own = [f for f in moving if self.same_pose(self.headers[f][1:], self.commanded[f])]
        late = [f for f in moving if self.same_pose(self.headers[f][1:], self.commanded[f + 1])]
        self.check(bool(moving) and len(own) == len(moving),
                   f"header: every image header carries the pose commanded for its own frame "
                   f"({len(own)} of {len(moving)}; {len(late)} carry the next frame's)")

        timed = sorted(f for f in self.headers if f in self.snapshot_time)
        on_time = [f for f in timed
                   if abs(self.headers[f][0] - self.snapshot_time[f]) <= SECONDS_TOLERANCE]
        a_tick_late = [f for f in timed if f + 1 in self.snapshot_time
                       and abs(self.headers[f][0] - self.snapshot_time[f + 1]) <= SECONDS_TOLERANCE]
        self.check(bool(timed) and len(on_time) == len(timed),
                   f"header: every image header's timestamp is its own frame's simulation time "
                   f"({len(on_time)} of {len(timed)}; {len(a_tick_late)} carry the next frame's)")

    def report_sidecars(self, captures: list[dict], origin, recorder) -> None:
        """The recorder: every sidecar's pose against its own frame's, and where each came from."""
        self.check(len(captures) >= self.args.ticks // 4,
                   f"sidecar: at least {self.args.ticks // 4} captures of commanded frames "
                   f"({len(captures)})")
        own = [c for c in captures if self.matches(c, self.commanded[c["frame"]], origin)]
        self.check(len(own) == len(captures),
                   f"sidecar: every capture carries the pose commanded for its own frame "
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
        self.check(not later, f"sidecar: no capture carries a pose commanded for one of the "
                              f"{LATER_FRAMES} frames after its own ({len(later)})")

        checks = getattr(recorder, "ChecksSensorPose", None)
        if checks is None:
            self.check(False, "recorder: the wheel publishes where each capture's pose came from "
                              "(it predates SensorPoseCheck; rebuild it)")
            return
        from_snapshot = int(recorder.SensorPoseFromSnapshot)
        disagreed = int(recorder.SensorPoseHeaderDisagreed)
        from_header = int(recorder.SensorPoseFromHeader)
        self.logger.info("recorder: %d capture(s) placed from their own frame's snapshot, %d of them "
                         "with a header that disagreed; %d placed from the header", from_snapshot,
                         disagreed, from_header)
        self.check(bool(checks) and from_snapshot > 0,
                   f"recorder: captures are placed from the snapshot of their own frame "
                   f"({from_snapshot} of {from_snapshot + from_header})")
        self.check(disagreed == 0,
                   f"recorder: no image header disagreed with its frame's snapshot ({disagreed})")

    def run(self) -> int:
        original = self.world.get_settings()
        directory = Path(tempfile.mkdtemp(prefix="moving-camera-pose-"))
        subscription = None
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
            subscription = self.listen_to_headers()
            self.world.tick()
            recorder = self.world.start_recording(self.camera, str(directory), RECORD_HZ,
                                                  fov=90.0, platform_uid=PLATFORM_UID)
            self.check(recorder is not None, "the native recorder started")
            if recorder is None:
                return 1

            for step in range(1, self.args.ticks + 1):
                transform = self.pose(step)
                self.camera.set_transform(transform)
                frame = int(self.world.tick())
                self.commanded[frame] = transform
                self.observe(frame)
            # A few more ticks at the last pose, so the last images reach the recorder.
            for _ in range(10):
                frame = int(self.world.tick())
                self.commanded[frame] = transform
                self.observe(frame)
            self.world.stop_recording()
            self.logger.info("recorded %s captures, %s dropped, into %s; %d image headers read",
                             recorder.Saved, recorder.Dropped, directory, len(self.headers))

            self.report_snapshots()
            self.report_headers()
            captures = [c for c in self.read(directory) if c["frame"] in self.commanded]
            self.report_sidecars(captures, origin, recorder)
        finally:
            if subscription is not None:
                try:
                    subscription.Dispose()
                except Exception as failure:  # noqa: BLE001 - cleanup reports and continues
                    self.logger.warning("could not stop reading the headers: %r", failure)
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
