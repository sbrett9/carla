"""The gate records are observations read from the run, and the monitor shows only what they read.

Plan 12 §7.1-§7.2, D12.14, D12.16. A gate record names what was observed, the threshold and whether it
was met, and nothing sums them; a gate whose input nothing publishes is `skipped` with its reason, so
*not measured* never reads as *passed*. The monitor is handed a snapshot and formats it: a snapshot
whose figures could not all have come from one computation -- an achieved factor that disagrees with
simulated over wall time -- must be shown exactly as given, which is only true of a monitor that
computes nothing. Off a terminal it writes log lines and no escape codes.
"""
from __future__ import annotations

import io
import logging
import sys
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))
sys.path.insert(0, str(_REPO / "CarlaControl" / "test"))

from CaptureFakes import FakeRecorder, FakeServer, FakeSession  # noqa: E402
from RunCaptureFixture import Layout, run_document  # noqa: E402

from carlacontrol.RunCloseoutReport import ChannelCapture, RunCloseoutReport  # noqa: E402
from carlacontrol.RunConfiguration import RunConfiguration  # noqa: E402
from carlacontrol.RunConfigurationResolver import RunConfigurationResolver  # noqa: E402
from carlacontrol.SessionMonitor import SessionMonitor  # noqa: E402
from carlacontrol.SiteProfile import SiteProfile  # noqa: E402
from carlacontrol.WindowAdmissions import WindowAdmissions  # noqa: E402


@pytest.fixture
def layout(tmp_path: Path) -> Layout:
    return Layout(tmp_path)


def closeout(layout: Layout, overrides=(), policy="freeze_at_window_start"):
    site = SiteProfile.discover(layout.root, layout.write_profile(), environ={})
    run = RunConfiguration.from_document(run_document(), "fixture.run.json")
    parsed = [(*RunConfiguration.parse_override(t), f"--set {t}") for t in overrides]
    effective = RunConfigurationResolver(site).resolve(run, parsed)
    server = FakeServer()
    session = FakeSession(server, {"warm_up_to": 25200.0, "real_time_factor": 0.0,
                                   "illumination": {"policy": policy}})
    report = RunCloseoutReport(effective, clock=iter(range(0, 10**6, 10)).__next__)
    report.attach(session, WindowAdmissions(25200.0, 27000.0, 1.0))
    recorder = FakeRecorder(2.0)
    server.recorders.append(recorder)
    report.add_channel(ChannelCapture("OVERWATCH-1", layout.capture_root / "c", recorder))
    return report, session, recorder


def gate(gates: list[dict], gate_id: str) -> dict:
    return next(g for g in gates if g["id"] == gate_id)


def test_the_snapshot_reads_the_session_and_the_recorders(layout):
    report, session, recorder = closeout(layout)
    for _ in range(900):
        session.Advance()
    snapshot = report.snapshot()
    assert snapshot["sim_time_s"] == 26100.0
    assert snapshot["window"]["progress"] == pytest.approx(0.5)
    assert snapshot["channels"][0]["written"] == 1800 == recorder.Saved
    assert snapshot["illumination"]["declared_civil"] == "t=26100"
    assert snapshot["pacing"]["achieved_factor"] == 3.2
    # The newest pass is one step ahead of the rendered clock.
    assert snapshot["admission"]["sim_time_s"] == 26101.0
    assert (snapshot["admission"]["population"], snapshot["admission"]["total_admissions"]) == (7, 7)


def test_the_snapshot_carries_the_session_s_checks_and_the_closeout_shows_them(layout):
    report, session, _ = closeout(layout)
    session.Advance()
    snapshot = report.snapshot()
    assert snapshot["scenario_checks"]["compile_lock"]["compiled"] is True
    assert snapshot["scenario_checks"]["teleporting"]["enabled"] is False
    text = RunCloseoutReport.render(snapshot, report.gates(snapshot, 0))
    assert f"compile lock: {session.Report.CompileLock}" in text
    assert f"routed by {session.Report.CompileLock.RoutedByText}" in text
    assert f"dry run {session.Report.CompileLock.DryRunText}" in text
    assert snapshot["scenario_checks"]["compile_lock"]["skipped_dry_run_accepted"] is False
    assert "teleporting: disabled (time-to-teleport '-1')" in text
    assert "admission passes in the window: 0; most population" in text


