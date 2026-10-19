"""An area of interest is placed where it was drawn: in CARLA metres, and on the SUMO lanes it covers.

`AreaOfInterestResolver` turns validated GeoJSON into the table the world package carries
(`04_Contracts.md` C5 §7.2-§7.3). Nothing here is mocked: every area is placed by a real SUMO process
holding a real netconvert network, and cross-checked against CarlaNet's real `Geodesy`.

Most tests use the cross-shaped network the world-build tests already use, whose lanes run along
known lines (`street_names_true.net.xml`, origin 39.5 N 104.9 W). Areas are drawn in that network's
metres and turned into longitude and latitude by SUMO's own inverse, so a test can say exactly which
stretch of which lane an area must cover. Three tests use the shipped world packages, and skip when
they are absent:

  * a lane on Arapahoe whose SUMO length is 5.76 m shorter than its shape, to show positions are
    lane positions a `<stop>` can use, not arc lengths;
  * Bahonar's guard tower 3, placed by the WGS84 transform the telemetry uses rather than by SUMO,
    which must resolve to the lane position its own scenario parks the guard at;
  * a two-lane Arapahoe edge, for the rule that makes an edge inside, crossing or near.

The frame check (C5 V5.12) is shown refusing a manifest whose origin is a tenth of a metre from the
network's, and a network carrying a road offset.
"""
from __future__ import annotations

import json
import math
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.AreaOfInterestResolver import AreaOfInterestResolver  # noqa: E402
from carlacontrol.AreaOfInterestSource import (  # noqa: E402
    AreaOfInterestError,
    AreaOfInterestSource,
)
from carlacontrol.GeodeticFrame import GeodeticFrame  # noqa: E402
from carlacontrol.SumoInstallation import SumoInstallation  # noqa: E402
from carlacontrol.SumoNetworkQuery import SumoNetworkQuery  # noqa: E402
from carlacontrol.WorldPackageReader import WorldPackageReader  # noqa: E402

FIXTURES = _REPO / "CarlaNet" / "test" / "CarlaNet.Tests" / "Map" / "Fixtures" / "Network"
NETWORK = (FIXTURES / "street_names_true.net.xml").read_text(encoding="utf-8")
PACKAGES = _REPO / "Build" / "world-packages"

MANIFEST = {
    "MapName": "StreetLayout",
    "OriginLatitude": 39.5,
    "OriginLongitude": -104.9,
    "GeoReferenceString": "+proj=tmerc +lat_0=39.5 +lon_0=-104.9 +k=1 +x_0=0 +y_0=0 +ellps=WGS84 "
                          "+units=m +no_defs",
    "StagingMinXMeters": -201.27, "StagingMinYMeters": -99.92,
    "StagingMaxXMeters": 201.27, "StagingMaxYMeters": 99.92,
    "StagingMarginMeters": 30.0,
}

# Guard tower 3 of the Bahonar scenario: the army edge nearest the surveyed tower and the lane
# position the guard parks at (`CarlaControl/scripts/make_bahonar_scenario.py`, TOWER_POSTS[3]).
BAHONAR_TOWER_3 = ("26413459", 58.9)

POSITION_TOLERANCE_M = 0.02


@pytest.fixture(scope="module")
def installation() -> SumoInstallation:
    staged = _REPO / "Build" / "sumo-install"
    try:
        found = SumoInstallation.locate(staged if staged.exists() else None)
        found.sumo  # noqa: B018 -- raises when the installation has no sumo executable
    except FileNotFoundError as missing:
        pytest.skip(f"no SUMO to project with: {missing}")
    return found


@pytest.fixture(scope="module")
def query(installation):
    with SumoNetworkQuery(installation, NETWORK) as held:
        yield held


def circle(query, area_id: str, x: float, y: float, radius: float) -> dict:
    """A circular area centred on a network-frame point."""
    longitude, latitude = query.to_geodetic(x, y)
    return {"type": "Feature", "geometry": {"type": "Point", "coordinates": [longitude, latitude]},
            "properties": {"id": area_id, "name": area_id, "radius_m": radius}}


