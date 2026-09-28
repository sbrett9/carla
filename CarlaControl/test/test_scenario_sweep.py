"""A sweep compiles every member in full, holds what it claims to hold, and pairs counterfactuals.

The properties asserted are the ones a corpus built from a sweep depends on: member ids that are
deterministic from the axis values; an illumination rule the compiler enforces rather than trusts
(check 43); `epoch.date` moving the sun while leaving the traffic **byte-identical** (07 §7.2.1); and a
counterfactual pair that shares its base's epoch, windows and light unless displaced in time, stated
as identical inputs rather than identical trajectories.
"""
from __future__ import annotations

import json
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))
sys.path.insert(0, str(Path(__file__).resolve().parent))

from ScenarioWorldFixture import ScenarioWorldFixture  # noqa: E402

from carlacontrol.ScenarioSweep import ScenarioSweep  # noqa: E402


@pytest.fixture(scope="module")
def installation():
    try:
        return ScenarioWorldFixture.locate_sumo()
    except FileNotFoundError as missing:
        pytest.skip(f"no SUMO with duarouter: {missing}")


@pytest.fixture(scope="module")
def world(tmp_path_factory, installation) -> ScenarioWorldFixture:
    fixture = ScenarioWorldFixture(tmp_path_factory.mktemp("world"), installation)
    fixture.write(fixture.specification(), "base.scenario.json")
    return fixture


def sweep(world, installation, tmp_path, name, **fields) -> dict:
    document = {"sweep_version": 1, "sweep_id": name, "base": "base.scenario.json", **fields}
    path = world.directory / f"{name}.sweep.json"
    path.write_text(json.dumps(document), encoding="utf-8")
    return ScenarioSweep(installation).compile(path, tmp_path / name)


def refusals(index) -> set[int]:
    return {f["check"] for f in index["findings"] if f["outcome"] == "refuse"}


def entries(path: Path) -> list[str]:
    """A route file's vehicles and flows, without its comments: the traffic it declares."""
    root = ET.parse(path).getroot()
    return [ET.tostring(e, encoding="unicode") for e in root if e.tag != "vType"]


def test_a_behaviour_sweep_compiles_every_member_with_deterministic_ids(world, installation,
                                                                       tmp_path):
    axes = [{"path": "seeds.sumo", "values": [42, 43]},
            {"path": "actors.probe.stops[0].duration", "values": ["5m", "10m"]}]
    first = sweep(world, installation, tmp_path, "dwell", axes=axes)
    second = sweep(world, installation, tmp_path / "again", "dwell", axes=axes)
    assert first["outcome"] == "compiled", first["findings"]
    assert len(first["members"]) == 4
    assert [m["member_id"] for m in first["members"]] == [m["member_id"] for m in second["members"]]
    assert all(m["outcome"] == "compiled" for m in first["members"])
    assert {tuple(a["value"] for a in m["assignments"]) for m in first["members"]} == {
        (42, "5m"), (42, "10m"), (43, "5m"), (43, "10m")}


def test_holding_illumination_refuses_an_axis_that_moves_the_sun(world, installation, tmp_path):
    index = sweep(world, installation, tmp_path, "held",
                  axes=[{"path": "epoch.date", "values": ["2026-03-21", "2026-06-21"]}])
    assert refusals(index) == {43}


def test_an_axis_on_a_window_begin_is_an_illumination_axis_whatever_it_is_called(world, installation,
                                                                               tmp_path):
    index = sweep(world, installation, tmp_path, "mislabelled",
                  axes=[{"path": "capture_windows.morning.begin", "values": ["d0 07:00"],
                         "kind": "behaviour"}], illumination="vary")
    assert 43 in refusals(index)


def test_holding_illumination_refuses_a_window_begin_axis(world, installation, tmp_path):
    index = sweep(world, installation, tmp_path, "window_hold",
                  axes=[{"path": "capture_windows.morning.begin",
                         "values": ["d0 07:00", "d0 07:30"]}])
    assert refusals(index) == {43}


def test_sweeping_the_date_moves_the_sun_and_leaves_the_traffic_byte_identical(world, installation,
                                                                              tmp_path):
    index = sweep(world, installation, tmp_path, "seasons", illumination="vary",
                  axes=[{"path": "epoch.date", "values": ["2026-03-21", "2026-06-21", "2026-09-21"]}])
    assert index["outcome"] == "compiled", index["findings"]
    members = index["members"]
    elevations = [m["windows"][0]["sun_open"]["elevation_deg"] for m in members]
    assert len({round(e, 1) for e in elevations}) == 3, elevations
    routes = [tmp_path / "seasons" / m["member_id"] / f"{m['member_id']}.rou.xml" for m in members]
    assert entries(routes[0]) == entries(routes[1]) == entries(routes[2])
    assert len({m["epoch_block_sha256"] for m in members}) == 3


