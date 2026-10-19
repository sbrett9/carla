"""The pacing lines carla-drive logs while its session advances."""
from __future__ import annotations

import logging

logger = logging.getLogger(__name__)


class PacingProgress:
    """Says, once per closed pacing window, how far the run has got and what pace it held.

    Every figure is the session's own (`session.Report.Pacing`), read between advances: nothing here
    times anything, so what this prints and what the final report records cannot disagree. A line
    per window of wall clock rather than per so many steps, because at real time a scenario with a
    one-second SUMO step would otherwise print once every few minutes.
    """

    def __init__(self) -> None:
        self.windows_seen = 0

    @staticmethod
    def declared(pacing) -> str:
        """The pace the session was declared to hold, as it echoes it back."""
        if pacing.Paced:
            return f"held to {pacing.DeclaredFactor:g}x real time"
        return "not paced, as fast as the machine allows"

    @staticmethod
    def achieved(pacing) -> str:
        """The pace held over the last window and the whole run, and for a paced run the slip."""
        if pacing.AchievedFactor is None:
            return "nothing timed yet"
        text = f"{pacing.AchievedFactor:.3f}x over the whole run"
        if pacing.LastWindowFactor is not None:
            text = (f"{pacing.LastWindowFactor:.3f}x over the last {pacing.WindowSeconds:g} s, "
                    f"{text}, worst window {pacing.WorstWindowFactor:.3f}x")
        if pacing.Paced:
            text += f", {pacing.BehindScheduleSeconds:.2f} s behind schedule"
        return text

    def after_step(self, session, steps: int) -> None:
        """Log a line if a pacing window closed during the step just taken."""
        pacing = session.Report.Pacing
        if pacing.CompletedWindows == self.windows_seen:
            return
        self.windows_seen = pacing.CompletedWindows
        # The divergence figures are the report's own, live: how far a body's applied pose has been
        # from the pose it was given, and how far the velocity the world reports for it -- the one
        # the truth telemetry reads -- from the velocity it was given.
        logger.info("  %d steps, t=%.1f s, %d rendered, pace %s, worst divergence %.4f m, "
                    "%.4f m/s", steps, session.RenderedTimeSeconds,
                    session.RenderedVehicleIds.Count, self.achieved(pacing),
                    session.Report.WorstPositionDivergenceMetres,
                    session.Report.WorstVelocityDivergenceMetresPerSecond)
        admission = session.Report.LastAdmissionPass
        if admission is None:
            return
        if not admission.Limited:
            logger.info("  sumo population %d, all in the render set; %d admitted and %d released at "
                        "the last pass, %d admitted in all", admission.Population,
                        admission.NewlyAdmitted, admission.Released, admission.TotalAdmissions)
            return
        logger.info("  sumo population %d, in the render set %d, drawn %d, shed %d%s; %d without a "
                    "body; %s",
                    admission.Population, admission.Eligible, admission.Admitted, admission.Shed,
                    "" if admission.Capacity is None else f", capacity {admission.Capacity}",
                    admission.Population - admission.Admitted, self.rule(admission))

    @staticmethod
    def rule(admission) -> str:
        """Which rule the pass decided by, as the log says it."""
        if str(admission.Rule) == "Cameras":
            return (f"by {admission.Cameras} camera footprint(s), {admission.Held} held by the "
                    "release lag")
        if str(admission.Rule) == "Circle":
            return "by the circle"
        return "every vehicle, up to the capacity"
