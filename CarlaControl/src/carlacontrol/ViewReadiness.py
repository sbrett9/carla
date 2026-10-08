"""Whether a capture camera's view is ready to be written: its photoreal tiles in, and its picture settled
where the run asks for that.

`03_CoSimulation_Runtime.md` §9.5.1. Cesium selects and refines tiles on the world tick, from the views
registered for that tick, so this readiness progresses only while the world ticks. It has two
witnesses, because neither can see what the other sees:

* **The tiles** -- only the server can say. `world.get_view_readiness(camera)` answers as of the end
  of the last tick. The tiles are in when the camera was among the views published on that tick and
  every visible tileset reads `load_progress` 100 with no failed tile in view. `load_progress` alone
  is not enough: it reads 100 for a camera not yet published, whose view has selected nothing, and
  after a failure, because a failed tile is never retried, counts as loaded and is drawn empty.
* **The picture** -- only the camera can say. With every tile in, the renderer still moves the
  picture for tens of ticks. It has settled when the camera's frame differs from its newest frame at
  least ten ticks earlier by at most 0.5 grey levels in its worst judged 80-pixel block, measured as
  the 27 placements behind the rule measured it: BT.601 grey from the camera's BGRA, the mean of each
  4 x 4 pixels truncated to a grey level, and the mean absolute difference over each 20 x 20 of
  those. A whole-frame mean would hide one tile refining in a corner. Only frames rendered on or after
  the tick the tiles were answered in for count, and a view whose tiles stop being in starts its
  picture again.

**The picture's wait is off by default.** By the owner's ruling of 2026-10-06 -- "It's in a state
that is not something I'd like to include at publication" -- the picture is waited on only where the
run configuration sets `capture.picture_settled_wait` true; its tool default is
`PICTURE_SETTLED_WAIT`, false. Off, a view is ready on the step its tiles are in: the camera's frames
are not listened to, none is compared, and the record says the wait was not run, names the field and
holds no picture figure. The tiles then have a lead of their own, `capture.tiles_hold_s`
(`TILES_HOLD_S`, 10 s): a stare that follows the traffic holds the pose the window opens on for that
long before the window, in whole SUMO steps, and every camera's prewarm must hold it, so the tiles are
asked about after every step of it. The picture's code below is kept as it is and runs, unchanged,
only where the field is true, and then its ceiling sets the hold and the tiles' lead is not read. It is off for a fault left unfixed: the comparison places each frame's rendered
vehicles from the client's snapshot of that frame, and the gate is asked once per SUMO step, after the
step's last tick, so at Bahonar's 1 s step of twenty ticks the client no longer holds the older
compared frame's snapshot by then and every comparison is `vehicles_unknown` -- 59 of 59 on the guard
run of 2026-10-06, refused at pre-roll. Arapahoe steps every tick, so it never showed there. The owner
had also found the wait too strict.

**The span is counted in ticks; the ceiling in the camera's own frames.** The placements were
measured with a camera rendering every tick, where ten frames and ten ticks are the same thing. A
frame is compared with the newest one rendered ten or more ticks before it -- the frame before it at
2 Hz, the frame ten before it at 20 Hz. The ceiling was first written as 120 of the camera's frames,
then as 120 ticks after one Bahonar run at 2 Hz whose view had settled five frames after its tiles,
and is counted in the camera's frames again by the owner's ruling of 2026-10-06, from what the
Arapahoe check stare measured: judged under a running session at 2 Hz, its worst block fell 2.63,
1.90, 2.04, 1.53, 1.15, 0.94, 0.76, 0.73, 0.73, 0.62 gray levels from its 4th to its 13th frame,
heading under 0.5 around its 15th, just past the 120 ticks, which at 2 Hz are twelve frames; the run
that passed the day before was judged on the camera's 72nd frame only because its tiles took 712
ticks to arrive. The capture component keeps its rendering state between captures, so what settles
-- antialiasing history, exposure, global illumination -- advances once per frame the camera draws
and not once per tick. So the ceiling is a number of frames the camera has drawn since its tiles
came in, `PICTURE_CEILING_FRAMES` (60: 30 s at 2 Hz) unless the run configuration says otherwise.

**The question is the world's rendering, not the scene's stillness.** A vehicle driving through the
view changes its blocks whatever the tiles are doing: on Bahonar, 56 rendered vehicles held the worst
block at 1.8 to 2.0 grey levels for 120 frames while the same view with none settled in 16. So every
block a rendered vehicle covers, in either frame of a comparison, is left out of it. A vehicle covers
its box -- the bounding box its actor description reports, posed where the client's snapshot of that
frame holds its body, for every body the session's render set says the frame drew (03 §8.9) --
projected from the camera's pose in the same snapshot with the pinhole the occlusion estimate uses
(`OcclusionEstimator`), together with the shadow the box casts on the ground from the world's sun, and
a margin of `VEHICLE_MARGIN_PX` pixels. A comparison that leaves less than half the view's blocks to
judge is not a judgement of the view, and neither is one whose vehicles could not be placed, because
the render set or the snapshot of a frame was no longer held: neither can settle the picture, and a
view that stays so until the ceiling is refused with that as the reason.

Each witness has a ceiling in the unit it progresses in, and a ceiling only fails: 90 s of wall clock
for the tiles -- above the 60 s request timeout, so a stalled request shows first as a failed tile --
and for the picture a number of the camera's frames, counted from the first rendered on or after the
tick its tiles were answered in for. The tiles' ceiling is not a count a caller supplies. The
picture's ceiling and its tolerance are session-fixed run-configuration fields,
`capture.picture_ceiling_frames` and `capture.picture_tolerance_levels`, whose tool defaults are
`PICTURE_CEILING_FRAMES` and `PICTURE_TOLERANCE_LEVELS` here, so either can be changed for a run
without a code change and the lock records the value; the ten-tick span is not a field.

**The gate never ticks the world.** `ViewReadinessGate.after_step` is called by the process that owns
the clock after each of its steps, and asks the server once then; a wait that did not tick would have
nothing new to ask about. Where the picture is waited on, the frames are watched by listening to
each camera for as long as the gate holds it, reduced on the stream's thread as they arrive, and
compared in frame order on the caller's, which is also where each frame's vehicles are placed.

**What a readiness record describes.** A view as of the end of a step: the tiles are asked about once
per SUMO step, so the ticks until they were in are counted to within one step, and the picture is
compared frame by frame. Every comparison is kept on the record, with how many frames the camera had
delivered at each of its two frames and whether the camera was posed from the snapshot or where it is
held: a refusal then shows whether the picture was converging and how fast, against the camera's own
frame count, the count the ceiling is in. The three refusals of 2026-10-06 were judged over a
camera's 2nd to 13th frames, its tiles in 4 ticks after it was spawned, with no vehicle within 250 m
of the view; the run that passed the day before was judged over the same camera's 72nd and 73rd
frames, its tiles in after 712 ticks (03 §9.5.1). Nothing here is written into a capture. An image
reaches a recorder several ticks after its frame, and the server answers only for the last tick, so a
capture's own readiness would need the server to publish it per frame, which it does not.
"""
from __future__ import annotations

