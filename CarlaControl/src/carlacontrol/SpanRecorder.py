"""The recording key of a camera flown inside the process that drives the world."""

from __future__ import annotations

import logging
import queue
import statistics
import threading
import time
from collections.abc import Callable
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

from carlacontrol import ViewReadiness


class SpanRecorder:
    """Records a flown camera in spans, one folder per span, each started once its tiles are in.

    A key press asks for a span and the next press ends it. A span is not started the moment the key
    is pressed: a camera flown to new ground renders that ground while Cesium is still streaming it,
    so the recorder first waits for the camera's photoreal tiles, asking the server
    (`world.get_view_readiness`), and starts only when every visible tileset reports its load
    complete with no failed tile in view, on a frame later than the first answer of the wait -- a
    frame rendered after the key was pressed. The wait has a ceiling of 90 s of wall clock, the one
    `03_CoSimulation_Runtime.md` section 9.5.1 sets, and a ceiling only fails: past it the span is
    not started and the operator is told why. Pressing the key during the wait cancels it. A server
    that cannot answer (built before the call existed) is said so, and the span starts without the
    wait, as recording from a flown camera always did: the operator is watching the view it writes.

    Each span is written to its own folder under the record directory, named by the camera's name
    and the instant the span started (`<camera name>-<UTC>`, see `span_directory`); the stills
    inside are the recorder's, named after the camera too (`<camera name>_<local capture time>`).
    While a span records, the view's readiness is asked again once per capture period, so the
    heads-up display can say whether the ground in view has finished streaming and the log says when
    it stops and starts being so. That answer is the view as of the last tick, not
    as of a capture's own frame -- an image reaches the recorder several ticks after its frame and
    the camera may have moved in between -- so it is shown and logged, never written into a capture.

    None of this runs on the thread that draws the window or the thread that drives the world. The
    key only records what was asked and wakes this recorder's own thread, which starts and stops the
    native recorder (a start subscribes to two camera streams; a stop flushes the encoder queue) and
    asks the server about the tiles. So neither a flush nor a slow answer holds a frame of the drive
    or of the window.

    The heads-up display reads it as it reads `NativeRecorder`: `recording`, `saved`, `dropped`,
    `frame_unpaired`, `record_hz` and `toggle_want()`, plus `waiting`, `waiting_s`, `tiles`,
    `render_set_paired`, `render_set_unpaired`, `supervision_paired` and `supervision_unpaired`, and it takes one-line
    messages for the operator from `notices`. Each capture's supervision is the server's for its own
    frame, read by the recorder from the snapshot, so nothing about it is handed to the recorder here.
    """

    TILE_CEILING_S = 90.0
    WAIT_POLL_S = 0.25

    OFF = "off"
    WAITING = "waiting"
    RECORDING = "recording"

    def __init__(
        self,
        record_dir: str | Path,
        sensor_id: str,
        record_hz: float,
        start_recording: Callable[[str], Any],
        stop_recording: Callable[[], None],
        view_readiness: Callable[[], dict] | None = None,
        may_record: Callable[[], str | None] | None = None,
        on_closed: Callable[[Any, Path], None] | None = None,
        clock: Callable[[], float] = time.monotonic,
        utc_now: Callable[[], datetime] | None = None,
        tile_ceiling_s: float = TILE_CEILING_S,
        logger: logging.Logger | None = None,
    ) -> None:
        """
        Args:
            record_dir: Where each span's folder is made.
            sensor_id: The camera's name as the server holds it -- the one it was spawned under,
                or the server's `Camera_<n>` -- which opens every span folder's name.
            record_hz: Captures per second, which is also how often the tiles are asked about
                while a span records.
            start_recording: Starts the native recorder into the directory given and returns its
                handle (a `FrameRecorder`), or None where recording is unavailable.
            stop_recording: Stops it, flushing what it holds.
            view_readiness: Asks the server about the camera's tiles (`world.get_view_readiness`).
                None records at once, with no wait.
            may_record: Says why a span may not start yet, or None when it may.
            on_closed: Handed the flushed handle and the folder when a span ends.
            clock: Wall clock, seconds.
            utc_now: The UTC instant a span's folder is named by.
            tile_ceiling_s: How long to wait for the tiles before giving up on a span.
            logger: Where to say what happened.
        """
        self.record_dir = Path(record_dir)
        self.sensor_id = sensor_id
        self.record_hz = float(record_hz)
        self._start_recording = start_recording
        self._stop_recording = stop_recording
        self._view_readiness = view_readiness
        self._may_record = may_record
        self._on_closed = on_closed
        self._clock = clock
        self._utc_now = utc_now or (lambda: datetime.now(UTC))
        self.tile_ceiling_s = float(tile_ceiling_s)
        self.logger = logger or logging.getLogger(__name__)

        self.want_enabled = False
        self.state = self.OFF
        self.tiles: str | None = None
        self.directory: Path | None = None
        self.spans: list[Path] = []
        self.notices: queue.SimpleQueue[str] = queue.SimpleQueue()

        self._handle: Any = None
        self._wait_started = 0.0
        self._wait_first_frame: int | None = None
        self._tiles_in: bool | None = None
        self._next_poll = 0.0
        self._asked_ms: list[float] = []
        self._wake = threading.Event()
        self._closing = threading.Event()
        self._thread: threading.Thread | None = None

    # -- what the heads-up display reads ------------------------------------------------------------
    @property
    def recording(self) -> bool:
        return self.state == self.RECORDING

    @property
    def waiting(self) -> bool:
        return self.state == self.WAITING

    @property
    def waiting_s(self) -> float:
        """How long the current wait for the tiles has run."""
        return self._clock() - self._wait_started if self.waiting else 0.0

    @property
    def saved(self) -> int:
        return self._counter("Saved")

    @property
    def dropped(self) -> int:
        return self._counter("Dropped")

    @property
    def frame_unpaired(self) -> int:
        """Stills dropped because the client held no truth of their own frame when the image arrived:
        a still is written with its own frame's truth or not at all."""
        return self._counter("FrameUnpaired")

    @property
    def render_set_paired(self) -> int | None:
        """Captures listing their own frame's rendered vehicles, or None where no set is paired."""
        return self._counter("RenderSetPaired") if self._pairs_render_set() else None

    @property
    def render_set_unpaired(self) -> int | None:
        """Captures listing no vehicle because their frame's set was no longer held."""
        return self._counter("RenderSetUnpaired") if self._pairs_render_set() else None

    @property
    def supervision_paired(self) -> int | None:
        """Captures carrying their own frame's supervision, as the server held it; None from a recorder
        that counts none (one built before the sidecar carried supervision), or with no span open."""
        return self._counter("SupervisionPaired") if self._counts_supervision() else None

    @property
    def supervision_unpaired(self) -> int | None:
        """Captures of a frame a plan was in force on, written with their supervision unknown."""
        return self._counter("SupervisionUnpaired") if self._counts_supervision() else None

    def toggle_want(self, enabled: bool | None = None) -> None:
        """Ask for a span, or for the one waiting or recording to end. Returns at once."""
        if self._closing.is_set():
            return
        self.want_enabled = (not self.want_enabled) if enabled is None else bool(enabled)
        self._wake.set()

    # -- the recorder's own thread ------------------------------------------------------------------
    def run_in_background(self) -> None:
        """Start the thread that starts, stops and waits, so no caller's thread ever does."""
        if self._thread is None:
            self._thread = threading.Thread(target=self._run, name="span-recorder", daemon=True)
            self._thread.start()

    def close(self, timeout_s: float = 30.0) -> None:
        """End any span, flushing it, and stop the thread. Safe to call more than once."""
        self._closing.set()
        self.want_enabled = False
        self._wake.set()
        if self._thread is not None:
            self._thread.join(timeout=timeout_s)
            if self._thread.is_alive():
                self.logger.error("the recorder's thread did not finish within %.0f s", timeout_s)
            self._thread = None
        else:
            self.step()

    def _run(self) -> None:
        while True:
            try:
                self.step()
            except Exception:
                self.logger.exception("the span recorder failed a step")
            if self._closing.is_set():
                if self.state == self.RECORDING:
                    self._close_span()
                return
            self._wake.wait(self._next_wake())
            self._wake.clear()

    def _next_wake(self) -> float | None:
        if self.state == self.WAITING:
            return self.WAIT_POLL_S
        if self.state == self.RECORDING and self._view_readiness is not None:
            return max(0.05, self._next_poll - self._clock())
        return None

    def step(self) -> None:
        """One pass: act on what was asked, and ask about the tiles when it is time to."""
        want = self.want_enabled and not self._closing.is_set()
        if self.state == self.OFF and want:
            self._begin()
        elif self.state == self.WAITING:
            if want:
                self._wait()
            else:
                self.state = self.OFF
                self.tiles = None
                self._tell("recording canceled while waiting for the tiles")
        elif self.state == self.RECORDING:
            if want:
                self._watch_tiles()
            else:
                self._close_span()

    # -- a span's life ------------------------------------------------------------------------------
    def _begin(self) -> None:
        reason = self._may_record() if self._may_record is not None else None
        if reason:
            self.want_enabled = False
            self._tell(f"not recording: {reason}")
            return
        if self._view_readiness is None:
            self._open_span()
            return
        self.state = self.WAITING
        self.tiles = None
        self._wait_started = self._clock()
        self._wait_first_frame = None
        self._asked_ms = []
        self.logger.info("recording asked for; waiting up to %.0f s for the camera's tiles",
                         self.tile_ceiling_s)
        self._wait()

    def _wait(self) -> None:
        try:
            readiness = self._ask()
        except Exception as failure:
            self.logger.warning("the server cannot say whether the camera's tiles are in (%r); "
                                "recording starts without waiting for them", failure)
            self._tell("tiles unknown: recording without waiting for them")
            self._open_span()
            return
        frame = int(readiness.get("frame", 0))
        if self._wait_first_frame is None:
            self._wait_first_frame = frame
        self.tiles = self.describe_tiles(readiness)
        waited = self._clock() - self._wait_started
        if frame > self._wait_first_frame and self.tiles_in(readiness):
            self.logger.info("the camera's tiles are in at frame %d after %.1f s%s", frame, waited,
                             self._asked_summary())
            self._open_span()
            return
        if waited >= self.tile_ceiling_s:
            self.state = self.OFF
            self.want_enabled = False
            self.logger.warning("the camera's tiles were not in within %.0f s (tiles %s at frame "
                                "%d); the span is not started%s", self.tile_ceiling_s, self.tiles,
                                frame, self._asked_summary())
            self._tell(f"not recording: tiles not in within {self.tile_ceiling_s:.0f} s "
                       f"(tiles {self.tiles}); F to try again")
            self.tiles = None

    def _open_span(self) -> None:
        directory = self.span_directory(self.record_dir, self.sensor_id, self._utc_now())
        try:
            directory.mkdir(parents=True, exist_ok=False)
            handle = self._start_recording(str(directory))
        except Exception as failure:
            self.logger.error("could not start recording into %s: %r", directory, failure)
            handle = None
        if handle is None:
            self.state = self.OFF
            self.want_enabled = False
            self.tiles = None
            self._tell("not recording: the recorder did not start (see the log)")
            return
        self._handle = handle
        self.directory = directory
        self.state = self.RECORDING
        self._asked_ms = []
        self._next_poll = self._clock() + self._period()
        self._tiles_in = True if self._view_readiness is not None else None
        self.logger.info("recording %s -> %s", self.sensor_id, directory)
        self._tell(f"recording -> {directory.name}")

    def _watch_tiles(self) -> None:
        if self._view_readiness is None or self._clock() < self._next_poll:
            return
        self._next_poll = self._clock() + self._period()
        try:
            readiness = self._ask()
        except Exception as failure:
            self.logger.debug("view readiness could not be read while recording: %r", failure)
            self.tiles = None
            return
        now_in = self.tiles_in(readiness)
        self.tiles = self.describe_tiles(readiness)
        if now_in != self._tiles_in:
            frame = int(readiness.get("frame", 0))
            if now_in:
                self.logger.info("the camera's tiles are in again at frame %d", frame)
            else:
                self.logger.warning("the camera's tiles are streaming at frame %d (tiles %s): "
                                    "captures from here can show ground still arriving until they "
                                    "are in", frame, self.tiles)
            self._tiles_in = now_in

    def _close_span(self) -> None:
        handle, directory = self._handle, self.directory
        try:
            self._stop_recording()
        except Exception as failure:
            self.logger.error("could not stop the recorder: %r", failure)
        self.state = self.OFF
        self.tiles = None
        self._tiles_in = None
        if directory is not None:
            self.spans.append(directory)
            self.logger.info("span closed: %s%s", directory, self._asked_summary())
            self._tell(f"stopped: {self._count(handle, 'Saved')} captures in {directory.name}")
        if self._on_closed is not None and handle is not None and directory is not None:
            try:
                self._on_closed(handle, directory)
            except Exception:
                self.logger.exception("reporting the closed span failed")

    # -- helpers ------------------------------------------------------------------------------------
    @staticmethod
    def span_directory(record_dir: Path, sensor_id: str, started: datetime) -> Path:
        """The folder a span starting at `started` is written to: `<camera name>-<UTC>`.

        The one place a span's name is decided. A second span started within the same second gets
        a numbered suffix rather than sharing the first one's folder.
        """
        stem = f"{sensor_id}-{started.astimezone(UTC):%Y%m%dT%H%M%SZ}"
        candidate = Path(record_dir) / stem
        suffix = 2
        while candidate.exists():
            candidate = Path(record_dir) / f"{stem}-{suffix}"
            suffix += 1
        return candidate

    @staticmethod
    def tiles_in(readiness: dict) -> bool:
        """The camera's view drove the tiles' selection on the tick answered for, and every visible
        tileset has loaded all of it with no failed tile in view: `ViewReadiness.tiles_in`, the
        one definition a capture's pre-roll waits on too."""
        return ViewReadiness.tiles_in(readiness)

    @staticmethod
    def describe_tiles(readiness: dict) -> str:
        """Where the tiles stand, in a few words to follow "tiles": in, a percentage, a failure."""
        return ViewReadiness.describe_tiles(readiness)

    def _ask(self) -> dict:
        started = time.perf_counter()
        readiness = self._view_readiness()
        self._asked_ms.append((time.perf_counter() - started) * 1000.0)
        return readiness

    def _asked_summary(self) -> str:
        """What asking the server about the tiles cost, measured on every call."""
        if not self._asked_ms:
            return ""
        return (f"; readiness asked {len(self._asked_ms)} times, median "
                f"{statistics.median(self._asked_ms):.1f} ms, worst {max(self._asked_ms):.1f} ms")

    def _period(self) -> float:
        return 1.0 / self.record_hz if self.record_hz > 0 else 1.0

    def _tell(self, message: str) -> None:
        self.notices.put(message)

    def _pairs_render_set(self) -> bool:
        try:
            return self._handle is not None and bool(self._handle.PairsRenderSet)
        except Exception:
            return False

    def _counts_supervision(self) -> bool:
        try:
            return self._handle is not None and hasattr(self._handle, "SupervisionUnpaired")
        except Exception:
            return False

    def _counter(self, name: str) -> int:
        return self._count(self._handle, name)

    @staticmethod
    def _count(handle: Any, name: str) -> int:
        try:
            return int(getattr(handle, name)) if handle is not None else 0
        except Exception:
            return 0
