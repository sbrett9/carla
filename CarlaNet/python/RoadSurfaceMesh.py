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

    def interior_holes(self, strips: list[Strip], step: float = 0.5) -> tuple[float, int, float]:
        """Unpaved area fully enclosed by paving, in square metres.

        The measure that matters for a vehicle: a gap open to the outside is just the
        edge of the road network, but a gap surrounded by surface is a hole to drop
        through. Uncovered cells reachable from outside the sampled area are flood-filled
        away; what remains is enclosed.

        Returns ``(total_area, hole_count, largest_area)``.
        """
        quads = [
            (s.right[i][:2], s.left[i][:2], s.left[i + 1][:2], s.right[i + 1][:2])
            for s in strips
            for i in range(len(s.right) - 1)
        ]
        if not quads:
            return 0.0, 0, 0.0

        xs = [v[0] for q in quads for v in q]
        ys = [v[1] for q in quads for v in q]
        # One cell of margin so the flood fill always has an outside to start from.
        min_x, min_y = min(xs) - step, min(ys) - step
        cols = int((max(xs) + step - min_x) / step) + 1
        rows = int((max(ys) + step - min_y) / step) + 1

        # Rasterise each quad through its bounding box rather than testing every cell
        # against every quad.
        covered = [[False] * cols for _ in range(rows)]
        for quad in quads:
            qx = [v[0] for v in quad]
            qy = [v[1] for v in quad]
            for row in range(
                max(0, int((min(qy) - min_y) / step)), min(rows, int((max(qy) - min_y) / step) + 2)
            ):
                for col in range(
                    max(0, int((min(qx) - min_x) / step)),
                    min(cols, int((max(qx) - min_x) / step) + 2),
                ):
                    if covered[row][col]:
                        continue
                    point = (min_x + col * step, min_y + row * step)
                    if self._inside(quad, point):
                        covered[row][col] = True

        outside = [[False] * cols for _ in range(rows)]
        stack = [(r, c) for r in range(rows) for c in (0, cols - 1) if not covered[r][c]]
        stack += [(r, c) for c in range(cols) for r in (0, rows - 1) if not covered[r][c]]
        for r, c in stack:
            outside[r][c] = True
        while stack:
            r, c = stack.pop()
            for dr, dc in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                nr, nc = r + dr, c + dc
                if (
                    0 <= nr < rows
                    and 0 <= nc < cols
                    and not covered[nr][nc]
                    and not outside[nr][nc]
                ):
                    outside[nr][nc] = True
                    stack.append((nr, nc))

        # Label what is left: unpaved and unreachable from outside.
        seen = [[False] * cols for _ in range(rows)]
        areas = []
        cell_area = step * step
        for r in range(rows):
            for c in range(cols):
                if covered[r][c] or outside[r][c] or seen[r][c]:
                    continue
                blob, frontier = 0, [(r, c)]
                seen[r][c] = True
                while frontier:
                    br, bc = frontier.pop()
                    blob += 1
                    for dr, dc in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                        nr, nc = br + dr, bc + dc
                        if (
                            0 <= nr < rows
                            and 0 <= nc < cols
                            and not covered[nr][nc]
                            and not outside[nr][nc]
                            and not seen[nr][nc]
                        ):
                            seen[nr][nc] = True
                            frontier.append((nr, nc))
                areas.append(blob * cell_area)
        return sum(areas), len(areas), max(areas) if areas else 0.0

    def surface_disagreement(
        self, strips: list[Strip], step: float = 0.5
    ) -> tuple[int, float, float, float]:
        """Vertical spread where two surfaces cover the same plan position.

        Inside a junction the connectors overlap, and each samples its heights
        independently, so the surfaces disagree. Where the disagreement is a step rather
        than a blend it is a bump for a vehicle: collision uses these triangles directly.

        Returns ``(samples, median, p90, max)`` over cells covered more than once.
        """
        quads = []
        for s in strips:
            for i in range(len(s.right) - 1):
                corners = (s.right[i], s.left[i], s.left[i + 1], s.right[i + 1])
                plan = tuple(c[:2] for c in corners)
                quads.append((plan, sum(c[2] for c in corners) / 4.0))
        if not quads:
            return 0, 0.0, 0.0, 0.0

        xs = [v[0] for q, _ in quads for v in q]
        ys = [v[1] for q, _ in quads for v in q]
        min_x, min_y = min(xs), min(ys)
        cols = int((max(xs) - min_x) / step) + 1
        rows = int((max(ys) - min_y) / step) + 1

        lowest: dict[int, float] = {}
        highest: dict[int, float] = {}
        for plan, z in quads:
            qx = [v[0] for v in plan]
            qy = [v[1] for v in plan]
            for row in range(
                max(0, int((min(qy) - min_y) / step)), min(rows, int((max(qy) - min_y) / step) + 2)
            ):
                for col in range(
                    max(0, int((min(qx) - min_x) / step)),
                    min(cols, int((max(qx) - min_x) / step) + 2),
                ):
                    point = (min_x + col * step, min_y + row * step)
                    if not self._inside(plan, point):
                        continue
                    key = row * cols + col
                    if key not in lowest or z < lowest[key]:
                        lowest[key] = z
                    if key not in highest or z > highest[key]:
                        highest[key] = z

        gaps = sorted(highest[k] - lowest[k] for k in highest if highest[k] - lowest[k] > 1e-9)
        if not gaps:
            return 0, 0.0, 0.0, 0.0
        return (len(gaps), gaps[len(gaps) // 2], gaps[int(len(gaps) * 0.9)], gaps[-1])

    @staticmethod
    def _inside(quad, point) -> bool:
        sign = None
        for i in range(len(quad)):
            ax, ay = quad[i]
            bx, by = quad[(i + 1) % len(quad)]
            cross = (bx - ax) * (point[1] - ay) - (by - ay) * (point[0] - ax)
            if abs(cross) < 1e-12:
                continue
            positive = cross > 0
            if sign is None:
                sign = positive
            elif positive != sign:
                return False
        return True

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
