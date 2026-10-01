"""The render set's admission passes as one capture window saw them.

The co-simulation session makes an admission pass once per SUMO step and hands every one to
`on_admission_pass` as it is made (`CarlaNet.CoSim.AdmissionPass`, `03_CoSimulation_Runtime.md`
§8.8): the vehicles SUMO has -- every one of which is rendered -- and those admitted and released at
the pass. A pass is made for the SUMO frame it names (`SimulatedTimeSeconds`), one step ahead of the
last frame rendered, and its render set is what the next step renders: a pass for frame F governs
the ticks from F - step up to F. The vehicles it newly admits are the exception: SUMO inserted them
at F, and they are drawn from F on, never on those ticks.

Two things are kept, and nothing else per pass, so a window with no end costs no memory:

* **the pass at the window's opening** -- the first whose frame is at or after the window's begin,
  which the session makes one step before the rendered clock reaches it (or while it starts, where
  there is no prewarm): the population the window opens on.
* **a summary of the passes rendered inside the window** -- those whose governed step lies within
  the window's begin and its declared end. A pass is counted only when the next one arrives, which is
  when the step it governs has been rendered, so the lookahead pass of a run that stopped is never
  counted: how many passes, and the largest population at one pass.

What it cannot see: whether a vehicle got a body -- one whose type names no measured blueprint does
not, and the session's report counts that (`VehicleTicksWithNoMeasuredBody`) -- and anything between
two passes, since the render set does not change between them.
"""
from __future__ import annotations

from typing import Any

FRAME_EPSILON_S = 1e-6


class WindowAdmissions:
    """The admission pass at a window's opening, and a summary of the passes inside it."""

    def __init__(self, begin_s: float, end_s: float, step_s: float) -> None:
        self.begin_s = float(begin_s)
        self.end_s = float(end_s)
        self.step_s = float(step_s)
        self.at_window_open: dict | None = None
        self.passes = 0
        self.most_population: int | None = None
        self.first_frame_s: float | None = None
        self.last_frame_s: float | None = None
        self._pending: tuple[float, int] | None = None

    def observe(self, admission_pass: Any) -> None:
        """Take one pass, as the session makes it. Bound to `on_admission_pass`."""
        frame = float(admission_pass.SimulatedTimeSeconds)
        if self.at_window_open is None and frame >= self.begin_s - FRAME_EPSILON_S:
            self.at_window_open = self.describe(admission_pass)
        if self._pending is not None:
            self._count(*self._pending)
        self._pending = (frame, int(admission_pass.Population))

    def _count(self, frame: float, population: int) -> None:
        """Count a pass whose step has been rendered, where that step lay inside the window."""
        if frame - self.step_s < self.begin_s - FRAME_EPSILON_S \
                or frame > self.end_s + FRAME_EPSILON_S:
            return
        self.passes += 1
        self.most_population = population if self.most_population is None \
            else max(self.most_population, population)
        self.first_frame_s = frame if self.first_frame_s is None else self.first_frame_s
        self.last_frame_s = frame

    @staticmethod
    def describe(admission_pass: Any) -> dict | None:
        """One pass as a run record states it; None where there is none."""
        if admission_pass is None:
            return None
        return {"sim_time_s": float(admission_pass.SimulatedTimeSeconds),
                "world_tick": int(admission_pass.WorldTick),
                "population": int(admission_pass.Population),
                "newly_admitted": int(admission_pass.NewlyAdmitted),
                "released": int(admission_pass.Released),
                "total_admissions": int(admission_pass.TotalAdmissions)}

    def to_dict(self) -> dict:
        return {"at_window_open": None if self.at_window_open is None
                else dict(self.at_window_open),
                "window": {"passes": self.passes, "most_population": self.most_population,
                           "first_frame_s": self.first_frame_s,
                           "last_frame_s": self.last_frame_s}}
