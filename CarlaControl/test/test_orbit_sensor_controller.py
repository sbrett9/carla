"""The orbit controller declares a circle to the server once and sends nothing per frame.

Issue #37: the orbit used to be a client thread pushing a pose about fifty times a second on the wall
clock, measured to halve the server's tick rate and to turn the camera as far per captured frame as a
synchronous run's pace was high. `OrbitSensorController` now gives the server's orbit mover the circle
through `Sensor.set_orbit`, pauses and resumes with one call each, turns the orbit off before any
manual move, and predicts the angle it shows from the parameters and the simulated clock. These tests
drive it against a stand-in camera that records every call, a stand-in rig and a stand-in flight
controller, with a simulated clock the test moves by hand. No server is contacted and no thread runs.
"""
from __future__ import annotations

import logging
import math
import sys
from pathlib import Path
from types import SimpleNamespace

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

pytest.importorskip("carlacontrol", reason="carlacontrol needs the CarlaNet assemblies on this machine")

import carlanet as carla  # noqa: E402

from carlacontrol.OrbitSensorController import OrbitSensorController  # noqa: E402
from carlacontrol.Pose import Pose  # noqa: E402

TWO_PI = 2.0 * math.pi


class _Camera:
    """The shim's Sensor as the controller uses it: the orbit calls, recorded, and a pose."""

    def __init__(self, flies_orbits: bool = True) -> None:
        self.calls: list[tuple] = []
        self.flies_orbits = flies_orbits
        self.transform = carla.Transform(carla.Location(x=1.0, y=2.0, z=300.0),
                                         carla.Rotation(pitch=-45.0, yaw=10.0, roll=0.0))

    def _refuse_if_older(self, call: str) -> None:
        if not self.flies_orbits:
            raise RuntimeError(f"this server cannot fly an orbit: it binds no {call}, so it was built "
                               "before the Carla plugin carried the orbit mover")

    def set_orbit(self, centre, radius, altitude, period, *, clockwise=True, start_angle=0.0,
                  pitch=None, enabled=True) -> None:
        self._refuse_if_older("set_orbit")
        self.calls.append(("set_orbit", (centre.x, centre.y, centre.z), radius, altitude, period,
                           clockwise, start_angle, pitch, enabled))

    def set_orbit_enabled(self, enabled: bool) -> None:
        self._refuse_if_older("set_orbit_enabled")
        self.calls.append(("set_orbit_enabled", enabled))

    def set_orbit_paused(self, paused: bool) -> None:
        self._refuse_if_older("set_orbit_paused")
        self.calls.append(("set_orbit_paused", paused))

    def get_orbit_state(self):
        self.calls.append(("get_orbit_state",))
        return SimpleNamespace(angle=0.0, enabled=False, paused=False)

    def get_transform(self):
        return self.transform

    def set_transform(self, transform) -> None:
        self.calls.append(("set_transform", transform))


class _Rig:
    """A SensorRig as the controller uses it: its colour camera, and the moves by hand."""

    FT_PER_M = 3.28084

    def __init__(self, camera: _Camera) -> None:
        self.camera = camera
        self.moves: list = []

    def set_transform(self, transform) -> None:
        self.moves.append(transform)

    def set_pose(self, pose: Pose) -> None:
        self.moves.append(pose.to_carla_transform())

    def get_current_transform(self):
        return self.camera.transform

    def get_position(self) -> Pose:
        return Pose.from_carla_transform(self.camera.transform)

    def get_initial_pose(self) -> Pose:
        return Pose(x=0.0, y=0.0, z=100.0, pitch=-90.0, yaw=0.0)


class _Flight:
    def __init__(self) -> None:
        self.pose = Pose(x=0.0, y=0.0, z=0.0, pitch=0.0, yaw=0.0)


class _Clock:
    def __init__(self, now: float = 1000.0) -> None:
        self.now = now

    def __call__(self) -> float:
        return self.now


def _controller(camera: _Camera | None = None, flight: _Flight | None = None,
                clock: _Clock | None = None, **params) -> tuple[OrbitSensorController, _Camera, _Clock]:
    camera = camera or _Camera()
    clock = clock or _Clock()
    controller = OrbitSensorController(_Rig(camera), world=None, flight_controller=flight,
                                       logger=logging.getLogger("orbit-test"), clock=clock)
    controller.set_orbit_params(center_x=50.0, center_y=-80.0, center_z=0.0, radius=200.0,
                                altitude=518.2, speed=240.0, **params)
    return controller, camera, clock


