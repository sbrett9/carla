"""run_capture builds the capture its effective configuration describes, and nothing else.

Plan 12 §3.8 and §6.3: a run binds a compiled scenario and its world package, takes the clock through
the co-simulation session with the declared pacing and illumination, and records the declared
channels. These tests drive `CaptureSession` against stand-ins for the client, the world, the session
and the recorders (`CaptureFakes`), and assert what the session was started with, where the cameras
went, when the recorders started and with which identity, and which outcome each way of failing
ends in. No server is contacted.

The values asserted are read from the fixture and the plan, not from the code under test: the
session's region is the render region with y negated (SUMO's frame), its warm-up is the window's
begin less the prewarm, the window opens at its own begin, and a stare camera stands where `StareAim`
puts it.

Every refusal the session raises carries the stage it had reached (03 §11.10), and the run's outcome
is read from that stage alone (12 §6.3). The refusals here are the real `CarlaNet.CoSim` exceptions,
given the stage the session would give them; the setter is the session's own, so a test sets it the
way the session does, by reflection over the property.
"""
from __future__ import annotations

import io
import json
import sys
from collections import namedtuple
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))
sys.path.insert(0, str(_REPO / "CarlaControl" / "test"))

import carlanet  # noqa: E402, F401  -- loads the CarlaNet assemblies the next imports name
from CaptureFakes import FakeCompileLock, FakeServer, FakeTeleporting  # noqa: E402
from CarlaNet.CoSim import (  # noqa: E402
    CoSimSessionRefusedException,
    CoSimSessionStage,
    PopulationAuthorityHeldException,
    PopulationMode,
    SolarAuditFailedException,
    WorldDriveAuthority,
)
from RunCaptureFixture import (  # noqa: E402
    A_STARE,
    AN_ORBIT,
    EPOCH,
    SCENARIO_ID,
    Layout,
    run_document,
)
from System import Enum  # noqa: E402

from carlacontrol.CaptureSession import CaptureSession  # noqa: E402
from carlacontrol.ChannelDescription import ChannelDescription  # noqa: E402
from carlacontrol.RunConfigurationValidator import RunConfigurationValidator  # noqa: E402
from carlacontrol.RunResult import RunResult  # noqa: E402
from carlacontrol.RunTerminationSequence import RunTerminationSequence  # noqa: E402
from carlacontrol.SessionMonitor import SessionMonitor  # noqa: E402
from carlacontrol.SiteProfile import SiteProfile  # noqa: E402
from carlacontrol.StareAim import StareAim  # noqa: E402

Usage = namedtuple("Usage", "total used free")
SESSION_ID = "cap-test"


@pytest.fixture
def layout(tmp_path: Path) -> Layout:
    return Layout(tmp_path)


@pytest.fixture
def server() -> FakeServer:
    return FakeServer()


def build(layout: Layout, server: FakeServer, document: dict | None = None, overrides=(), *,
          answer=lambda _prompt: "no", terminal=False, free=10**13, termination=None,
          profile_path=None) -> CaptureSession:
    """A run ready to launch against the stand-ins, not yet launched."""
    run_path = layout.root / "fixture.run.json"
    run_path.write_text(json.dumps(document if document is not None else run_document()),
                        encoding="utf-8")
    site = SiteProfile.discover(layout.root, profile_path or layout.write_profile(), environ={})
    session = CaptureSession(
        site, run_path, [(text, f"--set {text}") for text in overrides],
        client_factory=server.client,
        validator=RunConfigurationValidator(disk_usage=lambda _p: Usage(free, 0, free)),
        termination=termination or RunTerminationSequence(step_timeout_s=5.0),
        monitor=SessionMonitor(stream=io.StringIO(), is_terminal=False),
        answer=answer, stdin_is_terminal=lambda: terminal, session_id=SESSION_ID)
    server.result_path = layout.runs_root / SESSION_ID / "run.result.json"
    return session


def capture(layout: Layout, server: FakeServer, document: dict | None = None, overrides=(), *,
            answer=lambda _prompt: "no", terminal=False, free=10**13, validate_only=False,
            termination=None, profile_path=None):
    session = build(layout, server, document, overrides, answer=answer, terminal=terminal,
                    free=free, termination=termination, profile_path=profile_path)
    return session, session.run(validate_only=validate_only)


def pose_of(transform) -> tuple[float, ...]:
    location, rotation = transform.location, transform.rotation
    return location.x, location.y, location.z, rotation.pitch, rotation.yaw


def started_with(server: FakeServer) -> dict:
    [event] = server.events.of("start_sumo_drive")
    return {"scenario": event[1], "world_package": event[2], "catalogue": event[3], **event[4]}


