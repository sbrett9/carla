"""The echo before commit states the instant and the sun the run will actually use, and computes nothing.

Plan 12 §6.4: a multi-hour run states its first capture's civil instant and sun elevation before it
starts, because discovering afterwards that the sun was wrong is the most expensive failure there is.
The figures must be the ones the session will act on: the civil span from the scenario's epoch, and the
sun from the session's own declared-sun function with the window opening where the session opens it --
at the window's own begin, which run_capture hands the session as `window_opens_at`, not at the
prewarm's first frame five minutes earlier. At dawn five minutes is most of a degree, so the test
evaluates the sun independently with the engine's algorithm in Python at both instants and holds the
echo to the right one.

The rendering is checked to be a rendering: altering the block changes the text, so the text cannot be
coming from a second computation.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))
sys.path.insert(0, str(_REPO / "CarlaControl" / "test"))

from RunCaptureFixture import ORIGIN, Layout, run_document  # noqa: E402

from carlacontrol.LaunchEcho import LaunchEcho  # noqa: E402
from carlacontrol.RunConfiguration import RunConfiguration  # noqa: E402
from carlacontrol.RunConfigurationResolver import RunConfigurationResolver  # noqa: E402
from carlacontrol.SiteProfile import SiteProfile  # noqa: E402
from carlacontrol.SolarPositionModel import SolarPositionModel  # noqa: E402

PACIFIC_DAYLIGHT = -7.0


@pytest.fixture
def layout(tmp_path: Path) -> Layout:
    return Layout(tmp_path)


def echo_for(layout: Layout, overrides=()) -> LaunchEcho:
    profile = SiteProfile.discover(layout.root, layout.write_profile(), environ={})
    run = RunConfiguration.from_document(run_document(), "fixture.run.json")
    parsed = [(*RunConfiguration.parse_override(text), f"--set {text}") for text in overrides]
    effective = RunConfigurationResolver(profile).resolve(run, parsed)
    return LaunchEcho.compute(effective, "cap-test", layout.capture_root / "cap-test",
                              layout.runs_root / "run.result.json", 5e10, 7200.0, 7.5e5,
                              ["prewarm_clipped"])


def corrected_elevation(hour: int, minute: int) -> float:
    """The engine's algorithm, evaluated at a Pacific-daylight clock on the fixture's date."""
    return SolarPositionModel.sun_position(ORIGIN[0], ORIGIN[1], PACIFIC_DAYLIGHT, False,
                                           2026, 3, 21, hour, minute, 0).corrected_elevation_deg


def test_the_civil_span_is_the_epoch_s(layout):
    civil = echo_for(layout).to_dict()["civil"]
    assert civil["begin"] == "2026-03-21T07:00:00-07:00"
    assert civil["end"] == "2026-03-21T07:30:00-07:00"
    assert civil["first_rendered"] == "2026-03-21T06:55:00-07:00"


def test_a_frozen_sun_is_stated_at_the_instant_the_session_pins_it(layout):
    sun = echo_for(layout).to_dict()["sun"]
    first_rendered = corrected_elevation(6, 55)
    pinned = corrected_elevation(7, 0)
    assert abs(pinned - first_rendered) > 0.5, "the fixture must tell the two instants apart"
    assert sun["window_open_s"] == 25200.0
    assert sun["at_begin"]["elevation_deg"] == pytest.approx(pinned, abs=0.02)
    assert sun["at_end"]["elevation_deg"] == pytest.approx(pinned, abs=0.02)
    assert sun["held_at"] == "2026-03-21 07:00:00"
    assert "window's opening, t=25200" in sun["held_at_note"]
    assert "prewarm from t=24900 is lit by it" in sun["held_at_note"]


def test_an_advancing_sun_is_stated_at_the_window_s_two_ends(layout):
    sun = echo_for(layout, ["solar.policy=advance"]).to_dict()["sun"]
    assert sun["at_begin"]["elevation_deg"] == pytest.approx(corrected_elevation(7, 0), abs=0.02)
    assert sun["at_end"]["elevation_deg"] == pytest.approx(corrected_elevation(7, 30), abs=0.02)
    assert sun["rate"] == 1.0
    assert "held_at" not in sun


def test_a_zero_prewarm_pins_the_sun_at_the_window_s_begin(layout):
    sun = echo_for(layout, ["capture.prewarm_s=0"]).to_dict()["sun"]
    assert sun["at_begin"]["elevation_deg"] == pytest.approx(corrected_elevation(7, 0), abs=0.02)


def test_a_zero_prewarm_says_nothing_of_a_prewarm(layout):
    sun = echo_for(layout, ["capture.prewarm_s=0"]).to_dict()["sun"]
    assert sun["held_at"] == "2026-03-21 07:00:00" and "prewarm" not in sun["held_at_note"]


def test_an_ignored_sun_says_the_lighting_honours_no_epoch(layout):
    sun = echo_for(layout, ["solar.policy=ignore"]).to_dict()["sun"]
    assert sun["binds"] is False and "honours no epoch" in sun["statement"]


def test_the_captures_are_counted_from_the_window_and_the_rate(layout):
    captures = echo_for(layout).to_dict()["captures"]
    assert captures == {"capture_hz": 2.0, "channels": 1, "per_channel": 3600, "total": 3600,
                        "frames_per_hour": 7200}


def test_the_block_serialises_and_flattens_for_expectations(layout):
    echo = echo_for(layout)
    json.dumps(echo.to_dict())
    values = echo.values()
    assert values["launch_echo.civil.begin"] == "2026-03-21T07:00:00-07:00"
    assert values["launch_echo.captures.channels"] == 1
    assert values["launch_echo.warnings"] == ["prewarm_clipped"]


def test_the_rendering_formats_the_block_and_computes_nothing(layout):
    echo = echo_for(layout)
    text = echo.render()
    assert "2026-03-21T07:00:00-07:00" in text and "freeze_at_window_start" in text
    assert "warnings    1   (prewarm_clipped)" in text
    echo.block["civil"]["begin"] = "1999-01-01T00:00:00+00:00"
    echo.block["captures"]["per_channel"] = 12345
    altered = echo.render()
    assert "1999-01-01T00:00:00+00:00" in altered and "12,345 captures" in altered
