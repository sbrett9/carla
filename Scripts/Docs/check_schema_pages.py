"""Check the reference pages under Docs/CAT_Research/Schemas against their page template.

Each page there describes one kind of file these tools write or read. The template fixes the shape
every page shares, so a reader finds the same thing in the same place on every page:

  * the title, then a two-column header table with these rows, in this order:
    File, Schema, Schema id, Format version, Version field;
  * the sections, as second-level headings, in this order:
    Description, Writers and readers, (the page's own sections), Fields, Example, Checks,
    Versions, Related pages.
    Description, Fields, Example and Versions are on every page. The others are there when the
    page has something to put in them. A page's own sections, on topics only it has, sit between
    Writers and readers and Fields; everything that describes the file's content sits under Fields,
    as third-level headings where it needs dividing.
  * the Format version cell holds the number alone; the Version field cell names the field that
    carries it, in backticks, or says `none` when a file of the kind carries no version field.

The sentence rules are checked elsewhere (scratchpad/copyedit/check_layout.py); this script checks
the structure only. It names every departure by page and line, with the template's name for a
heading the older pages used, and exits 1 when any page departs, so a test can hold the folder to
the template.

Usage: python check_schema_pages.py [--json] [<page or folder> ...]
       With no argument, the Schemas folder of this repository.
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

REPOSITORY = Path(__file__).resolve().parents[2]
SCHEMAS = REPOSITORY / "Docs" / "CAT_Research" / "Schemas"
# The Markdown files in the folder that are not pages: the index, and the rules for an assistant.
NOT_PAGES = {"README.md", "AGENTS.md", "CLAUDE.md"}

HEADER_ROWS = ["File", "Schema", "Schema id", "Format version", "Version field"]
FIXED_SECTIONS = ["Description", "Writers and readers", "Fields", "Example", "Checks", "Versions",
                  "Related pages"]
REQUIRED_SECTIONS = ["Description", "Fields", "Example", "Versions"]
# A page's own sections are allowed only here: after the opening sections, before the content.
FREE_SECTIONS_AFTER = {"Description", "Writers and readers"}
# Headings the older pages used, with the template's name for each.
RENAMED = {
    "What it is": "Description",
    "What the file is": "Description",
    "Who writes it and who reads it": "Writers and readers",
    "Who writes it and when": "Writers and readers",
    "Who sends it and who receives it": "Writers and readers",
    "Who reads it": "Writers and readers",
    "Examples": "Example",
    "Checking a file": "Checks",
    "Format version": "the header rows Format version and Version field, with the history under "
                      "Versions",
    "When the version changes": "Versions",
}
VERSION_NUMBER = re.compile(r"^[1-9]\d*$")
# The field in backticks, with where it sits after a comma or in parentheses when that helps
# (`format_version`, on `<events>`); or `none`, then why.
VERSION_FIELD = re.compile(r"^(`[A-Za-z_][A-Za-z0-9_.]*`(, .+| \(.+\))?|none\b.*)$")
HEADING = re.compile(r"^(#{1,6}) (.+?)\s*$")
BOLD_LEAD = re.compile(r"^\*\*[^*]+:\*\*")
FENCE = re.compile(r"^[ \t]*(`{3,}|~{3,})")


def table_cells(line: str) -> list[str]:
    """The cells of a Markdown table row, trimmed; an empty list for a line that is not one."""
    if not line.startswith("|"):
        return []
    return [cell.strip() for cell in line.strip().strip("|").split("|")]


def check_page(path: Path) -> list[tuple[int, str]]:
    """Every departure of one page from the template, as (line number, what)."""
    findings: list[tuple[int, str]] = []
    lines = path.read_text(encoding="utf-8").split("\n")

    # The title.
    number = 1
    if not lines or not lines[0].startswith("# "):
        findings.append((1, "the page does not open with its title, a level-one heading"))
    else:
        number = 2

    # The header table, right after the title.
    while number <= len(lines) and not lines[number - 1].strip():
        number += 1
    first = lines[number - 1] if number <= len(lines) else ""
    if BOLD_LEAD.match(first):
        findings.append((number, "the page opens with bold leads instead of the header table "
                                 f"(rows {', '.join(HEADER_ROWS)})"))
    elif not first.startswith("|"):
        findings.append((number, "no header table after the title "
                                 f"(rows {', '.join(HEADER_ROWS)})"))
    else:
        rows: list[tuple[int, list[str]]] = []
        while number <= len(lines) and lines[number - 1].startswith("|"):
            cells = table_cells(lines[number - 1])
            if cells and not all(set(cell) <= set("-: ") for cell in cells) and any(cells):
                rows.append((number, cells))
            number += 1
        labels = [cells[0] for _, cells in rows if cells]
        if labels != HEADER_ROWS:
            findings.append((rows[0][0] if rows else number,
                             f"header rows are {labels}; the template's are {HEADER_ROWS}"))
        # A kind with no schema of its own, such as a container or a version declared by hand, has
        # no format version either; its page says `none` in both rows.
        schema_none = any(cells[0] == "Schema" and len(cells) > 1 and cells[1].startswith("none")
                          for _, cells in rows)
        for row_number, cells in rows:
            label = cells[0] if cells else ""
            value = cells[1] if len(cells) > 1 else ""
            if label == "Format version" and not VERSION_NUMBER.match(value) \
                    and not (schema_none and value == "none"):
                findings.append((row_number, f"Format version is '{value}'; the template wants the "
                                             "number alone, with the field under Version field"))
            if label == "Version field" and not VERSION_FIELD.match(value):
                findings.append((row_number, f"Version field is '{value}'; the template wants the "
                                             "field name in backticks, or 'none' with why"))

    # The sections.
    headings: list[tuple[int, int, str]] = []
    fence = None
    for line_number, line in enumerate(lines, 1):
        if fence:
            if re.match(r"^[ \t]*" + re.escape(fence[0]) + "{" + str(len(fence)) + r",}[ \t]*$", line):
                fence = None
            continue
        opened = FENCE.match(line)
        if opened:
            fence = opened.group(1)
            continue
        match = HEADING.match(line)
        if match:
            headings.append((line_number, len(match.group(1)), match.group(2)))
        elif BOLD_LEAD.match(line) and "Format version" in line:
            findings.append((line_number, "a bold Format version lead; the template puts the version "
                                          "in the header table"))

    if sum(1 for _, level, _ in headings if level == 1) != 1:
        findings.append((1, "the page has more than one level-one heading"))
    sections = [(line_number, text) for line_number, level, text in headings if level == 2]
    names = [text for _, text in sections]

    for line_number, text in sections:
        if text in RENAMED:
            findings.append((line_number, f"heading '{text}' is an older name; the template calls "
                                          f"it {RENAMED[text]}"))
    for required in REQUIRED_SECTIONS:
        if required not in names:
            findings.append((len(lines), f"no section '{required}'"))
    if names and names[0] != "Description":
        findings.append((sections[0][0], f"the first section is '{names[0]}'; the template opens "
                                         "with Description"))

    # The fixed sections keep their order, and a page's own sections sit in their place.
    last_fixed = -1
    last_fixed_name = None
    for line_number, text in sections:
        if text in FIXED_SECTIONS:
            index = FIXED_SECTIONS.index(text)
            if index < last_fixed:
                findings.append((line_number, f"section '{text}' comes after '{last_fixed_name}'; "
                                              f"the template's order is {', '.join(FIXED_SECTIONS)}"))
            last_fixed, last_fixed_name = max(last_fixed, index), text
        elif text not in RENAMED:
            if last_fixed_name not in FREE_SECTIONS_AFTER and last_fixed_name is not None:
                findings.append((line_number, f"the page's own section '{text}' comes after "
                                              f"'{last_fixed_name}'; the template places a page's "
                                              "own sections between Writers and readers and "
                                              "Fields, and content under Fields"))
            elif last_fixed_name is None:
                findings.append((line_number, f"the page's own section '{text}' comes before "
                                              "Description"))
    return sorted(findings)


def pages_of(targets: list[str]) -> list[Path]:
    paths: list[Path] = []
    for target in targets or [str(SCHEMAS)]:
        path = Path(target)
        if path.is_dir():
            paths += sorted(p for p in path.glob("*.md") if p.name not in NOT_PAGES)
        else:
            paths.append(path)
    return paths


def main(argv: list[str]) -> int:
    as_json = "--json" in argv
    targets = [a for a in argv if a != "--json"]
    report = {}
    for path in pages_of(targets):
        findings = check_page(path)
        if findings:
            report[str(path)] = findings
    if as_json:
        print(json.dumps({page: [{"line": line, "finding": what} for line, what in findings]
                          for page, findings in report.items()}, indent=1))
    else:
        for page, findings in report.items():
            print(f"{Path(page).name}: {len(findings)} departure(s)")
            for line, what in findings:
                print(f"  {line:4d}  {what}")
        total = sum(len(f) for f in report.values())
        checked = len(pages_of(targets))
        print(f"{total} departure(s) on {len(report)} of {checked} page(s)")
    return 1 if report else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
