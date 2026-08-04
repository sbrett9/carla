"""Measure how the photoreal (DSM) and bare-earth (DTM) surfaces behave at a grade separation.

Reads the drape cache written during the world build (the exact DSM/DTM grids the drape pipeline
consumed) plus the source .osm, and reports, for three populations of sample points:

  deck       - centreline of ways tagged bridge=*, i.e. the elevated structure
  under      - centreline of ways that cross a deck in plan while sharing no node with it
               (OSM only creates a junction when ways share a node, so "crosses but shares no
               node" IS grade separation, with no reliance on the bridge tag)
  open       - road well away from any deck; the control

For each it prints the DSM, the DTM, their gap, and the choice DrapeTerrain.Despike would make
(gap <= max-drape keeps the DSM, else falls back to the DTM).

Nothing here talks to the server, so it is safe to run against a live session.

    python CarlaNet\\python\\probe_overpass.py --osm Import\\Arapahoe_I25.osm

What the numbers mean:
  * open-road gap is the SYSTEMATIC photoreal-vs-bare-earth offset for this site. Any gap threshold
    has to sit above it.
  * under-road gap tells you whether the vertical DSM ray is hitting the deck instead of the roadway.
    That is the defect: the road below inherits the deck's height.
  * deck gap is the clearance recoverable from the photoreal without a constant.
  * If the open and under populations overlap, no single threshold can separate them and the
    per-road layer classification is doing the real work.

Caveat this probe CANNOT settle: a top-down sample hits the topmost surface either way, so it
distinguishes "there is an elevated surface here" from "there is not", but not "hollow deck" from
"solid smeared ramp". That difference matters for rendering, not for elevation extraction.
"""
import argparse
import math
import os
import struct
import sys
import xml.etree.ElementTree as ET
from array import array

WGS84_A = 6378137.0
WGS84_F = 1.0 / 298.257223563
WGS84_E2 = WGS84_F * (2.0 - WGS84_F)

CACHE_MAGIC = 0x44525031  # "DRP1"
HEADER_FORMAT = "<i3d3d2i2q"  # magic, origin lat/lon/alt, minX/minY/cell, cols/rows, photo/ground
HEADER_BYTES = struct.calcsize(HEADER_FORMAT)

ROAD_VALUES = {
    "motorway", "motorway_link", "trunk", "trunk_link", "primary", "primary_link",
    "secondary", "secondary_link", "tertiary", "tertiary_link", "unclassified",
    "residential", "living_street", "service", "road",
}
BRIDGE_FALSE = {"no", "false", "0", ""}


# ── geodesy (ports CarlaNet.Types.Geom.Geodesy) ──────────────────────────────

def geodetic_to_ecef(lat_deg, lon_deg, alt=0.0):
    lat, lon = math.radians(lat_deg), math.radians(lon_deg)
    s_lat, c_lat = math.sin(lat), math.cos(lat)
    s_lon, c_lon = math.sin(lon), math.cos(lon)
    n = WGS84_A / math.sqrt(1.0 - WGS84_E2 * s_lat * s_lat)
    return ((n + alt) * c_lat * c_lon,
            (n + alt) * c_lat * s_lon,
            (n * (1.0 - WGS84_E2) + alt) * s_lat)


def geodetic_to_carla_local(origin, lat_deg, lon_deg):
    """CARLA local metres (+X east, +Y south, i.e. -north) at a georeference origin."""
    o_lat, o_lon, o_alt = origin
    x0, y0, z0 = geodetic_to_ecef(o_lat, o_lon, o_alt)
    x, y, z = geodetic_to_ecef(lat_deg, lon_deg, 0.0)
    dx, dy, dz = x - x0, y - y0, z - z0
    lat0, lon0 = math.radians(o_lat), math.radians(o_lon)
    s_lat, c_lat = math.sin(lat0), math.cos(lat0)
    s_lon, c_lon = math.sin(lon0), math.cos(lon0)
    east = -s_lon * dx + c_lon * dy
    north = -s_lat * c_lon * dx - s_lat * s_lon * dy + c_lat * dz
    return east, -north


# ── drape cache ──────────────────────────────────────────────────────────────

