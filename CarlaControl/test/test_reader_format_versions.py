"""Every reader of our files reads a file from before versions and refuses a newer one by name.

A format version is an integer of its own file, independent of the release: a reader reads a version it
knows; reads a file written before its kind carried one as version 1; and refuses a newer one, naming
the file, the version it declares and what the reader supports, rather than reading it in part. Held
here for each reader of a file our tools write, with no server and no SUMO:

  * `WorldPackageReader`, the world package's `world.json` (`FormatVersion`);
  * `RunManifestDiff`, the run manifest's opening row (`manifest_version`);
  * `RunResult.read`, a run's result (`result_version`);
  * `RunConfiguration`, a run configuration and a run's replayable `run.effective.json`
    (`run_configuration_version`), which also carries what wrote it;
  * `VehicleCatalogue`, the catalogue (`catalogue_version`, which every catalogue carries);
  * `CorpusLeakValidator`, a truth sidecar's or the SUMO bridge's events (`format_version`).

The sidecar audit's own check is in `test_truth_sidecar_audit.py`, the compile lock's (check 6) in
`test_run_configuration_validation.py`, and the C# readers' in CarlaNet's tests.
"""
from __future__ import annotations

import json
import sys
import zipfile
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.CorpusLeakValidator import CorpusLeakValidator  # noqa: E402
from carlacontrol.FormatVersion import FormatVersionError  # noqa: E402
from carlacontrol.RunConfiguration import RunConfiguration  # noqa: E402
from carlacontrol.RunConfigurationFindings import RunConfigurationRefusedError  # noqa: E402
from carlacontrol.RunManifestDiff import ManifestUnreadable, read_manifest  # noqa: E402
from carlacontrol.RunResult import RunResult  # noqa: E402
from carlacontrol.VehicleCatalogue import VehicleCatalogue  # noqa: E402
from carlacontrol.WorldPackageReader import WorldPackageReader  # noqa: E402

PRODUCER = {"tool": "carlanet", "tool_version": None, "carlanet": "0.10.0+g1a2b3c4d5", "server": None,
            "sumo": "1.27.0", "written_utc": "2026-10-07T12:00:00.000Z"}


def newer_refused(refused: Exception, path: Path, field: str, declared: int, supported: int) -> None:
    message = str(refused)
    assert str(path) in message, message
    assert f"declares {field} {declared}" in message, message
    assert f"supports {field} {supported} and earlier" in message, message


# -- the world package ---------------------------------------------------------------------------------

def world_package(path: Path, **manifest) -> Path:
    with zipfile.ZipFile(path, "w", zipfile.ZIP_STORED) as package:
        package.writestr("world.json", json.dumps({"MapName": "TestArea", "OriginLatitude": 38.9,
                                                   "OriginLongitude": -119.7, **manifest}))
    return path


def test_a_world_package_from_before_its_format_was_recorded_reads_as_version_one(tmp_path):
    reader = WorldPackageReader(world_package(tmp_path / "TestArea.cwp"))
    assert reader.format_version == 1
    assert reader.producer is None
    assert reader.map_name == "TestArea"


def test_a_world_package_names_what_made_it(tmp_path):
    reader = WorldPackageReader(world_package(tmp_path / "TestArea.cwp", FormatVersion=1, Producer=PRODUCER))
    assert reader.format_version == 1
    assert reader.producer == PRODUCER


def test_a_world_package_of_a_newer_format_is_refused_by_name(tmp_path):
    path = world_package(tmp_path / "TestArea.cwp", FormatVersion=2)
    with pytest.raises(FormatVersionError) as refused:
        WorldPackageReader(path)
    newer_refused(refused.value, path, "FormatVersion", 2, 1)


# -- the run manifest ---------------------------------------------------------------------------------

def manifest(path: Path, opening: dict) -> Path:
    rows = [{"row": "manifest_opened", **opening, "scenario": {"scenario_id": "probe"}, "plan": None},
            {"row": "manifest_closed", "ended": "scenario_finished"}]
    path.write_text("".join(json.dumps(row) + "\n" for row in rows), encoding="utf-8")
    return path


