"""carla-drive's `--epoch-at INSTANT` moves simulated second zero of a compiled scenario for one run.

Held here, with the server and the session stood in for (`DriveFakes`), on a scenario whose compile lock
carries the shipped Bahonar pattern of life's epoch, 07:00 at +03:30 on 29 September 2026:

* **Without `--epoch`, the lock's epoch is moved.** The session is handed the compiled epoch, which the
  lock is checked against, and the instant as given; the lock's illumination is the sun's policy, and
  a sun flag replaces the field it names, dropping the lock's fields that belong to another policy.
* **With `--epoch`**, the file's epoch must be the lock's: the scenario's own scenario.json is taken,
  with its illumination as today, and a foreign epoch is refused naming both digests.
* **What is refused, before anything connects.** A scenario with no compile lock, with or without
  `--epoch`; an instant that is no UTC date and time; one at another offset.
* **What the drive says.** The launch logs the run's epoch and the compiled one it was moved from, and
  `--help` describes the option. Without `--epoch-at`, the session is handed no instant.
"""
from __future__ import annotations

import importlib
import json
import logging
import sys
from pathlib import Path
from types import SimpleNamespace

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))
sys.path.insert(0, str(_REPO / "CarlaControl" / "test"))

from DriveFakes import FakeClient, FakeSession, FakeWorld  # noqa: E402

BAHONAR_LOCK = (_REPO / "CarlaControl" / "Examples" / "Scenarios" / "Bahonar"
                / "Shahid_Bahonar_Port_PatternOfLife" / "Shahid_Bahonar_Port_PatternOfLife.lock.json")
LOCKED = json.loads(BAHONAR_LOCK.read_text(encoding="utf-8"))
EPOCH, EPOCH_DIGEST, ILLUMINATION = LOCKED["epoch"], LOCKED["epoch_block_sha256"], LOCKED["illumination"]
AT = "2026-10-22T17:40:00Z"


@pytest.fixture(scope="module")
def drive():
    """The command's module. It imports the co-simulation assemblies, so it needs carlanet."""
    pytest.importorskip("carlanet", reason="the drive needs carlanet and its assemblies")
    try:
        return importlib.import_module("carlacontrol.commands.drive")
    except ImportError as missing:
        pytest.skip(f"the co-simulation assemblies are not loaded here: {missing}")


@pytest.fixture
def scenario(tmp_path) -> Path:
    """A scenario's configuration with a compile lock beside it holding Bahonar's epoch and
    illumination, which is all the drive reads of the lock."""
    for name in ("s.sumocfg", "w.cwp", "vehicles.catalogue.json"):
        (tmp_path / name).write_text("{}", encoding="utf-8")
    (tmp_path / "s.lock.json").write_text(json.dumps(
        {"lock_version": 1, "epoch": EPOCH, "epoch_block_sha256": EPOCH_DIGEST,
         "illumination": ILLUMINATION}), encoding="utf-8")
    return tmp_path / "s.sumocfg"


def _drive(drive, monkeypatch, scenario: Path, *extra: str, session: FakeSession | None = None):
    """Run the command against the stand-ins, recording nothing; the world it asked for a session."""
    FakeClient.made = 0
    FakeClient.world = FakeWorld(session or FakeSession([], live_track=False))
    monkeypatch.setattr(importlib.import_module("carlacontrol.DriveRun"), "carla",
                        SimpleNamespace(Client=FakeClient))
    status = drive.main(["--scenario", str(scenario),
                         "--world-package", str(scenario.with_name("w.cwp")),
                         "--catalogue", str(scenario.with_name("vehicles.catalogue.json")),
                         "--no-record", "--steps", "1", *extra])
    assert status == 0
    return FakeClient.world


def _refusal(drive, monkeypatch, scenario: Path, *extra: str) -> str:
    """What the command exits with before connecting."""
    with pytest.raises(SystemExit) as refused:
        _drive(drive, monkeypatch, scenario, *extra)
    assert FakeClient.made == 0
    return str(refused.value)


