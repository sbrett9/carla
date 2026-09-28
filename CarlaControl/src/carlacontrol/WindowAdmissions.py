"""The render set's admission passes as one capture window saw them.

The co-simulation session makes an admission pass once per SUMO step and hands every one to
`on_admission_pass` as it is made (`CarlaNet.CoSim.AdmissionPass`, `03_CoSimulation_Runtime.md`
§8.8): the vehicles SUMO has, those inside the subscription margin, those the render region admits
(eligible), those holding a place (admitted), those the capacity declined (shed), and the capacity. A
pass is made for the SUMO frame it names (`SimulatedTimeSeconds`), one step ahead of the last frame
rendered, and its render set is what the next step renders: a pass for frame F governs the ticks from
F - step up to F.

Two things are kept, and nothing else per pass, so a window with no end costs no memory:

* **the pass at the window's opening** -- the first whose frame is at or after the window's begin,
  which the session makes one step before the rendered clock reaches it (or while it starts, where
  there is no prewarm). Its eligible count against its capacity is the in-region population at the
  window's begin against `render_cap`: check 33 of `12_Operator_Control_Surface.md` §6.2.
* **a summary of the passes rendered inside the window** -- those whose governed step lies within
  the window's begin and its declared end. A pass is counted only when the next one arrives, which is
  when the step it governs has been rendered, so the lookahead pass of a run that stopped is never
  counted: how many passes, how many shed a vehicle, and the most eligible and the most shed at one
  pass.

What it cannot see: whether an admitted vehicle got a body -- the session's report counts that
(`VehicleTicksWithNoMeasuredBody`, `PoseDeclinesForNoBody`) -- and anything between two passes, since
the render set does not change between them.
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
        self.passes_shedding = 0
        self.most_eligible: int | None = None
        self.most_shed: int | None = None
        self.capacity: int | None = None
        self.first_frame_s: float | None = None
        self.last_frame_s: float | None = None
        self._pending: tuple[float, int, int, int] | None = None

    def observe(self, admission_pass: Any) -> None:
        """Take one pass, as the session makes it. Bound to `on_admission_pass`."""
        frame = float(admission_pass.SimulatedTimeSeconds)
        if self.at_window_open is None and frame >= self.begin_s - FRAME_EPSILON_S:
            self.at_window_open = self.describe(admission_pass)
        if self._pending is not None:
            self._count(*self._pending)
        self._pending = (frame, int(admission_pass.Eligible), int(admission_pass.Shed),
                         int(admission_pass.Capacity))

    def _count(self, frame: float, eligible: int, shed: int, capacity: int) -> None:
        """Count a pass whose step has been rendered, where that step lay inside the window."""
        if frame - self.step_s < self.begin_s - FRAME_EPSILON_S \
                or frame > self.end_s + FRAME_EPSILON_S:
            return
        self.passes += 1
        self.passes_shedding += 1 if shed > 0 else 0
        self.most_eligible = eligible if self.most_eligible is None \
            else max(self.most_eligible, eligible)
        self.most_shed = shed if self.most_shed is None else max(self.most_shed, shed)
        self.capacity = capacity
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
                "subscribed": int(admission_pass.Subscribed),
                "eligible": int(admission_pass.Eligible),
                "admitted": int(admission_pass.Admitted),
                "shed": int(admission_pass.Shed),
                "capacity": int(admission_pass.Capacity),
                "newly_admitted": int(admission_pass.NewlyAdmitted),
                "released": int(admission_pass.Released),
                "total_admissions": int(admission_pass.TotalAdmissions),
                "total_capacity_declines": int(admission_pass.TotalCapacityDeclines)}

    def to_dict(self) -> dict:
        return {"at_window_open": None if self.at_window_open is None
                else dict(self.at_window_open),
                "window": {"passes": self.passes, "passes_shedding": self.passes_shedding,
                           "most_eligible": self.most_eligible, "most_shed": self.most_shed,
                           "capacity": self.capacity, "first_frame_s": self.first_frame_s,
                           "last_frame_s": self.last_frame_s}}
