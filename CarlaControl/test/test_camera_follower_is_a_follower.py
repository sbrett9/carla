"""A camera follower watches the world and changes nothing in it but its own camera.

The capture plan gives the world's clock one owner (01 section 4.1, D1.1): while SUMO drives, that
is the drive, and a camera-follower process "never cues". A viewer that ticked the world would
advance SUMO's rendering out from under the drive; one that wrote episode settings could drop a
synchronous drive into free-running; one that touched the sun, a layer or the map would change what
the drive is recording; and one that cleaned up by destroying actors could take the drive's vehicles
with it. None of that would announce itself -- the picture would still look like traffic.

So the world and the client here are stand-ins that record every call made on them, including calls
to methods they do not define, and the follower is run through a scripted session: frames under a
synchronous world, a stall while nothing ticks it, a switch to a free-running world, frames again.
What is asserted is the whole set of calls made, against the handful a viewer needs, and that the
only actor it ever destroys is the one camera it spawned. The window is a stand-in too, so no display
is needed; one test at the end opens a real window on SDL's dummy video driver to check the three ways
out and the colour conversion.
"""
from __future__ import annotations

import logging
import math
import signal
import sys
import time
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

CameraFollower = pytest.importorskip(
    "carlacontrol.CameraFollower",
    reason="the follower needs carlanet, which needs the CarlaNet assemblies on this machine",
).CameraFollower
import pygame  # noqa: E402  (the follower's window needs it, so it is here if the follower is)

from carlacontrol.ChannelDescription import ChannelDescription  # noqa: E402
from carlacontrol.FollowerWindow import FollowerWindow  # noqa: E402
from carlacontrol.FrameStallWatch import FrameStallWatch  # noqa: E402
from carlacontrol.StareAim import StareAim  # noqa: E402

# What a viewer may call. Anything else on the world or the client is a failure.
WORLD_CALLS_ALLOWED = {"get_blueprint_library", "spawn_actor", "get_settings"}
CLIENT_CALLS_ALLOWED = {"get_world"}

# Named individually as well, so a failure says which forbidden thing happened.
WORLD_CALLS_FORBIDDEN = {
    "tick", "apply_settings",
    "set_solar_time", "set_solar_date", "set_solar_epoch", "set_time_advance",
    "set_weather", "set_layer_visible", "set_layer_collision", "set_layer_offset",
    "set_road_rendered", "set_cesium_visible", "set_cesium_collision",
    "configure_cesium_georeference", "load_map_layer", "unload_map_layer", "reload_world",
    "destroy_actor", "get_actors", "start_recording", "start_sumo_drive", "start_scenario",
    "set_staging_bounds",
}
CLIENT_CALLS_FORBIDDEN = {
    "get_trafficmanager", "load_world", "reload_world", "load_delivered_world",
    "generate_opendrive_world", "apply_batch", "apply_batch_sync", "start_recorder",
}

A_STARE = ChannelDescription(sensor_id="OVERWATCH-1", stare_look_at_x_m=120.0,
                             stare_look_at_y_m=-340.0, stare_altitude_m=300.0,
                             stare_standoff_m=400.0)
AN_ORBIT = ChannelDescription(pattern="orbit", orbit_centre_x_m=50.0, orbit_centre_y_m=-80.0,
                              orbit_centre_z_m=10.0, orbit_radius_m=150.0,
                              orbit_altitude_m=300.0, orbit_period_s=240.0)
STALL_AFTER_S = 3.0


class _Recording:
    """A stand-in that records every call made on it, defined method or not.

    An undefined method returns None rather than raising, so a follower that calls something it
    should not is caught by the record instead of by an AttributeError it might swallow.
    """

    def __init__(self, calls: list[tuple[str, str]], owner: str) -> None:
        self._calls = calls
        self._owner = owner

    def _record(self, name: str) -> None:
        self._calls.append((self._owner, name))

    def __getattr__(self, name: str):
        if name.startswith("__"):
            raise AttributeError(name)

        def call(*_args, **_kwargs):
            self._record(name)

        return call


