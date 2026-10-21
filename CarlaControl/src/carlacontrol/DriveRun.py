"""One carla-drive run, from connecting to the server to giving the world back."""
from __future__ import annotations

import argparse
import logging
import os
import time
from datetime import UTC, datetime

import carlanet as carla

# isort: split
# The .NET namespaces exist only once carlanet has loaded their assemblies, so this import has to
# follow it; an import sorter would put it first and break the tool at start.
from CarlaNet.CoSim import CoSimSessionRefusedException

# isort: split
from carlacontrol.CameraFootprints import CameraFootprints
from carlacontrol.DriveCamera import DriveCamera
from carlacontrol.DriveCotFeed import DriveCotFeed
from carlacontrol.DriveReport import DriveReport
from carlacontrol.FreeViewParts import FreeViewParts
from carlacontrol.PacingProgress import PacingProgress
from carlacontrol.RenderedVehicleCentre import RenderedVehicleCentre
from carlacontrol.SessionSumo import SessionSumo
from carlacontrol.SunDeclaration import SunDeclaration

logger = logging.getLogger(__name__)


class DriveRun:
    """One carla-drive run: the SUMO session, the camera it records from and the report it ends with.

    The command parses its options (`carlacontrol.commands.drive`) and hands them here. The run checks
    the files they name, starts the session, places the fixed camera or opens the free view, advances
    the session until the steps run out, the scenario ends or the free view is closed, sending the live
    feed's samples after every advance where one was asked for, and logs what the session reports.
    Whatever happens after it connects, it gives back what it took, in order.
    """

    # How long any one server call may take once the session is driving.
    RUN_TIMEOUT_S = 30.0

    def __init__(self, args: argparse.Namespace, tool: str) -> None:
        self.args = args
        # The command the run manifest's header names, as every other file the drive writes does.
        self.tool = tool
        self.client = None
        self.world = None
        self.session = None
        self.camera = None
        self.depth = None
        self.recorder = None
        self.cot: DriveCotFeed | None = None
        self.run_id = ""
        self.steps = 0
        self.free = FreeViewParts()
        self.aim = RenderedVehicleCentre()
        self.progress = PacingProgress()
        self.footprints = CameraFootprints()
        self.aims_at_traffic = (args.view == "fixed" and args.camera_aim == "traffic"
                                and not args.no_record)

    def run(self) -> int:
        """Drive the world. Returns the command's exit status: 0 for a run that ended as asked, 1 for
        one refused or stopped, 2 for an input refused before anything started."""
        args = self.args
        refused = self._refuse_unusable_inputs()
        if refused is not None:
            return refused

        sun = SunDeclaration(args)
        sumo = SessionSumo(args.sumo_home)
        sumo.announce(args.sumo_gui)
        if args.view == "free":
            FreeViewParts.warn_of_an_uncovered_world(args)

        self.client = carla.Client(args.host, args.port)
        self.client.set_timeout(self.RUN_TIMEOUT_S)
        self.world = self.client.get_world()
        logger.info("server %s, map %s", self.client.get_server_version(),
                    self.world.get_map().name)
        self.run_id = f"run-{datetime.now(UTC):%Y%m%d-%H%M%S}"

        try:
            self.session = self._start_session(sun, sumo)
            if self.session is None:
                return 1
            if self.cot is not None:
                self.cot.start(self.session)
            self._announce_session()

            # Everything up to the recorder starting happens with the world already in synchronous mode:
            # the session takes the clock before the camera exists, so every image the camera delivers is
            # of a tick the session issued.
            if args.view == "free":
                stopped = self._open_free_view()
            elif not args.no_record:
                stopped = self._record_from_the_fixed_camera()
            else:
                stopped = None
            if stopped is not None:
                return stopped
            return self._drive()
        except CoSimSessionRefusedException as refused:
            # Refused before the first tick -- at the start, or on a prewarm step -- with everything the
            # session took given back. A world another drive holds gets its own line: the holder is who
            # to stop, and one SUMO drive is the only traffic system a world admits.
            holder = getattr(refused, "HeldBy", None)
            if holder is not None:
                logger.error("\nrefused: %s holds this world's drive lease. Stop that drive, or run "
                             "against a different world; where the drive is gone and did not release "
                             "the lease, world.break_drive_lease() ends it.", holder)
            logger.error("the session refused at %s: %s", refused.StageName, refused.Message)
            return 1
        finally:
            self._close()

    def _refuse_unusable_inputs(self) -> int | None:
        """2 where a file the run needs is missing, the camera name is one the rule refuses, or the live
        feed cannot be sent as asked, before anything starts; None where the run may go on."""
        args = self.args
        for label, path in (("scenario", args.scenario), ("world package", args.world_package),
                            ("catalogue", args.catalogue)):
            if not os.path.isfile(path):
                logger.error("no %s at %s", label, path)
                return 2
        # Refused before anything starts rather than when the camera is spawned, after the prewarm. A
        # name a live camera holds is the server's to refuse, at the spawn.
        if args.camera_name is not None:
            refused = carla.camera_name_problem(args.camera_name)
            if refused is not None:
                logger.error("--camera-name: %s", refused)
                return 2
        if args.cot_udp is not None:
            try:
                self.cot = DriveCotFeed(args)
            except ValueError as unusable:
                logger.error("%s", unusable)
                return 2
        return None

    def _start_session(self, sun: SunDeclaration, sumo: SessionSumo):
        """Start the SUMO drive session, giving the server calls of its start-up checks the longer
        timeout. None where carlanet returns no session, as it does when its co-simulation assembly
        is not loaded."""
        args = self.args
        self.client.set_timeout(max(args.setup_timeout, self.RUN_TIMEOUT_S))
        session = self.world.start_sumo_drive(
            args.scenario, args.world_package, args.catalogue,
            fixed_delta=args.fixed_delta,
            record_hz=args.record_hz,
            warm_up_to=args.warm_up,
            window_opens_at=args.window_opens_at,
            step_length=args.step_length,
            road_layer_visible=args.show_road_mesh,
            signal_layer_visible=args.show_signals,
            epoch=sun.epoch,
            illumination=sun.illumination,
            real_time_factor=args.real_time_factor,
            sumo_home=sumo.home,
            allow_sumo_version_mismatch=args.allow_sumo_version_mismatch,
            sumo_gui=args.sumo_gui,
            allow_teleporting=args.allow_teleporting,
            accept_skipped_dry_run=args.accept_skipped_dry_run,
            sumo_answer_timeout_s=args.sumo_answer_timeout,
            vehicle_lamps=not args.no_vehicle_lamps,
            headlight_on_below_deg=args.headlight_on_below,
            headlight_off_above_deg=args.headlight_off_above,
            draw_distance_m=args.draw_distance,
            render_set=args.render_set,
            region_centre=(args.region_x, args.region_y),
            region_radius_m=args.region_radius,
            region_hysteresis_m=args.region_hysteresis,
            capacity=args.capacity,
            render_min_pixels=args.render_min_pixels,
            render_admit_lead_s=args.render_admit_lead,
            render_release_lag_s=args.render_release_lag,
            world_truth_track=args.world_truth_track,
            world_truth_track_interval_s=args.world_truth_track_interval,
            run_manifest=args.run_manifest,
            run_manifest_header={"run_id": self.run_id, "tool": self.tool,
                                 "view": args.view} if args.run_manifest else None,
            live_track=self.cot.live_track() if self.cot is not None else None,
            # What is printed about collisions, and nothing else: the session keeps every one either
            # way, and a line per collision is bound only where every collision is to be printed.
            collision_detail=args.collision_detail,
            on_collision=DriveReport.print_collision if args.collision_detail else None,
            # Bound only where the aim needs it: the session hands out a pose per rendered
            # vehicle per tick, and a callback that spends the whole run declining them is a
            # crossing into Python per vehicle per tick for nothing. No divergence callback for
            # the same reason: the report keeps the worst divergence and names its vehicle and
            # tick, and a crossing per vehicle per tick is a wait on the interpreter inside the
            # tick loop whenever another thread of this process -- a free view's window -- holds it.
            on_pose=self.aim.collect if self.aims_at_traffic else None)
        self.client.set_timeout(self.RUN_TIMEOUT_S)
        return session

    def _announce_session(self) -> None:
        """Log what the session established as it started, before the first step."""
        session = self.session
        # Whether the server holds the drive lease for this run. Without it nothing on the server
        # stops a traffic manager or a second drive putting vehicles into the capture; the run report
        # says so, and so does the console, which an operator reads first.
        if session.Report.DriveLeaseRefused is not None:
            logger.warning("drive lease NOT HELD: this server has no drive lease, so nothing on it "
                           "stops another traffic system driving vehicles into this run (the server "
                           "said: %s)", session.Report.DriveLeaseRefused)
        else:
            logger.info("drive lease: held as %s", session.Report.DriveLeaseHolder)

        # What the session launched -- which installation, and which of its binaries -- and how that
        # binary stood against the world's converter, as the session established it. An accepted
        # mismatch and an unchecked world both ran, and both are said louder than a match.
        launched = session.Report.Sumo
        (logger.info if launched.Agrees else logger.warning)(
            "sumo: %s, launching %s; %s", launched.Installation, launched.Binary, launched.Verdict)
        # A compiled scenario was checked against its lock before SUMO started; an uncompiled one
        # ran with nothing to check it against, and says so louder.
        compiled = session.Report.CompileLock
        (logger.info if compiled.Compiled else logger.warning)("compile lock: %s", compiled)
        if compiled.Compiled:
            logger.info("routed by: %s", compiled.RoutedByText)
            # A skipped run the drive accepted is said louder: the compile never checked that every
            # planned vehicle enters.
            (logger.warning if compiled.SkippedDryRunAccepted else logger.info)(
                "dry run: %s", compiled.DryRunText)
        teleporting = session.Report.Teleporting
        (logger.warning if teleporting.Enabled else logger.info)("teleporting: %s", teleporting)
        logger.info("clock: %s", session.Clock)
        # The pace as the session declared it, not as carla-drive asked for it: the session read
        # the factor once and is the one thing that holds the run to it.
        logger.info("pace: %s", PacingProgress.declared(session.Report.Pacing))
        logger.info("sun: %s", session.Sun if session.Sun is not None
                    else "left as the world holds it; the run's lighting honors no epoch")
        logger.info("layers: %s", ", ".join(
            f"{layer} {'drawn' if session.Report.LayerVisibility[layer] else 'hidden'}"
            for layer in session.Report.LayerVisibility.Keys))
        logger.info("render set: %s", DriveReport.describe_render_set(session.Report))
        logger.info("draw distance: %s",
                    DriveReport.describe_draw_distance(session.Report.DrawDistanceMetres))
        logger.info("world truth track: %s",
                    DriveReport.describe_world_truth_track(session.WorldTruthTrack))
        logger.info("run manifest: %s", session.RunManifest.Path if session.RunManifest is not None
                    else "none written; give --run-manifest to write one")
        logger.info("cot feed: %s", DriveReport.describe_cot_feed(self.cot))

    def _open_free_view(self) -> int | None:
        """Open the window with the camera the operator flies. 1 where it cannot open."""
        # The window is up for the whole drive, the prewarm included: nothing is recorded until
        # F is pressed, and F records nothing before the capture window opens.
        logger.info("run id: %s", self.run_id)
        try:
            self.free.open(self.client, self.world, self.session, self.args, self.run_id)
        except RuntimeError as failure:
            # A window that cannot open -- no display on Linux, most often -- ends the run
            # before its first step, with everything given back on the way out.
            logger.error("%s", failure)
            return 1
        return None

    def _record_from_the_fixed_camera(self) -> int | None:
        """Aim and spawn the fixed camera, prewarm to the capture window and start recording. 1 where
        the scenario ends before the window opens or the recorder does not start."""
        args, world, session = self.args, self.world, self.session
        centre = DriveCamera.world_centre(world)
        if self.aims_at_traffic:
            # One step, to see where the bodies actually went. Bought rather than assumed, and
            # it costs the run its first SUMO step.
            self.aim.open = True
            session.Advance()
            self.aim.open = False
            self._send_the_feed()
            self.steps += 1
            if self.aim.centre() is None:
                logger.warning("nothing was rendered on the first step, so the camera is aimed "
                               "at the center of the world instead")
            else:
                centre = self.aim.centre()
                logger.info("aimed at %d rendered vehicles", self.aim.count)
        self.camera = DriveCamera.spawn_camera(world, args, centre)
        self.depth = DriveCamera.spawn_depth_camera(world, args, self.camera,
                                                    DriveCamera.depth_range_m())
        if session.RunManifest is not None:
            session.RunManifest.PlaceSensor(world.camera_name(self.camera), self.camera.id,
                                            world.camera_exposure(self.camera))
        if args.render_set == "cameras":
            # From the next step a render set that follows the cameras follows this one's view.
            session.AddCamera(self.camera.id)

        stopped = self._prewarm()
        if stopped is not None:
            return stopped
        return self._start_recording()

    def _prewarm(self) -> int | None:
        """Advance to the capture window's opening. 1 where the scenario ends first."""
        session = self.session
        # The prewarm is ticked with the camera in the world, so its tiles stream, and recorded by
        # nothing: the window's first capture is the frame at the instant it opens.
        prewarm_steps = 0
        while session.RenderedTimeSeconds < session.WindowOpensAtSeconds - 1e-6:
            more = session.Advance()
            self._send_the_feed()
            if not more:
                logger.error("the scenario ended at t=%.2f s, before the window opened at "
                             "t=%.2f s", session.RenderedTimeSeconds,
                             session.WindowOpensAtSeconds)
                return 1
            prewarm_steps += 1
        if prewarm_steps:
            self.steps += prewarm_steps
            logger.info("prewarmed %d SUMO steps; the window opens at t=%.2f s",
                        prewarm_steps, session.WindowOpensAtSeconds)
        return None

    def _start_recording(self) -> int | None:
        """Start the fixed camera's recorder. 1 where it does not start."""
        args, session = self.args, self.session
        os.makedirs(args.record_dir, exist_ok=True)
        # Every capture carries the declaration of its own frame's sun and the audit's residual
        # on the tick that rendered it, so a still's illumination is traceable to what this run
        # said it should be. And it lists the bodies its own frame rendered, each by the SUMO
        # vehicle it rendered, rather than every vehicle actor: the bodies parked between loans
        # stand below the ground and are no vehicle anyone could see. Each vehicle's occlusion
        # is measured against the depth camera attached to the camera.
        self.recorder = self.world.start_recording(self.camera, args.record_dir, args.record_hz,
                                                   fov=args.fov, depth_camera=self.depth,
                                                   illumination=session.Illumination,
                                                   render_set=session.RenderSet)
        if self.recorder is None:
            return 1
        logger.info("recording %s -> %s", self.recorder.Name, args.record_dir)
        return None

    def _drive(self) -> int:
        """Advance the session until the steps run out, the scenario ends or the free view is closed,
        then log what it reports. 1 where it stops part-way."""
        args, session = self.args, self.session
        started = time.time()
        try:
            while True:
                more = session.Advance()
                # The step SUMO ended on was rendered too, and its frames sampled.
                self._send_the_feed()
                if not more:
                    break
                self.steps += 1
                self.footprints.after_step(session)
                if args.steps and self.steps >= args.steps:
                    break
                self.progress.after_step(session, self.steps)
                if self.free.closed:
                    logger.info("the free view was closed at t=%.1f s; the drive ends there",
                                session.RenderedTimeSeconds)
                    break
        except CoSimSessionRefusedException as refused:
            # SUMO, the server or the sun failed part-way, and both sides stopped together. The report
            # says where, and which frame was the last whose truth holds.
            logger.error("\nthe run stopped at %s (%s): %s", refused.StageName, refused.CauseName,
                         refused.Message)
            logger.info("%s", session.Report)
            return 1

        self._report(time.time() - started)
        return 0

    def _send_the_feed(self) -> None:
        """Send the live feed's samples of the advance just made, where the run sends one."""
        if self.cot is not None:
            self.cot.after_step()

    def _report(self, elapsed: float) -> None:
        """Log the session's report of a run that ended as asked."""
        session = self.session
        pacing = session.Report.Pacing
        logger.info("\n%d SUMO steps in %.1f s wall clock; pace %s: %s\n", self.steps, elapsed,
                    PacingProgress.declared(pacing), PacingProgress.achieved(pacing))
        logger.info("%s", session.Report)
        track = session.WorldTruthTrack
        if track is not None:
            # Its summary is written as the session ends, below, saying why the track ended.
            logger.info("world truth track  %d rows over %d samples -> %s", track.Rows, track.Samples,
                        track.Path)
        worst = session.Report.WorstDivergence
        if worst is not None and worst.PositionMetres > 0.0:
            logger.info("\nworst divergence %.6f m on %s at tick %d",
                        worst.PositionMetres, worst.VehicleId, worst.TickIndex)
        if session.Report.PosesComputed == 0:
            # The likeliest reason by far, and the one that produces a run that looks healthy and
            # renders an empty road: the scenario's vTypes name no blueprint the catalogue has
            # measured, so every vehicle is simulated and none is rendered.
            logger.error("\nNOTHING WAS RENDERED. The refused-type counts above say why; the usual "
                         "cause is vTypes with no carla:blueprint parameter, which are simulated and "
                         "never given a body.")

    def _close(self) -> None:
        """Give back what the run took, each part tried whatever the ones before it did."""
        # Order matters on the way out: stop tapping the cameras, take them out of the world, then
        # let the session give back the bodies, the population lease and the world's clock. The
        # session restores the settings it found whatever happened above, which is what keeps an
        # editor from being left waiting for a tick from a process that has stopped.
        self.free.close()
        try:
            self.world.stop_recording()
        except Exception as failure:
            logger.error("could not stop the recorder: %r", failure)
        if self.recorder is not None:
            DriveReport.report_captures(self.recorder)
        if self.depth is not None:
            # The depth camera leaves the world before the camera it is attached to.
            try:
                self.depth.destroy()
            except Exception as failure:
                logger.error("could not destroy the depth camera: %r", failure)
        if self.camera is not None:
            try:
                # A session following the camera stops before the camera leaves the world.
                if self.session is not None and self.args.render_set == "cameras":
                    self.session.RemoveCamera(self.camera.id)
                self.camera.destroy()
            except Exception as failure:
                logger.error("could not destroy the camera: %r", failure)
        if self.cot is not None:
            # Before the session goes: the samples still waiting are taken from it, and its counts read.
            try:
                self.cot.close()
            except Exception as failure:
                logger.error("could not close the cot feed: %r", failure)
        if self.session is not None:
            # Disposal attempts every step of the shutdown whatever the ones before it did, and
            # reports the ones that failed together. Logging them is all a harness can do, and it
            # is more than losing them.
            try:
                self.session.Dispose()
            except Exception as failure:
                logger.error("the session did not shut down cleanly: %r", failure)
