"""
carlanet.py — Python shim for CarlaNet.
Exposes CarlaNet.Transport.CarlaClient with snake_case API matching libcarla conventions.

Usage:
    from carlanet import Client
    client = Client("192.168.1.10", 2000)
    world = client.get_world()

§12: sys.path bootstrapper — MUST remove script dir before importing clr
to prevent Python namespace package finder shadowing the CLR import hook (§13.8).
"""
import sys
import os

_this_dir = os.path.dirname(os.path.abspath(__file__))
if _this_dir in sys.path:
    sys.path.remove(_this_dir)

import clr_loader
import pythonnet

# Default to .NET 10 if available; fall back to highest installed.
def _find_runtime(prefix="10."):
    specs = list(clr_loader.find_runtimes())
    matches = [s for s in specs if s.name == "Microsoft.NETCore.App" and s.version.startswith(prefix)]
    if not matches and prefix != "":
        matches = [s for s in specs if s.name == "Microsoft.NETCore.App"]
    return sorted(matches, key=lambda s: s.version, reverse=True)[0] if matches else None

_spec = _find_runtime("10.") or _find_runtime("")
if _spec is not None:
    pythonnet.load(clr_loader.get_coreclr(runtime_spec=_spec))
else:
    pythonnet.load("coreclr")

import clr

_PUBLISH_DIR = os.environ.get("CARLANET_PUBLISH_DIR", os.path.join(_this_dir, "publish"))

def _ref(name):
    path = os.path.join(_PUBLISH_DIR, name + ".dll")
    if not os.path.exists(path):
        raise FileNotFoundError(f"CarlaNet assembly not found: {path}\n"
                                f"Set CARLANET_PUBLISH_DIR to the published output directory.")
    clr.AddReference(path)

_ref("CarlaNet.Types")
_ref("CarlaNet.Transport")
_ref("CarlaNet.Sensors")

from CarlaNet.Transport import CarlaClient as _CarlaClient
from CarlaNet.Types.Geom import Transform, Location, Rotation, Vector3D, BoundingBox
from CarlaNet.Types.Rpc.Actors import Actor, ActorDefinition, ActorDescription
from CarlaNet.Types.Rpc.Control import VehicleControl, WalkerControl
from CarlaNet.Types.Rpc.Environment import EpisodeSettings, WeatherParameters
from CarlaNet.Types.Rpc.Enums import TrafficLightState, MapLayer, VehicleLightStateFlags


def _sync(task):
    """Block on a .NET Task, returning its result."""
    return task.GetAwaiter().GetResult()


class Client:
    def __init__(self, host: str, port: int = 2000, timeout_ms: int = 5000):
        from System import TimeSpan
        self._inner = _CarlaClient(host, port, TimeSpan.FromMilliseconds(timeout_ms))
        self._host = host

    def get_client_version(self) -> str:
        return self._inner.GetClientVersion()

    def get_server_version(self) -> str:
        return _sync(self._inner.GetServerVersionAsync())

    def get_world(self):
        return World(self._inner)

    def get_available_maps(self):
        return _sync(self._inner.GetAvailableMapsAsync())

    def load_world(self, map_name: str, reset_settings: bool = True):
        _sync(self._inner.LoadEpisodeAsync(map_name, reset_settings))
        return self.get_world()

    def get_trafficmanager(self, port: int = 8000):
        from carlanet import TrafficManager
        return TrafficManager(self._inner.GetTrafficManager(port))

    def __del__(self):
        try:
            self._inner.DisposeAsync().GetAwaiter().GetResult()
        except Exception:
            pass


class World:
    def __init__(self, client):
        self._client = client

    def get_settings(self) -> EpisodeSettings:
        return _sync(self._client.GetEpisodeSettingsAsync())

    def apply_settings(self, settings: EpisodeSettings) -> int:
        return _sync(self._client.SetEpisodeSettingsAsync(settings))

    def get_spectator(self) -> Actor:
        return _sync(self._client.GetSpectatorAsync())

    def get_actors(self, actor_ids=None):
        if actor_ids is None:
            info = _sync(self._client.GetEpisodeInfoAsync())
            ids = []
        else:
            ids = list(actor_ids)
        return _sync(self._client.GetActorsByIdAsync(ids))

    def get_blueprint_library(self):
        return _sync(self._client.GetActorDefinitionsAsync())

    def spawn_actor(self, blueprint: ActorDescription, transform: Transform,
                    attach_to=None, attachment_type=None) -> Actor:
        if attach_to is not None:
            from CarlaNet.Types.Rpc.Enums import AttachmentType
            at = attachment_type if attachment_type is not None else AttachmentType.Rigid
            return _sync(self._client.SpawnActorWithParentAsync(blueprint, transform, attach_to.id, at))
        return _sync(self._client.SpawnActorAsync(blueprint, transform))

    def destroy_actor(self, actor_id: int) -> bool:
        return _sync(self._client.DestroyActorAsync(actor_id))

    def get_weather(self) -> WeatherParameters:
        return _sync(self._client.GetWeatherParametersAsync())

    def set_weather(self, weather: WeatherParameters):
        _sync(self._client.SetWeatherParametersAsync(weather))

    def tick(self) -> int:
        return _sync(self._client.SendTickCueAsync())


class TrafficManager:
    def __init__(self, tm):
        self._tm = tm

    def set_percentage_speed_difference(self, actor: Actor, pct: float):
        _sync(self._tm.SetPercentageSpeedDifferenceAsync(actor, pct))

    def set_global_percentage_speed_difference(self, pct: float):
        _sync(self._tm.SetGlobalPercentageSpeedDifferenceAsync(pct))

    def set_synchronous_mode(self, enabled: bool):
        _sync(self._tm.SetSynchronousModeAsync(enabled))

    def synchronous_tick(self) -> bool:
        return _sync(self._tm.SynchronousTickAsync())
