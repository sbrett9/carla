"""The published schemas of what a capture writes accept everything the writers write, and nothing else.

The schemas under `CarlaControl/schemas/` -- the truth sidecar's XSD, one JSON Schema per PNG text chunk,
the run manifest's, the world truth track's Table Schema and its summary's -- are generated from the
CarlaNet writers' own constants and word lists, and `CaptureSchemaPublicationTests` holds each file equal to
what the writers generate. Held here, with full validators (lxml for the XSD, jsonschema for JSON Schema):

* every schema is a schema, named by a URN of its own and the format version it describes;
* the writers' output validates: the manifests, tracks and summaries of real runs against a real SUMO
  (`fixtures/capture_schemas/`, recorded by `CaptureSchemaWriterTests`), the chunk JSON and a sidecar
  written by the CarlaNet writers themselves, and the manifest rows no recorded run writes in the exact
  shape their writer gives them;
* files written before the producer record and the format versions -- the fixture manifests of
  2026-10-06, and two stills, a track and its summary from a capture of 2026-10-07 -- validate as
  format version 1 (version 2 for the track, whose summary always carried its version);
* what the writers never write is refused: an unknown word, an unknown field, a newer format version.
"""
from __future__ import annotations

import copy
import json
import sys
from pathlib import Path
from types import SimpleNamespace

import jsonschema
import pytest
from lxml import etree

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.CaptureSchemaSet import CaptureSchemaSet  # noqa: E402
from carlacontrol.CaptureValidator import CaptureValidator  # noqa: E402
from carlacontrol.PngTextChunks import PngTextChunks  # noqa: E402
from carlacontrol.TableSchemaCheck import TableSchemaCheck  # noqa: E402

SCHEMAS = _REPO / "CarlaControl" / "schemas"
FIXTURES = Path(__file__).resolve().parent / "fixtures"
RECORDED = FIXTURES / "capture_schemas"
LEGACY_MANIFESTS = sorted((FIXTURES / "run_manifests").glob("*.jsonl"))
URN_PREFIX = "urn:carla-sumo-capture:schema:"
CAPTURE_FILES = ["truth_sidecar.xsd", "png_chunk_capture.schema.json", "png_chunk_solar.schema.json",
                 "png_chunk_illumination.schema.json", "png_chunk_sensor.schema.json",
                 "run_manifest.schema.json", "world_truth_track.tableschema.json",
                 "world_truth_track_summary.schema.json"]
ROW_KINDS = ["manifest_opened", "instance", "series", "cohort", "sensor_placed", "render_admitted",
             "render_released", "collision_began", "collision_ended", "vehicle_not_inserted",
             "emergency_stop", "teleport", "solar_window_open", "solar_window_end", "interval_opened",
             "interval_closed", "supervision_defect", "manifest_closed"]


@pytest.fixture(scope="module")
def schemas() -> CaptureSchemaSet:
    return CaptureSchemaSet(SCHEMAS)


def manifest_rows(path: Path) -> list[dict]:
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line]


def problems(validator, value) -> list[str]:
    return [f"{error.json_path}: {error.message}" for error in validator.iter_errors(value)]


# ---- the schemas themselves -------------------------------------------------------------------------

def test_every_capture_schema_is_published_named_by_a_urn_and_states_its_format_version(schemas):
    for name in CAPTURE_FILES:
        assert (SCHEMAS / name).is_file(), name
    for name in CAPTURE_FILES[1:]:
        document = json.loads((SCHEMAS / name).read_text(encoding="utf-8"))
        assert document["$id"].startswith(URN_PREFIX), name
        assert "format version" in document["title"], name
    xsd = etree.parse(str(SCHEMAS / "truth_sidecar.xsd")).getroot()
    assert xsd.get("targetNamespace") is None
    assert xsd.get("version") == "1"
    assert xsd.find(".//{urn:carla-sumo-capture:schema:annotation}id").text == \
        "urn:carla-sumo-capture:schema:truth-sidecar:1"
    assert set(schemas.chunks) == {"carla:capture", "carla:solar", "carla:illumination", "carla:sensor"}
    assert (schemas.sidecar_version, schemas.manifest_version, schemas.summary_version) == (1, 1, 2)


def test_the_manifest_schema_names_every_kind_of_row_and_the_track_schema_every_column(schemas):
    manifest = schemas.manifest.schema
    assert manifest["properties"]["row"]["enum"] == ROW_KINDS
    assert sorted(manifest["$defs"]) == sorted([*ROW_KINDS, "producer", "server", "camera_exposure",
                                                "illumination_policy", "worst_divergence",
                                                "interval_triple"])
    summary = schemas.summary.schema
    assert [field["name"] for field in schemas.track_schema["fields"]] == \
        summary["properties"]["columns"]["const"]
    assert all("description" in field for field in schemas.track_schema["fields"])


