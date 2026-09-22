"""A scenario's vehicle types name measured bodies, and a scenario whose types do not is refused.

The shipped Gardnerville routes file is the artifact under test: it is checked as it stands rather
than through a fixture, because a check that has only ever seen a fixture has not been pointed at
anything. Every refusal below is exercised too -- a validator that has never rejected anything has
not been tested, and the version of this scenario that shipped before the mapping existed is what
the first refusal is measured against.
"""
from __future__ import annotations

import json
import os
import sys
from xml.etree import ElementTree

import pytest

_REPO = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
sys.path.insert(0, os.path.join(_REPO, "CarlaControl", "src"))

from carlacontrol.ScenarioVehicleMix import ScenarioVehicleMix, VehicleClassSpec  # noqa: E402
from carlacontrol.SumoVehicleTypeWriter import SumoVehicleTypeWriter  # noqa: E402
from carlacontrol.VehicleCatalogue import VehicleCatalogue  # noqa: E402

CATALOGUE_PATH = os.path.join(_REPO, "CarlaControl", "catalogue", "vehicles.catalogue.json")
SCHEMA_PATH = os.path.join(_REPO, "Build", "sumo-install", "data", "xsd", "routes_file.xsd")
GARDNERVILLE_ROUTES = os.path.join(
    _REPO, "Import", "Gardnerville_Centerville_Lane_NeighborhoodOrbit.rou.xml")

# A body the content build does hold, used wherever a test needs a valid member.
A_MEASURED_CAR = "vehicle.mini.cooper"

pytestmark = pytest.mark.skipif(not os.path.exists(CATALOGUE_PATH),
                                reason="the measured vehicle catalogue is not present")


@pytest.fixture
def catalogue() -> VehicleCatalogue:
    return VehicleCatalogue.load(CATALOGUE_PATH)


@pytest.fixture
def car_class() -> VehicleClassSpec:
    return VehicleClassSpec(class_id="car", blueprints=(A_MEASURED_CAR,),
                            sumo_vclass="passenger", behaviour={"maxSpeed": "55"}, share=1.0)


def test_the_shipped_gardnerville_scenario_names_a_measured_body_for_every_type(catalogue):
    checked = ScenarioVehicleMix.check_route_file(GARDNERVILLE_ROUTES, catalogue)
    assert checked, "the scenario declares no vehicle types at all"


def test_a_type_without_a_blueprint_is_refused(catalogue, tmp_path):
    """The state the scenario shipped in: types that name no body, so nothing would be rendered."""
    unbound = tmp_path / "unbound.rou.xml"
    unbound.write_text(
        '<?xml version="1.0" encoding="UTF-8"?>\n<routes>\n'
        '  <vType id="car" vClass="passenger" length="4.6"/>\n'
        '</routes>\n', encoding="utf-8")
    with pytest.raises(ValueError, match="names no rendered body"):
        ScenarioVehicleMix.check_route_file(unbound, catalogue)


def test_a_declared_length_that_disagrees_with_the_body_is_refused(catalogue, tmp_path):
    """And the refusal states the pose bias, because half the difference is what it costs."""
    wrong = tmp_path / "wrong_length.rou.xml"
    wrong.write_text(
        '<?xml version="1.0" encoding="UTF-8"?>\n<routes>\n'
        '  <vType id="car" vClass="passenger" length="9.5" width="2.0956" height="1.7725">\n'
        f'    <param key="carla:blueprint" value="{A_MEASURED_CAR}"/>\n'
        '  </vType>\n</routes>\n', encoding="utf-8")
    with pytest.raises(ValueError, match=r"2\.47 m from where SUMO believes it is"):
        ScenarioVehicleMix.check_route_file(wrong, catalogue)


def test_a_flow_of_an_undeclared_type_is_refused(catalogue, tmp_path):
    dangling = tmp_path / "dangling.rou.xml"
    dangling.write_text(
        '<?xml version="1.0" encoding="UTF-8"?>\n<routes>\n'
        '  <flow id="ambient" type="ambient_mix" begin="0" end="60" vehsPerHour="10" '
        'from="a" to="b"/>\n</routes>\n', encoding="utf-8")
    with pytest.raises(ValueError, match="neither a vType nor a vTypeDistribution"):
        ScenarioVehicleMix.check_route_file(dangling, catalogue)


