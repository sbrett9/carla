"""Measure the vertical continuity of generated OpenDRIVE maps, offline.

Reads already-generated ``*_elevated.xodr`` files, re-fits each road's sampled heights
with a candidate scheme and reports what changed — slope steps between records, how far
a filtered fit drifts from the heights actually sampled, overshoot, agreement where
roads hand over to each other, junction-connector grades, paired-carriageway agreement
and the ragged final span every road ends on.

No server and no editor are involved, so this is the cheap loop for judging a fit change
before it lands in ``ElevationInjector``.

Examples::

    python probe_elevation_profile.py
    python probe_elevation_profile.py --fit monotone --smooth-window 5
    python probe_elevation_profile.py --map Iran_Route_96 --locate 27.0769276,55.9823149
"""

from __future__ import annotations

import argparse
import logging
import sys
from pathlib import Path

from ElevationProfileProbe import Distribution, ElevationProfileProbe

logger = logging.getLogger("probe_elevation_profile")

DEFAULT_MAP_DIR = Path(__file__).resolve().parents[2] / "Build" / "sumo-smoketest"
SECTIONS = (
    "census",
    "slope",
    "deviation",
    "overshoot",
    "links",
    "connectors",
    "carriageways",
    "terminal",
)


def format_distribution(label: str, distribution: Distribution, threshold: str) -> str:
    """One aligned line of distribution statistics."""
    return (
        f"    {label:<22s} n={distribution.count:<7d} median {distribution.median:8.4f}  "
        f"p90 {distribution.p90:8.4f}  p99 {distribution.p99:8.4f}  "
        f"max {distribution.maximum:9.4f}  over {threshold} {distribution.fraction_over:6.1%}"
    )


