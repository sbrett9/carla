"""run_capture writes no capture before the capture camera's tiles are in and its picture has settled.

Plan 03 §9.5.1 and plan 12 §6.2 check 50: readiness is two conditions from two witnesses. The server
says the tiles are in -- the camera published on the last tick, every visible tileset at load progress
100, no failed tile in view -- and the camera's own frames say the picture has settled: a frame within
0.5 grey levels of its frame ten or more ticks earlier in its worst judged 80-pixel block, counting
only frames rendered once the tiles were in, and leaving out every block a rendered vehicle covers in
either frame. The wait lives in the prewarm and ticks with it; a ceiling -- 90 s of wall clock for the
tiles, 120 ticks for the picture -- or a view not ready as the window opens refuses at pre-roll,
naming the channel and the witness, and the window's first frame is not moved.

The stand-in server numbers its frames from 1000 and the camera is spawned on that frame; with a
one-second SUMO step of twenty 0.05 s ticks and a 2 Hz capture, the camera renders every tenth frame,
the prewarm's 300 steps end on frame 7000, and the window's first frame is 7001. The stand-in's
frames are 320 x 160, so a frame is compared over eight 80-pixel blocks, two rows of four. A stare
looking straight down from 160 m with a 90 degree field of view sees one metre per pixel there, so a
block is 80 m of ground: block (row r, column c) is centred on x = 80 c - 120, y = 80 r - 40. Every
expected frame and block below is worked out from those figures, not read back from the code under
test. The run's clock is the stand-in's wall clock, which each tick advances.
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

import carlanet  # noqa: E402, F401  -- loads the CarlaNet assemblies the next imports name
import System  # noqa: E402
from CaptureFakes import FakeServer  # noqa: E402
from CarlaNet.Recording import DepthFrame, OcclusionEstimator, OcclusionOptions  # noqa: E402
from CarlaNet.Recording import VehicleBox as CsVehicleBox  # noqa: E402
from CarlaNet.Types.Geom import (  # noqa: E402
    BoundingBox,
    Location,
    Rotation,
    RotationBasis,
    Transform,
    Vector3D,
)
from RunCaptureFixture import A_STARE, AN_ORBIT, Layout, run_document  # noqa: E402
from System.Collections.Generic import List  # noqa: E402

from carlacontrol.CaptureSession import CaptureSession  # noqa: E402
from carlacontrol.OrbitSensorController import OrbitSensorController  # noqa: E402
from carlacontrol.RunCloseoutReport import RunCloseoutReport  # noqa: E402
from carlacontrol.RunConfigurationValidator import RunConfigurationValidator  # noqa: E402
from carlacontrol.RunTerminationSequence import RunTerminationSequence  # noqa: E402
from carlacontrol.SessionMonitor import SessionMonitor  # noqa: E402
from carlacontrol.SiteProfile import SiteProfile  # noqa: E402
from carlacontrol.ViewReadiness import (  # noqa: E402
    Pose,
    VehicleBox,
    blocks_covered,
    grey_small,
    rotation_basis,
    tiles_in,
    vehicle_footprint,
    worst_block,
)

Usage = namedtuple("Usage", "total used free")
SESSION_ID = "cap-ready"
FIRST_FRAME = 1000          # the frame the camera is spawned on
TICKS_PER_STEP = 20
FRAMES_PER_CAPTURE = 10     # 0.5 s of sensor_tick over 0.05 s ticks
LAST_PREWARM_FRAME = FIRST_FRAME + 300 * TICKS_PER_STEP
FIRST_PREWARM_S = 24900.0
BEGIN_S = 25200.0
WIDTH, HEIGHT = 320, 160
BLOCK = 80
A_NADIR = {"sensor_id": "NADIR-1", "stare_look_at_x_m": 0.0, "stare_look_at_y_m": 0.0,
           "stare_altitude_m": 160.0}


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


def block_centre(row: int, column: int) -> tuple[float, float]:
    """Where on the ground the nadir stare sees the middle of a block: one metre per pixel."""
    return BLOCK * column + BLOCK / 2 - WIDTH / 2, BLOCK * row + BLOCK / 2 - HEIGHT / 2


def painted(blocks: dict[tuple[int, int], int], base: int = 128) -> bytes:
    """A BGRA frame of the stand-in's size, grey `base` but for the blocks given their own level."""
    image = np.full((HEIGHT, WIDTH, 4), base, np.uint8)
    image[:, :, 3] = 255
    for (row, column), level in blocks.items():
        image[row * BLOCK:(row + 1) * BLOCK, column * BLOCK:(column + 1) * BLOCK, :3] = level
    return image.tobytes()


