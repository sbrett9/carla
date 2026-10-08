"""A capture run's configuration document, and the schema every layer of it is checked against.

`12_Operator_Control_Surface.md` §3.4-§3.7: a run is described by layers -- tool defaults, the site
profile, the world package's bindings, the scenario package's declarations, a run configuration and
operator overrides -- resolved into one effective configuration. This class is the run configuration
as a document (layer 5), unresolved, and the one definition of every field any layer can supply: its
path, its shape, its tool default, its mutability class, and its help text. The published schema
(`schema()`), `run_capture --help` and the resolver all read `FIELDS`, so the field names an assistant
reads in the schema are the ones the tool validates, and a default exists once.

**Field names are the tree's.** Where a field sets something the co-simulation session, the recorder
or the camera blueprint already takes, it carries that thing's own name: the illumination fields are
the `illumination` object `CarlaNet.CoSim.IlluminationPolicy` reads, and a channel's fields are
`ChannelDescription`'s, with their defaults read from it. By default no field limits what is
rendered: the scenario is the only arbiter of population, and the session draws every vehicle SUMO
has at any range. Two optional performance controls are off unless set: `capture.draw_distance_m`
limits how far from a camera a body is drawn and nothing else, and `capture.render_set` with its
`capture.render_*` fields limits which vehicles get a body at all -- a vehicle outside that limit is
simulated by SUMO and is not in CARLA or the truth.

**Positions are in CARLA's frame** -- metres, x east, y south, so north is -y -- as a channel's are.
The session takes its render region in SUMO's frame (y north); `CaptureSession` negates y once, at
the call.

A key the schema does not name is refused, with the nearest names it does (check 1); a channel key
that is the camera blueprint's own name for part of the exposure (`iso`, `shutter_speed`, ...) is
refused naming the channel field that sets it, in the run's units. A `world_build` block gets a refusal
of its own, because a capture run binds a world and never builds one (check 38).

**A channel's exposure is five fields** (`ChannelExposure`): the method, ISO, shutter, aperture and
compensation, each defaulting to the `Default` profile's value, every one sent to the camera so the run
states every capture's exposure. Check 16 refuses a value the camera cannot take as stated and warns of
`histogram`; it runs offline, in `RunConfigurationValidator`, on the resolved channel.
"""
from __future__ import annotations

import difflib
import json
import re
import typing
from dataclasses import dataclass, fields
from pathlib import Path
from typing import Any, ClassVar

from carlacontrol.CameraName import CameraName
from carlacontrol.ChannelDescription import ChannelDescription
from carlacontrol.ChannelExposure import METHODS as EXPOSURE_METHODS
from carlacontrol.ChannelExposure import ChannelExposure
from carlacontrol.FormatVersion import FormatVersion, FormatVersionError
from carlacontrol.RunConfigurationFindings import (
    RunConfigurationFindings,
    RunConfigurationRefusedError,
)
from carlacontrol.ScenarioSchema import ScenarioSchema
from carlacontrol.ViewReadiness import (
    PICTURE_CEILING_FRAMES,
    PICTURE_SETTLED_WAIT,
    PICTURE_TOLERANCE_LEVELS,
    TILES_HOLD_S,
)

RUN_CONFIGURATION_VERSION = 1
# What wrote a document a tool wrote -- a run's replayable run.effective.json -- as every file our
# tools write records it (carlacontrol.ProducerRecord). Provenance and not configuration: accepted,
# never read as a field, and outside the digest of the configuration it describes.
PRODUCER_KEY = "producer"


class _NoDefault:
    """The marker for a field no tool default exists for: a layer supplies it, or the run is refused."""

    def __repr__(self) -> str:
        return "NO_DEFAULT"


NO_DEFAULT = _NoDefault()

# Mutability classes, 12 §5.1.
BOUND = "bound"
SESSION_FIXED = "session_fixed"
RUN_MUTABLE = "run_mutable"
LAUNCH_PROVENANCE = "launch_provenance"

# Who may supply a field below the run configuration.
SUPPLIED_BY_TOOL = "tool"
SUPPLIED_BY_SITE = "site"
SUPPLIED_BY_WORLD = "world"
SUPPLIED_BY_SCENARIO = "scenario"

# The layers, lowest precedence first, and the names a resolved field's `layer` may carry besides.
LAYER_TOOL_DEFAULT = "tool_default"
LAYER_SITE_PROFILE = "site_profile"
LAYER_WORLD_PACKAGE = "world_package"
LAYER_SCENARIO_PACKAGE = "scenario_package"
LAYER_RUN_CONFIGURATION = "run_configuration"
LAYER_OPERATOR_OVERRIDE = "operator_override"
LAYERS = (LAYER_TOOL_DEFAULT, LAYER_SITE_PROFILE, LAYER_WORLD_PACKAGE, LAYER_SCENARIO_PACKAGE,
          LAYER_RUN_CONFIGURATION, LAYER_OPERATOR_OVERRIDE)
LAYER_DERIVED = "derived"
LAYER_PINNED_BY_POLICY = "pinned_by_policy"
LAYER_NOT_APPLICABLE = "not_applicable"
LAYER_UNSUPPLIED = "unsupplied"

MODES = ("sumo_driven_playback", "traffic_manager_ambient", "storyboard_execution",
         "recorded_replay")
ILLUMINATION_POLICIES = ("freeze_at_window_start", "advance", "freeze_at", "ignore")

# The post-process profiles the content tree ships (Content/Carla/Config/PostProcess/*.json).
POST_PROCESS_PROFILES = ("Default", "GoPro", "Town10HD_Opt", "Town_C")

# The camera blueprint's own names for the exposure (upstream CARLA's), each with the channel field that
# sets it: a channel names the field, in the run's units, never the attribute.
_BLUEPRINT_EXPOSURE_NAMES = {attribute: name for name, attribute in ChannelExposure.ATTRIBUTES.items()}
_WORLD_BUILD_KEY = "world_build"

