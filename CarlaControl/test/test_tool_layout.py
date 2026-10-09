"""A command's defaults come from its checkout when run from one, and from the current folder and the
package's own data when installed.

`carlacontrol.ToolLayout` decides which by where carlacontrol was imported from. Held here:

* from this source tree it is a checkout, and the catalogue, the vehicle types and the run
  configuration schema are the repository's files, the outputs go under `Build/` and the inputs come
  from `Import/`;
* installed, the current folder stands in for both, the data files are the copies inside the
  installed package, there is no staged SUMO, and netconvert and PROJ come from the environment;
* the capture run's site profile and the world build's parser take their defaults from either;
* the package data a wheel carries is exactly what pyproject.toml maps into it: the package and its
  mapped data folders, laid out as an installation in a folder outside the repository, resolve
  there when imported from there -- and, where setuptools is installed, a wheel built from a copy of
  the project holds the same files.
"""
from __future__ import annotations

import fnmatch
import json
import os
import shutil
import subprocess
import sys
import tomllib
import zipfile
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.CarlaControlArgumentParser import CarlaControlArgumentParser  # noqa: E402
from carlacontrol.SiteProfile import SiteProfile  # noqa: E402
from carlacontrol.ToolLayout import ToolLayout  # noqa: E402

PROJECT = _REPO / "CarlaControl"
CATALOGUE = PROJECT / "catalogue" / "vehicles.catalogue.json"
VEHICLE_TYPES = PROJECT / "catalogue" / "vehicles.vtypes.rou.xml"
SCHEMA = PROJECT / "schemas" / "run_configuration.schema.json"
TOOLCHAIN_VARIABLES = ("CARLA_NETCONVERT", "PROJ_LIB", "PROJ_DATA", "SUMO_HOME")
NETCONVERT = "netconvert.exe" if os.name == "nt" else "netconvert"


@pytest.fixture
def clean_environment(monkeypatch):
    """No toolchain variable set, and every one put back as it was afterwards."""
    saved = dict(os.environ)
    for variable in TOOLCHAIN_VARIABLES:
        monkeypatch.delenv(variable, raising=False)
    yield
    os.environ.clear()
    os.environ.update(saved)


@pytest.fixture
def installed(tmp_path) -> ToolLayout:
    """An installed layout: work in a folder of its own, data from a package folder holding copies."""
    package = tmp_path / "site" / "carlacontrol"
    for source, parts in ((CATALOGUE, ("catalogue", CATALOGUE.name)),
                          (VEHICLE_TYPES, ("catalogue", VEHICLE_TYPES.name)),
                          (SCHEMA, ("schemas", SCHEMA.name))):
        target = package.joinpath("data", *parts)
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
    work = tmp_path / "work"
    work.mkdir()
    return ToolLayout(None, work=work, package_root=package)


# -- which way ----------------------------------------------------------------------------------------

def test_this_source_tree_is_a_checkout_and_names_the_repository():
    assert ToolLayout.find_checkout() == _REPO
    assert not ToolLayout.current().installed


def test_a_package_anywhere_else_is_installed(tmp_path):
    site_packages = tmp_path / "Lib" / "site-packages" / "carlacontrol"
    site_packages.mkdir(parents=True)
    assert ToolLayout.find_checkout(site_packages) is None

    # Laid out like a checkout but without the catalogue beside it, it is not one either.
    lookalike = tmp_path / "carla" / "CarlaControl" / "src" / "carlacontrol"
    lookalike.mkdir(parents=True)
    assert ToolLayout.find_checkout(lookalike) is None
    (tmp_path / "carla" / "CarlaControl" / "catalogue").mkdir()
    (tmp_path / "carla" / "CarlaControl" / "catalogue" / "vehicles.catalogue.json").write_text("{}")
    assert ToolLayout.find_checkout(lookalike) == (tmp_path / "carla").resolve()


# -- from a checkout ----------------------------------------------------------------------------------

def test_from_a_checkout_the_data_files_are_the_repository_s():
    layout = ToolLayout(_REPO)
    assert layout.catalogue == CATALOGUE and layout.catalogue.is_file()
    assert layout.vehicle_types == VEHICLE_TYPES and layout.vehicle_types.is_file()
    assert layout.run_configuration_schema == SCHEMA and layout.run_configuration_schema.is_file()
    assert layout.schema_directory == SCHEMA.parent


def test_from_a_checkout_outputs_go_under_build_and_inputs_come_from_import():
    layout = ToolLayout(_REPO)
    assert layout.build_directory == _REPO / "Build"
    assert layout.import_directory == _REPO / "Import"
    assert layout.world_package_directory == _REPO / "Build" / "world-packages"
    assert layout.capture_directory == _REPO / "Build" / "captures"
    assert layout.staged_sumo == _REPO / "Build" / "sumo-install"
    assert [home for _, home in layout.repository_sumo_builds()] == [
        _REPO / "Build" / "sumo-install", _REPO / "Build" / "sumo-src"]
    assert str(_REPO) in layout.describe()


