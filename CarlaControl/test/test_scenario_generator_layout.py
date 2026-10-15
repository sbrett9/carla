"""Where a scenario generator reads and writes, decided from the folders around the script.

A generator under `CarlaControl/scripts/` uses the checkout's `Import/`, `Build/` and catalogue. The
same script copied beside its scenario, as a distribution's `Scenarios/<Place>/` has it, writes the
specification beside itself, compiles into `<folder>/<scenario id>/` and reads the distribution's
catalogue and SUMO, so the shipped READMEs' "change the script, not the specification" holds there.
"""
from __future__ import annotations

import sys
from pathlib import Path

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.ScenarioGeneratorLayout import ScenarioGeneratorLayout  # noqa: E402

MAP = "Some_World"
SCENARIO = "Some_World_Scenario"


def checkout_script(root: Path) -> Path:
    (root / "CarlaControl" / "src" / "carlacontrol").mkdir(parents=True)
    (root / "CarlaControl" / "src" / "carlacontrol" / "__init__.py").write_text("")
    (root / "CarlaControl" / "catalogue").mkdir()
    (root / "CarlaControl" / "catalogue" / "vehicles.catalogue.json").write_text("{}")
    (root / "CarlaControl" / "scripts").mkdir()
    script = root / "CarlaControl" / "scripts" / "make_some_scenario.py"
    script.write_text("")
    return script


def distribution_script(root: Path) -> Path:
    (root / "catalogue").mkdir(parents=True)
    (root / "catalogue" / "vehicles.catalogue.json").write_text("{}")
    (root / "Scenarios" / "Place").mkdir(parents=True)
    script = root / "Scenarios" / "Place" / "make_some_scenario.py"
    script.write_text("")
    return script


def test_a_checkout_script_uses_the_checkout_folders(tmp_path: Path) -> None:
    root = tmp_path / "carla"
    script = checkout_script(root)
    layout = ScenarioGeneratorLayout.of(script, MAP, SCENARIO)
    assert layout.checkout == root.resolve()
    assert not layout.beside_scenario
    assert layout.out_dir == root.resolve() / "Import"
    assert layout.compile_dir(layout.out_dir) == layout.out_dir
    assert layout.world_package == root.resolve() / "Build" / "world-packages" / f"{MAP}.cwp"
    assert layout.catalogue == root.resolve() / "CarlaControl" / "catalogue" / "vehicles.catalogue.json"
    assert layout.run_scenario_package == f"Import/{SCENARIO}.lock.json"
    assert "checkout" in layout.describe()


def test_a_checkout_names_its_staged_sumo_only_when_it_is_there(tmp_path: Path) -> None:
    root = tmp_path / "carla"
    script = checkout_script(root)
    assert ScenarioGeneratorLayout.of(script, MAP, SCENARIO).sumo_home is None
    (root / "Build" / "sumo-install").mkdir(parents=True)
    assert ScenarioGeneratorLayout.of(script, MAP, SCENARIO).sumo_home \
        == root.resolve() / "Build" / "sumo-install"


def test_a_script_beside_its_scenario_uses_the_distribution_folders(tmp_path: Path) -> None:
    root = tmp_path / "Carla-dist"
    script = distribution_script(root)
    layout = ScenarioGeneratorLayout.of(script, MAP, SCENARIO)
    assert layout.checkout is None
    assert layout.beside_scenario
    assert layout.out_dir == script.resolve().parent
    assert layout.compile_dir(layout.out_dir) == script.resolve().parent / SCENARIO
    assert layout.compile_dir(tmp_path / "elsewhere") == (tmp_path / "elsewhere" / SCENARIO)
    assert layout.world_package == script.resolve().parent / f"{MAP}.cwp"
    assert layout.catalogue == root.resolve() / "catalogue" / "vehicles.catalogue.json"
    assert layout.run_scenario_package == SCENARIO
    assert "beside" in layout.describe()


def test_a_script_beside_its_scenario_names_the_distribution_sumo_when_it_is_there(
        tmp_path: Path) -> None:
    root = tmp_path / "Carla-dist"
    script = distribution_script(root)
    assert ScenarioGeneratorLayout.of(script, MAP, SCENARIO).sumo_home is None
    (root / "tools" / "sumo").mkdir(parents=True)
    assert ScenarioGeneratorLayout.of(script, MAP, SCENARIO).sumo_home \
        == root.resolve() / "tools" / "sumo"


def test_a_checkout_is_not_mistaken_for_a_distribution_without_its_catalogue(tmp_path: Path) -> None:
    root = tmp_path / "carla"
    script = checkout_script(root)
    (root / "CarlaControl" / "catalogue" / "vehicles.catalogue.json").unlink()
    assert ScenarioGeneratorLayout.of(script, MAP, SCENARIO).beside_scenario


def test_the_repository_generators_run_from_this_checkout() -> None:
    for name in ("make_sumo_scenario.py", "make_arapahoe_scenario.py",
                 "make_supervision_check_scenario.py", "make_bahonar_scenario.py"):
        layout = ScenarioGeneratorLayout.of(_REPO / "CarlaControl" / "scripts" / name, MAP, SCENARIO)
        assert layout.checkout == _REPO, name
        assert layout.out_dir == _REPO / "Import", name
