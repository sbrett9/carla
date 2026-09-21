"""Rewrite the height in a telemetry dataset that was recorded in the wrong coordinate frame.

Datasets written before `BareEarthGrid.height_at` was corrected read the bare-earth grid with SUMO's
northing where the grid is indexed in the CARLA world frame, so every height came from the row
mirrored about the map's centre line. The recorded rows carry the coordinates they were sampled at,
and the grid that produced them still exists, so the height can be recomputed from each row's own
position rather than by running the simulation again: the vehicles, the times and the tracks are all
correct, and only one column is wrong.

**The script proves a file is one of its own before it touches it.** For every row it recomputes the
height the way the old code did -- unnegated and clamped to the grid's edge -- and compares it with
what the row records. A file produced from this grid reproduces itself almost exactly; a file from a
different world, a different grid, or one that has already been patched does not, and is refused
with the figures that led to the refusal. Some rows legitimately differ: the recorded position is
rounded to a centimetre and the grid's cells are metres across, so a vehicle within a centimetre of
a cell boundary can fall on the other side of it when recomputed. Measured on the two shipped
datasets, that is 0.34% of 905,017 Arapahoe rows and 0.20% of 88,039 Gardnerville rows, which is why
the gate is a fraction of rows rather than every row.

The XML form of a dataset does not carry the positions -- only latitude and longitude -- so it is
patched alongside the CSV its run wrote at the same time, record for record, with each event's uid
checked against the row's before either is used. Nothing is written in place: a patched copy is
written beside the original, and the original is what proves the copy.
"""
from __future__ import annotations

import csv
import logging
import math
import re
import statistics
from dataclasses import dataclass, field
from pathlib import Path

from carlacontrol.SumoCotBridge import BareEarthGrid

# One CoT event per line, as `SumoCotBridge` writes them. The height sits in the `point` child and
# the uid on the event itself; both are matched rather than parsed so a 568 MB file can stream.
EVENT_UID = re.compile(r'<event\b[^>]*\buid="([^"]*)"')
POINT_HAE = re.compile(r'(<point\b[^>]*\bhae=")([^"]*)(")')


@dataclass
class PatchReport:
    """What a patch pass found and did."""

    records: int = 0
    # Records whose recorded height the old lookup reproduces within the tolerance. The rest are
    # cell-boundary rounding, and are rewritten too -- they are still this grid's rows.
    verified: int = 0
    rewritten: int = 0
    # Records the corrected lookup cannot answer because the vehicle is outside the grid. Their
    # height is left exactly as recorded, since there is nothing truer to put there.
    off_grid: int = 0
    worst_verification_m: float = 0.0
    corrections_m: list[float] = field(default_factory=list)

    @property
    def verified_fraction(self) -> float:
        return self.verified / self.records if self.records else 0.0

    def summary(self) -> str:
        if not self.records:
            return "no records"
        corrections = sorted(abs(value) for value in self.corrections_m)
        median = statistics.median(corrections) if corrections else 0.0
        return (f"{self.records} records, {self.verified_fraction:.3%} reproduce the recorded "
                f"height (worst {self.worst_verification_m:.4f} m); {self.rewritten} rewritten, "
                f"{self.off_grid} left as recorded; correction median {median:.2f} m, "
                f"max {corrections[-1] if corrections else 0.0:.2f} m")


