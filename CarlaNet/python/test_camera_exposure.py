#!/usr/bin/env python3
"""The RGB camera takes its exposure from its attributes, over its profile, and every capture records it.

Stage K, as the owner ruled on 2026-10-06 (13_Work_Breakdown.md; 08_Collection_And_EPoL.md D8.26-D8.27).
The camera publishes upstream CARLA 0.9's exposure attributes -- `exposure_mode`, `exposure_compensation`,
`iso`, `shutter_speed` (per second) and `fstop` -- recommended at the `Default` profile's values, and the
plugin applies them after the post-process profile loads, so they set the exposure whatever the profile.
Every capture records the exposure its camera was given in `<_carla_exposure>`.

What it does, on the generated world the server has loaded (a stock Town map is refused: the recorder
needs a georeference, and checks run on generated worlds only):

  1. takes the world's clock (synchronous at 0.05 s) and spawns three RGB cameras at one fixed pose, the
     same image size, field of view and profile: the subject at ISO 400, and two at ISO 100 -- the
     reference and its control twin. Everything else is the blueprint's default exposure, the `Default`
     profile's manual 1/320 s at f/4 with no compensation;
  2. reads back each camera's attributes and the exposure record the shim makes from them;
  3. ticks until the reference camera's view is ready -- its tiles in, as `get_view_readiness` reports
     them -- and then `--settle-ticks` more, so every camera's picture has settled, and listens to all
     three through `--sample-ticks` ticks, keeping each frame's mean gray;
  4. records the subject for a few captures and reads `<_carla_exposure>` back from a sidecar.

The three cameras are spawned together, so they see the same frame of the same world: the sun, the tiles
and any vehicle are one state for all three, and the only thing varied is the ISO. The control twin, at
the reference's own settings, measures what two cameras at one pose differ by when nothing differs.

What it asserts, and why:

  * attributes: each camera's attributes echo what it was given -- `exposure_mode` manual, `iso` 100 or
    400, `shutter_speed` 320, `fstop` 4, `exposure_compensation` 0 -- and the exposure record read from
    them gives EV100 12.32 at ISO 100 and 10.32 at ISO 400, two stops brighter;
  * control: the reference and its twin differ by at most 1 gray level in mean on every compared frame;
  * the picture: the reference's mean gray is above 2 and below 250 on 8-bit, so the picture is neither
    black nor clipped white and a rise can be seen at all;
  * rise: on every compared frame the subject's mean gray exceeds the reference's by at least 8 levels
    and by at least ten times the control's difference -- measurably brighter in 8-bit. Four times the
    ISO is two stops, four times the light in linear terms, but the pixels are 8-bit and passed through
    the tone curve, whose shoulder compresses a brightening and whose white clips it, so a ratio of four
    cannot be asserted from them. The ratio of the sRGB-decoded linear means is printed, not judged;
  * record: a sidecar of the subject carries `<_carla_exposure>` with `iso` 400, `shutter_s` 0.003125,
    `fstop` 4, `compensation_ev` 0 and `ev100` 10.322.

Run it on its own, not beside a drive: it ticks the world. It gives the world's settings back and
destroys its cameras on every path out, and removes its captures unless told to keep them.

Prereqs: a generated world loaded on a server built with the exposure attributes, and the carlanet wheel
built from this tree. A server whose camera publishes no exposure is reported as such and the check fails.

Usage:
    python test_camera_exposure.py [--host h] [--port p] [--x 0] [--y 0] [--z 300] [--pitch -60]
                                   [--yaw 0] [--settle-ticks 100] [--sample-ticks 20] [--keep]
"""
import argparse
import logging
import re
import shutil
import sys
import tempfile
import threading
import xml.etree.ElementTree as ET
from pathlib import Path

import numpy as np

import carlanet as carla