_POSITIVE = {"type": "number", "exclusiveMinimum": 0}
_NON_NEGATIVE = {"type": "number", "minimum": 0}
_NUMBER = {"type": "number"}
_POSITIVE_INTEGER = {"type": "integer", "minimum": 1}
_BOOLEAN = {"type": "boolean"}
_TEXT = {"type": "string", "minLength": 1}
_SHA256 = {"type": "string", "pattern": "^[0-9a-f]{64}$"}
_CIVIL_TIME = {"type": "string", "pattern": "^[0-2][0-9]:[0-5][0-9]:[0-5][0-9]$"}

_CHANNEL_INDEX = re.compile(r"^capture\.channels\[(\d+)\]\.([A-Za-z0-9_]+)$")


def _nullable(fragment: dict) -> dict:
    return {"anyOf": [fragment, {"type": "null"}]}


@dataclass(frozen=True)
class RunField:
    """One field of the run configuration: where it lives, its shape, and its tool default.

    `schema` is written in the subset of JSON Schema `ScenarioSchema` validates, so the one
    validator checks both documents. `supplied_by` names the layer below the run configuration that
    may give it a value; a `BOUND` field may be restated by a higher layer only with the value its
    binding gave it (check 3).
    """

    path: str
    schema: dict
    default: Any = NO_DEFAULT
    mutability: str = SESSION_FIXED
    supplied_by: str = SUPPLIED_BY_TOOL
    help: str = ""
    alias: str | None = None

    @property
    def has_default(self) -> bool:
        return self.default is not NO_DEFAULT

    @property
    def bound(self) -> bool:
        return self.mutability == BOUND

    @property
    def group(self) -> str:
        return self.path.split(".", 1)[0] if "." in self.path else "run"

    def accepts_text(self) -> bool:
        """Whether an override's text is taken as the value itself rather than parsed as JSON."""
        return self._accepts(self.schema, "string")

    @classmethod
    def _accepts(cls, schema: dict, kind: str) -> bool:
        if schema.get("type") == kind:
            return True
        return any(cls._accepts(branch, kind) for branch in schema.get("anyOf", []))

    def published(self) -> dict:
        """The field as the published schema states it: its shape, its default, its class."""
        entry = dict(self.schema)
        entry["description"] = self.help
        if self.has_default:
            entry["default"] = self.default
        entry["x-mutability"] = self.mutability
        entry["x-supplied-by"] = self.supplied_by
        return entry


