"""Stand-ins for the CARLA client, a world, the co-simulation session and a recorder.

Each stand-in has the members `CaptureSession` reads -- named as `carlanet` and `CarlaNet.CoSim` name
them -- and records every call in one shared, ordered event log, so a test can assert what was done,
with what, and in which order. The session advances simulated time by one SUMO step per `Advance()`
and every recorder writes the captures that step is worth, so the counters a run reports move as a
real run's would. Nothing here opens a socket.

The session publishes its admission passes as the real one does (`SumoDriveSessionAdmissionTests`
holds that): two while it starts -- the fast-forward's frame and the step of lookahead -- and then one
per `Advance`, each for the frame one step ahead of the rendered clock, handed to
`on_admission_pass` and left on `Report.LastAdmissionPass`. Under a `capacity` it is handed, a pass
draws at most that many and sheds the rest, as the real session's every-vehicle policy does; under the
cameras it records each camera registered with `AddCamera` and let go with `RemoveCamera`. Where
`on_pose` is given it hands over a
pose record per vehicle per tick, from `FakeServer.traffic_at`. The records carry the member names
of `CarlaNet.CoSim.AdmissionPass` and `CoSimPoseRecord`; the tests that read the real types hold
the names equal.

A camera is named as the server names it: one spawned under a name (`spawn_camera`) carries it as its
`role_name`, and the spawn is refused where a live camera in the world already holds it, in any case
-- `FakeServer.cameras_of_other_clients` stands for the cameras other clients spawned; one spawned
with none, the depth cameras included, is `Camera_<n>` from a counter the server keeps, and
`camera_name` reads either back from the actor. `FakeServer.names_cameras = False` stands for a
server built before it named cameras, which leaves an unnamed camera its blueprint's role name. An
actor spawned attached to another (`spawn_actor(..., attach_to=)`) holds its parent's world pose from
then on, as the server reports an attached actor's pose on its snapshot, and stays where it was,
detached, when its parent is destroyed. A camera given an orbit (`set_orbit`, as the shim's `Sensor`
offers it) is flown by the stand-in server as the plugin's orbit mover flies it: on each tick, before
the frame's snapshot, the angle advances by the tick's delta while the orbit is enabled and not paused
and the camera is placed on the circle, by the plugin's pose rule, as a server move and never a client
one. `FakeServer.flies_orbits = False` stands for a server built before it carried the mover, which
refuses every orbit call as the shim says it.

Every tick of a step advances the server's frame counter and its wall clock, and a camera being
listened to is handed an image on each frame its `sensor_tick` renders, from its spawn frame on --
a uniform grey from `FakeServer.picture_at` unless a test says otherwise. `get_view_readiness`
answers as the shim does, as of the last tick, from `FakeServer.tiles_at`: by default the camera
is published once a tick has passed since it was spawned, and its one visible tileset is in, beside
a hidden tileset that never loads.

Each tick also records the frame's render set and the client's snapshot of it, as the session and
`CarlaClient.GetSnapshotFrame` hold them: the vehicles `FakeServer.vehicles_at` places on the frame,
and every camera where it stands. The session answers `RenderSet.TryGetRenderSet` for the last 256
frames and the client `GetSnapshotFrame` for the last 64, exactly or not at all; `get_actors` gives
each vehicle the box `FakeServer.vehicle_extent` describes.
"""
from __future__ import annotations

import json
import math
import sys
from dataclasses import dataclass, field
from pathlib import Path
from types import SimpleNamespace
from typing import Any

_SRC = Path(__file__).resolve().parents[1] / "src"
if str(_SRC) not in sys.path:
    sys.path.insert(0, str(_SRC))

from carlacontrol.CameraName import CameraName  # noqa: E402  (needs the path above)

RGB_ATTRIBUTES = {"image_size_x", "image_size_y", "fov", "sensor_tick", "post_process_profile",
                  "role_name"}
DEPTH_ATTRIBUTES = {"image_size_x", "image_size_y", "fov", "sensor_tick", "max_range", "role_name"}


@dataclass
class Events:
    """The ordered record every stand-in writes to."""

    log: list[tuple] = field(default_factory=list)

    def add(self, *event: Any) -> None:
        self.log.append(event)

    def names(self) -> list[str]:
        return [event[0] for event in self.log]

    def index(self, name: str) -> int:
        return self.names().index(name)

    def of(self, name: str) -> list[tuple]:
        return [event for event in self.log if event[0] == name]


class FakeBlueprint:
    def __init__(self, blueprint_id: str, attributes: set[str]) -> None:
        self.id = blueprint_id
        self._attributes = attributes
        self.values: dict[str, str] = {}

    def has_attribute(self, name: str) -> bool:
        return name in self._attributes

    def set_attribute(self, name: str, value: str) -> None:
        if name not in self._attributes:
            raise KeyError(f"Blueprint '{self.id}' has no attribute '{name}'")
        self.values[name] = str(value)


class FakeLibrary:
    def __init__(self, vehicles: tuple[str, ...]) -> None:
        self._blueprints = [FakeBlueprint("sensor.camera.rgb", RGB_ATTRIBUTES),
                            FakeBlueprint("sensor.camera.depth", DEPTH_ATTRIBUTES),
                            *(FakeBlueprint(v, set()) for v in vehicles)]

    def find(self, blueprint_id: str) -> FakeBlueprint:
        for blueprint in self._blueprints:
            if blueprint.id == blueprint_id:
                # A fresh copy each time, as the shim builds a library per request.
                return FakeBlueprint(blueprint.id, set(blueprint._attributes))
        raise KeyError(f"Blueprint '{blueprint_id}' not found")

    def __iter__(self):
        return iter(self._blueprints)


