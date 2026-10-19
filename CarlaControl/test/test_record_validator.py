"""The record and input files are found by name in any folder and checked against their schemas.

`RecordValidator` is what `carla-validate` checks the files that live beside a capture with: a run's
records beside its result, a compiled scenario's files, and the files a user writes. Held here: every
such file this repository ships, and the records of a capture run driven against the stand-ins, keep
their schemas; each kind is found by its name, and the two pairs of files that share a name -- a run's
lock and resolution report and a compiled scenario's -- by what they hold; a file that breaks its schema
is named with where and what; a newer format version is reported as written by a newer release; and a
legacy `.supervision.json`, which is another kind of file, is left alone.
"""
from __future__ import annotations

import io
import json
import shutil
import sys
from collections import namedtuple
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))
sys.path.insert(0, str(_REPO / "CarlaControl" / "test"))

from CaptureFakes import FakeServer  # noqa: E402
from RunCaptureFixture import Layout, run_document  # noqa: E402

from carlacontrol.CaptureSession import CaptureSession  # noqa: E402
from carlacontrol.PublishedSchemas import PublishedSchemas  # noqa: E402
from carlacontrol.RecordValidator import (  # noqa: E402
    AREAS,
    CHECK_LISTS,
    DISPLAY_CONVENTIONS,
    KINDS,
    RUN_CONFIGURATIONS,
    RUN_LOCKS,
    RUN_RESOLUTIONS,
    RUN_RESULTS,
    SCENARIO_LOCKS,
    SCENARIO_RESOLUTIONS,
    SCHEMA_FILES,
    SITE_PROFILES,
    SPECIFICATIONS,
    SUPERVISION_PLANS,
    SWEEP_INDEXES,
    SWEEPS,
    RecordValidator,
)
from carlacontrol.RunConfigurationValidator import RunConfigurationValidator  # noqa: E402
from carlacontrol.RunTerminationSequence import RunTerminationSequence  # noqa: E402
from carlacontrol.SessionMonitor import SessionMonitor  # noqa: E402
from carlacontrol.SiteProfile import SiteProfile  # noqa: E402

SCHEMAS = _REPO / "CarlaControl" / "schemas"
IMPORT = _REPO / "Import"
SKILL = _REPO / "CarlaControl" / "skills" / "sumo-traffic-scenarios"
SESSION_ID = "cap-test"
Usage = namedtuple("Usage", "total used free")


@pytest.fixture(scope="module")
def validator() -> RecordValidator:
    return RecordValidator(SCHEMAS)


def failures(validator: RecordValidator, root: Path) -> dict[str, list[str]]:
    """Every file found under `root`, by path, with each way it breaks its schema."""
    found = {}
    for record in validator.files(root):
        problems: list[str] = []
        validator.check(record, lambda where, message, into=problems: into.append(
            f"{where}: {message}" if where else message))
        found[str(record.path.relative_to(root) if root.is_dir() else record.path.name)] = problems
    return found


def test_every_kind_s_schema_is_a_published_one():
    assert set(SCHEMA_FILES.values()) <= set(PublishedSchemas.all())
    assert set(KINDS) == set(SCHEMA_FILES) | {SPECIFICATIONS, SWEEPS}


def test_every_record_and_input_this_repository_ships_keeps_its_schema(validator):
    for root in (IMPORT, SKILL):
        found = failures(validator, root)
        assert found, f"nothing found under {root}"
        broken = {path: problems for path, problems in found.items() if problems}
        assert broken == {}, broken


def test_the_shipped_files_are_found_as_their_kinds(validator):
    kinds = {record.kind for root in (IMPORT, SKILL) for record in validator.files(root)}
    assert {SCENARIO_LOCKS, SCENARIO_RESOLUTIONS, SUPERVISION_PLANS, SPECIFICATIONS,
            RUN_CONFIGURATIONS, DISPLAY_CONVENTIONS, AREAS, SWEEPS, SWEEP_INDEXES,
            CHECK_LISTS} <= kinds


