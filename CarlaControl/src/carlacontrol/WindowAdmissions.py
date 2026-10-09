"""The render set's admission passes as one capture window saw them.

The co-simulation session makes an admission pass once per SUMO step and hands every one to
`on_admission_pass` as it is made (`CarlaNet.CoSim.AdmissionPass`, `03_CoSimulation_Runtime.md`
§8.8): the vehicles SUMO has, those admitted and released at the pass and, where an optional
render-set limit is in force, how many the limit admitted (eligible), how many hold a body after the
pass (admitted) and how many a capacity declined (shed). With no limit -- the default -- every vehicle
SUMO has is rendered, and eligible and admitted are the population. A pass is made for the SUMO frame
it names (`SimulatedTimeSeconds`), one step ahead of the last frame rendered, and its render set is
what the next step renders: a pass for frame F governs the ticks from F - step up to F. The vehicles
SUMO inserted at F are one exception: they are drawn from F on, never on those ticks. The vehicles SUMO
no longer has at F are the other: they are drawn on the first of those ticks, the frame of F - step,
the last step that had them, and on none after it.

Two things are kept, and nothing else per pass, so a window with no end costs no memory:

* **the pass at the window's opening** -- the first whose frame is at or after the window's begin,
  which the session makes one step before the rendered clock reaches it (or while it starts, where
  there is no prewarm): the population the window opens on, and under a limit what it left out.
* **a summary of the passes rendered inside the window** -- those whose governed step lies within
  the window's begin and its declared end. A pass is counted only when the next one arrives, which is
  when the step it governs has been rendered, so the lookahead pass of a run that stopped is never
  counted: how many passes, the largest population at one pass, and under a limit how many passes
  left a vehicle without a body and the most they left out.

A pass from a session built before the limit's figures were carried reads as a pass with no limit.

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
        self.passes_leaving_out = 0
        self.most_left_out: int | None = None
        self.most_shed: int | None = None
        self.limited = False
        self.first_frame_s: float | None = None
        self.last_frame_s: float | None = None
        self._pending: dict | None = None

    def observe(self, admission_pass: Any) -> None:
        """Take one pass, as the session makes it. Bound to `on_admission_pass`."""
        described = self.describe(admission_pass)
        frame = described["sim_time_s"]
        if self.at_window_open is None and frame >= self.begin_s - FRAME_EPSILON_S:
            self.at_window_open = described
        if self._pending is not None:
            self._count(self._pending)
        self._pending = described

    def _count(self, described: dict) -> None:
        """Count a pass whose step has been rendered, where that step lay inside the window."""
        frame = described["sim_time_s"]
        if frame - self.step_s < self.begin_s - FRAME_EPSILON_S \
                or frame > self.end_s + FRAME_EPSILON_S:
            return
        self.passes += 1
        population = described["population"]
        self.most_population = population if self.most_population is None \
            else max(self.most_population, population)
        if described["limited"]:
            self.limited = True
            left_out = described["left_out"]
            self.passes_leaving_out += 1 if left_out > 0 else 0
            self.most_left_out = left_out if self.most_left_out is None \
                else max(self.most_left_out, left_out)
            self.most_shed = described["shed"] if self.most_shed is None \
                else max(self.most_shed, described["shed"])
        self.first_frame_s = frame if self.first_frame_s is None else self.first_frame_s
        self.last_frame_s = frame

    @staticmethod
    def describe(admission_pass: Any) -> dict | None:
        """One pass as a run record states it; None where there is none."""
        if admission_pass is None:
            return None
        population = int(admission_pass.Population)
        admitted = int(getattr(admission_pass, "Admitted", population))
        capacity = getattr(admission_pass, "Capacity", None)
        return {"sim_time_s": float(admission_pass.SimulatedTimeSeconds),
                "world_tick": int(admission_pass.WorldTick),
                "population": population,
                "newly_admitted": int(admission_pass.NewlyAdmitted),
                "released": int(admission_pass.Released),
                "total_admissions": int(admission_pass.TotalAdmissions),
                "limited": bool(getattr(admission_pass, "Limited", False)),
                "eligible": int(getattr(admission_pass, "Eligible", population)),
                "admitted": admitted,
                "shed": int(getattr(admission_pass, "Shed", 0)),
                "held": int(getattr(admission_pass, "Held", 0)),
                "capacity": None if capacity is None else int(capacity),
                "rule": str(getattr(admission_pass, "Rule", "Every")),
                "left_out": population - admitted}

    def to_dict(self) -> dict:
        return {"at_window_open": None if self.at_window_open is None
                else dict(self.at_window_open),
                "window": {"passes": self.passes, "most_population": self.most_population,
                           "limited": self.limited,
                           "passes_leaving_out": self.passes_leaving_out,
                           "most_left_out": self.most_left_out, "most_shed": self.most_shed,
                           "first_frame_s": self.first_frame_s,
                           "last_frame_s": self.last_frame_s}}
