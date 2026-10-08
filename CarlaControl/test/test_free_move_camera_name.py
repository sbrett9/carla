"""The free-move camera is named as it is created, and takes the server's name when it is not.

`carla-free-camera` (`carlacontrol.commands.free_camera`, run from a checkout as
`CarlaControl/scripts/run_free_move_camera.py`) records nothing, but its picture camera is a camera in
the world like any other, and `--camera-name` names it as it is spawned: the name is set as its
role_name, which every client reads, and the server refuses one a live camera in the world holds,
before the window opens. Without one the server names it `Camera_<n>`. Its depth camera is given no
name. The world and the window are stood in for.
"""
from __future__ import annotations

import importlib
import sys
from pathlib import Path
from types import SimpleNamespace

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))


@pytest.fixture(scope="module")
def viewer():
    """The command's module. It imports carlanet and the rig, so it needs the assemblies."""
    pytest.importorskip("carlanet", reason="the viewer needs carlanet and its assemblies")
    return importlib.import_module("carlacontrol.commands.free_camera")


def _arguments(viewer, monkeypatch, *extra: str):
    monkeypatch.setattr(sys, "argv", ["run_free_move_camera.py", *extra])
    return viewer.parse_args()


def test_the_camera_has_no_name_unless_one_is_given(viewer, monkeypatch):
    assert _arguments(viewer, monkeypatch).camera_name is None
    assert _arguments(viewer, monkeypatch, "--camera-name", "NapOfEarth_2").camera_name == \
        "NapOfEarth_2"


@pytest.mark.parametrize("name", ["Nap Of Earth", "nap.2", "CON", "front", "Camera_9",
                                  "CARLA-SENSOR-9"])
def test_a_name_outside_the_rule_is_refused_before_anything_starts(viewer, monkeypatch, capsys,
                                                                  name):
    with pytest.raises(SystemExit) as exited:
        _arguments(viewer, monkeypatch, "--camera-name", name)
    assert exited.value.code == 2
    assert "such as Overwatch_1" in capsys.readouterr().err


class _Blueprint:
    def __init__(self, blueprint_id: str) -> None:
        self.id = blueprint_id
        self.values: dict[str, str] = {}

    def has_attribute(self, _name: str) -> bool:
        return True

    def set_attribute(self, name: str, value) -> None:
        self.values[name] = str(value)


class _Camera:
    def __init__(self, actor_id: int) -> None:
        self.id = actor_id

    def listen(self, _callback) -> None:
        pass

    def set_transform(self, _transform) -> None:
        pass


class _World:
    """A world the viewer's cameras are spawned into, where another camera may hold a name."""

    def __init__(self, held: str | None = None) -> None:
        self.held = held
        self.spawned: list[tuple[str, str | None]] = []
        self.names: dict[int, str] = {}

    def get_map(self):
        return SimpleNamespace(name="Gardnerville_Centerville_Lane")

    def get_blueprint_library(self):
        return SimpleNamespace(find=_Blueprint)

    def spawn_actor(self, blueprint, _transform, attach_to=None, attachment_type=None) -> _Camera:
        # The server names every camera spawned without a name, the depth camera included -- which is
        # spawned attached to the picture camera -- and refuses one a live camera holds.
        given = blueprint.values.get("role_name")
        if given is not None and self.held is not None and given.upper() == self.held.upper():
            raise RuntimeError(f"camera name '{given}' is already held in this world by camera 7 "
                               "(sensor.camera.rgb): two cameras under one name would write files "
                               "of one name and report under one callsign; choose another")
        self.spawned.append((blueprint.id, given))
        camera = _Camera(100 + len(self.spawned))
        self.names[camera.id] = given or f"Camera_{len(self.spawned)}"
        return camera

    def spawn_camera(self, blueprint, transform, name=None) -> _Camera:
        # As the shim spawns a camera: the server's refusal said as a ValueError.
        if name is not None:
            blueprint.set_attribute("role_name", name)
        try:
            return self.spawn_actor(blueprint, transform)
        except Exception as refused:
            if name is not None and "camera name '" in str(refused):
                raise ValueError(str(refused)) from None
            raise

    def camera_name(self, camera) -> str:
        """The name the camera holds on the server, as the shim reads it back."""
        return self.names[camera.id]

    def get_spectator(self) -> _Camera:
        return _Camera(0)


def test_the_picture_camera_is_created_under_its_name_and_the_depth_camera_under_none(
        viewer, monkeypatch):
    world = _World()
    rig = viewer.SensorRig(world, _arguments(viewer, monkeypatch, "--camera-name", "NapOfEarth_2"))
    assert world.spawned == [("sensor.camera.rgb", "NapOfEarth_2"), ("sensor.camera.depth", None)]
    assert rig.camera_name == "NapOfEarth_2"


def test_an_unnamed_picture_camera_takes_the_name_the_server_gives_it(viewer, monkeypatch):
    world = _World()
    rig = viewer.SensorRig(world, _arguments(viewer, monkeypatch))
    assert world.spawned[0] == ("sensor.camera.rgb", None)
    assert rig.camera_name == "Camera_1"


def test_a_name_another_camera_holds_ends_the_viewer_before_its_window_opens(viewer, monkeypatch,
                                                                            caplog):
    world = _World(held="napofearth_2")
    opened: list = []

    class _Client:
        def __init__(self, *_args) -> None:
            pass

        def set_timeout(self, _seconds) -> None:
            pass

        def get_world(self) -> _World:
            return world

        def get_server_version(self) -> str:
            return "stand-in"

    monkeypatch.setattr(viewer, "carla", SimpleNamespace(Client=_Client))
    monkeypatch.setattr(viewer, "PygameInterface", lambda **kwargs: opened.append(kwargs))
    monkeypatch.setattr(sys, "argv", ["run_free_move_camera.py", "--camera-name", "NapOfEarth_2"])

    with caplog.at_level("ERROR", logger="run_free_move_camera"):
        assert viewer.main() == 2
    assert opened == []
    assert world.spawned == []
    assert "--camera-name: camera name 'NapOfEarth_2' is already held in this world" in caplog.text
