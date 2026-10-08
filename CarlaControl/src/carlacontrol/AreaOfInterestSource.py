"""Areas of interest as an author declares them, validated before a world is built.

An area of interest is a named, stable place that a scenario can site behaviour on and an annotation
can refer to. The author declares areas in GeoJSON (RFC 7946) beside the OSM extract the world is
built from -- `<extract>.aoi.geojson`, found by name, or named explicitly -- and this class reads that
file and refuses it whole, naming every rule it breaks, before any minutes are spent building the
world. The contract is `04_Contracts.md` C5 (§7.1 the source, §7.5 the rules).

What is accepted:

  * a `FeatureCollection` of `Feature`s, each with a `Polygon`, a `MultiPolygon` (holes allowed in
    either), or a `Point` carrying `properties.radius_m` -- a circle, which GeoJSON has no primitive
    for;
  * `properties.id` matching `[a-z][a-z0-9_]{0,63}`, unique; `properties.name`, non-empty;
    `properties.kind`, optional.

What is refused, each tagged with the rule id C5 gives it:

  * V5.1 -- an id that is missing, malformed or declared twice. The pattern admits lower case only,
    so two ids differing only in case cannot both pass;
  * V5.2 -- a ring that is not closed, has fewer than four positions, crosses itself, crosses
    another ring of its polygon, encloses less than a square metre, or is a hole lying outside its
    exterior; a circle whose radius is not a positive number; a radius on anything but a point;
  * V5.3 -- an area lying wholly outside the extract's `<bounds>`;
  * V5.4 -- the same, when swapping every position's two components would put it inside: the
    message then says so by name, because `[latitude, longitude]` is the single most likely
    authoring mistake and every internal signature in this project uses that order;
  * anything that is not the shape above -- another geometry type, a position that is not a pair
    of finite numbers, a position outside WGS84's degree ranges.

What it warns about and then carries on: an altitude on a position (these are ground footprints, and
the third component is ignored), a property it does not read (not carried into the world), and a
`crs` member (RFC 7946 removed it; positions are read as WGS84 degrees whatever it says).

What it cannot see. Validation here is geometric and structural and nothing more. It has no world
yet, so it cannot say whether a road reaches an area, whether an area sits in the staging ring, or
where it falls in CARLA or SUMO metres -- `AreaOfInterestResolver` does that once the world exists.
It cannot check `kind` against a vocabulary, because the vocabulary's term list is not settled; a
`kind` is carried through unchanged. Planar tests run in degrees, which preserves every topological
property checked here at the scale of one world; the square-metre floor converts degrees to metres
with a spherical approximation that is only ever used to reject the degenerate.
"""
from __future__ import annotations

import hashlib
import json
import logging
import math
import re
from dataclasses import dataclass
from pathlib import Path

from carlacontrol.OsmClipper import BoundingBox

# The file an extract's areas are declared in: `Import/Arapahoe_I25.osm` ->
# `Import/Arapahoe_I25.aoi.geojson`.
SOURCE_SUFFIX = ".aoi.geojson"

AREA_ID_PATTERN = re.compile(r"[a-z][a-z0-9_]{0,63}")
GEOMETRY_TYPES = ("Polygon", "MultiPolygon", "Point")
READ_PROPERTIES = frozenset({"id", "name", "kind", "radius_m"})

# A ring enclosing less than this cannot hold a vehicle; one that small is almost always a ring whose
# vertices are collinear or repeated.
MINIMUM_RING_AREA_M2 = 1.0

# Metres per degree of latitude on a sphere of the WGS84 semi-major axis -- used only to judge the
# square-metre floor and to widen a circle's envelope, never to place anything.
METRES_PER_DEGREE = 6378137.0 * math.pi / 180.0

logger = logging.getLogger(__name__)

Position = tuple[float, float]
Ring = list[Position]


class AreaOfInterestError(ValueError):
    """Areas that cannot be used, and every rule they break."""

    def __init__(self, source: str, problems: list[str]) -> None:
        self.source = source
        self.problems = list(problems)
        super().__init__(
            f"{source}: refused, {len(self.problems)} problem(s):\n  - "
            + "\n  - ".join(self.problems))


