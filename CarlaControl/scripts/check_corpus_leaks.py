#!/usr/bin/env python3
"""Fail if a behavioural corpus says which of its vehicles a scenario planted.

Reads a corpus written by `sumo_cot_telemetry.py` -- a CSV of rows or an XML of CoT events -- and
the scenario's `*.labels.json`, groups every record by whether the labels call it planted, and
reports any field value that occurs in only one of the two groups.

A value carried only by planted vehicles identifies them and is a defect: the exit status is 1 and
the values are listed. A value carried only by nominal vehicles rules those out instead, which is
unavoidable when a handful of planted vehicles cannot occupy every vehicle type in a scenario; those
are printed under `--verbose` and never fail the run.

Examples:
    python check_corpus_leaks.py --labels ../../Import/Scenario.labels.json --csv corpus.csv
    python check_corpus_leaks.py --labels Scenario.labels.json --xml corpus.xml --verbose
"""
import argparse
import json
import logging
import sys
from pathlib import Path

_THIS = Path(__file__).resolve().parent
_REPO = _THIS.parent.parent
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.CorpusLeakValidator import (  # noqa: E402  (needs the path above)
    DEFAULT_FIELDS,
    CorpusLeakValidator,
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--labels", type=Path, required=True,
                        help="the scenario's *.labels.json, which names the planted vehicles")
    parser.add_argument("--csv", type=Path, help="a corpus of CSV rows to check")
    parser.add_argument("--xml", type=Path, help="a corpus of CoT events to check")
    parser.add_argument("--uid-prefix", default="SUMO-TRUTH",
                        help="the prefix the run gave every event uid (default SUMO-TRUTH)")
    parser.add_argument("--fields", nargs="+", default=list(DEFAULT_FIELDS),
                        help="fields to compare (default: the names and appearance a vehicle "
                             "carries, plus its dimensions)")
    parser.add_argument("--verbose", action="store_true",
                        help="also list the values only nominal vehicles carry, which are reported "
                             "but never fail the run")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
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
        logging.error("the corpus carries its own answer key; it cannot be released as it stands")
        return 1
    logging.info("no field separates the planted vehicles from the traffic they move among")
    return 0


if __name__ == "__main__":
    sys.exit(main())
