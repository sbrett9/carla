"""Measure which lamps a vehicle blueprint actually lights, by looking at the rendered image.

Asking the simulator is not a measurement. Every blueprint in the content build declares
`has_lights = true`, and the light state read back after a command is the command itself -- the server
stores the requested bitmask and hands it back, whatever the vehicle did with it. Whether a lamp
illuminates is implemented per blueprint, in Blueprint, behind an event the C++ only raises. So a
client can set a light state, read it back, see exactly what it asked for, and be looking at an unlit
body. Every layer reports success and the imagery disagrees with all of them.

This pass therefore renders the vehicle and compares pixels. For one blueprint: put the sun below the
horizon so the vehicle is the only thing that can emit light, then alternate frames with no lamp
commanded and frames with exactly one lamp bit set, and count the pixels each bit adds.

Three things keep the answer honest:

  * **Every lamp sits between two off states.** The frames go off, lamp, off, lamp, off, and a lamp
    counts only if it brightens the image against *both* of its neighbours. Exposure adaptation makes
    a dark scene drift for many seconds after anything in it changes, and drift produces exactly the
    same kind of positive difference a lamp does. Measured on the shipped content: the same
    measurement that reported all eleven lamps lit on the ambulance against a single early reference
    reports one pixel of difference when each lamp is set against the off state beside it. The
    difference between the two off states is the noise floor this scene actually produces, and it is
    measured on the same time scale as the signal instead of being guessed at.
  * **A positive control.** Once per run, the sun is moved to a daylight instant and back, and the
    same metric must respond. If it does not, the camera or the metric is blind, and the whole pass
    records `ran: false` with every verdict `unknown` rather than reporting seventeen unlit vehicles
    it could not have seen light up.
  * **One bit at a time, never inferred.** Front and rear lamps are separate bits on separate meshes.
    No verdict is ever carried from one lamp to another.

A verdict is `lit`, `unlit` or `unknown`, and `unknown` means the pass could not look -- never that it
looked and saw nothing.
"""
from __future__ import annotations

import logging
import queue
from dataclasses import dataclass, field

import carlanet
import numpy as np

from carlacontrol.VehicleCatalogue import LAMP_BITS, LAMP_NAMES

logger = logging.getLogger(__name__)


