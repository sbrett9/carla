"""One capture run, from an invocation to its result: resolve, validate, launch, record, close.

`12_Operator_Control_Surface.md` §6.3's sequence, with the co-simulation session doing what only it can
do. `01_Architecture.md` §2.3 names the component that assigns one session identity to every recorder
and a stable `sensor_id` to every camera `CaptureSession`; this is it, in the process that drives the
world (D1.12):

* **Resolve** the six layers (`RunConfigurationResolver`) and **validate offline**
  (`RunConfigurationValidator`), then compute the **launch echo** (`LaunchEcho`) and check the
  caller's expectations and adjudications against it. The resolution report is written on every
  path; the lock and the replayable effective configuration only when the offline checks accept.
* **Echo.** An attended launch prints the echo, and blocks only when a warning was raised that no
  `on_warning` adjudicates (§6.4.2); with no terminal to ask, it refuses rather than guessing.
* **Connect and check the server.**
* **Start the session** (`world.start_sumo_drive`), which checks the loaded world is the package's
  and the scenario's files are the ones its compile lock digests, refuses a scenario that lets SUMO
  teleport, takes the population lease and the world's drive lease on the server, starts the SUMO
  release that converted the world, takes the world's clock, hides the layers, fast-forwards SUMO
  to the prewarm's first instant, and binds
  the sun for the window's opening -- `window_opens_at`, the window's begin, so a frozen sun is
  pinned there and the prewarm is lit by it. It is handed `on_admission_pass`, so every admission
  pass reaches `WindowAdmissions`, and -- only where a channel aims at the rendered traffic --
  `on_pose`, feeding `RenderedTrafficCentre`. Two optional performance controls, off by default,
  are handed over where set: `capture.draw_distance_m`, which the session gives every body it spawns,
  so no camera draws a body farther away while every vehicle keeps its body, its pose and its truth;
  and `capture.render_set` with its `capture.render_*` fields, which limits which vehicles get a body
  at all -- the region given in CARLA's frame and negated into SUMO's -- leaving every vehicle
  outside the limit simulated by SUMO and out of CARLA and the truth. And it is always handed the
  world truth track's path, `truth/world_truth_track.csv` under the capture directory, beside the
  channels' directories: every vehicle SUMO has, drawn or not, at every SUMO frame inside the window,
  which a base rate is taken over where the sidecars list only what was drawn (06 §8.3, D6.17). The
  session writes it a row at a time with its summary beside it; the run result names it. And the run
  manifest's, `truth/manifest.jsonl` beside it, with a header of what the run is -- its identity, its
  window, its channels -- which the session writes verbatim on the manifest's opening row beside what it
  established itself (04 §12.7, 06 §8.4); each channel's camera is named on it as it is placed.
* **Map every refusal by its stage.** A refusal from the session's start or from `Advance` is a
  `CoSimSessionRefusedException` whose `StageName` says how far the session had got: `Validation`
  and `Launch` are `refused_server`; `Authority` is `refused_authority`, with the holder a held
  lease names (`HeldBy`) -- the process-local population lease, or the world's drive lease another
  client holds on the server; `PreRoll` -- from the start, or from an `Advance` of the
  prewarm -- is `refused_preroll`, closed `aborted_at_preroll`; `Window` is `run_stopped`, closed
  `fault:<type>`. A SUMO failure is such a refusal. Anything else raised is `internal_error`.
* **Place the cameras**: each channel's RGB camera at its stare pose -- or at the pose its orbit
  opens on, the circle given to the server's orbit mover at once and held there, flown by the server
  on the simulation clock from the window's opening (`OrbitSensorController`; nothing in this
  process sends a pose per frame) -- with `sensor_tick` at the
  capture interval and the post-process profile set by name, spawned under the channel's
  `sensor_id` as its `role_name`, which the server refuses where a live camera in the world already
  holds it, so the run refuses at pre-roll; a single channel with no `sensor_id` takes the name the
  server gives its camera, `Camera_<n>`, read back from the spawned camera. That name is the
  channel's directory, the first part of every still's file name in it and the callsign of its
  platform track. And a depth camera attached to the channel's camera, rigidly and at its pose, for
  a stare and an orbit alike -- occlusion is measured on every channel, and nothing turns it off
  (the owner's ruling of 2026-10-05): one move of the camera carries both, so
  the two are never captured a frame apart -- not a stare's through the prewarm, not an orbit's
  through the window -- and the server reports the depth camera's world pose on its snapshot and in
  its image header, which is what the recorder's depth pose check reads. The cameras
  exist through the prewarm, so the tiles their views select are streamed before the first capture.
  A stare aimed at the rendered traffic starts over the centre of the world's staging bounds. Where
  `capture.picture_settled_wait` is true, every camera's frames are listened to from here until the
  recorders start (`ViewReadinessGate`). Under the optional `capture.render_set` `cameras`, each
  RGB camera is registered with the session (`AddCamera`), so the render set follows every channel's
  view, an orbit's as it flies, and is let go (`RemoveCamera`) before the camera is destroyed; its
  depth camera shares its view and is not registered. Under any other render set where the cameras
  look decides nothing about which vehicles are rendered.
* **Prewarm**: advance until the window's begin without recording, and wait there for every
  channel's view to be ready (03 §9.5.1, check 50): after each step the server is asked whether the
  camera's photoreal tiles are in. By default that is the whole wait: the picture's wait,
  `capture.picture_settled_wait`, is off by the owner's ruling of 2026-10-06, so a view is ready on
  the step its tiles are in and no frame is compared. Where the field is true, once the tiles are in
  the camera's own frames are compared until its picture has settled, leaving out every block a
  rendered vehicle covers -- placed from the session's render set of the frame and the client's
  snapshot of it (`SessionFrameVehicles`). The wait lives inside the prewarm and ticks with it, and
  it begins once every camera holds the pose the window opens on, because the tiles' figures cover
  every registered view and a moving camera's picture never reads settled. A ceiling reached -- 90 s
  of wall clock for the tiles; for the picture, where it is waited on,
  `capture.picture_ceiling_frames` of the camera's own frames from its tiles being in, 60 by
  default -- or a view not ready as the window opens refuses at pre-roll, naming the channel, the
  witness and its state; the window's first frame is never moved. A stare aimed at the rendered
  traffic follows it: after each prewarm step its cameras are moved to the pose around the centre of
  the vehicles that step's last frame rendered, until the tiles' own lead, `capture.tiles_hold_s`
  (10 s by default), before the window opens -- or one SUMO step and the picture's ceiling, where
  the picture is waited on (`ViewReadiness.hold_lead_s`) -- when that step's centre is the point it
  resolves to -- recorded, and held through the rest of the prewarm and the whole window; a frame
  there that rendered nothing refuses at pre-roll. Under `wall_clock`
  the pace the prewarm held is checked against the floor (check 44).
* **Record**: stop listening to any camera the wait listened to, then one recorder per channel, each
  started from its own `World` handle because the shim holds one recorder per handle, all given this
  session's id as their run id, the session's illumination source and its render set -- so each
  sidecar lists the bodies its own frame rendered, named by SUMO vehicle, and none of the bodies
  parked out of sight between loans, and, under a draw distance, marks every vehicle its camera did
  not draw. An orbit is set moving on the server as the window opens, one call, and sweeps on the
  simulation clock.
* **Advance** until the window's end, the scenario's end, a stop, a loud condition under an
  unattended caller, or write headroom running out (check 46).
* **Terminate** through `RunTerminationSequence`: drain the recorders, take the closing snapshot and
  gate records, write the run result -- with the session's compile lock and teleporting checks, the
  admission passes of the window, where every camera looked and how each view became ready -- and
  only then destroy the cameras and dispose the session.

The recorders are not given the scenario id or the seed: both would enter every PNG's `carla:capture`
chunk, and whether the observation side may carry either is open (`04_Contracts.md` open question 15);
the run result and the lock carry both.

**Every file the run writes says what made it** (`ProducerRecord`): the session declares itself the
tool, `carlacontrol.CaptureSession`, so the CarlaNet writers -- each still's sidecar and PNG, the run
manifest, the world truth track's summary -- name it and carlacontrol's release; the server's build
identity is asked once the server is reached, and the SUMO release once the session has started one,
and both go on the run result and on every still. The resolution report and the lock, written before
any server is reached, carry no server.
"""
from __future__ import annotations

