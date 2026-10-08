"""The files a user writes are checked against their published schemas before a tool uses them.

Each reader keeps its own checks, which say more than a type error would -- an unknown field with the
nearest names, an empty path refused by run check 37, a ring that crosses itself -- and then checks
the file against its schema, so a value of the wrong type is refused in the schema's words, naming
the schema, instead of failing later or being read in part:

* carla-capture: the site profile (`SiteProfile`); a run file is refused field by field against the
  same schema it is published as (`RunConfiguration`), so it already was;
* carla-compile-scenario: the specification and the sweep (check 53), which already were;
* the areas of interest (`AreaOfInterestSource`), read by the world build and the reference-set tool,
  and a display convention (`CotDisplayConvention`), read by carla-cot-telemetry; neither is read by
  the compiler;
* carla-drive: the epoch file (`--epoch`), before the session reads it.

The areas file's schema states only the structural half of the reader's rules, so it is held to agree
with the reader: what the reader accepts, the schema accepts, and every structural refusal of the
reader is one the schema makes too.
"""
from __future__ import annotations

import argparse
import copy
import importlib
import json
import sys
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))
sys.path.insert(0, str(_REPO / "CarlaControl" / "test"))

from RunCaptureFixture import Layout  # noqa: E402

from carlacontrol.AreaOfInterestSource import (  # noqa: E402
    AreaOfInterestError,
    AreaOfInterestSource,
)
from carlacontrol.CotDisplayConvention import CotDisplayConvention  # noqa: E402
from carlacontrol.OsmClipper import BoundingBox  # noqa: E402
from carlacontrol.ScenarioEpoch import ScenarioEpoch  # noqa: E402
from carlacontrol.SchemaPublication import SchemaPublication  # noqa: E402
from carlacontrol.SiteProfile import SiteProfile  # noqa: E402

IMPORT = _REPO / "Import"
EPOCH = {"epoch_version": 1, "civil_datetime": "2026-03-21T06:00:00-06:00",
         "utc_offset_hours": -6.0, "utc_datetime": "2026-03-21T12:00:00Z",
         "calendar_advances": True, "dst_in_effect": True, "time_zone_id": "America/Denver"}


# -- the site profile ---------------------------------------------------------------------------------

@pytest.fixture
def layout(tmp_path: Path) -> Layout:
    return Layout(tmp_path)


def profile_refusal(layout: Layout, **changes) -> str:
    path = layout.write_profile(**changes)
    with pytest.raises(ValueError) as raised:
        SiteProfile.discover(layout.root, path, environ={})
    return str(raised.value)


def test_a_site_profile_value_of_the_wrong_type_is_refused_in_the_schema_s_words(layout):
    message = profile_refusal(layout, server={"host": "127.0.0.1", "port": "2000"})
    assert SiteProfile.schema()["$id"] in message
    assert "$.server.port: must be integer, is string" in message


def test_a_site_profile_s_environment_list_is_held_to_its_schema(layout):
    message = profile_refusal(layout, environment="CARLANET_SUMO_HOME")
    assert "$.environment: must be array" in message


def test_a_site_profile_path_may_be_empty_or_null_for_the_launch_to_refuse(layout):
    for blank in ("", None):
        document = layout.profile_document()
        document["paths"]["runs_root"] = blank
        path = layout.root / "blank.profile.json"
        path.write_text(json.dumps(document), encoding="utf-8")
        profile = SiteProfile.discover(layout.root, path, environ={})
        assert profile.value("paths.runs_root") == blank


def test_a_site_profile_s_own_refusals_keep_their_words(layout):
    message = profile_refusal(layout, paths={"capture_rot": "x"})
    assert "capture_rot is not a field of 'paths'" in message


def test_the_shipped_template_reads_back(layout, tmp_path):
    site = SiteProfile.discover(layout.root, layout.write_profile(), environ={})
    template = site.write_template(tmp_path / "template.profile.json")
    assert SiteProfile.discover(layout.root, template, environ={}).value("server.port") == 2000


# -- a display convention ------------------------------------------------------------------------------

