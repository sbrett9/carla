"""Builds one continuous drivable surface for a junction from its connector ribbons.

CARLA meshes a junction as one quad strip per connector lane. The strips overlap, each
carries its own independently sampled heights, and nothing covers the asphalt between
them — so the junction is simultaneously stacked (surfaces disagreeing by up to 1.27 m on
`Arapahoe_I25`) and holed (347 m² of enclosed gaps map-wide). The two mesh-side
compensations upstream applies — widening connectors so they overlap, and Laplacian
smoothing to blend the overlaps — both exist because of that, and both become unnecessary
once the junction is a single surface.

This resolves the junction to **one height per plan position per layer** and triangulates
the covered area once. Layers matter: a junction footprint can contain a grade separation,
and at `Arapahoe_I25` junction 144 the I-25 deck runs 6.8 m above the road beneath it.
Collapsing those into one surface would bury the underpass, so surfaces are clustered by
height first and each layer triangulated separately.

Prototype for the engine-side change in `MeshFactory`; kept here so the result can be
measured against the same baselines the ribbons were measured against. See
``Docs/CAT_Research/Findings/21_Road_Elevation_Profile_Continuity.md`` §18.
"""

from __future__ import annotations

import logging
from typing import NamedTuple

from RoadSurfaceMesh import RoadSurfaceMesh, Strip

logger = logging.getLogger(__name__)


class Surface(NamedTuple):
    """One triangulated layer: welded vertices and the triangles indexing them."""

    vertices: list[tuple[float, float, float]]
    triangles: list[tuple[int, int, int]]

    @property
    def area(self) -> float:
        total = 0.0
        for a, b, c in self.triangles:
            va, vb, vc = self.vertices[a], self.vertices[b], self.vertices[c]
            total += (
                abs((vb[0] - va[0]) * (vc[1] - va[1]) - (vc[0] - va[0]) * (vb[1] - va[1])) / 2.0
            )
        return total


