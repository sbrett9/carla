"""A world's own road types reach the network the world publishes, and every scenario on that world.

`07_Scenario_Authoring.md` D7.9 and D7.32: what a road admits is decided when the world is built, by
the netconvert run that writes the world's network and its OpenDRIVE, and a scenario runs the world's
network byte for byte. So a world whose roads must admit vehicles SUMO's own type map keeps off them
is built with a type map of its own (`carlacontrol.NetconvertTypeMap`), not given a network rewritten
after the fact.

Held here, in order: the map is found and validated before anything is built; the world build passes
SUMO's own map first and the world's second, after every other extra argument, and refuses a second
`--type-files`; a real conversion admits what the map sets and changes nothing else; and the Shahid
Bahonar world, rebuilt with its map -- `Import/Shahid_Bahonar_Port.typ.xml` -- records the invocation
that gives the network it carries, and compiles the sizing scenario's guard rota, all 335 postings,
where the same invocation without the map refuses every one.

What these cannot see: a type map is keyed on road type, so the tests also pin that a private service
road and a public one take the same permissions -- netconvert 1.27 reads `access` only as `access=no`.
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import os
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.AuthoringReferenceSet import AuthoringReferenceSet  # noqa: E402
from carlacontrol.NetconvertTypeMap import (  # noqa: E402
    DEFAULT_MAP,
    NetconvertTypeMap,
    NetconvertTypeMapError,
)
from carlacontrol.NetworkFingerprint import NetworkFingerprint  # noqa: E402
from carlacontrol.ScenarioCompiler import ScenarioCompiler  # noqa: E402
from carlacontrol.SumoInstallation import SumoInstallation  # noqa: E402
from carlacontrol.WorldBuilder import WorldBuilder  # noqa: E402

STAGED_SUMO = _REPO / "Build" / "sumo-install"
EXE = ".exe" if os.name == "nt" else ""
NETCONVERT = STAGED_SUMO / "bin" / f"netconvert{EXE}"
FIXTURES = _REPO / "CarlaNet" / "test" / "CarlaNet.Tests" / "Map" / "Fixtures" / "Network"
WORLD_ARGV = [line for line in (FIXTURES / "world_build_argv.txt").read_text(encoding="utf-8")
              .splitlines() if line]
ROAD_FILTER = ["--keep-edges.by-vclass", "passenger", "--keep-edges.components", "1",
               "--remove-edges.isolated", "true"]

BAHONAR_MAP = _REPO / "Import" / "Shahid_Bahonar_Port.typ.xml"
BAHONAR_PACKAGE = _REPO / "Build" / "world-packages" / "Shahid_Bahonar_Port.cwp"
BAHONAR_OSM = _REPO / "Build" / "sumo-smoketest" / "Shahid_Bahonar_Port_clipped.osm"
CATALOGUE = _REPO / "CarlaControl" / "catalogue" / "vehicles.catalogue.json"
SHIPPED_ROUTES = (Path(__file__).resolve().parent / "fixtures"
                  / "Shahid_Bahonar_Port_PatternOfLife.shipped.rou.xml")

SERVICE_ARMY = """<?xml version="1.0" encoding="UTF-8"?>
<types>
    <type id="highway.service" allow="delivery pedestrian bicycle army authority"/>
</types>
"""

# The fixture street layout with two service roads off East Street's end: one public, one tagged
# access=private, so a conversion shows what a type map sets and what it cannot tell apart.
SERVICE_OSM = """<?xml version="1.0" encoding="utf-8"?>
<osm version="0.6" generator="type map fixture">
  <bounds minlat="39.4986500" minlon="-104.9029250" maxlat="39.5013500" maxlon="-104.8970750"/>
  <node id="100" lat="39.5000000" lon="-104.9023400" version="1"/>
  <node id="101" lat="39.5000000" lon="-104.9011700" version="1"/>
  <node id="102" lat="39.5000000" lon="-104.9000000" version="1"/>
  <node id="104" lat="39.5000000" lon="-104.8976600" version="1"/>
  <node id="300" lat="39.5009000" lon="-104.8976600" version="1"/>
  <node id="301" lat="39.4991000" lon="-104.8976600" version="1"/>
  <way id="900" version="1">
    <nd ref="100"/><nd ref="101"/><nd ref="102"/>
    <tag k="highway" v="residential"/><tag k="name" v="West Street"/>
  </way>
  <way id="901" version="1">
    <nd ref="102"/><nd ref="104"/>
    <tag k="highway" v="residential"/><tag k="name" v="East Street"/>
  </way>
  <way id="910" version="1">
    <nd ref="104"/><nd ref="300"/>
    <tag k="highway" v="service"/>
  </way>
  <way id="911" version="1">
    <nd ref="104"/><nd ref="301"/>
    <tag k="highway" v="service"/><tag k="access" v="private"/>
  </way>
