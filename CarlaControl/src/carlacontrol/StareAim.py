"""The pose a stare channel holds, worked out from how the channel declares it."""

from __future__ import annotations

import math
from dataclasses import dataclass

from .ChannelDescription import ChannelDescription


@dataclass(frozen=True)
class StareAim:
    """A fixed camera pose in CARLA's frame: metres, x east, y south; yaw 0 faces east.

    CARLA's yaw turns from +x towards +y, which with y pointing south is clockwise seen from above,
    the same sense as a compass. A compass bearing therefore maps to a CARLA yaw by one constant:
    yaw = bearing - 90, so north (bearing 0) is yaw -90 and east (bearing 90) is yaw 0.
    """

    x_m: float
    y_m: float
    z_m: float
    pitch_deg: float
    yaw_deg: float

    @classmethod
    def looking_at(
        cls,
        look_at_x_m: float,
        look_at_y_m: float,
        look_at_z_m: float,
        altitude_m: float,
        standoff_m: float,
        bearing_deg: float,
    ) -> StareAim:
        """Stand back from a point along a bearing, above it, and look straight at it.

        The camera is `standoff_m` from the point horizontally, on the side opposite the bearing it
        looks along, and `altitude_m` above it, so the boresight passes through the point and dips
        by atan(altitude / standoff) below the horizon. With no standoff it looks straight down and
        the bearing sets which way is up in the picture. This is the geometry `camera_transform` in
        `CarlaNet/python/run_sumo_drive.py` computes for its recording camera, with the look-at
        point's own height added, and with the bearing still setting the yaw when there is no
        standoff (where `camera_transform`'s yaw falls to 0).

        Args:
            look_at_x_m: The point's x, metres east of the origin.
            look_at_y_m: The point's y, metres south of the origin.
            look_at_z_m: The point's height in CARLA's frame, metres.
            altitude_m: How far above the point the camera sits, metres.
            standoff_m: Horizontal distance from the camera to the point, metres.
            bearing_deg: The compass direction the camera looks along, degrees clockwise from north.
        """
        bearing = math.radians(bearing_deg)
        # The horizontal unit vector a bearing points along, in a frame whose y axis points south.
        look_x = math.sin(bearing)
        look_y = -math.cos(bearing)
        return cls(
            x_m=look_at_x_m - standoff_m * look_x,
            y_m=look_at_y_m - standoff_m * look_y,
            z_m=look_at_z_m + altitude_m,
            pitch_deg=-math.degrees(math.atan2(altitude_m, standoff_m)),
            yaw_deg=cls.yaw_for_bearing(bearing_deg),
        )

    @classmethod
    def from_channel(cls, channel: ChannelDescription) -> StareAim:
        """The pose a stare channel declares, in whichever of its two forms it was declared.

        Raises:
            ValueError: when the channel is not a stare.
        """
        if channel.pattern != ChannelDescription.STARE:
            raise ValueError(f"a {channel.pattern} channel has no stare pose")
        if channel.declares_pose():
            return cls(
                x_m=channel.stare_x_m,
                y_m=channel.stare_y_m,
                z_m=channel.stare_z_m,
                pitch_deg=channel.stare_pitch_deg,
                yaw_deg=channel.stare_yaw_deg,
            )
        return cls.looking_at(
            channel.stare_look_at_x_m,
            channel.stare_look_at_y_m,
            channel.stare_look_at_z_m,
            channel.stare_altitude_m,
            channel.stare_standoff_m,
            channel.stare_bearing_deg,
        )

    @staticmethod
    def yaw_for_bearing(bearing_deg: float) -> float:
        """CARLA's yaw for a compass bearing, folded into -180 < yaw <= 180."""
        yaw = (bearing_deg - 90.0) % 360.0
        return yaw - 360.0 if yaw > 180.0 else yaw

    def describe(self) -> str:
        """One line for a log."""
        return (f"({self.x_m:.1f}, {self.y_m:.1f}, {self.z_m:.1f}) m, "
                f"pitch {self.pitch_deg:.1f}, yaw {self.yaw_deg:.1f}")
