"""A channel description's defaults are the plan's defaults, and a description that cannot work is refused.

`ChannelDescription` is where a camera channel's defaults are defined, once, so that a second front
end cannot quietly ship a different number from the first -- the failure the operator-surface plan
measured three times over (12 section 1.5). The plan's table of per-channel fields and this class
therefore have to agree, and the first test here reads that table out of the plan document itself
rather than copying its numbers, so a change to either side that is not made to the other fails.

The rest is refusal. A stare with nowhere to look, an orbit with no centre, a picture with no pixels:
each would otherwise reach the server as a camera looking at the world origin, or as a spawn error
that names nothing the operator typed. Every problem is named in one message.
"""
from __future__ import annotations

import math
import re
import sys
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.ChannelDescription import ChannelDescription  # noqa: E402

PLAN_12 = (_REPO / "Docs" / "CAT_Research" / "Plans" / "SUMO_Behavioral_Capture"
           / "12_Operator_Control_Surface.md")

# The values the camera-rig table gave these fields when this class was written, repeated so that
# the table and the class drifting together is caught as well as the two drifting apart.
PLAN_DEFAULTS = {
    "sensor_id": None,
    "pattern": "stare",
    "fov": 90.0,
    "width": 1280,
    "height": 720,
    "orbit_radius_m": 200.0,
    "orbit_altitude_m": 518.2,
    "orbit_period_s": 240.0,
}

A_STARE = {"stare_look_at_x_m": 120.0, "stare_look_at_y_m": -340.0}
AN_ORBIT = {"pattern": "orbit", "orbit_centre_x_m": 120.0, "orbit_centre_y_m": -340.0}
A_POSE = {"stare_x_m": 120.0, "stare_y_m": 60.0, "stare_z_m": 300.0,
          "stare_pitch_deg": -37.0, "stare_yaw_deg": -90.0}


class _CameraRigTable:
    """The per-channel camera-rig rows of plan 12 section 5.2, as field name -> default.

    A row names one or more fields in its first cell and their defaults, in the same order, in its
    second. A default cell that opens with the plan's no-default marks (**—** or **cond.**) gives
    every field in the row no default.
    """

    HEADING = "#### Camera rig — per channel"
    NO_DEFAULT_MARKS = ("**—**", "**cond.**")

    @classmethod
    def read(cls, path: Path) -> dict[str, object]:
        text = path.read_text(encoding="utf-8")
        start = text.index(cls.HEADING)
        rows: dict[str, object] = {}
        in_table = False
        for line in text[start:].splitlines()[1:]:
            if line.startswith("|"):
                in_table = True
                cells = [cell.strip() for cell in line.strip("|").split("|")]
                if cells[0] in ("Toggle", "") or set(cells[0]) <= set("-: "):
                    continue
                rows.update(cls._row(cells[0], cells[1]))
            elif in_table:
                break
        return rows

    @classmethod
    def _row(cls, names_cell: str, default_cell: str) -> dict[str, object]:
        names = re.findall(r"`([^`]+)`", names_cell)
        if default_cell.startswith(cls.NO_DEFAULT_MARKS):
            return dict.fromkeys(names)
        values = [cls._value(token) for token in re.findall(r"`([^`]+)`", default_cell)]
        if len(values) != len(names):
            # A row whose default is prose (a conditional value, or "not offered") has no single
            # default to compare, and none of those rows name a field this class carries.
            return {}
        return dict(zip(names, values, strict=True))

    @staticmethod
    def _value(token: str) -> object:
        for kind in (int, float):
            try:
                return kind(token)
            except ValueError:
                continue
        return token


def test_every_field_has_the_default_the_plan_gives_it():
    table = _CameraRigTable.read(PLAN_12)

    missing = [name for name in ChannelDescription.field_names() if name not in table]
    assert missing == [], f"fields with no row in plan 12's camera-rig table: {missing}"
    for name in ChannelDescription.field_names():
        assert ChannelDescription.default_of(name) == table[name], name


def test_the_named_defaults_are_the_plans_values():
    for name, value in PLAN_DEFAULTS.items():
        assert ChannelDescription.default_of(name) == value, name


