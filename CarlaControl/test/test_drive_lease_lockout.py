"""The traffic tools are refused, naming the holder, while a SUMO drive holds the world's drive lease.

A SUMO drive session takes the lease on the server before SUMO starts; while it holds it the server
refuses every other client's `set_autopilot(True)`, vehicle control and physics control for every
actor, naming the holder (03 §10.2, D3.13). These establish what the Python side makes of that:
`carlacontrol.DriveLease` recognises the server's refusal and names its holder, by the same mark the
.NET traffic manager uses; `TrafficController` refuses to switch traffic on while the lease is held
and switches it off when a spawn's autopilot is refused; and the shim reads the holder off the
server, with a server built before it carried the lease reading as nobody holding it. No server is
contacted: the world is a stand-in, and the server's words are the ones `CarlaServer.cpp` writes.

The shim under test is this tree's `CarlaNet/python/carlanet/__init__.py`, loaded by path: the
installed `carlanet` wheel is whatever was last built, and a `World` method added here is not in it
until the wheel is rebuilt.
"""
from __future__ import annotations

import importlib.util
import logging
import sys
from pathlib import Path
from types import SimpleNamespace

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

import carlanet  # noqa: E402, F401  -- loads the CarlaNet assemblies the shim below shares

from carlacontrol import DriveLease  # noqa: E402
from carlacontrol.TrafficController import TrafficController  # noqa: E402

HOLDER = "run_sumo_drive.py (process 41 on HOST)"
# As CarlaServer.cpp's DriveLeaseRefusal writes it, for each of the four refused calls.
AUTOPILOT_REFUSED = (
    f"set_actor_autopilot: refused while {HOLDER} holds the drive lease on this world; no other traffic "
    "drives a vehicle here until the holder releases it (release_drive_lease), the world is reloaded, "
    "or the lease is broken (break_drive_lease)")
CONTROL_REFUSED = (
    f"apply_control_to_vehicle: refused while {HOLDER} holds the drive lease on this world; no other "
    "traffic drives a vehicle here until the holder releases it (release_drive_lease), the world is "
    "reloaded, or the lease is broken (break_drive_lease)")
NO_SUCH_FUNCTION = "rpclib: server could not find function 'get_drive_lease' with argument count 1."


