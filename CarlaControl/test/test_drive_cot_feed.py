"""carla-drive sends its session's live Cursor-on-Target track, one event per UDP datagram.

`--cot-udp HOST:PORT` turns the feed on (`carlacontrol.DriveCotFeed`). The session builds every
event; the drive hands it the feed's settings and the display convention's affiliations, takes the
samples it completed after each advance, and sends each event's XML. Held here, with the server, the
session, its live track and the socket stood in for (`DriveFakes`):

* **The options.** Each `--cot-*` option takes its default once `--cot-udp` is given, and none is
  set without it. One given without `--cot-udp`, a malformed address or port, a rate or staleness
  that is not a positive finite number, a time-to-live outside 0 to 255, an affiliation that is not
  a CoT letter and a uid prefix that is empty or holds whitespace are each refused with exit status
  2, naming what is wrong, before anything connects. `--help` documents each option.
* **The convention.** The session is handed the rate, staleness, uid prefix and affiliation given,
  and the display convention's map: the one named, or else the scenario's own beside its `.sumocfg`,
  which for the Bahonar pattern of life is its 12 populations. One that cannot be read ends the drive
  with exit status 2 before it connects. A host name is looked up once, and one that does not
  resolve is refused the same way.
* **The sends.** Each event is sent as the session wrote it, one datagram each, in order. A send
  that fails is counted and the rest are sent; the first failure of a run of them is logged once,
  and so is the send that next succeeds.
* **The wiring.** The samples are taken after every advance -- the aim step, each prewarm step and
  each step of the drive, the one SUMO ended on included -- and the feed is closed before the session
  is disposed, its last samples sent and its socket closed, on a drive that ends, one the session
  stops part-way and one interrupted. The launch names the target, the interval and the convention.
"""
from __future__ import annotations

import importlib
import json
import logging
import socket
import sys
from pathlib import Path
from types import SimpleNamespace

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))
sys.path.insert(0, str(_REPO / "CarlaControl" / "test"))

from DriveFakes import FakeCamera, FakeClient, FakeEmitter, FakeSession, FakeWorld, sample  # noqa: E402

BAHONAR = _REPO / "CarlaControl" / "Examples" / "Scenarios" / "Bahonar"
BAHONAR_CONFIG = (BAHONAR / "Shahid_Bahonar_Port_PatternOfLife"
                  / "Shahid_Bahonar_Port_PatternOfLife.sumocfg")
BAHONAR_CONVENTION = BAHONAR_CONFIG.with_name("Shahid_Bahonar_Port_PatternOfLife.display.json")

FEED_OPTIONS = {"--cot-udp-ttl": "2", "--cot-hz": "2", "--cot-stale": "5", "--cot-affiliation": "f",
                "--cot-display-convention": "c.display.json", "--cot-uid-prefix": "LIVE"}


@pytest.fixture(scope="module")
def drive():
    """The command's module. It imports the co-simulation assemblies, so it needs carlanet."""
    pytest.importorskip("carlanet", reason="the drive needs carlanet and its assemblies")
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


def _arguments(drive, *extra: str, scenario: Path | str = "s.sumocfg"):
    return drive.parse_args(["--scenario", str(scenario), "--world-package", "w.cwp", *extra])


def _refusal(drive, capsys, *extra: str) -> str:
    with pytest.raises(SystemExit) as stopped:
        _arguments(drive, *extra)
    assert stopped.value.code == 2
    return capsys.readouterr().err


# ---- the options ---------------------------------------------------------------------------------

def test_without_cot_udp_no_feed_option_is_set(drive):
    args = _arguments(drive)
    assert args.cot_udp is None
    assert all(getattr(args, option[2:].replace("-", "_")) is None for option in FEED_OPTIONS)


def test_with_cot_udp_every_feed_option_takes_its_default(drive):
    args = _arguments(drive, "--cot-udp", "239.2.3.1:6969")
    assert (args.cot_udp, args.cot_udp_ttl, args.cot_hz, args.cot_stale, args.cot_affiliation,
            args.cot_display_convention, args.cot_uid_prefix) == \
        ("239.2.3.1:6969", 1, 1.0, 3.0, "n", None, "CARLA-TRUTH")


