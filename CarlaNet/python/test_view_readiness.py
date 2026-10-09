#!/usr/bin/env python3
"""The server reports whether a camera's photoreal tiles have arrived, and never answers "ready" wrongly.

get_view_readiness(actor) is the tile half of a capture's readiness: only the server can see tiles
stream. It answers as of the end of the last tick: the frame, whether the camera's view drove the
tilesets' selection on that tick ("published"), and per tileset its load progress, load queues and
failed tiles. Plan: Docs/CAT_Research/Plans/SUMO_Behavioral_Capture/03_CoSimulation_Runtime.md 9.5.1.

What it does, on the generated world the server has loaded (a stock map has no tilesets to report):

  1. an unknown actor id, and an actor that is not a camera, raise rather than answer;
  2. a camera asked about before any tick is not published; after one tick it is, and the frame it
     reports is the frame that tick returned;
  3. on every tick, a visible tileset's failed tiles among those drawn never exceed those loaded;
  4. over ground not looked at in this session, the tiles come in -- every visible tileset at load
     progress 100 with no failed tile in view -- within the ceiling, and the tick count is reported;
  5. returning to that pose, they are in again within a few ticks of publication.

Restores the world's settings and destroys what it spawns on every path out.

Prereqs: a server built with get_view_readiness, running a generated world with photoreal tiles, and
the carlanet wheel built from this tree.

Usage:
    python test_view_readiness.py [--host h] [--port p] [--x 510] [--y -98] [--altitude 450]
"""
import argparse
import logging
import sys
import time

import carlanet as carla

DELTA_S = 0.05
CEILING_S = 90.0


class ViewReadinessCheck:
    """Asks the server about cameras it spawns, and checks what the answers may and may not say."""

    def __init__(self, client, logger: logging.Logger, x: float, y: float, altitude: float) -> None:
        self.world = client.get_world()
        self.logger = logger
        self.x, self.y, self.altitude = x, y, altitude
        self.failures: list[str] = []
        self.spawned = []

    def check(self, passed: bool, what: str) -> None:
        self.logger.info("%-78s %s", what, "ok" if passed else "FAIL")
        if not passed:
            self.failures.append(what)

    def camera(self, x: float, y: float):
        blueprint = self.world.get_blueprint_library().find("sensor.camera.rgb")
        blueprint.set_attribute("image_size_x", "1280")
        blueprint.set_attribute("image_size_y", "720")
        actor = self.world.spawn_actor(blueprint, carla.Transform(
            carla.Location(x=x, y=y, z=self.altitude), carla.Rotation(pitch=-90.0)))
        self.spawned.append(actor)
        return actor

    @staticmethod
    def tiles_in(readiness) -> bool:
        visible = [t for t in readiness["tilesets"] if t["visible"]]
        return (readiness["published"] and bool(visible)
                and all(t["load_progress"] >= 100.0 and t["failed_in_view"] == 0 for t in visible))

    def raises(self, what: str, call) -> None:
        try:
            call()
        except Exception as refusal:  # noqa: BLE001 - any refusal is the expected answer
            self.check(True, f"{what}: raises ({type(refusal).__name__})")
            return
        self.check(False, f"{what}: raises")

    def wait_until_in(self, camera, label: str) -> int | None:
        started = time.monotonic()
        ticks = 0
        while time.monotonic() - started < CEILING_S:
            self.world.tick()
            ticks += 1
            readiness = self.world.get_view_readiness(camera)
            for tileset in readiness["tilesets"]:
                if tileset["failed_in_view"] > tileset["failed_loaded"]:
                    self.check(False, f"{label}: failed_in_view never exceeds failed_loaded "
                                      f"(tick {ticks}, ion {tileset['ion_asset_id']})")
                    return None
            if self.tiles_in(readiness):
                self.logger.info("%s: tiles in after %d ticks, %.1f s", label, ticks,
                                 time.monotonic() - started)
                return ticks
        self.logger.info("%s: tiles not in within %.0f s (%d ticks)", label, CEILING_S, ticks)
        return None

    def run(self) -> int:
        original = self.world.get_settings()
        try:
            settings = self.world.get_settings()
            settings.synchronous_mode = True
            settings.fixed_delta_seconds = DELTA_S
            self.world.apply_settings(settings)
            self.world.tick()

            self.raises("an unknown actor id", lambda: self.world.get_view_readiness(999_999))
            gnss = self.world.spawn_actor(self.world.get_blueprint_library().find("sensor.other.gnss"),
                                          carla.Transform(carla.Location(x=self.x, y=self.y, z=10.0)))
            self.spawned.append(gnss)
            self.raises("a sensor that is not a camera", lambda: self.world.get_view_readiness(gnss))

            camera = self.camera(self.x, self.y)
            before = self.world.get_view_readiness(camera)
            self.check(not before["published"], "a camera asked about before any tick: not published")
            frame = self.world.tick()
            after = self.world.get_view_readiness(camera)
            self.check(after["published"], "after one tick: published")
            self.check(after["frame"] == frame,
                       f"the frame reported is the frame the tick returned ({after['frame']} vs {frame})")
            visible = [t["ion_asset_id"] for t in after["tilesets"] if t["visible"]]
            self.check(bool(visible), f"at least one visible tileset is reported (ion {visible})")

            cold = self.wait_until_in(camera, "cold pose")
            self.check(cold is not None, "cold pose: tiles in within the ceiling")

            camera.destroy()
            self.spawned.remove(camera)
            self.world.tick()
            again = self.camera(self.x, self.y)
            warm = self.wait_until_in(again, "same pose again")
            self.check(warm is not None and (cold is None or warm <= cold),
                       f"same pose again: in no later than the cold pose ({warm} vs {cold} ticks)")
        finally:
            for actor in self.spawned:
                try:
                    actor.destroy()
                except Exception as failure:  # noqa: BLE001 - cleanup reports and continues
                    self.logger.warning("could not destroy an actor: %r", failure)
            self.world.apply_settings(original)

        for failure in self.failures:
            self.logger.error("FAIL: %s", failure)
        if self.failures:
            return 1
        self.logger.info("PASS")
        return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=2000)
    parser.add_argument("--x", type=float, default=510.0, help="camera position, CARLA x, metres")
    parser.add_argument("--y", type=float, default=-98.0, help="camera position, CARLA y, metres")
    parser.add_argument("--altitude", type=float, default=450.0, help="camera height, metres")
    args = parser.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(message)s")
    client = carla.Client(args.host, args.port)
    client.set_timeout(30.0)
    return ViewReadinessCheck(client, logging.getLogger("test_view_readiness"),
                              args.x, args.y, args.altitude).run()


if __name__ == "__main__":
    sys.exit(main())
