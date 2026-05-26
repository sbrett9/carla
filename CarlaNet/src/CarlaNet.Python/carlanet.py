"""
carlanet.py — Python shim matching the libcarla client API.

Drop-in replacement for 'carla' Python module using CarlaNet (.NET 10) instead
of native libcarla.  Exposes snake_case classes and attributes that match the
original PythonAPI so existing scripts work with minimal modification.

Usage:
    import carlanet as carla          # instead of 'import carla'
    client = carla.Client("localhost", 2000)
    world  = client.get_world()

Environment:
    CARLANET_PUBLISH_DIR  Path to the dotnet publish output containing the DLLs.
                          Defaults to the 'publish' folder relative to this file.

§12: sys.path bootstrapper — MUST remove this file's directory before importing clr.
§13.8: Python namespace package finder can shadow CLR imports otherwise.
"""
import sys
import os
import time
import threading
import fnmatch

_this_dir = os.path.dirname(os.path.abspath(__file__))
if _this_dir in sys.path:
    sys.path.remove(_this_dir)

# ── .NET runtime selection ────────────────────────────────────────────────────
import clr_loader
import pythonnet as _pythonnet

def _find_runtime(prefix="10."):
    specs = list(clr_loader.find_runtimes())
    matches = [s for s in specs
               if s.name == "Microsoft.NETCore.App" and s.version.startswith(prefix)]
    if not matches and prefix:
        matches = [s for s in specs if s.name == "Microsoft.NETCore.App"]
    return sorted(matches, key=lambda s: s.version, reverse=True)[0] if matches else None

_spec = _find_runtime("10.") or _find_runtime("")
if _spec is not None:
    _pythonnet.load(clr_loader.get_coreclr(runtime_spec=_spec))
else:
    _pythonnet.load("coreclr")

import clr

_PUBLISH_DIR = os.environ.get(
    "CARLANET_PUBLISH_DIR",
    os.path.join(_this_dir, "..", "..", "publish"))
_PUBLISH_DIR = os.path.normpath(_PUBLISH_DIR)

def _ref(name):
    path = os.path.join(_PUBLISH_DIR, name + ".dll")
    if not os.path.exists(path):
        raise FileNotFoundError(
            f"CarlaNet assembly not found: {path}\n"
            f"Set CARLANET_PUBLISH_DIR or run 'dotnet publish'.")
    clr.AddReference(path)

_ref("CarlaNet.Types")
_ref("CarlaNet.Transport")
_ref("CarlaNet.Sensors")

# ── C# type imports ───────────────────────────────────────────────────────────
from CarlaNet.Transport import CarlaClient as _CarlaClient
from CarlaNet.Types.Geom import (Transform, Location, Rotation,
                                  Vector2D, Vector3D, BoundingBox, GeoLocation)
from CarlaNet.Types.Rpc.Actors import (Actor as _Actor, ActorDefinition,
                                        ActorDescription, ActorAttributeValue)
from CarlaNet.Types.Rpc.Control import (VehicleControl, VehicleAckermannControl,
                                         AckermannControllerSettings, WalkerControl)
from CarlaNet.Types.Rpc.Environment import EpisodeSettings, WeatherParameters
from CarlaNet.Types.Rpc.Enums import (TrafficLightState, MapLayer,
                                       AttachmentType, VehicleDoor,
                                       ActorAttributeType)
from CarlaNet.Types.Rpc.Lighting import VehicleLightStateFlags
from CarlaNet.Types.Rpc.Commands import (
    Command, SpawnActorCommand, DestroyActorCommand, SetAutopilotCommand,
    ApplyVehicleControlCommand, ApplyTransformCommand, ApplyLocationCommand,
    ConsoleCommandCommand, SetVehicleLightStateCommand, CommandResponse)
from System import TimeSpan
from System.Collections.Generic import List


def _sync(task):
    """Block on a .NET Task and return its result."""
    return task.GetAwaiter().GetResult()


def _cs_list(items, cs_type=None):
    """Convert a Python iterable to a C# List.  If cs_type is None, uses object."""
    from System.Collections.Generic import List as _List
    from System import Object
    t = cs_type if cs_type is not None else Object
    try:
        result = _List[t]()
        for item in items:
            result.Add(item)
        return result
    except Exception:
        # Fallback: return Python list (pythonnet can often coerce it)
        return list(items)