def test_a_display_convention_of_the_wrong_type_is_refused_in_the_schema_s_words(tmp_path):
    path = tmp_path / "bad.display.json"
    path.write_text(json.dumps({"convention_version": 1, "description": ["two", "lines"],
                                "affiliation_by_type": {"car": "n"}}), encoding="utf-8")
    with pytest.raises(ValueError) as raised:
        CotDisplayConvention.from_file(path)
    assert CotDisplayConvention.schema()["$id"] in str(raised.value)
    assert "$.description: must be string" in str(raised.value)


def test_a_display_convention_s_own_refusals_keep_their_words(tmp_path):
    path = tmp_path / "planted.display.json"
    path.write_text(json.dumps({"convention_version": 1, "affiliation_by_type": {},
                                "marked_ids": ["x"]}), encoding="utf-8")
    with pytest.raises(ValueError, match="names planted vehicles"):
        CotDisplayConvention.from_file(path)


@pytest.mark.parametrize("path", sorted(IMPORT.glob("*.display.json")), ids=lambda p: p.name)
def test_a_shipped_display_convention_reads(path):
    assert len(CotDisplayConvention.from_file(path)) > 0


# -- the areas of interest -----------------------------------------------------------------------------

BOUNDS = BoundingBox(min_lat=39.58558, min_lon=-104.89004, max_lat=39.60304, max_lon=-104.87894)
BLOCK = [[-104.8850, 39.5940], [-104.8840, 39.5940], [-104.8840, 39.5950], [-104.8850, 39.5950],
         [-104.8850, 39.5940]]
POINT = {"type": "Point", "coordinates": [-104.88449, 39.59431]}


def area(area_id="school_lot", geometry=None, **properties) -> dict:
    return {"type": "Feature",
            "geometry": geometry or {"type": "Polygon", "coordinates": [copy.deepcopy(BLOCK)]},
            "properties": {"id": area_id, "name": "School car park", **properties}}


def collection(*features: dict, **extra) -> dict:
    return {"type": "FeatureCollection", "features": list(features), **extra}


ACCEPTED = {
    "polygon": collection(area(kind="fixture:car_park")),
    "multipolygon": collection(area(geometry={"type": "MultiPolygon",
                                              "coordinates": [[copy.deepcopy(BLOCK)]]})),
    "circle": collection(area(geometry=POINT, radius_m=150)),
    "altitude, an unread property and a crs": collection(
        area(geometry={"type": "Polygon", "coordinates": [[p + [1650.0] for p in BLOCK]]},
             color="red"), crs={"type": "name"}),
    "a null kind and a null radius on a polygon": collection(area(kind=None, radius_m=None)),
}
REFUSED = {
    "not a collection": {"type": "Feature"},
    "no features": {"type": "FeatureCollection"},
    "a feature that is not one": collection({"type": "Thing"}),
    "no id": collection({"type": "Feature", "geometry": POINT,
                         "properties": {"name": "x", "radius_m": 5}}),
    "an upper-case id": collection(area("School")),
    "no name": collection({"type": "Feature", "geometry": copy.deepcopy(POINT),
                           "properties": {"id": "x", "radius_m": 5}}),
    "a blank name": collection(area(name="  ")),
    "a blank kind": collection(area(kind="")),
    "a point with no radius": collection(area(geometry=POINT)),
    "a zero radius": collection(area(geometry=POINT, radius_m=0)),
    "a radius on a polygon": collection(area(radius_m=20)),
    "a line": collection(area(geometry={"type": "LineString", "coordinates": BLOCK})),
    "a ring of three positions": collection(area(geometry={
        "type": "Polygon", "coordinates": [[BLOCK[0], BLOCK[1], BLOCK[0]]]})),
    "a position of four numbers": collection(area(geometry={
        "type": "Point", "coordinates": [-104.88449, 39.59431, 1.0, 2.0]}, radius_m=5)),
    "an empty multipolygon": collection(area(geometry={"type": "MultiPolygon",
                                                       "coordinates": []})),
}


@pytest.mark.parametrize("name", sorted(ACCEPTED))
def test_what_the_areas_reader_accepts_the_schema_accepts(name):
    raw = json.dumps(ACCEPTED[name]).encode()
    AreaOfInterestSource.from_bytes(raw, "test.aoi.geojson", BOUNDS)
    assert SchemaPublication.problems(ACCEPTED[name], AreaOfInterestSource.schema()) == []