def test_a_feed_option_given_is_kept(drive):
    args = _arguments(drive, "--cot-udp", "127.0.0.1:7000",
                      *[part for pair in FEED_OPTIONS.items() for part in pair])
    assert (args.cot_udp_ttl, args.cot_hz, args.cot_stale, args.cot_affiliation,
            args.cot_display_convention, args.cot_uid_prefix) == \
        (2, 2.0, 5.0, "f", "c.display.json", "LIVE")


@pytest.mark.parametrize("option", sorted(FEED_OPTIONS))
def test_a_feed_option_without_cot_udp_is_refused(drive, capsys, option):
    said = _refusal(drive, capsys, option, FEED_OPTIONS[option])
    assert f"{option} sets the live feed that --cot-udp HOST:PORT turns on" in said


def test_every_feed_option_given_without_cot_udp_is_named(drive, capsys):
    said = _refusal(drive, capsys, "--cot-hz", "2", "--cot-stale", "5")
    assert "--cot-hz, --cot-stale set the live feed" in said
    assert "leave them out" in said


@pytest.mark.parametrize(("extra", "saying"), [
    (["--cot-udp", ""], "'' is not HOST:PORT: it names no host"),
    (["--cot-udp", ":6969"], "':6969' is not HOST:PORT: it names no host"),
    (["--cot-udp", "a host:6969"], "'a host:6969' is not HOST:PORT: it names no host"),
    (["--cot-udp", "127.0.0.1:"], "the port '' is not a whole number"),
    (["--cot-udp", "127.0.0.1:69x"], "the port '69x' is not a whole number"),
    (["--cot-udp", "127.0.0.1:-1"], "the port '-1' is not a whole number"),
    (["--cot-udp", "::1"], "it names no host"),
    (["--cot-udp", "127.0.0.1:0"], "names port 0, outside 1 to 65535"),
    (["--cot-udp", "127.0.0.1:65536"], "names port 65536, outside 1 to 65535"),
    (["--cot-udp", "127.0.0.1", "--cot-udp-ttl", "-1"], "--cot-udp-ttl -1 is outside 0 to 255"),
    (["--cot-udp", "127.0.0.1", "--cot-udp-ttl", "256"], "--cot-udp-ttl 256 is outside 0 to 255"),
    (["--cot-udp", "127.0.0.1", "--cot-hz", "0"],
     "--cot-hz 0 must be a positive number of samples per simulated second"),
    (["--cot-udp", "127.0.0.1", "--cot-hz", "-1"], "--cot-hz -1 must be a positive number"),
    (["--cot-udp", "127.0.0.1", "--cot-hz", "nan"], "--cot-hz nan must be a positive number"),
    (["--cot-udp", "127.0.0.1", "--cot-hz", "inf"], "--cot-hz inf must be a positive number"),
    (["--cot-udp", "127.0.0.1", "--cot-stale", "0"],
     "--cot-stale 0 must be a positive number of seconds"),
    (["--cot-udp", "127.0.0.1", "--cot-affiliation", "q"],
     "--cot-affiliation 'q' must be one of a f h j k n o p s u x"),
    (["--cot-udp", "127.0.0.1", "--cot-affiliation", "nf"], "--cot-affiliation 'nf' must be one of"),
    (["--cot-udp", "127.0.0.1", "--cot-uid-prefix", ""],
     "--cot-uid-prefix '' must be one or more characters, none of them whitespace"),
    (["--cot-udp", "127.0.0.1", "--cot-uid-prefix", "CARLA TRUTH"],
     "--cot-uid-prefix 'CARLA TRUTH' must be one or more characters"),
])
def test_an_unusable_feed_option_is_refused_naming_what_is_wrong(drive, capsys, extra, saying):
    assert saying in _refusal(drive, capsys, *extra)


def test_every_unusable_feed_option_is_named_at_once(drive, capsys):
    said = _refusal(drive, capsys, "--cot-udp", "127.0.0.1:0", "--cot-hz", "0", "--cot-stale", "-2")
    assert "port 0" in said and "--cot-hz 0" in said and "--cot-stale -2" in said