def staged(refusal, stage: str):
    """A session refusal carrying the stage the session would give it as it leaves."""
    refusal.GetType().GetProperty("Stage").SetValue(refusal, getattr(CoSimSessionStage, stage))
    return refusal


def refusal_at(stage: str, message: str = "the session refused"):
    return CoSimSessionRefusedException(getattr(CoSimSessionStage, stage), message, None)


# -- the session is started with what the configuration resolved ---------------------------------

def test_a_run_to_the_window_s_end_finishes(layout, server):
    session, result = capture(layout, server)
    assert (result.outcome, result.closed_by, result.exit_status) == \
        ("run_finished", "window_end", 0)
    assert server.session.RenderedTimeSeconds == pytest.approx(27000.0)


def test_the_session_is_started_from_the_compiled_package_and_the_resolved_fields(layout, server):
    capture(layout, server)
    started = started_with(server)
    assert Path(started["scenario"]) == layout.lock.parent / f"{SCENARIO_ID}.sumocfg"
    assert Path(started["world_package"]) == layout.world_package.resolve()
    assert (started["fixed_delta"], started["record_hz"]) == (0.05, 2.0)
    assert started["warm_up_to"] == 25200.0 - 300.0
    assert started["window_opens_at"] == 25200.0
    assert started["epoch"] == EPOCH
    assert started["illumination"]["policy"] == "freeze_at_window_start"
    assert started["real_time_factor"] == 0.0
    assert (started["road_layer_visible"], started["signal_layer_visible"]) == (False, False)
    assert started["sumo_home"] == str(layout.root / "sumo")


def test_every_run_writes_the_world_truth_track_beside_its_channels(layout, server):
    # The record of what the world contained, which a base rate is taken over: every capture run
    # writes one, at every SUMO frame inside the window, under the capture directory's truth folder.
    _, result = capture(layout, server)
    assert result.outcome == "run_finished"
    started = started_with(server)
    track = Path(result.produced["capture_directory"]) / "truth" / "world_truth_track.csv"
    assert Path(started["world_truth_track"]) == track
    assert started.get("world_truth_track_interval_s") is None
    assert Path(result.produced["world_truth_track"]) == track


def test_every_run_writes_its_manifest_beside_the_track_and_closes_it_before_its_gates(layout,
                                                                                       server):
    # The run manifest's rows from the run's opening to its terminal row, which this tool writes, with
    # its reason, before the closing snapshot reads the gate on it.
    _, result = capture(layout, server)
    assert (result.outcome, result.closed_by) == ("run_finished", "window_end")
    started = started_with(server)
    capture_directory = Path(result.produced["capture_directory"])
    manifest = capture_directory / "truth" / "manifest.jsonl"
    assert Path(started["run_manifest"]) == manifest
    assert Path(result.produced["run_manifest"]) == manifest

    header = started["run_manifest_header"]
    assert (header["run_id"], header["session_id"], header["scenario_id"]) == \
        (SESSION_ID, SESSION_ID, SCENARIO_ID)
    assert header["window"]["begin_s"] == 25200.0
    assert header["channels"] == [{"channel": 0, "sensor_id": A_STARE["sensor_id"], "pattern": "stare"}]
    assert Path(header["world_truth_track"]) == capture_directory / "truth" / "world_truth_track.csv"

    rows = [json.loads(line) for line in manifest.read_text(encoding="utf-8").splitlines()]
    assert [row["row"] for row in rows] == ["manifest_opened", "sensor_placed", "manifest_closed"]
    camera = next(actor for actor in server.actors if actor.type_id == "sensor.camera.rgb")
    assert (rows[1]["sensor_id"], rows[1]["camera_actor_id"]) == (A_STARE["sensor_id"], camera.id)
    assert rows[2]["caller_reason"] == "window_end"

    closing = next(g for g in result.produced["gates"]
                   if g["id"] == "supervision.manifest_closing_record")
    assert (closing["status"], closing["observed"], closing["met"]) == ("evaluated", True, True)


def test_every_admission_pass_is_asked_for_and_poses_only_where_a_stare_aims_at_traffic(
        layout, server):
    capture(layout, server)
    started = started_with(server)
    assert callable(started["on_admission_pass"])
    assert started["on_pose"] is None


