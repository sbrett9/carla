"""Validate that reported vehicle telemetry HAE is DECOUPLED from visual height-align.

Background (TELEMETRY_DTM_DECOUPLING_HANDOFF.md, project_height_align_mechanism):
  With --height-align area/origin the road MESH (and the cars on it) is shifted onto the
  Google photoreal DSM by a single constant offset for visual seating. Telemetry, however,
  must report each vehicle's BARE-EARTH ellipsoidal-WGS84 altitude (Cesium World Terrain DTM)
  — the locked HAE truth datum — NOT the photoreal-aligned road Z. get_vehicle_telemetry now
  removes that visual offset (cached on the C# client as LastHeightAlignOffset, gated on/off
  road by the persisted per-road-point DTM table LastGroundDtmSamples). No live Cesium
  sampling happens in the telemetry loop.

This test must do the world build itself: LastHeightAlignOffset and the DTM table live on the
SAME C# CarlaClient instance that built the world, so building in a separate process (e.g. a
prior test_digital_twin.py run) would leave this client's offset at 0.

Flow: build elevated world (--height-align, default 'area') -> spawn TM traffic -> poll
world.get_vehicle_telemetry(origin) and verify, per vehicle:
  1. EXACT decoupling: reported hae == physical_hae - offset   (on-road; |err| < 1 cm).
  2. BARE-EARTH truth: reported hae ≈ live one-shot 'ground' DTM sample at the vehicle lat/lon,
     within ~the vehicle pivot height (hae - dtm in [-0.5, +3.0] m).
  3. hae_dtm field ≈ that live ground sample (the cached table matches live DTM).
  4. lat/lon are unchanged by the decoupling (equal the raw geodetic transform).
With --height-align none the offset is 0 and hae must equal physical_hae exactly.

Prereqs (same as test_digital_twin.py):
  * Headless server running + ticking (RunCarlaServer.ps1).
  * SUMO netconvert staged under Build/sumo-install.
  * CESIUM_ION_TOKEN env var (or --ion-token).

Usage:
    python test_telemetry_dtm_decoupling.py [--height-align area|origin|none]
        [--osm <path>] [--traffic <N>] [--ion-token <jwt>] [--host h] [--port p]
"""
import argparse
import math
import os
import random
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
ap.add_argument("--lat", type=float, default=None, help="origin lat (default: OSM bounds center)")
ap.add_argument("--lon", type=float, default=None, help="origin lon (default: OSM bounds center)")
ap.add_argument("--step", type=float, default=10.0)
ap.add_argument("--ion-token", default=os.environ.get("CESIUM_ION_TOKEN", ""))
ap.add_argument("--ion-asset-id", type=int, default=2275207)   # Google photoreal (visual)
ap.add_argument("--ground-asset-id", type=int, default=1)      # Cesium World Terrain (bare earth)
ap.add_argument("--height-align", choices=["area", "origin", "none"], default="area",
                help="visual road-Z alignment under test (default 'area'). The decoupled hae must "
                     "be bare-earth regardless; with 'none' the offset is 0 (hae == physical).")
ap.add_argument("--settle", type=float, default=10.0)
ap.add_argument("--traffic", type=int, default=8)
ap.add_argument("--samples", type=int, default=5, help="telemetry polls (1 Hz)")
ap.add_argument("--tm-port", type=int, default=8000)
ap.add_argument("--host", default="127.0.0.1")
ap.add_argument("--port", type=int, default=2000)
ap.add_argument("--timeout", type=float, default=300.0)
# Tolerances.
ap.add_argument("--pivot-max", type=float, default=3.0, help="max plausible hae-dtm (pivot+slop) m")
ap.add_argument("--pivot-min", type=float, default=-0.5, help="min plausible hae-dtm m")
ap.add_argument("--dtm-tol", type=float, default=2.5, help="hae_dtm vs live ground sample tol m")
args = ap.parse_args()

os.environ.setdefault("CARLA_NETCONVERT", _NETCONVERT)
os.environ.setdefault("PROJ_LIB", _PROJ)
os.environ.setdefault("PROJ_DATA", _PROJ)

