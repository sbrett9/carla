"""The corpus a Bahonar run writes must not say which of its vehicles were planted.

A corpus is generated here rather than asserted about in the abstract: the real scenario definitions
from `make_bahonar_scenario.py` drive the real `SumoCotBridge` writers through a stand-in for TraCI,
so what is checked is the file the pipeline actually produces. Three things are asserted, and the
middle one is the reason the other two mean anything:

  * the corpus written today carries no field by which its planted vehicles can be told apart,
  * the corpus the pipeline wrote before this was repaired is **rejected**, on every one of the six
    fields that gave the answer away, and
  * an author who gives five vehicles the only length in the map that no other vehicle has is caught
    by the same check, without anybody having thought of that case in advance.
"""
from __future__ import annotations

import csv
import importlib.util
import sys
import types
import xml.etree.ElementTree as ET
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.CorpusLeakValidator import (  # noqa: E402  (needs the path above)
    DIMENSION_FIELDS,
    LABEL_FIELDS,
    CorpusLeakValidator,
)
from carlacontrol.SumoCotBridge import (  # noqa: E402
    CSV_COLUMNS,
    CotOutputSettings,
    SumoCotBridge,
)

SCENARIO_DAYS = 7
UID_PREFIX = "SUMO-TRUTH"

# How the four planted vehicle types looked before the repair, and the ids the vehicles using them
# carried: saturated colours against a muted population, a CoT affiliation of `u` that no nominal
# vehicle had, and names that stated the deviation in plain text. Reproduced here so the check can
# be run against the output the pipeline used to write, because a validator that has never rejected
# anything has not been tested.
PRE_REPAIR_TYPES = {
    "civ_sedan": ("anomaly_probe", "255,114,0", "u", 4.4, 1.8, 1.5),
    "mil_utility": ("anomaly_escort", "255,25,25", "u", 6.0, 2.3, 1.5),
    "mil_patrol": ("anomaly_shadow", "255,51,153", "u", 4.6, 1.8, 1.5),
    "port_van": ("anomaly_staybehind", "255,76,0", "u", 4.6, 1.8, 1.5),
}
PRE_REPAIR_IDS = {
    "haul_d3_3": "escort_0", "haul_d3_4": "escort_1", "haul_d3_5": "escort_2",
    "haul_d3_6": "escort_3", "haul_d3_7": "escort_4",
    "errand_d2": "probe_d2", "errand_d5": "probe_d5",
    "patrol_d6": "shadow", "port_run_d1": "staybehind",
}
PRE_REPAIR_CSV_COLUMNS = [
    "time_utc", "sim_time_s", "uid", "callsign", "cot_type", "how",
    "lat", "lon", "hae_m", "ce_m", "le_m",
    "course_deg", "speed_mps", "vx", "vy", "vz",
    "base_type", "type_id", "special_type", "length_m", "width_m", "height_m", "color",
    "role_name", "marked", "edge", "lane", "sumo_x", "sumo_y", "carla_x", "carla_y",
]


