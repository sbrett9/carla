"""run_capture's command line maps onto the field table, and its exit status is the result's.

The short options are generated from the fields' aliases, so each is exercised here for the field it
sets and for its precedence below `--set`. Only paths that contact no server are run: `--validate-only`,
offline refusals and those before them, `--help` and `--write-schema`. The exit status is compared with the
outcome read back from the result file, because plan 12 D12.22 makes the file the source of the
status.
"""
from __future__ import annotations

import json
import runpy
import sys
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))
sys.path.insert(0, str(_REPO / "CarlaControl" / "test"))

from RunCaptureFixture import Layout, run_document  # noqa: E402

from carlacontrol.RunConfiguration import RunConfiguration  # noqa: E402
from carlacontrol.RunResult import RunResult  # noqa: E402

SCRIPT = _REPO / "CarlaControl" / "scripts" / "run_capture.py"
MAIN = runpy.run_path(str(SCRIPT))["main"]


@pytest.fixture
def layout(tmp_path: Path) -> Layout:
    return Layout(tmp_path)


def invoke(layout: Layout, *arguments: str, document: dict | None = None) -> tuple[int, Path]:
    run_path = layout.root / "fixture.run.json"
    run_path.write_text(json.dumps(document or run_document()), encoding="utf-8")
    result = layout.root / "out" / "fixture.result.json"
    status = MAIN(["--run", str(run_path), "--site-profile", str(layout.write_profile()),
                   "--result", str(result), *arguments])
    return status, result


def effective_field(result: Path, path: str) -> dict:
    lock = json.loads(RunResult.sibling(result, "lock").read_text(encoding="utf-8"))
    return lock["effective_configuration"]["fields"][path]


def test_validate_only_accepts_and_writes_the_lock_beside_the_result(layout):
    status, result = invoke(layout, "--validate-only")
    assert status == 0
    assert RunResult.sibling(result, "lock").is_file()
    assert RunResult.sibling(result, "resolution").is_file()
    assert effective_field(result, "capture.window")["layer"] == "run_configuration"


def test_an_alias_sets_its_field_as_an_operator_override(layout):
    status, result = invoke(layout, "--validate-only", "--window", "night", "--solar", "advance")
    assert status == 0
    window = effective_field(result, "capture.window")
    assert (window["value"], window["layer"], window["provenance"]) == \
        ("night", "operator_override", "--window night")
    assert effective_field(result, "solar.policy")["value"] == "advance"


def test_set_is_applied_after_the_aliases(layout):
    status, result = invoke(layout, "--validate-only", "--window", "night",
                            "--set", "capture.window=morning")
    assert status == 0
    window = effective_field(result, "capture.window")
    assert window["value"] == "morning"
    assert [o["value"] for o in window["overridden"]][-1] == "night"


def test_an_offline_refusal_exits_with_the_result_s_status(layout):
    status, result = invoke(layout, "--set", "capture.world_delta_s=0.03")
    written = RunResult.read(result)
    assert (status, written["outcome"], written["exit_status"]) == (2, "refused_offline", 2)


def test_a_malformed_override_exits_1_with_a_result(layout):
    status, result = invoke(layout, "--set", "capture.render_capp=3")
    assert status == 1 and RunResult.read(result)["outcome"] == "usage_error"


def test_the_help_is_generated_from_the_field_table(capsys):
    with pytest.raises(SystemExit):
        MAIN(["--help"])
    text = capsys.readouterr().out
    for path, spec in RunConfiguration.FIELDS.items():
        assert path in text, path
        if spec.alias:
            assert spec.alias in text, spec.alias


def test_write_schema_publishes_the_generated_schema(tmp_path):
    target = tmp_path / "run_configuration.schema.json"
    assert MAIN(["--write-schema", str(target)]) == 0
    assert json.loads(target.read_text(encoding="utf-8")) == RunConfiguration.schema()


def test_an_unreadable_site_profile_is_a_usage_error(layout, tmp_path):
    broken = tmp_path / "broken.json"
    broken.write_text("{", encoding="utf-8")
    assert MAIN(["--site-profile", str(broken), "--validate-only"]) == 1
