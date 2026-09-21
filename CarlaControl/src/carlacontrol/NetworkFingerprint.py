"""A canonical identity for a SUMO road network, computed over the parsed graph, not the bytes.

This is the Python half of a cross-language contract. `CarlaNet.Map.NetworkFingerprint` computes
the same digest in C# when a world is built and records it in the world package; this side
recomputes it over the `map.net.xml` the package carries, so a scenario build can tell that the
network in front of it is the one the world was built from without a .NET runtime in the loop.
**The two implementations must be changed together**, or correct packages start being refused. The
canonical encoding is written out below, and the C# file carries the same description.

Why not a digest of the file? netconvert stamps every output with the moment it ran and echoes its
whole configuration -- including the temporary paths the world build hands it -- into a leading
comment, so two runs producing the same road graph produce different files. And the reverse: a
network can be re-serialised in another element order, or gain street names, without the graph a
scenario binds to changing at all.

Included: edges (id, from, to, function, priority) with their lanes (id, index, speed, length,
width, allow, disallow, shape); junctions (id, type, x, y, incLanes, intLanes); every connection,
every attribute; signal programmes (id, programID, type, ordered phase states); and the projection
(netOffset, convBoundary, origBoundary, projParameter).

Excluded: the generation comment, `<type>` defaults, street names and original names.

Edges whose `function` is `internal` are included deliberately -- the lanes inside a junction are
where a dropped turn restriction shows up, and excluding them would make that invisible. Signal
programmes are included because the world build and the scenario now come from a single netconvert
invocation, so the programme is fixed when the world is built; a scenario authored against a
differently-phased network is a real mismatch.
"""
from __future__ import annotations

import hashlib
import xml.etree.ElementTree as ET
from pathlib import Path

# Separators are drawn from the control range, which cannot occur in an XML attribute value. A
# missing attribute encodes differently from an empty one, so `width=""` and no width do not
# collide.
FIELD_SEPARATOR = b"\x1f"
ROW_SEPARATOR = b"\x1e"
ATTRIBUTE_ABSENT = b"\x00"


class NetworkFingerprint:
    """Computes the canonical fingerprint of a SUMO `.net.xml`."""

    @staticmethod
    def of_text(net_xml: str) -> str:
        """The fingerprint of a network document, as lowercase hexadecimal SHA-256."""
        return NetworkFingerprint._digest(ET.fromstring(net_xml))

    @staticmethod
    def of_file(path: str | Path) -> str:
        """The fingerprint of a network file on disk."""
        return NetworkFingerprint._digest(ET.parse(str(path)).getroot())

    @staticmethod
    def _digest(root: ET.Element) -> str:
        rows: list[bytes] = []

        # Edges, each immediately followed by its own lanes, both in id order. The edge's `name`
        # and the lanes' <param> children are deliberately not read.
        for edge in NetworkFingerprint._sorted_by_id(root.findall("edge")):
            rows.append(NetworkFingerprint._row(
                "E", edge.get("id"), edge.get("from"), edge.get("to"),
                edge.get("function"), edge.get("priority")))
            for lane in NetworkFingerprint._sorted_by_id(edge.findall("lane")):
                rows.append(NetworkFingerprint._row(
                    "L", lane.get("id"), lane.get("index"), lane.get("speed"),
                    lane.get("length"), lane.get("width"), lane.get("allow"),
                    lane.get("disallow"), lane.get("shape")))

        for junction in NetworkFingerprint._sorted_by_id(root.findall("junction")):
            rows.append(NetworkFingerprint._row(
                "J", junction.get("id"), junction.get("type"), junction.get("x"),
                junction.get("y"), junction.get("incLanes"), junction.get("intLanes")))

        # Connections carry no id, so every attribute is read and the rows are ordered by their own
        # encoded content. A connection gaining an attribute in a later netconvert is then visible
        # rather than silently ignored.
        connections = []
        for connection in root.findall("connection"):
            fields = ["C"]
            for name in sorted(connection.attrib, key=lambda n: n.encode("utf-8")):
                fields.append(f"{name}={connection.attrib[name]}")
            connections.append(NetworkFingerprint._row(*fields))
        rows.extend(sorted(connections))

        # Signal programmes: the junction they control, the programme's name, the control type and
        # the ordered phase states. Phase durations are timing rather than structure and are left
        # out; a phase appearing, disappearing or changing which movements it releases is not.
        programmes = []
        for logic in root.findall("tlLogic"):
            fields = ["T", logic.get("id"), logic.get("type"), logic.get("programID")]
            fields += [phase.get("state") for phase in logic.findall("phase")]
            programmes.append(NetworkFingerprint._row(*fields))
        rows.extend(sorted(programmes))

        # The frame the coordinates are in. Two networks with the same topology in different
        # projections are not interchangeable, and a scenario placed by coordinate would land
        # somewhere else without noticing.
        location = root.find("location")
        rows.append(NetworkFingerprint._row(
            "G",
            None if location is None else location.get("netOffset"),
            None if location is None else location.get("convBoundary"),
            None if location is None else location.get("origBoundary"),
            None if location is None else location.get("projParameter")))

        digest = hashlib.sha256()
        for row in rows:
            digest.update(row)
        return digest.hexdigest()

    @staticmethod
    def _sorted_by_id(elements: list[ET.Element]) -> list[ET.Element]:
        """Ordered by the UTF-8 encoding of the id, which every language agrees on."""
        return sorted(elements, key=lambda e: (e.get("id") or "").encode("utf-8"))

    @staticmethod
    def _row(*fields: str | None) -> bytes:
        """One encoded row: the fields, separated, terminated by the row separator."""
        encoded = [ATTRIBUTE_ABSENT if f is None else f.encode("utf-8") for f in fields]
        return FIELD_SEPARATOR.join(encoded) + ROW_SEPARATOR
