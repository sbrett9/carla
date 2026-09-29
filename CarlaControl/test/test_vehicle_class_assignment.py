"""What the measured box says a vehicle is, and what a curated override may and may not change.

The content build's own `BaseType` is hand-edited and nothing checks it against the bodies -- when
first measured it was wrong on seven of seventeen vehicles and absent on an eighth -- so the catalogue
derives the kind from the measurement instead. A derivation from a box cannot see everything -- it
cannot tell a tall estate car from a van, an ambulance from the van it is built on, or a bus from a
lorry -- so a curation file corrects it. These exercise both halves: that the derivation puts the
measured bodies where they belong, and that the curation cannot become a second, unexplained truth.

The boxes below are the measured ones, so a change in the content that moved a body across a band edge
would show up here rather than only in a regenerated catalogue. A body the content registers but the
sweep has not yet measured has no box, and is listed apart rather than given an invented one.
"""
from __future__ import annotations

import os
import sys

import pytest

_REPO = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
sys.path.insert(0, os.path.join(_REPO, "CarlaControl", "src"))

from carlacontrol.VehicleClassAssignment import (  # noqa: E402
    VehicleClassAssignment,
    VehicleClassOverride,
)

# Measured by the sweep against the shipped content: length, width, height in metres.
MEASURED = {
    "vehicle.ambulance.ford": (6.3567, 2.3512, 2.4313),
    "vehicle.carlacola.actors": (8.0036, 2.9118, 4.0546),
    "vehicle.carlamotors.european_hgv": (7.9241, 2.8647, 3.7833),
    "vehicle.dodge.charger": (5.0058, 1.8813, 1.5403),
    "vehicle.dodgecop.charger": (5.2372, 1.9241, 1.6436),
    "vehicle.firetruck.actors": (8.5803, 2.9007, 3.8274),
    "vehicle.fuso.mitsubishi": (10.1737, 3.9276, 4.2414),
    "vehicle.jeep.wrangler_rubicon": (3.8662, 1.9052, 1.8779),
    "vehicle.lincoln.mkz": (4.8924, 1.8356, 1.5241),
    "vehicle.mini.cooper": (4.5526, 2.0956, 1.7725),
    "vehicle.nissan.patrol": (5.5909, 2.1469, 2.0593),
    "vehicle.sprinter.mercedes": (5.9152, 1.9878, 2.7263),
    "vehicle.taxi.ford": (5.3544, 1.7886, 1.5749),
    "vehicle.ue4.audi.tt": (4.1809, 1.9938, 1.3854),
    "vehicle.ue4.bmw.grantourer": (4.6113, 2.2423, 1.6672),
    "vehicle.ue4.chevrolet.impala": (5.3574, 2.0329, 1.4112),
    "vehicle.ue4.ford.crown": (5.3663, 1.8007, 1.5749),
    "vehicle.ue4.ford.mustang": (4.7180, 1.8946, 1.3005),
    "vehicle.ue4.mercedes.ccc": (4.6736, 1.8118, 1.4423),
}

# Registered in the content and named by a default class, but not yet swept, so there is no box to
# put in MEASURED. An entry moves there once the sweep has measured it.
AWAITING_MEASUREMENT: frozenset[str] = frozenset()


def classes_over_measured_bodies() -> tuple[dict, ...]:
    """The default classes with every body awaiting measurement taken out, dropping emptied classes."""
    kept = []
    for template in VehicleClassAssignment.DEFAULT_CLASSES:
        members = [m for m in template["members"] if m not in AWAITING_MEASUREMENT]
        if members:
            kept.append(dict(template, members=members))
    return tuple(kept)


@pytest.fixture
def records():
    return VehicleClassAssignment().assign(MEASURED)


def test_the_three_largest_bodies_are_derived_as_trucks_from_their_boxes(records):
    for blueprint_id in ("vehicle.carlacola.actors", "vehicle.firetruck.actors",
                         "vehicle.fuso.mitsubishi"):
        assert records[blueprint_id].derived_base_type == "truck"


def test_the_derivation_never_produces_a_bus():
    """A box cannot tell a bus from a lorry of the same size, so no measured box derives `bus`."""
    derived = {VehicleClassAssignment.derive_base_type(*box) for box in MEASURED.values()}
    assert "bus" not in derived


def test_the_six_saloons_the_content_declared_as_buses_derive_as_cars(records):
    saloons = ("vehicle.ue4.audi.tt", "vehicle.ue4.bmw.grantourer", "vehicle.ue4.chevrolet.impala",
               "vehicle.ue4.ford.crown", "vehicle.ue4.ford.mustang", "vehicle.ue4.mercedes.ccc")
    assert {records[blueprint_id].base_type for blueprint_id in saloons} == {"car"}
    assert all(records[blueprint_id].base_type_is_derived for blueprint_id in saloons)


def test_the_light_bus_is_curated_out_of_the_truck_band(records):
    record = records["vehicle.fuso.mitsubishi"]
    assert record.derived_base_type == "truck"
    assert record.base_type == "bus"
    assert not record.base_type_is_derived
    assert "bus" in record.override_reason


