"""What carla-drive says about its session and its captures."""
from __future__ import annotations

import logging

logger = logging.getLogger(__name__)


class DriveReport:
    """Turns what the session and a recorder report into the lines carla-drive logs."""

    @staticmethod
    def describe_render_set(report) -> str:
        """The render set as the launch states it: every vehicle, or the limit and what it leaves out."""
        if not report.RenderSetLimits:
            return f"{report.RenderSetPolicy}; SUMO runs under seed {report.SumoSeed}"
        return (f"{report.RenderSetPolicy}; SUMO runs under seed {report.SumoSeed}. An optional limit, "
                "chosen for speed: a vehicle outside it is simulated by SUMO and is not in CARLA -- no "
                "body, no frame, no truth record")

    @staticmethod
    def describe_draw_distance(metres: float | None) -> str:
        """The draw distance as the launch states it: none, or what it does and does not change."""
        if metres is None:
            return "none; every body is drawn at any range"
        return (f"{metres:g} m, rendering only: every vehicle has its body, is posed and is in the "
                "truth; a body farther than that from a camera is not drawn in that camera's image, and "
                "each capture's sidecar marks it")

    @staticmethod
    def describe_world_truth_track(track) -> str:
        """The world truth track as the launch states it: none, or where it goes and how often."""
        if track is None:
            return "none written; give --world-truth-track to write one"
        every = ("every SUMO frame" if track.SumoStepsPerSample == 1
                 else f"every {track.IntervalSeconds:g} s")
        return (f"{track.Path}, {every} inside the capture window, every vehicle SUMO has, drawn or not; "
                f"summary {track.SummaryPath}")

    @staticmethod
    def describe_cot_feed(feed) -> str:
        """The live Cursor-on-Target feed as the launch states it: none, or where it goes, which vehicles
        it sends, how often, and the affiliations it draws them with."""
        if feed is None:
            return "none sent; give --cot-udp to send one"
        track = feed.track
        ticks = int(track.TicksPerSample)
        every = "every world tick" if ticks == 1 else f"every {ticks} world ticks"
        return (f"to {feed.target()}, ttl {feed.ttl}: every vehicle with a body, one datagram each, on "
                f"the newest frame of {every} ({track.SampleIntervalSeconds:.2f} s), the prewarm's "
                f"included; stale {feed.stale_s:g} s after its stamp; uid "
                f"{feed.uid_prefix}-SUMO-<SUMO id>; {feed.describe_convention()}")

    @staticmethod
    def print_collision(span) -> None:
        """One collision as it ends, under --collision-detail. Called from the session's tick thread; a log
        line does not block it."""
        logger.info("collision: %s", span)

    @staticmethod
    def report_captures(recorder) -> None:
        """Say what a recorder wrote. Read once it has flushed, so the counts are its run's and not a
        moment's."""
        logger.info("captures           %s written, %s dropped; %s carry their frame's "
                    "illumination declaration, %s do not", recorder.Saved, recorder.Dropped,
                    recorder.IlluminationPaired, recorder.IlluminationUnpaired)
        # A capture whose snapshot carried no sun was written with no _solar and no carla:solar: a still
        # with no recorded illumination and no band, so any is said louder.
        missing_sun = recorder.SolarBlockMissing
        (logger.warning if missing_sun else logger.info)(
            "sun                %s captures were written without a solar block", missing_sun)
        drawn_under = getattr(recorder, "DrawDistanceCaptures", 0)
        if drawn_under:
            # Vehicles in the world and in the truth that a capture's image did not show, each marked in
            # its sidecar so none is read as seen.
            logger.info("draw distance      %s captures drawn under it; %s vehicle records marked "
                        "wholly beyond it, %s partly", drawn_under, recorder.VehiclesBeyondDrawDistance,
                        recorder.VehiclesPartlyBeyondDrawDistance)
        # A capture whose frame's render set the session no longer held lists no vehicle rather
        # than a guessed set, so any such capture is truth missing, and is said louder.
        (logger.warning if recorder.RenderSetUnpaired or recorder.RenderSetBodiesMissing
         else logger.info)(
            "render set         %s captures list their frame's rendered vehicles, %s list none "
            "because the frame's set was no longer held; %s rendered bodies had no truth "
            "record", recorder.RenderSetPaired, recorder.RenderSetUnpaired,
            recorder.RenderSetBodiesMissing)
        # The supervision each capture carries is the server's for its own frame. A capture of a frame a
        # plan was in force on whose supervision was not to be had says so and carries none, which is
        # truth missing, and is said louder. A recorder built before it counted none.
        unknown = getattr(recorder, "SupervisionUnpaired", None)
        if unknown is not None:
            (logger.warning if unknown else logger.info)(
                "supervision        %s captures carry their frame's supervision, %s say it was unknown",
                recorder.SupervisionPaired, unknown)
        # Each vehicle in the picture carries its lights and, drawn by the drive, its pose source from its
        # frame's snapshot; a capture whose snapshot carried neither -- a server built before it did -- says
        # so and carries none, which is truth missing, and is said louder. A recorder built before it counted
        # none.
        lights_unknown = getattr(recorder, "LightsUnknown", None)
        if lights_unknown is not None:
            poses_unknown = recorder.PoseSourceUnknown
            (logger.warning if lights_unknown or poses_unknown else logger.info)(
                "lights, poses      %s captures say their vehicles' lights were unknown, %s say their "
                "pose source was", lights_unknown, poses_unknown)
        if recorder.ChecksSensorPose:
            # A capture is placed where its frame's snapshot holds the camera, and its image header is
            # checked against that. A header that disagreed is a server stamping the image's pose after
            # the frame, which the snapshot's pose has covered for but the server should not do, so it
            # is said louder.
            (logger.warning if recorder.SensorPoseHeaderDisagreed else logger.info)(
                "sensor pose        %s captures placed from their own frame's snapshot, %s of them with "
                "an image header that disagreed; %s placed from the header because their frame was no "
                "longer held", recorder.SensorPoseFromSnapshot, recorder.SensorPoseHeaderDisagreed,
                recorder.SensorPoseFromHeader)
        if recorder.MeasuresOcclusion:
            # A capture the depth camera could not be paired with carries no occlusion, which a
            # consumer counting unoccluded vehicles has to leave out, so any such capture is said louder.
            (logger.warning if recorder.OcclusionUnmatched else logger.info)(
                "occlusion          measured on %s captures, not on %s (%s with no depth frame, %s with "
                "the depth stream out of step, %s with the cameras at different poses)",
                recorder.OcclusionMeasured, recorder.OcclusionUnmatched,
                recorder.OcclusionNoDepthCaptures, recorder.OcclusionDepthOutOfStep,
                recorder.OcclusionDepthWrongPose)
            if recorder.ChecksDepthPose:
                (logger.warning if recorder.OcclusionDepthPoseHeaderDisagreed else logger.info)(
                    "depth pose         %s depth captures projected from their own frame's snapshot, "
                    "%s of them with a header that disagreed; %s from the header",
                    recorder.OcclusionDepthPoseFromSnapshot, recorder.OcclusionDepthPoseHeaderDisagreed,
                    recorder.OcclusionDepthPoseFromHeader)
