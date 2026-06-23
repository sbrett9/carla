"""Clip an OSM file's geometry to a lat/lon box, cutting ways exactly at the boundary.

An OSM export for a bounding box includes every way that merely *touches* the box, in full geometry
(nodes far outside the box included). netconvert never trims to the <bounds>, and its
--keep-edges.in-geo-boundary keeps whole crossing edges, so the generated road network sprawls well
past the selected area. This module pre-clips the OSM so the road network stops at the box edge:

  * interior nodes keep their original IDs (so shared intersection nodes stay shared, topology intact),
  * each way is split into the runs that lie inside the box, and
  * a new node is inserted exactly where a way crosses the boundary, so roads reach the edge but never
    extend past it.

Used by test_digital_twin.py; also runnable standalone:
    python osm_clip.py <in.osm> <out.osm> [minlat minlon maxlat maxlon]
(omit the box to use the file's own <bounds>).
"""
import sys
import xml.etree.ElementTree as ET


def read_bounds(in_path):
    """(minlat, minlon, maxlat, maxlon) from the <bounds> element, or None."""
    for _, el in ET.iterparse(in_path):
        if el.tag == "bounds":
            try:
                return (float(el.get("minlat")), float(el.get("minlon")),
                        float(el.get("maxlat")), float(el.get("maxlon")))
            except (TypeError, ValueError):
                return None
    return None


def _inside(lat, lon, b):
    return b[0] <= lat <= b[2] and b[1] <= lon <= b[3]


def _seg_clip(p0, p1, b):
    """Liang-Barsky clip of segment p0->p1 ((lat,lon)) to box b=(minlat,minlon,maxlat,maxlon).
    Returns (q0, q1, t0, t1) with clipped endpoints (lat,lon) and params, or None if fully outside."""
    mnla, mnlo, mxla, mxlo = b
    x0, y0 = p0[1], p0[0]                    # x=lon, y=lat
    x1, y1 = p1[1], p1[0]
    dx, dy = x1 - x0, y1 - y0
    t0, t1 = 0.0, 1.0
    for p, q in ((-dx, x0 - mnlo), (dx, mxlo - x0), (-dy, y0 - mnla), (dy, mxla - y0)):
        if p == 0.0:
            if q < 0.0:
                return None                 # parallel to an edge and outside it
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
    q0 = (y0 + t0 * dy, x0 + t0 * dx)        # (lat, lon)
    q1 = (y0 + t1 * dy, x0 + t1 * dx)
    return q0, q1, t0, t1


def clip_osm_to_bounds(in_path, out_path, b):
    """Clip in_path to box b=(minlat,minlon,maxlat,maxlon) -> out_path.
    Returns (ways_out, new_boundary_nodes)."""
    tree = ET.parse(in_path)
    root = tree.getroot()

    nodes = {}                               # id -> (lat, lon, element)
    max_id = 0
    for n in root.findall("node"):
        nid = n.get("id")
        nodes[nid] = (float(n.get("lat")), float(n.get("lon")), n)
        max_id = max(max_id, int(nid))
    for w in root.findall("way"):
        max_id = max(max_id, int(w.get("id")))
    next_id = max_id + 1

    used_orig = set()                        # interior original node ids to keep
    new_nodes = []                           # (id, lat, lon) boundary nodes
    out_ways = []                            # (id, [refs], [tag elements])

    def make_node(lat, lon):
        nonlocal next_id
        nid = str(next_id); next_id += 1
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
                    runs.append(cur); cur = []
                continue
            pa, pc = nodes[a][:2], nodes[c][:2]
            seg = _seg_clip(pa, pc, b)
            if seg is None:
                if cur:
                    runs.append(cur); cur = []
                continue
            q0, q1, t0, t1 = seg
            r0 = a if (t0 == 0.0 and _inside(pa[0], pa[1], b)) else make_node(*q0)
            r1 = c if (t1 == 1.0 and _inside(pc[0], pc[1], b)) else make_node(*q1)
            if not cur:
                cur = [r0, r1]
            elif cur[-1] == r0:
                cur.append(r1)
            else:
                runs.append(cur); cur = [r0, r1]
        if cur:
            runs.append(cur)
        for run in runs:
            if len(run) < 2:
                continue
            for ref in run:
                if ref in nodes:
                    used_orig.add(ref)
            if len(runs) == 1:
                wid = w.get("id")
            else:
                wid = str(next_id); next_id += 1
            out_ways.append((wid, run, tags))

    newroot = ET.Element("osm", dict(root.attrib))
    bnds = root.find("bounds")
    if bnds is not None:
        newroot.append(bnds)
    for nid in used_orig:
        newroot.append(nodes[nid][2])
    for nid, lat, lon in new_nodes:
        ET.SubElement(newroot, "node",
                      {"id": nid, "lat": f"{lat:.7f}", "lon": f"{lon:.7f}", "version": "1"})
    for wid, run, tags in out_ways:
        we = ET.SubElement(newroot, "way", {"id": wid, "version": "1"})
        for ref in run:
            ET.SubElement(we, "nd", {"ref": ref})
        for t in tags:
            ET.SubElement(we, "tag", dict(t.attrib))
    ET.ElementTree(newroot).write(out_path, encoding="utf-8", xml_declaration=True)
    return len(out_ways), len(new_nodes)


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print(__doc__); sys.exit(2)
    src, dst = sys.argv[1], sys.argv[2]
    box = (tuple(float(v) for v in sys.argv[3:7]) if len(sys.argv) >= 7 else read_bounds(src))
    if box is None:
        print("ERROR: no box given and no <bounds> in the OSM file", file=sys.stderr); sys.exit(1)
    nw, nn = clip_osm_to_bounds(src, dst, box)
    print(f"clipped -> {dst}: {nw} ways, {nn} new boundary nodes; box={box}")
