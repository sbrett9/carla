"""Build the vehicle catalogue by spawning every blueprint and measuring the body that appears.

A vehicle's dimensions do not exist before it is spawned. The parameter struct the content build reads
carries no dimension, the definition builder emits none, the RPC type has no slot for one, and the
bounding box is computed at registration over the already-spawned actor. So there is no cheaper source
than a spawn, and the catalogue is a build step against a running server rather than a projection of
the blueprint library.

The sweep has three passes over the same blueprint set:

  * **Dimensions.** Spawn one blueprint at a time at a fixed transform well clear of everything,
    read `2 x extent` and the box's own centre off the spawned actor, destroy it and confirm the
    destroy. One at a time, because concurrent spawns collide and a collision is indistinguishable
    from a blueprint that cannot spawn. The vehicle-actor count at the end must equal the count at
    the start, or nothing is written: a leaked actor may have turned a later spawn into a collision
    and the measurement taken against it into a fiction.
  * **Colour.** Spawn each blueprint again with a colour set, and watch the server log for the
    `MaterialNotFound` warning the body-colour code emits when it cannot find the material slot to
    paint. The warning is the only signal that a requested colour did not reach the body; the client
    is told nothing either way. With no log to read, the verdict is `unknown` and never `true`.
  * **Lamps.** The optical pass in `VehicleLampProbe`, which renders the vehicle and counts pixels,
    because the light-state read-back returns the command rather than the vehicle.

Nothing here infers a dimension, falls back to a default, or drops a blueprint that failed to spawn: a
failure is written into the catalogue as a failure, with its reason, and the consumers refuse to use
that entry rather than guessing what it would have measured.
"""
from __future__ import annotations

import hashlib
import json
import logging
import platform
import re
import time
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path

import carlanet

from carlacontrol.SumoVehicleTypeWriter import SumoVehicleTypeWriter
from carlacontrol.VehicleCatalogue import LAMP_NAMES, VehicleCatalogue
from carlacontrol.VehicleCatalogueValidator import VehicleCatalogueValidator
from carlacontrol.VehicleClassAssignment import VehicleClassAssignment
from carlacontrol.VehicleLampProbe import LampProbeResult, VehicleLampProbe

CATALOGUE_VERSION = 1
GENERATOR = "carlacontrol.VehicleCatalogueBuilder/1.0.0"

CATALOGUE_FILENAME = "vehicles.catalogue.json"
VEHICLE_TYPES_FILENAME = "vehicles.vtypes.rou.xml"
REPORT_FILENAME = "vehicle_catalogue_report.txt"
CORRECTED_PARAMETERS_FILENAME = "VehicleParameters.corrected.json"

# Height above the map's first spawn point at which every blueprint is measured. The bounding box is
# actor-local, so the pose does not enter the measurement; the altitude only guarantees that nothing
# is in the way and that no spawn is rejected for colliding with the last one.
SPAWN_ALTITUDE_M = 300.0

# The colour the colour pass requests. Any legal triple does: what is being measured is whether the
# request reaches the body at all, not which colour arrives.
COLOUR_PROBE_VALUE = "17,213,91"

# What the body-colour code prints when it cannot find the material slot to paint. Benign in itself --
# the vehicle spawns and drives -- and the only evidence that a set colour went nowhere.
MATERIAL_NOT_FOUND = "MaterialNotFound"

SPAWNING_ACTOR = re.compile(r"Spawning actor '([^']+)'")

logger = logging.getLogger(__name__)


