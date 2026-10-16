"""The geographic point, the street narrowed by a point, the gateway and the junction movement.

`07_Scenario_Authoring.md` §4.2: on a map with few street names -- Bahonar names 4.5 % of its edges --
an author places behaviour by a surveyed point, a road leaving the world or a turn, so those forms
resolve here, against the fixture world, and each refuses rather than guesses (D7.4): a point too far
from any road, a point equidistant from two, a gateway with nothing there, a turn made two ways.

The fixture network: West Street (900, eastbound; -900) meets East Street (901#0, 901#1; -901#0,
-901#1) at x = 0, and Cross Street (902#0 southbound into the signal at x = 100.64, 902#1 out of it;
-902#1, -902#0 northbound) crosses East Street there. Eastbound lanes lie at SUMO y = -1.68, which is
CARLA y = +1.68.
"""
from __future__ import annotations

import sys
import xml.etree.ElementTree as ET
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))
sys.path.insert(0, str(Path(__file__).resolve().parent))

from ScenarioWorldFixture import ScenarioWorldFixture  # noqa: E402

from carlacontrol.CompileFindings import CompileFindings  # noqa: E402
from carlacontrol.GeodeticFrame import GeodeticFrame  # noqa: E402
from carlacontrol.PlaceResolver import NetworkLane, PlaceResolver  # noqa: E402
from carlacontrol.ScenarioCompiler import ScenarioCompiler  # noqa: E402

FRAME = GeodeticFrame(39.5, -104.9)


def point(carla_x: float, carla_y: float, **more) -> dict:
    longitude, latitude = FRAME.to_geodetic(carla_x, carla_y)
    return {"lat": latitude, "lon": longitude, **more}


@pytest.fixture(scope="module")
def installation():
    try:
        return ScenarioWorldFixture.locate_sumo()
    except FileNotFoundError as missing:
        pytest.skip(f"no SUMO with duarouter: {missing}")


@pytest.fixture(scope="module")
def world(tmp_path_factory, installation) -> ScenarioWorldFixture:
    return ScenarioWorldFixture(tmp_path_factory.mktemp("world"), installation)


def compile_with(world, installation, tmp_path, places: dict, **changes):
    spec = world.specification(**changes)
    spec["places"].update(places)
    return ScenarioCompiler(installation).compile(world.write(spec, f"{tmp_path.name}.scenario.json"),
                                                  tmp_path / "out")


def refusals(result, check: int = 7) -> str:
    return " || ".join(f.message for f in result.findings.by_check(check) if f.outcome == "refuse")


# ---- the geographic point --------------------------------------------------------------------------

def test_a_point_snaps_to_the_lane_position_under_it_and_a_stop_there_uses_it(world, installation,
                                                                               tmp_path):
    spec = world.specification()
    spec["actors"][0]["stops"] = [{"place": "kerb_point", "duration": "5m"}]
    spec["places"]["kerb_point"] = point(50.0, 1.68, max_snap_m=5)
    result = ScenarioCompiler(installation).compile(world.write(spec, "point.scenario.json"),
                                                    tmp_path / "out")
    assert not result.refused, [str(f) for f in result.findings.findings if f.outcome == "refuse"]
    place = result.report["places"]["kerb_point"]
    assert place["form"] == "point" and place["lane"] == "901#0_0"
    assert place["end_pos"] == pytest.approx(50.0, abs=0.01)
    assert place["detail"].startswith("snapped 0.0")
    probe = next(e for e in ET.parse(result.files["routes"]).getroot() if e.get("id") == "probe")
    stop = probe.find("stop")
    assert stop.get("lane") == "901#0_0" and float(stop.get("endPos")) == pytest.approx(50.0, abs=0.01)


def test_a_point_past_its_snap_limit_is_refused_naming_the_distance(world, installation, tmp_path):
    result = compile_with(world, installation, tmp_path, {"far": point(50.0, -30.0, max_snap_m=5)})
    assert "past max_snap_m 5" in refusals(result) and "28.32 m" in refusals(result)


def test_a_point_no_lane_of_its_class_admits_is_refused(world, installation, tmp_path):
    result = compile_with(world, installation, tmp_path,
                          {"rails": point(50.0, 1.68, max_snap_m=5, vclass="rail")})
    assert "no lane of this world admits class 'rail'" in refusals(result)


def test_a_point_equidistant_from_two_edges_is_refused_with_both(world, installation, tmp_path):
    result = compile_with(world, installation, tmp_path, {"centre": point(50.0, 0.0, max_snap_m=5)})
    message = refusals(result)
    assert "alike" in message and "901#0" in message and "-901#0" in message


def test_a_street_is_narrowed_to_the_edge_nearest_a_point(world, installation, tmp_path):
    result = compile_with(world, installation, tmp_path, {
        "east_far": {"street": "East Street", "direction": "east", "near": point(150.0, 1.68)}})
    assert result.report["places"]["east_far"]["edges"] == ["901#1"]


# ---- the gateway -----------------------------------------------------------------------------------

@pytest.mark.parametrize(("authored", "edges"), [
    ({"gateway": "west", "travel": "in"}, ["900"]),
    ({"gateway": "west", "travel": "out"}, ["-900"]),
    ({"gateway": "east", "travel": "out"}, ["901#1"]),
    ({"gateway": "north", "travel": "in", "street": "Cross Street"}, ["902#0"]),
    ({"gateway": "south", "travel": "out"}, ["902#1"]),
])
def test_a_gateway_is_the_road_entering_or_leaving_the_world_on_that_side(world, installation,
                                                                          tmp_path, authored, edges):
    result = compile_with(world, installation, tmp_path, {"gate": authored})
    assert result.report["places"]["gate"]["edges"] == edges