DELTA_S = 0.05
WIDTH, HEIGHT, FOV = 640, 360, 90.0
REFERENCE_ISO, SUBJECT_ISO = 100.0, 400.0
# The blueprint's defaults, which are the Default profile's: 1/320 s, f/4, no compensation.
SHUTTER_PER_S, FSTOP, COMPENSATION_EV = 320.0, 4.0, 0.0
EV100_REFERENCE = 12.322
EV100_SUBJECT = 10.322
CONTROL_TOLERANCE_LEVELS = 1.0
RISE_FLOOR_LEVELS = 8.0
RISE_OVER_CONTROL = 10.0
PICTURE_RANGE = (2.0, 250.0)
READY_CEILING_TICKS = 1200
RECORD_HZ = 2.0
RECORD_TICKS = 60
EXPOSURE_ATTRIBUTES = ("exposure_mode", "iso", "shutter_speed", "fstop", "exposure_compensation")
# A stock CARLA town: Town01 to Town15, Town10HD and the like, with or without a package path.
STOCK_TOWN = re.compile(r"^town\d", re.IGNORECASE)


def mean_gray(image) -> tuple[float, float]:
    """The frame's mean 8-bit gray over every pixel, and its mean linear light, sRGB decoded."""
    pixels = np.frombuffer(image.raw_data, dtype=np.uint8)[:image.width * image.height * 4]
    rgb = pixels.reshape(image.height, image.width, 4)[:, :, :3].astype(np.float64)
    encoded = rgb / 255.0
    linear = np.where(encoded <= 0.04045, encoded / 12.92, ((encoded + 0.055) / 1.055) ** 2.4)
    return float(rgb.mean()), float(linear.mean())


