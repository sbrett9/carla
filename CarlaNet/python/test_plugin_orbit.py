#!/usr/bin/env python3
"""The server flies an orbiting camera from parameters sent once; the client sends no pose per frame.

Issue #37, promoted into the capture plan on 2026-10-05 (13_Work_Breakdown.md stage K). The orbit used
to be a client thread pushing a pose about fifty times a second on the wall clock; measured, the
server's tick rate on a loaded world fell from 41.6 to 20.7 per second with it running, and under a
synchronous drive the camera turned as far per captured frame as the pace was high. The plugin's
UOrbitMoverComponent flies it now (set_orbit), advancing the angle by each tick's delta on the
simulation clock in TG_PrePhysics, before the sensors capture and the world observer reports the frame.

What it does, on the generated world the server has loaded (the recorder needs its georeference):

  1. takes the world's clock (synchronous at 0.05 s), spawns one RGB camera at the circle's opening
     pose and a depth camera attached to it, rigidly at its own pose, and records the camera with the
     depth camera, so the recorder's occlusion pairing runs its depth pose check on every capture;
  2. gives the server the circle once (`Sensor.set_orbit`) and ticks the world through one full
     revolution, counting every client call that could move a camera -- `Actor.set_transform`,
     `Client.apply_batch` and `apply_batch_sync` -- and reading the camera's pose in the client's
     snapshot of every frame;
  3. pauses the orbit for a second of ticks, resumes it, turns it off, moves the camera by hand, and
     asks the server where the orbit stands;
  4. optionally, under `--tick-rate-seconds N`, hands the world back to free running and counts the
     server's frames over N seconds with the orbit paused and again with it running.

What it measures, against the issue's acceptance list:

  * calls: with the orbit running the client sent no call on the orbit's behalf -- the three counters
    read zero over the whole revolution;
  * pose: the camera's pose in every frame's snapshot is the shared pose rule's at the angle the
    simulation clock predicts, start angle plus 2 pi / period times the simulated seconds since the
    orbit was enabled, within one tick's step; so the orbit covers 2 pi in `period` simulated
    seconds, whatever the wall clock did;
  * the depth camera stands where the camera stands in every snapshot, and the recorder reports zero
    captures skipped for the cameras being at different poses (OcclusionDepthWrongPose) over the
    revolution;
  * pause holds the angle and the pose, resume advances again, and once the orbit is off a manual move
    stays where it was put;
  * tick rate, where asked: the server's frame rate with the orbit paused and running, printed, not
    judged -- the remaining gap, if any, is tile streaming from a moving view.

Run it on its own, not beside a drive: it ticks the world. It gives the world's settings back and
destroys its cameras on every path out, and removes its captures unless told to keep them.

Prereqs: a generated world loaded on the server built with the orbit mover, and the carlanet wheel
built from this tree. A server without the mover is reported as such and the check fails.

Usage:
    python test_plugin_orbit.py [--host h] [--port p] [--x 0] [--y 0] [--altitude 500]
                                [--radius 200] [--period 60] [--tick-rate-seconds 0] [--keep]
"""
import argparse
import logging
import math
import shutil
import sys
import tempfile
import time
from pathlib import Path

import carlanet as carla

# isort: split
# The .NET namespaces exist only once carlanet has loaded their assemblies.
from System import UInt32

DELTA_S = 0.05
RECORD_HZ = 2.0
TWO_PI = 2.0 * math.pi
# A snapshot holds the transform as the server encodes it, in single precision, in metres.
METRES_TOLERANCE = 0.02
ANGLE_TOLERANCE_DEG = 0.02
# How many ticks the pause and the manual move are each held for.
HOLD_TICKS = 20


