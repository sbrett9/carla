"""A free view flies its camera from a window on a thread of its own, and never writes the world.

`run_sumo_drive.py --view free` opens `run_free_move_camera.py`'s window inside the process that
drives the world (`12_Operator_Control_Surface.md` section 9.6). The drive keeps its own thread for
the session's steps and the world's ticks, and the window -- created, pumped and drawn -- lives on
another, which SDL needs on Windows and which keeps every frame the window draws off the drive's
thread. The window is the real `PygameInterface`, opened on SDL's dummy video driver so no display
is needed; the world, the rig, the flight controller and the recorder are stand-ins that record
what is done to them and on which thread.

The heads-up display's record field is checked on its own: for `NativeRecorder` it must read
exactly as it did, and for the span recorder it adds the wait, the render-set pairing and the tiles.
"""
from __future__ import annotations

import os
import queue
import sys
import threading
import time
from pathlib import Path
from types import SimpleNamespace

import pytest

os.environ.setdefault("SDL_VIDEODRIVER", "dummy")
os.environ.setdefault("PYGAME_HIDE_SUPPORT_PROMPT", "1")

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

import pygame  # noqa: E402

from carlacontrol.FreeView import FreeView  # noqa: E402  (needs the path above)
from carlacontrol.Pose import Pose  # noqa: E402
from carlacontrol.PygameInterface import PygameInterface  # noqa: E402

# Everything the window may ask of the world: the heads-up display's reads.
WORLD_CALLS_ALLOWED = {"get_staging_bounds", "get_solar_state", "get_cesium_origin", "ground_z_below"}

# What only the process that drives the world may do.
WORLD_CALLS_FORBIDDEN = {
    "tick", "apply_settings", "set_solar_time", "set_solar_date", "set_solar_epoch",
    "set_time_advance", "set_weather", "set_layer_visible", "set_layer_collision",
    "set_layer_offset", "set_road_rendered", "set_cesium_visible", "set_cesium_collision",
    "load_map_layer", "unload_map_layer", "reload_world", "start_sumo_drive", "start_recording",
}


class _World:
    """Records every call made on it, and the thread it was made on, defined method or not."""

    def __init__(self) -> None:
        self.calls: list[tuple[str, int]] = []

    def __getattr__(self, name: str):
        if name.startswith("__"):
            raise AttributeError(name)

        def call(*_args, **_kwargs):
            self.calls.append((name, threading.get_ident()))
            return None

        return call

    def names(self) -> set[str]:
        return {name for name, _ in self.calls}


class _Rig:
    FT_PER_M = 3.28084

    def __init__(self) -> None:
        self.ground_z = 0.0
        self.frames_received = 0
        self.draw_threads: set[int] = set()

    def get_latest_rgb(self):
        self.draw_threads.add(threading.get_ident())
        return None

    def get_position(self) -> Pose:
        return Pose(x=10.0, y=-20.0, z=300.0, pitch=-90.0, yaw=0.0)

    def reset_to_initial_pose(self) -> None:
        pass


class _Controller:
    def __init__(self) -> None:
        self.speed = 60.0
        self.started = 0
        self.stopped = 0
        self.moves = 0
        self.event_threads: set[int] = set()

    def start_async(self) -> None:
        self.started += 1

    def stop_async(self) -> None:
        self.stopped += 1

    def apply_transform_async(self) -> None:
        self.moves += 1

    def update_movement(self, _dt, _events) -> None:
        self.event_threads.add(threading.get_ident())


class _Recorder:
    """What the heads-up display and the F key see of a span recorder."""

    def __init__(self) -> None:
        self.recording = False
        self.waiting = False
        self.saved = 0
        self.dropped = 0
        self.record_hz = 2.0
        self.toggles = 0
        self.notices: queue.SimpleQueue[str] = queue.SimpleQueue()

    def toggle_want(self) -> None:
        self.toggles += 1


def _settings() -> SimpleNamespace:
    return SimpleNamespace(width=320, height=180, fov=90.0, fixed_delta=0.05, time_rate=1.0)


class _Open:
    """A free view opened on the stand-ins, closed on the way out whatever the test did."""

    def __init__(self, recorder=None) -> None:
        self.world = _World()
        self.rig = _Rig()
        self.controller = _Controller()
        self.recorder = recorder
        self.view = FreeView(_settings(), self.world, self.rig, self.controller, recorder=recorder)

    def __enter__(self) -> _Open:
        self.view.open()
        return self

    def __exit__(self, *_exc) -> None:
        self.view.close()

    def press(self, key: int) -> None:
        pygame.event.post(pygame.event.Event(pygame.KEYDOWN, key=key, mod=0, unicode="",
                                             scancode=0))

    def frames(self, count: int = 3) -> None:
        """Let the window run `count` frames or so, at its 20 fps."""
        time.sleep(count * 0.06)


