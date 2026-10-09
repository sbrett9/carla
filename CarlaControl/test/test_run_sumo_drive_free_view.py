"""The drive's free view records as the fixed camera does, and adds nothing to the drive's tick loop.

`carla-drive --view free` (`carlacontrol.commands.drive`, run from a checkout as
`CarlaNet/python/run_sumo_drive.py`) flies a camera inside the process that drives the
world (`12_Operator_Control_Surface.md` section 9.6). What is checked here, with the server and the
session stood in for:

* each span is recorded as the fixed camera's recording is -- the session's render set and
  illumination -- with the rig's depth camera for occlusion, this drive's run id, into the span's
  own folder, and no span starts before the capture window opens;
* the flown rig is spawned over the centre of the world's staging bounds at `--camera-z` -- CARLA's
  origin where the world publishes none -- with the run configuration's depth range rather than the
  depth camera's stock 1000 m;
* the fixed camera carries a depth camera attached to it, rigidly at the identity pose, with the
  camera's image size, field of view and sensor tick and that same depth range, so its captures
  measure occlusion as a capture run's do; there is no switch to turn the measurement off;
* the session is handed no per-vehicle callback it does not need, in either view: the worst
  divergence comes off the session's report, so nothing crosses into Python per vehicle per tick
  while a window thread in the same process holds the interpreter;
* by default nothing the session is handed limits which vehicles are rendered: every vehicle SUMO
  has is drawn. The optional limits -- a circle, the cameras, a capacity -- reach the session only
  when asked for, the flown camera is registered with the session only under the cameras, and a free
  view over a circle smaller than the world is told so;
* the camera, fixed or flown, is spawned under `--camera-name`, and its stills and span folders are
  named after it, or after its default `CARLA-SENSOR-<camera id>`; a name the rule refuses is
  refused before the drive connects to anything;
* a world truth track reaches the session only where `--world-truth-track` asks for one, with the
  interval `--world-truth-track-interval` gives.
"""
from __future__ import annotations

import importlib
import json
import sys
import zipfile
from datetime import UTC, datetime
from pathlib import Path
from types import SimpleNamespace

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

GARDNERVILLE = {
    "MapName": "Gardnerville_Centerville_Lane",
    "StagingMinXMeters": -838.9031372070312, "StagingMinYMeters": -455.0901184082031,
    "StagingMaxXMeters": 839.0968627929688, "StagingMaxYMeters": 456.9098815917969,
}


@pytest.fixture(scope="module")
def drive():
    """The command's module. It imports the co-simulation assemblies, so it needs carlanet."""
    pytest.importorskip("carlanet", reason="the drive needs carlanet and its assemblies")
    try:
        return importlib.import_module("carlacontrol.commands.drive")
    except ImportError as missing:
        pytest.skip(f"the co-simulation assemblies are not loaded here: {missing}")


def _arguments(drive, monkeypatch, *extra: str, package: Path | None = None):
    argv = ["run_sumo_drive.py", "--scenario", "s.sumocfg",
            "--world-package", str(package or "w.cwp"), *extra]
    monkeypatch.setattr(sys, "argv", argv)
    return drive.parse_args()


def test_each_view_gets_its_own_image_size_unless_one_is_given(drive, monkeypatch):
    fixed = _arguments(drive, monkeypatch)
    free = _arguments(drive, monkeypatch, "--view", "free")
    given = _arguments(drive, monkeypatch, "--view", "free", "--width", "1920", "--fov", "60")

    assert (fixed.width, fixed.height, fixed.fov) == (1920, 1080, 60.0)
    assert (free.width, free.height, free.fov) == (1280, 720, 90.0)
    assert (given.width, given.height, given.fov) == (1920, 720, 60.0)


class _StagedWorld:
    """A world that publishes its staging bounds, or none."""

    def __init__(self, bounds: dict | None) -> None:
        self.bounds = bounds

    def get_staging_bounds(self) -> dict | None:
        return self.bounds