def test_the_help_documents_every_feed_option(drive, capsys):
    with pytest.raises(SystemExit):
        _arguments(drive, "--help")
    said = capsys.readouterr().out
    for option in ("--cot-udp HOST:PORT", *(f"{option} " for option in FEED_OPTIONS)):
        assert option in said, option
    # The uids the feed carries, and how they differ from carla-cot-telemetry's.
    assert "CARLA-TRUTH-SUMO-<SUMO id>" in said and "SUMO-TRUTH-<SUMO id>" in said
    assert "A port left out is 6969" in said


# ---- the convention and the session's settings -------------------------------------------------

def test_the_session_is_handed_the_bahonar_convention_found_beside_the_scenario(drive, feed_module):
    args = _arguments(drive, "--cot-udp", "127.0.0.1", "--cot-affiliation", "u",
                      scenario=BAHONAR_CONFIG)
    feed = feed_module.DriveCotFeed(args)
    handed = feed.live_track()

    convention = json.loads(BAHONAR_CONVENTION.read_text(encoding="utf-8"))["affiliation_by_type"]
    assert len(convention) == 12
    assert handed == {"rate_hz": 1.0, "stale_s": 3.0, "uid_prefix": "CARLA-TRUTH",
                      "affiliation": "u", "affiliation_by_type": convention}
    assert (feed.convention_path, feed.convention_found) == (BAHONAR_CONVENTION, True)
    assert feed.describe_convention() == (
        f"display convention {BAHONAR_CONVENTION}, found beside the scenario: 12 populations; any "
        "other takes u")


def test_a_named_convention_is_drawn_with_and_none_leaves_every_vehicle_the_default(
        drive, feed_module, tmp_path):
    named = tmp_path / "exercise.display.json"
    named.write_text(json.dumps({"convention_version": 1,
                                 "affiliation_by_type": {"civ_car": "h"}}), encoding="utf-8")
    feed = feed_module.DriveCotFeed(_arguments(drive, "--cot-udp", "127.0.0.1",
                                               "--cot-display-convention", str(named),
                                               scenario=BAHONAR_CONFIG))
    assert feed.live_track()["affiliation_by_type"] == {"civ_car": "h"}
    assert feed.describe_convention().startswith(f"display convention {named}: 1 populations")

    alone = feed_module.DriveCotFeed(_arguments(drive, "--cot-udp", "127.0.0.1",
                                                "--cot-hz", "2", "--cot-stale", "4",
                                                "--cot-uid-prefix", "LIVE",
                                                scenario=tmp_path / "Port.sumocfg"))
    assert alone.live_track() == {"rate_hz": 2.0, "stale_s": 4.0, "uid_prefix": "LIVE",
                                  "affiliation": "n", "affiliation_by_type": {}}
    assert alone.describe_convention() == ("no display convention: none given and no "
                                           "Port.display.json beside the scenario; every vehicle "
                                           "takes n")


@pytest.mark.parametrize("which", ["named and missing", "found and naming vehicles"])
def test_a_convention_that_cannot_be_read_ends_the_drive_before_it_connects(
        drive, monkeypatch, tmp_path, caplog, which):
    config = tmp_path / "Port.sumocfg"
    for path in (config, tmp_path / "w.cwp", tmp_path / "vehicles.catalogue.json"):
        path.write_text("{}", encoding="utf-8")
    extra = ["--cot-display-convention", str(tmp_path / "absent.display.json")]
    if which == "found and naming vehicles":
        extra = []
        (tmp_path / "Port.display.json").write_text(json.dumps(
            {"convention_version": 1, "affiliation_by_type": {"civ_car": "n"},
             "marked_ids": ["probe_d2"]}), encoding="utf-8")
    FakeClient.made = 0
    monkeypatch.setattr(importlib.import_module("carlacontrol.DriveRun"), "carla",
                        SimpleNamespace(Client=FakeClient))

    with caplog.at_level(logging.ERROR, logger="carlacontrol.DriveRun"):
        assert drive.main(["--scenario", str(config), "--world-package", str(tmp_path / "w.cwp"),
                           "--catalogue", str(tmp_path / "vehicles.catalogue.json"),
                           "--cot-udp", "127.0.0.1", *extra]) == 2
    assert FakeClient.made == 0
    assert "the display convention" in caplog.text and "cannot be read" in caplog.text


