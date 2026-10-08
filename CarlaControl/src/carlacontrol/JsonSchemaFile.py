"""How a JSON Schema this package generates is named, published and checked against.

A schema generated here is a Python dict, held by the module of the file it describes, and published
to `CarlaControl/schemas/` by `write`: two-space indent, keys in the order the module gives them, UTF-8
with a final newline, so the published file is byte-stable and a test can hold it equal to the dict.

Its `$id` is a URN, `urn:carla-sumo-capture:schema:<kind>:<version>`, naming the kind of file, its
words joined by hyphens, and the format version the schema describes. It names no host, because none
serves the schemas.

`problems` checks a document against such a schema with the validator the scenario compiler uses
(`ScenarioSchema.validate_against`). That validator implements a subset of JSON Schema and refuses to
run on a keyword outside it, so a published schema states no more than its readers enforce. It resolves
a `$ref` only against the scenario schema's own definitions, so each local `$ref` here is replaced by
the definition it names before the document is checked: a published schema may then hold one
definition, the record of what made a file say, once, and use it wherever it applies.

The record of what made a file (`carlacontrol.ProducerRecord`, `CarlaNet.Types.Provenance.ProducerRecord`)
is defined here, once, for every schema this package generates (`producer_definitions`).
"""
from __future__ import annotations

import copy
import json
import re
from pathlib import Path

from carlacontrol.ScenarioSchema import ScenarioSchema

DRAFT = "https://json-schema.org/draft/2020-12/schema"
URN_PREFIX = "urn:carla-sumo-capture:schema:"
# A kind of file, as a schema's URN names it.
KIND = re.compile(r"[a-z][a-z0-9]*(-[a-z0-9]+)*")
DEFINITIONS = "$defs"
LOCAL_REFERENCE = "#/$defs/"

# ISO 8601 UTC to the millisecond, as both record writers stamp it: 2026-10-07T12:00:00.000Z.
UTC_MILLISECONDS = r"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}Z$"
# ISO 8601 UTC to any fraction of a second, or to the second: .NET's round-trip form writes seven
# digits, `date -u` none.
UTC_INSTANT = r"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]+)?Z$"
SHA256_HEX = r"^[0-9a-f]{64}$"
SHA256_HEX_OR_EMPTY = r"^([0-9a-f]{64})?$"
SHA1_HEX_OR_EMPTY = r"^([0-9a-f]{40})?$"
GIT_COMMIT_OR_EMPTY = r"^([0-9a-f]{40})?$"

TEXT = {"type": "string"}
NON_EMPTY_TEXT = {"type": "string", "minLength": 1}
NUMBER = {"type": "number"}
NON_NEGATIVE_INTEGER = {"type": "integer", "minimum": 0}


