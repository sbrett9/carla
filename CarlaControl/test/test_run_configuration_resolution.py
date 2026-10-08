"""Every resolved field says which layer set it, a binding cannot be overridden, and a run replays.

Plan 12 D12.3: the recorded artifact is the effective configuration, and every field in it carries its
value, its layer, the tool default it would have had, and what any override replaced. Layering is where
implicitness creeps back in (R5), so each layer is exercised here by a field it alone supplies, and the
record of an override is read back rather than assumed. The world package's and the scenario
package's values are bindings: a run that restates one with a different value is refused naming both
(check 3), because a scenario that asserts one world and a run that renders another produce a corpus
that contradicts itself. And the effective configuration read back as a run configuration must resolve
to itself -- R1's test made structural -- while a replay against a world that has since changed is
refused rather than quietly re-bound.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))
sys.path.insert(0, str(_REPO / "CarlaControl" / "test"))

from RunCaptureFixture import (  # noqa: E402
    MANIFEST,
    SCENARIO_ID,
    Layout,
    run_document,
    write_world_package,
)

from carlacontrol.RunConfiguration import RunConfiguration  # noqa: E402
from carlacontrol.RunConfigurationFindings import RunConfigurationRefusedError  # noqa: E402
from carlacontrol.RunConfigurationResolver import RunConfigurationResolver  # noqa: E402
from carlacontrol.SiteProfile import SUMO_EXECUTABLE, SiteProfile  # noqa: E402


@pytest.fixture
def layout(tmp_path: Path) -> Layout:
    return Layout(tmp_path)


def resolve(layout: Layout, document: dict | None = None, overrides=(), profile=None):
    site = profile or SiteProfile.discover(layout.root, layout.write_profile(), environ={})
    run = RunConfiguration.from_document(document if document is not None else run_document(),
                                         "fixture.run.json")
    parsed = [(*RunConfiguration.parse_override(text), f"--set {text}") for text in overrides]
    return RunConfigurationResolver(site).resolve(run, parsed)


def refused(layout: Layout, document=None, overrides=()) -> RunConfigurationRefusedError:
    with pytest.raises(RunConfigurationRefusedError) as raised:
        resolve(layout, document, overrides)
    return raised.value


# -- every layer, by a field it alone supplies -------------------------------------------------------

def test_each_layer_is_named_on_the_field_it_supplied(layout):
    effective = resolve(layout, overrides=["occlusion.samples=48"])
    layers = {path: effective.resolution(path).layer for path in (
        "capture.prewarm_s", "paths.capture_root", "world.origin_latitude", "scenario.sumo_step_s",
        "capture.window", "occlusion.samples")}
    assert layers == {"capture.prewarm_s": "tool_default", "paths.capture_root": "site_profile",
                      "world.origin_latitude": "world_package",
                      "scenario.sumo_step_s": "scenario_package",
                      "capture.window": "run_configuration",
                      "occlusion.samples": "operator_override"}


def test_the_tool_default_is_carried_beside_a_value_that_came_from_elsewhere(layout):
    resolved = resolve(layout, overrides=["occlusion.samples=48"]).resolution("occlusion.samples")
    assert resolved.value == 48
    assert resolved.tool_default == 24
    assert resolved.to_dict()["tool_default"] == 24


def test_an_override_records_the_value_it_replaced(layout):
    document = run_document()
    document["occlusion"] = {"samples": 40}
    resolved = resolve(layout, document, ["occlusion.samples=48"]).resolution("occlusion.samples")
    assert resolved.layer == "operator_override"
    assert [(r["layer"], r["value"]) for r in resolved.overridden] == [("run_configuration", 40)]


def test_a_later_override_of_the_same_field_records_the_earlier(layout):
    resolved = resolve(layout, overrides=["occlusion.samples=48", "occlusion.samples=32"]) \
        .resolution("occlusion.samples")
    assert resolved.value == 32
    assert [r["value"] for r in resolved.overridden] == [48]


def test_the_scenario_s_illumination_default_is_layer_four(layout):
    resolved = resolve(layout).resolution("solar.policy")
    assert resolved.value == "freeze_at_window_start"
    assert resolved.layer == "scenario_package"
    assert "illumination.policy" in resolved.provenance


def test_a_scenario_package_implies_its_mode(layout):
    resolved = resolve(layout).resolution("mode")
    assert (resolved.value, resolved.layer) == ("sumo_driven_playback", "scenario_package")


def test_a_field_no_layer_supplies_resolves_as_unsupplied(layout):
    document = run_document()
    del document["capture"]["channels"]
    effective = resolve(layout, document)
    assert effective.resolution("capture.channels").layer == "unsupplied"
    assert "capture.channels" in effective.unsupplied()


# -- bindings ------------------------------------------------------------------------------------------

def test_overriding_a_world_binding_is_refused_naming_both_values(layout):
    raised = refused(layout, overrides=["world.origin_latitude=40.0"])
    assert raised.outcome == "usage_error"
    [finding] = raised.findings.refusals
    assert finding.check_id == 3
    assert "38.91108" in finding.message and "40.0" in finding.message


def test_overriding_the_epoch_is_refused(layout):
    document = run_document(scenario={"epoch": {"epoch_version": 1}})
    [finding] = refused(layout, document).findings.refusals
    assert (finding.check_id, finding.subject) == (3, "scenario.epoch")


def test_restating_a_binding_with_its_own_value_is_accepted_and_noted(layout):
    effective = resolve(layout, overrides=["world.origin_latitude=38.91108"])
    resolved = effective.resolution("world.origin_latitude")
    assert resolved.layer == "world_package"
    assert "restated, equal" in resolved.note


def test_the_sun_s_rate_is_not_operator_settable(layout):
    raised = refused(layout, overrides=["solar.policy=advance",
                                        "solar.rate_sun_s_per_sim_s=2.0"])
    [finding] = raised.findings.refusals
    assert finding.check_id == 3 and "pinned to one sun-second per simulated second" in finding.message


# -- dependent fields ----------------------------------------------------------------------------------

def test_advance_pins_the_rate_and_drops_the_freeze_field_with_a_record(layout):
    effective = resolve(layout, overrides=["solar.policy=advance"])
    rate = effective.resolution("solar.rate_sun_s_per_sim_s")
    assert (rate.value, rate.layer) == (1.0, "pinned_by_policy")
    dropped = effective.resolution("solar.freeze_date_advances")
    assert dropped.value is None and dropped.layer == "not_applicable"
    assert dropped.dropped[0]["value"] is False
    assert effective.illumination_object() == {"illumination_version": 1, "policy": "advance",
                                               "rate_sun_s_per_sim_s": 1.0,
                                               "note": "one lighting condition per window"}


def test_a_policy_field_given_beside_the_policy_is_kept_for_the_policy_to_refuse(layout):
    effective = resolve(layout, overrides=["solar.policy=advance",
                                           "solar.freeze_date_advances=true"])
    assert effective.value("solar.freeze_date_advances") is True


def test_the_pacing_factors_are_not_applicable_as_available(layout):
    effective = resolve(layout)
    for path in ("pacing.real_time_factor", "pacing.min_achieved_factor"):
        assert effective.resolution(path).layer == "not_applicable"
        assert effective.value(path) is None


def test_under_wall_clock_the_factor_defaults_and_the_floor_is_unsupplied(layout):
    effective = resolve(layout, overrides=["pacing.mode=wall_clock"])
    assert effective.resolution("pacing.real_time_factor").value == 1.0
    assert effective.resolution("pacing.min_achieved_factor").layer == "unsupplied"


# -- channels ------------------------------------------------------------------------------------------

def test_a_channel_field_carries_its_own_provenance(layout):
    effective = resolve(layout, overrides=["capture.channels[0].fov=60"])
    channel = effective.channel_resolutions(0)
    assert (channel["fov"].value, channel["fov"].layer) == (60, "operator_override")
    assert channel["fov"].tool_default == 90.0
    assert channel["sensor_id"].layer == "run_configuration"
    assert (channel["width"].value, channel["width"].layer) == (1280, "tool_default")
    assert channel["post_process_profile"].value == "Default"


def test_an_override_of_a_channel_that_does_not_exist_is_refused(layout):
    [finding] = refused(layout, overrides=["capture.channels[3].fov=60"]).findings.refusals
    assert finding.check_id == 1 and "no channel 3" in finding.message


# -- the packages ----------------------------------------------------------------------------------------

def test_the_world_package_is_the_one_the_lock_names_under_the_root(layout):
    resolved = resolve(layout).resolution("world_package")
    assert Path(resolved.value) == layout.world_package.resolve()
    assert resolved.layer == "derived"


def test_a_scenario_found_nowhere_is_refused_naming_where_it_looked(layout):
    raised = refused(layout, run_document(scenario_package="no_such_scenario"))
    [finding] = raised.findings.refusals
    assert finding.check_id == 49
    assert "no_such_scenario.lock.json" in finding.message


def test_no_scenario_at_all_is_check_2(layout):
    document = run_document()
    del document["scenario_package"]
    raised = refused(layout, document)
    assert raised.outcome == "refused_offline"
    assert raised.findings.refusals[0].check_id == 2


def test_a_route_file_changed_after_the_compile_is_refused(layout):
    routes = layout.lock.parent / f"{SCENARIO_ID}.rou.xml"
    routes.write_text(routes.read_text(encoding="utf-8").replace("25300", "25400"),
                      encoding="utf-8")
    [finding] = refused(layout).findings.refusals
    assert finding.check_id == 49
    assert "changed after the compile" in finding.message


def test_a_scenario_can_be_named_by_its_lock_path(layout):
    effective = resolve(layout, run_document(scenario_package=str(layout.lock)))
    assert effective.scenario.lock_path == layout.lock.resolve()


# -- replay ----------------------------------------------------------------------------------------------

def test_the_effective_configuration_replays_to_itself(layout):
    first = resolve(layout, overrides=["solar.policy=advance", "occlusion.samples=48"])
    replayed = resolve(layout, first.to_run_configuration())
    assert replayed.digest == first.digest
    for path in RunConfiguration.FIELDS:
        assert replayed.value(path) == first.value(path), path


def test_the_replayable_document_names_no_machine_fact(layout):
    document = resolve(layout).to_run_configuration()
    assert "server" not in document and "paths" not in document
    assert "home" not in document.get("sumo", {})
    assert document["scenario_package"] == SCENARIO_ID


def test_a_replay_against_a_rebuilt_world_is_refused(layout):
    document = resolve(layout).to_run_configuration()
    rebuilt = dict(MANIFEST, OriginLatitude=38.92)
    write_world_package(layout.world_root, rebuilt)
    [finding] = refused(layout, document).findings.refusals
    assert finding.check_id == 3 and finding.subject == "world.origin_latitude"


def test_the_digest_moves_when_a_science_field_moves(layout):
    assert resolve(layout).digest != resolve(layout, overrides=["occlusion.samples=48"]).digest


def test_the_digest_does_not_depend_on_the_machine(layout, tmp_path):
    here = resolve(layout)
    elsewhere_profile = layout.profile_document(server={"host": "10.0.0.5", "port": 3000})
    path = tmp_path / "elsewhere.json"
    path.write_text(json.dumps(elsewhere_profile), encoding="utf-8")
    there = resolve(layout, profile=SiteProfile.discover(layout.root, path, environ={}))
    assert there.value("server.port") == 3000
    assert there.digest == here.digest


# -- the site profile ------------------------------------------------------------------------------------

def test_a_value_from_the_environment_names_its_variable(tmp_path):
    sumo = tmp_path / "sumo"
    (sumo / "bin").mkdir(parents=True)
    (sumo / "bin" / SUMO_EXECUTABLE).write_text("")
    profile = SiteProfile.discover(tmp_path, None, environ={"CARLANET_SUMO_HOME": str(sumo)})
    value = profile.get("sumo.home")
    assert value.environment_variable == "CARLANET_SUMO_HOME"
    assert profile.undeclared_environment() == [("sumo.home", "CARLANET_SUMO_HOME")]


def test_a_profile_naming_no_sumo_records_that_the_host_decides(tmp_path):
    profile = SiteProfile.discover(tmp_path, None, environ={})
    assert profile.value("sumo.home") is None
    assert ("sumo.home", "SUMO_HOME") in profile.undeclared_environment()


def test_a_profile_file_resolves_relative_paths_against_itself(tmp_path):
    path = tmp_path / "profiles" / "site.json"
    path.parent.mkdir()
    path.write_text(json.dumps({"site_profile_version": 1, "paths": {"capture_root": "../cap"},
                                "environment": ["CARLANET_SUMO_HOME"]}), encoding="utf-8")
    profile = SiteProfile.discover(tmp_path, path, environ={})
    assert Path(profile.value("paths.capture_root")) == (tmp_path / "cap").resolve()
    assert profile.declared_environment == ("CARLANET_SUMO_HOME",)


def test_a_profile_file_with_an_unknown_field_is_refused(tmp_path):
    path = tmp_path / "site.json"
    path.write_text(json.dumps({"site_profile_version": 1, "paths": {"capture_rot": "x"}}),
                    encoding="utf-8")
    with pytest.raises(ValueError, match="capture_rot"):
        SiteProfile.discover(tmp_path, path, environ={})
