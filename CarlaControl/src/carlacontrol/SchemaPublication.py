"""How a schema of ours is assembled, written and checked against: one way for every file kind.

Every schema we publish is generated from the code that writes or reads its file, never written by
hand beside it, and a test holds each checked-in copy equal to its generator. This module is what the
generators share:

* `document` -- the head every schema opens with: the JSON Schema 2020-12 dialect, the `$id` URN that
  names the file kind and its format version (`SchemaIdentifier`), a title and a description;
* `text` and `write` -- the schema as a file, byte for byte the same on every machine: two-space
  indent, keys in the order the generator gives them, UTF-8, LF endings and a final newline;
* `problems` and `refusal` -- a document checked against a schema with the validator the scenario
  compiler uses (`ScenarioSchema.validate_against`), so a published schema can say no more than a
  reader enforces, and the refusal a reader raises with the schema's own words. That validator
  implements a subset of JSON Schema and refuses to run on a keyword outside it, and it resolves a
  local `$ref` against the `$defs` of the schema it is given;
* the fragments several schemas share: the patterns and types below, a nullable value, a described
  value, a fixed count of numbers, and the `producer` record every file our tools write carries
  (`ProducerRecord`), defined here once.
"""
from __future__ import annotations

import json
from pathlib import Path

from carlacontrol.ScenarioSchema import ScenarioSchema
from carlacontrol.SchemaIdentifier import DIALECT, SchemaIdentifier

# The name every file gives its producer record.
PRODUCER = "producer"

# ISO 8601 UTC to the millisecond, as both record writers stamp it: 2026-10-07T12:00:00.000Z.
UTC_MILLISECONDS = r"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}Z$"
# ISO 8601 UTC to any fraction of a second, or to the second: .NET's round-trip form writes seven
# digits, `date -u` none.
UTC_INSTANT = r"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]+)?Z$"
SHA256_HEX = r"^[0-9a-f]{64}$"
SHA256_HEX_OR_EMPTY = r"^([0-9a-f]{64})?$"
SHA1_HEX_OR_EMPTY = r"^([0-9a-f]{40})?$"
GIT_COMMIT_OR_EMPTY = r"^([0-9a-f]{40})?$"

STRING = {"type": "string"}
NON_EMPTY_TEXT = {"type": "string", "minLength": 1}
NUMBER = {"type": "number"}
NON_NEGATIVE_INTEGER = {"type": "integer", "minimum": 0}

__all__ = ["DIALECT", "GIT_COMMIT_OR_EMPTY", "NON_EMPTY_TEXT", "NON_NEGATIVE_INTEGER", "NUMBER",
           "PRODUCER", "SHA1_HEX_OR_EMPTY", "SHA256_HEX", "SHA256_HEX_OR_EMPTY", "STRING",
           "UTC_INSTANT", "UTC_MILLISECONDS", "SchemaPublication"]