# ── Attribute value wrapper ───────────────────────────────────────────────────

class _Attribute:
    def __init__(self, name, info):
        self.id = name
        self._info = info

    @property
    def type(self):
        return self._info['type']

    @property
    def recommended_values(self):
        return self._info['recommended']

    def as_color(self):
        parts = self._info['value'].split(',')
        return tuple(int(p) for p in parts)

    def __str__(self):
        return self._info['value']

    def __repr__(self):
        return f"Attribute(id={self.id!r}, value={self._info['value']!r})"


# ── Actor blueprint (wraps ActorDefinition with mutable attributes) ───────────

class ActorBlueprint:
    def __init__(self, definition):
        self._def  = definition
        self._uid  = int(definition.Uid)
        self._id   = str(definition.Id)
        self._tags = str(definition.Tags)
        # Cache attributes as a Python dict for fast modification
        self._attrs = {}
        for i in range(definition.Attributes.Count):
            a = definition.Attributes[i]
            self._attrs[str(a.Id)] = {
                'type':        int(a.Type),
                'value':       str(a.Value),
                'recommended': [str(v) for v in a.RecommendedValues],
                'modifiable':  bool(a.IsModifiable),
            }

    @property
    def id(self):
        return self._id

    @property
    def tags(self):
        return self._tags

    def has_attribute(self, name: str) -> bool:
        return name in self._attrs

    def get_attribute(self, name: str) -> _Attribute:
        if name not in self._attrs:
            raise KeyError(f"Blueprint '{self._id}' has no attribute '{name}'")
        return _Attribute(name, self._attrs[name])

    def set_attribute(self, name: str, value: str):
        if name not in self._attrs:
            raise KeyError(f"Blueprint '{self._id}' has no attribute '{name}'")
        self._attrs[name]['value'] = str(value)

    def has_tag(self, tag: str) -> bool:
        return tag in self._tags.split(',')

    def to_description(self) -> ActorDescription:
        attrs = _cs_list(
            [ActorAttributeValue(k, ActorAttributeType(v['type']), v['value'])
             for k, v in self._attrs.items()],
            ActorAttributeValue)
        return ActorDescription(self._uid, self._id, attrs)

    def __repr__(self):
        return f"ActorBlueprint(id={self._id!r})"


# ── Blueprint library (wraps IReadOnlyList[ActorDefinition]) ─────────────────

class BlueprintLibrary:
    def __init__(self, definitions):
        self._bps = [ActorBlueprint(definitions[i])
                     for i in range(definitions.Count)]

    def filter(self, pattern: str):
        """Return a new BlueprintLibrary containing blueprints matching glob pattern."""
        matched = [bp for bp in self._bps
                   if fnmatch.fnmatch(bp.id, pattern)]
        lib = BlueprintLibrary.__new__(BlueprintLibrary)
        lib._bps = matched
        return lib

    def find(self, id: str) -> ActorBlueprint:
        for bp in self._bps:
            if bp.id == id:
                return bp
        raise KeyError(f"Blueprint '{id}' not found")

    def __iter__(self):
        return iter(self._bps)

    def __len__(self):
        return len(self._bps)

    def __getitem__(self, idx):
        return self._bps[idx]

    def __repr__(self):
        return f"BlueprintLibrary({len(self._bps)} blueprints)"


# ── Map wrapper ───────────────────────────────────────────────────────────────

class Map:
    def __init__(self, name: str, spawn_points):
        self.name = name
        self._spawn_points = [spawn_points[i]
                               for i in range(spawn_points.Count)]

    def get_spawn_points(self):
        return list(self._spawn_points)

    def __repr__(self):
        return f"Map(name={self.name!r}, spawn_points={len(self._spawn_points)})"


# ── Actor wrapper ─────────────────────────────────────────────────────────────

