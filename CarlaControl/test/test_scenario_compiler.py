"""The scenario compiler: one specification in, a routed, locked scenario package out -- or a refusal.

Every check the compiler claims is exercised here against a specification or a world package broken in
exactly the way the check exists to catch, and the refusal is asserted by its check id; the success path
is asserted against what SUMO itself does with the output. Three properties are held above the rest,
because they are why the compiler exists:

* **the route file carries no supervision** -- labels travel only in the supervision plan (06 D6.1);
* **every time is plain seconds with its civil meaning recoverable** from the report and the plan;
* **the same specification, seed and world give byte-identical scenario files** -- reproducible
  traffic is the priority, so the routed routes and everything that decides the traffic are fixed at
  compile time and bound in the lock.
"""
from __future__ import annotations

import hashlib
import json
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))
sys.path.insert(0, str(Path(__file__).resolve().parent))

from ScenarioWorldFixture import CATALOGUE, EPOCH, NETWORK, ScenarioWorldFixture  # noqa: E402

import carlanet  # noqa: E402, F401  -- loads the CarlaNet assemblies the next import names
from CarlaNet.Types.Supervision import CoreVocabulary  # noqa: E402

from carlacontrol.AnnotationVocabulary import CORE_TERMS  # noqa: E402
from carlacontrol.CompileFindings import CompileFindings  # noqa: E402
from carlacontrol.IlluminationBand import IlluminationBand  # noqa: E402
from carlacontrol.NetworkFingerprint import NetworkFingerprint  # noqa: E402
from carlacontrol.RouteValidator import RouteRequest, RouteValidator  # noqa: E402
from carlacontrol.ResolutionReport import ResolutionReport  # noqa: E402
from carlacontrol.ScenarioCompiler import SKIPPED_DRY_RUN, ScenarioCompiler  # noqa: E402
from carlacontrol.ScenarioEpoch import ScenarioEpoch  # noqa: E402
from carlacontrol.ScenarioSchema import OWN_CHECKS, SCHEMA, ScenarioSchema  # noqa: E402
from carlacontrol.SumoDryRun import SumoDryRun  # noqa: E402
from carlacontrol.VehicleCatalogue import VehicleCatalogue  # noqa: E402
from carlacontrol.version import RELEASE  # noqa: E402
from carlacontrol.version import RELEASE as CARLACONTROL_RELEASE  # noqa: E402
from carlacontrol.version import __version__ as carlacontrol_version  # noqa: E402


@pytest.fixture(scope="module")
def installation():
    try:
        return ScenarioWorldFixture.locate_sumo()
    except FileNotFoundError as missing:
        pytest.skip(f"no SUMO with duarouter: {missing}")


@pytest.fixture(scope="module")
def world(tmp_path_factory, installation) -> ScenarioWorldFixture:
    return ScenarioWorldFixture(tmp_path_factory.mktemp("world"), installation)


def compile_spec(fixture, installation, tmp_path, name="spec", **changes):
    spec = fixture.write(fixture.specification(**changes), f"{name}.{tmp_path.name}.scenario.json")
    return ScenarioCompiler(installation).compile(spec, tmp_path / "out")


def checks(result, outcome="refuse") -> set[int]:
    return {f.check_id for f in result.findings.findings if f.outcome == outcome}


def messages(result, check: int) -> str:
    return " || ".join(f.message for f in result.findings.by_check(check))


def rewrite_package(source: Path, destination: Path, changes: dict[str, bytes]) -> Path:
    destination.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(source) as old, zipfile.ZipFile(destination, "w", zipfile.ZIP_STORED) as new:
        for info in old.infolist():
            new.writestr(info.filename, changes.get(info.filename, old.read(info.filename)))
    return destination


# ---- the success path ------------------------------------------------------------------------------

def test_the_fixture_compiles_and_writes_the_whole_package(world, installation, tmp_path):
    result = compile_spec(world, installation, tmp_path)
    assert not result.refused, [str(f) for f in result.findings.refusals]
    assert checks(result, "warn") == {17, 41}
    for role in ("routes", "config", "network", "supervision", "lock", "resolution",
                 "resolution_md"):
        assert result.files[role].exists(), role
    assert result.files["network"].read_text(encoding="utf-8") == NETWORK


def test_the_route_file_is_routed_sorted_in_seconds_and_carries_no_supervision(world, installation,
                                                                                tmp_path):
    result = compile_spec(world, installation, tmp_path)
    text = result.files["routes"].read_text(encoding="utf-8")
    root = ET.fromstring(text)
    entries = [e for e in root if e.tag in ("vehicle", "flow")]
    times = [float(e.get("depart") or e.get("begin")) for e in entries]
    assert times == sorted(times)
    assert all(e.find("route") is not None and e.find("route").get("edges") for e in entries)
    assert {e.get("id"): float(e.get("depart")) for e in entries if e.tag == "vehicle"} == {
        "patrol_d0_h6": 900.0, "probe": 3600.0, "hauler": 5400.0}
    # No label, term, supervision state or instance reaches the route file.
    for forbidden in ("fixture:", "annotated", "nominal", "probe_standoff", "marked"):
        assert forbidden not in text
    assert {p.get("key") for p in root.iter("param")} <= {"carla:blueprint", "carla:class_id",
                                                           "carla:catalogue_digest"}


def test_sumo_runs_the_compiled_scenario_and_stops_where_the_report_says(world, installation,
                                                                       tmp_path):
    result = compile_spec(world, installation, tmp_path)
    stops = tmp_path / "stops.xml"
    completed = subprocess.run(
        [str(installation.sumo), "-c", str(result.files["config"]), "--end", "4000",
         "--stop-output", str(stops), "--no-step-log", "true"],
        capture_output=True, text=True, timeout=300, check=False)
    assert completed.returncode == 0, completed.stderr
    probe = [line for line in stops.read_text(encoding="utf-8").splitlines() if 'id="probe"' in line]
    assert probe and 'lane="901#0_0"' in probe[0] and 'pos="51.50"' in probe[0]
    kerb = result.report["places"]["kerb"]
    assert (kerb["lane"], kerb["end_pos"]) == ("901#0_0", 51.5)


def test_entries_departing_together_keep_the_specification_s_order(world, installation, tmp_path):
    """SUMO inserts, and draws its random numbers, in the order it reads, so the order of entries
    departing together decides the traffic: the author's order is kept, flows before actors."""
    spec = world.specification()
    first = dict(spec["flows"][0], id="zulu", begin="d0 07:00")
    second = dict(spec["flows"][0], id="alpha", begin="d0 07:00")
    spec["flows"] = [first, second, *spec["flows"]]
    spec["actors"][0]["depart"] = "d0 07:00"
    result = ScenarioCompiler(installation).compile(world.write(spec, "order.scenario.json"),
                                                    tmp_path / "out")
    assert not result.refused
    root = ET.parse(result.files["routes"]).getroot()
    together = [e.get("id") for e in root if e.tag in ("vehicle", "flow")
                and float(e.get("depart") or e.get("begin")) == 3600.0]
    assert together == ["zulu", "alpha", spec["actors"][0]["id"]]


def test_compiling_twice_gives_byte_identical_scenario_files(world, installation, tmp_path):
    first = compile_spec(world, installation, tmp_path / "a", name="twice")
    second = compile_spec(world, installation, tmp_path / "b", name="twice")
    for role in ("routes", "config", "network", "supervision"):
        assert first.files[role].read_bytes() == second.files[role].read_bytes(), role
    assert first.lock["files"] == second.lock["files"]