def _orbit_calls(camera: _Camera) -> list[tuple]:
    return [call for call in camera.calls if call[0].startswith("set_orbit")
            or call[0] == "get_orbit_state"]


# -- enabling sends the circle once ----------------------------------------------------------------------

def test_enabling_sends_the_circle_once_and_nothing_per_frame_afterwards():
    controller, camera, clock = _controller()

    controller.set_enabled(True)
    for _ in range(200):
        clock.now += 0.05
        controller.get_hud_info()
        controller.current_angle()

    assert controller.orbit_enabled and not controller.orbit_paused
    assert camera.calls == [("set_orbit", (50.0, -80.0, 0.0), 200.0, 518.2, 240.0, True, 0.0, None,
                             True)]


def test_the_angle_shown_is_predicted_from_the_simulated_clock_and_never_asked():
    controller, camera, clock = _controller()
    controller.set_enabled(True)

    clock.now += 60.0
    hud = controller.get_hud_info()

    # A quarter of 240 s is a quarter turn, by the clock the test moved and nothing else.
    assert controller.current_angle() == pytest.approx(math.pi / 2.0)
    assert hud["angle"] == pytest.approx(math.pi / 2.0)
    assert hud["orbit_progress"] == pytest.approx(25.0)
    assert ("get_orbit_state",) not in camera.calls
    # And the predicted pose is the shared rule's at that angle: south of the centre, looking north.
    predicted = controller.predicted_transform()
    assert (predicted.location.x, predicted.location.y) == pytest.approx((50.0, 120.0))
    assert predicted.rotation.yaw == pytest.approx(-90.0)


def test_the_angle_runs_the_other_way_anticlockwise_and_wraps():
    controller, _camera, clock = _controller(clockwise=False, angle=0.5)
    controller.set_enabled(True)

    clock.now += 60.0

    assert controller.current_angle() == pytest.approx((0.5 - math.pi / 2.0) % TWO_PI)


def test_a_simulated_second_is_a_second_of_orbit_whatever_the_wall_clock_did():
    # 2.7 simulated seconds at pace 2.7 took one wall second; the orbit still advanced 2.7 s of angle.
    controller, _camera, clock = _controller()
    controller.set_enabled(True)

    clock.now += 2.7

    assert controller.current_angle() == pytest.approx(TWO_PI / 240.0 * 2.7)


# -- pause and resume ------------------------------------------------------------------------------------

def test_pause_and_resume_are_one_call_each_and_the_angle_holds_meanwhile():
    controller, camera, clock = _controller()
    controller.set_enabled(True)
    clock.now += 60.0

    controller.toggle_orbit_pause()
    clock.now += 100.0
    held = controller.current_angle()
    controller.toggle_orbit_pause()
    clock.now += 60.0

    assert held == pytest.approx(math.pi / 2.0)
    assert controller.current_angle() == pytest.approx(math.pi)
    assert _orbit_calls(camera)[1:] == [("set_orbit_paused", True), ("set_orbit_paused", False)]
    assert not controller.orbit_paused


def test_pausing_an_orbit_that_is_off_does_nothing():
    controller, camera, _clock = _controller()

    controller.toggle_orbit_pause()

    assert camera.calls == [] and not controller.orbit_paused


# -- turning off, and manual moves ---------------------------------------------------------------------

def test_turning_off_is_one_call_and_hands_the_cameras_pose_to_the_flight_controller():
    flight = _Flight()
    controller, camera, clock = _controller(flight=flight)
    controller.set_enabled(True)
    clock.now += 30.0

    controller.set_enabled(False)

    assert not controller.orbit_enabled
    assert _orbit_calls(camera)[-1] == ("set_orbit_enabled", False)
    # The angle is kept where the orbit was predicted to be, so turning it on again starts there.
    assert controller.angle == pytest.approx(math.pi / 4.0)
    assert (flight.pose.x, flight.pose.y, flight.pose.z) == (1.0, 2.0, 300.0)
    assert (flight.pose.pitch, flight.pose.yaw) == (-45.0, 10.0)


def test_turning_on_again_starts_the_server_from_the_angle_it_was_turned_off_at():
    controller, camera, clock = _controller()
    controller.set_enabled(True)
    clock.now += 30.0
    controller.set_enabled(False)

    controller.set_enabled(True)

    sent = [call for call in camera.calls if call[0] == "set_orbit"]
    assert len(sent) == 2 and sent[1][6] == pytest.approx(math.pi / 4.0)


