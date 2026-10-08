"""A run's CoT display convention draws each population as it really looks, and no planted vehicle.

`06_Truth_And_Annotation.md` §9.1 keeps the display half of the legacy `affiliation_by_type` --
civilian traffic neutral, military friendly, which is what makes a TAK view of the port readable --
as a run display convention, and deletes the half that gave every anomaly type `u`.
`07_Scenario_Authoring.md` §3.4.1 keeps it out of the specification. Held here:

* **The convention is applied by population.** A compiled scenario's vType is one body of a class,
  named in its `carla:class_id` parameter, and the bridge reads the class back from the type rather
  than parsing the type id; a hand-written type is its own population; an unnamed one takes the
  default.
* **No planted vehicle can be told apart by its CoT type.** The Bahonar pattern of life's own
  vehicle types -- written by the compiler's own writer from the specification in `Import/` -- drive
  the real bridge writers through a stand-in for TraCI, and the CSV, the XML and the datagrams a TAK
  client would receive are each grouped by whether the specification annotates the vehicle. No value
  of the CoT type, and no pairing of it with the vehicle's observable base type, is carried by the
  planted vehicles alone. The same check, handed a convention that draws the perimeter crawl `u` or
  `n`, finds the shadow, so the silence above is measured rather than assumed.
* **A legacy labels file keeps its display half.** The shipped Bahonar labels' `u` for the four
  anomaly types is withheld; the CoT type the same shipped scenario then produces no longer names
  the planted vehicles, where the map applied whole names all nine.
* **A scenario's own convention is found beside it.** Without `--display-convention`,
  `sumo_cot_telemetry.py` draws with `<scenario>.display.json` beside the `.sumocfg` and says so;
  a found file behaves as a named one, so it wins over a labels file's affiliations and is refused
  when it does not read; a named file wins over a found one; with neither, the run says so.
* **A planted vehicle is marked in the files and nowhere else** (06 D6.18, 08 D8.23). Its
  `special_type` is its kind, in every sink: empty on a bridge given no catalogue, as every SUMO
  vehicle's then is. The XML and CSV carry `marked`; the datagrams carry neither field, and draw a
  planted vehicle apart only when `--marked-affiliation` asks, which never reaches a file. A
  compiled scenario marks none, and its planted vehicles are the supervision plan's, found in the
  sidecar by vehicle id.
* **A vehicle's base type and kind are the catalogue's for the blueprint its type names** (06
  D6.18). A bridge given the catalogue writes, into the XML and CSV, the base type and the kind the
  catalogue curates for the blueprint a compiled type names in `carla:blueprint` -- an ambulance a
  van, a fire appliance a truck, a police car and an army jeep cars -- and for a type that names none,
  or names a blueprint the catalogue does not curate, its vehicle class's base type and no kind; each
  type is asked once. Without a catalogue every base type is the vehicle class's. Over the compiled
  Bahonar traffic no base type and no kind is carried by the planted vehicles alone. A type written
  from another catalogue is warned about once. `sumo_cot_telemetry.py` reads the catalogue it is
  given, or this repository's.
"""
from __future__ import annotations

import argparse
import csv
import importlib.util
import json
import logging
import socket
import sys
import types
import xml.etree.ElementTree as ET
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.CorpusLeakValidator import CorpusLeakValidator  # noqa: E402
from carlacontrol.CotDisplayConvention import CotDisplayConvention  # noqa: E402
from carlacontrol.ScenarioVehicleMix import (  # noqa: E402  (needs the path above)
    ScenarioVehicleMix,
    VehicleClassSpec,
    VehicleMixSpec,
)
from carlacontrol.SumoCotBridge import CotOutputSettings, SumoCotBridge  # noqa: E402
from carlacontrol.VehicleCatalogue import (  # noqa: E402
    BLUEPRINT_PARAM,
    CATALOGUE_DIGEST_PARAM,
    CLASS_PARAM,
    VehicleCatalogue,
)
from carlacontrol.version import __version__ as carlacontrol_version  # noqa: E402

SCENARIO = "Shahid_Bahonar_Port_PatternOfLife"
SPECIFICATION = _REPO / "Import" / f"{SCENARIO}.scenario.json"
CONVENTION = _REPO / "Import" / f"{SCENARIO}.display.json"
CATALOGUE = _REPO / "CarlaControl" / "catalogue" / "vehicles.catalogue.json"
FIXTURES = Path(__file__).resolve().parent / "fixtures"
SHIPPED_ROUTES = FIXTURES / f"{SCENARIO}.shipped.rou.xml"
SHIPPED_LABELS = FIXTURES / f"{SCENARIO}.shipped.labels.json"
SCRIPT = _REPO / "CarlaControl" / "scripts" / "sumo_cot_telemetry.py"
UID_PREFIX = "SUMO-TRUTH"

# A measured body standing in for any the catalogue has not measured, as the Bahonar generator's own
# test compiles it: the bodies do not decide a vehicle's class, which is what is checked here.
STAND_IN_BODY = "vehicle.carlacola.actors"

NEUTRAL, FRIEND = "a-n-G-E-V", "a-f-G-E-V"

# The field the pair check groups on: what an observer reads of a track's kind, with its CoT type.
KIND_AND_TYPE = "base_type+cot_type"


# ---- the scenario as TraCI would report it -------------------------------------------------------

