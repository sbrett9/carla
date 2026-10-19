"""The place index: which edges of a world's network carry which street name, heading which way.

It is what turns "eastbound on Centerville Lane" into edges (`07_Scenario_Authoring.md` §2.5, §4), and
it carries its own coverage, because on some maps it is nearly empty. Measured on the shipped world
packages: 52 of 57 normal edges named on Gardnerville (91.2%), 288 of 317 on Arapahoe (90.9%), 47 of
1044 on Bahonar (4.5%, six names, all in Arabic script). A name is never an edge: on Arapahoe
`South Yosemite Street` is carried by 65 edges, so a street is always narrowed by direction and
position before it names anything.

Derived from the network alone. Every place form resolves to SUMO edges, the network carries each
edge's street name when netconvert is given `--output.street-names` (the world build always passes
it), and the OpenDRIVE's road names are written by the same invocation from the same OSM tags.

For each name: the edges carrying it, each with its heading as a compass bearing (the chord of its
rightmost lane, start to end, degrees clockwise from north in the network's frame, whose +y is grid
north), the cardinal direction that bearing falls in, its SUMO length, lane count, speed and extent;
and the edges grouped by cardinal direction, each group ordered along its direction of travel by
where its edges start. Extents are CARLA-local metres, `carla(x, y) = sumo(x, -y)`, the frame the
world package states everything else in.

What it cannot see. Whether a name is the one people at the site use -- the Bahonar script's own
comments call its roads "Shahid Rajaei Highway" and "Pasdaran Boulevard", and neither string appears
in any artifact. Whether two edges of one name form one road or two roads that share a name. A chord
bearing on a strongly curving edge describes the edge's net displacement, not its heading at any
point; the resolver refuses a direction on a street that turns through more than a right angle, and
that judgement is the resolver's, made against the network's shapes, not this index's.
"""
from __future__ import annotations

import logging
import math
import unicodedata
import xml.etree.ElementTree as ET

from carlacontrol.NetworkFingerprint import NetworkFingerprint

PLACE_INDEX_VERSION = 1

# Below this fraction of named normal edges, the index says name resolution contributes little on
# the map. The shipped maps sit at 0.91 and 0.045; any value between them separates the two cases, and
# this one is stated in the index beside the measurement so a reader sees what the warning meant.
WARN_BELOW_NAMED_FRACTION = 0.5

CARDINALS = ("north", "east", "south", "west")

logger = logging.getLogger(__name__)


