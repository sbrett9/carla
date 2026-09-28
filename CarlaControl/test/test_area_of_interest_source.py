"""Areas of interest are refused whole, with every broken rule named, before a world is built.

`AreaOfInterestSource` is the world build's gate on an author's GeoJSON (`04_Contracts.md` C5 §7.1,
§7.5). A gate that has never rejected anything is not a gate, so most of this file is refusals: each
test hands it a file that breaks one rule and asserts both that it is refused and that the refusal
names the rule, because a refusal that does not say what is wrong leaves the author no better off than
a crash. The transposition test also asserts the exact wording C5 V5.4 requires.

The extent used is the Arapahoe extract's own `<bounds>`, so "inside the world" means inside a real
world's extract, not a rectangle invented for the test.
"""
from __future__ import annotations

import copy
import hashlib
import json
import sys
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.AreaOfInterestSource import (  # noqa: E402
    AreaOfInterestError,
    AreaOfInterestSource,
)
from carlacontrol.OsmClipper import BoundingBox, OsmClipper  # noqa: E402

ARAPAHOE_OSM = _REPO / "Import" / "Arapahoe_I25.osm"
# Arapahoe_I25.osm's <bounds>, restated so the tests run where the extract is absent.
ARAPAHOE_BOUNDS = BoundingBox(min_lat=39.58558, min_lon=-104.89004, max_lat=39.60304,
                              max_lon=-104.87894)

# A block near the Arapahoe origin (39.59431, -104.88449), about 90 m by 110 m.
BLOCK = [[-104.8850, 39.5940], [-104.8840, 39.5940], [-104.8840, 39.5950], [-104.8850, 39.5950],
         [-104.8850, 39.5940]]


def feature(area_id="school_lot", geometry=None, **properties) -> dict:
    props = {"id": area_id, "name": "School car park", **properties}
    return {"type": "Feature",
            "geometry": geometry or {"type": "Polygon", "coordinates": [copy.deepcopy(BLOCK)]},
            "properties": props}


def document(*features: dict, **extra) -> bytes:
    return json.dumps({"type": "FeatureCollection", "features": list(features), **extra}).encode()


def load(raw: bytes, bounds: BoundingBox | None = ARAPAHOE_BOUNDS) -> AreaOfInterestSource:
    return AreaOfInterestSource.from_bytes(raw, "test.aoi.geojson", bounds)


def refusal(raw: bytes, bounds: BoundingBox | None = ARAPAHOE_BOUNDS) -> list[str]:
    with pytest.raises(AreaOfInterestError) as raised:
        load(raw, bounds)
    return raised.value.problems


def test_the_extract_bounds_restated_here_are_the_extracts():
    if not ARAPAHOE_OSM.exists():
        pytest.skip("Import/Arapahoe_I25.osm is not in this tree")
    assert OsmClipper.read_bounds(ARAPAHOE_OSM) == ARAPAHOE_BOUNDS


def test_every_accepted_shape_loads_in_file_order():
    hole = [[-104.8847, 39.5943], [-104.8843, 39.5943], [-104.8843, 39.5947], [-104.8847, 39.5947],
            [-104.8847, 39.5943]]
    second = [[[-104.8830, 39.5960], [-104.8825, 39.5960], [-104.8825, 39.5965],
               [-104.8830, 39.5960]]]
    source = load(document(
        feature("school_lot", kind="car_park"),
        feature("holed_block", {"type": "Polygon", "coordinates": [copy.deepcopy(BLOCK), hole]}),
        feature("two_sites", {"type": "MultiPolygon",
                              "coordinates": [[copy.deepcopy(BLOCK)], second]}),
        feature("standoff", {"type": "Point", "coordinates": [-104.88449, 39.59431]},
                radius_m=150),
    ))
    assert [a.area_id for a in source.areas] == ["school_lot", "holed_block", "two_sites",
                                                 "standoff"]
    assert source.areas[0].kind == "car_park"
    assert source.areas[3].is_circle and source.areas[3].radius_m == 150.0
    assert len(source.areas[1].polygons[0]) == 2, "the hole is kept"
    assert source.warnings == []


