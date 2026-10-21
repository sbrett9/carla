#!/usr/bin/env python3
"""carla-drive holds the pace of real traffic with its live feed on as it does with it off.

Server state it needs: a headless CARLA server with Shahid_Bahonar_Port loaded, with no drive holding
the world's drive lease and no live camera named Cot_Pace_1, and the carlanet wheel built from this
checkout.

What it does, from this checkout: runs carla-drive three times in a process of its own
(`CarlaNet/python/run_sumo_drive.py`), each at `--real-time-factor 1.0 --warm-up 271169 --steps 120
--no-record`, one after the other:

  1. without the feed;
  2. with the feed, every vehicle, to a UDP listener on 127.0.0.1;
  3. with the feed in view of a camera named Cot_Pace_1 that this script spawns over the port gate,
     looking down, and destroys in `finally` once the drives have exited.

For each run it prints the closing pace line, the pace over the whole run, the worst window, the
seconds behind schedule and, with the feed, the feed's drain seconds per drain, on average and at
worst. The proposed acceptance, which it checks: the pace with the feed is within 1% of the pace
without it, and the seconds behind schedule with the feed are no more than without it, give or take
--behind-tolerance-s.

It never stops a drive: a drive killed part-way leaves the world synchronous and its drive lease
held. Ctrl+C in this console reaches the drive too, and each gives back what it holds.

Usage, from the checkout's root:
    python CarlaNet/python/test_drive_cot_pace.py [--host h] [--port p] [--warm-up S] [--steps N]
        [--behind-tolerance-s S] [-- further carla-drive options]
"""
import argparse
import logging
import re
import socket
import subprocess
import sys
import threading
from pathlib import Path

import carlanet as carla

_REPO = Path(__file__).resolve().parents[2]
DRIVE = _REPO / "CarlaNet" / "python" / "run_sumo_drive.py"
BAHONAR = _REPO / "CarlaControl" / "Examples" / "Scenarios" / "Bahonar"
SCENARIO = BAHONAR / "Shahid_Bahonar_Port_PatternOfLife" / "Shahid_Bahonar_Port_PatternOfLife.sumocfg"
WORLD_PACKAGE = BAHONAR / "Shahid_Bahonar_Port.cwp"
EPOCH = BAHONAR / "Shahid_Bahonar_Port_PatternOfLife.scenario.json"

NAME = "Cot_Pace_1"
# The port gate area's centre in CARLA metres, as the package's areas.resolved.json gives it.
GATE = (1998.4, -1035.0)

# The drive's closing line (DriveRun._report) and its feed's summary (DriveCotFeed._summarise).
CLOSING = re.compile(r"^(\d+) SUMO steps in ([\d.]+) s wall clock; pace (.*?): (.*)$")
WHOLE_RUN = re.compile(r"([\d.]+)x over the whole run")
WORST_WINDOW = re.compile(r"worst window ([\d.]+)x")
BEHIND = re.compile(r"([\d.]+) s behind schedule")
DRAINED = re.compile(r"^cot feed\s+(\d+) samples, (\d+) events sent .* drained in ([\d.]+) s on "
                     r"average, ([\d.]+) s at worst")


class _Sink:
    """Takes every datagram sent to an ephemeral port on 127.0.0.1, so no send meets a closed port."""

    def __init__(self) -> None:
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.sock.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 8 * 1024 * 1024)
        self.sock.bind(("127.0.0.1", 0))
        self.sock.settimeout(0.2)
        self.port = self.sock.getsockname()[1]
        self.received = 0
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
            self.received += 1

    def close(self) -> None:
        self._stop.set()
        self._thread.join()
        self.sock.close()