class PlaceIndex:
    """The street-name index of one network, and how much of the network it covers."""

    def __init__(self, network_text: str) -> None:
        self.network_text = network_text
        self.warnings: list[str] = []
        root = ET.fromstring(network_text)
        self.normal_edges = [edge for edge in root.findall("edge")
                             if edge.get("function") in (None, "", "normal")]
        self._streets: dict[str, list[dict]] = {}
        for edge in self.normal_edges:
            name = (edge.get("name") or "").strip()
            if not name:
                continue
            record = self._edge_record(edge)
            if record is not None:
                self._streets.setdefault(name, []).append(record)
        self._table = self._build()

    @staticmethod
    def bearing_deg(start: tuple[float, float], end: tuple[float, float]) -> float:
        """Compass bearing of start->end in the network's frame, degrees clockwise from north."""
        return math.degrees(math.atan2(end[0] - start[0], end[1] - start[1])) % 360.0

    @staticmethod
    def cardinal(bearing: float) -> str:
        """The cardinal direction a bearing falls in; each owns the 90 degrees centred on it."""
        return CARDINALS[int(((bearing + 45.0) % 360.0) // 90.0)]

    @staticmethod
    def scripts(name: str) -> list[str]:
        """The writing systems a name's letters come from, by their Unicode character names."""
        found = {unicodedata.name(ch, "UNKNOWN").split()[0] for ch in name if ch.isalpha()}
        return sorted(found)

    def to_dict(self) -> dict:
        """The index as `places.json` carries it."""
        return self._table

    def _build(self) -> dict:
        streets = [self._street(name, edges)
                   for name, edges in sorted(self._streets.items(),
                                             key=lambda item: item[0].encode("utf-8"))]
        named = sum(len(edges) for edges in self._streets.values())
        total = len(self.normal_edges)
        fraction = named / total if total else 0.0
        by_script: dict[str, int] = {}
        for name in self._streets:
            for script in self.scripts(name) or ["NONE"]:
                by_script[script] = by_script.get(script, 0) + 1
        largest = max(self._streets.items(), key=lambda item: (len(item[1]), item[0]),
                      default=None)

        warnings = []
        if total and fraction < WARN_BELOW_NAMED_FRACTION:
            warnings.append(
                f"only {named} of {total} normal edges ({fraction:.1%}) carry a street name, in "
                f"{len(self._streets)} name(s) written in "
                f"{', '.join(sorted(by_script)) or 'no'} script: name resolution contributes little "
                "on this map; site places by area of interest, geographic point or gateway")
        if largest is not None and len(largest[1]) > 1:
            warnings.append(
                f"a street name is one-to-many: '{largest[0]}' is carried by {len(largest[1])} "
                "edges, so a name must be narrowed by direction and position before it names one")
        self.warnings = warnings
        for warning in warnings:
            logger.warning(warning)

        return {
            "place_index_version": PLACE_INDEX_VERSION,
            "network_fingerprint": NetworkFingerprint.of_text(self.network_text),
            "coverage": {
                "normal_edges": total,
                "named_edges": named,
                "named_fraction": round(fraction, 4),
                "distinct_names": len(self._streets),
                "names_by_script": by_script,
                "largest_name": ({"name": largest[0], "edge_count": len(largest[1])}
                                 if largest else None),
                "warn_below_named_fraction": WARN_BELOW_NAMED_FRACTION,
            },
            "warnings": warnings,
            "streets": streets,
        }

    def _street(self, name: str, edges: list[dict]) -> dict:
        directions: dict[str, list[str]] = {}
        for cardinal in CARDINALS:
            group = [e for e in edges if e["direction"] == cardinal]
            group.sort(key=lambda e: self._along(cardinal, e["_start"]))
            if group:
                directions[cardinal] = [e["edge_id"] for e in group]
        ordered = sorted(edges, key=lambda e: e["edge_id"].encode("utf-8"))
        extents = [e["extent_carla_m"] for e in edges]
        return {
            "name": name,
            "scripts": self.scripts(name),
            "edge_count": len(edges),
            "length_m": round(sum(e["length_m"] for e in edges), 2),
            "extent_carla_m": [min(x[0] for x in extents), min(x[1] for x in extents),
                               max(x[2] for x in extents), max(x[3] for x in extents)],
            "directions": directions,
            "edges": [{k: v for k, v in e.items() if not k.startswith("_")} for e in ordered],
        }

    @staticmethod
    def _along(cardinal: str, start: tuple[float, float]) -> float:
        """Distance along a direction of travel, in the network's frame (+y north)."""
        x, y = start
        return {"north": y, "east": x, "south": -y, "west": -x}[cardinal]

    @classmethod
    def _edge_record(cls, edge: ET.Element) -> dict | None:
        lanes = sorted(edge.findall("lane"), key=lambda lane: int(lane.get("index", "0")))
        if not lanes:
            return None
        shapes = [[tuple(float(v) for v in point.split(",")[:2])
                   for point in (lane.get("shape") or "").split()] for lane in lanes]
        rightmost = shapes[0]
        if len(rightmost) < 2:
            return None
        bearing = cls.bearing_deg(rightmost[0], rightmost[-1])
        xs = [p[0] for shape in shapes for p in shape]
        ys = [p[1] for shape in shapes for p in shape]
        return {
            "edge_id": edge.get("id"),
            "from_junction": edge.get("from"),
            "to_junction": edge.get("to"),
            "bearing_deg": round(bearing, 1) % 360.0,   # 359.96 rounds to 0.0, not 360.0
            "direction": cls.cardinal(bearing),
            "length_m": float(lanes[0].get("length")),
            "lane_count": len(lanes),
            "lane_ids": [lane.get("id") for lane in lanes],
            "speed_mps": float(lanes[0].get("speed")),
            "extent_carla_m": [round(min(xs), 2), round(-max(ys), 2),
                               round(max(xs), 2), round(-min(ys), 2)],
            "_start": rightmost[0],
        }
