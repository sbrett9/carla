"""The telemetry the tools emit and write is what its schemas say.

The SUMO bridge is run over a stand-in for TraCI -- a few vehicles of the catalogue's types, one of them
planted, for a few updates -- with every sink on: the UDP feed (its datagrams caught as they are sent),
the XML file and the CSV. Then:

  * each datagram, and the XML file, are valid against `cot_telemetry.xsd`; a SUMO vehicle's datagram
    leaves out the fields the files keep;
  * the CSV meets `sumo_cot_telemetry.tableschema.json`, whose fields are the bridge's columns, and its
    `.summary.json` meets its schema;
  * a CARLA vehicle's datagram, as carla-sctmv sends it with the capture tick and the sun, is valid too;
  * the legacy labels file and the gap sidecar written from it meet their schemas.

Every published schema here is held equal to `TelemetrySchemas`.
"""
from __future__ import annotations

import csv
import json
import re
import sys
import types
from datetime import UTC, datetime
from pathlib import Path

import pytest
from lxml import etree

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.CotUdpEmitter import CotUdpEmitter  # noqa: E402
from carlacontrol.JsonSchemaFile import JsonSchemaFile  # noqa: E402
from carlacontrol.SumoCotBridge import (  # noqa: E402
    AUTHORED_TRUTH_FIELDS,
    CSV_COLUMNS,
    CotOutputSettings,
    SumoCotBridge,
)
from carlacontrol.SupervisionSidecar import SupervisionSidecar  # noqa: E402
from carlacontrol.TelemetrySchemas import CSV_TABLE_SCHEMA, TelemetrySchemas  # noqa: E402
from carlacontrol.VehicleCatalogue import VehicleCatalogue  # noqa: E402

SCHEMAS = _REPO / "CarlaControl" / "schemas"
COT_SCHEMA = SCHEMAS / "cot_telemetry.xsd"
CATALOGUE = _REPO / "CarlaControl" / "catalogue" / "vehicles.catalogue.json"
LEGACY_LABELS = _REPO / "CarlaControl" / "test" / "fixtures" / \
    "Shahid_Bahonar_Port_PatternOfLife.shipped.labels.json"
REGENERATE = ("regenerate with: python -c \"from carlacontrol.TelemetrySchemas import "
              "TelemetrySchemas; TelemetrySchemas.write('CarlaControl/schemas')\"")
EPOCH = datetime(2026, 3, 21, 5, 0, tzinfo=UTC)

# The vehicles the stand-in presents: SUMO id, type id, and that type's class, blueprint and vClass.
TYPES = {
    "vehicle.lincoln.mkz": ("civ_car", "passenger"),
    "vehicle.ambulance.ford": ("ambulance", "emergency"),
    "hand_written_truck": (None, "truck"),
}
ROSTER = [("traffic.0", "vehicle.lincoln.mkz"), ("traffic.1", "vehicle.ambulance.ford"),
          ("orbiter", "vehicle.lincoln.mkz"), ("freight.0", "hand_written_truck")]


@pytest.mark.parametrize("name", sorted(TelemetrySchemas.schemas()))
def test_the_published_schema_is_the_generated_one(name):
    assert (SCHEMAS / name).read_text(encoding="utf-8") == JsonSchemaFile.text(
        TelemetrySchemas.schemas()[name]), REGENERATE


def test_the_table_schema_s_fields_are_the_bridge_s_columns_in_order():
    fields = json.loads((SCHEMAS / CSV_TABLE_SCHEMA).read_text(encoding="utf-8"))["fields"]
    assert [field["name"] for field in fields] == CSV_COLUMNS


# -- a run of the bridge ------------------------------------------------------------------------------

