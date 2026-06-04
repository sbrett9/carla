"""OSM -> OpenDRIVE -> runtime-world test (via the carlanet Python API).

Drops an .osm file on a running CARLA server and fabricates the level at
runtime: converts OSM->.xodr with the bundled SUMO netconvert, copies the
OpenDRIVE to the server, and loads the special "OpenDriveMap" episode. The
world origin (0,0) is pinned to a chosen lat/lon (default: Wrigley Field home
plate) so the result stays georeferenced.

Prereqs:
  * Build the SUMO netconvert tool once (CarlaSetup.bat SUMO section) so it is
    staged under Build/sumo-install/. This script auto-discovers it there.
  * Start the CARLA server first (Play-In-Editor, or a packaged server) unless
    using --convert-only.

Usage:
    python test_osm_world.py [options]
      --convert-only   OSM->.xodr only; no server needed (preflight).
      --no-origin      do not pin an origin (legacy auto-centred behaviour).
      --osm  <path>    OSM file (default Import/Maps/WrigleyVille.osm).
      --lat  <deg>     origin latitude  (default 41.94813  = home plate).
      --lon  <deg>     origin longitude (default -87.65593).
      --host <h>       CARLA host (default 127.0.0.1).
      --port <p>       CARLA port (default 2000).
      --timeout <sec>  RPC timeout for world generation (default 180).
"""
import argparse
import os
import sys
import time

# Repo layout: <repo>/CarlaNet/python/test_osm_world.py
_THIS = os.path.dirname(os.path.abspath(__file__))
_REPO = os.path.normpath(os.path.join(_THIS, "..", ".."))
_INSTALL = os.path.join(_REPO, "Build", "sumo-install")
_NETCONVERT = os.path.join(_INSTALL, "bin",
                           "netconvert.exe" if os.name == "nt" else "netconvert")
_PROJ = os.path.join(_INSTALL, "share", "proj")

ap = argparse.ArgumentParser()
ap.add_argument("--convert-only", action="store_true")
ap.add_argument("--no-origin", action="store_true")
ap.add_argument("--osm", default=os.path.join(_REPO, "Import", "Maps", "WrigleyVille.osm"))
ap.add_argument("--lat", type=float, default=41.94813)
ap.add_argument("--lon", type=float, default=-87.65593)
ap.add_argument("--host", default="127.0.0.1")
ap.add_argument("--port", type=int, default=2000)
ap.add_argument("--timeout", type=float, default=180.0)
args = ap.parse_args()

# Make the bundled netconvert discoverable by CarlaNet.Map.OsmConverter even if
# the caller didn't export these (we also pass them explicitly via options below).
os.environ.setdefault("CARLA_NETCONVERT", _NETCONVERT)
os.environ.setdefault("PROJ_LIB", _PROJ)
os.environ.setdefault("PROJ_DATA", _PROJ)

import carlanet as carla
from CarlaNet.Map import OsmConverter, OsmConversionOptions


def make_options():
    opts = OsmConversionOptions()
    opts.NetconvertPath = _NETCONVERT
    opts.ProjDataDirectory = _PROJ
    if not args.no_origin:
        opts.OriginLatitude = args.lat
        opts.OriginLongitude = args.lon
    return opts


def main() -> int:
    print("== OSM -> World test ==")
    print(f"  osm        : {args.osm}")
    print(f"  netconvert : {_NETCONVERT}  (exists: {os.path.exists(_NETCONVERT)})")
    print(f"  proj.db    : {os.path.join(_PROJ, 'proj.db')}  (exists: {os.path.exists(os.path.join(_PROJ, 'proj.db'))})")
    print(f"  origin     : {'(none)' if args.no_origin else f'{args.lat}, {args.lon} -> (0,0)'}")
    print()

    if not os.path.exists(args.osm):
        print(f"ERROR: OSM not found: {args.osm}", file=sys.stderr); return 1
    if not os.path.exists(_NETCONVERT):
        print(f"ERROR: netconvert not staged: {_NETCONVERT}\n  Run CarlaSetup.bat (SUMO section).", file=sys.stderr); return 1

    opts = make_options()

    if args.convert_only:
        print("[1] Converting OSM -> OpenDRIVE (no server)...")
        t0 = time.time()
        xodr = OsmConverter(opts).ConvertFileAsync(args.osm).GetAwaiter().GetResult()
        dt = time.time() - t0
        roads = xodr.count("<road ")
        juncs = xodr.count("<junction ")
        pinned = "lat_0=" in xodr
        print(f"    {len(xodr):,} chars, {roads} roads, {juncs} junctions in {dt:.1f}s")
        print(f"    geoReference carries pinned origin (lat_0=): {pinned}")
        out = os.path.join(_REPO, "Build", "sumo-smoketest", "osmtest.xodr")
        os.makedirs(os.path.dirname(out), exist_ok=True)
        with open(out, "w", encoding="utf-8") as f:
            f.write(xodr)
        print(f"    wrote {out}")
        print("OK (convert-only).")
        return 0

    print(f"[1] Connecting to CARLA at {args.host}:{args.port} ...")
    client = carla.Client(args.host, args.port)
    client.set_timeout(15.0)
    print(f"    server version: {client.get_server_version()}")

    print("[2] generate_world_from_osm (convert + copy + load OpenDriveMap)...")
    print("    (blocks while the server meshes the road network — be patient)")
    client.set_timeout(args.timeout)
    t0 = time.time()
    client.generate_world_from_osm(args.osm, osm_options=opts)
    print(f"    done in {time.time() - t0:.1f}s")

    client.set_timeout(15.0)
    world = client.get_world()
    print(f"[3] map after: {world.get_map().name if hasattr(world.get_map(), 'name') else '(loaded)'}")
    print("OK — world generated. Check the editor viewport for the road network.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