def test_with_no_draw_distance_the_closeout_says_nothing_of_one(layout):
    report, session, _ = closeout(layout)
    session.Advance()
    snapshot = report.snapshot()
    assert snapshot["render"]["draw_distance_m"] is None
    assert snapshot["channels"][0]["vehicles_beyond_draw_distance"] == 0
    assert "draw distance" not in RunCloseoutReport.render(snapshot, report.gates(snapshot, 0))


def test_a_draw_distance_and_what_each_channel_marked_beyond_it_reach_the_closeout(layout):
    report, session, recorder = closeout(layout)
    session.Report.DrawDistanceMetres = 750.0
    session.DrawDistanceMetres = 750.0
    session.Advance()
    recorder.DrawDistanceCaptures = 2
    recorder.VehiclesBeyondDrawDistance = 31
    recorder.VehiclesPartlyBeyondDrawDistance = 4
    snapshot = report.snapshot()
    assert (snapshot["render"]["draw_distance_m"], snapshot["render"]["draw_distance_in_force_m"],
            snapshot["render"]["draw_distance_refused"]) == (750.0, 750.0, None)
    channel = snapshot["channels"][0]
    assert (channel["draw_distance_captures"], channel["vehicles_beyond_draw_distance"],
            channel["vehicles_partly_beyond_draw_distance"]) == (2, 31, 4)
    text = RunCloseoutReport.render(snapshot, report.gates(snapshot, 0))
    assert "draw distance 750 m, rendering only" in text
    assert "under the draw distance 2 captures: 31 vehicle records marked wholly beyond it, 4 partly" \
        in text

    # A server that refused it drew every body at any range, and the closeout says so.
    session.Report.DrawDistanceRefused = "unknown method 'set_actors_max_draw_distance'"
    session.DrawDistanceMetres = None
    text = RunCloseoutReport.render(report.snapshot(), [])
    assert "refused by the server, so every body was drawn at any range" in text


def test_an_optional_render_set_limit_and_what_it_left_out_reach_the_closeout_and_the_panel(layout):
    site = SiteProfile.discover(layout.root, layout.write_profile(), environ={})
    run = RunConfiguration.from_document(run_document(), "fixture.run.json")
    effective = RunConfigurationResolver(site).resolve(run, [])
    server = FakeServer()
    admissions = WindowAdmissions(25200.0, 27000.0, 1.0)
    session = FakeSession(server, {"warm_up_to": 25200.0, "real_time_factor": 0.0,
                                   "illumination": {"policy": "freeze_at_window_start"},
                                   "render_set": "all", "capacity": 4,
                                   "on_admission_pass": admissions.observe})
    report = RunCloseoutReport(effective, clock=iter(range(0, 10**6, 10)).__next__)
    report.attach(session, admissions)
    session.Report.Releases["Capacity"] = 2
    for _ in range(3):
        session.Advance()

    snapshot = report.snapshot()
    render = snapshot["render"]
    assert (render["render_set_limits"], render["releases"]) == (True, {"Capacity": 2})
    admission = snapshot["admission"]
    assert (admission["limited"], admission["population"], admission["admitted"],
            admission["left_out"], admission["capacity"]) == (True, 7, 4, 3, 4)
    assert snapshot["admissions"]["window"]["limited"] is True
    assert snapshot["admissions"]["window"]["most_left_out"] == 3

    text = RunCloseoutReport.render(snapshot, report.gates(snapshot, 0))
    assert "an optional limit; a vehicle outside it is simulated by SUMO and is not in CARLA" in text
    assert "released 0 leaving the policy and 2 for the capacity" in text
    assert "drawn 4, shed 3; 3 without a body" in text
    assert "of them left vehicles without a body, at most 3 at once and 3 shed for the capacity" in text

    rows = SessionMonitor.lines(snapshot)
    assert any(row.startswith("sumo  population 7   eligible 7   drawn 4   shed 3   without a body 3")
               for row in rows)
    assert "population 7, drawn 4, 3 without a body" in SessionMonitor.line(snapshot)


def test_a_clean_run_meets_every_measured_gate(layout):
    report, session, _ = closeout(layout)
    session.Advance()
    gates = report.gates(report.snapshot(), unadjudicated_warnings=0)
    evaluated = [g for g in gates if g["status"] == "evaluated"]
    assert evaluated and all(g["met"] for g in evaluated)


