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
    assert "teleporting: disabled (time-to-teleport '-1')" in text
    assert "admission passes in the window: 0; most population" in text


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


def test_a_capture_listed_without_its_frame_s_render_set_is_a_gate_not_met(layout):
    report, session, recorder = closeout(layout)
    session.Advance()
    recorder.RenderSetUnpaired = 3
    snapshot = report.snapshot()
    assert snapshot["channels"][0]["render_set_paired"] == recorder.RenderSetPaired > 0
    unpaired = gate(report.gates(snapshot, 0), "capture.render_set_unpaired[OVERWATCH-1]")
    assert (unpaired["observed"], unpaired["threshold"], unpaired["met"]) == (3, 0, False)
    assert "render set unpaired 3" in RunCloseoutReport.render(snapshot, report.gates(snapshot, 0))


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
    for gate_id in ("capture.captured_minus_written", "render_accounting.rendered_fraction",
                    "radiometry.profile_digest_present", "supervision.manifest_closing_record"):
        record = gate(gates, gate_id)
        assert record["status"] == "skipped" and record["met"] is None and record["skip_reason"]


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