_F = RunField
_FIELDS: tuple[RunField, ...] = (
    # -- the run ----------------------------------------------------------------------------------
    _F("run_configuration_version", {"const": RUN_CONFIGURATION_VERSION},
       RUN_CONFIGURATION_VERSION, LAUNCH_PROVENANCE,
       help="The version of this schema the document is written against."),
    _F("mode", {"type": "string", "enum": list(MODES)}, NO_DEFAULT, SESSION_FIXED,
       SUPPLIED_BY_SCENARIO,
       help="Which authority drives the world. A scenario package implies "
            "sumo_driven_playback, the only mode this front end runs; the traffic-manager path is "
            "run_SCTMV.py."),
    _F("caller", {"type": "string", "enum": ["attended", "unattended"]}, "attended",
       SESSION_FIXED, alias="--caller",
       help="attended: a person reads the launch echo and adjudicates warnings at the terminal. "
            "unattended: warnings are adjudicated in advance by on_warning, and result_path is "
            "required. Recorded, because a dataset whose warnings a file adjudicated is not one a "
            "person watched."),
    _F("caller_label", _nullable(_TEXT), None, LAUNCH_PROVENANCE, alias="--caller-label",
       help="An opaque label the caller recognizes its own run by. Recorded and never "
            "interpreted."),
    _F("scenario_package", _TEXT, NO_DEFAULT, SESSION_FIXED, alias="--scenario",
       help="The compiled scenario: a scenario id (found as <id>/<id>.lock.json under "
            "paths.scenario_root), a scenario package directory, or its .lock.json. A capture "
            "binds a compiled scenario; it never compiles one."),
    _F("world_package", _nullable(_TEXT), None, SESSION_FIXED,
       help="The .cwp the world was built as. Unset, it is the package the scenario lock names, "
            "under paths.world_package_root. Its contents are bound, never overridden."),
    _F("result_path", _nullable(_TEXT), None, SESSION_FIXED, alias="--result",
       help="Where the run result is written, outside the capture root. Unset, it is "
            "<paths.runs_root>/<session id>/run.result.json; required under caller unattended. "
            "The resolution report, the lock and the replayable effective configuration are "
            "written beside it."),
    _F("write_headroom_floor_s", _NON_NEGATIVE, 600.0, SESSION_FIXED,
       help="Captured seconds of free disk below which the run stops itself cleanly, "
            "closed_by write_headroom. The one bound the tool imposes on a run."),
    _F("monitor", {"type": "string", "enum": ["on", "off"]}, "on", RUN_MUTABLE,
       help="The live monitor: a panel on a terminal, a log line every few seconds otherwise."),
    _F("collision_detail", {"type": "string", "enum": ["on", "off"]}, "off", SESSION_FIXED,
       alias="--collision-detail",
       help="What the run prints about collisions. off: their count, in the session's report. on: "
            "every collision as it ends, and every collision and every collision warning SUMO wrote in "
            "the session's report. Printing only: the record of collisions is kept in full either way, "
            "and nothing about the traffic or the truth depends on it."),
    _F("on_warning", {"type": "object",
                      "additionalProperties": {"type": "string", "enum": ["proceed", "refuse"]}},
       {}, SESSION_FIXED,
       help="The adjudication of a warning, by code, made in advance: proceed or refuse. Under "
            "caller unattended every warning raised must have one."),
    _F("expect", {"type": "object"}, {}, LAUNCH_PROVENANCE,
       help="What the caller believes a field or a launch-echo value resolves to, by path. A "
            "value is compared for equality; {\"at_most\": x} or {\"at_least\": x} bounds a "
            "number. An expectation never supplies a value; a failed one refuses the launch."),
    # -- the capture ------------------------------------------------------------------------------
    _F("capture.window", _TEXT, NO_DEFAULT, SESSION_FIXED, alias="--window",
       help="Which simulated time to render: a window the scenario declares, by id; "
            "'<begin_s>:<end_s>'; or '<begin_s>:' with no end, which runs to the scenario's end "
            "unless the caller stops it first."),
    _F("capture.prewarm_s", _NON_NEGATIVE, 300.0, SESSION_FIXED,
       help="Simulated seconds rendered before the window opens and not recorded, so every "
            "camera's view is ready -- its tiles in, and its picture settled where "
            "capture.picture_settled_wait is true -- before the first capture. A view "
            "not ready by the window's opening refuses the run; the window is not moved."),
    _F("capture.world_delta_s", _POSITIVE, 0.05, SESSION_FIXED,
       help="Simulated seconds per world tick. The SUMO step must be a whole number of them."),
    _F("capture.capture_hz", _POSITIVE, 2.0, SESSION_FIXED,
       help="Captures per simulated second, on every channel. One capture must be a whole number "
            "of world ticks."),
    _F("capture.picture_settled_wait", _BOOLEAN, PICTURE_SETTLED_WAIT, SESSION_FIXED,
       help="Whether the pre-roll also waits, once a camera's photoreal tiles are in, for its "
            "picture to settle (checks 50 and 51). Off by default until the wait is fixed: it "
            "places each compared frame's rendered vehicles from the client's snapshot of that "
            "frame and is asked once per SUMO step, after the step's last tick, so with many ticks "
            "to a step -- twenty at Bahonar's 1 s step -- the client no longer holds the older "
            "frame's snapshot, every comparison is vehicles_unknown and the pre-roll refuses; it "
            "has also proved too strict. Off, the "
            "pre-roll waits for the tiles alone, through capture.tiles_hold_s; no camera frame is "
            "compared, and capture.picture_ceiling_frames and capture.picture_tolerance_levels are "
            "not read. Set it true to run the wait as it stands."),
    _F("capture.tiles_hold_s", _POSITIVE, TILES_HOLD_S, SESSION_FIXED,
       help="Read only where capture.picture_settled_wait is false, its default: the photoreal "
            "tiles' own lead before the window, in simulated seconds. A stare aimed at "
            "the rendered traffic stops following it and holds the pose the window opens on this "
            "long before the window, rounded up to whole SUMO steps, so its tiles are asked about "
            "after every step of the hold; and every camera's prewarm must be at least this long, "
            "a stare aimed at the traffic one SUMO step longer to measure it on (check 51). 10 s "
            "covers every cold tile load measured, 30 to 124 ticks at 0.05 s, 1.5 to 6.2 s; one "
            "outlier took 712 ticks, 35.6 s. A view whose tiles are not in as the window opens "
            "still refuses the run (check 50). Where capture.picture_settled_wait is true, the "
            "picture's ceiling sets the hold instead."),
    _F("capture.picture_ceiling_frames", _POSITIVE_INTEGER, PICTURE_CEILING_FRAMES, SESSION_FIXED,
       help="Read only where capture.picture_settled_wait is true. How many of its own frames a "
            "camera has, from its photoreal tiles being in, to settle its picture before the run is "
            "refused at pre-roll (check 50): 60 is 30 s at 2 Hz. Counted in the camera's "
            "frames, not ticks, because what the renderer settles advances once per frame the camera "
            "draws. The prewarm must hold this many frames at the capture rate and the ten-tick "
            "comparison span (check 51)."),
    _F("capture.picture_tolerance_levels", _POSITIVE, PICTURE_TOLERANCE_LEVELS, SESSION_FIXED,
       help="Read only where capture.picture_settled_wait is true. The gray levels by which a "
            "camera's frame may differ from its frame at least ten ticks earlier, in its worst "
            "80-pixel block that no rendered vehicle covers, and count as settled. "
            "Measured over 27 placements; recorded in the lock, so a run that loosens it says so."),
    _F("capture.road_layer_visible", _BOOLEAN, False, SESSION_FIXED,
       help="Draw the generated road surface. Hidden by default: it is a flat ribbon over the "
            "photogrammetry of the real road, so drawn it is an artifact in every frame."),
    _F("capture.signal_layer_visible", _BOOLEAN, False, SESSION_FIXED,
       help="Draw the generated traffic-light and sign meshes. Hidden by default; SUMO simulates "
            "the signals either way."),
    _F("capture.render_set", {"type": "string", "enum": ["all", "circle", "cameras"]}, "all",
       SESSION_FIXED,
       help="Which vehicles get a body. all (the default): every vehicle SUMO has. The others are "
            "optional performance controls -- circle: those inside capture.render_region; cameras: "
            "those inside, or about to enter, the ground footprint of any channel's camera, orbits "
            "included, with capture.render_region deciding until the cameras are placed where it is "
            "given, and every vehicle where it is not. A vehicle a limit leaves out is simulated by "
            "SUMO and has no body, no frame and no truth record; the run's record counts it."),
    _F("capture.render_region",
       _nullable({"type": "object", "additionalProperties": False,
                  "required": ["x_m", "y_m", "radius_m"],
                  "properties": {"x_m": _NUMBER, "y_m": _NUMBER, "radius_m": _POSITIVE}}),
       None, SESSION_FIXED,
       help="The circle vehicles get a body inside, in CARLA's frame (x east, y south, meters): for "
            "the whole run under render_set circle, which needs it, and under cameras only until the "
            "channels' cameras are placed. Read by no other render set (check 53)."),
    _F("capture.render_hysteresis_m", _POSITIVE, 60.0, SESSION_FIXED,
       help="Under render_set circle, how much further out than the region's radius a vehicle keeps "
            "its body; under cameras, the band beyond a footprint's widest admission threshold it is "
            "kept inside."),
    _F("capture.render_cap", _nullable(_POSITIVE_INTEGER), None, SESSION_FIXED,
       help="How many vehicles may hold a body at once, under any render set; null (the default) for "
            "no limit. Under all, a vehicle drawn keeps its body and a newcomer takes a free place in "
            "the scenario seed's order; under circle the nearest the center are drawn; under cameras a "
            "vehicle in view ranks ahead of one approaching, one drawn ahead of a newcomer, then the "
            "seed decides."),
    _F("capture.render_min_pixels", _POSITIVE, 2.0, SESSION_FIXED,
       help="Under render_set cameras: a camera's footprint is capped at the range beyond which the "
            "catalogue's longest body covers fewer than this many pixels along its length, anywhere "
            "in the picture."),
    _F("capture.render_admit_lead_s", _NON_NEGATIVE, 3.0, SESSION_FIXED,
       help="Under render_set cameras: simulated seconds of its own travel ahead of a camera's "
            "footprint a vehicle is admitted, beyond a margin of its body and one SUMO step, so it "
            "appears out of view."),
    _F("capture.render_release_lag_s", _NON_NEGATIVE, 5.0, SESSION_FIXED,
       help="Under render_set cameras: simulated seconds a vehicle drawn is held after it last was "
            "within reach of a camera's footprint, before it is released."),
    _F("capture.draw_distance_m", _nullable(_POSITIVE), None, SESSION_FIXED,
       help="An optional performance control, off when null (the default): how far from a camera, "
            "in meters, a vehicle's body is drawn. Rendering only: every vehicle keeps its body, its "
            "pose and its truth, and a body farther than this from a channel's camera is not in "
            "that channel's images, whose sidecars mark it beyond_draw_distance. It must reach the "
            "point every channel is aimed at (check 52)."),
    _F("capture.channels", {"type": "array", "minItems": 1, "items": {"type": "object"}},
       NO_DEFAULT, SESSION_FIXED,
       help="The cameras, one object each; see the channel fields below. Every channel is "
            "recorded."),
    # -- occlusion: measured on every channel, against a depth camera attached to its camera ---------
    _F("occlusion.margin_m", _NON_NEGATIVE, 1.0, SESSION_FIXED,
       help="How much nearer than a vehicle's own surface something must be to block it, meters."),
    _F("occlusion.samples", _POSITIVE_INTEGER, 24, SESSION_FIXED,
       help="How finely each vehicle's outline is sampled."),
    _F("occlusion.depth_max_range_m", _POSITIVE, 20000.0, SESSION_FIXED,
       help="The depth camera's range, meters. A surface beyond it reads as sky."),
    # -- the bridge: what the world did with the poses it was commanded ------------------------------
    _F("bridge.position_divergence_limit_m", _POSITIVE, 0.01, SESSION_FIXED,
       help="The closeout gate bridge.position_divergence: the largest distance, meters, between a "
            "pose the bridge commanded and the one the world applied to the body on the same tick "
            "that the gate accepts over the whole run. Measured over 39 drives on Arapahoe and "
            "Bahonar the worst was 0.0004 m, the single-precision wire's rounding; a read-back "
            "lagging the write by a frame is one tick's travel, 0.75 m at 15 m/s, and a wrong "
            "reference point half a body length."),
    _F("bridge.velocity_divergence_limit_m_per_s", _POSITIVE, 0.01, SESSION_FIXED,
       help="The closeout gate bridge.velocity_divergence: the largest difference, meters per "
            "second, between a velocity the bridge commanded and the one the world reported for the "
            "body that the gate accepts over the whole run. Measured over the same drives the worst "
            "was 0.000006 m/s; a body that reports no velocity is short by its whole speed, 21.8 m/s "
            "in the control run that sent none."),
    # -- pacing ------------------------------------------------------------------------------------
    _F("pacing.mode", {"type": "string", "enum": ["as_available", "wall_clock"]},
       "as_available", SESSION_FIXED,
       help="as_available: the world ticks as fast as the machine allows. wall_clock: ticks are "
            "held to the wall clock at pacing.real_time_factor."),
    _F("pacing.real_time_factor", _nullable(_POSITIVE), 1.0, SESSION_FIXED,
       help="Under wall_clock, simulated seconds per wall-clock second. Not a field under "
            "as_available."),
    _F("pacing.min_achieved_factor", _nullable(_POSITIVE), NO_DEFAULT, SESSION_FIXED,
       help="Under wall_clock, required: the achieved factor below which the prewarm refuses the "
            "window and the run is loud. Not a field under as_available."),
    _F("pacing.window_s", _POSITIVE, 5.0, SESSION_FIXED,
       help="Wall-clock seconds the achieved factor is measured over."),
    # -- the sun: the scenario's illumination object, overridable ---------------------------------
    _F("solar.policy", {"type": "string", "enum": list(ILLUMINATION_POLICIES)}, NO_DEFAULT,
       SESSION_FIXED, SUPPLIED_BY_SCENARIO, alias="--solar",
       help="What the sun does across the window: freeze_at_window_start, advance, freeze_at or "
            "ignore. The scenario's illumination default supplies it; an override is recorded "
            "against the value it replaced."),
    _F("solar.rate_sun_s_per_sim_s", _nullable(_POSITIVE), None, BOUND, SUPPLIED_BY_SCENARIO,
       help="Under advance, pinned to 1.0: one sun-second per simulated second is the only rate "
            "under which recorded solar time is the scenario's clock."),
    _F("solar.freeze_at_civil_time", _nullable(_CIVIL_TIME), None, SESSION_FIXED,
       SUPPLIED_BY_SCENARIO,
       help="Under freeze_at, the civil time of day the sun is held at, HH:MM:SS."),
    _F("solar.freeze_date_advances", _nullable(_BOOLEAN), None, SESSION_FIXED,
       SUPPLIED_BY_SCENARIO,
       help="Under a freeze, whether the sun's date follows the civil date when the epoch's "
            "calendar advances."),
    _F("solar.require_sun", _nullable(_BOOLEAN), None, SESSION_FIXED, SUPPLIED_BY_SCENARIO,
       help="Whether a world with no sun refuses the run. Unset means required."),
    _F("solar.note", _nullable(_TEXT), None, SESSION_FIXED, SUPPLIED_BY_SCENARIO,
       help="Why this policy, in one sentence."),
    # -- SUMO -------------------------------------------------------------------------------------
    _F("sumo.allow_version_mismatch", _BOOLEAN, False, SESSION_FIXED,
       help="Run with a SUMO release other than the one that converted the world, instead of "
            "refusing. The session records that it did."),
    _F("sumo.home", _nullable(_TEXT), None, SESSION_FIXED, SUPPLIED_BY_SITE,
       help="The SUMO installation to launch, the directory holding bin/sumo. Unset, the session "
            "searches SUMO_HOME and then PATH."),
    # -- the machine: layer 2 -----------------------------------------------------------------------
    _F("server.host", _TEXT, "127.0.0.1", SESSION_FIXED, SUPPLIED_BY_SITE,
       help="The CARLA server's address."),
    _F("server.port", _POSITIVE_INTEGER, 2000, SESSION_FIXED, SUPPLIED_BY_SITE,
       help="The CARLA server's RPC port."),
    _F("server.timeout_s", _POSITIVE, 30.0, SESSION_FIXED, SUPPLIED_BY_SITE,
       help="Seconds to wait for the server to answer a request."),
    _F("paths.scenario_root", _TEXT, NO_DEFAULT, SESSION_FIXED, SUPPLIED_BY_SITE,
       help="Where compiled scenario packages are found by id."),
    _F("paths.world_package_root", _TEXT, NO_DEFAULT, SESSION_FIXED, SUPPLIED_BY_SITE,
       help="Where world packages are found by the name a scenario lock records."),
    _F("paths.catalogue", _TEXT, NO_DEFAULT, SESSION_FIXED, SUPPLIED_BY_SITE,
       help="The measured vehicle catalogue. Its digest must be the scenario lock's."),
    _F("paths.capture_root", _TEXT, NO_DEFAULT, SESSION_FIXED, SUPPLIED_BY_SITE,
       help="Where captures are written, one directory per session and one per channel inside "
            "it."),
    _F("paths.runs_root", _TEXT, NO_DEFAULT, SESSION_FIXED, SUPPLIED_BY_SITE,
       help="Where a run's result, resolution report and lock are written by default."),
    # -- bindings: layer 3, the world package ---------------------------------------------------------
    _F("world.map_name", _TEXT, NO_DEFAULT, BOUND, SUPPLIED_BY_WORLD,
       help="The world's map, from world.json."),
    _F("world.network_fingerprint", _SHA256, NO_DEFAULT, BOUND, SUPPLIED_BY_WORLD,
       help="The canonical fingerprint of the SUMO network the world carries."),
    _F("world.opendrive_sha256", {"type": "string"}, NO_DEFAULT, BOUND, SUPPLIED_BY_WORLD,
       help="The digest of the world's OpenDRIVE."),
    _F("world.origin_latitude", _NUMBER, NO_DEFAULT, BOUND, SUPPLIED_BY_WORLD,
       help="The latitude pinned to the map's origin."),
    _F("world.origin_longitude", _NUMBER, NO_DEFAULT, BOUND, SUPPLIED_BY_WORLD,
       help="The longitude pinned to the map's origin."),
    _F("world.netconvert_version", {"type": "string"}, NO_DEFAULT, BOUND, SUPPLIED_BY_WORLD,
       help="The netconvert that converted the world."),
    # -- bindings: layer 4, the scenario package ---------------------------------------------------
    _F("scenario.scenario_id", _TEXT, NO_DEFAULT, BOUND, SUPPLIED_BY_SCENARIO,
       help="The scenario's id, from its lock."),
    _F("scenario.lock_sha256", _SHA256, NO_DEFAULT, BOUND, SUPPLIED_BY_SCENARIO,
       help="The digest of the scenario lock the run binds."),
    _F("scenario.epoch", {"type": "object"}, NO_DEFAULT, BOUND, SUPPLIED_BY_SCENARIO,
       help="What simulated second zero means in civil time. A binding: a run that rendered "
            "another time would contradict its own scenario."),
    _F("scenario.epoch_block_sha256", _SHA256, NO_DEFAULT, BOUND, SUPPLIED_BY_SCENARIO,
       help="The epoch's digest."),
    _F("scenario.sumo_step_s", _POSITIVE, NO_DEFAULT, BOUND, SUPPLIED_BY_SCENARIO,
       help="SUMO's step. Never changed to suit the renderer."),
    _F("scenario.sumo_seed", {"type": "integer", "minimum": 0}, NO_DEFAULT, BOUND,
       SUPPLIED_BY_SCENARIO,
       help="SUMO's seed, compiled into the scenario's configuration."),
    _F("scenario.end_s", _NON_NEGATIVE, NO_DEFAULT, BOUND, SUPPLIED_BY_SCENARIO,
       help="The simulated second the scenario ends at."),
    _F("scenario.catalogue_digest", {"type": "string"}, NO_DEFAULT, BOUND, SUPPLIED_BY_SCENARIO,
       help="The digest of the catalogue the scenario was compiled against."),
    _F("scenario.accept_skipped_dry_run", _BOOLEAN, False, SESSION_FIXED,
       help="Run a scenario whose compile skipped its SUMO-only run (compile_scenario.py "
            "--skip-dry-run), or whose lock records none, instead of refusing it (check 54). That "
            "run is what finds a vehicle the supervision plan names that never enters the "
            "simulation; without it the same fault stops the run only when SUMO drops the vehicle. "
            "The echo and the session's report record the acceptance."),
)

