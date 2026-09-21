"""An anomaly that is an absence has to reach a consumer, or it is not truth at all.

A guard who never arrives has no vehicle. The signal is that fifteen towers were relieved and the
sixteenth was not, so there is nothing in the telemetry to attach the assertion to: the scenario
generator wrote it into the labels sidecar as free text, and the only reader of that file took
`marked_ids` and `affiliation_by_type` and nothing else. No artifact the pipeline produced mentioned
the gap. An absence recorded where nothing reads it is a comment, not ground truth.

It is routed rather than deleted, because a described gap is the only record that an absence was
authored at all, and the single one this pipeline has today covers eight hours of one shift out of
three hundred and thirty-five postings that were relieved normally.

Two things are asserted. The gap reaches a file, with the window placed on the epoch the run stamped
rather than left in scenario seconds nobody can locate. And it reaches a file *beside* the dataset
rather than inside it: a note naming which post stood unmanned between which hours is the answer to
the question the dataset asks, so it belongs on the truth side with `marked_ids`.
"""
from __future__ import annotations

import json
import logging
import sys
from datetime import UTC, datetime
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))
sys.path.insert(0, str(_REPO / "CarlaControl" / "scripts"))

import sumo_cot_telemetry  # noqa: E402  (a CLI, imported for the routing it performs)

from carlacontrol.SumoCotBridge import (  # noqa: E402
    AUTHORED_TRUTH_FIELDS,
    CSV_COLUMNS,
    RunReport,
)
from carlacontrol.SupervisionSidecar import LABELS_KEY, SupervisionSidecar  # noqa: E402

EPOCH = datetime(2026, 8, 28, 18, 0, 0, tzinfo=UTC)

# The gap the Shahid Bahonar scenario writes: one tower left unmanned for a shift on day four,
# beginning at 07:00 of that day in scenario seconds.
GUARD_NO_SHOW = {
    "kind": "guard_no_show", "tower_index": 3,
    "edge": "-1051684919#1", "edge_pos_m": 21.5,
    "begin_s": 4 * 86400 + 7 * 3600, "end_s": 4 * 86400 + 15 * 3600,
    "note": "one tower left unmanned for a shift; the detectable signal is a missing guard arrival "
            "while the other fifteen towers are relieved as usual",
}

LABELS = {"marked_ids": ["probe_d2", "shadow"],
          "affiliation_by_type": {"civilian_car": "n"},
          LABELS_KEY: [GUARD_NO_SHOW]}


@pytest.fixture
def sidecar() -> SupervisionSidecar:
    return SupervisionSidecar.from_labels(
        LABELS, scenario="Shahid_Bahonar_Port_PatternOfLife", epoch=EPOCH,
        labels_path=Path("Shahid_Bahonar_Port_PatternOfLife.labels.json"))


def test_labels_with_no_described_gaps_produce_nothing(tmp_path):
    """Most scenarios describe none, and they must not grow an empty file for it."""
    empty = SupervisionSidecar.from_labels({"marked_ids": ["orbiter"]}, "orbit", EPOCH)
    assert len(empty) == 0
    assert not empty


def test_the_gap_reaches_a_file(sidecar, tmp_path):
    path = sidecar.write(tmp_path / "corpus.supervision.json")
    record = json.loads(path.read_text(encoding="utf-8"))

    assert record["scenario"] == "Shahid_Bahonar_Port_PatternOfLife"
    assert record["source_labels"] == "Shahid_Bahonar_Port_PatternOfLife.labels.json"
    assert len(record["supervision_gaps"]) == 1
    assert record["supervision_gaps"][0]["kind"] == "guard_no_show"


def test_the_window_is_placed_on_the_runs_own_clock(sidecar, tmp_path):
    """Scenario seconds locate nothing without the epoch, which only the run knows."""
    gap = json.loads(sidecar.write(tmp_path / "corpus.supervision.json")
                     .read_text(encoding="utf-8"))["supervision_gaps"][0]

    # 4 days and 7 hours after an epoch of 2026-08-28T18:00Z.
    assert gap["begin_utc"].startswith("2026-09-02T01:00:00")
    assert gap["end_utc"].startswith("2026-09-02T09:00:00")
    # And the author's own record is carried through untouched beside it.
    for key, value in GUARD_NO_SHOW.items():
        assert gap[key] == value


def test_a_note_with_no_window_still_reaches_the_file(sidecar, tmp_path):
    """Free text is what the generator writes; a gap without times must not be dropped for it."""
    loose = SupervisionSidecar([{"kind": "unspecified", "note": "something did not happen"}],
                               "scenario", EPOCH)
    gap = json.loads(loose.write(tmp_path / "loose.supervision.json")
                     .read_text(encoding="utf-8"))["supervision_gaps"][0]

    assert gap["note"] == "something did not happen"
    assert "begin_utc" not in gap


