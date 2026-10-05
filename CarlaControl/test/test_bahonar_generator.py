"""The Shahid Bahonar pattern of life: what `Import/` carries is what its generator writes today, and the
generator reproduces the sizing scenario it replaced, entry for entry, at the same local times.

`make_bahonar_scenario.py` writes the scenario as a specification and compiles it against the world
package (`07_Scenario_Authoring.md` §3.4). Held here:

* **`Import/` is the generator's output.** Rerun against the same world package and catalogue, the
  generator writes the same specification and reaches the same outcome: while the catalogue lacks a
  body the scenario names, a refusal whose every finding is check 14 naming such a body; once it has
  them all, the same scenario files byte for byte and the same lock but for the specification's
  digest, as the Gardnerville canary holds (07 §8.5).
* **Every stage after the bodies passes.** With only the unmeasured blueprints swapped for measured
  ones, the specification compiles against the world package and the session's own network check
  admits the result, so the refusal above is the whole of what stands between the scenario and a run.
* **The sizing scenario survives.** `fixtures/Shahid_Bahonar_Port_PatternOfLife.shipped.rou.xml` and
  `.shipped.labels.json` are the route file and labels of `BahonarPatternOfLife.zip` (members sha256
  `af542454...` and `db3e5060...`, line endings here normalised to LF), written by the SUMO-XML
  generator this one replaced, whose `t = 0` was midnight of day 0 (07 §1.4). Under the 07:00 epoch every entry of it that falls in the run --
  the 335 guard postings, the 21 hauls, the nine planted vehicles and every flow window -- comes back
  with the same id, the same roads and stops and the same local time; what it held before 07:00 on
  day 0 is gone and what the run adds is day 7 before 07:00.
* **The six anomalies are supervision.** The nine planted vehicles are the participants of the five
  annotated instances, and the guard no-show is an absence in the guard rota's series, at the tower,
  instant and length the labels' described gap gave it.
* **The plan says nothing its terms do not define.** The perimeter shadow's `speed_factor` and
  `circuit_edges` are the keys its term declares, of the declared types, and a value of another type
  is refused (check 56); the hauls and the guard postings carry their terms' `hard_negative_for`.

What these cannot see: whether the traffic behaves as intended in SUMO -- that is measured when the
scenario changes (07 §3.4) -- and whether a body suits the vehicle it is drawn for, which is the
owner's choice.
"""
from __future__ import annotations

import importlib.util
import json
import os
import subprocess
import sys
import xml.etree.ElementTree as ET
from datetime import datetime
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

import carlanet  # noqa: E402, F401  -- loads the CarlaNet assemblies the next import names
from CarlaNet.CoSim import (  # noqa: E402
    RouteErrorCheck,
    ScenarioLockCheck,
    ScenarioNetworkCheck,
    SolarEpoch,
    TeleportingCheck,
)
from CarlaNet.CoSim import VehicleCatalogue as SessionCatalogue  # noqa: E402

from carlacontrol.CivilTimeResolver import CivilTimeResolver  # noqa: E402
from carlacontrol.CompileFindings import CompileFindings  # noqa: E402
from carlacontrol.NetworkFingerprint import NetworkFingerprint  # noqa: E402
from carlacontrol.RotaExpander import RotaExpander  # noqa: E402
from carlacontrol.ScenarioCompiler import ScenarioCompiler  # noqa: E402
from carlacontrol.ScenarioEpoch import ScenarioEpoch  # noqa: E402
from carlacontrol.SumoInstallation import SumoInstallation  # noqa: E402
from carlacontrol.VehicleCatalogue import VehicleCatalogue  # noqa: E402
from carlacontrol.WorldPackageReader import WorldPackageReader  # noqa: E402