def test_a_class_naming_a_body_the_catalogue_does_not_hold_is_refused(catalogue):
    absent = VehicleClassSpec(class_id="pickup", blueprints=("vehicle.ford.f150",),
                              sumo_vclass="passenger", share=1.0)
    with pytest.raises(ValueError, match="is not in catalogue"):
        ScenarioVehicleMix(catalogue, [absent])


def test_a_class_that_declares_its_own_length_is_refused(catalogue):
    """A scenario that states a length has an opinion the measurement would silently overrule."""
    sized = VehicleClassSpec(class_id="car", blueprints=(A_MEASURED_CAR,),
                             sumo_vclass="passenger", behaviour={"length": "4.6"}, share=1.0)
    with pytest.raises(ValueError, match="A dimension comes from the measurement"):
        ScenarioVehicleMix(catalogue, [sized])


def test_a_two_wheeler_class_is_refused(catalogue):
    """Outside the contract: the rider would not be rendered and no such blueprint is registered."""
    two_wheeled = VehicleClassSpec(class_id="motorbike", blueprints=(A_MEASURED_CAR,),
                                   sumo_vclass="motorcycle", share=1.0)
    with pytest.raises(ValueError, match="outside the vehicle mapping contract"):
        ScenarioVehicleMix(catalogue, [two_wheeled])


def test_a_colour_written_as_a_comma_triple_is_refused(catalogue):
    """SUMO re-reads a triple of small numbers as fractions of 255, so hex is the only unambiguous form."""
    ambiguous = VehicleClassSpec(class_id="car", blueprints=(A_MEASURED_CAR,),
                                 sumo_vclass="passenger", share=1.0, gui_colour="1,0,0")
    with pytest.raises(ValueError, match="writes #RRGGBB"):
        ScenarioVehicleMix(catalogue, [ambiguous])


def test_a_mix_asked_for_with_no_shares_is_refused(catalogue, car_class):
    """A distribution nothing takes part in would leave every flow referring to an empty draw."""
    from dataclasses import replace
    with pytest.raises(ValueError, match="no class declares a share above zero"):
        ScenarioVehicleMix(catalogue, [replace(car_class, share=0.0)], mix_id="ambient_mix")


def test_the_mix_normalises_the_declared_shares(catalogue):
    """Shares that no longer sum to one are redistributed in proportion, not handed to one class.

    This is what happens when a class is dropped because the content build has no body for it: the
    ratios the remaining classes were authored with survive unchanged.
    """
    classes = [
        VehicleClassSpec(class_id="car", blueprints=(A_MEASURED_CAR, "vehicle.lincoln.mkz"),
                         sumo_vclass="passenger", share=0.45),
        VehicleClassSpec(class_id="suv", blueprints=("vehicle.nissan.patrol",),
                         sumo_vclass="passenger", share=0.18),
    ]
    probabilities = ScenarioVehicleMix(catalogue, classes, mix_id="ambient_mix").member_probabilities()
    assert sum(probabilities.values()) == pytest.approx(1.0)
    car = sum(value for name, value in probabilities.items() if name.startswith("car."))
    suv = sum(value for name, value in probabilities.items() if name.startswith("suv."))
    assert car / suv == pytest.approx(0.45 / 0.18)
    # Bodies inside a class draw equally unless the class weights them.
    assert probabilities["car." + A_MEASURED_CAR] == pytest.approx(car / 2)


def test_every_emitted_type_carries_the_measurement_verbatim(catalogue, car_class):
    document = json.loads(open(CATALOGUE_PATH, encoding="utf-8").read())
    measured = {entry["blueprint_id"]: entry for entry in document["vehicles"]}
    xml = ScenarioVehicleMix(catalogue, [car_class], mix_id="ambient_mix").to_xml()
    root = ElementTree.fromstring(f"<routes>{xml}</routes>")
    for element in root.findall("vType"):
        entry = measured[SumoVehicleTypeWriter.blueprint_of(element)]
        for attribute, field in (("length", "length_m"), ("width", "width_m"),
                                 ("height", "height_m")):
            assert float(element.get(attribute)) == pytest.approx(entry[field], abs=0.01)


@pytest.mark.skipif(not os.path.exists(SCHEMA_PATH), reason="the staged SUMO schema is not present")
def test_the_shipped_gardnerville_scenario_is_valid_against_sumos_own_schema():
    SumoVehicleTypeWriter.validate(GARDNERVILLE_ROUTES, SCHEMA_PATH)
