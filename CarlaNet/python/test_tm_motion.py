"""Automated TrafficManager motion test.

Spawns N vehicles, registers them with the TM via autopilot, samples
positions for SAMPLE_SECONDS, reports per-vehicle total displacement,
then cleans up. Exits 0 if average displacement > MIN_DISPLACEMENT_M.

Captures the [TM tick ...] diagnostic stream emitted by
TrafficManagerLocal.RunOneTick alongside the verdict.

Usage:
    python test_tm_motion.py [N_VEHICLES] [SAMPLE_SECONDS]
"""
import math
import random
import sys
import time

N_VEHICLES        = int(sys.argv[1]) if len(sys.argv) > 1 else 5
SAMPLE_SECONDS    = float(sys.argv[2]) if len(sys.argv) > 2 else 12.0
SAMPLE_INTERVAL_S = 1.0
MIN_DISPLACEMENT_M = 3.0   # avg displacement threshold to call it "moving"

HOST = "127.0.0.1"
PORT = 2000
TM_PORT = 8000

import carlanet as carla


def dist(a, b):
    return math.sqrt((a.x - b.x) ** 2 + (a.y - b.y) ** 2 + (a.z - b.z) ** 2)


def main() -> int:
    print(f"== TM motion test: N={N_VEHICLES}, sample={SAMPLE_SECONDS}s ==")

    client = carla.Client(HOST, PORT)
    client.set_timeout(10.0)
    world = client.get_world()
    bp_lib = world.get_blueprint_library()
    spawn_points = world.get_map().get_spawn_points()
    random.shuffle(spawn_points)

    # Filter to plain four-wheel vehicles (skip bikes / motorcycles).
    vehicle_bps = [bp for bp in bp_lib.filter("vehicle.*")
                   if int(str(bp.get_attribute("number_of_wheels"))) == 4]
    if not vehicle_bps:
        print("FAIL: no 4-wheel vehicle blueprints", file=sys.stderr)
        return 2

    tm = client.get_trafficmanager(TM_PORT)
    print(f"   TM type={type(tm).__name__}, port={tm.get_port()}")
    # Generous following distance so vehicles don't bunch up and wedge.
    tm.set_global_distance_to_leading_vehicle(2.5)
    tm.set_global_percentage_speed_difference(0.0)

    spawned = []
    used = 0
    for sp in spawn_points:
        if len(spawned) >= N_VEHICLES:
            break
        bp = random.choice(vehicle_bps)
        bp.set_attribute("role_name", "autopilot")
        try:
            actor = world.spawn_actor(bp, sp)
            actor.set_autopilot(True, tm.get_port())
            spawned.append(actor)
            used += 1
        except Exception:
            used += 1
            continue
    print(f"   spawned {len(spawned)}/{N_VEHICLES} vehicles (tried {used} spawn points)")
    if not spawned:
        return 2

    # Allow the TM worker a moment to discover the vehicles in its first
    # ALSM tick before we sample the initial position.
    time.sleep(0.5)

    samples = []
    start = time.monotonic()
    while time.monotonic() - start < SAMPLE_SECONDS:
        t = time.monotonic() - start
        snap = [(a.id, a.get_location()) for a in spawned]
        samples.append((t, snap))
        time.sleep(SAMPLE_INTERVAL_S)

    # Cleanup BEFORE judgement so the world is left clean even on FAIL.
    try:
        for a in spawned:
            try: a.destroy()
            except Exception: pass
    finally:
        pass

    # Analyze.
    print(f"\n   collected {len(samples)} samples over {samples[-1][0]:.1f}s")
    if len(samples) < 2:
        print("FAIL: insufficient samples", file=sys.stderr)
        return 2

    by_actor = {}
    for t, snap in samples:
        for aid, loc in snap:
            by_actor.setdefault(aid, []).append((t, loc))

    print("\n   per-vehicle displacement:")
    total = 0.0
    for aid, hist in by_actor.items():
        if len(hist) < 2:
            continue
        first, last = hist[0][1], hist[-1][1]
        d = dist(first, last)
        # Total path-length too (not just net displacement).
        path = sum(dist(hist[i][1], hist[i+1][1]) for i in range(len(hist) - 1))
        total += d
        print(f"     vehicle {aid}: net={d:6.2f}m  path={path:6.2f}m"
              f"  start=({first.x:7.1f},{first.y:7.1f}) end=({last.x:7.1f},{last.y:7.1f})")

    avg_net = total / len(by_actor) if by_actor else 0.0
    print(f"\n   AVERAGE NET DISPLACEMENT: {avg_net:.2f}m (threshold {MIN_DISPLACEMENT_M}m)")

    if avg_net >= MIN_DISPLACEMENT_M:
        print("== PASS ==")
        return 0
    else:
        print("== FAIL: vehicles did not move ==", file=sys.stderr)
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
