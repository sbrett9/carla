"""Perpetual margin-to-margin traffic with spawn/despawn opacity fade (via the carlanet API).

A focused demonstrator for the boundary-aware staging fade. Unlike generate_traffic_carlanet
(which lets the Traffic Manager wander vehicles and fades them only by proximity to the map edge),
this script gives every vehicle an explicit trip and a clean fade life-cycle:

  * Vehicles SPAWN inside the margin (between the red map edge and the blue interior boundary), where
    they are FULLY TRANSPARENT. Opacity is the fraction of the vehicle's footprint that has crossed
    the blue line into the interior: a vehicle wholly in the margin is invisible (0); as it drives in
    and straddles blue its opacity equals the fraction of its volume past the line (hood across ~0.33,
    hood+cabin ~0.66); once wholly inside it is solid (1.0). The transition spans the vehicle's own
    length at the blue line, and reverses on the way out. Nothing ever reaches the red edge — a vehicle
    is despawned once it is back fully within the margin (invisible), well before the map edge.
  * By default each vehicle drives on plain Traffic-Manager autopilot, which keeps it strictly on the
    road network (it wanders lane-to-lane). Pass --route to instead hand it a custom-path destination
    on a far edge (margin-to-margin trips); routing can occasionally push a vehicle off-road or off a
    clipped dead-end, so it is opt-in.
  * Once a vehicle has been fully inside the interior and then returns fully behind the veil (back to
    invisible within a margin), it despawns. It is also despawned if it leaves the sandbox or falls
    off the world (e.g. driving off a clipped dead-end). There is NO time limit.
  * The script perpetually tops up toward a user-given maximum: whenever one leaves, another enters
    somewhere else, so a steady population crosses the scene.

The script reconciles its tracked vehicles against the server every cycle, so a vehicle the server has
destroyed is dropped immediately (no stale set_fade calls), and it stops cleanly on Ctrl+C.

Prereqs:
  * Headless server running (RunCarlaServer.ps1).
  * A draped digital-twin world already built (test_digital_twin.py --height-align drape), so the
    server reports staging bounds (the map edge ring). Without it this script has no margin to use.
  * A rebuilt server + carlanet wheel that include set_actor_fade (otherwise the fade is a no-op;
    this script checks for it up front and tells you to rebuild).

Run order (separate terminals):
    1. RunCarlaServer.ps1
    2. python test_digital_twin.py --height-align drape
    3. python test_generate_fade_traffic.py --max 30
    4. python eo_observer.py            # fly to a map edge and watch vehicles fade in / out

Usage:
    python test_generate_fade_traffic.py [--max N] [--spawn-interval S] [--filter BP]
        [--generation G] [--host H] [--port P] [--tm-port P] [--seed N]
"""
import argparse
import math
import random
import signal
import sys
import time

import carlanet as carla

ap = argparse.ArgumentParser()
ap.add_argument("--max", type=int, default=30, help="max vehicles alive at once (default 30)")
ap.add_argument("--spawn-interval", type=float, default=0.7,
                help="seconds between spawn attempts while below --max (default 0.7)")
ap.add_argument("--filter", default="vehicle.*", help="vehicle blueprint filter (default vehicle.*)")
ap.add_argument("--generation", default="all",
                help="vehicle blueprint generation to use: 1, 2, 3, or all (default all)")
ap.add_argument("--host", default="127.0.0.1", help="CARLA server host (default 127.0.0.1)")
ap.add_argument("--port", type=int, default=2000, help="CARLA server RPC port (default 2000)")
ap.add_argument("--tm-port", type=int, default=8000, help="Traffic Manager port (default 8000)")
ap.add_argument("--seed", type=int, default=None,
                help="random seed for repeatable spawns/destinations (default: nondeterministic)")
ap.add_argument("--route", action="store_true",
                help="use the Traffic Manager's custom-path routing to send each vehicle toward a far "
                     "edge (margin-to-margin trips). OFF by default because routing can occasionally "
                     "push a vehicle off the road or off a clipped dead-end; plain autopilot keeps "
                     "vehicles strictly on the road network.")
args = ap.parse_args()

