"""Whether a capture camera's view is ready to be written: its photoreal tiles in, and its picture settled.

`03_CoSimulation_Runtime.md` §9.5.1. Cesium selects and refines tiles on the world tick, from the views
registered for that tick, so this readiness progresses only while the world ticks. It has two
witnesses, because neither can see what the other sees:

* **The tiles** -- only the server can say. `world.get_view_readiness(camera)` answers as of the end
  of the last tick. The tiles are in when the camera was among the views published on that tick and
  every visible tileset reads `load_progress` 100 with no failed tile in view. `load_progress` alone
  is not enough: it reads 100 for a camera not yet published, whose view has selected nothing, and
  after a failure, because a failed tile is never retried, counts as loaded and is drawn empty.
* **The picture** -- only the camera can say. With every tile in, the renderer still moves the
  picture for tens of the camera's own frames. It has settled when the camera's frame differs from its
  frame ten frames earlier by at most 0.5 grey levels in its worst 80-pixel block, measured as the 27
  placements behind the rule measured it: BT.601 grey from the camera's BGRA, the mean of each 4 x 4
  pixels truncated to a grey level, and the mean absolute difference over each 20 x 20 of those. A
  whole-frame mean would hide one tile refining in a corner. Only frames rendered on or after the tick
  the tiles were answered in for count, and a view whose tiles stop being in starts its picture again.

Each witness has a ceiling in the unit it progresses in, and a ceiling only fails: 90 s of wall clock
for the tiles -- above the 60 s request timeout, so a stalled request shows first as a failed tile --
and 120 of the camera's frames for the picture, counted from the tiles being in. Neither is a count a
caller supplies.

**The gate never ticks the world.** `ViewReadinessGate.after_step` is called by the process that owns
the clock after each of its steps, and asks the server once then; a wait that did not tick would have
nothing new to ask about. The frames are watched by listening to each camera for as long as the gate
holds it, reduced on the stream's thread as they arrive, and compared in frame order on the caller's.

**What a readiness record describes.** A view as of the end of a step: the tiles are asked about once
per SUMO step, so the ticks until they were in are counted to within one step, and the picture is
compared frame by frame. Nothing here is written into a capture. An image reaches a recorder several
ticks after its frame, and the server answers only for the last tick, so a capture's own readiness
would need the server to publish it per frame, which it does not.
"""
from __future__ import annotations

import logging
import math
import statistics
import threading
import time
from collections import deque
from collections.abc import Callable
from typing import Any

import numpy as np

TILES_CEILING_S = 90.0
PICTURE_CEILING_FRAMES = 120
PICTURE_SPAN_FRAMES = 10
PICTURE_TOLERANCE_LEVELS = 0.5
PICTURE_DOWNSAMPLE = 4
# The comparison block, in downsampled pixels: 80 pixels at the camera's resolution.
PICTURE_BLOCK = 20
PICTURE_BLOCK_PX = PICTURE_BLOCK * PICTURE_DOWNSAMPLE
# A frame and the one ten before it, both rendered at the pose the window opens on.
FEWEST_FRAMES = PICTURE_SPAN_FRAMES + 1
# How long the window's opening waits for a camera's frames still in flight from the prewarm.
FRAME_DRAIN_S = 5.0
RULE = "03 §9.5.1"

NOT_STARTED = "not started"
WAITING_FOR_TILES = "waiting for tiles"
SETTLING = "settling"
READY = "ready"

PER_CAPTURE = ("not recorded: the server answers for the view as of the last tick, and an image "
               "reaches the recorder several ticks after its frame, so a capture's own readiness "
               "needs the server to publish it per frame, on the observer snapshot, which it does "
               "not; an orbit's readiness as the window opens says nothing of the ground it sweeps "
               "afterwards")


def visible_tilesets(readiness: dict) -> list[dict]:
    return [tileset for tileset in readiness.get("tilesets", []) if tileset.get("visible")]