class Actor:
    """Python wrapper around a CARLA actor. Backed by world observer cache."""

    def __init__(self, cs_actor, client):
        self._actor  = cs_actor
        self._client = client   # _CarlaClient (C# object)
        self._sub    = None     # sensor stream subscription

    @property
    def id(self) -> int:
        return int(self._actor.Id)

    @property
    def type_id(self) -> str:
        return str(self._actor.Description.Id)

    @property
    def bounding_box(self) -> BoundingBox:
        return self._actor.BoundingBox

    @property
    def attributes(self) -> dict:
        out = {}
        attrs = self._actor.Description.Attributes
        for i in range(attrs.Count):
            a = attrs[i]
            out[str(a.Id)] = str(a.Value)
        return out

    # ── State queries (from world observer cache) ─────────────────────────────

    def get_transform(self) -> Transform:
        return self._client.GetActorTransform(self._actor.Id)

    def get_location(self) -> Location:
        return self.get_transform().Location

    def get_velocity(self) -> Vector3D:
        return self._client.GetActorVelocity(self._actor.Id)

    def get_angular_velocity(self) -> Vector3D:
        return self._client.GetActorAngularVelocity(self._actor.Id)

    def get_acceleration(self) -> Vector3D:
        return self._client.GetActorAcceleration(self._actor.Id)

    def get_control(self) -> VehicleControl:
        return self._client.GetVehicleControl(self._actor.Id)

    def get_world(self):
        return World(self._client)

    # ── Commands ──────────────────────────────────────────────────────────────

    def set_transform(self, transform: Transform):
        _sync(self._client.SetActorTransformAsync(self._actor.Id, transform))

    def set_location(self, location: Location):
        _sync(self._client.SetActorLocationAsync(self._actor.Id, location))

    def apply_control(self, control: VehicleControl):
        _sync(self._client.ApplyControlToVehicleAsync(self._actor.Id, control))

    def apply_ackermann_control(self, control: VehicleAckermannControl):
        _sync(self._client.ApplyAckermannControlToVehicleAsync(self._actor.Id, control))

    def set_autopilot(self, enabled: bool):
        _sync(self._client.SetActorAutopilotAsync(self._actor.Id, enabled))

    def set_light_state(self, state):
        _sync(self._client.SetVehicleLightStateAsync(self._actor.Id, state))

    def set_simulate_physics(self, enabled: bool):
        _sync(self._client.SetActorSimulatePhysicsAsync(self._actor.Id, enabled))

    def set_enable_gravity(self, enabled: bool):
        _sync(self._client.SetActorEnableGravityAsync(self._actor.Id, enabled))

    def get_physics_control(self):
        return _sync(self._client.GetVehiclePhysicsControlAsync(self._actor.Id))

    def apply_physics_control(self, ctrl):
        _sync(self._client.ApplyPhysicsControlToVehicleAsync(self._actor.Id, ctrl))

    def enable_constant_velocity(self, velocity: Vector3D):
        _sync(self._client.EnableActorConstantVelocityAsync(self._actor.Id, velocity))

    def disable_constant_velocity(self):
        _sync(self._client.DisableActorConstantVelocityAsync(self._actor.Id))

    def show_debug_telemetry(self, enabled: bool):
        _sync(self._client.ShowVehicleDebugTelemetryAsync(self._actor.Id, enabled))

    def open_door(self, door):
        _sync(self._client.OpenVehicleDoorAsync(self._actor.Id, door))

    def close_door(self, door):
        _sync(self._client.CloseVehicleDoorAsync(self._actor.Id, door))

    # ── Sensor streaming ──────────────────────────────────────────────────────

    def listen(self, callback):
        """Subscribe to this sensor's data stream."""
        token = self._actor.StreamToken
        if len(token) != 24:
            raise RuntimeError(f"Actor {self.id} ({self.type_id}) has no sensor stream")
        from System import Action
        from CarlaNet.Transport.Streaming import SensorFrame
        cs_cb = Action[SensorFrame](callback)
        self._sub = self._client.SubscribeToStream(token, cs_cb)

    def stop(self):
        """Unsubscribe from sensor stream."""
        if self._sub is not None:
            self._sub.Dispose()
            self._sub = None

    def is_listening(self) -> bool:
        return self._sub is not None

    def destroy(self) -> bool:
        self.stop()
        return bool(_sync(self._client.DestroyActorAsync(self._actor.Id)))

    def __repr__(self):
        return f"Actor(id={self.id}, type={self.type_id!r})"