if args.seed is not None:
    random.seed(args.seed)


def _edge_of(x, y, b):
    """Which map edge a point is nearest to (W/E/S/N)."""
    dists = (("W", x - b["min_x"]), ("E", b["max_x"] - x),
             ("S", y - b["min_y"]), ("N", b["max_y"] - y))
    return min(dists, key=lambda t: t[1])[0]


def _in_scene(x, y, b):
    """Inside the interior / region of interest (the sandbox inset by one margin)."""
    return (b["min_x"] + b["margin"] <= x <= b["max_x"] - b["margin"] and
            b["min_y"] + b["margin"] <= y <= b["max_y"] - b["margin"])


def _in_ring(x, y, b):
    """Inside the sandbox but within the staging margin of an edge (the entry/exit ring)."""
    inside = (b["min_x"] <= x <= b["max_x"] and b["min_y"] <= y <= b["max_y"])
    return inside and not _in_scene(x, y, b)


def _inward_min(x, y, b):
    """Signed distance to the nearest interior (blue) edge: +ve inside the interior, -ve in the margin."""
    m = b["margin"]
    return min(x - (b["min_x"] + m), (b["max_x"] - m) - x,
               y - (b["min_y"] + m), (b["max_y"] - m) - y)


def _red_clearance(x, y, b):
    """Distance (m) to the nearest red (sandbox) edge — the literal map edge. Small => must despawn."""
    return min(x - b["min_x"], b["max_x"] - x, y - b["min_y"], b["max_y"] - y)


def _interior_opacity(cx, cy, yaw_deg, ext_x, ext_y, b):
    """Opacity [0,1] = the fraction of the vehicle's footprint that lies INSIDE the interior, i.e. past
    the blue line (one margin in from the red edge). 0 = the whole vehicle is still within the margin
    (fully transparent); 1 = the whole vehicle is in the interior (fully opaque). The change happens
    over the vehicle's OWN length as it straddles the nearest blue boundary — e.g. only the hood across
    -> ~0.33, hood+cabin across -> ~0.66 — per the staging spec."""
    sW = cx - (b["min_x"] + b["margin"])     # inward signed distance from each interior (blue) edge
    sE = (b["max_x"] - b["margin"]) - cx
    sS = cy - (b["min_y"] + b["margin"])
    sN = (b["max_y"] - b["margin"]) - cy
    axis, s = min((("x", sW), ("x", sE), ("y", sS), ("y", sN)), key=lambda e: e[1])
    yaw = math.radians(yaw_deg)
    hx = abs(ext_x * math.cos(yaw)) + abs(ext_y * math.sin(yaw))   # vehicle AABB half-extent, world x
    hy = abs(ext_x * math.sin(yaw)) + abs(ext_y * math.cos(yaw))   # ... world y
    h = hx if axis == "x" else hy
    if h <= 1e-3:
        return 1.0 if s >= 0.0 else 0.0
    return max(0.0, min(1.0, (s + h) / (2.0 * h)))


def _scene_center(b):
    return (0.5 * (b["min_x"] + b["max_x"]), 0.5 * (b["min_y"] + b["max_y"]))


def _is_inward(tf, b):
    """Spawning here and driving forward heads into the scene rather than off the edge."""
    cx, cy = _scene_center(b)
    yaw = math.radians(tf.rotation.yaw)
    return math.cos(yaw) * (cx - tf.location.x) + math.sin(yaw) * (cy - tf.location.y) > 0.0