class DrapeCache:
    def __init__(self, path):
        self.path = path
        with open(path, "rb") as fh:
            head = fh.read(HEADER_BYTES)
            if len(head) < HEADER_BYTES:
                raise ValueError("truncated header")
            (magic, o_lat, o_lon, o_alt, self.min_x, self.min_y, self.cell,
             self.cols, self.rows, self.photo_asset, self.ground_asset) = struct.unpack(HEADER_FORMAT, head)
            if magic != CACHE_MAGIC:
                raise ValueError("not a drape cache")
            self.origin = (o_lat, o_lon, o_alt)
            n = self.cols * self.rows
            self.dsm = array("d"); self.dsm.fromfile(fh, n)
            self.dtm = array("d"); self.dtm.fromfile(fh, n)

    @property
    def max_x(self):
        return self.min_x + (self.cols - 1) * self.cell

    @property
    def max_y(self):
        return self.min_y + (self.rows - 1) * self.cell

    def sample(self, grid, x, y):
        """Bilinear sample; NaN if outside the grid or any corner is NaN."""
        fc = (x - self.min_x) / self.cell
        fr = (y - self.min_y) / self.cell
        c0, r0 = int(math.floor(fc)), int(math.floor(fr))
        if c0 < 0 or r0 < 0 or c0 + 1 >= self.cols or r0 + 1 >= self.rows:
            return float("nan")
        tc, tr = fc - c0, fr - r0
        total = 0.0
        for dr, wr in ((0, 1.0 - tr), (1, tr)):
            for dc, wc in ((0, 1.0 - tc), (1, tc)):
                v = grid[(r0 + dr) * self.cols + (c0 + dc)]
                if math.isnan(v):
                    return float("nan")
                total += v * wr * wc
        return total

    def both(self, x, y):
        return self.sample(self.dsm, x, y), self.sample(self.dtm, x, y)


def header_only(path):
    with open(path, "rb") as fh:
        head = fh.read(HEADER_BYTES)
    if len(head) < HEADER_BYTES:
        return None
    vals = struct.unpack(HEADER_FORMAT, head)
    return vals if vals[0] == CACHE_MAGIC else None


def pick_cache(cache_dir, osm_bounds):
    """Choose the cache whose grid covers this OSM's bounds under its own origin."""
    best = None
    for name in sorted(os.listdir(cache_dir)):
        if not name.endswith(".bin"):
            continue
        path = os.path.join(cache_dir, name)
        vals = header_only(path)
        if vals is None:
            continue
        _, o_lat, o_lon, o_alt, min_x, min_y, cell, cols, rows, _, _ = vals
        origin = (o_lat, o_lon, o_alt)
        xs, ys = [], []
        for la, lo in ((osm_bounds[0], osm_bounds[1]), (osm_bounds[0], osm_bounds[3]),
                       (osm_bounds[2], osm_bounds[1]), (osm_bounds[2], osm_bounds[3])):
            x, y = geodetic_to_carla_local(origin, la, lo)
            xs.append(x); ys.append(y)
        # How far the cache grid's lower corner is from where this OSM's bounds project.
        err = max(abs(min(xs) - min_x), abs(min(ys) - min_y))
        span_err = max(abs((max(xs) - min(xs)) - (cols - 1) * cell),
                       abs((max(ys) - min(ys)) - (rows - 1) * cell))
        score = err + span_err
        if best is None or score < best[0]:
            best = (score, path, cols, rows, cell)
    return best


# ── OSM ──────────────────────────────────────────────────────────────────────

def load_osm(path):
    nodes, ways, bounds = {}, [], None
    for _, elem in ET.iterparse(path, events=("end",)):
        tag = elem.tag
        if tag == "bounds":
            bounds = (float(elem.get("minlat")), float(elem.get("minlon")),
                      float(elem.get("maxlat")), float(elem.get("maxlon")))
        elif tag == "node":
            nodes[elem.get("id")] = (float(elem.get("lat")), float(elem.get("lon")))
            elem.clear()
        elif tag == "way":
            refs = [nd.get("ref") for nd in elem.findall("nd")]
            tags = {t.get("k"): t.get("v") for t in elem.findall("tag")}
            if tags.get("highway") in ROAD_VALUES and len(refs) >= 2:
                ways.append({"id": elem.get("id"), "refs": refs, "tags": tags})
            elem.clear()
    return nodes, ways, bounds


def is_deck(way):
    return way["tags"].get("bridge", "no").lower() not in BRIDGE_FALSE


def way_points(way, nodes, origin):
    pts = []
    for ref in way["refs"]:
        ll = nodes.get(ref)
        if ll is not None:
            pts.append(geodetic_to_carla_local(origin, ll[0], ll[1]))
    return pts


def segments_cross(p1, p2, p3, p4):
    """Proper intersection test (shared endpoints excluded by the caller's node-disjoint check).
    Returns the intersection point, or None. The exact point matters: the clearance is read there."""
    def orient(a, b, c):
        return (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0])
    d1, d2 = orient(p3, p4, p1), orient(p3, p4, p2)
    d3, d4 = orient(p1, p2, p3), orient(p1, p2, p4)
    if not (((d1 > 0) != (d2 > 0)) and ((d3 > 0) != (d4 > 0))):
        return None
    denom = d1 - d2
    if denom == 0.0:
        return None
    t = d1 / denom
    return (p1[0] + (p2[0] - p1[0]) * t, p1[1] + (p2[1] - p1[1]) * t)


