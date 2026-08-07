"""Two vehicles on conflicting paths through one junction: does either give way?

Nothing in the traffic manager encodes right of way for a turn across oncoming traffic. Two vehicles
whose paths cross inside a junction resolve purely on the geometry of their swept paths, and that
decision can reverse as they move, so a pair can advance into each other and stop. Waiting for that
to happen in ambient traffic is slow and gives no control over which movements meet; this stages the
conflict directly.

The pair is the ordinary permissive left: one vehicle turns left across the path of one coming the
other way, both on the same green. Both are routed before they are spawned, so each takes a known
connecting road through the junction rather than whichever way the greedy walk happens to send it.

Run against a server with the generated Arapahoe map loaded and no other traffic:

    python test_left_turn_yield.py                 # junction 117, Arapahoe x Clinton
    python test_left_turn_yield.py --seconds 45

Reports each vehicle's speed and distance to the junction over time, and whether either came to a
stand inside it. A run where both stop inside the junction and stay stopped is the failure this is
looking for; one where the left-turner waits outside and goes after the through-vehicle clears is
the behaviour that is wanted.
"""
import argparse
import math
import sys
import time

import carlanet as carla

# Junction 117 on the generated Arapahoe map, in CARLA world coordinates. Taken from the OpenDRIVE:
# the two approaches are opposite ends of Arapahoe, and their connecting roads cross.
JUNCTION_CENTRE = (310.3, -79.4)

# Where each vehicle enters the junction, and where its connecting road puts it down again.
LEFT_TURN_ENTRY = (328.0, -79.4)     # road 2188 -> connecting road 2885, +89.9 degrees
LEFT_TURN_EXIT = (307.1, -56.3)      # where the connecting road rejoins the network
LEFT_TURN_TARGET = (307.4, -25.0)    # well beyond it, so the destination is routable
THROUGH_ENTRY = (293.3, -72.3)       # road 2093 -> connecting road 2893, -4.8 degrees
THROUGH_EXIT = (327.7, -74.4)
THROUGH_TARGET = (370.0, -74.8)


def dist(a, b):
    return math.hypot(a[0] - b[0], a[1] - b[1])


