"""What the tools write conforms to the published schema of its kind.

The run records and the compiler's outputs have schemas generated from the code that writes them, and
the generator could still describe a member wrongly. So each schema is held to real output: the
records a capture run writes in each way it can end (driven against the stand-ins of `CaptureFakes`,
no server contacted), what the compiler writes for the fixture world (a compile, a refused compile
and a sweep), and the compiled scenarios and inputs this repository ships. Two pairs of files share a
name and not a shape -- a scenario's `.lock.json` and a run's `run.lock.json`, the compiler's
`.resolution.json` and a run's `run.resolution.json` -- and each is held to its own schema and refused
by the other's.
"""
from __future__ import annotations

import io
import json
import sys
from collections import namedtuple
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))
sys.path.insert(0, str(_REPO / "CarlaControl" / "test"))

import carlanet  # noqa: E402, F401  -- loads the CarlaNet assemblies the next imports name
from CaptureFakes import FakeServer  # noqa: E402
from RunCaptureFixture import Layout, run_document  # noqa: E402
from ScenarioWorldFixture import ScenarioWorldFixture  # noqa: E402

from carlacontrol.CaptureSession import CaptureSession  # noqa: E402
from carlacontrol.PublishedSchemas import PublishedSchemas  # noqa: E402
from carlacontrol.RunConfiguration import RunConfiguration  # noqa: E402
from carlacontrol.RunConfigurationValidator import RunConfigurationValidator  # noqa: E402
from carlacontrol.RunTerminationSequence import RunTerminationSequence  # noqa: E402
from carlacontrol.ScenarioCheckCatalogue import ScenarioCheckCatalogue  # noqa: E402
from carlacontrol.ScenarioCompiler import ScenarioCompiler  # noqa: E402
from carlacontrol.ScenarioSchema import SCHEMA  # noqa: E402
from carlacontrol.ScenarioSweep import SWEEP_SCHEMA, ScenarioSweep  # noqa: E402
from carlacontrol.SchemaPublication import SchemaPublication  # noqa: E402
from carlacontrol.SessionMonitor import SessionMonitor  # noqa: E402
from carlacontrol.SiteProfile import SiteProfile  # noqa: E402

IMPORT = _REPO / "Import"
SKILL = _REPO / "CarlaControl" / "skills" / "sumo-traffic-scenarios"
SESSION_ID = "cap-test"
Usage = namedtuple("Usage", "total used free")


def schema(name: str) -> dict:
    return PublishedSchemas.all()[name]()


def problems(document: object, name: str) -> list[str]:
    return SchemaPublication.problems(document, schema(name))