def test_the_flown_rig_starts_over_the_world_s_centre_and_measures_depth_as_far_as_a_capture_run(
        drive, monkeypatch):
    from carlacontrol.RunConfiguration import RunConfiguration
    from carlacontrol.SensorRig import SensorRig

    args = _arguments(drive, monkeypatch, "--view", "free", "--camera-z", "450")
    centre = drive.world_centre(_StagedWorld({"min_x": -838.9, "min_y": -455.1, "max_x": 839.1,
                                              "max_y": 456.9, "margin": 50.0}))

    settings = drive.free_view_settings(args, centre, SensorRig.FT_PER_M, drive.depth_range_m())

    # The staging bounds are in CARLA's frame, as the camera is.
    assert (settings.x, settings.y) == pytest.approx((0.1, 0.9))
    assert settings.z / SensorRig.FT_PER_M == pytest.approx(450.0)
    # The run configuration's one depth range, read from its field table, for both views' cameras.
    assert settings.depth_max_range == 20000.0
    assert drive.depth_range_m() == RunConfiguration.field("occlusion.depth_max_range_m").default
    assert (settings.width, settings.height, settings.fov) == (1280, 720, 90.0)
    assert settings.asynchronous is True
    assert settings.ev is None
    # No name given: the rig's camera is its default.
    assert settings.camera_name is None
    named = _arguments(drive, monkeypatch, "--view", "free", "--camera-name", "DECK-I25")
    assert drive.free_view_settings(named, centre, SensorRig.FT_PER_M, 20000.0).camera_name == \
        "DECK-I25"


class _RecordingWorld:
    """The world a span records through: what it was asked to record, and about which camera."""

    def __init__(self, names: dict[int, str] | None = None) -> None:
        self.recorded: list[tuple[tuple, dict]] = []
        self.stops = 0
        self.asked_about: list = []
        self.names = names or {}

    def camera_name(self, camera) -> str:
        return self.names.get(camera.id, f"CARLA-SENSOR-{camera.id}")

    def start_recording(self, *args, **kwargs):
        self.recorded.append((args, kwargs))
        return SimpleNamespace(Saved=0, Dropped=0, PairsRenderSet=True, RenderSetPaired=0,
                               RenderSetUnpaired=0)

    def stop_recording(self) -> None:
        self.stops += 1

    def get_view_readiness(self, camera) -> dict:
        self.asked_about.append(camera)
        return {"frame": 7, "published": True, "tilesets": []}


def test_a_span_of_a_named_camera_is_written_to_a_folder_named_after_it(drive, monkeypatch,
                                                                       tmp_path):
    args = _arguments(drive, monkeypatch, "--view", "free", "--record-dir", str(tmp_path),
                      "--camera-name", "DECK-I25")
    recording = _RecordingWorld(names={4121: "DECK-I25"})
    session = SimpleNamespace(Illumination=None, RenderSet=None, WindowOpensAtSeconds=0.0,
                              RenderedTimeSeconds=0.0)
    parts = drive.FreeViewParts()
    parts.rig = SimpleNamespace(camera=SimpleNamespace(id=4121), depth_cam=None)

    recorder = parts.span_recorder(SimpleNamespace(get_world=lambda: recording), session, args,
                                   "run-20260930-142233")

    assert recorder.sensor_id == "DECK-I25"
    started = datetime(2026, 10, 2, 14, 7, 22, tzinfo=UTC)
    assert recorder.span_directory(tmp_path, recorder.sensor_id, started) == \
        tmp_path / "DECK-I25-20261002T140722Z"


class _Blueprint:
    def __init__(self, blueprint_id: str, attributes: set[str]) -> None:
        self.id = blueprint_id
        self.attributes = attributes
        self.values: dict[str, str] = {}

    def has_attribute(self, name: str) -> bool:
        return name in self.attributes

    def set_attribute(self, name: str, value) -> None:
        self.values[name] = str(value)


class _SpawningWorld:
    """A world a fixed camera is spawned into: the name it was spawned under, and the depth camera
    spawned attached to it, with the attributes each was given."""

    DEPTH = {"image_size_x", "image_size_y", "fov", "sensor_tick", "max_range"}

    def __init__(self, depth: set[str] | None = None) -> None:
        self.spawned: list[tuple[str, str | None]] = []
        self.blueprints = {"sensor.camera.rgb": _Blueprint("sensor.camera.rgb", self.DEPTH),
                           "sensor.camera.depth": _Blueprint(
                               "sensor.camera.depth", self.DEPTH if depth is None else depth)}
        self.attached: list[tuple[str, object, object]] = []

    def get_blueprint_library(self):
        return SimpleNamespace(find=lambda blueprint_id: self.blueprints[blueprint_id])

    def spawn_camera(self, blueprint, transform, name=None):
        self.spawned.append((blueprint.id, name))
        return SimpleNamespace(id=4121)

    def spawn_actor(self, blueprint, transform, attach_to=None, attachment_type=None):
        self.attached.append((blueprint.id, attach_to, attachment_type))
        return SimpleNamespace(id=4122, transform=transform)

    def camera_name(self, camera) -> str:
        return self.spawned[-1][1] or f"CARLA-SENSOR-{camera.id}"


