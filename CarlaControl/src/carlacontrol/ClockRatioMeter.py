"""Ticks a world a fixed number of times and reports what that cost in wall clock.

The clock ratio -- simulated seconds produced per wall-clock second -- is the quantity every
wall-clock and storage figure in the capture plan rests on, and it has only ever been recovered
after the fact by differencing a capture's PNG metadata against its file timestamp. That works on a
session that ran, but it cannot compare two configurations, because a comparison needs both arms to
differ in exactly one thing and to be measured the same way.

This measures it directly, in synchronous mode, where a tick is a bounded unit of work the client
asks for: call `world.tick()` a declared number of times and divide. Per-tick durations are kept
rather than only their mean, because the distribution is what says whether a slower arm is uniformly
slower or is paying for one stall.

**A warm-up is not optional.** The first ticks after a camera is spawned or a level is loaded pay for
shader compilation, tile streaming and render-target allocation, and folding those into a mean turns
any two arms into noise.
"""
from __future__ import annotations

import logging
import statistics
import time
from dataclasses import dataclass, field


@dataclass
class ClockRatioSample:
    """One arm of a comparison: what was ticked, and what it cost."""

    label: str
    ticks: int
    wall_seconds: float
    fixed_delta_seconds: float
    tick_milliseconds: list[float] = field(default_factory=list)
    first_frame: int = 0
    last_frame: int = 0

    @property
    def ticks_per_wall_second(self) -> float:
        return self.ticks / self.wall_seconds if self.wall_seconds else 0.0

    @property
    def clock_ratio(self) -> float:
        """Simulated seconds per wall-clock second. 1.0 is real time."""
        return self.ticks * self.fixed_delta_seconds / self.wall_seconds if self.wall_seconds else 0.0

    @property
    def mean_tick_ms(self) -> float:
        return statistics.fmean(self.tick_milliseconds) if self.tick_milliseconds else 0.0

    @property
    def median_tick_ms(self) -> float:
        return statistics.median(self.tick_milliseconds) if self.tick_milliseconds else 0.0

    @property
    def p95_tick_ms(self) -> float:
        if not self.tick_milliseconds:
            return 0.0
        ordered = sorted(self.tick_milliseconds)
        return ordered[min(len(ordered) - 1, int(0.95 * len(ordered)))]

    @property
    def frames_advanced(self) -> int:
        """Server frames between the first and last tick, which need not equal the tick count."""
        return self.last_frame - self.first_frame

    def describe(self) -> str:
        return (f"{self.label:<34}{self.ticks:>6} ticks{self.wall_seconds:>9.2f} s"
                f"{self.ticks_per_wall_second:>9.2f} t/ws{self.clock_ratio * 100:>8.1f}%"
                f"{self.mean_tick_ms:>9.1f} ms mean{self.median_tick_ms:>9.1f} p50"
                f"{self.p95_tick_ms:>9.1f} p95")


class ClockRatioMeter:
    """Measures ticks per wall-second on a synchronously ticking world."""

    def __init__(self, world, fixed_delta_seconds: float = 0.05,
                 logger: logging.Logger | None = None) -> None:
        self.world = world
        self.fixed_delta_seconds = fixed_delta_seconds
        self.logger = logger or logging.getLogger(__name__)

    def configure_synchronous(self) -> None:
        """Put the world in synchronous mode at the meter's step, so a tick is what is measured."""
        settings = self.world.get_settings()
        settings.synchronous_mode = True
        settings.fixed_delta_seconds = self.fixed_delta_seconds
        self.world.apply_settings(settings)

    def warm_up(self, ticks: int) -> None:
        """Tick without measuring, so shader compilation and streaming are not in the sample."""
        for _ in range(ticks):
            self.world.tick()

    def measure(self, label: str, ticks: int,
                before_tick=None, after_tick=None) -> ClockRatioSample:
        """Tick `ticks` times, timing each one.

        `before_tick` and `after_tick` receive the tick index and run inside the timed region, which
        is what makes the per-tick cost of a batch or a light sweep measurable as part of the tick
        rather than beside it.
        """
        durations: list[float] = []
        first_frame = 0
        last_frame = 0
        started = time.perf_counter()
        for index in range(ticks):
            tick_started = time.perf_counter()
            if before_tick is not None:
                before_tick(index)
            frame = self.world.tick()
            if after_tick is not None:
                after_tick(index)
            durations.append((time.perf_counter() - tick_started) * 1000.0)
            if index == 0:
                first_frame = frame
            last_frame = frame
        wall = time.perf_counter() - started
        sample = ClockRatioSample(label=label, ticks=ticks, wall_seconds=wall,
                                  fixed_delta_seconds=self.fixed_delta_seconds,
                                  tick_milliseconds=durations,
                                  first_frame=first_frame, last_frame=last_frame)
        self.logger.info("%s", sample.describe())
        return sample

    @staticmethod
    def header() -> str:
        return (f"{'arm':<34}{'ticks':>12}{'wall':>11}{'rate':>14}{'ratio':>8}"
                f"{'mean':>14}{'median':>12}{'p95':>12}")

    @staticmethod
    def relative(sample: ClockRatioSample, baseline: ClockRatioSample) -> float:
        """A sample's tick rate as a fraction of a baseline's."""
        if not baseline.ticks_per_wall_second:
            return 0.0
        return sample.ticks_per_wall_second / baseline.ticks_per_wall_second
