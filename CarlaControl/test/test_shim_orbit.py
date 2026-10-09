"""The shim's orbit calls carry the circle to the server once, and say plainly when it cannot fly one.

`Sensor.set_orbit`, `set_orbit_enabled`, `set_orbit_paused` and `get_orbit_state` are the client's
side of the server's orbit mover (`CarlaServer.cpp` `set_orbit`; `UOrbitMoverComponent`). Each is one
call through `CarlaClient`, the circle packed as `CarlaNet.Types.Rpc.Orbit.OrbitParameters`; a server
that binds no such call -- one built before the mover -- is said as `carlanet.OrbitNotOnServerError`,
a RuntimeError in plain words, and nothing in the shim moves the camera in its place.

The shim under test is this tree's, loaded by path under its own module name so the installed
`carlanet` is not what answers, over a stand-in client that records what it was sent. It needs the
CarlaNet assemblies (`CARLANET_PUBLISH_DIR`), as the shim does.
"""
from __future__ import annotations

import importlib.util
import math
import os
import sys
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
_SHIM = _REPO / "CarlaNet" / "python" / "carlanet" / "__init__.py"

pytest.importorskip("clr_loader", reason="the shim loads the CarlaNet assemblies through pythonnet")
pytest.importorskip("pythonnet", reason="the shim loads the CarlaNet assemblies through pythonnet")


@pytest.fixture(scope="module")
def shim():
    """This tree's shim, loaded once for the module."""
    if not os.environ.get("CARLANET_PUBLISH_DIR") and not (_SHIM.parent / "dlls").is_dir():
        pytest.skip("the shim needs the CarlaNet assemblies: set CARLANET_PUBLISH_DIR to a publish")
    name = "carlanet_under_test"
    if name in sys.modules:
        return sys.modules[name]
    spec = importlib.util.spec_from_file_location(name, _SHIM)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    try:
        from CarlaNet.Types.Rpc.Orbit import OrbitParameters  # noqa: F401
    except ImportError:
        pytest.skip("the loaded CarlaNet.Types predates the orbit mover; set CARLANET_PUBLISH_DIR to "
                    "a fresh publish")
    return module


class _Answer:
    """A .NET Task as the shim waits on one: the answer, or the server's refusal raised."""

    def __init__(self, result=None, refusal: Exception | None = None) -> None:
        self._result = result
        self._refusal = refusal

    def GetAwaiter(self):  # noqa: N802 -- the .NET member name
        return self

    def GetResult(self):  # noqa: N802 -- the .NET member name
        if self._refusal is not None:
            raise self._refusal
        return self._result


class _Client:
    """A stand-in for the C# CarlaClient: the four orbit calls, recorded, answered as the server
    answers them -- or refused as a server built before the mover refuses them."""

    def __init__(self, flies_orbits: bool = True) -> None:
        self.flies_orbits = flies_orbits
        self.calls: list[tuple] = []
        self.state = None
        self.fails_with: Exception | None = None

    def _answer(self, call: str, result=None) -> _Answer:
        if not self.flies_orbits:
            return _Answer(refusal=RuntimeError(
                f"rpclib: server could not find function '{call}' with argument count 3."))
        if self.fails_with is not None:
            return _Answer(refusal=self.fails_with)
        return _Answer(result)

    def SetOrbitAsync(self, actor_id, parameters):  # noqa: N802 -- the .NET member name
        self.calls.append(("set_orbit", int(actor_id), parameters))
        return self._answer("set_orbit")

    def SetOrbitEnabledAsync(self, actor_id, enabled):  # noqa: N802 -- the .NET member name
        self.calls.append(("set_orbit_enabled", int(actor_id), bool(enabled)))
        return self._answer("set_orbit_enabled")

    def SetOrbitPausedAsync(self, actor_id, paused):  # noqa: N802 -- the .NET member name
        self.calls.append(("set_orbit_paused", int(actor_id), bool(paused)))
        return self._answer("set_orbit_paused")

    def GetOrbitStateAsync(self, actor_id):  # noqa: N802 -- the .NET member name
        self.calls.append(("get_orbit_state", int(actor_id)))
        return self._answer("get_orbit_state", self.state)


def _camera(shim, client: _Client, actor_id: int = 4121):
    """A sensor.camera.rgb actor as the shim wraps one the server spawned."""
    from CarlaNet.Types.Geom import BoundingBox

    values = shim._cs_list(
        [shim.ActorAttributeValue("role_name", shim.ActorAttributeType.String, "Overwatch_1")],
        shim.ActorAttributeValue)
    description = shim.ActorDescription(17, "sensor.camera.rgb", values)
    return shim._wrap_actor(shim._Actor(actor_id, 0, description, BoundingBox(), b"", bytes(24)),
                            client)


def test_a_camera_is_a_sensor_and_carries_the_orbit_calls(shim):
    camera = _camera(shim, _Client())
    assert isinstance(camera, shim.Sensor)
    for name in ("set_orbit", "set_orbit_enabled", "set_orbit_paused", "get_orbit_state"):
        assert callable(getattr(camera, name))