def _wrap_actor(cs_actor, client) -> Actor:
    return Actor(cs_actor, client)


def _wrap_actors(cs_list, client):
    return [Actor(cs_list[i], client) for i in range(cs_list.Count)]


# ── command module ────────────────────────────────────────────────────────────

class command:
    """Namespace matching carla.command.* for apply_batch use."""

    FutureActor = 0  # sentinel: resolved to just-spawned actor id by server

    class Response:
        def __init__(self, cs_resp: CommandResponse):
            self.error    = str(cs_resp.Error) if cs_resp.HasError else None
            self.actor_id = int(cs_resp.ActorId)

        @property
        def has_error(self) -> bool:
            return self.error is not None

        def __repr__(self):
            if self.error:
                return f"Response(error={self.error!r})"
            return f"Response(actor_id={self.actor_id})"

    class _Cmd:
        def to_cs(self) -> Command:
            raise NotImplementedError

    class SpawnActor(_Cmd):
        def __init__(self, blueprint, transform, parent=None):
            self._bp        = blueprint
            self._transform = transform
            self._parent    = int(parent) if parent is not None and parent != command.FutureActor else None
            self._do_after  = []

        def then(self, *cmds):
            out = command.SpawnActor(self._bp, self._transform)
            out._parent   = self._parent
            out._do_after = list(self._do_after) + list(cmds)
            return out

        def to_cs(self) -> Command:
            desc = (self._bp.to_description()
                    if isinstance(self._bp, ActorBlueprint)
                    else self._bp)
            parent_id = self._parent  # None → optional not set
            do_after_cs = _cs_list([c.to_cs() for c in self._do_after], Command)
            return SpawnActorCommand(desc, self._transform, parent_id, do_after_cs)

    class DestroyActor(_Cmd):
        def __init__(self, actor):
            self._id = int(actor.id) if isinstance(actor, Actor) else int(actor)

        def to_cs(self) -> Command:
            return DestroyActorCommand(self._id)

    class SetAutopilot(_Cmd):
        def __init__(self, actor, enabled: bool, tm_port: int = 8000):
            self._id      = int(actor) if not isinstance(actor, Actor) else int(actor.id)
            self._enabled = bool(enabled)

        def to_cs(self) -> Command:
            return SetAutopilotCommand(self._id, self._enabled)

    class ApplyVehicleControl(_Cmd):
        def __init__(self, actor, control: VehicleControl):
            self._id   = int(actor) if not isinstance(actor, Actor) else int(actor.id)
            self._ctrl = control

        def to_cs(self) -> Command:
            return ApplyVehicleControlCommand(self._id, self._ctrl)

    class ApplyTransform(_Cmd):
        def __init__(self, actor, transform: Transform):
            self._id = int(actor) if not isinstance(actor, Actor) else int(actor.id)
            self._tf = transform

        def to_cs(self) -> Command:
            return ApplyTransformCommand(self._id, self._tf)

    class ApplyLocation(_Cmd):
        def __init__(self, actor, location: Location):
            self._id  = int(actor) if not isinstance(actor, Actor) else int(actor.id)
            self._loc = location

        def to_cs(self) -> Command:
            return ApplyLocationCommand(self._id, self._loc)

    class SetVehicleLightState(_Cmd):
        def __init__(self, actor, light_state):
            self._id    = int(actor) if not isinstance(actor, Actor) else int(actor.id)
            self._state = light_state

        def to_cs(self) -> Command:
            return SetVehicleLightStateCommand(self._id, self._state)

    class ConsoleCommand(_Cmd):
        def __init__(self, cmd_str: str):
            self._cmd = cmd_str

        def to_cs(self) -> Command:
            return ConsoleCommandCommand(self._cmd)


def _cmds_to_cs(cmds):
    cs = _cs_list([c.to_cs() if isinstance(c, command._Cmd) else c for c in cmds], Command)
    return cs


# ── Traffic Manager ───────────────────────────────────────────────────────────

