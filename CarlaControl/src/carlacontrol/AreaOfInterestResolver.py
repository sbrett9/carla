"""Places declared areas of interest on a built world, in CARLA metres and on SUMO lanes.

A declared area is geographic. Two consumers need it in other frames and neither may re-derive a
projection: the truth producer and the engine think in CARLA-local metres, and a SUMO scenario sites
behaviour on lanes, not on metres (`04_Contracts.md` C5 §7.2-§7.3). This produces the resolved table,
`areas.resolved.json`, that the world package carries beside the source GeoJSON.

**Frames.** Every vertex is converted into the network's metres by SUMO's own projection, applied by
a SUMO process holding the world's own network (`SumoNetworkQuery`) -- the transform that placed every
lane. CARLA-local metres are then the co-simulation bridge's identity, `carla(x, y) = sumo(x, -y)`,
so an area, a lane and a rendered road are in one frame by construction. Each vertex is also placed
independently by `GeodeticFrame`, the WGS84 transform the telemetry, the drape and the Cesium imagery
share, and the two must agree within `GEODESY_AGREEMENT_LIMIT_M` (C5 V5.12) or the table is refused:
they measured within 0.4 mm on every shipped world, so a disagreement means the network is not in the
geographic frame the world claims -- a road-offset build, or a package whose manifest and network
came from different builds -- and an area's position would then depend on which frame was meant.

**Lanes.** Every non-internal lane of the network is tested against every area. The contained portion
is found by clipping the lane's shape against the area's boundary, so a lane that leaves a concave
area and comes back reports both stretches (`intervals_m`); `s_begin_m`/`s_end_m` bound them. Positions
are in SUMO's lane-position metres -- the shape's arc length scaled by the lane's `length` over its
shape length, which is how SUMO maps a position onto a shape -- so they can be written straight into a
`<stop startPos endPos>`. Measured, the two lengths differ by up to 5.76 m on Arapahoe and 2.37 m on
Bahonar, so an unscaled arc length would put a stop metres from where the area is. A lane is `inside`
when its whole length is contained, `crossing` when part is, `near` when none is but it passes within
`near_m`. An edge is `inside` when all its lanes are, `crossing` when any is inside or crossing, `near`
otherwise (C5 §7.3 rule 5). Permitted classes are SUMO's own expansion of the lane's attributes.

**Warnings** (C5 V5.5-V5.7): an area crossing the edge of the staging rectangle; an area lying wholly
in the staging ring, where traffic enters and leaves; an area no four-wheeled road vehicle can reach.

What it cannot see. The permissions reported are the world network's. A scenario that rewrites them
-- the private-road fence does (`SumoScenarioBuilder.restrict_private_roads`) -- must check its own
classes against its own network (V5.9, at scenario build). Whether an area is where the author meant
is invisible here: an area validated and placed exactly as written can still be drawn around the
wrong building.
"""
from __future__ import annotations

import logging
import math
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field
from typing import TYPE_CHECKING

from carlacontrol.AreaOfInterestSource import (
    AreaOfInterestError,
    AreaOfInterestSource,
    DeclaredArea,
)
from carlacontrol.NetworkFingerprint import NetworkFingerprint

if TYPE_CHECKING:
    from carlacontrol.GeodeticFrame import GeodeticFrame
    from carlacontrol.SumoNetworkQuery import SumoNetworkQuery

RESOLVED_VERSION = 1

# A lane passing within this distance of an area is `near` it. The same distance as `aoi_halo_m`
# (`10_Scale_And_Performance.md`), so a vehicle on a lane the table calls near is, at the lane's
# closest point, one the truth producer counts as an `aoi_member`.
DEFAULT_NEAR_M = 50.0

# C5 V5.12. Two orders of magnitude above the worst disagreement measured on the shipped worlds.
GEODESY_AGREEMENT_LIMIT_M = 0.05