GENERATOR = _REPO / "CarlaControl" / "scripts" / "make_bahonar_scenario.py"
PACKAGE = _REPO / "Build" / "world-packages" / "Shahid_Bahonar_Port.cwp"
STAGED_SUMO = _REPO / "Build" / "sumo-install"
CATALOGUE = _REPO / "CarlaControl" / "catalogue" / "vehicles.catalogue.json"
IMPORT = _REPO / "Import"
FIXTURES = Path(__file__).resolve().parent / "fixtures"
SCENARIO = "Shahid_Bahonar_Port_PatternOfLife"
BYTE_FOR_BYTE = (f"{SCENARIO}.rou.xml", f"{SCENARIO}.sumocfg", f"{SCENARIO}.supervision.json")
# The network is the world package's, copied beside the scenario: its bytes carry netconvert's
# "generated on" stamp from the world build, so rebuilding the world changes them while the
# network stays the same. It is compared by its canonical fingerprint, as the session compares it.
NETWORK = "Shahid_Bahonar_Port.net.xml"
SHIPPED_ROUTES = FIXTURES / f"{SCENARIO}.shipped.rou.xml"
SHIPPED_LABELS = FIXTURES / f"{SCENARIO}.shipped.labels.json"
SHIPPED_END_S = 7 * 86_400


def load_generator():
    spec = importlib.util.spec_from_file_location("make_bahonar_scenario", GENERATOR)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def generated_specification(**changes) -> dict:
    """The specification the generator writes with its defaults, bound to no package."""
    generator = load_generator()
    arguments = {"days": 7, "no_show_day": 4, "no_show_hour": 7, "no_show_tower": 3,
                 "step_length_s": 1.0, "seed": 42}
    arguments.update(changes)
    return generator.BahonarPatternOfLifeSpecification(
        VehicleCatalogue.load(CATALOGUE), **arguments).build(
        PACKAGE, "0" * 64, CATALOGUE, IMPORT)


def require_world() -> None:
    if not PACKAGE.exists() or not STAGED_SUMO.exists():
        pytest.skip("the Shahid Bahonar world package or the staged SUMO is not here")


# ---- the scenario in Import/ ---------------------------------------------------------------------------

def test_the_shipped_scenario_is_what_its_generator_writes_today(tmp_path):
    require_world()
    if not (IMPORT / f"{SCENARIO}.scenario.json").exists():
        pytest.fail(f"Import/ carries no {SCENARIO} specification: run make_bahonar_scenario.py")
    done = subprocess.run([sys.executable, str(GENERATOR), "--out-dir", str(tmp_path)],
                          capture_output=True, text=True, env=dict(os.environ), timeout=600)
    specification = json.loads((tmp_path / f"{SCENARIO}.scenario.json").read_text(encoding="utf-8"))
    shipped_specification = json.loads((IMPORT / f"{SCENARIO}.scenario.json").read_text(
        encoding="utf-8"))
    for spec in (specification, shipped_specification):
        spec["world"].pop("package")
        spec.pop("catalogue")
    assert specification == shipped_specification

    if (IMPORT / f"{SCENARIO}.lock.json").exists():
        assert done.returncode == 0, done.stdout[-3000:] + done.stderr[-3000:]
        for name in BYTE_FOR_BYTE:
            assert (tmp_path / name).read_bytes() == (IMPORT / name).read_bytes(), name
        assert (NetworkFingerprint.of_file(tmp_path / NETWORK)
                == NetworkFingerprint.of_file(IMPORT / NETWORK)), NETWORK
        shipped = json.loads((IMPORT / f"{SCENARIO}.lock.json").read_text(encoding="utf-8"))
        regenerated = json.loads((tmp_path / f"{SCENARIO}.lock.json").read_text(encoding="utf-8"))
        for lock in (shipped, regenerated):
            lock.pop("specification_sha256")
            lock["files"]["network"].pop("sha256")
        assert regenerated == shipped
        return

    # Refused: the report says so, the regenerated one agrees, and every refusal is a body the
    # catalogue has not measured.
    assert done.returncode == 1, done.stdout[-3000:] + done.stderr[-3000:]
    assert not (tmp_path / f"{SCENARIO}.lock.json").exists()
    shipped = json.loads((IMPORT / f"{SCENARIO}.resolution.json").read_text(encoding="utf-8"))
    regenerated = json.loads((tmp_path / f"{SCENARIO}.resolution.json").read_text(encoding="utf-8"))
    for report in (shipped, regenerated):
        report["scenario"].pop("specification_sha256")
    assert regenerated == shipped
    assert shipped["outcome"] == "refused"
    refusals = [f for f in shipped["findings"] if f["outcome"] == "refuse"]
    assert refusals and {f["check"] for f in refusals} == {14}
    measured = set(VehicleCatalogue.load(CATALOGUE).blueprint_ids)
    named = {b for c in shipped_specification["vehicle_classes"] for b in c["blueprints"]}
    missing = sorted(named - measured)
    assert missing
    for finding in refusals:
        for line in finding["message"].splitlines()[1:]:
            assert any(f"'{blueprint}'" in line for blueprint in missing), line