class FakeImage:
    """An `Image` as the shim hands a listener one: its frame, size and BGRA bytes."""

    def __init__(self, frame: int, width: int, height: int, raw_data: bytes) -> None:
        self.frame = frame
        self.width = width
        self.height = height
        self.raw_data = raw_data


class FakeActor:
    """An actor in the stand-in world. One spawned attached to another (`parent`) holds its parent's
    world pose, as the server reports an attached actor's pose on its snapshot: the stand-in models
    a child riding its parent at the parent's own pose, which is the one attachment the session
    makes, and refuses any other relative pose rather than compose one."""

    def __init__(self, events: Events, actor_id: int, blueprint: FakeBlueprint,
                 transform: Any, spawn_frame: int = 0, parent: FakeActor | None = None,
                 attachment_type: Any = None) -> None:
        self.events = events
        self.id = actor_id
        self.type_id = blueprint.id
        self.attributes = dict(blueprint.values)
        self.parent = parent
        self.attachment_type = attachment_type
        self.children: list[FakeActor] = []
        if parent is not None:
            location, rotation = transform.location, transform.rotation
            if (location.x, location.y, location.z, rotation.pitch, rotation.yaw,
                    rotation.roll) != (0.0,) * 6:
                raise ValueError("the stand-in attaches a child only at its parent's own pose")
            parent.children.append(self)
        self._transform = transform
        self.spawned_at = self.transform
        self.spawn_frame = spawn_frame
        self.destroyed = False
        self.listener = None
        tick = float(self.attributes.get("sensor_tick", "0") or 0.0)
        self.ticks_per_frame = max(1, round(tick / FakeSession.DELTA_S))
        # The server's orbit mover on this actor (`set_orbit`), or None: the circle, the angle it
        # holds the actor at, and whether it is moving the actor. Whether this server flies orbits at
        # all is the server's knob, set on the actor as it is spawned.
        self.orbit: dict | None = None
        self.flies_orbits = True

    @property
    def transform(self) -> Any:
        """The actor's world pose: its parent's while it is attached to one."""
        if self.parent is not None:
            return self.parent.transform
        return self._transform

    @transform.setter
    def transform(self, transform: Any) -> None:
        self._transform = transform

    def destroy(self) -> bool:
        # A destroyed parent leaves its children in the world, detached where they stood.
        for child in self.children:
            child._transform = child.transform
            child.parent = None
        self.children = []
        self.destroyed = True
        self.listener = None
        self.events.add("destroy", self.type_id, self.id)
        return True

    def listen(self, callback) -> None:
        self.listener = callback
        self.events.add("listen", self.id)

    def stop(self) -> None:
        if self.listener is not None:
            self.events.add("stop_listening", self.id)
        self.listener = None

    def is_listening(self) -> bool:
        return self.listener is not None

    def renders(self, frame: int) -> bool:
        return frame > self.spawn_frame and (frame - self.spawn_frame) % self.ticks_per_frame == 0

    def set_transform(self, transform: Any) -> None:
        self.transform = transform
        self.events.add("move", self.id, transform)

    # -- the server's orbit mover, as the shim's Sensor offers it -------------------------------------

    def _orbit_call(self, call: str) -> None:
        # The shim's refusal, carlanet.OrbitNotOnServerError, is a RuntimeError with these words; the
        # tests here run against whichever carlanet is installed, which may predate the class.
        if not self.flies_orbits:
            raise RuntimeError(
                f"this server cannot fly an orbit: it binds no {call}, so it was built before the "
                f"Carla plugin carried the orbit mover. Rebuild the plugin. Nothing in the client moves "
                f"the camera in the server's place (the server said: rpclib: server could not find "
                f"function '{call}' with argument count 3.)")

    def set_orbit(self, centre, radius: float, altitude: float, period: float, *,
                  clockwise: bool = True, start_angle: float = 0.0, pitch=None,
                  enabled: bool = True) -> None:
        self._orbit_call("set_orbit")
        if radius <= 0.0 or period <= 0.0:
            raise RuntimeError("set_orbit: the radius and the period are positive numbers")
        self.orbit = {"centre": (float(centre.x), float(centre.y), float(centre.z)),
                      "radius": float(radius), "altitude": float(altitude), "period": float(period),
                      "clockwise": bool(clockwise), "angle": float(start_angle) % (2.0 * math.pi),
                      "pitch": None if pitch is None else float(pitch), "enabled": bool(enabled),
                      "paused": False}
        self.events.add("set_orbit", self.id, bool(enabled), float(start_angle))
        if enabled:
            self._place_on_orbit()

    def set_orbit_enabled(self, enabled: bool) -> None:
        self._orbit_call("set_orbit_enabled")
        self.events.add("set_orbit_enabled", self.id, bool(enabled))
        if self.orbit is None:
            if not enabled:
                return
            raise RuntimeError("set_orbit_enabled: the actor has no orbit; give it one with set_orbit "
                               "first")
        self.orbit["enabled"] = bool(enabled)
        if enabled:
            self._place_on_orbit()

    def set_orbit_paused(self, paused: bool) -> None:
        self._orbit_call("set_orbit_paused")
        self.events.add("set_orbit_paused", self.id, bool(paused))
        if self.orbit is None:
            raise RuntimeError("set_orbit_paused: the actor has no orbit; give it one with set_orbit "
                               "first")
        self.orbit["paused"] = bool(paused)

    def get_orbit_state(self):
        self._orbit_call("get_orbit_state")
        self.events.add("get_orbit_state", self.id)
        orbit = self.orbit or {"angle": 0.0, "enabled": False, "paused": False}
        return SimpleNamespace(angle=orbit["angle"], enabled=orbit["enabled"],
                               paused=orbit["paused"])

    def tick_orbit(self, delta_s: float) -> None:
        """One tick of the server's orbit mover, before the frame's snapshot is taken: the angle
        advances by the tick's delta where the orbit is enabled and not paused, and the actor is
        placed on the circle while it is enabled."""
        orbit = self.orbit
        if orbit is None or not orbit["enabled"]:
            return
        if not orbit["paused"]:
            sign = 1.0 if orbit["clockwise"] else -1.0
            orbit["angle"] = (orbit["angle"] + sign * 2.0 * math.pi / orbit["period"] * delta_s) \
                % (2.0 * math.pi)
        self._place_on_orbit()

    def _place_on_orbit(self) -> None:
        # The plugin's pose rule (UOrbitMoverComponent::PoseAt): on the circle at the centre's height
        # plus the altitude, yaw to the centre, pitch the depression to it unless overridden. Set as
        # the server sets it, not as a client move, so it is no "move" event.
        orbit = self.orbit
        cx, cy, cz = orbit["centre"]
        angle = orbit["angle"]
        x = cx + orbit["radius"] * math.cos(angle)
        y = cy + orbit["radius"] * math.sin(angle)
        z = cz + orbit["altitude"]
        dx, dy, dz = cx - x, cy - y, cz - z
        pitch = (math.degrees(math.atan2(dz, math.hypot(dx, dy))) if orbit["pitch"] is None
                 else orbit["pitch"])
        yaw = math.degrees(math.atan2(dy, dx))
        self._transform = FakeTransform(x, y, z, pitch, yaw, 0.0)