def test_set_orbit_sends_the_circle_once_in_carla_metres_moving_by_default(shim):
    client = _Client()
    camera = _camera(shim, client)

    camera.set_orbit(shim.Location(x=50.0, y=-80.0, z=0.0), 200.0, 518.2, 240.0)

    [(call, actor_id, parameters)] = client.calls
    assert (call, actor_id) == ("set_orbit", 4121)
    assert (parameters.CentreXMetres, parameters.CentreYMetres, parameters.CentreZMetres) == \
        (50.0, -80.0, 0.0)
    assert (parameters.RadiusMetres, parameters.AltitudeMetres, parameters.PeriodSeconds) == \
        (200.0, 518.2, 240.0)
    assert (parameters.Clockwise, parameters.StartAngleRadians) == (True, 0.0)
    assert (parameters.PitchOverridden, parameters.PitchDegrees) == (False, 0.0)
    assert parameters.Enabled is True


def test_set_orbit_carries_a_held_start_a_direction_a_start_angle_and_a_pitch_override(shim):
    client = _Client()
    camera = _camera(shim, client)

    camera.set_orbit((1.0, 2.0, 3.0), 10.0, 20.0, 60.0, clockwise=False, start_angle=1.5,
                     pitch=-30.0, enabled=False)

    [(_, _, parameters)] = client.calls
    assert (parameters.CentreXMetres, parameters.CentreYMetres, parameters.CentreZMetres) == \
        (1.0, 2.0, 3.0)
    assert (parameters.Clockwise, parameters.StartAngleRadians) == (False, 1.5)
    assert (parameters.PitchOverridden, parameters.PitchDegrees) == (True, -30.0)
    assert parameters.Enabled is False
    # The circle's angle after an elapsed time and its pose are the server's rule, carried on the
    # same type: anticlockwise, a quarter period back from 1.5 rad.
    assert parameters.AngleAfter(15.0) == pytest.approx((1.5 - math.pi / 2.0) % (2.0 * math.pi))
    pose = parameters.PoseAt(0.0)
    assert (pose.Location.X, pose.Location.Y, pose.Location.Z) == pytest.approx((11.0, 2.0, 23.0))
    assert pose.Rotation.Pitch == pytest.approx(-30.0)


def test_enable_pause_and_state_are_one_call_each(shim):
    from CarlaNet.Types.Rpc.Orbit import OrbitState

    client = _Client()
    client.state = OrbitState(1.25, True, False)
    camera = _camera(shim, client)

    camera.set_orbit_enabled(True)
    camera.set_orbit_paused(True)
    camera.set_orbit_paused(False)
    camera.set_orbit_enabled(False)
    state = camera.get_orbit_state()

    assert client.calls == [("set_orbit_enabled", 4121, True), ("set_orbit_paused", 4121, True),
                            ("set_orbit_paused", 4121, False), ("set_orbit_enabled", 4121, False),
                            ("get_orbit_state", 4121)]
    assert isinstance(state, shim.OrbitState)
    assert (state.angle, state.enabled, state.paused) == (1.25, True, False)
    assert "angle=1.250000" in repr(state)


def test_a_server_built_before_the_orbit_mover_is_said_plainly_and_nothing_moves(shim):
    client = _Client(flies_orbits=False)
    camera = _camera(shim, client)

    with pytest.raises(shim.OrbitNotOnServerError) as refused:
        camera.set_orbit(shim.Location(x=0.0, y=0.0, z=0.0), 100.0, 100.0, 60.0)

    said = str(refused.value)
    assert said.startswith("this server cannot fly an orbit: it binds no set_orbit")
    assert "Rebuild the plugin" in said
    assert "Nothing in the client moves the camera in the server's place" in said
    assert "could not find function 'set_orbit'" in said
    assert isinstance(refused.value, RuntimeError)
    # Every other orbit call is refused the same way.
    with pytest.raises(shim.OrbitNotOnServerError, match="binds no set_orbit_enabled"):
        camera.set_orbit_enabled(True)
    with pytest.raises(shim.OrbitNotOnServerError, match="binds no set_orbit_paused"):
        camera.set_orbit_paused(True)
    with pytest.raises(shim.OrbitNotOnServerError, match="binds no get_orbit_state"):
        camera.get_orbit_state()
    assert [call[0] for call in client.calls] == ["set_orbit", "set_orbit_enabled",
                                                   "set_orbit_paused", "get_orbit_state"]


def test_any_other_refusal_comes_through_in_the_servers_words(shim):
    client = _Client()
    client.fails_with = RuntimeError(
        "Responding error from function set_orbit: Actor could not be found in the registry. "
        "Actor Id: 4121")
    camera = _camera(shim, client)

    with pytest.raises(RuntimeError, match="Actor could not be found") as failure:
        camera.set_orbit(shim.Location(x=0.0, y=0.0, z=0.0), 100.0, 100.0, 60.0)
    assert not isinstance(failure.value, shim.OrbitNotOnServerError)
    # A circle the server would refuse -- a radius or period that is not positive -- is refused by
    # CarlaClient before it is sent (OrbitRpcTests); the stand-in here is the client, so that path is
    # the C# tests' to hold.
