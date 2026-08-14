"""Pose representation for CARLA simulation.

Represents a full 6-DOF pose with position (x, y, z) and orientation (pitch, yaw, roll).
"""

from __future__ import annotations

import carlanet


class Pose:
    """Pose with position and orientation.

    Represents a full 6-DOF pose with 3D position and 3-axis rotation (pitch, yaw, roll).
    Uses __slots__ for memory efficiency since poses are frequently created and updated.
    """

    __slots__ = ("x", "y", "z", "pitch", "yaw", "roll")

    def __init__(
        self,
        x: float = 0.0,
        y: float = 0.0,
        z: float = 0.0,
        pitch: float = 0.0,
        yaw: float = 0.0,
        roll: float = 0.0,
    ):
        """Initialize pose with position and orientation.

        Args:
            x: X coordinate in CARLA world space (meters)
            y: Y coordinate in CARLA world space (meters)
            z: Z coordinate in CARLA world space (meters)
            pitch: Pitch angle in degrees (rotation about Y axis)
            yaw: Yaw angle in degrees (rotation about Z axis)
            roll: Roll angle in degrees (rotation about X axis)
        """
        self.x = float(x)
        self.y = float(y)
        self.z = float(z)
        self.pitch = float(pitch)
        self.yaw = float(yaw)
        self.roll = float(roll)

    def copy(self) -> Pose:
        """Create a deep copy of this pose."""
        return Pose(self.x, self.y, self.z, self.pitch, self.yaw, self.roll)

    def update_from(self, other: Pose) -> None:
        """Update this pose from another pose.

        Args:
            other: Source pose to copy values from
        """
        self.x = other.x
        self.y = other.y
        self.z = other.z
        self.pitch = other.pitch
        self.yaw = other.yaw
        self.roll = other.roll

    def to_carla_transform(self) -> carlanet.Transform:
        """Convert to CARLA Transform.

        Returns:
            carlanet.Transform with this pose's position and rotation
        """

        return carlanet.Transform(
            carlanet.Location(x=self.x, y=self.y, z=self.z),
            carlanet.Rotation(pitch=self.pitch, yaw=self.yaw, roll=self.roll),
        )

    @classmethod
    def from_carla_transform(cls, transform: carlanet.Transform) -> Pose:
        """Create Pose from CARLA Transform.

        Args:
            transform: CARLA Transform object

        Returns:
            New Pose instance with transform's location and rotation
        """
        return cls(
            x=transform.location.x,
            y=transform.location.y,
            z=transform.location.z,
            pitch=transform.rotation.pitch,
            yaw=transform.rotation.yaw,
            roll=transform.rotation.roll,
        )

    def __repr__(self) -> str:
        """String representation for debugging."""
        return (
            f"Pose(x={self.x:.2f}, y={self.y:.2f}, z={self.z:.2f}, "
            f"pitch={self.pitch:.2f}, yaw={self.yaw:.2f}, roll={self.roll:.2f})"
        )