def test_the_lock_binds_the_four_files_the_epoch_and_the_traffic(world, installation, tmp_path):
    result = compile_spec(world, installation, tmp_path)
    lock = result.lock
    for role in ("routes", "config", "network", "supervision"):
        path = result.files[role]
        assert lock["files"][role] == {"path": path.name,
                                       "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
    assert lock["epoch"] == EPOCH
    assert lock["epoch_block_sha256"] == ScenarioEpoch.read(EPOCH).digest
    assert lock["traffic"]["sumo_seed"] == 42 and lock["traffic"]["step_length_s"] == 0.05
    assert lock["traffic"]["routed_by"]["version"] == installation.version
    assert lock["traffic"]["routed_by"]["world_converter"] == "Eclipse SUMO netconvert 1.27.0"
    assert "sumo-install" not in json.dumps(lock), "the lock names no machine path"
    plan = result.plan
    assert plan["routes_digest"] == lock["files"]["routes"]["sha256"]
    assert plan["config_digest"] == lock["files"]["config"]["sha256"]
    # No lane closure, so no additional file for the plan to be bound to.
    assert plan["additional_digest"] is None


def test_the_lock_and_the_report_say_what_made_them_and_the_files_two_compiles_share_carry_no_commit(
        world, installation, tmp_path):
    result = compile_spec(world, installation, tmp_path)
    lock = json.loads(result.files["lock"].read_text(encoding="utf-8"))
    report = json.loads(result.files["resolution"].read_text(encoding="utf-8"))

    for document in (lock, report):
        producer = document["producer"]
        assert producer["tool"] == "carlacontrol.ScenarioCompiler"
        assert producer["tool_version"] == carlacontrol_version
        assert producer["sumo"] == installation.version
        assert producer["server"] is None and producer["written_utc"]
    assert lock["compiler"] == {"name": "carlacontrol.ScenarioCompiler", "version": CARLACONTROL_RELEASE}
    assert "**Produced by:** carlacontrol.ScenarioCompiler" in result.files["resolution_md"].read_text(
        encoding="utf-8")
    # The supervision plan, digested by the lock and compared across runs, carries no record of the
    # commit that wrote it; nor does the route file's header name more than the release.
    assert "producer" not in json.loads(result.files["supervision"].read_text(encoding="utf-8"))
    routes = result.files["routes"].read_text(encoding="utf-8")
    assert f"compiled by carlacontrol.ScenarioCompiler {RELEASE} from" in routes
    assert carlacontrol_version == RELEASE or carlacontrol_version not in routes


def test_a_lane_change_takes_three_seconds_and_the_lock_and_the_report_say_so(world, installation,
                                                                             tmp_path):
    """SUMO's default lane change crosses a lane width inside one step, which renders as a sideways
    jump (06 §6.2); the compiler writes a physical duration into the configuration and records it."""
    result = compile_spec(world, installation, tmp_path)
    configuration = ET.parse(result.files["config"]).getroot()
    assert configuration.find("processing/lanechange.duration").get("value") == "3"
    assert result.lock["traffic"]["processing"]["lanechange.duration"] == "3"
    assert result.report["lock"]["traffic"]["processing"]["lanechange.duration"] == "3"
    report = result.files["resolution_md"].read_text(encoding="utf-8")
    assert "| `lanechange.duration` | 3 |" in report
    assert "| `time-to-teleport` | -1 |" in report


def test_the_report_states_each_body_s_lights_beside_its_dimensions(world, installation, tmp_path):
    """The resolution report's vehicle section carries, per type and body, whether the body's
    headlights, brake lights and turn signals light up, from the catalogue's optical pass. Information
    for the author; a body whose lights are unlit refuses nothing and warns nothing."""
    result = compile_spec(world, installation, tmp_path)
    assert not result.refused, [str(f) for f in result.findings.refusals]
    types = result.report["vehicle_types"]["types"]
    assert types
    catalogue = VehicleCatalogue.load(CATALOGUE)
    for type_id, body in types.items():
        assert set(body["lights"]) == {"headlights", "brake_lights", "turn_signals"}, type_id
        assert body["lights"] == catalogue.lights_of(body["blueprint"])
    report = result.files["resolution_md"].read_text(encoding="utf-8")
    assert "## Vehicle types" in report
    first_type, first = next(iter(types.items()))
    assert (f"| {first_type} | {first['blueprint']} | {first['length_m']:.3f} | "
            f"{first['body_width_m']:.3f} | {first['height_m']:.3f} | {first['lights']['headlights']} | "
            f"{first['lights']['brake_lights']} | {first['lights']['turn_signals']} |") in report
    assert all(f.check_id != 14 for f in result.findings.findings)


def test_the_plan_states_every_subject_explicitly(world, installation, tmp_path):
    plan = compile_spec(world, installation, tmp_path).plan
    assert {row["entity_id"]: row["supervision"] for row in plan["entities"]} == {
        "hauler": ["nominal"], "patrol_d0_h6": ["nominal"], "probe": ["annotated"]}
    assert plan["cohorts"] == [{"flow_id": "ambient", "supervision": "unlabelled", "labels": [],
                                "parameters": {}}]
    # Every row is a vehicle's or a flow's: no instance without a participant, no interval without
    # its vehicle, and no row of any kind for the patrol the rota skips (06 §3.5).
    assert all(row["participants"] for row in plan["instances"])
    assert all(interval["entity_id"] for row in plan["instances"] for interval in row["intervals"])
    (series,) = plan["series"]
    (slot,) = series["slots"]
    assert (slot["slot_key"], slot["entity_id"], slot["aoi_ref"]) == ("patrol_d0_h6", "patrol_d0_h6", "kerb")
    assert slot["declared_start_civil"] == "2026-03-21T06:15:00-06:00"
    assert "patrol_d0_h8" not in json.dumps(plan)
    assert "realisation" not in json.dumps(plan)
    assert plan["vocabulary"]["core"]["terms"]["supervision_state"] == ["annotated", "nominal",
                                                                       "unlabelled"]
    assert "solar" not in json.dumps(plan) and "epoch" not in plan


def test_the_plan_s_vocabulary_names_doc_11_s_six_illumination_bands(world, installation, tmp_path):
    """The published vocabulary and the association statistic use one band table (07 §12 q13)."""
    result = compile_spec(world, installation, tmp_path)
    assert result.plan["vocabulary"]["core"]["terms"]["illumination_band"] == [
        "day", "golden", "civil_twilight", "nautical_twilight", "astronomical_twilight", "night"]
    bands = result.lock["illumination_label_association"]["bands"]
    assert bands and set(bands) <= set(result.plan["vocabulary"]["core"]["terms"]["illumination_band"])


def test_the_plan_s_core_is_version_3_generated_from_the_enumerations_in_carlanet_types(
        world, installation, tmp_path):
    """06 D6.30: the core half is generated from the code that branches on it, so the plan publishes
    CarlaNet.Types' table family by family, and the lock records its version. Version 3 carries no
    realisation, no observability outcome and no reserved phase (06 §3.5, the ruling of 2026-10-05)."""
    result = compile_spec(world, installation, tmp_path)
    core = result.plan["vocabulary"]["core"]
    assert core["vocabulary_version"] == result.plan["vocabulary_version"] == 3
    assert result.lock["vocabulary"]["core_version"] == 3
    assert core["source"] == "CarlaNet.Types.Supervision.CoreVocabulary"
    assert core["terms"] == {str(family.Family): [str(term) for term in family.Terms]
                             for family in CoreVocabulary.Families}
    assert set(core["terms"]) == {"supervision_state", "subject_kind", "interval_onset", "closed_by",
                                  "illumination_band", "cadence", "reserved_role", "interval_anchor",
                                  "render_state", "render_reason"}
    assert core["terms"]["subject_kind"] == ["entity", "cohort"]
    assert "slot_unrealised" not in core["terms"]["closed_by"]
    assert core["terms"]["interval_anchor"] == ["depart", "stop", "stop_end", "phase"]
    assert core["terms"]["render_state"] == ["rendered", "simulated_only"]
    assert "outside_limit" in core["terms"]["render_reason"]


def test_the_schema_spells_the_core_s_terms_as_the_core_does():
    """The specification schema restates a few core families as its enums and its anchor pattern;
    each must be the core's own spelling, or an author could write a term the core does not have."""
    definitions = SCHEMA["$defs"]
    supervision = definitions["supervision"]["properties"]
    states = set(CORE_TERMS["supervision_state"])
    assert set(supervision["instances"]["items"]["properties"]["supervision"]["enum"]) <= states
    assert set(supervision["cohorts"]["items"]["properties"]["supervision"]["enum"]) == states
    assert set(supervision["series"]["items"]["properties"]["supervision"]["enum"]) == states
    assert definitions["term"]["properties"]["applies_to"]["items"]["enum"] == \
        CORE_TERMS["subject_kind"] == ["entity", "cohort"]
    # The absence shape is gone from the schema with the ruling of 2026-10-05 (06 §3.5).
    assert "realisation" not in definitions["term"]["properties"]
    assert "absences" not in supervision
    assert "realisation" not in CORE_TERMS and "observability_outcome" not in CORE_TERMS
    assert "reserved_phase" not in CORE_TERMS
    pattern = re.compile(definitions["anchor"]["properties"]["start"]["pattern"])
    depart, *indexed = CORE_TERMS["interval_anchor"]
    assert pattern.match(depart) and not pattern.match(f"{depart}:0")
    assert all(pattern.match(f"{kind}:3") and not pattern.match(kind) for kind in indexed)
    assert not pattern.match("arrive:0")


def test_the_report_states_every_time_in_seconds_and_civil(world, installation, tmp_path):
    report = compile_spec(world, installation, tmp_path).report
    assert report["epoch"]["statement"].startswith("t = 0 is 2026-03-21T06:00:00-06:00")
    probe = next(r for r in report["routes"] if r["id"] == "probe")
    assert probe["depart"] == {"authored": {"instant": "probe_time"}, "form": "day_clock",
                               "seconds": 3600.0, "civil": "2026-03-21T07:00:00-06:00"}
    window = report["capture_windows"][0]
    assert window["civil_date"] == "2026-03-21" and window["sun_open"]["sun_date"] == "2026-03-21"
    assert report["rotas"][0]["skips"][0]["because"] == "the second patrol does not come"


# ---- the specification and the world binding ----------------------------------------------------

def test_an_unknown_field_is_refused_under_check_53(world, installation, tmp_path):
    result = compile_spec(world, installation, tmp_path, dst_policy="fixed_offset")
    assert checks(result) == {53}
    assert not (tmp_path / "out" / "street_layout_probe.rou.xml").exists()
    assert result.files["resolution"].exists()


def test_a_different_network_fingerprint_is_refused_under_check_1(world, installation, tmp_path):
    result = compile_spec(world, installation, tmp_path,
                          world={"package": "StreetLayout.cwp", "network_fingerprint": "0" * 64})
    assert 1 in checks(result)


def test_a_package_carrying_a_network_it_does_not_record_is_refused_under_check_2(
        world, installation, tmp_path):
    manifest = json.loads(zipfile.ZipFile(world.package).read("world.json"))
    manifest["NetworkFingerprint"] = "f" * 64
    rewrite_package(world.package, tmp_path / "w" / "StreetLayout.cwp",
                    {"world.json": json.dumps(manifest).encode()})
    spec = world.specification()
    spec["world"]["package"] = str(tmp_path / "w" / "StreetLayout.cwp")
    result = ScenarioCompiler(installation).compile(world.write(spec, "c2.scenario.json"),
                                                    tmp_path / "out")
    assert 2 in checks(result)


@pytest.mark.parametrize(("entry", "change", "check"), [
    ("map.xodr", ("north=\"99.92\"", "north=\"109.92\""), 3),
    ("world.json", ("+lat_0=39.5 ", "+lat_0=39.6 "), 4),
])
def test_a_package_in_another_frame_is_refused(world, installation, tmp_path, entry, change, check):
    raw = zipfile.ZipFile(world.package).read(entry).decode("utf-8")
    rewrite_package(world.package, tmp_path / "w" / "StreetLayout.cwp",
                    {entry: raw.replace(*change).encode()})
    spec = world.specification()
    spec["world"]["package"] = str(tmp_path / "w" / "StreetLayout.cwp")
    result = ScenarioCompiler(installation).compile(world.write(spec, f"frame{check}.scenario.json"),
                                                    tmp_path / "out")
    assert check in checks(result)


def test_a_network_carrying_a_road_offset_is_refused_under_check_5(world, installation, tmp_path):
    shifted = NETWORK.replace('netOffset="0.00,0.00"', 'netOffset="5.00,-3.00"')
    manifest = json.loads(zipfile.ZipFile(world.package).read("world.json"))
    manifest["NetworkFingerprint"] = NetworkFingerprint.of_text(shifted)
    rewrite_package(world.package, tmp_path / "w" / "StreetLayout.cwp",
                    {"map.net.xml": shifted.encode(), "world.json": json.dumps(manifest).encode()})
    spec = world.specification()
    spec["world"] = {"package": str(tmp_path / "w" / "StreetLayout.cwp"),
                     "network_fingerprint": manifest["NetworkFingerprint"]}
    result = ScenarioCompiler(installation).compile(world.write(spec, "c5.scenario.json"),
                                                    tmp_path / "out")
    assert checks(result) == {5}
    assert "5.0, -3.0" in messages(result, 5)



# ---- an explicit route in phases, a held phase waypointed per edge -----------------------------------

# A closed circuit on the fixture network: east along East Street, the turnaround at its end, back
# west, the turnaround at West Street's end, and east again to where it began.
LOOP = ["901#0", "901#1", "-901#1", "-901#0", "-900", "900"]
LOOP_LENGTH_M = 93.29 + 93.28 + 93.28 + 93.29 + 201.27 + 201.27


def orbit_specification(world, **orbiter_changes) -> dict:
    """The fixture scenario with an orbiter: in on West Street at the posted limit, three laps of
    LOOP held to 8 m/s, and out along East Street on its own speedFactor."""
    spec = world.specification()
    names = {edge: f"orbit_{index}" for index, edge in enumerate(dict.fromkeys(LOOP))}
    for edge, name in names.items():
        spec["places"][name] = {"edge": edge}
    orbiter = {"id": "orbiter", "type": "saloon", "depart": "d0 07:10",
               "depart_speed": "13.41", "arrival_speed": "current",
               "phases": [{"route": [names["900"]], "hold": "posted"},
                          {"route": [names[e] for e in LOOP], "repeat": 3, "hold": 8.0},
                          {"route": [names["901#0"], names["901#1"]]}]}
    orbiter.update(orbiter_changes)
    spec["actors"].append(orbiter)
    return spec


def orbiter_entry(result) -> ET.Element:
    root = ET.parse(result.files["routes"]).getroot()
    return next(e for e in root if e.tag == "vehicle" and e.get("id") == "orbiter")


def test_phases_compile_to_one_route_and_a_waypoint_per_held_edge(world, installation, tmp_path):
    result = ScenarioCompiler(installation).compile(
        world.write(orbit_specification(world), "phases.scenario.json"), tmp_path / "out")
    assert not result.refused, [str(f) for f in result.findings.findings if f.outcome == "refuse"]
    orbiter = orbiter_entry(result)
    assert orbiter.find("route").get("edges").split() == ["900", *LOOP * 3, "901#0", "901#1"]
    waypoints = orbiter.findall("stop")
    assert len(waypoints) == 1 + 3 * len(LOOP), "one per held edge, none on the exit"
    assert waypoints[0].get("lane") == "900_0" and waypoints[0].get("speed") == "13.41"
    assert {w.get("speed") for w in waypoints[1:]} == {"8.00"}
    assert all(w.get("startPos") == "0.00" and w.get("duration") is None for w in waypoints)
    assert [w.get("lane") for w in waypoints[1:1 + len(LOOP)]] == [f"{e}_0" for e in LOOP]
    report = next(r for r in result.report["routes"] if r["id"] == "orbiter")
    assert report["waypoints"] == 19 and report["phases"][1] == {"edges": 6, "repeat": 3,
                                                                  "hold": 8.0}


def test_sumo_drives_the_phases_and_holds_the_held_one(world, installation, tmp_path):
    """Read back from SUMO: the orbiter drives the whole route, and on West Street's westbound
    carriageway -- 201 m with no junction along it, driven only inside the held laps -- it never
    exceeds the 8 m/s hold, where its own speedFactor at the 13.41 m/s limit would take it past."""
    result = ScenarioCompiler(installation).compile(
        world.write(orbit_specification(world), "phases_run.scenario.json"), tmp_path / "out")
    tripinfo, fcd = tmp_path / "tripinfo.xml", tmp_path / "fcd.xml"
    completed = subprocess.run(
        [str(installation.sumo), "-c", str(result.files["config"]), "--tripinfo-output",
         str(tripinfo), "--fcd-output", str(fcd), "--device.fcd.explicit", "orbiter",
         "--no-step-log", "true"],
        capture_output=True, text=True, timeout=300, check=False)
    assert completed.returncode == 0, completed.stderr
    trip = next(t for t in ET.parse(tripinfo).getroot().iter("tripinfo") if t.get("id") == "orbiter")
    assert float(trip.get("routeLength")) > 3 * LOOP_LENGTH_M
    samples = [(v.get("lane"), float(v.get("speed"))) for v in ET.parse(fcd).getroot().iter("vehicle")
               if v.get("id") == "orbiter"]
    westbound = [speed for lane, speed in samples if lane == "-900_0"]
    assert westbound and max(westbound) <= 8.0 + 0.05


@pytest.mark.parametrize(("changes", "check"), [
    ({"route": ["west_gate", "east_end"]}, 53),
    ({"from": "west_gate"}, 53),
    ({"stops": [{"place": "kerb", "duration": "1m"}]}, 53),
    ({"phases": [{"route": ["west_gate"], "hold": "fast"}]}, 53),
    ({"phases": [{"route": ["west_gate"], "repeat": 0}]}, 53),
    ({"phases": [{"route": ["east_before_cross", "east_end"], "repeat": 2}]}, 13),
])
def test_phases_given_with_another_route_or_a_stop_or_not_closing_are_refused(
        world, installation, tmp_path, changes, check):
    spec = orbit_specification(world, **changes)
    result = ScenarioCompiler(installation).compile(world.write(spec, f"phases{check}.scenario.json"),
                                                    tmp_path / "out")
    assert check in checks(result)


# ---- lane closures, the one element an additional file carries ---------------------------------------

# West Street's westbound carriageway, which no route of the fixture drives, closed for ten minutes
# from 07:10; a vehicle would learn of it as it entered the world eastbound. The fixture's edges are
# one lane each, so a closure there closes the whole edge, and only an edge no route crosses may be
# closed (check 55); what a closure does to traffic is measured on the Arapahoe dwell's six lanes
# (`test_arapahoe_generator.py`).
CLOSURE = {"id": "incident", "place": "west_out", "lanes": [0], "notify": ["west_gate"],
           "begin": "d0 07:10", "end": "d0 07:20"}
CLOSURE_BEGIN_S, CLOSURE_END_S = 4200.0, 4800.0


def closure_spec(world, **closure) -> dict:
    places = {**world.specification()["places"], "west_out": {"edge": "-900"}}
    return {"places": places, "lane_closures": [{**CLOSURE, **closure}]}


def test_a_lane_closure_compiles_into_an_additional_file_the_configuration_names_and_the_lock_binds(
        world, installation, tmp_path):
    result = compile_spec(world, installation, tmp_path, **closure_spec(world))
    assert not result.refused, [str(f) for f in result.findings.refusals]
    additional = result.files["additional"]
    assert additional.name == "street_layout_probe.add.xml"
    rerouter = ET.parse(additional).getroot().find("rerouter")
    assert (rerouter.get("id"), rerouter.get("edges")) == ("incident", "900")
    interval = rerouter.find("interval")
    assert (float(interval.get("begin")), float(interval.get("end"))) == (CLOSURE_BEGIN_S,
                                                                          CLOSURE_END_S)
    assert [(c.get("id"), c.get("allow")) for c in interval] == [("-900_0", "authority")]
    config = ET.parse(result.files["config"]).getroot()
    assert config.find("input/additional-files").get("value") == additional.name
    locked = result.lock["files"]["additional"]
    assert locked == {"path": additional.name,
                      "sha256": hashlib.sha256(additional.read_bytes()).hexdigest()}
    # The plan is bound to the closures as to the routes: by the file's digest, the lock's own.
    assert result.plan["additional_digest"] == locked["sha256"]
    reported = result.report["lane_closures"][0]
    assert (reported["lanes"], reported["open_lanes"]) == (["-900_0"], 0)
    assert reported["begin"]["civil"] == "2026-03-21T07:10:00-06:00"


def test_a_scenario_without_closures_writes_no_additional_file(world, installation, tmp_path):
    result = compile_spec(world, installation, tmp_path)
    assert "additional" not in result.files and "additional" not in result.lock["files"]
    assert "additional-files" not in result.files["config"].read_text(encoding="utf-8")
    assert not list((tmp_path / "out").glob("*.add.xml"))


def test_sumo_loads_the_compiled_closure_and_runs_to_the_end(world, installation, tmp_path):
    result = compile_spec(world, installation, tmp_path, **closure_spec(world))
    completed = subprocess.run(
        [str(installation.sumo), "-c", str(result.files["config"]), "--no-step-log", "true"],
        capture_output=True, text=True, timeout=300, check=False)
    assert completed.returncode == 0, completed.stderr
    assert "Simulation ended at time: 10800" in completed.stdout


def test_a_closure_breaking_a_route_is_refused_under_check_55(world, installation, tmp_path):
    """Closing the one lane of the ambient flow's destination leaves the flow no connection into it,
    and SUMO, measured, stops the run at the first vehicle inserted during the closure."""
    result = compile_spec(world, installation, tmp_path,
                          **closure_spec(world, place="east_end", notify=["west_gate"]))
    assert 55 in checks(result)
    assert "every lane of 901#1 is closed" in messages(result, 55)
    assert {f.subject for f in result.findings.by_check(55)} >= {"flow ambient", "actor probe"}
    assert not (tmp_path / "out" / "street_layout_probe.add.xml").exists()


@pytest.mark.parametrize(("changes", "check"), [
    ({"lanes": [1]}, 7),
    ({"place": "nowhere"}, 8),
    ({"lanes": [0, 0]}, 53),
    ({"begin": "d0 07:20", "end": "d0 07:10"}, 47),
    ({"end": "d0 09:30"}, 37),
    ({"id": "ambient"}, 54),
])
def test_a_closure_that_cannot_be_applied_as_written_is_refused(world, installation, tmp_path,
                                                                 changes, check):
    result = compile_spec(world, installation, tmp_path, **closure_spec(world, **changes))
    assert check in checks(result)
    assert not (tmp_path / "out" / "street_layout_probe.add.xml").exists()


def test_a_closure_naming_a_lane_its_edge_lacks_says_how_many_it_has(world, installation, tmp_path):
    result = compile_spec(world, installation, tmp_path, **closure_spec(world, lanes=[0, 2]))
    assert "closes lane 2 of -900, which has 1 lane, numbered 0 to 0" in messages(result, 7)


# ---- the SUMO release that routes (check 6) ---------------------------------------------------------

def package_recording_converter(world, tmp_path, converter: str | None) -> dict:
    """The fixture world, its manifest recording `converter` (None: recording none), as a spec."""
    manifest = json.loads(zipfile.ZipFile(world.package).read("world.json"))
    if converter is None:
        manifest.pop("NetconvertVersion", None)
    else:
        manifest["NetconvertVersion"] = converter
    rewrite_package(world.package, tmp_path / "w" / "StreetLayout.cwp",
                    {"world.json": json.dumps(manifest).encode()})
    spec = world.specification()
    spec["world"]["package"] = str(tmp_path / "w" / "StreetLayout.cwp")
    return spec


def test_a_world_converted_by_another_release_is_refused_under_check_6(world, installation,
                                                                       tmp_path):
    """A different duarouter release can route the same demand differently, so it is not the
    world's traffic; the compile stops before anything is routed or written."""
    spec = package_recording_converter(world, tmp_path, "Eclipse SUMO netconvert 1.26.0")
    result = ScenarioCompiler(installation).compile(world.write(spec, "c6.scenario.json"),
                                                    tmp_path / "out")
    assert checks(result) == {6}
    assert "1.26.0" in messages(result, 6) and installation.version in messages(result, 6)
    assert "--allow-sumo-version-mismatch" in messages(result, 6)
    assert set(result.files) == {"resolution", "resolution_md"}
    assert result.report["world"]["routing_sumo"]["release_agreement"] == "Mismatch"


def test_an_accepted_release_mismatch_compiles_warns_and_is_recorded_in_the_lock(
        world, installation, tmp_path):
    spec = package_recording_converter(world, tmp_path, "Eclipse SUMO netconvert 1.26.0")
    result = ScenarioCompiler(installation, allow_sumo_version_mismatch=True).compile(
        world.write(spec, "c6a.scenario.json"), tmp_path / "out")
    assert not result.refused
    assert 6 in checks(result, "warn") and "explicitly accepted" in messages(result, 6)
    routed_by = result.lock["traffic"]["routed_by"]
    assert routed_by == {"tool": "duarouter", "version": installation.version,
                         "world_converter": "Eclipse SUMO netconvert 1.26.0",
                         "release_agreement": "MismatchAccepted", "mismatch_accepted": True}


@pytest.mark.parametrize("converter", ["Eclipse SUMO netconvert 1.27.0", "1.27.0", "v1.27.0"])
def test_the_world_s_release_however_written_compiles_with_no_finding_on_check_6(
        world, installation, tmp_path, converter):
    if installation.version != "1.27.0":
        pytest.skip(f"the staged SUMO is {installation.version}, not the release written here")
    spec = package_recording_converter(world, tmp_path, converter)
    result = ScenarioCompiler(installation).compile(world.write(spec, "c6s.scenario.json"),
                                                    tmp_path / "out")
    assert not result.refused and not result.findings.by_check(6)
    routed_by = result.lock["traffic"]["routed_by"]
    assert routed_by["release_agreement"] == "SameRelease"
    assert routed_by["mismatch_accepted"] is False and routed_by["world_converter"] == converter


def test_a_world_recording_no_converter_compiles_and_warns_under_check_6(world, installation,
                                                                         tmp_path):
    """A package written before the converter was recorded is not evidence of a mismatch."""
    spec = package_recording_converter(world, tmp_path, None)
    result = ScenarioCompiler(installation).compile(world.write(spec, "c6n.scenario.json"),
                                                    tmp_path / "out")
    assert not result.refused
    assert 6 in checks(result, "warn") and "records no converter" in messages(result, 6)
    assert result.lock["traffic"]["routed_by"]["release_agreement"] == "NotRecorded"


def test_an_installation_whose_release_cannot_be_read_is_refused_under_check_6(world, installation,
                                                                               tmp_path):
    """An unreadable release agrees with nothing: it is not evidence of a match."""
    class _Unreadable(type(installation)):
        @property
        def version(self):  # type: ignore[override]
            return None

    unreadable = _Unreadable(home=installation.home, source=installation.source)
    result = ScenarioCompiler(unreadable).compile(
        world.write(world.specification(), "c6u.scenario.json"), tmp_path / "out")
    assert checks(result) == {6}


# ---- the epoch and civil time -----------------------------------------------------------------------

def test_no_epoch_is_refused_under_check_33(world, installation, tmp_path):
    spec = world.specification()
    del spec["epoch"]
    result = ScenarioCompiler(installation).compile(world.write(spec, "c33.scenario.json"),
                                                    tmp_path / "out")
    assert checks(result) == {33}


@pytest.mark.parametrize(("field", "check"), [("epoch", 33), ("illumination", 39)])
def test_the_schema_requires_what_the_compiler_requires_and_leaves_its_absence_to_its_check(
        world, field, check):
    """The published schema says a specification needs an epoch and an illumination default, as
    the compiler does; the compiler's shape check leaves a missing one to check 33 or 39, which
    the two tests beside this one hold to the only finding."""
    spec = world.specification()
    assert ScenarioSchema.validate_against(spec, SCHEMA) == []
    del spec[field]
    assert ScenarioSchema.validate_against(spec, SCHEMA) == [f"$: '{field}' is required"]
    assert ScenarioSchema.validate(spec) == [f"$: '{field}' is required"]
    assert ScenarioSchema.validate(spec, leaving_to_own_checks=True) == []
    assert OWN_CHECKS[field] == check


def test_an_offset_that_is_not_a_quarter_hour_is_refused_under_check_34(world, installation,
                                                                       tmp_path):
    result = compile_spec(world, installation, tmp_path, epoch=dict(EPOCH, utc_offset_hours=-6.1))
    assert 34 in checks(result)


def test_a_missing_or_malformed_illumination_default_is_refused_under_check_39(world, installation,
                                                                              tmp_path):
    spec = world.specification()
    del spec["illumination"]
    result = ScenarioCompiler(installation).compile(world.write(spec, "c39.scenario.json"),
                                                    tmp_path / "out")
    assert checks(result) == {39}
    rate_on_freeze = compile_spec(world, installation, tmp_path / "rate", illumination={
        "illumination_version": 1, "policy": "freeze_at_window_start", "rate_sun_s_per_sim_s": 1.0})
    assert "rate_sun_s_per_sim_s" in messages(rate_on_freeze, 39)


def test_a_clock_alone_on_a_multi_day_run_is_refused_under_check_47(world, installation, tmp_path):
    result = compile_spec(world, installation, tmp_path,
                          simulation={"end": "d2 00:00", "step_length_s": 0.05},
                          instants={"probe_time": "07:00"})
    assert 47 in checks(result) and "d0 07:00" in messages(result, 47)


def test_a_departure_outside_the_run_is_refused_under_check_37(world, installation, tmp_path):
    spec = world.specification()
    spec["actors"][1]["depart"] = "d0 09:30"
    result = ScenarioCompiler(installation).compile(world.write(spec, "c37.scenario.json"),
                                                    tmp_path / "out")
    assert 37 in checks(result) and "after the run ends" in messages(result, 37)


def test_a_skip_that_plants_nothing_is_refused_under_check_48(world, installation, tmp_path):
    spec = world.specification()
    spec["rotas"][0]["skip"][0]["at"] = "07:15"
    result = ScenarioCompiler(installation).compile(world.write(spec, "c48.scenario.json"),
                                                    tmp_path / "out")
    assert 48 in checks(result)


# ---- references and places --------------------------------------------------------------------------

def test_an_ambiguous_street_is_refused_with_its_candidates_under_check_7(world, installation,
                                                                        tmp_path):
    spec = world.specification()
    spec["places"]["east_before_cross"] = {"street": "East Street", "direction": "east"}
    result = ScenarioCompiler(installation).compile(world.write(spec, "c7.scenario.json"),
                                                    tmp_path / "out")
    assert 7 in checks(result)
    assert "901#0" in messages(result, 7) and "901#1" in messages(result, 7)


def test_an_unknown_edge_is_refused_with_the_nearest_names_under_check_7(world, installation,
                                                                       tmp_path):
    spec = world.specification()
    spec["places"]["west_gate"] = {"edge": "9000"}
    result = ScenarioCompiler(installation).compile(world.write(spec, "c7b.scenario.json"),
                                                    tmp_path / "out")
    assert 7 in checks(result) and "nearest" in messages(result, 7)


def test_an_undeclared_place_or_instant_is_refused_under_check_8(world, installation, tmp_path):
    spec = world.specification()
    spec["actors"][1]["to"] = "nowhere"
    spec["actors"][0]["depart"] = {"instant": "never"}
    result = ScenarioCompiler(installation).compile(world.write(spec, "c8.scenario.json"),
                                                    tmp_path / "out")
    assert 8 in checks(result)
    assert "nowhere" in messages(result, 8) and "never" in messages(result, 8)


def test_a_stop_beyond_its_lane_is_refused_under_check_9(world, installation, tmp_path):
    spec = world.specification()
    spec["places"]["kerb"] = {"lane": "901#0_0", "offset_m": 500.0}
    result = ScenarioCompiler(installation).compile(world.write(spec, "c9.scenario.json"),
                                                    tmp_path / "out")
    assert 9 in checks(result)


def test_a_class_barred_from_an_edge_is_refused_under_check_10(world, installation, tmp_path):
    spec = world.specification()
    spec["vehicle_classes"].append({"class_id": "tramcar", "blueprints": ["vehicle.mini.cooper",
                                                                         "vehicle.lincoln.mkz"],
                                    "sumo_vclass": "tram", "share": 0.0})
    spec["actors"][1]["type"] = "tramcar"
    result = ScenarioCompiler(installation).compile(world.write(spec, "c10.scenario.json"),
                                                    tmp_path / "out")
    assert 10 in checks(result) and "tram" in messages(result, 10)


def test_an_explicit_route_with_a_gap_is_refused_under_check_13(world, installation, tmp_path):
    spec = world.specification()
    spec["places"]["cross_north_in"] = {"edge": "-902#1"}
    spec["actors"][1] = {"id": "hauler", "type": "car", "depart": "d0 07:30",
                         "route": ["west_gate", "cross_south"]}
    result = ScenarioCompiler(installation).compile(world.write(spec, "c13.scenario.json"),
                                                    tmp_path / "out")
    assert 13 in checks(result) and "900 to 902#1" in messages(result, 13)


def test_a_duplicate_vehicle_id_is_refused_under_check_54(world, installation, tmp_path):
    spec = world.specification()
    spec["actors"][1]["id"] = "patrol_d0_h6"
    result = ScenarioCompiler(installation).compile(world.write(spec, "c54.scenario.json"),
                                                    tmp_path / "out")
    assert 54 in checks(result)


# ---- vehicles ----------------------------------------------------------------------------------------

def test_a_body_the_catalogue_did_not_measure_is_refused_under_check_14(world, installation,
                                                                       tmp_path):
    spec = world.specification()
    spec["vehicle_classes"][0]["blueprints"].append("vehicle.harley.lowrider")
    result = ScenarioCompiler(installation).compile(world.write(spec, "c14.scenario.json"),
                                                    tmp_path / "out")
    assert 14 in checks(result)


def test_an_undeclared_type_is_refused_under_check_16(world, installation, tmp_path):
    spec = world.specification()
    spec["actors"][1]["type"] = "pickup"
    result = ScenarioCompiler(installation).compile(world.write(spec, "c16.scenario.json"),
                                                    tmp_path / "out")
    assert 16 in checks(result)


def test_a_missing_body_leaves_every_other_refusal_of_its_stage_reported(world, installation,
                                                                         tmp_path):
    """What a type may drive is the declaration's, so a body the catalogue lacks does not stop the
    type and road checks: one compile names the missing body, the barred road and the unknown type."""
    spec = world.specification()
    spec["vehicle_classes"][0]["blueprints"].append("vehicle.harley.lowrider")
    spec["vehicle_classes"].append({"class_id": "tramcar", "blueprints": ["vehicle.mini.cooper"],
                                    "sumo_vclass": "tram", "share": 0.0})
    spec["actors"][1]["type"] = "tramcar"
    spec["actors"][0]["type"] = "pickup"
    result = ScenarioCompiler(installation).compile(world.write(spec, "c14b.scenario.json"),
                                                    tmp_path / "out")
    assert {14, 10, 16} <= checks(result)
    assert "vehicle.harley.lowrider" in messages(result, 14) and "tram" in messages(result, 10)


def named_mix_specification(world, shares: dict) -> dict:
    spec = world.specification()
    spec["vehicle_classes"].append({"class_id": "tramcar", "blueprints": ["vehicle.mini.cooper"],
                                    "sumo_vclass": "tram", "share": 0.0})
    spec["vehicle_mixes"] = [{"id": "street_mix", "shares": shares,
                              "note": "the kerb street's own population"}]
    spec["flows"][0]["type"] = "street_mix"
    return spec


def test_a_named_mix_is_written_as_its_own_distribution_and_a_flow_draws_from_it(
        world, installation, tmp_path):
    spec = named_mix_specification(world, {"car": 3.0, "saloon": 1.0})
    result = ScenarioCompiler(installation).compile(world.write(spec, "mix.scenario.json"),
                                                    tmp_path / "out")
    assert not result.refused, [str(f) for f in result.findings.refusals]
    root = ET.parse(result.files["routes"]).getroot()
    (mix,) = [d for d in root.iter("vTypeDistribution") if d.get("id") == "street_mix"]
    drawn = dict(zip(mix.get("vTypes").split(), map(float, mix.get("probabilities").split()),
                     strict=True))
    assert drawn == pytest.approx({"car.vehicle.lincoln.mkz": 0.25,
                                   "car.vehicle.dodge.charger": 0.25,
                                   "car.vehicle.mini.cooper": 0.25,
                                   "saloon.vehicle.lincoln.mkz": 0.25})
    assert next(e for e in root if e.tag == "flow").get("type") == "street_mix"
    (reported,) = result.report["vehicle_types"]["mixes"]
    assert reported["id"] == "street_mix" and reported["shares"] == {"car": 3.0, "saloon": 1.0}


def test_a_named_mix_drawing_an_undeclared_class_is_refused_under_check_14(world, installation,
                                                                          tmp_path):
    spec = named_mix_specification(world, {"car": 1.0, "lorry": 1.0})
    result = ScenarioCompiler(installation).compile(world.write(spec, "mix14.scenario.json"),
                                                    tmp_path / "out")
    assert 14 in checks(result) and "'lorry', which is not declared" in messages(result, 14)


def test_a_flow_drawing_a_named_mix_is_held_to_every_member_class_s_roads(world, installation,
                                                                         tmp_path):
    spec = named_mix_specification(world, {"car": 1.0, "tramcar": 1.0})
    result = ScenarioCompiler(installation).compile(world.write(spec, "mix10.scenario.json"),
                                                    tmp_path / "out")
    assert 10 in checks(result) and "tram" in messages(result, 10)
    assert "flow ambient" in {f.subject for f in result.findings.by_check(10)}


# ---- routes ----------------------------------------------------------------------------------------

def test_duarouter_false_accept_is_refused_under_check_12(installation, tmp_path):
    network = tmp_path / "net.xml"
    network.write_text(NETWORK, encoding="utf-8")
    findings = CompileFindings()
    requests = [RouteRequest("vehicle", "good", "car", 0.0, "900", "901#1"),
                RouteRequest("vehicle", "typo", "car", 1.0, "900", "NOT_AN_EDGE"),
                RouteRequest("flow", "typo_flow", "car", 0.0, "900", "NOT_AN_EDGE",
                             flow_end=100.0, vehs_per_hour=100.0)]
    result = RouteValidator(installation, findings).route(
        network, '<vType id="car" vClass="passenger"/>', requests, 42)
    assert result.routes == {"good": ("900", "901#0", "901#1")}
    guarded = {f.subject for f in findings.by_check(12)}
    assert guarded == {"vehicle typo", "flow typo_flow"}
    assert "ends on 900" in findings.by_check(12)[0].message


def test_a_request_with_no_route_is_refused_under_check_11(installation, tmp_path):
    network = tmp_path / "net.xml"
    network.write_text(NETWORK, encoding="utf-8")
    findings = CompileFindings()
    requests = [RouteRequest("vehicle", "tram", "tramtype", 0.0, "900", "901#1")]
    RouteValidator(installation, findings).route(
        network, '<vType id="tramtype" vClass="tram"/>', requests, 42)
    assert [f.check_id for f in findings.findings] == [11]


def test_the_guard_checks_via_and_stop_order():
    request = RouteRequest("vehicle", "x", "car", 0.0, "a", "d", via=("c", "b"),
                           stops=(("b_0", 5.0),))
    problems = RouteValidator._guard(request, ("a", "b", "c", "d"))
    assert any("via edge b" in p for p in problems)
    assert RouteValidator._guard(request, ("a", "c", "b", "d")) == []


# ---- supervision ------------------------------------------------------------------------------------

def supervision_with(world, **changes) -> dict:
    block = world.specification()["supervision"]
    block.update(changes)
    return block


def test_a_label_in_an_undeclared_namespace_is_refused_under_check_46(world, installation, tmp_path):
    block = supervision_with(world)
    block["instances"][0]["labels"] = ["elsewhere:standoff"]
    result = compile_spec(world, installation, tmp_path, supervision=block)
    assert 46 in checks(result)


def test_an_undeclared_term_is_refused_under_check_18(world, installation, tmp_path):
    block = supervision_with(world)
    block["instances"][0]["labels"] = ["fixture:loiter"]
    result = compile_spec(world, installation, tmp_path, supervision=block)
    assert 18 in checks(result)


def test_a_term_on_the_wrong_kind_of_subject_is_refused_under_check_45(world, installation,
                                                                      tmp_path):
    vocabulary = vocabulary_with(world, {"fixture:through_traffic": COHORT_TERM})
    block = supervision_with(world)
    block["instances"][0]["labels"] = ["fixture:through_traffic"]
    result = compile_spec(world, installation, tmp_path, supervision=block, vocabulary=vocabulary)
    assert 45 in checks(result)
    assert "applies to ['cohort'], and is attached to a entity" in messages(result, 45)


def test_a_participant_that_is_not_an_actor_is_refused_under_check_19(world, installation, tmp_path):
    block = supervision_with(world)
    block["instances"][0]["participants"] = [{"actor": "ambient", "role": "subject"}]
    result = compile_spec(world, installation, tmp_path, supervision=block)
    assert 19 in checks(result) and "cohort" in messages(result, 19)


def test_an_unknown_area_is_refused_under_check_20(world, installation, tmp_path):
    block = supervision_with(world)
    block["instances"][0]["aoi_refs"] = ["drydock"]
    result = compile_spec(world, installation, tmp_path, supervision=block)
    assert 20 in checks(result)


def test_a_duplicate_instance_is_refused_under_check_21(world, installation, tmp_path):
    block = supervision_with(world)
    block["instances"].append(dict(block["instances"][1]))
    result = compile_spec(world, installation, tmp_path, supervision=block)
    assert 21 in checks(result)


def test_an_interval_before_its_vehicle_departs_is_warned_under_check_22(world, installation,
                                                                        tmp_path):
    block = supervision_with(world)
    block["instances"][0]["intervals"][0]["begin"] = "d0 06:50"
    result = compile_spec(world, installation, tmp_path, supervision=block,
                          capture_windows=[{"id": "later", "begin": "d0 07:30", "length": "15m"}])
    assert not result.refused and 22 in checks(result, "warn")


def test_a_cohort_with_an_interval_or_nominal_is_refused_under_checks_23_and_49(world, installation,
                                                                             tmp_path):
    block = supervision_with(world, cohorts=[{"flow": "ambient", "supervision": "nominal",
                                              "intervals": [{"phase": "x"}]}])
    result = compile_spec(world, installation, tmp_path, supervision=block)
    assert {23, 49} <= checks(result)


def test_the_reserved_role_is_enforced_under_check_50(world, installation, tmp_path):
    block = supervision_with(world)
    block["instances"][0]["participants"][0]["role"] = "fixture:patroller"
    result = compile_spec(world, installation, tmp_path, supervision=block)
    assert 50 in checks(result)
    assert "subject" in messages(result, 50)


def test_an_instance_with_no_participant_is_refused_under_check_19(world, installation, tmp_path):
    """A label follows a vehicle (06 §3.5): an instance about no vehicle is refused, and told how an
    omission is conveyed instead."""
    block = supervision_with(world)
    block["instances"][0]["participants"] = []
    block["instances"][0]["intervals"] = []
    result = compile_spec(world, installation, tmp_path, supervision=block)
    assert 19 in checks(result)
    assert "has no participant" in messages(result, 19)
    assert "labeling the vehicle that deviates" in messages(result, 19)
    assert "scenario-level note" not in messages(result, 19)


def test_the_absence_shape_is_refused_by_the_schema_under_check_53(world, installation, tmp_path):
    """A specification written to the shape before the ruling of 2026-10-05 -- an `absences` block, a
    term that applies to a `slot`, a term declaring a `realisation` -- is refused at the schema, naming
    each field, rather than read as a place's label."""
    block = supervision_with(world, absences=[{"name": "patrol_missed_d0_h8", "series": "kerb_patrol",
                                               "entry": "patrol_d0_h8",
                                               "labels": ["fixture:routine_patrol"]}])
    result = compile_spec(world, installation, tmp_path / "a", supervision=block)
    assert checks(result) == {53} and "$.supervision: 'absences' is not a field here" in messages(result, 53)

    vocabulary = vocabulary_with(world, {"fixture:patrol_missed": {
        "definition": "A scheduled patrol that never came.", "applies_to": ["slot"],
        "realisation": ["absent"], "since": 1, "status": "active"}})
    result = compile_spec(world, installation, tmp_path / "b", vocabulary=vocabulary)
    assert checks(result) == {53}
    assert "applies_to" in messages(result, 53) and "realisation" in messages(result, 53)


def test_a_skipped_occasion_writes_no_trip_and_no_row(world, installation, tmp_path):
    """The rota's skip stands (check 48 still holds it to an occasion it removes), and what it removes
    is gone from everything: the route file, the series' slots and the plan's entities. The report
    states the skip with its reason, which is where the gap in the schedule is read from."""
    result = compile_spec(world, installation, tmp_path)
    assert not result.refused, [str(f) for f in result.findings.refusals]
    routes = result.files["routes"].read_text(encoding="utf-8")
    assert 'id="patrol_d0_h6"' in routes and 'id="patrol_d0_h8"' not in routes
    plan = result.plan
    assert [slot["slot_key"] for slot in plan["series"][0]["slots"]] == ["patrol_d0_h6"]
    assert "patrol_d0_h8" not in {entity["entity_id"] for entity in plan["entities"]}
    (rota,) = result.report["rotas"]
    assert rota["entries"] == 1
    assert [(skip["entry"], skip["because"]) for skip in rota["skips"]] == \
        [("patrol_d0_h8", "the second patrol does not come")]
    assert "skipped `patrol_d0_h8`" in result.files["resolution_md"].read_text(encoding="utf-8")


def test_annotating_without_any_nominal_subject_is_warned_under_check_24(world, installation,
                                                                        tmp_path):
    block = supervision_with(world)
    block["instances"] = block["instances"][:1]
    block["series"][0]["supervision"] = "unlabelled"
    block["series"][0]["labels"] = []
    result = compile_spec(world, installation, tmp_path, supervision=block)
    assert not result.refused and 24 in checks(result, "warn")


# ---- what a row says, against the terms that define it ----------------------------------------------

STANDOFF_PARAMETERS = {"dwell_s": {"type": "number", "unit": "s", "definition": "the authored halt"},
                       "repeats": {"type": "integer", "definition": "halts at the kerb"}}
COHORT_TERM = {"definition": "Traffic that passes the kerb without stopping.",
               "applies_to": ["cohort"], "since": 1, "status": "active"}


def vocabulary_with(world, terms: dict[str, dict]) -> dict:
    """The fixture vocabulary with each named term updated, or added when it is not there."""
    vocabulary = world.specification()["vocabulary"]
    declared = vocabulary["namespaces"][0]["terms"]
    for spelled, fields in terms.items():
        term = next((t for t in declared if t["term"] == spelled), None)
        if term is None:
            declared.append({"term": spelled, **fields})
        else:
            term.update(fields)
    return vocabulary


def plan_rows(plan: dict) -> dict[str, dict]:
    return {row["instance_id"].split("/", 1)[1]: row for row in plan["instances"]}


def test_parameters_their_label_declares_compile_into_the_plan_as_written(world, installation,
                                                                         tmp_path):
    block = supervision_with(world)
    block["instances"][0]["parameters"] = {"dwell_s": 360, "repeats": 1}
    vocabulary = vocabulary_with(world, {"fixture:standoff": {"parameters": STANDOFF_PARAMETERS}})
    result = compile_spec(world, installation, tmp_path, supervision=block, vocabulary=vocabulary)
    assert not result.refused, [str(f) for f in result.findings.refusals]
    assert plan_rows(result.plan)["probe_standoff"]["parameters"] == {"dwell_s": 360, "repeats": 1}
    assert "parameters dwell_s = 360, repeats = 1" in \
        result.files["resolution_md"].read_text(encoding="utf-8")


@pytest.mark.parametrize(("instance", "parameters", "says"), [
    (0, {"dwell": 360}, "carries parameter 'dwell', which none of its labels declares (they declare "
                        "dwell_s, repeats). A parameter is a key its label's term declares"),
    (0, {"repeats": 1.5}, "parameter 'repeats' is 1.5, and 'fixture:standoff' declares it integer "
                          "(halts at the kerb)"),
    (0, {"dwell_s": "six minutes"}, "parameter 'dwell_s' is \"six minutes\", and 'fixture:standoff' "
                                    "declares it number"),
    (0, {"dwell_s": True}, "parameter 'dwell_s' is true, and 'fixture:standoff' declares it number"),
    (1, {"dwell_s": 360}, "carries parameter 'dwell_s', which none of its labels declares, and it "
                          "carries no label"),
])
def test_a_parameter_no_label_declares_or_of_another_type_is_refused_under_check_56(
        world, installation, tmp_path, instance, parameters, says):
    block = supervision_with(world)
    block["instances"][instance]["parameters"] = parameters
    vocabulary = vocabulary_with(world, {"fixture:standoff": {"parameters": STANDOFF_PARAMETERS}})
    result = compile_spec(world, installation, tmp_path, supervision=block, vocabulary=vocabulary)
    assert checks(result) == {56}
    assert says in messages(result, 56)


def test_a_parameter_that_is_not_a_single_value_is_refused_under_check_53(world, installation,
                                                                         tmp_path):
    block = supervision_with(world)
    block["instances"][0]["parameters"] = {"dwell_s": {"value": 360, "unit": "s"}}
    result = compile_spec(world, installation, tmp_path, supervision=block)
    assert checks(result) == {53}
    assert "$.supervision.instances[0].parameters.dwell_s" in messages(result, 53)


def test_two_labels_declaring_one_key_differently_are_refused_under_check_56(world, installation,
                                                                             tmp_path):
    vocabulary = vocabulary_with(world, {
        "fixture:standoff": {"parameters": STANDOFF_PARAMETERS},
        "fixture:loiter": {"definition": "A vehicle lingers at the kerb.", "applies_to": ["entity"],
                           "since": 1, "status": "active",
                           "parameters": {"dwell_s": {"type": "number", "unit": "min",
                                                      "definition": "the lingering"}}}})
    block = supervision_with(world)
    block["instances"][0]["labels"] = ["fixture:standoff", "fixture:loiter"]
    block["instances"][0]["parameters"] = {"dwell_s": 6}
    result = compile_spec(world, installation, tmp_path, supervision=block, vocabulary=vocabulary)
    assert checks(result) == {56}
    assert ("parameter 'dwell_s' is declared differently by its labels: 'fixture:standoff' as "
            "number in s; 'fixture:loiter' as number in min") in messages(result, 56)


def test_series_and_cohort_rows_carry_the_parameters_their_labels_declare(world, installation,
                                                                         tmp_path):
    vocabulary = vocabulary_with(world, {
        "fixture:routine_patrol": {"parameters": {"halt_s": {
            "type": "number", "unit": "s", "definition": "the authored halt at the kerb"}}},
        "fixture:through_traffic": {**COHORT_TERM, "parameters": {"vehs_per_hour": {
            "type": "number", "unit": "1/h", "definition": "the authored rate"}}}})
    block = supervision_with(world, cohorts=[{"flow": "ambient", "supervision": "annotated",
                                              "labels": ["fixture:through_traffic"],
                                              "parameters": {"vehs_per_hour": 200}}])
    block["series"][0]["parameters"] = {"halt_s": 600}
    result = compile_spec(world, installation, tmp_path, supervision=block, vocabulary=vocabulary)
    assert not result.refused, [str(f) for f in result.findings.refusals]
    assert result.plan["series"][0]["parameters"] == {"halt_s": 600}
    assert result.plan["cohorts"][0]["parameters"] == {"vehs_per_hour": 200}


def test_a_series_or_cohort_parameter_no_label_declares_is_refused_under_check_56(
        world, installation, tmp_path):
    block = supervision_with(world, cohorts=[{"flow": "ambient", "supervision": "unlabelled",
                                              "parameters": {"vehs_per_hour": 200}}])
    block["series"][0]["parameters"] = {"halt_s": 600}
    result = compile_spec(world, installation, tmp_path, supervision=block)
    assert checks(result) == {56}
    assert {f.subject for f in result.findings.by_check(56)} == {"cohort ambient", "series kerb_patrol"}
    assert "'vehs_per_hour', which none of its labels declares, and it carries no label" in \
        messages(result, 56)


def test_a_nominal_subject_carries_its_terms_hard_negative_for_and_no_other_does(world, installation,
                                                                               tmp_path):
    plan = compile_spec(world, installation, tmp_path / "a").plan
    rows = plan_rows(plan)
    assert plan["series"][0]["hard_negative_for"] == ["fixture:standoff"]
    # Nominal with no label to narrow it: unspecified, which is null and not an empty set.
    assert rows["hauler_nominal"]["hard_negative_for"] is None
    assert rows["probe_standoff"]["hard_negative_for"] is None
    block = supervision_with(world)
    block["instances"][1]["labels"] = ["fixture:routine_patrol"]
    labelled = compile_spec(world, installation, tmp_path / "b", supervision=block).plan
    assert plan_rows(labelled)["hauler_nominal"]["hard_negative_for"] == ["fixture:standoff"]


def test_a_subject_restating_its_terms_hard_negative_for_exactly_compiles(world, installation,
                                                                         tmp_path):
    block = supervision_with(world)
    block["series"][0]["hard_negative_for"] = ["fixture:standoff"]
    block["instances"][1]["labels"] = ["fixture:routine_patrol"]
    block["instances"][1]["hard_negative_for"] = ["fixture:standoff"]
    result = compile_spec(world, installation, tmp_path, supervision=block)
    assert not result.refused, [str(f) for f in result.findings.refusals]


@pytest.mark.parametrize(("group", "index", "declared", "subject", "says"), [
    ("series", 0, ["fixture:patrol_missed"], "series kerb_patrol",
     "declares hard_negative_for ['fixture:patrol_missed'], and its labels' terms declare "
     "['fixture:standoff']. The term is the authority"),
    ("instances", 1, ["fixture:standoff"], "instance hauler_nominal",
     "declares hard_negative_for ['fixture:standoff'], and its labels' terms declare none"),
    ("instances", 0, ["fixture:standoff"], "instance probe_standoff",
     "is annotated and declares hard_negative_for ['fixture:standoff']. The field narrows a "
     "nominal subject's negative"),
])
def test_a_hard_negative_for_other_than_the_terms_is_refused_under_check_57(
        world, installation, tmp_path, group, index, declared, subject, says):
    block = supervision_with(world)
    block[group][index]["hard_negative_for"] = declared
    result = compile_spec(world, installation, tmp_path, supervision=block)
    assert checks(result) == {57}
    (finding,) = result.findings.by_check(57)
    assert finding.subject == subject and says in finding.message


def test_a_term_s_exemplars_resolve_to_an_instance_of_the_plan(world, installation, tmp_path):
    vocabulary = vocabulary_with(world, {
        "fixture:standoff": {"exemplar_instances": ["probe_standoff"]},
        "fixture:routine_patrol": {"exemplar_instances": ["hauler_nominal"]}})
    result = compile_spec(world, installation, tmp_path, vocabulary=vocabulary)
    assert not result.refused, [str(f) for f in result.findings.refusals]


@pytest.mark.parametrize(("exemplar", "says"), [
    ("probe_standof", "'fixture:standoff' exemplar_instances names 'probe_standof', which is no "
                      "instance of this scenario. An exemplar resolves or it is prose"),
    ("street_layout_probe/probe_standoff", "an exemplar names the instance as the specification "
                                           "does, 'probe_standoff', not by its id in one plan"),
])
def test_a_dangling_exemplar_is_refused_under_check_8(world, installation, tmp_path, exemplar, says):
    vocabulary = vocabulary_with(world, {"fixture:standoff": {"exemplar_instances": [exemplar]}})
    result = compile_spec(world, installation, tmp_path, vocabulary=vocabulary)
    assert checks(result) == {8}
    (finding,) = result.findings.by_check(8)
    assert finding.subject == "namespace fixture" and says in finding.message


def test_a_term_s_counterfactual_naming_a_subject_must_resolve_under_check_8(world, installation,
                                                                            tmp_path):
    resolving = vocabulary_with(world, {"fixture:standoff": {
        "counterfactual": {"kind": "series", "ref": "kerb_patrol"}}})
    assert not compile_spec(world, installation, tmp_path / "a", vocabulary=resolving).refused
    dangling = vocabulary_with(world, {"fixture:standoff": {
        "counterfactual": {"kind": "series", "ref": "kerb_patrols"}}})
    result = compile_spec(world, installation, tmp_path / "b", vocabulary=dangling)
    assert checks(result) == {8}
    assert "counterfactual series 'kerb_patrols' names nothing this scenario declares" in \
        messages(result, 8)


# ---- intervals anchored to the events of their participant ------------------------------------------

KERB_STOP = {"lane": "901#0_0", "end_pos_m": 51.5}


def anchored(world, anchor: dict, **probe_changes) -> dict:
    """The fixture specification with the probe's standoff interval anchored, and the probe changed."""
    spec = world.specification()
    spec["actors"][0].update(probe_changes)
    spec["supervision"]["instances"][0]["intervals"] = [
        {"participant": "probe", "phase": "wait", "anchor": anchor}]
    return spec


def compile_document(world, installation, tmp_path, spec: dict):
    path = world.write(spec, f"anchor.{tmp_path.name}.scenario.json")
    return ScenarioCompiler(installation).compile(path, tmp_path / "out")


def standoff_interval(result) -> dict:
    return plan_rows(result.plan)["probe_standoff"]["intervals"][0]


def test_an_interval_over_a_duration_stop_declares_its_length_and_no_start(world, installation,
                                                                          tmp_path):
    """06 D6.4: a duration stop declares a length, not a time, so the plan writes no declared
    start or end, the stop's length, and the events that will commit both."""
    result = compile_document(world, installation, tmp_path, anchored(
        world, {"start": "stop:0", "end": "stop_end:0"}))
    assert not result.refused, [str(f) for f in result.findings.refusals]
    assert standoff_interval(result) == {
        "entity_id": "probe", "phase": "wait",
        "anchor": {"start": {"event": "stop:0", **KERB_STOP},
                   "end": {"event": "stop_end:0", **KERB_STOP}},
        "declared_start_s": None, "declared_start_civil": None,
        "declared_end_s": None, "declared_end_civil": None,
        "declared_duration_s": 300.0}
    assert "  - wait: from stop:0 to stop_end:0, 300 s declared" in \
        result.files["resolution_md"].read_text(encoding="utf-8")


def test_an_interval_from_the_departure_declares_the_departure(world, installation, tmp_path):
    result = compile_document(world, installation, tmp_path, anchored(world, {"start": "depart"}))
    assert not result.refused, [str(f) for f in result.findings.refusals]
    interval = standoff_interval(result)
    assert interval["anchor"] == {"start": {"event": "depart"}, "end": None}
    assert (interval["declared_start_s"], interval["declared_start_civil"]) == (
        3600.0, "2026-03-21T07:00:00-06:00")
    assert (interval["declared_end_s"], interval["declared_duration_s"]) == (None, None)


def test_a_stop_held_until_an_instant_declares_its_end(world, installation, tmp_path):
    """An `until` stop declares when the vehicle leaves and not when it arrives: an interval over
    the stop has a declared end and no start; one from the departure to the stop's end has both."""
    until = [{"place": "kerb", "until": "d0 07:20"}]
    over_stop = compile_document(world, installation, tmp_path / "a", anchored(
        world, {"start": "stop:0", "end": "stop_end:0"}, stops=until))
    assert not over_stop.refused, [str(f) for f in over_stop.findings.refusals]
    interval = standoff_interval(over_stop)
    assert (interval["declared_start_s"], interval["declared_end_s"],
            interval["declared_end_civil"], interval["declared_duration_s"]) == (
        None, 4800.0, "2026-03-21T07:20:00-06:00", None)
    spec = anchored(world, {"start": "depart", "end": "stop_end:0"}, stops=until)
    # The fixture's window would cut the declared 07:00 to 07:20 (check 38); capture later.
    spec["capture_windows"] = [{"id": "later", "begin": "d0 08:30", "length": "10m"}]
    whole = compile_document(world, installation, tmp_path / "b", spec)
    assert not whole.refused, [str(f) for f in whole.findings.refusals]
    interval = standoff_interval(whole)
    assert (interval["declared_start_s"], interval["declared_end_s"],
            interval["declared_duration_s"]) == (3600.0, 4800.0, 1200.0)


def test_an_interval_anchored_to_phases_carries_where_each_is_entered_in_the_route(
        world, installation, tmp_path):
    """A phase is one of the actor's declared phases[], entered at its first edge's first pass: the
    plan names that index in the compiled route, which is what SUMO reports a vehicle's progress by."""
    spec = orbit_specification(world)
    spec["vocabulary"] = vocabulary_with(world, {"fixture:circuit": {
        "definition": "A vehicle drives the same closed loop again and again.",
        "applies_to": ["entity"], "since": 1, "status": "active"}})
    spec["supervision"]["instances"].append({
        "name": "orbiter_circuit", "supervision": "annotated", "labels": ["fixture:circuit"],
        "participants": [{"actor": "orbiter", "role": "subject"}],
        "intervals": [{"participant": "orbiter", "phase": "approach",
                       "anchor": {"start": "phase:0", "end": "phase:1"}},
                      {"participant": "orbiter", "phase": "laps",
                       "anchor": {"start": "phase:1", "end": "phase:2"}}]})
    result = compile_document(world, installation, tmp_path, spec)
    assert not result.refused, [str(f) for f in result.findings.refusals]
    approach, laps = plan_rows(result.plan)["orbiter_circuit"]["intervals"]
    assert laps["anchor"] == {"start": {"event": "phase:1", "route_index": 1, "edge": "901#0"},
                              "end": {"event": "phase:2", "route_index": 19, "edge": "901#0"}}
    assert (laps["declared_start_s"], laps["declared_end_s"], laps["declared_duration_s"]) == (
        None, None, None)
    # The first phase is entered where the vehicle is inserted, so it declares the departure.
    assert approach["anchor"]["start"] == {"event": "phase:0", "route_index": 0, "edge": "900"}
    assert approach["declared_start_s"] == 4200.0
    route = orbiter_entry(result).find("route").get("edges").split()
    for end in (*approach["anchor"].values(), *laps["anchor"].values()):
        assert route[end["route_index"]] == end["edge"]


def test_an_unanchored_interval_carries_no_anchor(world, installation, tmp_path):
    rows = plan_rows(compile_spec(world, installation, tmp_path).plan)
    assert rows["probe_standoff"]["intervals"][0]["anchor"] is None
    assert rows["probe_standoff"]["intervals"][0]["declared_start_s"] == 3600.0


@pytest.mark.parametrize(("anchor", "says"), [
    ({"start": "stop:1"}, "interval wait is anchored to 'stop:1', and 'probe' makes 1 stop, "
                          "numbered 0 to 0"),
    ({"start": "phase:0"}, "interval wait is anchored to 'phase:0', and 'probe' declares 0 phases: "
                           "a phase anchor names one of an actor's phases[]"),
    ({"start": "stop_end:0", "end": "stop:0"}, "is anchored from 'stop_end:0' to 'stop:0', and "
                                               "'stop:0' does not come after 'stop_end:0'"),
    ({"start": "depart", "end": "depart"}, "'depart' does not come after 'depart'"),
])
def test_an_anchor_its_participant_cannot_have_is_refused_under_check_58(world, installation,
                                                                         tmp_path, anchor, says):
    result = compile_document(world, installation, tmp_path, anchored(world, anchor))
    assert checks(result) == {58}
    assert says in messages(result, 58)


def test_an_actor_without_stops_has_no_stop_to_anchor_to_under_check_58(world, installation,
                                                                       tmp_path):
    block = supervision_with(world)
    block["instances"][1]["intervals"] = [{"participant": "hauler", "phase": "haul",
                                           "anchor": {"start": "stop:0"}}]
    result = compile_spec(world, installation, tmp_path, supervision=block)
    assert checks(result) == {58}
    assert "'hauler' makes 0 stops" in messages(result, 58)


@pytest.mark.parametrize(("interval", "says"), [
    ({"participant": "probe", "phase": "wait", "begin": "d0 07:00",
      "anchor": {"start": "stop:0", "end": "stop_end:0"}},
     "interval wait gives an anchor and begin. An anchored interval takes its bounds from its "
     "participant's events"),
    ({"participant": "probe", "phase": "wait"},
     "interval wait gives neither a begin nor an anchor"),
])
def test_an_interval_declared_twice_or_not_at_all_is_refused_under_check_58(
        world, installation, tmp_path, interval, says):
    block = supervision_with(world)
    block["instances"][0]["intervals"] = [interval]
    result = compile_spec(world, installation, tmp_path, supervision=block)
    assert checks(result) == {58}
    assert says in messages(result, 58)


def test_an_anchor_on_a_cohort_is_refused_under_check_58(world, installation, tmp_path):
    block = supervision_with(world, cohorts=[{"flow": "ambient", "supervision": "unlabelled",
                                              "intervals": [{"anchor": {"start": "depart"}}]}])
    result = compile_spec(world, installation, tmp_path, supervision=block)
    assert {23, 58} <= checks(result)
    assert "anchors an interval. An anchor names an event of one vehicle" in messages(result, 58)


@pytest.mark.parametrize("event", ["arrive", "stop:-1", "stop:01", "phase", "stop_end"])
def test_an_anchor_event_outside_the_grammar_is_refused_under_check_53(world, installation,
                                                                       tmp_path, event):
    result = compile_document(world, installation, tmp_path, anchored(world, {"start": event}))
    assert checks(result) == {53}


# ---- the dry run: the compiled scenario in SUMO alone ---------------------------------------------

def blocked_entrance(world) -> dict:
    """The fixture with West Street's entrance blocked from 06:50 to the end of the run by a car
    standing 20 m in, so nothing departing there after it gets in: the probe at 07:00 and the hauler
    at 07:30 wait out max-depart-delay, and a nominal late arrival at 08:55 is still waiting when the
    run ends. The patrol at 06:15 enters before the blocker does."""
    spec = world.specification()
    spec["places"]["west_gate_kerb"] = {"lane": "900_0", "offset_m": 20.0}
    spec["actors"] += [
        {"id": "blocker", "type": "saloon", "depart": "d0 06:50", "from": "west_gate",
         "to": "east_end", "stops": [{"place": "west_gate_kerb", "until": "d0 09:00"}]},
        {"id": "late", "type": "car", "depart": "d0 08:55", "from": "west_gate",
         "to": "cross_south"}]
    spec["supervision"]["instances"].append(
        {"name": "late_nominal", "supervision": "nominal", "labels": [],
         "participants": [{"actor": "late", "role": "subject"}]})
    return spec


def test_the_dry_run_finds_every_planned_vehicle_inserted_and_the_lock_records_it(
        world, installation, tmp_path):
    result = compile_spec(world, installation, tmp_path)
    assert not result.refused, [str(f) for f in result.findings.refusals]
    assert result.lock["dry_run"] == {
        "ran": True, "sumo_release": installation.version, "begin_s": 0.0, "end_s": 10800.0,
        "vehicles": {"loaded": 603, "inserted": 603, "discarded": 0, "waiting_at_end": 0},
        "planned_vehicles": {"total": 3, "inserted": 3}, "collisions": 0}
    planned = {row["vehicle_id"]: row for row in result.report["dry_run"]["planned"]}
    assert set(planned) == {"probe", "hauler", "patrol_d0_h6"}
    assert planned["patrol_d0_h6"]["refs"] == ["series kerb_patrol"]
    assert (planned["probe"]["declared_depart_s"], planned["probe"]["depart_s"],
            planned["probe"]["waited_s"]) == (3600.0, 3600.0, 0.0)
    report = result.files["resolution_md"].read_text(encoding="utf-8")
    assert "## Dry run (check 59)" in report
    assert ("| probe | street_layout_probe/probe_standoff | 2026-03-21T07:00:00-06:00 | 3600.0 | "
            "0 |") in report
    assert not (tmp_path / "out" / ".street_layout_probe.dry-run").exists()


def test_a_planned_vehicle_sumo_never_inserts_is_refused_under_check_59(world, installation,
                                                                      tmp_path):
    result = compile_document(world, installation, tmp_path, blocked_entrance(world))
    assert checks(result) == {59}
    refusals = {f.subject: f.message for f in result.findings.by_check(59)}
    assert set(refusals) == {"actor probe", "actor hauler", "actor late"}
    assert ("'probe', named by street_layout_probe/probe_standoff, is declared to depart at "
            "2026-03-21T07:00:00-06:00 (3600 s) and never enters the run: it waited 900 s to enter "
            "at edge 900, and SUMO discarded it (max-depart-delay 900 s)") in refusals["actor probe"]
    assert ("it was still waiting to enter at edge 900 when the run ended, 300 s after its "
            "departure") in refusals["actor late"]
    run = result.report["dry_run"]
    outcomes = {row["vehicle_id"]: (row["outcome"], row["waited_s"]) for row in run["planned"]}
    assert outcomes == {"patrol_d0_h6": ("inserted", 0.0), "probe": ("discarded", 900.0),
                        "hauler": ("discarded", 900.0), "late": ("waiting_at_end", 300.0)}
    assert run["other_vehicles_discarded"] == run["vehicles"]["discarded"] - 2 > 0
    assert run["other_vehicles_waiting_at_end"] == run["vehicles"]["waiting_at_end"] - 1
    # Refused: only the report is written, and the dry run's scratch is gone.
    assert not result.lock and "lock" not in result.files
    assert sorted(p.name for p in (tmp_path / "out").iterdir()) == [
        "street_layout_probe.resolution.json", "street_layout_probe.resolution.md"]


def test_a_refused_dry_run_leaves_an_earlier_package_as_it_was(world, installation, tmp_path):
    """The run reads its own copy of the files, so a refusal after it changes nothing on disk."""
    first = compile_document(world, installation, tmp_path, world.specification())
    assert not first.refused
    before = {path.name: path.read_bytes() for path in (tmp_path / "out").iterdir()
              if "resolution" not in path.name}
    second = compile_document(world, installation, tmp_path, blocked_entrance(world))
    assert 59 in checks(second)
    after = {path.name: path.read_bytes() for path in (tmp_path / "out").iterdir()
             if "resolution" not in path.name}
    assert after == before


def test_the_dry_run_skipped_for_a_draft_is_recorded_in_the_lock(world, installation, tmp_path):
    spec = world.write(blocked_entrance(world), f"skip.{tmp_path.name}.scenario.json")
    result = ScenarioCompiler(installation, skip_dry_run=True).compile(spec, tmp_path / "out")
    assert not result.refused, [str(f) for f in result.findings.refusals]
    assert result.lock["dry_run"] == {"ran": False, "reason": SKIPPED_DRY_RUN}
    assert f"## Dry run\n\nNot run: {SKIPPED_DRY_RUN}." in \
        result.files["resolution_md"].read_text(encoding="utf-8")


def test_every_collision_sumo_registers_is_read_and_reported(tmp_path):
    """Collisions are as deterministic as insertions and never refuse; each is read from SUMO's
    collision output and listed in the report with its civil time."""
    written = tmp_path / "collisions.xml"
    written.write_text(
        '<collisions><collision time="1234.50" type="collision" lane="900_0" pos="42.10" '
        'collider="ambient.7" victim="probe" colliderType="car" victimType="saloon" '
        'colliderSpeed="9.1" victimSpeed="0.0"/></collisions>', encoding="utf-8")
    (collision,) = SumoDryRun._read_collisions(written, lambda seconds: f"civil {seconds:g}")
    assert collision == {"time_s": 1234.5, "civil": "civil 1234.5", "type": "collision",
                         "collider": "ambient.7", "victim": "probe", "lane": "900_0",
                         "pos_m": 42.1}
    report = ResolutionReport()
    report.set("dry_run", {
        "ran": True, "sumo_release": "1.27.0", "begin_s": 0.0, "end_s": 3600.0,
        "vehicles": {"loaded": 2, "inserted": 2, "discarded": 0, "waiting_at_end": 0},
        "planned_vehicles": {"total": 0, "inserted": 0}, "collisions": 1, "teleports": 0,
        "emergency_stops": 0, "emergency_braking": 0, "other_vehicles_discarded": 0,
        "other_vehicles_waiting_at_end": 0, "planned": [], "collision_list": [collision]})
    assert "| civil 1234.5 (1234.5 s) | collision | ambient.7 | probe | 900_0 |" in \
        report.to_markdown()


# ---- capture windows and the sun --------------------------------------------------------------------

def test_a_window_cutting_an_interval_is_refused_under_check_38(world, installation, tmp_path):
    result = compile_spec(world, installation, tmp_path,
                          capture_windows=[{"id": "cut", "begin": "d0 07:03", "length": "10m"}])
    assert 38 in checks(result) and "probe_standoff" in messages(result, 38)


def test_a_window_past_the_run_is_refused_under_check_38(world, installation, tmp_path):
    result = compile_spec(world, installation, tmp_path,
                          capture_windows=[{"id": "late", "begin": "d0 08:50", "length": "30m"}])
    assert 38 in checks(result)


def test_a_window_s_lowest_sun_is_stated_under_check_42_and_nothing_is_concluded(world, installation,
                                                                                  tmp_path):
    """Check 42 states the lowest the sun reaches over a window and the band it falls in, and makes
    no finding of it: a fact in the report, with no pass mark (the charter's §4b)."""
    result = compile_spec(world, installation, tmp_path,
                          capture_windows=[{"id": "dark", "begin": "d0 06:00", "length": "5m"},
                                           {"id": "bright", "begin": "d0 07:00", "length": "15m"}])
    assert not result.refused
    assert 42 not in checks(result) and 42 not in checks(result, "warn")
    dark, bright = result.report["capture_windows"]
    assert dark["sun_lowest"]["elevation_deg"] == min(dark["sun_open"]["elevation_deg"],
                                                      dark["sun_close"]["elevation_deg"])
    assert dark["sun_lowest"]["elevation_deg"] < -6.0
    assert dark["sun_lowest"]["band"] in ("nautical_twilight", "astronomical_twilight", "night")
    assert bright["sun_lowest"]["elevation_deg"] > dark["sun_lowest"]["elevation_deg"]
    assert bright["sun_lowest"]["band"] in IlluminationBand.names()
    assert dark["sun_lowest"]["elevation_kind"] == "refraction_corrected"
    report = result.files["resolution_md"].read_text(encoding="utf-8")
    for word in ("eligible", "usable", "night window"):
        assert word not in report


def test_an_advancing_default_names_the_arc_under_check_39(world, installation, tmp_path):
    result = compile_spec(world, installation, tmp_path, illumination={
        "illumination_version": 1, "policy": "advance", "rate_sun_s_per_sim_s": 1.0})
    assert not result.refused and 39 in checks(result, "warn")
    window = result.report["capture_windows"][0]
    assert window["sun_close"]["elevation_deg"] > window["sun_open"]["elevation_deg"]


def test_a_held_calendar_warns_when_a_window_falls_on_another_date_under_check_36(
        world, installation, tmp_path):
    held = dict(EPOCH, calendar_advances=False)
    result = compile_spec(world, installation, tmp_path, epoch=held,
                          simulation={"end": "d2 00:00", "step_length_s": 0.05},
                          capture_windows=[{"id": "next_day", "begin": "d1 07:00", "length": "15m"}])
    assert not result.refused and 36 in checks(result, "warn")
    window = result.report["capture_windows"][0]
    assert (window["civil_date"], window["sun_open"]["sun_date"]) == ("2026-03-22", "2026-03-21")


def test_an_offset_far_from_the_world_warns_under_check_40(world, installation, tmp_path):
    iran = {"epoch_version": 1, "civil_datetime": "2026-03-21T06:00:00+03:30",
            "utc_offset_hours": 3.5, "utc_datetime": "2026-03-21T02:30:00Z",
            "calendar_advances": True, "dst_in_effect": False}
    result = compile_spec(world, installation, tmp_path, epoch=iran)
    assert 40 in checks(result, "warn")


def test_a_flow_past_the_run_warns_under_check_32_and_a_long_stop_under_check_31(
        world, installation, tmp_path):
    spec = world.specification()
    spec["flows"][0]["end"] = "d0 10:00"
    spec["actors"][1]["stops"] = [{"place": "kerb", "duration": "3h"}]
    spec["actors"][1]["to"] = "east_end"
    result = ScenarioCompiler(installation).compile(world.write(spec, "c31.scenario.json"),
                                                    tmp_path / "out")
    assert {31, 32} <= checks(result, "warn")


# ---- emission ----------------------------------------------------------------------------------------

def test_a_route_file_sumo_would_not_load_is_refused_under_check_51(world, installation, tmp_path):
    spec = world.specification()
    spec["actors"][1]["depart_lane"] = "sideways"
    result = ScenarioCompiler(installation).compile(world.write(spec, "c51.scenario.json"),
                                                    tmp_path / "out")
    assert 51 in checks(result)
    assert not (tmp_path / "out" / "street_layout_probe.rou.xml").exists()


def test_a_name_with_a_double_hyphen_is_escaped_rather_than_breaking_the_file(world, installation,
                                                                            tmp_path):
    result = compile_spec(world, installation, tmp_path, scenario_name="probe -- kerb")
    assert not result.refused
    assert "--" not in "".join(
        part for part in result.files["routes"].read_text(encoding="utf-8").split("<!--")[1:]
        for part in [part.split("-->")[0]])


@pytest.mark.parametrize(("routes", "check"), [
    ('<routes><vehicle id="b" depart="9.00"><route edges="900"/></vehicle>'
     '<vehicle id="a" depart="1.00"><route edges="900"/></vehicle></routes>', 29),
    ('<routes><!-- a -- b --><vehicle id="a" depart="1.00"><route edges="900"/></vehicle></routes>',
     30),
    ('<routes><vehicle id="a" depart="7:00:00"><route edges="900"/></vehicle></routes>', 44),
    ('<routes><vType id="t"><param key="capture:instance_id" value="x"/></vType></routes>', 52),
])
def test_the_self_checks_refuse_a_defective_route_file(installation, tmp_path, routes, check):
    compiler = ScenarioCompiler(installation)
    compiler.findings = CompileFindings()
    compiler._self_check(routes, "<configuration/>")
    assert check in {f.check_id for f in compiler.findings.refusals}


def test_the_compiler_and_its_cli_agree(world, installation, tmp_path):
    spec = world.write(world.specification(), "cli.scenario.json")
    script = _REPO / "CarlaControl" / "scripts" / "compile_scenario.py"
    completed = subprocess.run([sys.executable, str(script), str(spec), "--out-dir",
                                str(tmp_path / "out"), "--sumo-home", str(installation.home)],
                               capture_output=True, text=True, timeout=600, check=False)
    assert completed.returncode == 0, completed.stderr[-2000:]
    assert (tmp_path / "out" / "street_layout_probe.lock.json").exists()
    shutil.rmtree(tmp_path / "out")


# ---- assertions the mutation runs showed were missing -------------------------------------------

def test_a_street_narrowed_with_at_names_the_edge_arriving_at_the_cross_street(world, installation,
                                                                             tmp_path):
    report = compile_spec(world, installation, tmp_path).report
    assert report["places"]["east_before_cross"]["edges"] == ["901#0"]


def test_an_actor_nobody_supervises_is_written_unlabelled(world, installation, tmp_path):
    block = supervision_with(world)
    block["instances"] = block["instances"][:1]
    plan = compile_spec(world, installation, tmp_path, supervision=block).plan
    assert {row["entity_id"]: row["supervision"] for row in plan["entities"]}["hauler"] == \
        ["unlabelled"]


def test_an_undeclared_role_in_a_declared_namespace_is_refused_under_check_18(world, installation,
                                                                            tmp_path):
    block = supervision_with(world)
    block["series"][0]["member_role"] = "fixture:guard"
    result = compile_spec(world, installation, tmp_path, supervision=block)
    assert 18 in checks(result) and "fixture:guard" in messages(result, 18)


def test_an_interval_ending_after_the_run_is_warned_under_check_31(world, installation, tmp_path):
    block = supervision_with(world)
    block["instances"][0]["intervals"][0]["end"] = "d0 09:30"
    result = compile_spec(world, installation, tmp_path, supervision=block,
                          capture_windows=[{"id": "later", "begin": "d0 06:30", "length": "15m"}])
    assert not result.refused and "scenario_end" in messages(result, 31)
