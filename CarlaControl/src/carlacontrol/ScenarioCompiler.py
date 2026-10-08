"""Compile a scenario specification against a world package into a scenario package, or refuse.

`07_Scenario_Authoring.md` D7.2: the specification is the artifact that is compiled, validated,
archived and run, and a generator writes a specification rather than SUMO XML, so every scenario --
hand-written or generated -- passes through this one compiler and this one set of checks. It consumes
the specification, the world package (its network, place index, area table and solar frame) and the
measured vehicle catalogue; it emits, beside one another:

| File | Content |
|---|---|
| `<scenario_id>.rou.xml` | vehicle types from the catalogue; every actor and flow, **already routed**, departure-sorted, times in plain seconds, no supervision |
| `<scenario_id>.sumocfg` | the run configuration: seed, step, end, and the options that decide the traffic; the epoch restated as a comment |
| `<scenario_id>.add.xml` | the specification's lane closures, as SUMO rerouters the configuration names; written only when it declares any |
| `<MapName>.net.xml` | the world package's own network, byte for byte -- the network SUMO runs is the world's |
| `<scenario_id>.supervision.json` | the supervision plan, the sole annotation channel |
| `<scenario_id>.resolution.json` and `.md` | what everything resolved to, and every warning in full |
| `<scenario_id>.lock.json` | the scenario id, the four-way digest binding, the world binding, the epoch and illumination default verbatim, the seed, the versions |

A refused compile writes only its resolution report, marked refused, so the refusals can be read; no
scenario file is written. Checks are identified by the stable ids of `ScenarioCheckCatalogue`.

**The stages run in the plan's order** (§5.4): the specification's shape; the world binding; resolution
of the epoch, the instants, the places and the vehicles; routes; supervision; the epoch and illumination
group; emission and its self-checks; a SUMO-only run of what is about to be written; and the writing.
Checks inside a stage all run, so one compile reports every failure a stage can see; a stage that
refused stops the compile, because what follows depends on it.

**A scenario whose planned vehicles do not all get in is refused** (check 59). SUMO inserts a vehicle
only when its entrance has room and discards one that has waited `max-depart-delay`, both
deterministically, so the compiler runs the compiled files in SUMO alone over the whole span before
writing them, refuses a vehicle the supervision plan names that never enters, and reports every planned
vehicle's wait, the other vehicles discarded and every collision (`SumoDryRun`). The lock records that it
ran and what it counted, or that the author skipped it.

**Reproducible traffic is the point of the lock.** Everything that decides how the traffic is placed
and moves -- the routed routes, the network, the SUMO seed, the step length, the processing options,
the vehicle types and the SUMO release that routed them -- is written into the scenario files and
digested in the lock, so the same specification, seed and world give the same traffic.

**The SUMO that routes is the world's converter's release, or the compile is refused** (check 6), by
the comparison the co-simulation session refuses a mismatched SUMO with (`CarlaNet.Sumo.SumoRelease`,
reached through `SumoInstallation.release_check`): a different `duarouter` release can route the same
demand differently. `allow_sumo_version_mismatch` compiles anyway, warns, and records the acceptance in
the lock; a world that records no converter warns and compiles.

**A lane closure is the one thing the route file cannot carry.** SUMO closes a lane with a rerouter's
`closingLaneReroute`, an element of an additional file, so a specification declaring `lane_closures`
compiles into one more file, named by the configuration and digested in the lock with the others. A
closed lane admits only class `authority` for the closure's interval; the edge stays open on its other
lanes, so the routes fixed at compile time still hold, and traffic arriving faster than the open lanes
carry queues behind the closure and drains once it lifts.
"""
from __future__ import annotations

import copy
import hashlib
import json
import logging
import re
import xml.etree.ElementTree as ET
from pathlib import Path
from xml.sax.saxutils import quoteattr

import carlanet  # noqa: F401  -- loads the CarlaNet assemblies the next import names
from CarlaNet.CoSim import CoSimSessionRefusedException, IlluminationPolicy
from CarlaNet.Sumo import SumoReleaseAgreement
from lxml import etree

from carlacontrol.AnnotationVocabulary import AnnotationVocabulary
from carlacontrol.CivilTimeResolver import CivilTimeResolver, ResolvedInstant
from carlacontrol.CompileFindings import CompileFindings
from carlacontrol.CompileResult import CompileResult
from carlacontrol.IlluminationBand import BAND_SOURCE, IlluminationBand
from carlacontrol.IlluminationLabelAssociation import (
    AssociationEntry,
    IlluminationLabelAssociation,
)
from carlacontrol.NetworkFingerprint import NetworkFingerprint
from carlacontrol.PlaceResolver import PlaceResolver
from carlacontrol.PortablePath import PortablePath
from carlacontrol.ResolutionReport import ResolutionReport
from carlacontrol.RotaExpander import RotaEntry, RotaExpander, RotaSkip
from carlacontrol.RouteValidator import RouteRequest, RouteValidator
from carlacontrol.ScenarioEpoch import ScenarioEpoch, ScenarioEpochRefusedError
from carlacontrol.ScenarioSchema import SPEC_VERSION, ScenarioSchema
from carlacontrol.ScenarioVehicleMix import ScenarioVehicleMix, VehicleClassSpec, VehicleMixSpec
from carlacontrol.SumoDryRun import PlannedVehicle, SumoDryRun
from carlacontrol.SumoInstallation import SumoInstallation
from carlacontrol.SumoVehicleTypeWriter import (
    CATALOGUE_DIGEST_PARAM,
    CLASS_PARAM,
    ROUTES_SCHEMA_RELATIVE_PATH,
)
from carlacontrol.SupervisionPlanCompiler import (
    SUPERVISION_PLAN_VERSION,
    SupervisionInputs,
    SupervisionPlanCompiler,
)
from carlacontrol.VehicleCatalogue import BLUEPRINT_PARAM, VehicleCatalogue
from carlacontrol.version import __version__
from carlacontrol.WindowSun import WindowSun
from carlacontrol.WorldPackageReader import WorldPackageReader

COMPILER = "carlacontrol.ScenarioCompiler"
LOCK_VERSION = 1

# The SUMO processing options the compiler writes. Each decides how the traffic moves, so each is
# fixed here, written into the configuration and recorded in the lock rather than left to a default.
PROCESSING_OPTIONS = {
    # A teleport is a position jump nothing downstream can reproduce; -1 forbids it and lets a jam
    # stay a jam (07 §7.1).
    "time-to-teleport": "-1",
    "max-depart-delay": "900",
    "collision.action": "warn",
    # A lane change takes this long, the vehicle moving sideways at a steady rate, where SUMO's
    # default of 0 crosses a lane width inside one step and renders as a sideways jump (06 §6.2).
    # 3 s is a physical lane-change time for a passenger car, held for every vehicle (04 §5.3).
    "lanechange.duration": "3",
}

# The only <param> keys the emitted route file may carry: the vehicle-type binding's. Anything else
# would be a second channel beside the supervision plan (check 52).
ALLOWED_PARAMS = frozenset({BLUEPRINT_PARAM, CLASS_PARAM, CATALOGUE_DIGEST_PARAM})

DEFAULT_DEPART_LANE = "best"
DEFAULT_DEPART_SPEED = "max"

# What the lock says of a compile whose SUMO-only run was skipped (check 59).
SKIPPED_DRY_RUN = ("skipped at the author's request: nothing established that every vehicle the plan "
                   "names enters the run")

# SUMO's schema for an additional file, beside its route schema in the installation's data directory.
ADDITIONAL_SCHEMA_RELATIVE_PATH = Path("xsd") / "additional_file.xsd"

# The only vehicle class a closed lane admits while its closure lasts: the emergency services, which
# an incident does not close a road to. A scenario whose own traffic is of this class drives through
# its closures.
CLOSED_LANE_ALLOWS = "authority"

# A whole attribute value that is a clock, which SUMO would read as an offset from t = 0 (check 44).
_CLOCK_VALUE = re.compile(r"^\s*\d+:\d{2}(:\d{2}(\.\d+)?)?\s*$")
SUBJECT_TOKEN = "$subject"

logger = logging.getLogger(__name__)


