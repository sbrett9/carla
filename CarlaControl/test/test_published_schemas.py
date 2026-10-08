"""Every schema we publish is its generator's output, a JSON Schema 2020-12 document named by its URN,
checkable by the validator our readers use, and described on a page of its own.

A schema is generated from the code that writes or reads its file and never edited by hand, so a
checked-in copy that differs from its generator is a schema describing something the code no longer
does. Its `$id` names the file kind and the format version the file declares, so a reader holding a
file can tell which schema is its. Each schema uses only the keywords the readers' validator
(`ScenarioSchema.validate_against`) enforces, so a published schema says no more than a reader checks.
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

from carlacontrol.ProducerRecord import ProducerRecord  # noqa: E402
from carlacontrol.PublishedSchemas import (  # noqa: E402
    RUN_SCHEMAS,
    SCENARIO_SCHEMAS,
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
# Each schema's description page.
PAGES = {
    "run_configuration.schema.json": "RunConfiguration.md",
    "run_result.schema.json": "RunResult.md",
    "run_resolution.schema.json": "RunResolutionReport.md",
    "run_lock.schema.json": "RunLock.md",
    "launch_echo.schema.json": "LaunchEcho.md",
    "site_profile.schema.json": "SiteProfile.md",
    "scenario.schema.json": "ScenarioSpecification.md",
    "sweep.schema.json": "Sweep.md",
    "scenario_lock.schema.json": "ScenarioLock.md",
    "scenario_resolution.schema.json": "ScenarioResolutionReport.md",
    "supervision_plan.schema.json": "SupervisionPlan.md",
    "sweep_index.schema.json": "SweepIndex.md",
    "scenario_checks.schema.json": "ScenarioChecks.md",
    "epoch.schema.json": "Epoch.md",
    "display_convention.schema.json": "DisplayConvention.md",
    "area_of_interest.schema.json": "AreasOfInterest.md",
}
_URN = re.compile(r"^urn:carla-sumo-capture:schema:[a-z][a-z0-9]*(-[a-z0-9]+)*:[1-9][0-9]*$")


def every_schema() -> dict[str, dict]:
    """Every schema we publish by file name: this package's and the authoring skill's."""
    schemas = {name: generate() for name, generate in PublishedSchemas.all().items()}
    schemas["scenario.schema.json"] = SCHEMA
    schemas["sweep.schema.json"] = SWEEP_SCHEMA
    return schemas


@pytest.mark.parametrize("name", sorted(PublishedSchemas.all()))
def test_the_checked_in_schema_is_its_generator_s(name):
    path = SCHEMAS / name
    tool = "carla-capture" if name in RUN_SCHEMAS else "carla-compile-scenario"
    assert path.is_file(), f"publish it with {tool} --write-schemas CarlaControl/schemas"
    assert json.loads(path.read_text(encoding="utf-8")) == PublishedSchemas.all()[name](), \
        f"{name} is not what its generator writes; regenerate it with {tool} --write-schemas"


def test_the_two_tools_publish_different_schemas():
    assert not set(RUN_SCHEMAS) & set(SCENARIO_SCHEMAS)


def test_the_written_schemas_are_the_generated_ones(tmp_path):
    written = PublishedSchemas.write(tmp_path, PublishedSchemas.all())
    assert sorted(path.name for path in written) == sorted(PublishedSchemas.all())
    for path in written:
        text = path.read_bytes()
        assert text.endswith(b"}\n") and b"\r\n" not in text
        assert json.loads(text) == PublishedSchemas.all()[path.name]()


@pytest.mark.parametrize("name", sorted(PAGES))
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
    identifiers = [schema["$id"] for schema in every_schema().values()]
    assert len(identifiers) == len(set(identifiers))


@pytest.mark.parametrize("name", sorted(PAGES))
def test_each_schema_says_no_more_than_the_readers_validator_checks(name):
    # The validator refuses to run on a keyword it does not enforce.
    ScenarioSchema.validate_against({}, every_schema()[name])


@pytest.mark.parametrize("name", sorted(PAGES))
def test_each_schema_has_a_description_page_naming_it(name):
    page = PAGES_DIRECTORY / PAGES[name]
    assert page.is_file(), f"describe {name} in {page}"
    text = page.read_text(encoding="utf-8")
    assert name in text and every_schema()[name]["$id"] in text


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