def test_a_manual_move_turns_the_servers_orbit_off_first():
    controller, camera, _clock = _controller()
    controller.set_enabled(True)
    rig = controller.sensors

    controller.set_object_transform(carla.Transform(carla.Location(x=5.0, y=6.0, z=7.0),
                                                    carla.Rotation()))

    assert not controller.orbit_enabled
    names = [call[0] for call in camera.calls]
    assert names == ["set_orbit", "set_orbit_enabled"]
    assert camera.calls[1] == ("set_orbit_enabled", False)
    assert len(rig.moves) == 1

    controller.move_object_to_position(Pose(x=1.0, y=1.0, z=1.0, pitch=0.0, yaw=0.0))
    # Already off: nothing more is said to the server.
    assert [call[0] for call in camera.calls] == names
    assert len(rig.moves) == 2


# -- holding for a capture's pre-roll -----------------------------------------------------------------

def test_holding_gives_the_server_the_circle_not_moving_and_enabling_is_then_one_switch():
    controller, camera, _clock = _controller()

    controller.hold_on_server()
    assert not controller.orbit_enabled
    controller.set_enabled(True)

    assert camera.calls == [
        ("set_orbit", (50.0, -80.0, 0.0), 200.0, 518.2, 240.0, True, 0.0, None, False),
        ("set_orbit_enabled", True)]


def test_a_circle_changed_after_holding_is_sent_whole_when_enabled():
    controller, camera, _clock = _controller()
    controller.hold_on_server()

    controller.set_orbit_params(radius=300.0)
    controller.set_enabled(True)

    assert [call[0] for call in camera.calls] == ["set_orbit", "set_orbit"]
    assert camera.calls[1][2] == 300.0 and camera.calls[1][8] is True


def test_holding_on_a_server_that_cannot_fly_an_orbit_raises_the_servers_refusal():
    controller, _camera, _clock = _controller(camera=_Camera(flies_orbits=False))

    with pytest.raises(RuntimeError, match="this server cannot fly an orbit: it binds no set_orbit"):
        controller.hold_on_server()
    assert not controller.orbit_enabled


# -- refusals -------------------------------------------------------------------------------------------

def test_a_server_that_cannot_fly_an_orbit_is_said_plainly_and_the_orbit_stays_off(caplog):
    controller, camera, clock = _controller(camera=_Camera(flies_orbits=False))

    with caplog.at_level(logging.ERROR, logger="orbit-test"):
        controller.set_enabled(True)
    clock.now += 60.0

    assert not controller.orbit_enabled
    assert "orbit refused: this server cannot fly an orbit: it binds no set_orbit" in caplog.text
    # Nothing in the process flies the camera in the server's place.
    assert controller.sensors.moves == [] and camera.calls == []
    assert controller.current_angle() == 0.0
    assert controller.get_hud_info() == {"orbit_enabled": False, "orbit_paused": False}


def test_a_wheel_built_before_the_orbit_mover_is_said_plainly(caplog):
    class _OlderCamera:
        """A Sensor from a wheel with no set_orbit."""
        transform = carla.Transform()

        def get_transform(self):
            return self.transform

    rig = _Rig(_OlderCamera())  # type: ignore[arg-type]
    controller = OrbitSensorController(rig, world=None, logger=logging.getLogger("orbit-test"),
                                       clock=_Clock())
    controller.set_orbit_params(center_x=0.0, center_y=0.0, radius=10.0, altitude=10.0, speed=60.0)

    with caplog.at_level(logging.ERROR, logger="orbit-test"):
        controller.set_enabled(True)

    assert not controller.orbit_enabled
    assert "the carlanet wheel in use has no Sensor.set_orbit" in caplog.text
    assert rig.moves == []


def test_the_camera_the_server_flies_is_the_rigs_colour_camera_a_followers_actor_or_the_camera_itself():
    camera = _Camera()
    assert OrbitSensorController._camera_of(_Rig(camera)) is camera
    assert OrbitSensorController._camera_of(SimpleNamespace(actor=camera)) is camera
    assert OrbitSensorController._camera_of(camera) is camera


# -- the pose rule --------------------------------------------------------------------------------------

def test_the_pose_rule_puts_the_camera_on_the_circle_with_the_boresight_on_the_centre():
    pose = OrbitSensorController.orbit_transform(50.0, -80.0, 0.0, 200.0, 518.2, 0.0)
    assert (pose.location.x, pose.location.y, pose.location.z) == pytest.approx((250.0, -80.0, 518.2))
    assert abs(pose.rotation.yaw) == pytest.approx(180.0)
    assert pose.rotation.pitch == pytest.approx(-math.degrees(math.atan2(518.2, 200.0)))
    assert pose.rotation.roll == 0.0
