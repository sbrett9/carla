"""The user tools install as `carla-*` commands, and every one of them starts.

`CarlaControl/pyproject.toml` declares one console command per tool, each
`carlacontrol.commands.<module>:main`. Each is loaded here through its entry point, exactly as an
installed command loads it, and asked for `--help`, which must exit 0. The scripts a checkout runs
the same tools from -- under `CarlaControl/scripts/`, and `CarlaNet/python/run_sumo_drive.py` --
must call that same `main` and answer `--help` too, so nothing that ran them before has stopped
working.
"""
from __future__ import annotations

import importlib.metadata
import os
import runpy
import sys
import tomllib
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

PYPROJECT = _REPO / "CarlaControl" / "pyproject.toml"

# Every command, and the checkout script that runs the same tool.
COMMANDS = {
    "carla-sctmv": "CarlaControl/scripts/run_SCTMV.py",
    "carla-build-world": "CarlaControl/scripts/build_world.py",
    "carla-compile-scenario": "CarlaControl/scripts/compile_scenario.py",
    "carla-capture": "CarlaControl/scripts/run_capture.py",
    "carla-drive": "CarlaNet/python/run_sumo_drive.py",
    "carla-cot-telemetry": "CarlaControl/scripts/sumo_cot_telemetry.py",
    "carla-free-camera": "CarlaControl/scripts/run_free_move_camera.py",
    "carla-camera-follower": "CarlaControl/scripts/run_camera_follower.py",
    "carla-audit-sidecars": "CarlaControl/scripts/audit_truth_sidecars.py",
    "carla-diff-manifests": "CarlaControl/scripts/diff_run_manifests.py",
    "carla-check-label-leaks": "CarlaControl/scripts/check_corpus_leaks.py",
    "carla-publish-reference-set": "CarlaControl/scripts/publish_reference_set.py",
    "carla-check-sumo": "CarlaControl/scripts/test_sumo_toolchain.py",
    "carla-validate": "CarlaControl/scripts/validate_capture.py",
}


def declared() -> dict[str, str]:
    """The console commands pyproject.toml declares, by name."""
    return tomllib.loads(PYPROJECT.read_text(encoding="utf-8"))["project"]["scripts"]


def entry_point(name: str) -> importlib.metadata.EntryPoint:
    return importlib.metadata.EntryPoint(name, declared()[name], "console_scripts")


@pytest.fixture(autouse=True)
def untouched_process(monkeypatch):
    """The world-building commands name the SUMO toolchain in the environment, and every checkout
    script puts the source tree on the import path; both are put back afterwards."""
    saved = dict(os.environ)
    monkeypatch.setattr(sys, "path", list(sys.path))
    yield
    os.environ.clear()
    os.environ.update(saved)


@pytest.fixture
def commands_load():
    """The commands import carlanet, so they need its assemblies."""
    pytest.importorskip("carlanet", reason="the commands need carlanet and its assemblies")


def test_the_declared_commands_are_the_user_tools():
    assert set(declared()) == set(COMMANDS)


@pytest.mark.parametrize("name", sorted(COMMANDS))
def test_each_command_is_the_main_of_its_own_module_in_the_commands_package(name):
    module, _, function = declared()[name].partition(":")
    assert module == "carlacontrol.commands." + name.removeprefix("carla-").replace("-", "_")
    assert function == "main"


@pytest.mark.parametrize("name", sorted(COMMANDS))
def test_each_command_resolves_and_answers_help_through_its_entry_point(name, commands_load,
                                                                        monkeypatch, capsys):
    main = entry_point(name).load()
    assert callable(main)

    # A console command calls main() with no arguments and reads sys.argv, as here.
    monkeypatch.setattr(sys, "argv", [name, "--help"])
    with pytest.raises(SystemExit) as exited:
        main()
    assert exited.value.code == 0
    assert "usage:" in capsys.readouterr().out


@pytest.mark.parametrize("name", sorted(COMMANDS))
def test_each_checkout_script_runs_the_same_main_and_answers_help(name, commands_load,
                                                                   monkeypatch, capsys):
    script = _REPO / COMMANDS[name]
    assert runpy.run_path(str(script))["main"] is entry_point(name).load()

    monkeypatch.setattr(sys, "argv", [str(script), "--help"])
    with pytest.raises(SystemExit) as exited:
        runpy.run_path(str(script), run_name="__main__")
    assert exited.value.code == 0
    assert "usage:" in capsys.readouterr().out