def test_by_default_the_session_is_handed_no_limit_and_follows_no_camera(layout, server):
    # Every vehicle SUMO has is rendered unless an optional limit is chosen, so by default the session
    # is given every vehicle, no region and no cap, and no camera to follow -- in a stare's run and in
    # a run with an orbit flying.
    document = run_document()
    document["capture"]["channels"] = [A_STARE, dict(AN_ORBIT, sensor_id="ORBIT-2")]
    _, result = capture(layout, server, document, ["occlusion.enabled=false"])
    assert result.outcome == "run_finished"
    started = started_with(server)
    assert started["render_set"] == "all"
    assert started["capacity"] is None
    assert not {"region_centre", "region_radius_m"} & set(started)
    assert server.events.of("add_camera") == []
    assert result.produced["session"]["render_set"] == "every vehicle SUMO has"
    # Each camera still leaves the world at the end.
    cameras = [actor for actor in server.actors if actor.type_id == "sensor.camera.rgb"]
    assert len(cameras) == 2 and all(camera.destroyed for camera in cameras)


def test_an_optional_limit_reaches_the_session_with_its_region_in_sumo_s_frame(layout, server):
    _, result = capture(layout, server, overrides=[
        "capture.render_set=circle", 'capture.render_region={"x_m": 120, "y_m": -340, "radius_m": 300}',
        "capture.render_cap=50", "capture.render_hysteresis_m=25"])
    assert result.outcome == "run_finished"
    started = started_with(server)
    assert started["render_set"] == "circle"
    # CARLA's y runs south and SUMO's north.
    assert started["region_centre"] == (120.0, 340.0)
    assert (started["region_radius_m"], started["region_hysteresis_m"], started["capacity"]) == \
        (300.0, 25.0, 50)
    # The run's record says plainly that vehicles outside the limit are not in CARLA.
    assert "is not in CARLA" in result.produced["session"]["render_set"]
    assert result.launch_echo["render"]["limited"] is True
    assert "is not in CARLA" in result.launch_echo["render"]["left_out"]


def test_under_the_cameras_every_channel_camera_is_followed_and_let_go_before_it_is_destroyed(
        layout, server):
    document = run_document()
    document["capture"]["channels"] = [A_STARE, dict(AN_ORBIT, sensor_id="ORBIT-2")]
    _, result = capture(layout, server, document, ["occlusion.enabled=false",
                                                   "capture.render_set=cameras"])
    assert result.outcome == "run_finished"
    assert started_with(server)["render_set"] == "cameras"
    cameras = [actor for actor in server.actors if actor.type_id == "sensor.camera.rgb"]
    assert [event[1] for event in server.events.of("add_camera")] == [camera.id for camera in cameras]
    names = server.events.names()
    for camera in cameras:
        removed = server.events.log.index(("remove_camera", camera.id))
        destroyed = next(index for index, event in enumerate(server.events.log)
                         if event[0] == "destroy" and event[2] == camera.id)
        assert removed < destroyed
    assert server.session.Cameras == []
    assert names.index("dispose") > max(index for index, name in enumerate(names)
                                        if name == "remove_camera")


def test_under_the_cameras_a_depth_camera_is_never_followed(layout, server):
    _, result = capture(layout, server, overrides=["capture.render_set=cameras"])
    assert result.outcome == "run_finished"
    rgb = [actor.id for actor in server.actors if actor.type_id == "sensor.camera.rgb"]
    assert [event[1] for event in server.events.of("add_camera")] == rgb


def test_a_body_ceiling_from_before_is_refused_by_name_and_an_unusable_limit_offline(layout, server):
    # The pool's own ceiling is not an option: a run configuration naming it is refused as naming a
    # field the schema does not have, rather than run as though it meant something.
    _, result = capture(layout, server, overrides=["capture.render_cap_hard=192"])
    assert result.outcome == "usage_error"
    assert "capture.render_cap_hard" in result.refusals[0]["message"]
    # A circle with no region to draw is refused before anything is started.
    _, result = capture(layout, server, overrides=["capture.render_set=circle"])
    assert result.outcome == "refused_offline"
    assert result.refusals[0]["check"] == 53
    assert server.events.of("start_sumo_drive") == []


def test_collision_detail_is_off_unless_set_and_only_binds_a_printer(layout, server):
    # Off by default: the session's report prints the count, and nothing is called per collision.
    _, result = capture(layout, server)
    assert result.outcome == "run_finished"
    started = started_with(server)
    assert started["collision_detail"] is False
    assert started["on_collision"] is None


def test_collision_detail_on_reaches_the_session_and_prints_each_collision(layout, server):
    session, result = capture(layout, server, overrides=["collision_detail=on"])
    assert result.outcome == "run_finished"
    started = started_with(server)
    assert started["collision_detail"] is True
    assert started["on_collision"] == session._print_collision


