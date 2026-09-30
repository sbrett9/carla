"""run_capture writes no capture before the capture camera's tiles are in and its picture has settled.

Plan 03 §9.5.1 and plan 12 §6.2 check 50: readiness is two conditions from two witnesses. The server
says the tiles are in -- the camera published on the last tick, every visible tileset at load progress
100, no failed tile in view -- and the camera's own frames say the picture has settled: a frame within
0.5 grey levels of the frame ten before it in its worst 80-pixel block, counting only frames rendered
once the tiles were in. The wait lives in the prewarm and ticks with it; a ceiling -- 90 s of wall
clock for the tiles, 120 of the camera's frames for the picture -- or a view not ready as the window
opens refuses at pre-roll, naming the channel and the witness, and the window's first frame is not
moved.

The stand-in server numbers its frames from 1000 and the camera is spawned on that frame; with a
one-second SUMO step of twenty 0.05 s ticks and a 2 Hz capture, the camera renders every tenth frame,
the prewarm's 300 steps end on frame 7000, and the window's first frame is 7001. Every expected frame
below is worked out from those figures, not read back from the code under test. The run's clock is the
stand-in's wall clock, which each tick advances.
"""
from __future__ import annotations

import io
import json
import sys
from collections import namedtuple
from pathlib import Path

import numpy as np
import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))
sys.path.insert(0, str(_REPO / "CarlaControl" / "test"))

from CaptureFakes import FakeServer  # noqa: E402
from RunCaptureFixture import A_STARE, AN_ORBIT, Layout, run_document  # noqa: E402

from carlacontrol.CaptureSession import CaptureSession  # noqa: E402
from carlacontrol.OrbitSensorController import OrbitSensorController  # noqa: E402
from carlacontrol.RunCloseoutReport import RunCloseoutReport  # noqa: E402
from carlacontrol.RunConfigurationValidator import RunConfigurationValidator  # noqa: E402
from carlacontrol.RunTerminationSequence import RunTerminationSequence  # noqa: E402
from carlacontrol.SessionMonitor import SessionMonitor  # noqa: E402
from carlacontrol.SiteProfile import SiteProfile  # noqa: E402
from carlacontrol.ViewReadiness import grey_small, tiles_in, worst_block  # noqa: E402

Usage = namedtuple("Usage", "total used free")
SESSION_ID = "cap-ready"
FIRST_FRAME = 1000          # the frame the camera is spawned on
TICKS_PER_STEP = 20
FRAMES_PER_CAPTURE = 10     # 0.5 s of sensor_tick over 0.05 s ticks
LAST_PREWARM_FRAME = FIRST_FRAME + 300 * TICKS_PER_STEP
FIRST_PREWARM_S = 24900.0
BEGIN_S = 25200.0


@pytest.fixture
def layout(tmp_path: Path) -> Layout:
    return Layout(tmp_path)


def capture(layout: Layout, server: FakeServer, channels=None, overrides=()):
    document = run_document()
    if channels is not None:
        document["capture"]["channels"] = channels
    run_path = layout.root / "fixture.run.json"
    run_path.write_text(json.dumps(document), encoding="utf-8")
    site = SiteProfile.discover(layout.root, layout.write_profile(), environ={})
    session = CaptureSession(
        site, run_path, [(text, f"--set {text}") for text in overrides],
        client_factory=server.client,
        validator=RunConfigurationValidator(disk_usage=lambda _p: Usage(10**13, 0, 10**13)),
        termination=RunTerminationSequence(step_timeout_s=5.0, exit_now=lambda _s: None),
        monitor=SessionMonitor(stream=io.StringIO(), is_terminal=False),
        stdin_is_terminal=lambda: False, session_id=SESSION_ID, clock=server.clock)
    server.result_path = layout.runs_root / SESSION_ID / "run.result.json"
    return session, session.run()


def tiles(published=True, progress=100.0, failed=0) -> dict:
    return {"published": published, "load_progress": progress, "failed_in_view": failed}


