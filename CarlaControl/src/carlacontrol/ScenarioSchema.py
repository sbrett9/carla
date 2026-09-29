"""The scenario specification's schema: one document, used to validate and published as-is.

`07_Scenario_Authoring.md` §8.3 ships `schemas/scenario.schema.json` beside the authoring skill and
requires it to be the compiler's own schema, not a copy. So the schema lives here once, as a JSON
Schema (draft 2020-12) document; `validate` checks a specification against it and `write` publishes
the same document. A field the schema does not name is refused (check 53), because a field nothing
reads is one its author will later believe was honoured.

The schema fixes the **shape**. What a value means is checked where it is resolved: a time's form by
`CivilTimeResolver` (check 47), a place by `PlaceResolver` (check 7), the epoch and the illumination
default by the session's own readers (checks 33, 34, 39), a label by the vocabulary (checks 18, 45,
46). The epoch and illumination objects are typed only as objects here for that reason: their rules
are `CarlaNet.CoSim.SolarEpoch`'s and `IlluminationPolicy`'s, and restating them here would be a second
copy that can drift.

`validate` implements the subset of JSON Schema this document uses -- `type`, `properties`,
`required`, `additionalProperties`, `items`, `enum`, `const`, `minItems`, `minLength`, `pattern`,
`minimum`, `exclusiveMinimum`, `anyOf` and local `$ref` -- and refuses to run on a keyword outside
it, so the published schema cannot say more than the compiler enforces.
"""
from __future__ import annotations

import json
import re
from pathlib import Path

SPEC_VERSION = 1
SCHEMA_CHECK = 53

_IDENTIFIER = "^[A-Za-z0-9][A-Za-z0-9_.-]*$"
_TERM = "^[a-z0-9_]+:[a-z0-9_]+$"

_TIME = {"description": "A time: seconds, 'dN HH:MM[:SS]', 'HH:MM[:SS]', an ISO-8601 instant with "
                        "its offset, or {instant|at, plus}. Resolved by CivilTimeResolver",
         "anyOf": [{"type": "number"}, {"type": "string", "minLength": 1},
                   {"type": "object"}]}
_DURATION = {"description": "Seconds, or '[Nd][Nh][Nm][Ns]'",
             "anyOf": [{"type": "number", "minimum": 0}, {"type": "string", "minLength": 1}]}
_STRINGS = {"type": "array", "items": {"type": "string", "minLength": 1}}

