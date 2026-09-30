"""Check 33 reads the admission pass for the window's begin, and the result keeps the window's passes.

Plan 12 §6.2 check 33: the vehicles inside the render region at the window's begin, against the render
cap, warned with the numbers. The session publishes a pass per SUMO step for the frame one step ahead
of the rendered clock (03 §8.8), so after the last prewarm step the newest pass is the window's second
frame, not its first; the check has to take the pass for the begin itself. The stand-in session
publishes passes on exactly that schedule, and the tests make the region busy at one frame only, so a
check that read the wrong pass would miss it or raise it where it does not hold.

A pre-roll warning is adjudicated by `on_warning` as a phase-0 one is, but nobody is asked (12 §6.4.2):
`refuse` refuses, `proceed` proceeds with its source recorded, an unattended caller with no
adjudication is refused at pre-roll, and an attended run proceeds with the warning loud and on record
unadjudicated.

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
CODE = "render_cap_bound_at_window_open"


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


def busy_at(frame_s: float, eligible: int = 140):
    """A region holding `eligible` vehicles at one SUMO frame and 7 at every other."""
    return lambda at: eligible if abs(at - frame_s) < 1e-6 else 7


def population_warnings(result) -> list[dict]:
    return [w for w in result.warnings if w.get("code") == CODE]


# -- which pass ----------------------------------------------------------------------------------------

def test_a_cap_binding_at_the_window_s_begin_is_a_warning_with_the_numbers(layout, caplog):
    server = FakeServer()
    server.eligible_at = busy_at(BEGIN_S)
    result = capture(layout, server)
    assert result.outcome == "run_finished"
    [warning] = population_warnings(result)
    assert warning["check"] == 33
    for figure in ("t=25200", "140 vehicles", "render_cap 128", "12 were not rendered"):
        assert figure in warning["message"]
    assert warning["adjudication"] is None
    # Under the cameras, the default, the eligible are the vehicles within reach of a view.
    assert "within reach of a channel camera's view" in warning["message"]
    assert any(f"LOUD {CODE}" in record.message for record in caplog.records)
    gate = next(g for g in result.produced["gates"] if g["id"] == "launch.warnings_adjudicated")
    assert (gate["observed"], gate["met"]) == (1, False)


def test_under_the_circle_the_warning_names_the_render_region(layout):
    server = FakeServer()
    server.eligible_at = busy_at(BEGIN_S)
    result = capture(layout, server, ["capture.render_set=circle"])
    [warning] = population_warnings(result)
    assert "140 vehicles were inside the render region" in warning["message"]
    assert "narrow capture.render_region" in warning["message"]


@pytest.mark.parametrize("frame_s", [BEGIN_S - 1.0, BEGIN_S + 1.0],
                         ids=["the last prewarm frame", "the lookahead frame"])
def test_the_check_reads_the_pass_for_the_begin_and_no_other(layout, frame_s):
    server = FakeServer()
    server.eligible_at = busy_at(frame_s)
    result = capture(layout, server)
    assert population_warnings(result) == []
    assert result.produced["admissions"]["at_window_open"]["sim_time_s"] == BEGIN_S
    assert result.produced["admissions"]["at_window_open"]["eligible"] == 7


def test_a_region_holding_exactly_the_cap_raises_nothing(layout):
    server = FakeServer()
    server.eligible_at = busy_at(BEGIN_S, eligible=128)
    result = capture(layout, server)
    assert population_warnings(result) == []


# -- how it is adjudicated ---------------------------------------------------------------------------

def test_an_unattended_caller_with_no_adjudication_is_refused_at_preroll(layout):
    server = FakeServer()
    server.eligible_at = busy_at(BEGIN_S)
    result = capture(layout, server, unattended(layout))
    assert (result.outcome, result.closed_by) == ("refused_preroll", "aborted_at_preroll")
    assert result.refusals[0]["check"] == 33 and "unattended" in result.refusals[0]["message"]
    assert server.events.of("start_recording") == []
    assert server.session.disposed


def test_an_adjudication_to_refuse_refuses_even_an_attended_run(layout):
    server = FakeServer()
    server.eligible_at = busy_at(BEGIN_S)
    result = capture(layout, server, [f'on_warning={{"{CODE}": "refuse"}}'])
    assert result.outcome == "refused_preroll"
    assert server.events.of("start_recording") == []


def test_an_adjudication_to_proceed_proceeds_and_names_its_source(layout):
    server = FakeServer()
    server.eligible_at = busy_at(BEGIN_S)
    result = capture(layout, server, [*unattended(layout), f'on_warning={{"{CODE}": "proceed"}}'])
    assert result.outcome == "run_finished"
    [warning] = population_warnings(result)
    assert warning["adjudication"] == "proceed"
    assert warning["adjudicated_by"].startswith("--set on_warning=")


# -- the window's passes in the result ---------------------------------------------------------------

def test_the_result_counts_the_window_s_passes_and_those_that_shed(layout):
    server = FakeServer()
    server.eligible_at = lambda at: 140 if 26000.0 <= at < 26010.0 else 7
    result = capture(layout, server)
    window = result.produced["admissions"]["window"]
    # One pass per SUMO step rendered inside 25200-27000: the frames 25201 to 27000.
    assert (window["passes"], window["first_frame_s"], window["last_frame_s"]) == \
        (1800, 25201.0, 27000.0)
    assert (window["passes_shedding"], window["most_shed"], window["most_eligible"]) == (10, 12, 140)
    assert result.produced["session"]["last_snapshot"]["admission"]["sim_time_s"] == 27001.0


def test_a_stopped_run_does_not_count_the_pass_it_never_rendered(layout):
    server = FakeServer()
    # Only the frame one step past the stop sheds; the run never renders the step it governs.
    server.eligible_at = busy_at(26001.0)

    def interrupt(session):
        if session.RenderedTimeSeconds == 26000.0:
            signal.raise_signal(signal.SIGINT)

    server.on_advance = interrupt
    result = capture(layout, server)
    assert result.outcome == "run_stopped"
    window = result.produced["admissions"]["window"]
    assert (window["passes"], window["passes_shedding"], window["last_frame_s"]) == \
        (800, 0, 26000.0)