def main() -> int:
    client = carla.Client(args.host, args.port)
    client.set_timeout(20.0)
    print(f"server version: {client.get_server_version()}")
    world = client.get_world()

    staging = None
    try:
        staging = world.get_staging_bounds()
    except Exception as e:
        print(f"get_staging_bounds failed: {e!r}", file=sys.stderr)
    if not staging:
        print("ERROR: no staging bounds. Build a draped world first:\n"
              "       python test_digital_twin.py --height-align drape", file=sys.stderr)
        return 1
    print(f"staging: x[{staging['min_x']:.0f},{staging['max_x']:.0f}] "
          f"y[{staging['min_y']:.0f},{staging['max_y']:.0f}] margin {staging['margin']:.0f} m")

    bp_lib = world.get_blueprint_library()
    blueprints = list(bp_lib.filter(args.filter))
    # Skip two-wheelers for this demo: their rider is a separate pedestrian mesh that does not fade
    # yet, so a fading bike with a solid rider would look wrong. Match by blueprint id.
    _TWO_WHEELED = ("harley", "kawasaki", "yamaha", "vespa", "motorcycle",
                    "omafiets", "crossbike", "bike", "bicycle", "diamondback", "gazelle")
    def _is_two_wheeled(b):
        try:
            bid = str(b.id).lower()
        except Exception:
            return False
        return any(k in bid for k in _TWO_WHEELED)
    cars = [b for b in blueprints if not _is_two_wheeled(b)]
    if cars:
        blueprints = cars
    if args.generation != "all":
        try:
            gen = int(args.generation)
            blueprints = [b for b in blueprints
                          if b.has_attribute('generation') and int(b.get_attribute('generation')) == gen]
        except Exception:
            print(f"warning: bad --generation {args.generation!r}; ignoring", file=sys.stderr)
    if not blueprints:
        print("ERROR: no vehicle blueprints matched --filter / --generation", file=sys.stderr)
        return 1

    # Edge-ring spawn points that face inward (real OSM-edge roads).
    ring_sps = [sp for sp in world.get_map().get_spawn_points()
                if _in_ring(sp.location.x, sp.location.y, staging) and _is_inward(sp, staging)]
    if len(ring_sps) < 2:
        print(f"ERROR: only {len(ring_sps)} inward edge-ring spawn points; need >=2 for "
              "margin-to-margin trips. Select a larger OSM area or a smaller --terrain-margin.",
              file=sys.stderr)
        return 1
    # Spawn only in the MIDDLE of the margin band: at least 2 m inside the blue line (so a vehicle
    # starts fully transparent and materialises only as it later crosses blue) AND at least 5 m clear
    # of the red map edge (so it never spawns on the edge or immediately drives off the world). Fall
    # back to the full ring if too few qualify.
    spawn_pool = [sp for sp in ring_sps
                  if _inward_min(sp.location.x, sp.location.y, staging) <= -2.0
                  and _red_clearance(sp.location.x, sp.location.y, staging) >= 5.0]
    if len(spawn_pool) < 8:
        spawn_pool = ring_sps
    print(f"{len(ring_sps)} inward edge-ring spawn points; {len(spawn_pool)} usable in-margin spawn points")

    # Asynchronous only: the server free-runs (its -game loop ticks itself) and we pace this loop to
    # the server's frames with wait_for_tick(), reading/commanding over RPC. This coexists with
    # eo_observer and matches generate_traffic_carlanet --asynch. We do not enable synchronous_mode.
    tm = client.get_trafficmanager(args.tm_port)
    try:
        tm.set_synchronous_mode(False)
    except Exception:
        pass
    # If a previous synchronous run left the world in sync mode, there'd be no tick master now and the
    # world would be frozen. Put it back to async so vehicles actually move.
    try:
        settings = world.get_settings()
        if settings.synchronous_mode:
            settings.synchronous_mode = False
            settings.fixed_delta_seconds = None
            world.apply_settings(settings)
            print("note: world was synchronous with no master; switched it back to asynchronous.")
    except Exception:
        pass

    def _spawn_bp():
        bp = random.choice(blueprints)
        if bp.has_attribute('color'):
            bp.set_attribute('color', random.choice(bp.get_attribute('color').recommended_values))
        bp.set_attribute('role_name', 'autopilot')
        return bp

    # ── fade self-test: prove set_actor_fade is actually wired (else the demo is silently solid) ──
    probe = None
    for sp in random.sample(ring_sps, min(len(ring_sps), 8)):
        try:
            probe = world.spawn_actor(_spawn_bp(), sp)
            break
        except Exception:
            continue
    if probe is None:
        print("ERROR: could not spawn a probe vehicle to test fade.", file=sys.stderr)
        return 1
    try:
        probe.set_fade(0.5)
        print("fade self-test OK (set_actor_fade is wired).")
    except Exception as e:
        print("ERROR: set_actor_fade is NOT available — the fade would be a silent no-op.\n"
              f"       ({e!r})\n"
              "       Rebuild the server + wheel:  BuildCarla.ps1 -Vs 2026 -InstallWheel\n"
              "       then restart RunCarlaServer.ps1.", file=sys.stderr)
        try: probe.destroy()
        except Exception: pass
        return 1
    try: probe.destroy()
    except Exception: pass

    def _pick_destination(spawn_tf):
        """A ring spawn point on a different edge, far from the spawn (so the trip crosses the scene
        and never despawns back where it started)."""
        s_edge = _edge_of(spawn_tf.location.x, spawn_tf.location.y, staging)
        cands = [sp for sp in ring_sps
                 if _edge_of(sp.location.x, sp.location.y, staging) != s_edge]
        if not cands:
            cands = ring_sps
        cands.sort(key=lambda sp: -spawn_tf.location.distance(sp.location))
        # random among the farther half to keep variety while staying a real cross-map trip
        return random.choice(cands[:max(1, len(cands) // 2)])

    # active vehicle records: id -> {actor, entered, xy, stuck, misses}. We keep the actor handle from
    # spawn and act on it directly (get_location/set_fade work by id). A vehicle is dropped only after
    # the server's actor list has been MISSING it for several checks (grace) -- a just-spawned vehicle
    # lags a tick or two before it appears in the world-observer cache, and popping it on the first
    # miss is exactly what left vehicles untracked and fully opaque.
    actors = {}
    last_spawn = 0.0
    OOB_PAD = 8.0            # metres beyond the sandbox before a vehicle counts as having left/fallen
    RED_CLEAR = 3.0         # despawn before the vehicle gets this close to the red (map) edge
    CHECK_S = 0.1            # reconcile/fade cadence (10 Hz is smooth; keeps RPC traffic modest)
    MISS_LIMIT = 5          # consecutive cache-misses (~0.5 s) before treating a vehicle as gone
    mnx, mny, mxx, mxy = staging["min_x"], staging["min_y"], staging["max_x"], staging["max_y"]
    cxc, cyc = _scene_center(staging)
    try:
        _gzc = world.ground_z_below(cxc, cyc, 5000.0, search=10000.0)
        floor_z = (float(_gzc) - 50.0) if _gzc is not None else -1000.0
    except Exception:
        floor_z = -1000.0

    def _spawn_one():
        pool = list(spawn_pool); random.shuffle(pool)
        for sp in pool[:12]:
            try:
                v = world.spawn_actor(_spawn_bp(), sp)
            except Exception:
                continue
            try:
                bb = v.bounding_box
                ext = (float(bb.extent.x), float(bb.extent.y))   # vehicle half-length, half-width (m)
            except Exception:
                ext = (2.4, 1.0)                                  # sedan-ish fallback
            try:
                # Start at the correct opacity for the spawn pose (fully transparent in the margin).
                op = _interior_opacity(sp.location.x, sp.location.y, sp.rotation.yaw, ext[0], ext[1], staging)
                _safe_fade(v, 1.0 - op)
                v.set_autopilot(True, args.tm_port)
                if args.route:
                    tm.set_path(v, [_pick_destination(sp).location])
            except Exception as e:
                print(f"  setup failed for {v.id}: {e!r}", file=sys.stderr)
                try: v.destroy()
                except Exception: pass
                continue
            actors[v.id] = {"actor": v, "ext": ext, "entered": False,
                            "xy": None, "stuck": 0.0, "misses": 0}
            return True
        return False

    def _despawn(vid, actor):
        try: actor.set_fade(1.0)            # force fully transparent so a lagging destroy is never seen
        except Exception: pass
        try: actor.set_autopilot(False, args.tm_port)
        except Exception: pass
        try: actor.destroy()
        except Exception: pass
        actors.pop(vid, None)

    # Stop cleanly on Ctrl+C. A bare KeyboardInterrupt can be swallowed while blocked inside a .NET
    # call (pythonnet), so also flip a flag from a SIGINT handler and poll it in the loop.
    stop = {"flag": False}
    try:
        signal.signal(signal.SIGINT, lambda *a: stop.__setitem__("flag", True))
    except Exception:
        pass

    print(f"spawning up to {args.max} vehicles ({'routed' if args.route else 'autopilot'}); "
          "Ctrl+C to stop.")
    last_check = 0.0
    try:
        while not stop["flag"]:
            now = time.time()
            if len(actors) < args.max and (now - last_spawn) >= args.spawn_interval:
                last_spawn = now
                _spawn_one()

            if now - last_check >= CHECK_S:
                last_check = now
                ids = list(actors.keys())
                # Which of our tracked ids the server currently reports. Used ONLY to prune the
                # genuinely-gone after a grace period. If the query yields nothing for a non-empty set
                # (flaky/early cache), assume all live so we never mass-drop the whole fleet.
                live_ids = set()
                if ids:
                    try:
                        live_ids = {a.id for a in world.get_actors(ids)}
                    except Exception:
                        live_ids = set(ids)
                    if not live_ids:
                        live_ids = set(ids)
                for vid in ids:
                    rec = actors[vid]
                    if vid not in live_ids:
                        rec["misses"] += 1
                        if rec["misses"] >= MISS_LIMIT:
                            actors.pop(vid, None)        # gone for ~0.5 s -> drop (no death-id spam)
                        continue                          # grace: don't touch until confirmed present
                    rec["misses"] = 0
                    a = rec["actor"]
                    try:
                        tf = a.get_transform()
                    except Exception:
                        actors.pop(vid, None); continue
                    loc = tf.location; yaw = tf.rotation.yaw
                    # Off-world / out-of-bounds backstop.
                    if (loc.x < mnx - OOB_PAD or loc.x > mxx + OOB_PAD or
                            loc.y < mny - OOB_PAD or loc.y > mxy + OOB_PAD or loc.z < floor_z):
                        _despawn(vid, a); continue
                    # Never let an entered vehicle reach the red (map) edge: despawn it while still in
                    # the margin as it heads back out. This guards the EXIT only -- a freshly spawned
                    # vehicle that has not yet entered the interior is exempt, because on a tightly
                    # clipped map (roads cut exactly at the sandbox boundary) every edge spawn point sits
                    # right on the red edge, and despawning on entry would kill every vehicle instantly.
                    if rec["entered"] and _red_clearance(loc.x, loc.y, staging) <= RED_CLEAR:
                        _despawn(vid, a); continue
                    # Stuck (clipped dead-end stub / sunk spawn): never moves -> remove after 6 s. It is
                    # fully transparent in the margin, so this is unseen; a fresh one enters elsewhere.
                    xy = rec["xy"]; rec["xy"] = (loc.x, loc.y)
                    if rec["entered"] or xy is None or math.hypot(loc.x - xy[0], loc.y - xy[1]) > 0.05:
                        rec["stuck"] = 0.0
                    else:
                        rec["stuck"] += CHECK_S
                        if rec["stuck"] >= 6.0:
                            _despawn(vid, a); continue
                    # Opacity = fraction of the vehicle's footprint past the blue line into the interior;
                    # set_fade takes the inverse (hide). Fully transparent in the margin, ramps to solid
                    # over the vehicle's own length as it crosses blue.
                    op = _interior_opacity(loc.x, loc.y, yaw, rec["ext"][0], rec["ext"][1], staging)
                    _safe_fade(a, 1.0 - op)
                    if op >= 0.99:
                        rec["entered"] = True            # fully materialised in the interior
                    elif rec["entered"] and op <= 0.02:
                        _despawn(vid, a)                 # fully back in the margin (invisible) -> remove

            try:
                world.wait_for_tick(2.0)
            except Exception:
                time.sleep(0.05)
    except KeyboardInterrupt:
        pass
    finally:
        print("\nstopping; despawning vehicles...")
        for rec in list(actors.values()):
            try: rec["actor"].destroy()
            except Exception: pass
    return 0


def _safe_fade(v, hide):
    try:
        v.set_fade(hide)
    except Exception:
        pass


if __name__ == "__main__":
    sys.exit(main())
