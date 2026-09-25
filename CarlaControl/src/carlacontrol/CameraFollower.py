"""A viewer process that places one camera in a running world and shows its picture live."""

from __future__ import annotations

import logging
import signal
import threading
import time
from collections.abc import Callable
from typing import Any

import carlanet as carla

from .ChannelDescription import ChannelDescription
from .FollowerCamera import FollowerCamera
from .FollowerWindow import FollowerWindow
from .FrameStallWatch import FrameStallWatch
from .OrbitSensorController import OrbitSensorController
from .StareAim import StareAim


class CameraFollower:
    """Watches a world through one camera of its own, and changes nothing else in it.

    It is a *camera follower* in the sense of the capture plan's authority table
    (`01_Architecture.md` section 4.1 and D1.1): it follows the world's clock and never cues it.
    Whoever owns the clock -- `run_sumo_drive.py` while SUMO drives -- decides when the world
    advances, and this process only sees the frames that result. In every state it:

    * never calls `world.tick()` and never writes episode settings;
    * never touches the sun, the weather, layer visibility or the map;
    * never creates a traffic manager;
    * spawns one RGB camera and, on the way out, destroys that camera and nothing else.

    It works under either kind of world. Under a synchronous one a frame arrives each time the
    clock owner ticks; under an asynchronous one frames arrive at the server's own rate; and a
    drive starting or stopping underneath it only changes which of the two it is seeing. When no
    frame has arrived for `stall_after_s` it says so once, and again once when frames resume,
    rather than sitting on a picture that looks frozen.

    The overlay carries only what the frame itself carries -- its frame number and the simulated
    time stamped on it -- plus the channel's `sensor_id` and pattern. It computes no figures about
    the run: a monitor that computes its own numbers can disagree with the record
    (`12_Operator_Control_Surface.md` D12.14).

    An orbit is flown by `OrbitSensorController`, unchanged: on its own thread, advancing by wall
    clock, as it does in `run_SCTMV.py`. Under a synchronous world the camera therefore moves
    between ticks by however much wall time passed, which is smooth when the drive is paced to real
    time and jumpy when it is not.

    **It records nothing.** The annotation registry is process-local, so a recorder in a follower
    process would read an empty registry and write every vehicle as unlabelled
    (`08_Collection_And_EPoL.md` section 3.4). That section rules that annotation state is to be
    published to the server (D8.3), and nothing publishes it yet; recording from a follower waits
    until something does. Until then, record from the process that drives the world.

    A map load removes every actor, this camera included; restart the follower after one.
    """

    STALL_AFTER_S = 3.0
    STOP_SIGNALS = ("SIGINT", "SIGTERM", "SIGBREAK")

    def __init__(
        self,
        client: Any,
        channel: ChannelDescription,
        window: Any = None,
        clock: Callable[[], float] = time.monotonic,
        stall_after_s: float = STALL_AFTER_S,
        logger: logging.Logger | None = None,
    ) -> None:
        """
        Args:
            client: A connected `carlanet.Client`.
            channel: The camera to place and how it moves.
            window: Where the picture is shown; a `FollowerWindow` by default.
            clock: A monotonic wall clock in seconds, for noticing that frames have stopped.
            stall_after_s: How long without a frame before saying so, seconds.
            logger: Where to report; this module's logger by default.
        """
        self.client = client
        self.channel = channel
        self.window = window if window is not None else FollowerWindow()
        self.clock = clock
        self.stall_after_s = stall_after_s
        self.logger = logger or logging.getLogger(__name__)
        self.camera: FollowerCamera | None = None
        self.orbit: OrbitSensorController | None = None
        self._stop = threading.Event()

    def request_stop(self) -> None:
        """Ask the display loop to finish; safe from a signal handler or another thread."""
        self._stop.set()

    def run(self) -> int:
        """Place the camera, show its picture until told to stop, then remove the camera.

        Returns:
            0 when the viewer was closed or interrupted, 1 when the world could not be reached or
            the camera could not be placed.
        """
        previous_handlers = self._install_signal_handlers()
        try:
            return self._run()
        except KeyboardInterrupt:
            # Only reachable where the stop signals could not be taken over (not the main thread).
            self.logger.info("interrupted")
            return 0
        finally:
            self._restore_signal_handlers(previous_handlers)

    def overlay_lines(self, image: Any) -> list[str]:
        """The text drawn over a frame: the channel's identity, and what the frame carries."""
        return [
            f"{self.sensor_label()}   {self.channel.pattern}",
            f"frame {image.frame}   sim t {image.timestamp:.3f} s",
        ]

    def sensor_label(self) -> str:
        """The channel's `sensor_id`, or the camera's actor id where none was given."""
        if self.channel.sensor_id is not None:
            return self.channel.sensor_id
        if self.camera is not None:
            return f"unnamed camera, actor {self.camera.id}"
        return "unnamed camera"

    def _run(self) -> int:
        try:
            world = self.client.get_world()
        except Exception as failure:
            self.logger.error("could not reach the CARLA server: %r", failure)
            return 1
        self._log_world_mode(world)

        self.window.open(self.channel.width, self.channel.height,
                         f"camera follower - {self.sensor_label()} ({self.channel.pattern})")
        try:
            try:
                self.camera = FollowerCamera(world, self.channel, self._start_transform(),
                                             logger=self.logger)
            except Exception as failure:
                self.logger.error("could not place the camera: %r", failure)
                return 1
            if self.channel.pattern == ChannelDescription.ORBIT:
                self._start_orbit()
            self._show_until_stopped()
            return 0
        finally:
            self._shut_down()

    def _start_transform(self) -> carla.Transform:
        """Where the camera is spawned: its stare pose, or above an orbit's centre."""
        channel = self.channel
        if channel.pattern == ChannelDescription.STARE:
            aim = StareAim.from_channel(channel)
            self.logger.info("stare: camera at %s", aim.describe())
            return carla.Transform(carla.Location(x=aim.x_m, y=aim.y_m, z=aim.z_m),
                                   carla.Rotation(pitch=aim.pitch_deg, yaw=aim.yaw_deg, roll=0.0))
        # Spawned looking down over the centre; the orbit moves it onto its circle at once.
        return carla.Transform(
            carla.Location(x=channel.orbit_centre_x_m, y=channel.orbit_centre_y_m,
                           z=channel.orbit_centre_z_m + channel.orbit_altitude_m),
            carla.Rotation(pitch=-90.0, yaw=0.0, roll=0.0))

    def _start_orbit(self) -> None:
        channel = self.channel
        self.orbit = OrbitSensorController(self.camera, world=None, logger=self.logger)
        self.orbit.set_orbit_params(
            center_x=channel.orbit_centre_x_m,
            center_y=channel.orbit_centre_y_m,
            center_z=channel.orbit_centre_z_m,
            radius=channel.orbit_radius_m,
            altitude=channel.orbit_altitude_m,
            speed=channel.orbit_period_s,
        )
        self.orbit.orbit_description = (
            f"centre ({channel.orbit_centre_x_m:.1f}, {channel.orbit_centre_y_m:.1f}, "
            f"{channel.orbit_centre_z_m:.1f}) m, radius {channel.orbit_radius_m:.1f} m, "
            f"{channel.orbit_altitude_m:.1f} m above the centre, "
            f"{channel.orbit_period_s:.0f} s per revolution")
        self.orbit.start_updater()
        self.orbit.set_enabled(True)

    def _show_until_stopped(self) -> None:
        watch = FrameStallWatch(self.stall_after_s, self.clock)
        shown = 0
        waiting_drawn = False
        while not self._stop.is_set():
            if self.window.quit_requested():
                self.logger.info("window closed")
                break
            image, received = self.camera.latest()
            if image is not None and received != shown:
                self.window.show_frame(image, self.overlay_lines(image))
                shown = received
            elif image is None and not waiting_drawn:
                self.window.show_message([f"{self.sensor_label()}   {self.channel.pattern}",
                                          "waiting for the first frame"])
                waiting_drawn = True
            self._report(watch.observe(received), received, watch)
            self.window.pace()

    def _report(self, event: str | None, received: int, watch: FrameStallWatch) -> None:
        if event == FrameStallWatch.STALLED:
            waited = "since the camera was placed" if received == 0 else "since the last one"
            self.logger.warning(
                "no frame for %.1f s %s. A synchronous world delivers frames only when whoever "
                "owns its clock ticks it -- a drive that has stopped, or is fast-forwarding SUMO "
                "before its first tick, delivers none. An asynchronous world that delivers none has "
                "stopped rendering. This viewer never ticks the world; still waiting.",
                watch.quiet_for_s(), waited)
        elif event == FrameStallWatch.RESUMED:
            self.logger.info("frames arriving again")

    def _log_world_mode(self, world: Any) -> None:
        """Say which kind of world this is, read once; nothing here ever changes it."""
        try:
            settings = world.get_settings()
        except Exception as failure:
            self.logger.warning("could not read the world's settings: %r", failure)
            return
        if settings.synchronous_mode:
            self.logger.info("the world is synchronous: a frame arrives each time whoever owns its "
                             "clock ticks it")
        else:
            self.logger.info("the world is running free: frames arrive at the server's own rate")

    def _shut_down(self) -> None:
        """Stop the orbit before the camera goes, so nothing moves a camera that is gone."""
        if self.orbit is not None:
            try:
                self.orbit.stop_updater()
            except Exception as failure:
                self.logger.warning("could not stop the orbit: %r", failure)
        if self.camera is not None:
            self.camera.destroy()
        try:
            self.window.close()
        except Exception as failure:
            self.logger.warning("could not close the window: %r", failure)

    def _on_signal(self, signum: int, _frame: Any) -> None:
        self.logger.info("signal %d received; closing", signum)
        self.request_stop()

    def _install_signal_handlers(self) -> dict[int, Any]:
        """Turn the stop signals into a request to stop, so the camera is always cleaned up."""
        previous: dict[int, Any] = {}
        for name in self.STOP_SIGNALS:
            signum = getattr(signal, name, None)
            if signum is None:
                continue
            try:
                previous[signum] = signal.signal(signum, self._on_signal)
            except (ValueError, OSError):
                # Not the main thread, or a signal this platform will not hand over.
                continue
        return previous

    @staticmethod
    def _restore_signal_handlers(previous: dict[int, Any]) -> None:
        for signum, handler in previous.items():
            try:
                signal.signal(signum, handler)
            except (ValueError, OSError, TypeError):
                continue
