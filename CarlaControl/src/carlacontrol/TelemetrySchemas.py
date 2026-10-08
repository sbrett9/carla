"""The schemas of the telemetry files the SUMO bridge writes, and of the legacy labels file it reads.

  * `<name>.csv` from `carla-cot-telemetry --csv`: one row per vehicle per update
    (`SumoCotBridge.CSV_COLUMNS`), described by a Frictionless Table Schema generated from those
    columns;
  * `<name>.summary.json`, written beside that CSV as it is opened: the CSV's format, its columns
    and what made it (`SumoCotBridge.write_csv_summary`);
  * `<name>.supervision.json`, the described supervision gaps a legacy scenario's labels carry,
    placed on the run's clock (`SupervisionSidecar`);
  * `*.labels.json`, a legacy scenario's labels, which `carla-cot-telemetry --labels` and
    `carla-check-label-leaks --labels` read and nothing in this repository writes any more.

The Cursor-on-Target XML -- one `<event>` per UDP datagram, and the `--xml` file of every event -- is
`CarlaControl/schemas/cot_telemetry.xsd`, written by hand.

Each schema is published to `CarlaControl/schemas/` by `write` and held equal to this module by a test,
which also runs the bridge and checks what it writes against them.
"""
from __future__ import annotations

from pathlib import Path

from carlacontrol.CotDisplayConvention import COT_AFFILIATIONS
from carlacontrol.JsonSchemaFile import (
    DRAFT,
    LOCAL_REFERENCE,
    NON_EMPTY_TEXT,
    NUMBER,
    UTC_MILLISECONDS,
    JsonSchemaFile,
)
from carlacontrol.SumoCotBridge import CSV_COLUMNS, CSV_FORMAT_VERSION
from carlacontrol.SupervisionSidecar import BEGIN_SECONDS, END_SECONDS, LABELS_KEY
from carlacontrol.VehicleClassAssignment import COT_SPECIAL_TYPES

CSV_TABLE_SCHEMA = "sumo_cot_telemetry.tableschema.json"
CSV_SUMMARY_SCHEMA = "sumo_cot_telemetry_summary.schema.json"
SUPERVISION_GAPS_SCHEMA = "supervision_gaps.schema.json"
LEGACY_LABELS_SCHEMA = "legacy_labels.schema.json"

# Neither the gap sidecar nor the legacy labels file carries a version. A file of a kind written
# before it carried one is version 1.
SUPERVISION_GAPS_VERSION = 1
LEGACY_LABELS_VERSION = 1

# The Frictionless Table Schema profile this table schema follows (Data Package 2.0).
TABLE_SCHEMA_PROFILE = "https://datapackage.org/profiles/2.0/tableschema.json"
# CotUdpEmitter.format_cot_timestamp, in Python's strptime spelling.
CSV_INSTANT_FORMAT = "%Y-%m-%dT%H:%M:%S.%fZ"
SUMO_X = ("SUMO x, meters east in the network's projection.")
SUMO_Y = ("SUMO y, meters north in the network's projection.")

