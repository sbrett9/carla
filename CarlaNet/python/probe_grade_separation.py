"""Check, offline, that the layer routing puts each road on the right surface at a grade separation.

Runs the real classifier — CarlaNet.Map.OpenDrive.OsmRoadLayers + GradeSeparation — over a generated
map and the drape cache the build sampled, and reports the lift it assigns to three populations:

  deck   - centreline samples on a way the OSM places above grade
  under  - centreline samples on a way that crosses one of those in plan while sharing no node,
           taken near the crossing itself
  open   - road well away from any crossing; the control

The two numbers that decide whether the defect is fixed:

  * under-road lift must be 0. Anything else means a road passing beneath a structure has taken
    height from it, which is the artefact that makes roads climb into an overpass.
  * deck lift must match the clearance measurable in the photoreal at the same point. That is the
    structure's real height above the road it crosses, recovered without any constant.

Nothing here talks to the server, so it is safe to run against a live session:

    python CarlaNet\\python\\probe_grade_separation.py ^
        --osm Build\\sumo-smoketest\\Arapahoe_I25_clipped.osm ^
        --xodr Build\\sumo-smoketest\\Arapahoe_I25_elevated.xodr

Pass the CLIPPED .osm and the .xodr generated from it — those are the two files the build itself
correlated. The .xodr only supplies plan geometry, so an already-elevated one reads the same as the
flat one it came from.
"""
import argparse
import math
import os
import sys
import xml.etree.ElementTree as ET

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from probe_overpass import (BRIDGE_FALSE, DrapeCache, densify, load_osm, pick_cache,  # noqa: E402
                            segments_cross, stats, way_points)


def load_clr(publish_dir):
    if publish_dir:
        os.environ["CARLANET_PUBLISH_DIR"] = publish_dir
    import carlanet  # noqa: F401  (bootstraps the .NET runtime and loads the assemblies)
    from CarlaNet.Map.OpenDrive import (ElevationInjector, GradeSeparation,  # noqa: E402
                                        OpenDriveParser, OsmRoadLayers)
    from System import Array, Double  # noqa: E402
    return ElevationInjector, GradeSeparation, OpenDriveParser, OsmRoadLayers, Array, Double


def layer_of(way):
    """The layer the classifier will resolve for this way, mirroring OsmRoadLayers.ResolveLayer."""
    raw = way["tags"].get("layer")
    if raw is not None:
        try:
            return int(round(float(raw)))
        except ValueError:
            pass
    if way["tags"].get("bridge", "no").lower() not in BRIDGE_FALSE:
        return 1
    tunnel = way["tags"].get("tunnel", "no").lower()
    if tunnel not in BRIDGE_FALSE and tunnel != "building_passage":
        return -1
    return 0


def read_elevation_profiles(xodr_text):
    """road id -> the road's <elevation> records, so the z actually written can be evaluated."""
    profiles = {}
    root = ET.fromstring(xodr_text)
    for road in root.findall("road"):
        recs = []
        for e in road.findall("./elevationProfile/elevation"):
            recs.append(tuple(float(e.get(k, "0")) for k in ("s", "a", "b", "c", "d")))
        if recs:
            profiles[int(road.get("id"))] = sorted(recs)
    return profiles


def elevation_at(profiles, road_id, s):
    """Evaluate a road's injected elevation profile at station s (metres relative to the origin)."""
    recs = profiles.get(int(road_id))
    if not recs:
        return float("nan")
    rec = recs[0]
    for r in recs:
        if r[0] <= s + 1e-9:
            rec = r
        else:
            break
    ds = s - rec[0]
    return rec[1] + rec[2] * ds + rec[3] * ds * ds + rec[4] * ds * ds * ds


