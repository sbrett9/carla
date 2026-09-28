"""The authoring skill's examples compile, and resolve to exactly what their recorded reports say.

`07_Scenario_Authoring.md` §8.3 ships worked examples beside the skill and §8.5 keeps them true by
mechanism: each is compiled against the compiler's fixture world in the ordinary test run and what it
resolved to is compared with the report recorded beside it. So an example that stops compiling, or
starts resolving to something else, fails here rather than misleading an author.

The fixture world is `ScenarioWorldFixture`'s street layout. An example names it as `StreetLayout.cwp`
and the catalogue by its path in the repository; the test copies each example beside a freshly built
fixture package, points the catalogue at the repository's, and compiles. A report is compared whole,
less the one field that names the compiling machine: the SUMO installation's path.

After a deliberate change to what an example resolves to, record the new reports by running this file
with `CARLACONTROL_RECORD_SKILL_EXAMPLES=1`, and read the diff before committing it.
"""
from __future__ import annotations

import copy
import json
import os
import sys
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))
sys.path.insert(0, str(Path(__file__).resolve().parent))

from ScenarioWorldFixture import CATALOGUE, ScenarioWorldFixture  # noqa: E402

from carlacontrol.ScenarioCompiler import ScenarioCompiler  # noqa: E402
from carlacontrol.ScenarioSweep import ScenarioSweep  # noqa: E402

EXAMPLES = _REPO / "CarlaControl" / "skills" / "sumo-traffic-scenarios" / "examples"
SPECIFICATIONS = sorted([*EXAMPLES.glob("minimal/*.scenario.json"),
                         *EXAMPLES.glob("epoch/*.scenario.json")])
RECORD = os.environ.get("CARLACONTROL_RECORD_SKILL_EXAMPLES") == "1"


@pytest.fixture(scope="module")
def installation():
    try:
        return ScenarioWorldFixture.locate_sumo()
    except FileNotFoundError as missing:
        pytest.skip(f"no SUMO with duarouter: {missing}")


@pytest.fixture(scope="module")
def world(tmp_path_factory, installation) -> ScenarioWorldFixture:
    return ScenarioWorldFixture(tmp_path_factory.mktemp("world"), installation)


def beside_the_world(world: ScenarioWorldFixture, example: Path) -> Path:
    """The example, copied next to the fixture package, its catalogue the repository's."""
    document = json.loads(example.read_text(encoding="utf-8"))
    if "catalogue" in document:
        document["catalogue"] = str(CATALOGUE)
    return world.write(document, example.name)


def without_machine(report: dict) -> dict:
    report = copy.deepcopy(report)
    report.get("world", {}).get("routing_sumo", {}).pop("home", None)
    return report


def recorded(path: Path, actual: dict) -> dict:
    if RECORD:
        path.write_text(json.dumps(actual, indent=2, ensure_ascii=False) + "\n", encoding="utf-8",
                        newline="\n")
    if not path.exists():
        pytest.fail(f"no recorded report at {path}; record one with "
                    "CARLACONTROL_RECORD_SKILL_EXAMPLES=1")
    return json.loads(path.read_text(encoding="utf-8"))


def test_there_are_examples_of_each_kind_the_plan_names():
    assert {p.parent.name for p in SPECIFICATIONS} == {"minimal", "epoch"}
    assert len(list(EXAMPLES.glob("epoch/*.scenario.json"))) == 3
    assert list(EXAMPLES.glob("counterfactual/*.sweep.json"))


@pytest.mark.parametrize("example", SPECIFICATIONS, ids=lambda p: p.stem)
def test_an_example_compiles_to_its_recorded_report(world, installation, tmp_path, example):
    result = ScenarioCompiler(installation).compile(beside_the_world(world, example),
                                                    tmp_path / "out")
    assert not result.refused, [str(f) for f in result.findings.findings if f.outcome == "refuse"]
    actual = without_machine(result.report)
    report = example.with_name(example.name.replace(".scenario.json", ".resolution.json"))
    assert actual == recorded(report, actual)


def test_the_half_hour_epoch_is_declared_and_its_distance_from_the_world_is_warned(world,
                                                                                    installation,
                                                                                    tmp_path):
    """+03:30 compiles as declared; on a Colorado world check 40 names the gap, and nothing else."""
    example = EXAMPLES / "epoch" / "street_layout_epoch_half_hour.scenario.json"
    result = ScenarioCompiler(installation).compile(beside_the_world(world, example),
                                                    tmp_path / "out")
    assert result.lock["epoch"]["utc_offset_hours"] == 3.5
    assert [f.check_id for f in result.findings.findings if f.check_id in (33, 34, 40)] == [40]


def test_the_counterfactual_sweep_compiles_its_pairs_to_the_recorded_index(world, installation,
                                                                          tmp_path):
    for example in EXAMPLES.glob("counterfactual/*.json"):
        beside_the_world(world, example)
    sweep = world.directory / "probe_standoff_pairs.sweep.json"
    index = ScenarioSweep(installation).compile(sweep, tmp_path / "out")
    assert index["outcome"] != "refused", index["findings"]
    assert {pair["mode"] for pair in index["pairs"]} == {"nominal", "absent", "displaced"}
    actual = {key: value for key, value in index.items() if key != "path"}
    assert actual == recorded(EXAMPLES / "counterfactual" / "probe_standoff_pairs.sweep-index.json",
                              actual)