def _scenario_json(directory: Path, epoch: dict, illumination: dict | None) -> Path:
    path = directory / "s.scenario.json"
    document = {"epoch": epoch} if illumination is None else {"epoch": epoch,
                                                              "illumination": illumination}
    path.write_text(json.dumps(document), encoding="utf-8")
    return path


# ---- without --epoch, the compile lock's epoch is moved -------------------------------------------

def test_the_session_is_handed_the_lock_s_epoch_its_illumination_and_the_instant(
        drive, monkeypatch, scenario):
    asked = _drive(drive, monkeypatch, scenario, "--epoch-at", AT).drive_arguments
    assert asked["epoch"] == EPOCH
    assert asked["epoch_at"] == AT
    assert asked["illumination"] == ILLUMINATION


@pytest.mark.parametrize(("flags", "illumination"), [
    (["--solar-rate", "2.0"],
     {"illumination_version": 1, "policy": "advance", "rate_sun_s_per_sim_s": 2.0}),
    # The lock's rate belongs to advance, which the flag replaced.
    (["--illumination", "freeze_at_window_start"],
     {"illumination_version": 1, "policy": "freeze_at_window_start"}),
    # A flag's own field is passed on whatever policy it belongs to, for the session to refuse.
    (["--illumination", "freeze_at_window_start", "--solar-rate", "2.0"],
     {"illumination_version": 1, "policy": "freeze_at_window_start", "rate_sun_s_per_sim_s": 2.0}),
    (["--no-sun-required"],
     {"illumination_version": 1, "policy": "advance", "rate_sun_s_per_sim_s": 1.0,
      "require_sun": False}),
])
def test_a_sun_flag_replaces_the_lock_s_field_it_names(drive, monkeypatch, scenario, flags,
                                                       illumination):
    asked = _drive(drive, monkeypatch, scenario, "--epoch-at", AT, *flags).drive_arguments
    assert asked["illumination"] == illumination


def test_a_lock_that_declares_no_policy_leaves_the_sun_to_the_flags_or_the_default(
        drive, monkeypatch, scenario):
    lock = scenario.with_name("s.lock.json")
    lock.write_text(json.dumps({**json.loads(lock.read_text(encoding="utf-8")), "illumination": {}}),
                    encoding="utf-8")
    assert _drive(drive, monkeypatch, scenario, "--epoch-at", AT).drive_arguments["illumination"] \
        is None
    asked = _drive(drive, monkeypatch, scenario, "--epoch-at", AT, "--illumination", "ignore")
    assert asked.drive_arguments["illumination"] == {"illumination_version": 1, "policy": "ignore"}


# ---- with --epoch ----------------------------------------------------------------------------------

def test_the_scenario_s_own_specification_is_taken_with_its_illumination(drive, monkeypatch,
                                                                        scenario):
    illumination = {"illumination_version": 1, "policy": "freeze_at_window_start"}
    specification = _scenario_json(scenario.parent, EPOCH, illumination)
    asked = _drive(drive, monkeypatch, scenario, "--epoch", str(specification),
                   "--epoch-at", AT).drive_arguments
    assert (asked["epoch"], asked["epoch_at"], asked["illumination"]) == (EPOCH, AT, illumination)


def test_a_foreign_epoch_is_refused_naming_both_digests(drive, monkeypatch, scenario):
    foreign = {**EPOCH, "civil_datetime": "2026-09-30T07:00:00+03:30",
               "utc_datetime": "2026-09-30T03:30:00Z"}
    specification = _scenario_json(scenario.parent, foreign, None)
    said = _refusal(drive, monkeypatch, scenario, "--epoch", str(specification), "--epoch-at", AT)
    foreign_digest = importlib.import_module("carlacontrol.ScenarioEpoch").ScenarioEpoch.read(
        foreign).digest
    assert said == (f"--epoch-at moves the epoch {scenario} was compiled against, and "
                    f"{specification} declares another: its epoch digests as {foreign_digest}, and "
                    f"the compile lock {scenario.with_name('s.lock.json')} records {EPOCH_DIGEST}. "
                    "Leave --epoch out to move the lock's epoch, or give the scenario's own "
                    "scenario.json.")