class FakeRecorder:
    """A FrameRecorder's counters, moved by the session's steps."""

    def __init__(self, capture_hz: float) -> None:
        self.capture_hz = capture_hz
        self.Saved = 0
        self.Dropped = 0
        self.FrameUnpaired = 0
        self.IlluminationPaired = 0
        self.IlluminationUnpaired = 0
        self.SolarBlockMissing = 0
        self.RenderSetPaired = 0
        self.RenderSetUnpaired = 0
        self.RenderSetBodiesMissing = 0
        self.SupervisionPaired = 0
        self.SupervisionUnpaired = 0
        self.LightsUnknown = 0
        self.PoseSourceUnknown = 0
        self.OcclusionMeasured = 0
        self.OcclusionUnmatched = 0
        self.ChecksSensorPose = True
        self.SensorPoseFromSnapshot = 0
        self.SensorPoseHeaderDisagreed = 0
        self.SensorPoseFromHeader = 0
        self.ChecksDepthPose = False
        self.OcclusionDepthPoseHeaderDisagreed = 0
        self.OcclusionDepthPoseFromHeader = 0
        self.DrawDistanceCaptures = 0
        self.VehiclesBeyondDrawDistance = 0
        self.VehiclesPartlyBeyondDrawDistance = 0
        self.stopped = False
        self.drop_every_step = 0

    def step(self, seconds: float) -> None:
        if self.stopped:
            return
        captures = int(round(seconds * self.capture_hz))
        self.Saved += captures
        self.IlluminationPaired += captures
        self.RenderSetPaired += captures
        self.SupervisionPaired += captures
        self.OcclusionMeasured += captures
        self.SensorPoseFromSnapshot += captures
        self.Dropped += self.drop_every_step


class _Pacing:
    def __init__(self, factor: float) -> None:
        self.DeclaredFactor = factor
        self.Paced = factor > 0
        self.AchievedFactor = None
        self.LastWindowFactor = None
        self.WorstWindowFactor = None
        self.BehindScheduleSeconds = 0.0
        self.CompletedWindows = 0

    def __str__(self) -> str:
        return f"held to {self.DeclaredFactor:g}x real time" if self.Paced else "not paced"


class _Keyed(dict):
    @property
    def Keys(self):  # noqa: N802 -- the .NET dictionary's member name
        return list(self.keys())


class _Sumo:
    Installation = "fake SUMO 1.27.0"
    Verdict = "agrees with the world's converter"
    Agrees = True


class _Audit:
    ToleranceDegrees = 0.01
    ToleranceSeconds = 0.5
    AuditedTicks = 0


class _Declaration:
    def __init__(self, civil: str) -> None:
        self.Policy = "freeze_at_window_start"
        self.DeclaredCivil = civil
        self.SunDeclared = civil
        self.SunCorrectedElevationDeclaredDegrees = 4.5
        self.ResidualDegrees = 0.001


class _Illumination:
    def __init__(self) -> None:
        self.NewestFrame = None
        self._declarations: dict[int, _Declaration] = {}

    def record(self, frame: int, civil: str) -> None:
        self._declarations[frame] = _Declaration(civil)
        self.NewestFrame = frame

    def TryGetDeclaration(self, frame: int, _out: Any):  # noqa: N802 -- the .NET member name
        declaration = self._declarations.get(frame)
        return declaration is not None, declaration


class _Keys:
    def __init__(self, keys) -> None:
        self.Keys = list(keys)


class _FrameRenderSet:
    """A `RenderSet`: the bodies one frame drew, by actor id."""

    def __init__(self, actors) -> None:
        self.ByActor = _Keys(actors)
        self.Count = len(self.ByActor.Keys)


class _RenderSet:
    """A session's render-set source, answering for the frames the server has recorded."""

    def __init__(self, server: FakeServer) -> None:
        self.server = server

    @property
    def NewestFrame(self):  # noqa: N802 -- the .NET member name
        return max(self.server.render_sets) if self.server.render_sets else None

    def TryGetRenderSet(self, frame, _out):  # noqa: N802 -- the .NET member name
        actors = self.server.render_sets.get(int(frame))
        return (False, None) if actors is None else (True, _FrameRenderSet(actors))