def camera_frame(frame: int) -> int:
    """How many of the camera's frames since it was spawned."""
    return (frame - FIRST_FRAME) // FRAMES_PER_CAPTURE


def driving(*positions: tuple[int, int], actor: int = 11):
    """One vehicle standing in each block in turn, one block per camera frame."""
    def at(frame: int) -> tuple[int, int]:
        return positions[camera_frame(frame) % len(positions)]

    def vehicles(frame: int):
        x, y = block_centre(*at(frame))
        return [(actor, (x, y, 0.0, 0.0))]
    return at, vehicles


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
    excluded = np.zeros((9, 16), dtype=bool)
    excluded[2, 5] = True               # the refining block covered by a vehicle
    assert worst_block(newer, older, excluded) == (0.0, (0, 0))


def test_the_tiles_are_in_only_published_loaded_and_whole():
    def answer(published=True, progress=100.0, failed=0):
        return {"frame": 1, "published": published, "tilesets": [
            {"visible": True, "load_progress": progress, "failed_in_view": failed},
            {"visible": False, "load_progress": 0.0, "failed_in_view": 0}]}
    assert tiles_in(answer())
    assert not tiles_in(answer(published=False))
    assert not tiles_in(answer(failed=1))
    assert not tiles_in(answer(progress=99.9))


# -- where a vehicle is in the picture -------------------------------------------------------------

@pytest.mark.parametrize("rotation", [(0.0, 0.0, 0.0), (-90.0, -90.0, 0.0), (-37.0, 123.0, 8.0),
                                      (12.5, -170.0, -30.0)])
def test_the_axes_are_carlanet_s_rotation_basis(rotation):
    basis = RotationBasis(Rotation(*rotation))
    expected = [[axis.X, axis.Y, axis.Z] for axis in (basis.Forward, basis.Right, basis.Up)]
    assert rotation_basis(*rotation) == pytest.approx(np.array(expected), abs=1e-6)


@pytest.mark.parametrize(("camera", "vehicle"), [
    ((0.0, 0.0, 50.0, -60.0, 30.0), (30.0, 20.0, 0.0, 45.0)),
    ((-40.0, 10.0, 300.0, -90.0, -90.0), (-35.0, 22.0, 3.0, 170.0)),
    ((5.0, -5.0, 120.0, -30.0, 200.0), (-60.0, -150.0, 0.0, -20.0)),
])
def test_a_vehicle_s_box_is_projected_as_the_occlusion_estimate_projects_it(camera, vehicle):
    # The occlusion estimate's footprint is private, but it reports the box's apparent size, which
    # is the footprint's width and height rounded: the two projections agree to the pixel.
    width, height = 640, 360
    camera_transform = Transform(Location(*camera[:3]), Rotation(camera[3], camera[4], 0.0))
    vehicle_transform = Transform(Location(*vehicle[:3]), Rotation(0.0, vehicle[3], 0.0))
    box = BoundingBox(Location(0.0, 0.0, 0.8), Vector3D(2.4, 1.0, 0.8), Rotation(0.0, 0.0, 0.0))
    pixels = System.ReadOnlyMemory[System.Byte](System.Array[System.Byte](bytes(width * height * 4)))
    depth = DepthFrame(1, 0.0, camera_transform, width, height, 90.0, pixels, 1000.0)
    boxes = List[CsVehicleBox]()
    boxes.Add(CsVehicleBox(7, vehicle_transform, box))
    measured = OcclusionEstimator.Estimate(depth, boxes, OcclusionOptions.Default)[7]
    u0, v0, u1, v1 = vehicle_footprint(Pose.of(camera_transform), 90.0, width, height,
                                       Pose.of(vehicle_transform), VehicleBox.of(box))
    assert (round(u1 - u0), round(v1 - v0)) == (measured.ApparentWidthPx,
                                                measured.ApparentHeightPx)