def compilable_specification() -> dict:
    """The generated specification bound to the world package and the catalogue, with any body the
    catalogue has not measured swapped for one it has."""
    measured = VehicleCatalogue.load(CATALOGUE).blueprint_ids
    specification = generated_specification()
    specification["world"] = {"package": str(PACKAGE), "network_fingerprint":
                              NetworkFingerprint.of_text(WorldPackageReader(PACKAGE).network_text())}
    specification["catalogue"] = str(CATALOGUE)
    stand_in = "vehicle.carlacola.actors"
    for entry in specification["vehicle_classes"]:
        entry["blueprints"] = list(dict.fromkeys(
            b if b in measured else stand_in for b in entry["blueprints"]))
    return specification


def compile_specification(specification: dict, tmp_path: Path):
    path = tmp_path / f"{SCENARIO}.scenario.json"
    path.write_text(json.dumps(specification, indent=2), encoding="utf-8")
    return ScenarioCompiler(SumoInstallation.locate(STAGED_SUMO)).compile(path, tmp_path / "out")


def test_with_only_the_unmeasured_bodies_swapped_it_compiles_and_the_session_admits_it(tmp_path):
    """Every stage after the vehicle binding runs on the world package: routes through duarouter,
    supervision, emission and its self-checks; and the session's own checks -- network, compile
    lock, teleporting and route errors -- admit the result, each raising where it would refuse."""
    require_world()
    specification = compilable_specification()
    result = compile_specification(specification, tmp_path)
    assert not result.refused, [str(f) for f in result.findings.refusals]
    routes = ET.parse(result.files["routes"]).getroot()
    entries = [e for e in routes if e.tag in ("vehicle", "flow")]
    assert len(entries) == len(specification["flows"]) + len(specification["actors"]) + 335
    config = str(result.files["config"])
    ScenarioNetworkCheck.Require(config, str(PACKAGE))
    ScenarioLockCheck.Require(config, SessionCatalogue.Load(str(CATALOGUE)),
                              SolarEpoch.FromJson(json.dumps(specification["epoch"])))
    TeleportingCheck.Require(config, False)
    RouteErrorCheck.Require(config)


# ---- the plan, against the terms that define it ------------------------------------------------------

def shipped_plan() -> dict:
    return json.loads((IMPORT / f"{SCENARIO}.supervision.json").read_text(encoding="utf-8"))


def declared_term(term: str) -> dict:
    (namespace,) = load_generator().VOCABULARY["namespaces"]
    return next(t for t in namespace["terms"] if t["term"] == term)


def test_the_shipped_plan_carries_the_shadow_s_parameters_of_the_types_its_term_declares():
    """speed_factor and circuit_edges reach the plan as written, and are what the term defines."""
    shadow = next(i for i in shipped_plan()["instances"]
                  if i["instance_id"] == f"{SCENARIO}/pi_perimeter_shadow_d6")
    assert shadow["labels"] == ["bahonar:perimeter_transit_off_cadence"]
    assert shadow["parameters"] == {"speed_factor": 0.45, "circuit_edges": 7}
    declared = declared_term("bahonar:perimeter_transit_off_cadence")["parameters"]
    assert {key: entry["type"] for key, entry in declared.items()} == {
        "speed_factor": "number", "circuit_edges": "integer"}


@pytest.mark.parametrize(("parameters", "says"), [
    ({"speed_factor": 0.45, "circuit_edges": 7.5},
     "parameter 'circuit_edges' is 7.5, and 'bahonar:perimeter_transit_off_cadence' declares it "
     "integer (fence-line roads driven)"),
    ({"speed_factor": "crawl", "circuit_edges": 7},
     "parameter 'speed_factor' is \"crawl\", and 'bahonar:perimeter_transit_off_cadence' declares "
     "it number"),
    ({"speed_factor": 0.45, "circuit_edges": 7, "laps": 1},
     "carries parameter 'laps', which none of its labels declares (they declare circuit_edges, "
     "speed_factor)"),
])
def test_a_shadow_parameter_its_term_does_not_declare_as_written_is_refused(tmp_path, parameters,
                                                                            says):
    require_world()
    specification = compilable_specification()
    shadow = next(i for i in specification["supervision"]["instances"]
                  if i["name"] == "pi_perimeter_shadow_d6")
    shadow["parameters"] = parameters
    result = compile_specification(specification, tmp_path)
    assert {f.check_id for f in result.findings.refusals} == {56}
    (finding,) = result.findings.by_check(56)
    assert finding.subject == "instance pi_perimeter_shadow_d6" and says in finding.message