def test_no_draw_distance_is_handed_to_the_session_unless_one_is_set(layout, server):
    # An optional performance control, off by default: every body drawn at any range.
    _, result = capture(layout, server)
    assert started_with(server)["draw_distance_m"] is None
    assert result.produced["session"]["draw_distance"] == "none: every body drawn at any range"


def test_a_draw_distance_reaches_the_session_and_the_run_s_record(layout, server):
    _, result = capture(layout, server, overrides=["capture.draw_distance_m=750"])
    assert result.outcome == "run_finished"
    assert started_with(server)["draw_distance_m"] == 750
    session = result.produced["session"]
    assert session["draw_distance"].startswith("750 m, rendering only")
    render = session["last_snapshot"]["render"]
    assert (render["draw_distance_m"], render["draw_distance_in_force_m"],
            render["draw_distance_refused"]) == (750, 750, None)
    # The recorders are given the session's render set, whose frames say what each was drawn
    # under, and not the distance again.
    assert all("draw_distance_m" not in event[4] for event in server.events.of("start_recording"))


def test_wall_clock_pacing_is_handed_to_the_session(layout, server):
    capture(layout, server, overrides=["pacing.mode=wall_clock", "pacing.real_time_factor=1.0",
                                       "pacing.min_achieved_factor=0.8"])
    started = started_with(server)
    assert started["real_time_factor"] == 1.0 and started["pacing_window_s"] == 5.0


def test_an_overridden_policy_reaches_the_session(layout, server):
    capture(layout, server, overrides=["solar.policy=advance"])
    assert started_with(server)["illumination"] == {
        "illumination_version": 1, "policy": "advance", "rate_sun_s_per_sim_s": 1.0,
        "note": "one lighting condition per window"}


# -- cameras and recorders --------------------------------------------------------------------------

def test_a_stare_camera_is_placed_where_its_description_aims_it(layout, server):
    capture(layout, server)
    rgb = next(a for a in server.actors if a.type_id == "sensor.camera.rgb")
    aim = StareAim.from_channel(ChannelDescription(**A_STARE))
    location, rotation = rgb.transform.location, rgb.transform.rotation
    assert (location.x, location.y, location.z) == pytest.approx((aim.x_m, aim.y_m, aim.z_m))
    assert (rotation.pitch, rotation.yaw) == pytest.approx((aim.pitch_deg, aim.yaw_deg))
    assert rgb.attributes == {"image_size_x": "1280", "image_size_y": "720", "fov": "90.0",
                              "sensor_tick": "0.5", "post_process_profile": "Default",
                              "role_name": "OVERWATCH-1"}


def test_a_stare_measuring_occlusion_has_a_depth_camera_at_its_pose(layout, server):
    capture(layout, server)
    rgb, depth = server.actors
    assert depth.type_id == "sensor.camera.depth"
    assert depth.attributes["max_range"] == "20000.0"
    assert depth.transform.location.x == rgb.transform.location.x
    [start] = server.events.of("start_recording")
    assert start[4]["depth_camera"] is depth
    # Attached to the camera, rigidly and at the camera's own pose, so one move carries both.
    assert server.events.of("spawn")[1] == ("spawn", "sensor.camera.depth", depth.id, rgb.id)
    assert depth.attachment_type == carlanet.AttachmentType.Rigid
    assert pose_of(depth.spawned_at) == pose_of(rgb.spawned_at)
    # And it leaves the world before the camera it is attached to.
    destroyed = [event[2] for event in server.events.of("destroy")]
    assert destroyed.index(depth.id) < destroyed.index(rgb.id)


def test_recording_starts_when_the_window_opens_not_during_the_prewarm(layout, server):
    capture(layout, server)
    [start] = server.events.of("start_recording")
    assert start[5] == 25200.0
    advances_before = server.events.index("start_recording")
    assert len([e for e in server.events.log[:advances_before] if e[0] == "advance"]) == 300


def test_each_channel_records_through_its_own_handle_with_the_session_s_identity(layout, server):
    document = run_document()
    second = dict(A_STARE, sensor_id="OVERWATCH-2", stare_bearing_deg=90.0)
    document["capture"]["channels"] = [A_STARE, second]
    capture(layout, server, document)
    starts = server.events.of("start_recording")
    assert len(starts) == 2
    assert {start[2] for start in starts} == {layout.capture_root / SESSION_ID / "OVERWATCH-1",
                                              layout.capture_root / SESSION_ID / "OVERWATCH-2"}
    for start in starts:
        assert start[4]["run_id"] == SESSION_ID
        assert start[4]["illumination"] is server.session.Illumination
        # And the session's render set, so each sidecar lists its own frame's rendered vehicles by
        # SUMO id and none of the bodies parked between loans.
        assert start[4]["render_set"] is server.session.RenderSet
        assert "scenario_id" not in start[4] and "seed" not in start[4]
    # Each records under its camera's name, which its platform track carries as its callsign.
    assert {start[4]["camera_name"] for start in starts} == {"OVERWATCH-1", "OVERWATCH-2"}
    assert all("platform_callsign" not in start[4] for start in starts)
    # 1,800 s at 2 Hz on each: neither recorder was stopped by the other starting.
    assert [recorder.Saved for recorder in server.recorders] == [3600, 3600]


