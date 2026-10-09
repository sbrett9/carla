"""The orbit camera: a circle the server flies, declared once from here.

The orbit used to be driven from the client: a thread advanced the angle on the wall clock and pushed
a pose to the server as a synchronous call about fifty times a second. Measured (issue #37), the
server's tick rate on a loaded world fell from 41.6 to 20.7 per second with the orbit running, and
under a synchronous drive the camera turned as far per captured frame as the run's pace was high. The
server flies it now: `Sensor.set_orbit` puts an orbit mover on the camera, which advances the angle by
each tick's delta on the simulation clock and sets the camera on the circle before the frame's sensors
capture, so the client sends the circle once, a pause or a resume as one call each, and nothing per
frame. The angle shown here is computed from the parameters and the simulated clock, never asked for.

The pose rule is `orbit_transform`, which the server's mover shares, so the pose the server sets at an
angle is the pose this class predicts for it.
"""
from __future__ import annotations

import logging
import math
from collections.abc import Callable
from typing import TYPE_CHECKING

import carlanet as carla

from .Pose import Pose
from .SensorController import SensorController
from .SensorRig import SensorRig

if TYPE_CHECKING:
    from .PyGameSensorController import PyGameSensorController


FT_PER_M = 3.28084
TWO_PI = 2.0 * math.pi


