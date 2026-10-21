"""A camera follower spawns its camera under `--sensor-id`, so the server holds the camera by that name.

The shim's `world.spawn_camera` sends the name to the server, which refuses it where a live camera
already holds it, compared without regard to case: the spawn then raises ValueError with the server's
reason and nothing is spawned. A camera spawned without a name is named `Camera_<n>` by the server.
Either way the follower reads the name back from the server (`world.camera_name`) and shows that name
over its picture, the name every other client finds the camera by. A name the camera-name rule refuses
is refused at the command line, before a client is made.

The world, the client and the window are stand-ins, so neither a server nor a display is needed.
"""
from __future__ import annotations

import logging
import sys
import types
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

CameraFollower = pytest.importorskip(
    "carlacontrol.CameraFollower",
    reason="the follower needs carlanet, which needs the CarlaNet assemblies on this machine",
).CameraFollower

from carlacontrol.ChannelDescription import ChannelDescription  # noqa: E402
from carlacontrol.commands import camera_follower  # noqa: E402

A_NAME = "Gate_Orbit_1200ft"


def _stare(sensor_id: str | None) -> ChannelDescription:
    return ChannelDescription(sensor_id=sensor_id, stare_look_at_x_m=120.0,
                              stare_look_at_y_m=-340.0, stare_altitude_m=300.0)


class _Blueprint:
    def __init__(self, blueprint_id: str) -> None:
        self.id = blueprint_id
        self.attributes: dict[str, str] = {}

    def has_attribute(self, name: str) -> bool:
        return name in ("image_size_x", "image_size_y", "fov", "role_name")

    def set_attribute(self, name: str, value: str) -> None:
        self.attributes[name] = value


class _Library:
    def find(self, blueprint_id: str) -> _Blueprint:
        return _Blueprint(blueprint_id)


class _Camera:
    def __init__(self, actor_id: int, name: str) -> None:
        self.id = actor_id
        self.held_name = name
        self.callback = None
        self.destroyed = False

    def listen(self, callback) -> None:
        self.callback = callback

    def stop(self) -> None:
        self.callback = None

    def destroy(self) -> bool:
        self.destroyed = True
        return True


class _Settings:
    synchronous_mode = True


class _World:
    """A world whose server names cameras as CARLA's does, with one camera another client spawned."""

    def __init__(self) -> None:
        self.others = [_Camera(9, A_NAME)]
        self.spawned: list[_Camera] = []
        self.names_asked: list[str | None] = []
        self.names_read: list[int] = []
        self.next_server_name = 7
        self.name_unreadable = False

    def get_blueprint_library(self) -> _Library:
        return _Library()

    def get_settings(self) -> _Settings:
        return _Settings()

    def spawn_camera(self, blueprint: _Blueprint, transform, name: str | None = None) -> _Camera:
        self.names_asked.append(name)
        if name is not None:
            for held in [*self.others, *self.spawned]:
                if not held.destroyed and held.held_name.upper() == name.upper():
                    # The shim raises the server's refusal of a name as ValueError, nothing spawned.
                    raise ValueError(
                        f"camera name '{name}' is already held in this world by camera {held.id} "
                        "(sensor.camera.rgb): two cameras under one name would write files of one "
                        "name and report under one callsign; choose another")
        else:
            name = f"Camera_{self.next_server_name}"
            self.next_server_name += 1
        camera = _Camera(100 + len(self.spawned), name)
        self.spawned.append(camera)
        return camera

    def camera_name(self, camera: _Camera) -> str:
        self.names_read.append(camera.id)
        if self.name_unreadable:
            raise RuntimeError("camera names are ruled by CarlaNet.Recording, which is not loaded")
        return camera.held_name


class _Client:
    def __init__(self, world: _World) -> None:
        self.world = world

    def get_world(self) -> _World:
        return self.world


class _Image:
    def __init__(self, frame: int, timestamp: float) -> None:
        self.frame = frame
        self.timestamp = timestamp


class _Window:
    """Delivers one frame to the follower's camera, then asks to quit."""

    def __init__(self, world: _World) -> None:
        self.world = world
        self.opened = False
        self.closed = False
        self.loops = 0
        self.shown: list[list[str]] = []

    def open(self, width: int, height: int, title: str) -> None:
        self.opened = True

    def quit_requested(self) -> bool:
        self.loops += 1
        if self.loops == 1:
            self.world.spawned[0].callback(_Image(4812, 240.55))
            return False
        return True

    def show_frame(self, image: _Image, overlay: list[str]) -> None:
        self.shown.append(list(overlay))

    def show_message(self, lines: list[str]) -> None:
        pass

    def pace(self) -> None:
        pass

    def close(self) -> None:
        self.closed = True


def _follower(channel: ChannelDescription) -> tuple[CameraFollower, _World, _Window]:
    world = _World()
    window = _Window(world)
    return CameraFollower(_Client(world), channel, window=window), world, window


def test_the_camera_is_spawned_under_the_sensor_id_and_labelled_by_the_name_the_server_holds():
    follower, world, window = _follower(_stare("Overwatch_1"))

    assert follower.run() == 0

    assert world.names_asked == ["Overwatch_1"]
    [camera] = world.spawned
    assert world.names_read == [camera.id]
    assert follower.camera.name == "Overwatch_1"
    assert window.shown == [["Overwatch_1   stare", "frame 4812   sim t 240.550 s"]]
    assert camera.destroyed


def test_without_a_sensor_id_the_camera_is_labelled_by_the_name_the_server_gave_it():
    follower, world, window = _follower(_stare(None))

    assert follower.run() == 0

    assert world.names_asked == [None]
    assert follower.camera.name == "Camera_7"
    assert window.shown == [["Camera_7   stare", "frame 4812   sim t 240.550 s"]]


def test_a_name_a_live_camera_holds_is_refused_and_the_follower_ends_saying_why(caplog):
    follower, world, window = _follower(_stare(A_NAME.lower()))

    with caplog.at_level(logging.ERROR):
        assert follower.run() == 1

    assert f"camera name '{A_NAME.lower()}' is already held in this world by camera 9" in caplog.text
    assert world.spawned == []
    assert not any(other.destroyed for other in world.others)
    assert window.opened and window.closed
    assert window.shown == []


def test_a_camera_whose_name_cannot_be_read_back_is_removed_before_the_follower_ends(caplog):
    follower, world, window = _follower(_stare(None))
    world.name_unreadable = True

    with caplog.at_level(logging.ERROR):
        assert follower.run() == 1

    [camera] = world.spawned
    assert camera.destroyed
    assert not any(other.destroyed for other in world.others)
    assert "CarlaNet.Recording" in caplog.text
    assert window.closed


@pytest.mark.parametrize(("name", "reason"), [
    ("Gate.Orbit", "holds '.'"),
    ("DECK:I25", "holds ':'"),
    ("Camera_3", "has the form the server gives every camera spawned without one"),
    ("front", "is a role name the server gives sensors"),
])
def test_a_name_the_rule_refuses_is_refused_before_a_client_is_made(monkeypatch, caplog, name,
                                                                     reason):
    clients_made: list[tuple] = []
    monkeypatch.setattr(camera_follower, "carla",
                        types.SimpleNamespace(Client=lambda *given: clients_made.append(given)))

    with caplog.at_level(logging.ERROR):
        status = camera_follower.main(["--sensor-id", name, "--stare-look-at", "0", "0"])

    assert status == 2
    assert clients_made == []
    assert f"camera name '{name}' {reason}" in caplog.text