@dataclass
class _ServerLogWindow:
    """The bytes a server wrote to its log while one thing was happening.

    Opened by remembering where the log ended before the spawn and reading forward from there
    afterwards, so a busy server's other messages do not have to be filtered by time.
    """

    path: Path | None
    start: int = 0

    def open(self) -> _ServerLogWindow:
        """Mark where the log currently ends."""
        if self.path is not None and self.path.exists():
            self.start = self.path.stat().st_size
        return self

    def read(self) -> str | None:
        """Everything appended since `open`, or None when there is no log to read."""
        if self.path is None or not self.path.exists():
            return None
        with self.path.open("rb") as handle:
            handle.seek(self.start)
            return handle.read().decode("utf-8", errors="replace")

    def await_destroy(self, actor_id: int, timeout_s: float = 10.0) -> str | None:
        """Wait until the window holds the destroy of one actor, then return it.

        The server writes its log through a buffer, so reading immediately after an RPC returns
        usually finds nothing. The destroy is the last line the spawn-to-destroy sequence produces,
        so its arrival is what says the window holds all of it. On a timeout the window is returned
        as it stands and the caller decides on incomplete evidence -- which for the colour verdict
        means `unknown`, never `true`.
        """
        deadline = time.monotonic() + timeout_s
        marker = f"destroy_actor {actor_id}"
        while True:
            window = self.read()
            if window is None or marker in window:
                return window
            if time.monotonic() >= deadline:
                logger.warning("the server log did not show %s within %.0f s", marker, timeout_s)
                return window
            time.sleep(0.1)


