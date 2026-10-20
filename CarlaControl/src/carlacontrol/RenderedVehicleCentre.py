"""Where carla-drive's fixed camera looks: the mean position of the vehicles it rendered."""
from __future__ import annotations


class RenderedVehicleCentre:
    """Where the vehicles are, measured from the poses the bridge wrote to bodies.

    The middle of the world is a poor thing to point a camera at. On a corridor scenario the world is
    built around a road that runs through it, so its middle is whatever that road happens to pass: a
    run aimed at the middle of its area framed a builder's yard while the traffic was on a highway a
    hundred metres away. The mean of the poses is not a guess about where the traffic ought to be --
    it is where the bodies were put.

    The session hands out one pose per rendered vehicle per tick, from the tick thread, so a run's
    worth of them kept in a list is a run-length leak. This keeps a running sum instead, and only
    while it is open: the camera is aimed once, before the recorder starts, and does not move again.
    Poses that were written to no body are left out, because a vehicle with no body is not in frame.
    """

    def __init__(self) -> None:
        self.open = False
        self.total_x = 0.0
        self.total_y = 0.0
        self.count = 0

    def collect(self, record) -> None:
        """Take one computed pose. Bound to the session's pose callback for the whole run."""
        if self.open and record.Actor != 0:
            self.total_x += record.Pose.X
            self.total_y += record.Pose.Y
            self.count += 1

    def centre(self) -> tuple[float, float] | None:
        """The mean position in CARLA's frame, or None where nothing was rendered."""
        if self.count == 0:
            return None
        return self.total_x / self.count, self.total_y / self.count
