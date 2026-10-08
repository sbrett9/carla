"""The published schemas of what a capture writes, loaded from one folder.

The folder is `CarlaControl/schemas/` in a checkout and the copy installed with the package otherwise
(`carlacontrol.ToolLayout`). The CarlaNet writers generate every one of these files, and their tests
hold the published files equal to what the writers generate:

* `truth_sidecar.xsd`, the truth sidecar beside every still (XSD 1.0);
* `png_chunk_<name>.schema.json`, one per `carla:<name>` PNG text chunk (JSON Schema 2020-12);
* `run_manifest.schema.json`, every kind of row of `truth/manifest.jsonl`;
* `world_truth_track.tableschema.json`, the columns of `truth/world_truth_track.csv` (Table Schema);
* `world_truth_track_summary.schema.json`, the summary written beside the track.

Each names the format version it describes, which a validator holds every file's declared version to.
"""
from __future__ import annotations

import json
from pathlib import Path

import jsonschema
from lxml import etree

from carlacontrol.TableSchemaCheck import TableSchemaCheck

SIDECAR_SCHEMA = "truth_sidecar.xsd"
MANIFEST_SCHEMA = "run_manifest.schema.json"
TRACK_SCHEMA = "world_truth_track.tableschema.json"
SUMMARY_SCHEMA = "world_truth_track_summary.schema.json"
CHUNK_SCHEMA_PREFIX = "png_chunk_"
CHUNK_SCHEMA_SUFFIX = ".schema.json"
CHUNK_PREFIX = "carla:"
OPENED_ROW = "manifest_opened"


class CaptureSchemaSet:
    """The schemas of what a capture writes, each loaded and checked as a schema."""

    def __init__(self, folder: str | Path) -> None:
        self.folder = Path(folder)
        sidecar = etree.parse(str(self.folder / SIDECAR_SCHEMA))
        self.sidecar = etree.XMLSchema(sidecar)
        self.sidecar_version = int(sidecar.getroot().get("version"))
        self.manifest = self.json_validator(self.folder / MANIFEST_SCHEMA)
        self.manifest_version = \
            self.manifest.schema["$defs"][OPENED_ROW]["properties"]["manifest_version"]["const"]
        self.summary = self.json_validator(self.folder / SUMMARY_SCHEMA)
        self.summary_version = self.summary.schema["properties"]["world_truth_track_version"]["const"]
        self.track_schema = json.loads((self.folder / TRACK_SCHEMA).read_text(encoding="utf-8"))
        self.track = TableSchemaCheck(self.track_schema)
        self.chunks = {}
        for path in sorted(self.folder.glob(CHUNK_SCHEMA_PREFIX + "*" + CHUNK_SCHEMA_SUFFIX)):
            name = path.name[len(CHUNK_SCHEMA_PREFIX):-len(CHUNK_SCHEMA_SUFFIX)]
            self.chunks[CHUNK_PREFIX + name] = self.json_validator(path)

    @classmethod
    def files(cls) -> tuple[str, ...]:
        """The schema files a capture folder is checked against, the chunk schemas apart."""
        return SIDECAR_SCHEMA, MANIFEST_SCHEMA, TRACK_SCHEMA, SUMMARY_SCHEMA

    def chunk_version(self, keyword: str) -> int:
        """The format version the schema of chunk `keyword` describes."""
        return self.chunks[keyword].schema["properties"]["format_version"]["const"]

    @staticmethod
    def json_validator(path: str | Path):
        """A validator of the JSON Schema at `path`, the schema itself checked first."""
        schema = json.loads(Path(path).read_text(encoding="utf-8"))
        validator_class = jsonschema.validators.validator_for(schema)
        validator_class.check_schema(schema)
        return validator_class(schema)