class _Xyz:
    def __init__(self, x: float, y: float, z: float) -> None:
        self.x, self.y, self.z = x, y, z


class _Rotation:
    def __init__(self, pitch: float, yaw: float, roll: float) -> None:
        self.pitch, self.yaw, self.roll = pitch, yaw, roll


class FakeTransform:
    """A transform as CarlaNet's and the shim's both read: `location.x`, `rotation.yaw`."""

    def __init__(self, x: float, y: float, z: float, pitch: float = 0.0, yaw: float = 0.0,
                 roll: float = 0.0) -> None:
        self.location = _Xyz(x, y, z)
        self.rotation = _Rotation(pitch, yaw, roll)


class _ActorState:
    def __init__(self, transform) -> None:
        self.Transform = transform


class _Snapshot:
    """One frame's snapshot: every actor's transform, by id."""

    def __init__(self, transforms: dict) -> None:
        self.transforms = transforms

    def TryGetValue(self, actor, _out):  # noqa: N802 -- the .NET member name
        transform = self.transforms.get(int(actor))
        return (False, None) if transform is None else (True, _ActorState(transform))


class FakeCarlaClient:
    """The CarlaNet client behind a world: the snapshots of the frames it still holds."""

    def __init__(self, server: FakeServer) -> None:
        self.server = server

    def GetSnapshotFrame(self, frame):  # noqa: N802 -- the .NET member name
        held = self.server.snapshots
        return _Snapshot(held[int(frame)]) if int(frame) in held else None


class _FakeBoxed:
    """An actor as `world.get_actors` gives it: its id and bounding box."""

    def __init__(self, actor_id: int, extent: tuple[float, float, float]) -> None:
        self.id = actor_id
        self.bounding_box = type("BoundingBox", (), {
            "location": _Xyz(0.0, 0.0, extent[2]), "extent": _Xyz(*extent),
            "rotation": _Rotation(0.0, 0.0, 0.0)})()


class FakeCompileLock:
    """A ScenarioLockCheck's members, for a compiled scenario unless told otherwise."""

    def __init__(self, compiled: bool = True) -> None:
        self.Compiled = compiled
        self.ExpectedLockPath = "scenarios/gardnerville_fixture/gardnerville_fixture.lock.json"
        self.RoutedByText = ("duarouter 1.27.0 against the world converter 'Eclipse SUMO "
                             "netconvert 1.27.0' (SameRelease)" if compiled
                             else "not recorded: an uncompiled scenario")
        self.WorldText = ("Gardnerville_Fixture.cwp, map Gardnerville_Fixture" if compiled
                          else "not recorded: an uncompiled scenario")
        self.DryRunRan = compiled
        self.SkippedDryRunAccepted = False
        self.DryRunText = ("ran with SUMO 1.27.0 over 86400 s: 1 vehicles loaded, 1 inserted, 0 "
                           "discarded, 0 waiting at the end; 0 of 0 planned vehicles inserted; 0 "
                           "collisions" if compiled else "not recorded: an uncompiled scenario")

    def accept_skipped_dry_run(self, reason: str) -> FakeCompileLock:
        """The lock of a compile that skipped its SUMO-only run, which the session accepted."""
        self.DryRunRan = False
        self.SkippedDryRunAccepted = True
        self.DryRunText = f"SKIPPED at the compile ({reason}), accepted explicitly"
        return self

    def __str__(self) -> str:
        return ("gardnerville_fixture, compiled by carlacontrol.ScenarioCompiler; configuration, "
                "route file, network and catalogue agree with it; so does the epoch"
                if self.Compiled else f"none at {self.ExpectedLockPath}: an uncompiled scenario, "
                "nothing compared")


class FakeTeleporting:
    """A TeleportingCheck's members: disabled, as a compiled scenario declares it."""

    def __init__(self, seconds: float = -1.0, accepted: bool = False) -> None:
        self.Declared = f"{seconds:g}"
        self.Seconds = seconds
        self.Enabled = seconds > 0
        self.Accepted = accepted and self.Enabled

    def __str__(self) -> str:
        source = f"time-to-teleport '{self.Declared}'"
        return (f"ENABLED after {self.Seconds:g} s of blocking ({source}), accepted explicitly"
                if self.Enabled else f"disabled ({source})")


class FakeAdmissionPass:
    """An AdmissionPass: the render set's row for one SUMO frame."""

    def __init__(self, world_tick: int, frame_s: float, population: int, newly_admitted: int,
                 released: int, total_admissions: int, *, limited: bool = False,
                 admitted: int | None = None, capacity: int | None = None,
                 rule: str = "Every") -> None:
        self.WorldTick = world_tick
        self.SimulatedTimeSeconds = frame_s
        self.Population = population
        self.NewlyAdmitted = newly_admitted
        self.Released = released
        self.TotalAdmissions = total_admissions
        self.Limited = limited
        self.Eligible = population
        self.Admitted = population if admitted is None else admitted
        self.Shed = self.Eligible - self.Admitted
        self.Held = 0
        self.Capacity = capacity
        self.Rule = rule
        self.Cameras = 0


class _Position:
    def __init__(self, x: float, y: float, z: float) -> None:
        self.X, self.Y, self.Z = x, y, z


class FakePoseRecord:
    """A CoSimPoseRecord's members that a centre is measured from."""

    def __init__(self, tick: int, frame_s: float, actor: int, x: float, y: float,
                 z: float) -> None:
        self.TickIndex = tick
        self.SimulatedTimeSeconds = frame_s
        self.IsCaptureTick = tick % 10 == 0
        self.Actor = actor
        self.Pose = _Position(x, y, z)


