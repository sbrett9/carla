"""A label parameter declared `time` is written as any time a specification takes, and the supervision
plan carries the second it falls on and the civil instant the epoch names it.

Text that holds a civil instant reads as the clock it was written against, so a reader cannot place
it on another epoch. A parameter declared `time` is resolved by `CivilTimeResolver`, as an
interval's begin is, and compiles to `{"seconds", "civil"}`. Held here, with no SUMO run:

* every form of a time -- seconds, a day and clock, a clock alone on a run of one day, an absolute
  instant, a named instant, and an offset from either -- compiles to that object on an instance, a
  series and a cohort, and a parameter of another type is carried as written;
* a value that does not resolve is refused under check 47 alone; one naming an undeclared instant
  or falling before t = 0 under checks 8 and 37, as any time is; and a key two labels declare
  differently only under check 56;
* the specification's schema admits the type and each form of value and still refuses an object
  that is no time (check 53); the supervision plan's schema admits the object and refuses a time
  left as written.
"""
from __future__ import annotations

import sys
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.AnnotationVocabulary import AnnotationVocabulary  # noqa: E402
from carlacontrol.CivilTimeResolver import CivilTimeResolver, ResolvedInstant  # noqa: E402
from carlacontrol.CompileFindings import CompileFindings  # noqa: E402
from carlacontrol.ScenarioEpoch import ScenarioEpoch  # noqa: E402
from carlacontrol.ScenarioOutputSchemas import ScenarioOutputSchemas  # noqa: E402
from carlacontrol.ScenarioSchema import ScenarioSchema  # noqa: E402
from carlacontrol.SchemaPublication import SchemaPublication  # noqa: E402
from carlacontrol.SupervisionPlanCompiler import (  # noqa: E402
    SupervisionInputs,
    SupervisionPlanCompiler,
)

# Bahonar's: 07:00 at +03:30 on 29 September 2026.
EPOCH = ScenarioEpoch.read({
    "epoch_version": 1, "civil_datetime": "2026-09-29T07:00:00+03:30", "utc_offset_hours": 3.5,
    "utc_datetime": "2026-09-29T03:30:00Z", "calendar_advances": True, "dst_in_effect": False})
WEEK = 7 * 86_400
INSTANTS = {"shift_change": "d4 07:00"}

NAMESPACE = {
    "namespace": "shift", "version": 1, "authority": "this test",
    "terms": [
        {"term": "shift:post_left", "since": 1, "status": "active", "applies_to": ["entity"],
         "definition": "A guard due at a post parks elsewhere for the shift.",
         "parameters": {
             "expected_at": {"type": "time", "definition": "when the shift it was due to take "
                                                           "up began"},
             "expected_post": {"type": "string", "definition": "the post it was due at"}}},
        {"term": "shift:posting", "since": 1, "status": "active", "applies_to": ["entity"],
         "definition": "A guard posted for a shift.",
         "parameters": {"expected_at": {"type": "time", "definition": "when the shift began"}}},
        {"term": "shift:convoy", "since": 1, "status": "active", "applies_to": ["cohort"],
         "definition": "Vehicles that pass in one column.",
         "parameters": {"expected_at": {"type": "time", "definition": "when the column was due"}}},
        {"term": "shift:post_named", "since": 1, "status": "active", "applies_to": ["entity"],
         "definition": "A guard whose post is named in words.",
         "parameters": {"expected_at": {"type": "string", "definition": "the shift, in words"}}},
    ],
}

INPUTS = SupervisionInputs(
    scenario_id="shifts",
    actor_departures={"guard": ResolvedInstant(345_600.0, "2026-10-03T07:00:00+03:30",
                                               "d4 07:00", "day_clock")},
    flow_ids=["column"], rota_templates={"posting": {}}, rota_entries={"posting": []})


def block(value: object, labels: tuple[str, ...] = ("shift:post_left",)) -> dict:
    """An instance, a series and a cohort, each carrying `expected_at` as `value`."""
    return {
        "instances": [{"name": "post_left", "supervision": "annotated", "labels": list(labels),
                       "participants": [{"actor": "guard", "role": "subject"}],
                       "parameters": {"expected_at": value, "expected_post": "tower_03"}}],
        "series": [{"series_id": "postings", "rota": "posting", "member_role": "subject",
                    "slot_length": "8h", "slot_aoi_refs": {}, "supervision": "nominal",
                    "labels": ["shift:posting"], "parameters": {"expected_at": value}}],
        "cohorts": [{"flow": "column", "supervision": "annotated", "labels": ["shift:convoy"],
                     "parameters": {"expected_at": value}}],
    }


def compile_block(supervision: dict, span_end_s: float = WEEK) -> tuple[dict, CompileFindings]:
    findings = CompileFindings()
    vocabulary = AnnotationVocabulary({"shift": NAMESPACE}, findings)
    resolver = CivilTimeResolver(EPOCH, findings, instants=INSTANTS, span_end_s=span_end_s)
    plan = SupervisionPlanCompiler(vocabulary, resolver, findings).compile(supervision, INPUTS)
    return plan, findings


