"""Clip an OSM file's geometry to a lat/lon box, cutting ways exactly at the boundary.

An OSM export for a bounding box includes every way that merely *touches* the box, in full geometry
(nodes far outside the box included). netconvert never trims to the <bounds>, and its
--keep-edges.in-geo-boundary keeps whole crossing edges, so the generated road network sprawls well
past the selected area. This module pre-clips the OSM so the road network stops at the box edge:

  * interior nodes keep their original IDs (so shared intersection nodes stay shared, topology intact),
  * each way is split into the runs that lie inside the box, and
  * a new node is inserted exactly where a way crosses the boundary, so roads reach the edge but never
    extend past it.

Used by test_digital_twin.py;

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


    @staticmethod
    def clip_osm_to_bounds(in_path: str | Path, out_path: str | Path, bounds: BoundingBox | tuple[float, float, float, float]) -> tuple[int, int]:
        """Clip OSM file to bounding box.
        
        Args:
            in_path: Input OSM file path
            out_path: Output OSM file path
            bounds: BoundingBox or (minlat, minlon, maxlat, maxlon) tuple
            
        Returns:
            Tuple of (ways_written, new_boundary_nodes_created)
        """
        if isinstance(bounds, tuple):
            bounds = BoundingBox.from_tuple(bounds)
        
        tree = ET.parse(str(in_path))
        root = tree.getroot()

        nodes: dict[str, OsmNode] = {}
        max_id = 0
        
        for n in root.findall("node"):
            nid = n.get("id")
            node = OsmNode(nid, float(n.get("lat")), float(n.get("lon")), n)
            nodes[nid] = node
            max_id = max(max_id, int(nid))
        
        for w in root.findall("way"):
            max_id = max(max_id, int(w.get("id")))
        
        next_id = max_id + 1
        used_orig = set()
        new_nodes = []
        out_ways = []

        def make_node(lat: float, lon: float) -> str:
            nonlocal next_id
            nid = str(next_id)
            next_id += 1
            new_nodes.append((nid, lat, lon))
            return nid

        for w in root.findall("way"):
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
            
            for run in runs:
                if len(run) < 2:
                    continue
                for ref in run:
                    if ref in nodes:
                        used_orig.add(ref)
                wid = w.get("id") if len(runs) == 1 else str(next_id)
                if len(runs) > 1:
                    next_id += 1
                out_ways.append((wid, run, tags))

        newroot = ET.Element("osm", dict(root.attrib))
        bnds = root.find("bounds")
        if bnds is not None:
            newroot.append(bnds)
        
        for nid in used_orig:
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
        
        ET.ElementTree(newroot).write(str(out_path), encoding="utf-8", xml_declaration=True)
        return len(out_ways), len(new_nodes)

