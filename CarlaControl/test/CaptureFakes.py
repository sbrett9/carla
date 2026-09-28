"""Stand-ins for the CARLA client, a world, the co-simulation session and a recorder.

Each stand-in has the members `CaptureSession` reads -- named as `carlanet` and `CarlaNet.CoSim` name
them -- and records every call in one shared, ordered event log, so a test can assert what was done,
with what, and in which order. The session advances simulated time by one SUMO step per `Advance()`
and every recorder writes the captures that step is worth, so the counters a run reports move as a
real run's would. Nothing here opens a socket.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

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


class FakeActor:
    def __init__(self, events: Events, actor_id: int, blueprint: FakeBlueprint,
                 transform: Any) -> None:
        self.events = events
        self.id = actor_id
        self.type_id = blueprint.id
        self.attributes = dict(blueprint.values)
        self.transform = transform
        self.destroyed = False

    def destroy(self) -> bool:
        self.destroyed = True
        self.events.add("destroy", self.type_id, self.id)
        return True

    def set_transform(self, transform: Any) -> None:
        self.transform = transform


class FakeRecorder:
    """A FrameRecorder's counters, moved by the session's steps."""

    def __init__(self, capture_hz: float) -> None:
        self.capture_hz = capture_hz
        self.Saved = 0
        self.Dropped = 0
        self.IlluminationPaired = 0
        self.IlluminationUnpaired = 0
        self.OcclusionMeasured = 0
        self.OcclusionUnmatched = 0
        self.stopped = False
        self.drop_every_step = 0

    def step(self, seconds: float) -> None:
        if self.stopped:
            return
        captures = int(round(seconds * self.capture_hz))
        self.Saved += captures
        self.IlluminationPaired += captures
        self.OcclusionMeasured += captures
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


class _Report:
    def __init__(self, pacing: _Pacing) -> None:
        self.Pacing = pacing
        self.Sumo = _Sumo()
        self.LayerVisibility = _Keyed(road=False, signals=False)
        self.Ticks = 0
        self.SumoSteps = 0
        self.PosesComputed = 0
        self.BatchFailures = 0
        self.WorstSolarResidualDegrees = 0.002
        self.WorstSolarClockResidualSeconds = 0.1

    def __str__(self) -> str:
        return f"fake session report: {self.Ticks} ticks"


class _RenderedIds:
    Count = 7


class FakeSession:
    """A SumoDriveSession: advances one SUMO step per Advance, and every recorder with it."""

    def __init__(self, world: FakeServer, kwargs: dict) -> None:
        self.world = world
        self.kwargs = kwargs
        self.step_s = 1.0
        self.RenderedTimeSeconds = float(kwargs["warm_up_to"])
        self.Report = _Report(_Pacing(float(kwargs["real_time_factor"])))
        self.Clock = "SUMO step 1 s = 20 world ticks of 0.05 s; capture 2 Hz"
        illumination = kwargs["illumination"] or {}
        binds = illumination.get("policy") != "ignore"
        self.Sun = "bound to 2026-03-21 06:55:00-07:00" if binds else None
        self.SunAudit = _Audit() if binds else None
        self.Illumination = _Illumination()
        self.RenderedVehicleIds = _RenderedIds()
        self.disposed = False
        self.advances = 0

    def Advance(self) -> bool:  # noqa: N802 -- the .NET member name
        fault = self.world.fault_at
        if fault is not None and self.RenderedTimeSeconds >= fault[0]:
            raise fault[1]
        self.advances += 1
        self.RenderedTimeSeconds += self.step_s
        self.Report.Ticks += 20
        self.Report.SumoSteps += 1
        self.Report.PosesComputed += 5
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

    def spawn_actor(self, blueprint: FakeBlueprint, transform: Any) -> FakeActor:
        self.server.next_actor += 1
        actor = FakeActor(self.server.events, self.server.next_actor, blueprint, transform)
        self.server.actors.append(actor)
        self.server.events.add("spawn", blueprint.id, actor.id)
        return actor

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
                               None if session is None else session.RenderedTimeSeconds)
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
        self.recorders: list[FakeRecorder] = []
        self.session: FakeSession | None = None
        self.next_actor = 100
        self.start_raises: Exception | None = None
        self.start_returns_none = False
        self.fault_at: tuple[float, Exception] | None = None
        self.achieved_factor: float | None = 3.2
        self.drops_per_step = 0
        self.on_advance = None
        self.result_path: Path | None = None
        self.unreachable = False
        self.clients: list[FakeClient] = []

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
