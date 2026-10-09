"""The lamp pass reads the sun the world is showing, not the one it was showing before it was asked.

The server's sky takes a requested sun instant on the next tick, not when it is asked: measured on a
synchronous server, `get_solar_state` went on answering -59.88 degrees after the hour was set to noon,
a second later as well, and answered 63.15 only after one tick. A pass that reads the elevation before
ticking therefore judges the lighting it found, not the lighting it set -- which refused a night pass
over a world last left in daylight, and would admit a daylight pass over a world last left at night.
"""
from __future__ import annotations

import os
import sys

_REPO = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
sys.path.insert(0, os.path.join(_REPO, "CarlaControl", "src"))

from carlacontrol.VehicleLampProbe import LampProbeSettings, VehicleLampProbe  # noqa: E402

DAYLIGHT_DEG = 63.15
NIGHT_DEG = -59.88


class TickAppliedSunWorld:
    """A world whose sun, like the server's, changes only on the tick after it is set."""

    def __init__(self, elevation_found: float, elevation_at_hour: dict[float, float]) -> None:
        self.elevation = elevation_found
        self.elevation_at_hour = elevation_at_hour
        self.requested_hour: float | None = None
        self.settings_applied: list = []

    def get_settings(self):
        return type("Settings", (), {"synchronous_mode": False, "fixed_delta_seconds": None})()

    def apply_settings(self, settings) -> None:
        self.settings_applied.append(settings)

    def set_time_advance(self, advancing: bool) -> None:
        pass

    def set_solar_date(self, year: int, month: int, day: int) -> None:
        pass

    def set_solar_time(self, hours: float) -> None:
        self.requested_hour = hours

    def tick(self) -> int:
        if self.requested_hour is not None:
            self.elevation = self.elevation_at_hour[self.requested_hour]
        return 0

    def get_solar_state(self) -> dict:
        return {"sun_elevation_deg": self.elevation}


def probe(world) -> VehicleLampProbe:
    return VehicleLampProbe(world, LampProbeSettings(solar_settle_ticks=2))


def test_a_night_pass_over_a_world_left_in_daylight_runs():
    world = TickAppliedSunWorld(DAYLIGHT_DEG, {1.0: NIGHT_DEG})
    result = probe(world).run(None, [], None)
    assert result.ran, result.reason
    assert result.header["sun_elevation_deg"] == NIGHT_DEG


def test_a_pass_whose_hour_is_daylight_is_refused_even_over_a_world_left_at_night():
    world = TickAppliedSunWorld(NIGHT_DEG, {1.0: DAYLIGHT_DEG})
    result = probe(world).run(None, [], None)
    assert not result.ran
    assert result.header["sun_elevation_deg"] == DAYLIGHT_DEG
    assert "above the horizon" in result.reason


def test_the_world_is_given_its_settings_back_either_way():
    for found, at_hour in ((DAYLIGHT_DEG, NIGHT_DEG), (NIGHT_DEG, DAYLIGHT_DEG)):
        world = TickAppliedSunWorld(found, {1.0: at_hour})
        probe(world).run(None, [], None)
        assert world.settings_applied[-1].synchronous_mode is False