def test_the_shipped_plan_projects_hard_negative_for_from_the_terms_onto_its_negatives():
    """The 21 hauls are matched negatives for the escort's terms and the guard postings for the
    dwell-shaped ones, copied from the terms; the anomalies, all annotated, carry none."""
    plan = shipped_plan()
    hauls = [i for i in plan["instances"] if i["supervision"] == "nominal"]
    assert len(hauls) == 21
    assert {tuple(i["hard_negative_for"]) for i in hauls} == {
        tuple(declared_term("bahonar:routine_freight_haul")["hard_negative_for"])}
    (series,) = plan["series"]
    assert series["hard_negative_for"] == declared_term("bahonar:tower_posting")["hard_negative_for"]
    assert all(i["hard_negative_for"] is None for i in plan["instances"]
               if i["supervision"] == "annotated")


def test_the_shipped_anomalies_are_anchored_to_the_events_that_commit_them():
    """Each transit opens at its vehicle's departure, which it declares; each probe's standoff and
    the stay-behind's dwell are their one stop, which declares a length (06 D6.4) or an end."""
    intervals = {i["instance_id"].split("/", 1)[1]: i["intervals"]
                 for i in shipped_plan()["instances"]
                 if i["supervision"] == "annotated" and i["realisation"] == "present"}
    reading = Reading(generated_specification())
    departs = {a["id"]: reading.resolver.instant(a["depart"], a["id"]).seconds
               for a in reading.spec["actors"]}
    for name in ("pi_escort_drydock_d3", "pi_perimeter_shadow_d6"):
        for interval in intervals[name]:
            assert interval["anchor"] == {"start": {"event": "depart"}, "end": None}
            assert interval["declared_start_s"] == departs[interval["entity_id"]]
    for day in (2, 5):
        (standoff,) = intervals[f"pi_gate_probe_d{day}"]
        lane, offset = reading.stop({"place": "port_gate_standoff"})
        stop = {"lane": lane, "end_pos_m": offset}
        assert standoff["anchor"] == {"start": {"event": "stop:0", **stop},
                                      "end": {"event": "stop_end:0", **stop}}
        assert (standoff["declared_start_s"], standoff["declared_end_s"],
                standoff["declared_duration_s"]) == (None, None, 300.0)
    (dwell,) = intervals["pi_ferry_stay_behind_d1"]
    assert dwell["anchor"]["start"]["event"] == "stop:0"
    assert (dwell["declared_start_s"], dwell["declared_end_s"]) == (None, float(SHIPPED_END_S))


def test_a_probe_anchored_to_a_stop_it_does_not_make_is_refused_under_check_58(tmp_path):
    require_world()
    specification = compilable_specification()
    probe = next(i for i in specification["supervision"]["instances"]
                 if i["name"] == "pi_gate_probe_d2")
    probe["intervals"][0]["anchor"] = {"start": "stop:1", "end": "stop_end:1"}
    result = compile_specification(specification, tmp_path)
    assert {f.check_id for f in result.findings.refusals} == {58}
    assert "'probe_d2' makes 1 stop, numbered 0 to 0" in " ".join(
        f.message for f in result.findings.by_check(58))


def test_the_shipped_absence_is_sited_where_the_labels_described_the_gap():
    labels = json.loads(SHIPPED_LABELS.read_text(encoding="utf-8"))
    (gap,) = labels["anomaly_notes"]
    (absence,) = [i for i in shipped_plan()["instances"] if i["realisation"] == "absent"]
    expected = absence["expected"]
    assert expected["site_lane"].rsplit("_", 1)[0] == gap["edge"]
    assert expected["site_pos_m"] == gap["edge_pos_m"]


# ---- the owner's epoch ---------------------------------------------------------------------------------