def test_the_sidecar_schema_marks_the_attributes_only_a_vehicle_in_the_picture_carries_and_gives_units():
    ns = {"xs": "http://www.w3.org/2001/XMLSchema", "cap": "urn:carla-sumo-capture:schema:annotation"}
    xsd = etree.parse(str(SCHEMAS / "truth_sidecar.xsd"))
    carla = xsd.find("xs:complexType[@name='Carla']", ns)
    marked = {attribute.get("name") for attribute in carla.findall("xs:attribute", ns)
              if attribute.find(".//cap:onlyInPicture", ns) is not None}
    assert marked == {"pitch_deg", "roll_deg", "box_px", "box_oriented_px", "truncation", "lights",
                      "pose_source"}
    box = xsd.find(".//xs:element[@name='_box3d']", ns)
    assert box.find(".//cap:onlyInPicture", ns).text == "true"
    units = {attribute.get("name"): attribute.find(".//cap:unit", ns).text
             for attribute in carla.findall("xs:attribute", ns) if attribute.find(".//cap:unit", ns) is not None}
    assert units["length_m"] == "meters" and units["heading_deg"] == "degrees"
    assert units["box_px"] == "pixels" and units["vx"] == "meters per second"


# ---- real runs, recorded ----------------------------------------------------------------------------

@pytest.mark.parametrize("run", ["drawn", "supervised", "teleport", "defect", "stopped"])
def test_a_recorded_run_s_manifest_track_and_summary_keep_their_schemas(schemas, run):
    result = CaptureValidator(schemas).validate(RECORDED / run)
    assert result.ok, "\n".join(failure.describe(RECORDED) for failure in result.failures[:20])
    assert result.checked["run manifests"] == 1
    assert not result.notes


def test_the_recorded_runs_and_the_older_manifests_write_every_kind_of_row_but_the_four_no_run_writes():
    seen = set()
    for path in [*RECORDED.rglob("manifest.jsonl"), *LEGACY_MANIFESTS]:
        seen |= {row["row"] for row in manifest_rows(path)}
    assert set(ROW_KINDS) - seen == {"collision_began", "collision_ended", "vehicle_not_inserted",
                                     "emergency_stop"}


def test_the_rows_no_recorded_run_writes_keep_the_schema_in_the_shape_their_writer_gives_them(schemas):
    collision = {"collider": "follower", "victim": "leader", "collider_vtype_id": "car.vehicle.audi.tt",
                 "victim_vtype_id": "truck.vehicle.carlamotors.carlacola", "kind": "collision",
                 "lane": "approach_0", "lane_pos_m": 41.5}
    rows = [
        {"row": "collision_began", "sim_time_s": 12.5, **collision},
        {"row": "collision_ended", "sim_time_s": 14.0, **collision, "began_s": 12.5, "ended_s": 14.0,
         "collider_actor_id": 41, "victim_actor_id": None},
        {"row": "vehicle_not_inserted", "sim_time_s": 900.05, "sumo_id": "late.0", "waiting_at_s": 899.95},
        {"row": "emergency_stop", "sim_time_s": 33.0, "sumo_id": "brake.1"},
    ]
    for row in rows:
        assert not problems(schemas.manifest, row), row["row"]


def test_manifests_written_before_the_producer_record_keep_the_schema(schemas):
    assert LEGACY_MANIFESTS
    for path in LEGACY_MANIFESTS:
        rows = manifest_rows(path)
        assert "producer" not in rows[0]
        for row in rows:
            assert not problems(schemas.manifest, row), (path.name, row["row"])


def test_stills_and_a_track_written_before_the_format_versions_keep_their_schemas(schemas):
    result = CaptureValidator(schemas).validate(RECORDED / "legacy")
    assert result.ok, "\n".join(failure.describe(RECORDED) for failure in result.failures[:20])
    assert (result.checked["truth sidecars"], result.checked["PNG text chunks"]) == (2, 2)
    for sidecar in (RECORDED / "legacy").rglob("*.xml"):
        assert etree.parse(str(sidecar)).getroot().get("format_version") is None
    for still in (RECORDED / "legacy").rglob("*.png"):
        assert all("format_version" not in json.loads(text) for _, text in PngTextChunks.read(still))

    track = RECORDED / "legacy_track"
    assert not list(TableSchemaCheck(schemas.track_schema).problems(track / "world_truth_track_head.csv"))
    summary = json.loads((track / "world_truth_track.summary.json").read_text(encoding="utf-8"))
    assert "producer" not in summary
    assert not problems(schemas.summary, summary)


# ---- the CarlaNet writers' own output ---------------------------------------------------------------

@pytest.fixture(scope="module")
def clr():
    """The CarlaNet writers and the .NET types their calls take, for the writers' own output."""
    pytest.importorskip("carlanet", reason="the writers are the CarlaNet assemblies")
    # Imported here: these namespaces exist only once the carlanet shim has loaded the assemblies.
    import System
    import System.Collections.Generic as Generic
    from CarlaNet import Recording
    from CarlaNet.Types import Provenance
    return SimpleNamespace(Recording=Recording, Provenance=Provenance, System=System, Generic=Generic)