class FakeDivergence:
    """A `PoseDivergence`: the vehicle, instant, tick and body one worst figure was measured on."""

    def __init__(self, vehicle_id: str, seconds: float, tick: int, actor: int) -> None:
        self.VehicleId = vehicle_id
        self.SimulatedTimeSeconds = seconds
        self.TickIndex = tick
        self.Actor = actor


class _Report:
    def __init__(self, pacing: _Pacing) -> None:
        self.Pacing = pacing
        self.Sumo = _Sumo()
        self.CompileLock = FakeCompileLock()
        self.Teleporting = FakeTeleporting()
        self.LayerVisibility = _Keyed(road=False, signals=False)
        self.DriveLeaseHolder = "run_capture (process 41 on HOST)"
        self.DriveLeaseRefused = None
        self.Ticks = 0
        self.SumoSteps = 0
        self.PosesComputed = 0
        self.BatchFailures = 0
        # The commanded-against-applied comparison, one per rendered vehicle per tick: nothing compared
        # until the first step, then the figures a world that applies every pose shows -- the
        # single-precision wire's rounding on position and velocity.
        self.DivergenceSamples = 0
        self.VehicleTicksWithNoReadBack = 0
        self.WorstPositionDivergenceMetres = 0.0
        self.MeanPositionDivergenceMetres = 0.0
        self.WorstYawDivergenceDegrees = 0.0
        self.WorstPitchDivergenceDegrees = 0.0
        self.WorstRollDivergenceDegrees = 0.0
        self.WorstVelocityDivergenceMetresPerSecond = 0.0
        self.MeanVelocityDivergenceMetresPerSecond = 0.0
        self.MeanCommandedSpeedMetresPerSecond = 0.0
        self.WorstDivergence = None
        self.WorstVelocityDivergence = None
        self.Admissions = 0
        self.LastAdmissionPass = None
        self.DrawDistanceMetres = None
        self.DrawDistanceRefused = None
        self.PoseSourceRefused = None
        self.RenderSetPolicy = "every vehicle SUMO has"
        self.RenderSetLimits = False
        self.RenderSetCapacity = None
        self.VehiclePassesOutsideThePolicy = 0
        self.CapacityDeclines = 0
        self.Releases = _Keyed()
        self.WorstSolarResidualDegrees = 0.002
        self.WorstSolarClockResidualSeconds = 0.1

    def __str__(self) -> str:
        return f"fake session report: {self.Ticks} ticks"


class _RenderedIds:
    Count = 7


class FakeRunManifest:
    """A RunManifestWriter: the rows a session appends to its manifest, one JSON object per line, opened
    with the header it was handed and closed by its terminal row -- by its caller, or by the session's end."""

    def __init__(self, path: str, header: dict | None) -> None:
        self.Path = path
        self.Closed = False
        self.Rows = 0
        self.reasons: list = []
        Path(path).parent.mkdir(parents=True, exist_ok=True)
        self._write({"row": "manifest_opened", "run": header})

    def _write(self, row: dict) -> None:
        with open(self.Path, "a", encoding="utf-8") as file:
            file.write(json.dumps(row) + "\n")
        self.Rows += 1

    def PlaceSensor(self, sensor_id: str, camera: int) -> None:  # noqa: N802 -- the .NET member name
        if not self.Closed:
            self._write({"row": "sensor_placed", "sensor_id": sensor_id, "camera_actor_id": int(camera)})

    def Close(self, reason) -> None:  # noqa: N802 -- the .NET member name
        self.reasons.append(reason)
        if not self.Closed:
            self._write({"row": "manifest_closed", "caller_reason": reason})
            self.Closed = True


