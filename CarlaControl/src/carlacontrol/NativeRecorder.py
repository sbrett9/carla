"""Native frame recorder for CARLA using CarlaNet.Recording assembly.

Provides a wrapper around the in-engine C# FrameRecorder that captures camera
frames, encodes them to PNG, and writes CoT-XML telemetry sidecars entirely on
the .NET thread pool without crossing to Python or holding the GIL.
"""

import logging
import os
import time

import carlanet as carla

from carlacontrol.CaptureRunReport import CaptureRunReport


class NativeRecorder:
    """Wrapper for CarlaNet's native C# FrameRecorder.

    The recorder taps camera frames directly in the engine, encodes to PNG on
    .NET thread pool workers, and writes CoT-XML telemetry sidecars with vehicle
    tracks and platform metadata. No frames cross to Python, so the GIL is never
    held and the viewer stays smooth while recording.

    Usage pattern:
        recorder = NativeRecorder(world, camera, args)
        recorder.want_enabled = True  # User toggle
        recorder.apply_want()  # Start/stop based on want_enabled
        recorder.trigger(now, surface)  # Called per frame (no-op for native)
        if recorder.recording:
            print(f"Saved {recorder.saved} frames")
        recorder.stop()  # Clean shutdown
    """

    def __init__(
        self,
        world: carla.World,
        camera: carla.Actor,
        args,
        run_id: str | None = None,
        depth_camera: carla.Actor | None = None,
    ):
        """Initialize the native recorder.

        Args:
            world: CARLA world object
            camera: CARLA camera sensor actor to record
            args: Parsed arguments with record_dir, record_hz, affiliation, stale, fov,
                  platform_type, platform_affiliation, platform_callsign, platform_uid,
                  scenario, scenario_id, seed, occlusion, occlusion_margin, occlusion_samples
            run_id: Identifier grouping every capture of this run
            depth_camera: Depth camera held at the recorded camera's pose. When given (and
                  --no-occlusion was not passed) each capture also records how much of each
                  vehicle the camera cannot see.
        """
        self.world = world
        self.camera = camera
        self.args = args
        self.run_id = run_id
        self.depth_camera = depth_camera if getattr(args, "occlusion", True) else None
        self.record_dir = args.record_dir
        self.record_hz = args.record_hz
        self.affiliation = args.affiliation
        self.stale = args.stale
        self.fov = args.fov
        self.platform_type = args.platform_type
        self.platform_affiliation = args.platform_affiliation
        self.platform_callsign = args.platform_callsign
        self.platform_uid = args.platform_uid
        self.scenario_id = self.resolve_scenario_id(args)

        # Check if CarlaNet.Recording assembly is available
        self.available = bool(getattr(carla, "_CARLANET_RECORDING_AVAILABLE", False))

        self.recording = False
        self.want_enabled = False
        self._handle = None  # Type is CarlaNet.Recording.FrameRecorder (C# object)
        # Where the recording window started, on both clocks. The simulation clock is the one the
        # captures are stamped against; the wall clock is the one an operator is waiting on.
        self._started_sim_time = 0.0
        self._started_wall_time = 0.0
        self.logger = logging.getLogger(__name__)

        self.logger.info(
            f"native recorder initialized: dir={self.record_dir}, hz={self.record_hz}, "
            f"available={self.available}, "
            f"scenario={self.scenario_id or 'none'}, "
            f"occlusion={'on' if self.depth_camera is not None else 'off'}"
        )

    @staticmethod
    def resolve_scenario_id(args) -> str | None:
        """Which scenario a capture says it belongs to.

        An explicit --scenario-id wins. Failing that, a run driving a storyboard is named after that
        storyboard file, so the sidecar says which scenario produced a still without the operator
        having to name it twice. It reaches the sidecar and the run report and never the imagery: a
        scenario name indexes a whole set of scenes, and an image carrying one is a handle a model
        can learn instead of learning the scene.
        """
        explicit = getattr(args, "scenario_id", None)
        if explicit:
            return str(explicit)
        scenario = getattr(args, "scenario", None)
        if scenario:
            return os.path.splitext(os.path.basename(str(scenario)))[0]
        return None

    def apply_want(self) -> None:
        """Apply the want_enabled state (start or stop recording).

        Call this periodically (e.g., per frame) to reconcile the desired state
        with the actual recording state. Handles start/stop transitions and
        prints status messages.
        """
        if self.want_enabled and not self.recording:
            if not self.available:
                self.logger.error(
                    "recording unavailable: CarlaNet.Recording not built (rebuild the DLLs)."
                )
                self.want_enabled = False
                return

            self._handle = self.world.start_recording(
                self.camera,
                self.record_dir,
                self.record_hz,
                self.affiliation,
                self.stale,
                fov=self.fov,
                platform_type=self.platform_type,
                platform_affiliation=self.platform_affiliation,
                platform_callsign=self.platform_callsign,
                platform_uid=self.platform_uid,
                run_id=self.run_id,
                scenario_id=self.scenario_id,
                seed=self.args.seed,
                depth_camera=self.depth_camera,
                occlusion_margin_m=getattr(self.args, "occlusion_margin", 1.0),
                occlusion_samples=getattr(self.args, "occlusion_samples", 24),
            )

            if self._handle is None:
                self.logger.info("failed to start recording (handle is None)")
                self.want_enabled = False
                return

            self.recording = True
            self._started_sim_time = self._sim_time()
            self._started_wall_time = time.monotonic()
            note = (
                "" if self._handle.HaveTelemetryOrigin else " (PNG only; no georef origin for XML)"
            )
            self.logger.info(f"recording (native) -> {self.record_dir} @ {self.record_hz} Hz{note}")

        elif not self.want_enabled and self.recording:
            report = self.report()
            note = self._occlusion_note()
            self.world.stop_recording()
            self.recording = False
            self._handle = None
            self._log_report(report, note)

    def _occlusion_note(self) -> str:
        """How many captures got a per-vehicle occlusion measurement, for the stop message."""
        if self._handle is None or not self._handle.MeasuresOcclusion:
            return ""
        try:
            measured = int(self._handle.OcclusionMeasured)
            unmatched = int(self._handle.OcclusionUnmatched)
            none_at_all = int(self._handle.OcclusionNoDepthCaptures)
            out_of_step = int(self._handle.OcclusionDepthOutOfStep)
            wrong_pose = int(self._handle.OcclusionDepthWrongPose)
        except Exception as e:
            self.logger.debug(f"failed to read occlusion counters: {e}")
            return ""
        if not unmatched:
            return f"; occlusion measured on {measured}"
        # Name which way the pairing failed: no depth frames at all means the depth camera never
        # delivered, out-of-step means its stream is running behind or ahead of the recorded one, and
        # wrong-pose means the two cameras are no longer looking from the same place.
        reasons = ", ".join(
            f"{count} {label}"
            for label, count in (
                ("with no depth frame at all", none_at_all),
                ("with the depth stream out of step", out_of_step),
                ("with the cameras at different poses", wrong_pose),
            )
            if count
        )
        return f"; occlusion measured on {measured}, skipped on {unmatched} ({reasons})"

    def toggle_want(self, enabled: bool | None = None) -> None:
        """
        Toggle the want_enabled state.

        Args:
            enabled: If None, toggle the current state. If a boolean, set the state to this value.
        """
        if enabled is None:
            self.want_enabled = not self.want_enabled
        else:
            self.want_enabled = enabled
        
    def update(self, now) -> None:
        """Update the recorder (no-op for native recorder)."""
        pass

    @property
    def saved(self) -> int:
        """Number of frames saved so far.

        Returns:
            Frame count, or 0 if not recording or handle unavailable
        """
        return self._counter("Saved")

    @property
    def dropped(self) -> int:
        """Captures the recorder reached for and could not write.

        The encoder queue is bounded and never blocks the stream reader, so when every worker is
        busy the capture is discarded rather than delaying the frames behind it. That is the right
        trade for a viewer that has to stay smooth, but it means a recording can be short of
        captures -- imagery and truth sidecar alike -- with nothing in the output to show it.
        """
        return self._counter("Dropped")

    def _counter(self, name: str) -> int:
        try:
            return int(getattr(self._handle, name)) if self._handle is not None else 0
        except Exception as e:
            self.logger.info(f"exception reading the {name} capture count: {e}, returning 0")
            return 0

    def _sim_time(self) -> float:
        """The world's own clock, which is what captures are stamped against."""
        try:
            return float(self.world.get_sim_time())
        except Exception as e:
            self.logger.debug(f"could not read the simulation clock: {e!r}")
            return 0.0

    def report(self) -> CaptureRunReport:
        """What this stretch of recording produced, on both clocks. Call before releasing the handle."""
        return CaptureRunReport(
            saved=self.saved,
            dropped=self.dropped,
            sim_seconds=max(0.0, self._sim_time() - self._started_sim_time),
            wall_seconds=max(0.0, time.monotonic() - self._started_wall_time),
        )

    def _log_report(self, report: CaptureRunReport, note: str = "") -> None:
        """Say what the run produced and, separately and loudly, what it lost."""
        self.logger.info(f"recording stopped: {report.describe()}{note}")
        if report.dropped:
            self.logger.warning(
                f"{report.dropped} capture(s) were discarded because every encoder worker was "
                "busy; each is a missing still AND its missing truth sidecar, so this recording "
                "has gaps a consumer cannot see from the files alone"
            )

    def stop(self) -> None:
        """Stop recording and clean up.

        Safe to call even if not recording. Suppresses exceptions during cleanup.
        """
        if self.recording:
            report = self.report()
            note = self._occlusion_note()
            try:
                self.world.stop_recording()
            except Exception as e:
                self.logger.info(f"exception during stop_recording: {e}")
            self.recording = False
            self._handle = None
            self._log_report(report, note)
