"""The plan, the shipped skill and the compiler name the same checks, with the same outcomes.

`07_Scenario_Authoring.md` §8.5: `checks.json` and the schemas are generated from the compiler, never
written beside the skill, and a check added to the compiler appears in the shipped skill without anyone
remembering to update it. These tests make that a property rather than an intention: the skill's copies
must equal what the generators produce now, the table in the plan must list exactly the catalogue's
live checks, and every check id the compiler's source cites must be in the catalogue (charter §4a:
every name a check references must exist).
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.ScenarioCheck import RETIRED  # noqa: E402
from carlacontrol.ScenarioCheckCatalogue import ScenarioCheckCatalogue  # noqa: E402
from carlacontrol.ScenarioSchema import SCHEMA  # noqa: E402
from carlacontrol.ScenarioSweep import SWEEP_SCHEMA  # noqa: E402

SKILL = _REPO / "CarlaControl" / "skills" / "sumo-traffic-scenarios"
PLAN = _REPO / "Docs" / "CAT_Research" / "Plans" / "SUMO_Behavioral_Capture" / "07_Scenario_Authoring.md"
SOURCES = _REPO / "CarlaControl" / "src" / "carlacontrol"


def test_the_shipped_checks_json_is_the_catalogue():
    shipped = json.loads((SKILL / "checks.json").read_text(encoding="utf-8"))
    assert shipped == ScenarioCheckCatalogue.to_document()


def test_the_shipped_schemas_are_the_compilers_own():
    assert json.loads((SKILL / "schemas" / "scenario.schema.json").read_text(encoding="utf-8")) == SCHEMA
    assert json.loads((SKILL / "schemas" / "sweep.schema.json").read_text(encoding="utf-8")) == \
        SWEEP_SCHEMA


def plan_table_rows() -> dict[int, str]:
    text = PLAN.read_text(encoding="utf-8")
    section = text[text.index("### 5.2 The checks"):text.index("**Two notes on the table.**")]
    return {int(match[1]): match[2] for match in re.finditer(r"^\| (\d+) \|(.*)$", section, re.M)}


def test_the_plan_lists_exactly_the_catalogues_checks():
    rows = plan_table_rows()
    live = ScenarioCheckCatalogue.active_ids()
    retired = {c.check_id for c in ScenarioCheckCatalogue.all() if c.status == RETIRED}
    assert set(rows) == live | retired
    for check_id in retired:
        assert "retired" in rows[check_id].lower(), check_id


def test_the_plan_states_each_checks_outcome_as_the_catalogue_does():
    rows = plan_table_rows()
    for check in ScenarioCheckCatalogue.all():
        if check.status == RETIRED:
            continue
        row = rows[check.check_id].lower()
        for outcome in check.outcomes:
            assert f"**{outcome}" in row, (check.check_id, outcome)


def test_every_check_the_source_cites_is_in_the_catalogue():
    cited = set()
    pattern = re.compile(r"\.(?:refuse|warn)\(\s*(\d+)\s*,")
    for path in SOURCES.glob("*.py"):
        cited |= {int(n) for n in pattern.findall(path.read_text(encoding="utf-8"))}
    constants = {"FORM_CHECK": 47, "SPAN_CHECK": 37, "ROTA_CHECK": 48}
    assert cited, "no check id cited in the compiler's source"
    assert cited | set(constants.values()) <= ScenarioCheckCatalogue.active_ids()
