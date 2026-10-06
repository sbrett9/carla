"""The facts a capture run has established so far, and the gate records read from them.

`12_Operator_Control_Surface.md` §7.2, D12.16. Computed at any instant from the objects that hold the
facts -- the co-simulation session (`SumoDriveSession`: its clock, its pacing, the sun's audit, the
newest frame's illumination declaration, its latest admission pass, and the compile lock and
teleporting checks it made before SUMO started), the window's admission passes (`WindowAdmissions`),
the wait for each channel's view before the window opened (`ViewReadinessGate`) and each channel's
recorder (`FrameRecorder`: captures written, captures dropped, stills dropped for want of their own
frame's truth, illumination
pairing, captures written without a solar block, render-set pairing, supervision pairing, occlusion
pairing, where each capture's pose came from, the vehicles it marked beyond the draw distance, and the
captures whose vehicles in the picture went without their lights or pose source) -- never at
the end only, so a run stopped at minute nine has everything it knew at minute nine. `snapshot()` is
the one computation: the live monitor renders it (D12.14), the loud conditions are read from it, and
the run result carries the last one taken. Nothing here measures anything of its own.

**Gate records are observations, never a verdict.** Each names what it observed, the threshold it
compared against, the comparison, and whether it was met; nothing sums them. A gate whose input the
tree does not publish is recorded as `skipped` with the reason, so *not measured* never reads as
*passed*:

| gate | observed | status in the tree |
|---|---|---|
| `capture.recorder_dropped` | `FrameRecorder.Dropped` per channel, threshold 0 (10 D10.7) | measured |
| `capture.frame_unpaired` | stills dropped because the client held no truth of their own frame when the image arrived -- a still is written with its own frame's truth or not at all -- threshold 0 (06 §8.2) | measured; skipped from a recorder built before it counted them |
| `capture.illumination_unpaired` | captures written without their frame's illumination declaration, threshold 0 | measured |
| `capture.solar_block_missing` | captures written without a solar block -- no `_solar`, no `carla:solar`, so no recorded sun and no illumination band -- threshold 0 (11 §8.4) | measured |
| `capture.render_set_unpaired` | captures written with no vehicle list because their frame's render set was no longer held, threshold 0 | measured |
| `capture.supervision_unpaired` | captures written with `supervision="unknown"` -- a plan was in force and the supervision of the capture's own frame was not to be had -- threshold 0 (06 §8.2) | measured; skipped from a recorder built before it counted them |
| `capture.lights_unknown` | captures written with `lights="unknown"` -- a vehicle in the picture went without its lights because the snapshot of the capture's own frame did not carry them, as from a server built before it did -- threshold 0 (08 §5.1) | measured; skipped from a recorder built before it counted them |
| `capture.pose_source_unknown` | captures written with `pose_source="unknown"` -- a drawn SUMO vehicle in the picture went without its pose source because the snapshot of the capture's own frame did not carry one -- threshold 0 (08 §5.1) | measured; skipped from a recorder built before it counted them |
| `capture.sensor_pose_header_disagreed` | captures whose image header placed the camera elsewhere than their own frame's snapshot, threshold 0 | measured |
| `capture.depth_pose_header_disagreed` | the same for the depth captures occlusion is measured against, threshold 0 | measured where the channel has a depth camera |
| `clock.ratio_recorded` | whether the session's achieved real-time factor exists | measured |
| `pacing.achieved_factor` | under `wall_clock`, the achieved factor against `min_achieved_factor` | measured |
| `bridge.position_divergence` | the largest distance between a pose the bridge commanded and the one the world applied to the body on the same tick, metres, over the whole run, against `bridge.position_divergence_limit_m` (06 §4.3) | measured where a vehicle-tick was compared; skipped where none was, stating the vehicle-ticks nothing read back |
| `bridge.velocity_divergence` | the largest difference between a velocity the bridge commanded and the one the world reported for the body, metres per second, against `bridge.velocity_divergence_limit_m_per_s` | the same |
| `solar.applied_equals_confirmed` | the audit's worst angle between the world's sun and the declared one, against its tolerance | measured where the policy binds the sun |
| `launch.warnings_adjudicated` | warnings raised with no adjudication, threshold 0 | measured |
| `capture.captured_minus_written` | -- | skipped: the recorder counts no capture accepted into its queue |
| `radiometry.profile_digest_present` | -- | skipped: nothing reads back the profile a camera loaded |
| `supervision.manifest_closing_record` | whether the run manifest's last complete row is its terminal row, `manifest_closed` (04 §12.7, D4.36) | measured where the run writes a manifest |

**One loud condition interrupts every run** (D12.15): a recorder's `Dropped` becoming non-zero; a live
run adds the achieved factor falling below its floor. With no render-set limit -- the default -- a
participant is always drawn, because every vehicle SUMO has is drawn. Under an optional limit a
vehicle outside it is not in CARLA or the truth; the snapshot states the limit and counts what it left
out, and nothing interrupts for it, since the operator chose it.
"""
from __future__ import annotations