def test_the_fixed_camera_is_spawned_under_its_name_or_left_its_default(drive, monkeypatch):
    world = _SpawningWorld()
    drive.spawn_camera(world, _arguments(drive, monkeypatch, "--camera-name", "DECK-I25"), (0, 0))
    drive.spawn_camera(world, _arguments(drive, monkeypatch), (0, 0))
    assert world.spawned == [("sensor.camera.rgb", "DECK-I25"), ("sensor.camera.rgb", None)]


def test_the_fixed_camera_carries_a_depth_camera_attached_at_its_pose_with_its_optics(
        drive, monkeypatch):
    # Occlusion is measured on the fixed camera's captures as on a capture run's: the depth camera is
    # attached rigidly at the identity pose, so the camera's pose is its pose by construction, and
    # takes the camera's image size, field of view and sensor tick and the run configuration's one
    # depth range rather than the depth camera's stock 1000 m.
    import carlanet

    world = _SpawningWorld()
    args = _arguments(drive, monkeypatch, "--record-hz", "4")
    camera = drive.spawn_camera(world, args, (0, 0))

    depth = drive.spawn_depth_camera(world, args, camera, drive.depth_range_m())

    [(blueprint_id, attached_to, attachment)] = world.attached
    assert (blueprint_id, attached_to, attachment) == ("sensor.camera.depth", camera,
                                                       carlanet.AttachmentType.Rigid)
    pose = depth.transform
    assert (pose.location.x, pose.location.y, pose.location.z, pose.rotation.pitch,
            pose.rotation.yaw, pose.rotation.roll) == (0.0,) * 6
    rgb, depth_blueprint = world.blueprints["sensor.camera.rgb"], world.blueprints["sensor.camera.depth"]
    assert depth_blueprint.values == {**rgb.values, "max_range": "20000.0"}
    assert depth_blueprint.values["sensor_tick"] == "0.25"
    assert (depth_blueprint.values["image_size_x"], depth_blueprint.values["image_size_y"],
            depth_blueprint.values["fov"]) == ("1920", "1080", "60.0")


def test_a_server_whose_depth_camera_has_no_range_attribute_is_said_loudly(drive, monkeypatch,
                                                                            caplog):
    world = _SpawningWorld(depth=_SpawningWorld.DEPTH - {"max_range"})
    args = _arguments(drive, monkeypatch)
    camera = drive.spawn_camera(world, args, (0, 0))
    with caplog.at_level("WARNING", logger="run_sumo_drive"):
        drive.spawn_depth_camera(world, args, camera, 20000.0)
    assert "no max_range attribute" in caplog.text
    assert "max_range" not in world.blueprints["sensor.camera.depth"].values


def test_a_camera_name_the_rule_refuses_ends_the_drive_before_it_connects(drive, monkeypatch,
                                                                         tmp_path, caplog):
    from carlacontrol.CameraName import CameraName

    for name in ("s.sumocfg", "w.cwp", "vehicles.catalogue.json"):
        (tmp_path / name).write_text("{}", encoding="utf-8")
    connected: list = []
    monkeypatch.setattr(drive, "carla", SimpleNamespace(
        camera_name_problem=CameraName.problem, Client=lambda *a: connected.append(a)))
    monkeypatch.setattr(sys, "argv", [
        "run_sumo_drive.py", "--scenario", str(tmp_path / "s.sumocfg"),
        "--world-package", str(tmp_path / "w.cwp"),
        "--catalogue", str(tmp_path / "vehicles.catalogue.json"), "--camera-name", "DECK:I25"])

    with caplog.at_level("ERROR", logger="run_sumo_drive"):
        assert drive.main() == 2
    assert connected == []
    assert "--camera-name: camera name 'DECK:I25' holds ':'" in caplog.text


