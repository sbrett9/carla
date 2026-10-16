"""A vehicle whose extent the catalogue does not hold is refused, never placed at a guess.

SUMO reports a vehicle at its front bumper and CARLA places an actor at the body's origin, so the
bridge needs the measured length and box centre to convert one to the other. SUMO's own `length` is a
number somebody typed into a route file, and substituting it would put a body of unknown size at a
pose computed from an unmeasured number -- an error nothing downstream can detect, because the imagery
and the truth record would go on agreeing with each other while both were wrong.

So the reader has three refusals and no fallback, and this exercises all three against a catalogue
that contains exactly one measured vehicle and one that failed to measure.
"""
from __future__ import annotations

import json
import os
import sys

import pytest

_REPO = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
sys.path.insert(0, os.path.join(_REPO, "CarlaControl", "src"))

from carlacontrol.VehicleCatalogue import (  # noqa: E402
    BLUEPRINT_PARAM,
    NO_BLUEPRINT,
    UNKNOWN_EXTENT,
    UnrenderableVehicleTypeError,
    VehicleCatalogue,
)

CATALOGUE_PATH = os.path.join(_REPO, "CarlaControl", "catalogue", "vehicles.catalogue.json")

# Measured on the shipped content build: the Mini's box is 4.5526 m long and its centre sits 0.034 m
# behind the actor origin, so the bumper-to-origin shift is 2.2423 m rather than half the length.
DOCUMENT = {
    "catalogue_version": 1,
    "catalogue_id": "test",
    "catalogue_digest": "",
    "vehicles": [
        {"blueprint_id": "vehicle.mini.cooper", "measurement": "measured",
         "length_m": 4.5526, "width_m": 2.0956, "height_m": 1.7725,
         "bbox_centre_m": [-0.034, 0.0, 0.8884]},
        {"blueprint_id": "vehicle.ghost.prototype", "measurement": "failed",
         "measurement_note": "spawn refused: no blueprint class"},
    ],
    "classes": [],
}


@pytest.fixture
def catalogue() -> VehicleCatalogue:
    return VehicleCatalogue(DOCUMENT)


def test_a_measured_type_resolves_to_its_measured_box(catalogue):
    extent = catalogue.resolve("civ_car_mini", {BLUEPRINT_PARAM: "vehicle.mini.cooper"})
    assert extent.length_m == pytest.approx(4.5526)
    assert extent.width_m == pytest.approx(2.0956)
    assert extent.height_m == pytest.approx(1.7725)


def test_the_bumper_shift_uses_the_box_centre_and_not_half_the_length(catalogue):
    extent = catalogue.resolve("civ_car_mini", {BLUEPRINT_PARAM: "vehicle.mini.cooper"})
    assert extent.bumper_to_origin_m == pytest.approx(4.5526 / 2 - 0.034)
    assert extent.bumper_to_origin_m != pytest.approx(4.5526 / 2)
    assert extent.lateral_offset_m == pytest.approx(0.0)


def test_a_type_naming_no_blueprint_is_refused(catalogue):
    with pytest.raises(UnrenderableVehicleTypeError) as refused:
        catalogue.resolve("anomaly_probe", {"carla:class_id": "civ_car"})
    assert refused.value.reason == NO_BLUEPRINT
    assert catalogue.refusal("anomaly_probe", {}) == NO_BLUEPRINT


def test_a_type_naming_a_blueprint_the_catalogue_lacks_is_refused(catalogue):
    with pytest.raises(UnrenderableVehicleTypeError) as refused:
        catalogue.resolve("civ_bike", {BLUEPRINT_PARAM: "vehicle.harley.lowrider"})
    assert refused.value.reason == UNKNOWN_EXTENT
    assert "is not in catalogue" in refused.value.detail


def test_a_type_naming_a_failed_measurement_is_refused(catalogue):
    with pytest.raises(UnrenderableVehicleTypeError) as refused:
        catalogue.resolve("civ_ghost", {BLUEPRINT_PARAM: "vehicle.ghost.prototype"})
    assert refused.value.reason == UNKNOWN_EXTENT
    assert "measurement failed" in refused.value.detail
    assert "no blueprint class" in refused.value.detail


def test_a_renderable_type_produces_no_refusal(catalogue):
    assert catalogue.refusal("civ_car_mini", {BLUEPRINT_PARAM: "vehicle.mini.cooper"}) is None


def test_a_catalogue_of_another_version_is_refused_rather_than_read_loosely():
    with pytest.raises(ValueError, match="catalogue_version"):
        VehicleCatalogue(dict(DOCUMENT, catalogue_version=2))


@pytest.mark.skipif(not os.path.exists(CATALOGUE_PATH), reason="no catalogue has been swept yet")
def test_every_class_member_in_the_shipped_catalogue_has_an_extent():
    """Without a length and a box centre for every member, the pose conversion is undefined."""
    catalogue = VehicleCatalogue.load(CATALOGUE_PATH)
    for class_id, class_entry in catalogue.classes.items():
        for member in class_entry["members"]:
            extent = catalogue.extent_of(member["blueprint_id"])
            assert extent.length_m > 0, f"{class_id} draws a member with no length"
            assert extent.bumper_to_origin_m > 0


@pytest.mark.skipif(not os.path.exists(CATALOGUE_PATH), reason="no catalogue has been swept yet")
def test_the_shipped_catalogue_is_the_canonical_serialisation_of_itself():
    """The digest identifies the catalogue, so the file has to be the form the digest is taken over."""
    with open(CATALOGUE_PATH, encoding="utf-8") as handle:
        text = handle.read()
    document = json.loads(text)
    assert text == VehicleCatalogue.canonical_json(document)
    assert document["catalogue_digest"] == VehicleCatalogue.digest_of(document)
