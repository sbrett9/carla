"""Check every file our tools write against its published schema: a capture folder, a world package, a
vehicle catalogue, and whatever else a folder holds.

A capture folder holds, for each camera, a PNG and a truth sidecar for every still, and under truth/ the
run manifest and the world truth track with its summary. Each is checked against the schema installed
with carlacontrol:

  truth sidecar <camera>/<camera>_<time>.xml   truth_sidecar.xsd
  PNG text chunks carla:capture, carla:solar,  png_chunk_capture.schema.json and the three others
    carla:illumination, carla:sensor
  truth/manifest.jsonl, row by row             run_manifest.schema.json
  truth/world_truth_track.csv                  world_truth_track.tableschema.json
  truth/world_truth_track.summary.json         world_truth_track_summary.schema.json

The files that live beside a capture are checked too, wherever the folder holds them: a run's records
(run.result.json, run.resolution.json, run.lock.json, run.effective.json), a compiled scenario's lock,
resolution report, supervision plan and sweep index, the authoring skill's checks.json, and the files a
user writes -- run files, site profiles, specifications, sweeps, epochs, display conventions and areas
of interest -- each against the schema of its kind in the same folder of schemas. So are what the SUMO
bridge (carla-cot-telemetry) writes and the legacy files beside it:

  <name>.xml, root <events source="sumo">      sumo_cot_events.xsd
  <name>.csv and its <name>.summary.json       sumo_cot_telemetry.tableschema.json, and
                                               sumo_cot_telemetry_summary.schema.json
  <name>.supervision.json, the described gaps  supervision_gaps.schema.json
  *.labels.json, a legacy scenario's labels    legacy_labels.schema.json

A world package and the vehicle catalogue are checked whole, given themselves or a folder holding them:

  <name>.cwp, a world package                  every entry, against world_package_manifest.schema.json
                                               and the others its format page names
  a folder holding vehicles.catalogue.json     vehicle_catalogue.schema.json, vehicle_types.xsd and
                                               vehicle_body_widths.schema.json

The pixels are not checked. A file that declares a format version newer than its schema describes was
written by a newer release and is reported as such; a file that declares none is version 1. A manifest
with no closing row, or a bridge event file with no closing </events>, is noted as a run that was
interrupted, which is not a failure.

Exit status: 0 when every file keeps its schema, 1 when any does not, 2 when the path holds no file any
published schema describes.

`--write-schemas DIR` writes every published schema this release generates into DIR instead -- the ones
generated in Python and the capture schemas the loaded CarlaNet generates -- which is how
`CarlaControl/schemas` is regenerated after a writer or a reader changes (`PublishedSchemas`).

Examples (from a checkout, `python CarlaControl/scripts/validate_capture.py` is the same tool):
    carla-validate Build/captures/cap-20261007-173433-41f49b
    carla-validate Build/captures/cap-20261007-173433-41f49b --show 200
    carla-validate Build/world-packages/Arapahoe_I25.cwp
    carla-validate CarlaControl/catalogue
    carla-validate --write-schemas CarlaControl/schemas
"""
import argparse
import logging
import os
import sys
from pathlib import Path

from carlacontrol.CaptureSchemaSet import CaptureSchemaSet
from carlacontrol.CaptureValidator import KINDS as CAPTURE_KINDS
from carlacontrol.CaptureValidator import CaptureValidator
from carlacontrol.PublishedSchemas import PublishedSchemas
from carlacontrol.ToolLayout import ToolLayout
from carlacontrol.WorldFileValidator import CATALOGUE_FILE, PACKAGE_SUFFIX, WorldFileValidator
from carlacontrol.WorldFileValidator import KINDS as WORLD_KINDS

KINDS = (*CAPTURE_KINDS, *WORLD_KINDS)


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        prog=os.path.basename(sys.argv[0]), description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("path", type=Path, nargs="?",
                        help="a capture folder, or one camera's folder within it; a folder of run "
                             "records, compiled scenarios, inputs or SUMO bridge output; a world "
                             "package (.cwp); a vehicle catalogue folder or its "
                             "vehicles.catalogue.json; or any folder holding them")
    parser.add_argument("--show", type=int, default=50,
                        help="how many failures to list (default 50); every one is counted")
    parser.add_argument("--schemas", type=Path, default=None,
                        help="the folder of schemas to check against (default: the ones installed with "
                             "carlacontrol, or the checkout's CarlaControl/schemas)")
    parser.add_argument("--write-schemas", type=Path, metavar="DIR", default=None,
                        help="write every schema this release generates into DIR, and check nothing")
    args = parser.parse_args(argv)
    if args.path is None and args.write_schemas is None:
        parser.error("name a path to check, or --write-schemas DIR")
    return args


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    logging.basicConfig(level=logging.INFO, format="%(message)s")

    if args.write_schemas is not None:
        try:
            written = PublishedSchemas.write_every(args.write_schemas)
        except RuntimeError as unavailable:
            logging.error("%s", unavailable)
            return 2
        logging.info("wrote %d schemas into %s", len(written), args.write_schemas)
        return 0

    path: Path = args.path
    schema_directory = args.schemas or ToolLayout.current().schema_directory
    if path.is_file() and (path.suffix == PACKAGE_SUFFIX or path.name == CATALOGUE_FILE):
        results = [WorldFileValidator(schema_directory).validate(path)]
        shown_from = path.parent
    elif path.is_dir():
        results = [CaptureValidator(CaptureSchemaSet(schema_directory)).validate(path),
                   WorldFileValidator(schema_directory).validate(path)]
        shown_from = path
    else:
        logging.error("%s is not a folder, a world package (%s) or a %s", path, PACKAGE_SUFFIX,
                      CATALOGUE_FILE)
        return 2
    checked = sum(sum(result.checked.values()) for result in results)
    if not checked:
        logging.error("no file a published schema describes under %s", path)
        return 2

    failures = [failure for result in results for failure in result.failures]
    for kind in KINDS:
        count = sum(result.checked[kind] for result in results)
        if count:
            failed = sum(result.failed_files[kind] for result in results)
            logging.info("%-28s %6d checked, %d failed", kind, count, failed)
    for result in results:
        for note in result.notes:
            logging.info("note: %s", note)
    for failure in failures[:max(0, args.show)]:
        logging.error("FAILED: %s", failure.describe(shown_from))
    if len(failures) > args.show:
        logging.error("... and %d more failures", len(failures) - args.show)
    if failures:
        return 1
    logging.info("every file keeps its schema")
    return 0