class JsonSchemaFile:
    """Names, publishes and checks against the JSON Schemas this package generates."""

    @staticmethod
    def identifier(name: str, version: int) -> str:
        """The schema's `$id`: `urn:carla-sumo-capture:schema:<kind>:<version>`, the kind in lower case
        with words joined by hyphens, as every schema of ours spells it (`world-package-manifest`).
        `name` may join its words with underscores, as the schema's file name does."""
        kind = name.replace("_", "-")
        if not KIND.fullmatch(kind) or isinstance(version, bool) or not isinstance(version, int) \
                or version < 1:
            raise ValueError(f"{name!r} version {version!r} cannot name a schema")
        return f"{URN_PREFIX}{kind}:{version}"

    @staticmethod
    def nullable(schema: dict, description: str | None = None) -> dict:
        """`schema`, or null."""
        either: dict = {"anyOf": [schema, {"type": "null"}]}
        if description:
            either = {"description": description, **either}
        return either

    @staticmethod
    def described(schema: dict, description: str) -> dict:
        """`schema` with a description in front of it."""
        return {"description": description, **schema}

    @staticmethod
    def numbers(count: int, description: str) -> dict:
        """An array of at least `count` numbers. The validator has no `maxItems`, so the schema says
        the exact count in words and the writer is what holds it to the count."""
        return {"description": description, "type": "array", "minItems": count,
                "items": {"type": "number"}}

    @staticmethod
    def producer_definitions() -> dict[str, dict]:
        """The record of what made a file, as `$defs` entries: `producer`, and the two shapes of the
        server identity it may hold."""
        server_text = {"type": "string", "minLength": 1}
        return {
            "producer": {
                "title": "What made the file",
                "description": "The tool that wrote the file and its release, the CarlaNet release the "
                               "process loaded, the CARLA server's build identity where a server was "
                               "used, the SUMO release where SUMO ran, and when the file was written.",
                "type": "object",
                "additionalProperties": False,
                "required": ["tool", "tool_version", "carlanet", "server", "sumo"],
                "properties": {
                    "tool": {"type": "string", "minLength": 1,
                             "description": "The component that wrote the file, such as "
                                            "carlacontrol.SumoCotBridge, or the program's name where "
                                            "no component declared itself."},
                    "tool_version": JsonSchemaFile.nullable(
                        NON_EMPTY_TEXT, "The release of the package the tool comes from, such as "
                                        "0.10.0 or 0.10.0+g1a2b3c4d5. Null where it did not say."),
                    "carlanet": JsonSchemaFile.nullable(
                        NON_EMPTY_TEXT, "The CarlaNet release the process loaded. Null in a Python "
                                        "process that never loaded CarlaNet."),
                    "server": {"description": "The CARLA server's build identity, where a server "
                                              "was used. Null where none was.",
                               "anyOf": [{"type": "null"},
                                         {"$ref": f"{LOCAL_REFERENCE}server_identity"},
                                         {"$ref": f"{LOCAL_REFERENCE}server_identity_unavailable"}]},
                    "sumo": JsonSchemaFile.nullable(
                        NON_EMPTY_TEXT, "The SUMO release, such as 1.27.0, where SUMO ran. Null "
                                        "where it did not."),
                    "written_utc": {"type": "string", "pattern": UTC_MILLISECONDS,
                                    "description": "When the file was written, UTC to the "
                                                   "millisecond. Left out of a file that two runs of "
                                                   "one input must write byte for byte."},
                },
            },
            "server_identity": {
                "title": "A server's build identity",
                "description": "What the server answered when asked what it was built from. A value "
                               "the server cannot know is the word unknown.",
                "type": "object",
                "additionalProperties": False,
                "required": ["available", "release", "world_interface", "build", "configuration",
                             "carla_commit", "content_commit", "engine_commit", "commits_from"],
                "properties": {
                    "available": {"const": True},
                    "release": JsonSchemaFile.described(server_text, "The CARLA release the server "
                                                                     "was compiled with."),
                    "world_interface": JsonSchemaFile.described(
                        server_text, "The world interface version the server declares, Major.Minor."),
                    "build": JsonSchemaFile.described(
                        server_text, "package for a cooked server, editor for one run from the "
                                     "editor."),
                    "configuration": JsonSchemaFile.described(
                        server_text, "The build configuration, such as Development or Shipping."),
                    "carla_commit": JsonSchemaFile.described(server_text, "The CARLA commit."),
                    "content_commit": JsonSchemaFile.described(server_text, "The content commit."),
                    "engine_commit": JsonSchemaFile.described(server_text,
                                                              "The Unreal Engine commit."),
                    "commits_from": JsonSchemaFile.described(
                        server_text, "Where the commits came from: version_file, the package's "
                                     "VERSION file; compiled, the CARLA commit compiled into an "
                                     "editor build; or none."),
                },
            },
            "server_identity_unavailable": {
                "title": "A server that could not say what it was built from",
                "description": "A server built before it could answer, or one that could not be "
                               "asked: what its older calls still say, and why.",
                "type": "object",
                "additionalProperties": False,
                "required": ["available", "release", "world_interface", "reason"],
                "properties": {
                    "available": {"const": False},
                    "release": JsonSchemaFile.described(server_text, "The CARLA release, or "
                                                                     "unknown."),
                    "world_interface": JsonSchemaFile.described(
                        server_text, "The world interface version, or unknown."),
                    "reason": JsonSchemaFile.described(server_text,
                                                       "Why the identity is not available."),
                },
            },
        }

    # -- publishing ----------------------------------------------------------------------------

    @staticmethod
    def text(schema: dict) -> str:
        """The schema as it is published."""
        return json.dumps(schema, indent=2, ensure_ascii=False) + "\n"

    @classmethod
    def write(cls, schema: dict, path: str | Path) -> Path:
        """Publish a schema, byte-stable."""
        path = Path(path)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(cls.text(schema), encoding="utf-8", newline="\n")
        return path

    # -- checking ------------------------------------------------------------------------------

    @classmethod
    def problems(cls, document: object, schema: dict, *, top_level_required: bool = True) -> list[str]:
        """Every place `document` departs from `schema`, as `$.path: problem`. Empty when it is valid.

        `top_level_required` false checks every field the document holds and refuses any the schema
        does not name, but does not insist on the fields the schema requires at its top: for a reader
        that reads only some fields and must keep reading files made with fewer.
        """
        resolved = cls.resolved(schema)
        if not top_level_required:
            resolved.pop("required", None)
        return ScenarioSchema.validate_against(document, resolved)

    @classmethod
    def resolved(cls, schema: dict) -> dict:
        """A copy of `schema` with every local `$ref` replaced by the definition it names, and no
        `$defs` left."""
        definitions = schema.get(DEFINITIONS, {})

        def inline(node: object, seen: tuple[str, ...]) -> object:
            if isinstance(node, dict):
                reference = node.get("$ref")
                if isinstance(reference, str) and reference.startswith(LOCAL_REFERENCE):
                    name = reference[len(LOCAL_REFERENCE):]
                    if name in seen:
                        raise ValueError(f"the schema's definition {name!r} refers to itself")
                    rest = {key: value for key, value in node.items() if key != "$ref"}
                    target = inline(copy.deepcopy(definitions[name]), (*seen, name))
                    return {**target, **rest} if rest else target
                return {key: inline(value, seen) for key, value in node.items()
                        if key != DEFINITIONS}
            if isinstance(node, list):
                return [inline(item, seen) for item in node]
            return node

        return inline(copy.deepcopy(schema), ())
