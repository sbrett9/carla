"""Native frame recorder for CARLA using CarlaNet.Recording assembly.

Provides a wrapper around the in-engine C# FrameRecorder that captures camera
frames, encodes them to PNG, and writes CoT-XML telemetry sidecars entirely on
the .NET thread pool without crossing to Python or holding the GIL.
"""

import logging

import carlanet as carla


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
                  occlusion, occlusion_margin, occlusion_samples
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

        # Check if CarlaNet.Recording assembly is available
        self.available = bool(getattr(carla, "_CARLANET_RECORDING_AVAILABLE", False))

        self.recording = False
        self.want_enabled = False
        self._handle = None  # Type is CarlaNet.Recording.FrameRecorder (C# object)
        self.logger = logging.getLogger(__name__)

        self.logger.info(
            f"native recorder initialized: dir={self.record_dir}, hz={self.record_hz}, "
            f"available={self.available}, "
            f"occlusion={'on' if self.depth_camera is not None else 'off'}"
        )

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
            note = (
                "" if self._handle.HaveTelemetryOrigin else " (PNG only; no georef origin for XML)"
            )
            self.logger.info(f"recording (native) -> {self.record_dir} @ {self.record_hz} Hz{note}")

        elif not self.want_enabled and self.recording:
            n = self.saved
            note = self._occlusion_note()
            self.world.stop_recording()
            self.recording = False
            self._handle = None
            self.logger.info(f"recording stopped: {n} capture(s) saved{note}")

    def _occlusion_note(self) -> str:
        """How many captures got a per-vehicle occlusion measurement, for the stop message."""
        if self._handle is None or not self._handle.MeasuresOcclusion:
            return ""
        try:
            measured = int(self._handle.OcclusionMeasured)
            unmatched = int(self._handle.OcclusionUnmatched)
        except Exception as e:
            self.logger.debug(f"failed to read occlusion counters: {e}")
            return ""
        if unmatched:
            return (
                f"; occlusion measured on {measured}, "
                f"skipped on {unmatched} with no matching depth frame"
            )
        return f"; occlusion measured on {measured}"

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
        try:
            return int(self._handle.Saved) if self._handle is not None else 0
        except Exception as e:
            self.logger.info(f"exception reading saved frame count: {e}, returning 0")
            return 0

    def stop(self) -> None:
        """Stop recording and clean up.

        Safe to call even if not recording. Suppresses exceptions during cleanup.
        """
        if self.recording:
            try:
                self.world.stop_recording()
            except Exception as e:
                self.logger.info(f"exception during stop_recording: {e}")
            self.recording = False
            self._handle = None
