#!/usr/bin/env python3
"""Sweep the rendered-actor count to find the ceiling, and price what a pose batch costs.

`render_cap` and `render_cap_hard` are the only numbers the capture envelope structurally depends on
that have never been measured: 100 concurrent rendered vehicles are demonstrated on this fork and
128 is a recommendation one step beyond it. This sweeps N from nothing to beyond the recommendation
and reads the clock ratio at each step.

**The rule is declared before the run, not chosen from the curve.**

    render_cap        the largest N whose tick rate is at least 90% of the N = 0 rate
    render_cap_hard   the largest N whose tick rate is at least 75% of it, with no RPC timeout and
                      with frames per tick unchanged from N = 0

Four arms at each N, which is what separates the two costs that could be setting the ceiling:

    render + batch          the shape a SUMO-driven capture has: bodies in frame, poses written
    render, no batch        the same bodies, left where they were spawned
    out of frame + batch    the same poses written on bodies outside the camera's footprint
    render + batch + lights a signal-mask change on the measured 7.5% of the set per tick

Fitting tick cost against N as `a + b * N` in the first and third arms gives two slopes, and their
difference is what the render costs per body in frame. If it is small the ceiling is set by the pose
write and more actors are not bought by a faster camera; if it is most of the slope, the camera sets
it and the `sensor_tick` saving is spendable on actors.

Examples:
    python probe_actor_ceiling.py --counts 0 32 64 128 256 512
    python probe_actor_ceiling.py --counts 0 128 256 --ticks 400 --width 1920 --height 1080
"""
import argparse
import logging
import statistics
import sys
from pathlib import Path

_THIS = Path(__file__).resolve().parent
_REPO = _THIS.parent.parent
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

import carlanet  # noqa: E402  (needs the path above)

from carlacontrol.ClockRatioMeter import ClockRatioMeter  # noqa: E402
from carlacontrol.KinematicActorLattice import (  # noqa: E402
    LIGHT_TRANSITION_FRACTION,
    CameraFootprint,
    KinematicActorLattice,
)
from carlacontrol.ProbeCameraPair import ProbeCameraPair  # noqa: E402

FEET_PER_METRE = 3.28084
RENDER_CAP_FRACTION = 0.90
RENDER_CAP_HARD_FRACTION = 0.75

ARM_RENDERED_BATCHED = "render + batch"
ARM_RENDERED_STATIC = "render, no batch"
ARM_OUT_OF_FRAME = "out of frame + batch"
ARM_RENDERED_LIGHTS = "render + batch + lights"
ARM_NO_RENDERING = "no rendering + batch"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=2000)
    parser.add_argument("--timeout", type=float, default=180.0)
    parser.add_argument("--delivered-world", help="load this delivered world before measuring")
    parser.add_argument("--counts", type=int, nargs="+", default=[0, 32, 64, 128, 256, 512],
                        help="actor counts to sweep; 0 is the baseline every fraction is against")
    parser.add_argument("--ticks", type=int, default=200, help="measured ticks per arm")
    parser.add_argument("--warmup-ticks", type=int, default=40)
    parser.add_argument("--width", type=int, default=1920)
    parser.add_argument("--height", type=int, default=1080)
    parser.add_argument("--fov", type=float, default=90.0)
    parser.add_argument("--altitude", type=float, default=1000.0, help="camera altitude in feet")
    parser.add_argument("--record-hz", type=float, default=2.0,
                        help="capture rate the camera pair is configured for")
    parser.add_argument("--fixed-delta", type=float, default=0.05)
    parser.add_argument("--lattice-z", type=float, default=None,
                        help="height to place the lattice at; defaults to the highest road surface "
                             "under the camera plus the clearance below")
    parser.add_argument("--clearance", type=float, default=25.0,
                        help="metres above the highest road surface under the camera to fly the "
                             "lattice. A body spawned inside the terrain or a building is refused, "
                             "and a sweep missing bodies is not a point on the curve")
    parser.add_argument("--arms", nargs="+",
                        default=[ARM_RENDERED_BATCHED, ARM_RENDERED_STATIC, ARM_OUT_OF_FRAME,
                                 ARM_RENDERED_LIGHTS],
                        help="which arms to run")
    return parser.parse_args()


def highest_road_surface(world, radius: float) -> float:
    """The top of the map's road surface near the camera, from the spawn points it recommends."""
    heights = [point.location.z for point in world.get_map().get_spawn_points()
               if abs(point.location.x) < radius and abs(point.location.y) < radius]
    if not heights:
        heights = [point.location.z for point in world.get_map().get_spawn_points()]
    return max(heights) if heights else 0.0


