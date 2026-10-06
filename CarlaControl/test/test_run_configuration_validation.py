"""A configuration that cannot work is refused at launch, naming why, not at minute forty.

Plan 12 §6. Each test here is one thing wrong with an otherwise launchable configuration, and asserts
the check that refuses it and a phrase an operator can act on. Where the session has a validator of
its own for a rule -- the clock ratio, the illumination object, the epoch -- the offline checks must reach the
same verdict offline, so those tests pass values the session itself refuses. The unattended caller's
two mechanisms (checks 34 and 35) are exercised on both sides: a warning with no adjudication is
refused, one adjudicated in writing proceeds, and an expectation that disagrees is refused naming
both values.

The server checks run against a stand-in world: it is handed a blueprint library and a sun, and nothing may be
spawned or written.
"""
from __future__ import annotations

import json
import sys
from collections import namedtuple
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))
sys.path.insert(0, str(_REPO / "CarlaControl" / "test"))

from RunCaptureFixture import (  # noqa: E402
    AN_ORBIT,
    CATALOGUE,
    Layout,
    run_document,
    write_scenario_package,
)

from carlacontrol.LaunchEcho import LaunchEcho  # noqa: E402
from carlacontrol.RunConfiguration import RunConfiguration  # noqa: E402
from carlacontrol.RunConfigurationCheckCatalogue import RunConfigurationCheckCatalogue  # noqa: E402
from carlacontrol.RunConfigurationFindings import (  # noqa: E402
    RunConfigurationFindings,
    RunConfigurationRefusedError,
)
from carlacontrol.RunConfigurationResolver import RunConfigurationResolver  # noqa: E402
from carlacontrol.RunConfigurationValidator import (  # noqa: E402
    PNG_BYTES_PER_PIXEL,
    RunConfigurationValidator,
)
from carlacontrol.SiteProfile import SiteProfile  # noqa: E402

Usage = namedtuple("Usage", "total used free")
PLENTY = 10**13


@pytest.fixture
def layout(tmp_path: Path) -> Layout:
    return Layout(tmp_path)


def resolve(layout: Layout, document=None, overrides=(), profile_path=None, environ=None):
    profile = SiteProfile.discover(layout.root, profile_path or layout.write_profile(),
                                   environ=environ or {})
    run = RunConfiguration.from_document(document if document is not None else run_document(),
                                         "fixture.run.json")
    parsed = [(*RunConfiguration.parse_override(text), f"--set {text}") for text in overrides]
    return RunConfigurationResolver(profile).resolve(run, parsed)


def offline(layout: Layout, document=None, overrides=(), free=PLENTY, result=None, **resolve_kw):
    effective = resolve(layout, document, overrides, **resolve_kw)
    validator = RunConfigurationValidator(disk_usage=lambda _path: Usage(free, 0, free))
    result_path = result or layout.runs_root / "run.result.json"
    capture = layout.capture_root / "cap-test"
    findings = validator.validate_offline(effective, result_path, capture)
    return effective, validator, findings, result_path, capture


def launch(layout: Layout, document=None, overrides=(), free=PLENTY, **resolve_kw):
    """The offline checks in full: the configuration, the echo, then checks 35 and 34."""
    effective, validator, findings, result_path, capture = offline(layout, document, overrides,
                                                                   free, **resolve_kw)
    if not findings.refused:
        free_bytes, headroom = validator.headroom(effective, capture)
        codes = [RunConfigurationFindings.warning_code(f) for f in findings.warnings]
        echo = LaunchEcho.compute(effective, "cap-test", capture, result_path, free_bytes,
                                  headroom, validator.bytes_per_captured_second(effective), codes)
        validator.validate_launch(effective, echo.values(), findings)
    return findings


def checks(findings, outcome="refuse") -> set[int]:
    return {f.check_id for f in findings.findings if f.outcome == outcome}


def only(findings, check_id: int):
    matching = findings.by_check(check_id)
    assert len(matching) == 1, [str(f) for f in findings.findings]
    return matching[0]


def test_the_fixture_configuration_launches_clean(layout):
    findings = launch(layout)
    assert findings.findings == []


# -- the window --------------------------------------------------------------------------------------

@pytest.mark.parametrize(("window", "begin", "end"), [
    ("morning", 25200.0, 27000.0), ("3600:5400", 3600.0, 5400.0), ("80000:", 80000.0, 86400.0)])
def test_a_window_resolves_by_name_pair_or_open_end(layout, window, begin, end):
    effective, *_ = offline(layout, overrides=[f"capture.window={window}"])
    assert (effective.window.begin_s, effective.window.end_s) == (begin, end)


