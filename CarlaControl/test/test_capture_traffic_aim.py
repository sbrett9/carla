"""A stare aimed at the rendered traffic looks at where the session put the vehicles before the window.

Plan 12 §5.2 and D12.37: `stare_look_at_target: rendered_traffic` resolves to the mean position,
height included, of the vehicles the session rendered on the last frame before its camera holds for
the window, measured from the poses it wrote to bodies. The camera follows that centre through the
prewarm until one SUMO step and the picture's 120-tick ceiling before the window opens -- seven
one-second steps -- and then holds one pose, so the view whose tiles and picture are waited on
(03 §9.5.1) is the view the window holds; the run result records the point so the view is
reproducible as an ordinary look-at stare.

The stand-in session hands `on_pose` one record per vehicle per tick. Its vehicles drive east at one
metre per simulated second, so the centre of a step's last frame, the centre of the whole step and the
centre of the prewarm's first frame are all different points, and a body-less pose sits far away, so
counting it would move the centre by kilometres. The expected pose is computed from those positions
with `StareAim.looking_at`, not read back from the code under test.
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

from CaptureFakes import FakeServer  # noqa: E402
from RunCaptureFixture import A_STARE, Layout, run_document  # noqa: E402

from carlacontrol.CaptureSession import CaptureSession  # noqa: E402
from carlacontrol.RunConfigurationValidator import RunConfigurationValidator  # noqa: E402
from carlacontrol.RunTerminationSequence import RunTerminationSequence  # noqa: E402
from carlacontrol.SessionMonitor import SessionMonitor  # noqa: E402
from carlacontrol.SiteProfile import SiteProfile  # noqa: E402
from carlacontrol.StareAim import StareAim  # noqa: E402

Usage = namedtuple("Usage", "total used free")
SESSION_ID = "cap-traffic"
FIRST_RENDERED_S = 24900.0
BEGIN_S = 25200.0
# One SUMO step for the tiles to be first asked about and 120 ticks of 0.05 s for the picture: seven
# one-second steps before the window.
HOLD_S = 25193.0
LAST_FOLLOWED_FRAME_S = 25192.95
FOLLOWED_STEPS = int(HOLD_S - FIRST_RENDERED_S)
A_TRAFFIC_STARE = {"sensor_id": "TRAFFIC-1", "stare_look_at_target": "rendered_traffic",
                   "stare_altitude_m": 250.0, "stare_standoff_m": 300.0, "stare_bearing_deg": 45.0}
# Three bodies driving east at 1 m/s from these points at the first rendered frame, and a pose the
# pool had no body for, far away.
BODIES = ((11, 100.0, -200.0, 12.0), (12, 120.0, -220.0, 14.0), (13, 140.0, -240.0, 16.0))
BODILESS = (0, 5000.0, 5000.0, 900.0)


@pytest.fixture
def layout(tmp_path: Path) -> Layout:
    return Layout(tmp_path)


def eastbound(at: float) -> list[tuple[int, tuple[float, float, float]]]:
    moved = at - FIRST_RENDERED_S
    return [(actor, (x + moved, y, z)) for actor, x, y, z in BODIES] + \
        [(BODILESS[0], BODILESS[1:])]


def centre_at(at: float) -> tuple[float, float, float]:
    """The mean of the bodies' positions at one rendered instant, computed here independently."""
    moved = at - FIRST_RENDERED_S
    return (sum(x + moved for _, x, _, _ in BODIES) / len(BODIES),
            sum(y for _, _, y, _ in BODIES) / len(BODIES),
            sum(z for _, _, _, z in BODIES) / len(BODIES))


def aim_around(point: tuple[float, float, float]) -> StareAim:
    return StareAim.looking_at(*point, A_TRAFFIC_STARE["stare_altitude_m"],
                               A_TRAFFIC_STARE["stare_standoff_m"],
                               A_TRAFFIC_STARE["stare_bearing_deg"])


def capture(layout: Layout, server: FakeServer, channels=None, overrides=()):
    document = run_document()
    document["capture"]["channels"] = channels or [A_TRAFFIC_STARE]
    run_path = layout.root / "fixture.run.json"
    run_path.write_text(json.dumps(document), encoding="utf-8")
    site = SiteProfile.discover(layout.root, layout.write_profile(), environ={})
    session = CaptureSession(
        site, run_path, [(text, f"--set {text}") for text in overrides],
        client_factory=server.client,
        validator=RunConfigurationValidator(disk_usage=lambda _p: Usage(10**13, 0, 10**13)),
        termination=RunTerminationSequence(step_timeout_s=5.0, exit_now=lambda _s: None),
        monitor=SessionMonitor(stream=io.StringIO(), is_terminal=False),
        stdin_is_terminal=lambda: False, session_id=SESSION_ID)
    server.result_path = layout.runs_root / SESSION_ID / "run.result.json"
    return session.run()