@dataclass(frozen=True)
class LampProbeSettings:
    """Everything about the pass that a repeat of it would have to match.

    These are recorded in the catalogue header rather than left as constants in code, because a
    measurement whose lighting, framing and threshold are not written down is not repeatable.
    """

    #: Sun instant the pass runs at. A date and a sun-clock hour, not a civil time: this is the number
    #: the sky actor holds, in the sun's own zone, which is derived from the map's longitude.
    solar_date: tuple[int, int, int] = (2026, 3, 21)
    solar_time_hours: float = 1.0

    #: The daylight instant used once, for the positive control.
    control_solar_time_hours: float = 12.0

    #: Camera framing. The standoff is measured from the body's own end, so a lorry and a hatchback
    #: are both filled rather than one of them being a dot.
    standoff_m: float = 4.0
    height_offset_m: float = -2.5
    pitch_deg: float = 22.0
    yaws_deg: tuple[float, ...] = (180.0, 0.0)
    image_width: int = 480
    image_height: int = 360
    fov_deg: float = 60.0

    #: A pixel counts as lit when its brightest channel gains more than this, out of 255.
    luminance_threshold: int = 24

    #: The image region compared, as fractions of width and height: left, top, right, bottom. The
    #: whole frame by default -- at the probe's sun instant there is nothing else in it.
    region: tuple[float, float, float, float] = (0.0, 0.0, 1.0, 1.0)

    #: A lamp has to beat both the measured noise floor and this, so that one hot pixel is not a lamp.
    minimum_lit_pixels: int = 8

    #: A lamp's difference has to beat the measured drift by this factor as well as the pixel floor.
    #: A margin rather than a bare comparison, because two measurements of the same dark scene differ
    #: by a few pixels either way and a lamp that only just beats the noise is not a lamp.
    drift_margin: float = 4.0

    #: Simulation steps: after the sun moves, after a vehicle appears, and after a lamp is commanded.
    #: The first two are long because exposure adaptation takes many seconds of simulated time to
    #: converge -- first on the night sky, then again on the silhouette of whatever was just spawned
    #: into it, and the larger the body the longer it takes.
    solar_settle_ticks: int = 400
    spawn_settle_ticks: int = 120
    lamp_settle_ticks: int = 10

    #: Frames averaged per capture, to put temporal anti-aliasing below the threshold.
    average_frames: int = 5

    #: How many simulation steps a capture may take, as a multiple of the frames it needs. The slack
    #: is there so a step on which one camera delivers nothing costs a step rather than the pass.
    capture_step_allowance: int = 6

    #: How long to wait for one camera's frame on one step before treating it as a missed step.
    frame_timeout_s: float = 5.0

    fixed_delta_seconds: float = 0.05

    def as_header(self) -> dict:
        """The subset a catalogue reader needs to know the pass's conditions."""
        return {
            "solar_date": "{:04d}-{:02d}-{:02d}".format(*self.solar_date),
            "solar_time_hours": self.solar_time_hours,
            "camera_poses": [
                {"standoff_m": self.standoff_m, "height_offset_m": self.height_offset_m,
                 "pitch_deg": self.pitch_deg, "yaw_deg": yaw, "fov_deg": self.fov_deg}
                for yaw in self.yaws_deg],
            "image_size": [self.image_width, self.image_height],
            "luminance_threshold": self.luminance_threshold,
            "region": list(self.region),
            "minimum_lit_pixels": self.minimum_lit_pixels,
            "drift_margin": self.drift_margin,
            "average_frames": self.average_frames,
        }


@dataclass
class LampProbeResult:
    """What the pass established: a verdict per lamp per blueprint, and the evidence behind it."""

    ran: bool
    header: dict
    reason: str = ""
    capability: dict[str, dict[str, str]] = field(default_factory=dict)
    evidence: dict[str, dict[str, int]] = field(default_factory=dict)

    def for_blueprint(self, blueprint_id: str) -> dict[str, str]:
        """The eleven verdicts for one blueprint, all `unknown` when the pass did not reach it."""
        return self.capability.get(blueprint_id, dict.fromkeys(LAMP_NAMES, "unknown"))