class _TypeTable:
    """Vehicle types as TraCI reports them, with their `<param>`s, and their distributions."""

    DEFAULT_COLOUR = (255, 255, 0, 255)

    def __init__(self, routes_xml: str):
        root = ET.fromstring(f"<routes>{routes_xml}</routes>")
        self.types: dict[str, dict] = {}
        self.distributions: dict[str, list[str]] = {}
        for element in root.iter("vType"):
            self.types[element.get("id")] = {
                "vehicle_class": element.get("vClass", "passenger"),
                "color": self._colour(element.get("color")),
                "length": float(element.get("length", 5.0)),
                "width": float(element.get("width", 1.8)),
                "height": float(element.get("height", 1.5)),
                "params": {p.get("key"): p.get("value") for p in element.findall("param")},
            }
        for element in root.iter("vTypeDistribution"):
            self.distributions[element.get("id")] = element.get("vTypes").split()

    def members(self, name: str) -> list[str]:
        """The concrete types a declared type resolves to: SUMO draws one of these per vehicle."""
        return self.distributions.get(name, [name])

    @classmethod
    def _colour(cls, text: str | None) -> tuple[int, int, int, int]:
        if not text:
            return cls.DEFAULT_COLOUR
        if text.startswith("#"):
            return (int(text[1:3], 16), int(text[3:5], 16), int(text[5:7], 16), 255)
        red, green, blue = (round(float(v) * 255) for v in text.split(","))
        return (red, green, blue, 255)


class _Playback:
    """A stand-in for TraCI presenting a fixed roster for two steps, and counting type lookups."""

    STEPS = 2

    def __init__(self, roster: list[tuple[str, str]], table: _TypeTable):
        self.roster = dict(roster)
        self.order = {vehicle_id: i for i, (vehicle_id, _) in enumerate(roster)}
        self.table = table
        self.time = 0.0
        self.parameter_reads: list[tuple[str, str]] = []
        self.simulation = types.SimpleNamespace(
            getDeltaT=lambda: 1.0,
            getEndTime=lambda: float(self.STEPS),
            getMinExpectedNumber=lambda: 1 if self.time < self.STEPS else 0,
            getTime=lambda: self.time,
            convertGeo=lambda x, y: (56.18065 + x / 98_000.0, 27.15012 + y / 111_320.0),
        )
        self.vehicle = types.SimpleNamespace(
            getIDList=lambda: tuple(self.roster),
            getPosition=lambda v: (7.3 * self.order[v], 11.1 * self.order[v] + 40.0 * self.time),
            getTypeID=lambda v: self.roster[v],
            getSpeed=lambda v: 5.0 + self.order[v] % 11,
            getAngle=lambda v: (self.order[v] * 7) % 360,
            getLength=lambda v: self._type(v)["length"],
            getWidth=lambda v: self._type(v)["width"],
            getHeight=lambda v: self._type(v)["height"],
            getRoadID=lambda v: f"edge_{self.order[v] % 23}",
            getLaneID=lambda v: f"edge_{self.order[v] % 23}_0",
        )
        self.vehicletype = types.SimpleNamespace(
            getColor=lambda t: self.table.types[t]["color"],
            getVehicleClass=lambda t: self.table.types[t]["vehicle_class"],
            getParameter=self._parameter,
        )

    def _type(self, vehicle_id: str) -> dict:
        return self.table.types[self.roster[vehicle_id]]

    def _parameter(self, type_id: str, key: str) -> str:
        """TraCI's answer: the `<param>`'s value, or the empty string for one the type lacks."""
        self.parameter_reads.append((type_id, key))
        return self.table.types[type_id]["params"].get(key, "")

    def start(self, command):
        pass

    def simulationStep(self):  # noqa: N802  (TraCI's own spelling)
        self.time += 1.0

    def close(self):
        pass


class _Installation:
    """Stands in for a located SUMO so the bridge can run without one."""

    def __init__(self, traci):
        self._traci = traci

    def import_traci(self):
        return self._traci

    sumo = Path("sumo")
    sumo_gui = Path("sumo-gui")
    version = "1.27.0"


def _specification() -> dict:
    return json.loads(SPECIFICATION.read_text(encoding="utf-8"))


def _compiled_types(specification: dict) -> _TypeTable:
    """The specification's vehicle types exactly as the compiler writes them into the route file."""
    catalogue = VehicleCatalogue.load(CATALOGUE)
    measured = set(catalogue.blueprint_ids)
    classes = [VehicleClassSpec(
        class_id=c["class_id"], sumo_vclass=c["sumo_vclass"],
        blueprints=tuple(dict.fromkeys(b if b in measured else STAND_IN_BODY
                                       for b in c["blueprints"])),
        behaviour=dict(c.get("behaviour", {})), share=float(c.get("share", 0.0)),
        gui_shape=c.get("gui_shape", ""), gui_colour=c.get("gui_colour", ""))
        for c in specification["vehicle_classes"]]
    mixes = [VehicleMixSpec(mix_id=m["id"],
                            shares=tuple((k, float(v)) for k, v in m["shares"].items()))
             for m in specification["vehicle_mixes"]]
    return _TypeTable(ScenarioVehicleMix(catalogue, classes, mixes=mixes).to_xml())


def _compiled_roster(specification: dict, table: _TypeTable) -> list[tuple[str, str]]:
    """Every actor, one shift's sixteen guard postings, and every member type of every flow's mix.

    The check is over the set of values a field takes, which a handful of each type establishes; a
    week of flows would be hundreds of thousands of vehicles saying the same thing.
    """
    roster = []
    for index, actor in enumerate(specification["actors"]):
        members = table.members(actor["type"])
        roster.append((actor["id"], members[index % len(members)]))
    rota = specification["rotas"][0]
    guards = table.members(rota["template"]["type"])
    for tower in range(len(specification["place_sets"][rota["subjects"]["place_set"]])):
        roster.append((rota["id_pattern"].format(day=0, hour=7, subject_index=tower),
                       guards[tower % len(guards)]))
    sampled: set[str] = set()
    for flow in specification["flows"]:
        if flow["type"] in sampled:
            continue
        sampled.add(flow["type"])
        for n, type_id in enumerate(table.members(flow["type"]) * 2):
            roster.append((f"{flow['id']}.{n}", type_id))
    return roster


