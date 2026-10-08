"""Reads a generated world's package: what it records about itself, the network it carries, and the
authoring reference set published beside that network.

A world package (`<name>.cwp`) is a zip of STORED entries. `CarlaNet.Map` writes the world itself:

  * `world.json` -- the manifest;
  * `map.xodr` -- the elevated OpenDRIVE;
  * `map.net.xml` -- the SUMO network from the same netconvert run;
  * `bareearth.bin` -- the per-cell grids, when the world was seated on a draped surface;
  * `map.tll.xml` -- the ramp meters' programme file that run read (`--tllogic-files`, recorded as
    `<tllogic-files>`), only when the extract has ramp meters.

The world build then publishes the authoring reference set into the same file
(`carlacontrol.AuthoringReferenceSet`), which a scenario author and the scenario compiler read
without a running server:

  * `areas.resolved.json` -- areas of interest resolved to CARLA metres and SUMO lanes, always
    written; with no areas declared it holds an empty list, so "no areas" and "published before
    areas existed" read differently;
  * `areas.aoi.geojson` -- the GeoJSON those areas were resolved from, when any were declared;
  * `places.json` -- the place index;
  * `solar.json` -- the solar frame.

`map.net.xml` is carried rather than rebuilt because it cannot be rebuilt. Asking netconvert for
OpenDRIVE output makes it default `rectangular-lane-cut` to true, which feeds junction shape
computation, so a second run with byte-identical flags produces a different graph -- measured on one
Arapahoe extract, 743 of 4,978 canonical rows differ and lane lengths move by up to 3.3 m while the
map's boundary matches exactly. A scenario authored against a rebuilt network binds to edges the
rendered world does not have, and nothing downstream notices.
"""
from __future__ import annotations

import hashlib
import json
import zipfile
from pathlib import Path

from carlacontrol.FormatVersion import FormatVersion

# The newest `world.json` format this reader reads: `FormatVersion` in the manifest, as
# `CarlaNet.Map.WorldPackage.FormatVersion` writes it. A package written before the manifest carried
# one reads as version 1.
MANIFEST_FORMAT_VERSION = 1

# The schema shape each reference-set entry must declare to be read. A reader never reads one in part.
IMPLEMENTED_VERSIONS = {
    "areas.resolved.json": ("resolved_version", 1),
    "places.json": ("place_index_version", 1),
    "solar.json": ("solar_frame_version", 1),
}


