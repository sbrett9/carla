"""A capture run ends the same way whatever ends it, and the corpus is safe before the world is touched.

Plan 12 §3.10.2 and §7.1: a window's end, a signal, a loud condition under an unattended caller and
write headroom running out all take `RunTerminationSequence`'s one path -- drain every recorder, take
the closing record, write the result, and only then destroy the cameras and dispose the session. The
order is read off the stand-ins' shared event log, and the session's disposal records whether the
result was already on disk when it was called. A signal is raised in this process from inside a
simulation step, as a caller's would arrive, and the run is expected to stop at the next step
boundary with the signal named.
"""
from __future__ import annotations

import io
import itertools
import json
import signal
import sys
from collections import namedtuple
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))
sys.path.insert(0, str(_REPO / "CarlaControl" / "test"))

from CaptureFakes import FakeServer  # noqa: E402
from RunCaptureFixture import Layout, run_document  # noqa: E402

from carlacontrol.CaptureSession import CaptureSession  # noqa: E402
from carlacontrol.RunConfigurationValidator import RunConfigurationValidator  # noqa: E402
from carlacontrol.RunResult import RunResult  # noqa: E402
from carlacontrol.RunTerminationSequence import RunTerminationSequence  # noqa: E402
from carlacontrol.SessionMonitor import SessionMonitor  # noqa: E402
from carlacontrol.SiteProfile import SiteProfile  # noqa: E402

Usage = namedtuple("Usage", "total used free")
SESSION_ID = "cap-term"


@pytest.fixture
def layout(tmp_path: Path) -> Layout:
    return Layout(tmp_path)


def capture(layout: Layout, server: FakeServer, overrides=(), disk=None, clock=None):
    run_path = layout.root / "fixture.run.json"
    run_path.write_text(json.dumps(run_document()), encoding="utf-8")
    site = SiteProfile.discover(layout.root, layout.write_profile(), environ={})
    free = disk or (lambda _path: Usage(10**13, 0, 10**13))
    session = CaptureSession(
        site, run_path, [(text, f"--set {text}") for text in overrides],
        client_factory=server.client, validator=RunConfigurationValidator(disk_usage=free),
        termination=RunTerminationSequence(step_timeout_s=5.0, exit_now=lambda _s: None),
        monitor=SessionMonitor(stream=io.StringIO(), is_terminal=False),
        stdin_is_terminal=lambda: False, session_id=SESSION_ID,
        clock=clock or itertools.count(0.0, 0.01).__next__)
    server.result_path = layout.runs_root / SESSION_ID / "run.result.json"
    return session.run()


def test_the_corpus_is_safe_before_the_world_is_touched(layout):
    server = FakeServer()
    capture(layout, server)
    names = server.events.names()
    drained = names.index("stop_recording")
    destroyed = [i for i, name in enumerate(names) if name == "destroy"]
    disposed = names.index("dispose")
    assert drained < min(destroyed) and max(destroyed) < disposed
    assert server.events.of("dispose") == [("dispose", True)], "the result was not on disk yet"


def test_a_signal_mid_window_stops_the_run_at_the_next_step_naming_the_signal(layout):
    server = FakeServer()

    def interrupt(session):
        if session.RenderedTimeSeconds == 26000.0:
            signal.raise_signal(signal.SIGINT)

    server.on_advance = interrupt
    result = capture(layout, server)
    assert (result.outcome, result.closed_by, result.exit_status) == \
        ("run_stopped", "signal:SIGINT", 6)
    window = result.produced["window"]
    assert window["end_reached_s"] == 26000.0 and window["end_declared_s"] == 27000.0
    assert server.events.of("stop_recording") and server.session.disposed
    written = RunResult.read(layout.runs_root / SESSION_ID / "run.result.json")
    assert written["closed_by"] == "signal:SIGINT"
    assert written["produced"]["channels"][0]["written"] == 1600


def test_a_signal_during_the_prewarm_stops_before_anything_records(layout):
    server = FakeServer()

    def interrupt(session):
        if session.RenderedTimeSeconds == 25000.0:
            signal.raise_signal(signal.SIGTERM)

    server.on_advance = interrupt
    result = capture(layout, server)
    assert (result.outcome, result.closed_by) == ("run_stopped", "signal:SIGTERM")
    assert server.events.of("start_recording") == []
    assert server.session.disposed and all(actor.destroyed for actor in server.actors)


def test_an_unattended_loud_condition_ends_the_run(layout):
    server = FakeServer()
    server.drops_per_step = 1
    result = capture(layout, server, ["caller=unattended",
                                      f"result_path={layout.runs_root / SESSION_ID / 'run.result.json'}"])
    assert (result.outcome, result.closed_by) == ("run_stopped", "loud:recorder_dropped")


def test_an_attended_loud_condition_is_said_and_the_run_goes_on(layout, caplog):
    server = FakeServer()
    server.drops_per_step = 1
    result = capture(layout, server)
    assert result.outcome == "run_finished"
    assert any("LOUD recorder_dropped" in record.message for record in caplog.records)
    dropped = next(g for g in result.produced["gates"]
                   if g["id"] == "capture.recorder_dropped[OVERWATCH-1]")
    assert dropped["met"] is False and dropped["observed"] == 1800


def test_write_headroom_running_out_stops_the_run_cleanly(layout):
    server = FakeServer()
    calls = itertools.count()

    def disk(_path):
        free = 10**13 if next(calls) < 3 else 10**6
        return Usage(free, 0, free)

    result = capture(layout, server, disk=disk, clock=itertools.count(0.0, 1.0).__next__)
    assert (result.outcome, result.closed_by) == ("run_stopped", "write_headroom")


def test_the_result_points_at_what_the_run_wrote_and_how_it_shut_down(layout):
    server = FakeServer()
    result = capture(layout, server)
    written = RunResult.read(result.path)
    assert Path(written["lock"]).is_file() and Path(written["resolution_report"]).is_file()
    assert Path(written["effective_configuration"]).is_file()
    produced = written["produced"]
    assert produced["closed_by"] == "window_end"
    assert produced["window"]["civil"] == ["2026-03-21T07:00:00-07:00",
                                           "2026-03-21T07:30:00-07:00"]
    assert produced["recorder_run_id"] == SESSION_ID
    steps = [entry["step"] for entry in produced["termination"]]
    assert steps.index("drain recorder OVERWATCH-1") < steps.index("write the run result") \
        < steps.index("destroy camera OVERWATCH-1") < steps.index("dispose the session")
    assert written["launch_echo"]["civil"]["begin"] == "2026-03-21T07:00:00-07:00"
