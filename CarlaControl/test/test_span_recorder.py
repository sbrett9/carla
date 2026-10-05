"""A flown camera records in spans, each to its own folder, each started once the camera's tiles are in.

`SpanRecorder` is the F key of the free view `run_sumo_drive.py --view free` opens
(`12_Operator_Control_Surface.md` section 9.6). A camera flown to new ground renders it while
Cesium is still streaming it, so a span waits for the server to say the camera's tiles are in
before it starts, gives up at the 90 s ceiling of `03_CoSimulation_Runtime.md` section 9.5.1, and
never starts before the capture window opens. The native recorder, the server's readiness answers
and the clocks are stand-ins here, so each rule is asserted exactly; one test runs the recorder's
own thread to show that starting and stopping happen there and not on the caller's.
"""
from __future__ import annotations

import logging
import sys
import threading
from datetime import UTC, datetime
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.SpanRecorder import SpanRecorder  # noqa: E402  (needs the path above)

SENSOR = "CARLA-SENSOR-7"
STARTED = datetime(2026, 9, 30, 14, 22, 33, tzinfo=UTC)


def _readiness(frame: int, progress: float = 100.0, failed_in_view: int = 0,
               published: bool = True, visible: bool = True) -> dict:
    """A get_view_readiness answer with one photoreal tileset and one hidden ground tileset."""
    return {"frame": frame, "published": published, "tilesets": [
        {"ion_asset_id": 2275207, "visible": visible, "load_progress": progress,
         "loading": {"worker_queue": 0, "main_queue": 0, "kicked": 0},
         "failed_in_view": failed_in_view, "failed_loaded": failed_in_view},
        {"ion_asset_id": 1, "visible": False, "load_progress": 12.0,
         "loading": {"worker_queue": 3, "main_queue": 0, "kicked": 0},
         "failed_in_view": 0, "failed_loaded": 0},
    ]}


class _Handle:
    """A FrameRecorder's counters, as the heads-up display and the closing report read them."""

    def __init__(self, directory: str) -> None:
        self.directory = directory
        self.Saved = 0
        self.Dropped = 0
        self.PairsRenderSet = True
        self.RenderSetPaired = 0
        self.RenderSetUnpaired = 0


class _Clock:
    def __init__(self) -> None:
        self.now = 500.0

    def __call__(self) -> float:
        return self.now


class _Rig:
    """The recorder's surroundings: the native recorder, the server's answers, the capture window."""

    def __init__(self, tmp_path: Path, answers: list | None = None, window_opens: bool = True,
                 readiness: bool = True) -> None:
        self.clock = _Clock()
        self.answers = list(answers or [])
        self.window_opens = window_opens
        self.started: list[str] = []
        self.start_threads: list[int] = []
        self.stopped = 0
        self.stop_threads: list[int] = []
        self.closed: list[tuple[_Handle, Path]] = []
        self.handles: list[_Handle] = []
        self.asked = 0
        self.recorder = SpanRecorder(
            tmp_path, SENSOR, 2.0,
            start_recording=self.start, stop_recording=self.stop,
            view_readiness=self.readiness if readiness else None,
            may_record=self.may_record,
            on_closed=lambda handle, directory: self.closed.append((handle, directory)),
            clock=self.clock, utc_now=lambda: STARTED)

    def start(self, directory: str) -> _Handle:
        self.started.append(directory)
        self.start_threads.append(threading.get_ident())
        handle = _Handle(directory)
        self.handles.append(handle)
        return handle

    def stop(self) -> None:
        self.stopped += 1
        self.stop_threads.append(threading.get_ident())
        # What a flush leaves behind: every capture the span took, counted.
        self.handles[-1].Saved = 24
        self.handles[-1].RenderSetPaired = 24

    def readiness(self) -> dict:
        self.asked += 1
        answer = self.answers.pop(0) if len(self.answers) > 1 else self.answers[0]
        if isinstance(answer, Exception):
            raise answer
        return answer

    def may_record(self) -> str | None:
        return None if self.window_opens else "the capture window opens at t=310 s"

    def notices(self) -> list[str]:
        out = []
        while not self.recorder.notices.empty():
            out.append(self.recorder.notices.get_nowait())
        return out


