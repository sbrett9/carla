"""The schemas of a world package (`<name>.cwp`): what the zip holds, and the shape of each JSON entry.

A world package is a zip whose entries are all stored, never compressed, because the Unreal importer
reads stored entries only. `ENTRIES` lists every entry a package may hold, which are required, and when
the optional ones are present. The JSON entries each have a JSON Schema here, published to
`CarlaControl/schemas/` by `write` and held equal to this module by a test:

  * `world.json`, written by `CarlaNet.Map.WorldPackage.Write` (C#, PascalCase keys);
  * `places.json`, written by `PlaceIndex`;
  * `solar.json`, written by `SolarFrame`;
  * `areas.resolved.json`, written by `AreaOfInterestResolver`.

`bareearth.bin` is binary, and its layout is `BARE_EARTH_HEADER` here and the format page in
`Docs/CAT_Research/Schemas/BareEarthGrid.md`. The OpenDRIVE and SUMO entries are external standards
and have no schema of ours. `areas.aoi.geojson` is the author's input file copied byte for byte; its
schema is the areas-of-interest input's, `area_of_interest.schema.json`.

`WorldFileValidator` checks a whole package against all of this.

`WorldPackageReader` checks each JSON entry it reads against these schemas. It checks `world.json`
without insisting on the fields CarlaNet requires, because it reads only some of them and must keep
reading packages that carry fewer; every field a manifest does carry must be of the shape given here.
"""
from __future__ import annotations

import struct
from dataclasses import dataclass
from pathlib import Path

from carlacontrol.AreaOfInterestResolver import RESOLVED_VERSION
from carlacontrol.AreaOfInterestSource import AREA_ID_PATTERN, GEOMETRY_TYPES
from carlacontrol.JsonSchemaFile import (
    DRAFT,
    LOCAL_REFERENCE,
    NON_EMPTY_TEXT,
    NON_NEGATIVE_INTEGER,
    NUMBER,
    SHA1_HEX_OR_EMPTY,
    SHA256_HEX,
    SHA256_HEX_OR_EMPTY,
    TEXT,
    JsonSchemaFile,
)
from carlacontrol.PlaceIndex import CARDINALS, PLACE_INDEX_VERSION
from carlacontrol.SolarFrame import SOLAR_FRAME_VERSION

# The format of `world.json` this package reads: `FormatVersion` in the manifest, as
# `CarlaNet.Map.WorldPackage.FormatVersion` writes it. A manifest that declares none is version 1.
MANIFEST_FORMAT_VERSION = 1

MANIFEST_SCHEMA = "world_package_manifest.schema.json"
PLACE_INDEX_SCHEMA = "place_index.schema.json"
SOLAR_FRAME_SCHEMA = "solar_frame.schema.json"
AREAS_RESOLVED_SCHEMA = "areas_resolved.schema.json"

# The modes `run_SCTMV.py --height-align` offers, which the world build records as it was given.
HEIGHT_ALIGN_MODES = ("none", "area", "origin", "drape")

# The bare-earth grid entry: a little-endian header of one int32 magic number, six float64 values
# and two int32 counts, 60 bytes, then two float32 planes of columns x rows values each.
BARE_EARTH_HEADER = "<iddddddii"
BARE_EARTH_HEADER_BYTES = struct.calcsize(BARE_EARTH_HEADER)
# "CWP1" read as a little-endian int32, so the file's first four bytes are the ASCII "1PWC".
BARE_EARTH_MAGIC = 0x43575031
BARE_EARTH_HEADER_FIELDS = ("magic", "origin_latitude_deg", "origin_longitude_deg",
                            "origin_height_m", "grid_min_x_m", "grid_min_y_m", "cell_size_m",
                            "columns", "rows")


@dataclass(frozen=True)
class PackageEntry:
    """One entry a world package may hold."""

    name: str
    format: str
    required: bool
    present_when: str
    written_by: str
    schema: str | None = None