_CHANNEL_HELP = {
    "sensor_id": "The channel's camera's name, such as Overwatch_1 or Southeast_1700m_orbit: 1 to "
                 "63 characters, each an ASCII letter, digit, underscore or hyphen, and not a "
                 "Windows device name (CON, NUL, COM1, ...), a stock sensor role name (front, back, "
                 "left, right, ...), the server's own Camera_<number> or CARLA-SENSOR-<number>. "
                 "Required when there is more than one channel, and unique among them, case aside. "
                 "It names the channel's capture directory, begins every still's file name "
                 "(<sensor_id>_<local capture time>) and is the platform track's callsign; the "
                 "server refuses it at pre-roll where a live camera in the world already holds it. "
                 "A single channel without one takes the name the server gives its camera, "
                 "Camera_<n>.",
    "pattern": "stare holds one pose; orbit circles a center with the view held on it.",
    "fov": "Horizontal field of view, degrees.",
    "width": "Picture width, pixels.",
    "height": "Picture height, pixels.",
    "orbit_centre_x_m": "Orbit: the center it circles, x meters. Required for an orbit.",
    "orbit_centre_y_m": "Orbit: the center it circles, y meters (south). Required for an orbit.",
    "orbit_centre_z_m": "Orbit: the height the altitude is measured from, meters.",
    "orbit_radius_m": "Orbit: radius, meters.",
    "orbit_altitude_m": "Orbit: height above the center, meters.",
    "orbit_period_s": "Orbit: wall-clock seconds per revolution.",
    "stare_look_at_x_m": "Stare: the point looked at, x meters; with stare_look_at_y_m. Or name "
                         "stare_look_at_target, or give the five stare pose fields instead.",
    "stare_look_at_y_m": "Stare: the point looked at, y meters (south).",
    "stare_look_at_z_m": "Stare: the height of that point, meters. Not used with "
                         "stare_look_at_target, whose point carries the vehicles' own height.",
    "stare_look_at_target": "Stare: a point named instead of given. rendered_traffic is the center "
                            "of the vehicles the session rendered on the last frame before the "
                            "camera holds for the window; the camera follows it through the "
                            "prewarm until capture.tiles_hold_s before the window opens (10 s at "
                            "the default, in whole SUMO steps) -- or one SUMO step and the "
                            "picture's ceiling where capture.picture_settled_wait is true (32 s at "
                            "a 1 s step and the default ceiling: capture.picture_ceiling_frames at "
                            "the capture rate and the ten-tick span, in whole steps) -- then holds "
                            "the pose it resolves to while its view becomes ready and for the whole "
                            "window, and the run result records that point. Needs a prewarm of at "
                            "least one SUMO step to measure it on (check 47), and one SUMO step "
                            "more than check 51 asks of a camera at a fixed pose.",
    "stare_altitude_m": "Stare: height above the point, meters.",
    "stare_standoff_m": "Stare: horizontal distance back from the point, meters; 0 looks "
                        "straight down.",
    "stare_bearing_deg": "Stare: the compass direction the camera looks along, degrees clockwise "
                         "from north.",
    "stare_x_m": "Stare pose: x meters; all five pose fields or none.",
    "stare_y_m": "Stare pose: y meters (south).",
    "stare_z_m": "Stare pose: z meters.",
    "stare_pitch_deg": "Stare pose: pitch, degrees; negative looks down.",
    "stare_yaw_deg": "Stare pose: yaw, degrees; 0 faces east, -90 north.",
    "post_process_profile": "The post-process profile the camera spawns with: Default, GoPro, "
                            "Town10HD_Opt or Town_C, named with the file's own case so it resolves "
                            "on a case-sensitive file system. It sets the picture -- tone curve, "
                            "bloom, lens flare, vignette, motion blur -- and the exposure fields set "
                            "the exposure over it, whichever profile it is.",
    "exposure_method": "How the camera's exposure is set, over its profile: manual, fixed by "
                       "exposure_iso, exposure_shutter_s and exposure_fstop; or histogram, metered "
                       "by the engine from each frame, so the exposure follows what is in the "
                       "picture. histogram warns (check 16): it suits a live exercise's operator "
                       "picture, not captures meant to be compared.",
    "exposure_iso": "The camera's sensitivity, ISO, at least 1. Under manual, doubling it brightens "
                    "the picture by one stop.",
    "exposure_shutter_s": "The shutter, seconds, from 1/8000 s to 100 s: 0.003125 is 1/320 s. Sent "
                          "to the camera as its shutter_speed, which is per second. Under manual, "
                          "doubling it brightens the picture by one stop.",
    "exposure_fstop": "The aperture, as an f-number from 1 to 32: 4.0 is f/4. Under manual, each "
                      "doubling darkens the picture by two stops; it also sets the depth of field, "
                      "as on a lens.",
    "exposure_compensation_ev": "Exposure compensation, EV, from -15 to +15, added under either "
                                "method: +1 doubles the picture's brightness.",
}

