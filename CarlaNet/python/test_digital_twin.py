"""Full headless digital-twin build (via the carlanet Python API) — NO editor.

End-to-end Phase A->E: drops an .osm on a running headless CARLA server and produces
an ELEVATED, Cesium-aligned OpenDRIVE world:

  OSM -> flat .xodr (offline netconvert)
       -> extract road reference-line samples + reproject to WGS84 (offline)
       -> spawn a Cesium globe at the origin (runtime, no editor)
       -> sample terrain heights
       -> inject <elevationProfile> into the .xodr
       -> generate_opendrive_world(elevated) + re-establish Cesium overlay
  (-> optional: spawn traffic and let the TrafficManager drive it)

Prereqs:
  * Headless server running (RunCarlaServer.ps1) and ticking (async mode).
  * SUMO netconvert staged under Build/sumo-install (CarlaSetup.bat SUMO section).
  * CESIUM_ION_TOKEN env var (or --ion-token) for the spawned tileset.

Usage:
    python test_digital_twin.py [--osm <path>] [--lat <d>] [--lon <d>]
        [--step <m>] [--ion-asset-id <n>] [--ion-token <jwt>] [--traffic <N>]
        [--save <out.xodr>] [--host <h>] [--port <p>] [--timeout <sec>]
"""
import argparse
import os
import re
import sys
import time

_THIS = os.path.dirname(os.path.abspath(__file__))
_REPO = os.path.normpath(os.path.join(_THIS, "..", ".."))
_INSTALL = os.path.join(_REPO, "Build", "sumo-install")
_NETCONVERT = os.path.join(_INSTALL, "bin",
                           "netconvert.exe" if os.name == "nt" else "netconvert")
_PROJ = os.path.join(_INSTALL, "share", "proj")

ap = argparse.ArgumentParser()
ap.add_argument("--osm", default=os.path.join(_REPO, "Import", "Lakeview_Carson.osm"))
# Origin: if not given, it is DERIVED from the OSM <bounds> center (fully map-driven).
ap.add_argument("--lat", type=float, default=None, help="origin lat (default: OSM bounds center)")
ap.add_argument("--lon", type=float, default=None, help="origin lon (default: OSM bounds center)")
ap.add_argument("--step", type=float, default=10.0, help="reference-line sample spacing (m)")
ap.add_argument("--origin-height", type=float, default=None,
                help="vertical datum (m); default = sample the origin")
ap.add_argument("--ion-token", default=os.environ.get("CESIUM_ION_TOKEN", ""))
ap.add_argument("--ion-asset-id", type=int, default=2275207)  # Google Photorealistic 3D Tiles (visual)
ap.add_argument("--ground-asset-id", type=int, default=1,     # Cesium World Terrain (bare-earth, sampled)
                help="Cesium ion asset for the hidden bare-earth (no buildings/trees) terrain layer "
                     "whose heights set the road elevations (default 1 = Cesium World Terrain; "
                     "0 = take heights from the photoreal surface instead, legacy)")
ap.add_argument("--height-align", choices=["area", "origin", "none", "drape"], default="none",
                help="how the roads and drivable ground are matched to the Google photoreal imagery: "
                     "'none' (default) = leave them on the bare-earth terrain, which sits ~sub-meter "
                     "above the photoreal (invisible from high altitude); 'area'/'origin' = raise/lower "
                     "everything by ONE constant height so cars sit on the photoreal (good on flat "
                     "ground, drifts on hills); 'drape' = match the photoreal point-by-point across the "
                     "whole map area so cars sit on it everywhere, on roads AND off-road (best for "
                     "low/oblique views). Reported telemetry altitude stays true bare-earth in every mode.")
ap.add_argument("--terrain-res", type=float, default=2.0,
                help="'drape' only: spacing in metres between drivable-surface points - smaller hugs the "
                     "photoreal more closely but is slower to build (default 2.0)")
ap.add_argument("--terrain-margin", type=float, default=30.48,
                help="width (m) of the staging ring reserved just INSIDE the map edge, "
                     "where boundary-aware traffic enters/exits (the scene/region-of-interest is the "
                     "map inset by this much; select a slightly larger OSM area to compensate). "
                     "Default ~100 ft.")
