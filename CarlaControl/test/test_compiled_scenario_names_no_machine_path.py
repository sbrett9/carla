"""A compiled scenario names no folder of the machine that compiled it.

A compiled scenario is copied to other machines and read there. Its lock copies the netconvert
invocation the world package records, and a world build records the absolute paths it handed
netconvert: the clipped extract in the build's scratch folder, SUMO's own type map inside the SUMO
installation, a world's type map beside its extract. The resolution report named the SUMO
installation it routed with by its folder. Now:

* a path inside the folder the lock is written to is named relative to it;
* a file of a SUMO installation is named under `${SUMO_HOME}`, the installation being identified by
  the release the lock records beside it;
* any other file is named by its file name alone;
* the report names the routing SUMO by its release and how it was found, never by its folder;
* a sweep member's specification names its world package, catalogue and vocabulary imports relative
  to itself, so a member still compiles where it is moved with them.

No reader branches on the recorded invocation or the routing SUMO's folder, so locks and reports
compiled before this read as they did.
"""
from __future__ import annotations

import json
import re
import sys
import zipfile
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))
sys.path.insert(0, str(Path(__file__).resolve().parent))

from ScenarioWorldFixture import CATALOGUE, ScenarioWorldFixture  # noqa: E402

from carlacontrol.PortablePath import SUMO_HOME_VARIABLE, PortablePath  # noqa: E402
from carlacontrol.ScenarioCompiler import ScenarioCompiler  # noqa: E402
from carlacontrol.ScenarioPackage import ScenarioPackage  # noqa: E402
from carlacontrol.ScenarioSweep import ScenarioSweep  # noqa: E402

EXAMPLES = _REPO / "CarlaControl" / "skills" / "sumo-traffic-scenarios" / "examples"
IMPORT = _REPO / "Import"

# Anything that reads as a folder of some machine: a Windows drive or share, or a POSIX root that
# holds users' or scratch files.
MACHINE_PATH = re.compile(r"(?<![\w$])[A-Za-z]:[\\/]|\\\\[A-Za-z0-9]|(?<![\w.])/(?:home|Users|tmp|mnt|var)/")

BUILDER = "C:\\Users\\builder"
BUILT_OSM = f"{BUILDER}\\AppData\\Local\\Temp\\StreetLayout_clipped.osm"
BUILT_TYPE_FILES = ("D:\\sumo-1.27.0\\data\\typemap\\osmNetconvert.typ.xml,"
                    f"{BUILDER}\\carla\\Import\\StreetLayout.typ.xml")
BUILT_ARGV = ["--osm-files", BUILT_OSM, "--type-files", BUILT_TYPE_FILES,
              "--opendrive-output", "<opendrive-output>", "--output-file", "<output-file>",
              "--proj", "+proj=tmerc +lat_0=39.5 +lon_0=-104.9 +k=1 +x_0=0 +y_0=0 +ellps=WGS84",
              "--junctions.join-exclude", "2279085566,582784737"]
BUILT_NETCONVERT = "D:\\sumo-1.27.0\\bin\\netconvert.exe"


def strings_in(value) -> list[str]:
    if isinstance(value, str):
        return [value]
    if isinstance(value, dict):
        return [s for v in value.values() for s in strings_in(v)]
    if isinstance(value, list):
        return [s for v in value for s in strings_in(v)]
    return []


# ---- naming one path ------------------------------------------------------------------------------

def test_a_file_beside_the_record_is_named_relative_to_it(tmp_path):
    beside = tmp_path / "out"
    assert PortablePath.of(str(beside / "world.typ.xml"), beside) == "world.typ.xml"
    assert PortablePath.of(str(beside / "maps" / "a.osm"), beside) == "maps/a.osm"


def test_a_file_of_a_sumo_installation_is_named_under_sumo_home(tmp_path):
    home = Path("D:/sumo-1.27.0")
    assert (PortablePath.of("D:\\sumo-1.27.0\\data\\typemap\\osmNetconvert.typ.xml", tmp_path, [home])
            == f"{SUMO_HOME_VARIABLE}/data/typemap/osmNetconvert.typ.xml")
    assert PortablePath.sumo_home_of(BUILT_NETCONVERT) == [Path("D:/sumo-1.27.0")]
    assert PortablePath.sumo_home_of("") == []


def test_any_other_file_is_named_by_its_name_and_a_value_that_is_no_path_is_kept(tmp_path):
    assert PortablePath.of(f"{BUILDER}\\AppData\\Local\\Temp\\x_clipped.osm", tmp_path) == "x_clipped.osm"
    assert PortablePath.of("/home/builder/build/x_clipped.osm", tmp_path) == "x_clipped.osm"
    for kept in ("map.osm", "<output-file>", "+proj=tmerc +lat_0=39.5", "2279085566,582784737", "25"):
        assert PortablePath.of(kept, tmp_path) == kept


def test_a_command_line_is_named_file_by_file(tmp_path):
    named = PortablePath.argv(BUILT_ARGV, tmp_path, PortablePath.sumo_home_of(BUILT_NETCONVERT))
    assert named[named.index("--osm-files") + 1] == "StreetLayout_clipped.osm"
    assert named[named.index("--type-files") + 1] == (
        f"{SUMO_HOME_VARIABLE}/data/typemap/osmNetconvert.typ.xml,StreetLayout.typ.xml")
    assert named[named.index("--junctions.join-exclude") + 1] == "2279085566,582784737"
    assert named[named.index("--output-file") + 1] == "<output-file>"
    assert not any(MACHINE_PATH.search(argument) for argument in named)