def _planted(specification: dict) -> frozenset[str]:
    """The vehicles the specification annotates: every participant of an annotated instance."""
    return frozenset(participant["actor"]
                     for instance in specification["supervision"]["instances"]
                     if instance["supervision"] == "annotated"
                     for participant in instance.get("participants", []))


def _shipped_roster() -> tuple[list[tuple[str, str]], _TypeTable]:
    """The sizing scenario as it shipped in SUMO XML: every trip, and each flow's member types."""
    root = ET.parse(SHIPPED_ROUTES).getroot()
    table = _TypeTable("".join(ET.tostring(e, encoding="unicode") for e in root
                               if e.tag in ("vType", "vTypeDistribution")))
    roster = [(e.get("id"), e.get("type")) for e in root if e.tag == "trip"]
    sampled: set[str] = set()
    for flow in (e for e in root if e.tag == "flow"):
        if flow.get("type") in sampled:
            continue
        sampled.add(flow.get("type"))
        for n, type_id in enumerate(table.members(flow.get("type")) * 2):
            roster.append((f"{flow.get('id')}.{n}", type_id))
    return roster, table


# ---- running the real writers --------------------------------------------------------------------

class _Listener:
    """A UDP socket on the loopback, standing where a TAK client would, keeping every datagram."""

    def __init__(self):
        self.socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.socket.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 8 * 1024 * 1024)
        self.socket.bind(("127.0.0.1", 0))
        self.port = self.socket.getsockname()[1]

    def drain(self) -> list[str]:
        self.socket.settimeout(0.5)
        received = []
        try:
            while True:
                received.append(self.socket.recv(65536).decode("utf-8"))
        except TimeoutError:
            pass
        finally:
            self.socket.close()
        return received


def _run(tmp_path: Path, roster, table, affiliation_by_type: dict[str, str],
         source: str = "", affiliation: str = "n", udp: bool = False,
         marked_ids: frozenset[str] = frozenset(), marked_affiliation: str | None = None,
         catalogue: VehicleCatalogue | None = None):
    """Drive the bridge over the roster; return the report, the rows, the XML and the datagrams."""
    playback = _Playback(roster, table)
    listener = _Listener() if udp else None
    csv_path, xml_path = tmp_path / "corpus.csv", tmp_path / "corpus.xml"
    report = SumoCotBridge(_Installation(playback), tmp_path / f"{SCENARIO}.sumocfg",
                           constant_hae=12.0, catalogue=catalogue).run(CotOutputSettings(
        csv_path=csv_path, xml_path=xml_path, uid_prefix=UID_PREFIX, marked_vehicle="",
        marked_ids=marked_ids, marked_affiliation=marked_affiliation,
        affiliation=affiliation, affiliation_by_type=affiliation_by_type,
        display_convention=source,
        udp_host="127.0.0.1" if udp else None, udp_port=listener.port if udp else 6969))
    with open(csv_path, newline="", encoding="utf-8") as handle:
        rows = list(csv.DictReader(handle))
    datagrams = listener.drain() if listener else []
    return report, rows, xml_path, datagrams, playback


def _xml_records(path: Path) -> list[dict]:
    return list(CorpusLeakValidator.records_from_xml(path))


def _datagram_records(tmp_path: Path, datagrams: list[str]) -> list[dict]:
    """The received datagrams as the validator reads an event file."""
    path = tmp_path / "datagrams.xml"
    path.write_text("<events>\n" + "\n".join(datagrams) + "\n</events>\n", encoding="utf-8")
    return _xml_records(path)


def _with_kind(records: list[dict]) -> list[dict]:
    """Each record with its observable kind and CoT type joined, so a pairing is one value."""
    return [dict(record, **{KIND_AND_TYPE: f"{record['base_type']}|{record['cot_type']}"})
            for record in records]


def _identifying(planted, records, field: str) -> list:
    validator = CorpusLeakValidator(planted, fields=(field,), uid_prefix=UID_PREFIX)
    return validator.identifying(validator.check_records(records))


def _cot_type_by_vehicle(rows: list[dict]) -> dict[str, str]:
    return {row["uid"].removeprefix(f"{UID_PREFIX}-"): row["cot_type"] for row in rows}


@pytest.fixture(scope="module")
def specification():
    return _specification()


@pytest.fixture(scope="module")
def compiled(specification):
    table = _compiled_types(specification)
    return _compiled_roster(specification, table), table


# ---- the convention file -------------------------------------------------------------------------

def test_the_bahonar_convention_names_every_class_the_specification_declares(specification):
    """Every class is drawn as its population is: civilian and port neutral, the base friendly.

    A class the file did not name would take the run's default silently, so a class added to the
    specification without a line here fails this instead.
    """
    convention = CotDisplayConvention.from_file(CONVENTION)
    assert convention.source == CONVENTION.name
    assert set(convention.affiliation_by_type) == {c["class_id"]
                                                   for c in specification["vehicle_classes"]}
    by_vclass: dict[str, set[str]] = {}
    for entry in specification["vehicle_classes"]:
        by_vclass.setdefault(entry["sumo_vclass"], set()).add(
            convention.affiliation_by_type[entry["class_id"]])
    # One affiliation per SUMO vehicle class: the army is friendly, everything else neutral.
    assert by_vclass == {"passenger": {"n"}, "taxi": {"n"}, "truck": {"n"}, "bus": {"n"},
                         "authority": {"n"}, "army": {"f"}}


