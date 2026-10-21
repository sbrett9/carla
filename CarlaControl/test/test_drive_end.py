"""carla-drive's `--end SECOND` ends a drive at a simulated second, the step that second falls in drawn.

Held here, with the server, the session, its live track and the socket stood in for (`DriveFakes`):

* **The step at `--end` is the last one drawn, and sampled.** On Bahonar's 1.0 s step,
  `--warm-up 271169 --end 271433` draws the 265 steps from 271169 to 271433 and sends the live feed's
  sample of the last; an `--end` part-way through a step draws that step too. The aim step and the
  prewarm count towards it, and an aim step that has already drawn the step `--end` falls in is the
  last. The launch says which step is the last.
* **What is refused.** `--end` with `--steps`, `--steps 0` included; an `--end` not after `--warm-up`,
  or not after `--window-opens-at`; and one that is not a finite second. Each exits 2 before anything
  connects. With neither `--end` nor `--steps`, `--steps` is 600 as it always was. `--help` says that
  `--end` is a simulated second and the last step drawn.
"""
from __future__ import annotations

import importlib
import logging
import sys
from pathlib import Path
from types import SimpleNamespace

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))
sys.path.insert(0, str(_REPO / "CarlaControl" / "test"))

from DriveFakes import FakeCamera, FakeClient, FakeEmitter, FakeSession, FakeWorld  # noqa: E402


@pytest.fixture(scope="module")
def drive():
    """The command's module. It imports the co-simulation assemblies, so it needs carlanet."""
    pytest.importorskip("carlanet", reason="the drive needs carlanet and its assemblies")
    try:
        return importlib.import_module("carlacontrol.commands.drive")
    except ImportError as missing:
        pytest.skip(f"the co-simulation assemblies are not loaded here: {missing}")


@pytest.fixture
def feed_module(drive, monkeypatch):
    """`carlacontrol.DriveCotFeed`, with its socket stood in for."""
    module = importlib.import_module("carlacontrol.DriveCotFeed")
    FakeEmitter.made = []
    FakeEmitter.timeline = None
    monkeypatch.setattr(module, "CotUdpEmitter", FakeEmitter)
    return module


def _arguments(drive, *extra: str):
    return drive.parse_args(["--scenario", "s.sumocfg", "--world-package", "w.cwp", *extra])


def _refusal(drive, capsys, *extra: str) -> str:
    with pytest.raises(SystemExit) as stopped:
        _arguments(drive, *extra)
    assert stopped.value.code == 2
    return capsys.readouterr().err


def _drive_with(drive, monkeypatch, tmp_path, session: FakeSession, *extra: str) -> int:
    """Run the command against the stand-ins, sending the feed, its fixed camera spawned and recorded
    without a server."""
    for name in ("s.sumocfg", "w.cwp", "vehicles.catalogue.json"):
        (tmp_path / name).write_text("{}", encoding="utf-8")
    FakeClient.world = FakeWorld(session)
    FakeEmitter.timeline = session.timeline
    monkeypatch.setattr(importlib.import_module("carlacontrol.DriveRun"), "carla",
                        SimpleNamespace(Client=FakeClient))
    camera = importlib.import_module("carlacontrol.DriveCamera").DriveCamera
    monkeypatch.setattr(camera, "spawn_camera", staticmethod(lambda *_: FakeCamera(41)))
    monkeypatch.setattr(camera, "spawn_depth_camera", staticmethod(lambda *_: FakeCamera(42)))
    monkeypatch.setattr(importlib.import_module("carlacontrol.DriveReport").DriveReport,
                        "report_captures", staticmethod(lambda _recorder: None))
    return drive.main(["--scenario", str(tmp_path / "s.sumocfg"),
                       "--world-package", str(tmp_path / "w.cwp"),
                       "--catalogue", str(tmp_path / "vehicles.catalogue.json"),
                       "--record-dir", str(tmp_path / "captures"),
                       "--cot-udp", "127.0.0.1:7000", *extra])


def _stamps(emitter: FakeEmitter) -> list[str]:
    return [xml.split('time="')[1].split('"')[0] for xml in emitter.sent]


# ---- the options -------------------------------------------------------------------------------

def test_without_end_or_steps_a_drive_runs_600_steps(drive):
    args = _arguments(drive)
    assert (args.steps, args.end) == (600, None)
    assert _arguments(drive, "--steps", "0").steps == 0
    args = _arguments(drive, "--warm-up", "271169", "--end", "271433")
    assert (args.steps, args.end) == (None, 271433.0)


