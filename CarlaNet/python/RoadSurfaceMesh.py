"""Offline reconstruction of the road mesh CARLA builds from an OpenDRIVE map.

`MeshFactory` walks each lane at a fixed station step and emits the two lane-edge
positions at every step, so a lane becomes a quad strip. Those strips are combined with
``Mesh::operator+=``, which appends the vertex buffers and offsets the indices — it never
welds, never deduplicates and never shares a vertex between strips. A junction is
therefore one mesh *object* built from many overlapping strips rather than one surface.

This module reproduces those vertices from the .xodr alone, so the seams between strips
can be measured without the engine. See
``Docs/CAT_Research/Findings/21_Road_Elevation_Profile_Continuity.md`` §18.

Faithful to `Lane::GetCornerPositions` (`LibCarla/source/carla/road/Lane.cpp:206-268`):
the lateral offset accumulates the widths of the lanes between the reference line and
this one, driving lanes inside a junction are widened by the extra-width parameter, the
Y axis is negated for Unreal, and sidewalks are lifted 0.1524 m.
"""

from __future__ import annotations

import logging
import math
from typing import NamedTuple

from ElevationProfileFitter import ElevationProfileFitter
from ElevationProfileProbe import ElevationProfileProbe, Road

logger = logging.getLogger(__name__)

#: Sidewalk lift RoadRunner omits, hard-coded in Lane::GetCornerPositions.
SIDEWALK_LIFT_METRES = 0.1524


class Strip(NamedTuple):
    """One lane's quad strip: paired left/right edge vertices along the lane."""

    road_id: str
    lane_id: int
    lane_type: str
    junction: str
    right: list[tuple[float, float, float]]
    left: list[tuple[float, float, float]]

    @property
    def vertices(self) -> list[tuple[float, float, float]]:
        """Every vertex of the strip, in the order MeshFactory emits them."""
        out: list[tuple[float, float, float]] = []
        for r, ln in zip(self.right, self.left, strict=True):
            out.append(r)
            out.append(ln)
        return out


