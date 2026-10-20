"""The published run configuration page lists every field, with the default the field table gives.

The run configuration's field table is the single definition of its defaults, and
`Docs/Schemas/Run_Configuration.md` is where an operator reads them. A default changed in one and not
the other tells every operator a value the tool does not use, so the two are held equal here by reading
the page's own tables: every field has a row, and every row gives the field table's default, or `none`
where the field has no default. Every run check the published documents cite by number is one the
check catalogue carries.
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.RunConfiguration import RunConfiguration  # noqa: E402
from carlacontrol.RunConfigurationCheckCatalogue import (  # noqa: E402
    RETIRED,
    RunConfigurationCheckCatalogue,
)

PAGE = _REPO / "Docs" / "Schemas" / "Run_Configuration.md"
PUBLISHED = [_REPO / "Docs" / folder for folder in ("Guides", "EPOL", "Tracking", "Schemas", "Skills")]
PUBLISHED.append(_REPO / "CarlaControl" / "skills" / "sumo-traffic-scenarios")
NO_DEFAULT = "none"


def _page_defaults() -> dict[str, str]:
    """Every field the page's tables name, as field -> its Default cell.

    The page has two tables, the document's fields and a channel's fields. A row may name several
    fields that share one default.
    """
    text = PAGE.read_text(encoding="utf-8")
    cells_by_field: dict[str, str] = {}
    for start in (m.start() for m in re.finditer(r"^\| Field \|", text, flags=re.MULTILINE)):
        lines = text[start:].splitlines()
        header = [cell.strip() for cell in lines[0].strip().strip("|").split("|")]
        default_column = header.index("Default")
        for line in lines[2:]:
            if not line.startswith("|"):
                break
            cells = [cell.strip() for cell in line.strip().strip("|").split("|")]
            for name in re.findall(r"`([^`]+)`", cells[0]):
                cells_by_field[name] = cells[default_column]
    return cells_by_field


def _fields() -> dict:
    return {**RunConfiguration.FIELDS, **RunConfiguration.channel_fields()}


def test_every_field_has_a_row_on_the_page():
    rows = _page_defaults()
    missing = [name for name in _fields() if name not in rows]
    assert missing == [], f"fields with no row in Run_Configuration.md: {missing}"


def test_every_default_on_the_page_is_the_field_table_s():
    rows = _page_defaults()
    for name, spec in _fields().items():
        cell = rows.get(name)
        if cell is None:
            continue
        if cell == NO_DEFAULT:
            assert not spec.has_default, \
                f"{name}: the page gives no default, the field table gives {spec.default!r}"
            continue
        literal = re.fullmatch(r"`([^`]+)`", cell)
        assert literal, f"{name}: the page's default cell {cell!r} is neither none nor one literal"
        assert spec.has_default, f"{name}: the page gives {cell}, the field table no default"
        assert json.loads(literal.group(1)) == spec.default, (name, cell, spec.default)


def _cited_run_checks() -> dict[int, list[str]]:
    cited: dict[int, list[str]] = {}
    for root in PUBLISHED:
        for page in sorted(root.rglob("*.md")):
            text = page.read_text(encoding="utf-8")
            for match in re.finditer(r"\brun checks? (\d+)(?: and (\d+))?", text):
                for number in filter(None, match.groups()):
                    cited.setdefault(int(number), []).append(page.relative_to(_REPO).as_posix())
    return cited


def test_the_published_documents_cite_run_checks():
    assert _cited_run_checks(), "no published page cites a run check, so the next test checks nothing"


def test_every_run_check_a_published_document_cites_is_catalogued():
    catalogued = {check.check_id for check in RunConfigurationCheckCatalogue.all()}
    assert not catalogued & set(RETIRED)
    unknown = {number: pages for number, pages in _cited_run_checks().items()
               if number not in catalogued}
    assert unknown == {}, f"run checks cited but not catalogued (or retired): {unknown}"
