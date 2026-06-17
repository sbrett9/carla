"""Phase 2b step-1 proof: a Chaos heightfield built at runtime actually collides.

Builds a FLAT draped-terrain heightfield raised to TERRAIN_Z metres (above any existing map
floor), drops a vehicle a few metres above it, and checks the vehicle comes to rest near
TERRAIN_Z instead of falling through to the map floor / the void. No sampling, no draping yet —
this isolates the engine-side heightfield collision body (UDrapedTerrain / build_draped_terrain).

Prereq: a running server (RunCarlaServer.ps1) ticking in async mode, any map loaded.

Usage:
    python test_drape_ramp.py [--z M] [--host H] [--port P]
"""
import argparse
import sys
import time

import carlanet as carla

ap = argparse.ArgumentParser()
ap.add_argument("--z", type=float, default=50.0, help="flat terrain height (m), above the map floor")
ap.add_argument("--cell", type=float, default=2.0, help="heightfield cell size (m)")
ap.add_argument("--half", type=int, default=25, help="grid half-extent in cells (grid = 2*half+1)")
ap.add_argument("--settle", type=float, default=4.0, help="seconds to let physics settle")
ap.add_argument("--host", default="127.0.0.1")
ap.add_argument("--port", type=int, default=2000)
args = ap.parse_args()


def main() -> int:
    n = 2 * args.half + 1                      # grid is n x n
    origin = -args.half * args.cell            # so the grid is centred on (0,0)
    print(f"== drape heightfield collision proof: {n}x{n} flat @ {args.z} m, cell {args.cell} m ==")

    client = carla.Client(args.host, args.port)
    client.set_timeout(15.0)
    print(f"   server: {client.get_server_version()}")
    world = client.get_world()

    # Flat heightfield at z = args.z (row-major, all equal).
    heights = [args.z] * (n * n)
    ok = world.build_draped_terrain(origin, origin, args.cell, n, n, heights)
    print(f"   build_draped_terrain -> {ok}")
    if not ok:
        print("FAIL: build returned False", file=sys.stderr); return 1
    time.sleep(0.5)   # let the body register in the physics scene

    # Drop a vehicle a few metres above the centre of the flat terrain.
    bp_lib = world.get_blueprint_library()
    vbps = [b for b in bp_lib.filter("vehicle.*")
            if int(str(b.get_attribute("number_of_wheels"))) == 4]
    if not vbps:
        print("FAIL: no 4-wheel vehicle blueprints", file=sys.stderr); return 2
    drop_z = args.z + 3.0
    tf = carla.Transform(carla.Location(0.0, 0.0, drop_z), carla.Rotation(0.0, 0.0, 0.0))
    veh = world.spawn_actor(vbps[0], tf)
    print(f"   spawned {veh.type_id} at z={drop_z:.2f} m")

    # Watch it settle.
    zs = []
    t0 = time.time()
    while time.time() - t0 < args.settle:
        z = float(veh.get_location().z)
        zs.append(z)
        print(f"     t={time.time()-t0:4.1f}s  z={z:8.3f} m")
        time.sleep(0.5)
    final = zs[-1]

    try:
        veh.destroy()
    except Exception:
        pass

    # Resting on the flat heightfield => final z within a vehicle-height of args.z and clearly
    # above the drop-through threshold. A fall-through would plummet far below args.z.
    if final >= args.z - 1.0 and final <= args.z + 3.5:
        print(f"\n== PASS == vehicle rests at z={final:.3f} m on the heightfield (terrain {args.z} m)")
        return 0
    print(f"\n== FAIL == vehicle z={final:.3f} m (expected ~{args.z} m; it fell through)", file=sys.stderr)
    return 1


if __name__ == "__main__":
    try:
        rc = main()
    except Exception:
        import traceback
        print("\n== FAILURE ==", file=sys.stderr)
        traceback.print_exc()
        rc = 3
    sys.exit(rc)