# The characters a ChannelDescription text field may hold, where the schema can state them: a
# sensor_id is a camera name. What the characters allow and the rule still refuses -- a device name,
# a stock sensor role name, the server's own form, another camera's default -- is check 11's.
_CHANNEL_PATTERNS = {"sensor_id": CameraName.PATTERN}

# Channel fields this schema adds to ChannelDescription's, until the description carries them
# (12 §9.2). Each is defined here and nowhere else.
_CHANNEL_CAPTURE_FIELDS = (
    RunField("post_process_profile", {"type": "string", "enum": list(POST_PROCESS_PROFILES)},
             "Default", SESSION_FIXED, help=_CHANNEL_HELP["post_process_profile"]),
    # The exposure, over the profile; each default is the Default profile's (ChannelExposure).
    RunField("exposure_method", {"type": "string", "enum": list(EXPOSURE_METHODS)},
             ChannelExposure.default_of("exposure_method"), SESSION_FIXED,
             help=_CHANNEL_HELP["exposure_method"]),
    *(RunField(name, _NUMBER, ChannelExposure.default_of(name), SESSION_FIXED,
               help=_CHANNEL_HELP[name])
      for name in ("exposure_iso", "exposure_shutter_s", "exposure_fstop",
                   "exposure_compensation_ev")),
)


