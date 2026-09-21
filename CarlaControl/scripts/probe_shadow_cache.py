#!/usr/bin/env python3
"""Measure what invalidating the directional shadow cache every frame costs.

The capture plan freezes the sun on most of its windows and offers an advancing one. Setting the sun
does not stall the game thread, but this project renders with virtual shadow maps, whose directional
clipmap cache is invalidated whenever the light direction changes -- so an advancing sun re-renders
the directional shadow set uncached on every frame it moves, and a frozen one does not. Epic exposes
`r.Shadow.Virtual.Cache.ForceInvalidateDirectional` to force exactly that state, which makes the cost
measurable without moving the sun at all.

Two arms in one session, alternated, sun frozen in both, everything else identical. What differs is
one cvar.

**The console path is verified rather than assumed.** `console_command` reaches the engine through
`GetPlayerController`, which returns nothing in an editor server, and the batch response carries the
RPC's own success rather than the command's, so a cvar that was never applied is indistinguishable
from one that cost nothing. A third arm therefore turns shadows off with `r.ShadowQuality 0`, and
every arm reports the mean level of a captured frame beside its tick cost. Removing the casters'
shadows brightens the frame, so that arm establishes both that a cvar sent this way reaches the
renderer the sensor draws through and that the scene had shadows to lose -- which is what makes a
null result in the first two arms a reading instead of a silence. `r.Shadow.Virtual.Enable 0` is not
that control: it changes how a shadow is rendered rather than whether there is one, and its frame is
the same either way.

**Shadow casters have to be put there.** A generated world is photogrammetry over a road mesh: its
buildings and vegetation are in the imagery rather than in the scene, so their shadows are already in
the pixels and no light direction moves them. With no vehicles standing in the footprint the
directional shadow set is empty and the measurement is of nothing.

Examples:
    python probe_shadow_cache.py --repeats 3
    python probe_shadow_cache.py --delivered-world Arapahoe_I25 --ticks 400
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
    CameraFootprint,
    KinematicActorLattice,
)
from carlacontrol.ProbeCameraPair import ProbeCameraPair  # noqa: E402

FEET_PER_METRE = 3.28084

INVALIDATE_CVAR = "r.Shadow.Virtual.Cache.ForceInvalidateDirectional"
SHADOW_QUALITY_CVAR = "r.ShadowQuality"
SHADOW_QUALITY_DEFAULT = 5

ARM_CACHE_KEPT = "directional cache kept"
ARM_CACHE_INVALIDATED = "directional cache invalidated"
ARM_SHADOWS_OFF = "shadows off (control)"

ARMS = (
    (ARM_CACHE_KEPT, ((INVALIDATE_CVAR, 0), (SHADOW_QUALITY_CVAR, SHADOW_QUALITY_DEFAULT))),
    (ARM_CACHE_INVALIDATED, ((INVALIDATE_CVAR, 1), (SHADOW_QUALITY_CVAR, SHADOW_QUALITY_DEFAULT))),
    (ARM_SHADOWS_OFF, ((INVALIDATE_CVAR, 0), (SHADOW_QUALITY_CVAR, 0))),
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=2000)
    parser.add_argument("--timeout", type=float, default=180.0)
    parser.add_argument("--delivered-world", help="load this delivered world before measuring")
    parser.add_argument("--map", help="load this map before measuring")
    parser.add_argument("--ticks", type=int, default=300)
    parser.add_argument("--warmup-ticks", type=int, default=60)
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--width", type=int, default=1920)
    parser.add_argument("--height", type=int, default=1080)
    parser.add_argument("--fov", type=float, default=90.0)
    parser.add_argument("--altitude", type=float, default=1000.0)
    parser.add_argument("--fixed-delta", type=float, default=0.05)
    parser.add_argument("--solar-time", type=float, default=17.0,
                        help="the frozen sun's clock. A low sun casts the longest shadows and is "
                             "where the cache has the most to do")
    parser.add_argument("--solar-date", nargs=3, type=int, default=[2026, 3, 21],
                        metavar=("YEAR", "MONTH", "DAY"))
    parser.add_argument("--utc-offset", type=float, default=-7.0)
    parser.add_argument("--actors", type=int, default=128,
                        help="shadow casters to stand in the camera's footprint. A generated world "
                             "carries no buildings and no vegetation, so with none of these the "
                             "directional shadow set is empty and the cache has nothing to lose")
    parser.add_argument("--clearance", type=float, default=25.0,
                        help="metres above the highest road surface to place the casters")
    return parser.parse_args()


def set_cvars(client, settings: tuple[tuple[str, int], ...]) -> None:
    client.apply_batch_sync([carlanet.command.ConsoleCommand(f"{name} {value}")
                             for name, value in settings])


def main() -> int:
    args = parse_args()
    logging.basicConfig(level=logging.INFO, format="%(message)s")
    logger = logging.getLogger("probe_shadow_cache")

    client = carlanet.Client(args.host, args.port)
    client.set_timeout(args.timeout)
    if args.delivered_world:
        client.load_delivered_world(args.delivered_world)
    elif args.map:
        client.load_world(args.map)
    world = client.get_world()

    year, month, day = args.solar_date
    frozen = world.set_solar_epoch(year, month, day, args.solar_time, args.utc_offset)
    state = world.get_solar_state()
    if not frozen or state is None:
        logger.error("the world has no sun to freeze, so an advancing one cannot be priced here")
        return 2
    if state["advancing"]:
        logger.error("the sun is advancing; both arms must hold it still")
        return 2
    logger.info("map %s; sun frozen at %04d-%02d-%02d %05.2f UTC%+.2f, elevation %.2f deg",
                world.get_map().name, state["year"], state["month"], state["day"],
                state["solar_time"], state["time_zone"], state["sun_elevation_deg"])

    meter = ClockRatioMeter(world, args.fixed_delta, logger=logger)
    original = world.get_settings()
    meter.configure_synchronous()
    # sensor_tick is left unset on purpose: the shadow set is re-rendered on the frames the camera
    # renders, so a camera skipping nine frames in ten would dilute the effect being measured by ten.
    pair = ProbeCameraPair(world, args.width, args.height, args.fov, args.altitude, sensor_tick=0.0,
                           logger=logger)
    altitude_m = args.altitude / FEET_PER_METRE
    heights = [point.location.z for point in world.get_map().get_spawn_points()
               if abs(point.location.x) < altitude_m and abs(point.location.y) < altitude_m]
    caster_z = (max(heights) if heights else 0.0) + args.clearance
    lattice = KinematicActorLattice(
        world, args.actors,
        CameraFootprint.for_camera(altitude_m - caster_z, args.fov, args.width, args.height),
        caster_z, logger=logger)
    lattice.spawn(client)
    logger.info("%s", ClockRatioMeter.header())

    results: dict[str, list] = {label: [] for label, _ in ARMS}
    levels: dict[str, list] = {label: [] for label, _ in ARMS}
    try:
        meter.warm_up(args.warmup_ticks)
        for repeat in range(args.repeats):
            for label, cvars in ARMS:
                set_cvars(client, cvars)
                meter.warm_up(args.warmup_ticks)
                pair.request_brightness()
                results[label].append(meter.measure(f"{label}  (repeat {repeat + 1})", args.ticks))
                if pair.last_rgb_mean_level is not None:
                    levels[label].append(pair.last_rgb_mean_level)
    finally:
        set_cvars(client, ((INVALIDATE_CVAR, 0), (SHADOW_QUALITY_CVAR, SHADOW_QUALITY_DEFAULT)))
        pair.close()
        lattice.destroy(client)
        world.apply_settings(original)

    logger.info("")
    logger.info("%-32s%10s%10s%12s%12s%14s", "arm", "t/ws", "ms/tick", "spread", "of kept",
                "frame level")
    kept = statistics.fmean(sample.ticks_per_wall_second for sample in results[ARM_CACHE_KEPT])
    for label, samples in results.items():
        rate = statistics.fmean(sample.ticks_per_wall_second for sample in samples)
        spread = (max(sample.ticks_per_wall_second for sample in samples)
                  - min(sample.ticks_per_wall_second for sample in samples))
        level = statistics.fmean(levels[label]) if levels[label] else float("nan")
        logger.info("%-32s%10.2f%10.2f%12.2f%11.1f%%%14.3f", label, rate, 1000.0 / rate, spread,
                    100 * rate / kept, level)

    invalidated = statistics.fmean(
        sample.ticks_per_wall_second for sample in results[ARM_CACHE_INVALIDATED])
    shadows_off = statistics.fmean(
        sample.ticks_per_wall_second for sample in results[ARM_SHADOWS_OFF])
    level_shift = (statistics.fmean(levels[ARM_SHADOWS_OFF])
                   - statistics.fmean(levels[ARM_CACHE_KEPT])
                   if levels[ARM_SHADOWS_OFF] and levels[ARM_CACHE_KEPT] else float("nan"))
    logger.info("")
    logger.info("invalidating the directional cache every frame costs %+.2f ms per tick "
                "(%.2f -> %.2f ms)", 1000.0 / invalidated - 1000.0 / kept,
                1000.0 / kept, 1000.0 / invalidated)
    logger.info("the control: shadows off moves the tick by %+.2f ms and the frame's mean level by "
                "%+.3f. The level is what establishes that the cvar arrived and that the scene had "
                "shadows to lose; without it a null above would say nothing about the cache",
                1000.0 / shadows_off - 1000.0 / kept, level_shift)
    return 0


if __name__ == "__main__":
    sys.exit(main())
