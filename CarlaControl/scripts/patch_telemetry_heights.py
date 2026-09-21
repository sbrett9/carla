#!/usr/bin/env python3
"""Rewrite the height in telemetry datasets recorded before the bare-earth frame was corrected.

Datasets written earlier read the bare-earth grid with SUMO's northing where the grid is indexed in
the CARLA world frame, so every height came from the row mirrored about the map's centre line. Each
row carries the position it was sampled at and the world package that produced it still exists, so
the height is recomputed from the row's own coordinates: no simulation is re-run, and nothing but
that one column changes.

Before writing anything the datasets are checked against the grid: for every row the height is
recomputed the way the old code did, and the file is refused unless nearly all of them reproduce
what the row records. That is what stops this being run against a dataset from another world, or
against one that has already been patched.

The originals are never modified. Patched copies are written to the output directory, which must
not be the directory the originals are in unless `--in-place-directory` says so.

Examples:
    # check without writing
    python patch_telemetry_heights.py --grid Build/world-packages/Arapahoe_I25.cwp \\
        --csv Build/telemetry/Arapahoe_I25_cot.csv --verify-only

    # rewrite a dataset and its event stream together
    python patch_telemetry_heights.py --grid Build/world-packages/Arapahoe_I25.cwp \\
        --csv Build/telemetry/Arapahoe_I25_cot.csv --xml Build/telemetry/Arapahoe_I25_cot.xml \\
        --out-dir Build/telemetry/patched
"""
import argparse
import logging
import sys
from pathlib import Path

_THIS = Path(__file__).resolve().parent
_REPO = _THIS.parent.parent
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.SumoCotBridge import BareEarthGrid  # noqa: E402  (needs the path above)
from carlacontrol.TelemetryHeightPatcher import TelemetryHeightPatcher  # noqa: E402


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--grid", type=Path, required=True,
                        help="the world package (.cwp) or loose bareearth.bin the dataset's "
                             "heights were read from")
    parser.add_argument("--csv", type=Path, required=True,
                        help="the dataset to patch; it carries the coordinates everything else "
                             "is recomputed from")
    parser.add_argument("--xml", type=Path,
                        help="the event stream the same run wrote, patched record for record "
                             "against the CSV")
    parser.add_argument("--out-dir", type=Path,
                        help="where the patched copies go (default: beside the originals, with "
                             "'.patched' before the extension)")
    parser.add_argument("--in-place-directory", action="store_true",
                        help="allow the output directory to be the one holding the originals; the "
                             "originals are still not modified, only read")
    parser.add_argument("--verify-only", action="store_true",
                        help="check that the datasets came from this grid and report, without "
                             "writing anything")
    parser.add_argument("--tolerance", type=float,
                        default=TelemetryHeightPatcher.DEFAULT_TOLERANCE_M,
                        help="metres a recomputed height may differ from the recorded one and "
                             "still count as reproducing it (default %(default)s)")
    parser.add_argument("--verified-fraction", type=float,
                        default=TelemetryHeightPatcher.DEFAULT_VERIFIED_FRACTION,
                        help="share of rows that must reproduce themselves before the file is "
                             "accepted (default %(default)s)")
    return parser.parse_args()


def _destination(source: Path, out_dir: Path | None) -> Path:
    name = f"{source.stem}.patched{source.suffix}"
    return (out_dir / name) if out_dir else source.with_name(name)


def main() -> int:
    args = parse_args()
    logging.basicConfig(level=logging.INFO, format="%(message)s")

    grid = BareEarthGrid.from_file(args.grid)
    logging.info("grid %s: %d x %d at %.0f m, lower corner (%.1f, %.1f)",
                 args.grid.name, grid.columns, grid.rows, grid.cell_size, grid.min_x, grid.min_y)
    patcher = TelemetryHeightPatcher(grid, tolerance_m=args.tolerance,
                                     verified_fraction=args.verified_fraction)

    try:
        report = patcher.verify_csv(args.csv)
        logging.info("%s: %s", args.csv.name, report.summary())
        if args.verify_only:
            accepted = report.verified_fraction >= args.verified_fraction
            logging.info("%s %s this grid, and %s be patched against it", args.csv.name,
                         "was produced by" if accepted else "was NOT produced by",
                         "can" if accepted else "cannot")
            return 0 if accepted else 1

        out_dir = args.out_dir
        if out_dir:
            if out_dir.resolve() == args.csv.resolve().parent and not args.in_place_directory:
                logging.error("the output directory is the one holding the originals; pass "
                              "--in-place-directory if that is meant")
                return 2
            out_dir.mkdir(parents=True, exist_ok=True)

        patcher.patch_csv(args.csv, _destination(args.csv, out_dir))
        if args.xml:
            patcher.patch_xml(args.xml, args.csv, _destination(args.xml, out_dir))
    except (OSError, ValueError) as error:
        logging.error("%s", error)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