def test_each_channel_s_camera_is_spawned_under_its_sensor_id_for_every_client_to_read(
        layout, server):
    document = run_document()
    document["capture"]["channels"] = [A_STARE, dict(A_STARE, sensor_id="OVERWATCH-2",
                                                     stare_bearing_deg=90.0)]
    capture(layout, server, document)
    rgb = [actor for actor in server.actors if actor.type_id == "sensor.camera.rgb"]
    assert [camera.attributes["role_name"] for camera in rgb] == ["OVERWATCH-1", "OVERWATCH-2"]
    # The depth camera rides its channel's camera and holds no name of its own.
    assert all("role_name" not in actor.attributes for actor in server.actors
               if actor.type_id == "sensor.camera.depth")


def test_a_single_channel_with_no_sensor_id_is_named_after_its_camera(layout, server):
    document = run_document()
    unnamed = dict(A_STARE)
    unnamed.pop("sensor_id")
    document["capture"]["channels"] = [unnamed]
    session, result = capture(layout, server, document)
    assert result.outcome == "run_finished"
    [camera] = [actor for actor in server.actors if actor.type_id == "sensor.camera.rgb"]
    name = f"CARLA-SENSOR-{camera.id}"
    [start] = server.events.of("start_recording")
    assert start[2] == layout.capture_root / SESSION_ID / name
    assert start[4]["camera_name"] == name
    assert [rig.sensor_id for rig in session.channels] == [name]
    assert session.channels[0].aim_record["sensor_id"] == name
    # The manifest lists the channel as declared, with no name, and names its camera as it is placed.
    assert started_with(server)["run_manifest_header"]["channels"][0]["sensor_id"] is None
    rows = [json.loads(line) for line in
            Path(result.produced["run_manifest"]).read_text(encoding="utf-8").splitlines()]
    placed = next(row for row in rows if row["row"] == "sensor_placed")
    assert (placed["sensor_id"], placed["camera_actor_id"]) == (name, camera.id)


def test_a_sensor_id_another_client_s_camera_holds_refuses_at_preroll(layout, server):
    # Another client's camera in the same world was spawned under the name, in another case.
    server.cameras_of_other_clients = {7: "overwatch-1"}
    _, result = capture(layout, server)
    assert (result.outcome, result.closed_by) == ("refused_preroll", "aborted_at_preroll")
    assert "channel OVERWATCH-1: its camera could not be placed" in result.detail
    assert "already held in this world by camera 7" in result.detail
    assert server.events.of("start_recording") == []


def test_an_orbit_measuring_occlusion_has_a_depth_camera_attached_to_its_camera(layout, server):
    # Occlusion is on by default, and an orbit gets it as a stare does: a depth camera spawned
    # attached to the channel's camera, and the recorder started with it.
    document = run_document()
    document["capture"]["channels"] = [AN_ORBIT]
    session, result = capture(layout, server, document)
    assert result.outcome == "run_finished"
    rgb, depth = server.actors
    assert (rgb.type_id, depth.type_id) == ("sensor.camera.rgb", "sensor.camera.depth")
    assert server.events.of("spawn")[1] == ("spawn", "sensor.camera.depth", depth.id, rgb.id)
    assert depth.attachment_type == carlanet.AttachmentType.Rigid
    assert depth.attributes["max_range"] == "20000.0"
    assert depth.attributes["fov"] == rgb.attributes["fov"]
    assert pose_of(depth.spawned_at) == pose_of(rgb.spawned_at)
    [start] = server.events.of("start_recording")
    assert start[4]["depth_camera"] is depth
    assert session.channels[0].orbit is not None
    assert all(actor.destroyed for actor in server.actors)


