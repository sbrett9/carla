#!/usr/bin/env python3
"""Measure what `sensor_tick` is worth on a capture rig's clock ratio.

Every camera this pipeline has ever spawned leaves `sensor_tick` at its default of 0, which renders
and streams a frame on every world tick. A capture at 2 Hz against a 0.05 s step keeps one frame in
ten and discards nine. `sensor_tick` is documented as the simulation seconds between captures, and
the question the capture plan's whole wall-clock budget turns on is whether setting it removes the
render and the readout of the nine, or only their delivery.

Two arms, one variable. Same world, same camera pair, same resolution, same field of view, same
number of ticks; `sensor_tick` is 0 in one and `1 / record_hz` in the other. The measured quantity is
ticks per wall-second, and the arms are alternated so that a drift in the machine over the run is
visible as a spread between repeats of the same arm rather than mistaken for the effect.

Frames are counted in the listener and never waited for. With `sensor_tick` set, most ticks produce
no frame, so a getter that blocks until one arrives would charge the fast arm for the frames it was
told not to render. The frame numbers each camera reports carry the answer directly: the modal
spacing between deliveries is 1 when a camera renders every tick and `1 / (record_hz * step)` when it
does not.

Examples:
    python probe_sensor_tick.py --ticks 400
    python probe_sensor_tick.py --width 1920 --height 1080 --record-hz 2 --repeats 3
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
from carlacontrol.ProbeCameraPair import ProbeCameraPair  # noqa: E402


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=2000)
    parser.add_argument("--timeout", type=float, default=120.0)
    parser.add_argument("--delivered-world",
                        help="load this delivered world before measuring")
    parser.add_argument("--ticks", type=int, default=300, help="measured ticks per arm")
    parser.add_argument("--warmup-ticks", type=int, default=60,
                        help="ticks run and discarded after each camera pair is spawned, so shader "
                             "compilation and tile streaming are not in the sample")
    parser.add_argument("--repeats", type=int, default=2,
                        help="times to run each arm, alternating, so drift is visible")
    parser.add_argument("--width", type=int, default=1280)
    parser.add_argument("--height", type=int, default=720)
    parser.add_argument("--fov", type=float, default=90.0)
    parser.add_argument("--altitude", type=float, default=1000.0,
                        help="camera altitude in feet, looking straight down")
    parser.add_argument("--record-hz", type=float, default=2.0,
                        help="capture rate the set arm is configured for; sensor_tick is its "
                             "reciprocal")
    parser.add_argument("--fixed-delta", type=float, default=0.05,
                        help="synchronous simulation step")
    return parser.parse_args()


def run_arm(world, meter: ClockRatioMeter, args: argparse.Namespace, sensor_tick: float | None,
            label: str, logger: logging.Logger):
    """Spawn a camera pair, warm it up, measure, and take it away again.

    The pair is spawned and destroyed inside the arm rather than reconfigured between arms, because
    `sensor_tick` is a blueprint attribute and is read when the sensor is constructed. A
    `sensor_tick` of None spawns no cameras at all, which is the only arm that says what a tick costs
    with nothing rendering and is therefore the bound the other two are read against.
    """
    if sensor_tick is None:
        meter.warm_up(args.warmup_ticks)
        return meter.measure(label, args.ticks), 0, 0, None
    pair = ProbeCameraPair(world, args.width, args.height, args.fov, args.altitude,
                           sensor_tick=sensor_tick, logger=logger)
    try:
        meter.warm_up(args.warmup_ticks)
        pair.reset_tallies()
        pair.request_brightness()
        sample = meter.measure(label, args.ticks)
        logger.info("%s", pair.describe(args.ticks))
        level = pair.last_rgb_mean_level
        logger.info("   last frame mean byte level %s",
                    "not sampled" if level is None else f"{level:.1f}")
        return sample, pair.rgb_tally.frames, pair.depth_tally.frames, level
    finally:
        pair.close()
        # The destroy is a command to the server; let it retire before the next arm's spawn so the
        # two arms do not share a frame with three cameras in it.
        meter.warm_up(10)


def main() -> int:
    args = parse_args()
    logging.basicConfig(level=logging.INFO, format="%(message)s")
    logger = logging.getLogger("probe_sensor_tick")

    client = carlanet.Client(args.host, args.port)
    client.set_timeout(args.timeout)
    if args.delivered_world:
        client.load_delivered_world(args.delivered_world)
    world = client.get_world()
    logger.info("map %s, %d x %d, fov %.0f, %.0f ft, step %.3f s",
                world.get_map().name, args.width, args.height, args.fov, args.altitude,
                args.fixed_delta)

    meter = ClockRatioMeter(world, args.fixed_delta, logger=logger)
    original = world.get_settings()
    meter.configure_synchronous()
    sensor_tick = 1.0 / args.record_hz
    logger.info("%s", ClockRatioMeter.header())

    arms = [("no camera spawned", None), ("sensor_tick unset", 0.0),
            (f"sensor_tick {sensor_tick:.3f} s", sensor_tick)]
    results: dict[str, list] = {label: [] for label, _ in arms}
    try:
        for repeat in range(args.repeats):
            for label, value in arms:
                results[label].append(
                    run_arm(world, meter, args, value, f"{label}  (repeat {repeat + 1})", logger))
    finally:
        world.apply_settings(original)

    logger.info("")
    logger.info("%-28s%10s%10s%12s%14s%12s", "arm", "t/ws", "ratio", "ms/tick",
                "rgb frames", "per tick")
    rates = {}
    for label, runs in results.items():
        samples = [run[0] for run in runs]
        rate = statistics.fmean(sample.ticks_per_wall_second for sample in samples)
        rates[label] = rate
        spread = (max(sample.ticks_per_wall_second for sample in samples)
                  - min(sample.ticks_per_wall_second for sample in samples))
        frames = statistics.fmean(run[1] for run in runs)
        logger.info("%-28s%10.2f%9.1f%%%12.1f%14.1f%12.3f  (spread across repeats %.2f t/ws)",
                    label, rate, 100 * rate * args.fixed_delta, 1000.0 / rate, frames,
                    frames / args.ticks, spread)

    unset, been_set = (rates["sensor_tick unset"], rates[f"sensor_tick {sensor_tick:.3f} s"])
    bare = rates["no camera spawned"]
    if unset:
        logger.info("")
        logger.info("tick cost: %.1f ms with no camera, %.1f ms with the pair rendering every "
                    "tick, %.1f ms with sensor_tick set -- so the camera pair is %.1f ms of a tick "
                    "and setting sensor_tick returns %.1f ms of it",
                    1000.0 / bare, 1000.0 / unset, 1000.0 / been_set,
                    1000.0 / unset - 1000.0 / bare, 1000.0 / been_set - 1000.0 / bare)
        logger.info("setting sensor_tick to %.3f s multiplies the tick rate by %.2fx "
                    "(%.2f -> %.2f ticks per wall-second)",
                    sensor_tick, been_set / unset, unset, been_set)
        logger.info("frames kept per tick: %.3f unset, %.3f set; the capture rate asks for %.3f",
                    statistics.fmean(run[1] for run in results["sensor_tick unset"]) / args.ticks,
                    statistics.fmean(run[1] for run in
                                     results[f"sensor_tick {sensor_tick:.3f} s"]) / args.ticks,
                    args.record_hz * args.fixed_delta)
    return 0


if __name__ == "__main__":
    sys.exit(main())
