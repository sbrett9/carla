"""The run configuration's fields and checks are the ones plan 12 lists, with the defaults it gives.

Plan 12 §1.5 measured three defaults that had drifted between a parser and the code reading it. The
run configuration's field table is the single definition of its defaults, and §5.2 of the plan is where
an operator reads them, so the two are held equal here by reading the plan's tables themselves: every
field has a row, and every row whose default cell holds one value per field it names gives the
field table's value. §6.2.1's table of where each check is carried out is held equal to the check
catalogue the same way, and every check the catalogue numbers appears in §6.2.
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
    RunConfigurationCheckCatalogue,
)

PLAN_12 = (_REPO / "Docs" / "CAT_Research" / "Plans" / "SUMO_Behavioral_Capture"
           / "12_Operator_Control_Surface.md")
NO_DEFAULT_MARKS = ("**—**", "**cond.**")


class _Inventory:
    """Plan 12 §5.2's tables, as field -> default (None where the plan gives no single value)."""

    START = "### 5.2 The inventory"
    END = "### 5.3"

    @classmethod
    def read(cls) -> tuple[dict[str, object], set[str], set[str]]:
        text = PLAN_12.read_text(encoding="utf-8")
        section = text[text.index(cls.START):text.index(cls.END)]
        defaults: dict[str, object] = {}
        named: set[str] = set()
        no_default: set[str] = set()
        default_column = None
        for line in section.splitlines():
            if not line.startswith("|"):
                default_column = None
                continue
            cells = [cell.strip() for cell in line.strip().strip("|").split("|")]
            if cells[0] == "Toggle":
                default_column = cells.index("Default")
                continue
            if default_column is None or set(cells[0]) <= set("-: "):
                continue
            names = re.findall(r"`([^`]+)`", cls._unlinked(cells[0]))
            named.update(names)
            cell = cls._unlinked(cells[default_column])
            if cell.startswith(NO_DEFAULT_MARKS):
                no_default.update(names)
                continue
            values = [cls._value(token) for token in re.findall(r"`([^`]+)`", cell)]
            if len(values) == len(names):
                defaults.update(zip(names, values, strict=True))
        return defaults, named, no_default

    @staticmethod
    def _unlinked(cell: str) -> str:
        """The cell without its document links, whose text is a document number in backticks."""
        return re.sub(r"\[`[^`]*`\]\([^)]*\)", "", cell)

    @staticmethod
    def _value(token: str) -> object:
        try:
            return json.loads(token)
        except json.JSONDecodeError:
            return token


def test_every_field_has_a_row_in_the_inventory():
    _, named, _ = _Inventory.read()
    missing = [path for path in RunConfiguration.FIELDS if path not in named]
    missing += [name for name in RunConfiguration.channel_fields() if name not in named]
    assert missing == [], f"fields with no row in plan 12 §5.2: {missing}"


def test_every_default_is_the_plan_s():
    defaults, _, no_default = _Inventory.read()
    fields = {**RunConfiguration.FIELDS, **RunConfiguration.channel_fields()}
    for name, spec in fields.items():
        if name in no_default:
            assert not spec.has_default or spec.default is None, \
                f"{name}: the plan gives no default, the field table gives {spec.default!r}"
        elif name in defaults:
            assert spec.has_default, f"{name}: the plan gives {defaults[name]!r}, the table none"
            assert defaults[name] == spec.default, (name, defaults[name], spec.default)


def test_no_field_the_plan_marks_without_a_default_has_one():
    _, _, no_default = _Inventory.read()
    for path in no_default & set(RunConfiguration.FIELDS):
        spec = RunConfiguration.FIELDS[path]
        assert not spec.has_default, f"{path} has a tool default the plan does not give"


def _where_table() -> dict[int, tuple[str, str]]:
    text = PLAN_12.read_text(encoding="utf-8")
    section = text[text.index("#### 6.2.1"):text.index("### 6.3")]
    rows = {}
    for line in section.splitlines():
        match = re.match(r"^\| (\d+) \| ([^|]+) \| ([^|]+) \|", line)
        if match:
            rows[int(match.group(1))] = (match.group(2).strip(), match.group(3).strip())
    return rows


def test_the_plan_says_where_every_check_is_carried_out_as_the_catalogue_does():
    labels = {"run_capture": "`run_capture`", "session": "the session",
              "by_construction": "by construction", "not_built": "**not built**"}
    rows = _where_table()
    catalogue = {check.check_id: check for check in RunConfigurationCheckCatalogue.all()}
    assert set(rows) == set(catalogue)
    for check_id, (_phase, status) in rows.items():
        assert status == labels[catalogue[check_id].status], check_id


def test_every_catalogued_check_is_numbered_in_the_plan_s_check_tables():
    text = PLAN_12.read_text(encoding="utf-8")
    section = text[text.index("### 6.2 The checks"):text.index("#### 6.2.1")]
    numbered = {int(n) for n in re.findall(r"^\| (\d+) \|", section, flags=re.MULTILINE)}
    missing = sorted(c.check_id for c in RunConfigurationCheckCatalogue.all()
                     if c.check_id not in numbered)
    assert missing == []
