"""The compiler reads a scenario's epoch with the reader the session uses, and attributes its refusals.

`ScenarioEpoch` hands the `epoch` object to `CarlaNet.CoSim.SolarEpoch` rather than re-implementing
its rules, so a scenario the compiler accepts is one the session accepts, with the same digest and the
same civil instant for every simulated second. These tests pin that delegation from the outside: the
rules fire (check 33 and check 34, each attributed), the digest is the one Python's canonical form
gives, a half-hour or quarter-hour offset is ordinary, and a zone name is carried without ever being
resolved.
"""
from __future__ import annotations

import hashlib
import json
import sys
from datetime import timedelta
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.ScenarioEpoch import ScenarioEpoch, ScenarioEpochRefusedError  # noqa: E402

# The sizing site's epoch: midnight of day 0 at the port, Iran at +03:30 with no daylight saving.
BAHONAR = {
    "epoch_version": 1,
    "civil_datetime": "2026-03-21T00:00:00+03:30",
    "utc_offset_hours": 3.5,
    "utc_datetime": "2026-03-20T20:30:00Z",
    "calendar_advances": True,
    "dst_in_effect": False,
    "time_zone_id": "Asia/Tehran",
}


def epoch(**changes) -> dict:
    document = dict(BAHONAR)
    for key, value in changes.items():
        if value is None:
            document.pop(key, None)
        else:
            document[key] = value
    return document


def refusal(declaration) -> ScenarioEpochRefusedError:
    with pytest.raises(ScenarioEpochRefusedError) as raised:
        ScenarioEpoch.read(declaration)
    return raised.value


def test_the_sizing_site_epoch_reads_and_states_itself():
    read = ScenarioEpoch.read(BAHONAR)
    assert read.utc_offset_hours == 3.5
    assert read.calendar_advances is True and read.dst_in_effect is False
    assert read.civil_instant_at(0) == "2026-03-21T00:00:00+03:30"
    assert "2026-03-21T00:00:00+03:30" in read.describe()


def test_the_digest_is_the_canonical_python_form_the_contract_names():
    """04 §11.2: the digest is SHA-256 of `json.dumps(epoch, sort_keys=True, indent=2)`. The compiler
    records SolarEpoch's digest; this pins that it is exactly the Python form, so a record written by
    either language names the epoch the same way."""
    read = ScenarioEpoch.read(BAHONAR)
    canonical = json.dumps(BAHONAR, sort_keys=True, indent=2)
    assert read.canonical_json == canonical
    assert read.digest == hashlib.sha256(canonical.encode("utf-8")).hexdigest()


def test_no_epoch_is_refused_under_check_33():
    assert [check for check, _ in refusal(None).problems] == [33]


@pytest.mark.parametrize("offset", [3.6, 3.2, 1.1])
def test_an_offset_that_is_not_a_whole_number_of_quarter_hours_is_refused_under_check_34(offset):
    problems = refusal(epoch(utc_offset_hours=offset)).problems
    assert any(check == 34 and "quarter hours" in text for check, text in problems)


def test_an_offset_outside_the_engine_range_is_refused_under_check_34():
    problems = refusal(epoch(utc_offset_hours=14.5, civil_datetime="2026-03-21T00:00:00+14:30",
                             utc_datetime="2026-03-20T09:30:00Z")).problems
    assert any(check == 34 for check, _ in problems)


def test_every_problem_is_reported_not_only_the_first():
    problems = refusal(epoch(epoch_version=2, utc_offset_hours=3.6, dst_in_effect=None)).problems
    checks = sorted(check for check, _ in problems)
    assert checks.count(33) >= 2 and 34 in checks
    assert any("dst_in_effect" in text for _, text in problems)


def test_an_offset_applied_in_the_wrong_direction_is_refused_and_named():
    """The likeliest authoring error: +03:30 added rather than subtracted, a seven-hour error."""
    problems = refusal(epoch(utc_datetime="2026-03-21T03:30:00Z")).problems
    assert any(check == 33 and "wrong direction" in text for check, text in problems)


def test_a_civil_time_without_an_offset_is_refused():
    problems = refusal(epoch(civil_datetime="2026-03-21T00:00:00")).problems
    assert any("no offset" in text for _, text in problems)


def test_an_impossible_date_is_refused_rather_than_clamped():
    problems = refusal(epoch(civil_datetime="2026-02-31T00:00:00+03:30",
                             utc_datetime="2026-02-30T20:30:00Z")).problems
    assert any("civil_datetime" in text for _, text in problems)


def test_an_unknown_field_is_refused():
    problems = refusal(epoch(dst_policy="fixed_offset")).problems
    assert any("dst_policy" in text for _, text in problems)


@pytest.mark.parametrize(("civil", "hours", "utc"), [
    ("2026-06-01T00:00:00+05:45", 5.75, "2026-05-31T18:15:00Z"),    # Nepal
    ("2026-06-01T00:00:00+12:45", 12.75, "2026-05-31T11:15:00Z"),   # Chatham
    ("2026-06-01T00:00:00-03:30", -3.5, "2026-06-01T03:30:00Z"),    # Newfoundland
])
def test_half_and_quarter_hour_offsets_are_ordinary(civil, hours, utc):
    read = ScenarioEpoch.read(epoch(civil_datetime=civil, utc_offset_hours=hours, utc_datetime=utc,
                                    time_zone_id=None))
    assert read.utc_offset_hours == hours
    assert read.civil_instant_at(86_400).endswith(civil[-6:])
    assert read.civil_instant_at(86_400)[:10] == "2026-06-02"


def test_a_zone_name_is_carried_and_never_resolved():
    """04 §11.3: `time_zone_id` is provenance only. A name no database holds is accepted and carried
    verbatim; a compiler that resolved it would refuse this, or refuse every name on this machine,
    where zoneinfo holds zero zones."""
    read = ScenarioEpoch.read(epoch(time_zone_id="Nowhere/Not_A_Zone"))
    assert read.time_zone_id == "Nowhere/Not_A_Zone"


def test_the_inverse_of_civil_instant_at_agrees_with_it():
    read = ScenarioEpoch.read(epoch(civil_datetime="2026-03-21T06:00:00+03:30",
                                    utc_datetime="2026-03-21T02:30:00Z"))
    instant = read.civil_on_day(1, timedelta(hours=7))
    seconds = read.seconds_at(instant)
    assert seconds == 25 * 3600
    assert read.civil_instant_at(seconds) == "2026-03-22T07:00:00+03:30"