def test_a_dropped_capture_is_a_gate_not_met_and_a_loud_condition(layout):
    report, session, recorder = closeout(layout)
    recorder.drop_every_step = 1
    session.Advance()
    snapshot = report.snapshot()
    dropped = gate(report.gates(snapshot, 0), "capture.recorder_dropped[OVERWATCH-1]")
    assert (dropped["observed"], dropped["threshold"], dropped["met"]) == (1, 0, False)
    assert [c for c, _ in report.loud_conditions(snapshot)] == ["recorder_dropped"]


def test_a_still_dropped_for_want_of_its_own_frame_s_truth_is_a_gate_not_met(layout):
    # Doc 06 §8.2: a still is written with the truth of its own frame or not at all, and one the client
    # held no truth of its frame for is dropped and counted; the gate is that there are none.
    report, session, recorder = closeout(layout)
    session.Advance()
    met = gate(report.gates(report.snapshot(), 0), "capture.frame_unpaired[OVERWATCH-1]")
    assert (met["status"], met["observed"], met["threshold"], met["met"]) == ("evaluated", 0, 0, True)

    recorder.FrameUnpaired = 2
    snapshot = report.snapshot()
    assert snapshot["channels"][0]["frame_unpaired"] == 2
    unpaired = gate(report.gates(snapshot, 0), "capture.frame_unpaired[OVERWATCH-1]")
    assert (unpaired["observed"], unpaired["threshold"], unpaired["met"]) == (2, 0, False)
    assert unpaired["owner"] == "06 §8.2"
    assert "frame unpaired 2" in RunCloseoutReport.render(snapshot, report.gates(snapshot, 0))


def test_a_recorder_built_before_it_dropped_unpaired_stills_skips_the_gate_rather_than_meeting_it(layout):
    # A wheel built before the recorder refused such stills has no such counter: not measured is never
    # passed.
    report, session, recorder = closeout(layout)
    session.Advance()
    del recorder.FrameUnpaired
    snapshot = report.snapshot()
    assert snapshot["channels"][0]["frame_unpaired"] is None
    skipped = gate(report.gates(snapshot, 0), "capture.frame_unpaired[OVERWATCH-1]")
    assert skipped["status"] == "skipped"
    assert "frame unpaired" not in RunCloseoutReport.render(snapshot, report.gates(snapshot, 0))


def test_a_capture_listed_without_its_frame_s_render_set_is_a_gate_not_met(layout):
    report, session, recorder = closeout(layout)
    session.Advance()
    recorder.RenderSetUnpaired = 3
    snapshot = report.snapshot()
    assert snapshot["channels"][0]["render_set_paired"] == recorder.RenderSetPaired > 0
    unpaired = gate(report.gates(snapshot, 0), "capture.render_set_unpaired[OVERWATCH-1]")
    assert (unpaired["observed"], unpaired["threshold"], unpaired["met"]) == (3, 0, False)
    assert "render set unpaired 3" in RunCloseoutReport.render(snapshot, report.gates(snapshot, 0))


def test_a_capture_written_with_its_supervision_unknown_is_a_gate_not_met(layout):
    # Doc 06 §8.2: a capture of a frame a plan was in force on, whose own frame's supervision was not to
    # be had, is written with supervision="unknown" and none in its place; the gate is that there are none.
    report, session, recorder = closeout(layout)
    session.Advance()
    met = gate(report.gates(report.snapshot(), 0), "capture.supervision_unpaired[OVERWATCH-1]")
    assert (met["status"], met["observed"], met["threshold"], met["met"]) == ("evaluated", 0, 0, True)

    recorder.SupervisionUnpaired = 2
    snapshot = report.snapshot()
    assert snapshot["channels"][0]["supervision_paired"] == recorder.SupervisionPaired > 0
    assert snapshot["channels"][0]["supervision_unpaired"] == 2
    unknown = gate(report.gates(snapshot, 0), "capture.supervision_unpaired[OVERWATCH-1]")
    assert (unknown["observed"], unknown["threshold"], unknown["met"]) == (2, 0, False)
    assert unknown["owner"] == "06 §8.2"
    assert f"supervision {recorder.SupervisionPaired} paired 2 unknown" in RunCloseoutReport.render(
        snapshot, report.gates(snapshot, 0))