def bbox(points):
    xs = [p[0] for p in points]; ys = [p[1] for p in points]
    return min(xs), min(ys), max(xs), max(ys)


def bbox_overlaps(a, b, pad=0.0):
    return not (a[2] + pad < b[0] or b[2] + pad < a[0] or a[3] + pad < b[1] or b[3] + pad < a[1])


def densify(points, step):
    """Walk a polyline emitting points every `step` metres."""
    out = []
    for (x0, y0), (x1, y1) in zip(points, points[1:]):
        seg = math.hypot(x1 - x0, y1 - y0)
        n = max(1, int(seg / step))
        for i in range(n):
            t = i / n
            out.append((x0 + (x1 - x0) * t, y0 + (y1 - y0) * t))
    if points:
        out.append(points[-1])
    return out


# ── reporting ────────────────────────────────────────────────────────────────

def stats(values):
    vals = sorted(v for v in values if not math.isnan(v))
    if not vals:
        return None
    n = len(vals)
    return {"n": n, "min": vals[0], "max": vals[-1], "median": vals[n // 2],
            "p10": vals[int(n * 0.10)], "p90": vals[int(n * 0.90)]}


def describe(label, gaps, max_drape):
    st = stats(gaps)
    if st is None:
        print(f"  {label:<12} no valid samples")
        return None
    kept = sum(1 for g in gaps if not math.isnan(g) and abs(g) <= max_drape)
    total = st["n"]
    print(f"  {label:<12} n={total:<5} gap median {st['median']:6.2f} m   "
          f"p10 {st['p10']:6.2f}  p90 {st['p90']:6.2f}  "
          f"range [{st['min']:.2f}, {st['max']:.2f}]")
    print(f"  {'':<12} despike keeps the photoreal at {kept}/{total} "
          f"({100.0 * kept / total:.0f}%) of these points")
    return st


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--osm", required=True, help="the .osm the world was built from")
    ap.add_argument("--cache-dir", default=None,
                    help="drape cache folder (default: Build/drape-cache beside this script's repo)")
    ap.add_argument("--cache", default=None, help="explicit cache .bin, skips auto-selection")
    ap.add_argument("--max-drape", type=float, default=5.0,
                    help="the despike threshold to evaluate against (build default 5.0)")
    ap.add_argument("--step", type=float, default=4.0, help="sample spacing along a way (m)")
    ap.add_argument("--clear-radius", type=float, default=150.0,
                    help="how far an 'open' control point must be from any deck (m)")
    ap.add_argument("--under-radius", type=float, default=12.0,
                    help="how close to a crossing an under-road point must be to count as beneath "
                         "the structure (m); roughly half a deck width")
    ap.add_argument("--detail", type=int, default=0,
                    help="print this many individual deck/under samples")
    args = ap.parse_args()

    here = os.path.dirname(os.path.abspath(__file__))
    cache_dir = args.cache_dir or os.path.join(here, "..", "..", "Build", "drape-cache")

    print(f"reading {args.osm}")
    nodes, ways, bounds = load_osm(args.osm)
    if bounds is None:
        print("ERROR: the OSM has no <bounds> element; cannot match a cache.", file=sys.stderr)
        return 1
    decks = [w for w in ways if is_deck(w)]
    print(f"  {len(nodes)} nodes, {len(ways)} road ways, {len(decks)} tagged bridge=*")
    if not decks:
        print("  no bridge-tagged ways; nothing to measure.")
        return 1

    if args.cache:
        cache_path = args.cache
    else:
        picked = pick_cache(cache_dir, bounds)
        if picked is None:
            print(f"ERROR: no drape cache found under {cache_dir}", file=sys.stderr)
            return 1
        score, cache_path, cols, rows, cell = picked
        print(f"  matched cache {os.path.basename(cache_path)} "
              f"({cols}x{rows} @ {cell} m, corner/span error {score:.1f} m)")
        if score > 5.0:
            print("  WARNING: the best cache does not line up with this OSM; "
                  "pass --cache explicitly if this looks wrong.")

    cache = DrapeCache(cache_path)
    origin = cache.origin
    print(f"  grid origin {origin[0]:.7f}, {origin[1]:.7f} @ {origin[2]:.2f} m  "
          f"extent ({cache.min_x:.1f}, {cache.min_y:.1f}) .. ({cache.max_x:.1f}, {cache.max_y:.1f}) m")

    # Project every way once.
    for w in ways:
        w["pts"] = way_points(w, nodes, origin)
        w["bbox"] = bbox(w["pts"]) if w["pts"] else None
        w["nodeset"] = set(w["refs"])

    # Ways that cross a deck in plan while sharing no node with it: grade separation by construction.
    crossings = []          # (deck, under, crossing point)
    under_ids = set()
    for deck in decks:
        if not deck["bbox"]:
            continue
        for other in ways:
            if other is deck or not other["bbox"]:
                continue
            if not bbox_overlaps(deck["bbox"], other["bbox"]):
                continue
            if deck["nodeset"] & other["nodeset"]:
                continue          # shares a node -> at-grade junction, not a crossing
            found = None
            for a, b in zip(deck["pts"], deck["pts"][1:]):
                for c, d in zip(other["pts"], other["pts"][1:]):
                    hit = segments_cross(a, b, c, d)
                    if hit:
                        found = hit
                        break
                if found:
                    break
            if found:
                crossings.append((deck, other, found))
                under_ids.add(other["id"])

    print(f"  {len(crossings)} plan crossings with no shared node "
          f"({len(under_ids)} distinct ways passing under a deck)")

    deck_centres = [c[2] for c in crossings]

    def far_from_decks(pt):
        return all(math.hypot(pt[0] - d[0], pt[1] - d[1]) > args.clear_radius for d in deck_centres)

    # Build the three sample populations.
    groups = {"deck": [], "under": [], "open": []}
    for deck in decks:
        groups["deck"].extend(densify(deck["pts"], args.step))
    for _, under, centre in crossings:
        for p in densify(under["pts"], args.step):
            if math.hypot(p[0] - centre[0], p[1] - centre[1]) <= args.under_radius:
                groups["under"].append(p)
    deck_ids = {w["id"] for w in decks}
    for w in ways:
        if w["id"] in deck_ids or w["id"] in under_ids:
            continue
        if w["tags"].get("tunnel", "no").lower() not in BRIDGE_FALSE:
            continue
        for p in densify(w["pts"], args.step * 4):
            if far_from_decks(p):
                groups["open"].append(p)

    print()
    print(f"gap = photoreal DSM - bare-earth DTM, despike threshold {args.max_drape} m")
    print()
    summary = {}
    for label in ("open", "under", "deck"):
        pts = groups[label]
        gaps = []
        for x, y in pts:
            dsm, dtm = cache.both(x, y)
            gaps.append(dsm - dtm)
        summary[label] = describe(label, gaps, args.max_drape)

    # The measurement that decides the design: how high the deck rides over the road beneath it.
    print()
    print("per-crossing deck height above the bare earth under it")
    lifts = []
    for deck, under, centre in crossings[:200]:
        d_dsm, d_dtm = cache.both(centre[0], centre[1])
        if math.isnan(d_dsm) or math.isnan(d_dtm):
            continue
        lifts.append(d_dsm - d_dtm)
    st = stats(lifts)
    if st:
        print(f"  n={st['n']}  median {st['median']:.2f} m  "
              f"p10 {st['p10']:.2f}  p90 {st['p90']:.2f}  "
              f"range [{st['min']:.2f}, {st['max']:.2f}]")
    else:
        print("  no valid crossing samples")

    if args.detail:
        print()
        print(f"first {args.detail} crossings (CARLA-local metres)")
        print(f"  {'deck way':>12} {'under way':>12} {'x':>9} {'y':>9} {'DSM':>9} {'DTM':>9} {'gap':>7}  despike")
        for deck, under, centre in crossings[:args.detail]:
            dsm, dtm = cache.both(centre[0], centre[1])
            gap = dsm - dtm
            choice = "DSM" if (not math.isnan(gap) and abs(gap) <= args.max_drape) else "DTM"
            print(f"  {deck['id']:>12} {under['id']:>12} {centre[0]:9.1f} {centre[1]:9.1f} "
                  f"{dsm:9.2f} {dtm:9.2f} {gap:7.2f}  {choice}")

    # Read the separation off the numbers rather than asserting it.
    print()
    o, u = summary.get("open"), summary.get("under")
    if o and u:
        print(f"systematic photoreal offset on open road : {o['median']:.2f} m "
              f"(p10 {o['p10']:.2f}, p90 {o['p90']:.2f})")
        print(f"gap on roads passing under a deck        : {u['median']:.2f} m "
              f"(p10 {u['p10']:.2f}, p90 {u['p90']:.2f})")
        if u["p10"] > o["p90"]:
            print("  -> the two populations SEPARATE: a residual-gap threshold can distinguish them.")
        else:
            print("  -> the two populations OVERLAP: no gap threshold separates them; "
                  "per-road layer classification is required.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