def test_varying_only_illumination_refuses_a_behaviour_axis(world, installation, tmp_path):
    index = sweep(world, installation, tmp_path, "mixed", illumination="vary",
                  axes=[{"path": "epoch.date", "values": ["2026-06-21"]},
                        {"path": "seeds.sumo", "values": [42, 43]}])
    assert refusals(index) == {43}


def test_a_factorial_design_compiles_and_names_its_cells(world, installation, tmp_path):
    index = sweep(world, installation, tmp_path, "crossed", illumination="factorial",
                  axes=[{"path": "epoch.date", "values": ["2026-03-21", "2026-06-21"]},
                        {"path": "seeds.sumo", "values": [42, 43]}])
    assert index["outcome"] == "compiled"
    warning = [f for f in index["findings"] if f["check"] == 43]
    assert warning and warning[0]["outcome"] == "warn" and "4 cells" in warning[0]["message"]


def test_a_nominal_twin_shares_the_light_and_loses_the_anomaly(world, installation, tmp_path):
    index = sweep(world, installation, tmp_path, "twin",
                  counterfactuals=[{"actor": "probe", "mode": "nominal", "remove": ["stops"]}])
    assert index["outcome"] == "compiled", index["findings"]
    base, twin = index["members"]
    pair = index["pairs"][0]
    assert pair["base_member"] == base["member_id"] and pair["counterfactual_member"] == \
        twin["member_id"]
    assert pair["illumination_differs"] is False and pair["trajectories_expected_to_match"] is False
    assert twin["epoch_block_sha256"] == base["epoch_block_sha256"]
    assert twin["windows"] == base["windows"]
    plan = json.loads((tmp_path / "twin" / twin["member_id"]
                       / f"{twin['member_id']}.supervision.json").read_text(encoding="utf-8"))
    states = {row["entity_id"]: row["supervision"] for row in plan["entities"]}
    assert states["probe"] == ["nominal"]
    twin_routes = (tmp_path / "twin" / twin["member_id"]
                   / f"{twin['member_id']}.rou.xml").read_text(encoding="utf-8")
    probe = twin_routes.split('id="probe"')[1].split("</vehicle>")[0]
    assert "<stop" not in probe


def test_an_absent_counterfactual_removes_the_actor_and_its_instance(world, installation, tmp_path):
    index = sweep(world, installation, tmp_path, "absent",
                  counterfactuals=[{"actor": "probe", "mode": "absent"}])
    twin = index["members"][1]
    routes = (tmp_path / "absent" / twin["member_id"]
              / f"{twin['member_id']}.rou.xml").read_text(encoding="utf-8")
    assert 'id="probe"' not in routes
    plan = json.loads((tmp_path / "absent" / twin["member_id"]
                       / f"{twin['member_id']}.supervision.json").read_text(encoding="utf-8"))
    assert not any(i["instance_id"].endswith("probe_standoff") for i in plan["instances"])


def test_displacing_in_time_moves_the_sun_and_is_recorded_as_doing_so(world, installation, tmp_path):
    index = sweep(world, installation, tmp_path, "displaced",
                  counterfactuals=[{"actor": "probe", "mode": "displaced", "shift": "40m"}])
    assert index["outcome"] == "compiled", index["findings"]
    pair = index["pairs"][0]
    assert pair["displaced_in"] == ["time"] and pair["illumination_differs"] is True
    twin = index["members"][1]
    plan = json.loads((tmp_path / "displaced" / twin["member_id"]
                       / f"{twin['member_id']}.supervision.json").read_text(encoding="utf-8"))
    standoff = next(i for i in plan["instances"] if i["instance_id"].endswith("probe_standoff"))
    assert standoff["intervals"][0]["declared_start_civil"] == "2026-03-21T07:40:00-06:00"


def test_zipped_axes_of_different_lengths_are_refused(world, installation, tmp_path):
    index = sweep(world, installation, tmp_path, "zipped", pairing="zip",
                  axes=[{"path": "seeds.sumo", "values": [1, 2]},
                        {"path": "actors.probe.stops[0].duration", "values": ["5m"]}])
    assert refusals(index) == {53}


def test_an_axis_naming_nothing_in_the_base_is_refused(world, installation, tmp_path):
    index = sweep(world, installation, tmp_path, "dangling",
                  axes=[{"path": "actors.nobody.depart", "values": ["d0 07:00"]}])
    assert refusals(index) == {8}


def test_a_refused_member_refuses_the_sweep_with_its_own_check(world, installation, tmp_path):
    index = sweep(world, installation, tmp_path, "broken",
                  axes=[{"path": "actors.hauler.depart", "values": ["d0 09:30"]}])
    assert index["outcome"] == "refused" and 37 in refusals(index)
