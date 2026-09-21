"""Emit Cursor-on-Target telemetry for every vehicle in a running SUMO simulation.

Drives a SUMO scenario through TraCI and, at a chosen rate, turns each vehicle's state into one CoT
event. Events go to any combination of three sinks in a single pass: a UDP socket (unicast or the
TAK situational-awareness multicast group), one XML file holding every event, and a CSV row per
vehicle per update for use as a plain dataset.

The events are built by `CotUdpEmitter.vehicle_telemetry_to_cot`, the same formatter the CARLA truth
producer uses, so what comes out here is the schema described in
`Docs/CAT_Research/Findings/09_Telemetry_CoT_Contract.md` and is directly comparable to CARLA truth
rather than a second dialect of it.

Two conversions matter and both avoid guesswork:

  * **Position.** SUMO works in the projected metres the network was built in. Rather than
    re-implement the projection, this asks the running simulation to convert each point
    (`traci.simulation.convertGeo`), which uses SUMO's own PROJ and the network's own projection
    string. No pyproj, and no chance of the two disagreeing.
  * **Height.** A SUMO network is flat, so height cannot come from the simulation. Where a world
    package's `bareearth.bin` is available its per-cell bare-earth grid supplies the ellipsoidal
    height, which is the same bare-earth truth CARLA telemetry reports; otherwise a single
    configured height is used for every vehicle. Terrain relief across a map is tens of metres, so
    the grid is worth supplying.

SUMO reports heading as degrees clockwise from north, which is already what CoT `track/course`
wants, and unlike a course derived from velocity it stays meaningful for a stopped vehicle.
"""
from __future__ import annotations

import csv
import logging
import math
import struct
import time
import zipfile
from dataclasses import dataclass, field
from datetime import UTC, datetime, timedelta
from pathlib import Path

from carlacontrol.CotUdpEmitter import CotUdpEmitter
from carlacontrol.SumoInstallation import SumoInstallation

# SUMO's own vehicle classes carry enough to fill the contract's base_type without a per-scenario
# lookup table. Anything unlisted is reported as it comes.
BASE_TYPE_BY_VEHICLE_CLASS = {
    "passenger": "car",
    "delivery": "van",
    "truck": "truck",
    "trailer": "truck",
    "motorcycle": "motorcycle",
    "moped": "motorcycle",
    "bicycle": "bicycle",
    "bus": "bus",
    "coach": "bus",
    "taxi": "car",
    "emergency": "car",
}

CSV_COLUMNS = [
    "time_utc", "sim_time_s", "uid", "callsign", "cot_type", "how",
    "lat", "lon", "hae_m", "ce_m", "le_m",
    "course_deg", "speed_mps", "vx", "vy", "vz",
    "base_type", "type_id", "special_type", "length_m", "width_m", "height_m", "color",
    "role_name", "marked", "edge", "lane", "sumo_x", "sumo_y", "carla_x", "carla_y",
]


