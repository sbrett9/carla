"""The drive's free view records as the fixed camera does, and adds nothing to the drive's tick loop.

`CarlaNet/python/run_sumo_drive.py --view free` flies a camera inside the process that drives the
world (`12_Operator_Control_Surface.md` section 9.6). What is checked here, with the server and the
session stood in for:

* each span is recorded as the fixed camera's recording is -- the session's render set and
  illumination -- with the rig's depth camera for occlusion, this drive's run id, into the span's
  own folder, and no span starts before the capture window opens;
* the flown rig is spawned over the region centre at `--camera-z`, with the run configuration's
  depth range rather than the depth camera's stock 1000 m;
* the session is handed no per-vehicle callback it does not need, in either view: the worst
  divergence comes off the session's report, so nothing crosses into Python per vehicle per tick
  while a window thread in the same process holds the interpreter;
* a region smaller than the world is said before the drive starts, with the one that is not.
"""
from __future__ import annotations

import importlib.util
import json
import logging
import sys
import zipfile
from pathlib import Path
from types import SimpleNamespace

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))
SCRIPT = _REPO / "CarlaNet" / "python" / "run_sumo_drive.py"

GARDNERVILLE = {
    "MapName": "Gardnerville_Centerville_Lane",
    "StagingMinXMeters": -838.9031372070312, "StagingMinYMeters": -455.0901184082031,
    "StagingMaxXMeters": 839.0968627929688, "StagingMaxYMeters": 456.9098815917969,
}


