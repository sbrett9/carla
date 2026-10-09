"""The reference set is published into the world package, read back intact, and refused when wrong.

The world build writes a package with `CarlaNet.Map` and then publishes the authoring reference set
into it (`AuthoringReferenceSet`). Three properties are held here:

  * **The world the package already records is untouched.** Every entry `CarlaNet.Map` wrote comes
    out byte for byte, every entry is stored uncompressed -- the editor's importer reads nothing else
    -- and publishing again replaces the set rather than adding a second copy of any entry.
  * **What cannot be trusted is not read.** The reader refuses an area table whose recorded source
    digest disagrees with the GeoJSON beside it (C5 V5.11) and an entry declaring a schema version it
    does not implement.
  * **A bad file stops the world build before it starts**, and a good one ends up in the package the
    build writes. `WorldBuilder` is driven with a stand-in client, so the whole build path runs
    without a server.

Packages here are written as `CarlaNet.Map` writes them, holding the cross-shaped network the
world-build tests already use.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import sys
import zipfile
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.AreaOfInterestSource import AreaOfInterestSource  # noqa: E402
from carlacontrol.AuthoringReferenceSet import AuthoringReferenceSet  # noqa: E402
from carlacontrol.FormatVersion import FormatVersionError  # noqa: E402
from carlacontrol.GeodeticFrame import GeodeticFrame  # noqa: E402
from carlacontrol.OsmClipper import OsmClipper  # noqa: E402
from carlacontrol.SumoInstallation import SumoInstallation  # noqa: E402
from carlacontrol.WorldBuilder import WorldBuilder  # noqa: E402
from carlacontrol.WorldPackageReader import WorldPackageReader  # noqa: E402

FIXTURES = _REPO / "CarlaNet" / "test" / "CarlaNet.Tests" / "Map" / "Fixtures" / "Network"
NETWORK = (FIXTURES / "street_names_true.net.xml").read_text(encoding="utf-8")
EXTRACT = FIXTURES / "street_layout.osm"
STAGED_SUMO = _REPO / "Build" / "sumo-install"

MANIFEST = {
    "MapName": "StreetLayout",
    "OriginLatitude": 39.5,
    "OriginLongitude": -104.9,
    "GeoReferenceString": "+proj=tmerc +lat_0=39.5 +lon_0=-104.9 +k=1 +x_0=0 +y_0=0 +ellps=WGS84 "
                          "+units=m +no_defs",
    "StagingMinXMeters": -201.27, "StagingMinYMeters": -99.92,
    "StagingMaxXMeters": 201.27, "StagingMaxYMeters": 99.92,
    "StagingMarginMeters": 30.0,
    "NetconvertVersion": "Eclipse SUMO netconvert 1.27.0",
}
WORLD_ENTRIES = ("world.json", "map.xodr", "map.net.xml", "bareearth.bin")


@pytest.fixture(scope="module")
def installation() -> SumoInstallation:
    try:
        found = SumoInstallation.locate(STAGED_SUMO if STAGED_SUMO.exists() else None)
        found.sumo  # noqa: B018 -- raises when the installation has no sumo executable
    except FileNotFoundError as missing:
        pytest.skip(f"no SUMO to project with: {missing}")
    return found


def write_package(directory: Path, manifest: dict = MANIFEST) -> Path:
    """A package as `CarlaNet.Map` writes one: four stored entries."""
    directory.mkdir(parents=True, exist_ok=True)
    path = directory / f"{manifest['MapName']}.cwp"
    with zipfile.ZipFile(path, "w", zipfile.ZIP_STORED) as archive:
        archive.writestr("world.json", json.dumps(manifest, indent=2))
        archive.writestr("map.xodr", "<OpenDRIVE><header/></OpenDRIVE>")
        archive.writestr("map.net.xml", NETWORK)
        archive.writestr("bareearth.bin", b"CWP1" + bytes(range(256)))
    return path


def kerb_area(x: float = 50.0, y: float = 1.68, radius: float = 10.0) -> bytes:
    """A circle on East Street's eastbound lane, placed in CARLA metres."""
    longitude, latitude = GeodeticFrame(39.5, -104.9).to_geodetic(x, y)
    return json.dumps({"type": "FeatureCollection", "features": [{
        "type": "Feature", "geometry": {"type": "Point", "coordinates": [longitude, latitude]},
        "properties": {"id": "kerb", "name": "Kerb", "radius_m": radius}}]}).encode()