class _Blueprint:
    def __init__(self, blueprint_id: str) -> None:
        self.id = blueprint_id
        self.attributes: dict[str, str] = {}

    def has_attribute(self, name: str) -> bool:
        return name in ("image_size_x", "image_size_y", "fov", "sensor_tick", "role_name")

    def set_attribute(self, name: str, value: str) -> None:
        self.attributes[name] = value


class _Library:
    def __init__(self, world: _World) -> None:
        self.world = world

    def find(self, blueprint_id: str) -> _Blueprint:
        blueprint = _Blueprint(blueprint_id)
        self.world.blueprints.append(blueprint)
        return blueprint


class _Actor(_Recording):
    def __init__(self, calls: list, actor_id: int, type_id: str) -> None:
        super().__init__(calls, f"actor {actor_id}")
        self.id = actor_id
        self.type_id = type_id
        self.callback = None
        self.destroyed = False
        self.moves: list[tuple[object, bool]] = []

    def listen(self, callback) -> None:
        self._record("listen")
        self.callback = callback

    def stop(self) -> None:
        self._record("stop")
        self.callback = None

    def destroy(self) -> bool:
        self._record("destroy")
        self.destroyed = True
        return True

    def set_transform(self, transform) -> None:
        self._record("set_transform")
        self.moves.append((transform, self.destroyed))


class _Settings:
    def __init__(self, synchronous: bool) -> None:
        self.synchronous_mode = synchronous
        self.fixed_delta_seconds = 0.05 if synchronous else None


class _World(_Recording):
    """A world holding two vehicles and a camera that belong to somebody else."""

    def __init__(self, calls: list, synchronous: bool) -> None:
        super().__init__(calls, "world")
        self.synchronous = synchronous
        self.blueprints: list[_Blueprint] = []
        self.spawned: list[_Actor] = []
        self.spawn_transforms: list = []
        self.others = [_Actor(calls, 7, "vehicle.tesla.model3"),
                       _Actor(calls, 8, "vehicle.audi.a2"),
                       _Actor(calls, 9, "sensor.camera.rgb")]
        self.refuse_spawn = False

    def get_blueprint_library(self) -> _Library:
        self._record("get_blueprint_library")
        return _Library(self)

    def spawn_actor(self, blueprint: _Blueprint, transform) -> _Actor:
        self._record("spawn_actor")
        if self.refuse_spawn:
            raise RuntimeError("spawn refused")
        actor = _Actor(self._calls, 100 + len(self.spawned), blueprint.id)
        self.spawned.append(actor)
        self.spawn_transforms.append(transform)
        return actor

    def get_settings(self) -> _Settings:
        self._record("get_settings")
        return _Settings(self.synchronous)

    def get_actors(self) -> list[_Actor]:
        self._record("get_actors")
        return [*self.others, *self.spawned]


class _Client(_Recording):
    def __init__(self, calls: list, world: _World) -> None:
        super().__init__(calls, "client")
        self.world = world

    def get_world(self) -> _World:
        self._record("get_world")
        return self.world


class _Image:
    """A frame as the shim delivers it: a header, a size, and BGRA pixels."""

    def __init__(self, frame: int, timestamp: float, width: int = 4, height: int = 2) -> None:
        self.frame = frame
        self.timestamp = timestamp
        self.width = width
        self.height = height
        self.fov = 90.0
        self.raw_data = bytes(width * height * 4)


class _Clock:
    def __init__(self) -> None:
        self.now = 1000.0

    def __call__(self) -> float:
        return self.now


class _ScriptedWindow:
    """A window that runs one scripted step each time the follower asks whether to quit.

    When the script runs out it asks to quit, which is how a scripted session ends.
    """

    def __init__(self) -> None:
        self.steps: list = []
        self.opened: tuple | None = None
        self.shown: list[tuple[int, list[str]]] = []
        self.messages: list[list[str]] = []
        self.closed = False

    def open(self, width: int, height: int, title: str) -> None:
        self.opened = (width, height, title)

    def quit_requested(self) -> bool:
        if not self.steps:
            return True
        self.steps.pop(0)()
        return False

    def show_frame(self, image: _Image, overlay: list[str]) -> None:
        self.shown.append((image.frame, list(overlay)))

    def show_message(self, lines: list[str]) -> None:
        self.messages.append(list(lines))

    def pace(self) -> None:
        pass

    def close(self) -> None:
        self.closed = True


