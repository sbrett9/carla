"""Clip an OSM file's geometry to a lat/lon box, cutting ways exactly at the boundary.

An OSM export for a bounding box includes every way that merely *touches* the box, in full geometry
(nodes far outside the box included). netconvert never trims to the <bounds>, and its
--keep-edges.in-geo-boundary keeps whole crossing edges, so the generated road network sprawls well
past the selected area. This module pre-clips the OSM so the road network stops at the box edge:

  * interior nodes keep their original IDs (so shared intersection nodes stay shared, topology intact),
  * each way is split into the runs that lie inside the box, and
  * a new node is inserted exactly where a way crosses the boundary, so roads reach the edge but never
    extend past it.

Everything else in the document is carried through. A clip decides which *geometry* is in the area,
not which *facts* about it survive, and this one used to drop two whole categories (issue #12):

  * **relations**, of which turn restrictions are the consequential kind. They are copied verbatim,
    members and all. netconvert tolerates a relation whose members were partly clipped away: it
    warns, ignores that relation, exits 0, and leaves the rest of the graph untouched -- measured
    across five extracts -- so filtering them here would only quieten the log while losing the ones
    that do resolve.
  * **standalone nodes** -- the signals, barriers and other node-mapped features that belong to no
    way. Measured on two extracts, every node inside the box that no surviving way carries is
    tagged, so these are exactly the mapped features and nothing else.

Nodes a relation names are carried too, wherever they lie: a restriction's `via` node need not sit on
any surviving way, and dropping it turns a carried relation into a dangling reference.

**Way ids matter more than they look.** netconvert resolves a restriction's `from` and `to` way
reference by PREFIX match on the id string -- `NIImporter_OpenStreetMap::RelationHandler::findEdgeRef`
compares `edge.id[:len(ref)]` against both `ref` and `-ref`. So an id that some other id is a prefix
of will answer that other id's relations, silently, with no warning at all. See
`_synthetic_id_prefix` for what this file does about it.
"""
from __future__ import annotations

import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path



@dataclass
class BoundingBox:
    """Geographic bounding box."""
    min_lat: float
    min_lon: float
    max_lat: float
    max_lon: float

    def contains(self, lat: float, lon: float) -> bool:
        """Check if a point is inside the bounding box."""
        return self.min_lat <= lat <= self.max_lat and self.min_lon <= lon <= self.max_lon

    @classmethod
    def from_tuple(cls, bounds: tuple[float, float, float, float]) -> BoundingBox:
        """Create from (minlat, minlon, maxlat, maxlon) tuple."""
        return cls(bounds[0], bounds[1], bounds[2], bounds[3])


@dataclass
class OsmNode:
    """OSM node data."""
    node_id: str
    lat: float
    lon: float
    element: ET.Element


@dataclass
class ClipResult:
    """Result of segment clipping."""
    start: tuple[float, float]
    end: tuple[float, float]
    t0: float
    t1: float


@dataclass
class ClipSummary:
    """What one clip wrote, for a caller reporting it."""

    ways: int
    boundary_nodes: int
    standalone_nodes: int
    relation_nodes: int
    relations: int
    renumbered_runs: int