class ScenarioCompiler:
    """Compiles one specification into one scenario package."""

    def __init__(self, installation: SumoInstallation,
                 allow_sumo_version_mismatch: bool = False, skip_dry_run: bool = False) -> None:
        self.installation = installation
        # Accepting a routing SUMO other than the world's converter is the operator's explicit
        # decision, and the lock says it was taken (check 6).
        self.allow_sumo_version_mismatch = bool(allow_sumo_version_mismatch)
        # Skipping the SUMO-only run is for quick iteration on a draft, and the lock says it was
        # skipped (check 59).
        self.skip_dry_run = bool(skip_dry_run)

    # =============================================================================================
    def compile(self, spec_path: str | Path, out_dir: str | Path) -> CompileResult:
        """Compile, write what the outcome allows, and return the result."""
        self.spec_path = Path(spec_path).resolve()
        self.base = self.spec_path.parent
        self.out_dir = Path(out_dir)
        self.findings = CompileFindings()
        self.report = ResolutionReport()
        self.result = CompileResult(self.findings)
        self.spec: dict = {}
        stages = (self._stage_specification, self._stage_world_binding, self._stage_resolution,
                  self._stage_routes, self._stage_supervision, self._stage_epoch_and_illumination,
                  self._stage_emission, self._stage_dry_run, self._stage_write)
        for stage in stages:
            stage()
            if self.findings.refused:
                break
        self._write_report()
        return self.result

    # -- stage: the specification's shape --------------------------------------------------------
    def _stage_specification(self) -> None:
        raw = self.spec_path.read_bytes()
        self.spec_sha256 = hashlib.sha256(raw).hexdigest()
        try:
            self.spec = json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, ValueError) as problem:
            self.findings.refuse(53, self.spec_path.name, f"is not JSON: {problem}")
            return
        for problem in ScenarioSchema.validate(self.spec):
            self.findings.refuse(53, "specification", problem)
        self.scenario_id = str(self.spec.get("scenario_id", ""))
        self.report.set("scenario", {"scenario_id": self.scenario_id,
                                     "scenario_name": self.spec.get("scenario_name"),
                                     "description": self.spec.get("description"),
                                     "specification": self.spec_path.name,
                                     "specification_sha256": self.spec_sha256,
                                     "spec_version": SPEC_VERSION})

    # -- stage: world binding -------------------------------------------------------------------------
    def _stage_world_binding(self) -> None:
        world = self.spec["world"]
        package_path = (self.base / world["package"]).resolve()
        try:
            self.package = WorldPackageReader(package_path)
            self.network_text = self.package.network_text()
        except (FileNotFoundError, ValueError) as problem:
            self.findings.refuse(1, "world", str(problem))
            return
        manifest = self.package.manifest
        carried = NetworkFingerprint.of_text(self.network_text)
        recorded = self.package.recorded_network_fingerprint
        self.network_fingerprint = carried
        if world["network_fingerprint"] != carried:
            self.findings.refuse(1, "world", f"the specification was authored against network "
                                 f"{world['network_fingerprint']}; {package_path.name} carries "
                                 f"{carried}. Recompile against the world that will render")
        argv = self.package.netconvert_argv
        if not recorded or not argv:
            self.findings.refuse(2, "world", f"{package_path.name} records no network fingerprint or "
                                 "netconvert invocation, so nothing establishes that its network and "
                                 "OpenDRIVE came from one run; rebuild the world")
        else:
            if recorded != carried:
                self.findings.refuse(2, "world", f"{package_path.name} records network {recorded} and "
                                     f"carries {carried}: assembled from two builds")
            if "--output-file" not in argv or "--opendrive-output" not in argv:
                self.findings.refuse(2, "world", "the recorded netconvert invocation did not write "
                                     "both the network and the OpenDRIVE")
        root = ET.fromstring(self.network_text)
        location = root.find("location")
        location = location.attrib if location is not None else {}
        header = ET.fromstring(self.package.open_drive_text()).find("header")
        header = header.attrib if header is not None else {}
        try:
            west, south, east, north = (float(v) for v in location.get("convBoundary").split(","))
            declared = [float(header[k]) for k in ("west", "south", "east", "north")]
            if any(abs(a - b) > 0.005 for a, b in zip((west, south, east, north), declared,
                                                     strict=True)):
                self.findings.refuse(3, "world", f"the network's convBoundary "
                                     f"{location.get('convBoundary')} is not the OpenDRIVE header's "
                                     f"west/south/east/north {declared}")
        except (AttributeError, KeyError, ValueError):
            self.findings.refuse(3, "world", "the network's convBoundary or the OpenDRIVE header's "
                                 "bounds cannot be read")
        georeference = manifest.get("GeoReferenceString", "")
        if " ".join(location.get("projParameter", "").split()) != " ".join(georeference.split()):
            self.findings.refuse(4, "world", f"the network projects as "
                                 f"'{location.get('projParameter')}' and world.json's "
                                 f"GeoReferenceString is '{georeference}'")
        offset = [float(v) for v in location.get("netOffset", "0,0").split(",")[:2]]
        if any(offset):
            self.findings.refuse(5, "world", f"the network carries netOffset {offset}, so its meters "
                                 "are displaced from the world's geographic frame (a road-offset "
                                 "build). Rebuild the world without a road offset to compile a "
                                 "scenario against it")
        built_with = self.package.netconvert_version
        self._check_routing_release(built_with)
        self.report.set("world", {
            "package": package_path.name, "map_name": self.package.map_name,
            "network_fingerprint": carried, "netconvert_version": built_with,
            "origin": list(self.package.origin), "georeference": georeference,
            "routing_sumo": {"version": self.installation.version or "",
                             "matched_by": self.installation.source,
                             "release_agreement": self.release_agreement,
                             "verdict": str(self.release_check.Verdict)}})

    def _check_routing_release(self, built_with: str) -> None:
        """Check 6: the SUMO that routes is the release that converted the world."""
        self.release_check = self.installation.release_check(built_with or None,
                                                             self.allow_sumo_version_mismatch)
        agreement = self.release_check.Agreement
        self.release_agreement = str(agreement)
        installation = (f"SUMO {self.installation.version or 'of an unreadable release'} "
                        f"(matched by {self.installation.source})")
        if agreement == SumoReleaseAgreement.Mismatch:
            self.findings.refuse(6, "world", f"the world was converted by '{built_with}' and the "
                                 f"routes would be routed by {installation}. A different duarouter "
                                 "release can route the same demand differently, so the traffic "
                                 "would not be the world's. Name the world's release with "
                                 "--sumo-home, or accept the difference with "
                                 "--allow-sumo-version-mismatch, which the lock records")
        elif agreement == SumoReleaseAgreement.MismatchAccepted:
            self.findings.warn(6, "world", f"the world was converted by '{built_with}' and the routes "
                               f"are routed by {installation}; compiled because the mismatch was "
                               "explicitly accepted, and the lock records that it was")
        elif agreement == SumoReleaseAgreement.NotRecorded:
            self.findings.warn(6, "world", f"the world package records no converter, so the routes "
                               f"routed by {installation} are unchecked against it; rebuild the "
                               "world to record one")

    # -- stage: resolution ------------------------------------------------------------------------------
    def _stage_resolution(self) -> None:
        self._resolve_epoch()
        self._resolve_illumination()
        if self.findings.refused:
            return
        simulation = self.spec["simulation"]
        span = CivilTimeResolver(self.epoch, self.findings, instants=None, span_end_s=None)
        end = span.instant(simulation["end"], "simulation end")
        if end is None:
            return
        if end.seconds <= 0:
            self.findings.refuse(37, "simulation end", "the run must end after t = 0")
            return
        self.end = end
        self.step_length = float(simulation["step_length_s"])
        self.resolver = CivilTimeResolver(self.epoch, self.findings,
                                          instants=self.spec.get("instants", {}),
                                          span_end_s=end.seconds)
        self.instants = self.resolver.resolve_declared_instants()
        self._resolve_places()
        self._resolve_vehicles()
        self._resolve_rotas()
        self._resolve_actors()
        self._resolve_flows()
        self._resolve_lane_closures()
        self._check_unique_ids()
        self.report.set("epoch", {
            "declared": self.epoch.declaration, "epoch_block_sha256": self.epoch.digest,
            "statement": f"{self.epoch.describe()}; the run ends at {end.civil}",
            "t0_civil": self.epoch.civil_instant_at(0), "end_s": end.seconds, "end_civil": end.civil,
            "time_zone_id": self.epoch.time_zone_id, "time_zone_id_resolved": False})
        self.report.set("instants", {name: r.to_dict() for name, r in self.instants.items()})
        if self.spec.get("lane_closures"):
            self.report.set("lane_closures", [self._closure_report(c) for c in self.lane_closures])

    def _resolve_epoch(self) -> None:
        try:
            self.epoch = ScenarioEpoch.read(self.spec.get("epoch"))
        except ScenarioEpochRefusedError as refused:
            for check, text in refused.problems:
                self.findings.refuse(check, "epoch", text)

    def _resolve_illumination(self) -> None:
        declared = self.spec.get("illumination")
        if declared is None:
            self.findings.refuse(39, "illumination", "the specification declares no illumination "
                                 "default. There is none to assume: a frozen run and an "
                                 "unconfigured one write identical records; "
                                 "freeze_at_window_start is the recommended value")
            return
        try:
            self.policy = IlluminationPolicy.FromJson(json.dumps(declared))
        except CoSimSessionRefusedException as refused:
            self.findings.refuse(39, "illumination", str(refused.Message))

    def _resolve_places(self) -> None:
        try:
            table = self.package.areas_of_interest() or {}
        except ValueError as problem:
            self.findings.refuse(20, "world", str(problem))
            table = {}
        self.areas = {area["id"]: area for area in table.get("areas", [])}
        self.places = PlaceResolver(self.network_text, self.package.place_index(), self.areas,
                                    self.findings, origin=self.package.origin)
        resolved = self.places.resolve_all(self.spec.get("places", {}))
        self.report.set("places", {name: self._place_report(p) for name, p in resolved.items()})
        self.place_sets = self.spec.get("place_sets", {})
        for name, members in self.place_sets.items():
            for member in members:
                if member not in self.places.declared:
                    self.findings.refuse(8, f"place_set {name}", f"names place '{member}', which "
                                         "places does not declare")

    def _place_report(self, place) -> dict:
        entry = place.to_dict()
        entry["edge_details"] = []
        for edge in place.edges:
            lane = self.places.lanes[self.places.edge_lanes[edge][0]]
            entry["edge_details"].append({"edge": edge, "street": self.places.edge_names.get(edge, ""),
                                          "length_m": lane.length, "speed_mps": lane.speed})
        return entry

    def _resolve_vehicles(self) -> None:
        """Bind the declared classes and mixes to measured bodies (checks 14, 17).

        What a type may drive is the declaration's, not the catalogue's, so it is recorded before the
        bodies are looked up: a class naming a body the catalogue lacks still has its type and its
        routes checked (checks 16 and 10), and one compile reports every refusal the stage can see.
        """
        classes = [VehicleClassSpec(
            class_id=c["class_id"], blueprints=tuple(c["blueprints"]), sumo_vclass=c["sumo_vclass"],
            behaviour=dict(c.get("behaviour", {})), share=float(c.get("share", 0.0)),
            weights=tuple(float(w) for w in c.get("weights", [])),
            gui_shape=c.get("gui_shape", ""), gui_colour=c.get("gui_colour", ""),
            note=c.get("note", "")) for c in self.spec["vehicle_classes"]]
        mixes = [VehicleMixSpec(mix_id=m["id"],
                                shares=tuple((k, float(v)) for k, v in m["shares"].items()),
                                note=m.get("note", ""))
                 for m in self.spec.get("vehicle_mixes", [])]
        self.mix_id = self.spec.get("vehicle_mix", "")
        vclass_of = {entry.class_id: entry.sumo_vclass for entry in classes}
        self.type_vclasses: dict[str, set[str]] = {}
        for entry in classes:
            self.type_vclasses[entry.class_id] = {entry.sumo_vclass}
            for blueprint in entry.blueprints:
                self.type_vclasses[ScenarioVehicleMix.type_id(entry.class_id, blueprint)] = {
                    entry.sumo_vclass}
            if len(entry.blueprints) == 1:
                self.findings.warn(17, f"vehicle class {entry.class_id}",
                                   f"draws one body, {entry.blueprints[0]}, so every vehicle of the "
                                   "class looks the same and its appearance can become its label")
        if self.mix_id:
            self.type_vclasses[self.mix_id] = {e.sumo_vclass for e in classes if e.share > 0}
        for mix in mixes:
            self.type_vclasses[mix.mix_id] = {vclass_of[c] for c, _ in mix.shares if c in vclass_of}

        catalogue_path = (self.base / self.spec["catalogue"]).resolve()
        try:
            self.catalogue = VehicleCatalogue.load(catalogue_path)
        except (OSError, ValueError) as problem:
            self.findings.refuse(14, "catalogue", f"{catalogue_path.name} cannot be read: {problem}")
            return
        try:
            self.mix = ScenarioVehicleMix(self.catalogue, classes, mix_id=self.mix_id, mixes=mixes)
        except (LookupError, ValueError) as problem:
            self.findings.refuse(14, "vehicle_classes", str(problem))
            return
        self.report.set("vehicle_types", {
            "catalogue": {"path": catalogue_path.name, "catalogue_id": self.catalogue.catalogue_id,
                          "catalogue_digest": self.catalogue.catalogue_digest,
                          "blueprint_set_digest": self.catalogue.blueprint_set_digest},
            "classes": self.mix.summary(),
            "mix": {"id": self.mix_id, "probabilities": self.mix.member_probabilities()},
            "mixes": [{"id": mix.mix_id, "shares": dict(mix.shares),
                       "probabilities": self.mix.mix_probabilities(mix)} for mix in mixes],
            "types": {self.mix.type_id(e.class_id, b): self._body(b) for e in classes
                      for b in e.blueprints}})

    def _body(self, blueprint: str) -> dict:
        extent = self.catalogue.extent_of(blueprint)
        # The box's width, mirrors included, is the drawn body's; the width without them is SUMO's.
        # The lights are information for the author: whether the body's headlights, brake lights and
        # turn signals light up when the session drives them, from the catalogue's optical pass.
        return {"blueprint": blueprint, "length_m": extent.length_m, "width_m": extent.width_m,
                "body_width_m": extent.body_width_m, "height_m": extent.height_m,
                "lights": self.catalogue.lights_of(blueprint)}

    def _resolve_rotas(self) -> None:
        self.rota_entries: dict[str, list[RotaEntry]] = {}
        self.rota_skips: dict[str, list[RotaSkip]] = {}
        self.rota_templates: dict[str, dict] = {}
        expander = RotaExpander(self.resolver, self.findings)
        report = []
        for rota in self.spec.get("rotas", []):
            rota_id = rota["id"]
            subjects = rota["subjects"]
            if isinstance(subjects, dict):
                set_name = subjects["place_set"]
                if set_name not in self.place_sets:
                    self.findings.refuse(8, f"rotas[{rota_id}]", f"names place_set '{set_name}', "
                                         "which place_sets does not declare")
                    continue
                subjects = self.place_sets[set_name]
            entries, skips = expander.expand(rota, list(subjects))
            self.rota_entries[rota_id] = entries
            self.rota_skips[rota_id] = skips
            self.rota_templates[rota_id] = rota["template"]
            report.append({"id": rota_id, "entries": len(entries),
                           "skips": [{"entry": s.entry_id, "seconds": s.depart.seconds,
                                      "civil": s.depart.civil, "because": s.because}
                                     for s in skips]})
        self.report.set("rotas", report)

    def _resolve_actors(self) -> None:
        self.actors: list[dict] = []
        for declared in self.spec.get("actors", []):
            where = f"actor {declared['id']}"
            depart = self.resolver.instant(declared["depart"], f"{where} depart")
            if depart is not None and self.resolver.require_in_span(depart, f"{where} depart"):
                self._add_actor(declared, declared["id"], depart, where, "actor")
        for rota_id, entries in self.rota_entries.items():
            template = self.rota_templates[rota_id]
            for entry in entries:
                body = self._substitute(template, entry.subject)
                where = f"rotas[{rota_id}] entry {entry.entry_id}"
                if self.resolver.require_in_span(entry.depart, f"{where} depart"):
                    self._add_actor(body, entry.entry_id, entry.depart, where, f"rota:{rota_id}")

    @staticmethod
    def _substitute(template: dict, subject: str) -> dict:
        body = copy.deepcopy(template)

        def swap(value):
            if isinstance(value, str):
                return subject if value == SUBJECT_TOKEN else value
            if isinstance(value, list):
                return [swap(item) for item in value]
            if isinstance(value, dict):
                return {key: swap(item) for key, item in value.items()}
            return value
        return swap(body)

    def _add_actor(self, declared: dict, actor_id: str, depart: ResolvedInstant, where: str,
                   origin: str) -> None:
        type_id = declared["type"]
        self._check_type(type_id, where)
        actor = {"id": actor_id, "type": type_id, "depart": depart, "origin": origin, "where": where,
                 "depart_lane": declared.get("depart_lane", DEFAULT_DEPART_LANE),
                 "depart_speed": declared.get("depart_speed", DEFAULT_DEPART_SPEED),
                 "arrival_speed": declared.get("arrival_speed"), "stops": [], "route": None,
                 "waypoints": [], "phases": [], "phase_entries": []}
        if "phases" in declared:
            if not self._resolve_phases(declared, actor, where):
                return
        elif "route" in declared:
            if any(key in declared for key in ("from", "to", "via")):
                self.findings.refuse(53, where, "gives both an explicit route and from/to/via")
                return
            edges = [self.places.single_edge(name, f"{where} route") for name in declared["route"]]
            if None in edges:
                return
            for a, b in self.places.unconnected(edges):
                self.findings.refuse(13, where, f"route has no connection from {a} to {b}")
            actor["route"] = edges
            actor["from"], actor["to"], actor["via"] = edges[0], edges[-1], []
        else:
            if "from" not in declared or "to" not in declared:
                self.findings.refuse(53, where, "gives neither an explicit route nor both from and to")
                return
            actor["from"] = self.places.single_edge(declared["from"], f"{where} from")
            actor["to"] = self.places.single_edge(declared["to"], f"{where} to")
            actor["via"] = self._via(declared.get("via", []), where)
            if actor["from"] is None or actor["to"] is None or None in actor["via"]:
                return
        dwell = 0.0
        for index, stop in enumerate(declared.get("stops", [])):
            stop_where = f"{where} stop {index}"
            position = self.places.stop_position(stop["place"], stop_where)
            if position is None:
                continue
            lane, start, end = position
            record = {"place": stop["place"], "lane": lane, "start_pos": start, "end_pos": end,
                      "parking": bool(stop.get("parking", False))}
            if ("duration" in stop) == ("until" in stop):
                self.findings.refuse(47, stop_where, "gives exactly one of duration and until")
                continue
            if "duration" in stop:
                seconds = self.resolver.duration(stop["duration"], f"{stop_where} duration")
                if seconds is None:
                    continue
                record["duration"] = seconds
                dwell += seconds
            else:
                until = self.resolver.instant(stop["until"], f"{stop_where} until")
                if until is None or not self.resolver.require_in_span(until, f"{stop_where} until"):
                    continue
                record["until"] = until
            actor["stops"].append(record)
        self._check_permissions(actor)
        if depart.seconds + dwell > self.end.seconds:
            self.findings.warn(31, where, f"departs at {depart.civil} and stops for {dwell:g} s, so "
                               f"it is still out when the run ends at {self.end.civil}")
        self.actors.append(actor)

    def _resolve_phases(self, declared: dict, actor: dict, where: str) -> bool:
        """An explicit route in phases, each driven `repeat` times, a held phase waypointed per edge.

        A SUMO waypoint -- a `<stop>` carrying `speed` -- holds a vehicle to that speed only between
        its own start and end on its own edge (07 §6 gotcha 1), so holding a phase to a speed is one
        waypoint spanning the whole of each of its edges, on the edge's first lane. `hold` is a speed
        in m/s capped at each edge's limit, or `posted`, each edge's own limit. A stop is refused
        beside phases: on a repeated route it names no one pass.

        Where each phase is entered is recorded as the index in the compiled route of its first
        edge's first pass, which is what SUMO reports a vehicle's progress by, so an interval can be
        anchored to entering it (06 §3.3).
        """
        if any(key in declared for key in ("route", "from", "to", "via")):
            self.findings.refuse(53, where, "gives phases and also route or from/to/via; an actor's "
                                 "route is given one way")
            return False
        if declared.get("stops"):
            self.findings.refuse(53, where, "gives stops beside phases; a stop on a repeated route "
                                 "names no one pass of it")
            return False
        edges: list[str] = []
        for index, phase in enumerate(declared["phases"]):
            phase_edges = [self.places.single_edge(name, f"{where} phase {index}")
                           for name in phase["route"]]
            if None in phase_edges:
                return False
            repeat = int(phase.get("repeat", 1))
            hold = phase.get("hold")
            actor["phases"].append({"edges": len(phase_edges), "repeat": repeat, "hold": hold})
            actor["phase_entries"].append({"route_index": len(edges), "edge": phase_edges[0]})
            for _ in range(repeat):
                edges.extend(phase_edges)
                if hold is None:
                    continue
                for edge in phase_edges:
                    lane_id = self.places.edge_lanes[edge][0]
                    lane = self.places.lanes[lane_id]
                    speed = lane.speed if hold == "posted" else min(float(hold), lane.speed)
                    actor["waypoints"].append({"lane": lane_id, "end_pos": lane.length,
                                               "speed": speed})
        for a, b in self.places.unconnected(edges):
            self.findings.refuse(13, where, f"route has no connection from {a} to {b}")
        actor["route"] = edges
        actor["from"], actor["to"], actor["via"] = edges[0], edges[-1], []
        return True

    def _resolve_flows(self) -> None:
        self.flows: list[dict] = []
        for declared in self.spec.get("flows", []):
            where = f"flow {declared['id']}"
            self._check_type(declared["type"], where)
            begin = self.resolver.instant(declared["begin"], f"{where} begin")
            end = self.resolver.instant(declared["end"], f"{where} end")
            edges = [self.places.single_edge(declared["from"], f"{where} from"),
                     self.places.single_edge(declared["to"], f"{where} to")]
            via = self._via(declared.get("via", []), where)
            if begin is None or end is None or None in edges or None in via:
                continue
            if end.seconds <= begin.seconds:
                self.findings.refuse(47, where, f"ends at {end.civil}, not after it begins at "
                                     f"{begin.civil}")
                continue
            if begin.seconds >= self.end.seconds:
                self.findings.warn(32, where, f"begins at {begin.civil}, when the run has ended; it "
                                   "never fires")
            elif end.seconds > self.end.seconds:
                self.findings.warn(32, where, f"runs to {end.civil}, after the run ends at "
                                   f"{self.end.civil}")
            flow = {"id": declared["id"], "type": declared["type"], "begin": begin, "end": end,
                    "vehs_per_hour": float(declared["vehs_per_hour"]), "from": edges[0],
                    "to": edges[1], "via": via, "stops": [], "route": None, "where": where,
                    "depart_lane": declared.get("depart_lane", DEFAULT_DEPART_LANE),
                    "depart_speed": declared.get("depart_speed", DEFAULT_DEPART_SPEED)}
            self._check_permissions(flow)
            self.flows.append(flow)

    def _resolve_lane_closures(self) -> None:
        """Each declared closure: the edge its place names, the lanes that close, where vehicles
        learn of it, and its window, which must lie inside the run (checks 7, 8, 53, 47, 37).

        A lane is named by its index on the edge, 0 the rightmost, as SUMO numbers it, so a closure
        reads as "five of six lanes" rather than as five lane ids; an index the edge does not have
        is a place that does not resolve (check 7). `notify` defaults to the closed edge itself.
        """
        self.lane_closures: list[dict] = []
        for declared in self.spec.get("lane_closures", []):
            where = f"lane closure {declared['id']}"
            edge = self.places.single_edge(declared["place"], f"{where} place")
            notify = self._via(declared.get("notify", []), where)
            begin = self.resolver.instant(declared["begin"], f"{where} begin")
            end = self.resolver.instant(declared["end"], f"{where} end")
            if edge is None or None in notify or begin is None or end is None:
                continue
            lanes = self.places.edge_lanes[edge]
            indices = list(declared["lanes"])
            beyond = sorted({index for index in indices if index >= len(lanes)})
            if beyond:
                self.findings.refuse(7, where, f"closes lane {', '.join(map(str, beyond))} of {edge}, "
                                     f"which has {len(lanes)} lane{'s' if len(lanes) != 1 else ''}, "
                                     f"numbered 0 to {len(lanes) - 1} from the right")
            repeated = sorted({index for index in indices if indices.count(index) > 1})
            if repeated:
                self.findings.refuse(53, where, f"names lane {', '.join(map(str, repeated))} more "
                                     "than once")
            ordered = end.seconds > begin.seconds
            if not ordered:
                self.findings.refuse(47, where, f"ends at {end.civil}, not after it begins at "
                                     f"{begin.civil}")
            inside = [self.resolver.require_in_span(begin, f"{where} begin"),
                      self.resolver.require_in_span(end, f"{where} end")]
            if beyond or repeated or not ordered or not all(inside):
                continue
            self.lane_closures.append({
                "id": declared["id"], "where": where, "edge": edge, "street":
                self.places.edge_names.get(edge, ""), "lane_count": len(lanes),
                "indices": sorted(indices), "lanes": [lanes[index] for index in sorted(indices)],
                "notify": list(dict.fromkeys(notify)) or [edge], "begin": begin, "end": end})

    def _closure_report(self, closure: dict) -> dict:
        return {"id": closure["id"], "edge": closure["edge"], "street": closure["street"],
                "lanes": closure["lanes"], "open_lanes": closure["lane_count"] - len(closure["lanes"]),
                "notify": closure["notify"], "allow": CLOSED_LANE_ALLOWS,
                "begin": closure["begin"].to_dict(), "end": closure["end"].to_dict()}

    def _via(self, names: list[str], where: str) -> list[str | None]:
        """The edges a via list names, in order: a junction movement gives its two, any other place
        its one, and a place that did not resolve a None, so the caller can see it failed."""
        edges: list[str | None] = []
        for name in names:
            resolved = self.places.via_edges(name, f"{where} via")
            edges.extend([None] if resolved is None else resolved)
        return edges

    def _check_type(self, type_id: str, where: str) -> None:
        if not hasattr(self, "type_vclasses"):
            return
        if type_id not in self.type_vclasses:
            self.findings.refuse(16, where, f"type '{type_id}' is not a declared vehicle class, one "
                                 f"of its member types, or the mix; declared: "
                                 f"{', '.join(sorted(self.type_vclasses))}")

    def _check_permissions(self, vehicle: dict, routed: tuple[str, ...] | None = None) -> None:
        vclasses = getattr(self, "type_vclasses", {}).get(vehicle["type"], set())
        edges = list(routed) if routed else [vehicle["from"], *vehicle["via"], vehicle["to"],
                                             *[s["lane"].rsplit("_", 1)[0] for s in vehicle["stops"]]]
        for vclass in sorted(vclasses):
            barred = [edge for edge in dict.fromkeys(edges)
                      if not self.places.edge_permits(edge, vclass)]
            if barred:
                self.findings.refuse(10, vehicle["where"], f"vehicle class '{vclass}' may not drive "
                                     f"{', '.join(barred)}")

    def _check_unique_ids(self) -> None:
        seen: dict[str, str] = {}
        for kind, items in (("actor", self.actors), ("flow", self.flows),
                            ("lane closure", self.lane_closures)):
            for item in items:
                if item["id"] in seen:
                    self.findings.refuse(54, item["where"], f"id '{item['id']}' is also "
                                         f"{seen[item['id']]}")
                seen.setdefault(item["id"], item["where"])
        windows = [w["id"] for w in self.spec.get("capture_windows", [])]
        for window in {w for w in windows if windows.count(w) > 1}:
            self.findings.refuse(54, f"capture window {window}", "is declared more than once")

    # -- stage: routes --------------------------------------------------------------------------------
    def _stage_routes(self) -> None:
        self.scratch_network = self.out_dir / f".{self.scenario_id}.routing.net.xml"
        self.out_dir.mkdir(parents=True, exist_ok=True)
        self.scratch_network.write_text(self.network_text, encoding="utf-8")
        requests = []
        for actor in self.actors:
            if actor["route"] is not None:
                continue
            requests.append(RouteRequest(
                "vehicle", actor["id"], actor["type"], actor["depart"].seconds, actor["from"],
                actor["to"], tuple(actor["via"]),
                tuple((s["lane"], s["end_pos"]) for s in actor["stops"]), where=actor["where"]))
        for flow in self.flows:
            requests.append(RouteRequest(
                "flow", flow["id"], flow["type"], flow["begin"].seconds, flow["from"], flow["to"],
                tuple(flow["via"]), flow_end=flow["end"].seconds,
                vehs_per_hour=flow["vehs_per_hour"], where=flow["where"]))
        try:
            self.routing = RouteValidator(self.installation, self.findings).route(
                self.scratch_network, self.mix.to_xml(), requests, int(self.spec["seeds"]["sumo"]))
        finally:
            self.scratch_network.unlink(missing_ok=True)
        for vehicle in [*self.actors, *self.flows]:
            if vehicle["route"] is None:
                vehicle["route"] = list(self.routing.routes.get(vehicle["id"], ()))
            if vehicle["route"]:
                self._check_permissions(vehicle, tuple(vehicle["route"]))
        self._check_closed_routes()

    def _check_closed_routes(self) -> None:
        """Check 55: every route through a lane closure enters and leaves its edge on an open lane.

        A closed lane admits only `CLOSED_LANE_ALLOWS`, so for any other vehicle a connection into or
        out of it is gone while the closure lasts. SUMO refuses to insert a vehicle whose fixed route
        then has no connection it may use, and quits: *measured* with the staged SUMO 1.27.0 on the
        fixture world, closing the one lane of a flow's destination edge stopped the run at the first
        vehicle inserted during the closure ("has no valid route. No connection between edge '901#0'
        and edge '901#1'. Quitting (on error)"). Every route is held to it, whenever it departs.
        """
        for closure in self.lane_closures:
            edge, closed = closure["edge"], set(closure["indices"])
            for vehicle in [*self.actors, *self.flows]:
                route = vehicle["route"] or []
                problems = []
                for index, current in enumerate(route):
                    if current != edge:
                        continue
                    if len(closed) == closure["lane_count"]:
                        problems.append(f"every lane of {edge} is closed")
                        break
                    before = route[index - 1] if index else None
                    after = route[index + 1] if index + 1 < len(route) else None
                    if before is not None and not any(
                            to_lane not in closed
                            for _, to_lane in self.places.lane_links.get((before, edge), ())):
                        problems.append(f"{before} leads only into closed lanes of {edge}")
                    if after is not None and not any(
                            from_lane not in closed
                            for from_lane, _ in self.places.lane_links.get((edge, after), ())):
                        problems.append(f"only closed lanes of {edge} lead on to {after}")
                if problems:
                    self.findings.refuse(55, vehicle["where"], f"its route crosses lane closure "
                                         f"{closure['id']}, and {'; '.join(dict.fromkeys(problems))}"
                                         ". SUMO refuses to insert a vehicle on that route while "
                                         "the closure lasts, and stops the run")

    # -- stage: supervision -----------------------------------------------------------------------------
    def _stage_supervision(self) -> None:
        self.vocabulary = AnnotationVocabulary.from_specification(self.spec.get("vocabulary"),
                                                                  self.base, self.findings)
        inputs = SupervisionInputs(
            scenario_id=self.scenario_id,
            actor_departures={a["id"]: a["depart"] for a in self.actors},
            flow_ids=[f["id"] for f in self.flows],
            actor_stops={a["id"]: a["stops"] for a in self.actors},
            actor_phases={a["id"]: a["phase_entries"] for a in self.actors},
            rota_entries=self.rota_entries, rota_templates=self.rota_templates, areas=self.areas)
        self.supervision = SupervisionPlanCompiler(self.vocabulary, self.resolver, self.findings)
        self.plan_rows = self.supervision.compile(self.spec.get("supervision"), inputs)
        for area_id in sorted(self.supervision.referenced_areas):
            for warning in self.areas[area_id].get("warnings", []):
                check = 28 if warning.startswith("V5.7") else 27
                self.findings.warn(check, f"area {area_id}", warning)

    # -- stage: epoch and illumination --------------------------------------------------------------------
    def _stage_epoch_and_illumination(self) -> None:
        origin = self.package.origin
        self.window_sun = WindowSun(self.epoch, self.policy, origin[0], origin[1])
        frame = self.package.solar_frame()
        if frame is None:
            self.findings.warn(40, "epoch", "the world package publishes no solar frame, so the "
                               "declared offset was not compared with the world's")
            engine_zone = None
        else:
            engine_zone = float(frame["engine_time_zone_hours"])
            if abs(self.epoch.utc_offset_hours - engine_zone) > 1.0:
                self.findings.warn(40, "epoch", f"declares UTC{self._offset(self.epoch.utc_offset_hours)}"
                                   f" and the world's georeference configures "
                                   f"{frame['engine_time_zone']} (longitude / 15): more than an hour "
                                   "apart, so the offset and the map probably describe different "
                                   "places")
        self.windows = [w for w in (self._window(declared)
                                    for declared in self.spec.get("capture_windows", [])) if w]
        policy_name = str(self.policy.Name)
        self.report.set("illumination_default", {
            "declared": self.spec.get("illumination"), "policy": policy_name,
            "status": "an authored default the operator may override",
            "declared_elevation_kind": WindowSun.ELEVATION_KIND})
        self.report.set("zone", {"declared_offset_hours": self.epoch.utc_offset_hours,
                                 "engine_time_zone_hours": engine_zone,
                                 "difference_hours": None if engine_zone is None
                                 else self.epoch.utc_offset_hours - engine_zone,
                                 "written_by_the_session": "set_solar_epoch writes the declared "
                                                           "offset as the sun's zone"})
        self.report.set("capture_windows", self.windows)
        association = IlluminationLabelAssociation(self.window_sun)
        self.association = association.compute(self.windows, self._association_entries())
        self.findings.warn(41, "supervision", association.summary(self.association))
        self.report.set("illumination_label_association", self.association)

    def _association_entries(self) -> list[AssociationEntry]:
        """Every route entry with its supervision state and its estimated presence span."""
        entity_states = {row["entity_id"]: row["supervision"] for row in self.plan_rows["entities"]}
        cohort_states = {row["flow_id"]: row["supervision"] for row in self.plan_rows["cohorts"]}
        entries = []
        for actor in self.actors:
            states = entity_states.get(actor["id"], ["unlabelled"])
            state = next((s for s in ("annotated", "nominal") if s in states), "unlabelled")
            depart = actor["depart"].seconds
            dwell = sum(stop.get("duration", 0.0) for stop in actor["stops"])
            present_to = depart + self._free_flow_s(actor["route"]) + dwell
            entries.append(AssociationEntry(actor["id"], "actor", state, depart, depart, present_to))
        for flow in self.flows:
            entries.append(AssociationEntry(flow["id"], "flow", cohort_states.get(flow["id"],
                                                                                  "unlabelled"),
                                            flow["begin"].seconds, flow["begin"].seconds,
                                            flow["end"].seconds))
        return entries

    def _free_flow_s(self, route: list[str]) -> float:
        lanes = [self.places.lanes[self.places.edge_lanes[edge][0]] for edge in route or []]
        return sum(lane.length / lane.speed for lane in lanes if lane.speed > 0)

    def _window(self, declared: dict) -> dict | None:
        where = f"capture window {declared['id']}"
        begin = self.resolver.instant(declared["begin"], f"{where} begin")
        length = self.resolver.duration(declared["length"], f"{where} length")
        if begin is None or length is None:
            return None
        if length <= 0:
            self.findings.refuse(47, where, "has no length")
            return None
        end_s = begin.seconds + length
        if end_s > self.end.seconds:
            self.findings.refuse(38, where, f"runs to {self.epoch.civil_instant_at(end_s)}, after the "
                                 f"run ends at {self.end.civil}")
        # Only declared bounds can be compared with a window. An interval anchored to a stop's
        # arrival or to a later phase declares no start (06 D6.4); where it falls is the run's.
        for interval in self.supervision.intervals:
            if interval.begin is None or interval.end is None:
                continue
            s, t = interval.begin.seconds, interval.end.seconds
            if s < begin.seconds < t or s < end_s < t:
                self.findings.refuse(38, where, f"cuts interval {interval.phase} of instance "
                                     f"{interval.instance_id} ({interval.begin.civil} to "
                                     f"{interval.end.civil}); a partially observed positive teaches "
                                     "a truncated pattern")
        opens = self.window_sun.at(begin.seconds, begin.seconds)
        closes = self.window_sun.at(begin.seconds, end_s)
        record = {"id": declared["id"], "begin": begin.to_dict(), "length_s": length, "end_s": end_s,
                  "end_civil": self.epoch.civil_instant_at(end_s),
                  "civil_date": self.epoch.civil_date_at(begin.seconds),
                  "sun_open": None if opens is None else opens.to_dict(),
                  "sun_close": None if closes is None else closes.to_dict()}
        if opens is None:
            record["sun"] = "the default policy binds no sun (ignore)"
            return record
        if opens.sun_date != record["civil_date"]:
            self.findings.warn(36, where, f"opens on civil date {record['civil_date']} and its sun is "
                               f"written with {opens.sun_date} (calendar_advances "
                               f"{self.epoch.calendar_advances}, policy {self.policy.Name}), so it "
                               "renders under that date's seasonal sun")
        # Check 42 states a fact and concludes nothing: the lowest the sun reaches over the window, and
        # the band that elevation falls in, so the author knows what light the window is under. What
        # to make of it is the author's; the report carries no pass mark (the charter's §4b).
        lowest = min(opens.elevation_deg, closes.elevation_deg)
        record["sun_lowest"] = {"elevation_deg": round(lowest, 4),
                                "elevation_kind": WindowSun.ELEVATION_KIND,
                                "band": IlluminationBand.of(lowest),
                                "band_source": BAND_SOURCE}
        if bool(self.policy.Advances):
            self.findings.warn(39, where, f"the default policy advances the sun, from "
                               f"{opens.elevation_deg:.2f} deg at {begin.civil} to "
                               f"{closes.elevation_deg:.2f} deg at {record['end_civil']}; the window "
                               "is not one lighting condition")
        return record

    @staticmethod
    def _offset(hours: float) -> str:
        minutes = round(hours * 60)
        return f"{'-' if minutes < 0 else '+'}{abs(minutes) // 60:02d}:{abs(minutes) % 60:02d}"

    # -- stage: emission ------------------------------------------------------------------------------
    def _stage_emission(self) -> None:
        map_name = self.package.map_name or "world"
        paths = {
            "routes": self.out_dir / f"{self.scenario_id}.rou.xml",
            "config": self.out_dir / f"{self.scenario_id}.sumocfg",
            "network": self.out_dir / f"{map_name}.net.xml",
            "supervision": self.out_dir / f"{self.scenario_id}.supervision.json",
            "lock": self.out_dir / f"{self.scenario_id}.lock.json",
        }
        additional_xml = None
        if self.lane_closures:
            paths["additional"] = self.out_dir / f"{self.scenario_id}.add.xml"
            additional_xml = self._additional_xml()
        routes_xml = self._routes_xml()
        config_xml = self._config_xml(paths["network"].name, paths["routes"].name,
                                      paths["additional"].name if additional_xml else None)
        self._self_check(routes_xml, config_xml, additional_xml)
        self._check_bodies(routes_xml)
        self.emitted = {"paths": paths, "routes": routes_xml, "config": config_xml,
                        "additional": additional_xml}

    def _write_traffic_files(self, directory: Path) -> dict[str, Path]:
        """The network, routes, configuration and closures, as the configuration names them, into
        `directory`: the scenario's own directory, or the dry run's scratch."""
        names = {role: path.name for role, path in self.emitted["paths"].items()}
        written = {role: directory / names[role] for role in ("network", "routes", "config")}
        written["network"].write_text(self.network_text, encoding="utf-8", newline="")
        written["routes"].write_text(self.emitted["routes"], encoding="utf-8", newline="\n")
        written["config"].write_text(self.emitted["config"], encoding="utf-8", newline="\n")
        if self.emitted["additional"] is not None:
            written["additional"] = directory / names["additional"]
            written["additional"].write_text(self.emitted["additional"], encoding="utf-8",
                                             newline="\n")
        return written

    # -- stage: dry run -------------------------------------------------------------------------------
    def _stage_dry_run(self) -> None:
        """Check 59: run the compiled scenario in SUMO alone, before anything is written, and refuse
        a planned vehicle that never enters it (`SumoDryRun`).

        The files are written to a scratch directory beside the output and removed after, so a refused
        compile leaves an earlier compile's package as it was. `skip_dry_run` skips it for quick
        iteration, and the lock says it was skipped.
        """
        if self.skip_dry_run:
            self.dry_run = {"ran": False, "reason": SKIPPED_DRY_RUN}
            self.report.set("dry_run", self.dry_run)
            return
        scratch = self.out_dir / f".{self.scenario_id}.dry-run"
        SumoDryRun.remove(scratch)
        scratch.mkdir(parents=True)
        try:
            written = self._write_traffic_files(scratch)
            run = SumoDryRun(self.installation, self.findings).run(
                written["config"], scratch, self._planned_vehicles(), self.end.seconds,
                float(PROCESSING_OPTIONS["max-depart-delay"]), self.epoch.civil_instant_at)
        finally:
            SumoDryRun.remove(scratch)
        if run is None:
            return
        self.dry_run = run.lock_record()
        self.report.set("dry_run", run.report())

    def _planned_vehicles(self) -> list[PlannedVehicle]:
        """Every vehicle the plan names: each instance's participants, annotated or nominal, and the
        vehicle of every slot of every series. A flow's members are unknown until the run, so no
        cohort names one."""
        refs: dict[str, list[str]] = {}
        for row in self.plan_rows["instances"]:
            for participant in row["participants"]:
                refs.setdefault(participant["entity_id"], []).append(row["instance_id"])
        for series in self.plan_rows["series"]:
            for slot in series["slots"]:
                refs.setdefault(slot["entity_id"], []).append(f"series {series['series_id']}")
        actors = {actor["id"]: actor for actor in self.actors}
        planned = [PlannedVehicle(vehicle_id, actors[vehicle_id]["where"],
                                  actors[vehicle_id]["depart"],
                                  (actors[vehicle_id]["route"] or [""])[0], tuple(names))
                   for vehicle_id, names in refs.items() if vehicle_id in actors]
        return sorted(planned, key=lambda vehicle: (vehicle.depart.seconds, vehicle.vehicle_id))

    # -- stage: write ---------------------------------------------------------------------------------
    def _stage_write(self) -> None:
        paths = self.emitted["paths"]
        self._write_traffic_files(self.out_dir)
        roles = ("routes", "config", "network")
        if self.emitted["additional"] is not None:
            roles += ("additional",)
        digests = {role: self._sha256(paths[role]) for role in roles}
        plan = {
            "supervision_plan_version": SUPERVISION_PLAN_VERSION,
            "plan_id": self.scenario_id,
            "spec_version": SPEC_VERSION,
            "scenario_id": self.scenario_id,
            "routes_digest": digests["routes"],
            # The network's identity, not its file's bytes: the copy carries netconvert's build
            # stamp, which a rebuilt world changes while the network stays the same.
            "network_digest": self.network_fingerprint,
            "config_digest": digests["config"],
            # The lane closures' file, where the configuration names one: it decides the traffic as
            # the route file does, so the plan is bound to it the same way. None where there is none.
            "additional_digest": digests.get("additional"),
            "vocabulary_version": self.vocabulary.to_document()["core"]["vocabulary_version"],
            "vocabulary_digest": self.vocabulary.digest,
            "vocabulary": self.vocabulary.to_document(),
            **self.plan_rows,
        }
        paths["supervision"].write_text(json.dumps(plan, indent=2, ensure_ascii=False) + "\n",
                                        encoding="utf-8", newline="\n")
        digests["supervision"] = self._sha256(paths["supervision"])
        self.result.plan = plan
        self.result.lock = self._lock(digests, paths)
        paths["lock"].write_text(json.dumps(self.result.lock, indent=2, ensure_ascii=False) + "\n",
                                 encoding="utf-8", newline="\n")
        self.result.files.update(paths)
        self.report.set("routes", [self._vehicle_report(v) for v in [*self.actors, *self.flows]])
        self.report.set("supervision", {"instances": plan["instances"], "cohorts": plan["cohorts"],
                                        "series": [{k: v for k, v in s.items() if k != "slots"}
                                                   | {"slots": len(s["slots"])}
                                                   for s in plan["series"]]})
        self.report.set("lock", self.result.lock)

    def _routes_xml(self) -> str:
        """The route file: departure-sorted, and equal departures in the specification's order.

        SUMO inserts in the order it reads and draws its random numbers -- which member of a type
        distribution, where a gap falls -- in that order too, so the order of entries that depart
        together decides the traffic as much as the seed does. A stable sort on the departure alone
        keeps the author's order among them, flows before actors as the Python builders write them.
        Measured on the Gardnerville orbit: the same entries sorted by id instead gridlock the
        neighbourhood by 1 000 s, where the authored order runs clear.
        """
        entries = []
        for flow in self.flows:
            entries.append((flow["begin"].seconds, self._flow_xml(flow)))
        for actor in self.actors:
            entries.append((actor["depart"].seconds, self._vehicle_xml(actor)))
        entries.sort(key=lambda item: item[0])
        header = self._comment(
            f"{self.spec['scenario_name']}: compiled by {COMPILER} {__version__} from the "
            f"specification of {self.scenario_id}; edit that, not this. The epoch that gives these "
            f"seconds their civil meaning is stated in {self.scenario_id}.sumocfg. Every time "
            "in this file is simulated seconds from t = 0. Supervision is not in this file: it is "
            f"in {self.scenario_id}.supervision.json.")
        return ("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + header + "\n"
                "<routes xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" "
                "xsi:noNamespaceSchemaLocation=\"http://sumo.dlr.de/xsd/routes_file.xsd\">\n\n"
                + self.mix.to_xml() + "\n" + "\n".join(xml for _, xml in entries)
                + "\n\n</routes>\n")

    def _vehicle_xml(self, actor: dict) -> str:
        attributes = (f"id={quoteattr(actor['id'])} type={quoteattr(actor['type'])} "
                      f"depart=\"{actor['depart'].seconds:.2f}\" "
                      f"departLane={quoteattr(actor['depart_lane'])} "
                      f"departSpeed={quoteattr(actor['depart_speed'])}")
        if actor["arrival_speed"]:
            attributes += f" arrivalSpeed={quoteattr(actor['arrival_speed'])}"
        lines = [f"    <vehicle {attributes}>",
                 f"        <route edges={quoteattr(' '.join(actor['route']))}/>"]
        for stop in actor["stops"]:
            stop_attributes = f"lane={quoteattr(stop['lane'])}"
            if stop["start_pos"] is not None:
                stop_attributes += f" startPos=\"{stop['start_pos']:.2f}\""
            stop_attributes += f" endPos=\"{stop['end_pos']:.2f}\""
            if "duration" in stop:
                stop_attributes += f" duration=\"{stop['duration']:.2f}\""
            else:
                stop_attributes += f" until=\"{stop['until'].seconds:.2f}\""
            stop_attributes += f" parking=\"{'true' if stop['parking'] else 'false'}\""
            lines.append(f"        <stop {stop_attributes}/>")
        for waypoint in actor["waypoints"]:
            lines.append(f"        <stop lane={quoteattr(waypoint['lane'])} startPos=\"0.00\" "
                         f"endPos=\"{waypoint['end_pos']:.2f}\" speed=\"{waypoint['speed']:.2f}\"/>")
        lines.append("    </vehicle>")
        return "\n".join(lines)

    def _flow_xml(self, flow: dict) -> str:
        return (f"    <flow id={quoteattr(flow['id'])} type={quoteattr(flow['type'])} "
                f"begin=\"{flow['begin'].seconds:.2f}\" end=\"{flow['end'].seconds:.2f}\" "
                f"vehsPerHour=\"{flow['vehs_per_hour']:g}\" "
                f"departLane={quoteattr(flow['depart_lane'])} "
                f"departSpeed={quoteattr(flow['depart_speed'])}>\n"
                f"        <route edges={quoteattr(' '.join(flow['route']))}/>\n"
                "    </flow>")

    def _additional_xml(self) -> str:
        """The additional file: one rerouter per lane closure, closing its lanes for its window.

        A `closingLaneReroute` restricts its lane to `CLOSED_LANE_ALLOWS` from the interval's begin
        to its end, whatever the rerouter's edges; those edges are where a vehicle learns of the
        closure and may be rerouted round it.
        """
        blocks = []
        for closure in sorted(self.lane_closures, key=lambda c: (c["begin"].seconds, c["id"])):
            begin, end = closure["begin"], closure["end"]
            closed = len(closure["lanes"])
            blocks.append("\n".join([
                self._comment(f"{closure['id']}: {closed} of the {closure['lane_count']} lanes of "
                              f"{closure['edge']} ({closure['street'] or 'unnamed'}) closed from "
                              f"{begin.civil} to {end.civil}.", indent="    "),
                f"    <rerouter id={quoteattr(closure['id'])} "
                f"edges={quoteattr(' '.join(closure['notify']))}>",
                f"        <interval begin=\"{begin.seconds:.2f}\" end=\"{end.seconds:.2f}\">",
                *[f"            <closingLaneReroute id={quoteattr(lane)} "
                  f"allow={quoteattr(CLOSED_LANE_ALLOWS)}/>" for lane in closure["lanes"]],
                "        </interval>",
                "    </rerouter>"]))
        header = self._comment(
            f"{self.spec['scenario_name']}: the lane closures, compiled by {COMPILER} {__version__} "
            f"from the specification of {self.scenario_id}; edit that, not this. Every time in this "
            "file is simulated seconds from t = 0; the epoch that gives them their civil meaning is "
            f"stated in {self.scenario_id}.sumocfg.")
        return ("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + header + "\n"
                "<additional xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" "
                "xsi:noNamespaceSchemaLocation=\"http://sumo.dlr.de/xsd/additional_file.xsd\">\n"
                + "\n".join(blocks) + "\n</additional>\n")

    def _config_xml(self, network_name: str, routes_name: str,
                    additional_name: str | None = None) -> str:
        options = "\n".join(f"        <{name} value={quoteattr(value)}/>"
                            for name, value in PROCESSING_OPTIONS.items())
        epoch = self._comment(f"{self.epoch.describe()}. The run ends at {self.end.civil}. begin and "
                              "end below are simulated seconds from t = 0.", indent="    ")
        additional = ("" if additional_name is None else
                      f"\n        <additional-files value={quoteattr(additional_name)}/>")
        return (f"""<?xml version="1.0" encoding="UTF-8"?>
{self._comment(f"Compiled by {COMPILER} from the specification of {self.scenario_id}; edit that, not this.")}
<configuration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
               xsi:noNamespaceSchemaLocation="http://sumo.dlr.de/xsd/sumoConfiguration.xsd">
    <input>
        <net-file value={quoteattr(network_name)}/>
        <route-files value={quoteattr(routes_name)}/>{additional}
    </input>
{epoch}
    <time>
        <begin value="0"/>
        <end value="{self.end.seconds:g}"/>
        <step-length value="{self.step_length!r}"/>
    </time>
    <random_number>
        <seed value="{int(self.spec['seeds']['sumo'])}"/>
    </random_number>
    <processing>
{options}
    </processing>
    <report>
        <no-step-log value="true"/>
        <duration-log.statistics value="true"/>
    </report>
</configuration>
""")

    @staticmethod
    def _comment(text: str, indent: str = "") -> str:
        """An XML comment SUMO will load: no `--` inside it."""
        while "--" in text:
            text = text.replace("--", "- -")
        return f"{indent}<!-- {text} -->"

    def _self_check(self, routes_xml: str, config_xml: str, additional_xml: str | None = None) -> None:
        """Checks 29, 30, 44, 51 and 52: re-read what is about to be written."""
        parsed = {}
        texts = [("route file", routes_xml), ("configuration", config_xml)]
        if additional_xml is not None:
            texts.append(("additional file", additional_xml))
        for name, text in texts:
            for comment in re.findall(r"<!--(.*?)-->", text, flags=re.S):
                if "--" in comment:
                    self.findings.refuse(30, name, "an XML comment contains --, which SUMO rejects")
            try:
                root = ET.fromstring(text)
            except ET.ParseError as problem:
                self.findings.refuse(51, name, f"is not well-formed XML: {problem}")
                continue
            parsed[name] = root
            for element in root.iter():
                for attribute, value in element.attrib.items():
                    if _CLOCK_VALUE.match(value):
                        self.findings.refuse(44, name, f"<{element.tag} {attribute}=\"{value}\"> is a "
                                             "clock, which SUMO reads as an offset from t = 0")
        if "additional file" in parsed:
            self._validate("additional file", additional_xml, ADDITIONAL_SCHEMA_RELATIVE_PATH)
        root = parsed.get("route file")
        if root is None:
            return
        times = []
        for entry in root:
            if entry.tag in ("vehicle", "flow"):
                try:
                    times.append(float(entry.get("depart") if entry.tag == "vehicle"
                                       else entry.get("begin")))
                except (TypeError, ValueError):
                    continue
        if any(b < a for a, b in zip(times, times[1:], strict=False)):
            self.findings.refuse(29, "route file", "entries are not in departure order")
        for param in root.iter("param"):
            if param.get("key") not in ALLOWED_PARAMS:
                self.findings.refuse(52, "route file", f"carries <param key=\"{param.get('key')}\">; "
                                     "the route file carries only the vehicle-type binding, and "
                                     "supervision travels in the supervision plan alone")
        self._validate("route file", routes_xml, ROUTES_SCHEMA_RELATIVE_PATH)

    def _validate(self, name: str, text: str, relative_schema: Path) -> None:
        """Check 51: an emitted file against the SUMO installation's own schema for it."""
        schema = self.installation.home / "data" / relative_schema
        if schema.exists():
            validator = etree.XMLSchema(etree.parse(str(schema)))
            document = etree.fromstring(text.encode("utf-8"))
            if not validator.validate(document):
                for error in validator.error_log:
                    self.findings.refuse(51, name, f"line {error.line}: {error.message}")
        else:
            self.findings.refuse(51, name, f"SUMO's schema {relative_schema.name} is not at "
                                 f"{schema}, so the {name} cannot be validated")

    def _check_bodies(self, routes_xml: str) -> None:
        """Check 15: read the route file back as the bridge will, and hold every type to its body."""
        probe = self.out_dir / f".{self.scenario_id}.rou.check.xml"
        probe.write_text(routes_xml, encoding="utf-8", newline="\n")
        try:
            ScenarioVehicleMix.check_route_file(probe, self.catalogue)
        except (LookupError, ValueError) as problem:
            self.findings.refuse(15, "route file", str(problem))
        finally:
            probe.unlink(missing_ok=True)

    def _lock(self, digests: dict[str, str], paths: dict[str, Path]) -> dict:
        manifest = self.package.manifest
        return {
            "lock_version": LOCK_VERSION,
            "scenario_id": self.scenario_id,
            "scenario_name": self.spec["scenario_name"],
            "spec_version": SPEC_VERSION,
            "specification": self.spec_path.name,
            "specification_sha256": self.spec_sha256,
            "compiler": {"name": COMPILER, "version": __version__},
            "files": {role: {"path": paths[role].name, "sha256": digest}
                      for role, digest in digests.items()},
            "world": {"package": self.package.path.name, "map_name": self.package.map_name,
                      "network_fingerprint": self.network_fingerprint,
                      "netconvert_argv": PortablePath.argv(
                          self.package.netconvert_argv, self.out_dir,
                          [self.installation.home,
                           *PortablePath.sumo_home_of(manifest.get("NetconvertPath", ""))]),
                      "netconvert_version": self.package.netconvert_version,
                      "opendrive_sha256": manifest.get("OpenDriveSha256", ""),
                      "source_osm_sha256": manifest.get("SourceOsmSha256", ""),
                      "origin_latitude": self.package.origin[0],
                      "origin_longitude": self.package.origin[1],
                      "georeference": manifest.get("GeoReferenceString", "")},
            "catalogue": {"catalogue_id": self.catalogue.catalogue_id,
                          "catalogue_digest": self.catalogue.catalogue_digest,
                          "blueprint_set_digest": self.catalogue.blueprint_set_digest,
                          "content_build_id": self.catalogue.content_build_id},
            "vocabulary": {"core_version": self.vocabulary.to_document()["core"]["vocabulary_version"],
                           "namespaces": self.vocabulary.namespace_versions,
                           "vocabulary_digest": self.vocabulary.digest},
            "traffic": {"sumo_seed": int(self.spec["seeds"]["sumo"]),
                        "step_length_s": self.step_length, "end_s": self.end.seconds,
                        "processing": PROCESSING_OPTIONS,
                        "routed_by": {"tool": "duarouter",
                                      "version": self.routing.duarouter_version,
                                      "world_converter": self.package.netconvert_version,
                                      "release_agreement": self.release_agreement,
                                      "mismatch_accepted": self.release_agreement
                                      == str(SumoReleaseAgreement.MismatchAccepted)}},
            # Whether a SUMO-only run of these files inserted every planned vehicle (check 59).
            "dry_run": self.dry_run,
            "epoch": self.epoch.declaration,
            "epoch_block_sha256": self.epoch.digest,
            "illumination": self.spec.get("illumination"),
            "capture_windows": [{"id": w["id"], "begin_s": w["begin"]["seconds"],
                                 "end_s": w["end_s"], "civil_begin": w["begin"]["civil"],
                                 "civil_end": w["end_civil"], "civil_date": w["civil_date"]}
                                for w in self.windows],
            "ephemeris": "CarlaNet.CoSim.DeclaredSun and SolarPositionModel.AtInstant",
            "illumination_label_association": {
                key: self.association.get(key) for key in ("normalized_mutual_information",
                                                           "bands", "entries")},
        }

    def _vehicle_report(self, vehicle: dict) -> dict:
        route = vehicle["route"] or []
        length = sum(self.places.lanes[self.places.edge_lanes[e][0]].length for e in route)
        free_flow = sum(self.places.lanes[self.places.edge_lanes[e][0]].length
                        / self.places.lanes[self.places.edge_lanes[e][0]].speed for e in route)
        entry = {"id": vehicle["id"], "type": vehicle["type"], "from": vehicle["from"],
                 "to": vehicle["to"], "via": vehicle["via"], "route": route,
                 "route_length_m": round(length, 2), "free_flow_s": round(free_flow, 1),
                 "stops": [{k: (v.to_dict() if isinstance(v, ResolvedInstant) else v)
                            for k, v in s.items()} for s in vehicle["stops"]]}
        if vehicle.get("phases"):
            entry["phases"] = vehicle["phases"]
            entry["waypoints"] = len(vehicle["waypoints"])
        if "depart" in vehicle:
            entry["depart"] = vehicle["depart"].to_dict()
            entry["origin"] = vehicle["origin"]
        else:
            entry["begin"] = vehicle["begin"].to_dict()
            entry["end"] = vehicle["end"].to_dict()
            entry["vehs_per_hour"] = vehicle["vehs_per_hour"]
        return entry

    @staticmethod
    def _sha256(path: Path) -> str:
        return hashlib.sha256(path.read_bytes()).hexdigest()

    # -- the report -----------------------------------------------------------------------------------
    def _write_report(self) -> None:
        if not self.spec.get("scenario_id"):
            name = self.spec_path.stem.replace(".scenario", "")
        else:
            name = self.spec["scenario_id"]
        self.report.set("outcome", "refused" if self.findings.refused else "compiled")
        self.report.set("findings", self.findings.to_list())
        self.out_dir.mkdir(parents=True, exist_ok=True)
        json_path = self.out_dir / f"{name}.resolution.json"
        md_path = self.out_dir / f"{name}.resolution.md"
        json_path.write_text(json.dumps(self.report.document, indent=2, ensure_ascii=False) + "\n",
                             encoding="utf-8", newline="\n")
        md_path.write_text(self.report.to_markdown(), encoding="utf-8", newline="\n")
        self.result.files["resolution"] = json_path
        self.result.files["resolution_md"] = md_path
        self.result.report = self.report.document