def test_the_shadow_a_vehicle_casts_is_part_of_what_it_covers():
    # Straight down from 100 m over a 4 m tall box: one pixel is a metre of ground. A sun at 45
    # degrees in the south throws the box's top four metres north, up the picture; one below the
    # horizon throws nothing.
    camera = Pose(0.0, 0.0, 100.0, -90.0, -90.0)
    box = VehicleBox((0.0, 0.0, 2.0), (2.0, 1.0, 2.0))
    vehicle = Pose(0.0, 0.0, 0.0)
    bare = vehicle_footprint(camera, 90.0, 200, 200, vehicle, box)
    shadowed = vehicle_footprint(camera, 90.0, 200, 200, vehicle, box, sun=(45.0, 180.0))
    assert shadowed[1] == pytest.approx(100.0 - 5.0)       # the north edge of the shadow, y = -5
    assert (shadowed[0], shadowed[2], shadowed[3]) == pytest.approx((bare[0], bare[2], bare[3]))
    assert vehicle_footprint(camera, 90.0, 200, 200, vehicle, box, sun=(-3.0, 180.0)) == bare
    margin = vehicle_footprint(camera, 90.0, 200, 200, vehicle, box, margin_px=4.0)
    assert margin == pytest.approx((bare[0] - 4, bare[1] - 4, bare[2] + 4, bare[3] + 4))


def test_a_rectangle_covers_every_block_it_touches():
    covered = blocks_covered([(70.0, 10.0, 90.0, 20.0), (300.0, 150.0, 400.0, 400.0)], 320, 160)
    assert covered.tolist() == [[True, True, False, False], [False, False, False, True]]


# -- a view that becomes ready ---------------------------------------------------------------------

def test_recording_starts_on_the_frame_after_the_view_is_ready(layout):
    # Tiles in from frame 6980, the end of the 299th step. The picture changes on frame 6990 and
    # not again, so the prewarm's last frame, 7000, is the first to match the one ten ticks before
    # it: ready exactly as the window opens, recorded from frame 7001.
    server = FakeServer()
    server.tiles_at = in_from(6980)
    server.picture_at = lambda _camera, frame: 100 if frame < 6990 else 50
    _, result = capture(layout, server)
    assert result.outcome == "run_finished"
    view = the_view(result)
    assert view["tiles"]["in_at_frame"] == 6980
    assert view["picture"] == {"settled_at_frame": LAST_PREWARM_FRAME, "compared_with_frame": 6990,
                               "frames": 3, "ticks_since_tiles": 20, "residual_levels": 0.0,
                               "worst_block_px": [0, 0], "blocks": 8, "excluded_blocks": 0,
                               "judged_share": 1.0, "vehicles": [0, 0]}
    assert view["ready_at_window_open"] is True
    [start] = server.events.of("start_recording")
    assert start[5] == BEGIN_S and start[6] == LAST_PREWARM_FRAME


def test_a_view_ready_one_step_too_late_is_refused_rather_than_recorded_late(layout):
    # Tiles in from frame 7000, the prewarm's last: one frame since, none to compare it with, and
    # the window is not moved.
    server = FakeServer()
    server.tiles_at = in_from(7000)
    _, result = capture(layout, server)
    refused_by_check_50(server, result)
    assert "OVERWATCH-1" in result.detail and "had not settled" in result.detail
    assert "1 of its frames arrived since, none 10 ticks after another" in result.detail
    assert server.session.RenderedTimeSeconds == BEGIN_S


def test_the_result_records_how_each_view_became_ready(layout):
    server = FakeServer()
    server.tiles_at = in_from(1100)
    _, result = capture(layout, server)
    readiness = result.produced["readiness"]
    assert (readiness["tiles_ceiling_s"], readiness["picture_span_ticks"],
            readiness["picture_ceiling_ticks"], readiness["picture_min_judged_share"],
            readiness["vehicle_margin_px"]) == (90.0, 10, 120, 0.5, 4)
    assert readiness["wait_began"] == {"sim_time_s": FIRST_PREWARM_S, "ticks": 0}
    assert readiness["per_capture"].startswith("not recorded")
    view = the_view(result)
    # Asked after each step: the fifth step ends on frame 1100, 100 ticks and 0.5 s of the
    # stand-in's wall clock into the wait; the frame ten ticks later matches it.
    assert view["tiles"] == {"in_at_frame": 1100, "ticks": 100, "wall_s": 0.5}
    assert (view["picture"]["settled_at_frame"], view["picture"]["frames"],
            view["picture"]["ticks_since_tiles"]) == (1110, 2, 10)
    assert view["comparisons"] == {"made": 1, "judged": 1, "too_few_blocks": 0,
                                   "vehicles_unknown": 0}
    assert view["readiness_asked"]["count"] == 300
    text = RunCloseoutReport.render(result.produced["session"]["last_snapshot"] | {
        "scenario": "s", "window_name": "w", "sim_time_s": None, "window": {},
        "channels": [], "readiness": readiness}, [])
    assert "view OVERWATCH-1: tiles in at frame 1100 after 100 ticks, 0.5 s; picture settled at " \
           "frame 1110, 10 ticks after its tiles, worst judged block 0.00 grey levels with 100% " \
           "of its blocks judged" in text