import json
import time
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from carlacontrol.EffectiveRunConfiguration import EffectiveRunConfiguration
from carlacontrol.ViewReadiness import describe_view
from carlacontrol.WindowAdmissions import WindowAdmissions

SKIPPED = {
    "capture.captured_minus_written": ("captures accepted but not written",
                                       "the recorder counts no capture accepted into its encode "
                                       "queue, only captures written and dropped"),
    "radiometry.profile_digest_present": ("the loaded post-process profile is digested",
                                          "nothing reads back which profile a camera loaded"),
}
MANIFEST_CLOSING_RECORD = "supervision.manifest_closing_record"
MANIFEST_CLOSED_ROW = "manifest_closed"
POSITION_DIVERGENCE = "bridge.position_divergence"
VELOCITY_DIVERGENCE = "bridge.velocity_divergence"
POSITION_DIVERGENCE_LIMIT = "bridge.position_divergence_limit_m"
VELOCITY_DIVERGENCE_LIMIT = "bridge.velocity_divergence_limit_m_per_s"
# The first read of a manifest's end: enough for its last complete row, unless that row lists the open
# intervals of a large plan, when the read grows.
MANIFEST_TAIL_BYTES = 65536
LOUD_RECORDER_DROPPED = "recorder_dropped"
LOUD_PACE_BELOW_FLOOR = "pace_below_floor"


@dataclass
class ChannelCapture:
    """One channel as the closeout reads it: its name, where it writes, and its recorder."""

    sensor_id: str
    directory: Path
    recorder: Any


