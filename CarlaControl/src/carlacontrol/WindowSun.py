"""The sun a capture window declares, computed by the session's own code, for the resolution report.

A report that states the sun a window opens under is only worth reading if it is the sun the session
will bind. So nothing is re-derived here: the window's declared instant comes from
`CarlaNet.CoSim.DeclaredSun` -- the function the session writes the sun from and audits it against --
and the direction from `CarlaNet.CoSim.SolarPositionModel.AtInstant`, the engine's algorithm evaluated
at that instant, which is the audit's own expectation (`04_Contracts.md` C6 §8.3a). The elevation a
window is declared by is the one `DeclaredSunElevation` names -- refraction-corrected, the angle the
light is rotated by -- with the geometric one carried beside it.

What it cannot see: the sun an operator's override would give (the policy here is the authored
default), and whether the world has a sun at all.
"""
from __future__ import annotations

from dataclasses import dataclass

import carlanet  # noqa: F401  -- loads the CarlaNet assemblies the next import names
from CarlaNet.CoSim import DeclaredSun, DeclaredSunElevation, IlluminationPolicy, SolarPositionModel

from carlacontrol.ScenarioEpoch import ScenarioEpoch


@dataclass(frozen=True)
class SunInstant:
    """The declared sun at one simulated instant."""

    seconds: float
    sun_date: str
    sun_clock: str
    elevation_deg: float
    geometric_elevation_deg: float
    azimuth_deg: float

    def to_dict(self) -> dict:
        return {"seconds": self.seconds, "sun_date": self.sun_date, "sun_clock": self.sun_clock,
                "elevation_deg": round(self.elevation_deg, 4),
                "geometric_elevation_deg": round(self.geometric_elevation_deg, 4),
                "azimuth_deg": round(self.azimuth_deg, 4)}


class WindowSun:
    """The declared sun of windows under one epoch, policy and world origin."""

    ELEVATION_KIND = str(DeclaredSunElevation.Name)

    def __init__(self, epoch: ScenarioEpoch, policy: IlluminationPolicy, latitude: float,
                 longitude: float) -> None:
        self.epoch = epoch
        self.policy = policy
        self.latitude = float(latitude)
        self.longitude = float(longitude)

    @property
    def binds_the_sun(self) -> bool:
        return bool(self.policy.BindsTheSun)

    def at(self, window_open_s: float, seconds: float) -> SunInstant | None:
        """The declared sun at `seconds` in a window opening at `window_open_s`; None under `ignore`."""
        if not self.binds_the_sun:
            return None
        declared = DeclaredSun(self.epoch.solar_epoch, self.policy, float(window_open_s))
        instant = declared.SunAt(float(seconds))
        sun = SolarPositionModel.AtInstant(self.latitude, self.longitude,
                                           float(declared.TimeZoneHours), instant)
        return SunInstant(
            seconds=float(seconds),
            sun_date=f"{int(instant.Year):04d}-{int(instant.Month):02d}-{int(instant.Day):02d}",
            sun_clock=f"{int(instant.Hour):02d}:{int(instant.Minute):02d}:{int(instant.Second):02d}",
            elevation_deg=float(DeclaredSunElevation.Of(sun)),
            geometric_elevation_deg=float(sun.ElevationDegrees),
            azimuth_deg=float(sun.AzimuthDegrees))
