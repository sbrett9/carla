"""Stand-ins for the server, a SUMO drive session, its live track and the feed's socket, for carla-drive.

Each stand-in has the members `DriveRun` and `DriveCotFeed` read, named as `carlanet` and
`CarlaNet.CoSim` name them, and writes what is done to it into one ordered timeline, so a test can
assert what happened in which order: `("advance", second)` for each `Advance()`, `("take", count)`
for each `TakeSamplesJson()`, `("socket closed",)` and `("dispose",)`.

The session advances simulated time by one SUMO step of 1.0 s per `Advance()` and completes one live
track sample per step, of the step's newest frame, holding one event per vehicle it is told is drawn.
It answers false on the step a test says the scenario ends on, and raises on the step a test names,
after that step's sample is complete. Nothing here opens a socket.

The world holds actors any client spawned, cameras and others, each under the name it holds on the
server; `get_actor_ids` reads them as the latest snapshot holds them, and `get_actors` records the ids
it is asked about. A live track follows a camera of the world, reading its optics at `Follow`, and
refuses any other actor with the refusal it is given, as the session raises `ArgumentException`. While
it follows one, each sample says the camera was `followed`, its events flagged in view where the
session is told they are, or `missing` where the world no longer holds it. A test runs its own changes
to the world at the start of the steps it names, before that step's sample.
"""
from __future__ import annotations

import json
from types import SimpleNamespace

STEP_S = 1.0
TICKS_PER_STEP = 20


def event_xml(sumo_id: str, second: float, affiliation: str = "n") -> str:
    """One vehicle's event, spelled as the session writes it: one line, no XML declaration."""
    return (f'<event version="2.0" uid="CARLA-TRUTH-SUMO-{sumo_id}" type="a-{affiliation}-G-E-V" '
            f'how="m-g" time="{second:.3f}" start="{second:.3f}" stale="{second + 3:.3f}">'
            '<point lat="27.1501200" lon="56.1806500" hae="-12.30" ce="0.0" le="0.0" /><detail>'
            '<track course="90.0" speed="6.00" />'
            f'<contact callsign="car-{sumo_id}" /><_carla source="truth" actor_id="300" '
            f'base_type="car" sumo_id="{sumo_id}" admitted_tick="4" /></detail></event>')


def sample(frame: int, second: float, drawn, *, in_window: bool = True, camera: str = "none",
           camera_id: int | None = None, in_view=()) -> dict:
    """One sample as `TakeSamplesJson` spells it: an event per vehicle drawn, each flagged in view only
    where a camera was followed and posed on the frame."""
    followed = camera == "followed"
    return {"frame": frame, "sim_time_s": second, "time_utc": f"{second:.3f}", "in_window": in_window,
            "camera": camera, "camera_id": camera_id,
            "events": [{"sumo_id": vehicle, "in_view": (vehicle in in_view) if followed else None,
                        "xml": event_xml(vehicle, second)} for vehicle in drawn]}


class FakeEmitter:
    """`CotUdpEmitter` as the feed uses it: every datagram sent, and whether the socket is closed.

    `fail_next` makes that many sends raise, as a socket refused the datagram."""

    made: list[FakeEmitter] = []
    timeline: list | None = None

    def __init__(self, host: str, port: int, ttl: int = 1) -> None:
        self.address = (host, port)
        self.ttl = ttl
        self.sent: list[str] = []
        self.closed = False
        self.fail_next = 0
        FakeEmitter.made.append(self)

    def send(self, cot_xml: str) -> None:
        if self.fail_next:
            self.fail_next -= 1
            raise OSError(10065, "A socket operation was attempted to an unreachable host")
        self.sent.append(cot_xml)

    def close(self) -> None:
        self.closed = True
        if FakeEmitter.timeline is not None:
            FakeEmitter.timeline.append(("socket closed",))


class FakeActor:
    """An actor in the world: a camera with its optics, or anything else, under the name it holds."""

    def __init__(self, world: FakeWorld, actor_id: int, name: str, type_id: str = "sensor.camera.rgb",
                 optics: tuple[int, int, float] = (1280, 720, 90.0)) -> None:
        self.world = world
        self.id = actor_id
        self.name = name
        self.type_id = type_id
        self.optics = optics
        self.destroyed = False

    def destroy(self) -> bool:
        self.destroyed = True
        self.world.actors.pop(self.id, None)
        return True