class VehicleLampProbe:
    """Runs the optical lamp pass over a set of blueprints against a live server.

    Puts the world into synchronous stepping for the duration and restores the caller's settings
    afterwards: a camera on this server delivers a frame per simulation step and none at all while the
    server free-runs, so a pass that did not take the clock would be reading whatever arrived.
    """

    def __init__(self, world, settings: LampProbeSettings | None = None) -> None:
        self.world = world
        self.settings = settings or LampProbeSettings()

    def run(self, blueprint_library, blueprint_ids: list[str], spawn_transform) -> LampProbeResult:
        """Probe every named blueprint at one transform, and return a verdict for each lamp."""
        header = self.settings.as_header()
        original = self.world.get_settings()
        try:
            self._begin_synchronous()
            self._set_sun(self.settings.solar_time_hours)
            state = self.world.get_solar_state()
            elevation = state.get("sun_elevation_deg")
            header["sun_elevation_deg"] = elevation
            if elevation is None or elevation > 0.0:
                return LampProbeResult(
                    False, header,
                    f"the sun is {elevation} degrees above the horizon at solar hour "
                    f"{self.settings.solar_time_hours}, so a lamp would be measured against daylight")
            self._tick(self.settings.solar_settle_ticks)
            result = LampProbeResult(True, header)
            for index, blueprint_id in enumerate(blueprint_ids):
                verdicts, evidence, control = self._probe_one(
                    blueprint_library, blueprint_id, spawn_transform, positive_control=index == 0)
                if control is not None:
                    header["positive_control_pixels"] = control
                    if control <= self.settings.minimum_lit_pixels:
                        return LampProbeResult(
                            False, header,
                            "the positive control did not respond: moving the sun to daylight "
                            f"changed only {control} pixels above the threshold, so this camera and "
                            "this metric could not have seen a lamp either")
                result.capability[blueprint_id] = verdicts
                result.evidence[blueprint_id] = evidence
                logger.info("lamp probe %s: %s", blueprint_id,
                            ", ".join(f"{lamp}={verdict}" for lamp, verdict in verdicts.items()
                                      if verdict == "lit") or "no lamp changed the image")
            return result
        finally:
            self.world.apply_settings(original)

    def _begin_synchronous(self) -> None:
        """Take the clock, so every camera frame belongs to a step this pass asked for."""
        settings = self.world.get_settings()
        settings.synchronous_mode = True
        settings.fixed_delta_seconds = self.settings.fixed_delta_seconds
        self.world.apply_settings(settings)

    def _set_sun(self, hours: float) -> None:
        """Pin the sun: a fixed date and hour, with advancement off so it does not drift mid-pass."""
        self.world.set_time_advance(False)
        self.world.set_solar_date(*self.settings.solar_date)
        self.world.set_solar_time(hours)

    def _tick(self, steps: int) -> None:
        for _ in range(steps):
            self.world.tick()

    def _probe_one(self, blueprint_library, blueprint_id: str, spawn_transform,
                   positive_control: bool) -> tuple[dict[str, str], dict[str, int], int | None]:
        """Spawn one blueprint, measure every lamp against it, and destroy it again."""
        vehicle = self.world.spawn_actor(blueprint_library.find(blueprint_id), spawn_transform)
        cameras: list = []
        try:
            vehicle.set_simulate_physics(False)
            box = vehicle.bounding_box
            standoff = box.extent.x + self.settings.standoff_m
            queues = []
            for yaw in self.settings.yaws_deg:
                forward = standoff if abs(yaw) > 90.0 else -standoff
                pose = carlanet.Transform(
                    carlanet.Location(forward, 0.0, box.location.z + self.settings.height_offset_m),
                    carlanet.Rotation(self.settings.pitch_deg, yaw, 0.0))
                camera = self.world.spawn_actor(self._camera_blueprint(blueprint_library), pose,
                                                attach_to=vehicle)
                frames: queue.Queue = queue.Queue()
                camera.listen(frames.put)
                cameras.append(camera)
                queues.append(frames)

            vehicle.set_light_state(carlanet.VehicleLightState(0))
            self._tick(self.settings.spawn_settle_ticks)
            before = self._capture(queues)

            evidence: dict[str, int] = {}
            signals: dict[str, int] = {}
            worst_floor = 0
            for lamp, bit in LAMP_BITS:
                vehicle.set_light_state(carlanet.VehicleLightState(bit))
                self._tick(self.settings.lamp_settle_ticks)
                commanded = self._capture(queues)
                vehicle.set_light_state(carlanet.VehicleLightState(0))
                self._tick(self.settings.lamp_settle_ticks)
                after = self._capture(queues)

                # The lamp has to brighten the image against the off state before it and the off
                # state after it; the two off states measure what the scene does on its own.
                signals[lamp] = min(self._lit_pixels(before, commanded),
                                    self._lit_pixels(after, commanded))
                floor = self._lit_pixels(before, after)
                worst_floor = max(worst_floor, floor)
                evidence[lamp] = signals[lamp]
                evidence[f"{lamp}_floor"] = floor
                before = after
            evidence["noise_floor_pixels"] = worst_floor

            # Decided after the whole sequence, against the largest difference the scene produced
            # between two identical states anywhere in it. One quiet pair of neighbours is not
            # evidence that a lamp lit when eleven pairs were measured and the scene moved in others.
            decision_floor = max(self.settings.drift_margin * worst_floor,
                                 self.settings.minimum_lit_pixels)
            evidence["decision_floor_pixels"] = int(decision_floor)
            verdicts = {lamp: ("lit" if signal > decision_floor else "unlit")
                        for lamp, signal in signals.items()}

            control = None
            if positive_control:
                control = self._positive_control(before, queues)
            return verdicts, evidence, control
        finally:
            for camera in cameras:
                camera.stop()
                camera.destroy()
            vehicle.destroy()

    def _positive_control(self, reference: list, queues: list) -> int:
        """Move the sun to daylight and back, and report what the metric made of it.

        The one change in the pass that is certain to alter the image. If the metric cannot see this,
        it could not have seen a lamp, and the pass says so instead of publishing its verdicts.
        """
        self._set_sun(self.settings.control_solar_time_hours)
        self._tick(self.settings.solar_settle_ticks)
        lit = self._lit_pixels(reference, self._capture(queues))
        self._set_sun(self.settings.solar_time_hours)
        self._tick(self.settings.solar_settle_ticks)
        return lit

    def _camera_blueprint(self, blueprint_library):
        """A plain RGB camera at the declared framing, rendering on every step."""
        blueprint = blueprint_library.find("sensor.camera.rgb")
        blueprint.set_attribute("image_size_x", str(self.settings.image_width))
        blueprint.set_attribute("image_size_y", str(self.settings.image_height))
        blueprint.set_attribute("fov", str(self.settings.fov_deg))
        blueprint.set_attribute("sensor_tick", "0.0")
        return blueprint

    def _capture(self, queues: list) -> list:
        """One averaged frame per camera, taken from steps this call drives itself.

        A camera usually delivers one frame per step, but not always: a step on which one of them
        delivers nothing has been observed on a long-running server, and a capture that insisted on a
        frame from every camera on every step turned that into a twenty-second stall and then an
        empty exception that said nothing. So this keeps stepping until every camera has supplied
        enough frames, up to a bounded number of extra steps, and says which camera fell short if the
        bound is reached rather than failing anonymously.
        """
        for frames in queues:
            while not frames.empty():
                frames.get()
        wanted = self.settings.average_frames
        gathered: list[list] = [[] for _ in queues]
        for _ in range(wanted * self.settings.capture_step_allowance):
            if all(len(frames) >= wanted for frames in gathered):
                break
            self.world.tick()
            for index, frames in enumerate(queues):
                if len(gathered[index]) >= wanted:
                    continue
                try:
                    image = frames.get(timeout=self.settings.frame_timeout_s)
                except queue.Empty:
                    # Logged rather than swallowed: how often a camera skips a step is a fact about
                    # the server worth having, and the pass being tolerant of it should not hide it.
                    logger.debug("camera %d delivered nothing on a step", index)
                    continue
                pixels = np.frombuffer(image.raw_data, dtype=np.uint8)
                gathered[index].append(
                    pixels.reshape((image.height, image.width, 4))[:, :, :3].astype(np.float32))
        short = [index for index, frames in enumerate(gathered) if len(frames) < wanted]
        if short:
            raise RuntimeError(
                "camera " + ", ".join(str(index) for index in short)
                + f" delivered fewer than {wanted} frames in "
                + f"{wanted * self.settings.capture_step_allowance} simulation steps")
        return [sum(frames) / wanted for frames in gathered]

    def _lit_pixels(self, reference: list, candidate: list) -> int:
        """Pixels that gained more than the threshold, over the declared region, worst camera wins.

        Worst camera wins because a lamp is visible from one side of a vehicle and not the other, and
        a verdict taken from the average of the two would let the camera that cannot see the lamp
        dilute the one that can.
        """
        best = 0
        for before, after in zip(reference, candidate, strict=True):
            gain = np.clip(after - before, 0.0, None).max(axis=2)
            rows, columns = gain.shape
            left, top, right, bottom = self.settings.region
            window = gain[int(top * rows):int(bottom * rows),
                          int(left * columns):int(right * columns)]
            best = max(best, int((window > self.settings.luminance_threshold).sum()))
        return best
