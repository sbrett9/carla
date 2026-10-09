"""The schemas of a capture run's records: what carla-capture writes beside `--result`.

A run writes four files beside its result path (`RunResult.sibling`), and one block that two of them
carry:

| File | Written | Schema |
|---|---|---|
| `run.result.json` | in every outcome the tool survives, at the end | `result()` |
| `run.resolution.json` | on every path, once the configuration is resolved or refused | `resolution()` |
| `run.lock.json` | only when the offline checks accept | `lock()` |
| `run.effective.json` | with the lock: the run configuration that reproduces the run | `run_configuration.schema.json` |
| the launch echo | inside the result and the resolution report | `launch_echo()` |

Each schema is generated from the code that writes its file: the field table of the run configuration
(`RunConfiguration.FIELDS`) for every field of the effective configuration, the run check catalogue
for the checks and warning codes a finding may cite, the run outcomes and their exit statuses
(`RunResult`), the illumination policies and bands, and the site profile's own record
(`SiteProfile.record_schema`). What a record carries from the co-simulation session -- its report's
figures, a camera's readiness, a channel's aim -- is described to the depth this tool writes it and
left open below, because the session's report is the authority on its own members.

Not to be confused with the scenario compiler's `<scenario>.lock.json` and `<scenario>.resolution.json`
(`ScenarioOutputSchemas`): those describe a compiled scenario, these one run of it.
"""
from __future__ import annotations

from carlacontrol.CompileFinding import OUTCOMES as FINDING_OUTCOMES
from carlacontrol.CompileFinding import REFUSE, WARN
from carlacontrol.EffectiveRunConfiguration import EFFECTIVE_CONFIGURATION_VERSION
from carlacontrol.IlluminationBand import IlluminationBand
from carlacontrol.LaunchEcho import LAUNCH_ECHO_VERSION
from carlacontrol.RunConfiguration import (
    ILLUMINATION_POLICIES,
    LAYER_DERIVED,
    LAYER_NOT_APPLICABLE,
    LAYER_PINNED_BY_POLICY,
    LAYER_UNSUPPLIED,
    LAYERS,
    RunConfiguration,
)
from carlacontrol.RunConfigurationCheckCatalogue import (
    CATALOGUE_NAME,
    RunConfigurationCheckCatalogue,
)
from carlacontrol.RunResult import (
    OUTCOMES,
    RESULT_VERSION,
    RUN_LOCK_VERSION,
    RUN_RESOLUTION_VERSION,
)
from carlacontrol.RunTerminationSequence import PHASE_NAMES
from carlacontrol.SchemaPublication import SchemaPublication
from carlacontrol.SiteProfile import SiteProfile

# What the resolution report says of the launch: the offline checks accepted it, or it was refused
# as an invocation or by the offline checks.
RESOLUTION_OUTCOMES = ("accepted", "usage_error", "refused_offline")
# How the run ended, beside its outcome: a value, or a prefix and a name.
CLOSED_BY = ("window_end", "scenario_end", "aborted_at_preroll", "operator_stop", "write_headroom",
             "signal:<name>", "loud:<condition>", "fault:<exception type>")
_ADJUDICATIONS = ("proceed",)

_P = SchemaPublication
_TEXT = _P.TEXT
_STRING = {"type": "string"}
_NUMBER = {"type": "number"}
_INTEGER = {"type": "integer"}
_COUNT = {"type": "integer", "minimum": 0}
_BOOLEAN = {"type": "boolean"}
_SECONDS = {"type": "number"}


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