def test_from_a_checkout_the_staged_toolchain_is_the_default(clean_environment):
    layout = ToolLayout(_REPO)
    netconvert, proj = layout.apply_sumo_environment()
    staged = _REPO / "Build" / "sumo-install"
    # Named whether or not it is there, so a missing one is reported by its path.
    assert netconvert == staged / "bin" / NETCONVERT
    assert proj == staged / "share" / "proj"
    assert os.environ["CARLA_NETCONVERT"] == str(netconvert)
    assert os.environ["PROJ_LIB"] == os.environ["PROJ_DATA"] == str(proj)
    assert os.environ["SUMO_HOME"] == str(staged)


def test_an_operator_s_toolchain_is_kept(clean_environment, tmp_path):
    os.environ["CARLA_NETCONVERT"] = str(tmp_path / "mine" / NETCONVERT)
    os.environ["PROJ_LIB"] = str(tmp_path / "proj")
    os.environ["SUMO_HOME"] = str(tmp_path / "sumo")
    netconvert, proj = ToolLayout(_REPO).apply_sumo_environment()
    assert netconvert == tmp_path / "mine" / NETCONVERT
    assert proj == tmp_path / "proj"
    assert os.environ["SUMO_HOME"] == str(tmp_path / "sumo")


# -- installed ----------------------------------------------------------------------------------------

def test_installed_the_data_files_are_the_package_s_own(installed):
    assert installed.installed
    package_data = installed.package_data()
    assert installed.catalogue == package_data / "catalogue" / CATALOGUE.name
    assert installed.catalogue.read_bytes() == CATALOGUE.read_bytes()
    assert installed.vehicle_types.read_bytes() == VEHICLE_TYPES.read_bytes()
    assert installed.run_configuration_schema.read_bytes() == SCHEMA.read_bytes()
    assert installed.schema_directory == package_data / "schemas"


def test_installed_the_current_folder_stands_in_for_build_and_import(installed):
    assert installed.build_directory == installed.work
    assert installed.import_directory == installed.work
    assert installed.world_package_directory == installed.work / "world-packages"
    assert installed.capture_directory == installed.work / "captures"
    assert installed.staged_sumo is None
    assert installed.repository_sumo_builds() == ()
    assert "installed" in installed.describe()


def test_installed_the_toolchain_is_the_environment_s(installed, clean_environment, tmp_path,
                                                      monkeypatch):
    monkeypatch.setenv("PATH", "")
    assert installed.netconvert() is None
    assert installed.proj_data() is None
    assert installed.apply_sumo_environment() == (None, None)
    assert not any(variable in os.environ for variable in TOOLCHAIN_VARIABLES)

    home = tmp_path / "sumo"
    (home / "bin").mkdir(parents=True)
    (home / "bin" / NETCONVERT).write_text("")
    monkeypatch.setenv("SUMO_HOME", str(home))
    assert installed.netconvert() == home / "bin" / NETCONVERT

    monkeypatch.setenv("CARLA_NETCONVERT", str(tmp_path / "named" / NETCONVERT))
    monkeypatch.setenv("PROJ_DATA", str(tmp_path / "proj"))
    assert installed.netconvert() == tmp_path / "named" / NETCONVERT
    assert installed.proj_data() == tmp_path / "proj"


# -- the defaults the tools take from a layout ----------------------------------------------------------

def test_installed_the_site_profile_is_the_current_folder_and_the_package_s_catalogue(installed):
    profile = SiteProfile.discover(installed, None, environ={})
    work = installed.work
    assert profile.value("paths.scenario_root") == str(work / "scenarios")
    assert profile.value("paths.world_package_root") == str(work / "world-packages")
    assert profile.value("paths.capture_root") == str(work / "captures")
    assert profile.value("paths.runs_root") == str(work / "runs")
    assert profile.value("paths.catalogue") == str(installed.catalogue)
    assert profile.get("paths.catalogue").provenance == "the catalogue installed with carlacontrol"
    # No repository build to fall back on: the session searches, and the profile says so.
    assert profile.value("sumo.home") is None


def test_from_a_checkout_the_site_profile_is_unchanged():
    profile = SiteProfile.discover(_REPO, None, environ={})
    assert profile.value("paths.catalogue") == str(CATALOGUE)
    assert profile.value("paths.capture_root") == str(_REPO / "Build" / "captures")
    assert SiteProfile.discover(ToolLayout(_REPO), None, environ={}).values == profile.values


def test_the_world_build_s_defaults_follow_the_layout(installed):
    from_checkout = CarlaControlArgumentParser(str(_REPO)).parse_args([])
    assert from_checkout.osm == str(_REPO / "Import" / "Lakeview_Carson.osm")
    assert from_checkout.record_dir == str(_REPO / "Build" / "SCTMV_recordings")

    # Installed there is no Import/ to take an extract from: it has to be named.
    from_installed = CarlaControlArgumentParser(installed).parse_args([])
    assert from_installed.osm is None
    assert from_installed.record_dir == str(installed.work / "SCTMV_recordings")