@pytest.mark.parametrize(("extra", "saying"), [
    (["--end", "120", "--steps", "10"], "--end 120 and --steps 10 both say when the drive stops"),
    (["--end", "120", "--steps", "0"], "--end 120 and --steps 0 both say when the drive stops"),
    (["--warm-up", "271169", "--end", "271169"],
     "--end 271169 is not after --warm-up 271169: the drive draws its first step at --warm-up"),
    (["--warm-up", "271169", "--end", "271000.5"], "--end 271000.5 is not after --warm-up 271169"),
    (["--warm-up", "100", "--window-opens-at", "160", "--end", "150"],
     "--end 150 is not after --window-opens-at 160: the drive would stop before its capture window "
     "opens"),
    (["--end", "nan"], "--end nan must be a simulated second"),
    (["--end", "inf"], "--end inf must be a simulated second"),
])
def test_an_end_that_cannot_be_used_is_refused(drive, capsys, extra, saying):
    assert saying in _refusal(drive, capsys, *extra)


def test_the_help_says_end_is_a_simulated_second_and_the_last_step_drawn(drive, capsys):
    with pytest.raises(SystemExit):
        _arguments(drive, "--help")
    said = " ".join(capsys.readouterr().out.split())
    assert "--end SECOND" in said
    assert "the simulated second the drive ends at, in place of --steps: the SUMO step that second " \
        "falls in is the last step drawn" in said
    assert "`--warm-up 271169 --end 271433` draws the 265 steps from 271169 to 271433" in said


# ---- the drive ---------------------------------------------------------------------------------

def test_the_step_at_the_end_is_the_last_advanced_and_its_sample_is_sent(
        drive, feed_module, monkeypatch, tmp_path, caplog):
    session = FakeSession([], first=271169.0)
    with caplog.at_level(logging.INFO, logger="carlacontrol.DriveRun"):
        status = _drive_with(drive, monkeypatch, tmp_path, session, "--no-record",
                             "--warm-up", "271169", "--end", "271433")

    assert status == 0
    assert session.advanced == [271169.0 + step for step in range(265)]
    [emitter] = FakeEmitter.made
    assert _stamps(emitter)[-2:] == ["271433.950", "271433.950"]
    # The last step's sample is taken after its advance; the close takes what is left, which is none.
    assert session.timeline[-5:] == [
        ("advance", 271433.0), ("take", 1), ("take", 0), ("socket closed",), ("dispose",)]
    assert ("end: the step at t=271433.00 s is the last drawn, 265 SUMO steps from t=271169.00 s"
            in caplog.text)


def test_an_end_part_way_through_a_step_draws_that_step(drive, feed_module, monkeypatch, tmp_path):
    session = FakeSession([], first=271169.0)
    assert _drive_with(drive, monkeypatch, tmp_path, session, "--no-record", "--warm-up", "271169",
                       "--end", "271433.5") == 0
    assert session.advanced[-1] == 271433.0 and len(session.advanced) == 265


def test_the_aim_step_and_the_prewarm_count_towards_the_end(drive, feed_module, monkeypatch,
                                                           tmp_path):
    session = FakeSession([], first=271169.0, window=271171.0)
    assert _drive_with(drive, monkeypatch, tmp_path, session, "--warm-up", "271169",
                       "--window-opens-at", "271171", "--end", "271173") == 0
    # The aim step, one step of prewarm to the window's opening, and the window's steps to --end.
    assert session.advanced == [271169.0, 271170.0, 271171.0, 271172.0, 271173.0]
    assert _stamps(FakeEmitter.made[0])[-1] == "271173.950"


def test_an_aim_step_that_drew_the_step_the_end_falls_in_is_the_last(drive, feed_module,
                                                                     monkeypatch, tmp_path):
    session = FakeSession([], first=100.0)
    assert _drive_with(drive, monkeypatch, tmp_path, session, "--warm-up", "99.5",
                       "--end", "100.2") == 0
    assert session.advanced == [100.0]
    assert _stamps(FakeEmitter.made[0]) == ["100.950", "100.950"]


def test_a_scenario_that_ends_before_the_end_ends_the_drive(drive, feed_module, monkeypatch,
                                                            tmp_path):
    session = FakeSession([], first=271169.0, ends_on=10)
    assert _drive_with(drive, monkeypatch, tmp_path, session, "--no-record", "--warm-up", "271169",
                       "--end", "271433") == 0
    assert len(session.advanced) == 10
