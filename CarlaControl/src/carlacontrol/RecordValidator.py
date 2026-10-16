"""Checks the run records, the compiler's outputs and the files a user writes against their schemas.

`carla-validate` checks a capture folder; these files live beside it rather than in it -- a run's
records beside its result, a compiled scenario in its own folder, a user's inputs anywhere -- so this
class finds them by name in whatever folder it is given, and checks each against the schema of its
kind. It is written to be called from the capture validator, one file at a time, with each failure
handed back as the file's kind, where in it, and what is wrong.

| File | Kind | Schema |
|---|---|---|
| `*.result.json` | run results | `run_result.schema.json` |
| `*.resolution.json` with a `session_id` | run resolution reports | `run_resolution.schema.json` |
| `*.lock.json` with a `session_id` | run locks | `run_lock.schema.json` |
| `*.effective.json`, `*.run.json` | run configurations | `run_configuration.schema.json` |
| any `*.json` declaring `site_profile_version` | site profiles | `site_profile.schema.json` |
| `*.scenario.json` | scenario specifications | the compiler's own, and its `epoch` against `epoch.schema.json` |
| `*.sweep.json` | sweeps | the compiler's own |
| `*.lock.json` without a `session_id` | scenario locks | `scenario_lock.schema.json` |
| `*.resolution.json` without a `session_id` | scenario resolution reports | `scenario_resolution.schema.json` |
| `*.supervision.json` declaring `supervision_plan_version` | supervision plans | `supervision_plan.schema.json` |
| `*.sweep-index.json` | sweep indexes | `sweep_index.schema.json` |
| `checks.json` | check lists | `scenario_checks.schema.json` |
| `*.epoch.json` | epochs | `epoch.schema.json` |
| `*.display.json` | display conventions | `display_convention.schema.json` |
| `*.aoi.geojson` | areas of interest | `area_of_interest.schema.json` |

Two pairs of files share a name and not a shape, and are told apart by what they hold: a run's
`run.lock.json` and `run.resolution.json` carry the run's `session_id`, a compiled scenario's never do.
A `.supervision.json` that declares no `supervision_plan_version` is a legacy file of another kind, and
is not checked here.

Every file is held to the format version rule first (`FormatVersion`): one that declares a version
newer than its schema describes was written by a newer release and is checked no further. The schemas
are read from the folder the published ones are kept in, except the specification's and the sweep's,
which ship with the authoring skill and are the compiler's own (`ScenarioSchema`, `ScenarioSweep`).
Every file is checked with the validator the readers use (`ScenarioSchema.validate_against`), so a
file this class passes is one whose shape its reader accepts.
"""
from __future__ import annotations

import json
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path

from carlacontrol.FormatVersion import FormatVersion, FormatVersionError
from carlacontrol.ScenarioSchema import SCHEMA as SPECIFICATION_SCHEMA
from carlacontrol.ScenarioSchema import ScenarioSchema
from carlacontrol.ScenarioSweep import SWEEP_SCHEMA

# The kinds, in the order a report lists them.
RUN_RESULTS = "run results"
RUN_RESOLUTIONS = "run resolution reports"
RUN_LOCKS = "run locks"
RUN_CONFIGURATIONS = "run configurations"
SITE_PROFILES = "site profiles"
SPECIFICATIONS = "scenario specifications"
SWEEPS = "sweeps"
SCENARIO_LOCKS = "scenario locks"
SCENARIO_RESOLUTIONS = "scenario resolution reports"
SUPERVISION_PLANS = "supervision plans"
SWEEP_INDEXES = "sweep indexes"
CHECK_LISTS = "check lists"
EPOCHS = "epochs"
DISPLAY_CONVENTIONS = "display conventions"
AREAS = "areas of interest"
KINDS = (RUN_RESULTS, RUN_RESOLUTIONS, RUN_LOCKS, RUN_CONFIGURATIONS, SITE_PROFILES, SPECIFICATIONS,
         SWEEPS, SCENARIO_LOCKS, SCENARIO_RESOLUTIONS, SUPERVISION_PLANS, SWEEP_INDEXES, CHECK_LISTS,
         EPOCHS, DISPLAY_CONVENTIONS, AREAS)

# Each kind's published schema, by file name in the schema folder.
SCHEMA_FILES = {
    RUN_RESULTS: "run_result.schema.json",
    RUN_RESOLUTIONS: "run_resolution.schema.json",
    RUN_LOCKS: "run_lock.schema.json",
    RUN_CONFIGURATIONS: "run_configuration.schema.json",
    SITE_PROFILES: "site_profile.schema.json",
    SCENARIO_LOCKS: "scenario_lock.schema.json",
    SCENARIO_RESOLUTIONS: "scenario_resolution.schema.json",
    SUPERVISION_PLANS: "supervision_plan.schema.json",
    SWEEP_INDEXES: "sweep_index.schema.json",
    CHECK_LISTS: "scenario_checks.schema.json",
    EPOCHS: "epoch.schema.json",
    DISPLAY_CONVENTIONS: "display_convention.schema.json",
    AREAS: "area_of_interest.schema.json",
}

