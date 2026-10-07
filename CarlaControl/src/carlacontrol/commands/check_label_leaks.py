"""Fail if a behavioral dataset says which of its vehicles a scenario planted.

Reads a dataset written by `carla-cot-telemetry` -- a CSV of rows or an XML of CoT events -- and
the scenario's `*.labels.json`, groups every record by whether the labels call it planted, and
reports any field value that occurs in only one of the two groups.

A value carried only by planted vehicles identifies them and is a defect: the exit status is 1 and
the values are listed. A value carried only by nominal vehicles rules those out instead, which is
unavoidable when a handful of planted vehicles cannot occupy every vehicle type in a scenario; those
are printed under `--verbose` and never fail the run.

Examples (from a checkout, `python CarlaControl/scripts/check_corpus_leaks.py` is the same tool):
    carla-check-label-leaks --labels Import/Scenario.labels.json --csv dataset.csv
    carla-check-label-leaks --labels Scenario.labels.json --xml dataset.xml --verbose
"""
import argparse
import json
import logging
import os
import sys
from pathlib import Path

from carlacontrol.CorpusLeakValidator import (
    DEFAULT_FIELDS,
    CorpusLeakValidator,
)


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        prog=os.path.basename(sys.argv[0]), description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--labels", type=Path, required=True,
                        help="the scenario's *.labels.json, which names the planted vehicles")
    parser.add_argument("--csv", type=Path, help="a dataset of CSV rows to check")
    parser.add_argument("--xml", type=Path, help="a dataset of CoT events to check")
    parser.add_argument("--uid-prefix", default="SUMO-TRUTH",
                        help="the prefix the run gave every event uid (default SUMO-TRUTH)")
    parser.add_argument("--fields", nargs="+", default=list(DEFAULT_FIELDS),
                        help="fields to compare (default: the names and appearance a vehicle "
                             "carries, plus its dimensions)")
    parser.add_argument("--verbose", action="store_true",
                        help="also list the values only nominal vehicles carry, which are reported "
                             "but never fail the run")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    logging.basicConfig(level=logging.INFO, format="%(message)s")

    if not (args.csv or args.xml):
        logging.error("nothing to check: give --csv, --xml or both")
        return 2

    labels = json.loads(args.labels.read_text(encoding="utf-8"))
    marked_ids = labels.get("marked_ids", [])
    if not marked_ids:
        logging.error("%s names no planted vehicles, so there is nothing to check against",
                      args.labels)
        return 2
    validator = CorpusLeakValidator(marked_ids, fields=args.fields, uid_prefix=args.uid_prefix)
    logging.info("%d planted vehicles, comparing %s", len(marked_ids), ", ".join(args.fields))

    identifying = []
    for path, check in ((args.csv, validator.check_csv), (args.xml, validator.check_xml)):
        if not path:
            continue
        findings = check(path)
        named = validator.identifying(findings)
        identifying += named
        logging.info("%s: %d value(s) identify a planted vehicle", path.name, len(named))
        if named:
            logging.error("%s", CorpusLeakValidator.describe(named))
        if args.verbose:
            rest = [finding for finding in findings if finding not in named]
            logging.info("%s", CorpusLeakValidator.describe(rest))

    if identifying:
        logging.error("the dataset carries its own answer key; it cannot be released as it stands")
        return 1
    logging.info("no field separates the planted vehicles from the traffic they move among")
    return 0