def test_a_recorder_built_before_it_counted_supervision_skips_the_gate_rather_than_meeting_it(layout):
    # A wheel built before the sidecar carried supervision has no such counter: not measured is never
    # passed.
    report, session, recorder = closeout(layout)
    session.Advance()
    del recorder.SupervisionPaired
    del recorder.SupervisionUnpaired
    snapshot = report.snapshot()
    assert snapshot["channels"][0]["supervision_unpaired"] is None
    skipped = gate(report.gates(snapshot, 0), "capture.supervision_unpaired[OVERWATCH-1]")
    assert skipped["status"] == "skipped"
    assert "supervision" not in RunCloseoutReport.render(snapshot, report.gates(snapshot, 0)).split(
        "render set unpaired")[1].split("->")[0]


def test_a_capture_written_without_a_solar_block_is_a_gate_not_met(layout):
    # Doc 11 §8.4: a capture with no recorded sun can be neither stratified by band nor replayed, and
    # the gate is that there are none.
    report, session, recorder = closeout(layout)
    session.Advance()
    met = gate(report.gates(report.snapshot(), 0), "capture.solar_block_missing[OVERWATCH-1]")
    assert (met["status"], met["observed"], met["threshold"], met["met"]) == ("evaluated", 0, 0, True)

    recorder.SolarBlockMissing = 2
    snapshot = report.snapshot()
    assert snapshot["channels"][0]["solar_block_missing"] == 2
    missing = gate(report.gates(snapshot, 0), "capture.solar_block_missing[OVERWATCH-1]")
    assert (missing["observed"], missing["threshold"], missing["met"]) == (2, 0, False)
    assert missing["owner"] == "11 §8.4"
    assert "solar block missing 2" in RunCloseoutReport.render(snapshot, report.gates(snapshot, 0))


def test_a_capture_written_with_its_lights_or_pose_source_unknown_is_a_gate_not_met(layout):
    # Doc 08 §5.1: a vehicle in the picture carries its lights and, drawn by a SUMO drive, its pose source,
    # from the snapshot of the capture's own frame, or the capture says they are unknown; the gates are
    # that there are none.
    report, session, recorder = closeout(layout)
    session.Advance()
    for field in ("lights_unknown", "pose_source_unknown"):
        met = gate(report.gates(report.snapshot(), 0), f"capture.{field}[OVERWATCH-1]")
        assert (met["status"], met["observed"], met["threshold"], met["met"]) == ("evaluated", 0, 0, True)

    recorder.LightsUnknown = 3
    recorder.PoseSourceUnknown = 2
    snapshot = report.snapshot()
    assert (snapshot["channels"][0]["lights_unknown"], snapshot["channels"][0]["pose_source_unknown"]) == (3, 2)
    lights = gate(report.gates(snapshot, 0), "capture.lights_unknown[OVERWATCH-1]")
    poses = gate(report.gates(snapshot, 0), "capture.pose_source_unknown[OVERWATCH-1]")
    assert (lights["observed"], lights["met"], poses["observed"], poses["met"]) == (3, False, 2, False)
    assert lights["owner"] == poses["owner"] == "08 §5.1"
    assert "lights unknown 3, pose source unknown 2" in RunCloseoutReport.render(snapshot,
                                                                                report.gates(snapshot, 0))


def test_a_recorder_built_before_it_wrote_lights_and_pose_sources_skips_both_gates(layout):
    # Not measured is never passed.
    report, session, recorder = closeout(layout)
    session.Advance()
    del recorder.LightsUnknown
    del recorder.PoseSourceUnknown
    snapshot = report.snapshot()
    assert snapshot["channels"][0]["lights_unknown"] is None
    for field in ("lights_unknown", "pose_source_unknown"):
        assert gate(report.gates(snapshot, 0), f"capture.{field}[OVERWATCH-1]")["status"] == "skipped"
    assert "lights unknown" not in RunCloseoutReport.render(snapshot, report.gates(snapshot, 0))


def test_a_server_that_refused_the_pose_source_is_named_and_the_run_goes_on(layout):
    # A server built before it carried a pose source refuses the session's; the closeout says what it said.
    report, session, _ = closeout(layout)
    session.Advance()
    assert report.snapshot()["render"]["pose_source_refused"] is None
    assert "pose source: refused" not in RunCloseoutReport.render(report.snapshot(), [])

    session.Report.PoseSourceRefused = "unknown method 'update_pose_source'"
    snapshot = report.snapshot()
    assert snapshot["render"]["pose_source_refused"] == "unknown method 'update_pose_source'"
    assert ("pose source: refused by the server, so no capture says where a vehicle's pose came from "
            "(unknown method 'update_pose_source')") in RunCloseoutReport.render(snapshot, [])