class Playback:
    """Answers the TraCI calls the bridge makes, for a fixed roster over three one-second steps."""

    STEPS = 3

    def __init__(self) -> None:
        self.time = 0.0
        self.simulation = types.SimpleNamespace(
            getDeltaT=lambda: 1.0, getEndTime=lambda: float(self.STEPS),
            getMinExpectedNumber=lambda: 1 if self.time < self.STEPS else 0,
            getTime=lambda: self.time,
            convertGeo=lambda x, y: (-104.88449 + x / 86_000.0, 39.59431 + y / 111_000.0))
        index = {vehicle: n for n, (vehicle, _) in enumerate(ROSTER)}
        self.vehicle = types.SimpleNamespace(
            getIDList=lambda: tuple(vehicle for vehicle, _ in ROSTER),
            getPosition=lambda v: (10.0 * index[v] + 5.0 * self.time, -3.0 * index[v]),
            getTypeID=lambda v: dict(ROSTER)[v],
            getSpeed=lambda v: 5.0 + index[v],
            getAngle=lambda v: (90.0 * index[v] + 359.96) % 720.0,
            getLength=lambda v: 4.89, getWidth=lambda v: 1.83, getHeight=lambda v: 1.52,
            getRoadID=lambda v: f"edge{index[v]}", getLaneID=lambda v: f"edge{index[v]}_0")
        self.vehicletype = types.SimpleNamespace(
            getColor=lambda t: (204, 204, 209, 255),
            getVehicleClass=lambda t: TYPES[t][1],
            getParameter=self._parameter)

    @staticmethod
    def _parameter(type_id: str, key: str) -> str:
        class_id, _ = TYPES[type_id]
        if class_id is None:
            return ""
        return {"carla:blueprint": type_id, "carla:class_id": class_id}.get(key, "")

    def start(self, command) -> None:
        self.command = command

    def simulationStep(self) -> None:  # noqa: N802 -- TraCI's spelling
        self.time += 1.0

    def close(self) -> None:
        pass


class Installation:
    """A located SUMO, as far as the bridge asks."""

    version = "1.27.0"
    sumo = Path("sumo")
    sumo_gui = Path("sumo-gui")

    def __init__(self) -> None:
        self.playback = Playback()

    def import_traci(self):
        return self.playback


@pytest.fixture(scope="module")
def run(tmp_path_factory, request):
    directory = tmp_path_factory.mktemp("bridge")
    sent: list[bytes] = []
    patch = pytest.MonkeyPatch()
    patch.setattr(CotUdpEmitter, "send", lambda self, xml: sent.append(xml.encode("utf-8")))
    request.addfinalizer(patch.undo)
    bridge = SumoCotBridge(Installation(), directory / "fixture.sumocfg", constant_hae=1747.4,
                           catalogue=VehicleCatalogue.load(CATALOGUE))
    settings = CotOutputSettings(
        udp_host="127.0.0.1", udp_port=6969, xml_path=directory / "fixture.xml",
        csv_path=directory / "fixture.csv", marked_vehicle="orbiter", epoch=EPOCH,
        affiliation_by_type={"civ_car": "n", "ambulance": "f"},
        display_convention="fixture.display.json")
    report = bridge.run(settings)
    assert report.events == len(ROSTER) * Playback.STEPS
    return types.SimpleNamespace(directory=directory, sent=sent, settings=settings)


@pytest.fixture(scope="module")
def cot_schema() -> etree.XMLSchema:
    return etree.XMLSchema(etree.parse(str(COT_SCHEMA)))


def test_every_datagram_is_one_valid_event(run, cot_schema):
    assert len(run.sent) == len(ROSTER) * Playback.STEPS
    for datagram in run.sent:
        assert not datagram.startswith(b"<?xml")
        event = etree.fromstring(datagram)
        assert event.tag == "event"
        assert cot_schema.validate(event), cot_schema.error_log


def test_a_sumo_vehicle_s_datagram_leaves_out_what_the_files_keep(run):
    for datagram in run.sent:
        carla = etree.fromstring(datagram).find("detail/_carla")
        assert not set(AUTHORED_TRUTH_FIELDS) & set(carla.attrib)


