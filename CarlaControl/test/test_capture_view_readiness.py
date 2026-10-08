"""run_capture writes no capture before the capture camera's tiles are in and its picture has settled.

Plan 03 §9.5.1 and plan 12 §6.2 check 50: readiness is two conditions from two witnesses. The server
says the tiles are in -- the camera published on the last tick, every visible tileset at load progress
100, no failed tile in view -- and the camera's own frames say the picture has settled: a frame within
0.5 grey levels of its frame ten or more ticks earlier in its worst judged 80-pixel block, counting
only frames rendered once the tiles were in, and leaving out every block a rendered vehicle covers in
either frame. The wait lives in the prewarm and ticks with it; a ceiling -- 90 s of wall clock for the
tiles; for the picture, `capture.picture_ceiling_frames` of the camera's own frames from its tiles
being in, 60 by default -- or a view not ready as the window opens refuses at pre-roll, naming the
channel and the witness, and the window's first frame is not moved. The ceiling and the limit,
`capture.picture_tolerance_levels`, are the run configuration's, so a test that moves either does so
with an override and not a constant.

The picture's wait is off by default, by the owner's ruling of 2026-10-06, and runs only under
`capture.picture_settled_wait` true, which every test of the picture here sets. Off, a view is ready
on the step its tiles are in, no camera is listened to and no frame compared, the prewarm must hold
the tiles' own lead, `capture.tiles_hold_s` (10 s), and the record says the wait was not run; the
tests of that default are together near the end.

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
from carlacontrol.LaunchEcho import LaunchEcho  # noqa: E402
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
    hold_lead_s,
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
PICTURE_WAIT_ON = "capture.picture_settled_wait=true"
# What the run result's readiness block holds only where the picture is waited on.
PICTURE_FIGURES = ("picture_span_ticks", "picture_ceiling_frames", "picture_tolerance_levels",
                   "picture_block_px", "picture_min_judged_share", "vehicle_margin_px", "vehicles")
CHANNEL_PICTURE_FIGURES = ("picture", "last_comparison", "comparison_history", "comparisons",
                           "locate_failures", "last_locate_failure", "frames_received",
                           "frames_unreadable")


@pytest.fixture
def layout(tmp_path: Path) -> Layout:
    return Layout(tmp_path)


def capture(layout: Layout, server: FakeServer, channels=None, overrides=(), picture_wait=True):
    """Run the fixture capture. Most tests here are of the picture's wait, so they turn it on with
    the run field that does; `picture_wait=False` leaves the field at its default, off."""
    document = run_document()
    if channels is not None:
        document["capture"]["channels"] = channels
    if picture_wait:
        overrides = [PICTURE_WAIT_ON, *overrides]
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
    # The camera's 600th frame against its 599th: it renders every tenth frame from its spawn.
    assert view["picture"] == {"settled_at_frame": LAST_PREWARM_FRAME, "compared_with_frame": 6990,
                               "frames": 3, "ticks_since_tiles": 20, "camera_frames": [600, 599],
                               "residual_levels": 0.0, "worst_block_px": [0, 0], "blocks": 8,
                               "excluded_blocks": 0, "judged_share": 1.0, "vehicles": [0, 0],
                               "camera_from_snapshot": [True, True]}
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
            readiness["picture_ceiling_frames"], readiness["picture_tolerance_levels"],
            readiness["picture_min_judged_share"],
            readiness["vehicle_margin_px"]) == (90.0, 10, 60, 0.5, 0.5, 4)
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
           "frame 1110, its frame 2 since its tiles (10 ticks), worst judged block 0.00 gray " \
           "levels with 100% of its blocks judged" in text


def test_the_echo_says_the_wait_will_happen(layout):
    server = FakeServer()
    _, result = capture(layout, server)
    readiness = result.launch_echo["readiness"]
    assert readiness["waits"] is True
    assert (readiness["from_s"], readiness["until_s"]) == (FIRST_PREWARM_S, BEGIN_S)
    # The ceiling in the camera's frames, and what they come to at the capture rate.
    assert (readiness["tiles_ceiling_s"], readiness["picture_ceiling_frames"],
            readiness["picture_ceiling_s"], readiness["picture_tolerance_levels"]) == \
        (90.0, 60, 30.0, 0.5)
    assert "within 0.5 gray levels" in readiness["picture"]
    assert "check 50" in readiness["not_ready"] and "not moved" in readiness["not_ready"]
    assert readiness["vehicles"].startswith("excluded")
    assert ("readiness   every view's tiles (ceiling 90 s) and picture (ceiling 60 of the camera's "
            "frames, 30 s at 2 Hz, settled within 0.5 gray levels), from t=24,900 to t=25,200"
            in LaunchEcho(result.launch_echo).render())


def test_the_echo_states_a_ceiling_and_a_limit_the_run_configuration_changed(layout):
    server = FakeServer()
    _, result = capture(layout, server, overrides=["capture.picture_ceiling_frames=12",
                                                   "capture.picture_tolerance_levels=1.5",
                                                   "capture.capture_hz=10"])
    readiness = result.launch_echo["readiness"]
    # Twelve frames at 10 Hz are 24 ticks: 1.2 s, where the same twelve at 2 Hz would be 6 s.
    assert (readiness["picture_ceiling_frames"], readiness["picture_ceiling_s"],
            readiness["picture_tolerance_levels"]) == (12, 1.2, 1.5)
    assert "within 1.5 gray levels" in readiness["picture"]
    assert result.produced["readiness"]["picture_ceiling_frames"] == 12
    assert result.produced["readiness"]["picture_tolerance_levels"] == 1.5


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
    # Tiles in from the first step, on frame 1020; every frame differs from the one before. The
    # camera's 60 frames from its tiles being in are 1020 to 1610, 30 s at 2 Hz; the 60th, judged
    # unsettled, is the ceiling, reached in the 31st step. 59 comparisons were made.
    server = FakeServer()
    server.picture_at = lambda _camera, frame: (frame // FRAMES_PER_CAPTURE) * 7 % 256
    _, result = capture(layout, server)
    refused_by_check_50(server, result)
    assert "picture did not settle within 60 of its frames after its tiles were in at frame 1020" \
        in result.detail
    assert "frame 1610 differs from frame 1600, 10 ticks earlier" in result.detail
    assert "against 0.5" in result.detail
    assert server.session.RenderedTimeSeconds == FIRST_PREWARM_S + 31.0
    assert the_view(result)["comparisons"]["made"] == 59


def test_the_ceiling_is_the_run_configuration_s_and_counts_the_camera_s_frames(layout):
    # The same drifting picture under a ceiling of twelve frames: at 2 Hz the twelfth frame from
    # the tiles is 1130, 110 ticks on, in the seventh step; at 10 Hz, where the camera renders every
    # other tick, it is frame 1042, 22 ticks on, in the third step. The ceiling moves with the
    # camera's frames and not with the ticks.
    server = FakeServer()
    server.picture_at = lambda _camera, frame: frame * 7 % 256
    _, result = capture(layout, server, overrides=["capture.picture_ceiling_frames=12"])
    refused_by_check_50(server, result)
    assert "did not settle within 12 of its frames after its tiles were in at frame 1020" \
        in result.detail
    assert "frame 1130 differs from frame 1120, 10 ticks earlier" in result.detail
    assert server.session.RenderedTimeSeconds == FIRST_PREWARM_S + 7.0
    faster = FakeServer()
    faster.picture_at = lambda _camera, frame: frame * 7 % 256
    _, result = capture(layout, faster, overrides=["capture.picture_ceiling_frames=12",
                                                   "capture.capture_hz=10"])
    refused_by_check_50(faster, result)
    assert "did not settle within 12 of its frames after its tiles were in at frame 1020" \
        in result.detail
    assert "frame 1042 differs from frame 1032, 10 ticks earlier" in result.detail
    assert faster.session.RenderedTimeSeconds == FIRST_PREWARM_S + 3.0


def test_the_limit_is_the_run_configuration_s(layout):
    # The camera's k-th frame is 128 + (k mod 3) grey, so the comparisons read 2, 1, 1, 2, ... and
    # never reach 0.5; a limit of 1 is met by the first comparison that reads 1, the camera's
    # fourth frame against its third, at frame 1040.
    server = FakeServer()
    server.picture_at = lambda _camera, frame: 128 + camera_frame(frame) % 3
    _, result = capture(layout, server, overrides=["capture.picture_tolerance_levels=1"])
    assert result.outcome == "run_finished"
    picture = the_view(result)["picture"]
    assert (picture["settled_at_frame"], picture["compared_with_frame"],
            picture["residual_levels"], picture["camera_frames"]) == (1040, 1030, 1.0, [4, 3])


def test_the_record_keeps_every_comparison_with_the_camera_s_own_frame_count(layout):
    # Tiles in from the first step, on frame 1020. The picture converges on its own: the camera's
    # k-th frame is 128 + 2^(7-k) grey up to its seventh and 128 from its eighth on, so the
    # comparisons from frame 1030 -- the third frame against the second -- read 16, 8, 4, 2, 1, 1
    # and settle at frame 1090, the ninth against the eighth, at 0. The record keeps every one,
    # each with the camera's frame count on either side, so a refusal shows the trend against the
    # camera's own frames and not only against the ticks since the tiles.
    server = FakeServer()
    server.picture_at = lambda _camera, frame: 128 + (
        2 ** (7 - camera_frame(frame)) if camera_frame(frame) < 8 else 0)
    _, result = capture(layout, server)
    assert result.outcome == "run_finished"
    view = the_view(result)
    history = view["comparison_history"]
    assert [entry["frame"] for entry in history] == list(range(1030, 1100, 10))
    assert [entry["worst_block_levels"] for entry in history] == \
        [16.0, 8.0, 4.0, 2.0, 1.0, 1.0, 0.0]
    assert [entry["camera_frames"] for entry in history] == [[k, k - 1] for k in range(3, 10)]
    assert all(entry["camera_from_snapshot"] == [True, True] for entry in history)
    assert (view["picture"]["settled_at_frame"], view["picture"]["camera_frames"]) == (1090, [9, 8])
    assert view["last_comparison"] == history[-1]


def test_the_refusal_lists_every_judged_comparison_against_the_camera_s_frames(layout):
    # The camera's k-th frame is 128 + (k mod 3) grey, so the comparisons read 2, 1, 1, 2, 1, 1, ...
    # and never settle. Under a ceiling of twelve frames from the tiles (1020 to 1130) the refusal
    # lists them all, from the camera's third frame (1030, against its second) to its thirteenth
    # (1130, the ceiling), so the trend can be read from the log alone.
    server = FakeServer()
    server.picture_at = lambda _camera, frame: 128 + camera_frame(frame) % 3
    _, result = capture(layout, server, overrides=["capture.picture_ceiling_frames=12"])
    refused_by_check_50(server, result)
    assert ("the 11 judged comparisons read 2.00, 1.00, 1.00, 2.00, 1.00, 1.00, 2.00, 1.00, 1.00, "
            "2.00, 1.00 gray levels in order, from the camera's frame 3 to its frame 13"
            in result.detail)
    assert len(the_view(result)["comparison_history"]) == 11


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
    # The ceiling counts the camera's frames whether or not they could be judged: the 60th from
    # the tiles, frame 1610, is in the 31st step.
    assert server.session.RenderedTimeSeconds == FIRST_PREWARM_S + 31.0
    comparisons = the_view(result)["comparisons"]
    assert comparisons["judged"] == 0 and comparisons["too_few_blocks"] == comparisons["made"]


def test_a_camera_the_snapshot_does_not_hold_is_posed_where_it_is_held(layout):
    # The client's snapshots hold the vehicle and not the camera: the camera is posed where the
    # channel holds it, which is where it was spawned, the vehicle's blocks are left out just the
    # same, and the record says which pose was used.
    server = FakeServer()
    at, server.vehicles_at = driving((0, 1), (1, 2))
    server.picture_at = lambda _camera, frame: painted({at(frame): frame * 7 % 256})
    held = server._client.GetSnapshotFrame

    def vehicles_only(frame):
        snapshot = held(frame)
        if snapshot is not None:
            snapshot.transforms = {actor: transform for actor, transform
                                   in snapshot.transforms.items() if actor == 11}
        return snapshot

    server._client.GetSnapshotFrame = vehicles_only
    _, result = capture(layout, server, channels=[A_NADIR])
    assert result.outcome == "run_finished"
    picture = the_view(result)["picture"]
    assert (picture["settled_at_frame"], picture["excluded_blocks"],
            picture["camera_from_snapshot"]) == (1030, 2, [False, False])


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
    # The server flies the orbit: the circle goes to it held, before the pre-roll's first step, and
    # is set moving once the recorders have started; nothing in the process ever moves the camera.
    names = server.events.names()
    assert server.events.of("move") == []
    [(_, _, held_moving, start_angle)] = server.events.of("set_orbit")
    assert (held_moving, start_angle) == (False, 0.0)
    assert names.index("set_orbit") < names.index("advance")
    moving = [i for i, event in enumerate(server.events.log)
              if event[0] == "set_orbit_enabled" and event[2]]
    assert moving and min(moving) > names.index("start_recording"), \
        "the orbit was set moving before the window opened"
    # And turned off by the termination, after the recorders drained, before the camera went.
    assert not session.channels[0].orbit.orbit_enabled
    stopped = [i for i, event in enumerate(server.events.log)
               if event[0] == "set_orbit_enabled" and not event[2]]
    assert names.index("stop_recording") < min(stopped) < names.index("destroy")
    held = result.produced["cameras"][0]["held_through_the_pre_roll"]
    assert held["angle_deg"] == 0.0 and held["sweeps_from"] == "the window's opening"


@pytest.mark.parametrize(("prewarm", "ceiling", "outcome", "needed"), [
    ("31", 60, "refused_offline", "at least 31.5 s"), ("32", 60, "run_finished", None),
    ("2", 2, "refused_offline", "at least 2.5 s"), ("3", 2, "run_finished", None)])
def test_a_prewarm_too_short_for_the_ceiling_s_frames_and_the_span_is_refused_offline(
        layout, prewarm, ceiling, outcome, needed):
    # After the first step, the ceiling's frames at one every ten ticks and the ten-tick span: 610
    # ticks for 60 frames, which 31 s leave 600 of and 32 s 620; 30 for a ceiling of two frames,
    # which 2 s leave 20 of and 3 s 40.
    server = FakeServer()
    _, result = capture(layout, server, overrides=[f"capture.prewarm_s={prewarm}",
                                                   f"capture.picture_ceiling_frames={ceiling}"])
    assert result.outcome == outcome
    if outcome == "refused_offline":
        [refusal] = result.refusals
        assert (refusal["check"], refusal["subject"]) == (51, "capture.prewarm_s")
        assert server.clients == []
        assert f"{ceiling} frames at one every 10 ticks" in refusal["message"]
        assert needed in refusal["message"]


@pytest.mark.parametrize(("hz", "ceiling", "first_judged"), [("2", 1, 2), ("20", 10, 11)])
def test_a_ceiling_too_small_to_hold_a_comparison_is_refused_offline(layout, hz, ceiling,
                                                                    first_judged):
    # A frame is judged against the camera's frame at least ten ticks before it, also rendered
    # since the tiles: the second frame at 2 Hz, the eleventh at 20 Hz. A ceiling below that holds
    # no comparison, so every run would be refused at pre-roll; it is refused here instead.
    server = FakeServer()
    _, result = capture(layout, server, overrides=[f"capture.capture_hz={hz}",
                                                   f"capture.picture_ceiling_frames={ceiling}"])
    assert result.outcome == "refused_offline" and server.clients == []
    [refusal] = result.refusals
    assert (refusal["check"], refusal["subject"]) == (51, "capture.picture_ceiling_frames")
    assert f"first possible on its frame {first_judged} since the tiles" in refusal["message"]
    assert f"at least {first_judged} frames" in refusal["message"]


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


# -- the picture's wait is off by default ---------------------------------------------------------

def test_by_default_a_view_is_ready_on_the_step_its_tiles_are_in_and_no_frame_is_compared(layout):
    # A picture that changes on every frame and so never settles, and tiles in from frame 6980, the
    # end of the 299th step, 5980 ticks and 29.9 s of the stand-in's wall clock into the wait: with
    # the picture's wait off the view is ready there and the window opens on time. No camera is
    # listened to, and the tiles are still asked about once after every step.
    server = FakeServer()
    server.tiles_at = in_from(6980)
    server.picture_at = lambda _camera, frame: frame * 7 % 256
    _, result = capture(layout, server, picture_wait=False)
    assert result.outcome == "run_finished"
    view = the_view(result)
    assert (view["state"], view["picture_settled_wait"], view["ready_at_window_open"]) == \
        ("ready", False, True)
    assert view["tiles"] == {"in_at_frame": 6980, "ticks": 5980, "wall_s": pytest.approx(29.9)}
    assert not set(CHANNEL_PICTURE_FIGURES) & set(view)
    assert server.events.of("listen") == [] and server.events.of("stop_listening") == []
    assert len(server.events.of("view_readiness")) == 300
    assert server.events.of("world_tick") == []
    [start] = server.events.of("start_recording")
    assert start[5] == BEGIN_S and start[6] == LAST_PREWARM_FRAME


def test_by_default_tiles_in_on_the_prewarm_s_last_step_are_in_time(layout):
    # With the picture waited on, tiles in only on the prewarm's last frame leave no frame to
    # compare and the run is refused; with it off, the view is ready on that step.
    server = FakeServer()
    server.tiles_at = in_from(LAST_PREWARM_FRAME)
    _, result = capture(layout, server, picture_wait=False)
    assert result.outcome == "run_finished"
    assert the_view(result)["tiles"]["in_at_frame"] == LAST_PREWARM_FRAME


def test_by_default_tiles_that_never_come_in_still_refuse_at_preroll(layout):
    server = FakeServer()
    server.tiles_at = lambda _camera, _frame: tiles(published=False)
    _, result = capture(layout, server, picture_wait=False)
    refused_by_check_50(server, result)
    assert "not published for this view yet" in result.detail
    assert the_view(result)["state"] == "waiting for tiles"


def test_by_default_a_view_must_still_have_its_tiles_as_the_window_opens(layout):
    # In from the start, then streaming again over the last two steps: ready, then waiting for its
    # tiles again as the window opens.
    server = FakeServer()
    server.tiles_at = in_from(0, until=LAST_PREWARM_FRAME - TICKS_PER_STEP)
    _, result = capture(layout, server, picture_wait=False)
    refused_by_check_50(server, result)
    view = the_view(result)
    assert view["state"] == "waiting for tiles"
    assert view["relapses"] == [{"frame": LAST_PREWARM_FRAME - TICKS_PER_STEP, "was": "ready",
                                 "tiles": "62%"}]


def test_by_default_the_result_says_the_picture_wait_was_not_run_and_holds_no_figure_of_it(layout):
    # The tiles are in at the first ask, after the first step: frame 1020, 20 ticks and 0.1 s of
    # the stand-in's wall clock into the wait.
    server = FakeServer()
    _, result = capture(layout, server, picture_wait=False)
    readiness = result.produced["readiness"]
    assert readiness["picture_settled_wait"] is False
    assert readiness["picture"].startswith("not run")
    assert "capture.picture_settled_wait" in readiness["picture"]
    assert not set(PICTURE_FIGURES) & set(readiness)
    assert (readiness["tiles_ceiling_s"], readiness["tiles_hold_s"], readiness["wait_began"]) == \
        (90.0, 10.0, {"sim_time_s": FIRST_PREWARM_S, "ticks": 0})
    assert the_view(result)["tiles"] == {"in_at_frame": 1020, "ticks": 20, "wall_s": 0.1}
    text = RunCloseoutReport.render(result.produced["session"]["last_snapshot"] | {
        "scenario": "s", "window_name": "w", "sim_time_s": None, "window": {},
        "channels": [], "readiness": readiness}, [])
    assert "view OVERWATCH-1: tiles in at frame 1020 after 20 ticks, 0.1 s; picture-settled wait " \
           "off" in text
    assert "picture settl" not in text


def test_with_the_wait_on_the_result_and_the_echo_say_so_beside_its_figures(layout):
    server = FakeServer()
    _, result = capture(layout, server)
    readiness = result.produced["readiness"]
    assert readiness["picture_settled_wait"] is True
    assert set(PICTURE_FIGURES) <= set(readiness) and "tiles_hold_s" not in readiness
    assert "tiles_hold_s" not in result.launch_echo["readiness"]
    view = the_view(result)
    assert view["picture_settled_wait"] is True and set(CHANNEL_PICTURE_FIGURES) <= set(view)
    assert result.launch_echo["readiness"]["picture_settled_wait"] is True
    assert server.events.of("listen") != []


def test_by_default_the_echo_says_the_picture_is_not_waited_on(layout):
    server = FakeServer()
    _, result = capture(layout, server, picture_wait=False)
    readiness = result.launch_echo["readiness"]
    assert (readiness["waits"], readiness["picture_settled_wait"]) == (True, False)
    assert readiness["picture"].startswith("not waited on")
    assert "capture.picture_settled_wait" in readiness["picture"]
    assert not {"picture_ceiling_frames", "picture_ceiling_s", "picture_tolerance_levels",
                "vehicles"} & set(readiness)
    assert (readiness["tiles_ceiling_s"], readiness["tiles_hold_s"], readiness["from_s"],
            readiness["until_s"]) == (90.0, 10.0, FIRST_PREWARM_S, BEGIN_S)
    rendered = LaunchEcho(result.launch_echo).render()
    assert ("readiness   every view's tiles (ceiling 90 s), from t=24,900 to t=25,200; not ready "
            "by then refuses at pre-roll" in rendered)
    assert ("picture not waited on: capture.picture_settled_wait is false; the tiles' lead is 10 s "
            "(capture.tiles_hold_s)" in rendered)


@pytest.mark.parametrize(("prewarm", "outcome"), [
    ("0", "refused_offline"), ("9", "refused_offline"), ("10", "run_finished"),
    ("31", "run_finished")])
def test_by_default_the_prewarm_must_hold_the_tiles_lead(layout, prewarm, outcome):
    # With the picture not waited on, the tiles have a lead of their own, capture.tiles_hold_s, 10 s
    # by default, which a camera at a fixed pose holds from the prewarm's first step: its tiles are
    # asked about after each of the ten steps. That lead is all check 51 asks of the prewarm, where
    # with the picture waited on 31 s would be refused.
    server = FakeServer()
    _, result = capture(layout, server, overrides=[f"capture.prewarm_s={prewarm}"],
                        picture_wait=False)
    assert result.outcome == outcome
    if outcome == "refused_offline":
        [refusal] = result.refusals
        assert (refusal["check"], refusal["subject"]) == (51, "capture.prewarm_s")
        assert "capture.tiles_hold_s, 10 s" in refusal["message"]
        assert "at least 10 s" in refusal["message"]
        assert server.clients == []
        return
    assert the_view(result)["state"] == "ready"
    assert len(server.events.of("view_readiness")) == int(prewarm)


@pytest.mark.parametrize(("hold", "prewarm", "outcome", "needed"), [
    ("2", "1", "refused_offline", "at least 2 s"), ("2", "2", "run_finished", None),
    ("2.5", "2", "refused_offline", "at least 3 s"), ("2.5", "3", "run_finished", None)])
def test_the_tiles_lead_is_the_run_configuration_s_in_whole_steps(layout, hold, prewarm, outcome,
                                                                   needed):
    # 2 s is two one-second steps; 2.5 s is rounded up to three.
    server = FakeServer()
    _, result = capture(layout, server, overrides=[f"capture.tiles_hold_s={hold}",
                                                   f"capture.prewarm_s={prewarm}"],
                        picture_wait=False)
    assert result.outcome == outcome
    if outcome == "refused_offline":
        [refusal] = result.refusals
        assert (refusal["check"], refusal["subject"]) == (51, "capture.prewarm_s")
        assert needed in refusal["message"]
        return
    assert result.produced["readiness"]["tiles_hold_s"] == float(hold)


def test_by_default_the_picture_s_ceiling_and_limit_are_not_read(layout):
    # A ceiling of one frame at 2 Hz holds no comparison, which check 51 refuses where the picture
    # is waited on; with it off neither field is read, and the run goes ahead.
    server = FakeServer()
    _, result = capture(layout, server, overrides=["capture.picture_ceiling_frames=1",
                                                   "capture.picture_tolerance_levels=9"],
                        picture_wait=False)
    assert result.outcome == "run_finished"
    assert not set(PICTURE_FIGURES) & set(result.produced["readiness"])


def test_by_default_the_monitor_never_says_the_picture_is_settling():
    waiting = {"sensor_id": "OVERWATCH-1", "state": "waiting for tiles",
               "picture_settled_wait": False, "tiles": None, "tiles_now": "87%", "relapses": []}
    ready = dict(waiting, state="ready", tiles={"in_at_frame": 1100, "ticks": 100, "wall_s": 0.5},
                 tiles_now="in")
    snapshot = {"scenario": "s", "window_name": "w", "sim_time_s": 24910.0,
                "window": {"end_s": 27000.0}, "illumination": None, "pacing": None,
                "admission": None, "render": None, "channels": [],
                "readiness": {"channels": [waiting]}}
    assert "view  OVERWATCH-1   waiting for tiles (87%)" in SessionMonitor.lines(snapshot)
    held = dict(snapshot, readiness={"channels": [ready]})
    assert "view  OVERWATCH-1   tiles in at frame 1100 after 100 ticks, 0.5 s; picture-settled " \
           "wait off" in SessionMonitor.lines(held)
    assert "picture settling" not in SessionMonitor.line(held)


def test_the_hold_is_the_tiles_lead_by_default_and_the_picture_s_ceiling_with_it_on():
    # By default the tiles' own lead, 10 s, in whole SUMO steps: ten one-second steps, or two
    # hundred steps of a scenario that steps every tick; 2.5 s rounds up to three one-second steps,
    # and a lead shorter than a step is one step. With the picture waited on, one step for the tiles
    # to be first asked about and the ceiling's 60 frames at one every ten ticks and the ten-tick
    # span, 610 ticks, in whole steps: 32 one-second steps, whatever the tiles' lead.
    assert hold_lead_s(1.0, 0.05, 10, 60) == pytest.approx(10.0)
    assert hold_lead_s(0.05, 0.05, 10, 60) == pytest.approx(10.0)
    assert hold_lead_s(1.0, 0.05, 10, 60, tiles_hold_s=2.5) == pytest.approx(3.0)
    assert hold_lead_s(1.0, 0.05, 10, 60, tiles_hold_s=0.2) == pytest.approx(1.0)
    assert hold_lead_s(1.0, 0.05, 10, 60, picture_wait=True) == pytest.approx(32.0)
    assert hold_lead_s(1.0, 0.05, 10, 60, picture_wait=True, tiles_hold_s=60.0) == \
        pytest.approx(32.0)