def test_an_undeclared_window_is_refused_listing_the_declared_ones(layout):
    *_, findings, _, _ = offline(layout, overrides=["capture.window=dusk"])
    finding = only(findings, 7)
    assert "morning" in finding.message and "night" in finding.message


def test_a_window_past_the_scenario_s_end_is_refused(layout):
    *_, findings, _, _ = offline(layout, overrides=["capture.window=86000:90000"])
    assert "end time 86400" in only(findings, 7).message


def test_no_window_at_all_is_check_2_naming_the_declared_windows(layout):
    document = run_document()
    del document["capture"]["window"]
    *_, findings, _, _ = offline(layout, document)
    finding = only(findings, 2)
    assert finding.subject == "capture.window" and "morning, night" in finding.message


def test_a_prewarm_longer_than_the_window_s_start_warns(layout):
    *_, findings, _, _ = offline(layout, overrides=["capture.window=100:400"])
    finding = only(findings, 8)
    assert finding.outcome == "warn" and "clipped to 100" in finding.message


def test_a_prewarm_too_short_for_the_picture_ceiling_is_refused_offline(layout):
    # The ceiling's 60 frames at one every ten ticks and the ten-tick span are 610 ticks after the
    # tiles are first asked about, one step into the prewarm: 31.5 s. A shorter ceiling needs less.
    *_, findings, _, _ = offline(layout, overrides=["capture.prewarm_s=31"])
    finding = only(findings, 51)
    assert finding.subject == "capture.prewarm_s"
    assert "60 frames at one every 10 ticks and the 10-tick span" in finding.message
    assert "at least 31.5 s" in finding.message
    assert not offline(layout, overrides=["capture.prewarm_s=32"])[2].findings
    assert not offline(layout, overrides=["capture.prewarm_s=3",
                                          "capture.picture_ceiling_frames=2"])[2].findings


@pytest.mark.parametrize(("hz", "ceiling", "first_judged"), [("2", 1, 2), ("20", 10, 11)])
def test_a_picture_ceiling_too_small_to_hold_a_comparison_is_refused_offline(layout, hz, ceiling,
                                                                            first_judged):
    # A frame is judged against the camera's frame at least ten ticks before it, also rendered since
    # the tiles: the second frame at 2 Hz, the eleventh at 20 Hz.
    *_, findings, _, _ = offline(layout, overrides=[f"capture.capture_hz={hz}",
                                                    f"capture.picture_ceiling_frames={ceiling}"])
    finding = only(findings, 51)
    assert finding.subject == "capture.picture_ceiling_frames"
    assert f"at least {first_judged} frames" in finding.message
    assert not offline(layout, overrides=[f"capture.capture_hz={hz}",
                                          f"capture.picture_ceiling_frames={first_judged}"]
                       )[2].refused


@pytest.mark.parametrize(("field", "value"), [
    ("picture_ceiling_frames", 0), ("picture_ceiling_frames", 1.5),
    ("picture_ceiling_frames", "many"), ("picture_tolerance_levels", 0),
    ("picture_tolerance_levels", -0.5)])
def test_an_unusable_picture_ceiling_or_limit_is_refused_by_the_schema(field, value):
    with pytest.raises(RunConfigurationRefusedError) as raised:
        RunConfiguration.from_document({"capture": {field: value}}, "test.run.json")
    [finding] = raised.value.findings.refusals
    assert (finding.check_id, finding.subject) == (1, f"capture.{field}")


# -- the clock, the sun, the epoch ---------------------------------------------------------------

def test_a_world_delta_that_does_not_divide_the_step_is_refused(layout):
    *_, findings, _, _ = offline(layout, overrides=["capture.world_delta_s=0.03"])
    assert "not a whole number" in only(findings, 9).message


def test_a_capture_rate_that_is_not_whole_ticks_is_refused(layout):
    *_, findings, _, _ = offline(layout, overrides=["capture.capture_hz=3"])
    assert 9 in checks(findings)


def test_an_advancing_sun_at_another_rate_is_refused(layout, tmp_path):
    illumination = {"illumination_version": 1, "policy": "advance", "rate_sun_s_per_sim_s": 60.0}
    write_scenario_package(layout.scenario_root, illumination=illumination)
    *_, findings, _, _ = offline(layout)
    assert "D12.8" in only(findings, 15).message


def test_an_illumination_field_beside_the_wrong_policy_is_refused(layout):
    *_, findings, _, _ = offline(layout, overrides=["solar.policy=advance",
                                                    "solar.freeze_date_advances=true"])
    assert 15 in checks(findings)


