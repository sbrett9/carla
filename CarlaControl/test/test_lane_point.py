"""A place's point in CARLA's frame, from a network's lane shapes: SUMO (x, y) is CARLA (x, -y), an
offset is measured in the lane's own length and scaled onto its polyline, and an edge is answered at
its start, middle or end."""
from __future__ import annotations

import sys
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.LanePoint import LanePoint  # noqa: E402

# One edge whose lane runs 100 m east then 100 m north, drawn by a 200 m polyline but 180 m long in
# SUMO's own measure, so an offset of 90 m (half the lane) lies 100 m along the polyline: at the bend.
NETWORK = """<net>
  <edge id="e1" from="a" to="b">
    <lane id="e1_0" index="0" speed="10" length="180" shape="0.0,0.0 100.0,0.0 100.0,100.0"/>
  </edge>
  <edge id="e2" from="b" to="c">
    <lane id="e2_0" index="0" speed="10" length="50" shape="100.0,100.0 150.0,100.0"/>
  </edge>
</net>"""


def test_an_offset_is_scaled_onto_the_polyline_and_y_is_flipped() -> None:
    points = LanePoint(NETWORK)
    assert points.along_lane("e1_0", 0.0) == (0.0, 0.0)
    assert points.along_lane("e1_0", 90.0) == (100.0, 0.0)
    assert points.along_lane("e1_0", 135.0) == (100.0, -50.0)
    assert points.along_lane("e1_0", 180.0) == (100.0, -100.0)


def test_an_offset_past_the_end_stops_at_the_end() -> None:
    assert LanePoint(NETWORK).along_lane("e1_0", 500.0) == (100.0, -100.0)


def test_an_edge_is_answered_at_its_start_middle_or_end() -> None:
    points = LanePoint(NETWORK)
    assert points.at_edge("e2", "start") == (100.0, -100.0)
    assert points.at_edge("e2", "middle") == (125.0, -100.0)
    assert points.at_edge("e2") == (150.0, -100.0)
    with pytest.raises(ValueError, match="start, middle or end"):
        points.at_edge("e2", "somewhere")


def test_a_place_is_read_by_its_lane_or_its_edge() -> None:
    points = LanePoint(NETWORK)
    assert points.of_place({"lane": "e1_0", "offset_m": 90.0}) == (100.0, 0.0)
    assert points.of_place({"edge": "e2"}, "middle") == (125.0, -100.0)
    with pytest.raises(ValueError, match="names a lane or an edge"):
        points.of_place({"point": [0, 0]})


def test_an_unknown_lane_is_refused_by_name() -> None:
    with pytest.raises(ValueError, match="e9_0 is not in this network"):
        LanePoint(NETWORK).along_lane("e9_0", 1.0)