ENTRIES: tuple[PackageEntry, ...] = (
    PackageEntry("world.json", "JSON", True, "always", "CarlaNet.Map.WorldPackage", MANIFEST_SCHEMA),
    PackageEntry("map.xodr", "ASAM OpenDRIVE 1.4 XML", True, "always", "CarlaNet.Map.WorldPackage"),
    PackageEntry("map.net.xml", "SUMO network XML", False,
                 "every package written since the network was carried; a scenario cannot be built "
                 "against a package without it", "CarlaNet.Map.WorldPackage"),
    PackageEntry("bareearth.bin", "binary, see BareEarthGrid.md", False,
                 "when DrapeActive is true, and only then", "CarlaNet.Map.WorldPackage"),
    PackageEntry("map.tll.xml", "SUMO traffic light program XML", False,
                 "when the OpenStreetMap extract has ramp meters", "CarlaNet.Map.WorldPackage"),
    PackageEntry("areas.resolved.json", "JSON", False,
                 "once the authoring reference set is published, unless the areas were refused",
                 "carlacontrol.AuthoringReferenceSet", AREAS_RESOLVED_SCHEMA),
    PackageEntry("areas.aoi.geojson", "GeoJSON (RFC 7946), area_of_interest.schema.json", False,
                 "when areas of interest were declared and published", "carlacontrol.AuthoringReferenceSet"),
    PackageEntry("places.json", "JSON", False, "once the authoring reference set is published",
                 "carlacontrol.AuthoringReferenceSet", PLACE_INDEX_SCHEMA),
    PackageEntry("solar.json", "JSON", False, "once the authoring reference set is published",
                 "carlacontrol.AuthoringReferenceSet", SOLAR_FRAME_SCHEMA),
)

_EXTENT = JsonSchemaFile.numbers(4, "A rectangle in CARLA meters: four numbers, min x, min y, max x, "
                                    "max y. CARLA's frame has x east and y south.")
_STRINGS = {"type": "array", "items": {"type": "string"}}
_NON_EMPTY_STRINGS = {"type": "array", "items": NON_EMPTY_TEXT}


