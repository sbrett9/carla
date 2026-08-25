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

    #: Largest enclosed gap that is paved, in square metres. A gap the surface encloses
    #: is a hole a wheel drops into; a gap that opens outward is the space beside the
    #: road, and is left alone whatever its size.
    #:
    #: Among enclosed gaps, size is the only separator available, and it is not a clean
    #: one. On `Arapahoe_I25` the holes confirmed against the photoreal run from 11.2 to
    #: 28.2 m2 and the enclosed ground confirmed to need leaving alone starts at 521.5 m2,
    #: but ten regions lie between and none of them has been checked. This sits above
    #: every confirmed hole with room to spare and well below every confirmed island, so
    #: the unchecked middle is left unpaved rather than guessed at.
    MAX_ENCLOSED_GAP_AREA_M2 = 100.0

    #: Widest sliver between two lane quads that is paved over, in metres. Wide enough
    #: for the cracks measured, which are a cell or two across, and far narrower than the
    #: gap between a deck and the road beneath it.
    CRACK_SPAN_M = 1.0

    #: Neighbour-averaging passes over the resolved height field, removing the flips
    #: left where the lower of two overlapping surfaces changes from cell to cell.
    RELAX_PASSES = 4

    def __init__(
        self,
        cell: float | None = None,
        layer_separation: float | None = None,
        max_gap_area: float | None = None,
        fill_tolerance: float | None = None,
        crack_span: float | None = None,
        relax_passes: int | None = None,
    ) -> None:
        self.cell = cell or self.CELL_METRES
        self.layer_separation = layer_separation or self.LAYER_SEPARATION_METRES
        self.max_gap_area = self.MAX_ENCLOSED_GAP_AREA_M2 if max_gap_area is None else max_gap_area
        self.fill_tolerance = self.FILL_TOLERANCE_M if fill_tolerance is None else fill_tolerance
        self.crack_span = self.CRACK_SPAN_M if crack_span is None else crack_span
        self.relax_passes = self.RELAX_PASSES if relax_passes is None else relax_passes

    # ── layers ───────────────────────────────────────────────────────────────

    def build(self, strips: list[Strip]) -> list[Surface]:
        """One surface per height layer covered by these strips."""
        cells = self._sample(strips)
        layers = [layer for layer in self._split_layers(cells) if layer]
        return [
            self._triangulate(self._relax(self._close_gaps(self._close_cracks(layer))))
            for layer in layers
        ]

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

    def _close_cracks(self, layer: dict[tuple[int, int], float]) -> dict[tuple[int, int], float]:
        """Pave the slivers left where two lane quads meet.

        Adjacent lanes derive their shared boundary independently, from different
        reference lines, so the two edges disagree by a fraction of a millimetre. A cell
        centre landing inside that sliver is claimed by neither quad and the surface is
        left with a crack through it. Measured on `Arapahoe_I25`: 663 cells map-wide,
        166 m2, pinched between paving on opposite sides — narrower than a wheel and
        directly in the road.

        A cell is paved when paving lies close on two opposite sides and the two agree in
        height. That is what makes this safe to run over the whole network rather than
        inside junctions: it can only bridge a crack narrower than ``crack_span``, and
        only where both sides are already at the same height, so it cannot close the space
        between a deck and the road beneath it, nor round off the end of a road.
        """
        reach = max(1, int(self.crack_span / self.cell))
        paved = dict(layer)
        for _ in range(reach):
            additions: dict[tuple[int, int], float] = {}
            candidates = {
                (col + dc, row + dr)
                for col, row in paved
                for dc, dr in ((1, 0), (-1, 0), (0, 1), (0, -1))
                if (col + dc, row + dr) not in paved
            }
            for col, row in candidates:
                for dc, dr in ((1, 0), (0, 1)):
                    near = far = None
                    for step in range(1, reach + 1):
                        if near is None:
                            near = paved.get((col + dc * step, row + dr * step))
                        if far is None:
                            far = paved.get((col - dc * step, row - dr * step))
                    if near is None or far is None:
                        continue
                    if abs(near - far) > self.fill_tolerance:
                        continue
                    height = (near + far) / 2.0
                    # The two sides of the crack agreeing does not mean the height suits
                    # what else the cell touches: a crack running along the lip of a deck
                    # has the deck on one diagonal and the road below on the other, and
                    # bridging it there left a 1.10 m step standing in the surface.
                    if not self._agrees(paved, (col, row), height, self.fill_tolerance):
                        continue
                    additions[(col, row)] = height
                    break
            if not additions:
                break
            paved.update(additions)
        return paved

    def _close_gaps(self, layer: dict[tuple[int, int], float]) -> dict[tuple[int, int], float]:
        """Pave the gaps the surface encloses.

        OpenDRIVE models a junction as turning paths — a u-turn, some left turns, the
        straight-throughs, the rights — and between them sits asphalt no lane ever
        covers. A vehicle drops through it, since collision uses these triangles. The
        same holds wherever two roads meet without their lane polygons quite touching.

        What counts as enclosed, and how the size cap separates a hole from a piece of
        ground that must stay unpaved, is described on ``_enclosed_cells``.
        """
        filled = dict(layer)
        remaining = set(self._enclosed_cells(layer))
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

    def _enclosed_cells(self, layer: dict[tuple[int, int], float]) -> list[tuple[int, int]]:
        """Unpaved cells the surface encloses, found by flooding each gap under a cap.

        A gap the paving closes around is a hole a wheel drops into. A gap that reaches
        the outside is the space beside the road — a median between two approach
        carriageways, the verge, the space between a road and the ramp beside it — and
        paving those is what raised islands and spikes standing in the scene.

        Flooding answers that directly, where a ray cast in a few directions could not:
        rays report what a gap is near, not whether it is closed, so a median that happens
        to run past a junction reads the same as the junction's interior. Measured on
        `Arapahoe_I25`, the median, the jut and the spike are not enclosed at all, so this
        excludes them without needing to know which paving belongs to a junction.

        The cap keeps it bounded, and is what stops the open ground outside the network
        counting as one enormous gap: a flood that reaches the cap is abandoned, and every
        cell it reached is abandoned with it so the next boundary cell does not walk the
        same ground again. Size then separates the remaining two populations — real holes
        measured up to 64 m2, the enclosed ground that must stay unpaved from 522 m2 up.
        """
        limit = max(1, int(self.max_gap_area / (self.cell * self.cell)))
        interior: list[tuple[int, int]] = []
        done: set[tuple[int, int]] = set()
        for col, row in layer:
            for dc, dr in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                seed = (col + dc, row + dr)
                if seed in layer or seed in done:
                    continue
                region: set[tuple[int, int]] = {seed}
                frontier = [seed]
                overflowed = False
                while frontier:
                    if len(region) > limit:
                        overflowed = True
                        break
                    cur = frontier.pop()
                    for ec, er in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                        nxt = (cur[0] + ec, cur[1] + er)
                        if nxt not in layer and nxt not in region:
                            region.add(nxt)
                            frontier.append(nxt)
                done |= region
                if not overflowed:
                    interior.extend(region)
        return interior

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
        # A cell rejected while one layer grows is left unclaimed so it can seed a layer
        # of its own, but a single sweep only reaches the ones that sit later in iteration
        # order. Sweeping until a pass finds nothing unclaimed is what makes "every
        # sampled cell ends up in some layer" hold whatever order the cells come in.
        # Each pass that finds an unclaimed cell claims at least that one, so it ends.
        sweeping = True
        while sweeping:
            sweeping = False
            for seed_key, heights in clustered.items():
                for index in range(len(heights)):
                    if assigned[seed_key][index]:
                        continue
                    sweeping = True
                    layer: dict[tuple[int, int], float] = {}
                    # Claiming a cell when it is queued rather than when it is placed loses
                    # every cell the two tests below reject: it belongs to no layer, and no
                    # later seed can pick it up. That left 217 cells map-wide (54 m2) missing
                    # from the surface, in one-cell cracks along the line where two growth
                    # branches meet — a wheel drops through them. A cell is claimed only once
                    # it is actually placed, so a rejected one is still free to seed a layer
                    # of its own.
                    queued: set[tuple[tuple[int, int], int]] = {(seed_key, index)}
                    frontier = [(seed_key, index)]
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
                        assigned[(col, row)][which] = True
                        layer[(col, row)] = height
                        for dc, dr in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                            neighbour = (col + dc, row + dr)
                            if neighbour not in clustered:
                                continue
                            for other, other_height in enumerate(clustered[neighbour]):
                                if assigned[neighbour][other] or (neighbour, other) in queued:
                                    continue
                                if abs(other_height - height) <= self.layer_separation:
                                    queued.add((neighbour, other))
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
