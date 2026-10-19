#!/usr/bin/env python3
"""A vehicle moved by having its pose written reports the velocity it was given, not zero.

The co-simulation bridge moves the vehicles it renders by writing their transforms every tick with
physics and gravity off. With the root not simulating, a vehicle's velocity resolves to the Chaos
vehicle movement component's `Velocity`, which nothing on that path writes, so every such vehicle
read zero to everything that reads velocity: the world observer, the recorder, radar and the truth
telemetry. `set_target_velocity` on a vehicle whose physics is disabled now writes the fields the
getters read (plan doc 03 section 5, D3.5).

Velocity is read through `get_velocity`, which serves the world observer's snapshot -- the same
stream the truth telemetry reads.

What it does, on whatever map the server has loaded, synchronous at 0.05 s:

  kinematic vehicle, physics and gravity off:
    1. right after physics is disabled, it reads zero;
    2. moved by transforms alone for 20 ticks, it still reads zero -- the condition a bridge that
       sends no velocity produces, and the proof that this measurement can see it;
    3. moved for 100 ticks with its velocity set each tick, it reads the commanded velocity on every
       tick, and its position advances by that velocity;
    4. held for 10 ticks with transforms only, it keeps the last velocity; set to zero, it reads zero;
    5. switched to physics and back, it reads zero;
  control vehicle, physics on:
    6. driven on throttle for 100 ticks, its reported speed agrees with its speed from position.

Angular velocity and acceleration of the kinematic vehicle are logged, not asserted. Restores the
world's settings and destroys both vehicles on every path out.

Prereqs: a server built with the change, running with any map loaded that has spawn points.

Usage:
    python test_kinematic_velocity.py [--host h] [--port p] [--blueprint vehicle.lincoln.mkz]
"""
import argparse
import logging
import math
import sys
import time

import carlanet as carla

DELTA_S = 0.05
SPEED_MPS = 12.5
VELOCITY_TOLERANCE_MPS = 1e-3
CONTROL_TOLERANCE_MPS = 0.25