@dataclass(frozen=True)
class DeclaredArea:
    """One area as the author declared it, geometry still in `[longitude, latitude]`."""

    area_id: str
    name: str
    kind: str | None
    geometry: dict
    radius_m: float | None

    @property
    def is_circle(self) -> bool:
        return self.geometry["type"] == "Point"

    @property
    def polygons(self) -> list[list[Ring]]:
        """Each polygon as its rings, exterior first, positions as `(longitude, latitude)`."""
        kind = self.geometry["type"]
        if kind == "Polygon":
            raw = [self.geometry["coordinates"]]
        elif kind == "MultiPolygon":
            raw = self.geometry["coordinates"]
        else:
            return []
        return [[[(float(p[0]), float(p[1])) for p in ring] for ring in polygon]
                for polygon in raw]

    @property
    def centre(self) -> Position:
        """A circle's centre as `(longitude, latitude)`."""
        coordinates = self.geometry["coordinates"]
        return float(coordinates[0]), float(coordinates[1])

    def positions(self) -> list[Position]:
        if self.is_circle:
            return [self.centre]
        return [p for polygon in self.polygons for ring in polygon for p in ring]


class AreaOfInterestSource:
    """A validated `.aoi.geojson`: its bytes, its digest, and the areas it declares, in file order."""

    def __init__(self, source_name: str, raw: bytes, areas: list[DeclaredArea],
                 warnings: list[str], bounds_checked: bool = False) -> None:
        self.source_name = source_name
        self.raw = raw
        self.areas = areas
        self.warnings = warnings
        # Whether V5.3 and V5.4 have run against the extract's <bounds>. When the extract has none,
        # the caller runs them against the network's own geographic extent once the world exists.
        self.bounds_checked = bounds_checked

    @property
    def sha256(self) -> str:
        """Lowercase hexadecimal SHA-256 of the file's bytes -- C5's `source_sha256`."""
        return hashlib.sha256(self.raw).hexdigest()

    @staticmethod
    def beside(extract_path: str | Path) -> Path:
        """Where an extract's areas are declared: its own name, with `.aoi.geojson` for `.osm`."""
        extract = Path(extract_path)
        stem = extract.name[:-len(extract.suffix)] if extract.suffix else extract.name
        return extract.with_name(stem + SOURCE_SUFFIX)

    @classmethod
    def discover(cls, extract_path: str | Path) -> Path | None:
        """The extract's areas file, when one exists beside it."""
        candidate = cls.beside(extract_path)
        return candidate if candidate.is_file() else None

    @classmethod
    def load(cls, path: str | Path, bounds: BoundingBox | None) -> AreaOfInterestSource:
        """Read and validate a file. `bounds` is the extract's `<bounds>`; with None the envelope
        rules (V5.3, V5.4) are left for the caller to run once some other extent is known."""
        source = Path(path)
        return cls.from_bytes(source.read_bytes(), source.name, bounds)

    @classmethod
    def from_bytes(cls, raw: bytes, source_name: str,
                   bounds: BoundingBox | None) -> AreaOfInterestSource:
        """Validate a document already in hand. Raises `AreaOfInterestError` naming every problem."""
        problems: list[str] = []
        warnings: list[str] = []
        try:
            document = json.loads(raw.decode("utf-8-sig"))
        except (UnicodeDecodeError, json.JSONDecodeError) as error:
            raise AreaOfInterestError(source_name, [f"not readable as GeoJSON: {error}"]) from error

        if not isinstance(document, dict) or document.get("type") != "FeatureCollection":
            raise AreaOfInterestError(
                source_name, ["the document must be a GeoJSON FeatureCollection"])
        features = document.get("features")
        if not isinstance(features, list):
            raise AreaOfInterestError(source_name, ["the FeatureCollection has no features array"])
        if "crs" in document:
            warnings.append(
                "the document carries a 'crs' member, which RFC 7946 removed; positions are read as "
                "WGS84 [longitude, latitude] degrees whatever it says")

        areas: list[DeclaredArea] = []
        first_declared: dict[str, int] = {}
        for index, feature in enumerate(features):
            area = cls._read_feature(index, feature, problems, warnings)
            if area is None:
                continue
            if area.area_id in first_declared:
                problems.append(
                    f"V5.1 id '{area.area_id}' is declared by feature {first_declared[area.area_id]}"
                    f" and again by feature {index}")
                continue
            first_declared[area.area_id] = index
            areas.append(area)

        source = cls(source_name, raw, areas, warnings, bounds_checked=bounds is not None)
        if bounds is not None:
            problems.extend(source.envelope_problems(bounds))
        if problems:
            raise AreaOfInterestError(source_name, problems)
        for warning in warnings:
            logger.warning("%s: %s", source_name, warning)
        return source

    def envelope_problems(self, bounds: BoundingBox) -> list[str]:
        """V5.3 and V5.4: every area must reach into the world's extent, stated in degrees."""
        problems = []
        for area in self.areas:
            positions = area.positions()
            as_given = self._envelope(positions, area.radius_m)
            if self._in_degree_range(positions) and self._intersects(as_given, bounds):
                continue
            swapped_positions = [(lat, lon) for lon, lat in positions]
            swapped = self._envelope(swapped_positions, area.radius_m)
            if self._in_degree_range(swapped_positions) and self._intersects(swapped, bounds):
                problems.append(
                    f"V5.4 area '{area.area_id}': positions appear to be [latitude, longitude]; "
                    "GeoJSON requires [longitude, latitude]. Swapped, the area falls inside the "
                    "extract's bounds; as written it does not")
            elif not self._in_degree_range(positions):
                problems.append(
                    f"V5.3 area '{area.area_id}': positions are outside WGS84 degree ranges "
                    "(longitude -180..180, latitude -90..90); RFC 7946 positions are "
                    "[longitude, latitude] in degrees, not projected meters")
            else:
                problems.append(
                    f"V5.3 area '{area.area_id}' lies wholly outside the extract's bounds: its "
                    f"envelope is longitude {as_given[0]:.6f}..{as_given[2]:.6f}, latitude "
                    f"{as_given[1]:.6f}..{as_given[3]:.6f}; the extract covers longitude "
                    f"{bounds.min_lon:.6f}..{bounds.max_lon:.6f}, latitude "
                    f"{bounds.min_lat:.6f}..{bounds.max_lat:.6f}")
        return problems

    # ---- reading one feature ------------------------------------------------------------------

    @classmethod
    def _read_feature(cls, index: int, feature, problems: list[str],
                      warnings: list[str]) -> DeclaredArea | None:
        label = f"feature {index}"
        if not isinstance(feature, dict) or feature.get("type") != "Feature":
            problems.append(f"{label} is not a GeoJSON Feature")
            return None
        properties = feature.get("properties")
        if not isinstance(properties, dict):
            problems.append(f"{label} has no properties object; id and name are required")
            return None

        area_id = properties.get("id")
        if not isinstance(area_id, str):
            problems.append(
                f"V5.1 {label}: properties.id is required and must be a string (a Feature-level "
                "'id' member is not read)")
            area_id = None
        elif not AREA_ID_PATTERN.fullmatch(area_id):
            problems.append(
                f"V5.1 {label}: properties.id {area_id!r} must match [a-z][a-z0-9_]{{0,63}} -- "
                "lower case, a letter first, no spaces")
            area_id = None
        if area_id:
            label = f"area '{area_id}'"

        name = properties.get("name")
        if not isinstance(name, str) or not name.strip():
            problems.append(f"{label}: properties.name is required and must be non-empty text")
        kind = properties.get("kind")
        if kind is not None and (not isinstance(kind, str) or not kind.strip()):
            problems.append(f"{label}: properties.kind, when given, must be non-empty text")
            kind = None

        unread = sorted(set(properties) - READ_PROPERTIES)
        if unread:
            warnings.append(f"{label}: properties {unread} are not read and are not carried into "
                            "the world")

        geometry = feature.get("geometry")
        if not isinstance(geometry, dict) or geometry.get("type") not in GEOMETRY_TYPES:
            found = geometry.get("type") if isinstance(geometry, dict) else geometry
            problems.append(f"{label}: geometry must be one of {', '.join(GEOMETRY_TYPES)}; "
                            f"found {found!r}")
            return None

        radius = properties.get("radius_m")
        start = len(problems)
        if geometry["type"] == "Point":
            radius = cls._check_circle(label, geometry, radius, problems, warnings)
        else:
            if radius is not None:
                problems.append(f"V5.2 {label}: radius_m applies to a Point only; a "
                                f"{geometry['type']} would ignore it")
            cls._check_polygons(label, geometry, problems, warnings)
        if len(problems) > start or area_id is None or not isinstance(name, str) or not name.strip():
            return None
        return DeclaredArea(area_id, name.strip(), kind.strip() if kind else None, geometry,
                            float(radius) if radius is not None else None)

    @classmethod
    def _check_circle(cls, label: str, geometry: dict, radius, problems: list[str],
                      warnings: list[str]):
        if not cls._is_position(geometry.get("coordinates")):
            problems.append(f"{label}: a Point's coordinates must be [longitude, latitude]")
        elif len(geometry["coordinates"]) > 2:
            warnings.append(f"{label}: the position's altitude is ignored; areas are ground "
                            "footprints")
        if radius is None:
            problems.append(f"V5.2 {label}: a Point needs properties.radius_m to be an area")
            return None
        if isinstance(radius, bool) or not isinstance(radius, int | float) \
                or not math.isfinite(radius) or radius <= 0:
            problems.append(f"V5.2 {label}: radius_m must be a number greater than zero; "
                            f"found {radius!r}")
            return None
        return radius

    @classmethod
    def _check_polygons(cls, label: str, geometry: dict, problems: list[str],
                        warnings: list[str]) -> None:
        coordinates = geometry.get("coordinates")
        polygons = [coordinates] if geometry["type"] == "Polygon" else coordinates
        if not isinstance(polygons, list) or not polygons:
            problems.append(f"{label}: a {geometry['type']} needs coordinates")
            return
        altitude_seen = False
        for p_index, polygon in enumerate(polygons):
            where = label if geometry["type"] == "Polygon" else f"{label} polygon {p_index}"
            if not isinstance(polygon, list) or not polygon:
                problems.append(f"V5.2 {where}: a polygon needs at least an exterior ring")
                continue
            rings: list[Ring] = []
            for r_index, ring in enumerate(polygon):
                ring_label = f"{where} {'exterior' if r_index == 0 else f'hole {r_index}'}"
                if not isinstance(ring, list) or not all(cls._is_position(p) for p in ring):
                    problems.append(f"V5.2 {ring_label}: every position must be "
                                    "[longitude, latitude] as numbers")
                    continue
                altitude_seen = altitude_seen or any(len(p) > 2 for p in ring)
                points = [(float(p[0]), float(p[1])) for p in ring]
                if cls._check_ring(ring_label, points, problems):
                    rings.append(points)
            if len(rings) == len(polygon):
                cls._check_holes(where, rings, problems)
        if altitude_seen:
            warnings.append(f"{label}: position altitudes are ignored; areas are ground footprints")

    @classmethod
    def _check_ring(cls, label: str, ring: Ring, problems: list[str]) -> bool:
        if len(ring) < 4:
            problems.append(f"V5.2 {label}: a ring needs at least four positions, the last "
                            f"repeating the first; it has {len(ring)}")
            return False
        if ring[0] != ring[-1]:
            problems.append(f"V5.2 {label}: the ring is not closed -- its last position must "
                            "repeat its first")
            return False
        distinct = cls._without_repeats(ring)
        if len(distinct) < 4:
            problems.append(f"V5.2 {label}: the ring has fewer than three distinct vertices")
            return False
        if cls._self_intersects(distinct):
            problems.append(f"V5.2 {label}: the ring crosses or touches itself")
            return False
        if not cls._in_degree_range(distinct):
            # Not degrees at all -- transposed, or projected metres. A square-metre figure computed
            # from them would be meaningless, and the envelope rules name what is actually wrong.
            return True
        area = cls._ring_area_m2(distinct)
        if area < MINIMUM_RING_AREA_M2:
            problems.append(f"V5.2 {label}: the ring encloses {area:.3g} m^2; an area needs a "
                            f"positive area of at least {MINIMUM_RING_AREA_M2:g} m^2")
            return False
        return True

    @classmethod
    def _check_holes(cls, label: str, rings: list[Ring], problems: list[str]) -> None:
        exterior = cls._without_repeats(rings[0])
        holes = [cls._without_repeats(ring) for ring in rings[1:]]
        for h_index, hole in enumerate(holes, start=1):
            if any(cls._rings_intersect(hole, other)
                   for other in [exterior] + [h for i, h in enumerate(holes, start=1) if i != h_index]):
                problems.append(f"V5.2 {label} hole {h_index}: the hole crosses or touches another "
                                "ring of its polygon")
            elif not cls._point_in_ring(hole[0], exterior):
                problems.append(f"V5.2 {label} hole {h_index}: the hole lies outside the exterior "
                                "ring")

    # ---- planar geometry, in degrees ------------------------------------------------------------

    @staticmethod
    def _is_position(value) -> bool:
        return (isinstance(value, list) and 2 <= len(value) <= 3
                and all(isinstance(c, int | float) and not isinstance(c, bool)
                        and math.isfinite(c) for c in value))

    @staticmethod
    def _without_repeats(ring: Ring) -> Ring:
        """The closed ring with consecutive duplicate positions collapsed."""
        out = [ring[0]]
        for point in ring[1:]:
            if point != out[-1]:
                out.append(point)
        if out[-1] != out[0]:
            out.append(out[0])
        return out

    @staticmethod
    def _orientation(a: Position, b: Position, c: Position) -> float:
        return (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0])

    @classmethod
    def _segments_touch(cls, p1: Position, p2: Position, q1: Position, q2: Position) -> bool:
        """Whether two closed segments share any point."""
        d1 = cls._orientation(q1, q2, p1)
        d2 = cls._orientation(q1, q2, p2)
        d3 = cls._orientation(p1, p2, q1)
        d4 = cls._orientation(p1, p2, q2)
        if ((d1 > 0 > d2) or (d1 < 0 < d2)) and ((d3 > 0 > d4) or (d3 < 0 < d4)):
            return True

        def within(a: Position, b: Position, c: Position) -> bool:
            return (min(a[0], b[0]) <= c[0] <= max(a[0], b[0])
                    and min(a[1], b[1]) <= c[1] <= max(a[1], b[1]))

        return ((d1 == 0 and within(q1, q2, p1)) or (d2 == 0 and within(q1, q2, p2))
                or (d3 == 0 and within(p1, p2, q1)) or (d4 == 0 and within(p1, p2, q2)))

    @classmethod
    def _self_intersects(cls, ring: Ring) -> bool:
        """Any two non-adjacent edges of a closed ring meeting."""
        edges = list(zip(ring, ring[1:], strict=False))
        count = len(edges)
        for i in range(count):
            for j in range(i + 2, count):
                if i == 0 and j == count - 1:
                    continue  # the first and last edges share the closing vertex
                if cls._segments_touch(*edges[i], *edges[j]):
                    return True
        return False

    @classmethod
    def _rings_intersect(cls, a: Ring, b: Ring) -> bool:
        return any(cls._segments_touch(p1, p2, q1, q2)
                   for p1, p2 in zip(a, a[1:], strict=False)
                   for q1, q2 in zip(b, b[1:], strict=False))

    @staticmethod
    def _point_in_ring(point: Position, ring: Ring) -> bool:
        """Even-odd rule."""
        x, y = point
        inside = False
        for (x1, y1), (x2, y2) in zip(ring, ring[1:], strict=False):
            if (y1 > y) != (y2 > y) and x < x1 + (y - y1) * (x2 - x1) / (y2 - y1):
                inside = not inside
        return inside

    @staticmethod
    def _ring_area_m2(ring: Ring) -> float:
        """Shoelace area in square degrees, scaled to square metres at the ring's mean latitude."""
        twice = math.fsum(x1 * y2 - x2 * y1
                          for (x1, y1), (x2, y2) in zip(ring, ring[1:], strict=False))
        mean_latitude = sum(p[1] for p in ring[:-1]) / (len(ring) - 1)
        scale = METRES_PER_DEGREE ** 2 * math.cos(math.radians(mean_latitude))
        return abs(twice) / 2.0 * scale

    @staticmethod
    def _in_degree_range(positions: list[Position]) -> bool:
        return all(-180.0 <= lon <= 180.0 and -90.0 <= lat <= 90.0 for lon, lat in positions)

    @staticmethod
    def _envelope(positions: list[Position],
                  radius_m: float | None) -> tuple[float, float, float, float]:
        """(min lon, min lat, max lon, max lat), widened by a circle's radius."""
        lons = [p[0] for p in positions]
        lats = [p[1] for p in positions]
        widen_lat = widen_lon = 0.0
        if radius_m:
            widen_lat = radius_m / METRES_PER_DEGREE
            cos_lat = max(math.cos(math.radians(max(-89.9, min(89.9, lats[0])))), 1e-6)
            widen_lon = widen_lat / cos_lat
        return (min(lons) - widen_lon, min(lats) - widen_lat,
                max(lons) + widen_lon, max(lats) + widen_lat)

    @staticmethod
    def _intersects(envelope: tuple[float, float, float, float], bounds: BoundingBox) -> bool:
        min_lon, min_lat, max_lon, max_lat = envelope
        return not (max_lon < bounds.min_lon or min_lon > bounds.max_lon
                    or max_lat < bounds.min_lat or min_lat > bounds.max_lat)