def test_a_server_built_before_the_jump_state_is_named_with_the_jumps_written_as_sumo(layout):
    # Such a server refuses the jump list for its count; the session sends every change without it and
    # names each jumping body sumo, and the closeout says so and how many, while the run goes on.
    report, session, _ = closeout(layout)
    session.Advance()
    render = report.snapshot()["render"]
    assert (render["pose_source_without_jump"], render["pose_source_jumps_named_sumo"]) == (None, 0)
    assert "jump state" not in RunCloseoutReport.render(report.snapshot(), [])

    words = ("rpclib: Function 'update_pose_source' was called with an invalid number of arguments. "
             "Expected: 6, got: 7")
    session.Report.PoseSourceWithoutJump = words
    session.Report.PoseSourceJumpsNamedSumo = 3
    snapshot = report.snapshot()
    assert snapshot["render"]["pose_source_without_jump"] == words
    assert snapshot["render"]["pose_source_jumps_named_sumo"] == 3
    assert snapshot["render"]["pose_source_refused"] is None
    assert (f"pose source: the server was built before the jump state, so 3 jump(s) were sent and written "
            f"as sumo ({words})") in RunCloseoutReport.render(snapshot, [])


def test_a_session_report_built_before_the_jump_state_reads_as_no_fallback(layout):
    # A report from a CarlaNet built before it recorded the fallback carries neither field.
    report, session, _ = closeout(layout)
    session.Advance()
    del session.Report.PoseSourceWithoutJump
    del session.Report.PoseSourceJumpsNamedSumo
    render = report.snapshot()["render"]
    assert (render["pose_source_without_jump"], render["pose_source_jumps_named_sumo"]) == (None, 0)


def test_a_capture_whose_image_header_disagreed_with_its_frame_s_snapshot_is_a_gate_not_met(layout):
    # The recorder wrote the snapshot's pose, so the still is placed right; the gate records that the
    # server stamped the header after the frame.
    report, session, recorder = closeout(layout)
    session.Advance()
    recorder.SensorPoseHeaderDisagreed = 2
    recorder.SensorPoseFromHeader = 1
    snapshot = report.snapshot()
    channel = snapshot["channels"][0]
    assert channel["sensor_pose_from_snapshot"] == recorder.SensorPoseFromSnapshot > 0
    assert (channel["sensor_pose_header_disagreed"], channel["sensor_pose_from_header"]) == (2, 1)
    disagreed = gate(report.gates(snapshot, 0), "capture.sensor_pose_header_disagreed[OVERWATCH-1]")
    assert (disagreed["observed"], disagreed["threshold"], disagreed["met"]) == (2, 0, False)
    text = RunCloseoutReport.render(snapshot, report.gates(snapshot, 0))
    assert "header disagreed 2, from the header 1" in text


def test_the_depth_pose_gate_is_skipped_without_a_depth_camera_and_evaluated_with_one(layout):
    report, session, recorder = closeout(layout)
    session.Advance()
    skipped = gate(report.gates(report.snapshot(), 0),
                   "capture.depth_pose_header_disagreed[OVERWATCH-1]")
    assert skipped["status"] == "skipped" and skipped["met"] is None
    assert "no depth camera" in skipped["skip_reason"]

    recorder.ChecksDepthPose = True
    recorder.OcclusionDepthPoseHeaderDisagreed = 1
    evaluated = gate(report.gates(report.snapshot(), 0),
                     "capture.depth_pose_header_disagreed[OVERWATCH-1]")
    assert (evaluated["observed"], evaluated["threshold"], evaluated["met"]) == (1, 0, False)


def test_an_unmeasured_gate_is_skipped_with_its_reason_never_passed(layout):
    report, session, _ = closeout(layout)
    session.Advance()
    gates = report.gates(report.snapshot(), 0)
    for gate_id in ("capture.captured_minus_written", "radiometry.profile_digest_present",
                    "supervision.manifest_closing_record"):
        record = gate(gates, gate_id)
        assert record["status"] == "skipped" and record["met"] is None and record["skip_reason"]


