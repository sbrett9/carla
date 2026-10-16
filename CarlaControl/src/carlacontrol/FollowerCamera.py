"""One RGB camera a follower process spawns for itself, and the latest frame it has delivered."""

from __future__ import annotations

import logging
import threading
from typing import Any

import carlanet as carla

from .ChannelDescription import ChannelDescription
from .Pose import Pose


class FollowerCamera:
    """Owns exactly one `sensor.camera.rgb` actor: spawns it, keeps its newest frame, destroys it.

    Nothing else in the world is touched. There is no depth camera, no spectator and no recorder,
    and no batch command, so there is nothing through which another actor could be moved or removed.
    `sensor_tick` is left at the blueprint's default, so the camera renders on every world tick and
    its frames arrive at whatever rate the world is being advanced -- the tick rate of whoever owns
    a synchronous world's clock, or the server's own rate when it runs free.

    It offers the methods `OrbitSensorController` calls on the rig it moves (`set_transform`,
    `set_pose`, `get_current_transform`, `get_position`, `get_initial_pose`), so the orbit
    controller drives this one camera exactly as it drives `SensorRig`'s, unchanged.
    """

    FT_PER_M = 3.28084

    def __init__(
        self,
        world: Any,
        channel: ChannelDescription,
        transform: carla.Transform,
        logger: logging.Logger | None = None,
    ) -> None:
        """Spawn the camera at `transform` and start listening to it.

        Args:
            world: The world to spawn into.
            channel: The channel whose optics the camera takes (`fov`, `width`, `height`).
            transform: Where the camera starts.
            logger: Where to report; this module's logger by default.
        """
        self.logger = logger or logging.getLogger(__name__)
        self.channel = channel
        self._lock = threading.Lock()
        self._latest: Any = None
        self._frames_received = 0
        self._initial_pose = Pose.from_carla_transform(transform)

        blueprint = world.get_blueprint_library().find("sensor.camera.rgb")
        blueprint.set_attribute("image_size_x", str(channel.width))
        blueprint.set_attribute("image_size_y", str(channel.height))
        if blueprint.has_attribute("fov"):
            blueprint.set_attribute("fov", str(channel.fov))
        self.actor = world.spawn_actor(blueprint, transform)
        self.logger.info("spawned camera actor %d, %dx%d, fov %.1f", self.actor.id,
                         channel.width, channel.height, channel.fov)
        self.actor.listen(self._on_frame)

    @property
    def id(self) -> int:
        """The camera's actor id."""
        return self.actor.id

    def _on_frame(self, image: Any) -> None:
        """Keep the newest frame. Runs on the stream's delivery thread, so it does no work."""
        with self._lock:
            self._latest = image
            self._frames_received += 1

    def latest(self) -> tuple[Any, int]:
        """The newest frame (None before the first) and how many frames have arrived in all."""
        with self._lock:
            return self._latest, self._frames_received

    def set_transform(self, transform: carla.Transform) -> None:
        """Move the camera. A failure is logged and the camera stays where it was."""
        try:
            self.actor.set_transform(transform)
        except Exception as failure:
            self.logger.warning("could not move camera %d: %r", self.actor.id, failure)

    def set_pose(self, pose: Pose) -> None:
        """Move the camera to a pose."""
        self.set_transform(pose.to_carla_transform())

    def get_current_transform(self) -> carla.Transform:
        """Where the server has the camera now."""
        return self.actor.get_transform()

    def get_position(self) -> Pose:
        """Where the server has the camera now, as a pose."""
        return Pose.from_carla_transform(self.actor.get_transform())

    def get_initial_pose(self) -> Pose:
        """Where the camera was spawned."""
        return self._initial_pose.copy()

    def destroy(self) -> None:
        """Stop listening and remove this camera, and only this camera, from the world."""
        actor_id = self.actor.id
        try:
            self.actor.stop()
        except Exception as failure:
            self.logger.warning("could not stop listening to camera %d: %r", actor_id, failure)
        try:
            self.actor.destroy()
            self.logger.info("destroyed camera actor %d", actor_id)
        except Exception as failure:
            self.logger.warning(
                "could not destroy camera %d: %r (a map load since it was spawned would already "
                "have removed it)", actor_id, failure)