def pick_spawn(spawn_points, want_point, want_yaw_deg, tolerance_deg=35.0):
    """The spawn point closest to `want_point` that faces roughly `want_yaw_deg`.

    The wanted point has to be derived from the road the vehicle approaches ON, not from where it
    ends up: for a turning movement the line from junction entry to exit points across the junction,
    so backing off along it lands beside the road rather than up it.
    """
    best, best_score = None, None
    for sp in spawn_points:
        offset = (sp.rotation.yaw - want_yaw_deg + 180.0) % 360.0 - 180.0
        if abs(offset) > tolerance_deg:
            continue
        score = dist((sp.location.x, sp.location.y), want_point)
        if best_score is None or score < best_score:
            best, best_score = sp, score
    return best, best_score


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=2000)
    ap.add_argument("--tm-port", type=int, default=8000)
    ap.add_argument("--seconds", type=float, default=40.0, help="how long to watch (default 40)")
    ap.add_argument("--back-off", type=float, default=55.0,
                    help="metres upstream of the junction to start each vehicle (default 55)")
    ap.add_argument("--stalled-for", type=float, default=5.0,
                    help="seconds stationary inside the junction before calling it stuck")
    args = ap.parse_args()

    client = carla.Client(args.host, args.port)
    client.set_timeout(30.0)
    world = client.get_world()
    spawn_points = world.get_map().get_spawn_points()
    print(f"map {world.get_map().name}, {len(spawn_points)} spawn points")

    existing = list(world.get_actors().filter("vehicle.*"))
    if existing:
        print(f"WARNING: {len(existing)} vehicles already present; results will not be clean",
              file=sys.stderr)

    # Road 2188 runs east-west and is approached heading west (yaw 180); road 2093 is its opposite
    # number, approached heading east (yaw 0). Back off along each road, not through the junction.
    turner_want = (LEFT_TURN_ENTRY[0] + args.back_off, LEFT_TURN_ENTRY[1])
    through_want = (THROUGH_ENTRY[0] - args.back_off, THROUGH_ENTRY[1])
    turner_sp, d1 = pick_spawn(spawn_points, turner_want, 180.0)
    through_sp, d2 = pick_spawn(spawn_points, through_want, 0.0)
    if turner_sp is None or through_sp is None:
        print("could not find approach spawn points for both movements", file=sys.stderr)
        return 2
    if max(d1, d2) > 20.0:
        print(f"approach spawn points are too far from where the vehicles need to start "
              f"({d1:.1f} m, {d2:.1f} m): the conflict would not be staged, so not running.",
              file=sys.stderr)
        return 2
    print(f"left-turner starts ({turner_sp.location.x:.1f}, {turner_sp.location.y:.1f}) "
          f"yaw {turner_sp.rotation.yaw:.0f}, {d1:.1f} m from the wanted point")
    print(f"through     starts ({through_sp.location.x:.1f}, {through_sp.location.y:.1f}) "
          f"yaw {through_sp.rotation.yaw:.0f}, {d2:.1f} m from the wanted point")

    bp = world.get_blueprint_library().filter("vehicle.*")
    blueprint = bp[0]
    tm = client.get_trafficmanager(args.tm_port)

    spawned = []
    try:
        turner = world.spawn_actor(blueprint, turner_sp)
        spawned.append(turner)
        through = world.spawn_actor(blueprint, through_sp)
        spawned.append(through)
        print(f"spawned left-turner id={turner.id}, through id={through.id}")

        # Route each one before it drives, so the movement through the junction is the intended one
        # rather than whatever the greedy walk picks.
        for vehicle, exit_point, label in ((turner, LEFT_TURN_TARGET, "left turn"),
                                           (through, THROUGH_TARGET, "through")):
            destination = carla.Location(x=exit_point[0], y=exit_point[1], z=0.0)
            try:
                route = tm.plan_route(vehicle.get_location(), destination)
                if route and len(route) > 0:
                    tm.apply_route(vehicle, route)
                    print(f"  {label}: routed, {len(route)} waypoints")
                else:
                    print(f"  {label}: NO ROUTE FOUND — falling back to free driving")
            except Exception as exc:  # noqa: BLE001 - report and carry on with the measurement
                print(f"  {label}: routing unavailable ({exc!r})")
            vehicle.set_autopilot(True, args.tm_port)

        print("\n   t     left-turner                     through")
        print("        speed  dist-to-jn  in-jn     speed  dist-to-jn  in-jn")
        stalled_since = {turner.id: None, through.id: None}
        stuck_reported = set()
        started = time.time()
        while time.time() - started < args.seconds:
            time.sleep(0.5)
            row = [f"{time.time() - started:5.1f}"]
            for vehicle in (turner, through):
                loc = vehicle.get_location()
                vel = vehicle.get_velocity()
                speed = math.hypot(vel.x, vel.y)
                d = dist((loc.x, loc.y), JUNCTION_CENTRE)
                inside = d < 22.0
                if inside and speed < 0.5:
                    if stalled_since[vehicle.id] is None:
                        stalled_since[vehicle.id] = time.time()
                    elif (time.time() - stalled_since[vehicle.id] >= args.stalled_for
                          and vehicle.id not in stuck_reported):
                        stuck_reported.add(vehicle.id)
                else:
                    stalled_since[vehicle.id] = None
                row.append(f"{speed:6.1f} {d:9.1f}  {'IN' if inside else '  ':>5}")
            print("  ".join(row))

        print()
        if len(stuck_reported) >= 2:
            print("RESULT: both vehicles stopped inside the junction and stayed stopped — "
                  "neither gave way, and neither could recover.")
        elif stuck_reported:
            print(f"RESULT: {len(stuck_reported)} vehicle(s) stopped inside the junction. "
                  "One movement blocked the other rather than yielding outside it.")
        else:
            print("RESULT: neither vehicle was left standing inside the junction.")
    finally:
        for actor in spawned:
            try:
                actor.destroy()
            except Exception:  # noqa: BLE001 - cleanup must not mask the result
                pass
        print(f"cleaned up {len(spawned)} vehicle(s)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