import json
import logging
import math
import secrets
import sys
import time
from collections.abc import Callable
from dataclasses import dataclass, field
from datetime import UTC, datetime
from pathlib import Path
from typing import Any, NoReturn

import carlanet as carla
from CarlaNet.CoSim import CoSimSessionRefusedException

from carlacontrol.ChannelDescription import ChannelDescription
from carlacontrol.ChannelExposure import ChannelExposure
from carlacontrol.EffectiveRunConfiguration import EffectiveRunConfiguration
from carlacontrol.LaunchEcho import LaunchEcho
from carlacontrol.OrbitSensorController import OrbitSensorController
from carlacontrol.ProducerRecord import ProducerRecord
from carlacontrol.RenderedTrafficCentre import RenderedTrafficCentre
from carlacontrol.RunCloseoutReport import ChannelCapture, RunCloseoutReport
from carlacontrol.RunConfiguration import PRODUCER_KEY, RunConfiguration
from carlacontrol.RunConfigurationFindings import (
    RunConfigurationFindings,
    RunConfigurationRefusedError,
)
from carlacontrol.RunConfigurationResolver import RunConfigurationResolver
from carlacontrol.RunConfigurationValidator import (
    DEPTH_BLUEPRINT,
    RGB_BLUEPRINT,
    RunConfigurationValidator,
)
from carlacontrol.RunResult import RUN_LOCK_VERSION, RUN_RESOLUTION_VERSION, TOOL, RunResult
from carlacontrol.RunTerminationSequence import (
    CLOSE_RECORD,
    DRAIN_CAPTURE,
    RELEASE_WORLD,
    WRITE_RESULT,
    RunTerminationSequence,
)
from carlacontrol.ScenarioEpoch import ScenarioEpoch
from carlacontrol.SessionMonitor import SessionMonitor
from carlacontrol.SiteProfile import SiteProfile
from carlacontrol.StareAim import StareAim
from carlacontrol.ViewReadiness import (
    TILES_CEILING_S,
    SessionFrameVehicles,
    ViewNotReadyError,
    ViewReadinessGate,
    wait_begins_s,
)
from carlacontrol.WindowAdmissions import WindowAdmissions

SESSION_ID_PREFIX = "cap"
SIM_EPSILON_S = 1e-6
HEADROOM_INTERVAL_S = 10.0
DRAIN_TIMEOUT_S = 15.0
CAMERA_TIMEOUT_S = 10.0
DISPOSE_TIMEOUT_S = 60.0
# Within the world's release: nothing moves a camera that is gone, a depth camera leaves the world
# before the camera it is attached to, and the cameras leave before the session gives back its
# bodies, its lease and the world's clock.
ORDER_ORBIT, ORDER_DEPTH, ORDER_CAMERA, ORDER_SESSION, ORDER_REPORT = 0, 1, 2, 3, 4
# Within the record's closing: the manifest's terminal row is written before the snapshot reads it.
ORDER_MANIFEST = -1
ADJUDICATED_AT_TERMINAL = "the operator at the terminal"
# How far the session had got when it refused (CoSimSessionStage), and the outcome that makes the
# run's. A refusal at the Window stage is the run stopping, not a refusal: see _conclude_refusal.
SESSION_STAGE_OUTCOMES = {
    "Validation": ("refused_server", None),
    "Launch": ("refused_server", None),
    "Authority": ("refused_authority", None),
    "PreRoll": ("refused_preroll", "aborted_at_preroll"),
}
SESSION_STAGE_WINDOW = "Window"
# Where under the capture directory the session writes the world truth track, the truth of the run
# rather than of any one channel; its summary is written beside it.
WORLD_TRUTH_TRACK = Path("truth") / "world_truth_track.csv"
# And the run manifest, beside it: the rows the session appends from the run's opening to its close.
RUN_MANIFEST = Path("truth") / "manifest.jsonl"


class _RefusedError(Exception):
    """The launch ends with a refusal outcome."""

    def __init__(self, outcome: str, detail: str, closed_by: str | None = None) -> None:
        self.outcome = outcome
        self.detail = detail
        self.closed_by = closed_by
        super().__init__(detail)


class _StoppedError(Exception):
    """A stop arrived before the window opened."""


@dataclass
class ChannelRig:
    """The actors one channel put in the world, and its recorder."""

    index: int
    sensor_id: str
    directory: Path
    world: Any
    description: ChannelDescription | None = None
    camera: Any = None
    depth: Any = None
    orbit: OrbitSensorController | None = None
    recorder: Any = None
    aim: StareAim | None = None
    aim_record: dict = field(default_factory=dict)
    moves: int = 0
    last_move_m: float | None = None
    notes: list[str] = field(default_factory=list)
    # Where the camera is held now: spawned there, or moved there last.
    pose: Any = None
    # Whether the session follows this camera, under the render set that follows the cameras.
    followed: bool = False

    @property
    def aims_at_rendered_traffic(self) -> bool:
        return self.description is not None and self.description.aims_at_rendered_traffic()


