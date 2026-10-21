"""Stand-ins for the server, a SUMO drive session, its live track and the feed's socket, for carla-drive.

Each stand-in has the members `DriveRun` and `DriveCotFeed` read, named as `carlanet` and
`CarlaNet.CoSim` name them, and writes what is done to it into one ordered timeline, so a test can
assert what happened in which order: `("advance", second)` for each `Advance()`, `("take", count)`
for each `TakeSamplesJson()`, `("socket closed",)` and `("dispose",)`.

The session advances simulated time by one SUMO step of 1.0 s per `Advance()` and completes one live
track sample per step, of the step's newest frame, holding one event per vehicle it is told is drawn.
It answers false on the step a test says the scenario ends on, and raises on the step a test names,
after that step's sample is complete. Nothing here opens a socket.
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


class FakeLiveTrack:
    """A session's live track: the samples waiting to be taken, and the counters it reports."""

    def __init__(self, timeline: list) -> None:
        self.timeline = timeline
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
        self.waiting.append(completed)

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

    `drawn` are the vehicles every sample holds. `ends_on` is the step (counted from 1) whose
    `Advance()` answers false; `raises_on` the step that raises `raising` once its sample is complete.
    """

    def __init__(self, timeline: list, *, first: float = 100.0, window: float | None = None,
                 drawn=("v1", "v2"), ends_on: int | None = None, raises_on: int | None = None,
                 raising: BaseException | None = None, live_track: bool = True) -> None:
        self.timeline = timeline
        self.drawn = list(drawn)
        self.ends_on = ends_on
        self.raises_on = raises_on
        self.raising = raising
        self.LiveTrack = FakeLiveTrack(timeline) if live_track else None
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
        if self.LiveTrack is not None:
            self.LiveTrack.complete(sample(step * TICKS_PER_STEP - 1, second + 0.95, self.drawn,
                                           in_window=second >= self.WindowOpensAtSeconds))
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
    """A world that hands out one session, records what the drive asked of it, and records nothing."""

    def __init__(self, session: FakeSession | None) -> None:
        self.session = session
        self.drive_arguments: dict | None = None
        self.recording: tuple | None = None

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