@dataclass(frozen=True)
class BareEarthGrid:
    """The per-cell bare-earth heights a generated world package records alongside its map.

    Written by `WorldPackage`: a 60-byte header (magic, origin latitude/longitude/height, grid
    minimum corner, cell size, columns, rows) followed by two float32 planes of columns x rows --
    the drape offset first, then the bare-earth height this reads. Heights are ellipsoidal, which
    is the datum the telemetry contract is fixed to.
    """

    min_x: float
    min_y: float
    cell_size: float
    columns: int
    rows: int
    origin_height: float
    heights: tuple[float, ...]

    MAGIC = 0x43575031
    HEADER = "<iddddddii"

    # Inside a packaged world the grid is an entry in the archive under this name; some worlds ship
    # the same bytes as a loose file beside the map instead. Both are read the same way.
    PACKAGE_ENTRY = "bareearth.bin"

    @classmethod
    def from_file(cls, path: str | Path) -> BareEarthGrid:
        """Read the grid, from either a loose `bareearth.bin` or a packaged world."""
        path = Path(path)
        if zipfile.is_zipfile(path):
            with zipfile.ZipFile(path) as package:
                if cls.PACKAGE_ENTRY not in package.namelist():
                    raise ValueError(f"{path} has no {cls.PACKAGE_ENTRY}; it holds "
                                     f"{', '.join(package.namelist())}")
                raw = package.read(cls.PACKAGE_ENTRY)
        else:
            raw = path.read_bytes()
        magic, _lat, _lon, origin_h, min_x, min_y, cell, cols, rows = struct.unpack_from(
            cls.HEADER, raw, 0)
        if magic != cls.MAGIC:
            raise ValueError(f"{path} is not a bare-earth grid (magic {magic:#x})")
        count = cols * rows
        header_size = struct.calcsize(cls.HEADER)
        # The drape-offset plane comes first and is skipped; the bare-earth plane follows it.
        heights = struct.unpack_from(f"<{count}f", raw, header_size + 4 * count)
        return cls(min_x, min_y, cell, cols, rows, origin_h, heights)

    def height_at(self, x: float, y: float) -> float:
        """Bare-earth height at a point in the map's projected metres, clamped to the grid."""
        col = min(self.columns - 1, max(0, int((x - self.min_x) / self.cell_size)))
        row = min(self.rows - 1, max(0, int((y - self.min_y) / self.cell_size)))
        return self.heights[row * self.columns + col]


@dataclass
class CotOutputSettings:
    """Where the events go, how often, and how they are labelled."""

    udp_host: str | None = None
    udp_port: int = 6969
    udp_ttl: int = 1
    xml_path: Path | None = None
    csv_path: Path | None = None
    # Events per vehicle per second. Independent of the simulation step, which is much finer; the
    # contract's 3 s default staleness assumes a few updates per second.
    rate_hz: float = 1.0
    stale_seconds: float = 3.0
    affiliation: str = "n"
    uid_prefix: str = "SUMO-TRUTH"
    # The vehicle to flag in the dataset, and optionally to give a different CoT affiliation so it
    # stands out in a TAK client.
    marked_vehicle: str = "orbiter"
    marked_affiliation: str | None = None
    # Wall-clock instant that simulation time zero maps to. Pin it for a reproducible dataset;
    # leave it unset to stamp events from the clock when the run starts.
    epoch: datetime | None = None


@dataclass
class RunReport:
    """What a run produced."""

    events: int = 0
    updates: int = 0
    vehicles: int = 0
    sim_seconds: float = 0.0
    wall_seconds: float = 0.0
    sinks: list[str] = field(default_factory=list)

    @property
    def achieved_real_time_factor(self) -> float:
        """Seconds of simulation per second of wall clock."""
        return self.sim_seconds / self.wall_seconds if self.wall_seconds else 0.0


