"""The shim reads a camera's name back from the server, and refuses to record under any other.

The server issues the name of a camera spawned without one, `Camera_<n>`, and refuses a name a live
camera holds (`CarlaServer.cpp`, `SettleCameraName`). The shim (`CarlaNet/python/carlanet`) sends a
chosen name as the camera's `role_name`, refuses before the round trip only what the rule refuses,
and reads the name the server settled back from the spawned actor (`World.camera_name`), so every
process holds the same name for one camera; a recorder is started under that name and no other. A
server built before it named cameras hands an unnamed camera back with its blueprint's role name, and
the shim says so once and calls the camera `CARLA-SENSOR-<actor id>`.

The shim under test is this tree's, loaded by path under its own module name so the installed
`carlanet` is not what answers, over a stand-in client whose `SpawnActorAsync` answers as the server
does. It needs the CarlaNet assemblies (`CARLANET_PUBLISH_DIR`), as the shim does.
"""
from __future__ import annotations

import importlib.util
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
    spec = importlib.util.spec_from_file_location("carlanet_under_test", _SHIM)
    module = importlib.util.module_from_spec(spec)
    sys.modules["carlanet_under_test"] = module
    spec.loader.exec_module(module)
    if not module._CARLANET_RECORDING_AVAILABLE:
        pytest.skip("CarlaNet.Recording is not in the publish the shim loaded")
    from CarlaNet.Recording import CameraName
    if not hasattr(CameraName, "Of"):
        pytest.skip("the loaded CarlaNet.Recording predates server-issued camera names; set "
                    "CARLANET_PUBLISH_DIR to a fresh publish")
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
    """A stand-in for the C# CarlaClient: spawn_actor answered as the server answers it."""

    BLUEPRINT_DEFAULT_ROLE = "front"

    def __init__(self, shim, names_cameras: bool = True) -> None:
        self.shim = shim
        self.names_cameras = names_cameras
        self.spawned: list[dict] = []
        self.destroyed: list[int] = []
        self.fails_with: Exception | None = None
        self._next_actor = 4120
        self._cameras_named = 0
        self._live: dict[int, dict] = {}

    def SpawnActorAsync(self, description, transform):  # noqa: N802 -- the .NET member name
        attributes = {str(description.Attributes[i].Id): str(description.Attributes[i].Value)
                      for i in range(description.Attributes.Count)}
        self.spawned.append({"id": str(description.Id), "attributes": dict(attributes)})
        if self.fails_with is not None:
            return _Answer(refusal=self.fails_with)
        blueprint = str(description.Id)
        if blueprint.startswith("sensor.camera."):
            given = attributes.get("role_name", "")
            if given in ("", self.BLUEPRINT_DEFAULT_ROLE):
                if self.names_cameras:
                    self._cameras_named += 1
                    attributes["role_name"] = f"Camera_{self._cameras_named}"
            elif given.upper().startswith("CAMERA_") and given[len("Camera_"):].isdigit():
                return _Answer(refusal=RuntimeError(
                    f"camera name '{given}' has the form the server gives every camera spawned "
                    "without one, Camera_<n>, which a client cannot claim; choose another, such as "
                    "Overwatch_1"))
            else:
                for actor_id, held in self._live.items():
                    if held["id"].startswith("sensor.camera.") and \
                            held["attributes"].get("role_name", "").upper() == given.upper():
                        return _Answer(refusal=RuntimeError(
                            f"camera name '{given}' is already held in this world by camera "
                            f"{actor_id} ({held['id']}): two cameras under one name would write "
                            "files of one name and report under one callsign; choose another"))
        self._next_actor += 1
        self._live[self._next_actor] = {"id": blueprint, "attributes": attributes}
        return _Answer(self._actor(self._next_actor, blueprint, attributes))

    def DestroyActorAsync(self, actor_id):  # noqa: N802 -- the .NET member name
        self.destroyed.append(int(actor_id))
        self._live.pop(int(actor_id), None)
        return _Answer(True)

    def _actor(self, actor_id: int, blueprint: str, attributes: dict[str, str]):
        shim = self.shim
        values = shim._cs_list(
            [shim.ActorAttributeValue(key, shim.ActorAttributeType.String, value)
             for key, value in attributes.items()],
            shim.ActorAttributeValue)
        description = shim.ActorDescription(17, blueprint, values)
        from CarlaNet.Types.Geom import BoundingBox
        return shim._Actor(actor_id, 0, description, BoundingBox(), b"", bytes(24))


def _rgb_blueprint(shim):
    """A sensor.camera.rgb blueprint as the shim builds one from the server's definition: every
    attribute with the value the definition carries, role_name's being the first recommended."""
    blueprint = shim.ActorBlueprint.__new__(shim.ActorBlueprint)
    blueprint._def = None
    blueprint._uid = 17
    blueprint._id = "sensor.camera.rgb"
    blueprint._tags = "sensor,camera,rgb"
    string = int(shim.ActorAttributeType.String)
    blueprint._attrs = {
        "image_size_x": {"type": int(shim.ActorAttributeType.Int), "value": "640",
                         "recommended": ["640"], "modifiable": True},
        "role_name": {"type": string, "value": "front",
                      "recommended": ["front", "back", "left", "right"], "modifiable": True},
    }
    return blueprint


def _anywhere(shim):
    return shim.Transform(shim.Location(0.0, 0.0, 100.0), shim.Rotation(-90.0, 0.0, 0.0))