def test_the_closing_record_gate_is_met_exactly_when_the_manifest_ends_with_its_terminal_row(
        layout, tmp_path):
    report, session, _ = closeout(layout)
    session.Advance()
    manifest = tmp_path / "truth" / "manifest.jsonl"
    report.attach_manifest(manifest)

    def closing() -> dict:
        return gate(report.gates(report.snapshot(), 0), "supervision.manifest_closing_record")

    # No manifest on disk at all: nothing was written, and the gate says so rather than passing.
    assert (closing()["status"], closing()["met"]) == ("evaluated", False)

    manifest.parent.mkdir(parents=True)
    rows = ['{"row": "manifest_opened", "run": null}', '{"row": "render_admitted", "sumo_id": "a"}']
    manifest.write_text("\n".join(rows) + "\n", encoding="utf-8")
    record = closing()
    assert (record["observed"], record["threshold"], record["met"]) == (False, True, False)
    assert record["owner"] == "04 §12.7"

    # The terminal row, then a kill part-way through a row that should never follow it: the last
    # complete row is what is read, and the cut line is left off.
    with manifest.open("a", encoding="utf-8") as file:
        file.write('{"row": "manifest_closed", "ended": "caller_stopped"}\n')
    assert closing()["met"] is True
    with manifest.open("a", encoding="utf-8") as file:
        file.write('{"row": "render_adm')
    assert closing()["met"] is True
    assert RunCloseoutReport.last_manifest_row(manifest) == "manifest_closed"
    assert RunCloseoutReport.last_manifest_row(tmp_path / "missing.jsonl") is None

    # A terminal row longer than the first read of the file's end -- it lists the intervals still
    # open, and grows with the plan -- is still read whole.
    long_open = ", ".join(f'{{"instance_id": "Plan/instance-{index:05d}", "participant": "v{index}", '
                          f'"phase": "dwell"}}' for index in range(2000))
    manifest.write_text('{"row": "manifest_opened", "run": null}\n'
                        f'{{"row": "manifest_closed", "open_intervals": [{long_open}]}}\n',
                        encoding="utf-8")
    assert manifest.stat().st_size > 2 * 65536
    assert RunCloseoutReport.last_manifest_row(manifest) == "manifest_closed"


def test_the_solar_gate_is_skipped_when_no_sun_is_bound(layout):
    report, session, _ = closeout(layout, ["solar.policy=ignore"], policy="ignore")
    session.Advance()
    record = gate(report.gates(report.snapshot(), 0), "solar.applied_equals_confirmed")
    assert record["status"] == "skipped"


def test_the_solar_gate_compares_the_worst_residual_with_its_tolerance(layout):
    report, session, _ = closeout(layout)
    session.Report.WorstSolarResidualDegrees = 0.05
    session.Advance()
    record = gate(report.gates(report.snapshot(), 0), "solar.applied_equals_confirmed")
    assert (record["observed"], record["threshold"], record["met"]) == (0.05, 0.01, False)


def test_a_live_run_below_its_floor_is_loud_and_misses_its_pace_gate(layout):
    report, session, _ = closeout(layout, ["pacing.mode=wall_clock",
                                           "pacing.min_achieved_factor=0.8"])
    session.world.achieved_factor = 0.5
    session.Advance()
    snapshot = report.snapshot()
    assert [c for c, _ in report.loud_conditions(snapshot)] == ["pace_below_floor"]
    assert gate(report.gates(snapshot, 0), "pacing.achieved_factor")["met"] is False


def test_an_unadjudicated_warning_is_a_gate_not_met(layout):
    report, _, _ = closeout(layout)
    assert gate(report.gates(report.snapshot(), 1), "launch.warnings_adjudicated")["met"] is False