class VehicleCatalogueBuilder:
    """Sweeps a running server's vehicle blueprints and emits the catalogue they measure to."""

    def __init__(self, client, world, *, catalogue_id: str | None = None,
                 content_build_id: str | None = None, server_log: Path | None = None,
                 assignment: VehicleClassAssignment | None = None,
                 lamp_probe: VehicleLampProbe | None = None) -> None:
        self.client = client
        self.world = world
        self.server_version = client.get_server_version()
        default_id = f"carla-{self.server_version}-{platform.system().lower()}"
        self.catalogue_id = catalogue_id or default_id
        self.content_build_id = content_build_id or default_id
        self.server_log = Path(server_log) if server_log else None
        self.assignment = assignment or VehicleClassAssignment()
        self.lamp_probe = lamp_probe
        self.library = world.get_blueprint_library()
        self.definitions = sorted((bp for bp in self.library if bp.id.startswith("vehicle.")),
                                 key=lambda bp: bp.id)

    def build(self, *, probe_lamps: bool = True) -> dict:
        """Run every pass and return the finished, validated catalogue document."""
        transform = self._spawn_transform()
        before = self._vehicle_actor_count()
        vehicles = [self._measure(definition, transform) for definition in self.definitions]
        self._apply_colour_verdicts(vehicles, transform)
        after = self._vehicle_actor_count()
        if before != after:
            raise RuntimeError(
                f"the sweep started with {before} vehicle actors and ended with {after}; a leaked "
                "actor may have turned a later spawn into a collision, so no catalogue is written")

        lamp_result = self._probe_lamps(vehicles, transform) if probe_lamps else LampProbeResult(
            False, {}, "the lamp pass was not requested")
        for entry in vehicles:
            entry["lamp_capability"] = (lamp_result.for_blueprint(entry["blueprint_id"])
                                        if entry["measurement"] == "measured"
                                        else dict.fromkeys(LAMP_NAMES, "unknown"))

        measurements = {entry["blueprint_id"]: (entry["length_m"], entry["width_m"],
                                                entry["height_m"])
                        for entry in vehicles if entry["measurement"] == "measured"}
        self.class_records = self.assignment.assign(measurements)
        classes = self.assignment.build_classes(self.class_records)

        document = {
            "catalogue_version": CATALOGUE_VERSION,
            "catalogue_id": self.catalogue_id,
            "catalogue_digest": "",
            "content_build_id": self.content_build_id,
            "blueprint_set_digest": self._blueprint_set_digest(),
            "generated_at_utc": datetime.now(UTC).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z",
            "generator": GENERATOR,
            "server_version": self.server_version,
            "lamp_probe": self._lamp_probe_header(lamp_result),
            "vehicles": vehicles,
            "classes": classes,
        }
        VehicleCatalogueValidator(document).validate()
        document["catalogue_digest"] = VehicleCatalogue.digest_of(document)
        self.lamp_result = lamp_result
        return document

    def write(self, document: dict, output_directory: Path, schema_path: Path | None = None,
              vehicle_parameters: Path | None = None) -> dict[str, Path]:
        """Write every artifact the sweep produces and return where each one landed."""
        output_directory = Path(output_directory)
        output_directory.mkdir(parents=True, exist_ok=True)
        written: dict[str, Path] = {}

        catalogue_path = output_directory / CATALOGUE_FILENAME
        catalogue_path.write_text(VehicleCatalogue.canonical_json(document),
                                  encoding="utf-8", newline="\n")
        written["catalogue"] = catalogue_path

        writer = SumoVehicleTypeWriter(document)
        written["vehicle_types"] = writer.write(output_directory / VEHICLE_TYPES_FILENAME)
        if schema_path is not None:
            writer.validate(written["vehicle_types"], schema_path)

        report_path = output_directory / REPORT_FILENAME
        report_path.write_text(self.report(document), encoding="utf-8", newline="\n")
        written["report"] = report_path

        if vehicle_parameters is not None:
            corrected = self.corrected_vehicle_parameters(Path(vehicle_parameters))
            corrected_path = output_directory / CORRECTED_PARAMETERS_FILENAME
            # Tab-indented, which is how the content build writes the file, so the only difference a
            # reviewer sees is the metadata that changed.
            corrected_path.write_text(json.dumps(corrected, indent="\t") + "\n",
                                      encoding="utf-8", newline="\n")
            written["vehicle_parameters"] = corrected_path
        return written

    def _spawn_transform(self):
        """One fixed pose, far above the map's first spawn point, well clear of any other actor."""
        spawn_point = self.world.get_map().get_spawn_points()[0]
        return carlanet.Transform(
            carlanet.Location(spawn_point.location.x, spawn_point.location.y,
                              spawn_point.location.z + SPAWN_ALTITUDE_M),
            carlanet.Rotation(0.0, 0.0, 0.0))

    def _vehicle_actor_count(self) -> int:
        """How many vehicle actors the world holds, which the sweep must leave unchanged."""
        return sum(1 for actor in self.world.get_actors()
                   if actor.type_id.startswith("vehicle."))

    def _measure(self, definition, transform) -> dict:
        """Spawn one blueprint, read its box, destroy it, and record everything it declared."""
        attributes = {name: info["value"] for name, info in definition._attrs.items()}
        entry = {
            "blueprint_id": definition.id,
            "uid": definition._uid,
            "tags": definition.tags,
            "declared_base_type": attributes.get("base_type", ""),
            "declared_special_type": attributes.get("special_type", ""),
            "number_of_wheels": self._integer(attributes.get("number_of_wheels")),
            "generation": self._integer(attributes.get("generation")),
            "declared_has_lights": attributes.get("has_lights", "").lower() == "true",
            "settable_attributes": self._settable_attributes(definition),
            "colour_settable": "color" in definition._attrs,
            "colour_palette": list(definition._attrs.get("color", {}).get("recommended", [])),
            "colour_applied": "unknown",
        }
        try:
            actor = self.world.spawn_actor(definition, transform)
        except Exception as failure:
            logger.warning("%s did not spawn: %s", definition.id, failure)
            entry["measurement"] = "failed"
            entry["measurement_note"] = f"spawn refused: {failure}"
            return entry
        try:
            actor.set_simulate_physics(False)
            box = actor.bounding_box
            entry["measurement"] = "measured"
            entry["length_m"] = round(2.0 * box.extent.x, 4)
            entry["width_m"] = round(2.0 * box.extent.y, 4)
            entry["height_m"] = round(2.0 * box.extent.z, 4)
            entry["bbox_centre_m"] = [round(box.location.x, 4), round(box.location.y, 4),
                                      round(box.location.z, 4)]
            logger.info("%s measured %.3f x %.3f x %.3f m, box centre (%.3f, %.3f, %.3f)",
                        definition.id, entry["length_m"], entry["width_m"], entry["height_m"],
                        *entry["bbox_centre_m"])
        finally:
            if not actor.destroy():
                raise RuntimeError(
                    f"the server refused to destroy {definition.id} (actor {actor.id}); the next "
                    "spawn would be measured against a collision")
        return entry

    def _apply_colour_verdicts(self, vehicles: list[dict], transform) -> None:
        """Spawn each blueprint again with a colour set and record whether it reached the body."""
        if self.server_log is None or not self.server_log.exists():
            logger.warning("no server log to read, so every colour verdict stays 'unknown'")
            return
        for entry in vehicles:
            if entry["measurement"] != "measured" or not entry["colour_settable"]:
                continue
            definition = self.library.find(entry["blueprint_id"])
            definition.set_attribute("color", COLOUR_PROBE_VALUE)
            window = _ServerLogWindow(self.server_log).open()
            actor = self.world.spawn_actor(definition, transform)
            actor_id = actor.id
            try:
                actor.set_simulate_physics(False)
            finally:
                actor.destroy()
            entry["colour_applied"] = self._colour_verdict(
                window.await_destroy(actor_id), entry["blueprint_id"], actor_id)

    @staticmethod
    def _colour_verdict(log: str | None, blueprint_id: str, actor_id: int) -> str:
        """Read one spawn's log window: the warning means the colour went nowhere."""
        if log is None or f"destroy_actor {actor_id}" not in log:
            # Without the destroy the window may be short of the lines that matter, and an
            # absent warning in an incomplete window establishes nothing.
            return "unknown"
        spawned = False
        seen_this_spawn = False
        for line in log.splitlines():
            match = SPAWNING_ACTOR.search(line)
            if match:
                spawned = match.group(1) == blueprint_id
                seen_this_spawn = seen_this_spawn or spawned
            if spawned and MATERIAL_NOT_FOUND in line:
                return "false"
        if not seen_this_spawn:
            # The spawn this window was opened for is not in it, so the absence of the warning says
            # nothing -- the log may simply not have reached disk yet.
            return "unknown"
        return "true"

    def _probe_lamps(self, vehicles: list[dict], transform) -> LampProbeResult:
        """Run the optical lamp pass over every blueprint that measured."""
        probe = self.lamp_probe or VehicleLampProbe(self.world)
        measured = [entry["blueprint_id"] for entry in vehicles
                    if entry["measurement"] == "measured"]
        try:
            return probe.run(self.library, measured, transform)
        except Exception as failure:
            # The type as well as the message: a bare queue timeout stringifies to nothing at all,
            # and a reason of "" is indistinguishable from a pass that forgot to give one.
            reason = f"{type(failure).__name__}: {failure}".strip().rstrip(":")
            logger.warning("the lamp pass could not run: %s", reason)
            return LampProbeResult(False, probe.settings.as_header(), reason)

    @staticmethod
    def _lamp_probe_header(result: LampProbeResult) -> dict:
        """The pass's conditions, and whether it ran, for the catalogue header."""
        header = dict(result.header)
        header["ran"] = result.ran
        if result.reason:
            header["reason"] = result.reason
        return header

    @staticmethod
    def _settable_attributes(definition) -> list[dict]:
        """Every variation the definition declares, with its type and recommended values."""
        return [
            {"id": name, "type": info["type"],
             "restrict_to_recommended": not info["modifiable"],
             "recommended_values": list(info["recommended"])}
            for name, info in sorted(definition._attrs.items()) if info["modifiable"]
        ]

    @staticmethod
    def _integer(value: str | None) -> int:
        """A declared integer attribute, or -1 when the blueprint declares nothing readable."""
        try:
            return int(value)
        except (TypeError, ValueError):
            return -1

    def _blueprint_set_digest(self) -> str:
        """Digest over every definition and attribute the server declares, with no spawning.

        The one part of the catalogue a running server can reproduce for itself, which is how the
        bridge checks at run start that the content has not moved under the measurements.
        """
        rows = sorted(f"{blueprint.id}|{name}={info['value']}"
                      for blueprint in self.library
                      for name, info in blueprint._attrs.items())
        return hashlib.sha256("\n".join(rows).encode("utf-8")).hexdigest()

    def corrected_vehicle_parameters(self, source: Path) -> dict:
        """The content build's own vehicle metadata, with `BaseType` and `SpecialType` corrected.

        Emitted as a file for the next content build rather than applied here: this is content the
        engine reads at start-up, and the sweep's job is to say what it should contain, with the
        measurement that says so beside it.
        """
        document = json.loads(source.read_text(encoding="utf-8-sig"))
        for vehicle in document["Vehicles"]:
            blueprint_id = f"vehicle.{vehicle['Make']}.{vehicle['Model']}".lower()
            record = self.class_records.get(blueprint_id)
            if record is None:
                raise KeyError(
                    f"{source} declares {blueprint_id!r}, which the sweep did not measure; the "
                    "corrected file would silently keep its wrong metadata")
            vehicle["BaseType"] = record.base_type
            vehicle["SpecialType"] = record.special_type
        return document

    def report(self, document: dict) -> str:
        """The human-readable page: every measurement, and every place the content disagrees with it."""
        lines = [
            f"Vehicle catalogue {document['catalogue_id']}",
            f"  generated {document['generated_at_utc']} by {document['generator']}",
            f"  server {document['server_version']}, content build {document['content_build_id']}",
            f"  blueprint set digest {document['blueprint_set_digest']}",
            "",
            "Measured bodies. length, width and height are twice the spawned actor's bounding-box",
            "extent; the box centre is in the actor's own frame and is what the bumper-to-origin",
            "shift is computed from.",
            "",
            f"{'blueprint':32s} {'length':>7} {'width':>7} {'height':>7} {'centre x':>9}"
            f" {'centre y':>9} {'bumper':>7}  colour",
        ]
        for entry in document["vehicles"]:
            if entry["measurement"] != "measured":
                lines.append(f"{entry['blueprint_id']:32s}  FAILED: {entry['measurement_note']}")
                continue
            bumper = entry["length_m"] / 2.0 + entry["bbox_centre_m"][0]
            lines.append(
                f"{entry['blueprint_id']:32s} {entry['length_m']:7.3f} {entry['width_m']:7.3f}"
                f" {entry['height_m']:7.3f} {entry['bbox_centre_m'][0]:9.3f}"
                f" {entry['bbox_centre_m'][1]:9.3f} {bumper:7.3f}  {entry['colour_applied']}")
        lines += ["", "Where the content's own metadata disagrees with the measurement.", ""]
        for entry in document["vehicles"]:
            record = self.class_records.get(entry["blueprint_id"])
            if record is None:
                continue
            notes = []
            declared = entry["declared_base_type"]
            if declared != record.base_type:
                notes.append(f"base_type declared {declared or 'empty'!r}, measured "
                             f"{record.base_type!r}")
            if entry["declared_special_type"] != record.special_type:
                notes.append(f"special_type declared "
                             f"{entry['declared_special_type'] or 'empty'!r}, curated "
                             f"{record.special_type or 'empty'!r}")
            if record.override_reason:
                notes.append(f"curated: {record.override_reason}")
            if notes:
                lines.append(f"  {entry['blueprint_id']}: " + "; ".join(notes))
        lines += ["", "Lamp capability, measured optically.", ""]
        probe = document["lamp_probe"]
        if not probe.get("ran"):
            lines.append(f"  the pass did not run: {probe.get('reason', 'no reason recorded')}")
            lines.append("  every lamp is therefore recorded 'unknown', which is not 'unlit'.")
        else:
            lines.append(f"  sun at solar hour {probe['solar_time_hours']} on {probe['solar_date']},"
                         f" elevation {probe.get('sun_elevation_deg')} degrees")
            lines.append(f"  a pixel counts as lit above {probe['luminance_threshold']}/255 of gain;"
                         f" at least {probe['minimum_lit_pixels']} of them, and more than the"
                         " same-state control measured")
            lines.append(f"  positive control (sun moved to daylight):"
                         f" {probe.get('positive_control_pixels')} pixels")
            lines.append("  'unlit' means the lamp was commanded, the vehicle was rendered and the")
            lines.append("  image did not change. It does not say why, and it is not 'unknown'.")
            evidence = getattr(self, "lamp_result", None)
            for entry in document["vehicles"]:
                capability = entry.get("lamp_capability", {})
                lit = [lamp for lamp, verdict in capability.items() if verdict == "lit"]
                counts = (evidence.evidence.get(entry["blueprint_id"], {}) if evidence else {})
                floor = counts.get("noise_floor_pixels")
                needed = counts.get("decision_floor_pixels")
                best = max((counts.get(lamp, 0) for lamp in LAMP_NAMES), default=0)
                lines.append(
                    f"  {entry['blueprint_id']:32s} "
                    f"{'lit: ' + ', '.join(lit) if lit else 'no lamp changed the image'}"
                    f"  (strongest lamp {best} px, worst same-state control {floor} px,"
                    f" needed more than {needed} px)")
        return "\n".join(lines) + "\n"
