"""Every file our tools write says what made it, in one shape, and a reader holds its format version.

`ProducerRecord` builds the record the Python writers carry -- the tool and its release, the carlanet
release, the server's build identity where a server was used, the SUMO release where SUMO ran, and the
time -- in the shape the CarlaNet writers give it (`CarlaNet.Types.Provenance.ProducerRecord`), and
tells those writers which tool runs them. `FormatVersion` is the rule each reader holds a file to: a
version it knows is read, a file from before versions is version 1, a newer one is refused by name.

Asserted:
  * the record's keys, in order, with what was not used null and a file written alike twice untimed;
  * its XML element, every value an attribute and the server a child, as the sidecar writer gives it;
  * the server's identity from a client that answers, one built before the call, and one that fails;
  * a declared tool reaches the CarlaNet writers, and the carlanet release is their assemblies';
  * the format rule's three outcomes and its words.
"""
from __future__ import annotations

import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from lxml import etree  # noqa: E402

from carlacontrol.FormatVersion import FormatVersion, FormatVersionError  # noqa: E402
from carlacontrol.ProducerRecord import ProducerRecord  # noqa: E402
from carlacontrol.version import __version__  # noqa: E402

SIDECAR_SCHEMA = _REPO / "CarlaControl" / "schemas" / "truth_sidecar.xsd"
# A still's sidecar written before sidecars carried the record of what made them.
LEGACY_SIDECAR = (Path(__file__).resolve().parent / "fixtures" / "capture_schemas" / "legacy" /
                  "Check_Overhead_1" / "Check_Overhead_1_2026.10.07_10.34.51.318.xml")

PACKAGE = {"available": True, "release": "0.10.0", "world_interface": "1.0", "build": "package",
           "configuration": "Shipping", "carla_commit": "025443a83eaf1bb82f18795d608fca50eb77a452",
           "content_commit": "6bcd042a91a54d9a2f2f002869fbf1c75f3768f4",
           "engine_commit": "e5e266de195a2400a6a74180402fb1a3e8f75472", "commits_from": "version_file"}


def test_the_record_names_the_tool_its_release_carlanet_the_server_sumo_and_the_time_in_one_order():
    record = ProducerRecord.record("carlacontrol.CaptureSession", server=PACKAGE, sumo="1.27.0")

    assert list(record) == ["tool", "tool_version", "carlanet", "server", "sumo", "written_utc"]
    assert record["tool"] == "carlacontrol.CaptureSession"
    assert record["tool_version"] == __version__
    assert record["server"] == PACKAGE and record["server"] is not PACKAGE
    assert record["sumo"] == "1.27.0"
    # To the millisecond, as the C# writers stamp it.
    assert re.fullmatch(r"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z", record["written_utc"])


def test_what_was_not_used_is_null_and_a_file_written_alike_twice_carries_no_time():
    record = ProducerRecord.record("carlacontrol.ScenarioCompiler", timed=False)

    assert record["server"] is None and record["sumo"] is None
    assert "written_utc" not in record
    assert json.loads(json.dumps(record)) == record


def test_the_xml_record_is_a_producer_element_with_the_server_inside_and_nothing_unused():
    record = ProducerRecord.record("carlacontrol.SumoCotBridge", sumo="1.27.0",
                                   server=ProducerRecord.unavailable("built before the call", "0.10.0"))
    record["carlanet"] = None

    element = ET.fromstring(ET.tostring(ProducerRecord.xml_element(record), encoding="unicode"))

    assert element.tag == "_producer"
    assert element.get("tool") == "carlacontrol.SumoCotBridge"
    assert element.get("tool_version") == __version__
    assert element.get("sumo") == "1.27.0"
    assert element.get("carlanet") is None
    server = element.find("_server")
    assert server.attrib == {"available": "false", "release": "0.10.0", "world_interface": "unknown",
                             "reason": "built before the call"}


class _Answering:
    def get_build_identity(self) -> dict:
        return dict(PACKAGE)


class _Older:
    """A carlanet client from before the call: it has the two older ones."""

    @staticmethod
    def get_server_version() -> str:
        return "0.10.0"

    @staticmethod
    def get_world_interface_version() -> str:
        return "1.0"


