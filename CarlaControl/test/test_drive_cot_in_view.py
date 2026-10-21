"""carla-drive's in-view mode sends only the vehicles in a named live camera's picture.

`--cot-emit in-view --cot-camera NAME` filters the live feed by the picture of the live camera that
holds NAME on the server (`carlacontrol.DriveCotFeed`, `carlacontrol.CameraByName`). Held here, with
the server, the session, its live track and the socket stood in for (`DriveFakes`):

* **The camera is found by name.** Without regard to case, among cameras only, a server's own
  `Camera_<n>` included. Each look reads the snapshot's actor ids and asks the server only about the
  ones it has not examined, so a camera spawned under the name after another left is found.
* **The options.** `--cot-emit` is `all` unless given; `in-view` without `--cot-camera`, a
  `--cot-camera` without `in-view`, an empty name, and either without `--cot-udp` are refused with
  exit status 2. The camera-name rule does not apply. `--help` documents both.
* **What is sent.** In in-view mode, only the events the session flags in view, and nothing for a
  sample taken while no camera was followed or while the followed camera was missing from the frame;
  each such sample is counted and the close says how many.
* **Following.** A camera holding the name before the drive starts is followed from the first frame;
  one that goes is let go once a sample says so, and one spawned under the name afterwards is followed
  again. Each change is logged once, and so is a camera missing from the start. The drive's own camera,
  named by `--camera-name`, is followed as it is spawned. A camera that leaves between the look and the
  follow is said and looked past. The launch says which camera's picture the feed keeps.
"""
from __future__ import annotations

import importlib
import logging
import sys
from pathlib import Path
from types import SimpleNamespace

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))
sys.path.insert(0, str(_REPO / "CarlaControl" / "test"))

pytest.importorskip("carlanet", reason="the drive needs carlanet and its assemblies")

from DriveFakes import FakeClient, FakeEmitter, FakeSession, FakeWorld, sample  # noqa: E402
from System import ArgumentException  # noqa: E402  (carlanet, above, loads the .NET runtime)

from carlacontrol.CameraByName import CameraByName  # noqa: E402
from carlacontrol.CameraName import CameraName  # noqa: E402

NAME = "Gate_Orbit_1"


@pytest.fixture(scope="module")
def drive():
    """The command's module. It imports the co-simulation assemblies, so it needs carlanet."""
    try:
        return importlib.import_module("carlacontrol.commands.drive")
    except ImportError as missing:
        pytest.skip(f"the co-simulation assemblies are not loaded here: {missing}")


@pytest.fixture
def feed_module(drive, monkeypatch):
    """`carlacontrol.DriveCotFeed`, with its socket stood in for."""
    module = importlib.import_module("carlacontrol.DriveCotFeed")
    FakeEmitter.made = []
    FakeEmitter.timeline = None
    monkeypatch.setattr(module, "CotUdpEmitter", FakeEmitter)
    return module


def _arguments(drive, *extra: str):
    return drive.parse_args(["--scenario", "s.sumocfg", "--world-package", "w.cwp", *extra])


def _refusal(drive, capsys, *extra: str) -> str:
    with pytest.raises(SystemExit) as stopped:
        _arguments(drive, *extra)
    assert stopped.value.code == 2
    return capsys.readouterr().err


def _messages(caplog) -> list[str]:
    return [record.getMessage() for record in caplog.records]


# ---- finding the camera ------------------------------------------------------------------------

def test_a_camera_is_found_by_its_name_without_regard_to_case():
    world = FakeWorld(None)
    world.add(9, NAME)
    world.add(10, "Overwatch_1")
    assert CameraByName(world, "gate_orbit_1").find().id == 9


def test_an_actor_that_is_not_a_camera_is_never_found_and_its_name_never_read():
    world = FakeWorld(None)
    world.add(300, NAME, type_id="vehicle.lincoln.mkz")
    world.add(301, NAME, type_id="sensor.other.gnss")
    assert CameraByName(world, NAME).find() is None
    assert world.names_read == []


def test_each_actor_is_examined_once_and_only_new_ones_are_asked_about():
    world = FakeWorld(None)
    world.add(300, "sumo", type_id="vehicle.lincoln.mkz")
    world.add(5, "Camera_5")
    cameras = CameraByName(world, NAME)

    assert cameras.find() is None
    assert cameras.find() is None
    world.add(301, "sumo", type_id="vehicle.lincoln.mkz")
    world.add(9, NAME)
    assert cameras.find().id == 9

    assert world.asked == [[5, 300], [9, 301]]
    assert world.names_read == [5, 9]


