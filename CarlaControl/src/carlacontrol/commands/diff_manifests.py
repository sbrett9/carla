"""Fail if two runs of one scenario name different supervision rows in their run manifests.

The `(instance_id, participant, phase)` triples a run manifest names are fixed by the compiled
supervision plan before the run starts; a run may only bind them. Two runs of one scenario -- at
another SUMO step, with the capture window opening later, under another sun -- must name the same
triples, differing only in times and outcomes (`06_Truth_And_Annotation.md` D6.8). This compares the
two manifests' triples and lists every one named by one and not the other, every triple a manifest
names that its own plan does not declare, and every triple opened or closed twice. What the runs bound
differently -- onsets, closing instants, how each interval closed -- is listed beside, and is not a
difference. See `carlacontrol.RunManifestDiff` for how a manifest is read.

A manifest with no terminal row was interrupted: its intervals opened and never closed are listed as
open at the interruption, and it names only the triples its run reached.

Exit status: 0 when the triples are the same, 1 on any difference, 2 when a file is not a run manifest
or the two are not of one scenario.

Examples (from a checkout, `python CarlaControl/scripts/diff_run_manifests.py` is the same tool):
    carla-diff-manifests Build/captures/cap-a/truth/manifest.jsonl Build/captures/cap-b/truth/manifest.jsonl
    carla-diff-manifests first.jsonl second.jsonl --show 50
"""
import argparse
import logging
import os
import sys
from pathlib import Path

from carlacontrol.RunManifestDiff import (
    ManifestUnreadable,
    diff,
    read_manifest,
    same_scenario,
)


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        prog=os.path.basename(sys.argv[0]), description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("first", type=Path, help="one run's manifest")
    parser.add_argument("second", type=Path, help="another run's manifest, of the same scenario")
    parser.add_argument("--show", type=int, default=20,
                        help="how many intervals bound differently to list (default 20)")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    logging.basicConfig(level=logging.INFO, format="%(message)s")

    try:
        first = read_manifest(args.first)
        second = read_manifest(args.second)
    except (OSError, ManifestUnreadable) as failed:
        logging.error("%s", failed)
        return 2
    if not same_scenario(first, second):
        logging.error("not two runs of one scenario: %s is of %s and %s of %s", first.path.name,
                      first.scenario_id, second.path.name, second.scenario_id)
        return 2

    result = diff(first, second)
    for line in result.describe(show=args.show):
        logging.info("%s", line)
    for difference in result.differences:
        logging.error("DIFFERENCE: %s", difference)
    if not result.same:
        return 1
    logging.info("the two runs name the same supervision rows")
    return 0
