"""Every schema we publish is its generator's output, a JSON Schema 2020-12 document named by its URN,
checkable by the validator our readers use, and described on a page of its own.

Every file in `CarlaControl/schemas/` is one `PublishedSchemas` lists: generated in Python, generated
by the CarlaNet writers of what a capture writes, or written by hand. A generated schema comes from the
code that writes or reads its file and is never edited, so a checked-in copy that differs from its
generator, by a byte, is a schema describing something the code no longer does: each is held equal to
what its generator makes now, the capture schemas to what the CarlaNet this suite loaded makes, and
`carla-validate --write-schemas` writes them all. Its `$id` names the file kind and the format version
the file declares, so a reader holding a file can tell which schema is its. Each schema uses only the
keywords the readers' validator (`ScenarioSchema.validate_against`) enforces, so a published schema
says no more than a reader checks.
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

import carlanet  # noqa: E402, F401  -- loads the CarlaNet assemblies the next imports name
import clr  # noqa: E402
from CarlaNet.CoSim import SolarEpoch  # noqa: E402
from System.Reflection import BindingFlags  # noqa: E402

from carlacontrol.commands.validate import main as validate  # noqa: E402
from carlacontrol.ProducerRecord import ProducerRecord  # noqa: E402
from carlacontrol.PublishedSchemas import (  # noqa: E402
    CAPTURE_SCHEMAS,
    GENERATED,
    HAND_WRITTEN,
    PublishedSchemas,
)
from carlacontrol.ResolutionReport import SECTIONS  # noqa: E402
from carlacontrol.ScenarioEpoch import EPOCH_VERSION, ScenarioEpoch  # noqa: E402
from carlacontrol.ScenarioOutputSchemas import ScenarioOutputSchemas  # noqa: E402
from carlacontrol.ScenarioSchema import SCHEMA, ScenarioSchema  # noqa: E402
from carlacontrol.ScenarioSweep import SWEEP_SCHEMA  # noqa: E402
from carlacontrol.SchemaIdentifier import DIALECT, SchemaIdentifier  # noqa: E402
from carlacontrol.SchemaPublication import SchemaPublication  # noqa: E402

SCHEMAS = _REPO / "CarlaControl" / "schemas"
SKILL_SCHEMAS = _REPO / "CarlaControl" / "skills" / "sumo-traffic-scenarios" / "schemas"
PAGES_DIRECTORY = _REPO / "Docs" / "CAT_Research" / "Schemas"
INDEX = PAGES_DIRECTORY / "README.md"
# Each schema's description page: every schema in CarlaControl/schemas, and the authoring skill's two.
PAGES = {
    "truth_sidecar.xsd": "Truth_Sidecar.md",
    "png_chunk_capture.schema.json": "PNG_Chunk_Capture.md",
    "png_chunk_solar.schema.json": "PNG_Chunk_Solar.md",
    "png_chunk_illumination.schema.json": "PNG_Chunk_Illumination.md",
    "png_chunk_sensor.schema.json": "PNG_Chunk_Sensor.md",
    "run_manifest.schema.json": "Run_Manifest.md",
    "world_truth_track.tableschema.json": "World_Truth_Track.md",
    "world_truth_track_summary.schema.json": "World_Truth_Track_Summary.md",
    "run_configuration.schema.json": "Run_Configuration.md",
    "run_result.schema.json": "Run_Result.md",
    "run_resolution.schema.json": "Run_Resolution_Report.md",
    "run_lock.schema.json": "Run_Lock.md",
    "launch_echo.schema.json": "Launch_Echo.md",
    "site_profile.schema.json": "Site_Profile.md",
    "scenario.schema.json": "Scenario_Specification.md",
    "sweep.schema.json": "Sweep.md",
    "scenario_lock.schema.json": "Scenario_Lock.md",
    "scenario_resolution.schema.json": "Scenario_Resolution_Report.md",
    "supervision_plan.schema.json": "Supervision_Plan.md",
    "sweep_index.schema.json": "Sweep_Index.md",
    "scenario_checks.schema.json": "Scenario_Checks.md",
    "epoch.schema.json": "Epoch.md",
    "display_convention.schema.json": "Display_Convention.md",
    "area_of_interest.schema.json": "Areas_Of_Interest.md",
    "world_package_manifest.schema.json": "World_Package_Manifest.md",
    "place_index.schema.json": "Place_Index.md",
    "solar_frame.schema.json": "Solar_Frame.md",
    "areas_resolved.schema.json": "Areas_Resolved.md",
    "level_package_manifest.schema.json": "Level_Package_Manifest.md",
    "vehicle_catalogue.schema.json": "Vehicle_Catalogue.md",
    "vehicle_body_widths.schema.json": "Vehicle_Body_Widths.md",
    "vehicle_types.xsd": "Vehicle_Types.md",
    "cot_telemetry.xsd": "CoT_Telemetry_Stream.md",
    "cot_event_body.xsd": "CoT_Telemetry_Stream.md",
    "sumo_cot_events.xsd": "SUMO_CoT_Event_File.md",
    "sumo_cot_telemetry.tableschema.json": "SUMO_CoT_Table.md",
    "sumo_cot_telemetry_summary.schema.json": "SUMO_CoT_Table.md",
    "supervision_gaps.schema.json": "Supervision_Gaps.md",
    "legacy_labels.schema.json": "Legacy_Labels.md",
}
# A page's name: Title_Case_With_Underscores, each word capitalized or an acronym in its own capitals.
PAGE_NAME = re.compile(r"^([A-Z][a-z0-9]*|[A-Z]{2,}|CoT)(_([A-Z][a-z0-9]*|[A-Z]{2,}|CoT))*\.md$")
_XSD_URN = re.compile(r"<cap:id>(urn:[^<]+)</cap:id>")
_URN = re.compile(r"^urn:carla-sumo-capture:schema:[a-z][a-z0-9]*(-[a-z0-9]+)*:[1-9][0-9]*$")


def every_schema() -> dict[str, dict]:
    """Every schema generated in Python by file name: this package's and the authoring skill's."""
    schemas = {name: generate() for name, generate in PublishedSchemas.all().items()}
    schemas["scenario.schema.json"] = SCHEMA
    schemas["sweep.schema.json"] = SWEEP_SCHEMA
    return schemas


# The JSON Schemas generated in Python, which the readers' validator checks: every one but the
# Table Schema of the SUMO bridge's CSV, which TableSchemaCheck reads.
JSON_SCHEMAS = sorted(name for name in every_schema() if name.endswith(".schema.json"))


def urn_of(name: str) -> str | None:
    """The URN a published schema names itself with; None for an XSD part that has none."""
    path = SCHEMAS / name if (SCHEMAS / name).is_file() else SKILL_SCHEMAS / name
    text = path.read_text(encoding="utf-8")
    if name.endswith(".xsd"):
        found = _XSD_URN.search(text)
        return found.group(1) if found else None
    return json.loads(text)["$id"]


REGENERATE = "regenerate it with carla-validate --write-schemas CarlaControl/schemas"


def test_every_file_in_the_schema_folder_is_listed_once():
    groups = [*GENERATED.values(), CAPTURE_SCHEMAS, HAND_WRITTEN]
    listed = [name for group in groups for name in group]
    assert len(listed) == len(set(listed)), "a schema is listed in two groups"
    assert sorted(path.name for path in SCHEMAS.iterdir()) == sorted(listed) == \
        list(PublishedSchemas.files())


@pytest.mark.parametrize("name", sorted(PublishedSchemas.all()))
def test_the_checked_in_schema_is_its_generator_s_byte_for_byte(name):
    path = SCHEMAS / name
    assert path.is_file(), REGENERATE
    assert path.read_text(encoding="utf-8") == SchemaPublication.text(PublishedSchemas.all()[name]()), \
        f"{name} is not what its generator writes; {REGENERATE}"


@pytest.fixture(scope="module")
def capture_texts() -> dict[str, str]:
    return PublishedSchemas.capture_texts()


def test_the_capture_schemas_listed_are_the_ones_carlanet_generates(capture_texts):
    assert sorted(capture_texts) == sorted(CAPTURE_SCHEMAS)


@pytest.mark.parametrize("name", CAPTURE_SCHEMAS)
def test_each_capture_schema_is_what_the_carlanet_writers_generate(name, capture_texts):
    assert (SCHEMAS / name).read_text(encoding="utf-8") == capture_texts[name], \
        f"{name} is not what CarlaNet's writers generate; {REGENERATE}, or run CarlaNet's schema " \
        "tests with CARLANET_WRITE_CAPTURE_SCHEMAS=1"


def test_one_command_writes_every_generated_schema_as_it_is_checked_in(tmp_path):
    assert validate(["--write-schemas", str(tmp_path)]) == 0
    written = sorted(path.name for path in tmp_path.iterdir())
    assert written == sorted([*PublishedSchemas.all(), *CAPTURE_SCHEMAS])
    for name in written:
        text = (tmp_path / name).read_bytes()
        assert b"\r\n" not in text and text.endswith(b">\n" if name.endswith(".xsd") else b"}\n"), name
        assert text == (SCHEMAS / name).read_bytes(), name


@pytest.mark.parametrize("name", JSON_SCHEMAS)
def test_each_schema_is_2020_12_and_named_by_the_urn_of_its_format_version(name):
    schema = every_schema()[name]
    assert schema["$schema"] == DIALECT
    assert _URN.match(schema["$id"]), schema["$id"]
    assert schema["title"] and schema["description"]
    declared = {key: value["const"] for key, value in schema.get("properties", {}).items()
                if key.endswith("_version") and isinstance(value, dict) and "const" in value}
    if len(declared) > 1:
        # A file that records the format of another beside its own: a lock's spec_version.
        declared = {key: value for key, value in declared.items()
                    if key not in ("spec_version", "schema_version")}
    if declared:
        [version] = set(declared.values())
        assert SchemaIdentifier.version_of(schema["$id"]) == version, (name, declared)


def test_no_two_schemas_share_an_identifier():
    identifiers = [urn_of(name) for name in PAGES if urn_of(name)]
    assert len(identifiers) == len(set(identifiers))
    assert all(_URN.match(identifier) for identifier in identifiers), identifiers


@pytest.mark.parametrize("name", JSON_SCHEMAS)
def test_each_schema_says_no_more_than_the_readers_validator_checks(name):
    # The validator refuses to run on a keyword it does not enforce.
    ScenarioSchema.validate_against({}, every_schema()[name])


def test_every_schema_has_a_page():
    assert sorted(PAGES) == sorted([*PublishedSchemas.files(), "scenario.schema.json",
                                    "sweep.schema.json"])


@pytest.mark.parametrize("name", sorted(PAGES))
def test_each_schema_has_a_description_page_naming_it_and_its_urn(name):
    page = PAGES_DIRECTORY / PAGES[name]
    assert page.is_file(), f"describe {name} in {page}"
    text = page.read_text(encoding="utf-8")
    assert name in text
    if urn_of(name):
        assert urn_of(name) in text


def test_every_page_is_named_title_case_with_underscores():
    pages = sorted(path.name for path in PAGES_DIRECTORY.glob("*.md") if path != INDEX)
    assert pages and [name for name in pages if not PAGE_NAME.match(name)] == []


def test_the_index_lists_every_page_and_every_schema_with_its_urn():
    index = INDEX.read_text(encoding="utf-8")
    for page in PAGES_DIRECTORY.glob("*.md"):
        if page != INDEX:
            assert f"]({page.name})" in index, f"the index does not link {page.name}"
    for name in PAGES:
        assert f"`{name}`" in index, f"the index does not list {name}"
        if urn_of(name):
            assert f"`{urn_of(name)}`" in index, f"the index does not give {name}'s URN"


def test_the_scenario_resolution_schema_states_the_report_s_sections_in_order():
    assert list(ScenarioOutputSchemas.resolution()["properties"]) == list(SECTIONS)


def test_the_epoch_schema_names_the_fields_and_version_solar_epoch_reads():
    known = clr.GetClrType(SolarEpoch).GetField(
        "KnownFields", BindingFlags.NonPublic | BindingFlags.Static).GetValue(None)
    assert list(ScenarioEpoch.schema()["properties"]) == [str(name) for name in known]
    assert EPOCH_VERSION == int(SolarEpoch.SupportedVersion)


# A server's build identity as `get_build_identity` gives it.
ANSWERED = {"available": True, "release": "0.10.0", "world_interface": "1.0", "build": "package",
            "configuration": "Shipping", "carla_commit": "025443a83eaf1bb82f18795d608fca50eb77a452",
            "content_commit": "unknown", "engine_commit": "unknown", "commits_from": "version_file"}


@pytest.mark.parametrize("record", [
    ProducerRecord.record("carlacontrol.ScenarioCompiler", sumo="1.27.0"),
    ProducerRecord.record("carlacontrol.ScenarioCompiler", timed=False),
    ProducerRecord.record("carlacontrol.CaptureSession", server=ANSWERED),
    ProducerRecord.record("carlacontrol.CaptureSession",
                          server=ProducerRecord.unavailable("no call", "0.10.0", "1.0")),
])
def test_the_producer_record_conforms_to_its_schema(record):
    assert SchemaPublication.problems(record, SchemaPublication.producer()) == []


def test_a_server_identity_that_is_neither_an_answer_nor_a_reason_is_refused():
    """One definition of the record for every schema, holding the server to the two shapes it has."""
    partial = ProducerRecord.record("carlacontrol.CaptureSession",
                                    server={"available": True, "release": "0.10.0"})
    (problem,) = SchemaPublication.problems(partial, SchemaPublication.producer())
    assert problem.startswith("$.server: matches none of the accepted forms")
    assert "'commits_from' is required" in problem


def test_every_generated_schema_that_names_a_producer_gives_it_the_one_definition():
    def producers(node: object) -> list[object]:
        if isinstance(node, dict):
            found = [value for key, value in node.items() if key in ("producer", "Producer")]
            return found + [item for value in node.values() for item in producers(value)]
        if isinstance(node, list):
            return [item for value in node for item in producers(value)]
        return []

    definition = SchemaPublication.producer()
    checked = 0
    for name, schema in every_schema().items():
        for found in producers(schema.get("properties", {})) + producers(schema.get("$defs", {})):
            if isinstance(found, dict) and "anyOf" in found and "properties" not in found:
                found = next(form for form in found["anyOf"] if form.get("type") == "object")
            # A run configuration's producer is provenance its reader keeps without reading, so its
            # schema leaves it an open object.
            if "properties" not in found:
                continue
            assert {**found, "description": None} == {**definition, "description": None}, name
            checked += 1
    assert checked >= 6


def test_a_document_with_an_unknown_field_is_refused_by_the_schema_s_words():
    schema = ScenarioOutputSchemas.checks()
    problems = SchemaPublication.problems({"checks_version": 1, "outcomes": {}, "checks": [],
                                           "extra": 1}, schema)
    message = SchemaPublication.refusal("checks.json", schema, problems)
    assert "'extra' is not a field here" in message and schema["$id"] in message
