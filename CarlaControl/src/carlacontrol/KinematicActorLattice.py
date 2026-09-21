"""A grid of vehicles with physics off, moved by one batch of transforms per tick.

This is the fixture the rendered-actor ceiling and the batch cost are measured on. Under SUMO drive
the bridge holds the population and writes every vehicle's pose itself, so what has to be sized is
exactly this: N bodies whose transforms are written from outside the engine, every tick, while a
camera renders them.

Two properties of the fixture are load-bearing and neither is obvious.

**Every pose must actually change.** `AActor::SetActorTransform` compares against the current
transform and returns without touching the scene when they match, so a lattice that writes the pose
it already has measures the round trip and nothing else. Each body therefore orbits its own site by a
metre or so per tick and turns as it goes -- the same order of movement a vehicle at urban speed
makes in one 0.05 s step.

**The lattice is sized to the camera's footprint, not to a radius.** A ring is the obvious shape and
the wrong one: 512 bodies at a spacing that keeps them apart needs a radius far outside the swath a
detector can use, so most of them fall outside the frustum and are culled, and the sweep measures
culling rather than rendering. A grid inside the footprint keeps every body in frame at every N,
which is what makes the number a rendered-actor ceiling. Displacing the same grid outside the
footprint is the control that separates what the render costs from what the pose write costs.
"""
from __future__ import annotations

import logging
import math
from dataclasses import dataclass

import carlanet as carla

# Vehicles are laid out with this much of their cell left empty around them, so that the orbit each
# one runs cannot carry it into its neighbour's cell and turn the sweep into a collision test.
CELL_MARGIN = 2.0
# The radius each body orbits its own lattice site on, in metres. One step of the orbit is the same
# order of movement as a vehicle at urban speed makes in a 0.05 s tick, which is what the pose write
# has to carry under SUMO drive.
ORBIT_RADIUS_M = 1.0
ORBIT_STEP_RADIANS = 0.25
# How many distinct pose batches to build ahead of the run and then cycle through. Every tick still
# writes a pose different from the one before it, which is what the transform write needs to be real,
# but the Python that builds N command objects and marshals them is paid once per cycle instead of
# once per tick -- otherwise the sweep measures the client's object churn and calls it an actor cost.
POSE_CYCLE_LENGTH = 25
# The measured fraction of a render set that changes its signal mask per 0.05 s step inside the
# 300 m render region on the binding scenario (10_Scale_And_Performance.md section 4.8.1).
LIGHT_TRANSITION_FRACTION = 0.075


@dataclass(frozen=True)
class CameraFootprint:
    """The ground rectangle a downward-looking camera covers, in metres."""

    width: float
    height: float

    @classmethod
    def for_camera(cls, altitude_m: float, horizontal_fov_deg: float,
                   pixel_width: int, pixel_height: int) -> CameraFootprint:
        width = 2.0 * altitude_m * math.tan(math.radians(horizontal_fov_deg) / 2.0)
        return cls(width, width * pixel_height / pixel_width)


