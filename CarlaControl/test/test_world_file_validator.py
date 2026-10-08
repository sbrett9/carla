"""`WorldFileValidator` finds a world package or a catalogue sound when it is, and names what is wrong
when it is not.

A complete package is built here as the world build writes one -- a full manifest, the OpenDRIVE, a
network from netconvert, a small draped grid with its digests -- and the reference set is published
into it by `AuthoringReferenceSet` with no areas declared, which needs no SUMO. It passes, and so does
each copy of it damaged in one way fails, naming the entry. The world packages a world build wrote
(the main checkout's `Build/world-packages`) and the shipped catalogue folder pass as they are.
"""
from __future__ import annotations

import hashlib
import json
import shutil
import struct
import sys
import zipfile
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))
sys.path.insert(0, str(Path(__file__).resolve().parent))

from test_world_package_schemas import BUILT_PACKAGES, built_package_folders  # noqa: E402

from carlacontrol.AuthoringReferenceSet import AuthoringReferenceSet  # noqa: E402
from carlacontrol.NetworkFingerprint import NetworkFingerprint  # noqa: E402
from carlacontrol.VehicleCatalogue import VehicleCatalogue  # noqa: E402
from carlacontrol.WorldFileValidator import (  # noqa: E402
    CATALOGUES,
    WORLD_PACKAGES,
    WorldFileValidator,
)
from carlacontrol.WorldPackageSchemas import BARE_EARTH_HEADER, BARE_EARTH_MAGIC  # noqa: E402

SCHEMAS = _REPO / "CarlaControl" / "schemas"
CATALOGUE_FOLDER = _REPO / "CarlaControl" / "catalogue"
NETWORK = (_REPO / "CarlaNet" / "test" / "CarlaNet.Tests" / "Map" / "Fixtures" / "Network" /
           "street_names_true.net.xml").read_text(encoding="utf-8")
GEOREFERENCE = "+proj=tmerc +lat_0=39.5 +lon_0=-104.9 +k=1 +x_0=0 +y_0=0 +ellps=WGS84 +units=m +no_defs"
COLUMNS, ROWS = 3, 2


def grid_bytes(manifest: dict) -> tuple[bytes, bytes, bytes]:
    header = struct.pack(BARE_EARTH_HEADER, BARE_EARTH_MAGIC, manifest["OriginLatitude"],
                         manifest["OriginLongitude"], manifest["OriginHeightMeters"],
                         manifest["GridMinXMeters"], manifest["GridMinYMeters"],
                         manifest["GridCellSizeMeters"], COLUMNS, ROWS)
    offsets = struct.pack(f"<{COLUMNS * ROWS}f", *[0.25 * i for i in range(COLUMNS * ROWS)])
    ground = struct.pack(f"<{COLUMNS * ROWS}f", *[1600.0 + i for i in range(COLUMNS * ROWS)])
    return header, offsets, ground


def manifest() -> dict:
    document = {
        "FormatVersion": 1,
        "Producer": {"tool": "carla-build-world", "tool_version": "0.10.0", "carlanet": "0.10.0",
                     "server": None, "sumo": "1.27.0", "written_utc": "2026-10-07T12:00:00.000Z"},
        "MapName": "StreetLayout", "OriginLatitude": 39.5, "OriginLongitude": -104.9,
        "OriginHeightMeters": 1600.0, "GeoReferenceString": GEOREFERENCE,
        "HeightAlignMode": "drape", "DrapeActive": True, "HeightAlignOffsetMeters": 0,
        "GridMinXMeters": -2.0, "GridMinYMeters": -1.0, "GridCellSizeMeters": 2.0,
        "GridNumCols": COLUMNS, "GridNumRows": ROWS,
        "BareEarthOffsetSha1": "", "BareEarthDtmSha1": "",
        "PhotorealIonAssetId": 2275207, "GroundIonAssetId": 1,
        "StagingMinXMeters": -2.0, "StagingMinYMeters": -1.0, "StagingMaxXMeters": 2.0,
        "StagingMaxYMeters": 1.0, "StagingMarginMeters": 30.48,
        "SourceOsmFileName": "street_layout.osm", "SourceOsmSha256": "1" * 64,
        "OpenDriveSha256": "0" * 64, "NetworkFingerprint": NetworkFingerprint.of_text(NETWORK),
        "NetconvertArgv": ["--osm-files", "street_layout.osm", "--output-file", "<output-file>"],
        "NetconvertPath": "netconvert", "NetconvertVersion": "Eclipse SUMO netconvert 1.27.0",
        "SampleStepMeters": 10, "TerrainResolutionMeters": 2, "TerrainMarginMeters": 30.48,
        "GeneratedAtUtc": "2026-10-07T12:00:00.0000000Z", "GeneratorVersion": "1.0.0.0",
        "NetconvertExtraArgs": [],
    }
    header, offsets, ground = grid_bytes(document)
    document["BareEarthOffsetSha1"] = hashlib.sha1(offsets).hexdigest()
    document["BareEarthDtmSha1"] = hashlib.sha1(ground).hexdigest()
    return document


