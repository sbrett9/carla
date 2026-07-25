"""Boundary-aware staging traffic controller for CARLA.

Manages spawning, despawning, and opacity fading of traffic vehicles within
a defined staging area with margin-based entry/exit zones.
"""

from __future__ import annotations

import logging
import math
import random

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

    def __init__(self, world: carla.World, tm, args, staging: dict | None, blueprints: list, ring_sps: list, spawn_pool: list, floor_z: float):
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
        
        if staging is not None:
            self.logger.info(f"traffic controller initialized: {len(blueprints)} blueprints, {len(spawn_pool)} spawn points")

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
            stub.reason = "no staging bounds (build a draped world: --height-align drape)"
            return stub
        
        logger.info(f"staging bounds retrieved: {staging['max_x'] - staging['min_x']:.0f}x{staging['max_y'] - staging['min_y']:.0f}m, margin={staging['margin']:.0f}m")

        bp_lib = world.get_blueprint_library()
        blueprints = list(bp_lib.filter(args.filter))
        logger.info(f"filtered {len(blueprints)} vehicle blueprints matching '{args.filter}'")
        cars = [b for b in blueprints if not cls.is_two_wheeled(b)]
        if cars:
            blueprints = cars
            logger.info(f"excluded two-wheeled vehicles: {len(blueprints)} car blueprints remaining")
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
            and cls.red_clearance(sp.location.x, sp.location.y, staging) >= 5.0
        ]
        if len(spawn_pool) < 8:
            logger.info(f"spawn pool too small ({len(spawn_pool)}), using all {len(ring_sps)} ring spawn points")
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

        if args.start_traffic:
            ctl.want_enabled = True
        
        logger.info(f"traffic controller ready: {len(blueprints)} blueprints, {len(spawn_pool)} spawn points, max={args.max} vehicles")

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

    def pick_destination(self, spawn_tf):
        s_edge = self.edge_of(spawn_tf.location.x, spawn_tf.location.y, self.b)
        cands = [
            sp
            for sp in self.ring_sps
            if self.edge_of(sp.location.x, sp.location.y, self.b) != s_edge
        ] or self.ring_sps
        cands.sort(key=lambda sp: -spawn_tf.location.distance(sp.location))
        return random.choice(cands[: max(1, len(cands) // 2)])

    @staticmethod
    def safe_fade(v, hide):
        try:
            v.set_fade(hide)
        except Exception:
            pass

    def clear_shift(self, loc, yaw_deg, ext, pad=0.6):
        """Offset (dx, dy) to move a vehicle FORWARD along its lane just far enough that its whole
        footprint clears the nearest red (sandbox) edge."""
        b = self.b
        edges = (
            ("W", loc.x - b["min_x"], (1.0, 0.0)),
            ("E", b["max_x"] - loc.x, (-1.0, 0.0)),
            ("S", loc.y - b["min_y"], (0.0, 1.0)),
            ("N", b["max_y"] - loc.y, (0.0, -1.0)),
        )
        _, c, n = min(edges, key=lambda e: e[1])
        yaw = math.radians(yaw_deg)
        he = (
            (abs(ext[0] * math.cos(yaw)) + abs(ext[1] * math.sin(yaw)))
            if n[0] != 0.0
            else (abs(ext[0] * math.sin(yaw)) + abs(ext[1] * math.cos(yaw)))
        )
        deficit = (he + pad) - c
        if deficit <= 0:
            return (0.0, 0.0)
        ux, uy = math.cos(yaw), math.sin(yaw)
        dot = ux * n[0] + uy * n[1]
        if dot < 0.2:
            return (0.0, 0.0)
        d = min(deficit / dot, b["margin"])
        return (ux * d, uy * d)

    _NEW_VEHICLE_RADIUS = 3.7
    _SPAWN_CLEAR_PAD = 1.0

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
            ex, ey = self.clear_shift(sp.location, sp.rotation.yaw, (3.5, 1.1))
            if self.occupied(sp.location.x + ex, sp.location.y + ey):
                continue
            try:
                v = self.world.spawn_actor(self.spawn_bp(), sp)
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
                if self.args.route:
                    self.tm.set_path(v, [self.pick_destination(sp).location])
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
                "stuck": 0.0,
                "misses": 0,
                "speed": 0.0,
            }
            if len(self.actors) == 1:
                self.logger.info(f"first vehicle spawned: id={v.id}")
            return True
        return False

    def despawn(self, vid, actor, reason="other"):
        self.despawns[reason] = self.despawns.get(reason, 0) + 1
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
            self.spawn_one(now)
        if now - self.last_check < self.CHECK_S:
            return
        self.last_check = now

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
                if rec["entered"] or xy is None or dist > 0.05:
                    rec["stuck"] = 0.0
                else:
                    rec["stuck"] += self.CHECK_S
                    if rec["stuck"] >= 6.0:
                        self.despawn(vid, a, "stuck")
                        continue

        if now - self.last_summary >= self.SUMMARY_S:
            self.last_summary = now
            entered = sum(1 for r in self.actors.values() if r["entered"])
            speeds = [r["speed"] for r in self.actors.values()]
            avg = sum(speeds) / len(speeds) if speeds else 0.0
            mx = max(speeds) if speeds else 0.0
            reasons = " ".join(f"{k}={v}" for k, v in sorted(self.despawns.items())) or "none"
            self.logger.info(
                f"traffic: {len(self.actors)} alive ({entered} entered, "
                f"speed avg {avg:.1f} max {mx:.1f} m/s) | despawns/{self.SUMMARY_S:.0f}s: {reasons}"
            )
            self.despawns.clear()

    def count(self) -> int:
        return len(self.actors)