import logging
import math
import statistics
import threading
import time
from collections import deque
from collections.abc import Callable
from dataclasses import dataclass
from typing import Any

import numpy as np

TILES_CEILING_S = 90.0
PICTURE_SPAN_TICKS = 10
# The tool default of capture.picture_settled_wait: whether the picture is waited on once the tiles
# are in. Off, by the owner's ruling of 2026-10-06.
PICTURE_SETTLED_WAIT = False
# The tool default of capture.tiles_hold_s: with the picture's wait off, how long every camera holds
# the pose the window opens on before it, its tiles asked about after each step of that hold. 10 s
# covers every cold tile load measured, 30 to 124 ticks at 0.05 s, 1.5 to 6.2 s; one outlier took 712.
TILES_HOLD_S = 10.0
# The tool defaults of capture.picture_ceiling_frames and capture.picture_tolerance_levels: how many
# of its own frames a camera has, from its tiles being in, to settle its picture (30 s at 2 Hz), and
# the gray levels within which a frame counts as settled against its frame ten ticks earlier.
PICTURE_CEILING_FRAMES = 60
PICTURE_TOLERANCE_LEVELS = 0.5
# The share of the view's blocks a comparison must be left to judge once the vehicles are excluded.
PICTURE_MIN_JUDGED_SHARE = 0.5
PICTURE_DOWNSAMPLE = 4
# The comparison block, in downsampled pixels: 80 pixels at the camera's resolution.
PICTURE_BLOCK = 20
PICTURE_BLOCK_PX = PICTURE_BLOCK * PICTURE_DOWNSAMPLE
# Pixels added round a vehicle's projected box and shadow, for the single-precision poses and the
# antialiased edge the renderer draws.
VEHICLE_MARGIN_PX = 4
# How long the window's opening waits for a camera's frames still in flight from the prewarm.
FRAME_DRAIN_S = 5.0
RULE = ("run check 50: every channel's view is ready as the window opens -- its photoreal tiles "
        "in and, where capture.picture_settled_wait is true, its picture settled")
# Points nearer than this to the camera are at or behind the lens, as in `OcclusionEstimator`.
NEAR_PLANE_M = 0.1

NOT_STARTED = "not started"
WAITING_FOR_TILES = "waiting for tiles"
SETTLING = "settling"
READY = "ready"

TOO_FEW_BLOCKS = "too_few_blocks"
VEHICLES_UNKNOWN = "vehicles_unknown"

PER_CAPTURE = ("not recorded: the server answers for the view as of the last tick, and an image "
               "reaches the recorder several ticks after its frame, so a capture's own readiness "
               "needs the server to publish it per frame, on the observer snapshot, which it does "
               "not; an orbit's readiness as the window opens says nothing of the ground it sweeps "
               "afterwards")
PICTURE_WAIT_OFF = ("not run: capture.picture_settled_wait is false, its default, so each view "
                    "waited for its tiles only and no camera frame was compared; the wait is kept, "
                    "and runs where capture.picture_settled_wait is true")
VEHICLES = ("excluded: every 80-pixel block covered, in either frame of a comparison, by a rendered "
            "vehicle's box and its ground shadow, projected from the client's snapshot of that frame "
            f"with a {VEHICLE_MARGIN_PX}-pixel margin; a comparison left less than "
            f"{PICTURE_MIN_JUDGED_SHARE:.0%} of the blocks to judge, or whose vehicles could not be "
            "placed, does not count")