def in_from(first_frame: int, until: int | None = None):
    """Tiles in on the frames from `first_frame` on (up to `until`), not yet loaded before."""
    return lambda _camera, frame: tiles(progress=100.0 if frame >= first_frame and (
        until is None or frame < until) else 62.0)


def refused_by_check_50(server: FakeServer, result) -> None:
    assert (result.outcome, result.closed_by, result.exit_status) == \
        ("refused_preroll", "aborted_at_preroll", 5)
    assert {refusal["check"] for refusal in result.refusals} == {50}
    assert server.events.of("start_recording") == []
    assert server.session.disposed and all(actor.destroyed for actor in server.actors)


def the_view(result, index=0) -> dict:
    return result.produced["readiness"]["channels"][index]


# -- the measure -----------------------------------------------------------------------------------

def test_the_picture_is_grey_by_bt601_averaged_over_four_pixels_and_truncated():
    blue = np.zeros((8, 8, 4), np.uint8)
    blue[:, :, 0] = 255
    red = np.zeros((8, 8, 4), np.uint8)
    red[:, :, 2] = 255
    # BGRA: 0.114 of blue, 0.299 of red; each 4 x 4 averaged, truncated to a level.
    assert grey_small(blue.tobytes(), 8, 8).tolist() == [[29, 29], [29, 29]]
    assert grey_small(red.tobytes(), 8, 8).tolist() == [[76, 76], [76, 76]]
    mixed = np.zeros((4, 8, 4), np.uint8)
    mixed[:, :4, 1] = 255
    assert grey_small(mixed.tobytes(), 8, 4).tolist() == [[149, 0]]


def test_the_worst_80_pixel_block_sees_what_a_whole_frame_mean_hides():
    # A 1280 x 720 frame reduced by four: 144 blocks of 20 x 20.
    older = np.full((180, 320), 100, np.uint8)
    newer = older.copy()
    newer[40:60, 100:120] += 5          # one block still refining, at x 400, y 160
    levels, corner = worst_block(newer, older)
    assert levels == pytest.approx(5.0) and corner == (400, 160)
    assert float(np.abs(newer.astype(float) - older).mean()) < 0.5
    spread = older.copy()
    spread[::2, :] += 1                 # half a level everywhere: settled
    assert worst_block(spread, older)[0] == pytest.approx(0.5)


def test_the_tiles_are_in_only_published_loaded_and_whole():
    def answer(published=True, progress=100.0, failed=0):
        return {"frame": 1, "published": published, "tilesets": [
            {"visible": True, "load_progress": progress, "failed_in_view": failed},
            {"visible": False, "load_progress": 0.0, "failed_in_view": 0}]}
    assert tiles_in(answer())
    assert not tiles_in(answer(published=False))
    assert not tiles_in(answer(failed=1))
    assert not tiles_in(answer(progress=99.9))


# -- a view that becomes ready ---------------------------------------------------------------------

def test_recording_starts_on_the_frame_after_the_view_is_ready(layout):
    # Tiles in from frame 6900, the end of the 295th step, so the eleventh frame rendered since is
    # the prewarm's last, 7000: ready exactly as the window opens, recorded from frame 7001.
    server = FakeServer()
    server.tiles_at = in_from(6900)
    _, result = capture(layout, server)
    assert result.outcome == "run_finished"
    view = the_view(result)
    assert view["tiles"]["in_at_frame"] == 6900
    assert view["picture"] == {"settled_at_frame": LAST_PREWARM_FRAME, "frames": 11,
                               "residual_levels": 0.0, "compared_with_frame": 6900,
                               "worst_block_px": [0, 0]}
    assert view["ready_at_window_open"] is True
    [start] = server.events.of("start_recording")
    assert start[5] == BEGIN_S and start[6] == LAST_PREWARM_FRAME


