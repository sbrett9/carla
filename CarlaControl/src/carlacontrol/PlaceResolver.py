"""Turn a named place into the edge, lane or lane position it means, or refuse and list candidates.

A specification never puts a bare edge id in a route: it names a place, declares what the place is,
and the compiler resolves it (`07_Scenario_Authoring.md` §3.5, §4.2). Seven forms are accepted:

| Form | Example | Resolves to |
|---|---|---|
| Edge | `{"edge": "218965860#0"}`, with an optional `offset_m` | itself, after an existence check; with `offset_m`, a position on its rightmost lane |
| Lane and offset | `{"lane": "26413459_0", "offset_m": 58.9}` | a position on that lane |
| Area of interest | `{"area": "tower_03"}` | the area's inside or crossing lanes, from the world's area table |
| Street and direction | `{"street": "East Street", "direction": "east", "at": "Cross Street"}` | the edges of that name heading that way, from the place index; `at` narrows to the one arriving at the named cross street, `near` to the one nearest a point |
| Geographic point | `{"lat": 27.1, "lon": 56.2, "max_snap_m": 25, "vclass": "army"}` | the position on the nearest lane -- of one that admits `vclass`, where given, else of any lane a road vehicle may drive -- with the snap distance reported |
| Gateway | `{"gateway": "south", "travel": "in", "street": "South Valley Highway"}` | the edges entering (`in`) or leaving (`out`) the world at a dead end on that side of it |
| Junction movement | `{"from_street": "West Street", "to_street": "East Street"}` | the two edges either side of a junction that one connection joins -- never the internal edge -- usable as a `via` |

A point is placed by `GeodeticFrame`, the WGS84 transform the telemetry and the imagery use, at the
world's origin: it agrees with the network's own projection within 0.4 mm on every shipped world
(`GeodeticFrame`), and check 5 refuses the one kind of world where they would part. A position on a
lane is SUMO's -- the projected arc length scaled by the lane's `length` over its shape length.

A gateway is where a road leaves the world: an end served by a single road -- its edges one id either
way -- within `GATEWAY_MARGIN_M` of the network's boundary, on the side it lies nearest. It is not
netconvert's `dead_end` junction type: a two-way road cut by the clip ends at a `priority` junction
holding its turnaround, as all 5 boundary ends on Gardnerville do.

**It never guesses** (D7.4). A place that resolves to several edges where one is needed is refused
with every candidate listed -- direction, length and extent -- so the author can narrow it; a name that
matches nothing is refused with the nearest names. What a place is used for decides what it must
resolve to: an origin, destination or via needs one edge, a stop needs one lane and a position.

What it cannot see, and does not build: degenerate-edge detection, a street that curves through more
than 90 degrees, a warning threshold below `max_snap_m`, and the staging-ring warning for a place
(§4.3). Two lanes of different edges within `TIE_M` of a point are a tie, refused with both named.
"""
from __future__ import annotations

import difflib
import math
import xml.etree.ElementTree as ET
from dataclasses import dataclass

from carlacontrol.CompileFindings import CompileFindings
from carlacontrol.GeodeticFrame import GeodeticFrame

RESOLVE_CHECK = 7
POSITION_CHECK = 9

# Two lanes of different edges this close in distance to a point are a tie the resolver will not break.
TIE_M = 0.01

# Classes that make a lane a road a vehicle may drive, for a point with no `vclass`: anything but a
# pedestrian or a bicycle.
NOT_ROAD_VEHICLES = frozenset({"pedestrian", "bicycle"})

SIDES = ("north", "east", "south", "west")

# How far inside the network's boundary a gateway's end may lie. Measured on the three shipped
# worlds: every end served by a single road that lies on the clip boundary is within 4.99 m of it,
# and the nearest such end inside the map -- a cul-de-sac -- is 45.77 m in.
GATEWAY_MARGIN_M = 10.0

Point = tuple[float, float]