def test_the_bridge_divergence_reaches_the_snapshot_the_closeout_and_both_gates_at_their_limits(layout):
    report, session, _ = closeout(layout)
    session.Advance()
    snapshot = report.snapshot()
    divergence = snapshot["divergence"]
    # Seven rendered vehicles over twenty ticks, each compared once per tick.
    assert divergence["samples"] == 140 and divergence["vehicle_ticks_with_no_read_back"] == 0
    assert (divergence["worst_position_m"], divergence["mean_position_m"]) == (0.0001, 0.00002)
    assert (divergence["worst_yaw_deg"], divergence["worst_pitch_deg"],
            divergence["worst_roll_deg"]) == (0.0001, 0.0001, 0.0001)
    assert (divergence["worst_velocity_m_per_s"], divergence["mean_velocity_m_per_s"],
            divergence["mean_commanded_speed_m_per_s"]) == (0.000006, 0.000001, 15.2)
    assert divergence["worst_position_on"] == {"sumo_id": "flow.0", "sim_time_s": 25201.0,
                                               "tick": 20, "actor_id": 11}
    assert divergence["worst_velocity_on"]["sumo_id"] == "flow.1"

    gates = report.gates(snapshot, 0)
    position = gate(gates, "bridge.position_divergence")
    velocity = gate(gates, "bridge.velocity_divergence")
    assert (position["observed"], position["threshold"], position["comparison"],
            position["met"]) == (0.0001, 0.01, "at_most", True)
    assert (velocity["observed"], velocity["threshold"], velocity["comparison"],
            velocity["met"]) == (0.000006, 0.01, "at_most", True)
    assert position["owner"] == velocity["owner"] == "06 §4.3"

    text = RunCloseoutReport.render(snapshot, gates)
    assert ("bridge divergence: worst position 0.000100 m on flow.0 at t=25201 s, mean 0.000020 m "
            "over 140 vehicle-ticks; worst velocity 0.000006 m/s on flow.1") in text
    assert "mean 0.000001 m/s against a mean commanded 15.200 m/s" in text
    assert "read back by nothing" not in text


def test_a_body_out_of_place_or_reporting_no_velocity_misses_its_divergence_gate(layout):
    report, session, _ = closeout(layout)
    session.Advance()
    # Half a metre out on position: the characteristic residual of a wrong reference point. The
    # velocity gate is untouched by it.
    session.Report.WorstPositionDivergenceMetres = 0.5
    gates = report.gates(report.snapshot(), 0)
    assert (gate(gates, "bridge.position_divergence")["observed"],
            gate(gates, "bridge.position_divergence")["met"]) == (0.5, False)
    assert gate(gates, "bridge.velocity_divergence")["met"] is True
    # Bodies that report no velocity: the gap is the whole commanded speed.
    session.Report.WorstVelocityDivergenceMetresPerSecond = 21.8
    session.Report.VehicleTicksWithNoReadBack = 3
    snapshot = report.snapshot()
    gates = report.gates(snapshot, 0)
    assert (gate(gates, "bridge.velocity_divergence")["observed"],
            gate(gates, "bridge.velocity_divergence")["met"]) == (21.8, False)
    assert "; 3 vehicle-ticks read back by nothing" in RunCloseoutReport.render(snapshot, gates)


def test_the_divergence_limits_are_run_configuration_fields(layout):
    report, session, _ = closeout(layout, ["bridge.position_divergence_limit_m=1.0",
                                           "bridge.velocity_divergence_limit_m_per_s=25"])
    session.Advance()
    session.Report.WorstPositionDivergenceMetres = 0.5
    session.Report.WorstVelocityDivergenceMetresPerSecond = 21.8
    gates = report.gates(report.snapshot(), 0)
    position = gate(gates, "bridge.position_divergence")
    velocity = gate(gates, "bridge.velocity_divergence")
    assert (position["threshold"], position["met"]) == (1.0, True)
    assert (velocity["threshold"], velocity["met"]) == (25.0, True)


def test_a_run_that_compared_nothing_skips_both_divergence_gates_rather_than_meeting_them(layout):
    report, session, _ = closeout(layout)
    # Before the first step nothing has been compared, and no body was driven.
    snapshot = report.snapshot()
    assert snapshot["divergence"]["samples"] == 0
    assert snapshot["divergence"]["worst_position_on"] is None
    for gate_id in ("bridge.position_divergence", "bridge.velocity_divergence"):
        record = gate(report.gates(snapshot, 0), gate_id)
        assert (record["status"], record["met"]) == ("skipped", None)
        assert record["skip_reason"] == "no vehicle-tick was compared: no body was driven"
    # Poses written and read back by nothing: still nothing compared, and the reason says how many.
    session.Report.VehicleTicksWithNoReadBack = 40
    record = gate(report.gates(report.snapshot(), 0), "bridge.position_divergence")
    assert record["status"] == "skipped"
    assert record["skip_reason"] == "no vehicle-tick was compared: 40 were written and read back by nothing"
    assert "bridge divergence" not in RunCloseoutReport.render(snapshot, [])
    # Before the session is attached there is no report to read, and the gates say so.
    detached = RunCloseoutReport(report.effective)
    for gate_id in ("bridge.position_divergence", "bridge.velocity_divergence"):
        record = gate(detached.gates(detached.snapshot(), 0), gate_id)
        assert (record["status"], record["skip_reason"]) == ("skipped", "the session has not started")