@pytest.fixture(scope="module")
def shim():
    """This tree's shim, as a module of its own, beside the installed one."""
    path = _REPO / "CarlaNet" / "python" / "carlanet" / "__init__.py"
    spec = importlib.util.spec_from_file_location("carlanet_of_this_tree", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class _Answer:
    """A .NET task that answers at once, or raises, as the shim's `_sync` reads one."""

    def __init__(self, value=None, raises=None):
        self._value = value
        self._raises = raises

    def GetAwaiter(self):
        return self

    def GetResult(self):
        if self._raises is not None:
            raise self._raises
        return self._value


class _Client:
    """The C# client as `carla.World` reads it, holding one lease name, or none, or no such call."""

    def __init__(self, holder=None, raises=None):
        self.holder = holder
        self.raises = raises
        self.broken = []

    def GetDriveLeaseHolderAsync(self):
        return _Answer(self.holder, self.raises)

    def BreakDriveLeaseAsync(self):
        if self.raises is not None:
            return _Answer(raises=self.raises)
        broken, self.holder = self.holder, None
        self.broken.append(broken)
        return _Answer(broken)


# -- the server's refusal, recognised and its holder named -------------------------------------------

@pytest.mark.parametrize("words", [AUTOPILOT_REFUSED, CONTROL_REFUSED])
def test_the_servers_refusal_is_recognised_and_names_its_holder(words):
    assert DriveLease.refusal(RuntimeError(words)) == words
    assert DriveLease.refusal(words) == words
    assert DriveLease.holder_named(words) == HOLDER
    assert DriveLease.locked_out_by(RuntimeError(words)) == HOLDER


def test_a_dotnet_exception_is_read_by_its_message():
    assert DriveLease.refusal(SimpleNamespace(Message=AUTOPILOT_REFUSED)) == AUTOPILOT_REFUSED


@pytest.mark.parametrize("other", [
    "Responding error from function set_actor_autopilot: Actor not found. Actor Id: 7",
    NO_SUCH_FUNCTION,
    "unable to destroy actor: not found",
])
def test_any_other_error_is_not_a_lockout(other):
    assert DriveLease.refusal(RuntimeError(other)) is None
    assert DriveLease.locked_out_by(other) is None


def test_words_without_the_lead_in_name_nothing_invented():
    assert DriveLease.holder_named("holds the drive lease on this world") == ""
    assert DriveLease.holder_named("something else entirely") == "something else entirely"


def test_the_mark_is_the_dotnet_traffic_managers():
    # One mark, in CarlaServer.cpp's words, read by both clients. The traffic manager's is internal,
    # so it is read by reflection.
    from System.Reflection import Assembly, BindingFlags
    manager = Assembly.Load("CarlaNet.TrafficManager").GetType("CarlaNet.TrafficManager.TrafficManagerLocal")
    assert manager is not None, "CarlaNet.TrafficManager.TrafficManagerLocal is not there to compare"
    field = manager.GetField("DriveLeaseRefusalMark", BindingFlags.NonPublic | BindingFlags.Static)
    assert field is not None, "TrafficManagerLocal.DriveLeaseRefusalMark is not there to compare"
    assert str(field.GetValue(None)) == DriveLease.REFUSAL_MARK


# -- the shim's reads of the lease -----------------------------------------------------------------

def test_the_world_answers_the_holder_or_none(shim):
    assert shim.World(_Client(holder=None)).drive_lease_holder() is None
    assert shim.World(_Client(holder=HOLDER)).drive_lease_holder() == HOLDER


def test_a_server_without_the_lease_reads_as_nobody_holding_it(shim):
    world = shim.World(_Client(raises=RuntimeError(NO_SUCH_FUNCTION)))
    assert world.drive_lease_holder() is None
    assert world.break_drive_lease() is None


def test_any_other_failure_of_the_read_is_raised(shim):
    world = shim.World(_Client(raises=RuntimeError("episode not ready")))
    with pytest.raises(RuntimeError, match="episode not ready"):
        world.drive_lease_holder()


def test_breaking_answers_the_holder_whose_lease_was_ended(shim):
    client = _Client(holder=HOLDER)
    world = shim.World(client)
    assert world.break_drive_lease() == HOLDER
    assert client.broken == [HOLDER]
    assert world.break_drive_lease() is None


# -- TrafficController ---------------------------------------------------------------------------

class _World:
    def __init__(self, holder=None, raises=None):
        self.holder = holder
        self.raises = raises

    def drive_lease_holder(self):
        if self.raises is not None:
            raise self.raises
        return self.holder

    def get_map(self):
        return SimpleNamespace(get_spawn_points=lambda: [])


def _controller(world) -> TrafficController:
    args = SimpleNamespace(max=10, route=False, tm_port=8000, fade=False, speed_spread=0.0,
                           spawn_at_speed=False, speed_scale=100.0, spawn_interval=1.0)
    return TrafficController(world, tm=None, args=args, staging=None, blueprints=[], ring_sps=[],
                             spawn_pool=[], floor_z=-1000.0)


def test_traffic_is_refused_naming_the_holder_while_a_drive_holds_the_world(caplog):
    traffic = _controller(_World(holder=HOLDER))
    with caplog.at_level(logging.ERROR, logger="carlacontrol.TrafficController"):
        assert traffic.enable() is False
    assert not traffic.enabled
    assert HOLDER in caplog.text
    assert "drive lease" in caplog.text
    assert "break_drive_lease" in caplog.text


def test_a_toggle_that_was_refused_does_not_stay_wanted():
    traffic = _controller(_World(holder=HOLDER))
    traffic.toggle_want(True)
    traffic.apply_want()
    assert not traffic.enabled
    assert not traffic.want_enabled


def test_traffic_is_enabled_while_nobody_holds_the_world():
    traffic = _controller(_World(holder=None))
    assert traffic.enable() is True
    assert traffic.enabled


def test_a_world_that_cannot_say_who_holds_it_does_not_refuse_traffic():
    # A read that fails is not a held lease: the server's own refusal of the first autopilot is what
    # stops traffic then, and it says who holds the world.
    assert _controller(_World(raises=RuntimeError("episode not ready"))).enable() is True


def test_a_shim_without_the_read_does_not_refuse_traffic():
    world = _World(holder=HOLDER)
    world.drive_lease_holder = None
    assert _controller(world).enable() is True


def test_the_holder_is_read_off_a_refused_autopilot_and_off_nothing_else():
    traffic = _controller(_World())
    assert traffic.locked_out_by(RuntimeError(AUTOPILOT_REFUSED)) == HOLDER
    assert traffic.locked_out_by(RuntimeError("Actor not found. Actor Id: 7")) is None


def test_a_spawn_whose_autopilot_is_refused_switches_traffic_off_naming_the_holder(caplog):
    # A drive took the lease after traffic was switched on: the server refuses the next vehicle's
    # autopilot, and every spawn from then on would be refused the same way.
    destroyed = []

    def refuse_the_autopilot(*_):
        raise RuntimeError(AUTOPILOT_REFUSED)

    vehicle = SimpleNamespace(id=7, bounding_box=SimpleNamespace(extent=SimpleNamespace(x=2.4, y=1.0)),
                              set_transform=lambda _t: None, set_autopilot=refuse_the_autopilot,
                              destroy=lambda: destroyed.append(7))
    world = _World()
    world.spawn_actor = lambda _bp, _sp: vehicle
    site = SimpleNamespace(location=SimpleNamespace(x=0.0, y=0.0, z=0.0),
                           rotation=SimpleNamespace(pitch=0.0, yaw=0.0, roll=0.0))
    traffic = _controller(world)
    traffic.spawn_pool = [site]
    traffic.blueprints = [SimpleNamespace(id="vehicle.test.body")]
    traffic.occupied = lambda _x, _y: False
    traffic.clear_shift = lambda _location, _yaw, _extent: (0.0, 0.0)
    traffic.spawn_bp = lambda: traffic.blueprints[0]
    traffic.interior_opacity = lambda *_: 1.0
    assert traffic.enable() is True
    traffic.want_enabled = True

    with caplog.at_level(logging.ERROR, logger="carlacontrol.TrafficController"):
        assert traffic.spawn_one(now=1.0) is False

    assert destroyed == [7]
    assert not traffic.enabled
    assert not traffic.want_enabled
    assert traffic.actors == {}
    assert "traffic OFF" in caplog.text
    assert HOLDER in caplog.text


# -- generate_traffic_carlanet.py ----------------------------------------------------------------

def test_generate_traffic_is_refused_before_it_spawns_anything(monkeypatch, capsys):
    """The example script asks who holds the world before it touches it, and says so on one line."""
    path = _REPO / "PythonAPI" / "examples" / "generate_traffic_carlanet.py"
    spec = importlib.util.spec_from_file_location("generate_traffic_carlanet", path)
    script = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(script)

    touched = []

    class _Client:
        def __init__(self, _host, _port):
            pass

        def set_timeout(self, _seconds):
            pass

        def get_world(self):
            return SimpleNamespace(drive_lease_holder=lambda: HOLDER,
                                   get_settings=lambda: touched.append("settings"),
                                   get_blueprint_library=lambda: touched.append("blueprints"))

        def get_trafficmanager(self, _port):
            touched.append("traffic manager")

        def apply_batch(self, commands):
            touched.extend(commands)

    monkeypatch.setattr(script.carla, "Client", _Client)
    monkeypatch.setattr(sys, "argv", ["generate_traffic_carlanet.py", "-n", "5", "-w", "0"])

    assert script.main() == 1

    assert touched == []
    said = capsys.readouterr().err
    assert "refused" in said
    assert HOLDER in said
    assert "break_drive_lease" in said