# -- the tiles -------------------------------------------------------------------------------------

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
    witness stands, or where the tiles stand where the picture is not waited on."""
    tiles, picture = view.get("tiles"), view.get("picture")
    if view["state"] == NOT_STARTED:
        return "its wait has not begun"
    text = (f"tiles in at frame {tiles['in_at_frame']} after {tiles['ticks']} ticks, "
            f"{tiles['wall_s']:.1f} s" if tiles else f"waiting for tiles ({view['tiles_now']})")
    # A record written before capture.picture_settled_wait existed always waited on the picture.
    if not view.get("picture_settled_wait", True):
        if tiles:
            text += "; picture-settled wait off"
    elif picture:
        text += (f"; picture settled at frame {picture['settled_at_frame']}, its frame "
                 f"{picture['frames']} since its tiles ({picture['ticks_since_tiles']} ticks), worst "
                 f"judged block {picture['residual_levels']:.2f} gray levels with "
                 f"{picture['judged_share']:.0%} of its blocks judged")
    elif tiles:
        comparison = view.get("last_comparison")
        text += "; picture settling"
        if comparison is not None:
            text += f", {comparison_outcome(comparison)}"
    if view.get("relapses"):
        text += f"; tiles stopped being in {len(view['relapses'])} time(s)"
    return text


def comparison_outcome(comparison: dict) -> str:
    """What one comparison found, in a few words."""
    if comparison["reason"] == VEHICLES_UNKNOWN:
        return (f"the vehicles of frame {comparison['frame']} or {comparison['against_frame']} "
                "could not be placed")
    if comparison["reason"] == TOO_FEW_BLOCKS:
        return (f"vehicles covered {comparison['excluded_blocks']} of {comparison['blocks']} "
                f"blocks, {comparison['judged_share']:.0%} left against "
                f"{PICTURE_MIN_JUDGED_SHARE:.0%}")
    return (f"last {comparison['worst_block_levels']:.2f} gray levels with "
            f"{comparison['judged_share']:.0%} of its blocks judged")


# -- the picture -----------------------------------------------------------------------------------

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


def block_grid(width: int, height: int) -> tuple[int, int]:
    """How many whole 80-pixel blocks a frame of this size is compared over: rows, columns."""
    return ((height // PICTURE_DOWNSAMPLE) // PICTURE_BLOCK,
            (width // PICTURE_DOWNSAMPLE) // PICTURE_BLOCK)


def block_differences(newer: np.ndarray, older: np.ndarray) -> np.ndarray:
    """The mean absolute grey difference over each 80-pixel block, rows by columns."""
    difference = np.abs(newer.astype(np.float32) - older.astype(np.float32))
    rows = (difference.shape[0] // PICTURE_BLOCK) * PICTURE_BLOCK
    columns = (difference.shape[1] // PICTURE_BLOCK) * PICTURE_BLOCK
    return difference[:rows, :columns].reshape(rows // PICTURE_BLOCK, PICTURE_BLOCK,
                                               columns // PICTURE_BLOCK,
                                               PICTURE_BLOCK).mean(axis=(1, 3))


def worst_block(newer: np.ndarray, older: np.ndarray,
                excluded: np.ndarray | None = None) -> tuple[float | None, tuple[int, int] | None]:
    """The largest mean absolute grey difference over any 80-pixel block not `excluded`, and that
    block's top-left corner in the camera's pixels (x, y); (None, None) where every block is."""
    blocks = block_differences(newer, older)
    if blocks.size == 0:
        return float(np.abs(newer.astype(np.float32) - older.astype(np.float32)).mean()), (0, 0)
    if excluded is not None:
        blocks = np.where(excluded, -1.0, blocks)
        if not (blocks >= 0.0).any():
            return None, None
    row, column = np.unravel_index(int(np.argmax(blocks)), blocks.shape)
    return float(blocks[row, column]), (int(column) * PICTURE_BLOCK_PX,
                                        int(row) * PICTURE_BLOCK_PX)


# -- where the vehicles are in the picture -----------------------------------------------------------

@dataclass(frozen=True)
class Pose:
    """A pose in CARLA's frame: metres, x east, y south; degrees, CARLA's angle convention."""

    x: float
    y: float
    z: float
    pitch: float = 0.0
    yaw: float = 0.0
    roll: float = 0.0

    @classmethod
    def of(cls, transform: Any) -> Pose:
        """From a transform: the shim's, CarlaNet's, or anything with `location` and `rotation`."""
        location, rotation = transform.location, transform.rotation
        return cls(float(location.x), float(location.y), float(location.z),
                   float(rotation.pitch), float(rotation.yaw), float(rotation.roll))


@dataclass(frozen=True)
class VehicleBox:
    """The box an actor description reports, in the actor's own frame: its centre, half-sizes and
    rotation."""

    location: tuple[float, float, float]
    extent: tuple[float, float, float]
    rotation: tuple[float, float, float] = (0.0, 0.0, 0.0)

    @classmethod
    def of(cls, box: Any) -> VehicleBox:
        location, extent, rotation = box.location, box.extent, box.rotation
        return cls((float(location.x), float(location.y), float(location.z)),
                   (float(extent.x), float(extent.y), float(extent.z)),
                   (float(rotation.pitch), float(rotation.yaw), float(rotation.roll)))


@dataclass(frozen=True)
class FrameVehicles:
    """Where one frame's rendered vehicles stood, the camera the frame was seen from, and the sun.

    `sun` is (elevation, azimuth) in degrees, the azimuth clockwise from north; None where the world
    reports no sun, so no shadow is drawn.
    """

    camera: Pose
    vehicles: tuple[tuple[Pose, VehicleBox], ...]
    sun: tuple[float, float] | None = None
    camera_from_snapshot: bool = True


def rotation_basis(pitch: float, yaw: float, roll: float) -> np.ndarray:
    """Forward, right and up, as rows, for a CARLA rotation: `CarlaNet.Types.Geom.RotationBasis`,
    the rotation matrix Rz(yaw) Ry(pitch) Rx(roll) whose columns those axes are."""
    cp, sp = math.cos(math.radians(pitch)), math.sin(math.radians(pitch))
    cy, sy = math.cos(math.radians(yaw)), math.sin(math.radians(yaw))
    cr, sr = math.cos(math.radians(roll)), math.sin(math.radians(roll))
    return np.array([[cp * cy, cp * sy, sp],
                     [cy * sp * sr - sy * cr, sy * sp * sr + cy * cr, -cp * sr],
                     [-cy * sp * cr - sy * sr, -sy * sp * cr + cy * sr, cp * cr]])


def vehicle_footprint(camera: Pose, fov_deg: float, width: int, height: int, vehicle: Pose,
                      box: VehicleBox, sun: tuple[float, float] | None = None,
                      margin_px: float = 0.0) -> tuple[float, float, float, float] | None:
    """The pixel rectangle (u0, v0, u1, v1) a vehicle's box, and the shadow it casts on the ground
    from `sun`, cover in a camera's frame, widened by `margin_px`; None where it is wholly outside
    the frame or has a corner at or behind the lens.

    The box is posed and projected as `OcclusionEstimator` projects it: the actor's rotation carries
    the box's own placement and axes, and the pinhole has square pixels, the principal point at the
    centre and a focal length of width / (2 tan(fov / 2)). The shadow is each corner carried along
    the sun's rays down to the box's lowest corner's height.
    """
    actor = rotation_basis(vehicle.pitch, vehicle.yaw, vehicle.roll)
    local = rotation_basis(*box.rotation)
    centre = np.array([vehicle.x, vehicle.y, vehicle.z]) + actor.T @ np.array(box.location)
    axes = (actor.T @ local.T).T            # the box's forward, right and up in the world
    signs = np.array([[sx, sy, sz] for sz in (-1, 1) for sy in (-1, 1) for sx in (-1, 1)])
    corners = centre + (signs * np.array(box.extent)) @ axes
    if sun is not None and sun[0] > 0.0:
        elevation, azimuth = math.radians(sun[0]), math.radians(sun[1])
        # Away from the sun, in CARLA's frame: the sun's bearing turned round, x east and y south.
        away = np.array([-math.sin(azimuth), math.cos(azimuth), 0.0])
        ground = corners[:, 2].min()
        reach = (corners[:, 2] - ground) / math.tan(elevation)
        shadow = corners + reach[:, None] * away
        shadow[:, 2] = ground
        corners = np.vstack([corners, shadow])
    view = rotation_basis(camera.pitch, camera.yaw, camera.roll)
    relative = corners - np.array([camera.x, camera.y, camera.z])
    forward, right, up = relative @ view[0], relative @ view[1], relative @ view[2]
    if (forward <= NEAR_PLANE_M).any():
        return None
    focal = width / (2.0 * math.tan(math.radians(fov_deg) / 2.0))
    u = width / 2.0 + focal * right / forward
    v = height / 2.0 - focal * up / forward
    u0, u1 = float(u.min()) - margin_px, float(u.max()) + margin_px
    v0, v1 = float(v.min()) - margin_px, float(v.max()) + margin_px
    if u1 < 0.0 or v1 < 0.0 or u0 >= width or v0 >= height:
        return None
    return u0, v0, u1, v1


def blocks_covered(rectangles: list[tuple[float, float, float, float]], width: int,
                   height: int) -> np.ndarray:
    """Which of a frame's 80-pixel blocks any rectangle touches, rows by columns."""
    rows, columns = block_grid(width, height)
    covered = np.zeros((rows, columns), dtype=bool)
    for u0, v0, u1, v1 in rectangles:
        c0 = max(0, int(math.floor(u0 / PICTURE_BLOCK_PX)))
        c1 = min(columns - 1, int(math.floor(u1 / PICTURE_BLOCK_PX)))
        r0 = max(0, int(math.floor(v0 / PICTURE_BLOCK_PX)))
        r1 = min(rows - 1, int(math.floor(v1 / PICTURE_BLOCK_PX)))
        if c0 <= c1 and r0 <= r1:
            covered[r0:r1 + 1, c0:c1 + 1] = True
    return covered


