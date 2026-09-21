"""Carry a scenario's described absences into the run that was supposed to contain them.

Most of what a scenario asserts is attached to a vehicle, and a vehicle can be found in the output by
its id. Some of it is not. A guard who never arrives has no vehicle: the signal is that fifteen towers
were relieved and the sixteenth was not, and there is nothing in the telemetry to hang the assertion
on. The scenario generator writes those as `anomaly_notes` in its labels sidecar, and the telemetry
tool read `marked_ids` and `affiliation_by_type` and never looked at them, so no artifact the pipeline
produced mentioned the gap at all. An absence recorded where nothing reads it is not ground truth; it
is a comment.

This routes them. Each note is carried through verbatim -- the author's words are the record -- and
given the one thing the labels file cannot know: where the described window falls on the run's own
clock, resolved through the epoch that run was stamped with. A consumer holding a corpus can then find
the window in it.

**It is written beside the corpus, not into it.** The events and rows a consumer reads are the
observation channel, and a note saying which tower stood unmanned between which hours is the answer to
the question a behavioural model is being asked. It goes in its own file, on the truth side, exactly
as `marked_ids` does.

The shape here is a carry-forward, not a schema: one record per described gap, the author's keys
untouched, plus the resolved window. When supervision records proper arrive -- a recurring series with
an unrealised slot, which can say that the vacancy is one of three hundred and thirty-five postings
rather than a sentence of English -- they replace this rather than extending it.
"""
from __future__ import annotations

import json
from collections.abc import Sequence
from datetime import datetime, timedelta
from pathlib import Path

from carlacontrol.CotUdpEmitter import CotUdpEmitter

# The labels-file key the scenario generator writes these under. The key is the generator's; what
# they are is a described supervision gap, which is what this writes them out as.
LABELS_KEY = "anomaly_notes"

# Where a note carries the window it describes, in seconds of simulation from the scenario's own zero.
BEGIN_SECONDS = "begin_s"
END_SECONDS = "end_s"


class SupervisionSidecar:
    """The described absences a scenario asserts, pinned to the run that was meant to contain them."""

    def __init__(self, gaps: Sequence[dict], scenario: str, epoch: datetime,
                 labels_path: Path | None = None):
        self.gaps = list(gaps)
        self.scenario = scenario
        self.epoch = epoch
        self.labels_path = labels_path

    @classmethod
    def from_labels(cls, labels: dict, scenario: str, epoch: datetime,
                    labels_path: Path | None = None) -> SupervisionSidecar:
        """Read the described gaps out of a loaded `*.labels.json`."""
        return cls(labels.get(LABELS_KEY) or [], scenario, epoch, labels_path)

    def __len__(self) -> int:
        return len(self.gaps)

    def record(self) -> dict:
        """The sidecar's contents: the run it belongs to, and every gap with its resolved window."""
        return {
            "scenario": self.scenario,
            "epoch": CotUdpEmitter.format_cot_timestamp(self.epoch),
            "source_labels": self.labels_path.name if self.labels_path else None,
            "supervision_gaps": [self._resolved(gap) for gap in self.gaps],
        }

    def write(self, path: str | Path) -> Path:
        """Write the sidecar and return where it went."""
        path = Path(path)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(self.record(), indent=1), encoding="utf-8")
        return path

    def describe(self) -> str:
        """One line per gap, for the log of a run that has no file sink to write to."""
        if not self.gaps:
            return "the scenario describes no supervision gaps"
        return "\n".join(
            f"{gap.get('kind', 'gap')} from {self._stamp(gap, BEGIN_SECONDS)} to "
            f"{self._stamp(gap, END_SECONDS)}: {gap.get('note', '')}".rstrip(": ")
            for gap in self.gaps)

    @staticmethod
    def default_path(output_path: str | Path) -> Path:
        """Where the sidecar goes when nobody names a place: beside the run's own output."""
        output_path = Path(output_path)
        return output_path.with_name(output_path.stem + ".supervision.json")

    # -- resolving ---------------------------------------------------------------------------

    def _resolved(self, gap: dict) -> dict:
        """The author's record, plus the window on the run's clock rather than the scenario's."""
        resolved = dict(gap)
        for key, name in ((BEGIN_SECONDS, "begin_utc"), (END_SECONDS, "end_utc")):
            stamp = self._stamp(gap, key)
            if stamp:
                resolved[name] = stamp
        return resolved

    def _stamp(self, gap: dict, key: str) -> str:
        """One end of a gap's window as a wall-clock instant, or empty if the note gives none."""
        seconds = gap.get(key)
        if seconds is None:
            return ""
        return CotUdpEmitter.format_cot_timestamp(self.epoch + timedelta(seconds=float(seconds)))
