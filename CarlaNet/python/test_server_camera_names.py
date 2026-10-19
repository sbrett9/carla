#!/usr/bin/env python3
"""The server issues camera names and refuses duplicates, and every client reads the name it settled.

A camera spawned with no name, or with its blueprint's default role name, is Camera_<n> from a counter
the server keeps for its lifetime; a client-given name a live camera holds is refused at spawn, in any
case, and so is a client-given name of the server's form. The spawned actor's role_name carries the
name back, and the shim's world.camera_name reads it (CarlaServer.cpp, SettleCameraName; the plan's
04 C4 section 6.1).

What it does, on whatever world the server has loaded:

  1. a camera spawned with no name comes back named Camera_<n>, and the shim reads that name;
  2. a second unnamed camera takes the next number, and a destroyed camera's number is never reused;
  3. a depth camera spawned with no name is named too, and a non-camera sensor is not;
  4. a name the client gives is kept as given, and read back;
  5. the same name again, from a second client and in another case, is refused by the server with the
     holder named; once the holder is destroyed the name is free again;
  6. a client-given Camera_<digits> is refused by the server, past the shim's own rule;
  7. with --reload, the world is reloaded and the next unnamed camera continues the count rather than
     starting again at 1.

Destroys what it spawns on every path out. The world's settings are not changed.

Prereqs: a server built with SettleCameraName (a server built before it names nothing, and the shim
says so on stderr), and the carlanet wheel built from this tree.

Usage:
    python test_server_camera_names.py [--host h] [--port p] [--reload]
"""
import argparse
import logging
import re
import sys

import carlanet as carla

SERVER_FORM = re.compile(r"^Camera_(\d+)$")


class ServerCameraNamesCheck:
    """Spawns cameras through two clients and checks the names the server settles and refuses."""

    def __init__(self, client, second, logger: logging.Logger, reload: bool) -> None:
        self.client = client
        self.world = client.get_world()
        self.second = second.get_world()
        self.logger = logger
        self.reload = reload
        self.failures: list[str] = []
        self.spawned = []

    def check(self, passed: bool, what: str) -> None:
        self.logger.info("%-78s %s", what, "ok" if passed else "FAIL")
        if not passed:
            self.failures.append(what)

    def blueprint(self, world, blueprint_id: str):
        blueprint = world.get_blueprint_library().find(blueprint_id)
        if blueprint.has_attribute("image_size_x"):
            blueprint.set_attribute("image_size_x", "64")
            blueprint.set_attribute("image_size_y", "36")
        return blueprint

    def camera(self, world=None, name=None, blueprint_id: str = "sensor.camera.rgb"):
        world = world or self.world
        actor = world.spawn_camera(self.blueprint(world, blueprint_id),
                                   carla.Transform(carla.Location(x=0.0, y=0.0, z=50.0),
                                                   carla.Rotation(pitch=-90.0)),
                                   name=name)
        self.spawned.append(actor)
        return actor

    def destroy(self, actor) -> None:
        actor.destroy()
        self.spawned.remove(actor)

    def refused(self, what: str, call, saying: str) -> None:
        try:
            call()
        except ValueError as refusal:
            self.check(saying in str(refusal), f"{what}: refused saying '{saying}' ({refusal})")
            return
        except Exception as failure:  # noqa: BLE001 - a refusal of another kind is reported as one
            self.check(False, f"{what}: refused as a ValueError, not {type(failure).__name__}: {failure}")
            return
        self.check(False, f"{what}: refused")

    @staticmethod
    def number(name: str) -> int | None:
        match = SERVER_FORM.match(name)
        return int(match.group(1)) if match else None

    def run(self) -> int:
        try:
            first = self.camera()
            first_name = self.world.camera_name(first)
            self.check(self.number(first_name) is not None,
                       f"an unnamed camera is named by the server: {first_name}")
            self.check(first.attributes.get("role_name") == first_name,
                       "the name is the spawned actor's role_name")
            self.check(carla.camera_named_by_server(first), "the shim sees the server named it")

            second = self.camera()
            second_name = self.world.camera_name(second)
            n1, n2 = self.number(first_name), self.number(second_name)
            self.check(n1 is not None and n2 == n1 + 1,
                       f"the next unnamed camera takes the next number: {second_name}")

            self.destroy(second)
            third = self.camera()
            third_name = self.world.camera_name(third)
            self.check(self.number(third_name) == n1 + 2,
                       f"a destroyed camera's number is never reused: {third_name}")

            depth = self.camera(blueprint_id="sensor.camera.depth")
            depth_name = self.world.camera_name(depth)
            self.check(self.number(depth_name) == n1 + 3,
                       f"an unnamed depth camera is named too: {depth_name}")

            gnss = self.world.spawn_actor(self.blueprint(self.world, "sensor.other.gnss"),
                                          carla.Transform(carla.Location(x=0.0, y=0.0, z=50.0)))
            self.spawned.append(gnss)
            self.check(self.number(gnss.attributes.get("role_name", "")) is None,
                       f"a sensor that is not a camera is not named: role_name "
                       f"{gnss.attributes.get('role_name')!r}")

            named = self.camera(name="Overwatch_1")
            self.check(self.world.camera_name(named) == "Overwatch_1",
                       "a name the client gives is kept as given and read back")
            self.check(not carla.camera_named_by_server(named),
                       "the shim sees the client named it")

            self.refused("the same name from a second client, in another case",
                         lambda: self.camera(self.second, name="overwatch_1"),
                         f"already held in this world by camera {named.id}")
            self.destroy(named)
            freed = self.camera(self.second, name="overwatch_1")
            self.check(self.second.camera_name(freed) == "overwatch_1",
                       "once the holder is destroyed the name is free again")

            # Past the shim's own rule, straight at the server.
            raw = self.blueprint(self.world, "sensor.camera.rgb")
            raw.set_attribute("role_name", f"Camera_{n1 + 100}")

            def claim():
                actor = self.world.spawn_actor(raw, carla.Transform(carla.Location(z=50.0)))
                self.spawned.append(actor)

            try:
                claim()
                self.check(False, "a client-given Camera_<digits> is refused by the server")
            except Exception as refusal:  # noqa: BLE001 - the RPC's error is the expected answer
                self.check("which a client cannot claim" in str(refusal),
                           f"a client-given Camera_<digits> is refused by the server ({refusal})")

            if self.reload:
                for actor in list(self.spawned):
                    self.destroy(actor)
                map_name = self.world.get_map().name
                self.logger.info("reloading %s", map_name)
                self.client.reload_world()
                self.world = self.client.get_world()
                after = self.camera()
                after_name = self.world.camera_name(after)
                self.check(self.number(after_name) is not None and self.number(after_name) > n1 + 3,
                           f"after a world reload the count continues: {after_name}")
        finally:
            for actor in self.spawned:
                try:
                    actor.destroy()
                except Exception as failure:  # noqa: BLE001 - cleanup reports and continues
                    self.logger.warning("could not destroy an actor: %r", failure)

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
    parser.add_argument("--reload", action="store_true",
                        help="also reload the world and check the count continues")
    args = parser.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(message)s")
    client = carla.Client(args.host, args.port)
    client.set_timeout(30.0)
    second = carla.Client(args.host, args.port)
    second.set_timeout(30.0)
    return ServerCameraNamesCheck(client, second, logging.getLogger("test_server_camera_names"),
                                  args.reload).run()


if __name__ == "__main__":
    sys.exit(main())
