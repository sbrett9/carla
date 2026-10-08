"""The schemas of the vehicle catalogue's JSON files.

  * `vehicles.catalogue.json`: every CARLA vehicle blueprint measured on a running server, and the
    authoring classes a scenario draws vehicles from. Written by `VehicleCatalogueBuilder` (and
    rewritten by `scripts/apply_vehicle_body_widths.py`), read by `VehicleCatalogue` and by
    `CarlaNet.CoSim.VehicleCatalogue`.
  * `vehicle_body_widths.json`: each body's width without its mirrors, measured from the mesh in the
    editor by `scripts/measure_vehicle_body_widths.py` and merged into the catalogue.

The third catalogue file, `vehicles.vtypes.rou.xml`, is a SUMO route file; its schema is
`CarlaControl/schemas/vehicle_types.xsd`, written by hand.

The vocabularies here are the code's own: the lamps and verdicts `VehicleCatalogue` names, the base
types and SUMO names `VehicleClassAssignment` checks, and the identifier and color forms
`VehicleCatalogueValidator` checks. Both schemas are published to `CarlaControl/schemas/` by `write`
and held equal to this module by a test; `VehicleCatalogue.load` and
`VehicleCatalogueBuilder.load_body_widths` check what they read against them.

The schema holds the shape. The rules that relate one field to another -- a class drawing only
measured blueprints, a body no wider than its box, every measured blueprint in exactly one class --
are `VehicleCatalogueValidator`'s, which the builder runs before it writes.
"""
from __future__ import annotations

from pathlib import Path

from carlacontrol.SchemaIdentifier import DIALECT, SchemaIdentifier
from carlacontrol.SchemaPublication import (
    NON_EMPTY_TEXT,
    NUMBER,
    SHA256_HEX,
    STRING,
    UTC_MILLISECONDS,
    SchemaPublication,
)
from carlacontrol.VehicleCatalogue import LAMP_NAMES, LAMP_VERDICTS, SUPPORTED_CATALOGUE_VERSION
from carlacontrol.VehicleCatalogueValidator import (
    BLUEPRINT_ID_PATTERN,
    CARLA_COLOUR_PATTERN,
    CLASS_ID_PATTERN,
    COLOUR_APPLIED_VALUES,
    MINIMUM_DIMENSION_M,
    REQUIRED_CLASS_PARAMETERS,
    SUMO_COLOUR_PATTERN,
)
from carlacontrol.VehicleClassAssignment import (
    COT_BASE_TYPES,
    COT_SPECIAL_TYPES,
    SUMO_GUI_SHAPES,
    SUMO_VEHICLE_CLASSES,
)

CATALOGUE_SCHEMA = "vehicle_catalogue.schema.json"
BODY_WIDTHS_SCHEMA = "vehicle_body_widths.schema.json"

# The body-width table's format, as `scripts/measure_vehicle_body_widths.py` writes it and
# `VehicleCatalogueBuilder.load_body_widths` reads it.
BODY_WIDTHS_VERSION = 1

# What each class's driving parameter is, in SUMO's own vType attribute and the unit it is in.
CLASS_PARAMETERS: dict[str, str] = {
    "accel_mps2": "SUMO accel: the most the vehicle accelerates, meters per second squared.",
    "decel_mps2": "SUMO decel: how hard the vehicle brakes when it wants to, meters per second "
                  "squared.",
    "sigma": "SUMO sigma: the driver's imperfection in the Krauss car-following model, from 0 "
             "(perfect) to 1.",
    "speed_factor_mean": "SUMO speedFactor: the mean multiple of the speed limit the driver keeps.",
    "speed_factor_dev": "SUMO speedDev: the spread of that multiple between drivers.",
    "min_gap_m": "SUMO minGap: the gap, meters, the driver leaves to the vehicle ahead when "
                 "stopped.",
    "max_speed_mps": "SUMO maxSpeed: the vehicle's top speed, meters per second.",
}