def test_the_ignore_policy_warns(layout):
    *_, findings, _, _ = offline(layout, overrides=["solar.policy=ignore"])
    finding = only(findings, 15)
    assert finding.outcome == "warn" and "honours no epoch" in finding.message


def test_a_scenario_with_no_epoch_is_refused(layout):
    write_scenario_package(layout.scenario_root, epoch=None)
    *_, findings, _, _ = offline(layout)
    assert 12 in checks(findings)


def test_an_epoch_the_session_would_refuse_is_refused_offline(layout):
    epoch = {"epoch_version": 1, "civil_datetime": "2026-03-21T00:00:00-07:00",
             "utc_offset_hours": -7.1, "utc_datetime": "2026-03-21T07:06:00Z",
             "calendar_advances": True, "dst_in_effect": True}
    write_scenario_package(layout.scenario_root, epoch=epoch)
    *_, findings, _, _ = offline(layout)
    assert 13 in checks(findings)


# -- channels -----------------------------------------------------------------------------------------

def test_a_stare_with_nowhere_to_look_is_refused(layout):
    document = run_document()
    document["capture"]["channels"] = [{"sensor_id": "OVERWATCH-1"}]
    *_, findings, _, _ = offline(layout, document)
    assert "somewhere to look" in only(findings, 47).message


def test_an_orbit_is_accepted(layout):
    # Occlusion is measured on an orbit as on a stare: the depth camera is attached to the channel's
    # camera and moves with it, so check 47 has nothing to refuse about it.
    document = run_document()
    document["capture"]["channels"] = [AN_ORBIT]
    *_, findings, _, _ = offline(layout, document)
    assert findings.findings == []
    # And its other refusals stand: an orbit still needs its centre.
    centreless = {key: value for key, value in AN_ORBIT.items() if key != "orbit_centre_x_m"}
    document["capture"]["channels"] = [centreless]
    *_, findings, _, _ = offline(layout, document)
    assert "orbit_centre_x_m" in only(findings, 47).message


def test_two_channels_must_name_distinct_sensors(layout):
    document = run_document()
    first = dict(document["capture"]["channels"][0])
    document["capture"]["channels"] = [first, dict(first)]
    *_, findings, _, _ = offline(layout, document)
    assert "both name" in only(findings, 11).message
    document["capture"]["channels"][1].pop("sensor_id")
    *_, findings, _, _ = offline(layout, document)
    assert "no sensor_id" in only(findings, 11).message


@pytest.mark.parametrize("name", ["Overwatch 1", "deck.2", "DECK:I25", ""])
def test_a_sensor_id_with_characters_a_camera_name_cannot_hold_is_refused_as_it_is_read(name):
    # The schema states a camera name's characters, and the refusal says them, with an example.
    document = run_document()
    document["capture"]["channels"] = [dict(document["capture"]["channels"][0], sensor_id=name)]
    with pytest.raises(RunConfigurationRefusedError) as raised:
        RunConfiguration.from_document(document, "test.run.json")
    [finding] = raised.value.findings.refusals
    assert (finding.check_id, finding.subject) == (1, "capture.channels[0].sensor_id")
    assert "each an ASCII letter, digit, underscore or hyphen, such as Overwatch_1" in \
        finding.message


@pytest.mark.parametrize(("name", "reason"), [("CON", "a name Windows keeps for a device"),
                                              ("front", "a role name the server gives sensors"),
                                              ("CARLA-SENSOR-7", "another camera's name")])
def test_a_sensor_id_the_camera_name_rule_refuses_is_check_11_s_and_said_once(layout, name, reason):
    document = run_document()
    document["capture"]["channels"] = [dict(document["capture"]["channels"][0], sensor_id=name)]
    *_, findings, _, _ = offline(layout, document)
    assert reason in only(findings, 11).message
    # The channel is otherwise sound, so check 47 has nothing to repeat.
    assert 47 not in checks(findings)


def test_two_sensor_names_that_differ_only_in_case_are_one_name(layout):
    # A sensor_id names its channel's directory and begins every still's file name, and a Windows
    # file system holds the two as one.
    document = run_document()
    first = dict(document["capture"]["channels"][0], sensor_id="Deck-1")
    document["capture"]["channels"] = [first, dict(first, sensor_id="DECK-1")]
    *_, findings, _, _ = offline(layout, document)
    assert ("both name sensor_ids 'Deck-1' and 'DECK-1', which differ only in case"
            in only(findings, 11).message)


# -- render, pacing, mode ------------------------------------------------------------------------

