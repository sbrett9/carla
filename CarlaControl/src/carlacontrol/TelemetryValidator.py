"""Checks what the SUMO bridge writes, and the legacy files beside a bridge run, against their schemas.

`carla-validate` checks a capture folder; a run of `carla-cot-telemetry` writes files of its own -- the
XML of every event, the CSV of every update and its summary, the described supervision gaps -- and a
legacy scenario's labels file sits beside them. This class finds them by what they are in whatever
folder it is given, and checks each against the schema of its kind (`TelemetrySchemas` and the
hand-written XSDs). Like `RecordValidator`, it is called from the capture validator one file at a
time, each failure handed back as where in the file and what is wrong.

| File | Kind | Schema |
|---|---|---|
| `*.xml` whose root is `<events source="sumo">` | SUMO bridge event files | `sumo_cot_events.xsd` |
| `<name>.csv` with a `<name>.summary.json` naming it, or with the bridge's header | SUMO bridge tables | `sumo_cot_telemetry.tableschema.json` |
| the `<name>.summary.json` beside a bridge table | SUMO bridge table summaries | `sumo_cot_telemetry_summary.schema.json` |
| `*.supervision.json` declaring no `supervision_plan_version` | supervision gap files | `supervision_gaps.schema.json` |
| `*.labels.json` | legacy labels files | `legacy_labels.schema.json` |

A truth sidecar's root is `<events>` too; the bridge's says `source="sumo"`, and a sidecar's never does.
A `.supervision.json` that declares `supervision_plan_version` is a compiled supervision plan, which
`RecordValidator` checks.

Every file is held to the format version rule first (`FormatVersion`): the event file's
`format_version`, and the table's, which its summary declares, newer than the schema describes is
refused as written by a newer release and checked no further; one that declares none is version 1. A
table with no summary beside it was written before summaries were, and is noted as version 1. An event
file whose run was cut off before its closing `</events>` is noted, and its events are not checked.
"""
from __future__ import annotations

import csv
import json
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path

from lxml import etree

from carlacontrol.FormatVersion import FormatVersion, FormatVersionError
from carlacontrol.SchemaPublication import SchemaPublication
from carlacontrol.SumoCotBridge import CSV_SUMMARY_SUFFIX
from carlacontrol.TableSchemaCheck import TableSchemaCheck
from carlacontrol.TelemetrySchemas import (
    CSV_SUMMARY_SCHEMA,
    CSV_TABLE_SCHEMA,
    LEGACY_LABELS_SCHEMA,
    SUPERVISION_GAPS_SCHEMA,
)

# The kinds, in the order a report lists them.
EVENT_FILES = "SUMO bridge event files"
TABLES = "SUMO bridge tables"
TABLE_SUMMARIES = "SUMO bridge table summaries"
GAP_FILES = "supervision gap files"
LABELS_FILES = "legacy labels files"
KINDS = (EVENT_FILES, TABLES, TABLE_SUMMARIES, GAP_FILES, LABELS_FILES)

EVENTS_SCHEMA = "sumo_cot_events.xsd"
BRIDGE_SOURCE = "sumo"
# A capture's world truth track is a CSV too, and the capture validator's.
TRACK_FILE = "world_truth_track.csv"
GAPS_SUFFIX = ".supervision.json"
LABELS_SUFFIX = ".labels.json"
PLAN_VERSION_KEY = "supervision_plan_version"
# How many of one file's departures are reported.
SHOWN_PER_FILE = 50

Fail = Callable[[str, str], None]
Note = Callable[[str], None]


@dataclass(frozen=True)
class TelemetryFile:
    """One file this class checks: its path and its kind."""

    path: Path
    kind: str