class VehicleCatalogueSchemas:
    """The vehicle catalogue's JSON schemas, as published and as its readers check them."""

    @classmethod
    def schemas(cls) -> dict[str, dict]:
        """Every schema this module publishes, by file name."""
        return {CATALOGUE_SCHEMA: cls.catalogue(), BODY_WIDTHS_SCHEMA: cls.body_widths()}

    @classmethod
    def write(cls, directory: str | Path) -> list[Path]:
        """Publish every schema into `directory`."""
        return [SchemaPublication.write(Path(directory) / name, schema)
                for name, schema in cls.schemas().items()]

    # -- vehicles.catalogue.json ----------------------------------------------------------------

    @staticmethod
    def catalogue() -> dict:
        """`vehicles.catalogue.json`, as `VehicleCatalogueBuilder` writes it."""
        dimension = {"type": "number", "exclusiveMinimum": MINIMUM_DIMENSION_M}
        lamp_verdict = {"enum": sorted(LAMP_VERDICTS)}
        lamp_capability = {
            "type": "object", "additionalProperties": False, "required": list(LAMP_NAMES),
            "description": "For each of the eleven lamps CARLA can command, what the optical pass "
                           "saw: lit, the image changed; unlit, the lamp was commanded and the "
                           "image did not change; unknown, not measured.",
            "properties": {name: lamp_verdict for name in LAMP_NAMES},
        }
        settable_attribute = {
            "type": "object", "additionalProperties": False,
            "required": ["id", "type", "restrict_to_recommended", "recommended_values"],
            "properties": {
                "id": SchemaPublication.described(NON_EMPTY_TEXT, "The attribute's name, such as "
                                                                  "color or role_name."),
                "type": SchemaPublication.described(NON_EMPTY_TEXT, "CARLA's attribute type, such as "
                                                                    "RGBColor, String or Bool."),
                "restrict_to_recommended": SchemaPublication.described(
                    {"type": "boolean"}, "True when only the recommended values are accepted."),
                "recommended_values": {"type": "array", "items": STRING,
                                       "description": "The values the blueprint recommends."},
            },
        }
        declared = {
            "blueprint_id": {"type": "string", "pattern": f"^{BLUEPRINT_ID_PATTERN.pattern}",
                             "description": "The CARLA blueprint id, such as vehicle.lincoln.mkz."},
            "uid": SchemaPublication.described({"type": "integer"},
                                               "The blueprint's numeric id on the server that was "
                                               "measured."),
            "tags": SchemaPublication.described(STRING, "The blueprint's tags, comma separated."),
            "declared_base_type": SchemaPublication.described(
                STRING, "The base_type the content declares for the blueprint. Truth does not use "
                        "it; it uses the class's cot_base_type."),
            "declared_special_type": SchemaPublication.described(
                STRING, "The special_type the content declares. Truth does not use it; it uses the "
                        "class's cot_special_type."),
            "number_of_wheels": SchemaPublication.described(
                {"type": "integer"}, "Wheels the blueprint declares; -1 when it declares none."),
            "generation": SchemaPublication.described(
                {"type": "integer"}, "The CARLA generation the blueprint declares; -1 when none."),
            "declared_has_lights": SchemaPublication.described(
                {"type": "boolean"}, "Whether the blueprint declares it has lights."),
            "settable_attributes": {"type": "array", "items": settable_attribute,
                                    "description": "Every attribute a spawn may set, by id."},
            "colour_settable": SchemaPublication.described(
                {"type": "boolean"}, "Whether the blueprint has a color attribute."),
            "colour_palette": {"type": "array",
                               "items": {"type": "string",
                                         "pattern": f"^{CARLA_COLOUR_PATTERN.pattern}"},
                               "description": "The body colors the blueprint recommends, each "
                                              "\"R,G,B\" from 0 to 255."},
            "colour_applied": {"enum": sorted(COLOUR_APPLIED_VALUES),
                               "description": "Whether a requested color reached the body: true, "
                                              "false, or unknown when the server log could not "
                                              "say."},
            "lamp_capability": lamp_capability,
        }
        measured = {
            "type": "object", "additionalProperties": False,
            "required": [*declared, "measurement", "length_m", "width_m", "height_m",
                         "bbox_centre_m"],
            "properties": {
                **declared,
                "measurement": {"const": "measured"},
                "length_m": SchemaPublication.described(
                    dimension, "Length of the spawned body's bounding box, meters, front to back."),
                "width_m": SchemaPublication.described(
                    dimension, "Width of the bounding box, meters, mirrors included."),
                "height_m": SchemaPublication.described(
                    dimension, "Height of the bounding box, meters."),
                "bbox_centre_m": SchemaPublication.numbers(
                    3, "Center of the bounding box in the vehicle's own frame, three numbers in "
                       "meters: forward, right, up from the actor's origin."),
                "body_width_m": SchemaPublication.described(
                    dimension, "Width of the body without its mirrors, meters, measured from the "
                               "mesh in the editor. SUMO is given this width."),
            },
        }
        failed = {
            "type": "object", "additionalProperties": False,
            "required": [*declared, "measurement", "measurement_note"],
            "properties": {
                **declared,
                "measurement": {"const": "failed"},
                "measurement_note": SchemaPublication.described(
                    NON_EMPTY_TEXT, "Why the blueprint could not be measured."),
            },
        }
        member = {"type": "object", "additionalProperties": False,
                  "required": ["blueprint_id", "weight"],
                  "properties": {
                      "blueprint_id": {"type": "string",
                                       "pattern": f"^{BLUEPRINT_ID_PATTERN.pattern}",
                                       "description": "A measured blueprint the class draws."},
                      "weight": {"type": "number", "exclusiveMinimum": 0,
                                 "description": "Its relative chance of being drawn."}}}
        vehicle_class = {
            "type": "object", "additionalProperties": False,
            "required": ["class_id", "description", "sumo_vclass", "cot_base_type", "members",
                         *REQUIRED_CLASS_PARAMETERS, "gui_shape", "gui_colour",
                         "render_colour_policy"],
            "properties": {
                "class_id": {"type": "string", "pattern": f"^{CLASS_ID_PATTERN.pattern}",
                             "description": "The class's id, which a scenario names and which "
                                            "every vehicle drawn from it carries as "
                                            "carla:class_id."},
                "description": SchemaPublication.described(NON_EMPTY_TEXT, "The class in words."),
                "sumo_vclass": {"enum": sorted(SUMO_VEHICLE_CLASSES),
                                "description": "SUMO's vehicle class: which lanes the vehicle may "
                                               "use and SUMO's defaults for it."},
                "cot_base_type": {"enum": sorted(COT_BASE_TYPES),
                                  "description": "The base_type truth records for every member."},
                "cot_special_type": {"enum": sorted(COT_SPECIAL_TYPES),
                                     "description": "The special_type truth records for every "
                                                    "member. Absent means empty."},
                "members": {"type": "array", "minItems": 1, "items": member,
                            "description": "The blueprints the class draws from."},
                **{name: SchemaPublication.described(NUMBER, text)
                   for name, text in CLASS_PARAMETERS.items()},
                "gui_shape": {"enum": sorted(SUMO_GUI_SHAPES),
                              "description": "SUMO guiShape. Read by sumo-gui only."},
                "gui_colour": {"type": "string", "pattern": f"^{SUMO_COLOUR_PATTERN.pattern}",
                               "description": "The #RRGGBB color sumo-gui draws the class in. It "
                                              "never reaches the rendered vehicle."},
                "render_colour_policy": SchemaPublication.described(
                    NON_EMPTY_TEXT, "How a rendered body's color is chosen: palette, from the "
                                    "blueprint's colour_palette. Nothing reads it yet."),
            },
        }
        lamp_probe = {
            "type": "object", "additionalProperties": False, "required": ["ran"],
            "description": "The conditions of the optical lamp pass, and whether it ran.",
            "properties": {
                "ran": SchemaPublication.described({"type": "boolean"}, "Whether the pass ran."),
                "reason": SchemaPublication.described(NON_EMPTY_TEXT, "Why it did not run, or "
                                                                      "stopped."),
                "solar_date": SchemaPublication.described(STRING, "The date the sun was set to, "
                                                                  "YYYY-MM-DD."),
                "solar_time_hours": SchemaPublication.described(NUMBER, "The solar time the sun was "
                                                                        "set to, hours."),
                "sun_elevation_deg": SchemaPublication.nullable(NUMBER, "The sun's elevation the "
                                                                        "server reported, degrees."),
                "positive_control_pixels": SchemaPublication.described(
                    {"type": "integer"}, "Pixels that changed when the sun was moved to daylight, "
                                         "which proves the camera sees a change."),
                "camera_poses": {"type": "array",
                                 "items": {"type": "object", "additionalProperties": False,
                                           "required": ["standoff_m", "height_offset_m",
                                                        "pitch_deg", "yaw_deg", "fov_deg"],
                                           "properties": {"standoff_m": NUMBER,
                                                          "height_offset_m": NUMBER,
                                                          "pitch_deg": NUMBER, "yaw_deg": NUMBER,
                                                          "fov_deg": NUMBER}},
                                 "description": "Where the probe camera stood relative to the "
                                                "vehicle, meters and degrees."},
                "image_size": SchemaPublication.numbers(2, "Probe image width and height, pixels."),
                "luminance_threshold": SchemaPublication.described(
                    {"type": "integer"}, "A pixel counts as gained above this brightness, out of "
                                            "255."),
                "region": SchemaPublication.numbers(4, "The part of the image counted, as fractions "
                                                       "of its width and height."),
                "minimum_lit_pixels": SchemaPublication.described(
                    {"type": "integer"}, "A lamp needs more gained pixels than this to be lit."),
                "drift_margin": SchemaPublication.described(
                    NUMBER, "A lamp also needs more than this many times the largest difference "
                            "between two images of the same state."),
                "average_frames": SchemaPublication.described(
                    {"type": "integer"}, "Frames averaged per image."),
            },
        }
        return {
            "$schema": DIALECT,
            "$id": SchemaIdentifier.urn("vehicle-catalogue", SUPPORTED_CATALOGUE_VERSION),
            "title": "Vehicle catalogue (vehicles.catalogue.json)",
            "description": "Every CARLA vehicle blueprint, measured by spawning it on a running "
                           "server, and the classes a scenario draws vehicles from. Meters, "
                           "seconds and degrees throughout.",
            "type": "object",
            "additionalProperties": False,
            "required": ["catalogue_version", "catalogue_id", "catalogue_digest", "content_build_id",
                         "blueprint_set_digest", "generated_at_utc", "generator", "server_version",
                         "lamp_probe", "vehicles", "classes"],
            "properties": {
                "catalogue_version": {"const": SUPPORTED_CATALOGUE_VERSION,
                                      "description": "The format of this file."},
                "catalogue_id": SchemaPublication.described(
                    NON_EMPTY_TEXT, "The catalogue's name: carla-<server version>-<os> unless "
                                       "given."),
                "catalogue_digest": {"type": "string", "pattern": SHA256_HEX,
                                     "description": "SHA-256 of this file in its canonical form "
                                                    "(sorted keys, two-space indent) with this "
                                                    "field empty. Scenarios record it."},
                "content_build_id": SchemaPublication.described(
                    NON_EMPTY_TEXT, "Which content build was measured."),
                "blueprint_set_digest": {"type": "string", "pattern": SHA256_HEX,
                                         "description": "SHA-256 over every vehicle definition "
                                                        "and attribute the server declared."},
                "generated_at_utc": {"type": "string", "pattern": UTC_MILLISECONDS,
                                     "description": "When the sweep ran, UTC."},
                "generator": SchemaPublication.described(NON_EMPTY_TEXT, "The tool and its version."),
                "server_version": SchemaPublication.described(NON_EMPTY_TEXT,
                                                              "The CARLA server's version."),
                "producer": SchemaPublication.described(
                    SchemaPublication.producer(), "What made the catalogue. Absent from a catalogue "
                                                  "built before it was recorded."),
                "lamp_probe": lamp_probe,
                "body_width": {"type": "object", "additionalProperties": False,
                               "required": ["method", "measured", "source"],
                               "description": "How the body widths were measured.",
                               "properties": {
                                   "method": SchemaPublication.described(NON_EMPTY_TEXT, "The method."),
                                   "measured": SchemaPublication.described(NON_EMPTY_TEXT,
                                                                           "When, and from what."),
                                   "source": SchemaPublication.described(
                                       NON_EMPTY_TEXT, "The table they were merged from.")}},
                "vehicles": {"type": "array",
                             "items": {"anyOf": [measured, failed]},
                             "description": "One entry per vehicle blueprint the server offered, "
                                            "in blueprint id order."},
                "classes": {"type": "array", "items": vehicle_class,
                            "description": "The authoring classes."},
            },
        }

    # -- vehicle_body_widths.json ---------------------------------------------------------------

    @staticmethod
    def body_widths() -> dict:
        """`vehicle_body_widths.json`, as `scripts/measure_vehicle_body_widths.py` writes it."""
        dimension = {"type": "number", "exclusiveMinimum": 0}
        row = {
            "type": "object", "additionalProperties": False,
            "required": ["length_m", "full_width_m", "body_width_m", "height_m"],
            "properties": {
                "length_m": SchemaPublication.described(dimension, "The mesh's length, meters."),
                "full_width_m": SchemaPublication.described(
                    dimension, "The mesh's whole width, meters, mirrors included. Must equal the "
                               "catalogue's width_m to within a millimeter."),
                "body_width_m": SchemaPublication.described(
                    dimension, "The width without mirrors and other short protrusions, meters."),
                "height_m": SchemaPublication.described(dimension, "The mesh's height, meters."),
            },
        }
        return {
            "$schema": DIALECT,
            "$id": SchemaIdentifier.urn("vehicle-body-widths", BODY_WIDTHS_VERSION),
            "title": "Vehicle body widths (vehicle_body_widths.json)",
            "description": "Each vehicle body's width without its mirrors, measured from the mesh "
                           "in the Unreal Editor, keyed by CARLA blueprint id.",
            "type": "object",
            "additionalProperties": False,
            "required": ["body_widths_version", "method", "measured", "vehicles"],
            "properties": {
                "body_widths_version": {"const": BODY_WIDTHS_VERSION,
                                        "description": "The format of this file."},
                "method": SchemaPublication.described(NON_EMPTY_TEXT, "How the widths were measured."),
                "measured": SchemaPublication.described(NON_EMPTY_TEXT, "When, and from what."),
                "vehicles": {"type": "object", "additionalProperties": row,
                             "description": "One row per blueprint id."},
            },
        }
