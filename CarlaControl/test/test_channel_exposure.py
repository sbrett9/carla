"""A channel's exposure: its defaults, its conversion to the camera's attributes, and what it refuses.

`08_Collection_And_EPoL.md` D8.26-D8.27: exposure is a per-channel collection parameter, set in numbers
over the post-process profile. Asserted:

  * each default is the `Default` profile's (manual, ISO 100, 1/320 s, f/4, no compensation), whose
    EV100 doc 08 §2.9 measured at +12.32, so a run that sets none renders as every capture before it;
  * the camera is given every attribute, by upstream CARLA's names and units: the shutter in seconds
    in the run is per second on the camera;
  * EV100 is the engine's manual figure, and there is none under histogram; and
  * a value the camera cannot take as stated is refused naming its field and the range, and nothing
    else is.
"""
from __future__ import annotations

import math
import sys
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.ChannelExposure import ChannelExposure  # noqa: E402


def test_the_defaults_are_the_default_profile_s_and_make_ev100_12_32():
    exposure = ChannelExposure()
    assert (exposure.method, exposure.iso, exposure.shutter_s, exposure.fstop,
            exposure.compensation_ev) == ("manual", 100.0, 0.003125, 4.0, 0.0)
    assert exposure.ev100 == pytest.approx(12.32, abs=0.005)
    assert exposure.problems() == []


def test_a_channel_that_sets_none_is_sent_the_default_profile_s_attributes():
    assert ChannelExposure.from_channel({}).blueprint_attributes() == {
        "exposure_mode": "manual", "iso": "100.0", "shutter_speed": "320.0", "fstop": "4.0",
        "exposure_compensation": "0.0"}


@pytest.mark.parametrize(("seconds", "per_second"), [
    (0.003125, "320.0"), (0.002, "500.0"), (1 / 60, "60.0"), (0.000125, "8000.0"), (2.0, "0.5")])
def test_the_shutter_in_seconds_is_sent_per_second(seconds, per_second):
    exposure = ChannelExposure.from_channel({"exposure_shutter_s": seconds})
    assert exposure.blueprint_attributes()["shutter_speed"] == per_second


def test_the_fields_are_read_from_the_channel_by_their_run_names():
    exposure = ChannelExposure.from_channel({
        "exposure_method": "histogram", "exposure_iso": 400, "exposure_shutter_s": 0.001,
        "exposure_fstop": 8, "exposure_compensation_ev": -1.5, "fov": 60.0})
    assert exposure == ChannelExposure("histogram", 400, 0.001, 8, -1.5)
    assert exposure.blueprint_attributes() == {
        "exposure_mode": "histogram", "iso": "400.0", "shutter_speed": "1000.0", "fstop": "8.0",
        "exposure_compensation": "-1.5"}


def test_four_times_the_iso_is_two_stops_brighter_and_two_fewer_ev100():
    assert ChannelExposure(iso=400.0).ev100 == pytest.approx(ChannelExposure().ev100 - 2.0)


def test_ev100_is_the_engine_s_manual_figure_and_there_is_none_under_histogram():
    exposure = ChannelExposure(iso=200.0, shutter_s=0.004, fstop=5.6)
    assert exposure.ev100 == pytest.approx(math.log2(5.6 ** 2 * 250 * 100 / 200))
    assert ChannelExposure(method="histogram").ev100 is None


@pytest.mark.parametrize(("given", "field"), [
    ({"iso": 0.99}, "exposure_iso"), ({"iso": math.nan}, "exposure_iso"),
    ({"iso": True}, "exposure_iso"), ({"iso": "100"}, "exposure_iso"),
    ({"shutter_s": 0.0}, "exposure_shutter_s"), ({"shutter_s": -0.002}, "exposure_shutter_s"),
    ({"shutter_s": 1 / 9000}, "exposure_shutter_s"), ({"shutter_s": 320.0}, "exposure_shutter_s"),
    ({"shutter_s": math.inf}, "exposure_shutter_s"),
    ({"fstop": 0.95}, "exposure_fstop"), ({"fstop": 33.0}, "exposure_fstop"),
    ({"compensation_ev": 15.5}, "exposure_compensation_ev"),
    ({"compensation_ev": -16.0}, "exposure_compensation_ev"),
    ({"method": "auto"}, "exposure_method"),
])
def test_a_value_the_camera_cannot_take_as_stated_is_refused_naming_its_field(given, field):
    [(named, why)] = ChannelExposure(**given).problems()
    assert named == field
    assert "manual or histogram" in why if field == "exposure_method" else "accept" in why


@pytest.mark.parametrize("given", [
    {"iso": 1.0}, {"iso": 300000.0}, {"shutter_s": 1 / 8000}, {"shutter_s": 100.0},
    {"fstop": 1.0}, {"fstop": 32.0}, {"compensation_ev": 15.0}, {"compensation_ev": -15.0},
    {"method": "histogram"}])
def test_the_ends_of_every_range_and_histogram_are_taken(given):
    assert ChannelExposure(**given).problems() == []
