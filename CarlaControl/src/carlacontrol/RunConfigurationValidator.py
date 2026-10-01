"""The checks a capture run's effective configuration faces before anything is acquired.

`12_Operator_Control_Surface.md` §6.2, in the scenario compiler's refuse/warn vocabulary
(`07_Scenario_Authoring.md` §5.2): a refusal stops the launch naming the field, both values and the
candidates; a warning lets it go on and is carried in full. The checks are numbered by
`RunConfigurationCheckCatalogue`, which also says which checks the co-simulation session runs itself
(the loaded world, the SUMO release, the lease, the fast-forward and the sun's read-back), which hold
by construction, and which have nothing in the tree to compare.

* **The offline checks** (`validate_offline`, then `validate_launch`) need no server, no GPU and no
  SUMO, so a
  night's runs can be validated on a laptop (02 D2.2). Where the session has a validator of its own
  for a rule, they call it rather than restate it: the clock ratio is
  `CarlaNet.CoSim.CoSimClock.ForSession`, the illumination object is `IlluminationPolicy.FromJson`,
  and the epoch is `SolarEpoch` through `ScenarioEpoch`. Where the pre-roll's wait for each view
  could not possibly be met, the prewarm is refused here (check 51) rather than there (check 50).
* **The server checks** (`validate_against_server`) read what the rig and the scenario need from
  the server --
  the sun, the camera blueprints' attributes, the vehicle blueprints -- before anything is spawned.
* **Pre-roll** (`preroll_pace`) compares the prewarm's achieved real-time factor with the floor a
  live run declares (check 44).

**What the disk figures rest on.** Check 19 and check 46 express free space in captured seconds from
doc 10's measured PNG size: 2.25 MB per 1280x720 PNG (10 §4.6, read as MiB, the larger of the two
readings, since the figure guards a disk). The truth sidecars are left out: a sidecar grows with the
vehicles in frame, every vehicle SUMO has is rendered, and how many a channel will see is not known
before the run. A PNG's size depends on what is in frame too, so the figure is an estimate and the
refusal says so.
"""
from __future__ import annotations

import json
import os
import shutil
from collections.abc import Callable
from pathlib import Path
from typing import Any

import carlanet  # noqa: F401  -- loads the CarlaNet assemblies the next import names
from CarlaNet.CoSim import CoSimClock, CoSimSessionRefusedException, IlluminationPolicy

from carlacontrol.EffectiveRunConfiguration import (
    EffectiveRunConfiguration,
    WindowResolutionError,
)
from carlacontrol.RunConfigurationFindings import RunConfigurationFindings
from carlacontrol.ScenarioEpoch import ScenarioEpoch, ScenarioEpochRefusedError
from carlacontrol.ScenarioPackage import READABLE_LOCK_VERSIONS
from carlacontrol.SiteProfile import SUMO_SEARCHED_VARIABLES
from carlacontrol.VehicleCatalogue import VehicleCatalogue
from carlacontrol.ViewReadiness import (
    PICTURE_SPAN_TICKS,
    ticks_after_first_ask,
    ticks_to_first_comparison,
    wait_begins_s,
)

MEASURED_PNG_BYTES = 2.25 * 2**20
MEASURED_PNG_PIXELS = 1280 * 720
PNG_BYTES_PER_PIXEL = MEASURED_PNG_BYTES / MEASURED_PNG_PIXELS

RGB_BLUEPRINT = "sensor.camera.rgb"
DEPTH_BLUEPRINT = "sensor.camera.depth"
RGB_ATTRIBUTES = ("image_size_x", "image_size_y", "fov", "sensor_tick", "post_process_profile")
DEPTH_ATTRIBUTES = ("image_size_x", "image_size_y", "fov", "sensor_tick", "max_range")

# Fields a more specific check refuses when no layer supplies them.
_SPECIFIC_ABSENCE = {"scenario.epoch": 12}