class WorldPackageReader:
    """One generated world's package, opened for reading."""

    MANIFEST_ENTRY = "world.json"
    NETWORK_ENTRY = "map.net.xml"
    OPENDRIVE_ENTRY = "map.xodr"
    AREAS_RESOLVED_ENTRY = "areas.resolved.json"
    AREAS_SOURCE_ENTRY = "areas.aoi.geojson"
    PLACE_INDEX_ENTRY = "places.json"
    SOLAR_FRAME_ENTRY = "solar.json"
    TRAFFIC_LIGHT_PROGRAMS_ENTRY = "map.tll.xml"

    def __init__(self, package_path: str | Path) -> None:
        self.path = Path(package_path)
        if not self.path.exists():
            raise FileNotFoundError(f"world package not found: {self.path}")
        with zipfile.ZipFile(self.path) as package:
            names = set(package.namelist())
            if self.MANIFEST_ENTRY not in names:
                raise ValueError(f"not a world package, no {self.MANIFEST_ENTRY}: {self.path}")
            self.manifest: dict = json.loads(package.read(self.MANIFEST_ENTRY))
            self._entries = names
        # A newer manifest is refused for what it is, before any field it may have moved is read.
        self.format_version = FormatVersion.check(f"{self.path} ({self.MANIFEST_ENTRY})", "FormatVersion",
                                                  self.manifest.get("FormatVersion"),
                                                  MANIFEST_FORMAT_VERSION)

    @property
    def producer(self) -> dict | None:
        """What made the package (`carlacontrol.ProducerRecord`'s shape); None on one built before it
        was recorded."""
        return self.manifest.get("Producer")

    @property
    def map_name(self) -> str:
        return self.manifest.get("MapName", "")

    @property
    def origin(self) -> tuple[float, float]:
        """The latitude and longitude pinned to the CARLA map's (0, 0)."""
        return self.manifest["OriginLatitude"], self.manifest["OriginLongitude"]

    @property
    def has_network(self) -> bool:
        return self.NETWORK_ENTRY in self._entries

    @property
    def recorded_network_fingerprint(self) -> str:
        """The fingerprint the world build recorded. Empty on a package written before it existed."""
        return self.manifest.get("NetworkFingerprint", "")

    # The output files netconvert wrote, recorded by these fixed names: they were the build's scratch
    # files, named at random, and where netconvert writes changes nothing it produces.
    RECORDED_OUTPUTS = {"--opendrive-output": "<opendrive-output>", "--output-file": "<output-file>",
                        "-o": "<output-file>"}

    # The ramp meters' programme file, a scratch file of the build too, recorded by this name from the
    # first build that wrote one, so no older package needs it renamed. Its content is
    # `traffic_light_programs()`.
    RECORDED_PROGRAMS_INPUT = "<tllogic-files>"

    @property
    def netconvert_argv(self) -> list[str]:
        """Every argument netconvert was given, its output paths by fixed names.

        Empty on a package written before it was recorded. A package written before the world build
        recorded its outputs by name carries the random scratch paths, which made every rebuild of an
        identical world differ from the last; they are named here as the build now names them.
        """
        argv = list(self.manifest.get("NetconvertArgv", []))
        for index in range(1, len(argv)):
            if argv[index - 1] in self.RECORDED_OUTPUTS:
                argv[index] = self.RECORDED_OUTPUTS[argv[index - 1]]
        return argv

    @property
    def netconvert_version(self) -> str:
        return self.manifest.get("NetconvertVersion", "")

    def network_text(self) -> str:
        """The SUMO network the world was built with."""
        if not self.has_network:
            raise ValueError(
                f"world package carries no {self.NETWORK_ENTRY}: {self.path}. It was built before "
                "the network was kept, and the network cannot be regenerated -- asking netconvert "
                "for OpenDRIVE output changes the graph it produces. Rebuild the world from its "
                "original extract.")
        with zipfile.ZipFile(self.path) as package:
            return package.read(self.NETWORK_ENTRY).decode("utf-8")

    def open_drive_text(self) -> str:
        """The elevated OpenDRIVE the CARLA world was generated from."""
        with zipfile.ZipFile(self.path) as package:
            return package.read(self.OPENDRIVE_ENTRY).decode("utf-8")

    def traffic_light_programs(self) -> str | None:
        """The ramp meters' programme file the world's netconvert run read, or None when it read none.

        The recorded invocation names it `<tllogic-files>`; this is what that file said. The network
        carries the same programmes, so this is the record of the invocation, not a second source of
        them.
        """
        raw = self.entry_bytes(self.TRAFFIC_LIGHT_PROGRAMS_ENTRY)
        return None if raw is None else raw.decode("utf-8")

    # ---- the authoring reference set --------------------------------------------------------------

    def entry_bytes(self, name: str) -> bytes | None:
        """An entry's bytes, or None when the package does not carry it."""
        if name not in self._entries:
            return None
        with zipfile.ZipFile(self.path) as package:
            return package.read(name)

    def areas_source(self) -> bytes | None:
        """The GeoJSON the areas were resolved from, byte for byte, or None when none was declared."""
        return self.entry_bytes(self.AREAS_SOURCE_ENTRY)

    def areas_of_interest(self) -> dict | None:
        """The resolved area table, or None when the package predates the reference set.

        Refuses a table whose recorded source digest does not match the GeoJSON carried beside it
        (C5 V5.11): the table would then describe areas other than the ones the package declares.
        """
        table = self._reference_entry(self.AREAS_RESOLVED_ENTRY)
        if table is None:
            return None
        source = self.areas_source()
        recorded = table.get("source_sha256", "")
        carried = hashlib.sha256(source).hexdigest() if source is not None else ""
        if recorded != carried:
            raise ValueError(
                f"{self.path}: {self.AREAS_RESOLVED_ENTRY} was resolved from a source with digest "
                f"{recorded or '(none)'}, but the package carries "
                f"{carried or 'no ' + self.AREAS_SOURCE_ENTRY}; the areas it describes are not "
                "the areas this package declares. Republish the reference set.")
        return table

    def place_index(self) -> dict | None:
        """The place index, or None when the package predates the reference set."""
        return self._reference_entry(self.PLACE_INDEX_ENTRY)

    def solar_frame(self) -> dict | None:
        """The solar frame, or None when the package predates the reference set."""
        return self._reference_entry(self.SOLAR_FRAME_ENTRY)

    def _reference_entry(self, name: str) -> dict | None:
        raw = self.entry_bytes(name)
        if raw is None:
            return None
        document = json.loads(raw.decode("utf-8"))
        field, implemented = IMPLEMENTED_VERSIONS[name]
        if document.get(field) != implemented:
            raise ValueError(f"{self.path}: {name} declares {field} {document.get(field)!r}; this "
                             f"reader implements {implemented} and does not read one in part")
        return document
