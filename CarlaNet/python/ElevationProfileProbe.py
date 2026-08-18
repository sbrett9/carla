"""Read-only measurement of the vertical profile in a generated OpenDRIVE map.

Loads an already-generated ``*_elevated.xodr``, recovers each road's sampled ``(s, z)``
series from the ``a`` attributes (which are the sampled heights under every fit scheme
that keys one record per sample), re-fits it with a candidate scheme, and measures what
changed. No server and no editor are involved.

The measurements mirror the baseline tables in
``Docs/CAT_Research/Findings/21_Road_Elevation_Profile_Continuity.md`` §3, which the
probe must reproduce on the "before" side before any fit change is trusted.
"""

from __future__ import annotations

import logging
import math
import re
import statistics
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import NamedTuple

from ElevationProfileFitter import ElevationProfileFitter, ElevationRecord

logger = logging.getLogger(__name__)

WGS84_A = 6378137.0
WGS84_E2 = 6.69437999014e-3


class Distribution(NamedTuple):
    """Summary of a measured quantity, in the shape the findings tables report it."""

    count: int
    median: float
    p90: float
    p99: float
    maximum: float
    fraction_over: float

    @classmethod
    def of(cls, values: list[float], threshold: float) -> Distribution:
        if not values:
            return cls(0, math.nan, math.nan, math.nan, math.nan, math.nan)
        ordered = sorted(values)
        over = sum(1 for v in ordered if v > threshold) / len(ordered)
        return cls(
            count=len(ordered),
            median=statistics.median(ordered),
            p90=cls._percentile(ordered, 0.90),
            p99=cls._percentile(ordered, 0.99),
            maximum=ordered[-1],
            fraction_over=over,
        )

    @staticmethod
    def _percentile(ordered: list[float], q: float) -> float:
        k = (len(ordered) - 1) * q
        floor = int(k)
        if floor + 1 >= len(ordered):
            return ordered[floor]
        return ordered[floor] + (ordered[floor + 1] - ordered[floor]) * (k - floor)


class RoadLink(NamedTuple):
    """One end of a road, and the road it hands over to."""

    role: str  # "predecessor" or "successor"
    station: float  # station on this road where the handover happens
    other_id: str
    contact_point: str  # "start" or "end" on the other road


class Geometry(NamedTuple):
    """One ``<geometry>`` element of a plan view, with its shape parameters."""

    s: float
    x: float
    y: float
    hdg: float
    length: float
    kind: str
    params: dict[str, float]

    def position(self, s: float) -> tuple[float, float]:
        """Plan-view position at absolute station ``s`` (+X east, +Y north)."""
        ds = max(0.0, min(s - self.s, self.length))
        if self.kind == "line":
            u, v = ds, 0.0
        elif self.kind == "arc":
            curvature = self.params.get("curvature", 0.0)
            if abs(curvature) < 1e-12:
                u, v = ds, 0.0
            else:
                radius = 1.0 / curvature
                u = math.sin(ds * curvature) * radius
                v = (1.0 - math.cos(ds * curvature)) * radius
        elif self.kind == "paramPoly3":
            p = ds / self.length if self.params.get("normalized", 1.0) else ds
            u = (
                self.params["aU"]
                + self.params["bU"] * p
                + self.params["cU"] * p * p
                + self.params["dU"] * p * p * p
            )
            v = (
                self.params["aV"]
                + self.params["bV"] * p
                + self.params["cV"] * p * p
                + self.params["dV"] * p * p * p
            )
        else:
            raise ValueError(f"unsupported plan-view geometry {self.kind!r}")
        cos_h, sin_h = math.cos(self.hdg), math.sin(self.hdg)
        return self.x + u * cos_h - v * sin_h, self.y + u * sin_h + v * cos_h