def test_by_default_no_field_limits_the_rendered_population_and_the_old_checks_stay_retired(layout):
    # The session renders every vehicle SUMO has unless an optional limit is chosen: by default the
    # render set is every vehicle, with no region and no cap, and the checks that once compared a
    # mandatory region and cap (20, 21 and 33) stay retired -- the optional limits are checked under a
    # number of their own (53).
    effective, *_ = offline(layout)
    assert effective.value("capture.render_set") == "all"
    assert (effective.value("capture.render_region"), effective.value("capture.render_cap")) == \
        (None, None)
    assert launch(layout).findings == []
    for retired in (20, 21, 33):
        with pytest.raises(KeyError, match="retired"):
            RunConfigurationCheckCatalogue.get(retired)
    assert RunConfigurationCheckCatalogue.get(53).status == "run_capture"


REGION = 'capture.render_region={"x_m": 120, "y_m": -340, "radius_m": 300}'


def test_a_circle_with_no_region_is_refused(layout):
    *_, findings, _, _ = offline(layout, overrides=["capture.render_set=circle"])
    finding = only(findings, 53)
    assert finding.subject == "capture.render_region"
    assert "leave capture.render_set at all to draw every vehicle SUMO has" in finding.message


def test_a_region_given_to_every_vehicle_is_refused_rather_than_dropped(layout):
    *_, findings, _, _ = offline(layout, overrides=[REGION])
    assert "reads no region" in only(findings, 53).message


@pytest.mark.parametrize("overrides", [
    ["capture.render_set=circle", REGION],
    ["capture.render_set=cameras"],
    ["capture.render_set=cameras", REGION, "capture.render_cap=40"],
    ["capture.render_cap=1"],
])
def test_a_usable_optional_limit_launches_clean(layout, overrides):
    assert launch(layout, overrides=overrides).findings == []


@pytest.mark.parametrize("document", [
    {"capture": {"render_cap": 0}},
    {"capture": {"render_cap": 2.5}},
    {"capture": {"render_set": "nearest"}},
    {"capture": {"render_region": {"x_m": 0, "y_m": 0}}},
    {"capture": {"render_region": {"x_m": 0, "y_m": 0, "radius_m": 0}}},
    {"capture": {"render_hysteresis_m": 0}},
    {"capture": {"render_min_pixels": 0}},
    {"capture": {"render_admit_lead_s": -1}},
])
def test_an_unusable_render_set_value_is_refused_by_the_schema(document):
    with pytest.raises(RunConfigurationRefusedError) as raised:
        RunConfiguration.from_document(document, "test.run.json")
    assert {finding.check_id for finding in raised.value.findings.refusals} == {1}


def test_the_echo_says_plainly_that_vehicles_outside_a_limit_are_not_in_carla(layout):
    effective, validator, findings, result_path, capture = offline(
        layout, overrides=["capture.render_set=cameras", "capture.render_cap=40"])
    assert not findings.refused
    free, headroom = validator.headroom(effective, capture)
    echo = LaunchEcho.compute(effective, "cap-test", capture, result_path, free, headroom,
                              validator.bytes_per_captured_second(effective), [])
    render = echo.to_dict()["render"]
    assert (render["set"], render["cap"], render["limited"]) == ("cameras", 40, True)
    assert render["vehicles"].startswith("the vehicles in or approaching a channel camera's view")
    assert render["vehicles"].endswith("every vehicle until the cameras are placed, at most 40 at once")
    assert "is not in CARLA -- no body, no frame, no truth record" in echo.render()

    effective, validator, _findings, result_path, capture = offline(layout)
    unlimited = LaunchEcho.compute(effective, "cap-test", capture, result_path, free, headroom,
                                   validator.bytes_per_captured_second(effective), []).to_dict()
    assert (unlimited["render"]["vehicles"], unlimited["render"]["limited"],
            unlimited["render"]["left_out"]) == ("every vehicle SUMO has", False, None)


def test_no_draw_distance_is_set_unless_asked_for(layout):
    # An optional performance control, off by default: every body drawn at any range.
    effective, _validator, findings, _, _ = offline(layout)
    assert effective.value("capture.draw_distance_m") is None
    assert 52 not in checks(findings)


def test_a_draw_distance_that_reaches_every_channel_s_point_launches_clean(layout):
    # The fixture's stare stands 400 m back and 300 m up: 500 m from the point it looks at.
    assert launch(layout, overrides=["capture.draw_distance_m=500"]).findings == []


