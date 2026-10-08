"""A world package's entries are what their schemas and format pages say, and its reader holds them to it.

Checked here:

  * every published schema of a world package's JSON entries is the one `WorldPackageSchemas` generates,
    and each uses only what the reader's validator enforces;
  * a package the authoring reference set was published into by the real writers -- the place index,
    the solar frame and a resolved area -- matches the schemas;
  * the world packages a world build wrote, under the main checkout's `Build/world-packages`, match
    the schemas, hold the entries `ENTRIES` says they hold and no other, all stored, and their
    `bareearth.bin` is laid out as `BareEarthGrid.md` says: the header, its size, and the digests the
    manifest records;
  * `WorldPackageReader` refuses an entry that departs from its schema, naming the departure, and still
    reads a manifest that carries only some fields; the SUMO bridge's grid reader refuses a grid that
    is not its header and two whole planes.
"""
from __future__ import annotations

import hashlib
import json
import os
import struct
import sys
import zipfile
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))
sys.path.insert(0, str(Path(__file__).resolve().parent))

from ScenarioWorldFixture import ScenarioWorldFixture  # noqa: E402

from carlacontrol.JsonSchemaFile import JsonSchemaFile  # noqa: E402
from carlacontrol.SumoCotBridge import BareEarthGrid  # noqa: E402
from carlacontrol.WorldPackageReader import WorldPackageReader  # noqa: E402
from carlacontrol.WorldPackageSchemas import (  # noqa: E402
    BARE_EARTH_HEADER,
    BARE_EARTH_HEADER_BYTES,
    BARE_EARTH_MAGIC,
    ENTRIES,
    WorldPackageSchemas,
)

SCHEMAS = _REPO / "CarlaControl" / "schemas"
REGENERATE = ("regenerate with: python -c \"from carlacontrol.WorldPackageSchemas import "
              "WorldPackageSchemas; WorldPackageSchemas.write('CarlaControl/schemas')\"")



def built_package_folders() -> list[Path]:
    """Where world builds wrote packages: `CARLA_WORLD_PACKAGES` when set, this checkout's
    `Build/world-packages`, and, from a git worktree under `.claude/worktrees/`, which has no `Build/`,
    the main checkout's. They are only read."""
    folders = [_REPO / "Build" / "world-packages"]
    if _REPO.parent.name == "worktrees" and _REPO.parent.parent.name == ".claude":
        folders.append(_REPO.parents[2] / "Build" / "world-packages")
    if os.environ.get("CARLA_WORLD_PACKAGES"):
        folders.insert(0, Path(os.environ["CARLA_WORLD_PACKAGES"]))
    return [folder for folder in folders if folder.is_dir()]


BUILT_PACKAGES = sorted({path.resolve() for folder in built_package_folders()
                         for path in folder.glob("*.cwp")})
JSON_ENTRIES = [entry.name for entry in ENTRIES if entry.schema]


@pytest.mark.parametrize("name", sorted(WorldPackageSchemas.schemas()))
def test_the_published_schema_is_the_generated_one(name):
    published = SCHEMAS / name
    assert published.is_file(), REGENERATE
    assert published.read_text(encoding="utf-8") == JsonSchemaFile.text(
        WorldPackageSchemas.schemas()[name]), REGENERATE


@pytest.mark.parametrize("name", sorted(WorldPackageSchemas.schemas()))
def test_every_schema_says_only_what_the_reader_enforces(name):
    schema = WorldPackageSchemas.schemas()[name]
    assert schema["$id"].startswith("urn:carla-sumo-capture:schema:")
    # The validator refuses to run on a keyword it does not enforce.
    JsonSchemaFile.problems({}, schema)


def test_every_entry_the_reader_names_is_a_listed_entry():
    listed = {entry.name for entry in ENTRIES}
    named = {value for key, value in vars(WorldPackageReader).items()
             if key.endswith("_ENTRY") and isinstance(value, str)}
    assert named <= listed
    assert {entry.name for entry in ENTRIES if entry.required} == {"world.json", "map.xodr"}


# -- a package the real writers published into ---------------------------------------------------------