# -- the package data a wheel carries ------------------------------------------------------------------

def _setuptools_configuration() -> dict:
    return tomllib.loads((PROJECT / "pyproject.toml").read_text(encoding="utf-8"))["tool"]["setuptools"]


def _install_as_pyproject_maps_it(site: Path) -> None:
    """Lay the package out as an installation would, from pyproject.toml's own mapping: each listed
    package's modules, and its package data, from the folder package-dir maps it to."""
    configuration = _setuptools_configuration()
    folders = configuration["package-dir"]
    data = configuration.get("package-data", {})
    for package in configuration["packages"]:
        source = PROJECT / folders[package] if package in folders \
            else PROJECT / folders[""] / Path(*package.split("."))
        target = site / Path(*package.split("."))
        target.mkdir(parents=True, exist_ok=True)
        for item in source.iterdir():
            wanted = item.suffix == ".py" or any(
                fnmatch.fnmatch(item.name, pattern) for pattern in data.get(package, []))
            if item.is_file() and wanted:
                shutil.copyfile(item, target / item.name)


def test_an_installation_laid_out_from_pyproject_resolves_its_own_data(tmp_path):
    pytest.importorskip("carlanet", reason="importing carlacontrol loads carlanet and its assemblies")
    site = tmp_path / "site"
    _install_as_pyproject_maps_it(site)
    work = tmp_path / "work"
    work.mkdir()
    probe = ("import json\n"
             "from carlacontrol.ToolLayout import ToolLayout\n"
             "layout = ToolLayout.current()\n"
             "print(json.dumps({'installed': layout.installed, 'work': str(layout.work),\n"
             "                  'catalogue': str(layout.catalogue),\n"
             "                  'vehicle_types': str(layout.vehicle_types),\n"
             "                  'schema': str(layout.run_configuration_schema)}))\n")
    completed = subprocess.run([sys.executable, "-c", probe], cwd=work, capture_output=True,
                               text=True, timeout=300,
                               env={**os.environ, "PYTHONPATH": str(site)}, check=False)
    assert completed.returncode == 0, completed.stderr
    resolved = json.loads(completed.stdout.strip().splitlines()[-1])
    assert resolved["installed"] is True
    assert Path(resolved["work"]) == work.resolve()
    data = (site / "carlacontrol" / "data").resolve()
    for key, repository_file in (("catalogue", CATALOGUE), ("vehicle_types", VEHICLE_TYPES),
                                 ("schema", SCHEMA)):
        path = Path(resolved[key])
        assert path.is_relative_to(data), path
        assert path.read_bytes() == repository_file.read_bytes()


def test_a_wheel_built_from_the_project_carries_the_commands_and_the_data(tmp_path):
    pytest.importorskip("setuptools", reason="building a wheel here needs setuptools installed")
    project = tmp_path / "CarlaControl"
    for name in ("pyproject.toml", "MANIFEST.in", "README.md", "LICENSE"):
        project.mkdir(exist_ok=True)
        shutil.copyfile(PROJECT / name, project / name)
    for name in ("src", "catalogue", "schemas"):
        shutil.copytree(PROJECT / name, project / name,
                        ignore=shutil.ignore_patterns("__pycache__", "*.egg-info"))
    completed = subprocess.run([sys.executable, "-m", "pip", "wheel", "--no-deps",
                                "--no-build-isolation", "-w", str(tmp_path / "wheels"),
                                str(project)], capture_output=True, text=True, timeout=600,
                               check=False)
    assert completed.returncode == 0, completed.stdout + completed.stderr
    wheel = next((tmp_path / "wheels").glob("carlacontrol-*.whl"))
    with zipfile.ZipFile(wheel) as archive:
        names = set(archive.namelist())
        entry_points = next(archive.read(name).decode() for name in names
                            if name.endswith("entry_points.txt"))
    assert {"carlacontrol/data/catalogue/vehicles.catalogue.json",
            "carlacontrol/data/catalogue/vehicles.vtypes.rou.xml",
            "carlacontrol/data/schemas/run_configuration.schema.json",
            # The schemas of what a capture writes, the XSD and the Table Schema among them.
            "carlacontrol/data/schemas/truth_sidecar.xsd",
            "carlacontrol/data/schemas/png_chunk_capture.schema.json",
            "carlacontrol/data/schemas/run_manifest.schema.json",
            "carlacontrol/data/schemas/world_truth_track.tableschema.json",
            "carlacontrol/data/schemas/world_truth_track_summary.schema.json",
            "carlacontrol/commands/__init__.py"} <= names
    for command, target in tomllib.loads(
            (PROJECT / "pyproject.toml").read_text(encoding="utf-8"))["project"]["scripts"].items():
        assert f"{command} = {target}" in entry_points