def test_a_host_name_is_looked_up_once_and_one_that_does_not_resolve_is_refused(
        drive, feed_module, monkeypatch):
    looked_up: list[str] = []

    def lookup(host: str) -> str:
        looked_up.append(host)
        if host == "nowhere.invalid":
            raise socket.gaierror(11001, "getaddrinfo failed")
        return "10.1.2.3"

    monkeypatch.setattr(feed_module.socket, "gethostbyname", lookup)
    feed = feed_module.DriveCotFeed(_arguments(drive, "--cot-udp", "tak.example:7000"))
    feed.start(FakeSession([]))
    assert looked_up == ["tak.example"]
    assert FakeEmitter.made[-1].address == ("10.1.2.3", 7000)
    assert feed.target() == "tak.example (10.1.2.3):7000"

    with pytest.raises(ValueError, match="the host nowhere.invalid does not resolve"):
        feed_module.DriveCotFeed(_arguments(drive, "--cot-udp", "nowhere.invalid"))


# ---- the sends ---------------------------------------------------------------------------------

def _started(drive, feed_module, *extra: str):
    feed = feed_module.DriveCotFeed(_arguments(drive, "--cot-udp", "127.0.0.1:7000", *extra))
    session = FakeSession([])
    feed.start(session)
    return feed, session.LiveTrack, FakeEmitter.made[-1]


def test_each_event_is_sent_as_the_session_wrote_it_one_datagram_each(drive, feed_module):
    feed, track, emitter = _started(drive, feed_module)
    first = sample(19, 100.95, ["v1", "v2"])
    second = sample(39, 101.95, ["v2", "v3"], in_window=False)
    track.complete(first)
    track.complete(second)

    feed.after_step()

    assert emitter.address == ("127.0.0.1", 7000) and emitter.ttl == 1
    assert emitter.sent == [event["xml"] for event in (*first["events"], *second["events"])]
    assert (feed.samples, feed.events_sent, feed.sends_failed) == (2, 4, 0)
    feed.after_step()
    assert len(emitter.sent) == 4


def test_every_vehicle_is_sent_whatever_a_sample_says_of_a_camera(drive, feed_module):
    feed, track, emitter = _started(drive, feed_module)
    track.complete(sample(19, 100.95, ["v1", "v2", "v3"], camera="followed", camera_id=7,
                          in_view={"v2"}))
    track.complete(sample(39, 101.95, ["v1"], camera="missing", camera_id=7))
    feed.after_step()
    assert len(emitter.sent) == 4


def test_a_failed_send_is_counted_and_said_once_and_so_is_the_recovery(drive, feed_module, caplog):
    feed, track, emitter = _started(drive, feed_module)
    emitter.fail_next = 3
    five = sample(19, 100.95, ["v1", "v2", "v3", "v4", "v5"])
    track.complete(five)

    with caplog.at_level(logging.INFO, logger="carlacontrol.DriveCotFeed"):
        feed.after_step()

    failures = [record for record in caplog.records if "failed (" in record.getMessage()]
    recoveries = [record for record in caplog.records if "again" in record.getMessage()]
    assert len(failures) == 1 and failures[0].levelno == logging.WARNING
    assert "a send to 127.0.0.1:7000 failed" in failures[0].getMessage()
    assert len(recoveries) == 1
    assert "sending to 127.0.0.1:7000 again, 3 sends having failed so far" in \
        recoveries[0].getMessage()
    assert (feed.sends_failed, feed.events_sent) == (3, 2)
    assert emitter.sent == [event["xml"] for event in five["events"][3:]]

    caplog.clear()
    emitter.fail_next = 1
    track.complete(sample(39, 101.95, ["v1", "v2"]))
    with caplog.at_level(logging.INFO, logger="carlacontrol.DriveCotFeed"):
        feed.after_step()
    assert sum("failed (" in record.getMessage() for record in caplog.records) == 1
    assert (feed.sends_failed, feed.events_sent) == (4, 3)


