#!/usr/bin/env python3
"""The server says what it was built from: its release, world interface, build and commits.

Every file our tools write records what made it, the server's build identity included, so a capture or a
world package can be traced to the major.minor release of the distribution that made it. The server
answers `get_build_identity` (`CarlaServer.cpp`, `GetCarlaBuildIdentity` in `Carla.cpp`) with a map of
names to values, each "unknown" where the server cannot know it; the client keeps the answer for the
connection (`CarlaClient.GetBuildIdentityAsync`) and the shim hands it over as a dict
(`Client.get_build_identity`).

What it does, on the generated world the server has loaded (a stock Town map is refused: checks run on
generated worlds only):

  1. reads the identity once, and again, and the older calls it must agree with: `version` and
     `get_world_interface_version`;
  2. reads the release this checkout sets, CARLA_VERSION in the top-level CMakeLists.txt
     (`Util/ReleaseVersion.py`), and the carlanet release the shim and its assemblies carry.

What it holds the answer to:

  * available: the server answers the call. A server built before it does not, and the check stops
    there: rebuild the server and run it again;
  * release: the server's release is this checkout's, and the one `version` answers; carlanet's
    release is the same MAJOR.MINOR.PATCH, and the shim and its assemblies carry one string;
  * world interface: the one `get_world_interface_version` answers, MAJOR.MINOR;
  * build: `package` or `editor`, and a configuration that is not unknown;
  * commits: a package names all three from its VERSION file (`commits_from` `version_file`), each a
    full commit hash; an editor run names the CARLA commit compiled into it (`compiled`) and leaves the
    content and engine commits unknown -- never a guess;
  * kept: the second answer is the first.

It ticks nothing and spawns nothing, so it can run beside anything.

Prereqs: a generated world loaded on a server built with get_build_identity, and the carlanet wheel
built from this tree.

Usage:
    python test_build_identity.py [--host h] [--port p]
"""
import argparse
import importlib.util
import logging
import re
import sys
from pathlib import Path

import carlanet as carla

# isort: split
# The .NET namespaces exist only once carlanet has loaded their assemblies.
from CarlaNet.Types.Provenance import Producer

# A stock CARLA town: Town01 to Town15, Town10HD and the like, with or without a package path.
STOCK_TOWN = re.compile(r"^town\d", re.IGNORECASE)
FULL_COMMIT = re.compile(r"^[0-9a-f]{40}$")
CHECKOUT = Path(__file__).resolve().parents[2]


def checkout_release() -> str:
    """CARLA_VERSION as this checkout's top-level CMakeLists.txt sets it."""
    spec = importlib.util.spec_from_file_location("carla_release_version",
                                                  CHECKOUT / "Util" / "ReleaseVersion.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module.ReleaseVersion.read_carla_version(CHECKOUT / "CMakeLists.txt")


class BuildIdentityCheck:
    """Reads the server's build identity and holds it to what the server and the checkout say."""

    def __init__(self, client, logger: logging.Logger) -> None:
        self.client = client
        self.logger = logger
        self.failures: list[str] = []

    def check(self, passed: bool, what: str) -> None:
        self.logger.info("%-100s %s", what, "ok" if passed else "FAIL")
        if not passed:
            self.failures.append(what)

    def run(self) -> int:
        world = self.client.get_world()
        map_name = str(world.get_map().name)
        base = map_name.replace("\\", "/").rsplit("/", 1)[-1]
        if STOCK_TOWN.match(base):
            self.logger.error("the server has %s loaded, a stock Town map; this check runs on a "
                              "generated world only. Load one and run it again", map_name)
            return 2
        self.logger.info("world: %s", map_name)

        identity = self.client.get_build_identity()
        for name, value in identity.items():
            self.logger.info("  %-16s %s", name, value)
        if not identity.get("available"):
            self.logger.error("the server does not say what it was built from (%s); rebuild the "
                              "server from this tree and run this again", identity.get("reason"))
            return 3

        release = checkout_release()
        self.check(identity["release"] == release,
                   f"release: the server's, {identity['release']}, is this checkout's CARLA_VERSION, {release}")
        self.check(identity["release"] == str(self.client.get_server_version()),
                   "release: the one `version` answers")
        shim = str(carla.__version__)
        assemblies = str(Producer.CarlaNetVersion)
        self.check(shim.split("+", 1)[0] == release,
                   f"release: carlanet's, {shim}, is the same release")
        self.check(shim == assemblies,
                   f"release: the shim and its assemblies carry one string ({shim}, {assemblies})")

        interface = str(self.client.get_world_interface_version())
        self.check(identity["world_interface"] == interface
                   and re.fullmatch(r"\d+\.\d+", interface) is not None,
                   f"world interface: {identity['world_interface']}, the one get_world_interface_version "
                   f"answers ({interface})")

        build = identity.get("build")
        self.check(build in ("package", "editor"), f"build: package or editor ({build})")
        self.check(identity.get("configuration") not in (None, "", "unknown"),
                   f"configuration: stated ({identity.get('configuration')})")
        commits = {name: identity.get(name) for name in ("carla_commit", "content_commit", "engine_commit")}
        if build == "package":
            self.check(identity.get("commits_from") == "version_file",
                       f"commits: a package reads them from its VERSION file ({identity.get('commits_from')})")
            for name, value in commits.items():
                self.check(bool(FULL_COMMIT.match(str(value))), f"commits: {name} is a full commit ({value})")
        else:
            compiled = identity.get("commits_from") == "compiled"
            self.check(compiled and bool(FULL_COMMIT.match(str(commits["carla_commit"]))),
                       f"commits: the editor names the CARLA commit compiled into it "
                       f"({identity.get('commits_from')}, {commits['carla_commit']})")
            self.check(commits["content_commit"] == "unknown" and commits["engine_commit"] == "unknown",
                       "commits: the editor leaves the content and engine commits unknown, never a guess")

        self.check(self.client.get_build_identity() == identity, "kept: the second answer is the first")

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
    args = parser.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(message)s")
    client = carla.Client(args.host, args.port)
    client.set_timeout(30.0)
    return BuildIdentityCheck(client, logging.getLogger("test_build_identity")).run()


if __name__ == "__main__":
    sys.exit(main())
