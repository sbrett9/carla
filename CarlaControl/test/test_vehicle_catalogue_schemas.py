"""The vehicle catalogue's three files are what their schemas say, and their loaders hold them to it.

  * `vehicles.catalogue.json` and `vehicle_body_widths.json` match the JSON Schemas
    `VehicleCatalogueSchemas` generates, which are the published ones;
  * `vehicles.vtypes.rou.xml`, and the route file `SumoVehicleTypeWriter` writes from the catalogue now,
    match `vehicle_types.xsd`, whose attributes are the ones the writer writes;
  * `VehicleCatalogue.load` and `VehicleCatalogueBuilder.load_body_widths` refuse a file that departs
    from its schema, naming the departure.
"""
from __future__ import annotations

import copy
import importlib.util
import json
import sys
from pathlib import Path

import pytest
from lxml import etree

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.JsonSchemaFile import JsonSchemaFile  # noqa: E402
from carlacontrol.SumoVehicleTypeWriter import SumoVehicleTypeWriter  # noqa: E402
from carlacontrol.VehicleCatalogue import VehicleCatalogue  # noqa: E402
from carlacontrol.VehicleCatalogueBuilder import (  # noqa: E402
    BODY_WIDTHS_VERSION as BUILDER_BODY_WIDTHS_VERSION,
)
from carlacontrol.VehicleCatalogueBuilder import VehicleCatalogueBuilder  # noqa: E402
from carlacontrol.VehicleCatalogueSchemas import (  # noqa: E402
    BODY_WIDTHS_VERSION,
    VehicleCatalogueSchemas,
)

CATALOGUE_DIRECTORY = _REPO / "CarlaControl" / "catalogue"
CATALOGUE = CATALOGUE_DIRECTORY / "vehicles.catalogue.json"
BODY_WIDTHS = CATALOGUE_DIRECTORY / "vehicle_body_widths.json"
VEHICLE_TYPES = CATALOGUE_DIRECTORY / "vehicles.vtypes.rou.xml"
SCHEMAS = _REPO / "CarlaControl" / "schemas"
VEHICLE_TYPES_SCHEMA = SCHEMAS / "vehicle_types.xsd"
XSD = "{http://www.w3.org/2001/XMLSchema}"
REGENERATE = ("regenerate with: python -c \"from carlacontrol.VehicleCatalogueSchemas import "
              "VehicleCatalogueSchemas; VehicleCatalogueSchemas.write('CarlaControl/schemas')\"")


def shipped() -> dict:
    return json.loads(CATALOGUE.read_text(encoding="utf-8"))


@pytest.mark.parametrize("name", sorted(VehicleCatalogueSchemas.schemas()))
def test_the_published_schema_is_the_generated_one(name):
    assert (SCHEMAS / name).read_text(encoding="utf-8") == JsonSchemaFile.text(
        VehicleCatalogueSchemas.schemas()[name]), REGENERATE


def test_the_body_width_version_is_the_one_the_builder_reads():
    assert BODY_WIDTHS_VERSION == BUILDER_BODY_WIDTHS_VERSION
    measurer = _REPO / "CarlaControl" / "scripts" / "measure_vehicle_body_widths.py"
    spec = importlib.util.spec_from_file_location("measure_vehicle_body_widths", measurer)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    assert module.BODY_WIDTHS_VERSION == BODY_WIDTHS_VERSION


def test_the_shipped_catalogue_matches_its_schema():
    assert JsonSchemaFile.problems(shipped(), VehicleCatalogueSchemas.catalogue()) == []


def test_the_shipped_body_widths_match_their_schema():
    table = json.loads(BODY_WIDTHS.read_text(encoding="utf-8"))
    assert JsonSchemaFile.problems(table, VehicleCatalogueSchemas.body_widths()) == []


def test_every_class_parameter_the_validator_requires_is_described():
    schema = VehicleCatalogueSchemas.catalogue()
    vehicle_class = schema["properties"]["classes"]["items"]
    for name in ("accel_mps2", "decel_mps2", "sigma", "speed_factor_mean", "speed_factor_dev",
                 "min_gap_m", "max_speed_mps"):
        assert name in vehicle_class["required"]
        assert vehicle_class["properties"][name]["description"].startswith("SUMO ")


# -- the loaders refuse what their schema refuses -----------------------------------------------------

