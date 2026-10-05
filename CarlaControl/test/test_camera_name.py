"""A camera's name is used as given or refused, by the same rule the recorder applies.

`carlacontrol.CameraName` is the rule `CarlaNet.Recording.CameraName` applies, again, for the checks
made before anything reaches .NET -- `run_capture`'s offline validation of a channel's `sensor_id`
and the `--camera-name` of `run_SCTMV.py` and `run_free_move_camera.py`. The cases are those of
`CarlaNet/test/CarlaNet.Tests/Recording/CameraNameTests.cs`, so the two answer alike: a name is 1 to
63 ASCII letters, digits, underscores and hyphens, and every refusal says so.
"""
from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.CameraName import CameraName  # noqa: E402  (needs the path above)


def test_a_camera_given_no_name_is_named_by_its_actor_id():
    assert CameraName.default(42) == "CARLA-SENSOR-42"
    assert CameraName.problem(CameraName.default(42), 42) is None


ACCEPTED = [
    "OVERWATCH", "Overwatch_1", "Southeast_1700m_orbit", "NapOfEarth_2", "DECK-I25", "a", "-lead",
    "CONSOLE", "COM10", "CON_1", "Front_1", "frontier", "CARLA-SENSOR-", "CARLA-SENSOR-12a",
    "1234567890" * 6 + "123",
]


@pytest.mark.parametrize("name", ACCEPTED)
def test_a_short_plain_name_is_accepted(name):
    assert CameraName.problem(name) is None
    assert CameraName.argument(name) == name
    # The run configuration's schema states the same characters.
    assert re.fullmatch(CameraName.PATTERN, name)


REFUSED = [
    ("", "cannot be empty"),
    ("1234567890" * 6 + "1234", "is 64 characters long"),
    ("Deck Cam 1", "holds a space"),
    (" lead", "holds a space"),
    ("cam.v2", "holds '.'"),
    ("dot.", "holds '.'"),
    (".", "holds '.'"),
    ("Aux.cam", "holds '.'"),
    ("DECK:I25", "holds ':'"),
    ("a/b", "holds '/'"),
    ("a\\b", "holds '\\'"),
    ("a<b", "holds '<'"),
    ("a>b", "holds '>'"),
    ('a"b', "holds '\"'"),
    ("a|b", "holds '|'"),
    ("a?b", "holds '?'"),
    ("a*b", "holds '*'"),
    ("Deck#1", "holds '#'"),
    ("Deck(1)", "holds '('"),
    ("tab\there", "control character U+0009"),
    ("caf\u00e9", "U+00E9), which is not ASCII"),
    ("CON", "keeps for a device"),
    ("con", "keeps for a device"),
    ("Nul", "keeps for a device"),
    ("COM0", "keeps for a device"),
    ("COM1", "keeps for a device"),
    ("LPT9", "keeps for a device"),
    ("front", "role name the server gives sensors"),
    ("Back_Left", "role name the server gives sensors"),
    ("CARLA-SENSOR-12", "another camera's name"),
    ("carla-sensor-12", "another camera's name"),
]


@pytest.mark.parametrize(("name", "reason"), REFUSED)
def test_a_name_outside_the_rule_is_refused_saying_what_is_allowed(name, reason):
    problem = CameraName.problem(name)
    assert problem is not None and reason in problem
    # Every refusal points at what would be accepted.
    assert "Overwatch_1" in problem
    with pytest.raises(argparse.ArgumentTypeError, match=re.escape(reason)):
        CameraName.argument(name)


def test_the_default_form_is_accepted_only_as_the_default_of_the_camera_it_names():
    assert CameraName.problem("CARLA-SENSOR-42", 42) is None
    assert CameraName.problem("carla-sensor-42", 42) is None
    assert "is not camera 43's own" in CameraName.problem("CARLA-SENSOR-42", 43)
    assert "names no camera of its own" in CameraName.problem("CARLA-SENSOR-42")


def test_case_does_not_tell_two_names_apart():
    assert CameraName.same("Deck", "deck")
    assert not CameraName.same("Deck", "Deck-2")


def test_the_recorder_s_rule_gives_the_same_answer_word_for_word():
    """The rule the recorder and the shim apply, loaded from the CarlaNet assemblies, answers every
    case here as this mirror does. A wheel built before the rule was made plain has no `Allowed`,
    and the check waits for one that has (or for CARLANET_PUBLISH_DIR naming a fresh publish)."""
    pytest.importorskip("carlanet", reason="the recorder's rule is in the CarlaNet assemblies")
    from CarlaNet.Recording import CameraName as RecordersRule

    if not hasattr(RecordersRule, "Allowed"):
        pytest.skip("the loaded CarlaNet.Recording predates the plain-name rule; rebuild the wheel "
                    "or set CARLANET_PUBLISH_DIR to a fresh publish")
    cases = [(name, None) for name in ACCEPTED] + [(name, None) for name, _ in REFUSED] + \
        [("CARLA-SENSOR-42", 42), ("carla-sensor-42", 42), ("CARLA-SENSOR-42", 43)]
    for name, camera_id in cases:
        answered = RecordersRule.Problem(name, camera_id)
        assert (None if answered is None else str(answered)) == \
            CameraName.problem(name, camera_id), name