def fit_line(points: list[tuple[float, float]]) -> tuple[float, float]:
    """Least squares `a + b * x` over (x, y) pairs."""
    if len(points) < 2:
        return (points[0][1] if points else 0.0), 0.0
    mean_x = statistics.fmean(x for x, _ in points)
    mean_y = statistics.fmean(y for _, y in points)
    variance = sum((x - mean_x) ** 2 for x, _ in points)
    if not variance:
        return mean_y, 0.0
    slope = sum((x - mean_x) * (y - mean_y) for x, y in points) / variance
    return mean_y - slope * mean_x, slope


def run_arm(client, world, meter, args, arm: str, count: int, footprint: CameraFootprint,
            ground_z: float, logger: logging.Logger):
    """One (arm, N) point: spawn, warm up, measure, tear down."""
    out_of_frame = arm == ARM_OUT_OF_FRAME
    # The control for the frustum: with rendering off entirely, nothing on screen can be paying for
    # a body, so a slope that survives is per-actor work the renderer never sees. It is the answer to
    # the objection that an editor viewport might be drawing the out-of-frame lattice.
    settings = world.get_settings()
    settings.no_rendering_mode = arm == ARM_NO_RENDERING
    world.apply_settings(settings)
    # Displaced by a footprint and a half along y, which puts every body clear of the frustum while
    # keeping the lattice the same shape, the same spacing and the same distance from the camera's
    # own streaming.
    centre_y = footprint.height * 1.5 if out_of_frame else 0.0
    lattice = KinematicActorLattice(world, count, footprint, ground_z, centre_y=centre_y,
                                    logger=logger)
    pair = ProbeCameraPair(world, args.width, args.height, args.fov, args.altitude,
                           sensor_tick=1.0 / args.record_hz, logger=logger)
    try:
        spawned = lattice.spawn(client)
        meter.warm_up(args.warmup_ticks)
        pair.reset_tallies()
        pair.request_brightness()

        if arm == ARM_RENDERED_STATIC:
            before_tick = None
        else:
            poses = lattice.build_pose_cycle()
            lights = ([[cmd.to_cs() for cmd in lattice.light_batch()] for _ in range(len(poses))]
                      if arm == ARM_RENDERED_LIGHTS else None)

            def before_tick(index):
                slot = index % len(poses)
                client.apply_batch(poses[slot] if lights is None else poses[slot] + lights[slot])

        sample = meter.measure(f"{arm:<24} N={count:<4}", args.ticks, before_tick=before_tick)
        logger.info("%s", pair.describe(args.ticks))
        return {"arm": arm, "count": count, "spawned": spawned, "sample": sample,
                "rgb_frames": pair.rgb_tally.frames, "paired": pair.paired_frames,
                "level": pair.last_rgb_mean_level}
    finally:
        pair.close()
        lattice.destroy(client)
        settings.no_rendering_mode = False
        world.apply_settings(settings)
        meter.warm_up(10)