def source(raw: bytes, checked: bool = True) -> AreaOfInterestSource:
    bounds = OsmClipper.read_bounds(EXTRACT) if checked else None
    return AreaOfInterestSource.from_bytes(raw, "street_layout.aoi.geojson", bounds)


def entries(path: Path) -> list[zipfile.ZipInfo]:
    with zipfile.ZipFile(path) as archive:
        return archive.infolist()


# ---- publication ----------------------------------------------------------------------------------

def test_the_set_is_published_beside_an_untouched_world(tmp_path, installation):
    package = write_package(tmp_path)
    before = {name: zipfile.ZipFile(package).read(name) for name in WORLD_ENTRIES}
    raw = kerb_area()
    report = AuthoringReferenceSet(package, installation, source(raw)).publish()

    assert report.areas_published and report.refusals == []
    infos = entries(package)
    assert [i.filename for i in infos] == [*WORLD_ENTRIES, "places.json", "solar.json",
                                           "areas.resolved.json", "areas.aoi.geojson"]
    assert all(i.compress_type == zipfile.ZIP_STORED for i in infos)
    with zipfile.ZipFile(package) as archive:
        assert {name: archive.read(name) for name in WORLD_ENTRIES} == before

    reader = WorldPackageReader(package)
    table = reader.areas_of_interest()
    assert table["source_sha256"] == hashlib.sha256(raw).hexdigest()
    assert reader.areas_source() == raw
    assert [a["id"] for a in table["areas"]] == ["kerb"]
    assert reader.place_index()["coverage"]["named_edges"] == 10
    assert reader.solar_frame()["engine_time_zone"] == "-06:59:36"


def test_a_world_with_no_areas_publishes_an_empty_table_and_no_source(tmp_path):
    package = write_package(tmp_path)
    report = AuthoringReferenceSet(package, None, None).publish()
    assert report.areas_published
    reader = WorldPackageReader(package)
    assert reader.areas_of_interest()["areas"] == []
    assert reader.areas_source() is None


def test_publishing_again_replaces_the_set(tmp_path, installation):
    package = write_package(tmp_path)
    AuthoringReferenceSet(package, installation, source(kerb_area())).publish()
    AuthoringReferenceSet(package, None, None).publish()
    names = [i.filename for i in entries(package)]
    assert len(names) == len(set(names)), "no entry is written twice"
    assert "areas.aoi.geojson" not in names
    assert WorldPackageReader(package).areas_of_interest()["areas"] == []


def test_refused_areas_leave_the_rest_published_and_no_stale_areas(tmp_path, installation):
    package = write_package(tmp_path)
    AuthoringReferenceSet(package, installation, source(kerb_area())).publish()
    # Now the manifest's origin is 0.11 m from the network's: SUMO and the geographic frame disagree.
    moved = dict(MANIFEST, OriginLatitude=39.5 + 1e-6)
    with zipfile.ZipFile(package) as archive:
        kept = {n: archive.read(n) for n in archive.namelist() if n != "world.json"}
    with zipfile.ZipFile(package, "w", zipfile.ZIP_STORED) as archive:
        archive.writestr("world.json", json.dumps(moved))
        for name, data in kept.items():
            archive.writestr(name, data)

    report = AuthoringReferenceSet(package, installation, source(kerb_area())).publish()
    assert not report.areas_published
    assert report.refusals and report.refusals[0].startswith("V5.12")
    names = [i.filename for i in entries(package)]
    assert "places.json" in names and "solar.json" in names
    assert "areas.resolved.json" not in names and "areas.aoi.geojson" not in names
    assert WorldPackageReader(package).areas_of_interest() is None


def test_an_area_outside_the_network_is_refused_when_the_extract_had_no_bounds(tmp_path,
                                                                             installation):
    package = write_package(tmp_path)
    far = kerb_area(x=5000.0, y=0.0, radius=5.0)
    report = AuthoringReferenceSet(package, installation, source(far, checked=False)).publish()
    assert report.refusals and report.refusals[0].startswith("V5.3")