class RoadSurfaceMesh:
    """Rebuilds the road mesh vertices of one map and measures the seams between strips.

    The reconstruction is of vertex *positions* only — enough to find duplicated and
    near-coincident vertices, which is what determines whether the surface reads as one
    piece or as overlapping plates.
    """

    #: MeshFactory road_param.resolution — the station step vertices are emitted at.
    VERTEX_DISTANCE_METRES = 2.0

    #: OpendriveGenerationParameters.additional_width, widening driving lanes in junctions.
    EXTRA_LANE_WIDTH_METRES = 0.6

    def __init__(
        self,
        probe: ElevationProfileProbe,
        vertex_distance: float | None = None,
        extra_lane_width: float | None = None,
    ) -> None:
        self.probe = probe
        self.vertex_distance = vertex_distance or self.VERTEX_DISTANCE_METRES
        self.extra_lane_width = (
            self.EXTRA_LANE_WIDTH_METRES if extra_lane_width is None else extra_lane_width
        )
        self.strips: list[Strip] = []
        for road in probe.roads.values():
            self.strips.extend(self._build_road(road))

    # ── reconstruction ───────────────────────────────────────────────────────

    def _build_road(self, road: Road) -> list[Strip]:
        if not road.geometries or not road.lane_sections or road.length <= 0.0:
            return []
        stations = self._stations(road.length)
        strips = []
        for lane_id, lane_type in self._lane_ids(road):
            right, left = [], []
            for s in stations:
                corners = self._corners(road, s, lane_id)
                if corners is None:
                    break
                right.append(corners[0])
                left.append(corners[1])
            if len(right) >= 2:
                strips.append(Strip(road.id, lane_id, lane_type, road.junction, right, left))
        return strips

    def _stations(self, length: float) -> list[float]:
        """Stations MeshFactory emits vertices at: fixed step, then the exact road end."""
        stations, s = [], 0.0
        while s < length - 1e-6:
            stations.append(s)
            s += self.vertex_distance
        stations.append(length)
        return stations

    @staticmethod
    def _lane_ids(road: Road) -> list[tuple[int, str]]:
        section = road.lane_sections[0]
        return [(lane.id, lane.type) for lane in section.lanes]

    def _corners(
        self, road: Road, s: float, lane_id: int
    ) -> tuple[tuple[float, float, float], tuple[float, float, float]] | None:
        """The two edge positions of a lane at a station, as MeshFactory computes them."""
        section = road.lane_section_at(s)
        if section is None:
            return None
        lane = next((x for x in section.lanes if x.id == lane_id), None)
        if lane is None:
            return None

        s_offset = s - section.s
        # Widths of the lanes between the reference line and this one, on its own side.
        inner = 0.0
        for other in section.lanes:
            # A lane's inner edge sits beyond every lane between it and the reference
            # line, on its own side.
            inside_right = lane_id < 0 and lane_id < other.id < 0
            inside_left = lane_id > 0 and 0 < other.id < lane_id
            if inside_right or inside_left:
                inner += other.width_at(s_offset)
        half = lane.width_at(s_offset) / 2.0
        if self.extra_lane_width and road.is_connector and lane.type == "driving":
            half += self.extra_lane_width
        # Left lanes run the other way along the lateral axis.
        centre = (inner + half) if lane_id < 0 else -(inner + half)
        edge = half if lane_id < 0 else -half

        position = road.position(s)
        heading = road.heading(s)
        if position is None or heading is None:
            return None
        z = ElevationProfileFitter.evaluate(road.records, s) or 0.0
        z += SIDEWALK_LIFT_METRES if lane.type == "sidewalk" else 0.0
        # GetDirectedPointIn applies the road's lane offset before the lane widths.
        offset = -road.lane_offset_at(s)

        def place(lateral: float) -> tuple[float, float, float]:
            # ApplyLateralOffset: normal = (sin h, -cos h); the Y axis is then negated.
            x = position[0] + math.sin(heading) * (lateral + offset)
            y = position[1] - math.cos(heading) * (lateral + offset)
            return (x, -y, z)

        return place(centre + edge), place(centre - edge)

    # ── measurements ─────────────────────────────────────────────────────────

    def seam_report(self, weld_tolerance: float = 0.05) -> dict[str, float | int]:
        """How much of the surface is duplicated rather than shared.

        Counts vertices that another *strip* places at the same position within
        ``weld_tolerance``. Those are the seams: today each strip owns its own copy, so
        the surfaces overlap instead of meeting, and the depth buffer decides which one
        is drawn.
        """
        buckets: dict[tuple[int, int, int], list[tuple[tuple[float, float, float], int]]] = {}
        cell = max(weld_tolerance, 1e-3)
        total = 0
        for index, strip in enumerate(self.strips):
            for vertex in strip.vertices:
                total += 1
                key = (int(vertex[0] // cell), int(vertex[1] // cell), int(vertex[2] // cell))
                buckets.setdefault(key, []).append((vertex, index))

        duplicated = 0
        cross_strip = 0
        separations: list[float] = []
        for key, entries in buckets.items():
            neighbours: list[tuple[tuple[float, float, float], int]] = []
            for dx in (-1, 0, 1):
                for dy in (-1, 0, 1):
                    for dz in (-1, 0, 1):
                        neighbours.extend(buckets.get((key[0] + dx, key[1] + dy, key[2] + dz), []))
            for vertex, index in entries:
                partners = [
                    (other, j)
                    for other, j in neighbours
                    if math.dist(vertex, other) <= weld_tolerance and (other, j) != (vertex, index)
                ]
                if not partners:
                    continue
                duplicated += 1
                if any(j != index for _, j in partners):
                    cross_strip += 1
                    separations.append(
                        min(math.dist(vertex, other) for other, j in partners if j != index)
                    )

        separations.sort()
        return {
            "strips": len(self.strips),
            "vertices": total,
            "duplicated": duplicated,
            "cross_strip": cross_strip,
            "cross_strip_fraction": cross_strip / total if total else 0.0,
            "median_separation": separations[len(separations) // 2] if separations else 0.0,
            "max_separation": separations[-1] if separations else 0.0,
        }