def test_the_gap_is_named_in_a_line_an_operator_can_read(sidecar):
    described = sidecar.describe()
    assert "guard_no_show" in described
    assert "2026-09-02T01:00:00" in described
    assert "unmanned" in described


def test_the_sidecar_sits_beside_the_dataset_and_not_inside_it():
    """Where it goes is the decision: this is an answer key, so it may not join the corpus.

    The written dataset already refuses the per-vehicle authored fields. A described gap is the same
    kind of thing at scenario scope, so it takes the same route -- its own file -- rather than being
    added to a column or an event attribute where a consumer would read it as observation.
    """
    assert SupervisionSidecar.default_path("run/corpus.xml") == Path("run/corpus.supervision.json")
    assert SupervisionSidecar.default_path("run/corpus.csv") == Path("run/corpus.supervision.json")

    # Nothing about the gap has a home in either consumer channel, which is why it needs one.
    assert "supervision_gaps" not in CSV_COLUMNS
    assert "note" not in CSV_COLUMNS
    # The written CSV is the truth sidecar and carries the authoring fields; what stays out of
    # it is the described absence, which has no vehicle and therefore no row to sit in.
    assert set(AUTHORED_TRUTH_FIELDS) <= set(CSV_COLUMNS)


class _Arguments:
    """The arguments the telemetry tool reads when it routes a scenario's described gaps."""

    def __init__(self, config: Path, xml: Path | None = None, csv: Path | None = None,
                 supervision: Path | None = None, labels: Path | None = None):
        self.config = config
        self.xml = xml
        self.csv = csv
        self.supervision = supervision
        self.labels = labels


def test_the_telemetry_tool_writes_the_gap_beside_its_dataset(tmp_path):
    """The routing itself: the tool read marked_ids and affiliation_by_type and nothing else."""
    arguments = _Arguments(config=tmp_path / "Shahid_Bahonar_Port_PatternOfLife.sumocfg",
                           xml=tmp_path / "corpus.xml")
    sumo_cot_telemetry._write_supervision(arguments, LABELS, RunReport(epoch=EPOCH))

    written = tmp_path / "corpus.supervision.json"
    assert written.exists()
    record = json.loads(written.read_text(encoding="utf-8"))
    assert record["supervision_gaps"][0]["kind"] == "guard_no_show"
    assert record["supervision_gaps"][0]["begin_utc"].startswith("2026-09-02T01:00:00")


def test_a_run_with_no_described_gaps_writes_no_sidecar(tmp_path):
    arguments = _Arguments(config=tmp_path / "orbit.sumocfg", xml=tmp_path / "corpus.xml")
    sumo_cot_telemetry._write_supervision(arguments, {"marked_ids": ["orbiter"]},
                                          RunReport(epoch=EPOCH))

    assert list(tmp_path.glob("*.supervision.json")) == []


def test_a_live_feed_with_nowhere_to_write_says_so(tmp_path, caplog):
    """A run that writes no files still has an operator, and the gap is said out loud to them."""
    arguments = _Arguments(config=tmp_path / "Shahid_Bahonar_Port_PatternOfLife.sumocfg")
    with caplog.at_level(logging.WARNING):
        sumo_cot_telemetry._write_supervision(arguments, LABELS, RunReport(epoch=EPOCH))

    warnings = [record.getMessage() for record in caplog.records
                if record.levelno >= logging.WARNING]
    assert len(warnings) == 1
    assert "guard_no_show" in warnings[0]
    assert "reached no file" in warnings[0]


def test_the_shipped_bahonar_labels_route_through_unchanged(tmp_path):
    """The gap this pipeline actually has, read from the labels the generator wrote for it."""
    labels = {LABELS_KEY: [{"kind": "guard_no_show", "tower_index": 3, "edge": "26413459",
                            "edge_pos_m": 58.9, "begin_s": 370800, "end_s": 399600,
                            "note": "one tower left unmanned for a shift; the detectable signal is "
                                    "a missing guard arrival while the other fifteen towers are "
                                    "relieved as usual"}]}
    sidecar = SupervisionSidecar.from_labels(labels, "Shahid_Bahonar_Port_PatternOfLife", EPOCH)
    gap = json.loads(sidecar.write(tmp_path / "bahonar.supervision.json")
                     .read_text(encoding="utf-8"))["supervision_gaps"][0]

    # 370 800 s is 07:00 on day four; the shift it covers is eight hours long.
    assert gap["begin_utc"].startswith("2026-09-02T01:00:00")
    assert gap["end_utc"].startswith("2026-09-02T09:00:00")
    assert gap["edge"] == "26413459"
