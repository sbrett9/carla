#!/usr/bin/env python3
"""carla-drive's live feed sends each vehicle's truth sidecar event, all of them or the ones in view.

Server state it needs: a headless CARLA server with Shahid_Bahonar_Port loaded and no drive holding
the world's drive lease, and the carlanet wheel built from this checkout. The drives it runs take the
world's clock while they run and give it back as they end.

What it does, from this checkout:

  1. Runs carla-drive in this process (`carlacontrol.commands.drive.main`) for a few SUMO steps of the
     Bahonar pattern of life, recording its own fixed camera, Cot_Parity_1, at 20 Hz, so that every
     frame the feed samples has a capture, and sending the feed to a UDP listener on 127.0.0.1. For
     each datagram, the sidecar stamped with the same time holds an event with the same uid, and every
     attribute the datagram carries equals that event's, but the type of a vehicle whose population the
     display convention draws other than n, which the sidecar draws n. The datagram lacks exactly the
     withheld attributes. Each sampled frame's uids are its sidecar's vehicles, the sidecar listing the
     vehicles its frame rendered. Every datagram is valid against cot_telemetry.xsd, every one sent is
     received, and the largest datagram's size is printed.
  2. Runs the same drive with `--cot-emit in-view --cot-camera Cot_Parity_1`. Each sampled frame's
     uids are its sidecar's vehicles whose in_frame is wholly or partly and that are not wholly beyond
     a draw distance. The test and the sidecar share the function, so this checks that the inputs agree
     (the frame, the camera's pose, its optics) and that the events are delivered.

It spawns nothing: each drive removes its own cameras. The captures are written to a temporary folder,
removed at the end unless --keep is given. Each drive fast-forwards SUMO to --warm-up before its first
tick, which takes a while at the default.

Usage, from the checkout's root:
    python CarlaNet/python/test_drive_cot_feed_live.py [--host h] [--port p] [--warm-up S]
        [--steps N] [--keep] [-- further carla-drive options]
"""
import argparse
import json
import logging
import shutil
import socket
import sys
import tempfile
import threading
import time
import xml.etree.ElementTree as ET
from collections import defaultdict
from pathlib import Path

from lxml import etree

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.commands import drive  # noqa: E402  (needs the path above)

BAHONAR = _REPO / "CarlaControl" / "Examples" / "Scenarios" / "Bahonar"
SCENARIO = BAHONAR / "Shahid_Bahonar_Port_PatternOfLife" / "Shahid_Bahonar_Port_PatternOfLife.sumocfg"
ROUTES = SCENARIO.with_suffix(".rou.xml")
CONVENTION = SCENARIO.with_name("Shahid_Bahonar_Port_PatternOfLife.display.json")
WORLD_PACKAGE = BAHONAR / "Shahid_Bahonar_Port.cwp"
EPOCH = BAHONAR / "Shahid_Bahonar_Port_PatternOfLife.scenario.json"
STREAM_SCHEMA = _REPO / "CarlaControl" / "schemas" / "cot_telemetry.xsd"

CAMERA = "Cot_Parity_1"
UID_PREFIX = "CARLA-TRUTH-SUMO-"
CLASS_PARAMETER = "carla:class_id"
# The _carla attributes a sidecar's vehicle event may carry and a datagram never does.
WITHHELD = frozenset({
    "pitch_deg", "roll_deg", "in_frame", "apparent_width_px", "apparent_height_px", "occlusion",
    "occlusion_level", "occlusion_samples", "occlusion_unmeasured", "box_px", "box_oriented_px",
    "truncation", "lights", "pose_source", "beyond_draw_distance", "camera_range_m", "vtype_id",
    "sumo_angle_deg"})
# The only children of a datagram's <detail>.
DATAGRAM_DETAIL = ["track", "contact", "_carla"]
IN_PICTURE = {"wholly", "partly"}


class _Listener:
    """Receives every datagram sent to an ephemeral port on 127.0.0.1, on a thread of its own."""

    def __init__(self) -> None:
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        # A sample goes out as a burst of a datagram per vehicle; a large buffer keeps a burst whole.
        self.sock.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 8 * 1024 * 1024)
        self.sock.bind(("127.0.0.1", 0))
        self.sock.settimeout(0.2)
        self.port = self.sock.getsockname()[1]
        self.datagrams: list[bytes] = []
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._receive, daemon=True)
        self._thread.start()

    def _receive(self) -> None:
        while not self._stop.is_set():
            try:
                data, _ = self.sock.recvfrom(65535)
            except TimeoutError:
                continue
            except OSError:
                return
            self.datagrams.append(data)

    def take(self) -> list[bytes]:
        """Every datagram received, once none has arrived for half a second: the drive's last sends may
        still be on their way when it returns."""
        count = -1
        while count != len(self.datagrams):
            count = len(self.datagrams)
            time.sleep(0.5)
        taken, self.datagrams = self.datagrams, []
        return taken

    def close(self) -> None:
        self._stop.set()
        self._thread.join()
        self.sock.close()