class OrbitSensorController(SensorController):
    """Declares a camera's orbit to the server and keeps the parameters, the state and the HUD text.

    `sensors` is whatever holds the camera the server flies: a `SensorRig` (its colour camera; the
    depth camera is attached to it and rides with it), a `FollowerCamera`, or the camera itself.
    """

    def __init__(
        self,
        sensors: SensorRig,
        world: carla.World | None = None,
        args=None,
        flight_controller: PyGameSensorController | None = None,
        logger: logging.Logger | None = None,
        sync: bool = False,
        clock: Callable[[], float] | None = None,
    ) -> None:
        """
        Args:
            sensors: What holds the orbiting camera; see the class.
            world: The world, for its georeference and, failing `clock`, its simulated clock.
            args: Parsed `run_SCTMV.py` arguments to configure from, and to start under `--orbit`.
            flight_controller: Handed the camera's pose when the orbit is turned off.
            logger: Where to report.
            sync: Whether the world is synchronous; said in the log when the orbit starts.
            clock: Simulated seconds, read when the orbit starts, pauses and resumes and when the
                angle is shown. `world.get_sim_time` by default: a cache read, no round trip.
        """
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
        self.clockwise = True
        # The angle the orbit stands at, or started from: the server's start angle when it is
        # enabled, and the angle it was predicted at when it was paused or disabled.
        self.angle = 0.0
        self.angular_velocity = TWO_PI / self.orbit_speed
        self.center_lat: float | None = None
        self.center_lon: float | None = None
        self.georeference_origin: tuple[float, float, float] | None = None
        self.orbit_description = ""
        self.orbit_camera = self._camera_of(sensors)
        if clock is None and world is not None and hasattr(world, "get_sim_time"):
            clock = world.get_sim_time
        self.clock = clock
        # The simulated instant the angle last started advancing from `self.angle`, or None while
        # it is not advancing.
        self.advancing_since_s: float | None = None
        # Whether the server holds this circle, configured and not moving, from `hold_on_server`.
        self._held_on_server = False

        if self.world is not None:
            try:
                lat0, lon0, origin_h = self.world.get_cesium_origin()
                self.georeference_origin = (lat0, lon0, origin_h)
            except Exception:
                self.georeference_origin = None

        if args is not None:
            self.orbit_description = self.configure_from_args(args)
            if args.orbit:
                self.set_enabled(True)
            else:
                self.logger.info("orbit ready (press O): %s", self.orbit_description)

    @staticmethod
    def _camera_of(sensors):
        """The camera the server flies: a rig's colour camera, a follower's actor, or the camera."""
        camera = getattr(sensors, "camera", None)
        if camera is None:
            camera = getattr(sensors, "actor", None)
        return sensors if camera is None else camera

    # -- moving the rig by hand ------------------------------------------------------------------------

    def move_object_to_position(self, position: Pose) -> None:
        """Move the rig by hand. The server's orbit, if it is flying, is turned off first, so the two
        never fight over the camera's pose."""
        self._let_go_for_a_manual_move()
        self.sensors.set_pose(position)

    def set_object_transform(self, tf: carla.Transform) -> None:
        """Move the rig by hand; see `move_object_to_position`."""
        self._let_go_for_a_manual_move()
        self.sensors.set_transform(tf)

    def _let_go_for_a_manual_move(self) -> None:
        if self.orbit_enabled:
            self.set_enabled(False)

    def get_current_transform(self) -> carla.Transform:
        return self.sensors.get_current_transform()

    def get_current_pose(self) -> Pose:
        return self.sensors.get_position()

    # -- the server's orbit ------------------------------------------------------------------------------

    def toggle_orbit(self, enabled: bool | None = None) -> None:
        self.set_enabled(enabled)

    def set_enabled(self, enabled: bool | None = None) -> None:
        """Turn the server's orbit on or off; None toggles.

        On: the circle goes to the server once, with `self.angle` as its start angle, and the server
        flies the camera from the next tick; a server that cannot -- one built before the orbit mover
        -- is said plainly and the orbit stays off. Nothing here moves the camera in its place. Off:
        the server lets the camera go where it is, and the flight controller takes the pose.
        """
        if enabled is None:
            enabled = not self.orbit_enabled
        if enabled == self.orbit_enabled:
            return
        if enabled:
            self._start_on_server()
        else:
            self._stop_on_server()

    def _start_on_server(self) -> None:
        try:
            if self._held_on_server:
                self.orbit_camera.set_orbit_enabled(True)
            else:
                self._send_orbit(enabled=True)
        except Exception as refused:
            # The shim's refusal says what the server lacks (carlanet.OrbitNotOnServerError: a
            # server built before the orbit mover), a wheel built before it is said below, and any
            # other failure is the server's own words. Whichever, the orbit stays off and nothing
            # here flies the camera in the server's place.
            self.logger.error("orbit refused: %s", refused)
            return
        self._held_on_server = False
        self.orbit_enabled = True
        self.orbit_paused = False
        self.advancing_since_s = self._now()
        self.logger.info("orbit ON: %s; flown by the server on the simulation clock, from angle "
                         "%.3f rad", self.orbit_description, self.angle)
        if self.sync:
            self.logger.info("synchronous world: the camera advances by the fixed delta on every "
                             "tick, so a simulated second is a second of orbit whatever the pace")

    def _stop_on_server(self) -> None:
        self.angle = self.current_angle()
        self.advancing_since_s = None
        try:
            self.orbit_camera.set_orbit_enabled(False)
        except Exception as failure:
            self.logger.warning("orbit OFF: the server did not answer the stop: %r", failure)
        self.orbit_enabled = False
        self.orbit_paused = False
        if self.flight_controller is not None:
            try:
                self.flight_controller.pose.update_from(self.get_current_pose())
            except Exception as exc:
                self.logger.warning("orbit handoff: could not read camera transform: %r", exc)
            pose = self.flight_controller.pose
            self.logger.info(
                "orbit OFF: free flight resumed at (%.1f, %.1f), %.0f ft, yaw %.1f pitch %.1f",
                pose.x, pose.y, pose.z * FT_PER_M, pose.yaw, pose.pitch)
        else:
            self.logger.info("orbit OFF at angle %.3f rad", self.angle)

    def hold_on_server(self) -> None:
        """Give the server the circle, not moving: the camera stays where it is, at the pose of
        `self.angle` where it was spawned there, until `set_enabled(True)`. A capture does this when
        it places an orbit's camera, so a server that cannot fly an orbit refuses at pre-roll rather
        than as the window opens.

        Raises:
            carlanet.OrbitNotOnServerError: The server was built before the orbit mover.
        """
        self._send_orbit(enabled=False)
        self._held_on_server = True

    def _send_orbit(self, enabled: bool) -> None:
        if not hasattr(self.orbit_camera, "set_orbit"):
            raise RuntimeError(
                "the carlanet wheel in use has no Sensor.set_orbit: it was built before the orbit "
                "mover. Rebuild the wheel. Nothing in the client moves the camera in the server's "
                "place")
        self.orbit_camera.set_orbit(
            carla.Location(x=self.center_x, y=self.center_y, z=self.center_z),
            self.radius, self.cam_altitude, self.orbit_speed,
            clockwise=self.clockwise, start_angle=self.angle, enabled=enabled)

    def toggle_orbit_pause(self) -> None:
        self.set_paused(not self.orbit_paused)

    def set_paused(self, paused: bool) -> None:
        """Hold the orbit at its angle, or let it advance again: one call to the server each. The
        camera stays on the circle while paused."""
        if not self.orbit_enabled or paused == self.orbit_paused:
            return
        if paused:
            self.angle = self.current_angle()
        try:
            self.orbit_camera.set_orbit_paused(paused)
        except Exception as failure:
            self.logger.warning("orbit: the server did not take the %s: %r",
                                "pause" if paused else "resume", failure)
            return
        self.orbit_paused = paused
        self.advancing_since_s = None if paused else self._now()

    def _now(self) -> float | None:
        if self.clock is None:
            return None
        try:
            return float(self.clock())
        except Exception as failure:
            self.logger.debug("orbit: the simulated clock could not be read: %r", failure)
            return None

    def current_angle(self) -> float:
        """The angle the server holds the camera at now, predicted from the parameters and the
        simulated clock: the start angle plus the angular rate times the simulated seconds since the
        orbit last started advancing, signed by direction, in [0, 2 pi). Never asked of the server."""
        if self.advancing_since_s is None:
            return self.angle
        now = self._now()
        if now is None:
            return self.angle
        sign = 1.0 if self.clockwise else -1.0
        return (self.angle + sign * self.angular_velocity * (now - self.advancing_since_s)) % TWO_PI

    def predicted_transform(self) -> carla.Transform:
        """Where the server has the camera now, by the shared pose rule at `current_angle`."""
        return self.orbit_transform(self.center_x, self.center_y, self.center_z, self.radius,
                                    self.cam_altitude, self.current_angle())

    # -- the circle -------------------------------------------------------------------------------------

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
        clockwise: bool | None = None,
    ) -> None:
        """Set the circle. Takes effect on the server the next time the orbit is enabled or held; a
        circle the server already holds is not changed under it."""
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
            self.angular_velocity = TWO_PI / self.orbit_speed
        if angle is not None:
            self.angle = angle % TWO_PI
        if clockwise is not None:
            self.clockwise = clockwise
        self._held_on_server = False

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

    @staticmethod
    def orbit_transform(center_x: float, center_y: float, center_z: float, radius: float,
                        altitude: float, angle: float) -> carla.Transform:
        """The camera's pose at `angle` (radians) round the orbit: on the circle, `altitude` above
        the centre's height, with the boresight on the centre. The server's orbit mover sets the
        same pose (`UOrbitMoverComponent::PoseAt`), so a pose predicted here is the pose it sets."""
        cam_x = center_x + radius * math.cos(angle)
        cam_y = center_y + radius * math.sin(angle)
        cam_z = center_z + altitude
        dx = center_x - cam_x
        dy = center_y - cam_y
        dz = center_z - cam_z
        horizontal_dist = math.sqrt(dx * dx + dy * dy)
        pitch = math.degrees(math.atan2(dz, horizontal_dist))
        yaw = math.degrees(math.atan2(dy, dx))
        return carla.Transform(
            carla.Location(x=cam_x, y=cam_y, z=cam_z),
            carla.Rotation(pitch=pitch, yaw=yaw, roll=0.0),
        )

    def get_hud_info(self) -> dict[str, object]:
        """What the heads-up display shows. The angle is `current_angle`, computed here from the
        simulated clock; nothing is asked of the server."""
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

            angle = self.current_angle()
            info.update(
                {
                    "orbit_center": (self.center_x, self.center_y, self.center_z),
                    "center_latlon": latlon,
                    "radius": self.radius,
                    "radius_feet": self.radius * FT_PER_M,
                    "cam_altitude": self.cam_altitude,
                    "cam_altitude_feet": self.cam_altitude * FT_PER_M,
                    "orbit_speed": self.orbit_speed,
                    "angle": angle,
                    "orbit_progress": (angle / TWO_PI) * 100.0,
                }
            )

        return info