def read(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8-sig"))


# -- a capture run's records -------------------------------------------------------------------------

@pytest.fixture
def layout(tmp_path: Path) -> Layout:
    return Layout(tmp_path)


def capture(layout: Layout, overrides=(), document: dict | None = None,
            server: FakeServer | None = None) -> dict[str, Path]:
    """Run a capture against the stand-ins; the records it wrote, by kind."""
    server = server or FakeServer()
    run_path = layout.root / "fixture.run.json"
    run_path.write_text(json.dumps(document if document is not None else run_document()),
                        encoding="utf-8")
    site = SiteProfile.discover(layout.root, layout.write_profile(), environ={})
    session = CaptureSession(
        site, run_path, [(text, f"--set {text}") for text in overrides],
        client_factory=server.client,
        validator=RunConfigurationValidator(disk_usage=lambda _p: Usage(10**13, 0, 10**13)),
        termination=RunTerminationSequence(step_timeout_s=5.0),
        monitor=SessionMonitor(stream=io.StringIO(), is_terminal=False),
        answer=lambda _prompt: "no", stdin_is_terminal=lambda: False, session_id=SESSION_ID)
    session.run()
    directory = layout.runs_root / SESSION_ID
    return {kind: directory / f"run.{kind}.json"
            for kind in ("result", "resolution", "lock", "effective")
            if (directory / f"run.{kind}.json").is_file()}


def test_a_finished_run_s_four_records_conform(layout):
    records = capture(layout)
    assert sorted(records) == ["effective", "lock", "resolution", "result"]
    result = read(records["result"])
    assert result["outcome"] == "run_finished" and result["produced"] is not None
    assert problems(result, "run_result.schema.json") == []
    assert problems(read(records["resolution"]), "run_resolution.schema.json") == []
    assert problems(read(records["lock"]), "run_lock.schema.json") == []
    # The replayable configuration is a run configuration, and is read back as one.
    effective = read(records["effective"])
    assert problems(effective, "run_configuration.schema.json") == []
    RunConfiguration.from_document(effective, str(records["effective"]))
    assert problems(result["launch_echo"], "launch_echo.schema.json") == []


def test_an_offline_refusal_s_records_conform(layout):
    records = capture(layout, overrides=["capture.world_delta_s=0.03"])
    assert sorted(records) == ["resolution", "result"]
    result = read(records["result"])
    assert result["outcome"] == "refused_offline" and result["produced"] is None
    assert problems(result, "run_result.schema.json") == []
    resolution = read(records["resolution"])
    assert resolution["outcome"] == "refused_offline"
    assert problems(resolution, "run_resolution.schema.json") == []


def test_a_usage_error_s_records_conform(layout):
    records = capture(layout, overrides=["capture.prewarm_ss=3"])
    result = read(records["result"])
    assert result["outcome"] == "usage_error"
    assert problems(result, "run_result.schema.json") == []
    resolution = read(records["resolution"])
    assert resolution["effective_configuration"] is None
    assert problems(resolution, "run_resolution.schema.json") == []


def test_a_server_refusal_s_records_conform(layout):
    server = FakeServer()
    server.unreachable = True
    records = capture(layout, server=server)
    result = read(records["result"])
    assert result["outcome"] == "refused_server"
    assert problems(result, "run_result.schema.json") == []
    assert problems(read(records["lock"]), "run_lock.schema.json") == []


def test_a_run_s_lock_and_resolution_are_not_a_scenario_s(layout):
    records = capture(layout)
    assert problems(read(records["lock"]), "scenario_lock.schema.json")
    assert problems(read(records["resolution"]), "scenario_resolution.schema.json")


def test_the_site_profile_template_conforms(layout):
    site = SiteProfile.discover(layout.root, layout.write_profile(), environ={})
    template = site.to_template()
    assert problems(template, "site_profile.schema.json") == []
    assert SchemaPublication.problems(site.to_record(), SiteProfile.record_schema()) == []


# -- the compiler's outputs --------------------------------------------------------------------------

@pytest.fixture(scope="module")
def installation():
    try:
        return ScenarioWorldFixture.locate_sumo()
    except FileNotFoundError as missing:
        pytest.skip(f"no SUMO with duarouter: {missing}")


@pytest.fixture(scope="module")
def world(tmp_path_factory, installation) -> ScenarioWorldFixture:
    return ScenarioWorldFixture(tmp_path_factory.mktemp("world"), installation)


@pytest.fixture(scope="module")
def compiled(world, installation, tmp_path_factory):
    spec = world.write(world.specification())
    result = ScenarioCompiler(installation).compile(spec, tmp_path_factory.mktemp("compiled"))
    assert not result.refused, [str(f) for f in result.findings.refusals]
    return result


def test_a_compile_s_lock_report_and_plan_conform(compiled):
    assert problems(read(compiled.files["lock"]), "scenario_lock.schema.json") == []
    assert problems(read(compiled.files["resolution"]), "scenario_resolution.schema.json") == []
    assert problems(read(compiled.files["supervision"]), "supervision_plan.schema.json") == []


def test_a_scenario_s_lock_and_resolution_are_not_a_run_s(compiled):
    assert problems(read(compiled.files["lock"]), "run_lock.schema.json")
    assert problems(read(compiled.files["resolution"]), "run_resolution.schema.json")


def test_a_refused_compile_s_report_conforms(world, installation, tmp_path):
    spec = world.write(world.specification(epoch={"epoch_version": 1}), "refused.scenario.json")
    result = ScenarioCompiler(installation).compile(spec, tmp_path)
    assert result.refused
    report = read(result.files["resolution"])
    assert report["outcome"] == "refused"
    assert problems(report, "scenario_resolution.schema.json") == []


def test_a_compile_that_skips_its_dry_run_conforms(world, installation, tmp_path):
    spec = world.write(world.specification(), "skipped.scenario.json")
    result = ScenarioCompiler(installation, skip_dry_run=True).compile(spec, tmp_path)
    lock = read(result.files["lock"])
    assert lock["dry_run"]["ran"] is False
    assert problems(lock, "scenario_lock.schema.json") == []
    assert problems(read(result.files["resolution"]), "scenario_resolution.schema.json") == []


def test_a_sweep_s_index_conforms(world, installation, tmp_path):
    world.write(world.specification(), "sweep_base.scenario.json")
    sweep = world.directory / "fixture.sweep.json"
    sweep.write_text(json.dumps({
        "sweep_version": 1, "sweep_id": "fixture_seeds", "base": "sweep_base.scenario.json",
        "axes": [{"path": "seeds.sumo", "values": [42, 43]}],
        "counterfactuals": [{"actor": "hauler", "mode": "absent"}]}), encoding="utf-8")
    index = ScenarioSweep(installation, skip_dry_run=True).compile(sweep, tmp_path)
    assert index["outcome"] == "compiled", index["findings"]
    assert problems(read(Path(index["path"])), "sweep_index.schema.json") == []


def test_a_refused_sweep_s_index_conforms(world, installation, tmp_path):
    sweep = world.directory / "refused.sweep.json"
    sweep.write_text(json.dumps({"sweep_version": 1, "sweep_id": "refused", "base": 7}),
                     encoding="utf-8")
    index = ScenarioSweep(installation).compile(sweep, tmp_path)
    assert index["outcome"] == "refused"
    assert problems(read(Path(index["path"])), "sweep_index.schema.json") == []


# -- what this repository ships ----------------------------------------------------------------------

SHIPPED = [
    *((path, "scenario_lock.schema.json") for path in sorted(IMPORT.glob("*.lock.json"))),
    *((path, "scenario_resolution.schema.json")
      for path in sorted([*IMPORT.glob("*.resolution.json"),
                          *SKILL.glob("examples/*/*.resolution.json")])),
    *((path, "supervision_plan.schema.json") for path in sorted(IMPORT.glob("*.supervision.json"))),
    *((path, "sweep_index.schema.json") for path in sorted(SKILL.glob("examples/*/*.sweep-index.json"))),
    *((path, "display_convention.schema.json") for path in sorted(IMPORT.glob("*.display.json"))),
    *((path, "area_of_interest.schema.json") for path in sorted(IMPORT.glob("*.aoi.geojson"))),
    *((path, "run_configuration.schema.json") for path in sorted(IMPORT.glob("*.run.json"))),
    (SKILL / "checks.json", "scenario_checks.schema.json"),
]


def test_there_are_shipped_files_of_every_kind():
    assert {name for _, name in SHIPPED} == {
        "scenario_lock.schema.json", "scenario_resolution.schema.json",
        "supervision_plan.schema.json", "sweep_index.schema.json",
        "display_convention.schema.json", "area_of_interest.schema.json",
        "run_configuration.schema.json", "scenario_checks.schema.json"}


@pytest.mark.parametrize(("path", "name"), SHIPPED, ids=[path.name for path, _ in SHIPPED])
def test_a_shipped_file_conforms_to_its_schema(path, name):
    assert problems(read(path), name) == []


@pytest.mark.parametrize("path", sorted([*IMPORT.glob("*.scenario.json"),
                                         *SKILL.glob("examples/*/*.scenario.json")]),
                         ids=lambda path: path.name)
def test_a_shipped_scenario_s_epoch_conforms(path):
    assert problems(read(path)["epoch"], "epoch.schema.json") == []


def test_the_checks_list_conforms_as_generated():
    assert problems(ScenarioCheckCatalogue.to_document(), "scenario_checks.schema.json") == []


def test_the_skill_s_own_schemas_hold_its_examples():
    for path in SKILL.glob("examples/*/*.scenario.json"):
        assert SchemaPublication.problems(read(path), SCHEMA) == [], path.name
    for path in SKILL.glob("examples/*/*.sweep.json"):
        assert SchemaPublication.problems(read(path), SWEEP_SCHEMA) == [], path.name
