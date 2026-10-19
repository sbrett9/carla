"""Notices when a camera's frames stop arriving, and when they start again."""

from __future__ import annotations

from collections.abc import Callable


class FrameStallWatch:
    """Says once that frames have stopped, and once that they have resumed.

    Fed the running count of frames received, it reports `STALLED` the first time the count has not
    moved for `stall_after_s` of wall-clock time, and `RESUMED` the first time it moves again after
    that. In between it reports nothing, so a viewer waiting on a world nobody is ticking logs one
    line rather than one per loop. Wall clock is the right clock here: the question is whether the
    viewer looks frozen to the person watching it, and a stalled world's simulated clock is exactly
    the thing that has stopped.
    """

    STALLED = "stalled"
    RESUMED = "resumed"

    def __init__(self, stall_after_s: float, clock: Callable[[], float]) -> None:
        """
        Args:
            stall_after_s: How long the count may stand still before it is reported, seconds.
            clock: A monotonic wall clock, in seconds.
        """
        if stall_after_s <= 0.0:
            raise ValueError(f"stall_after_s must be above 0, got {stall_after_s}")
        self.stall_after_s = stall_after_s
        self._clock = clock
        self._count = 0
        self._since = clock()
        self.stalled = False

    def observe(self, frames_received: int) -> str | None:
        """Take the current count; return `STALLED`, `RESUMED` or None.

        Args:
            frames_received: Frames received so far, never decreasing.
        """
        now = self._clock()
        if frames_received != self._count:
            self._count = frames_received
            self._since = now
            if self.stalled:
                self.stalled = False
                return self.RESUMED
            return None
        if not self.stalled and now - self._since >= self.stall_after_s:
            self.stalled = True
            return self.STALLED
        return None

    def quiet_for_s(self) -> float:
        """How long the count has stood still, seconds."""
        return self._clock() - self._since