def test_a_span_records_the_flown_camera_as_the_fixed_camera_records(drive, monkeypatch, tmp_path):
    args = _arguments(drive, monkeypatch, "--view", "free", "--record-dir", str(tmp_path),
                      "--record-hz", "4")
    recording = _RecordingWorld()
    client = SimpleNamespace(get_world=lambda: recording)
    session = SimpleNamespace(Illumination="the session's illumination",
                              RenderSet="the session's render set",
                              WindowOpensAtSeconds=310.0, RenderedTimeSeconds=300.0)
    parts = drive.FreeViewParts()
    parts.rig = SimpleNamespace(camera=SimpleNamespace(id=4121), depth_cam="the rig's depth camera")

    recorder = parts.span_recorder(client, session, args, "run-20260930-142233")

    assert recorder.sensor_id == "CARLA-SENSOR-4121"
    assert recorder.record_dir == tmp_path
    assert recorder.record_hz == 4.0
    # Before the capture window opens a span is refused, and says when it may start.
    assert recorder._may_record() == "the capture window opens at t=310 s"
    session.RenderedTimeSeconds = 310.0
    assert recorder._may_record() is None

    recorder._start_recording(str(tmp_path / "span"))
    (positional, keywords), = recording.recorded
    assert positional == (parts.rig.camera, str(tmp_path / "span"), 4.0)
    assert keywords == {"fov": 90.0, "run_id": "run-20260930-142233",
                        "depth_camera": "the rig's depth camera",
                        "illumination": "the session's illumination",
                        "render_set": "the session's render set"}
    recorder._view_readiness()
    assert recording.asked_about == [parts.rig.camera]
    recorder._stop_recording()
    assert recording.stops == 1


class _DriveWorld:
    """A world whose drive is refused, so a run stops right after asking for it."""

    def __init__(self) -> None:
        self.drive_arguments: dict | None = None

    def get_map(self):
        return SimpleNamespace(name="Gardnerville_Centerville_Lane")

    def start_sumo_drive(self, *args, **kwargs):
        self.drive_arguments = kwargs
        return None

    def stop_recording(self) -> None:
        pass


def _run_main(drive, monkeypatch, tmp_path, *extra: str) -> _DriveWorld:
    package = tmp_path / "Gardnerville_Centerville_Lane.cwp"
    with zipfile.ZipFile(package, "w") as archive:
        archive.writestr("world.json", json.dumps(GARDNERVILLE))
    for name in ("s.sumocfg", "vehicles.catalogue.json"):
        (tmp_path / name).write_text("{}", encoding="utf-8")
    world = _DriveWorld()

    class _Client:
        def __init__(self, *_args) -> None:
            pass

        def set_timeout(self, _seconds) -> None:
            pass

        def get_world(self):
            return world

        def get_server_version(self) -> str:
            return "stand-in"

    monkeypatch.setattr(drive, "carla", SimpleNamespace(Client=_Client))
    monkeypatch.setattr(sys, "argv", [
        "run_sumo_drive.py", "--scenario", str(tmp_path / "s.sumocfg"),
        "--world-package", str(package),
        "--catalogue", str(tmp_path / "vehicles.catalogue.json"), *extra])
    assert drive.main() == 1
    return world


@pytest.mark.parametrize("view", ["fixed", "free"])
def test_the_session_is_handed_no_per_vehicle_divergence_callback(drive, monkeypatch, tmp_path,
                                                                  view):
    world = _run_main(drive, monkeypatch, tmp_path, "--view", view)

    assert world.drive_arguments is not None
    assert "on_divergence" not in world.drive_arguments
    # The pose callback aims the fixed camera, and only the fixed camera.
    assert (world.drive_arguments["on_pose"] is None) == (view == "free")


def test_no_draw_distance_is_handed_to_the_session_unless_one_is_given(drive, monkeypatch, tmp_path):
    # An optional performance control, off unless asked for.
    world = _run_main(drive, monkeypatch, tmp_path)
    assert world.drive_arguments["draw_distance_m"] is None
    world = _run_main(drive, monkeypatch, tmp_path, "--draw-distance", "400")
    assert world.drive_arguments["draw_distance_m"] == 400.0


def test_a_skipped_dry_run_is_accepted_only_when_asked_for(drive, monkeypatch, tmp_path):
    # Off by default: the session refuses a lock whose compile skipped its SUMO-only run.
    world = _run_main(drive, monkeypatch, tmp_path)
    assert world.drive_arguments["accept_skipped_dry_run"] is False
    world = _run_main(drive, monkeypatch, tmp_path, "--accept-skipped-dry-run")
    assert world.drive_arguments["accept_skipped_dry_run"] is True


