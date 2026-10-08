"""How a schema of ours is assembled, written and checked against: one way for every file kind.

Every schema we publish is generated from the code that writes or reads its file, never written by
hand beside it, and a test holds each checked-in copy equal to its generator. This class is what the
generators share:

* `document` -- the head every schema opens with: the JSON Schema 2020-12 dialect, the `$id` URN that
  names the file kind and its format version (`SchemaIdentifier`), a title and a description;
* `write` -- the schema as a file, byte for byte the same on every machine;
* `problems` and `refusal` -- a document checked against a schema with the validator the scenario
  compiler uses (`ScenarioSchema.validate_against`), so a published schema can say no more than a
  reader enforces, and the refusal a reader raises with the schema's own words;
* the fragments several schemas share: a nullable value, and the `producer` record every file our
  tools write carries (`ProducerRecord`).
"""
from __future__ import annotations

import json
from pathlib import Path

from carlacontrol.ScenarioSchema import ScenarioSchema
from carlacontrol.SchemaIdentifier import DIALECT, SchemaIdentifier

# The name every file gives its producer record.
PRODUCER = "producer"
_SHA256 = {"type": "string", "pattern": "^[0-9a-f]{64}$"}
_TEXT = {"type": "string", "minLength": 1}


class SchemaPublication:
    """The head, the shared fragments, the file and the check of a published schema."""

    SHA256 = _SHA256
    TEXT = _TEXT

    @staticmethod
    def document(kind: str, version: int, title: str, description: str, body: dict) -> dict:
        """A whole schema: the head, then `body` (its type, properties and definitions)."""
        return {"$schema": DIALECT, "$id": SchemaIdentifier.urn(kind, version), "title": title,
                "description": description, **body}

    @staticmethod
    def write(path: str | Path, schema: dict) -> Path:
        """Publish a schema, byte-stable: two-space indent, UTF-8, a final newline, LF endings."""
        path = Path(path)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(schema, indent=2, ensure_ascii=False) + "\n", encoding="utf-8",
                        newline="\n")
        return path

    @staticmethod
    def problems(document: object, schema: dict) -> list[str]:
        """Every place `document` departs from `schema`, as `$.path: problem`; empty when it
        conforms."""
        return ScenarioSchema.validate_against(document, schema)

    @staticmethod
    def refusal(source: object, schema: dict, problems: list[str]) -> str:
        """The message a reader refuses a file with: the file, the schema it was checked against,
        and every problem the check found."""
        return (f"{source} does not conform to {schema['$id']} ({schema['title']}): "
                + "; ".join(problems))

    @staticmethod
    def required(properties: dict) -> list[str]:
        """Every property but the producer record, which a file written before the record existed
        (2026-10-07) does not carry at the same format version."""
        return [name for name in properties if name != PRODUCER]

    @staticmethod
    def nullable(fragment: dict, description: str | None = None) -> dict:
        """`fragment`, or null."""
        node: dict = {"anyOf": [fragment, {"type": "null"}]}
        if description:
            node["description"] = description
        return node

    @staticmethod
    def described(fragment: dict, description: str) -> dict:
        """`fragment` with a description of its own."""
        return {**fragment, "description": description}

    @staticmethod
    def open_object(description: str) -> dict:
        """An object whose members this schema does not fix."""
        return {"type": "object", "description": description}

    @staticmethod
    def producer() -> dict:
        """The `producer` record: what wrote the file (`ProducerRecord.record`).

        `server` is the CARLA server's build identity where the writer reached one: the object
        `get_build_identity` returns, or, where it could not be had, `available` false with the
        release, the world interface and the reason. Its members are the server's, so they are left
        open here.
        """
        nullable_text = SchemaPublication.nullable(_TEXT)
        return {
            "type": "object", "additionalProperties": False,
            "required": ["tool", "tool_version", "carlanet", "server", "sumo"],
            "description": "What wrote the file: the tool and its release, the carlanet release, the "
                           "CARLA server and the SUMO release where they were used, and when. A "
                           "file written before 2026-10-07 has none.",
            "properties": {
                "tool": SchemaPublication.described(
                    _TEXT, "The component that wrote the file, such as "
                           "carlacontrol.ScenarioCompiler."),
                "tool_version": SchemaPublication.described(
                    _TEXT, "The carlacontrol release the tool is from, with the commit it was "
                           "built at."),
                "carlanet": SchemaPublication.described(
                    nullable_text, "The release of the CarlaNet libraries the process loaded; null "
                                   "where it loaded none."),
                "server": SchemaPublication.nullable(
                    {"type": "object"},
                    "The CARLA server's build identity where the writer reached a server; null "
                    "where it reached none. Where the identity could not be had it is "
                    "available false, with the release, the world interface and the reason."),
                "sumo": SchemaPublication.described(
                    nullable_text, "The SUMO release where SUMO ran; null where it did not."),
                "written_utc": {"type": "string",
                                "pattern": r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z$",
                                "description": "When the file was written, ISO 8601 in UTC. Left "
                                               "out of a file that two runs of one input must write "
                                               "byte for byte."},
            },
        }
