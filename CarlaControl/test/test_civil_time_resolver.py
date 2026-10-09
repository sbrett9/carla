"""An author writes civil time and the compiler emits seconds, with the session's function as authority.

Every form of `07_Scenario_Authoring.md` §3.5.1 is exercised against a known answer, including the one
that motivates the whole construct: the sizing scenario's probe departs `day * 137` seconds after
11:00, an instant its generator states nowhere, and the resolver names it d2 11:04:34 and d5 11:11:25.
The refusals are exercised too, because a resolver that never refused a bare clock on a multi-day
scenario would let SUMO's `H:M:S`-is-an-offset trap back in.
"""
from __future__ import annotations

import sys
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.CivilTimeResolver import CivilTimeResolver  # noqa: E402
from carlacontrol.CompileFindings import CompileFindings  # noqa: E402
from carlacontrol.ScenarioEpoch import ScenarioEpoch  # noqa: E402

MIDNIGHT = ScenarioEpoch.read({
    "epoch_version": 1, "civil_datetime": "2026-03-21T00:00:00+03:30", "utc_offset_hours": 3.5,
    "utc_datetime": "2026-03-20T20:30:00Z", "calendar_advances": True, "dst_in_effect": False})

SIX_AM = ScenarioEpoch.read({
    "epoch_version": 1, "civil_datetime": "2026-03-21T06:00:00+03:30", "utc_offset_hours": 3.5,
    "utc_datetime": "2026-03-21T02:30:00Z", "calendar_advances": True, "dst_in_effect": False})

WEEK = 7 * 86_400


def resolver(epoch=MIDNIGHT, span_end_s=WEEK, instants=None):
    findings = CompileFindings()
    return CivilTimeResolver(epoch, findings, instants=instants, span_end_s=span_end_s), findings


@pytest.mark.parametrize(("authored", "seconds", "civil"), [
    (25200, 25200.0, "2026-03-21T07:00:00+03:30"),
    ("d0 07:00", 25200.0, "2026-03-21T07:00:00+03:30"),
    ("d3 23:00", 3 * 86_400 + 82_800.0, "2026-03-24T23:00:00+03:30"),
    ("d6 02:30", 6 * 86_400 + 9_000.0, "2026-03-27T02:30:00+03:30"),
    ("d1 00:00:15", 86_415.0, "2026-03-22T00:00:15+03:30"),
    ("d0 24:00", 86_400.0, "2026-03-22T00:00:00+03:30"),
    ("2026-03-25T07:00:00+03:30", 370_800.0, "2026-03-25T07:00:00+03:30"),
])
def test_each_form_resolves_to_its_second_and_is_named_by_the_session(authored, seconds, civil):
    time, findings = resolver()
    resolved = time.instant(authored, "test")
    assert not findings.findings
    assert resolved.seconds == seconds
    assert resolved.civil == civil


def test_the_day_counts_from_the_epoch_date_and_the_clock_from_its_offset():
    """With t = 0 at 06:00, 07:00 on day 0 is one hour in, not seven."""
    time, findings = resolver(SIX_AM)
    assert time.instant("d0 07:00", "test").seconds == 3600.0
    assert time.instant("d1 06:00", "test").seconds == 86_400.0
    assert not findings.findings


def test_an_instant_before_t0_is_refused_under_check_37_naming_the_epoch():
    time, findings = resolver(SIX_AM)
    assert time.instant("d0 05:00", "actor early") is None
    refused = findings.by_check(37)
    assert len(refused) == 1 and "2026-03-21T06:00:00+03:30" in refused[0].message


def test_a_clock_alone_is_refused_on_a_multi_day_run_and_accepted_on_a_single_day():
    time, findings = resolver(span_end_s=WEEK)
    assert time.instant("07:00", "actor a") is None
    assert findings.by_check(47) and "d0 07:00" in findings.by_check(47)[0].message
    one_day, quiet = resolver(span_end_s=3600.0)
    assert one_day.instant("07:00", "actor a").seconds == 25200.0
    assert not quiet.findings