class CameraExposureCheck:
    """Spawns three cameras at one pose, ISO the one thing varied, and reads what the server did."""

    def __init__(self, client, logger: logging.Logger, args: argparse.Namespace) -> None:
        self.client = client
        self.world = client.get_world()
        self.logger = logger
        self.args = args
        self.failures: list[str] = []
        self.cameras: dict[str, object] = {}
        self.grays: dict[str, dict[int, tuple[float, float]]] = {}
        self.lock = threading.Lock()

    def check(self, passed: bool, what: str) -> None:
        self.logger.info("%-100s %s", what, "ok" if passed else "FAIL")
        if not passed:
            self.failures.append(what)

    # -- the cameras ------------------------------------------------------------------------------------

    def spawn(self, role: str, name: str, iso: float) -> None:
        library = self.world.get_blueprint_library()
        rgb = library.find("sensor.camera.rgb")
        rgb.set_attribute("image_size_x", str(WIDTH))
        rgb.set_attribute("image_size_y", str(HEIGHT))
        rgb.set_attribute("fov", str(FOV))
        rgb.set_attribute("sensor_tick", "0.0")
        rgb.set_attribute("iso", repr(iso))
        pose = carla.Transform(carla.Location(x=self.args.x, y=self.args.y, z=self.args.z),
                               carla.Rotation(pitch=self.args.pitch, yaw=self.args.yaw, roll=0.0))
        self.cameras[role] = self.world.spawn_camera(rgb, pose, name=name)

    def listen(self, role: str) -> None:
        frames: dict[int, tuple[float, float]] = {}
        self.grays[role] = frames

        def on_image(image) -> None:
            if image.width != WIDTH or image.height != HEIGHT:
                return
            measured = mean_gray(image)
            with self.lock:
                frames[int(image.frame)] = measured

        self.cameras[role].listen(on_image)

    def report_attributes(self) -> None:
        for role, iso, ev100 in (("reference", REFERENCE_ISO, EV100_REFERENCE),
                                 ("control", REFERENCE_ISO, EV100_REFERENCE),
                                 ("subject", SUBJECT_ISO, EV100_SUBJECT)):
            camera = self.cameras[role]
            held = camera.attributes
            expected = {"exposure_mode": "manual", "iso": iso, "shutter_speed": SHUTTER_PER_S,
                        "fstop": FSTOP, "exposure_compensation": COMPENSATION_EV}
            echoed = all(key in held for key in EXPOSURE_ATTRIBUTES) and held["exposure_mode"] == "manual" \
                and all(abs(float(held[key]) - float(value)) <= 1e-6
                        for key, value in expected.items() if key != "exposure_mode")
            self.check(echoed, f"attributes: the {role} camera's attributes echo its exposure "
                               f"({', '.join(f'{k}={held.get(k)}' for k in EXPOSURE_ATTRIBUTES)})")
            record = self.world.camera_exposure(camera)
            self.check(record is not None and record.Ev100 is not None
                       and abs(float(record.Ev100) - ev100) <= 0.001,
                       f"record: the {role} camera's exposure record gives EV100 {ev100} "
                       f"({None if record is None else record.Ev100})")

    # -- the view ---------------------------------------------------------------------------------------

    def tick_until_ready(self) -> int | None:
        """Tick until the reference camera's view drove the tilesets' selection and its visible
        tilesets are loaded with nothing queued, or the ceiling; the ticks taken, or None at the
        ceiling."""
        camera = self.cameras["reference"]
        for ticks in range(1, READY_CEILING_TICKS + 1):
            self.world.tick()
            readiness = self.world.get_view_readiness(camera)
            visible = [t for t in readiness["tilesets"] if t["visible"]]
            if readiness["published"] and visible and all(
                    t["load_progress"] >= 100.0 and t["loading"]["worker_queue"] == 0
                    and t["loading"]["main_queue"] == 0 for t in visible):
                return ticks
        return None

    def report_pictures(self, first_frame: int) -> None:
        with self.lock:
            reference = {f: g for f, g in self.grays["reference"].items() if f >= first_frame}
            control = {f: g for f, g in self.grays["control"].items() if f >= first_frame}
            subject = {f: g for f, g in self.grays["subject"].items() if f >= first_frame}
        frames = sorted(set(reference) & set(control) & set(subject))
        self.check(len(frames) >= max(1, self.args.sample_ticks // 2),
                   f"frames: all three cameras delivered the same frames ({len(frames)} of "
                   f"{self.args.sample_ticks} sampled ticks)")
        if not frames:
            return
        control_apart = [abs(reference[f][0] - control[f][0]) for f in frames]
        rises = [subject[f][0] - reference[f][0] for f in frames]
        worst_control = max(control_apart)
        floor = max(RISE_FLOOR_LEVELS, RISE_OVER_CONTROL * worst_control)
        ref_mean = float(np.mean([reference[f][0] for f in frames]))
        sub_mean = float(np.mean([subject[f][0] for f in frames]))
        ratio = float(np.mean([subject[f][1] for f in frames])) / max(
            1e-12, float(np.mean([reference[f][1] for f in frames])))
        self.check(worst_control <= CONTROL_TOLERANCE_LEVELS,
                   f"control: the reference and its twin, at one exposure, differ by at most "
                   f"{CONTROL_TOLERANCE_LEVELS:g} gray level on every frame (worst {worst_control:.3f})")
        self.check(PICTURE_RANGE[0] < ref_mean < PICTURE_RANGE[1],
                   f"picture: the reference at ISO 100 is neither black nor clipped white "
                   f"(mean gray {ref_mean:.1f} of 255)")
        self.check(min(rises) >= floor,
                   f"rise: at ISO 400 every frame is brighter than at ISO 100 by at least {floor:.1f} "
                   f"gray levels (least {min(rises):.1f}, mean {sub_mean:.1f} against {ref_mean:.1f})")
        self.logger.info("linear: the subject's mean linear light is %.2f times the reference's; four "
                         "is two stops before the tone curve, which compresses and clips it (printed, "
                         "not judged)", ratio)

    # -- the record -------------------------------------------------------------------------------------

    def report_record(self, directory: Path) -> None:
        camera = self.cameras["subject"]
        recorder = self.world.start_recording(camera, str(directory), RECORD_HZ, fov=FOV)
        self.check(recorder is not None, "the native recorder started on the subject camera")
        if recorder is None:
            return
        for _ in range(RECORD_TICKS):
            self.world.tick()
        self.world.stop_recording()
        sidecars = sorted(directory.glob("*.xml"))
        self.check(bool(sidecars), f"record: the subject's captures were written ({len(sidecars)})")
        if not sidecars:
            return
        found = [element for path in sidecars
                 for element in ET.parse(path).getroot().iter("_carla_exposure")]
        self.check(len(found) == len(sidecars),
                   f"record: every sidecar carries <_carla_exposure> ({len(found)} of {len(sidecars)})")
        if not found:
            return
        element = found[0].attrib
        expected = {"method": "manual", "iso": "400", "shutter_s": "0.003125", "fstop": "4",
                    "compensation_ev": "0", "ev100": f"{EV100_SUBJECT:g}"}
        self.check(all(element.get(key) == value for key, value in expected.items()),
                   f"record: the sidecar states the exposure the camera was given ({element})")

    # -- the run ---------------------------------------------------------------------------------------

    def run(self) -> int:
        map_name = str(self.world.get_map().name)
        base = map_name.replace("\\", "/").rsplit("/", 1)[-1]
        if STOCK_TOWN.match(base):
            self.logger.error("the server has %s loaded, a stock Town map; this check runs on a "
                              "generated world only. Load one and run it again", map_name)
            return 2
        self.logger.info("world: %s", map_name)
        blueprint = self.world.get_blueprint_library().find("sensor.camera.rgb")
        missing = [name for name in EXPOSURE_ATTRIBUTES if not blueprint.has_attribute(name)]
        if missing:
            self.check(False, f"server: the RGB camera publishes its exposure (missing "
                              f"{', '.join(missing)}; rebuild the plugin)")
            return 1

        original = self.world.get_settings()
        directory = Path(tempfile.mkdtemp(prefix="camera-exposure-"))
        try:
            settings = self.world.get_settings()
            settings.synchronous_mode = True
            settings.fixed_delta_seconds = DELTA_S
            self.world.apply_settings(settings)
            self.world.tick()

            self.spawn("reference", "ExposureCheck_ISO100", REFERENCE_ISO)
            self.spawn("control", "ExposureCheck_ISO100_twin", REFERENCE_ISO)
            self.spawn("subject", "ExposureCheck_ISO400", SUBJECT_ISO)
            self.world.tick()
            self.report_attributes()

            for role in ("reference", "control", "subject"):
                self.listen(role)
            ready = self.tick_until_ready()
            self.check(ready is not None, f"view: the reference camera's tiles are in "
                                          f"({ready} ticks, ceiling {READY_CEILING_TICKS})")
            for _ in range(self.args.settle_ticks):
                self.world.tick()
            first_frame = int(self.world.tick())
            for _ in range(self.args.sample_ticks - 1):
                self.world.tick()
            # A few more ticks, so the last sampled images reach their listeners.
            for _ in range(10):
                self.world.tick()
            self.report_pictures(first_frame)
            self.report_record(directory)
        finally:
            try:
                self.world.stop_recording()
            except Exception as failure:  # noqa: BLE001 - cleanup reports and continues
                self.logger.warning("could not stop the recorder: %r", failure)
            for role, camera in self.cameras.items():
                try:
                    camera.stop()
                    camera.destroy()
                except Exception as failure:  # noqa: BLE001 - cleanup reports and continues
                    self.logger.warning("could not destroy the %s camera: %r", role, failure)
            try:
                self.world.apply_settings(original)
            except Exception as failure:  # noqa: BLE001 - cleanup reports and continues
                self.logger.warning("could not give the world its settings back: %r", failure)
            if self.args.keep:
                self.logger.info("captures kept in %s", directory)
            else:
                shutil.rmtree(directory, ignore_errors=True)

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
    parser.add_argument("--x", type=float, default=0.0, help="the cameras' x, CARLA metres")
    parser.add_argument("--y", type=float, default=0.0, help="the cameras' y, CARLA metres (south)")
    parser.add_argument("--z", type=float, default=300.0, help="the cameras' height, CARLA metres")
    parser.add_argument("--pitch", type=float, default=-60.0,
                        help="the cameras' pitch, degrees; negative looks down")
    parser.add_argument("--yaw", type=float, default=0.0, help="the cameras' yaw, degrees; 0 faces east")
    parser.add_argument("--settle-ticks", type=int, default=100,
                        help="ticks after the tiles are in before sampling, so every picture settles")
    parser.add_argument("--sample-ticks", type=int, default=20, help="ticks whose frames are compared")
    parser.add_argument("--keep", action="store_true", help="keep the captures and say where")
    args = parser.parse_args()
    logging.basicConfig(level=logging.INFO, format="%(message)s")
    client = carla.Client(args.host, args.port)
    client.set_timeout(30.0)
    return CameraExposureCheck(client, logging.getLogger("test_camera_exposure"), args).run()


if __name__ == "__main__":
    sys.exit(main())
