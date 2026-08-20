"""Boundary-aware staging traffic controller for CARLA.

Manages spawning, despawning, and opacity fading of traffic vehicles within
a defined staging area with margin-based entry/exit zones.
"""

from __future__ import annotations

import logging
import math
import os
import random
import time

import carlanet as carla


class TrafficController:
    """Boundary-aware staging traffic as a steppable subsystem.

    enable()/disable() start and stop the population; update(now) does the per-frame
    spawn top-up, reconcile, and opacity fade. Stopping despawns every tracked vehicle cleanly.
    """

    OOB_PAD = 2.0  # metres beyond the red (sandbox) edge before a vehicle is culled as having left
    RED_CLEAR = 3.0  # despawn before an entered vehicle gets this close to the red (map) edge
    CHECK_S = 0.1  # reconcile/fade cadence
    MISS_LIMIT = 5  # consecutive cache-misses before treating a vehicle as gone
    SPAWN_GRACE = 4.0  # a fresh vehicle is exempt from the stuck/out-of-bounds guards this long
    SUMMARY_S = 5.0  # how often to print the alive/despawn-reason summary while enabled

    _TWO_WHEELED = (
        "harley",
        "kawasaki",
        "yamaha",
        "vespa",
        "motorcycle",
        "omafiets",
        "crossbike",
        "bike",
        "bicycle",
        "diamondback",
        "gazelle",
    )

    @staticmethod
    def scene_center(b):
        """Center (x, y) of the sandbox."""
        return ((b["min_x"] + b["max_x"]) / 2.0, (b["min_y"] + b["max_y"]) / 2.0)

    @staticmethod
    def edge_of(x, y, b):
        """Which red (sandbox) edge is (x, y) nearest to: 'W', 'E', 'S', or 'N'."""
        d_w = x - b["min_x"]
        d_e = b["max_x"] - x
        d_s = y - b["min_y"]
        d_n = b["max_y"] - y
        return min((("W", d_w), ("E", d_e), ("S", d_s), ("N", d_n)), key=lambda e: e[1])[0]

    @staticmethod
    def in_scene(x, y, b):
        """Inside the interior / region of interest (the sandbox inset by one margin)."""
        return (
            b["min_x"] + b["margin"] <= x <= b["max_x"] - b["margin"]
            and b["min_y"] + b["margin"] <= y <= b["max_y"] - b["margin"]
        )

    @staticmethod
    def in_ring(x, y, b):
        """Inside the sandbox but within the staging margin of an edge (the entry/exit ring)."""
        inside = b["min_x"] <= x <= b["max_x"] and b["min_y"] <= y <= b["max_y"]
        return inside and not TrafficController.in_scene(x, y, b)

    @staticmethod
    def inward_min(x, y, b):
        """Signed distance to the nearest interior (blue) edge: +ve inside the interior, -ve in margin."""
        m = b["margin"]
        return min(
            x - (b["min_x"] + m),
            (b["max_x"] - m) - x,
            y - (b["min_y"] + m),
            (b["max_y"] - m) - y,
        )

    @staticmethod
    def red_clearance(x, y, b):
        """Distance (m) to the nearest red (sandbox) edge — the literal map edge."""
        return min(x - b["min_x"], b["max_x"] - x, y - b["min_y"], b["max_y"] - y)

    @staticmethod
    def is_inward(tf, b):
        """Spawning here and driving forward heads into the scene rather than off the edge."""
        cx, cy = TrafficController.scene_center(b)
        yaw = math.radians(tf.rotation.yaw)
        return math.cos(yaw) * (cx - tf.location.x) + math.sin(yaw) * (cy - tf.location.y) > 0.0

    @staticmethod
    def interior_opacity(cx, cy, yaw_deg, ext_x, ext_y, b):
        """Opacity [0,1] = the fraction of the vehicle's footprint that lies INSIDE the interior (past
        the blue line, one margin in from the red edge). 0 = wholly within the margin (transparent); 1 =
        wholly in the interior (opaque). The change spans the vehicle's own length as it straddles the
        nearest blue boundary."""
        s_w = cx - (b["min_x"] + b["margin"])
        s_e = (b["max_x"] - b["margin"]) - cx
        s_s = cy - (b["min_y"] + b["margin"])
        s_n = (b["max_y"] - b["margin"]) - cy
        axis, s = min((("x", s_w), ("x", s_e), ("y", s_s), ("y", s_n)), key=lambda e: e[1])
        yaw = math.radians(yaw_deg)
        hx = abs(ext_x * math.cos(yaw)) + abs(ext_y * math.sin(yaw))
        hy = abs(ext_x * math.sin(yaw)) + abs(ext_y * math.cos(yaw))
        h = hx if axis == "x" else hy
        if h <= 1e-3:
            return 1.0 if s >= 0.0 else 0.0
        return max(0.0, min(1.0, (s + h) / (2.0 * h)))

    @staticmethod
    def configure_traffic_manager(tm, sync: bool, fixed_delta: float, seed: int | None) -> None:
        """Configure Traffic Manager for synchronous or asynchronous world mode.

        Args:
            tm: CARLA TrafficManager instance
            sync: True if world is in synchronous mode
            fixed_delta: World fixed delta seconds (sync mode only)
            seed: Random seed for TM (optional)
        """
        logger = logging.getLogger(__name__)
        try:
            tm.set_synchronous_mode(False)
        except Exception as e:
            logger.debug(f"failed to set TM synchronous mode: {e}")

        if sync and seed is not None:
            try:
                tm.set_random_device_seed(seed)
            except Exception as e:
                logger.debug(f"failed to set TM random seed: {e}")

        if sync:
            logger.info(
                f"mode: SYNCHRONOUS world + free-running Traffic Manager "
                f"(fixed_delta {fixed_delta}s -> ~{1.0 / fixed_delta:.0f} fps, real-time; "
                "traffic is not deterministic)"
            )
        else:
            logger.info("mode: ASYNCHRONOUS (server free-running)")

    def __init__(
        self,
        world: carla.World,
        tm,
        args,
        staging: dict | None,
        blueprints: list,
        ring_sps: list,
        spawn_pool: list,
        floor_z: float,
    ):
        self.world = world
        self.tm = tm
        self.args = args
        self.logger = logging.getLogger(__name__)
        self.b = staging
        self.blueprints = blueprints
        self.ring_sps = ring_sps
        self.spawn_pool = spawn_pool
        self.floor_z = floor_z
        self.available = True
        self.reason = ""
        self.enabled = False
        self.want_enabled = False
        self.actors = {}
        self.last_spawn = 0.0
        self.last_check = 0.0
        self.last_summary = 0.0
        self.despawns = {}
        self.routes_planned = 0
        self.unroutable_spawns = 0
        self.route_plan_ms_max = 0.0
        self.route_plan_ms_total = 0.0
        self.route_searches = 0
        self.spawn_ms = 0.0
        self.reconcile_ms = 0.0
        self.stuck_travel = []
        self.stalled_models = {}
        # Destinations beyond the entry ring, and the ones already known to route from a
        # given entry point. See destination_candidates for why the ring alone is not
        # enough to keep a sparse network populated.
        try:
            self.map_sps = list(world.get_map().get_spawn_points())
        except Exception as e:  # the ring alone still works, just less well
            self.logger.debug(f"failed to read map spawn points: {e}")
            self.map_sps = []
        self.reached_from = {}
        # Same-side routes, and everything they are rated against. A vehicle is meant to
        # cross the scene, so leaving by the side it entered from is a last resort and its
        # share of all routes is held to --same-side-exit-rate. Counted since traffic was
        # last switched on, so the rate describes this session rather than the process.
        self.routes_session = 0
        self.routes_same_side = 0
        self.same_side_refused = set()

        if staging is not None:
            self.logger.info(
                f"traffic controller initialized: {len(blueprints)} blueprints, {len(spawn_pool)} spawn points"
            )

    def apply_want(self) -> None:
        """Reconcile actual on/off with the hotkey's desired state."""
        if self.want_enabled and not self.enabled:
            if not self.enable():
                self.want_enabled = False
        elif not self.want_enabled and self.enabled:
            self.disable()

    def toggle_want(self, enabled: bool | None = None) -> None:
        """
        Toggle the want_enabled state.

        Args:
            enabled: If None, toggle the current state. If a boolean, set the state to this value.
        """
        if enabled is None:
            self.want_enabled = not self.want_enabled
        else:
            self.want_enabled = enabled

    @classmethod
    def create(cls, world: carla.World, client: carla.Client, tm, args):
        """Build the controller, computing the spawn pool and verifying set_actor_fade.

        Args:
            world: CARLA world instance
            client: CARLA client instance
            tm: Traffic manager instance
            args: Command-line arguments
        """
        logger = logging.getLogger(__name__)
        stub = cls(world, tm, args, None, [], [], [], -1000.0)
        try:
            staging = world.get_staging_bounds()
        except Exception as e:
            stub.available = False
            stub.reason = f"get_staging_bounds failed: {e!r}"
            return stub
        if not staging:
            stub.available = False
            stub.reason = "no staging bounds (this world was loaded, not built from an OSM area)"
            return stub

        logger.info(
            f"staging bounds retrieved: {staging['max_x'] - staging['min_x']:.0f}x{staging['max_y'] - staging['min_y']:.0f}m, margin={staging['margin']:.0f}m"
        )

        bp_lib = world.get_blueprint_library()
        blueprints = list(bp_lib.filter(args.filter))
        logger.info(f"filtered {len(blueprints)} vehicle blueprints matching '{args.filter}'")
        cars = [b for b in blueprints if not cls.is_two_wheeled(b)]
        if cars:
            blueprints = cars
            logger.info(
                f"excluded two-wheeled vehicles: {len(blueprints)} car blueprints remaining"
            )
        if args.generation != "all":
            try:
                gen = int(args.generation)
                blueprints = [
                    b
                    for b in blueprints
                    if b.has_attribute("generation") and int(b.get_attribute("generation")) == gen
                ]
                logger.info(f"filtered to generation {gen}: {len(blueprints)} blueprints")
            except Exception as e:
                logger.warning(f"bad --generation {args.generation!r}; ignoring: {e}")
        if not blueprints:
            stub.available = False
            stub.reason = "no vehicle blueprints matched --filter / --generation"
            return stub

        ring_sps = [
            sp
            for sp in world.get_map().get_spawn_points()
            if cls.in_ring(sp.location.x, sp.location.y, staging) and cls.is_inward(sp, staging)
        ]
        logger.info(f"found {len(ring_sps)} inward edge-ring spawn points")
        if len(ring_sps) < 2:
            stub.available = False
            stub.reason = (
                f"only {len(ring_sps)} inward edge-ring spawn points; need >=2 "
                "(select a larger OSM area or a smaller --terrain-margin)"
            )
            return stub
        spawn_pool = [
            sp
            for sp in ring_sps
            if cls.inward_min(sp.location.x, sp.location.y, staging) <= -2.0
            and cls.fits_inside_red_edge(
                sp.location.x,
                sp.location.y,
                sp.rotation.yaw,
                cls._ASSUMED_EXTENT,
                staging,
            )
        ]
        if len(spawn_pool) < 8:
            logger.info(
                f"spawn pool too small ({len(spawn_pool)}), using all {len(ring_sps)} ring spawn points"
            )
            spawn_pool = ring_sps

        cx, cy = cls.scene_center(staging)
        try:
            gz = world.ground_z_below(cx, cy, 5000.0, search=10000.0)
            floor_z = (float(gz) - 50.0) if gz is not None else -1000.0
        except Exception as e:
            logger.debug(f"failed to get floor z: {e}")
            floor_z = -1000.0

        ctl = cls(world, tm, args, staging, blueprints, ring_sps, spawn_pool, floor_z)
        if not ctl.fade_selftest():
            ctl.available = False
            ctl.reason = (
                "set_actor_fade not available — rebuild server + wheel "
                "(BuildCarla.ps1 -Vs 2026 -InstallWheel)"
            )
            return ctl
        logger.info("fade selftest passed: set_actor_fade available")

        sw = staging["max_x"] - staging["min_x"]
        sh = staging["max_y"] - staging["min_y"]
        m = staging["margin"]
        logger.info(
            f"traffic: scene {sw:.0f} x {sh:.0f} m, margin {m:.0f} m "
            f"(interior {sw - 2 * m:.0f} x {sh - 2 * m:.0f} m)"
        )
        rc = [cls.red_clearance(sp.location.x, sp.location.y, staging) for sp in spawn_pool]
        im = [cls.inward_min(sp.location.x, sp.location.y, staging) for sp in spawn_pool]
        logger.info(
            f"traffic: spawn-pool red-clearance {min(rc):.0f}..{max(rc):.0f} m, "
            f"inward {min(im):.0f}..{max(im):.0f} m (negative inward = inside the margin, as intended)"
        )
        logger.info(
            f"traffic: {len(ring_sps)} inward edge-ring spawn points; "
            f"{len(spawn_pool)} usable in-margin spawn points (set_actor_fade OK)"
        )

        try:
            tm.set_traffic_diagnostics(args.traffic_diagnostics)
            if args.traffic_diagnostics:
                logger.info("traffic: per-vehicle diagnostics ON (']' toggles)")
        except Exception as e:
            logger.warning("traffic: diagnostics switch unavailable (%r)", e)
        if args.log:
            try:
                tm.set_event_log_path(os.path.abspath(args.log))
            except Exception as e:
                logger.warning("traffic: traffic-manager lines will not reach the log (%r)", e)
        if args.speed_scale != 100.0:
            try:
                tm.set_global_percentage_speed_difference(100.0 - args.speed_scale)
                logger.info(
                    "traffic: driving %.0f%% of each road's posted speed limit",
                    args.speed_scale,
                )
            except Exception as e:
                logger.warning("traffic: speed scale unavailable (%r)", e)
        if args.route:
            try:
                tm.set_route_replan_attempt_limit(args.route_replan_limit)
                tm.set_route_greedy_fallback_enabled(args.route_greedy_fallback)
            except Exception as e:
                logger.warning("traffic: route recovery knobs unavailable (%r); using defaults", e)
            if args.route_greedy_fallback:
                recovery = (
                    f"after {args.route_replan_limit} failed replans, steer greedily"
                    if args.route_replan_limit > 0
                    else "keep replanning (limit 0)"
                )
            else:
                recovery = "keep replanning indefinitely"
            logger.info(
                "traffic: routes are planned before spawn; off-route recovery = %s", recovery
            )

        if args.start_traffic:
            ctl.want_enabled = True

        logger.info(
            f"traffic controller ready: {len(blueprints)} blueprints, {len(spawn_pool)} spawn points, max={args.max} vehicles"
        )

        return ctl

    @classmethod
    def is_two_wheeled(cls, b):
        try:
            return any(k in str(b.id).lower() for k in cls._TWO_WHEELED)
        except Exception:
            return False

    def spawn_bp(self):
        bp = random.choice(self.blueprints)
        if bp.has_attribute("color"):
            bp.set_attribute("color", random.choice(bp.get_attribute("color").recommended_values))
        bp.set_attribute("role_name", "autopilot")
        return bp

    def fade_selftest(self):
        probe = None
        for sp in random.sample(self.ring_sps, min(len(self.ring_sps), 8)):
            try:
                probe = self.world.spawn_actor(self.spawn_bp(), sp)
                break
            except Exception:
                continue
        if probe is None:
            return False
        ok = True
        try:
            probe.set_fade(0.5)
        except Exception as e:
            self.logger.debug(f"set_fade test failed: {e}")
            ok = False
        try:
            probe.destroy()
        except Exception:
            pass
        return ok

    @staticmethod
    def _place_key(location):
        return (round(location.x, 1), round(location.y, 1))

    def destination_candidates(self, spawn_tf):
        """Destinations to try from this entry point, best first.

        Traffic is meant to cross the scene, so a ring point on another edge comes first
        and the far ones before the near.

        The ring alone is not enough. Where a network is sparse, most pairs of entry
        points have no route between them: on the Hormuz trunk highway the ring is three
        distinct points, and 14 of the 20 ordered pairs between them cannot be routed,
        because a divided highway clipped to a box offers no way to turn from one
        carriageway to the other. Drawing only from that set left the map with no ambient
        traffic at all, every spawn being skipped as unreachable. The same shape of
        failure keeps vehicles off a motorway that crosses a denser map.

        So the map's own spawn points follow the ring. A vehicle then still gets a route
        across the scene rather than none, even if it ends inside the scene rather than
        at the far edge.
        """
        spawn_edge = self.edge_of(spawn_tf.location.x, spawn_tf.location.y, self.b)
        far_first = sorted(self.ring_sps, key=lambda sp: -spawn_tf.location.distance(sp.location))
        other_edge = [
            sp
            for sp in far_first
            if self.edge_of(sp.location.x, sp.location.y, self.b) != spawn_edge
        ]
        same_edge = [sp for sp in far_first if sp not in other_edge]
        elsewhere = sorted(self.map_sps, key=lambda sp: -spawn_tf.location.distance(sp.location))

        ordered, seen = [], {self._place_key(spawn_tf.location)}
        for sp in other_edge + same_edge + elsewhere:
            key = self._place_key(sp.location)
            if key in seen:
                continue
            seen.add(key)
            ordered.append(sp)
        # Furthest first, across the ring and the map together. Preferring any ring point
        # over any other destination sends a vehicle to the nearest edge rather than
        # across the scene: entering at the west of the Hormuz highway, the far entry is
        # unreachable because it is an entry, and the next ring point along is 2.0 km away
        # on the same side, where the east exit it should be heading for is 4.9 km off.
        # Distance says what the ring was standing in for, and the sort is stable, so a
        # ring point still wins against anything equally far.
        ordered.sort(key=lambda sp: -spawn_tf.location.distance(sp.location))
        return ordered

    def pick_destination(self, spawn_tf):
        candidates = self.destination_candidates(spawn_tf)
        if not candidates:
            return spawn_tf
        head = candidates[: max(1, min(len(candidates), self._ROUTE_DESTINATION_TRIES) // 2)]
        return random.choice(head)

    def same_side(self, spawn_tf, destination_tf):
        """True when a destination lies on the same side of the area as the entry."""
        return self.edge_of(
            destination_tf.location.x, destination_tf.location.y, self.b
        ) == self.edge_of(spawn_tf.location.x, spawn_tf.location.y, self.b)

    def same_side_allowed(self):
        """Whether a same-side exit may be used at all.

        Only reached once every crossing has been tried and none could be routed, so by
        the time this is asked a same-side exit is the only route there is. Rationing it
        against a share of the session's routes would then be rationing against itself:
        if the only routes available are same-side, their share is one, and any rate below
        one refuses forever. The rate says whether doubling back is permitted; the search
        order is what keeps it rare.

        Zero means zero. An entry that can only reach its own side spawns nothing.
        """
        return float(getattr(self.args, "same_side_exit_rate", 0.0) or 0.0) > 0.0

    def plan_route_from(self, spawn_tf):
        """Plan a route from an entry point, walking candidates until one is reachable.

        Re-drawing at random from a handful of ring points cannot find the route that
        exists: with three distinct entry points, four draws keep asking about the same
        two unreachable destinations. The candidates are walked instead.

        They are biased to the far half, so a vehicle crosses the scene rather than
        leaving by the nearest edge, but shuffled within it. Taking the single furthest
        every time sends every vehicle from an entry down one line -- measured, eight
        successive spawns from the west entry all chose the same destination -- when
        several of the reachable ones are equally good exits.

        A destination that routed from this entry is remembered and usually reused, since
        each search is an A* over the road graph and repeating the failures every spawn is
        expensive as well as fruitless. Some spawns look for a new one anyway, so the set
        of known destinations grows instead of freezing on whichever was found first.
        """
        entry = self._place_key(spawn_tf.location)
        known = self.reached_from.setdefault(entry, [])
        known_keys = {self._place_key(sp.location) for sp in known}

        fresh = [
            sp
            for sp in self.destination_candidates(spawn_tf)
            if self._place_key(sp.location) not in known_keys
        ]
        far = fresh[: max(1, len(fresh) // 2)]
        random.shuffle(far)
        fresh = far + fresh[len(far) :]

        exploring = not known or random.random() < self._DESTINATION_EXPLORE_CHANCE
        remembered = random.sample(known, len(known))
        order = fresh + remembered if exploring else remembered + fresh

        # Every crossing is tried before any same-side exit, so doubling back is used
        # only when there is no route out of the other sides at all -- not merely when a
        # same-side destination happened to sort well.
        crossing = [sp for sp in order if not self.same_side(spawn_tf, sp)]
        same_side = [sp for sp in order if self.same_side(spawn_tf, sp)]
        if same_side and not self.same_side_allowed():
            same_side = []
            if not crossing and entry not in self.same_side_refused:
                self.same_side_refused.add(entry)
                self.logger.warning(
                    "the entry at (%.0f, %.0f) can only reach destinations on the %s side "
                    "it came in on, and --same-side-exit-rate is 0, so it spawns nothing. "
                    "Give the rate a non-zero value to let traffic double back here.",
                    spawn_tf.location.x,
                    spawn_tf.location.y,
                    self.edge_of(spawn_tf.location.x, spawn_tf.location.y, self.b),
                )
        order = crossing + same_side

        budget = self._ROUTE_DESTINATION_TRIES + self._ROUTE_FALLBACK_TRIES
        for destination_tf in order[:budget]:
            t0 = time.perf_counter()
            try:
                route = self.tm.plan_route(spawn_tf.location, destination_tf.location)
            except Exception as e:
                self.logger.warning("route planning unavailable: %r", e)
                return None
            elapsed_ms = (time.perf_counter() - t0) * 1000.0
            self.route_searches += 1
            self.route_plan_ms_total += elapsed_ms
            self.route_plan_ms_max = max(self.route_plan_ms_max, elapsed_ms)
            if route is not None:
                key = self._place_key(destination_tf.location)
                if key not in known_keys and len(known) < self._REMEMBERED_DESTINATIONS:
                    known.append(destination_tf)
                self.routes_session += 1
                if self.same_side(spawn_tf, destination_tf):
                    self.routes_same_side += 1
                    if entry not in self.same_side_refused:
                        self.same_side_refused.add(entry)
                        self.logger.warning(
                            "the entry at (%.0f, %.0f) has no route to another side, so "
                            "its traffic leaves by the %s side it came in on. Permitted "
                            "because --same-side-exit-rate is %g; set it to 0 to leave "
                            "this entry unused instead.",
                            spawn_tf.location.x,
                            spawn_tf.location.y,
                            self.edge_of(spawn_tf.location.x, spawn_tf.location.y, self.b),
                            float(getattr(self.args, "same_side_exit_rate", 0.0) or 0.0),
                        )
                return route
        return None

    def clear_shift(self, loc, yaw_deg, ext, pad=0.6):
        """Offset (dx, dy) to move a vehicle FORWARD along its lane just far enough that its whole
        footprint clears the nearest red (sandbox) edge."""
        deficit, normal = self.red_edge_deficit(loc.x, loc.y, yaw_deg, ext, self.b, pad)
        if deficit <= 0:
            return (0.0, 0.0)
        yaw = math.radians(yaw_deg)
        ux, uy = math.cos(yaw), math.sin(yaw)
        dot = ux * normal[0] + uy * normal[1]
        if dot < 0.2:
            return (0.0, 0.0)
        distance = min(deficit / dot, self.b["margin"])
        return (ux * distance, uy * distance)

    _NEW_VEHICLE_RADIUS = 3.7
    _SPAWN_CLEAR_PAD = 1.0
    _ASSUMED_EXTENT = (3.5, 1.1)
    _ROUTE_DESTINATION_TRIES = 4
    # Destinations beyond the entry ring to try when no ring point can be reached. A
    # sparse network needs this: the ring is where traffic should ideally end, not the
    # only place it can.
    _ROUTE_FALLBACK_TRIES = 16
    # How often a spawn looks for a destination it has not used from this entry before,
    # rather than reusing one already known to route. Enough that the set keeps growing,
    # rare enough that most spawns cost one search.
    _DESTINATION_EXPLORE_CHANCE = 0.25
    # How many known-good destinations to keep per entry point.
    _REMEMBERED_DESTINATIONS = 8

    def occupied(self, x, y):
        """True if any tracked vehicle's footprint is close enough to (x, y) that spawning there would overlap it."""
        need = self._NEW_VEHICLE_RADIUS + self._SPAWN_CLEAR_PAD
        for rec in self.actors.values():
            p = rec["xy"]
            if p is None:
                continue
            pr = math.hypot(rec["ext"][0], rec["ext"][1])
            if math.hypot(x - p[0], y - p[1]) < (need + pr):
                return True
        return False

    def spawn_one(self, now):
        pool = list(self.spawn_pool)
        random.shuffle(pool)
        for sp in pool:
            ex, ey = self.clear_shift(sp.location, sp.rotation.yaw, self._ASSUMED_EXTENT)
            if self.occupied(sp.location.x, sp.location.y):
                continue
            if self.occupied(sp.location.x + ex, sp.location.y + ey):
                continue
            route = None
            if self.args.route:
                route = self.plan_route_from(sp)
                if route is None:
                    self.unroutable_spawns += 1
                    continue
            bp = self.spawn_bp()
            try:
                v = self.world.spawn_actor(bp, sp)
            except Exception:
                continue
            try:
                bb = v.bounding_box
                ext = (float(bb.extent.x), float(bb.extent.y))
            except Exception:
                ext = (2.4, 1.0)
            sx, sy, syaw = sp.location.x, sp.location.y, sp.rotation.yaw
            dx, dy = self.clear_shift(sp.location, syaw, ext)
            if dx or dy:
                sx += dx
                sy += dy
                try:
                    v.set_transform(
                        carla.Transform(
                            carla.Location(x=sx, y=sy, z=sp.location.z),
                            carla.Rotation(
                                pitch=sp.rotation.pitch, yaw=syaw, roll=sp.rotation.roll
                            ),
                        )
                    )
                except Exception:
                    sx, sy = sp.location.x, sp.location.y
            try:
                op = self.interior_opacity(sx, sy, syaw, ext[0], ext[1], self.b)
                if self.args.fade:
                    self.safe_fade(v, 1.0 - op)
                v.set_autopilot(True, self.args.tm_port)
                difference = 0.0
                if self.args.speed_spread > 0:
                    spread = float(self.args.speed_spread)
                    difference = random.uniform(-spread, spread)
                    self.tm.set_percentage_speed_difference(v, difference)
                limit_kph = 0.0
                if self.args.spawn_at_speed:
                    try:
                        limit_kph = self.tm.get_speed_limit_kph_at(sp.location)
                    except Exception:
                        pass
                if limit_kph > 0.0:
                    target = (
                        (limit_kph / 3.6)
                        * (self.args.speed_scale / 100.0)
                        * (1.0 - difference / 100.0)
                    )
                    yaw = math.radians(syaw)
                    try:
                        v.set_target_velocity(
                            carla.Vector3D(
                                x=target * math.cos(yaw),
                                y=target * math.sin(yaw),
                                z=0.0,
                            )
                        )
                    except Exception:
                        pass
                if route is not None:
                    self.tm.apply_route(v, route)
                    self.routes_planned += 1
            except Exception as e:
                self.logger.warning(f"setup failed for {v.id}: {e!r}")
                try:
                    v.destroy()
                except Exception:
                    pass
                continue
            self.actors[v.id] = {
                "actor": v,
                "ext": ext,
                "entered": False,
                "born": now,
                "xy": (sx, sy),
                "spawn_xy": (sx, sy),
                "routed": route is not None,
                "bp": str(getattr(bp, "id", "?")),
                "stuck": 0.0,
                "stalled": 0.0,
                "misses": 0,
                "speed": 0.0,
            }
            if len(self.actors) == 1:
                self.logger.info(f"first vehicle spawned: id={v.id}")
            return True
        return False

    def despawn(self, vid, actor, reason="other"):
        self.despawns[reason] = self.despawns.get(reason, 0) + 1
        if reason == "stalled":
            rec = self.actors.get(vid)
            if rec:
                model = rec.get("bp", "?").split(".", 1)[-1]
                self.stalled_models[model] = self.stalled_models.get(model, 0) + 1
        if reason == "stuck":
            rec = self.actors.get(vid)
            if rec and rec.get("spawn_xy") and rec.get("xy"):
                sx, sy = rec["spawn_xy"]
                cx, cy = rec["xy"]
                self.stuck_travel.append((math.hypot(cx - sx, cy - sy), rec.get("routed", False)))
        if self.args.fade:
            try:
                actor.set_fade(1.0)
            except Exception:
                pass
        try:
            actor.set_autopilot(False, self.args.tm_port)
        except Exception:
            pass
        try:
            actor.destroy()
        except Exception:
            pass
        self.actors.pop(vid, None)

    def enable(self) -> bool:
        if not self.available:
            self.logger.warning(f"traffic toggle ignored: {self.reason}")
            return False
        if not self.enabled:
            self.enabled = True
            self.last_spawn = 0.0
            # A new session: the same-side share is rated against this session's routes.
            # routes_planned is a lifetime count of routes applied and is left alone.
            self.routes_session = 0
            self.routes_same_side = 0
            self.same_side_refused.clear()
            self.logger.info(
                f"traffic ON (up to {self.args.max}, "
                f"{'routed' if self.args.route else 'autopilot'})"
            )
        return True

    def disable(self) -> None:
        if self.actors:
            self.logger.info(f"traffic OFF; despawning {len(self.actors)} vehicles...")
        for vid in list(self.actors.keys()):
            self.despawn(vid, self.actors[vid]["actor"])
        self.enabled = False

    def update(self, now: float) -> None:
        if not self.enabled:
            return
        b = self.b
        if len(self.actors) < self.args.max and (now - self.last_spawn) >= self.args.spawn_interval:
            self.last_spawn = now
            t0 = time.perf_counter()
            self.spawn_one(now)
            self.spawn_ms += (time.perf_counter() - t0) * 1000.0
        if now - self.last_check < self.CHECK_S:
            return
        self.last_check = now
        reconcile_t0 = time.perf_counter()

        mnx, mny, mxx, mxy = b["min_x"], b["min_y"], b["max_x"], b["max_y"]
        ids = list(self.actors.keys())
        live_ids = set()
        if ids:
            try:
                live_ids = {a.id for a in self.world.get_actors(ids)}
            except Exception as e:
                self.logger.debug(f"get_actors failed: {e}")
                live_ids = set(ids)
            if not live_ids:
                live_ids = set(ids)
        for vid in ids:
            rec = self.actors[vid]
            if vid not in live_ids:
                rec["misses"] += 1
                if rec["misses"] >= self.MISS_LIMIT:
                    self.actors.pop(vid, None)
                continue
            rec["misses"] = 0
            a = rec["actor"]
            try:
                tf = a.get_transform()
            except Exception as e:
                self.logger.debug(f"get_transform failed for {vid}: {e}")
                self.actors.pop(vid, None)
                continue
            loc = tf.location
            yaw = tf.rotation.yaw
            armed = (now - rec["born"]) >= self.SPAWN_GRACE

            xy = rec["xy"]
            rec["xy"] = (loc.x, loc.y)
            dist = math.hypot(loc.x - xy[0], loc.y - xy[1]) if xy is not None else 0.0
            rec["speed"] = dist / self.CHECK_S

            op = self.interior_opacity(loc.x, loc.y, yaw, rec["ext"][0], rec["ext"][1], b)
            if self.args.fade:
                self.safe_fade(a, 1.0 - op)

            if (
                loc.x < mnx - self.OOB_PAD
                or loc.x > mxx + self.OOB_PAD
                or loc.y < mny - self.OOB_PAD
                or loc.y > mxy + self.OOB_PAD
                or (armed and loc.z < self.floor_z)
            ):
                self.despawn(vid, a, "oob")
                continue

            if armed:
                if op >= 0.99:
                    rec["entered"] = True
                if rec["entered"] and op <= 0.02:
                    self.despawn(vid, a, "exited")
                    continue
                if rec["entered"] and self.red_clearance(loc.x, loc.y, b) <= self.RED_CLEAR:
                    self.despawn(vid, a, "red-edge")
                    continue
                if xy is None or dist > 0.05:
                    rec["stuck"] = 0.0
                    rec["stalled"] = 0.0
                elif not rec["entered"]:
                    rec["stuck"] += self.CHECK_S
                    if rec["stuck"] >= 6.0:
                        self.despawn(vid, a, "stuck")
                        continue
                else:
                    rec["stalled"] += self.CHECK_S
                    if self.args.stall_timeout > 0 and rec["stalled"] >= self.args.stall_timeout:
                        self.despawn(vid, a, "stalled")
                        continue

        self.reconcile_ms += (time.perf_counter() - reconcile_t0) * 1000.0

        if now - self.last_summary >= self.SUMMARY_S:
            window = max(self.CHECK_S, now - self.last_summary)
            self.last_summary = now
            entered = sum(1 for r in self.actors.values() if r["entered"])
            speeds = [r["speed"] for r in self.actors.values()]
            avg = sum(speeds) / len(speeds) if speeds else 0.0
            mx = max(speeds) if speeds else 0.0
            reasons = " ".join(f"{k}={v}" for k, v in sorted(self.despawns.items())) or "none"
            routes = ""
            if self.args.route:
                timing = ""
                if self.route_searches:
                    timing = (
                        f", search {self.route_plan_ms_total / self.route_searches:.0f} ms avg "
                        f"{self.route_plan_ms_max:.0f} ms worst"
                    )
                routes = (
                    f" | routes: {self.routes_planned} planned, "
                    f"{self.unroutable_spawns} spawn points skipped as unreachable{timing}"
                )
                self.route_plan_ms_max = 0.0
                self.route_plan_ms_total = 0.0
                self.route_searches = 0
            cost = (
                f" | cost {self.spawn_ms / window:.0f}+{self.reconcile_ms / window:.0f} ms/s "
                f"(spawn+reconcile)"
            )
            self.spawn_ms = 0.0
            self.reconcile_ms = 0.0
            if self.stuck_travel:
                travelled = [d for d, _ in self.stuck_travel]
                never = sum(1 for d in travelled if d < 1.0)
                routed = sum(1 for _, routed_flag in self.stuck_travel if routed_flag)
                cost += (
                    f" | stuck: {never}/{len(travelled)} never moved at all, "
                    f"furthest got {max(travelled):.1f} m, {routed} had a route"
                )
                self.stuck_travel.clear()
            if self.stalled_models:
                worst = sorted(self.stalled_models.items(), key=lambda kv: -kv[1])[:3]
                cost += " | stalled: " + ", ".join(f"{m}x{n}" for m, n in worst)
                self.stalled_models.clear()
            self.logger.info(
                f"traffic: {len(self.actors)} alive ({entered} entered, "
                f"speed avg {avg:.1f} max {mx:.1f} m/s) | despawns/{self.SUMMARY_S:.0f}s: "
                f"{reasons}{routes}{cost}"
            )
            self.despawns.clear()

    def count(self) -> int:
        return len(self.actors)