def traffic_server(traffic=eastbound) -> FakeServer:
    server = FakeServer()
    server.traffic_at = traffic
    return server


def pose_of(transform) -> tuple[float, ...]:
    location, rotation = transform.location, transform.rotation
    return location.x, location.y, location.z, rotation.pitch, rotation.yaw


def expected_pose(aim: StareAim) -> tuple[float, ...]:
    return aim.x_m, aim.y_m, aim.z_m, aim.pitch_deg, aim.yaw_deg


def cameras(server: FakeServer):
    rgb = next(a for a in server.actors if a.type_id == "sensor.camera.rgb")
    depth = next((a for a in server.actors if a.type_id == "sensor.camera.depth"), None)
    return rgb, depth


def moves_of(server: FakeServer, actor_id: int, before: str | None = None) -> list[tuple]:
    log = server.events.log
    end = server.events.index(before) if before else len(log)
    return [event for event in log[:end] if event[0] == "move" and event[1] == actor_id]


# -- the point it resolves to --------------------------------------------------------------------------

def test_it_resolves_to_the_centre_of_the_bodies_on_the_last_frame_before_the_hold(layout):
    server = traffic_server()
    result = capture(layout, server)
    assert result.outcome == "run_finished"
    point = centre_at(LAST_FOLLOWED_FRAME_S)
    rgb, _ = cameras(server)
    assert pose_of(rgb.transform) == pytest.approx(expected_pose(aim_around(point)), abs=1e-6)
    [record] = result.produced["cameras"]
    assert record["form"] == "rendered_traffic"
    assert (record["look_at"]["x_m"], record["look_at"]["y_m"], record["look_at"]["z_m"]) == \
        pytest.approx(point, abs=1e-6)
    assert record["measured_on"]["vehicles"] == len(BODIES)
    assert record["measured_on"]["frame_s"] == pytest.approx(LAST_FOLLOWED_FRAME_S)
    assert (record["held_from_s"], record["held_before_the_window_s"]) == (HOLD_S, 7.0)
    assert record["pose"] == pytest.approx(
        dict(zip(("x_m", "y_m", "z_m", "pitch_deg", "yaw_deg"),
                 expected_pose(aim_around(point)), strict=True)), abs=1e-6)


def test_the_height_of_the_point_is_the_bodies_own(layout):
    server = traffic_server()
    result = capture(layout, server)
    [record] = result.produced["cameras"]
    assert record["look_at"]["z_m"] == pytest.approx(14.0)
    assert record["pose"]["z_m"] == pytest.approx(14.0 + A_TRAFFIC_STARE["stare_altitude_m"])


def test_a_last_frame_with_no_body_refuses_at_preroll_whatever_came_before(layout):
    # Bodies throughout the prewarm but the step before the hold, which renders only a pose with
    # no body.
    server = traffic_server(lambda at: eastbound(at) if at < HOLD_S - 1.0
                            else [(BODILESS[0], BODILESS[1:])])
    result = capture(layout, server)
    assert (result.outcome, result.closed_by) == ("refused_preroll", "aborted_at_preroll")
    assert "TRAFFIC-1" in result.detail and "rendered no vehicle" in result.detail
    assert "before the hold" in result.detail
    assert server.events.of("start_recording") == []
    assert server.session.disposed and all(actor.destroyed for actor in server.actors)


# -- following through the prewarm, holding through the window ------------------------------------

def test_the_camera_starts_over_the_render_region_s_centre(layout):
    server = traffic_server()
    result = capture(layout, server, overrides=[
        'capture.render_region={"x_m": 50.0, "y_m": -80.0, "radius_m": 400.0}'])
    rgb, depth = cameras(server)
    start = expected_pose(aim_around((50.0, -80.0, 0.0)))
    assert pose_of(rgb.spawned_at) == pytest.approx(start, abs=1e-6)
    assert pose_of(depth.spawned_at) == pytest.approx(start, abs=1e-6)
    placed = result.produced["cameras"][0]["placed_before_the_prewarm"]
    assert placed["look_at"]["source"] == "the render region's centre"
    assert tuple(placed["pose"].values()) == pytest.approx(start, abs=1e-6)


def test_the_camera_follows_the_traffic_until_the_hold_and_holds_the_rest(layout):
    server = traffic_server()
    result = capture(layout, server)
    rgb, depth = cameras(server)
    assert len(moves_of(server, rgb.id, before="start_recording")) == FOLLOWED_STEPS
    assert len(moves_of(server, rgb.id)) == FOLLOWED_STEPS, "the camera moved after its hold"
    assert len(moves_of(server, depth.id)) == FOLLOWED_STEPS
    assert pose_of(depth.transform) == pose_of(rgb.transform)
    # The last move came with the step that ended at the hold, and none after it.
    log = server.events.log
    last_move = max(i for i, event in enumerate(log) if event[0] == "move")
    assert [e[1] for e in log[:last_move] if e[0] == "advance"][-1] == HOLD_S
    [record] = result.produced["cameras"]
    assert record["moves_before_the_window"] == FOLLOWED_STEPS
    # The bodies drive a metre a step, so the view held is a metre from the one before it.
    assert record["last_move_m"] == pytest.approx(1.0)


