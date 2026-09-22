"""The catalogue's generation-time checks, pointed at the catalogue in the tree and at broken copies.

A validator that has never rejected anything has not been tested, so every rule here is exercised by
breaking the real artifact in exactly one way and watching the check name it. The unbroken artifact
passing is the control: without it, a validator that rejects everything would look just as good.
"""
from __future__ import annotations

import copy
import json
import os
import sys

import pytest

_REPO = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
sys.path.insert(0, os.path.join(_REPO, "CarlaControl", "src"))

from carlacontrol.VehicleCatalogueValidator import VehicleCatalogueValidator  # noqa: E402

CATALOGUE_PATH = os.path.join(_REPO, "CarlaControl", "catalogue", "vehicles.catalogue.json")

pytestmark = pytest.mark.skipif(not os.path.exists(CATALOGUE_PATH),
                                reason="no catalogue has been swept yet")


@pytest.fixture
def document() -> dict:
    with open(CATALOGUE_PATH, encoding="utf-8") as handle:
        return json.load(handle)


def broken(document: dict, change) -> list[str]:
    """Apply one change to a copy of the catalogue and return what the validator says about it."""
    copied = copy.deepcopy(document)
    change(copied)
    return VehicleCatalogueValidator(copied).failures()


def test_the_catalogue_in_the_tree_passes_every_check(document):
    assert VehicleCatalogueValidator(document).failures() == []


def test_a_measured_entry_without_a_box_centre_is_refused(document):
    """The bumper-to-origin shift is undefined without it, and the bridge cannot place the body."""
    def remove_centre(copied):
        del copied["vehicles"][0]["bbox_centre_m"]

    assert any("bbox_centre_m" in failure for failure in broken(document, remove_centre))


def test_an_implausible_dimension_is_refused(document):
    def stretch(copied):
        copied["vehicles"][0]["length_m"] = 91.4

    assert any(failure.startswith("V1.2:") for failure in broken(document, stretch))


def test_a_missing_lamp_key_is_refused_rather_than_read_as_unmeasured(document):
    """An absent key and a key reading `unknown` must not collapse into the same thing."""
    def drop_a_lamp(copied):
        del copied["vehicles"][0]["lamp_capability"]["fog"]

    failures = broken(document, drop_a_lamp)
    assert any("missing lamp 'fog'" in failure for failure in failures)


def test_a_lamp_verdict_outside_the_vocabulary_is_refused(document):
    def invent_a_verdict(copied):
        copied["vehicles"][0]["lamp_capability"]["brake"] = "probably"

    assert any("'probably'" in failure for failure in broken(document, invent_a_verdict))


def test_a_lamp_probe_that_does_not_say_whether_it_ran_is_refused(document):
    def forget_to_say(copied):
        del copied["lamp_probe"]["ran"]

    assert any("lamp_probe" in failure for failure in broken(document, forget_to_say))


def test_a_class_drawing_a_blueprint_that_was_never_measured_is_refused(document):
    def add_a_ghost(copied):
        copied["classes"][0]["members"].append(
            {"blueprint_id": "vehicle.harley.lowrider", "weight": 1.0})

    assert any(failure.startswith("V1.5:") for failure in broken(document, add_a_ghost))


def test_a_class_that_omits_its_speed_deviation_is_refused(document):
    """A scalar speedFactor leaves SUMO's per-class default deviation in place, invisibly."""
    def drop_the_deviation(copied):
        del copied["classes"][0]["speed_factor_dev"]

    assert any("speed_factor_dev" in failure for failure in broken(document, drop_the_deviation))


def test_a_class_with_no_members_is_refused(document):
    def empty_it(copied):
        copied["classes"][0]["members"] = []

    assert any(failure.startswith("V1.7:") for failure in broken(document, empty_it))


def test_a_sumo_colour_written_as_a_comma_triple_is_refused(document):
    """SUMO re-reads a triple whose components are all at most 1 as fractions of 255."""
    def use_fractions(copied):
        copied["classes"][0]["gui_colour"] = "0.80,0.80,0.82"

    assert any(failure.startswith("V1.15:") for failure in broken(document, use_fractions))


def test_a_colour_verdict_outside_the_three_values_is_refused(document):
    def assume_it_worked(copied):
        copied["vehicles"][0]["colour_applied"] = True

    assert any(failure.startswith("V1.4:") for failure in broken(document, assume_it_worked))


def test_validate_raises_and_names_every_failure_at_once(document):
    copied = copy.deepcopy(document)
    del copied["vehicles"][0]["bbox_centre_m"]
    copied["classes"][0]["members"] = []
    with pytest.raises(ValueError) as refused:
        VehicleCatalogueValidator(copied).validate()
    assert "breaks 2 rule(s)" in str(refused.value)