def test_the_xml_file_is_valid_and_keeps_every_field(run, cot_schema):
    document = etree.parse(str(run.directory / "fixture.xml"))
    assert cot_schema.validate(document), cot_schema.error_log
    root = document.getroot()
    assert root.get("format_version") == "1"
    assert root[0].tag == "_producer" and root[0].get("sumo") == "1.27.0"
    marked = {event.get("uid"): event.find("detail/_carla").get("marked")
              for event in root.findall("event")}
    assert marked["SUMO-TRUTH-orbiter"] == "1" and marked["SUMO-TRUTH-traffic.0"] == "0"
    for event in root.findall("event"):
        assert set(AUTHORED_TRUTH_FIELDS) <= set(event.find("detail/_carla").attrib)


def check_table(rows: list[dict], header: list[str], table: dict) -> list[str]:
    """Every place a CSV departs from a Frictionless Table Schema, for the parts this one uses."""
    problems = []
    fields = table["fields"]
    if header != [field["name"] for field in fields]:
        problems.append(f"header {header} is not the fields in order")
    for number, row in enumerate(rows, start=2):
        for field in fields:
            name, value = field["name"], row.get(field["name"])
            constraints = field.get("constraints", {})
            if value is None or (value in table.get("missingValues", [""]) and constraints.get("required")):
                problems.append(f"line {number} {name}: missing")
                continue
            kind = field["type"]
            try:
                if kind == "number":
                    parsed = float(value)
                elif kind == "integer":
                    parsed = int(value)
                elif kind == "boolean":
                    if value not in field["trueValues"] + field["falseValues"]:
                        raise ValueError(value)
                    parsed = value in field["trueValues"]
                elif kind == "datetime":
                    parsed = datetime.strptime(value, field["format"])
                else:
                    parsed = value
            except ValueError:
                problems.append(f"line {number} {name}: {value!r} is not a {kind}")
                continue
            if "enum" in constraints and parsed not in constraints["enum"]:
                problems.append(f"line {number} {name}: {value!r} is not one of {constraints['enum']}")
            if "pattern" in constraints and not re.fullmatch(constraints["pattern"], value):
                problems.append(f"line {number} {name}: {value!r} does not match the pattern")
            if "minimum" in constraints and parsed < constraints["minimum"]:
                problems.append(f"line {number} {name}: {value} is below the minimum")
            if "maximum" in constraints and parsed > constraints["maximum"]:
                problems.append(f"line {number} {name}: {value} is above the maximum")
    return problems


def test_the_csv_meets_its_table_schema(run):
    with open(run.directory / "fixture.csv", encoding="utf-8", newline="") as handle:
        reader = csv.DictReader(handle)
        rows = list(reader)
        header = reader.fieldnames
    assert len(rows) == len(ROSTER) * Playback.STEPS
    assert check_table(rows, header, TelemetrySchemas.csv_table()) == []
    kinds = {row["uid"]: (row["base_type"], row["special_type"]) for row in rows}
    assert kinds["SUMO-TRUTH-traffic.1"] == ("van", "emergency")
    assert kinds["SUMO-TRUTH-freight.0"] == ("truck", "")


def test_a_table_checker_that_accepts_anything_would_fail_here():
    """The control: a row with a malformed time and a marked value of 2 is refused."""
    table = TelemetrySchemas.csv_table()
    row = {name: "0" for name in CSV_COLUMNS}
    row.update(time_utc="2026-03-21 05:00:00", marked="2", cot_type="a-n-G-E-V", how="m-g",
               special_type="", color="1,2,3")
    problems = check_table([row], CSV_COLUMNS, table)
    assert any("time_utc" in problem for problem in problems)
    assert any("marked" in problem for problem in problems)


def test_the_csv_summary_meets_its_schema(run):
    summary = json.loads((run.directory / "fixture.summary.json").read_text(encoding="utf-8"))
    assert JsonSchemaFile.problems(summary, TelemetrySchemas.csv_summary()) == []
    assert summary["csv"] == "fixture.csv"


# -- a CARLA vehicle's datagram -----------------------------------------------------------------------