class FakeLiveTrack:
    """A session's live track: the samples waiting to be taken, the camera it follows, and the counters
    it reports."""

    def __init__(self, timeline: list, world: FakeWorld | None = None,
                 refusal: type[Exception] = ValueError) -> None:
        self.timeline = timeline
        self.world = world
        self.refusal = refusal
        self.FollowedCamera: int | None = None
        self.FollowedOptics = None
        self.follows: list[int] = []
        self.unfollows = 0
        self.waiting: list[dict] = []
        self.TicksPerSample = TICKS_PER_STEP
        self.SampleIntervalSeconds = TICKS_PER_STEP * 0.05
        self.Samples = 0
        self.FramesUnpaired = 0
        self.BodiesMissing = 0
        self.SamplesCameraMissing = 0
        self.SamplesDropped = 0

    def complete(self, completed: dict) -> None:
        self.Samples += 1
        if completed["camera"] == "missing":
            self.SamplesCameraMissing += 1
        self.waiting.append(completed)

    def Follow(self, actor_id: int) -> None:  # noqa: N802 -- the .NET member name
        actor = self.world.actors.get(actor_id) if self.world is not None else None
        if actor is None or not actor.type_id.startswith("sensor.camera."):
            raise self.refusal(f"Actor {actor_id} is not a camera the world knows: it has no "
                               "image_size_x, image_size_y and fov to project a picture with.")
        self.follows.append(actor_id)
        width, height, fov = actor.optics
        self.FollowedCamera = actor_id
        self.FollowedOptics = SimpleNamespace(ImageWidth=width, ImageHeight=height,
                                              HorizontalFovDegrees=fov)

    def Unfollow(self) -> bool:  # noqa: N802 -- the .NET member name
        self.unfollows += 1
        was = self.FollowedCamera is not None
        self.FollowedCamera = self.FollowedOptics = None
        return was

    def TakeSamplesJson(self) -> str:  # noqa: N802 -- the .NET member name
        taken, self.waiting = self.waiting, []
        self.timeline.append(("take", len(taken)))
        return json.dumps(taken)


def _report() -> SimpleNamespace:
    """What `DriveRun` reads off a session's report as it starts, steps and ends."""
    pacing = SimpleNamespace(Paced=False, DeclaredFactor=0.0, CompletedWindows=0, AchievedFactor=None,
                             LastWindowFactor=None)
    return SimpleNamespace(
        DriveLeaseRefused=None, DriveLeaseHolder="the test",
        Sumo=SimpleNamespace(Agrees=True, Installation="stand-in", Binary="sumo",
                             Verdict="agrees with the world's converter"),
        CompileLock=SimpleNamespace(Compiled=False), Teleporting=SimpleNamespace(Enabled=False),
        Pacing=pacing, LayerVisibility=SimpleNamespace(Keys=()), RenderSetLimits=False,
        RenderSetPolicy="every vehicle SUMO has", SumoSeed=42, DrawDistanceMetres=None,
        CameraFootprints=SimpleNamespace(Count=0, Values=()), WorstDivergence=None, PosesComputed=1)