def report_map(probe: ElevationProfileProbe, args: argparse.Namespace) -> None:
    """Run the requested sections against one map and log the results."""
    counts = probe.census()
    logger.info("")
    logger.info("=== %s - %d roads, %d records", probe.name, len(probe.roads), counts["records"])

    fitted = probe.refit(args.fit, args.smooth_window, args.smooth_order)

    if "census" in args.sections:
        logger.info("  census")
        logger.info(
            "    records %d, with c!=0 %d, with d!=0 %d",
            counts["records"],
            counts["curved"],
            counts["cubic"],
        )
        logger.info(
            "    roads with a profile %d, more than two records %d, ending b=0 %d",
            counts["roads_with_profile"],
            counts["roads_over_two_records"],
            counts["roads_ending_flat"],
        )
        logger.info(
            "    plan view: line %d, arc %d, paramPoly3 %d",
            counts["line"],
            counts["arc"],
            counts["paramPoly3"],
        )

    if "slope" in args.sections:
        logger.info("  slope discontinuity at internal record boundaries")
        logger.info(format_distribution("before", probe.slope_discontinuity(), "0.02"))
        logger.info(format_distribution("after", probe.slope_discontinuity(fitted), "0.02"))
        logger.info(
            "    height step (C0)      before %.3e m   after %.3e m",
            probe.height_discontinuity(),
            probe.height_discontinuity(fitted),
        )

    if "deviation" in args.sections:
        worst_deviation, rms, worst = probe.deviation_from_samples(fitted)
        logger.info("  deviation of the fitted curve from the sampled heights")
        logger.info(
            "    max %.4f m, rms %.4f m%s",
            worst_deviation,
            rms,
            "" if args.smooth_window > 1 else "   (interpolating fit - exact by construction)",
        )
        for deviation, road_id, station in worst:
            logger.info("      %7.4f m  road %-8s s=%.2f", deviation, road_id, station)

    if "overshoot" in args.sections:
        violations, spans, worst = probe.overshoot(fitted)
        logger.info("  overshoot outside bracketing sample values")
        logger.info(
            "    %d violations over %d monotone spans, worst excursion %.6f m",
            violations,
            spans,
            worst,
        )

    if "links" in args.sections:
        for label, source in (("before", None), ("after", fitted)):
            heights, slopes, worst = probe.link_mismatch(source)
            logger.info("  road-to-road links (%s)", label)
            logger.info(format_distribution("height mismatch", heights, "0.05"))
            logger.info(format_distribution("slope mismatch", slopes, "0.02"))
            if label == "after":
                for mismatch, road_id, other_id in worst:
                    logger.info("      %7.4f  road %-8s -> %-8s", mismatch, road_id, other_id)

    if "connectors" in args.sections:
        for label, source in (("before", None), ("after", fitted)):
            connectors, spans, steep, worst_grade, worst = probe.connector_grades(source)
            logger.info(
                "  junction connectors (%s): %d roads, %d spans, %d above %.0f%%, worst %.1f%%",
                label,
                connectors,
                spans,
                steep,
                probe.STEEP_GRADE * 100.0,
                worst_grade * 100.0,
            )
            if label == "after":
                for grade, road_id, length, linked in worst:
                    logger.info(
                        "      %6.1f%%  road %-8s length %7.2f m   linked roads carry %.1f%%",
                        grade * 100.0,
                        road_id,
                        length,
                        linked * 100.0,
                    )

    if "carriageways" in args.sections:
        pairs = probe.carriageway_pairs()
        logger.info("  paired carriageways: %d detected", len(pairs))
        for label, source in (("before", None), ("after", fitted)):
            disagreements = probe.carriageway_disagreement(pairs, source)
            if not disagreements:
                break
            worst = max(d.maximum for _, _, d, _, _ in disagreements)
            crossovers = sum(c for _, _, _, c, _ in disagreements)
            sustained = sum(1 for _, _, _, _, run in disagreements if run > 0.0)
            logger.info(
                "    %-6s worst |dz| %.3f m over %d pairs, %d crossovers total, "
                "%d pairs with a sustained departure",
                label,
                worst,
                len(disagreements),
                crossovers,
                sustained,
            )
            ranked = sorted(disagreements, key=lambda entry: -entry[2].maximum)
            for left, right, distribution, count, run in ranked[: args.worst]:
                logger.info(
                    "      %-6s/%-6s median %.4f m  p90 %.4f m  max %.4f m  "
                    "over 0.25 m %5.1f%%  crossovers %-4d departure run %.0f m",
                    left,
                    right,
                    distribution.median,
                    distribution.p90,
                    distribution.maximum,
                    distribution.fraction_over * 100.0,
                    count,
                    run,
                )

    if "terminal" in args.sections:
        lengths, steep, total = probe.terminal_spans()
        if lengths:
            logger.info(
                "  terminal spans: n=%d  median %.2f m  p10 %.3f m  min %.4f m  under 1 m %.1f%%",
                total,
                lengths[len(lengths) // 2],
                lengths[len(lengths) // 10],
                lengths[0],
                sum(1 for x in lengths if x < 1.0) / total * 100.0,
            )
            logger.info(
                "    implying a grade above %.0f%%: %d of %d (%.1f%%)",
                probe.STEEP_GRADE * 100.0,
                steep,
                total,
                steep / total * 100.0,
            )


def report_location(probe: ElevationProfileProbe, args: argparse.Namespace) -> None:
    """Tie a WGS84 point reported from a running session to specific roads."""
    latitude, longitude = (float(v) for v in args.locate.split(","))
    found = probe.locate(latitude, longitude, args.radius)
    east, north = probe.project(latitude, longitude)
    logger.info("")
    logger.info(
        "=== %s - %.7f, %.7f projects to x=%.2f, y=%.2f (%d roads within %.0f m)",
        probe.name,
        latitude,
        longitude,
        east,
        north,
        len(found),
        args.radius,
    )
    for distance, road, station in found:
        logger.info(
            "    road %-8s length %9.2f m  junction %-4s  closest %5.2f m at s=%.2f",
            road.id,
            road.length,
            road.junction,
            distance,
            station,
        )
        records = road.records
        for i, record in enumerate(records):
            if abs(record.s - station) <= 20.0:
                logger.info(
                    "        s=%9.3f  a=%9.3f  b=%+.4f  c=%+.3e  d=%+.3e%s",
                    record.s,
                    record.a,
                    record.b,
                    record.c,
                    record.d,
                    "   <- road end" if i == len(records) - 1 else "",
                )


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument(
        "paths",
        nargs="*",
        type=Path,
        help=f"xodr files to measure (default: every elevated map in {DEFAULT_MAP_DIR.name})",
    )
    parser.add_argument(
        "--map",
        action="append",
        default=[],
        help="only maps whose name contains this text; repeatable",
    )
    parser.add_argument(
        "--fit",
        choices=("linear", "monotone"),
        default="monotone",
        help="candidate fit to re-fit the recovered samples with",
    )
    parser.add_argument(
        "--smooth-window",
        type=int,
        default=1,
        help="low-pass window in samples before fitting (1 disables it)",
    )
    parser.add_argument(
        "--smooth-order",
        type=int,
        default=None,
        help="polynomial order of the low-pass (default 2)",
    )
    parser.add_argument(
        "--sections",
        default=",".join(SECTIONS),
        help=f"comma-separated subset of: {', '.join(SECTIONS)}",
    )
    parser.add_argument(
        "--locate", default=None, help="LAT,LON to tie to road ids instead of measuring"
    )
    parser.add_argument(
        "--radius", type=float, default=45.0, help="search radius in metres for --locate"
    )
    parser.add_argument(
        "--worst", type=int, default=5, help="how many worst offenders to list per section"
    )
    return parser


def resolve_paths(args: argparse.Namespace) -> list[Path]:
    paths = args.paths or sorted(DEFAULT_MAP_DIR.glob("*_elevated.xodr"))
    if args.map:
        paths = [p for p in paths if any(m.lower() in p.stem.lower() for m in args.map)]
    return paths


def main() -> int:
    args = build_parser().parse_args()
    logging.basicConfig(level=logging.INFO, format="%(message)s", stream=sys.stdout)
    args.sections = {s.strip() for s in args.sections.split(",") if s.strip()}

    paths = resolve_paths(args)
    if not paths:
        logger.error("no maps matched; looked in %s", DEFAULT_MAP_DIR)
        return 1

    order = 2 if args.smooth_order is None else args.smooth_order
    if 1 < args.smooth_window <= order + 1:
        logger.warning(
            "a %d-point window fits an order-%d polynomial exactly, so it passes the "
            "series through unchanged; use a window of at least %d",
            args.smooth_window,
            order,
            order + 3,
        )

    logger.info("fit=%s  smooth-window=%d  maps=%d", args.fit, args.smooth_window, len(paths))
    for path in paths:
        probe = ElevationProfileProbe(path)
        if args.locate:
            report_location(probe, args)
        else:
            report_map(probe, args)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