def test_nothing_asks_about_the_view_while_the_camera_follows_the_traffic(layout):
    # The tiles' figures cover every registered view, and a moving camera's picture never reads
    # settled, so the wait begins when the camera holds.
    server = traffic_server()
    result = capture(layout, server)
    log = server.events.log
    first_ask = next(i for i, event in enumerate(log) if event[0] == "view_readiness")
    assert [e[1] for e in log[:first_ask] if e[0] == "advance"][-1] == HOLD_S + 1.0
    view = result.produced["readiness"]["channels"][0]
    assert view["wait_began"]["sim_time_s"] == HOLD_S
    assert view["state"] == "ready" and view["ready_at_window_open"] is True


def test_the_first_prewarm_step_already_moves_it_onto_the_traffic(layout):
    server = traffic_server()
    capture(layout, server)
    rgb, _ = cameras(server)
    first = moves_of(server, rgb.id)[0][2]
    assert pose_of(first) == pytest.approx(
        expected_pose(aim_around(centre_at(FIRST_RENDERED_S + 0.95))), abs=1e-6)


def test_a_one_step_prewarm_measures_the_traffic_but_leaves_no_frame_to_witness(layout):
    server = traffic_server()
    result = capture(layout, server, overrides=["capture.prewarm_s=1"])
    assert (result.outcome, result.refusals[0]["check"]) == ("refused_offline", 51)
    assert server.clients == []


@pytest.mark.parametrize(("prewarm", "outcome"), [("2", "refused_offline"),
                                                  ("3", "run_finished")])
def test_one_step_to_measure_and_one_to_compare_two_frames_after_the_first_ask(
        layout, prewarm, outcome):
    # One step followed and one held step before the tiles are first asked about; then a camera
    # rendering every ten ticks needs up to nineteen more for two frames ten ticks apart. Two
    # seconds leave none, three leave twenty.
    server = traffic_server()
    result = capture(layout, server, overrides=[f"capture.prewarm_s={prewarm}"])
    assert result.outcome == outcome
    if outcome == "refused_offline":
        assert result.refusals[0]["check"] == 51 and "at least 2.95 s" in             result.refusals[0]["message"]
        return
    rgb, _ = cameras(server)
    assert len(moves_of(server, rgb.id)) == 1
    assert result.produced["readiness"]["channels"][0]["picture"]["frames"] == 2


def test_a_stare_at_a_point_beside_it_is_never_moved_and_poses_are_asked_for(layout):
    server = traffic_server()
    result = capture(layout, server, channels=[A_TRAFFIC_STARE, A_STARE])
    assert result.outcome == "run_finished"
    rgbs = [a for a in server.actors if a.type_id == "sensor.camera.rgb"]
    assert len(moves_of(server, rgbs[0].id)) == FOLLOWED_STEPS
    assert moves_of(server, rgbs[1].id) == []
    [event] = server.events.of("start_sumo_drive")
    assert callable(event[4]["on_pose"])
    assert [c["form"] for c in result.produced["cameras"]] == ["rendered_traffic",
                                                                "look_at_point"]


# -- reproducible from the record ---------------------------------------------------------------------

def test_the_recorded_point_reproduces_the_view_as_a_look_at_stare(layout):
    server = traffic_server()
    result = capture(layout, server)
    [record] = result.produced["cameras"]
    replayed = {name: A_TRAFFIC_STARE[name] for name in
                ("sensor_id", "stare_altitude_m", "stare_standoff_m", "stare_bearing_deg")}
    replayed.update(record["as_look_at_point"])
    again = FakeServer()
    capture(layout, again, channels=[replayed])
    assert pose_of(cameras(again)[0].transform) == \
        pytest.approx(pose_of(cameras(server)[0].transform), abs=1e-9)


# -- what the launch says and refuses ---------------------------------------------------------------

def test_the_echo_says_where_it_will_look_is_not_predicted(layout):
    result = capture(layout, traffic_server())
    assert any("rendered traffic" in line for line in result.launch_echo["not_predicted"])


@pytest.mark.parametrize("prewarm", ["0", "0.5"])
def test_a_prewarm_shorter_than_a_sumo_step_is_refused_offline(layout, prewarm):
    server = traffic_server()
    result = capture(layout, server, overrides=[f"capture.prewarm_s={prewarm}"])
    assert (result.outcome, result.refusals[0]["check"]) == ("refused_offline", 47)
    assert "rendered traffic" in result.refusals[0]["message"]
    assert server.clients == []

