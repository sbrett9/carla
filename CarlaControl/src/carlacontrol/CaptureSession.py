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
* **Start the session** (`world.start_sumo_drive`), which checks the loaded world is the package's,
  starts the SUMO release that converted the world, takes the world's clock and the population lease,
  hides the layers, fast-forwards SUMO to the prewarm's first instant and binds the sun there. Its
  refusals are mapped by type, because they carry no stage: a lease held by another
  (`PopulationAuthorityHeldException`) is `refused_authority`, a failed solar audit
  (`SolarAuditFailedException`) is `refused_preroll`, and every other refusal is `refused_server` --
  including the few the session raises after its lease, such as a sun the world did not take.
* **Place the cameras**: each channel's RGB camera at its stare pose -- or above its orbit's centre,
  flown by `OrbitSensorController` -- with `sensor_tick` at the capture interval and the
  post-process profile set by name; and, where occlusion is measured, a depth camera at the stare's
  pose. The cameras exist through the prewarm, so the tiles their views select are streamed before
  the first capture.
* **Prewarm**: advance until the window's begin without recording. Under `wall_clock` the pace the
  prewarm held is checked against the floor (check 44).
* **Record**: one recorder per channel, each started from its own `World` handle because the shim
  holds one recorder per handle, all given this session's id as their run id and the session's
  illumination source.
* **Advance** until the window's end, the scenario's end, a stop, a loud condition under an
  unattended caller, or write headroom running out (check 46).
* **Terminate** through `RunTerminationSequence`: drain the recorders, take the closing snapshot and
  gate records, write the run result, and only then destroy the cameras and dispose the session.