def test_an_orbit_s_depth_camera_takes_every_pose_its_camera_takes(layout, server):
    # The orbit is advanced here by hand, a second's worth of angle on every step of the window, so
    # that the moves checked do not depend on its wall-clock thread's timing; the thread's own
    # moves come on top. After each move the depth camera stands where the camera stands, and no
    # call ever moves the depth camera itself.
    document = run_document()
    document["capture"]["channels"] = [AN_ORBIT]
    session = build(layout, server, document)
    agreed: list[tuple[float, ...]] = []

    def advanced(_fake_session) -> None:
        [rig] = session.channels
        if rig.depth is None or not rig.orbit.orbit_enabled:
            return
        rig.orbit.update_orbit(1.0)
        assert pose_of(rig.depth.transform) == pose_of(rig.camera.transform)
        agreed.append(pose_of(rig.camera.transform))

    server.on_advance = advanced
    result = session.run()
    assert result.outcome == "run_finished"
    rgb, depth = server.actors
    assert len(agreed) == 1800 and len(set(agreed)) > 1, "the orbit never swept"
    moved = [event[1] for event in server.events.of("move")]
    assert moved and set(moved) == {rgb.id}
    # Every frame the client holds places both cameras at one pose, which is what the recorder's
    # pose check compares a depth capture against.
    held = [snapshot for snapshot in server.snapshots.values()
            if rgb.id in snapshot and depth.id in snapshot]
    assert held and all(pose_of(s[rgb.id]) == pose_of(s[depth.id]) for s in held)


def test_with_occlusion_off_an_orbit_carries_no_depth_camera(layout, server):
    document = run_document()
    document["capture"]["channels"] = [AN_ORBIT]
    session, result = capture(layout, server, document, ["occlusion.enabled=false"])
    assert result.outcome == "run_finished"
    assert [a.type_id for a in server.actors] == ["sensor.camera.rgb"]
    [start] = server.events.of("start_recording")
    assert start[4]["depth_camera"] is None
    assert session.channels[0].orbit is not None


def test_a_depth_camera_the_server_will_not_attach_refuses_at_preroll(layout, server):
    server.spawn_raises["sensor.camera.depth"] = RuntimeError(
        "unable to attach actor: parent actor not found")
    document = run_document()
    document["capture"]["channels"] = [AN_ORBIT]
    _, result = capture(layout, server, document)
    assert (result.outcome, result.closed_by) == ("refused_preroll", "aborted_at_preroll")
    assert "channel ORBIT-1: its camera could not be placed" in result.detail
    assert "parent actor not found" in result.detail
    assert server.events.of("start_recording") == []
    # The camera that was placed leaves the world with the refusal.
    [rgb] = server.actors
    assert rgb.type_id == "sensor.camera.rgb" and rgb.destroyed


# -- how a run ends ---------------------------------------------------------------------------------

def test_a_window_with_no_end_runs_to_the_scenario_s_end(layout):
    server = FakeServer(scenario_end_s=86400.0)
    session, result = capture(layout, server, overrides=["capture.window=85000:"])
    assert (result.outcome, result.closed_by) == ("run_finished", "scenario_end")
    assert result.produced["window"]["end_reached_s"] == 86400.0


def test_an_open_window_reaching_the_declared_end_with_sumo_still_running_is_the_scenario_s_end(
        layout):
    server = FakeServer(scenario_end_s=90000.0)
    _, result = capture(layout, server, overrides=["capture.window=85000:"])
    assert (result.outcome, result.closed_by) == ("run_finished", "scenario_end")
    assert server.session.RenderedTimeSeconds == pytest.approx(86400.0)


def test_a_scenario_that_ends_before_the_window_refuses_at_preroll(layout):
    server = FakeServer(scenario_end_s=25000.0)
    _, result = capture(layout, server)
    assert (result.outcome, result.closed_by) == ("refused_preroll", "aborted_at_preroll")
    assert server.session.disposed


def test_a_prewarm_that_could_not_hold_the_floor_refuses_the_window(layout, server):
    server.achieved_factor = 0.31
    _, result = capture(layout, server, overrides=["pacing.mode=wall_clock",
                                                   "pacing.min_achieved_factor=0.8"])
    assert result.outcome == "refused_preroll"
    assert "0.31" in result.detail
    assert server.events.of("start_recording") == []
    assert server.session.disposed


def test_a_held_population_lease_refuses_naming_the_holder(layout, server):
    authority = WorldDriveAuthority.ForWorld("test://capture-session")
    lease = authority.Acquire(PopulationMode.TrafficManagerAmbient, "someone's traffic manager")
    try:
        authority.Acquire(PopulationMode.SumoDrivenPlayback, "run_capture")
    except PopulationAuthorityHeldException as held:
        server.start_raises = held
    finally:
        lease.Dispose()
    _, result = capture(layout, server)
    assert (result.outcome, result.exit_status) == ("refused_authority", 4)
    assert result.authority_holder == "someone's traffic manager"