class Road:
    """One ``<road>`` of a parsed map, with its profile, links and plan view."""

    def __init__(self, node: ET.Element) -> None:
        self.id: str = node.get("id", "")
        self.length: float = float(node.get("length", "0"))
        self.junction: str = node.get("junction", "-1")
        self.records: list[ElevationRecord] = self._read_profile(node)
        self.links: list[RoadLink] = self._read_links(node)
        self.geometries: list[Geometry] = self._read_plan_view(node)
        self.has_lane_offset: bool = node.find("lanes/laneOffset") is not None

    @property
    def is_connector(self) -> bool:
        """True when the road lies inside a junction rather than between junctions."""
        return self.junction != "-1"

    @property
    def stations(self) -> list[float]:
        return [r.s for r in self.records]

    @property
    def heights(self) -> list[float]:
        """The sampled heights: each record's ``a`` is the height at its own station."""
        return [r.a for r in self.records]

    def position(self, s: float) -> tuple[float, float] | None:
        """Plan-view position at station ``s``, or None when the road has no geometry."""
        if not self.geometries:
            return None
        chosen = self.geometries[0]
        for geometry in self.geometries:
            if geometry.s <= s + 1e-9:
                chosen = geometry
            else:
                break
        return chosen.position(s)

    @staticmethod
    def _read_profile(node: ET.Element) -> list[ElevationRecord]:
        profile = node.find("elevationProfile")
        if profile is None:
            return []
        records = [
            ElevationRecord(*(float(e.get(k, "0")) for k in ("s", "a", "b", "c", "d")))
            for e in profile.iter("elevation")
        ]
        return sorted(records, key=lambda r: r.s)

    def _read_links(self, node: ET.Element) -> list[RoadLink]:
        link = node.find("link")
        if link is None:
            return []
        links = []
        for role, station in (("predecessor", 0.0), ("successor", self.length)):
            element = link.find(role)
            if element is None or element.get("elementType") != "road":
                continue
            links.append(
                RoadLink(
                    role=role,
                    station=station,
                    other_id=element.get("elementId", ""),
                    contact_point=element.get("contactPoint", "start"),
                )
            )
        return links

    @staticmethod
    def _read_plan_view(node: ET.Element) -> list[Geometry]:
        plan_view = node.find("planView")
        if plan_view is None:
            return []
        geometries = []
        for element in plan_view.findall("geometry"):
            shape = next((child for child in element), None)
            if shape is None:
                continue
            params: dict[str, float] = {}
            if shape.tag == "arc":
                params["curvature"] = float(shape.get("curvature", "0"))
            elif shape.tag == "paramPoly3":
                for key in ("aU", "bU", "cU", "dU", "aV", "bV", "cV", "dV"):
                    params[key] = float(shape.get(key, "0"))
                params["normalized"] = 1.0 if shape.get("pRange") == "normalized" else 0.0
            geometries.append(
                Geometry(
                    s=float(element.get("s", "0")),
                    x=float(element.get("x", "0")),
                    y=float(element.get("y", "0")),
                    hdg=float(element.get("hdg", "0")),
                    length=float(element.get("length", "0")),
                    kind=shape.tag,
                    params=params,
                )
            )
        return sorted(geometries, key=lambda g: g.s)


