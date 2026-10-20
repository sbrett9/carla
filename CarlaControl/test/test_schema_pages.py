"""Every reference page under Docs/Schemas keeps the page template.

The pages describe one kind of file each, and a reader who has learnt one must find the same things
in the same places on the next. `Scripts/Docs/check_schema_pages.py` names every departure from the
template, and the `schema-page` skill under `.claude/skills/` says how to write a page that fits.
"""
from __future__ import annotations

import importlib.util
from pathlib import Path

_REPO = Path(__file__).resolve().parents[2]
CHECKER = _REPO / "Scripts" / "Docs" / "check_schema_pages.py"
SCHEMAS = _REPO / "Docs" / "Schemas"


def load_checker():
    spec = importlib.util.spec_from_file_location("check_schema_pages", CHECKER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def test_every_schema_page_keeps_the_template():
    checker = load_checker()
    pages = sorted(p for p in SCHEMAS.glob("*.md") if p.name not in checker.NOT_PAGES)
    assert len(pages) >= 41, "the schema pages are missing"
    departures = {page.name: checker.check_page(page) for page in pages}
    departures = {name: found for name, found in departures.items() if found}
    assert not departures, "\n".join(
        f"{name}:{line}: {what}" for name, found in departures.items() for line, what in found)


def test_the_skill_s_template_page_keeps_the_template():
    checker = load_checker()
    template = _REPO / ".claude" / "skills" / "schema-page" / "references" / "template.md"
    assert template.is_file()
    assert checker.check_page(template) == []
