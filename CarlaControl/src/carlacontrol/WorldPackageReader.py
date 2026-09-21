"""Reads a generated world's package: what it records about itself, and the network it carries.

A world package (`<name>.cwp`) is a zip holding `world.json`, `map.xodr`, `map.net.xml` and, when
the world was seated on a draped surface, `bareearth.bin`. It is written by `CarlaNet.Map`; this is
the read side, for the scenario tools, which need the SUMO network and the record of how it was
made.

`map.net.xml` is carried rather than rebuilt because it cannot be rebuilt. Asking netconvert for
OpenDRIVE output makes it default `rectangular-lane-cut` to true, which feeds junction shape
computation, so a second run with byte-identical flags produces a different graph -- measured on one
Arapahoe extract, 743 of 4,978 canonical rows differ and lane lengths move by up to 3.3 m while the
map's boundary matches exactly. A scenario authored against a rebuilt network binds to edges the
rendered world does not have, and nothing downstream notices.
"""
from __future__ import annotations

import json
import zipfile
from pathlib import Path


class WorldPackageReader:
    """One generated world's package, opened for reading."""

    MANIFEST_ENTRY = "world.json"
    NETWORK_ENTRY = "map.net.xml"
    OPENDRIVE_ENTRY = "map.xodr"

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

    @property
    def netconvert_argv(self) -> list[str]:
        """Every argument netconvert was given. Empty on a package written before it was recorded."""
        return list(self.manifest.get("NetconvertArgv", []))

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
