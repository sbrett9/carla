"""A stare aimed at a point is actually aimed at it.

CARLA's frame is easy to get backwards: y points south, so north is -y, and yaw turns from east
towards south. A sign slipped anywhere in the stare geometry puts the camera on the wrong side of the
point, or looking away from it, or up at the sky -- and the picture is simply of somewhere else, with
nothing to say so. These tests pin the geometry to what the description promises in words (a camera
south of the point faces north and dips by atan(altitude / standoff)) and then to the property that
matters in every case: the boresight passes through the look-at point.
"""
from __future__ import annotations

import math
import sys
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.ChannelDescription import ChannelDescription  # noqa: E402
from carlacontrol.StareAim import StareAim  # noqa: E402

LOOK_AT = (120.0, -340.0, 12.5)


def _boresight_hits(aim: StareAim) -> tuple[float, float, float]:
    """Where the camera's forward axis reaches the look-at point's height.

    The forward vector is CARLA's own: (cos pitch cos yaw, cos pitch sin yaw, sin pitch).
    """
    pitch, yaw = math.radians(aim.pitch_deg), math.radians(aim.yaw_deg)
    forward = (math.cos(pitch) * math.cos(yaw), math.cos(pitch) * math.sin(yaw), math.sin(pitch))
    distance = (LOOK_AT[2] - aim.z_m) / forward[2]
    return (aim.x_m + forward[0] * distance, aim.y_m + forward[1] * distance,
            aim.z_m + forward[2] * distance)


def test_a_camera_south_of_the_point_faces_north_and_dips_by_the_altitude_over_the_standoff():
    aim = StareAim.looking_at(*LOOK_AT, altitude_m=300.0, standoff_m=400.0, bearing_deg=0.0)

    # South is +y in CARLA's frame, so a camera looking north stands at a larger y than the point.
    assert aim.x_m == pytest.approx(LOOK_AT[0])
    assert aim.y_m == pytest.approx(LOOK_AT[1] + 400.0)
    assert aim.z_m == pytest.approx(LOOK_AT[2] + 300.0)
    assert aim.yaw_deg == pytest.approx(-90.0)
    assert aim.pitch_deg == pytest.approx(-math.degrees(math.atan(300.0 / 400.0)))


def test_a_camera_looking_east_stands_west_of_the_point_with_yaw_zero():
    aim = StareAim.looking_at(*LOOK_AT, altitude_m=300.0, standoff_m=400.0, bearing_deg=90.0)

    assert aim.x_m == pytest.approx(LOOK_AT[0] - 400.0)
    assert aim.y_m == pytest.approx(LOOK_AT[1])
    assert aim.yaw_deg == pytest.approx(0.0)


@pytest.mark.parametrize("bearing_deg", [0.0, 37.0, 90.0, 135.0, 180.0, 225.0, 270.0, 359.0, -45.0])
@pytest.mark.parametrize("standoff_m", [50.0, 400.0, 2500.0])
def test_the_boresight_passes_through_the_look_at_point(bearing_deg, standoff_m):
    aim = StareAim.looking_at(*LOOK_AT, altitude_m=300.0, standoff_m=standoff_m,
                              bearing_deg=bearing_deg)

    hit = _boresight_hits(aim)
    assert hit == pytest.approx(LOOK_AT, abs=1e-6)
    assert math.hypot(aim.x_m - LOOK_AT[0], aim.y_m - LOOK_AT[1]) == pytest.approx(standoff_m)
    assert -180.0 < aim.yaw_deg <= 180.0


def test_no_standoff_looks_straight_down_with_the_bearing_at_the_top_of_the_picture():
    aim = StareAim.looking_at(*LOOK_AT, altitude_m=304.8, standoff_m=0.0, bearing_deg=0.0)

    assert (aim.x_m, aim.y_m) == pytest.approx(LOOK_AT[:2])
    assert aim.pitch_deg == pytest.approx(-90.0)
    assert aim.yaw_deg == pytest.approx(-90.0)


def test_a_stare_description_aims_through_its_own_fields():
    channel = ChannelDescription(stare_look_at_x_m=LOOK_AT[0], stare_look_at_y_m=LOOK_AT[1],
                                 stare_look_at_z_m=LOOK_AT[2], stare_altitude_m=250.0,
                                 stare_standoff_m=600.0, stare_bearing_deg=210.0)

    assert StareAim.from_channel(channel) == StareAim.looking_at(
        *LOOK_AT, altitude_m=250.0, standoff_m=600.0, bearing_deg=210.0)


def test_an_explicit_pose_is_held_exactly_as_given():
    channel = ChannelDescription(stare_x_m=1.0, stare_y_m=2.0, stare_z_m=3.0,
                                 stare_pitch_deg=-40.0, stare_yaw_deg=170.0)

    assert StareAim.from_channel(channel) == StareAim(1.0, 2.0, 3.0, -40.0, 170.0)


def test_an_orbit_has_no_stare_pose():
    with pytest.raises(ValueError):
        StareAim.from_channel(ChannelDescription(pattern="orbit", orbit_centre_x_m=0.0,
                                                 orbit_centre_y_m=0.0))