class SchemaPublication:
    """The head, the shared fragments, the file and the check of a published schema."""

    SHA256 = {"type": "string", "pattern": SHA256_HEX}
    TEXT = NON_EMPTY_TEXT

    @staticmethod
    def document(kind: str, version: int, title: str, description: str, body: dict) -> dict:
        """A whole schema: the head, then `body` (its type, properties and definitions)."""
        return {"$schema": DIALECT, "$id": SchemaIdentifier.urn(kind, version), "title": title,
                "description": description, **body}

    # -- publishing ----------------------------------------------------------------------------

    @staticmethod
    def text(schema: dict) -> str:
        """The schema as it is published."""
        return json.dumps(schema, indent=2, ensure_ascii=False) + "\n"

    @classmethod
    def write(cls, path: str | Path, schema: dict) -> Path:
        """Publish a schema, byte-stable: two-space indent, UTF-8, a final newline, LF endings."""
        path = Path(path)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(cls.text(schema), encoding="utf-8", newline="\n")
        return path

    # -- checking ------------------------------------------------------------------------------

    @staticmethod
    def problems(document: object, schema: dict, *, top_level_required: bool = True) -> list[str]:
        """Every place `document` departs from `schema`, as `$.path: problem`; empty when it
        conforms.

        `top_level_required` false checks every field the document holds and refuses any the schema
        does not name, but does not insist on the fields the schema requires at its top: for a reader
        that reads only some fields and must keep reading files made with fewer.
        """
        if not top_level_required:
            schema = {key: value for key, value in schema.items() if key != "required"}
        return ScenarioSchema.validate_against(document, schema)

    @staticmethod
    def refusal(source: object, schema: dict, problems: list[str]) -> str:
        """The message a reader refuses a file with: the file, the schema it was checked against,
        and every problem the check found."""
        return (f"{source} does not conform to {schema['$id']} ({schema['title']}): "
                + "; ".join(problems))

    # -- fragments -----------------------------------------------------------------------------

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
        """`fragment` with a description of its own, in place of any it had."""
        return {**fragment, "description": description}

    @staticmethod
    def numbers(count: int, description: str) -> dict:
        """An array of at least `count` numbers. The schema says the exact count in words, and the
        writer is what holds it to the count."""
        return {"type": "array", "minItems": count, "items": {"type": "number"},
                "description": description}

    @staticmethod
    def open_object(description: str) -> dict:
        """An object whose members this schema does not fix."""
        return {"type": "object", "description": description}

    @staticmethod
    def producer() -> dict:
        """The `producer` record: what made the file, as `ProducerRecord.record` writes it in Python
        and `CarlaNet.Types.Provenance.ProducerRecord` in C#.

        `server` is the CARLA server's build identity where the writer reached one: what
        `get_build_identity` answered, or, where it could not be had, `available` false with the
        release, the world interface and the reason (`ProducerRecord.unavailable`).
        """
        server_text = NON_EMPTY_TEXT
        described = SchemaPublication.described
        nullable = SchemaPublication.nullable
        answered = {
            "type": "object", "additionalProperties": False,
            "required": ["available", "release", "world_interface", "build", "configuration",
                         "carla_commit", "content_commit", "engine_commit", "commits_from"],
            "description": "What the server answered when asked what it was built from. A value the "
                           "server cannot know is the word unknown.",
            "properties": {
                "available": {"const": True},
                "release": described(server_text, "The CARLA release the server was compiled "
                                                  "with."),
                "world_interface": described(server_text, "The world interface version the server "
                                                          "declares, Major.Minor."),
                "build": described(server_text, "package for a cooked server, editor for one run "
                                                "from the editor."),
                "configuration": described(server_text, "The build configuration, such as "
                                                        "Development or Shipping."),
                "carla_commit": described(server_text, "The CARLA commit."),
                "content_commit": described(server_text, "The content commit."),
                "engine_commit": described(server_text, "The Unreal Engine commit."),
                "commits_from": described(server_text, "Where the commits came from: version_file, "
                                                       "the package's VERSION file; compiled, the "
                                                       "CARLA commit compiled into an editor build; "
                                                       "or none."),
            },
        }
        unanswered = {
            "type": "object", "additionalProperties": False,
            "required": ["available", "release", "world_interface", "reason"],
            "description": "A server built before it could answer, or one that could not be asked: "
                           "what its older calls still say, and why.",
            "properties": {
                "available": {"const": False},
                "release": described(server_text, "The CARLA release, or unknown."),
                "world_interface": described(server_text, "The world interface version, or "
                                                          "unknown."),
                "reason": described(server_text, "Why the identity is not available."),
            },
        }
        return {
            "type": "object", "additionalProperties": False,
            "required": ["tool", "tool_version", "carlanet", "server", "sumo"],
            "description": "What made the file: the tool that wrote it and its release, the CarlaNet "
                           "release the process loaded, the CARLA server's build identity where a "
                           "server was used, the SUMO release where SUMO ran, and when the file was "
                           "written.",
            "properties": {
                "tool": described(NON_EMPTY_TEXT, "The component that wrote the file, such as "
                                                  "carlacontrol.ScenarioCompiler, or the program's "
                                                  "name where no component declared itself."),
                "tool_version": nullable(NON_EMPTY_TEXT, "The release of the package the tool comes "
                                                         "from, such as 0.10.0 or "
                                                         "0.10.0+g1a2b3c4d5. Null where it did not "
                                                         "say."),
                "carlanet": nullable(NON_EMPTY_TEXT, "The CarlaNet release the process loaded. Null "
                                                     "in a Python process that never loaded "
                                                     "CarlaNet."),
                "server": {"anyOf": [{"type": "null"}, answered, unanswered],
                           "description": "The CARLA server's build identity, where a server was "
                                          "used. Null where none was."},
                "sumo": nullable(NON_EMPTY_TEXT, "The SUMO release, such as 1.27.0, where SUMO ran. "
                                                 "Null where it did not."),
                "written_utc": {"type": "string", "pattern": UTC_MILLISECONDS,
                                "description": "When the file was written, UTC to the millisecond. "
                                               "Left out of a file that two runs of one input must "
                                               "write byte for byte."},
            },
        }
