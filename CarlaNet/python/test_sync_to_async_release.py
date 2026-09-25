#!/usr/bin/env python3
"""A world switched from synchronous back to asynchronous must start free-running on its own.

The server drains RPCs inside a loop while it is synchronous, waiting for the client's tick cue. A
client that switches the world to asynchronous is served from inside that drain, and an asynchronous
client never sends another cue. If the loop waits on the cue alone, the world then reports
asynchronous and advances nothing -- the whole engine, not only CARLA's clock -- until some client
ticks it. Every camera in it delivers nothing and every observer sees nothing, which reads exactly
like "a camera in an asynchronous world delivers no frames", and was once written down as that.

Any client that takes the clock and gives it back does this switch: a SUMO drive session restoring
the world on exit, run_SCTMV.py leaving synchronous mode, a probe. The next client to observe the
world asynchronously finds it stopped.

What it does, on whatever map the server has loaded:

  1. takes the world synchronous and ticks it --ticks times, counting observer frames; the count
     must equal the ticks, which proves the counter before anything is concluded from it;
  2. switches the world to asynchronous and, with no client ticking, counts observer frames for
     --seconds; any frame at all passes, none fails;
  3. spawns a camera into that asynchronous world and counts the images it delivers the same way;
  4. restores the settings it found.

Run against a server built before the drain loop tested the mode on every pass, step 2 fails.

Prereqs: a server running (RunCarlaServer.ps1) with any map loaded. No client may be ticking it.

Usage:
    python test_sync_to_async_release.py [--ticks 20] [--seconds 3] [--host h] [--port p]
"""
import argparse
import logging
import sys
import threading
import time

import carlanet as carla


class FrameCounter:
    """Counts world-observer frames through world.on_tick."""

    def __init__(self, world):
        self._world = world
        self._lock = threading.Lock()
        self._count = 0
        self._id = world.on_tick(self._on_tick)

    def _on_tick(self, _timestamp) -> None:
        with self._lock:
            self._count += 1

    def read(self) -> int:
        with self._lock:
            return self._count

    def count_over(self, seconds: float) -> int:
        """Frames that arrive during the next `seconds` of wall clock."""
        start = self.read()
        time.sleep(seconds)
        return self.read() - start

    def close(self) -> None:
        self._world.remove_on_tick(self._id)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=2000)
    parser.add_argument("--ticks", type=int, default=20)
    parser.add_argument("--seconds", type=float, default=3.0)
    args = parser.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(message)s")
    logger = logging.getLogger("test_sync_to_async_release")

    client = carla.Client(args.host, args.port)
    client.set_timeout(20.0)
    world = client.get_world()
    original = world.get_settings()
    logger.info("map %s; found sync=%s delta=%s", world.get_map().name,
                original.synchronous_mode, original.fixed_delta_seconds)

    counter = FrameCounter(world)
    camera = None
    failures = []
    try:
        settings = world.get_settings()
        settings.synchronous_mode = True
        settings.fixed_delta_seconds = 0.05
        world.apply_settings(settings)
        # A world found free-running can still deliver a frame it produced before the switch.
        time.sleep(0.5)

        before = counter.read()
        for _ in range(args.ticks):
            world.tick()
        time.sleep(0.5)
        seen = counter.read() - before
        logger.info("synchronous: %d ticks, %d observer frames", args.ticks, seen)
        if seen != args.ticks:
            logger.error("FAIL: the frame counter does not count ticks; nothing below can be "
                         "concluded")
            return 2

        settings = world.get_settings()
        settings.synchronous_mode = False
        settings.fixed_delta_seconds = None
        world.apply_settings(settings)

        seen = counter.count_over(args.seconds)
        logger.info("asynchronous, no client ticking, %.1f s: %d observer frames",
                    args.seconds, seen)
        if seen == 0:
            failures.append("the world did not advance after the switch to asynchronous")

        blueprint = world.get_blueprint_library().find("sensor.camera.rgb")
        blueprint.set_attribute("image_size_x", "320")
        blueprint.set_attribute("image_size_y", "240")
        camera = world.spawn_actor(blueprint, carla.Transform(
            carla.Location(x=0.0, y=0.0, z=120.0), carla.Rotation(pitch=-60.0)))
        images = []
        camera.listen(lambda image: images.append(image.frame))
        time.sleep(args.seconds)
        camera.stop()
        logger.info("asynchronous camera, %.1f s: %d images", args.seconds, len(images))
        if not images:
            failures.append("a camera in the asynchronous world delivered no images")
    finally:
        if camera is not None:
            camera.destroy()
        counter.close()
        world.apply_settings(original)

    for failure in failures:
        logger.error("FAIL: %s", failure)
    if failures:
        return 1
    logger.info("PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
