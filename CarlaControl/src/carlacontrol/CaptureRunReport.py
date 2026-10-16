"""What one stretch of recording produced, including what it lost."""
from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True)
class CaptureRunReport:
    """Captures written, captures lost, and how the simulation clock ran against the wall's.

    Saved frames alone describe a run that went perfectly and a run that dropped a third of its
    frames in exactly the same words, which is why the dropped count sits beside the saved one rather
    than in a counter nobody reads. A drop is a whole capture -- imagery and truth sidecar together --
    that the encoder queue had no room for, so a gap in a recording is a gap in the truth as well, and
    a consumer counting captures per second of simulation would otherwise see a rate that quietly
    changed under load.

    The clock ratio is the other half of the same question. Simulated seconds per wall-clock second
    says whether a capture window that was planned in simulation time is affordable in real time: the
    SUMO side of this pipeline has reported it since it was written, and the capture side reported
    nothing, so the two halves of one run could not be compared. A ratio below 1 means the world ran
    slower than real time, which is normal for a rendering client and is only alarming when it moves.
    """

    saved: int = 0
    dropped: int = 0
    sim_seconds: float = 0.0
    wall_seconds: float = 0.0

    @property
    def achieved_real_time_factor(self) -> float:
        """Seconds of simulation per second of wall clock."""
        return self.sim_seconds / self.wall_seconds if self.wall_seconds else 0.0

    @property
    def dropped_fraction(self) -> float:
        """Share of the captures the recorder reached for that it could not write."""
        offered = self.saved + self.dropped
        return self.dropped / offered if offered else 0.0

    def describe(self) -> str:
        """One line naming everything a run has to report, for the log at the end of it."""
        return (f"{self.saved} capture(s) saved, {self.dropped} dropped "
                f"({100.0 * self.dropped_fraction:.1f}%) over {self.sim_seconds:.1f} s of "
                f"simulation in {self.wall_seconds:.1f} s of wall clock "
                f"({self.achieved_real_time_factor:.2f}x real time)")

    def __str__(self) -> str:
        return self.describe()
