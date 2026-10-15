"""A point on a SUMO network's lane, in CARLA's frame, for siting a camera over a place.

A scenario names a place by a lane and an offset along it, or by an edge. A run file names the point
a camera looks at in metres, in CARLA's frame: x east, y south, so SUMO's (x, y) is CARLA's (x, -y).
This turns the one into the other from the network text a world package carries, the same text the
compiler resolves places against, so a run file written beside a scenario looks where its places are.

SUMO measures a position along a lane in the lane's own length, which can differ from the length of
the polyline that draws it (junction trimming, say), so an offset is scaled onto the polyline before
it is walked.
"""
from __future__ import annotations

import math
import xml.etree.ElementTree as ET


class LanePoint:
    """The lane geometry of one network, read once, answering where a lane position lies."""

    def __init__(self, network_text: str) -> None:
        self._root = ET.fromstring(network_text)

    def along_lane(self, lane_id: str, offset_m: float) -> tuple[float, float]:
        """Where `offset_m` along the lane lies, in CARLA's frame, to a tenth of a metre."""
        lane = self._lane(lane_id)
        shape = [tuple(float(v) for v in point.split(",")[:2]) for point in lane.get("shape").split()]
        segments = list(zip(shape, shape[1:], strict=False))
        if not segments:
            raise ValueError(f"lane {lane_id} has no shape")
        lengths = [math.dist(a, b) for a, b in segments]
        along = offset_m * sum(lengths) / float(lane.get("length"))
        for (a, b), length in zip(segments, lengths, strict=True):
            if along <= length or (a, b) == segments[-1]:
                t = min(1.0, along / length) if length else 0.0
                return round(a[0] + t * (b[0] - a[0]), 1), round(-(a[1] + t * (b[1] - a[1])), 1)
            along -= length
        raise ValueError(f"lane {lane_id} has no shape")

    def at_edge(self, edge_id: str, where: str = "end") -> tuple[float, float]:
        """A point on the edge's first lane: its `start`, its `middle` or its `end`, the end being
        where a vehicle routed to the edge arrives."""
        lane_id = f"{edge_id}_0"
        length = float(self._lane(lane_id).get("length"))
        offset = {"start": 0.0, "middle": length / 2.0, "end": length}
        if where not in offset:
            raise ValueError(f"'{where}' is not a point on an edge: start, middle or end")
        return self.along_lane(lane_id, offset[where])

    def of_place(self, place: dict, where: str = "end") -> tuple[float, float]:
        """Where a specification's place lies: a `lane` at its `offset_m`, or an `edge` at `where`."""
        if "lane" in place:
            return self.along_lane(place["lane"], float(place.get("offset_m", 0.0)))
        if "edge" in place:
            return self.at_edge(place["edge"], where)
        raise ValueError(f"a place names a lane or an edge, not {sorted(place)}")

    def _lane(self, lane_id: str) -> ET.Element:
        edge_id = lane_id.rsplit("_", 1)[0]
        lane = self._root.find(f"edge[@id='{edge_id}']/lane[@id='{lane_id}']")
        if lane is None:
            raise ValueError(f"lane {lane_id} is not in this network")
        return lane
