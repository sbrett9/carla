"""The run configuration's schema refuses what it does not name, and is the one source of its help.

An assistant generating a configuration makes mistakes that `argparse` would accept in silence: a
misspelt key, a number where a name belongs, a field that does not exist on this build. Plan 12 R4
requires each to be a refusal naming the field and the candidates, and some to be refusals that point
at the field that does exist -- the camera blueprint's own name for part of the exposure, which a
channel sets with a field in the run's units, and a world build (check 38) -- because a generic
"unknown key" would leave the operator no better off.

A channel's exposure is five fields, each defaulting to the Default profile's value: the run states
every capture's exposure whether or not it sets one (08 D8.26-D8.27).

The field table is also what `--help` prints and what the published schema says, so a default exists
once; the tests hold the published file equal to the generated one and every default equal to the
channel description's.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.ChannelDescription import ChannelDescription  # noqa: E402
from carlacontrol.ChannelExposure import ChannelExposure  # noqa: E402
from carlacontrol.RunConfiguration import (  # noqa: E402
    NO_DEFAULT,
    RunConfiguration,
)
from carlacontrol.RunConfigurationFindings import RunConfigurationRefusedError  # noqa: E402

PUBLISHED_SCHEMA = _REPO / "CarlaControl" / "schemas" / "run_configuration.schema.json"


def refusal(document: dict) -> RunConfigurationRefusedError:
    with pytest.raises(RunConfigurationRefusedError) as raised:
        RunConfiguration.from_document(document, "test.run.json")
    return raised.value


def test_a_document_of_known_fields_flattens_to_their_paths():
    document = RunConfiguration.from_document(
        {"capture": {"window": "morning", "prewarm_s": 600}, "pacing": {"mode": "wall_clock"}},
        "test.run.json")
    assert document.values == {"capture.window": "morning", "capture.prewarm_s": 600,
                               "pacing.mode": "wall_clock"}


def test_an_unknown_key_is_refused_with_the_nearest_names():
    raised = refusal({"capture": {"prewarm_ss": 600}})
    assert raised.outcome == "usage_error"
    [finding] = raised.findings.refusals
    assert finding.check_id == 1
    assert finding.subject == "capture.prewarm_ss"
    assert "did you mean 'capture.prewarm_s'" in finding.message


def test_every_problem_is_named_together():
    raised = refusal({"capture": {"prewarm_ss": 1, "window": 7}, "solar": {"policy": "noon"}})
    subjects = {finding.subject for finding in raised.findings.refusals}
    assert subjects == {"capture.prewarm_ss", "capture.window", "solar.policy"}


def test_a_value_of_the_wrong_type_is_refused_naming_the_field():
    raised = refusal({"occlusion": {"samples": "lots"}})
    [finding] = raised.findings.refusals
    assert finding.check_id == 1 and finding.subject == "occlusion.samples"
    assert "integer" in finding.message


def test_a_channel_s_exposure_is_five_fields_defaulting_to_the_default_profile_s():
    fields = RunConfiguration.channel_fields()
    assert {name: fields[name].default for name in ChannelExposure.FIELDS} == {
        "exposure_method": "manual", "exposure_iso": 100.0, "exposure_shutter_s": 0.003125,
        "exposure_fstop": 4.0, "exposure_compensation_ev": 0.0}
    assert fields["post_process_profile"].default == "Default"
    document = RunConfiguration.from_document(
        {"capture": {"channels": [{"stare_look_at_x_m": 0.0, "stare_look_at_y_m": 0.0,
                                   "exposure_method": "histogram", "exposure_iso": 400,
                                   "exposure_shutter_s": 0.002, "exposure_fstop": 5.6,
                                   "exposure_compensation_ev": -1.0}]}}, "test.run.json")
    assert document.values["capture.channels"][0]["exposure_iso"] == 400


def test_an_exposure_method_that_is_not_one_is_refused_naming_the_two():
    raised = refusal({"capture": {"channels": [{"exposure_method": "auto"}]}})
    [finding] = raised.findings.refusals
    assert finding.check_id == 1 and finding.subject == "capture.channels[0].exposure_method"
    assert "manual" in finding.message and "histogram" in finding.message


@pytest.mark.parametrize(("attribute", "field"), [
    ("iso", "exposure_iso"), ("shutter_speed", "exposure_shutter_s"), ("fstop", "exposure_fstop"),
    ("exposure_compensation", "exposure_compensation_ev"), ("exposure_mode", "exposure_method")])
def test_the_blueprint_s_name_for_part_of_the_exposure_is_refused_naming_the_field(attribute, field):
    raised = refusal({"capture": {"channels": [{"stare_look_at_x_m": 0.0,
                                                "stare_look_at_y_m": 0.0, attribute: 1.5}]}})
    [finding] = raised.findings.refusals
    assert finding.check_id == 1
    assert finding.subject == f"capture.channels[0].{attribute}"
    assert f"'{field}'" in finding.message
    assert ("in seconds" in finding.message) == (field == "exposure_shutter_s")


def test_an_override_names_the_exposure_by_its_field():
    assert RunConfiguration.parse_override("capture.channels[0].exposure_shutter_s=0.004") == (
        "capture.channels[0].exposure_shutter_s", 0.004)
    with pytest.raises(RunConfigurationRefusedError) as raised:
        RunConfiguration.parse_override("capture.channels[0].shutter_speed=250")
    assert "'exposure_shutter_s'" in raised.value.findings.refusals[0].message


def test_a_world_build_block_is_refused_as_a_world_build():
    raised = refusal({"world_build": {"osm": "extract.osm"}})
    [finding] = raised.findings.refusals
    assert finding.check_id == 38
    assert "run_SCTMV.py --build" in finding.message


def test_a_misspelt_channel_field_is_refused_with_its_candidate():
    raised = refusal({"capture": {"channels": [{"fovv": 60}]}})
    [finding] = raised.findings.refusals
    assert finding.subject == "capture.channels[0].fovv"
    assert "did you mean 'fov'" in finding.message


def test_a_group_given_a_scalar_is_refused():
    raised = refusal({"pacing": "wall_clock"})
    assert raised.findings.refusals[0].subject == "pacing"


@pytest.mark.parametrize(("text", "expected"), [
    ("occlusion.samples=32", ("occlusion.samples", 32)),
    ("capture.window=night", ("capture.window", "night")),
    ("capture.window=3600:5400", ("capture.window", "3600:5400")),
    ('caller_label="123"', ("caller_label", "123")),
    ("caller_label=123", ("caller_label", "123")),
    ("capture.road_layer_visible=true", ("capture.road_layer_visible", True)),
    ("capture.channels[1].fov=60", ("capture.channels[1].fov", 60)),
    ('on_warning={"prewarm_clipped": "proceed"}', ("on_warning", {"prewarm_clipped": "proceed"})),
])
def test_an_override_is_parsed_for_its_field(text, expected):
    assert RunConfiguration.parse_override(text) == expected


@pytest.mark.parametrize("text", ["occlusion.samples=lots", "capture.renders=3", "no-equals",
                                  "capture.channels[0].fovv=3"])
def test_a_bad_override_is_refused(text):
    with pytest.raises(RunConfigurationRefusedError) as raised:
        RunConfiguration.parse_override(text)
    assert raised.value.outcome == "usage_error"
    assert raised.value.findings.refusals[0].check_id == 1


def test_channel_defaults_are_the_channel_description_s():
    for name, spec in RunConfiguration.channel_fields().items():
        if name in ChannelDescription.field_names():
            assert spec.default == ChannelDescription.default_of(name), name


def test_the_help_names_every_field_and_its_default():
    text = RunConfiguration.help_text()
    for path, spec in RunConfiguration.FIELDS.items():
        assert f"    {path}" in text, path
        if spec.has_default:
            assert f"default {json.dumps(spec.default)}" in text
    for name in RunConfiguration.channel_fields():
        assert f"    {name}" in text


def test_the_schema_states_every_field_with_its_default():
    schema = RunConfiguration.schema()
    for path, spec in RunConfiguration.FIELDS.items():
        node = schema
        for part in path.split("."):
            node = node["properties"][part]
        assert node["x-mutability"] == spec.mutability
        assert ("default" in node) == (spec.default is not NO_DEFAULT)
    channel = schema["properties"]["capture"]["properties"]["channels"]["items"]
    assert set(channel["properties"]) == set(RunConfiguration.channel_fields())


def test_the_published_schema_is_the_generated_one():
    assert PUBLISHED_SCHEMA.is_file(), "publish it with run_capture.py --write-schema"
    assert json.loads(PUBLISHED_SCHEMA.read_text(encoding="utf-8")) == RunConfiguration.schema()
