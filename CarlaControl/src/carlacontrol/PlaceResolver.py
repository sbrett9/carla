"""Turn a named place into the edge, lane or lane position it means, or refuse and list candidates.

A specification never puts a bare edge id in a route: it names a place, declares what the place is,
and the compiler resolves it (`07_Scenario_Authoring.md` §3.5, §4.2). Four forms are accepted:

| Form | Example | Resolves to |
|---|---|---|
| Edge | `{"edge": "218965860#0"}`, with an optional `offset_m` | itself, after an existence check; with `offset_m`, a position on its rightmost lane |
| Lane and offset | `{"lane": "26413459_0", "offset_m": 58.9}` | a position on that lane |
| Area of interest | `{"area": "tower_03"}` | the area's inside or crossing lanes, from the world's area table |
| Street and direction | `{"street": "East Street", "direction": "east", "at": "Cross Street"}` | the edges of that name heading that way, from the place index; `at` narrows to the one arriving at the named cross street |

**It never guesses** (D7.4). A place that resolves to several edges where one is needed is refused
with every candidate listed -- direction, length and extent -- so the author can narrow it; a name that
matches nothing is refused with the nearest names. What a place is used for decides what it must
resolve to: an origin, destination or via needs one edge, a stop needs one lane and a position.

What it cannot see, and does not build: geographic point snapping, gateways, junction movements,
degenerate-edge detection and the staging-ring warning for a place (§4.2-§4.3). A place in one of
those forms is refused by the schema rather than half-resolved.
"""
from __future__ import annotations

import difflib
import xml.etree.ElementTree as ET
from dataclasses import dataclass

from carlacontrol.CompileFindings import CompileFindings

RESOLVE_CHECK = 7
POSITION_CHECK = 9


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

    def permits(self, vclass: str) -> bool:
        """SUMO's reading of `allow` / `disallow`: allow lists who may; disallow lists who may not."""
        if self.allow is not None:
            return "all" in self.allow or vclass in self.allow
        if self.disallow is not None:
            return not ("all" in self.disallow or vclass in self.disallow)
        return True


class PlaceResolver:
    """Resolves places against one world's network, place index and area table."""

    def __init__(self, network_text: str, place_index: dict | None, areas: dict[str, dict],
                 findings: CompileFindings) -> None:
        self.findings = findings
        self.place_index = place_index or {}
        self.areas = areas
        self.lanes: dict[str, NetworkLane] = {}
        self.edge_lanes: dict[str, list[str]] = {}
        self.edge_names: dict[str, str] = {}
        self.successors: dict[str, set[str]] = {}
        root = ET.fromstring(network_text)
        for edge in root.findall("edge"):
            if edge.get("function") == "internal":
                continue
            edge_id = edge.get("id")
            self.edge_names[edge_id] = edge.get("name", "")
            self.edge_lanes[edge_id] = []
            for lane in edge.findall("lane"):
                allow = lane.get("allow")
                disallow = lane.get("disallow")
                self.lanes[lane.get("id")] = NetworkLane(
                    lane.get("id"), edge_id, float(lane.get("length")), float(lane.get("speed")),
                    None if allow is None else frozenset(allow.split()),
                    None if disallow is None else frozenset(disallow.split()))
                self.edge_lanes[edge_id].append(lane.get("id"))
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
        return self._street(name, authored, where)

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
        if len(place.edges) != 1:
            self.findings.refuse(RESOLVE_CHECK, where,
                                 f"place '{place_name}' names {len(place.edges)} edges and one is "
                                 f"needed here: {self._candidates(place.edges)}. Narrow it -- "
                                 "with 'at' for a street, or by naming the edge or lane")
            return None
        return place.edges[0]

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
