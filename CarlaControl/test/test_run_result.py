"""The run result names how a run ended, the exit status is read from it, and it never lies about it.

Plan 12 §3.10.2-§3.10.3: eight outcomes whose one live distinction is finished against stopped, a
status read from the outcome rather than computed beside it, a file written whole or not at all, and
no aggregate verdict anywhere in it. The status numbers are the plan's table, repeated here so the
table and the code drifting together is caught as well as apart.
"""
from __future__ import annotations

import sys
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.RunResult import OUTCOMES, RunResult  # noqa: E402

PLAN_STATUSES = {"run_finished": 0, "usage_error": 1, "refused_offline": 2, "refused_server": 3,
                 "refused_authority": 4, "refused_preroll": 5, "run_stopped": 6,
                 "internal_error": 7}
PLAN_12 = (_REPO / "Docs" / "CAT_Research" / "Plans" / "SUMO_Behavioral_Capture"
           / "12_Operator_Control_Surface.md")


def test_the_statuses_are_the_plan_s():
    assert PLAN_STATUSES == OUTCOMES
    text = PLAN_12.read_text(encoding="utf-8")
    for name, status in PLAN_STATUSES.items():
        assert f"| {status} | `{name}` |" in text, name


@pytest.mark.parametrize(("outcome", "status"), sorted(PLAN_STATUSES.items()))
def test_the_exit_status_is_read_from_the_outcome(outcome, status):
    assert RunResult("cap-x").conclude(outcome).exit_status == status


def test_an_outcome_that_is_not_one_is_refused():
    with pytest.raises(ValueError, match="not a run outcome"):
        RunResult("cap-x").conclude("finished_ok")


def test_the_first_conclusion_stands():
    result = RunResult("cap-x").conclude("run_stopped", closed_by="signal:SIGTERM")
    result.conclude("run_finished", closed_by="window_end")
    assert (result.outcome, result.closed_by) == ("run_stopped", "signal:SIGTERM")


def test_a_result_with_no_outcome_has_no_status():
    with pytest.raises(ValueError):
        _ = RunResult("cap-x").exit_status


def test_the_file_is_written_whole_and_reads_back(tmp_path):
    result = RunResult("cap-x", caller="unattended", caller_label="ingest-38")
    result.conclude("refused_offline", detail="check 9")
    result.findings = [{"check": 9, "outcome": "refuse", "subject": "capture.world_delta_s",
                        "message": "clock ratio"}]
    path = result.write(tmp_path / "out" / "x.result.json")
    assert [p.name for p in path.parent.iterdir()] == ["x.result.json"]
    written = RunResult.read(path)
    assert (written["outcome"], written["exit_status"]) == ("refused_offline", 2)
    assert written["caller_label"] == "ingest-38"
    assert written["refusals"][0]["check"] == 9
    assert written["ended_wall_utc"] is not None


def test_a_warning_carries_its_adjudication_and_who_granted_it():
    result = RunResult("cap-x").conclude("run_finished")
    result.findings = [{"check": 15, "outcome": "warn", "subject": "solar.policy",
                        "message": "honours no epoch", "code": "lighting_honours_no_epoch"}]
    result.adjudications = [{"code": "lighting_honours_no_epoch", "adjudication": "proceed",
                             "adjudicated_by": "configs/night.run.json"}]
    [warning] = result.to_dict()["warnings"]
    assert (warning["adjudication"], warning["adjudicated_by"]) == \
        ("proceed", "configs/night.run.json")


def test_no_field_is_an_aggregate_verdict():
    keys = set(RunResult("cap-x").conclude("run_finished").to_dict())
    assert not keys & {"verdict", "fit", "passed", "ok", "quality", "fitness", "success"}


@pytest.mark.parametrize(("result_name", "sibling"), [
    ("night.result.json", "night.lock.json"), ("run.result.json", "run.lock.json"),
    ("custom.json", "custom.lock.json")])
def test_siblings_share_the_result_s_stem(tmp_path, result_name, sibling):
    assert RunResult.sibling(tmp_path / result_name, "lock") == tmp_path / sibling


def test_a_write_interrupted_midway_leaves_no_partial_result_under_the_real_name(tmp_path,
                                                                                 monkeypatch):
    original = Path.write_text

    def dies_halfway(self, data, *args, **kwargs):
        original(self, data[: len(data) // 2], *args, **kwargs)
        raise OSError("the disk filled")

    monkeypatch.setattr(Path, "write_text", dies_halfway)
    target = tmp_path / "x.result.json"
    with pytest.raises(OSError):
        RunResult.write_json(target, {"outcome": "run_stopped", "padding": "x" * 1000})
    assert not target.exists()
