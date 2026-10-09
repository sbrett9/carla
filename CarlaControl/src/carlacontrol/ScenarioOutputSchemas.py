"""The schemas of what the scenario compiler writes, and of the skill's list of its checks.

carla-compile-scenario writes, beside one another (`ScenarioCompiler`):

| File | Schema |
|---|---|
| `<scenario_id>.lock.json` | `lock()` |
| `<scenario_id>.resolution.json` | `resolution()` (and `.resolution.md`, its rendering for a person) |
| `<scenario_id>.supervision.json` | `supervision_plan()` |
| `<sweep_id>.sweep-index.json`, for a sweep | `sweep_index()` |
| the skill's `checks.json` (`--write-checks`) | `checks()` |

The `.rou.xml`, `.add.xml`, `.sumocfg` and the network copy are SUMO's own formats, each valid against
SUMO's own schema (check 51 holds the route and additional files to it); they are not described here.

Each schema is generated from the code that writes its file: the report's section order
(`ResolutionReport.SECTIONS`), the check catalogue's ids, groups and statuses, the processing options
the compiler fixes, the SUMO release agreements, the closed core of the annotation vocabulary, the
specification schema's own namespace and term definitions (`ScenarioSchema`), the epoch's schema
(`ScenarioEpoch`) and the illumination bands. A member of a report the compiler takes from another
component -- the illumination-label association's tables, a place's authored form -- is described to
the depth the compiler writes it and left open below.

Not to be confused with a capture run's `run.lock.json` and `run.resolution.json`
(`RunRecordSchemas`): those describe one run of a compiled scenario, these the compile itself.
"""
from __future__ import annotations

import carlanet  # noqa: F401  -- loads the CarlaNet assemblies the next import names
import System
from CarlaNet.Sumo import SumoReleaseAgreement

from carlacontrol.AnnotationVocabulary import CORE_TERMS
from carlacontrol.CompileFinding import OUTCOMES as FINDING_OUTCOMES
from carlacontrol.IlluminationBand import IlluminationBand
from carlacontrol.ResolutionReport import RESOLUTION_VERSION, SECTIONS
from carlacontrol.RunConfiguration import ILLUMINATION_POLICIES
from carlacontrol.ScenarioCheck import STATUSES
from carlacontrol.ScenarioCheckCatalogue import CHECKS_VERSION, ScenarioCheckCatalogue
from carlacontrol.ScenarioCompiler import COMPILER, LOCK_VERSION, PROCESSING_OPTIONS
from carlacontrol.ScenarioEpoch import ScenarioEpoch
from carlacontrol.ScenarioSchema import _ANCHOR_EVENT, _IDENTIFIER, _TERM, SCHEMA, SPEC_VERSION
from carlacontrol.ScenarioSweep import SWEEP_VERSION
from carlacontrol.SchemaPublication import SchemaPublication
from carlacontrol.SupervisionPlanCompiler import SUPERVISION_PLAN_VERSION

_P = SchemaPublication
_TEXT = _P.TEXT
_STRING = {"type": "string"}
_NUMBER = {"type": "number"}
_COUNT = {"type": "integer", "minimum": 0}
_BOOLEAN = {"type": "boolean"}
_TERMS = {"type": "array", "items": {"type": "string", "pattern": _TERM}}
_STATES = CORE_TERMS["supervision_state"]
_RELEASE_AGREEMENTS = [str(name) for name in System.Enum.GetNames(SumoReleaseAgreement)]
# The roles of the files a lock digests, in the order the compiler writes them.
FILE_ROLES = ("routes", "config", "network", "additional", "supervision")
_OPTIONAL_FILE_ROLES = ("additional",)


def _n(fragment: dict, description: str | None = None) -> dict:
    return _P.nullable(fragment, description)


def _d(fragment: dict, description: str) -> dict:
    return _P.described(fragment, description)


def _object(properties: dict, description: str, required: list[str] | None = None,
            closed: bool = True) -> dict:
    """An object of these properties, every one required unless `required` names fewer."""
    node: dict = {"type": "object", "description": description,
                  "required": list(properties) if required is None else required,
                  "properties": properties}
    if closed:
        node["additionalProperties"] = False
    return node