def test_a_camera_spawned_with_no_name_takes_the_name_the_server_gives_it(shim, capsys):
    client = _Client(shim)
    world = shim.World(client)

    first = world.spawn_camera(_rgb_blueprint(shim), _anywhere(shim))
    second = world.spawn_camera(_rgb_blueprint(shim), _anywhere(shim))

    # The blueprint's default went to the server, and the server's name came back on the actor.
    assert [spawn["attributes"]["role_name"] for spawn in client.spawned] == ["front", "front"]
    assert first.attributes["role_name"] == "Camera_1"
    assert world.camera_name(first) == "Camera_1"
    assert world.camera_name(second) == "Camera_2"
    assert shim.camera_named_by_server(first)
    # Nothing to say: this server names cameras.
    assert capsys.readouterr().err == ""


def test_a_name_the_client_gives_is_sent_as_the_role_name_and_read_back(shim):
    client = _Client(shim)
    world = shim.World(client)

    camera = world.spawn_camera(_rgb_blueprint(shim), _anywhere(shim), name="DECK-I25")

    assert client.spawned[-1]["attributes"]["role_name"] == "DECK-I25"
    assert world.camera_name(camera) == "DECK-I25"
    assert not shim.camera_named_by_server(camera)


@pytest.mark.parametrize(("name", "reason"), [
    ("Deck Cam 1", "holds a space"),
    ("front", "role name the server gives sensors"),
    ("Camera_3", "which a client cannot claim"),
    ("CARLA-SENSOR-12", "another camera's name"),
])
def test_a_name_the_rule_refuses_is_refused_before_the_server_is_reached(shim, name, reason):
    client = _Client(shim)
    world = shim.World(client)

    with pytest.raises(ValueError, match=reason):
        world.spawn_camera(_rgb_blueprint(shim), _anywhere(shim), name=name)

    assert client.spawned == []


def test_a_name_a_live_camera_holds_is_refused_by_the_server_with_its_reason(shim):
    client = _Client(shim)
    world = shim.World(client)
    holder = world.spawn_camera(_rgb_blueprint(shim), _anywhere(shim), name="Overwatch_1")

    # The server's refusal is the RPC's error, said as every refusal of a name is said.
    with pytest.raises(ValueError, match=f"already held in this world by camera {holder.id}"):
        world.spawn_camera(_rgb_blueprint(shim), _anywhere(shim), name="overwatch_1")
    # Any other failure of the spawn is not a refusal of the name, and comes through as it is.
    client.fails_with = RuntimeError("spawn failed: Collision")
    with pytest.raises(RuntimeError, match="Collision"):
        world.spawn_camera(_rgb_blueprint(shim), _anywhere(shim), name="Overwatch_2")
    client.fails_with = None

    # Destroyed, the name is free: the server holds it only while the camera lives.
    assert holder.destroy()
    assert client.destroyed == [holder.id]
    again = world.spawn_camera(_rgb_blueprint(shim), _anywhere(shim), name="overwatch_1")
    assert world.camera_name(again) == "overwatch_1"


def test_a_server_that_names_no_cameras_leaves_an_unnamed_camera_its_default_and_is_said_once(
        shim, capsys):
    client = _Client(shim, names_cameras=False)
    world = shim.World(client)
    shim._server_names_no_cameras_said = False

    first = world.spawn_camera(_rgb_blueprint(shim), _anywhere(shim))
    second = world.spawn_camera(_rgb_blueprint(shim), _anywhere(shim))
    named = world.spawn_camera(_rgb_blueprint(shim), _anywhere(shim), name="DECK-I25")

    assert first.attributes["role_name"] == "front"
    assert world.camera_name(first) == f"CARLA-SENSOR-{first.id}"
    assert world.camera_name(second) == f"CARLA-SENSOR-{second.id}"
    assert not shim.camera_named_by_server(first)
    # A name the client gave is held by such a server as by any other.
    assert world.camera_name(named) == "DECK-I25"
    said = capsys.readouterr().err
    assert said.count("this server names no cameras") == 1
    assert f"camera {first.id} was spawned without a name" in said
    assert "rebuild the server" in said


def test_a_recorder_is_started_under_the_name_the_camera_holds_and_no_other(shim):
    client = _Client(shim)
    world = shim.World(client)
    camera = world.spawn_camera(_rgb_blueprint(shim), _anywhere(shim))
    assert world.camera_name(camera) == "Camera_1"

    # None, the name itself, or the older spelling of it: the camera's name.
    assert world._recording_camera_name(camera, None, None) == "Camera_1"
    assert world._recording_camera_name(camera, "Camera_1", None) == "Camera_1"
    assert world._recording_camera_name(camera, None, "Camera_1") == "Camera_1"
    # Any other name is refused: a camera is named when it is spawned.
    with pytest.raises(ValueError, match="is named 'Camera_1' on the server"):
        world._recording_camera_name(camera, "Overwatch_1", None)
    with pytest.raises(ValueError, match="is named 'Camera_1' on the server"):
        world._recording_camera_name(camera, None, "Overwatch_1")
    with pytest.raises(ValueError, match="differ"):
        world._recording_camera_name(camera, "Camera_1", "Overwatch_1")


def test_a_recorder_of_a_camera_named_by_its_client_takes_that_name(shim):
    client = _Client(shim)
    world = shim.World(client)
    camera = world.spawn_camera(_rgb_blueprint(shim), _anywhere(shim), name="DECK-I25")

    assert world._recording_camera_name(camera, None, None) == "DECK-I25"
    assert world._recording_camera_name(camera, "DECK-I25", None) == "DECK-I25"
    with pytest.raises(ValueError, match="is named 'DECK-I25' on the server"):
        world._recording_camera_name(camera, "deck-i25", None)