class FakeSession:
    """A SumoDriveSession: advances one SUMO step per Advance, and every recorder with it."""

    TICKS_PER_STEP = 20
    DELTA_S = 0.05

    def __init__(self, world: FakeServer, kwargs: dict) -> None:
        self.world = world
        self.kwargs = kwargs
        self.step_s = 1.0
        self.RenderedTimeSeconds = float(kwargs["warm_up_to"])
        self.FirstRenderedSeconds = self.RenderedTimeSeconds
        opens = kwargs.get("window_opens_at")
        self.WindowOpensAtSeconds = self.RenderedTimeSeconds if opens is None else float(opens)
        self.Report = _Report(_Pacing(float(kwargs["real_time_factor"])))
        self.Report.CompileLock = world.compile_lock
        self.Report.Teleporting = world.teleporting
        # The server's drive lease: held for the run, or refused by a server built before it carried one.
        self.Report.DriveLeaseRefused = world.drive_lease_refused
        self.Clock = "SUMO step 1 s = 20 world ticks of 0.05 s; capture 2 Hz"
        illumination = kwargs["illumination"] or {}
        binds = illumination.get("policy") != "ignore"
        self.Sun = "bound to 2026-03-21 07:00:00-07:00" if binds else None
        self.SunAudit = _Audit() if binds else None
        self.Illumination = _Illumination()
        self.RenderSet = _RenderSet(world)
        self.RenderedVehicleIds = _RenderedIds()
        # The draw distance the session was asked for, which the bodies carry from their spawn.
        self.Report.DrawDistanceMetres = kwargs.get("draw_distance_m")
        self.DrawDistanceMetres = kwargs.get("draw_distance_m")
        # The render set it was asked for: every vehicle by default, or the limit chosen.
        self.render_set = kwargs.get("render_set", "all")
        self.capacity = kwargs.get("capacity")
        self.Report.RenderSetLimits = self.render_set != "all" or self.capacity is not None
        self.Report.RenderSetCapacity = self.capacity
        if self.Report.RenderSetLimits:
            self.Report.RenderSetPolicy = f"{self.render_set}, capacity {self.capacity}"
        self.Cameras: list[int] = []
        self.disposed = False
        self.advances = 0
        self.ticks = 0
        self.on_pose = kwargs.get("on_pose")
        self.on_admission_pass = kwargs.get("on_admission_pass")
        manifest = kwargs.get("run_manifest")
        self.RunManifest = None if manifest is None else FakeRunManifest(manifest,
                                                                         kwargs.get("run_manifest_header"))
        self._population = 0
        # The two passes the session makes while starting: the fast-forward's frame, and the step of
        # lookahead after it.
        self._publish_pass(self.RenderedTimeSeconds)
        self._publish_pass(self.RenderedTimeSeconds + self.step_s)

    def _publish_pass(self, frame_s: float) -> None:
        # Every vehicle SUMO has holds a place: the population's growth is its admissions, and its
        # shrinking its releases.
        population = self.world.population_at(frame_s)
        admitted = max(0, population - self._population)
        released = max(0, self._population - population)
        self._population = population
        report = self.Report
        report.Admissions += admitted
        drawn = population if self.capacity is None else min(population, self.capacity)
        report.CapacityDeclines += population - drawn
        admission = FakeAdmissionPass(self.ticks, frame_s, population, admitted, released,
                                      report.Admissions, limited=report.RenderSetLimits,
                                      admitted=drawn, capacity=self.capacity,
                                      rule={"circle": "Circle", "cameras": "Cameras"}.get(
                                          self.render_set, "Every"))
        report.LastAdmissionPass = admission
        if self.on_admission_pass is not None:
            self.on_admission_pass(admission)

    def _compare_poses(self) -> None:
        """One comparison per rendered vehicle per tick of the step just rendered, as a world that
        applies every pose shows it: the wire's rounding, 0.0001 m and 0.000006 m/s at worst, the
        first vehicle named for both."""
        report = self.Report
        rendered = self.RenderedVehicleIds.Count
        report.DivergenceSamples += rendered * self.TICKS_PER_STEP
        report.WorstPositionDivergenceMetres = 0.0001
        report.MeanPositionDivergenceMetres = 0.00002
        report.WorstYawDivergenceDegrees = 0.0001
        report.WorstPitchDivergenceDegrees = 0.0001
        report.WorstRollDivergenceDegrees = 0.0001
        report.WorstVelocityDivergenceMetresPerSecond = 0.000006
        report.MeanVelocityDivergenceMetresPerSecond = 0.000001
        report.MeanCommandedSpeedMetresPerSecond = 15.2
        if report.WorstDivergence is None:
            report.WorstDivergence = FakeDivergence("flow.0", self.RenderedTimeSeconds, self.ticks, 11)
            report.WorstVelocityDivergence = FakeDivergence("flow.1", self.RenderedTimeSeconds,
                                                            self.ticks + 1, 12)

    def _hand_over_poses(self) -> None:
        """A pose record per vehicle per tick of the step about to be rendered."""
        if self.on_pose is None or self.world.traffic_at is None:
            return
        for tick in range(self.TICKS_PER_STEP):
            frame_s = self.RenderedTimeSeconds + tick * self.DELTA_S
            for actor, (x, y, z) in self.world.traffic_at(frame_s):
                self.on_pose(FakePoseRecord(self.ticks + tick, frame_s, actor, x, y, z))

    def Advance(self) -> bool:  # noqa: N802 -- the .NET member name
        fault = self.world.fault_at
        if fault is not None and self.RenderedTimeSeconds >= fault[0]:
            raise fault[1]
        self._hand_over_poses()
        for _ in range(self.TICKS_PER_STEP):
            self.world.render_one_tick()
        self.advances += 1
        self.ticks += self.TICKS_PER_STEP
        self.RenderedTimeSeconds += self.step_s
        self.Report.Ticks += 20
        self.Report.SumoSteps += 1
        self.Report.PosesComputed += 5
        self._compare_poses()
        self._publish_pass(self.RenderedTimeSeconds + self.step_s)
        pacing = self.Report.Pacing
        pacing.AchievedFactor = self.world.achieved_factor
        pacing.LastWindowFactor = self.world.achieved_factor
        self.Illumination.record(self.advances, f"t={self.RenderedTimeSeconds:g}")
        for recorder in self.world.recorders:
            recorder.step(self.step_s)
        self.world.events.add("advance", self.RenderedTimeSeconds)
        if self.world.on_advance is not None:
            self.world.on_advance(self)
        return self.RenderedTimeSeconds < self.world.scenario_end_s

    def AddCamera(self, camera: int) -> None:  # noqa: N802 -- the .NET member name
        self.Cameras.append(int(camera))
        self.world.events.add("add_camera", int(camera))

    def RemoveCamera(self, camera: int) -> bool:  # noqa: N802 -- the .NET member name
        self.world.events.add("remove_camera", int(camera))
        if int(camera) in self.Cameras:
            self.Cameras.remove(int(camera))
            return True
        return False

    def Dispose(self) -> None:  # noqa: N802 -- the .NET member name
        self.disposed = True
        result_written = self.world.result_path is not None and self.world.result_path.is_file()
        self.world.events.add("dispose", result_written)


