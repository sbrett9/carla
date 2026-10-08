"""Checks world packages and the vehicle catalogue against their published schemas, whole.

Where a reader checks only what it reads, this checks a file as its schema and its format page
describe it, for someone who holds one and wants to know it is sound before using it:

* a **world package** (`<name>.cwp`): it holds the entries `WorldPackageSchemas.ENTRIES` lists and no
  other, every one stored; each JSON entry keeps its schema and declares a version this release reads;
  `bareearth.bin` is there exactly when `world.json` says the world is draped, its header repeats the
  manifest, its length is its header and two whole grids, and the grids hash to the digests the
  manifest records; `map.tll.xml` is there exactly when netconvert read a ramp meter program; the
  network, the place index and the area table name the network the manifest records; the area table
  names the digest of the areas file beside it;
* the **vehicle catalogue** folder (`vehicles.catalogue.json` and what is beside it): the catalogue
  keeps its schema and `VehicleCatalogueValidator`'s rules, and its digest is its content's;
  `vehicles.vtypes.rou.xml` keeps `vehicle_types.xsd`, draws only the types it holds, and declares the
  types `SumoVehicleTypeWriter` writes from that catalogue; `vehicle_body_widths.json`, where it is
  there, keeps its schema.

Given a folder, it checks every world package and every catalogue under it.
"""
from __future__ import annotations

import hashlib
import json
import struct
import zipfile
from collections import Counter
from dataclasses import dataclass, field
from pathlib import Path

from lxml import etree

from carlacontrol.FormatVersion import FormatVersion, FormatVersionError
from carlacontrol.JsonSchemaFile import JsonSchemaFile
from carlacontrol.NetworkFingerprint import NetworkFingerprint
from carlacontrol.SumoVehicleTypeWriter import SumoVehicleTypeWriter
from carlacontrol.VehicleCatalogue import VehicleCatalogue
from carlacontrol.VehicleCatalogueSchemas import VehicleCatalogueSchemas
from carlacontrol.VehicleCatalogueValidator import VehicleCatalogueValidator
from carlacontrol.WorldPackageSchemas import (
    BARE_EARTH_HEADER,
    BARE_EARTH_HEADER_BYTES,
    BARE_EARTH_MAGIC,
    ENTRIES,
    MANIFEST_FORMAT_VERSION,
    WorldPackageSchemas,
)

WORLD_PACKAGES = "world packages"
CATALOGUES = "vehicle catalogues"
KINDS = (WORLD_PACKAGES, CATALOGUES)

PACKAGE_SUFFIX = ".cwp"
CATALOGUE_FILE = "vehicles.catalogue.json"
VEHICLE_TYPES_FILE = "vehicles.vtypes.rou.xml"
BODY_WIDTHS_FILE = "vehicle_body_widths.json"
VEHICLE_TYPES_SCHEMA = "vehicle_types.xsd"

# The version field each reference-set entry declares; the reader reads exactly the schema's version.
REFERENCE_VERSIONS = {"places.json": "place_index_version", "solar.json": "solar_frame_version",
                      "areas.resolved.json": "resolved_version"}
# How many of one file's schema departures are reported; the rest are counted in the last.
SHOWN_PER_FILE = 20


@dataclass(frozen=True)
class WorldFileFailure:
    """One way one file departs from its schema or its format page."""

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
class WorldFileValidation:
    """How many files of each kind were checked and failed, and every failure."""

    root: Path
    checked: Counter = field(default_factory=Counter)
    failed_files: Counter = field(default_factory=Counter)
    failures: list[WorldFileFailure] = field(default_factory=list)

    @property
    def ok(self) -> bool:
        """Whether every file checked keeps its schema."""
        return not self.failures

    def fail(self, kind: str, path: Path, where: str, message: str) -> None:
        self.failures.append(WorldFileFailure(kind, path, where, message))