def test_the_digest_is_of_the_bytes_as_written():
    raw = document(feature())
    assert load(raw).sha256 == hashlib.sha256(raw).hexdigest()


def test_the_file_is_found_beside_its_extract(tmp_path):
    extract = tmp_path / "Arapahoe_I25.osm"
    extract.write_text("<osm/>")
    assert AreaOfInterestSource.discover(extract) is None
    beside = tmp_path / "Arapahoe_I25.aoi.geojson"
    beside.write_bytes(document(feature()))
    assert AreaOfInterestSource.discover(extract) == beside


# ---- refusals -------------------------------------------------------------------------------------

def test_a_duplicate_id_is_refused_naming_both_features():
    problems = refusal(document(feature("gate"), feature("gate")))
    assert any("V5.1" in p and "'gate'" in p and "feature 0" in p and "feature 1" in p
               for p in problems)


@pytest.mark.parametrize("bad_id", ["Gate", "gate 3", "3gate", "", "g" * 65])
def test_a_malformed_id_is_refused(bad_id):
    problems = refusal(document(feature(bad_id)))
    assert any("V5.1" in p and "[a-z][a-z0-9_]{0,63}" in p for p in problems)


def test_an_id_only_at_feature_level_is_refused_and_says_where_it_is_read():
    raw = document({"type": "Feature", "id": "gate",
                    "geometry": {"type": "Polygon", "coordinates": [BLOCK]},
                    "properties": {"name": "Gate"}})
    problems = refusal(raw)
    assert any("properties.id is required" in p and "Feature-level" in p for p in problems)


def test_a_missing_name_is_refused():
    raw = document({"type": "Feature", "geometry": {"type": "Polygon", "coordinates": [BLOCK]},
                    "properties": {"id": "gate"}})
    assert any("properties.name" in p for p in refusal(raw))


def test_an_open_ring_is_refused():
    open_ring = BLOCK[:-1] + [[-104.8849, 39.5941]]
    problems = refusal(document(feature(geometry={"type": "Polygon", "coordinates": [open_ring]})))
    assert any("V5.2" in p and "not closed" in p for p in problems)


def test_a_ring_of_three_positions_is_refused():
    ring = [BLOCK[0], BLOCK[1], BLOCK[0]]
    problems = refusal(document(feature(geometry={"type": "Polygon", "coordinates": [ring]})))
    assert any("V5.2" in p and "at least four positions" in p for p in problems)


def test_a_self_crossing_ring_is_refused():
    bow_tie = [[-104.8850, 39.5940], [-104.8840, 39.5950], [-104.8840, 39.5940],
               [-104.8850, 39.5950], [-104.8850, 39.5940]]
    problems = refusal(document(feature(geometry={"type": "Polygon", "coordinates": [bow_tie]})))
    assert any("V5.2" in p and "crosses or touches itself" in p for p in problems)


def test_a_ring_with_no_area_is_refused():
    collinear = [[-104.8850, 39.5940], [-104.8845, 39.5940], [-104.8840, 39.5940],
                 [-104.8850, 39.5940]]
    problems = refusal(document(feature(geometry={"type": "Polygon", "coordinates": [collinear]})))
    assert any("V5.2" in p for p in problems)


def test_a_hole_outside_its_exterior_is_refused():
    elsewhere = [[-104.8830, 39.5960], [-104.8825, 39.5960], [-104.8825, 39.5965],
                 [-104.8830, 39.5960]]
    problems = refusal(document(feature(
        geometry={"type": "Polygon", "coordinates": [copy.deepcopy(BLOCK), elsewhere]})))
    assert any("V5.2" in p and "outside the exterior" in p for p in problems)


def test_a_hole_crossing_its_exterior_is_refused():
    straddling = [[-104.8855, 39.5945], [-104.8845, 39.5945], [-104.8845, 39.5947],
                  [-104.8855, 39.5947], [-104.8855, 39.5945]]
    problems = refusal(document(feature(
        geometry={"type": "Polygon", "coordinates": [copy.deepcopy(BLOCK), straddling]})))
    assert any("V5.2" in p and "crosses or touches another ring" in p for p in problems)


