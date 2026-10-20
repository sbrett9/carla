"""The fixed camera carla-drive records from, and where either of its views starts."""
from __future__ import annotations

import argparse
import logging
import math

import carlanet as carla

from carlacontrol.RunConfiguration import RunConfiguration

logger = logging.getLogger(__name__)


class DriveCamera:
    """Places carla-drive's cameras: the fixed camera, the depth camera attached to it, and the
    point over the world either view starts from when it has nothing else to look at.
    """

    @staticmethod
    def world_centre(world) -> tuple[float, float]:
        """The centre of the world's staging bounds in CARLA's frame, or CARLA's origin where the world
        publishes none.

        A fact about the world -- the middle of the area it was built from -- rather than a setting, so a
        camera that has nothing yet to look at starts over the world itself.
        """
        bounds = world.get_staging_bounds()
        if bounds is None:
            return 0.0, 0.0
        return (bounds["min_x"] + bounds["max_x"]) / 2.0, (bounds["min_y"] + bounds["max_y"]) / 2.0

    @staticmethod
    def camera_transform(args: argparse.Namespace, centre: tuple[float, float]) -> carla.Transform:
        """An oblique view of a point, standing off along a bearing and looking back at it."""
        centre_x, centre_y = centre
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

    @staticmethod
    def spawn_camera(world, args: argparse.Namespace, centre: tuple[float, float]):
        blueprint = world.get_blueprint_library().find("sensor.camera.rgb")
        blueprint.set_attribute("image_size_x", str(args.width))
        blueprint.set_attribute("image_size_y", str(args.height))
        if blueprint.has_attribute("fov"):
            blueprint.set_attribute("fov", str(args.fov))
        # Render at the rate the recorder keeps, not at the world's. Left unset, the camera renders and
        # streams a frame every tick while the recorder keeps one in ten, and the frames the recorder
        # discards are discarded after they have been rendered, read back off the GPU and sent. At
        # 1920x1080 that was measured at 35.1 ms of a 43.0 ms tick, and the backlog stalls the server
        # until an apply_batch times out. sensor_tick suppresses the render itself, not merely the
        # delivery, so the cost goes with it.
        if blueprint.has_attribute("sensor_tick") and args.record_hz > 0:
            blueprint.set_attribute("sensor_tick", str(1.0 / args.record_hz))
        transform = DriveCamera.camera_transform(args, centre)
        # Spawned under its name as the camera's role_name, which the server refuses where a live camera
        # in the world holds it; given none, the server names it. The name is read back from the camera.
        camera = world.spawn_camera(blueprint, transform, name=args.camera_name)
        logger.info("camera %s (%s) at (%.1f, %.1f, %.1f), pitch %.1f, yaw %.1f, looking at (%.1f, %.1f)",
                    world.camera_name(camera), camera.id, transform.location.x, transform.location.y,
                    transform.location.z, transform.rotation.pitch, transform.rotation.yaw, centre[0],
                    centre[1])
        return camera

    @staticmethod
    def spawn_depth_camera(world, args: argparse.Namespace, camera, range_m: float):
        """The depth camera the fixed camera's occlusion is measured against, attached to the camera.

        A rigid attachment at the identity pose, as `CaptureSession._attach_depth_camera` spawns a
        channel's: the server keeps an attached actor at its pose relative to its parent and reports its
        world pose on every snapshot and in every image header, so the camera's pose is the depth
        camera's by construction and the recorder's depth pose check compares two poses nothing set
        apart. It takes the camera's image size, field of view and sensor tick, so its captures pair with
        the camera's frame for frame, and the capture run's one depth range, so occlusion is measured as
        far as a capture run's is rather than to the depth camera's stock 1000 m. Destroyed before the
        camera it is attached to.
        """
        blueprint = world.get_blueprint_library().find("sensor.camera.depth")
        blueprint.set_attribute("image_size_x", str(args.width))
        blueprint.set_attribute("image_size_y", str(args.height))
        if blueprint.has_attribute("fov"):
            blueprint.set_attribute("fov", str(args.fov))
        if blueprint.has_attribute("sensor_tick") and args.record_hz > 0:
            blueprint.set_attribute("sensor_tick", str(1.0 / args.record_hz))
        if blueprint.has_attribute("max_range"):
            blueprint.set_attribute("max_range", str(range_m))
        else:
            logger.warning("the server's depth camera has no max_range attribute, so occlusion is "
                           "measured to its built-in range and no farther")
        depth = world.spawn_actor(blueprint, carla.Transform(), attach_to=camera,
                                  attachment_type=carla.AttachmentType.Rigid)
        logger.info("depth camera %s attached to camera %s, range %g m", depth.id, camera.id, range_m)
        return depth

    @staticmethod
    def depth_range_m() -> float:
        """The capture run's one depth range, `occlusion.depth_max_range_m`, read from the run
        configuration's field table so a drive and a capture run measure occlusion to one range."""
        return float(RunConfiguration.field("occlusion.depth_max_range_m").default)