def test_no_gate_is_an_aggregate(layout):
    report, session, _ = closeout(layout)
    session.Advance()
    ids = [g["id"] for g in report.gates(report.snapshot(), 0)]
    assert not any(word in gate_id for gate_id in ids for word in ("overall", "verdict", "all"))


# -- the monitor ---------------------------------------------------------------------------------------

SNAPSHOT = {
    "scenario": "gardnerville_fixture@abc", "window_name": "morning",
    "window": {"begin_s": 25200.0, "end_s": 27000.0, "progress": 0.25},
    "sim_time_s": 25650.0,
    "illumination": {"policy": "advance", "declared_civil": "2026-03-21T07:07:30-07:00",
                     "sun_elevation_deg": 1.23, "residual_deg": 0.0, "frame": 9,
                     "sun_declared": "x"},
    # Deliberately impossible together: 450 s simulated over 100 s of wall is 4.5, not 0.777.
    "pacing": {"paced": False, "declared_factor": 0.0, "achieved_factor": 0.777,
               "last_window_factor": 0.5, "worst_window_factor": 0.5, "behind_schedule_s": 0.0,
               "completed_windows": 3},
    "render": {"rendered_now": 41, "ticks": 9000, "sumo_steps": 450, "poses_computed": 1,
               "batch_failures": 0},
    "admission": {"sim_time_s": 25651.0, "world_tick": 9000, "population": 139, "newly_admitted": 2,
                  "released": 1, "total_admissions": 4000},
    "channels": [{"sensor_id": "OVERWATCH-1", "directory": "d", "captured": None, "written": 900,
                  "recorder_dropped": 2, "illumination_paired": 900, "illumination_unpaired": 0,
                  "occlusion_measured": 880, "occlusion_unmatched": 20}],
    "wall_elapsed_s": 100.0,
}


def test_the_panel_shows_the_snapshot_s_figures_as_given():
    stream = io.StringIO()
    SessionMonitor(stream=stream, is_terminal=True).update(SNAPSHOT)
    text = stream.getvalue()
    for figure in ("0.777", "2026-03-21T07:07:30-07:00", "+1.23", "25.0%", "written 900",
                   "recorder-dropped 2", "[advance]",
                   "population 139, all rendered   admitted 2   released 1   admitted in all 4,000"):
        assert figure in text, figure
    assert "4.5" not in text


def test_off_a_terminal_it_logs_lines_and_writes_no_escape_codes(caplog):
    stream = io.StringIO()
    with caplog.at_level(logging.INFO):
        SessionMonitor(stream=stream, is_terminal=False).update(SNAPSHOT)
    assert stream.getvalue() == ""
    assert any("t=25650.0" in record.message and "\x1b" not in record.message
               and "population 139, all rendered" in record.message
               for record in caplog.records)


def test_a_terminal_panel_redraws_in_place():
    stream = io.StringIO()
    clock = iter([0.0, 5.0]).__next__
    monitor = SessionMonitor(stream=stream, is_terminal=True, clock=clock)
    monitor.update(SNAPSHOT)
    monitor.update(SNAPSHOT)
    assert f"\x1b[{len(SessionMonitor.lines(SNAPSHOT))}F" in stream.getvalue()


def test_updates_are_rate_limited(caplog):
    clock = iter([0.0, 1.0, 2.0, 11.0]).__next__
    monitor = SessionMonitor(stream=io.StringIO(), is_terminal=False, clock=clock)
    with caplog.at_level(logging.INFO):
        for _ in range(4):
            monitor.update(SNAPSHOT)
    assert len([r for r in caplog.records if "t=25650.0" in r.message]) == 2


def test_a_loud_condition_is_said_once_at_once(caplog):
    monitor = SessionMonitor(stream=io.StringIO(), is_terminal=False)
    with caplog.at_level(logging.WARNING):
        monitor.loud("recorder_dropped", "channel OVERWATCH-1 has dropped 2")
        monitor.loud("recorder_dropped", "channel OVERWATCH-1 has dropped 3")
    assert [r.levelno for r in caplog.records] == [logging.WARNING]


def test_the_monitor_holds_no_reference_to_the_session():
    monitor = SessionMonitor(stream=io.StringIO(), is_terminal=False)
    assert not any(isinstance(value, FakeSession | FakeRecorder)
                   for value in vars(monitor).values())