class ElevationProfileProbe:
    """Measures the continuity of one map's vertical profile, before and after a re-fit.

    Every method is read-only with respect to the map on disk; a re-fit produces a new
    set of records held in memory and compared against the ones the file carries.
    """

    #: Slope step counted as a discontinuity when reporting a fraction, in rise/run.
    SLOPE_EPSILON = 0.02

    #: Grade above which a junction connector is reported as one no road would carry.
    STEEP_GRADE = 0.15

    #: Plan separation within which two opposing roads count as one carriageway.
    CARRIAGEWAY_TOLERANCE_METRES = 0.5

    def __init__(self, path: Path) -> None:
        self.path = Path(path)
        root = ET.parse(self.path).getroot()
        self.roads: dict[str, Road] = {}
        for node in root.iter("road"):
            road = Road(node)
            self.roads[road.id] = road
        self.origin: tuple[float, float] | None = self._read_origin(root)

    @property
    def name(self) -> str:
        return self.path.stem

    # ── census ───────────────────────────────────────────────────────────────

    def census(self) -> dict[str, int]:
        """Counts of emitted records and plan-view primitives."""
        counts = {
            "records": 0,
            "curved": 0,
            "cubic": 0,
            "roads_with_profile": 0,
            "roads_over_two_records": 0,
            "roads_ending_flat": 0,
            "line": 0,
            "arc": 0,
            "paramPoly3": 0,
        }
        for road in self.roads.values():
            if road.records:
                counts["roads_with_profile"] += 1
                if len(road.records) > 2:
                    counts["roads_over_two_records"] += 1
                if road.records[-1].b == 0.0:
                    counts["roads_ending_flat"] += 1
            for record in road.records:
                counts["records"] += 1
                counts["curved"] += 1 if record.c != 0.0 else 0
                counts["cubic"] += 1 if record.d != 0.0 else 0
            for geometry in road.geometries:
                if geometry.kind in counts:
                    counts[geometry.kind] += 1
        return counts

    # ── re-fitting ───────────────────────────────────────────────────────────

    def refit(
        self, scheme: str, smooth_window: int = 1, smooth_order: int | None = None
    ) -> dict[str, list[ElevationRecord]]:
        """Re-fit every road's recovered sample series with a candidate scheme."""
        fitted: dict[str, list[ElevationRecord]] = {}
        for road_id, road in self.roads.items():
            if not road.records:
                continue
            stations, heights = road.stations, road.heights
            if smooth_window > 1:
                heights = ElevationProfileFitter.smooth(
                    stations, heights, smooth_window, smooth_order
                )
            if scheme == "linear":
                fitted[road_id] = ElevationProfileFitter.piecewise_linear(stations, heights)
            elif scheme == "monotone":
                fitted[road_id] = ElevationProfileFitter.monotone_cubic(stations, heights)
            else:
                raise ValueError(f"unknown fit scheme {scheme!r}")
        return fitted

    def records_of(
        self, road_id: str, fitted: dict[str, list[ElevationRecord]] | None
    ) -> list[ElevationRecord]:
        """The fitted records for a road when re-fitting, else the ones on disk."""
        if fitted is None:
            return self.roads[road_id].records
        return fitted.get(road_id, [])

    # ── continuity within a road ─────────────────────────────────────────────

    def slope_discontinuity(
        self, fitted: dict[str, list[ElevationRecord]] | None = None
    ) -> Distribution:
        """Slope step at every internal record boundary.

        ``|b[i] − (b[i-1] + 2·c[i-1]·h + 3·d[i-1]·h²)|`` — the outgoing record's slope
        against the incoming record's tangent evaluated at the shared station.
        """
        steps = []
        for road_id in self.roads:
            records = self.records_of(road_id, fitted)
            for i in range(1, len(records)):
                station = records[i].s
                steps.append(abs(records[i].b - records[i - 1].tangent(station)))
        return Distribution.of(steps, self.SLOPE_EPSILON)

    def height_discontinuity(self, fitted: dict[str, list[ElevationRecord]] | None = None) -> float:
        """Largest height step at an internal record boundary — C0 continuity."""
        worst = 0.0
        for road_id in self.roads:
            records = self.records_of(road_id, fitted)
            for i in range(1, len(records)):
                station = records[i].s
                worst = max(worst, abs(records[i].a - records[i - 1].evaluate(station)))
        return worst

    def deviation_from_samples(
        self, fitted: dict[str, list[ElevationRecord]], worst_count: int = 5
    ) -> tuple[float, float, list[tuple[float, str, float]]]:
        """How far the fitted curve sits from the heights actually sampled.

        Zero for an interpolating fit; it becomes the cost of the low-pass. Returns
        ``(max, rms, worst)`` where each worst entry is ``(deviation, road, station)``.
        """
        squares, worst = [], []
        for road_id, road in self.roads.items():
            records = fitted.get(road_id)
            if not records:
                continue
            for station, sampled in zip(road.stations, road.heights, strict=True):
                value = ElevationProfileFitter.evaluate(records, station)
                if value is None:
                    continue
                deviation = abs(value - sampled)
                squares.append(deviation * deviation)
                worst.append((deviation, road_id, station))
        if not squares:
            return 0.0, 0.0, []
        worst.sort(reverse=True)
        return math.sqrt(max(squares)), math.sqrt(sum(squares) / len(squares)), worst[:worst_count]

    def overshoot(
        self, fitted: dict[str, list[ElevationRecord]], samples_per_span: int = 21
    ) -> tuple[int, int, float]:
        """Excursions outside the bracketing sample values on a monotone span.

        Returns ``(violations, spans_checked, worst_excursion_metres)``. A monotone
        cubic Hermite fit must report zero violations; that guarantee is the reason the
        fit is monotone rather than a natural spline.
        """
        violations, spans, worst = 0, 0, 0.0
        for records in fitted.values():
            for i in range(len(records) - 1):
                lo_z, hi_z = records[i].a, records[i + 1].a
                if lo_z == hi_z:
                    continue
                low, high = min(lo_z, hi_z), max(lo_z, hi_z)
                spans += 1
                start, end = records[i].s, records[i + 1].s
                offending = 0.0
                for k in range(1, samples_per_span):
                    station = start + (end - start) * k / samples_per_span
                    value = records[i].evaluate(station)
                    offending = max(offending, low - value, value - high)
                if offending > 1e-9:
                    violations += 1
                    worst = max(worst, offending)
        return violations, spans, worst

    # ── continuity between roads ─────────────────────────────────────────────

    def link_mismatch(
        self, fitted: dict[str, list[ElevationRecord]] | None = None
    ) -> tuple[Distribution, Distribution, list[tuple[float, str, str]]]:
        """Height and slope disagreement where two roads hand over to each other.

        Travel direction reverses when roads meet end-to-end or start-to-start, so the
        neighbour's tangent is negated in those cases before comparison. Returns
        ``(heights, slopes, worst_slopes)``.
        """
        heights, slopes, worst = [], [], []
        for road_id, road in self.roads.items():
            mine = self.records_of(road_id, fitted)
            if not mine:
                continue
            for link in road.links:
                other = self.roads.get(link.other_id)
                theirs = self.records_of(link.other_id, fitted) if other else []
                if not theirs:
                    continue
                their_station = 0.0 if link.contact_point == "start" else other.length
                my_z = ElevationProfileFitter.evaluate(mine, link.station)
                their_z = ElevationProfileFitter.evaluate(theirs, their_station)
                my_slope = ElevationProfileFitter.tangent(mine, link.station)
                their_slope = ElevationProfileFitter.tangent(theirs, their_station)
                if None in (my_z, their_z, my_slope, their_slope):
                    continue
                reversed_travel = (link.role == "successor") == (link.contact_point == "end")
                sign = -1.0 if reversed_travel else 1.0
                heights.append(abs(my_z - their_z))
                mismatch = abs(my_slope - sign * their_slope)
                slopes.append(mismatch)
                worst.append((mismatch, road_id, link.other_id))
        worst.sort(reverse=True)
        return (
            Distribution.of(heights, 0.05),
            Distribution.of(slopes, self.SLOPE_EPSILON),
            worst[:5],
        )

    def connector_grades(
        self, fitted: dict[str, list[ElevationRecord]] | None = None
    ) -> tuple[int, int, int, float, list[tuple[float, str, float, float]]]:
        """Grades carried by junction connectors, against the roads they link.

        Returns ``(connectors, spans, steep_spans, worst_grade, worst)`` where each
        worst entry is ``(grade, road, road_length, linked_grade)``. A connector steeper
        than everything it joins is sampled terrain, not a fitting artifact: a C1 fit
        rounds the creases either side and leaves the ramp standing.
        """
        connectors = spans = steep = 0
        worst_grade, worst = 0.0, []
        for road_id, road in self.roads.items():
            if not road.is_connector:
                continue
            records = self.records_of(road_id, fitted)
            if len(records) < 2:
                continue
            connectors += 1
            linked = self._linked_grade(road, fitted)
            for i in range(len(records) - 1):
                span = records[i + 1].s - records[i].s
                if span <= 1e-6:
                    continue
                spans += 1
                grade = abs(records[i + 1].a - records[i].a) / span
                if grade > self.STEEP_GRADE:
                    steep += 1
                if grade > worst_grade:
                    worst_grade = grade
                worst.append((grade, road_id, road.length, linked))
        worst.sort(reverse=True)
        return connectors, spans, steep, worst_grade, worst[:5]

    def _linked_grade(self, road: Road, fitted: dict[str, list[ElevationRecord]] | None) -> float:
        """Steepest grade carried by the roads a connector links, at their contact ends."""
        steepest = 0.0
        for link in road.links:
            other = self.roads.get(link.other_id)
            if other is None:
                continue
            records = self.records_of(link.other_id, fitted)
            if not records:
                continue
            station = 0.0 if link.contact_point == "start" else other.length
            slope = ElevationProfileFitter.tangent(records, station)
            if slope is not None:
                steepest = max(steepest, abs(slope))
        return steepest

    # ── paired carriageways ──────────────────────────────────────────────────

    def carriageway_pairs(self, station_step: float = 5.0) -> list[tuple[str, str, float]]:
        """Opposing-direction road pairs: equal length, coincident reference line.

        OSM encodes each direction of a street as its own way, so netconvert emits two
        roads on one centreline. Returns ``(left, right, worst_separation)``.
        """
        pairs = []
        by_length: dict[int, list[Road]] = {}
        for road in self.roads.values():
            if not road.geometries or road.length <= station_step:
                continue
            by_length.setdefault(int(round(road.length * 100.0)), []).append(road)
        for group in by_length.values():
            for i in range(len(group)):
                for j in range(i + 1, len(group)):
                    separation = self._reference_line_separation(group[i], group[j], station_step)
                    if separation is not None and separation <= self.CARRIAGEWAY_TOLERANCE_METRES:
                        pairs.append((group[i].id, group[j].id, separation))
        return sorted(pairs)

    @staticmethod
    def _reference_line_separation(left: Road, right: Road, step: float) -> float | None:
        """Worst plan separation between two roads compared in opposing directions."""
        worst = 0.0
        station = 0.0
        while station <= left.length:
            here = left.position(station)
            there = right.position(right.length - station)
            if here is None or there is None:
                return None
            worst = max(worst, math.dist(here, there))
            station += step
        return worst

    def carriageway_disagreement(
        self,
        pairs: list[tuple[str, str, float]],
        fitted: dict[str, list[ElevationRecord]] | None = None,
        station_step: float = 5.0,
        departure_metres: float = 1.0,
    ) -> list[tuple[str, str, Distribution, int, float]]:
        """Height disagreement between paired carriageways, with the crossover count.

        A crossover is a sign flip of ``z_left − z_right`` at matched physical stations:
        the two halves of one street swapping which is on top, which opens a vertical
        step along the seam where their lanes meet.

        The last element of each result is the longest continuous run, in metres, over
        which the two disagree by more than ``departure_metres``. It separates the two
        mechanisms: a one-sample spike is an isolated outlier, while a sustained run is
        one carriageway's samples sitting on a structure its twin passed under — and a
        sustained run is invisible to ``RejectOutliers``, which only rejects a sample
        that is far from *both* of its neighbours.
        """
        results = []
        for left_id, right_id, _ in pairs:
            left, right = self.roads[left_id], self.roads[right_id]
            left_records = self.records_of(left_id, fitted)
            right_records = self.records_of(right_id, fitted)
            if not left_records or not right_records:
                continue
            differences, crossovers, previous = [], 0, 0.0
            run = longest_run = 0.0
            station = 0.0
            while station <= left.length:
                left_z = ElevationProfileFitter.evaluate(left_records, station)
                right_z = ElevationProfileFitter.evaluate(right_records, right.length - station)
                if left_z is not None and right_z is not None:
                    delta = left_z - right_z
                    differences.append(abs(delta))
                    if previous != 0.0 and delta != 0.0 and (delta > 0.0) != (previous > 0.0):
                        crossovers += 1
                    if delta != 0.0:
                        previous = delta
                    run = run + station_step if abs(delta) > departure_metres else 0.0
                    longest_run = max(longest_run, run)
                station += station_step
            results.append(
                (left_id, right_id, Distribution.of(differences, 0.25), crossovers, longest_run)
            )
        return results

    # ── terminal spans ───────────────────────────────────────────────────────

    def terminal_spans(self) -> tuple[list[float], int, int]:
        """Length of each road's final span, and how many imply an implausible grade.

        The sampler walks ``0, step, 2·step, …`` and then jumps to the road length, so
        the last span is a remainder. Returns ``(lengths, steep_count, total)``.
        """
        lengths, steep = [], 0
        for road in self.roads.values():
            if len(road.records) < 2:
                continue
            span = road.records[-1].s - road.records[-2].s
            lengths.append(span)
            if span > 1e-6:
                rise = abs(road.records[-1].a - road.records[-2].a)
                if rise / span > self.STEEP_GRADE:
                    steep += 1
        return sorted(lengths), steep, len(lengths)

    # ── locating a reported surface ──────────────────────────────────────────

    def locate(
        self, latitude: float, longitude: float, radius: float = 45.0, step: float = 1.0
    ) -> list[tuple[float, Road, float]]:
        """Roads within ``radius`` of a WGS84 point, nearest approach first.

        Ties a surface reported from a running session to specific road ids without
        loading the map. The projection is the ellipsoidal tangent-plane transform the
        elevation sampler itself uses (``Geodesy.CarlaLocalToGeodetic``), so a located
        point lands where the heights for that road were sampled.
        """
        if self.origin is None:
            raise ValueError(f"{self.path.name} carries no geoReference to project through")
        target = self.project(latitude, longitude)
        found = []
        for road in self.roads.values():
            if not road.geometries:
                continue
            best, best_station = self._closest_approach(road, target, step)
            if best <= radius:
                # Refine inside the bracketing coarse steps so the reported approach is
                # not quantised by the walk.
                best, best_station = self._closest_approach(
                    road,
                    target,
                    step / 50.0,
                    start=max(0.0, best_station - step),
                    end=min(road.length, best_station + step),
                )
                found.append((best, road, best_station))
        return sorted(found, key=lambda entry: entry[0])

    @staticmethod
    def _closest_approach(
        road: Road,
        target: tuple[float, float],
        step: float,
        start: float = 0.0,
        end: float | None = None,
    ) -> tuple[float, float]:
        """Nearest approach of a road's reference line to a plan-view point."""
        end = road.length if end is None else end
        best, best_station = math.inf, start
        station = start
        while station <= end:
            position = road.position(station)
            if position is not None:
                distance = math.dist(position, target)
                if distance < best:
                    best, best_station = distance, station
            station += step
        return best, best_station

    def project(self, latitude: float, longitude: float) -> tuple[float, float]:
        """WGS84 to plan-view metres (+X east, +Y north).

        The ellipsoidal tangent-plane transform the elevation sampler uses
        (``Geodesy.CarlaLocalToGeodetic``), pinned at the map's georeference origin.
        """
        origin_lat, origin_lon = self.origin
        x0, y0, z0 = self._to_ecef(origin_lat, origin_lon)
        x, y, z = self._to_ecef(latitude, longitude)
        dx, dy, dz = x - x0, y - y0, z - z0
        lat0, lon0 = math.radians(origin_lat), math.radians(origin_lon)
        sin_lat, cos_lat = math.sin(lat0), math.cos(lat0)
        sin_lon, cos_lon = math.sin(lon0), math.cos(lon0)
        east = -sin_lon * dx + cos_lon * dy
        north = -sin_lat * cos_lon * dx - sin_lat * sin_lon * dy + cos_lat * dz
        return east, north

    @staticmethod
    def _to_ecef(latitude: float, longitude: float) -> tuple[float, float, float]:
        lat, lon = math.radians(latitude), math.radians(longitude)
        sin_lat, cos_lat = math.sin(lat), math.cos(lat)
        sin_lon, cos_lon = math.sin(lon), math.cos(lon)
        n = WGS84_A / math.sqrt(1.0 - WGS84_E2 * sin_lat * sin_lat)
        return (n * cos_lat * cos_lon, n * cos_lat * sin_lon, n * (1.0 - WGS84_E2) * sin_lat)

    @staticmethod
    def _read_origin(root: ET.Element) -> tuple[float, float] | None:
        """The georeference pin at world origin, from the header's proj string."""
        node = root.find("header/geoReference")
        if node is None or not node.text:
            return None
        latitude = re.search(r"\+lat_0=([-\d.]+)", node.text)
        longitude = re.search(r"\+lon_0=([-\d.]+)", node.text)
        if not latitude or not longitude:
            return None
        return float(latitude.group(1)), float(longitude.group(1))