</osm>
"""


def write(path: Path, text: str) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")
    return path


def world_args(osm: Path, **changes) -> argparse.Namespace:
    """The arguments a world build reads when it assembles netconvert's extra arguments."""
    values = {"lat": 39.5, "lon": -104.9, "no_road_filter": True, "road_offset_east": 0.0,
              "road_offset_north": 0.0, "netconvert_arg": [], "type_map": None, "osm": str(osm)}
    values.update(changes)
    return argparse.Namespace(**values)


def builder() -> WorldBuilder:
    return WorldBuilder(str(_REPO), str(NETCONVERT), str(STAGED_SUMO / "share" / "proj"))


def extra_arguments(world_builder: WorldBuilder, args: argparse.Namespace) -> list[str]:
    return [str(a) for a in world_builder.make_osm_conversion_options(args).ExtraArgs]


def require_staged_netconvert() -> None:
    if not NETCONVERT.exists() or not (STAGED_SUMO / DEFAULT_MAP).exists():
        pytest.skip("no staged SUMO installation with its type maps")


def convert(osm: Path, argv: list[str], out_dir: Path, name: str) -> Path:
    """Run the staged netconvert with a world build's argument list over `osm`."""
    out_dir.mkdir(parents=True, exist_ok=True)
    network, opendrive = out_dir / f"{name}.net.xml", out_dir / f"{name}.xodr"
    argv = list(argv)
    for flag, value in (("--osm-files", str(osm)), ("--output-file", str(network)),
                        ("--opendrive-output", str(opendrive))):
        argv[argv.index(flag) + 1] = value
    env = {**os.environ, "PROJ_DATA": str(STAGED_SUMO / "share" / "proj")}
    done = subprocess.run([str(NETCONVERT), *argv], capture_output=True, text=True, env=env)
    assert done.returncode == 0, done.stderr[-2000:]
    return network


def lane_permissions(network: Path) -> dict[str, str | None]:
    """Each normal edge's first-lane `allow`, keyed by the OSM way it came from."""
    permissions = {}
    for edge in ET.parse(network).getroot().findall("edge"):
        if edge.get("function") == "internal":
            continue
        way = edge.get("id").lstrip("-").split("#", 1)[0]
        permissions[way] = edge.find("lane").get("allow")
    return permissions


# ---- the map itself ----------------------------------------------------------------------------------

def test_the_map_beside_the_extract_is_found_by_the_extract_s_name(tmp_path):
    extract = write(tmp_path / "Import" / "Some_Port.osm", SERVICE_OSM)
    assert NetconvertTypeMap.discover(extract) is None
    beside = write(tmp_path / "Import" / "Some_Port.typ.xml", SERVICE_ARMY)
    assert NetconvertTypeMap.beside(extract) == beside
    assert NetconvertTypeMap.discover(extract) == beside


@pytest.mark.parametrize(("text", "problem"), [
    ("<types><type id='highway.service'", "is not XML"),
    ("<edges><type id='highway.service' allow='army'/></edges>", "root element is <edges>"),
    ("<types><type allow='army'/></types>", "has no id"),
])
def test_a_malformed_map_is_refused_before_anything_is_built(tmp_path, text, problem):
    with pytest.raises(NetconvertTypeMapError) as refused:
        NetconvertTypeMap.load(write(tmp_path / "bad.typ.xml", text))
    assert problem in str(refused.value)