ap.add_argument("--drape-cache-dir", default=None,
                help="'drape' only: folder to cache this area's terrain-height samples so rebuilds skip "
                     "the slow re-sampling")
ap.add_argument("--no-ground-collision", dest="ground_collision", action="store_false", default=True,
                help="disable collision on the bare-earth ground (default ON = vehicles always have "
                     "ground to drive on, on and off road). Safe to leave ON with any --height-align: "
                     "the ground is matched to where the roads sit, so cars neither float nor fall through.")
ap.add_argument("--settle", type=float, default=10.0)
ap.add_argument("--traffic", type=int, default=0, help="spawn N autopilot vehicles after build")
ap.add_argument("--no-road-filter", action="store_true",
                help="don't restrict netconvert to car-drivable roads (keeps sidewalks/rail/parking)")
ap.add_argument("--no-clip-bounds", action="store_true",
                help="don't clip the road network to the OSM <bounds>. By default the roads are "
                     "clipped to the selected area so they don't sprawl far beyond it (an OSM export "
                     "includes whole ways that merely touch the box, trailing off for kilometres), "
                     "keeping the roads aligned with the perimeter, drape terrain and staging ring.")
ap.add_argument("--save", default=None, help="output elevated .xodr (default: Build/sumo-smoketest/<osm>_elevated.xodr)")
ap.add_argument("--host", default="127.0.0.1")
ap.add_argument("--port", type=int, default=2000)
ap.add_argument("--timeout", type=float, default=300.0)
args = ap.parse_args()


def read_osm_bounds(path):
    """Return (minlat, minlon, maxlat, maxlon) from the OSM <bounds> element, or None."""
    try:
        with open(path, encoding="utf-8") as f:
            for line in f:
                if "<bounds" in line:
                    def g(k):
                        m = re.search(k + r'="([-0-9.]+)"', line)
                        return float(m.group(1)) if m else None
                    vals = (g("minlat"), g("minlon"), g("maxlat"), g("maxlon"))
                    return vals if None not in vals else None
    except OSError:
        return None
    return None

os.environ.setdefault("CARLA_NETCONVERT", _NETCONVERT)
os.environ.setdefault("PROJ_LIB", _PROJ)
os.environ.setdefault("PROJ_DATA", _PROJ)

import carlanet as carla
from CarlaNet.Map import OsmConversionOptions


def make_options():
    opts = OsmConversionOptions()
    opts.NetconvertPath = _NETCONVERT
    opts.ProjDataDirectory = _PROJ
    opts.GenerateTrafficLights = False  # avoids the ungrouped-TL log spam (known issue #1)
    opts.OriginLatitude = args.lat
    opts.OriginLongitude = args.lon
    if not args.no_road_filter:
        # Restrict netconvert to car-drivable streets: drop sidewalks/footways/cycleways,
        # all rail/subway/tram, and service/parking-aisle ways; then prune disconnected bits.
        from System.Collections.Generic import List
        extra = List[str]()
        for a in ["--keep-edges.by-vclass", "passenger",
                  "--keep-edges.components", "1",
                  "--remove-edges.isolated", "true"]:
            extra.Add(a)
        opts.ExtraArgs = extra
    return opts