def rows(plan: dict) -> list[dict]:
    return [*plan["instances"], *plan["series"], *plan["cohorts"]]


@pytest.mark.parametrize(("authored", "seconds", "civil", "span_end_s"), [
    (345_600, 345_600.0, "2026-10-03T07:00:00+03:30", WEEK),
    ("d4 07:00", 345_600.0, "2026-10-03T07:00:00+03:30", WEEK),
    ("08:00", 3_600.0, "2026-09-29T08:00:00+03:30", 86_400.0),
    ("2026-10-03T07:00:00+03:30", 345_600.0, "2026-10-03T07:00:00+03:30", WEEK),
    ({"instant": "shift_change"}, 345_600.0, "2026-10-03T07:00:00+03:30", WEEK),
    ({"instant": "shift_change", "plus": "15m"}, 346_500.0, "2026-10-03T07:15:00+03:30", WEEK),
    ({"at": "d4 07:00", "plus": 274}, 345_874.0, "2026-10-03T07:04:34+03:30", WEEK),
], ids=["seconds", "day_clock", "clock", "absolute", "named", "named_plus", "at_plus"])
def test_a_time_parameter_in_each_form_compiles_to_its_second_and_civil_instant(
        authored, seconds, civil, span_end_s):
    plan, findings = compile_block(block(authored), span_end_s)
    assert findings.refusals == []
    for row in rows(plan):
        assert row["parameters"]["expected_at"] == {"seconds": seconds, "civil": civil}
    assert plan["instances"][0]["parameters"]["expected_post"] == "tower_03"


@pytest.mark.parametrize(("authored", "says"), [
    ("the morning shift", "'the morning shift' not a time in any accepted form"),
    (True, "true a time is a number of seconds or a civil time, not true or false"),
    ("2026-10-03T07:00:00+04:30", "carries the offset +04:30 and the epoch declares +03:30"),
    ("07:00", "a clock alone names no day, and this run spans 7 days"),
    ({"plus": "15m"}, "a composite time is {\"instant\": name} or {\"at\": time}"),
])
def test_a_time_parameter_that_does_not_resolve_is_refused_under_check_47(authored, says):
    _, findings = compile_block(block(authored))
    assert {f.check_id for f in findings.refusals} == {47}
    subjects = {f.subject for f in findings.by_check(47)}
    assert subjects == {"instance post_left parameter expected_at",
                        "series postings parameter expected_at",
                        "cohort column parameter expected_at"}
    assert all(says in f.message for f in findings.by_check(47))


@pytest.mark.parametrize(("authored", "check"), [({"instant": "relief"}, 8), (-60, 37)])
def test_a_time_parameter_is_refused_as_any_time_is(authored, check):
    _, findings = compile_block(block(authored))
    assert {f.check_id for f in findings.refusals} == {check}


def test_a_key_two_labels_declare_differently_is_refused_only_under_check_56():
    """The labels disagree on what the value is, so nothing reads it as a time."""
    supervision = block("the morning shift", labels=("shift:post_left", "shift:post_named"))
    supervision.update(series=[], cohorts=[])
    _, findings = compile_block(supervision)
    assert {f.check_id for f in findings.refusals} == {56}
    assert "'shift:post_left' as time; 'shift:post_named' as string" in \
        findings.by_check(56)[0].message


@pytest.mark.parametrize("authored", [345_600, "d4 07:00", "2026-10-03T07:00:00+03:30",
                                      {"instant": "shift_change"},
                                      {"instant": "shift_change", "plus": "15m"},
                                      {"at": "d4 07:00", "plus": 274}])
def test_the_specification_schema_admits_a_time_parameter_in_each_form(authored):
    assert ScenarioSchema.validate_definition(NAMESPACE, "namespace", "$") == []
    assert ScenarioSchema.validate_definition(block(authored), "supervision", "$") == []


def test_the_specification_schema_still_refuses_an_object_that_is_no_time():
    problems = ScenarioSchema.validate_definition(block({"value": 360, "unit": "s"}),
                                                  "supervision", "$")
    assert any(p.startswith("$.instances[0].parameters.expected_at") for p in problems)


@pytest.mark.parametrize("definition", ["instance", "series", "cohort"])
def test_the_plan_schema_admits_the_compiled_object_and_refuses_a_time_left_as_written(definition):
    plan, _ = compile_block(block({"instant": "shift_change"}))
    row = {"instance": plan["instances"], "series": plan["series"],
           "cohort": plan["cohorts"]}[definition][0]
    schema = ScenarioOutputSchemas.supervision_plan()
    of_row = {"$defs": schema["$defs"], "$ref": f"#/$defs/{definition}"}
    assert SchemaPublication.problems(row, of_row) == []
    row["parameters"]["expected_at"] = {"instant": "shift_change"}
    assert SchemaPublication.problems(row, of_row)