@pytest.mark.parametrize("document, complaint", [
    ({"convention_version": 1, "affiliation_by_type": {"civ_car": "n"},
      "marked_ids": ["probe_d2"]}, "names planted vehicles"),
    ({"convention_version": 1, "affiliation_by_type": {"civ_car": "n"}, "anomaly_notes": []},
     "anomaly_notes"),
    ({"convention_version": 2, "affiliation_by_type": {"civ_car": "n"}}, "implements 1"),
    ({"convention_version": 1, "affiliation_by_type": {"civ_car": "neutral"}}, "'neutral'"),
    ({"convention_version": 1, "affiliation_by_type": {"civ_car": "N"}}, "'N'"),
    ({"convention_version": 1, "affiliation_by_type": ["civ_car", "n"]}, "no affiliation_by_type"),
])
def test_a_convention_names_populations_and_nothing_else(tmp_path, document, complaint):
    """Planted vehicles, a described gap or a letter no CoT type has: refused, not partly read."""
    path = tmp_path / "run.display.json"
    path.write_text(json.dumps(document), encoding="utf-8")
    with pytest.raises(ValueError, match=complaint):
        CotDisplayConvention.from_file(path)


# ---- applying it ---------------------------------------------------------------------------------

def test_a_compiled_vehicle_is_drawn_by_the_class_its_type_names(tmp_path):
    """The class comes from the type's `carla:class_id`, ahead of anything its id spells.

    Two bodies of one class take the class's letter even where the convention also names one body's
    type id otherwise; a hand-written type with no class parameter is looked up by its id; a class
    the convention does not name takes the default; and each type is asked once, not once a vehicle.
    """
    table = _TypeTable("""
        <vType id="mil_jeep.vehicle.nissan.patrol" vClass="army">
            <param key="carla:class_id" value="mil_jeep"/></vType>
        <vType id="mil_jeep.vehicle.jeep.wrangler_rubicon" vClass="army">
            <param key="carla:class_id" value="mil_jeep"/></vType>
        <vType id="tractor.vehicle.carlacola.actors" vClass="truck">
            <param key="carla:class_id" value="tractor"/></vType>
        <vType id="civ_car" vClass="passenger"/>""")
    roster = [("jeep_a", "mil_jeep.vehicle.nissan.patrol"),
              ("jeep_b", "mil_jeep.vehicle.jeep.wrangler_rubicon"),
              ("jeep_c", "mil_jeep.vehicle.nissan.patrol"),
              ("tractor", "tractor.vehicle.carlacola.actors"),
              ("saloon", "civ_car")]
    convention = {"mil_jeep": "f", "civ_car": "n", "mil_jeep.vehicle.nissan.patrol": "h"}
    _, rows, _, _, playback = _run(tmp_path, roster, table, convention, affiliation="o")

    assert _cot_type_by_vehicle(rows) == {
        "jeep_a": FRIEND, "jeep_b": FRIEND, "jeep_c": FRIEND,
        "tractor": "a-o-G-E-V", "saloon": NEUTRAL}
    assert sorted(playback.parameter_reads) == sorted(
        (type_id, CLASS_PARAM) for type_id in set(dict(roster).values()))


def test_a_run_without_a_convention_asks_nothing_and_draws_every_vehicle_the_default(tmp_path,
                                                                                   compiled):
    roster, table = compiled
    _, rows, _, _, playback = _run(tmp_path, roster, table, {})
    assert {row["cot_type"] for row in rows} == {NEUTRAL}
    assert playback.parameter_reads == []


def test_no_planted_vehicle_is_told_apart_by_its_cot_type_in_any_sink(tmp_path, specification,
                                                                     compiled):
    """The Bahonar convention over the Bahonar traffic: the CSV, the XML and the datagrams.

    Measured on each sink: no CoT type, and no pairing of the CoT type with the vehicle's observable
    base type, occurs among the ten annotated vehicles and nowhere else; both letters the
    convention uses reach the output, so the check is over a convention that was applied; and the
    XML says which convention drew it.
    """
    roster, table = compiled
    planted = _planted(specification)
    assert planted == {"escort_0", "escort_1", "escort_2", "escort_3", "escort_4",
                       "probe_d2", "probe_d5", "shadow", "staybehind", "offpost_d4_h7_t3"}
    assert planted <= {vehicle_id for vehicle_id, _ in roster}
    convention = CotDisplayConvention.from_file(CONVENTION)

    report, rows, xml_path, datagrams, _ = _run(
        tmp_path, roster, table, convention.affiliation_by_type, source=convention.source,
        udp=True)

    assert len(datagrams) == report.events == len(rows)
    sinks = {"csv": rows, "xml": _xml_records(xml_path),
             "udp": _datagram_records(tmp_path, datagrams)}
    for name, records in sinks.items():
        assert len(records) == report.events, name
        assert {record["cot_type"] for record in records} == {NEUTRAL, FRIEND}, name
        for field in ("cot_type", KIND_AND_TYPE):
            findings = _identifying(planted, _with_kind(records), field)
            assert findings == [], f"{name}: {CorpusLeakValidator.describe(findings)}"

    # Each planted vehicle is drawn as the population it moves among.
    drawn = _cot_type_by_vehicle(rows)
    assert {vehicle: drawn[vehicle] for vehicle in planted} == {
        "escort_0": FRIEND, "escort_1": FRIEND, "escort_2": FRIEND, "escort_3": FRIEND,
        "escort_4": FRIEND, "shadow": FRIEND, "offpost_d4_h7_t3": FRIEND,
        "probe_d2": NEUTRAL, "probe_d5": NEUTRAL, "staybehind": NEUTRAL}

    header = ET.parse(xml_path).getroot().find("_display_convention")
    assert header.get("source") == CONVENTION.name
    assert header.get("default_affiliation") == "n"
    assert {p.get("type"): p.get("affiliation") for p in header.findall("population")} == \
        convention.affiliation_by_type

    # Both files name their format and what made them: the XML on its container and first inside it,
    # the CSV in a summary beside it, so its header stays the columns every reader reads.
    events = ET.parse(xml_path).getroot()
    assert events.get("format_version") == "1"
    producer = events[0]
    assert producer.tag == "_producer"
    assert producer.get("tool") == "carlacontrol.SumoCotBridge"
    assert producer.get("tool_version") == carlacontrol_version
    assert producer.get("sumo") == "1.27.0" and producer.find("_server") is None
    summary = json.loads(SumoCotBridge.csv_summary_path(xml_path.with_suffix(".csv")).read_text("utf-8"))
    assert summary["format_version"] == 1 and summary["csv"] == "corpus.csv"
    assert summary["columns"] == list(rows[0]) and summary["producer"]["sumo"] == producer.get("sumo")