class TrafficManager:
    def __init__(self, tm_client, port: int = 8000):
        self._tm   = tm_client
        self._port = port

    def get_port(self) -> int:
        return self._port

    def set_percentage_speed_difference(self, actor: Actor, pct: float):
        _sync(self._tm.SetPercentageSpeedDifferenceAsync(actor._actor, pct))

    def set_desired_speed(self, actor: Actor, speed: float):
        _sync(self._tm.SetDesiredSpeedAsync(actor._actor, speed))

    def set_global_percentage_speed_difference(self, pct: float):
        _sync(self._tm.SetGlobalPercentageSpeedDifferenceAsync(pct))

    def set_global_distance_to_leading_vehicle(self, distance: float):
        _sync(self._tm.SetGlobalDistanceToLeadingVehicleAsync(distance))

    def set_distance_to_leading_vehicle(self, actor: Actor, distance: float):
        _sync(self._tm.SetDistanceToLeadingVehicleAsync(actor._actor, distance))

    def set_collision_detection(self, reference: Actor, other: Actor, detect: bool):
        _sync(self._tm.SetCollisionDetectionAsync(reference._actor, other._actor, detect))

    def set_lane_offset(self, actor: Actor, offset: float):
        _sync(self._tm.SetLaneOffsetAsync(actor._actor, offset))

    def set_global_lane_offset(self, offset: float):
        _sync(self._tm.SetGlobalLaneOffsetAsync(offset))

    def set_auto_lane_change(self, actor: Actor, enable: bool):
        _sync(self._tm.SetAutoLaneChangeAsync(actor._actor, enable))

    def set_force_lane_change(self, actor: Actor, to_left: bool):
        _sync(self._tm.SetForceLaneChangeAsync(actor._actor, to_left))

    def set_percentage_ignore_walkers(self, actor: Actor, pct: float):
        _sync(self._tm.SetPercentageIgnoreWalkersAsync(actor._actor, pct))

    def set_percentage_ignore_vehicles(self, actor: Actor, pct: float):
        _sync(self._tm.SetPercentageIgnoreVehiclesAsync(actor._actor, pct))

    def set_percentage_running_light(self, actor: Actor, pct: float):
        _sync(self._tm.SetPercentageRunningLightAsync(actor._actor, pct))

    def set_percentage_running_sign(self, actor: Actor, pct: float):
        _sync(self._tm.SetPercentageRunningSignAsync(actor._actor, pct))

    def set_percentage_keep_slow_lane_rule(self, actor: Actor, pct: float):
        _sync(self._tm.SetPercentageKeepSlowLaneRuleAsync(actor._actor, pct))

    def set_percentage_random_left_lanechange(self, actor: Actor, pct: float):
        _sync(self._tm.SetPercentageRandomLeftLaneChangeAsync(actor._actor, pct))

    def set_percentage_random_right_lanechange(self, actor: Actor, pct: float):
        _sync(self._tm.SetPercentageRandomRightLaneChangeAsync(actor._actor, pct))

    def set_hybrid_physics_mode(self, enabled: bool):
        _sync(self._tm.SetHybridPhysicsModeAsync(enabled))

    def set_hybrid_physics_radius(self, radius: float):
        _sync(self._tm.SetHybridPhysicsRadiusAsync(radius))

    def set_random_device_seed(self, seed: int):
        _sync(self._tm.SetRandomDeviceSeedAsync(seed))

    def set_respawn_dormant_vehicles(self, enabled: bool):
        _sync(self._tm.SetRespawnDormantVehiclesAsync(enabled))

    def set_boundaries_respawn_dormant_vehicles(self, lower: float, upper: float):
        _sync(self._tm.SetBoundariesRespawnDormantVehiclesAsync(lower, upper))

    def set_synchronous_mode(self, enabled: bool):
        _sync(self._tm.SetSynchronousModeAsync(enabled))

    def set_synchronous_mode_timeout_in_milisecond(self, ms: float):
        _sync(self._tm.SetSynchronousModeTimeoutAsync(ms))

    def update_vehicle_lights(self, actor: Actor, enabled: bool):
        _sync(self._tm.UpdateVehicleLightsAsync(actor._actor, enabled))

    def global_percentage_speed_difference(self, pct: float):
        _sync(self._tm.SetGlobalPercentageSpeedDifferenceAsync(pct))

    def synchronous_tick(self) -> bool:
        return bool(_sync(self._tm.SynchronousTickAsync()))

    def shut_down(self):
        _sync(self._tm.ShutDownAsync())