class SumoCotBridge:
    """Runs a SUMO scenario and emits CoT telemetry for every vehicle in it."""

    def __init__(self, installation: SumoInstallation, config_path: str | Path,
                 bare_earth: BareEarthGrid | None = None, constant_hae: float = 0.0,
                 use_gui: bool = False):
        self.installation = installation
        self.config_path = Path(config_path)
        self.bare_earth = bare_earth
        self.constant_hae = constant_hae
        self.use_gui = use_gui
        self.logger = logging.getLogger(__name__)

    def run(self, settings: CotOutputSettings, end_time: float | None = None,
            extra_sumo_args: list[str] | None = None,
            real_time_factor: float = 0.0) -> RunReport:
        """Step the scenario to its end, emitting CoT for every vehicle at the configured rate.

        `real_time_factor` paces the run against the wall clock: 1.0 makes a second of simulation
        take a second, 2.0 runs at twice that, and 0 -- the default -- steps as fast as the machine
        allows, which on a map this size is tens of times faster than real time. Pacing matters for
        a live feed, where a consumer expects tracks to advance at a believable rate; it is only
        wasted time when the point is to write a dataset.
        """
        traci = self.installation.import_traci()
        report = RunReport()
        epoch = settings.epoch or datetime.now(UTC)

        udp = CotUdpEmitter(settings.udp_host, settings.udp_port, settings.udp_ttl) \
            if settings.udp_host else None
        xml_file = open(settings.xml_path, "w", encoding="utf-8") if settings.xml_path else None
        csv_file = open(settings.csv_path, "w", encoding="utf-8", newline="") \
            if settings.csv_path else None
        csv_writer = None
        if csv_file:
            csv_writer = csv.DictWriter(csv_file, fieldnames=CSV_COLUMNS)
            csv_writer.writeheader()
        if xml_file:
            xml_file.write('<?xml version="1.0" encoding="UTF-8"?>\n<events source="sumo" '
                           f'scenario="{self.config_path.stem}" '
                           f'epoch="{CotUdpEmitter.format_cot_timestamp(epoch)}">\n')
        for name, on in (("UDP", udp), ("XML", xml_file), ("CSV", csv_file)):
            if on:
                report.sinks.append(name)
        if not report.sinks:
            raise ValueError("no output selected: give a UDP host, an XML path or a CSV path")

        binary = self.installation.sumo_gui if self.use_gui else self.installation.sumo
        command = [str(binary), "-c", str(self.config_path), "--no-step-log", "true"]
        command += extra_sumo_args or []
        traci.start(command)
        try:
            step = traci.simulation.getDeltaT()
            every = max(1, int(round(1.0 / (settings.rate_hz * step))))
            self.logger.info("emitting every %d steps (%.2f s) to %s",
                             every, every * step, ", ".join(report.sinks))
            # Stepping until no vehicles remain would run past the configuration's own end time,
            # since flows stop inserting before the last trips finish. Honour that end time so a
            # run here covers the same span as running sumo on the configuration directly; an
            # explicit end_time overrides it, and -1 means the configuration set none.
            if end_time is None:
                configured = traci.simulation.getEndTime()
                end_time = configured if configured > 0 else None
            seen: set[str] = set()
            index = 0
            started_at = time.monotonic()
            sim_start = traci.simulation.getTime()
            if real_time_factor > 0:
                self.logger.info("pacing at %.2fx real time", real_time_factor)
            while traci.simulation.getMinExpectedNumber() > 0:
                traci.simulationStep()
                now = traci.simulation.getTime()
                if real_time_factor > 0:
                    # Targets are absolute rather than a sleep per step, so a step that overruns is
                    # absorbed by the next one instead of accumulating drift over a long run.
                    behind = (started_at + (now - sim_start) / real_time_factor) - time.monotonic()
                    if behind > 0:
                        time.sleep(behind)
                if end_time is not None and now > end_time:
                    break
                index += 1
                if index % every:
                    continue
                stamp = epoch + timedelta(seconds=now)
                report.updates += 1
                for vehicle_id in traci.vehicle.getIDList():
                    seen.add(vehicle_id)
                    record = self._sample(traci, vehicle_id, settings)
                    marked = vehicle_id == settings.marked_vehicle
                    affiliation = (settings.marked_affiliation or settings.affiliation) \
                        if marked else settings.affiliation
                    event = CotUdpEmitter.vehicle_telemetry_to_cot(
                        record, affiliation=affiliation, stale_seconds=settings.stale_seconds,
                        source="truth", uid_prefix=settings.uid_prefix, when=stamp)
                    if udp:
                        udp.send(event)
                    if xml_file:
                        xml_file.write("  " + event + "\n")
                    if csv_writer:
                        csv_writer.writerow(self._row(record, settings, affiliation, stamp,
                                                      now, marked))
                    report.events += 1
                report.sim_seconds = now
            report.vehicles = len(seen)
            report.wall_seconds = time.monotonic() - started_at
        finally:
            traci.close()
            if udp:
                udp.close()
            if xml_file:
                xml_file.write("</events>\n")
                xml_file.close()
            if csv_file:
                csv_file.close()
        self.logger.info("%d events for %d vehicles over %.0f s of simulation in %.0f s "
                         "(%.1fx real time)", report.events, report.vehicles, report.sim_seconds,
                         report.wall_seconds, report.achieved_real_time_factor)
        return report

    # -- sampling ------------------------------------------------------------------------------

    def _sample(self, traci, vehicle_id: str, settings: CotOutputSettings) -> dict:
        """One vehicle's state as the record `vehicle_telemetry_to_cot` expects."""
        x, y = traci.vehicle.getPosition(vehicle_id)
        lon, lat = traci.simulation.convertGeo(x, y)
        type_id = traci.vehicle.getTypeID(vehicle_id)
        speed = traci.vehicle.getSpeed(vehicle_id)
        # SUMO heading: degrees clockwise from north, which is CoT course as it stands.
        course = traci.vehicle.getAngle(vehicle_id) % 360.0
        heading = math.radians(course)
        red, green, blue, _alpha = traci.vehicletype.getColor(type_id)
        vehicle_class = traci.vehicletype.getVehicleClass(type_id)
        return {
            "id": vehicle_id,
            "lat": lat,
            "lon": lon,
            "hae": self._height_at(x, y),
            "course_deg": course,
            "speed_mps": speed,
            # The contract's frame is CARLA's: +X east, -Y north, so that
            # atan2(vx, -vy) recovers the compass bearing.
            "vx": speed * math.sin(heading),
            "vy": -speed * math.cos(heading),
            "vz": 0.0,
            "base_type": BASE_TYPE_BY_VEHICLE_CLASS.get(vehicle_class, vehicle_class),
            "type_id": type_id,
            "special_type": "marked" if vehicle_id == settings.marked_vehicle else "",
            "length_m": traci.vehicle.getLength(vehicle_id),
            "width_m": traci.vehicle.getWidth(vehicle_id),
            "height_m": traci.vehicle.getHeight(vehicle_id),
            "color": f"{red},{green},{blue}",
            # SUMO names a flow's vehicles "<flow id>.<n>", so the flow it belongs to is the
            # closest thing the simulation has to a role.
            "role_name": vehicle_id.rsplit(".", 1)[0],
            "edge": traci.vehicle.getRoadID(vehicle_id),
            "lane": traci.vehicle.getLaneID(vehicle_id),
            "x": x,
            "y": y,
        }

    def _height_at(self, x: float, y: float) -> float:
        if self.bare_earth:
            return self.bare_earth.height_at(x, y)
        return self.constant_hae

    def _row(self, record: dict, settings: CotOutputSettings, affiliation: str,
             stamp: datetime, sim_time: float, marked: bool) -> dict:
        return {
            "time_utc": CotUdpEmitter.format_cot_timestamp(stamp),
            "sim_time_s": f"{sim_time:.2f}",
            "uid": f"{settings.uid_prefix}-{record['id']}",
            "callsign": f"{record['base_type']}-{record['id']}",
            "cot_type": f"a-{affiliation}-G-E-V",
            "how": "m-g",
            "lat": f"{record['lat']:.7f}",
            "lon": f"{record['lon']:.7f}",
            "hae_m": f"{record['hae']:.2f}",
            "ce_m": "0.0",
            "le_m": "0.0",
            "course_deg": f"{record['course_deg']:.1f}",
            "speed_mps": f"{record['speed_mps']:.2f}",
            "vx": f"{record['vx']:.2f}",
            "vy": f"{record['vy']:.2f}",
            "vz": "0.00",
            "base_type": record["base_type"],
            "type_id": record["type_id"],
            "special_type": record["special_type"],
            "length_m": f"{record['length_m']:.2f}",
            "width_m": f"{record['width_m']:.2f}",
            "height_m": f"{record['height_m']:.2f}",
            "color": record["color"],
            "role_name": record["role_name"],
            "marked": "1" if marked else "0",
            "edge": record["edge"],
            "lane": record["lane"],
            "sumo_x": f"{record['x']:.2f}",
            "sumo_y": f"{record['y']:.2f}",
            # CARLA negates Y against the projected frame SUMO works in.
            "carla_x": f"{record['x']:.2f}",
            "carla_y": f"{-record['y']:.2f}",
        }
