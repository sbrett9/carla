"""The telemetry the tools emit and write is what its schemas say.

The SUMO bridge is run over a stand-in for TraCI -- a few vehicles of the catalogue's types, one of them
planted, for a few updates -- with every sink on: the UDP feed (its datagrams caught as they are sent),
the XML file and the CSV. Then:

  * each datagram is valid against `cot_telemetry.xsd`, which takes the parts it shares with the truth
    sidecar from `truth_sidecar.xsd`, and the XML file against `sumo_cot_events.xsd`, whose copies of
    those parts are held equal to the sidecar's; a SUMO vehicle's datagram leaves out the fields the
    files keep;
  * the CSV meets `sumo_cot_telemetry.tableschema.json`, whose fields are the bridge's columns, checked
    by `TableSchemaCheck`, the check `carla-validate` holds every table to; and its `.summary.json`
    meets its schema;
  * a CARLA vehicle's datagram, as carla-sctmv sends it with the capture tick and the sun, is valid too;
  * the legacy labels file and the gap sidecar written from it meet their schemas;
  * `carla-validate`, given the folders that hold them, checks each against its schema
    (`TelemetryValidator`), names a file that breaks it, and tells the bridge's event file from a
    truth sidecar and a gap file from a compiled supervision plan.

`test_published_schemas` holds every published schema here equal to `TelemetrySchemas`.
"""
from __future__ import annotations

import csv
import json
import shutil
import sys
import types
from datetime import UTC, datetime
from pathlib import Path

import pytest
from lxml import etree

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.commands.validate import main as validate  # noqa: E402
from carlacontrol.CotUdpEmitter import CotUdpEmitter  # noqa: E402
from carlacontrol.SchemaPublication import SchemaPublication  # noqa: E402
from carlacontrol.SumoCotBridge import (  # noqa: E402
    AUTHORED_TRUTH_FIELDS,
    CSV_COLUMNS,
    CotOutputSettings,
    SumoCotBridge,
)
from carlacontrol.SupervisionSidecar import SupervisionSidecar  # noqa: E402
from carlacontrol.TableSchemaCheck import TableSchemaCheck  # noqa: E402
from carlacontrol.TelemetrySchemas import CSV_TABLE_SCHEMA, TelemetrySchemas  # noqa: E402
from carlacontrol.TelemetryValidator import (  # noqa: E402
    EVENT_FILES,
    GAP_FILES,
    LABELS_FILES,
    TABLE_SUMMARIES,
    TABLES,
)
from carlacontrol.VehicleCatalogue import VehicleCatalogue  # noqa: E402

SCHEMAS = _REPO / "CarlaControl" / "schemas"
DATAGRAM_SCHEMA = SCHEMAS / "cot_telemetry.xsd"
EVENTS_SCHEMA = SCHEMAS / "sumo_cot_events.xsd"
SIDECAR_SCHEMA = SCHEMAS / "truth_sidecar.xsd"
XSD = "{http://www.w3.org/2001/XMLSchema}"
# The datagram's schema includes the truth sidecar's, which the capture schemas publish.
NO_SIDECAR = "truth_sidecar.xsd, which cot_telemetry.xsd includes, is not in CarlaControl/schemas"
needs_sidecar = pytest.mark.skipif(not SIDECAR_SCHEMA.is_file(), reason=NO_SIDECAR)
# The truth sidecar's parts the SUMO bridge's event file holds copies of.
SHARED_PARTS = ("Instant", "CalendarDate", "TrueOrFalse", "Latitude", "Longitude", "Bearing",
                "NonNegativeDecimal", "CotType", "Color", "Point", "Track", "Contact", "Producer",
                "Server")
CATALOGUE = _REPO / "CarlaControl" / "catalogue" / "vehicles.catalogue.json"
LEGACY_LABELS = _REPO / "CarlaControl" / "test" / "fixtures" / \
    "Shahid_Bahonar_Port_PatternOfLife.shipped.labels.json"
EPOCH = datetime(2026, 3, 21, 5, 0, tzinfo=UTC)