def polygon(query, area_id: str, *rings: list[tuple[float, float]]) -> dict:
    """A polygon from network-frame rings, closed here."""
    coordinates = []
    for ring in rings:
        points = [list(query.to_geodetic(x, y)) for x, y in ring]
        coordinates.append(points + [points[0]])
    return {"type": "Feature", "geometry": {"type": "Polygon", "coordinates": coordinates},
            "properties": {"id": area_id, "name": area_id}}


def resolve(query, *features, manifest=MANIFEST, network=NETWORK, near_m=None) -> dict:
    raw = json.dumps({"type": "FeatureCollection", "features": list(features)}).encode()
    source = AreaOfInterestSource.from_bytes(raw, "fixture.aoi.geojson", None)
    frame = GeodeticFrame(manifest["OriginLatitude"], manifest["OriginLongitude"])
    extra = {} if near_m is None else {"near_m": near_m}
    return AreaOfInterestResolver(network, manifest, query, frame, **extra).resolve(source)


def lane(area: dict, lane_id: str) -> dict | None:
    return next((entry for entry in area["sumo"]["lanes"] if entry["lane_id"] == lane_id), None)


def edge(area: dict, edge_id: str) -> dict | None:
    return next((entry for entry in area["sumo"]["edges"] if entry["edge_id"] == edge_id), None)


def assert_intervals(entry: dict, expected: list[list[float]]) -> None:
    assert len(entry["intervals_m"]) == len(expected), entry
    for got, want in zip(entry["intervals_m"], expected, strict=True):
        assert got == pytest.approx(want, abs=POSITION_TOLERANCE_M), entry


def lane_point(network: str, lane_id: str, position: float) -> tuple[float, float]:
    """The network-frame point at a SUMO lane position, scaled onto the shape as SUMO scales it."""
    root = ET.fromstring(network)
    element = next(ln for e in root.findall("edge") for ln in e.findall("lane")
                   if ln.get("id") == lane_id)
    points = [tuple(map(float, p.split(",")[:2])) for p in element.get("shape").split()]
    shape_length = sum(math.dist(a, b) for a, b in zip(points, points[1:], strict=False))
    target = position * shape_length / float(element.get("length"))
    run = 0.0
    for a, b in zip(points, points[1:], strict=False):
        step = math.dist(a, b)
        if run + step >= target:
            t = (target - run) / step
            return a[0] + t * (b[0] - a[0]), a[1] + t * (b[1] - a[1])
        run += step
    return points[-1]


def shipped(name: str) -> WorldPackageReader:
    path = PACKAGES / f"{name}.cwp"
    if not path.exists():
        pytest.skip(f"{path} is not in this tree")
    return WorldPackageReader(path)


# ---- where an area falls on the lanes --------------------------------------------------------------

def test_a_circle_on_a_lane_reports_the_stretch_it_covers(query):
    table = resolve(query, circle(query, "kerb", 50.0, -1.68, 10.0))
    area = table["areas"][0]
    eastbound = lane(area, "901#0_0")
    assert eastbound["containment"] == "crossing"
    assert_intervals(eastbound, [[40.0, 60.0]])
    assert eastbound["s_begin_m"] == pytest.approx(40.0, abs=POSITION_TOLERANCE_M)
    # The westbound lane runs the other way, 3.36 m from the centre: a chord of 2*sqrt(100-3.36^2),
    # measured from its own start at x = 93.29.
    half = math.sqrt(10.0 ** 2 - 3.36 ** 2)
    assert_intervals(lane(area, "-901#0_0"), [[93.29 - 50.0 - half, 93.29 - 50.0 + half]])
    assert edge(area, "901#0")["containment"] == "crossing"


def test_a_lane_wholly_inside_is_inside_from_end_to_end(query):
    table = resolve(query, polygon(query, "block", [(-1, -3), (94, -3), (94, 3), (-1, 3)]))
    area = table["areas"][0]
    whole = lane(area, "901#0_0")
    assert whole["containment"] == "inside"
    assert (whole["s_begin_m"], whole["s_end_m"]) == (0.0, 93.29)
    assert edge(area, "901#0")["containment"] == "inside"
    # West Street's eastbound lane ends at x = 0, so only its last metre is inside.
    tail = lane(area, "900_0")
    assert tail["containment"] == "crossing"
    assert_intervals(tail, [[200.27, 201.27]])