# SUMO classes a four-wheeled road vehicle can carry. Pedestrians are outside this project (brief
# decision 5) and so are two-wheelers (D4.40: bicycle, motorcycle, moped); the rest of SUMO's classes
# -- rail, tram, ship, aircraft, cable car, subway, container, wheelchair, scooter, drone -- are not
# road vehicles. Used only to decide whether an area is reachable (V5.7), which is a warning.
FOUR_WHEELED_ROAD_CLASSES = frozenset({
    "private", "emergency", "authority", "army", "vip", "passenger", "hov", "taxi", "bus", "coach",
    "delivery", "truck", "trailer", "evehicle", "custom1", "custom2"})

POSITION_DECIMALS = 2   # lane positions, as SUMO writes them
GEOMETRY_DECIMALS = 3   # CARLA-local vertices, to the millimetre
SHORTEST_INTERVAL_M = 1e-6

logger = logging.getLogger(__name__)

Point = tuple[float, float]


@dataclass
class _Lane:
    """One non-internal lane of the network, in SUMO metres."""

    lane_id: str
    edge_id: str
    length: float
    shape: list[Point]
    declares_permissions: bool
    cumulative: list[float] = field(default_factory=list)
    bbox: tuple[float, float, float, float] = (0.0, 0.0, 0.0, 0.0)

    def __post_init__(self) -> None:
        total = [0.0]
        for a, b in zip(self.shape, self.shape[1:], strict=False):
            total.append(total[-1] + math.dist(a, b))
        self.cumulative = total
        xs = [p[0] for p in self.shape]
        ys = [p[1] for p in self.shape]
        self.bbox = (min(xs), min(ys), max(xs), max(ys))


@dataclass
class _PlacedArea:
    """One area in SUMO metres: polygons as closed rings, or a circle."""

    polygons: list[list[list[Point]]]
    centre: Point | None = None
    radius: float | None = None

    @property
    def bbox(self) -> tuple[float, float, float, float]:
        if self.centre is not None:
            (x, y), r = self.centre, self.radius
            return x - r, y - r, x + r, y + r
        xs = [p[0] for polygon in self.polygons for ring in polygon for p in ring]
        ys = [p[1] for polygon in self.polygons for ring in polygon for p in ring]
        return min(xs), min(ys), max(xs), max(ys)

    def boundary(self) -> list[tuple[Point, Point]]:
        return [(a, b) for polygon in self.polygons for ring in polygon
                for a, b in zip(ring, ring[1:], strict=False)]

    def contains(self, point: Point) -> bool:
        if self.centre is not None:
            return math.dist(point, self.centre) <= self.radius
        return any(self._in_polygon(point, polygon) for polygon in self.polygons)

    @staticmethod
    def _in_polygon(point: Point, rings: list[list[Point]]) -> bool:
        """Even-odd over every ring of one polygon, so a hole is outside."""
        x, y = point
        inside = False
        for ring in rings:
            for (x1, y1), (x2, y2) in zip(ring, ring[1:], strict=False):
                if (y1 > y) != (y2 > y) and x < x1 + (y - y1) * (x2 - x1) / (y2 - y1):
                    inside = not inside
        return inside

    def crossings(self, a: Point, b: Point) -> list[float]:
        """Parameters in (0, 1) at which the segment a->b meets the boundary."""
        dx, dy = b[0] - a[0], b[1] - a[1]
        found = []
        if self.centre is not None:
            fx, fy = a[0] - self.centre[0], a[1] - self.centre[1]
            qa = dx * dx + dy * dy
            qb = 2.0 * (fx * dx + fy * dy)
            qc = fx * fx + fy * fy - self.radius * self.radius
            disc = qb * qb - 4.0 * qa * qc
            if qa > 0.0 and disc > 0.0:
                root = math.sqrt(disc)
                found = [(-qb - root) / (2.0 * qa), (-qb + root) / (2.0 * qa)]
        else:
            for c, d in self.boundary():
                ex, ey = d[0] - c[0], d[1] - c[1]
                denominator = dx * ey - dy * ex
                if denominator == 0.0:
                    continue  # parallel; a midpoint test on either side settles containment
                cx, cy = c[0] - a[0], c[1] - a[1]
                t = (cx * ey - cy * ex) / denominator
                u = (cx * dy - cy * dx) / denominator
                if 0.0 <= u <= 1.0:
                    found.append(t)
        return [t for t in found if 0.0 < t < 1.0]

    def distance_to(self, shape: list[Point]) -> float:
        """Shortest distance from a polyline wholly outside the area to the area."""
        segments = list(zip(shape, shape[1:], strict=False))
        if self.centre is not None:
            nearest = min(_point_segment_distance(self.centre, a, b) for a, b in segments)
            return max(0.0, nearest - self.radius)
        return min(_segment_segment_distance(a, b, c, d)
                   for a, b in segments for c, d in self.boundary())