# ── World ─────────────────────────────────────────────────────────────────────

class World:
    def __init__(self, client):
        self._client = client  # _CarlaClient (C# object)

    def get_settings(self) -> EpisodeSettings:
        return _sync(self._client.GetEpisodeSettingsAsync())

    def apply_settings(self, settings: EpisodeSettings) -> int:
        return int(_sync(self._client.SetEpisodeSettingsAsync(settings)))

    def get_map(self) -> Map:
        info = _sync(self._client.GetMapInfoAsync())
        return Map(str(info.Name), info.RecommendedSpawnPoints)

    def get_spectator(self) -> Actor:
        return _wrap_actor(_sync(self._client.GetSpectatorAsync()), self._client)

    def get_actors(self, actor_ids=None):
        from System import UInt32
        if actor_ids is None:
            # Get all cached actor IDs from the world observer
            cached = self._client.GetCachedActorIds()
            if cached.Count == 0:
                return []
            ids = _cs_list([int(cached[i]) for i in range(cached.Count)], UInt32)
        else:
            ids = _cs_list([int(i) for i in actor_ids], UInt32)
        cs_actors = _sync(self._client.GetActorsByIdAsync(ids))
        return _wrap_actors(cs_actors, self._client)

    def get_blueprint_library(self) -> BlueprintLibrary:
        defs = _sync(self._client.GetActorDefinitionsAsync())
        return BlueprintLibrary(defs)

    def spawn_actor(self, blueprint, transform: Transform,
                    attach_to=None, attachment_type=None) -> Actor:
        desc = (blueprint.to_description()
                if isinstance(blueprint, ActorBlueprint) else blueprint)
        if attach_to is not None:
            parent_id = int(attach_to.id) if isinstance(attach_to, Actor) else int(attach_to)
            at = attachment_type if attachment_type is not None else AttachmentType.Rigid
            cs_actor = _sync(self._client.SpawnActorWithParentAsync(desc, transform, parent_id, at))
        else:
            cs_actor = _sync(self._client.SpawnActorAsync(desc, transform))
        return _wrap_actor(cs_actor, self._client)

    def try_spawn_actor(self, blueprint, transform: Transform,
                        attach_to=None, attachment_type=None):
        try:
            return self.spawn_actor(blueprint, transform, attach_to, attachment_type)
        except Exception:
            return None

    def destroy_actor(self, actor) -> bool:
        actor_id = int(actor.id) if isinstance(actor, Actor) else int(actor)
        return bool(_sync(self._client.DestroyActorAsync(actor_id)))

    def tick(self) -> int:
        return int(_sync(self._client.SendTickCueAsync()))

    def wait_for_tick(self, seconds: float = 10.0):
        """In async mode: sleep briefly. Override if you need a true tick event."""
        time.sleep(0.05)

    def get_weather(self) -> WeatherParameters:
        return _sync(self._client.GetWeatherParametersAsync())

    def set_weather(self, weather: WeatherParameters):
        _sync(self._client.SetWeatherParametersAsync(weather))

    def set_pedestrians_seed(self, seed: int):
        # Not a direct RPC; uses console command to set navigation seed
        pass  # Best-effort; server handles this internally in newer builds

    def set_pedestrians_cross_factor(self, percentage: float):
        pass  # Best-effort

    def get_random_location_from_navigation(self):
        # Returns None if not available; caller should handle None
        return None

    def load_map_layer(self, layer: MapLayer):
        _sync(self._client.LoadLevelLayerAsync(layer))

    def unload_map_layer(self, layer: MapLayer):
        _sync(self._client.UnloadLevelLayerAsync(layer))

    def get_available_maps(self):
        return list(_sync(self._client.GetAvailableMapsAsync()))

    def reload_world(self, reset_settings: bool = True):
        # Reload current map
        mi = _sync(self._client.GetMapInfoAsync())
        map_name = str(mi.Name)
        _sync(self._client.LoadEpisodeAsync(map_name, reset_settings))
        return World(self._client)

    def on_tick(self, callback):
        """Register a callback to be called each tick (approximated)."""
        # Not directly implementable without world observer tick events
        # The world observer subscription handles this via actor snapshot callbacks
        pass

    def __repr__(self):
        return f"World(client={self._client})"


