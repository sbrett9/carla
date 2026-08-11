from __future__ import annotations

import logging
import math
import threading
import time
from typing import TYPE_CHECKING

import carlanet as carla

from .Pose import Pose
from .SensorController import SensorController
from .SensorRig import SensorRig

if TYPE_CHECKING:
    from .PyGameSensorController import PyGameSensorController


FT_PER_M = 3.28084


class OrbitSensorController(SensorController):
    def __init__(
        self,
        sensors: SensorRig,
        world: carla.World | None = None,
        args=None,
        flight_controller: PyGameSensorController | None = None,
        logger: logging.Logger | None = None,
        sync: bool = False,
    ) -> None:
        super().__init__(sensors)
        self.sensors = sensors
        self.world = world
        self.flight_controller = flight_controller
        self.logger = logger or logging.getLogger(__name__)
        self.sync = sync
        self.orbit_enabled = False
        self.orbit_paused = False
        self.center_x = 0.0
        self.center_y = 0.0
        self.center_z = 0.0
        self.radius = 200.0
        self.cam_altitude = 0.0
        self.orbit_speed = 240.0
        self.angle = 0.0
        self.angular_velocity = (2.0 * math.pi) / self.orbit_speed
        self.last_time: float | None = None
        self.center_lat: float | None = None
        self.center_lon: float | None = None
        self.georeference_origin: tuple[float, float, float] | None = None
        self.orbit_description = ""
        self._thread_stop = threading.Event()
        self._thread: threading.Thread | None = None

        if self.world is not None:
            try:
                lat0, lon0, origin_h = self.world.get_cesium_origin()
                self.georeference_origin = (lat0, lon0, origin_h)
            except Exception:
                self.georeference_origin = None

        if args is not None:
            self.orbit_description = self.configure_from_args(args)
            self.start_updater()
            if args.orbit:
                self.set_enabled(True)
            else:
                self.logger.info("orbit ready (press O): %s", self.orbit_description)

    def move_object_to_position(self, position: Pose) -> None:
        self.sensors.set_pose(position)

    def set_object_transform(self, tf: carla.Transform) -> None:
        self.sensors.set_transform(tf)

    def get_current_transform(self) -> carla.Transform:
        return self.sensors.get_current_transform()

    def get_current_pose(self) -> Pose:
        return self.sensors.get_position()

    def start_updater(self) -> None:
        if self._thread is not None and self._thread.is_alive():
            return
        self._thread_stop.clear()
        self._thread = threading.Thread(target=self._orbit_updater, daemon=True)
        self._thread.start()

    def stop_updater(self) -> None:
        self._thread_stop.set()
        if self._thread is not None:
            self._thread.join(timeout=2.0)
            self._thread = None

    def _orbit_updater(self) -> None:
        while not self._thread_stop.is_set():
            if self.orbit_enabled and not self.orbit_paused:
                self.update_orbit()
            time.sleep(0.02)

    def toggle_orbit(self, enabled: bool | None = None) -> None:
        self.set_enabled(enabled)

    def set_enabled(self, enabled: bool | None = None) -> None:
        self.logger.info("toggling orbit")
        if enabled is None:
            enabled = not self.orbit_enabled
        if enabled == self.orbit_enabled:
            return
        self.orbit_enabled = enabled
        if self.orbit_enabled:
            self.logger.info("orbit ON: %s", self.orbit_description)
            if self.sync:
                self.logger.info(
                    "orbit streams photoreal tiles best under --async; with a synchronous "
                    "world the camera still moves between ticks, on its own thread"
                )
            self.last_time = time.time()
            self.orbit_paused = False
            self.update_orbit(0.0)
        else:
            try:
                pose = self.get_current_pose()
                if self.flight_controller is not None:
                    self.flight_controller.pose.update_from(pose)
            except Exception as exc:
                self.logger.warning("orbit handoff: could not read camera transform: %r", exc)
            self.logger.info(
                "orbit OFF: free flight resumed at (%.1f, %.1f), %.0f ft, yaw %.1f pitch %.1f",
                self.flight_controller.pose.x if self.flight_controller is not None else 0.0,
                self.flight_controller.pose.y if self.flight_controller is not None else 0.0,
                (self.flight_controller.pose.z * self.sensors.FT_PER_M)
                if self.flight_controller is not None
                else 0.0,
                self.flight_controller.pose.yaw if self.flight_controller is not None else 0.0,
                self.flight_controller.pose.pitch if self.flight_controller is not None else 0.0,
            )

    def toggle_orbit_pause(self) -> None:
        self.orbit_paused = not self.orbit_paused

    def latlon_to_carla(self, lat: float, lon: float) -> tuple[float, float] | None:
        if self.georeference_origin is None:
            return None
        lat0, lon0, _ = self.georeference_origin
        radius = 6378137.0
        lat_rad = math.radians(lat)
        lon_rad = math.radians(lon)
        lat0_rad = math.radians(lat0)
        lon0_rad = math.radians(lon0)
        x = radius * (lon_rad - lon0_rad) * math.cos(lat0_rad)
        y = -radius * (lat_rad - lat0_rad)
        return x, y

    def carla_to_latlon(self, x: float, y: float) -> tuple[float, float] | None:
        if self.georeference_origin is None:
            return None
        lat0, lon0, _ = self.georeference_origin
        radius = 6378137.0
        lat0_rad = math.radians(lat0)
        lon0_rad = math.radians(lon0)
        lon_rad = lon0_rad + (x / (radius * math.cos(lat0_rad)))
        lat_rad = lat0_rad - (y / radius)
        return math.degrees(lat_rad), math.degrees(lon_rad)

    def set_orbit_params(
        self,
        center_x: float | None = None,
        center_y: float | None = None,
        center_z: float | None = None,
        center_lat: float | None = None,
        center_lon: float | None = None,
        radius: float | None = None,
        radius_feet: float | None = None,
        altitude: float | None = None,
        altitude_feet: float | None = None,
        speed: float | None = None,
        angle: float | None = None,
    ) -> None:
        if center_lat is not None and center_lon is not None:
            result = self.latlon_to_carla(center_lat, center_lon)
            if result is not None:
                center_x, center_y = result
                self.center_lat = center_lat
                self.center_lon = center_lon

        if center_x is not None:
            self.center_x = center_x
        if center_y is not None:
            self.center_y = center_y
        if center_z is not None:
            self.center_z = center_z

        if radius_feet is not None:
            self.radius = radius_feet / FT_PER_M
        elif radius is not None:
            self.radius = radius

        if altitude_feet is not None:
            self.cam_altitude = altitude_feet / FT_PER_M
        elif altitude is not None:
            self.cam_altitude = altitude

        if speed is not None:
            self.orbit_speed = speed
            self.angular_velocity = (2.0 * math.pi) / self.orbit_speed
        if angle is not None:
            self.angle = angle


    def configure_from_args(self, args) -> str:
        orbit_altitude_ft = args.orbit_altitude if args.orbit_altitude else args.z
        orbit_kwargs = {
            "center_z": 0.0,
            "radius_feet": args.orbit_radius,
            "altitude_feet": orbit_altitude_ft,
            "speed": args.orbit_speed,
        }

        if args.orbit_lat is not None and args.orbit_lon is not None:
            self.set_orbit_params(
                center_lat=args.orbit_lat,
                center_lon=args.orbit_lon,
                **orbit_kwargs,
            )
            orbit_center_desc = f"center lat {args.orbit_lat:.7f}, lon {args.orbit_lon:.7f}"
        elif args.orbit_x is not None and args.orbit_y is not None:
            self.set_orbit_params(
                center_x=args.orbit_x,
                center_y=args.orbit_y,
                **orbit_kwargs,
            )
            orbit_center_desc = f"center ({args.orbit_x:.1f}, {args.orbit_y:.1f})"
        else:
            initial_pose = self.sensors.get_initial_pose()
            self.set_orbit_params(
                center_x=initial_pose.x,
                center_y=initial_pose.y,
                **orbit_kwargs,
            )
            orbit_center_desc = f"center ({initial_pose.x:.1f}, {initial_pose.y:.1f})"

        self.orbit_description = (
            f"{orbit_center_desc}, radius {args.orbit_radius:.0f} ft, "
            f"altitude {orbit_altitude_ft:.0f} ft, {args.orbit_speed:.0f} s per revolution"
        )
        return self.orbit_description

    def update_orbit(self, dt: float | None = None) -> None:
        if not self.orbit_enabled or self.orbit_paused:
            return

        if dt is None:
            current_time = time.time()
            if self.last_time is None:
                self.last_time = current_time
                return
            dt = current_time - self.last_time
            self.last_time = current_time

        self.angle = (self.angle + self.angular_velocity * dt) % (2.0 * math.pi)
        cam_x = self.center_x + self.radius * math.cos(self.angle)
        cam_y = self.center_y + self.radius * math.sin(self.angle)
        cam_z = self.center_z + self.cam_altitude
        dx = self.center_x - cam_x
        dy = self.center_y - cam_y
        dz = self.center_z - cam_z
        horizontal_dist = math.sqrt(dx * dx + dy * dy)
        pitch = math.degrees(math.atan2(dz, horizontal_dist))
        yaw = math.degrees(math.atan2(dy, dx))

        transform = carla.Transform(
            carla.Location(x=cam_x, y=cam_y, z=cam_z),
            carla.Rotation(pitch=pitch, yaw=yaw, roll=0.0),
        )
        self.sensors.set_transform(transform)

    def get_hud_info(self) -> dict[str, object]:
        info: dict[str, object] = {
            "orbit_enabled": self.orbit_enabled,
            "orbit_paused": self.orbit_paused,
        }

        if self.orbit_enabled:
            latlon = None
            if self.center_lat is not None and self.center_lon is not None:
                latlon = (self.center_lat, self.center_lon)
            elif self.georeference_origin is not None:
                latlon = self.carla_to_latlon(self.center_x, self.center_y)

            info.update(
                {
                    "orbit_center": (self.center_x, self.center_y, self.center_z),
                    "center_latlon": latlon,
                    "radius": self.radius,
                    "radius_feet": self.radius * FT_PER_M,
                    "cam_altitude": self.cam_altitude,
                    "cam_altitude_feet": self.cam_altitude * FT_PER_M,
                    "orbit_speed": self.orbit_speed,
                    "angle": self.angle,
                    "orbit_progress": (self.angle / (2.0 * math.pi)) * 100.0,
                }
            )

        return info