def test_a_span_waits_for_the_tiles_then_records_into_a_folder_named_by_the_camera_and_its_start(
        tmp_path):
    rig = _Rig(tmp_path, [_readiness(10, 40.0), _readiness(11, 87.0), _readiness(12, 100.0)])
    recorder = rig.recorder

    recorder.toggle_want()
    recorder.step()
    assert recorder.waiting and not recorder.recording
    assert recorder.tiles == "40%"
    rig.clock.now += 0.25
    recorder.step()
    assert recorder.waiting
    assert recorder.tiles == "87%"
    assert recorder.waiting_s == pytest.approx(0.25)
    assert rig.started == []

    rig.clock.now += 0.25
    recorder.step()

    assert recorder.recording and not recorder.waiting
    expected = tmp_path / "CARLA-SENSOR-7-20260930T142233Z"
    assert rig.started == [str(expected)]
    assert expected.is_dir()
    assert recorder.directory == expected
    assert rig.notices()[-1] == f"recording -> {expected.name}"


def test_tiles_reported_in_on_the_first_answer_are_not_trusted_until_a_later_frame(tmp_path):
    # The first answer is of the tick before the key was pressed, which may be of a pose the camera
    # has since left. A span starts on a frame rendered after the press.
    rig = _Rig(tmp_path, [_readiness(10), _readiness(10), _readiness(11)])
    recorder = rig.recorder

    recorder.toggle_want()
    recorder.step()
    recorder.step()
    assert recorder.waiting
    assert rig.started == []

    recorder.step()
    assert recorder.recording


def test_the_wait_gives_up_at_its_ceiling_and_starts_nothing(tmp_path, caplog):
    rig = _Rig(tmp_path, [_readiness(10, 62.0), _readiness(11, 62.0)])
    recorder = rig.recorder

    recorder.toggle_want()
    recorder.step()
    rig.clock.now += 45.0
    recorder.step()
    assert recorder.waiting
    rig.clock.now += 45.0
    with caplog.at_level(logging.WARNING):
        recorder.step()

    assert not recorder.waiting and not recorder.recording
    assert not recorder.want_enabled
    assert rig.started == []
    assert "not in within 90 s (tiles 62% at frame 11)" in caplog.text
    assert rig.notices()[-1] == "not recording: tiles not in within 90 s (tiles 62%); F to try again"

    # And the key starts a fresh wait.
    rig.answers = [_readiness(20), _readiness(21)]
    recorder.toggle_want()
    recorder.step()
    recorder.step()
    assert recorder.recording


def test_a_failed_tile_in_view_is_not_in_even_at_full_progress():
    holed = _readiness(10, 100.0, failed_in_view=3)

    assert not SpanRecorder.tiles_in(holed)
    assert SpanRecorder.describe_tiles(holed) == "100%, 3 failed in view"
    assert not SpanRecorder.tiles_in(_readiness(10, published=False))
    assert SpanRecorder.describe_tiles(_readiness(10, published=False)) == "not published for this view yet"
    assert not SpanRecorder.tiles_in(_readiness(10, visible=False))
    assert SpanRecorder.tiles_in(_readiness(10))
    assert SpanRecorder.describe_tiles(_readiness(10)) == "in"


def test_the_key_during_the_wait_cancels_it(tmp_path):
    rig = _Rig(tmp_path, [_readiness(10, 30.0)])
    recorder = rig.recorder

    recorder.toggle_want()
    recorder.step()
    recorder.toggle_want()
    recorder.step()

    assert not recorder.waiting and not recorder.recording
    assert rig.started == []
    assert rig.notices()[-1] == "recording cancelled while waiting for the tiles"


def test_nothing_starts_before_the_capture_window_opens(tmp_path):
    rig = _Rig(tmp_path, [_readiness(10)], window_opens=False)
    recorder = rig.recorder

    recorder.toggle_want()
    recorder.step()

    assert not recorder.waiting and not recorder.recording
    assert not recorder.want_enabled
    assert rig.asked == 0
    assert rig.notices() == ["not recording: the capture window opens at t=310 s"]


def test_a_server_that_cannot_answer_records_without_the_wait_and_says_so(tmp_path, caplog):
    rig = _Rig(tmp_path, [RuntimeError("get_view_readiness is not bound")])
    recorder = rig.recorder

    with caplog.at_level(logging.WARNING):
        recorder.toggle_want()
        recorder.step()

    assert recorder.recording
    assert "cannot say whether the camera's tiles are in" in caplog.text
    assert rig.notices()[0] == "tiles unknown: recording without waiting for them"


def test_stopping_flushes_reports_and_the_next_span_gets_a_folder_of_its_own(tmp_path):
    rig = _Rig(tmp_path, [_readiness(10), _readiness(11)])
    recorder = rig.recorder
    recorder.toggle_want()
    recorder.step()
    recorder.step()
    first = recorder.directory

    recorder.toggle_want()
    recorder.step()

    assert not recorder.recording
    assert rig.stopped == 1
    # Reported after the flush, so the counts are the span's whole.
    assert rig.closed == [(rig.handles[0], first)]
    assert rig.closed[0][0].Saved == 24
    assert rig.notices()[-1] == f"stopped: 24 captures in {first.name}"

    # A second span started within the same second is not written into the first one's folder.
    rig.answers = [_readiness(30), _readiness(31)]
    recorder.toggle_want()
    recorder.step()
    recorder.step()
    assert recorder.directory == tmp_path / "CARLA-SENSOR-7-20260930T142233Z-2"
    assert recorder.spans == [first]