def test_every_problem_is_named_at_once(tmp_path):
    with pytest.raises(NetconvertTypeMapError) as refused:
        NetconvertTypeMap.load(write(tmp_path / "bad.typ.xml",
                                     "<edges><type/><type id=''/></edges>"))
    assert len(refused.value.problems) == 3


def test_sumo_s_own_map_is_named_first_and_both_are_absolute(tmp_path):
    type_map = NetconvertTypeMap.load(write(tmp_path / "w.typ.xml", SERVICE_ARMY))
    home = tmp_path / "sumo"
    write(home / DEFAULT_MAP, "<types/>")
    option, files = type_map.netconvert_arguments(home)
    assert option == "--type-files"
    assert files.split(",") == [str((home / DEFAULT_MAP).resolve()), str(type_map.path)]
    assert type_map.describe() == ["highway.service: allow=delivery pedestrian bicycle army authority"]


def test_a_map_is_refused_where_the_installation_carries_no_default_map(tmp_path):
    """Given alone, netconvert would discard every road type the map does not restate."""
    type_map = NetconvertTypeMap.load(write(tmp_path / "w.typ.xml", SERVICE_ARMY))
    with pytest.raises(FileNotFoundError):
        type_map.netconvert_arguments(tmp_path / "no-sumo-here")


# ---- the world build -----------------------------------------------------------------------------------

def test_the_world_build_passes_the_map_after_every_other_extra_argument(tmp_path):
    require_staged_netconvert()
    extract = write(tmp_path / "Some_Port.osm", SERVICE_OSM)
    write(tmp_path / "Some_Port.typ.xml", SERVICE_ARMY)
    world_builder = builder()
    args = world_args(extract, netconvert_arg=["--remove-edges.by-type highway.footway"])
    assert world_builder.load_type_map(args)
    assert extra_arguments(world_builder, args) == [
        "--remove-edges.by-type", "highway.footway", "--type-files",
        f"{(STAGED_SUMO / DEFAULT_MAP).resolve()},{(tmp_path / 'Some_Port.typ.xml').resolve()}"]


def test_a_named_map_is_used_and_an_extract_with_none_passes_no_type_files(tmp_path):
    require_staged_netconvert()
    extract = write(tmp_path / "Plain.osm", SERVICE_OSM)
    world_builder = builder()
    args = world_args(extract)
    assert world_builder.load_type_map(args)
    assert "--type-files" not in extra_arguments(world_builder, args)
    named = write(tmp_path / "elsewhere" / "types.typ.xml", SERVICE_ARMY)
    args = world_args(extract, type_map=str(named))
    assert world_builder.load_type_map(args)
    assert extra_arguments(world_builder, args)[-1].endswith(str(named.resolve()))


def test_the_world_build_refuses_a_map_and_a_type_files_argument_together(tmp_path):
    """netconvert reads the option once; a second list would replace the first."""
    require_staged_netconvert()
    extract = write(tmp_path / "Some_Port.osm", SERVICE_OSM)
    write(tmp_path / "Some_Port.typ.xml", SERVICE_ARMY)
    args = world_args(extract, netconvert_arg=["--type-files other.typ.xml"])
    assert not builder().load_type_map(args)


def test_the_world_build_refuses_a_malformed_map(tmp_path):
    require_staged_netconvert()
    extract = write(tmp_path / "Some_Port.osm", SERVICE_OSM)
    write(tmp_path / "Some_Port.typ.xml", "<types><type/></types>")
    world_builder = builder()
    assert not world_builder.load_type_map(world_args(extract))
    assert world_builder.type_map is None


# ---- what a conversion does with it --------------------------------------------------------------------