def test_a_catalogue_that_departs_from_its_schema_is_refused_naming_each_departure(tmp_path):
    document = shipped()
    del document["vehicles"][0]["lamp_capability"]["fog"]
    document["classes"][0]["gui_colour"] = "204,204,209"
    path = tmp_path / "vehicles.catalogue.json"
    path.write_text(json.dumps(document), encoding="utf-8")
    with pytest.raises(ValueError) as refused:
        VehicleCatalogue.load(path)
    message = str(refused.value)
    assert str(path) in message
    assert "urn:carla-sumo-capture:schema:vehicle-catalogue:1" in message
    assert "'fog' is required" in message
    assert "$.classes[0].gui_colour" in message


def test_a_catalogue_with_only_some_header_fields_loads_and_a_malformed_one_does_not(tmp_path):
    path = tmp_path / "other.catalogue.json"
    path.write_text(json.dumps({"catalogue_version": 1, "catalogue_id": "other", "classes": []}),
                    encoding="utf-8")
    assert VehicleCatalogue.load(path).catalogue_digest == ""
    path.write_text(json.dumps({"catalogue_version": 1, "catalogue_id": "other",
                                "catalogue_digest": "not a digest"}), encoding="utf-8")
    with pytest.raises(ValueError, match=r"\$\.catalogue_digest"):
        VehicleCatalogue.load(path)


def test_the_shipped_catalogue_loads():
    assert VehicleCatalogue.load(CATALOGUE).catalogue_id == shipped()["catalogue_id"]


def test_a_newer_catalogue_is_still_refused_for_its_version_first(tmp_path):
    path = tmp_path / "vehicles.catalogue.json"
    path.write_text(json.dumps({"catalogue_version": 2, "vehicles": []}), encoding="utf-8")
    with pytest.raises(ValueError, match="written by a newer release"):
        VehicleCatalogue.load(path)


def test_a_body_width_table_that_departs_from_its_schema_is_refused(tmp_path):
    table = json.loads(BODY_WIDTHS.read_text(encoding="utf-8"))
    first = next(iter(table["vehicles"]))
    table["vehicles"][first]["body_width_m"] = "2.2"
    path = tmp_path / "vehicle_body_widths.json"
    path.write_text(json.dumps(table), encoding="utf-8")
    with pytest.raises(ValueError) as refused:
        VehicleCatalogueBuilder.load_body_widths(path)
    assert f"$.vehicles.{first}.body_width_m: must be number" in str(refused.value)


# -- vehicles.vtypes.rou.xml ----------------------------------------------------------------------------

@pytest.fixture(scope="module")
def vehicle_types_schema() -> etree.XMLSchema:
    return etree.XMLSchema(etree.parse(str(VEHICLE_TYPES_SCHEMA)))


def test_the_shipped_vehicle_types_match_their_schema(vehicle_types_schema):
    document = etree.parse(str(VEHICLE_TYPES))
    assert vehicle_types_schema.validate(document), vehicle_types_schema.error_log


def test_the_vehicle_types_written_now_match_their_schema(vehicle_types_schema):
    written = etree.fromstring(SumoVehicleTypeWriter(shipped()).to_xml().encode("utf-8"))
    assert vehicle_types_schema.validate(written), vehicle_types_schema.error_log


def test_the_schema_s_vehicle_type_attributes_are_the_writer_s():
    schema = etree.parse(str(VEHICLE_TYPES_SCHEMA))
    (vehicle_type,) = schema.findall(f"{XSD}complexType[@name='VehicleType']")
    declared = [attribute.get("name") for attribute in vehicle_type.findall(f"{XSD}attribute")]
    written = etree.fromstring(SumoVehicleTypeWriter(shipped()).to_xml().encode("utf-8"))
    for element in written.findall("vType"):
        assert list(element.attrib) == declared


def test_a_vehicle_type_with_a_parameter_of_its_own_is_refused(vehicle_types_schema):
    written = etree.fromstring(SumoVehicleTypeWriter(shipped()).to_xml().encode("utf-8"))
    extra = copy.deepcopy(written.find("vType/param"))
    extra.set("key", "has.ssm.device")
    written.find("vType").append(extra)
    assert not vehicle_types_schema.validate(written)


@pytest.mark.parametrize("source", ["shipped", "written"])
def test_every_distribution_draws_only_types_the_file_holds(source):
    """The schema types vTypes as IDREFS, which libxml2 does not check, so it is checked here."""
    root = (etree.parse(str(VEHICLE_TYPES)).getroot() if source == "shipped"
            else etree.fromstring(SumoVehicleTypeWriter(shipped()).to_xml().encode("utf-8")))
    held = {element.get("id") for element in root.findall("vType")}
    for distribution in root.findall("vTypeDistribution"):
        drawn = distribution.get("vTypes").split()
        assert set(drawn) <= held, distribution.get("id")
        assert len(distribution.get("probabilities").split()) == len(drawn)