class FakeSession:
    """A SUMO drive session whose first rendered step starts at `first`, one 1.0 s step per `Advance()`.

    `drawn` are the vehicles every sample holds, and `in_view` those a followed camera's picture
    holds. `ends_on` is the step (counted from 1) whose `Advance()` answers false; `raises_on` the step
    that raises `raising` once its sample is complete. `before_step` maps a step to what is done to the
    world as it starts.
    """

    def __init__(self, timeline: list, *, first: float = 100.0, window: float | None = None,
                 drawn=("v1", "v2"), in_view=("v1",), ends_on: int | None = None,
                 raises_on: int | None = None, raising: BaseException | None = None,
                 live_track: bool = True, world: FakeWorld | None = None,
                 refusal: type[Exception] = ValueError, before_step: dict | None = None) -> None:
        self.timeline = timeline
        self.drawn = list(drawn)
        self.in_view = set(in_view)
        self.ends_on = ends_on
        self.raises_on = raises_on
        self.raising = raising
        self.world = world
        self.before_step = before_step or {}
        self.LiveTrack = FakeLiveTrack(timeline, world, refusal) if live_track else None
        self.FirstRenderedSeconds = first
        self.RenderedTimeSeconds = first
        self.WindowOpensAtSeconds = first if window is None else window
        self.Clock = SimpleNamespace(RenderedSecondsPerSumoStep=STEP_S, WorldDeltaSeconds=0.05)
        self.Report = _report()
        self.Sun = None
        self.RunManifest = None
        self.WorldTruthTrack = None
        self.RenderSet = "the session's render set"
        self.Illumination = "the session's illumination"
        self.RenderedVehicleIds = SimpleNamespace(Count=len(self.drawn))
        self.advanced: list[float] = []

    def Advance(self) -> bool:  # noqa: N802 -- the .NET member name
        second = self.RenderedTimeSeconds
        self.advanced.append(second)
        self.timeline.append(("advance", second))
        step = len(self.advanced)
        if step in self.before_step:
            self.before_step[step]()
        track = self.LiveTrack
        if track is not None:
            camera, camera_id = "none", track.FollowedCamera
            if camera_id is not None:
                camera = "followed" if camera_id in self.world.actors else "missing"
            track.complete(sample(step * TICKS_PER_STEP - 1, second + 0.95, self.drawn,
                                  in_window=second >= self.WindowOpensAtSeconds, camera=camera,
                                  camera_id=camera_id, in_view=self.in_view))
        if step == self.raises_on:
            raise self.raising
        self.RenderedTimeSeconds = second + STEP_S
        return step != self.ends_on

    def AddCamera(self, camera: int) -> None:  # noqa: N802 -- the .NET member name
        self.timeline.append(("add camera", camera))

    def Dispose(self) -> None:  # noqa: N802 -- the .NET member name
        self.timeline.append(("dispose",))


class FakeCamera:
    """A camera actor the drive spawned, which it destroys on the way out."""

    def __init__(self, actor_id: int) -> None:
        self.id = actor_id
        self.destroyed = False

    def destroy(self) -> bool:
        self.destroyed = True
        return True


class FakeWorld:
    """A world that hands out one session, holds the actors any client spawned, records what the drive
    asked of it, and records nothing."""

    def __init__(self, session: FakeSession | None) -> None:
        self.session = session
        self.drive_arguments: dict | None = None
        self.recording: tuple | None = None
        self.actors: dict[int, FakeActor] = {}
        self.asked: list[list[int]] = []
        self.names_read: list[int] = []

    def add(self, actor_id: int, name: str, type_id: str = "sensor.camera.rgb",
            optics: tuple[int, int, float] = (1280, 720, 90.0)) -> FakeActor:
        """Spawn an actor, as this client or another would."""
        actor = FakeActor(self, actor_id, name, type_id, optics)
        self.actors[actor_id] = actor
        return actor

    def get_actor_ids(self) -> set:
        return set(self.actors)

    def get_actors(self, actor_ids):
        self.asked.append(list(actor_ids))
        return [self.actors[actor_id] for actor_id in actor_ids if actor_id in self.actors]

    def camera_name(self, camera: FakeActor) -> str:
        self.names_read.append(camera.id)
        return camera.name

    def get_map(self):
        return SimpleNamespace(name="Shahid_Bahonar_Port")

    def get_staging_bounds(self):
        return None

    def start_sumo_drive(self, *_args, **kwargs):
        self.drive_arguments = kwargs
        return self.session

    def start_recording(self, camera, record_dir, record_hz, **kwargs):
        self.recording = (camera, record_dir, record_hz, kwargs)
        return SimpleNamespace(Name=f"Camera_{camera.id}")

    def stop_recording(self) -> None:
        pass


class FakeClient:
    """The client `DriveRun` makes, connected to one world."""

    world: FakeWorld | None = None
    made = 0

    def __init__(self, *_address) -> None:
        FakeClient.made += 1

    def set_timeout(self, _seconds) -> None:
        pass

    def get_world(self) -> FakeWorld:
        return FakeClient.world

    def get_server_version(self) -> str:
        return "stand-in"
