"""A rota expresses the sizing scenario's guard rota exactly, and a skip must plant what it claims to.

`07_Scenario_Authoring.md` §8.5 sets the honest test of the rota construct: re-expressed as a rota, the
sizing scenario's guard postings must produce **byte-identical** departure seconds and ids from
civil-time literals, or the rota does not express what the triple loop expressed. This compares
against the 335 guard trips of the route file that loop shipped
(`fixtures/Shahid_Bahonar_Port_PatternOfLife.shipped.rou.xml`, whose `t = 0` is midnight of day 0),
not a transcription of them, and holds the rota under test to the one the generator now writes.
"""
from __future__ import annotations

import importlib.util
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.CivilTimeResolver import CivilTimeResolver  # noqa: E402
from carlacontrol.CompileFindings import CompileFindings  # noqa: E402
from carlacontrol.RotaExpander import RotaExpander  # noqa: E402
from carlacontrol.ScenarioEpoch import ScenarioEpoch  # noqa: E402

EPOCH = ScenarioEpoch.read({
    "epoch_version": 1, "civil_datetime": "2026-03-21T00:00:00+03:30", "utc_offset_hours": 3.5,
    "utc_datetime": "2026-03-20T20:30:00Z", "calendar_advances": True, "dst_in_effect": False})

TOWERS = [f"tower_{n:02d}" for n in range(16)]

GUARD_ROTA = {
    "id": "guard_posting",
    "days": "0..6",
    "at": ["07:00", "15:00", "23:00"],
    "id_pattern": "guard_d{day}_h{hour}_t{subject_index}",
    "skip": [{"day": 4, "at": "07:00", "subject_index": 3,
              "because": "the no-show: this post is not manned this shift"}],
}


def expander(span_end_s=7 * 86_400):
    findings = CompileFindings()
    resolver = CivilTimeResolver(EPOCH, findings, span_end_s=span_end_s)
    return RotaExpander(resolver, findings), findings


SHIPPED_ROUTES = (Path(__file__).resolve().parent / "fixtures"
                  / "Shahid_Bahonar_Port_PatternOfLife.shipped.rou.xml")


def load_bahonar_generator():
    path = _REPO / "CarlaControl" / "scripts" / "make_bahonar_scenario.py"
    spec = importlib.util.spec_from_file_location("make_bahonar_scenario", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def shipped_guard_postings() -> list[tuple[str, float]]:
    """Every guard trip of the shipped route file: its id and its departure second."""
    return [(trip.get("id"), float(trip.get("depart")))
            for trip in ET.parse(SHIPPED_ROUTES).getroot().iter("trip")
            if trip.get("id").startswith("guard_")]


def test_the_guard_rota_reproduces_the_shipped_postings_byte_for_byte():
    shipped = shipped_guard_postings()
    rota, findings = expander()
    entries, _ = rota.expand(GUARD_ROTA, TOWERS)
    assert not findings.findings
    assert len(entries) == len(shipped) == 335
    assert sorted((e.entry_id, e.depart.seconds) for e in entries) == sorted(shipped)


def test_the_rota_under_test_is_the_one_the_generator_writes():
    """Days, clocks, id pattern and the skipped occasion; the skip's wording is the generator's."""
    written = load_bahonar_generator().BahonarPatternOfLifeSpecification(
        None, days=7, no_show_day=4, no_show_hour=7, no_show_tower=3, step_length_s=1.0,
        seed=42).guard_rota()
    for key in ("days", "at", "id_pattern"):
        assert written[key] == GUARD_ROTA[key], key
    (skip,) = written["skip"]
    assert {k: v for k, v in skip.items() if k != "because"} == \
        {k: v for k, v in GUARD_ROTA["skip"][0].items() if k != "because"}
    assert written["subjects"] == {"place_set": "guard_towers"}


def test_the_skip_is_the_one_occasion_removed_with_its_reason_and_its_civil_time():
    rota, findings = expander()
    _, skips = rota.expand(GUARD_ROTA, TOWERS)
    assert len(skips) == 1
    skip = skips[0]
    assert skip.entry_id == "guard_d4_h7_t3"
    assert skip.depart.seconds == 370_800.0
    assert skip.depart.civil == "2026-03-25T07:00:00+03:30"
    assert "not manned" in skip.because
    assert skip.subject == "tower_03"


def test_every_departure_carries_its_civil_meaning():
    rota, _ = expander()
    entries, _ = rota.expand(GUARD_ROTA, TOWERS)
    first = next(e for e in entries if e.entry_id == "guard_d0_h7_t0")
    assert first.depart.civil == "2026-03-21T07:00:00+03:30"
    assert first.depart.authored == "d0 07:00"


def test_a_skip_that_matches_nothing_is_refused():
    rota, findings = expander()
    stale = dict(GUARD_ROTA, skip=[{"day": 9, "at": "07:00", "subject_index": 3, "because": "x"}])
    rota.expand(stale, TOWERS)
    assert any("removes nothing" in f.message for f in findings.by_check(48))


def test_a_skip_without_a_reason_is_refused():
    rota, findings = expander()
    silent = dict(GUARD_ROTA, skip=[{"day": 4, "at": "07:00", "subject_index": 3}])
    rota.expand(silent, TOWERS)
    assert any("because" in f.message for f in findings.by_check(48))


def test_an_id_pattern_that_collides_is_refused():
    rota, findings = expander()
    rota.expand(dict(GUARD_ROTA, id_pattern="guard_d{day}_h{hour}", skip=[]), TOWERS)
    assert any("gives" in f.message for f in findings.by_check(48))


@pytest.mark.parametrize(("change", "fragment"), [
    ({"days": []}, "no days"),
    ({"at": []}, "no 'at'"),
    ({"days": "6..0"}, "no days"),
])
def test_a_rota_that_expands_to_nothing_is_refused(change, fragment):
    rota, findings = expander()
    rota.expand(dict(GUARD_ROTA, skip=[], **change), TOWERS)
    assert any(fragment in f.message for f in findings.by_check(48))


def test_a_clock_with_a_day_in_it_is_refused_because_the_day_comes_from_days():
    rota, findings = expander()
    rota.expand(dict(GUARD_ROTA, at=["d1 07:00"], skip=[]), TOWERS)
    assert findings.by_check(47)


def test_a_departure_past_the_run_is_left_to_the_caller_to_range_check():
    """The rota resolves; whether a departure may lie past the end is the actor rule's (check 37)."""
    rota, findings = expander(span_end_s=86_400)
    entries, _ = rota.expand(dict(GUARD_ROTA, days="0..1", skip=[]), TOWERS)
    assert len(entries) == 2 * 3 * 16
    assert not findings.findings