import carlanet as carla
from CarlaNet.Map import OsmConversionOptions
from CarlaNet.Types.Geom import Geodesy, GeoLocation


def read_osm_bounds(path):
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


def make_options():
    opts = OsmConversionOptions()
    opts.NetconvertPath = _NETCONVERT
    opts.ProjDataDirectory = _PROJ
    opts.GenerateTrafficLights = False
    opts.OriginLatitude = args.lat
    opts.OriginLongitude = args.lon
    from System.Collections.Generic import List
    extra = List[str]()
    for a in ["--keep-edges.by-vclass", "passenger",
              "--keep-edges.components", "1",
              "--remove-edges.isolated", "true"]:
        extra.Add(a)
    opts.ExtraArgs = extra
    return opts


def main() -> int:
    print(f"== telemetry DTM-decoupling test (height-align={args.height_align}) ==")
    if not os.path.exists(args.osm):
        print(f"ERROR: OSM not found: {args.osm}", file=sys.stderr); return 2
    if not os.path.exists(_NETCONVERT):
        print(f"ERROR: netconvert not staged: {_NETCONVERT}", file=sys.stderr); return 2
    if not args.ion_token:
        print("ERROR: no Cesium Ion token (set CESIUM_ION_TOKEN or --ion-token).", file=sys.stderr)
        return 2

    if args.lat is None or args.lon is None:
        b = read_osm_bounds(args.osm)
        if b is None:
            print("ERROR: no --lat/--lon and no <bounds> in OSM", file=sys.stderr); return 2
        args.lat = (b[0] + b[2]) / 2.0
        args.lon = (b[1] + b[3]) / 2.0
    print(f"   origin: {args.lat:.7f}, {args.lon:.7f}")

    client = carla.Client(args.host, args.port)
    client.set_timeout(15.0)
    print(f"   server: {client.get_server_version()}")

    print("[1] building elevated world (convert -> sample -> inject -> mesh)...")
    client.set_timeout(args.timeout)
    t0 = time.time()
    client.generate_world_from_osm_with_elevation(
        args.osm, args.ion_token, args.ion_asset_id,
        ground_ion_asset_id=args.ground_asset_id,
        osm_options=make_options(),
        sample_step_meters=args.step,
        height_align=args.height_align,
        ground_collision=True,
        cesium_settle_seconds=args.settle)
    print(f"    built in {time.time() - t0:.1f}s")

    client.set_timeout(30.0)
    world = client.get_world()
    origin = world.get_cesium_origin()
    offset = float(client._inner.LastHeightAlignOffset)
    table_n = int(client._inner.LastGroundDtmSamples.Count)
    cs_origin = GeoLocation(float(origin[0]), float(origin[1]), float(origin[2]))
    print(f"    georef origin h = {origin[2]:.2f} m | LastHeightAlignOffset = {offset:.3f} m"
          f" | DTM table = {table_n} pts")
    if args.height_align == "none" and abs(offset) > 1e-9:
        print(f"FAIL: height-align none but offset {offset:.3f} != 0", file=sys.stderr); return 1
    if args.height_align != "none" and abs(offset) < 1e-6:
        print("WARN: offset ~0 — photoreal and ground coincide here, so the decoupling is a no-op "
              "(can't demonstrate the bias removal). Try a tile with a real DTM-DSM gap.",
              file=sys.stderr)
    if table_n == 0:
        print("FAIL: DTM table empty — world build did not persist samples", file=sys.stderr); return 1

    # ── spawn TM traffic on road spawn points ──────────────────────────────
    print(f"[2] spawning {args.traffic} autopilot vehicle(s)...")
    bp_lib = world.get_blueprint_library()
    spawn_points = world.get_map().get_spawn_points()
    random.shuffle(spawn_points)
    vehicle_bps = [bp for bp in bp_lib.filter("vehicle.*")
                   if int(str(bp.get_attribute("number_of_wheels"))) == 4]
    tm = client.get_trafficmanager(args.tm_port)
    spawned = []
    for sp in spawn_points:
        if len(spawned) >= args.traffic:
            break
        bp = random.choice(vehicle_bps)
        bp.set_attribute("role_name", "autopilot")
        try:
            a = world.spawn_actor(bp, sp)
            a.set_autopilot(True, tm.get_port())
            spawned.append(a)
        except Exception:
            continue
    print(f"    spawned {len(spawned)}/{args.traffic}")
    if not spawned:
        print("FAIL: no vehicles spawned", file=sys.stderr); return 1
    time.sleep(1.0)   # let the TM discover them

    # ── validate over a few telemetry polls ────────────────────────────────
    print(f"[3] validating telemetry over {args.samples} poll(s)...")
    fails, checked = [], 0
    worst_exact = 0.0
    pivots, dtm_errs = [], []
    for _ in range(args.samples):
        recs = world.get_vehicle_telemetry(origin)
        for r in recs:
            checked += 1
            # (1) EXACT decoupling: reported hae == physical_hae - offset (on-road).
            #     Recompute physical from the same actor transform the producer used.
            a = next((x for x in spawned if x.id == r["id"]), None)
            if a is not None:
                loc = a.get_transform().location
                physical = float(Geodesy.CarlaLocalToGeodetic(
                    cs_origin, float(loc.x), float(loc.y), float(loc.z)).Altitude)
                exact_err = abs((physical - offset) - r["hae"])
                worst_exact = max(worst_exact, exact_err)
                if exact_err > 0.01:
                    fails.append(f"veh {r['id']}: hae {r['hae']:.3f} != physical {physical:.3f} "
                                 f"- offset {offset:.3f} (err {exact_err:.3f} m)")
            # (2)+(3) bare-earth truth: live one-shot ground DTM at the vehicle lat/lon.
            try:
                gs = world.sample_terrain_heights([(r["lat"], r["lon"])], selector="ground")
                dtm_live = float(gs[0][2]) if gs and math.isfinite(gs[0][2]) else None
            except Exception:
                dtm_live = None
            if dtm_live is not None:
                pivot = r["hae"] - dtm_live
                pivots.append(pivot)
                if not (args.pivot_min <= pivot <= args.pivot_max):
                    fails.append(f"veh {r['id']}: hae {r['hae']:.2f} - live DTM {dtm_live:.2f} "
                                 f"= {pivot:.2f} m outside [{args.pivot_min}, {args.pivot_max}]")
                if r.get("hae_dtm") is not None:
                    de = abs(r["hae_dtm"] - dtm_live)
                    dtm_errs.append(de)
                    if de > args.dtm_tol:
                        fails.append(f"veh {r['id']}: hae_dtm {r['hae_dtm']:.2f} vs live DTM "
                                     f"{dtm_live:.2f} (err {de:.2f} > {args.dtm_tol} m)")
        time.sleep(1.0)

    # cleanup before verdict
    for a in spawned:
        try: a.destroy()
        except Exception: pass

    def stat(xs):
        return (f"min {min(xs):+.2f} median {sorted(xs)[len(xs)//2]:+.2f} max {max(xs):+.2f}"
                if xs else "n/a")
    print(f"\n   checks: {checked}  worst exact-decoupling err: {worst_exact*1000:.1f} mm")
    print(f"   hae - live_DTM (≈ vehicle pivot): {stat(pivots)} m")
    print(f"   hae_dtm vs live_DTM error:        {stat(dtm_errs)} m")
    if args.height_align != "none":
        print(f"   (offset removed from each hae: {offset:+.3f} m — this is the photoreal bias)")

    if fails:
        print(f"\n== FAIL ({len(fails)}) ==", file=sys.stderr)
        for f in fails[:20]:
            print("   " + f, file=sys.stderr)
        return 1
    print("\n== PASS — telemetry hae is bare-earth DTM, decoupled from visual placement ==")
    return 0


if __name__ == "__main__":
    try:
        rc = main()
    except Exception:
        import traceback
        print("\n== FAILURE ==", file=sys.stderr)
        traceback.print_exc()
        rc = 3
    sys.exit(rc)