class _Failing:
    def get_build_identity(self) -> dict:
        raise RuntimeError("the server went away")


def test_the_server_identity_is_the_client_s_answer_or_said_to_be_unavailable_and_never_raised():
    assert ProducerRecord.server_identity(None) is None
    assert ProducerRecord.server_identity(_Answering()) == PACKAGE
    assert ProducerRecord.server_identity(_Older()) == {
        "available": False, "release": "0.10.0", "world_interface": "1.0",
        "reason": "the carlanet client offers no get_build_identity: it was built before the call"}
    failed = ProducerRecord.server_identity(_Failing())
    assert failed["available"] is False and "the server went away" in failed["reason"]
    assert failed["release"] == "unknown"


def test_a_declared_tool_reaches_the_carlanet_writers_and_carlanet_is_their_release():
    carlanet = pytest.importorskip("carlanet", reason="the CarlaNet writers need the carlanet shim")
    try:
        from CarlaNet.Types.Provenance import Producer
    except ImportError:
        pytest.skip("the loaded CarlaNet.Types predates the record; set CARLANET_PUBLISH_DIR to a "
                    "fresh publish")
    assert carlanet is not None
    try:
        ProducerRecord.declare_tool("carlacontrol.TestTool")
        declared = Producer.Tool
        assert (str(declared.Item1), str(declared.Item2)) == ("carlacontrol.TestTool", __version__)
        written = json.loads(str(Producer.Now(None, "1.27.0").ToJson()))
        assert written["tool"] == "carlacontrol.TestTool" and written["tool_version"] == __version__
        assert ProducerRecord.carlanet_version() == str(Producer.CarlaNetVersion) == written["carlanet"]
    finally:
        Producer.ForgetDeclaredTool()


# -- the format rule ----------------------------------------------------------------------------------

def test_a_reader_reads_what_it_knows_and_a_file_from_before_versions_as_version_one():
    assert FormatVersion.check("a.json", "format_version", None, 1) == 1
    assert FormatVersion.check("a.json", "format_version", 1, 1) == 1
    assert FormatVersion.check("a.xml", "format_version", "1", 2) == 1
    assert FormatVersion.check("a.json", "lock_version", 2, 3) == 2


def test_a_newer_version_is_refused_naming_the_file_its_version_and_what_the_reader_supports():
    with pytest.raises(FormatVersionError) as refused:
        FormatVersion.check(Path("C:/run/truth/manifest.jsonl"), "manifest_version", 3, 1)
    assert str(refused.value) == (
        f"{Path('C:/run/truth/manifest.jsonl')} declares manifest_version 3, and this reader supports "
        "manifest_version 1 and earlier. It was written by a newer release; read it with that "
        "release's tools.")
    assert isinstance(refused.value, ValueError)


@pytest.mark.parametrize("declared", [0, -1, "two", 1.5, True])
def test_a_version_that_is_not_one_is_refused(declared):
    with pytest.raises(FormatVersionError, match="a.json declares format_version"):
        FormatVersion.check("a.json", "format_version", declared, 1)


@pytest.mark.parametrize("carlanet", [None, "0.10.0+g1a2b3c4d5"])
def test_the_xml_record_is_one_the_truth_sidecar_s_schema_accepts_with_or_without_carlanet(
        monkeypatch, carlanet):
    """The writer and the schema agree: a process that never loaded CarlaNet leaves carlanet out."""
    monkeypatch.setattr(ProducerRecord, "carlanet_version", staticmethod(lambda: carlanet))
    record = ProducerRecord.record("carlacontrol.SumoCotBridge", server=PACKAGE, sumo="1.27.0")
    element = etree.fromstring(ET.tostring(ProducerRecord.xml_element(record)))
    assert ("carlanet" in element.attrib) == (carlanet is not None)
    sidecar = etree.parse(str(LEGACY_SIDECAR))
    sidecar.getroot().insert(0, element)
    schema = etree.XMLSchema(etree.parse(str(SIDECAR_SCHEMA)))
    assert schema.validate(sidecar), schema.error_log