def test_a_view_ready_one_step_too_late_is_refused_rather_than_recorded_late(layout):
    # Tiles in from frame 6920: nine frames by the window's opening, and the window is not moved.
    server = FakeServer()
    server.tiles_at = in_from(6920)
    _, result = capture(layout, server)
    refused_by_check_50(server, result)
    assert "OVERWATCH-1" in result.detail and "had not settled" in result.detail
    assert "9 of its frames arrived since" in result.detail
    assert server.session.RenderedTimeSeconds == BEGIN_S


def test_the_result_records_how_each_view_became_ready(layout):
    server = FakeServer()
    server.tiles_at = in_from(1100)
    _, result = capture(layout, server)
    readiness = result.produced["readiness"]
    assert (readiness["tiles_ceiling_s"], readiness["picture_ceiling_frames"]) == (90.0, 120)
    assert readiness["wait_began"] == {"sim_time_s": FIRST_PREWARM_S, "ticks": 0}
    assert readiness["per_capture"].startswith("not recorded")
    view = the_view(result)
    # Asked after each step: the fifth step ends on frame 1100, 100 ticks and 0.5 s of the
    # stand-in's wall clock into the wait.
    assert view["tiles"] == {"in_at_frame": 1100, "ticks": 100, "wall_s": 0.5}
    assert view["picture"]["settled_at_frame"] == 1200 and view["picture"]["frames"] == 11
    assert view["readiness_asked"]["count"] == 300
    text = RunCloseoutReport.render(result.produced["session"]["last_snapshot"] | {
        "scenario": "s", "window_name": "w", "sim_time_s": None, "window": {},
        "channels": [], "readiness": readiness}, [])
    assert "view OVERWATCH-1: tiles in at frame 1100 after 100 ticks, 0.5 s; picture settled at " \
           "frame 1200 after 11 frames" in text


def test_the_echo_says_the_wait_will_happen(layout):
    server = FakeServer()
    _, result = capture(layout, server)
    readiness = result.launch_echo["readiness"]
    assert readiness["waits"] is True
    assert (readiness["from_s"], readiness["until_s"]) == (FIRST_PREWARM_S, BEGIN_S)
    assert (readiness["tiles_ceiling_s"], readiness["picture_ceiling_frames"]) == (90.0, 120)
    assert "check 50" in readiness["not_ready"] and "not moved" in readiness["not_ready"]


# -- a view that is not ready ----------------------------------------------------------------------

def test_an_unpublished_camera_reading_100_is_not_ready(layout):
    server = FakeServer()
    server.tiles_at = lambda _camera, _frame: tiles(published=False)
    _, result = capture(layout, server)
    refused_by_check_50(server, result)
    assert "not published for this view yet" in result.detail
    assert the_view(result)["state"] == "waiting for tiles"


def test_a_failed_tile_in_view_reading_100_is_not_ready(layout):
    server = FakeServer()
    server.tiles_at = lambda _camera, _frame: tiles(failed=3)
    _, result = capture(layout, server)
    refused_by_check_50(server, result)
    assert "100%, 3 failed in view" in result.detail


def test_the_tiles_ceiling_refuses_naming_the_tiles_witness(layout):
    # 1.25 s of wall clock a step, a sum a float holds exactly: the 72nd step reaches the 90 s
    # ceiling, long before the window opens.
    server = FakeServer()
    server.wall_per_tick = 0.0625
    server.tiles_at = lambda _camera, _frame: tiles(progress=87.0)
    _, result = capture(layout, server)
    refused_by_check_50(server, result)
    assert "photoreal tiles were not in within 90 s of wall clock" in result.detail
    assert "87%" in result.detail and "ion 2275207: progress 87.0" in result.detail
    assert server.session.RenderedTimeSeconds == FIRST_PREWARM_S + 72.0