# The vehicles the stand-in presents: SUMO id, type id, and that type's class, blueprint and vClass.
TYPES = {
    "vehicle.lincoln.mkz": ("civ_car", "passenger"),
    "vehicle.ambulance.ford": ("ambulance", "emergency"),
    "hand_written_truck": (None, "truck"),
}
ROSTER = [("traffic.0", "vehicle.lincoln.mkz"), ("traffic.1", "vehicle.ambulance.ford"),
          ("orbiter", "vehicle.lincoln.mkz"), ("freight.0", "hand_written_truck")]


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
def datagram_schema() -> etree.XMLSchema:
    if not SIDECAR_SCHEMA.is_file():
        pytest.skip(NO_SIDECAR)
    return etree.XMLSchema(etree.parse(str(DATAGRAM_SCHEMA)))


@pytest.fixture(scope="module")
def events_schema() -> etree.XMLSchema:
    return etree.XMLSchema(etree.parse(str(EVENTS_SCHEMA)))


def test_the_datagram_schema_takes_the_shared_parts_from_the_sidecar_s():
    included = [node.get("schemaLocation")
                for node in etree.parse(str(DATAGRAM_SCHEMA)).getroot().findall(f"{XSD}include")]
    assert included == ["truth_sidecar.xsd", "cot_event_body.xsd"]


def structure(node: etree._Element) -> tuple:
    """A schema component without its annotations: what it allows, not what it says."""
    return (node.tag, tuple(sorted(node.attrib.items())),
            tuple(structure(child) for child in node
                  if isinstance(child.tag, str) and child.tag != f"{XSD}annotation"))


@needs_sidecar
@pytest.mark.parametrize("name", SHARED_PARTS)
def test_the_event_file_s_copy_of_a_sidecar_part_allows_what_the_sidecar_s_does(name):
    def component(path: Path) -> etree._Element:
        (found,) = [node for node in etree.parse(str(path)).getroot() if node.get("name") == name]
        return found
    assert structure(component(EVENTS_SCHEMA)) == structure(component(SIDECAR_SCHEMA))


def test_every_datagram_is_one_valid_event(run, datagram_schema):
    assert len(run.sent) == len(ROSTER) * Playback.STEPS
    for datagram in run.sent:
        assert not datagram.startswith(b"<?xml")
        event = etree.fromstring(datagram)
        assert event.tag == "event"
        assert datagram_schema.validate(event), datagram_schema.error_log


def test_a_sumo_vehicle_s_datagram_leaves_out_what_the_files_keep(run):
    for datagram in run.sent:
        carla = etree.fromstring(datagram).find("detail/_carla")
        assert not set(AUTHORED_TRUTH_FIELDS) & set(carla.attrib)


def test_the_xml_file_is_valid_and_keeps_every_field(run, events_schema):
    document = etree.parse(str(run.directory / "fixture.xml"))
    assert events_schema.validate(document), events_schema.error_log
    root = document.getroot()
    assert root.get("format_version") == "1"
    assert root[0].tag == "_producer" and root[0].get("sumo") == "1.27.0"
    marked = {event.get("uid"): event.find("detail/_carla").get("marked")
              for event in root.findall("event")}
    assert marked["SUMO-TRUTH-orbiter"] == "1" and marked["SUMO-TRUTH-traffic.0"] == "0"
    for event in root.findall("event"):
        assert set(AUTHORED_TRUTH_FIELDS) <= set(event.find("detail/_carla").attrib)


def test_each_event_of_the_xml_file_is_also_a_valid_datagram(run, datagram_schema):
    for event in etree.parse(str(run.directory / "fixture.xml")).getroot().findall("event"):
        alone = etree.fromstring(etree.tostring(event))
        assert datagram_schema.validate(alone), datagram_schema.error_log


def test_the_csv_meets_its_table_schema(run):
    with open(run.directory / "fixture.csv", encoding="utf-8", newline="") as handle:
        rows = list(csv.DictReader(handle))
    assert len(rows) == len(ROSTER) * Playback.STEPS
    assert [str(problem) for problem in TableSchemaCheck(TelemetrySchemas.csv_table()).problems(
        run.directory / "fixture.csv")] == []
    kinds = {row["uid"]: (row["base_type"], row["special_type"]) for row in rows}
    assert kinds["SUMO-TRUTH-traffic.1"] == ("van", "emergency")
    assert kinds["SUMO-TRUTH-freight.0"] == ("truck", "")