def test_the_echo_says_the_wait_will_happen(layout):
    server = FakeServer()
    _, result = capture(layout, server)
    readiness = result.launch_echo["readiness"]
    assert readiness["waits"] is True
    assert (readiness["from_s"], readiness["until_s"]) == (FIRST_PREWARM_S, BEGIN_S)
    assert (readiness["tiles_ceiling_s"], readiness["picture_ceiling_ticks"]) == (90.0, 120)
    assert "check 50" in readiness["not_ready"] and "not moved" in readiness["not_ready"]
    assert readiness["vehicles"].startswith("excluded")


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
    # Tiles in from the first step, on frame 1020; every frame differs from the one before. Frames
    # up to 1140, 120 ticks on, are compared; frame 1150 is past the ceiling, in the eighth step.
    server = FakeServer()
    server.picture_at = lambda _camera, frame: (frame // FRAMES_PER_CAPTURE) * 7 % 256
    _, result = capture(layout, server)
    refused_by_check_50(server, result)
    assert "picture did not settle within 120 ticks of its tiles being in at frame 1020" \
        in result.detail
    assert "frame 1140 differs from frame 1130, 10 ticks earlier" in result.detail
    assert server.session.RenderedTimeSeconds == FIRST_PREWARM_S + 8.0


def test_the_span_is_ten_ticks_whatever_the_capture_rate(layout):
    # At 10 Hz the camera renders every other tick, so ten ticks is five of its frames. A level
    # every six ticks: frames two ticks apart are mostly identical, frames ten ticks apart differ,
    # until the drift stops at frame 1096. Frame 1106 is the first to match its frame ten ticks
    # before; its frame before is two ticks back and its frame ten frames before, twenty.
    server = FakeServer()
    server.picture_at = lambda _camera, frame: 50 + (min(frame, 1096) - FIRST_FRAME) // 6
    _, result = capture(layout, server, overrides=["capture.capture_hz=10"])
    assert result.outcome == "run_finished"
    picture = the_view(result)["picture"]
    assert (picture["settled_at_frame"], picture["compared_with_frame"]) == (1106, 1096)


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
            result.produced["readiness"]["channels"]] == [1030, 1030]


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
    assert view["tiles"]["in_at_frame"] == 3100 and view["picture"]["settled_at_frame"] == 3110


def test_a_server_that_cannot_say_is_refused_at_preroll(layout):
    server = FakeServer()
    server.readiness_raises = RuntimeError("get_view_readiness is not bound on this server")
    _, result = capture(layout, server)
    refused_by_check_50(server, result)
    assert "could not say whether camera" in result.detail


# -- the rendered vehicles are left out ---------------------------------------------------------------

def test_a_picture_changing_only_where_vehicles_drive_settles(layout):
    # One vehicle stands in block (0, 1) on one frame and block (1, 2) on the next, and its block
    # changes every frame; nothing else does. Each comparison leaves out the block it stands in on
    # either frame -- the one it left as well as the one it entered -- so six of eight are judged.
    server = FakeServer()
    at, server.vehicles_at = driving((0, 1), (1, 2))
    server.picture_at = lambda _camera, frame: painted({at(frame): frame * 7 % 256})
    _, result = capture(layout, server, channels=[A_NADIR])
    assert result.outcome == "run_finished"
    picture = the_view(result)["picture"]
    assert (picture["settled_at_frame"], picture["excluded_blocks"], picture["judged_share"],
            picture["vehicles"]) == (1030, 2, 0.75, [1, 1])
    assert server.events.of("get_actors") == [("get_actors", [11])]


