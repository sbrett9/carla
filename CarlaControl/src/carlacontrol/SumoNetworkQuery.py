"""SUMO's own answers about one road network, from a SUMO process started on it and nothing else.

Two questions about a network are answered here, and both are answered by SUMO rather than by a
reimplementation:

  * **Where a longitude and latitude fall in the network's metres.** `traci.simulation.convertGeo`
    applies the network's own projection string and offset through the PROJ that SUMO links, which
    is the PROJ that placed every lane. The offline alternative, `sumolib`'s `convertLonLat2XY`,
    needs `pyproj`, which is not a dependency here and would bring a second PROJ build with it --
    two implementations of one transform is a divergence this project has already paid for.
  * **Which vehicle classes a lane admits.** `traci.lane.getAllowed` expands a lane's `allow` or
    `disallow` attribute against the class list of the SUMO that will run it, so a class added in a
    later SUMO release is accounted for without a copy of the list living here.

No CARLA server is involved. The process loads the network, is asked, and is closed; it never takes a
simulation step. Starting it measured 0.53 to 0.68 s on the three shipped world networks, and one
conversion about 60 microseconds.
"""
from __future__ import annotations

import logging
import shutil
import subprocess
import tempfile
import uuid
from pathlib import Path

from carlacontrol.SumoInstallation import SumoInstallation

logger = logging.getLogger(__name__)


class SumoNetworkQuery:
    """A SUMO process holding one network, asked about it. Use as a context manager."""

    def __init__(self, installation: SumoInstallation, network_text: str) -> None:
        self.installation = installation
        self.network_text = network_text
        self._directory: Path | None = None
        self._connection = None

    @property
    def sumo_version(self) -> str:
        """The release of the SUMO answering, e.g. `1.27.0`; empty when it cannot be read."""
        return self.installation.version or ""

    def __enter__(self) -> SumoNetworkQuery:
        traci = self.installation.import_traci()
        self._directory = Path(tempfile.mkdtemp(prefix="carlacontrol_network_"))
        network_path = self._directory / "map.net.xml"
        network_path.write_text(self.network_text, encoding="utf-8")
        label = f"network_query_{uuid.uuid4().hex}"
        command = [str(self.installation.sumo), "-n", str(network_path),
                   "--begin", "0", "--end", "1",
                   "--no-step-log", "true", "--no-warnings", "true",
                   "--duration-log.disable", "true"]
        try:
            traci.start(command, label=label, stdout=subprocess.DEVNULL, doSwitch=False)
            self._connection = traci.getConnection(label)
        except Exception:
            self._remove_directory()
            raise
        logger.debug("SUMO %s holding %s for queries", self.sumo_version, network_path)
        return self

    def __exit__(self, *_exc) -> None:
        try:
            if self._connection is not None:
                self._connection.close()
        finally:
            self._connection = None
            self._remove_directory()

    def to_sumo(self, *, longitude: float, latitude: float) -> tuple[float, float]:
        """The network-frame metres of a WGS84 position. Keyword-only, because every internal
        signature in this project orders latitude first and GeoJSON orders longitude first."""
        x, y = self._require().simulation.convertGeo(float(longitude), float(latitude),
                                                     fromGeo=True)
        return float(x), float(y)

    def to_geodetic(self, x: float, y: float) -> tuple[float, float]:
        """The WGS84 `(longitude, latitude)` of a network-frame position."""
        longitude, latitude = self._require().simulation.convertGeo(float(x), float(y))
        return float(longitude), float(latitude)

    def allowed_vclasses(self, lane_id: str) -> list[str]:
        """The vehicle classes SUMO admits on a lane, in SUMO's own order."""
        return list(self._require().lane.getAllowed(lane_id))

    def _require(self):
        if self._connection is None:
            raise RuntimeError("SumoNetworkQuery is not open; use it as a context manager")
        return self._connection

    def _remove_directory(self) -> None:
        if self._directory is not None:
            shutil.rmtree(self._directory, ignore_errors=True)
            self._directory = None
