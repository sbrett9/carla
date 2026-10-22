#!/usr/bin/env python3
"""carla-drive --epoch-at runs the Bahonar pattern of life with its simulated second zero moved.

Server state it needs: a headless CARLA server with Shahid_Bahonar_Port loaded and no drive holding
the world's drive lease, and the carlanet wheel built from this checkout. The drive takes the world's
clock while it runs and gives it back as it ends.

What it does, from this checkout:

  1. Runs carla-drive in this process (`carlacontrol.commands.drive.main`) with
     `--epoch-at 2026-10-22T17:40:00Z` and no `--epoch`, so the epoch moved is the compile lock's,
     07:00 at +03:30 on 29 September 2026, and the run's is 21:10 at +03:30 on 22 October. SUMO is
     fast-forwarded to t = 271109, the capture window opens at t = 271169, 3 d 03:19:29 into the
     scenario, and the drive ends after the step at t = 271170. Its fixed camera, Epoch_At_1, records
     at 2 Hz; the drive writes the run manifest and the world truth track, and sends its live feed to
     a UDP listener on 127.0.0.1.
  2. Holds what the drive wrote and sent to the moved clock:
     - the still at t = 271169 is Epoch_At_1_20261026_002929.000.png and .xml, its sidecar captured
       at 2026-10-25T20:59:29.000Z, its illumination chunk naming the moved epoch, by its civil
       instant and its digest, and the frame's civil instant 2026-10-26T00:29:29+03:30;
     - the world truth track's rows at t = 271169 are stamped 2026-10-25T20:59:29.000Z;
     - the live feed's sample of the step at 271169, its last frame, is stamped
       2026-10-25T20:59:29.950Z and goes stale 3 s later;
     - the manifest's opening row carries the moved epoch, the compiled one as epoch_moved_from and
       the lock's digest of the compiled one; the escort transits, the perimeter shadow, the ferry
       stay-behind and the skipped posting's expected_shift_start are on the moved clock; the sun at
       the window's opening is the moved instant's, below -18 degrees, where the compiled clock has
       10:19 in daylight, and matched its declaration over the window;
     - the launch says that the epoch agrees with the lock and names the instant moved to, the run's
       epoch and the compiled one, and so does the session's report.

It spawns nothing: the drive removes its own cameras. Its files are written to a temporary folder,
removed at the end unless --keep is given. The fast-forward to t = 271109 takes a while.

Usage, from the checkout's root:
    python CarlaNet/python/test_drive_epoch_at_live.py [--host h] [--port p] [--keep]
        [-- further carla-drive options]
"""
import argparse
import csv
import json
import logging
import shutil
import socket
import sys
import tempfile
import threading
import time
import xml.etree.ElementTree as ET
from datetime import datetime, timedelta, timezone
from pathlib import Path

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.commands import drive  # noqa: E402  (needs the path above)
from carlacontrol.PngTextChunks import PngTextChunks  # noqa: E402

BAHONAR = _REPO / "CarlaControl" / "Examples" / "Scenarios" / "Bahonar"
SCENARIO = BAHONAR / "Shahid_Bahonar_Port_PatternOfLife" / "Shahid_Bahonar_Port_PatternOfLife.sumocfg"
LOCK = SCENARIO.with_suffix(".lock.json")
WORLD_PACKAGE = BAHONAR / "Shahid_Bahonar_Port.cwp"

EPOCH_AT = "2026-10-22T17:40:00Z"
CAMERA = "Epoch_At_1"
WARM_UP_S, OPENS_AT_S, END_S = 271109, 271169, 271170
COMPILED_DIGEST = "f1ba0ddab35f800a4b78ebc902fa9a1dd9d47a64fee72eb3cfab687969bbecdf"
MOVED_DIGEST = "59f0d5a8bb8bbe38a03880bc02f902cfefcad4bade22cb722da313c292d5fbd8"
MOVED_CIVIL = "2026-10-22T21:10:00+03:30"
MOVED_ZERO = datetime(2026, 10, 22, 21, 10, tzinfo=timezone(timedelta(hours=3, minutes=30)))
STILL = f"{CAMERA}_20261026_002929.000"
STILL_CIVIL = "2026-10-26T00:29:29+03:30"
STILL_UTC = "2026-10-25T20:59:29.000Z"
FEED_TIME, FEED_STALE = "2026-10-25T20:59:29.950Z", "2026-10-25T20:59:32.950Z"
ILLUMINATION_KEYWORD = "carla:illumination"
ASTRONOMICAL_NIGHT_DEG = -18.0


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
    """Every line the drive logs, kept so its launch and its report can be read."""

    def __init__(self) -> None:
        super().__init__(logging.INFO)
        self.lines: list[str] = []

    def emit(self, record: logging.LogRecord) -> None:
        self.lines.append(record.getMessage())