def test_a_camera_spawned_under_the_name_after_another_left_is_found():
    world = FakeWorld(None)
    first = world.add(9, NAME)
    cameras = CameraByName(world, NAME)
    assert cameras.find() is first
    first.destroy()
    assert cameras.find() is None
    world.add(12, NAME.upper())
    assert cameras.find().id == 12


def test_the_server_s_own_camera_name_can_be_followed(drive):
    world = FakeWorld(None)
    world.add(7, "Camera_7")
    assert CameraByName(world, "Camera_7").find().id == 7
    args = _arguments(drive, "--cot-udp", "127.0.0.1", "--cot-emit", "in-view",
                      "--cot-camera", "Camera_7")
    assert args.cot_camera == "Camera_7"


# ---- the options -------------------------------------------------------------------------------

def test_every_vehicle_is_sent_unless_in_view_is_asked_for(drive):
    args = _arguments(drive, "--cot-udp", "127.0.0.1")
    assert (args.cot_emit, args.cot_camera) == ("all", None)
    args = _arguments(drive, "--cot-udp", "127.0.0.1", "--cot-emit", "in-view", "--cot-camera", NAME)
    assert (args.cot_emit, args.cot_camera) == ("in-view", NAME)
    assert _arguments(drive).cot_emit is None


@pytest.mark.parametrize(("extra", "saying"), [
    (["--cot-emit", "in-view"], "--cot-emit sets the live feed that --cot-udp HOST:PORT turns on"),
    (["--cot-camera", NAME], "--cot-camera sets the live feed that --cot-udp HOST:PORT turns on"),
    (["--cot-udp", "127.0.0.1", "--cot-emit", "in-view"],
     "--cot-emit in-view needs --cot-camera NAME"),
    (["--cot-udp", "127.0.0.1", "--cot-camera", NAME], "--cot-camera needs --cot-emit in-view"),
    (["--cot-udp", "127.0.0.1", "--cot-emit", "all", "--cot-camera", NAME],
     "--cot-camera needs --cot-emit in-view"),
    (["--cot-udp", "127.0.0.1", "--cot-emit", "in-view", "--cot-camera", ""],
     "--cot-camera names no camera"),
    (["--cot-udp", "127.0.0.1", "--cot-emit", "nearest"], "invalid choice: 'nearest'"),
])
def test_an_in_view_option_that_cannot_be_used_is_refused(drive, capsys, extra, saying):
    assert saying in _refusal(drive, capsys, *extra)


def test_the_help_documents_the_in_view_options(drive, capsys):
    with pytest.raises(SystemExit):
        _arguments(drive, "--help")
    said = " ".join(capsys.readouterr().out.split())
    assert "--cot-emit {all,in-view}" in said and "--cot-camera NAME" in said
    assert "`--cot-emit in-view --cot-camera NAME` sends only the vehicles in the picture" in said
    assert "compared without regard to case" in said
    assert "A vehicle hidden behind another is in view." in said


# ---- what is sent ------------------------------------------------------------------------------

def _in_view_feed(drive, feed_module, world: FakeWorld, *extra: str):
    feed = feed_module.DriveCotFeed(_arguments(drive, "--cot-udp", "127.0.0.1:7000", "--cot-emit",
                                               "in-view", "--cot-camera", NAME, *extra))
    session = FakeSession([], world=world, refusal=ArgumentException)
    feed.connect(world)
    feed.start(session)
    return feed, session.LiveTrack, FakeEmitter.made[-1]


def test_only_the_vehicles_in_view_are_sent_and_nothing_without_a_followed_camera(
        drive, feed_module, caplog):
    world = FakeWorld(None)
    world.add(9, NAME)
    feed, track, emitter = _in_view_feed(drive, feed_module, world)
    assert track.FollowedCamera == 9
    seen = sample(19, 100.95, ["v1", "v2", "v3"], camera="followed", camera_id=9, in_view={"v1", "v3"})
    track.complete(seen)
    track.complete(sample(39, 101.95, ["v1", "v2"]))
    track.complete(sample(59, 102.95, ["v1", "v2"], camera="missing", camera_id=7))

    feed.after_step()
    assert emitter.sent == [seen["events"][0]["xml"], seen["events"][2]["xml"]]
    assert (feed.samples, feed.samples_unfollowed) == (3, 2)

    with caplog.at_level(logging.INFO, logger="carlacontrol.DriveCotFeed"):
        feed.close()
    assert any(message.startswith(f"cot camera         2 samples sent nothing because no camera "
                                  f"named {NAME} was followed, 1 of them with the followed camera "
                                  "gone from the frame") for message in _messages(caplog))