def test_every_chunk_the_writers_write_keeps_its_schema(schemas, clr):
    recording, provenance, system = clr.Recording, clr.Provenance, clr.System
    producer = provenance.Producer.Now(provenance.ServerBuildIdentity.NotAnswered("built before the call"),
                                       "1.27.0")
    identity = recording.CaptureIdentity(23674, 194.179475, "cap-1", "Arapahoe_I25",
                                         system.Nullable[system.Int64](42))
    identity.Producer = producer
    chunks = {"carla:capture": [identity.ToJson(), recording.CaptureIdentity(0, 0.0).ToJson()]}
    sun = [7.45, 2026, 9, 29, -6, 39.59431, -104.88449, 5.549, 97.827, 0, 0, 5.694]
    chunks["carla:solar"] = [recording.SolarMetadata.ToJson(system.Array[system.Double](sun)),
                             recording.SolarMetadata.ToJson(system.Array[system.Double](sun[:11]))]
    declaration = recording.IlluminationDeclaration("freeze_at_window_start", True, True)
    declaration.EpochDigest = "b" * 64
    declaration.DeclaredCivil = "2026-09-29T07:27:00-06:00"
    declaration.DeclaredUtc = "2026-09-29T13:27:00Z"
    chunks["carla:illumination"] = [declaration.ToJson()]
    pose = recording.SensorPose("a-f-A-M-F-Q", "Check_Overhead_1", "CARLA-SENSOR-107", 39.5971345,
                                -104.8891363, 1818.06, -0.63, 90.0, -70.346, 0.0, 90.0, 0.0, 1920, 1080,
                                2058.73, 2058.73, 960.0, 540.0, 50.0, 29.395, "sensor.camera.rgb",
                                "pinhole", "none", None)
    chunks["carla:sensor"] = [recording.SensorMetadata.ToJson(pose)]
    for keyword, texts in chunks.items():
        for text in texts:
            value = json.loads(str(text))
            assert not problems(schemas.chunks[keyword], value), (keyword, str(text))
    assert json.loads(str(identity.ToJson()))["format_version"] == 1


def test_a_sidecar_the_writer_writes_keeps_the_schema(schemas, clr, tmp_path):
    recording, provenance, system = clr.Recording, clr.Provenance, clr.System
    record = recording.VehicleTelemetry(7, "vehicle.audi.tt", "car", "", "0,0,0", "sumo", 39.5973969,
                                        -104.8889602, 1735.95, 1735.9, 16.43, 180.4, -0.11, 16.43, 0.07,
                                        4.18, 1.99, 1.39)
    records = clr.Generic.List[recording.VehicleTelemetry]()
    records.Add(record)
    path = tmp_path / "Camera_1_2026.10.07_10.34.49.411.xml"
    producer = provenance.Producer.Now(provenance.ServerBuildIdentity.NotAnswered("built before the call"),
                                       None)
    captured = system.DateTime(2026, 10, 7, 17, 34, 49, 411, system.DateTimeKind.Utc)
    recording.CotWriter.WriteToFile(str(path), captured, records, "n", 3.0, None, None,
                                    recording.CaptureIdentity(23674, 194.179475, "cap-1"), None,
                                    recording.SidecarVehicles.World, None, None, producer)
    document = etree.parse(str(path))
    assert document.getroot().get("format_version") == "1"
    assert schemas.sidecar.validate(document), schemas.sidecar.error_log


# ---- refusals ----------------------------------------------------------------------------------------

def test_what_the_writers_never_write_is_refused(schemas):
    sidecar = (RECORDED / "legacy" / "Check_Overhead_1" / "Check_Overhead_1_2026.10.07_10.34.51.318.xml") \
        .read_text(encoding="utf-8")
    for written, altered in [('in_frame="wholly"', 'in_frame="maybe"'),
                             ('pose_source="sumo"', 'pose_source="guessed"'),
                             ('<events ', '<events format_version="2" '),
                             ('role_name="sumo"', 'role_name="sumo" seen="true"')]:
        assert written in sidecar
        changed = etree.fromstring(sidecar.replace(written, altered, 1).encode("utf-8"))
        assert not schemas.sidecar.validate(etree.ElementTree(changed)), altered

    opened = manifest_rows(RECORDED / "drawn" / "manifest.jsonl")[0]
    for change in (lambda row: row.update(manifest_version=2), lambda row: row.update(seen=True),
                   lambda row: row["vehicle_lights"].update(brake_lights="always"),
                   lambda row: row["producer"].pop("carlanet")):
        row = copy.deepcopy(opened)
        change(row)
        assert problems(schemas.manifest, row)
    assert problems(schemas.manifest, {"row": "render_admitted", "sim_time_s": 1.0, "sumo_id": "a",
                                       "vtype_id": "car", "reason": "spawned", "frame": 1, "actor_id": None})
    assert problems(schemas.manifest, {"row": "arrived"})

    capture = {"tick": 1, "sim_time_s": 0.05}
    assert not problems(schemas.chunks["carla:capture"], capture)
    for change in ({"format_version": 2}, {"frame": 1}, {"tick": -1}):
        assert problems(schemas.chunks["carla:capture"], {**capture, **change}), change

    with pytest.raises(jsonschema.SchemaError):
        jsonschema.Draft202012Validator.check_schema({"type": "no-such-type"})