class RunRecordSchemas:
    """Generates the schema of each record a capture run writes."""

    # -- the published schemas ----------------------------------------------------------------------

    @classmethod
    def result(cls) -> dict:
        """`run.result.json`: how the run ended, and pointers to everything it wrote."""
        artifact = _n(_TEXT)
        properties = {
            "result_version": {"const": RESULT_VERSION,
                               "description": "The format version of this file."},
            "producer": _P.producer(),
            "outcome": {"enum": list(OUTCOMES),
                        "description": "How the run ended. The exit status is read from it."},
            "exit_status": {"enum": sorted(set(OUTCOMES.values())),
                            "description": "The process exit status: "
                                           + ", ".join(f"{status} {outcome}"
                                                       for outcome, status in OUTCOMES.items())
                                           + "."},
            "closed_by": _n(_TEXT, "What closed the run, where something did: "
                                   + ", ".join(CLOSED_BY) + "."),
            "detail": _n(_STRING, "One line on how the run ended: the first refusal, or the "
                                  "fault."),
            "caller": {"enum": ["attended", "unattended"],
                       "description": "Whether a person watched the launch at a terminal."},
            "caller_label": _n(_STRING, "The caller's own label for the run, never interpreted."),
            "session_id": _d(_TEXT, "The run's identity: cap-<UTC date>-<UTC time>-<six hex "
                                    "digits>. It names the capture directory."),
            "tool_version": _d(_TEXT, "The carlacontrol release, kept beside producer for "
                                      "readers written before it."),
            "schema_version": _n(_INTEGER, "The run configuration format version the run was "
                                           "resolved against; null where nothing was resolved."),
            "effective_configuration_digest": _n(_P.SHA256, "SHA-256 of the replayable run "
                                                             "configuration; null where nothing "
                                                             "was resolved."),
            "resolution_report": _d(artifact, "Path of run.resolution.json; null where none was "
                                              "written."),
            "lock": _d(artifact, "Path of run.lock.json; null where the offline checks did not "
                                 "accept."),
            "effective_configuration": _d(artifact, "Path of run.effective.json; null where the "
                                                    "offline checks did not accept."),
            "launch_echo": _n({"$ref": "#/$defs/launch_echo"},
                              "What the run said it would do before committing; null where the "
                              "offline checks did not get that far."),
            "authority_holder": _n(_STRING, "Who holds the world's population lease, under "
                                            "refused_authority."),
            "refusals": {"type": "array", "items": {"$ref": "#/$defs/refusal"},
                         "description": "Every refusal the launch made."},
            "warnings": {"type": "array", "items": {"$ref": "#/$defs/adjudicated_warning"},
                         "description": "Every warning raised, with how it was adjudicated."},
            "expectations_declared": _d(_COUNT, "How many expect entries the configuration "
                                                "declared."),
            "produced": _n({"$ref": "#/$defs/produced"},
                           "What the run produced and observed; null where it ended before the "
                           "record was opened."),
            "started_wall_utc": _d(_TEXT, "When the launch began, ISO 8601 in UTC."),
            "ended_wall_utc": _d(_TEXT, "When the result was written, ISO 8601 in UTC."),
        }
        return _P.document(
            "run-result", RESULT_VERSION, "Capture run result",
            "How one carla-capture run ended and where everything it wrote is. Written in every "
            "outcome the tool survives, to --result or <paths.runs_root>/<session id>/"
            "run.result.json, outside the capture directory. It records observations and carries "
            "no overall verdict.",
            {"type": "object", "additionalProperties": False, "required": _P.required(properties),
             "properties": properties,
             "$defs": {"launch_echo": cls.launch_echo_block(),
                       "refusal": cls._finding(REFUSE),
                       "adjudicated_warning": cls._adjudicated_warning(),
                       "produced": cls._produced(),
                       **cls._shared_defs()}})

    @classmethod
    def resolution(cls) -> dict:
        """`run.resolution.json`: what the launch resolved and what the checks said."""
        properties = {
            "resolution_version": {"const": RUN_RESOLUTION_VERSION,
                                   "description": "The format version of this file."},
            "producer": _P.producer(),
            "outcome": {"enum": list(RESOLUTION_OUTCOMES),
                        "description": "accepted: the offline checks passed and the lock was "
                                       "written. usage_error or refused_offline: the launch "
                                       "stopped here."},
            "session_id": _d(_TEXT, "The run's identity, as in the run result."),
            "check_catalogue": {"type": "string", "examples": [CATALOGUE_NAME],
                                "description": "The catalogue the findings' check numbers belong "
                                               f"to: {CATALOGUE_NAME}."},
            "findings": {"type": "array", "items": {"$ref": "#/$defs/finding"},
                         "description": "Every refusal and warning, in the order made."},
            "launch_echo": _n({"$ref": "#/$defs/launch_echo"},
                              "The launch echo; null where the offline checks refused first."),
            "effective_configuration": _n({"$ref": "#/$defs/effective_configuration"},
                                          "Every field, its value and where it came from; null "
                                          "where nothing resolved."),
            "site_profile": {"$ref": "#/$defs/site_profile"},
        }
        return _P.document(
            "run-resolution", RUN_RESOLUTION_VERSION, "Capture run resolution report",
            "What one carla-capture launch resolved, every field with the layer that set it, and "
            "every finding of the offline checks. Written beside the run result as "
            "<stem>.resolution.json on every path, whether the launch was accepted or refused. "
            "Not the scenario compiler's <scenario>.resolution.json.",
            {"type": "object", "additionalProperties": False, "required": _P.required(properties),
             "properties": properties,
             "$defs": {"launch_echo": cls.launch_echo_block(),
                       "finding": cls._finding(),
                       **cls.effective_configuration_defs(),
                       "site_profile": SiteProfile.record_schema(),
                       **cls._shared_defs()}})

    @classmethod
    def lock(cls) -> dict:
        """`run.lock.json`: what the run is bound to and how to replay it."""
        inputs = {
            "scenario_lock_sha256": _d(_P.SHA256, "SHA-256 of the scenario's .lock.json."),
            "scenario_id": _d(_TEXT, "The scenario's id."),
            "world_network_fingerprint": _d(_P.SHA256, "The canonical fingerprint of the world's "
                                                       "SUMO network."),
            "world_opendrive_sha256": _d(_STRING, "SHA-256 of the world's OpenDRIVE."),
            "catalogue_digest": _d(_STRING, "The digest of the vehicle catalogue the scenario was "
                                            "compiled against."),
            "epoch_block_sha256": _d(_P.SHA256, "SHA-256 of the scenario's epoch object, in its "
                                                "canonical form."),
            "sumo_seed": _d({"type": "integer", "minimum": 0}, "SUMO's seed."),
        }
        properties = {
            "lock_version": {"const": RUN_LOCK_VERSION,
                             "description": "The format version of this file."},
            "producer": _P.producer(),
            "session_id": _d(_TEXT, "The run's identity, as in the run result."),
            "effective_configuration_sha256": _d(_P.SHA256, "SHA-256 of the replayable run "
                                                            "configuration, run.effective.json "
                                                            "without its producer."),
            "tool_version": _d(_TEXT, "The carlacontrol release that resolved the run."),
            "schema_version": {"const": RunConfiguration.VERSION,
                               "description": "The run configuration format version."},
            "caller": {"enum": ["attended", "unattended"],
                       "description": "Whether a person watched the launch at a terminal."},
            "caller_label": _n(_STRING, "The caller's own label for the run."),
            "inputs": _object(inputs, "The digests and ids of what the run is bound to."),
            "adjudications": {"type": "array", "items": {"$ref": "#/$defs/adjudication"},
                              "description": "Each warning's adjudication, and who made it."},
            "expectations": _d({"type": "object"}, "The configuration's expect block, as "
                                                   "given."),
            "effective_configuration": {"$ref": "#/$defs/effective_configuration"},
            "replay": _object({"configuration_path": _d(_TEXT, "Path of run.effective.json."),
                               "non_interactive": {"const": True,
                                                   "description": "The replay needs no "
                                                                  "terminal."}},
                              "How to run the same configuration again."),
            "site_profile": {"$ref": "#/$defs/site_profile"},
        }
        return _P.document(
            "run-lock", RUN_LOCK_VERSION, "Capture run lock",
            "What one accepted carla-capture run is bound to: the digests of its scenario, world, "
            "catalogue and epoch, every field of its effective configuration with where it came "
            "from, its adjudications and this machine's site profile. Written beside the run "
            "result as <stem>.lock.json, only when the offline checks accept. Not the scenario "
            "compiler's <scenario>.lock.json.",
            {"type": "object", "additionalProperties": False, "required": _P.required(properties),
             "properties": properties,
             "$defs": {"adjudication": cls._adjudication(),
                       **cls.effective_configuration_defs(),
                       "site_profile": SiteProfile.record_schema()}})

    @classmethod
    def launch_echo(cls) -> dict:
        """The launch echo block, published on its own for a reader of either record carrying it."""
        block = cls.launch_echo_block()
        return _P.document(
            "launch-echo", LAUNCH_ECHO_VERSION, "Capture launch echo",
            "What a carla-capture run is about to do, stated before anything is acquired: the "
            "simulated and civil span, the captures, the sun, the world, what is rendered, the "
            "disk it will cost, where it writes, the wait for each view, and the warnings. "
            "Carried as launch_echo in run.result.json and run.resolution.json. An expect entry "
            "names a value here as launch_echo.<path>.",
            {key: value for key, value in block.items() if key != "description"}
            | {"$defs": cls._shared_defs()})

    # -- the effective configuration -----------------------------------------------------------------

    @classmethod
    def effective_configuration_defs(cls) -> dict:
        """The flat effective configuration (`EffectiveRunConfiguration.to_document`) and the entry
        each of its fields resolves to, as `$defs`: every field keyed by its path, with its value and
        where it came from.

        Every field and every channel field is named, and each takes one of two entries: with the
        tool's default beside the value, for a field that has one, or without. A value's own shape is
        its field's in the run configuration's schema, which `run.effective.json` is held to; the
        entry leaves it open, because the resolution report records what was resolved even where a
        check then refused it.
        """
        def entry(has_default: bool) -> dict:
            return {"$ref": "#/$defs/resolved_field_with_default" if has_default
                    else "#/$defs/resolved_field"}

        fields = {path: entry(spec.has_default) for path, spec in RunConfiguration.FIELDS.items()}
        channel = {name: entry(spec.has_default)
                   for name, spec in RunConfiguration.channel_fields().items()}
        block = _object({
            "effective_configuration_version": {"const": EFFECTIVE_CONFIGURATION_VERSION,
                                                "description": "The format version of this "
                                                               "block."},
            "tool_version": _d(_TEXT, "The carlacontrol release that resolved it."),
            "schema_version": {"const": RunConfiguration.VERSION,
                               "description": "The run configuration format version."},
            "digest": _d(_P.SHA256, "SHA-256 of the replayable run configuration."),
            "fields": _object(fields, "Every run configuration field, keyed by its dotted path."),
            "channels": {"type": "array",
                         "items": _object(channel, "One channel, every field of it."),
                         "description": "Each channel's fields, in the order declared."},
        }, "Every field the run is subject to, its value, the layer that set it and where that "
           "layer read it.")
        return {"effective_configuration": block,
                "resolved_field": cls._resolution(has_default=False),
                "resolved_field_with_default": cls._resolution(has_default=True)}

    @staticmethod
    def _resolution(has_default: bool) -> dict:
        """One resolved field (`FieldResolution.to_dict`)."""
        layers = [*LAYERS, LAYER_DERIVED, LAYER_PINNED_BY_POLICY, LAYER_NOT_APPLICABLE,
                  LAYER_UNSUPPLIED]
        earlier = {"type": "object", "additionalProperties": False,
                   "required": ["layer", "value", "provenance"],
                   "properties": {"layer": {"enum": layers},
                                  "value": {"description": "The value that layer gave."},
                                  "provenance": _STRING}}
        properties = {
            "value": {"description": "The value the run uses, of the field's type in the run "
                                     "configuration's schema; null where the field does not apply "
                                     "or nothing supplied it."},
            "layer": {"enum": layers,
                      "description": "The layer that set it: a configuration layer, or derived, "
                                     "pinned_by_policy, not_applicable or unsupplied."},
            "provenance": _d(_STRING, "The file and key, or the command-line text, it was read "
                                      "from."),
            "overridden": _n({"type": "array", "items": earlier},
                             "What lower layers gave and a higher one replaced; null where "
                             "nothing was replaced."),
            "dropped": {"type": "array", "items": earlier,
                        "description": "An illumination value of a lower layer dropped because a "
                                       "higher layer chose a policy that does not take it."},
            "note": _d(_STRING, "A note, such as a binding restated with the same value."),
            "environment_variable": _d(_STRING, "The environment variable the value was read "
                                                "from."),
        }
        required = ["value", "layer", "provenance", "overridden"]
        if has_default:
            properties["tool_default"] = {"description": "The tool's default, carried even where "
                                                         "another layer set the value."}
            required.append("tool_default")
        return {"type": "object", "additionalProperties": False, "required": required,
                "properties": properties,
                "description": "One field: its value, the layer that set it and where that layer "
                               "read it" + (", and the tool's default." if has_default else ".")}

    # -- findings ------------------------------------------------------------------------------------

    @staticmethod
    def _finding(outcome: str | None = None) -> dict:
        """A finding of the run checks (`RunConfigurationFindings.finding_dict`)."""
        checks = sorted(check.check_id for check in RunConfigurationCheckCatalogue.all())
        properties = {
            "check": {"enum": checks,
                      "description": "The run check's number in the carla-capture run checks."},
            "outcome": ({"const": outcome} if outcome else {"enum": list(FINDING_OUTCOMES)})
            | {"description": "refuse: the launch stops. warn: it goes on, and the warning must be "
                              "adjudicated."},
            "subject": _d(_STRING, "What the finding is about: a field path, a channel, a "
                                   "package."),
            "message": _d(_STRING, "The finding in full."),
            "catalogue": {"type": "string", "examples": [CATALOGUE_NAME],
                          "description": f"The catalogue the check belongs to: {CATALOGUE_NAME}."},
            "code": {"enum": list(RunConfigurationCheckCatalogue.warning_codes()),
                     "description": "A warning's code, which on_warning.<code> adjudicates. "
                                    "Present on a warning only."},
        }
        return {"type": "object", "additionalProperties": False,
                "required": ["check", "outcome", "subject", "message", "catalogue"],
                "properties": properties}

    @classmethod
    def _adjudicated_warning(cls) -> dict:
        warning = cls._finding(WARN)
        return {**warning, "required": [*warning["required"], "code", "adjudication",
                                        "adjudicated_by"],
                "properties": {**warning["properties"],
                               "adjudication": _n({"enum": list(_ADJUDICATIONS)},
                                                  "How the warning was adjudicated; null where "
                                                  "nothing adjudicated it."),
                               "adjudicated_by": _n(_STRING, "Where the adjudication came from: "
                                                             "the on_warning field's source, or "
                                                             "the operator at the terminal.")}}

    @staticmethod
    def _adjudication() -> dict:
        return _object({"code": {"enum": list(RunConfigurationCheckCatalogue.warning_codes())},
                        "adjudication": {"enum": list(_ADJUDICATIONS)},
                        "adjudicated_by": _d(_TEXT, "The on_warning field's source, or the "
                                                    "operator at the terminal.")},
                       "One warning code, adjudicated.")

    # -- the launch echo ------------------------------------------------------------------------------

    @classmethod
    def launch_echo_block(cls) -> dict:
        """The launch echo (`LaunchEcho.compute`)."""
        region = RunConfiguration.FIELDS["capture.render_region"].schema
        sun_at = {"$ref": "#/$defs/sun_at"}
        sun = {"anyOf": [
            _object({"policy": {"enum": list(ILLUMINATION_POLICIES)},
                     "binds": {"const": False},
                     "statement": _STRING},
                    "A policy that binds no sun: the world's own is left as it is."),
            _object({"policy": {"enum": list(ILLUMINATION_POLICIES)},
                     "binds": {"const": True},
                     "advances": _d(_BOOLEAN, "Whether the sun moves across the window."),
                     "rate": _n(_NUMBER, "Sun-clock seconds per simulated second, under advance."),
                     "elevation_kind": _d(_STRING, "What the elevations are: refraction-corrected "
                                                   "or geometric."),
                     "window_open_s": _d(_SECONDS, "The simulated second the window opens and "
                                                   "the sun is bound."),
                     "at_begin": sun_at, "at_end": sun_at,
                     "held_at": _d(_STRING, "The sun's date and clock where it is held."),
                     "held_at_note": _d(_STRING, "When the sun is pinned, and what the prewarm is "
                                                 "lit by.")},
                    "A policy that binds the sun, and the sun at the window's first and last "
                    "instant.",
                    required=["policy", "binds", "advances", "rate", "elevation_kind",
                              "window_open_s", "at_begin", "at_end"])]}
        properties = {
            "launch_echo_version": {"const": LAUNCH_ECHO_VERSION,
                                    "description": "The format version of this block."},
            "session_id": _d(_TEXT, "The run's identity."),
            "caller": {"enum": ["attended", "unattended"]},
            "scenario": _object({
                "scenario_id": _TEXT,
                "lock": _d(_TEXT, "The scenario as <scenario_id>@<first 12 digits of its lock's "
                                  "SHA-256>."),
                "window": _d(_TEXT, "The declared window's id, or explicit."),
                "dry_run": _object({"ran": _d(_BOOLEAN, "Whether the compile ran the scenario in "
                                                        "SUMO alone."),
                                    "skipped_accepted": _d(_BOOLEAN, "Whether a skipped run was "
                                                                     "accepted for this launch."),
                                    "statement": _STRING},
                                   "What the scenario lock records of the compiler's SUMO-only "
                                   "run.")}, "The scenario and window."),
            "simulated": _object({
                "begin_s": _d(_SECONDS, "Where the window begins, simulated seconds."),
                "end_s": _d(_SECONDS, "Where it ends."),
                "length_s": _d(_SECONDS, "Its length."),
                "end_source": _d(_STRING, "Where the end came from."),
                "prewarm_s": _d(_SECONDS, "Seconds rendered before the window and not recorded."),
                "first_rendered_s": _d(_SECONDS, "The first simulated second rendered.")},
                "The simulated span."),
            "captures": _object({
                "capture_hz": _d(_NUMBER, "Captures per simulated second on each channel."),
                "channels": _d(_COUNT, "How many channels."),
                "per_channel": _d(_COUNT, "Captures each channel will make."),
                "total": _d(_COUNT, "Captures over every channel."),
                "frames_per_hour": _d(_COUNT, "Captures per simulated hour over every channel.")},
                "The captures the window will make."),
            "civil": _object({
                "begin": _d(_STRING, "The window's begin in civil time, ISO 8601 with offset."),
                "end": _d(_STRING, "Its end."),
                "first_rendered": _d(_STRING, "The first rendered instant."),
                "epoch": _d(_STRING, "The epoch in one line.")}, "The civil span."),
            "sun": _d(sun, "The policy holding the sun, and the sun it binds."),
            "world": _object({
                "map_name": _STRING, "package": _d(_STRING, "The world package's file name."),
                "network_fingerprint": _P.SHA256,
                "origin": {"type": "array", "minItems": 2, "maxItems": 2, "items": _NUMBER,
                           "description": "[latitude, longitude] of the map's origin, degrees."}},
                "The world."),
            "render": _object({
                "set": {"enum": list(RunConfiguration.FIELDS["capture.render_set"].schema["enum"])},
                "region": region,
                "hysteresis_m": _NUMBER, "min_pixels": _NUMBER, "admit_lead_s": _NUMBER,
                "release_lag_s": _NUMBER, "cap": _n(_INTEGER),
                "limited": _d(_BOOLEAN, "Whether an optional limit leaves vehicles without a "
                                        "body."),
                "vehicles": _d(_STRING, "Which vehicles get a body, in words."),
                "left_out": _n(_STRING, "What a limit leaves out; null with no limit."),
                "road_layer_visible": _BOOLEAN, "signal_layer_visible": _BOOLEAN,
                "draw_distance_m": _n(_NUMBER), "draw_distance": _d(_STRING, "The draw distance "
                                                                             "in words.")},
                "What is rendered: the render set's fields as resolved."),
            "cost": _object({
                "bytes_per_captured_second": _NUMBER,
                "estimated_bytes": _d(_NUMBER, "The window's estimated size on disk."),
                "free_bytes": _d(_NUMBER, "Free space where captures are written."),
                "headroom_s": _n(_NUMBER, "Captured seconds the free space holds."),
                "basis": _STRING}, "The disk the window will cost."),
            "writes": _object({"capture_directory": _STRING, "result_path": _STRING},
                              "Where the run writes."),
            "pacing": _object({"mode": {"enum": ["as_available", "wall_clock"]},
                               "real_time_factor": _n(_NUMBER),
                               "min_achieved_factor": _n(_NUMBER)}, "The pacing."),
            "readiness": cls._echo_readiness(),
            "warnings": {"type": "array",
                         "items": {"enum": list(RunConfigurationCheckCatalogue.warning_codes())},
                         "description": "The codes of the warnings the offline checks raised."},
            "not_predicted": {"type": "array", "items": _STRING,
                              "description": "What the echo cannot say before the run."},
        }
        return _object(properties, "What the run is about to do, stated before anything is "
                                   "acquired.")

    @staticmethod
    def _echo_readiness() -> dict:
        properties = {
            "waits": {"const": True}, "rule": _STRING,
            "tiles": _d(_STRING, "What a view's tiles being in means."),
            "picture_settled_wait": _d(_BOOLEAN, "Whether the picture is also waited on."),
            "picture": _d(_STRING, "What the picture's wait compares, or that it is not run."),
            "vehicles": _STRING,
            "tiles_ceiling_s": _d(_NUMBER, "Wall-clock seconds a view has for its tiles."),
            "tiles_hold_s": _d(_NUMBER, "The tiles' lead before the window, simulated seconds."),
            "picture_ceiling_frames": _COUNT, "picture_ceiling_s": _NUMBER,
            "picture_tolerance_levels": _NUMBER,
            "from_s": _d(_SECONDS, "The simulated second the wait begins."),
            "until_s": _d(_SECONDS, "The simulated second the window opens."),
            "traffic_stare_holds_from_s": _n(_SECONDS, "Where a stare aimed at the traffic holds "
                                                       "its pose; null with no such stare."),
            "not_ready": _STRING, "per_capture": _STRING,
        }
        return _object(properties, "The wait for every channel's view before the window opens.",
                       required=["waits", "rule", "tiles", "picture_settled_wait", "picture",
                                 "tiles_ceiling_s", "from_s", "until_s",
                                 "traffic_stare_holds_from_s", "not_ready", "per_capture"])

    @staticmethod
    def _shared_defs() -> dict:
        return {"sun_at": _object({
            "seconds": _d(_SECONDS, "The simulated second."),
            "sun_date": _d(_STRING, "The date the sun is computed for, YYYY-MM-DD."),
            "sun_clock": _d(_STRING, "The clock time the sun is computed for, HH:MM:SS."),
            "elevation_deg": _d(_NUMBER, "Refraction-corrected elevation, degrees."),
            "geometric_elevation_deg": _d(_NUMBER, "Geometric elevation, degrees."),
            "azimuth_deg": _d(_NUMBER, "Azimuth, degrees clockwise from north."),
            "band": {"enum": IlluminationBand.names(),
                     "description": "The illumination band of the elevation."}},
            "The declared sun at one instant.")}

    # -- what the run produced -----------------------------------------------------------------------

    @classmethod
    def _produced(cls) -> dict:
        """The result's `produced` block (`CaptureSession._close_the_record`)."""
        count = _n(_COUNT)
        channel_counts = ("captured", "written", "recorder_dropped", "frame_unpaired",
                          "illumination_paired", "illumination_unpaired", "solar_block_missing",
                          "render_set_paired", "render_set_unpaired", "supervision_paired",
                          "supervision_unpaired", "lights_unknown", "pose_source_unknown",
                          "occlusion_measured", "occlusion_unmatched", "sensor_pose_from_snapshot",
                          "sensor_pose_header_disagreed", "sensor_pose_from_header",
                          "depth_pose_header_disagreed", "depth_pose_from_header",
                          "draw_distance_captures", "vehicles_beyond_draw_distance",
                          "vehicles_partly_beyond_draw_distance")
        channel = _object({"sensor_id": _d(_TEXT, "The channel's camera name."),
                           "directory": _d(_TEXT, "The channel's capture directory."),
                           **{name: count for name in channel_counts}},
                          "One channel's recorder counts; null where its recorder did not count "
                          "it.")
        gate = _object({
            "id": _d(_TEXT, "The gate, such as capture.recorder_dropped[<sensor_id>]."),
            "name": _d(_STRING, "What it observes."),
            "owner": _d(_STRING, "What publishes the observation."),
            "status": {"enum": ["evaluated", "skipped"]},
            "observed": {"description": "What was observed: a number or a boolean; null where "
                                        "skipped."},
            "threshold": {"description": "What it is compared against; null where skipped."},
            "comparison": _n({"enum": ["equals", "at_least", "at_most"]}),
            "met": _n(_BOOLEAN, "Whether the observation met the threshold; null where skipped."),
            "skip_reason": _d(_STRING, "Why the gate was skipped.")},
            "One gate record: an observation against a threshold, never a verdict.",
            required=["id", "name", "owner", "status", "observed", "threshold", "comparison",
                      "met"])
        admission_pass = _n(_object({
            "sim_time_s": _SECONDS, "world_tick": _COUNT, "population": _COUNT,
            "newly_admitted": _COUNT, "released": _COUNT, "total_admissions": _COUNT,
            "limited": _BOOLEAN, "eligible": _COUNT, "admitted": _COUNT, "shed": _COUNT,
            "held": _COUNT, "capacity": _n(_COUNT), "rule": _STRING,
            "left_out": _d(_COUNT, "Vehicles SUMO had and the render set gave no body.")},
            "One admission pass of the render set."))
        admissions = _n(_object({
            "at_window_open": admission_pass,
            "window": _object({"passes": _COUNT, "most_population": _n(_COUNT),
                               "limited": _BOOLEAN, "passes_leaving_out": _COUNT,
                               "most_left_out": _n(_COUNT), "most_shed": _n(_COUNT),
                               "first_frame_s": _n(_SECONDS), "last_frame_s": _n(_SECONDS)},
                              "A summary of the passes inside the window.")},
            "The render set's admission passes as the window saw them."))
        step = _object({"phase": {"enum": list(PHASE_NAMES.values())},
                        "step": _STRING, "ran": _BOOLEAN, "completed": _BOOLEAN,
                        "failure": _n(_STRING)}, "One shutdown step as it ran.")
        properties = {
            "capture_directory": _d(_TEXT, "Where the captures are: one directory per channel."),
            "world_truth_track": _d(_TEXT, "Path of truth/world_truth_track.csv."),
            "run_manifest": _d(_TEXT, "Path of truth/manifest.jsonl."),
            "closed_by": _n(_STRING, "What closed the run."),
            "window": _object({
                "name": _n(_STRING, "The declared window's id; null for an explicit one."),
                "begin_s": _SECONDS, "end_declared_s": _SECONDS,
                "end_source": _STRING,
                "end_reached_s": _n(_SECONDS, "The simulated second the run reached."),
                "civil": {"type": "array", "minItems": 2, "maxItems": 2, "items": _n(_STRING),
                          "description": "The begin and the end reached in civil time."}},
                "The window as declared and as reached."),
            "channels": {"type": "array", "items": channel},
            "cameras": {"type": "array", "items": _P.open_object(
                "Where one channel's camera looked: sensor_id, pattern, and for a stare its form, "
                "look_at and pose, for an orbit the point it circles with radius_m, altitude_m and "
                "period_s, and the exposure the camera was given.")},
            "readiness": _n(_P.open_object(
                "How each view became ready before the window: the rule, the ceilings, and per "
                "channel its state and when its tiles were in.")),
            "gates": {"type": "array", "items": gate},
            "admissions": admissions,
            "session": _P.open_object(
                "What the co-simulation session established: its clock, SUMO, pace, sun, layers, "
                "drive lease, render set and draw distance in words, its compile lock and "
                "teleporting checks, and the last snapshot's figures (pacing, render, admission, "
                "illumination, solar_audit, divergence)."),
            "preroll_achieved_factor": _n(_NUMBER, "The real-time factor the prewarm achieved."),
            "recorder_run_id": _d(_TEXT, "The run id the recorders stamped: the session id."),
            "termination": {"type": "array", "items": step,
                            "description": "The shutdown steps, in order."},
        }
        return _object(properties, "What the run produced and observed.",
                       required=[key for key in properties if key != "termination"])