# Every CSV column, in order: its Table Schema type, its constraints, and what it means. The names
# and order are `SumoCotBridge.CSV_COLUMNS`; a test holds the two equal.
CSV_FIELDS: dict[str, dict] = {
    "time_utc": {"type": "datetime", "format": CSV_INSTANT_FORMAT,
                 "description": "When the update happened, UTC to the millisecond: the run's epoch "
                                "plus sim_time_s."},
    "sim_time_s": {"type": "number", "constraints": {"minimum": 0},
                   "description": "SUMO simulation time, seconds."},
    "uid": {"type": "string",
            "description": "The track id: <uid prefix>-<SUMO vehicle id>. The uid prefix is "
                           "SUMO-TRUTH unless --uid-prefix sets it."},
    "callsign": {"type": "string", "description": "<base_type>-<SUMO vehicle id>."},
    "cot_type": {"type": "string", "constraints": {"pattern": "a-[a-z]-G-E-V"},
                 "description": "The CoT type: a ground vehicle, with the affiliation the run's "
                                "display convention gives the vehicle's population."},
    "how": {"type": "string", "constraints": {"enum": ["m-g"]},
            "description": "How the position was found: m-g, machine-generated truth."},
    "lat": {"type": "number", "description": "WGS84 latitude, degrees, of the front bumper's center."},
    "lon": {"type": "number", "description": "WGS84 longitude, degrees, of the front bumper's center."},
    "hae_m": {"type": "number",
              "description": "Bare-earth ground height under the vehicle, WGS84 ellipsoidal "
                             "meters, from the world package's bareearth.bin; the configured "
                             "constant when there is no grid or the vehicle is off it."},
    "ce_m": {"type": "number", "description": "Horizontal error, meters. Always 0.0 for truth."},
    "le_m": {"type": "number", "description": "Vertical error, meters. Always 0.0 for truth."},
    "course_deg": {"type": "number", "constraints": {"minimum": 0, "maximum": 360},
                   "description": "The vehicle's heading as SUMO reports it, degrees clockwise "
                                  "from north."},
    "speed_mps": {"type": "number", "constraints": {"minimum": 0},
                  "description": "Speed, meters per second."},
    "vx": {"type": "number", "description": "Velocity east, meters per second (CARLA's x)."},
    "vy": {"type": "number", "description": "Velocity south, meters per second (CARLA's y)."},
    "vz": {"type": "number", "description": "Vertical velocity. Always 0.00: SUMO is flat."},
    "base_type": {"type": "string",
                  "description": "What kind of vehicle it is: the catalogue's cot_base_type for "
                                 "the blueprint the vehicle's type names, or its SUMO vehicle "
                                 "class's when the type names none."},
    "type_id": {"type": "string", "description": "The SUMO vehicle type id."},
    "special_type": {"type": "string", "constraints": {"enum": sorted(COT_SPECIAL_TYPES)},
                     "description": "The catalogue's cot_special_type for the vehicle's blueprint: "
                                    "emergency, taxi, electric, or empty."},
    "length_m": {"type": "number", "constraints": {"minimum": 0},
                 "description": "Length SUMO gives the vehicle, meters."},
    "width_m": {"type": "number", "constraints": {"minimum": 0},
                "description": "Width SUMO gives the vehicle, meters."},
    "height_m": {"type": "number", "constraints": {"minimum": 0},
                 "description": "Height SUMO gives the vehicle, meters."},
    "color": {"type": "string", "constraints": {"pattern": "[0-9]{1,3},[0-9]{1,3},[0-9]{1,3}"},
              "description": "The vehicle type's sumo-gui color, R,G,B from 0 to 255. Not the "
                             "color CARLA renders."},
    "role_name": {"type": "string",
                  "description": "The flow the vehicle came from: its SUMO id before the last "
                                 "dot."},
    "marked": {"type": "boolean", "trueValues": ["1"], "falseValues": ["0"],
               "description": "1 when the vehicle is one the scenario planted (--marked-vehicle "
                              "or a legacy labels file's marked_ids), 0 otherwise."},
    "edge": {"type": "string", "description": "The SUMO edge the vehicle is on."},
    "lane": {"type": "string", "description": "The SUMO lane the vehicle is on."},
    "sumo_x": {"type": "number", "description": SUMO_X},
    "sumo_y": {"type": "number", "description": SUMO_Y},
    "carla_x": {"type": "number", "description": "CARLA x, meters east: sumo_x."},
    "carla_y": {"type": "number", "description": "CARLA y, meters south: -sumo_y."},
}


