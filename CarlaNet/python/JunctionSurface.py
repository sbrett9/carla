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

    #: How far, in cells, a gap is paved inwards from the surface surrounding it. Four
    #: cells at 0.5 m closes gaps up to roughly 4 m across, which covers the junction
    #: interiors measured while leaving the open edge of the network its shape.
    FILL_RADIUS_CELLS = 4

    #: Neighbour-averaging passes over the resolved height field, removing the flips
    #: left where the lower of two overlapping surfaces changes from cell to cell.
    RELAX_PASSES = 4

    def __init__(
        self,
        cell: float | None = None,
        layer_separation: float | None = None,
        fill_radius: int | None = None,
        relax_passes: int | None = None,
    ) -> None:
        self.cell = cell or self.CELL_METRES
        self.layer_separation = layer_separation or self.LAYER_SEPARATION_METRES
        self.fill_radius = self.FILL_RADIUS_CELLS if fill_radius is None else fill_radius
        self.relax_passes = self.RELAX_PASSES if relax_passes is None else relax_passes

    # ── layers ───────────────────────────────────────────────────────────────

    def build(self, strips: list[Strip]) -> list[Surface]:
        """One surface per height layer covered by these strips."""
        cells = self._sample(strips)
        layers = [layer for layer in self._split_layers(cells) if layer]
        return [self._triangulate(self._relax(self._close_gaps(layer))) for layer in layers]

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

    def _close_gaps(self, layer: dict[tuple[int, int], float]) -> dict[tuple[int, int], float]:
        """Pave the gaps a junction's connector paths leave between them.

        OpenDRIVE models a junction as turning paths, so the asphalt between them is
        never covered by any lane — on `Arapahoe_I25` that leaves 347 m² of enclosed gaps
        for a vehicle to drop through. A gap narrower than ``self.fill_radius`` cells is
        interior to the junction and gets paved at the height of the surface around it;
        anything wider is left alone, so the outer boundary of the road network keeps its
        shape instead of being inflated.
        """
        filled = dict(layer)
        for _ in range(self.fill_radius):
            additions: dict[tuple[int, int], float] = {}
            for (col, row), height in filled.items():
                for dc, dr in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    key = (col + dc, row + dr)
                    if key in filled:
                        continue
                    additions[key] = additions.get(key, 0.0) + height
            # Only keep a candidate if enough neighbours already carry surface, which is
            # what distinguishes an enclosed gap from the open edge of the network.
            counts: dict[tuple[int, int], int] = {}
            for col, row in filled:
                for dc, dr in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    key = (col + dc, row + dr)
                    if key not in filled:
                        counts[key] = counts.get(key, 0) + 1
            for key, total in additions.items():
                if counts.get(key, 0) >= 2:
                    filled[key] = total / counts[key]
        return filled

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