class JunctionSurface:
    """Resolves a junction's overlapping ribbons into continuous triangulated layers."""

    #: Cell size of the resolved height field, in metres.
    CELL_METRES = 0.5

    #: Height difference above which two overlapping surfaces are separate layers rather
    #: than the same surface sampled twice. Well above the worst same-layer disagreement
    #: measured (1.27 m) and well below the shallowest grade separation (6.8 m).
    LAYER_SEPARATION_METRES = 3.0

    #: How far a paved gap may sit from the surface around it, in metres. The layer
    #: separation is the wrong tolerance here: it asks whether two cells belong to the
    #: same sheet, which a deck and the ramp beside it can, while this asks whether
    #: bridging them would invent a slope. Tight enough that a gap beside a deck edge is
    #: left open rather than ramped into.
    FILL_TOLERANCE_M = 0.5

    #: How far paving must lie in every direction for a gap to count as interior, in
    #: metres. Comfortably spans the interior of the intersections measured while falling
    #: far short of the space between roads, which is what keeps a median unpaved.
    MAX_GAP_SPAN_M = 20.0

    #: Neighbour-averaging passes over the resolved height field, removing the flips
    #: left where the lower of two overlapping surfaces changes from cell to cell.
    RELAX_PASSES = 4

    def __init__(
        self,
        cell: float | None = None,
        layer_separation: float | None = None,
        max_gap_span: float | None = None,
        fill_tolerance: float | None = None,
        relax_passes: int | None = None,
    ) -> None:
        self.cell = cell or self.CELL_METRES
        self.layer_separation = layer_separation or self.LAYER_SEPARATION_METRES
        self.max_gap_span = self.MAX_GAP_SPAN_M if max_gap_span is None else max_gap_span
        self.fill_tolerance = self.FILL_TOLERANCE_M if fill_tolerance is None else fill_tolerance
        self.relax_passes = self.RELAX_PASSES if relax_passes is None else relax_passes

    # ── layers ───────────────────────────────────────────────────────────────

    def build(self, strips: list[Strip]) -> list[Surface]:
        """One surface per height layer covered by these strips."""
        cells = self._sample(strips)
        junction_cells = self._sample_junction_cells(strips)
        layers = [layer for layer in self._split_layers(cells) if layer]
        return [
            self._triangulate(self._relax(self._close_gaps(layer, junction_cells)))
            for layer in layers
        ]

    def _sample_junction_cells(self, strips: list[Strip]) -> set[tuple[int, int]]:
        """Cells a junction connector covers, as opposed to a road between junctions."""
        owned: set[tuple[int, int]] = set()
        for strip in strips:
            if strip.junction == "-1":
                continue
            for i in range(len(strip.right) - 1):
                corners = (strip.right[i], strip.left[i], strip.left[i + 1], strip.right[i + 1])
                plan = tuple(c[:2] for c in corners)
                xs = [v[0] for v in plan]
                ys = [v[1] for v in plan]
                for col in range(int(min(xs) / self.cell) - 1, int(max(xs) / self.cell) + 2):
                    for row in range(int(min(ys) / self.cell) - 1, int(max(ys) / self.cell) + 2):
                        if RoadSurfaceMesh._inside(plan, (col * self.cell, row * self.cell)):
                            owned.add((col, row))
        return owned

    def _relax(self, layer: dict[tuple[int, int], float]) -> dict[tuple[int, int], float]:
        """Take the flips out of the resolved height field.

        Where two connectors overlap and disagree, the lower of the two wins — and which
        one is lower can change from cell to cell, leaving a field that jumps by the
        amount the two disagreed. Measured at up to 0.47 m across a single 0.5 m cell,
        which is a wall rather than a road.

        Averaging each cell against its neighbours removes those flips. This is not the
        junction smoothing it replaces: that one blended separate overlapping ribbons
        into each other and left the mesh disagreeing with the profile. This runs inside
        one already-single-valued surface, so it cannot reintroduce a stack, and it
        stays within a layer, so a deck is never pulled towards the road beneath it.
        """
        relaxed = dict(layer)
        for _ in range(self.relax_passes):
            updated: dict[tuple[int, int], float] = {}
            for (col, row), height in relaxed.items():
                total, count = height, 1
                for dc, dr in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    neighbour = relaxed.get((col + dc, row + dr))
                    if neighbour is not None:
                        total += neighbour
                        count += 1
                updated[(col, row)] = total / count
            relaxed = updated
        return relaxed

    def _close_gaps(
        self,
        layer: dict[tuple[int, int], float],
        junction_cells: set[tuple[int, int]],
    ) -> dict[tuple[int, int], float]:
        """Pave the gaps a junction's turning paths leave enclosed between them.

        OpenDRIVE models a junction as turning paths — a u-turn, some left turns, the
        straight-throughs, the rights — and between them sits asphalt no lane ever
        covers. A vehicle drops through it, since collision uses these triangles.

        The test is enclosure, not distance. A gap the turning paths surround is interior
        to the intersection and gets paved whatever its shape; a gap that opens outward is
        not, which is what keeps the median between two approach carriageways unpaved —
        it reaches the space outside the network, so the flood fill from outside finds it.
        A convex hull of the junction cannot make that distinction: measured on
        `Arapahoe_I25`, the median beside junction 114 lies inside that junction's hull.

        Regions above ``max_gap_area`` are left alone. The city blocks a road network
        rings are enclosed by the same test, and they are four orders of magnitude larger
        than the largest interior gap measured, so the two never overlap.
        """
        filled = dict(layer)
        remaining = set(self._enclosed_cells(layer, junction_cells))
        # Work inwards from the surface around the gap, so each cell takes the height of
        # what it already touches.
        while remaining:
            progressed = False
            for key in sorted(remaining):
                col, row = key
                around = [
                    filled[(col + dc, row + dr)]
                    for dc, dr in ((1, 0), (-1, 0), (0, 1), (0, -1))
                    if (col + dc, row + dr) in filled
                ]
                if not around:
                    continue
                height = sum(around) / len(around)
                if self._agrees(filled, key, height, self.fill_tolerance):
                    filled[key] = height
                remaining.discard(key)
                progressed = True
            if not progressed:
                break
        return filled

    def _enclosed_cells(
        self,
        layer: dict[tuple[int, int], float],
        junction_cells: set[tuple[int, int]],
    ) -> list[tuple[int, int]]:
        """Unpaved cells the surface surrounds, found without flooding the whole plane.

        A cell is interior when paving lies within reach in all four directions. That is
        a local test costing a bounded ray per direction, so it scales with the gaps
        rather than with the sheet's bounding box — which for a whole road network is
        mostly the empty space around it.

        It is also what separates an intersection's interior from a median. The interior
        of a junction is ringed by turning paths, so every ray hits one; a median between
        two approach carriageways runs away down the road, so the ray along it reaches
        the limit and finds nothing. A convex hull of the junction cannot tell them apart
        — measured on `Arapahoe_I25`, the median beside junction 114 lies inside that
        junction's own hull.
        """
        reach = max(1, int(self.max_gap_span / self.cell))
        candidates: set[tuple[int, int]] = set()
        for col, row in layer:
            for dc, dr in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                key = (col + dc, row + dr)
                if key not in layer:
                    candidates.add(key)

        interior: list[tuple[int, int]] = []
        checked: set[tuple[int, int]] = set()
        frontier = list(candidates)
        while frontier:
            cell = frontier.pop()
            if cell in checked or cell in layer:
                continue
            checked.add(cell)
            if not self._surrounded(layer, cell, reach, junction_cells):
                continue
            interior.append(cell)
            # A gap is usually more than one cell wide, so its neighbours are candidates
            # too even though they do not touch paving themselves.
            for dc, dr in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                key = (cell[0] + dc, cell[1] + dr)
                if key not in layer and key not in checked:
                    frontier.append(key)
        return interior

    def _surrounded(
        self,
        layer: dict[tuple[int, int], float],
        cell: tuple[int, int],
        reach: int,
        junction_cells: set[tuple[int, int]],
    ) -> bool:
        """True when a junction's own paving lies within ``reach`` in all four directions.

        Reaching *any* paving is not enough. A triangular island between a slip lane and
        the road it leaves, a median, the outside of a bend — all have road on every side
        and would be paved over, which is what filled them in the surface before this.
        Requiring the paving to belong to a junction connector is what confines the fill
        to the inside of an intersection, where the turning paths genuinely ring a piece
        of asphalt no lane covers.
        """
        col, row = cell
        for dc, dr in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            hit = None
            for step in range(1, reach + 1):
                key = (col + dc * step, row + dr * step)
                if key in layer:
                    hit = key
                    break
            if hit is None or hit not in junction_cells:
                return False
        return True

    def _agrees(
        self,
        layer: dict[tuple[int, int], float],
        key: tuple[int, int],
        height: float,
        tolerance: float | None = None,
    ) -> bool:
        """True when a height fits every neighbour already held, diagonals included."""
        limit = self.layer_separation if tolerance is None else tolerance
        col, row = key
        return not any(
            abs(layer[(col + dc, row + dr)] - height) > limit
            for dc in (-1, 0, 1)
            for dr in (-1, 0, 1)
            if (dc or dr) and (col + dc, row + dr) in layer
        )

    def _sample(self, strips: list[Strip]) -> dict[tuple[int, int], list[float]]:
        """Every height each strip contributes to each cell of the grid."""
        cells: dict[tuple[int, int], list[float]] = {}
        for strip in strips:
            for i in range(len(strip.right) - 1):
                corners = (strip.right[i], strip.left[i], strip.left[i + 1], strip.right[i + 1])
                plan = tuple(c[:2] for c in corners)
                height = sum(c[2] for c in corners) / 4.0
                xs = [v[0] for v in plan]
                ys = [v[1] for v in plan]
                for col in range(int(min(xs) / self.cell) - 1, int(max(xs) / self.cell) + 2):
                    for row in range(int(min(ys) / self.cell) - 1, int(max(ys) / self.cell) + 2):
                        point = (col * self.cell, row * self.cell)
                        if RoadSurfaceMesh._inside(plan, point):
                            cells.setdefault((col, row), []).append(height)
        return cells

    def _split_layers(
        self, cells: dict[tuple[int, int], list[float]]
    ) -> list[dict[tuple[int, int], float]]:
        """Separate stacked surfaces, so a deck is not merged with the road beneath it.

        Heights in a cell are clustered by gap; each cluster joins the layer its
        neighbours already occupy, so a ramp climbing away from the ground stays attached
        to the deck it leads to rather than to the road it crosses.
        """
        clustered: dict[tuple[int, int], list[float]] = {}
        for key, heights in cells.items():
            ordered = sorted(heights)
            groups, current = [], [ordered[0]]
            for height in ordered[1:]:
                if height - current[-1] > self.layer_separation:
                    groups.append(current)
                    current = [height]
                else:
                    current.append(height)
            groups.append(current)
            # One representative height per cluster: the lowest, since a surface model
            # can place a sample above the ground but never below it.
            clustered[key] = [min(g) for g in groups]

        layers: list[dict[tuple[int, int], float]] = []
        assigned: dict[tuple[int, int], list[bool]] = {
            k: [False] * len(v) for k, v in clustered.items()
        }
        for seed_key, heights in clustered.items():
            for index in range(len(heights)):
                if assigned[seed_key][index]:
                    continue
                layer: dict[tuple[int, int], float] = {}
                frontier = [(seed_key, index)]
                assigned[seed_key][index] = True
                while frontier:
                    (col, row), which = frontier.pop()
                    height = clustered[(col, row)][which]
                    # A layer is a height *function* of plan position: one value per
                    # cell. A ramp climbing to a deck is continuously connected to the
                    # road it crosses, so growing purely by connectivity would walk up
                    # the ramp and claim both — and the cell where they cross can only
                    # keep one of them, burying the underpass. Reaching a cell this
                    # layer already occupies means the surface has passed over itself,
                    # so it stops there and the rest becomes its own sheet.
                    if (col, row) in layer:
                        continue
                    # Growth is checked against the cell it came from, but two branches
                    # — one along the ground, one climbing a ramp — can meet and become
                    # neighbours without ever being compared, leaving a step of metres
                    # inside one sheet. A cell joins only if it agrees with every
                    # neighbour the layer already holds.
                    # Every cell touching a corner shares that corner's vertex, and two
                    # cells that are only diagonal neighbours still share one. Comparing
                    # the four edge neighbours alone let a deck cell sit diagonally
                    # against a road cell: their shared corner averaged between the two
                    # and the triangles stretched from one height to the other, leaving
                    # a vertical fin standing in the road. All eight are compared.
                    if any(
                        abs(layer[(col + dc, row + dr)] - height) > self.layer_separation
                        for dc in (-1, 0, 1)
                        for dr in (-1, 0, 1)
                        if (dc or dr) and (col + dc, row + dr) in layer
                    ):
                        continue
                    layer[(col, row)] = height
                    for dc, dr in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                        neighbour = (col + dc, row + dr)
                        if neighbour not in clustered:
                            continue
                        for other, other_height in enumerate(clustered[neighbour]):
                            if assigned[neighbour][other]:
                                continue
                            if abs(other_height - height) <= self.layer_separation:
                                assigned[neighbour][other] = True
                                frontier.append((neighbour, other))
                layers.append(layer)
        return layers

    # ── triangulation ────────────────────────────────────────────────────────

    def _triangulate(self, layer: dict[tuple[int, int], float]) -> Surface:
        """Two triangles per covered cell, sharing corner vertices with their neighbours.

        Corner heights average the cells meeting at that corner, so the surface is
        continuous across every cell boundary by construction — there is no seam to weld
        and nothing to smooth afterwards.
        """
        corner_heights: dict[tuple[int, int], list[float]] = {}
        for (col, row), height in layer.items():
            for dc, dr in ((0, 0), (1, 0), (0, 1), (1, 1)):
                corner_heights.setdefault((col + dc, row + dr), []).append(height)

        index_of: dict[tuple[int, int], int] = {}
        vertices: list[tuple[float, float, float]] = []
        for corner, heights in corner_heights.items():
            index_of[corner] = len(vertices)
            vertices.append(
                (corner[0] * self.cell, corner[1] * self.cell, sum(heights) / len(heights))
            )

        triangles = []
        for col, row in layer:
            a = index_of[(col, row)]
            b = index_of[(col + 1, row)]
            c = index_of[(col + 1, row + 1)]
            d = index_of[(col, row + 1)]
            triangles.append((a, b, c))
            triangles.append((a, c, d))
        return Surface(vertices, triangles)
