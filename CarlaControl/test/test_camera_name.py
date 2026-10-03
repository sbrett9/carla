"""A camera's name is used as given or refused, by the same rule the recorder applies.

`carlacontrol.CameraName` is the rule `CarlaNet.Recording.CameraName` applies, again, for the checks
made before anything reaches .NET -- `run_capture`'s offline validation of a channel's `sensor_id`
and `run_SCTMV.py`'s `--camera-name`. The cases are those of
`CarlaNet/test/CarlaNet.Tests/Recording/CameraNameTests.cs`, so the two answer alike.
"""
from __future__ import annotations

import argparse
import sys
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.CameraName import CameraName  # noqa: E402  (needs the path above)


def test_a_camera_given_no_name_is_named_by_its_actor_id():
    assert CameraName.default(42) == "CARLA-SENSOR-42"
    assert CameraName.problem(CameraName.default(42), 42) is None


@pytest.mark.parametrize("name", [
    "OVERWATCH", "DECK-I25", "Deck Cam #1 (I-25)", "a", "cam.v2", "-lead", "CONSOLE", "COM10",
    "CON_1", "CARLA-SENSOR-", "CARLA-SENSOR-12a", "1234567890" * 6 + "123",
])
def test_a_name_every_file_system_and_record_keeps_as_given_is_accepted(name):
    assert CameraName.problem(name) is None
    assert CameraName.argument(name) == name


@pytest.mark.parametrize(("name", "reason"), [
    ("", "cannot be empty"),
    ("1234567890" * 6 + "1234", "at most 63"),
    ("DECK:I25", "Windows file name"),
    ("a/b", "Windows file name"),
    ("a\\b", "Windows file name"),
    ("a<b", "Windows file name"),
    ("a>b", "Windows file name"),
    ('a"b', "Windows file name"),
    ("a|b", "Windows file name"),
    ("a?b", "Windows file name"),
    ("a*b", "Windows file name"),
    (" lead", "drops from a file name"),
    ("trail ", "drops from a file name"),
    ("dot.", "drops from a file name"),
    (".", "drops from a file name"),
    ("..", "drops from a file name"),
    ("CON", "keeps for a device"),
    ("con", "keeps for a device"),
    ("Aux.cam", "keeps for a device"),
    ("nul .x", "keeps for a device"),
    ("COM0", "keeps for a device"),
    ("COM1", "keeps for a device"),
    ("LPT9", "keeps for a device"),
    ("tab\there", "control character U+0009"),
    ("café", "printable ASCII"),
    ("CARLA-SENSOR-12", "another camera's name"),
    ("carla-sensor-12", "another camera's name"),
])
def test_a_name_that_would_not_reach_every_file_and_record_unchanged_is_refused_with_the_reason(
        name, reason):
    problem = CameraName.problem(name)
    assert problem is not None and reason in problem
    with pytest.raises(argparse.ArgumentTypeError, match=reason.replace("+", r"\+")):
        CameraName.argument(name)


def test_the_default_form_is_accepted_only_as_the_default_of_the_camera_it_names():
    assert CameraName.problem("CARLA-SENSOR-42", 42) is None
    assert CameraName.problem("carla-sensor-42", 42) is None
    assert "is not camera 43's own" in CameraName.problem("CARLA-SENSOR-42", 43)
    assert "names no camera of its own" in CameraName.problem("CARLA-SENSOR-42")


def test_case_does_not_tell_two_names_apart():
    assert CameraName.same("Deck", "deck")
    assert not CameraName.same("Deck", "Deck-2")