class SessionFrameVehicles:
    """Where a frame's rendered vehicles stood, as the session and the client hold it.

    The bodies are the session's render set of the frame (`session.RenderSet`, 03 §8.9), each posed
    where the client's world snapshot of exactly that frame holds it (`CarlaClient.GetSnapshotFrame`,
    a lookup in memory), with the box its actor description reports, fetched once per body; the
    camera is posed from the same snapshot, or at the pose it is held at where the snapshot does not
    hold it; the sun is the world's, from the observer's cached solar block. A frame whose render set
    or snapshot is no longer held, or holds a rendered body it cannot place, answers None: its
    vehicles cannot be placed.
    """

    def __init__(self, world: Any, render_sets: Any, camera_id: int,
                 held_pose: Callable[[], Any]) -> None:
        self._world = world
        self._render_sets = render_sets
        self._camera_id = int(camera_id)
        self._held_pose = held_pose
        self._boxes: dict[int, VehicleBox] = {}

    def __call__(self, frame: int) -> FrameVehicles | None:
        found, render_set = self._render_sets.TryGetRenderSet(frame, None)
        if not found or render_set is None:
            return None
        bodies = [int(actor) for actor in render_set.ByActor.Keys]
        # The client's snapshot of exactly this frame, or None where it does not hold it; the snapshot
        # is keyed by the actor's uint id, which a plain int does not match.
        snapshot = self._world._client.GetSnapshotFrame(frame)
        if snapshot is None:
            return None
        unknown = [actor for actor in bodies if actor not in self._boxes]
        if unknown:
            for actor in self._world.get_actors(unknown):
                self._boxes[int(actor.id)] = VehicleBox.of(actor.bounding_box)
        vehicles = []
        for actor in bodies:
            held, state = snapshot.TryGetValue(_actor_key(actor), None)
            if not held or actor not in self._boxes:
                return None
            vehicles.append((Pose.of(state.Transform), self._boxes[actor]))
        held, camera = snapshot.TryGetValue(_actor_key(self._camera_id), None)
        pose = Pose.of(camera.Transform) if held else Pose.of(self._held_pose())
        return FrameVehicles(pose, tuple(vehicles), self._sun(), bool(held))

    def _sun(self) -> tuple[float, float] | None:
        state = self._world.get_solar_state()
        if not state or state.get("sun_azimuth_deg") is None:
            return None
        elevation = state.get("sun_corrected_elevation_deg")
        if elevation is None:
            elevation = state.get("sun_elevation_deg")
        return None if elevation is None else (float(elevation), float(state["sun_azimuth_deg"]))


