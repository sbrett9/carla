"""The two-run manifest diff: two runs of one scenario name the same supervision rows (06 D6.8).

The fixtures are the manifests of three real runs of one supervised scenario -- the fixture cross
under a plan with a stop-anchored dwell, an unanchored approach, a route-part transit and an absence --
written by `CarlaNet.CoSim.RunManifestWriter` against SUMO with no CARLA: at a 1 s SUMO step, at a
0.5 s step, and at 1 s with the capture window opening at 15 s, the three ways the binder's own D6.8
test runs it. Only the absolute paths in their opening rows were cut to file names. Asserted:

  * the runs at two steps, and the run whose window opens late, name the same triples and pass,
    their onsets and closings listed as bound differently;
  * a manifest altered to name a phase its plan does not declare fails, naming the row made at run
    time and the triple each manifest alone names, and the tool exits 1;
  * an interval opened twice is a difference;
  * a manifest cut off mid-run reads as the rows before the cut, the cut line left off, with every
    interval opened and not closed open at the interruption -- equal where the cut fell after every
    triple was named, a difference where it fell before one was; and
  * a file that is not a manifest, or a manifest of another scenario, exits 2.
"""
from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.RunManifestDiff import (  # noqa: E402  (needs the path above)
    ManifestUnreadable,
    diff,
    read_manifest,
    read_rows,
    same_scenario,
)

FIXTURES = Path(__file__).resolve().parent / "fixtures" / "run_manifests"
WHOLE = FIXTURES / "supervised_step_1.0.jsonl"
FINER = FIXTURES / "supervised_step_0.5.jsonl"
LATE = FIXTURES / "supervised_window_15.jsonl"
TOOL = _REPO / "CarlaControl" / "scripts" / "diff_run_manifests.py"

STANDOFF = ("Supervised/standoff", "dweller", "standoff")
EXIT = ("Supervised/transit", "passer", "exit")
VACANCY = ("Supervised/missing", None, "vacancy")
APPROACH = ("Supervised/approach", "dweller", "approach")


def run_tool(first: Path, second: Path) -> subprocess.CompletedProcess:
    return subprocess.run([sys.executable, str(TOOL), str(first), str(second)],
                          capture_output=True, text=True, timeout=120, check=False)


def test_two_runs_at_different_steps_name_the_same_triples_and_bind_them_at_different_times():
    whole, finer = read_manifest(WHOLE), read_manifest(FINER)
    assert whole.closed and finer.closed
    assert whole.named == finer.named == set(whole.planned) == {STANDOFF, EXIT, VACANCY, APPROACH}

    result = diff(whole, finer)
    assert result.same, result.differences
    bound = "\n".join(result.bound_differently)
    assert "(Supervised/standoff, dweller, standoff): committed_start_s 11 / 10" in bound

    completed = run_tool(WHOLE, FINER)
    assert completed.returncode == 0, completed.stderr
    assert "the two runs name the same supervision rows" in completed.stderr


def test_a_run_whose_window_opens_late_names_the_same_triples_begun_before_its_window():
    result = diff(read_manifest(WHOLE), read_manifest(LATE))
    assert result.same, result.differences
    assert any(line.startswith("(Supervised/approach, dweller, approach): begun_before_window false / true")
               for line in result.bound_differently)
    assert run_tool(WHOLE, LATE).returncode == 0


def test_a_manifest_altered_to_name_another_phase_fails_naming_the_rows_that_differ(tmp_path):
    altered = tmp_path / "altered.jsonl"
    text = FINER.read_text(encoding="utf-8")
    renamed = (text.replace('"participant":"passer","phase":"exit","role"',
                            '"participant":"passer","phase":"loiter","role"')
                   .replace('"participant":"passer","phase":"exit","closed_by"',
                            '"participant":"passer","phase":"loiter","closed_by"'))
    assert renamed != text
    altered.write_text(renamed, encoding="utf-8")

    result = diff(read_manifest(WHOLE), read_manifest(altered))
    assert not result.same
    joined = "\n".join(result.differences)
    assert ("altered.jsonl names (Supervised/transit, passer, loiter), which its plan does not declare"
            in joined)
    assert "(Supervised/transit, passer, exit) is named by supervised_step_1.0.jsonl only" in joined
    assert "(Supervised/transit, passer, loiter) is named by altered.jsonl only" in joined

    completed = run_tool(WHOLE, altered)
    assert completed.returncode == 1
    assert "DIFFERENCE: (Supervised/transit, passer, exit) is named by supervised_step_1.0.jsonl only" \
        in completed.stderr


