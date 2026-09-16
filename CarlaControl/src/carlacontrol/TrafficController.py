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
    # Smallest opacity step worth a round-trip. The fade is a dithered dissolve driven by a single
    # custom-primitive float, so a change below one 8-bit step cannot be seen; sending it anyway
    # costs a blocking RPC that the server can only service inside its per-frame budget.
    FADE_EPSILON = 1.0 / 255.0

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
        # Per-site draw weights aligned with spawn_pool, or None for a uniform draw.
        # Filled in by create once the pool is final.
        self.spawn_weights = None
        # Posted speed limit per pool site, aligned with spawn_pool, and how many vehicles each
        # site has successfully placed. Together these answer whether the sites on the fastest
        # road are actually being drawn, which the stuck tally alone cannot show.
        self.spawn_limits = None
        self.spawn_counts = {}
        self.spawns_total = 0
        self.last_site_report = 0.0
        self.stuck_models = {}
        # Vehicles whose real footprint still pokes past the red edge once placed. The pool is
        # filtered with an assumed footprint and the nudge cannot help a lane that runs along the
        # edge, so whether a given vehicle actually landed wholly inside is a thing to measure.
        self.placed_outside = {}
        self.placed_outside_worst = 0.0
        # Seconds from creation to the first movement, for every vehicle that ever moved. The
        # question this answers is whether STUCK_S sits outside that distribution or inside it.
        # Did the traffic manager actually take the vehicle when it was handed over, and did it
        # still have it when the vehicle was given up on? Aggregate counts cannot answer either:
        # they show a standing gap without saying which vehicles it is made of.
        self.reg_at_spawn = [0, 0]      # [took it, did not]
        self.stuck_registered = [0, 0]  # [held at cull, not held]
        try:
            self.STUCK_S = float(getattr(args, "stuck_timeout", self.STUCK_S) or self.STUCK_S)
        except Exception:
            pass
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
        # Cumulative count of stuck despawns per spawn site, keyed by rounded spawn position.
        # Never cleared: one five-second window holds too few to tell a bad site from bad luck,
        # so the offenders only stand out once the counts accumulate over a run.
        self.stuck_sites = {}
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
        # Destinations already proven not to route from an entry. Without this every
        # spawn re-tests the same failures, which is what made a cap on attempts
        # necessary in the first place.
        self.unreachable_from = {}

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
        """Build the controller and compute the spawn pool.

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

        # A site belongs to the entry ring if the ring holds either the spawn point itself or the
        # pose the vehicle will actually occupy once nudged forward off the edge. Accepting either
        # keeps every site the raw test already accepted and adds back the lanes whose entry the
        # map edge happens to cut: a multi-lane carriageway meeting the boundary at an angle fans
        # its lane centres across that boundary, so the lanes nearest the road's reference line
        # fall outside it while the rest of the same road lies well inside.
        ring_sps = []
        for sp in world.get_map().get_spawn_points():
            if not cls.is_inward(sp, staging):
                continue
            sx, sy = cls.staged_xy(
                sp.location.x, sp.location.y, sp.rotation.yaw, cls._ASSUMED_EXTENT, staging
            )
            if cls.in_ring(sp.location.x, sp.location.y, staging) or cls.in_ring(sx, sy, staging):
                ring_sps.append(sp)
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

        lane_sites, spawn_pool = cls.ring_lane_sites(tm, staging, args, logger, spawn_pool)
        spawn_pool = spawn_pool + lane_sites
        if lane_sites:
            logger.info(
                f"spawn pool: {len(spawn_pool)} sites "
                f"({len(spawn_pool) - len(lane_sites)} road entries + {len(lane_sites)} along lanes)"
            )

        ctl = cls(world, tm, args, staging, blueprints, ring_sps, spawn_pool, floor_z)
        ctl.spawn_weights, ctl.spawn_limits = cls.speed_limit_weights(
            tm, spawn_pool, args, logger
        )
        # Only a run that asked for the fade needs the server to support it. Traffic itself does
        # not, so an unavailable set_actor_fade is no longer a reason to refuse to run at all.
        if args.fade:
            if not ctl.fade_selftest():
                ctl.available = False
                ctl.reason = (
                    "--fade given but set_actor_fade is not available — rebuild server + wheel "
                    "(BuildCarla.ps1 -Vs 2026 -InstallWheel), or drop --fade"
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
            f"{len(spawn_pool)} usable in-margin spawn points"
        )

        try:
            tm.set_traffic_diagnostics(args.traffic_diagnostics)
            if args.traffic_diagnostics:
                logger.info("traffic: per-vehicle diagnostics ON (']' toggles)")
        except Exception as e:
            logger.warning("traffic: diagnostics switch unavailable (%r)", e)
        if args.log:
            # The traffic manager writes its event lines itself, through its own handle on the
            # file. Pointing it at the path the Python logger already holds open gives two writers
            # each tracking their own offset into one file, so they overwrite one another's bytes
            # and the log ends up shorter than what was written to it. Give it a sibling file
            # instead: both sets of lines survive, and each is parseable.
            base, ext = os.path.splitext(os.path.abspath(args.log))
            tm_log = f"{base}.trafficmanager{ext or '.txt'}"
            try:
                tm.set_event_log_path(tm_log)
                logger.info(f"traffic: traffic-manager event lines -> {tm_log}")
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

    @staticmethod
    def safe_fade(v, hide) -> bool:
        """Push an opacity to the server. Returns whether it landed, so a caller tracking the
        last value it set does not record one the server never received."""
        try:
            v.set_fade(hide)
            return True
        except Exception:
            return False

    def apply_fade(self, rec, v, hide: float) -> None:
        """Set this vehicle's staging opacity, but only when it actually changed.

        Every call is a blocking round-trip whose handler walks each primitive component on the
        vehicle, and a vehicle anywhere inside the interior sits at exactly 0.0 for its whole
        life. Re-sending that on every reconcile was the heaviest single load this client put on
        the server, so a round-trip is spent only on a step the dissolve can actually show --
        plus the two endpoints, which are always sent exactly rather than approached to within
        FADE_EPSILON and left there."""
        last = rec.get("fade")
        if last is not None:
            if hide == last:
                return
            if hide not in (0.0, 1.0) and abs(hide - last) < self.FADE_EPSILON:
                return
        if self.safe_fade(v, hide):
            rec["fade"] = hide

    # Height CARLA raises its own spawn points to above the carriageway
    # (AOpenDriveGenerator::SpawnersHeight, 300 cm). Lane sites are raised to match so a vehicle
    # drops onto the road the same way from either kind of site.
    _SPAWN_HEIGHT_M = 3.0

    # Two spawn sites closer together than this are the same place, and keeping both would just
    # double the draw weight of that spot. About one vehicle length: far enough that a road entry
    # sitting on top of a lane site is dropped, close enough that an entry whose lane the dense
    # graph does not cover is kept rather than silently lost.
    _SITE_DUPLICATE_M = 6.0

    # A footprint is only "over the edge" once it is past it by more than this. The fit test lands
    # a nudged vehicle exactly on the boundary, so without a tolerance every such placement reports
    # a rounding crumb as an overhang.
    OVERHANG_TOL = 0.02

    # How long a newly created vehicle may sit motionless before it is given up on. Named rather
    # than buried so it can be read against the measured time-to-first-movement below: a cull that
    # falls inside the distribution of legitimate starts removes vehicles that were about to drive.
    STUCK_S = 6.0  # default; --stuck-timeout overrides it per run

    @classmethod
    def ring_lane_sites(cls, tm, staging, args, logger, entry_sites):
        """Spawn sites spread along every drivable lane that passes through the staging ring.

        CARLA places one spawn point per lane at each road entry and none in between, so a road
        clipped by the sandbox boundary offers only the few metres at its entry however much of the
        same carriageway lies inside the ring. The Traffic Manager keeps a waypoint every few metres
        along every driving lane to plan routes with, so those sites already exist; this asks for the
        ones inside the band. Entry points within one spacing of a lane site are dropped, so the
        density stays even rather than doubling up where the two kinds meet.

        Returns (sites, kept_entry_sites).
        """
        spacing = float(getattr(args, "lane_spawn_spacing", 0.0) or 0.0)
        if spacing <= 0.0 or staging is None:
            return [], entry_sites
        try:
            raw = tm.get_ring_lane_spawn_points(
                staging["min_x"], staging["min_y"], staging["max_x"], staging["max_y"],
                staging["margin"], spacing, 0.0, cls._SPAWN_HEIGHT_M,
            )
        except Exception as e:
            logger.info(
                f"lane spawn sites unavailable ({e!r}); using road-entry points only. "
                "Rebuild the server and wheel to pick them up."
            )
            return [], entry_sites
        sites = [
            sp
            for sp in raw
            if cls.is_inward(sp, staging)
            and cls.inward_min(sp.location.x, sp.location.y, staging) <= -2.0
            and cls.fits_inside_red_edge(
                sp.location.x, sp.location.y, sp.rotation.yaw, cls._ASSUMED_EXTENT, staging
            )
        ]
        near = cls._SITE_DUPLICATE_M * cls._SITE_DUPLICATE_M
        kept = [
            e
            for e in entry_sites
            if not any(
                (e.location.x - s.location.x) ** 2 + (e.location.y - s.location.y) ** 2 < near
                for s in sites
            )
        ]
        logger.info(
            f"lane spawn sites: {len(raw)} in the ring, {len(sites)} usable after the inward and "
            f"fit tests, at {spacing:.0f} m spacing; {len(entry_sites) - len(kept)} of "
            f"{len(entry_sites)} road-entry points dropped as duplicates"
        )
        return sites, kept

    @staticmethod
    def speed_limit_weights(tm, spawn_pool, args, logger):
        """Draw weights for the spawn pool, one per site, biased toward the faster roads.

        CARLA offers one spawn point per lane at each road entry and none in between, so a map's
        site count follows how many streets it has rather than how much traffic they carry. On
        Arapahoe the six lanes of I-25 are six sites out of 204, and a uniform draw therefore puts
        under 3% of all traffic on the one road that should carry most of it.

        The posted speed limit separates a motorway from a residential street without needing the
        lane or road-class data the client cannot see, and the Traffic Manager already answers for
        it per location -- the same figure it governs the vehicle by once it is driving. Returns
        None for a uniform draw.
        """
        bias = float(getattr(args, "speed_bias", 0.0) or 0.0)
        if bias <= 0.0 or not spawn_pool:
            return None, None
        limits = []
        for sp in spawn_pool:
            try:
                limits.append(float(tm.get_speed_limit_kph_at(sp.location)))
            except Exception:
                limits.append(0.0)
        known = sorted(v for v in limits if v > 0.0)
        if not known:
            logger.info("no spawn point reports a speed limit; spawn draw stays uniform")
            return None, None
        median = known[len(known) // 2]
        # A site whose road declares no limit is treated as typical rather than as slow, so an
        # unposted road is neither favoured nor starved by the absence of data.
        limits = [v if v > 0.0 else median for v in limits]
        weights = [max(v / median, 0.05) ** bias for v in limits]
        total = sum(weights)
        fastest = max(limits)
        fast = [i for i, v in enumerate(limits) if v >= fastest - 1.0]
        share = sum(weights[i] for i in fast) / total
        logger.info(
            f"spawn draw weighted by speed limit (--speed-bias {bias:g}): "
            f"{known[0]:.0f}..{known[-1]:.0f} km/h across {len(spawn_pool)} sites, "
            f"median {median:.0f}; the {len(fast)} site(s) at {fastest:.0f} km/h now draw "
            f"{100.0 * share:.1f}% of spawns against {100.0 * len(fast) / len(spawn_pool):.1f}% "
            f"when drawn uniformly"
        )
        return weights, limits

    @staticmethod
    def red_edge_deficit(x, y, yaw_deg, ext, b, pad=0.6):
        """How far a vehicle footprint pokes past the nearest red edge, plus that edge's inward normal."""
        edges = (
            ("W", x - b["min_x"], (1.0, 0.0)),
            ("E", b["max_x"] - x, (-1.0, 0.0)),
            ("S", y - b["min_y"], (0.0, 1.0)),
            ("N", b["max_y"] - y, (0.0, -1.0)),
        )
        _, clearance, normal = min(edges, key=lambda e: e[1])
        yaw = math.radians(yaw_deg)
        half_extent = (
            (abs(ext[0] * math.cos(yaw)) + abs(ext[1] * math.sin(yaw)))
            if normal[0] != 0.0
            else (abs(ext[0] * math.sin(yaw)) + abs(ext[1] * math.cos(yaw)))
        )
        return (half_extent + pad) - clearance, normal

    @classmethod
    def fits_inside_red_edge(cls, x, y, yaw_deg, ext, b, pad=0.6):
        """True if a vehicle spawned here can be nudged forward to lie wholly inside the red edge."""
        deficit, normal = cls.red_edge_deficit(x, y, yaw_deg, ext, b, pad)
        if deficit <= 0:
            return True
        yaw = math.radians(yaw_deg)
        ux, uy = math.cos(yaw), math.sin(yaw)
        dot = ux * normal[0] + uy * normal[1]
        if dot < 0.2:
            return False
        distance = min(deficit / dot, b["margin"])
        nx = x + ux * distance
        ny = y + uy * distance
        residual, _ = cls.red_edge_deficit(nx, ny, yaw_deg, ext, b, pad)
        return residual <= 1e-3

    @classmethod
    def staged_xy(cls, x, y, yaw_deg, ext, b, pad=0.6):
        """Where a vehicle spawned at this point actually comes to rest: the spawn point plus the
        forward nudge applied before it is placed. Ring membership is decided on this rather than
        on the raw spawn point, because CARLA puts a spawn point at the upstream end of each lane,
        and a map clipped mid-road can leave that end a metre or two OUTSIDE the sandbox while the
        lane it belongs to runs hundreds of metres inside."""
        deficit, normal = cls.red_edge_deficit(x, y, yaw_deg, ext, b, pad)
        if deficit <= 0:
            return (x, y)
        yaw = math.radians(yaw_deg)
        ux, uy = math.cos(yaw), math.sin(yaw)
        dot = ux * normal[0] + uy * normal[1]
        if dot < 0.2:
            return (x, y)
        distance = min(deficit / dot, b["margin"])
        return (x + ux * distance, y + uy * distance)

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
        dead = self.unreachable_from.setdefault(entry, set())
        known_keys = {self._place_key(sp.location) for sp in known}

        fresh = [
            sp
            for sp in self.destination_candidates(spawn_tf)
            if self._place_key(sp.location) not in known_keys
            and self._place_key(sp.location) not in dead
        ]
        far = fresh[: max(1, len(fresh) // 2)]
        random.shuffle(far)
        fresh = far + fresh[len(far) :]

        exploring = not known or random.random() < self._DESTINATION_EXPLORE_CHANCE
        remembered = random.sample(known, len(known))
        order = fresh + remembered if exploring else remembered + fresh

        # A same-side exit is considered only once every crossing has been proven not to
        # route -- proven, not merely tried this once, which is what remembering the
        # failures buys. Until then the crossings are the only candidates.
        crossing = [sp for sp in order if not self.same_side(spawn_tf, sp)]
        same_side = [sp for sp in order if self.same_side(spawn_tf, sp)]
        if crossing:
            candidates = crossing
        elif same_side and self.same_side_allowed():
            candidates = same_side
        else:
            candidates = []
            if same_side and entry not in self.same_side_refused:
                self.same_side_refused.add(entry)
                self.logger.warning(
                    "the entry at (%.0f, %.0f) can only reach destinations on the %s side "
                    "it came in on, and --same-side-exit-rate is 0, so it spawns nothing. "
                    "Give the rate a non-zero value to let traffic double back here.",
                    spawn_tf.location.x,
                    spawn_tf.location.y,
                    self.edge_of(spawn_tf.location.x, spawn_tf.location.y, self.b),
                )

        deadline = time.perf_counter() + self._ROUTE_SEARCH_BUDGET_MS / 1000.0
        for destination_tf in candidates:
            if time.perf_counter() >= deadline:
                break
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
            if route is None:
                dead.add(self._place_key(destination_tf.location))
                continue
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
        sx, sy = self.staged_xy(loc.x, loc.y, yaw_deg, ext, self.b, pad)
        return (sx - loc.x, sy - loc.y)

    _NEW_VEHICLE_RADIUS = 3.7
    _SPAWN_CLEAR_PAD = 1.0
    _ASSUMED_EXTENT = (3.5, 1.1)
    _ROUTE_DESTINATION_TRIES = 4
    # Destinations beyond the entry ring to try when no ring point can be reached. A
    # sparse network needs this: the ring is where traffic should ideally end, not the
    # only place it can.
    # How long one spawn may spend searching for a route before giving up and letting the
    # next frame carry on. A search costs about 10 ms on the networks measured and the
    # synchronous tick is 50 ms at 20 fps, so this is roughly two searches: enough to
    # place a vehicle whose destination is already known, small enough not to cost a
    # frame. Nothing is lost by stopping, because both the successes and the failures are
    # remembered -- an entry works through its candidates across successive spawns rather
    # than re-testing them, so the cost of a cold entry is paid once and not per spawn.
    _ROUTE_SEARCH_BUDGET_MS = 25.0
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
        if self.spawn_weights:
            # A weighted random ORDER, not a weighted pick: scale one exponential draw per site
            # by that site's weight and visit the smallest first. That biases which site is tried
            # first without ever excluding one, so the walk below still falls through to a slower
            # road when the fast ones are occupied, unroutable or already taken.
            order = sorted(
                range(len(self.spawn_pool)),
                key=lambda i: random.expovariate(1.0) / self.spawn_weights[i],
            )
        else:
            order = list(range(len(self.spawn_pool)))
            random.shuffle(order)
        for site_index in order:
            sp = self.spawn_pool[site_index]
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
            # Hide it before it can be drawn. Reading the bounding box, placing the vehicle and
            # setting its real opacity below each cost a server round-trip, and the world keeps
            # ticking on the main thread while those are in flight -- so a vehicle left visible
            # here renders fully opaque for those frames, at the un-nudged spawn point and still
            # hovering above the carriageway. One round-trip now replaces three of flicker.
            if self.args.fade:
                self.safe_fade(v, 1.0)
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
                spawn_hide = None
                if self.args.fade and self.safe_fade(v, 1.0 - op):
                    spawn_hide = 1.0 - op
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
                try:
                    self.reg_at_spawn[0 if self.tm.is_vehicle_registered(v.id) else 1] += 1
                except Exception:
                    pass
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
                # Last opacity the server acknowledged, so the reconcile can tell a real change
                # from the same value being sent again. None until one has landed.
                "fade": spawn_hide,
            }
            overhang, _ = self.red_edge_deficit(sx, sy, syaw, ext, self.b)
            if overhang > self.OVERHANG_TOL:
                model = str(getattr(bp, "id", "?")).split(".", 1)[-1]
                self.placed_outside[model] = self.placed_outside.get(model, 0) + 1
                self.placed_outside_worst = max(self.placed_outside_worst, overhang)
            self.spawn_counts[site_index] = self.spawn_counts.get(site_index, 0) + 1
            self.spawns_total += 1
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
                self.stuck_travel.append(
                    (math.hypot(cx - sx, cy - sy), rec.get("routed", False), (sx, sy))
                )
                site = (round(sx), round(sy))
                self.stuck_sites[site] = self.stuck_sites.get(site, 0) + 1
                model = rec.get("bp", "?").split(".", 1)[-1]
                self.stuck_models[model] = self.stuck_models.get(model, 0) + 1
                try:
                    self.stuck_registered[0 if self.tm.is_vehicle_registered(vid) else 1] += 1
                except Exception:
                    pass
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
            self.unreachable_from.clear()
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
            # Which of ours are still alive, straight off the world-observer snapshot. Asking the
            # server instead would be a blocking RPC returning a full description and bounding box
            # for every vehicle, every 0.1 s, when all that is wanted is the set of ids.
            try:
                snapshot = self.world.get_actor_ids()
            except Exception as e:
                self.logger.debug(f"get_actor_ids failed: {e}")
                snapshot = None
            if not snapshot:
                # No snapshot yet (or the read failed) is not evidence of death — the observer
                # simply has not spoken. Skip the culling this pass rather than charge every
                # tracked vehicle with a miss.
                live_ids = set(ids)
            else:
                live_ids = {vid for vid in ids if vid in snapshot}
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
            rec["yaw"] = yaw
            armed = (now - rec["born"]) >= self.SPAWN_GRACE

            xy = rec["xy"]
            rec["xy"] = (loc.x, loc.y)
            dist = math.hypot(loc.x - xy[0], loc.y - xy[1]) if xy is not None else 0.0
            rec["speed"] = dist / self.CHECK_S

            op = self.interior_opacity(loc.x, loc.y, yaw, rec["ext"][0], rec["ext"][1], b)
            if self.args.fade:
                self.apply_fade(rec, a, 1.0 - op)

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
                    if rec["stuck"] >= self.STUCK_S:
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
            # Everything below the pre-existing stuck/stalled counts is diagnostic: it was added
            # to find out why spawned vehicles were not being driven, and it answered that. Kept
            # because the same questions recur, but printed only when asked for -- ']' toggles it
            # at runtime, so read the live setting rather than the flag the run started with.
            try:
                diag = bool(self.tm.get_traffic_diagnostics())
            except Exception:
                diag = bool(getattr(self.args, "traffic_diagnostics", False))

            if self.stuck_travel:
                travelled = [d for d, _, _ in self.stuck_travel]
                never = sum(1 for d in travelled if d < 1.0)
                routed = sum(1 for _, routed_flag, _ in self.stuck_travel if routed_flag)
                cost += (
                    f" | stuck: {never}/{len(travelled)} never moved at all, "
                    f"furthest got {max(travelled):.1f} m, {routed} had a route"
                )
                if diag:
                    # Sites the widened ring gate admitted (spawn point outside the sandbox, so red
                    # clearance is negative) against the ones the narrower gate already accepted.
                    clear = [self.red_clearance(sx, sy, self.b)
                             for _, _, (sx, sy) in self.stuck_travel]
                    outside = sum(1 for c in clear if c < 0.0)
                    cost += (
                        f", {outside} outside the edge "
                        f"(red clearance {min(clear):.1f}..{max(clear):.1f} m)"
                    )
                self.stuck_travel.clear()
            if self.stalled_models:
                worst = sorted(self.stalled_models.items(), key=lambda kv: -kv[1])[:3]
                cost += " | stalled: " + ", ".join(f"{m}x{n}" for m, n in worst)
                self.stalled_models.clear()

            if diag:
                if self.stuck_sites:
                    worst = sorted(self.stuck_sites.items(), key=lambda kv: -kv[1])[:4]
                    cost += (
                        f" | worst stuck sites ({len(self.stuck_sites)} distinct): "
                        + ", ".join(
                            f"({x},{y}) rc{self.red_clearance(x, y, self.b):.0f}m x{n}"
                            for (x, y), n in worst
                        )
                    )
                if self.stuck_models:
                    # An asset-specific pattern here would point at the blueprint rather than at
                    # the placement or the traffic manager.
                    worst = sorted(self.stuck_models.items(), key=lambda kv: -kv[1])[:3]
                    cost += (
                        f" | stuck models ({len(self.stuck_models)} distinct): "
                        + ", ".join(f"{m}x{n}" for m, n in worst)
                    )
                if self.placed_outside:
                    n = sum(self.placed_outside.values())
                    worst = sorted(self.placed_outside.items(), key=lambda kv: -kv[1])[:3]
                    cost += (
                        f" | placed poking out: {n}/{self.spawns_total} spawns, worst "
                        f"{self.placed_outside_worst:.2f} m: "
                        + ", ".join(f"{m}x{c}" for m, c in worst)
                    )
                outside_now = 0
                for r in self.actors.values():
                    if r["xy"] is None or "yaw" not in r:
                        continue
                    if self.red_edge_deficit(r["xy"][0], r["xy"][1], r["yaw"], r["ext"],
                                             self.b)[0] > self.OVERHANG_TOL:
                        outside_now += 1
                if outside_now:
                    cost += f" | {outside_now}/{len(self.actors)} alive are over the edge right now"
                took, missed = self.reg_at_spawn
                if took or missed:
                    cost += f" | TM took {took}/{took + missed} at spawn"
                held, lost = self.stuck_registered
                if held or lost:
                    cost += f", still held at cull {held}/{held + lost}"
                # What the traffic manager itself holds. A count below the number alive says the
                # vehicles never reached it, which nothing measured at the spawn site would show.
                try:
                    routed_now = self.tm.get_routed_vehicle_count()
                    try:
                        cost += (f" | TM holds {self.tm.get_registered_vehicle_count()} of "
                                 f"{len(self.actors)}, routing {routed_now}")
                    except Exception:
                        cost += f" | TM routing {routed_now}"
                except Exception:
                    pass
                if self.spawn_limits and self.spawns_total:
                    fastest = max(self.spawn_limits)
                    fast = [i for i, v in enumerate(self.spawn_limits) if v >= fastest - 1.0]
                    used = sum(1 for i in fast if self.spawn_counts.get(i))
                    drawn = sum(self.spawn_counts.get(i, 0) for i in fast)
                    cost += (
                        f" | fast sites: {used}/{len(fast)} used, "
                        f"{drawn}/{self.spawns_total} spawns "
                        f"({100.0 * drawn / self.spawns_total:.0f}%)"
                    )
                    if now - self.last_site_report >= 30.0:
                        self.last_site_report = now
                        self.logger.info(
                            f"fast-road spawn sites ({fastest:.0f} km/h): "
                            + " ".join(
                                f"({self.spawn_pool[i].location.x:.0f},"
                                f"{self.spawn_pool[i].location.y:.0f})"
                                f"x{self.spawn_counts.get(i, 0)}"
                                for i in fast
                            )
                        )
            self.logger.info(
                f"traffic: {len(self.actors)} alive ({entered} entered, "
                f"speed avg {avg:.1f} max {mx:.1f} m/s) | despawns/{self.SUMMARY_S:.0f}s: "
                f"{reasons}{routes}{cost}"
            )
            self.despawns.clear()

    def count(self) -> int:
        return len(self.actors)