class PluginOrbitCheck:
    """Flies a camera on the server's orbit under its own ticks and reads what the server did."""

    def __init__(self, client, logger: logging.Logger, args: argparse.Namespace) -> None:
        self.client = client
        self.world = client.get_world()
        self.logger = logger
        self.args = args
        self.failures: list[str] = []
        self.camera = None
        self.depth = None
        self.calls = {"set_transform": 0, "apply_batch": 0, "apply_batch_sync": 0}
        self.centre = (args.x, args.y, 0.0)

    def check(self, passed: bool, what: str) -> None:
        self.logger.info("%-100s %s", what, "ok" if passed else "FAIL")
        if not passed:
            self.failures.append(what)

    # -- the pose rule, the client's and the server's --------------------------------------------------

    def angle_after(self, elapsed_s: float) -> float:
        return (TWO_PI / self.args.period * elapsed_s) % TWO_PI

    def pose_at(self, angle: float) -> tuple:
        cx, cy, cz = self.centre
        x = cx + self.args.radius * math.cos(angle)
        y = cy + self.args.radius * math.sin(angle)
        z = cz + self.args.altitude
        dx, dy, dz = cx - x, cy - y, cz - z
        pitch = math.degrees(math.atan2(dz, math.hypot(dx, dy)))
        yaw = math.degrees(math.atan2(dy, dx))
        return (x, y, z, pitch, yaw, 0.0)

    @staticmethod
    def angle_apart(a: float, b: float) -> float:
        return abs((a - b + 180.0) % 360.0 - 180.0)

    def same_pose(self, held: tuple, expected: tuple) -> bool:
        return (math.dist(held[:3], expected[:3]) <= METRES_TOLERANCE
                and self.angle_apart(held[3], expected[3]) <= ANGLE_TOLERANCE_DEG
                and self.angle_apart(held[4], expected[4]) <= ANGLE_TOLERANCE_DEG)

    @staticmethod
    def pose_of(transform) -> tuple:
        location, rotation = transform.Location, transform.Rotation
        return (float(location.X), float(location.Y), float(location.Z),
                float(rotation.Pitch), float(rotation.Yaw), float(rotation.Roll))

    def snapshot_pose(self, frame: int, actor_id: int) -> tuple | None:
        # The client serves a frame it holds exactly, or nothing (never a neighbouring frame).
        actors = self.world._client.GetSnapshotFrame(frame)
        if actors is None:
            return None
        found, state = actors.TryGetValue(UInt32(actor_id), None)
        return self.pose_of(state.Transform) if found and state is not None else None

    # -- counting the client's calls --------------------------------------------------------------------

    def count_calls(self):
        """Wrap every shim call that could move a camera, so a call sent on the orbit's behalf is
        counted. Restored by the returned callable."""
        originals = (carla.Actor.set_transform, carla.Client.apply_batch, carla.Client.apply_batch_sync)
        counters = self.calls

        def counting(name, original):
            def wrapped(*args, **kwargs):
                counters[name] += 1
                return original(*args, **kwargs)
            return wrapped

        carla.Actor.set_transform = counting("set_transform", originals[0])
        carla.Client.apply_batch = counting("apply_batch", originals[1])
        carla.Client.apply_batch_sync = counting("apply_batch_sync", originals[2])

        def restore() -> None:
            carla.Actor.set_transform, carla.Client.apply_batch, carla.Client.apply_batch_sync = originals

        return restore

    # -- the revolution ---------------------------------------------------------------------------------

    def fly_one_revolution(self) -> dict:
        """Enable the orbit and tick through one period, reading every frame's snapshot."""
        ticks = int(round(self.args.period / DELTA_S))
        enabled_at = float(self.world.get_sim_time())
        self.camera.set_orbit(carla.Location(x=self.centre[0], y=self.centre[1], z=self.centre[2]),
                              self.args.radius, self.args.altitude, self.args.period)
        poses: dict[int, tuple] = {}
        depth_poses: dict[int, tuple] = {}
        times: dict[int, float] = {}
        for _ in range(ticks):
            frame = int(self.world.tick())
            times[frame] = float(self.world.get_sim_time())
            held = self.snapshot_pose(frame, self.camera.id)
            if held is not None:
                poses[frame] = held
            depth = self.snapshot_pose(frame, self.depth.id)
            if depth is not None:
                depth_poses[frame] = depth
        return {"enabled_at": enabled_at, "poses": poses, "depth_poses": depth_poses, "times": times,
                "ticks": ticks}

    def report_revolution(self, flown: dict) -> None:
        poses, times = flown["poses"], flown["times"]
        self.check(len(poses) >= flown["ticks"] - 2,
                   f"snapshot: the camera was held in the client's snapshot of every frame "
                   f"({len(poses)} of {flown['ticks']})")
        on_rule = []
        a_tick_behind = []
        off = []
        for frame, held in sorted(poses.items()):
            elapsed = times[frame] - flown["enabled_at"]
            expected = self.pose_at(self.angle_after(elapsed))
            behind = self.pose_at(self.angle_after(elapsed - DELTA_S))
            if self.same_pose(held, expected):
                on_rule.append(frame)
            elif self.same_pose(held, behind):
                a_tick_behind.append(frame)
            else:
                off.append((frame, held, expected))
        self.check(not off,
                   f"pose: every snapshot holds the camera at the shared pose rule's pose for the "
                   f"angle the simulation clock predicts, within one tick's step ({len(on_rule)} on the "
                   f"predicted angle, {len(a_tick_behind)} one tick behind it, {len(off)} elsewhere)")
        for frame, held, expected in off[:5]:
            self.logger.error("  frame %d: held (%.2f, %.2f, %.2f, p %.2f, y %.2f), predicted "
                              "(%.2f, %.2f, %.2f, p %.2f, y %.2f)", frame, *held[:5], *expected[:5])
        # One period of simulated time is one revolution: the last frame's angle is the start's.
        last = max(poses)
        elapsed = times[last] - flown["enabled_at"]
        self.check(abs(elapsed - self.args.period) <= DELTA_S + 1e-6,
                   f"clock: {flown['ticks']} ticks of {DELTA_S} s advanced the simulation clock by "
                   f"the period ({elapsed:.3f} s of {self.args.period} s)")
        self.check(self.same_pose(poses[last], self.pose_at(0.0))
                   or self.same_pose(poses[last], self.pose_at(self.angle_after(-DELTA_S))),
                   "period: after one period of simulated time the camera is back at its opening "
                   "pose, within one tick's step")
        # The depth camera rode the camera.
        agreed = [f for f in poses if f in flown["depth_poses"]
                  and self.same_pose(flown["depth_poses"][f], poses[f])]
        self.check(len(agreed) == len(poses),
                   f"depth: the attached depth camera stands where the camera stands in every "
                   f"snapshot ({len(agreed)} of {len(poses)})")
        self.check(all(count == 0 for count in self.calls.values()),
                   f"calls: the client sent no call on the orbit's behalf over the revolution "
                   f"(set_transform {self.calls['set_transform']}, apply_batch "
                   f"{self.calls['apply_batch']}, apply_batch_sync {self.calls['apply_batch_sync']})")
        state = self.camera.get_orbit_state()
        predicted = self.angle_after(elapsed)
        apart = abs((state.angle - predicted + math.pi) % TWO_PI - math.pi)
        self.check(state.enabled and not state.paused and apart <= TWO_PI / self.args.period * DELTA_S + 1e-6,
                   f"state: the server's angle is the predicted one within a tick's step "
                   f"({state.angle:.4f} against {predicted:.4f} rad, enabled {state.enabled}, "
                   f"paused {state.paused})")

    # -- pause, resume, off, a manual move ---------------------------------------------------------------

    def report_pause_and_release(self) -> None:
        self.camera.set_orbit_paused(True)
        before = self.camera.get_orbit_state().angle
        frames = [int(self.world.tick()) for _ in range(HOLD_TICKS)]
        held = [self.snapshot_pose(f, self.camera.id) for f in frames]
        held = [h for h in held if h is not None]
        after = self.camera.get_orbit_state()
        self.check(after.paused and after.angle == before and held
                   and all(self.same_pose(h, held[0]) for h in held),
                   f"pause: the angle and the pose hold through {HOLD_TICKS} ticks "
                   f"({before:.4f} rad)")

        self.camera.set_orbit_paused(False)
        for _ in range(HOLD_TICKS):
            frame = int(self.world.tick())
        resumed = self.camera.get_orbit_state()
        advanced = (resumed.angle - before) % TWO_PI
        expected = TWO_PI / self.args.period * HOLD_TICKS * DELTA_S
        self.check(not resumed.paused and abs(advanced - expected) <= TWO_PI / self.args.period * DELTA_S + 1e-6,
                   f"resume: the angle advances again by the ticks' delta ({advanced:.4f} rad over "
                   f"{HOLD_TICKS} ticks, expected {expected:.4f})")

        self.camera.set_orbit_enabled(False)
        off = self.camera.get_orbit_state()
        by_hand = carla.Transform(carla.Location(x=self.centre[0], y=self.centre[1],
                                                 z=self.centre[2] + self.args.altitude),
                                  carla.Rotation(pitch=-90.0, yaw=0.0, roll=0.0))
        self.camera.set_transform(by_hand)
        held = []
        for _ in range(HOLD_TICKS):
            frame = int(self.world.tick())
            pose = self.snapshot_pose(frame, self.camera.id)
            if pose is not None:
                held.append(pose)
        expected = (by_hand.location.x, by_hand.location.y, by_hand.location.z, -90.0, 0.0, 0.0)
        self.check(not off.enabled and held
                   and all(math.dist(h[:3], expected[:3]) <= METRES_TOLERANCE
                           and self.angle_apart(h[3], -90.0) <= ANGLE_TOLERANCE_DEG for h in held),
                   f"off: once the orbit is off a manual move stays where it was put through "
                   f"{HOLD_TICKS} ticks (enabled {off.enabled})")

    # -- the recorder -----------------------------------------------------------------------------------

    def report_recorder(self, recorder) -> None:
        measured = int(recorder.OcclusionMeasured)
        wrong_pose = getattr(recorder, "OcclusionDepthWrongPose", None)
        if wrong_pose is None:
            self.check(False, "recorder: the wheel publishes the depth pose counters (it predates "
                              "them; rebuild it)")
            return
        self.logger.info("recorder: %d captures, %d with occlusion measured, %d unmatched (%d no "
                         "depth capture, %d out of step, %d at a different pose)", recorder.Saved,
                         measured, recorder.OcclusionUnmatched, recorder.OcclusionNoDepthCaptures,
                         recorder.OcclusionDepthOutOfStep, wrong_pose)
        # Occlusion is measured per vehicle, so a world with no vehicle in it gives the recorder nothing
        # to measure: the pairing itself is what this check holds, below. With vehicles present, at
        # least one capture must carry a measurement.
        vehicles = len(self.world.get_actors().filter("vehicle.*"))
        if vehicles == 0:
            self.logger.info("recorder: no vehicle in the world, so no occlusion to measure")
        else:
            self.check(measured > 0, f"recorder: occlusion was measured on the orbit ({measured} captures, "
                                     f"{vehicles} vehicles in the world)")
        self.check(int(wrong_pose) == 0,
                   f"recorder: zero captures skipped for the cameras being at different poses "
                   f"({int(wrong_pose)})")

    # -- the server's tick rate, free running ------------------------------------------------------------

    def report_tick_rate(self) -> None:
        seconds = self.args.tick_rate_seconds
        if seconds <= 0:
            return
        settings = self.world.get_settings()
        settings.synchronous_mode = False
        settings.fixed_delta_seconds = None
        self.world.apply_settings(settings)
        net = self.world._client

        def frames_over(label: str) -> float:
            start_frame, start = int(net.LatestObservedFrame), time.monotonic()
            time.sleep(seconds)
            rate = (int(net.LatestObservedFrame) - start_frame) / (time.monotonic() - start)
            self.logger.info("tick rate: %.1f frames/s with the orbit %s, free running, over %d s",
                             rate, label, seconds)
            return rate

        self.camera.set_orbit(carla.Location(x=self.centre[0], y=self.centre[1], z=self.centre[2]),
                              self.args.radius, self.args.altitude, self.args.period)
        self.camera.set_orbit_paused(True)
        frames_over("paused")
        self.camera.set_orbit_paused(False)
        frames_over("running")
        self.camera.set_orbit_enabled(False)

    # -- the run ---------------------------------------------------------------------------------------

    def run(self) -> int:
        original = self.world.get_settings()
        directory = Path(tempfile.mkdtemp(prefix="plugin-orbit-"))
        restore_calls = None
        recorder = None
        try:
            settings = self.world.get_settings()
            settings.synchronous_mode = True
            settings.fixed_delta_seconds = DELTA_S
            self.world.apply_settings(settings)
            self.world.tick()

            library = self.world.get_blueprint_library()
            rgb = library.find("sensor.camera.rgb")
            rgb.set_attribute("image_size_x", "640")
            rgb.set_attribute("image_size_y", "360")
            rgb.set_attribute("fov", "90")
            rgb.set_attribute("sensor_tick", str(1.0 / RECORD_HZ))
            opening = self.pose_at(0.0)
            self.camera = self.world.spawn_camera(
                rgb, carla.Transform(carla.Location(x=opening[0], y=opening[1], z=opening[2]),
                                     carla.Rotation(pitch=opening[3], yaw=opening[4], roll=0.0)),
                name="PluginOrbitCheck")
            depth = library.find("sensor.camera.depth")
            depth.set_attribute("image_size_x", "640")
            depth.set_attribute("image_size_y", "360")
            depth.set_attribute("fov", "90")
            depth.set_attribute("sensor_tick", str(1.0 / RECORD_HZ))
            if depth.has_attribute("max_range"):
                depth.set_attribute("max_range", "20000.0")
            self.depth = self.world.spawn_actor(depth, carla.Transform(), attach_to=self.camera,
                                                attachment_type=carla.AttachmentType.Rigid)
            self.world.tick()

            try:
                self.camera.get_orbit_state()
            except carla.OrbitNotOnServerError as refused:
                self.check(False, f"server: it flies orbits ({refused})")
                return 1

            recorder = self.world.start_recording(self.camera, str(directory), RECORD_HZ, fov=90.0,
                                                  depth_camera=self.depth)
            self.check(recorder is not None, "the native recorder started with the depth camera")
            if recorder is None:
                return 1

            restore_calls = self.count_calls()
            flown = self.fly_one_revolution()
            restore_calls()
            restore_calls = None
            self.report_revolution(flown)
            self.report_pause_and_release()
            # A few more ticks so the last images reach the recorder.
            for _ in range(10):
                self.world.tick()
            self.world.stop_recording()
            self.report_recorder(recorder)
            self.report_tick_rate()
        finally:
            if restore_calls is not None:
                restore_calls()
            try:
                self.world.stop_recording()
            except Exception as failure:  # noqa: BLE001 - cleanup reports and continues
                self.logger.warning("could not stop the recorder: %r", failure)
            for actor, what in ((self.depth, "depth camera"), (self.camera, "camera")):
                if actor is None:
                    continue
                try:
                    actor.destroy()
                except Exception as failure:  # noqa: BLE001 - cleanup reports and continues
                    self.logger.warning("could not destroy the %s: %r", what, failure)
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
    parser.add_argument("--x", type=float, default=0.0, help="the circle's centre, CARLA x, metres")
    parser.add_argument("--y", type=float, default=0.0, help="the circle's centre, CARLA y, metres")
    parser.add_argument("--altitude", type=float, default=500.0,
                        help="the camera's height above the centre, metres")
    parser.add_argument("--radius", type=float, default=200.0, help="the circle's radius, metres")
    parser.add_argument("--period", type=float, default=60.0,
                        help="simulated seconds per revolution; one revolution is flown")
    parser.add_argument("--tick-rate-seconds", type=float, default=0.0,
                        help="also count the server's free-running frames over this many seconds "
                             "with the orbit paused and with it running; 0 skips it")
    parser.add_argument("--keep", action="store_true", help="keep the captures and say where")
    args = parser.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(message)s")
    client = carla.Client(args.host, args.port)
    client.set_timeout(30.0)
    return PluginOrbitCheck(client, logging.getLogger("test_plugin_orbit"), args).run()


if __name__ == "__main__":
    sys.exit(main())