class KinematicActorLattice:
    """N vehicles on a grid, physics off, whose poses are written in one batch per tick."""

    def __init__(self, world, count: int, footprint: CameraFootprint, ground_z: float,
                 centre_x: float = 0.0, centre_y: float = 0.0,
                 logger: logging.Logger | None = None) -> None:
        self.world = world
        self.count = count
        self.footprint = footprint
        self.ground_z = ground_z
        self.centre_x = centre_x
        self.centre_y = centre_y
        self.logger = logger or logging.getLogger(__name__)
        self.actor_ids: list[int] = []
        self.sites: list[tuple[float, float]] = []
        self.spawn_failures = 0
        self._light_cursor = 0

    @property
    def spacing(self) -> float:
        """Metres between neighbouring lattice sites, from the count and the footprint."""
        if self.count <= 1:
            return max(self.footprint.width, self.footprint.height)
        columns = max(1, round(math.sqrt(self.count * self.footprint.width / self.footprint.height)))
        return self.footprint.width / columns

    def _lattice_sites(self) -> list[tuple[float, float]]:
        if self.count == 0:
            return []
        columns = max(1, round(math.sqrt(self.count * self.footprint.width / self.footprint.height)))
        rows = math.ceil(self.count / columns)
        step_x = self.footprint.width / columns
        step_y = self.footprint.height / rows
        sites = []
        for index in range(self.count):
            column, row = index % columns, index // columns
            sites.append((
                self.centre_x - self.footprint.width / 2.0 + (column + 0.5) * step_x,
                self.centre_y - self.footprint.height / 2.0 + (row + 0.5) * step_y))
        return sites

    def spawn(self, client) -> int:
        """Spawn the lattice and take physics, gravity and collision off every body.

        Returns the number of bodies that reached the world. A shortfall is reported rather than
        retried: a sweep whose N is not the N it asked for is not a point on the curve.
        """
        if self.count == 0:
            return 0
        self.sites = self._lattice_sites()
        blueprints = list(self.world.get_blueprint_library().filter("vehicle.*"))
        if not blueprints:
            raise RuntimeError("the world has no vehicle blueprints to build a lattice from")
        commands = []
        for index, (x, y) in enumerate(self.sites):
            blueprint = blueprints[index % len(blueprints)]
            transform = carla.Transform(carla.Location(x, y, self.ground_z),
                                        carla.Rotation(0.0, (index * 37) % 360, 0.0))
            commands.append(carla.command.SpawnActor(blueprint, transform))
        responses = client.apply_batch_sync(commands)
        self.actor_ids = [response.actor_id for response in responses if not response.has_error]
        self.spawn_failures = len(responses) - len(self.actor_ids)
        for actor in self.world.get_actors(self.actor_ids):
            actor.set_simulate_physics(False)
            actor.set_enable_gravity(False)
            actor.set_collisions(False)
        self.logger.info("lattice of %d on a %.0f x %.0f m footprint, %.1f m spacing, "
                         "%d spawned, %d refused", self.count, self.footprint.width,
                         self.footprint.height, self.spacing, len(self.actor_ids),
                         self.spawn_failures)
        if self.spacing < CELL_MARGIN:
            self.logger.warning("lattice spacing %.1f m is tighter than the %.1f m cell margin",
                                self.spacing, CELL_MARGIN)
        return len(self.actor_ids)

    def build_pose_cycle(self, length: int = POSE_CYCLE_LENGTH) -> list:
        """Marshal `length` distinct pose batches up front, to be cycled through during the run.

        The returned batches are already in the form the client sends, so the timed part of a tick is
        the send and the server's execution of it, not the building of it here.
        """
        return [[cmd.to_cs() for cmd in self.transform_batch(index)] for index in range(length)]

    def transform_batch(self, tick_index: int) -> list:
        """One tick's worth of pose writes, every one of them a real move."""
        angle = tick_index * ORBIT_STEP_RADIANS
        commands = []
        for offset, (actor_id, (x, y)) in enumerate(zip(self.actor_ids, self.sites, strict=False)):
            phase = angle + offset * 0.1
            transform = carla.Transform(
                carla.Location(x + ORBIT_RADIUS_M * math.cos(phase),
                               y + ORBIT_RADIUS_M * math.sin(phase),
                               self.ground_z),
                carla.Rotation(0.0, math.degrees(phase) % 360.0, 0.0))
            commands.append(carla.command.ApplyTransform(actor_id, transform))
        return commands

    def light_batch(self, fraction: float = LIGHT_TRANSITION_FRACTION) -> list:
        """A tick's signal-mask changes, on the measured fraction of the set.

        The bodies are taken in a rotating window rather than at random, so the same number of
        transitions happens every tick and the arm is a constant load rather than a distribution.
        """
        if not self.actor_ids:
            return []
        changing = max(1, round(fraction * len(self.actor_ids)))
        commands = []
        for step in range(changing):
            index = (self._light_cursor + step) % len(self.actor_ids)
            # Alternate between two masks so that every command is a transition: the engine's light
            # path early-outs on an unchanged state, and a sweep that sets what is already set would
            # measure the early-out instead of the Blueprint call it is there to price.
            mask = (carla.VehicleLightState.Brake if (index + self._light_cursor) % 2
                    else carla.VehicleLightState.LeftBlinker)
            commands.append(carla.command.SetVehicleLightState(
                self.actor_ids[index], carla.VehicleLightStateFlags(int(mask))))
        self._light_cursor = (self._light_cursor + changing) % len(self.actor_ids)
        return commands

    def destroy(self, client) -> None:
        if not self.actor_ids:
            return
        client.apply_batch_sync([carla.command.DestroyActor(actor_id)
                                 for actor_id in self.actor_ids])
        self.actor_ids = []
        self.sites = []
