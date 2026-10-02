"""Every check a capture run's configuration is subject to, by the number the plan gives it.

`12_Operator_Control_Surface.md` §6.2 numbers the checks, and the numbers are stable and never
reused because other sections cite them (`08_Collection_And_EPoL.md` §15 cites check 17). A check
withdrawn is retired with its number (`RETIRED`), which no other check is ever given. They are a
different numbering from the scenario compiler's (`ScenarioCheckCatalogue`): a run's findings are
reported under this catalogue, and a resolution report says which catalogue it cites.

A check is carried out in one of four places, and the catalogue says which, so a check's presence in
the list is never read as a guarantee:

* `run_capture` -- the resolver or the validator in this package runs it;
* `session` -- the co-simulation session (`CarlaNet.CoSim.SumoDriveSession`) runs it as part of
  starting, and is the one validator of it. `run_capture` does not check it a second time; it maps
  the session's refusal onto the run's outcome;
* `by_construction` -- what it guards against cannot be configured, so there is nothing to compare;
* `not_built` -- the thing it would compare is not published by anything in the tree. `where` says
  what is missing.

The refuse/warn vocabulary is the scenario compiler's (`CompileFinding`), not a second one.
"""
from __future__ import annotations

from dataclasses import dataclass

from carlacontrol.CompileFinding import REFUSE, WARN

RUN_CAPTURE = "run_capture"
SESSION = "session"
BY_CONSTRUCTION = "by_construction"
NOT_BUILT = "not_built"
STATUSES = (RUN_CAPTURE, SESSION, BY_CONSTRUCTION, NOT_BUILT)

# Where in the launch a check runs. `resolution` refusals end the launch before anything is
# resolved (outcome `usage_error`); an offline refusal is `refused_offline`, and so on.
RESOLUTION = "resolution"
OFFLINE = "offline"
SERVER = "server"
AUTHORITY = "authority"
PRE_ROLL = "pre_roll"
CONTINUOUS = "continuous"
PHASES = (RESOLUTION, OFFLINE, SERVER, AUTHORITY, PRE_ROLL, CONTINUOUS)

CATALOGUE_NAME = "12_Operator_Control_Surface.md §6.2"

_R = (REFUSE,)
_W = (WARN,)
_RW = (REFUSE, WARN)


@dataclass(frozen=True)
class RunCheck:
    """One check: its stable number, where it runs, what it guards, and what it can conclude.

    `warning_code` is the name an `on_warning.<code>` adjudication uses for a check that can warn;
    a check that cannot warn has none.
    """

    check_id: int
    phase: str
    title: str
    outcomes: tuple[str, ...]
    status: str
    where: str
    warning_code: str | None = None

    def __post_init__(self) -> None:
        if self.status not in STATUSES:
            raise ValueError(f"check {self.check_id}: status {self.status!r} is not one of {STATUSES}")
        if self.phase not in PHASES:
            raise ValueError(f"check {self.check_id}: phase {self.phase!r} is not one of {PHASES}")
        if (WARN in self.outcomes) != (self.warning_code is not None):
            raise ValueError(f"check {self.check_id}: a check that can warn has a warning code, "
                             "and only such a check")

    def to_dict(self) -> dict:
        return {"id": self.check_id, "phase": self.phase, "title": self.title,
                "outcomes": list(self.outcomes), "status": self.status, "where": self.where,
                "warning_code": self.warning_code}


