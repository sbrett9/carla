"""Automated walker navigation motion test.

Spawns N walkers at random navmesh-derived locations, attaches a
controller.ai.walker to each, starts the controllers, routes them to a
fresh random navmesh location, samples positions for SAMPLE_SECONDS,
reports per-walker net + path displacement, then cleans up.

Exits 0 if average net displacement > MIN_DISPLACEMENT_M.

Mirrors the structure of test_tm_motion.py.

Usage:
    python test_nav_walkers.py [N_WALKERS] [SAMPLE_SECONDS]
"""
import math
import random
import sys
import time

N_WALKERS         = int(sys.argv[1]) if len(sys.argv) > 1 else 5
SAMPLE_SECONDS    = float(sys.argv[2]) if len(sys.argv) > 2 else 12.0
SAMPLE_INTERVAL_S = 1.0
MIN_DISPLACEMENT_M = 3.0   # avg displacement threshold to call it "moving"

HOST = "127.0.0.1"
PORT = 2000

import carlanet as carla


def dist(a, b):
    return math.sqrt((a.x - b.x) ** 2 + (a.y - b.y) ** 2 + (a.z - b.z) ** 2)


def main() -> int:
    print(f"== Walker navigation motion test: N={N_WALKERS}, sample={SAMPLE_SECONDS}s ==")

    client = carla.Client(HOST, PORT)
    client.set_timeout(10.0)
    world = client.get_world()
    # Walker AI ticks inside the TM worker thread (Integrator design choice).
    # Construct the TM up-front so the worker is alive before Start()ing walkers.
    tm = client.get_trafficmanager(8000)
    print(f"   TM port={tm.get_port()}")
    bp_lib = world.get_blueprint_library()

    walker_bps = list(bp_lib.filter("walker.pedestrian.*"))
    if not walker_bps:
        print("FAIL: no walker.pedestrian.* blueprints", file=sys.stderr)
        return 2
    controller_bp = bp_lib.find("controller.ai.walker")
    if controller_bp is None:
        print("FAIL: controller.ai.walker blueprint not found", file=sys.stderr)
        return 2

    # ---- 1. collect N nav-derived spawn locations ----
    spawn_locs = []
    nav_attempts = 0
    while len(spawn_locs) < N_WALKERS and nav_attempts < N_WALKERS * 10:
        nav_attempts += 1
        loc = world.get_random_location_from_navigation()
        if loc is not None:
            # small Z offset so the pawn doesn't intersect the ground at spawn.
            spawn_locs.append(carla.Transform(
                carla.Location(float(loc.x), float(loc.y), float(loc.z) + 1.0)))

    if not spawn_locs:
        # Fall back to vehicle spawn points (offset up). This means the navmesh
        # is unavailable so the test will likely still fail later, but at least
        # we'll surface a useful diagnostic.
        print("   WARN: get_random_location_from_navigation returned None; "
              "falling back to vehicle spawn points", file=sys.stderr)
        sps = world.get_map().get_spawn_points()
        random.shuffle(sps)
        for sp in sps[:N_WALKERS]:
            sp.location.z += 1.0
            spawn_locs.append(sp)
    print(f"   collected {len(spawn_locs)} spawn locations (nav attempts={nav_attempts})")

    # ---- 2. spawn walkers + their controllers ----
    walkers = []
    controllers = []
    for tr in spawn_locs:
        bp = random.choice(walker_bps)
        if bp.has_attribute("is_invincible"):
            try: bp.set_attribute("is_invincible", "false")
            except Exception: pass
        try:
            walker = world.spawn_actor(bp, tr)
        except Exception as ex:
            print(f"   walker spawn failed: {ex}", file=sys.stderr)
            continue
        try:
            controller = world.spawn_actor(
                controller_bp, carla.Transform(), attach_to=walker)
        except Exception as ex:
            print(f"   controller spawn failed: {ex}", file=sys.stderr)
            try: walker.destroy()
            except Exception: pass
            continue
        walkers.append(walker)
        controllers.append(controller)
    print(f"   spawned {len(walkers)}/{N_WALKERS} walkers + controllers")
    if not walkers:
        return 2

    # Give the server a tick to register the new actors / parent links before
    # the controllers start dispatching crowd commands.
    time.sleep(0.5)

    try:
        # ---- 3. start each controller and route to a fresh nav location ----
        for controller in controllers:
            try:
                controller.start()
            except Exception as ex:
                print(f"   controller.start failed: {ex}", file=sys.stderr)
                continue
            try:
                target = world.get_random_location_from_navigation()
                if target is not None:
                    controller.go_to_location(target)
            except Exception as ex:
                print(f"   go_to_location failed: {ex}", file=sys.stderr)
            try:
                controller.set_max_speed(1.4)
            except Exception as ex:
                print(f"   set_max_speed failed: {ex}", file=sys.stderr)

        # Brief settle to let Detour pick up the first nav request.
        time.sleep(0.5)

        # ---- 4. sample positions ----
        samples = []
        start = time.monotonic()
        while time.monotonic() - start < SAMPLE_SECONDS:
            t = time.monotonic() - start
            snap = [(w.id, w.get_location()) for w in walkers]
            samples.append((t, snap))
            time.sleep(SAMPLE_INTERVAL_S)
    finally:
        # ---- 5. always clean up: stop controllers, then destroy both ----
        for controller in controllers:
            try: controller.stop()
            except Exception: pass
        for controller in controllers:
            try: controller.destroy()
            except Exception: pass
        for walker in walkers:
            try: walker.destroy()
            except Exception: pass

    # ---- 6. analyze ----
    print(f"\n   collected {len(samples)} samples over {samples[-1][0]:.1f}s")
    if len(samples) < 2:
        print("FAIL: insufficient samples", file=sys.stderr)
        return 2

    by_actor = {}
    for t, snap in samples:
        for aid, loc in snap:
            by_actor.setdefault(aid, []).append((t, loc))

    print("\n   per-walker displacement:")
    total = 0.0
    for aid, hist in by_actor.items():
        if len(hist) < 2:
            continue
        first, last = hist[0][1], hist[-1][1]
        d = dist(first, last)
        path = sum(dist(hist[i][1], hist[i+1][1]) for i in range(len(hist) - 1))
        total += d
        print(f"     walker {aid}: net={d:6.2f}m  path={path:6.2f}m"
              f"  start=({first.x:7.1f},{first.y:7.1f}) end=({last.x:7.1f},{last.y:7.1f})")

    avg_net = total / len(by_actor) if by_actor else 0.0
    print(f"\n   AVERAGE NET DISPLACEMENT: {avg_net:.2f}m (threshold {MIN_DISPLACEMENT_M}m)")

    if avg_net >= MIN_DISPLACEMENT_M:
        print("== PASS ==")
        return 0
    else:
        print("== FAIL: walkers did not move ==", file=sys.stderr)
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