@dataclass(frozen=True)
class ResolvedPlace:
    """What a place became: its candidate edges, and a lane position when it names one."""

    name: str
    authored: dict
    form: str
    edges: tuple[str, ...]
    lane: str | None = None
    start_pos: float | None = None
    end_pos: float | None = None
    street: str = ""
    detail: str = ""

    def to_dict(self) -> dict:
        return {"name": self.name, "authored": self.authored, "form": self.form,
                "edges": list(self.edges), "lane": self.lane, "start_pos": self.start_pos,
                "end_pos": self.end_pos, "street": self.street, "detail": self.detail}


@dataclass(frozen=True)
class NetworkLane:
    """One normal lane of the world's network: what a place and a permission check need."""

    lane_id: str
    edge_id: str
    length: float
    speed: float
    allow: frozenset[str] | None
    disallow: frozenset[str] | None
    shape: tuple[Point, ...] = ()

    def permits(self, vclass: str) -> bool:
        """SUMO's reading of `allow` / `disallow`: allow lists who may; disallow lists who may not."""
        if self.allow is not None:
            return "all" in self.allow or vclass in self.allow
        if self.disallow is not None:
            return not ("all" in self.disallow or vclass in self.disallow)
        return True

    def admits_a_road_vehicle(self) -> bool:
        """Whether any class but a pedestrian or a bicycle may drive this lane."""
        if self.allow is not None:
            return "all" in self.allow or bool(self.allow - NOT_ROAD_VEHICLES)
        return not (self.disallow is not None and "all" in self.disallow)

    def nearest(self, point: Point) -> tuple[float, float]:
        """The distance from `point` to this lane's shape, and the lane position of the nearest
        point on it: the arc length scaled by `length` over the shape length, as SUMO maps it."""
        best = (math.inf, 0.0)
        travelled = 0.0
        for a, b in zip(self.shape, self.shape[1:], strict=False):
            dx, dy = b[0] - a[0], b[1] - a[1]
            segment = math.hypot(dx, dy)
            t = 0.0 if segment == 0.0 else max(0.0, min(1.0, ((point[0] - a[0]) * dx
                                                              + (point[1] - a[1]) * dy)
                                                             / (segment * segment)))
            distance = math.hypot(point[0] - (a[0] + t * dx), point[1] - (a[1] + t * dy))
            if distance < best[0]:
                best = (distance, travelled + t * segment)
            travelled += segment
        scale = self.length / travelled if travelled > 0.0 else 1.0
        return best[0], min(self.length, best[1] * scale)


