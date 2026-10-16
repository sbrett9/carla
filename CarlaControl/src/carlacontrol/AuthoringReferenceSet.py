"""Publishes a world's authoring reference set into its world package.

The reference set is what a scenario author and the scenario compiler read about a world before
writing anything, without a running server (`13_Work_Breakdown.md` stage E; `07_Scenario_Authoring.md`
§2). Three of its artifacts are built here and written into the package beside the network they are
derived from:

  * `areas.resolved.json` and `areas.aoi.geojson` -- the areas of interest, resolved to CARLA metres
    and SUMO lanes (`AreaOfInterestResolver`), and the GeoJSON they came from, byte for byte, whose
    digest the table records (C5 V5.11);
  * `places.json` -- the place index (`PlaceIndex`);
  * `solar.json` -- the solar frame (`SolarFrame`).

The vehicle catalogue belongs to the content build rather than to a world and lives in
`CarlaControl/catalogue/`; the fingerprint is already in `world.json`.

**Published in the same manner as the rest of the package.** Entries are named for their role, stored
uncompressed -- the editor's `FZipArchiveReader` reads stored entries only -- and self-describing,
each carrying its own schema version. The package is rewritten whole to `<name>.cwp.partial` and moved
into place, so a package that exists is always complete; the entries `CarlaNet.Map` wrote are copied
across byte for byte. Publishing again replaces the whole set rather than adding to it, so an entry can
never outlive the declarations it described.

**What refuses what.** Areas that fail resolution -- SUMO's projection and the world's geographic frame
disagreeing (V5.12), an area outside the network's extent when the extract had no `<bounds>` to check
it against, or no SUMO to project with -- are not published, and the place index and solar frame are
published without them: neither depends on an area. The package then carries no `areas.resolved.json`,
which reads as "not published", and the refusal is in the build log naming every rule broken.
"""
from __future__ import annotations

import io
import json
import logging
import os
import time
import xml.etree.ElementTree as ET
import zipfile
from dataclasses import dataclass, field
from pathlib import Path

from carlacontrol.AreaOfInterestResolver import DEFAULT_NEAR_M, AreaOfInterestResolver
from carlacontrol.AreaOfInterestSource import AreaOfInterestError, AreaOfInterestSource
from carlacontrol.GeodeticFrame import GeodeticFrame
from carlacontrol.OsmClipper import BoundingBox
from carlacontrol.PlaceIndex import PlaceIndex
from carlacontrol.SolarFrame import SolarFrame
from carlacontrol.SumoInstallation import SumoInstallation
from carlacontrol.SumoNetworkQuery import SumoNetworkQuery
from carlacontrol.WorldPackageReader import WorldPackageReader

REFERENCE_ENTRIES = (
    WorldPackageReader.AREAS_RESOLVED_ENTRY,
    WorldPackageReader.AREAS_SOURCE_ENTRY,
    WorldPackageReader.PLACE_INDEX_ENTRY,
    WorldPackageReader.SOLAR_FRAME_ENTRY,
)

logger = logging.getLogger(__name__)


@dataclass
class ReferenceSetReport:
    """What one publication wrote, what it warned about, and what it refused."""

    package: Path
    entries: list[str] = field(default_factory=list)
    warnings: list[str] = field(default_factory=list)
    refusals: list[str] = field(default_factory=list)

    @property
    def areas_published(self) -> bool:
        return WorldPackageReader.AREAS_RESOLVED_ENTRY in self.entries


