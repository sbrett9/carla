#!/usr/bin/env python3
"""Merge the editor-measured body widths into the existing vehicle catalogue, with no server.

The catalogue sweep (`make_vehicle_catalogue.py`) merges `vehicle_body_widths.json` as it writes a new
catalogue. This applies a new body-width measurement to the catalogue already in the tree without
re-running the sweep, which needs a running server: every measured blueprint gains `body_width_m`
beside its bounding box, the header records the measurement's method and date, the document is
validated and its digest recomputed, and the SUMO vehicle types are written again with the body
widths. Everything the sweep measured is left as it measured it.

A changed catalogue has a new digest, so every compiled scenario is recompiled afterwards: a session
refuses a scenario compiled against another catalogue digest.

    python apply_vehicle_body_widths.py [--catalogue ../catalogue/vehicles.catalogue.json]
        [--body-widths ../catalogue/vehicle_body_widths.json] [--sumo-home ...]
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

_THIS = Path(__file__).resolve().parent
_REPO = _THIS.parent.parent
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.SumoInstallation import SumoInstallation  # noqa: E402
from carlacontrol.SumoVehicleTypeWriter import (  # noqa: E402
    ROUTES_SCHEMA_RELATIVE_PATH,
    SumoVehicleTypeWriter,
)
from carlacontrol.VehicleCatalogue import VehicleCatalogue  # noqa: E402
from carlacontrol.VehicleCatalogueBuilder import (  # noqa: E402
    BODY_WIDTHS_FILENAME,
    CATALOGUE_FILENAME,
    REPORT_FILENAME,
    VEHICLE_TYPES_FILENAME,
    VehicleCatalogueBuilder,
)
from carlacontrol.VehicleCatalogueValidator import VehicleCatalogueValidator  # noqa: E402

CATALOGUE_DIRECTORY = _REPO / "CarlaControl" / "catalogue"
STAGED_SUMO = _REPO / "Build" / "sumo-install"
REPORT_SECTION = "Body widths without mirrors, measured in the editor"


def report_section(document: dict) -> str:
    """The body widths beside the boxes, for the human-readable report."""
    header = document["body_width"]
    lines = [REPORT_SECTION, f"  {header['measured']}; {header['source']}", f"  {header['method']}", "",
             f"  {'blueprint':32s} {'box width':>9} {'body':>7}"]
    for entry in document["vehicles"]:
        if "body_width_m" in entry:
            lines.append(f"  {entry['blueprint_id']:32s} {entry['width_m']:9.3f} {entry['body_width_m']:7.3f}")
    return "\n".join(lines) + "\n"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--catalogue", type=Path, default=CATALOGUE_DIRECTORY / CATALOGUE_FILENAME)
    parser.add_argument("--body-widths", type=Path, default=CATALOGUE_DIRECTORY / BODY_WIDTHS_FILENAME)
    parser.add_argument("--sumo-home", type=Path,
                        default=STAGED_SUMO if STAGED_SUMO.exists() else None,
                        help="the SUMO whose route-file schema the vehicle types are checked against")
    args = parser.parse_args(argv)

    document = json.loads(args.catalogue.read_text(encoding="utf-8"))
    VehicleCatalogueBuilder.apply_body_widths(
        document, VehicleCatalogueBuilder.load_body_widths(args.body_widths), source=args.body_widths.name)
    VehicleCatalogueValidator(document).validate()
    document["catalogue_digest"] = VehicleCatalogue.digest_of(document)
    args.catalogue.write_text(VehicleCatalogue.canonical_json(document), encoding="utf-8", newline="\n")

    directory = args.catalogue.parent
    writer = SumoVehicleTypeWriter(document)
    vehicle_types = writer.write(directory / VEHICLE_TYPES_FILENAME)
    if args.sumo_home is not None:
        schema = SumoInstallation.locate(str(args.sumo_home)).home / "data" / ROUTES_SCHEMA_RELATIVE_PATH
        if schema.exists():
            writer.validate(vehicle_types, schema)

    report = directory / REPORT_FILENAME
    if report.exists():
        text = report.read_text(encoding="utf-8")
        kept = text.split(REPORT_SECTION)[0].rstrip("\n")
        report.write_text(kept + "\n\n" + report_section(document), encoding="utf-8", newline="\n")

    print(f"catalogue {args.catalogue} digest {document['catalogue_digest']}")
    print(f"vehicle types {vehicle_types}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
