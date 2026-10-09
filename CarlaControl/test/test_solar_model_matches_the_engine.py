"""The window plan's model of the sun, pinned to what the engine was measured to return.

Every capture window is placed by evaluating `SolarPositionModel` at a site, a date and a clock.
On 2026-09-21 the same instants were set on a running server with `set_solar_epoch` and read back
with `get_solar_state`; the readings below are those returned values, transcribed. They pin the
model to a measurement rather than to itself, so a change to either the model or the engine's
inputs shows up here instead of in a corpus.

The engine returns its angles as single-precision floats derived from a value near 180 degrees, so
they quantise at roughly 1.5e-5 degrees, and `ACesiumSunSky` truncates its clock to whole seconds,
worth at most 0.0042 degrees of elevation. `RESOLUTION_FLOOR_DEGREES` is the sum rounded up, and it
is the tightest a comparison against the engine can honestly assert. The measured worst residual was
6.9e-6 degrees, so the test also holds the model to a hundred times tighter than the floor -- a
deviation between the two implementations would have to be at least that large to be real.

`SolarAudit` itself is exercised against a stand-in world, so that the input checks that guard the
audit are shown to reject a sun holding the wrong epoch, which is the failure they exist for.
"""
from __future__ import annotations

import sys
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.SolarAudit import SolarAudit  # noqa: E402
from carlacontrol.SolarPositionModel import (  # noqa: E402
    RESOLUTION_FLOOR_DEGREES,
    SolarPositionModel,
)

ARAPAHOE = (39.59431, -104.88449)
BAHONAR = (27.15012, 56.18065)

# (latitude, longitude, time_zone, year, month, day, solar_time,
#  engine elevation, engine refraction-corrected elevation, engine azimuth)
ENGINE_READINGS = [
    (*ARAPAHOE, -7.0, 2026, 12, 21, 6.0, -14.22632, -14.20357, 108.83122),
    (*ARAPAHOE, -7.0, 2026, 12, 21, 7.0, -3.61803, -3.52676, 117.70262),
    (*ARAPAHOE, -7.0, 2026, 12, 21, 17.0, -4.43510, -4.36070, 243.02682),
    (*ARAPAHOE, -7.0, 2026, 12, 21, 23.0, -69.73611, -69.73398, 318.58496),
    (*ARAPAHOE, -7.0, 2026, 3, 21, 7.0, 10.39754, 10.48235, 98.36960),
    (*ARAPAHOE, -7.0, 2026, 6, 21, 7.0, 25.63060, 25.66406, 79.88106),
    (*BAHONAR, 3.5, 2026, 12, 21, 6.0, -6.99658, -6.94955, 112.79269),
    (*BAHONAR, 3.5, 2026, 12, 21, 7.0, 4.98076, 5.14085, 119.56246),
    (*BAHONAR, 3.5, 2026, 12, 21, 17.0, -1.58296, -1.37418, 244.34160),
    (*BAHONAR, 3.5, 2026, 12, 21, 23.0, -79.47812, -79.47705, 288.25415),
    (*BAHONAR, 3.5, 2026, 3, 21, 7.0, 15.10179, 15.16060, 97.63795),
    (*BAHONAR, 3.5, 2026, 6, 21, 7.0, 25.91144, 25.94449, 75.65086),
]

# A hundred times inside the floor: the measured worst residual across both sites was 6.9e-6 deg.
AGREEMENT_LIMIT_DEGREES = RESOLUTION_FLOOR_DEGREES / 100.0


class _StubWorld:
    """A world whose sun holds whatever it is told to hold, so the input checks can be made to fail."""

    def __init__(self, state: dict, accept_epoch: bool = True):
        self.state = dict(state)
        self.accept_epoch = accept_epoch

    def set_solar_epoch(self, year, month, day, hours, utc_offset_hours):
        if not self.accept_epoch:
            return False
        self.state.update(year=year, month=month, day=day, solar_time=hours,
                          time_zone=utc_offset_hours)
        return True

    def get_solar_state(self):
        return dict(self.state)


def _reading_state(reading) -> dict:
    latitude, longitude, zone, year, month, day, hour, elevation, corrected, azimuth = reading
    return {"solar_time": hour, "year": year, "month": month, "day": day, "time_zone": zone,
            "lat": latitude, "lon": longitude, "sun_elevation_deg": elevation,
            "sun_azimuth_deg": azimuth, "advancing": False, "rate": 1.0,
            "sun_corrected_elevation_deg": corrected}


@pytest.mark.parametrize("reading", ENGINE_READINGS,
                         ids=[f"{r[3]:04d}-{r[4]:02d}-{r[5]:02d}T{r[6]:04.1f}@{r[0]:.2f}"
                              for r in ENGINE_READINGS])
def test_model_reproduces_the_engines_reading(reading):
    latitude, longitude, zone, year, month, day, hour, elevation, corrected, azimuth = reading
    modelled = SolarPositionModel.at_solar_time(
        latitude, longitude, zone, False, year, month, day, hour)
    assert modelled.elevation_deg == pytest.approx(elevation, abs=AGREEMENT_LIMIT_DEGREES)
    assert modelled.corrected_elevation_deg == pytest.approx(
        corrected, abs=AGREEMENT_LIMIT_DEGREES)
    assert modelled.azimuth_deg == pytest.approx(azimuth, abs=AGREEMENT_LIMIT_DEGREES)