class TelemetrySchemas:
    """The SUMO bridge's file schemas, and the legacy labels file's."""

    @classmethod
    def schemas(cls) -> dict[str, dict]:
        """Every schema this module publishes, by file name."""
        return {
            CSV_TABLE_SCHEMA: cls.csv_table(),
            CSV_SUMMARY_SCHEMA: cls.csv_summary(),
            SUPERVISION_GAPS_SCHEMA: cls.supervision_gaps(),
            LEGACY_LABELS_SCHEMA: cls.legacy_labels(),
        }

    @classmethod
    def write(cls, directory: str | Path) -> list[Path]:
        """Publish every schema into `directory`."""
        return [JsonSchemaFile.write(schema, Path(directory) / name)
                for name, schema in cls.schemas().items()]

    # -- the CSV --------------------------------------------------------------------------------

    @staticmethod
    def csv_table() -> dict:
        """The CSV's Frictionless Table Schema, its fields in `CSV_COLUMNS` order."""
        missing = [name for name in CSV_COLUMNS if name not in CSV_FIELDS]
        if missing:
            raise KeyError(f"the CSV columns {missing} have no field description")
        fields = []
        for name in CSV_COLUMNS:
            field = {"name": name, **CSV_FIELDS[name]}
            field.setdefault("constraints", {})["required"] = True
            fields.append(field)
        return {
            "$schema": TABLE_SCHEMA_PROFILE,
            "$id": JsonSchemaFile.identifier("sumo_cot_telemetry", CSV_FORMAT_VERSION),
            "name": "sumo_cot_telemetry",
            "title": "SUMO bridge CoT telemetry table (carla-cot-telemetry --csv)",
            "description": "One row per vehicle per update of a SUMO run. The first line is the "
                           "header. Comma separated, UTF-8. Its format version and what made it "
                           "are in <name>.summary.json beside it. An empty cell is a value, the "
                           "empty string, never a missing one.",
            "missingValues": [],
            "fields": fields,
        }

    @staticmethod
    def csv_summary() -> dict:
        """`<name>.summary.json`, as `SumoCotBridge.write_csv_summary` writes it."""
        return {
            "$schema": DRAFT,
            "$id": JsonSchemaFile.identifier("sumo_cot_telemetry_summary", CSV_FORMAT_VERSION),
            "title": "SUMO bridge CSV summary (<name>.summary.json)",
            "description": "Written beside a carla-cot-telemetry --csv file when the CSV is "
                           "opened: the CSV's format version, its file name, its columns, and "
                           "what made it.",
            "type": "object",
            "additionalProperties": False,
            "required": ["format_version", "producer", "csv", "columns"],
            "properties": {
                "format_version": {"const": CSV_FORMAT_VERSION,
                                   "description": "The CSV's format."},
                "producer": {"$ref": f"{LOCAL_REFERENCE}producer"},
                "csv": JsonSchemaFile.described(NON_EMPTY_TEXT, "The CSV's file name, without a "
                                                                "folder."),
                "columns": {"const": list(CSV_COLUMNS),
                            "description": "The CSV's columns, in order: its header."},
            },
            "$defs": JsonSchemaFile.producer_definitions(),
        }

    # -- the legacy gap sidecar -----------------------------------------------------------------

    @staticmethod
    def supervision_gaps() -> dict:
        """`<name>.supervision.json`, as `SupervisionSidecar.write` writes it."""
        instant = {"type": "string", "pattern": UTC_MILLISECONDS}
        gap = {
            "type": "object",
            "description": "One described gap: the labels file's note with every key the author "
                           "gave it, and where its window falls on the run's clock.",
            "properties": {
                "kind": JsonSchemaFile.described(NON_EMPTY_TEXT, "What kind of gap it is, such "
                                                                 "as guard_no_show."),
                "note": JsonSchemaFile.described(NON_EMPTY_TEXT, "The author's description."),
                BEGIN_SECONDS: JsonSchemaFile.described(
                    NUMBER, "Where the window begins, seconds of simulation time."),
                END_SECONDS: JsonSchemaFile.described(
                    NUMBER, "Where the window ends, seconds of simulation time."),
                "begin_utc": JsonSchemaFile.described(
                    instant, f"{BEGIN_SECONDS} placed on the run's clock: epoch + {BEGIN_SECONDS}, "
                             "UTC to the millisecond. Present when the note gives "
                             f"{BEGIN_SECONDS}."),
                "end_utc": JsonSchemaFile.described(
                    instant, f"{END_SECONDS} placed on the run's clock. Present when the note "
                             f"gives {END_SECONDS}."),
            },
        }
        return {
            "$schema": DRAFT,
            "$id": JsonSchemaFile.identifier("supervision_gaps", SUPERVISION_GAPS_VERSION),
            "title": "Legacy supervision gaps (<name>.supervision.json beside a SUMO bridge run)",
            "description": "The supervision gaps a legacy scenario's labels file describes -- an "
                           "absence, such as a guard who never arrives, which no vehicle record "
                           "can carry -- placed on the clock of the run that wrote them. Written "
                           "beside the run's XML or CSV, never into it. A compiled scenario's "
                           "supervision is its compiled supervision plan, a different file with "
                           "the same suffix.",
            "type": "object",
            "additionalProperties": False,
            "required": ["scenario", "epoch", "source_labels", "supervision_gaps"],
            "properties": {
                "scenario": JsonSchemaFile.described(NON_EMPTY_TEXT, "The scenario: the .sumocfg "
                                                                     "file's name without "
                                                                     "extension."),
                "epoch": JsonSchemaFile.described(instant, "The UTC instant simulation time 0 was "
                                                           "stamped with."),
                "source_labels": JsonSchemaFile.nullable(
                    NON_EMPTY_TEXT, "The labels file's name, or null when none was named."),
                "supervision_gaps": {"type": "array", "items": gap,
                                     "description": "Every described gap, in the labels file's "
                                                    "order."},
            },
        }

    # -- the legacy labels file -----------------------------------------------------------------

    @staticmethod
    def legacy_labels() -> dict:
        """A legacy scenario's `*.labels.json`, as `carla-cot-telemetry` and
        `carla-check-label-leaks` read it."""
        return {
            "$schema": DRAFT,
            "$id": JsonSchemaFile.identifier("legacy_labels", LEGACY_LABELS_VERSION),
            "title": "Legacy scenario labels (*.labels.json)",
            "description": "What a scenario made before compiled scenarios planted: the planted "
                           "vehicles, an affiliation per vehicle type, and the described gaps. "
                           "Nothing writes this file any more; a compiled scenario's labels are "
                           "its supervision plan. Every key may be absent, and a reader ignores a "
                           "key it does not name.",
            "type": "object",
            "properties": {
                "marked_ids": {"type": "array", "items": NON_EMPTY_TEXT,
                               "description": "SUMO ids of the vehicles the scenario planted."},
                "affiliation_by_type": {
                    "type": "object",
                    "additionalProperties": {"enum": sorted(COT_AFFILIATIONS)},
                    "description": "A CoT affiliation letter per SUMO vehicle type. u marks an "
                                   "anomaly type; a reader withholds those and uses the rest only "
                                   "when the run has no display convention."},
                LABELS_KEY: {"type": "array", "items": {"type": "object"},
                             "description": "The described gaps, each an object with the "
                                            "author's own keys; begin_s and end_s, when given, "
                                            "are seconds of simulation time."},
            },
        }