def test_no_sumo_means_no_areas_rather_than_areas_placed_some_other_way(tmp_path):
    package = write_package(tmp_path)
    report = AuthoringReferenceSet(package, None, source(kerb_area())).publish()
    assert not report.areas_published and "SUMO" in report.refusals[0]


# ---- reading ---------------------------------------------------------------------------------------

def rewrite(package: Path, replace: dict[str, bytes]) -> None:
    with zipfile.ZipFile(package) as archive:
        kept = {n: archive.read(n) for n in archive.namelist()}
    kept.update(replace)
    with zipfile.ZipFile(package, "w", zipfile.ZIP_STORED) as archive:
        for name, data in kept.items():
            archive.writestr(name, data)


def test_a_table_resolved_from_another_source_is_refused(tmp_path, installation):
    package = write_package(tmp_path)
    AuthoringReferenceSet(package, installation, source(kerb_area())).publish()
    rewrite(package, {"areas.aoi.geojson": kerb_area(x=60.0)})
    with pytest.raises(ValueError) as raised:
        WorldPackageReader(package).areas_of_interest()
    assert "not the areas this package declares" in str(raised.value)


def test_a_table_whose_source_went_missing_is_refused(tmp_path, installation):
    package = write_package(tmp_path)
    AuthoringReferenceSet(package, installation, source(kerb_area())).publish()
    with zipfile.ZipFile(package) as archive:
        kept = {n: archive.read(n) for n in archive.namelist() if n != "areas.aoi.geojson"}
    with zipfile.ZipFile(package, "w", zipfile.ZIP_STORED) as archive:
        for name, data in kept.items():
            archive.writestr(name, data)
    with pytest.raises(ValueError):
        WorldPackageReader(package).areas_of_interest()


@pytest.mark.parametrize(("entry", "field", "read"), [
    ("places.json", "place_index_version", WorldPackageReader.place_index),
    ("solar.json", "solar_frame_version", WorldPackageReader.solar_frame),
    ("areas.resolved.json", "resolved_version", WorldPackageReader.areas_of_interest),
])
def test_an_entry_of_a_newer_version_is_refused_by_name(tmp_path, entry, field, read):
    package = write_package(tmp_path)
    AuthoringReferenceSet(package, None, None).publish()
    future = json.loads(WorldPackageReader(package).entry_bytes(entry))
    future[field] = 2
    rewrite(package, {entry: json.dumps(future).encode()})
    with pytest.raises(FormatVersionError) as raised:
        read(WorldPackageReader(package))
    assert f"({entry}) declares {field} 2, and this reader supports {field} 1 and earlier" in \
        str(raised.value)


@pytest.mark.parametrize(("entry", "field", "read"), [
    ("places.json", "place_index_version", WorldPackageReader.place_index),
    ("solar.json", "solar_frame_version", WorldPackageReader.solar_frame),
    ("areas.resolved.json", "resolved_version", WorldPackageReader.areas_of_interest),
])
def test_an_entry_that_declares_no_version_is_version_one(tmp_path, entry, field, read):
    """The rule `world.json` keeps, and every reader in both languages: no version is version 1."""
    package = write_package(tmp_path)
    AuthoringReferenceSet(package, None, None).publish()
    unversioned = json.loads(WorldPackageReader(package).entry_bytes(entry))
    del unversioned[field]
    rewrite(package, {entry: json.dumps(unversioned).encode()})
    assert read(WorldPackageReader(package)) == unversioned


def test_a_package_from_before_the_set_reads_as_unpublished(tmp_path):
    reader = WorldPackageReader(write_package(tmp_path))
    assert (reader.areas_of_interest(), reader.place_index(), reader.solar_frame()) == \
        (None, None, None)


# ---- the world build -------------------------------------------------------------------------------

class _StandInClient:
    """Enough of a client for `WorldBuilder.build_world`: records what it was asked to do."""

    def __init__(self, package_template: Path | None = None) -> None:
        self.calls: list[str] = []
        self.package_template = package_template

    def set_timeout(self, _seconds) -> None:
        pass

    def generate_world_from_osm_with_elevation(self, *_args, **_kwargs) -> str:
        self.calls.append("generate")
        return "<OpenDRIVE><road id=\"1\"/></OpenDRIVE>"

    def write_world_package(self, directory, map_name, *_args) -> str:
        self.calls.append("write_world_package")
        target = Path(directory) / f"{map_name}.cwp"
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(self.package_template, target)
        return str(target)