def test_a_table_checker_that_accepts_anything_would_fail_here(tmp_path):
    """The control: a row with a malformed time and a marked value of 2 is refused."""
    row = {name: "0" for name in CSV_COLUMNS}
    row.update(time_utc="2026-03-21 05:00:00", marked="2", cot_type="a-n-G-E-V", how="m-g",
               special_type="", color="1,2,3")
    path = tmp_path / "control.csv"
    with open(path, "w", encoding="utf-8", newline="") as handle:
        writer = csv.DictWriter(handle, CSV_COLUMNS)
        writer.writeheader()
        writer.writerow(row)
    problems = {problem.column for problem in TableSchemaCheck(TelemetrySchemas.csv_table()).problems(path)}
    assert problems == {"time_utc", "marked"}


def test_the_csv_summary_meets_its_schema(run):
    summary = json.loads((run.directory / "fixture.summary.json").read_text(encoding="utf-8"))
    assert SchemaPublication.problems(summary, TelemetrySchemas.csv_summary()) == []
    assert summary["csv"] == "fixture.csv"


# -- a CARLA vehicle's datagram -----------------------------------------------------------------------

def test_a_carla_vehicle_s_datagram_with_its_tick_and_sun_is_valid(datagram_schema):
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
    assert datagram_schema.validate(event), datagram_schema.error_log
    assert event.get("uid") == "CARLA-TRUTH-SUMO-traffic.17"
    assert [child.tag for child in event.find("detail")] == ["track", "contact", "_carla", "_capture",
                                                             "_solar"]
    # A blueprint with no color attribute is reported with an empty color, as the sidecar allows.
    uncolored = etree.fromstring(CotUdpEmitter.vehicle_telemetry_to_cot(
        {**record, "color": ""}, when=EPOCH).encode("utf-8"))
    assert datagram_schema.validate(uncolored), datagram_schema.error_log


def test_an_event_the_schema_does_not_describe_is_refused(events_schema):
    """The control: an event with its point's height missing and a type that is not a vehicle."""
    event = etree.fromstring(
        '<events source="sumo" scenario="s" epoch="2026-03-21T05:00:00.000Z">'
        '<event version="2.0" uid="X-1" type="a-n-A-M-F" how="m-g" time="2026-03-21T05:00:00.000Z" '
        'start="2026-03-21T05:00:00.000Z" stale="2026-03-21T05:00:03.000Z">'
        '<point lat="1" lon="2" ce="0" le="0"/><detail><track course="0" speed="0"/>'
        '<contact callsign="car-1"/><_carla source="truth" actor_id="1" base_type="car" '
        'length_m="1" width_m="1" height_m="1" color="1,2,3" vx="0" vy="0" vz="0"/></detail></event>'
        '</events>')
    assert not events_schema.validate(event)
    messages = " ".join(error.message for error in events_schema.error_log)
    assert "hae" in messages and "a-n-A-M-F" in messages


# -- the legacy labels and the gap sidecar ------------------------------------------------------------

def test_the_shipped_legacy_labels_meet_their_schema():
    labels = json.loads(LEGACY_LABELS.read_text(encoding="utf-8"))
    assert SchemaPublication.problems(labels, TelemetrySchemas.legacy_labels()) == []


def test_the_gap_sidecar_written_from_them_meets_its_schema(tmp_path):
    labels = json.loads(LEGACY_LABELS.read_text(encoding="utf-8"))
    sidecar = SupervisionSidecar.from_labels(labels, scenario="Shahid_Bahonar_Port_PatternOfLife",
                                             epoch=EPOCH, labels_path=LEGACY_LABELS)
    written = json.loads(sidecar.write(tmp_path / "run.supervision.json").read_text(encoding="utf-8"))
    assert SchemaPublication.problems(written, TelemetrySchemas.supervision_gaps()) == []
    (gap,) = written["supervision_gaps"]
    assert gap["begin_utc"] == "2026-03-25T12:00:00.000Z"


def test_a_gap_sidecar_with_a_malformed_window_is_refused():
    document = {"scenario": "s", "epoch": "2026-03-21T05:00:00.000Z", "source_labels": None,
                "supervision_gaps": [{"kind": "guard_no_show", "begin_utc": "d4 00:00"}]}
    problems = SchemaPublication.problems(document, TelemetrySchemas.supervision_gaps())
    assert problems and "begin_utc" in problems[0]


# -- carla-validate -----------------------------------------------------------------------------------

def checked(kind: str, count: int, failed: int = 0) -> str:
    """How carla-validate reports a kind it checked."""
    return f"{kind:<28s} {count:6d} checked, {failed} failed"


def validated(folder: Path, caplog) -> tuple[int, str]:
    caplog.clear()
    with caplog.at_level("INFO"):
        status = validate([str(folder)])
    return status, caplog.text


