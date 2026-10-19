"""Validates every file a capture writes against its published schema.

A capture folder holds, per camera, a PNG and a truth sidecar for every still, and under `truth/` the
run manifest and the world truth track with its summary. Each kind of file has a published schema
(`carlacontrol.CaptureSchemaSet`):

* every `*.xml` whose root is `<events>` is a truth sidecar, checked against `truth_sidecar.xsd`, and
  so is any other `*.xml` beside a still of its name; the SUMO bridge's event file, whose `<events>`
  says `source="sumo"`, is `TelemetryValidator`'s, and any other XML file is not a capture's;
* every `*.png` carries text chunks, each `carla:<name>` chunk checked against
  `png_chunk_<name>.schema.json`; a still must carry `carla:capture`, and a `carla:` chunk with no
  schema is a failure;
* `manifest.jsonl` is checked row by row against `run_manifest.schema.json`, and as a file: its first
  row opens it, nothing follows its terminal row, and a manifest with no terminal row is noted as a run
  that was interrupted;
* `world_truth_track.csv` is checked against `world_truth_track.tableschema.json`, and the summary beside
  it against `world_truth_track_summary.schema.json`; once the run has ended, the summary must count the
  rows the track holds.

Every file is held to the format version rule first: a file that declares a version newer than its
schema describes is reported as written by a newer release and checked no further, and a file that
declares none is version 1. The pixels are not checked.

The files that live beside a capture rather than in it -- a run's records, a compiled scenario's files,
the inputs a user writes -- are found in the same folder by `RecordValidator`, and what the SUMO bridge
writes, with the legacy files beside it, by `TelemetryValidator`; each is checked against its own
schema and reported as its own kind.
"""
from __future__ import annotations

import json
from collections import Counter
from dataclasses import dataclass, field
from pathlib import Path

from lxml import etree

from carlacontrol.CaptureSchemaSet import CHUNK_PREFIX, OPENED_ROW, CaptureSchemaSet
from carlacontrol.FormatVersion import LEGACY, FormatVersion, FormatVersionError
from carlacontrol.PngTextChunks import PngTextChunks, PngTextChunksError
from carlacontrol.RecordValidator import KINDS as RECORD_KINDS
from carlacontrol.RecordValidator import RecordValidator
from carlacontrol.TelemetryValidator import KINDS as TELEMETRY_KINDS
from carlacontrol.TelemetryValidator import TelemetryValidator

CAPTURE_CHUNK = "carla:capture"
MANIFEST_FILE = "manifest.jsonl"
TRACK_FILE = "world_truth_track.csv"
CLOSED_ROW = "manifest_closed"

# The kinds of file, in the order a report lists them.
SIDECARS = "truth sidecars"
STILLS = "PNG text chunks"
MANIFESTS = "run manifests"
TRACKS = "world truth tracks"
SUMMARIES = "world truth track summaries"
KINDS = (SIDECARS, STILLS, MANIFESTS, TRACKS, SUMMARIES, *RECORD_KINDS, *TELEMETRY_KINDS)


@dataclass(frozen=True)
class Failure:
    """One way one file breaks its schema."""

    kind: str
    path: Path
    where: str
    message: str

    def describe(self, root: Path) -> str:
        """The failure in one line, the file named from `root`."""
        try:
            shown = self.path.relative_to(root)
        except ValueError:
            shown = self.path
        return f"{shown}{': ' + self.where if self.where else ''}: {self.message}"


@dataclass
class CaptureValidation:
    """What a validation found: how many files of each kind were checked and failed, the failures, and
    notes that are not failures."""

    root: Path
    checked: Counter = field(default_factory=Counter)
    failed_files: Counter = field(default_factory=Counter)
    failures: list[Failure] = field(default_factory=list)
    notes: list[str] = field(default_factory=list)

    @property
    def ok(self) -> bool:
        """Whether every file checked keeps its schema."""
        return not self.failures

    def fail(self, kind: str, path: Path, where: str, message: str) -> None:
        self.failures.append(Failure(kind, path, where, message))