class _Session:
    """One scripted run of a follower against the stand-ins."""

    def __init__(self, channel: ChannelDescription, synchronous: bool = True) -> None:
        self.calls: list[tuple[str, str]] = []
        self.world = _World(self.calls, synchronous)
        self.client = _Client(self.calls, self.world)
        self.clock = _Clock()
        self.window = _ScriptedWindow()
        self.follower = CameraFollower(self.client, channel, window=self.window, clock=self.clock,
                                       stall_after_s=STALL_AFTER_S)
        self.next_frame = 4800
        self.sim_time = 240.0

    @property
    def camera(self) -> _Actor:
        return self.world.spawned[0]

    def frames(self, count: int, spacing_s: float = 0.05) -> None:
        """Deliver `count` frames, one per loop, as a clock owner ticking the world would."""
        for _ in range(count):
            self.window.steps.append(lambda s=spacing_s: self._deliver(s))

    def quiet(self, seconds: float, loops: int = 10) -> None:
        """Let `seconds` of wall clock pass over `loops` loops with no frame delivered."""
        for _ in range(loops):
            self.window.steps.append(lambda s=seconds / loops: self._advance(s))

    def switch(self, synchronous: bool) -> None:
        """A drive starting (synchronous) or ending (free-running) underneath the follower."""
        self.window.steps.append(lambda: setattr(self.world, "synchronous", synchronous))

    def step(self, action) -> None:
        self.window.steps.append(action)

    def _advance(self, seconds: float) -> None:
        self.clock.now += seconds

    def _deliver(self, seconds: float) -> None:
        self.clock.now += seconds
        self.next_frame += 1
        self.sim_time += 0.05
        self.camera.callback(_Image(self.next_frame, self.sim_time))

    def run(self) -> int:
        return self.follower.run()

    def called(self, owner: str) -> set[str]:
        return {name for who, name in self.calls if who == owner}


def _typical_session(channel: ChannelDescription, synchronous: bool) -> _Session:
    """Frames, a stall, a switch of clock owner, frames again: a drive starting and stopping."""
    session = _Session(channel, synchronous)
    session.frames(5)
    session.quiet(STALL_AFTER_S * 2)
    session.switch(not synchronous)
    session.frames(5)
    return session


@pytest.mark.parametrize("synchronous", [True, False], ids=["synchronous", "free-running"])
@pytest.mark.parametrize("channel", [A_STARE, AN_ORBIT], ids=["stare", "orbit"])
def test_the_follower_calls_nothing_on_the_world_but_what_a_viewer_needs(channel, synchronous):
    session = _typical_session(channel, synchronous)

    assert session.run() == 0

    world_calls = session.called("world")
    client_calls = session.called("client")
    assert world_calls & WORLD_CALLS_FORBIDDEN == set()
    assert client_calls & CLIENT_CALLS_FORBIDDEN == set()
    assert world_calls <= WORLD_CALLS_ALLOWED, world_calls - WORLD_CALLS_ALLOWED
    assert client_calls <= CLIENT_CALLS_ALLOWED, client_calls - CLIENT_CALLS_ALLOWED


@pytest.mark.parametrize("channel", [A_STARE, AN_ORBIT], ids=["stare", "orbit"])
def test_the_only_actor_destroyed_is_the_followers_own_camera(channel):
    session = _typical_session(channel, synchronous=True)

    session.run()

    assert len(session.world.spawned) == 1
    assert session.camera.type_id == "sensor.camera.rgb"
    assert session.camera.destroyed
    for other in session.world.others:
        assert not other.destroyed
        assert session.called(f"actor {other.id}") == set(), f"actor {other.id} was touched"
    camera_calls = [name for who, name in session.calls if who == f"actor {session.camera.id}"]
    assert camera_calls.count("destroy") == 1
    assert camera_calls.index("stop") < camera_calls.index("destroy")
    assert session.window.closed