_CHECKS: tuple[RunCheck, ...] = (
    # -- resolution: the invocation itself ------------------------------------------------------
    RunCheck(1, RESOLUTION, "The document and every override name fields of the schema, with "
             "values of each field's type", _R, RUN_CAPTURE,
             "RunConfiguration, when a document or an override is read"),
    RunCheck(3, RESOLUTION, "No override targets a value bound by the world package or the scenario "
             "package", _R, RUN_CAPTURE, "RunConfigurationResolver"),
    RunCheck(16, RESOLUTION, "A numeric camera exposure is not requested", _R, RUN_CAPTURE,
             "RunConfiguration; the refusal names post_process_profile, the field that exists"),
    RunCheck(38, RESOLUTION, "No world_build block is present: a capture run binds a world "
             "package and never builds one", _R, RUN_CAPTURE, "RunConfiguration"),
    RunCheck(49, RESOLUTION, "The scenario package and the world package resolve, and the "
             "scenario's files are the ones its lock digests", _R, RUN_CAPTURE,
             "ScenarioPackage, RunConfigurationResolver"),
    # -- offline ---------------------------------------------------------------------------------
    RunCheck(2, OFFLINE, "Every required field is supplied by some layer", _R, RUN_CAPTURE,
             "RunConfigurationValidator"),
    RunCheck(4, OFFLINE, "mode is one this front end runs", _R, RUN_CAPTURE,
             "RunConfigurationValidator; only sumo_driven_playback is built here, and the "
             "traffic-manager path is run_SCTMV.py"),
    RunCheck(5, OFFLINE, "The scenario lock's world binding equals the world package's: network "
             "fingerprint, OpenDRIVE digest, origin", _R, RUN_CAPTURE, "RunConfigurationValidator"),
    RunCheck(6, OFFLINE, "The scenario lock is at a version this tool reads", _R, RUN_CAPTURE,
             "RunConfigurationValidator"),
    RunCheck(7, OFFLINE, "capture.window resolves to a declared window, a begin:end pair inside "
             "the scenario's span, or a begin with no end", _R, RUN_CAPTURE,
             "EffectiveRunConfiguration.window"),
    RunCheck(8, OFFLINE, "The render prewarm fits before the window", _W, RUN_CAPTURE,
             "RunConfigurationValidator", warning_code="prewarm_clipped"),
    RunCheck(9, OFFLINE, "The SUMO step, the world delta and the capture rate are in whole-number "
             "ratio", _R, RUN_CAPTURE, "CarlaNet.CoSim.CoSimClock.ForSession, the session's own "
             "validator, called offline"),
    RunCheck(10, OFFLINE, "sensor_tick is consistent with capture_hz", (), BY_CONSTRUCTION,
             "sensor_tick is not a field; every camera's is set to 1 / capture_hz"),
    RunCheck(11, OFFLINE, "Every channel's sensor_id is present and unique when there is more than "
             "one channel", _R, RUN_CAPTURE, "RunConfigurationValidator"),
    RunCheck(12, OFFLINE, "The scenario declares a solar epoch", _R, RUN_CAPTURE,
             "RunConfigurationValidator"),
    RunCheck(13, OFFLINE, "The window's civil span is computable from the epoch", _R, RUN_CAPTURE,
             "ScenarioEpoch, which reads the epoch with the session's SolarEpoch"),
    RunCheck(14, OFFLINE, "A window darker than -6 degrees states solar.vehicle_lights", (),
             NOT_BUILT, "the session writes no vehicle lamps, so there is no field to state"),
    RunCheck(15, OFFLINE, "The illumination object is one a capture run permits: accepted by "
             "IlluminationPolicy, and advancing only at one sun-second per simulated second; "
             "'ignore' warns", _RW, RUN_CAPTURE, "RunConfigurationValidator",
             warning_code="lighting_honours_no_epoch"),
    RunCheck(17, OFFLINE, "The observation and truth roots are distinct and disjoint", (),
             NOT_BUILT, "the recorder writes each capture's image and sidecar into one directory; "
             "the two-root split is stage K's and no writer makes it"),
    RunCheck(18, OFFLINE, "Every seed has an explicit value", (), BY_CONSTRUCTION,
             "the only seed the run consumes is SUMO's, bound by the scenario package"),
    RunCheck(19, OFFLINE, "Free space under the capture root holds the window, in captured seconds",
             _RW, RUN_CAPTURE, "RunConfigurationValidator, from doc 10's measured capture sizes",
             warning_code="capture_may_outrun_disk"),
    RunCheck(34, OFFLINE, "Under caller unattended, every warning raised has an on_warning "
             "adjudication", _R, RUN_CAPTURE, "RunConfigurationValidator"),
    RunCheck(35, OFFLINE, "Every declared expectation holds against the resolved value", _R,
             RUN_CAPTURE, "RunConfigurationValidator, against the configuration and the launch "
             "echo"),
    RunCheck(36, OFFLINE, "Under caller unattended, no field resolved from an environment variable "
             "the site profile does not declare", _R, RUN_CAPTURE, "RunConfigurationValidator"),
    RunCheck(37, OFFLINE, "No site-profile path resolves empty, and the catalogue it names exists",
             _R, RUN_CAPTURE, "RunConfigurationValidator"),
    RunCheck(39, OFFLINE, "A drawn seed is written down before the first capture", (),
             BY_CONSTRUCTION, "no seed is drawn; the SUMO seed is the scenario package's"),
    RunCheck(40, OFFLINE, "result_path is writable and lies outside the capture root", _R,
             RUN_CAPTURE, "RunConfigurationValidator"),
    RunCheck(41, OFFLINE, "The pacing fields are consistent with pacing.mode", _R, RUN_CAPTURE,
             "RunConfigurationValidator"),
    RunCheck(42, OFFLINE, "Handover channels are declared channels; the transcript root lies "
             "outside the corpus", (), NOT_BUILT, "no handover or transcript writer exists"),
    RunCheck(47, OFFLINE, "Every channel is a valid ChannelDescription, occlusion is measured "
             "only on a stare, and a stare aimed at the rendered traffic has a prewarm of at least "
             "one SUMO step to measure it over", _R, RUN_CAPTURE,
             "ChannelDescription, RunConfigurationValidator"),
    RunCheck(52, OFFLINE, "capture.draw_distance_m, where set, reaches the point every channel's "
             "camera is aimed at", _R, RUN_CAPTURE, "RunConfigurationValidator"),
    RunCheck(53, OFFLINE, "An optional render-set limit is one the session can draw: render_set circle "
             "has a render_region, and a render_region is given only to a render set that reads it",
             _R, RUN_CAPTURE, "RunConfigurationValidator"),
    RunCheck(48, OFFLINE, "The catalogue at paths.catalogue is the one the scenario was compiled "
             "against", _R, RUN_CAPTURE, "RunConfigurationValidator"),
    RunCheck(51, OFFLINE, "The prewarm leaves every camera, at the pose it holds as the window "
             "opens, enough ticks after its tiles are first asked about to render two frames at "
             "least ten ticks apart: the fewest its picture can be witnessed settled on", _R,
             RUN_CAPTURE, "RunConfigurationValidator"),
    # -- the server --------------------------------------------------------------------------
    RunCheck(22, SERVER, "The loaded world is the world package's", (), SESSION,
             "SumoDriveSession.Start, LoadedWorldCheck, before SUMO is started"),
    RunCheck(23, SERVER, "The world has a sun, where the policy binds one or requires one", _R,
             RUN_CAPTURE, "RunConfigurationValidator.validate_against_server"),
    RunCheck(24, SERVER, "Every attribute the rig sets exists on the blueprint it targets", _R,
             RUN_CAPTURE, "RunConfigurationValidator.validate_against_server"),
    RunCheck(25, SERVER, "Every blueprint the scenario's vehicle types name is on this server", _R,
             RUN_CAPTURE, "RunConfigurationValidator.validate_against_server"),
    RunCheck(26, SERVER, "The SUMO launched is the release that converted the world", (), SESSION,
             "SumoDriveSession.Start; sumo.allow_version_mismatch runs anyway and is reported"),
    RunCheck(27, SERVER, "No other client is attached", (), NOT_BUILT,
             "the server publishes no count of attached clients"),
    RunCheck(43, SERVER, "Each channel's post_process_profile loaded, confirmed by digest", (),
             NOT_BUILT, "nothing reads back which profile a camera loaded"),
    # -- authority ---------------------------------------------------------------------------
    RunCheck(28, AUTHORITY, "Population authority is acquired", (), SESSION,
             "SumoDriveSession.Start; PopulationAuthorityHeldException names the holder"),
    RunCheck(29, AUTHORITY, "Motion authority is free for every vehicle the run drives", (),
             NOT_BUILT, "the session holds a population lease and no per-actor motion lease"),
    # -- pre-roll -----------------------------------------------------------------------------
    RunCheck(30, PRE_ROLL, "SUMO reaches the prewarm's first instant", (), SESSION,
             "SumoDriveSession.Start, its fast-forward"),
    RunCheck(31, PRE_ROLL, "The applied sun matches the declared one, read back", (), SESSION,
             "SumoDriveSession.Start, SolarLease and the window-open audit; "
             "SolarAuditFailedException"),
    RunCheck(32, PRE_ROLL, "The first cued tick delivers a frame on every channel", (), NOT_BUILT,
             "the recorder publishes no count of frames received"),
    RunCheck(44, PRE_ROLL, "Under pacing.mode wall_clock, the prewarm held min_achieved_factor", _R,
             RUN_CAPTURE, "CaptureSession, from the session's RealTimePacer"),
    RunCheck(45, PRE_ROLL, "The handover transport opens", (), NOT_BUILT,
             "no handover transport exists"),
    RunCheck(50, PRE_ROLL, "Every channel's view is ready as the window opens: its photoreal tiles "
             "in and its picture settled with the blocks rendered vehicles cover left out, each "
             "within its ceiling", _R, RUN_CAPTURE,
             "CaptureSession, ViewReadinessGate: world.get_view_readiness after every prewarm "
             "step, and the camera's own frames"),
    # -- while the run proceeds -------------------------------------------------------------------
    RunCheck(46, CONTINUOUS, "Write headroom under the capture root stays above "
             "write_headroom_floor_s of capture", _R, RUN_CAPTURE,
             "RunConfigurationValidator at launch; CaptureSession while the run proceeds"),
)