class WorldPackageSchemas:
    """The world package's entry schemas, as published and as its reader checks them."""

    @classmethod
    def schemas(cls) -> dict[str, dict]:
        """Every schema this module publishes, by file name."""
        return {
            MANIFEST_SCHEMA: cls.manifest(),
            PLACE_INDEX_SCHEMA: cls.place_index(),
            SOLAR_FRAME_SCHEMA: cls.solar_frame(),
            AREAS_RESOLVED_SCHEMA: cls.areas_resolved(),
        }

    @classmethod
    def write(cls, directory: str | Path) -> list[Path]:
        """Publish every schema into `directory`."""
        return [JsonSchemaFile.write(schema, Path(directory) / name)
                for name, schema in cls.schemas().items()]

    @classmethod
    def entry_schema(cls, entry: str) -> dict:
        """The schema a JSON entry of the package is checked against."""
        named = {item.name: item.schema for item in ENTRIES}
        if not named.get(entry):
            raise KeyError(f"a world package's {entry} has no JSON schema")
        return cls.schemas()[named[entry]]

    # -- world.json -----------------------------------------------------------------------------

    @staticmethod
    def manifest() -> dict:
        """`world.json`, as `CarlaNet.Map.WorldPackage.Write` writes it."""
        meters = {"type": "number"}
        return {
            "$schema": DRAFT,
            "$id": JsonSchemaFile.identifier("world_package_manifest", MANIFEST_FORMAT_VERSION),
            "title": "World package manifest (world.json in a .cwp)",
            "description": "What a generated world is: its geographic origin, how its roads were "
                           "seated on the photoreal imagery, the imagery layers, the traffic "
                           "staging rectangle, and how it was built. Keys are PascalCase. CARLA's "
                           "frame has x east, y south and z up, in meters. Heights are WGS84 "
                           "ellipsoidal heights in meters.",
            "type": "object",
            "additionalProperties": False,
            "required": ["MapName", "OriginLatitude", "OriginLongitude", "OriginHeightMeters",
                         "HeightAlignMode", "DrapeActive", "HeightAlignOffsetMeters"],
            "properties": {
                "FormatVersion": {"const": MANIFEST_FORMAT_VERSION,
                                  "description": "The format of this file. A manifest without it "
                                                 "is version 1."},
                "Producer": JsonSchemaFile.nullable(
                    {"$ref": f"{LOCAL_REFERENCE}producer"},
                    "What made the package. Absent from a package written before it was recorded."),
                "MapName": {"type": "string", "minLength": 1,
                            "description": "The world's name. The package file is <MapName>.cwp."},
                "OriginLatitude": JsonSchemaFile.described(
                    NUMBER, "WGS84 latitude, degrees, of CARLA's (0, 0)."),
                "OriginLongitude": JsonSchemaFile.described(
                    NUMBER, "WGS84 longitude, degrees, of CARLA's (0, 0)."),
                "OriginHeightMeters": JsonSchemaFile.described(
                    meters, "Ellipsoidal height, meters, that CARLA's z = 0 stands for."),
                "GeoReferenceString": JsonSchemaFile.described(
                    TEXT, "The OpenDRIVE geoReference projection, a PROJ string, copied verbatim "
                          "from map.xodr. Empty when the OpenDRIVE carries none."),
                "HeightAlignMode": {"enum": list(HEIGHT_ALIGN_MODES),
                                    "description": "How roads were seated on the photoreal "
                                                   "imagery: none, area, origin or drape."},
                "DrapeActive": {"type": "boolean",
                                "description": "True when the drivable surface was fitted to the "
                                               "imagery cell by cell, and bareearth.bin holds the "
                                               "grids."},
                "HeightAlignOffsetMeters": JsonSchemaFile.described(
                    meters, "The one height, meters, added to the road surface by the area and "
                            "origin modes. 0 for none and for drape."),
                "GridMinXMeters": JsonSchemaFile.described(
                    meters, "CARLA x, meters, of the grid's first column. 0 when there is no grid."),
                "GridMinYMeters": JsonSchemaFile.described(
                    meters, "CARLA y, meters, of the grid's first row. 0 when there is no grid."),
                "GridCellSizeMeters": JsonSchemaFile.described(
                    meters, "Spacing, meters, between grid points. 0 when there is no grid."),
                "GridNumCols": JsonSchemaFile.described(
                    NON_NEGATIVE_INTEGER, "Grid points along x. 0 when there is no grid."),
                "GridNumRows": JsonSchemaFile.described(
                    NON_NEGATIVE_INTEGER, "Grid points along y. 0 when there is no grid."),
                "BareEarthOffsetSha1": {"type": "string", "pattern": SHA1_HEX_OR_EMPTY,
                                        "description": "SHA-1, lowercase hexadecimal, of the "
                                                       "offset plane's bytes in bareearth.bin. "
                                                       "Empty when there is no grid, and on older "
                                                       "packages."},
                "BareEarthDtmSha1": {"type": "string", "pattern": SHA1_HEX_OR_EMPTY,
                                     "description": "SHA-1, lowercase hexadecimal, of the ground "
                                                    "height plane's bytes in bareearth.bin. Empty "
                                                    "when there is no grid, and on older packages."},
                "PhotorealIonAssetId": JsonSchemaFile.described(
                    {"type": "integer"}, "Cesium ion asset id of the photoreal imagery tileset."),
                "GroundIonAssetId": JsonSchemaFile.described(
                    {"type": "integer"}, "Cesium ion asset id of the bare-earth terrain the road "
                                         "heights were taken from. 1 is Cesium World Terrain; 0 "
                                         "means heights came from the photoreal surface."),
                "StagingMinXMeters": JsonSchemaFile.described(
                    meters, "CARLA x, meters, of the staging rectangle's west edge."),
                "StagingMinYMeters": JsonSchemaFile.described(
                    meters, "CARLA y, meters, of the staging rectangle's north edge."),
                "StagingMaxXMeters": JsonSchemaFile.described(
                    meters, "CARLA x, meters, of the staging rectangle's east edge."),
                "StagingMaxYMeters": JsonSchemaFile.described(
                    meters, "CARLA y, meters, of the staging rectangle's south edge."),
                "StagingMarginMeters": JsonSchemaFile.described(
                    meters, "Width, meters, of the ring inside the staging rectangle where traffic "
                            "enters and leaves."),
                "SourceOsmFileName": JsonSchemaFile.described(
                    TEXT, "File name of the OpenStreetMap extract the world was built from."),
                "SourceOsmSha256": {"type": "string", "pattern": SHA256_HEX_OR_EMPTY,
                                    "description": "Fingerprint of that extract: SHA-256 over its "
                                                   "parsed content, not its bytes. Older packages "
                                                   "hold a digest of the bytes instead."},
                "OpenDriveSha256": {"type": "string", "pattern": SHA256_HEX_OR_EMPTY,
                                    "description": "SHA-256 of map.xodr from its root element on, "
                                                   "with the header's date blanked."},
                "NetworkFingerprint": {"type": "string", "pattern": SHA256_HEX_OR_EMPTY,
                                       "description": "Fingerprint of map.net.xml: SHA-256 over "
                                                      "its parsed content. Empty on a package "
                                                      "without a network."},
                "NetconvertArgv": JsonSchemaFile.described(
                    _STRINGS, "Every argument netconvert was given, in order. Its output files "
                              "are named <opendrive-output>, <output-file> and <tllogic-files>."),
                "NetconvertPath": JsonSchemaFile.described(
                    TEXT, "The netconvert executable that ran. Empty on older packages."),
                "NetconvertVersion": JsonSchemaFile.described(
                    TEXT, "What that netconvert reported as its version, such as Eclipse SUMO "
                          "netconvert 1.27.0. Empty on older packages."),
                "SampleStepMeters": JsonSchemaFile.described(
                    meters, "Spacing, meters, of the road reference-line height samples."),
                "TerrainResolutionMeters": JsonSchemaFile.described(
                    meters, "Spacing, meters, of the drivable surface points in drape mode."),
                "TerrainMarginMeters": JsonSchemaFile.described(
                    meters, "The staging margin the build was asked for, meters."),
                "GeneratedAtUtc": JsonSchemaFile.described(
                    TEXT, "When the package was written, ISO 8601 UTC."),
                "GeneratorVersion": JsonSchemaFile.described(
                    TEXT, "Assembly version of the CarlaNet client that wrote the package."),
                "NetconvertExtraArgs": JsonSchemaFile.described(
                    _STRINGS, "The arguments, among NetconvertArgv, that the caller added to the "
                              "standard set."),
            },
            "$defs": JsonSchemaFile.producer_definitions(),
        }

    # -- places.json ----------------------------------------------------------------------------

    @staticmethod
    def place_index() -> dict:
        """`places.json`, as `PlaceIndex.to_dict` writes it."""
        edge = {
            "type": "object", "additionalProperties": False,
            "required": ["edge_id", "from_junction", "to_junction", "bearing_deg", "direction",
                         "length_m", "lane_count", "lane_ids", "speed_mps", "extent_carla_m"],
            "properties": {
                "edge_id": JsonSchemaFile.described(NON_EMPTY_TEXT, "The SUMO edge id."),
                "from_junction": JsonSchemaFile.described(TEXT, "The SUMO junction it leaves."),
                "to_junction": JsonSchemaFile.described(TEXT, "The SUMO junction it reaches."),
                "bearing_deg": {"type": "number", "minimum": 0,
                                "description": "Compass bearing, degrees clockwise from grid "
                                               "north, of the rightmost lane's first point to its "
                                               "last, from 0 up to but not including 360."},
                "direction": {"enum": list(CARDINALS),
                              "description": "The quarter of the compass the bearing falls in."},
                "length_m": JsonSchemaFile.described(NUMBER, "SUMO length of the rightmost lane, "
                                                             "meters."),
                "lane_count": {"type": "integer", "minimum": 1, "description": "Lanes on the edge."},
                "lane_ids": {"type": "array", "minItems": 1, "items": NON_EMPTY_TEXT,
                             "description": "The SUMO lane ids, rightmost first."},
                "speed_mps": JsonSchemaFile.described(NUMBER, "Speed limit of the rightmost lane, "
                                                              "meters per second."),
                "extent_carla_m": _EXTENT,
            },
        }
        directions = {"type": "object", "additionalProperties": False,
                      "description": "The edges heading each way, each list in the order a vehicle "
                                     "traveling that way meets them. A direction with no edge is "
                                     "left out.",
                      "properties": {name: _NON_EMPTY_STRINGS for name in CARDINALS}}
        street = {
            "type": "object", "additionalProperties": False,
            "required": ["name", "scripts", "edge_count", "length_m", "extent_carla_m",
                         "directions", "edges"],
            "properties": {
                "name": JsonSchemaFile.described(NON_EMPTY_TEXT, "The street name the edges "
                                                                 "carry."),
                "scripts": JsonSchemaFile.described(_STRINGS, "The writing systems of the name's "
                                                              "letters, by Unicode name: LATIN, "
                                                              "ARABIC and so on."),
                "edge_count": {"type": "integer", "minimum": 1,
                               "description": "Edges carrying the name."},
                "length_m": JsonSchemaFile.described(NUMBER, "Their lengths added up, meters."),
                "extent_carla_m": _EXTENT,
                "directions": directions,
                "edges": {"type": "array", "minItems": 1, "items": edge,
                          "description": "Every edge carrying the name, ordered by edge id."},
            },
        }
        return {
            "$schema": DRAFT,
            "$id": JsonSchemaFile.identifier("place_index", PLACE_INDEX_VERSION),
            "title": "Place index (places.json in a .cwp)",
            "description": "Which edges of the world's SUMO network carry which street name, and "
                           "which way they head. Derived from map.net.xml alone.",
            "type": "object",
            "additionalProperties": False,
            "required": ["place_index_version", "network_fingerprint", "coverage", "warnings",
                         "streets"],
            "properties": {
                "place_index_version": {"const": PLACE_INDEX_VERSION,
                                        "description": "The format of this file."},
                "network_fingerprint": {"type": "string", "pattern": SHA256_HEX,
                                        "description": "Fingerprint of the network the index was "
                                                       "made from."},
                "coverage": {
                    "type": "object", "additionalProperties": False,
                    "description": "How much of the network the names cover.",
                    "required": ["normal_edges", "named_edges", "named_fraction", "distinct_names",
                                 "names_by_script", "largest_name", "warn_below_named_fraction"],
                    "properties": {
                        "normal_edges": JsonSchemaFile.described(
                            NON_NEGATIVE_INTEGER, "Edges that are not inside a junction."),
                        "named_edges": JsonSchemaFile.described(
                            NON_NEGATIVE_INTEGER, "Of those, the edges with a street name."),
                        "named_fraction": {"type": "number", "minimum": 0,
                                           "description": "named_edges / normal_edges, to four "
                                                          "decimals."},
                        "distinct_names": JsonSchemaFile.described(NON_NEGATIVE_INTEGER,
                                                                   "Different names."),
                        "names_by_script": {"type": "object",
                                            "additionalProperties": NON_NEGATIVE_INTEGER,
                                            "description": "Names per writing system. NONE "
                                                           "counts names with no letters."},
                        "largest_name": JsonSchemaFile.nullable(
                            {"type": "object", "additionalProperties": False,
                             "required": ["name", "edge_count"],
                             "properties": {"name": NON_EMPTY_TEXT,
                                            "edge_count": {"type": "integer", "minimum": 1}}},
                            "The name carried by the most edges. Null when no edge is named."),
                        "warn_below_named_fraction": JsonSchemaFile.described(
                            NUMBER, "Below this named_fraction the index warns that names "
                                    "contribute little."),
                    },
                },
                "warnings": JsonSchemaFile.described(_STRINGS, "What the index warned about when "
                                                               "it was built."),
                "streets": {"type": "array", "items": street,
                            "description": "One entry per street name, ordered by name."},
            },
        }

    # -- solar.json -----------------------------------------------------------------------------

    @staticmethod
    def solar_frame() -> dict:
        """`solar.json`, as `SolarFrame.to_dict` writes it."""
        return {
            "$schema": DRAFT,
            "$id": JsonSchemaFile.identifier("solar_frame", SOLAR_FRAME_VERSION),
            "title": "Solar frame (solar.json in a .cwp)",
            "description": "The facts about the world's sun that a scenario's start time is checked "
                           "against without a server: the origin the engine computes the sun from, "
                           "and the time zone the engine sets from its longitude.",
            "type": "object",
            "additionalProperties": False,
            "required": ["solar_frame_version", "origin_latitude", "origin_longitude",
                         "engine_time_zone_hours", "engine_time_zone", "engine_time_zone_rule",
                         "engine_daylight_saving", "engine_solar_time_at_configure_hours"],
            "properties": {
                "solar_frame_version": {"const": SOLAR_FRAME_VERSION,
                                        "description": "The format of this file."},
                "origin_latitude": {"type": "number", "minimum": -90,
                                    "description": "WGS84 latitude, degrees, of the world's "
                                                   "origin."},
                "origin_longitude": {"type": "number", "minimum": -180,
                                     "description": "WGS84 longitude, degrees, of the world's "
                                                    "origin."},
                "engine_time_zone_hours": JsonSchemaFile.described(
                    NUMBER, "The zone the engine sets, hours east of UTC: origin_longitude / 15. "
                            "Local mean solar time, not the site's civil zone."),
                "engine_time_zone": {"type": "string", "pattern": r"^[+-][0-9]{2}:[0-9]{2}:[0-9]{2}$",
                                     "description": "The same zone as a signed offset, "
                                                    "hours:minutes:seconds."},
                "engine_time_zone_rule": JsonSchemaFile.described(
                    NON_EMPTY_TEXT, "The rule in words."),
                "engine_daylight_saving": JsonSchemaFile.described(
                    {"type": "boolean"}, "Whether the engine applies daylight saving. It does not."),
                "engine_solar_time_at_configure_hours": JsonSchemaFile.described(
                    NUMBER, "The solar time, hours, the engine sets when the world's georeference "
                            "is configured."),
            },
        }

    # -- areas.resolved.json --------------------------------------------------------------------

    @staticmethod
    def areas_resolved() -> dict:
        """`areas.resolved.json`, as `AreaOfInterestResolver` writes it."""
        point = JsonSchemaFile.numbers(2, "A CARLA position: two numbers, x and y, meters.")
        ring = {"type": "array", "minItems": 4, "items": point,
                "description": "A closed ring of CARLA positions, first and last the same."}
        carla_local = {
            "description": "The area in CARLA meters, rounded to the millimeter.",
            "anyOf": [
                {"type": "object", "additionalProperties": False,
                 "required": ["type", "centre", "radius_m"],
                 "properties": {"type": {"const": "Circle"}, "centre": point,
                                "radius_m": {"type": "number", "exclusiveMinimum": 0}}},
                {"type": "object", "additionalProperties": False, "required": ["type", "coordinates"],
                 "properties": {"type": {"const": "Polygon"},
                                "coordinates": {"type": "array", "minItems": 1, "items": ring}}},
                {"type": "object", "additionalProperties": False, "required": ["type", "coordinates"],
                 "properties": {"type": {"const": "MultiPolygon"},
                                "coordinates": {"type": "array", "minItems": 1,
                                                "items": {"type": "array", "minItems": 1,
                                                          "items": ring}}}},
            ],
        }
        vclasses = JsonSchemaFile.described(
            _NON_EMPTY_STRINGS, "SUMO vehicle classes allowed on the lane; all when the lane "
                                "declares no restriction.")
        lane = {
            "description": "A lane inside, crossing, or near the area.",
            "anyOf": [
                {"type": "object", "additionalProperties": False,
                 "required": ["lane_id", "edge_id", "containment", "s_begin_m", "s_end_m",
                              "intervals_m", "allowed_vclasses"],
                 "properties": {
                     "lane_id": NON_EMPTY_TEXT, "edge_id": NON_EMPTY_TEXT,
                     "containment": {"enum": ["inside", "crossing"]},
                     "s_begin_m": JsonSchemaFile.described(
                         {"type": "number", "minimum": 0},
                         "SUMO lane position, meters, where the first stretch inside begins."),
                     "s_end_m": JsonSchemaFile.described(
                         {"type": "number", "minimum": 0},
                         "SUMO lane position, meters, where the last stretch inside ends."),
                     "intervals_m": {"type": "array", "minItems": 1,
                                     "items": JsonSchemaFile.numbers(
                                         2, "Begin and end, SUMO lane positions in meters."),
                                     "description": "Every stretch of the lane inside the area."},
                     "allowed_vclasses": vclasses}},
                {"type": "object", "additionalProperties": False,
                 "required": ["lane_id", "edge_id", "containment", "distance_m", "allowed_vclasses"],
                 "properties": {
                     "lane_id": NON_EMPTY_TEXT, "edge_id": NON_EMPTY_TEXT,
                     "containment": {"const": "near"},
                     "distance_m": JsonSchemaFile.described(
                         {"type": "number", "minimum": 0},
                         "Shortest distance, meters, from the lane to the area."),
                     "allowed_vclasses": vclasses}},
            ],
        }
        area = {
            "type": "object", "additionalProperties": False,
            "required": ["id", "name", "kind", "geographic", "carla_local", "envelope_carla_m",
                         "sumo", "warnings"],
            "properties": {
                "id": {"type": "string", "pattern": f"^{AREA_ID_PATTERN.pattern}$",
                       "description": "The area's id, as the areas file declares it."},
                "name": JsonSchemaFile.described(NON_EMPTY_TEXT, "The area's name."),
                "kind": JsonSchemaFile.nullable(NON_EMPTY_TEXT, "The area's kind, or null when "
                                                                "the areas file gives none."),
                "geographic": {"type": "object",
                               "description": "The area's GeoJSON geometry, copied from the areas "
                                              "file: [longitude, latitude] in WGS84 degrees. The "
                                              "areas file's schema is "
                                              "area_of_interest.schema.json.",
                               "required": ["type", "coordinates"],
                               "properties": {"type": {"enum": list(GEOMETRY_TYPES)},
                                              "coordinates": {"type": "array"}}},
                "carla_local": carla_local,
                "envelope_carla_m": _EXTENT,
                "sumo": {"type": "object", "additionalProperties": False,
                         "required": ["edges", "lanes"],
                         "properties": {
                             "edges": {"type": "array",
                                       "items": {"type": "object", "additionalProperties": False,
                                                 "required": ["edge_id", "containment"],
                                                 "properties": {
                                                     "edge_id": NON_EMPTY_TEXT,
                                                     "containment": {"enum": ["inside", "crossing",
                                                                              "near"]}}},
                                       "description": "Each edge with a listed lane: inside when "
                                                      "every lane is, crossing when any lane is "
                                                      "inside or crossing, near otherwise."},
                             "lanes": {"type": "array", "items": lane,
                                       "description": "Ordered by edge id, then lane id."}}},
                "warnings": JsonSchemaFile.described(_STRINGS, "What resolving the area warned "
                                                               "about."),
            },
        }
        return {
            "$schema": DRAFT,
            "$id": JsonSchemaFile.identifier("areas_resolved", RESOLVED_VERSION),
            "title": "Resolved areas of interest (areas.resolved.json in a .cwp)",
            "description": "The declared areas of interest placed on the built world: in CARLA "
                           "meters, and on the SUMO lanes inside, crossing or near each one. "
                           "Always written when the reference set is published; with no areas "
                           "declared, the list is empty.",
            "type": "object",
            "additionalProperties": False,
            "required": ["resolved_version", "source_file_name", "source_sha256", "world_map_name",
                         "world_georeference", "world_origin_latitude", "world_origin_longitude",
                         "network_fingerprint", "near_m", "frame", "areas"],
            "properties": {
                "resolved_version": {"const": RESOLVED_VERSION,
                                     "description": "The format of this file."},
                "source_file_name": JsonSchemaFile.described(
                    TEXT, "The areas file's name. Empty when no areas were declared."),
                "source_sha256": {"type": "string", "pattern": SHA256_HEX_OR_EMPTY,
                                  "description": "SHA-256 of areas.aoi.geojson's bytes. Empty when "
                                                 "no areas were declared. A reader refuses the "
                                                 "table when this differs from the entry beside "
                                                 "it."},
                "world_map_name": JsonSchemaFile.described(TEXT, "MapName from world.json."),
                "world_georeference": JsonSchemaFile.described(
                    TEXT, "GeoReferenceString from world.json."),
                "world_origin_latitude": JsonSchemaFile.nullable(
                    NUMBER, "OriginLatitude from world.json, degrees."),
                "world_origin_longitude": JsonSchemaFile.nullable(
                    NUMBER, "OriginLongitude from world.json, degrees."),
                "network_fingerprint": {"type": "string", "pattern": SHA256_HEX,
                                        "description": "Fingerprint of the network the lanes are "
                                                       "from."},
                "near_m": {"type": "number", "exclusiveMinimum": 0,
                           "description": "A lane within this distance, meters, of an area is "
                                          "near it."},
                "frame": {
                    "type": "object", "additionalProperties": False,
                    "required": ["carla_from_sumo", "projected_by", "net_offset_m",
                                 "geodesy_agreement_limit_m", "geodesy_worst_residual_m"],
                    "properties": {
                        "carla_from_sumo": JsonSchemaFile.described(
                            NON_EMPTY_TEXT, "How a SUMO position becomes a CARLA one: "
                                            "carla(x, y) = sumo(x, -y)."),
                        "projected_by": JsonSchemaFile.described(
                            TEXT, "What converted the areas' degrees to SUMO meters. Empty when "
                                  "no areas were declared."),
                        "net_offset_m": JsonSchemaFile.numbers(
                            2, "The network's netOffset, two numbers in meters. 0, 0 unless the "
                               "roads were deliberately shifted."),
                        "geodesy_agreement_limit_m": JsonSchemaFile.described(
                            NUMBER, "How far apart, meters, SUMO's projection and the world's own "
                                    "may place one vertex before the table is refused."),
                        "geodesy_worst_residual_m": JsonSchemaFile.nullable(
                            NUMBER, "The largest such distance measured, meters. Null when no "
                                    "areas were declared."),
                    },
                },
                "areas": {"type": "array", "items": area,
                          "description": "One entry per declared area, in the areas file's order."},
            },
        }
