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
    the grid is worth supplying. The grid is indexed in the CARLA world frame, where north is -y,
    while SUMO reports the projection's own metres, where north is +y; the two differ by the sign
    of y alone, and a position crosses that boundary once, in `_height_at`.

SUMO reports heading as degrees clockwise from north, which is already what CoT `track/course`
wants, and unlike a course derived from velocity it stays meaningful for a stopped vehicle.

**Two channels, split here rather than downstream.** Sampling a vehicle produces a *record*, which is
ground truth and knows everything, including which vehicles a scenario author planted and what the
author called them. The XML and CSV are the truth sidecar and are written from the whole record. The
datagram feed is a moving-map display and is given what a display needs, which leaves out the fields
named in `AUTHORED_TRUTH_FIELDS`. A field means one thing in every sink: `special_type` is the
vehicle's kind as the contract spells it and never the author's marking, which the sidecar carries
in `marked`, a field of its own and not of the contract (`06_Truth_And_Annotation.md` D6.18). SUMO
reports no kind, and its vehicle class is no base type, so both are the measured vehicle catalogue's
for the CARLA blueprint a vehicle's type names, where the bridge is given the catalogue, exactly as
the capture path's truth takes them; only a type that names none of the catalogue's blueprints has
its base type read from its vehicle class. A compiled scenario marks nothing here; its labels are its
`*.supervision.json`, joined to the sidecar by vehicle id.

