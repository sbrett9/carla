"""The place index names streets by the edges that carry them, and says how much of the map that is.

Two halves. The first uses the cross-shaped network the world-build tests already use, whose streets
run along known lines, so each heading, grouping and order can be asserted exactly. The second
measures the index on the three shipped world packages and pins what was measured -- the coverage the
index publishes as its own caveat -- to the network each figure was measured on: a rebuilt world has a
different fingerprint and is skipped with a message saying the figure needs measuring again, rather
than failing on a number that was never wrong.
"""
from __future__ import annotations

import sys
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.PlaceIndex import WARN_BELOW_NAMED_FRACTION, PlaceIndex  # noqa: E402
from carlacontrol.WorldPackageReader import WorldPackageReader  # noqa: E402

FIXTURES = _REPO / "CarlaNet" / "test" / "CarlaNet.Tests" / "Map" / "Fixtures" / "Network"
PACKAGES = _REPO / "Build" / "world-packages"

# Measured 2026-09-25 on the shipped packages: (named, normal, distinct names, largest name, its
# edge count, scripts), keyed by map, each against the network fingerprint it was measured on.
MEASURED = {
    "Gardnerville_Centerville_Lane": (
        "a50ac5452330ef83485625a8ec89ff3dbec0d0d7b4fc7a9bfb50392ffb829bc6",
        52, 57, 10, "Centerville Lane", 18, {"LATIN": 10}),
    "Arapahoe_I25": (
        "ac83aa8b541158e60f012d185405ec20e5a924ff95f1a4609660ccd3a714191b",
        288, 317, 32, "South Yosemite Street", 65, {"LATIN": 32}),
    "Shahid_Bahonar_Port": (
        "672554bd5e65578d0efc20d32016cd874cb6f25c956f6161aba5d40a70c6e7ad",
        47, 1044, 6, "بزرگراه شهید "
                     "رجایی", 14, {"ARABIC": 6}),
}


@pytest.fixture(scope="module")
def cross() -> dict:
    return PlaceIndex((FIXTURES / "street_names_true.net.xml").read_text(encoding="utf-8")).to_dict()


def street(index: dict, name: str) -> dict:
    return next(s for s in index["streets"] if s["name"] == name)


def test_every_named_normal_edge_is_indexed_and_nothing_else(cross):
    coverage = cross["coverage"]
    # Ten normal edges, all named; the thirty internal edges inside the junctions are not counted.
    assert (coverage["normal_edges"], coverage["named_edges"], coverage["distinct_names"]) == \
        (10, 10, 3)
    assert sorted(s["name"] for s in cross["streets"]) == ["Cross Street", "East Street",
                                                           "West Street"]


def test_headings_are_compass_bearings_in_the_networks_frame(cross):
    """The network's +y is north. CARLA's is south, so a frame mix-up swaps north and south here."""
    cross_street = {e["edge_id"]: e for e in street(cross, "Cross Street")["edges"]}
    assert cross_street["-902#0"]["direction"] == "north"
    assert cross_street["-902#0"]["bearing_deg"] == pytest.approx(0.0, abs=0.1)
    assert cross_street["902#0"]["direction"] == "south"
    assert cross_street["902#0"]["bearing_deg"] == pytest.approx(180.0, abs=0.1)
    east = {e["edge_id"]: e for e in street(cross, "East Street")["edges"]}
    assert (east["901#0"]["direction"], east["-901#0"]["direction"]) == ("east", "west")


def test_each_direction_is_ordered_along_its_direction_of_travel(cross):
    east_street = street(cross, "East Street")["directions"]
    assert east_street == {"east": ["901#0", "901#1"], "west": ["-901#1", "-901#0"]}
    assert street(cross, "Cross Street")["directions"] == {
        "north": ["-902#1", "-902#0"], "south": ["902#0", "902#1"]}


def test_extents_are_in_carla_metres(cross):
    """North of the origin in the network (+y) is negative y in CARLA."""
    northbound = next(e for e in street(cross, "Cross Street")["edges"] if e["edge_id"] == "-902#0")
    min_x, min_y, max_x, max_y = northbound["extent_carla_m"]
    assert max_y < 0 and min_y == pytest.approx(-99.92, abs=0.01)


def test_edges_carry_what_a_resolver_needs(cross):
    record = next(e for e in street(cross, "West Street")["edges"] if e["edge_id"] == "900")
    assert record["length_m"] == 201.27 and record["lane_count"] == 1
    assert record["lane_ids"] == ["900_0"] and record["speed_mps"] == 13.41
    assert (record["from_junction"], record["to_junction"]) == ("100", "102")


def test_a_one_to_many_name_is_warned_about_but_full_coverage_is_not(cross):
    assert len(cross["warnings"]) == 1 and "one-to-many" in cross["warnings"][0]


def test_an_unnamed_network_is_warned_about():
    network = (FIXTURES / "street_names_omitted.net.xml").read_text(encoding="utf-8")
    index = PlaceIndex(network).to_dict()
    assert index["coverage"]["named_edges"] == 0 and index["streets"] == []
    assert any("carry a street name" in w for w in index["warnings"])


def test_the_scripts_a_name_is_written_in_are_measured():
    assert PlaceIndex.scripts("South Yosemite Street") == ["LATIN"]
    assert PlaceIndex.scripts("بلوار پاسدار"
                              "ان") == ["ARABIC"]


@pytest.mark.parametrize("map_name", sorted(MEASURED))
def test_the_shipped_worlds_measure_as_recorded(map_name):
    path = PACKAGES / f"{map_name}.cwp"
    if not path.exists():
        pytest.skip(f"{path} is not in this tree")
    reader = WorldPackageReader(path)
    fingerprint, named, normal, distinct, largest, largest_count, scripts = MEASURED[map_name]
    if reader.recorded_network_fingerprint != fingerprint:
        pytest.skip(f"{map_name} has been rebuilt since its coverage was measured; measure it again")
    index = PlaceIndex(reader.network_text()).to_dict()
    coverage = index["coverage"]
    assert (coverage["named_edges"], coverage["normal_edges"], coverage["distinct_names"]) == \
        (named, normal, distinct)
    assert coverage["largest_name"] == {"name": largest, "edge_count": largest_count}
    assert coverage["names_by_script"] == scripts
    sparse = named / normal < WARN_BELOW_NAMED_FRACTION
    assert any("carry a street name" in w for w in index["warnings"]) == sparse
