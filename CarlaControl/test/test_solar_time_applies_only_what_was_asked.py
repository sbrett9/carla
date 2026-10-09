"""A run that asks for no sun must not be given one.

Setting the sun used to be unconditional: with no `--date` the host machine's calendar was used and
with no `--time` the hour was forced to 12.0, in attach mode as well as after a build. Every capture
this pipeline has ever produced is therefore noon on whatever day the run happened, and the seasonal
sun angle in those frames is an artifact of the calendar rather than anything the run declared. The
sun is in the pixels, so none of it is recoverable; the only thing that can be fixed is that it stops
happening.

What is checked here is the decision, not the rendering: which setters `setup_solar_time` calls for a
given set of arguments. The world stands in as an object that records the calls made against it, so
the no-arguments case can assert the strongest thing available -- that nothing at all was set -- and
each supplied argument can be shown to move one setting and leave the others alone. A server is not
needed for any of it, which is what lets this run in CI beside the rest.
"""
from __future__ import annotations

import logging
import sys
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

WorldBuilder = pytest.importorskip(
    "carlacontrol.WorldBuilder",
    reason="carlacontrol.WorldBuilder needs the CarlaNet assemblies on this machine",
).WorldBuilder

# The sun this stand-in world reports when it is asked, so that "left alone" can be distinguished
# from "set to the same thing by chance": nothing here is a default any code path would invent.
WORLD_SOLAR_STATE = {
    "solar_time": 7.25, "year": 2031, "month": 3, "day": 4, "time_zone": -6.992299,
    "lat": 39.59431, "lon": -104.88449, "sun_elevation_deg": 14.87, "sun_azimuth_deg": 101.3,
    "advancing": False, "rate": 1.0,
}


class _RecordingWorld:
    """A world that records which solar setters were called on it, and sets nothing."""

    def __init__(self, solar_state: dict | None = None):
        self.calls: list[tuple] = []
        self.solar_state = solar_state

    def set_solar_date(self, year: int, month: int, day: int) -> bool:
        self.calls.append(("set_solar_date", year, month, day))
        return True

    def set_solar_time(self, hours: float) -> bool:
        self.calls.append(("set_solar_time", hours))
        return True

    def set_time_advance(self, enabled: bool, rate: float) -> bool:
        self.calls.append(("set_time_advance", enabled, rate))
        return True

    def get_solar_state(self) -> dict | None:
        return self.solar_state

    def setters_called(self) -> list[str]:
        return [call[0] for call in self.calls]


class _Arguments:
    """The four parsed arguments `setup_solar_time` reads, at their parser defaults."""

    def __init__(self, time=None, date=None, time_advance: bool = False, time_rate: float = 1.0):
        self.time = time
        self.date = date
        self.time_advance = time_advance
        self.time_rate = time_rate


def test_no_solar_arguments_sets_nothing():
    """The whole point: an operator who asked for no sun gets the world's own sun, untouched."""
    world = _RecordingWorld(WORLD_SOLAR_STATE)

    assert WorldBuilder.solar_time_requested(_Arguments()) is False
    assert WorldBuilder.setup_solar_time(world, _Arguments()) is True
    assert world.calls == []


def test_no_solar_arguments_says_which_sun_the_run_is_using(caplog):
    """Silence is what let the forced noon stand, so the sun left in place is reported."""
    world = _RecordingWorld(WORLD_SOLAR_STATE)
    with caplog.at_level(logging.INFO, logger="carlacontrol.WorldBuilder"):
        WorldBuilder.setup_solar_time(world, _Arguments())

    logged = "\n".join(record.getMessage() for record in caplog.records)
    assert "left as the world has it" in logged
    assert "07:15" in logged
    assert "2031-03-04" in logged


def test_a_world_with_no_sun_to_read_is_still_reported(caplog):
    """A world that cannot answer must not be silently relit either."""
    world = _RecordingWorld(solar_state=None)
    with caplog.at_level(logging.INFO, logger="carlacontrol.WorldBuilder"):
        assert WorldBuilder.setup_solar_time(world, _Arguments()) is True

    assert world.calls == []
    assert "no sun to read" in "\n".join(record.getMessage() for record in caplog.records)


def test_a_time_alone_sets_the_clock_and_not_the_date():
    """The date stays the world's, rather than becoming the host machine's calendar."""
    world = _RecordingWorld(WORLD_SOLAR_STATE)

    assert WorldBuilder.setup_solar_time(world, _Arguments(time="17:30")) is True
    assert world.setters_called() == ["set_solar_time"]
    assert world.calls == [("set_solar_time", 17.5)]


def test_a_date_alone_sets_the_date_and_not_the_clock():
    """The hour stays the world's, rather than being forced to noon."""
    world = _RecordingWorld(WORLD_SOLAR_STATE)

    assert WorldBuilder.setup_solar_time(world, _Arguments(date="2026-12-21")) is True
    assert world.setters_called() == ["set_solar_date"]
    assert world.calls == [("set_solar_date", 2026, 12, 21)]


def test_advancing_alone_starts_the_sun_moving_from_where_it_already_is():
    world = _RecordingWorld(WORLD_SOLAR_STATE)

    assert WorldBuilder.setup_solar_time(
        world, _Arguments(time_advance=True, time_rate=3600.0)) is True
    assert world.setters_called() == ["set_time_advance"]
    assert world.calls == [("set_time_advance", True, 3600.0)]


def test_everything_asked_for_is_applied():
    """Asking is still honoured in full; the change is to what happens when nobody asks."""
    world = _RecordingWorld(WORLD_SOLAR_STATE)
    arguments = _Arguments(time="6", date="2026-06-21", time_advance=True, time_rate=60.0)

    assert WorldBuilder.solar_time_requested(arguments) is True
    assert WorldBuilder.setup_solar_time(world, arguments) is True
    assert world.calls == [
        ("set_solar_date", 2026, 6, 21),
        ("set_solar_time", 6.0),
        ("set_time_advance", True, 60.0),
    ]


@pytest.mark.parametrize("written,hours", [("00:00", 0.0), ("6", 6.0), ("17:30", 17.5),
                                           ("23:45", 23.75), ("12.5", 12.5)])
def test_a_time_is_read_the_same_either_way_it_is_written(written: str, hours: float):
    assert WorldBuilder.parse_solar_hours(written) == pytest.approx(hours)


def test_midnight_is_a_request_and_not_an_absence():
    """`--time 00:00` is falsy as a string is not; a request for midnight must survive the check."""
    assert WorldBuilder.solar_time_requested(_Arguments(time="00:00")) is True
    assert WorldBuilder.solar_time_requested(_Arguments(time=0.0)) is True

    world = _RecordingWorld(WORLD_SOLAR_STATE)
    WorldBuilder.setup_solar_time(world, _Arguments(time=0.0))
    assert world.calls == [("set_solar_time", 0.0)]