def main() -> int:
    args = parse_args()
    logging.basicConfig(level=logging.INFO, format="%(message)s")
    logger = logging.getLogger("probe_actor_ceiling")

    client = carlanet.Client(args.host, args.port)
    client.set_timeout(args.timeout)
    if args.delivered_world:
        client.load_delivered_world(args.delivered_world)
    world = client.get_world()

    altitude_m = args.altitude / FEET_PER_METRE
    # The footprint is computed at the lattice's own height, not the ground's: a grid sized for the
    # ground plane and then flown above it has its outer rows outside the frustum, and those bodies
    # are culled rather than rendered.
    ground_z = (args.lattice_z if args.lattice_z is not None
                else highest_road_surface(
                    world, CameraFootprint.for_camera(
                        altitude_m, args.fov, args.width, args.height).width) + args.clearance)
    footprint = CameraFootprint.for_camera(altitude_m - ground_z, args.fov, args.width, args.height)
    logger.info("map %s; camera %d x %d fov %.0f at %.0f ft (z %.1f) covers %.0f x %.0f m at the "
                "lattice's z of %.1f", world.get_map().name, args.width, args.height, args.fov,
                args.altitude, altitude_m, footprint.width, footprint.height, ground_z)
    logger.info("render_cap at >= %.0f%% of the N = 0 tick rate, render_cap_hard at >= %.0f%%, "
                "declared before the run", RENDER_CAP_FRACTION * 100, RENDER_CAP_HARD_FRACTION * 100)

    meter = ClockRatioMeter(world, args.fixed_delta, logger=logger)
    original = world.get_settings()
    meter.configure_synchronous()
    logger.info("%s", ClockRatioMeter.header())

    rows = []
    try:
        for count in args.counts:
            arms = [ARM_RENDERED_BATCHED] if count == 0 else args.arms
            for arm in arms:
                rows.append(run_arm(client, world, meter, args, arm, count, footprint, ground_z,
                                    logger))
    finally:
        world.apply_settings(original)

    # Every N = 0 row, not the first: putting the baseline at both ends of the sweep and averaging
    # is what keeps a machine that drifts over the run from being read as an actor cost.
    baselines = [row for row in rows if row["count"] == 0]
    baseline_rate = statistics.fmean(row["sample"].ticks_per_wall_second for row in baselines)
    baseline_frames = max(row["rgb_frames"] for row in baselines)
    if len(baselines) > 1:
        spread = (max(row["sample"].ticks_per_wall_second for row in baselines)
                  - min(row["sample"].ticks_per_wall_second for row in baselines))
        logger.info("baseline measured %d times, %.2f t/ws mean, %.2f t/ws spread across the sweep",
                    len(baselines), baseline_rate, spread)

    logger.info("")
    logger.info("%-26s%6s%9s%10s%11s%11s%11s%12s", "arm", "N", "spawned", "t/ws", "ms/tick",
                "ms client", "of N=0", "rgb frames")
    for row in rows:
        sample = row["sample"]
        logger.info("%-26s%6d%9d%10.2f%11.2f%11.2f%10.1f%%%12d", row["arm"], row["count"],
                    row["spawned"], sample.ticks_per_wall_second, sample.mean_tick_ms,
                    sample.mean_before_ms,
                    100 * sample.ticks_per_wall_second / baseline_rate, row["rgb_frames"])

    logger.info("")
    for arm in (ARM_RENDERED_BATCHED, ARM_RENDERED_STATIC, ARM_OUT_OF_FRAME, ARM_RENDERED_LIGHTS,
                ARM_NO_RENDERING):
        points = [(float(row["count"]), row["sample"].mean_tick_ms)
                  for row in rows if row["arm"] == arm]
        if len(points) < 2:
            continue
        intercept, slope = fit_line(points)
        client_points = [(float(row["count"]), row["sample"].mean_before_ms)
                         for row in rows if row["arm"] == arm]
        client_intercept, client_slope = fit_line(client_points)
        logger.info("%-26s server tick = %.2f ms + %.4f ms per actor; client send = %.2f ms + "
                    "%.4f ms per actor", arm, intercept, slope, client_intercept, client_slope)

    in_frame = [(float(row["count"]), row["sample"].mean_tick_ms)
                for row in rows if row["arm"] == ARM_RENDERED_BATCHED]
    out_frame = [(float(row["count"]), row["sample"].mean_tick_ms)
                 for row in rows if row["arm"] == ARM_OUT_OF_FRAME]
    if len(in_frame) > 1 and len(out_frame) > 1:
        _, slope_in = fit_line(in_frame)
        _, slope_out = fit_line(out_frame)
        logger.info("rendering a body in frame costs %.4f ms per tick above writing its pose "
                    "out of frame (%.4f - %.4f)", slope_in - slope_out, slope_in, slope_out)

    batched = {row["count"]: row for row in rows if row["arm"] == ARM_RENDERED_BATCHED}
    lit = {row["count"]: row for row in rows if row["arm"] == ARM_RENDERED_LIGHTS}
    if lit:
        logger.info("")
        logger.info("a signal-mask change on %.1f%% of the set per tick costs:",
                    LIGHT_TRANSITION_FRACTION * 100)
        for count in sorted(lit):
            if count in batched:
                delta = lit[count]["sample"].mean_tick_ms - batched[count]["sample"].mean_tick_ms
                changing = max(1, round(LIGHT_TRANSITION_FRACTION * lit[count]["spawned"]))
                logger.info("   N=%-5d %+.3f ms per tick over %d transitions (%+.4f ms each)",
                            count, delta, changing, delta / changing if changing else 0.0)

    passing = [row for row in rows
               if row["arm"] == ARM_RENDERED_BATCHED and row["count"] > 0
               and row["sample"].ticks_per_wall_second >= RENDER_CAP_FRACTION * baseline_rate]
    hard = [row for row in rows
            if row["arm"] == ARM_RENDERED_BATCHED and row["count"] > 0
            and row["sample"].ticks_per_wall_second >= RENDER_CAP_HARD_FRACTION * baseline_rate
            and row["rgb_frames"] == baseline_frames]
    # Reported beside the declared rule, not instead of it. The rule is a fraction of an empty
    # world's tick rate; this is the count at which the capture stops keeping up with the clock it
    # is capturing, which is the threshold a window plan actually spends.
    real_time = [row for row in rows
                 if row["arm"] == ARM_RENDERED_BATCHED and row["count"] > 0
                 and row["sample"].clock_ratio >= 1.0]
    logger.info("")
    logger.info("largest N still at or above real time: %s",
                max(row["count"] for row in real_time) if real_time
                else "none of the counts swept")
    logger.info("render_cap      %s",
                max(row["count"] for row in passing) if passing
                else f"below the smallest N swept ({min(c for c in args.counts if c)})")
    logger.info("render_cap_hard %s",
                max(row["count"] for row in hard) if hard
                else f"below the smallest N swept ({min(c for c in args.counts if c)})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