# Kinds found by the end of their file name alone, longest ending first.
_BY_NAME = ((".sweep-index.json", SWEEP_INDEXES), (".result.json", RUN_RESULTS),
            (".effective.json", RUN_CONFIGURATIONS), (".run.json", RUN_CONFIGURATIONS),
            (".scenario.json", SPECIFICATIONS), (".sweep.json", SWEEPS),
            (".epoch.json", EPOCHS), (".display.json", DISPLAY_CONVENTIONS),
            (".aoi.geojson", AREAS))
CHECK_LIST_NAME = "checks.json"
# The key a run's lock and resolution report carry and a compiled scenario's never do.
RUN_KEY = "session_id"
PLAN_VERSION_KEY = "supervision_plan_version"
SITE_PROFILE_VERSION_KEY = "site_profile_version"
# Version fields that record another file's format beside a file's own.
_OTHER_FORMATS = ("spec_version", "schema_version")

Fail = Callable[[str, str], None]


@dataclass(frozen=True)
class RecordFile:
    """One file this class checks: its path, its kind, and the document it holds."""

    path: Path
    kind: str
    document: object


class RecordValidator:
    """Finds the record and input files under a folder and checks each against its schema."""

    def __init__(self, schema_folder: str | Path) -> None:
        self.folder = Path(schema_folder)
        self.schemas: dict[str, dict] = {
            kind: json.loads((self.folder / name).read_text(encoding="utf-8"))
            for kind, name in SCHEMA_FILES.items()}
        self.schemas[SPECIFICATIONS] = SPECIFICATION_SCHEMA
        self.schemas[SWEEPS] = SWEEP_SCHEMA

    # -- finding the files ------------------------------------------------------------------------

    @classmethod
    def files(cls, root: str | Path) -> list[RecordFile]:
        """Every file under `root` (or `root` itself, a file) this class checks, in path order."""
        root = Path(root)
        candidates = [root] if root.is_file() else sorted(
            path for path in root.rglob("*") if path.is_file()
            and (path.suffix in (".json", ".geojson")))
        found = []
        for path in candidates:
            record = cls.classify(path)
            if record is not None:
                found.append(record)
        return found

    @staticmethod
    def classify(path: Path) -> RecordFile | None:
        """The file's kind, or None for a file this class does not check. A file that is not JSON
        is classified by its name alone, so the check can say so."""
        name = path.name
        named = next((kind for ending, kind in _BY_NAME if name.endswith(ending)), None)
        if name == CHECK_LIST_NAME:
            named = CHECK_LISTS
        twin = name.endswith(".lock.json") or name.endswith(".resolution.json") \
            or name.endswith(".supervision.json")
        if named is None and not twin and path.suffix != ".json":
            return None
        try:
            document: object = json.loads(path.read_text(encoding="utf-8-sig"))
        except (OSError, UnicodeDecodeError, json.JSONDecodeError) as broken:
            if named is None and not twin:
                return None
            document = broken
        is_object = isinstance(document, dict)
        if named is not None:
            return RecordFile(path, named, document)
        if name.endswith(".lock.json"):
            kind = RUN_LOCKS if is_object and RUN_KEY in document else SCENARIO_LOCKS
            return RecordFile(path, kind, document)
        if name.endswith(".resolution.json"):
            kind = RUN_RESOLUTIONS if is_object and RUN_KEY in document else SCENARIO_RESOLUTIONS
            return RecordFile(path, kind, document)
        if name.endswith(".supervision.json"):
            if is_object and PLAN_VERSION_KEY in document:
                return RecordFile(path, SUPERVISION_PLANS, document)
            return None
        if is_object and SITE_PROFILE_VERSION_KEY in document:
            return RecordFile(path, SITE_PROFILES, document)
        return None

    # -- checking one -----------------------------------------------------------------------------

    def check(self, record: RecordFile, fail: Fail) -> None:
        """Check one file, calling `fail(where, message)` for every way it breaks its schema."""
        if isinstance(record.document, Exception):
            fail("", f"is not JSON: {record.document}")
            return
        schema = self.schemas[record.kind]
        if not self._version(record, schema, fail):
            return
        self._against(record.document, schema, "", fail)
        if record.kind == SPECIFICATIONS and isinstance(record.document, dict) \
                and "epoch" in record.document:
            self._against(record.document["epoch"], self.schemas[EPOCHS], "epoch", fail)

    @staticmethod
    def version_field(schema: dict) -> str | None:
        """The field a file of this schema declares its format version in, if it declares one."""
        declared = [name for name, node in schema.get("properties", {}).items()
                    if name.endswith("_version") and isinstance(node, dict) and "const" in node]
        if len(declared) > 1:
            declared = [name for name in declared if name not in _OTHER_FORMATS]
        return declared[0] if declared else None

    @classmethod
    def _version(cls, record: RecordFile, schema: dict, fail: Fail) -> bool:
        """Whether the file is of a version its schema can describe: refused as newer otherwise."""
        field = cls.version_field(schema)
        if field is None or not isinstance(record.document, dict):
            return True
        try:
            FormatVersion.check(record.path.name, field, record.document.get(field),
                                schema["properties"][field]["const"])
        except FormatVersionError as refused:
            fail("", str(refused))
            return False
        return True

    @staticmethod
    def _against(document: object, schema: dict, where: str, fail: Fail) -> None:
        for problem in ScenarioSchema.validate_against(document, schema):
            at, _, message = problem.partition(": ")
            at = at.replace("$", where, 1) if where else at
            fail(at if at != "$" else "", message)