class TelemetryValidator:
    """Finds the SUMO bridge's files and the legacy ones under a folder and checks each."""

    def __init__(self, schema_folder: str | Path) -> None:
        self.folder = Path(schema_folder)
        events = etree.parse(str(self.folder / EVENTS_SCHEMA))
        self.events = etree.XMLSchema(events)
        self.events_version = int(events.getroot().get("version"))
        self.table_schema = self._json(CSV_TABLE_SCHEMA)
        self.table = TableSchemaCheck(self.table_schema)
        self.columns = [field["name"] for field in self.table_schema["fields"]]
        self.summary = self._json(CSV_SUMMARY_SCHEMA)
        self.table_version = self.summary["properties"]["format_version"]["const"]
        self.gaps = self._json(SUPERVISION_GAPS_SCHEMA)
        self.labels = self._json(LEGACY_LABELS_SCHEMA)

    def _json(self, name: str) -> dict:
        return json.loads((self.folder / name).read_text(encoding="utf-8"))

    # -- finding the files ------------------------------------------------------------------------

    def files(self, root: str | Path) -> list[TelemetryFile]:
        """Every file under `root` this class checks, in path order, a table's summary after it."""
        root = Path(root)
        found: list[TelemetryFile] = []
        for path in sorted(p for p in root.rglob("*") if p.is_file()):
            name = path.name
            if path.suffix == ".xml" and self.is_event_file(path):
                found.append(TelemetryFile(path, EVENT_FILES))
            elif path.suffix == ".csv" and name != TRACK_FILE and self._is_table(path):
                found.append(TelemetryFile(path, TABLES))
                if self.summary_of(path).is_file():
                    found.append(TelemetryFile(self.summary_of(path), TABLE_SUMMARIES))
            elif name.endswith(GAPS_SUFFIX) and self._is_gap_file(path):
                found.append(TelemetryFile(path, GAP_FILES))
            elif name.endswith(LABELS_SUFFIX):
                found.append(TelemetryFile(path, LABELS_FILES))
        return found

    @staticmethod
    def root_element(path: Path) -> etree._Element | None:
        """The file's root element as it opens, read even where the document is cut off later; None
        where the file is not XML."""
        try:
            with open(path, "rb") as handle:
                for _, element in etree.iterparse(handle, events=("start",)):
                    return element
        except (etree.XMLSyntaxError, OSError):
            return None
        return None

    @classmethod
    def is_event_file(cls, path: Path) -> bool:
        """Whether the XML file is the SUMO bridge's event file: its root `<events source="sumo">`."""
        root = cls.root_element(path)
        return root is not None and root.tag == "events" and root.get("source") == BRIDGE_SOURCE

    @staticmethod
    def summary_of(table: Path) -> Path:
        """Where a bridge table's summary is: its name with `.summary.json` for its extension."""
        return table.with_suffix(CSV_SUMMARY_SUFFIX)

    def _is_table(self, path: Path) -> bool:
        summary = self.summary_of(path)
        if summary.is_file():
            try:
                document = json.loads(summary.read_text(encoding="utf-8"))
            except (OSError, UnicodeDecodeError, json.JSONDecodeError):
                document = None
            if isinstance(document, dict) and document.get("csv") == path.name:
                return True
        try:
            with open(path, encoding="utf-8", newline="") as handle:
                header = next(csv.reader(handle), None)
        except (OSError, UnicodeDecodeError, csv.Error):
            return False
        return header == self.columns

    @staticmethod
    def _is_gap_file(path: Path) -> bool:
        try:
            # Read as RecordValidator reads it, so the two never both take one file.
            document = json.loads(path.read_text(encoding="utf-8-sig"))
        except (OSError, UnicodeDecodeError, json.JSONDecodeError):
            return True
        return not (isinstance(document, dict) and PLAN_VERSION_KEY in document)

    # -- checking one -----------------------------------------------------------------------------

    def check(self, file: TelemetryFile, fail: Fail, note: Note) -> None:
        """Check one file, calling `fail(where, message)` for every way it breaks its schema and
        `note(message)` for what is worth saying and is not a failure."""
        if file.kind == EVENT_FILES:
            self._event_file(file.path, fail, note)
        elif file.kind == TABLES:
            self._table(file.path, fail, note)
        elif file.kind == TABLE_SUMMARIES:
            self._summary(file.path, fail)
        elif file.kind == GAP_FILES:
            self._against(file.path, self.gaps, fail)
        else:
            self._against(file.path, self.labels, fail)

    def _event_file(self, path: Path, fail: Fail, note: Note) -> None:
        root = self.root_element(path)
        try:
            FormatVersion.check(path.name, "format_version", root.get("format_version"),
                                self.events_version)
        except FormatVersionError as refused:
            fail("", str(refused))
            return
        try:
            document = etree.parse(str(path))
        except etree.XMLSyntaxError as broken:
            note(f"{path}: is cut off before its closing </events>, so its run was interrupted, and "
                 f"its events are not checked ({broken})")
            return
        if not self.events.validate(document):
            for error in list(self.events.error_log)[:SHOWN_PER_FILE]:
                fail(f"line {error.line}", error.message)

    def _table(self, path: Path, fail: Fail, note: Note) -> None:
        summary = self.summary_of(path)
        declared = None
        if summary.is_file():
            try:
                document = json.loads(summary.read_text(encoding="utf-8"))
            except (OSError, UnicodeDecodeError, json.JSONDecodeError):
                document = None
            if isinstance(document, dict):
                declared = document.get("format_version")
        else:
            note(f"{path}: has no {summary.name} beside it, so it was written before the summary "
                 "was, and is read as format version 1")
        try:
            FormatVersion.check(path.name, "format_version", declared, self.table_version)
        except FormatVersionError as refused:
            fail("", f"{refused} (its summary declares the version)")
            return
        for problem in self.table.problems(path, limit=SHOWN_PER_FILE):
            fail(f"line {problem.line}" + (f", {problem.column}" if problem.column else ""),
                 problem.message)

    def _summary(self, path: Path, fail: Fail) -> None:
        document = self._read(path, fail)
        if document is None:
            return
        if isinstance(document, dict):
            try:
                FormatVersion.check(path.name, "format_version", document.get("format_version"),
                                    self.table_version)
            except FormatVersionError as refused:
                fail("", str(refused))
                return
        self._problems(document, self.summary, fail)

    def _against(self, path: Path, schema: dict, fail: Fail) -> None:
        document = self._read(path, fail)
        if document is not None:
            self._problems(document, schema, fail)

    @staticmethod
    def _read(path: Path, fail: Fail) -> object | None:
        try:
            return json.loads(path.read_text(encoding="utf-8-sig"))
        except (OSError, UnicodeDecodeError, json.JSONDecodeError) as broken:
            fail("", f"is not JSON: {broken}")
            return None

    @staticmethod
    def _problems(document: object, schema: dict, fail: Fail) -> None:
        for problem in SchemaPublication.problems(document, schema)[:SHOWN_PER_FILE]:
            at, _, message = problem.partition(": ")
            fail(at if at != "$" else "", message)