class RunConfigurationValidator:
    """Runs the checks of each launch phase against an effective configuration."""

    def __init__(self, disk_usage: Callable[[str], Any] = shutil.disk_usage) -> None:
        self.disk_usage = disk_usage

    # =============================================================================================
    # the offline checks
    # =============================================================================================
    def validate_offline(self, effective: EffectiveRunConfiguration, result_path: Path,
                         capture_directory: Path) -> RunConfigurationFindings:
        """Every phase-0 check that does not need the launch echo."""
        findings = RunConfigurationFindings()
        self._required(effective, result_path, findings)
        self._mode(effective, findings)
        self._lock_version(effective, findings)
        self._world_binding(effective, findings)
        self._catalogue(effective, findings)
        self._site_paths(effective, findings)
        self._environment(effective, findings)
        window_ok = self._window(effective, findings)
        self._clock(effective, findings)
        self._channels(effective, findings)
        if window_ok:
            self._readiness_prewarm(effective, findings)
        self._epoch(effective, findings)
        self._illumination(effective, findings)
        self._pacing(effective, findings)
        self._result_path(effective, result_path, capture_directory, findings)
        if window_ok:
            self._disk(effective, capture_directory, findings)
        return findings

    def validate_launch(self, effective: EffectiveRunConfiguration, echo_values: dict[str, Any],
                        findings: RunConfigurationFindings) -> None:
        """Check 35 against the configuration and the echo, then check 34 over the warnings raised.

        Adds its refusals to `findings`, which holds every phase-0 finding so far.
        """
        self._expectations(effective, echo_values, findings)
        self._adjudications(effective, findings)

    # -- check 2 ----------------------------------------------------------------------------------
    @staticmethod
    def _required(effective: EffectiveRunConfiguration, result_path: Path | None,
                  findings: RunConfigurationFindings) -> None:
        for path in effective.unsupplied():
            check = _SPECIFIC_ABSENCE.get(path)
            if check == 12:
                findings.refuse(12, "scenario.epoch", f"scenario {effective.scenario.describe()} "
                                "declares no epoch. A capture cannot set a sun from simulated "
                                "seconds without one (12 §4.3)")
            elif path == "capture.window":
                names = ", ".join(sorted(effective.scenario.windows)) or "none declared"
                findings.refuse(2, path, "'capture.window' has no value and no default. Supply it "
                                f"with --window, or name one of the scenario's declared windows: "
                                f"{names}")
            elif path == "pacing.min_achieved_factor":
                findings.refuse(2, path, "pacing.mode is wall_clock and pacing.min_achieved_factor "
                                "has no default: state the achieved factor below which the live "
                                "run has failed (12 §7.5 L3)")
            else:
                findings.refuse(2, path, f"'{path}' has no value and no default. Supply it")
        if effective.value("caller") == "unattended" and effective.value("result_path") is None:
            findings.refuse(2, "result_path", "under caller unattended the result path is "
                            "required: give --result, outside the capture root")

    # -- check 4 ----------------------------------------------------------------------------------
    @staticmethod
    def _mode(effective: EffectiveRunConfiguration, findings: RunConfigurationFindings) -> None:
        mode = effective.value("mode")
        if mode != "sumo_driven_playback":
            findings.refuse(4, "mode", f"mode is '{mode}'; run_capture runs sumo_driven_playback "
                            "only. Ambient traffic under the traffic manager is run_SCTMV.py, and "
                            "the two are mutually exclusive in one world (01 D1.7)")

    # -- check 6 ----------------------------------------------------------------------------------
    @staticmethod
    def _lock_version(effective: EffectiveRunConfiguration,
                      findings: RunConfigurationFindings) -> None:
        version = effective.scenario.lock_version
        if version not in READABLE_LOCK_VERSIONS:
            findings.refuse(6, f"scenario {effective.scenario.describe()}",
                            f"lock_version {version!r} is not one this tool reads "
                            f"({', '.join(map(str, READABLE_LOCK_VERSIONS))})")

    # -- check 5 ----------------------------------------------------------------------------------
    @staticmethod
    def _world_binding(effective: EffectiveRunConfiguration,
                       findings: RunConfigurationFindings) -> None:
        lock_world = effective.scenario.world
        manifest = effective.world.manifest
        where = f"scenario {effective.scenario.describe()}"
        pairs = (("network_fingerprint", "NetworkFingerprint", "network fingerprint"),
                 ("opendrive_sha256", "OpenDriveSha256", "OpenDRIVE digest"),
                 ("origin_latitude", "OriginLatitude", "origin latitude"),
                 ("origin_longitude", "OriginLongitude", "origin longitude"))
        for lock_key, manifest_key, label in pairs:
            compiled = lock_world.get(lock_key)
            carried = manifest.get(manifest_key)
            if compiled is None or carried is None:
                findings.refuse(5, where, f"the {label} is not recorded by both the scenario lock "
                                f"({compiled!r}) and world package {effective.world.path.name} "
                                f"({carried!r}); the binding cannot be established")
            elif compiled != carried:
                findings.refuse(5, where, f"was compiled against {label} {compiled}; world package "
                                f"{effective.world.path.name} carries {carried}. Recompile the "
                                "scenario against the world that will render, or name that world's "
                                "package")

    # -- checks 37 and 48 -------------------------------------------------------------------------
    @staticmethod
    def _site_paths(effective: EffectiveRunConfiguration,
                    findings: RunConfigurationFindings) -> None:
        for path in ("paths.scenario_root", "paths.world_package_root", "paths.catalogue",
                     "paths.capture_root", "paths.runs_root", "sumo.home"):
            value = effective.value(path)
            # sumo.home alone may be absent: the session then searches, and check 36 governs that.
            if value == "" or (value is None and path != "sumo.home"):
                findings.refuse(37, path, f"'{path}' resolves to {json.dumps(value)} "
                                f"({effective.resolution(path).provenance}). An absent path is a "
                                "refusal, not a blank")

    @staticmethod
    def _catalogue(effective: EffectiveRunConfiguration,
                   findings: RunConfigurationFindings) -> None:
        path = effective.value("paths.catalogue")
        if not path:
            return
        if not Path(str(path)).is_file():
            findings.refuse(37, "paths.catalogue", f"no catalogue at {path} "
                            f"({effective.resolution('paths.catalogue').provenance})")
            return
        try:
            digest = VehicleCatalogue.load(path).catalogue_digest
        except (OSError, ValueError, KeyError) as failure:
            findings.refuse(37, "paths.catalogue", f"{path} is not a readable catalogue: {failure}")
            return
        compiled = effective.value("scenario.catalogue_digest")
        if digest != compiled:
            findings.refuse(48, "paths.catalogue", f"{path} has catalogue_digest {digest}; scenario "
                            f"{effective.scenario.describe()} was compiled against {compiled}. The "
                            "session would seat bodies of other dimensions than the routes were "
                            "built for")

    # -- check 36 ---------------------------------------------------------------------------------
    @staticmethod
    def _environment(effective: EffectiveRunConfiguration,
                     findings: RunConfigurationFindings) -> None:
        if effective.value("caller") != "unattended":
            return
        searched: list[str] = []
        for path, variable in effective.site.undeclared_environment():
            if effective.resolution(path).layer != "site_profile":
                continue
            if variable in SUMO_SEARCHED_VARIABLES:
                searched.append(variable)
                continue
            findings.refuse(36, path, f"'{path}' resolved from {variable}, which the site profile "
                            "does not declare. An unattended run does not inherit host state it "
                            "was not given (12 §3.10 M2)")
        if searched:
            findings.refuse(36, "sumo.home", f"'sumo.home' names no installation, so the session "
                            f"will search {', then '.join(searched)} for one, which the site "
                            "profile does not declare. An unattended run does not inherit host "
                            "state it was not given (12 §3.10 M2; 09 D9.6): name sumo.home in the "
                            "site profile")

    # -- checks 7 and 8 ---------------------------------------------------------------------------
    @staticmethod
    def _window(effective: EffectiveRunConfiguration,
                findings: RunConfigurationFindings) -> bool:
        if effective.value("capture.window") is None or effective.value("scenario.end_s") is None:
            return False
        try:
            window = effective.window
        except WindowResolutionError as problem:
            findings.refuse(7, "capture.window", str(problem))
            return False
        prewarm = float(effective.value("capture.prewarm_s"))
        if prewarm > window.begin_s:
            findings.warn(8, "capture.prewarm_s", f"prewarm {prewarm:g} s clipped to "
                          f"{window.begin_s:g} s: the window begins at t={window.begin_s:g}")
        return True

    # -- check 9 ----------------------------------------------------------------------------------
    @staticmethod
    def _clock(effective: EffectiveRunConfiguration, findings: RunConfigurationFindings) -> None:
        step = effective.value("scenario.sumo_step_s")
        if step is None:
            return
        try:
            CoSimClock.ForSession(float(step), float(effective.value("capture.world_delta_s")),
                                  float(effective.value("capture.capture_hz")), True)
        except CoSimSessionRefusedException as refusal:
            findings.refuse(9, "capture.world_delta_s", f"clock ratio: {refusal.Message}")

    # -- checks 11 and 47 -------------------------------------------------------------------------
    @staticmethod
    def _channels(effective: EffectiveRunConfiguration,
                  findings: RunConfigurationFindings) -> None:
        count = effective.channel_count
        seen: dict[str, int] = {}
        for index in range(count):
            subject = f"capture.channels[{index}]"
            try:
                description = effective.channel_description(index)
            except ValueError as refusal:
                findings.refuse(47, subject, str(refusal))
                continue
            if effective.value("occlusion.enabled") and description.pattern == "orbit":
                findings.refuse(47, subject, "occlusion is measured against a depth camera held at "
                                "the channel's pose, and an orbit moves its camera with one call "
                                "at a time, so the depth camera would be captured a frame apart "
                                "(SensorRig.set_transform measured 25 of 65 captures lost that "
                                "way). Set occlusion.enabled false for a run with an orbit, or "
                                "make this channel a stare")
            if description.aims_at_rendered_traffic():
                RunConfigurationValidator._traffic_prewarm(effective, subject, findings)
            sensor_id = description.sensor_id
            if count > 1:
                if not sensor_id:
                    findings.refuse(11, subject, f"{count} channels are declared and this one has "
                                    "no sensor_id; every channel needs one when there are several")
                elif sensor_id in seen:
                    findings.refuse(11, subject, f"channels {seen[sensor_id]} and {index} both name "
                                    f"sensor_id '{sensor_id}'")
                else:
                    seen[sensor_id] = index

    @staticmethod
    def _traffic_prewarm(effective: EffectiveRunConfiguration, subject: str,
                         findings: RunConfigurationFindings) -> None:
        """A stare aimed at the rendered traffic measures it on a prewarm frame, so the prewarm has
        to render at least one SUMO step before the window opens."""
        step = effective.value("scenario.sumo_step_s")
        if step is None or effective.value("capture.window") is None:
            return
        try:
            prewarm = effective.prewarm_s
        except WindowResolutionError:
            return
        if prewarm + 1e-9 < float(step):
            findings.refuse(47, subject, f"this stare aims at the rendered traffic, which is "
                            f"measured on a frame the prewarm renders before the window opens; "
                            f"the prewarm is {prewarm:g} s (capture.prewarm_s, clipped to "
                            f"the window's begin) and one SUMO step is {float(step):g} s, so no "
                            "frame would be rendered to measure it on. Give a prewarm of at least "
                            "one SUMO step, or aim the channel at a point or a pose")

    # -- check 51 ---------------------------------------------------------------------------------
    @staticmethod
    def _readiness_prewarm(effective: EffectiveRunConfiguration,
                           findings: RunConfigurationFindings) -> None:
        """Every channel's view is waited on inside the prewarm (03 §9.5.1), from where every camera
        holds the pose the window opens on; a prewarm that leaves too few ticks there for the camera
        to render two frames the picture can be compared on is refused before anything is started."""
        step = effective.value("scenario.sumo_step_s")
        if step is None:
            return
        try:
            descriptions = [effective.channel_description(index)
                            for index in range(effective.channel_count)]
            prewarm = effective.prewarm_s
        except (ValueError, WindowResolutionError):
            return
        step = float(step)
        delta = float(effective.value("capture.world_delta_s"))
        period = round(1.0 / (float(effective.value("capture.capture_hz")) * delta))
        follows = any(description.aims_at_rendered_traffic() for description in descriptions)
        if follows and prewarm + 1e-9 < step:
            return  # check 47 refuses it: there is no step to measure the traffic on
        begin = effective.window.begin_s
        held = begin - wait_begins_s(begin, effective.first_rendered_s, step, delta, follows)
        ticks = ticks_after_first_ask(held, step, delta)
        needed_ticks = ticks_to_first_comparison(period)
        if ticks + 1e-6 >= needed_ticks:
            return
        needed = step + needed_ticks * delta + (step if follows else 0.0)
        findings.refuse(51, "capture.prewarm_s", (
            f"every channel's view is waited on inside the prewarm, and its picture is witnessed "
            f"settled by comparing one of the camera's frames with its frame at least "
            f"{PICTURE_SPAN_TICKS} ticks before it, both rendered after its tiles are first asked "
            f"about, one SUMO step into the wait (03 §9.5.1): the prewarm is {prewarm:g} s "
            f"(capture.prewarm_s, clipped to the window's begin)"
            + (", and the wait begins only where the stare following the rendered traffic stops "
               "to hold its pose, after the step it measures the traffic on" if follows else "")
            + f", which leaves {max(ticks, 0.0):g} ticks against the {needed_ticks} a camera "
            f"rendering every {period} ticks may need for two such frames, so the run would be "
            f"refused at pre-roll. Give a prewarm of at least {needed:g} s"))

    # -- checks 12 and 13 -------------------------------------------------------------------------
    @staticmethod
    def _epoch(effective: EffectiveRunConfiguration, findings: RunConfigurationFindings) -> None:
        epoch = effective.value("scenario.epoch")
        if epoch is None:
            return
        try:
            ScenarioEpoch.read(epoch)
        except ScenarioEpochRefusedError as refusal:
            for _check, problem in refusal.problems:
                findings.refuse(13, "scenario.epoch", f"the window's civil span cannot be "
                                f"computed: {problem}")

    # -- check 15 ---------------------------------------------------------------------------------
    @staticmethod
    def _illumination(effective: EffectiveRunConfiguration,
                      findings: RunConfigurationFindings) -> None:
        if effective.value("solar.policy") is None:
            return
        document = effective.illumination_object()
        try:
            policy = IlluminationPolicy.FromJson(json.dumps(document))
        except CoSimSessionRefusedException as refusal:
            findings.refuse(15, "solar", str(refusal.Message))
            return
        if bool(policy.Advances) and abs(float(policy.Rate) - 1.0) > 1e-12:
            findings.refuse(15, "solar.rate_sun_s_per_sim_s", f"the sun advances at "
                            f"{float(policy.Rate):g} sun-seconds per simulated second "
                            f"({effective.resolution('solar.rate_sun_s_per_sim_s').provenance}). A "
                            "capture run permits only 1.0: any other rate makes recorded solar "
                            "time disagree with the scenario's clock (12 D12.8). Choose another "
                            "policy with --solar, or use the interactive viewer for look "
                            "development")
        if not bool(policy.BindsTheSun):
            findings.warn(15, "solar.policy", "the policy is 'ignore': the sun is left as the world "
                          "holds it, so the run's lighting honours no epoch and depends on whatever "
                          "the last session left")

    # -- check 41 ---------------------------------------------------------------------------------
    @staticmethod
    def _pacing(effective: EffectiveRunConfiguration, findings: RunConfigurationFindings) -> None:
        mode = effective.value("pacing.mode")
        factor = effective.value("pacing.real_time_factor")
        floor = effective.value("pacing.min_achieved_factor")
        if mode == "as_available":
            for path, value in (("pacing.real_time_factor", factor),
                                ("pacing.min_achieved_factor", floor)):
                if value is not None:
                    findings.refuse(41, path, f"pacing.mode is 'as_available'; '{path}' is not "
                                    f"valid in that mode ({effective.resolution(path).provenance})")
            return
        if factor is not None and floor is not None and floor > factor:
            findings.refuse(41, "pacing.min_achieved_factor", f"pacing.min_achieved_factor "
                            f"{floor:g} exceeds pacing.real_time_factor {factor:g}")

    # -- check 40 ---------------------------------------------------------------------------------
    @staticmethod
    def _result_path(effective: EffectiveRunConfiguration, result_path: Path,
                     capture_directory: Path, findings: RunConfigurationFindings) -> None:
        capture_root = effective.value("paths.capture_root")
        if not capture_root:
            return
        resolved = result_path.resolve()
        root = Path(str(capture_root)).resolve()
        if resolved == root or root in resolved.parents or capture_directory.resolve() in \
                resolved.parents:
            findings.refuse(40, "result_path", f"result_path '{result_path}' is inside the capture "
                            f"root {root}. The result must survive a refusal that produces no "
                            "corpus, and is not a corpus artifact")
            return
        existing = RunConfigurationValidator.nearest_existing(resolved.parent)
        if existing is None or not os.access(existing, os.W_OK):
            findings.refuse(40, "result_path", f"result_path '{result_path}' cannot be written: "
                            f"{existing or 'no ancestor of it exists'} is not writable")

    # -- checks 19 and 46 at launch ---------------------------------------------------------------
    def _disk(self, effective: EffectiveRunConfiguration, capture_directory: Path,
              findings: RunConfigurationFindings) -> None:
        free, headroom_s = self.headroom(effective, capture_directory)
        if headroom_s is None:
            return
        window = effective.window
        floor_s = float(effective.value("write_headroom_floor_s"))
        available_s = headroom_s - floor_s
        if window.length_s <= available_s:
            return
        rate = self.bytes_per_captured_second(effective)
        message = (f"the window needs ~{window.length_s * rate / 1e9:.1f} GB at "
                   f"{effective.value('capture.capture_hz'):g} Hz x {effective.channel_count} "
                   f"channel(s); {free / 1e9:.1f} GB free under {capture_directory} is "
                   f"{headroom_s / 3600:.2f} h of capture, and the floor keeps {floor_s:g} s of it. "
                   "Estimated from doc 10's measured PNG size; the truth sidecars, which grow with "
                   "the traffic in frame, are not counted")
        if window.end_source == "the scenario's own end: the window declares none":
            findings.warn(19, "capture.window", message + ". This window declares no end")
        else:
            findings.refuse(19, "capture.window", message)

    def headroom(self, effective: EffectiveRunConfiguration,
                 directory: Path) -> tuple[float, float | None]:
        """Free bytes where captures go, and how many captured seconds they hold (None if no rate)."""
        existing = self.nearest_existing(Path(directory).resolve())
        if existing is None:
            return 0.0, None
        free = float(self.disk_usage(str(existing)).free)
        rate = self.bytes_per_captured_second(effective)
        return free, (free / rate if rate > 0 else None)

    @staticmethod
    def bytes_per_capture(effective: EffectiveRunConfiguration, index: int) -> float:
        """One capture's PNG, from doc 10's measured size per pixel. Its truth sidecar is not
        counted: it grows with the vehicles in frame, which no figure known before the run bounds."""
        values = effective.channel_values(index)
        return PNG_BYTES_PER_PIXEL * float(values["width"]) * float(values["height"])

    @classmethod
    def bytes_per_captured_second(cls, effective: EffectiveRunConfiguration) -> float:
        hz = float(effective.value("capture.capture_hz") or 0.0)
        return hz * sum(cls.bytes_per_capture(effective, i) for i in range(effective.channel_count))

    @staticmethod
    def nearest_existing(path: Path) -> Path | None:
        for candidate in (path, *path.parents):
            if candidate.exists():
                return candidate
        return None

    # -- check 35 ---------------------------------------------------------------------------------
    @staticmethod
    def _expectations(effective: EffectiveRunConfiguration, echo_values: dict[str, Any],
                      findings: RunConfigurationFindings) -> None:
        expectations = effective.value("expect") or {}
        for path, expected in expectations.items():
            if path in echo_values:
                actual = echo_values[path]
            elif path in effective.paths:
                actual = effective.value(path)
            else:
                known = sorted([*echo_values, *effective.paths])
                near = [k for k in known if k.split(".")[-1] == path.split(".")[-1]][:3]
                hint = f"; did you mean {' or '.join(near)}?" if near else ""
                findings.refuse(35, f"expect.{path}", f"'{path}' is neither a configuration field "
                                f"nor a launch-echo value{hint}")
                continue
            held, statement = RunConfigurationValidator._holds(expected, actual)
            if not held:
                findings.refuse(35, f"expect.{path}", f"expect.{path} was {statement}; the "
                                f"configuration resolved {json.dumps(actual)}. The scenario is "
                                f"{effective.scenario.describe()} and the window is "
                                f"{effective.value('capture.window')}")

    @staticmethod
    def _holds(expected: Any, actual: Any) -> tuple[bool, str]:
        if isinstance(expected, dict) and set(expected) <= {"at_most", "at_least"} and expected:
            if not isinstance(actual, int | float) or isinstance(actual, bool):
                return False, f"a bound {json.dumps(expected)} on a number"
            held = all((actual <= bound) if key == "at_most" else (actual >= bound)
                       for key, bound in expected.items())
            return held, json.dumps(expected)
        if isinstance(expected, int | float) and isinstance(actual, int | float) \
                and not isinstance(expected, bool) and not isinstance(actual, bool):
            return abs(float(expected) - float(actual)) <= 1e-9 * max(1.0, abs(float(actual))), \
                json.dumps(expected)
        return expected == actual, json.dumps(expected)

    # -- check 34 ---------------------------------------------------------------------------------
    @staticmethod
    def _adjudications(effective: EffectiveRunConfiguration,
                       findings: RunConfigurationFindings) -> None:
        adjudications = effective.value("on_warning") or {}
        for warning in list(findings.warnings):
            code = RunConfigurationFindings.warning_code(warning)
            decision = adjudications.get(code)
            if decision == "refuse":
                findings.refuse(34, f"on_warning.{code}", f"warning '{code}' was raised and "
                                f"on_warning.{code} is 'refuse': {warning.message}")
            elif decision is None and effective.value("caller") == "unattended":
                findings.refuse(34, f"on_warning.{code}", f"warning '{code}' was raised and the "
                                f"caller is unattended. Set on_warning.{code} to 'proceed' -- which "
                                "is recorded against this configuration -- or change what raised "
                                f"it. An unattended run does not proceed past an unadjudicated "
                                f"warning (12 §6.4): {warning.message}")

    # =============================================================================================
    # the server checks
    # =============================================================================================
    def validate_against_server(self, effective: EffectiveRunConfiguration,
                                world: Any) -> RunConfigurationFindings:
        """Checks 23, 24 and 25 against a connected world; nothing is spawned or written."""
        findings = RunConfigurationFindings()
        self._sun(effective, world, findings)
        try:
            library = world.get_blueprint_library()
        except Exception as failure:
            findings.refuse(24, "blueprints", f"could not read the server's blueprint library: "
                            f"{failure!r}")
            return findings
        self._rig_attributes(effective, library, findings)
        self._vehicle_blueprints(effective, library, findings)
        return findings

    @staticmethod
    def _sun(effective: EffectiveRunConfiguration, world: Any,
             findings: RunConfigurationFindings) -> None:
        binds = effective.value("solar.policy") != "ignore"
        required = effective.value("solar.require_sun") is not False
        if not (binds or required):
            return
        if world.get_solar_state() is None:
            findings.refuse(23, "solar", "the world has no CesiumSunSky, so a capture cannot set its "
                            "sun" + ("" if binds else ", and the run requires one"))

    @staticmethod
    def _rig_attributes(effective: EffectiveRunConfiguration, library: Any,
                        findings: RunConfigurationFindings) -> None:
        wanted = [(RGB_BLUEPRINT, RGB_ATTRIBUTES)]
        stares = any(effective.channel_values(i)["pattern"] == "stare"
                     for i in range(effective.channel_count))
        if effective.value("occlusion.enabled") and stares:
            wanted.append((DEPTH_BLUEPRINT, DEPTH_ATTRIBUTES))
        for blueprint_id, attributes in wanted:
            try:
                blueprint = library.find(blueprint_id)
            except (KeyError, IndexError):
                findings.refuse(24, blueprint_id, f"the server has no {blueprint_id} blueprint")
                continue
            for attribute in attributes:
                if not blueprint.has_attribute(attribute):
                    findings.refuse(24, blueprint_id, f"{blueprint_id} has no attribute "
                                    f"'{attribute}' on this build, and the rig sets it")

    @staticmethod
    def _vehicle_blueprints(effective: EffectiveRunConfiguration, library: Any,
                            findings: RunConfigurationFindings) -> None:
        present = {blueprint.id for blueprint in library}
        try:
            bound = effective.scenario.vtype_blueprints()
        except (OSError, KeyError) as failure:
            findings.refuse(25, "scenario routes", f"cannot read the scenario's vehicle types: "
                            f"{failure}")
            return
        for vtype, blueprint in sorted(bound.items()):
            if blueprint not in present:
                findings.refuse(25, f"vType {vtype}", f"binds blueprint {blueprint}, which this "
                                "server does not have; the vehicle would be simulated and never "
                                "rendered")

    # =============================================================================================
    # pre-roll
    # =============================================================================================
    @staticmethod
    def preroll_pace(effective: EffectiveRunConfiguration,
                     achieved: float | None) -> RunConfigurationFindings:
        """Check 44: under wall_clock, the prewarm held the declared floor."""
        findings = RunConfigurationFindings()
        if effective.value("pacing.mode") != "wall_clock" or achieved is None:
            return findings
        floor = float(effective.value("pacing.min_achieved_factor"))
        if achieved < floor:
            findings.refuse(44, "pacing.min_achieved_factor", f"the prewarm held {achieved:.2f} of "
                            f"real time against a requested "
                            f"{float(effective.value('pacing.real_time_factor')):g} and a floor of "
                            f"{floor:g}. A live exercise that cannot hold its rate does not open "
                            "its window")
        return findings
