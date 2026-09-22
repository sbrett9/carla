"""The vehicle types the catalogue emits are valid against SUMO's own route schema.

The blueprint a type renders as travels in a `<param>` child of `<vType>`. That `<param>` is a
first-class element of `vTypeType` in SUMO's schema, and this checks it rather than asserting it: a
route file SUMO refuses to load is a scenario that cannot run, discovered at the far end of a build.

The schema is the one this repository stages and the distribution ships, not whichever SUMO is
installed on the machine running the test -- the two have been observed to differ by a patch release.
"""
from __future__ import annotations

import json
import os
import sys

import pytest

_REPO = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
sys.path.insert(0, os.path.join(_REPO, "CarlaControl", "src"))

from carlacontrol.SumoVehicleTypeWriter import SumoVehicleTypeWriter  # noqa: E402
from carlacontrol.VehicleCatalogue import BLUEPRINT_PARAM  # noqa: E402

CATALOGUE_DIRECTORY = os.path.join(_REPO, "CarlaControl", "catalogue")
CATALOGUE_PATH = os.path.join(CATALOGUE_DIRECTORY, "vehicles.catalogue.json")
VEHICLE_TYPES_PATH = os.path.join(CATALOGUE_DIRECTORY, "vehicles.vtypes.rou.xml")
SCHEMA_PATH = os.path.join(_REPO, "Build", "sumo-install", "data", "xsd", "routes_file.xsd")

pytestmark = pytest.mark.skipif(
    not (os.path.exists(CATALOGUE_PATH) and os.path.exists(SCHEMA_PATH)),
    reason="the catalogue or the staged SUMO schema is not present")


@pytest.fixture
def document() -> dict:
    with open(CATALOGUE_PATH, encoding="utf-8") as handle:
        return json.load(handle)


def test_the_emitted_vehicle_types_validate_against_the_staged_schema():
    SumoVehicleTypeWriter.validate(VEHICLE_TYPES_PATH, SCHEMA_PATH)


def test_a_document_that_breaks_the_schema_is_rejected(tmp_path):
    """The control: a validator that accepts anything would pass the test above just as well."""
    broken = tmp_path / "broken.rou.xml"
    broken.write_text(
        '<?xml version="1.0" encoding="UTF-8"?>\n'
        '<routes>\n'
        '  <vType id="vehicle.mini.cooper" length="not a number"/>\n'
        '</routes>\n', encoding="utf-8")
    with pytest.raises(ValueError, match="not valid against"):
        SumoVehicleTypeWriter.validate(broken, SCHEMA_PATH)


def test_every_type_names_its_blueprint_in_a_param(document):
    from xml.etree import ElementTree

    root = ElementTree.parse(VEHICLE_TYPES_PATH).getroot()
    types = root.findall("vType")
    measured = [entry["blueprint_id"] for entry in document["vehicles"]
                if entry["measurement"] == "measured"]
    assert len(types) == len(measured)
    for element in types:
        blueprint = SumoVehicleTypeWriter.blueprint_of(element)
        assert blueprint in measured, f"{element.get('id')} names no known blueprint"


def test_every_declared_length_equals_the_measurement(document):
    """A vType whose length disagrees with the body biases every pose by half the difference."""
    from xml.etree import ElementTree

    measured = {entry["blueprint_id"]: entry for entry in document["vehicles"]
                if entry["measurement"] == "measured"}
    root = ElementTree.parse(VEHICLE_TYPES_PATH).getroot()
    for element in root.findall("vType"):
        entry = measured[SumoVehicleTypeWriter.blueprint_of(element)]
        for attribute, field in (("length", "length_m"), ("width", "width_m"),
                                 ("height", "height_m")):
            assert abs(float(element.get(attribute)) - entry[field]) <= 0.01


def test_a_type_distribution_exists_for_every_class(document):
    from xml.etree import ElementTree

    root = ElementTree.parse(VEHICLE_TYPES_PATH).getroot()
    declared = {element.get("id") for element in root.findall("vTypeDistribution")}
    assert declared == {entry["class_id"] for entry in document["classes"]}


def test_a_class_member_with_no_measurement_stops_the_write(document):
    """A class may not name a blueprint the sweep failed on: SUMO would size it from a default."""
    document = json.loads(json.dumps(document))
    document["classes"][0]["members"].append(
        {"blueprint_id": "vehicle.harley.lowrider", "weight": 1.0})
    with pytest.raises(KeyError, match="no measured"):
        SumoVehicleTypeWriter(document).to_xml()


def test_the_blueprint_param_key_is_the_one_the_reader_looks_for():
    """The writer and the reader must not drift apart on the key that carries the blueprint."""
    from xml.etree import ElementTree

    root = ElementTree.parse(VEHICLE_TYPES_PATH).getroot()
    first = root.find("vType")
    assert any(param.get("key") == BLUEPRINT_PARAM for param in first.findall("param"))