SCHEMA: dict = {
    "$schema": "https://json-schema.org/draft/2020-12/schema",
    "$id": "https://carla.local/schemas/scenario.schema.json",
    "title": "SUMO behavioural-capture scenario specification",
    "type": "object",
    "additionalProperties": False,
    "required": ["spec_version", "scenario_id", "scenario_name", "description", "world",
                 "seeds", "simulation", "catalogue", "vehicle_classes"],
    "properties": {
        "spec_version": {"const": SPEC_VERSION},
        "scenario_id": {"type": "string", "pattern": _IDENTIFIER,
                        "description": "Stable across runs; the recorder's scenario_id"},
        "scenario_name": {"type": "string", "minLength": 1},
        "description": {"type": "string", "minLength": 1},
        "world": {
            "type": "object", "additionalProperties": False,
            "required": ["package", "network_fingerprint"],
            "properties": {
                "package": {"type": "string", "minLength": 1,
                            "description": "The world package, relative to this file"},
                "network_fingerprint": {"type": "string", "pattern": "^[0-9a-f]{64}$"},
            },
        },
        "epoch": {"type": "object", "description": "04_Contracts.md §11.3; read by SolarEpoch"},
        "illumination": {"type": "object",
                         "description": "The authored default, 04_Contracts.md §11.5; read by "
                                        "IlluminationPolicy"},
        "seeds": {"type": "object", "additionalProperties": False, "required": ["sumo"],
                  "properties": {"sumo": {"type": "integer", "minimum": 0}}},
        "simulation": {
            "type": "object", "additionalProperties": False, "required": ["end", "step_length_s"],
            "properties": {"end": _TIME,
                           "step_length_s": {"type": "number", "exclusiveMinimum": 0}},
        },
        "catalogue": {"type": "string", "minLength": 1,
                      "description": "The measured vehicle catalogue, relative to this file"},
        "vehicle_classes": {"type": "array", "minItems": 1, "items": {"$ref": "#/$defs/vehicle_class"}},
        "vehicle_mix": {"type": "string", "pattern": _IDENTIFIER,
                        "description": "The id of the whole-mix distribution, drawn by each "
                                       "class's share"},
        "vehicle_mixes": {"type": "array", "items": {"$ref": "#/$defs/named_vehicle_mix"},
                          "description": "Further named mixes, each drawn by its own shares of "
                                         "declared classes"},
        "places": {"type": "object", "additionalProperties": {"$ref": "#/$defs/place"}},
        "place_sets": {"type": "object", "additionalProperties": _STRINGS},
        "instants": {"type": "object", "additionalProperties": _TIME},
        "flows": {"type": "array", "items": {"$ref": "#/$defs/flow"}},
        "actors": {"type": "array", "items": {"$ref": "#/$defs/actor"}},
        "rotas": {"type": "array", "items": {"$ref": "#/$defs/rota"}},
        "capture_windows": {"type": "array", "items": {"$ref": "#/$defs/capture_window"}},
        "vocabulary": {"$ref": "#/$defs/vocabulary"},
        "supervision": {"$ref": "#/$defs/supervision"},
    },
    "$defs": {
        "vehicle_class": {
            "type": "object", "additionalProperties": False,
            "required": ["class_id", "blueprints", "sumo_vclass"],
            "properties": {
                "class_id": {"type": "string", "pattern": _IDENTIFIER},
                "blueprints": {"type": "array", "minItems": 1,
                               "items": {"type": "string", "minLength": 1}},
                "sumo_vclass": {"type": "string", "minLength": 1},
                "behaviour": {"type": "object", "additionalProperties": {"type": "string"}},
                "share": {"type": "number", "minimum": 0},
                "weights": {"type": "array", "items": {"type": "number", "exclusiveMinimum": 0}},
                "gui_shape": {"type": "string"},
                "gui_colour": {"type": "string"},
                "note": {"type": "string"},
            },
        },
        "named_vehicle_mix": {
            "type": "object", "additionalProperties": False, "required": ["id", "shares"],
            "properties": {
                "id": {"type": "string", "pattern": _IDENTIFIER},
                "shares": {"type": "object",
                           "additionalProperties": {"type": "number", "exclusiveMinimum": 0},
                           "description": "Class id to that class's weight in this mix"},
                "note": {"type": "string"},
            },
        },
        "place": {
            "description": "Exactly one form. Resolved by PlaceResolver",
            "anyOf": [
                {"type": "object", "additionalProperties": False, "required": ["edge"],
                 "properties": {"edge": {"type": "string", "minLength": 1},
                                "offset_m": {"type": "number", "minimum": 0}}},
                {"type": "object", "additionalProperties": False, "required": ["lane", "offset_m"],
                 "properties": {"lane": {"type": "string", "minLength": 1},
                                "offset_m": {"type": "number", "minimum": 0}}},
                {"type": "object", "additionalProperties": False, "required": ["area"],
                 "properties": {"area": {"type": "string", "minLength": 1}}},
                {"type": "object", "additionalProperties": False,
                 "required": ["street", "direction"],
                 "properties": {"street": {"type": "string", "minLength": 1},
                                "direction": {"enum": ["north", "east", "south", "west"]},
                                "at": {"type": "string", "minLength": 1},
                                "near": {"$ref": "#/$defs/geographic_point"}}},
                {"type": "object", "additionalProperties": False,
                 "required": ["lat", "lon", "max_snap_m"],
                 "properties": {"lat": {"type": "number"}, "lon": {"type": "number"},
                                "max_snap_m": {"type": "number", "exclusiveMinimum": 0},
                                "vclass": {"type": "string", "minLength": 1}}},
                {"type": "object", "additionalProperties": False,
                 "required": ["gateway", "travel"],
                 "properties": {"gateway": {"enum": ["north", "east", "south", "west"]},
                                "travel": {"enum": ["in", "out"]},
                                "street": {"type": "string", "minLength": 1}}},
                {"type": "object", "additionalProperties": False,
                 "required": ["from_street", "to_street"],
                 "properties": {"from_street": {"type": "string", "minLength": 1},
                                "to_street": {"type": "string", "minLength": 1},
                                "from_direction": {"enum": ["north", "east", "south", "west"]},
                                "to_direction": {"enum": ["north", "east", "south", "west"]}}},
            ],
        },
        "geographic_point": {
            "type": "object", "additionalProperties": False, "required": ["lat", "lon"],
            "properties": {"lat": {"type": "number"}, "lon": {"type": "number"}},
        },
        "stop": {
            "type": "object", "additionalProperties": False, "required": ["place"],
            "properties": {"place": {"type": "string", "minLength": 1},
                           "duration": _DURATION, "until": _TIME,
                           "parking": {"type": "boolean"}},
        },
        "flow": {
            "type": "object", "additionalProperties": False,
            "required": ["id", "type", "from", "to", "vehs_per_hour", "begin", "end"],
            "properties": {
                "id": {"type": "string", "pattern": _IDENTIFIER},
                "type": {"type": "string", "minLength": 1},
                "from": {"type": "string", "minLength": 1},
                "to": {"type": "string", "minLength": 1},
                "via": _STRINGS,
                "vehs_per_hour": {"type": "number", "exclusiveMinimum": 0},
                "begin": _TIME, "end": _TIME,
                "depart_lane": {"type": "string"}, "depart_speed": {"type": "string"},
            },
        },
        "actor": {
            "type": "object", "additionalProperties": False, "required": ["id", "type", "depart"],
            "properties": {
                "id": {"type": "string", "pattern": _IDENTIFIER},
                "depart": _TIME,
                "type": {"type": "string", "minLength": 1},
                "from": {"type": "string", "minLength": 1},
                "to": {"type": "string", "minLength": 1},
                "via": _STRINGS,
                "route": {"type": "array", "minItems": 2, "items": {"type": "string", "minLength": 1}},
                "phases": {"type": "array", "minItems": 1, "items": {"$ref": "#/$defs/phase"},
                           "description": "An explicit route in phases, each driven `repeat` times "
                                          "and optionally held to a speed; instead of route or "
                                          "from/to"},
                "stops": {"type": "array", "items": {"$ref": "#/$defs/stop"}},
                "depart_lane": {"type": "string"}, "depart_speed": {"type": "string"},
                "arrival_speed": {"type": "string"},
            },
        },
        "phase": {
            "type": "object", "additionalProperties": False, "required": ["route"],
            "properties": {
                "route": {"type": "array", "minItems": 1, "items": {"type": "string", "minLength": 1},
                          "description": "Places naming one edge each, in driving order"},
                "repeat": {"type": "integer", "minimum": 1,
                           "description": "How many times the phase's route is driven; default 1"},
                "hold": {"anyOf": [{"type": "number", "exclusiveMinimum": 0},
                                   {"const": "posted"}],
                         "description": "Hold the vehicle to this speed in m/s, capped at each "
                                        "edge's limit, or to each edge's own limit ('posted'), "
                                        "over every edge of the phase: one waypoint per edge. "
                                        "Without it the vehicle drives at its own speedFactor"},
            },
        },
        "rota": {
            "type": "object", "additionalProperties": False,
            "required": ["id", "days", "at", "subjects", "id_pattern", "template"],
            "properties": {
                "id": {"type": "string", "pattern": _IDENTIFIER},
                "days": {"anyOf": [{"type": "string", "pattern": r"^\d+\.\.\d+$"},
                                   {"type": "array", "items": {"type": "integer", "minimum": 0}}]},
                "at": {"type": "array", "minItems": 1, "items": {"type": "string"}},
                "subjects": {"anyOf": [_STRINGS,
                                       {"type": "object", "additionalProperties": False,
                                        "required": ["place_set"],
                                        "properties": {"place_set": {"type": "string"}}}]},
                "id_pattern": {"type": "string", "minLength": 1},
                "template": {
                    "type": "object", "additionalProperties": False, "required": ["type"],
                    "properties": {
                        "type": {"type": "string", "minLength": 1},
                        "from": {"type": "string", "minLength": 1},
                        "to": {"type": "string", "minLength": 1},
                        "via": _STRINGS,
                        "stops": {"type": "array", "items": {"$ref": "#/$defs/stop"}},
                        "depart_lane": {"type": "string"}, "depart_speed": {"type": "string"},
                        "arrival_speed": {"type": "string"},
                    },
                },
                "skip": {"type": "array", "items": {
                    "type": "object", "additionalProperties": False,
                    "required": ["day", "at", "because"],
                    "properties": {"day": {"type": "integer", "minimum": 0},
                                   "at": {"type": "string"},
                                   "subject_index": {"type": "integer", "minimum": 0},
                                   "subject": {"type": "string"},
                                   "because": {"type": "string", "minLength": 1}}}},
            },
        },
        "capture_window": {
            "type": "object", "additionalProperties": False, "required": ["id", "begin", "length"],
            "properties": {"id": {"type": "string", "pattern": _IDENTIFIER},
                           "begin": _TIME, "length": _DURATION},
        },
        "term": {
            "type": "object", "additionalProperties": False,
            "required": ["term", "definition", "applies_to", "realisation", "since", "status"],
            "properties": {
                "term": {"type": "string", "pattern": _TERM},
                "definition": {"type": "string", "minLength": 1},
                "applies_to": {"type": "array", "minItems": 1,
                               "items": {"enum": ["entity", "cohort", "slot"]}},
                "realisation": {"type": "array", "minItems": 1,
                                "items": {"enum": ["present", "absent"]}},
                "since": {"type": "integer", "minimum": 1},
                "status": {"enum": ["active", "deprecated"]},
                "superseded_by": {"type": "string", "pattern": _TERM},
                "broader": {"type": "string", "pattern": _TERM},
                "parameters": {"type": "object", "additionalProperties": {
                    "type": "object", "additionalProperties": False,
                    "required": ["type", "definition"],
                    "properties": {"type": {"enum": ["number", "integer", "string", "boolean"]},
                                   "unit": {"type": "string"},
                                   "definition": {"type": "string", "minLength": 1}}}},
                "counterfactual": {"$ref": "#/$defs/counterfactual"},
                "contrast_with": {"type": "array", "items": {"type": "string", "pattern": _TERM}},
                "hard_negative_for": {"type": "array",
                                      "items": {"type": "string", "pattern": _TERM}},
                "exemplar_instances": _STRINGS,
            },
        },
        "namespace": {
            "type": "object", "additionalProperties": False,
            "required": ["namespace", "version", "authority", "terms"],
            "properties": {
                "namespace": {"type": "string", "pattern": "^[a-z0-9_]+$"},
                "version": {"type": "integer", "minimum": 1},
                "authority": {"type": "string", "minLength": 1},
                "terms": {"type": "array", "items": {"$ref": "#/$defs/term"}},
                "roles": {"type": "array", "items": {
                    "type": "object", "additionalProperties": False,
                    "required": ["role", "definition"],
                    "properties": {"role": {"type": "string", "pattern": _TERM},
                                   "definition": {"type": "string", "minLength": 1}}}},
                "area_kinds": {"type": "array", "items": {
                    "type": "object", "additionalProperties": False, "required": ["kind"],
                    "properties": {"kind": {"type": "string", "pattern": _TERM},
                                   "definition": {"type": "string"}}}},
            },
        },
        "vocabulary": {
            "type": "object", "additionalProperties": False,
            "properties": {
                "import": {"type": "array", "items": {"type": "string", "minLength": 1},
                           "description": "Namespace documents travelling in the bundle, "
                                          "relative to this file"},
                "namespaces": {"type": "array", "items": {"$ref": "#/$defs/namespace"}},
            },
        },
        "counterfactual": {
            "type": "object", "additionalProperties": False, "required": ["kind", "ref"],
            "properties": {"kind": {"enum": ["series", "cohort", "instance", "term"]},
                           "ref": {"type": "string", "minLength": 1}},
        },
        "interval": {
            "type": "object", "additionalProperties": False,
            "required": ["participant", "phase", "begin"],
            "properties": {"participant": {"type": "string", "minLength": 1},
                           "phase": {"type": "string", "minLength": 1},
                           "begin": _TIME, "end": _TIME, "duration": _DURATION},
        },
        "supervision": {
            "type": "object", "additionalProperties": False,
            "properties": {
                "instances": {"type": "array", "items": {
                    "type": "object", "additionalProperties": False,
                    "required": ["name", "supervision", "labels", "participants"],
                    "properties": {
                        "name": {"type": "string", "pattern": "^[a-z0-9_]+$"},
                        "supervision": {"enum": ["annotated", "nominal"]},
                        "labels": {"type": "array", "items": {"type": "string", "pattern": _TERM}},
                        "participants": {"type": "array", "items": {
                            "type": "object", "additionalProperties": False,
                            "required": ["actor", "role"],
                            "properties": {"actor": {"type": "string", "minLength": 1},
                                           "role": {"type": "string", "minLength": 1}}}},
                        "intervals": {"type": "array", "items": {"$ref": "#/$defs/interval"}},
                        "aoi_refs": _STRINGS,
                        "parameters": {"type": "object"},
                        "counterfactual": {"$ref": "#/$defs/counterfactual"},
                    }}},
                "cohorts": {"type": "array", "items": {
                    "type": "object", "additionalProperties": False,
                    "required": ["flow", "supervision"],
                    "properties": {"flow": {"type": "string", "minLength": 1},
                                   "supervision": {"enum": ["annotated", "unlabelled", "nominal"]},
                                   "labels": {"type": "array",
                                              "items": {"type": "string", "pattern": _TERM}},
                                   "intervals": {"type": "array"}}}},
                "series": {"type": "array", "items": {
                    "type": "object", "additionalProperties": False,
                    "required": ["series_id", "rota", "member_role", "slot_length",
                                 "slot_aoi_refs", "supervision"],
                    "properties": {
                        "series_id": {"type": "string", "pattern": "^[a-z0-9_]+$"},
                        "rota": {"type": "string", "minLength": 1},
                        "member_role": {"type": "string", "minLength": 1},
                        "slot_length": _DURATION,
                        "slot_aoi_refs": {"type": "object",
                                          "additionalProperties": {"type": "string"}},
                        "supervision": {"enum": ["annotated", "nominal", "unlabelled"]},
                        "labels": {"type": "array", "items": {"type": "string", "pattern": _TERM}},
                    }}},
                "absences": {"type": "array", "items": {
                    "type": "object", "additionalProperties": False,
                    "required": ["name", "series", "entry", "labels"],
                    "properties": {
                        "name": {"type": "string", "pattern": "^[a-z0-9_]+$"},
                        "series": {"type": "string", "minLength": 1},
                        "entry": {"type": "string", "minLength": 1,
                                  "description": "The id the skipped rota entry would have had"},
                        "labels": {"type": "array", "minItems": 1,
                                   "items": {"type": "string", "pattern": _TERM}},
                        "aoi_refs": _STRINGS,
                        "counterfactual": {"$ref": "#/$defs/counterfactual"},
                    }}},
            },
        },
    },
}

