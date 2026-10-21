#!/usr/bin/env python3
"""carla-drive's in-view feed follows a named camera another client flies, lets it go, and finds it again.

Server state it needs: a headless CARLA server with Shahid_Bahonar_Port loaded, built with the orbit
mover so it flies a camera's orbit itself, with no drive holding the world's drive lease and no live
camera named Cot_Orbit_Probe_1, and the carlanet wheel built from this checkout.

What it does, from this checkout:

  1. Spawns a camera named Cot_Orbit_Probe_1 and has the server fly it round an orbit over the port
     gate, as carla-camera-follower does.
  2. Starts carla-drive in a process of its own (`CarlaNet/python/run_sumo_drive.py`) at the pace of
     real traffic, sending only the vehicles in that camera's picture to a UDP listener on 127.0.0.1.
  3. Once the drive follows the camera and datagrams have arrived, destroys the camera. Once the drive
     says the camera has gone, waits a few seconds and spawns a camera under the same name, flown the
     same way. Then it lets the drive run to its last step.
  4. Checks that the drive said it followed the camera, that it had gone and that it followed the new
     one, each once and in that order; that no datagram arrived while no camera was followed, and that
     datagrams arrived again after; and that the drive exited with status 0.

It never stops the drive: a drive killed part-way leaves the world synchronous and its drive lease
held. Ctrl+C in this console reaches both processes, and each gives back what it holds. The camera
this spawns is destroyed in `finally`, once the drive has exited.

Usage, from the checkout's root:
    python CarlaNet/python/test_drive_cot_named_camera_live.py [--host h] [--port p] [--warm-up S]
        [--steps N] [--orbit-centre X Y] [--orbit-centre-z-m Z] [-- further carla-drive options]
"""
import argparse
import logging
import socket
import subprocess
import sys
import threading
import time
from pathlib import Path

import carlanet as carla

_REPO = Path(__file__).resolve().parents[2]
DRIVE = _REPO / "CarlaNet" / "python" / "run_sumo_drive.py"
BAHONAR = _REPO / "CarlaControl" / "Examples" / "Scenarios" / "Bahonar"
SCENARIO = BAHONAR / "Shahid_Bahonar_Port_PatternOfLife" / "Shahid_Bahonar_Port_PatternOfLife.sumocfg"
WORLD_PACKAGE = BAHONAR / "Shahid_Bahonar_Port.cwp"
EPOCH = BAHONAR / "Shahid_Bahonar_Port_PatternOfLife.scenario.json"

NAME = "Cot_Orbit_Probe_1"
# The port gate area's centre in CARLA metres, as the package's areas.resolved.json gives it.
GATE = (1998.4, -1035.0)
# Datagrams that must arrive from the first camera before it is destroyed.
DATAGRAMS_FIRST = 3
# How long the camera stays gone before another is spawned under its name.
GONE_FOR_S = 5.0
# How long to wait for each line the drive is to log, once the drive is past its fast-forward.
LINE_WAIT_S = 60.0


class _Listener:
    """Receives every datagram sent to an ephemeral port on 127.0.0.1, with the wall clock it arrived at."""

    def __init__(self) -> None:
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.sock.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 8 * 1024 * 1024)
        self.sock.bind(("127.0.0.1", 0))
        self.sock.settimeout(0.2)
        self.port = self.sock.getsockname()[1]
        self.arrivals: list[float] = []
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._receive, daemon=True)
        self._thread.start()

    def _receive(self) -> None:
        while not self._stop.is_set():
            try:
                self.sock.recvfrom(65535)
            except TimeoutError:
                continue
            except OSError:
                return
            self.arrivals.append(time.monotonic())

    def close(self) -> None:
        self._stop.set()
        self._thread.join()
        self.sock.close()