def test_a_draw_distance_short_of_a_stare_s_point_is_refused_naming_the_range(layout):
    *_, findings, _, _ = offline(layout, overrides=["capture.draw_distance_m=499"])
    finding = only(findings, 52)
    assert finding.subject == "capture.draw_distance_m"
    assert "channel OVERWATCH-1's camera stands 500.0 m from the point it is aimed at" in \
        finding.message
    assert "leave it unset to draw every body at any range" in finding.message


def test_a_draw_distance_short_of_an_orbit_s_centre_is_refused(layout):
    # An orbit flies 200 m out and 518.2 m up: 555.5 m from its centre.
    document = run_document()
    document["capture"]["channels"] = [AN_ORBIT]
    *_, findings, _, _ = offline(layout, document, overrides=["capture.draw_distance_m=550"])
    assert "555.5 m" in only(findings, 52).message
    *_, findings, _, _ = offline(layout, document, overrides=["capture.draw_distance_m=560"])
    assert findings.findings == []


def test_a_stare_given_as_a_pose_names_no_point_and_is_not_judged(layout):
    document = run_document()
    document["capture"]["channels"] = [{"sensor_id": "POSE-1", "stare_x_m": 0.0, "stare_y_m": 0.0,
                                        "stare_z_m": 3000.0, "stare_pitch_deg": -90.0,
                                        "stare_yaw_deg": 0.0}]
    *_, findings, _, _ = offline(layout, document, overrides=["capture.draw_distance_m=50"])
    assert 52 not in checks(findings)


@pytest.mark.parametrize("metres", [0, -10])
def test_a_draw_distance_that_is_not_a_positive_number_is_refused_by_the_schema(metres):
    with pytest.raises(RunConfigurationRefusedError) as raised:
        RunConfiguration.from_document({"capture": {"draw_distance_m": metres}}, "test.run.json")
    [finding] = raised.value.findings.refusals
    assert (finding.check_id, finding.subject) == (1, "capture.draw_distance_m")


def test_the_echo_states_the_draw_distance_and_what_it_leaves_alone(layout):
    effective, validator, findings, result_path, capture = offline(
        layout, overrides=["capture.draw_distance_m=750"])
    assert not findings.refused
    free, headroom = validator.headroom(effective, capture)
    echo = LaunchEcho.compute(effective, "cap-test", capture, result_path, free, headroom,
                              validator.bytes_per_captured_second(effective), [])
    render = echo.to_dict()["render"]
    assert render["draw_distance_m"] == 750
    assert render["draw_distance"].startswith("750 m, rendering only: every vehicle keeps its body")
    assert "draw distance 750 m, rendering only" in echo.render()
    assert echo.values()["launch_echo.render.draw_distance_m"] == 750

    effective, validator, _findings, result_path, capture = offline(layout)
    unset = LaunchEcho.compute(effective, "cap-test", capture, result_path, free, headroom,
                               validator.bytes_per_captured_second(effective), [])
    assert unset.to_dict()["render"]["draw_distance_m"] is None
    assert "draw distance none: every body is drawn at any range" in unset.render()


def test_the_disk_estimate_counts_the_pictures_and_says_it_leaves_the_sidecars_out(layout):
    effective, validator, findings, _, _ = offline(layout, free=10**9)
    # The fixture's one channel at 2 Hz: its PNGs alone, at doc 10's measured size per pixel.
    values = effective.channel_values(0)
    assert validator.bytes_per_captured_second(effective) == pytest.approx(
        2.0 * PNG_BYTES_PER_PIXEL * values["width"] * values["height"])
    assert "truth sidecars, which grow with the traffic in frame, are not counted" in \
        only(findings, 19).message


def test_a_real_time_factor_as_available_is_refused(layout):
    *_, findings, _, _ = offline(layout, overrides=["pacing.real_time_factor=1.0"])
    assert "as_available" in only(findings, 41).message


def test_wall_clock_needs_a_floor(layout):
    *_, findings, _, _ = offline(layout, overrides=["pacing.mode=wall_clock"])
    finding = only(findings, 2)
    assert finding.subject == "pacing.min_achieved_factor"
    assert "below which the live run has failed" in finding.message


def test_a_floor_above_the_factor_is_refused(layout):
    *_, findings, _, _ = offline(layout, overrides=["pacing.mode=wall_clock",
                                                    "pacing.min_achieved_factor=1.5"])
    assert "exceeds" in only(findings, 41).message


def test_another_mode_is_refused_pointing_at_run_sctmv(layout):
    *_, findings, _, _ = offline(layout, overrides=["mode=traffic_manager_ambient"])
    assert "run_SCTMV.py" in only(findings, 4).message


# -- bindings and packages ------------------------------------------------------------------------