def describe(label, values, unit="m"):
    st = stats(values)
    if st is None:
        print(f"  {label:<10} no samples")
        return None
    print(f"  {label:<10} n={st['n']:<5} median {st['median']:6.2f} {unit}  "
          f"p10 {st['p10']:6.2f}  p90 {st['p90']:6.2f}  "
          f"range [{st['min']:.2f}, {st['max']:.2f}]")
    return st


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--osm", required=True, help="the .osm the .xodr was generated from")
    ap.add_argument("--xodr", required=True, help="the generated OpenDRIVE map")
    ap.add_argument("--cache-dir", default=None,
                    help="drape cache folder (default: Build/drape-cache in this repo)")
    ap.add_argument("--cache", default=None, help="explicit cache .bin, skips auto-selection")
    ap.add_argument("--no-cache", action="store_true",
                    help="skip the drape cache and report only what the .xodr actually contains. "
                         "Use for a map built without a cached terrain grid: the clearance and lift "
                         "columns are then unavailable, but the deck-over-road separation is not.")
    ap.add_argument("--step", type=float, default=10.0,
                    help="centreline sampling step; use the same --step the world was built with")
    ap.add_argument("--max-drape", type=float, default=5.0,
                    help="de-spike threshold, for the systematic-offset estimate (build default 5.0)")
    ap.add_argument("--near-crossing", type=float, default=12.0,
                    help="how close to a crossing a sample must be to count as under the structure (m)")
    ap.add_argument("--publish-dir", default=None,
                    help="CarlaNet DLL directory (default: the installed carlanet package)")
    args = ap.parse_args()

    here = os.path.dirname(os.path.abspath(__file__))
    cache_dir = args.cache_dir or os.path.join(here, "..", "..", "Build", "drape-cache")

    (ElevationInjector, GradeSeparation, OpenDriveParser,
     OsmRoadLayers, Array, Double) = load_clr(args.publish_dir)

    with open(args.xodr, encoding="utf-8") as fh:
        xodr = fh.read()
    road_map = OpenDriveParser.Load(xodr)
    if road_map is None:
        print(f"ERROR: {args.xodr} failed to parse", file=sys.stderr)
        return 1
    origin = road_map.GeoReference
    # The pure-Python helpers borrowed from probe_overpass take the origin as a plain tuple.
    origin_tuple = (origin.Latitude, origin.Longitude, 0.0)
    print(f"map     : {args.xodr}")
    print(f"          origin {origin.Latitude:.7f}, {origin.Longitude:.7f}")

    nodes, ways, bounds = load_osm(args.osm)
    if bounds is None:
        print("ERROR: the OSM has no <bounds>; cannot match a drape cache.", file=sys.stderr)
        return 1

    cache = None
    if not args.no_cache:
        if args.cache:
            cache_path = args.cache
        else:
            picked = pick_cache(cache_dir, bounds)
            if picked is None:
                print(f"ERROR: no drape cache found under {cache_dir}. Pass --no-cache to check "
                      f"only what the .xodr contains.", file=sys.stderr)
                return 1
            score, cache_path, cols, rows, cell = picked
            print(f"cache   : {os.path.basename(cache_path)} ({cols}x{rows} @ {cell} m, "
                  f"corner/span error {score:.1f} m)")
        cache = DrapeCache(cache_path)

    # The systematic photoreal-vs-bare-earth offset, computed exactly as the build does.
    systematic = 0.0
    if cache is not None:
        gaps = sorted(d - g for d, g in zip(cache.dsm, cache.dtm)
                      if not math.isnan(d - g) and abs(d - g) <= args.max_drape)
        systematic = gaps[len(gaps) // 2] if gaps else 0.0
        print(f"offset  : systematic photoreal-vs-bare-earth gap = {systematic:.2f} m "
              f"(over {len(gaps)} cells)")
    else:
        print("cache   : none — reporting only the elevations the .xodr already contains")

    samples = ElevationInjector.ExtractCenterlineSamples(road_map, args.step)
    n = samples.Count
    xs, ys = [0.0] * n, [0.0] * n
    for i in range(n):
        xs[i], ys[i] = samples[i].X, samples[i].Y

    # Re-run the real classifier over the same surfaces the build saw. Without a cached grid there
    # are no surfaces to give it, so only the generated map itself can be reported.
    result = None
    lift = [float("nan")] * n
    if cache is not None:
        layers = OsmRoadLayers.Read(args.osm, origin)
        surface = Array[Double](n)
        ground = Array[Double](n)
        for i in range(n):
            dsm, dtm = cache.both(xs[i], ys[i])
            surface[i] = dsm
            ground[i] = dtm
        result = GradeSeparation.Compute(road_map, samples, layers, surface, ground, systematic, None)
        lift = [result.Lift[i] for i in range(n)]
    profiles = read_elevation_profiles(xodr)

    if result is not None:
        print()
        print(f"classifier: {layers.Ways.Count} drivable OSM ways, "
              f"{layers.Crossings.Count} plan crossings sharing no node")
        print(f"            {result.SamplesMatched}/{n} samples matched to a way, "
              f"{result.SamplesLifted} lifted, max lift {result.MaxLiftMeters:.2f} m")
        print(f"            {result.StructuresFromSurface} structures measured from the photoreal, "
              f"{result.StructuresFromFallback} from the fixed separation")

    # Populations, built from the source OSM independently of the classifier so this is a check and
    # not a restatement.
    for w in ways:
        w["pts"] = way_points(w, nodes, origin_tuple)
        w["nodeset"] = set(w["refs"])
        w["layer"] = layer_of(w)
    decks = [w for w in ways if w["layer"] > 0]

    crossings = []
    for deck in decks:
        for other in ways:
            if other is deck or deck["nodeset"] & other["nodeset"]:
                continue
            if other["layer"] >= deck["layer"]:
                continue
            hit = None
            for a, b in zip(deck["pts"], deck["pts"][1:]):
                for c, d in zip(other["pts"], other["pts"][1:]):
                    hit = segments_cross(a, b, c, d)
                    if hit:
                        break
                if hit:
                    break
            if hit:
                crossings.append((deck, other, hit))

    # Assign each centreline sample near a crossing to the deck or to the road under it. Right at a
    # crossing the two centrelines pass within metres of each other, so proximity alone cannot tell
    # them apart — the discriminator is BEARING: the two roads cross, so they run at an angle. Built
    # from the source OSM alone, so this is an independent check on the classifier and not a
    # restatement of it.
    sample_bearing = [0.0] * n
    for i in range(n):
        j = i + 1 if (i + 1 < n and samples[i + 1].RoadId == samples[i].RoadId) else i - 1
        if j < 0 or j >= n or samples[j].RoadId != samples[i].RoadId:
            sample_bearing[i] = 0.0
            continue
        step = 1.0 if j > i else -1.0
        sample_bearing[i] = math.atan2((ys[j] - ys[i]) * step, (xs[j] - xs[i]) * step)

    def nearest_on_way(points, px, py):
        """(distance, bearing) of the nearest point on a polyline."""
        best_d, best_b = float("inf"), 0.0
        for (x0, y0), (x1, y1) in zip(points, points[1:]):
            dx, dy = x1 - x0, y1 - y0
            seg = dx * dx + dy * dy
            if seg <= 1e-12:
                continue
            t = max(0.0, min(1.0, ((px - x0) * dx + (py - y0) * dy) / seg))
            d = math.hypot(px - (x0 + dx * t), py - (y0 + dy * t))
            if d < best_d:
                best_d, best_b = d, math.atan2(dy, dx)
        return best_d, best_b

    def bearing_delta(a, b):
        d = abs(math.atan2(math.sin(a - b), math.cos(a - b)))
        return min(d, math.pi - d)

    groups = {"deck": [], "under": [], "open": []}
    crossing_report = []
    for deck, under, centre in crossings:
        picked = {"deck": None, "under": None}
        for i in range(n):
            if math.hypot(xs[i] - centre[0], ys[i] - centre[1]) > args.near_crossing:
                continue
            scored = []
            for label, way in (("deck", deck), ("under", under)):
                dist, bear = nearest_on_way(way["pts"], xs[i], ys[i])
                delta = bearing_delta(sample_bearing[i], bear)
                if dist <= 12.0 and delta <= math.radians(45.0):
                    scored.append((delta, dist, label))
            if not scored:
                continue
            scored.sort()
            delta, dist, label = scored[0]
            if picked[label] is None or dist < picked[label][1]:
                picked[label] = (i, dist)
        if cache is not None:
            dsm, dtm = cache.both(*centre)
            clearance = dsm - dtm - systematic
        else:
            clearance = float("nan")

        def injected(pick):
            if pick is None:
                return float("nan")
            s = samples[pick[0]]
            return elevation_at(profiles, s.RoadId, s.S)

        row = {"deck_way": deck["id"], "under_way": under["id"], "clearance": clearance,
               "deck_lift": lift[picked["deck"][0]] if picked["deck"] else float("nan"),
               "under_lift": lift[picked["under"][0]] if picked["under"] else float("nan"),
               "deck_z": injected(picked["deck"]), "under_z": injected(picked["under"])}
        row["separation"] = row["deck_z"] - row["under_z"]
        crossing_report.append(row)
        if picked["deck"]:
            groups["deck"].append(row["deck_lift"])
        if picked["under"]:
            groups["under"].append(row["under_lift"])

    for i in range(n):
        near = any(math.hypot(xs[i] - c[2][0], ys[i] - c[2][1]) < 150.0 for c in crossings)
        if not near:
            groups["open"].append(lift[i])

    if result is not None:
        print()
        print("lift assigned, by population (metres above the at-grade surface)")
        describe("open", groups["open"])
        describe("under", groups["under"])
        describe("deck", groups["deck"])

    print()
    print("per crossing: the clearance measurable in the photoreal, the lift each road was given,")
    print("and the elevation actually written into the .xodr at that point (metres, origin-relative)")
    print(f"  {'deck way':>12} {'under way':>12} {'clearance':>10} {'deck lift':>10} "
          f"{'under lift':>11} {'deck z':>9} {'under z':>9} {'gap':>7}")
    for r in crossing_report:
        print(f"  {r['deck_way']:>12} {r['under_way']:>12} {r['clearance']:10.2f} "
              f"{r['deck_lift']:10.2f} {r['under_lift']:11.2f} "
              f"{r['deck_z']:9.2f} {r['under_z']:9.2f} {r['separation']:7.2f}")

    # Where the photoreal shows a structure, the .xodr must put the deck above the road it crosses.
    # Without a cache there is no clearance to compare against, so every resolved crossing counts.
    real = [r for r in crossing_report if not math.isnan(r["separation"])
            and (math.isnan(r["clearance"]) or r["clearance"] > 1.5)]
    print()
    if real:
        describe("separation", [r["separation"] for r in real])
        below = [r for r in real if r["separation"] <= 0.5]
        if below:
            print(f"  WARNING: {len(below)} deck(s) are not above the road they cross in the .xodr.")
        else:
            print(f"  PASS: all {len(real)} deck(s) sit above the road they cross in the .xodr.")

    if result is None:
        print("  (no drape cache: the lift the classifier assigned could not be checked)")
        return 0

    bad_under = [r for r in crossing_report
                 if not math.isnan(r["under_lift"]) and abs(r["under_lift"]) > 0.01]
    if bad_under:
        print(f"  FAIL: {len(bad_under)} road(s) passing under a structure were lifted off grade.")
    else:
        print("  PASS: no road passing under a structure took any height from it.")

    measured = [r for r in crossing_report
                if not math.isnan(r["deck_lift"]) and r["clearance"] > 1.5]
    recovered = [r for r in measured if abs(r["deck_lift"] - r["clearance"]) < 0.75]
    if measured:
        print(f"        {len(recovered)}/{len(measured)} crossings with a reconstructed structure got "
              f"their clearance from the photoreal (within 0.75 m).")
    flat = [r for r in crossing_report if r["clearance"] <= 1.5]
    if flat:
        print(f"        {len(flat)} crossing(s) sit where the photoreal shows no elevated structure, "
              f"so no lift is applied there.")
    if result.StructuresFromFallback:
        print(f"        {result.StructuresFromFallback} way(s) the photogrammetry did not reconstruct "
              f"were lifted by the fixed separation instead.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
