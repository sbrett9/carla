"""The free-move camera's flight window, on a thread of its own inside the process that drives the world."""

from __future__ import annotations

import logging
import threading
import time
from collections.abc import Callable
from typing import Any

from .PygameInterface import PygameInterface


class FreeView:
    """`run_free_move_camera.py`'s window and flight controls, beside a drive in the same process.

    The drive keeps the thread it runs on -- the one that steps the session and ticks the world --
    and the window gets a thread of its own. Everything pygame does happens on that thread: the
    window is created there and its events are pumped and its frames drawn there, which SDL requires
    on Windows (a window's events arrive on the thread that created it) and which keeps the drive's
    thread free of every frame the window draws. The flight controller moves the cameras from its
    own mover thread, as it does in the separate viewer, so neither the window nor the drive waits
    on a camera move.

    The window is opened read-only: it never ticks the world and binds none of the keys that would
    change its settings, layers or sun. The drive owns all of those. It shows the rig's frames as
    they arrive, at the drive's tick rate.

    Closing the window, or Esc, sets `closed`; the drive reads it between steps and decides what to
    do. `close()` stops the window from the drive's side. Neither destroys a camera: the rig is the
    caller's, and is destroyed by the caller once the window and any recorder reading it have
    stopped.
    """

    TITLE = "Free-move camera, driving"
    OPEN_TIMEOUT_S = 30.0

    def __init__(
        self,
        args: Any,
        world: Any,
        rig: Any,
        controller: Any,
        recorder: Any = None,
        window_factory: Callable[..., Any] = PygameInterface,
        logger: logging.Logger | None = None,
    ) -> None:
        """
        Args:
            args: The window's settings, as `PygameInterface` reads them: width, height, fov,
                fixed_delta and time_rate.
            world: The world the heads-up display reads from.
            rig: The `SensorRig` whose frames are shown and which the controller moves.
            controller: The `PyGameSensorController` flying the rig.
            recorder: What F toggles and the heads-up display reports, or None for no recording.
            window_factory: Builds the window; `PygameInterface` unless a test stands one in.
            logger: Where to say what happened.
        """
        self.args = args
        self.world = world
        self.rig = rig
        self.controller = controller
        self.recorder = recorder
        self.window_factory = window_factory
        self.logger = logger or logging.getLogger(__name__)
        self.window: Any = None
        self.closed = threading.Event()
        self._opened = threading.Event()
        self._stop = threading.Event()
        self._failure: BaseException | None = None
        self._thread: threading.Thread | None = None

    def open(self, timeout_s: float = OPEN_TIMEOUT_S) -> None:
        """Start the window's thread and return once the window is up. Raises if it did not open."""
        self._thread = threading.Thread(target=self._run, name="free-view", daemon=True)
        self._thread.start()
        if not self._opened.wait(timeout_s):
            self._stop.set()
            raise RuntimeError(f"the free view's window did not open within {timeout_s:.0f} s")
        if self._failure is not None:
            raise RuntimeError(f"the free view's window did not open: {self._failure!r}") \
                from self._failure

    def close(self, timeout_s: float = 5.0) -> None:
        """Stop the window and its flight controller. Safe to call more than once."""
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=timeout_s)
            if self._thread.is_alive():
                self.logger.error("the free view's window did not close within %.0f s", timeout_s)
            self._thread = None

    def _run(self) -> None:
        try:
            self.window = self.window_factory(
                args=self.args,
                world=self.world,
                window_title=self.TITLE,
                sync=True,
                sensors=self.rig,
                controller=self.controller,
                recorder=self.recorder,
                read_only=True,
            )
        except BaseException as failure:
            self._failure = failure
            self.closed.set()
            self._opened.set()
            return
        self._opened.set()
        self.controller.start_async()
        try:
            while self.window.running and not self._stop.is_set():
                if self.window.process_events():
                    break
                self.controller.apply_transform_async()
                self._show_notices()
                self.window.render()
        except Exception:
            self.logger.exception("the free view's window failed")
        finally:
            # The mover first, so nothing moves a camera after the window has gone.
            self.controller.stop_async()
            self.window.quit()
            self.closed.set()

    def _show_notices(self) -> None:
        """Put the recorder's latest message where the window shows a note for a few seconds."""
        notices = getattr(self.recorder, "notices", None)
        if notices is None:
            return
        latest = None
        while not notices.empty():
            latest = notices.get_nowait()
        if latest is not None:
            self.window.note = (latest, time.time())
