"""Where the rendered vehicles are, measured from the poses the co-simulation session wrote to bodies.

A stare aimed at the traffic (`ChannelDescription.stare_look_at_target` set to `rendered_traffic`)
looks at the centre of the vehicles the session rendered on the last frame before the capture window
opens. The middle of the render region is a poor stand-in: on a corridor scenario the region is drawn
around a road that runs through it, and a run aimed at its middle framed a builder's yard while the
traffic was on a highway a hundred metres away (`CarlaNet/python/run_sumo_drive.py`,
`RenderedVehicleCentre`). The mean of the poses is not a guess about where the traffic ought to be;
it is where the bodies were put.

The session hands `on_pose` one record per rendered vehicle per tick, on the thread that called
`Advance` and before that call returns (`SumoDriveSession.ComputePoses`), so once a step's `Advance`
has returned every pose of the step has been seen. The centre is the mean of the poses of the
**latest tick** seen in the step -- each vehicle once, where it stood on that frame -- height
included, so a camera's altitude is measured above the traffic rather than above CARLA's origin. The
render set changes only between steps, at the admission pass, so every tick of a step renders the
same vehicles and the latest tick is the step's last frame.

A pose written to no body (`Actor` zero: the pool had no body for its blueprint, or no world is
attached) is left out, because a vehicle with no body is in no frame.

The session reads its callbacks once, when it starts, so this is bound for the whole run. Between the
steps it measures it declines each record after one comparison, and it keeps running sums rather than
records, so a run-length stream of poses costs no memory.

What it cannot see: whether the vehicles it averages are inside any camera's view -- it measures
where the traffic is, not what a camera would frame.
"""
from __future__ import annotations

from typing import Any


class RenderedTrafficCentre:
    """The centre of the vehicles rendered on the latest frame of the step being measured."""

    def __init__(self) -> None:
        self.open = False
        self.frame_tick: int | None = None
        self.frame_s: float | None = None
        self.vehicles = 0
        self._x = 0.0
        self._y = 0.0
        self._z = 0.0

    def begin_step(self) -> None:
        """Measure the step about to be advanced, forgetting the last one's."""
        self.open = True
        self.frame_tick = None
        self.frame_s = None
        self.vehicles = 0
        self._x = self._y = self._z = 0.0

    def end_step(self) -> None:
        """Stop taking poses; the step's measurement stands until the next `begin_step`."""
        self.open = False

    def collect(self, record: Any) -> None:
        """Take one computed pose: a `CarlaNet.CoSim.CoSimPoseRecord`. Bound to `on_pose`."""
        if not self.open or record.Actor == 0:
            return
        tick = int(record.TickIndex)
        if tick != self.frame_tick:
            if self.frame_tick is not None and tick < self.frame_tick:
                return
            self.frame_tick = tick
            self.frame_s = float(record.SimulatedTimeSeconds)
            self.vehicles = 0
            self._x = self._y = self._z = 0.0
        pose = record.Pose
        self._x += float(pose.X)
        self._y += float(pose.Y)
        self._z += float(pose.Z)
        self.vehicles += 1

    def centre(self) -> tuple[float, float, float] | None:
        """The mean position in CARLA's frame, metres; None where the step rendered nothing."""
        if self.vehicles == 0:
            return None
        return self._x / self.vehicles, self._y / self.vehicles, self._z / self.vehicles

    def to_dict(self) -> dict | None:
        """The measurement as a run record states it; None where the step rendered nothing."""
        centre = self.centre()
        if centre is None:
            return None
        return {"x_m": centre[0], "y_m": centre[1], "z_m": centre[2], "vehicles": self.vehicles,
                "frame_tick": self.frame_tick, "frame_s": self.frame_s}