@pytest.mark.parametrize("crawl, field", [("u", "cot_type"), ("n", KIND_AND_TYPE)])
def test_the_check_finds_a_convention_that_draws_a_planted_class_apart(tmp_path, specification,
                                                                      compiled, crawl, field):
    """The perimeter crawl is the one class only the shadow drives, and its letter decides.

    Drawn `u` it is the only unknown track; drawn `n` its letter is shared with every civilian, yet
    it is the only army vehicle drawn neutral. Both are caught, which is why the convention draws it
    friendly, as the base's traffic is drawn.
    """
    roster, table = compiled
    convention = dict(CotDisplayConvention.from_file(CONVENTION).affiliation_by_type)
    convention["army_car_crawl"] = crawl
    _, rows, _, _, _ = _run(tmp_path, roster, table, convention)

    findings = _identifying(_planted(specification), _with_kind(rows), field)
    assert [finding.vehicles for finding in findings] == [("shadow",)], \
        CorpusLeakValidator.describe(findings)


# ---- a legacy labels file ------------------------------------------------------------------------

def test_a_legacy_labels_file_keeps_its_display_half_and_withholds_its_anomaly_half(tmp_path):
    """The shipped Bahonar labels, read as a display convention, no longer write the answer.

    Applied whole, their map draws exactly the nine planted vehicles `u`. Read here, the four
    anomaly types are withheld, every letter kept is a display affiliation, and the CoT type no
    longer names any planted vehicle. What the legacy scenario's own types still separate is
    measured too: its escort and shadow types are army and take the default, so the pairing of kind
    and CoT type still names them -- a property of the legacy route file, which gives them types of
    their own (06 §2.4), that a display convention naming those types closes.
    """
    labels = json.loads(SHIPPED_LABELS.read_text(encoding="utf-8"))
    planted = frozenset(labels["marked_ids"])
    roster, table = _shipped_roster()

    legacy = CotDisplayConvention.from_legacy_labels(labels, source=SHIPPED_LABELS.name)
    assert legacy.withheld == ("anomaly_escort", "anomaly_probe", "anomaly_shadow",
                               "anomaly_staybehind")
    assert len(legacy) == 10 and set(legacy.affiliation_by_type.values()) == {"n", "f"}

    _, whole, _, _, _ = _run(tmp_path, roster, table, labels["affiliation_by_type"])
    findings = _identifying(planted, whole, "cot_type")
    assert [(f.value, len(f.vehicles)) for f in findings] == [("a-u-G-E-V", 9)]

    _, rows, _, _, _ = _run(tmp_path, roster, table, legacy.affiliation_by_type)
    assert _identifying(planted, rows, "cot_type") == []
    residual = _identifying(planted, _with_kind(rows), KIND_AND_TYPE)
    assert [(f.value, f.vehicles) for f in residual] == [
        ("army|a-n-G-E-V", ("escort_0", "escort_1", "escort_2", "escort_3", "escort_4", "shadow"))]

    covered = dict(legacy.affiliation_by_type, anomaly_escort="f", anomaly_shadow="f")
    _, rows, _, _, _ = _run(tmp_path, roster, table, covered)
    assert _identifying(planted, _with_kind(rows), KIND_AND_TYPE) == []


