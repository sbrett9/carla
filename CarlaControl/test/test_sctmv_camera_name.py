"""run_SCTMV.py's camera is named as the operator asks, and records under that name.

Every capture is named after its camera, `<camera name>_<local capture time>`, and the camera's
platform track carries the name as its callsign, so cameras sharing a world are told apart in their
files and their telemetry. `--camera-name` -- `--platform-callsign` is its older spelling -- names the
camera `SensorRig` spawns, and `NativeRecorder` records under it. Its default used to be OVERWATCH,
which every camera given no name shared; it is now no name at all, and the server then names the
camera `Camera_<n>`, which the rig reads back. The world and the recorder are stood in for.
"""
from __future__ import annotations

import sys
from pathlib import Path
from types import SimpleNamespace

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

_carlacontrol = pytest.importorskip(
    "carlacontrol", reason="carlacontrol needs the CarlaNet assemblies on this machine")

from carlacontrol.CarlaControlArgumentParser import CarlaControlArgumentParser  # noqa: E402
from carlacontrol.NativeRecorder import NativeRecorder  # noqa: E402
from carlacontrol.SensorRig import SensorRig  # noqa: E402


def _parse(*extra: str):
    return CarlaControlArgumentParser(str(_REPO)).parse_args(list(extra))


def test_the_camera_has_no_name_unless_the_operator_gives_one():
    assert _parse().camera_name is None


def test_the_older_spelling_names_the_camera_too():
    assert _parse("--camera-name", "DECK-I25").camera_name == "DECK-I25"
    assert _parse("--platform-callsign", "Southeast_1700m_orbit").camera_name == \
        "Southeast_1700m_orbit"


@pytest.mark.parametrize(("name", "reason"), [("DECK:I25", "holds ':'"),
                                              ("Overwatch 1", "holds a space"),
                                              ("Overwatch.1", "holds '.'"),
                                              ("CON", "keeps for a device"),
                                              ("front", "role name the server gives sensors"),
                                              ("Camera_1", "which a client cannot claim"),
                                              ("CARLA-SENSOR-12", "another camera's name")])
def test_a_name_the_rule_refuses_is_refused_at_the_command_line(capsys, name, reason):
    with pytest.raises(SystemExit) as exited:
        _parse("--camera-name", name)
    assert exited.value.code == 2
    said = capsys.readouterr().err
    assert reason in said and "such as Overwatch_1" in said


class _RecordingWorld:
    """A world that records what it was asked to record under."""

    def __init__(self) -> None:
        self.asked: dict | None = None

    def start_recording(self, camera, record_dir, hz, affiliation, stale, **kwargs):
        self.asked = kwargs
        return SimpleNamespace(HaveTelemetryOrigin=True,
                               Name=kwargs["camera_name"] or f"CARLA-SENSOR-{camera.id}")

    def get_sim_time(self) -> float:
        return 0.0


@pytest.mark.parametrize(("given", "asked"), [(("--camera-name", "DECK-I25"), "DECK-I25"),
                                              ((), None)])
def test_the_recorder_records_under_the_camera_s_name_and_names_no_callsign_of_its_own(given,
                                                                                    asked):
    world = _RecordingWorld()
    recorder = NativeRecorder(world, SimpleNamespace(id=7), _parse(*given))
    recorder.available = True
    recorder.want_enabled = True

    recorder.apply_want()

    assert recorder.recording
    # None leaves the shim the name the camera was spawned under, or its default.
    assert world.asked["camera_name"] == asked
    assert "platform_callsign" not in world.asked


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


class _SpawningWorld:
    """A world a rig is spawned into: which cameras, under which names."""

    def __init__(self) -> None:
        self.spawned: list[tuple[str, str | None]] = []
        self.names: dict[int, str] = {}
        # Child camera id -> the id of the camera it was spawned attached to.
        self.attached: dict[int, int] = {}

    def get_blueprint_library(self):
        return SimpleNamespace(find=_Blueprint)

    def spawn_actor(self, blueprint, _transform, attach_to=None, attachment_type=None) -> _Camera:
        # The server names every camera spawned without a name, the depth camera included.
        self.spawned.append((blueprint.id, None))
        camera = _Camera(100 + len(self.spawned))
        self.names[camera.id] = f"Camera_{len(self.spawned)}"
        if attach_to is not None:
            self.attached[camera.id] = attach_to.id
        return camera

    def spawn_camera(self, blueprint, transform, name=None) -> _Camera:
        camera = self.spawn_actor(blueprint, transform)
        self.spawned[-1] = (blueprint.id, name)
        if name is not None:
            self.names[camera.id] = name
        return camera

    def camera_name(self, camera) -> str:
        """The name the camera holds on the server, as the shim reads it back."""
        return self.names[camera.id]

    def get_spectator(self) -> _Camera:
        return _Camera(0)


def _rig_settings(**more) -> SimpleNamespace:
    return SimpleNamespace(x=0.0, y=0.0, z=1000.0, width=64, height=36, fov=90.0, ev=None,
                           asynchronous=True, **more)


def test_the_rig_s_camera_is_spawned_under_the_name_given_and_its_depth_camera_under_none():
    world = _SpawningWorld()
    rig = SensorRig(world, _rig_settings(camera_name="DECK-I25"))
    assert world.spawned == [("sensor.camera.rgb", "DECK-I25"), ("sensor.camera.depth", None)]
    assert rig.camera_name == "DECK-I25"
    # The depth camera rides the RGB camera: spawned attached to it, so one move -- a client's, or
    # the server's orbit mover's -- carries both.
    assert world.attached == {rig.depth_cam.id: rig.camera.id}


def test_a_rig_given_no_name_takes_the_name_the_server_gives_its_camera():
    # run_free_move_camera.py's arguments carry no name at all.
    world = _SpawningWorld()
    rig = SensorRig(world, _rig_settings())
    assert world.spawned[0] == ("sensor.camera.rgb", None)
    assert rig.camera_name == "Camera_1"