def test_collision_detail_is_off_unless_asked_for_and_only_binds_a_printer(drive, monkeypatch, tmp_path):
    # Off by default: the report prints the count, and nothing is called per collision.
    world = _run_main(drive, monkeypatch, tmp_path)
    assert world.drive_arguments["collision_detail"] is False
    assert world.drive_arguments["on_collision"] is None
    # On: the report lists every collision, and each is printed as it ends -- by a printer, which keeps
    # nothing the session does not already keep.
    world = _run_main(drive, monkeypatch, tmp_path, "--collision-detail")
    assert world.drive_arguments["collision_detail"] is True
    assert world.drive_arguments["on_collision"] is drive.print_collision


def test_a_collision_printed_as_it_ends_is_the_span_s_own_words(drive, caplog):
    with caplog.at_level("INFO", logger="run_sumo_drive"):
        drive.print_collision("'goer' into 'turner' (collision) on approach_0 at 50.91 m, t=2.15 to 4.4 s")
    assert "collision: 'goer' into 'turner' (collision) on approach_0 at 50.91 m, t=2.15 to 4.4 s" \
        in caplog.text


def test_a_world_truth_track_reaches_the_session_only_when_asked_for(drive, monkeypatch, tmp_path):
    # Off unless asked for: a drive is not a capture run, which always writes one.
    world = _run_main(drive, monkeypatch, tmp_path)
    assert world.drive_arguments["world_truth_track"] is None
    assert world.drive_arguments["world_truth_track_interval_s"] is None
    track = str(tmp_path / "truth" / "world_truth_track.csv")
    world = _run_main(drive, monkeypatch, tmp_path, "--world-truth-track", track,
                      "--world-truth-track-interval", "2")
    assert world.drive_arguments["world_truth_track"] == track
    assert world.drive_arguments["world_truth_track_interval_s"] == 2.0


def test_a_run_manifest_reaches_the_session_only_when_asked_for(drive, monkeypatch, tmp_path):
    world = _run_main(drive, monkeypatch, tmp_path)
    assert world.drive_arguments["run_manifest"] is None
    assert world.drive_arguments["run_manifest_header"] is None
    manifest = str(tmp_path / "truth" / "manifest.jsonl")
    world = _run_main(drive, monkeypatch, tmp_path, "--run-manifest", manifest)
    assert world.drive_arguments["run_manifest"] == manifest
    header = world.drive_arguments["run_manifest_header"]
    assert header["tool"] == "run_sumo_drive.py" and header["run_id"].startswith("run-")
    assert header["view"] == "fixed"


def test_the_launch_says_where_the_world_truth_track_goes_and_how_often(drive):
    assert drive.describe_world_truth_track(None) == \
        "none written; give --world-truth-track to write one"
    every_frame = SimpleNamespace(Path="t.csv", SummaryPath="t.summary.json", SumoStepsPerSample=1,
                                  IntervalSeconds=0.1)
    assert drive.describe_world_truth_track(every_frame).startswith(
        "t.csv, every SUMO frame inside the capture window, every vehicle SUMO has, drawn or not")
    sampled = SimpleNamespace(Path="t.csv", SummaryPath="t.summary.json", SumoStepsPerSample=10,
                              IntervalSeconds=1.0)
    assert drive.describe_world_truth_track(sampled).startswith("t.csv, every 1 s inside")


def test_the_launch_says_what_a_draw_distance_does_and_what_it_leaves_alone(drive):
    assert drive.describe_draw_distance(None) == "none; every body is drawn at any range"
    said = drive.describe_draw_distance(400.0)
    assert said.startswith("400 m, rendering only: every vehicle has its body, is posed and is in "
                           "the truth")
    assert "each capture's sidecar marks it" in said


def test_a_world_with_no_staging_bounds_starts_the_flown_rig_over_carla_s_origin(drive):
    assert drive.world_centre(_StagedWorld(None)) == (0.0, 0.0)


def test_by_default_nothing_the_session_is_handed_limits_which_vehicles_are_rendered(
        drive, monkeypatch, tmp_path):
    for view in ("fixed", "free"):
        handed = _run_main(drive, monkeypatch, tmp_path, "--view", view).drive_arguments
        assert handed["render_set"] == "all", view
        assert (handed["capacity"], handed["region_radius_m"]) == (None, None), view