@pytest.mark.parametrize("name", sorted(REFUSED))
def test_what_the_areas_reader_refuses_by_shape_the_schema_refuses(name):
    raw = json.dumps(REFUSED[name]).encode()
    with pytest.raises(AreaOfInterestError):
        AreaOfInterestSource.from_bytes(raw, "test.aoi.geojson", BOUNDS)
    assert SchemaPublication.problems(REFUSED[name], AreaOfInterestSource.schema())


@pytest.mark.parametrize("path", sorted(IMPORT.glob("*.aoi.geojson")), ids=lambda p: p.name)
def test_a_shipped_areas_file_reads(path):
    assert AreaOfInterestSource.load(path, None).areas


# -- the epoch carla-drive reads -----------------------------------------------------------------------

@pytest.fixture(scope="module")
def drive():
    """The command's module. It imports the co-simulation assemblies, so it needs carlanet."""
    pytest.importorskip("carlanet", reason="the drive needs carlanet and its assemblies")
    try:
        return importlib.import_module("carlacontrol.commands.drive")
    except ImportError as missing:
        pytest.skip(f"the co-simulation assemblies are not loaded here: {missing}")


def declaration(drive, path: Path):
    arguments = argparse.Namespace(epoch=str(path), illumination=None, solar_rate=None,
                                   freeze_at=None, freeze_date_advances=False,
                                   no_sun_required=False)
    return drive.SunDeclaration(arguments)


def test_an_epoch_file_that_conforms_is_handed_on(drive, tmp_path):
    path = tmp_path / "site.epoch.json"
    path.write_text(json.dumps(EPOCH), encoding="utf-8")
    assert declaration(drive, path).epoch == EPOCH
    ScenarioEpoch.read(EPOCH)


def test_a_scenario_s_epoch_is_checked_where_the_file_is_a_scenario(drive, tmp_path):
    path = tmp_path / "site.scenario.json"
    path.write_text(json.dumps({"epoch": EPOCH, "illumination": {"illumination_version": 1,
                                                                 "policy": "ignore"}}),
                    encoding="utf-8")
    assert declaration(drive, path).illumination["policy"] == "ignore"
    path.write_text(json.dumps({"epoch": {**EPOCH, "utc_offset_hours": "-6"}}), encoding="utf-8")
    with pytest.raises(SystemExit) as raised:
        declaration(drive, path)
    assert f"the epoch object of {path}" in str(raised.value)


@pytest.mark.parametrize(("change", "said"), [
    ({"utc_offset_hours": "-06:00"}, "$.utc_offset_hours: must be number"),
    ({"utc_offset_hours": -6.1}, "$.utc_offset_hours: -6.1 is not a multiple of 0.25"),
    ({"civil_datetime": "2026-03-21T06:00:00"}, "$.civil_datetime: '2026-03-21T06:00:00' does "
                                                "not match"),
    ({"utc_datetime": "2026-03-21T06:00:00-06:00"}, "$.utc_datetime:"),
    ({"zone": "America/Denver"}, "$: 'zone' is not a field here"),
    ({"epoch_version": 2}, "$.epoch_version: must be 1"),
])
def test_an_epoch_file_that_does_not_conform_is_refused_in_the_schema_s_words(drive, tmp_path,
                                                                              change, said):
    path = tmp_path / "site.epoch.json"
    path.write_text(json.dumps({**EPOCH, **change}), encoding="utf-8")
    with pytest.raises(SystemExit) as raised:
        declaration(drive, path)
    assert said in str(raised.value)
    assert ScenarioEpoch.schema()["$id"] in str(raised.value)


def test_a_missing_epoch_field_is_refused_by_the_schema_and_by_solar_epoch_alike(drive, tmp_path):
    document = {key: value for key, value in EPOCH.items() if key != "dst_in_effect"}
    path = tmp_path / "site.epoch.json"
    path.write_text(json.dumps(document), encoding="utf-8")
    with pytest.raises(SystemExit, match="'dst_in_effect' is required"):
        declaration(drive, path)
    with pytest.raises(ValueError, match="dst_in_effect"):
        ScenarioEpoch.read(document)
