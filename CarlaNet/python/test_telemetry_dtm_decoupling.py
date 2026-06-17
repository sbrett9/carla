"""Validate that reported vehicle telemetry HAE is DECOUPLED from visual height-align.

Background (TELEMETRY_DTM_DECOUPLING_HANDOFF.md, project_height_align_mechanism):
  With --height-align area/origin the road mesh AND the collidable bare-earth ground are both
  shifted by one constant offset (Option A: the ground layer is dropped by the same amount so it
  coincides with the road), so every vehicle sits at DTM + offset. Telemetry must report each
  vehicle's BARE-EARTH ellipsoidal-WGS84 altitude (Cesium World Terrain DTM) — the locked HAE truth
  datum — NOT the photoreal-aligned road Z. get_vehicle_telemetry recovers it by subtracting that
  one constant (LastHeightAlignOffset) unconditionally. No live Cesium sampling in the loop.

This test must do the world build itself: LastHeightAlignOffset and the DTM table live on the
SAME C# CarlaClient instance that built the world, so building in a separate process (e.g. a
prior test_digital_twin.py run) would leave this client's offset at 0.

Flow: build elevated world (--height-align, default 'area') -> spawn TM traffic -> poll
world.get_vehicle_telemetry(origin) and verify, per vehicle. Checks are RACE-FREE — they only
compare values within a SINGLE telemetry record (the producer's own snapshot); we deliberately
do NOT recompute `physical` from a second get_transform() read, which would race vehicle motion
(a moving car climbs/descends between the two reads -> spurious cm-dm "errors"). The exact
subtraction hae == physical - offset is deterministic; the end-to-end property we validate is:
  1. pivot := hae - hae_dtm (both from one record) is a plausible vehicle pivot height
     (in [pivot_min, pivot_max]). This IS the decoupling: hae - hae_dtm = (physical - offset)
     - dtm = pivot, independent of the visual offset.
  2. hae_dtm ≈ live one-shot 'ground' DTM sample at the vehicle lat/lon (cached table matches
     live Cesium World Terrain), within --dtm-tol.
Vehicles with |pivot| > --glitch-pivot are CARLA physics artifacts (airborne / fell through the
world) and are excluded (reported, not failed). With --height-align none, offset is 0 and the
same invariants hold (hae == physical, still pivot above bare-earth ground).

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
ap.add_argument("--height-align", choices=["area", "origin", "none", "drape"], default="area",
                help="visual road-Z alignment under test (default 'area'). The decoupled hae must "
                     "be bare-earth regardless; 'none' -> offset 0; 'drape' -> per-cell offset field.")
ap.add_argument("--terrain-res", type=float, default=8.0, help="drape: heightfield cell size (m)")
ap.add_argument("--terrain-margin", type=float, default=30.48, help="drape: sandbox margin past OSM (m)")
ap.add_argument("--drape-cache-dir", default=os.path.join(_REPO, "Build", "drape-cache"),
                help="drape: grid sampling cache dir (speeds re-runs)")
ap.add_argument("--offroad", type=int, default=3, help="drape: also spawn N off-road vehicles to check")
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
ap.add_argument("--glitch-pivot", type=float, default=5.0,
                help="exclude vehicles with |hae-hae_dtm| beyond this (m) as airborne/fell sim artifacts")
args = ap.parse_args()

os.environ.setdefault("CARLA_NETCONVERT", _NETCONVERT)
os.environ.setdefault("PROJ_LIB", _PROJ)
os.environ.setdefault("PROJ_DATA", _PROJ)

import carlanet as carla
from CarlaNet.Map import OsmConversionOptions


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
        cesium_settle_seconds=args.settle,
        terrain_res=args.terrain_res,
        terrain_margin=args.terrain_margin,
        drape_cache_dir=args.drape_cache_dir)
    print(f"    built in {time.time() - t0:.1f}s")

    client.set_timeout(30.0)
    world = client.get_world()
    origin = world.get_cesium_origin()
    offset = float(client._inner.LastHeightAlignOffset)
    drape = bool(client._inner.LastDrapeActive)
    if drape:
        print(f"    georef origin h = {origin[2]:.2f} m | DRAPE per-cell grid "
              f"{client._inner.LastDrapeNumCols}x{client._inner.LastDrapeNumRows} @ "
              f"{client._inner.LastDrapeCellSize:.1f} m")
    else:
        table_n = int(client._inner.LastGroundDtmSamples.Count)
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
    if args.height_align == "drape" and not drape:
        print("FAIL: height-align drape but LastDrapeActive is False", file=sys.stderr); return 1

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

    # ── drape: also seat a few OFF-ROAD vehicles (the seamless on/off-road win) ──
    if drape and args.offroad > 0:
        nc = int(client._inner.LastDrapeNumCols); nr = int(client._inner.LastDrapeNumRows)
        minx = float(client._inner.LastDrapeMinX); miny = float(client._inner.LastDrapeMinY)
        cell = float(client._inner.LastDrapeCellSize)
        cx = minx + 0.5 * (nc - 1) * cell; cy = miny + 0.5 * (nr - 1) * cell
        placed = 0
        for _ in range(args.offroad * 6):
            if placed >= args.offroad:
                break
            x = random.uniform(cx - 0.3 * nc * cell, cx + 0.3 * nc * cell)
            y = random.uniform(cy - 0.3 * nr * cell, cy + 0.3 * nr * cell)
            sz = world.ground_z_below(x, y, 1000.0, search=5000.0)   # hits the draped heightfield
            if sz is None:
                continue
            bp = random.choice(vehicle_bps); bp.set_attribute("role_name", "offroad")
            try:
                a = world.spawn_actor(bp, carla.Transform(carla.Location(x, y, sz + 0.5),
                                                          carla.Rotation(0, 0, 0)))
                spawned.append(a); placed += 1
            except Exception:
                continue
        print(f"    + {placed}/{args.offroad} off-road vehicle(s)")

    if not spawned:
        print("FAIL: no vehicles spawned", file=sys.stderr); return 1
    time.sleep(1.0)   # let the TM discover them

    # ── validate over a few telemetry polls ────────────────────────────────
    # Checks are RACE-FREE: everything compared comes from the SAME telemetry record
    # (the producer's own snapshot). We do NOT recompute `physical` from a second
    # get_transform() read — that races vehicle motion (a moving car climbs/descends
    # between the two reads), which produces spurious cm-to-dm "errors". The exact
    # subtraction hae == physical - offset is deterministic code; what we validate
    # end-to-end is the bare-earth property, via:
    #   (1) pivot := hae - hae_dtm   (both from one record) must be a plausible vehicle
    #       pivot height — this IS hae's decoupling: hae - hae_dtm = (physical - offset)
    #       - dtm = pivot, independent of the visual offset.
    #   (2) hae_dtm ≈ live one-shot 'ground' DTM at the vehicle lat/lon (cached table
    #       matches live Cesium World Terrain).
    # Vehicles whose |pivot| exceeds --glitch-pivot are CARLA physics artifacts
    # (airborne / fell through the world) and are excluded (reported, not failed).
    # In drape mode the collision heightfield is TRIANGULATED per cell while telemetry reads a
    # BILINEAR grid, so on steep non-planar cells (mostly off-road) the two differ by up to the
    # within-cell relief, which scales with the cell size. Widen the lower pivot bound accordingly
    # (sub-0.5 m at the 2 m production default; larger at coarse test resolutions). On-road cells are
    # near-planar so this barely applies. hae_dtm-vs-live (the truth check) stays strict.
    pivot_min_eff = args.pivot_min
    if drape:
        pivot_min_eff = min(args.pivot_min, -(0.5 + 0.45 * args.terrain_res))
    print(f"[3] validating telemetry over {args.samples} poll(s)...  pivot range "
          f"[{pivot_min_eff:.2f}, {args.pivot_max:.2f}] m")
    fails, checked, glitched = [], 0, 0
    pivots, dtm_errs = [], []
    for _ in range(args.samples):
        recs = world.get_vehicle_telemetry(origin)
        # Batch the live bare-earth sample for all vehicles in ONE call this poll.
        pts = [(r["lat"], r["lon"]) for r in recs]
        try:
            gs = world.sample_terrain_heights(pts, selector="ground") if pts else []
        except Exception:
            gs = []
        live = {i: float(gs[i][2]) for i in range(len(gs)) if math.isfinite(gs[i][2])}
        for i, r in enumerate(recs):
            hae_dtm = r.get("hae_dtm")
            pivot = (r["hae"] - hae_dtm) if hae_dtm is not None else None
            if pivot is not None and abs(pivot) > args.glitch_pivot:
                glitched += 1               # airborne / fell — sim artifact, skip
                continue
            checked += 1
            # (1) race-free decoupling invariant: pivot in a plausible vehicle range.
            if pivot is not None:
                pivots.append(pivot)
                if not (pivot_min_eff <= pivot <= args.pivot_max):
                    fails.append(f"veh {r['id']}: hae {r['hae']:.2f} - hae_dtm {hae_dtm:.2f} "
                                 f"= pivot {pivot:.2f} m outside [{pivot_min_eff:.2f}, {args.pivot_max}]")
            # (2) cached DTM table matches live bare-earth ground sampling.
            dtm_live = live.get(i)
            if dtm_live is not None and hae_dtm is not None:
                de = abs(hae_dtm - dtm_live)
                dtm_errs.append(de)
                if de > args.dtm_tol:
                    fails.append(f"veh {r['id']}: hae_dtm {hae_dtm:.2f} vs live DTM "
                                 f"{dtm_live:.2f} (err {de:.2f} > {args.dtm_tol} m)")
        time.sleep(1.0)

    # cleanup before verdict
    for a in spawned:
        try: a.destroy()
        except Exception: pass

    def stat(xs):
        return (f"min {min(xs):+.2f} median {sorted(xs)[len(xs)//2]:+.2f} max {max(xs):+.2f}"
                if xs else "n/a")
    print(f"\n   checks: {checked}  (excluded {glitched} sim-glitched/airborne)")
    print(f"   pivot = hae - hae_dtm (race-free): {stat(pivots)} m")
    print(f"   hae_dtm vs live_DTM error:         {stat(dtm_errs)} m")
    if args.height_align != "none":
        print(f"   offset removed from each hae: {offset:+.3f} m (the photoreal bias, now "
              f"absent from telemetry)")

    if checked == 0:
        print("\nFAIL: no usable (non-glitched) vehicle records sampled", file=sys.stderr)
        return 1
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
