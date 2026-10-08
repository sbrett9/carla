"""Build a generated world on a running CARLA server and write its world package, then exit.

The world build of `carla-sctmv`, on its own: the same options, the same build, and none of the
interactive view, traffic or telemetry afterwards. The build converts the OpenStreetMap extract to
OpenDRIVE with netconvert, samples the Cesium terrain under every road, meshes the elevated network
and loads it on the server; this then writes the world package -- the road network, the grids that
convert driven height to true ground height, the manifest, and the authoring reference set --
which the scenario compiler, the drive and the capture runs all read.

    carla-build-world --osm Import/Arapahoe_I25.osm
    carla-build-world --osm osm/Lakeview_Carson.osm --emit-world-package world-packages

The package is written to `--emit-world-package`, by default `Build/world-packages` from a checkout
and `world-packages` under the current folder when installed. The options below that belong to the
interactive view, traffic, telemetry and recording are accepted and have no effect here.

(From a checkout, `python CarlaControl/scripts/build_world.py` is the same tool.)

Prerequisites:
  * a headless CARLA server running
  * netconvert: from a checkout, the one under Build/sumo-install; installed, the one
    CARLA_NETCONVERT or SUMO_HOME names, which a CARLA distribution's environment step sets
  * the CESIUM_ION_TOKEN environment variable, or --ion-token

Exit status 0 when the world was built and its package written, 1 when either was not.
"""
import logging

import carlanet as carla

from carlacontrol.CarlaControlArgumentParser import CarlaControlArgumentParser
from carlacontrol.commands.sctmv import configure_logging
from carlacontrol.ProducerRecord import ProducerRecord
from carlacontrol.ToolLayout import ToolLayout
from carlacontrol.WorldBuilder import WorldBuilder

# How long the first server calls may take, before the build sets its own --timeout.
CONNECT_TIMEOUT_S = 15.0

# The tool the world package names as what made it: this command.
TOOL = "carla-build-world"

logger = logging.getLogger("build_world")


def main(argv: list[str] | None = None) -> int:
    # First, so the world package names this command and carlacontrol's release.
    ProducerRecord.declare_tool(TOOL)
    layout = ToolLayout.current()
    netconvert, proj = layout.apply_sumo_environment()
    args = CarlaControlArgumentParser(layout, description=__doc__).parse_args(argv)
    configure_logging(args.log)
    if args.emit_world_package is None:
        args.emit_world_package = str(layout.world_package_directory)
    logger.info("defaults from %s; the world package goes to %s", layout.describe(),
                args.emit_world_package)
    if netconvert is None:
        logger.error("no netconvert to build the world with: set CARLA_NETCONVERT, or SUMO_HOME to "
                     "a SUMO installation (a CARLA distribution's environment step sets both)")
        return 1

    client = carla.Client(args.host, args.port)
    client.set_timeout(CONNECT_TIMEOUT_S)
    logger.info("server version: %s", client.get_server_version())
    builder = WorldBuilder(layout, str(netconvert), str(proj) if proj else None)
    if not builder.build_world(client, args):
        logger.error("the world was not built")
        return 1
    if builder.world_package_path is None:
        # The build reports a package it could not write and carries on, because the world on the
        # server is still usable; for this command the package is the point.
        logger.error("the world is built and loaded, but its package was not written (the warning "
                     "above says why)")
        return 1
    logger.info("world built; its package is %s", builder.world_package_path)
    return 0