def test_a_carla_vehicle_s_datagram_with_its_tick_and_sun_is_valid(cot_schema):
    record = {"id": 214, "type_id": "vehicle.lincoln.mkz", "base_type": "car", "special_type": "",
              "color": "17,213,91", "role_name": "autopilot", "lat": 39.5943123, "lon": -104.8844901,
              "hae": 1716.25, "hae_dtm": 1715.70, "speed_mps": 12.5, "course_deg": 271.3,
              "heading_deg": 270.9, "vx": -12.49, "vy": -0.28, "vz": 0.0, "length_m": 4.89,
              "width_m": 2.05, "height_m": 1.52, "sumo_id": "traffic.17", "vtype_id": "civ_car",
              "admitted_tick": 4410}
    solar = {"solar_time": 7.25, "year": 2026, "month": 3, "day": 21, "time_zone": -6.9923,
             "sun_elevation_deg": 9.871, "sun_azimuth_deg": 97.412, "advancing": True, "rate": 1.0}
    clock = types.SimpleNamespace(attributes=lambda: {"tick": "4520"})
    event = etree.fromstring(CotUdpEmitter.vehicle_telemetry_to_cot(
        record, solar=solar, capture=clock, when=EPOCH).encode("utf-8"))
    assert cot_schema.validate(event), cot_schema.error_log
    assert event.get("uid") == "CARLA-TRUTH-SUMO-traffic.17"
    assert [child.tag for child in event.find("detail")] == ["track", "contact", "_carla", "_capture",
                                                             "_solar"]


def test_an_event_the_schema_does_not_describe_is_refused(cot_schema):
    """The control: a datagram with its point's height missing and a type that is not a vehicle."""
    event = etree.fromstring(
        '<event version="2.0" uid="X-1" type="a-n-A-M-F" how="m-g" time="2026-03-21T05:00:00.000Z" '
        'start="2026-03-21T05:00:00.000Z" stale="2026-03-21T05:00:03.000Z">'
        '<point lat="1" lon="2" ce="0" le="0"/><detail><track course="0" speed="0"/>'
        '<contact callsign="car-1"/><_carla source="truth" actor_id="1" base_type="car" '
        'length_m="1" width_m="1" height_m="1" color="1,2,3" vx="0" vy="0" vz="0"/></detail></event>')
    assert not cot_schema.validate(event)
    messages = " ".join(error.message for error in cot_schema.error_log)
    assert "hae" in messages and "a-n-A-M-F" in messages


# -- the legacy labels and the gap sidecar ------------------------------------------------------------

def test_the_shipped_legacy_labels_meet_their_schema():
    labels = json.loads(LEGACY_LABELS.read_text(encoding="utf-8"))
    assert JsonSchemaFile.problems(labels, TelemetrySchemas.legacy_labels()) == []


def test_the_gap_sidecar_written_from_them_meets_its_schema(tmp_path):
    labels = json.loads(LEGACY_LABELS.read_text(encoding="utf-8"))
    sidecar = SupervisionSidecar.from_labels(labels, scenario="Shahid_Bahonar_Port_PatternOfLife",
                                             epoch=EPOCH, labels_path=LEGACY_LABELS)
    written = json.loads(sidecar.write(tmp_path / "run.supervision.json").read_text(encoding="utf-8"))
    assert JsonSchemaFile.problems(written, TelemetrySchemas.supervision_gaps()) == []
    (gap,) = written["supervision_gaps"]
    assert gap["begin_utc"] == "2026-03-25T12:00:00.000Z"


def test_a_gap_sidecar_with_a_malformed_window_is_refused():
    document = {"scenario": "s", "epoch": "2026-03-21T05:00:00.000Z", "source_labels": None,
                "supervision_gaps": [{"kind": "guard_no_show", "begin_utc": "d4 00:00"}]}
    problems = JsonSchemaFile.problems(document, TelemetrySchemas.supervision_gaps())
    assert problems and "begin_utc" in problems[0]