class AuthoringReferenceSet:
    """Builds one world's reference set from its package, and writes it back into the package."""

    def __init__(self, package_path: str | Path, installation: SumoInstallation | None,
                 areas: AreaOfInterestSource | None = None,
                 near_m: float = DEFAULT_NEAR_M) -> None:
        self.package_path = Path(package_path)
        self.installation = installation
        self.areas = areas
        self.near_m = near_m

    @staticmethod
    def serialise(document: dict) -> bytes:
        """Stable bytes: sorted keys, two-space indent, UTF-8 with non-Latin names left readable."""
        return (json.dumps(document, indent=2, sort_keys=True, ensure_ascii=False) + "\n").encode(
            "utf-8")

    def build(self) -> tuple[dict[str, bytes], ReferenceSetReport]:
        """The entries to publish, name to bytes, and the report of how they were made."""
        report = ReferenceSetReport(self.package_path)
        reader = WorldPackageReader(self.package_path)
        manifest = reader.manifest
        network = reader.network_text()

        entries: dict[str, bytes] = {}
        places = PlaceIndex(network)
        entries[WorldPackageReader.PLACE_INDEX_ENTRY] = self.serialise(places.to_dict())
        report.warnings.extend(places.warnings)
        entries[WorldPackageReader.SOLAR_FRAME_ENTRY] = self.serialise(
            SolarFrame.from_manifest(manifest).to_dict())

        if self.areas is None:
            table = AreaOfInterestResolver.empty_table(network, manifest, self.near_m)
            entries[WorldPackageReader.AREAS_RESOLVED_ENTRY] = self.serialise(table)
        else:
            try:
                table, warnings = self._resolve(network, manifest)
            except AreaOfInterestError as refusal:
                report.refusals.extend(refusal.problems)
                for problem in refusal.problems:
                    logger.error("areas of interest not published: %s", problem)
            else:
                report.warnings.extend(warnings)
                entries[WorldPackageReader.AREAS_RESOLVED_ENTRY] = self.serialise(table)
                entries[WorldPackageReader.AREAS_SOURCE_ENTRY] = self.areas.raw
        report.entries = list(entries)
        return entries, report

    def publish(self) -> ReferenceSetReport:
        """Build the set and write it into the package, replacing any set published before."""
        entries, report = self.build()
        self._rewrite(entries)
        logger.info("published the authoring reference set into %s: %s", self.package_path,
                    ", ".join(report.entries))
        return report

    def _resolve(self, network: str, manifest: dict) -> tuple[dict, list[str]]:
        source = self.areas
        if not source.bounds_checked:
            extent = self._network_extent(network)
            if extent is None:
                raise AreaOfInterestError(source.source_name, [
                    "V5.3 neither the extract nor the network records a geographic extent, so no "
                    "area can be shown to lie inside the world"])
            problems = source.envelope_problems(extent)
            if problems:
                raise AreaOfInterestError(source.source_name, problems)
        if self.installation is None:
            raise AreaOfInterestError(source.source_name, [
                "no SUMO installation to project with: areas are placed on the network by SUMO's "
                "own projection, and without it they cannot be resolved"])
        latitude, longitude = manifest["OriginLatitude"], manifest["OriginLongitude"]
        with SumoNetworkQuery(self.installation, network) as query:
            resolver = AreaOfInterestResolver(network, manifest, query,
                                              GeodeticFrame(latitude, longitude), self.near_m)
            table = resolver.resolve(source)
        warnings = list(resolver.warnings)
        for area in table["areas"]:
            warnings.extend(area["warnings"])
        return table, warnings

    @staticmethod
    def _network_extent(network: str) -> BoundingBox | None:
        """The network's own geographic extent, `<location origBoundary>`, in degrees."""
        for _, element in ET.iterparse(io.StringIO(network)):
            if element.tag != "location":
                continue
            try:
                min_lon, min_lat, max_lon, max_lat = (
                    float(v) for v in element.get("origBoundary", "").split(","))
            except ValueError:
                return None
            return BoundingBox(min_lat=min_lat, min_lon=min_lon, max_lat=max_lat, max_lon=max_lon)
        return None

    def _rewrite(self, entries: dict[str, bytes]) -> None:
        staging = self.package_path.with_name(self.package_path.name + ".partial")
        staging.unlink(missing_ok=True)
        now = time.localtime()[:6]
        try:
            with zipfile.ZipFile(self.package_path) as source, \
                    zipfile.ZipFile(staging, "w", zipfile.ZIP_STORED) as target:
                for info in source.infolist():
                    if info.filename in REFERENCE_ENTRIES:
                        continue
                    copied = zipfile.ZipInfo(info.filename, info.date_time)
                    copied.compress_type = zipfile.ZIP_STORED
                    copied.external_attr = info.external_attr
                    target.writestr(copied, source.read(info))
                for name, data in entries.items():
                    added = zipfile.ZipInfo(name, now)
                    added.compress_type = zipfile.ZIP_STORED
                    target.writestr(added, data)
            os.replace(staging, self.package_path)
        finally:
            staging.unlink(missing_ok=True)