def test_the_launch_says_whose_picture_the_feed_keeps(drive, feed_module):
    from carlacontrol.DriveReport import DriveReport
    world = FakeWorld(None)
    feed, _, _ = _in_view_feed(drive, feed_module, world)
    assert DriveReport.describe_cot_feed(feed).startswith(
        f"to 127.0.0.1:7000, ttl 1: only the vehicles in camera {NAME}'s picture, one datagram each")


def test_a_camera_that_leaves_between_the_look_and_the_follow_is_said_and_looked_past(
        drive, feed_module, caplog):
    world = FakeWorld(None)
    world.add(9, NAME)
    with caplog.at_level(logging.INFO, logger="carlacontrol.DriveCotFeed"):
        feed = feed_module.DriveCotFeed(_arguments(drive, "--cot-udp", "127.0.0.1", "--cot-emit",
                                                   "in-view", "--cot-camera", NAME))
        session = FakeSession([], world=world, refusal=ArgumentException)
        feed.connect(world)
        world.actors[9].destroy()
        feed.start(session)
        assert session.LiveTrack.FollowedCamera is None
        world.add(12, NAME)
        feed.after_step()

    assert session.LiveTrack.follows == [12]
    said = _messages(caplog)
    assert f"cot camera {NAME}: actor 9 holds the name, followed once the session starts" in said
    assert any(message.startswith(f"cot camera {NAME}: actor 9 could not be followed (Actor 9 is not a "
                                  "camera the world knows") and message.endswith("; still looking")
               for message in said)
    assert f"cot camera {NAME} followed: actor 12, 1280x720, fov 90" in said


def test_a_snapshot_not_yet_received_says_nothing_of_a_missing_camera(drive, feed_module, caplog):
    world = FakeWorld(None)
    with caplog.at_level(logging.INFO, logger="carlacontrol.DriveCotFeed"):
        feed = feed_module.DriveCotFeed(_arguments(drive, "--cot-udp", "127.0.0.1", "--cot-emit",
                                                   "in-view", "--cot-camera", NAME))
        feed.connect(world)
        world.add(9, NAME)
        feed.start(FakeSession([], world=world, refusal=ArgumentException))
    assert _messages(caplog) == [f"cot camera {NAME} followed: actor 9, 1280x720, fov 90"]


# ---- following through a drive ------------------------------------------------------------------

def _drive_in_view(drive, feed_module, monkeypatch, tmp_path, world: FakeWorld, *extra: str,
                   **session_options) -> tuple[int, FakeSession]:
    """Run the command in in-view mode against the stand-ins."""
    for name in ("s.sumocfg", "w.cwp", "vehicles.catalogue.json"):
        (tmp_path / name).write_text("{}", encoding="utf-8")
    timeline: list = []
    session = FakeSession(timeline, world=world, refusal=ArgumentException, **session_options)
    world.session = session
    FakeClient.world = world
    FakeEmitter.timeline = timeline
    run_module = importlib.import_module("carlacontrol.DriveRun")
    monkeypatch.setattr(run_module, "carla", SimpleNamespace(
        Client=FakeClient, camera_name_problem=CameraName.problem))
    camera_module = importlib.import_module("carlacontrol.DriveCamera").DriveCamera
    monkeypatch.setattr(camera_module, "spawn_camera",
                        staticmethod(lambda spawned_in, args, _centre:
                                     spawned_in.add(41, args.camera_name or "Camera_41",
                                                    optics=(args.width, args.height, args.fov))))
    monkeypatch.setattr(camera_module, "spawn_depth_camera",
                        staticmethod(lambda spawned_in, *_: spawned_in.add(42, "Camera_42",
                                                                           "sensor.camera.depth")))
    monkeypatch.setattr(importlib.import_module("carlacontrol.DriveReport").DriveReport,
                        "report_captures", staticmethod(lambda _recorder: None))
    status = drive.main(["--scenario", str(tmp_path / "s.sumocfg"),
                         "--world-package", str(tmp_path / "w.cwp"),
                         "--catalogue", str(tmp_path / "vehicles.catalogue.json"),
                         "--record-dir", str(tmp_path / "captures"),
                         "--cot-udp", "127.0.0.1:7000", "--cot-emit", "in-view",
                         "--cot-camera", NAME, *extra])
    return status, session