def tiles_in(readiness: dict) -> bool:
    """The camera's view drove the tiles' selection on the tick answered for, and every visible
    tileset has loaded all of it with no failed tile in view.

    A failed tile is drawn empty and counts as loaded, so progress at 100 with a failure in view is
    a frame with a hole in it, and is not in.
    """
    visible = visible_tilesets(readiness)
    return (bool(readiness.get("published")) and bool(visible)
            and all(tileset["load_progress"] >= 100.0 and tileset["failed_in_view"] == 0
                    for tileset in visible))


def describe_tiles(readiness: dict | None) -> str:
    """Where the tiles stand, in a few words to follow "tiles": in, a percentage, a failure."""
    if readiness is None:
        return "not asked yet"
    if not readiness.get("published"):
        return "not published for this view yet"
    visible = visible_tilesets(readiness)
    if not visible:
        return "absent: no visible tileset"
    if tiles_in(readiness):
        return "in"
    progress = min(float(tileset["load_progress"]) for tileset in visible)
    failed = sum(int(tileset["failed_in_view"]) for tileset in visible)
    text = f"{progress:.0f}%"
    if failed:
        text += f", {failed} failed in view"
    return text


def describe_view(view: dict) -> str:
    """One channel's wait for its view (`ChannelReadiness.to_dict`) in a line: where each
    witness stands."""
    tiles, picture = view.get("tiles"), view.get("picture")
    if view["state"] == NOT_STARTED:
        return "its wait has not begun"
    text = (f"tiles in at frame {tiles['in_at_frame']} after {tiles['ticks']} ticks, "
            f"{tiles['wall_s']:.1f} s" if tiles else f"waiting for tiles ({view['tiles_now']})")
    if picture:
        text += (f"; picture settled at frame {picture['settled_at_frame']} after "
                 f"{picture['frames']} frames, worst block {picture['residual_levels']:.2f} "
                 "grey levels")
    elif tiles:
        comparison = view.get("last_comparison")
        text += ("; picture settling" if comparison is None else
                 f"; picture settling, last {comparison['worst_block_levels']:.2f} grey levels "
                 f"after {comparison['frames_since_tiles']} frames")
    if view.get("relapses"):
        text += f"; tiles stopped being in {len(view['relapses'])} time(s)"
    return text