class _DriveOutput:
    """The drive's console, line by line, each with the wall clock it was read at, echoed here."""

    def __init__(self, process: subprocess.Popen, logger: logging.Logger) -> None:
        self.process = process
        self.logger = logger
        self.lines: list[tuple[float, str]] = []
        self._thread = threading.Thread(target=self._read, daemon=True)
        self._thread.start()

    def _read(self) -> None:
        for line in self.process.stdout:
            line = line.rstrip("\n")
            self.lines.append((time.monotonic(), line))
            self.logger.info("drive| %s", line)

    def when(self, beginning: str) -> float | None:
        """When the first line beginning so was read, or None."""
        return next((at for at, line in self.lines if line.startswith(beginning)), None)

    def count(self, beginning: str) -> int:
        return sum(line.startswith(beginning) for _, line in self.lines)

    def wait_for(self, beginning: str, seconds: float | None) -> float | None:
        """Wait for a line beginning so while the drive runs, at most `seconds` (None: no limit)."""
        deadline = None if seconds is None else time.monotonic() + seconds
        while (deadline is None or time.monotonic() < deadline) and self.process.poll() is None:
            at = self.when(beginning)
            if at is not None:
                return at
            time.sleep(0.1)
        return self.when(beginning)

    def join(self) -> None:
        self._thread.join()