class RunCloseoutReport:
    """Snapshots of a run's facts, the gate records over them, and their rendering."""

    def __init__(self, effective: EffectiveRunConfiguration,
                 clock: Callable[[], float] = time.monotonic) -> None:
        self.effective = effective
        self.clock = clock
        self.session: Any = None
        self.admissions: WindowAdmissions | None = None
        self.scenario_checks: dict | None = None
        self.channels: list[ChannelCapture] = []
        self.started_at: float | None = None
        self.readiness: Any = None
        self.manifest: Path | None = None

    def attach(self, session: Any, admissions: WindowAdmissions | None = None) -> None:
        """Read from `session` from now on, and from the window's admission passes where given.

        The compile lock and teleporting checks are read here, once: the session made both before
        SUMO started, and neither changes while it runs.
        """
        self.session = session
        self.admissions = admissions
        self.scenario_checks = self.scenario_checks_of(session.Report)
        self.started_at = self.clock()

    @staticmethod
    def scenario_checks_of(report: Any) -> dict:
        """The session's compile lock and teleporting checks, as a run record states them."""
        lock = report.CompileLock
        teleporting = report.Teleporting
        return {
            "compile_lock": {"compiled": bool(lock.Compiled),
                             "lock_path": str(lock.ExpectedLockPath),
                             "statement": str(lock),
                             "routed_by": str(lock.RoutedByText),
                             "compiled_for": str(lock.WorldText),
                             "dry_run": str(lock.DryRunText),
                             "skipped_dry_run_accepted": bool(lock.SkippedDryRunAccepted)},
            "teleporting": {"enabled": bool(teleporting.Enabled),
                            "accepted": bool(teleporting.Accepted),
                            "seconds": float(teleporting.Seconds),
                            "declared": None if teleporting.Declared is None
                            else str(teleporting.Declared),
                            "statement": str(teleporting)},
        }

    def attach_manifest(self, path: str | Path) -> None:
        """Read the run manifest's closing record from `path`, the manifest the session writes."""
        self.manifest = Path(path)

    @staticmethod
    def last_manifest_row(path: Path) -> str | None:
        """The `row` of a manifest's last complete line -- one that ends in a line break -- or None
        where the file holds no complete line or does not exist. Read from the file's end, so a long
        manifest costs no more than a short one; the read grows until it holds the whole of that line,
        since the terminal row lists the intervals still open and grows with the plan."""
        try:
            with open(path, "rb") as file:
                file.seek(0, 2)
                size = file.tell()
                span = MANIFEST_TAIL_BYTES
                while True:
                    begin = max(0, size - span)
                    file.seek(begin)
                    tail = file.read()
                    end = tail.rfind(b"\n")
                    # Whole once a line break comes before it in what was read, or the read reached the
                    # file's start.
                    if begin == 0 or (end >= 0 and tail.rfind(b"\n", 0, end) >= 0):
                        break
                    span *= 2
        except OSError:
            return None
        if end < 0:
            return None
        start = tail.rfind(b"\n", 0, end) + 1
        try:
            row = json.loads(tail[start:end].decode("utf-8"))
        except (ValueError, UnicodeDecodeError):
            return None
        return row.get("row") if isinstance(row, dict) else None

    def add_channel(self, channel: ChannelCapture) -> None:
        self.channels.append(channel)

    def attach_readiness(self, readiness: Any) -> None:
        """Read each channel's wait for its view from `readiness` (a `ViewReadinessGate`)."""
        self.readiness = readiness

    # -- the one computation ----------------------------------------------------------------------------

    def snapshot(self) -> dict:
        """What the run has established at this instant."""
        window = self.effective.window
        snapshot: dict = {
            "scenario": self.effective.scenario.describe(),
            "window_name": window.name or "explicit",
            "window": {"begin_s": window.begin_s, "end_s": window.end_s},
            "sim_time_s": None,
            "illumination": None,
            "pacing": None,
            "render": None,
            "admission": None,
            "admissions": None if self.admissions is None else self.admissions.to_dict(),
            "scenario_checks": self.scenario_checks,
            "channels": [self._channel(channel) for channel in self.channels],
            "readiness": None if self.readiness is None else self.readiness.to_dict(),
            "wall_elapsed_s": None if self.started_at is None else self.clock() - self.started_at,
        }
        session = self.session
        if session is None:
            return snapshot
        now = float(session.RenderedTimeSeconds)
        snapshot["sim_time_s"] = now
        length = window.end_s - window.begin_s
        snapshot["window"]["progress"] = min(1.0, max(0.0, (now - window.begin_s) / length)) \
            if length > 0 else None
        snapshot["illumination"] = self._illumination(session)
        pacing = session.Report.Pacing
        snapshot["pacing"] = {
            "paced": bool(pacing.Paced), "declared_factor": float(pacing.DeclaredFactor),
            "achieved_factor": self._number(pacing.AchievedFactor),
            "last_window_factor": self._number(pacing.LastWindowFactor),
            "worst_window_factor": self._number(pacing.WorstWindowFactor),
            "behind_schedule_s": float(pacing.BehindScheduleSeconds),
            "completed_windows": int(pacing.CompletedWindows)}
        report = session.Report
        snapshot["render"] = {"rendered_now": int(session.RenderedVehicleIds.Count),
                              "ticks": int(report.Ticks), "sumo_steps": int(report.SumoSteps),
                              "poses_computed": int(report.PosesComputed),
                              "batch_failures": int(report.BatchFailures),
                              "draw_distance_m": self._number(report.DrawDistanceMetres),
                              "draw_distance_in_force_m": self._number(session.DrawDistanceMetres),
                              "draw_distance_refused": None if report.DrawDistanceRefused is None
                              else str(report.DrawDistanceRefused),
                              # Why the server refused the session's pose source, where it did -- a server
                              # built before it carried one -- which the run goes on without.
                              "pose_source_refused": self._refusal(report, "PoseSourceRefused"),
                              # A server built before the jump state: its words, and the jumping bodies it
                              # was sent as sumo, the one name it has for them, which the captures then say.
                              "pose_source_without_jump": self._refusal(report, "PoseSourceWithoutJump"),
                              "pose_source_jumps_named_sumo": int(getattr(report, "PoseSourceJumpsNamedSumo", 0)),
                              "render_set": str(report.RenderSetPolicy),
                              "render_set_limits": bool(report.RenderSetLimits),
                              "vehicle_passes_outside_the_policy":
                                  int(report.VehiclePassesOutsideThePolicy),
                              "capacity_declines": int(report.CapacityDeclines),
                              "releases": {str(reason): int(report.Releases[reason])
                                           for reason in report.Releases.Keys}}
        snapshot["admission"] = WindowAdmissions.describe(report.LastAdmissionPass)
        audit = session.SunAudit
        snapshot["solar_audit"] = None if audit is None else {
            "worst_angle_deg": self._number(report.WorstSolarResidualDegrees),
            "worst_clock_s": self._number(report.WorstSolarClockResidualSeconds),
            "tolerance_deg": float(audit.ToleranceDegrees),
            "tolerance_s": float(audit.ToleranceSeconds),
            "audited_ticks": int(audit.AuditedTicks)}
        snapshot["divergence"] = self._divergence(report)
        return snapshot

    @staticmethod
    def _number(value: Any) -> float | None:
        return None if value is None else float(value)

    @staticmethod
    def _refusal(report: Any, name: str) -> str | None:
        """A server's refusal the report names, in its words; None where it refused nothing, or the report
        was built before it recorded that refusal."""
        refusal = getattr(report, name, None)
        return None if refusal is None else str(refusal)

    @classmethod
    def _divergence(cls, report: Any) -> dict:
        """How far the world departed from what the bridge commanded, over the run so far: the figures
        the session's report accumulates from its comparison of every pose and velocity written against
        what the world reported for the body on the same tick (06 §4.3), as the run manifest's closing
        row writes them. The worst position and the worst velocity each name the SUMO vehicle, the
        instant on TraCI's clock, the tick and the body they were measured on, or None where nothing
        was compared."""
        return {"samples": int(report.DivergenceSamples),
                "vehicle_ticks_with_no_read_back": int(report.VehicleTicksWithNoReadBack),
                "worst_position_m": float(report.WorstPositionDivergenceMetres),
                "mean_position_m": float(report.MeanPositionDivergenceMetres),
                "worst_yaw_deg": float(report.WorstYawDivergenceDegrees),
                "worst_pitch_deg": float(report.WorstPitchDivergenceDegrees),
                "worst_roll_deg": float(report.WorstRollDivergenceDegrees),
                "worst_velocity_m_per_s": float(report.WorstVelocityDivergenceMetresPerSecond),
                "mean_velocity_m_per_s": float(report.MeanVelocityDivergenceMetresPerSecond),
                "mean_commanded_speed_m_per_s": float(report.MeanCommandedSpeedMetresPerSecond),
                "worst_position_on": cls._worst_on(report.WorstDivergence),
                "worst_velocity_on": cls._worst_on(report.WorstVelocityDivergence)}

    @staticmethod
    def _worst_on(sample: Any) -> dict | None:
        if sample is None:
            return None
        return {"sumo_id": str(sample.VehicleId),
                "sim_time_s": round(float(sample.SimulatedTimeSeconds), 6),
                "tick": int(sample.TickIndex), "actor_id": int(sample.Actor)}

    @classmethod
    def _illumination(cls, session: Any) -> dict | None:
        source = session.Illumination
        frame = source.NewestFrame
        if frame is None:
            return None
        found, declaration = source.TryGetDeclaration(frame, None)
        if not found or declaration is None:
            return None
        return {"frame": int(frame), "policy": str(declaration.Policy),
                "declared_civil": None if declaration.DeclaredCivil is None
                else str(declaration.DeclaredCivil),
                "sun_declared": None if declaration.SunDeclared is None
                else str(declaration.SunDeclared),
                "sun_elevation_deg": cls._number(declaration.SunCorrectedElevationDeclaredDegrees),
                "residual_deg": cls._number(declaration.ResidualDegrees)}

    @staticmethod
    def _channel(channel: ChannelCapture) -> dict:
        recorder = channel.recorder
        entry = {"sensor_id": channel.sensor_id, "directory": str(channel.directory),
                 "captured": None, "written": None, "recorder_dropped": None, "frame_unpaired": None,
                 "illumination_paired": None, "illumination_unpaired": None,
                 "solar_block_missing": None, "render_set_paired": None, "render_set_unpaired": None,
                 "supervision_paired": None, "supervision_unpaired": None,
                 "lights_unknown": None, "pose_source_unknown": None,
                 "occlusion_measured": None, "occlusion_unmatched": None,
                 "sensor_pose_from_snapshot": None, "sensor_pose_header_disagreed": None,
                 "sensor_pose_from_header": None,
                 "depth_pose_header_disagreed": None, "depth_pose_from_header": None,
                 "draw_distance_captures": None, "vehicles_beyond_draw_distance": None,
                 "vehicles_partly_beyond_draw_distance": None}
        if recorder is None:
            return entry
        entry.update({"written": int(recorder.Saved), "recorder_dropped": int(recorder.Dropped),
                      "illumination_paired": int(recorder.IlluminationPaired),
                      "illumination_unpaired": int(recorder.IlluminationUnpaired),
                      "solar_block_missing": int(recorder.SolarBlockMissing),
                      "render_set_paired": int(recorder.RenderSetPaired),
                      "render_set_unpaired": int(recorder.RenderSetUnpaired),
                      "occlusion_measured": int(recorder.OcclusionMeasured),
                      "occlusion_unmatched": int(recorder.OcclusionUnmatched),
                      "draw_distance_captures": int(recorder.DrawDistanceCaptures),
                      "vehicles_beyond_draw_distance": int(recorder.VehiclesBeyondDrawDistance),
                      "vehicles_partly_beyond_draw_distance":
                          int(recorder.VehiclesPartlyBeyondDrawDistance)})
        # Counted by a recorder built with the sidecar's supervision; one built before it carries none,
        # and its gate is skipped rather than read as met.
        if hasattr(recorder, "SupervisionUnpaired"):
            entry.update({"supervision_paired": int(recorder.SupervisionPaired),
                          "supervision_unpaired": int(recorder.SupervisionUnpaired)})
        # Counted by a recorder that writes a still with its own frame's truth or not at all; one built
        # before it wrote such stills beside a neighbouring frame's truth and counted nothing here.
        if hasattr(recorder, "FrameUnpaired"):
            entry["frame_unpaired"] = int(recorder.FrameUnpaired)
        # Counted by a recorder that writes each vehicle's lights and pose source from its frame's snapshot;
        # one built before it carries neither, and its gates are skipped rather than read as met.
        if hasattr(recorder, "LightsUnknown"):
            entry.update({"lights_unknown": int(recorder.LightsUnknown),
                          "pose_source_unknown": int(recorder.PoseSourceUnknown)})
        if recorder.ChecksSensorPose:
            entry.update({"sensor_pose_from_snapshot": int(recorder.SensorPoseFromSnapshot),
                          "sensor_pose_header_disagreed": int(recorder.SensorPoseHeaderDisagreed),
                          "sensor_pose_from_header": int(recorder.SensorPoseFromHeader)})
        if recorder.ChecksDepthPose:
            entry.update({
                "depth_pose_header_disagreed": int(recorder.OcclusionDepthPoseHeaderDisagreed),
                "depth_pose_from_header": int(recorder.OcclusionDepthPoseFromHeader)})
        return entry

    # -- read from the snapshot ---------------------------------------------------------------------

    def loud_conditions(self, snapshot: dict) -> list[tuple[str, str]]:
        """(condition, message) for every loud condition the snapshot shows."""
        loud = []
        for channel in snapshot["channels"]:
            if channel["recorder_dropped"]:
                loud.append((LOUD_RECORDER_DROPPED,
                             f"channel {channel['sensor_id']} has dropped "
                             f"{channel['recorder_dropped']} capture(s): the corpus has a hole the "
                             "coverage will not show (10 D10.7)"))
        pacing = snapshot["pacing"]
        floor = self.effective.value("pacing.min_achieved_factor")
        if pacing and self.effective.value("pacing.mode") == "wall_clock" and floor is not None \
                and pacing["last_window_factor"] is not None \
                and pacing["last_window_factor"] < float(floor):
            loud.append((LOUD_PACE_BELOW_FLOOR,
                         f"the achieved factor over the last pacing window was "
                         f"{pacing['last_window_factor']:.2f} against a floor of {float(floor):g}"))
        return loud

    def gates(self, snapshot: dict, unadjudicated_warnings: int) -> list[dict]:
        """The gate records as of this snapshot: observations, each with its threshold."""
        gates = []
        for channel in snapshot["channels"]:
            gates.append(self._gate(f"capture.recorder_dropped[{channel['sensor_id']}]",
                                    "captures the recorder's queue had no room for", "10 D10.7",
                                    channel["recorder_dropped"], 0, "equals"))
            # A still the recorder did not write because the client held no truth of its own frame when
            # the image arrived: a still is written with its own frame's truth or not at all, so each is a
            # missing still and its missing sidecar, which the collection has to know about.
            unpaired_gate = f"capture.frame_unpaired[{channel['sensor_id']}]"
            unpaired_name = ("stills dropped because the client held no truth of their own frame when "
                             "the image arrived")
            if channel["frame_unpaired"] is None:
                gates.append(self._skipped(unpaired_gate, unpaired_name,
                                           "the recorder was built before it dropped such stills"))
            else:
                gates.append(self._gate(unpaired_gate, unpaired_name, "06 §8.2",
                                        channel["frame_unpaired"], 0, "equals"))
            gates.append(self._gate(f"capture.illumination_unpaired[{channel['sensor_id']}]",
                                    "captures written without their frame's illumination "
                                    "declaration", "12 §7.2", channel["illumination_unpaired"], 0,
                                    "equals"))
            # A capture whose snapshot carried no sun: written with no _solar and no carla:solar, so
            # a still whose illumination, and band, nothing records, which a corpus can neither
            # stratify nor replay.
            gates.append(self._gate(f"capture.solar_block_missing[{channel['sensor_id']}]",
                                    "captures written without a solar block, so with no recorded "
                                    "sun and no illumination band", "11 §8.4",
                                    channel["solar_block_missing"], 0, "equals"))
            # A capture listed with no vehicles because its frame's render set had aged out: truth
            # missing from a still that shows vehicles, which a corpus has to know about.
            gates.append(self._gate(f"capture.render_set_unpaired[{channel['sensor_id']}]",
                                    "captures written with no vehicle list because their frame's "
                                    "render set was no longer held", "06 §8.2",
                                    channel["render_set_unpaired"], 0, "equals"))
            # A capture whose frame a plan was in force on, written with its supervision unknown because
            # the snapshot of its own frame was not to be had: a still whose vehicles' labels are missing,
            # never a neighbour's, which a corpus has to know about.
            supervision_gate = f"capture.supervision_unpaired[{channel['sensor_id']}]"
            supervision_name = ("captures written with their supervision unknown because their own "
                                "frame's was not to be had")
            if channel["supervision_unpaired"] is None:
                gates.append(self._skipped(supervision_gate, supervision_name,
                                           "the recorder was built before it counted supervision"))
            else:
                gates.append(self._gate(supervision_gate, supervision_name, "06 §8.2",
                                        channel["supervision_unpaired"], 0, "equals"))
            # A capture whose frame's snapshot did not carry what a vehicle in the picture shows -- its
            # lights, or where a SUMO vehicle's drawn pose came from -- written without it rather than with
            # a guess: a server built before it carried them, or one that refused the session's pose source.
            for field, name in (("lights_unknown", "captures written with a vehicle in the picture whose "
                                                   "lights their own frame's snapshot did not carry"),
                                ("pose_source_unknown", "captures written with a drawn SUMO vehicle in the "
                                                        "picture whose pose source their own frame's "
                                                        "snapshot did not carry")):
                field_gate = f"capture.{field}[{channel['sensor_id']}]"
                if channel[field] is None:
                    gates.append(self._skipped(field_gate, name,
                                               "the recorder was built before it wrote lights and pose sources"))
                else:
                    gates.append(self._gate(field_gate, name, "08 §5.1", channel[field], 0, "equals"))
            # A capture whose image header placed the camera somewhere the snapshot of its own frame
            # did not. The snapshot's pose is the one written, so the still is placed right, but the
            # server stamped the header after the frame, and a corpus has to know it was.
            gates.append(self._gate(f"capture.sensor_pose_header_disagreed[{channel['sensor_id']}]",
                                    "captures whose image header disagreed with the camera's pose "
                                    "in their own frame's snapshot", "12 §9.6",
                                    channel["sensor_pose_header_disagreed"], 0, "equals"))
            depth_gate = f"capture.depth_pose_header_disagreed[{channel['sensor_id']}]"
            depth_name = ("depth captures whose header disagreed with the depth camera's pose in "
                          "their own frame's snapshot")
            if channel["depth_pose_header_disagreed"] is None:
                gates.append(self._skipped(depth_gate, depth_name,
                                           "the channel measures occlusion against no depth camera"))
            else:
                gates.append(self._gate(depth_gate, depth_name, "12 §9.6",
                                        channel["depth_pose_header_disagreed"], 0, "equals"))
        pacing = snapshot["pacing"]
        achieved = None if pacing is None else pacing["achieved_factor"]
        gates.append(self._gate("clock.ratio_recorded", "the achieved real-time factor is recorded",
                                "12 §7.2", achieved is not None, True, "equals"))
        if self.effective.value("pacing.mode") == "wall_clock":
            gates.append(self._gate("pacing.achieved_factor", "the achieved real-time factor held "
                                    "its floor", "08 §11.1", achieved,
                                    self.effective.value("pacing.min_achieved_factor"),
                                    "at_least"))
        audit = snapshot.get("solar_audit")
        if audit is None:
            gates.append(self._skipped("solar.applied_equals_confirmed", "the world's sun is the "
                                       "declared one", "the policy binds no sun, or the session "
                                       "has not started"))
        else:
            gates.append(self._gate("solar.applied_equals_confirmed", "the world's sun is the "
                                    "declared one: worst angle, degrees", "04 C9 §11.8.2",
                                    audit["worst_angle_deg"], audit["tolerance_deg"], "at_most"))
        gates.extend(self._divergence_gates(snapshot.get("divergence")))
        gates.append(self._gate("launch.warnings_adjudicated", "warnings raised with no "
                                "adjudication", "12 §6.4", unadjudicated_warnings, 0, "equals"))
        closing_name = "the run manifest ends with its terminal row"
        if self.manifest is None:
            gates.append(self._skipped(MANIFEST_CLOSING_RECORD, closing_name,
                                       "the run writes no run manifest"))
        else:
            # A manifest whose run reached its end, and was closed, ends with manifest_closed; one without
            # it is a run that was interrupted, and every row before the cut is still valid.
            gates.append(self._gate(MANIFEST_CLOSING_RECORD, closing_name, "04 §12.7",
                                    self.last_manifest_row(self.manifest) == MANIFEST_CLOSED_ROW, True,
                                    "equals"))
        for gate_id, (name, reason) in SKIPPED.items():
            gates.append(self._skipped(gate_id, name, reason))
        return gates

    def _divergence_gates(self, divergence: dict | None) -> list[dict]:
        """The two gates on the bridge's divergence: the worst position and the worst velocity over the
        whole run, each against its limit from the run configuration. A run that compared nothing --
        no body driven, or none the world reported -- skips both, saying how many vehicle-ticks were
        written and read back by nothing, so a run that measured nothing never reads as one that
        measured zero."""
        position_name = ("the largest distance between a pose the bridge commanded and the one the "
                         "world applied, metres")
        velocity_name = ("the largest difference between a velocity the bridge commanded and the one "
                         "the world reported, metres per second")
        if divergence is None:
            reason = "the session has not started"
            return [self._skipped(POSITION_DIVERGENCE, position_name, reason),
                    self._skipped(VELOCITY_DIVERGENCE, velocity_name, reason)]
        if divergence["samples"] == 0:
            unread = divergence["vehicle_ticks_with_no_read_back"]
            reason = (f"no vehicle-tick was compared: {unread} were written and read back by nothing"
                      if unread else "no vehicle-tick was compared: no body was driven")
            return [self._skipped(POSITION_DIVERGENCE, position_name, reason),
                    self._skipped(VELOCITY_DIVERGENCE, velocity_name, reason)]
        return [self._gate(POSITION_DIVERGENCE, position_name, "06 §4.3",
                           divergence["worst_position_m"],
                           self.effective.value(POSITION_DIVERGENCE_LIMIT), "at_most"),
                self._gate(VELOCITY_DIVERGENCE, velocity_name, "06 §4.3",
                           divergence["worst_velocity_m_per_s"],
                           self.effective.value(VELOCITY_DIVERGENCE_LIMIT), "at_most")]

    @staticmethod
    def _gate(gate_id: str, name: str, owner: str, observed: Any, threshold: Any,
              comparison: str) -> dict:
        if observed is None or threshold is None:
            return {"id": gate_id, "name": name, "owner": owner, "status": "skipped",
                    "observed": observed, "threshold": threshold, "comparison": comparison,
                    "met": None, "skip_reason": "nothing has been observed yet"}
        if comparison == "equals":
            met = observed == threshold
        elif comparison == "at_least":
            met = observed >= threshold
        else:
            met = observed <= threshold
        return {"id": gate_id, "name": name, "owner": owner, "status": "evaluated",
                "observed": observed, "threshold": threshold, "comparison": comparison, "met": met}

    @staticmethod
    def _skipped(gate_id: str, name: str, reason: str) -> dict:
        return {"id": gate_id, "name": name, "owner": "12 §7.2", "status": "skipped",
                "observed": None, "threshold": None, "comparison": None, "met": None,
                "skip_reason": reason}

    @staticmethod
    def render(snapshot: dict, gates: list[dict]) -> str:
        """The closeout for a person: the last snapshot's figures and every gate record."""
        lines = [f"closeout  {snapshot['scenario']} :: {snapshot['window_name']}"]
        if snapshot["sim_time_s"] is not None:
            lines.append(f"  reached t={snapshot['sim_time_s']:,.1f} s of "
                         f"{snapshot['window']['begin_s']:,.0f} - {snapshot['window']['end_s']:,.0f}")
        checks = snapshot.get("scenario_checks")
        if checks:
            lock = checks["compile_lock"]
            lines.append(f"  compile lock: {lock['statement']}")
            if lock["compiled"]:
                lines.append(f"    routed by {lock['routed_by']}")
                lines.append(f"    compiled for {lock['compiled_for']}")
                lines.append(f"    dry run {lock['dry_run']}")
            lines.append(f"  teleporting: {checks['teleporting']['statement']}")
        render = snapshot.get("render")
        if render is not None and render.get("render_set_limits"):
            releases = render.get("releases") or {}
            lines.append(f"  render set {render['render_set']}: an optional limit; a vehicle outside "
                         f"it is simulated by SUMO and is not in CARLA or the truth -- "
                         f"{render['vehicle_passes_outside_the_policy']} vehicle-passes outside the "
                         f"policy, {render['capacity_declines']} declined for the capacity, released "
                         f"{releases.get('LeftTheRegion', 0)} leaving the policy and "
                         f"{releases.get('Capacity', 0)} for the capacity")
        admissions = snapshot.get("admissions")
        if admissions:
            opening = admissions["at_window_open"]
            if opening is not None and opening.get("limited"):
                lines.append(f"  admission at the window's begin, t={opening['sim_time_s']:g}: "
                             f"population {opening['population']}, eligible {opening['eligible']}, "
                             f"drawn {opening['admitted']}, shed {opening['shed']}; "
                             f"{opening['left_out']} without a body")
            elif opening is not None:
                lines.append(f"  admission at the window's begin, t={opening['sim_time_s']:g}: "
                             f"population {opening['population']}, all rendered")
            window = admissions["window"]
            lines.append(f"  admission passes in the window: {window['passes']}; most population "
                         f"{window['most_population']}")
            if window.get("limited"):
                lines.append(f"    {window['passes_leaving_out']} of them left vehicles without a "
                             f"body, at most {window['most_left_out']} at once and "
                             f"{window['most_shed']} shed for the capacity")
        render = snapshot.get("render")
        if render is not None and render.get("draw_distance_m") is not None:
            refused = render.get("draw_distance_refused")
            lines.append(f"  draw distance {render['draw_distance_m']:g} m"
                         + (f": refused by the server, so every body was drawn at any range "
                            f"({refused})" if refused is not None
                            else ", rendering only: vehicles beyond it from a camera are in the "
                                 "truth and marked in that camera's sidecars"))
        if render is not None and render.get("pose_source_refused") is not None:
            lines.append(f"  pose source: refused by the server, so no capture says where a vehicle's pose "
                         f"came from ({render['pose_source_refused']})")
        if render is not None and render.get("pose_source_without_jump") is not None:
            lines.append(f"  pose source: the server was built before the jump state, so "
                         f"{render['pose_source_jumps_named_sumo']} jump(s) were sent and written as sumo "
                         f"({render['pose_source_without_jump']})")
        divergence = snapshot.get("divergence")
        if divergence is not None and divergence["samples"]:
            worst = divergence["worst_position_on"]
            fastest = divergence["worst_velocity_on"]
            lines.append(f"  bridge divergence: worst position {divergence['worst_position_m']:.6f} m "
                         f"on {worst['sumo_id']} at t={worst['sim_time_s']:g} s, mean "
                         f"{divergence['mean_position_m']:.6f} m over {divergence['samples']} "
                         f"vehicle-ticks; worst velocity {divergence['worst_velocity_m_per_s']:.6f} m/s "
                         f"on {fastest['sumo_id']} at t={fastest['sim_time_s']:g} s, mean "
                         f"{divergence['mean_velocity_m_per_s']:.6f} m/s against a mean commanded "
                         f"{divergence['mean_commanded_speed_m_per_s']:.3f} m/s"
                         + (f"; {divergence['vehicle_ticks_with_no_read_back']} vehicle-ticks read "
                            "back by nothing" if divergence["vehicle_ticks_with_no_read_back"] else ""))
        readiness = snapshot.get("readiness")
        for view in (readiness or {}).get("channels", []):
            lines.append(f"  view {view['sensor_id']}: {describe_view(view)}")
        for channel in snapshot["channels"]:
            lines.append(f"  channel {channel['sensor_id']}: written {channel['written']}, "
                         f"recorder-dropped {channel['recorder_dropped']}, "
                         + (f"frame unpaired {channel['frame_unpaired']}, "
                            if channel.get("frame_unpaired") is not None else "")
                         + f"illumination unpaired "
                         f"{channel['illumination_unpaired']}, solar block missing "
                         f"{channel['solar_block_missing']}, render set unpaired "
                         f"{channel['render_set_unpaired']}"
                         + (f", supervision {channel['supervision_paired']} paired "
                            f"{channel['supervision_unpaired']} unknown"
                            if channel.get("supervision_unpaired") is not None else "")
                         + (f", lights unknown {channel['lights_unknown']}, pose source unknown "
                            f"{channel['pose_source_unknown']}"
                            if channel.get("lights_unknown") is not None else "")
                         + f"  -> {channel['directory']}")
            if channel.get("draw_distance_captures"):
                lines.append(f"    under the draw distance {channel['draw_distance_captures']} "
                             f"captures: {channel['vehicles_beyond_draw_distance']} vehicle records "
                             f"marked wholly beyond it, "
                             f"{channel['vehicles_partly_beyond_draw_distance']} partly")
            if channel.get("sensor_pose_from_snapshot") is not None:
                lines.append(f"    pose from its own frame's snapshot "
                             f"{channel['sensor_pose_from_snapshot']}, header disagreed "
                             f"{channel['sensor_pose_header_disagreed']}, from the header "
                             f"{channel['sensor_pose_from_header']}")
        for gate in gates:
            if gate["status"] == "skipped":
                lines.append(f"  gate {gate['id']}: skipped ({gate['skip_reason']})")
            else:
                lines.append(f"  gate {gate['id']}: observed {gate['observed']} "
                             f"{gate['comparison']} {gate['threshold']} -> "
                             f"{'met' if gate['met'] else 'NOT met'}")
        return "\n".join(lines)