class ScenarioOutputSchemas:
    """Generates the schema of each file the scenario compiler writes."""

    # -- the published schemas ----------------------------------------------------------------------

    @classmethod
    def lock(cls) -> dict:
        """`<scenario_id>.lock.json`."""
        body = cls._lock_body()
        return _P.document(
            "scenario-lock", LOCK_VERSION, "Compiled scenario lock",
            "What a compiled scenario is bound to: the digests of its specification and of every "
            "file the compile wrote, the world it was compiled against, the vehicle catalogue, the "
            "vocabulary, the SUMO seed, step and options, what the SUMO-only run showed, the epoch "
            "and the illumination default. Written by carla-compile-scenario as "
            "<scenario_id>.lock.json; read by carla-capture and by the co-simulation session, "
            "which refuse a scenario whose files are not the ones it digests. Not a capture run's "
            "run.lock.json.",
            {**body, "$defs": cls._lock_defs()})

    @classmethod
    def resolution(cls) -> dict:
        """`<scenario_id>.resolution.json`."""
        sections = cls._resolution_sections()
        return _P.document(
            "scenario-resolution", RESOLUTION_VERSION, "Scenario resolution report",
            "What a scenario compile resolved, so its author can check it against what they meant: "
            "every place, instant, route, vehicle type, lane closure, capture window and the sun "
            "it opens under, the supervision, what a SUMO-only run showed, every finding and the "
            "lock. Written by carla-compile-scenario as <scenario_id>.resolution.json, with "
            "<scenario_id>.resolution.md beside it for reading, whether the compile succeeded or "
            "was refused; a refused compile writes only the report. Sections appear in this "
            "order, and only those the compile reached. Not a capture run's run.resolution.json.",
            {"type": "object", "additionalProperties": False,
             "required": ["resolution_version", "outcome", "findings"],
             "properties": sections,
             "$defs": {**cls._lock_defs(), "lock": cls._lock_body(),
                       "finding": cls._finding(), "instant": cls._instant(),
                       "window_sun": cls._window_sun(), **cls._plan_defs(),
                       "counterfactual": SCHEMA["$defs"]["counterfactual"]}})

    @classmethod
    def supervision_plan(cls) -> dict:
        """`<scenario_id>.supervision.json`."""
        sha = _P.SHA256
        properties = {
            "supervision_plan_version": {"const": SUPERVISION_PLAN_VERSION,
                                         "description": "The format version of this file. The "
                                                        "session refuses any other."},
            "plan_id": _d(_TEXT, "The plan's id: the scenario id."),
            "spec_version": {"const": SPEC_VERSION,
                             "description": "The specification format version compiled."},
            "scenario_id": {"type": "string", "pattern": _IDENTIFIER},
            "routes_digest": _d(sha, "SHA-256 of the route file the plan was compiled with."),
            "network_digest": _d(sha, "The canonical fingerprint of the network, not its file's "
                                      "bytes."),
            "config_digest": _d(sha, "SHA-256 of the .sumocfg."),
            "additional_digest": _n(sha, "SHA-256 of the lane closures' .add.xml; null where "
                                         "there is none."),
            "vocabulary_version": _d(_COUNT, "The core vocabulary's version."),
            "vocabulary_digest": _d(sha, "SHA-256 of the vocabulary block, keys sorted, two-space "
                                         "indent."),
            "vocabulary": cls._vocabulary(),
            "instances": {"type": "array", "items": {"$ref": "#/$defs/instance"},
                          "description": "Each authored assertion about one or more vehicles."},
            "series": {"type": "array", "items": {"$ref": "#/$defs/series"},
                       "description": "Each schedule read as a recurring series, one slot per "
                                      "vehicle it sends."},
            "cohorts": {"type": "array", "items": {"$ref": "#/$defs/cohort"},
                        "description": "Every flow, with its whole-life supervision; unlabelled "
                                       "where nothing was declared."},
            "entities": {"type": "array", "items": {"$ref": "#/$defs/entity"},
                         "description": "Every authored vehicle, with its supervision states and "
                                        "the rows that name it; unlabelled where none does."},
        }
        return _P.document(
            "supervision-plan", SUPERVISION_PLAN_VERSION, "Supervision plan",
            "The scenario's annotation, and the only place annotation travels: every labeled "
            "assertion about authored vehicles and flows, fixed at compile time and bound to the "
            "compiled traffic files by digest. Written by carla-compile-scenario as "
            "<scenario_id>.supervision.json; read by the co-simulation session, whose run manifest "
            "and sidecars report each interval as it happens. It carries no time of writing, so "
            "two compiles of one specification write it byte for byte.",
            {"type": "object", "additionalProperties": False, "required": _P.required(properties),
             "properties": properties,
             "$defs": {**cls._plan_defs(),
                       **{name: SCHEMA["$defs"][name]
                          for name in ("namespace", "term", "counterfactual")}}})

    @classmethod
    def sweep_index(cls) -> dict:
        """`<sweep_id>.sweep-index.json`."""
        assignment = _object({"path": _d(_TEXT, "The axis path."),
                              "value": {"description": "The value the member takes on it."}},
                             "One axis value of a member.")
        pair = _object({
            "counterfactual_pair_id": _d(_TEXT, "<base member>.cf.<actor>.<mode>, the twin's "
                                                "member id."),
            "base_member": _TEXT, "counterfactual_member": _TEXT,
            "actor": _d(_TEXT, "The actor the twin changes."),
            "mode": {"enum": ["absent", "nominal", "displaced"]},
            "displaced_in": {"type": "array", "items": {"enum": ["time", "space"]}},
            "illumination_differs": _d(_BOOLEAN, "Whether the twin is lit differently: a "
                                                 "displacement in time."),
            "trajectories_expected_to_match": {"const": False},
            "statement": _STRING}, "A counterfactual pair: identical inputs except one vehicle.")
        window = _object({"id": _TEXT, "civil_begin": _STRING, "civil_date": _STRING,
                          "sun_open": _n({"$ref": "#/$defs/window_sun"})},
                         "One of the member's capture windows.")
        member = _object({
            "member_id": _d(_TEXT, "<base scenario id>.base, or .m<10 hex digits of the "
                                   "assignment's SHA-256>; a twin's is its pair id."),
            "assignments": {"type": "array", "items": assignment},
            "outcome": {"enum": ["compiled", "refused"]},
            "specification": _d(_TEXT, "The member's specification, relative to the output "
                                       "directory, in forward slashes. An index written before "
                                       "2026-10-07 on Windows separates them with backslashes."),
            "findings": {"type": "array", "items": {"$ref": "#/$defs/finding"}},
            "epoch_block_sha256": _P.SHA256,
            "illumination": _d({"type": "object"}, "The member's illumination default, from its "
                                                   "lock."),
            "files": _d({"type": "object", "additionalProperties": _P.SHA256},
                        "Each file the member's lock digests, by role, to its SHA-256."),
            "windows": {"type": "array", "items": window},
            "counterfactual": {"$ref": "#/$defs/pair"}},
            "One member of the sweep, compiled in full in its own directory.",
            required=["member_id", "assignments", "outcome", "specification", "findings"])
        axis = _object({"path": _TEXT, "values": {"type": "array"},
                        "illumination_axis": _d(_BOOLEAN, "Whether the axis changes the light: "
                                                          "its path touches the epoch, the "
                                                          "illumination or a window's begin.")},
                       "One axis, as the compiler read it.")
        properties = {
            "sweep_version": {"const": SWEEP_VERSION,
                              "description": "The format version of the sweep compiled."},
            "producer": _P.producer(),
            "sweep_id": _n(_STRING, "The sweep's id; null where the sweep file gave none."),
            "members": {"type": "array", "items": member},
            "pairs": {"type": "array", "items": {"$ref": "#/$defs/pair"}},
            "findings": {"type": "array", "items": {"$ref": "#/$defs/finding"},
                         "description": "The sweep's findings, and every member's refusals."},
            "base": _d(_TEXT, "The base specification's file name."),
            "base_sha256": _P.SHA256,
            "pairing": {"enum": ["cross", "zip"]},
            "illumination": {"enum": ["hold", "vary", "factorial"]},
            "axes": {"type": "array", "items": axis},
            "outcome": {"enum": ["compiled", "refused"]},
        }
        return _P.document(
            "sweep-index", SWEEP_VERSION, "Sweep index",
            "What a sweep compiled: every member with the axis values it took and the files its "
            "lock digests, each counterfactual pair, and every finding. Written by "
            "carla-compile-scenario --sweep as <sweep_id>.sweep-index.json beside the members' "
            "directories. A sweep refused before its members were made carries only the version, "
            "producer, id, empty lists, its findings and the outcome.",
            {"type": "object", "additionalProperties": False,
             "required": ["sweep_version", "sweep_id", "members", "pairs", "findings", "outcome"],
             "properties": properties,
             "$defs": {"pair": pair, "finding": cls._finding(), "window_sun": cls._window_sun()}})

    @staticmethod
    def checks() -> dict:
        """The skill's `checks.json` (`ScenarioCheckCatalogue.to_document`)."""
        catalogue = ScenarioCheckCatalogue.all()
        check = _object({
            "id": _d({"enum": sorted(c.check_id for c in catalogue)},
                     "The check's stable id. An id is never reused; a removed check stays, "
                     "retired."),
            "group": {"enum": list(dict.fromkeys(c.group for c in catalogue))},
            "title": _d(_TEXT, "What the check establishes."),
            "against": _d(_STRING, "What it compares against."),
            "outcomes": {"type": "array", "items": {"enum": list(FINDING_OUTCOMES)},
                         "description": "What it can conclude: refuse, warn, both, or neither "
                                        "for a check that only states a fact."},
            "status": {"enum": list(STATUSES),
                       "description": "Where the check is carried out, or retired."},
            "prevents": _d(_TEXT, "The failure it exists to prevent.")}, "One check.")
        properties = {
            "checks_version": {"const": CHECKS_VERSION,
                               "description": "The format version of this file."},
            "outcomes": _object({outcome: _STRING for outcome in FINDING_OUTCOMES},
                                "What each outcome means."),
            "checks": {"type": "array", "items": check,
                       "description": "Every check, in the order the compiler runs them."},
        }
        return _P.document(
            "scenario-checks", CHECKS_VERSION, "Scenario compiler checks",
            "Every check the scenario compiler runs, by its stable id, with what it compares and "
            "what it can conclude. Shipped with the authoring skill as checks.json, generated by "
            "carla-compile-scenario --write-checks; a finding in a resolution report cites a check "
            "by its id.",
            {"type": "object", "additionalProperties": False, "required": _P.required(properties),
             "properties": properties})

    # -- shared pieces --------------------------------------------------------------------------------

    @staticmethod
    def _finding() -> dict:
        ids = sorted(check.check_id for check in ScenarioCheckCatalogue.all())
        return _object({"check": _d({"enum": ids}, "The check's id in checks.json."),
                        "outcome": {"enum": list(FINDING_OUTCOMES),
                                    "description": "refuse: nothing is emitted. warn: the compile "
                                                   "goes on and the warning is carried in full."},
                        "subject": _d(_STRING, "What it is about, in the specification's own "
                                               "names."),
                        "message": _d(_STRING, "The finding in full.")},
                       "One refusal or warning of the compile.")

    @staticmethod
    def _instant() -> dict:
        return _object({"authored": {"description": "The time as written in the specification."},
                        "form": _d(_STRING, "Which form it was written in."),
                        "seconds": _d(_NUMBER, "Simulated seconds from t = 0."),
                        "civil": _d(_STRING, "The civil instant, ISO 8601 with the epoch's "
                                             "offset.")},
                       "An authored time and what it resolved to.")

    @staticmethod
    def _window_sun() -> dict:
        return _object({
            "seconds": _d(_NUMBER, "The simulated second."),
            "sun_date": _d(_STRING, "The date the sun is computed for, YYYY-MM-DD."),
            "sun_clock": _d(_STRING, "The clock time the sun is computed for, HH:MM:SS."),
            "elevation_deg": _d(_NUMBER, "Refraction-corrected elevation, degrees."),
            "geometric_elevation_deg": _d(_NUMBER, "Geometric elevation, degrees."),
            "azimuth_deg": _d(_NUMBER, "Azimuth, degrees clockwise from north.")},
            "The sun the session will declare at one instant.")

    @staticmethod
    def _band_table() -> dict:
        counts = _object({state: _COUNT for state in _STATES} | {"total": _COUNT},
                         "How many route entries of each supervision state fall in the band.")
        return {"type": "object", "additionalProperties": False,
                "properties": {band: counts for band in IlluminationBand.names()},
                "description": "Route entries by illumination band and supervision state."}

    # -- the lock ----------------------------------------------------------------------------------------

    @classmethod
    def _lock_defs(cls) -> dict:
        epoch = ScenarioEpoch.schema()
        return {"epoch": {key: value for key, value in epoch.items()
                          if key not in ("$schema", "$id", "title")}}

    @classmethod
    def _lock_body(cls) -> dict:
        sha = _P.SHA256
        file_entry = _object({"path": _d(_TEXT, "The file's name, beside the lock."),
                              "sha256": sha}, "One file the lock digests.")
        files = _object({role: file_entry for role in FILE_ROLES},
                        "Every file the compile wrote that decides the traffic or the "
                        "supervision, by role: the routes, the .sumocfg, the network, the lane "
                        "closures where there are any, and the supervision plan.",
                        required=[r for r in FILE_ROLES if r not in _OPTIONAL_FILE_ROLES])
        dry_run = {"anyOf": [
            _object({"ran": {"const": True},
                     "sumo_release": _STRING, "begin_s": _NUMBER, "end_s": _NUMBER,
                     "vehicles": _object({"loaded": _COUNT, "inserted": _COUNT,
                                          "discarded": _COUNT, "waiting_at_end": _COUNT},
                                         "SUMO's own counts over the run."),
                     "planned_vehicles": _object({"total": _COUNT, "inserted": _COUNT},
                                                 "The vehicles the supervision plan names."),
                     "collisions": _COUNT},
                    "The compiled scenario was run in SUMO alone over its whole span."),
            _object({"ran": {"const": False}, "reason": _STRING},
                    "The SUMO-only run was skipped (--skip-dry-run).")]}
        processing = _object({name: {"const": value} for name, value in PROCESSING_OPTIONS.items()},
                             "The SUMO options the compiler fixes and writes into the .sumocfg.")
        properties = {
            "lock_version": {"const": LOCK_VERSION,
                             "description": "The format version of this file. carla-capture "
                                            "refuses any other (run check 6)."},
            "scenario_id": {"type": "string", "pattern": _IDENTIFIER},
            "scenario_name": _TEXT,
            "spec_version": {"const": SPEC_VERSION},
            "specification": _d(_TEXT, "The specification's file name."),
            "specification_sha256": sha,
            "compiler": _object({"name": {"const": COMPILER},
                                 "version": _d(_TEXT, "The compiler's release, without the "
                                                      "commit.")},
                                "The compiler and its release."),
            "producer": _P.producer(),
            "files": files,
            "world": _object({
                "package": _d(_TEXT, "The world package's file name."),
                "map_name": _STRING,
                "network_fingerprint": _d(sha, "The canonical fingerprint of the network."),
                "netconvert_argv": {"type": "array", "items": _STRING,
                                    "description": "The netconvert invocation that built the "
                                                   "world, machine paths replaced by "
                                                   "<placeholders>."},
                "netconvert_version": _STRING,
                "opendrive_sha256": _STRING, "source_osm_sha256": _STRING,
                "origin_latitude": _d(_NUMBER, "Degrees."),
                "origin_longitude": _d(_NUMBER, "Degrees."),
                "georeference": _d(_STRING, "The PROJ string of the map's projection.")},
                "The world package the scenario was compiled against."),
            "catalogue": _object({"catalogue_id": _STRING, "catalogue_digest": _STRING,
                                  "blueprint_set_digest": _STRING, "content_build_id": _STRING},
                                 "The measured vehicle catalogue."),
            "vocabulary": _object({
                "core_version": _COUNT,
                "namespaces": {"type": "array", "items": _object(
                    {"namespace": _STRING, "version": _COUNT}, "One author namespace.")},
                "vocabulary_digest": sha}, "The vocabulary the labels resolve against."),
            "traffic": _object({
                "sumo_seed": {"type": "integer", "minimum": 0},
                "step_length_s": _d(_NUMBER, "SUMO's step, seconds."),
                "end_s": _d(_NUMBER, "The simulated second the scenario ends."),
                "processing": processing,
                "routed_by": _object({"tool": {"const": "duarouter"}, "version": _STRING,
                                      "world_converter": _STRING,
                                      "release_agreement": {"enum": _RELEASE_AGREEMENTS},
                                      "mismatch_accepted": _BOOLEAN},
                                     "The router, and whether its release is the world "
                                     "converter's.")},
                "What decides how the traffic is placed and moves."),
            "dry_run": dry_run,
            "epoch": {"$ref": "#/$defs/epoch"},
            "epoch_block_sha256": _d(sha, "SHA-256 of the epoch object, keys sorted, two-space "
                                          "indent."),
            "illumination": _d({"type": "object"}, "The specification's illumination default, "
                                                   "verbatim."),
            "capture_windows": {"type": "array", "items": _object({
                "id": _STRING, "begin_s": _NUMBER, "end_s": _NUMBER, "civil_begin": _STRING,
                "civil_end": _STRING, "civil_date": _STRING}, "One declared capture window.")},
            "ephemeris": _d(_STRING, "What computes the sun."),
            "illumination_label_association": _object({
                "normalized_mutual_information": _n(_NUMBER),
                "bands": _n(cls._band_table()),
                "entries": _n(_COUNT)},
                "The headline of the illumination-label association (check 41)."),
        }
        return {"type": "object", "additionalProperties": False, "required": _P.required(properties),
                "properties": properties}

    # -- the resolution report -----------------------------------------------------------------------

    @classmethod
    def _resolution_sections(cls) -> dict:
        instant = {"$ref": "#/$defs/instant"}
        window_sun = {"$ref": "#/$defs/window_sun"}
        open_object = _P.open_object
        place = _object({
            "name": _STRING, "authored": {"type": "object"}, "form": _STRING,
            "edges": {"type": "array", "items": _STRING}, "lane": _n(_STRING),
            "start_pos": _n(_NUMBER), "end_pos": _n(_NUMBER), "street": _STRING,
            "detail": _STRING,
            "edge_details": {"type": "array", "items": _object(
                {"edge": _STRING, "street": _STRING, "length_m": _NUMBER, "speed_mps": _NUMBER},
                "One candidate edge.")}}, "What a declared place became.")
        window = _object({
            "id": _STRING, "begin": instant, "length_s": _NUMBER, "end_s": _NUMBER,
            "end_civil": _STRING, "civil_date": _STRING,
            "sun_open": _n(window_sun), "sun_close": _n(window_sun),
            "sun": _d(_STRING, "Where the default policy binds no sun."),
            "sun_lowest": _object({"elevation_deg": _NUMBER, "elevation_kind": _STRING,
                                   "band": {"enum": IlluminationBand.names()},
                                   "band_source": _STRING},
                                  "The lowest the sun reaches over the window, and its band.")},
            "One capture window, resolved.",
            required=["id", "begin", "length_s", "end_s", "end_civil", "civil_date", "sun_open",
                      "sun_close"])
        body = _object({"blueprint": _STRING, "length_m": _NUMBER, "width_m": _NUMBER,
                        "body_width_m": _n(_NUMBER), "height_m": _NUMBER,
                        "lights": open_object("Whether the body's headlights, brake_lights and "
                                              "turn_signals light up: lit, unlit or unknown.")},
                       "The measured body a vehicle type binds.")
        route = _object({
            "id": _STRING, "type": _STRING, "from": _n(_STRING), "to": _n(_STRING),
            "via": {"type": "array"}, "route": _n({"type": "array", "items": _STRING}),
            "route_length_m": _NUMBER, "free_flow_s": _NUMBER,
            "stops": {"type": "array", "items": {"type": "object"}},
            "phases": {"type": "array"}, "waypoints": _COUNT,
            "depart": _d(instant, "An actor's departure."),
            "origin": _d(_STRING, "Where an actor came from: actor, or rota:<id>."),
            "begin": _d(instant, "A flow's begin."), "end": _d(instant, "A flow's end."),
            "vehs_per_hour": _d(_NUMBER, "A flow's rate.")},
            "One routed actor or flow, as duarouter produced it.",
            required=["id", "type", "from", "to", "via", "route", "route_length_m",
                      "free_flow_s", "stops"])
        closure = _object({
            "id": _STRING, "edge": _STRING, "street": _STRING,
            "lanes": {"type": "array", "items": _STRING},
            "open_lanes": _COUNT, "notify": {"type": "array", "items": _STRING},
            "allow": _d(_STRING, "The vehicle class a closed lane still admits."),
            "begin": instant, "end": instant}, "One lane closure, resolved.")
        dry_run_report = {"anyOf": [
            _object({"ran": {"const": True}}, "What the SUMO-only run showed: the lock's record, "
                                               "every planned vehicle's wait, the other vehicles "
                                               "that never got in, and every collision.",
                    required=["ran", "sumo_release", "end_s", "vehicles", "planned_vehicles",
                              "collisions", "planned", "collision_list"], closed=False),
            _object({"ran": {"const": False}, "reason": _STRING}, "The run was skipped.")]}
        sections = {
            "resolution_version": {"const": RESOLUTION_VERSION,
                                   "description": "The format version of this file."},
            "producer": _P.producer(),
            "outcome": {"enum": ["compiled", "refused"]},
            "scenario": _object({"scenario_id": _STRING, "scenario_name": _n(_STRING),
                                 "description": _n(_STRING), "specification": _STRING,
                                 "specification_sha256": _P.SHA256,
                                 "spec_version": {"const": SPEC_VERSION}},
                                "The specification compiled."),
            "findings": {"type": "array", "items": {"$ref": "#/$defs/finding"},
                         "description": "Every refusal and warning, in full."},
            "epoch": _object({"declared": {"$ref": "#/$defs/epoch"},
                              "epoch_block_sha256": _P.SHA256,
                              "statement": _d(_STRING, "The epoch and the run's end in one "
                                                       "line."),
                              "t0_civil": _STRING, "end_s": _NUMBER, "end_civil": _STRING,
                              "time_zone_id": _n(_STRING),
                              "time_zone_id_resolved": {"const": False}},
                             "The epoch, and when the run ends."),
            "zone": _object({"declared_offset_hours": _NUMBER,
                             "engine_time_zone_hours": _n(_NUMBER),
                             "difference_hours": _n(_NUMBER),
                             "written_by_the_session": _STRING},
                            "The declared offset against the zone the world's georeference "
                            "configures."),
            "illumination_default": _object({"declared": {"type": "object"},
                                             "policy": {"enum": list(ILLUMINATION_POLICIES)},
                                             "status": _STRING,
                                             "declared_elevation_kind": _STRING},
                                            "The specification's illumination default."),
            "capture_windows": {"type": "array", "items": window},
            "illumination_label_association": _object({
                "statistic": _STRING, "band_source": _STRING,
                "band_edges": {"type": "array", "items": _object(
                    {"band": {"enum": IlluminationBand.names()}, "above_deg": _n(_NUMBER)},
                    "One band's lower edge.")},
                "elevation_kind": _STRING, "presence_estimate": _STRING,
                "over_windows": open_object("The association over the declared windows, or "
                                            "not_computed and why."),
                "over_span": open_object("The association over the span at each departure, or "
                                         "not_computed and why."),
                "normalized_mutual_information": _n(_NUMBER),
                "bands": _n(cls._band_table()), "entries": _n(_COUNT),
                "measured_over": {"enum": ["windows", "span"]},
                "remedies": {"type": "array", "items": _STRING}},
                "How far the illumination band predicts the supervision state (check 41)."),
            "world": _object({
                "package": _STRING, "map_name": _STRING, "network_fingerprint": _P.SHA256,
                "netconvert_version": _STRING,
                "origin": {"type": "array", "minItems": 2, "maxItems": 2, "items": _NUMBER,
                           "description": "[latitude, longitude], degrees."},
                "georeference": _STRING,
                "routing_sumo": _object({"version": _STRING, "matched_by": _STRING,
                                         "release_agreement": {"enum": _RELEASE_AGREEMENTS},
                                         "verdict": _STRING},
                                        "The SUMO that routed the scenario.")},
                "The world package the scenario was compiled against."),
            "instants": {"type": "object", "additionalProperties": instant,
                         "description": "Every named instant the specification declares."},
            "places": {"type": "object", "additionalProperties": place,
                       "description": "Every declared place and what it became."},
            "rotas": {"type": "array", "items": _object({
                "id": _STRING, "entries": _COUNT,
                "skips": {"type": "array", "items": _object(
                    {"entry": _STRING, "seconds": _NUMBER, "civil": _STRING, "because": _STRING},
                    "One skipped entry and why.")}}, "One schedule.")},
            "routes": {"type": "array", "items": route},
            "lane_closures": {"type": "array", "items": closure},
            "vehicle_types": _object({
                "catalogue": _object({"path": _STRING, "catalogue_id": _STRING,
                                      "catalogue_digest": _STRING,
                                      "blueprint_set_digest": _STRING}, "The catalogue read."),
                "classes": {"type": "array", "items": _STRING,
                            "description": "One line per class: its bodies and lengths."},
                "mix": _object({"id": _STRING,
                                "probabilities": {"type": "object",
                                                  "additionalProperties": _NUMBER}},
                               "The whole-mix distribution: each vehicle type's probability."),
                "mixes": {"type": "array", "items": _object(
                    {"id": _STRING, "shares": {"type": "object", "additionalProperties": _NUMBER},
                     "probabilities": {"type": "object", "additionalProperties": _NUMBER}},
                    "One named mix.")},
                "types": {"type": "object", "additionalProperties": body,
                          "description": "Every vehicle type id and the body it binds."}},
                "The vehicle types and the bodies they bind."),
            "supervision": _object({
                "instances": {"type": "array", "items": {"$ref": "#/$defs/instance"}},
                "cohorts": {"type": "array", "items": {"$ref": "#/$defs/cohort"}},
                "series": {"type": "array", "items": {"$ref": "#/$defs/report_series"}}},
                "The supervision plan's rows; a series gives its count of slots."),
            "dry_run": dry_run_report,
            "lock": {"$ref": "#/$defs/lock"},
        }
        missing = set(SECTIONS) ^ set(sections)
        if missing:
            raise AssertionError(f"the report's sections and its schema differ: {sorted(missing)}")
        return {name: sections[name] for name in SECTIONS}

    # -- the supervision plan ------------------------------------------------------------------------

    @classmethod
    def _vocabulary(cls) -> dict:
        core = _object({
            "vocabulary_version": _COUNT,
            "source": _STRING,
            "terms": _object({family: {"type": "array", "items": {"enum": terms}}
                              for family, terms in CORE_TERMS.items()},
                             "The closed core: the terms the pipeline's own code branches on, by "
                             "family.")},
            "The core vocabulary.")
        return _object({"core": core,
                        "namespaces": {"type": "array", "items": {"$ref": "#/$defs/namespace"},
                                       "description": "Every author namespace, declared or "
                                                      "imported, by name."}},
                       "The resolved vocabulary every label is a term of.")

    @classmethod
    def _plan_defs(cls) -> dict:
        nullable_number = _n(_NUMBER)
        nullable_text = _n(_STRING)
        parameters = {"type": "object",
                      "additionalProperties": {"type": ["number", "string", "boolean"]},
                      "description": "Values for keys the labels' terms declare."}
        anchor_event = _object({
            "event": {"type": "string", "pattern": _ANCHOR_EVENT},
            "lane": _d(_STRING, "The stop's lane."), "end_pos_m": _d(_NUMBER, "The stop's end "
                                                                              "position, meters."),
            "route_index": _d(_COUNT, "Where in the compiled route the phase is entered."),
            "edge": _d(_STRING, "The edge it is entered on.")},
            "One end of an anchor: depart, stop:<i>, stop_end:<i> or phase:<i>.",
            required=["event"])
        interval = _object({
            "entity_id": _STRING, "phase": _d(_STRING, "The phase's name, as authored."),
            "anchor": _n(_object({"start": anchor_event, "end": _n(anchor_event)},
                                 "The participant's events that commit the interval.")),
            "declared_start_s": nullable_number, "declared_start_civil": nullable_text,
            "declared_end_s": nullable_number, "declared_end_civil": nullable_text,
            "declared_duration_s": nullable_number}, "One declared interval.")
        instance = _object({
            "instance_id": _d({"type": "string", "pattern": "^[^/]+/[a-z0-9_]+$"},
                              "<scenario_id>/<authored name>."),
            "supervision": {"enum": ["annotated", "nominal"]},
            "labels": _TERMS, "parameters": parameters,
            "hard_negative_for": _n(_TERMS, "A nominal row's copy of its terms' "
                                            "hard_negative_for; null where unspecified."),
            "counterfactual": _n({"$ref": "#/$defs/counterfactual"}),
            "aoi_refs": {"type": "array", "items": _STRING},
            "participants": {"type": "array", "items": _object(
                {"entity_id": _STRING, "role": _STRING, "sumo_id": _STRING},
                "One participant and its role.")},
            "intervals": {"type": "array", "items": interval}},
            "One pattern instance.")
        slot = _object({"slot_key": _STRING, "aoi_ref": nullable_text,
                        "declared_start_s": _NUMBER, "declared_start_civil": _STRING,
                        "declared_end_s": nullable_number, "declared_end_civil": nullable_text,
                        "entity_id": _STRING}, "One occasion of the schedule.")
        series_properties = {
            "series_id": {"type": "string", "pattern": "^[a-z0-9_]+$"},
            "rota_ref": _STRING, "cadence": {"const": "enumerated"}, "member_role": _STRING,
            "supervision": {"enum": list(_STATES)},
            "labels": _TERMS, "parameters": parameters,
            "hard_negative_for": _n(_TERMS)}
        series = _object({**series_properties, "slots": {"type": "array", "items": slot}},
                         "A schedule read as a recurring series.")
        report_series = _object({**series_properties,
                                 "slots": _d(_COUNT, "How many slots the series has.")},
                                "A series, its slots counted.")
        cohort = _object({"flow_id": _STRING,
                          "supervision": {"enum": [s for s in _STATES if s != "nominal"]},
                          "labels": _TERMS, "parameters": parameters},
                         "One flow's whole-life supervision.")
        entity = _object({"entity_id": _STRING,
                          "supervision": {"type": "array", "minItems": 1,
                                          "items": {"enum": list(_STATES)}},
                          "refs": {"type": "array", "items": _STRING}},
                         "One authored vehicle's states and the rows naming it.")
        return {"instance": instance, "series": series, "report_series": report_series,
                "cohort": cohort, "entity": entity}
