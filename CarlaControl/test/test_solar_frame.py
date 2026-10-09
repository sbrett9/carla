"""The solar frame states the zone the engine derives for a world, which is not the site's civil zone.

The engine sets `TimeZone = longitude / 15` when it configures a world's georeference. At Bahonar that
is +03:44:43 against Iran's civil +03:30 -- 14.72 minutes of clock, and up to 3.3 degrees of sun
elevation near the horizon (`04_Contracts.md` §11.4). The frame publishes the derived figure so a
scenario's declared offset can be compared with it offline; these tests pin the figure to the three
shipped worlds' origins and to the arithmetic `04_Contracts.md` works through for the sizing site.
"""
from __future__ import annotations

import sys
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.SolarFrame import SolarFrame  # noqa: E402


@pytest.mark.parametrize(("latitude", "longitude", "hours", "formatted"), [
    (27.15012, 56.18065, 3.7453767, "+03:44:43"),       # Shahid_Bahonar_Port
    (39.59431, -104.88449, -6.9922993, "-06:59:32"),    # Arapahoe_I25
    (38.91108, -119.7645965, -7.9843064, "-07:59:04"),  # Gardnerville_Centerville_Lane
])
def test_the_zone_is_longitude_over_fifteen(latitude, longitude, hours, formatted):
    frame = SolarFrame(latitude, longitude).to_dict()
    assert frame["engine_time_zone_hours"] == pytest.approx(hours, abs=1e-7)
    assert frame["engine_time_zone"] == formatted
    assert (frame["origin_latitude"], frame["origin_longitude"]) == (latitude, longitude)


def test_the_sizing_sites_correction_is_the_one_the_contract_works_through():
    """`04_Contracts.md` §11.4: +0.2453767 h, 14.72 minutes, against a declared +03:30."""
    zone = SolarFrame(27.15012, 56.18065).to_dict()["engine_time_zone_hours"]
    assert (zone - 3.5) == pytest.approx(0.2453767, abs=1e-7)
    assert (zone - 3.5) * 60.0 == pytest.approx(14.72, abs=0.005)


def test_the_frame_states_what_the_engine_sets_besides_the_zone():
    frame = SolarFrame(27.15012, 56.18065).to_dict()
    assert frame["engine_daylight_saving"] is False
    assert frame["engine_solar_time_at_configure_hours"] == 12.0
    assert frame["solar_frame_version"] == 1


@pytest.mark.parametrize("hours", [3.7453767, -6.9922993, 0.0, 14.0, -12.0])
def test_the_formatted_offset_reads_back_to_the_second(hours):
    text = SolarFrame.format_offset(hours)
    sign = -1 if text[0] == "-" else 1
    h, m, s = (int(v) for v in text[1:].split(":"))
    assert sign * (h + m / 60 + s / 3600) == pytest.approx(hours, abs=0.5 / 3600)


@pytest.mark.parametrize(("latitude", "longitude"), [(91.0, 0.0), (0.0, 181.0)])
def test_an_origin_that_is_not_a_position_is_refused(latitude, longitude):
    with pytest.raises(ValueError):
        SolarFrame(latitude, longitude)