def test_t0_is_07_00_at_bahonar_and_every_local_time_is_kept():
    specification = generated_specification()
    epoch = specification["epoch"]
    assert (epoch["civil_datetime"], epoch["utc_datetime"], epoch["utc_offset_hours"],
            epoch["time_zone_id"], epoch["calendar_advances"], epoch["dst_in_effect"]) == (
        "2026-09-29T07:00:00+03:30", "2026-09-29T03:30:00Z", 3.5, "Asia/Tehran", True, False)
    resolver = resolver_for(specification)
    assert resolver.instant(specification["simulation"]["end"], "end").civil == \
        "2026-10-06T07:00:00+03:30"
    entries, skips = RotaExpander(resolver, CompileFindings()).expand(
        specification["rotas"][0], specification["place_sets"]["guard_towers"])
    first = next(e for e in entries if e.entry_id == "guard_d0_h7_t0")
    assert (first.depart.seconds, first.depart.civil) == (0.0, "2026-09-29T07:00:00+03:30")
    assert {e.depart.civil[11:19] for e in entries} == {"07:00:00", "15:00:00", "23:00:00"}
    assert [s.depart.civil for s in skips] == ["2026-10-03T07:00:00+03:30"]
    departs = {a["id"]: resolver.instant(a["depart"], a["id"]).civil
               for a in specification["actors"]}
    assert departs["shadow"] == "2026-10-05T02:30:00+03:30"
    assert departs["probe_d2"] == "2026-10-01T11:04:34+03:30"
    assert departs["staybehind"] == "2026-09-30T08:00:00+03:30"
    assert departs["escort_4"] == "2026-10-02T10:00:16+03:30"


# ---- the sizing scenario it replaced ---------------------------------------------------------------------

def resolver_for(specification: dict) -> CivilTimeResolver:
    epoch = ScenarioEpoch.read(specification["epoch"])
    return CivilTimeResolver(epoch, CompileFindings(), instants=specification["instants"],
                             span_end_s=7 * 86_400)


def shipped_entries() -> tuple[dict[str, dict], dict[str, dict]]:
    """The shipped route file's flows and trips, keyed by id, times in its own seconds."""
    flows, trips = {}, {}
    for element in ET.parse(SHIPPED_ROUTES).getroot():
        if element.tag == "flow":
            flows[element.get("id")] = {
                "type": element.get("type"), "begin": float(element.get("begin")),
                "end": float(element.get("end")), "rate": float(element.get("vehsPerHour")),
                "from": element.get("from"), "to": element.get("to")}
        elif element.tag == "trip":
            trips[element.get("id")] = {
                "type": element.get("type"), "depart": float(element.get("depart")),
                "from": element.get("from"), "to": element.get("to"),
                "via": (element.get("via") or "").split(),
                "stops": [(s.get("lane"), float(s.get("endPos")), float(s.get("duration")),
                           s.get("parking") == "true") for s in element.findall("stop")]}
    return flows, trips


class Reading:
    """The generated specification read the way the shipped route file is: edges and seconds."""

    def __init__(self, specification: dict) -> None:
        self.spec = specification
        self.resolver = resolver_for(specification)
        civil = datetime.fromisoformat(specification["epoch"]["civil_datetime"])
        # The shipped file's t = 0 was midnight of day 0; this one's is the epoch's clock.
        self.shift = civil.hour * 3600 + civil.minute * 60 + civil.second

    def edge(self, place: str) -> str:
        declared = self.spec["places"][place]
        return declared["edge"] if "edge" in declared else declared["lane"].rsplit("_", 1)[0]

    def shipped_seconds(self, time: object, where: str) -> float:
        resolved = self.resolver.instant(time, where)
        assert resolved is not None, where
        return resolved.seconds + self.shift

    def stop(self, stop: dict) -> tuple[str, float]:
        declared = self.spec["places"][stop["place"]]
        return declared["lane"], declared["offset_m"]


