"""One ordered, bounded, idempotent path out of a run, whatever started it.

Plan 12 §3.10.2, D12.33. The order is the substance -- the corpus is made safe before anything is spent
on the world -- so it is asserted with the steps registered in the wrong order. A step that hangs on a
server the caller has killed must not hold the rest hostage, a step that fails must not stop the ones
after it, a second call must do nothing, and a second signal must abandon what is left. The signals are
raised in this process with `signal.raise_signal`, so the handlers under test are the real ones.
"""
from __future__ import annotations

import signal
import sys
import threading
import time
from pathlib import Path

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.RunTerminationSequence import (  # noqa: E402
    CLOSE_RECORD,
    DRAIN_CAPTURE,
    RELEASE_WORLD,
    WRITE_RESULT,
    RunTerminationSequence,
)


def test_steps_run_in_phase_order_whatever_order_they_were_registered_in():
    ran = []
    sequence = RunTerminationSequence(step_timeout_s=2.0)
    sequence.add_step(RELEASE_WORLD, "dispose", lambda: ran.append("dispose"), 1.0)
    sequence.add_step(WRITE_RESULT, "result", lambda: ran.append("result"))
    sequence.add_step(DRAIN_CAPTURE, "drain", lambda: ran.append("drain"), 1.0)
    sequence.add_step(CLOSE_RECORD, "close", lambda: ran.append("close"))
    sequence.run()
    assert ran == ["drain", "close", "result", "dispose"]


def test_within_a_phase_steps_run_by_their_order():
    ran = []
    sequence = RunTerminationSequence()
    sequence.add_step(RELEASE_WORLD, "session", lambda: ran.append("session"), 1.0, order=2)
    sequence.add_step(RELEASE_WORLD, "camera", lambda: ran.append("camera"), 1.0, order=1)
    sequence.add_step(RELEASE_WORLD, "orbit", lambda: ran.append("orbit"), 1.0, order=0)
    sequence.run()
    assert ran == ["orbit", "camera", "session"]


def test_running_it_twice_runs_each_step_once():
    ran = []
    sequence = RunTerminationSequence()
    sequence.add_step(WRITE_RESULT, "result", lambda: ran.append("result"))
    sequence.run()
    sequence.run()
    assert ran == ["result"]


def test_a_call_from_inside_a_step_returns_at_once():
    ran = []
    sequence = RunTerminationSequence()
    sequence.add_step(WRITE_RESULT, "reenter", lambda: (sequence.run(), ran.append("reenter")))
    sequence.add_step(RELEASE_WORLD, "after", lambda: ran.append("after"), 1.0)
    sequence.run()
    assert ran == ["reenter", "after"]


def test_a_hung_step_is_left_behind_and_the_rest_still_run():
    ran = []
    release = threading.Event()
    # The stuck step is freed after 3 s whatever happens, so a sequence that waits for it returns
    # late rather than never, and the timing below tells the two apart.
    threading.Timer(3.0, release.set).start()
    sequence = RunTerminationSequence()
    sequence.add_step(DRAIN_CAPTURE, "hangs", release.wait, 0.2)
    sequence.add_step(WRITE_RESULT, "result", lambda: ran.append("result"))
    started = time.monotonic()
    steps = sequence.run()
    elapsed = time.monotonic() - started
    release.set()
    assert ran == ["result"]
    assert elapsed < 2.0
    hung = next(s for s in steps if s.name == "hangs")
    assert not hung.completed and "did not finish" in hung.failure


def test_a_failing_step_does_not_stop_the_ones_after_it():
    ran = []
    sequence = RunTerminationSequence()

    def fails():
        raise RuntimeError("the server is gone")

    sequence.add_step(RELEASE_WORLD, "camera", fails, 1.0, order=1)
    sequence.add_step(RELEASE_WORLD, "session", lambda: ran.append("session"), 1.0, order=2)
    sequence.add_step(WRITE_RESULT, "inline failure", fails)
    sequence.run()
    assert ran == ["session"]
    report = {entry["step"]: entry for entry in sequence.report()}
    assert "server is gone" in report["camera"]["failure"]
    assert report["session"]["completed"]


def test_the_first_reason_to_stop_is_the_one_recorded():
    sequence = RunTerminationSequence()
    sequence.request_stop("window_end")
    sequence.request_stop("signal:SIGTERM")
    assert sequence.closed_by == "window_end" and sequence.stop_requested.is_set()


def test_a_signal_requests_a_stop_and_names_itself():
    sequence = RunTerminationSequence(exit_now=lambda _status: None)
    sequence.install()
    try:
        signal.raise_signal(signal.SIGINT)
    finally:
        sequence.uninstall()
    assert sequence.stop_requested.is_set()
    assert sequence.closed_by == "signal:SIGINT"
    assert not sequence.abandoned


def test_sigterm_is_handled_too():
    sequence = RunTerminationSequence(exit_now=lambda _status: None)
    sequence.install()
    try:
        signal.raise_signal(signal.SIGTERM)
    finally:
        sequence.uninstall()
    assert sequence.closed_by == "signal:SIGTERM"


def test_a_second_signal_abandons_the_rest_and_exits_at_once():
    exits = []
    ran = []
    sequence = RunTerminationSequence(exit_now=exits.append)
    sequence.install()
    try:
        signal.raise_signal(signal.SIGINT)
        sequence.add_step(DRAIN_CAPTURE, "second signal arrives",
                          lambda: signal.raise_signal(signal.SIGINT))
        sequence.add_step(RELEASE_WORLD, "never", lambda: ran.append("never"), 1.0)
        sequence.run()
    finally:
        sequence.uninstall()
    assert exits == [6]
    assert sequence.abandoned and ran == []


def test_a_second_signal_after_a_stop_that_was_not_a_signal_still_abandons():
    exits = []
    sequence = RunTerminationSequence(exit_now=exits.append)
    sequence.request_stop("window_end")
    sequence.install()
    try:
        signal.raise_signal(signal.SIGINT)
        signal.raise_signal(signal.SIGINT)
    finally:
        sequence.uninstall()
    assert exits == [6]
    assert sequence.closed_by == "window_end"


def test_uninstall_gives_the_handlers_back():
    before = signal.getsignal(signal.SIGINT)
    sequence = RunTerminationSequence()
    sequence.install()
    assert signal.getsignal(signal.SIGINT) != before
    sequence.uninstall()
    assert signal.getsignal(signal.SIGINT) == before