class DriveCotNamedCameraLiveCheck:
    """Flies a named camera, runs an in-view drive, and takes the camera away and back."""

    def __init__(self, args: argparse.Namespace, logger: logging.Logger) -> None:
        self.args = args
        self.logger = logger
        self.failures: list[str] = []
        self.client = carla.Client(args.host, args.port)
        self.client.set_timeout(30.0)
        self.world = self.client.get_world()
        self.camera = None

    def check(self, passed: bool, what: str) -> None:
        self.logger.info("%-96s %s", what, "ok" if passed else "FAIL")
        if not passed:
            self.failures.append(what)

    def _spawn_camera(self):
        """A camera under the name, flown round the gate by the server."""
        centre_x, centre_y = self.args.orbit_centre
        blueprint = self.world.get_blueprint_library().find("sensor.camera.rgb")
        blueprint.set_attribute("image_size_x", "1280")
        blueprint.set_attribute("image_size_y", "720")
        blueprint.set_attribute("fov", "90")
        transform = carla.Transform(
            carla.Location(x=centre_x, y=centre_y, z=self.args.orbit_centre_z_m + 365.76),
            carla.Rotation(pitch=-90.0))
        camera = self.world.spawn_camera(blueprint, transform, name=NAME)
        self.camera = camera
        camera.set_orbit((centre_x, centre_y, self.args.orbit_centre_z_m), 200.0, 365.76, 240.0)
        self.logger.info("spawned camera %s, actor %d, orbiting (%.1f, %.1f)", NAME, camera.id,
                         centre_x, centre_y)
        return camera

    def _destroy_camera(self) -> None:
        if self.camera is None:
            return
        camera, self.camera = self.camera, None
        try:
            camera.destroy()
            self.logger.info("destroyed camera actor %d", camera.id)
        except Exception as failure:  # noqa: BLE001 - cleanup reports and continues
            self.logger.warning("could not destroy camera %d: %r", camera.id, failure)

    def _drive_command(self, port: int) -> list[str]:
        return [sys.executable, "-u", str(DRIVE), "--host", self.args.host,
                "--port", str(self.args.port), "--scenario", str(SCENARIO),
                "--world-package", str(WORLD_PACKAGE), "--epoch", str(EPOCH), "--no-record",
                "--real-time-factor", "1.0", "--warm-up", str(self.args.warm_up),
                "--steps", str(self.args.steps), "--cot-udp", f"127.0.0.1:{port}",
                "--cot-emit", "in-view", "--cot-camera", NAME, *self.args.drive_args]

    def _take_the_camera_away_and_back(self, output: _DriveOutput, listener: _Listener):
        """Destroy the camera once the drive follows it and datagrams have come, and spawn another
        under its name once the drive says it has gone. Returns when it went and came back, by the
        drive's own lines, as far as it got."""
        # The fast-forward to --warm-up comes first, and how long it takes is not known.
        followed = output.wait_for(f"cot camera {NAME} followed: ", None)
        if followed is None:
            return None, None
        deadline = time.monotonic() + LINE_WAIT_S
        while len(listener.arrivals) < DATAGRAMS_FIRST and time.monotonic() < deadline \
                and output.process.poll() is None:
            time.sleep(0.1)
        if len(listener.arrivals) < DATAGRAMS_FIRST:
            self.logger.error("the camera saw %d datagrams' worth of vehicles; give an --orbit-centre "
                              "over traffic", len(listener.arrivals))
            return None, None
        self._destroy_camera()
        gone = output.wait_for(f"cot camera {NAME} gone: ", LINE_WAIT_S)
        if gone is None:
            return None, None
        time.sleep(GONE_FOR_S)
        self._spawn_camera()
        again = output.wait_for(f"cot camera {NAME} followed again: ", LINE_WAIT_S)
        return gone, again

    def run(self) -> int:
        listener = _Listener()
        process = None
        output = None
        try:
            self._spawn_camera()
            command = self._drive_command(listener.port)
            self.logger.info("%s", " ".join(command))
            process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                       text=True, bufsize=1, cwd=str(_REPO))
            output = _DriveOutput(process, self.logger)
            gone, again = self._take_the_camera_away_and_back(output, listener)
            status = process.wait()
            output.join()
            self._judge(output, listener, status, gone, again)
        finally:
            # Ctrl+C reaches the drive too, which then gives the world back: the camera is destroyed
            # once it has, however many times Ctrl+C is pressed meanwhile.
            while process is not None and process.poll() is None:
                try:
                    process.wait()
                except KeyboardInterrupt:
                    self.logger.warning("waiting for the drive to give the world back")
            self._destroy_camera()
            listener.close()
        for failure in self.failures:
            self.logger.error("FAIL: %s", failure)
        if self.failures:
            return 1
        self.logger.info("PASS")
        return 0

    def _judge(self, output: _DriveOutput, listener: _Listener, status: int, gone, again) -> None:
        self.check(status == 0, f"the drive ended as asked (exit status {status})")
        lines = [f"cot camera {NAME} followed: ", f"cot camera {NAME} gone: ",
                 f"cot camera {NAME} followed again: "]
        for beginning in lines:
            self.check(output.count(beginning) == 1, f"the drive said '{beginning.strip()}' once")
        times = [output.when(beginning) for beginning in lines]
        self.check(None not in times and times == sorted(times),
                   "it said followed, gone and followed again in that order")
        if gone is None or again is None:
            self.check(False, "the camera went and came back while the drive ran; give more --steps")
            return
        # From just after the drive said the camera had gone: the datagrams of the step it went on were
        # sent before that line.
        during = [at for at in listener.arrivals if gone + 0.5 < at < again]
        after = [at for at in listener.arrivals if at > again]
        self.check(not during, f"no datagram arrived while no camera was followed ({len(during)} did)")
        self.check(bool(after), f"datagrams arrived again once the new camera was followed "
                                f"({len(after)} did)")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=2000)
    parser.add_argument("--warm-up", type=float, default=271169.0,
                        help="simulated second the drive fast-forwards SUMO to (default 271169)")
    parser.add_argument("--steps", type=int, default=60,
                        help="SUMO steps the drive runs, one per second of wall clock (default 60)")
    parser.add_argument("--orbit-centre", type=float, nargs=2, metavar=("X", "Y"), default=GATE,
                        help="the point the camera circles and looks at, CARLA metres (default: the "
                             "port gate)")
    parser.add_argument("--orbit-centre-z-m", type=float, default=0.0, metavar="Z",
                        help="the height of that point, CARLA metres (default 0)")
    parser.add_argument("drive_args", nargs="*",
                        help="further carla-drive options, after --, such as --sumo-home DIR")
    args = parser.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(message)s", stream=sys.stdout)
    return DriveCotNamedCameraLiveCheck(args, logging.getLogger("test_drive_cot_named_camera_live")).run()


if __name__ == "__main__":
    sys.exit(main())
