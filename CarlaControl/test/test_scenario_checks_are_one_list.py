"""The shipped skill, the published documents and the compiler name the same checks.

`checks.json` and the schemas are generated from the compiler, never written beside the skill, so a
check added to the compiler appears in the shipped skill without anyone remembering to update it.
These tests make that a property rather than an intention: the skill's copies must equal what the
generators produce now, every check id the compiler's source cites must be in the catalogue, and
every check a published document cites by number must be a live one, so no document names a check
that does not exist.
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.ScenarioCheckCatalogue import ScenarioCheckCatalogue  # noqa: E402
from carlacontrol.ScenarioSchema import SCHEMA  # noqa: E402
from carlacontrol.ScenarioSweep import SWEEP_SCHEMA  # noqa: E402

SKILL = _REPO / "CarlaControl" / "skills" / "sumo-traffic-scenarios"
PUBLISHED = [_REPO / "Docs" / folder for folder in ("Guides", "EPOL", "Tracking", "Schemas", "Skills")]
PUBLISHED.append(SKILL)
SOURCES = _REPO / "CarlaControl" / "src" / "carlacontrol"


def test_the_shipped_checks_json_is_the_catalogue():
    shipped = json.loads((SKILL / "checks.json").read_text(encoding="utf-8"))
    assert shipped == ScenarioCheckCatalogue.to_document()


def test_the_shipped_schemas_are_the_compilers_own():
    assert json.loads((SKILL / "schemas" / "scenario.schema.json").read_text(encoding="utf-8")) == SCHEMA
    assert json.loads((SKILL / "schemas" / "sweep.schema.json").read_text(encoding="utf-8")) == \
        SWEEP_SCHEMA


def published_check_citations() -> dict[int, list[str]]:
    """Every scenario check a published page cites by number, as id -> pages. A "run check" is the
    run configuration's own numbering and is left out."""
    cited: dict[int, list[str]] = {}
    pattern = re.compile(r"(?<!run )\bchecks? (\d+)(?: (?:and|or|to) (\d+))?", re.IGNORECASE)
    for root in PUBLISHED:
        for page in sorted(root.rglob("*.md")):
            for match in pattern.finditer(page.read_text(encoding="utf-8")):
                for number in filter(None, match.groups()):
                    cited.setdefault(int(number), []).append(page.relative_to(_REPO).as_posix())
    return cited


def test_the_published_documents_cite_checks():
    assert published_check_citations(), "no published page cites a check, so the next test checks nothing"


def test_every_check_a_published_document_cites_is_live():
    live = ScenarioCheckCatalogue.active_ids()
    unknown = {number: pages for number, pages in published_check_citations().items()
               if number not in live}
    assert unknown == {}, f"checks cited but not live in the catalogue: {unknown}"


def test_every_check_the_source_cites_is_in_the_catalogue():
    """Every id the compiler reports is one the catalogue lists. The compiler's modules are the ones
    that report into `CompileFindings`; another catalogue in the package -- the run configuration's
    (`RunConfigurationCheckCatalogue`) -- numbers its own checks in its own findings."""
    cited = set()
    pattern = re.compile(r"\.(?:refuse|warn)\(\s*(\d+)\s*,")
    for path in SOURCES.glob("*.py"):
        text = path.read_text(encoding="utf-8")
        if "CompileFindings" in text:
            cited |= {int(n) for n in pattern.findall(text)}
    constants = {"FORM_CHECK": 47, "SPAN_CHECK": 37, "ROTA_CHECK": 48}
    assert cited, "no check id cited in the compiler's source"
    assert cited | set(constants.values()) <= ScenarioCheckCatalogue.active_ids()