@pytest.fixture(scope="module")
def drive():
    """The script as a module. It imports the co-simulation assemblies, so it needs carlanet."""
    pytest.importorskip("carlanet", reason="the drive needs carlanet and its assemblies")
    spec = importlib.util.spec_from_file_location("run_sumo_drive", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    try:
        spec.loader.exec_module(module)
    except ImportError as missing:
        pytest.skip(f"the co-simulation assemblies are not loaded here: {missing}")
    return module


def _arguments(drive, monkeypatch, *extra: str, package: Path | None = None):
    argv = ["run_sumo_drive.py", "--scenario", "s.sumocfg",
            "--world-package", str(package or "w.cwp"), *extra]
    monkeypatch.setattr(sys, "argv", argv)
    return drive.parse_args()


def test_each_view_gets_its_own_image_size_unless_one_is_given(drive, monkeypatch):
    fixed = _arguments(drive, monkeypatch)
    free = _arguments(drive, monkeypatch, "--view", "free")
    given = _arguments(drive, monkeypatch, "--view", "free", "--width", "1920", "--fov", "60")

    assert (fixed.width, fixed.height, fixed.fov) == (1920, 1080, 60.0)
    assert (free.width, free.height, free.fov) == (1280, 720, 90.0)
    assert (given.width, given.height, given.fov) == (1920, 720, 60.0)


def test_the_flown_rig_starts_over_the_region_centre_and_measures_depth_as_far_as_a_capture_run(
        drive, monkeypatch):
    from carlacontrol.RunConfiguration import RunConfiguration
    from carlacontrol.SensorRig import SensorRig

    args = _arguments(drive, monkeypatch, "--view", "free", "--region-x", "120",
                      "--region-y", "340", "--camera-z", "450")

    settings = drive.free_view_settings(
        args, SensorRig.FT_PER_M, RunConfiguration.field("occlusion.depth_max_range_m").default)

    # The region is in SUMO's frame, the camera in CARLA's: the same frame with the northing negated.
    assert (settings.x, settings.y) == (120.0, -340.0)
    assert settings.z / SensorRig.FT_PER_M == pytest.approx(450.0)
    assert settings.depth_max_range == 20000.0
    assert (settings.width, settings.height, settings.fov) == (1280, 720, 90.0)
    assert settings.asynchronous is True
    assert settings.ev is None


class _RecordingWorld:
    """The world a span records through: what it was asked to record, and about which camera."""

    def __init__(self) -> None:
        self.recorded: list[tuple[tuple, dict]] = []
        self.stops = 0
        self.asked_about: list = []

    def start_recording(self, *args, **kwargs):
        self.recorded.append((args, kwargs))
        return SimpleNamespace(Saved=0, Dropped=0, PairsRenderSet=True, RenderSetPaired=0,
                               RenderSetUnpaired=0)

    def stop_recording(self) -> None:
        self.stops += 1

    def get_view_readiness(self, camera) -> dict:
        self.asked_about.append(camera)
        return {"frame": 7, "published": True, "tilesets": []}


def test_a_span_records_the_flown_camera_as_the_fixed_camera_records(drive, monkeypatch, tmp_path):
    args = _arguments(drive, monkeypatch, "--view", "free", "--record-dir", str(tmp_path),
                      "--record-hz", "4")
    recording = _RecordingWorld()
    client = SimpleNamespace(get_world=lambda: recording)
    session = SimpleNamespace(Illumination="the session's illumination",
                              RenderSet="the session's render set",
                              WindowOpensAtSeconds=310.0, RenderedTimeSeconds=300.0)
    parts = drive.FreeViewParts()
    parts.rig = SimpleNamespace(camera=SimpleNamespace(id=4121), depth_cam="the rig's depth camera")

    recorder = parts.span_recorder(client, session, args, "run-20260930-142233")

    assert recorder.sensor_id == "CARLA-SENSOR-4121"
    assert recorder.record_dir == tmp_path
    assert recorder.record_hz == 4.0
    # Before the capture window opens a span is refused, and says when it may start.
    assert recorder._may_record() == "the capture window opens at t=310 s"
    session.RenderedTimeSeconds = 310.0
    assert recorder._may_record() is None

    recorder._start_recording(str(tmp_path / "span"))
    (positional, keywords), = recording.recorded
    assert positional == (parts.rig.camera, str(tmp_path / "span"), 4.0)
    assert keywords == {"fov": 90.0, "run_id": "run-20260930-142233",
                        "depth_camera": "the rig's depth camera",
                        "illumination": "the session's illumination",
                        "render_set": "the session's render set"}
    recorder._view_readiness()
    assert recording.asked_about == [parts.rig.camera]
    recorder._stop_recording()
    assert recording.stops == 1


class _DriveWorld:
    """A world whose drive is refused, so a run stops right after asking for it."""

    def __init__(self) -> None:
        self.drive_arguments: dict | None = None

    def get_map(self):
        return SimpleNamespace(name="Gardnerville_Centerville_Lane")

    def start_sumo_drive(self, *args, **kwargs):
        self.drive_arguments = kwargs
        return None

    def stop_recording(self) -> None:
        pass


def _run_main(drive, monkeypatch, tmp_path, *extra: str) -> _DriveWorld:
    package = tmp_path / "Gardnerville_Centerville_Lane.cwp"
    with zipfile.ZipFile(package, "w") as archive:
        archive.writestr("world.json", json.dumps(GARDNERVILLE))
    for name in ("s.sumocfg", "vehicles.catalogue.json"):
        (tmp_path / name).write_text("{}", encoding="utf-8")
    world = _DriveWorld()

    class _Client:
        def __init__(self, *_args) -> None:
            pass

        def set_timeout(self, _seconds) -> None:
            pass

        def get_world(self):
            return world

        def get_server_version(self) -> str:
            return "stand-in"

    monkeypatch.setattr(drive, "carla", SimpleNamespace(Client=_Client))
    monkeypatch.setattr(sys, "argv", [
        "run_sumo_drive.py", "--scenario", str(tmp_path / "s.sumocfg"),
        "--world-package", str(package),
        "--catalogue", str(tmp_path / "vehicles.catalogue.json"), *extra])
    assert drive.main() == 1
    return world


@pytest.mark.parametrize("view", ["fixed", "free"])
def test_the_session_is_handed_no_per_vehicle_divergence_callback(drive, monkeypatch, tmp_path,
                                                                  view):
    world = _run_main(drive, monkeypatch, tmp_path, "--view", view)

    assert world.drive_arguments is not None
    assert "on_divergence" not in world.drive_arguments
    # The pose callback aims the fixed camera, and only the fixed camera.
    assert (world.drive_arguments["on_pose"] is None) == (view == "free")


def test_a_region_smaller_than_the_world_is_said_before_the_drive_starts(drive, monkeypatch,
                                                                         tmp_path, caplog):
    with caplog.at_level(logging.INFO, logger="run_sumo_drive"):
        _run_main(drive, monkeypatch, tmp_path, "--view", "free")

    assert "--region-x 0 --region-y -1 --region-radius 956" in caplog.text

    caplog.clear()
    with caplog.at_level(logging.INFO, logger="run_sumo_drive"):
        _run_main(drive, monkeypatch, tmp_path, "--view", "free", "--region-x", "0",
                  "--region-y", "-1", "--region-radius", "956")
    assert "the render region takes in the whole world" in caplog.text