def test_a_manifest_says_what_made_it_and_one_from_before_the_record_is_read_as_it_was(tmp_path):
    assert read_manifest(manifest(tmp_path / "a.jsonl", {"manifest_version": 1,
                                                         "producer": PRODUCER})).producer == PRODUCER
    legacy = read_manifest(manifest(tmp_path / "b.jsonl", {"manifest_version": 1}))
    assert legacy.producer is None and legacy.closed


def test_a_manifest_of_a_newer_format_is_refused_by_name(tmp_path):
    path = manifest(tmp_path / "manifest.jsonl", {"manifest_version": 2})
    with pytest.raises(ManifestUnreadable) as refused:
        read_manifest(path)
    newer_refused(refused.value, path, "manifest_version", 2, 1)


# -- the run result -----------------------------------------------------------------------------------

def test_a_run_result_of_a_newer_format_is_refused_and_one_it_knows_is_read(tmp_path):
    known = tmp_path / "run.result.json"
    known.write_text(json.dumps({"result_version": 1, "outcome": "run_finished"}), encoding="utf-8")
    assert RunResult.read(known)["outcome"] == "run_finished"

    newer = tmp_path / "later.result.json"
    newer.write_text(json.dumps({"result_version": 2, "outcome": "run_finished"}), encoding="utf-8")
    with pytest.raises(FormatVersionError) as refused:
        RunResult.read(newer)
    newer_refused(refused.value, newer, "result_version", 2, 1)


# -- the run configuration ----------------------------------------------------------------------------

def test_a_run_configuration_of_a_newer_version_is_refused_for_its_version_not_its_keys():
    with pytest.raises(RunConfigurationRefusedError) as raised:
        RunConfiguration.from_document({"run_configuration_version": 2, "a_renamed_key": 1},
                                       "later.run.json")
    [finding] = raised.value.findings.refusals
    assert finding.check_id == 1 and finding.subject == "run_configuration_version"
    newer_refused(finding.message, Path("later.run.json"), "run_configuration_version", 2, 1)


def test_what_wrote_a_run_configuration_is_kept_out_of_its_fields_and_must_be_an_object():
    read = RunConfiguration.from_document({"run_configuration_version": 1, "caller": "unattended",
                                           "producer": PRODUCER}, "run.effective.json")
    assert read.values == {"run_configuration_version": 1, "caller": "unattended"}

    with pytest.raises(RunConfigurationRefusedError) as raised:
        RunConfiguration.from_document({"producer": "run_capture 0.10.0"}, "run.effective.json")
    [finding] = raised.value.findings.refusals
    assert finding.subject == "producer" and "is an object" in finding.message


# -- the vehicle catalogue ----------------------------------------------------------------------------

def test_a_catalogue_of_a_newer_version_is_refused_naming_the_file(tmp_path):
    path = tmp_path / "vehicles.catalogue.json"
    path.write_text(json.dumps({"catalogue_version": 2, "vehicles": []}), encoding="utf-8")
    with pytest.raises(ValueError) as refused:
        VehicleCatalogue.load(path)
    assert str(path) in str(refused.value)
    assert "declares catalogue_version 2" in str(refused.value)
    assert "written by a newer release" in str(refused.value)


# -- the label-leak check's events --------------------------------------------------------------------

EVENTS = """<?xml version="1.0" encoding="utf-8"?>
<events {version}source="sumo" scenario="probe" epoch="2026-03-21T05:00:00.000Z">
  <event version="2.0" uid="SUMO-TRUTH-a" type="a-n-G-E-V" how="m-g" time="t" start="t" stale="t">
    <point lat="0" lon="0" hae="0" ce="0.0" le="0.0" />
    <detail><_carla type_id="car" marked="0" /></detail>
  </event>
</events>
"""


def test_events_from_before_versions_are_read_and_newer_ones_refused_by_name(tmp_path):
    legacy = tmp_path / "legacy.xml"
    legacy.write_text(EVENTS.format(version=""), encoding="utf-8")
    assert [record["uid"] for record in CorpusLeakValidator.records_from_xml(legacy)] == ["SUMO-TRUTH-a"]

    newer = tmp_path / "newer.xml"
    newer.write_text(EVENTS.format(version='format_version="2" '), encoding="utf-8")
    with pytest.raises(FormatVersionError) as refused:
        list(CorpusLeakValidator.records_from_xml(newer))
    newer_refused(refused.value, newer, "format_version", 2, 1)