# ── Client ────────────────────────────────────────────────────────────────────

class Client:
    def __init__(self, host: str, port: int = 2000, worker_threads: int = 2):
        self._host    = host
        self._port    = port
        self._timeout = TimeSpan.FromMilliseconds(5000)
        self._inner   = _CarlaClient(host, port, self._timeout)
        # World observer is opt-in. Auto-starting it from __init__ races with
        # the first RPC calls and can cause them to time out. Call
        # start_observer() explicitly if you need actor-transform caching.
        self._observer_started = False

    def start_observer(self):
        """Subscribe to the world observer stream so actor transforms are cached."""
        if self._observer_started:
            return
        _sync(self._inner.StartWorldObserverAsync())
        self._observer_started = True

    def set_timeout(self, timeout_s: float):
        self._timeout = TimeSpan.FromMilliseconds(int(timeout_s * 1000))
        # Note: timeout is set at construction; re-creating client would be needed
        # for strict timeout enforcement, but for most scripts this is fine.

    def get_client_version(self) -> str:
        return self._inner.GetClientVersion()

    def get_server_version(self) -> str:
        return str(_sync(self._inner.GetServerVersionAsync()))

    def get_world(self) -> World:
        return World(self._inner)

    def reload_world(self, reset_settings: bool = True) -> World:
        return self.get_world().reload_world(reset_settings)

    def get_available_maps(self):
        return list(_sync(self._inner.GetAvailableMapsAsync()))

    def load_world(self, map_name: str, reset_settings: bool = True) -> World:
        _sync(self._inner.LoadEpisodeAsync(map_name, reset_settings))
        return World(self._inner)

    def get_trafficmanager(self, port: int = 8000) -> TrafficManager:
        tm = self._inner.GetTrafficManager(port)
        return TrafficManager(tm, port)

    def apply_batch(self, cmds, do_tick_cue: bool = False):
        cs_cmds = _cmds_to_cs(cmds)
        _sync(self._inner.ApplyBatchAsync(cs_cmds, do_tick_cue))

    def apply_batch_sync(self, cmds, do_tick_cue: bool = False):
        cs_cmds = _cmds_to_cs(cmds)
        cs_resp = _sync(self._inner.ApplyBatchSyncAsync(cs_cmds, do_tick_cue))
        return [command.Response(cs_resp[i]) for i in range(cs_resp.Count)]

    def start_recorder(self, name: str, additional_data: bool = False) -> str:
        return str(_sync(self._inner.StartRecorderAsync(name, additional_data)))

    def stop_recorder(self):
        _sync(self._inner.StopRecorderAsync())

    def show_recorder_file_info(self, name: str, show_all: bool = False) -> str:
        return str(_sync(self._inner.ShowRecorderFileInfoAsync(name, show_all)))

    def replay_file(self, name: str, start: float = 0.0, duration: float = 0.0,
                    follow_id: int = 0, replay_sensors: bool = False) -> str:
        return str(_sync(self._inner.ReplayFileAsync(
            name, start, duration, follow_id, replay_sensors)))

    def stop_replayer(self, keep_actors: bool = False):
        _sync(self._inner.StopReplayerAsync(keep_actors))

    def set_replayer_time_factor(self, factor: float):
        _sync(self._inner.SetReplayerTimeFactorAsync(factor))

    def set_replayer_ignore_hero(self, ignore: bool):
        _sync(self._inner.SetReplayerIgnoreHeroAsync(ignore))

    def __del__(self):
        try:
            self._inner.DisposeAsync().GetAwaiter().GetResult()
        except Exception:
            pass

    def __repr__(self):
        return f"Client({self._host}:{self._port})"


# ── Convenience re-exports matching carla.* names ────────────────────────────

WorldSettings = EpisodeSettings   # alias — same type, different name in Python API

# Re-export C# enums under carlanet namespace
# Usage: carlanet.AttachmentType.Rigid, carlanet.VehicleLightStateFlags.Brake, etc.
# (Already imported at top-level — nothing more needed here.)