def test_closing_sends_what_is_waiting_says_what_was_sent_and_closes_the_socket(
        drive, feed_module, caplog):
    feed, track, emitter = _started(drive, feed_module)
    track.complete(sample(19, 100.95, ["v1", "v2"]))
    feed.after_step()
    track.complete(sample(39, 101.95, ["v1"]))
    track.FramesUnpaired = 1

    with caplog.at_level(logging.INFO, logger="carlacontrol.DriveCotFeed"):
        feed.close()

    assert len(emitter.sent) == 3 and emitter.closed
    [summary] = [record for record in caplog.records if record.getMessage().startswith("cot feed  ")]
    assert summary.levelno == logging.WARNING
    assert "2 samples, 3 events sent to 127.0.0.1:7000, 0 sends failed; 1 sampled frames had no " \
        "truth" in summary.getMessage()
    feed.close()
    assert len(FakeEmitter.made) == 1


def test_the_launch_names_the_target_the_interval_and_the_convention(drive, feed_module):
    from carlacontrol.DriveReport import DriveReport
    assert DriveReport.describe_cot_feed(None) == "none sent; give --cot-udp to send one"
    feed = feed_module.DriveCotFeed(_arguments(drive, "--cot-udp", "127.0.0.1",
                                               scenario=BAHONAR_CONFIG))
    feed.start(FakeSession([]))
    said = DriveReport.describe_cot_feed(feed)
    assert said.startswith("to 127.0.0.1:6969, ttl 1: every vehicle with a body, one datagram each, "
                           "on the newest frame of every 20 world ticks (1.00 s), the prewarm's "
                           "included; stale 3 s after its stamp; uid CARLA-TRUTH-SUMO-<SUMO id>; ")
    assert said.endswith(f"display convention {BAHONAR_CONVENTION}, found beside the scenario: 12 "
                         "populations; any other takes n")


# ---- the wiring -------------------------------------------------------------------------------

def _drive_with(drive, feed_module, monkeypatch, tmp_path, session: FakeSession, *extra: str,
                feed: bool = True) -> tuple[int, FakeWorld]:
    """Run the command against the stand-ins, its fixed camera spawned and recorded without a server."""
    for name in ("s.sumocfg", "w.cwp", "vehicles.catalogue.json"):
        (tmp_path / name).write_text("{}", encoding="utf-8")
    world = FakeWorld(session)
    FakeClient.world = world
    FakeEmitter.timeline = session.timeline if session is not None else None
    run_module = importlib.import_module("carlacontrol.DriveRun")
    monkeypatch.setattr(run_module, "carla", SimpleNamespace(Client=FakeClient))
    camera_module = importlib.import_module("carlacontrol.DriveCamera").DriveCamera
    monkeypatch.setattr(camera_module, "spawn_camera", staticmethod(lambda *_: FakeCamera(41)))
    monkeypatch.setattr(camera_module, "spawn_depth_camera", staticmethod(lambda *_: FakeCamera(42)))
    monkeypatch.setattr(importlib.import_module("carlacontrol.DriveReport").DriveReport,
                        "report_captures", staticmethod(lambda _recorder: None))
    argv = ["--scenario", str(tmp_path / "s.sumocfg"), "--world-package", str(tmp_path / "w.cwp"),
            "--catalogue", str(tmp_path / "vehicles.catalogue.json"),
            "--record-dir", str(tmp_path / "captures"), *extra]
    if feed:
        argv += ["--cot-udp", "127.0.0.1:7000"]
    return drive.main(argv), world


def _taken_after_every_advance(timeline: list) -> bool:
    kinds = [entry[0] for entry in timeline]
    return all(kinds[index + 1] == "take" for index, kind in enumerate(kinds) if kind == "advance")


def _closed_before_disposal(timeline: list) -> bool:
    kinds = [entry[0] for entry in timeline]
    return kinds[-3:] == ["take", "socket closed", "dispose"]