def test_a_lane_that_leaves_and_comes_back_reports_both_stretches(query):
    notched = [(10, -10), (40, -10), (40, -0.5), (50, -0.5), (50, -10), (80, -10), (80, 10),
               (10, 10)]
    area = resolve(query, polygon(query, "notched", notched))["areas"][0]
    assert_intervals(lane(area, "901#0_0"), [[10.0, 40.0], [50.0, 80.0]])
    assert_intervals(lane(area, "-901#0_0"), [[13.29, 83.29]])


def test_a_hole_is_outside_its_area(query):
    outer = [(10, -10), (80, -10), (80, 10), (10, 10)]
    hole = [(40, -5), (50, -5), (50, 5), (40, 5)]
    area = resolve(query, polygon(query, "holed", outer, hole))["areas"][0]
    assert_intervals(lane(area, "901#0_0"), [[10.0, 40.0], [50.0, 80.0]])
    assert_intervals(lane(area, "-901#0_0"), [[13.29, 43.29], [53.29, 83.29]])


def test_a_lane_passing_close_is_near_and_one_passing_far_is_not(query):
    close = resolve(query, circle(query, "close", 50.0, -30.0, 5.0))["areas"][0]
    near = lane(close, "901#0_0")
    assert near["containment"] == "near" and "intervals_m" not in near
    assert near["distance_m"] == pytest.approx(30.0 - 1.68 - 5.0, abs=POSITION_TOLERANCE_M)
    far = resolve(query, circle(query, "far", 50.0, -70.0, 5.0))["areas"][0]
    assert lane(far, "901#0_0") is None, "63 m away is further than near_m"


def test_near_is_measured_on_the_shape_not_on_its_bounding_box(query):
    """Diagonally off the lane's start, 50.39 m away, while each axis of its bounding box is within
    50 m of the area's: the distance, not the box, decides."""
    diagonal = resolve(query, circle(query, "diagonal", -40.0, -40.0, 5.0))["areas"][0]
    assert lane(diagonal, "901#0_0") is None
    assert lane(diagonal, "900_0")["distance_m"] == pytest.approx(40.0 - 1.68 - 5.0, abs=0.02)


def test_every_listed_lane_reports_the_classes_sumo_admits(query):
    area = resolve(query, circle(query, "kerb", 50.0, -1.68, 10.0))["areas"][0]
    assert lane(area, "901#0_0")["allowed_vclasses"] == query.allowed_vclasses("901#0_0")
    assert "passenger" in lane(area, "901#0_0")["allowed_vclasses"]


# ---- the frames ------------------------------------------------------------------------------------

def test_the_published_geometry_is_the_bridge_identity(query):
    ring = [(10, -10), (80, -10), (80, 10), (10, 10)]
    area = resolve(query, polygon(query, "block", ring))["areas"][0]
    carla = area["carla_local"]
    assert carla["type"] == "Polygon"
    for (x, y), (cx, cy) in zip(ring + ring[:1], carla["coordinates"][0], strict=True):
        assert (cx, cy) == pytest.approx((x, -y), abs=0.001)
    assert area["envelope_carla_m"] == pytest.approx([10, -10, 80, 10], abs=0.001)
    kerb = resolve(query, circle(query, "kerb", 50.0, -1.68, 10.0))["areas"][0]["carla_local"]
    assert kerb["type"] == "Circle" and kerb["radius_m"] == 10.0
    assert kerb["centre"] == pytest.approx([50.0, 1.68], abs=0.001)


def test_the_table_records_the_frame_it_was_placed_in(query):
    table = resolve(query, circle(query, "kerb", 50.0, -1.68, 10.0))
    frame = table["frame"]
    assert frame["carla_from_sumo"] == "carla(x, y) = sumo(x, -y)"
    assert frame["net_offset_m"] == [0.0, 0.0]
    assert 0.0 <= frame["geodesy_worst_residual_m"] < 0.001
    assert frame["projected_by"].startswith("SUMO ")