def write_package(path: Path, document: dict, *, grid: bytes | None = None,
                  extra: dict[str, bytes] | None = None, deflate: str = "") -> Path:
    header, offsets, ground = grid_bytes(document)
    entries = {"world.json": json.dumps(document, indent=2).encode(),
               "map.xodr": f"<OpenDRIVE><header><geoReference><![CDATA[{GEOREFERENCE}]]></geoReference>"
                           "</header></OpenDRIVE>".encode(),
               "map.net.xml": NETWORK.encode(),
               "bareearth.bin": grid if grid is not None else header + offsets + ground,
               **(extra or {})}
    with zipfile.ZipFile(path, "w", zipfile.ZIP_STORED) as archive:
        for name, data in entries.items():
            archive.writestr(name, data, zipfile.ZIP_DEFLATED if name == deflate else zipfile.ZIP_STORED)
    return path


@pytest.fixture
def validator() -> WorldFileValidator:
    return WorldFileValidator(SCHEMAS)


@pytest.fixture
def package(tmp_path) -> Path:
    path = write_package(tmp_path / "StreetLayout.cwp", manifest())
    AuthoringReferenceSet(path, None, None).publish()
    return path


def failures(validator: WorldFileValidator, path: Path) -> list[str]:
    result = validator.validate(path)
    return [failure.describe(path.parent) for failure in result.failures]


def damaged(package: Path, tmp_path: Path, **changes) -> Path:
    """The package with its reference set, rewritten with one change."""
    with zipfile.ZipFile(package) as archive:
        entries = {name: archive.read(name) for name in archive.namelist()}
    for name, data in changes.pop("replace", {}).items():
        if data is None:
            entries.pop(name, None)
        else:
            entries[name] = data
    path = tmp_path / "Damaged.cwp"
    deflate = changes.pop("deflate", "")
    with zipfile.ZipFile(path, "w", zipfile.ZIP_STORED) as archive:
        for name, data in entries.items():
            archive.writestr(name, data, zipfile.ZIP_DEFLATED if name == deflate else zipfile.ZIP_STORED)
    return path


def test_a_complete_package_is_sound(validator, package):
    result = validator.validate(package)
    assert result.checked[WORLD_PACKAGES] == 1
    assert result.ok, [failure.describe(package.parent) for failure in result.failures]


@pytest.mark.parametrize(("change", "said"), [
    ({"deflate": "map.net.xml"}, "map.net.xml: is compressed"),
    ({"replace": {"notes.txt": b"x"}}, "notes.txt: is not an entry a world package holds"),
    ({"replace": {"map.xodr": None}}, "map.xodr: is missing"),
    ({"replace": {"map.tll.xml": b"<tlLogics/>"}}, "map.tll.xml: is present without netconvert"),
])
def test_a_package_of_the_wrong_entries_is_named(validator, package, tmp_path, change, said):
    assert any(said in line for line in failures(validator, damaged(package, tmp_path, **change)))


