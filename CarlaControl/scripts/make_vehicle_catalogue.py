"""Build the vehicle catalogue by spawning and measuring every vehicle blueprint on a running server.

A vehicle's dimensions do not exist until it is spawned: nothing in the blueprint library, the
definition or the RPC that carries it holds a length, a width or a height, and the bounding box is
computed over the actor after it appears. So the catalogue is produced once per content build, by a
sweep against a server, and this is the command that runs it.

The server needs no world package, no imagery and no road network -- any map will do. It does need to
be free: the sweep spawns and destroys every vehicle blueprint in turn and refuses to write a
catalogue if the world does not hold the same number of vehicle actors at the end as at the start.

    python make_vehicle_catalogue.py
    python make_vehicle_catalogue.py --no-lamp-probe          # dimensions and colour only

The lamp pass renders each vehicle at night and counts the pixels each lamp adds, because the
simulator cannot be asked: every blueprint declares that it has lights, and reading the light state
back returns the command that was sent. Skipping the pass records every lamp as `unknown`, which is
not the same as `unlit` and is never treated as one.
"""
from __future__ import annotations

import argparse
import logging
import sys
from pathlib import Path

import carlanet

from carlacontrol.SumoInstallation import SumoInstallation
from carlacontrol.SumoVehicleTypeWriter import ROUTES_SCHEMA_RELATIVE_PATH
from carlacontrol.VehicleCatalogueBuilder import VehicleCatalogueBuilder
from carlacontrol.VehicleClassAssignment import VehicleClassAssignment
from carlacontrol.VehicleLampProbe import LampProbeSettings, VehicleLampProbe

DEFAULT_OUTPUT = Path("CarlaControl") / "catalogue"
STAGED_SUMO_INSTALL = Path("Build") / "sumo-install"
DEFAULT_SERVER_LOG = Path("Unreal") / "CarlaUnreal" / "Saved" / "Logs" / "CarlaUnreal.log"
DEFAULT_VEHICLE_PARAMETERS = (Path("Unreal") / "CarlaUnreal" / "Content" / "Carla" / "Config"
                              / "VehicleParameters.json")

logger = logging.getLogger("make_vehicle_catalogue")


def parse_arguments(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Spawn every vehicle blueprint, measure it, and write the vehicle catalogue.")
    parser.add_argument("--host", default="127.0.0.1", help="CARLA server host")
    parser.add_argument("--port", type=int, default=2000, help="CARLA server RPC port")
    parser.add_argument("--timeout", type=float, default=60.0, help="RPC timeout in seconds")
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT,
                        help="directory the catalogue and its report are written to")
    parser.add_argument("--catalogue-id", default=None,
                        help="name for this catalogue; defaults to the server version and platform")
    parser.add_argument("--content-build-id", default=None,
                        help="version string of the cooked content being measured; a mismatch "
                             "against it at run start is the one staleness nothing else can detect")
    parser.add_argument("--server-log", type=Path, default=DEFAULT_SERVER_LOG,
                        help="the running server's log, read to tell whether a requested colour "
                             "reached the vehicle body; without it every colour verdict is unknown")
    parser.add_argument("--vehicle-parameters", type=Path, default=DEFAULT_VEHICLE_PARAMETERS,
                        help="the content build's vehicle metadata, from which a corrected copy is "
                             "written beside the catalogue")
    parser.add_argument("--curation", type=Path, default=None,
                        help="JSON file of class assignments and overrides, replacing the built-in "
                             "ones; validated by the same rules either way")
    parser.add_argument("--sumo-home", type=Path, default=None,
                        help="SUMO installation whose route schema the emitted vehicle types are "
                             "validated against")
    parser.add_argument("--no-lamp-probe", action="store_true",
                        help="skip the optical lamp pass; every lamp is then recorded as unknown")
    parser.add_argument("--lamp-solar-time", type=float, default=LampProbeSettings().solar_time_hours,
                        help="sun-clock hour the lamp pass runs at; it must leave the sun below the "
                             "horizon or the pass refuses to report")
    parser.add_argument("--verbose", action="store_true", help="log every measurement as it is taken")
    return parser.parse_args(argv)


def resolve_schema(sumo_home: Path | None) -> Path | None:
    """SUMO's route schema: the one this repository stages, unless the caller names another.

    The staged install is preferred over whatever `SUMO_HOME` points at, because the catalogue is
    validated against the schema the distribution ships rather than against whichever SUMO happens
    to be installed on the machine that ran the sweep. The two have been observed to differ by a
    patch release on this tree.
    """
    staged = STAGED_SUMO_INSTALL
    if sumo_home is None and (staged / "data" / ROUTES_SCHEMA_RELATIVE_PATH).exists():
        sumo_home = staged
    try:
        installation = SumoInstallation.locate(sumo_home)
    except Exception as failure:
        logger.warning("no SUMO installation found (%s); the emitted vehicle types will not be "
                       "schema-checked", failure)
        return None
    schema = installation.home / "data" / ROUTES_SCHEMA_RELATIVE_PATH
    if not schema.exists():
        logger.warning("%s has no %s; the emitted vehicle types will not be schema-checked",
                       installation.home, ROUTES_SCHEMA_RELATIVE_PATH)
        return None
    return schema


def main(argv: list[str] | None = None) -> int:
    arguments = parse_arguments(argv)
    logging.basicConfig(level=logging.DEBUG if arguments.verbose else logging.INFO,
                        format="%(levelname)s %(name)s: %(message)s")

    client = carlanet.Client(arguments.host, arguments.port)
    client.set_timeout(arguments.timeout)
    world = client.get_world()

    assignment = (VehicleClassAssignment.from_file(arguments.curation)
                  if arguments.curation else VehicleClassAssignment())
    probe = VehicleLampProbe(world, LampProbeSettings(solar_time_hours=arguments.lamp_solar_time))
    builder = VehicleCatalogueBuilder(
        client, world,
        catalogue_id=arguments.catalogue_id,
        content_build_id=arguments.content_build_id,
        server_log=arguments.server_log if arguments.server_log.exists() else None,
        assignment=assignment,
        lamp_probe=probe)

    document = builder.build(probe_lamps=not arguments.no_lamp_probe)
    written = builder.write(
        document, arguments.output,
        schema_path=resolve_schema(arguments.sumo_home),
        vehicle_parameters=(arguments.vehicle_parameters
                            if arguments.vehicle_parameters.exists() else None))
    for name, path in written.items():
        logger.info("wrote %s: %s", name, path)
    logger.info("catalogue digest %s over %d blueprints",
                document["catalogue_digest"], len(document["vehicles"]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