def test_an_interval_opened_twice_is_a_difference(tmp_path):
    lines = WHOLE.read_text(encoding="utf-8").splitlines(keepends=True)
    opened = next(line for line in lines if '"row":"interval_opened"' in line and '"phase":"exit"' in line)
    doubled = tmp_path / "doubled.jsonl"
    doubled.write_text("".join(lines[:-1]) + opened + lines[-1], encoding="utf-8")

    result = diff(read_manifest(WHOLE), read_manifest(doubled))
    assert "doubled.jsonl opens (Supervised/transit, passer, exit) 2 times" in result.differences


def cut(path: Path, after: str, into: Path) -> Path:
    """The manifest cut off half-way through the line after the first line holding `after`."""
    data = path.read_bytes()
    line_end = data.index(b"\n", data.index(after.encode("utf-8")))
    next_end = data.index(b"\n", line_end + 1)
    into.write_bytes(data[:(line_end + next_end) // 2])
    return into


def test_a_manifest_cut_off_mid_run_reads_as_its_rows_with_the_open_intervals_open_at_the_interruption(
        tmp_path):
    # Cut after the standoff opened: every triple is named, and the standoff was open.
    after = cut(WHOLE, '"row":"interval_opened","sim_time_s":11,', tmp_path / "after.jsonl")
    rows = read_rows(after)
    assert len(rows) == after.read_bytes().count(b"\n")
    assert rows[-1]["row"] == "interval_opened" and rows[-1]["phase"] == "standoff"
    interrupted = read_manifest(after)
    assert not interrupted.closed
    assert interrupted.open_at_interruption == [STANDOFF, EXIT]
    result = diff(read_manifest(WHOLE), interrupted)
    assert result.same, result.differences
    assert any("after.jsonl was interrupted with (Supervised/standoff, dweller, standoff), "
               "(Supervised/transit, passer, exit) open" in line for line in result.describe())

    # Cut before it opened: the standoff is named by the whole run alone, and the cut run is said to
    # have been interrupted.
    before = cut(WHOLE, '"row":"interval_opened","sim_time_s":10,', tmp_path / "before.jsonl")
    early = read_manifest(before)
    assert early.open_at_interruption == [EXIT]
    result = diff(read_manifest(WHOLE), early)
    assert result.differences == [
        "(Supervised/standoff, dweller, standoff) is named by supervised_step_1.0.jsonl only "
        "(before.jsonl has no terminal row: its run was interrupted)"]
    assert run_tool(WHOLE, before).returncode == 1


def test_a_file_that_is_not_a_manifest_or_a_manifest_of_another_scenario_exits_2(tmp_path):
    not_one = tmp_path / "not_one.jsonl"
    not_one.write_text('{"row": "render_admitted"}\n', encoding="utf-8")
    try:
        read_manifest(not_one)
        raise AssertionError("a file with no opening row was read as a manifest")
    except ManifestUnreadable:
        pass
    assert run_tool(WHOLE, not_one).returncode == 2

    lines = WHOLE.read_text(encoding="utf-8").splitlines(keepends=True)
    opening = json.loads(lines[0])
    opening["scenario"]["scenario_id"] = "AnotherScenario"
    other = tmp_path / "other.jsonl"
    other.write_text(json.dumps(opening) + "\n" + "".join(lines[1:]), encoding="utf-8")
    assert not same_scenario(read_manifest(WHOLE), read_manifest(other))
    completed = run_tool(WHOLE, other)
    assert completed.returncode == 2
    assert "not two runs of one scenario" in completed.stderr