class WorldFileValidator:
    """Checks world packages and vehicle catalogue folders against the published schemas."""

    def __init__(self, schema_directory: str | Path) -> None:
        """
        Args:
            schema_directory: the published schemas: the checkout's `CarlaControl/schemas/`, or the
                copies installed with the package. The JSON schemas are this package's own; the folder
                supplies `vehicle_types.xsd`.
        """
        self.schema_directory = Path(schema_directory)
        self._vehicle_types: etree.XMLSchema | None = None

    def validate(self, path: str | Path) -> WorldFileValidation:
        """Every world package and catalogue at `path`: a `.cwp`, a catalogue file or its folder, or a
        folder holding any of them."""
        path = Path(path)
        result = WorldFileValidation(path)
        if path.is_file() and path.suffix == PACKAGE_SUFFIX:
            self._counted(WORLD_PACKAGES, result, lambda: self._package(path, result))
        elif path.is_file() and path.name == CATALOGUE_FILE:
            self._counted(CATALOGUES, result, lambda: self._catalogue(path.parent, result))
        elif path.is_dir():
            for package in sorted(path.rglob("*" + PACKAGE_SUFFIX)):
                self._counted(WORLD_PACKAGES, result, lambda p=package: self._package(p, result))
            for catalogue in sorted(path.rglob(CATALOGUE_FILE)):
                self._counted(CATALOGUES, result, lambda c=catalogue: self._catalogue(c.parent, result))
        return result

    @staticmethod
    def _counted(kind: str, result: WorldFileValidation, check) -> None:
        before = len(result.failures)
        check()
        result.checked[kind] += 1
        if len(result.failures) > before:
            result.failed_files[kind] += 1

    @staticmethod
    def _schema_failures(result: WorldFileValidation, kind: str, path: Path, where: str,
                         problems: list[str]) -> None:
        for problem in problems[:SHOWN_PER_FILE]:
            result.fail(kind, path, where, problem)
        if len(problems) > SHOWN_PER_FILE:
            result.fail(kind, path, where, f"and {len(problems) - SHOWN_PER_FILE} more departures")

    # -- a world package ------------------------------------------------------------------------

    def _package(self, path: Path, result: WorldFileValidation) -> None:
        kind = WORLD_PACKAGES
        try:
            archive = zipfile.ZipFile(path)
        except (OSError, zipfile.BadZipFile) as broken:
            result.fail(kind, path, "", f"is not a readable zip: {broken}")
            return
        with archive:
            infos = {info.filename: info for info in archive.infolist()}
            listed = {entry.name: entry for entry in ENTRIES}
            for name in sorted(set(infos) - set(listed)):
                result.fail(kind, path, name, "is not an entry a world package holds")
            for name in sorted(n for n, entry in listed.items() if entry.required and n not in infos):
                result.fail(kind, path, name, "is missing, and every world package holds it")
            for name, info in sorted(infos.items()):
                if info.compress_type != zipfile.ZIP_STORED:
                    result.fail(kind, path, name, "is compressed; every entry is stored, which the "
                                                  "Unreal importer needs")
            documents = {}
            for name in sorted(n for n in infos if listed.get(n) and listed[n].schema):
                try:
                    documents[name] = json.loads(archive.read(name).decode("utf-8"))
                except (UnicodeDecodeError, json.JSONDecodeError) as broken:
                    result.fail(kind, path, name, f"is not readable JSON: {broken}")
            manifest = documents.get("world.json")
            if manifest is not None and not self._versions(path, documents, result):
                return
            for name, document in documents.items():
                self._schema_failures(result, kind, path, name, JsonSchemaFile.problems(
                    document, WorldPackageSchemas.entry_schema(name)))
            if not isinstance(manifest, dict):
                return
            self._grid(path, archive, infos, manifest, result)
            argv = manifest.get("NetconvertArgv") or []
            if ("map.tll.xml" in infos) != ("<tllogic-files>" in argv):
                result.fail(kind, path, "map.tll.xml",
                            "is present without netconvert having read a ramp meter program"
                            if "map.tll.xml" in infos else
                            "is missing, and netconvert read a ramp meter program (<tllogic-files>)")
            self._fingerprints(path, archive, infos, manifest, documents, result)

    @staticmethod
    def _versions(path: Path, documents: dict, result: WorldFileValidation) -> bool:
        """Hold each JSON entry to the version rule; False when the manifest is of a newer format."""
        try:
            FormatVersion.check(f"{path} (world.json)", "FormatVersion",
                                documents["world.json"].get("FormatVersion"), MANIFEST_FORMAT_VERSION)
        except (FormatVersionError, AttributeError) as refused:
            result.fail(WORLD_PACKAGES, path, "world.json", str(refused))
            return False
        for name, version_field in REFERENCE_VERSIONS.items():
            document = documents.get(name)
            if not isinstance(document, dict):
                continue
            expected = WorldPackageSchemas.entry_schema(name)["properties"][version_field]["const"]
            if document.get(version_field) != expected:
                result.fail(WORLD_PACKAGES, path, name,
                            f"declares {version_field} {document.get(version_field)!r}; this release "
                            f"reads {expected} only")
        return True

    @staticmethod
    def _grid(path: Path, archive: zipfile.ZipFile, infos: dict, manifest: dict,
              result: WorldFileValidation) -> None:
        kind, name = WORLD_PACKAGES, "bareearth.bin"
        draped = manifest.get("DrapeActive") is True
        if (name in infos) != draped:
            result.fail(kind, path, name, "is present on a world that is not draped" if name in infos
                        else "is missing, and world.json says the world is draped")
            return
        if not draped:
            return
        raw = archive.read(name)
        if len(raw) < BARE_EARTH_HEADER_BYTES:
            result.fail(kind, path, name, f"is {len(raw)} bytes, shorter than its 60-byte header")
            return
        magic, latitude, longitude, height, min_x, min_y, cell, columns, rows = struct.unpack_from(
            BARE_EARTH_HEADER, raw, 0)
        if magic != BARE_EARTH_MAGIC:
            result.fail(kind, path, name, f"opens with {raw[:4]!r}, not the magic b'1PWC'")
            return
        header = {"OriginLatitude": latitude, "OriginLongitude": longitude,
                  "OriginHeightMeters": height, "GridMinXMeters": min_x, "GridMinYMeters": min_y,
                  "GridCellSizeMeters": cell, "GridNumCols": columns, "GridNumRows": rows}
        for key, value in header.items():
            if manifest.get(key) != value:
                result.fail(kind, path, name, f"says {key} {value!r} where world.json says "
                                              f"{manifest.get(key)!r}")
        if columns < 2 or rows < 2:
            result.fail(kind, path, name, f"is a {columns} x {rows} grid; a grid has at least 2 x 2")
            return
        plane = 4 * columns * rows
        if len(raw) != BARE_EARTH_HEADER_BYTES + 2 * plane:
            result.fail(kind, path, name, f"is {len(raw)} bytes; a {columns} x {rows} grid is "
                                          f"{BARE_EARTH_HEADER_BYTES + 2 * plane}")
            return
        start = BARE_EARTH_HEADER_BYTES
        for field_name, data in (("BareEarthOffsetSha1", raw[start:start + plane]),
                                 ("BareEarthDtmSha1", raw[start + plane:])):
            recorded = manifest.get(field_name) or ""
            if recorded and hashlib.sha1(data).hexdigest() != recorded:
                result.fail(kind, path, name, f"does not hash to {field_name} in world.json")

    @staticmethod
    def _fingerprints(path: Path, archive: zipfile.ZipFile, infos: dict, manifest: dict,
                      documents: dict, result: WorldFileValidation) -> None:
        kind = WORLD_PACKAGES
        recorded = manifest.get("NetworkFingerprint") or ""
        if recorded and "map.net.xml" in infos:
            carried = NetworkFingerprint.of_text(archive.read("map.net.xml").decode("utf-8"))
            if carried != recorded:
                result.fail(kind, path, "map.net.xml", "does not have the NetworkFingerprint "
                                                       "world.json records")
        for name in ("places.json", "areas.resolved.json"):
            document = documents.get(name)
            if isinstance(document, dict) and recorded and document.get("network_fingerprint") != recorded:
                result.fail(kind, path, name, "names a network other than the one world.json records")
        table = documents.get("areas.resolved.json")
        if isinstance(table, dict):
            source = archive.read("areas.aoi.geojson") if "areas.aoi.geojson" in infos else None
            carried = hashlib.sha256(source).hexdigest() if source is not None else ""
            if table.get("source_sha256", "") != carried:
                result.fail(kind, path, "areas.resolved.json",
                            "was resolved from an areas file other than the areas.aoi.geojson "
                            "beside it")

    # -- a vehicle catalogue folder -------------------------------------------------------------

    def _catalogue(self, folder: Path, result: WorldFileValidation) -> None:
        kind = CATALOGUES
        path = folder / CATALOGUE_FILE
        try:
            document = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, UnicodeDecodeError, json.JSONDecodeError) as broken:
            result.fail(kind, path, "", f"is not readable JSON: {broken}")
            return
        try:
            VehicleCatalogue.check_version(document, path)
        except (ValueError, AttributeError) as refused:
            result.fail(kind, path, "", str(refused))
            return
        problems = JsonSchemaFile.problems(document, VehicleCatalogueSchemas.catalogue())
        self._schema_failures(result, kind, path, "", problems)
        if problems:
            return
        for rule in VehicleCatalogueValidator(document).failures():
            result.fail(kind, path, "", rule)
        if VehicleCatalogue.digest_of(document) != document["catalogue_digest"]:
            result.fail(kind, path, "catalogue_digest", "is not the digest of the catalogue's content")
        self._vehicle_type_file(folder / VEHICLE_TYPES_FILE, document, result)
        widths = folder / BODY_WIDTHS_FILE
        if widths.is_file():
            try:
                table = json.loads(widths.read_text(encoding="utf-8"))
            except (OSError, UnicodeDecodeError, json.JSONDecodeError) as broken:
                result.fail(kind, widths, "", f"is not readable JSON: {broken}")
            else:
                self._schema_failures(result, kind, widths, "", JsonSchemaFile.problems(
                    table, VehicleCatalogueSchemas.body_widths()))

    def _vehicle_type_file(self, path: Path, document: dict, result: WorldFileValidation) -> None:
        kind = CATALOGUES
        if not path.is_file():
            result.fail(kind, path, "", "is missing beside the catalogue it is written from")
            return
        try:
            written = etree.parse(str(path))
        except etree.XMLSyntaxError as broken:
            result.fail(kind, path, "", f"is not well-formed XML: {broken}")
            return
        schema = self._vehicle_types_schema()
        if not schema.validate(written):
            for error in list(schema.error_log)[:SHOWN_PER_FILE]:
                result.fail(kind, path, f"line {error.line}", error.message)
            return
        root = written.getroot()
        held = {element.get("id") for element in root.findall("vType")}
        for distribution in root.findall("vTypeDistribution"):
            missing = sorted(set(distribution.get("vTypes").split()) - held)
            if missing:
                result.fail(kind, path, f"vTypeDistribution {distribution.get('id')}",
                            f"draws {', '.join(missing)}, which the file does not hold")
        expected = etree.fromstring(SumoVehicleTypeWriter(document).to_xml().encode("utf-8"))
        if self._declarations(root) != self._declarations(expected):
            result.fail(kind, path, "", f"does not declare the types {CATALOGUE_FILE} gives; write it "
                                        "again from the catalogue")

    @staticmethod
    def _declarations(root: etree._Element) -> list[tuple]:
        """Every type and distribution with its attributes and parameters, comments left out."""
        return [(element.tag, tuple(element.attrib.items()),
                 tuple(tuple(param.attrib.items()) for param in element.findall("param")))
                for element in root if isinstance(element.tag, str)]

    def _vehicle_types_schema(self) -> etree.XMLSchema:
        if self._vehicle_types is None:
            self._vehicle_types = etree.XMLSchema(
                etree.parse(str(self.schema_directory / VEHICLE_TYPES_SCHEMA)))
        return self._vehicle_types