def test_a_grid_cut_short_or_of_other_values_is_named(validator, package, tmp_path):
    with zipfile.ZipFile(package) as archive:
        grid = archive.read("bareearth.bin")
    short = failures(validator, damaged(package, tmp_path, replace={"bareearth.bin": grid[:-4]}))
    assert any("bareearth.bin: is 104 bytes; a 3 x 2 grid is 108" in line for line in short)
    changed = grid[:-4] + struct.pack("<f", 9.0)
    other = failures(validator, damaged(package, tmp_path, replace={"bareearth.bin": changed}))
    assert any("does not hash to BareEarthDtmSha1" in line for line in other)


def test_a_manifest_the_grid_disagrees_with_is_named(validator, package, tmp_path):
    document = manifest()
    document["GridNumCols"] = 4
    found = failures(validator, damaged(package, tmp_path, replace={
        "world.json": json.dumps(document).encode()}))
    assert any("says GridNumCols 3 where world.json says 4" in line for line in found)


def test_a_reference_set_of_another_network_is_named(validator, package, tmp_path):
    with zipfile.ZipFile(package) as archive:
        places = json.loads(archive.read("places.json"))
    places["network_fingerprint"] = "f" * 64
    found = failures(validator, damaged(package, tmp_path, replace={
        "places.json": json.dumps(places).encode()}))
    assert any("places.json: names a network other than" in line for line in found)


def test_an_area_table_beside_another_areas_file_is_named(validator, package, tmp_path):
    found = failures(validator, damaged(package, tmp_path, replace={
        "areas.aoi.geojson": b'{"type": "FeatureCollection", "features": []}'}))
    assert any("areas.resolved.json: was resolved from an areas file other than" in line
               for line in found)


def test_a_manifest_missing_a_required_field_or_of_a_newer_format_is_named(validator, package,
                                                                          tmp_path):
    document = manifest()
    del document["HeightAlignMode"]
    found = failures(validator, damaged(package, tmp_path, replace={
        "world.json": json.dumps(document).encode()}))
    assert any("'HeightAlignMode' is required" in line for line in found)
    document = {**manifest(), "FormatVersion": 2}
    newer = failures(validator, damaged(package, tmp_path, replace={
        "world.json": json.dumps(document).encode()}))
    assert any("declares FormatVersion 2" in line for line in newer)


@pytest.mark.skipif(not BUILT_PACKAGES, reason="no world package a world build wrote")
def test_the_packages_a_world_build_wrote_are_sound(validator):
    for folder in built_package_folders():
        result = validator.validate(folder)
        assert result.ok, [failure.describe(folder) for failure in result.failures]


# -- the catalogue folder ---------------------------------------------------------------------------

def test_the_shipped_catalogue_folder_is_sound(validator):
    result = validator.validate(CATALOGUE_FOLDER)
    assert result.checked[CATALOGUES] == 1
    assert result.ok, [failure.describe(CATALOGUE_FOLDER) for failure in result.failures]


def copied_catalogue(tmp_path: Path) -> Path:
    folder = tmp_path / "catalogue"
    shutil.copytree(CATALOGUE_FOLDER, folder)
    return folder


def test_a_catalogue_whose_digest_is_not_its_content_s_is_named(validator, tmp_path):
    folder = copied_catalogue(tmp_path)
    path = folder / "vehicles.catalogue.json"
    document = json.loads(path.read_text(encoding="utf-8"))
    document["classes"][0]["max_speed_mps"] = 36.0
    path.write_text(VehicleCatalogue.canonical_json(document), encoding="utf-8")
    found = failures(validator, folder)
    assert any("catalogue_digest: is not the digest of the catalogue's content" in line for line in found)
    assert any("does not declare the types vehicles.catalogue.json gives" in line for line in found)


def test_vehicle_types_that_break_their_schema_are_named(validator, tmp_path):
    folder = copied_catalogue(tmp_path)
    path = folder / "vehicles.vtypes.rou.xml"
    path.write_text(path.read_text(encoding="utf-8").replace('key="carla:class_id"', 'key="class"', 1),
                    encoding="utf-8")
    found = failures(validator, folder)
    assert any("vehicles.vtypes.rou.xml: line" in line and "class" in line for line in found)