def test_an_origin_a_tenth_of_a_metre_away_is_refused(query):
    """V5.12. 1e-6 degree of latitude is 0.11 m: the network and the manifest describe different
    frames. 1e-7 degree is 0.011 m, inside the limit, and passes."""
    moved = dict(MANIFEST, OriginLatitude=MANIFEST["OriginLatitude"] + 1e-6)
    with pytest.raises(AreaOfInterestError) as raised:
        resolve(query, circle(query, "kerb", 50.0, -1.68, 10.0), manifest=moved)
    assert raised.value.problems[0].startswith("V5.12 area 'kerb'")
    nudged = dict(MANIFEST, OriginLatitude=MANIFEST["OriginLatitude"] + 1e-7)
    assert resolve(query, circle(query, "kerb", 50.0, -1.68, 10.0), manifest=nudged)["areas"]


def test_a_network_carrying_a_road_offset_is_refused_naming_the_offset(installation):
    """A road-offset build shifts the network's metres off the geographic frame on purpose, so an
    area drawn on the imagery and the same area placed by SUMO sit 10 m apart."""
    offset = NETWORK.replace('netOffset="0.00,0.00"', 'netOffset="10.00,0.00"')
    with SumoNetworkQuery(installation, offset) as shifted:
        feature = circle(shifted, "kerb", 60.0, -1.68, 10.0)
        with pytest.raises(AreaOfInterestError) as raised:
            resolve(shifted, feature, network=offset)
    assert "V5.12" in raised.value.problems[0] and "netOffset [10.0, 0.0]" in raised.value.problems[0]


# ---- warnings -----------------------------------------------------------------------------------

def test_an_area_across_the_staging_edge_is_warned_about(query):
    area = resolve(query, circle(query, "edge", 195.0, -1.68, 10.0))["areas"][0]
    assert any(w.startswith("V5.5") and "crosses the edge" in w for w in area["warnings"])
    outside = resolve(query, circle(query, "beyond", 260.0, -1.68, 5.0))["areas"][0]
    assert any(w.startswith("V5.5") and "wholly outside" in w for w in outside["warnings"])


def test_an_area_in_the_staging_ring_and_out_of_reach_is_warned_about(query):
    # SUMO (180, 60) is CARLA (180, -60): inside the staging rectangle, outside the 30 m inset.
    area = resolve(query, circle(query, "ring", 180.0, 60.0, 5.0))["areas"][0]
    assert any(w.startswith("V5.6") for w in area["warnings"])
    assert any(w.startswith("V5.7") for w in area["warnings"])
    inland = resolve(query, circle(query, "inland", -150.0, 60.0, 5.0))["areas"][0]
    assert not any(w.startswith("V5.6") for w in inland["warnings"])


def test_an_area_on_the_roads_warns_about_nothing(query):
    area = resolve(query, circle(query, "kerb", 50.0, -1.68, 10.0))["areas"][0]
    assert area["warnings"] == []


def test_an_empty_table_says_no_areas_were_declared():
    table = AreaOfInterestResolver.empty_table(NETWORK, MANIFEST)
    assert table["areas"] == [] and table["source_sha256"] == ""
    assert table["resolved_version"] == 1


def test_resolving_twice_gives_the_same_table(query):
    feature = circle(query, "kerb", 50.0, -1.68, 10.0)
    assert json.dumps(resolve(query, feature), sort_keys=True) == \
        json.dumps(resolve(query, feature), sort_keys=True)


# ---- the shipped worlds ---------------------------------------------------------------------------

def test_positions_are_lane_positions_not_arc_lengths(installation):
    """Arapahoe's `223207869#0_1` is 204.10 m to SUMO and 209.86 m along its shape. A stop written at
    position 180 sits at 185.08 m of shape; an area centred there must report 180, or every `<stop>`
    sited from the table sits five metres from the area."""
    reader = shipped("Arapahoe_I25")
    network = reader.network_text()
    x, y = lane_point(network, "223207869#0_1", 180.0)
    with SumoNetworkQuery(installation, network) as held:
        area = resolve(held, circle(held, "stop", x, y, 3.0),
                       manifest=reader.manifest, network=network)["areas"][0]
    entry = lane(area, "223207869#0_1")
    scale = 204.10 / 209.86
    assert_intervals(entry, [[180.0 - 3.0 * scale, 180.0 + 3.0 * scale]])