class PlaceResolver:
    """Resolves places against one world's network, place index and area table."""

    def __init__(self, network_text: str, place_index: dict | None, areas: dict[str, dict],
                 findings: CompileFindings, origin: tuple[float, float] | None = None) -> None:
        self.findings = findings
        self.place_index = place_index or {}
        self.areas = areas
        # The world's origin, which places a latitude and longitude on the network (GeodeticFrame).
        self.frame = None if origin is None else GeodeticFrame(*origin)
        self.lanes: dict[str, NetworkLane] = {}
        self.edge_lanes: dict[str, list[str]] = {}
        self.edge_names: dict[str, str] = {}
        self.edge_ends: dict[str, tuple[str, str]] = {}
        self.successors: dict[str, set[str]] = {}
        root = ET.fromstring(network_text)
        for edge in root.findall("edge"):
            if edge.get("function") == "internal":
                continue
            edge_id = edge.get("id")
            self.edge_names[edge_id] = edge.get("name", "")
            self.edge_ends[edge_id] = (edge.get("from", ""), edge.get("to", ""))
            self.edge_lanes[edge_id] = []
            for lane in edge.findall("lane"):
                allow = lane.get("allow")
                disallow = lane.get("disallow")
                shape = tuple(tuple(float(v) for v in point.split(",")[:2])
                              for point in (lane.get("shape") or "").split())
                self.lanes[lane.get("id")] = NetworkLane(
                    lane.get("id"), edge_id, float(lane.get("length")), float(lane.get("speed")),
                    None if allow is None else frozenset(allow.split()),
                    None if disallow is None else frozenset(disallow.split()), shape)
                self.edge_lanes[edge_id].append(lane.get("id"))
        self.junctions = {junction.get("id"): (junction.get("type", ""), float(junction.get("x", 0)),
                                               float(junction.get("y", 0)))
                          for junction in root.findall("junction")}
        location = root.find("location")
        bounds = location.get("convBoundary", "") if location is not None else ""
        self.boundary = tuple(float(v) for v in bounds.split(",")) if bounds.count(",") == 3 else None
        for connection in root.findall("connection"):
            source, target = connection.get("from"), connection.get("to")
            if source in self.edge_lanes and target in self.edge_lanes:
                self.successors.setdefault(source, set()).add(target)
        self._streets = {street["name"]: street for street in self.place_index.get("streets", [])}
        self._edge_records = {edge["edge_id"]: edge
                              for street in self.place_index.get("streets", [])
                              for edge in street["edges"]}
        self.resolved: dict[str, ResolvedPlace] = {}
        self.declared: set[str] = set()

    # -- resolving declared places ----------------------------------------------------------------

    def resolve_all(self, places: dict[str, dict]) -> dict[str, ResolvedPlace]:
        """Resolve every declared place, whether or not anything uses it, recording refusals."""
        self.declared = set(places)
        for name, authored in places.items():
            resolved = self._resolve(name, authored)
            if resolved is not None:
                self.resolved[name] = resolved
        return self.resolved

    def _resolve(self, name: str, authored: dict) -> ResolvedPlace | None:
        where = f"place {name}"
        if "edge" in authored:
            edge = authored["edge"]
            if not self._edge_exists(edge, where):
                return None
            if "offset_m" in authored:
                lane = self.edge_lanes[edge][0]
                if not self._position_ok(lane, authored["offset_m"], where):
                    return None
                return ResolvedPlace(name, authored, "edge", (edge,), lane, None,
                                     float(authored["offset_m"]), self.edge_names.get(edge, ""))
            return ResolvedPlace(name, authored, "edge", (edge,), street=self.edge_names.get(edge, ""))
        if "lane" in authored:
            lane = authored["lane"]
            if lane not in self.lanes:
                self._no_match(where, "lane", lane, list(self.lanes))
                return None
            if not self._position_ok(lane, authored["offset_m"], where):
                return None
            edge = self.lanes[lane].edge_id
            return ResolvedPlace(name, authored, "lane", (edge,), lane, None,
                                 float(authored["offset_m"]), self.edge_names.get(edge, ""))
        if "area" in authored:
            return self._area(name, authored, where)
        if "lat" in authored:
            return self._point(name, authored, where)
        if "gateway" in authored:
            return self._gateway(name, authored, where)
        if "from_street" in authored:
            return self._movement(name, authored, where)
        return self._street(name, authored, where)

    def _point(self, name: str, authored: dict, where: str) -> ResolvedPlace | None:
        """The position on the nearest lane admitting the class, refused past `max_snap_m`."""
        point = self._local(authored, where)
        if point is None:
            return None
        vclass = authored.get("vclass")
        candidates = [lane for lane in self.lanes.values() if lane.shape
                      and (lane.permits(vclass) if vclass else lane.admits_a_road_vehicle())]
        if not candidates:
            self.findings.refuse(RESOLVE_CHECK, where, "no lane of this world admits "
                                 + (f"class '{vclass}'" if vclass else "a road vehicle"))
            return None
        ranked = sorted(((*lane.nearest(point), lane) for lane in candidates),
                        key=lambda item: (item[0], item[2].lane_id))
        distance, position, lane = ranked[0]
        limit = float(authored["max_snap_m"])
        admitting = f" admitting '{vclass}'" if vclass else ""
        if distance > limit:
            self.findings.refuse(RESOLVE_CHECK, where, f"the nearest lane{admitting}, {lane.lane_id}, "
                                 f"is {distance:.2f} m from the point, past max_snap_m {limit:g}")
            return None
        tied = [other for d, _, other in ranked[1:]
                if d - distance <= TIE_M and other.edge_id != lane.edge_id]
        if tied:
            self.findings.refuse(RESOLVE_CHECK, where, f"the point is {distance:.2f} m from lanes of "
                                 f"{len(tied) + 1} edges alike: "
                                 f"{self._candidates([lane.edge_id, *[t.edge_id for t in tied]])}. "
                                 "Move the point towards one, or name its lane")
            return None
        return ResolvedPlace(name, authored, "point", (lane.edge_id,), lane.lane_id, None,
                             round(position, 2), self.edge_names.get(lane.edge_id, ""),
                             f"snapped {distance:.2f} m to {lane.lane_id}{admitting}")

    def _local(self, authored: dict, where: str) -> Point | None:
        """A latitude and longitude as network metres: CARLA-local with y negated."""
        if self.frame is None:
            self.findings.refuse(RESOLVE_CHECK, where, "names a latitude and longitude, and the "
                                 "world package records no origin to place them from")
            return None
        x, y = self.frame.to_carla(longitude=float(authored["lon"]), latitude=float(authored["lat"]))
        return x, -y

    def _gateway(self, name: str, authored: dict, where: str) -> ResolvedPlace | None:
        """The edges entering or leaving the world at a dead end on one side of it."""
        if self.boundary is None:
            self.findings.refuse(RESOLVE_CHECK, where, "the network states no convBoundary, so it "
                                 "has no sides")
            return None
        side, travel, street = authored["gateway"], authored["travel"], authored.get("street")
        roads: dict[str, set[str]] = {}
        for edge, ends in self.edge_ends.items():
            for node in ends:
                roads.setdefault(node, set()).add(edge.lstrip("-"))
        at_side = []
        for edge, (start, end) in self.edge_ends.items():
            node = start if travel == "in" else end
            _, x, y = self.junctions.get(node, ("", 0.0, 0.0))
            if (len(roads.get(node, ())) == 1 and self._gap(x, y) <= GATEWAY_MARGIN_M
                    and self._side(x, y) == side):
                at_side.append(edge)
        found = [edge for edge in at_side if street is None or self.edge_names.get(edge) == street]
        if not found:
            known = sorted({self.edge_names.get(edge, "") for edge in at_side} - {""})
            self.findings.refuse(RESOLVE_CHECK, where, f"no edge "
                                 f"{'enters' if travel == 'in' else 'leaves'} the world on its "
                                 f"{side} side" + (f" on '{street}'" if street else "")
                                 + (f"; streets there: {', '.join(known)}" if known else ""))
            return None
        found.sort()
        return ResolvedPlace(name, authored, "gateway", tuple(found),
                             street=street or "", detail=f"{len(found)} edge(s) "
                             f"{'entering' if travel == 'in' else 'leaving'} on the {side} side")

    def _gaps(self, x: float, y: float) -> dict[str, float]:
        """How far inside each side of the network's boundary a point lies; y grows northward."""
        west, south, east, north = self.boundary
        return {"north": north - y, "east": east - x, "south": y - south, "west": x - west}

    def _gap(self, x: float, y: float) -> float:
        return min(self._gaps(x, y).values())

    def _side(self, x: float, y: float) -> str:
        """The side of the network's boundary a point lies nearest."""
        gaps = self._gaps(x, y)
        return min(SIDES, key=lambda side: (gaps[side], SIDES.index(side)))

    def _movement(self, name: str, authored: dict, where: str) -> ResolvedPlace | None:
        """The two edges either side of a junction, joined by one connection."""
        wanted = {"from": (authored["from_street"], authored.get("from_direction")),
                  "to": (authored["to_street"], authored.get("to_direction"))}
        for street, direction in wanted.values():
            if street not in set(self.edge_names.values()):
                self._no_match(where, "street", street, sorted(set(self.edge_names.values()) - {""}))
                return None
            if direction is not None and not self._edge_records:
                self.findings.refuse(RESOLVE_CHECK, where, "narrows by direction, and the world "
                                     "package publishes no place index")
                return None

        def fits(edge: str, key: str) -> bool:
            street, direction = wanted[key]
            return (self.edge_names.get(edge) == street
                    and (direction is None or self._edge_records.get(edge, {}).get("direction")
                         == direction))

        pairs = sorted((a, b) for a in self.edge_names if fits(a, "from")
                       for b in self.successors.get(a, ()) if fits(b, "to"))
        if len(pairs) != 1:
            self.findings.refuse(RESOLVE_CHECK, where,
                                 f"'{wanted['from'][0]}' turns onto '{wanted['to'][0]}' by "
                                 f"{len(pairs)} connections"
                                 + (": " + "; ".join(f"{a} -> {b}" for a, b in pairs)
                                    + ". Narrow it with from_direction or to_direction"
                                    if pairs else ""))
            return None
        return ResolvedPlace(name, authored, "movement", pairs[0], street=wanted["from"][0],
                             detail=f"{wanted['from'][0]} onto {wanted['to'][0]}: "
                             f"{pairs[0][0]} -> {pairs[0][1]}")

    def _area(self, name: str, authored: dict, where: str) -> ResolvedPlace | None:
        area_id = authored["area"]
        area = self.areas.get(area_id)
        if area is None:
            self._no_match(where, "area", area_id, list(self.areas))
            return None
        lanes = [lane for lane in area.get("sumo", {}).get("lanes", [])
                 if lane.get("containment") in ("inside", "crossing") and lane["lane_id"] in self.lanes]
        edges = tuple(dict.fromkeys(lane["edge_id"] for lane in lanes))
        if not edges:
            self.findings.refuse(RESOLVE_CHECK, where, f"area '{area_id}' has no lane inside or "
                                 "crossing it, so it names no road")
            return None
        if len(lanes) == 1:
            lane = lanes[0]
            return ResolvedPlace(name, authored, "area", edges, lane["lane_id"],
                                 float(lane["s_begin_m"]), float(lane["s_end_m"]),
                                 self.edge_names.get(lane["edge_id"], ""),
                                 f"the one lane of area '{area_id}'")
        return ResolvedPlace(name, authored, "area", edges, detail=f"{len(lanes)} lanes of area "
                             f"'{area_id}': {', '.join(lane['lane_id'] for lane in lanes)}")

    def _street(self, name: str, authored: dict, where: str) -> ResolvedPlace | None:
        street_name = authored["street"]
        street = self._streets.get(street_name)
        if street is None:
            if not self._streets:
                self.findings.refuse(RESOLVE_CHECK, where, "names a street, and the world package "
                                     "publishes no place index")
            else:
                self._no_match(where, "street", street_name, list(self._streets))
            return None
        direction = authored["direction"]
        run = street.get("directions", {}).get(direction, [])
        if not run:
            self.findings.refuse(RESOLVE_CHECK, where, f"'{street_name}' has no edge heading "
                                 f"{direction}; it heads {sorted(street.get('directions', {}))}")
            return None
        if "at" in authored and "near" in authored:
            self.findings.refuse(RESOLVE_CHECK, where, "narrows by both 'at' and 'near'; give one")
            return None
        if "near" in authored:
            point = self._local(authored["near"], where)
            if point is None:
                return None
            nearest = min(run, key=lambda edge: (min(self.lanes[lane].nearest(point)[0]
                                                     for lane in self.edge_lanes[edge]), edge))
            distance = min(self.lanes[lane].nearest(point)[0] for lane in self.edge_lanes[nearest])
            return ResolvedPlace(name, authored, "street", (nearest,), street=street_name,
                                 detail=f"{street_name} heading {direction}, the edge "
                                 f"{distance:.2f} m from the point")
        if "at" in authored:
            cross = self._streets.get(authored["at"])
            if cross is None:
                self._no_match(where, "street", authored["at"], list(self._streets))
                return None
            junctions = {edge[key] for edge in cross["edges"]
                         for key in ("from_junction", "to_junction")}
            arriving = [edge for edge in run if self._edge_records[edge]["to_junction"] in junctions]
            if len(arriving) != 1:
                self.findings.refuse(RESOLVE_CHECK, where,
                                     f"'{street_name}' heading {direction} arrives at "
                                     f"'{authored['at']}' on {len(arriving)} edges"
                                     + (": " + self._candidates(arriving) if arriving else ""))
                return None
            run = arriving
        return ResolvedPlace(name, authored, "street", tuple(run), street=street_name,
                             detail=f"{street_name} heading {direction}"
                             + (f" at {authored['at']}" if "at" in authored else ""))

    # -- what a use of a place requires ------------------------------------------------------------

    def single_edge(self, place_name: str, where: str) -> str | None:
        """The one edge a place names, for an origin, destination or via; refuses otherwise."""
        place = self._named(place_name, where)
        if place is None:
            return None
        if place.form == "movement":
            self.findings.refuse(RESOLVE_CHECK, where, f"place '{place_name}' is a junction movement, "
                                 "two edges, and one is needed here; a movement is a via")
            return None
        if len(place.edges) != 1:
            self.findings.refuse(RESOLVE_CHECK, where,
                                 f"place '{place_name}' names {len(place.edges)} edges and one is "
                                 f"needed here: {self._candidates(place.edges)}. Narrow it -- "
                                 "with 'at' for a street, or by naming the edge or lane")
            return None
        return place.edges[0]

    def via_edges(self, place_name: str, where: str) -> list[str] | None:
        """What a via place contributes to a route: a junction movement's two edges, in order, or
        the one edge any other place names."""
        place = self._named(place_name, where)
        if place is not None and place.form == "movement":
            return list(place.edges)
        edge = self.single_edge(place_name, where)
        return None if edge is None else [edge]

    def stop_position(self, place_name: str, where: str) -> tuple[str, float | None, float] | None:
        """The lane, start position and end position a stop at a place uses; refuses otherwise."""
        place = self._named(place_name, where)
        if place is None:
            return None
        if place.lane is None or place.end_pos is None:
            self.findings.refuse(RESOLVE_CHECK, where,
                                 f"place '{place_name}' names no lane position, and a stop needs "
                                 "one: declare it as a lane with offset_m, an edge with offset_m, "
                                 "or an area holding exactly one lane")
            return None
        return place.lane, place.start_pos, place.end_pos

    def _named(self, place_name: str, where: str) -> ResolvedPlace | None:
        place = self.resolved.get(place_name)
        if place is None:
            if place_name not in self.declared:
                self.findings.refuse(8, where, f"names place '{place_name}', which places does not "
                                     "declare")
            return None
        return place

    # -- network questions ------------------------------------------------------------------------

    def edge_permits(self, edge_id: str, vclass: str) -> bool:
        return any(self.lanes[lane].permits(vclass) for lane in self.edge_lanes.get(edge_id, []))

    def unconnected(self, edges: list[str]) -> list[tuple[str, str]]:
        """Consecutive pairs of an explicit edge list with no connection between them."""
        return [(a, b) for a, b in zip(edges, edges[1:], strict=False)
                if b not in self.successors.get(a, set())]

    # -- helpers ----------------------------------------------------------------------------------

    def _edge_exists(self, edge: str, where: str) -> bool:
        if edge in self.edge_lanes:
            return True
        self._no_match(where, "edge", edge, list(self.edge_lanes))
        return False

    def _position_ok(self, lane: str, offset: float, where: str) -> bool:
        length = self.lanes[lane].length
        if offset > length:
            self.findings.refuse(POSITION_CHECK, where, f"offset {offset:g} m lies beyond lane "
                                 f"{lane}, which is {length:g} m long")
            return False
        return True

    def _no_match(self, where: str, kind: str, value: str, known: list[str]) -> None:
        near = difflib.get_close_matches(value, known, n=5, cutoff=0.5)
        self.findings.refuse(RESOLVE_CHECK, where, f"{kind} '{value}' is not in this world"
                             + (f"; nearest: {', '.join(near)}" if near else ""))

    def _candidates(self, edges) -> str:
        parts = []
        for edge in edges:
            record = self._edge_records.get(edge)
            if record:
                parts.append(f"{edge} ({record['direction']}, {record['length_m']:g} m, extent "
                             f"{record['extent_carla_m']})")
            else:
                lane = self.edge_lanes.get(edge, [None])[0]
                length = self.lanes[lane].length if lane in self.lanes else 0.0
                parts.append(f"{edge} ({length:g} m)")
        return "; ".join(parts)