class DriveEpochAtLiveCheck:
    """Runs carla-drive once with simulated second zero moved, and holds its files to the moved clock."""

    def __init__(self, args: argparse.Namespace, logger: logging.Logger) -> None:
        self.args = args
        self.logger = logger
        self.failures: list[str] = []

    def check(self, passed: bool, what: str) -> None:
        self.logger.info("%-96s %s", what, "ok" if passed else "FAIL")
        if not passed:
            self.failures.append(what)

    def _drive(self, listener: _Listener, directory: Path) -> int:
        argv = ["--host", self.args.host, "--port", str(self.args.port),
                "--scenario", str(SCENARIO), "--world-package", str(WORLD_PACKAGE),
                "--epoch-at", EPOCH_AT, "--warm-up", str(WARM_UP_S),
                "--window-opens-at", str(OPENS_AT_S), "--end", str(END_S),
                "--camera-name", CAMERA, "--record-dir", str(directory / "captures"),
                "--run-manifest", str(directory / "run.manifest.jsonl"),
                "--world-truth-track", str(directory / "world_truth_track.csv"),
                "--cot-udp", f"127.0.0.1:{listener.port}", *self.args.drive_args]
        self.logger.info("carla-drive %s", " ".join(argv))
        return drive.main(argv)

    def _launch(self, log: _LogLines) -> None:
        text = "\n".join(log.lines)
        self.check(any(line.startswith("compile lock: ") and line.endswith(
            f"so does the epoch, which the run moves to start at {EPOCH_AT}") for line in log.lines),
            "the compile lock line says the epoch agrees and names the instant moved to")
        self.check(f"epoch: t = 0 is {MOVED_CIVIL} ({EPOCH_AT})" in text,
                   "the launch names the run's epoch")
        self.check("  moved at run time from the compiled epoch: t = 0 is 2026-09-29T07:00:00+03:30"
                   in text, "the launch names the compiled epoch it was moved from")
        self.check("  moved from       the compiled epoch: t = 0 is 2026-09-29T07:00:00+03:30" in text,
                   "the session's report names the compiled epoch")

    def _still(self, captures: Path) -> None:
        pngs = list(captures.rglob(f"{STILL}.png"))
        sidecars = list(captures.rglob(f"{STILL}.xml"))
        self.check(len(pngs) == 1 and len(sidecars) == 1,
                   f"the still at t={OPENS_AT_S} is {STILL}.png and .xml")
        named_compiled = [path.name for path in captures.rglob("*_20261002_*")]
        self.check(not named_compiled, f"no file is named on the compiled clock ({named_compiled[:2]})")
        if not (pngs and sidecars):
            return
        root = ET.parse(sidecars[0]).getroot()
        self.check(root.get("captured") == STILL_UTC and float(root.get("sim_time_s")) == OPENS_AT_S,
                   f"its sidecar was captured at {STILL_UTC}, t={OPENS_AT_S} "
                   f"({root.get('captured')}, {root.get('sim_time_s')})")
        chunks = dict(PngTextChunks.read(pngs[0]))
        illumination = json.loads(chunks.get(ILLUMINATION_KEYWORD, "{}"))
        self.check(illumination.get("epoch_civil") == MOVED_CIVIL
                   and illumination.get("epoch_digest") == MOVED_DIGEST,
                   f"its illumination chunk names the moved epoch ({illumination.get('epoch_civil')})")
        self.check(illumination.get("declared_civil") == STILL_CIVIL,
                   f"its illumination chunk's civil instant is {STILL_CIVIL} "
                   f"({illumination.get('declared_civil')})")

    def _track(self, track: Path) -> None:
        with open(track, newline="", encoding="utf-8") as handle:
            rows = [row for row in csv.DictReader(handle)
                    if abs(float(row["sim_time_s"]) - OPENS_AT_S) < 1e-6]
        stamps = {row["time_utc"] for row in rows}
        self.check(bool(rows) and stamps == {STILL_UTC},
                   f"the world truth track's {len(rows)} rows at t={OPENS_AT_S} are stamped "
                   f"{STILL_UTC} ({sorted(stamps)})")

    def _feed(self, listener: _Listener) -> None:
        events = [ET.fromstring(data) for data in listener.take()]
        at_step = [event for event in events if event.get("time") == FEED_TIME]
        stale = {event.get("stale") for event in at_step}
        self.check(bool(at_step) and stale == {FEED_STALE},
                   f"the live feed's {len(at_step)} events of the step at {OPENS_AT_S} are stamped "
                   f"{FEED_TIME} and go stale at {FEED_STALE} ({sorted(stale)})")

    def _manifest(self, manifest: Path) -> None:
        rows = [json.loads(line) for line in manifest.read_text(encoding="utf-8").splitlines()
                if line.strip()]
        compiled = json.loads(LOCK.read_text(encoding="utf-8"))["epoch"]
        opened = next(row for row in rows if row["row"] == "manifest_opened")
        solar = opened["solar"]
        self.check(opened["scenario"]["epoch_digest"] == COMPILED_DIGEST,
                   "the manifest's scenario.epoch_digest is the lock's")
        self.check(solar["epoch"]["civil_datetime"] == MOVED_CIVIL
                   and solar["epoch_block_sha256"] == MOVED_DIGEST,
                   f"its solar.epoch is the moved epoch ({solar['epoch']['civil_datetime']})")
        self.check(solar.get("epoch_moved_from") == compiled,
                   "its solar.epoch_moved_from is the compiled epoch")

        instances = {row["instance_id"].rsplit("/", 1)[-1]: row for row in rows
                     if row["row"] == "instance"}
        escort = [interval["declared_start_civil"]
                  for interval in instances["pi_escort_drydock_d3"]["intervals"]]
        self.check(escort == [f"2026-10-26T00:10:{second:02d}+03:30" for second in (0, 4, 8, 12, 16)],
                   f"the escort transits start at 00:10:00 to 00:10:16 on 26 October ({escort})")
        [shadow] = instances["pi_perimeter_shadow_d6"]["intervals"]
        self.check(shadow["declared_start_civil"] == "2026-10-28T16:40:00+03:30",
                   f"the perimeter shadow starts at 16:40 on 28 October ({shadow['declared_start_civil']})")
        [stay] = instances["pi_ferry_stay_behind_d1"]["intervals"]
        self.check(stay["declared_end_civil"] == "2026-10-29T21:10:00+03:30",
                   f"the ferry stay-behind ends at 21:10 on 29 October ({stay['declared_end_civil']})")
        shift = instances["pi_posting_not_taken_up_d4"]["parameters"]["expected_shift_start"]
        self.check(shift == {"seconds": 345600.0, "civil": "2026-10-26T21:10:00+03:30"},
                   f"the skipped posting's expected_shift_start is 21:10 on 26 October ({shift})")

        window_open = next(row for row in rows if row["row"] == "solar_window_open")
        self.check(window_open["civil_begin"] == STILL_CIVIL
                   and window_open["solar_date_begin"] == "2026-10-26",
                   f"the window opens at {STILL_CIVIL} with the sun's date 2026-10-26 "
                   f"({window_open['civil_begin']}, {window_open['solar_date_begin']})")
        # The zone the world's sun was found with, before the run bound it: the server's state, which
        # the move does not set.
        self.logger.info("the world's sun was found in the time zone %s h",
                         window_open["sun_time_zone_hours"])
        delta = window_open["declared_civil_vs_solar_delta_h"]
        self.check(delta is not None and abs(delta) < 0.01,
                   f"the declared sun's clock is the civil clock at the window's opening ({delta} h)")
        elevation = window_open["sun_elevation_begin_deg"]
        self.check(window_open["illumination_band_begin"] == "night" and elevation is not None
                   and elevation < ASTRONOMICAL_NIGHT_DEG,
                   f"the sun at the window's opening is night, below {ASTRONOMICAL_NIGHT_DEG:g} degrees "
                   f"({window_open['illumination_band_begin']}, {elevation})")
        window_end = next(row for row in rows if row["row"] == "solar_window_end")
        ended = window_end["civil_end"]
        self.check(ended is not None and window_end["end_s"] is not None
                   and datetime.fromisoformat(ended) == MOVED_ZERO + timedelta(seconds=window_end["end_s"])
                   and ended.endswith("+03:30"),
                   f"the window's last capture is on the moved clock ({ended} at t={window_end['end_s']})")
        self.check(window_end["sun_matched_declaration"] is True,
                   "the sun matched its declaration over the window")

    def run(self) -> int:
        listener = _Listener()
        log = _LogLines()
        logging.getLogger().addHandler(log)
        temporary = Path(tempfile.mkdtemp(prefix="drive_epoch_at_"))
        try:
            status = self._drive(listener, temporary)
            self.check(status == 0, f"the drive ended as asked (exit status {status})")
            self._launch(log)
            self._feed(listener)
            self._still(temporary / "captures")
            self._track(temporary / "world_truth_track.csv")
            self._manifest(temporary / "run.manifest.jsonl")
        finally:
            logging.getLogger().removeHandler(log)
            listener.close()
            if self.args.keep:
                self.logger.info("files kept in %s", temporary)
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
    parser.add_argument("--keep", action="store_true", help="keep the drive's files and say where")
    parser.add_argument("drive_args", nargs="*",
                        help="further carla-drive options, after --, such as --sumo-home DIR")
    args = parser.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(message)s", stream=sys.stdout)
    return DriveEpochAtLiveCheck(args, logging.getLogger("test_drive_epoch_at_live")).run()


if __name__ == "__main__":
    sys.exit(main())