class KinematicVelocityCheck:
    """Drives one kinematic and one physics-simulated vehicle and compares what they report."""

    def __init__(self, client, blueprint_id: str, logger: logging.Logger) -> None:
        self.client = client
        self.world = client.get_world()
        self.inner = client._inner
        self.blueprint_id = blueprint_id
        self.logger = logger
        self.failures: list[str] = []

    def tick(self) -> None:
        """Advance one tick and make sure the observer holds that frame, so reads are of it.

        The tick cue already waits for its frame to be observed; this confirms it rather than
        relying on it, because every read below is of the observer's snapshot.
        """
        frame = self.world.tick()
        deadline = time.monotonic() + 2.0
        while self.inner.LatestObservedFrame < frame and time.monotonic() < deadline:
            time.sleep(0.002)

    def check(self, passed: bool, what: str) -> None:
        self.logger.info("%-72s %s", what, "ok" if passed else "FAIL")
        if not passed:
            self.failures.append(what)

    @staticmethod
    def speed(vector) -> float:
        return math.sqrt(vector.x * vector.x + vector.y * vector.y + vector.z * vector.z)

    @staticmethod
    def forward(transform) -> tuple[float, float]:
        yaw = math.radians(transform.rotation.yaw)
        return math.cos(yaw), math.sin(yaw)

    def spawn(self, point):
        blueprint = self.world.get_blueprint_library().find(self.blueprint_id)
        return self.world.spawn_actor(blueprint, point)

    def run_kinematic(self, vehicle, start) -> None:
        vehicle.set_simulate_physics(False)
        vehicle.set_enable_gravity(False)
        self.tick()
        self.check(self.speed(vehicle.get_velocity()) < VELOCITY_TOLERANCE_MPS,
                   "kinematic: reads zero right after physics is disabled")

        fx, fy = self.forward(start)
        step = SPEED_MPS * DELTA_S
        location = carla.Location(x=start.location.x, y=start.location.y, z=start.location.z)

        def advance(set_velocity: bool) -> None:
            location.x += fx * step
            location.y += fy * step
            vehicle.set_transform(carla.Transform(location, start.rotation))
            if set_velocity:
                vehicle.set_target_velocity(carla.Vector3D(x=fx * SPEED_MPS, y=fy * SPEED_MPS, z=0.0))
            self.tick()

        worst = 0.0
        for _ in range(20):
            advance(set_velocity=False)
            worst = max(worst, self.speed(vehicle.get_velocity()))
        self.check(worst < VELOCITY_TOLERANCE_MPS,
                   f"kinematic, transforms only: reads zero (worst {worst:.4f} m/s)")

        worst_error = 0.0
        worst_travel_error = 0.0
        previous = vehicle.get_location()
        for _ in range(100):
            advance(set_velocity=True)
            v = vehicle.get_velocity()
            worst_error = max(worst_error, math.hypot(v.x - fx * SPEED_MPS, v.y - fy * SPEED_MPS), abs(v.z))
            here = vehicle.get_location()
            travelled = math.hypot(here.x - previous.x, here.y - previous.y) / DELTA_S
            worst_travel_error = max(worst_travel_error, abs(travelled - SPEED_MPS))
            previous = here
        self.check(worst_error < VELOCITY_TOLERANCE_MPS,
                   f"kinematic, velocity set each tick: reads it every tick (worst {worst_error:.2e} m/s)")
        self.check(worst_travel_error < 0.01,
                   f"kinematic: position advances at the commanded speed (worst {worst_travel_error:.2e} m/s)")
        self.logger.info("kinematic: angular velocity %s, acceleration %s (logged, not asserted)",
                         vehicle.get_angular_velocity(), vehicle.get_acceleration())

        worst_hold = 0.0
        for _ in range(10):
            advance(set_velocity=False)
            worst_hold = max(worst_hold, abs(self.speed(vehicle.get_velocity()) - SPEED_MPS))
        self.check(worst_hold < VELOCITY_TOLERANCE_MPS,
                   f"kinematic, held with transforms only: keeps the last velocity (worst {worst_hold:.2e})")
        vehicle.set_target_velocity(carla.Vector3D(x=0.0, y=0.0, z=0.0))
        self.tick()
        self.check(self.speed(vehicle.get_velocity()) < VELOCITY_TOLERANCE_MPS,
                   "kinematic, velocity set to zero: reads zero")

        vehicle.set_target_velocity(carla.Vector3D(x=fx * SPEED_MPS, y=fy * SPEED_MPS, z=0.0))
        self.tick()
        vehicle.set_simulate_physics(True)
        self.tick()
        vehicle.set_simulate_physics(False)
        self.tick()
        self.check(self.speed(vehicle.get_velocity()) < VELOCITY_TOLERANCE_MPS,
                   "kinematic, physics switched on and off: reads zero")

    def run_control(self, vehicle, spawned_at_z: float) -> None:
        # A control with nothing under it falls, and a falling body's speed agrees with its travel
        # without saying anything about driving. Refuse to compare rather than compare that.
        settled_z = vehicle.get_location().z
        if settled_z < spawned_at_z - 2.0:
            self.check(False, f"control: has ground under it (fell {spawned_at_z - settled_z:.1f} m "
                              "while settling; this map gives it nothing to stand on)")
            return
        vehicle.apply_control(carla.VehicleControl(throttle=0.6))
        previous = vehicle.get_location()
        errors = []
        top = 0.0
        for tick in range(100):
            self.tick()
            here = vehicle.get_location()
            travelled = math.sqrt((here.x - previous.x) ** 2 + (here.y - previous.y) ** 2
                                  + (here.z - previous.z) ** 2) / DELTA_S
            reported = self.speed(vehicle.get_velocity())
            top = max(top, reported)
            if tick >= 10:  # past the settling of the spawn drop
                errors.append(abs(reported - travelled))
            previous = here
        errors.sort()
        median = errors[len(errors) // 2]
        self.check(top > 1.0, f"control, physics on: it moved (top speed {top:.2f} m/s)")
        self.check(median < CONTROL_TOLERANCE_MPS,
                   f"control: reported speed agrees with speed from position (median gap {median:.3f} m/s)")

    def run(self) -> int:
        original = self.world.get_settings()
        vehicles = []
        try:
            settings = self.world.get_settings()
            settings.synchronous_mode = True
            settings.fixed_delta_seconds = DELTA_S
            self.world.apply_settings(settings)
            points = self.world.get_map().get_spawn_points()
            if len(points) < 2:
                self.logger.error("the loaded map has fewer than two spawn points")
                return 2
            kinematic_start, control_start = points[0], points[len(points) // 2]
            kinematic = self.spawn(kinematic_start)
            vehicles.append(kinematic)
            control = self.spawn(control_start)
            vehicles.append(control)
            for _ in range(20):
                self.tick()
            self.run_kinematic(kinematic, kinematic_start)
            self.run_control(control, control_start.location.z)
        finally:
            for vehicle in vehicles:
                try:
                    vehicle.destroy()
                except Exception as failure:  # noqa: BLE001 - cleanup reports and continues
                    self.logger.warning("could not destroy a vehicle: %r", failure)
            self.world.apply_settings(original)

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
    parser.add_argument("--blueprint", default="vehicle.lincoln.mkz")
    args = parser.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(message)s")
    logger = logging.getLogger("test_kinematic_velocity")

    client = carla.Client(args.host, args.port)
    client.set_timeout(20.0)
    logger.info("map %s", client.get_world().get_map().name)
    return KinematicVelocityCheck(client, args.blueprint, logger).run()


if __name__ == "__main__":
    sys.exit(main())
