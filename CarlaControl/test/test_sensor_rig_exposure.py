"""`--ev` sets the rig camera's exposure compensation, and says so where the server cannot take it.

The RGB camera publishes `exposure_compensation` beside its other exposure attributes since stage K
(08_Collection_And_EPoL.md D8.27), so `run_SCTMV.py --ev` and `run_free_move_camera.py --ev`, which
`SensorRig` reads, set it: EV added to the camera's exposure, which is otherwise the blueprint's own, the
Default profile's manual ISO 100, 1/320 s and f/4. A server built before the camera published its
exposure has no such attribute; there a value other than zero is not applied and the rig says so,
rather than leaving it unapplied in silence. The world is stood in for.
"""
from __future__ import annotations

import logging
import sys
from pathlib import Path
from types import SimpleNamespace

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

pytest.importorskip("carlacontrol", reason="carlacontrol needs the CarlaNet assemblies on this machine")

from carlacontrol.CarlaControlArgumentParser import CarlaControlArgumentParser  # noqa: E402
from carlacontrol.SensorRig import SensorRig  # noqa: E402


class _Blueprint:
    def __init__(self, blueprint_id: str, exposure: bool) -> None:
        self.id = blueprint_id
        self.exposure = exposure
        self.values: dict[str, str] = {}

    def has_attribute(self, name: str) -> bool:
        return name != "exposure_compensation" or self.exposure

    def set_attribute(self, name: str, value) -> None:
        assert self.has_attribute(name), name
        self.values[name] = str(value)


class _Camera:
    def __init__(self, actor_id: int) -> None:
        self.id = actor_id

    def listen(self, _callback) -> None:
        pass

    def set_transform(self, _transform) -> None:
        pass


class _World:
    """A world whose RGB camera publishes its exposure, or one built before it did."""

    def __init__(self, exposure: bool) -> None:
        self.exposure = exposure
        self.blueprints: dict[str, _Blueprint] = {}

    def get_blueprint_library(self):
        def find(blueprint_id: str) -> _Blueprint:
            self.blueprints[blueprint_id] = _Blueprint(blueprint_id, self.exposure)
            return self.blueprints[blueprint_id]
        return SimpleNamespace(find=find)

    def spawn_actor(self, _blueprint, _transform, attach_to=None, attachment_type=None) -> _Camera:
        return _Camera(102)

    def spawn_camera(self, _blueprint, _transform, name=None) -> _Camera:
        return _Camera(101)

    def camera_name(self, camera) -> str:
        return f"Camera_{camera.id}"

    def get_spectator(self) -> _Camera:
        return _Camera(0)


def _settings(ev) -> SimpleNamespace:
    return SimpleNamespace(x=0.0, y=0.0, z=1000.0, width=64, height=36, fov=90.0, ev=ev,
                           asynchronous=True)


def test_ev_defaults_to_no_compensation():
    assert CarlaControlArgumentParser(str(_REPO)).parse_args([]).ev == 0.0


def test_ev_sets_the_camera_s_exposure_compensation():
    world = _World(exposure=True)
    SensorRig(world, _settings(1.5))
    assert world.blueprints["sensor.camera.rgb"].values["exposure_compensation"] == "1.5"
    assert "exposure_compensation" not in world.blueprints["sensor.camera.depth"].values


def test_ev_on_a_server_whose_camera_publishes_no_exposure_is_said_and_not_applied(caplog):
    world = _World(exposure=False)
    with caplog.at_level(logging.WARNING):
        SensorRig(world, _settings(-2.0))
    assert "exposure_compensation" not in world.blueprints["sensor.camera.rgb"].values
    assert "--ev -2 is not applied" in caplog.text and "rebuild the server" in caplog.text


@pytest.mark.parametrize("ev", [0.0, None])
def test_no_compensation_asked_is_not_said_on_such_a_server(caplog, ev):
    with caplog.at_level(logging.WARNING):
        SensorRig(_World(exposure=False), _settings(ev))
    assert "exposure_compensation" not in caplog.text