class DriveCotPaceCheck:
    """Runs the same paced drive without the feed, with it, and with it in view, and compares the pace."""

    def __init__(self, args: argparse.Namespace, logger: logging.Logger) -> None:
        self.args = args
        self.logger = logger
        self.failures: list[str] = []
        self.client = None
        self.camera = None

    def check(self, passed: bool, what: str) -> None:
        self.logger.info("%-96s %s", what, "ok" if passed else "FAIL")
        if not passed:
            self.failures.append(what)

    def _spawn_camera(self) -> None:
        """A camera over the gate looking straight down, which only the in-view run follows."""
        self.client = carla.Client(self.args.host, self.args.port)
        self.client.set_timeout(30.0)
        world = self.client.get_world()
        blueprint = world.get_blueprint_library().find("sensor.camera.rgb")
        blueprint.set_attribute("image_size_x", "1280")
        blueprint.set_attribute("image_size_y", "720")
        blueprint.set_attribute("fov", "90")
        self.camera = world.spawn_camera(
            blueprint, carla.Transform(carla.Location(x=GATE[0], y=GATE[1], z=365.76),
                                       carla.Rotation(pitch=-90.0)), name=NAME)
        self.logger.info("spawned camera %s, actor %d", NAME, self.camera.id)

    def _destroy_camera(self) -> None:
        if self.camera is None:
            return
        camera, self.camera = self.camera, None
        try:
            camera.destroy()
            self.logger.info("destroyed camera actor %d", camera.id)
        except Exception as failure:  # noqa: BLE001 - cleanup reports and continues
            self.logger.warning("could not destroy camera %d: %r", camera.id, failure)

    def _drive(self, label: str, *feed: str) -> dict:
        """Run one drive to its end and read its closing figures."""
        command = [sys.executable, "-u", str(DRIVE), "--host", self.args.host,
                   "--port", str(self.args.port), "--scenario", str(SCENARIO),
                   "--world-package", str(WORLD_PACKAGE), "--epoch", str(EPOCH), "--no-record",
                   "--real-time-factor", "1.0", "--warm-up", str(self.args.warm_up),
                   "--steps", str(self.args.steps), *feed, *self.args.drive_args]
        self.logger.info("%s: %s", label, " ".join(command))
        process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                   text=True, bufsize=1, cwd=str(_REPO))
        figures: dict = {"label": label}
        try:
            for line in process.stdout:
                line = line.rstrip("\n")
                self.logger.info("drive| %s", line)
                self._read(line, figures)
        finally:
            # Ctrl+C reaches the drive too, which then gives the world back: wait for it whatever
            # happens here meanwhile.
            while process.poll() is None:
                try:
                    process.wait()
                except KeyboardInterrupt:
                    self.logger.warning("waiting for the drive to give the world back")
        figures["status"] = process.returncode
        return figures

    @staticmethod
    def _read(line: str, figures: dict) -> None:
        closing = CLOSING.match(line.strip())
        if closing:
            figures["closing"] = line.strip()
            achieved = closing.group(4)
            for name, pattern in (("whole", WHOLE_RUN), ("worst", WORST_WINDOW), ("behind", BEHIND)):
                found = pattern.search(achieved)
                figures[name] = float(found.group(1)) if found else None
        drained = DRAINED.match(line.strip())
        if drained:
            figures["samples"], figures["events"] = int(drained.group(1)), int(drained.group(2))
            figures["drain_mean"], figures["drain_worst"] = (float(drained.group(3)),
                                                             float(drained.group(4)))

    def _say(self, figures: dict) -> None:
        self.logger.info("%s: %s", figures["label"], figures.get("closing", "no closing line"))
        self.logger.info("%s: pace %s over the whole run, worst window %s, %s s behind schedule",
                         figures["label"], figures.get("whole"), figures.get("worst"),
                         figures.get("behind"))
        if "drain_mean" in figures:
            self.logger.info("%s: %d samples, %d events; drained in %.4f s on average, %.4f s at worst",
                             figures["label"], figures["samples"], figures["events"],
                             figures["drain_mean"], figures["drain_worst"])

    def run(self) -> int:
        sink = _Sink()
        runs = []
        try:
            runs.append(self._drive("without the feed"))
            runs.append(self._drive("all", "--cot-udp", f"127.0.0.1:{sink.port}"))
            self._spawn_camera()
            runs.append(self._drive("in-view", "--cot-udp", f"127.0.0.1:{sink.port}",
                                    "--cot-emit", "in-view", "--cot-camera", NAME))
        finally:
            self._destroy_camera()
            sink.close()
        for figures in runs:
            self._say(figures)
            self.check(figures["status"] == 0 and figures.get("whole") is not None,
                       f"{figures['label']}: the drive ended as asked and said its pace "
                       f"(exit status {figures['status']})")
        baseline = runs[0]
        for figures in runs[1:]:
            if baseline.get("whole") is None or figures.get("whole") is None:
                continue
            drift = abs(figures["whole"] - baseline["whole"]) / baseline["whole"]
            self.check(drift <= 0.01, f"{figures['label']}: the pace is within 1% of the pace without "
                                      f"the feed ({figures['whole']:.3f}x against "
                                      f"{baseline['whole']:.3f}x)")
            if baseline.get("behind") is not None and figures.get("behind") is not None:
                self.check(figures["behind"] <= baseline["behind"] + self.args.behind_tolerance_s,
                           f"{figures['label']}: no further behind schedule than without the feed "
                           f"({figures['behind']:.2f} s against {baseline['behind']:.2f} s)")
        for failure in self.failures:
            self.logger.error("FAIL: %s", failure)
        if self.failures:
            return 1
        self.logger.info("PASS")
        return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=2000)
    parser.add_argument("--warm-up", type=float, default=271169.0,
                        help="simulated second each drive fast-forwards SUMO to (default 271169)")
    parser.add_argument("--steps", type=int, default=120,
                        help="SUMO steps each drive runs, one per second of wall clock (default 120)")
    parser.add_argument("--behind-tolerance-s", type=float, default=0.25, metavar="S",
                        help="how much further behind schedule a run with the feed may end than the "
                             "run without it (default 0.25)")
    parser.add_argument("drive_args", nargs="*",
                        help="further carla-drive options, after --, such as --sumo-home DIR")
    args = parser.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(message)s", stream=sys.stdout)
    return DriveCotPaceCheck(args, logging.getLogger("test_drive_cot_pace")).run()


if __name__ == "__main__":
    sys.exit(main())