def test_refraction_moves_a_low_sun_by_more_than_the_floor():
    """A threshold on the geometric elevation is not a threshold on the one the light is rotated by.

    The world-observer header carries eleven solar fields and the refraction-corrected elevation is
    the twelfth, so a consumer reading the pushed stream sees the geometric angle only. At the
    Bahonar port at 17:00 on the December solstice the two differ by 0.21 degrees, against a sun
    1.58 degrees below the horizon.
    """
    sun = SolarPositionModel.at_solar_time(*BAHONAR, 3.5, False, 2026, 12, 21, 17.0)
    separation = sun.corrected_elevation_deg - sun.elevation_deg
    assert separation > 20 * RESOLUTION_FLOOR_DEGREES
    assert sun.elevation_deg < 0.0
    assert sun.corrected_elevation_deg < 0.0


def test_the_spawned_time_zone_flips_a_terminator_window_day_to_night():
    """A generated world spawns its sun at longitude/15, which is not the site's civil offset.

    At the Bahonar port that is 14.72 minutes. At 17:00 on the December solstice the civil clock
    puts the sun below the horizon and the spawned clock puts it above, which is what makes the gap
    worth a measurement rather than a footnote.
    """
    spawned_zone = SolarPositionModel.estimate_time_zone_for_longitude(BAHONAR[1])
    assert abs(spawned_zone - 3.5) * 60.0 == pytest.approx(14.72, abs=0.01)
    civil = SolarPositionModel.at_solar_time(*BAHONAR, 3.5, False, 2026, 12, 21, 17.0)
    spawned = SolarPositionModel.at_solar_time(*BAHONAR, spawned_zone, False, 2026, 12, 21, 17.0)
    assert civil.elevation_deg < 0.0 < spawned.elevation_deg


def test_the_same_gap_at_arapahoe_is_inside_the_noise():
    """One site cannot establish the gap: at Arapahoe it is 0.46 minutes and moves nothing.

    This is why the audit runs at two sites. A single-site run at Arapahoe passes whether or not the
    time zone is the site's civil offset.
    """
    spawned_zone = SolarPositionModel.estimate_time_zone_for_longitude(ARAPAHOE[1])
    assert abs(spawned_zone - -7.0) * 60.0 == pytest.approx(0.46, abs=0.01)
    civil = SolarPositionModel.at_solar_time(*ARAPAHOE, -7.0, False, 2026, 12, 21, 17.0)
    spawned = SolarPositionModel.at_solar_time(*ARAPAHOE, spawned_zone, False, 2026, 12, 21, 17.0)
    assert abs(civil.elevation_deg - spawned.elevation_deg) < 0.2


def test_input_checks_reject_a_sun_holding_someone_elses_epoch():
    """The check the audit leads with, shown rejecting what it is there to reject.

    A world package that carries its own `ACesiumSunSky` can reach a session on the class defaults --
    `TimeZone = -5`, `SolarTime = 13.0`, 2019-09-21, daylight saving on. Compared against a model
    evaluated at the epoch that was asked for, such a sun produces a large elevation residual that
    looks exactly like an algorithm disagreement.

    Five of the six checks fail and `day` does not, because the class default day-of-month is 21 and
    so is the solstice's. A check on the date alone would pass a sun holding none of the epoch it
    was given, which is why every field is checked rather than a representative one.
    """
    class_defaults = {"solar_time": 13.0, "year": 2019, "month": 9, "day": 21, "time_zone": -5.0,
                      "lat": BAHONAR[0], "lon": BAHONAR[1], "sun_elevation_deg": 0.0,
                      "sun_azimuth_deg": 0.0, "advancing": True, "rate": 1.0,
                      "sun_corrected_elevation_deg": None}
    audit = SolarAudit(_StubWorld(class_defaults), "stand-in", *BAHONAR)
    checks = audit.check_inputs(class_defaults, 2026, 12, 21, 17.0, 3.5)
    failed = {check.name for check in checks if not check.agrees}
    assert failed == {"time_zone", "year", "month", "solar_time", "advancing"}
    assert {check.name for check in checks if check.agrees} == {"day", "lat", "lon"}


def test_input_checks_pass_a_sun_holding_the_epoch_it_was_given():
    reading = ENGINE_READINGS[-1]
    state = _reading_state(reading)
    audit = SolarAudit(_StubWorld(state), "stand-in", *BAHONAR)
    checks = audit.check_inputs(state, reading[3], reading[4], reading[5], reading[6], reading[2])
    assert all(check.agrees for check in checks)


def test_shadow_length_ratio_is_undefined_below_the_horizon():
    assert SolarPositionModel.shadow_length_ratio(45.0) == pytest.approx(1.0)
    assert SolarPositionModel.shadow_length_ratio(5.0) == pytest.approx(11.430, abs=1e-3)
    assert SolarPositionModel.shadow_length_ratio(-1.6) is None