@pytest.mark.parametrize("radius", [None, 0, -5, "30", True])
def test_a_circle_needs_a_positive_numeric_radius(radius):
    props = {} if radius is None else {"radius_m": radius}
    problems = refusal(document(feature(
        geometry={"type": "Point", "coordinates": [-104.88449, 39.59431]}, **props)))
    assert any("V5.2" in p and "radius_m" in p for p in problems)


def test_a_radius_on_a_polygon_is_refused_rather_than_ignored():
    problems = refusal(document(feature(radius_m=30)))
    assert any("V5.2" in p and "Point only" in p for p in problems)


@pytest.mark.parametrize("kind", ["LineString", "MultiPoint", "GeometryCollection"])
def test_an_unsupported_geometry_is_refused_by_name(kind):
    problems = refusal(document(feature(geometry={"type": kind, "coordinates": []})))
    assert any(kind in p for p in problems)


def test_something_that_is_not_a_feature_collection_is_refused():
    assert any("FeatureCollection" in p
               for p in refusal(json.dumps({"type": "Feature"}).encode()))
    assert any("not readable" in p for p in refusal(b"{not json"))


def test_an_area_outside_the_world_is_refused_with_both_extents():
    far = [[-104.80, 39.70], [-104.79, 39.70], [-104.79, 39.71], [-104.80, 39.71], [-104.80, 39.70]]
    problems = refusal(document(feature(geometry={"type": "Polygon", "coordinates": [far]})))
    assert any("V5.3" in p and "wholly outside" in p and "-104.890040" in p for p in problems)


def test_a_transposed_file_is_diagnosed_by_name():
    """C5 V5.4: the single most likely authoring mistake gets the exact message the contract sets."""
    transposed = [[lat, lon] for lon, lat in BLOCK]
    problems = refusal(document(feature(geometry={"type": "Polygon", "coordinates": [transposed]})))
    assert any(p.startswith("V5.4") and "positions appear to be [latitude, longitude]; GeoJSON "
               "requires [longitude, latitude]" in p for p in problems)


def test_a_transposed_circle_is_diagnosed_too():
    problems = refusal(document(feature(
        geometry={"type": "Point", "coordinates": [39.59431, -104.88449]}, radius_m=50)))
    assert any(p.startswith("V5.4") for p in problems)


def test_projected_metres_are_refused_as_not_degrees():
    metres = [[500.0, 500.0], [600.0, 500.0], [600.0, 600.0], [500.0, 600.0], [500.0, 500.0]]
    problems = refusal(document(feature(geometry={"type": "Polygon", "coordinates": [metres]})))
    assert any("V5.3" in p and "not projected metres" in p for p in problems)


def test_every_problem_is_named_at_once():
    """Refused whole: an author fixes everything in one pass rather than one error per build."""
    problems = refusal(document(
        feature("Bad"),
        feature("gate", radius_m=5),
        feature("gate"),
        feature("gate2", geometry={"type": "LineString", "coordinates": []})))
    assert len(problems) >= 3
    assert {p.split()[0] for p in problems} >= {"V5.1", "V5.2"}


def test_the_envelope_rules_wait_when_the_extract_has_no_bounds():
    far = [[-104.80, 39.70], [-104.79, 39.70], [-104.79, 39.71], [-104.80, 39.71], [-104.80, 39.70]]
    source = load(document(feature(geometry={"type": "Polygon", "coordinates": [far]})),
                  bounds=None)
    assert not source.bounds_checked
    assert any("V5.3" in p for p in source.envelope_problems(ARAPAHOE_BOUNDS))


# ---- warnings -------------------------------------------------------------------------------------

def test_an_altitude_an_unread_property_and_a_crs_are_warned_about_and_carried_past():
    with_altitude = [p + [1650.0] for p in BLOCK]
    source = load(document(
        feature(geometry={"type": "Polygon", "coordinates": [with_altitude]}, colour="red"),
        crs={"type": "name", "properties": {"name": "EPSG:4326"}}))
    text = " ".join(source.warnings)
    assert "altitude" in text and "'colour'" in text and "crs" in text
    assert len(source.areas) == 1