def test_a_gateway_with_no_such_road_is_refused_naming_what_is_there(world, installation, tmp_path):
    result = compile_with(world, installation, tmp_path,
                          {"gate": {"gateway": "north", "travel": "in", "street": "West Street"}})
    assert "no edge enters the world on its north side on 'West Street'" in refusals(result)
    assert "Cross Street" in refusals(result)


# ---- the junction movement -------------------------------------------------------------------------

def test_a_movement_is_the_two_edges_a_connection_joins_and_routes_as_a_via(world, installation,
                                                                            tmp_path):
    spec = world.specification()
    spec["places"]["west_onto_east"] = {"from_street": "West Street", "to_street": "East Street"}
    spec["actors"][1]["via"] = ["west_onto_east"]
    result = ScenarioCompiler(installation).compile(world.write(spec, "movement.scenario.json"),
                                                    tmp_path / "out")
    assert not result.refused, [str(f) for f in result.findings.findings if f.outcome == "refuse"]
    assert result.report["places"]["west_onto_east"]["edges"] == ["900", "901#0"]
    hauler = next(r for r in result.report["routes"] if r["id"] == spec["actors"][1]["id"])
    assert hauler["via"] == ["900", "901#0"]
    route = hauler["route"]
    assert route[route.index("900") + 1] == "901#0"


def test_a_turn_made_several_ways_is_refused_until_directions_narrow_it(world, installation,
                                                                         tmp_path):
    """East Street meets Cross Street in both directions, and each turns either way: four turns."""
    every = compile_with(world, installation, tmp_path / "a",
                         {"turn": {"from_street": "East Street", "to_street": "Cross Street"}})
    assert "by 4 connections" in refusals(every) and "901#0 -> 902#1" in refusals(every)
    south = compile_with(world, installation, tmp_path / "b",
                         {"turn": {"from_street": "East Street", "to_street": "Cross Street",
                                   "to_direction": "south"}})
    assert "by 2 connections" in refusals(south)
    east_south = compile_with(world, installation, tmp_path / "c",
                              {"turn": {"from_street": "East Street", "to_street": "Cross Street",
                                        "from_direction": "east", "to_direction": "south"}})
    assert east_south.report["places"]["turn"]["edges"] == ["901#0", "902#1"]


def test_a_movement_where_one_edge_is_needed_is_refused(world, installation, tmp_path):
    spec = world.specification()
    spec["places"]["west_onto_east"] = {"from_street": "West Street", "to_street": "East Street"}
    spec["actors"][1]["from"] = "west_onto_east"
    result = ScenarioCompiler(installation).compile(world.write(spec, "movement1.scenario.json"),
                                                    tmp_path / "out")
    assert "is a junction movement" in refusals(result)


# ---- the arithmetic and the definitions, on networks made to test them -------------------------------

def test_a_lane_position_is_the_arc_length_scaled_as_sumo_maps_it():
    """SUMO's `length` and the drawn shape differ -- by up to 5.76 m on Arapahoe -- and a position is
    the shape's arc length times length over shape length."""
    lane = NetworkLane("a_0", "a", 200.0, 13.9, None, None, ((0.0, 0.0), (100.0, 0.0)))
    distance, position = lane.nearest((50.0, 3.0))
    assert distance == 3.0 and position == 100.0


# A 200 m square world: West Road from the west boundary to a crossing at the centre, East Road on to a
# node on the east boundary where South Lane also starts, and a stub, North Stub, ending 50 m inside the
# north boundary.
SYNTHETIC = """<net>
  <location netOffset="0.00,0.00" convBoundary="-100.00,-100.00,100.00,100.00"/>
  <edge id="1" from="w" to="c" name="West Road"><lane id="1_0" index="0" speed="13.9" length="100" shape="-100,0 0,0"/></edge>
  <edge id="-1" from="c" to="w" name="West Road"><lane id="-1_0" index="0" speed="13.9" length="100" shape="0,0 -100,0"/></edge>
  <edge id="2" from="c" to="e" name="East Road"><lane id="2_0" index="0" speed="13.9" length="100" shape="0,0 100,0"/></edge>
  <edge id="3" from="e" to="s" name="South Lane"><lane id="3_0" index="0" speed="13.9" length="108" shape="100,0 60,-100"/></edge>
  <edge id="4" from="c" to="n" name="North Stub"><lane id="4_0" index="0" speed="13.9" length="50" shape="0,0 0,50"/></edge>
  <junction id="w" type="priority" x="-100" y="0"/>
  <junction id="c" type="priority" x="0" y="0"/>
  <junction id="e" type="priority" x="100" y="0"/>
  <junction id="s" type="dead_end" x="60" y="-100"/>
  <junction id="n" type="dead_end" x="0" y="50"/>
</net>"""


@pytest.mark.parametrize(("authored", "edges"), [
    ({"gateway": "west", "travel": "in"}, ("1",)),
    ({"gateway": "west", "travel": "out"}, ("-1",)),
    ({"gateway": "south", "travel": "out"}, ("3",)),
    ({"gateway": "east", "travel": "out"}, None),
    ({"gateway": "north", "travel": "out"}, None),
])
def test_a_gateway_is_a_single_road_end_on_the_boundary(authored, edges):
    """An end two roads meet at is not a gateway, however near the boundary; an end fifty metres
    inside it is not either, however few roads serve it."""
    findings = CompileFindings()
    resolved = PlaceResolver(SYNTHETIC, None, {}, findings).resolve_all({"gate": authored})
    if edges is None:
        assert "gate" not in resolved and findings.by_check(7)
    else:
        assert resolved["gate"].edges == edges