def test_the_picture_ceiling_refuses_naming_the_picture_witness(layout):
    # Tiles in from the first step, on frame 1020; every frame differs from the one ten before.
    # The 121st frame since, 2220, ends the 61st step.
    server = FakeServer()
    server.picture_at = lambda _camera, frame: (frame // FRAMES_PER_CAPTURE) * 7 % 256
    _, result = capture(layout, server)
    refused_by_check_50(server, result)
    assert "picture did not settle within 120 of the camera's frames" in result.detail
    assert "tiles were in at frame 1020" in result.detail
    assert server.session.RenderedTimeSeconds == FIRST_PREWARM_S + 61.0


def test_a_picture_drifting_a_level_every_few_frames_is_not_settled_until_it_stops(layout):
    # A level every third frame: two frames in three are identical to the one before, but a frame
    # and the one ten before it differ by three levels or more until the drift stops at frame 1960.
    # Ten of the camera's frames later, on frame 2060, the picture has settled.
    server = FakeServer()
    server.picture_at = lambda _camera, frame: 50 + (min(frame, 1960) - FIRST_FRAME) // 30
    _, result = capture(layout, server)
    assert result.outcome == "run_finished"
    assert the_view(result)["picture"]["settled_at_frame"] == 2060


def test_each_channel_waits_for_its_own_view(layout):
    server = FakeServer()
    server.tiles_at = lambda camera, _frame: tiles(published=camera.id != 103)
    second = dict(A_STARE, sensor_id="OVERWATCH-2", stare_bearing_deg=90.0)
    _, result = capture(layout, server, channels=[A_STARE, second])
    refused_by_check_50(server, result)
    [refusal] = result.refusals
    assert refusal["subject"] == "capture.channels[1]" and "OVERWATCH-2" in refusal["message"]
    assert [view["state"] for view in result.produced["readiness"]["channels"]] == \
        ["ready", "waiting for tiles"]


def test_every_channel_is_asked_about_after_every_step(layout):
    server = FakeServer()
    second = dict(A_STARE, sensor_id="OVERWATCH-2", stare_bearing_deg=90.0)
    _, result = capture(layout, server, channels=[A_STARE, second])
    assert result.outcome == "run_finished"
    asked = [event[1] for event in server.events.of("view_readiness")]
    rgbs = [actor.id for actor in server.actors if actor.type_id == "sensor.camera.rgb"]
    assert [asked.count(camera) for camera in rgbs] == [300, 300]
    assert [view["picture"]["settled_at_frame"] for view in
            result.produced["readiness"]["channels"]] == [1120, 1120]


def test_a_view_must_still_be_ready_as_the_window_opens(layout):
    # In from the start, then streaming again over the last two steps: its picture starts again,
    # and the window opens with the tiles out.
    server = FakeServer()
    server.tiles_at = in_from(0, until=LAST_PREWARM_FRAME - TICKS_PER_STEP)
    _, result = capture(layout, server)
    refused_by_check_50(server, result)
    view = the_view(result)
    assert view["state"] == "waiting for tiles" and len(view["relapses"]) == 1
    assert view["relapses"][0] == {"frame": LAST_PREWARM_FRAME - TICKS_PER_STEP,
                                   "was": "ready", "tiles": "62%"}


def test_a_view_whose_tiles_come_back_settles_again_from_their_return(layout):
    server = FakeServer()
    server.tiles_at = lambda _camera, frame: tiles(
        progress=62.0 if 3000 <= frame < 3100 else 100.0)
    _, result = capture(layout, server)
    assert result.outcome == "run_finished"
    view = the_view(result)
    assert len(view["relapses"]) == 1
    assert view["tiles"]["in_at_frame"] == 3100 and view["picture"]["settled_at_frame"] == 3200


def test_a_server_that_cannot_say_is_refused_at_preroll(layout):
    server = FakeServer()
    server.readiness_raises = RuntimeError("get_view_readiness is not bound on this server")
    _, result = capture(layout, server)
    refused_by_check_50(server, result)
    assert "could not say whether camera" in result.detail


# -- the wait ticks, and only the session ticks ------------------------------------------------------

def test_the_wait_is_asked_once_after_every_step_and_never_ticks_the_world(layout):
    server = FakeServer()
    capture(layout, server)
    assert server.events.of("world_tick") == []
    log = server.events.log
    asks = [i for i, event in enumerate(log) if event[0] == "view_readiness"]
    assert len(asks) == 300
    assert asks[0] > log.index(next(e for e in log if e[0] == "advance"))
    previous = None
    for ask in asks:
        if previous is not None:
            between = [e for e in log[previous + 1:ask] if e[0] == "advance"]
            assert len(between) == 1, "the view was asked about without a step between"
        previous = ask
    frames = [log[i][2] for i in asks]
    assert frames == list(range(FIRST_FRAME + TICKS_PER_STEP, LAST_PREWARM_FRAME + 1,
                                TICKS_PER_STEP))


def test_the_cameras_are_listened_to_until_the_recorders_take_them(layout):
    server = FakeServer()
    capture(layout, server)
    names = server.events.names()
    assert names.index("listen") < names.index("advance")
    assert names.index("stop_listening") < names.index("start_recording")


# -- orbits and the prewarm's length --------------------------------------------------------------

def test_an_orbit_holds_the_pose_it_opens_on_until_the_window_opens(layout):
    server = FakeServer()
    session, result = capture(layout, server, channels=[AN_ORBIT],
                              overrides=["occlusion.enabled=false"])
    assert result.outcome == "run_finished"
    [camera] = server.actors
    opening = OrbitSensorController.orbit_transform(50.0, -80.0, 0.0, 200.0, 518.2, 0.0)
    assert (camera.spawned_at.location.x, camera.spawned_at.location.y,
            camera.spawned_at.location.z) == pytest.approx(
        (opening.location.x, opening.location.y, opening.location.z))
    assert camera.spawned_at.rotation.pitch == pytest.approx(opening.rotation.pitch)
    names = server.events.names()
    moves = [i for i, event in enumerate(server.events.log) if event[0] == "move"]
    assert moves and min(moves) > names.index("start_recording"), \
        "the orbit moved before the window opened"
    assert session.channels[0].orbit.orbit_enabled
    held = result.produced["cameras"][0]["held_through_the_pre_roll"]
    assert held["angle_deg"] == 0.0 and held["sweeps_from"] == "the window's opening"


@pytest.mark.parametrize(("prewarm", "outcome"), [("6", "refused_offline"),
                                                  ("7", "run_finished")])
def test_a_prewarm_that_leaves_fewer_than_eleven_frames_to_compare_is_refused_offline(
        layout, prewarm, outcome):
    # Six seconds leave ten frames after the first step at 2 Hz; seven leave twelve.
    server = FakeServer()
    _, result = capture(layout, server, overrides=[f"capture.prewarm_s={prewarm}"])
    assert result.outcome == outcome
    if outcome == "refused_offline":
        assert result.refusals[0]["check"] == 51 and server.clients == []
        assert "at least 6.5 s" in result.refusals[0]["message"]


def test_the_monitor_shows_each_view_until_the_recorders_start():
    view = {"sensor_id": "OVERWATCH-1", "state": "waiting for tiles", "tiles": None,
            "picture": None, "tiles_now": "87%", "last_comparison": None, "relapses": []}
    snapshot = {"scenario": "s", "window_name": "w", "sim_time_s": 24910.0,
                "window": {"end_s": 27000.0}, "illumination": None, "pacing": None,
                "admission": None, "render": None, "channels": [],
                "readiness": {"channels": [view]}}
    assert "view  OVERWATCH-1   waiting for tiles (87%)" in SessionMonitor.lines(snapshot)
    assert "view OVERWATCH-1 waiting for tiles (87%)" in SessionMonitor.line(snapshot)
    recording = dict(snapshot, channels=[{"sensor_id": "OVERWATCH-1", "written": 3,
                                          "recorder_dropped": 0, "illumination_unpaired": 0,
                                          "occlusion_measured": 3, "occlusion_unmatched": 0}])
    assert not any(row.startswith("view") for row in SessionMonitor.lines(recording))