def main() -> int:
    print("== Digital-twin build (headless, no editor) ==")
    print(f"  osm        : {args.osm}")

    if not os.path.exists(args.osm):
        print(f"ERROR: OSM not found: {args.osm}", file=sys.stderr); return 1
    if not os.path.exists(_NETCONVERT):
        print(f"ERROR: netconvert not staged: {_NETCONVERT}", file=sys.stderr); return 1

    # Origin: derive from the OSM <bounds> center unless explicitly given.
    if args.lat is None or args.lon is None:
        b = read_osm_bounds(args.osm)
        if b is None:
            print("ERROR: no --lat/--lon given and could not read <bounds> from the OSM file",
                  file=sys.stderr); return 1
        args.lat = (b[0] + b[2]) / 2.0
        args.lon = (b[1] + b[3]) / 2.0
        print(f"  origin     : {args.lat:.7f}, {args.lon:.7f}  (derived from OSM bounds center)")
    else:
        print(f"  origin     : {args.lat:.7f}, {args.lon:.7f}  (explicit)")
    print(f"  step       : {args.step} m   road-filter: {'OFF' if args.no_road_filter else 'ON (drivable only)'}"
          f"   height-align: {args.height_align}")
    print(f"  ion asset  : {args.ion_asset_id} (photoreal)  ground: {args.ground_asset_id} "
          f"({'World Terrain bare-earth' if args.ground_asset_id > 0 else 'photoreal (legacy)'})  "
          f"token: {'set' if args.ion_token else 'MISSING'}")
    print()

    if not args.ion_token:
        print("WARNING: no Ion token; the tileset can't be spawned and sampling will fail.",
              file=sys.stderr)

    # Clip the road network to the selected area (the OSM <bounds>) BEFORE conversion. An OSM export
    # keeps whole ways that merely touch the box, trailing far outside it, and netconvert can't cut
    # mid-edge; osm_clip cuts each way exactly at the boundary so the generated roads stay inside the
    # red perimeter / drape terrain / staging ring. The clipped file keeps the same <bounds>, so the
    # origin and drape sizing are unchanged.
    osm_for_build = args.osm
    if not args.no_clip_bounds:
        bb = read_osm_bounds(args.osm)
        if bb is None:
            print("  clip       : skipped (no <bounds> in the OSM)")
        else:
            import osm_clip
            clipped = os.path.join(_REPO, "Build", "sumo-smoketest",
                                   os.path.splitext(os.path.basename(args.osm))[0] + "_clipped.osm")
            os.makedirs(os.path.dirname(clipped), exist_ok=True)
            nways, nbnd = osm_clip.clip_osm_to_bounds(args.osm, clipped, bb)
            osm_for_build = clipped
            print(f"  clip       : roads cut to <bounds> -> {nways} ways (+{nbnd} edge nodes) -> {clipped}")
    else:
        print("  clip       : OFF (--no-clip-bounds) — roads may extend beyond the selected area")

    save_path = args.save or os.path.join(
        _REPO, "Build", "sumo-smoketest",
        os.path.splitext(os.path.basename(args.osm))[0] + "_elevated.xodr")

    client = carla.Client(args.host, args.port)
    client.set_timeout(15.0)
    print(f"[1] server version: {client.get_server_version()}")

    print("[2] generate_world_from_osm_with_elevation (convert -> sample -> inject -> build)...")
    print("    (blocks while sampling heights and meshing the elevated road network)")
    client.set_timeout(args.timeout)
    t0 = time.time()
    elevated = client.generate_world_from_osm_with_elevation(
        osm_for_build, args.ion_token, args.ion_asset_id,
        ground_ion_asset_id=args.ground_asset_id,
        osm_options=make_options(),
        sample_step_meters=args.step,
        origin_height=args.origin_height,
        height_align=args.height_align,
        ground_collision=args.ground_collision,
        cesium_settle_seconds=args.settle,
        terrain_res=args.terrain_res,
        terrain_margin=args.terrain_margin,
        drape_cache_dir=args.drape_cache_dir)
    dt = time.time() - t0

    roads = elevated.count("<road ")
    elevs = elevated.count("<elevation ")
    print(f"    done in {dt:.1f}s — {len(elevated):,} chars, {roads} roads, {elevs} elevation records")

    os.makedirs(os.path.dirname(save_path), exist_ok=True)
    with open(save_path, "w", encoding="utf-8") as f:
        f.write(elevated)
    print(f"    wrote elevated .xodr -> {save_path}")

    client.set_timeout(30.0)
    world = client.get_world()
    print("[3] elevated world generated; Cesium overlay re-established.")

    if args.traffic > 0:
        print(f"[4] spawning {args.traffic} autopilot vehicle(s)...")
        spawned = []
        for i in range(args.traffic):
            try:
                v = client.spawn_vehicle(spawn_index=i)
                v.set_autopilot(True)
                spawned.append(v)
            except Exception as e:
                print(f"    spawn {i} failed: {e}", file=sys.stderr)
        print(f"    {len(spawned)} vehicle(s) driving. Watch the photoreal streets.")

    print("\nOK — digital twin built headlessly.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