class FakeWorld:
    """One `client.get_world()` handle. Handles share the server's state and the event log, and each
    holds its own recorder, as the shim's World does."""

    def __init__(self, server: FakeServer) -> None:
        self.server = server
        self.recorder: FakeRecorder | None = None

    def __getattr__(self, name: str) -> Any:
        return getattr(self.server, name)

    def get_map(self):
        return type("Map", (), {"name": self.server.map_name})()

    def get_blueprint_library(self) -> FakeLibrary:
        self.server.events.add("get_blueprint_library")
        return FakeLibrary(self.server.vehicles)

    def get_solar_state(self):
        return self.server.sun

    def get_staging_bounds(self):
        return self.server.staging_bounds

    def get_actors(self, actor_ids):
        self.server.events.add("get_actors", sorted(int(a) for a in actor_ids))
        return [_FakeBoxed(int(a), self.server.vehicle_extent) for a in actor_ids]

    def get_view_readiness(self, camera):
        server = self.server
        server.events.add("view_readiness", camera.id, server.frame)
        if server.readiness_raises is not None:
            raise server.readiness_raises
        tiles = server.tiles_at(camera, server.frame)
        visible = {"ion_asset_id": 2275207, "visible": True,
                   "load_progress": float(tiles["load_progress"]),
                   "loading": {"worker_queue": 0 if tiles["load_progress"] >= 100.0 else 4,
                               "main_queue": 0, "kicked": 0},
                   "failed_in_view": int(tiles["failed_in_view"]),
                   "failed_loaded": int(tiles["failed_in_view"])}
        hidden = {"ion_asset_id": 1, "visible": False, "load_progress": 0.0,
                  "loading": {"worker_queue": 9, "main_queue": 9, "kicked": 0},
                  "failed_in_view": 0, "failed_loaded": 0}
        return {"frame": server.frame, "published": bool(tiles["published"]),
                "tilesets": [visible, hidden]}

    def tick(self, *_args):
        # The session owns the clock: nothing in a capture run ticks the world itself.
        self.server.events.add("world_tick", self.server.frame)
        return self.server.frame

    def spawn_actor(self, blueprint: FakeBlueprint, transform: Any, attach_to=None,
                    attachment_type=None) -> FakeActor:
        """Spawn an actor; given `attach_to`, attached to that actor at `transform` relative to it,
        as the shim's `spawn_actor` is called. The spawn event carries the parent's id, or None."""
        refused = self.server.spawn_raises.get(blueprint.id)
        if refused is not None:
            raise refused
        # The server settles a camera's role_name before it spawns it (CarlaServer.cpp,
        # SettleCameraName): a camera given no name, or its blueprint's default, is Camera_<n>; a
        # client-given name a live camera holds, or one of the server's form, is refused.
        if blueprint.id.startswith("sensor.camera."):
            given = blueprint.values.get("role_name", "")
            if given in ("", "front"):
                if self.server.names_cameras:
                    self.server.cameras_named += 1
                    blueprint.values["role_name"] = f"Camera_{self.server.cameras_named}"
            elif given[len("Camera_"):].isdigit() and given.upper().startswith("CAMERA_"):
                raise RuntimeError(f"camera name '{given}' has the form the server gives every camera "
                                   "spawned without one, Camera_<n>, which a client cannot claim; "
                                   "choose another, such as Overwatch_1")
            else:
                held = dict(self.server.cameras_of_other_clients)
                held.update({actor.id: actor.attributes["role_name"] for actor in self.server.actors
                             if not actor.destroyed and actor.type_id.startswith("sensor.camera.")
                             and "role_name" in actor.attributes})
                for holder, taken in held.items():
                    if taken.upper() == given.upper():
                        raise RuntimeError(f"camera name '{given}' is already held in this world by "
                                           f"camera {holder} (sensor.camera.rgb): two cameras under "
                                           "one name would write files of one name and report under "
                                           "one callsign; choose another")
        parent = None
        if attach_to is not None:
            parent = next((actor for actor in self.server.actors
                           if actor.id == int(attach_to.id) and not actor.destroyed), None)
            if parent is None:
                raise RuntimeError("unable to attach actor: parent actor not found")
        self.server.next_actor += 1
        actor = FakeActor(self.server.events, self.server.next_actor, blueprint, transform,
                          self.server.frame, parent, attachment_type)
        actor.flies_orbits = self.server.flies_orbits
        self.server.actors.append(actor)
        self.server.events.add("spawn", blueprint.id, actor.id,
                               None if parent is None else parent.id)
        return actor

    def spawn_camera(self, blueprint: FakeBlueprint, transform: Any,
                     name: str | None = None) -> FakeActor:
        # As the shim spawns a camera: the rule's refusal of a chosen name before the round trip, the
        # name sent as the role_name, the server's refusal said as a ValueError, and the name the
        # server settled read back from the actor.
        if name is not None:
            problem = CameraName.problem(name)
            if problem is not None:
                raise ValueError(problem)
            blueprint.set_attribute("role_name", name)
        try:
            return self.spawn_actor(blueprint, transform)
        except Exception as refused:
            if name is not None and "camera name '" in str(refused):
                raise ValueError(str(refused)) from None
            raise

    def camera_name(self, camera) -> str:
        held = camera.attributes.get("role_name")
        if held is not None and CameraName.held_problem(held, camera.id) is None:
            return held
        return f"CARLA-SENSOR-{camera.id}"

    def start_sumo_drive(self, scenario, world_package, catalogue, **kwargs):
        self.server.events.add("start_sumo_drive", scenario, world_package, catalogue, kwargs)
        if self.server.start_raises is not None:
            raise self.server.start_raises
        if self.server.start_returns_none:
            return None
        self.server.session = FakeSession(self.server, kwargs)
        return self.server.session

    def start_recording(self, camera, record_dir, hz=2.0, **kwargs):
        # The shim holds one recorder per World handle and stops the one it holds first.
        self.stop_recording()
        recorder = FakeRecorder(hz)
        recorder.drop_every_step = self.server.drops_per_step
        self.recorder = recorder
        self.server.recorders.append(recorder)
        session = self.server.session
        self.server.events.add("start_recording", camera.id, Path(record_dir), hz, kwargs,
                               None if session is None else session.RenderedTimeSeconds,
                               self.server.frame)
        return recorder

    def stop_recording(self) -> None:
        if self.recorder is not None:
            self.recorder.stopped = True
            self.server.events.add("stop_recording", id(self.recorder))