class TelemetryHeightPatcher:
    """Rewrites a dataset's height column from its own coordinates, against the grid that made it."""

    # A recorded position is rounded to a centimetre, so a recomputed height agrees to well inside
    # this except where the rounding crosses a cell boundary.
    DEFAULT_TOLERANCE_M = 0.01
    # Below this share of rows reproducing themselves, the file was not produced from this grid.
    DEFAULT_VERIFIED_FRACTION = 0.99

    def __init__(self, grid: BareEarthGrid, tolerance_m: float = DEFAULT_TOLERANCE_M,
                 verified_fraction: float = DEFAULT_VERIFIED_FRACTION,
                 logger: logging.Logger | None = None):
        self.grid = grid
        self.tolerance_m = tolerance_m
        self.verified_fraction = verified_fraction
        self.logger = logger or logging.getLogger(__name__)

    # -- the two lookups ---------------------------------------------------------------------

    def recorded_height(self, x: float, y: float) -> float:
        """The height the dataset holds: the lookup as it stood when the dataset was written.

        SUMO's northing fed straight into a grid indexed in the CARLA frame, and an out-of-range
        read clamped to the edge cell instead of refusing. Reproduced here, and only here, so the
        patcher can recognise a file it is able to repair.
        """
        col = min(self.grid.columns - 1,
                  max(0, math.floor((x - self.grid.min_x) / self.grid.cell_size)))
        row = min(self.grid.rows - 1,
                  max(0, math.floor((y - self.grid.min_y) / self.grid.cell_size)))
        return self.grid.heights[row * self.grid.columns + col]

    def corrected_height(self, x: float, y: float) -> float | None:
        """The height the same position should have had, in the frame the grid is indexed in."""
        return self.grid.height_at(x, -y)

    # -- patching ----------------------------------------------------------------------------

    def patch_csv(self, source: str | Path, destination: str | Path) -> PatchReport:
        """Rewrite `hae_m` in a CSV dataset, after proving the file came from this grid."""
        source, destination = Path(source), Path(destination)
        report = self.verify_csv(source)
        self._require(report, source)

        with open(source, newline="", encoding="utf-8") as reader, \
                open(destination, "w", newline="", encoding="utf-8") as writer:
            rows = csv.DictReader(reader)
            out = csv.DictWriter(writer, fieldnames=rows.fieldnames)
            out.writeheader()
            for row in rows:
                height = self.corrected_height(float(row["sumo_x"]), float(row["sumo_y"]))
                if height is not None:
                    row["hae_m"] = f"{height:.2f}"
                out.writerow(row)
        self.logger.info("%s -> %s: %s", source.name, destination.name, report.summary())
        return report

    def patch_xml(self, source: str | Path, companion_csv: str | Path,
                  destination: str | Path) -> PatchReport:
        """Rewrite each event's `hae`, taking positions from the CSV the same run wrote.

        The two files hold the same records in the same order -- one pass of the bridge writes both
        -- so they are walked together and each event's uid is checked against its row's before the
        row's coordinates are used for it.
        """
        source, companion_csv, destination = Path(source), Path(companion_csv), Path(destination)
        report = self.verify_csv(companion_csv)
        self._require(report, companion_csv)

        written = PatchReport()
        with open(source, encoding="utf-8") as reader, \
                open(companion_csv, newline="", encoding="utf-8") as csv_reader, \
                open(destination, "w", encoding="utf-8", newline="") as writer:
            rows = csv.DictReader(csv_reader)
            for line in reader:
                uid = EVENT_UID.search(line)
                if uid is None:
                    writer.write(line)
                    continue
                row = next(rows, None)
                if row is None:
                    raise ValueError(f"{source.name} holds more events than {companion_csv.name} "
                                     f"holds rows; they are not from the same run")
                if row["uid"] != uid.group(1):
                    raise ValueError(f"{source.name} and {companion_csv.name} disagree at record "
                                     f"{written.records + 1}: {uid.group(1)!r} against "
                                     f"{row['uid']!r}. They are not from the same run")
                written.records += 1
                height = self.corrected_height(float(row["sumo_x"]), float(row["sumo_y"]))
                if height is None:
                    written.off_grid += 1
                    writer.write(line)
                    continue
                written.rewritten += 1
                writer.write(POINT_HAE.sub(
                    lambda match, value=height: f"{match.group(1)}{value:.2f}{match.group(3)}",
                    line, count=1))
            if next(rows, None) is not None:
                raise ValueError(f"{companion_csv.name} holds more rows than {source.name} holds "
                                 f"events; they are not from the same run")
        written.verified = report.verified
        written.worst_verification_m = report.worst_verification_m
        written.corrections_m = report.corrections_m
        self.logger.info("%s -> %s: %d events rewritten from %s",
                         source.name, destination.name, written.rewritten, companion_csv.name)
        return written

    # -- verification ------------------------------------------------------------------------

    def verify_csv(self, source: str | Path) -> PatchReport:
        """Recompute every row's height the way the old code did and compare it with the record."""
        report = PatchReport()
        with open(source, newline="", encoding="utf-8") as handle:
            rows = csv.DictReader(handle)
            missing = {"sumo_x", "sumo_y", "hae_m"} - set(rows.fieldnames or ())
            if missing:
                raise ValueError(f"{Path(source).name} has no {', '.join(sorted(missing))} column, "
                                 "so its heights cannot be recomputed from its own coordinates")
            for row in rows:
                x, y = float(row["sumo_x"]), float(row["sumo_y"])
                recorded = float(row["hae_m"])
                difference = abs(round(self.recorded_height(x, y), 2) - recorded)
                report.records += 1
                report.worst_verification_m = max(report.worst_verification_m, difference)
                if difference <= self.tolerance_m:
                    report.verified += 1
                corrected = self.corrected_height(x, y)
                if corrected is None:
                    report.off_grid += 1
                else:
                    report.rewritten += 1
                    report.corrections_m.append(corrected - recorded)
        return report

    def _require(self, report: PatchReport, source: Path) -> None:
        if not report.records:
            raise ValueError(f"{source.name} holds no records")
        if report.verified_fraction < self.verified_fraction:
            raise ValueError(
                f"{source.name} was not produced by this grid, or has already been patched: only "
                f"{report.verified_fraction:.3%} of {report.records} rows reproduce their own "
                f"recorded height to {self.tolerance_m} m (worst "
                f"{report.worst_verification_m:.4f} m), against the "
                f"{self.verified_fraction:.1%} required")