# Numbers whose checks were withdrawn, with why. Each stays retired: a finding never cites one, and no
# other check is given it.
RETIRED: dict[int, str] = {
    20: "compared a render region and cap that no longer exist: the session renders every vehicle "
        "SUMO has",
    21: "predicted the in-region population against a render cap that no longer exists",
    33: "compared the population at the window's begin with a render cap that no longer exists",
}


class RunConfigurationCheckCatalogue:
    """The run checks, looked up by number."""

    _BY_ID = {check.check_id: check for check in _CHECKS}
    _BY_CODE = {check.warning_code: check for check in _CHECKS if check.warning_code}

    @classmethod
    def all(cls) -> tuple[RunCheck, ...]:
        """Every check, in launch order."""
        return _CHECKS

    @classmethod
    def get(cls, check_id: int) -> RunCheck:
        """The check with this number. A number nobody assigned, or one retired, is a bug, and says
        so."""
        try:
            return cls._BY_ID[check_id]
        except KeyError:
            if check_id in RETIRED:
                raise KeyError(f"run check {check_id} is retired ({RETIRED[check_id]}); a finding "
                               "may only cite a check that exists") from None
            raise KeyError(f"run check {check_id} is not in the catalogue; a finding may only cite "
                           "a check that exists") from None

    @classmethod
    def warning_codes(cls) -> tuple[str, ...]:
        """Every code an `on_warning` adjudication can name."""
        return tuple(sorted(cls._BY_CODE))

    @classmethod
    def by_warning_code(cls, code: str) -> RunCheck:
        return cls._BY_CODE[code]

    @classmethod
    def to_document(cls) -> dict:
        return {"catalogue": CATALOGUE_NAME,
                "outcomes": {REFUSE: "nothing is emitted and no session starts",
                             WARN: "the launch goes on, and the warning is carried in full into the "
                                   "resolution report and the run result"},
                "checks": [check.to_dict() for check in _CHECKS],
                "retired": {str(check_id): reason for check_id, reason in sorted(RETIRED.items())}}