class RunConfiguration:
    """A run configuration document, read and checked against the field table.

    `values` maps a field's dotted path to the value the document gives it; a field the document
    does not mention is absent. `capture.channels` holds the channel objects as written.
    """

    VERSION: ClassVar[int] = RUN_CONFIGURATION_VERSION
    FIELDS: ClassVar[dict[str, RunField]] = {f.path: f for f in _FIELDS}

    def __init__(self, values: dict[str, Any], source: str) -> None:
        self.values = dict(values)
        self.source = source

    # -- reading ----------------------------------------------------------------------------------

    @classmethod
    def from_file(cls, path: str | Path) -> RunConfiguration:
        """Read a document, refusing it whole (check 1) if it is not one.

        Raises:
            RunConfigurationRefusedError: with outcome `usage_error`, naming every problem found.
        """
        path = Path(path)
        findings = RunConfigurationFindings()
        try:
            document = json.loads(path.read_text(encoding="utf-8"))
        except OSError as failure:
            findings.refuse(1, str(path), f"cannot read the run configuration: {failure}")
            raise RunConfigurationRefusedError(findings, "usage_error") from None
        except json.JSONDecodeError as failure:
            findings.refuse(1, str(path), f"not JSON: {failure}")
            raise RunConfigurationRefusedError(findings, "usage_error") from None
        return cls.from_document(document, str(path))

    @classmethod
    def from_document(cls, document: object, source: str) -> RunConfiguration:
        """Flatten a parsed document into field paths, refusing every key and value that is wrong.

        Raises:
            RunConfigurationRefusedError: with outcome `usage_error`.
        """
        findings = RunConfigurationFindings()
        values: dict[str, Any] = {}
        if not isinstance(document, dict):
            findings.refuse(1, source, "a run configuration is a JSON object")
            raise RunConfigurationRefusedError(findings, "usage_error")
        # A version newer than this tool's is refused for what it is, before any key it may have
        # renamed is refused as unknown.
        declared = document.get("run_configuration_version")
        if isinstance(declared, int) and not isinstance(declared, bool) \
                and declared > RUN_CONFIGURATION_VERSION:
            try:
                FormatVersion.check(source, "run_configuration_version", declared,
                                    RUN_CONFIGURATION_VERSION)
            except FormatVersionError as newer:
                findings.refuse(1, "run_configuration_version", str(newer))
                raise RunConfigurationRefusedError(findings, "usage_error") from None
        document = dict(document)
        producer = document.pop(PRODUCER_KEY, None)
        if producer is not None and not isinstance(producer, dict):
            findings.refuse(1, PRODUCER_KEY, f"{source}: '{PRODUCER_KEY}' records what wrote the "
                            "document, and is an object")
        cls._flatten(document, "", values, findings, source)
        for path, value in values.items():
            cls._check_value(path, value, findings, source)
        if findings.refused:
            raise RunConfigurationRefusedError(findings, "usage_error")
        return cls(values, source)

    @classmethod
    def _flatten(cls, node: dict, prefix: str, values: dict[str, Any],
                 findings: RunConfigurationFindings, source: str) -> None:
        groups = cls.groups()
        for key, value in node.items():
            path = f"{prefix}{key}"
            if path in cls.FIELDS:
                values[path] = value
            elif path in groups:
                if isinstance(value, dict):
                    cls._flatten(value, f"{path}.", values, findings, source)
                else:
                    findings.refuse(1, path, f"{source}: '{path}' is a group of fields and takes an "
                                    "object")
            else:
                cls._refuse_unknown(path, findings, source)

    @classmethod
    def _refuse_unknown(cls, path: str, findings: RunConfigurationFindings, source: str) -> None:
        leaf = path.rsplit(".", 1)[-1].split("]")[-1].lstrip(".")
        if path == _WORLD_BUILD_KEY or path.startswith(_WORLD_BUILD_KEY + "."):
            findings.refuse(38, path, "a capture run binds a world package; it does not build one. "
                            "Build the world with run_SCTMV.py --build and name the package it "
                            "writes")
            return
        if leaf in _BLUEPRINT_EXPOSURE_NAMES:
            field = _BLUEPRINT_EXPOSURE_NAMES[leaf]
            findings.refuse(1, path, f"{source}: '{leaf}' is the camera blueprint's name for part of "
                            f"the exposure; a channel sets it with '{field}'"
                            + (", in seconds, where the camera's shutter_speed is per second"
                               if field == "exposure_shutter_s" else ""))
            return
        near = difflib.get_close_matches(path, cls.known_paths(), n=3, cutoff=0.6)
        hint = f"; did you mean {' or '.join(repr(n) for n in near)}?" if near else ""
        findings.refuse(1, path, f"{source}: unknown key '{path}'{hint}")

    @classmethod
    def _check_value(cls, path: str, value: Any, findings: RunConfigurationFindings,
                     source: str) -> None:
        """Refuse a value of the wrong shape for its field."""
        if path == "capture.channels":
            cls._check_channels(value, findings, source)
            return
        channel = _CHANNEL_INDEX.match(path)
        if channel is not None:
            cls._check_channel_field(path, channel.group(2), value, findings, source)
            return
        spec = cls.FIELDS[path]
        for problem in ScenarioSchema.validate_against(value, spec.schema):
            findings.refuse(1, path, f"{source}: {problem.replace('$', path, 1)}")

    @classmethod
    def _check_channels(cls, channels: Any, findings: RunConfigurationFindings,
                        source: str) -> None:
        spec = cls.FIELDS["capture.channels"]
        problems = ScenarioSchema.validate_against(channels, spec.schema)
        for problem in problems:
            findings.refuse(1, "capture.channels",
                            f"{source}: {problem.replace('$', 'capture.channels', 1)}")
        if problems:
            return
        channel_fields = cls.channel_fields()
        for index, item in enumerate(channels):
            for key, value in item.items():
                path = f"capture.channels[{index}].{key}"
                if key in channel_fields:
                    cls._check_channel_field(path, key, value, findings, source)
                elif key in _BLUEPRINT_EXPOSURE_NAMES:
                    cls._refuse_unknown(path, findings, source)
                else:
                    near = difflib.get_close_matches(key, list(channel_fields), n=3, cutoff=0.6)
                    hint = f"; did you mean {' or '.join(repr(n) for n in near)}?" if near else ""
                    findings.refuse(1, path, f"{source}: '{key}' is not a channel field{hint}")

    @classmethod
    def _check_channel_field(cls, path: str, key: str, value: Any,
                             findings: RunConfigurationFindings, source: str) -> None:
        spec = cls.channel_fields().get(key)
        if spec is None:
            cls._refuse_unknown(path, findings, source)
            return
        problems = ScenarioSchema.validate_against(value, spec.schema)
        if problems and key == "sensor_id" and isinstance(value, str):
            # The pattern is a camera name's characters; the refusal says what they are, with an
            # example, rather than quoting the pattern.
            problems = [f"$: {CameraName.problem(value)}"]
        for problem in problems:
            findings.refuse(1, path, f"{source}: {problem.replace('$', path, 1)}")

    # -- overrides ----------------------------------------------------------------------------------

    @classmethod
    def parse_override(cls, text: str, source: str | None = None) -> tuple[str, Any]:
        """`path=value` as `--set` gives it: the path checked, the value parsed for its field.

        A value is JSON (`192`, `true`, `{"x_m": 0, ...}`), except for a field that takes text,
        whose value is the text as written unless it is a quoted JSON string.

        Raises:
            RunConfigurationRefusedError: with outcome `usage_error`.
        """
        source = source or f"--set {text}"
        findings = RunConfigurationFindings()
        if "=" not in text:
            findings.refuse(1, text, f"{source}: an override is <path>=<value>")
            raise RunConfigurationRefusedError(findings, "usage_error")
        path, raw = text.split("=", 1)
        path = path.strip()
        value = cls.parse_value(path, raw, findings, source)
        if not findings.refused:
            cls._check_value(path, value, findings, source)
        if findings.refused:
            raise RunConfigurationRefusedError(findings, "usage_error")
        return path, value

    @classmethod
    def parse_value(cls, path: str, raw: str, findings: RunConfigurationFindings,
                    source: str) -> Any:
        """An override's text as the value of the field at `path`."""
        spec = cls.field_or_channel_field(path)
        if spec is None:
            cls._refuse_unknown(path, findings, source)
            return None
        if spec.accepts_text() and not raw.startswith('"'):
            return raw
        try:
            return json.loads(raw)
        except json.JSONDecodeError:
            findings.refuse(1, path, f"{source}: {raw!r} is not a value for '{path}'; it takes "
                            f"{json.dumps(spec.schema)}")
            return None

    # -- the field table --------------------------------------------------------------------------

    @classmethod
    def field(cls, path: str) -> RunField:
        return cls.FIELDS[path]

    @classmethod
    def field_or_channel_field(cls, path: str) -> RunField | None:
        if path in cls.FIELDS:
            return cls.FIELDS[path]
        channel = _CHANNEL_INDEX.match(path)
        if channel is not None:
            return cls.channel_fields().get(channel.group(2))
        return None

    @classmethod
    def groups(cls) -> set[str]:
        return {path.split(".", 1)[0] for path in cls.FIELDS if "." in path}

    @classmethod
    def known_paths(cls) -> list[str]:
        return [*cls.FIELDS, *(f"capture.channels[0].{name}" for name in cls.channel_fields())]

    @classmethod
    def channel_fields(cls) -> dict[str, RunField]:
        """A channel object's fields: `ChannelDescription`'s, with its defaults, then the capture's."""
        table: dict[str, RunField] = {}
        hints = typing.get_type_hints(ChannelDescription)
        for item in fields(ChannelDescription):
            default = ChannelDescription.default_of(item.name)
            table[item.name] = RunField(item.name,
                                        cls._schema_for(hints[item.name],
                                                        _CHANNEL_PATTERNS.get(item.name)),
                                        default, SESSION_FIXED,
                                        help=_CHANNEL_HELP.get(item.name, ""))
        for extra in _CHANNEL_CAPTURE_FIELDS:
            table[extra.path] = extra
        return table

    @staticmethod
    def _schema_for(hint: Any, pattern: str | None = None) -> dict:
        """The schema fragment for one of ChannelDescription's annotated types, with the pattern
        a text field's value must match, where it has one."""
        arguments = typing.get_args(hint)
        nullable = type(None) in arguments
        base = next((a for a in arguments if a is not type(None)), hint) if arguments else hint
        if base is str:
            fragment = {"type": "string"} if pattern is None else {"type": "string",
                                                                   "pattern": pattern}
        elif base is int:
            fragment = {"type": "integer"}
        else:
            fragment = {"type": "number"}
        return _nullable(fragment) if nullable else fragment

    # -- publication -----------------------------------------------------------------------------

    @classmethod
    def schema(cls) -> dict:
        """The run configuration's schema, as published: every field's shape, default and class."""
        root: dict = {"$schema": "https://json-schema.org/draft/2020-12/schema",
                      "$id": "https://carla.local/schemas/run_configuration.schema.json",
                      "title": "SUMO-driven capture run configuration",
                      "description": "One capture run's configuration: every field, its default, "
                                     "its mutability class and the layer that may supply it. "
                                     "Positions are in CARLA's frame: meters, x east, y south.",
                      "type": "object", "additionalProperties": False, "properties": {}}
        for spec in cls.FIELDS.values():
            node = root
            parts = spec.path.split(".")
            for part in parts[:-1]:
                node = node["properties"].setdefault(
                    part, {"type": "object", "additionalProperties": False, "properties": {}})
            node["properties"][parts[-1]] = spec.published()
        channel = {"type": "object", "additionalProperties": False,
                   "properties": {name: spec.published()
                                  for name, spec in cls.channel_fields().items()}}
        root["properties"]["capture"]["properties"]["channels"]["items"] = channel
        root["properties"][PRODUCER_KEY] = {
            "type": ["object", "null"],
            "description": "What wrote the document, where a tool did: the tool and its release, the "
                           "carlanet release, the server and the SUMO release where used, and when. "
                           "Provenance, never read as configuration and outside its digest; a run's "
                           "run.effective.json carries it."}
        return root

    @classmethod
    def write_schema(cls, path: str | Path) -> Path:
        """Publish the schema, byte-stable."""
        path = Path(path)
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(cls.schema(), indent=2, ensure_ascii=False) + "\n",
                        encoding="utf-8", newline="\n")
        return path

    @classmethod
    def help_text(cls) -> str:
        """Every field, grouped, with its default and class: what `run_capture --help` prints."""
        lines = ["Run configuration fields (set in a --run document, or with --set <path>=<value>):",
                 ""]
        current = None
        for spec in cls.FIELDS.values():
            if spec.group != current:
                current = spec.group
                lines.append(f"  [{current}]")
            lines.extend(cls._help_entry(spec.path, spec))
        lines += ["", "  [capture.channels[N]] -- each channel object:"]
        for name, spec in cls.channel_fields().items():
            lines.extend(cls._help_entry(name, spec))
        return "\n".join(lines)

    @staticmethod
    def _help_entry(name: str, spec: RunField) -> list[str]:
        default = "no default" if not spec.has_default else f"default {json.dumps(spec.default)}"
        alias = f", {spec.alias}" if spec.alias else ""
        head = f"    {name}{alias}  ({default}; {spec.mutability}"
        head += "" if spec.supplied_by == SUPPLIED_BY_TOOL else f"; from the {spec.supplied_by}"
        return [head + ")", f"        {spec.help}"]