@pytest.fixture(scope="module")
def published(tmp_path_factory) -> Path:
    try:
        installation = ScenarioWorldFixture.locate_sumo()
    except FileNotFoundError as missing:
        pytest.skip(f"no SUMO to resolve the fixture's area with: {missing}")
    return ScenarioWorldFixture(tmp_path_factory.mktemp("world"), installation).package


def test_a_published_reference_set_matches_its_schemas(published):
    with zipfile.ZipFile(published) as package:
        names = set(package.namelist())
        assert {"places.json", "solar.json", "areas.resolved.json", "areas.aoi.geojson"} <= names
        for name in ("places.json", "solar.json", "areas.resolved.json"):
            document = json.loads(package.read(name))
            assert JsonSchemaFile.problems(document, WorldPackageSchemas.entry_schema(name)) == [], name
        areas = json.loads(package.read("areas.resolved.json"))
    (kerb,) = areas["areas"]
    assert kerb["carla_local"]["type"] == "Circle"
    assert kerb["sumo"]["lanes"], "the fixture area reaches no lane, so the lane shapes go unchecked"


def test_the_reader_reads_what_the_writers_published(published):
    reader = WorldPackageReader(published)
    assert reader.place_index()["place_index_version"] == 1
    assert reader.solar_frame()["solar_frame_version"] == 1
    assert reader.areas_of_interest()["areas"][0]["id"] == "kerb"


# -- what a world build wrote ---------------------------------------------------------------------------

needs_built = pytest.mark.skipif(not BUILT_PACKAGES, reason="no world package a world build wrote")


@needs_built
@pytest.mark.parametrize("package", BUILT_PACKAGES, ids=lambda path: path.name)
def test_a_built_package_s_json_entries_match_their_schemas(package):
    with zipfile.ZipFile(package) as archive:
        for name in JSON_ENTRIES:
            if name in archive.namelist():
                document = json.loads(archive.read(name))
                assert JsonSchemaFile.problems(document, WorldPackageSchemas.entry_schema(name)) == [], \
                    name


@needs_built
@pytest.mark.parametrize("package", BUILT_PACKAGES, ids=lambda path: path.name)
def test_a_built_package_holds_the_listed_entries_all_stored(package):
    listed = {entry.name: entry for entry in ENTRIES}
    with zipfile.ZipFile(package) as archive:
        infos = archive.infolist()
        manifest = json.loads(archive.read("world.json"))
    names = {info.filename for info in infos}
    assert names <= set(listed), names - set(listed)
    assert {name for name, entry in listed.items() if entry.required} <= names
    assert all(info.compress_type == zipfile.ZIP_STORED for info in infos)
    assert ("bareearth.bin" in names) == manifest["DrapeActive"]
    assert ("map.tll.xml" in names) == ("<tllogic-files>" in manifest.get("NetconvertArgv", []))
    if "areas.aoi.geojson" in names:
        with zipfile.ZipFile(package) as archive:
            source = archive.read("areas.aoi.geojson")
            table = json.loads(archive.read("areas.resolved.json"))
        assert table["source_sha256"] == hashlib.sha256(source).hexdigest()