@pytest.fixture
def run_records(tmp_path: Path) -> Path:
    """The folder a finished capture run writes its records into, driven against the stand-ins."""
    layout = Layout(tmp_path)
    run_path = layout.root / "fixture.run.json"
    run_path.write_text(json.dumps(run_document()), encoding="utf-8")
    site = SiteProfile.discover(layout.root, layout.write_profile(), environ={})
    CaptureSession(
        site, run_path, [], client_factory=FakeServer().client,
        validator=RunConfigurationValidator(disk_usage=lambda _p: Usage(10**13, 0, 10**13)),
        termination=RunTerminationSequence(step_timeout_s=5.0),
        monitor=SessionMonitor(stream=io.StringIO(), is_terminal=False),
        answer=lambda _prompt: "no", stdin_is_terminal=lambda: False,
        session_id=SESSION_ID).run()
    folder = layout.runs_root / SESSION_ID
    shutil.copy(layout.root / "site.profile.json", folder / "site.profile.json")
    return folder


def test_a_run_s_records_are_found_by_name_and_keep_their_schemas(validator, run_records):
    kinds = {record.path.name: record.kind for record in validator.files(run_records)}
    assert kinds == {"run.result.json": RUN_RESULTS, "run.resolution.json": RUN_RESOLUTIONS,
                     "run.lock.json": RUN_LOCKS, "run.effective.json": RUN_CONFIGURATIONS,
                     "site.profile.json": SITE_PROFILES}
    assert all(problems == [] for problems in failures(validator, run_records).values())


def test_a_run_s_lock_is_told_from_a_scenario_s_by_what_it_holds(validator, run_records, tmp_path):
    scenario = tmp_path / "scenario"
    scenario.mkdir()
    shutil.copy(IMPORT / "Arapahoe_I25_SupervisionCheck.lock.json", scenario / "run.lock.json")
    [record] = validator.files(scenario / "run.lock.json")
    assert record.kind == SCENARIO_LOCKS


def test_a_record_that_breaks_its_schema_is_named_with_where_and_what(validator, run_records):
    path = run_records / "run.result.json"
    result = json.loads(path.read_text(encoding="utf-8"))
    result["outcome"] = "finished"
    del result["session_id"]
    path.write_text(json.dumps(result), encoding="utf-8")
    problems = failures(validator, run_records)["run.result.json"]
    assert any(problem.startswith("$.outcome: \"finished\" is not one of") for problem in problems)
    assert "'session_id' is required" in " ".join(problems)


def test_a_newer_format_version_is_reported_as_written_by_a_newer_release(validator, run_records):
    path = run_records / "run.lock.json"
    lock = json.loads(path.read_text(encoding="utf-8"))
    lock["lock_version"] = 2
    path.write_text(json.dumps(lock), encoding="utf-8")
    [problem] = failures(validator, run_records)["run.lock.json"]
    assert "lock_version 2" in problem and "newer release" in problem


def test_a_file_that_is_not_json_says_so(validator, tmp_path):
    (tmp_path / "x.scenario.json").write_text("{not json", encoding="utf-8")
    [problem] = failures(validator, tmp_path)["x.scenario.json"]
    assert problem.startswith("is not JSON")


def test_a_specification_s_epoch_is_held_to_the_epoch_schema(validator, tmp_path):
    document = json.loads((IMPORT / "Arapahoe_I25_SupervisionCheck.scenario.json")
                          .read_text(encoding="utf-8"))
    document["epoch"]["utc_offset_hours"] = -6.1
    (tmp_path / "x.scenario.json").write_text(json.dumps(document), encoding="utf-8")
    [problem] = failures(validator, tmp_path)["x.scenario.json"]
    assert problem.startswith("epoch.utc_offset_hours: -6.1 is not a multiple of 0.25")


def test_a_legacy_supervision_file_is_another_kind_and_is_left_alone(validator, tmp_path):
    (tmp_path / "old.supervision.json").write_text(json.dumps({"gaps": []}), encoding="utf-8")
    (tmp_path / "notes.json").write_text(json.dumps({"anything": 1}), encoding="utf-8")
    assert validator.files(tmp_path) == []