def _script():
    spec = importlib.util.spec_from_file_location("sumo_cot_telemetry", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _arguments(config: Path, display_convention: Path | None = None,
               labels: Path | None = None) -> argparse.Namespace:
    """The arguments `display_convention` reads, as the script's parser would give them."""
    return argparse.Namespace(config=config, display_convention=display_convention, labels=labels,
                              affiliation="n")


def _write_convention(path: Path, affiliation_by_type: dict[str, str]) -> Path:
    path.write_text(json.dumps({"convention_version": 1,
                                "affiliation_by_type": affiliation_by_type}), encoding="utf-8")
    return path


def _info(caplog) -> str:
    return "\n".join(record.getMessage() for record in caplog.records
                     if record.levelno == logging.INFO)


def test_the_script_draws_with_the_convention_file_and_else_with_the_labels(tmp_path):
    """A convention file wins outright; labels alone give their display half; neither, none."""
    script = _script()
    labels = json.loads(SHIPPED_LABELS.read_text(encoding="utf-8"))
    # A configuration with nothing beside it, so only what is named is drawn with.
    config = tmp_path / f"{SCENARIO}.sumocfg"

    def chosen(display_convention, labels_path):
        return script.display_convention(_arguments(config, display_convention, labels_path),
                                          labels if labels_path else {})

    bahonar = CotDisplayConvention.from_file(CONVENTION).affiliation_by_type
    both = chosen(CONVENTION, SHIPPED_LABELS)
    assert (both.source, both.affiliation_by_type) == (CONVENTION.name, bahonar)

    alone = chosen(None, SHIPPED_LABELS)
    assert alone.source == SHIPPED_LABELS.name
    assert "u" not in alone.affiliation_by_type.values() and len(alone.withheld) == 4

    neither = chosen(None, None)
    assert (neither.source, neither.affiliation_by_type) == ("", {})


# ---- a scenario's own convention, found beside it ------------------------------------------------

def test_the_bahonar_run_finds_its_convention_beside_its_configuration(caplog):
    """No flag: the file beside the Bahonar `.sumocfg` draws the run, and the log names the file."""
    config = _REPO / "Import" / f"{SCENARIO}.sumocfg"
    assert CotDisplayConvention.beside(config) == CONVENTION

    with caplog.at_level(logging.INFO):
        found = _script().display_convention(_arguments(config), {})

    assert found.source == CONVENTION.name
    assert found.affiliation_by_type == \
        CotDisplayConvention.from_file(CONVENTION).affiliation_by_type
    assert f"display convention {CONVENTION}, found beside the configuration" in _info(caplog)


def test_a_found_convention_behaves_as_a_named_one(tmp_path, caplog):
    """A named file wins over a found one; a found one wins over labels, and is refused unread.

    Found or named, the convention is the one file the run draws with, so a labels file's display
    half gives way to it whichever way it arrived, and one that names a vehicle stops the run.
    """
    script = _script()
    labels = json.loads(SHIPPED_LABELS.read_text(encoding="utf-8"))
    config = tmp_path / "Port.sumocfg"
    found = _write_convention(tmp_path / "Port.display.json", {"civ_car": "h"})
    named = _write_convention(tmp_path / "exercise.display.json", {"civ_car": "f"})

    with caplog.at_level(logging.INFO):
        drawn = script.display_convention(_arguments(config, display_convention=named), {})
    assert (drawn.source, drawn.affiliation_by_type) == (named.name, {"civ_car": "f"})
    assert f"display convention {named}:" in _info(caplog)
    assert "found beside" not in _info(caplog)

    caplog.clear()
    with caplog.at_level(logging.INFO):
        drawn = script.display_convention(_arguments(config, labels=SHIPPED_LABELS), labels)
    assert (drawn.source, drawn.affiliation_by_type, drawn.withheld) == \
        (found.name, {"civ_car": "h"}, ())
    assert f"display convention {found}, found beside the configuration" in _info(caplog)
    assert f"it replaces the affiliations in {SHIPPED_LABELS.name}" in _info(caplog)

    found.write_text(json.dumps({"convention_version": 1, "affiliation_by_type": {"civ_car": "n"},
                                 "marked_ids": ["probe_d2"]}), encoding="utf-8")
    with pytest.raises(ValueError, match="names planted vehicles"):
        script.display_convention(_arguments(config), {})


def test_without_a_convention_the_run_says_none_was_found(tmp_path, caplog):
    """Nothing named and nothing beside: a labels file's display half, else the default for all."""
    script = _script()
    labels = json.loads(SHIPPED_LABELS.read_text(encoding="utf-8"))
    config = tmp_path / "Port.sumocfg"
    missing = "no display convention: none given and no Port.display.json beside the configuration"

    with caplog.at_level(logging.INFO):
        drawn = script.display_convention(_arguments(config), {})
    assert (drawn.source, drawn.affiliation_by_type) == ("", {})
    assert f"{missing}; every vehicle takes n" in _info(caplog)

    caplog.clear()
    with caplog.at_level(logging.INFO):
        drawn = script.display_convention(_arguments(config, labels=SHIPPED_LABELS), labels)
    assert drawn.source == SHIPPED_LABELS.name and len(drawn.withheld) == 4
    assert missing in _info(caplog)
    assert "every vehicle takes" not in _info(caplog)


# ---- the planted vehicles in each sink -----------------------------------------------------------

def _values(records: list[dict], field: str, vehicles=None) -> set[str]:
    """Every value a field takes across the records, `<absent>` where a record lacks it."""
    return {record.get(field, "<absent>") for record in records
            if vehicles is None or record["uid"].removeprefix(f"{UID_PREFIX}-") in vehicles}


@pytest.mark.parametrize("marked_affiliation", [None, "h"])
def test_a_legacy_planted_vehicle_is_marked_in_the_files_and_is_its_own_kind(tmp_path,
                                                                            marked_affiliation):
    """The shipped labels' nine: `marked` in the XML and CSV, and `special_type` never the answer.

    A planted vehicle's `special_type` is what any vehicle SUMO drives carries, in every sink. The
    XML and CSV say which vehicles were planted in `marked`, exactly the nine; the datagrams carry
    neither field. Its CoT type in the files is its population's, asked for or not, and on the
    datagrams it is the operator's `--marked-affiliation` only when one was asked for.
    """
    labels = json.loads(SHIPPED_LABELS.read_text(encoding="utf-8"))
    planted = frozenset(labels["marked_ids"])
    roster, table = _shipped_roster()
    convention = CotDisplayConvention.from_legacy_labels(labels, source=SHIPPED_LABELS.name)

    report, rows, xml_path, datagrams, _ = _run(
        tmp_path, roster, table, convention.affiliation_by_type, source=convention.source,
        udp=True, marked_ids=planted, marked_affiliation=marked_affiliation)

    assert report.marked_vehicles == len(planted) == 9
    files = {"csv": rows, "xml": _xml_records(xml_path)}
    udp = _datagram_records(tmp_path, datagrams)
    for name, records in files.items():
        assert len(records) == report.events, name
        assert _values(records, "special_type") == {""}, name
        assert {record["uid"].removeprefix(f"{UID_PREFIX}-") for record in records
                if record["marked"] == "1"} == planted, name
        assert _values(records, "cot_type", planted) == {NEUTRAL}, name
    assert len(udp) == report.events
    assert _values(udp, "special_type") == _values(udp, "marked") == {"<absent>"}
    shown = "a-h-G-E-V" if marked_affiliation else NEUTRAL
    assert _values(udp, "cot_type", planted) == {shown}
    assert shown == NEUTRAL or shown not in _values(
        udp, "cot_type", {vehicle_id for vehicle_id, _ in roster} - planted)


def test_a_compiled_scenario_marks_nothing_and_its_plan_names_the_planted_by_id(tmp_path,
                                                                                specification,
                                                                                compiled):
    """Bahonar compiled: no vehicle marked, no kind, and every planted one found by its plan's id.

    A compiled scenario writes no labels file, so the sidecar sets `marked` on nothing and
    `special_type` is empty for all. Its labels are in `*.supervision.json`: each annotated
    participant's SUMO id is a vehicle the sidecar carries, so the plan joins to the rows by id and
    nothing the labels say is lost.
    """
    roster, table = compiled
    convention = CotDisplayConvention.from_file(CONVENTION)
    report, rows, xml_path, datagrams, _ = _run(
        tmp_path, roster, table, convention.affiliation_by_type, source=convention.source,
        udp=True)

    assert report.marked_vehicles == 0
    for name, records in {"csv": rows, "xml": _xml_records(xml_path)}.items():
        assert _values(records, "special_type") == {""}, name
        assert _values(records, "marked") == {"0"}, name
    udp = _datagram_records(tmp_path, datagrams)
    assert _values(udp, "special_type") == _values(udp, "marked") == {"<absent>"}

    plan = json.loads((_REPO / "Import" / f"{SCENARIO}.supervision.json").read_text(
        encoding="utf-8"))
    annotated = {participant["sumo_id"] for instance in plan["instances"]
                 if instance["supervision"] == "annotated"
                 for participant in instance.get("participants", [])}
    assert annotated == _planted(specification)
    assert annotated <= {row["uid"].removeprefix(f"{UID_PREFIX}-") for row in rows}


# ---- the kinds, from the catalogue ---------------------------------------------------------------

def _kinds(records: list[dict]) -> dict[str, tuple[str, str]]:
    """Each vehicle's `base_type` and `special_type`, by its id."""
    return {record["uid"].removeprefix(f"{UID_PREFIX}-"): (record["base_type"], record["special_type"])
            for record in records}


def test_a_compiled_vehicle_s_kinds_are_the_catalogue_s_for_its_blueprint(tmp_path, caplog,
                                                                         specification, compiled):
    """Bahonar compiled, the bridge given the catalogue: each vehicle carries its blueprint's kinds.

    In the XML and the CSV every vehicle's `base_type` and `special_type` are the ones the catalogue
    curates for the blueprint its type names -- `taxi` for the cabs and no kind for every other body
    Bahonar draws -- so the army's and the port authority's classes, which SUMO's vehicle classes
    would report as `army` and `authority`, are the cars and lorries their bodies are. No base type,
    no kind and no pairing of the base type with the CoT type is carried by the planted vehicles
    alone. The callsign is built from the catalogue's base type, the datagrams carry no kind,
    nothing is marked, each type's blueprint is asked for once, and types written from this
    catalogue draw no warning.
    """
    roster, table = compiled
    catalogue = VehicleCatalogue.load(CATALOGUE)
    with caplog.at_level(logging.WARNING):
        report, rows, xml_path, datagrams, playback = _run(
            tmp_path, roster, table, {}, udp=True, catalogue=catalogue)

    blueprint_of = {type_id: entry["params"].get(BLUEPRINT_PARAM, "")
                    for type_id, entry in table.types.items()}
    expected = {vehicle_id: (catalogue.base_type_of(blueprint_of[type_id]),
                             catalogue.special_type_of(blueprint_of[type_id]) or "")
                for vehicle_id, type_id in roster}
    assert {kind for _, kind in expected.values()} == {"", "taxi"}
    assert {base for base, _ in expected.values()} == {"car", "truck", "bus"}
    # An escort's army jeep and a convoy's army lorry, and the planted shadow's army saloon.
    assert (expected["escort_0"], expected["haul_d0_0"], expected["shadow"]) == \
        (("car", ""), ("truck", ""), ("car", ""))
    planted = _planted(specification)
    for name, records in {"csv": rows, "xml": _xml_records(xml_path)}.items():
        assert len(records) == report.events, name
        assert _kinds(records) == expected, name
        for field in ("base_type", "special_type", KIND_AND_TYPE):
            findings = _identifying(planted, _with_kind(records), field)
            assert findings == [], f"{name}: {CorpusLeakValidator.describe(findings)}"
        assert _values(records, "marked") == {"0"}, name
    assert all(row["callsign"] == f"{row['base_type']}-{row['uid'].removeprefix(f'{UID_PREFIX}-')}"
               for row in rows)
    assert _values(_datagram_records(tmp_path, datagrams), "special_type") == {"<absent>"}
    assert sorted(read for read in playback.parameter_reads if read[1] == BLUEPRINT_PARAM) == \
        sorted((type_id, BLUEPRINT_PARAM) for type_id in set(dict(roster).values()))
    assert [r.getMessage() for r in caplog.records if r.levelno == logging.WARNING] == []


def _hand_written_types(digest: str) -> _TypeTable:
    """The emergency and authority bodies SUMO's classes misname, an army jeep, cabs, a blueprint no
    class draws, and types naming none."""
    return _TypeTable(f"""
        <vType id="ambulance.vehicle.ambulance.ford" vClass="emergency">
            <param key="{BLUEPRINT_PARAM}" value="vehicle.ambulance.ford"/>
            <param key="{CATALOGUE_DIGEST_PARAM}" value="{digest}"/></vType>
        <vType id="fire.vehicle.firetruck.actors" vClass="emergency">
            <param key="{BLUEPRINT_PARAM}" value="vehicle.firetruck.actors"/></vType>
        <vType id="police.vehicle.dodgecop.charger" vClass="authority">
            <param key="{BLUEPRINT_PARAM}" value="vehicle.dodgecop.charger"/></vType>
        <vType id="mil_jeep.vehicle.jeep.wrangler_rubicon" vClass="army">
            <param key="{BLUEPRINT_PARAM}" value="vehicle.jeep.wrangler_rubicon"/></vType>
        <vType id="cab.vehicle.taxi.ford" vClass="taxi">
            <param key="{BLUEPRINT_PARAM}" value="vehicle.taxi.ford"/>
            <param key="{CATALOGUE_DIGEST_PARAM}" value="0ldcatalogue"/></vType>
        <vType id="cab.vehicle.ue4.ford.crown" vClass="taxi">
            <param key="{BLUEPRINT_PARAM}" value="vehicle.ue4.ford.crown"/>
            <param key="{CATALOGUE_DIGEST_PARAM}" value="0ldcatalogue"/></vType>
        <vType id="microcar.vehicle.bmw.isetta" vClass="passenger">
            <param key="{BLUEPRINT_PARAM}" value="vehicle.bmw.isetta"/></vType>
        <vType id="convoy" vClass="army"/>
        <vType id="civ_car" vClass="passenger"/>""")


HAND_WRITTEN_ROSTER = [
    ("ambulance", "ambulance.vehicle.ambulance.ford"), ("fire", "fire.vehicle.firetruck.actors"),
    ("police", "police.vehicle.dodgecop.charger"), ("jeep", "mil_jeep.vehicle.jeep.wrangler_rubicon"),
    ("cab_a", "cab.vehicle.taxi.ford"), ("cab_b", "cab.vehicle.ue4.ford.crown"),
    ("isetta", "microcar.vehicle.bmw.isetta"), ("convoy", "convoy"), ("saloon", "civ_car")]


def test_only_a_blueprint_the_catalogue_curates_takes_its_kinds_and_another_catalogue_is_named(
        tmp_path, caplog):
    """The catalogue decides the base type and the kind; a type from another catalogue is named.

    The ambulance is a van and the fire appliance a truck, where their `emergency` class reads as a
    car; the police car a car, where its `authority` class reads as nothing the contract knows; the
    army jeep a car, where `army` reads as itself; and the three emergency bodies carry the
    emergency kind. A cab type written from an older catalogue that drew the Crown as a taxi carries
    what this catalogue gives the Crown, a car of no kind. A type naming a blueprint no class draws,
    and hand-written types naming none, take their vehicle class's base type -- `army` included,
    which nothing maps -- and no kind. The two types from the older catalogue draw one warning naming
    both digests, and the type from this one draws none.
    """
    digest = VehicleCatalogue.load(CATALOGUE).catalogue_digest
    with caplog.at_level(logging.WARNING):
        _, rows, xml_path, _, _ = _run(tmp_path, HAND_WRITTEN_ROSTER, _hand_written_types(digest),
                                       {}, catalogue=VehicleCatalogue.load(CATALOGUE))

    expected = {
        "ambulance": ("van", "emergency"), "fire": ("truck", "emergency"),
        "police": ("car", "emergency"), "jeep": ("car", ""),
        "cab_a": ("car", "taxi"), "cab_b": ("car", ""),
        "isetta": ("car", ""), "convoy": ("army", ""), "saloon": ("car", "")}
    assert _kinds(rows) == _kinds(_xml_records(xml_path)) == expected
    assert {row["callsign"] for row in rows if row["uid"].endswith("-ambulance")} == \
        {"van-ambulance"}
    warnings = [r.getMessage() for r in caplog.records if r.levelno == logging.WARNING]
    assert len(warnings) == 1
    assert "0ldcatalogue" in warnings[0] and digest in warnings[0]


def test_a_bridge_given_no_catalogue_reads_every_base_type_from_the_vehicle_class(tmp_path):
    """The control: without a catalogue the same types are what their SUMO classes say, and no kind.

    This is what the catalogue corrects -- the ambulance and the fire appliance read as cars, the
    police car as `authority` and the army jeep as `army` -- and no type is asked for a parameter.
    """
    _, rows, _, _, playback = _run(tmp_path, HAND_WRITTEN_ROSTER, _hand_written_types(""), {})

    assert _kinds(rows) == {
        "ambulance": ("car", ""), "fire": ("car", ""), "police": ("authority", ""),
        "jeep": ("army", ""), "cab_a": ("car", ""), "cab_b": ("car", ""),
        "isetta": ("car", ""), "convoy": ("army", ""), "saloon": ("car", "")}
    assert playback.parameter_reads == []


def test_the_script_reads_kinds_from_the_catalogue_it_is_given_or_else_this_repository_s(tmp_path,
                                                                                      caplog):
    """A named catalogue, else the repository's, else none; one that does not read stops the run."""
    script = _script()
    with caplog.at_level(logging.INFO):
        found = script.vehicle_catalogue(argparse.Namespace(catalogue=None))
    assert found.catalogue_digest == VehicleCatalogue.load(CATALOGUE).catalogue_digest
    assert f"vehicle kinds from catalogue {CATALOGUE}" in _info(caplog)

    named = tmp_path / "other.catalogue.json"
    named.write_text(json.dumps({"catalogue_version": 1, "catalogue_id": "other", "classes": []}),
                     encoding="utf-8")
    assert script.vehicle_catalogue(argparse.Namespace(catalogue=named)).catalogue_id == "other"

    with pytest.raises(ValueError, match="cannot be read"):
        script.vehicle_catalogue(argparse.Namespace(catalogue=tmp_path / "missing.json"))

    script.DEFAULT_CATALOGUE = tmp_path / "no.catalogue.json"
    caplog.clear()
    with caplog.at_level(logging.INFO):
        assert script.vehicle_catalogue(argparse.Namespace(catalogue=None)) is None
    assert "no vehicle catalogue: every vehicle's special_type is empty" in _info(caplog)
