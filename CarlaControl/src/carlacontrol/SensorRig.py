"""SensorRig - Manages synchronized RGB and depth cameras for CARLA simulation.

This module provides a unified interface for spawning, configuring, and controlling
RGB and depth camera sensors along with the spectator camera in CARLA.
"""

from __future__ import annotations

import logging
import math
import queue

import numpy as np

try:
    import carlanet as carla
except ImportError:
    import carla

from .Pose import Pose


class SensorRig:
    """Manages RGB + depth cameras with synchronized transforms.

    Encapsulates camera spawning, blueprint configuration, listener setup,
    and cleanup for both synchronous and asynchronous execution modes.
    """

    FT_PER_M = 3.28084

    # How close to the camera's greatest range a reading has to be before it is treated as having hit
    # nothing at all. Ten metres of a range that is kilometres long, so it costs nothing real.
    SATURATION_MARGIN_M = 10.0

    def __init__(self, world: carla.World, args):
        """Initialize and spawn RGB and depth cameras.

        Args:
            world: CARLA world object
            args: Parsed arguments with x, y, z, width, height, fov, ev, asynchronous
        """
        self.world = world
        self.width = args.width
        self.height = args.height
        self.fov = args.fov
        self.logger = logging.getLogger(__name__)

        self._sync = not getattr(args, "asynchronous", False)
        self._rgb_queue: queue.Queue | None = None
        self._depth_queue: queue.Queue | None = None
        self._latest_rgb: carla.Image | None = None
        self._latest_depth: dict[str, carla.Image | Pose | bytes | int] | None = None
        self.ground_z: float | None = None
        self.frames_received: int = 0

        # Create initial pose from args (z in feet, convert to meters)
        self.initial_pose = Pose(x=args.x, y=args.y, z=args.z / self.FT_PER_M, pitch=-90.0, yaw=0.0)

        # Configure RGB camera blueprint
        bp = world.get_blueprint_library().find("sensor.camera.rgb")
        bp.set_attribute("image_size_x", str(args.width))
        bp.set_attribute("image_size_y", str(args.height))
        if bp.has_attribute("fov"):
            bp.set_attribute("fov", str(args.fov))
        if args.ev is not None and bp.has_attribute("exposure_compensation"):
            bp.set_attribute("exposure_compensation", str(args.ev))

        # Configure depth camera blueprint
        dbp = world.get_blueprint_library().find("sensor.camera.depth")
        dbp.set_attribute("image_size_x", str(args.width))
        dbp.set_attribute("image_size_y", str(args.height))
        if dbp.has_attribute("fov"):
            dbp.set_attribute("fov", str(args.fov))
        # How far the depth camera can report. A surface beyond it is indistinguishable from sky, and
        # the stock 1000 m runs out at about 3250 ft looking straight down, which is inside the
        # altitudes flown here. An older server without the attribute keeps its built-in range.
        self.depth_max_range_m = float(getattr(args, "depth_max_range", 1000.0))
        if dbp.has_attribute("max_range"):
            dbp.set_attribute("max_range", str(self.depth_max_range_m))
        else:
            self.depth_max_range_m = 1000.0
            self.logger.warning(
                "server's depth camera has no max_range attribute; depth is limited to 1000 m "
                "(rebuild the server to raise it)"
            )

        # Spawn cameras
        tf = self.initial_pose.to_carla_transform()
        self.camera = world.spawn_actor(bp, tf)
        self.depth_cam = world.spawn_actor(dbp, tf)
        self.spectator = world.get_spectator()
        self.spectator.set_transform(tf)

        self.logger.info(f"spawned RGB camera id={self.camera.id}, depth camera id={self.depth_cam.id}")

        # Set up listeners based on sync mode
        self._setup_listeners_internal()

    def set_transform(self, tf: carla.Transform) -> None:
        """Update all camera transforms synchronously.

        Args:
            tf: CARLA Transform object
        """
        try:
            self.camera.set_transform(tf)
            self.depth_cam.set_transform(tf)
            self.spectator.set_transform(tf)
        except Exception as e:
            self.logger.warning(f"failed to set transform: {e}")

    def set_pose(self, pose: Pose) -> None:
        """Update all camera transforms synchronously.

        Args:
            pose: Camera pose (6-DOF)
        """
        try:
            tf = pose.to_carla_transform()
            self.camera.set_transform(tf)
            self.depth_cam.set_transform(tf)
            self.spectator.set_transform(tf)
        except Exception as e:
            self.logger.warning(f"failed to set pose: {e}")

    def _setup_listeners_internal(self) -> None:
        """Attach sensor listeners based on execution mode (determined at init)."""
        if self._sync:
            self._rgb_queue = queue.Queue()
            self._depth_queue = queue.Queue()
            self._latest_rgb = None

            def rgb_sync_handler(img):
                self.frames_received += 1
                self._rgb_queue.put(img)

            self.camera.listen(rgb_sync_handler)
            self.depth_cam.listen(self._depth_queue.put)
        else:
            self._rgb_queue = None
            self._depth_queue = None

            def rgb_async_handler(img):
                self.frames_received += 1
                self._latest_rgb = img

            def depth_async_handler(img):
                self.store_depth(img)

            self.camera.listen(rgb_async_handler)
            self.depth_cam.listen(depth_async_handler)

    def get_latest_rgb(self, timeout: float = 2.0) -> carla.Image | None:
        """Return the most recent RGB frame (queue-based in sync mode, stored in async mode).

        Args:
            timeout: Maximum seconds to wait for the first frame (sync mode only)

        Returns:
            Most recent RGB frame, or None if no frame available
        """
        if self._sync:
            return self._drain_latest(self._rgb_queue, timeout)
        else:
            return self._latest_rgb

    def get_latest_depth(self, timeout: float = 2.0) -> carla.Image | None:
        """Return the most recent queued depth frame in synchronous mode.

        Args:
            timeout: Maximum seconds to wait for the first frame

        Returns:
            Most recent depth frame, or None if no frame available
        """
        return self._drain_latest(self._depth_queue, timeout)

    def get_initial_pose(self) -> Pose:
        """Return the initial camera pose."""
        return self.initial_pose.copy()

    def reset_to_initial_pose(self) -> None:
        """Reset the camera to its initial pose."""
        self.camera.set_transform(self.initial_pose.to_carla_transform())
        self.logger.info(f"reset to initial pose of {self.initial_pose}")

    def get_current_transform(self) -> carla.Transform:
        """Return the current camera transform."""
        return self.camera.get_transform()

    def get_position(self) -> Pose:
        """Return the current camera position."""
        return Pose.from_carla_transform(self.camera.get_transform())

    def set_ground_z(self, ground_z: float | None):
        """Set the ground Z value for validation.

        Args:
            ground_z: Ground elevation in local Z, or None to clear
        """
        self.ground_z = ground_z
        if ground_z is not None:
            self.logger.info(f"ground z set: {ground_z:.2f}m")
        else:
            self.logger.info("ground z cleared")

    def store_depth(self, img, fallback_pose: Pose | None = None) -> None:
        """Process and store the latest depth frame.

        Args:
            img: Depth camera image
            fallback_pose: Pose to use if image has no transform (optional, uses camera transform if None)
        """
        if fallback_pose is None:
            fallback_pose = self.get_position()
        self._latest_depth = self.process_depth(img, fallback_pose)

    def get_latest_depth_dict(self) -> dict[str, carla.Image | Pose | bytes | int] | None:
        """Return the most recently stored depth frame dict.

        Returns:
            Depth dict with 'raw', 'w', 'h', 'pose' keys, or None if no depth stored
        """
        return self._latest_depth

    def is_synchronous(self) -> bool:
        """Return whether listeners are configured for synchronous retrieval."""
        return self._sync

    @staticmethod
    def process_depth(img: carla.Image, fallback_pose: Pose | None = None) -> dict[str, carla.Image | Pose | bytes | int]:
        """Decode a depth frame's capture pose into a picking record.

        Args:
            img: Depth camera image
            fallback_pose: Pose to use if image has no transform (optional)

        Returns:
            Dict with raw bytes, dimensions, and capture pose
        """
        if hasattr(img, "transform") and img.transform is not None:
            cap = Pose.from_carla_transform(img.transform)
        else:
            cap = fallback_pose.copy() if fallback_pose else Pose()
        return {"raw": bytes(img.raw_data), "w": img.width, "h": img.height, "pose": cap}

    def pick_world_point(
        self,
        u: int,
        v: int,
        origin_lat: float | None = None,
        origin_lon: float | None = None,
        origin_h: float | None = None,
    ) -> dict[str, float | int | tuple[float, float, float]]:
        """Reconstruct world point at pixel (u,v) from depth frame and convert to geodetic.

        Uses the SensorRig's stored depth frame, dimensions, FOV, ground elevation, and current pose.

        Args:
            u: Pixel x-coordinate
            v: Pixel y-coordinate
            origin_lat: Geodetic origin latitude (optional, for geodetic conversion)
            origin_lon: Geodetic origin longitude (optional, for geodetic conversion)
            origin_h: Geodetic origin height in meters (optional, for geodetic conversion)

        Returns:
            Dict with 'u', 'v', 'lat', 'lon', 'elev_ft', 'elev_m', 'P' (world xyz).

        Raises:
            ValueError: If no depth frame available, pixel out of bounds, no surface detected,
                       camera below terrain, hit above camera, or geodesy conversion fails.
        """
        if self._latest_depth is None:
            raise ValueError("no depth frame yet")

        w, h = self._latest_depth["w"], self._latest_depth["h"]
        if not (0 <= u < w and 0 <= v < h):
            return

        arr = np.frombuffer(self._latest_depth["raw"], np.uint8).reshape(h, w, 4)
        b = float(arr[v, u, 0])
        g = float(arr[v, u, 1])
        r = float(arr[v, u, 2])
        normalized = (r + g * 256.0 + b * 65536.0) / (256.0**3 - 1.0)
        depth_m = normalized * self.depth_max_range_m

        # Readings saturate at the camera's greatest range, so sky and anything beyond that range
        # arrive as the same maximum value. The band that counts as saturated is a fixed distance,
        # not a fraction of the range: as a fraction it would swallow whole kilometres of real
        # surface once the range is set high.
        if depth_m >= self.depth_max_range_m - self.SATURATION_MARGIN_M:
            raise ValueError("no surface (sky)")
        cp = self._latest_depth["pose"]
        cam_loc = (cp.x, cp.y, cp.z)
        yr = math.radians(cp.yaw)
        pr = math.radians(cp.pitch)
        fwd = (math.cos(yr) * math.cos(pr), math.sin(yr) * math.cos(pr), math.sin(pr))
        right = (-math.sin(yr), math.cos(yr), 0.0)

        def _cross(a, b):
            return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])

        up = _cross(fwd, right)
        f = self.width / (2.0 * math.tan(math.radians(self.fov) / 2.0))
        cx, cy = self.width / 2.0, self.height / 2.0
        s_right = (u - cx) / f
        s_up = -(v - cy) / f
        px = cam_loc[0] + (fwd[0] + right[0] * s_right + up[0] * s_up) * depth_m
        py = cam_loc[1] + (fwd[1] + right[1] * s_right + up[1] * s_up) * depth_m
        pz = cam_loc[2] + (fwd[2] + right[2] * s_right + up[2] * s_up) * depth_m

        if self.ground_z is not None and self.get_current_transform().location.z < self.ground_z:
            raise ValueError("camera below terrain — pick disabled")

        if pz > cam_loc[2] + 1.0:
            raise ValueError("hit above camera — rejected")

        lat = lon = elev_m = None
        have_origin = origin_lat is not None and origin_lon is not None and origin_h is not None

        if have_origin:
            try:
                from CarlaNet.Types.Geom import Geodesy, GeoLocation

                origin = GeoLocation(origin_lat, origin_lon, origin_h)
                geo = Geodesy.CarlaLocalToGeodetic(origin, px, py, pz)
                lat, lon, elev_m = geo.Latitude, geo.Longitude, geo.Altitude
            except Exception as e:
                raise ValueError(f"geodesy failed: {e!r}") from e

        if elev_m is None:
            elev_m = (origin_h if origin_h is not None else 0.0) + pz

        return {
            "u": u,
            "v": v,
            "lat": lat,
            "lon": lon,
            "elev_ft": elev_m * SensorRig.FT_PER_M,
            "elev_m": elev_m,
            "P": (px, py, pz),
        }

    @staticmethod
    def _drain_latest(sensor_queue: queue.Queue | None, timeout: float) -> carla.Image | None:
        """Get the most recent queued item (sync mode), blocking briefly for the first.

        Args:
            sensor_queue: Queue to drain
            timeout: Maximum seconds to wait for first item

        Returns:
            Most recent item from queue, or None if queue is None or empty
        """
        if sensor_queue is None:
            return None
        try:
            item = sensor_queue.get(timeout=timeout)
        except queue.Empty:
            return None
        while True:
            try:
                item = sensor_queue.get_nowait()
            except queue.Empty:
                return item

    def cleanup(self) -> None:
        """Stop and destroy all sensors.

        Stops sensor data streams and destroys actors. The stop() call should
        block until the sensor is fully stopped.
        """
        self.logger.info(f"cleaning up sensor rig: RGB camera id={self.camera.id}, depth camera id={self.depth_cam.id}")
        try:
            self.camera.stop()
            self.camera.destroy()
        except Exception as e:
            self.logger.warning(f"failed to cleanup RGB camera: {e}")
        try:
            self.depth_cam.stop()
            self.depth_cam.destroy()
        except Exception as e:
            self.logger.warning(f"failed to cleanup depth camera: {e}")
