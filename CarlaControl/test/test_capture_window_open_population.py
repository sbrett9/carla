"""The result keeps the admission pass for the window's begin and a summary of the window's passes.

The session publishes a pass per SUMO step for the frame one step ahead of the rendered clock
(03 §8.8), so after the last prewarm step the newest pass is the window's second frame, not its
first; the pass kept for the window's opening has to be the one for the begin itself. The stand-in
session publishes passes on exactly that schedule, and the tests make the population large at one
frame only, so a reader that took the wrong pass would show it.

Every vehicle SUMO has is rendered, so a crowded window is drawn in full and warned about nowhere:
there is no cap for it to bind, and run check 33, which compared the two, is retired.

The window's summary counts a pass once the step it governs has been rendered, so a run stopped early
never counts the lookahead pass it did not render.
"""
from __future__ import annotations

import io
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
from carlacontrol.RunTerminationSequence import RunTerminationSequence  # noqa: E402
from carlacontrol.SessionMonitor import SessionMonitor  # noqa: E402
from carlacontrol.SiteProfile import SiteProfile  # noqa: E402

Usage = namedtuple("Usage", "total used free")
SESSION_ID = "cap-population"
BEGIN_S = 25200.0


@pytest.fixture
def layout(tmp_path: Path) -> Layout:
    return Layout(tmp_path)


def capture(layout: Layout, server: FakeServer, overrides=()):
    run_path = layout.root / "fixture.run.json"
    run_path.write_text(json.dumps(run_document()), encoding="utf-8")
    site = SiteProfile.discover(layout.root, layout.write_profile(), environ={})
    session = CaptureSession(
        site, run_path, [(text, f"--set {text}") for text in overrides],
        client_factory=server.client,
        validator=RunConfigurationValidator(disk_usage=lambda _p: Usage(10**13, 0, 10**13)),
        termination=RunTerminationSequence(step_timeout_s=5.0, exit_now=lambda _s: None),
        monitor=SessionMonitor(stream=io.StringIO(), is_terminal=False),
        stdin_is_terminal=lambda: False, session_id=SESSION_ID)
    server.result_path = layout.runs_root / SESSION_ID / "run.result.json"
    return session.run()


def unattended(layout: Layout) -> list[str]:
    return ["caller=unattended",
            f"result_path={layout.runs_root / SESSION_ID / 'run.result.json'}"]


def busy_at(frame_s: float, population: int = 400):
    """`population` vehicles at one SUMO frame and 7 at every other."""
    return lambda at: population if abs(at - frame_s) < 1e-6 else 7


# -- which pass ----------------------------------------------------------------------------------------

def test_a_crowded_window_opening_is_drawn_in_full_and_warned_about_nowhere(layout):
    # Unattended, with no adjudication written: any warning would refuse the run.
    server = FakeServer()
    server.population_at = busy_at(BEGIN_S)
    result = capture(layout, server, unattended(layout))
    assert result.outcome == "run_finished"
    assert result.warnings == [] and result.refusals == []
    opening = result.produced["admissions"]["at_window_open"]
    assert (opening["sim_time_s"], opening["population"]) == (BEGIN_S, 400)


@pytest.mark.parametrize("frame_s", [BEGIN_S - 1.0, BEGIN_S + 1.0],
                         ids=["the last prewarm frame", "the lookahead frame"])
def test_the_pass_kept_for_the_window_s_opening_is_the_begin_s_and_no_other(layout, frame_s):
    server = FakeServer()
    server.population_at = busy_at(frame_s)
    result = capture(layout, server)
    opening = result.produced["admissions"]["at_window_open"]
    assert (opening["sim_time_s"], opening["population"]) == (BEGIN_S, 7)


# -- the window's passes in the result ---------------------------------------------------------------

def test_the_result_counts_the_window_s_passes_and_its_largest_population(layout):
    server = FakeServer()
    server.population_at = lambda at: 400 if 26000.0 <= at < 26010.0 else 7
    result = capture(layout, server)
    window = result.produced["admissions"]["window"]
    # One pass per SUMO step rendered inside 25200-27000: the frames 25201 to 27000.
    assert (window["passes"], window["first_frame_s"], window["last_frame_s"]) == \
        (1800, 25201.0, 27000.0)
    assert window["most_population"] == 400
    assert result.produced["session"]["last_snapshot"]["admission"]["sim_time_s"] == 27001.0


def test_a_stopped_run_does_not_count_the_pass_it_never_rendered(layout):
    server = FakeServer()
    # Only the frame one step past the stop is crowded; the run never renders the step it governs.
    server.population_at = busy_at(26001.0)

    def interrupt(session):
        if session.RenderedTimeSeconds == 26000.0:
            signal.raise_signal(signal.SIGINT)

    server.on_advance = interrupt
    result = capture(layout, server)
    assert result.outcome == "run_stopped"
    window = result.produced["admissions"]["window"]
    assert (window["passes"], window["most_population"], window["last_frame_s"]) == \
        (800, 7, 26000.0)