def test_default_of_refuses_a_name_that_is_not_a_field():
    with pytest.raises(KeyError):
        ChannelDescription.default_of("orbit_radius")


@pytest.mark.parametrize("fields", [A_STARE, A_POSE, AN_ORBIT], ids=["look-at", "pose", "orbit"])
def test_a_complete_description_is_accepted(fields):
    channel = ChannelDescription(**fields)
    for name, value in fields.items():
        assert getattr(channel, name) == value


# Each case is one thing wrong with an otherwise workable description, and a phrase the refusal must
# contain so an operator can tell which thing it was.
REFUSED = {
    "no pixels wide": ({**A_STARE, "width": 0}, "width"),
    "negative height": ({**A_STARE, "height": -720}, "height"),
    "fractional width": ({**A_STARE, "width": 1280.5}, "width"),
    "a flag for a width": ({**A_STARE, "width": True}, "width"),
    "text for a field of view": ({**A_STARE, "fov": "90"}, "fov"),
    "no field of view": ({**A_STARE, "fov": 0.0}, "fov"),
    "a half-sphere field of view": ({**A_STARE, "fov": 180.0}, "fov"),
    "an unset field of view": ({**A_STARE, "fov": None}, "fov"),
    "infinite altitude": ({**A_STARE, "stare_altitude_m": math.inf}, "stare_altitude_m"),
    "not a number": ({**AN_ORBIT, "orbit_radius_m": math.nan}, "orbit_radius_m"),
    "transit is not built": ({**A_STARE, "pattern": "transit"}, "not built"),
    "an unknown pattern": ({**A_STARE, "pattern": "hover"}, "pattern"),
    "an unnameable sensor": ({**A_STARE, "sensor_id": "overwatch 1"}, "sensor_id"),
    "an empty sensor name": ({**A_STARE, "sensor_id": ""}, "sensor_id"),
    "a stare with nowhere to look": ({}, "needs somewhere to look"),
    "a look-at point missing y": ({"stare_look_at_x_m": 1.0}, "stare_look_at_y_m"),
    "both stare forms": ({**A_STARE, **A_POSE}, "not both"),
    "a pose missing its yaw": ({k: v for k, v in A_POSE.items() if k != "stare_yaw_deg"},
                               "stare_yaw_deg"),
    "a pitch past straight down": ({**A_POSE, "stare_pitch_deg": -120.0}, "stare_pitch_deg"),
    "an orbit centre on a stare": ({**A_STARE, "orbit_centre_x_m": 5.0}, "orbit centre"),
    "an orbit with no centre": ({"pattern": "orbit"}, "needs a centre"),
    "an orbit centre missing y": ({"pattern": "orbit", "orbit_centre_x_m": 5.0},
                                  "orbit_centre_y_m"),
    "a look-at point on an orbit": ({**AN_ORBIT, **A_STARE}, "given for an orbit"),
    "no orbit radius": ({**AN_ORBIT, "orbit_radius_m": 0.0}, "orbit_radius_m"),
    "an orbit below its centre": ({**AN_ORBIT, "orbit_altitude_m": -10.0}, "orbit_altitude_m"),
    "a revolution in no time": ({**AN_ORBIT, "orbit_period_s": 0.0}, "orbit_period_s"),
    "a stare standing off backwards": ({**A_STARE, "stare_standoff_m": -1.0}, "stare_standoff_m"),
    "a stare at ground level": ({**A_STARE, "stare_altitude_m": 0.0}, "stare_altitude_m"),
}


@pytest.mark.parametrize("fields, named", list(REFUSED.values()), ids=list(REFUSED))
def test_an_unworkable_description_is_refused_naming_what_is_wrong(fields, named):
    with pytest.raises(ValueError, match="channel description refused") as refusal:
        ChannelDescription(**fields)
    assert named in str(refusal.value)


def test_every_problem_is_named_in_one_refusal():
    with pytest.raises(ValueError) as refusal:
        ChannelDescription(pattern="orbit", width=0, fov=200.0)
    message = str(refusal.value)
    for named in ("width", "fov", "needs a centre"):
        assert named in message