class CaptureSession:
    """One capture run."""

    def __init__(self, site: SiteProfile, run_path: str | Path | None = None,
                 override_texts: list[tuple[str, str]] = (), *,
                 client_factory: Callable[[str, int], Any] | None = None,
                 validator: RunConfigurationValidator | None = None,
                 termination: RunTerminationSequence | None = None,
                 monitor: SessionMonitor | None = None,
                 answer: Callable[[str], str] = input,
                 stdin_is_terminal: Callable[[], bool] | None = None,
                 clock: Callable[[], float] = time.monotonic,
                 session_id: str | None = None,
                 logger: logging.Logger | None = None) -> None:
        self.site = site
        self.run_path = None if run_path is None else Path(run_path)
        self.override_texts = list(override_texts)
        self.document: RunConfiguration | None = None
        self.overrides: list[tuple[str, Any, str]] = []
        self.client_factory = client_factory or carla.Client
        self.validator = validator or RunConfigurationValidator()
        self.logger = logger or logging.getLogger(__name__)
        self.termination = termination or RunTerminationSequence(logger=self.logger)
        self.monitor_override = monitor
        self.answer = answer
        self.stdin_is_terminal = stdin_is_terminal or (lambda: sys.stdin.isatty())
        self.clock = clock
        self.session_id = session_id or self.new_session_id()
        self.effective: EffectiveRunConfiguration | None = None
        self.findings = RunConfigurationFindings()
        self.echo: LaunchEcho | None = None
        self.adjudications: list[dict] = []
        self.client: Any = None
        self.session: Any = None
        self.channels: list[ChannelRig] = []
        self.closeout: RunCloseoutReport | None = None
        self.monitor: SessionMonitor | None = None
        self.result = RunResult(self.session_id)
        self.result_path: Path | None = None
        self.capture_directory: Path | None = None
        self.session_facts: dict = {}
        self.admissions: WindowAdmissions | None = None
        self.traffic: RenderedTrafficCentre | None = None
        self.end_reached_s: float | None = None
        self.preroll_pace: float | None = None
        self.readiness: ViewReadinessGate | None = None
        self.hold_from_s: float | None = None
        # The tool every file this run writes names, the CarlaNet writers' included.
        ProducerRecord.declare_tool(TOOL)

    @staticmethod
    def new_session_id() -> str:
        """A run's identity: unique per invocation, and bookkeeping only -- nothing rendered or
        recorded depends on the wall clock it is stamped from."""
        stamp = datetime.now(UTC).strftime("%Y%m%d-%H%M%S")
        return f"{SESSION_ID_PREFIX}-{stamp}-{secrets.token_hex(3)}"

    # =============================================================================================
    def run(self, validate_only: bool = False) -> RunResult | None:
        """Run the capture and return its result, written; under `validate_only`, stop after the
        offline checks and return None when they accepted."""
        self.termination.install()
        self.termination.add_step(WRITE_RESULT, "write the run result", self._write_result)
        try:
            self._parse()
            self._resolve()
            self._validate_offline()
            if validate_only:
                self.logger.info("the offline checks accepted: %s", self.result_path)
                self.termination.steps.clear()
                return None
            self._connect()
            self._start_session()
            self._place_cameras()
            self._prewarm()
            self._record()
            self._advance_window()
        except RunConfigurationRefusedError as refusal:
            self.findings.extend(refusal.findings)
            self.result.conclude(refusal.outcome, detail=self._first_refusal())
        except _RefusedError as refusal:
            self.result.conclude(refusal.outcome, closed_by=refusal.closed_by,
                                 detail=refusal.detail)
        except _StoppedError:
            self.result.conclude("run_stopped", closed_by=self.termination.closed_by,
                                 detail="stopped before the window opened")
        except KeyboardInterrupt:
            self.termination.request_stop("operator_stop")
            self.result.conclude("run_stopped", closed_by=self.termination.closed_by,
                                 detail="interrupted")
        except CoSimSessionRefusedException as refused:
            self._conclude_refusal(refused)
        except Exception as fault:
            self.logger.exception("internal error")
            self.result.conclude("internal_error", detail=repr(fault))
        finally:
            self.termination.run()
            self.termination.uninstall()
            if self.monitor is not None:
                self.monitor.close()
        return self.result

    def _conclude_refusal(self, refused: Any) -> None:
        """Conclude the run from a session refusal, by the stage the session had reached.

        From the start and from a prewarm `Advance` alike: nothing of the window was rendered below
        `Window`, and everything the session took has been given back (the start) or is given back
        when it is disposed (an `Advance`). A stage this tool does not know is a defect, and is
        reported as one rather than guessed at.
        """
        stage = str(refused.StageName)
        detail = f"the session refused at {stage}: {refused.Message}"
        if stage == SESSION_STAGE_WINDOW:
            self.result.conclude("run_stopped", closed_by=f"fault:{type(refused).__name__}",
                                 detail=detail)
            return
        if stage not in SESSION_STAGE_OUTCOMES:
            self.logger.error("the session refused at a stage this tool does not know: %s", stage)
            self.result.conclude("internal_error", detail=detail)
            return
        outcome, closed_by = SESSION_STAGE_OUTCOMES[stage]
        holder = getattr(refused, "HeldBy", None)
        if holder is not None:
            self.result.authority_holder = str(holder)
        self.result.conclude(outcome, closed_by=closed_by, detail=detail)

    # -- resolve and validate ----------------------------------------------------------------------
    def _parse(self) -> None:
        """Read the run configuration and the overrides, keeping every one that parses so that a
        refusal still knows where its result goes."""
        findings = RunConfigurationFindings()
        for text, source in self.override_texts:
            try:
                path, value = RunConfiguration.parse_override(text, source)
            except RunConfigurationRefusedError as refusal:
                findings.extend(refusal.findings)
                continue
            self.overrides.append((path, value, source))
        if self.run_path is not None:
            try:
                self.document = RunConfiguration.from_file(self.run_path)
            except RunConfigurationRefusedError as refusal:
                findings.extend(refusal.findings)
        if findings.refused:
            self.findings.extend(findings)
            self._write_resolution_report(outcome="usage_error")
            raise RunConfigurationRefusedError(findings, "usage_error")

    def _resolve(self) -> None:
        try:
            self.effective = RunConfigurationResolver(self.site).resolve(self.document,
                                                                         self.overrides)
        except RunConfigurationRefusedError as refusal:
            self.findings.extend(refusal.findings)
            self._write_resolution_report(outcome=refusal.outcome)
            raise
        effective = self.effective
        self.result.caller = effective.value("caller")
        self.result.caller_label = effective.value("caller_label")
        self.result.schema_version = RunConfiguration.VERSION
        self.result.effective_configuration_digest = effective.digest
        self.result.expectations_declared = len(effective.value("expect") or {})
        self.capture_directory = Path(str(effective.value("paths.capture_root"))) / self.session_id
        self.result_path = self._result_path()

    def _result_path(self) -> Path:
        if self.effective is not None and self.effective.value("result_path"):
            return Path(str(self.effective.value("result_path")))
        chosen = next((value for path, value, _source in reversed(self.overrides)
                       if path == "result_path" and value), None)
        if chosen is None and self.document is not None:
            chosen = self.document.values.get("result_path")
        if chosen:
            return Path(str(chosen))
        runs_root = self.site.get("paths.runs_root")
        root = Path(str(runs_root.value)) if runs_root and runs_root.value else Path.cwd()
        return root / self.session_id / "run.result.json"

    def _validate_offline(self) -> None:
        effective = self.effective
        findings = self.validator.validate_offline(effective, self.result_path,
                                                   self.capture_directory)
        self.findings.extend(findings)
        if not findings.refused:
            free, headroom = self.validator.headroom(effective, self.capture_directory)
            codes = [RunConfigurationFindings.warning_code(w) for w in findings.warnings]
            self.echo = LaunchEcho.compute(effective, self.session_id, self.capture_directory,
                                           self.result_path, free, headroom,
                                           self.validator.bytes_per_captured_second(effective),
                                           codes)
            self.result.launch_echo = self.echo.to_dict()
            if effective.value("caller") == "attended":
                self.logger.info("\n%s\n", self.echo.render())
            self.validator.validate_launch(effective, self.echo.values(), findings)
            self.findings.extend(findings)
        if not findings.refused:
            self._adjudicate(findings)
        outcome = "refused_offline" if self.findings.refused else "accepted"
        self._write_resolution_report(outcome=outcome)
        if self.findings.refused:
            raise _RefusedError("refused_offline", self._first_refusal())
        self._write_lock()

    def _adjudicate(self, findings: RunConfigurationFindings) -> None:
        """Record each warning's adjudication, and ask a person for the ones nobody wrote down."""
        written = self.effective.value("on_warning") or {}
        source = self.effective.resolution("on_warning").provenance
        unadjudicated = []
        for warning in findings.warnings:
            code = RunConfigurationFindings.warning_code(warning)
            if written.get(code) == "proceed":
                self.adjudications.append({"code": code, "adjudication": "proceed",
                                           "adjudicated_by": source})
            elif code not in written:
                unadjudicated.append(warning)
        if not unadjudicated:
            return
        codes = sorted({RunConfigurationFindings.warning_code(w) for w in unadjudicated})
        for warning in unadjudicated:
            self.logger.warning("%s", warning)
        if not self.stdin_is_terminal():
            findings.refuse(34, "on_warning", f"warning(s) {', '.join(codes)} were raised and there "
                            "is no terminal to ask. Adjudicate each with on_warning.<code>")
            self.findings.extend(findings)
            return
        reply = self.answer(f"Proceed past {len(codes)} warning(s) ({', '.join(codes)})? "
                            "Type 'proceed' to continue: ")
        if reply.strip().lower() != "proceed":
            findings.refuse(34, "on_warning", f"the operator did not proceed past {', '.join(codes)}")
            self.findings.extend(findings)
            return
        for code in codes:
            self.adjudications.append({"code": code, "adjudication": "proceed",
                                       "adjudicated_by": ADJUDICATED_AT_TERMINAL})

    # -- the server -----------------------------------------------------------------------------
    def _connect(self) -> None:
        effective = self.effective
        host, port = str(effective.value("server.host")), int(effective.value("server.port"))
        try:
            self.client = self.client_factory(host, port)
            self.client.set_timeout(float(effective.value("server.timeout_s")))
            world = self.client.get_world()
            self.logger.info("server %s at %s:%d, map %s", self.client.get_server_version(),
                             host, port, world.get_map().name)
        except Exception as failure:
            raise _RefusedError("refused_server", f"could not reach the CARLA server at {host}:{port}: "
                           f"{failure!r}") from None
        # What the server was built from, for every record of what made a file from here on. Never
        # refuses: a server that cannot say is recorded as such.
        self.result.server = ProducerRecord.server_identity(self.client)
        server = self.result.server or {}
        self.logger.info("server build: %s", ", ".join(f"{key} {value}" for key, value in server.items()))
        findings = self.validator.validate_against_server(effective, world)
        self.findings.extend(findings)
        if findings.refused:
            raise _RefusedError("refused_server", self._first_refusal())
        self._check_stop()

    def _start_session(self) -> None:
        """Start the co-simulation session. A refusal it raises is concluded by `run`, by stage."""
        effective = self.effective
        paced = effective.value("pacing.mode") == "wall_clock"
        window = effective.window
        self.admissions = WindowAdmissions(window.begin_s, window.end_s,
                                           float(effective.value("scenario.sumo_step_s")))
        if any(effective.channel_description(index).aims_at_rendered_traffic()
               for index in range(effective.channel_count)):
            self.traffic = RenderedTrafficCentre()
        collision_detail = effective.value("collision_detail") == "on"
        world = self.client.get_world()
        session = world.start_sumo_drive(
            str(effective.scenario.config_path), str(effective.value("world_package")),
            str(effective.value("paths.catalogue")),
            fixed_delta=float(effective.value("capture.world_delta_s")),
            record_hz=float(effective.value("capture.capture_hz")),
            warm_up_to=float(effective.first_rendered_s),
            window_opens_at=float(window.begin_s),
            road_layer_visible=bool(effective.value("capture.road_layer_visible")),
            signal_layer_visible=bool(effective.value("capture.signal_layer_visible")),
            epoch=effective.epoch_object(),
            illumination=effective.illumination_object(),
            real_time_factor=float(effective.value("pacing.real_time_factor")) if paced else 0.0,
            pacing_window_s=float(effective.value("pacing.window_s")),
            sumo_home=effective.value("sumo.home"),
            allow_sumo_version_mismatch=bool(effective.value("sumo.allow_version_mismatch")),
            # Check 54 has already refused a skipped dry run the run does not accept; the session is
            # told the same so its own refusal agrees and its report records the acceptance.
            accept_skipped_dry_run=bool(effective.value("scenario.accept_skipped_dry_run")),
            draw_distance_m=effective.value("capture.draw_distance_m"),
            **self._render_set_arguments(effective),
            # Every capture run writes it, at every SUMO frame inside the window: a base rate is taken
            # over what the world contained, and the sidecars hold only what was drawn.
            world_truth_track=str(self.world_truth_track),
            run_manifest=str(self.run_manifest),
            run_manifest_header=self._manifest_header(),
            # Bound only where a channel aims at the traffic: the session reads its callbacks once
            # and hands this a record per rendered vehicle per tick for the whole run.
            on_pose=None if self.traffic is None else self.traffic.collect,
            on_admission_pass=self.admissions.observe,
            # What is printed about collisions, and nothing else: the session keeps every one either
            # way, and the line per collision is bound only where every collision is to be printed.
            collision_detail=collision_detail,
            on_collision=self._print_collision if collision_detail else None)
        if session is None:
            raise _RefusedError("refused_server", "the co-simulation assemblies are not loaded "
                           "(CarlaNet.CoSim)")
        self.session = session
        # The SUMO release the session launched, for the run result and every still.
        release = getattr(getattr(getattr(session, "Report", None), "Sumo", None), "Release", None)
        self.result.sumo = None if release is None else str(release)
        self.termination.add_step(RELEASE_WORLD, "dispose the session", session.Dispose,
                                  DISPOSE_TIMEOUT_S, ORDER_SESSION)
        self.termination.add_step(RELEASE_WORLD, "report the session", self._log_session_report,
                                  CAMERA_TIMEOUT_S, ORDER_REPORT)
        self.closeout = RunCloseoutReport(effective, self.clock)
        self.closeout.attach(session, self.admissions)
        self.closeout.attach_manifest(self.run_manifest)
        self.monitor = self.monitor_override or SessionMonitor(
            enabled=effective.value("monitor") == "on", clock=self.clock)
        # Closed before the closing snapshot, whose gate reads the manifest's terminal row.
        self.termination.add_step(CLOSE_RECORD, "close the run manifest", self._close_the_manifest,
                                  CAMERA_TIMEOUT_S, ORDER_MANIFEST)
        self.termination.add_step(CLOSE_RECORD, "take the closing snapshot",
                                  self._close_the_record)
        self.session_facts = self._session_facts(session)
        for key, value in self.session_facts.items():
            self.logger.info("%s: %s", key, value)
        self._log_scenario_checks(self.closeout.scenario_checks)
        self._check_stop()

    @property
    def run_manifest(self) -> Path:
        """Where the session writes the run manifest: beside the world truth track."""
        return self.capture_directory / RUN_MANIFEST

    def _manifest_header(self) -> dict:
        """What the run is, as the manifest's opening row carries it: what this tool knows of the run
        and the session does not. Each channel is listed as declared; its camera is named on the
        manifest as it is placed, which is when a channel declaring no name gets its default."""
        effective = self.effective
        window = effective.window
        return {
            "run_id": self.session_id,
            "session_id": self.session_id,
            "scenario_id": effective.value("scenario.scenario_id"),
            "caller": effective.value("caller"),
            "caller_label": effective.value("caller_label"),
            "tool_version": effective.tool_version,
            "effective_configuration_sha256": effective.digest,
            "window": {"name": window.name, "begin_s": window.begin_s,
                       "end_s": window.end_s if math.isfinite(window.end_s) else None,
                       "end_source": window.end_source,
                       "rendered_from_s": float(effective.first_rendered_s)},
            "channels": [{"channel": index,
                          "sensor_id": effective.channel_values(index).get("sensor_id"),
                          "pattern": effective.channel_description(index).pattern}
                         for index in range(effective.channel_count)],
            "capture_directory": str(self.capture_directory),
            "world_truth_track": str(self.world_truth_track),
            "run_manifest": str(self.run_manifest),
        }

    def _close_the_manifest(self) -> None:
        """Write the manifest's terminal row, saying why the run ended in this tool's words, before the
        closing snapshot reads it. The session's end would write it too, after the gates were read."""
        manifest = None if self.session is None else self.session.RunManifest
        if manifest is not None:
            manifest.Close(self.result.closed_by or self.termination.closed_by or self.result.outcome)

    @property
    def world_truth_track(self) -> Path:
        """Where the session writes the world truth track: under the capture directory, beside the
        channels' directories."""
        return self.capture_directory / WORLD_TRUTH_TRACK

    def _print_collision(self, span) -> None:
        """One collision as it ends, under collision_detail on. Called from the session's tick thread;
        a log line does not block it."""
        self.logger.info("collision: %s", span)

    @staticmethod
    def _render_set_arguments(effective: EffectiveRunConfiguration) -> dict:
        """The render set as `start_sumo_drive` takes it: every vehicle by default, or the optional
        limit chosen, with the region moved from CARLA's frame into SUMO's, whose y is CARLA's
        negated."""
        region = effective.value("capture.render_region")
        arguments = {"render_set": str(effective.value("capture.render_set")),
                     "region_hysteresis_m": float(effective.value("capture.render_hysteresis_m")),
                     "capacity": None if effective.value("capture.render_cap") is None
                     else int(effective.value("capture.render_cap")),
                     "render_min_pixels": float(effective.value("capture.render_min_pixels")),
                     "render_admit_lead_s": float(effective.value("capture.render_admit_lead_s")),
                     "render_release_lag_s": float(effective.value("capture.render_release_lag_s"))}
        if region is not None:
            arguments["region_centre"] = (float(region["x_m"]), -float(region["y_m"]))
            arguments["region_radius_m"] = float(region["radius_m"])
        return arguments

    def _log_scenario_checks(self, checks: dict) -> None:
        """Say what the session found of the compile lock and teleporting -- an uncompiled scenario
        and an accepted teleport louder than their ordinary cases."""
        lock = checks["compile_lock"]
        (self.logger.info if lock["compiled"] else self.logger.warning)(
            "compile lock: %s", lock["statement"])
        if lock["compiled"]:
            self.logger.info("routed by: %s", lock["routed_by"])
            self.logger.info("compiled for: %s", lock["compiled_for"])
            (self.logger.warning if lock["skipped_dry_run_accepted"] else self.logger.info)(
                "dry run: %s", lock["dry_run"])
        teleporting = checks["teleporting"]
        (self.logger.warning if teleporting["enabled"] else self.logger.info)(
            "teleporting: %s", teleporting["statement"])

    @staticmethod
    def _session_facts(session: Any) -> dict:
        report = session.Report
        facts = {"clock": str(session.Clock),
                 "sumo": f"{report.Sumo.Installation}; {report.Sumo.Verdict}",
                 "pace": str(report.Pacing),
                 "sun": str(session.Sun) if session.Sun is not None
                 else "left as the world holds it; the run's lighting honors no epoch"}
        layers = report.LayerVisibility
        facts["layers"] = {str(key): bool(layers[key]) for key in layers.Keys}
        # Whether the server holds the drive lease for this run: without it nothing on the server
        # stops a traffic manager or a second drive putting vehicles into the capture.
        refused = getattr(report, "DriveLeaseRefused", None)
        facts["drive_lease"] = (
            f"NOT HELD: this server has no drive lease, so nothing on it stops another traffic "
            f"system driving vehicles into this run (the server said: {refused})"
            if refused is not None
            else f"held as {report.DriveLeaseHolder}; the server refuses every other traffic "
                 "system's control writes")
        facts["render_set"] = (
            str(report.RenderSetPolicy) if not bool(report.RenderSetLimits)
            else f"{report.RenderSetPolicy}. An optional limit: a vehicle outside it is simulated by "
                 "SUMO and is not in CARLA -- no body, no frame, no truth record")
        asked = report.DrawDistanceMetres
        facts["draw_distance"] = (
            "none: every body drawn at any range" if asked is None
            else f"{float(asked):g} m, rendering only: a body farther than that from a channel's "
                 "camera is not in its images, and its sidecars mark it")
        return facts

    # -- the cameras -------------------------------------------------------------------------------------
    def _place_cameras(self) -> None:
        effective = self.effective
        for index in range(effective.channel_count):
            label = effective.channel_sensor_label(index)
            rig = ChannelRig(index, label, self.capture_directory / label, self.client.get_world())
            self.channels.append(rig)
            try:
                self._place_channel(rig)
            except Exception as failure:
                raise _RefusedError("refused_preroll", f"channel {label}: its camera could not be "
                               f"placed: {failure!r}", closed_by="aborted_at_preroll") from None
        self._watch_views()
        self._check_stop()

    def _watch_views(self) -> None:
        """Ask about every capture camera's tiles from the prewarm on, so that no capture is written
        before its view is ready (03 §9.5.1). Where capture.picture_settled_wait is true, also
        listen to its frames, each frame's rendered vehicles placed from the session's render set of
        the frame and the client's snapshot of it, so the blocks they cover are left out of the
        picture's comparisons; with it off, its default, no camera is listened to."""
        effective = self.effective
        ticks_per_frame = effective.ticks_per_frame
        picture_wait = bool(effective.value("capture.picture_settled_wait"))
        if picture_wait:
            self.readiness = ViewReadinessGate(
                clock=self.clock, logger=self.logger,
                ceiling_frames=int(effective.value("capture.picture_ceiling_frames")),
                tolerance_levels=float(effective.value("capture.picture_tolerance_levels")),
                picture_wait=True)
        else:
            self.readiness = ViewReadinessGate(
                clock=self.clock, logger=self.logger, picture_wait=False,
                tiles_hold_s=float(effective.value("capture.tiles_hold_s")))
        self.termination.add_step(RELEASE_WORLD, "stop watching the cameras' views",
                                  self.readiness.stop, CAMERA_TIMEOUT_S, ORDER_ORBIT)
        self.closeout.attach_readiness(self.readiness)
        for rig in self.channels:
            if not picture_wait:
                self.readiness.watch(rig.sensor_id, rig.camera, rig.world, ticks_per_frame)
                continue
            try:
                locate = SessionFrameVehicles(rig.world, self.session.RenderSet, rig.camera.id,
                                              lambda rig=rig: rig.pose)
                self.readiness.watch(rig.sensor_id, rig.camera, rig.world, ticks_per_frame,
                                     locate, float(rig.description.fov))
            except Exception as failure:
                raise _RefusedError("refused_preroll", f"channel {rig.sensor_id}: its camera's "
                               f"frames could not be watched, so its picture cannot be seen to "
                               f"settle: {failure!r}", closed_by="aborted_at_preroll") from None

    def _place_channel(self, rig: ChannelRig) -> None:
        effective = self.effective
        values = effective.channel_values(rig.index)
        description = effective.channel_description(rig.index)
        rig.description = description
        library = rig.world.get_blueprint_library()
        tick = 1.0 / float(effective.value("capture.capture_hz"))
        rgb = library.find(RGB_BLUEPRINT)
        rgb.set_attribute("image_size_x", str(description.width))
        rgb.set_attribute("image_size_y", str(description.height))
        rgb.set_attribute("fov", str(description.fov))
        rgb.set_attribute("sensor_tick", str(tick))
        rgb.set_attribute("post_process_profile", str(values["post_process_profile"]))
        # The whole exposure, every field of it, so the run states every capture's exposure and none
        # is left to the blueprint's defaults; the camera applies it over the profile.
        for attribute, value in ChannelExposure.from_channel(values).blueprint_attributes().items():
            rgb.set_attribute(attribute, value)
        transform = self._start_transform(rig)
        # Spawned under the channel's sensor_id as the camera's role_name, which the server refuses
        # where a live camera in the world holds it, so the spawn raises here. A channel with none --
        # a single channel may go unnamed -- is named by the server, Camera_<n>. Either way the name
        # is read back from the spawned camera, as every client reads it, and is the channel's from
        # here: its directory, every still in it and its platform track's callsign carry it.
        rig.camera = rig.world.spawn_camera(rgb, transform, name=description.sensor_id)
        rig.sensor_id = rig.world.camera_name(rig.camera)
        # The exposure the camera was given, read from its attributes as every capture's sidecar reads
        # it, for the manifest's camera entry and the result's.
        exposure = rig.world.camera_exposure(rig.camera)
        if self.session.RunManifest is not None:
            self.session.RunManifest.PlaceSensor(rig.sensor_id, rig.camera.id, exposure)
        rig.aim_record["exposure"] = None if exposure is None else json.loads(str(exposure.ToJson()))
        rig.directory = self.capture_directory / rig.sensor_id
        rig.aim_record["sensor_id"] = rig.sensor_id
        rig.pose = transform
        self.termination.add_step(RELEASE_WORLD, f"destroy camera {rig.sensor_id}",
                                  lambda: self._release_camera(rig), CAMERA_TIMEOUT_S, ORDER_CAMERA)
        if self._follows_cameras:
            # The render set follows every channel's camera, an orbit's included, from the next step;
            # its depth camera shares its pose and view, so it is not registered as well.
            self.session.AddCamera(rig.camera.id)
            rig.followed = True
        self.logger.info("channel %s: camera %s at %s", rig.sensor_id, rig.camera.id,
                         self._describe(transform))
        self._attach_depth_camera(rig, library, tick)
        if description.pattern == "orbit":
            rig.orbit = OrbitSensorController(
                rig.camera, world=None, logger=self.logger,
                clock=lambda: float(self.session.RenderedTimeSeconds))
            rig.orbit.set_orbit_params(center_x=description.orbit_centre_x_m,
                                       center_y=description.orbit_centre_y_m,
                                       center_z=description.orbit_centre_z_m,
                                       radius=description.orbit_radius_m,
                                       altitude=description.orbit_altitude_m,
                                       speed=description.orbit_period_s,
                                       angle=0.0)
            # The circle goes to the server now, not moving: the camera was spawned at the pose it
            # opens on and the server holds it there until the window opens (`_set_orbits_moving`),
            # so the view the window's first frame is written from is the one whose readiness is
            # witnessed. A server built before it flew orbits refuses here, at pre-roll, rather than
            # as the window opens; nothing in this process flies the camera in its place.
            rig.orbit.hold_on_server()
            self.termination.add_step(RELEASE_WORLD, f"stop orbit {rig.sensor_id}",
                                      lambda: rig.orbit.set_enabled(False), CAMERA_TIMEOUT_S,
                                      ORDER_ORBIT)

    def _attach_depth_camera(self, rig: ChannelRig, library: Any, tick: float) -> None:
        """Spawn the depth camera occlusion is measured against, attached to the channel's camera.

        Every channel gets one: occlusion is measured wherever a camera records, and nothing turns
        it off (the owner's ruling of 2026-10-05). The depth camera takes the camera's image size,
        field of view and sensor tick, so its captures pair with the camera's frame for frame.

        The attachment is rigid and the depth camera's pose relative to its parent the identity, so
        it holds the camera's pose and view by construction and one move of the camera carries
        both. That is what keeps the two from being captured a frame apart: a move sent to each
        camera in turn is two round trips, and under a free-running world the simulator renders
        between them, so a frame can be captured with the camera at the new pose and the depth
        camera still at the old one -- which the recorder refuses to pair (`OcclusionEstimator`'s
        pose check), and measured on an orbit lost 25 of 65 captures that way before the rig moved
        its cameras in one batch. Attached, there is no second camera to move: a stare moved
        through the prewarm and an orbit flown through the window carry their depth camera with
        them, and nothing in this process ever moves the depth camera itself.

        The server spawns an attached actor at the given transform and attaches it keeping that
        transform relative to its parent (`spawn_actor_with_parent`, `AttachActors`), so the pose
        given here is the relative one. It reports an attached sensor's world pose on every
        snapshot (`GetActorGlobalTransform`) and in every image header (`MakeCaptureHeader`), which
        is what the recorder's depth pose check compares against the camera's. The depth camera
        is destroyed before the camera it is attached to.
        """
        effective = self.effective
        description = rig.description
        depth = library.find(DEPTH_BLUEPRINT)
        depth.set_attribute("image_size_x", str(description.width))
        depth.set_attribute("image_size_y", str(description.height))
        depth.set_attribute("fov", str(description.fov))
        depth.set_attribute("sensor_tick", str(tick))
        depth.set_attribute("max_range", str(effective.value("occlusion.depth_max_range_m")))
        rig.depth = rig.world.spawn_actor(depth, carla.Transform(), attach_to=rig.camera,
                                          attachment_type=carla.AttachmentType.Rigid)
        self.termination.add_step(RELEASE_WORLD, f"destroy depth camera {rig.sensor_id}",
                                  rig.depth.destroy, CAMERA_TIMEOUT_S, ORDER_DEPTH)
        self.logger.info("channel %s: depth camera %s attached to camera %s", rig.sensor_id,
                         rig.depth.id, rig.camera.id)

    @property
    def _follows_cameras(self) -> bool:
        """Whether the optional render set that follows the cameras was chosen."""
        return self.effective.value("capture.render_set") == "cameras"

    def _release_camera(self, rig: ChannelRig) -> None:
        """Let the session stop following a channel's camera, where it was, then destroy it: a
        registered camera the world no longer has is counted as missing at every pass."""
        if rig.followed:
            rig.followed = False
            self.session.RemoveCamera(rig.camera.id)
        rig.camera.destroy()

    def _start_transform(self, rig: ChannelRig) -> carla.Transform:
        """Where a channel's camera is spawned, recording what it declared in `rig.aim_record`.

        A stare aimed at the rendered traffic has no point yet: it starts over the centre of the
        world's staging bounds, at CARLA's origin height, and follows the traffic from the prewarm's
        first step.
        """
        description = rig.description
        record: dict = {"sensor_id": rig.sensor_id, "pattern": description.pattern}
        if description.pattern == ChannelDescription.STARE:
            if description.aims_at_rendered_traffic():
                x, y, source = self._world_centre(rig.world)
                aim = StareAim.aimed_at(description, x, y, 0.0)
                record.update({"form": ChannelDescription.RENDERED_TRAFFIC,
                               "placed_before_the_prewarm": {
                                   "look_at": {"x_m": x, "y_m": y, "z_m": 0.0, "source": source},
                                   "pose": aim.to_dict()}})
            else:
                aim = StareAim.from_channel(description)
                if description.declares_pose():
                    record["form"] = "pose"
                else:
                    record.update({"form": "look_at_point",
                                   "look_at": {"x_m": description.stare_look_at_x_m,
                                               "y_m": description.stare_look_at_y_m,
                                               "z_m": description.stare_look_at_z_m}})
                record["pose"] = aim.to_dict()
            rig.aim = aim
            rig.aim_record = record
            return self._transform_of(aim)
        # The pose the orbit opens on: angle zero, east of the centre, where
        # `OrbitSensorController` starts. It is held there through the pre-roll.
        opening = OrbitSensorController.orbit_transform(
            description.orbit_centre_x_m, description.orbit_centre_y_m,
            description.orbit_centre_z_m, description.orbit_radius_m,
            description.orbit_altitude_m, 0.0)
        location, rotation = opening.location, opening.rotation
        record.update({"centre": {"x_m": description.orbit_centre_x_m,
                                  "y_m": description.orbit_centre_y_m,
                                  "z_m": description.orbit_centre_z_m},
                       "radius_m": description.orbit_radius_m,
                       "altitude_m": description.orbit_altitude_m,
                       "period_s": description.orbit_period_s,
                       "held_through_the_pre_roll": {
                           "angle_deg": 0.0,
                           "pose": {"x_m": location.x, "y_m": location.y, "z_m": location.z,
                                    "pitch_deg": rotation.pitch, "yaw_deg": rotation.yaw},
                           "sweeps_from": "the window's opening"}})
        rig.aim_record = record
        return opening

    @staticmethod
    def _world_centre(world: Any) -> tuple[float, float, str]:
        """The centre of the world's staging bounds in CARLA's frame, and what it is -- CARLA's origin
        where the world publishes no bounds. A fact about the world, never a setting."""
        bounds = world.get_staging_bounds()
        if bounds is None:
            return 0.0, 0.0, "CARLA's origin: the world publishes no staging bounds"
        return ((float(bounds["min_x"]) + float(bounds["max_x"])) / 2.0,
                (float(bounds["min_y"]) + float(bounds["max_y"])) / 2.0,
                "the center of the world's staging bounds")

    @staticmethod
    def _transform_of(aim: StareAim) -> carla.Transform:
        return carla.Transform(carla.Location(x=aim.x_m, y=aim.y_m, z=aim.z_m),
                               carla.Rotation(pitch=aim.pitch_deg, yaw=aim.yaw_deg, roll=0.0))

    def _move(self, rig: ChannelRig, aim: StareAim) -> None:
        """Put a stare's camera at a new pose. Its depth camera is attached to the camera and goes
        with it in the same call (`_attach_depth_camera`)."""
        transform = self._transform_of(aim)
        rig.camera.set_transform(transform)
        rig.pose = transform
        previous = rig.aim
        rig.last_move_m = None if previous is None else math.dist(
            (previous.x_m, previous.y_m, previous.z_m), (aim.x_m, aim.y_m, aim.z_m))
        rig.aim = aim
        rig.moves += 1

    @staticmethod
    def _describe(transform: Any) -> str:
        location, rotation = transform.location, transform.rotation
        return (f"({location.x:.1f}, {location.y:.1f}, {location.z:.1f}) m, pitch "
                f"{rotation.pitch:.1f}, yaw {rotation.yaw:.1f}")

    # -- the prewarm --------------------------------------------------------------------------------------
    def _prewarm(self) -> None:
        """Advance to the window's opening without recording, waiting there for every view.

        The wait for each channel's view (03 §9.5.1) is told after every step the session renders
        and asks nothing in between, so it ticks with the prewarm and cannot run without it. It
        begins once every camera holds the pose the window opens on: at once, or -- where a stare
        follows the rendered traffic -- when that stare stops to hold its pose, capture.tiles_hold_s
        before the window opens, or one SUMO step and the picture's ceiling where the picture is
        waited on.
        """
        window = self.effective.window
        session = self.session
        started = float(session.RenderedTimeSeconds)
        following = self.traffic is not None
        self.hold_from_s = self._hold_from(started) if following else started
        if not following:
            self._begin_the_wait()
        while float(session.RenderedTimeSeconds) < window.begin_s - SIM_EPSILON_S:
            self._check_stop()
            waiting = self.readiness.begun
            if following:
                self.traffic.begin_step()
            more = session.Advance()
            if following:
                self.traffic.end_step()
                self._follow_traffic()
            if not more:
                raise _RefusedError("refused_preroll", f"the scenario ended at "
                               f"t={float(session.RenderedTimeSeconds):g}, before the window "
                               f"opened at t={window.begin_s:g}", closed_by="aborted_at_preroll")
            if waiting:
                self._observe_views()
            elif following and float(session.RenderedTimeSeconds) >= \
                    self.hold_from_s - SIM_EPSILON_S:
                following = False
                self._resolve_traffic_aim()
                self._begin_the_wait()
            self.monitor.update(self.closeout.snapshot())
        if float(session.RenderedTimeSeconds) > started:
            self.preroll_pace = self.closeout.snapshot()["pacing"]["achieved_factor"]
        findings = self.validator.preroll_pace(self.effective, self.preroll_pace)
        self.findings.extend(findings)
        if findings.refused:
            raise _RefusedError("refused_preroll", self._first_refusal(),
                           closed_by="aborted_at_preroll")
        self._check_views_ready()
        self._check_stop()

    def _hold_from(self, started: float) -> float:
        """Where a stare following the rendered traffic stops and holds: capture.tiles_hold_s before
        the window opens -- or, where capture.picture_settled_wait is true, one SUMO step and the
        picture's ceiling, its frames at the capture rate and the ten-tick span -- in whole SUMO
        steps, and never before the prewarm's first step has rendered traffic to measure
        (`wait_begins_s`)."""
        effective = self.effective
        picture_wait = bool(effective.value("capture.picture_settled_wait"))
        ceiling = int(effective.value("capture.picture_ceiling_frames")) if picture_wait else 0
        tiles_hold = 0.0 if picture_wait else float(effective.value("capture.tiles_hold_s"))
        return wait_begins_s(effective.window.begin_s, started,
                             float(effective.value("scenario.sumo_step_s")),
                             float(effective.value("capture.world_delta_s")),
                             effective.ticks_per_frame, ceiling, True,
                             picture_wait=picture_wait, tiles_hold_s=tiles_hold)

    # -- the views ---------------------------------------------------------------------------------
    def _begin_the_wait(self) -> None:
        session = self.session
        rendered = float(session.RenderedTimeSeconds)
        self.readiness.begin(int(session.Report.Ticks), rendered)
        if not self.readiness.picture_wait:
            self.logger.info("waiting for every channel's view from t=%g to the window's opening at "
                             "t=%g: its photoreal tiles in (ceiling %.0f s of wall clock); the "
                             "picture is not waited on (capture.picture_settled_wait is false); a "
                             "view not ready by then refuses the run", rendered,
                             self.effective.window.begin_s, TILES_CEILING_S)
            return
        self.logger.info("waiting for every channel's view from t=%g to the window's opening at "
                         "t=%g: its photoreal tiles in (ceiling %.0f s of wall clock), then its "
                         "picture settled within %g gray levels with its rendered vehicles left out "
                         "(ceiling %d of the camera's frames since its tiles); a view not ready by "
                         "then refuses the run", rendered,
                         self.effective.window.begin_s, TILES_CEILING_S,
                         self.readiness.tolerance_levels, self.readiness.ceiling_frames)

    def _observe_views(self) -> None:
        try:
            said = self.readiness.after_step(int(self.session.Report.Ticks))
        except ViewNotReadyError as not_ready:
            self._refuse_not_ready([(not_ready.sensor_id, str(not_ready))])
        for level, line in said:
            self.logger.log(level, "%s", line)

    def _check_views_ready(self) -> None:
        """Check 50: every channel's view is ready as the window opens."""
        begin = self.effective.window.begin_s
        try:
            not_ready = self.readiness.at_window_open(begin)
        except ViewNotReadyError as at_ceiling:
            # The prewarm's last frames, compared as the window opens, reached the ceiling.
            self._refuse_not_ready([(at_ceiling.sensor_id, str(at_ceiling))])
        if not_ready:
            self._refuse_not_ready([(channel.sensor_id, channel.not_ready_message(begin))
                                    for channel in not_ready])
        for channel in self.readiness.channels:
            if not channel.picture_wait:
                self.logger.info("channel %s: ready as the window opens -- tiles in at frame %d; the "
                                 "picture was not waited on (capture.picture_settled_wait is false)",
                                 channel.sensor_id, channel.tiles["in_at_frame"])
                continue
            self.logger.info("channel %s: ready as the window opens -- tiles in at frame %d, picture "
                             "settled at frame %d (%.2f gray levels, %.0f%% of its blocks judged)",
                             channel.sensor_id, channel.tiles["in_at_frame"],
                             channel.picture["settled_at_frame"],
                             channel.picture["residual_levels"],
                             100.0 * channel.picture["judged_share"])

    def _refuse_not_ready(self, not_ready: list[tuple[str, str]]) -> NoReturn:
        """Refuse at pre-roll, one finding per channel whose view is not ready."""
        findings = RunConfigurationFindings()
        for sensor_id, message in not_ready:
            index = next((rig.index for rig in self.channels if rig.sensor_id == sensor_id), None)
            findings.refuse(50, "capture.channels" if index is None
                            else f"capture.channels[{index}]", message)
        self.findings.extend(findings)
        raise _RefusedError("refused_preroll", not_ready[0][1], closed_by="aborted_at_preroll")

    def _follow_traffic(self) -> None:
        """Move every stare aimed at the rendered traffic to the pose around the centre of the
        vehicles the step just rendered showed on its last frame. A step that rendered nothing
        leaves them where they are."""
        centre = self.traffic.centre()
        if centre is None:
            return
        for rig in self.channels:
            if rig.aims_at_rendered_traffic:
                self._move(rig, StareAim.aimed_at(rig.description, *centre))

    def _resolve_traffic_aim(self) -> None:
        """Record the point each stare aimed at the rendered traffic resolved to, and the pose it
        now holds through the rest of the prewarm and the whole window: the centre of the step the
        hold begins after, which `_follow_traffic` has just moved it to.

        Raises:
            _RefusedError: `refused_preroll` where the last frame before the hold rendered no
                vehicle, so there is nothing to aim at.
        """
        rigs = [rig for rig in self.channels if rig.aims_at_rendered_traffic]
        measured = self.traffic.to_dict()
        rendered = float(self.session.RenderedTimeSeconds)
        if measured is None:
            labels = ", ".join(rig.sensor_id for rig in rigs)
            raise _RefusedError(
                "refused_preroll", f"channel(s) {labels} aim at the rendered traffic, and the "
                f"prewarm's step before the hold, which ended at t={rendered:g} with the window "
                f"opening at t={self.effective.window.begin_s:g}, rendered no vehicle with a body: "
                "there is nothing to aim at. Aim the channel at a point or a pose, or open the "
                "window where the scenario has traffic",
                closed_by="aborted_at_preroll")
        point = {key: measured[key] for key in ("x_m", "y_m", "z_m")}
        for rig in rigs:
            rig.aim_record.update({
                "look_at": point,
                "measured_on": {key: measured[key]
                                for key in ("vehicles", "frame_tick", "frame_s")},
                "pose": rig.aim.to_dict(),
                "moves_before_the_window": rig.moves,
                "last_move_m": rig.last_move_m,
                "held_from_s": rendered,
                "held_before_the_window_s": self.effective.window.begin_s - rendered,
                "as_look_at_point": {"stare_look_at_x_m": point["x_m"],
                                     "stare_look_at_y_m": point["y_m"],
                                     "stare_look_at_z_m": point["z_m"]}})
            self.logger.info("channel %s: aimed at %d rendered vehicles centered on (%.1f, %.1f, "
                             "%.1f) m at t=%g; camera at %s, held from t=%g for its view to be "
                             "ready and for the window", rig.sensor_id, measured["vehicles"],
                             point["x_m"], point["y_m"], point["z_m"], measured["frame_s"],
                             rig.aim.describe(), rendered)

    # -- recording ----------------------------------------------------------------------------------------
    def _record(self) -> None:
        effective = self.effective
        hz = float(effective.value("capture.capture_hz"))
        # Every view was ready as the window opened; the recorders take the cameras from here.
        self.readiness.stop()
        for rig in self.channels:
            description = effective.channel_description(rig.index)
            rig.directory.mkdir(parents=True, exist_ok=True)
            rig.recorder = rig.world.start_recording(
                rig.camera, str(rig.directory), hz, fov=float(description.fov),
                camera_name=rig.sensor_id, run_id=self.session_id,
                depth_camera=rig.depth,
                occlusion_margin_m=float(effective.value("occlusion.margin_m")),
                occlusion_samples=int(effective.value("occlusion.samples")),
                illumination=self.session.Illumination,
                render_set=self.session.RenderSet,
                sumo_version=self.result.sumo)
            if rig.recorder is None:
                raise _RefusedError("refused_preroll", "native recording is unavailable "
                               "(CarlaNet.Recording)", closed_by="aborted_at_preroll")
            self.termination.add_step(DRAIN_CAPTURE, f"drain recorder {rig.sensor_id}",
                                      rig.world.stop_recording, DRAIN_TIMEOUT_S)
            self.closeout.add_channel(ChannelCapture(rig.sensor_id, rig.directory, rig.recorder))
            self.logger.info("recording %s -> %s", rig.sensor_id, rig.directory)
        self._set_orbits_moving()

    def _set_orbits_moving(self) -> None:
        """An orbit held the pose it opens on through the pre-roll, so the view its first capture is
        written from is the one whose readiness was witnessed; it sweeps from the window's opening,
        and the ground it sweeps afterwards has no such witness."""
        for rig in self.channels:
            if rig.orbit is not None:
                rig.orbit.set_enabled(True)

    # -- the window ------------------------------------------------------------------------------------
    def _advance_window(self) -> None:
        effective = self.effective
        window = effective.window
        session = self.session
        at_scenario_end = window.end_source.startswith("the scenario's own end")
        unattended = effective.value("caller") == "unattended"
        next_headroom = self.clock() + HEADROOM_INTERVAL_S
        while True:
            now = float(session.RenderedTimeSeconds)
            if self.termination.stop_requested.is_set():
                self.end_reached_s = now
                self.result.conclude("run_stopped", closed_by=self.termination.closed_by,
                                     detail=f"stopped at t={now:g}")
                return
            if now >= window.end_s - SIM_EPSILON_S:
                self.end_reached_s = now
                self.result.conclude("run_finished",
                                     closed_by="scenario_end" if at_scenario_end else "window_end")
                return
            more = session.Advance()
            snapshot = self.closeout.snapshot()
            self.monitor.update(snapshot)
            for condition, message in self.closeout.loud_conditions(snapshot):
                self.monitor.loud(condition, message)
                if unattended:
                    self.termination.request_stop(f"loud:{condition}")
            if self.clock() >= next_headroom:
                next_headroom = self.clock() + HEADROOM_INTERVAL_S
                self._check_headroom()
            if not more:
                self.end_reached_s = float(session.RenderedTimeSeconds)
                self.result.conclude("run_finished", closed_by="scenario_end",
                                     detail="SUMO has no vehicle left to simulate or insert")
                return

    def _check_headroom(self) -> None:
        _free, headroom_s = self.validator.headroom(self.effective, self.capture_directory)
        floor = float(self.effective.value("write_headroom_floor_s"))
        if headroom_s is not None and headroom_s < floor:
            self.logger.warning("write headroom is %.0f s of capture and the floor is %.0f s; "
                                "stopping cleanly", headroom_s, floor)
            self.termination.request_stop("write_headroom")

    def _check_stop(self) -> None:
        if self.termination.stop_requested.is_set():
            raise _StoppedError()

    # -- the record ------------------------------------------------------------------------------------
    def _close_the_record(self) -> None:
        """The closing snapshot and gate records: the run result's `produced` block."""
        snapshot = self.closeout.snapshot()
        # The result's warnings are read from its findings and adjudications, which must be the
        # run's before the gate counts the unadjudicated ones.
        self.result.findings = self.findings.to_list()
        self.result.adjudications = list(self.adjudications)
        unadjudicated = len([w for w in self.result.warnings if not w.get("adjudication")])
        gates = self.closeout.gates(snapshot, unadjudicated)
        window = self.effective.window
        epoch = ScenarioEpoch.read(self.effective.epoch_object())
        reached = self.end_reached_s if self.end_reached_s is not None else snapshot["sim_time_s"]
        self.result.produced = {
            "capture_directory": str(self.capture_directory),
            "world_truth_track": str(self.world_truth_track),
            "run_manifest": str(self.run_manifest),
            "closed_by": self.result.closed_by or self.termination.closed_by,
            "window": {"name": window.name, "begin_s": window.begin_s,
                       "end_declared_s": window.end_s, "end_source": window.end_source,
                       "end_reached_s": reached,
                       "civil": [epoch.civil_instant_at(window.begin_s),
                                 None if reached is None or not math.isfinite(reached)
                                 else epoch.civil_instant_at(reached)]},
            "channels": snapshot["channels"],
            "cameras": [dict(rig.aim_record) for rig in self.channels],
            "readiness": snapshot.get("readiness"),
            "gates": gates,
            "admissions": snapshot["admissions"],
            "session": {**self.session_facts, **(snapshot["scenario_checks"] or {}),
                        "last_snapshot": {
                            key: snapshot[key] for key in ("pacing", "render", "admission",
                                                           "illumination", "solar_audit",
                                                           "divergence")
                            if key in snapshot}},
            "preroll_achieved_factor": self.preroll_pace,
            "recorder_run_id": self.session_id,
        }
        self.logger.info("\n%s\n", RunCloseoutReport.render(snapshot, gates))

    def _log_session_report(self) -> None:
        """The session's own report, logged once it has given everything back.

        Its counts are live throughout -- the admissions are the latest admission pass's running
        total (03 §8.8), and the run result carries that pass and the window's -- and it is read
        after disposal only so that the text includes what disposal
        tallies last: the refused vehicle types, the bodies and the time spent.
        """
        self.logger.info("%s", self.session.Report)

    def _write_result(self) -> None:
        result = self.result
        if result.outcome is None:
            result.conclude("internal_error", detail="the run ended without an outcome")
        result.findings = self.findings.to_list()
        result.adjudications = list(self.adjudications)
        if result.produced is not None:
            result.produced["termination"] = self.termination.report()
        path = self.result_path or self._result_path()
        written = result.write(path)
        self.logger.info("run result: %s (%s, exit status %d)", written, result.outcome,
                         result.exit_status)

    def _write_resolution_report(self, outcome: str) -> None:
        path = RunResult.sibling(self.result_path or self._result_path(), "resolution")
        document = {
            "resolution_version": RUN_RESOLUTION_VERSION,
            "producer": self.result.producer(),
            "outcome": outcome,
            "session_id": self.session_id,
            "check_catalogue": "carla-capture run checks",
            "findings": self.findings.to_list(),
            "launch_echo": None if self.echo is None else self.echo.to_dict(),
            "effective_configuration": None if self.effective is None
            else self.effective.to_document(),
            "site_profile": self.site.to_record(),
        }
        RunResult.write_json(path, document)
        self.result.artifacts["resolution_report"] = str(path)

    def _write_lock(self) -> None:
        effective = self.effective
        result_path = self.result_path
        replay = RunResult.sibling(result_path, "effective")
        # Replayable as a run configuration, which reads the record of what wrote it as provenance and
        # leaves it out of the configuration's digest.
        RunResult.write_json(replay, {**effective.to_run_configuration(),
                                      PRODUCER_KEY: self.result.producer()})
        lock = RunResult.sibling(result_path, "lock")
        RunResult.write_json(lock, {
            "lock_version": RUN_LOCK_VERSION,
            "producer": self.result.producer(),
            "session_id": self.session_id,
            "effective_configuration_sha256": effective.digest,
            "tool_version": effective.tool_version,
            "schema_version": RunConfiguration.VERSION,
            "caller": effective.value("caller"),
            "caller_label": effective.value("caller_label"),
            "inputs": {"scenario_lock_sha256": effective.value("scenario.lock_sha256"),
                       "scenario_id": effective.value("scenario.scenario_id"),
                       "world_network_fingerprint": effective.value("world.network_fingerprint"),
                       "world_opendrive_sha256": effective.value("world.opendrive_sha256"),
                       "catalogue_digest": effective.value("scenario.catalogue_digest"),
                       "epoch_block_sha256": effective.value("scenario.epoch_block_sha256"),
                       "sumo_seed": effective.value("scenario.sumo_seed")},
            "adjudications": self.adjudications,
            "expectations": effective.value("expect"),
            "effective_configuration": effective.to_document(),
            "replay": {"configuration_path": str(replay), "non_interactive": True},
            "site_profile": self.site.to_record(),
        })
        self.result.artifacts["lock"] = str(lock)
        self.result.artifacts["effective_configuration"] = str(replay)

    def _first_refusal(self) -> str:
        refusals = self.findings.refusals
        return str(refusals[0]) if refusals else "refused"