def grey_small(raw: bytes, width: int, height: int) -> np.ndarray:
    """A BGRA frame as BT.601 grey, each 4 x 4 pixels averaged and truncated to a level."""
    image = np.frombuffer(raw, dtype=np.uint8)[:width * height * 4].reshape(height, width, 4)
    rgb = image[:, :, :3].astype(np.float32)
    grey = 0.114 * rgb[:, :, 0] + 0.587 * rgb[:, :, 1] + 0.299 * rgb[:, :, 2]
    rows = (height // PICTURE_DOWNSAMPLE) * PICTURE_DOWNSAMPLE
    columns = (width // PICTURE_DOWNSAMPLE) * PICTURE_DOWNSAMPLE
    small = grey[:rows, :columns].reshape(rows // PICTURE_DOWNSAMPLE, PICTURE_DOWNSAMPLE,
                                          columns // PICTURE_DOWNSAMPLE,
                                          PICTURE_DOWNSAMPLE).mean(axis=(1, 3))
    return np.clip(small, 0, 255).astype(np.uint8)


def worst_block(newer: np.ndarray, older: np.ndarray) -> tuple[float, tuple[int, int]]:
    """The largest mean absolute grey difference over any 80-pixel block, and that block's top-left
    corner in the camera's pixels (x, y)."""
    difference = np.abs(newer.astype(np.float32) - older.astype(np.float32))
    rows = (difference.shape[0] // PICTURE_BLOCK) * PICTURE_BLOCK
    columns = (difference.shape[1] // PICTURE_BLOCK) * PICTURE_BLOCK
    if rows == 0 or columns == 0:
        return float(difference.mean()), (0, 0)
    blocks = difference[:rows, :columns].reshape(rows // PICTURE_BLOCK, PICTURE_BLOCK,
                                                 columns // PICTURE_BLOCK,
                                                 PICTURE_BLOCK).mean(axis=(1, 3))
    row, column = np.unravel_index(int(np.argmax(blocks)), blocks.shape)
    return float(blocks[row, column]), (int(column) * PICTURE_BLOCK_PX,
                                        int(row) * PICTURE_BLOCK_PX)


def hold_lead_s(capture_hz: float, step_s: float) -> float:
    """How long before the window opens a camera that follows the traffic stops and holds the pose
    it opens on: the picture witness's ceiling in the camera's frames, in whole SUMO steps."""
    return math.ceil(PICTURE_CEILING_FRAMES / (capture_hz * step_s) - 1e-9) * step_s


def wait_begins_s(window_begin_s: float, first_rendered_s: float, step_s: float,
                  capture_hz: float, follows_traffic: bool) -> float:
    """Where every camera's wait for its view begins: the prewarm's first rendered instant, or --
    where a stare follows the rendered traffic -- where that stare stops to hold its pose, one
    picture ceiling of its frames before the window opens, and never before the prewarm's first
    step has rendered traffic to measure. The tiles' figures cover every registered view, so no
    camera's wait begins while another is still moving."""
    if not follows_traffic:
        return first_rendered_s
    return max(window_begin_s - hold_lead_s(capture_hz, step_s), first_rendered_s + step_s)


def frames_to_witness(held_s: float, capture_hz: float, step_s: float) -> float:
    """The camera's frames a view held for `held_s` before the window opens renders after the tiles
    are first asked about, one SUMO step into the hold."""
    return (held_s - step_s) * capture_hz


class ViewNotReadyError(Exception):
    """A channel's view is not ready: a witness reached its ceiling, or the window opened first."""

    def __init__(self, sensor_id: str, witness: str, message: str) -> None:
        self.sensor_id = sensor_id
        self.witness = witness
        super().__init__(message)


class CameraFrames:
    """A camera's frames as it delivers them, reduced for the picture witness on arrival.

    Called on the stream's thread; a frame that cannot be read is counted, never raised there. Frames
    are counted from the first, and kept only once the wait has begun (`keep`): a camera following
    the traffic renders hundreds of frames before it holds, none of which the witness compares.
    """

    def __init__(self) -> None:
        self._pending: list[tuple[int, np.ndarray]] = []
        self._newest: int | None = None
        self._keeping = False
        self._condition = threading.Condition()
        self.received = 0
        self.unreadable = 0

    def keep(self) -> None:
        """Keep every frame from here on, and none from before."""
        with self._condition:
            self._pending = []
            self._keeping = True

    def __call__(self, image: Any) -> None:
        with self._condition:
            self.received += 1
            keeping = self._keeping
        if not keeping:
            return
        try:
            frame = int(image.frame)
            small = grey_small(image.raw_data, int(image.width), int(image.height))
        except Exception:
            with self._condition:
                self.unreadable += 1
            return
        with self._condition:
            self._pending.append((frame, small))
            self._newest = frame if self._newest is None else max(self._newest, frame)
            self._condition.notify_all()

    @property
    def newest(self) -> int | None:
        with self._condition:
            return self._newest

    def take(self) -> list[tuple[int, np.ndarray]]:
        """Every frame arrived since the last take, in frame order."""
        with self._condition:
            taken, self._pending = self._pending, []
        return sorted(taken, key=lambda pair: pair[0])

    def wait_for(self, frame: int, timeout_s: float) -> bool:
        """Wait until a frame at or after `frame` has arrived, for at most `timeout_s`."""
        deadline = time.monotonic() + timeout_s
        with self._condition:
            while self._newest is None or self._newest < frame:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    return False
                self._condition.wait(remaining)
            return True


class ChannelReadiness:
    """One capture camera's wait: its tiles, then its picture, then held until the window opens."""

    def __init__(self, sensor_id: str, camera_id: int, ask: Callable[[], dict],
                 frames: CameraFrames, clock: Callable[[], float], ticks_per_frame: int) -> None:
        self.sensor_id = sensor_id
        self.camera_id = camera_id
        self._ask = ask
        self.frames = frames
        self._clock = clock
        self.ticks_per_frame = max(1, int(ticks_per_frame))
        self.state = NOT_STARTED
        self.began: dict | None = None
        self.tiles: dict | None = None
        self.picture: dict | None = None
        self.last_answer: dict | None = None
        self.last_comparison: dict | None = None
        self.relapses: list[dict] = []
        self.ready_at_window_open: bool | None = None
        self._asked_ms: list[float] = []
        self._tiles_wait_wall = 0.0
        self._tiles_wait_ticks = 0
        self._settle_from = 0
        self._compared = 0
        self._history: deque[tuple[int, np.ndarray]] = deque(maxlen=PICTURE_SPAN_FRAMES + 1)

    @property
    def ready(self) -> bool:
        return self.state == READY

    def begin(self, ticks: int, rendered_s: float) -> None:
        """The camera holds the pose the window opens on from here: forget every frame before."""
        self.frames.keep()
        now = self._clock()
        self.began = {"sim_time_s": rendered_s, "ticks": ticks}
        self._wait_for_tiles(now, ticks)

    def observe(self, ticks: int) -> list[tuple[int, str]]:
        """After a step: ask about the tiles, compare the frames that arrived, and say what changed,
        each with the logging level it is said at.

        Raises:
            ViewNotReadyError: a ceiling was reached, or the server could not be asked.
        """
        started = time.perf_counter()
        try:
            answer = self._ask()
        except Exception as failure:
            raise ViewNotReadyError(
                self.sensor_id, "tiles",
                f"channel {self.sensor_id}: the server could not say whether camera "
                f"{self.camera_id}'s photoreal tiles are in ({failure!r}), and a capture is not "
                f"written before they are ({RULE})") from None
        self._asked_ms.append((time.perf_counter() - started) * 1000.0)
        self.last_answer = answer
        frame = int(answer["frame"])
        now = self._clock()
        arrived = self.frames.take()
        said: list[tuple[int, str]] = []
        if not tiles_in(answer):
            if self.state in (SETTLING, READY):
                self.relapses.append({"frame": frame, "was": self.state,
                                      "tiles": describe_tiles(answer)})
                said.append((logging.WARNING,
                             f"channel {self.sensor_id}: its tiles stopped being in at frame "
                             f"{frame} ({describe_tiles(answer)}); its picture starts again once "
                             "they are back"))
                self._wait_for_tiles(now, ticks)
            waited = now - self._tiles_wait_wall
            if waited >= TILES_CEILING_S:
                raise ViewNotReadyError(
                    self.sensor_id, "tiles",
                    f"channel {self.sensor_id}: its photoreal tiles were not in within "
                    f"{TILES_CEILING_S:.0f} s of wall clock ({RULE}): at frame {frame}, "
                    f"{ticks - self._tiles_wait_ticks} ticks and {waited:.1f} s into the wait, "
                    f"the tiles were {describe_tiles(answer)}{self._tilesets_text(answer)}")
            return said
        if self.state == WAITING_FOR_TILES:
            self.state = SETTLING
            self.tiles = {"in_at_frame": frame, "ticks": ticks - self._tiles_wait_ticks,
                          "wall_s": round(now - self._tiles_wait_wall, 3)}
            self._settle_from = frame
            self._compared = 0
            self._history.clear()
            said.append((logging.INFO,
                         f"channel {self.sensor_id}: tiles in at frame {frame}, "
                         f"{self.tiles['ticks']} ticks and {self.tiles['wall_s']:.1f} s into the "
                         "wait"))
        if self.state == SETTLING:
            said.extend(self._settle(arrived))
        return said

    def drain(self, timeout_s: float) -> list[tuple[int, str]]:
        """Compare the prewarm's last frames still in flight, where the picture has yet to settle."""
        if self.state != SETTLING or self.last_answer is None:
            return []
        # The camera's newest frame at or before the last tick answered for, allowing a tick of
        # jitter in the sensor's own period.
        expected = int(self.last_answer["frame"]) - self.ticks_per_frame
        if expected >= self._settle_from:
            self.frames.wait_for(expected, timeout_s)
        return self._settle(self.frames.take())

    def _wait_for_tiles(self, now: float, ticks: int) -> None:
        self.state = WAITING_FOR_TILES
        self.tiles = None
        self.picture = None
        self.last_comparison = None
        self._tiles_wait_wall = now
        self._tiles_wait_ticks = ticks

    def _settle(self, arrived: list[tuple[int, np.ndarray]]) -> list[tuple[int, str]]:
        for frame, small in arrived:
            if frame < self._settle_from or (self._history and frame <= self._history[-1][0]):
                continue
            if self._compared >= PICTURE_CEILING_FRAMES:
                raise ViewNotReadyError(
                    self.sensor_id, "picture",
                    f"channel {self.sensor_id}: its picture did not settle within "
                    f"{PICTURE_CEILING_FRAMES} of the camera's frames once its tiles were in at "
                    f"frame {self._settle_from} ({RULE}): {self._comparison_text()}")
            self._compared += 1
            self._history.append((frame, small))
            if len(self._history) <= PICTURE_SPAN_FRAMES:
                continue
            older_frame, older = self._history[0]
            levels, corner = worst_block(small, older)
            self.last_comparison = {"frame": frame, "against_frame": older_frame,
                                    "worst_block_levels": round(levels, 3),
                                    "worst_block_px": list(corner),
                                    "frames_since_tiles": self._compared}
            if levels <= PICTURE_TOLERANCE_LEVELS:
                self.state = READY
                self.picture = {"settled_at_frame": frame, "frames": self._compared,
                                "residual_levels": round(levels, 3),
                                "compared_with_frame": older_frame,
                                "worst_block_px": list(corner)}
                return [(logging.INFO,
                         f"channel {self.sensor_id}: picture settled at frame {frame}, "
                         f"{self._compared} of the camera's frames after its tiles were in; worst "
                         f"80-pixel block {levels:.2f} grey levels from frame {older_frame}")]
        return []

    def _comparison_text(self) -> str:
        comparison = self.last_comparison
        if comparison is None:
            return (f"{self._compared} of its frames arrived since, too few to compare a frame "
                    f"with the one {PICTURE_SPAN_FRAMES} before it")
        x, y = comparison["worst_block_px"]
        return (f"frame {comparison['frame']} differs from frame {comparison['against_frame']}, "
                f"{PICTURE_SPAN_FRAMES} of the camera's frames earlier, by "
                f"{comparison['worst_block_levels']:.2f} grey levels in its worst 80-pixel block "
                f"(at x {x}, y {y}) against {PICTURE_TOLERANCE_LEVELS}")

    @staticmethod
    def _tilesets_text(answer: dict) -> str:
        visible = visible_tilesets(answer)
        if not visible:
            return ""
        parts = [f"ion {tileset['ion_asset_id']}: progress {float(tileset['load_progress']):.1f}, "
                 f"queued {tileset['loading']['worker_queue']}/{tileset['loading']['main_queue']}, "
                 f"kicked {tileset['loading']['kicked']}, failed in view "
                 f"{tileset['failed_in_view']}, failed loaded {tileset['failed_loaded']}"
                 for tileset in visible]
        return " (" + "; ".join(parts) + ")"

    def not_ready_message(self, window_begin_s: float) -> str:
        """Why the view is not ready as the window opens, naming the witness and its state."""
        head = (f"channel {self.sensor_id}: its view was not ready when the window opened at "
                f"t={window_begin_s:g} ({RULE})")
        if self.state == NOT_STARTED:
            return f"{head}: its camera never held the pose the window opens on during the prewarm"
        if self.state == WAITING_FOR_TILES:
            frame = "" if self.last_answer is None else f" at frame {self.last_answer['frame']}"
            return (f"{head}: the tiles{frame} were {describe_tiles(self.last_answer)}"
                    f"{'' if self.last_answer is None else self._tilesets_text(self.last_answer)}")
        return (f"{head}: the tiles were in at frame {self.tiles['in_at_frame']}, "
                f"{self.tiles['ticks']} ticks and {self.tiles['wall_s']:.1f} s into the wait, and "
                f"the picture had not settled: {self._comparison_text()}")

    def to_dict(self) -> dict:
        asked = None
        if self._asked_ms:
            asked = {"count": len(self._asked_ms),
                     "median_ms": round(statistics.median(self._asked_ms), 3),
                     "worst_ms": round(max(self._asked_ms), 3)}
        return {"sensor_id": self.sensor_id, "camera_id": self.camera_id, "state": self.state,
                "wait_began": self.began, "tiles": self.tiles, "picture": self.picture,
                "tiles_now": describe_tiles(self.last_answer),
                "last_answer": None if self.last_answer is None else {
                    "frame": int(self.last_answer["frame"]),
                    "published": bool(self.last_answer.get("published")),
                    "visible_tilesets": visible_tilesets(self.last_answer)},
                "last_comparison": self.last_comparison, "relapses": list(self.relapses),
                "frames_received": self.frames.received,
                "frames_unreadable": self.frames.unreadable,
                "readiness_asked": asked, "ready_at_window_open": self.ready_at_window_open}


class ViewReadinessGate:
    """Every capture camera's wait, told after each of the session's steps and never ticking."""

    def __init__(self, clock: Callable[[], float] = time.monotonic,
                 logger: logging.Logger | None = None, drain_s: float = FRAME_DRAIN_S) -> None:
        self._clock = clock
        self.logger = logger or logging.getLogger(__name__)
        self.drain_s = drain_s
        self.channels: list[ChannelReadiness] = []
        self._cameras: list[Any] = []
        self.begun_at: dict | None = None
        self.window_opens_at_s: float | None = None

    @property
    def begun(self) -> bool:
        return self.begun_at is not None

    def watch(self, sensor_id: str, camera: Any, world: Any,
              ticks_per_frame: int) -> ChannelReadiness:
        """Listen to a capture camera's frames and ask about its tiles through `world`."""
        frames = CameraFrames()
        channel = ChannelReadiness(sensor_id, int(camera.id),
                                   lambda: world.get_view_readiness(camera), frames, self._clock,
                                   ticks_per_frame)
        camera.listen(frames)
        self._cameras.append(camera)
        self.channels.append(channel)
        return channel

    def begin(self, ticks: int, rendered_s: float) -> None:
        """Every camera now holds the pose the window opens on: the wait starts."""
        self.begun_at = {"sim_time_s": rendered_s, "ticks": ticks}
        for channel in self.channels:
            channel.begin(ticks, rendered_s)

    def after_step(self, ticks: int) -> list[tuple[int, str]]:
        """Ask about every channel after a step the session rendered; say what changed."""
        said: list[tuple[int, str]] = []
        for channel in self.channels:
            said.extend(channel.observe(ticks))
        return said

    def at_window_open(self, window_begin_s: float) -> list[ChannelReadiness]:
        """Compare what is still in flight, then return every channel not ready as the window
        opens."""
        self.window_opens_at_s = window_begin_s
        for channel in self.channels:
            for level, line in channel.drain(self.drain_s):
                self.logger.log(level, "%s", line)
            channel.ready_at_window_open = channel.ready
        return [channel for channel in self.channels if not channel.ready]

    def stop(self) -> None:
        """Stop listening to every camera. Safe to call more than once."""
        for camera in self._cameras:
            try:
                camera.stop()
            except Exception as failure:
                self.logger.warning("could not stop listening to camera %s: %r",
                                    getattr(camera, "id", "?"), failure)
        self._cameras = []

    def to_dict(self) -> dict:
        return {"rule": RULE, "tiles_ceiling_s": TILES_CEILING_S,
                "picture_ceiling_frames": PICTURE_CEILING_FRAMES,
                "picture_span_frames": PICTURE_SPAN_FRAMES,
                "picture_tolerance_levels": PICTURE_TOLERANCE_LEVELS,
                "picture_block_px": PICTURE_BLOCK_PX,
                "asked": "once per SUMO step, after its last tick",
                "wait_began": self.begun_at, "window_opens_at_s": self.window_opens_at_s,
                "per_capture": PER_CAPTURE,
                "channels": [channel.to_dict() for channel in self.channels]}