class OsmClipper:
    """Static utility class for clipping OSM files to bounding boxes."""

    @staticmethod
    def read_bounds(in_path: str | Path) -> BoundingBox | None:
        """Read bounding box from OSM file's <bounds> element."""
        for _, el in ET.iterparse(str(in_path)):
            if el.tag == "bounds":
                try:
                    return BoundingBox(
                        min_lat=float(el.get("minlat")),
                        min_lon=float(el.get("minlon")),
                        max_lat=float(el.get("maxlat")),
                        max_lon=float(el.get("maxlon")),
                    )
                except (TypeError, ValueError):
                    return None
        return None



    @staticmethod
    def clip_segment(p0: tuple[float, float], p1: tuple[float, float], box: BoundingBox) -> ClipResult | None:
        """Liang-Barsky clip of segment p0->p1 ((lat,lon)) to bounding box.
        
        Returns ClipResult with clipped endpoints and parameters, or None if fully outside.
        """
        x0, y0 = p0[1], p0[0]
        x1, y1 = p1[1], p1[0]
        dx, dy = x1 - x0, y1 - y0
        t0, t1 = 0.0, 1.0
        
        edges = [
            (-dx, x0 - box.min_lon),
            (dx, box.max_lon - x0),
            (-dy, y0 - box.min_lat),
            (dy, box.max_lat - y0),
        ]
        
        for p, q in edges:
            if p == 0.0:
                if q < 0.0:
                    return None
            else:
                r = q / p
                if p < 0.0:
                    if r > t1:
                        return None
                    if r > t0:
                        t0 = r
                else:
                    if r < t0:
                        return None
                    if r < t1:
                        t1 = r
        
        q0 = (y0 + t0 * dy, x0 + t0 * dx)
        q1 = (y0 + t1 * dy, x0 + t1 * dx)
        return ClipResult(q0, q1, t0, t1)


    # How many digits of a synthetic id are the counter. Six allows a million renumbered runs and
    # boundary nodes in one clip, against a few thousand on the largest extract here, while keeping
    # the whole id inside the signed 64-bit range netconvert parses ids into.
    SYNTHETIC_ID_COUNTER_DIGITS = 6

    @staticmethod
    def _synthetic_id_prefix(existing_ids: set[str]) -> str:
        """Leading digits for synthetic ids that no id in this document can prefix-match.

        netconvert resolves a turn restriction's way reference by prefix match on the id string
        (`NIImporter_OpenStreetMap::RelationHandler::findEdgeRef`), against both `<id>` and `-<id>`.
        Synthetic ids used to be allocated as `max(existing) + 1`, one decimal digit longer than the
        largest real id in the file, so a real id was frequently a prefix of one -- and a relation
        naming a way this clip had renumbered away would then bind to whichever unrelated run
        happened to start with those digits. findEdgeRef warns only when *two* candidates match, so
        a single wrong match was silent.

        The block returned here is one digit longer than every id in the document and has no
        proper prefix among them, which makes that impossible: a reference to a renumbered way now
        matches nothing and netconvert says "No way found for reference" out loud.
        """
        width = max((len(i) for i in existing_ids), default=1) + 1
        digits = ""
        for _ in range(width):
            for candidate in "987654321":
                if digits + candidate not in existing_ids:
                    digits += candidate
                    break
            else:
                raise ValueError(
                    "no synthetic id block is free of prefix collisions: every leading digit "
                    f"after {digits!r} is itself an id in this document")
        if int(digits + "9" * OsmClipper.SYNTHETIC_ID_COUNTER_DIGITS) >= 2**63:
            raise ValueError(
                f"ids in this document run to {width - 1} digits; a synthetic id would not fit the "
                "signed 64-bit range netconvert parses ids into")
        return digits

    @staticmethod
    def clip_osm_to_bounds(in_path: str | Path, out_path: str | Path,
                           bounds: BoundingBox | tuple[float, float, float, float]) -> ClipSummary:
        """Clip an OSM file to a bounding box, carrying everything the box still contains.

        Args:
            in_path: Input OSM file path
            out_path: Output OSM file path
            bounds: BoundingBox or (minlat, minlon, maxlat, maxlon) tuple

        Returns:
            A ClipSummary of what was written.
        """
        if isinstance(bounds, tuple):
            bounds = BoundingBox.from_tuple(bounds)

        tree = ET.parse(str(in_path))
        root = tree.getroot()

        nodes: dict[str, OsmNode] = {}
        for n in root.findall("node"):
            nid = n.get("id")
            nodes[nid] = OsmNode(nid, float(n.get("lat")), float(n.get("lon")), n)

        ways = root.findall("way")
        relations = root.findall("relation")

        prefix = OsmClipper._synthetic_id_prefix(
            set(nodes)
            | {w.get("id") for w in ways}
            | {r.get("id") for r in relations})
        counter = 0
        renumbered = 0
        used_orig: set[str] = set()
        new_nodes: list[tuple[str, float, float]] = []
        out_ways: list[tuple[str, list[str], list[ET.Element]]] = []

        def synthetic_id() -> str:
            nonlocal counter
            counter += 1
            if counter >= 10**OsmClipper.SYNTHETIC_ID_COUNTER_DIGITS:
                raise ValueError(
                    "this clip needs more synthetic ids than the block allows; raise "
                    "OsmClipper.SYNTHETIC_ID_COUNTER_DIGITS")
            return prefix + str(counter).zfill(OsmClipper.SYNTHETIC_ID_COUNTER_DIGITS)

        def make_node(lat: float, lon: float) -> str:
            nid = synthetic_id()
            new_nodes.append((nid, lat, lon))
            return nid

        for w in ways:
            refs = [nd.get("ref") for nd in w.findall("nd")]
            tags = w.findall("tag")
            runs, cur = [], []

            for i in range(len(refs) - 1):
                a, c = refs[i], refs[i + 1]
                if a not in nodes or c not in nodes:
                    if cur:
                        runs.append(cur)
                        cur = []
                    continue

                node_a, node_c = nodes[a], nodes[c]
                pa = (node_a.lat, node_a.lon)
                pc = (node_c.lat, node_c.lon)

                seg = OsmClipper.clip_segment(pa, pc, bounds)
                if seg is None:
                    if cur:
                        runs.append(cur)
                        cur = []
                    continue

                r0 = a if (seg.t0 == 0.0 and bounds.contains(pa[0], pa[1])) else make_node(*seg.start)
                r1 = c if (seg.t1 == 1.0 and bounds.contains(pc[0], pc[1])) else make_node(*seg.end)

                if not cur:
                    cur = [r0, r1]
                elif cur[-1] == r0:
                    cur.append(r1)
                else:
                    runs.append(cur)
                    cur = [r0, r1]

            if cur:
                runs.append(cur)

            kept = [run for run in runs if len(run) >= 2]
            if not kept:
                continue
            # The longest surviving run keeps the way's own id, so a relation naming that way still
            # resolves, and to the largest piece of it. `max` takes the first of equal lengths, so
            # which run that is does not depend on anything but the document.
            longest = max(range(len(kept)), key=lambda index: len(kept[index]))
            for index, run in enumerate(kept):
                for ref in run:
                    if ref in nodes:
                        used_orig.add(ref)
                if index == longest:
                    out_ways.append((w.get("id"), run, tags))
                else:
                    renumbered += 1
                    out_ways.append((synthetic_id(), run, tags))

        # Node-mapped features: everything inside the box that no surviving way carries. Measured on
        # Arapahoe_I25 (469) and SF_LaurelHeights (224), every one of these is tagged.
        standalone = {nid for nid, node in nodes.items()
                      if nid not in used_orig and bounds.contains(node.lat, node.lon)}
        # Nodes a relation names, wherever they lie. A restriction's `via` node need not sit on any
        # surviving way; without this a carried relation would reference a node that is not there.
        relation_nodes = {member.get("ref")
                          for relation in relations for member in relation.findall("member")
                          if member.get("type") == "node" and member.get("ref") in nodes}
        relation_nodes -= used_orig | standalone

        newroot = ET.Element("osm", dict(root.attrib))
        bnds = root.find("bounds")
        if bnds is not None:
            newroot.append(bnds)

        # Emitted in a fixed numeric order rather than in set-iteration order: Python randomises the
        # string hash seed once per process, so iterating the set gives a different node order in
        # every process and the same graph clipped twice never produces the same bytes. Anything that
        # records a digest of the clipped extract -- the world package's SourceOsmSha256, a cache key,
        # a build comparison -- is worthless without this.
        for nid in sorted(used_orig | standalone | relation_nodes, key=int):
            newroot.append(nodes[nid].element)

        for nid, lat, lon in new_nodes:
            ET.SubElement(
                newroot,
                "node",
                {"id": nid, "lat": f"{lat:.7f}", "lon": f"{lon:.7f}", "version": "1"},
            )

        for wid, run, tags in out_ways:
            we = ET.SubElement(newroot, "way", {"id": wid, "version": "1"})
            for ref in run:
                ET.SubElement(we, "nd", {"ref": ref})
            for t in tags:
                ET.SubElement(we, "tag", dict(t.attrib))

        for relation in relations:
            newroot.append(relation)

        ET.ElementTree(newroot).write(str(out_path), encoding="utf-8", xml_declaration=True)
        return ClipSummary(ways=len(out_ways), boundary_nodes=len(new_nodes),
                           standalone_nodes=len(standalone), relation_nodes=len(relation_nodes),
                           relations=len(relations), renumbered_runs=renumbered)