**Both files name their format and what made them.** The XML's `<events>` carries `format_version` and,
first inside it, a `<_producer>` (`ProducerRecord`): the bridge and its release, and the SUMO release
that ran. The CSV's header stays its columns, so every reader of it reads it as before; its format and
the same record are in `<name>.summary.json` beside it, written as the CSV is opened.
"""
from __future__ import annotations

import csv
import json
import logging
import math
import struct
import sys
import time
import xml.etree.ElementTree as ET
import zipfile
from array import array
from dataclasses import dataclass, field
from datetime import UTC, datetime, timedelta
from pathlib import Path

from carlacontrol.CotUdpEmitter import CotUdpEmitter
from carlacontrol.ProducerRecord import ProducerRecord
from carlacontrol.SumoInstallation import SumoInstallation
from carlacontrol.VehicleCatalogue import (
    BLUEPRINT_PARAM,
    CATALOGUE_DIGEST_PARAM,
    CLASS_PARAM,
    VehicleCatalogue,
)

# The component the files' record of what made them names.
TOOL = "carlacontrol.SumoCotBridge"
# The format of the XML file (on its <events>) and of the CSV (in its summary). Independent of the
# release version; a file written before either carried one is version 1.
XML_FORMAT_VERSION = 1
CSV_FORMAT_VERSION = 1
CSV_SUMMARY_SUFFIX = ".summary.json"

# SUMO's own vehicle classes as the contract's base_type, for a vehicle whose type names no blueprint
# the catalogue curates: a type that names one takes the catalogue's base type, because a class says
# how SUMO drives a vehicle and not what it is -- an ambulance's `emergency` reads as a car here, a
# police car's `authority` as nothing at all. Anything unlisted is reported as it comes.
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

# Record fields the datagram feed is not given. The XML and CSV are the truth sidecar and carry
# every one of them; a moving-map display has no use for them:
#
#   `marked` says that this vehicle is one the author planted, which is the whole question a
#       behavioural model is being given. It is the sidecar's own field and not the contract's;
#   `special_type` is the contract's vehicle kind -- emergency, taxi, electric -- which SUMO does
#       not report. A record carries the kind the vehicle catalogue curates for the blueprint the
#       vehicle's type names, where the bridge is given a catalogue, and an empty one otherwise:
#       the same for a planted vehicle as for any other of its blueprint;
#   `type_id` is the SUMO vehicle-type id and `role_name` the flow the vehicle was generated from,
#       which are the author's own names for their populations. A planted vehicle needs a vehicle
#       type of its own to carry its behaviour -- a reduced speed factor, say -- so its type id is
#       unique to it no matter what it is called, and a flow written for one vehicle names that one
#       vehicle. Neither can be made indistinguishable while the behaviour stays intact, so they
#       stay on the truth side.
AUTHORED_TRUTH_FIELDS = ("type_id", "special_type", "role_name", "marked")


@dataclass(frozen=True)
class BareEarthGrid:
    """The per-cell bare-earth heights a generated world package records alongside its map.

    Written by `WorldPackage`: a 60-byte header (magic, origin latitude/longitude/height, grid
    minimum corner, cell size, columns, rows) followed by two float32 planes of columns x rows --
    the drape offset first, then the bare-earth height this reads. Heights are ellipsoidal, which
    is the datum the telemetry contract is fixed to.

    The heights are held as a `float32` array rather than a tuple of Python floats. A port-sized map
    is 7.6 million cells: 30.4 MB of float32 on disk, which as boxed objects becomes 60.9 MB of
    pointers plus 182.7 MB of float objects. Nothing reads them as objects -- the only access is one
    cell at a time through `height_at` -- so the boxing buys nothing and costs 211 MB of resident
    memory on a process that also holds a simulation.
    """

    min_x: float
    min_y: float
    cell_size: float
    columns: int
    rows: int
    origin_height: float
    heights: array

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
        start = header_size + 4 * count
        heights = array("f")
        heights.frombytes(memoryview(raw)[start:start + 4 * count])
        # The file is little-endian float32 and `array` reads in the host's order.
        if sys.byteorder != "little":
            heights.byteswap()
        return cls(min_x, min_y, cell, cols, rows, origin_h, heights)

    def height_at(self, x: float, y: float) -> float | None:
        """Bare-earth height under a point in the CARLA world frame, or None outside the grid.

        The grid is laid out in the frame the world was generated in -- CARLA's, where +x is east
        and **-y** is north (`DrapeTerrain.MakeGrid`, `Geodesy.GeodeticToCarlaLocal`). A caller
        holding a position in the projection's own metres, as SUMO and OpenDRIVE report them, must
        negate y before asking: the two frames differ by that sign alone, so an unnegated y reads
        the row mirrored about the map's centre line and returns a height measured somewhere else.

        Outside the grid there is no answer. Returning the nearest edge cell would report a height
        from the map's rim as though it had been measured under the vehicle, and nothing in the
        number would show it; the C# reader of the same grids refuses the same way
        (`CarlaClient.SampleDrapeGroundElevation`).
        """
        col = math.floor((x - self.min_x) / self.cell_size)
        row = math.floor((y - self.min_y) / self.cell_size)
        if not (0 <= col < self.columns and 0 <= row < self.rows):
            return None
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
    # The vehicle the scenario planted. It is recorded in the sidecar's `marked` field and counted
    # in the run report; its `special_type`, and its CoT type in the files, are those of any
    # vehicle of its kind.
    marked_vehicle: str = "orbiter"
    # A scenario with more than one planted vehicle flags several at once: any vehicle whose id is
    # in this set is treated exactly like `marked_vehicle`. Empty keeps the single-vehicle
    # behaviour above.
    marked_ids: frozenset[str] = frozenset()
    # CoT affiliation per vehicle population -- the run's display convention, `CotDisplayConvention`
    # -- so a mixed population carries the affiliations its populations would really have: civilian
    # neutral, military friendly. A population is the class a compiled scenario's vType names in its
    # `carla:class_id` parameter or, where a type names none, the SUMO vehicle-type id itself; one
    # absent from the map falls back to `affiliation`. A planted vehicle takes the affiliation of
    # the population it is hiding in, exactly as its neighbours do, so the affiliation never
    # announces it.
    affiliation_by_type: dict[str, str] = field(default_factory=dict)
    # The file that convention came from, recorded ahead of the XML's events so the dataset says
    # which convention drew it. Empty when the run was given none.
    display_convention: str = ""
    # Give the planted vehicles a different affiliation **in the live feed only**, so an operator
    # watching a TAK client can see which vehicle the scenario planted. It reaches the UDP stream
    # and never the XML or CSV: a recorded CoT type carries the population's affiliation and
    # nothing else, and the files say which vehicles were planted in `marked`. Leave it unset for
    # an unmarked live feed as well.
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
    # How many of those vehicles the scenario's labels flagged. Zero against a labelled scenario
    # means the label ids and the route file have drifted apart, which the written files show
    # only as a `marked` field that is never set.
    marked_vehicles: int = 0
    # Events whose height fell back to the configured constant because the vehicle was outside the
    # bare-earth grid. A run with any of these has heights of two different kinds in one file.
    off_grid_heights: int = 0
    sim_seconds: float = 0.0
    wall_seconds: float = 0.0
    sinks: list[str] = field(default_factory=list)
    # The instant simulation time zero was stamped with. Reported because it is not always chosen:
    # without an explicit epoch the run takes the clock as it starts, and anything that has to place
    # this run's output on a wall clock afterwards -- a described absence, say -- needs to be told
    # which instant that was rather than guessing it.
    epoch: datetime | None = None

    @property
    def achieved_real_time_factor(self) -> float:
        """Seconds of simulation per second of wall clock."""
        return self.sim_seconds / self.wall_seconds if self.wall_seconds else 0.0


class SumoCotBridge:
    """Runs a SUMO scenario and emits CoT telemetry for every vehicle in it."""

    def __init__(self, installation: SumoInstallation, config_path: str | Path,
                 bare_earth: BareEarthGrid | None = None, constant_hae: float = 0.0,
                 use_gui: bool = False, catalogue: VehicleCatalogue | None = None):
        self.installation = installation
        self.config_path = Path(config_path)
        self.bare_earth = bare_earth
        self.constant_hae = constant_hae
        self.use_gui = use_gui
        # The measured vehicle catalogue each vehicle's base type and kind are read from, by the
        # blueprint its type names. None reports every vehicle's kind empty and its base type its
        # vehicle class's, which is all SUMO alone can say.
        self.catalogue = catalogue
        self.off_grid_heights = 0
        # The population each vehicle type belongs to, read once per type per run.
        self._population_of: dict[str, str] = {}
        # The base type and kind each vehicle type's blueprint has, read once per type per run.
        self._kinds_of: dict[str, tuple[str, str]] = {}
        # Catalogue digests vehicle types were written from that are not the catalogue's own,
        # each warned about once.
        self._foreign_catalogues: set[str] = set()
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
        self.off_grid_heights = 0
        self._population_of = {}
        self._kinds_of = {}
        self._foreign_catalogues = set()
        epoch = settings.epoch or datetime.now(UTC)
        report.epoch = epoch

        udp = CotUdpEmitter(settings.udp_host, settings.udp_port, settings.udp_ttl) \
            if settings.udp_host else None
        xml_file = open(settings.xml_path, "w", encoding="utf-8") if settings.xml_path else None
        csv_file = open(settings.csv_path, "w", encoding="utf-8", newline="") \
            if settings.csv_path else None
        # What made the files: this bridge and the SUMO release it runs; no CARLA server is used.
        producer = ProducerRecord.record(TOOL, sumo=getattr(self.installation, "version", None))
        csv_writer = None
        if csv_file:
            csv_writer = csv.DictWriter(csv_file, fieldnames=CSV_COLUMNS)
            csv_writer.writeheader()
            self.write_csv_summary(Path(settings.csv_path), producer)
        if xml_file:
            xml_file.write('<?xml version="1.0" encoding="UTF-8"?>\n<events source="sumo" '
                           f'format_version="{XML_FORMAT_VERSION}" '
                           f'scenario="{self.config_path.stem}" '
                           f'epoch="{CotUdpEmitter.format_cot_timestamp(epoch)}">\n')
            xml_file.write("  " + ET.tostring(ProducerRecord.xml_element(producer), encoding="unicode")
                           + "\n")
            xml_file.write("  " + self._display_convention_xml(settings) + "\n")
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
            seen_marked: set[str] = set()
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
                    if record["marked"]:
                        seen_marked.add(vehicle_id)
                    # A planted vehicle takes its population's affiliation like any other member of
                    # it, so that the CoT type carries affiliation and nothing else.
                    affiliation = self._affiliation(traci, record["type_id"], settings)
                    # The written files are the truth sidecar and carry the whole record; the
                    # datagram feed is a moving-map display and carries what a display needs.
                    event = CotUdpEmitter.vehicle_telemetry_to_cot(
                        record, affiliation=affiliation, stale_seconds=settings.stale_seconds,
                        source="truth", uid_prefix=settings.uid_prefix, when=stamp)
                    if xml_file:
                        xml_file.write("  " + event + "\n")
                    if csv_writer:
                        csv_writer.writerow(self._row(record, settings, affiliation, stamp, now))
                    if udp:
                        # An operator watching a live feed may be shown which vehicle the scenario
                        # planted, which is what --marked-affiliation is for.
                        shown = settings.marked_affiliation if (
                            settings.marked_affiliation and record["marked"]) else affiliation
                        udp.send(CotUdpEmitter.vehicle_telemetry_to_cot(
                            self._published(record), affiliation=shown,
                            stale_seconds=settings.stale_seconds, source="truth",
                            uid_prefix=settings.uid_prefix, when=stamp))
                    report.events += 1
                report.sim_seconds = now
            report.vehicles = len(seen)
            report.marked_vehicles = len(seen_marked)
            report.off_grid_heights = self.off_grid_heights
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
        if report.off_grid_heights:
            self.logger.warning("%d of %d events (%.1f%%) report the configured constant height "
                                "because the vehicle was outside the bare-earth grid",
                                report.off_grid_heights, report.events,
                                100.0 * report.off_grid_heights / max(1, report.events))
        expected_marked = len(settings.marked_ids) or 1
        if report.marked_vehicles:
            self.logger.info("%d of %d labeled vehicles appeared; the written files record which "
                             "in their marked field", report.marked_vehicles, expected_marked)
        elif settings.marked_ids:
            self.logger.warning("none of the %d labeled vehicle ids appeared in the run: the "
                                "labels and the route file name different vehicles",
                                len(settings.marked_ids))
        return report

    # -- sampling ------------------------------------------------------------------------------

    def _sample(self, traci, vehicle_id: str, settings: CotOutputSettings) -> dict:
        """One vehicle's state as a ground-truth record, including the fields only truth may see."""
        x, y = traci.vehicle.getPosition(vehicle_id)
        lon, lat = traci.simulation.convertGeo(x, y)
        type_id = traci.vehicle.getTypeID(vehicle_id)
        speed = traci.vehicle.getSpeed(vehicle_id)
        # SUMO heading: degrees clockwise from north, which is CoT course as it stands.
        course = traci.vehicle.getAngle(vehicle_id) % 360.0
        heading = math.radians(course)
        red, green, blue, _alpha = traci.vehicletype.getColor(type_id)
        base_type, special_type = self._kinds(traci, type_id)
        marked = vehicle_id == settings.marked_vehicle or vehicle_id in settings.marked_ids
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
            # The catalogue's for the blueprint the type names, and the vehicle class's only for a
            # type that names none of its blueprints (06 D6.18).
            "base_type": base_type,
            "type_id": type_id,
            # The contract's vehicle kind, which SUMO does not report: the catalogue's for the
            # blueprint the type names. Whether the author planted this one is `marked`, never
            # this (06 D6.18).
            "special_type": special_type,
            "marked": marked,
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

    def _kinds(self, traci, type_id: str) -> tuple[str, str]:
        """The vehicle's base type and kind: the catalogue's for the blueprint its type names.

        SUMO reports no kind, and its vehicle class says how SUMO drives a vehicle rather than what
        it is. A compiled scenario's vType names the CARLA blueprint it was measured from in its
        `carla:blueprint` parameter, and the catalogue curates a base type and a kind for every
        blueprint its classes draw, so a bridge given the catalogue reports what the capture path
        reports for the same body (`06_Truth_And_Annotation.md` D6.18). A type that names no
        blueprint, one that names a blueprint the catalogue does not curate, and every type on a
        bridge given no catalogue have their base type read from their vehicle class
        (`BASE_TYPE_BY_VEHICLE_CLASS`) and no kind. Whether the author planted the vehicle never
        enters into it. A type does not change during a run, so each type is asked once.
        """
        kinds = self._kinds_of.get(type_id)
        if kinds is not None:
            return kinds
        vehicle_class = traci.vehicletype.getVehicleClass(type_id)
        base_type = BASE_TYPE_BY_VEHICLE_CLASS.get(vehicle_class, vehicle_class)
        kind = ""
        if self.catalogue is not None:
            blueprint = traci.vehicletype.getParameter(type_id, BLUEPRINT_PARAM)
            if blueprint:
                base_type = self.catalogue.base_type_of(blueprint) or base_type
                kind = self.catalogue.special_type_of(blueprint) or ""
            self._check_catalogue(traci, type_id)
        kinds = self._kinds_of[type_id] = (base_type, kind)
        return kinds

    def _check_catalogue(self, traci, type_id: str) -> None:
        """Say so, once per catalogue, when a type was written from a catalogue other than this one.

        A compiled type records the digest of the catalogue it was written from. Read against
        another, a blueprint whose class has changed between the two is reported with this
        catalogue's kind, and nothing in the record would show it.
        """
        written_from = traci.vehicletype.getParameter(type_id, CATALOGUE_DIGEST_PARAM)
        if (not written_from or written_from == self.catalogue.catalogue_digest
                or written_from in self._foreign_catalogues):
            return
        self._foreign_catalogues.add(written_from)
        self.logger.warning(
            "vehicle type %r was written from catalogue %s, and kinds are read from catalogue %s "
            "(%s): a blueprint whose class differs between the two is reported with the latter's",
            type_id, written_from, self.catalogue.catalogue_digest, self.catalogue.catalogue_id)

    def _affiliation(self, traci, type_id: str, settings: CotOutputSettings) -> str:
        """The affiliation the run's display convention gives a vehicle of this type.

        The convention names populations. A compiled scenario's vType is one body of a class and
        says which in its `carla:class_id` parameter, read back here rather than parsed out of the
        type id; a hand-written type names no class and is a population of its own. A type's
        parameters do not change during a run, so each type is asked once.
        """
        if not settings.affiliation_by_type:
            return settings.affiliation
        population = self._population_of.get(type_id)
        if population is None:
            population = traci.vehicletype.getParameter(type_id, CLASS_PARAM) or type_id
            self._population_of[type_id] = population
        return settings.affiliation_by_type.get(population, settings.affiliation)

    @staticmethod
    def csv_summary_path(csv_path: Path) -> Path:
        """Where a CSV's summary is written: its name with `.summary.json` for its extension."""
        return Path(csv_path).with_suffix(CSV_SUMMARY_SUFFIX)

    @classmethod
    def write_csv_summary(cls, csv_path: Path, producer: dict) -> Path:
        """The CSV's format, its columns and what made it, beside it, so the CSV's own header stays
        the columns every reader of it already reads."""
        path = cls.csv_summary_path(csv_path)
        path.write_text(json.dumps({"format_version": CSV_FORMAT_VERSION, "producer": producer,
                                    "csv": Path(csv_path).name, "columns": CSV_COLUMNS},
                                   indent=2, ensure_ascii=False) + "\n", encoding="utf-8",
                        newline="\n")
        return path

    @staticmethod
    def _display_convention_xml(settings: CotOutputSettings) -> str:
        """The run's display convention, written once ahead of the events it drew.

        An affiliation is the run's choice and not the scenario's, so the file it styled says which
        convention was in force: without it, a population the convention drew neutral and one that
        took the default read the same.
        """
        element = ET.Element("_display_convention", {
            "source": settings.display_convention, "default_affiliation": settings.affiliation})
        for population, affiliation in sorted(settings.affiliation_by_type.items()):
            ET.SubElement(element, "population", {"type": population, "affiliation": affiliation})
        return ET.tostring(element, encoding="unicode")

    def _height_at(self, x: float, y: float) -> float:
        """Bare-earth height under a SUMO position, which is in the projection's metres.

        This is the one place a position crosses from SUMO's frame into the grid's, so it is the one
        place y changes sign. A vehicle outside the grid -- a map whose network runs past the
        terrain the world was generated over -- falls back to the configured constant, which is
        wrong by whatever the relief is, but is wrong in a way that is stated rather than disguised
        as a measurement.
        """
        if self.bare_earth:
            height = self.bare_earth.height_at(x, -y)
            if height is not None:
                return height
            self.off_grid_heights += 1
            if self.off_grid_heights == 1:
                self.logger.warning(
                    "a vehicle at SUMO (%.1f, %.1f) is outside the bare-earth grid; reporting the "
                    "configured %.1f m for it and for any others", x, y, self.constant_hae)
        return self.constant_hae

    @staticmethod
    def _published(record: dict) -> dict:
        """The part of a truth record a consumer may read: everything an observer could measure.

        This is where the two channels part. Nothing downstream removes anything, so nothing
        downstream can forget to.
        """
        return {name: value for name, value in record.items()
                if name not in AUTHORED_TRUTH_FIELDS}

    def _row(self, record: dict, settings: CotOutputSettings, affiliation: str,
             stamp: datetime, sim_time: float) -> dict:
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
            "marked": "1" if record["marked"] else "0",
            "edge": record["edge"],
            "lane": record["lane"],
            "sumo_x": f"{record['x']:.2f}",
            "sumo_y": f"{record['y']:.2f}",
            # CARLA negates Y against the projected frame SUMO works in.
            "carla_x": f"{record['x']:.2f}",
            "carla_y": f"{-record['y']:.2f}",
        }