def test_a_drive_with_no_feed_hands_the_session_none(drive, feed_module, monkeypatch, tmp_path):
    timeline: list = []
    status, world = _drive_with(drive, feed_module, monkeypatch, tmp_path,
                                FakeSession(timeline, ends_on=2, live_track=False), "--no-record",
                                feed=False)
    assert status == 0
    assert world.drive_arguments["live_track"] is None
    assert [entry[0] for entry in timeline] == ["advance", "advance", "dispose"]


def test_the_samples_are_taken_after_every_step_the_one_sumo_ended_on_included(
        drive, feed_module, monkeypatch, tmp_path):
    timeline: list = []
    session = FakeSession(timeline, ends_on=3)
    status, world = _drive_with(drive, feed_module, monkeypatch, tmp_path, session, "--no-record")

    assert status == 0
    assert world.drive_arguments["live_track"]["uid_prefix"] == "CARLA-TRUTH"
    assert session.advanced == [100.0, 101.0, 102.0]
    assert _taken_after_every_advance(timeline)
    assert _closed_before_disposal(timeline)
    [emitter] = FakeEmitter.made
    assert len(emitter.sent) == 6 and emitter.closed


def test_the_aim_step_and_the_prewarm_are_sent_as_well(drive, feed_module, monkeypatch, tmp_path):
    timeline: list = []
    session = FakeSession(timeline, window=103.0)
    status, world = _drive_with(drive, feed_module, monkeypatch, tmp_path, session,
                                "--window-opens-at", "103", "--steps", "6")

    assert status == 0
    # One aim step, two prewarm steps to the window's opening, and three more to --steps.
    assert session.advanced == [100.0, 101.0, 102.0, 103.0, 104.0, 105.0]
    assert world.recording is not None
    assert _taken_after_every_advance(timeline)
    assert _closed_before_disposal(timeline)
    [emitter] = FakeEmitter.made
    assert len(emitter.sent) == 12 and emitter.closed


def test_a_prewarm_the_scenario_ends_in_is_sent_before_the_drive_stops(
        drive, feed_module, monkeypatch, tmp_path):
    timeline: list = []
    session = FakeSession(timeline, window=110.0, ends_on=2)
    status, _ = _drive_with(drive, feed_module, monkeypatch, tmp_path, session,
                            "--window-opens-at", "110")
    assert status == 1
    assert session.advanced == [100.0, 101.0]
    assert _taken_after_every_advance(timeline)
    assert _closed_before_disposal(timeline)
    assert len(FakeEmitter.made[0].sent) == 4


def test_a_drive_the_session_stops_part_way_sends_its_last_sample_and_closes_first(
        drive, feed_module, monkeypatch, tmp_path):
    from CarlaNet.CoSim import CoSimSessionRefusedException
    timeline: list = []
    session = FakeSession(timeline, raises_on=3,
                          raising=CoSimSessionRefusedException("the server dropped the connection"))
    status, _ = _drive_with(drive, feed_module, monkeypatch, tmp_path, session, "--no-record")

    assert status == 1
    assert session.advanced == [100.0, 101.0, 102.0]
    assert _closed_before_disposal(timeline)
    [emitter] = FakeEmitter.made
    assert len(emitter.sent) == 6 and emitter.closed


def test_an_interrupted_drive_sends_its_last_sample_and_closes_first(
        drive, feed_module, monkeypatch, tmp_path):
    timeline: list = []
    session = FakeSession(timeline, raises_on=2, raising=KeyboardInterrupt())
    with pytest.raises(KeyboardInterrupt):
        _drive_with(drive, feed_module, monkeypatch, tmp_path, session, "--no-record")

    assert _closed_before_disposal(timeline)
    [emitter] = FakeEmitter.made
    assert len(emitter.sent) == 4 and emitter.closed


def test_a_session_refused_at_its_start_opens_no_socket(drive, feed_module, monkeypatch, tmp_path):
    status, world = _drive_with(drive, feed_module, monkeypatch, tmp_path, None, "--no-record")
    assert status == 1
    assert world.drive_arguments["live_track"] is not None
    assert FakeEmitter.made == []