def test_the_light_bus_is_the_only_member_of_the_bus_class(records):
    classes = VehicleClassAssignment(classes=classes_over_measured_bodies()).build_classes(records)
    by_id = {entry["class_id"]: entry for entry in classes}
    assert [m["blueprint_id"] for m in by_id["bus"]["members"]] == ["vehicle.fuso.mitsubishi"]
    assert (by_id["bus"]["sumo_vclass"], by_id["bus"]["cot_base_type"]) == ("bus", "bus")
    assert "vehicle.fuso.mitsubishi" not in [m["blueprint_id"] for m in by_id["civ_truck"]["members"]]


def test_the_blueprint_with_no_declared_type_measures_as_a_van(records):
    assert records["vehicle.sprinter.mercedes"].base_type == "van"


def test_the_emergency_vehicles_carry_a_special_type(records):
    for blueprint_id in ("vehicle.ambulance.ford", "vehicle.dodgecop.charger",
                         "vehicle.firetruck.actors"):
        assert records[blueprint_id].special_type == "emergency"
    assert records["vehicle.taxi.ford"].special_type == "taxi"


def test_a_sport_utility_is_curated_back_out_of_the_van_band(records):
    record = records["vehicle.nissan.patrol"]
    assert record.derived_base_type == "van"
    assert record.base_type == "car"
    assert not record.base_type_is_derived
    assert "sport utility" in record.override_reason


def test_an_override_naming_a_blueprint_that_was_not_measured_is_refused():
    assignment = VehicleClassAssignment(
        overrides=(VehicleClassOverride("vehicle.harley.lowrider", base_type="motorcycle",
                                        reason="a motorcycle"),))
    with pytest.raises(ValueError, match="which the sweep did not measure"):
        assignment.assign(MEASURED)


def test_an_override_outside_the_truth_vocabulary_is_refused():
    assignment = VehicleClassAssignment(
        overrides=(VehicleClassOverride("vehicle.mini.cooper", base_type="hatchback",
                                        reason="it is a hatchback"),))
    with pytest.raises(ValueError, match="not one of"):
        assignment.assign(MEASURED)


def test_an_override_that_restates_the_derivation_is_refused():
    """Otherwise the file grows into a second copy of the derivation that nobody re-derives."""
    assignment = VehicleClassAssignment(
        overrides=(VehicleClassOverride("vehicle.mini.cooper", base_type="car",
                                        reason="it is a car"),))
    with pytest.raises(ValueError, match="restates"):
        assignment.assign(MEASURED)


def test_an_override_that_changes_a_type_without_saying_why_is_refused():
    assignment = VehicleClassAssignment(
        overrides=(VehicleClassOverride("vehicle.mini.cooper", base_type="van", reason="  "),))
    with pytest.raises(ValueError, match="carries no reason"):
        assignment.assign(MEASURED)


def test_every_measured_blueprint_reaches_exactly_one_class(records):
    classes = VehicleClassAssignment(classes=classes_over_measured_bodies()).build_classes(records)
    members = [member["blueprint_id"] for entry in classes for member in entry["members"]]
    assert sorted(members) == sorted(MEASURED)
    assert len(members) == len(set(members))


def test_the_default_classes_refuse_a_sweep_that_did_not_measure_a_registered_body():
    """What a sweep against a server still reading the previous registry would meet: a refusal that
    names the missing body, rather than a catalogue quietly short of it."""
    missing = "vehicle.jeep.wrangler_rubicon"
    short = {blueprint_id: box for blueprint_id, box in MEASURED.items() if blueprint_id != missing}
    with pytest.raises(ValueError, match="which the sweep did not measure") as refused:
        VehicleClassAssignment().build_classes(VehicleClassAssignment().assign(short))
    assert missing in str(refused.value)


def test_the_two_bodies_registered_last_take_the_derivation_unchanged(records):
    """The jeep reads as a car and the three-axle lorry as a truck from their boxes alone, so their
    classes need no curated override to agree with the truth record."""
    assert records["vehicle.jeep.wrangler_rubicon"].base_type == "car"
    assert records["vehicle.jeep.wrangler_rubicon"].base_type_is_derived
    assert records["vehicle.carlamotors.european_hgv"].base_type == "truck"
    assert records["vehicle.carlamotors.european_hgv"].base_type_is_derived


def test_a_class_whose_published_kind_disagrees_with_its_members_is_refused(records):
    """Truth would otherwise carry two different answers for the same vehicle."""
    classes = list(classes_over_measured_bodies())
    classes[0] = dict(classes[0], cot_base_type="truck")
    with pytest.raises(ValueError, match="two different answers"):
        VehicleClassAssignment(classes=tuple(classes)).build_classes(records)


def test_a_class_naming_a_vehicle_class_sumo_does_not_define_is_refused(records):
    classes = list(classes_over_measured_bodies())
    classes[0] = dict(classes[0], sumo_vclass="saloon")
    with pytest.raises(ValueError, match="which SUMO does not define"):
        VehicleClassAssignment(classes=tuple(classes)).build_classes(records)


def test_a_blueprint_reaching_no_class_is_refused(records):
    classes = list(classes_over_measured_bodies())
    classes[0] = dict(classes[0], members=classes[0]["members"][:-1])
    with pytest.raises(ValueError, match="reach no class"):
        VehicleClassAssignment(classes=tuple(classes)).build_classes(records)