def _wait_for(condition, timeout_s: float = 5.0) -> None:
    deadline = time.monotonic() + timeout_s
    while not condition():
        if time.monotonic() > deadline:
            raise AssertionError("condition not reached in time")
        time.sleep(0.01)


def test_the_window_is_made_pumped_and_drawn_on_one_thread_that_is_not_the_drives():
    drive = threading.get_ident()
    with _Open() as opened:
        opened.frames()
        _wait_for(lambda: opened.controller.event_threads and opened.rig.draw_threads)

    made = {thread for name, thread in opened.world.calls if name == "get_staging_bounds"}
    assert len(made) == 1
    assert made == opened.controller.event_threads == opened.rig.draw_threads
    assert drive not in made


def test_the_window_writes_nothing_to_the_world_whatever_is_pressed():
    recorder = _Recorder()
    with _Open(recorder) as opened:
        # The run_SCTMV keys that toggle layers, collision, the road and the sun's advance, then F.
        for key in (pygame.K_c, pygame.K_g, pygame.K_v, pygame.K_r, pygame.K_l, pygame.K_k,
                    pygame.K_f):
            opened.press(key)
        _wait_for(lambda: recorder.toggles == 1)
        opened.frames()

    names = opened.world.names()
    assert names & WORLD_CALLS_FORBIDDEN == set()
    assert names <= WORLD_CALLS_ALLOWED, names - WORLD_CALLS_ALLOWED
    assert recorder.toggles == 1


def test_esc_closes_the_window_and_stops_the_mover_and_the_drive_is_told():
    with _Open() as opened:
        _wait_for(lambda: opened.controller.moves > 0)
        assert opened.controller.started == 1
        opened.press(pygame.K_ESCAPE)
        _wait_for(opened.view.closed.is_set)

    assert opened.controller.stopped == 1
    opened.view.close()


def test_closing_from_the_drives_side_stops_the_window():
    with _Open() as opened:
        opened.frames(1)
    assert opened.view.closed.is_set()
    assert opened.controller.stopped == 1


def test_the_recorders_latest_message_is_shown_as_the_windows_note():
    recorder = _Recorder()
    with _Open(recorder) as opened:
        recorder.notices.put("waiting")
        recorder.notices.put("recording -> CARLA-SENSOR-7-20260930T142233Z")
        _wait_for(lambda: opened.view.window.note is not None)
        assert opened.view.window.note[0] == "recording -> CARLA-SENSOR-7-20260930T142233Z"


def test_a_window_that_cannot_open_is_said_to_the_caller_and_marked_closed():
    def refuses(**_kwargs):
        raise pygame.error("No available video device")

    view = FreeView(_settings(), _World(), _Rig(), _Controller(), window_factory=refuses)

    with pytest.raises(RuntimeError, match="did not open"):
        view.open()
    assert view.closed.is_set()
    view.close()


# -- the heads-up display's record field -------------------------------------------------------------
class _NativeRecorder:
    """NativeRecorder's surface as the display reads it: no wait, no render set, no tiles."""

    def __init__(self, recording: bool, saved: int = 0, dropped: int = 0) -> None:
        self.recording = recording
        self.saved = saved
        self.dropped = dropped
        self.record_hz = 2.0


def _status(recorder) -> str:
    return PygameInterface.recording_status(SimpleNamespace(recorder=recorder))


def test_a_native_recorder_reads_as_it_always_did():
    assert _status(None) == "n/a"
    assert _status(_NativeRecorder(False)) == "off"
    assert _status(_NativeRecorder(True, saved=12)) == "REC 12@2Hz"
    assert _status(_NativeRecorder(True, saved=12, dropped=1)) == "REC 12@2Hz -1 dropped"


def test_a_span_recorder_shows_its_wait_its_pairing_and_its_tiles():
    waiting = SimpleNamespace(recording=False, waiting=True, tiles="87%", waiting_s=12.4)
    assert _status(waiting) == "waiting for tiles, 87% (12 s)"
    asking = SimpleNamespace(recording=False, waiting=True, tiles=None, waiting_s=0.1)
    assert _status(asking) == "waiting for tiles, asking (0 s)"

    recording = SimpleNamespace(recording=True, waiting=False, saved=24, dropped=0, record_hz=2.0,
                                render_set_paired=24, render_set_unpaired=0, tiles="in")
    assert _status(recording) == "REC 24@2Hz  set 24 paired  tiles in"
    recording.saved, recording.dropped = 30, 2
    recording.render_set_paired, recording.render_set_unpaired = 29, 1
    recording.tiles = "71%"
    assert _status(recording) == "REC 30@2Hz -2 dropped  set 29 paired -1 unpaired  tiles 71%"
