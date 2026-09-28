"""Publish a world's authoring reference set into an existing world package, without rebuilding it.

The world build publishes the set itself (`run_SCTMV.py --emit-world-package`). This is for a world
built before the set existed, and for one whose areas of interest were declared or edited afterwards:
an area edit changes no road geometry, so it is no reason to rebuild a world. No CARLA server is
used. A SUMO process is started on the package's own network to place the areas, and closed.

    python CarlaControl/scripts/publish_reference_set.py --package Build/world-packages/Arapahoe_I25.cwp
    python CarlaControl/scripts/publish_reference_set.py --package X.cwp --aoi Import/X.aoi.geojson
    python CarlaControl/scripts/publish_reference_set.py --package X.cwp --output scratch/X.cwp

Areas come from the first of: `--aoi`; `<extract>.aoi.geojson` beside the extract (`--osm`, by
default `Import/<map>.osm`); the GeoJSON the package already carries; none, which publishes an empty
area table. The extract's `<bounds>` check that every area reaches into the world; without an
extract, the network's own geographic extent does.

Exits 1 when areas were declared and refused, naming every rule they broke; the place index and the
solar frame are published either way.
"""
from __future__ import annotations

import argparse
import logging
import shutil
import sys
from pathlib import Path

from carlacontrol.AreaOfInterestResolver import DEFAULT_NEAR_M
from carlacontrol.AreaOfInterestSource import AreaOfInterestError, AreaOfInterestSource
from carlacontrol.AuthoringReferenceSet import AuthoringReferenceSet
from carlacontrol.OsmClipper import OsmClipper
from carlacontrol.SumoInstallation import SumoInstallation
from carlacontrol.WorldPackageReader import WorldPackageReader

REPO = Path(__file__).resolve().parents[2]
STAGED_SUMO_INSTALL = REPO / "Build" / "sumo-install"
IMPORT_DIRECTORY = REPO / "Import"

logger = logging.getLogger("publish_reference_set")


def parse_arguments(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Publish areas of interest, the place index and the solar frame into a world "
                    "package.")
    parser.add_argument("--package", type=Path, required=True, help="the world package (.cwp)")
    parser.add_argument("--aoi", type=Path, default=None,
                        help="areas of interest as GeoJSON; default <extract>.aoi.geojson beside the "
                             "extract, then the GeoJSON the package already carries")
    parser.add_argument("--osm", type=Path, default=None,
                        help="the OSM extract the world was built from, for its <bounds>; default "
                             "Import/<map>.osm")
    parser.add_argument("--output", type=Path, default=None,
                        help="publish into a copy written here, leaving --package untouched")
    parser.add_argument("--near-m", type=float, default=DEFAULT_NEAR_M,
                        help="distance within which a lane counts as near an area (m)")
    parser.add_argument("--sumo-home", type=Path, default=None,
                        help="SUMO installation to project with; default the staged Build/sumo-install")
    parser.add_argument("--allow-version-mismatch", action="store_true",
                        help="proceed when that SUMO is not the release that converted the world")
    return parser.parse_args(argv)


def locate_sumo(arguments: argparse.Namespace, reader: WorldPackageReader) -> SumoInstallation:
    """The staged install unless another is named, checked against the world's converter."""
    explicit = arguments.sumo_home
    if explicit is None and STAGED_SUMO_INSTALL.exists():
        explicit = STAGED_SUMO_INSTALL
    installation = SumoInstallation.locate(explicit)
    installation.require_version(reader.netconvert_version or None,
                                 allow_mismatch=arguments.allow_version_mismatch)
    return installation


def load_areas(arguments: argparse.Namespace, reader: WorldPackageReader,
               extract: Path | None) -> AreaOfInterestSource | None:
    """Areas from the first source that has them. Raises `AreaOfInterestError` on a bad file."""
    bounds = OsmClipper.read_bounds(extract) if extract is not None else None
    if arguments.aoi is not None:
        logger.info("areas of interest from --aoi: %s", arguments.aoi)
        return AreaOfInterestSource.load(arguments.aoi, bounds)
    if extract is not None:
        beside = AreaOfInterestSource.discover(extract)
        if beside is not None:
            logger.info("areas of interest from beside the extract: %s", beside)
            return AreaOfInterestSource.load(beside, bounds)
    carried = reader.areas_source()
    if carried is not None:
        logger.info("areas of interest from the GeoJSON the package already carries")
        return AreaOfInterestSource.from_bytes(carried, WorldPackageReader.AREAS_SOURCE_ENTRY,
                                               bounds)
    logger.info("no areas of interest declared; publishing an empty area table")
    return None


def main(argv: list[str] | None = None) -> int:
    arguments = parse_arguments(argv)
    logging.basicConfig(level=logging.INFO, format="%(levelname)s %(name)s: %(message)s")

    package = arguments.package
    if arguments.output is not None:
        arguments.output.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(package, arguments.output)
        package = arguments.output
    reader = WorldPackageReader(package)

    extract = arguments.osm or IMPORT_DIRECTORY / f"{reader.map_name}.osm"
    if not extract.is_file():
        logger.info("no extract at %s; areas are checked against the network's own extent", extract)
        extract = None
    try:
        areas = load_areas(arguments, reader, extract)
    except AreaOfInterestError as refusal:
        for problem in refusal.problems:
            logger.error("%s", problem)
        logger.error("areas of interest refused; nothing was published")
        return 1

    installation = locate_sumo(arguments, reader) if areas is not None else None
    report = AuthoringReferenceSet(package, installation, areas, arguments.near_m).publish()
    logger.info("published into %s: %s (%d warning(s) above)", package, ", ".join(report.entries),
                len(report.warnings))
    return 1 if report.refusals else 0


if __name__ == "__main__":
    sys.exit(main())