def _point_segment_distance(p: Point, a: Point, b: Point) -> float:
    dx, dy = b[0] - a[0], b[1] - a[1]
    length2 = dx * dx + dy * dy
    if length2 == 0.0:
        return math.dist(p, a)
    t = max(0.0, min(1.0, ((p[0] - a[0]) * dx + (p[1] - a[1]) * dy) / length2))
    return math.dist(p, (a[0] + t * dx, a[1] + t * dy))


def _segment_segment_distance(a: Point, b: Point, c: Point, d: Point) -> float:
    """Distance between two segments known not to cross (the caller has found no crossing)."""
    return min(_point_segment_distance(a, c, d), _point_segment_distance(b, c, d),
               _point_segment_distance(c, a, b), _point_segment_distance(d, a, b))


class AreaOfInterestResolver:
    """Resolves validated areas against one world's network and manifest."""

    def __init__(self, network_text: str, manifest: dict, query: SumoNetworkQuery,
                 geodetic: GeodeticFrame, near_m: float = DEFAULT_NEAR_M) -> None:
        if near_m <= 0:
            raise ValueError(f"near_m must be positive, got {near_m}")
        self.network_text = network_text
        self.manifest = manifest
        self.query = query
        self.geodetic = geodetic
        self.near_m = float(near_m)
        self.warnings: list[str] = []
        root = ET.fromstring(network_text)
        self.lanes = self._read_lanes(root)
        self.lane_counts: dict[str, int] = {}
        for lane in self.lanes:
            self.lane_counts[lane.edge_id] = self.lane_counts.get(lane.edge_id, 0) + 1
        self.net_offset = self._read_net_offset(root)

    @staticmethod
    def empty_table(network_text: str, manifest: dict, near_m: float = DEFAULT_NEAR_M) -> dict:
        """The table for a world built with no areas declared. Written rather than omitted, so a
        package with no areas reads differently from a package that predates them."""
        root = ET.fromstring(network_text)
        return AreaOfInterestResolver._table(
            manifest, network_text, None, near_m, AreaOfInterestResolver._read_net_offset(root),
            projected_by="", worst_residual=None, areas=[])

    def resolve(self, source: AreaOfInterestSource) -> dict:
        """The resolved table. Raises `AreaOfInterestError` when the two frames disagree (V5.12)."""
        if any(self.net_offset):
            self.warnings.append(
                f"the network carries netOffset {self.net_offset}: its meters are shifted from the "
                "world's geographic frame, which only a road-offset build does on purpose")
            logger.warning(self.warnings[-1])
        placed: list[tuple[DeclaredArea, _PlacedArea, float]] = []
        refusals: list[str] = []
        for area in source.areas:
            sumo_area, residual, worst_vertex = self._place(area)
            if residual > GEODESY_AGREEMENT_LIMIT_M:
                refusals.append(
                    f"V5.12 area '{area.area_id}': SUMO's projection and the world's geographic "
                    f"frame disagree by {residual:.3f} m at [{worst_vertex[0]:.7f}, "
                    f"{worst_vertex[1]:.7f}] (limit {GEODESY_AGREEMENT_LIMIT_M} m); the network is "
                    "not in the frame this world's origin describes"
                    + (f" -- it carries netOffset {self.net_offset}" if any(self.net_offset) else ""))
            placed.append((area, sumo_area, residual))
        if refusals:
            raise AreaOfInterestError(source.source_name, refusals)

        entries = []
        worst = 0.0
        for area, sumo_area, residual in placed:
            worst = max(worst, residual)
            entries.append(self._resolve_area(area, sumo_area))
        version = self.query.sumo_version or "of unknown version"
        return self._table(self.manifest, self.network_text, source, self.near_m, self.net_offset,
                           projected_by=f"SUMO {version}, traci.simulation.convertGeo",
                           worst_residual=round(worst, 4), areas=entries)

    # ---- placing one area -------------------------------------------------------------------------

    def _place(self, area: DeclaredArea) -> tuple[_PlacedArea, float, Point]:
        """The area in SUMO metres, the worst disagreement with the geographic frame, and where."""
        worst = (0.0, (0.0, 0.0))

        def convert(position: Point) -> Point:
            nonlocal worst
            longitude, latitude = position
            sx, sy = self.query.to_sumo(longitude=longitude, latitude=latitude)
            gx, gy = self.geodetic.to_carla(longitude=longitude, latitude=latitude)
            residual = math.hypot(sx - gx, -sy - gy)
            if residual > worst[0]:
                worst = (residual, position)
            return sx, sy

        if area.is_circle:
            placed = _PlacedArea([], convert(area.centre), area.radius_m)
        else:
            placed = _PlacedArea([[[convert(p) for p in ring] for ring in polygon]
                                  for polygon in area.polygons])
        return placed, worst[0], worst[1]

    def _resolve_area(self, area: DeclaredArea, placed: _PlacedArea) -> dict:
        lanes = []
        bx0, by0, bx1, by1 = placed.bbox
        reach = self.near_m
        for lane in self.lanes:
            lx0, ly0, lx1, ly1 = lane.bbox
            if lx1 < bx0 - reach or lx0 > bx1 + reach or ly1 < by0 - reach or ly0 > by1 + reach:
                continue
            entry = self._classify(lane, placed)
            if entry is not None:
                lanes.append(entry)
        lanes.sort(key=lambda e: (e["edge_id"].encode("utf-8"), e["lane_id"].encode("utf-8")))
        edges = self._edges(lanes)

        carla = self._carla_geometry(area, placed)
        envelope = self._carla_envelope(placed)
        warnings = self._staging_warnings(area, placed, envelope)
        if not any(set(lane["allowed_vclasses"]) & FOUR_WHEELED_ROAD_CLASSES
                   or "all" in lane["allowed_vclasses"] for lane in lanes):
            warnings.append(
                f"V5.7 area '{area.area_id}': no lane a four-wheeled road vehicle may use lies "
                f"inside it, crosses it, or passes within {self.near_m:g} m of it, so no vehicle "
                "can reach it and it will gather no vehicle relations")
        for warning in warnings:
            logger.warning(warning)
        return {
            "id": area.area_id,
            "name": area.name,
            "kind": area.kind,
            "geographic": area.geometry,
            "carla_local": carla,
            "envelope_carla_m": envelope,
            "sumo": {"edges": edges, "lanes": lanes},
            "warnings": warnings,
        }

    def _classify(self, lane: _Lane, area: _PlacedArea) -> dict | None:
        intervals: list[list[float]] = []
        for i, (a, b) in enumerate(zip(lane.shape, lane.shape[1:], strict=False)):
            segment = lane.cumulative[i + 1] - lane.cumulative[i]
            if segment == 0.0:
                continue
            cuts = sorted({0.0, 1.0, *area.crossings(a, b)})
            for t0, t1 in zip(cuts, cuts[1:], strict=False):
                middle = (t0 + t1) / 2.0
                if not area.contains((a[0] + (b[0] - a[0]) * middle,
                                      a[1] + (b[1] - a[1]) * middle)):
                    continue
                s0 = lane.cumulative[i] + t0 * segment
                s1 = lane.cumulative[i] + t1 * segment
                if intervals and abs(intervals[-1][1] - s0) < 1e-9:
                    intervals[-1][1] = s1
                else:
                    intervals.append([s0, s1])
        intervals = [iv for iv in intervals if iv[1] - iv[0] > SHORTEST_INTERVAL_M]
        total = lane.cumulative[-1]

        entry = {"lane_id": lane.lane_id, "edge_id": lane.edge_id}
        if intervals:
            contained = sum(b - a for a, b in intervals)
            scale = lane.length / total if total > 0.0 else 1.0
            positions = [[round(min(lane.length, a * scale), POSITION_DECIMALS),
                          round(min(lane.length, b * scale), POSITION_DECIMALS)]
                         for a, b in intervals]
            entry["containment"] = "inside" if contained >= total - 1e-6 else "crossing"
            entry["s_begin_m"] = positions[0][0]
            entry["s_end_m"] = positions[-1][1]
            entry["intervals_m"] = positions
        else:
            distance = area.distance_to(lane.shape)
            if distance > self.near_m:
                return None
            entry["containment"] = "near"
            entry["distance_m"] = round(distance, POSITION_DECIMALS)
        allowed = self.query.allowed_vclasses(lane.lane_id)
        if not allowed and not lane.declares_permissions:
            allowed = ["all"]
        entry["allowed_vclasses"] = allowed
        return entry

    def _edges(self, lanes: list[dict]) -> list[dict]:
        """C5 §7.3 rule 5. A lane of the edge that is not listed at all counts as disagreeing."""
        by_edge: dict[str, list[str]] = {}
        for lane in lanes:
            by_edge.setdefault(lane["edge_id"], []).append(lane["containment"])
        edges = []
        for edge_id, states in by_edge.items():
            if len(states) == self.lane_counts[edge_id] and all(s == "inside" for s in states):
                containment = "inside"
            elif any(s in ("inside", "crossing") for s in states):
                containment = "crossing"
            else:
                containment = "near"
            edges.append({"edge_id": edge_id, "containment": containment})
        return edges

    # ---- CARLA-frame outputs and staging warnings ---------------------------------------------------

    @staticmethod
    def _carla(point: Point) -> list[float]:
        return [round(point[0], GEOMETRY_DECIMALS), round(-point[1], GEOMETRY_DECIMALS)]

    def _carla_geometry(self, area: DeclaredArea, placed: _PlacedArea) -> dict:
        if placed.centre is not None:
            return {"type": "Circle", "centre": self._carla(placed.centre),
                    "radius_m": placed.radius}
        polygons = [[[self._carla(p) for p in ring] for ring in polygon]
                    for polygon in placed.polygons]
        if area.geometry["type"] == "Polygon":
            return {"type": "Polygon", "coordinates": polygons[0]}
        return {"type": "MultiPolygon", "coordinates": polygons}

    @staticmethod
    def _carla_envelope(placed: _PlacedArea) -> list[float]:
        x0, y0, x1, y1 = placed.bbox
        return [round(x0, GEOMETRY_DECIMALS), round(-y1, GEOMETRY_DECIMALS),
                round(x1, GEOMETRY_DECIMALS), round(-y0, GEOMETRY_DECIMALS)]

    def _staging_warnings(self, area: DeclaredArea, placed: _PlacedArea,
                          envelope: list[float]) -> list[str]:
        m = self.manifest
        sx0, sy0 = m.get("StagingMinXMeters", 0.0), m.get("StagingMinYMeters", 0.0)
        sx1, sy1 = m.get("StagingMaxXMeters", 0.0), m.get("StagingMaxYMeters", 0.0)
        margin = m.get("StagingMarginMeters", 0.0)
        if sx1 <= sx0 or sy1 <= sy0:
            return []
        ex0, ey0, ex1, ey1 = envelope
        warnings = []
        within = ex0 >= sx0 and ey0 >= sy0 and ex1 <= sx1 and ey1 <= sy1
        if ex1 < sx0 or ex0 > sx1 or ey1 < sy0 or ey0 > sy1:
            warnings.append(f"V5.5 area '{area.area_id}' lies wholly outside the staging rectangle; "
                            "nothing is simulated there")
        elif not within:
            warnings.append(f"V5.5 area '{area.area_id}' crosses the edge of the staging rectangle; "
                            "relations derived for the part outside it describe an unsimulated "
                            "world")
        inner = (sx0 + margin, sy0 + margin, sx1 - margin, sy1 - margin)
        if within and margin > 0 and inner[2] > inner[0] and inner[3] > inner[1] \
                and not self._meets_rectangle(placed, inner):
            warnings.append(f"V5.6 area '{area.area_id}' lies wholly in the {margin:g} m staging "
                            "ring, where traffic enters and leaves the world")
        return warnings

    @staticmethod
    def _meets_rectangle(placed: _PlacedArea, rect: tuple[float, float, float, float]) -> bool:
        """Whether the area touches a CARLA-frame rectangle. Tested in SUMO metres."""
        x0, y0, x1, y1 = rect[0], -rect[3], rect[2], -rect[1]
        if placed.centre is not None:
            cx, cy = placed.centre
            nearest = (min(max(cx, x0), x1), min(max(cy, y0), y1))
            return math.dist(nearest, placed.centre) <= placed.radius
        corners = [(x0, y0), (x1, y0), (x1, y1), (x0, y1)]
        if any(x0 <= p[0] <= x1 and y0 <= p[1] <= y1
               for polygon in placed.polygons for ring in polygon for p in ring):
            return True
        if any(placed.contains(c) for c in corners):
            return True
        return any(placed.crossings(a, b) for a, b in zip(corners, corners[1:] + corners[:1],
                                                         strict=False))

    # ---- the network --------------------------------------------------------------------------------

    @staticmethod
    def _read_lanes(root: ET.Element) -> list[_Lane]:
        lanes = []
        for edge in root.findall("edge"):
            if edge.get("function") not in (None, "", "normal"):
                continue
            for lane in edge.findall("lane"):
                shape = [tuple(float(v) for v in point.split(",")[:2])
                         for point in (lane.get("shape") or "").split()]
                if len(shape) < 2:
                    continue
                lanes.append(_Lane(lane.get("id"), edge.get("id"), float(lane.get("length")),
                                   shape, lane.get("allow") is not None
                                   or lane.get("disallow") is not None))
        return lanes

    @staticmethod
    def _read_net_offset(root: ET.Element) -> list[float]:
        location = root.find("location")
        text = location.get("netOffset", "0,0") if location is not None else "0,0"
        return [float(v) for v in text.split(",")[:2]]

    @staticmethod
    def _table(manifest: dict, network_text: str, source: AreaOfInterestSource | None,
               near_m: float, net_offset: list[float], projected_by: str,
               worst_residual: float | None, areas: list[dict]) -> dict:
        return {
            "resolved_version": RESOLVED_VERSION,
            "source_file_name": source.source_name if source else "",
            "source_sha256": source.sha256 if source else "",
            "world_map_name": manifest.get("MapName", ""),
            "world_georeference": manifest.get("GeoReferenceString", ""),
            "world_origin_latitude": manifest.get("OriginLatitude"),
            "world_origin_longitude": manifest.get("OriginLongitude"),
            "network_fingerprint": NetworkFingerprint.of_text(network_text),
            "near_m": near_m,
            "frame": {
                "carla_from_sumo": "carla(x, y) = sumo(x, -y)",
                "projected_by": projected_by,
                "net_offset_m": net_offset,
                "geodesy_agreement_limit_m": GEODESY_AGREEMENT_LIMIT_M,
                "geodesy_worst_residual_m": worst_residual,
            },
            "areas": areas,
        }