def _actor_key(actor_id: int):
    """An actor id as the client's snapshot is keyed: a .NET uint, where the CarlaNet assemblies are
    loaded; the plain number otherwise, which is what a test's stand-in snapshot takes."""
    try:
        from System import UInt32  # noqa: PLC0415 -- present only once carlanet has loaded .NET
    except ImportError:
        return int(actor_id)
    return UInt32(int(actor_id))


# -- how long a wait needs -------------------------------------------------------------------------

def ticks_to_ceiling(ticks_per_frame: int, ceiling_frames: int) -> int:
    """The ticks the picture's ceiling needs after the tiles are first answered for: the camera's
    `ceiling_frames` frames at its rate, whatever the phase of its period, and the
    `PICTURE_SPAN_TICKS` a frame is compared across. 610 at the default ceiling, 30.5 s."""
    return max(1, int(ticks_per_frame)) * int(ceiling_frames) + PICTURE_SPAN_TICKS


def first_judged_frame(ticks_per_frame: int) -> int:
    """Which of the camera's frames since its tiles were in is the first the picture can be judged
    on: the frame whose frame at least `PICTURE_SPAN_TICKS` before it was also rendered since the
    tiles -- the 2nd at 2 Hz, the 11th at 20 Hz. A ceiling of fewer frames holds no comparison."""
    period = max(1, int(ticks_per_frame))
    return 1 + math.ceil(PICTURE_SPAN_TICKS / period)


def hold_lead_s(step_s: float, delta_s: float, ticks_per_frame: int, ceiling_frames: int,
                *, picture_wait: bool = PICTURE_SETTLED_WAIT,
                tiles_hold_s: float = TILES_HOLD_S) -> float:
    """How long before the window opens a camera that follows the traffic stops and holds the pose
    it opens on, in whole SUMO steps and never less than one. With the picture's wait off, its
    default, `tiles_hold_s` (capture.tiles_hold_s), so the tiles are asked about after every step
    of it: 10 s at the default. With it on, one SUMO step for the tiles to be first asked about and
    the ticks the picture's ceiling needs (`ticks_to_ceiling`), and `tiles_hold_s` is not read: 32 s
    at a 1 s step and the default ceiling."""
    if picture_wait:
        lead = step_s + ticks_to_ceiling(ticks_per_frame, ceiling_frames) * delta_s
    else:
        lead = float(tiles_hold_s)
    return max(1, math.ceil(lead / step_s - 1e-9)) * step_s


def wait_begins_s(window_begin_s: float, first_rendered_s: float, step_s: float, delta_s: float,
                  ticks_per_frame: int, ceiling_frames: int, follows_traffic: bool,
                  *, picture_wait: bool = PICTURE_SETTLED_WAIT,
                  tiles_hold_s: float = TILES_HOLD_S) -> float:
    """Where every camera's wait for its view begins: the prewarm's first rendered instant, or --
    where a stare follows the rendered traffic -- where that stare stops to hold its pose,
    `hold_lead_s` before the window opens, and never before the prewarm's first step has rendered
    traffic to measure. The tiles' figures cover every registered view, so no camera's wait begins
    while another is still moving."""
    if not follows_traffic:
        return first_rendered_s
    return max(window_begin_s - hold_lead_s(step_s, delta_s, ticks_per_frame, ceiling_frames,
                                            picture_wait=picture_wait, tiles_hold_s=tiles_hold_s),
               first_rendered_s + step_s)


def ticks_after_first_ask(held_s: float, step_s: float, delta_s: float) -> float:
    """The ticks a view held for `held_s` before the window opens renders after its tiles are first
    asked about, one SUMO step into the hold."""
    return (held_s - step_s) / delta_s


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
        self._pending: list[tuple[int, np.ndarray, int, int, int]] = []
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
            # How many frames the camera has delivered, this one included: its age in its own frames.
            delivered = self.received
        if not keeping:
            return
        try:
            frame = int(image.frame)
            width, height = int(image.width), int(image.height)
            small = grey_small(image.raw_data, width, height)
        except Exception:
            with self._condition:
                self.unreadable += 1
            return
        with self._condition:
            self._pending.append((frame, small, width, height, delivered))
            self._newest = frame if self._newest is None else max(self._newest, frame)
            self._condition.notify_all()

    @property
    def newest(self) -> int | None:
        with self._condition:
            return self._newest

    def take(self) -> list[tuple[int, np.ndarray, int, int, int]]:
        """Every frame arrived since the last take, in frame order: frame, reduced grey, size, and
        how many frames the camera had delivered by then, that one included."""
        with self._condition:
            taken, self._pending = self._pending, []
        return sorted(taken, key=lambda entry: entry[0])

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


@dataclass
class _Frame:
    """One frame kept for comparing: its reduced picture, the blocks its vehicles cover (None where
    they could not be placed), how many frames the camera had delivered by it, and whether the camera
    was posed from the snapshot of the frame (None where the vehicles could not be placed)."""

    frame: int
    small: np.ndarray
    covered: np.ndarray | None
    vehicles: int | None
    camera_frame: int
    camera_from_snapshot: bool | None


