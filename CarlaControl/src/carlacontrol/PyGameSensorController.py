from __future__ import annotations

import logging
import math
import threading
import time
from typing import Any

import carlanet as carla
import pygame

from .Pose import Pose
from .SensorController import SensorController

from .SensorRig import SensorRig


class PyGameSensorController(SensorController):
    """Pygame-controlled camera mover for SensorRig.

    Manages camera movement from pygame controls and handles asynchronous
    transform updates and ground elevation tracking for AGL calculations.
    """

    def __init__(self, sensor_rig: SensorRig, world: carla.World, initial_pose: Pose, speed: float = 10.0):
        """Initialize pygame mover.

        Args:
            sensor_rig: SensorRig instance to control
            world: CARLA world for ground elevation queries
            initial_pose: Initial camera pose
            pygame_interface: PyGameInterface instance
            speed: Base movement speed in m/s
        """
        super().__init__(sensor_rig)
        self.world = world
        self.pose = initial_pose.copy()
        self.speed = speed
        self.boost_multiplier = 3.0

        self._pending_transform = None
        self._stop = False
        self._thread = None
        self._last_agl_refresh = 0.0
        self._sync_mode = False
        self.logger = logging.getLogger(__name__)
        
        self.logger.info(
            f"pygame sensor controller initialized: speed={self.speed:.1f} m/s, "
            f"pose=({self.pose.x:.1f}, {self.pose.y:.1f}, {self.pose.z:.1f})"
        )

    def start_async(self):
        """Start the background mover thread for async mode."""
        if self._thread is not None:
            self.logger.info("async mode already running")
            return
        self._stop = False
        self._thread = threading.Thread(target=self._mover_loop, daemon=True)
        self._thread.start()
        self.logger.info("async mode started")

    def stop_async(self):
        """Stop the background mover thread."""
        if self._thread is None:
            return
        self._stop = True
        self._thread.join(timeout=1.0)
        self._thread = None
        self.logger.info("async mode stopped")

    def _mover_loop(self):
        """Background thread loop: applies pending transforms and refreshes ground Z."""
        last_transform = None
        while not self._stop:
            tf = self._pending_transform
            if tf is not None and tf is not last_transform:
                last_transform = tf
                self.set_object_transform(tf)

            now = time.time()
            if now - self._last_agl_refresh > 0.3:
                self._last_agl_refresh = now
                current_tf = self.controlled_object.get_current_transform()
                self._refresh_ground_z(current_tf.location.x, current_tf.location.y)

            time.sleep(0.04)

    def _refresh_ground_z(self, px: float, py: float):
        """Update ground Z for AGL calculations.

        Prefers drape terrain (non-physics grid lookup) and falls back to raycast.

        Args:
            px: World X coordinate
            py: World Y coordinate
        """
        # try drape first (non-physics grid lookup)
        try:
            ge = self.world.drape_ground_elevation(px, py)
        except Exception as e:
            self.logger.warning(f"drape ground elevation failed at ({px:.1f}, {py:.1f}): {e}")
            ge = None

        if ge is not None:
            try:
                origin_h = self.world.get_cesium_origin()[2]
                self.controlled_object.set_ground_z(ge - origin_h)
                self.logger.debug(f"ground z updated via drape: {ge - origin_h:.1f}m at ({px:.1f}, {py:.1f})")
            except Exception as e:
                self.logger.warning(f"failed to set ground z from drape: {e}")
            return

        # fallback to raycast
        try:
            current_tf = self.controlled_object.get_current_transform()
            start = current_tf.location.z + 100.0
            gz = self.world.ground_z_below(px, py, start, search=start + 6000.0)
            self.controlled_object.set_ground_z(gz)
            self.logger.debug(f"ground z updated via raycast: {gz:.1f}m at ({px:.1f}, {py:.1f})")
        except Exception as e:
            self.logger.warning(f"raycast ground z failed at ({px:.1f}, {py:.1f}): {e}")

    def move_object_to_position(self, position: Pose) -> None:
        """Move the controlled object to the specified position.

        Args:
            position: Pose object with position and orientation
        """
        self._pending_transform = position.to_carla_transform()

    def set_object_transform(self, tf: carla.Transform) -> None:
        """Set the controlled object's transform immediately.

        Args:
            tf: CARLA Transform object
        """
        self.controlled_object.set_transform(tf)

    def update_movement(self, dt: float, events: dict) -> tuple[bool, tuple[float, float, float]]:
        """Update camera movement from pygame controls.

        Args:
            dt: Delta time in seconds
            events: Events dict from PygameInterface.process_events()

        Returns:
            Tuple of (moved, agl_pose_tuple) where moved is True if position changed
            and agl_pose_tuple is (x, y, z) for AGL tracking
        """
        moved = False

        # update look from mouse dx/dy
        if events["mouse_look_delta"]:
            dx, dy = events["mouse_look_delta"]
            self.pose.yaw += dx
            self.pose.pitch = max(-89.0, min(89.0, self.pose.pitch - dy))
            moved = True

        # update speed from mouse wheel
        if events["mouse_wheel_delta"]:
            old_speed = self.speed
            self.speed = max(1.0, self.speed + events["mouse_wheel_delta"] * 5.0)
            self.logger.debug(f"speed changed: {old_speed:.1f} -> {self.speed:.1f} m/s")

        movement = self.get_flying_movement(dt, events["pressed_keys"])

        if movement["dx"] or movement["dy"] or movement["dz"]:
            self.pose.x += movement["dx"]
            self.pose.y += movement["dy"]
            self.pose.z = max(2.0, self.pose.z + movement["dz"])
            moved = True

        return moved, (self.pose.x, self.pose.y, self.pose.z)

    def get_flying_movement(self, dt: float, keys: dict) -> dict[str, float]:
        """Calculate camera movement from WASD/EQ keys.

        Args:
            dt: Delta time in seconds
            keys: Pygame keys dict

        Returns:
            Dictionary with dx, dy, dz movement deltas
        """
        boost = self.boost_multiplier if (keys[pygame.K_LSHIFT] or keys[pygame.K_RSHIFT]) else 1.0
        step = self.speed * boost * dt

        yr = math.radians(self.pose.yaw)
        pr = math.radians(self.pose.pitch)
        fwd = (
            math.cos(yr) * math.cos(pr),
            math.sin(yr) * math.cos(pr),
            math.sin(pr),
        )
        right = (-math.sin(yr), math.cos(yr), 0.0)

        dx = dy = dz = 0.0
        if keys[pygame.K_w]:
            dx += fwd[0] * step
            dy += fwd[1] * step
            dz += fwd[2] * step
        if keys[pygame.K_s]:
            dx -= fwd[0] * step
            dy -= fwd[1] * step
            dz -= fwd[2] * step
        if keys[pygame.K_d]:
            dx += right[0] * step
            dy += right[1] * step
        if keys[pygame.K_a]:
            dx -= right[0] * step
            dy -= right[1] * step
        if keys[pygame.K_e]:
            dz += step
        if keys[pygame.K_q]:
            dz -= step

        return {"dx": dx, "dy": dy, "dz": dz}

    def apply_transform_sync(self):
        """Apply current pose transform immediately (sync mode)."""
        tf = self.pose.to_carla_transform()
        self.controlled_object.set_transform(tf)

    def apply_transform_async(self):
        """Queue current pose transform for async application."""
        self._pending_transform = self.pose.to_carla_transform()

    def refresh_ground_z_if_needed(self, last_refresh_time: float) -> float:
        """Refresh ground Z if enough time has passed.

        Args:
            last_refresh_time: Last time ground Z was refreshed

        Returns:
            Updated last_refresh_time
        """
        now = time.time()
        if now - last_refresh_time > 0.3:
            self._refresh_ground_z(self.pose.x, self.pose.y)
            return now
        return last_refresh_time