def test_a_sun_that_did_not_bind_refuses_at_preroll(layout, server):
    server.start_raises = staged(
        SolarAuditFailedException("requested 07:00, world reports 12:00", None), "PreRoll")
    _, result = capture(layout, server)
    assert (result.outcome, result.closed_by) == ("refused_preroll", "aborted_at_preroll")


# The session's stages, and the outcome each is the run's (12 §6.3). The refusal's type plays no part:
# a solar audit that failed as the window opened, raised as its start's PreRoll, and the same class of
# refusal before anything was written are told apart by the stage alone.
START_STAGES = {
    "Validation": ("refused_server", None, 3),
    "Launch": ("refused_server", None, 3),
    "PreRoll": ("refused_preroll", "aborted_at_preroll", 5),
}


@pytest.mark.parametrize("stage", list(START_STAGES))
def test_a_start_refusal_is_concluded_by_its_stage(layout, server, stage):
    server.start_raises = refusal_at(stage, "the package is not the loaded world")
    _, result = capture(layout, server)
    outcome, closed_by, status = START_STAGES[stage]
    assert (result.outcome, result.closed_by, result.exit_status) == (outcome, closed_by, status)
    assert result.detail == f"the session refused at {stage}: the package is not the loaded world"
    assert server.events.of("spawn") == []


def test_the_same_exception_type_at_another_stage_is_another_outcome(layout):
    early = FakeServer()
    early.start_raises = staged(SolarAuditFailedException("a declaration refused", None),
                                "Validation")
    _, result = capture(layout, early)
    assert result.outcome == "refused_server"


def test_a_refusal_on_a_prewarm_tick_refuses_at_preroll(layout, server):
    server.fault_at = (25000.0, refusal_at("PreRoll", "SUMO failed at simulated 25000 s"))
    _, result = capture(layout, server)
    assert (result.outcome, result.closed_by) == ("refused_preroll", "aborted_at_preroll")
    assert "PreRoll" in result.detail and "SUMO failed" in result.detail
    assert server.events.of("start_recording") == []
    assert server.session.disposed and all(actor.destroyed for actor in server.actors)


def test_a_stage_this_tool_does_not_know_is_an_internal_error(layout, server):
    refusal = refusal_at("Validation")
    refusal.GetType().GetProperty("Stage").SetValue(
        refusal, Enum.ToObject(refusal.Stage.GetType(), 9))
    server.start_raises = refusal
    _, result = capture(layout, server)
    assert result.outcome == "internal_error"


def test_missing_co_simulation_assemblies_are_refused_server(layout, server):
    server.start_returns_none = True
    _, result = capture(layout, server)
    assert result.outcome == "refused_server" and "CarlaNet.CoSim" in result.detail


def test_a_server_check_refusing_starts_no_session(layout, server):
    server.sun = None
    _, result = capture(layout, server)
    assert result.outcome == "refused_server"
    assert server.events.of("start_sumo_drive") == []
    assert result.refusals[0]["check"] == 23


def test_an_offline_refusal_contacts_no_server(layout, server):
    _, result = capture(layout, server, overrides=["capture.world_delta_s=0.03"])
    assert (result.outcome, result.exit_status) == ("refused_offline", 2)
    assert server.clients == []
    assert result.refusals[0]["check"] == 9


def test_an_unreachable_server_is_refused_server(layout, server):
    server.unreachable = True
    _, result = capture(layout, server)
    assert result.outcome == "refused_server" and "127.0.0.1:2000" in result.detail


def test_a_malformed_override_is_a_usage_error_with_a_result(layout, server):
    _, result = capture(layout, server, overrides=["capture.prewarm_ss=3"])
    assert (result.outcome, result.exit_status) == ("usage_error", 1)
    written = RunResult.read(layout.runs_root / SESSION_ID / "run.result.json")
    assert written["outcome"] == "usage_error"
    assert "prewarm_ss" in written["refusals"][0]["message"]
    assert (layout.runs_root / SESSION_ID / "run.resolution.json").is_file()


def test_a_world_that_stops_ticking_mid_window_stops_the_run(layout, server):
    server.fault_at = (26000.0, refusal_at("Window", "the world produced no frame"))
    _, result = capture(layout, server)
    assert result.outcome == "run_stopped"
    assert result.closed_by == "fault:CoSimSessionRefusedException"
    assert result.detail == "the session refused at Window: the world produced no frame"
    assert server.session.disposed
    # Its manifest is closed all the same, saying why, before the gate reads it.
    assert server.session.RunManifest.reasons == ["fault:CoSimSessionRefusedException"]
    closing = next(g for g in result.produced["gates"]
                   if g["id"] == "supervision.manifest_closing_record")
    assert closing["met"] is True