def test_the_orbit_stops_before_its_camera_is_destroyed():
    session = _Session(AN_ORBIT)
    for _ in range(5):
        session.step(lambda: time.sleep(0.03))

    session.run()

    assert session.follower.orbit is not None
    assert session.follower.orbit._thread is None
    assert session.camera.moves, "the orbit never moved the camera"
    assert not any(after_destroy for _, after_destroy in session.camera.moves)


def test_the_orbit_holds_the_boresight_on_its_centre():
    session = _Session(AN_ORBIT)
    for _ in range(5):
        session.step(lambda: time.sleep(0.03))

    session.run()

    spawn = session.world.spawn_transforms[0]
    assert (spawn.location.x, spawn.location.y, spawn.location.z) == pytest.approx((50.0, -80.0, 310.0))
    for transform, _ in session.camera.moves:
        x, y, z = transform.location.x, transform.location.y, transform.location.z
        assert math.hypot(x - 50.0, y + 80.0) == pytest.approx(150.0)
        assert z == pytest.approx(310.0)
        assert transform.rotation.yaw == pytest.approx(math.degrees(math.atan2(-80.0 - y, 50.0 - x)))
        assert transform.rotation.pitch == pytest.approx(-math.degrees(math.atan2(300.0, 150.0)))


def test_a_stare_is_spawned_at_its_declared_pose_with_the_channels_optics():
    session = _Session(A_STARE)
    session.frames(1)

    session.run()

    aim = StareAim.from_channel(A_STARE)
    spawn = session.world.spawn_transforms[0]
    assert (spawn.location.x, spawn.location.y, spawn.location.z,
            spawn.rotation.pitch, spawn.rotation.yaw) == pytest.approx(
        (aim.x_m, aim.y_m, aim.z_m, aim.pitch_deg, aim.yaw_deg))
    blueprint = session.world.blueprints[0]
    assert blueprint.id == "sensor.camera.rgb"
    # No sensor_tick: the camera renders every tick, so its frames come at the clock owner's rate.
    assert blueprint.attributes == {"image_size_x": "1280", "image_size_y": "720", "fov": "90.0"}
    assert session.camera.moves == []


@pytest.mark.parametrize("synchronous", [True, False], ids=["synchronous", "free-running"])
def test_every_frame_delivered_is_shown_under_either_world(synchronous):
    session = _typical_session(A_STARE, synchronous)

    session.run()

    assert [frame for frame, _ in session.window.shown] == list(range(4801, 4811))


def test_the_overlay_carries_only_what_the_frame_carries():
    session = _Session(A_STARE)
    session.step(lambda: session.camera.callback(_Image(4812, 240.55)))

    session.run()

    assert session.window.shown == [
        (4812, ["OVERWATCH-1   stare", "frame 4812   sim t 240.550 s"]),
    ]


def test_an_unnamed_camera_is_labelled_by_its_actor_id():
    session = _Session(AN_ORBIT)
    session.step(lambda: session.camera.callback(_Image(7, 0.35)))

    session.run()

    assert session.window.shown == [(7, ["unnamed camera, actor 100   orbit", "frame 7   sim t 0.350 s"])]


def test_the_window_says_it_is_waiting_before_the_first_frame():
    session = _Session(A_STARE)
    session.quiet(1.0, loops=3)

    session.run()

    assert session.window.messages == [["OVERWATCH-1   stare", "waiting for the first frame"]]
    assert session.window.shown == []


def _warnings_and_notes(caplog) -> tuple[list[str], list[str]]:
    records = [r for r in caplog.records if r.name == "carlacontrol.CameraFollower"]
    stalls = [r.getMessage() for r in records if r.levelno == logging.WARNING]
    resumes = [r.getMessage() for r in records if "frames arriving again" in r.getMessage()]
    return stalls, resumes