def test_an_absolute_instant_at_another_offset_is_refused():
    time, findings = resolver()
    assert time.instant("2026-03-25T07:00:00+04:30", "actor a") is None
    assert "+04:30" in findings.by_check(47)[0].message


def test_the_probe_jitter_the_generator_never_states_is_named():
    """`make_bahonar_scenario.py` departs each probe at `day * DAY + 11 * HOUR + day * 137`."""
    time, findings = resolver()
    assert time.instant({"at": "d2 11:00", "plus": 2 * 137}, "probe_d2").civil == \
        "2026-03-23T11:04:34+03:30"
    assert time.instant({"at": "d5 11:00", "plus": 5 * 137}, "probe_d5").civil == \
        "2026-03-26T11:11:25+03:30"
    assert not findings.findings


def test_named_instants_resolve_with_an_offset_and_refuse_when_undeclared_or_circular():
    time, findings = resolver(instants={"night_shift": "d3 23:00",
                                        "relief": {"instant": "night_shift", "plus": "8h"},
                                        "a": {"instant": "b"}, "b": {"instant": "a"}})
    assert time.instant({"instant": "relief"}, "x").seconds == 4 * 86_400 + 7 * 3600.0
    assert time.instant({"instant": "missing"}, "x") is None
    assert findings.by_check(8)
    assert time.instant({"instant": "a"}, "x") is None
    assert any("itself" in f.message for f in findings.by_check(47))


@pytest.mark.parametrize(("authored", "seconds"), [
    ("8h", 28_800.0), ("30m", 1_800.0), ("1h30m", 5_400.0), ("274s", 274.0), ("7d", float(WEEK)),
    (300, 300.0),
])
def test_durations(authored, seconds):
    time, findings = resolver()
    assert time.duration(authored, "test") == seconds
    assert not findings.findings


@pytest.mark.parametrize("authored", ["8 hours", "-5m", "", True, "h", -300])
def test_a_malformed_duration_is_refused(authored):
    time, findings = resolver()
    assert time.duration(authored, "test") is None
    assert findings.by_check(47)


@pytest.mark.parametrize("authored", ["7:00:00 PM", "d0 25:00", "d0 07:61", "tomorrow", [7]])
def test_a_malformed_time_is_refused(authored):
    time, findings = resolver()
    assert time.instant(authored, "test") is None
    assert findings.by_check(47)


def test_an_instant_after_the_run_is_refused_by_the_span_check():
    time, findings = resolver(span_end_s=86_400.0)
    resolved = time.instant("d1 07:00", "actor late")
    assert not time.require_in_span(resolved, "actor late")
    assert "after the run ends" in findings.by_check(37)[0].message


def test_a_quarter_hour_offset_maps_seconds_linearly_onto_civil_time():
    nepal = ScenarioEpoch.read({
        "epoch_version": 1, "civil_datetime": "2026-06-01T00:00:00+05:45",
        "utc_offset_hours": 5.75, "utc_datetime": "2026-05-31T18:15:00Z",
        "calendar_advances": True, "dst_in_effect": False})
    time, findings = resolver(nepal)
    assert time.instant("d2 05:45", "test").seconds == 2 * 86_400 + 5 * 3600 + 45 * 60.0
    assert time.instant("d2 05:45", "test").civil == "2026-06-03T05:45:00+05:45"
    assert not findings.findings


def test_the_session_function_is_the_authority_and_a_disagreement_stops_the_compile(monkeypatch):
    """Every day-clock instant is handed back to `SolarEpoch.CivilInstantAt`; if the civil instant it
    returns is not the one written, the compile stops rather than printing a second it cannot vouch
    for. Simulated here by an epoch whose civil_instant_at answers an hour late."""
    time, _ = resolver()
    true_answer = MIDNIGHT.civil_instant_at
    monkeypatch.setattr(MIDNIGHT, "civil_instant_at",
                        lambda seconds: true_answer(seconds + 3600))
    with pytest.raises(RuntimeError, match="compiler defect"):
        time.instant("d0 07:00", "test")