@pytest.fixture
def bridge_output(run, tmp_path) -> Path:
    """A copy of the bridge run's files, to break one at a time."""
    folder = tmp_path / "bridge"
    shutil.copytree(run.directory, folder)
    return folder


def test_carla_validate_checks_every_file_the_bridge_wrote(bridge_output, caplog):
    status, text = validated(bridge_output, caplog)
    assert status == 0, text
    for kind in (EVENT_FILES, TABLES, TABLE_SUMMARIES):
        assert checked(kind, 1) in text, text
    # The bridge's event file has an <events> root, and is not a truth sidecar.
    assert "truth sidecars" not in text


def test_carla_validate_names_an_event_a_table_row_and_a_summary_that_break_their_schemas(
        bridge_output, caplog):
    events = bridge_output / "fixture.xml"
    events.write_text(events.read_text(encoding="utf-8").replace('how="m-g"', 'how="h-e"', 1),
                      encoding="utf-8")
    table = bridge_output / "fixture.csv"
    lines = table.read_text(encoding="utf-8").split("\n")
    lines[1] = lines[1].replace(",m-g,", ",h-e,", 1)
    table.write_text("\n".join(lines), encoding="utf-8", newline="")
    summary = bridge_output / "fixture.summary.json"
    document = json.loads(summary.read_text(encoding="utf-8"))
    document["csv"] = ""
    summary.write_text(json.dumps(document), encoding="utf-8")
    status, text = validated(bridge_output, caplog)
    assert status == 1
    assert checked(EVENT_FILES, 1, 1) in text and checked(TABLES, 1, 1) in text
    assert "FAILED: fixture.xml: line " in text and "'h-e'" in text
    assert "FAILED: fixture.csv: line 2, how: 'h-e' is not one of ['m-g']" in text
    assert "FAILED: fixture.summary.json: $.csv: must not be empty" in text


def test_a_table_whose_summary_declares_a_newer_format_is_named_and_its_rows_left_unchecked(
        bridge_output, caplog):
    summary = bridge_output / "fixture.summary.json"
    document = json.loads(summary.read_text(encoding="utf-8"))
    summary.write_text(json.dumps({**document, "format_version": 2}), encoding="utf-8")
    status, text = validated(bridge_output, caplog)
    assert status == 1
    assert "FAILED: fixture.csv: fixture.csv declares format_version 2, and this reader supports " \
           "format_version 1 and earlier" in text
    assert "line 2" not in text


def test_an_event_file_cut_off_by_an_interrupted_run_is_noted_and_does_not_fail(bridge_output, caplog):
    events = bridge_output / "fixture.xml"
    events.write_text(events.read_text(encoding="utf-8").replace("</events>\n", ""), encoding="utf-8")
    status, text = validated(bridge_output, caplog)
    assert status == 0, text
    assert checked(EVENT_FILES, 1) in text
    assert "is cut off before its closing </events>, so its run was interrupted" in text


def test_carla_validate_checks_the_legacy_labels_and_gaps_and_leaves_a_plan_to_the_records(
        tmp_path, caplog):
    folder = tmp_path / "legacy"
    folder.mkdir()
    shutil.copy(LEGACY_LABELS, folder / "Shahid_Bahonar_Port_PatternOfLife.labels.json")
    labels = json.loads(LEGACY_LABELS.read_text(encoding="utf-8"))
    SupervisionSidecar.from_labels(labels, scenario="Shahid_Bahonar_Port_PatternOfLife", epoch=EPOCH,
                                   labels_path=LEGACY_LABELS).write(folder / "run.supervision.json")
    plan = "Arapahoe_I25_SupervisionCheck.supervision.json"
    shutil.copy(_REPO / "Import" / plan, folder / plan)
    status, text = validated(folder, caplog)
    assert status == 0, text
    assert checked(LABELS_FILES, 1) in text and checked(GAP_FILES, 1) in text
    assert checked("supervision plans", 1) in text
    gaps = folder / "run.supervision.json"
    document = json.loads(gaps.read_text(encoding="utf-8"))
    document["supervision_gaps"][0]["begin_utc"] = "d4 00:00"
    gaps.write_text(json.dumps(document), encoding="utf-8")
    status, text = validated(folder, caplog)
    assert status == 1
    assert "FAILED: run.supervision.json: $.supervision_gaps[0].begin_utc: 'd4 00:00' does not match" \
        in text

