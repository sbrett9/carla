"""An RGB and depth camera pair that counts what it is sent without ever waiting for it.

The capture rig this stands in for pairs an RGB camera with a depth camera at the same pose and
pulls frames through a queue. A measurement cannot use that shape: with `sensor_tick` set, most ticks
produce no frame at all, so a getter that blocks until one arrives turns "the camera was told not to
render" into "the probe waited two seconds", and the arm being measured for speed reads as the slow
one.

So this counts in the listener and never blocks. What it records is the server frame number each
image carries, which is what makes "frames per tick" a measured quantity rather than an assumption:
an arm delivering one frame in ten ticks and an arm delivering one per tick are told apart by their
frame numbers, not by how long a read took.

It also keeps a coarse brightness reading from the last RGB frame. A capture probe measuring a scene
that never streamed its imagery is measuring an empty frustum, and the resulting clock ratio belongs
to a different scene than the one a corpus would be made from; the reading is there so that can be
said rather than assumed.
"""
from __future__ import annotations

import logging
import threading
from dataclasses import dataclass, field

from carlacontrol.Pose import Pose

# One byte in sixty-four of the last RGB frame, which is enough to tell a lit scene from an empty one
# and cheap enough to do in a listener that must not fall behind the stream.
BRIGHTNESS_STRIDE = 64


@dataclass
class CameraFrameTally:
    """What one camera was sent."""

    name: str
    frames: int = 0
    first_frame: int = 0
    last_frame: int = 0
    frame_numbers: list[int] = field(default_factory=list)

    @property
    def frame_spacing(self) -> list[int]:
        """Server frames between consecutive deliveries -- 1 when the camera renders every tick."""
        return [b - a for a, b in zip(self.frame_numbers, self.frame_numbers[1:], strict=False)]

    @property
    def modal_spacing(self) -> int:
        spacing = self.frame_spacing
        if not spacing:
            return 0
        return max(set(spacing), key=spacing.count)


class ProbeCameraPair:
    """Spawns an RGB and a depth camera at one pose and tallies what they deliver."""

    FEET_PER_METRE = 3.28084

    def __init__(self, world, width: int, height: int, fov: float, altitude_feet: float,
                 sensor_tick: float = 0.0, x: float = 0.0, y: float = 0.0,
                 logger: logging.Logger | None = None) -> None:
        self.world = world
        self.width = width
        self.height = height
        self.fov = fov
        self.sensor_tick = sensor_tick
        self.logger = logger or logging.getLogger(__name__)
        self._lock = threading.Lock()
        self.rgb_tally = CameraFrameTally("rgb")
        self.depth_tally = CameraFrameTally("depth")
        self.last_rgb_mean_level: float | None = None
        self._want_brightness = False

        pose = Pose(x=x, y=y, z=altitude_feet / self.FEET_PER_METRE, pitch=-90.0, yaw=0.0)
        transform = pose.to_carla_transform()

        library = world.get_blueprint_library()
        self.rgb_blueprint = self._configure(library.find("sensor.camera.rgb"))
        self.depth_blueprint = self._configure(library.find("sensor.camera.depth"))
        self.rgb_camera = world.spawn_actor(self.rgb_blueprint, transform)
        self.depth_camera = world.spawn_actor(self.depth_blueprint, transform)
        self.rgb_camera.listen(self._on_rgb)
        self.depth_camera.listen(self._on_depth)
        self.logger.info(
            "camera pair %d x %d at %.0f ft, fov %.0f, sensor_tick %.4f s -- rgb id %s, depth id %s",
            width, height, altitude_feet, fov, sensor_tick, self.rgb_camera.id,
            self.depth_camera.id)

    def _configure(self, blueprint):
        blueprint.set_attribute("image_size_x", str(self.width))
        blueprint.set_attribute("image_size_y", str(self.height))
        if blueprint.has_attribute("fov"):
            blueprint.set_attribute("fov", str(self.fov))
        if blueprint.has_attribute("sensor_tick"):
            blueprint.set_attribute("sensor_tick", str(self.sensor_tick))
        elif self.sensor_tick:
            raise RuntimeError(
                f"{blueprint.id} has no sensor_tick attribute, so the arm that sets it cannot be "
                "distinguished from the arm that does not")
        return blueprint

    def _tally(self, tally: CameraFrameTally, image) -> None:
        with self._lock:
            if tally.frames == 0:
                tally.first_frame = image.frame
            tally.frames += 1
            tally.last_frame = image.frame
            tally.frame_numbers.append(image.frame)

    def _on_rgb(self, image) -> None:
        self._tally(self.rgb_tally, image)
        if self._want_brightness:
            # Once per request, never per frame: summing a megapixel buffer in Python runs on the
            # stream thread, and a measurement that slows the stream it is measuring is not one.
            self._want_brightness = False
            raw = bytes(image.raw_data)
            if raw:
                sampled = raw[::BRIGHTNESS_STRIDE]
                self.last_rgb_mean_level = sum(sampled) / len(sampled)

    def _on_depth(self, image) -> None:
        self._tally(self.depth_tally, image)

    def request_brightness(self) -> None:
        """Ask the next RGB frame to report its mean level, so an empty scene can be named as one."""
        self.last_rgb_mean_level = None
        self._want_brightness = True

    def reset_tallies(self) -> None:
        with self._lock:
            self.rgb_tally = CameraFrameTally("rgb")
            self.depth_tally = CameraFrameTally("depth")

    @property
    def paired_frames(self) -> int:
        """Server frames on which both cameras delivered.

        The occlusion path pairs an RGB frame with the depth frame of the same instant, so whether
        `sensor_tick` keeps the two cameras on the same frames is a property of the capture and not
        a detail of the probe.
        """
        with self._lock:
            return len(set(self.rgb_tally.frame_numbers) & set(self.depth_tally.frame_numbers))

    def describe(self, ticks: int) -> str:
        return (f"   rgb {self.rgb_tally.frames:>5} frames in {ticks} ticks "
                f"({self.rgb_tally.frames / ticks:.3f} per tick, modal spacing "
                f"{self.rgb_tally.modal_spacing} frames); depth {self.depth_tally.frames:>5} "
                f"({self.depth_tally.frames / ticks:.3f} per tick, modal spacing "
                f"{self.depth_tally.modal_spacing}); {self.paired_frames} frames carry both")

    def close(self) -> None:
        for camera in (self.rgb_camera, self.depth_camera):
            try:
                camera.stop()
            except Exception as error:  # a sensor already torn down by the server
                self.logger.debug("stop failed: %s", error)
            try:
                camera.destroy()
            except Exception as error:
                self.logger.debug("destroy failed: %s", error)
