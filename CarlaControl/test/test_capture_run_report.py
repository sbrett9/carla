"""A recording that lost captures must not read like one that did not.

The native recorder counts what it drops -- the encoder queue is bounded and never blocks the stream
reader, so when every worker is busy the capture is discarded rather than delaying the frames behind
it -- but nothing read that counter. The message at the end of a recording named the saved count
alone, so a run that wrote every capture and a run that lost a third of them produced the same line.
A dropped capture is a missing still and a missing truth sidecar together, which is a gap a consumer
cannot see from the files.

The clock ratio is the other half. Simulated seconds per wall-clock second is what says whether a
window planned in simulation time is affordable in real time; the SUMO side of this pipeline has
reported it since it was written and the capture side reported nothing, so the two halves of one run
could not be compared.

Both are checked here against a recorder handle that stands in for the C# one, so no server and no
encoder are needed: what is being asserted is that the counters are read and said out loud.
"""
from __future__ import annotations

import logging
import sys
import time
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

_carlacontrol = pytest.importorskip(
    "carlacontrol",
    reason="carlacontrol needs the CarlaNet assemblies on this machine")
CaptureRunReport = _carlacontrol.CaptureRunReport
NativeRecorder = _carlacontrol.NativeRecorder


class _Handle:
    """The counters the C# FrameRecorder publishes, which is all the recorder reads off it."""

    def __init__(self, saved: int, dropped: int):
        self.Saved = saved
        self.Dropped = dropped
        self.HaveTelemetryOrigin = True
        self.MeasuresOcclusion = False


class _World:
    """A world whose simulation clock can be moved by hand."""

    def __init__(self, sim_time: float = 0.0):
        self.sim_time = sim_time
        self.stopped = False

    def get_sim_time(self) -> float:
        return self.sim_time

    def stop_recording(self) -> None:
        self.stopped = True


class _Arguments:
    """The arguments NativeRecorder reads when it is constructed."""

    record_dir = "unused"
    record_hz = 2.0
    affiliation = "n"
    stale = 3.0
    fov = 90.0
    platform_type = "uas-fixed"
    platform_affiliation = "f"
    platform_callsign = "OVERWATCH"
    platform_uid = None
    scenario = None
    scenario_id = None
    seed = None
    occlusion = False


def _recording(world: _World, saved: int, dropped: int, started_sim_time: float = 0.0,
               wall_seconds: float = 10.0) -> NativeRecorder:
    """A recorder mid-recording, with its window already open on both clocks."""
    recorder = NativeRecorder(world, camera=None, args=_Arguments())
    recorder._handle = _Handle(saved, dropped)
    recorder.recording = True
    recorder._started_sim_time = started_sim_time
    recorder._started_wall_time = time.monotonic() - wall_seconds
    return recorder


def test_the_report_carries_both_counts_and_both_clocks():
    world = _World(sim_time=300.0)
    report = _recording(world, saved=600, dropped=42, wall_seconds=400.0).report()

    assert report.saved == 600
    assert report.dropped == 42
    assert report.sim_seconds == pytest.approx(300.0)
    assert report.wall_seconds == pytest.approx(400.0, abs=1.0)
    assert report.achieved_real_time_factor == pytest.approx(0.75, abs=0.01)


def test_a_run_with_drops_does_not_read_like_a_clean_one():
    """The property the old message could not have: two runs, one lossy, distinguishable."""
    clean = _recording(_World(sim_time=60.0), saved=120, dropped=0).report()
    lossy = _recording(_World(sim_time=60.0), saved=120, dropped=60).report()

    # The saved count is identical, which is exactly why naming it alone said nothing.
    assert clean.saved == lossy.saved
    assert clean.describe() != lossy.describe()
    assert "0 dropped" in clean.describe()
    assert "60 dropped" in lossy.describe()
    # 60 lost of the 180 the recorder reached for, which is the third the docstring above names.
    assert lossy.dropped_fraction == pytest.approx(1.0 / 3.0)


def test_stopping_says_what_was_saved_lost_and_how_the_clocks_ran(caplog):
    world = _World(sim_time=120.0)
    recorder = _recording(world, saved=240, dropped=0, wall_seconds=150.0)

    with caplog.at_level(logging.INFO, logger="carlacontrol.NativeRecorder"):
        recorder.stop()

    assert world.stopped
    logged = "\n".join(record.getMessage() for record in caplog.records)
    assert "240 capture(s) saved, 0 dropped" in logged
    assert "120.0 s of simulation" in logged
    assert "real time" in logged


def test_dropped_captures_are_reported_as_a_warning(caplog):
    recorder = _recording(_World(sim_time=60.0), saved=100, dropped=7)

    with caplog.at_level(logging.INFO, logger="carlacontrol.NativeRecorder"):
        recorder.stop()

    warnings = [r.getMessage() for r in caplog.records if r.levelno >= logging.WARNING]
    assert len(warnings) == 1
    assert "7 capture(s) were discarded" in warnings[0]
    assert "truth sidecar" in warnings[0]


def test_a_clean_run_raises_no_warning(caplog):
    recorder = _recording(_World(sim_time=60.0), saved=100, dropped=0)

    with caplog.at_level(logging.INFO, logger="carlacontrol.NativeRecorder"):
        recorder.stop()

    assert [r.getMessage() for r in caplog.records if r.levelno >= logging.WARNING] == []


def test_toggling_the_recorder_off_reports_the_same_way(caplog):
    """The hotkey path and the shutdown path must not disagree about what a run produced."""
    recorder = _recording(_World(sim_time=30.0), saved=60, dropped=3)
    recorder.want_enabled = False

    with caplog.at_level(logging.INFO, logger="carlacontrol.NativeRecorder"):
        recorder.apply_want()

    logged = "\n".join(record.getMessage() for record in caplog.records)
    assert "60 capture(s) saved, 3 dropped" in logged
    assert not recorder.recording


def test_counters_read_from_a_released_handle_are_zero_rather_than_an_error():
    recorder = NativeRecorder(_World(), camera=None, args=_Arguments())
    assert recorder.saved == 0
    assert recorder.dropped == 0


def test_a_ratio_with_no_wall_clock_behind_it_is_zero_rather_than_a_division():
    empty = CaptureRunReport()
    assert empty.achieved_real_time_factor == 0.0
    assert empty.dropped_fraction == 0.0