def test_a_stall_is_reported_once_and_its_end_once(caplog):
    session = _Session(A_STARE)
    session.frames(3)
    session.quiet(STALL_AFTER_S * 4, loops=40)
    session.frames(3)
    session.quiet(STALL_AFTER_S * 2, loops=20)

    with caplog.at_level(logging.INFO, logger="carlacontrol.CameraFollower"):
        session.run()

    stalls, resumes = _warnings_and_notes(caplog)
    assert len(stalls) == 2, stalls
    assert all("since the last one" in message for message in stalls)
    assert "never ticks the world" in stalls[0]
    assert len(resumes) == 1


def test_a_camera_that_never_delivers_is_reported_once(caplog):
    session = _Session(A_STARE)
    session.quiet(STALL_AFTER_S * 5, loops=50)

    with caplog.at_level(logging.INFO, logger="carlacontrol.CameraFollower"):
        session.run()

    stalls, resumes = _warnings_and_notes(caplog)
    assert len(stalls) == 1
    assert "since the camera was placed" in stalls[0]
    assert resumes == []


def test_a_stop_signal_ends_the_session_cleanly_and_gives_the_handler_back():
    stop_signals = [signal.SIGINT, signal.SIGTERM]
    originals = {signum: signal.getsignal(signum) for signum in stop_signals}
    during: dict[int, object] = {}
    session = _Session(A_STARE)
    session.frames(2)
    session.step(lambda: during.update({s: signal.getsignal(s) for s in stop_signals}))
    session.step(lambda: signal.raise_signal(signal.SIGINT))
    session.frames(50)

    assert session.run() == 0

    # SIGTERM is taken over as well as SIGINT: left at its default it ends the process on the spot,
    # and the camera with no chance to remove it.
    assert all(handler == session.follower._on_signal for handler in during.values()), during
    assert len(session.window.steps) == 50, "the session ran on after the signal"
    assert session.camera.destroyed
    assert session.window.closed
    assert {signum: signal.getsignal(signum) for signum in stop_signals} == originals


def test_a_keyboard_interrupt_still_removes_the_camera():
    session = _Session(AN_ORBIT)
    session.frames(2)

    def interrupt():
        raise KeyboardInterrupt

    session.step(interrupt)

    assert session.run() == 0
    assert session.camera.destroyed
    assert session.follower.orbit._thread is None
    assert session.window.closed


def test_a_camera_that_cannot_be_placed_leaves_the_world_untouched():
    session = _Session(A_STARE)
    session.world.refuse_spawn = True

    assert session.run() == 1

    assert session.world.spawned == []
    assert session.called("world") <= WORLD_CALLS_ALLOWED
    assert session.window.closed


def test_the_stall_watch_speaks_once_per_stall():
    clock = _Clock()
    watch = FrameStallWatch(3.0, clock)
    heard = []
    for frames, advance in [(1, 0.1), (1, 2.0), (1, 1.5), (1, 5.0), (1, 5.0), (2, 0.1), (2, 3.0)]:
        clock.now += advance
        heard.append(watch.observe(frames))

    assert heard == [None, None, FrameStallWatch.STALLED, None, None, FrameStallWatch.RESUMED,
                     FrameStallWatch.STALLED]


def test_the_window_quits_on_escape_q_or_close_and_draws_true_colour(monkeypatch):
    monkeypatch.setenv("SDL_VIDEODRIVER", "dummy")
    window = FollowerWindow()
    window.open(16, 32, "test")
    try:
        assert window.quit_requested() is False
        for event in (pygame.event.Event(pygame.KEYDOWN, key=pygame.K_ESCAPE),
                      pygame.event.Event(pygame.KEYDOWN, key=pygame.K_q),
                      pygame.event.Event(pygame.QUIT)):
            pygame.event.post(event)
            assert window.quit_requested() is True
        pygame.event.post(pygame.event.Event(pygame.KEYDOWN, key=pygame.K_t))
        assert window.quit_requested() is False

        image = _Image(1, 0.05, width=16, height=32)
        image.raw_data = bytes([10, 20, 30, 255]) * (16 * 32)   # blue 10, green 20, red 30
        window.show_frame(image, [])
        assert tuple(window.display.get_at((8, 24)))[:3] == (30, 20, 10)
    finally:
        window.close()
