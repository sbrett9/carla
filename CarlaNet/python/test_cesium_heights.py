"""Cesium terrain-height RPC test (via the carlanet Python API).

Exercises the digital-twin elevation hand-off end to end against a running CARLA
server whose loaded world contains a Cesium 3D tileset:

  world.configure_cesium_georeference(lat, lon, height, ...)   # point the globe
  world.sample_terrain_heights([(lat, lon), ...])              # sample ground

These map to the new server RPCs (request_terrain_heights / poll_terrain_heights /
configure_cesium_georeference) backed by CesiumCarlaBridge's UCesiumHeightSampler.

Prereqs:
  * A CARLA server running (Play-In-Editor or packaged) on a map that has a
    CesiumGeoreference + Cesium3DTileset (Phase D). The server must be ticking
    (async mode — the default).
  * If the tileset's Ion token / asset id are already set in the editor, you can
    leave --ion-token / --ion-asset-id at their defaults (the configure call then
    only repoints the georeference origin).

Usage:
    python test_cesium_heights.py [options]
      --lat  <deg>        origin latitude  (default 41.94813 = Wrigley home plate)
      --lon  <deg>        origin longitude (default -87.65593)
      --height <m>        origin ellipsoidal height for the georeference (default 146.508)
      --ion-token <str>   Cesium Ion access token to set on the tileset(s) (default: leave as-is)
      --ion-asset-id <n>  Cesium Ion asset id to set on the tileset(s) (default 0 = leave as-is)
      --no-configure      skip configure_cesium_georeference (assume editor already set it up)
      --settle <sec>      wait this long after configure before sampling (default 5)
      --host <h> / --port <p> / --timeout <sec>
"""
import argparse
import math
import os
import sys
import time

import carlanet as carla

ap = argparse.ArgumentParser()
ap.add_argument("--lat", type=float, default=41.94813)
ap.add_argument("--lon", type=float, default=-87.65593)
ap.add_argument("--height", type=float, default=146.508)
# Cesium tileset is SPAWNED at runtime by configure (no editor needed), so a valid
# Ion token + asset id are required. Token defaults to the CESIUM_ION_TOKEN env var.
ap.add_argument("--ion-token", default=os.environ.get("CESIUM_ION_TOKEN", ""))
ap.add_argument("--ion-asset-id", type=int, default=2275207)  # Google Photorealistic 3D Tiles
ap.add_argument("--no-configure", action="store_true")
ap.add_argument("--settle", type=float, default=5.0)
ap.add_argument("--host", default="127.0.0.1")
ap.add_argument("--port", type=int, default=2000)
ap.add_argument("--timeout", type=float, default=120.0)
args = ap.parse_args()


def main() -> int:
    # Three points: origin (home plate), ~111 m north, ~83 m east — same set the
    # headless de-risk probe used, so heights should match (~146.5 m ellipsoidal).
    points = [
        (args.lat,           args.lon,           "origin (home plate)"),
        (args.lat + 0.001,   args.lon,           "~111 m north"),
        (args.lat,           args.lon + 0.00121, "~90 m east"),
    ]

    print("== Cesium terrain-height RPC test ==")
    print(f"  server : {args.host}:{args.port}")
    print(f"  origin : {args.lat}, {args.lon}  height={args.height} m")
    print()

    client = carla.Client(args.host, args.port)
    client.set_timeout(15.0)
    print(f"[1] server version: {client.get_server_version()}")
    world = client.get_world()

    if not args.no_configure:
        if not args.ion_token:
            print("    WARNING: no Ion token (--ion-token or CESIUM_ION_TOKEN). The tileset"
                  " can only be spawned with a valid token; sampling will fail otherwise.",
                  file=sys.stderr)
        print("[2] configure_cesium_georeference (spawns Cesium if absent) ...")
        ok = world.configure_cesium_georeference(
            args.lat, args.lon, args.height,
            ion_token=args.ion_token, ion_asset_id=args.ion_asset_id, refresh=True)
        print(f"    configured: {ok}")
        if args.settle > 0:
            print(f"    letting tiles stream for {args.settle:.0f}s ...")
            time.sleep(args.settle)
    else:
        print("[2] skipping configure (--no-configure)")

    print("[3] sample_terrain_heights ...")
    client.set_timeout(args.timeout)
    t0 = time.time()
    results = world.sample_terrain_heights([(p[0], p[1]) for p in points],
                                           timeout=args.timeout)
    dt = time.time() - t0
    print(f"    {len(results)} result(s) in {dt:.2f}s\n")

    ok_count = 0
    for (lat, lon, height), (_, _, label) in zip(results, points):
        good = not math.isnan(height)
        ok_count += 1 if good else 0
        h = f"{height:9.3f} m" if good else "   FAILED"
        print(f"    {label:22s} lat={lat:.7f} lon={lon:.7f}  h={h}")

    print()
    if ok_count == len(points):
        print(f"OK — all {ok_count} point(s) sampled.")
        return 0
    print(f"PARTIAL — {ok_count}/{len(points)} point(s) sampled "
          f"(NaN = tileset had no ground there / not streamed yet).")
    return 0 if ok_count > 0 else 2


if __name__ == "__main__":
    sys.exit(main())