def test_the_guard_rota_and_the_hauls_come_back_entry_for_entry():
    specification = generated_specification()
    reading = Reading(specification)
    _, trips = shipped_entries()
    entries, _ = RotaExpander(reading.resolver, CompileFindings()).expand(
        specification["rotas"][0], specification["place_sets"]["guard_towers"])
    template = specification["rotas"][0]["template"]
    guards = {k: v for k, v in trips.items() if k.startswith("guard_")}
    assert len(entries) == len(guards) == 335
    for entry in entries:
        shipped = guards[entry.entry_id]
        lane, offset = reading.stop({"place": entry.subject})
        assert entry.depart.seconds + reading.shift == shipped["depart"], entry.entry_id
        assert (reading.edge(template["from"]), reading.edge(template["to"])) == (
            shipped["from"], shipped["to"])
        assert [reading.edge(entry.subject)] == shipped["via"]
        assert [(lane, offset, 28_800.0, True)] == shipped["stops"], entry.entry_id
    hauls = {a["id"]: a for a in specification["actors"] if a["id"].startswith("haul_")}
    shipped_hauls = {k: v for k, v in trips.items() if k.startswith("haul_")}
    assert sorted(hauls) == sorted(shipped_hauls) and len(hauls) == 21
    for haul_id, haul in hauls.items():
        shipped = shipped_hauls[haul_id]
        assert reading.shipped_seconds(haul["depart"], haul_id) == shipped["depart"]
        assert (reading.edge(haul["from"]), reading.edge(haul["to"])) == (shipped["from"],
                                                                          shipped["to"])


def test_the_nine_planted_vehicles_come_back_with_their_roads_stops_and_local_times():
    specification = generated_specification()
    reading = Reading(specification)
    _, trips = shipped_entries()
    marked = json.loads(SHIPPED_LABELS.read_text(encoding="utf-8"))["marked_ids"]
    actors = {a["id"]: a for a in specification["actors"] if not a["id"].startswith("haul_")}
    assert sorted(actors) == sorted(marked)
    for actor_id, actor in actors.items():
        shipped = trips[actor_id]
        assert reading.shipped_seconds(actor["depart"], actor_id) == shipped["depart"], actor_id
        assert reading.edge(actor["from"]) == shipped["from"]
        assert reading.edge(actor["to"]) == shipped["to"]
        assert [reading.edge(p) for p in actor.get("via", [])] == shipped["via"], actor_id
    for probe in ("probe_d2", "probe_d5"):
        (stop,) = actors[probe]["stops"]
        assert (*reading.stop(stop), reading.resolver.duration(stop["duration"], probe),
                bool(stop.get("parking"))) == trips[probe]["stops"][0]
    # The stay-behind parks at the same berth and stays until the run ends, as its shipped stop did.
    (stop,) = actors["staybehind"]["stops"]
    lane, end_pos, duration, parking = trips["staybehind"]["stops"][0]
    assert reading.stop(stop) == (lane, end_pos) and stop["parking"] is parking is True
    assert trips["staybehind"]["depart"] + 600 + duration == SHIPPED_END_S
    assert reading.resolver.instant(stop["until"], "until").seconds == \
        reading.resolver.instant(specification["simulation"]["end"], "end").seconds


def test_every_flow_window_comes_back_cut_to_the_run():
    """A shipped window keeps its id, population, roads and rate and is cut to the run; one wholly
    before 07:00 on day 0 is gone; the only windows it did not have are day 7's before 07:00."""
    specification = generated_specification()
    reading = Reading(specification)
    flows, _ = shipped_entries()
    generated = {f["id"]: f for f in specification["flows"]}
    run_begin, run_end = reading.shift, reading.shift + 7 * 86_400
    for flow_id, shipped in flows.items():
        begin, end = max(shipped["begin"], run_begin), min(shipped["end"], run_end)
        if end <= begin:
            assert flow_id not in generated, flow_id
            continue
        flow = generated[flow_id]
        assert (flow["type"], reading.edge(flow["from"]), reading.edge(flow["to"]),
                flow["vehs_per_hour"]) == (shipped["type"], shipped["from"], shipped["to"],
                                           shipped["rate"]), flow_id
        assert (reading.shipped_seconds(flow["begin"], flow_id),
                reading.shipped_seconds(flow["end"], flow_id)) == (begin, end), flow_id
    added = {flow_id: flow for flow_id, flow in generated.items() if flow_id not in flows}
    assert added
    for flow_id, flow in added.items():
        assert "_d7_" in flow_id
        assert reading.shipped_seconds(flow["begin"], flow_id) >= SHIPPED_END_S
        assert reading.shipped_seconds(flow["end"], flow_id) <= run_end