class _LogLines(logging.Handler):
    """Every line the drive logs, kept so its summary can be read."""

    def __init__(self) -> None:
        super().__init__(logging.INFO)
        self.lines: list[str] = []

    def emit(self, record: logging.LogRecord) -> None:
        self.lines.append(record.getMessage())


class DriveCotFeedLiveCheck:
    """Runs carla-drive twice in this process and holds its feed to the sidecars of the same frames."""

    def __init__(self, args: argparse.Namespace, logger: logging.Logger) -> None:
        self.args = args
        self.logger = logger
        self.failures: list[str] = []
        self.schema = etree.XMLSchema(etree.parse(str(STREAM_SCHEMA)))
        self.letter_of_type = self._letters_by_type()

    def check(self, passed: bool, what: str) -> None:
        self.logger.info("%-96s %s", what, "ok" if passed else "FAIL")
        if not passed:
            self.failures.append(what)

    @staticmethod
    def _letters_by_type() -> dict[str, str]:
        """Each vType's affiliation letter by the display convention: its population's, else n."""
        convention = json.loads(CONVENTION.read_text(encoding="utf-8"))
        letters = convention["affiliation_by_type"]
        by_type = {}
        for vtype in ET.parse(ROUTES).getroot().iter("vType"):
            classes = [param.get("value") for param in vtype.findall("param")
                       if param.get("key") == CLASS_PARAMETER]
            population = classes[0] if classes else vtype.get("id")
            by_type[vtype.get("id")] = letters.get(population, "n")
        return by_type

    def _drive(self, listener: _Listener, captures: Path, log: _LogLines, *extra: str) -> int:
        argv = ["--host", self.args.host, "--port", str(self.args.port),
                "--scenario", str(SCENARIO), "--world-package", str(WORLD_PACKAGE),
                "--epoch", str(EPOCH), "--warm-up", str(self.args.warm_up),
                "--steps", str(self.args.steps), "--record-dir", str(captures), "--record-hz", "20",
                "--camera-name", CAMERA, "--width", "960", "--height", "540",
                "--cot-udp", f"127.0.0.1:{listener.port}", *extra, *self.args.drive_args]
        self.logger.info("carla-drive %s", " ".join(argv))
        log.lines.clear()
        return drive.main(argv)

    @staticmethod
    def _sidecars(captures: Path) -> dict[str, ET.Element]:
        """Each sidecar the drive's recorder wrote, by the instant it is stamped with."""
        by_time = {}
        for path in captures.rglob("*.xml"):
            root = ET.parse(path).getroot()
            if root.tag == "events" and root.get("captured"):
                by_time[root.get("captured")] = root
        return by_time

    @staticmethod
    def _vehicles(sidecar: ET.Element) -> dict[str, ET.Element]:
        return {event.get("uid"): event for event in sidecar.findall("event")
                if event.get("uid", "").startswith(UID_PREFIX)}

    def _sampled(self, sidecar: ET.Element) -> bool:
        """Whether the feed sampled the sidecar's frame: the newest of each simulated second, as Bahonar
        steps 1.0 s from a whole second at 20 ticks a step."""
        tick = round((float(sidecar.get("sim_time_s")) - self.args.warm_up) / 0.05)
        return tick % 20 == 19

    def _sent_count(self, log: _LogLines) -> int | None:
        for line in log.lines:
            if line.startswith("cot feed ") and " events sent to " in line:
                return int(line.split(" samples, ")[1].split(" events sent")[0])
        return None

    def _datagrams(self, raw: list[bytes], log: _LogLines, mode: str):
        """The datagrams parsed, after checking each against the schema and the count against the
        drive's own."""
        sent = self._sent_count(log)
        self.check(sent is not None and sent == len(raw),
                   f"{mode}: every datagram sent was received ({len(raw)} received, {sent} sent)")
        invalid = [data for data in raw if not self.schema.validate(etree.fromstring(data))]
        self.check(not invalid, f"{mode}: every datagram is valid against cot_telemetry.xsd "
                                f"({len(invalid)} invalid)")
        if raw:
            self.logger.info("%s: the largest datagram is %d bytes", mode, max(map(len, raw)))
        by_time = defaultdict(dict)
        for data in raw:
            event = ET.fromstring(data)
            by_time[event.get("time")][event.get("uid")] = event
        return by_time

    def _compare(self, datagram: ET.Element, event: ET.Element) -> list[str]:
        """How a datagram differs from the sidecar's event for the same vehicle, beyond what it may."""
        problems = []
        letter = self.letter_of_type.get(event.find("detail/_carla").get("vtype_id"), "n")
        for path in (".", "point", "detail/track", "detail/contact", "detail/_carla"):
            sent, kept = datagram.find(path), event.find(path)
            for name, value in sent.attrib.items():
                if path == "." and name == "type":
                    if (value, kept.get(name)) != (f"a-{letter}-G-E-V", "a-n-G-E-V"):
                        problems.append(f"type {value} where the sidecar says {kept.get(name)} and the "
                                        f"convention {letter}")
                elif kept.get(name) != value:
                    problems.append(f"{path}@{name} {value!r} where the sidecar says {kept.get(name)!r}")
        sent_carla = set(datagram.find("detail/_carla").attrib)
        kept_carla = set(event.find("detail/_carla").attrib)
        if sent_carla & WITHHELD or not (kept_carla - sent_carla) <= WITHHELD:
            problems.append(f"_carla carries {sorted(sent_carla & WITHHELD)} withheld and lacks "
                            f"{sorted((kept_carla - sent_carla) - WITHHELD)} that is not")
        children = [child.tag for child in datagram.find("detail")]
        if children != DATAGRAM_DETAIL:
            problems.append(f"<detail> holds {children}")
        return problems

    def _all_mode(self, listener: _Listener, captures: Path, log: _LogLines) -> None:
        status = self._drive(listener, captures, log)
        self.check(status == 0, f"all: the drive ended as asked (exit status {status})")
        datagrams = self._datagrams(listener.take(), log, "all")
        sidecars = self._sidecars(captures)
        compared = differing = 0
        for stamp, sent in sorted(datagrams.items()):
            sidecar = sidecars.get(stamp)
            if sidecar is None:
                # A frame the recorder has no capture of: the aim step's, sampled before the recorder
                # started, or one it dropped.
                continue
            compared += 1
            kept = self._vehicles(sidecar)
            self.check(sidecar.get("vehicles") == "rendered" and set(sent) == set(kept),
                       f"all: the datagrams of {stamp} are its sidecar's rendered vehicles "
                       f"({len(sent)} sent, {len(kept)} in the sidecar)")
            for uid, datagram in sent.items():
                if uid in kept:
                    problems = self._compare(datagram, kept[uid])
                    if problems:
                        differing += 1
                        self.logger.error("%s at %s: %s", uid, stamp, "; ".join(problems))
        self.check(compared > 0, f"all: {compared} sampled frames have a sidecar to compare with")
        self.check(differing == 0, f"all: every datagram is its sidecar event, less what is withheld "
                                   f"({differing} differ)")

    def _in_view_mode(self, listener: _Listener, captures: Path, log: _LogLines) -> None:
        status = self._drive(listener, captures, log, "--cot-emit", "in-view", "--cot-camera", CAMERA)
        self.check(status == 0, f"in-view: the drive ended as asked (exit status {status})")
        self.check(any(line.startswith(f"cot camera {CAMERA} followed: ") for line in log.lines),
                   f"in-view: the drive followed its own camera {CAMERA}")
        datagrams = self._datagrams(listener.take(), log, "in-view")
        sampled = [sidecar for sidecar in self._sidecars(captures).values() if self._sampled(sidecar)]
        in_view_total = all_total = 0
        for sidecar in sampled:
            stamp = sidecar.get("captured")
            vehicles = self._vehicles(sidecar)
            expected = {uid for uid, event in vehicles.items()
                        if event.find("detail/_carla").get("in_frame") in IN_PICTURE
                        and event.find("detail/_carla").get("beyond_draw_distance") != "wholly"}
            sent = set(datagrams.get(stamp, {}))
            in_view_total += len(sent)
            all_total += len(vehicles)
            self.check(sent == expected, f"in-view: the datagrams of {stamp} are the vehicles its "
                                         f"sidecar has in the picture ({len(sent)} sent, "
                                         f"{len(expected)} expected)")
        self.check(bool(sampled), f"in-view: {len(sampled)} sampled frames have a sidecar")
        self.logger.info("in-view: %d events over the sampled frames, of %d vehicles drawn",
                         in_view_total, all_total)

    def run(self) -> int:
        listener = _Listener()
        log = _LogLines()
        logging.getLogger().addHandler(log)
        temporary = Path(tempfile.mkdtemp(prefix="drive_cot_feed_"))
        try:
            self._all_mode(listener, temporary / "all", log)
            self._in_view_mode(listener, temporary / "in_view", log)
        finally:
            logging.getLogger().removeHandler(log)
            listener.close()
            if self.args.keep:
                self.logger.info("captures kept in %s", temporary)
            else:
                shutil.rmtree(temporary, ignore_errors=True)
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
    parser.add_argument("--steps", type=int, default=4,
                        help="SUMO steps each drive runs, the aim step included (default 4)")
    parser.add_argument("--keep", action="store_true", help="keep the captures and say where")
    parser.add_argument("drive_args", nargs="*",
                        help="further carla-drive options, after --, such as --sumo-home DIR")
    args = parser.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(message)s", stream=sys.stdout)
    return DriveCotFeedLiveCheck(args, logging.getLogger("test_drive_cot_feed_live")).run()


if __name__ == "__main__":
    sys.exit(main())
