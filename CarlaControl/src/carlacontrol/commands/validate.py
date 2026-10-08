"""Check every file of a capture folder against its published schema.

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
of interest -- each against the schema of its kind in the same folder of schemas.

The pixels are not checked. A file that declares a format version newer than its schema describes was
written by a newer release and is reported as such; a file that declares none is version 1. A manifest
with no closing row is noted as a run that was interrupted, which is not a failure.

Exit status: 0 when every file keeps its schema, 1 when any does not, 2 when the folder holds no file
of a capture.

Examples (from a checkout, `python CarlaControl/scripts/validate_capture.py` is the same tool):
    carla-validate Build/captures/cap-20261007-173433-41f49b
    carla-validate Build/captures/cap-20261007-173433-41f49b --show 200
"""
import argparse
import logging
import os
import sys
from pathlib import Path

from carlacontrol.CaptureSchemaSet import CaptureSchemaSet
from carlacontrol.CaptureValidator import KINDS, CaptureValidator
from carlacontrol.ToolLayout import ToolLayout


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        prog=os.path.basename(sys.argv[0]), description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("capture", type=Path,
                        help="a capture folder, or one camera's folder within it; or a folder of run "
                             "records, compiled scenarios or inputs")
    parser.add_argument("--show", type=int, default=50,
                        help="how many failures to list (default 50); every one is counted")
    parser.add_argument("--schemas", type=Path, default=None,
                        help="the folder of schemas to check against (default: the ones installed with "
                             "carlacontrol, or the checkout's CarlaControl/schemas)")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    logging.basicConfig(level=logging.INFO, format="%(message)s")

    if not args.capture.is_dir():
        logging.error("%s is not a folder", args.capture)
        return 2
    schemas = CaptureSchemaSet(args.schemas or ToolLayout.current().schema_directory)
    result = CaptureValidator(schemas).validate(args.capture)
    if not sum(result.checked.values()):
        logging.error("no file of a capture under %s", args.capture)
        return 2

    for kind in KINDS:
        if result.checked[kind]:
            logging.info("%-28s %6d checked, %d failed", kind, result.checked[kind], result.failed_files[kind])
    for note in result.notes:
        logging.info("note: %s", note)
    for failure in result.failures[:max(0, args.show)]:
        logging.error("FAILED: %s", failure.describe(args.capture))
    if len(result.failures) > args.show:
        logging.error("... and %d more failures", len(result.failures) - args.show)
    if not result.ok:
        return 1
    logging.info("every file keeps its schema")
    return 0
