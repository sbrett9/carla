"""The camera carla-drive's free view flies, and what it records from it."""
from __future__ import annotations

import argparse
import logging

from carlacontrol.DriveCamera import DriveCamera
from carlacontrol.DriveReport import DriveReport
from carlacontrol.FreeView import FreeView
from carlacontrol.PyGameSensorController import PyGameSensorController
from carlacontrol.RenderRegionCoverage import RenderRegionCoverage
from carlacontrol.SensorRig import SensorRig
from carlacontrol.SpanRecorder import SpanRecorder
from carlacontrol.WorldPackageReader import WorldPackageReader

logger = logging.getLogger(__name__)


class FreeViewParts:
    """What a free view is made of, filled in as each part is made.

    The way out takes down exactly what was made, in order: the recorder first, flushing its span
    while the cameras still exist; then the window and the flight controller's mover, so nothing
    moves a camera being destroyed; then the cameras.
    """

    def __init__(self) -> None:
        self.rig = None
        self.recorder = None
        self.view = None
        self.session = None

    @property
    def closed(self) -> bool:
        """Whether the operator has closed the window."""
        return self.view is not None and self.view.closed.is_set()

    def open(self, client, world, session, args: argparse.Namespace, run_id: str) -> None:
        settings = self.settings(args, DriveCamera.world_centre(world), SensorRig.FT_PER_M,
                                 DriveCamera.depth_range_m())
        # Spawned with the world already the session's, so every frame either camera delivers is of
        # a tick the session issued.
        self.rig = SensorRig(world=world, args=settings, client=client)
        if args.render_set == "cameras":
            self.follow(session)
        controller = PyGameSensorController(self.rig, world, self.rig.get_initial_pose(),
                                            speed=args.flight_speed)
        if not args.no_record:
            self.recorder = self.span_recorder(client, session, args, run_id)
            self.recorder.run_in_background()
        self.view = FreeView(settings, world, self.rig, controller, recorder=self.recorder)
        self.view.open()
        logger.info("free view: camera %s over (%.1f, %.1f) at %.0f m, %dx%d, fov %g; %s",
                    self.rig.camera.id, settings.x, settings.y, args.camera_z, args.width,
                    args.height, args.fov,
                    "F records a span once the camera's tiles are in, from the capture window's "
                    f"opening at t={session.WindowOpensAtSeconds:g} s; Esc ends the drive"
                    if self.recorder is not None else "nothing is recorded; Esc ends the drive")

    def span_recorder(self, client, session, args: argparse.Namespace, run_id: str):
        """The recorder F toggles: the fixed camera's recording, from the flown camera.

        It records through a world object of its own, so its recorder is the only one that
        object's stop_recording ever stops. Every span shares this drive's run id, so the spans of
        one drive can be gathered back into it, and every span's folder and stills are named after
        the camera: the name it was spawned under, or its default.
        """
        recording = client.get_world()
        camera, depth = self.rig.camera, self.rig.depth_cam

        def start(directory: str):
            # As the fixed camera records, and with the rig's depth camera for occlusion.
            return recording.start_recording(camera, directory, args.record_hz, fov=args.fov,
                                             run_id=run_id, depth_camera=depth,
                                             illumination=session.Illumination,
                                             render_set=session.RenderSet)

        def window_open() -> str | None:
            opens = session.WindowOpensAtSeconds
            if session.RenderedTimeSeconds < opens - 1e-6:
                return f"the capture window opens at t={opens:g} s"
            return None

        return SpanRecorder(
            args.record_dir, recording.camera_name(camera), args.record_hz,
            start_recording=start, stop_recording=recording.stop_recording,
            view_readiness=lambda: recording.get_view_readiness(camera),
            may_record=window_open,
            on_closed=lambda handle, _directory: DriveReport.report_captures(handle))

    def follow(self, session) -> None:
        """Register the flown camera with the session, so a render set that follows the cameras
        follows its view from the next step on. Its depth camera shares its pose and view, so it is
        not registered as well."""
        session.AddCamera(self.rig.camera.id)
        self.session = session

    def close(self) -> None:
        """Take down what was made. Each part is tried whatever the ones before it did.

        The session stops following the camera first: a camera destroyed while still registered is
        left out of every step after, and counted, rather than followed.
        """
        if self.session is not None and self.rig is not None:
            try:
                self.session.RemoveCamera(self.rig.camera.id)
            except Exception as failure:
                logger.error("could not stop the session following the free view's camera: %r",
                             failure)
        for part, take_down in ((self.recorder, "close"), (self.view, "close"),
                                (self.rig, "cleanup")):
            if part is None:
                continue
            try:
                getattr(part, take_down)()
            except Exception as failure:
                logger.error("could not take down the free view's %s: %r",
                             type(part).__name__, failure)

    @staticmethod
    def settings(args: argparse.Namespace, centre: tuple[float, float], feet_per_metre: float,
                 depth_range_m: float) -> argparse.Namespace:
        """The flight window's settings, in the shape `SensorRig` and `PygameInterface` read them, with
        the camera starting over `centre` in CARLA's frame."""
        x, y = centre
        return argparse.Namespace(
            x=x, y=y,
            # The rig takes its start altitude in feet, as run_SCTMV.py does.
            z=args.camera_z * feet_per_metre,
            width=args.width, height=args.height, fov=args.fov,
            # The camera keeps its blueprint's exposure, the Default profile's: manual, ISO 100, 1/320 s,
            # f/4 and no compensation. Every capture records it from the camera's attributes.
            ev=None,
            # The run configuration's one depth range, so occlusion is measured as far as a capture
            # run's is rather than to the depth camera's stock 1000 m.
            depth_max_range=depth_range_m,
            # Frames are kept as they arrive: this window never ticks the world.
            asynchronous=True,
            fixed_delta=args.fixed_delta,
            time_rate=1.0,
            # The flown camera is spawned under it, and every span from it is named after it.
            camera_name=args.camera_name)

    @staticmethod
    def warn_of_an_uncovered_world(args: argparse.Namespace) -> None:
        """Say what a free view will find rendered under a limit: under the cameras, whatever it flies
        over; under the circle, only the circle, and then whether the circle is smaller than the world.

        A circle is fixed and a free camera can go anywhere, so the operator is told before the drive
        starts, with the region that would take in the whole world package. With no limit there is
        nothing to say: every vehicle SUMO has is rendered.
        """
        if args.render_set == "all":
            return
        if args.render_set == "cameras":
            logger.info("free view: vehicles are rendered where the flown camera looks, placed %g s of "
                        "their travel ahead of its view and taken away %g s after they leave it; until "
                        "the camera is registered %s decides", args.render_admit_lead,
                        args.render_release_lag,
                        "the region circle" if args.region_radius is not None else "every vehicle")
            return
        coverage = RenderRegionCoverage.from_manifest(WorldPackageReader(args.world_package).manifest)
        if coverage is None:
            logger.info("free view: the world package records no extent to check the region against")
            return
        advice = coverage.advice(args.region_x, args.region_y, args.region_radius, args.capacity)
        if advice is None:
            logger.info("free view: the render region takes in the whole world")
        else:
            logger.warning("free view: %s", advice)