# ---- refused before anything connects --------------------------------------------------------------

@pytest.mark.parametrize("with_epoch", [False, True])
def test_a_scenario_with_no_compile_lock_is_refused(drive, monkeypatch, scenario, with_epoch):
    lock = scenario.with_name("s.lock.json")
    lock.unlink()
    extra = ["--epoch", str(_scenario_json(scenario.parent, EPOCH, None))] if with_epoch else []
    said = _refusal(drive, monkeypatch, scenario, *extra, "--epoch-at", AT)
    assert said == (f"--epoch-at {AT} moves simulated second zero of the epoch the scenario was "
                    f"compiled against, and {scenario} has no compile lock at {lock}: an uncompiled "
                    "scenario was compiled against none. Give --epoch FILE declaring the epoch at "
                    "the instant wanted, without --epoch-at.")


@pytest.mark.parametrize(("instant", "saying"), [
    ("2026-10-22 17:40", "Simulated second zero is not moved: --epoch-at '2026-10-22 17:40' is not "
                         "a UTC date and time of the form 2026-10-22T17:40:00Z."),
    ("2026-10-22T21:10:00+03:30", "Simulated second zero is not moved: --epoch-at "
                                  "'2026-10-22T21:10:00+03:30' carries the offset +03:30, and the "
                                  "instant is given in UTC: write it 2026-10-22T17:40:00Z."),
])
def test_an_instant_that_is_no_utc_date_and_time_is_refused(drive, monkeypatch, scenario, instant,
                                                            saying):
    assert _refusal(drive, monkeypatch, scenario, "--epoch-at", instant) == saying


# ---- what the drive says ---------------------------------------------------------------------------

def test_the_launch_names_the_run_s_epoch_and_the_compiled_one(drive, monkeypatch, scenario,
                                                               caplog):
    from CarlaNet.CoSim import SolarEpoch
    compiled = SolarEpoch.FromJson(json.dumps(EPOCH))
    session = FakeSession([], live_track=False)
    session.Report.Epoch = compiled.MovedTo(SolarEpoch.ReadInstant(AT, "--epoch-at"))
    session.Report.EpochMovedFrom = compiled
    with caplog.at_level(logging.INFO, logger="carlacontrol.DriveRun"):
        _drive(drive, monkeypatch, scenario, "--epoch-at", AT, session=session)
    assert ("epoch: t = 0 is 2026-10-22T21:10:00+03:30 (2026-10-22T17:40:00Z), UTC+03:30, calendar "
            "advances, Asia/Tehran") in caplog.text
    assert ("  moved at run time from the compiled epoch: t = 0 is 2026-09-29T07:00:00+03:30 "
            "(2026-09-29T03:30:00Z)") in caplog.text


def test_without_epoch_at_the_session_is_handed_no_instant(drive, monkeypatch, scenario):
    specification = _scenario_json(scenario.parent, EPOCH, None)
    asked = _drive(drive, monkeypatch, scenario, "--epoch", str(specification)).drive_arguments
    assert (asked["epoch"], asked["epoch_at"], asked["illumination"]) == (EPOCH, None, None)


def test_the_help_describes_the_move(drive, capsys):
    with pytest.raises(SystemExit):
        drive.parse_args(["--help"])
    said = " ".join(capsys.readouterr().out.split())
    assert "--epoch-at INSTANT" in said
    assert ("move simulated second zero to this UTC date and time for the run, such as "
            "2026-10-22T17:40:00Z, the Z optional; any other offset is refused") in said
    assert "so 17:40Z on Bahonar is 21:10+03:30" in said
    assert "Refused on a scenario with no compile lock" in said
