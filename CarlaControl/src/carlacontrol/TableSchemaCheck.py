"""Checks a CSV file against a Frictionless Table Schema.

The subset of Table Schema the published capture schemas use: the fields in order, `missingValues`,
`primaryKey`, the types `string`, `number`, `integer`, `boolean` (with `trueValues` and `falseValues`)
and `datetime` (with a `strptime` format), and the constraints `required`, `enum`, `pattern`,
`minimum` and `maximum`. A schema that uses anything else is refused, rather than passed by.

A cell equal to one of the missing values is missing: it breaks `required`, and nothing else is
checked of it. A pattern must match the whole cell.
"""
from __future__ import annotations

import csv
import re
from collections.abc import Callable, Iterator
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path

_NUMBER = re.compile(r"[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?|NaN|INF|-INF")
_INTEGER = re.compile(r"[+-]?\d+")
_TYPES = {"string", "number", "integer", "boolean", "datetime"}
_CONSTRAINTS = {"required", "enum", "pattern", "minimum", "maximum"}
_FIELD_KEYS = {"name", "type", "format", "title", "description", "constraints", "trueValues",
               "falseValues", "x-unit"}


class TableSchemaUnsupportedError(ValueError):
    """A Table Schema uses something this check does not implement."""


@dataclass(frozen=True)
class TableProblem:
    """One way a CSV breaks its Table Schema: the line it is on (1 is the header) and what."""

    line: int
    column: str | None
    message: str

    def __str__(self) -> str:
        where = f"line {self.line}" + (f", {self.column}" if self.column else "")
        return f"{where}: {self.message}"


class TableSchemaCheck:
    """Checks the rows of a CSV file against one Table Schema."""

    def __init__(self, schema: dict) -> None:
        self.schema = schema
        self.fields: list[dict] = list(schema["fields"])
        self.names = [field["name"] for field in self.fields]
        self.missing = set(schema.get("missingValues", [""]))
        self.primary_key = schema.get("primaryKey", [])
        if isinstance(self.primary_key, str):
            self.primary_key = [self.primary_key]
        self._checks = [self._compile(field) for field in self.fields]
        self._required = [bool(field.get("constraints", {}).get("required")) for field in self.fields]

    def problems(self, path: str | Path, limit: int | None = None) -> Iterator[TableProblem]:
        """Every way the file at `path` breaks the schema, in file order, up to `limit` of them.

        A last line with no line break -- a track cut off while it was being written -- is left
        off, as every reader of a track leaves it off.
        """
        found = 0
        with open(path, encoding="utf-8", newline="") as file:
            text_lines = _complete_lines(file)
            reader = csv.reader(text_lines)
            header = next(reader, None)
            if header != self.names:
                yield TableProblem(1, None, f"the header is {header}, and the schema's columns are "
                                            f"{self.names}")
                return
            keys: set[tuple[str, ...]] = set()
            key_columns = [self.names.index(name) for name in self.primary_key]
            for line, row in enumerate(reader, start=2):
                for problem in self._row(line, row, keys, key_columns):
                    yield problem
                    found += 1
                    if limit is not None and found >= limit:
                        return

    def rows(self, path: str | Path) -> int:
        """How many complete rows the file holds below its header."""
        with open(path, encoding="utf-8", newline="") as file:
            return max(0, sum(1 for _ in csv.reader(_complete_lines(file))) - 1)

    # -- one row ----------------------------------------------------------------------------------

    def _row(self, line: int, row: list[str], keys: set, key_columns: list[int]) -> Iterator[TableProblem]:
        if len(row) != len(self.names):
            yield TableProblem(line, None, f"{len(row)} cells, and the schema has {len(self.names)} columns")
            return
        for index, cell in enumerate(row):
            if cell in self.missing:
                if self._required[index]:
                    yield TableProblem(line, self.names[index], "is empty, and the column is required")
                continue
            problem = self._checks[index](cell)
            if problem is not None:
                yield TableProblem(line, self.names[index], f"{cell!r} {problem}")
        if key_columns:
            key = tuple(row[index] for index in key_columns)
            if key in keys:
                yield TableProblem(line, None, f"repeats the primary key {dict(zip(self.primary_key, key))}")
            keys.add(key)

    # -- one field --------------------------------------------------------------------------------

    @staticmethod
    def _compile(field: dict) -> Callable[[str], str | None]:
        """The check of one non-missing cell of `field`: None where it passes, else what is wrong."""
        unknown = set(field) - _FIELD_KEYS
        kind = field.get("type", "string")
        constraints = field.get("constraints", {})
        if unknown or kind not in _TYPES or set(constraints) - _CONSTRAINTS:
            raise TableSchemaUnsupportedError(
                f"field {field.get('name')!r} uses {sorted(unknown | (set(constraints) - _CONSTRAINTS))} "
                f"or type {kind!r}, which this check does not implement")

        parse: Callable[[str], object]
        if kind == "number":
            def parse(cell: str) -> object:
                if not _NUMBER.fullmatch(cell):
                    raise ValueError("is not a number")
                return float(cell)
        elif kind == "integer":
            def parse(cell: str) -> object:
                if not _INTEGER.fullmatch(cell):
                    raise ValueError("is not an integer")
                return int(cell)
        elif kind == "boolean":
            true_values = set(field.get("trueValues", ["true", "True", "TRUE", "1"]))
            false_values = set(field.get("falseValues", ["false", "False", "FALSE", "0"]))

            def parse(cell: str) -> object:
                if cell in true_values:
                    return True
                if cell in false_values:
                    return False
                raise ValueError(f"is not one of {sorted(true_values | false_values)}")
        elif kind == "datetime":
            pattern = field.get("format", "default")
            if pattern in ("default", "any"):
                raise TableSchemaUnsupportedError(f"field {field['name']!r} gives no strptime format")

            def parse(cell: str) -> object:
                try:
                    return datetime.strptime(cell, pattern)
                except ValueError:
                    raise ValueError(f"is not a datetime of the form {pattern}") from None
        else:
            def parse(cell: str) -> object:
                return cell

        words = set(constraints["enum"]) if "enum" in constraints else None
        matcher = re.compile(constraints["pattern"]) if "pattern" in constraints else None
        minimum = constraints.get("minimum")
        maximum = constraints.get("maximum")

        def check(cell: str) -> str | None:
            try:
                value = parse(cell)
            except ValueError as wrong:
                return str(wrong)
            if words is not None and cell not in {str(word) for word in words} and value not in words:
                return f"is not one of {sorted(map(str, words))}"
            if matcher is not None and not matcher.fullmatch(cell):
                return f"does not match {matcher.pattern}"
            if minimum is not None and not value >= minimum:
                return f"is below the minimum {minimum}"
            if maximum is not None and not value <= maximum:
                return f"is above the maximum {maximum}"
            return None

        return check


def _complete_lines(file) -> Iterator[str]:
    """The file's lines that end in a line break: a last line without one was being written."""
    for line in file:
        if line.endswith("\n"):
            yield line