def test_a_world_package_the_scenario_was_not_compiled_against_is_refused(layout):
    world = layout.scenario_root / "gardnerville_fixture"
    lock = write_scenario_package(layout.scenario_root)
    text = lock.read_text(encoding="utf-8").replace('"origin_latitude": 38.91108',
                                                    '"origin_latitude": 38.5')
    lock.write_text(text, encoding="utf-8")
    assert world.is_dir()
    *_, findings, _, _ = offline(layout)
    assert "origin latitude" in only(findings, 5).message


def test_a_lock_of_another_version_is_refused(layout):
    write_scenario_package(layout.scenario_root, lock_version=2)
    *_, findings, _, _ = offline(layout)
    assert 6 in checks(findings)


SKIPPED_DRY_RUN = {"ran": False, "reason": "skipped at the author's request: nothing established that "
                                           "every vehicle the plan names enters the run"}


def test_a_lock_whose_compile_skipped_the_dry_run_is_refused_naming_the_scenario_and_the_reason(layout):
    # As the compiler writes the lock under --skip-dry-run.
    write_scenario_package(layout.scenario_root, dry_run=SKIPPED_DRY_RUN)
    *_, findings, _, _ = offline(layout)
    finding = only(findings, 54)
    assert finding.subject.startswith("scenario gardnerville_fixture@")
    assert "records that the compile skipped its SUMO-only run (skipped at the author's request" \
        in finding.message
    assert "scenario.accept_skipped_dry_run" in finding.message
    assert "--skip-dry-run" in finding.message


def test_a_lock_written_before_the_compiler_ran_a_dry_run_is_refused_the_same_way(layout):
    write_scenario_package(layout.scenario_root, dry_run=None)
    *_, findings, _, _ = offline(layout)
    assert "records no dry_run block, so it was written before the compiler" in only(findings, 54).message


def test_an_accepted_skipped_dry_run_launches_and_the_echo_says_so(layout):
    write_scenario_package(layout.scenario_root, dry_run=SKIPPED_DRY_RUN)
    *_, findings, _, _ = offline(layout, overrides=["scenario.accept_skipped_dry_run=true"])
    assert 54 not in checks(findings)
    assert launch(layout, overrides=["scenario.accept_skipped_dry_run=true"]).findings == []

    effective = resolve(layout, overrides=["scenario.accept_skipped_dry_run=true"])
    echo = LaunchEcho.compute(effective, "cap-test", layout.capture_root / "cap-test",
                              layout.runs_root / "run.result.json", PLENTY, None, 1.0, [])
    dry_run = echo.to_dict()["scenario"]["dry_run"]
    assert dry_run["ran"] is False and dry_run["skipped_accepted"] is True
    assert dry_run["statement"].startswith("skipped at the compile: skipped at the author's request")
    assert "accepted by scenario.accept_skipped_dry_run" in dry_run["statement"]
    assert "  dry run     skipped at the compile:" in echo.render()
    assert echo.values()["launch_echo.scenario.dry_run.skipped_accepted"] is True


def test_a_dry_run_that_ran_needs_no_acceptance_and_the_echo_states_what_it_found(layout):
    # The fixture lock records a completed run, as every shipped lock does.
    findings = launch(layout)
    assert findings.findings == []
    effective = resolve(layout)
    echo = LaunchEcho.compute(effective, "cap-test", layout.capture_root / "cap-test",
                              layout.runs_root / "run.result.json", PLENTY, None, 1.0, [])
    dry_run = echo.to_dict()["scenario"]["dry_run"]
    assert dry_run == {"ran": True, "skipped_accepted": False,
                       "statement": "ran with SUMO 1.27.0 over 86400.0 s: 1 vehicles loaded, 1 "
                                    "inserted, 0 discarded, 0 waiting at the end; 0 of 0 planned "
                                    "vehicles inserted; 0 collisions"}
    assert "dry run" not in echo.render()
    # Accepting accepts nothing where the run happened.
    effective = resolve(layout, overrides=["scenario.accept_skipped_dry_run=true"])
    echo = LaunchEcho.compute(effective, "cap-test", layout.capture_root / "cap-test",
                              layout.runs_root / "run.result.json", PLENTY, None, 1.0, [])
    assert echo.to_dict()["scenario"]["dry_run"]["skipped_accepted"] is False


def test_a_catalogue_of_another_digest_is_refused(layout, tmp_path):
    other = tmp_path / "other.catalogue.json"
    other.write_text(CATALOGUE.read_text(encoding="utf-8").replace(
        '"catalogue_digest": "', '"catalogue_digest": "0'), encoding="utf-8")
    profile = layout.profile_document()
    profile["paths"]["catalogue"] = str(other)
    path = tmp_path / "profile.json"
    path.write_text(json.dumps(profile), encoding="utf-8")
    *_, findings, _, _ = offline(layout, profile_path=path)
    assert "compiled against" in only(findings, 48).message