_SUPPORTED_KEYWORDS = frozenset({
    "$schema", "$id", "title", "description", "type", "properties", "required",
    "additionalProperties", "items", "enum", "const", "minItems", "minLength", "pattern", "minimum",
    "exclusiveMinimum", "anyOf", "$ref", "$defs",
})

_JSON_TYPES = {
    "object": dict, "array": list, "string": str, "boolean": bool,
}


class ScenarioSchema:
    """Validates a specification against `SCHEMA`, and publishes `SCHEMA`."""

    @classmethod
    def validate(cls, document: object) -> list[str]:
        """Every place the document departs from the schema, as `$.path: problem`. Empty when valid."""
        cls._require_supported(SCHEMA)
        problems: list[str] = []
        cls._check(document, SCHEMA, "$", problems)
        return problems

    @classmethod
    def validate_against(cls, document: object, schema: dict) -> list[str]:
        """Every departure of a document from another schema written in the same subset -- the
        sweep's -- checked by the same validator."""
        cls._require_supported(schema)
        problems: list[str] = []
        cls._check(document, schema, "$", problems)
        return problems

    @classmethod
    def validate_definition(cls, document: object, definition: str, path: str) -> list[str]:
        """Every departure of a document from one of the schema's `$defs` -- a namespace document
        imported from the bundle is checked against the same definition an inline one is."""
        problems: list[str] = []
        cls._check(document, SCHEMA["$defs"][definition], path, problems)
        return problems

    @classmethod
    def write(cls, path: str | Path) -> Path:
        """Publish the schema, byte-stable, for the authoring skill."""
        path = Path(path)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(SCHEMA, indent=2, ensure_ascii=False) + "\n", encoding="utf-8",
                        newline="\n")
        return path

    @classmethod
    def _require_supported(cls, schema: object) -> None:
        if isinstance(schema, dict):
            unknown = set(schema) - _SUPPORTED_KEYWORDS
            # A properties / $defs map is keyed by names, not keywords.
            for key, value in schema.items():
                if key in ("properties", "$defs"):
                    for sub in value.values():
                        cls._require_supported(sub)
                elif key not in ("enum", "const", "required") and isinstance(value, (dict, list)):
                    cls._require_supported(value)
            if unknown:
                raise ValueError(f"the scenario schema uses {sorted(unknown)}, which the validator "
                                 "does not enforce; the published schema may not say more than the "
                                 "compiler checks")
        elif isinstance(schema, list):
            for item in schema:
                cls._require_supported(item)

    @classmethod
    def _check(cls, value: object, schema: dict, path: str, problems: list[str]) -> None:
        if "$ref" in schema:
            name = schema["$ref"].rsplit("/", 1)[-1]
            cls._check(value, SCHEMA["$defs"][name], path, problems)
            return
        if "anyOf" in schema:
            branches = []
            for branch in schema["anyOf"]:
                attempt: list[str] = []
                cls._check(value, branch, path, attempt)
                if not attempt:
                    break
                branches.append(attempt)
            else:
                shown = " | ".join("; ".join(b) for b in branches)
                problems.append(f"{path}: matches none of the accepted forms ({shown})")
            return
        if "const" in schema and value != schema["const"]:
            problems.append(f"{path}: must be {json.dumps(schema['const'])}, is {json.dumps(value)}")
            return
        if "enum" in schema and value not in schema["enum"]:
            problems.append(f"{path}: {json.dumps(value)} is not one of {schema['enum']}")
            return
        kind = schema.get("type")
        if kind is not None and not cls._is_type(value, kind):
            problems.append(f"{path}: must be {kind}, is {cls._type_name(value)}")
            return
        if isinstance(value, str):
            if len(value) < schema.get("minLength", 0):
                problems.append(f"{path}: must not be empty")
            if "pattern" in schema and not re.search(schema["pattern"], value):
                problems.append(f"{path}: {value!r} does not match {schema['pattern']}")
        if isinstance(value, (int, float)) and not isinstance(value, bool):
            if "minimum" in schema and value < schema["minimum"]:
                problems.append(f"{path}: {value} is below {schema['minimum']}")
            if "exclusiveMinimum" in schema and value <= schema["exclusiveMinimum"]:
                problems.append(f"{path}: {value} must be above {schema['exclusiveMinimum']}")
        if isinstance(value, list):
            if len(value) < schema.get("minItems", 0):
                problems.append(f"{path}: needs at least {schema['minItems']} item(s)")
            if "items" in schema:
                for index, item in enumerate(value):
                    cls._check(item, schema["items"], f"{path}[{index}]", problems)
        if isinstance(value, dict):
            properties = schema.get("properties", {})
            for name in schema.get("required", []):
                if name not in value:
                    problems.append(f"{path}: '{name}' is required")
            extra = schema.get("additionalProperties", True)
            for name, item in value.items():
                if name in properties:
                    cls._check(item, properties[name], f"{path}.{name}", problems)
                elif extra is False:
                    problems.append(f"{path}: '{name}' is not a field here")
                elif isinstance(extra, dict):
                    cls._check(item, extra, f"{path}.{name}", problems)

    @staticmethod
    def _is_type(value: object, kind: str) -> bool:
        if kind == "integer":
            return isinstance(value, int) and not isinstance(value, bool)
        if kind == "number":
            return isinstance(value, (int, float)) and not isinstance(value, bool)
        if kind == "null":
            return value is None
        return isinstance(value, _JSON_TYPES[kind])

    @staticmethod
    def _type_name(value: object) -> str:
        if value is None:
            return "null"
        if isinstance(value, bool):
            return "boolean"
        if isinstance(value, (int, float)):
            return "number"
        return {dict: "object", list: "array", str: "string"}.get(type(value), type(value).__name__)