def _camera_lines(caplog) -> list[str]:
    return [message for message in _messages(caplog) if message.startswith("cot camera ")]


def test_a_camera_that_goes_is_let_go_and_one_spawned_under_its_name_is_followed_again(
        drive, feed_module, monkeypatch, tmp_path, caplog):
    world = FakeWorld(None)
    world.add(300, "sumo", type_id="vehicle.lincoln.mkz")
    world.add(9, NAME)
    # Gone as step 3 starts; another camera of the name is spawned as step 6 starts.
    before_step = {3: lambda: world.actors[9].destroy(), 6: lambda: world.add(12, NAME)}
    with caplog.at_level(logging.INFO, logger="carlacontrol.DriveCotFeed"):
        status, session = _drive_in_view(drive, feed_module, monkeypatch, tmp_path, world,
                                         "--no-record", "--steps", "8", in_view=("v2",),
                                         before_step=before_step)

    assert status == 0
    assert _camera_lines(caplog)[:4] == [
        f"cot camera {NAME}: actor 9 holds the name, followed once the session starts",
        f"cot camera {NAME} followed: actor 9, 1280x720, fov 90",
        f"cot camera {NAME} gone: actor 9 is no longer in the world, so in-view mode sends nothing "
        "until a live camera holds the name again",
        f"cot camera {NAME} followed again: actor 12, 1280x720, fov 90"]
    track = session.LiveTrack
    assert (track.follows, track.unfollows) == ([9, 12], 1)
    # Steps 1 and 2 under the first camera; step 3 found it gone, 4 to 6 had none followed, and the
    # second is followed from step 7. One vehicle in view on each.
    [emitter] = FakeEmitter.made
    assert [xml.split('time="')[1].split('"')[0] for xml in emitter.sent] == \
        ["100.950", "101.950", "106.950", "107.950"]
    assert all('uid="CARLA-TRUTH-SUMO-v2"' in xml for xml in emitter.sent)
    # Each actor was asked about once, whenever it first appeared.
    assert sorted(actor for asked in world.asked for actor in asked) == [9, 12, 300]


def test_a_camera_missing_from_the_start_is_said_once_and_followed_once_it_appears(
        drive, feed_module, monkeypatch, tmp_path, caplog):
    world = FakeWorld(None)
    world.add(300, "sumo", type_id="vehicle.lincoln.mkz")
    with caplog.at_level(logging.INFO, logger="carlacontrol.DriveCotFeed"):
        status, session = _drive_in_view(drive, feed_module, monkeypatch, tmp_path, world,
                                         "--no-record", "--steps", "5",
                                         before_step={3: lambda: world.add(9, NAME)})

    assert status == 0
    assert _camera_lines(caplog)[:2] == [
        f"cot camera {NAME}: no live camera holds the name, so in-view mode sends nothing until one "
        "does",
        f"cot camera {NAME} followed: actor 9, 1280x720, fov 90"]
    assert session.LiveTrack.follows == [9]
    assert len(FakeEmitter.made[0].sent) == 2


def test_the_drive_s_own_camera_is_followed_as_it_is_spawned(
        drive, feed_module, monkeypatch, tmp_path, caplog):
    world = FakeWorld(None)
    with caplog.at_level(logging.INFO, logger="carlacontrol.DriveCotFeed"):
        status, session = _drive_in_view(drive, feed_module, monkeypatch, tmp_path, world,
                                         "--camera-name", NAME.lower(), "--width", "640",
                                         "--height", "360", "--steps", "4")

    assert status == 0
    assert _camera_lines(caplog)[:2] == [
        f"cot camera {NAME} is this drive's own camera, followed once it is spawned",
        f"cot camera {NAME} followed: actor 41, 640x360, fov 60"]
    assert session.LiveTrack.follows == [41]
    # The aim step was sampled before the camera existed; every step after it was followed.
    [emitter] = FakeEmitter.made
    assert [xml.split('time="')[1].split('"')[0] for xml in emitter.sent] == ["101.950", "102.950", "103.950"]