def test_the_network_admits_what_the_map_sets_and_nothing_else(tmp_path):
    require_staged_netconvert()
    extract = write(tmp_path / "Some_Port.osm", SERVICE_OSM)
    assert WORLD_ARGV[-len(ROAD_FILTER):] == ROAD_FILTER
    base = WORLD_ARGV[:-len(ROAD_FILTER)]
    base[base.index("--proj") + 1] = ("+proj=tmerc +lat_0=39.5 +lon_0=-104.9 +k=1 +x_0=0 +y_0=0 "
                                      "+ellps=WGS84 +units=m +no_defs")
    plain = lane_permissions(convert(extract, base, tmp_path, "plain"))
    write(tmp_path / "Some_Port.typ.xml", SERVICE_ARMY)
    world_builder = builder()
    args = world_args(extract)
    assert world_builder.load_type_map(args)
    typed = lane_permissions(convert(extract, base + extra_arguments(world_builder, args),
                                     tmp_path, "typed"))

    assert "army" not in plain["910"].split() and "army" not in plain["911"].split()
    assert {"army", "authority"} <= set(typed["910"].split())
    assert typed["900"] == plain["900"] and typed["901"] == plain["901"], "residential is untouched"
    # What a type map cannot see: the private service road takes the public one's permissions.
    assert typed["911"] == typed["910"]


# ---- the Shahid Bahonar world ---------------------------------------------------------------------------