class ChannelReadiness:
    """One capture camera's wait: its tiles, then -- where the picture is waited on -- its picture,
    then held until the window opens."""

    def __init__(self, sensor_id: str, camera_id: int, ask: Callable[[], dict],
                 frames: CameraFrames | None, clock: Callable[[], float], ticks_per_frame: int,
                 locate: Callable[[int], FrameVehicles | None] | None = None,
                 fov_deg: float = 90.0, ceiling_frames: int = PICTURE_CEILING_FRAMES,
                 tolerance_levels: float = PICTURE_TOLERANCE_LEVELS,
                 picture_wait: bool = PICTURE_SETTLED_WAIT) -> None:
        """
        Args:
            frames: The camera's frames as it delivers them; read only where `picture_wait`.
            locate: Where a frame's rendered vehicles stood (`SessionFrameVehicles`); None where the
                camera sees no vehicle a session renders, so nothing is excluded.
            fov_deg: The camera's horizontal field of view, which the vehicles are projected with.
            ceiling_frames: How many of its own frames the camera has, from its tiles being in, to
                settle its picture (capture.picture_ceiling_frames).
            tolerance_levels: The gray levels within which a frame counts as settled against its
                frame at least `PICTURE_SPAN_TICKS` earlier (capture.picture_tolerance_levels).
            picture_wait: Whether the picture is waited on once the tiles are in
                (capture.picture_settled_wait). Off, the view is ready on the step its tiles are in
                and no frame is compared.
        """
        self.picture_wait = bool(picture_wait)
        if self.picture_wait and frames is None:
            raise ValueError(f"channel {sensor_id}: the picture is waited on, so its camera's "
                             "frames are needed")
        self.sensor_id = sensor_id
        self.camera_id = camera_id
        self._ask = ask
        self.frames = frames
        self._clock = clock
        self._locate = locate
        self.fov_deg = float(fov_deg)
        self.ticks_per_frame = max(1, int(ticks_per_frame))
        self.ceiling_frames = max(1, int(ceiling_frames))
        self.tolerance_levels = float(tolerance_levels)
        self.state = NOT_STARTED
        self.began: dict | None = None
        self.tiles: dict | None = None
        self.picture: dict | None = None
        self.last_answer: dict | None = None
        self.last_comparison: dict | None = None
        # Every comparison made, in order, across the whole wait.
        self.comparison_history: list[dict] = []
        self.relapses: list[dict] = []
        self.ready_at_window_open: bool | None = None
        self.comparisons = {"made": 0, "judged": 0, TOO_FEW_BLOCKS: 0, VEHICLES_UNKNOWN: 0}
        self.locate_failures = 0
        self.last_locate_failure: str | None = None
        self._asked_ms: list[float] = []
        self._tiles_wait_wall = 0.0
        self._tiles_wait_ticks = 0
        self._settle_from = 0
        self._compared = 0
        self._history: deque[_Frame] = deque(
            maxlen=math.ceil(PICTURE_SPAN_TICKS / self.ticks_per_frame) + 2)

    @property
    def ready(self) -> bool:
        return self.state == READY

    def begin(self, ticks: int, rendered_s: float) -> None:
        """The camera holds the pose the window opens on from here: forget every frame before."""
        if self.picture_wait:
            self.frames.keep()
        now = self._clock()
        self.began = {"sim_time_s": rendered_s, "ticks": ticks}
        self._wait_for_tiles(now, ticks)

    def observe(self, ticks: int) -> list[tuple[int, str]]:
        """After a step: ask about the tiles, compare the frames that arrived where the picture is
        waited on, and say what changed, each with the logging level it is said at.

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
                "written before they are") from None
        self._asked_ms.append((time.perf_counter() - started) * 1000.0)
        self.last_answer = answer
        frame = int(answer["frame"])
        now = self._clock()
        arrived = self.frames.take() if self.picture_wait else []
        said: list[tuple[int, str]] = []
        if not tiles_in(answer):
            if self.state in (SETTLING, READY):
                self.relapses.append({"frame": frame, "was": self.state,
                                      "tiles": describe_tiles(answer)})
                said.append((logging.WARNING,
                             f"channel {self.sensor_id}: its tiles stopped being in at frame "
                             f"{frame} ({describe_tiles(answer)}); "
                             + ("its picture starts again once they are back" if self.picture_wait
                                else "its view is not ready until they are back")))
                self._wait_for_tiles(now, ticks)
            waited = now - self._tiles_wait_wall
            if waited >= TILES_CEILING_S:
                raise ViewNotReadyError(
                    self.sensor_id, "tiles",
                    f"channel {self.sensor_id}: its photoreal tiles were not in within "
                    f"{TILES_CEILING_S:.0f} s of wall clock: at frame {frame}, "
                    f"{ticks - self._tiles_wait_ticks} ticks and {waited:.1f} s into the wait, "
                    f"the tiles were {describe_tiles(answer)}{self._tilesets_text(answer)}")
            return said
        if self.state == WAITING_FOR_TILES and not self.picture_wait:
            self.state = READY
            self.tiles = {"in_at_frame": frame, "ticks": ticks - self._tiles_wait_ticks,
                          "wall_s": round(now - self._tiles_wait_wall, 3)}
            said.append((logging.INFO,
                         f"channel {self.sensor_id}: tiles in at frame {frame}, "
                         f"{self.tiles['ticks']} ticks and {self.tiles['wall_s']:.1f} s into the "
                         "wait; ready, with the picture-settled wait off"))
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

    def _settle(self, arrived: list[tuple[int, np.ndarray, int, int, int]]
                ) -> list[tuple[int, str]]:
        for frame, small, width, height, camera_frame in arrived:
            if frame < self._settle_from or (self._history and frame <= self._history[-1].frame):
                continue
            # The camera's frames since its tiles were in: the count the ceiling is in.
            self._compared += 1
            entry = self._keep(frame, small, width, height, camera_frame)
            older = next((kept for kept in reversed(self._history)
                          if kept.frame <= frame - PICTURE_SPAN_TICKS), None)
            self._history.append(entry)
            if older is not None:
                comparison = self._compare(entry, older)
                self.last_comparison = comparison
                self.comparison_history.append(comparison)
                if comparison["reason"] is None and \
                        comparison["worst_block_levels"] <= self.tolerance_levels:
                    self.state = READY
                    self.picture = {"settled_at_frame": frame, "compared_with_frame": older.frame,
                                    "frames": self._compared,
                                    "ticks_since_tiles": frame - self._settle_from,
                                    "camera_frames": comparison["camera_frames"],
                                    "residual_levels": comparison["worst_block_levels"],
                                    "worst_block_px": comparison["worst_block_px"],
                                    "blocks": comparison["blocks"],
                                    "excluded_blocks": comparison["excluded_blocks"],
                                    "judged_share": comparison["judged_share"],
                                    "vehicles": comparison["vehicles"],
                                    "camera_from_snapshot": comparison["camera_from_snapshot"]}
                    return [(logging.INFO,
                             f"channel {self.sensor_id}: picture settled at frame {frame}, its "
                             f"frame {self._compared} since its tiles were in "
                             f"({frame - self._settle_from} ticks); worst judged 80-pixel block "
                             f"{comparison['worst_block_levels']:.2f} gray levels from frame "
                             f"{older.frame}, {comparison['excluded_blocks']} of "
                             f"{comparison['blocks']} blocks left out for rendered vehicles")]
            if self._compared >= self.ceiling_frames:
                raise ViewNotReadyError(
                    self.sensor_id, "picture",
                    f"channel {self.sensor_id}: its picture did not settle within "
                    f"{self.ceiling_frames} of its frames after its tiles were in at frame "
                    f"{self._settle_from}: {self._comparison_text()}")
        return []

    def _keep(self, frame: int, small: np.ndarray, width: int, height: int,
              camera_frame: int) -> _Frame:
        """A frame with the blocks its rendered vehicles cover."""
        rows, columns = block_grid(width, height)
        if self._locate is None:
            return _Frame(frame, small, np.zeros((rows, columns), dtype=bool), 0, camera_frame,
                          None)
        try:
            located = self._locate(frame)
        except Exception as failure:
            # Said in the record rather than raised: the frame's vehicles cannot be placed, and a
            # comparison with it is not judged, which the refusal at the ceiling names.
            self.locate_failures += 1
            self.last_locate_failure = f"frame {frame}: {failure!r}"
            located = None
        if located is None:
            return _Frame(frame, small, None, None, camera_frame, None)
        rectangles = [footprint for pose, box in located.vehicles
                      if (footprint := vehicle_footprint(located.camera, self.fov_deg, width,
                                                         height, pose, box, located.sun,
                                                         VEHICLE_MARGIN_PX)) is not None]
        return _Frame(frame, small, blocks_covered(rectangles, width, height),
                      len(located.vehicles), camera_frame, bool(located.camera_from_snapshot))

    def _compare(self, newer: _Frame, older: _Frame) -> dict:
        """One comparison: the worst judged block, or why it could not be judged."""
        self.comparisons["made"] += 1
        blocks = block_differences(newer.small, older.small)
        comparison = {"frame": newer.frame, "against_frame": older.frame,
                      "ticks_apart": newer.frame - older.frame,
                      "ticks_since_tiles": newer.frame - self._settle_from,
                      "frames_since_tiles": self._compared,
                      # How many frames the camera had delivered at each: its age in its own frames.
                      "camera_frames": [newer.camera_frame, older.camera_frame],
                      "blocks": int(blocks.size),
                      "vehicles": [newer.vehicles, older.vehicles],
                      "camera_from_snapshot": [newer.camera_from_snapshot,
                                               older.camera_from_snapshot],
                      "excluded_blocks": None, "judged_share": None, "worst_block_levels": None,
                      "worst_block_px": None, "reason": None}
        if newer.covered is None or older.covered is None:
            comparison["reason"] = VEHICLES_UNKNOWN
            self.comparisons[VEHICLES_UNKNOWN] += 1
            return comparison
        excluded = newer.covered | older.covered
        judged = int(blocks.size - excluded.sum())
        comparison["excluded_blocks"] = int(excluded.sum())
        comparison["judged_share"] = round(judged / blocks.size, 4) if blocks.size else 0.0
        if blocks.size == 0 or judged / blocks.size < PICTURE_MIN_JUDGED_SHARE:
            comparison["reason"] = TOO_FEW_BLOCKS
            self.comparisons[TOO_FEW_BLOCKS] += 1
            return comparison
        levels, corner = worst_block(newer.small, older.small, excluded)
        comparison["worst_block_levels"] = round(levels, 3)
        comparison["worst_block_px"] = list(corner)
        self.comparisons["judged"] += 1
        return comparison

    def _comparison_text(self) -> str:
        comparison = self.last_comparison
        if comparison is None:
            return (f"{self._compared} of its frames arrived since, none {PICTURE_SPAN_TICKS} ticks "
                    "after another to compare it with")
        counts = (f" ({self.comparisons['made']} comparisons: {self.comparisons['judged']} "
                  f"judged, {self.comparisons[TOO_FEW_BLOCKS]} with too few blocks left, "
                  f"{self.comparisons[VEHICLES_UNKNOWN]} with vehicles that could not be placed)")
        if comparison["reason"] == VEHICLES_UNKNOWN:
            return (f"the rendered vehicles of frame {comparison['frame']} or frame "
                    f"{comparison['against_frame']} could not be placed -- the session no longer "
                    "held its render set, or the client its snapshot -- so the comparison could not "
                    f"be judged{counts}"
                    + ("" if self.last_locate_failure is None
                       else f"; the last failure placing them: {self.last_locate_failure}"))
        if comparison["reason"] == TOO_FEW_BLOCKS:
            return (f"rendered vehicles covered {comparison['excluded_blocks']} of its "
                    f"{comparison['blocks']} 80-pixel blocks in frame {comparison['frame']} or "
                    f"frame {comparison['against_frame']}, leaving "
                    f"{comparison['judged_share']:.0%} of the view to judge against the "
                    f"{PICTURE_MIN_JUDGED_SHARE:.0%} the witness needs, so the view could not be "
                    f"judged{counts}")
        x, y = comparison["worst_block_px"]
        return (f"frame {comparison['frame']} differs from frame {comparison['against_frame']}, "
                f"{comparison['ticks_apart']} ticks earlier, by "
                f"{comparison['worst_block_levels']:.2f} gray levels in its worst judged 80-pixel "
                f"block (at x {x}, y {y}) against {self.tolerance_levels:g}, with "
                f"{comparison['excluded_blocks']} of {comparison['blocks']} blocks left out for "
                f"rendered vehicles{counts}{self._series_text()}")

    def _series_text(self) -> str:
        """Every judged comparison so far, in order, against the camera's own frame count, so a
        refusal shows whether the picture was converging and how fast."""
        judged = [comparison for comparison in self.comparison_history
                  if comparison["reason"] is None]
        if len(judged) < 2:
            return ""
        levels = ", ".join(f"{comparison['worst_block_levels']:.2f}" for comparison in judged)
        return (f"; the {len(judged)} judged comparisons read {levels} gray levels in order, from "
                f"the camera's frame {judged[0]['camera_frames'][0]} to its frame "
                f"{judged[-1]['camera_frames'][0]}")

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
                f"t={window_begin_s:g}")
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
        """The channel's wait; with the picture's wait off, its tiles alone and no picture figure."""
        asked = None
        if self._asked_ms:
            asked = {"count": len(self._asked_ms),
                     "median_ms": round(statistics.median(self._asked_ms), 3),
                     "worst_ms": round(max(self._asked_ms), 3)}
        view = {"sensor_id": self.sensor_id, "camera_id": self.camera_id, "state": self.state,
                "picture_settled_wait": self.picture_wait,
                "wait_began": self.began, "tiles": self.tiles,
                "tiles_now": describe_tiles(self.last_answer),
                "last_answer": None if self.last_answer is None else {
                    "frame": int(self.last_answer["frame"]),
                    "published": bool(self.last_answer.get("published")),
                    "visible_tilesets": visible_tilesets(self.last_answer)},
                "relapses": list(self.relapses),
                "readiness_asked": asked, "ready_at_window_open": self.ready_at_window_open}
        if self.picture_wait:
            view.update({"picture": self.picture,
                         "last_comparison": self.last_comparison,
                         "comparison_history": list(self.comparison_history),
                         "comparisons": dict(self.comparisons),
                         "locate_failures": self.locate_failures,
                         "last_locate_failure": self.last_locate_failure,
                         "frames_received": self.frames.received,
                         "frames_unreadable": self.frames.unreadable})
        return view


class ViewReadinessGate:
    """Every capture camera's wait, told after each of the session's steps and never ticking."""

    def __init__(self, clock: Callable[[], float] = time.monotonic,
                 logger: logging.Logger | None = None, drain_s: float = FRAME_DRAIN_S,
                 ceiling_frames: int = PICTURE_CEILING_FRAMES,
                 tolerance_levels: float = PICTURE_TOLERANCE_LEVELS,
                 picture_wait: bool = PICTURE_SETTLED_WAIT,
                 tiles_hold_s: float = TILES_HOLD_S) -> None:
        """`picture_wait`, `ceiling_frames` and `tolerance_levels` are the run's
        capture.picture_settled_wait, capture.picture_ceiling_frames and
        capture.picture_tolerance_levels, given to every channel watched; the last two are read only
        where the first is true. `tiles_hold_s` is capture.tiles_hold_s, recorded where the first is
        false."""
        self._clock = clock
        self.logger = logger or logging.getLogger(__name__)
        self.drain_s = drain_s
        self.picture_wait = bool(picture_wait)
        self.tiles_hold_s = float(tiles_hold_s)
        self.ceiling_frames = max(1, int(ceiling_frames))
        self.tolerance_levels = float(tolerance_levels)
        self.channels: list[ChannelReadiness] = []
        self._cameras: list[Any] = []
        self.begun_at: dict | None = None
        self.window_opens_at_s: float | None = None

    @property
    def begun(self) -> bool:
        return self.begun_at is not None

    def watch(self, sensor_id: str, camera: Any, world: Any, ticks_per_frame: int,
              locate: Callable[[int], FrameVehicles | None] | None = None,
              fov_deg: float = 90.0) -> ChannelReadiness:
        """Ask about a capture camera's tiles through `world`; where the picture is waited on, also
        listen to its frames and place each frame's rendered vehicles with `locate`. With the
        picture's wait off the camera is not listened to."""
        frames = CameraFrames() if self.picture_wait else None
        channel = ChannelReadiness(sensor_id, int(camera.id),
                                   lambda: world.get_view_readiness(camera), frames, self._clock,
                                   ticks_per_frame, locate, fov_deg, self.ceiling_frames,
                                   self.tolerance_levels, self.picture_wait)
        if frames is not None:
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
        """The wait as the run result records it: whether the picture was waited on, and with it off
        the statement that it was not run in place of every picture figure."""
        record: dict = {"rule": RULE, "tiles_ceiling_s": TILES_CEILING_S,
                        "picture_settled_wait": self.picture_wait}
        if self.picture_wait:
            record.update({"picture_span_ticks": PICTURE_SPAN_TICKS,
                           # The camera's frames since its tiles were in; the run configuration's
                           # values.
                           "picture_ceiling_frames": self.ceiling_frames,
                           "picture_tolerance_levels": self.tolerance_levels,
                           "picture_block_px": PICTURE_BLOCK_PX,
                           "picture_min_judged_share": PICTURE_MIN_JUDGED_SHARE,
                           "vehicle_margin_px": VEHICLE_MARGIN_PX,
                           "vehicles": VEHICLES})
        else:
            record.update({"picture": PICTURE_WAIT_OFF,
                           # The tiles' own lead before the window: how long a stare following the
                           # traffic holds, and the least prewarm every camera had.
                           "tiles_hold_s": self.tiles_hold_s})
        record.update({"asked": "once per SUMO step, after its last tick",
                       "wait_began": self.begun_at, "window_opens_at_s": self.window_opens_at_s,
                       "per_capture": PER_CAPTURE,
                       "channels": [channel.to_dict() for channel in self.channels]})
        return record