The recorders are not given the scenario id or the seed: both would enter every PNG's `carla:capture`
chunk, and whether the observation side may carry either is open (`04_Contracts.md` open question 15);
the run result and the lock carry both.
"""
from __future__ import annotations

import logging
import math
import secrets
import sys
import time
from collections.abc import Callable
from dataclasses import dataclass, field
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

import carlanet as carla
from CarlaNet.CoSim import (
    CoSimSessionRefusedException,
    PopulationAuthorityHeldException,
    SolarAuditFailedException,
)

from carlacontrol.EffectiveRunConfiguration import EffectiveRunConfiguration
from carlacontrol.LaunchEcho import LaunchEcho
from carlacontrol.OrbitSensorController import OrbitSensorController
from carlacontrol.RunCloseoutReport import ChannelCapture, RunCloseoutReport
from carlacontrol.RunConfiguration import RunConfiguration
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
from carlacontrol.RunResult import RunResult
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

SESSION_ID_PREFIX = "cap"
SIM_EPSILON_S = 1e-6
HEADROOM_INTERVAL_S = 10.0
DRAIN_TIMEOUT_S = 15.0
CAMERA_TIMEOUT_S = 10.0
DISPOSE_TIMEOUT_S = 60.0
# Within the world's release: nothing moves a camera that is gone, and the cameras leave the world
# before the session gives back its bodies, its lease and the world's clock.
ORDER_ORBIT, ORDER_CAMERA, ORDER_SESSION, ORDER_REPORT = 0, 1, 2, 3
ADJUDICATED_AT_TERMINAL = "the operator at the terminal"


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
    camera: Any = None
    depth: Any = None
    orbit: OrbitSensorController | None = None
    recorder: Any = None
    notes: list[str] = field(default_factory=list)


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
        self.end_reached_s: float | None = None
        self.preroll_pace: float | None = None

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
        except CoSimSessionRefusedException as fault:
            self.result.conclude("run_stopped",
                                 closed_by=f"fault:{type(fault).__name__}",
                                 detail=str(fault.Message))
        except Exception as fault:
            self.logger.exception("internal error")
            self.result.conclude("internal_error", detail=repr(fault))
        finally:
            self.termination.run()
            self.termination.uninstall()
            if self.monitor is not None:
                self.monitor.close()
        return self.result

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
                            "is no terminal to ask. Adjudicate each with on_warning.<code> "
                            "(12 §6.4)")
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
        findings = self.validator.validate_against_server(effective, world)
        self.findings.extend(findings)
        if findings.refused:
            raise _RefusedError("refused_server", self._first_refusal())
        self._check_stop()

    def _start_session(self) -> None:
        effective = self.effective
        region = effective.value("capture.render_region")
        paced = effective.value("pacing.mode") == "wall_clock"
        world = self.client.get_world()
        try:
            session = world.start_sumo_drive(
                str(effective.scenario.config_path), str(effective.value("world_package")),
                str(effective.value("paths.catalogue")),
                # The session's region is in SUMO's frame, whose y is CARLA's negated.
                region_centre=(float(region["x_m"]), -float(region["y_m"])),
                admit_radius_m=float(region["radius_m"]),
                hysteresis_m=float(effective.value("capture.render_hysteresis_m")),
                capacity=int(effective.value("capture.render_cap")),
                maximum_bodies=int(effective.value("capture.render_cap_hard")),
                fixed_delta=float(effective.value("capture.world_delta_s")),
                record_hz=float(effective.value("capture.capture_hz")),
                warm_up_to=float(effective.first_rendered_s),
                road_layer_visible=bool(effective.value("capture.road_layer_visible")),
                signal_layer_visible=bool(effective.value("capture.signal_layer_visible")),
                epoch=effective.epoch_object(),
                illumination=effective.illumination_object(),
                real_time_factor=float(effective.value("pacing.real_time_factor")) if paced else 0.0,
                pacing_window_s=float(effective.value("pacing.window_s")),
                sumo_home=effective.value("sumo.home"),
                allow_sumo_version_mismatch=bool(effective.value("sumo.allow_version_mismatch")))
        except PopulationAuthorityHeldException as held:
            self.result.authority_holder = str(held.HeldBy)
            raise _RefusedError("refused_authority", str(held.Message)) from None
        except SolarAuditFailedException as failed:
            raise _RefusedError("refused_preroll", str(failed.Message),
                           closed_by="aborted_at_preroll") from None
        except CoSimSessionRefusedException as refused:
            raise _RefusedError("refused_server", str(refused.Message)) from None
        if session is None:
            raise _RefusedError("refused_server", "the co-simulation assemblies are not loaded "
                           "(CarlaNet.CoSim)")
        self.session = session
        self.termination.add_step(RELEASE_WORLD, "dispose the session", session.Dispose,
                                  DISPOSE_TIMEOUT_S, ORDER_SESSION)
        self.termination.add_step(RELEASE_WORLD, "report the session", self._log_session_report,
                                  CAMERA_TIMEOUT_S, ORDER_REPORT)
        self.closeout = RunCloseoutReport(effective, self.clock)
        self.closeout.attach(session)
        self.monitor = self.monitor_override or SessionMonitor(
            enabled=effective.value("monitor") == "on", clock=self.clock)
        self.termination.add_step(CLOSE_RECORD, "take the closing snapshot",
                                  self._close_the_record)
        self.session_facts = self._session_facts(session)
        for key, value in self.session_facts.items():
            self.logger.info("%s: %s", key, value)
        self._check_stop()

    @staticmethod
    def _session_facts(session: Any) -> dict:
        report = session.Report
        facts = {"clock": str(session.Clock),
                 "sumo": f"{report.Sumo.Installation}; {report.Sumo.Verdict}",
                 "pace": str(report.Pacing),
                 "sun": str(session.Sun) if session.Sun is not None
                 else "left as the world holds it; the run's lighting honours no epoch"}
        layers = report.LayerVisibility
        facts["layers"] = {str(key): bool(layers[key]) for key in layers.Keys}
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
        self._check_stop()

    def _place_channel(self, rig: ChannelRig) -> None:
        effective = self.effective
        values = effective.channel_values(rig.index)
        description = effective.channel_description(rig.index)
        library = rig.world.get_blueprint_library()
        tick = 1.0 / float(effective.value("capture.capture_hz"))
        rgb = library.find(RGB_BLUEPRINT)
        rgb.set_attribute("image_size_x", str(description.width))
        rgb.set_attribute("image_size_y", str(description.height))
        rgb.set_attribute("fov", str(description.fov))
        rgb.set_attribute("sensor_tick", str(tick))
        rgb.set_attribute("post_process_profile", str(values["post_process_profile"]))
        transform = self._start_transform(description)
        rig.camera = rig.world.spawn_actor(rgb, transform)
        self.termination.add_step(RELEASE_WORLD, f"destroy camera {rig.sensor_id}",
                                  rig.camera.destroy, CAMERA_TIMEOUT_S, ORDER_CAMERA)
        self.logger.info("channel %s: camera %s at %s", rig.sensor_id, rig.camera.id,
                         self._describe(transform))
        if description.pattern == "stare" and effective.value("occlusion.enabled"):
            depth = library.find(DEPTH_BLUEPRINT)
            depth.set_attribute("image_size_x", str(description.width))
            depth.set_attribute("image_size_y", str(description.height))
            depth.set_attribute("fov", str(description.fov))
            depth.set_attribute("sensor_tick", str(tick))
            depth.set_attribute("max_range", str(effective.value("occlusion.depth_max_range_m")))
            rig.depth = rig.world.spawn_actor(depth, transform)
            self.termination.add_step(RELEASE_WORLD, f"destroy depth camera {rig.sensor_id}",
                                      rig.depth.destroy, CAMERA_TIMEOUT_S, ORDER_CAMERA)
        if description.pattern == "orbit":
            rig.orbit = OrbitSensorController(rig.camera, world=None, logger=self.logger)
            rig.orbit.set_orbit_params(center_x=description.orbit_centre_x_m,
                                       center_y=description.orbit_centre_y_m,
                                       center_z=description.orbit_centre_z_m,
                                       radius=description.orbit_radius_m,
                                       altitude=description.orbit_altitude_m,
                                       speed=description.orbit_period_s)
            self.termination.add_step(RELEASE_WORLD, f"stop orbit {rig.sensor_id}",
                                      rig.orbit.stop_updater, CAMERA_TIMEOUT_S, ORDER_ORBIT)
            rig.orbit.start_updater()
            rig.orbit.set_enabled(True)

    @staticmethod
    def _start_transform(description) -> carla.Transform:
        if description.pattern == "stare":
            aim = StareAim.from_channel(description)
            return carla.Transform(carla.Location(x=aim.x_m, y=aim.y_m, z=aim.z_m),
                                   carla.Rotation(pitch=aim.pitch_deg, yaw=aim.yaw_deg, roll=0.0))
        return carla.Transform(
            carla.Location(x=description.orbit_centre_x_m, y=description.orbit_centre_y_m,
                           z=description.orbit_centre_z_m + description.orbit_altitude_m),
            carla.Rotation(pitch=-90.0, yaw=0.0, roll=0.0))

    @staticmethod
    def _describe(transform: Any) -> str:
        location, rotation = transform.location, transform.rotation
        return (f"({location.x:.1f}, {location.y:.1f}, {location.z:.1f}) m, pitch "
                f"{rotation.pitch:.1f}, yaw {rotation.yaw:.1f}")

    # -- the prewarm --------------------------------------------------------------------------------------
    def _prewarm(self) -> None:
        window = self.effective.window
        session = self.session
        started = float(session.RenderedTimeSeconds)
        while float(session.RenderedTimeSeconds) < window.begin_s - SIM_EPSILON_S:
            self._check_stop()
            if not session.Advance():
                raise _RefusedError("refused_preroll", f"the scenario ended at "
                               f"t={float(session.RenderedTimeSeconds):g}, before the window "
                               f"opened at t={window.begin_s:g}", closed_by="aborted_at_preroll")
            self.monitor.update(self.closeout.snapshot())
        if float(session.RenderedTimeSeconds) > started:
            self.preroll_pace = self.closeout.snapshot()["pacing"]["achieved_factor"]
        findings = self.validator.preroll_pace(self.effective, self.preroll_pace)
        self.findings.extend(findings)
        if findings.refused:
            raise _RefusedError("refused_preroll", self._first_refusal(),
                           closed_by="aborted_at_preroll")
        self._check_stop()

    # -- recording ----------------------------------------------------------------------------------------
    def _record(self) -> None:
        effective = self.effective
        hz = float(effective.value("capture.capture_hz"))
        for rig in self.channels:
            description = effective.channel_description(rig.index)
            rig.directory.mkdir(parents=True, exist_ok=True)
            rig.recorder = rig.world.start_recording(
                rig.camera, str(rig.directory), hz, fov=float(description.fov),
                platform_callsign=rig.sensor_id, run_id=self.session_id,
                depth_camera=rig.depth,
                occlusion_margin_m=float(effective.value("occlusion.margin_m")),
                occlusion_samples=int(effective.value("occlusion.samples")),
                illumination=self.session.Illumination)
            if rig.recorder is None:
                raise _RefusedError("refused_preroll", "native recording is unavailable "
                               "(CarlaNet.Recording)", closed_by="aborted_at_preroll")
            self.termination.add_step(DRAIN_CAPTURE, f"drain recorder {rig.sensor_id}",
                                      rig.world.stop_recording, DRAIN_TIMEOUT_S)
            self.closeout.add_channel(ChannelCapture(rig.sensor_id, rig.directory, rig.recorder))
            self.logger.info("recording %s -> %s", rig.sensor_id, rig.directory)

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
        unadjudicated = len([w for w in self.result.warnings if not w.get("adjudication")])
        gates = self.closeout.gates(snapshot, unadjudicated)
        window = self.effective.window
        epoch = ScenarioEpoch.read(self.effective.epoch_object())
        reached = self.end_reached_s if self.end_reached_s is not None else snapshot["sim_time_s"]
        self.result.produced = {
            "capture_directory": str(self.capture_directory),
            "closed_by": self.result.closed_by or self.termination.closed_by,
            "window": {"name": window.name, "begin_s": window.begin_s,
                       "end_declared_s": window.end_s, "end_source": window.end_source,
                       "end_reached_s": reached,
                       "civil": [epoch.civil_instant_at(window.begin_s),
                                 None if reached is None or not math.isfinite(reached)
                                 else epoch.civil_instant_at(reached)]},
            "channels": snapshot["channels"],
            "gates": gates,
            "session": {**self.session_facts, "last_snapshot": {
                key: snapshot[key] for key in ("pacing", "render", "illumination",
                                               "solar_audit") if key in snapshot}},
            "preroll_achieved_factor": self.preroll_pace,
            "recorder_run_id": self.session_id,
        }
        self.logger.info("\n%s\n", RunCloseoutReport.render(snapshot, gates))

    def _log_session_report(self) -> None:
        """The session's own report, read once it has given everything back -- its admission and
        capacity counts are completed only when it is disposed."""
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
            "resolution_version": 1,
            "outcome": outcome,
            "session_id": self.session_id,
            "check_catalogue": "12_Operator_Control_Surface.md §6.2",
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
        RunResult.write_json(replay, effective.to_run_configuration())
        lock = RunResult.sibling(result_path, "lock")
        RunResult.write_json(lock, {
            "lock_version": 1,
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