def bahonar_generator():
    path = _REPO / "CarlaControl" / "scripts" / "make_bahonar_scenario.py"
    spec = importlib.util.spec_from_file_location("make_bahonar_scenario", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def shipped_guard_postings() -> dict[str, float]:
    """Every guard trip of the sizing scenario's shipped route file: id to departure second."""
    return {trip.get("id"): float(trip.get("depart"))
            for trip in ET.parse(SHIPPED_ROUTES).getroot().iter("trip")
            if trip.get("id").startswith("guard_")}


def bahonar_package(network: Path, argv: list[str], directory: Path,
                    installation: SumoInstallation) -> Path:
    """The Bahonar package carrying `network`, recording it and the argument list."""
    text = network.read_text(encoding="utf-8")
    opendrive = network.with_suffix("").with_suffix(".xodr").read_bytes()
    package = directory / "Shahid_Bahonar_Port.cwp"
    with zipfile.ZipFile(BAHONAR_PACKAGE) as source, \
            zipfile.ZipFile(package, "w", zipfile.ZIP_STORED) as sink:
        for info in source.infolist():
            if info.filename in ("places.json", "areas.resolved.json", "areas.aoi.geojson",
                                 "solar.json", "bareearth.bin"):
                continue
            data = source.read(info.filename)
            if info.filename == "world.json":
                manifest = json.loads(data)
                manifest["NetworkFingerprint"] = NetworkFingerprint.of_text(text)
                manifest["NetconvertArgv"] = argv
                data = json.dumps(manifest, indent=2).encode("utf-8")
            elif info.filename == "map.net.xml":
                data = text.encode("utf-8")
            elif info.filename == "map.xodr":
                data = opendrive
            sink.writestr(info.filename, data)
    AuthoringReferenceSet(package, installation, None).publish()
    return package


def compile_guard_rota(package: Path, directory: Path, installation: SumoInstallation):
    """The generator's guard rota alone, under the midnight epoch the sizing scenario shipped with,
    so its departure seconds are the shipped file's."""
    generator = bahonar_generator()
    rota = generator.BahonarPatternOfLifeSpecification(
        None, days=7, no_show_day=4, no_show_hour=7, no_show_tower=3, step_length_s=1.0,
        seed=42).guard_rota()
    places = {"apron": {"edge": generator.PLACE_EDGES["apron"]}}
    for index, (edge, position) in enumerate(generator.TOWER_POSTS):
        places[f"tower_{index:02d}"] = {"lane": f"{edge}_0", "offset_m": position}
    with zipfile.ZipFile(package) as archive:
        fingerprint = NetworkFingerprint.of_text(archive.read("map.net.xml").decode("utf-8"))
    specification = {
        "spec_version": 1, "scenario_id": "bahonar_guard_rota",
        "scenario_name": "Bahonar guard rota",
        "description": "The sizing scenario's seven-day guard rota as one rota block, with the "
                       "no-show.",
        "world": {"package": str(package), "network_fingerprint": fingerprint},
        "epoch": {"epoch_version": 1, "civil_datetime": "2026-03-21T00:00:00+03:30",
                  "utc_offset_hours": 3.5, "utc_datetime": "2026-03-20T20:30:00Z",
                  "calendar_advances": True, "dst_in_effect": False,
                  "time_zone_id": "Asia/Tehran"},
        "illumination": {"illumination_version": 1, "policy": "freeze_at_window_start"},
        "seeds": {"sumo": 42}, "simulation": {"end": "d7 00:00", "step_length_s": 1.0},
        "catalogue": str(CATALOGUE),
        "vehicle_classes": [{"class_id": "guard", "blueprints": ["vehicle.nissan.patrol"],
                             "sumo_vclass": "army", "share": 0.0}],
        "places": places,
        "place_sets": {"guard_towers": [f"tower_{i:02d}" for i in range(16)]},
        "rotas": [rota],
    }
    spec = directory / "bahonar.scenario.json"
    spec.parent.mkdir(parents=True, exist_ok=True)
    spec.write_text(json.dumps(specification, indent=2), encoding="utf-8")
    return ScenarioCompiler(installation).compile(spec, directory / "out")


def without_type_files(argv: list[str]) -> list[str]:
    """An argument list with its `--type-files` option and value taken out."""
    index = argv.index("--type-files")
    return argv[:index] + argv[index + 2:]


def test_the_bahonar_world_carries_its_type_map_and_its_guard_rota_compiles_on_it(tmp_path):
    """The acceptance for moving the fence into the world build, on the real extract and package.

    The world was rebuilt with its map. Its recorded invocation over the clipped extract gives the
    network it carries; the world build assembles exactly its recorded extra arguments from the map
    beside the extract; the guard rota compiles on the package with the shipped ids and departure
    seconds; and the same invocation without the map -- the world before it had one -- refuses the
    rota on every one of its 335 postings.
    """
    require_staged_netconvert()
    if not (BAHONAR_PACKAGE.exists() and BAHONAR_OSM.exists()):
        pytest.skip("the Shahid Bahonar world package and clipped extract are not built here")
    installation = SumoInstallation.locate(STAGED_SUMO)
    manifest = json.loads(zipfile.ZipFile(BAHONAR_PACKAGE).read("world.json"))
    recorded = list(manifest["NetconvertArgv"])
    if "--type-files" not in recorded:
        pytest.fail("the Shahid Bahonar package records no --type-files: it predates its type map; "
                    "rebuild it (07 §9.8)")
    assert recorded[recorded.index("--type-files") + 1].endswith(str(BAHONAR_MAP.resolve()))

    # The recorded invocation is the network the package carries.
    carried = convert(BAHONAR_OSM, recorded, tmp_path / "carried", "carried")
    assert NetworkFingerprint.of_file(carried) == manifest["NetworkFingerprint"]

    # The world build, given the package's other extra arguments, finds the map beside the extract
    # and passes it last: exactly the extra arguments the package records.
    extra = list(manifest["NetconvertExtraArgs"])
    world_builder = builder()
    args = world_args(_REPO / "Import" / "Shahid_Bahonar_Port.osm", lat=27.15012, lon=56.18065,
                      netconvert_arg=[" ".join(without_type_files(extra))])
    assert world_builder.load_type_map(args)
    assert world_builder.type_map.path == BAHONAR_MAP.resolve()
    assert extra_arguments(world_builder, args) == extra

    # The rota compiles on the package itself.
    result = compile_guard_rota(BAHONAR_PACKAGE, tmp_path / "fenced", installation)
    assert not result.refused, [str(f) for f in result.findings.findings if f.outcome == "refuse"]
    routes = ET.parse(result.files["routes"]).getroot()
    compiled = {e.get("id"): float(e.get("depart")) for e in routes if e.tag == "vehicle"}
    assert compiled == shipped_guard_postings()
    assert len(compiled) == 335

    # Control: without the map, army may drive none of the posts.
    unfenced_argv = without_type_files(recorded)
    unfenced = convert(BAHONAR_OSM, unfenced_argv, tmp_path / "unfenced", "unfenced")
    refused = compile_guard_rota(
        bahonar_package(unfenced, unfenced_argv, tmp_path / "unfenced", installation),
        tmp_path / "unfenced", installation)
    assert refused.refused
    assert {f.subject for f in refused.findings.by_check(10)} == \
        {f"rotas[guard_posting] entry {entry}" for entry in shipped_guard_postings()}