@needs_built
@pytest.mark.parametrize("package", BUILT_PACKAGES, ids=lambda path: path.name)
def test_a_built_package_s_bare_earth_grid_is_laid_out_as_its_format_page_says(package):
    with zipfile.ZipFile(package) as archive:
        manifest = json.loads(archive.read("world.json"))
        if "bareearth.bin" not in archive.namelist():
            pytest.skip("this world was not draped, so it has no grid")
        raw = archive.read("bareearth.bin")
    assert BARE_EARTH_HEADER_BYTES == 60
    # The magic is a little-endian int32, so the file opens with the ASCII "1PWC".
    assert raw[:4] == b"1PWC"
    magic, latitude, longitude, height, min_x, min_y, cell, columns, rows = struct.unpack_from(
        BARE_EARTH_HEADER, raw, 0)
    assert magic == BARE_EARTH_MAGIC
    assert (latitude, longitude, height) == (manifest["OriginLatitude"], manifest["OriginLongitude"],
                                             manifest["OriginHeightMeters"])
    assert (min_x, min_y, cell, columns, rows) == (
        manifest["GridMinXMeters"], manifest["GridMinYMeters"], manifest["GridCellSizeMeters"],
        manifest["GridNumCols"], manifest["GridNumRows"])
    plane = 4 * columns * rows
    assert len(raw) == 60 + 2 * plane
    offsets, ground = raw[60:60 + plane], raw[60 + plane:]
    if manifest.get("BareEarthOffsetSha1"):
        assert hashlib.sha1(offsets).hexdigest() == manifest["BareEarthOffsetSha1"]
        assert hashlib.sha1(ground).hexdigest() == manifest["BareEarthDtmSha1"]
    # The grid's points span the staging rectangle, the first point at its minimum corner.
    assert min_x == manifest["StagingMinXMeters"] and min_y == manifest["StagingMinYMeters"]
    assert min_x + (columns - 1) * cell == pytest.approx(manifest["StagingMaxXMeters"], abs=1e-3)
    assert min_y + (rows - 1) * cell == pytest.approx(manifest["StagingMaxYMeters"], abs=1e-3)
    # The ground plane holds ellipsoidal heights near the origin's, not heights relative to it.
    first_ground = struct.unpack_from("<f", ground, 0)[0]
    assert abs(first_ground - height) < 500.0


# -- the reader holds an entry to its schema -----------------------------------------------------------

def package_with(path: Path, manifest: dict, **entries: object) -> Path:
    with zipfile.ZipFile(path, "w", zipfile.ZIP_STORED) as archive:
        archive.writestr("world.json", json.dumps(manifest))
        for name, document in entries.items():
            archive.writestr(name.replace("_", ".", 1), json.dumps(document))
    return path


def test_a_manifest_with_only_some_fields_is_read(tmp_path):
    reader = WorldPackageReader(package_with(tmp_path / "Some.cwp", {"MapName": "Some"}))
    assert reader.map_name == "Some"


def test_a_manifest_field_of_the_wrong_shape_is_refused_by_name(tmp_path):
    path = package_with(tmp_path / "Bad.cwp", {"MapName": "Bad", "DrapeActive": "yes",
                                               "HeightAlignMode": "sideways"})
    with pytest.raises(ValueError) as refused:
        WorldPackageReader(path)
    message = str(refused.value)
    assert "world.json does not match its schema" in message
    assert "urn:carla-sumo-capture:schema:world-package-manifest:1" in message
    assert "$.DrapeActive: must be boolean" in message
    assert "$.HeightAlignMode" in message


def test_a_manifest_field_the_schema_does_not_name_is_refused(tmp_path):
    path = package_with(tmp_path / "Extra.cwp", {"MapName": "Extra", "Heigth": 3})
    with pytest.raises(ValueError, match="'Heigth' is not a field here"):
        WorldPackageReader(path)


def test_a_reference_entry_that_departs_from_its_schema_is_refused(tmp_path):
    solar = {"solar_frame_version": 1, "origin_latitude": 39.5}
    path = package_with(tmp_path / "Solar.cwp", {"MapName": "Solar"}, solar_json=solar)
    with pytest.raises(ValueError) as refused:
        WorldPackageReader(path).solar_frame()
    assert "solar.json does not match its schema" in str(refused.value)
    assert "'engine_time_zone' is required" in str(refused.value)


def test_the_bridge_refuses_a_grid_that_is_not_whole(tmp_path):
    columns, rows = 3, 2
    header = struct.pack(BARE_EARTH_HEADER, BARE_EARTH_MAGIC, 39.5, -104.9, 1700.0, -2.0, -1.0, 2.0,
                         columns, rows)
    whole = header + struct.pack(f"<{2 * columns * rows}f", *range(2 * columns * rows))
    path = tmp_path / "bareearth.bin"
    path.write_bytes(whole)
    # The first point of the ground plane, which follows the six values of the offset plane.
    assert BareEarthGrid.from_file(path).height_at(-2.0, -1.0) == 6.0
    path.write_bytes(whole[:-4])
    with pytest.raises(ValueError, match="is not a whole bare-earth grid: a 3 x 2 grid is 108 bytes"):
        BareEarthGrid.from_file(path)