def test_an_optional_limit_reaches_the_session_as_given(drive, monkeypatch, tmp_path):
    handed = _run_main(drive, monkeypatch, tmp_path, "--render-set", "cameras", "--region-x", "120",
                       "--region-y", "-45", "--region-radius", "300", "--region-hysteresis", "25",
                       "--capacity", "96", "--render-min-pixels", "3", "--render-admit-lead", "4",
                       "--render-release-lag", "6").drive_arguments
    assert handed["render_set"] == "cameras"
    assert handed["region_centre"] == (120.0, -45.0)
    assert (handed["region_radius_m"], handed["region_hysteresis_m"], handed["capacity"]) == \
        (300.0, 25.0, 96)
    assert (handed["render_min_pixels"], handed["render_admit_lead_s"],
            handed["render_release_lag_s"]) == (3.0, 4.0, 6.0)


@pytest.mark.parametrize("option", ["--maximum-bodies", "--render-max-speed"])
def test_a_body_ceiling_and_a_speed_bound_are_not_options(drive, monkeypatch, option):
    with pytest.raises(SystemExit):
        _arguments(drive, monkeypatch, option, "1")


def test_the_render_set_chooses_from_all_circle_and_cameras(drive, monkeypatch):
    assert _arguments(drive, monkeypatch).render_set == "all"
    assert _arguments(drive, monkeypatch, "--render-set", "circle").render_set == "circle"
    with pytest.raises(SystemExit):
        _arguments(drive, monkeypatch, "--render-set", "nearest")


class _Following:
    """A session's camera registration, as the free view uses it."""

    def __init__(self) -> None:
        self.added: list[int] = []
        self.removed: list[int] = []

    def AddCamera(self, camera: int) -> None:  # noqa: N802 -- the .NET member name
        self.added.append(camera)

    def RemoveCamera(self, camera: int) -> bool:  # noqa: N802 -- the .NET member name
        self.removed.append(camera)
        return True


def test_the_flown_camera_is_followed_under_the_cameras_and_let_go_before_it_is_destroyed(drive):
    session = _Following()
    parts = drive.FreeViewParts()
    torn_down: list[str] = []
    parts.rig = SimpleNamespace(camera=SimpleNamespace(id=4121),
                                cleanup=lambda: torn_down.append(f"rig, followed {session.removed}"))

    parts.follow(session)
    assert session.added == [4121]
    parts.close()

    assert session.removed == [4121]
    assert torn_down == ["rig, followed [4121]"]


def test_a_free_view_over_a_circle_smaller_than_the_world_is_told_how_to_take_it_all_in(
        drive, monkeypatch, tmp_path, caplog):
    package = tmp_path / "Gardnerville_Centerville_Lane.cwp"
    with zipfile.ZipFile(package, "w") as archive:
        archive.writestr("world.json", json.dumps(GARDNERVILLE))
    args = _arguments(drive, monkeypatch, "--view", "free", "--render-set", "circle",
                      "--region-radius", "400", package=package)

    with caplog.at_level("INFO", logger="run_sumo_drive"):
        drive.warn_of_an_uncovered_world(args)
    assert "--region-x 0 --region-y -1 --region-radius 956" in caplog.text

    caplog.clear()
    with caplog.at_level("INFO", logger="run_sumo_drive"):
        drive.warn_of_an_uncovered_world(_arguments(drive, monkeypatch, "--view", "free",
                                                    package=package))
    assert caplog.text == ""


def test_the_launch_says_plainly_that_vehicles_outside_a_limit_are_not_in_carla(drive):
    every = SimpleNamespace(RenderSetLimits=False, RenderSetPolicy="every vehicle SUMO has",
                            SumoSeed=42)
    circle = SimpleNamespace(RenderSetLimits=True, SumoSeed=42,
                             RenderSetPolicy="circle of 300 m around (0, 0) in SUMO metres")
    assert drive.describe_render_set(every) == "every vehicle SUMO has; SUMO runs under seed 42"
    said = drive.describe_render_set(circle)
    assert said.startswith("circle of 300 m around (0, 0) in SUMO metres; SUMO runs under seed 42")
    assert "a vehicle outside it is simulated by SUMO and is not in CARLA" in said