class CaptureValidator:
    """Validates the files of one capture folder against the published schemas."""

    def __init__(self, schemas: CaptureSchemaSet, failures_per_file: int = 50) -> None:
        """
        Args:
            schemas: the published schemas.
            failures_per_file: how many failures one file reports at most; a file that breaks its
                schema on every row says so in its first few.
        """
        self.schemas = schemas
        self.failures_per_file = failures_per_file
        self.records = RecordValidator(schemas.folder)
        self.telemetry = TelemetryValidator(schemas.folder)

    def validate(self, folder: str | Path) -> CaptureValidation:
        """Every file of the capture under `folder`, checked against its schema."""
        root = Path(folder)
        result = CaptureValidation(root)
        for path in sorted(root.rglob("*.xml")):
            if self._is_sidecar(path):
                self._counted(SIDECARS, result, lambda: self._sidecar(path, result))
        for path in sorted(root.rglob("*.png")):
            self._counted(STILLS, result, lambda: self._still(path, result))
        for path in sorted(root.rglob(MANIFEST_FILE)):
            self._counted(MANIFESTS, result, lambda: self._manifest(path, result))
        for path in sorted(root.rglob(TRACK_FILE)):
            self._track(path, result)
        for record in self.records.files(root):
            self._counted(record.kind, result, lambda: self.records.check(
                record, lambda where, message: result.fail(record.kind, record.path, where, message)))
        for file in self.telemetry.files(root):
            self._counted(file.kind, result, lambda: self.telemetry.check(
                file, lambda where, message: result.fail(file.kind, file.path, where, message),
                result.notes.append))
        return result

    @staticmethod
    def _is_sidecar(path: Path) -> bool:
        """Whether an XML file is a still's truth sidecar: its root is `<events>` and not the SUMO
        bridge's, or, whatever it holds, a still of its name is beside it."""
        if path.with_suffix(".png").is_file():
            return True
        root = TelemetryValidator.root_element(path)
        return root is not None and root.tag == "events" and not TelemetryValidator.is_event_file(path)

    # -- truth sidecars ---------------------------------------------------------------------------

    def _sidecar(self, path: Path, result: CaptureValidation) -> None:
        try:
            document = etree.parse(str(path))
        except etree.XMLSyntaxError as broken:
            result.fail(SIDECARS, path, "", f"is not well-formed XML: {broken}")
            return
        root = document.getroot()
        if root.tag != "events":
            result.fail(SIDECARS, path, "", f"is not a truth sidecar: its root is <{root.tag}>, not <events>")
            return
        if not self._version(SIDECARS, path, "format_version", root.get("format_version"),
                             self.schemas.sidecar_version, result):
            return
        if not self.schemas.sidecar.validate(document):
            for error in list(self.schemas.sidecar.error_log)[:self.failures_per_file]:
                result.fail(SIDECARS, path, f"line {error.line}", error.message)

    # -- PNG text chunks --------------------------------------------------------------------------

    def _still(self, path: Path, result: CaptureValidation) -> None:
        try:
            chunks = PngTextChunks.read(path)
        except (OSError, PngTextChunksError) as broken:
            result.fail(STILLS, path, "", str(broken))
            return
        keywords = [keyword for keyword, _ in chunks]
        if CAPTURE_CHUNK not in keywords:
            result.fail(STILLS, path, "", f"carries no {CAPTURE_CHUNK} chunk, which every still carries")
        for keyword, text in chunks:
            if not keyword.startswith(CHUNK_PREFIX):
                continue
            if keywords.count(keyword) > 1:
                result.fail(STILLS, path, keyword, "appears more than once")
                continue
            validator = self.schemas.chunks.get(keyword)
            if validator is None:
                result.fail(STILLS, path, keyword, "is a chunk no published schema describes")
                continue
            try:
                value = json.loads(text)
            except json.JSONDecodeError as broken:
                result.fail(STILLS, path, keyword, f"is not JSON: {broken}")
                continue
            declared = value.get("format_version") if isinstance(value, dict) else None
            if self._version(STILLS, path, f"{keyword} format_version", declared,
                             self.schemas.chunk_version(keyword), result):
                self._json(STILLS, path, keyword, validator, value, result)

    # -- the run manifest -------------------------------------------------------------------------

    def _manifest(self, path: Path, result: CaptureValidation) -> None:
        lines = path.read_text(encoding="utf-8").split("\n")
        # A reader keeps every line that ends in a line break; the rest was being written.
        if lines.pop():
            result.notes.append(f"{path}: its last line has no line break and is left off, as a manifest "
                                "cut off while it was being written is read")
        rows: list[dict] = []
        for number, line in enumerate(lines, start=1):
            try:
                row = json.loads(line)
            except json.JSONDecodeError as broken:
                result.fail(MANIFESTS, path, f"line {number}", f"is not JSON: {broken}")
                continue
            if not isinstance(row, dict):
                result.fail(MANIFESTS, path, f"line {number}", "is not a JSON object")
                continue
            if number == 1:
                if row.get("row") != OPENED_ROW:
                    result.fail(MANIFESTS, path, "line 1",
                                f"is a {row.get('row')!r} row, and a manifest opens with {OPENED_ROW}")
                elif not self._version(MANIFESTS, path, "manifest_version", row.get("manifest_version"),
                                       self.schemas.manifest_version, result):
                    return
            rows.append(row)
            self._json(MANIFESTS, path, f"line {number} ({row.get('row')})", self.schemas.manifest, row,
                       result)
        if not lines:
            result.fail(MANIFESTS, path, "", "holds no row")
            return
        kinds = [row.get("row") for row in rows]
        if kinds.count(OPENED_ROW) > 1:
            result.fail(MANIFESTS, path, "", f"holds {kinds.count(OPENED_ROW)} {OPENED_ROW} rows, not one")
        if CLOSED_ROW in kinds:
            if kinds.count(CLOSED_ROW) > 1 or kinds[-1] != CLOSED_ROW:
                result.fail(MANIFESTS, path, "", f"has rows after its {CLOSED_ROW} row, which is the last")
        elif rows:
            result.notes.append(f"{path}: has no {CLOSED_ROW} row, so its run was interrupted")

    # -- the world truth track and its summary ----------------------------------------------------

    def _track(self, path: Path, result: CaptureValidation) -> None:
        summary_path = path.with_suffix(".summary.json")
        summary: object = None
        rows_checked = True
        result.checked[SUMMARIES] += 1
        before = len(result.failures)
        if not summary_path.is_file():
            result.fail(SUMMARIES, summary_path, "", "is missing: a world truth track is written with its summary")
        else:
            try:
                summary = json.loads(summary_path.read_text(encoding="utf-8"))
            except json.JSONDecodeError as broken:
                result.fail(SUMMARIES, summary_path, "", f"is not JSON: {broken}")
            if isinstance(summary, dict):
                # The track has no version of its own: it is the summary's, and a summary without one is
                # version 1, an older shape the published schema does not describe.
                declared = summary.get("world_truth_track_version", LEGACY)
                if self._version(SUMMARIES, summary_path, "world_truth_track_version", declared,
                                 self.schemas.summary_version, result, legacy_described=False):
                    self._json(SUMMARIES, summary_path, "", self.schemas.summary, summary, result)
                else:
                    rows_checked = False
        if len(result.failures) > before:
            result.failed_files[SUMMARIES] += 1

        result.checked[TRACKS] += 1
        before = len(result.failures)
        if rows_checked:
            for problem in self.schemas.track.problems(path, limit=self.failures_per_file):
                where = f"line {problem.line}" + (f", {problem.column}" if problem.column else "")
                result.fail(TRACKS, path, where, problem.message)
            if isinstance(summary, dict) and summary.get("ended") is not None:
                held = self.schemas.track.rows(path)
                if summary.get("rows") != held:
                    result.fail(TRACKS, path, "", f"holds {held} rows, and its summary, written when the run "
                                                  f"ended, says {summary.get('rows')}")
        if len(result.failures) > before:
            result.failed_files[TRACKS] += 1

    # -- shared -----------------------------------------------------------------------------------

    @staticmethod
    def _counted(kind: str, result: CaptureValidation, check) -> None:
        """Run one file's check, counting the file, and counting it as failed where it added a failure."""
        result.checked[kind] += 1
        before = len(result.failures)
        check()
        if len(result.failures) > before:
            result.failed_files[kind] += 1

    def _json(self, kind: str, path: Path, where: str, validator, value: object,
              result: CaptureValidation) -> None:
        errors = sorted(validator.iter_errors(value), key=lambda error: list(map(str, error.absolute_path)))
        for error in errors[:self.failures_per_file]:
            result.fail(kind, path, f"{where} {error.json_path}".strip(), error.message)

    @staticmethod
    def _version(kind: str, path: Path, field_name: str, declared: object, described: int,
                 result: CaptureValidation, legacy_described: bool = True) -> bool:
        """Whether the file's format version is one its schema describes; a failure where it is not."""
        try:
            version = FormatVersion.check(path.name, field_name, declared, described)
        except FormatVersionError as refused:
            result.fail(kind, path, "", str(refused))
            return False
        if version != described and not (legacy_described and version == LEGACY):
            result.fail(kind, path, "", f"declares {field_name} {version}, an older shape the published schema "
                                        f"does not describe: it describes version {described}")
            return False
        return True