class FakeServer:
    """What the stand-in client connects to: its world's state, and knobs a test turns."""

    def __init__(self, scenario_end_s: float = 86400.0) -> None:
        self.events = Events()
        self.map_name = "Gardnerville_Fixture"
        self.vehicles = ("vehicle.lincoln.mkz", "vehicle.dodge.charger")
        self.sun: dict | None = {"solar_time": 12.0}
        self.scenario_end_s = scenario_end_s
        self.actors: list[FakeActor] = []
        # Whether the server names the cameras spawned without a name (False: a server built before
        # it did), how many it has named, and the cameras of other clients holding a name in the
        # world, by actor id.
        self.names_cameras = True
        self.cameras_named = 0
        self.cameras_of_other_clients: dict[int, str] = {}
        # Whether the server flies orbits (False: a server built before it carried the orbit mover,
        # which binds no set_orbit and refuses every orbit call as the shim says it).
        self.flies_orbits = True
        self.recorders: list[FakeRecorder] = []
        self.session: FakeSession | None = None
        self.next_actor = 100
        # What the server answers a spawn of a blueprint with, where it refuses one: by blueprint id.
        self.spawn_raises: dict[str, Exception] = {}
        self.start_raises: Exception | None = None
        self.start_returns_none = False
        # Why the server granted the session no drive lease, in its words, or None where it holds one.
        self.drive_lease_refused: str | None = None
        self.fault_at: tuple[float, Exception] | None = None
        self.achieved_factor: float | None = 3.2
        self.drops_per_step = 0
        self.on_advance = None
        # What the session found of the scenario's compile lock and its time-to-teleport.
        self.compile_lock = FakeCompileLock()
        self.teleporting = FakeTeleporting()
        # The vehicles SUMO has at a SUMO frame, for the admission passes.
        self.population_at = lambda _frame_s: 7
        # What get_staging_bounds answers: the world's extent in CARLA's frame, as a world built from
        # an OSM area publishes it, or None for one that publishes none.
        self.staging_bounds: dict | None = {"min_x": -400.0, "min_y": -300.0, "max_x": 600.0,
                                            "max_y": 100.0, "margin": 50.0}
        # (actor, (x, y, z)) per rendered vehicle at a rendered instant, for on_pose; None hands
        # over no poses.
        self.traffic_at = None
        self.result_path: Path | None = None
        self.unreachable = False
        self.clients: list[FakeClient] = []
        # The frame the last tick produced, and the wall clock the ticks have cost.
        self.frame = 1000
        self.wall_s = 0.0
        self.wall_per_tick = 0.005
        # What get_view_readiness says of a camera at a frame, and the grey level (or the BGRA
        # bytes) of the image a camera renders at a frame.
        self.tiles_at = lambda camera, frame: {"published": frame > camera.spawn_frame,
                                               "load_progress": 100.0, "failed_in_view": 0}
        self.picture_at = lambda _camera, _frame: 128
        self.image_size = (320, 160)
        self.readiness_raises: Exception | None = None
        # (actor, (x, y, z, yaw)) per vehicle a frame renders; the frame's render set and snapshot
        # are drawn from it, as are the boxes get_actors gives those actors.
        self.vehicles_at = lambda _frame: []
        self.vehicle_extent = (2.4, 1.0, 0.8)
        self.render_sets: dict[int, list[int]] = {}
        self.snapshots: dict[int, dict] = {}
        self._client = FakeCarlaClient(self)

    def clock(self) -> float:
        """The wall clock the ticks have cost, for a run's `clock`."""
        return self.wall_s

    def render_one_tick(self) -> None:
        """One world tick: the frame counter and wall clock move, and every camera being listened
        to that renders this frame is handed its image."""
        self.frame += 1
        self.wall_s += self.wall_per_tick
        # The server's orbit movers run before the frame's snapshot is taken, as TG_PrePhysics does.
        for actor in self.actors:
            if not actor.destroyed:
                actor.tick_orbit(FakeSession.DELTA_S)
        vehicles = list(self.vehicles_at(self.frame))
        self.render_sets[self.frame] = [actor for actor, _pose in vehicles]
        snapshot = {actor: FakeTransform(x, y, z, 0.0, yaw) for actor, (x, y, z, yaw) in vehicles}
        snapshot.update({actor.id: actor.transform for actor in self.actors if not actor.destroyed})
        self.snapshots[self.frame] = snapshot
        for held, keep in ((self.render_sets, 256), (self.snapshots, 64)):
            while len(held) > keep:
                del held[min(held)]
        width, height = self.image_size
        for actor in list(self.actors):
            if actor.listener is None or actor.destroyed or not actor.renders(self.frame):
                continue
            picture = self.picture_at(actor, self.frame)
            if not isinstance(picture, bytes | bytearray):
                level = int(picture)
                picture = bytes((level, level, level, 255)) * (width * height)
            actor.listener(FakeImage(self.frame, width, height, bytes(picture)))

    def client(self, host: str, port: int) -> FakeClient:
        if self.unreachable:
            raise ConnectionError(f"nothing listening at {host}:{port}")
        client = FakeClient(self, host, port)
        self.clients.append(client)
        return client


class FakeClient:
    def __init__(self, server: FakeServer, host: str, port: int) -> None:
        self.server = server
        self.host = host
        self.port = port
        self.timeout_s: float | None = None
        self.worlds: list[FakeWorld] = []

    def set_timeout(self, seconds: float) -> None:
        self.timeout_s = seconds

    def get_world(self) -> FakeWorld:
        world = FakeWorld(self.server)
        self.worlds.append(world)
        return world

    def get_server_version(self) -> str:
        return "0.10.0-fake"