def test_the_bahonar_tower_resolves_to_the_post_its_scenario_parks_at(installation):
    """Placed by the WGS84 transform the telemetry uses, resolved by SUMO: the two implementations
    meet at the lane position the scenario author validated with duarouter."""
    reader = shipped("Shahid_Bahonar_Port")
    network = reader.network_text()
    edge_id, position = BAHONAR_TOWER_3
    x, y = lane_point(network, f"{edge_id}_0", position)
    frame = GeodeticFrame(*reader.origin)
    longitude, latitude = frame.to_geodetic(x, -y)
    feature = {"type": "Feature", "geometry": {"type": "Point", "coordinates": [longitude, latitude]},
               "properties": {"id": "tower_03", "name": "Guard tower 3", "radius_m": 25.0}}
    with SumoNetworkQuery(installation, network) as held:
        area = resolve(held, feature, manifest=reader.manifest, network=network)["areas"][0]
    post = lane(area, f"{edge_id}_0")
    assert post["containment"] == "crossing"
    assert post["s_begin_m"] == pytest.approx(position - 25.0, abs=0.05)
    assert any(a <= position <= b for a, b in post["intervals_m"])


def test_an_edge_is_inside_crossing_or_near_as_its_lanes_are(installation):
    reader = shipped("Arapahoe_I25")
    network = reader.network_text()
    root = ET.fromstring(network)
    # The first straight two-lane edge longer than 80 m, by id, so the choice is reproducible.
    chosen = None
    for element in sorted(root.findall("edge"), key=lambda e: e.get("id")):
        lanes = element.findall("lane")
        if element.get("function") or len(lanes) != 2:
            continue
        shape = [tuple(map(float, p.split(","))) for p in lanes[0].get("shape").split()]
        if len(shape) == 2 and float(lanes[0].get("length")) > 80.0:
            chosen = element
            break
    assert chosen is not None
    edge_id = chosen.get("id")
    length = float(chosen.find("lane").get("length"))
    mid = lane_point(network, f"{edge_id}_0", length / 2)
    start = lane_point(network, f"{edge_id}_0", 0.0)
    end = lane_point(network, f"{edge_id}_0", length)
    along = ((end[0] - start[0]) / length, (end[1] - start[1]) / length)
    right = (along[1], -along[0])   # lane 0 is the rightmost; step further right, off the road
    with SumoNetworkQuery(installation, network) as held:
        def containment_of(feature):
            area = resolve(held, feature, manifest=reader.manifest, network=network)["areas"][0]
            return edge(area, edge_id)["containment"]

        assert containment_of(circle(held, "spot", *mid, 1.0)) == "crossing"
        box = [(start[0] - 20 * along[0] + 30 * right[0], start[1] - 20 * along[1] + 30 * right[1]),
               (end[0] + 20 * along[0] + 30 * right[0], end[1] + 20 * along[1] + 30 * right[1]),
               (end[0] + 20 * along[0] - 30 * right[0], end[1] + 20 * along[1] - 30 * right[1]),
               (start[0] - 20 * along[0] - 30 * right[0], start[1] - 20 * along[1] - 30 * right[1])]
        assert containment_of(polygon(held, "box", box)) == "inside"
        aside = (mid[0] + 25 * right[0], mid[1] + 25 * right[1])
        assert containment_of(circle(held, "aside", *aside, 1.0)) == "near"

        # A strip holding lane 0 whole, with lane 1 more than near_m away: lane 1 is not listed at
        # all, and an edge is inside only when every one of its lanes is.
        strip = [(start[0] - 20 * along[0] + right[0], start[1] - 20 * along[1] + right[1]),
                 (end[0] + 20 * along[0] + right[0], end[1] + 20 * along[1] + right[1]),
                 (end[0] + 20 * along[0] - right[0], end[1] + 20 * along[1] - right[1]),
                 (start[0] - 20 * along[0] - right[0], start[1] - 20 * along[1] - right[1])]
        area = resolve(held, polygon(held, "strip", strip), manifest=reader.manifest,
                       network=network, near_m=0.5)["areas"][0]
        assert lane(area, f"{edge_id}_0")["containment"] == "inside"
        assert lane(area, f"{edge_id}_1") is None
        assert edge(area, edge_id)["containment"] == "crossing"