def test_the_margin_takes_in_a_vehicle_s_edge_across_a_block_line(layout):
    # A vehicle whose box ends two pixels short of the line between blocks (0, 0) and (0, 1) -- its
    # centre at x = -84.4, its east edge at pixel 78 -- drawn a pixel past the line, and changing
    # every frame. Only the margin takes in block (0, 1), which a one-pixel strip moves by 2.5 levels.
    server = FakeServer()
    server.vehicles_at = lambda _frame: [(11, (-84.4, -40.0, 0.0, 0.0))]

    def picture(_camera, frame):
        image = np.full((HEIGHT, WIDTH, 4), 128, np.uint8)
        image[30:50, 72:81, :3] = frame * 7 % 256
        return image.tobytes()

    server.picture_at = picture
    _, result = capture(layout, server, channels=[A_NADIR])
    assert result.outcome == "run_finished"
    assert the_view(result)["picture"]["excluded_blocks"] == 2


def test_a_block_changing_where_no_vehicle_is_does_not_settle(layout):
    # The same vehicle, and block (1, 0) -- where it never is -- changing every frame as well.
    server = FakeServer()
    at, server.vehicles_at = driving((0, 1), (1, 2))
    server.picture_at = lambda _camera, frame: painted({at(frame): frame * 7 % 256,
                                                         (1, 0): frame * 3 % 256})
    _, result = capture(layout, server, channels=[A_NADIR])
    refused_by_check_50(server, result)
    assert "worst judged 80-pixel block (at x 0, y 80)" in result.detail
    assert "with 2 of 8 blocks left out for rendered vehicles" in result.detail


def test_a_view_too_full_of_vehicles_to_judge_is_refused_naming_that(layout):
    # Five vehicles in five of the eight blocks: three are left to judge, 38% against 50%, and
    # though nothing in them changes, the view cannot be judged. It is refused at the ceiling.
    server = FakeServer()
    blocks = [(0, 0), (0, 1), (0, 2), (1, 0), (1, 1)]
    server.vehicles_at = lambda _frame: [(20 + index, (*block_centre(*block), 0.0, 0.0))
                                         for index, block in enumerate(blocks)]
    _, result = capture(layout, server, channels=[A_NADIR])
    refused_by_check_50(server, result)
    assert "rendered vehicles covered 5 of its 8 80-pixel blocks" in result.detail
    assert "leaving 38% of the view to judge against the 50% the witness needs" in result.detail
    assert server.session.RenderedTimeSeconds == FIRST_PREWARM_S + 8.0
    comparisons = the_view(result)["comparisons"]
    assert comparisons["judged"] == 0 and comparisons["too_few_blocks"] == comparisons["made"]


def test_a_frame_whose_vehicles_cannot_be_placed_is_not_judged(layout):
    # The client holds no snapshot of any frame: no vehicle can be placed, nothing is judged.
    server = FakeServer()
    server._client.GetSnapshotFrame = lambda _frame: None
    _, result = capture(layout, server, channels=[A_NADIR])
    refused_by_check_50(server, result)
    assert "could not be placed" in result.detail
    comparisons = the_view(result)["comparisons"]
    assert comparisons["judged"] == 0 and comparisons["vehicles_unknown"] == comparisons["made"]


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
    session, result = capture(layout, server, channels=[AN_ORBIT])
    assert result.outcome == "run_finished"
    # The orbit's depth camera is attached to its camera and takes no move of its own.
    [camera] = [actor for actor in server.actors if actor.type_id == "sensor.camera.rgb"]
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


@pytest.mark.parametrize(("prewarm", "outcome"), [("1", "refused_offline"),
                                                  ("2", "run_finished")])
def test_a_prewarm_too_short_to_compare_two_frames_is_refused_offline(layout, prewarm, outcome):
    # After the first step, a camera rendering every ten ticks may need nineteen more for two
    # frames ten ticks apart: one second leaves none, two leave twenty.
    server = FakeServer()
    _, result = capture(layout, server, overrides=[f"capture.prewarm_s={prewarm}"])
    assert result.outcome == outcome
    if outcome == "refused_offline":
        assert result.refusals[0]["check"] == 51 and server.clients == []
        assert "at least 1.95 s" in result.refusals[0]["message"]


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
