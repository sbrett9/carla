"""run_SCTMV.py's recorder measures occlusion on every capture, and no option turns it off.

The owner's ruling of 2026-10-05: the occlusion measurement is always on, in every capture path,
with no switch. `--no-occlusion` is gone from the front end's parser, so a command line naming it is
refused as an unknown option; `--occlusion-margin` and `--occlusion-samples` stay, shaping the
measurement rather than switching it. `NativeRecorder` records against the depth camera the rig hands
it -- `run_SCTMV.py` passes `sensors.depth_cam` -- and starts the native recorder with it. The world
is stood in for.
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


def _parse(*extra: str):
    return CarlaControlArgumentParser(str(_REPO)).parse_args(list(extra))


def test_there_is_no_option_to_turn_the_occlusion_measurement_off(capsys):
    with pytest.raises(SystemExit) as exited:
        _parse("--no-occlusion")
    assert exited.value.code == 2
    assert "unrecognized arguments: --no-occlusion" in capsys.readouterr().err
    assert not hasattr(_parse(), "occlusion")


def test_the_measurement_s_settings_still_parse_with_the_plan_s_defaults():
    args = _parse()
    assert (args.occlusion_margin, args.occlusion_samples) == (1.0, 24)
    given = _parse("--occlusion-margin", "0.5", "--occlusion-samples", "48")
    assert (given.occlusion_margin, given.occlusion_samples) == (0.5, 48)


class _RecordingWorld:
    """A world that records what it was asked to record, and against which depth camera."""

    def __init__(self) -> None:
        self.asked: dict | None = None

    def start_recording(self, camera, record_dir, hz, affiliation, stale, **kwargs):
        self.asked = kwargs
        return SimpleNamespace(HaveTelemetryOrigin=True, Name=f"CARLA-SENSOR-{camera.id}")

    def get_sim_time(self) -> float:
        return 0.0


def test_the_recorder_starts_the_native_recorder_with_the_rig_s_depth_camera():
    world = _RecordingWorld()
    depth = SimpleNamespace(id=8)
    recorder = NativeRecorder(world, SimpleNamespace(id=7), _parse("--occlusion-samples", "32"),
                              depth_camera=depth)
    recorder.available = True
    recorder.want_enabled = True

    recorder.apply_want()

    assert recorder.recording
    assert world.asked["depth_camera"] is depth
    assert (world.asked["occlusion_margin_m"], world.asked["occlusion_samples"]) == (1.0, 32)