def test_an_unexpected_fault_is_an_internal_error_and_still_cleans_up(layout, server):
    server.fault_at = (26000.0, RuntimeError("boom"))
    _, result = capture(layout, server)
    assert (result.outcome, result.exit_status) == ("internal_error", 7)
    assert server.session.disposed and all(a.destroyed for a in server.actors)


def test_a_failure_that_is_not_a_session_refusal_is_an_internal_error(layout, server):
    # A dropped CARLA connection is not one of the session's refusals and carries no stage.
    server.fault_at = (25000.0, ConnectionError("the server closed the connection"))
    _, result = capture(layout, server)
    assert result.outcome == "internal_error"


# -- what the session checked before SUMO started ----------------------------------------------------

def test_the_compile_lock_and_teleporting_reach_the_result_and_the_closeout(layout, server, caplog):
    caplog.set_level("INFO")
    _, result = capture(layout, server)
    session = result.produced["session"]
    assert session["compile_lock"] == {
        "compiled": True, "lock_path": server.session.Report.CompileLock.ExpectedLockPath,
        "statement": str(server.session.Report.CompileLock),
        "routed_by": server.session.Report.CompileLock.RoutedByText,
        "compiled_for": server.session.Report.CompileLock.WorldText}
    assert session["teleporting"] == {"enabled": False, "accepted": False, "seconds": -1.0,
                                      "declared": "-1", "statement": "disabled (time-to-teleport '-1')"}
    text = caplog.text
    assert "compile lock: gardnerville_fixture, compiled by" in text
    assert "routed by duarouter 1.27.0" in text
    assert "teleporting: disabled (time-to-teleport '-1')" in text


def test_an_uncompiled_scenario_and_an_accepted_teleport_are_said_louder(layout, server, caplog):
    server.compile_lock = FakeCompileLock(compiled=False)
    server.teleporting = FakeTeleporting(seconds=300.0, accepted=True)
    _, result = capture(layout, server)
    warnings = [r.message for r in caplog.records if r.levelname == "WARNING"]
    assert any(m.startswith("compile lock: none at") for m in warnings)
    assert any(m.startswith("teleporting: ENABLED after 300 s") for m in warnings)
    assert result.produced["session"]["compile_lock"]["compiled"] is False
    assert result.produced["session"]["teleporting"]["accepted"] is True


# -- the echo and the artifacts written before the window --------------------------------------------

def test_validate_only_stops_after_the_offline_checks_and_writes_the_lock(layout, server):
    _, result = capture(layout, server, validate_only=True)
    assert result is None
    assert server.clients == []
    lock = json.loads((layout.runs_root / SESSION_ID / "run.lock.json").read_text("utf-8"))
    assert lock["inputs"]["scenario_id"] == SCENARIO_ID
    assert not (layout.runs_root / SESSION_ID / "run.result.json").exists()


def test_an_attended_warning_blocks_until_the_operator_proceeds(layout, server):
    asked = []
    _, result = capture(layout, server, overrides=["solar.policy=ignore"], terminal=True,
                        answer=lambda prompt: asked.append(prompt) or "proceed")
    assert len(asked) == 1 and "lighting_honours_no_epoch" in asked[0]
    assert result.outcome == "run_finished"
    [warning] = result.warnings
    assert warning["adjudicated_by"] == "the operator at the terminal"


def test_an_attended_warning_the_operator_declines_is_refused(layout, server):
    _, result = capture(layout, server, overrides=["solar.policy=ignore"], terminal=True,
                        answer=lambda _prompt: "no")
    assert result.outcome == "refused_offline" and server.clients == []


def test_an_attended_warning_with_no_terminal_to_ask_is_refused(layout, server):
    _, result = capture(layout, server, overrides=["solar.policy=ignore"], terminal=False)
    assert result.outcome == "refused_offline"
    assert "no terminal to ask" in result.refusals[-1]["message"]


def test_a_warning_adjudicated_in_writing_is_recorded_with_its_source(layout, server):
    _, result = capture(layout, server, overrides=[
        "solar.policy=ignore", 'on_warning={"lighting_honours_no_epoch": "proceed"}'])
    assert result.outcome == "run_finished"
    assert result.warnings[0]["adjudicated_by"].startswith("--set on_warning=")


def test_the_replayable_configuration_reproduces_the_run_s_digest(layout, server):
    first_session, first = capture(layout, server, overrides=["occlusion.samples=32"])
    replay = json.loads((layout.runs_root / SESSION_ID / "run.effective.json").read_text("utf-8"))
    second_session, second = capture(layout, FakeServer(), replay)
    assert second.effective_configuration_digest == first.effective_configuration_digest
    assert second.outcome == "run_finished"