def test_an_empty_site_path_is_refused(layout, tmp_path):
    profile = layout.profile_document()
    profile["paths"]["runs_root"] = ""
    path = tmp_path / "profile.json"
    path.write_text(json.dumps(profile), encoding="utf-8")
    *_, findings, _, _ = offline(layout, profile_path=path)
    assert only(findings, 37).subject == "paths.runs_root"


def test_a_null_site_path_is_refused(layout, tmp_path):
    profile = layout.profile_document()
    profile["paths"]["capture_root"] = None
    path = tmp_path / "profile.json"
    path.write_text(json.dumps(profile), encoding="utf-8")
    *_, findings, _, _ = offline(layout, profile_path=path)
    assert only(findings, 37).subject == "paths.capture_root"


def test_a_result_inside_the_capture_root_is_refused(layout):
    *_, findings, _, _ = offline(layout, result=layout.capture_root / "x" / "run.result.json")
    assert "inside the capture root" in only(findings, 40).message


# -- disk --------------------------------------------------------------------------------------------

def test_a_declared_window_that_does_not_fit_on_disk_is_refused(layout):
    *_, findings, _, _ = offline(layout, free=10**9)
    assert "GB" in only(findings, 19).message and only(findings, 19).outcome == "refuse"


def test_an_open_window_that_may_outrun_the_disk_warns(layout):
    *_, findings, _, _ = offline(layout, overrides=["capture.window=25200:"], free=10**9)
    finding = only(findings, 19)
    assert finding.outcome == "warn" and "declares no end" in finding.message


# -- the unattended caller -----------------------------------------------------------------------

def test_an_unattended_warning_without_adjudication_is_refused(layout):
    findings = launch(layout, overrides=["caller=unattended", "solar.policy=ignore",
                                         f"result_path={layout.runs_root / 'r.result.json'}"])
    finding = only(findings, 34)
    assert "on_warning.lighting_honours_no_epoch" in finding.message


def test_an_unattended_warning_adjudicated_proceed_proceeds(layout):
    findings = launch(layout, overrides=[
        "caller=unattended", "solar.policy=ignore",
        f"result_path={layout.runs_root / 'r.result.json'}",
        'on_warning={"lighting_honours_no_epoch": "proceed"}'])
    assert not findings.refused
    assert checks(findings, "warn") == {15}


def test_a_warning_adjudicated_refuse_is_refused_even_attended(layout):
    findings = launch(layout, overrides=['on_warning={"lighting_honours_no_epoch": "refuse"}',
                                         "solar.policy=ignore"])
    assert 34 in checks(findings)


def test_unattended_needs_a_result_path(layout):
    findings = launch(layout, overrides=["caller=unattended"])
    assert only(findings, 2).subject == "result_path"


def test_an_unattended_run_does_not_inherit_undeclared_host_state(layout, tmp_path):
    profile = layout.profile_document()
    del profile["sumo"]
    path = tmp_path / "profile.json"
    path.write_text(json.dumps(profile), encoding="utf-8")
    overrides = ["caller=unattended", f"result_path={layout.runs_root / 'r.result.json'}"]
    findings = launch(layout, overrides=overrides, profile_path=path)
    assert "SUMO_HOME" in only(findings, 36).message
    profile["environment"] = ["SUMO_HOME", "PATH"]
    path.write_text(json.dumps(profile), encoding="utf-8")
    assert 36 not in checks(launch(layout, overrides=overrides, profile_path=path))


def test_an_expectation_that_disagrees_is_refused_naming_both(layout):
    findings = launch(layout, overrides=[
        'expect={"launch_echo.civil.begin": "2026-03-21T23:00:00-07:00"}'])
    finding = only(findings, 35)
    assert "23:00:00" in finding.message and "07:00:00" in finding.message


def test_an_expectation_that_holds_changes_nothing(layout):
    findings = launch(layout, overrides=[
        'expect={"launch_echo.civil.begin": "2026-03-21T07:00:00-07:00", '
        '"capture.capture_hz": 2.0, "launch_echo.sun.at_begin.elevation_deg": {"at_most": 6.0}}'])
    assert findings.findings == []


def test_an_expectation_bound_that_fails_is_refused(layout):
    findings = launch(layout, overrides=[
        'expect={"launch_echo.sun.at_begin.elevation_deg": {"at_least": 10.0}}'])
    assert 35 in checks(findings)