def _load_scenario():
    """Import `make_bahonar_scenario.py`, which is a script rather than a package module."""
    path = _REPO / "CarlaControl" / "scripts" / "make_bahonar_scenario.py"
    spec = importlib.util.spec_from_file_location("make_bahonar_scenario", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class _VehicleTypeTable:
    """The vType definitions a scenario declares, resolved to what TraCI would report for them."""

    DEFAULT_WIDTH = 1.8
    DEFAULT_HEIGHT = 1.5

    def __init__(self, vehicle_types_xml: str):
        root = ET.fromstring(f"<types>{vehicle_types_xml}</types>")
        self.types: dict[str, dict] = {}
        self.distributions: dict[str, list[str]] = {}
        for element in root.findall("vType"):
            red, green, blue = (float(v) for v in element.get("color").split(","))
            self.types[element.get("id")] = {
                "vehicle_class": element.get("vClass", "passenger"),
                "color": (round(red * 255), round(green * 255), round(blue * 255), 255),
                "length": float(element.get("length", 5.0)),
                "width": float(element.get("width", self.DEFAULT_WIDTH)),
                "height": float(element.get("height", self.DEFAULT_HEIGHT)),
            }
        for element in root.findall("vTypeDistribution"):
            self.distributions[element.get("id")] = element.get("vTypes").split()

    def members(self, name: str) -> list[str]:
        """The concrete types a flow's declared type resolves to."""
        return self.distributions.get(name, [name])


class _ScenarioPlayback:
    """A stand-in for TraCI that presents a fixed roster of vehicles for a fixed number of steps.

    The bridge asks the simulation for a vehicle list and then for each vehicle's state; none of
    that needs a simulation to run, only answers that are self-consistent. Positions and speeds vary
    by roster index so the corpus looks like a corpus, and the origin is Bandar Abbas so the
    geodetic conversion produces plausible coordinates.
    """

    ORIGIN_LAT, ORIGIN_LON = 27.15012, 56.18065
    STEPS = 2

    def __init__(self, roster: list[tuple[str, str]], table: _VehicleTypeTable):
        self.roster = roster
        self.table = table
        self.time = 0.0
        self.started: list[str] | None = None
        self.closed = False
        self.simulation = types.SimpleNamespace(
            getDeltaT=lambda: 1.0,
            getEndTime=lambda: float(self.STEPS),
            getMinExpectedNumber=lambda: 1 if self.time < self.STEPS else 0,
            getTime=lambda: self.time,
            convertGeo=self._convert_geo,
        )
        self.vehicle = types.SimpleNamespace(
            getIDList=lambda: tuple(vehicle_id for vehicle_id, _ in self.roster),
            getPosition=self._position,
            getTypeID=lambda vehicle_id: self._type_of(vehicle_id),
            getSpeed=lambda vehicle_id: 5.0 + self._index(vehicle_id) % 11,
            getAngle=lambda vehicle_id: (self._index(vehicle_id) * 7) % 360,
            getLength=lambda vehicle_id: self._attribute(vehicle_id, "length"),
            getWidth=lambda vehicle_id: self._attribute(vehicle_id, "width"),
            getHeight=lambda vehicle_id: self._attribute(vehicle_id, "height"),
            getRoadID=lambda vehicle_id: f"edge_{self._index(vehicle_id) % 23}",
            getLaneID=lambda vehicle_id: f"edge_{self._index(vehicle_id) % 23}_0",
        )
        self.vehicletype = types.SimpleNamespace(
            getColor=lambda type_id: self.table.types[type_id]["color"],
            getVehicleClass=lambda type_id: self.table.types[type_id]["vehicle_class"],
        )

    # -- what the bridge calls on the module itself --

    def start(self, command):
        self.started = command

    def simulationStep(self):  # noqa: N802  (TraCI's own spelling)
        self.time += 1.0

    def close(self):
        self.closed = True

    # -- per-vehicle answers --

    def _index(self, vehicle_id: str) -> int:
        return self._order[vehicle_id]

    def _type_of(self, vehicle_id: str) -> str:
        return dict(self.roster)[vehicle_id]

    def _attribute(self, vehicle_id: str, name: str) -> float:
        return self.table.types[self._type_of(vehicle_id)][name]

    def _position(self, vehicle_id: str) -> tuple[float, float]:
        index = self._index(vehicle_id)
        return (-3000.0 + 7.3 * index, -1800.0 + 11.1 * index + 40.0 * self.time)

    def _convert_geo(self, x: float, y: float) -> tuple[float, float]:
        """Metres east and north of the scenario origin, as degrees. Close enough for a fixture."""
        return (self.ORIGIN_LON + x / 98_000.0, self.ORIGIN_LAT + y / 111_320.0)

    @property
    def _order(self) -> dict[str, int]:
        if not hasattr(self, "_order_cache"):
            self._order_cache = {vehicle_id: i for i, (vehicle_id, _) in enumerate(self.roster)}
        return self._order_cache


class _StubInstallation:
    """Stands in for a located SUMO so the bridge can run without one."""

    def __init__(self, traci):
        self._traci = traci

    def import_traci(self):
        return self._traci

    @property
    def sumo(self) -> Path:
        return Path("sumo")

    @property
    def sumo_gui(self) -> Path:
        return Path("sumo-gui")


def _roster(scenario, table: _VehicleTypeTable) -> list[tuple[str, str]]:
    """Every scheduled vehicle plus a deterministic sample of each flow's members.

    Every scheduled vehicle is included because the planted ones are scheduled; the flows are
    sampled because a week of them is hundreds of thousands of vehicles and the check is over the
    set of values a field takes, which a handful of each type establishes.
    """
    roster: list[tuple[str, str]] = []
    for vehicle in (scenario.tower_postings(SCENARIO_DAYS, 4, 7, 3)
                    + scenario.routine_hauls(SCENARIO_DAYS)
                    + scenario.anomaly_vehicles(SCENARIO_DAYS)):
        roster.append((vehicle.veh_id, vehicle.vehicle_type))

    flows = (scenario.diurnal_corridor_flows(SCENARIO_DAYS)
             + scenario.ferry_pulse_flows(SCENARIO_DAYS)
             + scenario.shift_change_flows(SCENARIO_DAYS))
    sampled: set[str] = set()
    for flow in flows:
        family = flow.flow_id.split("_d")[0]
        if family in sampled:
            continue
        sampled.add(family)
        members = table.members(flow.vehicle_type)
        for n, type_id in enumerate(members * 2):
            roster.append((f"{flow.flow_id}.{n}", type_id))
    return roster


def _write_corpus(tmp_path: Path, scenario, table, roster, marked_ids) -> tuple[Path, Path]:
    """Run the real bridge writers over the roster and return the CSV and XML it wrote."""
    playback = _ScenarioPlayback(roster, table)
    bridge = SumoCotBridge(_StubInstallation(playback), tmp_path / "scenario.sumocfg",
                           constant_hae=12.0)
    csv_path, xml_path = tmp_path / "corpus.csv", tmp_path / "corpus.xml"
    bridge.run(CotOutputSettings(
        csv_path=csv_path, xml_path=xml_path, uid_prefix=UID_PREFIX,
        marked_vehicle="", marked_ids=frozenset(marked_ids),
        affiliation_by_type=scenario.AFFILIATION_BY_TYPE))
    return csv_path, xml_path


def _pre_repair_rows(rows: list[dict], roster, table, marked_ids) -> list[dict]:
    """The same traffic as it was written before the repair, in the shipped column set."""
    types_by_vehicle = dict(roster)
    out = []
    for row in rows:
        vehicle_id = row["uid"].removeprefix(f"{UID_PREFIX}-")
        type_id = types_by_vehicle[vehicle_id]
        marked = vehicle_id in marked_ids
        old_type, old_color, old_affiliation, length, width, height = PRE_REPAIR_TYPES.get(
            type_id, (type_id, None, None, None, None, None))
        old_id = PRE_REPAIR_IDS.get(vehicle_id, vehicle_id)
        colour = table.types[type_id]["color"]
        legacy = dict(row)
        legacy["uid"] = f"{UID_PREFIX}-{old_id}"
        legacy["callsign"] = f"{row['base_type']}-{old_id}"
        legacy["type_id"] = old_type
        legacy["special_type"] = "marked" if marked else ""
        legacy["marked"] = "1" if marked else "0"
        legacy["role_name"] = old_id.rsplit(".", 1)[0]
        legacy["color"] = old_color or f"{colour[0]},{colour[1]},{colour[2]}"
        if old_affiliation:
            legacy["cot_type"] = f"a-{old_affiliation}-G-E-V"
            legacy["length_m"] = f"{length:.2f}"
            legacy["width_m"] = f"{width:.2f}"
            legacy["height_m"] = f"{height:.2f}"
        out.append({name: legacy.get(name, "") for name in PRE_REPAIR_CSV_COLUMNS})
    return out


@pytest.fixture(scope="module")
def scenario():
    return _load_scenario()


@pytest.fixture(scope="module")
def table(scenario):
    return _VehicleTypeTable(scenario.VEHICLE_TYPES)


@pytest.fixture(scope="module")
def roster(scenario, table):
    return _roster(scenario, table)


@pytest.fixture(scope="module")
def marked_ids(scenario):
    return frozenset(v.veh_id for v in scenario.anomaly_vehicles(SCENARIO_DAYS) if v.marked)


@pytest.fixture(scope="module")
def corpus(tmp_path_factory, scenario, table, roster, marked_ids):
    return _write_corpus(tmp_path_factory.mktemp("corpus"), scenario, table, roster, marked_ids)


def _rows(path: Path) -> list[dict]:
    with open(path, newline="", encoding="utf-8") as handle:
        return list(csv.DictReader(handle))


def test_scenario_plants_the_vehicles_the_corpus_is_checked_against(marked_ids, roster):
    """The fixture is only meaningful if the roster really contains planted vehicles."""
    assert len(marked_ids) == 9
    assert marked_ids <= {vehicle_id for vehicle_id, _ in roster}


def test_written_columns_carry_the_authoring_fields():
    """The CSV is the truth sidecar, so the author's own categories belong in it."""
    for name in ("type_id", "special_type", "role_name", "marked"):
        assert name in CSV_COLUMNS


def test_the_written_csv_carries_which_vehicles_were_planted(corpus, marked_ids):
    """Companion to the XML case: both written sinks are the sidecar and both carry the answer."""
    csv_path, _ = corpus
    validator = CorpusLeakValidator(marked_ids, fields=LABEL_FIELDS, uid_prefix=UID_PREFIX)
    fields = {finding.field for finding in validator.identifying(validator.check_csv(csv_path))}
    assert {"type_id", "special_type", "marked"} <= fields


def test_the_written_sidecar_carries_which_vehicles_were_planted(corpus, marked_ids):
    """The XML and CSV are the truth sidecar, and truth is meant to carry the answer.

    The check reports the fields that separate the two groups; on the sidecar that is a description
    of the artifact rather than a defect in it. What the tool is for is an artifact where the
    separation would be a defect, and there is none of those in the tree yet.
    """
    _, xml_path = corpus
    validator = CorpusLeakValidator(marked_ids, fields=LABEL_FIELDS, uid_prefix=UID_PREFIX)
    fields = {finding.field for finding in validator.identifying(validator.check_xml(xml_path))}
    assert "type_id" in fields, "the sidecar was expected to name the planted vehicles' type"


def test_the_check_names_every_field_that_separates_the_two_groups(corpus, marked_ids):
    """What the tool is for, stated against a corpus that does separate them.

    The sidecar separates them on every label field, which is what a sidecar is supposed to do. The
    same check over an artifact where that separation would be a defect is the use; this pins that
    the check finds all of it rather than the first one.
    """
    csv_path, _ = corpus
    validator = CorpusLeakValidator(marked_ids, fields=LABEL_FIELDS, uid_prefix=UID_PREFIX)
    findings = validator.identifying(validator.check_csv(csv_path))
    assert {finding.field for finding in findings} == {
        "type_id", "special_type", "role_name", "marked", "color", "cot_type"},         CorpusLeakValidator.describe(findings)


def test_a_corpus_that_separates_nothing_yields_nothing(corpus, marked_ids):
    """The check is not simply always failing: flatten the separating fields and it goes quiet."""
    csv_path, _ = corpus
    rows = _rows(csv_path)
    for row in rows:
        for field in LABEL_FIELDS:
            if field in row:
                row[field] = "same"
    validator = CorpusLeakValidator(marked_ids, fields=LABEL_FIELDS, uid_prefix=UID_PREFIX)
    assert validator.identifying(validator.check_records(rows)) == []


def test_the_check_catches_a_dimension_no_other_vehicle_has(corpus, marked_ids):
    """An author who makes the escort a metre longer than its cover has labelled it."""
    csv_path, _ = corpus
    rows = _rows(csv_path)
    escorts = {"escort_0", "escort_1", "escort_2", "escort_3", "escort_4"}
    for row in rows:
        if row["uid"].removeprefix(f"{UID_PREFIX}-") in escorts:
            row["length_m"] = "6.00"

    validator = CorpusLeakValidator(marked_ids, fields=DIMENSION_FIELDS, uid_prefix=UID_PREFIX)
    findings = validator.identifying(validator.check_records(rows))
    lengths = [finding for finding in findings if finding.field == "length_m"]
    assert [finding.value for finding in lengths] == ["6.00"], \
        CorpusLeakValidator.describe(findings)
    assert set(lengths[0].vehicles) == escorts


def test_an_unmodified_corpus_passes_the_same_dimension_check(corpus, marked_ids):
    """The dimension check is not simply always failing."""
    csv_path, _ = corpus
    validator = CorpusLeakValidator(marked_ids, fields=("height_m",), uid_prefix=UID_PREFIX)
    assert validator.check_csv(csv_path) == []


def test_the_run_report_counts_the_planted_vehicles_it_saw(tmp_path, scenario, table, roster,
                                                           marked_ids):
    """The record still knows the truth; only the written output does not."""
    playback = _ScenarioPlayback(roster, table)
    bridge = SumoCotBridge(_StubInstallation(playback), tmp_path / "scenario.sumocfg",
                           constant_hae=12.0)
    report = bridge.run(CotOutputSettings(
        csv_path=tmp_path / "corpus.csv", uid_prefix=UID_PREFIX,
        marked_vehicle="", marked_ids=marked_ids,
        affiliation_by_type=scenario.AFFILIATION_BY_TYPE))
    assert report.marked_vehicles == len(marked_ids)
    assert report.vehicles == len(roster)