def test_a_path_a_generated_file_follows_is_relative_to_that_file(tmp_path):
    (tmp_path / "w").mkdir()
    (tmp_path / "out" / "member").mkdir(parents=True)
    assert PortablePath.relative(tmp_path / "w" / "World.cwp", tmp_path / "out" / "member") == \
        "../../w/World.cwp"


# ---- a compiled scenario --------------------------------------------------------------------------

@pytest.fixture(scope="module")
def installation():
    try:
        return ScenarioWorldFixture.locate_sumo()
    except FileNotFoundError as missing:
        pytest.skip(f"no SUMO with duarouter: {missing}")


def built_elsewhere(package: Path) -> None:
    """The fixture package as another machine's world build records it: absolute input paths."""
    with zipfile.ZipFile(package) as archive:
        entries = {name: archive.read(name) for name in archive.namelist()}
    manifest = json.loads(entries["world.json"])
    argv = list(manifest["NetconvertArgv"])
    argv[argv.index("--osm-files") + 1] = BUILT_OSM
    manifest["NetconvertArgv"] = [*argv, "--type-files", BUILT_TYPE_FILES]
    manifest["NetconvertPath"] = BUILT_NETCONVERT
    entries["world.json"] = json.dumps(manifest, indent=2).encode()
    with zipfile.ZipFile(package, "w", zipfile.ZIP_STORED) as archive:
        for name, data in entries.items():
            archive.writestr(name, data)


@pytest.fixture(scope="module")
def compiled(tmp_path_factory, installation):
    directory = tmp_path_factory.mktemp("compiled")
    world = ScenarioWorldFixture(directory / "world", installation)
    built_elsewhere(world.package)
    result = ScenarioCompiler(installation).compile(world.write(world.specification()),
                                                    directory / "out")
    assert not result.refused, [str(f) for f in result.findings.findings if f.outcome == "refuse"]
    return directory, result


def test_the_lock_names_the_world_build_s_files_portably(compiled):
    _, result = compiled
    argv = result.lock["world"]["netconvert_argv"]
    assert argv[argv.index("--osm-files") + 1] == "StreetLayout_clipped.osm"
    assert argv[argv.index("--type-files") + 1] == (
        f"{SUMO_HOME_VARIABLE}/data/typemap/osmNetconvert.typ.xml,StreetLayout.typ.xml")


def test_the_report_names_the_routing_sumo_by_its_release_not_its_folder(compiled, installation):
    _, result = compiled
    routing = result.report["world"]["routing_sumo"]
    assert "home" not in routing
    assert routing["version"] == (installation.version or "")
    assert routing["matched_by"] == installation.source


def test_no_file_the_compile_writes_names_a_folder_of_the_compiling_machine(compiled):
    directory, result = compiled
    written = [result.files[role] for role in ("lock", "resolution", "resolution_md", "config",
                                               "routes", "supervision")]
    for path in written:
        text = path.read_text(encoding="utf-8")
        for folder in (directory, directory.resolve(), Path.home()):
            assert str(folder).lower() not in text.lower(), path.name
        if path.suffix == ".json":
            offending = [s for s in strings_in(json.loads(text)) if MACHINE_PATH.search(s)]
        else:
            offending = MACHINE_PATH.findall(text)
        assert not offending, (path.name, offending[:5])


def test_a_sweep_member_names_what_it_reads_relative_to_itself(installation, tmp_path):
    """On one drive, as here; a file on another drive has no relative path and keeps its absolute one."""
    world = ScenarioWorldFixture(tmp_path / "world", installation)
    (world.directory / CATALOGUE.name).write_bytes(CATALOGUE.read_bytes())
    for example in EXAMPLES.glob("counterfactual/*.json"):
        document = json.loads(example.read_text(encoding="utf-8"))
        if "catalogue" in document:
            document["catalogue"] = CATALOGUE.name
        world.write(document, example.name)
    index = ScenarioSweep(installation).compile(world.directory / "probe_standoff_pairs.sweep.json",
                                                tmp_path / "out")
    assert index["outcome"] != "refused", index["findings"]
    for member in index["members"]:
        specification = json.loads((tmp_path / "out" / member["specification"]).read_text(
            encoding="utf-8"))
        named = [specification["world"]["package"], specification["catalogue"],
                 *specification.get("vocabulary", {}).get("import", [])]
        assert all(not PortablePath.is_absolute(path) for path in named), named
        assert specification["world"]["package"].endswith("world/StreetLayout.cwp")


@pytest.mark.parametrize("lock", sorted(IMPORT.glob("*.lock.json")), ids=lambda p: p.stem)
def test_a_lock_compiled_before_reads_as_it_did(lock):
    """The shipped locks still carry the absolute paths they were compiled with; they read whole."""
    package = ScenarioPackage(lock)
    assert package.scenario_id
    assert package.world["package"].endswith(".cwp")
    assert isinstance(package.world.get("netconvert_argv", []), list)