def test_the_six_anomalies_are_supervision_and_the_no_show_keeps_its_described_gap():
    specification = generated_specification()
    reading = Reading(specification)
    labels = json.loads(SHIPPED_LABELS.read_text(encoding="utf-8"))
    supervision = specification["supervision"]
    annotated = [i for i in supervision["instances"] if i["supervision"] == "annotated"]
    assert len(annotated) == 5
    assert sorted(p["actor"] for i in annotated for p in i["participants"]) == \
        sorted(labels["marked_ids"])
    assert all(i["labels"] for i in annotated)
    (absence,) = supervision["absences"]
    (series,) = supervision["series"]
    assert (absence["series"], series["rota"], series["supervision"]) == (
        series["series_id"], "guard_posting", "nominal")
    (gap,) = labels["anomaly_notes"]
    _, skips = RotaExpander(reading.resolver, CompileFindings()).expand(
        specification["rotas"][0], specification["place_sets"]["guard_towers"])
    (skip,) = skips
    assert absence["entry"] == skip.entry_id
    assert skip.subject_index == gap["tower_index"]
    lane, offset = reading.stop({"place": skip.subject})
    assert (lane.rsplit("_", 1)[0], offset) == (gap["edge"], gap["edge_pos_m"])
    assert skip.depart.seconds + reading.shift == gap["begin_s"]
    slot = reading.resolver.duration(series["slot_length"], "slot")
    assert skip.depart.seconds + slot + reading.shift == gap["end_s"]
    assert series["slot_aoi_refs"][skip.subject] == skip.subject


def test_only_the_two_behaviour_classes_are_carried_by_planted_vehicles_alone():
    """A class only planted vehicles carry puts the label in the vehicle type the truth record
    names; the two that do differ from their population in the behaviour itself."""
    specification = generated_specification()
    marked = set(json.loads(SHIPPED_LABELS.read_text(encoding="utf-8"))["marked_ids"])
    planted = {a["type"] for a in specification["actors"] if a["id"] in marked}
    ordinary = {a["type"] for a in specification["actors"] if a["id"] not in marked}
    ordinary |= {r["template"]["type"] for r in specification["rotas"]}
    ordinary |= {c for m in specification["vehicle_mixes"] for c in m["shares"]}
    assert planted - ordinary == {"army_car_crawl", "port_car"}
    behaviour = {c["class_id"]: c["behaviour"] for c in specification["vehicle_classes"]}
    assert behaviour["army_car_crawl"]["speedFactor"] == "0.45"


def test_the_jeep_types_are_drawn_as_the_wrangler_once_the_catalogue_measures_it():
    """The owner's order: the Wrangler, and the Patrol while the catalogue lacks it. Only presence
    in the catalogue decides, so a stand-in catalogue carrying names and no measurement suffices."""
    generator = load_generator()
    real = VehicleCatalogue.load(CATALOGUE)

    def jeep_classes(catalogue) -> dict[str, list[str]]:
        specification = generator.BahonarPatternOfLifeSpecification(
            catalogue, 7, 4, 7, 3, 1.0, 42).build(PACKAGE, "0" * 64, CATALOGUE, IMPORT)
        return {c["class_id"]: c["blueprints"] for c in specification["vehicle_classes"]
                if c["class_id"] in ("civ_pickup", "mil_jeep", "guard")}

    class Names:
        def __init__(self, blueprint_ids: list[str]) -> None:
            self.blueprint_ids = blueprint_ids
            self.classes = real.classes

    wrangler, patrol = "vehicle.jeep.wrangler_rubicon", "vehicle.nissan.patrol"
    expected = wrangler if wrangler in real.blueprint_ids else patrol
    assert set(map(tuple, jeep_classes(real).values())) == {(expected,)}
    without = [b for b in real.blueprint_ids if b != wrangler]
    assert set(map(tuple, jeep_classes(Names(without)).values())) == {(patrol,)}
    assert set(map(tuple, jeep_classes(Names([*real.blueprint_ids, wrangler])).values())) == \
        {(wrangler,)}
    neither = [b for b in real.blueprint_ids if b not in (wrangler, patrol)]
    assert set(map(tuple, jeep_classes(Names(neither)).values())) == {(patrol,)}
    assert {a["type"] for a in generated_specification()["actors"]
            if a["id"].startswith("escort_")} == {"mil_jeep"}