def build_arguments(tmp_path: Path, extract: Path, **overrides) -> argparse.Namespace:
    values = dict(osm=str(extract), lat=39.5, lon=-104.9, step=10.0, no_road_filter=False,
                  height_align="drape", road_offset_east=0.0, road_offset_north=0.0,
                  ion_asset_id=1, ground_asset_id=1, ion_token="token", no_clip_bounds=True,
                  save=str(tmp_path / "out.xodr"), timeout=10.0, origin_height=None,
                  ground_collision=True, terrain_res=2.0, terrain_margin=30.0,
                  drape_cache_dir=None, emit_world_package=None, netconvert_arg=[], aoi=None)
    values.update(overrides)
    return argparse.Namespace(**values)


def copy_extract(tmp_path: Path) -> Path:
    extract = tmp_path / "StreetLayout.osm"
    shutil.copyfile(EXTRACT, extract)
    return extract


def netconvert_stand_in(tmp_path: Path) -> str:
    path = tmp_path / "netconvert.exe"
    path.write_text("")
    return str(path)


def test_a_malformed_areas_file_stops_the_build_before_it_starts(tmp_path):
    extract = copy_extract(tmp_path)
    bad = tmp_path / "bad.aoi.geojson"
    bad.write_text(json.dumps({"type": "FeatureCollection", "features": [
        {"type": "Feature", "geometry": {"type": "Point", "coordinates": [-104.9, 39.5]},
         "properties": {"id": "Kerb", "name": "Kerb"}}]}))
    client = _StandInClient()
    builder = WorldBuilder(str(_REPO), netconvert_stand_in(tmp_path), str(tmp_path))
    assert builder.build_world(client, build_arguments(tmp_path, extract, aoi=str(bad))) is False
    assert client.calls == [], "nothing is built for a world whose areas are refused"


def test_a_transposed_areas_file_beside_the_extract_stops_the_build(tmp_path):
    extract = copy_extract(tmp_path)
    (tmp_path / "StreetLayout.aoi.geojson").write_text(json.dumps({
        "type": "FeatureCollection", "features": [
            {"type": "Feature", "geometry": {"type": "Point", "coordinates": [39.5, -104.9]},
             "properties": {"id": "kerb", "name": "Kerb", "radius_m": 10}}]}))
    client = _StandInClient()
    builder = WorldBuilder(str(_REPO), netconvert_stand_in(tmp_path), str(tmp_path))
    assert builder.build_world(client, build_arguments(tmp_path, extract)) is False
    assert client.calls == []


def test_a_named_areas_file_that_does_not_exist_stops_the_build(tmp_path):
    client = _StandInClient()
    builder = WorldBuilder(str(_REPO), netconvert_stand_in(tmp_path), str(tmp_path))
    arguments = build_arguments(tmp_path, copy_extract(tmp_path), aoi=str(tmp_path / "none.json"))
    assert builder.build_world(client, arguments) is False and client.calls == []


def test_the_areas_beside_the_extract_are_published_with_the_world(tmp_path, installation):
    if not (STAGED_SUMO / "bin").exists():
        pytest.skip("the staged SUMO the world build resolves beside netconvert is absent")
    extract = copy_extract(tmp_path)
    raw = kerb_area()
    (tmp_path / "StreetLayout.aoi.geojson").write_bytes(raw)
    client = _StandInClient(write_package(tmp_path / "template"))
    netconvert = STAGED_SUMO / "bin" / ("netconvert.exe" if sys.platform == "win32" else "netconvert")
    builder = WorldBuilder(str(_REPO), str(netconvert), str(tmp_path))
    out = tmp_path / "packages"
    assert builder.build_world(client, build_arguments(tmp_path, extract,
                                                       emit_world_package=str(out))) is True
    assert client.calls == ["generate", "write_world_package"]
    reader = WorldPackageReader(out / "StreetLayout.cwp")
    assert reader.areas_source() == raw
    assert [a["id"] for a in reader.areas_of_interest()["areas"]] == ["kerb"]
    assert reader.place_index() is not None and reader.solar_frame() is not None