def test_the_heads_up_display_reads_the_recorders_counts_while_it_records(tmp_path):
    rig = _Rig(tmp_path, [_readiness(10), _readiness(11)])
    recorder = rig.recorder
    assert (recorder.saved, recorder.dropped, recorder.render_set_paired) == (0, 0, None)

    recorder.toggle_want()
    recorder.step()
    recorder.step()
    handle = rig.handles[0]
    handle.Saved, handle.Dropped, handle.RenderSetPaired, handle.RenderSetUnpaired = 12, 1, 11, 1

    assert (recorder.saved, recorder.dropped) == (12, 1)
    assert (recorder.render_set_paired, recorder.render_set_unpaired) == (11, 1)
    handle.PairsRenderSet = False
    assert recorder.render_set_paired is None

    # A recorder built before the sidecar carried supervision counts none, and none is shown.
    assert (recorder.supervision_paired, recorder.supervision_unpaired) == (None, None)
    handle.SupervisionPaired, handle.SupervisionUnpaired = 10, 2
    assert (recorder.supervision_paired, recorder.supervision_unpaired) == (10, 2)


def test_while_recording_the_tiles_are_asked_once_per_capture_period_and_changes_are_logged(
        tmp_path, caplog):
    rig = _Rig(tmp_path, [_readiness(10), _readiness(11)])
    recorder = rig.recorder
    recorder.toggle_want()
    recorder.step()
    recorder.step()
    asked = rig.asked

    recorder.step()
    rig.clock.now += 0.25
    recorder.step()
    assert rig.asked == asked, "asked before a capture period had passed"

    rig.answers = [_readiness(40, 71.0)]
    rig.clock.now += 0.25
    with caplog.at_level(logging.INFO):
        recorder.step()
        assert rig.asked == asked + 1
        assert recorder.tiles == "71%"
        assert "tiles are streaming at frame 40 (tiles 71%)" in caplog.text

        rig.answers = [_readiness(50)]
        rig.clock.now += 0.5
        recorder.step()
        assert recorder.tiles == "in"
        assert "tiles are in again at frame 50" in caplog.text


def test_close_ends_a_recording_span_and_flushes_it(tmp_path):
    rig = _Rig(tmp_path, [_readiness(10), _readiness(11)])
    recorder = rig.recorder
    recorder.toggle_want()
    recorder.step()
    recorder.step()

    recorder.close()
    recorder.toggle_want()
    recorder.step()

    assert rig.stopped == 1
    assert not recorder.recording
    assert len(rig.closed) == 1
    assert rig.started == [str(tmp_path / "CARLA-SENSOR-7-20260930T142233Z")]


def test_with_no_readiness_to_ask_the_span_starts_at_once(tmp_path):
    rig = _Rig(tmp_path, readiness=False)

    rig.recorder.toggle_want()
    rig.recorder.step()

    assert rig.recorder.recording
    assert rig.asked == 0


def test_the_recorders_own_thread_starts_and_stops_a_span_and_the_key_only_asks(tmp_path):
    rig = _Rig(tmp_path, [_readiness(10), _readiness(11)])
    recorder = rig.recorder
    recorder.WAIT_POLL_S = 0.01
    recorder.run_in_background()
    try:
        recorder.toggle_want()
        _wait_for(lambda: recorder.recording)
        recorder.toggle_want()
        _wait_for(lambda: rig.stopped == 1)
    finally:
        recorder.close()

    caller = threading.get_ident()
    assert rig.start_threads and caller not in rig.start_threads
    assert rig.stop_threads and caller not in rig.stop_threads


def test_closing_the_thread_flushes_a_span_still_recording(tmp_path):
    rig = _Rig(tmp_path, [_readiness(10), _readiness(11)])
    recorder = rig.recorder
    recorder.WAIT_POLL_S = 0.01
    recorder.run_in_background()
    recorder.toggle_want()
    _wait_for(lambda: recorder.recording)

    recorder.close()

    assert rig.stopped == 1
    assert len(rig.closed) == 1
    assert recorder._thread is None


def _wait_for(condition, timeout_s: float = 5.0) -> None:
    deadline = threading.Event()
    timer = threading.Timer(timeout_s, deadline.set)
    timer.start()
    try:
        while not condition():
            if deadline.wait(0.005):
                raise AssertionError("condition not reached in time")
    finally:
        timer.cancel()