def test_an_expectation_naming_nothing_is_refused(layout):
    findings = launch(layout, overrides=['expect={"launch_echo.civil.begun": "x"}'])
    assert "neither a configuration field" in only(findings, 35).message


# -- the server checks -------------------------------------------------------------------------------------------

class _Blueprint:
    def __init__(self, blueprint_id: str, attributes: set[str]) -> None:
        self.id = blueprint_id
        self._attributes = attributes

    def has_attribute(self, name: str) -> bool:
        return name in self._attributes


class _Library:
    def __init__(self, blueprints: list[_Blueprint]) -> None:
        self._blueprints = blueprints

    def find(self, blueprint_id: str) -> _Blueprint:
        for blueprint in self._blueprints:
            if blueprint.id == blueprint_id:
                return blueprint
        raise KeyError(blueprint_id)

    def __iter__(self):
        return iter(self._blueprints)


class _World:
    """A world that answers the server checks' two reads and refuses every write."""

    RGB = {"image_size_x", "image_size_y", "fov", "sensor_tick", "post_process_profile"}
    DEPTH = {"image_size_x", "image_size_y", "fov", "sensor_tick", "max_range"}

    def __init__(self, sun=True, rgb=None, depth=None, vehicles=("vehicle.lincoln.mkz",
                                                                "vehicle.dodge.charger")) -> None:
        self.sun = {"solar_time": 12.0} if sun else None
        self.library = _Library([_Blueprint("sensor.camera.rgb", self.RGB if rgb is None else rgb),
                                 _Blueprint("sensor.camera.depth",
                                            self.DEPTH if depth is None else depth),
                                 *(_Blueprint(v, set()) for v in vehicles)])

    def get_solar_state(self):
        return self.sun

    def get_blueprint_library(self):
        return self.library

    def __getattr__(self, name):
        raise AssertionError(f"the server checks called world.{name}")


def server(layout, world, document=None, overrides=()):
    effective = resolve(layout, document, overrides)
    return RunConfigurationValidator().validate_against_server(effective, world)


def test_the_server_checks_pass_against_a_world_that_has_what_the_run_needs(layout):
    assert server(layout, _World()).findings == []


def test_a_world_with_no_sun_is_refused(layout):
    assert "no CesiumSunSky" in only(server(layout, _World(sun=False)), 23).message


def test_a_world_with_no_sun_is_accepted_when_nothing_needs_one(layout):
    findings = server(layout, _World(sun=False), overrides=["solar.policy=ignore",
                                                            "solar.require_sun=false"])
    assert 23 not in checks(findings)


def test_a_camera_attribute_the_build_lacks_is_refused_by_name(layout):
    findings = server(layout, _World(rgb=_World.RGB - {"post_process_profile"}))
    assert "post_process_profile" in only(findings, 24).message


def test_the_depth_camera_is_checked_for_every_run(layout):
    # Every channel gets a depth camera, so the blueprint it is spawned from is checked against the
    # server on every run, and a server with no depth camera at all is refused.
    world = _World(depth=_World.DEPTH - {"max_range"})
    assert "max_range" in only(server(layout, world), 24).message
    missing = _World()
    missing.library = _Library([b for b in missing.library if b.id != "sensor.camera.depth"])
    assert "no sensor.camera.depth blueprint" in only(server(layout, missing), 24).message


def test_the_depth_camera_is_checked_for_an_orbit_as_for_a_stare(layout):
    # An orbit gets a depth camera too, so the blueprint it is spawned from is checked against the
    # server for a run whose only channel is an orbit.
    document = run_document()
    document["capture"]["channels"] = [AN_ORBIT]
    world = _World(depth=_World.DEPTH - {"max_range"})
    assert "max_range" in only(server(layout, world, document), 24).message


def test_a_vehicle_blueprint_the_server_lacks_is_refused(layout):
    findings = server(layout, _World(vehicles=("vehicle.lincoln.mkz",)))
    assert "vehicle.dodge.charger" in only(findings, 25).message


# -- pre-roll -----------------------------------------------------------------------------------------

def test_a_prewarm_that_fell_below_the_floor_refuses_the_window(layout):
    effective = resolve(layout, overrides=["pacing.mode=wall_clock",
                                           "pacing.min_achieved_factor=0.8"])
    assert RunConfigurationValidator.preroll_pace(effective, 0.31).by_check(44)
    assert not RunConfigurationValidator.preroll_pace(effective, 0.95).findings
    assert not RunConfigurationValidator.preroll_pace(resolve(layout), 0.1).findings
